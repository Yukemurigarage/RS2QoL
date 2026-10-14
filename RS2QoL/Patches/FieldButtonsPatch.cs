using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// フィールド専用のボタン機能 (PadFunction.Dash / CancelOnly)。
    ///
    /// ゲームはフィールドの操作を Core.move_human_sub_r で joy_data[0] (押下中) / joy_data[1] (押下エッジ) から読む。
    /// ダッシュは joy_data[0] の 0x400000 (= キャンセルと同じビット) で human_key |= 128。本体ではキャンセルとダッシュが
    /// 1 つのビットなので、ここで分ける。
    ///  - Dash (ダッシュ / とじる): ButtonRemapPatch では 0x200000 (メニュー / とじる。本体の Y) を出す。フィールドではその
    ///    ビットを外してメニューを開かせず、代わりに 0x400000 を立ててダッシュにする。
    ///  - CancelOnly (キャンセルのみ): ButtonRemapPatch では 0x400000 を出す。フィールドでは、それだけが押されている時に
    ///    0x400000 を外してダッシュしないようにする。
    ///  - MapMove (マップ移動 / 情報・切替) は本体の X と同じ 0x20 を出すだけで、ここでは触らない。フィールドでは 0x20 の
    ///    押下エッジ (SFC 版の Sys.KEY_MAPIDOU) が exit_ivent を起動する (建物の中なら外へ、町の外ならワールドマップ、
    ///    ダンジョンは exit_ivent が無いので何もしない)。ワールドマップでは地域画面から大陸一覧へ戻る。
    ///
    /// Prefix で joy_data を書き換え、Postfix で元に戻す。move_human_sub_r の中でしか効かないので、
    /// 同じ論理フレームの後続 (イベント処理の key_is_what = joy_data[0] など) やメニュー・戦闘には漏れない。
    ///
    /// joy_data は論理フレーム 1 つ分遅れている: Main.run が Sys.syncKey で Sys.key を更新した後、フィールドの 1 フレーム
    /// (wait_nmi_sa) は move_human_sub_r → nmi_routine → get_joy (joy_data = Sys.key) の順なので、move_human_sub_r が見る
    /// joy_data は前の論理フレームの入力。一方ここの IsFunctionDown は今の入力を見る。そのためダッシュを離した直後の
    /// 1 フレームは「ダッシュは離している (dash = false) が joy_data[0] にはまだ 0x200000 が残っている」状態になり、
    /// 外し損ねた 0x200000 が本体のメニュー起動 (joy_data[0] & 0x200000 && menukey_off == 0) に通ってメニューが開いていた
    /// (十字キーを押していないと他のビットが無く、起動条件がそのまま成立する)。前の論理フレームでダッシュが
    /// 押されていた場合も外す (s_prevDash) ことで、離した瞬間の取りこぼしを無くす。
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.move_human_sub_r))]
    internal static class FieldButtonsPatch
    {
        private const int BIT_CANCEL_DASH = 0x400000;
        private const int BIT_MENU = 0x200000;

        private static bool s_changed;
        private static int s_saved0, s_saved1;
        private static bool s_prevDash; // 前回の Prefix (= 前の論理フレーム) でダッシュが押されていたか

        private static void Prefix(Core __instance)
        {
            s_changed = false;
            if (QuitGame.BlockInput) return;   // 終了ダイアログ中はパッドをゲームに渡さない (QuitDialogBlockInputPatch)
            if (PadMap.Entries == null || __instance.joy_data == null) return;
            int[] joy = __instance.joy_data;

            bool dash = PadMap.IsFunctionDown(PadFunction.Dash);
            bool prevDash = s_prevDash;
            s_prevDash = dash;
            // 「キャンセルのみ」だけが押されている時はダッシュのビットを外す。
            // キャンセル + ダッシュ (本体既定の B) が押されていればそのまま (IsFunctionDown はキーボードの割り当て (KeyMap) も見る)
            bool cancelOnly = !dash && PadMap.IsFunctionDown(PadFunction.CancelOnly)
                && !PadMap.IsFunctionDown(PadFunction.Cancel);
            // ダッシュのボタンは 0x200000 (メニュー / とじる) を出しているので、フィールドでメニューを開かないよう外す。
            // メニューを割り当てたボタン / キーが押されていればそのまま
            bool stripMenu = (dash || prevDash) && ((joy[0] | joy[1]) & BIT_MENU) != 0
                && !PadMap.IsFunctionDown(PadFunction.Menu);

            if (!dash && !cancelOnly && !stripMenu) return;
            s_saved0 = joy[0];
            s_saved1 = joy[1];
            s_changed = true;
            if (dash) joy[0] |= BIT_CANCEL_DASH;
            else if (cancelOnly) joy[0] &= ~BIT_CANCEL_DASH;
            if (stripMenu) { joy[0] &= ~BIT_MENU; joy[1] &= ~BIT_MENU; }
        }

        private static void Postfix(Core __instance)
        {
            if (!s_changed) return;
            s_changed = false;
            __instance.joy_data[0] = s_saved0;
            __instance.joy_data[1] = s_saved1;
        }
    }
}
