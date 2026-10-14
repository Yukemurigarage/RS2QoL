using System;
using System.Collections.Generic;
using UnityEngine;

namespace RS2QoL
{
    /// <summary>
    /// ゲーム本体の UI 部品 (ウィンドウ枠・手のカーソル・L / R の矢印・とじる ボタン) を、起動中のゲームのアーカイブから
    /// 実行時に読んで IMGUI で描く。画像はこの MOD に同梱しない (インストール済みのゲームにあるものを読むだけ)。
    /// 読めなければ各 Draw は false を返し、呼び出し側は文字や単色で代用する。
    ///
    /// 枠は Esc の終了ダイアログと同じ CBattleWindow の 9 枚 (battle_window/btlwdw_*.png、128px を 0.25 倍 = 仮想画面 960x540 で 32px)。
    /// カーソルは本体メニューの手 (menu/btn/cursor_00.png)。矢印は Util.LoadTexture が返すもの (Steam 版は起動時に
    /// 登録された L / R 文字入り。持ち物画面などのページ送りと同じ見た目)。
    /// </summary>
    internal static class UiParts
    {
        // CBattleWindow.m_FileName と同じ並び: 左上, 上, 右上, 左, 中, 右, 左下, 下, 右下
        private static readonly string[] WindowFiles =
        {
            "battle_window/btlwdw_08.png", "battle_window/btlwdw_01.png", "battle_window/btlwdw_02.png",
            "battle_window/btlwdw_07.png", "battle_window/btlwdw_00.png", "battle_window/btlwdw_03.png",
            "battle_window/btlwdw_06.png", "battle_window/btlwdw_05.png", "battle_window/btlwdw_04.png",
        };
        private const float WINDOW_TILE = 32f;      // 仮想画面 (960x540) での枠 1 枚の大きさ (CBattleWindow と同じ)
        private const float WINDOW_UV_INSET = 3f / 128f; // CBattleWindow.SetUVAdjust(3) と同じ、端の滲み防止

        private const string WallpaperFile = "menu/wallpaper/wallpaper_01.png"; // MenuBackground の MAIN_MENU (960x540 の羊皮紙)
        private const string WallpaperGrayFile = "menu/wallpaper/wallpaper_05.png"; // MenuBackground の WAREHOUSE (倉庫画面の灰色の石の紙。どこでも大臣の画面用)
        // 枠の中に壁紙を敷く時の寸法 (仮想画面 px)。角の絵 btlwdw_08 (128px = 32 仮想px) を測ると、外側の円弧の半径 ≈ 70px、
        // 縁 (黒線 + 白い帯) の幅 ≈ 15px なので、壁紙は縁の内側 (≈ 4 仮想px) から、角は同心の半径 ≈ 54px (≈ 13.5 仮想px) で丸める
        private const float PAPER_INSET = 4f;
        private const float PAPER_RADIUS = 13.5f;

        private static Texture2D[] s_window;
        private static Texture2D s_cursor, s_arrowL, s_arrowR, s_btnClose, s_wallpaper, s_wallpaperGray;
        private static Texture2D s_quill;      // ワールドマップのカーソル (羽、common/quill.png 80x80)。設定画面の自由カーソルの行の目印
        private static Texture2D s_btnPlain;   // とじる の画像から文字を消して横に伸ばした無地の金のプレート (MakePlainButton)
        private static bool s_btnPlainTried;
        /// <summary>無地のプレートの幅 / 高さ。「ボタン割り当て」(7 文字) が収まる幅。</summary>
        public const float PlainButtonAspect = 4.2f;
        private static bool s_loaded;

        /// <summary>Label が黒い影を付けるか (青灰色の枠の上では付け、羊皮紙の上では付けない)。</summary>
        public static bool Shadow = true;

        /// <summary>仮想画面 (960x540) の 1px が実画面で何 px か。</summary>
        public static float Unit { get { return Mathf.Max(0.5f, Screen.height / 540f); } }

        /// <summary>手のカーソルの縦横比 (幅 / 高さ)。</summary>
        public static float CursorAspect { get { return s_cursor != null ? (float)s_cursor.width / s_cursor.height : 1f; } }
        public static float ArrowAspect { get { return s_arrowL != null ? (float)s_arrowL.width / s_arrowL.height : 1f; } }
        public static float CloseButtonAspect { get { return s_btnClose != null ? (float)s_btnClose.width / s_btnClose.height : 2.2f; } }

        private static void Load()
        {
            if (s_loaded) return;
            s_loaded = true;
            try
            {
                Texture2D[] w = new Texture2D[WindowFiles.Length];
                bool ok = true;
                for (int i = 0; i < w.Length; i++)
                {
                    w[i] = LoadOwn(WindowFiles[i]);
                    if (w[i] == null) { ok = false; break; }
                    w[i].wrapMode = TextureWrapMode.Clamp;
                    w[i].filterMode = FilterMode.Point; // CBattleWindow と同じ
                }
                s_window = ok ? w : null;
                s_cursor = LoadOwn("menu/btn/cursor_00.png");
                if (s_cursor != null) s_cursor.wrapMode = TextureWrapMode.Clamp;
                s_btnClose = LoadOwn("menu/btn/btn_close.png");
                s_quill = LoadOwn("common/quill.png");
                if (s_quill != null) s_quill.wrapMode = TextureWrapMode.Clamp;
                // 壁紙は本体 (MenuBackground) がメニューを閉じる時に自分の分を破棄するので、共有せず自前で持つ
                s_wallpaper = LoadOwn(WallpaperFile);
                if (s_wallpaper != null) s_wallpaper.wrapMode = TextureWrapMode.Clamp;
                s_wallpaperGray = LoadOwn(WallpaperGrayFile);
                if (s_wallpaperGray != null) s_wallpaperGray.wrapMode = TextureWrapMode.Clamp;
                // 矢印はアーカイブの文字なし (スマホ版) を自前で持つ。Util.LoadTexture だと登録表の絵 (ButtonGlyph が
                // ArrowButtonScalePercent で縮めた絵) が返り、この MOD の窓の矢印まで小さくなるため
                s_arrowL = LoadOwn("menu/btn/L_btn.png");
                s_arrowR = LoadOwn("menu/btn/R_btn.png");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[UiParts] load failed: " + e.Message);
            }
            Plugin.Log.LogInfo(string.Format("[UiParts] window={0} cursor={1} arrows={2} close={3} wallpaper={4} quill={5}",
                s_window != null, s_cursor != null, s_arrowL != null && s_arrowR != null, s_btnClose != null, s_wallpaper != null, s_quill != null));
        }

        // アーカイブから自前の Texture2D に読む (本体の登録テクスチャとは別の実体。この MOD が持ち続ける)
        private static Texture2D LoadOwn(string fname)
        {
            byte[] bytes = null;
            try { bytes = Util.falloc(fname); } catch { }
            if (bytes == null || bytes.Length == 0)
            {
                Plugin.Log.LogWarning("[UiParts] not found: " + fname);
                return null;
            }
            Texture2D t = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            t.name = "rs2qol_" + System.IO.Path.GetFileNameWithoutExtension(fname);
            t.hideFlags = HideFlags.HideAndDontSave;
            if (!t.LoadImage(bytes)) { UnityEngine.Object.Destroy(t); return null; }
            return t;
        }

        /// <summary>枠を 9 枚で描く (CBattleWindow.Draw と同じ分割)。部品が無ければ false。</summary>
        public static bool DrawWindow(Rect r)
        {
            Load();
            if (s_window == null) return false;
            float tile = WINDOW_TILE * Unit;
            if (tile * 2 > r.width) tile = r.width / 2;
            if (tile * 2 > r.height) tile = r.height / 2;
            float x0 = r.x, x1 = r.x + tile, x2 = r.xMax - tile;
            float y0 = r.y, y1 = r.y + tile, y2 = r.yMax - tile;
            float mw = r.width - tile * 2, mh = r.height - tile * 2;
            Rect uv = new Rect(WINDOW_UV_INSET, WINDOW_UV_INSET, 1f - WINDOW_UV_INSET * 2, 1f - WINDOW_UV_INSET * 2);
            Color c = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(new Rect(x1, y1, mw, mh), s_window[4], uv);
            GUI.DrawTextureWithTexCoords(new Rect(x1, y0, mw, tile), s_window[1], uv);
            GUI.DrawTextureWithTexCoords(new Rect(x1, y2, mw, tile), s_window[7], uv);
            GUI.DrawTextureWithTexCoords(new Rect(x0, y1, tile, mh), s_window[3], uv);
            GUI.DrawTextureWithTexCoords(new Rect(x2, y1, tile, mh), s_window[5], uv);
            GUI.DrawTextureWithTexCoords(new Rect(x0, y0, tile, tile), s_window[0], uv);
            GUI.DrawTextureWithTexCoords(new Rect(x2, y0, tile, tile), s_window[2], uv);
            GUI.DrawTextureWithTexCoords(new Rect(x0, y2, tile, tile), s_window[6], uv);
            GUI.DrawTextureWithTexCoords(new Rect(x2, y2, tile, tile), s_window[8], uv);
            GUI.color = c;
            return true;
        }

        /// <summary>
        /// 枠の中に本体メニューの壁紙 (羊皮紙) を敷いた窓。壁紙は本体と同じく画面全体の大きさで置き、枠の内側だけ見せる
        /// (枠を通してメニュー画面の背景が見えている形)。枠の縁 (PAPER_INSET) は残す。枠か壁紙が無ければ false。
        /// </summary>
        public static bool DrawPaperWindow(Rect r)
        {
            return DrawPaperWindow(r, false);
        }

        /// <summary>枠の中に壁紙を敷いて描く。gray なら倉庫画面の灰色の石の紙 (どこでも大臣の画面)、そうでなければメニューの羊皮紙。</summary>
        public static bool DrawPaperWindow(Rect r, bool gray)
        {
            Load();
            Texture2D paper = gray ? s_wallpaperGray : s_wallpaper;
            if (paper == null || !DrawWindow(r)) return false;
            float inset = Mathf.Round(PAPER_INSET * Unit);
            Rect inner = new Rect(Mathf.Round(r.x + inset), Mathf.Round(r.y + inset), Mathf.Round(r.width - inset * 2), Mathf.Round(r.height - inset * 2));
            if (inner.width <= 0 || inner.height <= 0) return true;
            // 壁紙は画面全体を覆う大きさ (本体の MenuBackground と同じ比率で、画面が 16:9 でなければ中央合わせで切る)
            float scale = Mathf.Max(Screen.width / (float)paper.width, Screen.height / (float)paper.height);
            float pw = paper.width * scale, ph = paper.height * scale;
            float px = (Screen.width - pw) / 2f, py = (Screen.height - ph) / 2f;
            // 枠の角は丸いので、壁紙の四隅も同じ半径で丸める。角の部分だけ壁紙の画素を写した小さなテクスチャに
            // 円弧のアルファを付けて描き (BakeCorners)、残りは上・中・下の 3 枚に分けてそのまま描く
            int c = Mathf.CeilToInt(PAPER_RADIUS * Unit);
            if (c * 2 > inner.width || c * 2 > inner.height) c = 0;
            Color col = GUI.color;
            GUI.color = Color.white;
            if (c > 0)
            {
                BakeCorners(paper, inner, c, px, py, pw, ph);
                DrawPaperClip(paper, new Rect(inner.x + c, inner.y, inner.width - 2 * c, c), px, py, pw, ph);
                DrawPaperClip(paper, new Rect(inner.x, inner.y + c, inner.width, inner.height - 2 * c), px, py, pw, ph);
                DrawPaperClip(paper, new Rect(inner.x + c, inner.yMax - c, inner.width - 2 * c, c), px, py, pw, ph);
                GUI.DrawTexture(new Rect(inner.x, inner.y, c, c), s_corners[0], ScaleMode.StretchToFill, true);
                GUI.DrawTexture(new Rect(inner.xMax - c, inner.y, c, c), s_corners[1], ScaleMode.StretchToFill, true);
                GUI.DrawTexture(new Rect(inner.x, inner.yMax - c, c, c), s_corners[2], ScaleMode.StretchToFill, true);
                GUI.DrawTexture(new Rect(inner.xMax - c, inner.yMax - c, c, c), s_corners[3], ScaleMode.StretchToFill, true);
            }
            else
            {
                DrawPaperClip(paper, inner, px, py, pw, ph);
            }
            GUI.color = col;
            return true;
        }

        // 画面全体に置いた壁紙のうち clip の範囲だけ描く
        private static void DrawPaperClip(Texture2D paper, Rect clip, float px, float py, float pw, float ph)
        {
            GUI.BeginGroup(clip);
            GUI.DrawTexture(new Rect(px - clip.x, py - clip.y, pw, ph), paper, ScaleMode.StretchToFill, false);
            GUI.EndGroup();
        }

        private static readonly Texture2D[] s_corners = new Texture2D[4];
        private static Rect s_cornersRect;
        private static int s_cornersSize, s_cornersW, s_cornersH;
        private static Texture2D s_cornersPaper;

        /// <summary>
        /// 壁紙の四隅 (c x c px) を、その画面位置に見える壁紙の画素で作り、角の外側 (円弧の外) を透明にする。
        /// 窓の位置か画面の大きさか壁紙が変わった時だけ作り直す。壁紙は自前で読んだ Texture2D なので画素を読める。
        /// </summary>
        private static void BakeCorners(Texture2D paper, Rect inner, int c, float px, float py, float pw, float ph)
        {
            if (s_corners[0] != null && s_cornersPaper == paper && s_cornersRect == inner && s_cornersSize == c && s_cornersW == Screen.width && s_cornersH == Screen.height) return;
            s_cornersPaper = paper; s_cornersRect = inner; s_cornersSize = c; s_cornersW = Screen.width; s_cornersH = Screen.height;
            float radius = c;
            for (int k = 0; k < 4; k++)
            {
                bool right = (k & 1) != 0, bottom = (k & 2) != 0;
                float ox = right ? inner.xMax - c : inner.x;   // この角の正方形の画面上の左上
                float oy = bottom ? inner.yMax - c : inner.y;
                float ccx = right ? 0f : c;                     // 円弧の中心 (正方形内の座標)
                float ccy = bottom ? 0f : c;
                Texture2D t = s_corners[k];
                if (t == null || t.width != c)
                {
                    if (t != null) UnityEngine.Object.Destroy(t);
                    t = new Texture2D(c, c, TextureFormat.ARGB32, false);
                    t.name = "rs2qol_paper_corner" + k;
                    t.hideFlags = HideFlags.HideAndDontSave;
                    t.wrapMode = TextureWrapMode.Clamp;
                    t.filterMode = FilterMode.Bilinear;
                    s_corners[k] = t;
                }
                Color[] pix = new Color[c * c];
                for (int j = 0; j < c; j++)         // j: 上から
                {
                    for (int i = 0; i < c; i++)
                    {
                        float lx = i + 0.5f, ly = j + 0.5f;
                        float dist = Mathf.Sqrt((lx - ccx) * (lx - ccx) + (ly - ccy) * (ly - ccy));
                        float a = Mathf.Clamp01(radius - dist + 0.5f);
                        float u = (ox + lx - px) / pw;
                        float v = 1f - (oy + ly - py) / ph;      // テクスチャは下が 0
                        Color p = paper.GetPixelBilinear(u, v);
                        p.a *= a;
                        pix[(c - 1 - j) * c + i] = p;
                    }
                }
                t.SetPixels(pix);
                t.Apply();
            }
        }

        // 装備画面の品目欄と同じ見た目の箱。色と縁の幅はスクリーンショットの画素から逆算した。
        // 羊皮紙に「へこんでいる」箱: 上と左は暗い縁 (2 単位) から 2.5 単位かけて地の色に戻り、右と下は 5 単位かけて明るくなる
        private const float DARK_BOX_RADIUS = 12f;   // 角の半径 (仮想単位)
        private const float DARK_BOX_SHADOW = 2f;    // 上・左の暗い縁の太さ (仮想単位)
        private const float DARK_BOX_SHADOW_FADE = 2.5f; // 暗い縁から地の色へ戻る幅 (仮想単位)
        private const float DARK_BOX_LIGHT = 5f;     // 右・下の明るい縁の幅 (仮想単位)
        private static readonly Color DarkBoxFill = new Color(0.255f, 0.24f, 0.28f, 0.5f);
        private static readonly Color DarkBoxShadow = new Color(0f, 0f, 0f, 0.45f);
        private static readonly Color DarkBoxLight = new Color(0.8f, 0.8f, 0.9f, 0.25f);
        private static Texture2D s_darkBox;
        private static GUIStyle s_darkBoxStyle;
        private static int s_darkBoxRadius;

        /// <summary>
        /// 本体の装備画面の品目欄 (武器: / 防具: の暗い半透明の角丸) と同じ見た目の箱。部品は自前で焼くので常に描ける。
        /// 角だけ焼いた小さな絵を GUIStyle.border で 9 分割して引き伸ばす。
        /// </summary>
        public static void DrawDarkBox(Rect r)
        {
            int rad = Mathf.Max(4, Mathf.RoundToInt(DARK_BOX_RADIUS * Unit));
            if (s_darkBox == null || s_darkBoxRadius != rad) BakeDarkBox(rad);
            GUI.Box(r, GUIContent.none, s_darkBoxStyle);
        }

        private static void BakeDarkBox(int rad)
        {
            s_darkBoxRadius = rad;
            int n = rad * 2 + 4;                        // 中央に 4px の伸ばし代
            if (s_darkBox != null) UnityEngine.Object.Destroy(s_darkBox);
            Texture2D t = new Texture2D(n, n, TextureFormat.ARGB32, false);
            t.name = "rs2qol_dark_box";
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            float u = Unit;
            float shadowW = DARK_BOX_SHADOW * u, shadowFade = DARK_BOX_SHADOW_FADE * u, lightW = DARK_BOX_LIGHT * u;
            float half = n / 2f;
            Color[] pix = new Color[n * n];
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    // 角丸四角の符号付き距離 (外が正) と、その点に一番近い縁の向き (外向きの法線)
                    float px = i + 0.5f - half, py = j + 0.5f - half;
                    float dx = Mathf.Max(Mathf.Abs(px) - (half - rad), 0f);
                    float dy = Mathf.Max(Mathf.Abs(py) - (half - rad), 0f);
                    float len = Mathf.Sqrt(dx * dx + dy * dy);
                    float d = len - rad;
                    float nx = 0f, ny = 0f;
                    if (len > 0f) { nx = Mathf.Sign(px) * dx / len; ny = Mathf.Sign(py) * dy / len; }
                    float cover = Mathf.Clamp01(0.5f - d);                 // 外縁のアンチエイリアス
                    float depth = -d;                                          // 縁からの深さ (px)
                    // 上・左 (法線が上か左を向く) は暗い縁、右・下は明るい縁。右上・左下の角は中間で地の色のまま
                    float shadowK = Mathf.Clamp01(-(nx + ny));
                    float lightK = Mathf.Clamp01(nx + ny);
                    Color c = DarkBoxFill;
                    if (shadowK > 0f)
                    {
                        float s = depth <= shadowW ? 1f : Mathf.Clamp01(1f - (depth - shadowW) / shadowFade);
                        c = Color.Lerp(c, DarkBoxShadow, s * shadowK);
                    }
                    if (lightK > 0f)
                    {
                        float s = Mathf.Clamp01(1f - depth / lightW);
                        c = Color.Lerp(c, DarkBoxLight, s * lightK);
                    }
                    c.a *= cover;
                    pix[(n - 1 - j) * n + i] = c;
                }
            }
            t.SetPixels(pix);
            t.Apply();
            s_darkBox = t;
            s_darkBoxStyle = new GUIStyle();
            s_darkBoxStyle.normal.background = t;
            s_darkBoxStyle.border = new RectOffset(rad + 1, rad + 1, rad + 1, rad + 1);
        }

        /// <summary>手のカーソル (右向き) を、指先が (tipX, centerY) に来るように高さ h で描く。部品が無ければ false。</summary>
        public static bool DrawCursor(float tipX, float centerY, float h)
        {
            Load();
            if (s_cursor == null) return false;
            float w = h * CursorAspect;
            GUI.DrawTexture(new Rect(tipX - w, centerY - h / 2f, w, h), s_cursor, ScaleMode.StretchToFill, true);
            return true;
        }

        /// <summary>ワールドマップのカーソル (羽) を矩形いっぱい (正方形、絵の縦横比を保つ) に描く。部品が無ければ false。</summary>
        public static bool DrawQuill(Rect r)
        {
            Load();
            if (s_quill == null) return false;
            GUI.DrawTexture(r, s_quill, ScaleMode.ScaleToFit, true);
            return true;
        }

        /// <summary>L / R の矢印ボタンを描く。部品が無ければ false。</summary>
        public static bool DrawArrow(Rect r, bool left)
        {
            Load();
            Texture2D t = left ? s_arrowL : s_arrowR;
            if (t == null) return false;
            GUI.DrawTexture(r, t, ScaleMode.ScaleToFit, true);
            return true;
        }

        /// <summary>とじる ボタンの画像を描く。部品が無ければ false。</summary>
        public static bool DrawCloseButton(Rect r)
        {
            Load();
            if (s_btnClose == null) return false;
            GUI.DrawTexture(r, s_btnClose, ScaleMode.StretchToFill, true);   // 呼ぶ側が CloseButtonAspect で矩形を作るので、矩形の高さそのままに描く (ScaleToFit だと丸めで 1px ずれる)
            return true;
        }

        /// <summary>
        /// 文字の無い金のプレート (とじる と同じ見た目) を描く。本体には無地のボタン画像が無いので、btn_close.png から作る:
        /// プレートの中は縦のグラデーションだけで横には一様なので、各行を左縁の内側 (x=44/286) の色で塗りつぶして文字を消し、
        /// 左右の縁 (角丸と縁取り) はそのまま、中を伸ばして横長にする。文字は呼ぶ側が上に描く。部品が無ければ false。
        /// </summary>
        public static bool DrawPlainButton(Rect r)
        {
            Load();
            if (s_btnPlain == null && !s_btnPlainTried)
            {
                s_btnPlainTried = true;
                try { if (s_btnClose != null) s_btnPlain = MakePlainButton(s_btnClose, Mathf.RoundToInt(s_btnClose.height * PlainButtonAspect)); }
                catch (Exception e) { Plugin.Log.LogWarning("[UiParts] plain button failed: " + e.Message); }
            }
            if (s_btnPlain == null) return false;
            GUI.DrawTexture(r, s_btnPlain, ScaleMode.StretchToFill, true);   // 同上 (PlainButtonAspect)
            return true;
        }

        private static Texture2D MakePlainButton(Texture2D src, int newW)
        {
            int w = src.width, h = src.height;
            int edge = w * 44 / 286;            // 縁取りと角丸はこの幅に収まっている (btn_close.png は 286x130、文字は x=60 あたりから)
            if (newW < w) newW = w;
            Color32[] sp = src.GetPixels32();   // LoadImage で読んだ自前の Texture2D なので読める
            Color32[] dp = new Color32[newW * h];
            for (int y = 0; y < h; y++)
            {
                Color32 fill = sp[y * w + edge];
                for (int x = 0; x < newW; x++)
                {
                    Color32 c;
                    if (x < edge) c = sp[y * w + x];
                    else if (x >= newW - edge) c = sp[y * w + (x - (newW - w))];
                    else c = fill;
                    dp[y * newW + x] = c;
                }
            }
            Texture2D t = new Texture2D(newW, h, TextureFormat.ARGB32, false);
            t.name = "rs2qol_btn_plain";
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = TextureWrapMode.Clamp;
            t.SetPixels32(dp);
            t.Apply(false, false);
            return t;
        }

        /// <summary>見えないボタン (画像の上に置いてマウスのクリックを拾う)。OnGUI の中からだけ呼ぶ。</summary>
        public static bool ClickArea(Rect r)
        {
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        // ---------------------------------------------------------------- 影付き文字

        private static readonly Dictionary<GUIStyle, GUIStyle> s_shadowStyles = new Dictionary<GUIStyle, GUIStyle>();

        /// <summary>本体のダイアログ文字と同じく、黒い影を付けて描く。</summary>
        public static void Label(Rect r, string text, GUIStyle style)
        {
            if (!Shadow) { GUI.Label(r, text, style); return; }
            GUIStyle sh;
            if (!s_shadowStyles.TryGetValue(style, out sh))
            {
                sh = new GUIStyle(style);
                sh.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
                s_shadowStyles[style] = sh;
            }
            float o = Mathf.Max(1f, Mathf.Round(Unit * 0.75f));
            GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), text, sh);
            GUI.Label(r, text, style);
        }

        /// <summary>スタイルを作り直した時に影のキャッシュも捨てる。</summary>
        public static void ResetStyles()
        {
            s_shadowStyles.Clear();
        }

        /// <summary>本体のメニューと同じ効果音 (212 = カーソル移動、213 = 決定、211 = キャンセル)。</summary>
        public static void SeCursor() { PlaySe(212); }
        public static void SeOk() { PlaySe(213); }
        public static void SeCancel() { PlaySe(211); }

        /// <summary>
        /// MOD 設定画面を閉じる時の効果音 = カーソル音 (212) 固定。211 (キャンセル) はきつく 213 (決定) は大げさ、というユーザーの判断 (2026-10-13)。
        /// 効果音を番号で選ぶ設定は一度作って消した (本体の効果音を順に鳴らせる作りは、サウンドトラックの商品に触れかねないため)。
        /// MOD が鳴らすのはメニューの 3 音 (211 / 212 / 213) だけにする。
        /// </summary>
        public static void SeClose() { PlaySe(212); }

        private static void PlaySe(int no)
        {
            try { Sound.setSe(no, 10); } catch { }
        }
    }
}
