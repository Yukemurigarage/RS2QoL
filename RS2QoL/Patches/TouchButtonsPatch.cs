using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// スマホ版のタッチ (マウス) 用のボタンの絵を画面から消す ([Window] HideTouchButtons、既定 Off)。
    /// パッドで遊ぶ時にはどれも押せない絵で、スーパーファミコン版には無かったもの。
    ///
    /// 消す場所 (本体が描いている 4 か所):
    ///  - フィールド: UiField.Draw が描くダッシュ / メニュー / ワールド / 停止の 4 つ (左上・右上・左下)。
    ///    本体のコンフィグにも「表示する/しない」がある (Work.partydata[2080] の bit0、UiField.Initialize で読む) が、
    ///    こちらはコンフィグに関係なく描かない。地名の表示 (DrawPlace) は別の関数なので残る。
    ///  - メニュー: MenuButton.Draw が描く画面下の とじる / もどる / 決定 (SpriteStudioManager.UpdateAfter から毎フレーム)。
    ///  - 戦闘のコマンド窓: SpriteStudio の UI (1_b_2_a_Control) の部品 L_btn / R_btn (ページ送りの矢印) / btn_joho (情報) /
    ///    btn_return (もどる)。本体は SetVisibleBattleUI(true) で全部品を見せるので、その後ろで 4 つだけ隠す。
    ///    D_btn (コマンドが多い時の ▼) は「下にまだある」の印でもあるので残す。
    ///  - 戦闘の相手選び: CBattleUIReturn.Draw の「もどる」(右下)。
    ///  - ワールドマップ (地域マップ): Util.GuiButton の「ぜんたい / ちいき」「とじる」(Update で描く)。
    ///
    /// 絵だけを消し、タッチの当たり判定 (InputManager の touch rect) は触らない。本体の状態 (表示フラグ、Android の戻るキーの行き先、
    /// 隠した時の m_TouchObject の消去など) を変えないため。マウスで元の場所を押せば今までどおり効く。
    /// </summary>
    internal static class TouchButtons
    {
        public static ConfigEntry<bool> Hide;

        public static void Bind(ConfigFile config)
        {
            Hide = config.Bind("Window", "HideTouchButtons", false,
                "スマホ版のタッチ用のボタンの絵 (フィールドのダッシュ / メニュー / ワールド / 停止、メニュー画面下の とじる / もどる / 決定、戦闘の矢印 / 情報 / もどる) を描かない。パッドで遊ぶ時向け。絵を消すだけで、マウスで元の場所を押せば今までどおり効く");
            Plugin.Log.LogInfo("[TouchButtons] HideTouchButtons=" + Hide.Value);
        }

        public static bool On { get { return Hide != null && Hide.Value; } }

        private static bool s_logged;

        internal static void LogOnce(string where)
        {
            if (s_logged) return;
            s_logged = true;
            Plugin.Log.LogInfo("[TouchButtons] applied: " + where);
        }
    }

    /// <summary>フィールドの 4 つのボタン (UiField.Draw)。元処理を止めるのは描画だけの関数だから。</summary>
    [HarmonyPatch(typeof(UiField), nameof(UiField.Draw))]
    internal static class TouchButtonsFieldPatch
    {
        private static bool Prefix()
        {
            if (!TouchButtons.On) return true;
            TouchButtons.LogOnce("UiField.Draw");
            return false;
        }
    }

    /// <summary>メニュー画面下の とじる / もどる / 決定 (MenuButton.Draw)。描画だけの関数。</summary>
    [HarmonyPatch(typeof(MenuButton), nameof(MenuButton.Draw))]
    internal static class TouchButtonsMenuPatch
    {
        private static bool Prefix()
        {
            if (!TouchButtons.On) return true;
            TouchButtons.LogOnce("MenuButton.Draw");
            return false;
        }
    }

    /// <summary>戦闘の相手選びの「もどる」(CBattleUIReturn.Draw)。描画と押下絵の切替だけの関数。</summary>
    [HarmonyPatch(typeof(CBattleUIReturn), nameof(CBattleUIReturn.Draw))]
    internal static class TouchButtonsBattleReturnPatch
    {
        private static bool Prefix()
        {
            if (!TouchButtons.On) return true;
            TouchButtons.LogOnce("CBattleUIReturn.Draw");
            return false;
        }
    }

    /// <summary>
    /// 戦闘のコマンド窓の L_btn / R_btn / btn_joho / btn_return。
    /// SetVisibleBattleUI(true) の後ろで隠し、DrawCommand (毎フレーム) で設定の切替を追う (MOD 設定画面で戦闘中に変えても効くように)。
    /// 戻す時は本体と同じ m_IsVisible (= true のはず) にする。
    /// </summary>
    [HarmonyPatch(typeof(Battle), "SetVisibleBattleUI")]
    internal static class TouchButtonsBattleUiPatch
    {
        private static readonly string[] Parts = { "L_btn", "R_btn", "btn_joho", "btn_return" };

        private static readonly AccessTools.FieldRef<Battle, CSpriteStudioObject> UiBattle =
            AccessTools.FieldRefAccess<Battle, CSpriteStudioObject>("UiBattle");
        internal static readonly AccessTools.FieldRef<Battle, bool> IsUiVisible =
            AccessTools.FieldRefAccess<Battle, bool>("m_IsVisible");

        /// <summary>今の戦闘 UI に適用した値 (null = まだ / UI が替わった)。</summary>
        private static CSpriteStudioObject s_appliedTo;
        private static bool s_applied;

        private static void Postfix(Battle __instance, bool IsVisible)
        {
            s_appliedTo = null;
            if (IsVisible) Apply(__instance, TouchButtons.On);
        }

        internal static void Apply(Battle battle, bool hide)
        {
            CSpriteStudioObject ui = UiBattle(battle);
            if (ui == null || !ui.IsReady()) return;
            if (s_appliedTo == ui && s_applied == hide) return;
            for (int i = 0; i < Parts.Length; i++) ui.SetVisible(Parts[i], !hide);
            s_appliedTo = ui;
            s_applied = hide;
            if (hide) TouchButtons.LogOnce("Battle UI parts");
        }

        /// <summary>毎フレーム隠し直す (本体が出し直す btn_return 対策)。SetVisible は部品の描画フラグを立てるだけで軽い。</summary>
        internal static void ForceHide(Battle battle)
        {
            CSpriteStudioObject ui = UiBattle(battle);
            if (ui == null || !ui.IsReady()) return;
            for (int i = 0; i < Parts.Length; i++) ui.SetVisible(Parts[i], false);
            s_appliedTo = ui;
            s_applied = true;
        }
    }

    /// <summary>
    /// ワールドマップ (地域マップ) の「ぜんたい / ちいき (もどる)」「とじる」。Util.GuiButton (GameObject の MonoBehaviour) が
    /// 自分の Update で描き、押下の絵の切替もそこで行う。当たり判定は InputManager の touch rect で、押された時の処理は
    /// Core.select_chiikimap が button_name で見るので、Update を止めても絵が消えるだけで押せば効く。
    /// </summary>
    [HarmonyPatch(typeof(Util.GuiButton), "Update")]
    internal static class TouchButtonsGuiButtonPatch
    {
        private static bool Prefix()
        {
            if (!TouchButtons.On) return true;
            TouchButtons.LogOnce("Util.GuiButton.Update");
            return false;
        }
    }

    /// <summary>
    /// DrawCommand は 2 人目以降のコマンド入力 (menu_num != 0) の間、毎フレーム btn_return を見える側に戻す
    /// (SSTriangle2 の SetPartsVisible(true))。そのため Prefix の「変わった時だけ」では足りず、Postfix で毎フレーム隠し直す。
    /// </summary>
    [HarmonyPatch(typeof(Battle), "DrawCommand")]
    internal static class TouchButtonsBattleDrawPatch
    {
        private static void Postfix(Battle __instance)
        {
            // UI を本体が隠している間 (m_IsVisible == false) は触らない。Off に戻した時に 4 つだけ出てしまうのを防ぐ
            if (!TouchButtonsBattleUiPatch.IsUiVisible(__instance)) return;
            if (TouchButtons.On) TouchButtonsBattleUiPatch.ForceHide(__instance);
            else TouchButtonsBattleUiPatch.Apply(__instance, false);   // Off に戻した時に 1 回だけ出し直す
        }
    }
}
