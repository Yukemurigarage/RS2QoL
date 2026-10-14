using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 「A (決定) を押したのに反応しない」の切り分け用診断ログ ([Diagnostics] Enabled で ON)。
    /// 本体は決定ボタンを「押した瞬間の 1 フレーム」だけ有効な信号として扱うので、
    /// その 1 フレームをどの層が捨てたかを追う。
    ///   [OK]      Sys.syncKey 後に決定ビットの押下エッジ (Sys.key[1]) が立った。key[2] に方向があれば
    ///             メニュー層 (InputManager.GetKeyInput) は方向を優先して決定を捨てる
    ///   [MenuIn]  InputManager.GetMenuInput が返した値 (決定なら 0x100001)。[OK] の直後にこれが無ければメニュー層で消えた
    ///   [KeyRep]  Core.get_key_rep 後の key_is_what に決定が入ったか。入らなければフィールド層の取り込みで消えた
    ///   [Search]  Core.search_ivent が決定を見た時の阻害フラグ。どれかが非 0 なら話す/調べるはブロックされた
    /// </summary>
    [HarmonyPatch(typeof(Sys), nameof(Sys.syncKey))]
    [HarmonyPriority(Priority.Last)]
    internal static class OkDiagSysPatch
    {
        public const int OK_BITS = 0x100001;

        private static void Postfix()
        {
            if (!InputDiag.Enabled) return;
            if ((Sys.key[1] & OK_BITS) != 0)
            {
                Plugin.Log.LogInfo(string.Format("[OK] t={0} edge key[0]=0x{1:X} key[2]=0x{2:X}",
                    InputDiag.T(), Sys.key[0], Sys.key[2]));
            }
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.GetMenuInput))]
    internal static class OkDiagMenuPatch
    {
        private static void Postfix(int __result)
        {
            if (!InputDiag.Enabled) return;
            if (__result == -1 || __result == 0) return;

            // どの画面処理 (Menu.proc_* 等) が入力を読んだか: 呼び出し元の関数名と Menu の段 (stg) を添える
            string callers = "";
            try
            {
                var st = new System.Diagnostics.StackTrace(false);
                for (int i = 1; i < st.FrameCount && i <= 4; i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    if (m == null) continue;
                    if (m.Name == "GetMenuInput" || m.DeclaringType == typeof(OkDiagMenuPatch)) continue;
                    if (callers.Length > 0) callers += "<";
                    callers += m.DeclaringType.Name + "." + m.Name;
                }
            }
            catch { }
            string menuState = "";
            Menu menu = Main.menu;
            if (menu != null)
            {
                int stg = (menu.stg_no > 0 && (menu.stg_no - 1) * 2 + 1 < menu.stg_work.Length) ? menu.stg_work[(menu.stg_no - 1) * 2 + 1] : -1;
                menuState = string.Format(" menu_type={0} stg={1} init_flg={2}", menu.menu_type, stg, menu.init_flg);
            }
            Plugin.Log.LogInfo(string.Format("[MenuIn] t={0} result=0x{1:X}{2} by {3}", InputDiag.T(), __result, menuState, callers));
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.get_key_rep))]
    internal static class OkDiagKeyRepPatch
    {
        private static void Postfix(Core __instance)
        {
            if (!InputDiag.Enabled) return;
            bool sysEdge = (Sys.key[1] & OkDiagSysPatch.OK_BITS) != 0;
            bool took = (__instance.key_is_what & OkDiagSysPatch.OK_BITS) != 0;
            if (sysEdge || took)
            {
                Plugin.Log.LogInfo(string.Format("[KeyRep] t={0} sysEdge={1} key_is_what=0x{2:X} joy=0x{3:X} sub=0x{4:X} rep={5}",
                    InputDiag.T(), sysEdge, __instance.key_is_what, __instance.joy_data[0], __instance.key_is_what_sub, __instance.key_rep_cont));
            }
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.search_ivent))]
    internal static class OkDiagSearchPatch
    {
        private static void Prefix(Core __instance)
        {
            if (!InputDiag.Enabled) return;
            Core c = __instance;
            if ((c.key_is_what & OkDiagSysPatch.OK_BITS) == 0) return;
            Plugin.Log.LogInfo(string.Format(
                "[Search] t={0} ivent_sw={1} mess_flag={2} ivent_stop={3} exevt={4} battle={5} tono={6} exdemo={7} cant_move={8} move_wait={9} scrol(v={10},h={11})",
                InputDiag.T(), c.ivent_sw, c.mess_flag, c.ivent_stop_flag, c.exevt_flg, c.battle_flg, c.tono_flg, c.exdemo_flg,
                c.man_cant_move, c.man_move_wait, c.map_scrol_flag_v, c.map_scrol_flag_h));
        }
    }
}
