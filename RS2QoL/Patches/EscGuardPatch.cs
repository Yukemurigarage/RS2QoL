using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 設定画面が使った Esc を本体に渡さない。
    ///
    /// 本体は Main.Disp.run の case 2 (ゲーム中) で Input.GetKeyUp(Escape) を直接読んで終了ダイアログを開く
    /// (倍速 / 60fps の複製フレームでは Interp60RunStartPatch が同じことをする)。設定画面は Esc の押下 (GetKeyDown) で閉じたり
    /// キー割り当ての取り込みを中止したりするが、その後キーを離した時の GetKeyUp は本体に見えたままなので、
    /// そのままだと設定画面を Esc で閉じるたびに終了ダイアログが開く。
    /// Harmony で Input.GetKeyUp (ネイティブ) は差し替えられないので、run の後 (全パッチの最後) に
    /// 「設定画面が Esc を使ってからキーを離すまでの間に開いたダイアログ」を閉じて無かったことにする
    /// (Initialize は音を鳴らさないので黙って戻せる)。開いたかどうかは m_isEndDialog の false → true の変化で見る。
    /// </summary>
    [HarmonyPatch(typeof(Main.Disp), nameof(Main.Disp.run))]
    [HarmonyPriority(Priority.Last)]
    internal static class EscGuardPatch
    {
        private static readonly FieldInfo s_isEndDialog = AccessTools.Field(typeof(Main), "m_isEndDialog");
        private static readonly FieldInfo s_endgame = AccessTools.Field(typeof(Main), "m_endgame");
        private static readonly MethodInfo s_release = AccessTools.Method(AccessTools.TypeByName("EndGameSteam"), "Release");
        private static bool s_logged;
        private static bool s_prevDialog;
        private const int TIMEOUT_FRAMES = 180;   // 3 秒押しっぱなしなら諦める (その後に離せば本体どおりダイアログが開く)

        private static void Postfix()
        {
            bool dialog = s_isEndDialog != null && (bool)s_isEndDialog.GetValue(null);
            bool opened = dialog && !s_prevDialog;
            s_prevDialog = dialog;
            if (ConfigMenu.EscTakenFrame < 0) return;
            if (!s_logged) { s_logged = true; Plugin.Log.LogInfo("[EscGuard] patched: Main.Disp.run (設定画面が使った Esc で終了ダイアログを開かない)"); }
            int now = Time.frameCount;
            if (opened)
            {
                object endgame = s_endgame != null ? s_endgame.GetValue(null) : null;
                if (endgame != null && s_release != null) s_release.Invoke(endgame, null);
                s_isEndDialog.SetValue(null, false);
                s_prevDialog = false;
                Plugin.Log.LogInfo("[EscGuard] applied: 終了ダイアログを取り消し t=" + InputDiag.T() + " taken=" + ConfigMenu.EscTakenFrame + " now=" + now);
                ConfigMenu.EscTakenFrame = -1;
                return;
            }
            // 離したのに開かなかった (このフレームで離した / 複製フレームで離した分の持ち越しを本体より先に取った / ゲーム中でない等): 役目は終わり
            if (Input.GetKeyUp(KeyCode.Escape) || Interp60.TakeEscUp())
            {
                Plugin.Log.LogDebug("[EscGuard] released without dialog taken=" + ConfigMenu.EscTakenFrame + " now=" + now + " held=" + Input.GetKey(KeyCode.Escape));
                ConfigMenu.EscTakenFrame = -1;
                return;
            }
            if (now - ConfigMenu.EscTakenFrame > TIMEOUT_FRAMES)
            {
                Plugin.Log.LogDebug("[EscGuard] timeout taken=" + ConfigMenu.EscTakenFrame + " now=" + now + " held=" + Input.GetKey(KeyCode.Escape));
                ConfigMenu.EscTakenFrame = -1;
            }
        }
    }
}
