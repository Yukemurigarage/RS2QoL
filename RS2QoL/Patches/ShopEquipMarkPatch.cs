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
    /// 絵の横に E を描く。表示は ShopBubble (本体の会話窓を小さくした吹き出し + ドット絵の文字)。買う画面では ShopEquipCompare が
    /// E と ▲N / ▼N を 1 つの吹き出しにまとめて描くので、ここが描くのは売る一覧と、比べる対象でない品物 (道具など) の時だけ。
    /// </summary>
    internal static class ShopEquipMark
    {
        public static ConfigEntry<bool> Enabled;

        private const int EQUIP_SLOT_FIRST = 77, EQUIP_SLOTS = 8;
        private static readonly int[] s_e = { ShopBubble.GLYPH_E, ShopBubble.GLYPH_BANG };   // E!

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
            if (ShopEquipCompare.Handles(control, item)) return;   // 買う画面では ShopEquipCompare が E も含めて 1 つの吹き出しに描く
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
                    float x0, y0;
                    ShopBubble.Anchor(rect, out x0, out y0);
                    ShopBubble.Draw(i, s_e, 2, x0, y0, pri);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[ShopEquipMark] disabled until restart: " + e.Message);
                s_failed = true;   // 設定 (cfg) は書き換えない
            }
        }

        private static bool s_failed;

        /// <summary>この仲間に E が出ているか (ShopEquipCompare が同じ行で E の右に数字を置くために見る)。</summary>
        internal static bool Shown(int member, int item)
        {
            return Enabled != null && Enabled.Value && !s_failed && item >= 0 && item < 255 && Equipped(member, item);
        }

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
