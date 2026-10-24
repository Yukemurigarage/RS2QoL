using System.Collections.Generic;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// どこでもアバロンの窓の「どこでも倉庫」ページ (TreasuryScreen の一部)。倉庫 (預かり所) の品物をその場で引き出す / 持ち物を預ける。
    /// 引き出す (PAGE_STORAGE) と 預ける (PAGE_DEPOSIT) は別のページで、LB / RB で行き来する (ユーザー要望 2026-10-24: 左右の切り替えではなくページで)。
    /// 上下で品物、決定で 1 個動かす。中身の読み書きは ItemStorage (本体の Menu の手順をそのまま)。
    /// </summary>
    internal static partial class TreasuryScreen
    {
        private static List<ItemStorage.Entry> s_stList = new List<ItemStorage.Entry>();
        private static int s_stCursor, s_stTop;
        private static GUIStyle s_descFit, s_stName, s_stNameSel, s_stMark;
        private static string s_descFitKey;
        /// <summary>品物の行は本文の行より高くし (1.2 倍)、名前の文字 (+3) とアイコンを大きく見せる (ユーザー要望 2026-10-24: 本体の倉庫画面に近く)。</summary>
        private const float ITEM_ROW = 1.2f;
        private const int ITEM_FONT_PLUS = 3;
        /// <summary>続きの印 ▲ ▼ の行の高さ (本文の行に対する倍率)。低くして品物を 1 つ多く見せる。</summary>
        private const float MARK_ROW = 0.6f;

        private static bool IsStoragePage { get { return s_page == PAGE_STORAGE || s_page == PAGE_DEPOSIT; } }
        private static bool StorageDeposit { get { return s_page == PAGE_DEPOSIT; } }

        /// <summary>一覧を作り直す (ページを開いた時・動かした後)。カーソルは範囲に収める。</summary>
        private static void StorageRefresh()
        {
            s_stList = StorageDeposit ? ItemStorage.HeldList() : ItemStorage.StoredList();
            int min = StorageMinCursor;
            if (s_stCursor >= s_stList.Count) s_stCursor = Mathf.Max(min, s_stList.Count - 1);
            if (s_stCursor < min) s_stCursor = min;
            if (s_stTop > s_stCursor) s_stTop = Mathf.Max(0, s_stCursor);
        }

        /// <summary>預けるページには「並べ替え」の行 (カーソル −1) がある。画面では とじる の行の左 (一覧の下) に置く。</summary>
        private static int StorageMinCursor { get { return StorageDeposit ? -1 : 0; } }

        /// <summary>上下: 品物 0 .. n−1 → (預けるページなら 並べ替え −1) → 先頭 へ輪になる。</summary>
        private static void StorageDir(int dir)
        {
            if ((dir & DIR_UP) == 0 && (dir & DIR_DOWN) == 0) return;   // 左右は使わない (ページは LB / RB)
            bool sortRow = StorageDeposit;
            int n = s_stList.Count;
            if (n == 0 && !sortRow) return;
            int c = s_stCursor;
            if ((dir & DIR_DOWN) != 0)
                c = c == -1 ? (n > 0 ? 0 : -1) : (c >= n - 1 ? (sortRow ? -1 : 0) : c + 1);
            else
                c = c == -1 ? n - 1 : (c <= 0 ? (sortRow ? -1 : n - 1) : c - 1);
            if (c < StorageMinCursor) c = StorageMinCursor;
            s_stCursor = c;
            UiParts.SeCursor();
        }

        /// <summary>
        /// 預けるページ: とじる の行の左に「並べ替え」(本体の「ならびかえ」は手で 1 つずつ動かすだけなので、自動の並べ替えは MOD 独自)。
        /// 一覧の上に置くと 引き出す ⇄ 預ける で品物の位置がずれるので、ここ (ユーザー要望 2026-10-24)。
        /// </summary>
        private static void DrawStorageSortRow(float cx, float curW, float textX, Rect btnR, float lineH, int fontSize)
        {
            bool sel = s_stCursor == -1;
            Rect r = new Rect(textX, btnR.y, btnR.x - fontSize - textX, btnR.height);
            if (sel) DrawHand(cx + curW, btnR.y + btnR.height / 2f, lineH);
            UiParts.Label(r, "持ち物を倉庫と同じ順に並べ替える", sel ? s_lineSel : s_line);
            if (UiParts.ClickArea(r)) { s_stCursor = -1; StorageActivate(); }
        }

        /// <summary>決定: カーソルの品物を 1 個動かす (引き出すページ: 倉庫 → 持ち物、預けるページ: 持ち物 → 倉庫)。預けるページの −1 は持ち物の並べ替え。</summary>
        private static void StorageActivate()
        {
            if (StorageDeposit && s_stCursor == -1)
            {
                bool sorted;
                s_status = ItemStorage.SortParty(out sorted);
                if (sorted) UiParts.SeOk(); else UiParts.SeCancel();
                StorageRefresh();
                return;
            }
            if (s_stCursor < 0 || s_stCursor >= s_stList.Count) { UiParts.SeCancel(); return; }
            int id = s_stList[s_stCursor].Id;
            bool ok;
            s_status = StorageDeposit ? ItemStorage.Deposit(id, out ok) : ItemStorage.Withdraw(id, out ok);
            if (ok) UiParts.SeOk(); else UiParts.SeCancel();
            StorageRefresh();
        }

        /// <summary>
        /// 倉庫のページ: 1 行目 = 列の見出し、以下 品物の一覧 (bodyH に入る行数だけ。上下に続きがあれば ▲ ▼)。戻り値は本文の領域の下端。
        /// </summary>
        private static float DrawStoragePage(float cx, float cy, float cw, float lineH, int fontSize, float bodyH)
        {
            float bottom = cy + bodyH;             // ページの高さは他のページと同じ (窓が上下しないように)
            float curW = lineH * UiParts.CursorAspect;
            float textX = cx + curW + fontSize * 0.3f;
            float textW = cw - (textX - cx);
            float colW = fontSize * 4.5f;
            float x2 = cx + cw - colW, x1 = x2 - colW;
            bool deposit = StorageDeposit;

            // 列の見出し (預けるページの「並べ替え」は とじる の行の左: DrawStorageSortRow)
            UiParts.Label(new Rect(textX, cy, x1 - textX, lineH), (deposit ? "持ち物 (" : "倉庫の品物 (") + s_stList.Count + " 種類)", s_lineDim);
            UiParts.Label(new Rect(x1, cy, colW, lineH), "倉庫", s_rightDim);
            UiParts.Label(new Rect(x2, cy, colW, lineH), "持ち物", s_rightDim);
            cy += lineH;

            // 一番下の 2 行はカーソルの品物の説明 (本体の倉庫画面の下の文と同じ。ユーザー要望 2026-10-24)。上揃えで折り返す
            // (中央揃えだと 2 行になった時に上の ▼ の行に重なった)
            float descY = bottom - 2f * lineH;
            int rows = Mathf.Max(1, Mathf.FloorToInt((descY - cy) / lineH));
            if (s_stList.Count == 0)
            {
                UiParts.Label(new Rect(textX, cy, textW, lineH), deposit ? "持ち物がありません (装備している物は預けられません)" : "倉庫は空です", s_lineDim);
                return bottom;
            }
            if (s_descFit == null || s_descFitKey != s_lineDim.fontSize.ToString())
            {
                s_descFit = new GUIStyle(s_lineDim) { wordWrap = true, alignment = TextAnchor.UpperLeft };
                s_stName = new GUIStyle(s_line) { fontSize = s_line.fontSize + ITEM_FONT_PLUS };
                s_stNameSel = new GUIStyle(s_lineSel) { fontSize = s_lineSel.fontSize + ITEM_FONT_PLUS };
                s_stMark = new GUIStyle(s_lineDim) { fontSize = Mathf.Max(10, s_lineDim.fontSize - 6) };
                s_descFitKey = s_lineDim.fontSize.ToString();
            }
            // 品物の行は本文より高い (名前とアイコンを大きく)。アイコンは行いっぱいの正方形、名前はその右
            float rowH = Mathf.Round(lineH * ITEM_ROW);
            float iconS = Mathf.Round(rowH * 0.95f);
            float nameX = textX + iconS + fontSize * 0.4f;
            if (s_stCursor >= 0 && s_stCursor < s_stList.Count)
            {
                // 説明の左端は品物の名前の列に揃える (ユーザー要望 2026-10-24)
                string desc = ItemStorage.Description(s_stList[s_stCursor].Id);
                if (desc.Length > 0) UiParts.Label(new Rect(nameX, descY, cx + cw - nameX, 2f * lineH), desc, s_descFit);
            }
            // 入り切らない時は続きの印 ▲ ▼ に上下 1 行ずつ使う (品物の行には混ぜない。ユーザー要望 2026-10-24)。印の行は低く (MARK_ROW) して品物を 1 つ多く見せる
            float markH = Mathf.Round(lineH * MARK_ROW);
            float avail = descY - cy;
            rows = Mathf.Max(1, Mathf.FloorToInt(avail / rowH));
            bool scroll = s_stList.Count > rows;
            int visible = scroll ? Mathf.Max(1, Mathf.FloorToInt((avail - 2f * markH) / rowH)) : rows;
            // カーソルが見える位置まで送る (−1 = 並べ替えの行の時は先頭を見せる。先頭は 0 未満にしない: 一覧の添字に使う)
            int cur = Mathf.Max(0, s_stCursor);
            if (cur < s_stTop) s_stTop = cur;
            if (cur >= s_stTop + visible) s_stTop = cur - visible + 1;
            if (s_stTop > s_stList.Count - visible) s_stTop = Mathf.Max(0, s_stList.Count - visible);
            if (s_stTop < 0) s_stTop = 0;
            int last = Mathf.Min(s_stList.Count, s_stTop + visible);
            if (scroll)
            {
                if (s_stTop > 0) UiParts.Label(new Rect(nameX, cy, textW, markH), "▲", s_stMark);
                cy += markH;
            }
            for (int i = s_stTop; i < last; i++)
            {
                ItemStorage.Entry e = s_stList[i];
                bool sel = i == s_stCursor;
                Rect r = new Rect(textX, cy, textW, rowH);
                if (sel) DrawHand(cx + curW, cy + rowH / 2f, lineH);
                // 名前の左に本体と同じ品物のアイコン (ユーザー要望 2026-10-24: 本体の倉庫画面のように、もう少し大きく)
                UiParts.DrawItemIcon(new Rect(textX, cy + Mathf.Round((rowH - iconS) / 2f), iconS, iconS), e.Id);
                UiParts.Label(new Rect(nameX, cy, x1 - nameX, rowH), ItemStorage.Name(e.Id), sel ? s_stNameSel : s_stName);
                UiParts.Label(new Rect(x1, cy, colW, rowH), e.Stored.ToString(), sel ? s_rightSel : (deposit ? s_rightDim : s_right));
                UiParts.Label(new Rect(x2, cy, colW, rowH), e.Held.ToString(), sel ? s_rightSel : (deposit ? s_right : s_rightDim));
                if (UiParts.ClickArea(r)) { s_stCursor = i; StorageActivate(); }
                cy += rowH;
            }
            if (scroll && last < s_stList.Count) UiParts.Label(new Rect(nameX, cy, textW, markH), "▼", s_stMark);
            return bottom;
        }
    }
}
