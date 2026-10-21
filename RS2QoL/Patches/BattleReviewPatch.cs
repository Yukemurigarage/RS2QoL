using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 全滅 (または皇帝の戦闘不能) の後、継承の画面に行く前に「戦闘の振り返り」の窓を 1 枚出す ([BattleReview] Enabled、既定 On)。
    /// 3 ページ (LB / RB で送る): 敵の弱点と耐性 / 敵が使った技と誰がいくら食らったか・見切りの難度 / こちらの行動 (ターンごと)。
    /// 本体の流れ: eval_battle が party_result (1 = 皇帝の戦闘不能、2 = 全滅) を立てる → p_judge (revert_member で HP を最大に戻す)
    /// → fade_out → party_result 2 は fade_out02 (「全滅」の窓を出してキー待ち、状態 20 で暗転)、1 / 3 / 5 は fade_out01(1)
    /// (画面を暗くしてキー待ち、状態 30 で暗転) → Core の戦闘後の script (ivent 8,5) が継承の画面を開く。
    /// MOD は Battle.sync の Prefix で、暗転が終わって後始末の状態 300 に入る直前に窓に出すものを写し (Capture。Work.monsterdata が
    /// まだ生きている)、継承の画面の入口 (Menu._emper_chg1 / _emper_chg2 の proc[0] == 0。script の暗転で画面は真っ黒) で窓を開く。
    /// 開いている間は Menu 側を止める (戻り値 1 = まだ続く)。閉じると本体が継承の画面を作ってフェードインする。
    /// (最初は fade_out02 / fade_out01 の暗転の直前に出していたが、窓の前後で画面の切り替えが 2 回挟まるので移した。ユーザー 2026-10-20)
    /// 記録: Battle.exec の 1 行動 (状態 10 → 20 = Calc が終わって mbox が埋まった時) に mbox を写す。
    ///   mbox[0] = 行動した人 (0x80 = 敵、下位 = 枠)、[10] = 対象の数、11 から 8 つずつ: +0 対象、+1 旗 (1 = 外れ、256 = 見切り / 回避)、
    ///   +5 結果 (1 = HP に当たった、2 = LP、8 = 戦闘不能、16 = 回復、32 = 外れ)、+6 数字 (ダメージ / 回復量)。
    ///   技は A0[4] (10 未満は武器の通常攻撃で、名前は A0[3] の武器)、ターンは Battle.turn。
    /// 敵の弱点: monster_data の 8 種の防御力 (枠の [85..92] = 斬 打 突 射 熱 冷 雷 状)。ダメージ式は「武器の威力 − 防御力」の
    /// 引き算 (damage00 等) で、128 以上 (bit 0x80) はその属性を無効にする。一番低い値を弱点として赤で出す。
    /// 見切り: waza_data[技*10+8] の bit 0x4000 が立つ技だけ見切れる。難度は Data.mikiri_level[技]。partydata[289..] の旗が
    /// 立っていればもう誰かが覚えている (check_mikiriflag で判定を飛ばす)。
    /// </summary>
    internal static class BattleReview
    {
        public static ConfigEntry<bool> Enabled;

        private const int BTN_OK = 0x100000, BTN_CANCEL = 0x400000;
        private const int PAGES = 4;
        private const int PAGE_ENEMY = 0, PAGE_ENEMY_ACTS = 1, PAGE_ENEMY_GRID = 2, PAGE_OWN = 3;   // 順番はユーザー 2026-10-21 (技と見切りを 2 ページ目に)
        private const int DIR_LEFT = 0x10000, DIR_UP = 0x20000, DIR_RIGHT = 0x40000, DIR_DOWN = 0x80000;   // 設定画面と同じ機能ビット
        private const float REPEAT_FIRST = 0.4f, REPEAT_NEXT = 0.1f;
        private const int GRID_COLS = 5;                   // ターン表に一度に出す列の数 (それより多い敵は左右でスクロール)
        private static readonly int[] s_rowOff = new int[PAGES], s_colOff = new int[PAGES];   // ターン表のスクロール (ページごと)。-1 = まだ触っていない (最後のターンを出す)
        private static int s_heldDir; private static float s_nextRepeat;
        private const int OWN_TURNS = 8;                  // こちらの行動のページに出す最後のターン数
        private static readonly string[] TypeNames = { "斬", "打", "突", "射", "熱", "冷", "雷", "状" };
        private static readonly string[] PageTitles = { "敵の弱点と防御力", "敵の技と見切り", "敵の行動", "こちらの行動" };   // 「耐」は本体のフォントに無い (実機 2026-10-20) ので「防御力」

        // HpLoss / LpLoss は行動の前後の写し (Snapshot) の差。ソウルスティールのように LP だけ 0 にする技は結果ビットも数字も付かない
        // (set_status3 は XA1[18] = 0 にするだけ。実機 2026-10-20) ので、差分で拾う
        private sealed class Hit { public int Target, Flags, Result, Amount, HpLoss, LpLoss, LpAfter; }
        private sealed class Act { public int Turn, Actor, Tech, Weapon; public Hit[] Hits; }

        private const int FLAG_MISS = 1, FLAG_MIKIRI = 256, FLAG_BLOCK = 1024, FLAG_BLOCK_HALF = 2048;   // mbox +1 の旗。1024 = 盾 / 受け流しで防いだ (set_shield、N_parry)、2048 = 盾で軽減
        private const int RES_HP = 1, RES_LP = 2, RES_KO = 8, RES_HEAL = 16, RES_MISS = 32;             // mbox +5 の結果

        private static readonly int[] s_hpBefore = new int[15], s_lpBefore = new int[15];   // 0..4 = 味方の枠、5..14 = 敵の枠
        private static readonly List<int> s_learned = new List<int>();                       // この戦闘で覚えた見切り (技の番号。set_mikiri)

        private static int SlotIndex(int code) { int s = code & 0x7F; return (code & 0x80) == 0 ? (s < 5 ? s : -1) : (s < 10 ? 5 + s : -1); }

        /// <summary>行動の始まり (exec の状態 0) に全員の HP / LP を写す。</summary>
        internal static void Snapshot(Battle b)
        {
            if (s_failed || Enabled == null || !Enabled.Value) return;
            for (int i = 0; i < 5; i++) { int[] r = Work.memberdata_b[i]; s_hpBefore[i] = r != null ? r[14] : 0; s_lpBefore[i] = r != null ? r[18] : 0; }
            for (int i = 0; i < 10; i++) { int[] r = Work.monsterdata[i]; s_hpBefore[5 + i] = r != null ? r[14] : 0; s_lpBefore[5 + i] = r != null ? r[18] : 0; }
        }

        /// <summary>見切りを覚えた (Battle.set_mikiri は覚える時だけ呼ばれる)。A0[4] = 敵の技。</summary>
        internal static void Learned(Battle b)
        {
            try { int t = b.A0[4]; if (!s_learned.Contains(t)) s_learned.Add(t); Plugin.Log.LogInfo("[BattleReview] mikiri learned: " + TechName(t, 254, false)); }
            catch (Exception e) { Fail("learned", e); }
        }
        private sealed class Enemy
        {
            public int Id, Count, Hp, HpMax, Cond;
            public string Name;
            public int[] Def = new int[8];
        }

        private static readonly List<Act> s_acts = new List<Act>();
        private static bool s_open, s_shown, s_failed, s_guard;
        private static int s_page;
        private static int s_prevKey = -1, s_lastEscFrame = -1;
        private static bool s_prevLB, s_prevRB;
        private static string[] s_memberNames = new string[5];
        private static List<Enemy> s_enemies = new List<Enemy>();
        private static int s_turns;
        private static string s_reason = "";
        private static float s_openTime;                   // 開いた時刻 (Time.realtimeSinceStartup)。黒い背景のフェードに使う
        private const float BLACK_FADE_SECONDS = 0.5f;     // 開いてから背景が真っ黒になるまで (ユーザー 2026-10-20: 全滅の後なので真っ暗がよい。閉じたら元の明るさ)

        public static bool IsOpen { get { return s_open; } }

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("BattleReview", "Enabled", true,
                "全滅した時 (皇帝が倒れた時も)、継承の画面に行く前に「戦闘の振り返り」の窓を出す。敵の弱点と耐性 (8 種の防御力)、敵と味方のターンごとの行動の表、敵の技と見切りの難度の 4 ページ (LB / RB で送る、決定かキャンセルで閉じる)");
            Plugin.Log.LogInfo("[BattleReview] patched: Battle.sync (capture at 300) / exec / set_mikiri / Menu._emper_chg1 / _emper_chg2 (enabled=" + Enabled.Value + ")");
        }

        // ---------------------------------------------------------------- 記録

        internal static void Reset()
        {
            s_acts.Clear();
            s_shown = false;
            s_open = false;
            s_guard = false;
            s_namesBuilt = false;
            s_learned.Clear();
            s_captured = false;
        }

        private static bool s_namesBuilt;

        /// <summary>味方の名前の表 (戦闘ごとに 1 回)。戦闘の枠 (Work.memberdata_b の添字) → メニューの並び (Work.memberdata) は partydata[28 + i] (revert_memberwork と同じ)。
        /// 記録の行をログに書く時にも要るので、最初の記録か窓を開く時のどちらか早い方で作る (作る前は名前が空で出ていた。実機 2026-10-20)。</summary>
        private static void EnsureMemberNames(Battle b)
        {
            if (s_namesBuilt) return;
            s_namesBuilt = true;
            s_memberNames = new string[5];
            for (int i = 0; i < 5; i++) s_memberNames[i] = "?";
            int members = Mathf.Clamp(b.member, 0, 5);
            for (int i = 0; i < members; i++)
            {
                int slot = Work.partydata[28 + i];
                if (slot >= 0 && slot < 5) s_memberNames[slot] = PartyWeight.MemberName(i);
            }
        }

        /// <summary>Battle.exec の状態 10 → 20 (Calc が終わった直後) に、mbox を 1 行動分として写す。</summary>
        internal static void Record(Battle b)
        {
            if (s_failed || Enabled == null || !Enabled.Value) return;
            try
            {
                int[] m = b.mbox;
                int n = m[10];
                if (n < 0 || n > 15) n = 0;
                Act a = new Act { Turn = b.turn, Actor = m[0], Tech = b.A0[4], Weapon = b.A0[3], Hits = new Hit[n] };
                for (int k = 0; k < n; k++)
                {
                    int i = 11 + 8 * k;
                    Hit h = new Hit { Target = m[i], Flags = m[i + 1], Result = m[i + 5], Amount = m[i + 6] };
                    int si = SlotIndex(h.Target);
                    if (si >= 0)
                    {
                        int[] r = si < 5 ? Work.memberdata_b[si] : Work.monsterdata[si - 5];
                        if (r != null) { h.HpLoss = s_hpBefore[si] - r[14]; h.LpLoss = s_lpBefore[si] - r[18]; h.LpAfter = r[18]; }
                    }
                    a.Hits[k] = h;
                }
                s_acts.Add(a);
                if (Plugin.DiagEnabled != null && Plugin.DiagEnabled.Value)
                {
                    EnsureMemberNames(b);
                    StringBuilder sb = new StringBuilder();
                    sb.Append("[BattleReview] turn ").Append(a.Turn).Append(' ').Append(ActorName(a.Actor)).Append(' ').Append(TechName(a.Tech, a.Weapon, true));
                    for (int k = 0; k < n; k++)
                        sb.Append(" | ").Append(ActorName(a.Hits[k].Target)).Append(' ').Append(a.Hits[k].Amount).Append(" r=").Append(a.Hits[k].Result).Append(" f=").Append(a.Hits[k].Flags)
                          .Append(" hp-").Append(a.Hits[k].HpLoss).Append(" lp-").Append(a.Hits[k].LpLoss);
                    Plugin.Log.LogInfo(sb.ToString());
                }
            }
            catch (Exception e) { Fail("record", e); }
        }

        /// <summary>
        /// Battle.sync の Prefix から。暗転が終わって状態 300 (戦闘の後始末 → Core に戻って継承の script) に入る直前、
        /// 負け (party_result 1 = 皇帝の戦闘不能、2 = 全滅。イベント戦闘で負けても話が続くものは除く) なら最初の 1 回に窓を開く。
        /// 画面はもう真っ暗なので、窓を閉じるとそのまま継承の画面が出る (切り替えが 2 回挟まらない。ユーザー 2026-10-20)。
        /// true = 本体を止める (窓が開いている)。
        /// </summary>
        internal static void CaptureAtBattleEnd(Battle b)
        {
            if (s_failed || Enabled == null || !Enabled.Value || s_captured) return;
            if (b.procstg != 0 || b.proc[0][0] != 300 || Sys.fadeflg) return;
            if (b.party_result != 1 && b.party_result != 2) return;
            if (b.event_kind != 0 && (b.event_flag & 1) != 0) return;
            try { Capture(b, b.party_result == 2 ? "全滅" : "皇帝が倒れた"); }
            catch (Exception e) { Fail("capture", e); }
        }

        /// <summary>
        /// Menu._emper_chg1 / _emper_chg2 (継承の画面の入口、proc[0] == 0 = まだ何も描いていない。画面は script の暗転で真っ黒) の Prefix から。
        /// 負けの戦闘の記録を持っていれば最初の 1 回に窓を開き、開いている間は本体を止める (戻り値 1 = まだ続く)。
        /// 閉じると本体が継承の画面を作ってフェードインする。true = 本体を止める。
        /// </summary>
        internal static bool GateMenu(Menu m)
        {
            if (s_failed || Enabled == null || !Enabled.Value) return false;
            if (s_open) return true;
            if (!s_captured || s_shown) return false;
            if (m.proc[0] != 0) return false;
            s_shown = true;
            try { Open(); }
            catch (Exception e) { Fail("open", e); return false; }
            return s_open;
        }

        private static void Fail(string where, Exception e)
        {
            s_failed = true;
            s_open = false;
            Plugin.Log.LogError("[BattleReview] " + where + " failed, feature disabled until restart: " + e);
        }

        // ---------------------------------------------------------------- 開く / 閉じる

        private static bool s_captured;                              // 負けの戦闘の記録を持っている (継承の画面で出す)
        private static readonly Enemy[] s_slotEnemy = new Enemy[10];   // 敵の枠 → 種類 (窓を開く時は Work.monsterdata がもう無いので写しておく)

        /// <summary>戦闘の終わり (sync の状態 300 の直前。Work.monsterdata / memberdata_b がまだ生きている) に、窓に出すものを全部写す。</summary>
        private static void Capture(Battle b, string reason)
        {
            s_reason = reason;
            s_turns = b.turn;
            EnsureMemberNames(b);
            // 敵: 同じ種類はまとめる (HP は最初の 1 体)
            s_enemies = new List<Enemy>();
            for (int s = 0; s < 10; s++) s_slotEnemy[s] = null;
            int monsters = Mathf.Clamp(b.monster, 0, 10);
            for (int s = 0; s < monsters; s++)
            {
                int[] row = Work.monsterdata[s];
                if (row == null || (row[0] & 0x4000) == 0) continue;
                int id = row[0] & 0x1FFF;
                Enemy e = null;
                foreach (Enemy x in s_enemies) if (x.Id == id) { e = x; break; }
                if (e != null) { e.Count++; s_slotEnemy[s] = e; continue; }
                e = new Enemy { Id = id, Count = 1, Hp = row[14], HpMax = row[16], Cond = row[93], Name = MonsterName(id) };
                for (int k = 0; k < 8; k++) e.Def[k] = row[85 + k];
                s_enemies.Add(e);
                s_slotEnemy[s] = e;
            }
            s_captured = true;
            StringBuilder sb = new StringBuilder();
            sb.Append("[BattleReview] captured (").Append(reason).Append(") turns=").Append(s_turns).Append(" acts=").Append(s_acts.Count).Append(" enemies:");
            foreach (Enemy e in s_enemies)
            {
                sb.Append(' ').Append(e.Name).Append("x").Append(e.Count).Append(" hp=").Append(e.Hp).Append('/').Append(e.HpMax).Append(" def=");
                for (int k = 0; k < 8; k++) sb.Append(k > 0 ? "," : "").Append(e.Def[k]);
                sb.Append(" cond=").Append(e.Cond);
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static void Open()
        {
            s_page = PAGE_ENEMY;
            for (int i = 0; i < PAGES; i++) { s_rowOff[i] = -1; s_colOff[i] = 0; }
            s_heldDir = 0;
            s_prevKey = -1;
            s_prevLB = PageBackDown(); s_prevRB = PageNextDown();
            s_open = true;
            s_openTime = Time.realtimeSinceStartup;
            UiParts.SeOk();
            Plugin.Log.LogInfo("[BattleReview] applied: open (" + s_reason + ") at the succession screen");
        }

        private static void Close()
        {
            if (!s_open) return;
            s_open = false;
            s_guard = true;
            s_captured = false;   // 1 回出したら捨てる (次に継承の画面が出る時に再び開かない)
            UiParts.SeClose();
            Plugin.Log.LogInfo("[BattleReview] close");
        }

        private static bool PageBackDown() { return PadMap.IsPhysicalDown(PadMap.PHYS_LB) || KeyMap.IsDown(PadFunction.L); }
        private static bool PageNextDown() { return PadMap.IsPhysicalDown(PadMap.PHYS_RB) || KeyMap.IsDown(PadFunction.R); }

        private static void TurnPage(int d)
        {
            s_page = (s_page + d + PAGES) % PAGES;
            UiParts.SeCursor();
        }

        /// <summary>Util.GetKeypadState の最終段から。開いている間は入力を奪い、決定 / キャンセル / Esc で閉じる。</summary>
        public static int Tick(int key)
        {
            if (!s_open)
            {
                if (!s_guard) return key;
                if ((key & (BTN_OK | BTN_CANCEL)) == 0) s_guard = false;
                return 0;
            }
            int pressed = s_prevKey < 0 ? 0 : key & ~s_prevKey;
            s_prevKey = key;
            bool escEdge = Time.frameCount != s_lastEscFrame && (Input.GetKeyDown(KeyCode.Escape) || Interp60.TakeKeyDown(KeyCode.Escape));
            s_lastEscFrame = Time.frameCount;
            if ((pressed & (BTN_OK | BTN_CANCEL)) != 0 || escEdge) { Close(); return 0; }
            bool lb = PageBackDown(), rb = PageNextDown();
            if (lb && !s_prevLB) TurnPage(-1);
            else if (rb && !s_prevRB) TurnPage(+1);
            s_prevLB = lb; s_prevRB = rb;
            // 十字キー / スティック: ターン表のスクロール (押した瞬間 + 長押しリピート。設定画面と同じ)
            int dir = key & (DIR_LEFT | DIR_UP | DIR_RIGHT | DIR_DOWN);
            float now = Time.realtimeSinceStartup;
            if (dir == 0) s_heldDir = 0;
            else if (dir != s_heldDir) { s_heldDir = dir; s_nextRepeat = now + REPEAT_FIRST; Scroll(dir); }
            else if (now >= s_nextRepeat) { s_nextRepeat = now + REPEAT_NEXT; Scroll(dir); }
            return 0;
        }

        // ターン表の大きさ (描いた時に覚える。スクロールの上限に使う)
        private static int s_gridRows, s_gridVisibleRows, s_gridCols;

        private static void Scroll(int dir)
        {
            if (s_page != PAGE_ENEMY_GRID && s_page != PAGE_OWN) return;
            int p = s_page;
            int maxRow = Mathf.Max(0, s_gridRows - s_gridVisibleRows);
            if (s_rowOff[p] < 0) s_rowOff[p] = maxRow;
            int r = s_rowOff[p], c = s_colOff[p];
            if ((dir & DIR_UP) != 0) r--;
            if ((dir & DIR_DOWN) != 0) r++;
            if ((dir & DIR_LEFT) != 0) c--;
            if ((dir & DIR_RIGHT) != 0) c++;
            r = Mathf.Clamp(r, 0, maxRow);
            c = Mathf.Clamp(c, 0, Mathf.Max(0, s_gridCols - GRID_COLS));
            if (r != s_rowOff[p] || c != s_colOff[p]) UiParts.SeCursor();
            s_rowOff[p] = r; s_colOff[p] = c;
        }

        // ---------------------------------------------------------------- 名前

        private static readonly System.Reflection.MethodInfo s_getMenuMsg =
            AccessTools.Method(AccessTools.TypeByName("MsgDataManager"), "GetMenuMsg", new Type[] { typeof(int), typeof(int) });

        private static string Msg(int sheet, int no)
        {
            try { return s_getMenuMsg != null ? ((string)s_getMenuMsg.Invoke(null, new object[] { sheet, no }) ?? "?") : "?"; }
            catch { return "?"; }
        }

        private static string MonsterName(int id) { return Msg(12, id + 1); }

        /// <summary>技の名前 (本体の getNameWaza と同じ): 10 未満は武器の通常攻撃 (武器の名前)、10 は素手、それ以外は技 / 術の名前。</summary>
        private static string TechName(int tech, int weapon, bool plain)
        {
            if (tech < 10)
            {
                if (weapon < 0 || weapon >= 254) return "攻撃";
                return plain ? Msg(6, weapon + 1) : Msg(6, weapon + 1);
            }
            if (tech == 10) return Msg(13, 11);
            return Msg(13, tech + 1);
        }

        private static string ActorName(int code)
        {
            int slot = code & 0x7F;
            if ((code & 0x80) == 0) return slot < 5 ? s_memberNames[slot] : "味方" + slot;
            if (slot < 10 && s_captured && s_slotEnemy[slot] != null) return s_slotEnemy[slot].Name;   // 戦闘の後は写しから
            if (slot < 10 && Work.monsterdata[slot] != null) return MonsterName(Work.monsterdata[slot][0] & 0x1FFF);
            return "敵" + slot;
        }

        private static bool CanMikiri(int tech)
        {
            byte[] w = Data.waza_data;
            if (tech < 0 || tech >= 200 || w == null || tech * 10 + 9 >= w.Length) return false;
            return (Sys.getWord(w, tech * 10 + 8) & 0x4000) != 0;
        }

        private static int MikiriLevel(int tech)
        {
            byte[] t = Data.mikiri_level;
            return (t != null && tech >= 0 && tech < t.Length) ? t[tech] & 0xFF : -1;
        }

        private static bool MikiriKnown(int tech)
        {
            int[] p = Work.partydata;
            int i = 289 + tech / 8;
            return p != null && i < p.Length && (p[i] & (128 >> (tech & 7))) != 0;
        }

        private static string MikiriText(int tech)
        {
            if (!CanMikiri(tech)) return "－";
            if (MikiriKnown(tech)) return "済";
            return "難度 " + MikiriLevel(tech);
        }

        // ---------------------------------------------------------------- 描画 (Plugin.OnGUI)

        private static Font s_font;
        private static GUIStyle s_line, s_dim, s_title, s_box, s_hint, s_head, s_center, s_right, s_rightDim, s_red, s_redRight, s_cell, s_cellDim, s_cellHead;
        private static int s_fontSizeBuilt = -1;

        public static void Draw()
        {
            if (s_failed || ConfigMenu.IsOpen) return;
            if (!s_open) return;
            try { DrawWindow(); }
            catch (Exception e) { Fail("draw", e); }
        }

        // 本体のフォント (UDTypos512) には無い字がある (「耐」が抜けて「弱点と性」になった。実機 2026-10-20)。
        // 文字列ごとに全部の字があるかを 1 回調べ、無ければその文字列だけ OS のフォント (UiParts.Label) で描く
        private static readonly Dictionary<string, bool> s_glyphOk = new Dictionary<string, bool>();

        private static bool GameFontHas(string s)
        {
            bool ok;
            if (s_glyphOk.TryGetValue(s, out ok)) return ok;
            ok = true;
            Font f = UiParts.GameFont();
            if (f != null)
                foreach (char c in s)
                    if (c != ' ' && !f.HasCharacter(c)) { ok = false; break; }
            if (s_glyphOk.Count > 4000) s_glyphOk.Clear();
            s_glyphOk[s] = ok;
            return ok;
        }

        private static void Txt(Rect r, string s, GUIStyle st)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (GameFontHas(s)) UiParts.GameLabel(r, s, st); else UiParts.Label(r, s, st);
        }

        private static float TextW(string s, GUIStyle st)
        {
            return GameFontHas(s) ? UiParts.GameTextWidth(s, st) : st.CalcSize(new GUIContent(s)).x;
        }

        // 幅に収まるように折り返して描く (右端からはみ出していた。実機 2026-10-20)。空白があればそこで、無ければ字の途中で切る。
        // 折り返した結果は (文字列, 幅) ごとに覚えておく (毎フレーム測り直さない)
        private static readonly Dictionary<string, string[]> s_wrapCache = new Dictionary<string, string[]>();

        private static string[] Wrap(string text, float w, GUIStyle st)
        {
            string key = text + "\u0001" + (int)w + "\u0001" + st.fontSize;
            string[] lines;
            if (s_wrapCache.TryGetValue(key, out lines)) return lines;
            List<string> outl = new List<string>();
            int start = 0;
            while (start < text.Length)
            {
                // 切る所: 2 つ並んだ空白 (項目の区切り) があればそこ、無ければ 1 つの空白、それも無ければ字の途中
                // (「状 = 状態異常」の中の空白で切れていた。実機 2026-10-21)
                int n = start, lastSpace = -1, lastDouble = -1;
                while (n < text.Length)
                {
                    if (text[n] == ' ') { lastSpace = n; if (n + 1 < text.Length && text[n + 1] == ' ') lastDouble = n; }
                    if (TextW(text.Substring(start, n - start + 1), st) > w) break;
                    n++;
                }
                int end;
                if (n >= text.Length) end = text.Length;
                else if (lastDouble > start) end = lastDouble;
                else if (lastSpace > start) end = lastSpace;
                else end = Math.Max(start + 1, n);
                outl.Add(text.Substring(start, end - start));
                start = end;
                while (start < text.Length && text[start] == ' ') start++;
            }
            lines = outl.ToArray();
            if (s_wrapCache.Count > 500) s_wrapCache.Clear();
            s_wrapCache[key] = lines;
            return lines;
        }

        /// <summary>折り返して描き、描き終えた y を返す。bottom を超える行は描かない。</summary>
        private static float DrawWrapped(float x, float y, float w, float lineH, string text, GUIStyle st, float bottom)
        {
            foreach (string line in Wrap(text, w, st))
            {
                if (y + lineH > bottom) break;
                Txt(new Rect(x, y, w, lineH), line, st);
                y += lineH;
            }
            return y;
        }

        private static void DrawWindow()
        {
            float outer = Mathf.Round(UiParts.Unit * 8f);
            float lineH = Mathf.Clamp(Screen.height / 24f, 16f, 48f);
            int fontSize = Mathf.Clamp(Mathf.FloorToInt(lineH / 1.45f), 11, 32);
            lineH = Mathf.Min(lineH, fontSize * 1.6f);
            EnsureStyles(fontSize);
            float pad = lineH * 0.6f;
            float headH = Mathf.Round(lineH * 1.4f);
            float footH = lineH;
            float w = Mathf.Min(Screen.width - 2 * outer, Mathf.Round(fontSize * 46f));
            float h = Mathf.Min(Screen.height - 2 * outer, Mathf.Round(pad * 2 + headH + lineH * 0.3f + lineH * 18.5f + lineH * 0.3f + footH));
            float x = Mathf.Round((Screen.width - w) / 2f);
            float y = Mathf.Round((Screen.height - h) / 2f);
            Rect win = new Rect(x, y, w, h);
            // 背景を真っ黒に (開いてから BLACK_FADE_SECONDS で暗くなる。閉じると描かなくなるので元の明るさに戻り、本体が自分の暗転に進む)
            {
                float a = Mathf.Clamp01((Time.realtimeSinceStartup - s_openTime) / BLACK_FADE_SECONDS);
                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, a);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = old;
            }
            UiParts.Shadow = !UiParts.DrawPaperWindow(win);
            if (UiParts.Shadow) GUI.Box(win, GUIContent.none, s_box);
            float cx = x + pad, cy = y + pad, cw = w - 2 * pad;

            // 見出し: 左にタイトル、右に [L] n / 3 [R]
            Txt(new Rect(cx, cy, cw, headH), "戦闘の振り返り (" + s_reason + ")  " + PageTitles[s_page], s_title);
            {
                float arrowH = Mathf.Round(lineH * 1.1f);
                float arrowW = Mathf.Round(arrowH * UiParts.ArrowAspect);
                string pageText = (s_page + 1) + " / " + PAGES;
                float pageW = UiParts.GameTextWidth(pageText, s_center) + fontSize * 0.8f;
                float ay = cy + Mathf.Round((headH - arrowH) / 2f);
                Rect rR = new Rect(cx + cw - arrowW, ay, arrowW, arrowH);
                Rect tR = new Rect(rR.x - pageW, cy, pageW, headH);
                Rect lR = new Rect(tR.x - arrowW, ay, arrowW, arrowH);
                if (!UiParts.DrawArrow(lR, true)) Txt(lR, "◀", s_center);
                if (!UiParts.DrawArrow(rR, false)) Txt(rR, "▶", s_center);
                Txt(tR, pageText, s_center);
                if (UiParts.ClickArea(lR)) TurnPage(-1);
                if (UiParts.ClickArea(rR)) TurnPage(+1);
            }
            cy += headH + lineH * 0.3f;
            float bodyBottom = y + h - pad - footH - lineH * 0.3f;

            switch (s_page)
            {
                case PAGE_ENEMY: DrawEnemyPage(cx, cy, cw, lineH, fontSize, bodyBottom); break;
                case PAGE_ENEMY_ACTS: DrawEnemyActsPage(cx, cy, cw, lineH, fontSize, bodyBottom); break;
                case PAGE_ENEMY_GRID: DrawGrid(true, cx, cy, cw, lineH, fontSize, bodyBottom); break;
                default: DrawGrid(false, cx, cy, cw, lineH, fontSize, bodyBottom); break;
            }

            string foot = (s_page == PAGE_ENEMY_GRID || s_page == PAGE_OWN)
                ? "十字キー: スクロール   LB / RB: ページ   決定・キャンセル・Esc: 閉じて継承へ"
                : "LB / RB: ページ   決定・キャンセル・Esc: 閉じて継承へ";
            Txt(new Rect(cx, y + h - pad - footH, cw, footH), foot, s_hint);
        }

        /// <summary>敵のページ: 名前 (×N)、残り HP、8 種の防御力 (一番低い = 弱点を赤、128 以上 = 無効)。</summary>
        private static void DrawEnemyPage(float cx, float cy, float cw, float lineH, int fontSize, float bottom)
        {
            float cellW = Mathf.Round(fontSize * 2.6f);
            float hpW = Mathf.Round(fontSize * 6.5f);
            float defX = cx + cw - cellW * 8;
            float hpX = defX - hpW - Mathf.Round(fontSize * 1.2f);   // 残り HP と 斬 の間に余白 (ユーザー 2026-10-21)
            float nameW = hpX - cx - fontSize * 0.5f;
            // 列の見出し
            Txt(new Rect(cx, cy, nameW, lineH), "敵", s_dim);
            Txt(new Rect(hpX, cy, hpW, lineH), "残り HP", s_rightDim);
            for (int k = 0; k < 8; k++) Txt(new Rect(defX + cellW * k, cy, cellW, lineH), TypeNames[k], s_cellHead);
            cy += lineH;
            if (s_enemies.Count == 0)
            {
                Txt(new Rect(cx, cy, cw, lineH), "敵の記録がありません", s_dim);
                cy += lineH;
            }
            foreach (Enemy e in s_enemies)
            {
                if (cy + lineH > bottom) break;
                string name = e.Count > 1 ? e.Name + " ×" + e.Count : e.Name;
                Txt(new Rect(cx, cy, nameW, lineH), name, s_line);
                Txt(new Rect(hpX, cy, hpW, lineH), e.Hp + " / " + e.HpMax, e.Hp * 4 <= e.HpMax ? s_redRight : s_right);
                int min = 255;
                for (int k = 0; k < 8; k++) if (e.Def[k] < 128 && e.Def[k] < min) min = e.Def[k];
                for (int k = 0; k < 8; k++)
                {
                    int v = e.Def[k];
                    Rect r = new Rect(defX + cellW * k, cy, cellW, lineH);
                    if (v >= 128) Txt(r, "無効", s_cellDim);
                    else Txt(r, v.ToString(), v == min ? s_red : s_cell);
                }
                cy += lineH;
            }
            cy += lineH * 0.3f;
            // 説明: 表の下に 1 行空け、文ごとに 1 行 (長ければ折り返す)、1 行空けて属性の説明 (ユーザー 2026-10-21: 改行の位置と余白)
            float hintH = Mathf.Round(lineH * 0.9f);
            cy += hintH;
            cy = DrawWrapped(cx, cy, cw, hintH, "数字が小さいほどその属性のダメージが通る (赤 = その敵の弱点)。", s_hint, bottom);
            cy = DrawWrapped(cx, cy, cw, hintH, "「無効」はその属性のダメージを受けない。", s_hint, bottom);
            cy += hintH;
            // 属性の説明は 2 列 × 3 行 (ユーザー 2026-10-21: 1 項目 1 行で見やすく)
            string[] attrNotes = { "斬 = 剣・大剣・斧など", "射 = 弓", "打 = 棍棒・体術", "熱 / 冷 / 雷 = 術と属性武器", "突 = 槍・小剣", "状 = 状態異常" };
            // 右の列は左の列の一番長い項目のすぐ右 (ユーザー 2026-10-21: 左に寄せてひとかたまりに見せる)
            float colW2 = 0f;
            for (int i = 0; i < attrNotes.Length; i += 2) colW2 = Mathf.Max(colW2, TextW(attrNotes[i], s_hint));
            colW2 = Mathf.Round(colW2 + fontSize * 2f);
            for (int i = 0; i < attrNotes.Length; i += 2)
            {
                if (cy + hintH > bottom) break;
                Txt(new Rect(cx, cy, colW2, hintH), attrNotes[i], s_hint);
                if (i + 1 < attrNotes.Length) Txt(new Rect(cx + colW2, cy, colW2, hintH), attrNotes[i + 1], s_hint);
                cy += hintH;
            }
        }

        private sealed class TechUse
        {
            public int Tech, Weapon, Count, Total, Max, LpTotal;
            public int[] PerMember = new int[5], PerMemberLp = new int[5];
            public string[] PerMemberNote = new string[5];   // ダメージも LP も無い時の理由 (盾 / 見切り / 外れ)
        }

        /// <summary>当たらなかった理由 (mbox の旗と結果から)。null = 当たっている (理由なし)。</summary>
        private static string Outcome(Hit h)
        {
            if ((h.Flags & FLAG_MIKIRI) != 0) return "見切り";
            if ((h.Flags & FLAG_BLOCK) != 0) return "盾";
            if ((h.Flags & FLAG_BLOCK_HALF) != 0 && h.Amount == 0 && h.HpLoss <= 0) return "盾";
            if ((h.Flags & FLAG_MISS) != 0 || (h.Result & RES_MISS) != 0) return "外れ";
            return null;
        }

        /// <summary>敵が使った技のページ: 敵ごとに、技 / 回数 / 合計ダメージ / 最大 / 誰にいくら / 見切り。</summary>
        private static void DrawEnemyActsPage(float cx, float cy, float cw, float lineH, int fontSize, float bottom)
        {
            float colCnt = Mathf.Round(fontSize * 3.2f), colSum = Mathf.Round(fontSize * 4.5f), colMax = Mathf.Round(fontSize * 4f), colMik = Mathf.Round(fontSize * 5f);
            float rightPad = Mathf.Round(fontSize * 6f);   // 4 列を全体に左へ (ユーザー 2026-10-20 → 21: もっと左に)
            float mikX = cx + cw - rightPad - colMik, maxX = mikX - colMax, sumX = maxX - colSum, cntX = sumX - colCnt;
            float nameW = cntX - cx - fontSize * 0.5f;
            Txt(new Rect(cx, cy, nameW, lineH), "技 (使った敵)", s_dim);
            Txt(new Rect(cntX, cy, colCnt, lineH), "回数", s_rightDim);
            Txt(new Rect(sumX, cy, colSum, lineH), "合計", s_rightDim);
            Txt(new Rect(maxX, cy, colMax, lineH), "最大", s_rightDim);
            Txt(new Rect(mikX, cy, colMik, lineH), "見切り", s_rightDim);
            cy += lineH;
            bool any = false;
            foreach (Enemy e in s_enemies)
            {
                // この種類の敵 (どの枠でも) が使った技を集める
                List<TechUse> uses = new List<TechUse>();
                foreach (Act a in s_acts)
                {
                    if ((a.Actor & 0x80) == 0) continue;
                    int slot = a.Actor & 0x7F;
                    if (slot >= 10 || Work.monsterdata[slot] == null || (Work.monsterdata[slot][0] & 0x1FFF) != e.Id) continue;
                    TechUse u = null;
                    foreach (TechUse x in uses) if (x.Tech == a.Tech) { u = x; break; }
                    if (u == null) { u = new TechUse { Tech = a.Tech, Weapon = a.Weapon }; uses.Add(u); }
                    u.Count++;
                    foreach (Hit hit in a.Hits)
                    {
                        if ((hit.Target & 0x80) != 0) continue;           // 敵に向けた行動 (回復など) は数えない
                        int t = hit.Target & 0x7F;
                        if (t >= 5) continue;
                        // HP のダメージは本体が出した数字 (結果 1 = HP)。LP は前後の差 (結果ビットが付かない技があるため)
                        int dmg = (hit.Result & RES_HP) != 0 ? hit.Amount : 0;
                        int lp = hit.LpLoss > 0 ? hit.LpLoss : ((hit.Result & RES_LP) != 0 ? hit.Amount : 0);
                        u.Total += dmg;
                        if (dmg > u.Max) u.Max = dmg;
                        u.PerMember[t] += dmg;
                        u.LpTotal += lp;
                        u.PerMemberLp[t] += lp;
                        if (dmg == 0 && lp == 0) { string why = Outcome(hit); if (why != null) u.PerMemberNote[t] = why; }
                    }
                }
                uses.Sort((p, q) => q.Total.CompareTo(p.Total));
                if (cy + lineH > bottom) break;
                Txt(new Rect(cx, cy, cw, lineH), e.Name, s_head);
                cy += lineH;
                if (uses.Count == 0 && cy + lineH <= bottom)
                {
                    Txt(new Rect(cx + fontSize, cy, nameW, lineH), "(行動の記録なし)", s_dim);
                    cy += lineH;
                }
                foreach (TechUse u in uses)
                {
                    if (cy + lineH > bottom) break;
                    any = true;
                    Txt(new Rect(cx + fontSize, cy, nameW - fontSize, lineH), TechName(u.Tech, u.Weapon, false), s_line);
                    Txt(new Rect(cntX, cy, colCnt, lineH), u.Count.ToString(), s_right);
                    // 合計: HP のダメージ。無くて LP だけ減らした技 (ソウルスティールなど) は「LP −N」を赤で
                    if (u.Total > 0) Txt(new Rect(sumX, cy, colSum, lineH), u.Total.ToString(), s_right);
                    else if (u.LpTotal > 0) Txt(new Rect(sumX, cy, colSum, lineH), "LP −" + u.LpTotal, s_redRight);
                    else Txt(new Rect(sumX, cy, colSum, lineH), "－", s_right);
                    Txt(new Rect(maxX, cy, colMax, lineH), u.Max > 0 ? u.Max.ToString() : "－", u.Max >= 200 ? s_redRight : s_right);
                    string mik = s_learned.Contains(u.Tech) ? "覚えた！" : MikiriText(u.Tech);
                    Txt(new Rect(mikX, cy, colMik, lineH), mik, mik.StartsWith("難度") || mik.StartsWith("覚") ? s_redRight : s_rightDim);
                    cy += lineH;
                    // 「誰にいくら」の薄い行は出さない (ユーザー 2026-10-20: 要らない。ターン表の方にある)
                }
                // 行動表にあってまだ使っていない技は出さない (ユーザー 2026-10-20: 見たくない)
                cy += lineH * 0.2f;
            }
            if (!any && s_enemies.Count == 0) Txt(new Rect(cx, cy, cw, lineH), "敵の記録がありません", s_dim);
        }

        // ---------------------------------------------------------------- ターン表 (敵の行動 / こちらの行動)

        private sealed class GridCol { public int Actor; public string Name; }

        /// <summary>ターン表の列: 味方は戦闘の枠 0..4 (名前のある人)、敵はその戦闘で行動した枠 (同じ種類が複数なら名前に番号)。</summary>
        private static List<GridCol> GridColumns(bool enemy)
        {
            List<GridCol> cols = new List<GridCol>();
            if (!enemy)
            {
                for (int i = 0; i < 5; i++) if (s_memberNames[i] != "?") cols.Add(new GridCol { Actor = i, Name = s_memberNames[i] });
                if (cols.Count == 0) for (int i = 0; i < 5; i++) cols.Add(new GridCol { Actor = i, Name = s_memberNames[i] });
                return cols;
            }
            for (int s = 0; s < 10; s++)
            {
                bool acted = false;
                foreach (Act a in s_acts) if ((a.Actor & 0x80) != 0 && (a.Actor & 0x7F) == s) { acted = true; break; }
                if (!acted) continue;
                cols.Add(new GridCol { Actor = 0x80 | s, Name = ActorName(0x80 | s) });
            }
            // 同じ名前が複数なら 1, 2, ... を付ける
            for (int i = 0; i < cols.Count; i++)
            {
                int same = 0, idx = 0;
                for (int j = 0; j < cols.Count; j++) if (cols[j].Name == cols[i].Name) { same++; if (j < i) idx++; }
                if (same > 1) cols[i].Name = cols[i].Name + " " + (idx + 1);
            }
            return cols;
        }

        /// <summary>
        /// ターン表: 行 = ターン (上下でスクロール)、列 = 行動した人 (GRID_COLS より多ければ左右でスクロール)。
        /// マスは「技の名前 / 結果」。結果は与えたダメージ (相手が 1 人なら「名前 数字」、複数なら「合計 (N 人)」。味方の表は数字だけ)、
        /// 回復は +N、LP だけ削ったら LP −N、当たらなければ 盾 / 見切り / 外れ、相手を倒したら †。
        /// 味方の表では、そのターンに戦闘不能になった人のマスを赤の † にする。行動していない人は －。
        /// </summary>
        private static void DrawGrid(bool enemy, float cx, float cy, float cw, float lineH, int fontSize, float bottom)
        {
            int page = enemy ? PAGE_ENEMY_GRID : PAGE_OWN;
            List<GridCol> cols = GridColumns(enemy);
            float turnW = Mathf.Round(fontSize * 3.2f);
            float rowH = Mathf.Round(lineH * 1.85f);
            int visibleRows = Mathf.Max(1, Mathf.FloorToInt((bottom - cy - lineH) / rowH));
            s_gridRows = s_turns; s_gridVisibleRows = visibleRows; s_gridCols = cols.Count;
            int maxRow = Mathf.Max(0, s_turns - visibleRows);
            if (s_rowOff[page] < 0 || s_rowOff[page] > maxRow) s_rowOff[page] = maxRow;   // 最初は最後のターンが見える位置
            int colOff = Mathf.Clamp(s_colOff[page], 0, Mathf.Max(0, cols.Count - GRID_COLS));
            s_colOff[page] = colOff;
            int shown = Mathf.Min(GRID_COLS, cols.Count);
            float colW = shown > 0 ? Mathf.Floor((cw - turnW) / shown) : cw - turnW;

            // 列の見出し。左右にまだ列があれば ◀ ▶ をターンの欄に
            string turnHead = "ターン";
            if (colOff > 0) turnHead = "◀ " + turnHead;
            if (colOff + shown < cols.Count) turnHead = turnHead + " ▶";
            Txt(new Rect(cx, cy, turnW, lineH), turnHead, s_dim);
            for (int c = 0; c < shown; c++) Txt(new Rect(cx + turnW + colW * c, cy, colW, lineH), cols[colOff + c].Name, s_cellHead);
            cy += lineH;
            if (s_turns <= 0 || cols.Count == 0) { Txt(new Rect(cx, cy, cw, lineH), "行動の記録がありません", s_dim); return; }

            int first = s_rowOff[page] + 1, last = Mathf.Min(s_turns, first + visibleRows - 1);
            for (int turn = first; turn <= last; turn++)
            {
                if (cy + rowH > bottom) break;
                // ターンの番号。上下にまだターンがあれば先頭 / 末尾の行に ▲ ▼
                string tn = turn.ToString();
                if (turn == first && first > 1) tn = "▲ " + tn;
                else if (turn == last && last < s_turns) tn = "▼ " + tn;
                Txt(new Rect(cx, cy, turnW, rowH), tn, s_center);
                for (int c = 0; c < shown; c++)
                {
                    GridCol col = cols[colOff + c];
                    string top = "", num = "";
                    bool ko = false, red = false, dim = false, weak = false;
                    foreach (Act a in s_acts)
                    {
                        if (a.Turn != turn) continue;
                        // 味方の表: この人が戦闘不能になったターンか (誰の行動でも、対象がこの人で結果に 8。LP が 0 になった時も)
                        if (!enemy)
                            foreach (Hit hit in a.Hits)
                                if ((hit.Target & 0x80) == 0 && (hit.Target & 0x7F) == col.Actor && ((hit.Result & RES_KO) != 0 || (hit.LpLoss > 0 && hit.LpAfter == 0))) ko = true;
                        if (a.Actor != col.Actor) continue;
                        top = TechName(a.Tech, a.Weapon, false);
                        int dmg = 0, heal = 0, lp = 0, targets = 0; bool hitAny = false, killed = false; string why = null, oneTarget = null;
                        foreach (Hit hit in a.Hits)
                        {
                            targets++;
                            oneTarget = ActorName(hit.Target);
                            if ((hit.Result & RES_HEAL) != 0) { heal += hit.Amount; hitAny = true; continue; }
                            int d = (hit.Result & RES_HP) != 0 ? hit.Amount : 0;
                            dmg += d; if (d > 0) hitAny = true;
                            if (!enemy && d > 0 && HitsWeakness(a.Tech, hit.Target)) weak = true;
                            int l = hit.LpLoss > 0 ? hit.LpLoss : ((hit.Result & RES_LP) != 0 ? hit.Amount : 0);
                            if (l > 0) { lp += l; hitAny = true; }
                            if ((hit.Result & RES_KO) != 0 || (l > 0 && hit.LpAfter == 0)) killed = true;
                            if (d == 0 && l == 0 && why == null) why = Outcome(hit);   // 盾 / 見切り / 外れ (最初の 1 つ)
                        }
                        if (dmg > 0) num = dmg.ToString();
                        else if (heal > 0) num = "+" + heal;
                        else if (lp > 0) { num = "LP −" + lp; red = true; }
                        else if (why != null) { num = why; dim = true; }
                        else if (hitAny || targets > 0) num = "0";
                        // 相手の名前も (1 人ならその名前、複数なら人数)。敵の表も味方の表も同じ書き方 (ユーザー 2026-10-20)
                        if (targets > 0 && num.Length > 0)
                            num = targets == 1 ? oneTarget + " " + num : num + " (" + targets + "人)";
                        if (killed && num.Length > 0) num += " †";
                        if (weak) red = true;   // 弱点をついた (味方の表)
                    }
                    Rect r = new Rect(cx + turnW + colW * c, cy, colW, rowH);
                    Rect topR = new Rect(r.x, r.y, r.width, lineH * 0.9f), botR = new Rect(r.x, r.y + lineH * 0.85f, r.width, lineH * 0.9f);
                    GUIStyle numStyle = red ? s_red : (dim ? s_cellDim : s_cell);
                    if (ko)
                    {
                        // 戦闘不能になったターン: 赤いのは右端の † だけ。技や数字の色は弱点などの意味のまま (ユーザー 2026-10-20: 死の赤が他の内容に紐づくのはやめる)
                        float markW = Mathf.Round(fontSize * 1.3f);
                        if (top.Length == 0 && num.Length == 0) { Txt(botR, "†", s_red); continue; }
                        Txt(topR, top, s_cell);
                        Txt(new Rect(botR.x, botR.y, botR.width - markW, botR.height), num, numStyle);
                        Txt(new Rect(botR.x + botR.width - markW, botR.y, markW, botR.height), "†", s_red);
                        continue;
                    }
                    if (top.Length == 0) { Txt(new Rect(r.x, r.y, r.width, rowH), "－", s_cellDim); continue; }   // 行動なし (戦闘不能・石化など)
                    Txt(topR, top, s_cell);
                    Txt(botR, num, numStyle);
                }
                cy += rowH;
            }
        }

        /// <summary>
        /// 味方の技がその敵の弱点をついたか。技の攻撃属性 (waza_data[技*10+4] のビット、0x80 = 斬 … 0x01 = 状) のうち一番低い防御力が、
        /// その敵の 8 種の中で一番低い値 (ページ 1 で赤にするもの) と同じなら弱点。全部同じ値の敵 (差が無い) は弱点なしとする。
        /// </summary>
        private static bool HitsWeakness(int tech, int targetCode)
        {
            if ((targetCode & 0x80) == 0) return false;
            int slot = targetCode & 0x7F;
            Enemy e = slot < 10 ? s_slotEnemy[slot] : null;
            byte[] w = Data.waza_data;
            if (e == null || w == null || tech < 0 || tech * 10 + 4 >= w.Length) return false;
            int attr = w[tech * 10 + 4] & 0xFF;
            if (attr == 0 || attr == 255) return false;
            int min = 255, max = -1, eff = 255;
            for (int k = 0; k < 8; k++)
            {
                int v = e.Def[k];
                if (v >= 128) continue;
                if (v < min) min = v;
                if (v > max) max = v;
                if ((attr & (0x80 >> k)) != 0 && v < eff) eff = v;
            }
            return min < 255 && max > min && eff == min;
        }

        private static Texture2D Solid(Color c)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        private static void EnsureStyles(int fontSize)
        {
            if (s_fontSizeBuilt == fontSize && s_line != null) return;
            s_fontSizeBuilt = fontSize;
            if (s_font == null)
            {
                try { s_font = Font.CreateDynamicFontFromOSFont(new[] { "Meiryo UI", "Yu Gothic UI", "MS UI Gothic" }, fontSize); }
                catch (Exception e) { Plugin.Log.LogWarning("[BattleReview] OS font unavailable: " + e.Message); }
            }
            s_box = new GUIStyle(GUI.skin.box);
            s_box.normal.background = Solid(new Color(0.18f, 0.18f, 0.2f, 0.94f));
            s_line = new GUIStyle(GUI.skin.label);
            s_line.fontSize = fontSize;
            s_line.alignment = TextAnchor.MiddleLeft;
            s_line.wordWrap = false;
            s_line.normal.textColor = new Color(0.12f, 0.08f, 0.05f);
            if (s_font != null) s_line.font = s_font;
            s_dim = new GUIStyle(s_line);
            s_dim.normal.textColor = new Color(0.3f, 0.26f, 0.22f);   // 薄い灰色は読みにくい (ユーザー 2026-10-20) ので濃いめ
            s_hint = new GUIStyle(s_dim);
            s_hint.fontSize = Mathf.Max(10, fontSize - 3);
            s_title = new GUIStyle(s_line);
            s_title.fontStyle = FontStyle.Bold;
            s_title.fontSize = fontSize + 3;
            s_head = new GUIStyle(s_line);
            s_head.fontStyle = FontStyle.Bold;
            s_head.fontSize = fontSize + 1;
            s_center = new GUIStyle(s_line);
            s_center.alignment = TextAnchor.MiddleCenter;
            s_right = new GUIStyle(s_line);
            s_right.alignment = TextAnchor.MiddleRight;
            s_rightDim = new GUIStyle(s_dim);
            s_rightDim.alignment = TextAnchor.MiddleRight;
            s_red = new GUIStyle(s_center);
            s_red.fontStyle = FontStyle.Bold;
            s_red.normal.textColor = new Color(0.6f, 0.06f, 0.03f);
            s_redRight = new GUIStyle(s_red);
            s_redRight.alignment = TextAnchor.MiddleRight;
            s_cell = new GUIStyle(s_center);
            s_cell.fontSize = Mathf.Max(10, fontSize - 2);
            s_cellDim = new GUIStyle(s_cell);
            s_cellDim.normal.textColor = s_dim.normal.textColor;
            s_cellHead = new GUIStyle(s_cell);
            s_cellHead.normal.textColor = s_dim.normal.textColor;
            UiParts.ResetStyles();
        }
    }

    /// <summary>
    /// 負けの暗転が終わった直後 (sync の状態 300 の直前。Work.monsterdata がまだ生きている) に、窓に出すものを写す。本体は止めない。
    /// (一度ここで窓を開いて sync を止める作りにしたが、戻り値 0 = 終わった と取り違えて勝ち扱いでフィールドに戻る不具合を出した。
    ///  ユーザーの要望も「継承の画面の前の真っ暗な時」なので、窓は Menu 側で開く。2026-10-20)
    /// </summary>
    [HarmonyPatch(typeof(Battle), nameof(Battle.sync))]
    internal static class BattleReviewSyncPatch
    {
        private static void Prefix(Battle __instance) { BattleReview.CaptureAtBattleEnd(__instance); }
    }

    /// <summary>継承の画面の入口 (menu_type 2 / 27 → _emper_chg1、13 → _emper_chg2。_emper_chg3 は _emper_chg1 を呼ぶ)。proc[0] == 0 の最初の 1 回に窓を開く。</summary>
    [HarmonyPatch(typeof(Menu), nameof(Menu._emper_chg1))]
    internal static class BattleReviewSuccession1Patch
    {
        private static bool Prefix(Menu __instance, ref int __result)
        {
            if (BattleReview.GateMenu(__instance)) { __result = 1; return false; }   // 1 = まだ続く (Core は Menu.sync が 0 になるまで待つ)
            return true;
        }
    }

    [HarmonyPatch(typeof(Menu), nameof(Menu._emper_chg2))]
    internal static class BattleReviewSuccession2Patch
    {
        private static bool Prefix(Menu __instance, ref int __result)
        {
            if (BattleReview.GateMenu(__instance)) { __result = 1; return false; }
            return true;
        }
    }

    /// <summary>1 行動の記録: exec の状態 10 (Calc) が 20 (演出) に進んだ直後、mbox が埋まっている。</summary>
    [HarmonyPatch(typeof(Battle), "exec")]
    internal static class BattleReviewExecPatch
    {
        private static void Prefix(Battle __instance, out int __state)
        {
            __state = __instance.proc[__instance.procstg][0];
            if (__state == 0) BattleReview.Snapshot(__instance);   // 行動の始まり: 全員の HP / LP を写す (差分で LP だけ減る技を拾う)
        }
        private static void Postfix(Battle __instance, int __state)
        {
            if (__state == 10 && __instance.proc[__instance.procstg][0] == 20) BattleReview.Record(__instance);
        }
    }

    /// <summary>見切りを覚えた時だけ呼ばれる (test_mikiri の抽選に通った後)。ページ 2 の見切りの欄に「覚えた！」を出すため。</summary>
#pragma warning disable Harmony002   // 本体のメソッド名が set_mikiri (プロパティではない)。名前で引けば見つかる
    [HarmonyPatch(typeof(Battle), "set_mikiri")]
    internal static class BattleReviewMikiriPatch
    {
        private static void Postfix(Battle __instance) { BattleReview.Learned(__instance); }
    }
#pragma warning restore Harmony002

    [HarmonyPatch(typeof(Battle), "init")]
    internal static class BattleReviewInitPatch
    {
        private static void Postfix() { BattleReview.Reset(); }
    }
}
