using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 画面のボタン (「Ⓐけってい」「Ⓑもどる」など) から Ⓐ Ⓑ Ⓧ の丸文字と、ページ送りの矢印の L / R の文字を消す
    /// ([Display] HideButtonGlyphs、既定 true)。
    ///
    /// 本体 (Steam 版) は起動時 (Util.file_regist_on_boot、etc/regist_files_steam.csv の「*」行) に Xbox 用の絵
    /// menu/btn_xboxone/btn_kettei.png (Ⓐけってい) などをファイル名「btn_kettei」で Util.m_regist_texture に登録し、
    /// Util.LoadTexture は同じ名前ならこの登録を優先して返す。そのため UI が読む menu/btn/btn_kettei.png (スマホ版) は
    /// 全部 Xbox 用の絵に置き換わっている。丸文字はパッドの割り当てを変えると合わなくなり (この MOD の既定では決定は B)、
    /// キーボードでは最初から意味が無いので消す。
    /// ボタンは Xbox 用の絵から丸文字の列を消して文字の列を中央へ写した絵を作って登録に差し替える (Recenter。スマホ版の絵
    /// menu/btn/btn_*.png も丸文字なしで残っているが、文字が本体より大きいので使わない。本体の文字の大きさをそのまま保つ)。
    /// L / R の矢印 (btn_steam_en/L_btn.png に白い L) は文字なしのスマホ版 menu/btn/L_btn.png に差し替える
    /// (戦闘では BattleLR が同じことを戦闘 UI の中でやっている)。
    /// 差し替えは最初の GameMain.Update (登録が終わった後) と設定変更時。登録を戻せば本体の絵に戻る
    /// (作成済みの画面は作り直されるまで前の絵のまま)。
    /// </summary>
    internal static class ButtonGlyph
    {
        public static ConfigEntry<bool> Enabled;
        /// <summary>
        /// ページ送りの矢印 (L_btn / R_btn) の大きさ (%)。スーパーファミコン版には無いスマホ版の飾り矢印 (141x145) で、
        /// メニューの HP / LP の切替などに出る。SpriteStudio の部品の大きさは絵の大きさで決まるので、同じ大きさの透明な絵の
        /// 中央に縮めた矢印を描いた絵に差し替えて小さく見せる (当たり判定や位置は変わらない)。
        /// 100 以外なら HideButtonGlyphs が Off でも矢印だけは差し替える (元は本体と同じ文字入り btn_steam_en の絵)。
        /// </summary>
        public static ConfigEntry<int> ArrowScale;

        /// <summary>文字なしのスマホ版 (menu/btn/) にそのまま差し替えるもの。通常と _push の 2 枚ずつ。</summary>
        private static readonly string[] PlainNames = { "L_btn", "R_btn" };
        private static bool s_arrowsBuiltOn;     // 矢印を作った時の On
        private static int s_arrowsBuiltScale = -1;   // 矢印を作った時の %
        private static bool s_appliedOn;          // 登録表に入れた時の On
        private static int s_appliedScale = -1;   // 登録表に入れた時の %
        /// <summary>Xbox 用の絵 (menu/btn_xboxone/) から丸文字を消して文字を中央に寄せるもの。値は丸文字の数 (情報 は ⓍⓎ情報)。</summary>
        private static readonly KeyValuePair<string, int>[] RecenterNames =
        {
            new KeyValuePair<string, int>("btn_change", 1), new KeyValuePair<string, int>("btn_chiiki", 1),
            new KeyValuePair<string, int>("btn_close", 1), new KeyValuePair<string, int>("btn_exit", 1),
            new KeyValuePair<string, int>("btn_info", 1), new KeyValuePair<string, int>("btn_joho", 2),
            new KeyValuePair<string, int>("btn_kettei", 1), new KeyValuePair<string, int>("btn_return", 1),
            new KeyValuePair<string, int>("btn_stop", 1), new KeyValuePair<string, int>("btn_zentai", 1),
        };

        private static readonly System.Reflection.FieldInfo s_registField = AccessTools.Field(typeof(Util), "m_regist_texture");
        private static readonly System.Reflection.FieldInfo s_registField2 = AccessTools.Field(typeof(Util), "m_regist_texture2");

        private static readonly Dictionary<string, Texture2D> s_orig = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Texture2D> s_mine = new Dictionary<string, Texture2D>();
        private static bool s_built;
        private static bool s_applied;
        private static bool s_loggedApplied;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Display", "HideButtonGlyphs", true,
                "画面のボタン (Ⓐけってい、Ⓑもどる、Ⓧ地域 など) から丸文字を消して文字を中央に、ページ送りの矢印から L / R の文字を消す。" +
                "丸文字はボタン割り当てを変えると合わなくなり、キーボードでは意味が無いため。false なら本体どおり (Xbox 用の絵)");
            Enabled.SettingChanged += (s, e) => Apply();
            ArrowScale = config.Bind("Display", "ArrowButtonScalePercent", 55,
                new ConfigDescription("ページ送りの矢印ボタン (メニューの HP / LP の切替、店や道場のページ送り) の大きさ (%)。スーパーファミコン版には無いスマホ版の飾り矢印で、100 が本体どおり。既定 55。" +
                    "同じ大きさの透明な絵の中央に縮めた矢印を描くので、位置とマウスの当たり判定は変わらない。戦闘の画面端の矢印と MOD の窓の矢印は対象外",
                    new AcceptableValueRange<int>(30, 100)));
            ArrowScale.SettingChanged += (s, e) => Apply();
        }

        public static bool On { get { return Enabled != null && Enabled.Value; } }
        public static int Scale { get { return ArrowScale != null ? Mathf.Clamp(ArrowScale.Value, 30, 100) : 100; } }

        /// <summary>MOD 設定画面用: 5 % 刻みで回す (30〜100)。</summary>
        public static void CycleScale(int dir)
        {
            int v = Scale + dir * 5;
            if (v > 100) v = 30; else if (v < 30) v = 100;
            ArrowScale.Value = v;
        }

        public static string ScaleLabel(int v) { return v == 100 ? "100 % (本体どおり)" : v + " %"; }

        /// <summary>登録表を差し替える (On) か戻す (Off)。登録前 (GameMain.Start の途中) に呼ばれたら何もしない。</summary>
        public static void Apply()
        {
            Hashtable regist = s_registField != null ? s_registField.GetValue(null) as Hashtable : null;
            Hashtable regist2 = s_registField2 != null ? s_registField2.GetValue(null) as Hashtable : null;
            if (regist == null || regist2 == null || regist.Count == 0) return;
            bool on = On;
            int scale = Scale;
            bool wantArrows = on || scale != 100;   // 矢印は縮小だけでも差し替える
            bool want = on || wantArrows;
            if (want == s_applied && on == s_appliedOn && scale == s_appliedScale) return;
            if (on && !s_built) Build(regist);
            if (wantArrows && (s_arrowsBuiltOn != on || s_arrowsBuiltScale != scale)) BuildArrows(regist, on, scale);
            int n = 0;
            foreach (KeyValuePair<string, Texture2D> kv in s_mine)
            {
                if (kv.Value == null) continue;
                bool isArrow = kv.Key.StartsWith("L_btn") || kv.Key.StartsWith("R_btn");
                if (isArrow ? wantArrows : on)
                {
                    if (!regist.Contains(kv.Key)) continue;       // 本体が登録していない名前は触らない
                    if (!s_orig.ContainsKey(kv.Key)) s_orig[kv.Key] = regist[kv.Key] as Texture2D;
                    regist[kv.Key] = kv.Value;
                    regist2[kv.Value] = true;                      // Util.Destroy で消されないように (登録済み扱い)
                }
                else
                {
                    Texture2D o;
                    if (s_orig.TryGetValue(kv.Key, out o) && o != null) regist[kv.Key] = o;
                }
                n++;
            }
            s_applied = want;
            s_appliedOn = on;
            s_appliedScale = scale;
            if (!s_loggedApplied || !want)
            {
                s_loggedApplied = true;
                Plugin.Log.LogInfo("[ButtonGlyph] applied: " + (on ? "hide glyphs" : "vanilla glyphs") + ", arrows " + scale + "% (" + n + " textures)");
            }
        }

        /// <summary>
        /// 矢印 (L_btn / R_btn とその _push) を作る。元は on なら文字なしのスマホ版 menu/btn/、Off なら本体と同じ文字入り
        /// menu/btn_steam_en/。scale が 100 でなければ同じ大きさの透明な絵の中央に縮めて描く。
        /// 前に作った矢印が登録表に入っていれば本体の絵に戻してから捨てる (画面はテクスチャを参照で持つので、捨てずに戻す)。
        /// </summary>
        private static void BuildArrows(Hashtable regist, bool on, int scale)
        {
            foreach (string name in PlainNames)
            {
                foreach (string suffix in new[] { "", "_push" })
                {
                    string key = name + suffix;
                    Texture2D old;
                    if (s_mine.TryGetValue(key, out old) && old != null)
                    {
                        Texture2D o;
                        if (regist[key] == old && s_orig.TryGetValue(key, out o) && o != null) regist[key] = o;
                        Object.Destroy(old);
                        s_mine[key] = null;
                    }
                    string path = (on ? "menu/btn/" : "menu/btn_steam_en/") + key + ".png";
                    Texture2D src = LoadOwn(path);
                    if (src != null && scale != 100)
                    {
                        Texture2D small = null;
                        try { small = Shrink(src, scale, key); }
                        catch (System.Exception e) { Plugin.Log.LogWarning("[ButtonGlyph] shrink failed: " + key + ": " + e.Message); }
                        if (small != null) { Object.Destroy(src); src = small; }
                    }
                    s_mine[key] = src;
                }
            }
            s_arrowsBuiltOn = on;
            s_arrowsBuiltScale = scale;
            Plugin.Log.LogInfo("[ButtonGlyph] arrows built: " + (on ? "noletter" : "vanilla") + " " + scale + "%");
        }

        /// <summary>
        /// src と同じ大きさの透明な絵の中央に、src を percent % に縮めて描く。縮小は元の画素を箱型で平均 (α で重み付け。
        /// 透明な画素の色が縁に混ざって黒ずまないように)。
        /// </summary>
        private static Texture2D Shrink(Texture2D src, int percent, string key)
        {
            int w = src.width, h = src.height;
            int tw = Mathf.Max(1, Mathf.RoundToInt(w * percent / 100f)), th = Mathf.Max(1, Mathf.RoundToInt(h * percent / 100f));
            int ox = (w - tw) / 2, oy = (h - th) / 2;
            Color32[] p = src.GetPixels32();
            Color32[] d = new Color32[w * h];   // 既定は (0,0,0,0) = 透明
            float sx = w / (float)tw, sy = h / (float)th;
            for (int y = 0; y < th; y++)
            {
                int y0 = Mathf.FloorToInt(y * sy), y1 = Mathf.Min(h, Mathf.Max(y0 + 1, Mathf.CeilToInt((y + 1) * sy)));
                for (int x = 0; x < tw; x++)
                {
                    int x0 = Mathf.FloorToInt(x * sx), x1 = Mathf.Min(w, Mathf.Max(x0 + 1, Mathf.CeilToInt((x + 1) * sx)));
                    float r = 0, g = 0, b = 0, a = 0; int n = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        for (int xx = x0; xx < x1; xx++)
                        {
                            Color32 c = p[yy * w + xx];
                            r += c.r * c.a; g += c.g * c.a; b += c.b * c.a; a += c.a; n++;
                        }
                    }
                    if (n == 0 || a <= 0f) continue;
                    d[(oy + y) * w + ox + x] = new Color32((byte)Mathf.RoundToInt(r / a), (byte)Mathf.RoundToInt(g / a), (byte)Mathf.RoundToInt(b / a), (byte)Mathf.RoundToInt(a / n));
                }
            }
            Texture2D t = new Texture2D(w, h, TextureFormat.ARGB32, false);
            t.name = key + "_" + percent;
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = src.wrapMode;
            t.filterMode = src.filterMode;
            t.SetPixels32(d);
            t.Apply(false, false);
            return t;
        }

        private static void Build(Hashtable regist)
        {
            s_built = true;
            foreach (string suffix in new[] { "", "_push" })
            {
                Texture2D baseTex = LoadOwn("menu/btn/btn_close" + suffix + ".png");   // 丸文字の無い同じ型のプレート (スマホ版)
                foreach (KeyValuePair<string, int> nr in RecenterNames)
                {
                    string key = nr.Key + suffix;
                    Texture2D src = LoadOwn("menu/btn_xboxone/" + key + ".png");
                    Texture2D made = null;
                    if (src != null && baseTex != null)
                    {
                        try { made = Recenter(baseTex, src, key, nr.Value); }
                        catch (System.Exception e) { Plugin.Log.LogWarning("[ButtonGlyph] recenter failed: " + key + ": " + e.Message); }
                    }
                    if (src != null) Object.Destroy(src);
                    s_mine[key] = made;
                }
                if (baseTex != null) Object.Destroy(baseTex);
            }
            int ok = 0, missing = 0;
            foreach (KeyValuePair<string, Texture2D> kv in s_mine) { if (kv.Value != null) ok++; else missing++; }
            Plugin.Log.LogInfo("[ButtonGlyph] built: " + ok + " textures" + (missing > 0 ? ", " + missing + " missing" : ""));
        }

        private static Texture2D LoadOwn(string fname)
        {
            byte[] bytes = null;
            try { bytes = Util.falloc(fname); } catch { }
            if (bytes == null || bytes.Length == 0)
            {
                Plugin.Log.LogWarning("[ButtonGlyph] not found: " + fname);
                return null;
            }
            Texture2D t = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            t.name = System.IO.Path.GetFileNameWithoutExtension(fname) + "_noglyph";
            t.hideFlags = HideFlags.HideAndDontSave;   // 戦闘終了時の Util.GC (UnloadUnusedAssets) で消されないように
            if (!t.LoadImage(bytes)) { Object.Destroy(t); return null; }
            return t;
        }

        private static int Diff(Color32 a, Color32 b)
        {
            return Mathf.Max(Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g)), Mathf.Max(Mathf.Abs(a.b - b.b), Mathf.Abs(a.a - b.a)));
        }

        private static Color32 Lerp(Color32 a, Color32 b, float t)
        {
            return new Color32((byte)Mathf.RoundToInt(a.r + (b.r - a.r) * t), (byte)Mathf.RoundToInt(a.g + (b.g - a.g) * t),
                               (byte)Mathf.RoundToInt(a.b + (b.b - a.b) * t), (byte)Mathf.RoundToInt(a.a + (b.a - a.a) * t));
        }

        /// <summary>
        /// 丸文字の無い同じ型のプレート (base = とじる のスマホ版) を手掛かりに、src (Ⓐけってい など) の丸文字を消して文字の列を中央へ写す。
        /// プレートは同じ型なので、文字の無い行は base と src で同じ絵。手順 (手元の 20 枚で確認済み):
        ///   1. base で同じ色が最も長く続く行の区間 = プレートの内側。その両端から 8 px 内側を基準列 (leftRef / refX) にする
        ///   2. 行ごとに、base の基準列から色が続く範囲をその行の内側 [lo, hi] とする (縁と角丸を除く)
        ///   3. src と base が内側で違う行 = 丸文字か文字のある行 (縁の行は両方同じなので除外される)
        ///   4. その行で、隣の列との差が大きい列を「内容」(押下版の文字は彫り込み風で薄いので、基準色との差ではなく局所の差を見る。
        ///      隣 1 px との差なら丸と文字の間の 1 列の隙間が残る)。左端 16 px (押下版は丸が縁に掛かる) と右端 1 px は見ない。
        ///      丸文字は最初の内容から 28 列以上続く塊 (Ⓑ の中の隙間を跨ぐ) で、その次の空き列で終わる。丸が 2 つ (ⓍⓎ情報) なら繰り返す。
        ///      残りの内容の範囲 = 文字
        ///   5. その行だけ描き直す: 丸文字より左は base、丸文字から文字の右端までは両脇の色で線形に埋め (押下版は横にも薄い
        ///      グラデーションがある。文字が右端まで届く行は右端の色を base から取る)、文字の列 (両脇 2 px 付き) を中央へ写す。
        ///      右端で切れている文字 (いれかえ、けってい) は右の 3 列をプレートへ溶け込ませる
        /// </summary>
        private static Texture2D Recenter(Texture2D baseTex, Texture2D src, string key, int rings)
        {
            const int EDGE = 14, PLATE = 10, MARGIN = 16, RMARGIN = 1, ROWDIFF = 20, MINGLYPH = 28, PAD = 2;
            int w = src.width, h = src.height;
            if (baseTex.width != w || baseTex.height != h) throw new System.Exception("size differs: base " + baseTex.width + "x" + baseTex.height + ", src " + w + "x" + h);
            Color32[] b = baseTex.GetPixels32();        // 行は下から上 (y=0 が一番下)。上下は対称に扱うので向きは問わない
            Color32[] p = src.GetPixels32();
            // 1. base で最長の同色区間 → 基準列
            int bestL = 0, bestR = -1;
            for (int y = 0; y < h; y++)
            {
                int runL = 0;
                for (int x = 1; x <= w; x++)
                {
                    bool cont = x < w && b[y * w + x].a == 255 && Diff(b[y * w + x], b[y * w + runL]) <= 6;
                    if (!cont)
                    {
                        if (x - 1 - runL > bestR - bestL) { bestL = runL; bestR = x - 1; }
                        runL = x;
                    }
                }
            }
            if (bestR - bestL < w / 3) throw new System.Exception("plate span not found (" + bestL + ".." + bestR + ")");
            int leftRef = bestL + 8, refX = bestR - 8;
            // 2. 行ごとの内側 [lo, hi] (base)
            int[] lo = new int[h], hi = new int[h];
            bool[] plate = new bool[h];
            for (int y = 0; y < h; y++)
            {
                plate[y] = b[y * w + refX].a == 255 && b[y * w + leftRef].a == 255;
                if (!plate[y]) continue;
                int l = leftRef; while (l > 0 && Diff(b[y * w + l - 1], b[y * w + leftRef]) <= PLATE) l--;
                int r = refX; while (r < w - 1 && Diff(b[y * w + r + 1], b[y * w + refX]) <= PLATE) r++;
                lo[y] = l; hi[y] = r;
            }
            // 3. 丸文字か文字のある行 (src と base が違う行)
            int ty0 = -1, ty1 = -1;
            for (int y = 0; y < h; y++)
            {
                if (!plate[y]) continue;
                for (int x = lo[y]; x <= hi[y]; x++) if (Diff(p[y * w + x], b[y * w + x]) > ROWDIFF) { if (ty0 < 0) ty0 = y; ty1 = y; break; }
            }
            if (ty0 < 0) throw new System.Exception("no content rows");
            // 4. 内容の列 (隣との差)、丸文字 / 文字の切り分け
            bool[] content = new bool[w];
            for (int y = ty0; y <= ty1; y++)
            {
                if (!plate[y]) continue;
                for (int x = lo[y] + MARGIN; x <= hi[y] - RMARGIN; x++)
                {
                    Color32 c = p[y * w + x];
                    if (Diff(c, p[y * w + x - 1]) > EDGE || Diff(c, p[y * w + x + 1]) > EDGE) content[x] = true;
                }
            }
            int g0 = -1;
            for (int x = 0; x < w; x++) if (content[x]) { g0 = x; break; }
            if (g0 < 0) throw new System.Exception("no content columns");
            int g1 = -1, gs = g0;
            for (int k = 0; k < rings; k++)
            {
                g1 = -1;
                for (int x = gs + MINGLYPH; x < w; x++) if (!content[x]) { g1 = x - 1; break; }
                if (g1 < 0) throw new System.Exception("ring " + k + " end not found from " + gs);
                gs = -1;
                for (int x = g1 + 1; x < w; x++) if (content[x]) { gs = x; break; }
                if (gs < 0) throw new System.Exception("nothing after ring " + k);
            }
            int t0 = -1, t1 = -1;
            for (int x = g1 + 1; x < w; x++) if (content[x]) { if (t0 < 0) t0 = x; t1 = x; }
            if (t0 < 0) throw new System.Exception("label not found after glyph " + g0 + ".." + g1);
            int labelW = t1 - t0 + 1;
            int mid = (ty0 + ty1) / 2;
            int newT0 = lo[mid] + (hi[mid] - lo[mid] + 1 - labelW) / 2;
            // 5. 描き直し
            Color32[] d = (Color32[])p.Clone();
            for (int y = ty0; y <= ty1; y++)
            {
                if (!plate[y]) continue;
                int row = y * w;
                for (int x = 0; x < g0; x++) d[row + x] = b[row + x];
                int z1 = Mathf.Min(t1 + PAD, hi[y]);
                bool clipped = t1 + PAD > hi[y];
                Color32 za = b[row + Mathf.Max(g0 - 1, 0)], zb = clipped ? b[row + hi[y]] : p[row + z1 + 1];
                for (int x = g0; x <= z1; x++) d[row + x] = Lerp(za, zb, (x - g0 + 1) / (float)(z1 - g0 + 2));
                for (int x = t0 - PAD; x <= t1 + PAD; x++)
                {
                    int nx = x - t0 + newT0;
                    if (x <= g1 || x > hi[y] || nx < lo[y] || nx > hi[y]) continue;
                    Color32 c = p[row + x];
                    if (clipped && x > hi[y] - 3) c = Lerp(c, d[row + nx], (x - (hi[y] - 3)) / 3f);
                    d[row + nx] = c;
                }
            }
            Texture2D t = new Texture2D(w, h, TextureFormat.ARGB32, false);
            t.name = key + "_noglyph";
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = src.wrapMode;
            t.filterMode = src.filterMode;
            t.SetPixels32(d);
            t.Apply(false, false);
            Plugin.Log.LogInfo("[ButtonGlyph] recenter " + key + ": rows " + ty0 + ".." + ty1 + ", interior " + lo[mid] + ".." + hi[mid] + ", glyph " + g0 + ".." + g1 + ", label " + t0 + ".." + t1 + " -> " + newT0);
            return t;
        }
    }

    /// <summary>最初の GameMain.Update (Util.file_regist_on_boot の後) で登録表を差し替える。</summary>
    [HarmonyPatch(typeof(GameMain), "Update")]
    internal static class ButtonGlyphApplyPatch
    {
        private static bool s_done;
        private static bool s_logged;

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (!s_logged) { s_logged = true; Plugin.Log.LogInfo("[ButtonGlyph] patched: GameMain.Update (ボタンの丸文字を消す)"); }
            if (s_done) return;
            s_done = true;
            ButtonGlyph.Apply();
        }
    }
}
