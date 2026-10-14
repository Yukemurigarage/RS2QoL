using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// メニューで選んでいる文字の点滅 (明滅) の速さを、倍速中も等倍と同じに保つ ([Menu] CursorBlinkRealTime、既定 true)。
    /// 戦闘のコマンド窓・フィールドのメニュー・店など、SpriteStudio 製の UI 全部と、会話の選択肢 (はい / いいえ など) に共通。
    ///
    /// 本体は SpriteStudioCursor.Update (Main.Disp.run の中の SpriteStudioManager.UpdateAfter から、毎論理フレーム) で
    /// 選択中の文字の色 m_StringColor を m_StringColorAdd (±0.1) ずつ動かし、0 と 1 で折り返している (20 フレーム周期)。
    /// 倍速中は論理フレームが倍率回回るので明滅も倍率分速くなり、x3〜x4 ではちらつきになる。
    /// ここでは Update の Prefix で「このフレームは進めない」と決めた時だけ m_StringColorAdd を 0 にして (0 なら色も折り返しも
    /// 動かない)、Postfix で戻す。進める回数は 100 / 倍率% を累積で決める (x1.5 なら 3 フレームに 2 回)。
    /// </summary>
    internal static class CursorBlink
    {
        public static ConfigEntry<bool> RealTime;

        public static void Bind(ConfigFile config)
        {
            RealTime = config.Bind("Menu", "CursorBlinkRealTime", true,
                "メニューで選んでいる文字の点滅の速さを、倍速中も等倍と同じに保つ (戦闘のコマンド窓・フィールドのメニュー・店・会話の選択肢など共通)。false なら倍速中は倍率分速く点滅する (本体どおり)");
        }

        /// <summary>
        /// 「このフレームは進めない」かどうかを 100 / 倍率% の累積で決める (x1.5 なら 3 フレームに 2 回進める)。
        /// 等倍以下なら常に進める。点滅の種類ごとに累積値を別に持つ。
        /// </summary>
        internal static bool HoldThisFrame(ref int acc)
        {
            if (RealTime == null || !RealTime.Value) return false;
            int pct = GameSpeed.EffectivePercent();
            if (pct <= 100) { acc = 0; return false; }
            acc += 100;
            if (acc >= pct) { acc -= pct; return false; }
            return true;
        }
    }

    /// <summary>
    /// 会話の選択肢 (はい / いいえ など) の赤い点滅。Core.put_select が選択肢を開いている間毎論理フレーム呼ばれ、
    /// 選んでいる行を cursor_cnt を透明度にして赤で描き (Window.drawString2 で byte にして 0〜255 ののこぎり波)、
    /// 最後に cursor_cnt += 32 する (8 論理フレーム周期)。倍速中はこれが倍率分速くなるので、進めないフレームでは
    /// Postfix で 32 を戻す。十字キーや決定で cursor_cnt = 256 にされた時も 256 + 32 − 32 = 256 で変わらない。
    /// (メッセージ送りの ▼ は SpriteStudio のアニメで実時間なので対象外)
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.put_select))]
    internal static class SelectBlinkPatch
    {
        private const int STEP = 32;
        private static int s_acc;
        private static bool s_held;

        private static void Prefix()
        {
            s_held = CursorBlink.HoldThisFrame(ref s_acc);
        }

        private static void Postfix(Core __instance)
        {
            if (s_held)
            {
                s_held = false;
                __instance.cursor_cnt -= STEP;
            }
            Diag(__instance.cursor_cnt);
        }

        // 診断: 点滅 1 周期 (byte に切った cursor_cnt が折り返すまで) の実時間を出す。等倍なら 8 論理フレーム ≒ 267ms
        private static int s_prevByte = -1;
        private static float s_wrapAt;
        private static void Diag(int cnt)
        {
            if (Plugin.DiagEnabled == null || !Plugin.DiagEnabled.Value) { s_prevByte = -1; return; }
            int b = cnt & 0xFF;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (s_prevByte >= 0 && b < s_prevByte)
            {
                if (s_wrapAt > 0f) Plugin.Log.LogInfo("[SelectBlink] period=" + (int)((now - s_wrapAt) * 1000f) + "ms pct=" + GameSpeed.EffectivePercent());
                s_wrapAt = now;
            }
            s_prevByte = b;
        }
    }

    [HarmonyPatch(typeof(SpriteStudioCursor), nameof(SpriteStudioCursor.Update))]
    internal static class CursorBlinkPatch
    {
        private static readonly System.Reflection.FieldInfo s_add = AccessTools.Field(typeof(SpriteStudioCursor), "m_StringColorAdd");
        private static int s_acc;
        private static bool s_held;
        private static float s_saved;

        private static void Prefix()
        {
            s_held = false;
            if (!CursorBlink.HoldThisFrame(ref s_acc)) return;   // このフレームは進める
            if (s_add == null) return;
            s_saved = (float)s_add.GetValue(null);
            s_add.SetValue(null, 0f);
            s_held = true;
        }

        private static void Postfix()
        {
            if (!s_held) return;
            s_held = false;
            s_add.SetValue(null, s_saved);
        }
    }
}
