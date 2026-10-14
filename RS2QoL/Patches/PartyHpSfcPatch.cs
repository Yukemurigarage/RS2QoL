using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 戦闘のコマンド入力中に出る味方の HP / LP を、スーパーファミコン版と同じ「現在値/最大値」の 2 行で、
    /// SFC 版と同じ細い数字 (白・灰の縁・黒の影の 8x8 ドット) で出す ([Battle] PartyHpSfc、既定 On)。
    ///
    /// 本体の Battle.disp_charhp は現在値だけを BattleUIDamage.DrawStatus (スマホ版の高解像度数字) で描き、
    /// 最大値は色 (白 / 半分未満で橙 / 0 で赤) の判定にしか使わない。これを Prefix で丸ごと置き換える。
    ///
    /// 並べ方は SFC 版の画面のスクリーンショット (リポジトリには入れていない、1 ドット = 6px) を測って合わせた:
    /// - 文字は 8px 送り。「/」のタイルの左端 = 編成座標 x + 8 (キャラ枠 24px の中央 - 4)。
    ///   現在値は「/」の左に右詰め、最大値は右に左詰め (HP 3 桁 / LP 2 桁)。
    /// - タイルの上端は HP が y + 15、LP が y + 23 (タイルの 0 行目は灰の縁、数字の本体は 1〜6 行目、7 行目は影)。
    /// - 同じ段に 32px 間隔で 3 人並ぶ (インペリアルクロスの中列など) と幅 56px の文字列が重なるので、
    ///   SFC 版は真ん中の 1 人だけ 16px 上 (キャラに重ねて) に出す。同じ段で隣が 32px 以内にいる人を
    ///   左から順に 1 人おきに上げる (3 人なら真ん中だけ、2 人なら右側)。
    /// - 数字の形は SFC 版の画面から起こした。ゲーム内の sfont.img (太い 2 ドット幅の数字) とは別物なので、
    ///   自前の 8x8 タイル表 (0〜9 と「/」) をパレット画像として作り、本体のダメージ数字と同じ Util.Sprite で描く。
    /// 色は本体と同じ判定で、タイル全体に乗算する。
    /// </summary>
    internal static class PartyHpSfc
    {
        public static ConfigEntry<bool> Enabled;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Battle", "PartyHpSfc", true,
                "戦闘のコマンド入力中の味方の HP / LP を、スーパーファミコン版と同じ「現在値/最大値」のドット文字で出す。Off = スマホ版の大きい数字で現在値だけ (本体どおり)");
        }

        private const int Pitch = 8;        // SFC 版と同じ 8px 送り
        private const int SlashX = 8;       // 「/」のタイル左端 = 編成座標 x + 8
        private const int HpY = 15;         // HP の行 (タイル上端。数字の本体は y+16 から)
        private const int LpY = 23;         // LP の行
        private const int RaiseY = 16;      // 隣と重なる人はこれだけ上に出す (SFC 版: インペリアルクロスの中列中央)
        private const int NeighborX = 32;   // 同じ段でこの距離以内に隣がいると重なる (文字列の幅 56px > 32px)
        private const int Scale9 = 36;      // Util.Sprite の拡大率 2.25 = 36/16 (本体の drawImageDamage と同じ)

        // 8x8 タイル。'.' = 透明、'#' = 白 (色を乗算)、'+' = 灰の縁、'o' = 黒の影。10 = 「/」。
        private static readonly string[] Font =
        {
            "...++..." + "...##+.." + ".+#o+#o." + ".+#o+#o." + ".+#o+#o." + ".+#+.#o." + "..+##o.." + "...oo...",   // 0
            "....+..." + "....#o.." + "..+##o.." + "..o+#o.." + "...+#o.." + "...+#o.." + "...+#o.." + "....o...",   // 1
            "...++..." + "...##+.." + ".+#o+#o." + "..o..#o." + "....#oo." + "...#++.." + ".+####o." + "..oooo..",   // 2
            "...++..." + "...##+.." + ".+#o.#o." + ".oo+##o." + "..+.+#o." + ".+#+.#o." + "..+##o.." + "...oo...",   // 3
            "....+..." + "....#o.." + "...##o.." + "..#+#o.." + "+#+.#+.." + "+#####o." + ".oo+#o.." + "....o...",   // 4
            "..++++.." + ".+####o." + ".+#++o.." + ".+###+.." + "..+o+#o." + ".+#+.#o." + "..+##o.." + "...oo...",   // 5
            "...++..." + "...##+.." + ".+#o+#o." + ".+#+++.." + ".+####o." + ".+#+.#o." + "..+##o.." + "...oo...",   // 6
            "..++++.." + ".+####o." + ".+#o.#o." + "..o+#o.." + "...+#o.." + "...+#o.." + "...+#o.." + "....o...",   // 7
            "...++..." + "...##+.." + ".+#o+#o." + "..+##o.." + ".+#o+#o." + ".+#+.#o." + "..+##o.." + "...oo...",   // 8 (SFC 版の画面に無かったので 0 と 3 から起こした)
            "...++..." + "...##+.." + ".+#o+#o." + ".+#+.#o." + "..+###o." + "....#o.." + "..+#o..." + "..oo....",   // 9
            ".......#" + "......#o" + ".....#o." + "....#o.." + "...#o..." + "..#o...." + ".#o....." + "#o......",   // /
        };
        private const int Slash = 10;
        private const int TileStride = 9;   // タイルの間に透明の 1 列 (隣のタイルがにじまないように)
        private const int TileTop = 1;      // 上下にも透明の 1 行
        private const int SpriteCount = 80; // 5 人 x 2 行 x 最大 7 文字 = 70

        private static Image s_img;
        private static Util.Sprite[] s_sprites;
        private static bool[] s_ready;
        private static int s_idx;

        private static bool EnsureImage()
        {
            if (s_img != null && s_img.m_index_tex != null && s_img.m_palette_tex != null) return true;
            int w = Font.Length * TileStride;
            int h = 8 + TileTop * 2;
            Image img = Image.createImage2(w, h);
            if (img == null || img.m_px == null) return false;
            for (int t = 0; t < Font.Length; t++)
            {
                string tile = Font[t];
                for (int yy = 0; yy < 8; yy++)
                    for (int xx = 0; xx < 8; xx++)
                    {
                        byte idx = 0;
                        switch (tile[yy * 8 + xx])
                        {
                            case '#': idx = 1; break;
                            case '+': idx = 2; break;
                            case 'o': idx = 3; break;
                        }
                        img.m_px[(yy + TileTop) * w + t * TileStride + xx] = idx;
                    }
            }
            Color32[] pal = new Color32[256];
            pal[0] = new Color32(0, 0, 0, 0);
            pal[1] = new Color32(255, 255, 255, 255);
            pal[2] = new Color32(48, 48, 48, 255);
            pal[3] = new Color32(32, 32, 32, 255);
            img.Apply(pal);
            img.m_index_tex.wrapMode = TextureWrapMode.Clamp;
            img.m_palette_tex.wrapMode = TextureWrapMode.Clamp;
            s_img = img;
            // 画像を作り直したらスプライトも貼り直す
            s_ready = new bool[SpriteCount];
            return true;
        }

        private static Util.Sprite GetSprite()
        {
            if (s_sprites == null)
            {
                s_sprites = new Util.Sprite[SpriteCount];
                s_ready = new bool[SpriteCount];
            }
            int i = s_idx++;
            if (s_idx >= SpriteCount) s_idx = 0;
            Util.Sprite sp = s_sprites[i];
            if (sp == null)
            {
                sp = new Util.Sprite(true);
                s_sprites[i] = sp;
                s_ready[i] = false;
            }
            if (!s_ready[i] || sp.m_mtl == null)
            {
                // 本体の drawImageDamage と同じ設定。m_uv_adjust は 0 にして、タイルをドット単位でそのまま 2.25 倍にする
                // (本体の 0.5 はタイルを半ドットずらして 8/7 倍に伸ばすので、SFC 版と位置がずれる)。
                sp.Init(s_img, false);
                sp.SetScale(Scale9 / 16f);
                sp.m_mtl.renderQueue = 6000;
                sp.SetUVAdjust(0f);
                s_ready[i] = true;
            }
            return sp;
        }

        internal static void Draw(Battle b)
        {
            if (!EnsureImage()) return;
            int n = b.member;
            int[] xs = new int[n], ys = new int[n];
            for (int i = 0; i < n; i++)
            {
                if (b.my_formation < 16)
                {
                    xs[i] = (Data.chara_arrange_data[(b.my_formation * 5 + i) * 2] & 0xFF) * 8;
                    ys[i] = (Data.chara_arrange_data[(b.my_formation * 5 + i) * 2 + 1] & 0xFF) * 8;
                }
                else
                {
                    xs[i] = NewData.m_FormationBattlePos[b.my_formation - 16, i, 0] * 8;
                    ys[i] = NewData.m_FormationBattlePos[b.my_formation - 16, i, 1] * 8;
                }
            }
            bool[] raised = Raised(xs, ys);
            for (int i = 0; i < n; i++)
            {
                int[] m = Work.memberdata_b[i];
                if ((m[7] & 0xC0) != 0) continue;
                int slashX = xs[i] + SlashX;
                int y = ys[i] - (raised[i] ? RaiseY : 0);
                DrawLine(m[14], m[16], 3, slashX, y + HpY);
                DrawLine(m[18], m[19], 2, slashX, y + LpY);
            }
        }

        /// <summary>同じ段 (y が同じ) で隣が NeighborX 以内にいる人を、左から順に 1 人おきに上げる。</summary>
        private static bool[] Raised(int[] xs, int[] ys)
        {
            int n = xs.Length;
            bool[] raised = new bool[n];
            // 左から順に見る (人数は最大 5 なので単純な選択)
            bool[] done = new bool[n];
            for (int step = 0; step < n; step++)
            {
                int best = -1;
                for (int i = 0; i < n; i++)
                    if (!done[i] && (best < 0 || xs[i] < xs[best])) best = i;
                done[best] = true;
                // 自分より左で同じ段、NeighborX 以内にいる人のうち一番近い人
                int prev = -1;
                for (int j = 0; j < n; j++)
                    if (j != best && ys[j] == ys[best] && xs[j] < xs[best] && xs[best] - xs[j] <= NeighborX && (prev < 0 || xs[j] > xs[prev])) prev = j;
                raised[best] = prev >= 0 && !raised[prev];
            }
            return raised;
        }

        /// <summary>「cur/max」を 1 行。slashX = 「/」のタイルの左端。cur は「/」の左に右詰め、max は右に左詰め。</summary>
        private static void DrawLine(int cur, int max, int digits, int slashX, int y)
        {
            Color color = Color.white;
            if (cur < max / 2) color = new Color(1f, 128 / 255f, 0f);
            if (cur == 0) color = new Color(1f, 0f, 0f);
            int limit = digits == 3 ? 999 : 99;
            if (cur > limit) cur = limit;
            if (max > limit) max = limit;

            int n = BattleUIDamage.GetDigit(cur);
            for (int k = 0; k < n; k++)
                Glyph(color, BattleUIDamage.GetDigitValue(cur, k + 1), slashX - (k + 1) * Pitch, y);
            Glyph(color, Slash, slashX, y);
            n = BattleUIDamage.GetDigit(max);
            for (int k = 0; k < n; k++)
                Glyph(color, BattleUIDamage.GetDigitValue(max, n - k), slashX + (k + 1) * Pitch, y);
        }

        private static void Glyph(Color color, int tile, int x, int y)
        {
            Util.Sprite sp = GetSprite();
            sp.SetColor(color);
            sp.DrawEx(0, Sys.get_screen_x(x), Sys.get_screen_y(y), tile * TileStride, TileTop, 8, 8);
        }
    }

    /// <summary>
    /// Battle.disp_charhp の Prefix。SFC 版の形で描いて元処理は止める
    /// (元処理はスマホ版の数字を別のスプライトで描くので、両方出さないために止める必要がある)。
    /// </summary>
    [HarmonyPatch(typeof(Battle), "disp_charhp")]
    internal static class PartyHpSfcPatch
    {
        private static bool Prefix(Battle __instance)
        {
            if (PartyHpSfc.Enabled == null || !PartyHpSfc.Enabled.Value) return true;
            PartyHpSfc.Draw(__instance);
            return false;
        }
    }
}
