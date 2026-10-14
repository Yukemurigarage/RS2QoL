using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// ワールドマップ (大陸・地域画面) の開閉で広がる / 縮む巻物 (Core.m_fld_world_open / m_fld_world_close、fld/wld_waku_open / wld_waku_close)
    /// の速さを、倍速 (GameSpeed) に関わらず等倍と同じ実時間にする (v0.30.0 の羽のカーソルと同じ考え)。
    ///
    /// 巻物のアニメは FLdDrawer.Update (MonoBehaviour = Unity フレームごと) が FldObject.Update を呼んで cur_frame を m_frame_rate_speed
    /// (本体は SetFlameSpeed(4)) ずつ進める。60fps 表示 (Interp60) では等倍で論理フレームの Unity フレーム (33ms 間隔) だけが本物の Update で、
    /// 複製フレームは Interp60FldAnim のプレビュー。倍速 x2 以上では全ての Unity フレーム (16.7ms 間隔) が本物の Update になるので、
    /// 巻物は 2 倍の速さで開閉する。本体の処理は巻物の終わりを待たない (開く時は world_size 50→240 の 5 フレームで地図を出し、
    /// 閉じる時は fade_out_world の直後に setFade(250) でフェードする) ので、速さだけ変えても進行は変わらない。
    ///
    /// ここでは本物の Update の直前に、前回の本物の Update からの実時間に応じた進み (33ms あたり 4 = 本体の値) を SetFlameSpeed で入れる。
    /// 端数は持ち越す (16.7ms ごとなら 2, 2, 2 …、33ms ごとなら 4, 4, 4 …)。巻物以外の FldObject (フィールドの 3D オブジェクト) には触らない。
    /// </summary>
    internal static class WorldScrollSpeed
    {
        public static ConfigEntry<bool> Enabled;

        private const float STEP_PER_SECOND = 4f * 30f;   // 本体: 1 論理フレーム (1/30 秒) に 4
        private const int STEP_MIN = 1, STEP_MAX = 16;
        private const int VANILLA_STEP = 4;

        private class Track { public float lastTime = -1f; public float acc; }
        private static readonly Dictionary<FldObject, Track> s_tracks = new Dictionary<FldObject, Track>();
        private static readonly List<FldObject> s_prune = new List<FldObject>();
        private static bool s_appliedLogged;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("WorldMap", "ScrollSpeedFixed", true,
                "ワールドマップを開く / 閉じる時の巻物の速さを、倍速中も等倍と同じ (実時間で固定) にする (既定 On)");
        }

        public static void LogPatched()
        {
            Plugin.Log.LogInfo("[Scroll] patched: FLdDrawer.Update (wld_waku_open / wld_waku_close の進みを実時間で固定、ScrollSpeedFixed=" + (Enabled != null && Enabled.Value) + ")");
        }

        /// <summary>巻物の FldObject か (データパスで判定)。</summary>
        private static bool IsScroll(FldObject fld)
        {
            string p = fld.m_data_path;
            return p != null && p.IndexOf("wld_waku") >= 0;
        }

        /// <summary>FLdDrawer.Update の Prefix (本物の Update の直前)。複製フレーム (Interp60 のプレビュー) では何もしない。</summary>
        internal static void BeforeUpdate(FLdDrawer drawer)
        {
            if (Enabled == null || !Enabled.Value) return;
            FldObject fld = drawer.m_fld;
            if (fld == null || !fld.m_visible || Interp60.Skipping || !IsScroll(fld)) return;
            Track t;
            if (!s_tracks.TryGetValue(fld, out t))
            {
                if (s_tracks.Count > 8) Prune();
                t = new Track();
                s_tracks[fld] = t;
            }
            float now = Time.realtimeSinceStartup;
            int step;
            if (t.lastTime < 0f || now - t.lastTime > 0.5f)
            {
                step = VANILLA_STEP;   // 最初 / 長い空白の後は本体の値
                t.acc = 0f;
            }
            else
            {
                t.acc += (now - t.lastTime) * STEP_PER_SECOND;
                step = Mathf.RoundToInt(t.acc);
                if (step < STEP_MIN) step = STEP_MIN;
                if (step > STEP_MAX) step = STEP_MAX;
                t.acc -= step;
                if (t.acc > 2f) t.acc = 2f; else if (t.acc < -2f) t.acc = -2f;
            }
            t.lastTime = now;
            fld.SetFlameSpeed(step);
            if (!s_appliedLogged)
            {
                s_appliedLogged = true;
                Plugin.Log.LogInfo("[Scroll] applied: " + fld.m_data_path + " step=" + step + " speed=" + GameSpeed.EffectivePercent() + "% interp60=" + Interp60.Active);
            }
        }

        private static void Prune()
        {
            s_prune.Clear();
            float now = Time.realtimeSinceStartup;
            foreach (KeyValuePair<FldObject, Track> kv in s_tracks)
            {
                if (kv.Key == null || now - kv.Value.lastTime > 5f) s_prune.Add(kv.Key);
            }
            for (int i = 0; i < s_prune.Count; i++) s_tracks.Remove(s_prune[i]);
        }
    }

    /// <summary>FLdDrawer.Update の前に巻物の進みを入れる。Interp60FldDrawerPatch (複製フレームのプレビュー) より先に走る。</summary>
    [HarmonyPatch(typeof(FLdDrawer), "Update")]
    [HarmonyPriority(Priority.High)]
    internal static class WorldScrollSpeedPatch
    {
        private static void Prefix(FLdDrawer __instance)
        {
            WorldScrollSpeed.BeforeUpdate(__instance);
        }
    }

    /// <summary>
    /// 巻物の開閉に重なるフェードも実時間にする。本体は巻物を閉じる時 (Core.fade_out_world の直後) に Sys.setFade(250) / its_outworld の setFade(400)、
    /// 開く時 (init_japan_vram_sa / init_world_vram_sa の直後) に setFade(-250) を同じ論理フレームで呼ぶ。フェードは Sys.syncFade (Main.update の中) が
    /// 論理フレームごとに進めるので、倍速 x2 では 125 ms で暗転し、実時間で固定した巻物の閉じる姿が隠れてしまう (ユーザー報告: 「ほぼカット」)。
    /// そこで、これらの直後の setFade だけ長さ (ms) を倍率分だけ伸ばし、実時間で等倍と同じ 250 ms にする。
    /// 腕木: fade_out_world / init_*_vram_sa の Postfix で「次の setFade を伸ばす」印を立て、同じ Unity フレームの setFade の Prefix で v を伸ばして印を消す。
    /// </summary>
    internal static class WorldFadeScale
    {
        private static int s_armFrame = -10;
        private static bool s_logged;

        internal static void Arm()
        {
            if (WorldScrollSpeed.Enabled == null || !WorldScrollSpeed.Enabled.Value) return;
            s_armFrame = Time.frameCount;
        }

        internal static void OnSetFade(ref int v)
        {
            if (s_armFrame != Time.frameCount) return;
            s_armFrame = -10;
            int pct = GameSpeed.EffectivePercent();
            if (pct == 100 || v == 0) return;
            int was = v;
            v = v * pct / 100;
            if (!s_logged)
            {
                s_logged = true;
                Plugin.Log.LogInfo("[Scroll] fade applied: setFade " + was + " -> " + v + " (speed=" + pct + "%)");
            }
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.fade_out_world))]
    internal static class WorldFadeScaleClosePatch
    {
        private static void Postfix() { WorldFadeScale.Arm(); }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.init_japan_vram_sa))]
    internal static class WorldFadeScaleOpenAreaPatch
    {
        private static void Postfix() { WorldFadeScale.Arm(); }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.init_world_vram_sa))]
    internal static class WorldFadeScaleOpenWorldPatch
    {
        private static void Postfix() { WorldFadeScale.Arm(); }
    }

    [HarmonyPatch(typeof(Sys), nameof(Sys.setFade), new[] { typeof(int), typeof(int), typeof(int), typeof(int) })]
    internal static class WorldFadeScaleSetFadePatch
    {
        private static void Prefix(ref int v) { WorldFadeScale.OnSetFade(ref v); }
    }
}
