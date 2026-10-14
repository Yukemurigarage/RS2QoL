using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// フィールドのポーズ (Start) まわり。
    ///
    /// 本体の仕組み: フィールドで Start を押すと Core.debug_message_put がメッセージイベント (8,9) を起動し、
    /// move_ivent_routine_sub → its_message_ivent → message_start で mess_no = 0x809 のメッセージが表示される。
    /// 表示中の待ちは 2 か所: メッセージ命令 44 = Core.pause_sa と、メッセージ末尾の待ち (message_lop2 の pauseflg&2)。
    /// どちらも key_is_what に 0x700171 (決定/キャンセル/メニュー/Start/LB/RB/情報) のどれかが入ると解除する。
    /// つまり「どのボタンでも解除される」のは本体仕様。
    ///
    /// ここではポーズ用メッセージ (IsPaused 参照) の間だけ、Start とキャンセル以外のボタンをこの 2 つに見せないようにして
    /// 「Start を押し直すかキャンセルを押した時だけ解除」にする (キャンセルはユーザー要望 2026-10-13。決定・メニュー・LB / RB・情報では解除しない)。
    /// 他のメッセージには mess_no で絞って影響させない。
    /// </summary>
    internal static class FieldPause
    {
        public const int KEY_PAUSE = 0x100;
        public const int KEY_CANCEL = 0x400000;   // キャンセルの機能ビット (Core.joy_data の 4194304 と同じ)
        public const int RELEASE_BITS = 0x700171;
        private const int KEEP_BITS = KEY_PAUSE | KEY_CANCEL;   // 解除に使うボタン

        /// <summary>
        /// Start のポーズ中か。戦闘にはポーズが無いので戦闘中は false。
        /// 実機ログ (2026-10-07): Start → ivent_no=(8,9) → mess_no=0x809 → すぐ 0xB34 へ飛んで pauseflg=1 で待つ。
        /// 解除で ivent_no は (31,212) に変わる。メッセージ番号は途中で変わるので、
        /// 「イベント (8,9) が起動したメッセージが表示中」で判定する。
        /// </summary>
        public static bool IsPaused()
        {
            return IsPaused(Main.core);
        }

        public static bool IsPaused(Core core)
        {
            return core != null && core.mess_flag == 1 && core.ivent_no[0] == 8 && core.ivent_no[1] == 9;
        }

        private static bool s_wasPaused;

        /// <summary>
        /// 毎論理フレーム (ConfigMenu.Tick) 呼ぶ。本体のポーズは効果音を鳴らさない (msg 0x809 → 0xB34 に音の命令が無い) ので、
        /// 始まった時に決定音、解除された時にカーソル音を鳴らす (ユーザー要望 2026-10-13)。suppress の間 (MOD の画面が自分で
        /// ポーズをかける流れ: どこでも大臣・終了・ソフトリセット・F1 の設定画面) は鳴らさず、状態の追跡だけ行う (その画面の開閉の音で代える)。
        /// </summary>
        public static void TrackSound(bool suppress)
        {
            bool now = IsPaused();
            if (now == s_wasPaused) return;
            s_wasPaused = now;
            if (suppress) return;
            if (now) UiParts.SeOk(); else UiParts.SeCursor();
        }

        /// <summary>診断用: ポーズ判定に使う Core の状態を 1 行にする。</summary>
        public static string StateText()
        {
            Core core = Main.core;
            if (core == null) return "core=null";
            return string.Format("mess_flag={0} mess_no=0x{1:X} pauseflg={2} ivent_sw={3} ivent_no=({4},{5}) key_is_what=0x{6:X}",
                core.mess_flag, core.mess_no, core.pauseflg, core.ivent_sw, core.ivent_no[0], core.ivent_no[1], core.key_is_what);
        }

        /// <summary>Prefix/Postfix で受け渡す「隠す前の値」と「隠した後の値」。</summary>
        public struct Masked
        {
            public int Orig, Set;
            public bool Applied;
        }

        /// <summary>ポーズ用メッセージ中で Start もキャンセルも押されていなければ、解除ビットを隠した key_is_what にする。</summary>
        public static Masked MaskRelease(Core core)
        {
            Masked m;
            m.Orig = core.key_is_what;
            m.Set = m.Orig;
            m.Applied = false;
            if (IsPaused(core) && (m.Orig & KEEP_BITS) == 0)
            {
                m.Set = m.Orig & ~RELEASE_BITS;
                m.Applied = true;
                core.key_is_what = m.Set;
            }
            return m;
        }

        /// <summary>対象メソッドが key_is_what を書き換えていなければ元の値に戻す (解除時に 0 にする処理を潰さないため)。</summary>
        public static void Restore(Core core, Masked m)
        {
            if (m.Applied && core.key_is_what == m.Set) core.key_is_what = m.Orig;
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.pause_sa))]
    internal static class PauseReleaseOnlyByStartPatch
    {
        private static void Prefix(Core __instance, out FieldPause.Masked __state)
        {
            __state = FieldPause.MaskRelease(__instance);
        }

        private static void Postfix(Core __instance, FieldPause.Masked __state)
        {
            FieldPause.Restore(__instance, __state);
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.message_lop2))]
    internal static class PauseMessageEndOnlyByStartPatch
    {
        private static void Prefix(Core __instance, out FieldPause.Masked __state)
        {
            __state = FieldPause.MaskRelease(__instance);
        }

        private static void Postfix(Core __instance, FieldPause.Masked __state)
        {
            FieldPause.Restore(__instance, __state);
        }
    }
}
