using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 仲間にするかの 2 択 (パーティーに入れる / 入れない) に 3 つ目「ステータスを見る」を足す ([Recruit] StatusOption、既定 On)。
    /// 選ぶと候補の絵・名前・クラス・6 つの能力・武器 / 術の適性と加入時のレベルの見込み・HP の見込み・よくひらめく技の系統を
    /// 羊皮紙の窓に出し、決定 / キャンセル / Esc / クリックで閉じると 2 択に戻る。スマホ移植の UI は仲間にする前に何も見せてくれない。
    ///
    /// 本体の仕組み:
    /// - 選択肢は script の flash_select → select_pos → 文字 (put_moji2 が select_work[i] に fonttbl の添字で写す) → select_sa (op 39)。
    ///   select_sa は決定まで mess_adrs が進まないので毎論理フレーム呼ばれ、put_select が行の描画と上下 / 決定を処理する。
    ///   select_adrs[i] = cursor_y + wind_which * 4096 (窓内の y と窓番号)。決定で select_now = 行番号、increment_adrs() で次の op
    ///   (jump_of_sepa / call_of_sepa) が select_now で分岐する。call_of_sepa は select_count*2+1 だけ mess_adrs を進めるので、
    ///   決定の瞬間までに select_count を 2 に戻しておかないと script の読み位置がずれる。
    /// - 候補の id は呼び出し側 script が koukan_work[10..11] に入れている (名前を出す put_npc_name(10) と同じ値)。
    ///   msg 1423 / 1580 / 2839 は選んだ後に入れるので、そこでは 3 行目を出さない。
    /// - 加入時の能力は Menu.npc_data_set / snpc_data_set / player_skil_set / _npc_hp_set を写した (値の意味はそちらのコメント参照)。
    /// </summary>
    internal static class RecruitInfo
    {
        public static ConfigEntry<bool> Enabled;

        private const string OPTION_TEXT = "ステータスを見る";
        private const string OPTION_TEXT_ALT = "能力を見る";
        private const string JOIN_TEXT = "パーティーに入れる";
        private const int ROW = 2;                       // 足す行の番号
        private const int ROW_H = 16;                    // 本体の選択肢の行間 (ドット)
        private const int BTN_OK = 0x100000, BTN_CANCEL = 0x400000;
        private const int DECIDE_BITS = 0x100001;        // put_select の決定判定と同じ (OK ボタン + タッチの決定)
        private static readonly int[] SKIP_MESSAGES = { 1423, 1580, 2839 };
        // memberdata[128..133] の順 (実機で加入後のステータス画面と突き合わせ 2026-10-18: 体力は 133、HP の計算 (_npc_hp_set) も 133 を使う)
        private static readonly string[] STAT_NAMES = { "腕力", "器用さ", "魔力", "理力", "素早さ", "体力" };
        // レーダーチャートの頂点の並び (上から時計回り): 腕力 → 器用さ → 魔力 → 素早さ → 体力 → 理力 (本体のステータス画面の並び + 理力)
        private static readonly int[] RADAR_ORDER = { 0, 1, 2, 4, 5, 3 };
        private static readonly string[] WEAPON_NAMES = { "剣", "斧", "槍", "弓", "体術" };      // レベルの枠 0..4 (剣+大剣 / 斧+棍棒 / 槍+小剣 / 弓 / 体術+爪)
        private static readonly string[] MAGIC_NAMES = { "火", "水", "風", "地", "天", "冥" };     // レベルの枠 5..10

        private static int[] s_optionCodes;              // 「ステータスを見る」の fonttbl 添字 (0 終端)
        private static bool s_failed;                    // 例外が出たら再起動まで止める (cfg は触らない)
        private static bool s_rowActive;                 // 今の選択肢に 3 行目を足してある
        private static bool s_open;                      // 窓が開いている (put_select を止めて script を待たせる)
        private static bool s_guard;                     // 閉じた直後: 決定 / キャンセルを離すまでゲームに入力を渡さない
        private static int s_prevKey = -1;
        private static int s_lastEscFrame = -1;
        private static int s_loggedMsg = -1;
        private static Core s_core;
        private static Info s_info;

        public static bool IsOpen { get { return s_open; } }

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Recruit", "StatusOption", true,
                "仲間にするかの 2 択 (パーティーに入れる / 入れない) に 3 つ目「ステータスを見る」を足す。選ぶと候補の絵・クラス・能力・武器 / 術の適性と加入時のレベルの見込み・よくひらめく技の系統を窓に出し、閉じると 2 択に戻る。");
            PartyStatus = config.Bind("Recruit", "PartyStatus", true,
                "ステータス画面 (メニュー → ステータス) で決定ボタンを押すと、表示中の仲間を「ステータスを見る」と同じ窓 (能力のレーダーチャート・武器 / 術の今のレベル・よくひらめく技) で見せる。本体はこの画面で決定ボタンに役目が無い。");
            Plugin.Log.LogInfo("[Recruit] StatusOption=" + Enabled.Value + " PartyStatus=" + PartyStatus.Value);
        }

        public static ConfigEntry<bool> PartyStatus;
        /// <summary>今の選択肢に 3 行目を足してある (DialogueCancel はこの間は手を出さない)。</summary>
        public static bool RowActive { get { return s_rowActive; } }
        private static int s_statusFrame = -10;          // ステータス画面の入力処理 (proc_status) が最後に走ったフレーム (入った瞬間の判定用)
        private static int s_statusSince = -10;          // ステータス画面に入ったフレーム (入った時の決定の押下を拾わないため)
        private const int CANCEL_BIT = 0x400000;         // put_select のキャンセル判定と同じ
        private const int DECLINE_ROW = 1;               // 「入れない」の行

        // ---------------------------------------------------------------- 候補の情報

        private sealed class Info
        {
            public int Id;
            public string Name = "";
            public string Class = "";
            public Texture2D Sprite;
            public int[] Stats = new int[6];     // memberdata[128..133] の素質 (表示は +10)
            public int[] Levels = new int[11];   // memberdata[24..34] の加入時の見込み (0 = 適性なし)
            public int Spark;                    // memberdata[103] 閃きタイプ
            public int Hp;                       // 加入時 HP の見込み (乱数の中央)。仲間なら最大 HP
            public bool Member;                  // 仲間になっている人 (ステータス画面から)。レベルは今の値、HP は 今 / 最大
            public int HpNow;                    // 仲間の今の HP
            public string SparkText = "";
            public string Speech = "";           // 選択肢の前にこの人が言っていた台詞 (改行入り、最後の 4 行まで)
            public string[] SpeechLines;         // Speech を吹き出しの幅で折り返した行 (WrapSpeech の結果。幅か字の大きさが変わったら作り直す)
            public float SpeechWrapW;
            public int SpeechFont;
            public Texture2D Bubble;             // 台詞の吹き出し (大きさが決まった時に 1 回描く)
            public int BubbleW, BubbleH;
            public Texture2D[] Frames;           // 「回って構える」のコマ (SPIN_PATS の順)。Sprite はその最初のコマ
            public Texture2D RadarGrid;          // レーダーチャートの網目 (軸と目盛りの六角形)。開いた時に 1 回描く
            public Texture2D RadarFill;          // 能力の六角形 (中心から拡大して「伸びる」動きにする)
        }

        private static Info Build(int id)
        {
            Info f = new Info();
            f.Id = id;
            int obj;
            if (id >= 256 && id < 288)
            {
                // 特別な NPC (snpc_data 43 バイト): 能力もレベルも固定。id 258 だけレベルにパーティのレベルが乗る
                byte[] s = Data.snpc_data;
                int no = (id - 256) * 43;
                if (s == null || no + 43 > s.Length) return null;
                obj = (id & 0xFF) + 32;
                ReadStats(s, no + 1, f.Stats);
                f.Spark = s[no + 5] & 0xFF;
                for (int i = 0; i < 11; i++) f.Levels[i] = s[no + 6 + i] & 0xFF;
                if ((id & 0x1FF) == 258)
                    for (int i = 0; i < 11; i++)
                        if (f.Levels[i] > 0) f.Levels[i] = Mathf.Min(50, Work.partydata[385 + i] + f.Levels[i]);
            }
            else
            {
                byte[] a = id < 288 ? Data.npc_data : Data.new_npc_data;
                int idx = (id < 288 ? id : id - 288) * 10;
                if (a == null || idx + 10 > a.Length) return null;
                obj = id < 288 ? id >> 3 : ((id - 288) >> 3) + 203;
                ReadStats(a, idx + 1, f.Stats);
                f.Spark = a[idx + 5] & 0xFF;
                int bonus = a[idx + 8] & 0xFF;
                // player_skil_set と同じ: 適性ビットを上位から枠 0.. に当て、立っている枠だけ パーティのレベル + 加算 − 7 (0 未満は 0、50 まで)
                SkillSet(a[idx + 6] & 0xFF, bonus >> 4, 0, f.Levels);
                SkillSet(a[idx + 7] & 0xFF, bonus & 0xF, 5, f.Levels);
            }
            // _npc_hp_set: 乱数 (0..体力) + n × 体力 + 760 − (218 − 年/8)^2 / 64 (n は年で 0..14)。乱数は中央値で (実機: 見込み 58 に対し 57)
            {
                int ch = f.Stats[5];   // memberdata[133] = 体力
                int year = Work.partydata[686];
                int n = year <= 640 ? 14 - (640 - year) / 64 : 14;
                int hp = ch / 2 + n * ch + 760;
                int q = year / 8;
                if (q < 218) hp -= (218 - q) * (218 - q) / 64;
                f.Hp = Mathf.Clamp(hp, 1, 999);
            }
            try { f.Name = Main.menu.get_name(2, id); } catch (Exception e) { Plugin.Log.LogWarning("[Recruit] name failed: " + e.Message); }
            f.Class = ClassName(obj);
            f.SparkText = SparkText(f.Spark);
            f.Frames = PartyWeight.BuildFrames(obj, id, SPIN_PATS, "RecruitInfo");
            if (f.Frames != null && f.Frames[0] != null) f.Sprite = f.Frames[0];
            else f.Sprite = PartyWeight.BuildFrontFrame(obj, id, "RecruitInfo");
            if (Plugin.DiagEnabled != null && Plugin.DiagEnabled.Value)
            {
                string n5 = "";
                try { n5 = Main.menu.get_name(5, obj); } catch { }
                Plugin.Log.LogInfo("[Recruit] name5(obj " + obj + ")=\"" + n5 + "\"");
            }
            return f;
        }

        /// <summary>
        /// 仲間になっている人 (Work.memberdata の 1 人分) から。能力 memberdata[128..133] は加入時に npc_data から写された値で、その後
        /// 書き換える処理は本体に無い (書くのは npc_data_set / snpc_data_set だけ。Menu 4787 は表示用に +10 して [97..102] へ写すだけ)。
        /// 同じクラスをもう一度誘うと次の人 (id + 1、8 人で一周。加入のたびに script の inc_flag_0to7 が flag 224+クラス を進める) が来るので、
        /// 外して誘い直しても同じ人の数値が上がることはない。レベルは今の値 (memberdata[24..34])、HP は 今 [14] / 最大 [16]。
        /// </summary>
        private static Info BuildMember(int[] m)
        {
            if (m == null || m.Length < 134) return null;
            Info f = new Info();
            f.Member = true;
            f.Id = m[0] & 0x7FFF;
            int obj = m[2] & 0xFF, pal = m[0] & 0xFFFF;
            for (int i = 0; i < 6; i++) f.Stats[i] = Mathf.Clamp(m[128 + i], 0, 15);
            for (int i = 0; i < 11; i++) f.Levels[i] = Mathf.Clamp(m[24 + i], 0, 50);
            f.Spark = m[103] & 0xFF;
            f.Hp = m[16];
            f.HpNow = m[14];
            try { f.Name = Main.menu.get_name(2, f.Id); } catch (Exception e) { Plugin.Log.LogWarning("[Recruit] name failed: " + e.Message); }
            f.Class = ClassName(obj);
            f.SparkText = SparkText(f.Spark);
            f.Frames = PartyWeight.BuildFrames(obj, pal, SPIN_PATS, "RecruitInfo");
            if (f.Frames != null && f.Frames[0] != null) f.Sprite = f.Frames[0];
            else f.Sprite = PartyWeight.BuildFrontFrame(obj, pal, "RecruitInfo");
            return f;
        }

        private static void ReadStats(byte[] a, int at, int[] stats)
        {
            for (int i = 0; i < 3; i++)
            {
                int b = a[at + i] & 0xFF;
                stats[i * 2] = b & 0xF;
                stats[i * 2 + 1] = b >> 4;
            }
        }

        private static void SkillSet(int bits, int bonus, int start, int[] levels)
        {
            for (int i = start; i < 11; i++)
            {
                bits <<= 1;
                if ((bits & 0x100) == 0) continue;
                int v = Work.partydata[385 + i] + bonus;
                v = v < 7 ? 0 : Mathf.Min(50, v - 7);
                levels[i] = v;
            }
        }

        /// <summary>クラス名 (本体の Menu.GetClassName はデバッグ用の表だが、利用者向けの表は無いのでこれを整えて使う)。</summary>
        private static string ClassName(int obj)
        {
            if (obj == 203) return "忍者";
            if (obj == 204) return "陰陽師";
            try
            {
                string s = Menu.GetClassName(obj);
                if (string.IsNullOrEmpty(s) || s.StartsWith("不明")) return "";
                return s;
            }
            catch { return ""; }
        }

        /// <summary>
        /// よくひらめく技の系統。Battle.check_gettype は技ごとの npc_waza_table (u16) のビット (閃きタイプ) で閃けるかを決める。
        /// 武器ごとの技表 (sword_level 等、255 終端) で「このタイプが閃ける技の数 − 誰でも閃ける基本の技の数 (タイプ 15)」を数え、
        /// 3 つ以上多い武器を多い順に 3 つまで。
        /// </summary>
        private static string SparkText(int type)
        {
            byte[] tbl = Data.npc_waza_table;
            if (tbl == null || type < 0 || type > 15) return "";
            string[] names = { "剣", "大剣", "斧", "棍棒", "槍", "小剣", "弓", "体術" };
            byte[][] lists = { Data.sword_level, Data.great_sword_level, Data.axe_level, Data.club_level, Data.spear_level, Data.rapier_level, Data.bow_level, Data.wrestle_level };
            List<KeyValuePair<string, int>> extra = new List<KeyValuePair<string, int>>();
            StringBuilder diag = new StringBuilder();
            for (int w = 0; w < lists.Length; w++)
            {
                byte[] l = lists[w];
                if (l == null) continue;
                int mine = 0, basic = 0;
                for (int k = 0; k < l.Length; k++)
                {
                    int z = l[k] & 0xFF;
                    if (z == 255) break;
                    if (z * 2 + 1 >= tbl.Length) continue;
                    int mask = (tbl[z * 2] & 0xFF) | ((tbl[z * 2 + 1] & 0xFF) << 8);
                    if ((mask & (1 << type)) != 0) mine++;
                    if ((mask & 0x8000) != 0) basic++;
                }
                diag.Append(names[w]).Append(mine).Append('/').Append(basic).Append(' ');
                if (mine - basic >= 3) extra.Add(new KeyValuePair<string, int>(names[w], mine - basic));
            }
            extra.Sort((x, y) => y.Value.CompareTo(x.Value));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < extra.Count && i < 3; i++)
            {
                if (sb.Length > 0) sb.Append('・');
                sb.Append(extra[i].Key);
            }
            if (Plugin.DiagEnabled != null && Plugin.DiagEnabled.Value) Plugin.Log.LogInfo("[Recruit] spark type " + type + ": " + diag);
            return sb.Length > 0 ? sb.ToString() : "特になし (基本の技だけ)";
        }

        // ---------------------------------------------------------------- 3 行目を足す

        private static int[] OptionCodes()
        {
            if (s_optionCodes != null) return s_optionCodes;
            int[] c = Encode(OPTION_TEXT) ?? Encode(OPTION_TEXT_ALT);
            if (c == null)
            {
                Plugin.Log.LogWarning("[Recruit] option text not in font table, disabled");
                s_failed = true;
                return null;
            }
            s_optionCodes = c;
            return c;
        }

        /// <summary>文字列を本体の文字コード (Window.fonttbl の添字、0 終端) に。表に無い文字があれば null。</summary>
        private static int[] Encode(string text)
        {
            string tbl = Window.fonttbl;
            if (string.IsNullOrEmpty(tbl)) return null;
            int[] codes = new int[text.Length + 1];
            for (int i = 0; i < text.Length; i++)
            {
                int c = tbl.IndexOf(text[i]);
                if (c <= 0 || c == 80) return null;   // 0 は終端、80 は本体が終端扱いにする添字
                codes[i] = c;
            }
            return codes;
        }

        internal static string Decode(int[] codes)
        {
            string tbl = Window.fonttbl;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < codes.Length && codes[i] != 0; i++)
            {
                int c = codes[i];
                if (c == 230) { sb.Append(' '); continue; }
                if (c > 0 && c < tbl.Length) sb.Append(tbl[c]);
            }
            return sb.ToString();
        }

        /// <summary>Core.select_sa の後 (決定まで毎フレーム)。2 択が「パーティーに入れる」で始まる時だけ 3 行目を足す。</summary>
        internal static void AfterSelectSa(Core c)
        {
            if (s_failed || Enabled == null || !Enabled.Value) return;
            if (c.select_count != 2)
            {
                if (c.select_count < 2) s_rowActive = false;   // flash_select で組み直し
                return;
            }
            if (s_rowActive) return;
            try
            {
                string first = Decode(c.select_work[0]);
                if (!first.StartsWith(JOIN_TEXT)) return;
                if (Array.IndexOf(SKIP_MESSAGES, c.mess_no) >= 0)
                {
                    if (s_loggedMsg != c.mess_no) { s_loggedMsg = c.mess_no; Plugin.Log.LogInfo("[Recruit] msg " + c.mess_no + " sets the candidate after the choice, no option"); }
                    return;
                }
                int id = (c.koukan_work[10] + c.koukan_work[11] * 256) & 0x7FFF;
                int[] codes = OptionCodes();
                if (codes == null) return;
                int which = c.select_adrs[1] >> 12;
                int y = (c.select_adrs[1] & 0xFF) + ROW_H;
                Window win = (which >= 0 && which < c.meswin.Length) ? c.meswin[which] : null;
                if (win == null || y + ROW_H > win.h)
                {
                    if (s_loggedMsg != c.mess_no) { s_loggedMsg = c.mess_no; Plugin.Log.LogWarning("[Recruit] msg " + c.mess_no + ": no room for a 3rd row (y=" + y + " h=" + (win != null ? win.h : -1) + ")"); }
                    return;
                }
                c.select_adrs[ROW] = y + which * 4096;
                int[] row = c.select_work[ROW];
                int n = Math.Min(codes.Length, row.Length - 1);
                for (int i = 0; i < n; i++) row[i] = codes[i];
                row[n] = 0;
                // 本体の 2 行は script の put_moji2 が窓の画像 (Window.m_draw_text、色 3、1 文字 14 ドット) に描いて残る。足した行は put_select の
                // 点滅の重ね描き (選ばれている行だけ、alpha = cursor_cnt) でしか描かれず、カーソルが乗るまで見えなかった (ユーザー 2026-10-18)。
                // put_moji2 と同じく窓に描いておく (230 = 空白は進めるだけ)
                int tx = 0;
                for (int i = 0; i < n && row[i] != 0; i++)   // 0 は終端 (描くと fonttbl[0] の字が出て文字化けに見えた)
                {
                    if (row[i] != 230) win.drawFont(row[i], tx, y, 3);
                    tx += 14;
                }
                c.select_count = 3;
                s_rowActive = true;
                s_core = c;
                Plugin.Log.LogInfo("[Recruit] option added msg=" + c.mess_no + " id=" + id + " row y=" + y + " win=" + which + " h=" + win.h);
            }
            catch (Exception e)
            {
                Fail("select_sa", e);
            }
        }

        /// <summary>Core.put_select の前。3 行目で決定したら窓を開き、開いている間は本体の処理を止めて script を待たせる。</summary>
        internal static bool BeforePutSelect(Core c)
        {
            if (!s_rowActive) return true;
            if (s_failed || Enabled == null || !Enabled.Value)
            {
                // 途中で止まった / 切られた: 3 行目を外し、カーソルが 3 行目なら 1 行目へ (select_now = 2 のまま決定されると script が 3 つ目の飛び先を読む)
                if (c.select_count == 3) { c.select_count = 2; if (c.select_now > 1) c.select_now = 0; }
                s_rowActive = false;
                s_open = false;
                return true;
            }
            if (s_open) return false;
            if (c.select_count != 3) { s_rowActive = false; return true; }
            try
            {
                // キャンセルを「入れない」の決定に読み替える: カーソルを「入れない」に置き、キャンセルのビットを決定のビットに差し替えて本体に渡す
                // (本体の put_select は select_b_trig_ok = 1 の script の選択肢ではキャンセルを見ない)。他の会話の選択肢は DialogueCancel が同じ事をする
                if (DialogueCancel.Enabled != null && DialogueCancel.Enabled.Value && (c.key_is_what & CANCEL_BIT) != 0 && (c.key_is_what & DECIDE_BITS) == 0)
                {
                    c.select_now = DECLINE_ROW;
                    c.key_is_what = (c.key_is_what & ~CANCEL_BIT) | 0x100000;
                    Plugin.Log.LogInfo("[Recruit] cancel -> decline (row " + DECLINE_ROW + ")");
                    return true;
                }
                if (c.select_now != ROW) return true;
                bool decide = (c.key_is_what & DECIDE_BITS) != 0 || InputManager.IsDaubleTap() || InputManager.get_button_name_down() == "Select0" + ROW;
                if (!decide) return true;
                Open(c);
                return false;
            }
            catch (Exception e)
            {
                Fail("put_select", e);
                return true;
            }
        }

        /// <summary>Core.put_select の後。本体が決定を受け付けたら (is_message_select が落ちる) select_count を 2 に戻す。</summary>
        internal static void AfterPutSelect(Core c)
        {
            if (!s_rowActive || c.is_message_select) return;
            if (c.select_count == 3) c.select_count = 2;
            s_rowActive = false;
            Plugin.Log.LogInfo("[Recruit] decided row " + c.select_now);
        }

        private static void Fail(string where, Exception e)
        {
            Plugin.Log.LogError("[Recruit] " + where + " failed, disabled until restart: " + e);
            s_failed = true;
            s_open = false;
            s_rowActive = false;
        }

        // ---------------------------------------------------------------- 窓

        private static void Open(Core c)
        {
            int id = (c.koukan_work[10] + c.koukan_work[11] * 256) & 0x7FFF;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Info f = Build(id);
            if (f == null)
            {
                Plugin.Log.LogWarning("[Recruit] no data for id " + id);
                c.select_now = 0;
                return;
            }
            Show(f, c, SpeechCapture.Current(c.select_adrs[ROW] >> 12), sw);
        }

        /// <summary>ステータス画面から: 表示中の仲間 (Work.memberdata[slot]) の窓。台詞の吹き出しは無し (帯ごと省かれる)。</summary>
        private static void OpenMember(int slot)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            int[][] md = Work.memberdata;
            Info f = (md != null && slot >= 0 && slot < md.Length) ? BuildMember(md[slot]) : null;
            if (f == null)
            {
                Plugin.Log.LogWarning("[Recruit] no data for member slot " + slot);
                return;
            }
            s_prevLB = PageBackDown(); s_prevRB = PageNextDown();   // 開いた時の押しっぱなしを送りに見せない
            Show(f, null, "", sw);
        }

        private static bool s_prevLB, s_prevRB;   // 仲間の窓を開いている間の LB / RB (押下エッジ用)
        private static System.Reflection.MethodInfo s_statusL, s_statusR;

        private static bool PageBackDown() { return PadMap.IsPhysicalDown(PadMap.PHYS_LB) || KeyMap.IsDown(PadFunction.L); }
        private static bool PageNextDown() { return PadMap.IsPhysicalDown(PadMap.PHYS_RB) || KeyMap.IsDown(PadFunction.R); }

        /// <summary>
        /// 仲間の窓を開いたまま LB / RB で前後の仲間へ (ユーザー 2026-10-18)。後ろのステータス画面も本体の status_l / status_r で同じ人に
        /// 送る (main_te2 / oam_type[0] を進めて m_is_init_status を落とすので、本体が次のフレームで描き直す)。窓は滑り込みを繰り返さず、
        /// 絵の回転 → レーダー → バー だけやり直す (演出の時計を滑り込みの終わりに合わせる)。
        /// </summary>
        private static void SwitchMember(int delta)
        {
            Menu menu = Main.menu;
            if (menu == null) return;
            if (s_statusL == null) s_statusL = AccessTools.Method(typeof(Menu), "status_l");
            if (s_statusR == null) s_statusR = AccessTools.Method(typeof(Menu), "status_r");
            System.Reflection.MethodInfo m = delta < 0 ? s_statusL : s_statusR;
            if (m == null) { Plugin.Log.LogWarning("[Recruit] Menu.status_l/status_r not found"); return; }
            m.Invoke(menu, null);
            int slot = (menu.oam_type != null && menu.oam_type.Length > 0) ? menu.oam_type[0] - 1 : -1;
            int[][] md = Work.memberdata;
            Info f = (md != null && slot >= 0 && slot < md.Length) ? BuildMember(md[slot]) : null;
            if (f == null) { Plugin.Log.LogWarning("[Recruit] no data for member slot " + slot); return; }
            DisposeInfo(s_info);
            s_info = f;
            ResetAnim();
            f.Speech = "";
            BuildRadar(f);
            s_openTime = Time.realtimeSinceStartup - SLIDE_SECONDS;
            UiParts.SeCursor();
            Plugin.Log.LogInfo("[Recruit] switch member slot " + slot + " id=" + f.Id + " name=" + f.Name);
        }

        private static void Show(Info f, Core c, string speech, System.Diagnostics.Stopwatch sw)
        {
            long msBuild = sw.ElapsedMilliseconds;
            DisposeInfo(s_info);
            s_info = f;
            s_core = c;
            s_open = true;
            s_prevKey = -1;
            // 演出の時計は最初の 1 枚を描き終えた時に始める (DrawWindow の末尾)。開いた時刻から数えると、初回はテクスチャ作り・スタイル作り・JIT で
            // 最初の 1 枚が出るまでに時間がかかり、滑り込みが途中から始まって見えた (ユーザー 2026-10-18。2 回目からは出ていた)
            s_openTime = -1f;
            s_openWatch = sw;
            ResetAnim();
            f.Speech = speech ?? "";
            BuildRadar(f);
            Plugin.Log.LogInfo("[Recruit] build " + msBuild + " ms, radar " + (sw.ElapsedMilliseconds - msBuild) + " ms");
            UiParts.SeOk();
            StringBuilder sb = new StringBuilder();
            sb.Append(f.Member ? "[Recruit] open member id=" : "[Recruit] open id=").Append(f.Id).Append(" name=").Append(f.Name).Append(" class=").Append(f.Class)
              .Append(" stats=");
            for (int i = 0; i < 6; i++) sb.Append(f.Stats[i]).Append(i < 5 ? "/" : "");
            sb.Append(" lv=");
            for (int i = 0; i < 11; i++) sb.Append(f.Levels[i]).Append(i < 10 ? "/" : "");
            sb.Append(" spark=").Append(f.Spark).Append(f.Member ? " hp=" + f.HpNow + "/" : " hp~").Append(f.Hp).Append(" party=");
            for (int i = 0; i < 11; i++) sb.Append(Work.partydata[385 + i]).Append(i < 10 ? "/" : "");
            sb.Append(" year=").Append(Work.partydata[686]);
            sb.Append(" speech=\"").Append(f.Speech.Replace("\n", "/")).Append('"');
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static void Close()
        {
            if (!s_open) return;
            s_open = false;
            s_guard = true;
            if (s_core != null)
            {
                s_core.select_now = 0;       // 2 択に戻す (カーソルは「パーティーに入れる」)
                s_core.cursor_cnt = 256;
            }
            UiParts.SeClose();
            Plugin.Log.LogInfo("[Recruit] close");
        }

        private static void DisposeInfo(Info f)
        {
            if (f == null) return;
            if (f.Frames != null)
            {
                for (int i = 0; i < f.Frames.Length; i++) if (f.Frames[i] != null) UnityEngine.Object.Destroy(f.Frames[i]);
                f.Frames = null;
                f.Sprite = null;   // Frames[0] と同じもの
            }
            if (f.Sprite != null) UnityEngine.Object.Destroy(f.Sprite);
            if (f.Bubble != null) UnityEngine.Object.Destroy(f.Bubble);
            f.Bubble = null;
            if (f.RadarGrid != null) UnityEngine.Object.Destroy(f.RadarGrid);
            if (f.RadarFill != null) UnityEngine.Object.Destroy(f.RadarFill);
            f.Sprite = null; f.RadarGrid = null; f.RadarFill = null;
        }

        /// <summary>
        /// Util.GetKeypadState の最終段 (ConfigMenuGatePatch) から。開いている間は入力を奪い、決定 / キャンセル / Esc で閉じる。
        /// 閉じた後も押したままの決定 / キャンセルを離すまで 0 を返す (そのまま渡すと本体の put_select が「パーティーに入れる」を決定してしまう)。
        /// </summary>
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
            if (s_info != null && s_info.Member)
            {
                // 仲間の窓: LB / RB で前後の仲間 (物理ボタン。どこでも大臣のページ送りと同じ読み方)
                bool lb = PageBackDown(), rb = PageNextDown();
                try
                {
                    if (lb && !s_prevLB) SwitchMember(-1);
                    else if (rb && !s_prevRB) SwitchMember(+1);
                }
                catch (Exception e) { Fail("switch member", e); }
                s_prevLB = lb; s_prevRB = rb;
            }
            return 0;
        }

        // ---------------------------------------------------------------- 描画 (Plugin.OnGUI)

        private static Font s_font;
        private static GUIStyle s_line, s_dim, s_title, s_box, s_hint, s_name, s_accent, s_head, s_center, s_speech;
        private static GUIStyle s_barLabel, s_barDim, s_barHead;
        private static int s_barFontBuilt = -1;
        private static Texture2D s_barTrack, s_barWeapon, s_barMagic;
        private static int s_fontSizeBuilt = -1;

        private static Texture2D Solid(Color c)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        public static void Draw()
        {
            if (s_failed || ConfigMenu.IsOpen) return;
            if (!s_open || s_info == null) return;
            try { DrawWindow(s_info); }
            catch (Exception e) { Fail("draw", e); }
        }

        // ---------------------------------------------------------------- ステータス画面から開く

        /// <summary>
        /// Menu.proc_status (ステータス画面の入力) の後。決定ボタンの押下 (Sys.key[1] = そのフレームに押されたビット) で表示中の仲間
        /// (oam_type[0] − 1 = Work.memberdata の添字。本体が StatusDisply に写すのと同じ) の窓を開く。
        /// 画面に入った直後の 2 フレームは拾わない (メニューで「ステータス」を決定した押下が残ることがある)。
        /// </summary>
        internal static void AfterProcStatus(Menu menu)
        {
            if (s_failed || PartyStatus == null || !PartyStatus.Value || s_open || menu == null) return;
            int now = Time.frameCount;
            if (now - s_statusFrame > 2) s_statusSince = now;
            s_statusFrame = now;
            if (s_guard || now - s_statusSince < 2) return;
            if (!Sys.checkKey2(1, BTN_OK)) return;
            try
            {
                int slot = (menu.oam_type != null && menu.oam_type.Length > 0) ? menu.oam_type[0] - 1 : -1;
                OpenMember(slot);
            }
            catch (Exception e) { Fail("status screen", e); }
        }

        // 画面の下に「決定ボタン: …」の案内を出していたが、本体の画面に白い文字が浮いて見えたので消した (ユーザー 2026-10-18 夕)。
        // 開き方は README と設定画面の項目名で伝える

        // 開いた時の演出は 5 段階 (ユーザー 2026-10-18「1 個ずつ段階的に」): 窓が右から滑り込む → 吹き出しの台詞が左から 1 文字ずつ出る →
        // 絵が回って構える → レーダーが中心から広がる → バーが伸びる。
        // 絵の回転は最初 Show() の時刻から数えていたので、台詞が出終わる頃には回り終えて構えの絵で止まっていた (ユーザー 2026-10-18 夕)。
        // 今は Anim.Wait で正面の絵のまま待ち、台詞の終わり (台詞が無ければ滑り込みの終わり) で回し始める
        private const float SLIDE_SECONDS = 0.5f;    // 窓が右から滑り込む時間 (0.28 秒はあっさりしすぎた)
        private const float SPIN_PAUSE = 0.1f;       // 回って構えてからレーダーが広がり始めるまでの間
        private static float s_spinStart;            // 絵が回り始める時刻 (開いてからの秒。DrawWindow が毎回計算)
        private const float TYPE_SECONDS = 0.022f;   // 台詞 1 文字あたりの時間 (約 45 文字 / 秒。0.035 は遅かった。ユーザー 2026-10-18)
        private const float TYPE_PAUSE = 0.15f;      // 台詞を出し終えてからレーダーが広がり始めるまでの間
        private const float PUNCT_PAUSE = 0.25f;     // 文末 (。！？) の後のタメ
        private const float ANIM_SECONDS = 0.6f;     // レーダーが広がる時間
        private static float s_chartStart;           // レーダーが広がり始める時刻 (開いてからの秒。台詞の長さで変わるので DrawWindow が毎回計算)
        private const float BAR_SECONDS = 0.45f;     // バーが 0 から伸びる時間。全部のバーが同時に同じ秒数で伸びる (長いバーほど速い。ユーザー 2026-10-18)
        private const float BAR_START = 0.7f;        // バーが伸び始めるのはレーダーの進みがこの割合の時 (終わりは緩めているので、1.0 まで待つと間が空いて見えた)
        private const float BAR_STAGGER = 0f;        // 上から 1 本ずつ遅らせる間隔 (0 = 同時。1 本ずつはユーザーの好みに合わなかった)

        /// <summary>0 → 1 を終わりで緩める (3 乗のイーズアウト)。</summary>
        private static float Ease(float t) { return 1f - (1f - t) * (1f - t) * (1f - t); }
        // 絵の動き (ShopEquipSpin と同じ実機の記録。1 論理フレーム = 1/30 秒):
        //   攻撃を決めた時 (302): コマ 0 (1) → 3 (1) → 1 (2) → 2 (2) → 6 (2) → 7 (武器の構え)
        //   コマンド窓が開く時 (305): コマ 0 (1) → 3 (1) → 1 (2) → 10 (選択中の構え)
        // 開いた直後は必ず「攻撃を決めた時」。構えのまま少し止まり、正面のコマを左右に反転する歩き (どこでも体重計と同じ、8 論理フレームごと) に戻り、
        // しばらく歩いたらどちらかの回り方をランダムに選んで繰り返す (ユーザーの指定 2026-10-18)。
        private static readonly int[] SPIN_PATS = { 0, 3, 1, 2, 6, 7, 10 };            // Frames の順
        private static readonly int[][] SEQ_WEAPON = { new[] { 0, 1 }, new[] { 1, 1 }, new[] { 2, 2 }, new[] { 3, 2 }, new[] { 4, 2 }, new[] { 5, 0 } };   // { Frames の添字, フレーム数 (0 = 構え) }
        private static readonly int[][] SEQ_SELECT = { new[] { 0, 1 }, new[] { 1, 1 }, new[] { 2, 2 }, new[] { 6, 0 } };
        private const float LOGICAL_FRAME = 1f / 30f;
        private const float WALK_STEP = 8f / 30f;      // 歩きの反転の間隔
        private const float HOLD_SECONDS = 1.6f;        // 構えのまま止まる時間
        private const float WALK_MIN = 1.8f, WALK_MAX = 3.2f;
        private enum Anim { Wait, Spin, Hold, Walk }   // Wait = 正面の絵のまま s_spinStart を待つ (開いた直後だけ)
        private static Anim s_anim;
        private static float s_animStart;
        private static float s_walkDur;
        private static int[][] s_seq;

        private static void ResetAnim()
        {
            s_anim = Anim.Wait;
            // 最初は必ず「攻撃を決めた時」の回り方 (構え 7)。2 回目以降は歩きのあとにランダム
            s_seq = SEQ_WEAPON;
            s_animStart = Time.realtimeSinceStartup;
        }

        /// <summary>「攻撃を決めた時」の回り方にかかる秒数 (構えまで。SEQ_WEAPON のコマ数の合計 = 8 論理フレーム)。</summary>
        private static float SpinSeconds()
        {
            int frames = 0;
            for (int i = 0; i < SEQ_WEAPON.Length; i++) frames += SEQ_WEAPON[i][1];
            return frames * LOGICAL_FRAME;
        }

        /// <summary>今のコマと左右反転。状態の切り替えもここで (描画のたびに呼ばれる)。elapsed は開いてからの秒 (演出の時計)。</summary>
        private static Texture2D CurrentFrame(Info f, float elapsed, out bool flip)
        {
            flip = false;
            if (f.Frames == null) return f.Sprite;
            float now = Time.realtimeSinceStartup;
            if (s_anim == Anim.Wait)
            {
                if (elapsed < s_spinStart)
                {
                    // 窓が着いてから台詞を喋っている間は歩く (正面の絵の左右反転。ユーザー 2026-10-18「喋ってる風な時に動かしたい」)。
                    // 滑り込みの途中は止まったまま。台詞が無ければ s_spinStart = 滑り込みの終わりなので歩かない
                    float talking = elapsed - SLIDE_SECONDS;
                    flip = talking > 0f && ((int)(talking / WALK_STEP) & 1) != 0;
                    return f.Sprite;
                }
                s_anim = Anim.Spin; s_animStart = now;
            }
            float el = now - s_animStart;
            switch (s_anim)
            {
                case Anim.Spin:
                {
                    float acc = 0f;
                    for (int i = 0; i < s_seq.Length; i++)
                    {
                        if (s_seq[i][1] == 0)
                        {
                            s_anim = Anim.Hold; s_animStart = now;
                            return f.Frames[s_seq[i][0]] ?? f.Sprite;
                        }
                        acc += s_seq[i][1] * LOGICAL_FRAME;
                        if (el < acc) return f.Frames[s_seq[i][0]] ?? f.Sprite;
                    }
                    s_anim = Anim.Hold; s_animStart = now;
                    return f.Sprite;
                }
                case Anim.Hold:
                    if (el >= HOLD_SECONDS)
                    {
                        s_anim = Anim.Walk; s_animStart = now;
                        s_walkDur = UnityEngine.Random.Range(WALK_MIN, WALK_MAX);
                    }
                    return f.Frames[s_seq[s_seq.Length - 1][0]] ?? f.Sprite;
                default:
                    if (el >= s_walkDur)
                    {
                        s_anim = Anim.Spin; s_animStart = now;
                        s_seq = UnityEngine.Random.value < 0.5f ? SEQ_WEAPON : SEQ_SELECT;
                        return f.Sprite;
                    }
                    flip = ((int)(el / WALK_STEP) & 1) != 0;
                    return f.Sprite;
            }
        }
        private const int STAT_MIN = 10, STAT_MAX = 25; // 表示の能力値 (素質 0..15 に +10)。チャートは 0 を中心、25 を外周に (目盛り 5 刻み)
        private const int RADAR_RINGS = 5;              // 目盛りの六角形の数 (5 / 10 / 15 / 20 / 25)。中心 10 の 3 段では 13 が中心近くに見えて値と合わなかった (ユーザー 2026-10-18)
        private const int LEVEL_MAX = 50;
        private static float s_openTime;             // 演出の起点 (最初の 1 枚を描き終えた時刻)。-1 = まだ描いていない
        private static System.Diagnostics.Stopwatch s_openWatch;   // 開いてから最初の 1 枚までの時間をログに出す用

        private static void DrawWindow(Info f)
        {
            float outer = Mathf.Round(UiParts.Unit * 8f);
            // 画面の高さの 8 割ほどの大きな窓。文字は設定画面より一回り大きく
            float lineH = Mathf.Clamp(Screen.height / 19f, 18f, 56f);
            int fontSize = Mathf.Clamp(Mathf.FloorToInt(lineH / 1.45f), 12, 38);
            lineH = Mathf.Min(lineH, fontSize * 1.6f);
            EnsureStyles(fontSize);
            float pad = lineH * 0.7f;
            float headH = Mathf.Round(lineH * 3.2f);
            float footH = lineH;
            float wW = Mathf.Min(Screen.width - 2 * outer, Mathf.Round(lineH * 26f));
            float cw = wW - 2 * pad;
            // 台詞の吹き出し: 文字は一回り小さく、行は詰める。台詞が無ければ帯ごと無し。
            // 本体の会話窓の 1 行が吹き出しの幅より長いとはみ出していた (ユーザー 2026-10-18 夕) ので、幅で折り返してから行数を数える
            string[] speech = WrapSpeech(f, cw - lineH * 1.6f - fontSize * 0.2f);
            int speechLines = speech.Length;
            float speechLineH = Mathf.Round(lineH * 0.82f);
            // 吹き出しは店の E / ▲N と同じドット絵 (格子 1 マス = 1.125 単位、文字のドットの半分)。尾は下向きに BUBBLE_TAIL マス
            int cell = Mathf.Max(1, Mathf.RoundToInt(UiParts.Unit * 1.125f));
            float bubbleH = speechLines > 0 ? Mathf.Round(speechLineH * speechLines + lineH * 0.9f) : 0f;
            float bubbleGap = Mathf.Round(lineH * 0.35f);   // 尾の先と絵の頭の間 (0 だと重なり、0.75 行は空きすぎだった → その中間)
            float bubbleBand = speechLines > 0 ? bubbleH + BUBBLE_TAIL * cell + bubbleGap : 0f;
            float h = Mathf.Min(Screen.height - 2 * outer, Mathf.Round(lineH * 15.5f + bubbleBand));
            float x = Mathf.Round((Screen.width - wW) / 2f);
            float y = Mathf.Round((Screen.height - h) / 2f);
            // 開いてからの進み: まず窓が右から滑り込み (SLIDE_SECONDS、終わりで緩める)、着いてからチャートとバーが伸びる (ANIM_SECONDS)
            float elapsed = s_openTime < 0f ? 0f : Time.realtimeSinceStartup - s_openTime;
            float slide = Ease(Mathf.Clamp01(elapsed / SLIDE_SECONDS));
            x += Mathf.Round((1f - slide) * (Screen.width - x));
            Rect win = new Rect(x, y, wW, h);
            // 台詞は窓が着いてから 1 文字ずつ (改行は数えない)。レーダーは台詞を出し終えて TYPE_PAUSE 置いてから
            // 文末 (。！？) の後には PUNCT_PAUSE のタメを入れる (ユーザー 2026-10-18)。文字ごとの出る時刻を頭から足して、今の時刻までに出た数を数える
            int typed = 0;
            float acc = 0f, lastPause = 0f;
            float typing = elapsed - SLIDE_SECONDS;
            for (int i = 0; i < f.Speech.Length; i++)
            {
                char ch = f.Speech[i];
                if (ch == '\n') continue;
                acc += TYPE_SECONDS;
                if (acc <= typing) typed++;
                lastPause = (ch == '。' || ch == '！' || ch == '？') ? PUNCT_PAUSE : 0f;
                acc += lastPause;
            }
            // 台詞の終わりで絵が回り始め、構えてから SPIN_PAUSE 置いてレーダー
            s_spinStart = SLIDE_SECONDS + (acc > 0f ? acc - lastPause + Mathf.Max(TYPE_PAUSE, lastPause) : 0f);
            s_chartStart = s_spinStart + SpinSeconds() + SPIN_PAUSE;
            float t = Ease(Mathf.Clamp01((elapsed - s_chartStart) / ANIM_SECONDS));

            UiParts.Shadow = !UiParts.DrawPaperWindow(win);
            if (UiParts.Shadow) GUI.Box(win, GUIContent.none, s_box);
            float cx = x + pad, cy = y + pad;

            // 台詞の吹き出し (一番上、幅いっぱい。尾は左下からその下の絵へ)。絵の中心 x は見出しと同じ計算 (2 行分の正方形の中心)
            if (speechLines > 0)
            {
                int sprDot = Mathf.Max(1, Mathf.FloorToInt(lineH * 2f / 26f));
                float sprCx = cx + sprDot * 26f / 2f;
                int cellsW = Mathf.FloorToInt(cw / cell), cellsH = Mathf.CeilToInt(bubbleH / cell) + BUBBLE_TAIL;
                int tailCell = Mathf.RoundToInt((sprCx - cx) / cell);
                if (f.Bubble == null || f.BubbleW != cellsW || f.BubbleH != cellsH)
                {
                    if (f.Bubble != null) UnityEngine.Object.Destroy(f.Bubble);
                    f.Bubble = MakeBubbleTexture(cellsW, cellsH, BUBBLE_TAIL, tailCell);
                    f.BubbleW = cellsW; f.BubbleH = cellsH;
                }
                Rect br = new Rect(cx, cy, cellsW * cell, cellsH * cell);
                if (f.Bubble != null)
                {
                    Color col = GUI.color; GUI.color = Color.white;
                    GUI.DrawTexture(br, f.Bubble, ScaleMode.StretchToFill, true);
                    GUI.color = col;
                }
                // 文字の縦位置: 計算上の中央は上下 0.45 行。OS フォント (Meiryo UI) は枠の中で下寄りに出るので 0.1 行上げる。
                // 本体のフォントは逆に上寄りに出る (0.35 行では 5 px ほど高かった。ユーザー 2026-10-18) ので 0.5 行
                float ty = cy + Mathf.Round(lineH * (UiParts.GameFont() != null ? 0.5f : 0.35f));
                int li = 0, left = typed;   // 出し終えた文字数を行ごとに消費する (左揃えなので頭から出せば左から右へ現れる)
                foreach (string line in speech)
                {
                    int n = Mathf.Min(left, line.Length);
                    if (n > 0) UiParts.GameLabel(new Rect(cx + lineH * 0.8f, ty + speechLineH * li, cw - lineH * 1.6f, speechLineH), n == line.Length ? line : line.Substring(0, n), s_speech);
                    left -= n;
                    li++;
                }
                cy += br.height + bubbleGap;
            }

            // 見出し: 左に候補の絵 (ドット絵なので整数倍)、右に名前 (大きく) とクラス、その下によくひらめく技
            float titleX = cx;
            float sprCenterX = cx + lineH;
            bool flip;
            Texture2D frame = CurrentFrame(f, elapsed, out flip);
            if (frame != null)
            {
                // 2 行分の高さ (1600x900 で 3 倍 = 78 px)。見出しの幅いっぱいだと大きすぎた
                int dot = Mathf.Max(1, Mathf.FloorToInt(lineH * 2f / frame.height));
                float spr = dot * frame.height;
                Rect sr = new Rect(cx, cy, spr, spr);   // 見出しの上端に寄せる (吹き出しの尾のすぐ下)
                Color col = GUI.color; GUI.color = Color.white;
                GUI.DrawTextureWithTexCoords(sr, frame, flip ? new Rect(1f, 0f, -1f, 1f) : new Rect(0f, 0f, 1f, 1f), true);
                GUI.color = col;
                titleX = cx + spr + fontSize * 0.6f;
                sprCenterX = cx + spr / 2f;
            }
            float headW = cw - (titleX - cx);
            float nameH = Mathf.Round(lineH * 1.5f);
            UiParts.GameLabel(new Rect(titleX, cy, headW, nameH), f.Name, s_name);
            if (f.Class.Length > 0)
            {
                float nameW = UiParts.GameTextWidth(f.Name, s_name);
                UiParts.GameLabel(new Rect(titleX + nameW + fontSize, cy, headW - nameW - fontSize, nameH), f.Class, s_line);
            }
            UiParts.GameLabel(new Rect(titleX, cy + nameH, headW, lineH), "よくひらめく技:  " + f.SparkText, s_accent);
            UiParts.GameLabel(new Rect(titleX, cy + nameH + lineH * 0.95f, headW, lineH), f.Member ? "HP  " + f.HpNow + " / " + f.Hp : "HP の見込み  約 " + f.Hp, s_line);
            cy += headH + lineH * 0.2f;

            // 本文: 左にレーダーチャート (能力 6 つ)、右に武器 / 術のレベルのバー
            float bodyH = y + h - pad - footH - cy;
            float chartW = Mathf.Round(cw * 0.46f);
            DrawRadar(f, new Rect(cx, cy, chartW, bodyH), fontSize, t);
            float bx = cx + chartW + fontSize;
            float bw = cx + cw - bx;
            DrawBars(f, new Rect(bx, cy, bw, bodyH), lineH, fontSize, elapsed);

            // 説明は枠の下の余白に 0.25 行だけ食い込ませる (バーの段との間が詰まって見えた。ユーザー 2026-10-18)
            UiParts.GameLabel(new Rect(cx, y + h - pad - footH + Mathf.Round(lineH * 0.25f), cw, footH),
                f.Member ? "能力は生まれつき。武器 / 術は今のレベル。決定 / キャンセル で戻る" : "武器 / 術は加入した時のレベルの見込み。決定 / キャンセル で戻る", s_hint);
            if (UiParts.ClickArea(win)) Close();
            if (s_openTime < 0f)
            {
                s_openTime = Time.realtimeSinceStartup;
                Plugin.Log.LogInfo("[Recruit] first frame drawn " + (s_openWatch != null ? s_openWatch.ElapsedMilliseconds : -1) + " ms after open (animation clock starts now)");
            }
        }

        private const string NO_LINE_HEAD = "、。」』！？…ー";   // 行頭に来させない文字 (前の行の末尾に付ける。はみ出しは 1 文字分まで)

        /// <summary>
        /// 台詞を吹き出しの幅 maxW で折り返す。本体の改行はそのまま行の切れ目。1 行が幅を超えたら文字単位で切る (本体のフォントで実測)。
        /// 結果は Info に覚え、幅か字の大きさが変わった時だけ作り直す (毎フレーム測ると 1 文字ずつ幅を測るのが無駄)。
        /// </summary>
        private static string[] WrapSpeech(Info f, float maxW)
        {
            if (f.Speech.Length == 0) return new string[0];
            if (f.SpeechLines != null && f.SpeechFont == s_fontSizeBuilt && Mathf.Abs(f.SpeechWrapW - maxW) < 0.5f) return f.SpeechLines;
            List<string> lines = new List<string>();
            string[] src = f.Speech.Split('\n');
            for (int li = 0; li < src.Length; li++)
            {
                string s = src[li];
                if (s.Length == 0 || UiParts.GameTextWidth(s, s_speech) <= maxW) { lines.Add(s); continue; }
                int start = 0;
                while (start < s.Length)
                {
                    int n = 1;
                    while (start + n < s.Length && UiParts.GameTextWidth(s.Substring(start, n + 1), s_speech) <= maxW) n++;
                    if (start + n < s.Length && NO_LINE_HEAD.IndexOf(s[start + n]) >= 0) n++;
                    lines.Add(s.Substring(start, n));
                    start += n;
                }
            }
            f.SpeechLines = lines.ToArray();
            f.SpeechWrapW = maxW;
            f.SpeechFont = s_fontSizeBuilt;
            if (f.SpeechLines.Length != src.Length)
                Plugin.Log.LogInfo("[Recruit] speech wrapped " + src.Length + " -> " + f.SpeechLines.Length + " lines (maxW " + Mathf.Round(maxW) + " px, font " + s_fontSizeBuilt + ")");
            return f.SpeechLines;
        }

        /// <summary>レーダーチャート。網目と能力の六角形は開いた時に描いたテクスチャ。六角形は中心から t 倍に拡大して出す (頂点の位置は中心からの距離に比例するので、そのまま「伸びる」絵になる)。</summary>
        private static void DrawRadar(Info f, Rect area, int fontSize, float t)
        {
            float labelW = fontSize * 4.8f, labelH = fontSize * 1.4f;   // 「器用さ 22」(3 文字 + 空白 + 2 桁) が入る幅
            // 頂点の文字が入る分だけ内側に (左右は文字の幅、上下は文字の高さ)
            float size = Mathf.Min(area.width - labelW * 2f, area.height - labelH * 2f);
            if (size < fontSize * 4) size = Mathf.Min(area.width, area.height);
            float ccx = area.x + area.width / 2f, ccy = area.y + area.height / 2f;
            Rect r = new Rect(Mathf.Round(ccx - size / 2f), Mathf.Round(ccy - size / 2f), Mathf.Round(size), Mathf.Round(size));
            Color col = GUI.color; GUI.color = Color.white;
            if (f.RadarGrid != null) GUI.DrawTexture(r, f.RadarGrid, ScaleMode.StretchToFill, true);
            if (f.RadarFill != null && t > 0.01f)
            {
                float s = size * t;
                GUI.DrawTexture(new Rect(ccx - s / 2f, ccy - s / 2f, s, s), f.RadarFill, ScaleMode.StretchToFill, true);
            }
            GUI.color = col;
            // 頂点の外に 能力名 と 値
            float radius = size / 2f * RADAR_R;
            // 左右の頂点の文字の置き方: 基本は頂点から gap 離して頂点側の端を揃える (左は右端、右は左端)。
            //   それだけだと短い「魔力 13」が長い「器用さ 22」より内側に寄って見えたので、短い方だけ 1 画素 (1600x900 で) 外へずらす
            //   (幅の差の 1/2 = 中心を揃える は外に寄りすぎ、1/4 でもまだ寄りすぎだった。ユーザー 2026-10-18 の指定は「揃えた位置から 1 画素右」)
            float shortPx = Mathf.Max(1f, Mathf.Round(UiParts.Unit * 0.6f));
            string[] texts = new string[6];
            float[] widths = new float[6];
            float maxL = 0f, maxR = 0f;
            for (int i = 0; i < 6; i++)
            {
                int si = RADAR_ORDER[i];
                texts[i] = STAT_NAMES[si] + " " + Mathf.RoundToInt((STAT_MIN + f.Stats[si]) * t);   // 数字は 0 から数え上げる (10 からではなく。ユーザー 2026-10-18)
                float dx = Mathf.Cos((-90f + 60f * i) * Mathf.Deg2Rad);
                if (Mathf.Abs(dx) < 0.1f) continue;
                widths[i] = UiParts.GameTextWidth(STAT_NAMES[si] + " " + (STAT_MIN + f.Stats[si]), s_center);   // 数字は最終値で測る (伸びる間に動かない)
                if (dx < 0f) maxL = Mathf.Max(maxL, widths[i]); else maxR = Mathf.Max(maxR, widths[i]);
            }
            float gap = fontSize * 0.35f;
            for (int i = 0; i < 6; i++)
            {
                float ang = (-90f + 60f * i) * Mathf.Deg2Rad;
                float dx = Mathf.Cos(ang), dy = Mathf.Sin(ang);
                float ly = ccy + dy * (radius + labelH * 0.6f);
                float vx = ccx + dx * radius;
                float lx;   // 文字の中心
                if (dx < -0.1f) lx = vx - gap - (widths[i] < maxL - 0.5f ? shortPx : 0f) - widths[i] / 2f;
                else if (dx > 0.1f) lx = vx + gap + (widths[i] < maxR - 0.5f ? shortPx : 0f) + widths[i] / 2f;
                else lx = ccx;
                UiParts.GameLabel(new Rect(lx - labelW / 2f, ly - labelH / 2f, labelW, labelH), texts[i], s_center);
            }
        }

        /// <summary>武器 5 本と術 6 本のレベルのバー (0..50)。適性なしは空のバーと「－」。</summary>
        private static void DrawBars(Info f, Rect area, float lineH, int fontSize, float elapsed)
        {
            float rowH = Mathf.Min(lineH, area.height / 13.2f);     // 見出し 2 + バー 11 + 余白
            // 行が文字より低いと上下が切れる (最初の版の不具合) ので、行に入る大きさの文字にする
            int barFont = Mathf.Clamp(Mathf.FloorToInt(rowH / 1.35f), 10, fontSize);
            if (s_barFontBuilt != barFont || s_barLabel == null)
            {
                s_barFontBuilt = barFont;
                s_barLabel = new GUIStyle(s_line); s_barLabel.fontSize = barFont;
                s_barDim = new GUIStyle(s_dim); s_barDim.fontSize = barFont;
                s_barHead = new GUIStyle(s_head); s_barHead.fontSize = barFont + 1;
                UiParts.ResetStyles();
            }
            fontSize = barFont;
            float labelW = fontSize * 2.6f, valueW = fontSize * 2.2f;
            float barX = area.x + labelW, barW = area.width - labelW - valueW - fontSize * 0.4f;
            float cy = area.y;
            UiParts.GameLabel(new Rect(area.x, cy, area.width, rowH), "武器", s_barHead);
            cy += rowH;
            // バーは窓が着いてから、上から 1 本ずつ BAR_STAGGER だけ遅れて 0 から右へ伸びる
            for (int i = 0; i < 5; i++) { DrawBar(WEAPON_NAMES[i], f.Levels[i], area.x, barX, barW, valueW, cy, rowH, fontSize, s_barWeapon, BarT(elapsed, i)); cy += rowH; }
            cy += rowH * 0.2f;
            UiParts.GameLabel(new Rect(area.x, cy, area.width, rowH), "術", s_barHead);
            cy += rowH;
            for (int i = 0; i < 6; i++) { DrawBar(MAGIC_NAMES[i], f.Levels[5 + i], area.x, barX, barW, valueW, cy, rowH, fontSize, s_barMagic, BarT(elapsed, 5 + i)); cy += rowH; }
        }

        private static float BarT(float elapsed, int index)
        {
            return Ease(Mathf.Clamp01((elapsed - s_chartStart - ANIM_SECONDS * BAR_START - BAR_STAGGER * index) / BAR_SECONDS));
        }

        private static void DrawBar(string name, int level, float x, float barX, float barW, float valueW, float cy, float rowH, int fontSize, Texture2D fill, float t)
        {
            UiParts.GameLabel(new Rect(x, cy, barX - x, rowH), name, s_barLabel);
            float bh = Mathf.Round(rowH * 0.5f);
            Rect track = new Rect(barX, Mathf.Round(cy + (rowH - bh) / 2f), barW, bh);
            Color col = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(track, s_barTrack, ScaleMode.StretchToFill, true);
            if (level > 0)
            {
                float w = Mathf.Max(2f, barW * Mathf.Clamp01(level / (float)LEVEL_MAX) * t);
                GUI.DrawTexture(new Rect(track.x, track.y, w, bh), fill, ScaleMode.StretchToFill, true);
            }
            GUI.color = col;
            int shown = Mathf.RoundToInt(level * t);
            UiParts.GameLabel(new Rect(track.xMax + fontSize * 0.4f, cy, valueW, rowH), level > 0 ? shown.ToString() : "－", level > 0 ? s_barLabel : s_barDim);
        }

        private static string Lv(int v) { return v > 0 ? v.ToString() : "－"; }

        // ---------------------------------------------------------------- レーダーチャートのテクスチャ

        private const int RADAR_PX = 384;            // テクスチャの一辺 (2x2 で平均して縁を滑らかに)
        private const float RADAR_R = 0.86f;         // 外周の六角形の半径 (テクスチャの半分に対する比)

        private static void BuildRadar(Info f)
        {
            try
            {
                f.RadarGrid = MakeRadarTexture(f, true);
                f.RadarFill = MakeRadarTexture(f, false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Recruit] radar failed: " + e.Message);
            }
        }

        /// <summary>
        /// grid = 軸 + 目盛りの六角形 (15 / 20 / 25)、それ以外 = 能力の六角形 (塗り + 縁取り)。
        /// 全画素の総当たりは遅かった (384² × 4 サンプル × 線 24 本で開くまで 1 秒以上) ので、線は線に沿って丸い点を打ち、
        /// 塗りは走査線ごとに左右の交点の間を埋める (数万回の演算で済む)。
        /// </summary>
        private static Texture2D MakeRadarTexture(Info f, bool grid)
        {
            int n = RADAR_PX;
            float half = n / 2f;
            float rOut = half * RADAR_R;
            float[] ax = new float[6], ay = new float[6];
            for (int i = 0; i < 6; i++)
            {
                float ang = (-90f + 60f * i) * Mathf.Deg2Rad;
                ax[i] = Mathf.Cos(ang); ay[i] = Mathf.Sin(ang);
            }
            Color gridCol = new Color(0.25f, 0.18f, 0.1f, 0.5f);
            Color axisCol = new Color(0.25f, 0.18f, 0.1f, 0.32f);
            Color fillCol = new Color(0.78f, 0.22f, 0.12f, 0.5f);
            Color edgeCol = new Color(0.55f, 0.1f, 0.05f, 0.95f);
            float lineW = n / 160f, edgeW = n / 130f;   // 赤の縁取りは n/90 では太すぎた (ユーザー 2026-10-18)
            Color[] px = new Color[n * n];   // 上の行から。最後に上下を入れ替えて Texture2D に
            if (grid)
            {
                for (int i = 0; i < 6; i++) StampLine(px, n, half, half, half + ax[i] * rOut, half + ay[i] * rOut, lineW * 0.7f, axisCol);
                for (int ring = 1; ring <= RADAR_RINGS; ring++)
                {
                    float rr = rOut * ring / RADAR_RINGS;
                    float w = lineW * (ring == RADAR_RINGS ? 1.3f : 0.8f);
                    for (int i = 0; i < 6; i++)
                    {
                        int j = (i + 1) % 6;
                        StampLine(px, n, half + ax[i] * rr, half + ay[i] * rr, half + ax[j] * rr, half + ay[j] * rr, w, gridCol);
                    }
                }
            }
            else
            {
                float[] vx = new float[6], vy = new float[6];
                for (int i = 0; i < 6; i++)
                {
                    // 中心 0、外周 25 の線形 (値 13 なら半径の 52 %)。素質は 0..15 なので表示値は 10 以上 = 半径 40 % より内には入らない
                    float v = Mathf.Clamp01((STAT_MIN + f.Stats[RADAR_ORDER[i]]) / (float)STAT_MAX);
                    vx[i] = half + ax[i] * rOut * v; vy[i] = half + ay[i] * rOut * v;
                }
                FillConvex(px, n, vx, vy, fillCol);
                for (int i = 0; i < 6; i++)
                {
                    int j = (i + 1) % 6;
                    StampLine(px, n, vx[i], vy[i], vx[j], vy[j], edgeW, edgeCol);
                }
            }
            Color32[] outPx = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    outPx[(n - 1 - y) * n + x] = px[y * n + x];
            Texture2D t = new Texture2D(n, n, TextureFormat.ARGB32, false);
            t.name = grid ? "RecruitRadarGrid" : "RecruitRadarFill";
            t.hideFlags = HideFlags.HideAndDontSave;
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            t.SetPixels32(outPx);
            t.Apply();
            return t;
        }

        /// <summary>線分に沿って半径 r の丸い点を打つ (縁 1 画素は距離で薄くする)。重なりは濃い方を残す (max) ので何度打っても濃くならない。</summary>
        private static void StampLine(Color[] px, int n, float x0, float y0, float x1, float y1, float r, Color col)
        {
            float dx = x1 - x0, dy = y1 - y0;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.5f));
            int rad = Mathf.CeilToInt(r + 1f);
            for (int s = 0; s <= steps; s++)
            {
                float cx = x0 + dx * s / steps, cy = y0 + dy * s / steps;
                int ix0 = Mathf.Max(0, Mathf.FloorToInt(cx - rad)), ix1 = Mathf.Min(n - 1, Mathf.CeilToInt(cx + rad));
                int iy0 = Mathf.Max(0, Mathf.FloorToInt(cy - rad)), iy1 = Mathf.Min(n - 1, Mathf.CeilToInt(cy + rad));
                for (int y = iy0; y <= iy1; y++)
                {
                    for (int x = ix0; x <= ix1; x++)
                    {
                        float ddx = x + 0.5f - cx, ddy = y + 0.5f - cy;
                        float d = Mathf.Sqrt(ddx * ddx + ddy * ddy);
                        float cov = Mathf.Clamp01(r + 0.5f - d);
                        if (cov <= 0f) continue;
                        float a = col.a * cov;
                        int k = y * n + x;
                        if (a > px[k].a) px[k] = new Color(col.r, col.g, col.b, a);
                    }
                }
            }
        }

        private const int BUBBLE_TAIL = 6;                                   // 尾の長さ (マス)。店の吹き出しと同じ
        private static readonly int[] BUBBLE_CORNER = { 3, 1, 1 };            // 角の欠き (端の行から 1 行ごとに何マス欠くか)。店の吹き出しと同じ半径 5 マス相当

        /// <summary>
        /// 台詞の吹き出し。店の E / ▲N の吹き出し (ShopBubble) と同じ作り: 1 マス = 1 テクセルのドット絵、生成り (243,243,217) の 3/4 の不透明度、
        /// 縁の線は無し、角は段で欠く、尾は下辺の tailCell から下に伸びる三角 (先は左寄り)。Point で拡大して貼る。
        /// </summary>
        private static Texture2D MakeBubbleTexture(int cellsW, int cellsH, int tail, int tailCell)
        {
            int bodyH = cellsH - tail;
            if (cellsW <= 0 || bodyH <= BUBBLE_CORNER.Length * 2) return null;
            Color32 face = new Color32(243, 243, 217, 191);
            Color32 clear = new Color32(0, 0, 0, 0);
            Color32[] px = new Color32[cellsW * cellsH];
            for (int i = 0; i < px.Length; i++) px[i] = clear;
            // 地 (上の行から y = 0)。角の欠きは端の行ほど大きい
            for (int y = 0; y < bodyH; y++)
            {
                int fromTop = y, fromBottom = bodyH - 1 - y;
                int edge = Mathf.Min(fromTop, fromBottom);
                int cut = edge < BUBBLE_CORNER.Length ? BUBBLE_CORNER[edge] : 0;
                for (int x = cut; x < cellsW - cut; x++) px[(cellsH - 1 - y) * cellsW + x] = face;
            }
            // 尾: 底辺 tail マスの左端 (= 先) を絵の中心の 4 マス右に置き、1 行ごとに右から 1 マスずつ細くなる (先は絵の中心より少し右。ユーザー調整 2026-10-18)
            int baseL = Mathf.Clamp(tailCell + 4, 1, cellsW - tail - 1);
            for (int k = 0; k < tail; k++)
            {
                int y = bodyH + k;
                int x0 = baseL, x1 = baseL + tail - 1 - k;   // 右端が縮む (先は左の根元に残る)
                for (int x = x0; x <= x1 && x < cellsW; x++) px[(cellsH - 1 - y) * cellsW + x] = face;
            }
            Texture2D t = new Texture2D(cellsW, cellsH, TextureFormat.ARGB32, false);
            t.name = "RecruitBubble";
            t.hideFlags = HideFlags.HideAndDontSave;
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        /// <summary>凸多角形を走査線で塗る (行ごとに辺との交点の最小・最大の間)。縁は後から打つ線に任せる。</summary>
        private static void FillConvex(Color[] px, int n, float[] vx, float[] vy, Color col)
        {
            int m = vx.Length;
            for (int y = 0; y < n; y++)
            {
                float sy = y + 0.5f;
                float xmin = float.MaxValue, xmax = float.MinValue;
                for (int i = 0; i < m; i++)
                {
                    int j = (i + 1) % m;
                    float y0 = vy[i], y1 = vy[j];
                    if ((sy < y0 && sy < y1) || (sy >= y0 && sy >= y1) || y0 == y1) continue;
                    float x = vx[i] + (vx[j] - vx[i]) * (sy - y0) / (y1 - y0);
                    if (x < xmin) xmin = x;
                    if (x > xmax) xmax = x;
                }
                if (xmin > xmax) continue;
                int ix0 = Mathf.Max(0, Mathf.RoundToInt(xmin)), ix1 = Mathf.Min(n - 1, Mathf.RoundToInt(xmax) - 1);
                for (int x = ix0; x <= ix1; x++) px[y * n + x] = col;
            }
        }

        private static void EnsureStyles(int fontSize)
        {
            if (s_fontSizeBuilt == fontSize && s_line != null) return;
            s_fontSizeBuilt = fontSize;
            Font gf = UiParts.GameFont();
            Plugin.Log.LogInfo(gf != null ? string.Format("[Recruit] applied: game font {0} size={1} dynamic={2} (window font {3})", gf.name, gf.fontSize, gf.dynamic, fontSize) : "[Recruit] game font unavailable, OS font used");
            if (s_font == null)
            {
                try { s_font = Font.CreateDynamicFontFromOSFont(new[] { "Meiryo UI", "Yu Gothic UI", "MS UI Gothic" }, fontSize); }   // 本体のフォント (UiParts.GameLabel) が取れない時の予備
                catch (Exception e) { Plugin.Log.LogWarning("[Recruit] OS font unavailable: " + e.Message); }
            }
            s_box = new GUIStyle(GUI.skin.box);
            Texture2D t = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            t.SetPixel(0, 0, new Color(0.18f, 0.18f, 0.2f, 0.94f));
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            s_box.normal.background = t;
            s_line = new GUIStyle(GUI.skin.label);
            s_line.fontSize = fontSize;
            s_line.alignment = TextAnchor.MiddleLeft;
            s_line.wordWrap = false;
            s_line.normal.textColor = new Color(0.12f, 0.08f, 0.05f);
            if (s_font != null) s_line.font = s_font;
            s_dim = new GUIStyle(s_line);
            s_dim.normal.textColor = new Color(0.4f, 0.35f, 0.3f);
            s_hint = new GUIStyle(s_dim);
            s_hint.fontSize = Mathf.Max(10, fontSize - 2);
            s_title = new GUIStyle(s_line);
            s_title.fontStyle = FontStyle.Bold;
            s_title.fontSize = fontSize + 4;
            s_name = new GUIStyle(s_title);
            s_name.fontSize = fontSize + fontSize / 2;
            s_head = new GUIStyle(s_title);
            s_head.fontSize = fontSize + 1;
            s_accent = new GUIStyle(s_line);
            s_accent.fontStyle = FontStyle.Bold;
            s_accent.normal.textColor = new Color(0.55f, 0.1f, 0.05f);
            s_center = new GUIStyle(s_line);
            s_center.alignment = TextAnchor.MiddleCenter;
            s_center.fontSize = Mathf.Max(10, fontSize - 2);
            s_speech = new GUIStyle(s_line);
            s_speech.fontSize = Mathf.Max(10, fontSize - 2);
            s_speech.normal.textColor = new Color(60f / 255f, 49f / 255f, 39f / 255f);   // 本体の会話窓の文字の焦げ茶
            if (s_barTrack == null)
            {
                s_barTrack = Solid(new Color(0.1f, 0.07f, 0.04f, 0.16f));
                s_barWeapon = Solid(new Color(0.16f, 0.32f, 0.58f, 0.9f));
                s_barMagic = Solid(new Color(0.46f, 0.18f, 0.58f, 0.9f));
            }
            UiParts.ResetStyles();
        }
    }

    /// <summary>
    /// 会話窓に出た文字を窓ごとに覚えておき、選択肢の前にこの人が言っていた台詞を取り出す。
    /// 本体は文字を put_moji2 で 1 文字ずつ窓の画像に描くだけで文字列は残さない。勧誘の台詞は people / human の窓に出て、PAUSE のあと
    /// close_wind で閉じられてから選択肢の窓が開く (msg 879 等) ので、閉じた窓の文字も「最後に閉じた窓」として残す。
    /// 選択肢の行 (select_switch != 0) は入れない。新しい会話 (message_start で mess_flag == 0) で全部消す。
    /// </summary>
    internal static class SpeechCapture
    {
        private const int MAX_LINES = 4;      // 会話窓の最大の行数
        private const int PEOPLE = 1;         // wind_which 1 = people_window (NPC 側)。0 = human_window (皇帝側。選択肢もこちら)
        private static readonly StringBuilder[] s_buf = { new StringBuilder(), new StringBuilder() };
        private static readonly string[] s_lastClosed = { "", "" };

        internal static void OnChar(Core c)
        {
            if (c.select_switch != 0) return;
            int code = c.work_a[0];
            if (code >= 8192) code -= 7936;
            int w = c.wind_which & 1;
            if (code == 230) { s_buf[w].Append(' '); return; }
            string tbl = Window.fonttbl;
            if (code > 0 && tbl != null && code < tbl.Length) s_buf[w].Append(tbl[code]);
        }

        internal static void OnNewline(Core c) { s_buf[c.wind_which & 1].Append('\n'); }

        internal static void OnWindowOpen(Core c) { s_buf[c.wind_which & 1].Length = 0; }

        internal static void OnWindowClose(Core c)
        {
            int w = c.wind_which & 1;
            string s = Trim(s_buf[w].ToString());
            if (s.Length > 0) s_lastClosed[w] = s;
            s_buf[w].Length = 0;
        }

        internal static void OnNewConversation()
        {
            s_buf[0].Length = 0; s_buf[1].Length = 0; s_lastClosed[0] = ""; s_lastClosed[1] = "";
        }

        /// <summary>
        /// この人 (NPC) の台詞 = people の窓の文字。開いていればその文字、閉じていれば最後に閉じた時の文字。
        /// 皇帝側 (human) の窓は選択肢の窓でもあり、「○○、よろしくたのむぞ！」のような皇帝の台詞が残っている (最初の版で拾ってしまった) ので、
        /// people に何も無い時だけ使う。
        /// </summary>
        internal static string Current(int selectWindow)
        {
            string s = Trim(s_buf[PEOPLE].ToString());
            if (s.Length > 0) return s;
            if (s_lastClosed[PEOPLE].Length > 0) return s_lastClosed[PEOPLE];
            int h = PEOPLE ^ 1;
            s = Trim(s_buf[h].ToString());
            if (s.Length > 0) return s;
            return s_lastClosed[h];
        }

        /// <summary>前後の空白と空行を落とし、最後の MAX_LINES 行だけにする。</summary>
        private static string Trim(string s)
        {
            string[] parts = s.Replace("\r", "").Split('\n');
            List<string> lines = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string l = parts[i].Trim();
                if (l.Length > 0) lines.Add(l);
            }
            if (lines.Count > MAX_LINES) lines.RemoveRange(0, lines.Count - MAX_LINES);
            return string.Join("\n", lines.ToArray());
        }
    }

    [HarmonyPatch(typeof(Core), "put_moji2")]
    internal static class SpeechCaptureCharPatch
    {
        private static void Postfix(Core __instance) { try { SpeechCapture.OnChar(__instance); } catch { } }
    }

    [HarmonyPatch(typeof(Core), "kaigyo_sub")]
    internal static class SpeechCaptureNewlinePatch
    {
        private static void Prefix(Core __instance) { SpeechCapture.OnNewline(__instance); }
    }

    [HarmonyPatch(typeof(Core), "normal_set_window")]
    internal static class SpeechCaptureOpenPatch
    {
        private static void Postfix(Core __instance) { SpeechCapture.OnWindowOpen(__instance); }
    }

    // close_wind_sub は wind_which を切り替えるので、その前 (Prefix) に今の窓の文字を取る
    [HarmonyPatch(typeof(Core), "close_wind_sub")]
    internal static class SpeechCaptureClosePatch
    {
        private static void Prefix(Core __instance) { SpeechCapture.OnWindowClose(__instance); }
    }

    [HarmonyPatch(typeof(Core), "message_start")]
    internal static class SpeechCaptureStartPatch
    {
        private static void Prefix(Core __instance) { if (__instance.mess_flag == 0) SpeechCapture.OnNewConversation(); }
    }

    // 会話の終わり (ok_mes_finish) でも消す。前の会話の台詞が次の勧誘に残らないように
    [HarmonyPatch(typeof(Core), "ok_mes_finish")]
    internal static class SpeechCaptureFinishPatch
    {
        private static void Postfix() { SpeechCapture.OnNewConversation(); }
    }

    [HarmonyPatch(typeof(Core), "select_sa")]
    internal static class RecruitInfoSelectSaPatch
    {
        private static void Postfix(Core __instance) { RecruitInfo.AfterSelectSa(__instance); }
    }

    // DialogueAdvanceSelectPatch (決定をエッジにする) の後に見るため優先度を下げる。窓を開いている間だけ本体を止める (Prefix を使う理由)
    [HarmonyPatch(typeof(Core), "put_select")]
    [HarmonyPriority(Priority.Low)]
    internal static class RecruitInfoPutSelectPatch
    {
        private static bool Prefix(Core __instance) { return RecruitInfo.BeforePutSelect(__instance); }
        private static void Postfix(Core __instance) { RecruitInfo.AfterPutSelect(__instance); }
    }

    /// <summary>
    /// Menu.proc_status (ステータス画面の入力): 決定ボタンで表示中の仲間の窓を開く ([Recruit] PartyStatus)。
    /// 本体はこの画面で決定に役目が無い (キャンセルで戻る、とじる (Y) で閉じる、LB / RB で前後の仲間、タッチの l_btn / r_btn)。
    /// 窓が開いている間は Prefix で本体の処理を止める (パッドの入力は RecruitInfo.Tick が 0 にしているが、タッチ / クリックの l_btn / r_btn は別経路)。
    /// </summary>
    [HarmonyPatch(typeof(Menu), "proc_status")]
    internal static class RecruitInfoStatusScreenPatch
    {
        private static bool Prefix() { return !RecruitInfo.IsOpen; }
        private static void Postfix(Menu __instance) { RecruitInfo.AfterProcStatus(__instance); }
    }
}
