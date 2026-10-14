using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// XInput (Windows 標準のゲームパッド API) で十字キーを直接読む。
    ///
    /// このゲームは Unity 5.6 の旧 Input で、Bluetooth 接続の Xbox コントローラーを汎用 HID として読んでいる
    /// (十字キーがハットスイッチ→軸 6/7 に変換され、斜めが 0.638 になるのはそのため)。
    /// 実機で「十字キーを押したままなのに 4 軸すべて 0 (無入力) になって止まる」現象が出た (2026-10-07)。
    /// Unity の HID 経路の問題なら、同じコントローラーを XInput 経由で読めば落ちないはずなので、
    /// ここで読んだ十字キーを Util.GetKeypadState の結果に OR する (AxisThresholdPatch)。
    /// 物理的な接点や Bluetooth 自体の問題なら両方とも落ちるので、診断ログ ([Pad] の xi=) で切り分けられる。
    ///
    /// 接続されていないスロットの XInputGetState は遅いことがあるので、未接続スロットは 60 フレームに 1 回だけ見る。
    /// </summary>
    internal static class XInputPad
    {
        private const int ERROR_SUCCESS = 0;
        private const ushort DPAD_UP = 0x0001;
        private const ushort DPAD_DOWN = 0x0002;
        private const ushort DPAD_LEFT = 0x0004;
        private const ushort DPAD_RIGHT = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        private static class Xi14 { [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] public static extern int Get(int i, out XINPUT_STATE s); }
        private static class Xi13 { [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")] public static extern int Get(int i, out XINPUT_STATE s); }
        private static class Xi910 { [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] public static extern int Get(int i, out XINPUT_STATE s); }

        private delegate int GetStateFn(int i, out XINPUT_STATE s);
        private static GetStateFn s_get;
        private static bool s_unavailable;
        private static readonly bool[] s_connected = new bool[4];
        private static int s_lastFrame = -1;
        private static int s_scanTick;
        private static int s_lastDpad;
        private static int s_lastButtons;

        // XInput の wButtons のビット。PadMap.PhysicalNames の順 (Button0..3, LB, RB, Start, Select) に対応
        private const ushort XI_A = 0x1000, XI_B = 0x2000, XI_X = 0x4000, XI_Y = 0x8000;
        private const ushort XI_LB = 0x0100, XI_RB = 0x0200, XI_START = 0x0010, XI_BACK = 0x0020;
        private const ushort XI_LTHUMB = 0x0040, XI_RTHUMB = 0x0080;
        // トリガーは wButtons に無い (bLeftTrigger/bRightTrigger の 0..255)。wButtons の未使用ビットに合成して同じ表で扱う
        private const ushort XI_LT_SYNTH = 0x0400, XI_RT_SYNTH = 0x0800;
        // トリガーの押下判定 (0..255)。通常は 1/4 (64。半分 (128) だと浅い引きが無視され「反応しない」と感じる。XInput 推奨の最小値は 30)。
        // ソフトリセット・ゲーム終了を割り当てたトリガーは、触れただけで発動しないように奥まで引いた時 (既定 240 ≒ 94%) だけ押下扱い。
        // 255 ちょうどを要求しないのは、個体差・摩耗で 255 まで届かないパッドがあるため
        internal static BepInEx.Configuration.ConfigEntry<int> TriggerThreshold, TriggerDeepThreshold;
        private static int TriggerThresholdFor(int physical)
        {
            int normal = TriggerThreshold != null ? TriggerThreshold.Value : 64;
            if (TriggerDeepThreshold == null || PadMap.Entries == null || physical >= PadMap.Entries.Length || PadMap.Entries[physical] == null) return normal;
            PadFunction f = PadMap.Entries[physical].Value;
            return f == PadFunction.SoftReset || f == PadFunction.Quit ? Math.Max(normal, TriggerDeepThreshold.Value) : normal;
        }
        private static readonly ushort[] s_physicalToXi = { XI_A, XI_B, XI_X, XI_Y, XI_LB, XI_RB, XI_START, XI_BACK, XI_LT_SYNTH, XI_RT_SYNTH, XI_LTHUMB, XI_RTHUMB };

        /// <summary>
        /// 物理ボタン (PadMap の並び) が XInput で押されているか。
        /// 実機ログ (2026-10-07 夜) で、A を押したのに Unity の Input.GetButton に一度も現れないフレーム列が確認された
        /// (十字キーで見つかったのと同じ HID 経路の読み取り落ち)。十字キーと同様にここで併読して OR する。
        /// </summary>
        internal static bool IsButtonDown(int physicalIndex)
        {
            if (physicalIndex < 0 || physicalIndex >= s_physicalToXi.Length) return false;
            ReadDpad(); // このフレームの読み取りを確実に行う (同一フレーム内はキャッシュ)
            return (s_lastButtons & s_physicalToXi[physicalIndex]) != 0;
        }

        /// <summary>直近フレームの wButtons (接続中スロットの OR)。診断用。</summary>
        internal static int LastButtons { get { return s_lastButtons; } }

        private static int s_lastStick;
        private const int STICK_THRESHOLD = 16384; // 32767 * 0.5

        /// <summary>このフレームの左右スティックを方向ビットで読む (ReadDpad と同じフレームキャッシュ)。</summary>
        internal static int ReadStick()
        {
            ReadDpad();
            return s_lastStick;
        }

        private static int ThumbToDir(short x, short y)
        {
            int d = 0;
            if (x > STICK_THRESHOLD) d |= DiagonalInputPatch.DIR_RIGHT;
            else if (x < -STICK_THRESHOLD) d |= DiagonalInputPatch.DIR_LEFT;
            if (y > STICK_THRESHOLD) d |= DiagonalInputPatch.DIR_UP;
            else if (y < -STICK_THRESHOLD) d |= DiagonalInputPatch.DIR_DOWN;
            return d;
        }
        internal static bool AnyConnected { get { return s_connected[0] || s_connected[1] || s_connected[2] || s_connected[3]; } }

        /// <summary>このフレームの十字キーを読む (同一フレーム内の 2 回目以降はキャッシュ)。</summary>
        internal static int ReadDpad()
        {
            int frame = Time.frameCount;
            if (frame == s_lastFrame) return s_lastDpad;
            s_lastFrame = frame;
            s_lastDpad = 0;
            s_lastButtons = 0;
            s_lastStick = 0;

            if (s_unavailable) return 0;
            if (s_get == null && !Resolve()) return 0;

            // 未接続スロットの再確認は「この関数が新しいフレームで呼ばれた回数」で数える。
            // 以前は Time.frameCount % 60 で見ていたが、ここは論理フレーム (Util.GetKeypadState) からしか呼ばれず、
            // 60fps 表示では論理フレームが 2 Unity フレームに 1 回で偶奇が起動ごとに固定なので、
            // 奇数側に当たった起動ではスロット 0 を一度も調べず XInput が永久に「未接続」のままだった (2026-10-09 実機ログ)。
            s_scanTick++;
            for (int i = 0; i < 4; i++)
            {
                if (!s_connected[i] && (s_scanTick + i) % 60 != 0) continue;
                XINPUT_STATE st;
                int rc;
                try
                {
                    rc = s_get(i, out st);
                }
                catch (Exception e)
                {
                    // 接続済みの印も消す。残すと AxisThreshold / ButtonRemap の「XInput だけ読む」経路が生きたまま
                    // XInput は 0 を返し続け、方向もボタンも再起動まで効かなくなる
                    s_unavailable = true;
                    Array.Clear(s_connected, 0, s_connected.Length);
                    Plugin.Log.LogWarning("[XInput] disabled: " + e.Message);
                    return 0;
                }
                bool ok = rc == ERROR_SUCCESS;
                if (ok != s_connected[i])
                {
                    s_connected[i] = ok;
                    Plugin.Log.LogInfo(string.Format("[XInput] slot {0} {1}", i, ok ? "connected" : "disconnected"));
                }
                if (!ok) continue;
                int b = st.Gamepad.wButtons & ~(XI_LT_SYNTH | XI_RT_SYNTH);
                if (st.Gamepad.bLeftTrigger >= TriggerThresholdFor(8)) b |= XI_LT_SYNTH;    // 8 / 9 = PadMap.PhysicalNames の LT / RT
                if (st.Gamepad.bRightTrigger >= TriggerThresholdFor(9)) b |= XI_RT_SYNTH;
                s_lastButtons |= b;
                if ((b & DPAD_UP) != 0) s_lastDpad |= DiagonalInputPatch.DIR_UP;
                if ((b & DPAD_DOWN) != 0) s_lastDpad |= DiagonalInputPatch.DIR_DOWN;
                if ((b & DPAD_LEFT) != 0) s_lastDpad |= DiagonalInputPatch.DIR_LEFT;
                if ((b & DPAD_RIGHT) != 0) s_lastDpad |= DiagonalInputPatch.DIR_RIGHT;
                // 左右スティックも併読して方向ビットにする (本体仕様どおり右スティックも左と同じ扱い)。
                // しきい値は AxisThresholdPatch のスティックと同じ 0.5 (32767 * 0.5)
                s_lastStick |= ThumbToDir(st.Gamepad.sThumbLX, st.Gamepad.sThumbLY)
                             | ThumbToDir(st.Gamepad.sThumbRX, st.Gamepad.sThumbRY);
            }
            return s_lastDpad;
        }

        private static bool Resolve()
        {
            XINPUT_STATE st;
            GetStateFn[] candidates = { Xi14.Get, Xi13.Get, Xi910.Get };
            string[] names = { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    candidates[i](0, out st);
                    s_get = candidates[i];
                    Plugin.Log.LogInfo("[XInput] using " + names[i]);
                    return true;
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
                catch (Exception e)
                {
                    // BadImageFormatException 等。捕まえずに抜けると GetKeypadState の Postfix が毎フレーム投げ続ける
                    Plugin.Log.LogWarning("[XInput] " + names[i] + ": " + e.Message);
                }
            }
            s_unavailable = true;
            Plugin.Log.LogWarning("[XInput] no usable xinput dll; XInput d-pad disabled");
            return false;
        }
    }
}
