using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 弔いの鍛冶: 皇帝が死んで継承すると 1 回ぶん「ためる」。開発所で武器 / 防具の開発を頼んだとき、たまっていれば国庫を使わずに開発できる
    /// (亡き陛下を弔って職人が腕をふるう)。国庫の額は一切動かさない (死ぬほど国力が上がる形にしない)。
    ///
    /// 本体の仕組み (tools/script-analysis で script.bin を読んだ結果):
    /// - 開発所の会話 (武器 msg 2897 / 防具 2898): 「〜を開発したいと/思います。つきましては、/」 put_10_hidari(2) 「００００○」 work0=133
    ///   APPNEW 225 「クラウンほど/使わせていただけると‥‥」 PAUSE budget_sep(3) → 足りなければ msg 2899 / 2900 (「無い物ねだりですな」)。
    ///   足りれば 選択肢「よろしい！」「そんなに出せるか！」→ よろしい で msg 2901 / 2902: people_window window_small 「/頑張ってやります！」
    ///   work0,1=56,5 APPNEW 241 (完成まで 5 戦) flag115 |= 4 / 8 work2=0 sub_budget(2)。
    ///   費用は koukan_work[2] (下位、表示用 = (段階 + 1) × 2) + [3] × 256 + [4] × 65536 (= (段階 + 1) × 200,704 ≒ 40 万 / 80 万 / 120 万)。
    ///   budget_sep は国庫 / 256 と work[3] + work[4] × 256 を比べ、sub_budget は 3 バイトの額を国庫から引く。
    /// - 開発できるのは年代 (年代ジャンプ) ごとに各種類 1 回 (開発するとフラグが偶数になり「思い付きません」)。この上限は触らないので、
    ///   死をいくら重ねても 1 年代で手に入るのは各種類の試作品 1 本ずつまで。
    /// - 死による継承は 2 経路: 全滅 (msg 2058 → 2834 → Core.koutei_select) と、勝ったが皇帝の LP が 0 (msg 2059 → Core.koutei_select2)。
    ///   koutei_select は年代ジャンプ (msg 2832 → 2834) からも呼ばれるので、全滅かどうかは Battle.dest で partydata[129] == 2 を控えて見分ける。
    ///   最終皇帝は継承しないので増えない。年代ジャンプでは増えない。
    /// - サブレベル (ScriptText) で文を差し込むと戻った時に mess_no が -1 になるので、会話の中の位置は mess_no ではなく
    ///   読み位置 (Core.mess_adrs) で見分ける。位置はゲーム内の script.bin のポインタ表から探し、バイト列が想定と違えば本体のまま (Enabled でも何もしない)。
    ///
    /// MOD:
    /// - message_r の Postfix: msg 2897 / 2898 の頭なら、本文を「を開発したいと/思います。金は要りません。/先帝の御心に報いる一作、/
    ///   我らの腕で作らせてください‥‥」に差し替えて PAUSE まで飛ばす (本体の数字と「クラウンほど…」は出さない)。
    ///   ここで「この開発は無料」の状態 (s_armed) にする。msg 2832 (年代ジャンプ) ならためた回数を 0 に戻す。
    /// - budget_sep の Prefix: 無料なら費用 (koukan_work[2..4]) を 0 にして判定を通す。
    /// - flash_select の Postfix: 無料なら選択肢を「頼む！」「今はよい」に差し替える (select_pos → 文字 → 改行 → select_pos → 文字 をサブレベルで)。
    /// - window_small_set の Postfix: 承諾後の「頑張ってやります！」を「亡き陛下の魂に誓って、/必ず仕上げます！」に差し替える。
    /// - sub_budget の Prefix: 無料ならここで 1 回ぶん使う (費用 0 なので国庫は減らない)。
    /// - 回数は本体のセーブに入る予備の領域 (partydata[1900]。Work.PARAY_flag_reserve 1823..1949 のうち本体は 1823..1826 しか使わない。
    ///   セーブ表の (1, 127, 1823) で保存される) に 1 バイトで持つ。セーブ・ロード・新規ゲームは本体に任せる。
    /// </summary>
    internal static class MourningForge
    {
        public static ConfigEntry<bool> Enabled;
        /// <summary>ためられる上限。</summary>
        public static ConfigEntry<int> MaxStock;

        public const string NAME = "弔いの鍛冶";
        private const string TAG = "[Mourning]";
        private const int STOCK_SLOT = 1900;        // partydata の予備領域 (セーブに入る)。本体は使っていない
        private const int RESULT = 129, RESULT_WIPE = 2;
        private const int MIN_MAX = 1, MAX_MAX = 99;

        // 差し込む文 (本体の字形表にある字だけ。弔・鍛・冶・職・費・品 は無いので「一品」は「一作」)。
        // 会話の 1 行目は呼び出し側が「新しい剣」「新しいかぶと」まで出してから msg 2897 / 2898 に入るので、本文の頭 (「を開発したいと…」) から差し替える
        private const string TEXT_OFFER_W = "を開発したいと\n思います。金は要りません。\n先帝の御心に報いる一作、\n我らの腕で作らせてください‥‥";
        private const string TEXT_OFFER_A = "の開発をしたい\nと思います。金は要りません。\n先帝の御心に報いる一作、\n我らの腕で作らせてください‥‥";
        private const string TEXT_YES = "頼む！";
        private const string TEXT_NO = "今はよい";
        private const string TEXT_ACCEPT = "亡き陛下の魂に誓って\n必ず仕上げます！";   // 本体は頭に改行 (1 行目を空ける) が、こちらは 1〜2 行目に出す (ユーザーの指定)

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("MourningForge", "Enabled", false,
                "弔いの鍛冶: 皇帝が死んで継承すると 1 回ぶんたまり、開発所で開発を頼んだとき国庫を使わずに開発できる (職人が亡き陛下のために腕をふるう)。"
                + "年代ジャンプ (「N年後」) では増えず、たまっていた回数も 0 に戻る。開発は年代ごとに各種類 1 回のまま。既定 Off = 本体どおり");
            MaxStock = config.Bind("MourningForge", "MaxStock", 9,
                new ConfigDescription("ためられる上限 (回)", new AcceptableValueRange<int>(MIN_MAX, MAX_MAX)));
        }

        public static bool On { get { return Enabled != null && Enabled.Value; } }
        public static int Max { get { return MaxStock != null ? MaxStock.Value : 9; } }

        /// <summary>今ためている回数 (セーブに入る)。</summary>
        public static int Stock
        {
            get { return Work.partydata != null && Work.partydata.Length > STOCK_SLOT ? Work.partydata[STOCK_SLOT] & 0xFF : 0; }
            set
            {
                if (Work.partydata == null || Work.partydata.Length <= STOCK_SLOT) return;
                if (value < 0) value = 0;
                if (value > 255) value = 255;
                Work.partydata[STOCK_SLOT] = value;
            }
        }

        /// <summary>この開発を無料にする状態 (費用の文を差し替えた時に立ち、sub_budget で使う)。</summary>
        private static DevTalk s_armed;
        private static bool s_wipePending;
        private static bool s_resolved, s_resolveFailed;
        private static DevTalk s_weapon, s_armor;

        /// <summary>開発所の会話 1 組 (武器 / 防具) の script.bin 上の位置。</summary>
        private sealed class DevTalk
        {
            public string Name;
            public string Offer;      // 差し替える本文 (msg の頭から PAUSE の手前まで)
            public int Start;         // msg 2897 / 2898 の本文の先頭 (message_r 直後の mess_adrs)
            public int Pause;         // 戻り先: PAUSE (44)
            public int BudgetSub;     // budget_sep のサブ命令 (42) の位置
            public int FlashSelect;   // flash_select (37) の位置
            public int SelectSa;      // 戻り先: select_sa (39)
            public int SmallWindow;   // 承諾後 (msg 2901 / 2902) の window_small (77) の位置
            public int AcceptNext;    // 戻り先: 「頑張ってやります！」の次の命令 (work0,1 = 56,5)
            public int SubBudgetSub;  // sub_budget のサブ命令 (79) の位置
        }

        // 2897 の頭「を開発したいと/思います。つきましては、/」、2898 の頭「の開発をしたい/と思います。それで、/」
        private static readonly byte[] PAT_HEAD_W = { 124, 34, 243, 32, 228, 92, 96, 82, 100, 36, 32, 198, 82, 110, 93, 231, 98, 87, 110, 92, 99, 106, 232, 36 };
        private static readonly byte[] PAT_HEAD_A = { 105, 34, 243, 32, 228, 124, 92, 96, 82, 36, 100, 32, 198, 82, 110, 93, 231, 95, 121, 197, 232, 36 };
        // 続く put_10_hidari(2) 「００００○」 work0=133 APPNEW 225
        private static readonly byte[] PAT_PUT = { 79, 67, 2, 35, 0, 35, 0, 35, 0, 35, 0, 35, 0, 64, 0, 133, 13, 225 };
        private static readonly byte[] PAT_PAUSE_BUDGET = { 44, 79, 42, 3 };                                   // PAUSE budget_sep(3)
        private static readonly byte[] PAT_SELECT = { 77, 40, 37, 38, 117, 122, 92, 82, 234, 36, 38, 95, 125, 101, 102, 253, 94, 120, 86, 234, 39 };
        // window_small human_window flash_select select_pos「よろしい！」改行 select_pos「そんなに出せるか！」select_sa
        // 2901 / 2902: people_window window_small 改行「頑張ってやります！」 work0,1 = 56,5
        private static readonly byte[] PAT_ACCEPT = { 41, 77, 36, 34, 200, 34, 5, 129, 99, 115, 119, 110, 93, 234, 65, 0, 56, 5 };
        private static readonly byte[] PAT_SUB_BUDGET = { 64, 2, 0, 79, 79, 2 };                                // work2 = 0 sub_budget(2)

        /// <summary>script.bin の中の位置を探す (最初の 1 回)。見つからなければ機能を止める。</summary>
        private static bool Resolve(Core c)
        {
            if (s_resolved) return !s_resolveFailed;
            if (c == null || c.mestbl == null || c.mestbl[0] == null) return false;   // まだ読み込み前。次の機会に
            s_resolved = true;
            s_weapon = ResolveOne(c, "weapon", 2897, 2901, PAT_HEAD_W, TEXT_OFFER_W);
            s_armor = ResolveOne(c, "armor", 2898, 2902, PAT_HEAD_A, TEXT_OFFER_A);
            s_resolveFailed = s_weapon == null || s_armor == null;
            if (s_resolveFailed) Plugin.Log.LogWarning(TAG + " script layout differs from the expected one, feature disabled (vanilla development talk)");
            else Plugin.Log.LogInfo(TAG + " script offsets: weapon start@" + s_weapon.Start + " pause@" + s_weapon.Pause + " budget@" + s_weapon.BudgetSub + " select@" + s_weapon.FlashSelect
                + " accept@" + s_weapon.SmallWindow + " sub@" + s_weapon.SubBudgetSub + "; armor start@" + s_armor.Start + " pause@" + s_armor.Pause + " budget@" + s_armor.BudgetSub
                + " select@" + s_armor.FlashSelect + " accept@" + s_armor.SmallWindow + " sub@" + s_armor.SubBudgetSub);
            return !s_resolveFailed;
        }

        private static DevTalk ResolveOne(Core c, string name, int offerMsg, int acceptMsg, byte[] head, string offer)
        {
            byte[] s = c.mestbl[0];
            int a, b;
            if (!ScriptText.Range(c, offerMsg, out a, out b)) { Plugin.Log.LogWarning(TAG + " msg " + offerMsg + " range not found"); return null; }
            if (!ScriptText.Match(s, a, head)) { Plugin.Log.LogWarning(TAG + " msg " + offerMsg + ": opening line differs"); return null; }
            int put = a + head.Length;
            if (!ScriptText.Match(s, put, PAT_PUT)) { Plugin.Log.LogWarning(TAG + " msg " + offerMsg + ": cost line not found"); return null; }
            int pause = ScriptText.Find(s, put + PAT_PUT.Length, b, PAT_PAUSE_BUDGET);
            if (pause < 0 || pause - (put + PAT_PUT.Length) > 40) { Plugin.Log.LogWarning(TAG + " msg " + offerMsg + ": PAUSE / budget_sep not found"); return null; }
            int sel = pause + PAT_PAUSE_BUDGET.Length + 2;                                        // budget_sep の飛び先 2 バイトの次
            if (!ScriptText.Match(s, sel, PAT_SELECT)) { Plugin.Log.LogWarning(TAG + " msg " + offerMsg + ": choices differ"); return null; }
            DevTalk t = new DevTalk { Name = name, Offer = offer };
            t.Start = a;
            t.Pause = pause;
            t.BudgetSub = pause + 2;
            t.FlashSelect = sel + 2;
            t.SelectSa = sel + PAT_SELECT.Length - 1;

            if (!ScriptText.Range(c, acceptMsg, out a, out b)) { Plugin.Log.LogWarning(TAG + " msg " + acceptMsg + " range not found"); return null; }
            int acc = ScriptText.Find(s, a, b, PAT_ACCEPT);
            if (acc < 0) { Plugin.Log.LogWarning(TAG + " msg " + acceptMsg + ": accept line not found"); return null; }
            int sub = ScriptText.Find(s, acc + PAT_ACCEPT.Length, b, PAT_SUB_BUDGET);
            if (sub < 0) { Plugin.Log.LogWarning(TAG + " msg " + acceptMsg + ": sub_budget not found"); return null; }
            t.SmallWindow = acc + 1;
            t.AcceptNext = acc + 14;
            t.SubBudgetSub = sub + 4;
            return t;
        }

        private static bool Ready(Core c) { return On && c != null && c.mess_type == 0 && Resolve(c); }

        // ---- 会話の差し替え ----

        /// <summary>開発の会話 (msg 2897 / 2898) の頭で、たまっていれば本文を差し替えて PAUSE まで飛ばし、無料の状態にする。</summary>
        private static void OfferStart(Core c, int msg)
        {
            if (!Ready(c) || Stock <= 0) return;
            DevTalk t = msg == 2897 ? s_weapon : msg == 2898 ? s_armor : null;
            if (t == null || Core.mess_adrs != t.Start) return;
            byte[] body = ScriptText.Encode(t.Offer, TAG);
            if (body == null) return;
            ScriptText.EnterSublevel(c, body, t.Pause);
            s_armed = t;
            Plugin.Log.LogInfo(TAG + " " + t.Name + " development offered for free (stock " + Stock + ")");
        }

        public static void OnBudgetSep(Core c)
        {
            if (s_armed == null || !Ready(c) || Core.mess_adrs != s_armed.BudgetSub) return;
            c.koukan_work[2] = 0; c.koukan_work[3] = 0; c.koukan_work[4] = 0;
            if (Plugin.DiagOn) Plugin.Log.LogInfo(TAG + " cost zeroed for budget_sep");
        }

        public static void OnFlashSelect(Core c)
        {
            if (s_armed == null || !Ready(c) || Core.mess_adrs != s_armed.FlashSelect + 1) return;
            byte[] yes = ScriptText.Encode(TEXT_YES, TAG), no = ScriptText.Encode(TEXT_NO, TAG);
            if (yes == null || no == null) return;
            byte[] body = new byte[1 + yes.Length + 1 + 1 + no.Length];
            int i = 0;
            body[i++] = 38; System.Array.Copy(yes, 0, body, i, yes.Length); i += yes.Length;
            body[i++] = ScriptText.NL;
            body[i++] = 38; System.Array.Copy(no, 0, body, i, no.Length);
            ScriptText.EnterSublevel(c, body, s_armed.SelectSa);
        }

        public static void OnWindowSmall(Core c)
        {
            if (s_armed == null || !Ready(c) || Core.mess_adrs != s_armed.SmallWindow + 1) return;
            byte[] body = ScriptText.Encode(TEXT_ACCEPT, TAG);
            if (body == null) return;
            ScriptText.EnterSublevel(c, body, s_armed.AcceptNext);
        }

        public static void OnSubBudget(Core c)
        {
            if (s_armed == null || !Ready(c) || Core.mess_adrs != s_armed.SubBudgetSub) return;
            c.koukan_work[2] = 0; c.koukan_work[3] = 0; c.koukan_work[4] = 0;    // 念のため (budget_sep で 0 にしてある)
            int before = Stock;
            Stock = before - 1;
            Plugin.Log.LogInfo(TAG + " " + s_armed.Name + " development started for free: stock " + before + " -> " + Stock);
            s_armed = null;
            Wallet.ShowNotice("亡き陛下のため、職人が腕をふるう (残り " + Stock + " 回)", NAME, false, 4f);
        }

        /// <summary>会話が開発所のもの以外に移ったら無料の状態を捨てる (「今はよい」で断った後など)。</summary>
        public static void OnMessageStart(Core c)
        {
            int msg = c.mess_no;
            if (msg == 2897 || msg == 2898) { s_armed = null; OfferStart(c, msg); return; }   // 無料にするかは毎回この頭で決め直す
            if (msg == 2832)
            {
                // 年代ジャンプ (「N年後」): この継承は数えない (全滅の控えが残っていても捨てる)。
                // たまっている回数も 0 に戻す (年月が流れ、職人の世代も変わる。亡き陛下のための仕事は持ち越さない)
                s_wipePending = false;
                if (On && Stock > 0)
                {
                    Plugin.Log.LogInfo(TAG + " generation jump: stock " + Stock + " -> 0");
                    Stock = 0;
                    Wallet.ShowNotice("年月が流れ、職人の世代も変わった (残り 0 回)", NAME, false, 4f);
                }
            }
            if (s_armed == null) return;
            if (msg >= 2897 && msg <= 2902) return;
            s_armed = null;
        }

        // ---- ためる ----

        /// <summary>死の演出 (MourningScene) で既に増やした (本体の koutei_select / koutei_select2 では増やさない)。</summary>
        private static bool s_sceneGranted;

        public static void OnBattleStart() { s_wipePending = false; s_sceneGranted = false; MourningScene.Reset(); }

        public static void OnBattleEnd()
        {
            int result = Work.partydata != null && Work.partydata.Length > RESULT ? Work.partydata[RESULT] : -1;
            s_wipePending = result == RESULT_WIPE;
        }

        public static void OnReset() { s_wipePending = false; s_sceneGranted = false; s_armed = null; MourningScene.Reset(); }

        /// <summary>
        /// 死による継承がこの後に来る (死の演出を出す条件): 全滅の控えがあるか、皇帝の LP (memberdata[0][18]) が 0。
        /// 勝ったが皇帝の LP 0 の経路 (msg 2059) は hp_full の後に koutei_select2 なので、暗転の時点では LP がまだ 0。
        /// </summary>
        public static bool DeathPending
        {
            get
            {
                if (s_wipePending) return true;
                int[] m = Work.memberdata != null && Work.memberdata.Length > 0 ? Work.memberdata[0] : null;
                return m != null && m.Length > 18 && (m[18] & 0xFF) == 0;
            }
        }

        /// <summary>死の演出の窓を開く時に増やし、窓に出す文を返す。本体の継承の Postfix では二重に増やさない。</summary>
        public static string GrantForScene()
        {
            s_sceneGranted = true;
            return Grant("death scene", false);
        }

        /// <summary>皇帝の LP が 0 で勝った後の継承 (Core.koutei_select2)。</summary>
        public static void OnEmperorDeath()
        {
            if (s_sceneGranted) { s_sceneGranted = false; s_wipePending = false; return; }
            Grant("emperor LP 0", true);
        }

        /// <summary>継承の選択 (Core.koutei_select)。全滅の直後だけ数える (年代ジャンプからも呼ばれる)。</summary>
        public static void OnSuccession()
        {
            if (s_sceneGranted) { s_sceneGranted = false; s_wipePending = false; return; }
            if (!s_wipePending) return;
            s_wipePending = false;
            Grant("party wiped", true);
        }

        /// <summary>1 回ぶん増やして知らせの文を返す。notice なら画面上部の知らせにも出す (演出で出す時は出さない)。</summary>
        private static string Grant(string why, bool notice)
        {
            if (!On) return "";
            int before = Stock;
            string text;
            if (before >= Max)
            {
                Plugin.Log.LogInfo(TAG + " " + why + ": stock already at max " + Max);
                text = "亡き陛下を弔い、職人が腕で報いると誓う (残り " + Stock + " 回 = 上限)";
            }
            else
            {
                Stock = before + 1;
                Plugin.Log.LogInfo(TAG + " " + why + ": stock " + before + " -> " + Stock);
                text = "亡き陛下を弔い、職人が腕で報いると誓う (残り " + Stock + " 回)";
            }
            if (notice && before < Max) Wallet.ShowNotice(text, NAME, false, 5f);
            return text;
        }

        // ---- 設定画面用 ----

        public static string StockLabel() { return Work.partydata == null ? "-" : Stock + " 回"; }
        public static string MaxLabel() { return Max + " 回"; }
        public static void CycleMax(int d)
        {
            if (MaxStock == null) return;
            int v = MaxStock.Value + d;
            if (v < MIN_MAX) v = MIN_MAX;
            if (v > MAX_MAX) v = MAX_MAX;
            MaxStock.Value = v;
        }

        public static void LogPatched()
        {
            Plugin.Log.LogInfo(TAG + " patched: Core.message_r / budget_sep / flash_select / window_small_set / sub_budget / koutei_select / koutei_select2 / routine_reset / fade_out_on_message (death scene), Battle.init / dest");
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.budget_sep))]
    internal static class MourningForge_budget_sep_Patch
    {
        private static void Prefix(Core __instance) { MourningForge.OnBudgetSep(__instance); }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.flash_select))]
    internal static class MourningForge_flash_select_Patch
    {
        private static void Postfix(Core __instance) { MourningForge.OnFlashSelect(__instance); }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.window_small_set))]
    internal static class MourningForge_window_small_set_Patch
    {
        private static void Postfix(Core __instance) { MourningForge.OnWindowSmall(__instance); }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.sub_budget))]
    internal static class MourningForge_sub_budget_Patch
    {
        private static void Prefix(Core __instance) { MourningForge.OnSubBudget(__instance); }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.message_r))]
    internal static class MourningForge_message_r_Patch
    {
        private static void Postfix(Core __instance) { MourningForge.OnMessageStart(__instance); }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.koutei_select))]
    internal static class MourningForge_koutei_select_Patch
    {
        private static void Postfix() { MourningForge.OnSuccession(); }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.koutei_select2))]
    internal static class MourningForge_koutei_select2_Patch
    {
        private static void Postfix() { MourningForge.OnEmperorDeath(); }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.routine_reset))]
    internal static class MourningForge_routine_reset_Patch
    {
        private static void Postfix() { MourningForge.OnReset(); }
    }

    [HarmonyPatch(typeof(Battle), "init")]
    internal static class MourningForge_Battle_init_Patch
    {
        private static void Postfix() { MourningForge.OnBattleStart(); }
    }

    [HarmonyPatch(typeof(Battle), "dest")]
    internal static class MourningForge_Battle_dest_Patch
    {
        private static void Postfix() { MourningForge.OnBattleEnd(); }
    }
}
