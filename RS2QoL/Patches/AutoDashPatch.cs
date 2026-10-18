using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// フィールドのダッシュの基準 (ボタンを押していない時に走るか歩くか) を、今のマップにモンスターがいるかで自動的に切り替える。
    /// モンスターがいれば「歩きが基準 (押すと走る)」、いなければ「ダッシュが基準 (押すと歩く)」。
    ///
    /// 本体のオプション「ダッシュ」は Work.partydata[678] (0 = 歩き基準、1 = ダッシュ基準) で、Core.move_human_sub_r が
    /// joy_data[0] から human_key を組み立てた直後に `if (partydata[678] != 0) human_key ^= 128` と反転しているだけ。
    /// そこで move_human_sub_r の Prefix で partydata[678] を望む値に差し替え、Postfix で元の値に戻す。本体の反転処理を
    /// そのまま通すので、オプション画面の表示やセーブに残る値は変わらない (partydata[678] を読むのはこのメソッドとメニューだけ)。
    ///
    /// モンスターの判定: 本体に「モンスターの数」は無く、マップの人物 40 枠 (Core.people) を自分で数える。
    /// 人物は people_ivent_type が 1 = 触れると戦闘 で、その戦闘イベント (people_ivent の下位 5 ビット) が 24 = 通常戦闘 /
    /// 25 = 特殊戦闘 (ボス等)。people_condition が 255 の枠は空き、0x40 は無効 (people_move が飛ばす)。
    /// 倒した / 消えたモンスターは people_condition = 255 (clear_one_people) か、次のマップ再構築でフラグにより作られないので、
    /// 全滅させればその場でダッシュ基準に変わる。点滅中 (0x10) はまだ数える (出現 / 消滅の演出中に基準が揺れないように)。
    /// 動かないモンスター (people_condition & 0x80) も数える (ぶつかれば戦闘になるので)。
    /// </summary>
    internal static class AutoDash
    {
        public static ConfigEntry<bool> Enabled;

        private const int PARTY_DASHMODE = 678;
        private const int EV_NORMAL_BATTLE = 24;
        private const int EV_SPECIAL_BATTLE = 25;

        private static int s_lastCount = -1;   // 前回数えたモンスターの数 (変わった時だけログ)

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Field", "AutoDash", false,
                "フィールドのダッシュの基準をマップのモンスターの有無で自動的に切り替える (モンスターがいれば歩きが基準で押すと走る、いなければダッシュが基準で押すと歩く)。"
                + "ON の間は本体のオプション「ダッシュ」の値を無視する (設定の表示とセーブは変えない)");
        }

        public static bool IsEnabled { get { return Enabled != null && Enabled.Value; } }

        /// <summary>今のマップにいるモンスター (触れると戦闘になる人物) の数</summary>
        public static int CountMonsters(Core c)
        {
            Core.People[] people = c.people;
            if (people == null) return 0;
            int n = 0;
            for (int i = 0; i < people.Length; i++)
            {
                Core.People p = people[i];
                if (p == null) continue;
                if (p.people_condition == 255 || (p.people_condition & 0x40) != 0) continue;
                if (p.people_ivent_type != 1) continue;
                int ev = p.people_ivent & 0x1F;
                if (ev != EV_NORMAL_BATTLE && ev != EV_SPECIAL_BATTLE) continue;
                n++;
            }
            return n;
        }

        /// <summary>望むダッシュの基準 (partydata[678] に入れる値)。モンスターがいれば 0 = 歩き、いなければ 1 = ダッシュ</summary>
        public static int DesiredMode(Core c)
        {
            int n = CountMonsters(c);
            if (n != s_lastCount)
            {
                Plugin.Log.LogInfo("[AutoDash] applied: monsters=" + n + " base=" + (n > 0 ? "walk" : "dash") + " (option=" + Work.partydata[PARTY_DASHMODE] + ")");
                s_lastCount = n;
            }
            return n > 0 ? 0 : 1;
        }

        [HarmonyPatch(typeof(Core), nameof(Core.move_human_sub_r))]
        private static class MoveHumanPatch
        {
            private static bool s_changed;
            private static int s_saved;

            private static void Prefix(Core __instance)
            {
                s_changed = false;
                if (!IsEnabled || Work.partydata == null || Work.partydata.Length <= PARTY_DASHMODE) return;
                s_saved = Work.partydata[PARTY_DASHMODE];
                Work.partydata[PARTY_DASHMODE] = DesiredMode(__instance);
                s_changed = true;
            }

            private static void Postfix()
            {
                if (!s_changed) return;
                s_changed = false;
                Work.partydata[PARTY_DASHMODE] = s_saved;
            }
        }
    }
}
