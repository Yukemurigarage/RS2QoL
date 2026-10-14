using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// フィールド / イベントの会話ウィンドウの送りを「ボタンを新たに押した瞬間」だけにし、十字キーでも送れるようにする。
    ///
    /// 本体は会話の「▼ 待ち」(Core.pause_sa) と「文末待ち」(Core.message_lop2 の末尾分岐) を
    /// Core.key_is_what で送る。key_is_what は Core.get_key_rep が joy_data[0] (押しっぱなし状態) から作る値で、
    /// 押した瞬間に加えて長押しのリピート (400ms 後から 120ms 間隔) でも立つ。そのため決定を押したままだと
    /// 文章が 120ms ごとに流れて読めない。選択肢 (Core.put_select) も同じ値で決定するので、押したままだと選択肢まで勝手に決まる。
    ///
    /// ここでは対象メソッドの間だけ key_is_what を「押下エッジ (joy_data[1] = Sys.key[1])」に差し替え、終わったら戻す。
    /// 受け付けるボタンの種類は本体のまま (決定 / キャンセル / メニュー / ポーズ / L / R / 情報)。
    /// 本体は十字キーでは送れない (方向ビットがマスク外) ので、十字キーを新たに押した瞬間を決定扱いにして足す (DialogueAdvanceByDpad)。
    /// 選択肢ではカーソル移動 (上下のリピート) は本体のまま残し、決定 / キャンセルのビットだけエッジにする。
    /// 戦闘中のメッセージ (タイマー自動送り) には触らない。
    /// </summary>
    internal static class DialogueAdvance
    {
        public static ConfigEntry<bool> WaitForPress;
        public static ConfigEntry<bool> AdvanceByDpad;

        // Core.pause_sa / message_lop2 が見るビット (決定 0x1|0x100000, キャンセル 0x400000, メニュー 0x200000, ポーズ 0x100, L 0x10, 情報 0x20, R 0x40)
        private const int ADVANCE_BITS = 0x700171;
        private const int OK_BIT = 0x100000;
        private const int DIR_BITS = 0xF0000;
        // Core.put_select が決定 / キャンセルとして見るビット
        private const int SELECT_BITS = 0x100001 | 0x400000;

        public static void Bind(ConfigFile config)
        {
            WaitForPress = config.Bind("Field", "DialogueWaitForPress", true,
                "会話ウィンドウの送り (▼ と文末) と選択肢の決定を、ボタンを新たに押した時だけにする (長押しでは流れない)");
            AdvanceByDpad = config.Bind("Field", "DialogueAdvanceByDpad", true,
                "会話ウィンドウを十字キー (新たに押した瞬間) でも送れるようにする");
        }

        public static bool WaitEnabled { get { return WaitForPress != null && WaitForPress.Value; } }
        public static bool DpadEnabled { get { return AdvanceByDpad != null && AdvanceByDpad.Value; } }

        /// <summary>▼ 待ち / 文末待ち用。戻り値は元の key_is_what。</summary>
        public static int AdvanceMask(Core c)
        {
            int orig = c.key_is_what;
            int edge = c.joy_data[1];
            int v = WaitEnabled ? (orig & ~ADVANCE_BITS) | (edge & ADVANCE_BITS) : orig;
            if (DpadEnabled && (edge & DIR_BITS) != 0) v |= OK_BIT;
            c.key_is_what = v;
            return orig;
        }

        /// <summary>選択肢用。決定 / キャンセルだけエッジにする。戻り値は元の key_is_what。</summary>
        public static int SelectMask(Core c)
        {
            int orig = c.key_is_what;
            c.key_is_what = (orig & ~SELECT_BITS) | (c.joy_data[1] & SELECT_BITS);
            return orig;
        }
    }

    // ▼ 待ち (ページ送り)。受け付けても key_is_what は触らないので常に戻す
    [HarmonyPatch(typeof(Core), "pause_sa")]
    internal static class DialogueAdvancePausePatch
    {
        private static void Prefix(Core __instance, out int __state)
        {
            __state = (DialogueAdvance.WaitEnabled || DialogueAdvance.DpadEnabled) ? DialogueAdvance.AdvanceMask(__instance) : int.MinValue;
        }

        private static void Postfix(Core __instance, int __state)
        {
            if (__state != int.MinValue) __instance.key_is_what = __state;
        }
    }

    // 文末待ち。message_lop2 は毎フレーム文字送りにも使われるので、文末 (mess_adrs >= end_mess_adrs) の時だけ差し替える。
    // 受け付けると本体が key_is_what = 0 にする (二重処理防止) ので、その場合は戻さない
    [HarmonyPatch(typeof(Core), "message_lop2")]
    internal static class DialogueAdvanceEndPatch
    {
        private struct State { public int orig; public int masked; public bool applied; }

        private static void Prefix(Core __instance, out State __state)
        {
            __state = default(State);
            if (!(DialogueAdvance.WaitEnabled || DialogueAdvance.DpadEnabled)) return;
            if (__instance.mess_flag == 0 || Core.mess_adrs < __instance.end_mess_adrs) return;
            __state.orig = DialogueAdvance.AdvanceMask(__instance);
            __state.masked = __instance.key_is_what;
            __state.applied = true;
        }

        private static void Postfix(Core __instance, State __state)
        {
            if (__state.applied && __instance.key_is_what == __state.masked) __instance.key_is_what = __state.orig;
        }
    }

    // 選択肢。決定 / キャンセルだけエッジにし、上下のカーソルリピートは残す。key_is_what は触られないので常に戻す
    [HarmonyPatch(typeof(Core), "put_select")]
    internal static class DialogueAdvanceSelectPatch
    {
        private static void Prefix(Core __instance, out int __state)
        {
            __state = DialogueAdvance.WaitEnabled ? DialogueAdvance.SelectMask(__instance) : int.MinValue;
        }

        private static void Postfix(Core __instance, int __state)
        {
            if (__state != int.MinValue) __instance.key_is_what = __state;
        }
    }
}
