using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 戦闘でアニメーションするモンスター (七英雄などのボス級) の動きを、倍速中も実時間の等倍にする ([Battle] MonsterAnimRealtime、既定 OFF)。
    ///
    /// 戦闘のモンスターは SpriteStudio の焼き込みフレーム (monster/mNNN.ssb) で、Effect.draw (戦闘の毎論理フレーム、Battle.sync → draw から)
    /// が drawMonster → Monster.Update → SSObject.Update を呼び、SSObject.Anime.Update が m_cur_frame++ とコマを 1 つ進めるだけ
    /// (Time.deltaTime は使わない)。倍速中は論理フレームが倍率回回るのでアニメも倍率分速くなる。
    /// そこで Effect.draw の Prefix で「この論理フレームはコマを進めるか」を実時間基準で決め (100 / 倍率% を累積)、
    /// 進めないフレームは SSObject.Update の Prefix で Monster の時だけ本体の処理を飛ばす (return false)。
    /// 静止しているモンスターは元々 1 コマなので何も変わらない。
    ///
    /// Monster.Update の中の被弾の色点滅 (m_color_anim_cur_frame) は base.Update() の後に別に進むので倍速のまま。
    /// 死亡時の溶ける演出は本体が SetPause で止めているので関係ない。ラスボスの登場 (boss_put) はアニメの終わりを待つので等倍の時間がかかる。
    /// 技・攻撃のアニメ (AttackAnimSpeed) や自分たちの絵には触らない。
    /// </summary>
    internal static class MonsterAnim
    {
        public static ConfigEntry<bool> Enabled;

        private static int s_acc;              // 100 / 倍率% の累積 (% 単位の余り)
        private static bool s_advance = true;  // この論理フレームはモンスターのコマを進めるか
        private static bool s_applied;         // 診断: 初めて飛ばした時に 1 行

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Battle", "MonsterAnimRealtime", false,
                "戦闘でアニメーションするモンスター (ボス級) の動きを、倍速中も実時間の等倍にする (他の進行は倍速のまま。静止しているモンスターには影響なし)");
        }

        public static bool IsEnabled { get { return Enabled != null && Enabled.Value; } }

        /// <summary>Effect.draw (戦闘の毎論理フレーム): この論理フレームにコマを進めるかを実時間基準で決める。</summary>
        [HarmonyPatch(typeof(Effect), nameof(Effect.draw))]
        private static class TickPatch
        {
            private static void Prefix()
            {
                if (!IsEnabled) { s_advance = true; s_acc = 0; return; }
                int pct = GameSpeed.EffectivePercent();   // 100 = 等倍
                if (pct <= 100) { s_advance = true; s_acc = 0; return; }
                s_acc += 100;
                if (s_acc >= pct) { s_acc -= pct; s_advance = true; }
                else s_advance = false;
            }
        }

        /// <summary>SSObject.Update: 進めない論理フレームでは Monster の時だけ本体のコマ送りを飛ばす。</summary>
        [HarmonyPatch(typeof(SSObject), nameof(SSObject.Update))]
        private static class UpdatePatch
        {
            private static bool Prefix(SSObject __instance)
            {
                if (s_advance || !IsEnabled) return true;
                Monster m = __instance as Monster;
                if (m == null) return true;
                if (!s_applied && m.m_anime != null && m.m_cur_anim_idx >= 0 && m.m_cur_anim_idx < m.m_anime.Length
                    && m.m_anime[m.m_cur_anim_idx] != null && m.m_anime[m.m_cur_anim_idx].m_frames != null
                    && m.m_anime[m.m_cur_anim_idx].m_frames.Length > 1)
                {
                    s_applied = true;
                    Plugin.Log.LogInfo("[MonsterAnim] applied: frames=" + m.m_anime[m.m_cur_anim_idx].m_frames.Length + " speed=" + GameSpeed.EffectivePercent() + "%");
                }
                return false;   // 本体の Update を飛ばす (コマを進めない。描画は Draw が別に行う)
            }
        }
    }
}
