using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 戦闘のコマンド入力で L / R をスーパーファミコン版と同じ「退却 / 防御の呼び出し」にする ([Battle] SfcLR、既定 true)。
    ///
    /// 本体 (Steam 版) の L / R は十字キー左右と同じ「前のページ / 次のページ」で、L+R 同時押しで防御/退却のページ (cur_page 6) に飛ぶ。
    /// スーパーファミコン版は L を押すとカーソルの行に「退却」、R を押すと「防御」が差し替わって出て、もう一度同じボタンで元に戻る。
    /// ここでは
    ///   - Battle.scan_00 (コマンド入力の 1 フレーム分の処理) の Prefix で joy1[1] (押した瞬間) の L / R を見て差し替え状態をトグルし、
    ///     本体が見る joy1 からは L / R のビットを外す (ページ送りにしない。Postfix で戻す)。
    ///   - 差し替え中に決定が押されたら、本体の防御/退却ページ (cur_page 6、行 0 = 防御、行 1 = 退却) の決定処理をそのまま使うため、
    ///     Prefix で cur_page / cur_line を一時的に 6 / 0 か 1 にして本体を通し、Postfix で元のページと行に戻す。
    ///     退却できない戦闘では本体どおり「逃げられない」のメッセージ (戻り値 6) になる。
    ///   - 差し替え中に十字キー (上下左右) が押されたら差し替えを解除して本体に通す。コマンドが決まった (戻り値が 0 以外) 時も解除。
    ///   - 表示は Battle.DrawCommand (SpriteStudio の UI にコマンド名を流し込む) の Postfix で、カーソル行の文字列を
    ///     本体と同じメッセージ (シート 13 の 258 = 防御、259 = 退却) に差し替え、その行の使用回数/消費の列を消す。
    ///     下の説明文は DrawCommand の中で GS.DrawString に直接描かれるので、DrawCommand の間だけ GS.DrawString の Prefix で
    ///     説明文 (シート 14 の 258 / 259) に差し替える。
    /// 戦闘以外 (メニュー画面のページ送り) の L / R には触らない。
    /// </summary>
    internal static class BattleLR
    {
        public static ConfigEntry<bool> Enabled;

        /// <summary>差し替え状態。0 = なし、1 = 退却 (L)、2 = 防御 (R)。</summary>
        public static int Overlay;

        /// <summary>DrawCommand の実行中 (GS.DrawString の差し替えを効かせる範囲)。</summary>
        internal static bool InDrawCommand;

        public const int BIT_L = 0x10;
        public const int BIT_R = 0x40;
        public const int BIT_OK = 0x100001;
        public const int DIR_BITS = 0xF0000;

        public const int PAGE_GUARD = 6;
        public const int LINE_GUARD = 0;
        public const int LINE_ESCAPE = 1;
        public const int MSG_SHEET_NAME = 13;
        public const int MSG_SHEET_HELP = 14;
        public const int MSG_GUARD = 258;
        public const int MSG_ESCAPE = 259;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Battle", "SfcLR", true,
                "戦闘のコマンド入力で L を押すとカーソルの行に「退却」、R を押すと「防御」を出す (スーパーファミコン版と同じ。もう一度押すと戻る)。" +
                "false なら本体どおり L / R は前/次のページ (十字キー左右と同じ)");
        }

        public static bool On { get { return Enabled != null && Enabled.Value; } }

        public static int MsgId { get { return Overlay == 2 ? MSG_GUARD : MSG_ESCAPE; } }

        internal static readonly AccessTools.FieldRef<Battle, CSpriteStudioObject> UiBattle = AccessTools.FieldRefAccess<Battle, CSpriteStudioObject>("UiBattle");
        internal static readonly AccessTools.FieldRef<Battle, bool> IsVisible = AccessTools.FieldRefAccess<Battle, bool>("m_IsVisible");
        internal static readonly AccessTools.FieldRef<Battle, int> HelpScroll = AccessTools.FieldRefAccess<Battle, int>("m_HelpScroll");
        internal static readonly AccessTools.FieldRef<Battle, int> HelpScrollWait = AccessTools.FieldRefAccess<Battle, int>("m_HelpScrollWait");
        internal static readonly AccessTools.FieldRef<Battle, string[]> NameCommand = AccessTools.FieldRefAccess<Battle, string[]>("ObjectNameCommand");
        internal static readonly AccessTools.FieldRef<Battle, string[]> NameUse = AccessTools.FieldRefAccess<Battle, string[]>("ObjectNameUse");
        internal static readonly AccessTools.FieldRef<Battle, string[]> NamePoint = AccessTools.FieldRefAccess<Battle, string[]>("ObjectNamePoint");
        private static readonly System.Reflection.FieldInfo s_menuScroll = AccessTools.Field(typeof(Battle), "m_MenuScroll");

        internal static int MenuScroll { get { return (int)s_menuScroll.GetValue(null); } }

        // ---- 矢印ボタンの「L」「R」の文字を消す ----
        // Steam 版は起動時 (Util.file_regist_on_boot、etc/regist_files_steam.csv の「*」行) に menu/btn_steam_en/L_btn.png 等
        // (金色の矢印に白い L / R の文字入り) をファイル名「L_btn」「R_btn」(とその _push) で登録し、Util.LoadTexture はこの登録を
        // 優先して返すので、SpriteStudio の UI が読む menu/btn/L_btn.png (文字なしの矢印) は全てこの文字入りに置き換わっている。
        // 戦闘では L / R が退却 / 防御になるので、戦闘 UI のオブジェクトだけ、アーカイブから直接読んだ文字なしの矢印に差し替える
        // (Util.falloc はアーカイブそのものを読む)。メニュー画面の L / R はページ送りのままなので触らない。
        // 差し替えはテクスチャスロットの SetTexture と、押下時に本体が SetTexture(名前, パス) で引く m_LoadTextureList の両方。
        private static readonly AccessTools.FieldRef<CSpriteStudioObject, System.Collections.Generic.List<CSpriteTexture>> LoadTextureList =
            AccessTools.FieldRefAccess<CSpriteStudioObject, System.Collections.Generic.List<CSpriteTexture>>("m_LoadTextureList");
        private static readonly string[] ArrowFiles = { "menu/btn/L_btn.png", "menu/btn/L_btn_push.png", "menu/btn/R_btn.png", "menu/btn/R_btn_push.png" };
        private static Texture2D[] s_arrows;
        private static bool s_arrowsFailed;
        private static CSpriteStudioObject s_arrowAppliedTo;
        private static bool s_arrowApplied;
        private static readonly Texture2D[] s_origArrows = new Texture2D[4];

        private static bool LoadArrows()
        {
            // Unity の == null は破棄済みも true。戦闘終了時の Util.GC (Resources.UnloadUnusedAssets) で消されていたら読み直す
            if (s_arrows != null && s_arrows[0] != null) return true;
            if (s_arrows != null) Plugin.Log.LogWarning("[BattleLR] arrow textures were unloaded, reloading");
            if (s_arrowsFailed) return false;
            Texture2D[] t = new Texture2D[ArrowFiles.Length];
            for (int i = 0; i < ArrowFiles.Length; i++)
            {
                byte[] bytes = null;
                try { bytes = Util.falloc(ArrowFiles[i]); } catch { }
                if (bytes == null)
                {
                    Plugin.Log.LogWarning("[BattleLR] arrow texture not found: " + ArrowFiles[i]);
                    s_arrowsFailed = true;
                    return false;
                }
                t[i] = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                t[i].name = System.IO.Path.GetFileNameWithoutExtension(ArrowFiles[i]) + "_noletter";
                // 戦闘終了時の Util.GC (Resources.UnloadUnusedAssets) で破棄されないようにする (管理側の参照だけでは守られない)
                t[i].hideFlags = HideFlags.HideAndDontSave;
                t[i].LoadImage(bytes);
            }
            s_arrows = t;
            return true;
        }

        /// <summary>戦闘 UI の L / R ボタンを文字なしの矢印にする (On の時)。Off に戻した時は元の文字入りに戻す。</summary>
        internal static void ApplyArrows(CSpriteStudioObject ui)
        {
            if (ui == null || !ui.IsReady()) return;
            bool want = On;
            if (s_arrowAppliedTo == ui && s_arrowApplied == want) return;
            if (s_arrowAppliedTo != ui) { s_arrowAppliedTo = ui; s_arrowApplied = false; }
            if (want == s_arrowApplied) return;
            if (want && !LoadArrows()) { Plugin.Log.LogWarning("[BattleLR] arrows: load failed"); return; }
            System.Collections.Generic.List<CSpriteTexture> list = LoadTextureList(ui);
            if (list == null) { Plugin.Log.LogWarning("[BattleLR] arrows: m_LoadTextureList null"); return; }
            int matched = 0;
            for (int i = 0; i < ArrowFiles.Length; i++)
            {
                for (int j = 0; j < list.Count; j++)
                {
                    if (list[j].m_FileName != ArrowFiles[i]) continue;
                    matched++;
                    if (want) { s_origArrows[i] = list[j].m_Texture2D; list[j].m_Texture2D = s_arrows[i]; }
                    else if (s_origArrows[i] != null) { list[j].m_Texture2D = s_origArrows[i]; }
                }
            }
            // 通常状態のスロットを差し替える (押下中の _push は本体が離した時に m_LoadTextureList から引き直す)
            Texture2D l = want ? s_arrows[0] : s_origArrows[0];
            Texture2D r = want ? s_arrows[2] : s_origArrows[2];
            if (l != null) ui.SetTexture("L_btn", l);
            if (r != null) ui.SetTexture("R_btn", r);
            s_arrowApplied = want;
            Plugin.Log.LogDebug("[BattleLR] arrows " + (want ? "noletter" : "restore") + ": matched=" + matched + " new=" + (l != null ? l.width + "x" + l.height : "null"));
        }

        internal static void ResetArrows() { s_arrowAppliedTo = null; s_arrowApplied = false; }

        // MsgDataManager は internal なので反射で呼ぶ (GetMenuMsg(int sheetTypeId, int msg_id))
        private static readonly System.Reflection.MethodInfo s_getMenuMsg =
            AccessTools.Method(AccessTools.TypeByName("MsgDataManager"), "GetMenuMsg", new[] { typeof(int), typeof(int) });

        internal static string GetMenuMsg(int sheet, int id)
        {
            if (s_getMenuMsg == null) return string.Empty;
            return (string)s_getMenuMsg.Invoke(null, new object[] { sheet, id });
        }
    }

    [HarmonyPatch(typeof(Battle), "scan_00")]
    internal static class BattleLRScanPatch
    {
        internal struct State
        {
            public int J0, J1, J2;
            public int Page, Line;
            public bool Swapped;
        }

        private static void Prefix(Battle __instance, out State __state)
        {
            __state = default(State);
            if (!BattleLR.On)
            {
                BattleLR.Overlay = 0;
                return;
            }
            int[] joy = __instance.joy1;
            __state.J0 = joy[0];
            __state.J1 = joy[1];
            __state.J2 = joy[2];

            // 押した瞬間 (joy1[1]) だけで切り替える。joy1[2] は長押しのリピートでも立つので使わない
            int edge = joy[1];
            if ((edge & BattleLR.BIT_L) != 0)
            {
                BattleLR.Overlay = (BattleLR.Overlay == 1) ? 0 : 1;
                Sound.setSe(212, 10);
            }
            else if ((edge & BattleLR.BIT_R) != 0)
            {
                BattleLR.Overlay = (BattleLR.Overlay == 2) ? 0 : 2;
                Sound.setSe(212, 10);
            }

            // 本体には L / R を見せない (前/次のページと L+R の防御/退却ページ飛びをやめる)
            int mask = ~(BattleLR.BIT_L | BattleLR.BIT_R);
            joy[0] &= mask;
            joy[1] &= mask;
            joy[2] &= mask;

            if (BattleLR.Overlay == 0) return;
            if ((joy[2] & BattleLR.DIR_BITS) != 0)
            {
                // カーソル移動 / ページ送りで差し替えを解除し、本体にそのまま処理させる
                BattleLR.Overlay = 0;
                return;
            }
            if ((joy[2] & BattleLR.BIT_OK) != 0)
            {
                // 決定: 本体の防御/退却ページの決定処理を借りる
                __state.Page = __instance.cur_page;
                __state.Line = __instance.cur_line;
                __state.Swapped = true;
                __instance.cur_page = BattleLR.PAGE_GUARD;
                __instance.cur_line = (BattleLR.Overlay == 2) ? BattleLR.LINE_GUARD : BattleLR.LINE_ESCAPE;
            }
        }

        private static void Postfix(Battle __instance, int __result, State __state)
        {
            if (!BattleLR.On) return;
            int[] joy = __instance.joy1;
            joy[0] = __state.J0;
            joy[1] = __state.J1;
            joy[2] = __state.J2;
            if (__state.Swapped)
            {
                __instance.cur_page = __state.Page;
                __instance.cur_line = __state.Line;
                __instance.A3 = __instance.membermenu[__instance.cur_page];
            }
            // コマンドが決まった / キャンセルした / メッセージに移った時は差し替えを終える
            if (__result != 0) BattleLR.Overlay = 0;
        }
    }

    /// <summary>
    /// マウス (タッチ) で画面の矢印ボタンを押した時は、本体が L / R のビット (0x10 / 0x40) にしているのを
    /// 十字キーの左 / 右 (0x10000 / 0x40000) に読み替える。矢印はページ送りの絵なので、マウスでは退却 / 防御にせず
    /// 本体どおり前 / 次のページにする (パッドの L / R だけが退却 / 防御)。
    /// InputManager.get_button_name_touch_up は同じフレーム内なら何度呼んでも同じ名前を返す。
    /// </summary>
    [HarmonyPatch(typeof(Battle), "InputBattleUI")]
    internal static class BattleLRTouchPatch
    {
        private const int DIR_LEFT = 0x10000;
        private const int DIR_RIGHT = 0x40000;

        private static void Postfix(Battle __instance)
        {
            if (!BattleLR.On) return;
            string name = InputManager.get_button_name_touch_up();
            int from, to;
            if (name == "L_btn") { from = BattleLR.BIT_L; to = DIR_LEFT; }
            else if (name == "R_btn") { from = BattleLR.BIT_R; to = DIR_RIGHT; }
            else return;
            int[] joy = __instance.joy1;
            for (int i = 0; i < 3; i++)
            {
                if ((joy[i] & from) == 0) continue;
                joy[i] = (joy[i] & ~from) | to;
            }
        }
    }

    /// <summary>戦闘開始時に差し替え状態を消す (前の戦闘をタイトルに戻る等で途中終了した時の残りを持ち込まない)。</summary>
    [HarmonyPatch(typeof(Battle), "InitializeBattleUI")]
    internal static class BattleLRInitPatch
    {
        private static void Postfix() { BattleLR.Overlay = 0; BattleLR.InDrawCommand = false; BattleLR.ResetArrows(); }
    }

    [HarmonyPatch(typeof(Battle), "DrawCommand")]
    internal static class BattleLRDrawPatch
    {
        private static void Prefix(Battle __instance)
        {
            BattleLR.InDrawCommand = BattleLR.On && BattleLR.Overlay != 0 && BattleLR.IsVisible(__instance);
        }

        private static void Postfix(Battle __instance)
        {
            BattleLR.InDrawCommand = false;
            CSpriteStudioObject ui = BattleLR.UiBattle(__instance);
            BattleLR.ApplyArrows(ui);   // L / R ボタンの文字を消す (Off なら戻す)
            if (!BattleLR.On || BattleLR.Overlay == 0) return;
            if (ui == null || !ui.IsReady() || !BattleLR.IsVisible(__instance)) return;
            int row = __instance.cur_line - BattleLR.MenuScroll;
            string[] names = BattleLR.NameCommand(__instance);
            if (row < 0 || row >= names.Length) return;

            CSpriteAnalyze a = ui.GetAnalyzeData(names[row]);
            if (a == null || a.Data == null) return;
            a.Data.type = 0;
            a.Data.data1 = BattleLR.MSG_SHEET_NAME;
            a.Data.data2 = BattleLR.MsgId;
            a.Data.fontColor = 1;
            a.DrawString = a.Data.GetMessageText();

            CSpriteAnalyze use = ui.GetAnalyzeData(BattleLR.NameUse(__instance)[row]);
            if (use != null) use.IsVisible = false;
            CSpriteAnalyze point = ui.GetAnalyzeData(BattleLR.NamePoint(__instance)[row]);
            if (point != null) point.IsVisible = false;

            // 説明文は短いので流さない (本体は元のコマンドの説明文の長さで流しカウンタを進める)
            BattleLR.HelpScroll(__instance) = 0;
            BattleLR.HelpScrollWait(__instance) = 0;
        }
    }

    /// <summary>DrawCommand の中で描かれる説明文 (y = 475 の 1 行) を差し替え中の説明文にする。</summary>
    [HarmonyPatch(typeof(GS), "DrawString", typeof(string), typeof(int), typeof(int), typeof(int), typeof(Color32), typeof(GS.FontEffect))]
    internal static class BattleLRHelpPatch
    {
        private static void Prefix(ref string str, ref int _x, int _y)
        {
            if (!BattleLR.InDrawCommand || _y != 475) return;
            string t = BattleLR.GetMenuMsg(BattleLR.MSG_SHEET_HELP, BattleLR.MsgId);
            if (string.IsNullOrEmpty(t)) return;
            str = t;
            _x = 175;
        }
    }
}
