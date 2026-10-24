using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 戦闘のたびに国庫に入る税収を % で調整する。
    ///
    /// 本体の仕組み (tools/script-analysis で script.bin を読んだ結果):
    /// - 金は戦闘では手に入らない。税収は戦闘後のスクリプト (msg 2061) が毎戦闘の直後に自動で国庫 (Work.partydata[418]、上限 16,777,215) に
    ///   足す。額 = (収入ポイント partydata[152] + アバロンの木の収入 10〜64) × 100。国庫が 16,056,320 以上なら足さない。
    ///   収入ポイントは領土併合や施設完成のイベントで +1〜+18 ずつ増える値。玉座に戻る必要はなく、歩いていても入る。
    /// - 足す命令は Core.add_budget (サブ命令 79 41) で、スクリプト全体でこの 1 か所しか使っていない。宝箱の「N 万クラウン」
    ///   (Core.takara_gold_ivent)、施設の費用 (sub_budget)、金庫番の資金補充 (msg 88: 国庫 −1 万、所持金 = 1 万) は別の命令なので影響しない。
    /// - 本体の Debug.Log("add_budget N") が残っているので、BepInEx コンソールには戦闘ごとに本体の額が出る。
    ///
    /// MOD: add_budget の Prefix で国庫の前の値を覚え、Postfix で増えた分を × Percent / 100 に置き換える。
    /// 敵の強さ (戦闘回数カウンタ) と味方の成長 (技術点) には触らないので、「敵は遅く、金は普通に」「敵は普通、金は多め」のどちらも組める。
    /// </summary>
    internal static class TaxIncome
    {
        /// <summary>税収の倍率 (%)。本体は 100。</summary>
        public static ConfigEntry<int> Percent;
        /// <summary>退却で終わった戦闘でも税収を入れる。本体は入れる。</summary>
        public static ConfigEntry<bool> OnEscape;

        private const int TREASURY = 418;        // Work.PARTY_budget
        private const int INCOME_POINTS = 152;   // partydata[97 + 55] = スクリプトの work55 (収入ポイント)
        private const int TREASURY_MAX = 16777215;
        private const int MIN_PCT = 10, MAX_PCT = 500, STEP_PCT = 10;

        public static void Bind(ConfigFile config)
        {
            Percent = config.Bind("Economy", "TaxPercent", 100,
                new ConfigDescription("戦闘のたびに国庫に入る税収の倍率 (%)。本体は 100。200 なら 2 倍、50 なら半分。"
                    + "敵の強さ・味方の成長・宝箱の N 万クラウン・施設の費用には影響しない。10 刻み",
                    new AcceptableValueRange<int>(MIN_PCT, MAX_PCT)));
            OnEscape = config.Bind("Economy", "TaxOnEscape", true,
                "退却で終わった戦闘でも税収を国庫に入れる (本体どおり)。いいえ なら退却した戦闘では入れない (退却を繰り返して稼げなくなる)。"
                + "敵の強さの戦闘回数は [Growth] SkipEscape で別に決める");
        }

        private static int Pct { get { return Percent != null ? Percent.Value : 100; } }

        public static bool SkipOnEscape { get { return OnEscape != null && !OnEscape.Value; } }

        public static int Treasury { get { return Work.partydata != null && Work.partydata.Length > TREASURY ? Work.partydata[TREASURY] : 0; } }

        /// <summary>本体の 1 戦あたりの税収 (収入ポイント + アバロンの木の収入) × 100。</summary>
        public static int VanillaIncomePerBattle()
        {
            if (Work.partydata == null || Work.partydata.Length <= INCOME_POINTS) return 0;
            int points = Work.partydata[INCOME_POINTS];
            // アバロンの木の収入は new_flag 5 (下位 4 bit) と 6 (上位 4 bit)。Core.flag_work_store_koukan が work55 に足している値と同じ
            if (Main.core != null)
            {
                try { points += Main.core.get_new_flag_atai(5) + (Main.core.get_new_flag_atai(6) << 4); }
                catch (System.Exception) { }
            }
            return points * 100;
        }

        public static int Scale(int delta)
        {
            if (delta <= 0) return delta;
            return (int)((long)delta * Pct / 100);
        }

        // ---- 設定画面用 ----

        public static string PercentLabel()
        {
            int p = Pct;
            if (p == 100) return "100 % (本体どおり)";
            return p + " %";
        }

        public static string TreasuryLabel() { return Treasury.ToString("N0") + " クラウン"; }

        public static string IncomeLabel()
        {
            int v = VanillaIncomePerBattle();
            int m = Scale(v);
            return m.ToString("N0") + " クラウン" + (m != v ? " (本体 " + v.ToString("N0") + ")" : "");
        }

        public static void CyclePercent(int delta)
        {
            if (Percent == null) return;
            int p = (Percent.Value / STEP_PCT) * STEP_PCT + delta;
            if (p < MIN_PCT) p = MIN_PCT;
            if (p > MAX_PCT) p = MAX_PCT;
            Percent.Value = p;
        }
    }

    /// <summary>国庫に税収を足す命令。増えた分を % で置き換える。</summary>
    [HarmonyPatch(typeof(Core), "add_budget")]
    internal static class TaxIncome_add_budget_Patch
    {
        private static void Prefix(out int __state)
        {
            __state = TaxIncome.Treasury;
        }

        private static void Postfix(int __state)
        {
            int after = TaxIncome.Treasury;
            int delta = after - __state;
            if (delta <= 0)
            {
                // 国庫が上限近く (budget_sep で足さない) か、増えなかった。パッチが呼ばれたことだけ残す
                Plugin.Log.LogInfo("[Economy] add_budget called, treasury unchanged (" + after + ")");
                return;
            }
            if (TaxIncome.SkipOnEscape && EnemyGrowth.LastBattleEscaped)
            {
                // 退却した戦闘: 戦闘後スクリプト (msg 2061) が足した分をそのまま戻す。add_budget はこのスクリプトからしか呼ばれない
                Work.partydata[418] = __state;
                Plugin.Log.LogInfo("[Economy] tax vanilla +" + delta + " escaped, not added (treasury " + __state + ")");
                return;
            }
            int scaled = TaxIncome.Scale(delta);
            long v = (long)__state + scaled;
            if (v > 16777215) v = 16777215;
            if (scaled != delta) Work.partydata[418] = (int)v;
            // 100% でも 1 戦に 1 行だけなので常に出す (動作確認用。本体の Debug.Log "add_budget N" はコンソールにしか出ない)
            Plugin.Log.LogInfo("[Economy] tax vanilla +" + delta + " x" + TaxIncome.Percent.Value + "% -> +" + scaled
                + " (treasury " + __state + " -> " + v + ")");
        }
    }
}
