using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 戦闘後の結果表示 (経験値 / HP 上昇 / 技能レベル上昇 / アイテム入手 / 見切り習得) の送りを
    /// 「決定かキャンセルを新たに押した瞬間」だけにする。
    ///
    /// 本体はこれらの表示を joy1[2] != 0 で送る。joy1[2] は Sys.key[2] で、押した瞬間に加えて
    /// 長押しのリピート (400ms 後から 120ms 間隔) と十字キー/スティックの方向でも立つ。
    /// そのため A を押したまま、または方向を倒したまま戦闘が終わると、何が上がったか読めないまま 120ms ごとに流れる。
    /// ここでは対象メソッドの間だけ joy1[2] を「決定/キャンセル/十字キーの押下エッジ (joy1[1])」に差し替え、終わったら戻す。
    /// 十字キーは本体も送りに使える (joy1[1] に方向ビットが入る) ので、エッジにしたうえで残す。
    /// 戦闘中の行動メッセージ (タイマー自動送り) には触らない。
    /// </summary>
    internal static class BattleResult
    {
        public static ConfigEntry<bool> WaitForPress;
        private const int OK_CANCEL = 0x500001;
        private const int DIR_BITS = 0xF0000;

        public static void Bind(ConfigFile config)
        {
            WaitForPress = config.Bind("Battle", "ResultWaitForPress", true,
                "戦闘後の結果表示 (経験値/HP/技能/アイテム/見切り) を、決定・キャンセル・十字キーを新たに押した時だけ送る (長押しでは送らない)");
        }

        public static int Mask(Battle b)
        {
            int orig = b.joy1[2];
            if (WaitForPress != null && WaitForPress.Value)
            {
                b.joy1[2] = b.joy1[1] & (OK_CANCEL | DIR_BITS);
            }
            return orig;
        }
    }

    [HarmonyPatch(typeof(Battle), "dialog_exp")]
    internal static class BattleResultExpPatch
    {
        private static void Prefix(Battle __instance, out int __state) { __state = BattleResult.Mask(__instance); }
        private static void Postfix(Battle __instance, int __state) { __instance.joy1[2] = __state; }
    }

    [HarmonyPatch(typeof(Battle), "dialog_hp")]
    internal static class BattleResultHpPatch
    {
        private static void Prefix(Battle __instance, out int __state) { __state = BattleResult.Mask(__instance); }
        private static void Postfix(Battle __instance, int __state) { __instance.joy1[2] = __state; }
    }

    [HarmonyPatch(typeof(Battle), "dialog_skill")]
    internal static class BattleResultSkillPatch
    {
        private static void Prefix(Battle __instance, out int __state) { __state = BattleResult.Mask(__instance); }
        private static void Postfix(Battle __instance, int __state) { __instance.joy1[2] = __state; }
    }

    [HarmonyPatch(typeof(Battle), "dialog_item")]
    internal static class BattleResultItemPatch
    {
        private static void Prefix(Battle __instance, out int __state) { __state = BattleResult.Mask(__instance); }
        private static void Postfix(Battle __instance, int __state) { __instance.joy1[2] = __state; }
    }

    [HarmonyPatch(typeof(Battle), "test_mikiri")]
    internal static class BattleResultMikiriPatch
    {
        private static void Prefix(Battle __instance, out int __state) { __state = BattleResult.Mask(__instance); }
        private static void Postfix(Battle __instance, int __state) { __instance.joy1[2] = __state; }
    }
}
