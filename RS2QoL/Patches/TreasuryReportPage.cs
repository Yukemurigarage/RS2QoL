using System.Collections.Generic;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// どこでもアバロンの窓の 1 ページ目「どこでもアバロン」(TreasuryScreen の一部) = 皇帝への報告書。今のアバロンの状態を一枚にまとめて読む
    /// (ユーザー要望 2026-10-24「報告物はまとめて提出するもの」)。読むだけで、操作は後ろのページ (金庫番 / 体重計 / 理力計 / 倉庫)。
    ///
    /// 上 4 行 = 数字の報告 (2 列):
    ///   国庫 (Wallet.Treasury) / 戦闘回数 (Work.partydata[686] = 情報官 msg 3072 の APPNEW 0)
    ///   所持金 (Wallet.Money) / 倉庫 (倉庫にある種類と持ち物の種類。ItemStorage)
    ///   税収 (1 戦あたり。TaxIncome.VanillaIncomePerBattle × 倍率) / 開発所 (flag115 bit 4 = 武器、8 = 防具を開発中。new_flag 56 = 完成までの戦闘数。
    ///     弔いの鍛冶がたまっていれば回数も。dev README 弔いの鍛冶の項)
    ///   施設: 術法研究所 (new_flag 48) / 大学 (49) / 新市街 (50) / アバロンの木 (55)。0 = 無し、1 = 完成 (情報官が未報告)、2 = 完成 (報告済み。msg 3210〜3212 / 3208)
    /// 下 = 情報官 ((42,27) msg 82) の今の言葉を仲間の窓と同じ吹き出し (尾は上の大臣へ) に本体のフォントで。
    ///   ScriptReader で読むだけなので、本体が話す時の書き込み (開発の知らせを「聞いた」にする new_flag → 2、武装商船団の flag12 |= 2) はしない
    ///   (ユーザー決定 2026-10-24)。開発の知らせは情報室で本人に聞くまで何度でも出る。最終決戦の前 (flag190 == 14) は msg 1253 (イベント) に飛ぶので読まない。
    ///   段落 (本体の PAUSE) の間は半行空ける。入らない分は上下で 1 行ずつ送る (右端に ▲ ▼)。決定・キャンセルで閉じる。
    /// </summary>
    internal static partial class TreasuryScreen
    {
        private const int INFO_MSG_HINTS = 82;
        private const int REPORT_ROWS = 4;                 // 数字の報告の行数
        private static List<string> s_infoParas = new List<string>();
        private static List<string> s_infoLines;          // 吹き出しの幅で折り返した行 (段落の間は "")
        private static float s_infoLinesW = -1f;
        private static int s_infoLinesFont = -1;
        private static int s_infoTop, s_infoMaxTop;
        private static GUIStyle s_infoSpeech;
        private static int s_infoSpeechFont = -1;
        private static Texture2D s_infoBubble;
        private static int s_infoBubbleW, s_infoBubbleH, s_infoBubbleTail;
        private static string s_repTreasury = "", s_repMoney = "", s_repTax = "", s_repBattles = "", s_repStorage = "", s_repDev = "", s_repFacilities = "";

        /// <summary>報告を組み立て直す (開いた時・ページを開いた時)。</summary>
        private static void ReportRefresh()
        {
            s_infoParas.Clear();
            s_infoLines = null;
            s_infoTop = 0;
            if (Work.partydata == null) return;
            s_repTreasury = Wallet.Treasury.ToString("N0") + " クラウン";
            s_repMoney = Wallet.Money.ToString("N0") + " クラウン";
            s_repTax = "1 戦 " + TaxIncome.Scale(TaxIncome.VanillaIncomePerBattle()).ToString("N0") + " クラウン";
            s_repBattles = ScriptReader.BattleCount.ToString("N0") + " 回";
            s_repStorage = ItemStorage.StoredList().Count + " 種類";   // 持ち物の「N / 32」は分かりにくいので出さない (ユーザー 2026-10-24)
            int dev = ScriptReader.Flag(115), left = ScriptReader.NewFlag(56);
            string kind = (dev & 4) != 0 ? "武器" : (dev & 8) != 0 ? "防具" : null;
            // 残り 0 = 仕上がっている (開発所の script msg 2068 / 2069 は new_flag 56 == 0 で渡す。「まもなく完成」と出していたのは誤り、ユーザー 2026-10-24)
            s_repDev = kind == null ? "開発なし" : left > 0 ? kind + "を開発中 (あと " + left + " 戦)" : kind + "が仕上がっています (開発所へ)";
            int stock = 0;
            try { stock = MourningForge.Stock; } catch { }
            if (stock > 0) s_repDev += "・弔い " + stock + " 回";
            // 完成した施設だけ名前を並べる (ユーザー要望 2026-10-24: ○ / － で先を見せない)
            s_repFacilities = "";
            if (ScriptReader.NewFlag(48) >= 1) s_repFacilities += "術法研究所  ";
            if (ScriptReader.NewFlag(49) >= 1) s_repFacilities += "大学  ";
            if (ScriptReader.NewFlag(50) >= 1) s_repFacilities += "新市街  ";
            if (ScriptReader.NewFlag(55) >= 1) s_repFacilities += "アバロンの木  ";
            s_repFacilities = s_repFacilities.Length == 0 ? "まだありません" : s_repFacilities.TrimEnd();
            if (ScriptReader.Flag(190) == 14)
            {
                s_infoParas.Add("(最終決戦の前なので、情報官は情報を話しません)");
                return;
            }
            string stopped;
            List<string> paras = ScriptReader.Read(INFO_MSG_HINTS, out stopped);
            if (paras == null) { s_infoParas.Add("(スクリプトが読めませんでした)"); return; }
            for (int i = 0; i < paras.Count; i++) s_infoParas.Add(paras[i].Replace("\n", ""));   // 本体の窓の幅の改行は外し、吹き出しの幅で折り返す
            if (stopped != null) s_infoParas.Add("(ここから先は読めない命令: " + stopped + ")");
            Plugin.Log.LogInfo("[Report] applied: " + s_infoParas.Count + " paragraphs (battles " + ScriptReader.BattleCount + ", dev flag115 " + dev + " left " + left
                + ", facilities " + ScriptReader.NewFlag(48) + "/" + ScriptReader.NewFlag(49) + "/" + ScriptReader.NewFlag(50) + "/" + ScriptReader.NewFlag(55) + ", flag130 " + ScriptReader.Flag(130) + ")");
        }

        private static void ReportDir(int dir)
        {
            if ((dir & DIR_UP) != 0 && s_infoTop > 0) { s_infoTop--; UiParts.SeCursor(); }
            else if ((dir & DIR_DOWN) != 0 && s_infoTop < s_infoMaxTop) { s_infoTop++; UiParts.SeCursor(); }
        }

        private static void EnsureInfoStyles(int fontSize)
        {
            if (s_infoSpeech != null && s_infoSpeechFont == fontSize) return;
            s_infoSpeech = new GUIStyle(s_line);
            s_infoSpeech.fontSize = Mathf.Max(10, fontSize - 2);                           // 仲間の窓の台詞と同じ: 一回り小さく
            s_infoSpeech.normal.textColor = new Color(60f / 255f, 49f / 255f, 39f / 255f);   // 本体の会話窓の文字の焦げ茶
            s_infoSpeech.alignment = TextAnchor.MiddleLeft;
            s_infoSpeechFont = fontSize;
        }

        private const string INFO_NO_LINE_HEAD = "、。」』！？…ー)";   // 行頭に来させない文字 (仲間の窓の WrapSpeech と同じ考え)

        /// <summary>段落を吹き出しの幅 maxW で折り返した行の一覧 (段落の間は "")。幅か字の大きさが変わった時だけ作り直す。</summary>
        private static List<string> InfoLines(float maxW)
        {
            if (s_infoLines != null && s_infoLinesFont == s_infoSpeechFont && Mathf.Abs(s_infoLinesW - maxW) < 0.5f) return s_infoLines;
            List<string> lines = new List<string>();
            for (int p = 0; p < s_infoParas.Count; p++)
            {
                if (p > 0) lines.Add("");
                string s = s_infoParas[p];
                if (s.Length == 0 || UiParts.GameTextWidth(s, s_infoSpeech) <= maxW) { lines.Add(s); continue; }
                int start = 0;
                while (start < s.Length)
                {
                    int n = 1;
                    while (start + n < s.Length && UiParts.GameTextWidth(s.Substring(start, n + 1), s_infoSpeech) <= maxW) n++;
                    if (start + n < s.Length && INFO_NO_LINE_HEAD.IndexOf(s[start + n]) >= 0) n++;
                    lines.Add(s.Substring(start, n));
                    start += n;
                }
            }
            s_infoLines = lines;
            s_infoLinesW = maxW;
            s_infoLinesFont = s_infoSpeechFont;
            return lines;
        }

        /// <summary>
        /// 報告書のページ: 上 REPORT_ROWS 行に数字の報告 (2 列)、残りいっぱいに情報官の吹き出し (尾は上の大臣へ)。戻り値は本文の領域の下端。
        /// </summary>
        private static float DrawReportPage(float cx, float cy, float cw, float lineH, int fontSize, float bodyH)
        {
            float bottom = cy + bodyH;
            float colW = Mathf.Round(cw / 2f), labelW = fontSize * 4.2f, labelW2 = fontSize * 6f;   // 右列の見出し (戦闘回数・開発所) は 4 文字なので広く (ユーザー要望 2026-10-24: 余白を開ける)
            // 上 = 情報官の吹き出し (大臣のすぐ下に尾を向けて)、下 = 数字の報告 REPORT_ROWS 行 (ユーザー要望 2026-10-24: 吹き出しが上)
            // 玉座のヒント ([Avalon] ThroneHint) が On なら 1 行増える (座ると何かが起こるか。ThroneHint.RowText)
            string throne = ThroneHint.RowText();
            float statsH = (REPORT_ROWS + (throne != null ? 1 : 0)) * lineH;
            float bubbleBottom = bottom - statsH - Mathf.Round(lineH * 0.3f);

            // 情報官の吹き出し。仲間の窓と同じ格子・余白
            EnsureInfoStyles(fontSize);
            int cell = Mathf.Max(1, Mathf.RoundToInt(UiParts.Unit * 1.125f));
            int tail = UiParts.BUBBLE_TAIL;
            int cellsW = Mathf.FloorToInt(cw / cell), cellsH = Mathf.FloorToInt((bubbleBottom - cy) / cell);
            float sprCx = cx + MinisterDot * FRAME / 2f;                          // 見出しの大臣の絵の中心 x (Draw と同じ置き方: 左端 = cx)
            int tailCell = Mathf.RoundToInt((sprCx - cx) / cell);
            if (s_infoBubble == null || s_infoBubbleW != cellsW || s_infoBubbleH != cellsH || s_infoBubbleTail != tailCell)
            {
                if (s_infoBubble != null) Object.Destroy(s_infoBubble);
                s_infoBubble = UiParts.MakeBubbleTexture(cellsW, cellsH, tail, tailCell, true);
                s_infoBubbleW = cellsW; s_infoBubbleH = cellsH; s_infoBubbleTail = tailCell;
            }
            Rect br = new Rect(cx, cy, cellsW * cell, cellsH * cell);
            if (s_infoBubble != null)
            {
                Color col = GUI.color; GUI.color = Color.white;
                GUI.DrawTexture(br, s_infoBubble, ScaleMode.StretchToFill, true);
                GUI.color = col;
            }
            // 文字: 尾の下の地の中。左右 0.8 行、上下 0.45 行の余白。本体のフォントは枠の中で上寄りに出るので 0.5 行。段落の間 ("") は半行
            float speechLineH = Mathf.Round(lineH * 0.82f), gapH = Mathf.Round(speechLineH * 0.5f);
            float textX = cx + lineH * 0.8f, textW = cw - lineH * 1.6f;
            float top = cy + tail * cell;
            float ty = top + Mathf.Round(lineH * (UiParts.GameFont() != null ? 0.5f : 0.35f));
            float avail = br.height - tail * cell - lineH * 0.9f;
            List<string> lines = InfoLines(textW);
            // 一番下まで入る先頭の行 (後ろから足して avail に収まる限り戻る)
            s_infoMaxTop = lines.Count - 1;
            for (float acc = 0f; s_infoMaxTop >= 0; s_infoMaxTop--)
            {
                acc += lines[s_infoMaxTop].Length == 0 ? gapH : speechLineH;
                if (acc > avail) break;
            }
            s_infoMaxTop = Mathf.Clamp(s_infoMaxTop + 1, 0, Mathf.Max(0, lines.Count - 1));
            if (s_infoTop > s_infoMaxTop) s_infoTop = s_infoMaxTop;
            int i = s_infoTop;
            for (float y = 0f; i < lines.Count; i++)
            {
                float h = lines[i].Length == 0 ? gapH : speechLineH;
                if (y + h > avail + 0.5f) break;
                if (lines[i].Length > 0) UiParts.GameLabel(new Rect(textX, ty + y, textW, speechLineH), lines[i], s_infoSpeech);
                y += h;
            }
            // 続きの印 (吹き出しの右端。OS フォントの ▲ ▼)
            float markW = lineH * 0.8f;
            if (s_infoTop > 0) UiParts.Label(new Rect(cx + cw - markW, top, markW, lineH * 0.8f), "▲", s_lineDim);
            if (i < lines.Count) UiParts.Label(new Rect(cx + cw - markW, br.yMax - lineH * 0.8f, markW, lineH * 0.8f), "▼", s_lineDim);

            // 数字の報告 (2 列 × 3 行 + 施設の 1 行)。見出しは薄く、値はその右
            float sy = bottom - statsH;
            DrawReportCell(cx, sy, colW, labelW, lineH, "国庫", s_repTreasury);
            DrawReportCell(cx + colW, sy, cw - colW, labelW2, lineH, "戦闘回数", s_repBattles);
            sy += lineH;
            DrawReportCell(cx, sy, colW, labelW, lineH, "所持金", s_repMoney);
            DrawReportCell(cx + colW, sy, cw - colW, labelW2, lineH, "倉庫", s_repStorage);
            sy += lineH;
            DrawReportCell(cx, sy, colW, labelW, lineH, "税収", s_repTax);
            DrawReportCell(cx + colW, sy, cw - colW, labelW2, lineH, "開発所", s_repDev);
            sy += lineH;
            DrawReportCell(cx, sy, cw, labelW, lineH, "施設", s_repFacilities);
            if (throne != null)
            {
                sy += lineH;
                DrawReportCell(cx, sy, cw, labelW, lineH, ThroneHint.ROW_LABEL, throne);
            }
            return bottom;
        }

        private static void DrawReportCell(float x, float y, float w, float labelW, float lineH, string label, string value)
        {
            UiParts.Label(new Rect(x, y, labelW, lineH), label, s_lineDim);
            UiParts.Label(new Rect(x + labelW, y, w - labelW, lineH), value, s_line);
        }
    }
}
