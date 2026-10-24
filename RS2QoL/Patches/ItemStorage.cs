using System.Collections.Generic;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 倉庫 (アバロンの預かり所) と持ち物の読み書き。どこでも倉庫のページ (TreasuryScreen) が使う。
    ///
    /// 本体の持ち方 (Menu.souko_item_chk / hikidasu_a / azukeru_a / party_item_serch / item_work_pless から):
    /// - 倉庫 = 品物 ID ごとの個数 1 つずつ。Work.partydata[421 + id] (id 0..254、0 = 無し、上限 99)。並びは ID 順
    ///   (0..95 武器、96..207 防具、208..254 道具 = 本体の倉庫画面の 3 つの札)。
    /// - 持ち物 = 32 枠の (ID, 個数) の対。Work.partydata[33 + 枠 * 2] と [34 + 枠 * 2]。空き枠は ID 255 / 個数 0 で、
    ///   空きは必ず末尾に詰める (item_work_pless)。同じ ID は 1 枠にまとめ、1 枠 99 個まで。
    /// - 名前は MsgDataManager.GetMenuMsg(6, id + 1) (本体の倉庫画面と同じ表)。
    /// - 持ち物に ID 240 があるかの印 Work.partydata[150] (Menu.item_0f0h_chk) は本体がメニューを閉じる時に作り直す。
    ///   こちらで持ち物を変えたら同じ計算で作り直す (フィールドの移動の判定 Core 9017 / 9427 が読む)。
    /// - 本体の倉庫画面は開くたびに ID 13 と 87 の倉庫の個数を 0 にする (Menu.init_souko)。その 2 つは預けても本体で消えるので、
    ///   こちらでは預けられない品物として断る。
    /// </summary>
    internal static class ItemStorage
    {
        public const int STORAGE_BASE = 421, ITEM_IDS = 255, STACK_MAX = 99;
        public const int PARTY_BASE = 33, PARTY_SLOTS = 32, NONE = 255;
        public const int FLAG_ITEM240 = 150;
        private static readonly int[] UNSTORABLE = { 13, 87 };   // Menu.init_souko が倉庫から消す ID

        public struct Entry
        {
            public int Id;      // 品物 ID
            public int Stored;  // 倉庫の個数
            public int Held;    // 持ち物の個数 (装備中は数えない = 本体の預ける画面と同じ)
        }

        private static bool Ready { get { return Work.partydata != null && Work.partydata.Length > STORAGE_BASE + ITEM_IDS; } }
        /// <summary>partydata が使える (ゲーム中)。ItemRefill 用。</summary>
        public static bool InGame { get { return Ready; } }

        /// <summary>持ち物から id を 1 個取る (装備するために消す。本体の soubi_a3 と同じ: 個数 −1、0 なら枠を詰める)。無ければ false。</summary>
        public static bool TakeFromParty(int id)
        {
            if (!Ready) return false;
            int slot = PartySlot(id);
            if (slot < 0 || Work.partydata[PARTY_BASE + slot * 2 + 1] <= 0) return false;
            Work.partydata[PARTY_BASE + slot * 2 + 1]--;
            if (Work.partydata[PARTY_BASE + slot * 2 + 1] <= 0) PartyRemoveSlot(slot);
            RefreshItem240Flag();
            return true;
        }

        /// <summary>倉庫から id を 1 個取る (持ち物を経由せずに装備するため)。無ければ false。</summary>
        public static bool TakeFromStorage(int id)
        {
            if (!Ready || StoredCount(id) <= 0) return false;
            Work.partydata[STORAGE_BASE + id]--;
            return true;
        }

        public static int StoredCount(int id)
        {
            if (!Ready || id < 0 || id >= ITEM_IDS) return 0;
            return Work.partydata[STORAGE_BASE + id];
        }

        public static int HeldCount(int id)
        {
            if (!Ready) return 0;
            for (int i = 0; i < PARTY_SLOTS; i++)
            {
                int v = Work.partydata[PARTY_BASE + i * 2];
                if (v == id) return Work.partydata[PARTY_BASE + i * 2 + 1];
                if (v == NONE) break;
            }
            return 0;
        }

        /// <summary>倉庫にある品物 (ID 順 = 本体の並び)。</summary>
        public static List<Entry> StoredList()
        {
            List<Entry> list = new List<Entry>();
            if (!Ready) return list;
            for (int id = 0; id < ITEM_IDS; id++)
            {
                int n = Work.partydata[STORAGE_BASE + id];
                if (n <= 0) continue;
                Entry e; e.Id = id; e.Stored = n; e.Held = HeldCount(id);
                list.Add(e);
            }
            return list;
        }

        /// <summary>持ち物 (枠の順 = 本体のアイテム欄の並び)。</summary>
        public static List<Entry> HeldList()
        {
            List<Entry> list = new List<Entry>();
            if (!Ready) return list;
            for (int i = 0; i < PARTY_SLOTS; i++)
            {
                int id = Work.partydata[PARTY_BASE + i * 2];
                if (id == NONE) break;
                int n = Work.partydata[PARTY_BASE + i * 2 + 1];
                if (n <= 0) continue;
                Entry e; e.Id = id; e.Stored = StoredCount(id); e.Held = n;
                list.Add(e);
            }
            return list;
        }

        public static string Name(int id)
        {
            string s = null;
            try { s = BattleLR.GetMenuMsg(6, id + 1); } catch { }   // MsgDataManager は internal (BattleLR の反射の helper を使う)
            return string.IsNullOrEmpty(s) ? "品物 " + id : s;
        }

        /// <summary>品物の説明 (本体の倉庫画面の下の文と同じ表 = GetMenuMsg(7, id + 1)。Menu.item_page_azukeru)。無ければ ""。</summary>
        public static string Description(int id)
        {
            string s = null;
            try { s = BattleLR.GetMenuMsg(7, id + 1); } catch { }
            return s ?? "";
        }

        public static bool IsUnstorable(int id)
        {
            for (int i = 0; i < UNSTORABLE.Length; i++) if (UNSTORABLE[i] == id) return true;
            return false;
        }

        /// <summary>
        /// 倉庫から持ち物へ 1 個 (本体の Menu.hikidasu_a と同じ: party_item_serch で入れ、入らなければ何もしない)。
        /// 戻り値は結果の文。ok = 動かせたか。
        /// </summary>
        public static string Withdraw(int id, out bool ok)
        {
            ok = false;
            if (!Ready) return "ゲーム中だけ使える";
            if (StoredCount(id) <= 0) return Name(id) + " は倉庫に無い";
            if (!PartyAdd(id)) return "持ち物がいっぱい (" + PARTY_SLOTS + " 種類まで、1 種類 " + STACK_MAX + " 個まで)";
            Work.partydata[STORAGE_BASE + id]--;
            RefreshItem240Flag();
            ok = true;
            Plugin.Log.LogInfo("[Storage] withdraw id " + id + " (" + Name(id) + ") stored " + StoredCount(id) + " held " + HeldCount(id));
            return Name(id) + " を 1 個引き出した (倉庫 " + StoredCount(id) + "、持ち物 " + HeldCount(id) + ")";
        }

        /// <summary>
        /// 持ち物から倉庫へ 1 個 (本体の Menu.azukeru_a と同じ: 倉庫が 99 なら断る、持ち物の個数が 0 になれば枠を詰める)。
        /// </summary>
        public static string Deposit(int id, out bool ok)
        {
            ok = false;
            if (!Ready) return "ゲーム中だけ使える";
            if (IsUnstorable(id)) return Name(id) + " は倉庫に預けられない (本体の倉庫で消える品物)";
            int slot = PartySlot(id);
            if (slot < 0 || Work.partydata[PARTY_BASE + slot * 2 + 1] <= 0) return Name(id) + " を持っていない";
            if (StoredCount(id) >= STACK_MAX) return "倉庫はその品物を " + STACK_MAX + " 個までしか預かれない";
            Work.partydata[STORAGE_BASE + id]++;
            Work.partydata[PARTY_BASE + slot * 2 + 1]--;
            if (Work.partydata[PARTY_BASE + slot * 2 + 1] <= 0) PartyRemoveSlot(slot);
            RefreshItem240Flag();
            ok = true;
            Plugin.Log.LogInfo("[Storage] deposit id " + id + " (" + Name(id) + ") stored " + StoredCount(id) + " held " + HeldCount(id));
            return Name(id) + " を 1 個預けた (倉庫 " + StoredCount(id) + "、持ち物 " + HeldCount(id) + ")";
        }

        /// <summary>
        /// 持ち物の枠を品物 ID の順 (= 倉庫の並び: 武器 → 防具 → 道具) に並べ替える。本体の「ならびかえ」(Menu.item_sort) は手で 1 つずつ動かすだけなので
        /// 自動の並べ替えは MOD 独自。枠の位置に本体側の意味は無い (item_sort が自由に動かしている) ので、詰めて書き戻すだけでよい。
        /// </summary>
        public static string SortParty(out bool ok)
        {
            ok = false;
            if (!Ready) return "ゲーム中だけ使える";
            List<int[]> list = new List<int[]>();
            for (int i = 0; i < PARTY_SLOTS; i++)
            {
                int id = Work.partydata[PARTY_BASE + i * 2], n = Work.partydata[PARTY_BASE + i * 2 + 1];
                if (id == NONE || n <= 0) continue;
                list.Add(new int[] { id, n });
            }
            // 安定な挿入ソート (同じ ID が 2 枠にあっても順は保つ)
            for (int i = 1; i < list.Count; i++)
            {
                int[] v = list[i]; int j = i - 1;
                while (j >= 0 && list[j][0] > v[0]) { list[j + 1] = list[j]; j--; }
                list[j + 1] = v;
            }
            for (int i = 0; i < PARTY_SLOTS; i++)
            {
                Work.partydata[PARTY_BASE + i * 2] = i < list.Count ? list[i][0] : NONE;
                Work.partydata[PARTY_BASE + i * 2 + 1] = i < list.Count ? list[i][1] : 0;
            }
            RefreshItem240Flag();
            ok = true;
            Plugin.Log.LogInfo("[Storage] sorted party items (" + list.Count + " kinds)");
            return "持ち物を倉庫と同じ順 (武器 → 防具 → 道具) に並べ替えた (" + list.Count + " 種類)";
        }

        private static int PartySlot(int id)
        {
            for (int i = 0; i < PARTY_SLOTS; i++)
            {
                int v = Work.partydata[PARTY_BASE + i * 2];
                if (v == id) return i;
                if (v == NONE) break;
            }
            return -1;
        }

        /// <summary>Menu.party_item_serch と同じ: 同じ ID の枠があれば +1 (99 なら失敗)、無ければ最初の空き枠に 1 個。</summary>
        private static bool PartyAdd(int id)
        {
            for (int i = 0; i < PARTY_SLOTS; i++)
            {
                int v = Work.partydata[PARTY_BASE + i * 2];
                if (v == id)
                {
                    if (Work.partydata[PARTY_BASE + i * 2 + 1] >= STACK_MAX) return false;
                    Work.partydata[PARTY_BASE + i * 2 + 1]++;
                    return true;
                }
                if (v == NONE)
                {
                    Work.partydata[PARTY_BASE + i * 2] = id;
                    Work.partydata[PARTY_BASE + i * 2 + 1] = 1;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Menu.item_work_pless と同じ: 枠 slot を消して後ろを 1 つずつ前に詰め、最後の枠を空にする。</summary>
        private static void PartyRemoveSlot(int slot)
        {
            for (int i = slot; i < PARTY_SLOTS - 1; i++)
            {
                Work.partydata[PARTY_BASE + i * 2] = Work.partydata[PARTY_BASE + i * 2 + 2];
                Work.partydata[PARTY_BASE + i * 2 + 1] = Work.partydata[PARTY_BASE + i * 2 + 3];
            }
            Work.partydata[PARTY_BASE + (PARTY_SLOTS - 1) * 2] = NONE;
            Work.partydata[PARTY_BASE + (PARTY_SLOTS - 1) * 2 + 1] = 0;
        }

        /// <summary>Menu.item_0f0h_chk と同じ: 持ち物に ID 240 があれば partydata[150] = 1、無ければ 0。</summary>
        private static void RefreshItem240Flag()
        {
            int f = 0;
            for (int i = 0; i < PARTY_SLOTS; i++)
            {
                int v = Work.partydata[PARTY_BASE + i * 2];
                if (v == NONE) break;
                if (v == 240) { f = 1; break; }
            }
            Work.partydata[FLAG_ITEM240] = f;
        }
    }
}
