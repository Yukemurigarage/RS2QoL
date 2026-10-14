using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// Esc の「ゲームを終了しますか？」ダイアログが倍速中に一瞬で消える問題の修正。
    ///
    /// Main.Disp.run は Input.GetKeyUp(Escape) でダイアログを開き (EndGameSteam.Initialize)、
    /// 開いた後は EndGameSteam.DialogInput が同じ GetKeyUp(Escape) で閉じる。GetKeyUp は Unity の 1 フレームの間ずっと
    /// true なので、倍速で 1 Unity フレームに論理フレームを 2 回以上回すと、1 回目で開いて 2 回目で閉じてしまう。
    /// 開いたのと同じ Unity フレームでは DialogInput を飛ばして、キーアップを二重に拾わないようにする。
    /// </summary>
    [HarmonyPatch]
    internal static class EndDialogOpenPatch
    {
        internal static int OpenedFrame = -1;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("EndGameSteam"), "Initialize");
        }

        private static void Postfix()
        {
            OpenedFrame = Time.frameCount;
            Plugin.Log.LogDebug("[EndDialog] opened t=" + InputDiag.T() + " frame=" + Time.frameCount + " escTaken=" + ConfigMenu.EscTakenFrame + " config=" + ConfigMenu.IsOpen);
        }
    }

    [HarmonyPatch]
    internal static class EndDialogInputPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("EndGameSteam"), "DialogInput");
        }

        // 開いた Unity フレーム内は入力を見ない (元処理を止める理由: 同じ GetKeyUp を拾って即閉じるため)
        private static bool Prefix()
        {
            return Time.frameCount != EndDialogOpenPatch.OpenedFrame;
        }
    }
}
