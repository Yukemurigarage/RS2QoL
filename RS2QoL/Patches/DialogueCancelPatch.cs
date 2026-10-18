using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 会話の選択肢でキャンセルボタンを「いいえ」側 (最後の行) の決定にする ([Dialogue] CancelPicksDecline、既定 On)。
    ///
    /// 本体は script の選択肢 (select_sa / select_b_bad → put_select) を全部 select_b_trig_ok = 1 で呼ぶので、ゲーム中の会話の選択肢
    /// (169 か所) はどれもキャンセルを無視し、「いいえ」「入れない」「そんなに出せるか！」をカーソルで選んで決定するしかない。
    /// script.bin の全選択肢を数えると、2 択 (157 か所) の 2 行目はほぼ全部が否定側 (入れない 38、なんでもない 16、何でもない 14、
    /// はい / いいえ 5、乗らない、休まない、やめとく、ダメ、そんなに出せるか！ …)、3 択以上も最後の行が 乗らない / 何でもない / 行かない。
    /// ただし「ちょっと待て / 行くぞ！」「金置いて逃げる / 教訓をあたえる」のような中立の 2 択も数か所あるので、
    /// **最後の行の文に否定の言葉が含まれる時だけ** 効かせる。含まなければ本体どおり無反応 (押し間違いで話が進む事故を起こさない)。
    /// 仲間を誘う 2 択に 3 行目「ステータスを見る」を足している間は RecruitInfo が自分で 2 行目「入れない」に読み替える (こちらは何もしない)。
    ///
    /// 仕組み: Core.put_select の Prefix (DialogueAdvance の決定エッジ化の後に見るため Priority.Low) で、キャンセルのビットだけが立っていたら
    /// カーソルを最後の行に置き、key_is_what のキャンセルのビットを決定のビットに差し替えて本体に渡す (本体はそのまま決定として処理する)。
    /// 行の文は select_work[i] (Window.fonttbl の添字、0 終端) を RecruitInfo.Decode で文字列に戻す。
    /// </summary>
    internal static class DialogueCancel
    {
        public static ConfigEntry<bool> Enabled;

        private const int CANCEL_BIT = 0x400000;        // put_select のキャンセル判定と同じ
        private const int DECIDE_BITS = 0x100001;       // put_select の決定判定と同じ (OK ボタン + タッチの決定)
        private const int OK_BIT = 0x100000;

        // 最後の行が「断る / 何もしない」側だと判断する言葉 (script.bin の全選択肢から拾った。部分一致)
        private static readonly string[] DECLINE_WORDS =
        {
            "ない", "いいえ", "やめ", "ダメ", "帰る", "帰ろう", "待て", "いや", "出せるか", "できるか", "頼むか", "頼まん", "払わん",
            "遠慮", "あきらめ", "放って", "かんベん", "かんべん", "勝手に", "別の方法", "引き上げ", "人違い", "なし",
        };

        private static bool s_failed;
        private static int s_loggedMsg = -1;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Dialogue", "CancelPicksDecline", true,
                "会話の選択肢でキャンセルボタンを「いいえ」側 (最後の行) の決定にする。本体は会話の選択肢でキャンセルを受け付けず (select_b_trig_ok)、「いいえ」「入れない」「そんなに出せるか！」をカーソルで選ぶしかない。最後の行の文に否定の言葉 (〜ない、やめ、ダメ、いいえ、帰る など) が無い選択肢では何もしない。");
            Plugin.Log.LogInfo("[Dialogue] CancelPicksDecline=" + Enabled.Value);
        }

        /// <summary>Core.put_select の前。true = 本体の処理を続ける (常に true。key_is_what と select_now を直すだけ)。</summary>
        internal static void BeforePutSelect(Core c)
        {
            if (s_failed || Enabled == null || !Enabled.Value) return;
            if (c.select_b_trig_ok == 0) return;                       // メニューの選択肢はもともとキャンセルが効く
            if (RecruitInfo.RowActive) return;                         // 仲間の 3 択は RecruitInfo が扱う
            if ((c.key_is_what & CANCEL_BIT) == 0 || (c.key_is_what & DECIDE_BITS) != 0) return;
            try
            {
                int last = c.select_count - 1;
                if (last < 1 || last >= c.select_work.Length) return;
                string text = RecruitInfo.Decode(c.select_work[last]);
                if (!IsDecline(text))
                {
                    if (s_loggedMsg != c.mess_no) { s_loggedMsg = c.mess_no; Plugin.Log.LogInfo("[Dialogue] msg " + c.mess_no + ": cancel ignored, last row \"" + text + "\" is not a decline"); }
                    return;
                }
                c.select_now = last;
                c.key_is_what = (c.key_is_what & ~CANCEL_BIT) | OK_BIT;
                Plugin.Log.LogInfo("[Dialogue] msg " + c.mess_no + ": cancel -> row " + last + " \"" + text + "\"");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[Dialogue] put_select failed, disabled until restart: " + e);
                s_failed = true;
            }
        }

        private static bool IsDecline(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < DECLINE_WORDS.Length; i++) if (text.IndexOf(DECLINE_WORDS[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Core), "put_select")]
    [HarmonyPriority(Priority.Low)]
    internal static class DialogueCancelPutSelectPatch
    {
        private static void Prefix(Core __instance) { DialogueCancel.BeforePutSelect(__instance); }
    }
}
