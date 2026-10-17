using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 店の仲間の絵の横に出す小さな吹き出しと、その中の文字。ShopEquipMark (E) と ShopEquipCompare (▲N / ▼N) が共用する。
    ///
    /// 吹き出しはドット絵。格子は文字のドット (1 ドット = 36/16 = 2.25 単位) の半分 (1.125 単位) で、文字より一段細かい
    /// (文字と同じ粗さだと角がごつく、本体の会話窓の画像を縮めて貼ると滑らかすぎて中のドット文字と合わなかった)。
    /// 色は本体の会話窓の画像 (menu/msg_hd) から取った値: 地は生成り (243,243,217) の 3/4 の不透明度、文字は縁の線の焦げ茶 (60,49,39)。
    /// 縁の線は描かない (LINE = false。黒い線が邪魔だった。描く仕組みは残してある)。
    /// 形: 文字の周り 6 マスが地、その外 1 マスが縁の線。幅は文字数で変えず ▲10 が入る幅 (文字は真ん中)。角は半径 5 マス相当に欠く。左に 6 マスの尾が出て仲間の絵を指す。
    /// 縁の線は「地の形を上下左右に 1 マス太らせた外側」として作るので、角や尾の形を変えても線が付いてくる。
    ///
    /// 描き方: 本体の GS.FillRect は 1 回ごとに Material と Mesh を作る (次のフレームで破棄) ので毎フレーム大量に呼ぶとフレームが
    /// 落ち、本体の入力 (論理フレームごとの読み取り) が短い押下を取りこぼす (実機で A が効かない場面があった)。ここでは仲間 (枠) ごとに
    /// 縁・地・文字の 3 つの Mesh を持ち、文字か位置が変わった時だけ組み直し、毎フレームは Graphics.DrawMesh を 3 回呼ぶだけ。
    /// Material は本体の Util.m_mtl_color2d の複製 (深度テスト無し)、renderQueue = 優先度 (大きい方が後に描かれる)。
    /// 座標は FillRect と同じ仮想 960x540 の左上原点で受け、頂点は中心原点・上向き y に直す。
    /// </summary>
    internal static class ShopBubble
    {
        public const int GLYPH_UP = 10, GLYPH_DOWN = 11, GLYPH_E = 12, GLYPH_PM = 13, GLYPH_BANG = 14;
        private const float DOT_UNITS = 36f / 16f;               // 文字の 1 ドットの目安 (仲間の絵の 1 ドット = 2.25 単位)。実際は画素の整数倍に丸める (下の Fit)
        private static float s_dot = DOT_UNITS, s_u = DOT_UNITS / 2f;   // 文字の 1 ドットと格子 1 マス (単位)。Fit で画素の整数倍にする
        private static float s_unit = 1f;                        // 画素 / 単位 (Fit で更新)
        private static int s_lastUPx = -1, s_lastH = -1;         // 窓の大きさが変わった時にログを 1 行出すため
        // 文字の 1 ドットは格子 2 マスぴったり。割合 (0.8 / 0.9) で縮めると画素への丸めが窓の高さごとに違い (900 行で 4 画素、1440 行で 5 画素)、
        // 吹き出しと文字の比率が窓によって変わって見えたのでやめた。文字を小さく見せたい時は余白 (PAD) を広げる
        private const float ADVANCE = 6f;                        // 文字の送り (ドット)。5 ドット幅 + 1 ドット
        private const int PAD = 6;                               // 文字と縁の線の間の地 (マス。上下左右とも。4 では文字が窮屈だったので 6)
        private const int MIN_GLYPHS = 3;                        // 吹き出しの幅は文字数で変えず、▲10 (3 文字) が入る幅に固定 (E ▼10 の 4 文字の時だけ広がる)
        private const int TAIL = 6;                              // 尾の長さ (マス)
        private const int MARGIN = 1;                            // 縁の線の太さ (マス)。LINE が false でもこの分は空けておく (位置を変えないため)
        private const bool LINE = false;                         // 縁の線を描くか。黒い線は邪魔だったので描かない (地と尾だけ)
        private static readonly int[] s_cornerCut = { 3, 1, 1 }; // 角の欠き (上から 1 行ごとに左右何マス欠くか。半径 5 マスの円)
        // 位置: 尾の先 (格子の左端) は絵の矩形の右端から 19 単位 (14 だと横歩きで髪や肩に掛かり、24 は離れすぎた)。
        // 格子の上端は絵の矩形の上端の 5 単位上 (高さ 28 マス = 31.5 単位なので、尾が絵の頭の高さを指す)
        public const float OFFSET_X = 19f, OFFSET_Y = -5f;
        private static readonly Color32 s_lineColor = new Color32(60, 49, 39, 255);
        private static readonly Color32 s_paperColor = new Color32(243, 243, 217, 191);

        // 5x7 のドット絵。'#' が点。並びは 0..9、上向きの三角 = プラス (10)、下向きの三角 = マイナス (11)、E (12)、± (13。上に +、下に −)、! (14。E の右に添える)
        // (+ / - の記号より三角の方が一目で上下が分かる。三角は 5x3 で、行 2..4 に置いて数字の中段に揃える)
        private static readonly string[][] s_glyphs =
        {
            new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." },
            new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
            new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
            new[] { "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###." },
            new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." },
            new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." },
            new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." },
            new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." },
            new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." },
            new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." },
            new[] { ".....", ".....", "..#..", ".###.", "#####", ".....", "....." },
            new[] { ".....", ".....", "#####", ".###.", "..#..", ".....", "....." },
            new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" },
            new[] { ".....", "..#..", "#####", "..#..", ".....", "#####", "....." },
            new[] { "..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#.." },
        };

        private sealed class Slot
        {
            public string key;
            public float x, y;
            public Mesh line = GS.MeshNew(), paper = GS.MeshNew(), text = GS.MeshNew();
        }
        private static readonly Slot[] s_slots = new Slot[5];
        private static Material s_mtlLine, s_mtlPaper, s_mtlText;
        private static readonly List<Vector3> s_verts = new List<Vector3>(1200);
        private static bool s_failed, s_loaded;

        public static bool Ok { get { return !s_failed; } }

        /// <summary>仲間の絵の矩形から吹き出し (尾を含む格子) の左上を決める。</summary>
        public static void Anchor(Rect rect, out float x0, out float y0)
        {
            x0 = rect.x + rect.width + OFFSET_X;
            y0 = rect.y + OFFSET_Y;
        }

        private static Material MakeMaterial(Color32 color)
        {
            Material m = new Material(Util.m_mtl_color2d);
            ShaderUtil.SetDepthTest(m, CompareFunction.Always);
            m.color = color;
            return m;
        }

        private static void Load()
        {
            if (s_loaded) return;
            s_loaded = true;
            try
            {
                if (Util.m_mtl_color2d == null) throw new System.Exception("Util.m_mtl_color2d not ready");
                s_mtlLine = MakeMaterial(s_lineColor);
                s_mtlPaper = MakeMaterial(s_paperColor);
                s_mtlText = MakeMaterial(s_lineColor);   // 縁と同じ色だが、地より後に描くので別の Material (renderQueue は Material ごと)
                Plugin.Log.LogInfo(string.Format("[ShopBubble] ready: pad={0} tail={1}", PAD, TAIL));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[ShopBubble] disabled until restart: " + e.Message);
                s_failed = true;
            }
        }

        /// <summary>FillRect と同じ: 仮想 960x540 の左上原点を、中心原点・上向き y の頂点にして四角を足す。</summary>
        private static void AddQuad(List<Vector3> v, float x, float y, float w, float h)
        {
            float gx = x - 480f, gy = 270f - y;
            v.Add(new Vector3(gx, gy, 0f));
            v.Add(new Vector3(gx, gy - h, 0f));
            v.Add(new Vector3(gx + w, gy - h, 0f));
            v.Add(new Vector3(gx + w, gy, 0f));
        }

        private static void AddGlyph(List<Vector3> v, int g, float x, float y)
        {
            string[] rows = s_glyphs[g];
            for (int r = 0; r < rows.Length; r++)
            {
                string row = rows[r];
                int start = -1;
                for (int c = 0; c <= row.Length; c++)
                {
                    bool on = c < row.Length && row[c] == '#';
                    if (on && start < 0) start = c;
                    else if (!on && start >= 0)
                    {
                        AddQuad(v, x + s_dot * start, y + s_dot * r, s_dot * (c - start), s_dot);
                        start = -1;
                    }
                }
            }
        }

        /// <summary>格子 (true = 塗る) を行ごとの横並びにまとめて四角として足す。格子の (0,0) が仮想座標 (x, y) のマス。</summary>
        private static void AddGrid(List<Vector3> v, bool[,] g, float x, float y)
        {
            int w = g.GetLength(0), h = g.GetLength(1);
            for (int r = 0; r < h; r++)
            {
                int start = -1;
                for (int c = 0; c <= w; c++)
                {
                    bool on = c < w && g[c, r];
                    if (on && start < 0) start = c;
                    else if (!on && start >= 0)
                    {
                        AddQuad(v, x + s_u * start, y + s_u * r, s_u * (c - start), s_u);
                        start = -1;
                    }
                }
            }
        }

        private static void SetMesh(Mesh m, List<Vector3> v)
        {
            m.Clear();
            m.vertices = v.ToArray();
            int[] idx = new int[v.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            m.SetIndices(idx, MeshTopology.Quads, 0);
            m.bounds = GS.m_aoid_clip;
        }

        private static void Build(Slot s, int[] glyphs, int count, float x0, float y0)
        {
            int textW = 2 * ((int)ADVANCE * count - 1), textH = 14;   // 文字の大きさ (マス)。5 ドットの字 + 1 ドットの間
            int boxW = 2 * ((int)ADVANCE * (count > MIN_GLYPHS ? count : MIN_GLYPHS) - 1);   // 文字の場所の幅 (固定)。文字はその真ん中
            // 格子: 左から 縁 1 + 尾 6 + 地 PAD + 文字 + 地 PAD + 縁 1、上から 縁 1 + 地 PAD + 文字 + 地 PAD + 縁 1
            int ox = MARGIN + TAIL + PAD, oy = MARGIN + PAD;        // 文字の左上のマス
            int gw = ox + boxW + PAD + MARGIN, gh = oy + textH + PAD + MARGIN;
            int fx0 = MARGIN + TAIL, fx1 = gw - MARGIN, fy0 = MARGIN, fy1 = gh - MARGIN;   // 地 (角を欠く前の四角) の範囲
            bool[,] paper = new bool[gw, gh], line = new bool[gw, gh];
            for (int r = fy0; r < fy1; r++)
            {
                int cut = 0;
                int fromTop = r - fy0, fromBottom = fy1 - 1 - r;
                if (fromTop < s_cornerCut.Length) cut = s_cornerCut[fromTop];
                if (fromBottom < s_cornerCut.Length && s_cornerCut[fromBottom] > cut) cut = s_cornerCut[fromBottom];
                for (int c = fx0 + cut; c < fx1 - cut; c++) paper[c, r] = true;
            }
            // 尾: 地の左の縁から左へ、下に向かって細くなる (先端は真ん中より少し下)。本体の会話窓の尾と同じ向き
            int mid = (fy0 + fy1) / 2;                                // 地の縦の真ん中 (= 10)
            for (int k = 1; k <= TAIL; k++)
            {
                int bottom = mid + 2;                                  // 1 列目は 5 マス (真ん中 ±2)、左へ 1 マスずつ細くなり先端は 1 マス
                int h = 6 - k; if (h < 1) h = 1;
                int top = bottom - h + 1;
                for (int r = top; r <= bottom; r++) paper[fx0 - k, r] = true;
            }
            for (int c = 0; c < gw; c++)
                for (int r = 0; r < gh; r++)
                {
                    if (paper[c, r]) continue;
                    line[c, r] = (c > 0 && paper[c - 1, r]) || (c + 1 < gw && paper[c + 1, r])
                              || (r > 0 && paper[c, r - 1]) || (r + 1 < gh && paper[c, r + 1]);
                }
            s_verts.Clear(); if (LINE) AddGrid(s_verts, line, x0, y0); SetMesh(s.line, s_verts);
            s_verts.Clear(); AddGrid(s_verts, paper, x0, y0); SetMesh(s.paper, s_verts);
            s_verts.Clear();
            // 文字は文字の場所 (boxW x textH マス) の真ん中。画素に揃えるため、ずらす量は画素の整数に丸める
            float tw = s_dot * (ADVANCE * count - 1f), th = s_dot * 7f;
            float tx = x0 + Mathf.Round((s_u * ox + (s_u * boxW - tw) / 2f) * s_unit) / s_unit;
            float ty = y0 + Mathf.Round((s_u * oy + (s_u * textH - th) / 2f) * s_unit) / s_unit;
            for (int i = 0; i < count; i++) AddGlyph(s_verts, glyphs[i], tx + s_dot * ADVANCE * i, ty);
            SetMesh(s.text, s_verts);
        }

        /// <summary>
        /// 画素に揃える。1 ドット = 2.25 単位は 1600x900 の窓では 3.75 画素で、描く位置の端数によって線が 3 画素になったり 4 画素に
        /// なったりする (仲間ごとに E の線の太さが違って見えた)。格子 1 マスを画素の整数倍 (1.125 単位 × 画素/単位 を四捨五入、最低 1 画素)
        /// にし、吹き出しの原点も画素に揃えると、どの仲間でも同じ太さになる。横は窓が 16:9 でないときの余白 (左右中央寄せ) も含めて揃える。
        /// 戻り値は 1 マスの画素数 (組み直しの要否の判定に使う)。
        /// </summary>
        private static int Fit(ref float x0, ref float y0)
        {
            float unit = Mathf.Max(0.5f, Screen.height / 540f);          // 画素 / 単位 (UiParts.Unit と同じ)
            float offX = (Screen.width - 960f * unit) / 2f;               // 16:9 でない窓の左の余白 (画素)
            int uPx = Mathf.Max(1, Mathf.RoundToInt(DOT_UNITS / 2f * unit));
            if (uPx != s_lastUPx || Screen.height != s_lastH)
            {
                s_lastUPx = uPx; s_lastH = Screen.height;
                Plugin.Log.LogInfo(string.Format("[ShopBubble] fit: screen={0}x{1} unit={2:F3} cell={3}px dot={4}px offX={5:F1}",
                    Screen.width, Screen.height, unit, uPx, uPx * 2, offX));
            }
            s_u = uPx / unit;
            s_unit = unit;
            s_dot = s_u * 2f;
            x0 = (Mathf.Round(x0 * unit + offX) - offX) / unit;
            y0 = Mathf.Round(y0 * unit) / unit;
            return uPx;
        }

        /// <summary>枠 slot (仲間の番号) に、glyphs[0..count) の文字を入れた吹き出しを描く。(x0, y0) は格子の左上 (Anchor で得る)。</summary>
        public static void Draw(int slot, int[] glyphs, int count, float x0, float y0, int pri)
        {
            Load();
            if (s_failed || count <= 0) return;
            Slot s = s_slots[slot];
            if (s == null) s_slots[slot] = s = new Slot();
            string key = "";
            for (int i = 0; i < count; i++) key += glyphs[i] + ",";
            int uPx = Fit(ref x0, ref y0);
            key += uPx;
            if (key != s.key || x0 != s.x || y0 != s.y)
            {
                Build(s, glyphs, count, x0, y0);
                s.key = key; s.x = x0; s.y = y0;
            }
            // 縁 → 地 → 文字 の順 (renderQueue が大きい方が後)
            s_mtlLine.renderQueue = pri;
            s_mtlPaper.renderQueue = pri + 1;
            s_mtlText.renderQueue = pri + 2;
            if (LINE) Graphics.DrawMesh(s.line, Matrix4x4.identity, s_mtlLine, 0);
            Graphics.DrawMesh(s.paper, Matrix4x4.identity, s_mtlPaper, 0);
            Graphics.DrawMesh(s.text, Matrix4x4.identity, s_mtlText, 0);
        }
    }
}
