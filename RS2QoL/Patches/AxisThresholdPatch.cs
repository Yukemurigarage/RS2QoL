using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 十字キー/スティックの斜め入力で移動が止まる問題の修正 (本体)。
    ///
    /// Util.GetKeypadState はスティック/十字キーの軸 (STEAM_Horizontal(_sub) / STEAM_Vertical(_sub)) が
    /// ±0.7 を超えた時だけ方向ビットを立てる。ところが実機の十字キーは斜めに入れると各軸が 0.638 で
    /// 入ってくる (2026-10-07 実測。Bluetooth の Xbox パッドを汎用 HID として読み、ハットの 0.707 を
    /// 不感帯 0.19 で引き直した値)。両軸とも 0.7 未満 → 方向なし → キャラが止まる。
    ///
    /// ここでは閾値 0.5 で拾い直したビットを元の結果に OR する (効くのは 0.5〜0.7 の帯だけ)。
    /// さらに config の XInputDpad が true なら XInput で読んだ十字キーと左右スティックも OR する
    /// (Unity の HID 経路で押したままなのに無入力になる現象の対策。XInputPad 参照)。
    /// 斜めで両方向が立った場合は DiagonalInputPatch (Sys.syncKey) が 1 方向に丸める。
    ///
    /// 診断ログ (InputDiagPadPatch) が OR 後の結果を見られるよう、同じメソッドの Postfix の中で先に走らせる。
    /// </summary>
    [HarmonyPatch(typeof(Util), nameof(Util.GetKeypadState))]
    [HarmonyPriority(Priority.High)]
    internal static class AxisThresholdPatch
    {
        private const int DIR_LEFT = 0x10000;
        private const int DIR_UP = 0x20000;
        private const int DIR_RIGHT = 0x40000;
        private const int DIR_DOWN = 0x80000;
        // スティックは 0.5、十字キー (sub 軸) は値が ±1 か 0.638 しか来ないので 0.3 で十分拾える
        private const float STICK_THRESHOLD = 0.5f;
        private const float DPAD_THRESHOLD = 0.3f;

        private const int DIR_MASK = 0xF0000;

        private static void Postfix(ref int __result)
        {
            bool useXi = Plugin.XInputDpad == null || Plugin.XInputDpad.Value;
            int xi = useXi ? (XInputPad.ReadDpad() | XInputPad.ReadStick()) : 0;

            // XInput のパッドが繋がっている間は方向を XInput だけから読む。
            // Unity の HID 経路は「押したままなのに無入力」だけでなく「上→左と転がした時に左上で数秒固まる」
            // (2026-10-07 実測、XInput 側は正しく左だけ) こともあり、OR すると固まった方向が混ざって斜め扱いになる。
            // キーボードの方向キー (KEY_*) は Unity から読み直して残す。XInput が無いパッドは従来どおり Unity 側を使う。
            bool exclusive = useXi && (Plugin.XInputDpadExclusive == null || Plugin.XInputDpadExclusive.Value) && XInputPad.AnyConnected;
            if (exclusive)
            {
                __result = (__result & ~DIR_MASK) | KeyboardDirs() | xi;
                return;
            }

            __result |= AxisToDir(Input.GetAxis("STEAM_Horizontal"), Input.GetAxis("STEAM_Vertical"), STICK_THRESHOLD)
                      | AxisToDir(Input.GetAxis("STEAM_Horizontal_sub"), Input.GetAxis("STEAM_Vertical_sub"), DPAD_THRESHOLD)
                      | xi;
        }

        // Util.GetKeypadState と同じ KEY_ 仮想ボタン (キーボードの方向キー)
        private static int KeyboardDirs()
        {
            int d = 0;
            if (Input.GetButton("KEY_Left")) d |= DIR_LEFT;
            if (Input.GetButton("KEY_Up")) d |= DIR_UP;
            if (Input.GetButton("KEY_Right")) d |= DIR_RIGHT;
            if (Input.GetButton("KEY_Down")) d |= DIR_DOWN;
            return d;
        }

        private static int AxisToDir(float x, float y, float threshold)
        {
            int d = 0;
            if (x > threshold) d |= DIR_RIGHT;
            else if (x < -threshold) d |= DIR_LEFT;
            if (y > threshold) d |= DIR_UP;
            else if (y < -threshold) d |= DIR_DOWN;
            return d;
        }
    }
}
