using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 開発が仕上がった時の知らせ ([Avalon] DevNotice、既定 On)。
    /// 本体は開発所の開発 (武器 / 防具) が仕上がっても何も言わず、情報室で情報官に聞いた時だけ「仕上がった」と話す。
    /// MOD は仕上がった戦闘の直後に、引き出しの知らせと同じ小窓 (絵は弔いの鍛冶と同じ職人 2 人) で「武器が仕上がりました」と出す。
    ///
    /// 本体の事実 (decompiled Core.appnew_ivent case 32 + script ops、2026-10-24 調査):
    ///  - 開発を頼む (msg 2901 武器 / 2902 防具) と new_flag 56 = 5 (残りの戦闘数)、flag115 |= 4 (武器) / 8 (防具)。
    ///  - 戦闘後のスクリプト (msg 2061) の APPNEW 32 (= Core.appnew_ivent、ivent_no[1] == 32) が new_flag 56 を 1 減らす (0 で止まる)。
    ///    同じ case 32 が施設 (new_flag 48 / 49 / 50) の完成と戦闘回数 (new_flag 7..10) も進める。
    ///  - 開発所 (msg 2068 / 2069) は flag115 が 0..3 なら「開発なし」、new_flag 56 == 0 なら msg 2903 (「が完成しました！」、品を渡す、new_flag 51 = 2)、
    ///    それ以外は「開発中です」。つまり仕上がりの印は new_flag 56 == 0 で、new_flag 51 は関わらない。
    ///  - new_flag 51 は情報官の知らせ用: case 32 で 56 が 0 になった時、51 == 0 なら 51 = 1。情報官 (msg 3206) は 51 == 1 の時だけ「仕上がった」と話して 51 = 2。
    ///    51 を 0 に戻す所は script にも C# にも無いので、2 回目以降の開発は情報官が知らせない (本体の仕様)。最初の版はこの 0 → 1 を見ていて、
    ///    2 回目の開発で何も出なかった (ユーザー 2026-10-24)。
    /// MOD は appnew_ivent の Prefix で new_flag 56 を控え、Postfix で 開発中かつ 1 以上 → 0 になった時だけ知らせる (ソフトリセット中は出さない)。
    /// 読み書きは本体の get_flag_atai / get_new_flag_atai だけで、フラグは変えない。
    /// </summary>
    internal static class DevNotice
    {
        public static ConfigEntry<bool> Enabled;

        public const string NAME = "開発所";
        private const int FLAG_DONE = 51, FLAG_LEFT = 56, FLAG_DEV = 115;
        private const float SECONDS = 6f;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Avalon", "DevNotice", true,
                "開発所の開発 (武器 / 防具) が仕上がった戦闘の直後に「武器が仕上がりました」の小窓を出す。"
                + "本体は仕上がっても何も言わず、情報室で情報官に聞いた時だけ話す。false なら本体どおり");
        }

        public static bool On { get { return Enabled != null && Enabled.Value; } }

        /// <summary>appnew_ivent の前に控えた new_flag 56 (残りの戦闘数。−1 = APPNEW 32 でないので見ない)。</summary>
        public static int Before(Core core)
        {
            if (core == null || core.ivent_no == null || core.ivent_no[1] != 32) return -1;
            return core.get_new_flag_atai(FLAG_LEFT);
        }

        /// <summary>appnew_ivent の後。開発中 (flag115 bit 4 / 8) で残りが 1 以上 → 0 になった時だけ知らせる。</summary>
        public static void After(Core core, int beforeLeft)
        {
            if (beforeLeft < 0 || core == null) return;
            int left = core.get_new_flag_atai(FLAG_LEFT);
            int dev = core.get_flag_atai(FLAG_DEV);
            int done = core.get_new_flag_atai(FLAG_DONE);
            if (Plugin.DiagOn) Plugin.Log.LogInfo("[DevNotice] appnew 32: left " + beforeLeft + " -> " + left + " flag115 " + dev + " new_flag51 " + done);
            if (beforeLeft <= 0 || left != 0 || (dev & 12) == 0) return;
            string kind = (dev & 4) != 0 ? "武器" : "防具";
            Plugin.Log.LogInfo("[DevNotice] applied: " + kind + " done (flag115 " + dev + " left " + beforeLeft + " -> 0 new_flag51 " + done + " enabled " + On + ")");
            if (!On || SoftReset.Active) return;
            if (MourningScene.SmithsReady())
                Wallet.ShowNotice(kind + "が仕上がりました", NAME, DrawSmiths, MourningScene.SmithPairDots, SECONDS);
            else
                Wallet.ShowNotice(kind + "が仕上がりました", NAME, false, SECONDS);
        }

        private static float s_shown;

        private static void DrawSmiths(Rect r, int dot)
        {
            if (s_shown == 0f || Time.realtimeSinceStartup - s_shown > SECONDS + 1f) s_shown = Time.realtimeSinceStartup;
            MourningScene.DrawSmithPair(r, dot, s_shown);
        }
    }

    /// <summary>戦闘後スクリプトの APPNEW (ivent_no[1] == 32 で開発の残り戦闘数を減らす)。前後で new_flag 51 を比べる。</summary>
    [HarmonyPatch(typeof(Core), nameof(Core.appnew_ivent))]
    internal static class DevNotice_appnew_ivent_Patch
    {
        private static void Prefix(Core __instance, out int __state) { __state = DevNotice.Before(__instance); }

        private static void Postfix(Core __instance, int __state)
        {
            try { DevNotice.After(__instance, __state); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[DevNotice] failed: " + e.Message); }
        }
    }
}
