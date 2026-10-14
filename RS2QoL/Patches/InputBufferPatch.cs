using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// フィールド移動の補助 2 つ。
    ///
    /// 1. 壁ずり (WallTurn) — Core.move_human_sub_r の Prefix で joy_data[0] の方向を差し替え、Postfix で戻す。
    ///    斜めを押していて、採用中の方向で WallTurnFrames フレーム進めなかったら (壁・障害物)、
    ///    斜めのもう片方の方向に切り替える。DiagonalPolicy=Keep の「進めなくなっても曲がらない」
    ///    弱点を補う。切り替えた方向は DiagonalInputPatch.Forced に入れて、斜めを押し続けている間は維持する。
    ///
    /// 2. 先行入力 (InputBuffer) — 押した方向を、ゲームが方向を読む瞬間まで捨てずに持っておく。
    ///    元のゲームは 1 マス単位で動き、新しい方向を読むのは Core.move_by_key がスクロール完了を見た瞬間だけ
    ///    (歩き 1 マス = 8 フレーム ≒ 0.27 秒)。その間に押した方向は、
    ///      a. 押して離した (短いタップ) → 完全に捨てられる
    ///      b. 右を押したまま下を足した (十字キーは斜めとして届く) → DiagonalInputPatch (Keep) が「右」に丸めるので見えない
    ///    のどちらも無かったことになる。ここでは丸める前の生入力 (DiagonalInputPatch.LastRaw) で「新しく押された方向」を
    ///    1 つ覚えておき (move_human_sub_r Prefix で毎フレーム更新)、次に move_by_key が方向を読む瞬間に human_key へ割り込ませる
    ///    (move_by_key Prefix)。別の方向を押していても割り込む。
    ///    - 親指のブレ (1 フレームだけ斜めになる) を拾わないよう、BufferTapMinFrames フレーム以上押された方向だけ採用する
    ///    - 覚えた方向を押す直前まで押していた方向 (基準方向) に親指が戻っただけなら、覚えた方向は上書きしない
    ///      (上に歩き中に 上+左→左→左+上 と転がすと、最後の上で左が消えて曲がれなくなっていた)
    ///    - ゲームがその方向を自力で読んだ (= 押した方向にマス移動が始まった) ら覚えた分は消す。離した後に余分に 1 歩進まない
    ///    - 離した後は InputBufferFrames フレームで忘れる。押したままなら忘れない
    ///    - イベント・メッセージ・移動禁止になったら忘れる
    /// </summary>
    [HarmonyPatch(typeof(Core), "move_human_sub_r")]
    internal static class InputBufferPatch
    {
        private const int DIR_MASK = DiagonalInputPatch.DIR_MASK;

        // 壁ずり
        private static int s_heldDir;          // 今押している方向 (0 なら無し)
        private static int s_blockedFrames;    // 方向を押しているのにマス移動が始まらない連続フレーム数
        private static int s_savedJoy;
        private static bool s_injected;

        // 先行入力 (1 スロット)
        private static int s_prevRaw;
        internal static int TapDir;            // 覚えている方向 (方向ビット 1 つ)。0 なら無し
        internal static int TapHeldFrames;     // その方向が押され続けているフレーム数
        internal static int TapReleasedFrames; // 離してからのフレーム数。押したままなら -1
        private static int s_baseDir;          // TapDir を押す直前まで押していた方向 (方向ビット 1 つ)。0 なら無し

        internal static bool CanMove(Core c)
        {
            // pause_sw は見ない: 本体は Start で 1 にするだけで、ゲーム開始の initail_work 以外で 0 に戻さず読みもしない (死んだフラグ)。
            // 以前はここで見ていたため、一度 Start を押すと以後ずっと先行入力・壁ずりが止まっていた (2026-10-10 実機ログで確認)。
            if (QuitGame.BlockInput) return false;   // 終了ダイアログ中は覚えない・差し込まない (ダイアログ中に押した方向で 1 歩動いてしまう)
            return c.man_cant_move == 0 && c.mess_flag == 0 && c.ivent_sw == 0 && c.man_road_sw == 0
                && c.exevt_flg == 0 && c.battle_flg == 0 && c.tono_flg == 0 && c.exdemo_flg == 0
                && c.demo_flag == 0;
        }

        private static bool IsStepping(Core c)
        {
            return c.map_scrol_flag_v != 0 || c.map_scrol_flag_h != 0 || c.man_move_flag_v != 0 || c.man_move_flag_h != 0;
        }

        private static void Inject(Core c, int dir)
        {
            s_savedJoy = c.joy_data[0];
            c.joy_data[0] = (s_savedJoy & ~DIR_MASK) | dir;
            s_injected = true;
        }

        internal static void ClearTap()
        {
            TapDir = 0;
            TapHeldFrames = 0;
            TapReleasedFrames = -1;
            s_baseDir = 0;
        }

        private static void Prefix(Core __instance)
        {
            Core c = __instance;
            s_injected = false;
            // しきい値は実時間フレーム。倍速中はゲームフレームが 1 Unity フレームに複数回進むので倍率を掛ける
            int wallTurn = (Plugin.WallTurnFrames != null ? Plugin.WallTurnFrames.Value : 3) * GameSpeed.FrameScale();
            int dir = c.joy_data[0] & DIR_MASK;
            bool stepping = IsStepping(c);

            TrackTap(c);

            if (dir == 0)
            {
                s_heldDir = 0;
                s_blockedFrames = 0;
                return;
            }

            if (dir != s_heldDir)
            {
                s_heldDir = dir;
                s_blockedFrames = 0;
            }

            // 壁ずり: 斜めを押していて採用中の方向で進めないなら、もう片方へ
            s_blockedFrames = stepping ? 0 : s_blockedFrames + 1;
            if (wallTurn > 0 && s_blockedFrames >= wallTurn && CanMove(c))
            {
                int other = DiagonalInputPatch.PickOne(DiagonalInputPatch.LastRaw & ~dir & DIR_MASK);
                if (other != 0)
                {
                    if (InputDiag.Enabled) Plugin.Log.LogInfo(string.Format(
                        "[WallTurn] t={0} blocked dir=0x{1:X} -> 0x{2:X}", InputDiag.T(), dir, other));
                    DiagonalInputPatch.Forced = other;
                    s_heldDir = other;
                    s_blockedFrames = 0;
                    Inject(c, other);
                }
            }
        }

        // 生入力の押下エッジを 1 スロットで追う
        private static void TrackTap(Core c)
        {
            int window = (Plugin.InputBufferFrames != null ? Plugin.InputBufferFrames.Value : 10) * GameSpeed.FrameScale();
            int raw = DiagonalInputPatch.LastRaw & DIR_MASK;
            int prevRaw = s_prevRaw;
            int newly = raw & ~prevRaw;
            s_prevRaw = raw;

            if (window <= 0 || !CanMove(c))
            {
                ClearTap();
                return;
            }

            if (newly != 0)
            {
                if (TapDir != 0 && s_baseDir != 0 && (newly & ~s_baseDir) == 0)
                {
                    // 親指が「TapDir を押す直前まで押していた方向」に戻っただけ (上→上+左→左→左+上 の最後の上など)。
                    // 「最後に押したものを優先」で上書きすると、本体が自力で読もうとしていた TapDir を基準方向で潰して
                    // 曲がれなくなる (2026-10-12 実機ログ: 上に歩き中の左が消えて上に進み続けた)。戻った分は新しい入力と見なさない。
                    if (InputDiag.Enabled) Plugin.Log.LogInfo(string.Format(
                        "[Buffer] t={0} keep dir=0x{1:X} (returned to base 0x{2:X})", InputDiag.T(), TapDir, s_baseDir));
                }
                else
                {
                    // 新しく押された方向で上書き (最後に押したものを優先)。直前まで押していた方向を基準方向として覚える
                    TapDir = DiagonalInputPatch.PickOne(newly);
                    TapHeldFrames = 1;
                    TapReleasedFrames = -1;
                    s_baseDir = DiagonalInputPatch.PickOne(prevRaw & ~newly & DIR_MASK);
                    return;
                }
            }
            if (TapDir == 0) return;

            if ((raw & TapDir) != 0)
            {
                TapHeldFrames++;
                TapReleasedFrames = -1;
            }
            else
            {
                TapReleasedFrames++;
                if (TapReleasedFrames > window)
                {
                    if (InputDiag.Enabled) Plugin.Log.LogInfo(string.Format("[Buffer] t={0} expired dir=0x{1:X}", InputDiag.T(), TapDir));
                    ClearTap();
                }
            }
        }

        private static void Postfix(Core __instance)
        {
            if (s_injected)
            {
                __instance.joy_data[0] = s_savedJoy;
                s_injected = false;
            }
        }
    }

    /// <summary>
    /// 先行入力の注入。move_by_key が方向を読む条件 (スクロール完了・フェード無し・ジャンプ中でない・イベント無し) が
    /// 揃ったフレームで、覚えている方向を human_key に割り込ませる。human_key は毎フレーム作り直されるので戻す必要は無い。
    /// </summary>
    [HarmonyPatch(typeof(Core), "move_by_key")]
    internal static class InputBufferInjectPatch
    {
        private static int ToHumanKey(int dir)
        {
            switch (dir)
            {
                case DiagonalInputPatch.DIR_UP: return 8;
                case DiagonalInputPatch.DIR_DOWN: return 4;
                case DiagonalInputPatch.DIR_LEFT: return 2;
                case DiagonalInputPatch.DIR_RIGHT: return 1;
            }
            return 0;
        }

        private static void Prefix(Core __instance)
        {
            Core c = __instance;
            if (InputBufferPatch.TapDir == 0) return;
            if ((c.map_scrol_flag_v | c.map_scrol_flag_h | c.fade_now) != 0 || Sys.fadeflg) return; // まだ読まない
            if (c.jump_flag != 0 || c.ivent_sw != 0 || !InputBufferPatch.CanMove(c)) return;

            int cur = c.human_key & 0xF;
            int want = ToHumanKey(InputBufferPatch.TapDir);
            if (cur == want)
            {
                // ゲームが自力で読んだ → 役目終わり
                InputBufferPatch.ClearTap();
                return;
            }

            int minFrames = (Plugin.BufferTapMinFrames != null ? Plugin.BufferTapMinFrames.Value : 1) * GameSpeed.FrameScale();
            if (InputBufferPatch.TapHeldFrames < minFrames) return; // ブレかも知れないのでまだ採用しない

            if (InputDiag.Enabled) Plugin.Log.LogInfo(string.Format(
                "[Buffer] t={0} inject dir=0x{1:X} (held {2}f, released {3}f) over human_key={4}",
                InputDiag.T(), InputBufferPatch.TapDir, InputBufferPatch.TapHeldFrames, InputBufferPatch.TapReleasedFrames, cur));
            c.human_key = (c.human_key & ~0xF) | want;
            InputBufferPatch.ClearTap();
        }
    }
}
