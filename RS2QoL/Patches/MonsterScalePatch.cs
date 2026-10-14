using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 戦闘のモンスターの絵の大きさを % で変える ([Battle] MonsterScalePercent、既定 107 = スーパーファミコン版と同じ見かけ)。
    ///
    /// 本体はゲーム画面を 240 ドット角として描き、1600x900 の窓では 1 ドットが 3.75 画面ピクセルになる。
    /// スーパーファミコン版は縦 224 ドットを同じ窓の高さに出すので 1 ドットが約 4.02 画面ピクセルになり、
    /// 同じ絵でもリマスター版は見かけが 224/240 ≒ 93 % と少し小さい。モンスターの絵の 1 ドットはスーパーファミコン版と
    /// 同じ (Monster.Load が SetScale(2.25) = 36/16 で等倍にしている) ので、107 % にするとスーパーファミコン版と
    /// ほぼ同じ見かけの大きさになる。味方や背景はそのままなので、モンスターだけ相対的に大きくなる点は承知の上で使う。
    ///
    /// Monster.Load(int) の直後に本体が決めた倍率 (ふつう 2.25、No.294 だけ 2.5) を覚え、Monster.Draw の直前に
    /// 「覚えた倍率 × 設定 %」を m_scl に入れる (毎フレーム入れるので設定画面で変えると戦闘中でもすぐ反映される)。
    /// 位置 (m_pos) は Monster.SetPos が足元の中央に置くので、拡大しても足元の位置は変わらない。
    /// ダメージ数字や技のエフェクトの位置は本体の配置表 (拡大前の寸法) のままで、拡大の影響を受けない。
    /// </summary>
    internal static class MonsterScale
    {
        public static ConfigEntry<int> Percent;
        public const int DEFAULT = 107;
        public const int MIN = 50, MAX = 200;
        private static readonly int[] Steps = { 100, 105, 107, 110, 115, 120, 125, 130, 140, 150 };

        private static readonly Dictionary<Monster, float> s_base = new Dictionary<Monster, float>();

        public static void Bind(ConfigFile config)
        {
            Percent = config.Bind("Battle", "MonsterScalePercent", DEFAULT,
                new ConfigDescription("戦闘のモンスターの絵の大きさ (%)。100 = 本体どおり。既定 107 = スーパーファミコン版と同じ見かけの大きさ (リマスター版は画面が縦 240 ドットなので 224 ドットのスーパーファミコン版より 7 % 小さく見える)", new AcceptableValueRange<int>(MIN, MAX)));
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

        internal static void Remember(Monster m)
        {
            s_base[m] = m.m_scl.x;
        }

        internal static void Forget(Monster m)
        {
            s_base.Remove(m);
        }

        internal static void Apply(Monster m)
        {
            float b;
            if (Percent == null || !s_base.TryGetValue(m, out b)) return;
            float s = b * Percent.Value / 100f;
            m.m_scl.x = s;
            m.m_scl.y = s;
        }
    }

    /// <summary>Monster.Load(int): 本体が決めた倍率を覚える。</summary>
    [HarmonyPatch(typeof(Monster), "Load", typeof(int))]
    internal static class MonsterScaleLoadPatch
    {
        private static void Postfix(Monster __instance)
        {
            MonsterScale.Remember(__instance);
        }
    }

    /// <summary>Monster.Term: 覚えた倍率を捨てる (Load の冒頭でも呼ばれるが、直後に Load の Postfix で覚え直す)。</summary>
    [HarmonyPatch(typeof(Monster), "Term")]
    internal static class MonsterScaleTermPatch
    {
        private static void Postfix(Monster __instance)
        {
            MonsterScale.Forget(__instance);
        }
    }

    /// <summary>Monster.Draw: 描く直前に「本体の倍率 × 設定 %」を入れる。</summary>
    [HarmonyPatch(typeof(Monster), "Draw")]
    internal static class MonsterScaleDrawPatch
    {
        private static void Prefix(Monster __instance)
        {
            MonsterScale.Apply(__instance);
        }
    }
}
