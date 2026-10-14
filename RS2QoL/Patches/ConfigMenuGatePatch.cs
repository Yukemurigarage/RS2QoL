using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// MOD 設定画面の入口。Util.GetKeypadState の最終段 (他の Postfix が全部終わった後) で
    /// ConfigMenu.Tick に渡し、開いている間はゲームに 0 を返して操作を奪う。
    /// </summary>
    [HarmonyPatch(typeof(Util), nameof(Util.GetKeypadState))]
    [HarmonyPriority(Priority.Last)]
    internal static class ConfigMenuGatePatch
    {
        private static void Postfix(ref int __result)
        {
            GameSpeed.HandleToggleButton();
            __result = ConfigMenu.Tick(__result);
        }
    }
}
