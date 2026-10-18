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
    ///
    /// イベント番号が 24 / 25 でなく「台本 (メッセージ)」の人物もいる: 下位 5 ビットが 12 以下ならメッセージ番号
    /// (下位 5 ビット) × 256 + 上位バイト の台本が走る。触れると (people_ivent_type 1) 台本が走る人物は 1000 体以上あり、
    /// 門番のように話すだけのものと、台本の中で戦闘命令 (EV 24 / 25、CALL 先を含む) に至るもの (アバロンのゴブリン襲来 = msg 1500、
    /// 各マップ 8 体、フラグ 190 = 14 の間だけ出る) が混ざる。後者の 150 個のメッセージ番号を BATTLE_SCRIPTS に埋め込み、
    /// それに当たる人物もモンスターとして数える (tools/script-analysis/battle_msgs.ps1 で生成。script.bin は固定なので表で持つ)。
    /// </summary>
    internal static class AutoDash
    {
        public static ConfigEntry<bool> Enabled;

        private const int PARTY_DASHMODE = 678;
        private const int EV_NORMAL_BATTLE = 24;
        private const int EV_SPECIAL_BATTLE = 25;

        private static int s_lastCount = -1;   // 前回数えたモンスターの数 (変わった時だけログ)
        private static int s_lastScripted;     // そのうち台本経由のもの (ログ用)

        /// <summary>触れると戦闘の台本が走るメッセージ番号 (昇順。tools/script-analysis/battle_msgs.ps1 の出力)</summary>
        private static readonly int[] BATTLE_SCRIPTS =
        {
            1030, 1033, 1034, 1035, 1036, 1037, 1041, 1045, 1049, 1050, 1052, 1054, 1055, 1057, 1058, 1060,
            1061, 1066, 1067, 1076, 1082, 1089, 1092, 1094, 1095, 1096, 1100, 1101, 1104, 1109, 1111, 1114,
            1121, 1125, 1137, 1139, 1141, 1144, 1147, 1148, 1149, 1154, 1177, 1192, 1205, 1214, 1215, 1224,
            1252, 1253, 1254, 1255, 1256, 1257, 1258, 1259, 1291, 1292, 1293, 1300, 1301, 1302, 1303, 1349,
            1358, 1359, 1397, 1411, 1425, 1427, 1428, 1429, 1430, 1431, 1432, 1433, 1445, 1466, 1467, 1468,
            1469, 1470, 1472, 1473, 1474, 1475, 1477, 1483, 1484, 1485, 1486, 1487, 1488, 1490, 1491, 1492,
            1500, 1504, 1505, 1506, 1569, 1583, 1588, 1621, 1629, 1635, 1638, 1640, 1644, 1646, 1651, 1656,
            1658, 1661, 1683, 1690, 1696, 1729, 1764, 1771, 1773, 1776, 1789, 1805, 1806, 1819, 1831, 1832,
            1838, 1841, 1871, 1872, 1873, 1902, 1911, 2054, 3076, 3127, 3130, 3131, 3132, 3133, 3138, 3152,
            3155, 3175, 3176, 3186, 3188, 3200,
        };

        /// <summary>people_ivent が台本 (下位 5 ビット ≤ 12) で、その台本が戦闘に至るか</summary>
        public static bool IsBattleScript(int peopleIvent)
        {
            int lo = peopleIvent & 0x1F;
            if (lo > 12) return false;
            int msg = lo * 256 + ((peopleIvent >> 8) & 0xFF);
            return System.Array.BinarySearch(BATTLE_SCRIPTS, msg) >= 0;
        }

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
            int scripted;
            return CountMonsters(c, out scripted);
        }

        /// <summary>同上。scripted = そのうち台本経由 (BATTLE_SCRIPTS) で数えた人物の数</summary>
        public static int CountMonsters(Core c, out int scripted)
        {
            scripted = 0;
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
                if (ev == EV_NORMAL_BATTLE || ev == EV_SPECIAL_BATTLE) { n++; continue; }
                if (IsBattleScript(p.people_ivent)) { n++; scripted++; }
            }
            return n;
        }

        /// <summary>望むダッシュの基準 (partydata[678] に入れる値)。モンスターがいれば 0 = 歩き、いなければ 1 = ダッシュ</summary>
        public static int DesiredMode(Core c)
        {
            int scripted;
            int n = CountMonsters(c, out scripted);
            if (n != s_lastCount || scripted != s_lastScripted)
            {
                Plugin.Log.LogInfo("[AutoDash] applied: monsters=" + n + " (scripted=" + scripted + ") base=" + (n > 0 ? "walk" : "dash") + " (option=" + Work.partydata[PARTY_DASHMODE] + ")");
                s_lastCount = n;
                s_lastScripted = scripted;
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
