using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 敵が強くなる速さを % で調整し、止められるようにする。
    ///
    /// 本体の仕組み:
    /// - 敵の強さの元は Work.partydata[686] = 「戦闘回数カウンタ」(0〜65535、セーブに入る。新規ゲームで 0)。
    ///   増えるのは Core.btl_counter_add (イベント命令 28) だけで、戦闘後のスクリプトが koukan_work の値 (通常 1) を足す。乱数は入らない。
    ///   追憶の迷宮 (mapno >= 496) では Work.checkBCRank が、その階層の上限ランクを超えていたら加算を 0 にする。
    /// - 戦闘の敵決めでは Battle.check_limit が Work.getBattleCount() (通常はそのカウンタ、追憶の迷宮は階層のランク帯から乱数、
    ///   mapno 494/495 は固定 1225) を読み、my_birth = min(値, 1224) → get_X で使い捨ての
    ///   X = my_birth + rand(0 〜 my_birth*9/8) (Data.mon_birth_const = 9 8 1 1 0 30。X はカウンタの 1〜2.125 倍に散らばる) を求め、
    ///   出現系統 (16 系統) ごとの 16 段のしきい値表 (Data.main_mon_order、系統ごとに別の値) で「X 以下の最初の段」を選ぶ。
    ///   カウンタがしきい値に達するとそれより弱い段は出なくなり (下限保証)、達する前から乱数の上振れで時々出る (出始め = しきい値 ÷ 2.125)。
    ///   段ごとのしきい値の 16 系統平均 (RANK_FLOORS): 段 2 = 55, 3 = 122, 4 = 231, 5 = 320, 6 = 432, 7 = 570, 8 = 683, 9 = 827, 10 = 957,
    ///   11 = 1094, 12 = 1234 (1224 で頭打ちなので段 12 以上は乱数の上振れでしか出ない)。
    ///   getBattleCount の値は check_limit で my_battle (敵の数 Battle.decide_number、マップの mon_range で頭打ち) にも使う。
    /// - カウンタそのものは他にも使われる: 新しく仲間になる人の HP (Menu._npc_hp_set)、敵の行動回数の上限 (Battle.attack_number: 500 ごと)、
    ///   アバロンの発展段階 (Core の avalon_map_data のしきい値と比較)。これらは partydata[686] を直接読むので getBattleCount には影響されない。
    ///
    /// MOD: カウンタ (= 戦闘回数) は本体のまま増やし、Work.getBattleCount の Postfix で「戦闘回数 × Percent / 100」に読み替える。
    /// 一律の倍率なので、16 系統それぞれのしきい値がちょうど 100 / Percent 倍の戦闘回数になる (系統ごとのずれは出ない)。
    /// 例: 50 % なら、どの系統のどの段も本体の 2 倍の戦闘回数で出る。
    /// (v0.22.0 は段ごとの折れ線 RankNNFrom / CapFrom だったが、段を個別に動かすとその区間だけが伸び縮みし、区間内に散らばる系統ごとのしきい値は
    ///  おおよそでしか動かないうえ、そこまで調整したい場面が無いので % だけにした。段ごとの戦闘回数は設定画面に表示だけする。)
    /// 読み替えるのは敵決め (段と数) だけなので、仲間の HP・アバロン発展・敵の行動回数は本体どおり。追憶の迷宮 (mapno >= 494) は触らない。
    /// 一時停止は Core.btl_counter_add の Prefix/Postfix で増えた分を戻す (こちらはカウンタそのものなので HP・発展も止まる)。
    /// </summary>
    internal static class EnemyGrowth
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> Paused;
        /// <summary>敵が強くなる速さ (%)。本体は 100。50 なら 2 倍の戦闘回数をかけて強くなる。</summary>
        public static ConfigEntry<int> Percent;

        /// <summary>段 1〜16 の下限保証 (しきい値の 16 系統平均)。</summary>
        private static readonly int[] RANK_FLOORS = { 0, 55, 122, 231, 320, 432, 570, 683, 827, 957, 1094, 1234, 1362, 1499, 1641, 1803 };
        private const int BIRTH_CAP = 1224;
        private const int FIRST_RANK = 2, LAST_RANK = 11;   // 表示する段 (段 12 以上の下限保証は 1224 を超えるので届かない)
        private const int COUNTER = 686;     // Work.PARTY_battle_count
        private const int MIN_PCT = 10, MAX_PCT = 300, STEP_PCT = 10;

        /// <summary>表示行 i の本体の戦闘回数 (段 2〜11 の下限保証の 16 系統平均、最後は上限 1224)。</summary>
        private static int Target(int i) { return i < LAST_RANK - FIRST_RANK + 1 ? RANK_FLOORS[FIRST_RANK - 1 + i] : BIRTH_CAP; }
        public static int PointCount { get { return LAST_RANK - FIRST_RANK + 2; } }
        public static string PointName(int i) { return i < LAST_RANK - FIRST_RANK + 1 ? "敵レベル " + (FIRST_RANK + i) + " 以上になる" : "敵レベルが最大になる (上限)"; }
        public static string PointShortName(int i) { return i < LAST_RANK - FIRST_RANK + 1 ? "敵レベル " + (FIRST_RANK + i) : "最大 (上限)"; }

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Growth", "Enabled", true,
                "敵が強くなる速さを MOD で調整する。いいえ なら本体そのまま");
            Paused = config.Bind("Growth", "Paused", false,
                "はい の間は敵が強くならない (戦闘回数カウンタが増えない。仲間の初期 HP と、アバロンの次の建設が解禁される戦闘回数も止まる。"
                + "アバロンの木の成長と建設中の完成待ちは別のカウンタなので止まらない)");
            Percent = config.Bind("Growth", "Percent", 100,
                new ConfigDescription("敵が強くなる速さ (%)。本体は 100。50 なら本体の 2 倍の戦闘回数をかけて強くなり、200 なら半分で強くなる。"
                    + "16 系統どれも同じ割合で変わる。10 刻み",
                    new AcceptableValueRange<int>(MIN_PCT, MAX_PCT)));
        }

        public static bool IsEnabled { get { return Enabled != null && Enabled.Value; } }

        private static int Pct { get { return Percent != null ? Percent.Value : 100; } }

        /// <summary>今の戦闘回数カウンタ (= 戦闘回数)。</summary>
        public static int Counter { get { return Work.partydata != null && Work.partydata.Length > COUNTER ? Work.partydata[COUNTER] : 0; } }

        /// <summary>戦闘回数 c → 本体換算のカウンタ (c × % / 100)。</summary>
        public static int Map(int c)
        {
            if (!IsEnabled || c <= 0) return c;
            return (int)((long)c * Pct / 100);
        }

        // ---- 設定画面用 ----

        /// <summary>X の値 x で選ばれる敵レベル (1〜16。しきい値の 16 系統平均で)。</summary>
        private static int RankAt(int x)
        {
            int r = 1;
            for (int i = 1; i < RANK_FLOORS.Length; i++) if (x >= RANK_FLOORS[i]) r = i + 1;
            return r;
        }

        public static string CounterLabel()
        {
            int c = Counter;
            int e = Map(c);
            int my = e > BIRTH_CAP ? BIRTH_CAP : e;         // my_birth は 1224 で頭打ち。X の最大 = my_birth + my_birth*9/8 は頭打ちされない
            int a = RankAt(my), b = RankAt(my * 17 / 8);
            string ranks = a == b ? a.ToString() : a + "〜" + b;
            return c + " 戦 (出る敵レベル " + ranks + (e != c ? "、本体換算 " + e + " 戦" : "") + ")";
        }

        /// <summary>表示行 i: 今の % で何戦目になるか (Map(c) が本体の値に届く最小の c)。</summary>
        public static string FromLabel(int i)
        {
            int t = Target(i);
            int p = IsEnabled ? Pct : 100;
            int v = (int)(((long)t * 100 + p - 1) / p);
            return v + " 戦目" + (v == t ? "" : " (" + t + ")");
        }

        public static bool FromChanged(int i) { return IsEnabled && Pct != 100; }

        public static string PercentLabel()
        {
            int p = Pct;
            if (p == 100) return "100 % (本体どおり)";
            return p + " %";
        }

        /// <summary>% を STEP_PCT 刻みで動かす (MIN_PCT〜MAX_PCT)。</summary>
        public static void CyclePercent(int delta)
        {
            if (Percent == null) return;
            int p = (Percent.Value / STEP_PCT) * STEP_PCT + delta;
            if (p < MIN_PCT) p = MIN_PCT;
            if (p > MAX_PCT) p = MAX_PCT;
            Percent.Value = p;
        }

        // ---- 一時停止 (カウンタの加算を戻す) ----

        public static void OnAdded(int before)
        {
            int after = Work.partydata[COUNTER];
            int delta = after - before;
            if (delta <= 0) return;
            if (IsEnabled && Paused.Value)
            {
                Work.partydata[COUNTER] = before;
                Plugin.Log.LogInfo("[Growth] +" + delta + " paused -> " + before);
                return;
            }
            Plugin.Log.LogInfo("[Growth] +" + delta + " -> " + after + " (enemy level counter " + Map(after) + ")");
        }
    }

    [HarmonyPatch(typeof(Core), "btl_counter_add")]
    internal static class EnemyGrowth_btl_counter_add_Patch
    {
        private static void Prefix(out int __state)
        {
            __state = EnemyGrowth.Counter;
        }

        private static void Postfix(int __state)
        {
            EnemyGrowth.OnAdded(__state);
        }
    }

    /// <summary>敵決めが読む戦闘回数を折れ線で読み替える。追憶の迷宮と mapno 494/495 は本体が別の値を返すので触らない。</summary>
    [HarmonyPatch(typeof(Work), "getBattleCount")]
    internal static class EnemyGrowth_getBattleCount_Patch
    {
        private static int s_lastLogged = -1;   // check_limit は 1 戦で 2〜3 回呼ぶので同じ値は 1 回だけログに出す

        private static void Postfix(ref int __result)
        {
            if (!EnemyGrowth.IsEnabled) return;
            if (Main.map == null || Main.map.mapno >= 494) return;
            int mapped = EnemyGrowth.Map(__result);
            if (mapped != __result)
            {
                if (s_lastLogged != __result)
                {
                    s_lastLogged = __result;
                    Plugin.Log.LogInfo("[Growth] battle count " + __result + " -> " + mapped + " for enemy selection");
                }
                __result = mapped;
            }
        }
    }
}
