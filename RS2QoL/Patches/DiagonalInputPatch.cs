using HarmonyLib;

namespace RS2QoL.Patches
{
    public enum DiagonalPolicy
    {
        Keep,
        Newest,
    }

    /// <summary>
    /// 斜め入力 (方向ビットが 2 つ以上) を 1 方向に丸める。
    ///
    /// Sys.syncKey(k) は方向ビット (0xF0000) が 2 つ以上立っていると
    /// 「前フレームから押し続けている方向だけ残し、無ければ方向を全部捨てる」というフィルタをかける
    /// (スマホ版の仮想スティック向けの処理)。AxisThresholdPatch で斜めが両ビットとして入るようになったので、
    /// syncKey に渡る前にここで必ず 1 方向に丸める。丸めた後は方向ビットが 1 つなので元のフィルタは
    /// 素通りする (元処理は止めないので Prefix でも安全)。
    ///
    /// Xbox 系パッドの十字キーは 8 方向ハットなので「右を押したまま下も押す」は右下 (斜め) として届く。
    /// 優先順位は config の DiagonalPolicy で切り替える:
    ///   Keep   : 今ゲームが認識している方向 (Sys.key[0]) が斜めに含まれていればそれを維持する。
    ///            親指のブレで一瞬斜めになっても方向が変わらない。
    ///            ただし同じ斜めが DiagonalSwitchFrames フレームより長く続いたら「意図して新しい方向を足した」と
    ///            みなし、斜めのもう片方 (新しい方向) へ転換する。転換後は斜めを押し続けている間その方向を維持する。
    ///            維持している方向で進めなくなった時は InputBufferPatch (壁ずり) が Forced でもう片方に切り替える。
    ///   Newest : このフレームで新しく押された方向へ即座に向く (ブレに弱い)。
    /// どちらも、ニュートラルから斜めに入った時は意図が分からないので DiagonalGraceFrames だけ待ち、
    /// それでも斜めなら 右 > 左 > 下 > 上 (元のフィールド移動処理と同じ水平優先)。
    /// </summary>
    [HarmonyPatch(typeof(Sys), nameof(Sys.syncKey))]
    internal static class DiagonalInputPatch
    {
        public const int DIR_MASK = 0xF0000;
        public const int DIR_LEFT = 0x10000;
        public const int DIR_UP = 0x20000;
        public const int DIR_RIGHT = 0x40000;
        public const int DIR_DOWN = 0x80000;

        /// <summary>直近フレームの生の方向ビット (丸める前)。他のパッチが斜めの内訳を見るのに使う。</summary>
        internal static int LastRaw;
        /// <summary>壁ずり・斜め長押しで強制した方向。生入力にこの方向が含まれている間は優先して採用する。</summary>
        internal static int Forced;

        private static int s_prevRaw;
        private static int s_graceCount;
        private static int s_diagFrames;   // 同じ斜めが連続しているフレーム数

        private static void Prefix(ref int __0)
        {
            int raw = __0 & DIR_MASK;
            int resolved = raw;
            // しきい値は実時間フレーム。倍速中は syncKey が 1 Unity フレームに複数回呼ばれるので倍率を掛ける
            int scale = GameSpeed.FrameScale();
            int grace = (Plugin.DiagonalGraceFrames != null ? Plugin.DiagonalGraceFrames.Value : 4) * scale;
            int switchFrames = (Plugin.DiagonalSwitchFrames != null ? Plugin.DiagonalSwitchFrames.Value : 3) * scale;

            if (raw != 0 && (raw & (raw - 1)) != 0) // 2 ビット以上
            {
                int newly = raw & ~s_prevRaw;
                int held = Sys.key[0] & DIR_MASK;
                bool newest = Plugin.DiagonalPolicy != null && Plugin.DiagonalPolicy.Value == DiagonalPolicy.Newest;
                bool newlySingle = newly != 0 && (newly & (newly - 1)) == 0;
                s_diagFrames = (raw == s_prevRaw) ? s_diagFrames + 1 : 1;

                if (Forced != 0 && (raw & Forced) == 0)
                {
                    Forced = 0;
                }

                if (Forced != 0)
                {
                    resolved = Forced;
                }
                else if (newest && newlySingle)
                {
                    resolved = newly;
                }
                else if ((raw & held) != 0)
                {
                    int other = PickOne(raw & ~held);
                    if (!newest && switchFrames > 0 && s_diagFrames > switchFrames && other != 0)
                    {
                        // 斜めを意図して押し続けている → 新しく足した方向へ転換
                        resolved = other;
                        Forced = other;
                        if (InputDiag.Enabled) Plugin.Log.LogInfo(string.Format(
                            "[DiagSwitch] t={0} raw=0x{1:X} held=0x{2:X} -> 0x{3:X} after {4} frames",
                            InputDiag.T(), raw, held, other, s_diagFrames - 1));
                    }
                    else
                    {
                        resolved = held;
                    }
                }
                else if (newlySingle)
                {
                    resolved = newly;
                }
                else if (held == 0 && s_graceCount < grace)
                {
                    // ニュートラルからの斜め: 少し待って本命の方向を見る
                    s_graceCount++;
                    resolved = 0;
                }
                else
                {
                    resolved = PickOne(raw);
                }
                Plugin.Log.LogDebug(string.Format(
                    "[DiagonalInput] raw=0x{0:X} newly=0x{1:X} held=0x{2:X} forced=0x{3:X} grace={4} diag={5} -> 0x{6:X}",
                    raw, newly, held, Forced, s_graceCount, s_diagFrames, resolved));
            }
            else
            {
                s_graceCount = 0;
                s_diagFrames = 0;
                Forced = 0;
            }

            s_prevRaw = raw;
            LastRaw = raw;
            __0 = (__0 & ~DIR_MASK) | resolved;
        }

        internal static int PickOne(int dirs)
        {
            if ((dirs & DIR_RIGHT) != 0) return DIR_RIGHT;
            if ((dirs & DIR_LEFT) != 0) return DIR_LEFT;
            if ((dirs & DIR_DOWN) != 0) return DIR_DOWN;
            if ((dirs & DIR_UP) != 0) return DIR_UP;
            return 0;
        }
    }
}
