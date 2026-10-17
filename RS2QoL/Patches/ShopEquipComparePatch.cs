using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 店で買う品物 (武器・防具・盾) について、仲間 5 人それぞれの今の装備と比べた差 (▲N 強い / ▼N 弱い / 同じなら ±0。E が出ている時は E だけ) を仲間の絵の横に出す
    /// ([Menu] ShopEquipCompare、既定 true)。買う品物の一覧 (stage 33 "1_b_1_Control") と個数の画面 (34 "1_b_2_Control")。
    ///
    /// 比べる数は本体が店で「攻撃力 / 防御力」として出している item_menu_data[no*5+1] (武器は weapon_data[no*4] の攻撃力と同じ、
    /// 防具は armor_data の「斬」) で、本体の表示と同じ 99 で止める。防具の 8 種の防御力の差は防具詳細窓 (ArmorInfo) の仕事。
    ///
    /// 「今の装備」の決め方:
    ///   武器 (品物番号 0..95、94・95 は攻撃力の無い品物なので除く): 武器欄 (memberdata 77..80、傷薬なども入る) の武器のうち、
    ///     同じ系統 (weapon_data[no*4+1] の上位 4 ビット。0x30 = 剣と小剣のように 2 つの技能にまたがる系統もあるので、
    ///     技能の集合が重なれば同系統とみなす) で攻撃力が最大のもの。同系統が無ければ系統を問わず最大のもの。武器が無ければ 0。
    ///   防具・盾 (96..207): 防具欄 (81..84) のうち同じ部位 (item_menu_data[no*5+2] の下位 7 ビット。本体が「同じ部位は
    ///     2 つ装備できない」判定に使うビット) のもの。無ければ 0 (その部位は空いている)。
    ///   道具 (208 以上) は出さない。
    ///
    /// 表示は ShopBubble (本体の会話窓を小さくした吹き出し + 5x7 のドット絵の文字)。買う画面では E (ShopEquipMark) もここで同じ吹き出しに
    /// まとめて描く (E が出ている仲間はその右に ▲N / ▼N。両方出るのは、装備中の武器と同系統にもっと強い武器を持つ時だけ)。
    /// 強弱は三角の向き (▲ 強い / ▼ 弱い) だけで示す。
    /// </summary>
    internal static class ShopEquipCompare
    {
        public static ConfigEntry<bool> Enabled;

        private const int WEAPON_SLOT_FIRST = 77, ARMOR_SLOT_FIRST = 81, SLOTS = 4;

        // 系統 (weapon_data[no*4+1] の上位 4 ビット) → 使う技能の集合。Battle.set_basic_At の振り分けから
        // (剣 1、小剣 2、斧 4、棍棒 8、大剣 16、槍 32、弓 64、爪 128)
        private static int Family(int kind)
        {
            switch (kind & 0xF0)
            {
                case 0x10: return 1;
                case 0x20: return 2;
                case 0x30: return 1 | 2;
                case 0x40: return 4;
                case 0x50: return 8;
                case 0x80: return 16;
                case 0x90: return 32;
                case 0xA0: return 64;
                case 0xB0: return 1 | 16;
                case 0xC0: return 32 | 4;
                case 0xD0: return 16 | 32;
                case 0xE0: return 128;
                default: return 0;
            }
        }

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Menu", "ShopEquipCompare", true,
                "店で買う品物 (武器・防具・盾) について、仲間それぞれの今の装備と比べた差 (▲N 強い / ▼N 弱い / 同じなら ±0。E が出ている時は E だけ) を仲間の絵の横に出す。武器は同じ系統の武器のうち攻撃力が最大のものと、防具・盾は同じ部位に着けているものと比べる。数は本体が表示している攻撃力 / 防御力 (防具は「斬」)");
        }

        private static bool IsWeapon(int item) { return item >= 0 && item < 96 && item != 94 && item != 95; }
        private static bool IsArmor(int item) { return item >= 96 && item <= 207; }

        private static int Power(int item)
        {
            int v = Data.item_menu_data[item * 5 + 1] & 0xFF;
            return v > 99 ? 99 : v;
        }

        /// <summary>member (パーティの並び) の今の装備と item の差。比べる物が無ければ 0 との差。</summary>
        internal static int Diff(int member, int item)
        {
            int[] data = Work.memberdata[member];
            int baseline = 0;
            if (IsWeapon(item))
            {
                int fam = Family(Data.weapon_data[item * 4 + 1] & 0xFF);
                int bestSame = -1, bestAny = -1;
                for (int k = 0; k < SLOTS; k++)
                {
                    int w = data[WEAPON_SLOT_FIRST + k];
                    if (!IsWeapon(w)) continue;
                    int p = Power(w);
                    if (p > bestAny) bestAny = p;
                    if (fam != 0 && (Family(Data.weapon_data[w * 4 + 1] & 0xFF) & fam) != 0 && p > bestSame) bestSame = p;
                }
                baseline = bestSame >= 0 ? bestSame : (bestAny >= 0 ? bestAny : 0);
            }
            else
            {
                int kind = Data.item_menu_data[item * 5 + 2] & 0x7F;
                for (int k = 0; k < SLOTS; k++)
                {
                    int a = data[ARMOR_SLOT_FIRST + k];
                    if (!IsArmor(a)) continue;
                    if (((Data.item_menu_data[a * 5 + 2] & 0x7F) & kind) == 0) continue;
                    int p = Power(a);
                    if (p > baseline) baseline = p;
                }
            }
            return Power(item) - baseline;
        }

        private static bool s_failed, s_applied;

        /// <summary>その画面の仲間の絵 (p_obj_01..05) の横に item と今の装備の差を描く。init_shop_* の Postfix から毎フレーム。</summary>
        internal static void Draw(string control, int item)
        {
            if (Enabled == null || !Enabled.Value || s_failed) return;
            if (!IsWeapon(item) && !IsArmor(item)) return;
            if (Data.item_menu_data == null || Data.weapon_data == null) return;
            try
            {
                CSpriteStudioObject sso = SpriteStudioManager.GetSpriteStudio(control);
                if (sso == null || !sso.IsReady()) return;
                // ShopEquipMark と同じ: partydata[149] != 0 (ひとり旅) なら 1 人、それ以外はパーティ人数
                int count = (Work.partydata[149] == 0) ? Work.partydata[24] : 1;
                if (count > 5) count = 5;
                int pri = sso.GetPriority() + 101;
                for (int i = 0; i < count; i++)
                {
                    Rect rect = sso.GetSpriteRect("p_obj_" + (i + 1).ToString("00"));
                    if (rect.width <= 0f) continue;
                    int diff = Diff(i, item);
                    if (!s_applied)
                    {
                        s_applied = true;
                        Plugin.Log.LogInfo(string.Format("[ShopEquipCompare] applied: {0} item={1} member0 diff={2} rect=({3},{4},{5},{6})",
                            control, item, diff, rect.x, rect.y, rect.width, rect.height));
                    }
                    int n = 0;
                    if (ShopEquipMark.Shown(i, item)) { s_glyphs[n++] = ShopBubble.GLYPH_E; s_glyphs[n++] = ShopBubble.GLYPH_BANG; }   // E!
                    if (diff == 0) { if (n == 0) { s_glyphs[n++] = ShopBubble.GLYPH_PM; s_glyphs[n++] = 0; } }   // 同じ強さは ±0 (出さないと「吹き出しが出ない」ように見えた)。E が出ている時は E だけ
                    else
                    {
                        s_glyphs[n++] = diff > 0 ? ShopBubble.GLYPH_UP : ShopBubble.GLYPH_DOWN;
                        int a = diff < 0 ? -diff : diff;
                        if (a > 99) a = 99;
                        if (a >= 10) s_glyphs[n++] = a / 10;
                        s_glyphs[n++] = a % 10;
                    }
                    if (n == 0) continue;
                    float x0, y0;
                    ShopBubble.Anchor(rect, out x0, out y0);
                    ShopBubble.Draw(i, s_glyphs, n, x0, y0, pri);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[ShopEquipCompare] disabled until restart: " + e.Message);
                s_failed = true;   // 設定 (cfg) は書き換えない
            }
        }

        private static readonly int[] s_glyphs = new int[5];   // E ! ▼ 1 0 が最長

        /// <summary>この画面・この品物の表示 (E も含めて) をここが引き受けるか。true なら ShopEquipMark は描かない。</summary>
        internal static bool Handles(string control, int item)
        {
            return Enabled != null && Enabled.Value && !s_failed && ShopBubble.Ok
                && (IsWeapon(item) || IsArmor(item)) && Data.item_menu_data != null && Data.weapon_data != null
                && control != "1_c_1_Control";
        }
    }

    // 以下、品物の決め方は ShopEquipMark の各 Postfix と同じ (買う側だけ。売る一覧では比べる意味が薄い)
    [HarmonyPatch(typeof(Menu), "init_shop_kau")]
    internal static class ShopEquipCompareBuyListPatch
    {
        private static void Postfix(Menu __instance)
        {
            int idx = __instance.gene_te2 * 8;
            if (idx < 0 || idx >= __instance.equip_work.Length) return;
            ShopEquipCompare.Draw("1_b_1_Control", __instance.equip_work[idx]);
        }
    }

    [HarmonyPatch(typeof(Menu), "init_shop_kau_2")]
    internal static class ShopEquipCompareConfirmPatch
    {
        private static void Postfix(Menu __instance)
        {
            int idx = __instance.index1;
            if (idx < 0 || idx >= __instance.equip_work.Length) return;
            ShopEquipCompare.Draw("1_b_2_Control", __instance.equip_work[idx]);
        }
    }
}
