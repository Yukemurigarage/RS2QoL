using System;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace RS2QoL.Patches
{
    internal enum WindowModeKind
    {
        Unchanged,   // 触らない (Unity が前回の設定を覚えている。F11 で本体の排他フルスクリーン切替)
        Windowed,    // 枠ありウィンドウ (WindowWidth x WindowHeight)
        Borderless,  // 枠なしウィンドウをモニター全体に (疑似フルスクリーン)
        Fullscreen,  // Unity の排他フルスクリーン (モニターの解像度)
    }

    /// <summary>
    /// 画面モード。本体には画面設定が無く、F11 で排他フルスクリーンに切り替える (ScreenCtrl.Update) だけ。
    /// Unity 5.6 にはボーダーレス (枠なしウィンドウ) の設定が無いので、Win32 API でウィンドウの枠を外して
    /// モニター全体に広げる。Alt+Tab で他のウィンドウに切り替えても画面モードが切り替わらないのが利点。
    ///
    /// 手順: Screen.SetResolution で目標サイズにしてから、Unity 側のサイズ変更が終わる (Screen.width が目標と一致する)
    /// のを待ち、そのあとで SetWindowLongPtr / SetWindowPos で枠を変える。順番が逆だと Unity がサイズ変更時に枠を戻す。
    /// 本体の F11 で排他フルスクリーンにして戻ると枠が付いた巨大ウィンドウになるので、Borderless の時は
    /// 定期的に枠の有無を見て付け直す。描画の縦横比は ScreenCtrl が 16:9 でレターボックスにしてくれる。
    /// モニターの大きさは MonitorFromWindow で取る (Unity 5.6 の Windows プレイヤーは DPI 非対応なので、
    /// Screen.currentResolution ではなく同じ座標系の Win32 の値を使う)。Windows 専用。
    /// </summary>
    internal static class WindowMode
    {
        public static ConfigEntry<WindowModeKind> Mode;
        public static ConfigEntry<int> Width, Height;

        /// <summary>設定画面で選べるウィンドウサイズ (幅, 高さ の組)。</summary>
        public static readonly int[] Sizes = { 960, 540, 1280, 720, 1600, 900, 1920, 1080, 2560, 1440 };

        private static bool s_startupDone;
        private static int s_startupFrames;
        private static WindowModeKind s_pending = WindowModeKind.Unchanged;
        private static int s_targetW, s_targetH;
        private static bool s_targetFull;
        private static int s_waitFrames;
        private static int s_lastStyleCheck;
        private static bool s_unavailable;

        public static void Bind(ConfigFile config)
        {
            Mode = config.Bind("Window", "Mode", WindowModeKind.Unchanged,
                "画面モード。Unchanged=触らない (本体任せ、F11 で排他フルスクリーン切替) / Windowed=枠ありウィンドウ (WindowWidth x WindowHeight) / Borderless=枠なしウィンドウをモニター全体に (疑似フルスクリーン) / Fullscreen=排他フルスクリーン。Windows 専用");
            Width = config.Bind("Window", "WindowWidth", 1280, "Windowed の時のウィンドウの幅 (描画領域)");
            Height = config.Bind("Window", "WindowHeight", 720, "Windowed の時のウィンドウの高さ (描画領域)");
            Mode.SettingChanged += (s, e) => Request();
            TakeFocusFromConsole = config.Bind("Window", "TakeFocusFromConsole", true,
                "起動時に BepInEx のコンソール等 (同じプロセスの別ウィンドウ) が前に出ていたらゲームのウィンドウを前に出す。Windows 10 以降はゲームが前面でないと XInput がパッドを読まないため");
            TakeFocusOnStart = config.Bind("Window", "TakeFocusOnStart", true,
                "起動時に Steam のクライアントなど他のアプリの窓が手前にいたら、ゲームの窓を前に出す (ゲームが一度も前面になっていない起動 30 秒以内だけ。自分で Alt+Tab した時は取り返さない)");
        }

        /// <summary>設定画面のウィンドウサイズの表示用。</summary>
        public static string SizeText()
        {
            return Width.Value + " x " + Height.Value;
        }

        /// <summary>設定画面でウィンドウサイズを Sizes の中で前後に動かす。Windowed の時はその場で適用する。</summary>
        public static void CycleSize(int delta)
        {
            int n = Sizes.Length / 2;
            int cur = -1;
            for (int i = 0; i < n; i++) if (Sizes[i * 2] == Width.Value && Sizes[i * 2 + 1] == Height.Value) { cur = i; break; }
            int next = cur < 0 ? (delta > 0 ? 0 : n - 1) : (cur + delta + n) % n;
            Width.Value = Sizes[next * 2];
            Height.Value = Sizes[next * 2 + 1];
            if (Mode.Value == WindowModeKind.Windowed) Request();
        }

        public static string ModeLabel(WindowModeKind k)
        {
            switch (k)
            {
                case WindowModeKind.Windowed: return "ウィンドウ";
                case WindowModeKind.Borderless: return "ボーダーレス";
                case WindowModeKind.Fullscreen: return "フルスクリーン";
                default: return "変更しない";
            }
        }

        /// <summary>今の設定を画面に適用する (非同期。Update で仕上げる)。</summary>
        public static void Request()
        {
            if (Mode == null || Mode.Value == WindowModeKind.Unchanged || s_unavailable) return;
            try
            {
                RECT mon;
                if (!TryGetMonitorRect(out mon)) { Plugin.Log.LogWarning("[Window] window handle not found"); return; }
                int mw = mon.right - mon.left, mh = mon.bottom - mon.top;
                switch (Mode.Value)
                {
                    case WindowModeKind.Windowed:
                        s_targetW = Mathf.Clamp(Width.Value, 320, mw);
                        s_targetH = Mathf.Clamp(Height.Value, 180, mh);
                        s_targetFull = false;
                        break;
                    case WindowModeKind.Borderless:
                        s_targetW = mw; s_targetH = mh; s_targetFull = false;
                        break;
                    default:
                        s_targetW = mw; s_targetH = mh; s_targetFull = true;
                        break;
                }
                s_pending = Mode.Value;
                s_waitFrames = 0;
                Plugin.Log.LogInfo("[Window] request " + Mode.Value + " " + s_targetW + "x" + s_targetH + " (monitor " + mw + "x" + mh + ")");
                Screen.SetResolution(s_targetW, s_targetH, s_targetFull);
            }
            catch (Exception e)
            {
                s_unavailable = true;
                Plugin.Log.LogWarning("[Window] unavailable: " + e.Message);
            }
        }

        /// <summary>Plugin.Update から毎 Unity フレーム呼ぶ。</summary>
        public static void Update()
        {
            if (Mode == null || s_unavailable) return;
            if (!s_startupDone)
            {
                // 起動直後は Unity が前回のウィンドウ状態を復元している最中なので、数フレーム待ってから適用する
                if (++s_startupFrames < 5) return;
                s_startupDone = true;
                Request();
                return;
            }
            if (s_pending != WindowModeKind.Unchanged)
            {
                s_waitFrames++;
                bool reached = Screen.fullScreen == s_targetFull && Screen.width == s_targetW && Screen.height == s_targetH;
                if (!reached && s_waitFrames < 90) return;
                WindowModeKind kind = s_pending;
                s_pending = WindowModeKind.Unchanged;
                if (!reached) Plugin.Log.LogWarning("[Window] resolution not reached: now " + Screen.width + "x" + Screen.height + " full=" + Screen.fullScreen);
                if (!s_targetFull) ApplyStyle(kind == WindowModeKind.Borderless);
                else Plugin.Log.LogInfo("[Window] fullscreen " + Screen.width + "x" + Screen.height);
                return;
            }
            try { FocusCheck(); } catch (Exception e) { s_unavailable = true; Plugin.Log.LogWarning("[Window] focus check unavailable: " + e.Message); }
            // Borderless 中に本体の F11 で排他フルスクリーンにして戻ると枠付きに戻るので、30 フレームごとに見て付け直す
            if (Mode.Value == WindowModeKind.Borderless && !Screen.fullScreen && Time.frameCount - s_lastStyleCheck >= 30)
            {
                s_lastStyleCheck = Time.frameCount;
                IntPtr hwnd = FindGameWindow();
                if (hwnd != IntPtr.Zero && (GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_CAPTION) != 0) Request();
            }
        }

        // ウィンドウの枠を付け外しして位置とサイズを決める。borderless=true なら枠なしでモニター全体、false なら枠ありで中央
        private static void ApplyStyle(bool borderless)
        {
            IntPtr hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) { Plugin.Log.LogWarning("[Window] window handle not found"); return; }
            RECT mon;
            if (!TryGetMonitorRect(out mon)) return;
            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            int x, y, w, h;
            if (borderless)
            {
                style = (style & ~WS_OVERLAPPEDWINDOW) | WS_POPUP | WS_VISIBLE;
                ex &= ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE);
                x = mon.left; y = mon.top; w = mon.right - mon.left; h = mon.bottom - mon.top;
            }
            else
            {
                style = (style & ~WS_POPUP) | WS_OVERLAPPEDWINDOW | WS_VISIBLE;
                ex |= WS_EX_WINDOWEDGE;
                // 描画領域が目標サイズになるように枠の分を足し、モニターの中央に置く
                RECT r = new RECT { left = 0, top = 0, right = s_targetW, bottom = s_targetH };
                AdjustWindowRectEx(ref r, (uint)style, false, (uint)ex);
                w = r.right - r.left; h = r.bottom - r.top;
                x = mon.left + ((mon.right - mon.left) - w) / 2;
                y = mon.top + ((mon.bottom - mon.top) - h) / 2;
                if (y < mon.top) y = mon.top;
            }
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            Plugin.Log.LogInfo("[Window] " + (borderless ? "borderless" : "windowed") + " " + w + "x" + h + " at (" + x + "," + y + ")");
        }

        // ---------------------------------------------------------------- Win32

        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        private const long WS_OVERLAPPEDWINDOW = 0x00CF0000L;
        private const long WS_CAPTION = 0x00C00000L;
        private const long WS_POPUP = 0x80000000L;
        private const long WS_VISIBLE = 0x10000000L;
        private const long WS_EX_DLGMODALFRAME = 0x0001L, WS_EX_WINDOWEDGE = 0x0100L, WS_EX_CLIENTEDGE = 0x0200L, WS_EX_STATICEDGE = 0x00020000L;
        private const uint SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040, SWP_NOOWNERZORDER = 0x0200;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
        private const long WS_EX_TOPMOST = 0x0008L;
        private const uint GW_HWNDPREV = 3;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFO { public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        private delegate bool EnumThreadWndProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumThreadWndProc fn, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder buf, int max);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
        [DllImport("user32.dll")] private static extern bool AdjustWindowRectEx(ref RECT rect, uint style, bool menu, uint exStyle);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool flag);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        // ゲームは x64 なので Ptr 版を直接使う (32bit の user32 には無いエントリ)
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        private static IntPtr s_hwnd;

        // ---------------------------------------------------------------- フォーカス
        // Windows 10 以降、XInput はゲームのウィンドウがフォアグラウンドでない間はパッドを「未接続」として返す
        // (XInputEnable の説明にある「ウィンドウのフォーカスに応じて自動で有効/無効」の挙動)。
        // BepInEx のコンソールは同じプロセスの別ウィンドウなので、起動時にそちらが前に出るとゲームは Unity の HID 経路だけで
        // パッドを読むことになり、押下が丸ごと落ちる既知の現象 (XInputPad 参照) に戻ってしまう。
        // そこで、フォアグラウンドが自プロセスの別ウィンドウ (= コンソール) ならゲームのウィンドウを前に出す。
        // 他のアプリのウィンドウからは奪わない (SetForegroundWindow 自体も他プロセスからは原則奪えない)。

        public static ConfigEntry<bool> TakeFocusFromConsole;
        private static int s_lastFocusCheck = -1000;
        private static float s_lastFocusLog = -100f;
        private static bool s_lastForeground = true;

        /// <summary>ゲームのウィンドウが今フォアグラウンドか (診断の [Pad] 行の fg=)。</summary>
        public static bool IsGameForeground()
        {
            if (s_unavailable) return true;
            try
            {
                IntPtr hwnd = FindGameWindow();
                return hwnd == IntPtr.Zero || GetForegroundWindow() == hwnd;
            }
            catch (Exception) { return true; }
        }

        private static string DescribeWindow(IntPtr hwnd)
        {
            var sb = new StringBuilder(128);
            GetWindowText(hwnd, sb, sb.Capacity);
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            return "'" + sb + "' (" + cls + ")";
        }

        // ---- 起動時に他のアプリ (Steam のクライアント等) が前にいる場合 ([Window] TakeFocusOnStart)
        // Steam から起動すると、Steam が「起動中」の小窓を閉じたあと自分のクライアント窓を前に戻すことがあり、ゲームの窓が
        // その後ろに隠れたまま始まる (ログ: foreground = 'Steam' (SDL_app))。他プロセスの窓から前面を取るのは Windows の
        // 制限があるが、「前面のプロセスが起動した子プロセス」は許されるので (ゲームは Steam の子)、まず素直に
        // SetForegroundWindow を試し、だめなら前面の窓のスレッドに入力を結び付けて (AttachThreadInput) もう一度、
        // それでもだめなら最前面属性を付けてすぐ外して見た目だけでも手前に出す。
        // ゲームが一度でも前面になったら以後は何もしない (ユーザーが自分で Alt+Tab した時に取り返さない)。起動 30 秒以内、5 回まで
        public static ConfigEntry<bool> TakeFocusOnStart;
        private const float START_SECONDS = 30f;
        private const int START_ATTEMPTS_MAX = 5;
        private static bool s_everForeground;
        private static int s_startAttempts;

        private static void TakeForegroundFrom(IntPtr hwnd, IntPtr fg, uint fgThread)
        {
            string from = DescribeWindow(fg);
            string how = "SetForegroundWindow";
            SetForegroundWindow(hwnd);
            if (GetForegroundWindow() != hwnd && fgThread != 0)
            {
                how = "AttachThreadInput";
                uint me = GetCurrentThreadId();
                if (AttachThreadInput(me, fgThread, true))
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                    AttachThreadInput(me, fgThread, false);
                }
            }
            bool got = GetForegroundWindow() == hwnd;
            if (!got)
            {
                how = "TOPMOST flip";
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            Plugin.Log.LogInfo("[Window] t=" + Time.realtimeSinceStartup.ToString("0.0") + " foreground was " + from + " (other process) -> " + how
                + (got ? " ok (game is foreground)" : " (still not foreground, attempt " + s_startAttempts + "/" + START_ATTEMPTS_MAX + ")"));
        }

        // 60 フレームに 1 回 (ゲームが一度も前面になっていない起動直後は 10 フレームに 1 回)。
        // 自プロセスの別ウィンドウが前にいればゲームを前に出す。他プロセスなら起動直後だけ前面を取り、以後は診断ログだけ (10 秒に 1 回)
        private static void FocusCheck()
        {
            if (Time.frameCount - s_lastFocusCheck < (s_everForeground ? 60 : 10)) return;
            s_lastFocusCheck = Time.frameCount;
            IntPtr hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) return;
            IntPtr fg = GetForegroundWindow();
            bool isFg = fg == hwnd;
            if (isFg != s_lastForeground)
            {
                s_lastForeground = isFg;
                if (isFg) Plugin.Log.LogInfo("[Window] game window is foreground again");
            }
            if (isFg) { s_everForeground = true; ZOrderCheck(hwnd); return; }
            if (fg == IntPtr.Zero) return;
            uint pid;
            uint fgThread = GetWindowThreadProcessId(fg, out pid);
            bool ownProcess = pid == GetCurrentProcessId();
            if (ownProcess && TakeFocusFromConsole != null && TakeFocusFromConsole.Value)
            {
                bool ok = SetForegroundWindow(hwnd);
                Plugin.Log.LogInfo("[Window] foreground was " + DescribeWindow(fg) + " of this process -> SetForegroundWindow(game) " + (ok ? "ok" : "failed"));
                return;
            }
            if (!ownProcess && !s_everForeground && s_startAttempts < START_ATTEMPTS_MAX
                && Time.realtimeSinceStartup < START_SECONDS && TakeFocusOnStart != null && TakeFocusOnStart.Value)
            {
                s_startAttempts++;
                TakeForegroundFrom(hwnd, fg, fgThread);
                return;
            }
            if (Time.realtimeSinceStartup - s_lastFocusLog >= 10f)
            {
                s_lastFocusLog = Time.realtimeSinceStartup;
                Plugin.Log.LogInfo("[Window] game window not foreground: " + DescribeWindow(fg) + (ownProcess ? " (this process)" : " (pid " + pid + ")") + " -> XInput は停止中");
            }
        }

        // 起動後しばらく (ZORDER_SECONDS)、ゲームが前面 (アクティブ) なのに他のウィンドウ (Steam のクライアント等) が Z 順で
        // ゲームの上に重なっていないかを 1 秒ごとに見る。重なっていれば何かを記録し、最前面属性 (TOPMOST) でなければ
        // ゲームを Z 順の一番上に上げ直す (アクティブは変えない)。TOPMOST の窓は Windows の決まりで下にできないので記録だけ
        private const float ZORDER_SECONDS = 20f;
        private static int s_zorderLogs;
        private static void ZOrderCheck(IntPtr hwnd)
        {
            if (Time.realtimeSinceStartup > ZORDER_SECONDS) return;
            RECT game;
            if (!GetWindowRect(hwnd, out game)) return;
            uint me = GetCurrentProcessId();
            IntPtr w = GetWindow(hwnd, GW_HWNDPREV);
            bool raise = false;
            int guard = 0;
            var found = new StringBuilder();
            while (w != IntPtr.Zero && guard++ < 512)
            {
                IntPtr next = GetWindow(w, GW_HWNDPREV);
                if (IsWindowVisible(w))
                {
                    RECT r;
                    if (GetWindowRect(w, out r) && r.right > game.left && r.left < game.right && r.bottom > game.top && r.top < game.bottom)
                    {
                        var title = new StringBuilder(128);
                        GetWindowText(w, title, title.Capacity);
                        if (title.Length > 0)
                        {
                            uint pid;
                            GetWindowThreadProcessId(w, out pid);
                            bool topmost = (GetWindowLongPtr(w, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
                            found.Append(" | ").Append(DescribeWindow(w)).Append(pid == me ? " (this process)" : " (pid " + pid + ")").Append(topmost ? " TOPMOST" : "");
                            if (!topmost && pid != me) raise = true;
                        }
                    }
                }
                w = next;
            }
            if (found.Length == 0) return;
            bool diag = s_zorderLogs < 8 && Plugin.DiagEnabled != null && Plugin.DiagEnabled.Value;   // 記録は Diagnostics の時だけ (最大 8 回)
            if (diag) { s_zorderLogs++; Plugin.Log.LogInfo("[Window] t=" + Time.realtimeSinceStartup.ToString("0.0") + " game is foreground but these windows are above it:" + found); }
            if (raise && TakeFocusFromConsole != null && TakeFocusFromConsole.Value)
            {
                bool ok = SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                Plugin.Log.LogInfo("[Window] -> raised game window to top " + (ok ? "ok" : "failed"));
            }
        }

        // Unity のメインウィンドウ (クラス名 UnityWndClass) を、このスレッド (Unity メインスレッド) のウィンドウから探す
        private static IntPtr FindGameWindow()
        {
            if (s_hwnd != IntPtr.Zero) return s_hwnd;
            IntPtr found = IntPtr.Zero;
            var sb = new StringBuilder(64);
            EnumThreadWindows(GetCurrentThreadId(), (hwnd, l) =>
            {
                sb.Length = 0;
                GetClassName(hwnd, sb, sb.Capacity);
                if (sb.ToString() == "UnityWndClass") { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) found = GetActiveWindow();
            s_hwnd = found;
            return found;
        }

        private static bool TryGetMonitorRect(out RECT rect)
        {
            rect = new RECT();
            IntPtr hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) return false;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(mon, ref info)) return false;
            rect = info.rcMonitor;
            return true;
        }
    }
}
