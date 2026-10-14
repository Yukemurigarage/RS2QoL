using System.Reflection;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// メニュー (店など) で「A (決定) を押したのに反応しない」の切り分け用診断 ([Diagnostics] Enabled で ON)。
    /// 本体が決定の押下エッジ (Sys.key[1]) を読まずに捨てる窓は静的に 4 つ見つかっている。それぞれの窓で
    /// 決定エッジが立っていた時に [Drop] 行を出す ([OK] 行の直後にこれが出ていれば、その窓で消えたと分かる)。
    ///   notReady   : proc_* の先頭 `if (!spriteStudio.IsReady()) return;` (画面の SpriteStudio が読み込み中 / フェードアウト)
    ///   sceneWait  : Menu.menu(stg, 0) が NextScene / PrevScene 後の古い画面の消滅を待っていて init/proc を回さない
    ///   waitKey    : メッセージ (「ありがとう」等、menu_flg=5) の最初の fix41 フレームはキーを見ない
    ///   dirFirst   : InputManager.GetKeyInput が同じフレームに新たに押された方向 (key[2]) を優先して決定を捨てた
    /// </summary>
    internal static class MenuDropDiag
    {
        private const int OK_BITS = 0x100001;
        private const int DIR_BITS = 0xF0000;

        internal static bool OkEdge { get { return InputDiag.Enabled && (Sys.key[1] & OK_BITS) != 0; } }

        internal static void Log(string why, string detail)
        {
            Plugin.Log.LogInfo(string.Format("[Drop] t={0} {1} {2} key[0]=0x{3:X} key[2]=0x{4:X}", InputDiag.T(), why, detail, Sys.key[0], Sys.key[2]));
        }

        internal static bool DirFirst { get { return (Sys.key[2] & DIR_BITS) != 0; } }
    }

    [HarmonyPatch(typeof(CSpriteStudioObject), nameof(CSpriteStudioObject.IsReady))]
    internal static class MenuDropDiagReadyPatch
    {
        private static readonly FieldInfo s_mode = AccessTools.Field(typeof(CSpriteStudioObject), "m_Mode");
        private static float s_lastT = -1;

        private static void Postfix(CSpriteStudioObject __instance, bool __result)
        {
            if (__result || !MenuDropDiag.OkEdge) return;
            float t = UnityEngine.Time.realtimeSinceStartup;
            if (t == s_lastT) return; // 同じフレームに複数回呼ばれても 1 行
            s_lastT = t;
            MenuDropDiag.Log("notReady", __instance.GetFileName() + " mode=" + (s_mode != null ? s_mode.GetValue(__instance) : null));
        }
    }

    [HarmonyPatch(typeof(Menu), "menu")]
    internal static class MenuDropDiagScenePatch
    {
        private static readonly FieldInfo s_remove = AccessTools.Field(typeof(Menu), "m_removeSpriteName");
        private static readonly FieldInfo s_prev = AccessTools.Field(typeof(Menu), "m_prevSpriteName");

        private static void Prefix(Menu __instance, int stg, int mode)
        {
            if (mode != 0 || !MenuDropDiag.OkEdge || s_remove == null || s_prev == null) return;
            string rm = s_remove.GetValue(__instance) as string;
            string pv = s_prev.GetValue(__instance) as string;
            if (!string.IsNullOrEmpty(rm) && SpriteStudioManager.GetSpriteStudio(rm) == null)
                MenuDropDiag.Log("sceneWait", "next after " + rm + " stg=" + stg);
            else if (!string.IsNullOrEmpty(pv) && SpriteStudioManager.GetSpriteStudio(pv) == null)
                MenuDropDiag.Log("sceneWait", "prev after " + pv + " stg=" + stg);
        }
    }

    [HarmonyPatch(typeof(Menu), "wait_key")]
    internal static class MenuDropDiagWaitKeyPatch
    {
        private static void Prefix(Menu __instance)
        {
            if (__instance.fix41 != 0 && MenuDropDiag.OkEdge)
                MenuDropDiag.Log("waitKey", "fix41=" + __instance.fix41 + " fix4=" + __instance.fix4 + " menu_flg2=" + __instance.menu_flg2);
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.GetMenuInput))]
    internal static class MenuDropDiagDirFirstPatch
    {
        private static void Postfix(int __result)
        {
            if (!MenuDropDiag.OkEdge || !MenuDropDiag.DirFirst) return;
            if (__result == 1048577) return;
            MenuDropDiag.Log("dirFirst", "GetMenuInput=0x" + __result.ToString("X"));
        }
    }
}
