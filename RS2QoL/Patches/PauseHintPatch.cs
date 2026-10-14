using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// フィールドのポーズ (Start) の窓に「どのボタンで MOD 設定画面を開けるか」を足す。
    ///
    /// 本体のポーズはメッセージ 0x809 → 0xB34 (script.bin): 窓 (x=40, y=40, 種類 15 = 152x64、10 文字 x 3 行、吹き出しの尾なし) を開き、
    /// 改行して「　　　ポーズ」を 2 行目に置き、命令 44 (pause_sa) で Start 待ち。1 行目と 3 行目は空いている。
    /// ここでは
    ///  - 先頭の改行 (kaigyo) を飛ばして「ポーズ」とカーソルを 1 行目に置かせ (最初から 1 行目なので一瞬 2 行目に見えることがない)、
    ///  - 窓を開いた時 (set_window) に案内の文言と位置を決め、「ポーズ」を書き終えた後 (pause_sa) に 3 行目へ本体と同じ速さ・同じ描き方
    ///    (Window の m_draw_text に 1 文字ずつ 14 ドット間隔。GS.DrawString で描かれる) で足す。窓が閉じる (close_wind) と消える。
    ///  - 開くボタンがスティック押し込み (L3 / R3) なら、「３」の升の代わりにスティックのドット絵 (GS.FillRect) を Window.draw の後に描く。
    /// 本体のフォント (GS.m_font[0]) は字が限られていて全角の Ｍ・Ｏ などが無いので、無い字がある文言は飛ばす。
    /// </summary>
    internal static class PauseHint
    {
        public static ConfigEntry<bool> Enabled;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Field", "PauseHint", true,
                "ポーズ (Start) の窓の 3 行目に、MOD 設定画面を開くボタン (例: 独自コンフィグ：Ｌ[スティックの絵]) を出す。「ポーズ」は 1 行目になる。ボタン割り当てに合わせて変わり、割り当てが無ければ F1");
        }

        public static bool On { get { return Enabled != null && Enabled.Value; } }

        /// <summary>
        /// 物理ボタンの短い表示名。順番は PadMap.PhysicalNames。
        /// 全角は本体の文字と同じ 14 ドットの升に 1 文字ずつ置く。本体のフォント (Fonts/UDTypos512) は全角英字が
        /// ＡＢＣＤＥＦＧＨＬＮＰＱＲＳＴＹ と小文字 ａｃｇｉｍｎｏ しか無い (Ｘ も無い) ので、書けない名前は半角 (A〜Z a〜z は全部ある) で、
        /// 本体の描き方どおり 1 つの文字列として詰めて並べる。
        /// </summary>
        private static readonly string[] ShortNames = { "Ａ", "Ｂ", "X", "Ｙ", "ＬＢ", "ＲＢ", "Start", "Select", "ＬＴ", "ＲＴ", "Ｌ３", "Ｒ３", "LB+RB" };
        private const int PHYS_L3 = 10, PHYS_R3 = 11;

        /// <summary>案内の前半の候補 (長い順。窓の幅とフォントの字の有無で選ぶ)。</summary>
        private static readonly string[] Prefixes = { "独自コンフィグ", "独自設定", "設定" };

        public const int CELL = 14;   // 本体の 1 文字の幅 (put_moji2)
        public const int ROW_Y = 32;  // 3 行目 (16 ドット行)
        private const char BLANK = '　';

        // 今出している案内のスティック絵の位置 (窓の文字座標。IconWhich < 0 なら無し)
        public static int IconWhich = -1;
        public static int IconX;

        private static string s_text;          // 案内の全文 (全角部分 + 半角部分)
        private static int s_halfStart;        // s_text の中で半角部分が始まる位置 (無ければ s_text.Length)
        private static int s_x, s_which, s_pos, s_cnt;
        private static bool s_iconAtEnd;
        private static Window.DrawText s_halfItem;   // 半角部分を 1 つにまとめて描く項目 (1 字ずつ伸ばす)

        /// <summary>今の割り当てで MOD 設定画面を開く最初のボタン。無ければ -1 (キーボードの F1)。</summary>
        private static int ButtonIndex()
        {
            if (PadMap.Entries == null) return -1;
            for (int i = 0; i < PadMap.Entries.Length && i < ShortNames.Length; i++)
            {
                if (PadMap.Entries[i].Value == PadFunction.ConfigMenu) return i;
            }
            return -1;
        }

        /// <summary>窓を開いた直後に案内の文言と位置を決める (出すのは Type)。</summary>
        public static void Put(Core c)
        {
            int which = c.wind_which;
            Window w = c.meswin[which];
            if (w == null || w.w <= 0 || c.w_c_size_y[which] < ROW_Y + 16) return;
            int width = c.w_c_size_x[which];
            int idx = ButtonIndex();
            string btn = idx >= 0 ? ShortNames[idx] : "Ｆ１";
            bool icon = idx == PHYS_L3 || idx == PHYS_R3;
            bool half = btn.Length > 0 && btn[0] < 0x80;   // 半角の名前 (X / Start / Select)
            Font font = (GS.m_font != null && GS.m_font.Length > 0) ? GS.m_font[0] : null;
            // スティック押し込みは「独自コンフィグ：Ｌ[絵]」(３ の升に絵)
            string label = icon ? btn.Substring(0, 1) + BLANK : btn;
            float labelDots = half ? HalfWidthDots(font, label) : label.Length * CELL;

            string prefix = null;
            for (int i = 0; i < Prefixes.Length; i++)
            {
                string p = Prefixes[i];
                if ((p.Length + 1) * CELL + labelDots > width) continue;
                if (font != null && !HasAllChars(font, p + "：" + btn)) continue;
                prefix = p;
                break;
            }
            string full;   // 全角部分 (升に 1 字ずつ)
            if (prefix == null)
            {
                if (font != null && !HasAllChars(font, btn)) return;
                full = half ? "" : label;
                icon = false;
            }
            else full = prefix + "：" + (half ? "" : label);
            string text = half ? full + label : full;
            int x = (int)((width - (full.Length * CELL + (half ? labelDots : 0f))) / 2f);
            if (x < 0) x = 0;
            s_text = text;
            s_halfStart = full.Length;
            s_x = x;
            s_which = which;
            s_pos = 0;
            s_cnt = 0;
            s_iconAtEnd = icon;
            s_halfItem = null;
        }

        /// <summary>
        /// pause_sa (Start 待ち。毎論理フレーム) から。本体の文字送り (message_lop2: 1 文字描いて mess_cnt の bit1 が立っていれば同じフレームで続ける)
        /// と同じリズムで案内を 1 文字ずつ足す。最後の升が絵なら、そこまで来た時に絵を出す。
        /// </summary>
        public static void Type(Core c)
        {
            if (s_text == null || s_pos >= s_text.Length) return;
            Window w = c.meswin[s_which];
            if (w == null || w.w <= 0) { s_text = null; return; }
            bool more;
            do
            {
                char ch = s_text[s_pos];
                // 先頭に入れる (末尾に足すと、本体が最後の文字の右に置く手のカーソル (Window.draw の m_SSCursorX) が案内に付いて動く)
                if (s_pos >= s_halfStart)
                {
                    // 半角部分: 1 つの項目にまとめ、1 字ずつ伸ばす (本体の DrawString が字ごとの幅で詰めて並べる)
                    if (s_halfItem == null)
                    {
                        s_halfItem = new Window.DrawText(s_x + s_halfStart * CELL, ROW_Y, ch.ToString(), 3);
                        w.m_draw_text.Insert(0, s_halfItem);
                    }
                    else s_halfItem.m_str += ch;
                }
                else if (ch != BLANK) w.m_draw_text.Insert(0, new Window.DrawText(s_x + s_pos * CELL, ROW_Y, ch.ToString(), 3));
                s_pos++;
                more = (s_cnt & 2) != 0;
                s_cnt++;
            }
            while (more && s_pos < s_text.Length);
            if (s_pos >= s_text.Length && s_iconAtEnd)
            {
                IconWhich = s_which;
                IconX = s_x + (s_text.Length - 1) * CELL;
            }
        }

        /// <summary>窓を開き直した (set_window) 時: 途中の案内を捨て、絵も消す。</summary>
        public static void Reset()
        {
            s_text = null;
            IconWhich = -1;
            s_halfItem = null;
        }

        /// <summary>半角の文字列を本体の描き方 (GS.DrawString: 1 字ごとに info.width * 字の倍率 - 1 だけ進む) で並べた時の幅 (本体のドット)。</summary>
        private static float HalfWidthDots(Font font, string s)
        {
            float fontScale = (font != null && font.fontSize > 30) ? 30f / font.fontSize : 1f;
            float virt = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                CharacterInfo info;
                if (font != null && font.GetCharacterInfo(s[i], out info)) virt += info.width * fontScale - 1f;
                else virt += CELL * DOT / 2f;
            }
            return virt / DOT;
        }

        private static bool HasAllChars(Font font, string t)
        {
            for (int i = 0; i < t.Length; i++)
            {
                if (!font.HasCharacter(t[i]))
                {
                    Plugin.Log.LogInfo("[PauseHint] font lacks '" + t[i] + "' → skip \"" + t + "\"");
                    return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- スティック押し込みのドット絵 (24x22 升。1 升 = 本体の半ドット)
        // 本体の文字はドット絵ではなく、トゥルータイプのフォントを拡大した滑らかな字 (漢字 1 字で縦 11 ドットほど) なので、
        // 絵は半ドット (1080p で 2 ピクセル) の升で描いて、文字と並べても粗く見えないようにする。
        // 22 段 = 本体の 11 ドット = 漢字とほぼ同じ高さ。横は升 (14 ドット = 28 升) に収まる 24 升まで。
        // 上から: 押し込みの下向き矢印、空き、キノコ形の頭、軸 (台は描かない)。文字と同じ色 (Window.m_msg_color0 = 51,0,51)。

        private static readonly string[] IconRows =
        {
            "..........####..........",
            "..........####..........",
            "..........####..........",
            "......############......",
            "......############......",
            "........########........",
            "........########........",
            "..........####..........",
            "..........####..........",
            "........................",
            "........................",
            "........########........",
            "........########........",
            "....################....",
            "....################....",
            "..####################..",
            "..####################..",
            ".........######.........",
            "..........####..........",
            "..........####..........",
            "..........####..........",
            "..........####..........",
        };
        private const int ICON_W = 24, ICON_H = 22;
        private const float DOT = 36f / 16f;                 // 本体の 1 ドット = 仮想 960x540 の 2.25
        private const float ICON_DOT = DOT / 2f;             // 絵の 1 升 = 半ドット
        private const float ICON_CENTER_Y = ROW_Y + 8f;      // 絵の縦の中心 (3 行目の漢字の中心。漢字は行の上から約 2.5〜13.3 ドット)
        private static readonly Color32 s_iconColor = new Color32(51, 0, 51, 255);

        /// <summary>
        /// Window.draw の後に、案内の最後の升にスティックの絵を描く (文字と同じ優先度の 1 つ上)。
        /// 本体の 1 ドットは画面では半端な大きさ (1080p で 4.5 ピクセル) なので、段ごとにそのまま矩形を置くと太さの違う段が混ざり
        /// 太く見える段ができる。絵は画面のピクセルに揃え、全部の升を同じ大きさ (半ドット分を四捨五入した整数ピクセル) で描く。
        /// </summary>
        public static void DrawIcon(Window w)
        {
            if (IconWhich < 0 || !On) return;
            Core c = Main.core;
            if (c == null || !FieldPause.IsPaused(c) || w != c.meswin[IconWhich] || w.w <= 0) return;
            int pri = (GS.m_font_mtl_w != null && GS.m_font_mtl_w.Length > 0 && GS.m_font_mtl_w[0] != null) ? GS.m_font_mtl_w[0].renderQueue + 1 : 3000;
            // 仮想 960x540 の 1 単位 = 画面の何ピクセルか (カメラは orthographicSize 270)
            Camera cam = Camera.main;
            float scale = (cam != null ? cam.pixelHeight : Screen.height) / 540f;
            if (scale <= 0f) scale = 1f;
            // 1 升のピクセル数は四捨五入 (1600x900 なら半ドット = 1.875 ピクセル → 2。切り捨てだと 1 ピクセルになり絵が半分に縮む)。
            // ただし絵が文字の升 (縦 14 ドット) からはみ出すなら 1 つ小さくする
            int dotPx = Mathf.Max(1, Mathf.RoundToInt(ICON_DOT * scale));
            if (dotPx > 1 && dotPx * ICON_H > CELL * DOT * scale) dotPx--;
            float dot = dotPx / scale;
            // 文字の升 (14 ドット幅) の中央に、縦は 3 行目の漢字の中心に揃える (どちらも画面のピクセルに丸める)
            float cx = Sys.get_screen_xf(IconX + CELL / 2f + w.x + w.mx);
            float cy = Sys.get_screen_yf(ICON_CENTER_Y + w.y + w.my);
            float sx = Mathf.Round(cx * scale - dotPx * ICON_W / 2f) / scale;
            float sy = Mathf.Round(cy * scale - dotPx * ICON_H / 2f) / scale;
            for (int r = 0; r < ICON_H; r++)
            {
                string row = IconRows[r];
                int col = 0;
                while (col < ICON_W)
                {
                    if (row[col] != '#') { col++; continue; }
                    int start = col;
                    while (col < ICON_W && row[col] == '#') col++;
                    GS.FillRect(sx + dot * start, sy + dot * r, pri, dot * (col - start), dot, s_iconColor);
                }
            }
        }
    }

    /// <summary>ポーズのメッセージ先頭の改行を飛ばし、「ポーズ」を 1 行目に置かせる。元処理 (改行フラグを立てる) を止めるので Prefix。</summary>
    [HarmonyPatch(typeof(Core), nameof(Core.kaigyo))]
    internal static class PauseHintNoFirstNewlinePatch
    {
        private static bool Prefix(Core __instance)
        {
            Core c = __instance;
            if (!PauseHint.On || !FieldPause.IsPaused(c)) return true;
            int which = c.wind_which;
            if (c.cursor_x[which] != 0 || c.cursor_y[which] != 0) return true;
            c.increment_adrs();
            return false;
        }
    }

    /// <summary>窓を開いた時 (m_draw_text が消えた直後) に案内の文言を決める。</summary>
    [HarmonyPatch(typeof(Core), nameof(Core.set_window))]
    internal static class PauseHintPutPatch
    {
        private static void Postfix(Core __instance)
        {
            PauseHint.Reset();
            if (PauseHint.On && FieldPause.IsPaused(__instance)) PauseHint.Put(__instance);
        }
    }

    /// <summary>Start 待ちの間 (「ポーズ」を書き終えた後)、案内を本体と同じ速さで 1 文字ずつ出す。</summary>
    [HarmonyPatch(typeof(Core), nameof(Core.pause_sa))]
    internal static class PauseHintTypePatch
    {
        private static void Prefix(Core __instance)
        {
            if (PauseHint.On && FieldPause.IsPaused(__instance)) PauseHint.Type(__instance);
        }
    }

    [HarmonyPatch(typeof(Window), nameof(Window.draw))]
    internal static class PauseHintIconPatch
    {
        private static void Postfix(Window __instance)
        {
            PauseHint.DrawIcon(__instance);
        }
    }
}
