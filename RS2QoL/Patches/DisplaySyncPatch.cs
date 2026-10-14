using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    internal enum DisplaySyncKind
    {
        Unchanged, // 本体任せ (GameMain のインスペクタ値: targetFrameRate / vSyncCount)
        VRR,       // G-Sync / FreeSync 向け: VSync OFF + プラグインの正確な 30fps リミッタ
        VSync,     // 固定リフレッシュ向け: モニターの Hz が 30 の整数倍なら vSyncCount=Hz/30 で完全同期、それ以外は VSync ON + リミッタ
    }

    /// <summary>
    /// 画面の同期 (VRR / VSync) と正確なフレームリミッタ。
    ///
    /// 本体はガラケー版の移植で 1 Unity フレーム = 1 論理フレーム (33ms) なので、ゲームの速さ = Unity のフレームレート。
    /// 画面に出す速さを変えることはできず、「30fps を正確な間隔で画面に出す」ことが唯一できる改善になる。
    /// 本体は GameMain.Start でインスペクタの framerate / vsynccount を Application.targetFrameRate / QualitySettings.vSyncCount
    /// に入れるだけで、Unity 5.6 の targetFrameRate のリミッタは Sleep 頼みで間隔がばらつく (数 ms)。
    /// VRR モニターは「フレームが来た時に描く」ので、間隔がばらつくと LFC (低フレームレート補償) の予測が外れて
    /// ちらつき・ティアリング様の分断・カクつきになる。そこで Unity のリミッタを外し (targetFrameRate=-1)、
    /// GameMain.Update の Prefix (論理フレームの直前) で Stopwatch を見て Sleep(1) + スピンで目標時刻まで待つ。
    /// 待つ場所はフレームのどこでもよい (ループ全体の周期が決まる) が、Update の Prefix ならウィンドウ最小化中
    /// (描画が止まり WaitForEndOfFrame が来ない) でも確実に通るので、ゲームが暴走 (超高速進行) しない。
    ///
    /// VSync モードは vSyncCount で Unity が垂直同期に合わせて待つ (60Hz→2、120Hz→4)。
    /// 144Hz のように 30 で割り切れない時は vSyncCount=1 + リミッタで平均 30fps にする (4 回と 5 回の垂直同期が混ざるので
    /// 多少カクつく。そういうモニターは VRR が使えるはずなので VRR を勧める)。
    /// Unity は vSyncCount が 0 以外だと targetFrameRate を無視するので、vSync 中は必ず自前のリミッタも併用する。
    /// </summary>
    internal static class DisplaySync
    {
        public static ConfigEntry<DisplaySyncKind> Mode;
        public static ConfigEntry<int> FrameRate;

        private static readonly Stopwatch s_clock = Stopwatch.StartNew();
        private static bool s_gameStarted;
        private static int s_gameFrameRate = -1, s_gameVSync = -1; // 本体が GameMain.Start で設定した値
        private static bool s_limiterOn;
        private static double s_periodMs = 1000.0 / 30.0;
        private static double s_nextMs = -1;
        private static int s_lastWaitFrame = -1;
        private static bool s_timerPeriodSet;
        private static int s_appliedHz;    // VSync で適用した時のモニターの Hz (変わったら再適用)
        private static int s_hzCheckFrame;

        // 診断用: フレーム間隔の統計 (Diagnostics ON の時だけ 5 秒ごとに出す)
        private static double s_statPrevMs = -1, s_statStartMs, s_statMin, s_statMax, s_statSum;
        private static int s_statN, s_statLong;

        public static void Bind(ConfigFile config)
        {
            Mode = config.Bind("Display", "Sync", DisplaySyncKind.VSync,
                "画面の同期。VSync=固定リフレッシュ向け・既定 (モニターの Hz が 30 の整数倍なら垂直同期 2 回に 1 フレームで完全に 30fps、それ以外は VSync ON + リミッタ) / VRR=G-Sync・FreeSync 向け (VSync OFF + 正確な 30fps リミッタ。Windows の可変リフレッシュレートかドライバー側でウィンドウでも VRR を有効にしておく) / Unchanged=本体任せ (Unity の 30fps リミッタは間隔が 26〜40ms と暴れる)");
            FrameRate = config.Bind("Display", "FrameRate", 0,
                new ConfigDescription("VRR / VSync の時の目標フレームレート。0=本体の値 (通常 30)。ゲームの進行速度はフレームレートに比例するので、速さを変えたい時は [Speed] を使う", new AcceptableValueRange<int>(0, 120)));
            Mode.SettingChanged += (s, e) => Apply();
            FrameRate.SettingChanged += (s, e) => Apply();
        }

        public static string ModeLabel(DisplaySyncKind k)
        {
            switch (k)
            {
                case DisplaySyncKind.VRR: return "VRR (G-Sync / FreeSync)";
                case DisplaySyncKind.VSync: return "垂直同期";
                default: return "変更しない";
            }
        }

        /// <summary>GameMain.Start の直後。本体の設定値を記録してから、config に従って上書きする。</summary>
        internal static void OnGameStart()
        {
            s_gameFrameRate = Application.targetFrameRate;
            s_gameVSync = QualitySettings.vSyncCount;
            s_gameStarted = true;
            Resolution r = Screen.currentResolution;
            Plugin.Log.LogInfo(string.Format(
                "[Display] game: targetFrameRate={0} vSyncCount={1} | monitor {2}x{3}@{4}Hz fullScreen={5} window {6}x{7} | {8} ({9}) threaded={10}",
                s_gameFrameRate, s_gameVSync, r.width, r.height, r.refreshRate, Screen.fullScreen, Screen.width, Screen.height,
                SystemInfo.graphicsDeviceName, SystemInfo.graphicsDeviceVersion, SystemInfo.graphicsMultiThreaded));
            Apply();
        }

        /// <summary>config に従って Unity の設定とリミッタを決める。</summary>
        public static void Apply()
        {
            if (Mode == null || !s_gameStarted) return;
            int fps = FrameRate.Value > 0 ? FrameRate.Value : (s_gameFrameRate >= 20 && s_gameFrameRate <= 120 ? s_gameFrameRate : 30);
            // 60fps 表示 (Interp60) 中は Unity を 2 倍で回す (論理フレームは 2 回に 1 回なので進行速度は同じ)
            fps *= Interp60.DisplayMultiplier();
            s_periodMs = 1000.0 / fps;
            s_nextMs = -1;
            // Unchanged (本体任せ) は Unity の 30fps リミッタのままなので、60fps 表示中は VSync として扱う (本体任せでは 60fps にならない)
            DisplaySyncKind mode = Mode.Value;
            if (mode == DisplaySyncKind.Unchanged && Interp60.DisplayMultiplier() > 1)
            {
                mode = DisplaySyncKind.VSync;
                Plugin.Log.LogInfo("[Display] Sync=Unchanged but 60fps display is on -> treating as VSync");
            }
            switch (mode)
            {
                case DisplaySyncKind.VRR:
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = -1;
                    s_limiterOn = true;
                    Plugin.Log.LogInfo("[Display] VRR: vSyncCount=0 targetFrameRate=-1 limiter=" + fps + "fps (" + s_periodMs.ToString("F2") + "ms)");
                    break;
                case DisplaySyncKind.VSync:
                {
                    // Unity は 59.95Hz を 59 と切り捨てて返すので、±1Hz は倍数とみなす
                    int hz = Screen.currentResolution.refreshRate;
                    s_appliedHz = hz;
                    int n = hz > 0 ? (int)Math.Round((double)hz / fps) : 0;
                    bool multiple = n >= 1 && n <= 4 && Math.Abs(hz - n * fps) <= 1;
                    // リミッタは常に併用する。垂直同期だけだと、後からモニターの Hz を変えた時に vSyncCount の分だけゲームが速く/遅くなる
                    // (60Hz で 2 のまま 165Hz にすると 2.75 倍速)。周期が同じなら垂直同期の待ちで足りるので、リミッタはほぼ待たない
                    QualitySettings.vSyncCount = multiple ? n : 1;
                    Application.targetFrameRate = -1;
                    s_limiterOn = true;
                    if (multiple) Plugin.Log.LogInfo("[Display] VSync: " + hz + "Hz / " + fps + "fps -> vSyncCount=" + n + " + limiter");
                    else Plugin.Log.LogWarning("[Display] VSync: " + hz + "Hz is not a multiple of " + fps + "fps -> vSyncCount=1 + limiter (uneven cadence; prefer VRR on this monitor)");
                    break;
                }
                default:
                    if (s_limiterOn || QualitySettings.vSyncCount != s_gameVSync || Application.targetFrameRate != s_gameFrameRate)
                    {
                        QualitySettings.vSyncCount = s_gameVSync;
                        Application.targetFrameRate = s_gameFrameRate;
                        Plugin.Log.LogInfo("[Display] Unchanged: restored vSyncCount=" + s_gameVSync + " targetFrameRate=" + s_gameFrameRate);
                    }
                    s_limiterOn = false;
                    break;
            }
            if (s_limiterOn && !s_timerPeriodSet)
            {
                // Sleep(1) の粒度を 1ms にする (Unity 自身も呼んでいるはずだが念のため)。終了時に戻す
                try { timeBeginPeriod(1); s_timerPeriodSet = true; } catch { }
            }
        }

        /// <summary>GameMain.Update の Prefix (毎 Unity フレーム、論理フレームの直前)。</summary>
        internal static void OnFrameStart()
        {
            if (Time.frameCount == s_lastWaitFrame) return;
            s_lastWaitFrame = Time.frameCount;
            // BepInEx はゲームが初めて UnityEngine.Application に触れた時にプラグインを読み込むが、その場所が
            // GameMain.Start の 1 行目 (Application.backgroundLoadingPriority) なので、その回の Start には Postfix が効かない。
            // 最初の Update は Start が終わった後なので、ここで本体の設定値を拾って適用する
            if (!s_gameStarted)
            {
                try { OnGameStart(); }
                catch (Exception e) { s_gameStarted = true; Plugin.Log.LogWarning("[Display] " + e); }
            }
            if (s_limiterOn) Wait();
            // VSync 中はモニターの Hz が変わったら (設定変更・別モニターへ移動) vSyncCount を取り直す
            if ((Mode.Value == DisplaySyncKind.VSync || (Mode.Value == DisplaySyncKind.Unchanged && Interp60.DisplayMultiplier() > 1)) && Time.frameCount - s_hzCheckFrame >= 60)
            {
                s_hzCheckFrame = Time.frameCount;
                if (Screen.currentResolution.refreshRate != s_appliedHz) { Plugin.Log.LogInfo("[Display] refresh rate changed " + s_appliedHz + " -> " + Screen.currentResolution.refreshRate); Apply(); }
            }
            if (Plugin.DiagEnabled != null && Plugin.DiagEnabled.Value) Stat();
        }

        // 目標時刻 (前回の目標 + 周期) まで待つ。2ms 以上残っていれば Sleep(1)、残りはスピン。
        // 1 周期以上遅れたら (ロード等) そこを新しい基準にして取り返そうとしない (取り返すと一瞬速くなる)
        private static void Wait()
        {
            double now = s_clock.Elapsed.TotalMilliseconds;
            if (s_nextMs < 0 || now > s_nextMs + s_periodMs) s_nextMs = now;
            else
            {
                while (s_nextMs - now > 2.0)
                {
                    Thread.Sleep(1);
                    now = s_clock.Elapsed.TotalMilliseconds;
                }
                while (now < s_nextMs)
                {
                    Thread.SpinWait(100);
                    now = s_clock.Elapsed.TotalMilliseconds;
                }
            }
            s_nextMs += s_periodMs;
        }

        private static void Stat()
        {
            double now = s_clock.Elapsed.TotalMilliseconds;
            if (s_statPrevMs < 0) { s_statPrevMs = now; s_statStartMs = now; return; }
            double dt = now - s_statPrevMs;
            s_statPrevMs = now;
            if (s_statN == 0) { s_statMin = dt; s_statMax = dt; s_statSum = 0; s_statLong = 0; }
            s_statN++;
            s_statSum += dt;
            if (dt < s_statMin) s_statMin = dt;
            if (dt > s_statMax) s_statMax = dt;
            if (dt > s_periodMs * 1.5) s_statLong++;
            if (now - s_statStartMs >= 5000)
            {
                Plugin.Log.LogInfo(string.Format("[Display] {0} frames in {1:F1}s: avg={2:F2}ms min={3:F2} max={4:F2} long(>{5:F0}ms)={6} | vSync={7} target={8} limiter={9}",
                    s_statN, (now - s_statStartMs) / 1000.0, s_statSum / s_statN, s_statMin, s_statMax, s_periodMs * 1.5, s_statLong,
                    QualitySettings.vSyncCount, Application.targetFrameRate, s_limiterOn ? "on" : "off"));
                s_statN = 0;
                s_statStartMs = now;
            }
        }

        internal static void Shutdown()
        {
            if (s_timerPeriodSet) { try { timeEndPeriod(1); } catch { } s_timerPeriodSet = false; }
        }

        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
    }

    /// <summary>本体が targetFrameRate / vSyncCount を設定した直後に、値を記録して config で上書きする。</summary>
    [HarmonyPatch(typeof(GameMain), "Start")]
    internal static class DisplaySyncStartPatch
    {
        private static void Postfix()
        {
            try { DisplaySync.OnGameStart(); }
            catch (Exception e) { Plugin.Log.LogWarning("[Display] " + e); }
        }
    }

    /// <summary>毎 Unity フレームの先頭 (論理フレームの直前) でフレームリミッタを回す。</summary>
    [HarmonyPatch(typeof(GameMain), "Update")]
    internal static class DisplaySyncUpdatePatch
    {
        // 60fps 表示 (Interp60) が「このフレームは論理フレームを回さず複製する」と決めたら GameMain.Update を止める
        // (Prefix を 1 つにまとめているのは、false を返す Prefix と他の Prefix の実行順に依存しないため)
        private static bool Prefix()
        {
            DisplaySync.OnFrameStart();
            return Interp60.OnFrameStart();
        }
    }
}
