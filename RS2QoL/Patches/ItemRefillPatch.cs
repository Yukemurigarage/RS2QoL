using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 傷薬 / 高級傷薬 の補充 ([Storage] RefillItem / RefillPerMember / RefillAfterBattle。設定画面「倉庫」ページに集める)。
    /// 仲間の空いている武器の欄に、持ち物 → 倉庫 の順で取って入れる。本体には無い操作 (倉庫は 預ける / 引き出す だけ、装備は 1 人 1 欄ずつ)。
    /// どこでもアバロンの窓 (どこでも倉庫) には置かない (ユーザー決定 2026-10-24: 似た機能を窓と設定画面に散らさない)。
    ///
    /// 本体の事実 (decompiled Menu / Battle、2026-10-24 調査):
    ///  - 装備欄は memberdata[m][77 + k]: k 0..3 が武器、4..7 が防具。255 = 空欄。
    ///  - 本体の装備の確定 (Menu.soubi_a3) は武器なら「持ち物の個数を 1 減らす (0 なら枠を詰める) → memberdata[77 + 欄] = ID」だけ。
    ///    能力値・重さの組み直し (armor_add_prop) は防具だけなので、武器の欄への書き込みで体重計・ステータスの数字はずれない。
    ///  - memberdata[m][7] の 0x40 は装備を外せない仲間 (本体の soubi_irekae / _all_equip_stock が飛ばす) → ここも飛ばす。
    ///  - 武器の欄に入る消耗品は 傷薬 (94) と 高級傷薬 (95) だけ (武器 ID 0..95 の末尾 2 つ。name.bin で確認)。
    ///  - 戦闘で使うと Battle.lost_weapon が欄を 255 に戻す。宝箱・ドロップの品物は持ち物 (partydata[33..]) に入り、欄には入らない。
    /// 戦闘後の自動補充は Battle.dest (Core.sync の battle_proc[1]==200、フィールドに戻る直前) の Postfix。全滅・皇帝の戦闘不能
    /// (party_result 1 / 2) の後は継承で装備が倉庫へ移るので補充しない。ソフトリセットの EndBattleForReset から来た時も何もしない。
    /// </summary>
    internal static class ItemRefill
    {
        public const string NAME = "どこでも倉庫";
        public const int KIZUGUSURI = 94, KOUKYU = 95;
        public const int SLOT_FIRST = 77, WEAPON_SLOTS = 4, EMPTY = 255;
        private const int LOCKED = 0x40;   // memberdata[7]: 装備を外せない仲間
        private static readonly int[] ITEMS = { KIZUGUSURI, KOUKYU };

        public static ConfigEntry<int> Item;
        public static ConfigEntry<int> PerMember;
        public static ConfigEntry<bool> AfterBattle;

        public static void Bind(ConfigFile config)
        {
            Item = config.Bind("Storage", "RefillItem", KIZUGUSURI,
                new ConfigDescription("設定画面「倉庫」ページの補充 (今すぐ補充する / 戦闘後の自動補充) で空いている武器の欄に入れる品物。94 = 傷薬 / 95 = 高級傷薬 (本体で武器の欄に入る消耗品はこの 2 つだけ)",
                    new AcceptableValueList<int>(KIZUGUSURI, KOUKYU)));
            PerMember = config.Bind("Storage", "RefillPerMember", 2,
                new ConfigDescription("補充で 1 人に持たせる個数 (1〜4)。空いている武器の欄にだけ入れる (今装備している武器は動かさない)。既に持っている分は数に入れる",
                    new AcceptableValueRange<int>(1, WEAPON_SLOTS)));
            AfterBattle = config.Bind("Storage", "RefillAfterBattle", false,
                "戦闘が終わるたびに自動で補充する (RefillItem を RefillPerMember 個まで、持ち物 → 倉庫 の順で取る)。補充した時は画面上部の小窓で知らせる。倉庫の在庫が気づかないうちに減っていくことに注意。全滅・皇帝が倒れた戦闘の後は補充しない");
        }

        public static int CurrentItem { get { return Item != null && Item.Value == KOUKYU ? KOUKYU : KIZUGUSURI; } }
        public static int Limit { get { return PerMember == null ? 2 : UnityEngine.Mathf.Clamp(PerMember.Value, 1, WEAPON_SLOTS); } }

        public static string ItemLabel() { return ItemStorage.Name(CurrentItem); }
        public static string LimitLabel() { return "1 人 " + Limit + " 個まで"; }
        /// <summary>設定画面「倉庫」ページの在庫の行: 持ち物 / 倉庫 の個数 (ゲーム中以外は空)。</summary>
        public static string StockLabel()
        {
            if (!ItemStorage.InGame) return "";
            int id = CurrentItem;
            return ItemStorage.HeldCount(id) + " / " + ItemStorage.StoredCount(id);
        }

        /// <summary>左右: 傷薬 ⇄ 高級傷薬。</summary>
        public static void CycleItem(int dir)
        {
            if (Item == null) return;
            Item.Value = Item.Value == KIZUGUSURI ? KOUKYU : KIZUGUSURI;
        }

        public static void CycleLimit(int dir)
        {
            if (PerMember == null) return;
            int v = Limit + dir;
            if (v < 1) v = WEAPON_SLOTS; else if (v > WEAPON_SLOTS) v = 1;
            PerMember.Value = v;
        }

        /// <summary>
        /// 仲間全員の空いている武器の欄に id を入れる (1 人 Limit 個まで。既に持っている分は数に入れる)。持ち物から先に取り、無ければ倉庫から引き出す。
        /// 戻り値は結果の文。moved = 入れた個数。
        /// </summary>
        public static string Refill(int id, out int moved) { return Refill(id, out moved, false); }

        /// <summary>lines = true なら戦闘後の小窓用に 3 行 (改行 '\n') で短く (幅を取らないように。ユーザー 2026-10-25)。false は設定画面の 1 行の結果。</summary>
        public static string Refill(int id, out int moved, bool lines)
        {
            moved = 0;
            if (!ItemStorage.InGame || Work.memberdata == null) return "ゲーム中だけ使える";
            string name = ItemStorage.Name(id);
            int limit = Limit;
            int fromBag = 0, fromStore = 0, locked = 0, full = 0, noRoom = 0;
            bool shortage = false;
            int n = PartyWeight.Count();
            for (int m = 0; m < n && !shortage; m++)
            {
                int[] d = m < Work.memberdata.Length ? Work.memberdata[m] : null;
                if (d == null || d.Length <= SLOT_FIRST + WEAPON_SLOTS) continue;
                if ((d[7] & LOCKED) != 0) { locked++; continue; }
                int have = 0, empty = 0;
                for (int s = 0; s < WEAPON_SLOTS; s++)
                {
                    if (d[SLOT_FIRST + s] == id) have++;
                    else if (d[SLOT_FIRST + s] == EMPTY) empty++;
                }
                int need = limit - have;
                if (need <= 0) { full++; continue; }
                if (empty == 0) { noRoom++; continue; }
                for (int s = 0; s < WEAPON_SLOTS && need > 0; s++)
                {
                    if (d[SLOT_FIRST + s] != EMPTY) continue;
                    if (ItemStorage.TakeFromParty(id)) fromBag++;
                    else if (ItemStorage.TakeFromStorage(id)) fromStore++;
                    else { shortage = true; break; }
                    d[SLOT_FIRST + s] = id;
                    need--;
                    moved++;
                }
            }
            string stock = " (残り 持ち物 " + ItemStorage.HeldCount(id) + "、倉庫 " + ItemStorage.StoredCount(id) + ")";
            string msg;
            if (moved > 0 && lines)
            {
                msg = name + " を " + moved + " 個補充した" + (shortage ? " (途中まで)" : "")
                    + "\n持ち物から " + fromBag + "、倉庫から " + fromStore
                    + "\n残り 持ち物 " + ItemStorage.HeldCount(id) + "、倉庫 " + ItemStorage.StoredCount(id);
            }
            else if (moved > 0)
            {
                msg = name + " を " + moved + " 個補充した (持ち物から " + fromBag + "、倉庫から " + fromStore + ")" + stock;
                if (shortage) msg += " 足りなくなったので途中まで";
            }
            else if (shortage) msg = name + " が持ち物にも倉庫にも無い";
            else if (full > 0 && noRoom == 0) msg = "全員が " + name + " を " + limit + " 個以上持っている" + stock;
            else if (noRoom > 0) msg = "空いている武器の欄が無い (" + noRoom + " 人)" + (full > 0 ? "、残りは足りている" : "") + stock;
            else if (locked > 0 && n == locked) msg = "装備を変えられない仲間だけ";
            else msg = "補充する相手がいない";
            Plugin.Log.LogInfo("[Refill] id " + id + " (" + name + ") moved " + moved + " bag " + fromBag + " store " + fromStore
                + " limit " + limit + " members " + n + " full " + full + " noRoom " + noRoom + " locked " + locked + (shortage ? " shortage" : ""));
            return msg;
        }

        /// <summary>戦闘の後始末 (Battle.dest) から。設定が On で、負け (継承) でなく、ソフトリセット中でなければ補充し、知らせは Tick に預ける。</summary>
        public static void OnBattleEnd(Battle b)
        {
            if (AfterBattle == null || !AfterBattle.Value) return;
            if (SoftReset.Active) return;
            if (b != null && (b.party_result == 1 || b.party_result == 2)) return;   // 皇帝の戦闘不能 / 全滅 → 継承で装備が倉庫へ移る
            try
            {
                int moved;
                string msg = Refill(CurrentItem, out moved, true);
                if (moved > 0)
                {
                    // Battle.dest はフィールドのフェードインより前。ここで出すと暗い画面に小窓だけが先に現れて驚かせる
                    // (ユーザー 2026-10-25) ので、文を預けて、フィールドで自由に動けるようになった最初のフレームで出す (遅延は入れない)。
                    s_pending = msg;
                    s_pendingSince = UnityEngine.Time.realtimeSinceStartup;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Refill] after battle failed: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- 戦闘後の知らせの先送り
        private const float NOTICE_GIVE_UP = 30f;   // 戦闘の直後にイベントが続いてこれだけ出せなかったら捨てる (古い知らせを後から出さない)
        private static string s_pending;
        private static float s_pendingSince;

        /// <summary>Plugin.OnGUI から毎フレーム。預かった知らせを、フィールドで自由になった (フェードインも終わった) 最初のフレームで出す。</summary>
        public static void Tick()
        {
            if (s_pending == null) return;
            try
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                Core c = Main.core;
                if (c == null || c.proc == null || Main.proc != 2 || c.proc[0] != 1 || SoftReset.Active || now > s_pendingSince + NOTICE_GIVE_UP)
                {
                    if (Plugin.DiagOn) Plugin.Log.LogInfo("[Refill] notice dropped (" + (now > s_pendingSince + NOTICE_GIVE_UP ? "timeout" : "left the field") + ")");
                    s_pending = null;
                    return;
                }
                if (!Wallet.IsFieldFree(c)) return;
                Wallet.ShowNotice(s_pending, NAME, false, 5f);
                if (Plugin.DiagOn) Plugin.Log.LogInfo("[Refill] notice shown " + (now - s_pendingSince).ToString("0.00") + " s after Battle.dest");
                s_pending = null;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Refill] notice failed: " + e.Message);
                s_pending = null;
            }
        }
    }

    [HarmonyPatch(typeof(Battle), nameof(Battle.dest))]
    internal static class ItemRefillBattleEndPatch
    {
        private static void Postfix(Battle __instance)
        {
            ItemRefill.OnBattleEnd(__instance);
        }
    }
}
