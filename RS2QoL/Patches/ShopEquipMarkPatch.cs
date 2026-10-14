using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 店で、カーソルが乗っている (または買う / 売ると決めた) 品物をすでに装備している仲間の絵の横に「E」を出す
    /// ([Menu] ShopEquipMark、既定 true)。
    ///
    /// 本体の店の画面 (Menu の stage 33 = 買う品物の一覧 "1_b_1_Control"、34 = 買う個数 / 36 = 売る個数 "1_b_2_Control"、
    /// 35 = 売る品物の一覧 "1_c_1_Control") はどれも仲間 5 人の絵を出すが、個数の画面でパーティ全体の「装備数」を出すだけで、
    /// 誰が装備しているかは分からない。各画面の init_shop_* は mode 0 の間毎フレーム呼ばれ、その中の player_sel_obj_all が
    /// 仲間の絵を "p_obj_01".."p_obj_05" の矩形に毎フレーム描き直している。ここではその Postfix で同じ矩形を取り、
    /// 装備欄 (memberdata[i][77..84] の 8 枠。本体の item_have_chk が「装備数」を数えるのと同じ場所) に品物があれば
    /// 絵の右上に E を描く。E は隣のキャラの絵と同じ粗さのドット絵 (5x7) で、本体の GS.FillRect (仮想 960x540 の座標) で描く。
    /// </summary>
    internal static class ShopEquipMark
    {
        public static ConfigEntry<bool> Enabled;

        private const int EQUIP_SLOT_FIRST = 77, EQUIP_SLOTS = 8;
        // E は本体のフォントではなく、隣のキャラの絵と同じ粗さのドット絵で描く (本体のフォントを半分にすると線が細すぎる)。
        // 1 ドット = キャラの絵の 1 ドット (16 ドット幅の絵が 36 単位の矩形に描かれるので 36/16 = 2.25 単位)。
        // 字形は昔の 8x8 フォント風の 5x7。行ごとに横につながった部分を 1 つの矩形にして GS.FillRect で描く (1 文字 7 矩形)
        private const float DOT = 36f / 16f;
        private static readonly int[] s_rows = { 5, 1, 1, 4, 1, 1, 5 };   // 各行の左端からの横幅 (ドット)
        private const float OFFSET_X = 2f + DOT, OFFSET_Y = -2f;      // 絵の右上。少し上げて頭の上端に揃える (仮想 960x540 の単位)
        private const float SHADOW_DOTS = 1f;                   // 右下に 1 ドットずらした薄い影を先に描き、その上に本体の黒を重ねる
        private static readonly Color32 s_color = new Color32(48, 48, 48, 255);
        private static readonly Color32 s_shadow = new Color32(0, 0, 0, 72);

        private static void DrawE(float x, float y, int pri)
        {
            float sx = x + DOT * SHADOW_DOTS, sy = y + DOT * SHADOW_DOTS;
            for (int r = 0; r < s_rows.Length; r++)
                GS.FillRect(sx, sy + DOT * r, pri, DOT * s_rows[r], DOT, s_shadow);
            for (int r = 0; r < s_rows.Length; r++)
                GS.FillRect(x, y + DOT * r, pri, DOT * s_rows[r], DOT, s_color);
        }

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Menu", "ShopEquipMark", true,
                "店の買う / 売る品物の一覧と確認画面で、カーソルが乗っている品物をすでに装備している仲間の絵の横に E を出す");
        }

        /// <summary>その画面の仲間の絵 (p_obj_01..05) の横に、item を装備している仲間だけ E を描く。</summary>
        internal static void Draw(string control, int item)
        {
            if (Enabled == null || !Enabled.Value || s_failed) return;
            if (item < 0 || item >= 255) return;
            try
            {
                CSpriteStudioObject sso = SpriteStudioManager.GetSpriteStudio(control);
                if (sso == null || !sso.IsReady()) return;
                // 本体の chk_flag_34h と同じ: partydata[149] != 0 (ひとり旅) なら 1 人、それ以外はパーティ人数
                int count = (Work.partydata[149] == 0) ? Work.partydata[24] : 1;
                if (count > 5) count = 5;
                int pri = sso.GetPriority() + 101;   // キャラの絵 (player_sel_obj_all は priority + 100) のすぐ上
                for (int i = 0; i < count; i++)
                {
                    if (!Equipped(i, item)) continue;
                    Rect rect = sso.GetSpriteRect("p_obj_" + (i + 1).ToString("00"));
                    if (rect.width <= 0f) continue;
                    DrawE(rect.x + rect.width + OFFSET_X, rect.y + OFFSET_Y, pri);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[ShopEquipMark] disabled until restart: " + e.Message);
                s_failed = true;   // 設定 (cfg) は書き換えない
            }
        }

        private static bool s_failed;

        private static bool Equipped(int member, int item)
        {
            int[] data = Work.memberdata[member];
            for (int k = 0; k < EQUIP_SLOTS; k++)
            {
                if (data[EQUIP_SLOT_FIRST + k] == item) return true;
            }
            return false;
        }
    }

    // 買う品物の一覧。カーソル (gene_te2) の品物は make_shop_item が出しているのと同じ equip_work[gene_te2 * 8]
    [HarmonyPatch(typeof(Menu), "init_shop_kau")]
    internal static class ShopEquipMarkBuyListPatch
    {
        private static void Postfix(Menu __instance)
        {
            int idx = __instance.gene_te2 * 8;
            if (idx < 0 || idx >= __instance.equip_work.Length) return;
            ShopEquipMark.Draw("1_b_1_Control", __instance.equip_work[idx]);
        }
    }

    // 買う / 売る個数の画面。品物は shop_kau_a / shop_uru_a が置いた equip_work[index1]
    [HarmonyPatch(typeof(Menu), "init_shop_kau_2")]
    internal static class ShopEquipMarkConfirmPatch
    {
        private static void Postfix(Menu __instance)
        {
            int idx = __instance.index1;
            if (idx < 0 || idx >= __instance.equip_work.Length) return;
            ShopEquipMark.Draw("1_b_2_Control", __instance.equip_work[idx]);
        }
    }

    // 売る品物の一覧 (持ち物の並び)。カーソルの品物は shop_uru_a と同じ partydata[33 + (page_no * 4 + gene_te2) * 2]
    [HarmonyPatch(typeof(Menu), "init_shop_uru")]
    internal static class ShopEquipMarkSellListPatch
    {
        private static void Postfix(Menu __instance)
        {
            int idx = 33 + (__instance.page_no * 4 + __instance.gene_te2) * 2;
            if (idx < 33 || idx >= Work.partydata.Length) return;
            ShopEquipMark.Draw("1_c_1_Control", Work.partydata[idx]);
        }
    }
}
