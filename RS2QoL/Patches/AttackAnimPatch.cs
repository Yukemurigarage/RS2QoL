using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 技・攻撃アニメ (戦闘の効果スクリプト) の速さ ([Battle] AttackAnimSpeed。0 = 倍速に従う (本体どおり) /
    /// 25〜400 = 倍速に関係なく、等倍の本体を 100 とした % の実時間の速さに固定。既定 0)。
    ///
    /// 本体の技・攻撃のアニメは efe_anm (スーパーファミコン版と同じ形式のスクリプト) を Effect.prosece → pro_com_loop で
    /// 解釈して動かす。pro_com_loop は 1 論理フレームにスクリプトを animspeed コマ進め、prosece はこれを常に 2 にしている
    /// (エンディングのデモ lastdemo だけ 1)。待ち (pro_com_wai) のカウンタも 1 コマに 1 減るので、コマ数がそのまま速さになる。
    /// 倍速中は論理フレームが倍率回回るので、本体どおりだと技アニメも倍率分速くなる。
    ///
    /// % 指定の時は「1 論理フレームあたり 2 × (設定 / 100) / 倍率 コマ」を累積で進める (150% の x4 なら 0.75 コマ)。
    /// 整数にならないぶんは余りとして持ち越し、余りが足りないフレームは 0 コマ (スクリプトを進めずに同じ絵を描く)。
    /// これで「コマンド入力や行動の間は倍速で短く、技アニメだけは好みの速さ」にできる。
    /// </summary>
    internal static class AttackAnim
    {
        public static ConfigEntry<int> Speed;
        public const int MIN = 25, MAX = 400, STEP = 25;
        private static int s_acc;

        public static void Bind(ConfigFile config)
        {
            Speed = config.Bind("Battle", "AttackAnimSpeed", 0,
                new ConfigDescription("技・攻撃アニメの速さ。0=倍速に従う (本体どおり。倍速なら倍率分速くなる) / 25〜400=倍速に関係なく、等倍の本体を 100 とした % の実時間の速さに固定 (25 刻み。150 なら x2 でも x4 でも等倍の 1.5 倍)。コマンド入力や行動の間は倍速のまま", new AcceptableValueRange<int>(0, MAX)));
        }

        public static bool Active { get { return Speed != null && Speed.Value > 0; } }

        public static string Label(int pct)
        {
            if (pct <= 0) return "倍速に従う (本体どおり)";
            return pct + "% (倍速に関係なく固定)";
        }

        public static void Cycle(int delta)
        {
            int v = Speed.Value;
            if (delta > 0) v = v <= 0 ? MIN : v + STEP;
            else v = v <= MIN ? 0 : v - STEP;
            if (v > MAX) v = MAX;
            Speed.Value = v;
        }

        public static void ResetAccum() { s_acc = 0; }

        /// <summary>この論理フレームにスクリプトを進めるコマ数 (2 × 設定% / 倍率 を累積)。</summary>
        public static int StepsThisFrame()
        {
            int unit = GameSpeed.EffectivePercent();   // 倍率は % (100 = 等倍) なので、2 × 設定% を % で割る
            if (unit < 100) unit = 100;
            s_acc += 2 * Speed.Value;
            int n = s_acc / unit;
            s_acc %= unit;
            return n;
        }
    }

    /// <summary>Effect.pro_com_loop (1 論理フレームに 1 回、prosece から): animspeed をこのフレームのコマ数にする。</summary>
    [HarmonyPatch(typeof(Effect), "pro_com_loop")]
    internal static class AttackAnimSpeedPatch
    {
        private static void Prefix(Effect __instance)
        {
            if (!AttackAnim.Active || __instance.lastdemo != 0) return;
            __instance.animspeed = AttackAnim.StepsThisFrame();
        }
    }

    /// <summary>Effect.prosece(no): 効果の開始 (proc[procstg][0] == 0) で累積を戻し、効果番号を記録する (診断)。</summary>
    [HarmonyPatch(typeof(Effect), "prosece")]
    internal static class AttackAnimStartPatch
    {
        private static void Prefix(Effect __instance, int no)
        {
            if (__instance.proc[__instance.procstg][0] != 0) return;
            AttackAnim.ResetAccum();
            Battle b = Main.battle;
            if (b != null) Plugin.Log.LogDebug("[Anim] effect=" + no + " tag=" + b.tag + " actor=" + b.mbox[0] + " weapon=" + b.mbox[6]);
        }
    }
}
