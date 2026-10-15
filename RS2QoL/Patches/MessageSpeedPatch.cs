using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    internal enum MessageSpeedMode { Default, x2, x3, Instant }

    /// <summary>
    /// フィールド / イベントの会話ウィンドウの文字送りの速さ。倍速 (GameSpeed) の ON/OFF や倍率に関わらず実時間で同じ速さにする。
    ///
    /// 本体は Core.sync のフィールド処理から毎論理フレーム Core.message_lop を呼び、その中の do-while で Core.message_lop2 を
    /// mess_loop が 1 の間繰り返す。message_lop2 は文字 1 つ (コード 80 以上、または 32〜35 の漢字) を出すたびに
    /// mess_loop = (mess_cnt &amp; 2) != 0、mess_cnt++ とするので、1 フレームに出る文字数は 1, 1, 3, 1, 3, … (平均 2 文字/フレーム = 60 文字/秒)。
    /// 改行・名前の差し込み・フラグ操作などの制御コードの多くは mess_loop = 1 で同じフレーム内に続けて処理され、
    /// ▼ 待ち (pause_sa)・ウェイト (wait_30)・窓の開閉・スクロール・文末・イベント (ivent_sw) はそのフレームで止まる。
    ///
    /// ここでは Default 以外の時だけ message_lop を Prefix で丸ごと置き換え (return false)、「このフレームで出してよい文字数」を
    /// 予算として持って message_lop2 を回す。文字が出たか (mess_cnt が進んだか) で予算を減らし、文字でなく mess_loop = 1 の制御コードは
    /// 本体どおり続け、それ以外 (待ち・スクロール・文末・イベント) で止める。元の do-while を止める必要があるので Prefix。
    /// 予算は 1 フレームあたり「2 文字 × 倍率 ÷ 倍速の倍率」で、倍速 x2 中は 1 論理フレームあたり半分にして実時間の速さを保つ
    /// (小数は次の論理フレームに持ち越す)。待ちで止まったフレームの余りは捨てる (再開した瞬間にまとめて出ないように)。
    /// 即時は予算なし。戦闘中のメッセージは別の仕組み (Battle) なので触らない。
    /// </summary>
    internal static class MessageSpeed
    {
        public static ConfigEntry<MessageSpeedMode> Mode;

        /// <summary>本体の平均の文字数 (1 論理フレームあたり)。</summary>
        private const float BASE_CHARS_PER_FRAME = 2f;
        /// <summary>即時でも暴走しないための上限 (1 フレームに回す message_lop2 の回数)。</summary>
        private const int MAX_STEPS = 4000;

        private static float s_budget;
        private static bool s_logged;

        public static void Bind(ConfigFile config)
        {
            Mode = config.Bind("Field", "MessageSpeed", MessageSpeedMode.Default,
                "会話ウィンドウの文字送りの速さ。Default=本体のまま (倍速中は倍速に連動) / x2, x3=本体の 2 倍, 3 倍 / Instant=即時に全文。" +
                "Default 以外は倍速の ON/OFF や倍率に関わらず実時間で同じ速さ (▼ 待ちやウェイトは本体のまま)");
        }

        public static bool Enabled { get { return Mode != null && Mode.Value != MessageSpeedMode.Default; } }

        /// <summary>倍率 (%)。Instant は 0 を返す。</summary>
        public static int Percent(MessageSpeedMode m)
        {
            switch (m)
            {
                case MessageSpeedMode.x2: return 200;
                case MessageSpeedMode.x3: return 300;
                case MessageSpeedMode.Instant: return 0;
                default: return 100;
            }
        }

        public static string Label(MessageSpeedMode m)
        {
            switch (m)
            {
                case MessageSpeedMode.x2: return "x2";
                case MessageSpeedMode.x3: return "x3";
                case MessageSpeedMode.Instant: return "即時";
                default: return "本体のまま";
            }
        }

        public static void Cycle(int delta)
        {
            var all = (MessageSpeedMode[])System.Enum.GetValues(typeof(MessageSpeedMode));
            int cur = System.Array.IndexOf(all, Mode.Value);
            Mode.Value = all[(cur + delta + all.Length) % all.Length];
        }

        /// <summary>message_lop の置き換え本体。戻り値 true なら本体の処理を飛ばした。</summary>
        public static bool Run(Core c)
        {
            if (!Enabled || c.mess_flag == 0) return false;
            MessageSpeedMode mode = Mode.Value;
            bool instant = mode == MessageSpeedMode.Instant;
            if (!instant)
            {
                // 1 論理フレームの予算。倍速 x2 なら論理フレームが 2 倍回るので半分にして実時間で同じにする
                float perFrame = BASE_CHARS_PER_FRAME * Percent(mode) / 100f;
                int speed = GameSpeed.EffectivePercent();
                if (speed > 100) perFrame = perFrame * 100f / speed;
                s_budget += perFrame;
            }
            if (!s_logged)
            {
                s_logged = true;
                Plugin.Log.LogInfo("[MessageSpeed] applied: " + Label(mode) + (instant ? "" : " (" + BASE_CHARS_PER_FRAME * Percent(mode) / 100f + " 文字/フレーム)"));
            }

            bool stoppedByWait = false;
            for (int step = 0; step < MAX_STEPS; step++)
            {
                if (!instant && s_budget < 1f) break;
                c.mess_loop = 0;
                int cntBefore = c.mess_cnt;
                c.message_lop2();
                if (c.ivent_sw != 0) { c.mess_loop = 0; stoppedByWait = true; break; }
                if (c.mess_cnt != cntBefore)
                {
                    // 文字を 1 つ出した
                    if (!instant) s_budget -= 1f;
                    continue;
                }
                if (c.mess_loop == 1) continue;   // 同じフレーム内に続く制御コード (本体どおり)
                stoppedByWait = true;             // ▼ 待ち・ウェイト・窓の開閉・スクロール・文末
                break;
            }
            if (stoppedByWait) s_budget = 0f;
            if (c.ivent_sw == 0) c.mess_wait_minus = 0;
            return true;
        }
    }

    // 元の do-while を止めて予算付きのループに置き換えるため Prefix (Default の時は本体のまま)
    [HarmonyPatch(typeof(Core), "message_lop")]
    internal static class MessageSpeedPatch
    {
        private static bool Prefix(Core __instance)
        {
            return !MessageSpeed.Run(__instance);
        }
    }
}
