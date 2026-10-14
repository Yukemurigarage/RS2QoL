using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 店で、カーソルの品物に合わせて仲間の絵を戦闘でコマンドを決めた時と同じ「くるくる回って構える」動きにする
    /// ([Menu] ShopEquipSpin、既定 true)。
    /// 回るのは E が付く仲間 (カーソルの品物をすでに装備している仲間) だけ。E が誰にも付かなければ誰も回らない (E を目立たせるのが目的)。
    ///   武器 (品物番号 0..95) を装備している仲間 → 攻撃を決めた時の並びで回って武器の構え
    ///   防具・盾・装飾品 (96..207) や道具 (208 以上。傷薬なども武器欄に装備できる) を装備している仲間
    ///     → コマンド窓が開く時の並びで回って選択中の構え
    ///
    /// 戦闘の味方の絵は Effect.p_pat_no (26 ドット角のコマ番号、絵の帯の x = 番号 × 26) で決まり、コマンド決定後の動きは
    /// 効果スクリプト (efe_anm.bin の 301 + mbox[5] 番) がそのコマ番号を順に書く。実機で記録した並び (論理フレーム):
    ///   攻撃を決めた時 (302): 0 (1) → 3 (1) → 1 (2) → 2 (2) → 6 (2) → 7 (武器の構え、そのまま)
    ///   術を決めた時   (303): 0 (1) → 3 (1) → 1 (2) → 2 (2) → 9 (術の構え)
    ///   コマンド窓が開く時 (305): 0 (1) → 3 (1) → 1 (2) → 10 (選択中の構え)
    /// 店の仲間の絵は同じ帯を Menu.chara_tbl (x ずれ, y ずれ, 帯の x, 帯の y, 幅, 高さ, 反転) の添字で描くので、
    /// chara_tbl を伸ばして「添字 64 + コマ番号 = ずれ・反転なしでそのコマ」の行を足し (戦闘はずれなしで描く)、
    /// 上の並びでその添字を流す。武器の区切り (品物番号 96 未満) は本体の装備画面 (lock_type の振り分け) と同じ。
    /// 道具は武器と同じ欄 (memberdata 77..80) に入るので、欄ではなく品物番号で見分ける。
    ///
    /// 店の画面 (stage 33〜36) の仲間の絵は、毎フレームの init_shop_* → player_sel_obj_all (playerMove = 0) →
    /// player_sel_obj_base が dma_oam_adr を横歩き (2 + oam_cnt) で上書きしてから描く。ここでは init_shop_* の Prefix で
    /// 「この画面で回す仲間と、その並び」を調べ、player_sel_obj_base の Prefix でその仲間だけ dma_oam_adr に自前の添字を
    /// 入れて playerMove を 1 にする (本体は playerMove != 0 なら dma_oam_adr を触らない)。品物が変わって新しく対象になった
    /// 仲間 (または並びが変わった仲間) だけ回り始め、対象のままの間は構えのまま。対象から外れた仲間は本体どおりの横歩きに戻る。
    /// 戦闘不能・石化 (memberdata[7] の 0xC0) の仲間は本体が倒れたコマに固定するので触らない。
    /// 本体は memberdata[2] == 31 の仲間だけ添字に 8 を足す (反転の違う行) ので、その分を引いて同じ行に着地させる。
    /// </summary>
    internal static class ShopEquipSpin
    {
        public static ConfigEntry<bool> Enabled;

        private const int TBL_BASE = 64;            // chara_tbl に足す行の先頭 (本体は 16 行、ほかの画面が 0..15 しか使わない)
        private const int PAT_MAX = 16;             // 足す行の数 (コマ番号 0..15)
        private const int CLASS_ALT = 31;           // 本体が添字に 8 を足すクラス
        private const int SLOT_FIRST = 77, SLOTS = 8;
        private const int ITEM_ARMOR_FIRST = 96;    // 0..95 武器、96..207 防具、208.. 道具 (本体の装備画面と同じ区切り)

        // { コマ番号, 続けるフレーム数 }。最後の行は構え (0 = ずっと)
        private static readonly int[][] s_weapon = { new[] { 0, 1 }, new[] { 3, 1 }, new[] { 1, 2 }, new[] { 2, 2 }, new[] { 6, 2 }, new[] { 7, 0 } };
        private static readonly int[][] s_select = { new[] { 0, 1 }, new[] { 3, 1 }, new[] { 1, 2 }, new[] { 10, 0 } };

        private static int s_mask;                  // 今の画面で回す仲間 (bit = 仲間の番号)
        private static int s_lastFrame = -100;
        private static bool s_drawing;
        private static bool s_tableReady;
        private static readonly int[][][] s_seq = new int[5][][];
        private static readonly int[] s_phase = new int[5];
        private static readonly int[] s_count = new int[5];

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Menu", "ShopEquipSpin", true,
                "店で E が付く仲間 (カーソルの品物をすでに装備している仲間) の絵を、戦闘でコマンドを決めた時と同じ「回って構える」動きにする (武器なら攻撃の構え、防具や道具ならコマンド窓が開いた時の構え)");
        }

        /// <summary>chara_tbl を伸ばして、添字 TBL_BASE + コマ番号 の行を足す (一度だけ)。</summary>
        private static bool EnsureTable()
        {
            if (s_tableReady) return true;
            int[][] old = Menu.chara_tbl;
            if (old == null) return false;
            if (old.Length >= TBL_BASE + PAT_MAX) { s_tableReady = true; return true; }
            int[][] tbl = new int[TBL_BASE + PAT_MAX][];
            for (int i = 0; i < tbl.Length; i++)
            {
                if (i < old.Length) tbl[i] = old[i];
                else if (i >= TBL_BASE) tbl[i] = new[] { 0, 0, (i - TBL_BASE) * 26, 0, 26, 26, 0 };
                else tbl[i] = old[2];   // 使わない隙間。万一参照されても横歩きの立ち絵
            }
            Menu.chara_tbl = tbl;
            s_tableReady = true;
            return true;
        }

        /// <summary>品物 item に対して仲間 member を回す並び。回さないなら null。</summary>
        private static int[][] SequenceFor(int member, int item)
        {
            if (EquippedSlot(member, item) < 0) return null;         // 装備していない (E が付かない)
            return (item < ITEM_ARMOR_FIRST) ? s_weapon : s_select;  // 武器だけ攻撃の構え。防具・道具は選択中の構え
        }

        /// <summary>init_shop_* の Prefix から。この画面で回す仲間に印を付け、新しく対象になった仲間は回り始める。</summary>
        internal static void Begin(int item)
        {
            s_drawing = false;
            if (Enabled == null || !Enabled.Value || s_failed) { s_mask = 0; return; }
            // 店を出て戻って来た時 (しばらく呼ばれていない) は前の印を忘れる
            int now = Time.frameCount;
            if (now - s_lastFrame > 10) s_mask = 0;
            s_lastFrame = now;
            if (item < 0 || item >= 255) { s_mask = 0; return; }
            try
            {
                if (!EnsureTable()) { s_mask = 0; return; }
                int count = (Work.partydata[149] == 0) ? Work.partydata[24] : 1;
                if (count > 5) count = 5;
                int mask = 0;
                for (int i = 0; i < count; i++)
                {
                    int[][] seq = SequenceFor(i, item);
                    if (seq == null) continue;
                    mask |= 1 << i;
                    if ((s_mask & (1 << i)) == 0 || s_seq[i] != seq) { s_seq[i] = seq; s_phase[i] = 0; s_count[i] = 0; }
                }
                s_mask = mask;
                s_drawing = mask != 0;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[ShopEquipSpin] disabled until restart: " + e.Message);
                s_failed = true;   // 設定 (cfg) は書き換えない
                s_mask = 0;
            }
        }

        private static bool s_failed;

        internal static void End()
        {
            s_drawing = false;
        }

        /// <summary>item が仲間の装備欄のどこに入っているか (77..84)。無ければ -1。</summary>
        private static int EquippedSlot(int member, int item)
        {
            int[] data = Work.memberdata[member];
            for (int k = 0; k < SLOTS; k++)
            {
                if (data[SLOT_FIRST + k] == item) return SLOT_FIRST + k;
            }
            return -1;
        }

        /// <summary>player_sel_obj_base の Prefix から。印の付いた仲間なら dma_oam_adr に自前の添字を入れて true を返す。</summary>
        internal static bool Frame(Menu menu, int no)
        {
            if (!s_drawing || no < 0 || no >= 5 || (s_mask & (1 << no)) == 0) return false;
            int[][] seq = s_seq[no];
            if (seq == null) return false;
            int p = s_phase[no];
            if (p >= seq.Length) p = seq.Length - 1;
            int pat = seq[p][0];
            int hold = seq[p][1];
            if (hold > 0)
            {
                s_count[no]++;
                if (s_count[no] >= hold) { s_count[no] = 0; s_phase[no] = p + 1; }
            }
            int idx = TBL_BASE + pat;
            if (Work.memberdata[no][2] == CLASS_ALT) idx -= 8;   // 本体が +8 するので同じ行に着地させる
            menu.dma_oam_adr[no] = idx;
            return true;
        }
    }

    /// <summary>player_sel_obj_base(no, x, y, render, playerMove, flg): 印の付いた仲間は自前のコマで描かせる。</summary>
    [HarmonyPatch(typeof(Menu), "player_sel_obj_base", typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int))]
    internal static class ShopEquipSpinDrawPatch
    {
        private static void Prefix(Menu __instance, int no, ref int playerMove)
        {
            if (ShopEquipSpin.Frame(__instance, no)) playerMove = 1;
        }
    }

    // 以下、品物の決め方は ShopEquipMark の各 Postfix と同じ

    [HarmonyPatch(typeof(Menu), "init_shop_kau")]
    internal static class ShopEquipSpinBuyListPatch
    {
        private static void Prefix(Menu __instance)
        {
            int idx = __instance.gene_te2 * 8;
            ShopEquipSpin.Begin((idx < 0 || idx >= __instance.equip_work.Length) ? -1 : __instance.equip_work[idx]);
        }
        private static void Postfix() { ShopEquipSpin.End(); }
    }

    [HarmonyPatch(typeof(Menu), "init_shop_kau_2")]
    internal static class ShopEquipSpinConfirmPatch
    {
        private static void Prefix(Menu __instance)
        {
            int idx = __instance.index1;
            ShopEquipSpin.Begin((idx < 0 || idx >= __instance.equip_work.Length) ? -1 : __instance.equip_work[idx]);
        }
        private static void Postfix() { ShopEquipSpin.End(); }
    }

    [HarmonyPatch(typeof(Menu), "init_shop_uru")]
    internal static class ShopEquipSpinSellListPatch
    {
        private static void Prefix(Menu __instance)
        {
            int idx = 33 + (__instance.page_no * 4 + __instance.gene_te2) * 2;
            ShopEquipSpin.Begin((idx < 33 || idx >= Work.partydata.Length) ? -1 : Work.partydata[idx]);
        }
        private static void Postfix() { ShopEquipSpin.End(); }
    }
}
