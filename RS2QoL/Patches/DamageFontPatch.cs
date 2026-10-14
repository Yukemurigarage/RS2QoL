using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// ダメージ数字をスーパーファミコン版のドット文字で出す ([Battle] DamageDigitsSfc、既定 On) と、
    /// その数字の大きさ ([Battle] DamageDigitsScalePercent、既定 100 = ドット等倍)。
    ///
    /// 本体の Effect.draw のダメージ描画ループは、グリフ番号 0〜9 (数字) だけを BattleUIDamage.Draw (スマホ版の高解像度画像
    /// btl_num/num_N.png) で描き、それ以外 (ミス・かいひ等) は Sys.drawImageDamage で sfont.img (9x9 ドットの文字表) から描く。
    /// sfont.img の 5 行目 (sy = 36) の 0〜9 列に元のダメージ用の数字が残っているので、Effect.draw の中で呼ばれた
    /// BattleUIDamage.Draw だけを、その数字を描くように差し替える。
    /// 色 (白・赤・回復の緑と点滅) は Effect.dam_roll が sfont のパレット 1 番にも入れているので、それをそのまま使う。
    /// 味方の HP 表示 (Battle.disp_charhp) も同じ Draw を使うが、Effect.draw の外なので変えない。
    ///
    /// 大きさ: 本体の drawImageDamage は m_uv_adjust = 0.5 (半ドット内側から取る) で 9x9 の枠に描くので、8 ドットぶんの絵が
    /// 9 ドットに伸びて (112.5 %)、半ドットずれる。ここでは自前のスプライト (m_uv_adjust = 0) で描き、100 % = 絵の 1 ドットが
    /// 画面の 1 ドット (sfont の数字は縁込みで幅 8・高さ 9 ドット、送りは本体のまま 9 ドット)。% を変えるときは数字の並び全体の
    /// 中央・下端を基準に、送りも同じ倍率で縮める / 伸ばす。何桁目かは本体の dam_data (基準位置と桁数) から逆算する。
    /// </summary>
    internal static class DamageFont
    {
        public static ConfigEntry<bool> Sfc;
        public static ConfigEntry<int> ScalePercent;
        public const int MIN = 50, MAX = 200;
        private static readonly int[] Steps = { 75, 80, 90, 100, 107, 112, 125, 150 };

        public static void Bind(ConfigFile config)
        {
            Sfc = config.Bind("Battle", "DamageDigitsSfc", true,
                "ダメージ数字をスーパーファミコン版のドット文字 (sfont.img の数字) で出す。Off = スマホ版の大きい数字 (本体どおり)");
            ScalePercent = config.Bind("Battle", "DamageDigitsScalePercent", 100,
                new ConfigDescription("スーパーファミコン版のドット文字にしたダメージ数字の大きさ (%)。100 = 絵の 1 ドットが画面の 1 ドット (縁込みで幅 8・高さ 9 ドット)。107 = スーパーファミコン版と同じ見かけ (画面が縦 240 ドットなので 224 ドットの SFC 版より 7 % 小さく見えるぶん)。112 = 本体の描き方 (半ドットずれて 9/8 に伸びる) と同じ大きさ。DamageDigitsSfc が Off のときは効かない", new AcceptableValueRange<int>(MIN, MAX)));
        }

        /// <summary>設定画面用: 用意した段階の中で前後に動かす。段階にない値のときは一番近い段階から動く。</summary>
        public static void Cycle(int delta)
        {
            int v = ScalePercent.Value;
            int best = 0;
            for (int i = 1; i < Steps.Length; i++) if (System.Math.Abs(Steps[i] - v) < System.Math.Abs(Steps[best] - v)) best = i;
            int idx = best + delta;
            if (idx < 0) idx = Steps.Length - 1; else if (idx >= Steps.Length) idx = 0;
            ScalePercent.Value = Steps[idx];
        }

        public static string Label(int v)
        {
            if (v == 100) return "100 % (ドット等倍)";
            if (v == 107) return "107 % (スーファミと同じ見かけ)";
            if (v == 112) return "112 % (本体の伸び方)";
            return v + " %";
        }

        internal static int s_effectDepth;   // Effect.draw の中か
        internal static Effect s_effect;     // 今描いている Effect (dam_data を見るため)

        private const int Cell = 9;          // sfont の 1 文字の枠 (= 本体の数字の送り)
        private const int SpriteCount = 64;
        private static Util.Sprite[] s_sprites;
        private static int s_idx;
        private static int s_lastEntry = -1, s_lastIndex = -1;

        /// <summary>
        /// Effect.draw が BattleUIDamage.Draw(value, x, y) に渡した x, y から、dam_data のどの数字列の何桁目かを逆算する。
        /// (x = 基準 x + 桁 × 9、y = 基準 y − 跳ね (0〜16))。同じ Effect.draw の中では前の桁の次を優先する。
        /// </summary>
        private static bool Locate(int x, int y, out int entry, out int index)
        {
            entry = -1; index = -1;
            Effect e = s_effect;
            if (e == null || e.dam_data == null) return false;
            int[] d = e.dam_data;
            for (int n = 0; n < 15; n++)
            {
                int count = d[n * 12 + 2];
                if (count <= 0 || count > 4) continue;
                int dx = x - d[n * 12];
                if (dx < 0 || dx % Cell != 0) continue;
                int i = dx / Cell;
                if (i >= count) continue;
                int dy = d[n * 12 + 1] - y;
                if (dy < 0 || dy > 16) continue;
                if (n == s_lastEntry && i == s_lastIndex + 1) { entry = n; index = i; break; }
                if (entry < 0) { entry = n; index = i; }
            }
            if (entry < 0) return false;
            s_lastEntry = entry; s_lastIndex = index;
            return true;
        }

        private static Util.Sprite GetSprite(Image img)
        {
            if (s_sprites == null) s_sprites = new Util.Sprite[SpriteCount];
            int i = s_idx++;
            if (s_idx >= SpriteCount) s_idx = 0;
            Util.Sprite sp = s_sprites[i];
            if (sp == null)
            {
                sp = new Util.Sprite(true);
                s_sprites[i] = sp;
            }
            // 本体の drawImageDamage と同じく毎回 Init する (sfont の画像は読み直されることがある)。m_uv_adjust だけ 0 にする
            sp.Init(img, false);
            sp.SetUVAdjust(0f);
            return sp;
        }

        /// <summary>sfont の数字 value を、本体が指定した (x, y) (9x9 の枠の左上、ゲームのドット座標) に設定の倍率で描く。</summary>
        internal static void Draw(int value, int x, int y)
        {
            Sys.File f = Sys.file[168];
            if (f == null || f.pimg == null || f.pal == null) return;
            float s = (ScalePercent != null ? ScalePercent.Value : 100) / 100f;
            float fx = x, fy = y;
            if (s != 1f)
            {
                int entry, index;
                if (Locate(x, y, out entry, out index))
                {
                    // 数字の並びの中央 (横) と、この桁の下端 (縦) を基準に伸び縮みさせる
                    int[] d = s_effect.dam_data;
                    float center = d[entry * 12] + d[entry * 12 + 2] * Cell / 2f;
                    fx = center + (x - center) * s;
                }
                else
                {
                    fx = x + Cell / 2f - Cell / 2f * s;
                }
                fy = y + Cell - Cell * s;
            }
            Util.Sprite sp = GetSprite(f.pimg);
            sp.SetScale(36f / 16f * s);
            sp.m_mtl.renderQueue = 6000;
            Color32 c = f.pal.m_pal[1];
            sp.SetColor(new Color(c.r / 255f, c.g / 255f, c.b / 255f));
            float sx = (Util.SCREEN_W - 540) / 2f + fx * 36f / 16f;
            float sy = (Util.SCREEN_H - 540) / 2f + fy * 36f / 16f;
            sp.DrawEx(0, sx, sy, value * Cell, 4 * Cell, Cell, Cell);
        }
    }

    [HarmonyPatch(typeof(Effect), nameof(Effect.draw))]
    internal static class DamageFontEffectScopePatch
    {
        private static void Prefix(Effect __instance) { DamageFont.s_effectDepth++; DamageFont.s_effect = __instance; }
        private static void Finalizer() { if (DamageFont.s_effectDepth > 0) DamageFont.s_effectDepth--; }
    }

    /// <summary>
    /// BattleUIDamage.Draw(value, x, y) の Prefix。高解像度画像の代わりに sfont の数字を描き、元処理は止める
    /// (元処理は別のスプライトで高解像度画像を描くので、両方出さないために止める必要がある)。
    /// </summary>
    [HarmonyPatch(typeof(BattleUIDamage), nameof(BattleUIDamage.Draw), typeof(int), typeof(int), typeof(int))]
    internal static class DamageFontPatch
    {
        private static bool Prefix(int Value, int x, int y)
        {
            if (DamageFont.Sfc == null || !DamageFont.Sfc.Value || DamageFont.s_effectDepth == 0) return true;
            if (Value < 0 || Value > 9) return true;
            Sys.File f = Sys.file[168];
            if (f == null || f.pimg == null || f.pal == null) return true;
            DamageFont.Draw(Value, x, y);
            return false;
        }
    }
}
