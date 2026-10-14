using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine.Rendering;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 戦闘の味方キャラの絵の大きさを % で変える ([Battle] PlayerScalePercent、既定 107 = スーパーファミコン版と同じ見かけ)。
    ///
    /// 味方は Effect.drawPlayer が Sys.drawImageDepth で 26x26 ドットの絵を等倍 (SetScale(2.25) = 36/16、絵の 1 ドット = 画面の 1 ドット)
    /// で描く。MonsterScalePatch と同じ理由 (リマスター版は画面が縦 240 ドット、SFC 版は縦 224 ドット) で、107 % にすると
    /// SFC 版と同じ見かけの大きさになる。
    ///
    /// drawImageDepth は本体のあちこち (メニューの顔など) から呼ばれるので、drawPlayer の中 (Prefix/Postfix で印を付ける) で
    /// 呼ばれた時だけ、本体と同じ手順を「倍率 × 設定 %」で描き直して本体の描画を止める (Prefix return false。本体の描画を
    /// そのまま通すと等倍の絵が重なって描かれるため)。拡大の基準は足元の中央 (x + 13, y + 26 ドット) で、足元の位置は変わらない。
    /// 足元の影 (drawPlayer が GS.FillRect2 で描く 2 枚の矩形) はドット単位の矩形なのでそのまま (107 % では 9 → 9.6 ドットの差)。
    /// ダメージ数字の位置 (Effect.dam_xy_calc) は拡大前の寸法のままで、拡大の影響を受けない。
    /// 60fps 表示の戦闘用補間 (Interp60Battle) は描画位置の差分で動くので、毎フレーム同じずらし方をするこの拡大とは干渉しない。
    /// </summary>
    internal static class PlayerScale
    {
        public static ConfigEntry<int> Percent;
        public const int MIN = 50, MAX = 200;
        public const int DEFAULT = 107;
        private const float DOT = 36f / 16f;   // 本体の等倍 (Sys.drawImageDepth の SetScale(2.25))
        private static readonly int[] Steps = { 100, 105, 107, 110, 115, 120, 125, 130, 140, 150 };

        private static int s_depth;             // Effect.drawPlayer の中か (再入はしないが念のため数える)

        private static readonly AccessTools.FieldRef<Util.Sprite[]> s_tempSprite = AccessTools.StaticFieldRefAccess<Util.Sprite[]>(AccessTools.Field(typeof(Sys), "m_temp_sprite"));
        private static readonly AccessTools.FieldRef<int> s_spriteIdx = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(Sys), "m_sprite_idx"));

        public static void Bind(ConfigFile config)
        {
            Percent = config.Bind("Battle", "PlayerScalePercent", DEFAULT,
                new ConfigDescription("戦闘の味方キャラの絵の大きさ (%)。100 = 本体どおり。107 = スーパーファミコン版と同じ見かけの大きさ (リマスター版は画面が縦 240 ドットなので 224 ドットのスーパーファミコン版より 7 % 小さく見える)。足元の位置は変わらない", new AcceptableValueRange<int>(MIN, MAX)));
        }

        /// <summary>設定画面用: 用意した段階の中で前後に動かす。段階にない値のときは一番近い段階から動く。</summary>
        public static void Cycle(int delta)
        {
            int v = Percent.Value;
            int best = 0;
            for (int i = 1; i < Steps.Length; i++) if (System.Math.Abs(Steps[i] - v) < System.Math.Abs(Steps[best] - v)) best = i;
            int idx = best + delta;
            if (idx < 0) idx = Steps.Length - 1; else if (idx >= Steps.Length) idx = 0;
            Percent.Value = Steps[idx];
        }

        public static string Label(int v)
        {
            if (v == 100) return "100 % (本体どおり)";
            if (v == 107) return "107 % (スーファミと同じ見かけ)";
            return v + " %";
        }

        internal static void Enter() { s_depth++; }
        internal static void Exit() { if (s_depth > 0) s_depth--; }

        /// <summary>drawPlayer の中で、設定が 100 % 以外なら本体の代わりに描く。描いたら true (本体の描画を止める)。</summary>
        internal static bool TryDraw(int flip, Image img, int dx, int dy, int sx, int sy, int w, int h, int pri, float depth)
        {
            if (s_depth <= 0 || Percent == null || Percent.Value == 100 || img == null) return false;
            Util.Sprite[] ring = s_tempSprite();
            if (ring == null) return false;
            float k = Percent.Value / 100f;
            // 本体の Sys.drawImageDepth と同じ手順 (一時スプライトのリングから 1 つ取り、クリップ・倍率・描画順・深度を設定して描く)
            int idx = s_spriteIdx();
            Util.Sprite sprite = ring[idx];
            s_spriteIdx() = (idx + 1) & 255;
            sprite.InitClip(img, false);
            sprite.SetScale(DOT * k);
            sprite.m_mtl.renderQueue = pri;
            ShaderUtil.SetDepthTest(sprite.m_mtl, CompareFunction.LessEqual);
            ShaderUtil.SetDepthWrite(sprite.m_mtl, true);
            sprite.m_uv_adjust = 0.5f;
            sprite.SetDepth(depth);
            // 足元の中央を基準に拡大: 左上を (幅の差の半分, 高さの差) だけずらす
            float x = Sys.get_screen_x(dx) + w * DOT * (1f - k) * 0.5f;
            float y = Sys.get_screen_y(dy) + h * DOT * (1f - k);
            sprite.DrawEx(flip, x, y, sx, sy, w, h);
            return true;
        }
    }

    /// <summary>Effect.drawPlayer: 中で呼ばれる Sys.drawImageDepth を味方の絵と見分けるための印。</summary>
    [HarmonyPatch(typeof(Effect), nameof(Effect.drawPlayer))]
    internal static class PlayerScaleDrawPlayerPatch
    {
        private static void Prefix() { PlayerScale.Enter(); }
        private static void Postfix() { PlayerScale.Exit(); }
    }

    /// <summary>Sys.drawImageDepth: drawPlayer の中なら拡大して描き、本体の描画を止める (重ね描きを防ぐため Prefix で止める)。</summary>
    [HarmonyPatch(typeof(Sys), nameof(Sys.drawImageDepth))]
    internal static class PlayerScaleDrawImagePatch
    {
        private static bool Prefix(int flip, Image img, int dx, int dy, int sx, int sy, int w, int h, int pri, float depth)
        {
            return !PlayerScale.TryDraw(flip, img, dx, dy, sx, sy, w, h, pri, depth);
        }
    }
}
