using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 起動時 (とタイトル放置後) のプロローグのスクロールを A/B/X/Y でも飛ばせるようにする。
    ///
    /// 本体は Menu._opening1 (menu_type 18) で InputManager.GetMenuInput() が 256 (Start) を返した時だけ
    /// 飛ばす (決定 0x100001 はタッチ由来の時だけ通す) ので、パッドでは Start しか効かない。
    /// 一方ロゴ (Menu._logo、menu_type 101) は決定 (A) で飛ばせるので、挙動を揃える。
    /// GetMenuInput の Postfix で、その場面に合わせて A/B/X/Y の押下を本体が見ているボタンに読み替える。
    /// </summary>
    internal static class TitleSkip
    {
        public static ConfigEntry<bool> AnyButton;

        private const int MENU_OPENING1 = 18;   // Menu._opening1: プロローグのスクロール
        private const int MENU_LOGO = 101;      // Menu._logo: SQEX / ArtePiazza のロゴ
        private const int IN_OK = 0x100001, IN_CANCEL = 0x400000, IN_Y = 0x200000, IN_X = 0x20, IN_START = 256;

        public static void Bind(ConfigFile config)
        {
            AnyButton = config.Bind("Title", "AnyButtonSkip", true,
                "起動時のプロローグのスクロールを Start だけでなく A/B/X/Y でも飛ばす (ロゴも B/X/Y で飛ばせるようにする)");
        }

        private static bool IsFaceButton(int v)
        {
            return v == IN_OK || v == IN_CANCEL || v == IN_Y || v == IN_X;
        }

        [HarmonyPatch(typeof(InputManager), nameof(InputManager.GetMenuInput))]
        private static class GetMenuInputPatch
        {
            private static void Postfix(ref int __result)
            {
                if (AnyButton == null || !AnyButton.Value) return;
                if (Main.menu == null || ConfigMenu.IsOpen) return;
                if (!IsFaceButton(__result)) return;
                int t = Main.menu.menu_type;
                if (t == MENU_OPENING1) __result = IN_START;
                else if (t == MENU_LOGO) __result = IN_OK;
            }
        }
    }
}
