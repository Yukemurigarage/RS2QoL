using System;
using System.Threading;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// フリーズ (メインスレッドが進まなくなった) の診断 ([Diagnostics] Enabled の時だけ動く)。
    ///
    /// 2026-10-08: 戦闘中からタイトルに戻った後、タイトルで A を押したら (Menu._title1 が受け取った記録はある) それきり
    /// パッド入力の記録が一切出なくなり、ユーザーがゲームを終了した。GetKeypadState は毎フレーム必ず呼ばれるので、
    /// 記録が止まる = ゲームのループ自体が止まった (フリーズ) か、ウィンドウが非アクティブで Unity が更新を止めたか。
    /// どちらか、どこで止まったかをログに残すため:
    ///   - メインスレッドが通る要所 (GetKeypadState / Core.sync / Menu.sync / Battle.sync の入口と出口) で「最後に通った点」を記録する
    ///   - 別スレッドが 0.5 秒ごとに見て、2 秒以上メインスレッドが要所を通っていなければ [Freeze] を 1 回出す
    ///     (マップ切り替えの処理落ちは 0.4 秒程度なので引っかからない)。復帰したら [Freeze] recovered を出す
    ///   - ウィンドウのアクティブ切り替えは Plugin.OnApplicationFocus から [Focus] を出す (非アクティブで止まった場合と区別する)
    /// 別スレッドからは Unity の API を呼べないので、ログに出す値は全てメインスレッド側で int にして保存しておく。
    /// </summary>
    internal static class FreezeWatchdog
    {
        private const int STALL_MS = 2000;
        private const int POLL_MS = 500;

        // 要所の番号。奇数 = 入口、偶数 = 出口
        internal const int P_KEYPAD = 1, P_CORE_IN = 3, P_CORE_OUT = 4, P_MENU_IN = 5, P_MENU_OUT = 6, P_BATTLE_IN = 7, P_BATTLE_OUT = 8;
        private static readonly string[] s_pointNames = { "", "GetKeypadState", "", "Core.sync in", "Core.sync out", "Menu.sync in", "Menu.sync out", "Battle.sync in", "Battle.sync out" };

        private static volatile int s_lastTick;      // Environment.TickCount (別スレッドから読める時計)
        private static volatile int s_point;
        private static volatile int s_menuType, s_menuProc, s_coreProc1, s_battleProc1, s_logicalFrame;
        private static Thread s_thread;

        /// <summary>メインスレッドの要所で呼ぶ。</summary>
        internal static void Mark(int point)
        {
            s_point = point;
            s_lastTick = Environment.TickCount;
            if (point == P_KEYPAD) s_logicalFrame++;
            Core c = Main.core;
            Menu m = Main.menu;
            Battle b = Main.battle;
            if (c != null) s_coreProc1 = c.battle_proc[1];
            if (m != null) { s_menuType = m.menu_type; s_menuProc = m.proc[0]; }
            if (b != null) s_battleProc1 = b.proc[0][0];
        }

        internal static void Start()
        {
            if (s_thread != null || !InputDiag.Enabled) return;
            s_lastTick = Environment.TickCount;
            s_thread = new Thread(Loop);
            s_thread.IsBackground = true; // ゲーム終了を妨げない
            s_thread.Name = "RS2QoL freeze watchdog";
            s_thread.Start();
            Plugin.Log.LogInfo("[Freeze] watchdog started");
        }

        private static void Loop()
        {
            bool stalled = false;
            int stallStartFrame = 0;
            while (true)
            {
                Thread.Sleep(POLL_MS);
                int gap = Environment.TickCount - s_lastTick;
                if (!stalled && gap >= STALL_MS)
                {
                    stalled = true;
                    stallStartFrame = s_logicalFrame;
                    Plugin.Log.LogWarning(string.Format(
                        "[Freeze] main thread has not advanced for {0}ms. last point={1} logicalFrame={2} menu_type={3} menu_proc={4} battle_proc1={5} bproc={6}",
                        gap, s_pointNames[s_point], s_logicalFrame, s_menuType, s_menuProc, s_coreProc1, s_battleProc1));
                }
                else if (stalled && gap < STALL_MS)
                {
                    stalled = false;
                    Plugin.Log.LogInfo(string.Format("[Freeze] recovered (stalled at logicalFrame={0}, now {1})", stallStartFrame, s_logicalFrame));
                }
            }
        }
    }

    [HarmonyPatch(typeof(Util), nameof(Util.GetKeypadState))]
    [HarmonyPriority(Priority.First)]
    internal static class FreezeWatchdogKeypadPatch
    {
        private static void Prefix() { if (InputDiag.Enabled) FreezeWatchdog.Mark(FreezeWatchdog.P_KEYPAD); }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.sync))]
    internal static class FreezeWatchdogCorePatch
    {
        private static void Prefix() { if (InputDiag.Enabled) FreezeWatchdog.Mark(FreezeWatchdog.P_CORE_IN); }
        private static void Postfix() { if (InputDiag.Enabled) FreezeWatchdog.Mark(FreezeWatchdog.P_CORE_OUT); }
    }

    [HarmonyPatch(typeof(Menu), nameof(Menu.sync))]
    internal static class FreezeWatchdogMenuPatch
    {
        private static void Prefix() { if (InputDiag.Enabled) FreezeWatchdog.Mark(FreezeWatchdog.P_MENU_IN); }
        private static void Postfix() { if (InputDiag.Enabled) FreezeWatchdog.Mark(FreezeWatchdog.P_MENU_OUT); }
    }

    [HarmonyPatch(typeof(Battle), nameof(Battle.sync))]
    internal static class FreezeWatchdogBattlePatch
    {
        private static void Prefix() { if (InputDiag.Enabled) FreezeWatchdog.Mark(FreezeWatchdog.P_BATTLE_IN); }
        private static void Postfix() { if (InputDiag.Enabled) FreezeWatchdog.Mark(FreezeWatchdog.P_BATTLE_OUT); }
    }
}
