using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 弱点をついた時のモンスターのダメージ数字を黄色にする ([Battle] WeakPointDamageColor、既定 On)。本体は白 (HP) / 赤 (LP) / 緑 (回復) の 3 色だけ。
    ///
    /// 本体の仕組み:
    /// - 1 行動の結果は Battle.mbox (11 + 8k から: +0 対象 (0x80 = 敵、下位 = 枠)、+5 結果 (&amp;3: 1 = HP、2 = LP、3 = 回復)、+6 数字)。
    /// - Effect.dam_conv が結果ごとに dam_pal_set(ofs) で dam_data[枠 × 12 + 11] に色の番号を入れる (((結果 &amp; 3) + 4) × 2 | 0x31 = 59 白 / 61 赤 / 63 緑)。
    ///   dam_data の枠は対象そのもの (味方 0〜4、敵は (枠 + 5))。
    /// - Effect.draw は数字を描く前に、その枠の色の番号で dam_roll(col) を呼び、((col − 59) / 2) % 3 で色を選んで点滅 (damrollcnt) を混ぜ、
    ///   BattleUIDamage.SetColor (スマホ版の数字) と sfont のパレット 1 番 (ドット文字の数字と文字) に入れる。
    /// - 弱点の判定は本体の set_defence と同じ: 技の属性 (waza_data[技 × 10 + 4] のビット。0x80 = 斬 … 0x01 = 状) のうち一番低い防御力
    ///   (monsterdata[枠][85..92]) を使う。その値が、その敵の 8 種 (128 以上 = 無効は除く) の中で一番低く、かつ全部同じではない時を弱点とする
    ///   (戦闘の振り返りの「敵の弱点」で赤にするものと同じ)。
    ///
    /// MOD: dam_pal_set の Postfix で、敵への HP ダメージで弱点なら色の番号を 65 (本体が使わない番号) に差し替え、dam_roll の Prefix で 65 の時だけ
    /// 本体と同じ手順で黄色を入れる (元処理は止める。他の番号は本体のまま)。
    /// </summary>
    internal static class WeakPointColor
    {
        public static ConfigEntry<bool> Enabled;
        private const string TAG = "[WeakColor]";
        private const int CODE = 65;                                  // 本体の 59 / 61 / 63 と重ならない色の番号
        private static readonly int[] YELLOW = { 255, 230, 0 };
        private static bool s_logged;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Battle", "WeakPointDamageColor", true,
                "弱点をついた時 (技の属性のうち一番低い防御力が、その敵の 8 種の防御力の中で一番低い時) のモンスターのダメージ数字を黄色にする。Off = 本体どおり (白)");
        }

        public static bool On { get { return Enabled != null && Enabled.Value; } }

        /// <summary>Effect.dam_pal_set(ofs) の Postfix: 敵への HP ダメージで弱点なら色の番号を差し替える。</summary>
        public static void AfterPalSet(Effect e, int ofs)
        {
            if (!On || e == null || e.dam_data == null) return;
            Battle b = Main.battle;
            if (b == null || b.mbox == null || b.A0 == null || ofs + 16 >= b.mbox.Length) return;
            int target = b.mbox[ofs + 11];
            if ((target & 0x80) == 0) return;                          // 味方へのダメージは本体のまま
            if ((b.mbox[ofs + 16] & 3) != 1) return;                   // HP ダメージだけ (LP の赤、回復の緑はそのまま)
            int slot = target & 0x7F;
            int tech = b.A0.Length > 4 ? b.A0[4] : -1;
            if (!IsWeak(tech, slot)) return;
            int entry = (slot + 5) * 12;
            if (entry + 11 >= e.dam_data.Length) return;
            e.dam_data[entry + 11] = CODE;
            if (!s_logged || Plugin.DiagOn)
            {
                s_logged = true;
                Plugin.Log.LogInfo(TAG + " applied: tech " + tech + " hit the weakness of slot " + slot + " (" + b.mbox[ofs + 17] + " damage)");
            }
        }

        /// <summary>技 tech がその枠の敵の弱点をつくか (本体の set_defence の選び方 + 振り返り窓の弱点の定義)。</summary>
        public static bool IsWeak(int tech, int slot)
        {
            byte[] w = Data.waza_data;
            if (w == null || tech < 0 || tech * 10 + 4 >= w.Length) return false;
            if (Work.monsterdata == null || slot < 0 || slot >= Work.monsterdata.Length) return false;
            int[] row = Work.monsterdata[slot];
            if (row == null || row.Length < 93) return false;
            int attr = w[tech * 10 + 4] & 0xFF;
            if (attr == 0 || attr == 255) return false;
            int min = 255, max = -1, eff = 255;
            for (int k = 0; k < 8; k++)
            {
                int v = row[85 + k] & 0xFF;
                if (v >= 128) continue;
                if (v < min) min = v;
                if (v > max) max = v;
                if ((attr & (0x80 >> k)) != 0 && v < eff) eff = v;
            }
            return min < 255 && max > min && eff == min;
        }

        /// <summary>Effect.dam_roll(col) の Prefix: 弱点の番号なら本体と同じ手順で黄色を入れ、元処理を止める。</summary>
        public static bool BeforeRoll(Effect e, int col)
        {
            if (col != CODE) return true;
            int num = e.damrollcnt & 0x7F;
            if ((e.damrollcnt & 0x80) == 0) num = 127 - num;
            int[] c = new int[3];
            for (int i = 0; i < 3; i++) c[i] = (YELLOW[i] * (255 - num) + 64 * num) / 255;
            BattleUIDamage.SetColor(c[0] / 255f, c[1] / 255f, c[2] / 255f);
            Sys.File f = Sys.file != null && Sys.file.Length > 168 ? Sys.file[168] : null;
            if (f != null && f.pal != null)
            {
                f.pal.setEntry(1, DGraphics.getColorOfRGB(c[0], c[1], c[2]));
                f.pal.setEntry(2, DGraphics.getColorOfRGB(16, 16, 16));
            }
            Sys.font8_type = -1;
            return false;
        }

        public static void LogPatched()
        {
            Plugin.Log.LogInfo(TAG + " patched: Effect.dam_pal_set / dam_roll (WeakPointDamageColor=" + On + ")");
        }
    }

    [HarmonyPatch(typeof(Effect), "dam_pal_set")]
    internal static class WeakPointColor_dam_pal_set_Patch
    {
        private static void Postfix(Effect __instance, int ofs) { WeakPointColor.AfterPalSet(__instance, ofs); }
    }

    [HarmonyPatch(typeof(Effect), "dam_roll")]
    internal static class WeakPointColor_dam_roll_Patch
    {
        private static bool Prefix(Effect __instance, int col) { return WeakPointColor.BeforeRoll(__instance, col); }
    }
}
