using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 起動時 (とタイトル放置後) のプロローグのスクロールと、タイトル放置後の自動デモ (フィールド・戦闘) を
    /// A/B/X/Y でも飛ばせるようにする。
    ///
    /// 本体は Menu._opening1 (menu_type 18) で InputManager.GetMenuInput() が 256 (Start) を返した時だけ
    /// 飛ばす (決定 0x100001 はタッチ由来の時だけ通す) ので、パッドでは Start しか効かない。
    /// 自動デモのフィールド部分 (Core.first_routine case 25 → Core.opening_sa、proc[0]==0 && demo_flag==1) も
    /// 同じ条件 (Start か、タッチ由来の決定) でしか抜けられない。デモ中はイベントや戦闘が menu_type を
    /// 書き換えるので menu_type 18 では判定できず、Core の proc[0] / demo_flag で見る。
    /// 一方ロゴ (Menu._logo、menu_type 101) は決定 (A) で飛ばせるので、挙動を揃える。
    /// GetMenuInput の Postfix で、その場面に合わせて A/B/X/Y の押下を本体が見ているボタンに読み替える。
    ///
    /// デモの戦闘中 (battle_flg) や拡張イベント・tono の間は Core.sync が opening_sa を呼ばず、本体は Start も見ていない。
    /// そこでは HandleDemo (Util.GetKeypadState の最終段 ConfigMenu.Tick から毎論理フレーム) で A/B/X/Y/Start の押下エッジを見て、
    /// ソフトリセットの「強制的に片付けてタイトルへ」の経路 (SoftReset.ForceToTitle、確認ダイアログ無し) で戻す。
    /// </summary>
    internal static class TitleSkip
    {
        public static ConfigEntry<bool> AnyButton;

        private const int MENU_OPENING1 = 18;   // Menu._opening1: プロローグのスクロール
        private const int MENU_LOGO = 101;      // Menu._logo: SQEX / ArtePiazza のロゴ
        private const int IN_OK = 0x100001, IN_CANCEL = 0x400000, IN_Y = 0x200000, IN_X = 0x20, IN_START = 256;
        // 割り当て変更後の key のビット: 決定 / キャンセル / X / Y / Start (ポーズ)
        private const int DEMO_SKIP_KEYS = 0x100000 | 0x400000 | 0x20 | 0x200000 | 0x100;

        private static bool s_demoLogged;
        private static int s_prevDemoKey;

        public static void Bind(ConfigFile config)
        {
            AnyButton = config.Bind("Title", "AnyButtonSkip", true,
                "起動時のプロローグのスクロールと、タイトル放置後の自動デモ (フィールド・戦闘) を Start だけでなく A/B/X/Y でも飛ばす (ロゴも B/X/Y で飛ばせるようにする)");
        }

        private static bool IsFaceButton(int v)
        {
            return v == IN_OK || v == IN_CANCEL || v == IN_Y || v == IN_X;
        }

        private const int FIRST_ROUTINE_DEMO = 25;   // Core.first_routine の proc[1]: opening_sa でデモを回している段

        /// <summary>
        /// タイトル放置後の自動デモ (フィールド・戦闘) の最中か。Core.first_routine が proc[1]==25 (opening_sa) を demo_flag=1 で回している間。
        /// demo_flag はデモを抜けてタイトルに戻っても 1 のまま (タイトルメニューを抜ける case 110 で 0 になる) なので、
        /// demo_flag だけで見るとタイトルの A/B/X/Y まで Start に読み替えてタイトルが操作できなくなる (実機 2026-10-18)。proc[1] も見る。
        /// </summary>
        private static bool InTitleDemo()
        {
            Core core = Main.core;
            return core != null && core.proc != null && core.proc[0] == 0 && core.proc[1] == FIRST_ROUTINE_DEMO && core.demo_flag == 1;
        }

        /// <summary>
        /// 自動デモの戦闘中など、本体が Start を見ていない場面での A/B/X/Y/Start の押下エッジ。
        /// ConfigMenu.Tick から毎論理フレーム呼ぶ (key は割り当て変更後のビット)。フィールド部分は本体の判定 (GetMenuInput の読み替え) に任せる。
        /// </summary>
        public static void HandleDemo(int key)
        {
            int pressed = key & ~s_prevDemoKey & DEMO_SKIP_KEYS;
            s_prevDemoKey = key;
            if (pressed == 0) return;
            if (AnyButton == null || !AnyButton.Value) return;
            if (!InTitleDemo() || ConfigMenu.IsOpen || SoftReset.Active) return;
            Core c = Main.core;
            if (c.battle_flg == 0 && c.exevt_flg == 0 && c.tono_flg == 0) return;
            SoftReset.ForceToTitle("title demo (battle=" + c.battle_flg + " exevt=" + c.exevt_flg + " tono=" + c.tono_flg + ") key=0x" + pressed.ToString("X"));
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
                if (InTitleDemo())
                {
                    if (!s_demoLogged)
                    {
                        s_demoLogged = true;
                        Plugin.Log.LogInfo("[TitleSkip] applied: title demo, face button 0x" + __result.ToString("X") + " -> Start (menu_type=" + t + ")");
                    }
                    __result = IN_START;
                }
                else if (t == MENU_OPENING1) __result = IN_START;
                else if (t == MENU_LOGO) __result = IN_OK;
            }
        }
    }
}
