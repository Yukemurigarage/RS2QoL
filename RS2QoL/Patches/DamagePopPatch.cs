using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// ダメージ数字の表示の速さ ([Battle] DamageAnimStep = 跳ねの刻み、既定 2。[Battle] DamageHoldStep = 消えるまでの刻み、既定 4。
    /// どちらも 4 = 本体どおり、1 = いちばんゆっくり。1 はテーブルの設計どおりの速さだが、実際に比べるとスーパーファミコン版より遅い)。
    ///
    /// 本体のダメージ数字は Effect.dam_poyon で「桁ごとのカウンタ (16, 20, 24, 28 から 0 へ) を 1 論理フレームに 4 減らし、
    /// 跳ねテーブル (17 段、1px 刻み) をカウンタで引く」(dam_poyon_sub) → 全部 0 になったら「保持カウンタ (15 か 30、
    /// 本体のメッセージ速度設定で変わる) を 1 論理フレームに 4 減らし、0 で消す」。跳ねテーブルが 1px 刻み 17 段なのに
    /// 4 ずつ飛ばすので、跳ねは 5 フレーム・桁の出現差は 1 フレーム・保持は 4〜8 フレームで、スーパーファミコン版
    /// (1 ずつ進める: 跳ね 16 フレーム、桁が 4 フレームおきに順に出る、保持 15〜30 フレーム) よりかなり速い。
    /// この刻み (4) を跳ねと保持で別々に 1〜4 にできるようにする。
    ///
    /// dam_poyon は 1 論理フレームに 1 回呼ばれる。dam_poyon_sub は Prefix で元処理を止めて同じ処理を刻みだけ変えて行う
    /// (定数が埋め込まれているため)。保持は dam_poyon の中の `proc[procstg][1] -= 4` なので、Prefix で先に (4 - 刻み) を足して
    /// 差し引き刻み分だけ減るようにする。保持に入るフレーム (跳ねが終わった同じ呼び出しで保持の減算まで走る) は Postfix で足し戻す。
    ///
    /// 倍速中は論理フレームが実時間 33ms に倍率回回るので、同じ刻みだと見た目が倍率分速くなる。[Battle] DamageAnimRealTime (既定 ON) なら
    /// 設定値を「等倍の時の刻み」として扱い、倍速中は 1 論理フレームあたり (刻み / 倍率) だけ進める。整数でないぶんは累積して
    /// 余りが倍率に達したフレームで 1 多く進める (刻み 3 の x2 なら 1, 2, 1, 2 …)。これで実時間の見た目が等倍と同じになる。
    /// </summary>
    internal static class DamagePop
    {
        public static ConfigEntry<int> Step;
        public static ConfigEntry<int> HoldStep;
        public static ConfigEntry<bool> RealTime;

        /// <summary>この論理フレームで跳ねのカウンタを減らす量 (dam_poyon の Prefix で決める)。</summary>
        public static int BounceThisFrame = 4;
        /// <summary>この論理フレームで保持のカウンタを減らす量。</summary>
        public static int HoldThisFrame = 4;
        private static int s_bounceAcc;
        private static int s_holdAcc;

        public static void Bind(ConfigFile config)
        {
            Step = config.Bind("Battle", "DamageAnimStep", 2,
                new ConfigDescription("ダメージ数字の跳ねの速さ (1〜4、既定 2)。小さいほど跳ねがゆっくりで桁が順に出る。4 が本体どおり、1 がいちばんゆっくり (跳ね 16 フレーム。スーパーファミコン版より遅い)。DamageAnimRealTime が true なら等倍の時の値", new AcceptableValueRange<int>(1, 4)));
            HoldStep = config.Bind("Battle", "DamageHoldStep", 4,
                new ConfigDescription("ダメージ数字が跳ね終わってから消えるまでの長さ (1〜4、既定 4)。4 が本体どおり (4〜8 フレーム)、小さいほど長く残る (1 で 15〜30 フレーム)。DamageAnimRealTime が true なら等倍の時の値", new AcceptableValueRange<int>(1, 4)));
            RealTime = config.Bind("Battle", "DamageAnimRealTime", true,
                "倍速中もダメージ数字の跳ねと消えるまでの見た目の速さを等倍の時と同じに保つ (上の 2 つの値を倍率で割って進める)。false なら倍速中は倍率分速くなる (本体どおりの挙動)");
        }

        public static string Label(int step)
        {
            switch (step)
            {
                case 1: return "1 (いちばんゆっくり)";
                case 4: return "4 (本体どおり)";
                default: return step.ToString();
            }
        }

        public static string HoldLabel(int step)
        {
            switch (step)
            {
                case 1: return "1 (いちばん長い)";
                case 4: return "4 (本体どおり)";
                default: return step.ToString();
            }
        }

        private static int Cycled(int cur, int delta)
        {
            int v = cur + delta;
            if (v < 1) v = 4; else if (v > 4) v = 1;
            return v;
        }

        public static void Cycle(int delta) { Step.Value = Cycled(Step.Value, delta); }
        public static void CycleHold(int delta) { HoldStep.Value = Cycled(HoldStep.Value, delta); }

        /// <summary>設定が読めているか (Bind 前なら本体どおりに動かす)。</summary>
        public static bool Active { get { return Step != null && HoldStep != null && RealTime != null; } }

        /// <summary>新しいダメージ表示が始まる時に累積をリセットする (前回の余りを持ち越さない)。</summary>
        public static void ResetAccum()
        {
            s_bounceAcc = 0;
            s_holdAcc = 0;
        }

        /// <summary>この論理フレームの刻みを決める (dam_poyon の Prefix から、1 フレームに 1 回)。</summary>
        public static void BeginFrame()
        {
            // 倍率は % (100 = 等倍、戦闘は 150 のような端数もある) なので、刻み × 100 を累積して % で割る
            int pct = RealTime.Value ? GameSpeed.EffectivePercent() : 100;
            if (pct < 100) pct = 100;
            s_bounceAcc += Step.Value * 100;
            BounceThisFrame = s_bounceAcc / pct;
            s_bounceAcc %= pct;
            s_holdAcc += HoldStep.Value * 100;
            HoldThisFrame = s_holdAcc / pct;
            s_holdAcc %= pct;
        }
    }

    /// <summary>Effect.dam_poyon_sub: 桁のカウンタを 4 ではなくこのフレームの刻み分だけ減らす (元処理を止める理由: 4 が定数で埋め込まれている)。</summary>
    [HarmonyPatch(typeof(Effect), "dam_poyon_sub")]
    internal static class DamagePopSubPatch
    {
        private static bool Prefix(Effect __instance, int ofs)
        {
            if (!DamagePop.Active) return true;
            int[] d = __instance.dam_data;
            int step = DamagePop.BounceThisFrame;
            int i;
            for (i = 0; i < d[ofs + 2]; i++)
            {
                d[ofs + 7 + i] -= step;
                if (d[ofs + 7 + i] < 0) d[ofs + 7 + i] = 0;
            }
            for (; i < 4; i++) d[ofs + 7 + i] = 0;
            return false;
        }
    }

    /// <summary>Effect.dam_poyon: このフレームの刻みを決め、保持カウンタの減りを 4 からこのフレームの刻みに変える。</summary>
    [HarmonyPatch(typeof(Effect), "dam_poyon")]
    internal static class DamagePopHoldPatch
    {
        private static void Prefix(Effect __instance, out int __state)
        {
            int[] p = __instance.proc[__instance.procstg];
            __state = p[0];
            if (!DamagePop.Active) return;
            if (p[0] == 0) DamagePop.ResetAccum();
            DamagePop.BeginFrame();
            if (p[0] == 20 && p[1] > 0) p[1] += 4 - DamagePop.HoldThisFrame;
        }

        private static void Postfix(Effect __instance, int __state)
        {
            if (!DamagePop.Active) return;
            int[] p = __instance.proc[__instance.procstg];
            // この呼び出しで保持に入った (跳ね終了 → 保持カウンタ設定 → 同じ呼び出しで 4 減算) なら差分を足し戻す
            if (__state != 20 && p[0] == 20 && p[1] > 0) p[1] += 4 - DamagePop.HoldThisFrame;
        }
    }

    /// <summary>
    /// Effect.draw の Postfix: ダメージ数字の色の脈動 (dam_roll: damrollcnt を 1 論理フレームに 64 進めて 4 フレーム周期で
    /// 白と灰色を往復) を倍速中も等速と同じ速さに保つ。倍速は論理フレームを 1 秒に 60〜120 回回すので、そのままだと
    /// 脈動が 2〜4 倍の速さのちらつきになる。本体が 64 足した後に (64 - 64/倍率) 引いて、実時間あたりの進みを等速と同じにする。
    /// </summary>
    [HarmonyPatch(typeof(Effect), nameof(Effect.draw))]
    internal static class DamagePopRollSpeedPatch
    {
        private static void Postfix(Effect __instance)
        {
            if (__instance.damdisp == 0) return;
            int pct = GameSpeed.EffectivePercent();
            if (pct <= 100) return;
            __instance.damrollcnt -= 64 - 64 * 100 / pct;
        }
    }
}
