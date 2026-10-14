using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 60fps 表示 (Interp60) の複製フレームに落ちたマウスのクリックを、直後の論理フレームへ持ち越す。
    ///
    /// Interp60 は 2 Unity フレームに 1 回 GameMain.Update を止めて前フレームの描画を複製する。その Unity フレームでは
    /// InputManager.update も Menu の処理も回らない。本体のタッチ判定 (スマホ版の名残) は
    ///   - 押した瞬間: InputManager.is_touch_down_list (Input.GetMouseButtonDown(0)) で矩形に指 (m_fingerid) を付ける
    ///   - 離した瞬間: update_touch_rect が「押している or 今離した」でなければ指を外し、
    ///                 get_button_name_touch_up が「指が付いていて、離した or 押していない」でボタン名を返す
    /// と、押下と離しをその Unity フレームでしか見ない。複製フレームに当たると押下は見えず (指が付かない)、
    /// 離しは次の論理フレームで「押していない」として先に指が外されるので、クリックが 2 回に 1 回以上消える
    /// (メニューのボタン、店の「買う」、会話送りのクリック、スライド入力のタップ = 決定、右クリック = キャンセル)。
    /// Esc のキーアップは Interp60 で持ち越し済みだったが、マウスは漏れていた。
    ///
    /// ここでは複製フレームで左右ボタンの押下・離しを覚え、直後の Unity フレーム (= 論理フレーム) の間だけ
    ///   - is_touch_down_list に 0 を足す (矩形に指が付く。SlideInput のタップ開始にもなる)
    ///   - is_touch_up() / is_touch_up(int) を true にする (会話送り、ボタン名の確定)
    ///   - update_touch_rect で、本体が「押していない」として外そうとする指を、矩形内にあれば残す
    ///   - InputManager.GetMouseButtonDown / Up (右クリック = キャンセル等) を true にする
    /// 60fps 表示が Off の時は複製フレームが無いので何も起きない。
    /// </summary>
    internal static class MouseLatch
    {
        private static readonly int[] s_downFrame = { -10, -10 };
        private static readonly int[] s_upFrame = { -10, -10 };
        private static int s_rescued;

        /// <summary>複製フレーム (GameMain.Update を止めた Unity フレーム) から呼ぶ。</summary>
        internal static void OnSkippedFrame()
        {
            int f = Time.frameCount;
            int seen = 0;
            for (int b = 0; b < 2; b++)
            {
                if (Input.GetMouseButtonDown(b)) { s_downFrame[b] = f; seen |= 1 << (b * 2); }
                if (Input.GetMouseButtonUp(b)) { s_upFrame[b] = f; seen |= 2 << (b * 2); }
            }
            if (seen != 0)
            {
                s_rescued++;
                if (InputDiag.Enabled)
                {
                    Plugin.Log.LogInfo(string.Format("[Mouse] latched from skipped frame: {0}{1}{2}{3} (total {4})",
                        (seen & 1) != 0 ? "L-down " : "", (seen & 2) != 0 ? "L-up " : "",
                        (seen & 4) != 0 ? "R-down " : "", (seen & 8) != 0 ? "R-up " : "", s_rescued));
                }
            }
        }

        // 複製フレームの直後の Unity フレーム (論理フレーム) の間だけ有効
        internal static bool Down(int b) { return b >= 0 && b < 2 && Time.frameCount - s_downFrame[b] == 1; }
        internal static bool Up(int b) { return b >= 0 && b < 2 && Time.frameCount - s_upFrame[b] == 1; }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.is_touch_down_list))]
    internal static class MouseLatchTouchDownListPatch
    {
        private static void Postfix(ref int[] __result)
        {
            if (!MouseLatch.Down(0)) return;
            if (__result != null && __result.Length > 0) return;
            __result = new[] { 0 };
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.is_touch_up), new Type[0])]
    internal static class MouseLatchTouchUpPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (!__result && MouseLatch.Up(0)) __result = true;
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.is_touch_up), new[] { typeof(int) })]
    internal static class MouseLatchTouchUpFingerPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (!__result && MouseLatch.Up(0) && !DebugMenu.IsActive()) __result = true;
        }
    }

    /// <summary>
    /// update_touch_rect は「押している or 今離した」でなければ矩形の指を外す。複製フレームで離していた時は、
    /// 本体の条件と同じく矩形内にある指だけ残す (Prefix で控え、Postfix で戻す)。
    /// </summary>
    [HarmonyPatch(typeof(InputManager), "update_touch_rect")]
    internal static class MouseLatchUpdateTouchRectPatch
    {
        private static readonly FieldInfo s_list = AccessTools.Field(typeof(InputManager), "m_data_list");
        private static readonly MethodInfo s_inRect = AccessTools.Method(typeof(InputManager), "is_position_rect");
        private static readonly List<TouchRect> s_keep = new List<TouchRect>();

        private static void Prefix()
        {
            s_keep.Clear();
            if (!MouseLatch.Up(0) || Input.GetMouseButton(0) || Input.GetMouseButtonUp(0)) return;
            if (s_list == null || s_inRect == null) return;
            List<TouchRect> list = s_list.GetValue(null) as List<TouchRect>;
            if (list == null) return;
            float x = Input.mousePosition.x;
            float y = Screen.height - Input.mousePosition.y;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].m_fingerid < 0) continue;
                if ((bool)s_inRect.Invoke(null, new object[] { i, x, y })) s_keep.Add(list[i]);
            }
        }

        private static void Postfix()
        {
            for (int i = 0; i < s_keep.Count; i++) s_keep[i].m_fingerid = 0;
            s_keep.Clear();
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.GetMouseButtonDown))]
    internal static class MouseLatchButtonDownPatch
    {
        private static void Postfix(int input, ref bool __result)
        {
            if (!__result && MouseLatch.Down(input) && !InputManager.IsTouchOnly()) __result = true;
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.GetMouseButtonUp))]
    internal static class MouseLatchButtonUpPatch
    {
        private static void Postfix(int input, ref bool __result)
        {
            if (!__result && MouseLatch.Up(input) && !InputManager.IsTouchOnly()) __result = true;
        }
    }
}
