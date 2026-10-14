using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// ダメージ数字の縦位置の調整 ([Battle] DamageYOffsetMonster 既定 4 / DamageYOffsetPlayer 既定 0。v0.27.0 のドット等倍の数字で実機で目で合わせた値。
    /// v0.27.0 までの既定 3 / -1 は本体の 112 % に伸びた数字で合わせたもの)。
    ///
    /// 本体は Effect.dam_xy_calc で数字の基準位置を決める。モンスターは「配置 (8 ドット単位) の下端 − 10」、味方は
    /// 「p_hv (キャラの位置) + 17」に数字 (9 ドット角、左上基準) の上端を置き、跳ねテーブル (0〜8 ドット) ぶん上に動かす。
    /// 式はスーパーファミコン版のままだが、リマスター版のモンスター画像は元のタイル寸法と足元の位置が合っていないものがあり、
    /// 数字が足元より上に見える。ここでは dam_xy_calc の Postfix で基準位置に設定値 (ドット、下が正) を足す。
    /// </summary>
    internal static class DamagePos
    {
        public static ConfigEntry<int> MonsterY;
        public static ConfigEntry<int> PlayerY;
        public const int MIN = -32, MAX = 32;

        public static void Bind(ConfigFile config)
        {
            MonsterY = config.Bind("Battle", "DamageYOffsetMonster", 4,
                new ConfigDescription("モンスターに出るダメージ数字の縦位置の調整 (ドット、下が正。画面は 240 ドット角。既定 4 = ドット文字の数字で足元に合わせる。0 = 本体どおり = モンスター配置の下端 − 10 に数字の上端)", new AcceptableValueRange<int>(MIN, MAX)));
            PlayerY = config.Bind("Battle", "DamageYOffsetPlayer", 0,
                new ConfigDescription("味方に出るダメージ数字の縦位置の調整 (ドット、下が正。既定 0 = 本体どおり)", new AcceptableValueRange<int>(MIN, MAX)));
        }

        public static void Cycle(ConfigEntry<int> e, int delta)
        {
            int v = e.Value + delta;
            if (v < MIN) v = MIN; else if (v > MAX) v = MAX;
            e.Value = v;
        }

        public static string Label(int v)
        {
            if (v == 0) return "0 (本体どおり)";
            return v > 0 ? "+" + v + " (下へ)" : v + " (上へ)";
        }
    }

    /// <summary>Effect.dam_xy_calc(ofs): 基準位置を決めた直後に縦の調整値を足す。</summary>
    [HarmonyPatch(typeof(Effect), "dam_xy_calc")]
    internal static class DamagePosPatch
    {
        private static void Postfix(Effect __instance, int ofs)
        {
            if (DamagePos.MonsterY == null) return;
            int num = Main.battle.mbox[ofs + 11];
            if ((num & 0x80) == 0)
            {
                __instance.dam_data[num * 12 + 1] += DamagePos.PlayerY.Value;
            }
            else
            {
                num &= 0x7F;
                __instance.dam_data[(num + 5) * 12 + 1] += DamagePos.MonsterY.Value;
            }
        }
    }
}
