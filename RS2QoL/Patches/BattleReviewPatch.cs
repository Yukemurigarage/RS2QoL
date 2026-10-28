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
    /// 引き算 (damage00 等) で、128 以上 (bit 0x80) はその属性を無効にする。効き具合は絶対値で 4 段階 (Rate: ◎ ○ △ ×) に分けて出す。
    /// 見切り: waza_data[技*10+8] の bit 0x4000 が立つ技だけ見切れる。難度は Data.mikiri_level[技]。partydata[289..] の旗が
    /// 立っていればもう誰かが覚えている (check_mikiriflag で判定を飛ばす)。
    /// </summary>
    internal static class BattleReview
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> EquipHints;
        public static ConfigEntry<int> WeakMax, NormalMax, ResistMax;

        // ---------------------------------------------------------------- 効き具合の 4 段階 (ユーザー 2026-10-25: 敵側の数字は記号と色に。判定は絶対値)
        //
        // 本体の式 (Battle.set_defence → damage00 (武器) / damage01 (術)): 技の属性のうち、相手の防御力が一番低いものを d として
        //   武器: ((威力 − d、1 未満は 1) × 補正 + 基礎) / 64 × 技の倍率 + 乱数 − d × 5
        //   術:   (系統レベルや魔力の項) + 乱数 − d × 5
        //   (d の bit 0x80 = 無効 → 0)
        // d は「威力から引く」(武器だけ) と「最後に d × 5 を引く」の 2 か所で効く。1 世代目の武器の威力は 7〜15 なので、d がそれを超えると
        // 前者は底 (1) に張り付いて武器の威力が意味を失い、後者は d = 20 で 1 発につき 100 (序盤の 1 発分) を削る。この窓に出る敵の値は 2〜24 ほど。
        // 既定: ◎ 6 以下 (威力 11 の武器でも (11 − 6) = 5 倍の差が残り、引かれるのも 30 まで)、○ 7〜14 (威力の差は薄いが 1 発分は残る)、
        //       △ 15〜19、× 20 以上と無効 (1 発 100 を削られる)。境界は cfg ([BattleReview] WeakMax / NormalMax / ResistMax) で変えられる。
        private static readonly string[] RateMarks = { "◎", "○", "△", "×" };
        private const int RATE_WEAK = 0, RATE_NORMAL = 1, RATE_RESIST = 2, RATE_NONE = 3;

        private static int Cfg(ConfigEntry<int> e, int fallback) { return e != null ? e.Value : fallback; }

        /// <summary>防御力 def の効き具合: 0 = ◎ よく効く (弱点)、1 = ○ 普通、2 = △ 効きにくい、3 = × ほぼ効かない / 無効。</summary>
        private static int Rate(int def)
        {
            if (def >= 128) return RATE_NONE;
            if (def <= Cfg(WeakMax, 6)) return RATE_WEAK;
            if (def <= Cfg(NormalMax, 14)) return RATE_NORMAL;
            if (def <= Cfg(ResistMax, 19)) return RATE_RESIST;
            return RATE_NONE;
        }

        /// <summary>記号の色: ◎ 赤、○ 黒 (通常)、△ × 灰。center = マス用の中央寄せ。</summary>
        private static GUIStyle RateStyle(int rate, bool center)
        {
            if (rate == RATE_WEAK) return center ? s_red : s_redLeft;
            if (rate == RATE_NORMAL) return center ? s_cell : s_line;
            return center ? s_cellDim : s_dim;
        }

        private const int BTN_OK = 0x100000, BTN_CANCEL = 0x400000;
        private const int DIR_UP = 0x20000, DIR_DOWN = 0x80000;   // Util.GetKeypadState のビット (TreasuryScreen と同じ)
        private const float REPEAT_FIRST = 0.4f, REPEAT_NEXT = 0.1f;
        private static int PAGES { get { return EquipHints != null && EquipHints.Value ? 4 : 2; } }   // 装備のヒントの 2 ページは設定で足す (2026-10-25)
        private const int PAGE_ENEMY = 0, PAGE_ENEMY_ACTS = 1, PAGE_WEAPONS = 2, PAGE_ARMOR = 3;   // ターン表 (敵の行動 / こちらの行動) の 2 ページはユーザー 2026-10-25 に外した (91496c6 まで)
        private static readonly string[] TypeNames = { "斬", "打", "突", "射", "熱", "冷", "雷", "状" };
        private static readonly string[] PageTitles = { "敵の弱点と防御力", "敵の技と見切り", "装備のヒント (武器と術)", "装備のヒント (防具)" };   // 「耐」は本体のフォントに無い (実機 2026-10-20) ので「防御力」

        // HpLoss / LpLoss は行動の前後の写し (Snapshot) の差。ソウルスティールのように LP だけ 0 にする技は結果ビットも数字も付かない
        // (set_status3 は XA1[18] = 0 にするだけ。実機 2026-10-20) ので、差分で拾う
        private sealed class Hit { public int Target, Flags, Result, Amount, HpLoss, LpLoss, LpAfter; }
        private sealed class Act { public int Turn, Actor, Tech, Weapon; public Hit[] Hits; }

        private const int FLAG_MISS = 1, FLAG_MIKIRI = 256, FLAG_BLOCK = 1024, FLAG_BLOCK_HALF = 2048;   // mbox +1 の旗。1024 = 盾 / 受け流しで防いだ (set_shield、N_parry)、2048 = 盾で軽減
        private const int RES_HP = 1, RES_LP = 2, RES_KO = 8, RES_HEAL = 16, RES_MISS = 32;             // mbox +5 の結果

        private static readonly int[] s_hpBefore = new int[15], s_lpBefore = new int[15];   // 0..4 = 味方の枠、5..14 = 敵の枠
        private static readonly List<int> s_learned = new List<int>();                       // この戦闘で覚えた見切り (技の番号。set_mikiri)

        private static int SlotIndex(int code) { int s = code & 0x7F; return (code & 0x80) == 0 ? (s < 5 ? s : -1) : (s < 10 ? 5 + s : -1); }

        // 1 回の当たりから数える量。全部のページがここを通る (ページごとに別の数え方をして合計が食い違った。ユーザー 2026-10-25:
        // 防具のページが LP の減りを 1 点 100 で足していて、射 993 が 1393 に見えた)。
        /// <summary>HP のダメージ (本体が出した数字。結果 1 = HP)。回復や LP だけの技は 0。</summary>
        private static int HpOf(Hit h) { return (h.Result & RES_HP) != 0 ? h.Amount : 0; }
        /// <summary>LP の減り (行動の前後の差。結果ビットが付かない技があるので差を先に見る)。</summary>
        private static int LpOf(Hit h) { return h.LpLoss > 0 ? h.LpLoss : ((h.Result & RES_LP) != 0 ? h.Amount : 0); }
        /// <summary>味方の枠に向けた当たりか (敵に向けた回復などは数えない)。</summary>
        private static bool ToParty(Hit h) { return (h.Target & 0x80) == 0 && (h.Target & 0x7F) < 5; }

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
        // 表のページ (武器と術 / 防具) の縦スクロール。行単位。s_maxScroll は描画のたびに内容の高さから出す (ユーザー 2026-10-28: 入り切らない分は送って見せる)
        private static int s_scroll, s_maxScroll, s_heldDir;
        private static float s_nextRepeat;
        private static bool s_clip;
        private static float s_clipTop, s_clipBottom;
        private static string[] s_memberNames = new string[5];
        private static List<Enemy> s_enemies = new List<Enemy>();
        private static int s_turns;
        private static string s_reason = "";
        private static float s_openTime;                   // 開いた時刻 (Time.realtimeSinceStartup)。黒い背景のフェードに使う
        private const float BLACK_FADE_SECONDS = 0.5f;     // 開いてから背景が真っ黒になるまで (ユーザー 2026-10-20: 全滅の後なので真っ暗がよい。閉じたら元の明るさ)
        // 窓は知らせの小窓 (Wallet.DrawNotice) と同じ速さで、右の画面外から減速してすべり込み (ease-out)、閉じると加速して右へ抜ける (ease-in)。
        // 抜け終わるまで s_open のまま (本体の継承の画面は止めたまま、入力は捨てる)。ユーザー 2026-10-25
        private const float SLIDE_IN = 0.4f, SLIDE_OUT = 0.3f;
        private static bool s_closing;                     // 閉じる操作の後、右へ抜けている間
        private static float s_closeTime;

        public static bool IsOpen { get { return s_open; } }

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("BattleReview", "Enabled", true,
                "全滅した時 (皇帝が倒れた時も)、継承の画面に行く前に「戦闘の振り返り」の窓を出す。敵の弱点と防御力 (8 種の防御力と ◎○△× の印)、敵の技と見切りの難度の 2 ページ (LB / RB で送る、決定かキャンセルで閉じる。EquipHints で「装備のヒント」の 2 ページを足す)");
            EquipHints = config.Bind("BattleReview", "EquipHints", false,
                "戦闘の振り返りに「装備のヒント」の 2 ページを足す。武器と術: 敵によく効く属性ごとに、手に入る中で一番強い武器 (または研究所で覚えられる術) と、今行ける店で売っているか (○×)、その武器を今いくつ持っているか。防具: 敵が使った属性ごとに同じ表 (どちらも誰かが装備中 / 持ち物 / 倉庫 / 今ワールドマップで行ける町の店で売っている品から)");
            WeakMax = config.Bind("BattleReview", "WeakMax", 6,
                "敵の属性防御力がこの値以下なら ◎ (よく効く = 弱点、赤) と見る。本体の式では防御力は「武器の威力から引く」と「最後に 5 倍を引く」の 2 か所で効き、1 世代目の武器 (威力 7〜15) で差が残るのはこの辺りまで");
            NormalMax = config.Bind("BattleReview", "NormalMax", 14,
                "敵の属性防御力がこの値以下 (WeakMax より上) なら ○ (普通に効く)。これより上は △ (効きにくい)");
            ResistMax = config.Bind("BattleReview", "ResistMax", 19,
                "敵の属性防御力がこの値以下 (NormalMax より上) なら △ (効きにくい)。これより上と「無効」は × (ほぼ効かない)。既定の 20 以上は 1 発につき 100 を削られる");
            Plugin.Log.LogInfo("[BattleReview] patched: Battle.sync (capture at 300) / exec / set_mikiri / Menu._emper_chg1 / _emper_chg2 (enabled=" + Enabled.Value + ")");
        }

        // ---------------------------------------------------------------- 記録

        internal static void Reset()
        {
            s_acts.Clear();
            s_shown = false;
            s_open = false; s_closing = false;
            s_guard = false;
            s_namesBuilt = false;
            s_learned.Clear();
            s_captured = false;
            s_previewCaptured = false;
            s_shopLogged = false;
        }

        /// <summary>ソフトリセット (SoftReset.RequestReset) から。窓が開いたままタイトルに戻ると出っ放しになる (実機 2026-10-25) ので閉じて記録も捨てる。</summary>
        internal static void OnSoftReset()
        {
            bool was = s_open;
            Reset();
            s_preview = false;
            s_guard = false;
            if (was) Plugin.Log.LogInfo("[BattleReview] closed by soft reset");
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
            bool lost = (b.party_result == 1 || b.party_result == 2) && !(b.event_kind != 0 && (b.event_flag & 1) != 0);
            if (!lost)
            {
                // 負け以外は窓を出さないが、Diagnostics On なら記録だけ写しておく (プレビュー (F6) で最後の戦闘を使えるように。2026-10-25)
                if (!Plugin.DiagOn || s_previewCaptured) return;
                try { Capture(b, "最後の戦闘"); s_captured = false; s_previewCaptured = true; }
                catch (Exception e) { Fail("capture", e); }
                return;
            }
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
            s_open = false; s_closing = false;
            Plugin.Log.LogError("[BattleReview] " + where + " failed, feature disabled until restart: " + e);
        }

        // ---------------------------------------------------------------- 開く / 閉じる

        private static bool s_captured;                              // 負けの戦闘の記録を持っている (継承の画面で出す)
        private static bool s_previewCaptured;                       // この戦闘の記録をプレビュー用に写した (状態 300 は何フレームか続くので 1 回だけ)

        // ---------------------------------------------------------------- プレビュー (Diagnostics On のときだけ。2026-10-25)
        // 全滅しなくても窓を見るためのもの。F6 で最後の戦闘 (無ければ空) を使って窓を開く。「記録」の 録 は本体のフォントに無い。中身・操作は本番と同じ (F6 でも閉じる)。
        // 文章やレイアウトの調整を「直す → 1 戦して F6」の往復で回すため (外部ファイルで文字列を変える案は 2026-10-25 にユーザーが取り下げた)。

        private static bool s_preview;
        private const KeyCode KEY_PREVIEW = KeyCode.F6;

        private static bool KeyEdge(bool firstCallThisFrame, KeyCode k)
        {
            return firstCallThisFrame && (Input.GetKeyDown(k) || Interp60.TakeKeyDown(k));
        }

        private static void OpenPreview()
        {
            if (s_enemies == null) s_enemies = new List<Enemy>();
            if (s_memberNames == null) { s_memberNames = new string[5]; for (int i = 0; i < 5; i++) s_memberNames[i] = "?"; }
            if (!s_previewCaptured && !s_captured) s_reason = "戦闘なし";
            Open();
            s_preview = true;
            Plugin.Log.LogInfo("[BattleReview] preview open (" + s_reason + ")");
        }
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
            s_prevKey = -1;
            s_scroll = 0; s_heldDir = 0;
            s_prevLB = PageBackDown(); s_prevRB = PageNextDown();
            s_open = true;
            s_shopLogged = false;   // 開くたびに店の判定をログに (調査用)
            s_openTime = Time.realtimeSinceStartup;
            UiParts.SeOk();
            Plugin.Log.LogInfo("[BattleReview] applied: open (" + s_reason + ") at the succession screen");
        }

        /// <summary>閉じる操作。音を鳴らして右へ抜け始める。抜け終わったら FinishClose。</summary>
        private static void Close()
        {
            if (!s_open || s_closing) return;
            s_closing = true;
            s_closeTime = Time.realtimeSinceStartup;
            UiParts.SeClose();
            Plugin.Log.LogInfo("[BattleReview] close");
        }

        private static void FinishClose()
        {
            if (!s_open) return;
            s_open = false; s_closing = false;
            s_closing = false;
            s_guard = true;
            if (!s_preview) s_captured = false;   // 1 回出したら捨てる (次に継承の画面が出る時に再び開かない)。プレビューは負けの記録を消さない
            s_preview = false;
        }

        /// <summary>右へ抜けている途中なら、時間が来た時に閉じ終える。Draw と Tick の両方から (設定画面が開いていて Draw が走らない時の保険)。</summary>
        private static void AdvanceClose()
        {
            if (s_closing && Time.realtimeSinceStartup - s_closeTime >= SLIDE_OUT) FinishClose();
        }

        private static bool PageBackDown() { return PadMap.IsPhysicalDown(PadMap.PHYS_LB) || KeyMap.IsDown(PadFunction.L); }
        private static bool PageNextDown() { return PadMap.IsPhysicalDown(PadMap.PHYS_RB) || KeyMap.IsDown(PadFunction.R); }

        private static void TurnPage(int d)
        {
            s_page = (s_page + d + PAGES) % PAGES;
            s_scroll = 0;
            UiParts.SeCursor();
        }

        /// <summary>Util.GetKeypadState の最終段から。開いている間は入力を奪い、決定 / キャンセル / Esc で閉じる。</summary>
        public static int Tick(int key)
        {
            bool first = Time.frameCount != s_lastEscFrame;   // 倍速中は同じ Unity フレームで何度も呼ばれる。GetKeyDown は 1 回目だけ見る
            s_lastEscFrame = Time.frameCount;
            if (!s_open)
            {
                // Diagnostics On: F6 でプレビューを開く (全滅しなくても窓を見るため。設定画面が開いていれば無視)
                if (!s_failed && Plugin.DiagOn && !ConfigMenu.IsOpen && KeyEdge(first, KEY_PREVIEW))
                {
                    try { OpenPreview(); } catch (Exception e) { Fail("preview", e); }
                    return 0;
                }
                if (!s_guard) return key;
                if ((key & (BTN_OK | BTN_CANCEL)) == 0) s_guard = false;
                return 0;
            }
            if (s_closing) { AdvanceClose(); s_prevKey = key; return 0; }   // 右へ抜けている間は入力を捨てるだけ
            int pressed = s_prevKey < 0 ? 0 : key & ~s_prevKey;
            s_prevKey = key;
            bool escEdge = KeyEdge(first, KeyCode.Escape) || (s_preview && KeyEdge(first, KEY_PREVIEW));
            if ((pressed & (BTN_OK | BTN_CANCEL)) != 0 || escEdge) { Close(); return 0; }
            bool lb = PageBackDown(), rb = PageNextDown();
            if (lb && !s_prevLB) TurnPage(-1);
            else if (rb && !s_prevRB) TurnPage(+1);
            s_prevLB = lb; s_prevRB = rb;
            int dir = key & (DIR_UP | DIR_DOWN);
            float now = Time.realtimeSinceStartup;
            if (dir == 0) s_heldDir = 0;
            else if (dir != s_heldDir) { s_heldDir = dir; s_nextRepeat = now + REPEAT_FIRST; Scroll((dir & DIR_UP) != 0 ? -1 : 1); }
            else if (now >= s_nextRepeat) { s_nextRepeat = now + REPEAT_NEXT; Scroll((dir & DIR_UP) != 0 ? -1 : 1); }
            return 0;
        }

        private static void Scroll(int d)
        {
            int v = Mathf.Clamp(s_scroll + d, 0, s_maxScroll);
            if (v == s_scroll) return;
            s_scroll = v;
            UiParts.SeCursor();
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

        /// <summary>技の攻撃属性のビット (waza_data[技*10+4]。0x80 = 斬 … 0x01 = 状。10 未満の通常攻撃も同じ表)。無ければ 0。</summary>
        private static int TechAttr(int tech)
        {
            byte[] w = Data.waza_data;
            if (w == null || tech < 0 || tech * 10 + 4 >= w.Length) return 0;
            int a = w[tech * 10 + 4] & 0xFF;
            return a == 255 ? 0 : a;
        }

        /// <summary>属性ビットを「斬」「熱」「斬熱」のような文字に。0 なら「－」。</summary>
        private static string AttrText(int attr)
        {
            if (attr == 0) return "－";
            StringBuilder sb = new StringBuilder();
            for (int k = 0; k < 8; k++) if ((attr & (0x80 >> k)) != 0) sb.Append(TypeNames[k]);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 描画 (Plugin.OnGUI)

        private static Font s_font;
        private static GUIStyle s_line, s_dim, s_title, s_box, s_hint, s_head, s_center, s_right, s_rightDim, s_red, s_redRight, s_redLeft, s_redRightPlain, s_cell, s_cellDim, s_cellHead;
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
                    if (c != ' ' && !f.HasCharacter(c)) { ok = false; if (Plugin.DiagOn) Plugin.Log.LogInfo("[BattleReview] game font lacks '" + c + "' (U+" + ((int)c).ToString("X4") + ") in \"" + s + "\" → OS font"); break; }
            if (s_glyphOk.Count > 4000) s_glyphOk.Clear();
            s_glyphOk[s] = ok;
            return ok;
        }

        // Diagnostics On: 本体のフォントにある字を全部と、本体 / OS のフォントの寸法 (行の高さ・ベースライン・字の上下) を 1 回ログに。
        // OS のフォントに落ちた欄だけ字が下に寄って見える (実機 2026-10-25) ので、どの字が無いか・どれだけずれるかを調べるため
        private static bool s_fontLogged;

        private static void LogFonts(int fontSize)
        {
            if (s_fontLogged || !Plugin.DiagOn) return;
            s_fontLogged = true;
            Font f = UiParts.GameFont();
            if (f == null) { Plugin.Log.LogInfo("[BattleReview] game font: null"); return; }
            CharacterInfo[] all = f.characterInfo;
            List<char> chars = new List<char>();
            if (all != null) foreach (CharacterInfo c in all) chars.Add((char)c.index);
            chars.Sort();
            Plugin.Log.LogInfo("[BattleReview] game font " + f.name + ": fontSize=" + f.fontSize + " lineHeight=" + f.lineHeight + " ascent=" + f.ascent + " dynamic=" + f.dynamic + " glyphs=" + chars.Count + " (drawn at " + fontSize + ")");
            StringBuilder sb = new StringBuilder();
            foreach (char c in chars) { sb.Append(c); if (sb.Length >= 100) { Plugin.Log.LogInfo("[BattleReview] glyphs: " + sb); sb.Length = 0; } }
            if (sb.Length > 0) Plugin.Log.LogInfo("[BattleReview] glyphs: " + sb);
            string probe = "0 1 , . × ◎ ○ △ ― ・ 敵 威 防 被 x ＋ － ※ ●";
            sb.Length = 0; sb.Append("[BattleReview] game font metrics (minY..maxY advance):");
            foreach (char c in probe) { if (c == ' ') continue; CharacterInfo ci; sb.Append(' ').Append(c).Append(f.GetCharacterInfo(c, out ci) ? "=" + ci.minY + ".." + ci.maxY + "/" + ci.advance : "=none"); }
            Plugin.Log.LogInfo(sb.ToString());
            if (s_font != null)
            {
                try
                {
                    s_font.RequestCharactersInTexture(probe, fontSize, FontStyle.Normal);
                    sb.Length = 0; sb.Append("[BattleReview] OS font " + s_font.name + ": fontSize=" + s_font.fontSize + " lineHeight=" + s_font.lineHeight + " ascent=" + s_font.ascent + " metrics at " + fontSize + ":");
                    foreach (char c in probe) { if (c == ' ') continue; CharacterInfo ci; sb.Append(' ').Append(c).Append(s_font.GetCharacterInfo(c, out ci, fontSize) ? "=" + ci.minY + ".." + ci.maxY + "/" + ci.advance : "=none"); }
                    Plugin.Log.LogInfo(sb.ToString());
                }
                catch (Exception e) { Plugin.Log.LogInfo("[BattleReview] OS font metrics failed: " + e.Message); }
            }
        }

        private static void Txt(Rect r, string s, GUIStyle st)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (s_clip && (r.y < s_clipTop - 0.5f || r.y + r.height > s_clipBottom + 0.5f)) return;   // 表のスクロール: 枠からはみ出す行は描かない (行単位)
            if (HasMark(s)) { TxtMarks(r, s, st); return; }
            if (GameFontHas(s)) UiParts.GameLabel(r, s, st);
            else UiParts.Label(new Rect(r.x, r.y + s_osNudge, r.width, r.height), s, st);   // OS のフォントは字が行の中で下に寄る → 本体のフォントの字の中心に合わせる
        }

        private static float TextW(string s, GUIStyle st)
        {
            if (HasMark(s)) return MarksW(s, st);
            return GameFontHas(s) ? UiParts.GameTextWidth(s, st) : st.CalcSize(new GUIContent(s)).x;
        }

        // ---------------------------------------------------------------- 記号 ◎ ○ △ × (本体のフォントに無い)
        //
        // 本体のフォント (UDTypos512) には ◎ ○ △ × ● のような図形の字が 1 つも無い (あるのは ※ ← ▼ ― ‥ … と英数字。実機のダンプ 2026-10-25)。
        // 無い字が混ざった文字列は OS のフォントで描いていたが、字の上下の位置がずれて「その欄だけ下がって見える」(実機 2026-10-25)。
        // → 4 つの記号は自前の絵 (白いテクスチャに文字の色を掛ける) で描き、前後の文字は本体のフォントで描く。
        //   絵の中心は本体のフォントの「0」の字の中心 (行の中心から s_gameCenterOff) に置く。文字列の幅も同じ計算で測る (折り返しに使う)。

        private static readonly Dictionary<char, Texture2D> s_marks = new Dictionary<char, Texture2D>();
        private static readonly Dictionary<GUIStyle, GUIStyle> s_leftStyles = new Dictionary<GUIStyle, GUIStyle>();
        private static float s_gameCenterOff;   // 本体のフォントで描いた字の中心が行の中心からどれだけ下か (画面の画素。負 = 上)
        private static float s_osNudge;         // OS のフォントで描く時に矩形をどれだけ動かせば字の中心が本体の字と揃うか
        private const int MARK_PX = 64;

        private static bool IsMark(char c) { return c == '◎' || c == '○' || c == '△' || c == '×'; }
        private static bool HasMark(string s) { foreach (char c in s) if (IsMark(c)) return true; return false; }

        /// <summary>記号の送り幅 (em)。◎ ○ △ は全角、× は欧文の幅。</summary>
        private static float MarkAdvance(char c) { return c == '×' ? 0.8f : 1f; }
        /// <summary>記号の絵の大きさ (em)。◎ ○ △ は字の高さと同じくらい、× は小さめ。</summary>
        private static float MarkSize(char c) { return c == '×' ? 0.5f : 0.78f; }

        private static float MarksW(string s, GUIStyle st)
        {
            float w = 0f; int start = 0;
            for (int i = 0; i <= s.Length; i++)
            {
                if (i < s.Length && !IsMark(s[i])) continue;
                if (i > start) w += TextW(s.Substring(start, i - start), st);
                if (i < s.Length) w += Mathf.Round(st.fontSize * MarkAdvance(s[i]));
                start = i + 1;
            }
            return w;
        }

        private static GUIStyle LeftOf(GUIStyle st)
        {
            if (st.alignment == TextAnchor.MiddleLeft) return st;
            GUIStyle l;
            if (!s_leftStyles.TryGetValue(st, out l)) { l = new GUIStyle(st); l.alignment = TextAnchor.MiddleLeft; s_leftStyles[st] = l; }
            return l;
        }

        /// <summary>記号を含む文字列: 文字の部分は本体のフォント、記号は絵で、左から並べる。st の寄せ (左 / 中央 / 右) は全体の幅で合わせる。</summary>
        private static void TxtMarks(Rect r, string s, GUIStyle st)
        {
            float total = MarksW(s, st);
            float x = r.x;
            if (st.alignment == TextAnchor.MiddleCenter) x = r.x + Mathf.Round((r.width - total) / 2f);
            else if (st.alignment == TextAnchor.MiddleRight) x = r.x + r.width - total;
            GUIStyle left = LeftOf(st);
            int start = 0;
            for (int i = 0; i <= s.Length; i++)
            {
                if (i < s.Length && !IsMark(s[i])) continue;
                if (i > start)
                {
                    string run = s.Substring(start, i - start);
                    float w = TextW(run, left);
                    Txt(new Rect(x, r.y, w + 4f, r.height), run, left);
                    x += w;
                }
                if (i < s.Length) { DrawMark(s[i], x, r, st); x += Mathf.Round(st.fontSize * MarkAdvance(s[i])); }
                start = i + 1;
            }
        }

        private static void DrawMark(char c, float x, Rect line, GUIStyle st)
        {
            Texture2D t = MarkTex(c);
            float adv = Mathf.Round(st.fontSize * MarkAdvance(c));
            float size = Mathf.Round(st.fontSize * MarkSize(c));
            float cx = x + adv / 2f, cy = line.y + line.height / 2f + s_gameCenterOff;
            Rect m = new Rect(Mathf.Round(cx - size / 2f), Mathf.Round(cy - size / 2f), size, size);
            Color old = GUI.color;
            bool bold = st.fontStyle == FontStyle.Bold;
            if (UiParts.Shadow)
            {
                float o = Mathf.Max(1f, Mathf.Round(UiParts.Unit * 0.75f));
                GUI.color = new Color(0f, 0f, 0f, 0.8f * old.a);
                GUI.DrawTexture(new Rect(m.x + o, m.y + o, m.width, m.height), t, ScaleMode.StretchToFill, true);
                if (bold) GUI.DrawTexture(new Rect(m.x + o + 1f, m.y + o, m.width, m.height), t, ScaleMode.StretchToFill, true);
            }
            Color tc = st.normal.textColor;
            GUI.color = new Color(tc.r, tc.g, tc.b, tc.a * old.a);
            if (bold) GUI.DrawTexture(new Rect(m.x + 1f, m.y, m.width, m.height), t, ScaleMode.StretchToFill, true);
            GUI.DrawTexture(m, t, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        /// <summary>記号の絵 (白、縁は 4×4 の点で滑らかに)。中心 (0.5, 0.5)、1 = 絵の一辺。</summary>
        private static Texture2D MarkTex(char c)
        {
            Texture2D t;
            if (s_marks.TryGetValue(c, out t) && t != null) return t;
            t = new Texture2D(MARK_PX, MARK_PX, TextureFormat.ARGB32, false);
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            t.hideFlags = HideFlags.HideAndDontSave;
            const int SS = 4;
            Color32[] px = new Color32[MARK_PX * MARK_PX];
            for (int y = 0; y < MARK_PX; y++)
                for (int x = 0; x < MARK_PX; x++)
                {
                    int hit = 0;
                    for (int sy = 0; sy < SS; sy++)
                        for (int sx = 0; sx < SS; sx++)
                        {
                            float u = (x + (sx + 0.5f) / SS) / MARK_PX, v = 1f - (y + (sy + 0.5f) / SS) / MARK_PX;   // v は上が 0
                            if (MarkInside(c, u, v)) hit++;
                        }
                    byte a = (byte)(hit * 255 / (SS * SS));
                    px[y * MARK_PX + x] = new Color32(255, 255, 255, a);
                }
            t.SetPixels32(px);
            t.Apply();
            s_marks[c] = t;
            return t;
        }

        private static bool MarkInside(char c, float u, float v)
        {
            float dx = u - 0.5f, dy = v - 0.5f, d = Mathf.Sqrt(dx * dx + dy * dy);
            switch (c)
            {
                case '○': return d <= 0.46f && d >= 0.36f;
                case '◎': return (d <= 0.48f && d >= 0.405f) || (d <= 0.235f && d >= 0.16f);
                case '×':
                    {
                        // 2 本の対角線 (太さ 0.16、端は 0.1 〜 0.9)
                        float a = Mathf.Abs(dx - dy) / 1.41421f, b = Mathf.Abs(dx + dy) / 1.41421f;
                        bool box = u >= 0.1f && u <= 0.9f && v >= 0.1f && v <= 0.9f;
                        return box && (a <= 0.08f || b <= 0.08f);
                    }
                case '△':
                    {
                        // 正三角形の縁 (上の頂点 (0.5, 0.06)、底辺 v = 0.86)。内側の三角形は重心に向けて縮めたもの
                        float top = 0.06f, bot = 0.86f, h = bot - top, half = h / 1.7320508f;   // 半分の底辺
                        float gy = top + h * 2f / 3f;                                              // 重心の v
                        float inr = h / 3f, k = (inr - 0.09f) / inr;                               // 内接円の半径、縮め率 (縁の太さ 0.09)
                        return InTri(u, v, 0.5f, top, half, h) && !InTri(0.5f + (u - 0.5f) / k, gy + (v - gy) / k, 0.5f, top, half, h);
                    }
            }
            return false;
        }

        /// <summary>上の頂点 (ax, ay)、底辺の半分 half、高さ h の三角形の中か。</summary>
        private static bool InTri(float u, float v, float ax, float ay, float half, float h)
        {
            float t = (v - ay) / h;
            if (t < 0f || t > 1f) return false;
            return Mathf.Abs(u - ax) <= half * t;
        }

        /// <summary>本体のフォントと OS のフォントの、字の中心の位置 (行の中心からの差) を測る。EnsureStyles から (fontSize が変わった時)。</summary>
        private static void MeasureCenters(int fontSize)
        {
            s_gameCenterOff = 0f; s_osNudge = 0f;
            try
            {
                Font f = UiParts.GameFont();
                CharacterInfo gi;
                if (f != null && f.GetCharacterInfo('0', out gi))
                {
                    float sc = fontSize / (float)f.fontSize;
                    s_gameCenterOff = (f.ascent - f.lineHeight / 2f - (gi.minY + gi.maxY) / 2f) * sc;
                }
                if (s_font != null)
                {
                    s_font.RequestCharactersInTexture("0", fontSize, FontStyle.Normal);
                    CharacterInfo oi;
                    if (s_font.GetCharacterInfo('0', out oi, fontSize))
                    {
                        float sc = s_font.fontSize > 0 ? fontSize / (float)s_font.fontSize : 1f;
                        float osOff = (s_font.ascent * sc - s_font.lineHeight * sc / 2f) - (oi.minY + oi.maxY) / 2f;
                        s_osNudge = Mathf.Round(s_gameCenterOff - osOff);
                    }
                }
                if (Plugin.DiagOn) Plugin.Log.LogInfo("[BattleReview] text centers at " + fontSize + ": game " + s_gameCenterOff.ToString("0.00") + " px below line center, OS label nudge " + s_osNudge);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[BattleReview] MeasureCenters: " + e.Message); }
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
            LogFonts(fontSize);
            float pad = lineH * 0.6f;
            float headH = Mathf.Round(lineH * 1.4f);
            float footH = lineH;
            float w = Mathf.Min(Screen.width - 2 * outer, Mathf.Round(fontSize * 46f));
            float h = Mathf.Min(Screen.height - 2 * outer, Mathf.Round(pad * 2 + headH + lineH * 0.3f + lineH * 18.5f + lineH * 0.3f + footH));
            float now = Time.realtimeSinceStartup;
            AdvanceClose();
            if (!s_open) return;
            // 中央が定位置。出る時は右の画面外から減速してすべり込み、閉じると加速して右へ抜ける (知らせの小窓と同じ 0.4 / 0.3 秒)
            float off = 0f;                                                                      // 0 = 定位置、1 = 画面の右外
            if (s_closing) { float t = Mathf.Clamp01((now - s_closeTime) / SLIDE_OUT); off = t * t * t; }
            else if (now < s_openTime + SLIDE_IN) { float t = Mathf.Clamp01((now - s_openTime) / SLIDE_IN); off = (1f - t) * (1f - t) * (1f - t); }
            float restX = Mathf.Round((Screen.width - w) / 2f);
            float x = Mathf.Round(restX + off * (Screen.width - restX));
            float y = Mathf.Round((Screen.height - h) / 2f);
            Rect win = new Rect(x, y, w, h);
            // 背景を真っ黒に (開いてから BLACK_FADE_SECONDS で暗くなる。閉じ終わると描かなくなるので元の明るさに戻り、本体が自分の暗転に進む)
            {
                float a = Mathf.Clamp01((now - s_openTime) / BLACK_FADE_SECONDS);
                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, a);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = old;
            }
            UiParts.Shadow = !UiParts.DrawPaperWindow(win, false, new Vector2(x - restX, 0f));   // 壁紙も窓と一緒に動かす
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

            s_maxScroll = 0; s_clip = false;   // 表のページだけが BeginTable / EndTable で入れ直す
            switch (s_page)
            {
                case PAGE_ENEMY: DrawEnemyPage(cx, cy, cw, lineH, fontSize, bodyBottom); break;
                case PAGE_ENEMY_ACTS: DrawEnemyActsPage(cx, cy, cw, lineH, fontSize, bodyBottom); break;
                case PAGE_WEAPONS: DrawWeaponTablePage(cx, cy, cw, lineH, fontSize, bodyBottom); break;
                case PAGE_ARMOR: DrawArmorTablePage(cx, cy, cw, lineH, fontSize, bodyBottom); break;
            }

            string foot = "LB / RB: ページ   " + (s_maxScroll > 0 ? "上下: 表を送る   " : "") + "決定・キャンセル・Esc: 閉じて継承へ";
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
                // 数字 (防御力) を残すのはこのページだけ。記号と色は絶対値の 4 段階 (前は「その敵の中で一番低い値」を赤にしていて、全属性 20 の敵にも赤が付いた。ユーザー 2026-10-25)
                for (int k = 0; k < 8; k++)
                {
                    int v = e.Def[k];
                    Rect r = new Rect(defX + cellW * k, cy, cellW, lineH);
                    if (v >= 128) Txt(r, "無効", s_cellDim);
                    else { int rate = Rate(v); Txt(r, RateMarks[rate] + v, RateStyle(rate, true)); }
                }
                cy += lineH;
            }
            cy += lineH * 0.3f;
            // 説明: 表の下に 1 行空け、文ごとに 1 行 (長ければ折り返す)、1 行空けて属性の説明 (ユーザー 2026-10-21: 改行の位置と余白)
            float hintH = Mathf.Round(lineH * 0.9f);
            cy += hintH;
            cy = DrawWrapped(cx, cy, cw, hintH, "数字 = 防御力 (小さいほどその属性のダメージが通る)。「無効」はその属性のダメージを受けない。", s_hint, bottom);
            cy = DrawWrapped(cx, cy, cw, hintH, "◎ よく効く (弱点)  ○ 普通に効く  △ 効きにくい  × ほぼ効かない", s_hint, bottom);
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
            // 列の並び: 技 / 属性 / 見切り / 回数 / 合計 / 最大 (ユーザー 2026-10-25: 属性を回数の左に、その右に見切り)
            float colCnt = Mathf.Round(fontSize * 3.2f), colSum = Mathf.Round(fontSize * 4.5f), colMax = Mathf.Round(fontSize * 4f), colMik = Mathf.Round(fontSize * 5f), colAttr = Mathf.Round(fontSize * 3.2f);
            float rightPad = Mathf.Round(fontSize * 4f);   // 列全体を少し左へ (ユーザー 2026-10-20 → 21: もっと左に)
            float maxX = cx + cw - rightPad - colMax, sumX = maxX - colSum, cntX = sumX - colCnt, mikX = cntX - colMik, attrX = mikX - colAttr;
            float nameW = attrX - cx - fontSize * 0.5f;
            Txt(new Rect(cx, cy, nameW, lineH), "技 (使った敵)", s_dim);
            Txt(new Rect(attrX, cy, colAttr, lineH), "属性", s_rightDim);
            Txt(new Rect(mikX, cy, colMik, lineH), "見切り", s_rightDim);
            Txt(new Rect(cntX, cy, colCnt, lineH), "回数", s_rightDim);
            Txt(new Rect(sumX, cy, colSum, lineH), "合計", s_rightDim);
            Txt(new Rect(maxX, cy, colMax, lineH), "最大", s_rightDim);
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
                        if (!ToParty(hit)) continue;           // 敵に向けた行動 (回復など) は数えない
                        int t = hit.Target & 0x7F;
                        int dmg = HpOf(hit), lp = LpOf(hit);   // 防具のページ (UsedAttrs) と同じ数え方
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
                    Txt(new Rect(mikX, cy, colMik, lineH), mik, mik.StartsWith("難度") || mik.StartsWith("覚") ? s_redRightPlain : s_rightDim);   // 太字にしない (本体のフォントの太字は 1 画素ずらして 2 回描くので文字が読みにくい。ユーザー 2026-10-25)
                    Txt(new Rect(attrX, cy, colAttr, lineH), AttrText(TechAttr(u.Tech)), s_right);
                    cy += lineH;
                    // 「誰にいくら」の薄い行は出さない (ユーザー 2026-10-20: 要らない。ターン表の方にある)
                }
                // 行動表にあってまだ使っていない技は出さない (ユーザー 2026-10-20: 見たくない)
                cy += lineH * 0.2f;
            }
            if (!any && s_enemies.Count == 0) Txt(new Rect(cx, cy, cw, lineH), "敵の記録がありません", s_dim);
        }

        // ---------------------------------------------------------------- 装備のヒント ([BattleReview] EquipHints、既定 Off。2026-10-25)
        //
        // 部位: 本体の装備画面 (Menu.equip_work_make / make_bougu_name) は item_menu_data[id*5+2] のビットで部位の重なりを見る
        //   (1 = 頭、2 = 体、4 = 腕、8 = 足、16 = 服、0 = 装飾 (重ならない)、15 = 全身 (頭体腕足を占める)、32 = 盾、0x80 = 外せない)。
        //   盾は armor_data を持たない (回避の判定) のでここでは出さない。
        // 店: 本体は店ごとの品物表を持たない。店 (Data.shop_data の 32 店 × 4 分類) は分類 (192..223) を並べるだけで、分類ごとの
        //   段階 (partydata[97 + 分類/2] の 4 ビット。偶数は上位、奇数は下位 = Core.get_flag_atai) が Data.shop_item[(分類-192)*8 + 段階] の
        //   品を決める。開発所の開発は店に品を足すのではなく分類の段階を上げる (msg 2916 / 2915)。
        //   「今買える」= 今行ける町 (ShopPlaces の表で判定) の店が並べている分類の、今の段階の品。
        // 装備中: memberdata[81..83] (84 は仲間の作り込みの防具で外せないので候補にしない)。

        private const string SRC_EQUIP = "装", SRC_HELD = "持", SRC_STORED = "倉", SRC_SHOP = "店";
        private static readonly string[] PartNames = { "頭", "体", "腕", "足", "服", "装飾", "全身" };
        private const int PARTS = 7;

        private sealed class AttrCol { public int Index, Damage, Lp; public List<string> Enemies = new List<string>(), Techs = new List<string>(); }
        // InShop = 今行ける店で売っている (持っていても立つ。候補を 1 品 1 つにまとめる時に「店でも買える」が消えて、
        // 持ち物にあるメイスが「店に無い」と出た。実機 2026-10-25)
        private sealed class ArmorCand { public int Id, Part; public string Src; public bool InShop; }

        /// <summary>防具の部位 (PartNames の添字)。防具 (96..191) 以外は -1。</summary>
        private static int ArmorPart(int id)
        {
            if (id < 96 || id >= 192 || Data.item_menu_data == null || id * 5 + 2 >= Data.item_menu_data.Length) return -1;
            int bits = Data.item_menu_data[id * 5 + 2] & 0x7F;
            if ((bits & 15) == 15) return 6;
            switch (bits)
            {
                case 1: return 0;
                case 2: return 1;
                case 4: return 2;
                case 8: return 3;
                case 16: return 4;
                case 0: return 5;
            }
            return -1;
        }

        private static int ArmorDef(int id, int k)
        {
            int a = (id - 96) * 12 + k;
            return (Data.armor_data != null && a < Data.armor_data.Length) ? Data.armor_data[a] & 0xFF : 0;
        }

        /// <summary>
        /// この戦闘で敵が味方に使った技の属性 (HP のダメージの多い順、同じなら LP の減りの多い順)。属性が複数ある技はそれぞれに数える。
        /// Damage は 2 ページ目の「合計」と同じ数え方 (HpOf。LP は Lp に別に持つ) なので、ページをまたいで同じ数字になる。
        /// </summary>
        private static List<AttrCol> UsedAttrs()
        {
            List<AttrCol> cols = new List<AttrCol>();
            foreach (Act a in s_acts)
            {
                if ((a.Actor & 0x80) == 0) continue;
                int attr = TechAttr(a.Tech);
                if (attr == 0) continue;
                int dmg = 0, lp = 0;
                foreach (Hit h in a.Hits)
                {
                    if (!ToParty(h)) continue;
                    dmg += HpOf(h);
                    lp += LpOf(h);
                }
                for (int k = 0; k < 8; k++)
                {
                    if ((attr & (0x80 >> k)) == 0) continue;
                    AttrCol c = null;
                    foreach (AttrCol x in cols) if (x.Index == k) { c = x; break; }
                    if (c == null) { c = new AttrCol { Index = k }; cols.Add(c); }
                    c.Damage += dmg;
                    c.Lp += lp;
                    string en = ActorName(a.Actor);
                    if (!c.Enemies.Contains(en)) c.Enemies.Add(en);
                    string tn = TechName(a.Tech, a.Weapon, true);
                    if (!c.Techs.Contains(tn)) c.Techs.Add(tn);
                }
            }
            cols.Sort((p, q) => p.Damage != q.Damage ? q.Damage.CompareTo(p.Damage) : q.Lp.CompareTo(p.Lp));
            return cols;
        }

        /// <summary>表に出す「受けた量」: 被993。HP のダメージが無ければ技の名前 (影ぬい など)。LP の減りは出さない (ユーザー 2026-10-25)。</summary>
        private static string TakenText(AttrCol a, bool withTechs)
        {
            if (a.Damage > 0) return "被" + Num(a.Damage);
            return withTechs && a.Techs.Count > 0 ? string.Join("・", a.Techs.ToArray()) : "被0";
        }

        /// <summary>本体の旗 (Core.get_flag_atai と同じ: partydata[97 + N/2] の 4 ビット。偶数は上位、奇数は下位)。</summary>
        private static int Flag(int n)
        {
            int[] pd = Work.partydata;
            int i = 97 + n / 2;
            if (pd == null || i >= pd.Length) return 0;
            return (n & 1) != 0 ? pd[i] & 0xF : (pd[i] >> 4) & 0xF;
        }

        // 店 → 町の対応 (script の shop_rout の呼び出し元 NPC と、その町のワールドマップの行き先。2026-10-25 に decompiled と
        // tools/script-analysis/out/ops.txt・maps.txt から起こした)。店は 0..15 だけ使われている (16..31 は呼ばれない)。
        // 「今行ける」= 地方の旗 (get_flag_atai(4 + 地方/4) の bit 地方%4) と町の旗 ([Flag] & [Mask]) が立っていて、
        // 店主の NPC が出ている (maps.txt の fN=lo..hi) こと。Core.select_chiikimap / area_name_tbl / world_icon_tbl と同じ判定。
        private sealed class ShopPlace { public int Shop, Region, Flag, Mask, NpcFlag, NpcLo, NpcHi; public string Town; }
        private static readonly ShopPlace[] ShopPlaces =
        {
            new ShopPlace { Shop = 0,  Town = "アバロン",   Region = 0,  Flag = 8,  Mask = 1, NpcFlag = 128, NpcLo = 0, NpcHi = 0 },
            new ShopPlace { Shop = 1,  Town = "ソーモン",   Region = 0,  Flag = 8,  Mask = 2 },
            new ShopPlace { Shop = 2,  Town = "ニーベル",   Region = 1,  Flag = 10, Mask = 1 },
            new ShopPlace { Shop = 3,  Town = "ダグラス",   Region = 6,  Flag = 20, Mask = 1 },
            new ShopPlace { Shop = 4,  Town = "フォーファー", Region = 6, Flag = 20, Mask = 4 },
            new ShopPlace { Shop = 5,  Town = "ティファール", Region = 2, Flag = 16, Mask = 1 },           // flag156 == 0 だと店が開かない (下で見る)
            new ShopPlace { Shop = 6,  Town = "アバロン",   Region = 0,  Flag = 8,  Mask = 1, NpcFlag = 128, NpcLo = 0, NpcHi = 0 },   // flag190 10..14 は別の話になる
            new ShopPlace { Shop = 7,  Town = "モーベルム", Region = 4,  Flag = 12, Mask = 2 },
            new ShopPlace { Shop = 8,  Town = "モーベルム", Region = 4,  Flag = 12, Mask = 2 },
            new ShopPlace { Shop = 9,  Town = "ミラマー",   Region = 1,  Flag = 11, Mask = 1 },           // 地方 4 の 13&2 からも行ける (下で見る)
            new ShopPlace { Shop = 10, Town = "マイルズ",   Region = 7,  Flag = 22, Mask = 2 },
            new ShopPlace { Shop = 11, Town = "テレルテバ", Region = 8,  Flag = 24, Mask = 4 },
            new ShopPlace { Shop = 12, Town = "ユウヤン",   Region = 11, Flag = 30, Mask = 1, NpcFlag = 35,  NpcLo = 0, NpcHi = 3 },
            new ShopPlace { Shop = 13, Town = "リャンシャン", Region = 11, Flag = 30, Mask = 2, NpcFlag = 35, NpcLo = 0, NpcHi = 1 },
            new ShopPlace { Shop = 14, Town = "ツキジマ",   Region = 12, Flag = 32, Mask = 1, NpcFlag = 173, NpcLo = 0, NpcHi = 13 },
            new ShopPlace { Shop = 15, Town = "ツキジマ",   Region = 12, Flag = 32, Mask = 1, NpcFlag = 173, NpcLo = 0, NpcHi = 13 },
        };

        private static bool RegionOpen(int region) { return (Flag(4 + region / 4) & (1 << (region % 4))) != 0; }

        /// <summary>その店の町に今ワールドマップで行けて、店主が出ているか。</summary>
        private static bool ShopReachable(ShopPlace p)
        {
            bool dest = RegionOpen(p.Region) && (Flag(p.Flag) & p.Mask) != 0;
            if (p.Shop == 9 && !dest) dest = RegionOpen(4) && (Flag(13) & 2) != 0;   // ミラマーは地方 4 からも
            if (!dest) return false;
            if (p.NpcFlag != 0 || p.NpcHi != 0) { int v = Flag(p.NpcFlag); if (v < p.NpcLo || v > p.NpcHi) return false; }
            if (p.Shop == 5 && Flag(156) == 0) return false;
            if (p.Shop == 6) { int v = Flag(190); if (v >= 10 && v <= 14) return false; }
            return true;
        }

        private static bool s_shopLogged;

        /// <summary>今行ける町の店で買える品物 (店の分類の今の段階の品)。</summary>
        private static List<int> ShopItemsNow()
        {
            List<int> list = new List<int>();
            byte[] sd = Data.shop_data, si = Data.shop_item;
            if (sd == null || si == null || Work.partydata == null) return list;
            StringBuilder log = s_shopLogged ? null : new StringBuilder();
            if (log != null)
            {
                // 店ごとの判定の内訳 (調査用。ユーザー 2026-10-25: 店で売っているメイスが「店に無い」と出た)
                StringBuilder d = new StringBuilder("[BattleReview] shops:");
                foreach (ShopPlace p in ShopPlaces)
                    d.Append(' ').Append(p.Shop).Append(ShopReachable(p) ? "+" : "-").Append(p.Town)
                     .Append("(R").Append(p.Region).Append(RegionOpen(p.Region) ? "+" : "-").Append(" f").Append(p.Flag).Append('=').Append(Flag(p.Flag)).Append('&').Append(p.Mask)
                     .Append(p.NpcFlag != 0 || p.NpcHi != 0 ? " n" + p.NpcFlag + "=" + Flag(p.NpcFlag) : "").Append(')');
                d.Append(" f128=").Append(Flag(128)).Append(" f156=").Append(Flag(156)).Append(" f190=").Append(Flag(190)).Append(" tiers192..223=");
                for (int c = 192; c <= 223; c++) d.Append(Flag(c).ToString("X"));
                Plugin.Log.LogInfo(d.ToString());
            }
            foreach (ShopPlace p in ShopPlaces)
            {
                if (!ShopReachable(p)) continue;
                if (log != null) log.Append(' ').Append(p.Shop).Append('=').Append(p.Town);
                for (int i = 0; i < 4; i++)
                {
                    int at0 = p.Shop * 4 + i;
                    if (at0 >= sd.Length) break;
                    int code = sd[at0] & 0xFF;
                    if (code < 192 || code > 223) continue;
                    int tier = Flag(code);
                    int at = (code - 192) * 8 + (tier & 7);
                    if (at >= si.Length) continue;
                    int item = si[at] & 0xFF;
                    if (item < 255 && !list.Contains(item)) list.Add(item);
                }
            }
            if (log != null)
            {
                s_shopLogged = true;
                StringBuilder names = new StringBuilder();
                foreach (int id in list) names.Append(' ').Append(ItemStorage.Name(id));
                Plugin.Log.LogInfo("[BattleReview] reachable shops:" + log + " items=" + list.Count + " map=" + (Work.partydata[0] & 0xFFF) + " |" + names);
            }
            return list;
        }

        /// <summary>手に入る防具の候補: 誰かが装備中 / 持ち物 / 倉庫 / 今買える店。同じ品物は 1 つ (出どころは最初に見つけた順 = 装 > 持 > 倉 > 店)。</summary>
        private static List<ArmorCand> ArmorCandidates()
        {
            List<ArmorCand> list = new List<ArmorCand>();
            Dictionary<int, ArmorCand> seen = new Dictionary<int, ArmorCand>();
            Action<int, string> add = (id, src) =>
            {
                ArmorCand old;
                if (seen.TryGetValue(id, out old)) { if (src == SRC_SHOP) old.InShop = true; return; }
                int part = ArmorPart(id);
                if (part < 0) return;
                ArmorCand c = new ArmorCand { Id = id, Part = part, Src = src, InShop = src == SRC_SHOP };
                seen[id] = c;
                list.Add(c);
            };
            if (Work.memberdata != null)
            {
                int n = PartyWeight.Count();
                for (int m = 0; m < n && m < Work.memberdata.Length; m++)
                {
                    int[] md = Work.memberdata[m];
                    if (md == null || md.Length < 84) continue;
                    for (int s = 81; s <= 83; s++) if (md[s] != 255) add(md[s], SRC_EQUIP);
                }
            }
            foreach (ItemStorage.Entry e in ItemStorage.HeldList()) add(e.Id, SRC_HELD);
            foreach (ItemStorage.Entry e in ItemStorage.StoredList()) add(e.Id, SRC_STORED);
            foreach (int id in ShopItemsNow()) add(id, SRC_SHOP);
            return list;
        }

        // ---------------------------------------------------------------- 装備のヒント (武器)。2026-10-25
        //
        // 通常攻撃の属性: Battle.set_basic_At が武器の系統 (weapon_data[id*4+1] の上位 4 ビット) で技 0..9 を 1 つ決め、その属性は
        //   waza_data[技*10+4] だけで決まる (武器が属性を足すことはない。炎の剣などは weapon_data[+2]/[+3] で別の技を増やすだけ)。
        //   系統 → 技: 0x10 剣 → 0、0x80 大剣 / 0xB0 剣大剣 → 1、0x40 斧 → 2、0x50 棍棒 → 3、0x90 槍 / 0xC0 槍斧 / 0xD0 大剣槍 → 4、
        //   0x20 小剣 / 0x30 剣小剣 → 5、0xA0 弓 → 6、0xE0 爪 → 7。それ以外 (傷薬など) は武器ではない。
        //   表の中身 (斬 斬 打 打 突 突 射 斬) は実行時に waza_data から読む (決め打ちしない)。
        // 技能 (Skills のビット): 1 剣、2 小剣、4 斧、8 棍棒、16 大剣、32 槍、64 弓、128 爪 (今は表示に使っていない。ユーザー 2026-10-25: 種類ではなく属性で)。

        private sealed class WeaponCand { public int Id, Power, Skills, Attr; public string Src; public bool InShop; }   // InShop は ArmorCand と同じ

        /// <summary>武器の系統 (上位 4 ビット) → 通常攻撃の技 0..9。武器でなければ -1。</summary>
        private static int PlainTech(int kind)
        {
            switch (kind & 0xF0)
            {
                case 0x10: return 0;
                case 0x80: case 0xB0: return 1;
                case 0x40: return 2;
                case 0x50: return 3;
                case 0x90: case 0xC0: case 0xD0: return 4;
                case 0x20: case 0x30: return 5;
                case 0xA0: return 6;
                case 0xE0: return 7;
            }
            return -1;
        }

        private static int SkillsOf(int kind)
        {
            switch (kind & 0xF0)
            {
                case 0x10: return 1;
                case 0x20: return 2;
                case 0x30: return 1 | 2;
                case 0x40: return 4;
                case 0x50: return 8;
                case 0x80: return 16;
                case 0x90: return 32;
                case 0xA0: return 64;
                case 0xB0: return 1 | 16;
                case 0xC0: return 32 | 4;
                case 0xD0: return 16 | 32;
                case 0xE0: return 128;
            }
            return 0;
        }

        private static WeaponCand MakeWeapon(int id, string src)
        {
            byte[] wd = Data.weapon_data;
            if (id < 0 || id >= 96 || wd == null || id * 4 + 1 >= wd.Length) return null;
            int kind = wd[id * 4 + 1] & 0xFF;
            int tech = PlainTech(kind);
            if (tech < 0) return null;
            return new WeaponCand { Id = id, Power = wd[id * 4] & 0xFF, Skills = SkillsOf(kind), Attr = TechAttr(tech), Src = src };
        }

        /// <summary>手に入る武器の候補 (装備中 / 持ち物 / 倉庫 / 今買える店。同じ品物は 1 つ)。</summary>
        private static List<WeaponCand> WeaponCandidates()
        {
            List<WeaponCand> list = new List<WeaponCand>();
            Dictionary<int, WeaponCand> seen = new Dictionary<int, WeaponCand>();
            Action<int, string> add = (id, src) =>
            {
                WeaponCand old;
                if (seen.TryGetValue(id, out old)) { if (src == SRC_SHOP) old.InShop = true; return; }
                WeaponCand w = MakeWeapon(id, src);
                if (w == null) return;
                w.InShop = src == SRC_SHOP;
                seen[id] = w;
                list.Add(w);
            };
            if (Work.memberdata != null)
            {
                int n = PartyWeight.Count();
                for (int m = 0; m < n && m < Work.memberdata.Length; m++)
                {
                    int[] md = Work.memberdata[m];
                    if (md == null || md.Length < 81) continue;
                    for (int s = 77; s <= 80; s++) if (md[s] != 255) add(md[s], SRC_EQUIP);
                }
            }
            foreach (ItemStorage.Entry e in ItemStorage.HeldList()) add(e.Id, SRC_HELD);
            foreach (ItemStorage.Entry e in ItemStorage.StoredList()) add(e.Id, SRC_STORED);
            foreach (int id in ShopItemsNow()) add(id, SRC_SHOP);
            return list;
        }

        private sealed class AttrRank { public int Index, Weak, Normal; public List<Enemy> Weaks = new List<Enemy>(), Normals = new List<Enemy>(); }

        /// <summary>
        /// 属性ごとに、それが ◎ (Weaks) / ○ (Normals) の敵。mask = 見る属性 (0xF0 物理、0x0F 術系、0xFF 全部)。
        /// 並びは ◎ の敵の数 (体数) が多い順 → ○ が多い順。どの敵にも ○ 以上が付かない属性は入れない。
        /// (前は「その敵の物理 / 術系の中で一番低い属性」を弱点にしていて、全属性 20 の敵にも弱点が出た。ユーザー 2026-10-25: 絶対値で)
        /// </summary>
        private static List<AttrRank> RankAttrs(int mask)
        {
            List<AttrRank> list = new List<AttrRank>();
            for (int k = 0; k < 8; k++)
            {
                if ((mask & (0x80 >> k)) == 0) continue;
                AttrRank r = new AttrRank { Index = k };
                foreach (Enemy e in s_enemies)
                {
                    int rate = Rate(e.Def[k]);
                    if (rate == RATE_WEAK) { r.Weak += e.Count; r.Weaks.Add(e); }
                    else if (rate == RATE_NORMAL) { r.Normal += e.Count; r.Normals.Add(e); }
                }
                if (r.Weak + r.Normal > 0) list.Add(r);
            }
            list.Sort((p, q) => p.Weak != q.Weak ? q.Weak.CompareTo(p.Weak) : p.Normal != q.Normal ? q.Normal.CompareTo(p.Normal) : p.Index.CompareTo(q.Index));
            return list;
        }

        /// <summary>全部の属性が △ 以下の敵 (弱点なし。無理に一番低い属性を選ばない)。</summary>
        private static List<Enemy> NoWeakEnemies()
        {
            List<Enemy> list = new List<Enemy>();
            foreach (Enemy e in s_enemies)
            {
                bool any = false;
                for (int k = 0; k < 8 && !any; k++) any = Rate(e.Def[k]) <= RATE_NORMAL;
                if (!any) list.Add(e);
            }
            return list;
        }

        /// <summary>「◎ドビー  ○ガーゴイル」の並び (◎ は赤、○ は黒)。</summary>
        private static List<KeyValuePair<string, GUIStyle>> EnemyMarks(AttrRank r)
        {
            List<KeyValuePair<string, GUIStyle>> segs = new List<KeyValuePair<string, GUIStyle>>();
            foreach (Enemy e in r.Weaks) segs.Add(new KeyValuePair<string, GUIStyle>(RateMarks[RATE_WEAK] + e.Name + "  ", s_redLeft));
            foreach (Enemy e in r.Normals) segs.Add(new KeyValuePair<string, GUIStyle>(RateMarks[RATE_NORMAL] + e.Name + "  ", s_line));
            return segs;
        }

        /// <summary>色の違う文字列を左から並べて描く。幅を超える分は描かない。</summary>
        private static void DrawSegs(float x, float y, float w, float h, List<KeyValuePair<string, GUIStyle>> segs)
        {
            float right = x + w;
            foreach (KeyValuePair<string, GUIStyle> s in segs)
            {
                float tw = TextW(s.Key, s.Value);
                if (x + tw > right) break;
                Txt(new Rect(x, y, tw + 4f, h), s.Key, s.Value);
                x += tw;
            }
        }

        /// <summary>属性の印 [突] [熱] (武器 / 術の名前の前に。ユーザー 2026-10-25: 剣や槍の種類ではなく属性で言ってほしい)。</summary>
        private static string AttrLabel(int bits) { return "[" + AttrText(bits) + "]"; }

        // ---------------------------------------------------------------- 術のヒント (術系の弱点に対して。ユーザー 2026-10-25)
        //
        // 術 = 技 201..254 (201..230 が 6 系統 × 5 の基本の術、231..254 が合成術 2 つ × 12 組)。系統は waza_data[+8] の 0x20 火 0x40 水 0x80 風、
        // [+9] の 0x01 地 0x02 天 0x04 冥。対象は [+5] の下位 6 ビット (2 = 敵 1 体、14 = 敵 1 体 + 周り、50 = 敵全体。それ以外は味方向け)。威力は [+2]。
        // 覚えられる術 = パーティ共通の旗 partydata[257 + id/8] (使える) / [321 + id/8] (研究済み、世代が進むと使える旗に写る = 研究中)。
        // 基本の術は系統のレベル partydata[390 + 系統] が Data.jyutsu_flag_data[系統*10 + j*2] (レベル, +1 = 術) に届くと旗が立つ (Battle.set_jyutuflag)。
        // 合成術は研究所で研究 (組の 1 つ目は両系統 Lv15、2 つ目は Lv25、50 万クラウン。msg 1012 / 1013)。
        // 術法研究所 (アバロンの開発、100 万、msg 745): flag132 = 13 提案中、14 建設中、15 完成 (年が進む msg 2061 が 14 → 15)。無いと術は一切覚えられない。
        //   (最初は 14 を完成と見ていて、建っているのに「建てると」が出た。ユーザー 2026-10-25)
        // 誰がどの系統を覚えるかはユーザーの判断 (ここでは見ない)。

        private static readonly string[] SchoolNames = { "火", "水", "風", "地", "天", "冥" };   // partydata[390 + i] と jyutsu_flag_data の順

        private sealed class SpellCand { public int Id, Power, Rank; public string Status; }   // Rank: 0 = 習得可、1 = 研究中、2 = まだ

        private static bool PartyBit(int baseIdx, int id)
        {
            int[] pd = Work.partydata;
            int i = baseIdx + id / 8;
            return pd != null && i < pd.Length && (pd[i] & (128 >> (id & 7))) != 0;
        }

        /// <summary>術の系統のビット (SchoolNames の添字のビット、bit i = 系統 i)。</summary>
        private static int SpellSchools(int id)
        {
            byte[] w = Data.waza_data;
            if (w == null || id * 10 + 9 >= w.Length) return 0;
            int b8 = w[id * 10 + 8] & 0xFF, b9 = w[id * 10 + 9] & 0xFF, s = 0;
            if ((b8 & 0x20) != 0) s |= 1;
            if ((b8 & 0x40) != 0) s |= 2;
            if ((b8 & 0x80) != 0) s |= 4;
            if ((b9 & 0x01) != 0) s |= 8;
            if ((b9 & 0x02) != 0) s |= 16;
            if ((b9 & 0x04) != 0) s |= 32;
            return s;
        }

        private static string SchoolText(int bits)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 6; i++) if ((bits & (1 << i)) != 0) sb.Append(SchoolNames[i]);
            return sb.ToString();
        }

        /// <summary>基本の術が解禁される系統のレベル (jyutsu_flag_data から)。見つからなければ -1。</summary>
        private static int UnlockLevel(int id, out int school)
        {
            school = -1;
            byte[] t = Data.jyutsu_flag_data;
            if (t == null) return -1;
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 5; j++)
                {
                    int at = i * 10 + j * 2;
                    if (at + 1 >= t.Length) return -1;
                    if ((t[at + 1] & 0xFF) == id) { school = i; return t[at] & 0xFF; }
                }
            return -1;
        }

        private static int SchoolLevel(int school)
        {
            int[] pd = Work.partydata;
            return (pd != null && school >= 0 && 390 + school < pd.Length) ? pd[390 + school] : 0;
        }

        /// <summary>術系の弱点 weak (熱冷雷状のビット) を突ける攻撃の術と、その手に入れ方。習得できるものを先に、あとは威力の高い順。</summary>
        private static List<SpellCand> SpellCandidates(int weak)
        {
            List<SpellCand> list = new List<SpellCand>();
            byte[] w = Data.waza_data;
            if (w == null) return list;
            for (int id = 201; id <= 254; id++)
            {
                if (id * 10 + 9 >= w.Length) break;
                if ((TechAttr(id) & weak) == 0) continue;
                int tgt = w[id * 10 + 5] & 0x3F;
                if (tgt != 2 && tgt != 14 && tgt != 50) continue;   // 味方向け (回復・補助) は出さない
                SpellCand c = new SpellCand { Id = id, Power = w[id * 10 + 2] & 0xFF };
                // 冥術は古代魔術書の件が済む (f131 >= 8。研究所の冥の部屋 map 437 の NPC の表示条件) まで教えてもらえない (ユーザー 2026-10-25: ホラーが習得可と出た)
                bool abyssLocked = (SpellSchools(id) & 32) != 0 && Flag(131) < 8;
                if (abyssLocked) { c.Rank = 2; c.Status = "冥術はまだ教えてもらえない"; }
                else if (PartyBit(257, id)) { c.Rank = 0; c.Status = "習得可"; }
                else if (PartyBit(321, id)) { c.Rank = 1; c.Status = "研究中"; }
                else if (id <= 230)
                {
                    int school; int lv = UnlockLevel(id, out school);
                    c.Rank = 2;
                    c.Status = lv < 0 ? "まだ" : SchoolNames[school] + " Lv" + lv + " で (今 " + SchoolLevel(school) + ")";
                }
                else
                {
                    int need = ((id - 231) & 1) == 0 ? 15 : 25;
                    c.Rank = 2;
                    c.Status = "研究 (" + SchoolText(SpellSchools(id)) + " Lv" + need + ")";
                }
                list.Add(c);
            }
            list.Sort((p, q) => p.Rank != q.Rank ? p.Rank.CompareTo(q.Rank) : q.Power.CompareTo(p.Power));
            return list;
        }

        // ---------------------------------------------------------------- 装備のヒントの 2 ページ (表)。2026-10-25
        //
        // 「この属性の最強は何で、どこにあって、何個持っているか」の表。人数分 (PartyWeight.Count) は注釈の目安だけ。
        // 誰に持たせるかは見ない (ユーザーの判断)。所持金は出さない (ユーザー 2026-10-25)。値段・不足・買い足しの合計も出さない
        // (ユーザー 2026-10-28: 何がどこにあって何個持っているかが分かれば十分。列が多いと字がかぶる)。

        private static string Num(int n) { return n.ToString("#,0"); }

        /// <summary>条件に合う品物の ID → 今持っている個数 (装備中の枠 77..83 + 持ち物 + 倉庫)。</summary>
        private static Dictionary<int, int> OwnedList(Predicate<int> pred)
        {
            Dictionary<int, int> d = new Dictionary<int, int>();
            if (Work.memberdata != null)
            {
                int members = PartyWeight.Count();
                for (int m = 0; m < members && m < Work.memberdata.Length; m++)
                {
                    int[] md = Work.memberdata[m];
                    if (md == null || md.Length < 84) continue;
                    for (int s = 77; s <= 83; s++) { int id = md[s]; if (id != 255 && pred(id)) d[id] = (d.ContainsKey(id) ? d[id] : 0) + 1; }
                }
            }
            foreach (ItemStorage.Entry e in ItemStorage.HeldList()) if (pred(e.Id)) d[e.Id] = (d.ContainsKey(e.Id) ? d[e.Id] : 0) + e.Held;
            foreach (ItemStorage.Entry e in ItemStorage.StoredList()) if (pred(e.Id)) d[e.Id] = (d.ContainsKey(e.Id) ? d[e.Id] : 0) + e.Stored;
            return d;
        }

        private static int CountOf(Dictionary<int, int> owned) { int n = 0; foreach (int v in owned.Values) n += v; return n; }

        /// <summary>品物 1 種の今持っている個数 (装備中 + 持ち物 + 倉庫)。</summary>
        private static int OwnedCount(int id) { return id < 0 ? 0 : CountOf(OwnedList(x => x == id)); }

        // 表の列: 名前 (属性を [ ] で先頭に) / 店 (今行ける店で売っていれば ○、無ければ ×) / 手持ち / 効く敵・受けた量 (/ 誰から)。名前の列は残り全部
        // (ユーザー 2026-10-25: 何の武器か分かるように名前を先頭に。2026-10-28: 不足・買うなら・合計の列は外し、店の有無だけ ○× で。列が多いと字がかぶる)
        private sealed class Tbl { public float X, W, LineH, HeadY, NameW, ShopX, ShopW, HaveX, HaveW, MidX, MidW, Mid2X, Mid2W; }

        /// <summary>列の幅 (fontSize の倍数)。mid = 効く敵 / 受けた量、mid2 = 誰から (0 なら無し)。名前は残りの幅。</summary>
        private static Tbl MakeTbl(float cx, float cw, float lineH, int fontSize, float midEm, float mid2Em)
        {
            Tbl t = new Tbl { X = cx, W = cw, LineH = lineH };
            t.ShopW = Mathf.Round(fontSize * 2.4f); t.HaveW = Mathf.Round(fontSize * 3.4f); t.MidW = Mathf.Round(fontSize * midEm); t.Mid2W = Mathf.Round(fontSize * mid2Em);
            float gapW = Mathf.Round(fontSize * 0.8f);
            t.NameW = cw - (t.ShopW + t.HaveW + gapW + t.MidW + t.Mid2W);
            t.ShopX = cx + t.NameW; t.HaveX = t.ShopX + t.ShopW; t.MidX = t.HaveX + t.HaveW + gapW; t.Mid2X = t.MidX + t.MidW;
            return t;
        }

        private static void TblHeader(Tbl t, float y, string kind, string mid, string mid2)
        {
            t.HeadY = y;
            Txt(new Rect(t.X, y, t.NameW, t.LineH), kind, s_head);
            Txt(new Rect(t.ShopX, y, t.ShopW, t.LineH), "店", s_cellDim);
            Txt(new Rect(t.HaveX, y, t.HaveW, t.LineH), "手持ち", s_rightDim);
            Txt(new Rect(t.MidX, y, t.MidW, t.LineH), mid, s_dim);
            if (t.Mid2W > 0f) Txt(new Rect(t.Mid2X, y, t.Mid2W, t.LineH), mid2, s_dim);
        }

        /// <summary>1 行: 名前、店 (0 = ×、1 = ○、それ以外 = 出さない。術の行)、手持ち (have &lt; 0 = 数えないので ―)。○× は本体フォントに無いのでテクスチャ (TxtMarks)。</summary>
        private static void TblRow(Tbl t, float y, string name, GUIStyle nst, int shop, int have)
        {
            Txt(new Rect(t.X, y, t.NameW, t.LineH), name, nst);
            if (shop == 1) Txt(new Rect(t.ShopX, y, t.ShopW, t.LineH), "○", s_cell);
            else if (shop == 0) Txt(new Rect(t.ShopX, y, t.ShopW, t.LineH), "×", s_cellDim);
            Txt(new Rect(t.HaveX, y, t.HaveW, t.LineH), have < 0 ? "―" : have.ToString(), have > 0 ? s_right : s_rightDim);
        }

        // 名前が店にしか無い行の 2 行目 (ユーザー 2026-10-28: 買わなくても戦えるかの判断用に、今持っている中で一番強い物を添える)
        private const string SUB_SHOP = "今行ける店では: ", SUB_SHOP_NONE = "今行ける店にはこの属性の物は無い";   // 店が × の行 (ユーザー 2026-10-28: 店で買える一番良い物も知りたい)
        private const string SUB_OWN = "今持っている中では: ", SUB_NONE = "今持っている中にこの属性の物は無い";

        /// <summary>行の下に薄く 1 行 (少し字下げ)。表の枠からはみ出す分は Txt が描かない。</summary>
        private static float DrawSubLine(float cx, float cy, float cw, float lineH, int fontSize, string text)
        {
            Txt(new Rect(cx + fontSize, cy, cw - fontSize, lineH), text, s_dim);
            return cy + lineH;
        }

        /// <summary>表の本体の始まり: 枠 (top..bottom) の外は Txt が描かない。戻り値はスクロール分だけ上にずらした y。</summary>
        private static float BeginTable(float top, float bottom, float lineH)
        {
            s_clip = true; s_clipTop = top; s_clipBottom = bottom;
            return top - s_scroll * lineH;
        }

        /// <summary>表の本体の終わり: 内容の高さから送れる行数を出し、見出しの行の右端に ▲▼ の印を描く。戻り値は枠の下端。</summary>
        private static float EndTable(Tbl t, float cy, float tableBottom, float lineH)
        {
            s_clip = false;
            float over = cy + s_scroll * lineH - tableBottom;   // 送らない時の内容の下端が枠をどれだけ超えるか
            s_maxScroll = over > 0.5f ? Mathf.CeilToInt(over / lineH) : 0;
            if (s_scroll > s_maxScroll) s_scroll = s_maxScroll;
            string marks = (s_scroll > 0 ? "▲" : "") + (s_scroll < s_maxScroll ? "▼" : "");
            if (marks.Length > 0) Txt(new Rect(t.X, t.HeadY, t.W, t.LineH), marks, s_rightDim);
            return tableBottom;
        }

        /// <summary>
        /// 装備のヒント (武器と術) のページ。◎ の敵がいる属性ごとに 1 行 (◎ の敵が多い順。◎ がどこにも無ければ ○ の属性)。
        /// 物理: 名前 = 揃える武器 (足りていれば持っている中で一番強いもの、足りなければ今行ける店で買える一番強いもの)、手持ち = その属性で通常攻撃できる武器の数。
        /// 術系: 名前 = 研究所で今覚えられる一番強い攻撃の術 (研究所が無ければ建つと覚えられる基本の術と、建てる助言)。手持ち / 不足は数えない (人数分を買う物ではない)。
        /// 手に入る武器も覚えられる術も無い属性は出さない (ユーザー 2026-10-25: 手が無いのに敵の名前を並べても要らない)。
        /// </summary>
        private static void DrawWeaponTablePage(float cx, float cy, float cw, float lineH, int fontSize, float bottom)
        {
            if (s_enemies.Count == 0) { Txt(new Rect(cx, cy, cw, lineH), "敵の記録がありません", s_dim); return; }
            int members = PartyWeight.Count();
            Tbl t = MakeTbl(cx, cw, lineH, fontSize, 13f, 0f);
            List<WeaponCand> wc = WeaponCandidates();
            List<AttrRank> ranks = RankAttrs(0xFF);
            bool anyWeak = false;
            foreach (AttrRank r in ranks) if (r.Weak > 0) anyWeak = true;
            float tableBottom = bottom - lineH * 4.4f;   // 下に説明 4 行 (0.9 行 × 4 + 余白) を残す
            TblHeader(t, cy, "武器 / 術", "効く敵", null); cy += lineH;
            cy = BeginTable(cy, tableBottom, lineH);
            int shown = 0, f = Flag(132);
            // 1 周目は武器 (物理の属性)、2 周目は術 (術系の属性)。それぞれの中は ◎ の敵が多い順 (ユーザー 2026-10-25: 武器と術でまとめる)
            for (int pass = 0; pass < 2; pass++)
            foreach (AttrRank r in ranks)
            {
                if ((r.Index < 4) != (pass == 0) || (anyWeak && r.Weak == 0)) continue;
                int bit = 0x80 >> r.Index;
                string sub = null;
                if (r.Index < 4)
                {
                    // 名前 = 手に入る中 (手持ち + 倉庫 + 今行ける店) で一番強い武器。同じ威力なら持っている方。店 = 今行ける店で売っているか (○ / ×)。
                    // 手持ち = その品そのものの個数 (ユーザー 2026-10-28。属性の合計だと「パワードスーツ 6」のように読めて紛らわしい)。
                    // 店にしか無い時だけ 2 行目に「今持っている中では」一番強い武器を薄く添える (買わなくても戦えるかの判断用。折衷案)
                    WeaponCand best = null, own = null, shopBest = null;
                    foreach (WeaponCand w in wc)
                    {
                        if ((w.Attr & bit) == 0) continue;
                        bool owned = w.Src != SRC_SHOP;
                        if (best == null || w.Power > best.Power || (w.Power == best.Power && owned && best.Src == SRC_SHOP)) best = w;
                        if (owned && (own == null || w.Power > own.Power)) own = w;
                        if (w.InShop && (shopBest == null || w.Power > shopBest.Power)) shopBest = w;
                    }
                    if (best == null) continue;
                    TblRow(t, cy, AttrLabel(bit) + ItemStorage.Name(best.Id) + " 威" + best.Power, s_line, best.InShop ? 1 : 0, OwnedCount(best.Id));
                    // 店が × なら 2 行目に今行ける店で買える一番強い物 (無ければ無いと)
                    if (!best.InShop) sub = shopBest != null ? SUB_SHOP + ItemStorage.Name(shopBest.Id) + " 威" + shopBest.Power : SUB_SHOP_NONE;
                    else if (best.Src == SRC_SHOP) sub = own != null ? SUB_OWN + ItemStorage.Name(own.Id) + " 威" + own.Power + " × " + OwnedCount(own.Id) : SUB_NONE;
                }
                else
                {
                    // 研究所が完成 (15) なら今覚えられる一番強い術。建設中 (14) / 未建設なら、建つと覚えられる基本の術の一番強いもの (薄く)
                    SpellCand pick = null;
                    foreach (SpellCand c in SpellCandidates(bit)) if (f >= 15 ? c.Rank == 0 : c.Id <= 230) { pick = c; break; }
                    if (pick == null) continue;
                    // 名前の後ろに (研究所 = 今覚えられる / 建設中 / 未建設 = 建つと覚えられる)。店・手持ちの列は出さない (ユーザー 2026-10-28)
                    string how = f >= 15 ? "研究所" : (f == 14 ? "研究所 建設中" : "研究所 未建設");
                    TblRow(t, cy, AttrLabel(bit) + TechName(pick.Id, -1, true) + " 威" + pick.Power + " (" + how + ")", f >= 15 ? s_line : s_dim, -1, -1);
                }
                DrawSegs(t.MidX, cy, t.MidW, lineH, EnemyMarks(r));
                cy += lineH;
                shown++;
                // 店にしか無い時だけ、その行の下に「今持っている中では」を薄く 1 行 (ユーザー 2026-10-28)
                if (sub != null) cy = DrawSubLine(cx, cy, cw, lineH, fontSize, sub);
            }
            if (shown == 0) { Txt(new Rect(cx, cy, cw, lineH), "○ 以上の属性に、手に入る武器も覚えられる術も無い", s_dim); cy += lineH; }
            List<Enemy> none = NoWeakEnemies();
            if (none.Count > 0)
            {
                StringBuilder sb = new StringBuilder("弱点なし (全部 △ 以下): ");
                for (int i = 0; i < none.Count; i++) sb.Append(i > 0 ? "・" : "").Append(none[i].Name);
                Txt(new Rect(cx, cy, cw, lineH), sb.ToString(), s_dim);
                cy += lineH;
            }
            cy = EndTable(t, cy, tableBottom, lineH);
            cy += Mathf.Round(lineH * 0.3f);
            float hintH = Mathf.Round(lineH * 0.9f);
            // 注釈は 1 行ずつ 1 行に収まる長さ (1600×900 で約 50 字。2026-10-28 の撮影で 3 行目が切れていた)
            cy = DrawWrapped(cx, cy, cw, hintH, "武器 = [ ] の属性で通常攻撃できる、手に入る中で一番強いもの。威 = 威力。店 = 今行ける店にあるか。", s_hint, bottom);
            cy = DrawWrapped(cx, cy, cw, hintH, "術 = 研究所で今覚えられる一番強い攻撃の術 (未建設 / 建設中なら建つと覚えられる術)。", s_hint, bottom);
            cy = DrawWrapped(cx, cy, cw, hintH, "手持ち = その品の数 (装備中 + 持ち物 + 倉庫。パーティ " + members + " 人)。◎ よく効く (弱点)  ○ 普通に効く", s_hint, bottom);
            cy = DrawWrapped(cx, cy, cw, hintH, "2 行目 = 店に無い時は今行ける店で一番強い物、手持ち 0 の時は今持っている中で一番強い物。", s_hint, bottom);
        }

        /// <summary>
        /// 装備のヒント (防具) のページ。敵が使った属性ごとに 1 行 (受けた HP のダメージの多い順)。名前 = 揃える防具 (足りていれば持っている中で
        /// その属性の防御力が一番高いもの、足りなければ今行ける店で買える一番高いもの。部位も出す)、手持ち = その属性の防御力が持っている中で一番高い値の
        /// その品の個数 (属性の合計ではない。ユーザー 2026-10-28)、受けた量 = 被ダメージと使った敵 (HP も LP も減らない技は技の名前)。
        /// (ユーザー 2026-10-25: 防具は武器と別のページに。部位ごとの表 (旧 5 ページ目) は無くす)
        /// </summary>
        private static void DrawArmorTablePage(float cx, float cy, float cw, float lineH, int fontSize, float bottom)
        {
            if (s_enemies.Count == 0) { Txt(new Rect(cx, cy, cw, lineH), "敵の記録がありません", s_dim); return; }
            int members = PartyWeight.Count();
            Tbl t = MakeTbl(cx, cw, lineH, fontSize, 5.5f, 7.5f);   // 受けた量 / 誰から を別の列に (ユーザー 2026-10-25)
            List<AttrCol> used = UsedAttrs();
            List<ArmorCand> ac = ArmorCandidates();
            float tableBottom = bottom - lineH * 4.4f;
            TblHeader(t, cy, "防具", "受けた量", "誰から"); cy += lineH;
            cy = BeginTable(cy, tableBottom, lineH);
            if (used.Count == 0) { Txt(new Rect(cx, cy, cw, lineH), "敵の技の属性の記録がない", s_dim); cy += lineH; }
            foreach (AttrCol a in used)
            {
                int k = a.Index;
                // 名前 = 手に入る中でこの属性の防御力が一番高い防具 (部位は問わない。同じなら持っている方)。店 / 手持ち / 2 行目は武器のページと同じ
                ArmorCand best = null, own = null, shopBest = null; int bestV = 0, ownV = 0, shopV = 0;
                foreach (ArmorCand c in ac)
                {
                    int v = ArmorDef(c.Id, k);
                    if (v <= 0) continue;
                    bool owned = c.Src != SRC_SHOP;
                    if (best == null || v > bestV || (v == bestV && owned && best.Src == SRC_SHOP)) { bestV = v; best = c; }
                    if (owned && v > ownV) { ownV = v; own = c; }
                    if (c.InShop && v > shopV) { shopV = v; shopBest = c; }
                }
                string name = "[" + TypeNames[k] + "]" + (best != null ? ItemStorage.Name(best.Id) + " (" + PartNames[best.Part] + ") 防" + bestV : " 防ぐ防具が手に入らない");
                TblRow(t, cy, name, best != null ? s_line : s_dim, best == null ? -1 : (best.InShop ? 1 : 0), best != null ? OwnedCount(best.Id) : -1);
                Txt(new Rect(t.MidX, cy, t.MidW, lineH), TakenText(a, true), s_dim);
                // 誰から = 敵 1 体ずつ 1 行に (2〜3 体を「・」でつなぐと右にはみ出す。ユーザー 2026-10-28)。2 体目からは名前だけの行
                for (int i = 0; i < a.Enemies.Count; i++)
                {
                    Txt(new Rect(t.Mid2X, cy, t.Mid2W, lineH), a.Enemies[i], s_dim);
                    cy += lineH;
                }
                if (a.Enemies.Count == 0) cy += lineH;
                if (best != null && !best.InShop) cy = DrawSubLine(cx, cy, cw, lineH, fontSize, shopBest != null ? SUB_SHOP + ItemStorage.Name(shopBest.Id) + " (" + PartNames[shopBest.Part] + ") 防" + shopV : SUB_SHOP_NONE);
                else if (best != null && best.Src == SRC_SHOP) cy = DrawSubLine(cx, cy, cw, lineH, fontSize, own != null ? SUB_OWN + ItemStorage.Name(own.Id) + " (" + PartNames[own.Part] + ") 防" + ownV + " × " + OwnedCount(own.Id) : SUB_NONE);
            }
            cy = EndTable(t, cy, tableBottom, lineH);
            cy += Mathf.Round(lineH * 0.3f);
            float hintH = Mathf.Round(lineH * 0.9f);
            cy = DrawWrapped(cx, cy, cw, hintH, "防具 = [ ] の属性の防御力 (防) が手に入る中で一番高いもの (部位は問わない)。店 = 今行ける店にあるか。", s_hint, bottom);
            cy = DrawWrapped(cx, cy, cw, hintH, "受けた量: 被 = 受けた HP のダメージの合計。HP の減らない技は技の名前。", s_hint, bottom);
            cy = DrawWrapped(cx, cy, cw, hintH, "手持ち = その品の数 (装備中 + 持ち物 + 倉庫。パーティ " + members + " 人)。", s_hint, bottom);
            cy = DrawWrapped(cx, cy, cw, hintH, "2 行目 = 店に無い時は今行ける店で一番強い物、手持ち 0 の時は今持っている中で一番強い物。", s_hint, bottom);
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
            MeasureCenters(fontSize);
            s_leftStyles.Clear();
            s_box = new GUIStyle(GUI.skin.box);
            s_box.normal.background = Solid(new Color(0.18f, 0.18f, 0.2f, 0.94f));
            s_line = new GUIStyle(GUI.skin.label);
            s_line.fontSize = fontSize;
            s_line.alignment = TextAnchor.MiddleLeft;
            s_line.wordWrap = false;
            s_line.normal.textColor = new Color(0.12f, 0.08f, 0.05f);
            if (s_font != null) s_line.font = s_font;
            s_dim = new GUIStyle(s_line);
            s_dim.normal.textColor = UiParts.DimPaper;   // 薄い灰色は読みにくい (ユーザー 2026-10-20、2026-10-24 にさらに濃く) → 共通の色
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
            s_redLeft = new GUIStyle(s_red);
            s_redLeft.alignment = TextAnchor.MiddleLeft;
            s_redRightPlain = new GUIStyle(s_redRight);
            s_redRightPlain.fontStyle = FontStyle.Normal;
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
