using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 入力落ちの切り分け用の診断ログ (config の [Diagnostics] Enabled で ON/OFF)。
    /// 「パッド側で入っていない」のか「ゲーム側で捨てている」のかを、3 段階の変化をタイムスタンプ付きで記録して判断する。
    ///   [Pad]   Util.GetKeypadState: 4 軸の生値 (0.01 刻み) とボタン/方向ビットが変わった時
    ///   [Key]   Sys.syncKey 通過後にゲームが認識した方向 (Sys.key[0]) が変わった時
    ///   [Field] Core.move_by_key: フィールド移動処理が見ている方向 (human_key) が変わった時 + 阻害フラグ
    ///   [MARK]  Start+Select を同時に押した時 (キーボードは MarkKey、既定 F7)。「今おかしかった」の目印に使う
    /// ON/OFF は config のほか MOD 設定画面の「その他」と ToggleKey (既定 F8)。OFF 時と終了時に LogOutput.log を RS2QoL-diag\ に取り分けて
    /// エクスプローラで開く (次の起動で上書きされないように)。
    /// </summary>
    internal static class InputDiag
    {
        public static bool Enabled { get { return Plugin.DiagEnabled != null && Plugin.DiagEnabled.Value; } }

        private static GUIStyle s_badge;
        private static float s_markTime = -100f;   // 最後に Start+Select の目印を書いた時刻 (realtimeSinceStartup)。しばらく左下の表示を「目印を記録しました」にする
        private static int s_badgeSize = -1;

        /// <summary>診断ログを ON/OFF するキー (config [Diagnostics] ToggleKey、既定 F8。None で無し)。</summary>
        public static ConfigEntry<KeyCode> ToggleKey;
        /// <summary>目印 ([MARK]) を書くキー (config [Diagnostics] MarkKey、既定 F7。パッドの Start+Select と同じ。キーボードだけで遊ぶ人用)。</summary>
        public static ConfigEntry<KeyCode> MarkKey;
        private static bool s_recorded;       // この起動で一度でも ON にした (終了時に複製してフォルダを開く条件)
        private static bool s_folderOpened;   // ログのフォルダを開いた (1 回の起動で 1 度だけ)
        private static readonly string s_launch = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");   // 起動日時 (複製のファイル名)

        public static string LogPath { get { return System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log"); } }
        /// <summary>複製の置き場 (BepInEx\RS2QoL-diag\)。LogOutput.log は次の起動で上書きされるので、ここに起動ごとに 1 ファイルで取り分ける。</summary>
        public static string CopyDir { get { return System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "RS2QoL-diag"); } }
        public static string CopyPath { get { return System.IO.Path.Combine(CopyDir, "RS2QoL-diag_" + s_launch + ".log"); } }

        /// <summary>
        /// ON/OFF を切り替える (設定画面の項目と F8 から)。戻り値は設定画面の下に出す案内。
        /// OFF にした時は LogOutput.log を RS2QoL-diag\ に複製し (次の起動で上書きされないように)、エクスプローラでその複製を選択した
        /// 状態でフォルダを開く (利用者が保存先を探さなくて済むように。ユーザー要望 2026-10-18)。開くのは 1 回の起動で 1 度だけ、
        /// 複製は OFF のたびに最新の内容で上書き (同じ起動の中では同じファイル)。ON のまま終了した時は OnQuit が複製して開く。
        /// </summary>
        public static string SetEnabled(bool on, string via)
        {
            if (Plugin.DiagEnabled == null) return "";
            Plugin.DiagEnabled.Value = on;
            Plugin.Log.LogInfo("[Diag] " + (on ? "ON" : "OFF") + " via " + via + " (log = " + LogPath + ")");
            if (on) { s_recorded = true; return @"BepInEx\LogOutput.log に記録中 (OFF で RS2QoL-diag\ に取り分け)"; }
            bool copied = CopyLog();
            bool opened = OpenLogFolderOnce(copied ? CopyPath : LogPath);
            if (copied) return opened ? @"BepInEx\RS2QoL-diag\ に取り分けて、そのフォルダを開きました" : @"BepInEx\RS2QoL-diag\ に取り分けました";
            return opened ? "記録を止めました。ログのフォルダを開きました" : "記録を止めました (ログは残っています)";
        }

        public static string Toggle(string via) { return SetEnabled(!Enabled, via); }

        /// <summary>目印の行 ([MARK]) を書く (パッドの Start+Select、キーボードの MarkKey から)。左下の表示もしばらく変える。</summary>
        public static void Mark(string via)
        {
            Plugin.Log.LogInfo("[MARK] ---------------- " + via + " pressed at " + T());
            NoteMark();
            Interp60.RequestDump();
        }

        /// <summary>
        /// LogOutput.log を CopyPath に複製する (書き切ってから。BepInEx は FileShare.Read で開いているので読める)。成功したら true。
        /// </summary>
        public static bool CopyLog()
        {
            try
            {
                Plugin.FlushLog();
                if (!System.IO.Directory.Exists(CopyDir)) System.IO.Directory.CreateDirectory(CopyDir);
                using (System.IO.FileStream src = new System.IO.FileStream(LogPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (System.IO.FileStream dst = new System.IO.FileStream(CopyPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read))
                {
                    // BepInEx は BOM 無しの UTF-8 で書く。メモ帳は BOM 無しの日本語を Shift_JIS と誤判定して化けるので (実機 2026-10-18)、
                    // 複製の先頭には UTF-8 の BOM を付ける
                    dst.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
                }
                Plugin.Log.LogInfo("[Diag] copied log to " + CopyPath);
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Diag] copy log failed: " + e.Message);
                return false;
            }
        }

        /// <summary>エクスプローラで file を選択してフォルダを開く。1 回の起動で 1 度だけ (開けたら true)。</summary>
        public static bool OpenLogFolderOnce(string file)
        {
            if (s_folderOpened) return false;
            s_folderOpened = true;
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\"");
                Plugin.Log.LogInfo("[Diag] opened folder of " + file);
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Diag] open log folder failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Plugin.Update (Unity フレームごと) から: ToggleKey の押下で ON/OFF (設定画面が開いていれば案内も出す)、ON 中の MarkKey で目印。</summary>
        public static void UpdateHotkey()
        {
            if (ToggleKey != null && ToggleKey.Value != KeyCode.None && Input.GetKeyDown(ToggleKey.Value))
                ConfigMenu.SetStatus(Toggle("key " + ToggleKey.Value));
            if (Enabled && MarkKey != null && MarkKey.Value != KeyCode.None && Input.GetKeyDown(MarkKey.Value))
                Mark(MarkKey.Value.ToString());
        }

        /// <summary>終了時 (Plugin.OnApplicationQuit): この起動で記録したなら複製し、フォルダをまだ開いていなければ開く。</summary>
        public static void OnQuit()
        {
            if (!s_recorded) return;
            bool copied = CopyLog();
            OpenLogFolderOnce(copied ? CopyPath : LogPath);
        }

        /// <summary>Start+Select の目印を書いた (左下の表示をしばらく「目印を記録しました」にする)。</summary>
        public static void NoteMark() { s_markTime = Time.realtimeSinceStartup; }

        /// <summary>
        /// 診断ログが ON の間、画面の左下に小さく「診断ログ 記録中」と出す (Plugin.OnGUI から、Unity フレームごと)。
        /// 設定画面から ON にした利用者に「今は記録している」と分かるようにするため (ユーザー要望 2026-10-18。コンソール窓は
        /// フルスクリーンで見えず、× で閉じるとゲームごと落ちるので使わない)。タイトルでも出す。設定画面が開いている間はその下になる。
        /// Start+Select の目印の直後 2.5 秒は「目印を記録しました」。
        /// </summary>
        public static void DrawBadge()
        {
            if (!Enabled) return;
            int size = Mathf.Max(11, Screen.height / 45);
            if (s_badge == null || s_badgeSize != size)
            {
                s_badgeSize = size;
                s_badge = new GUIStyle(GUI.skin.label);
                s_badge.fontSize = size;
                s_badge.fontStyle = FontStyle.Bold;
                s_badge.alignment = TextAnchor.LowerLeft;
                s_badge.normal.textColor = new Color(0.55f, 0.9f, 1f);
            }
            string text = Time.realtimeSinceStartup - s_markTime <= 2.5f ? "診断ログ 目印を記録しました" : "診断ログ 記録中";
            Rect r = new Rect(size / 2, Screen.height - size * 2, size * 20, size * 1.5f);
            Color c = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, s_badge);
            GUI.color = c;
            GUI.Label(r, text, s_badge);
        }

        public static string T()
        {
            return Time.realtimeSinceStartup.ToString("F2");
        }
    }

    [HarmonyPatch(typeof(Util), nameof(Util.GetKeypadState))]
    [HarmonyPriority(Priority.Low)] // AxisThresholdPatch (XInput 合成) の後に走らせ、合成後の key を記録する
    internal static class InputDiagPadPatch
    {
        private static int s_prevKey = -1;
        private static int s_prevAxes = int.MinValue;
        private static int s_prevXi = -1;
        private static float s_prevT = -1f;
        private static bool s_prevMark;
        private static int s_prevXb = -1;

        // XInput と Unity の押下の見え方の差を物理ボタンごとに追う (Unity の仮想ボタンがある先頭 8 個)。
        // XInput は論理フレームの中で直接読むのに対し Unity はフレーム先頭で取り込むので、XInput の方が 1 フレーム程度
        // 先に見えるのが普通。先に見えただけなら取りこぼしではない (ButtonRemapPatch が XInput を OR して渡している)。
        //   [XiLead]   XInput が先に見えてから Unity に現れるまでが 2 Unity フレーム以上あった (Unity 経路の遅れが大きい)
        //   [XiDrop]   離すまで Unity に一度も現れなかった (Unity 経路の取りこぼし。XInput の併読で救われている)
        //   [UnityOnly] 離すまで XInput に一度も現れなかった (XInput 側の取りこぼし、または非 XInput パッド)
        //   [UnityLate] XInput が離したと見てから Unity が離したと報告するまで 2 Unity フレーム以上あった (Unity 側が押したまま固まる)
        private static readonly int[] s_pressFrame = new int[PadMap.UNITY_BUTTONS]; // 押し始めの Time.frameCount (0 = 離している)
        private static readonly int[] s_xiReleaseFrame = new int[PadMap.UNITY_BUTTONS]; // XInput が離したと見た Time.frameCount (0 = まだ)
        private static readonly bool[] s_seenXi = new bool[PadMap.UNITY_BUTTONS];
        private static readonly bool[] s_seenUnity = new bool[PadMap.UNITY_BUTTONS];

        private static void TrackButtonSources()
        {
            int frame = Time.frameCount;
            for (int i = 0; i < PadMap.UNITY_BUTTONS; i++)
            {
                bool xi = XInputPad.IsButtonDown(i);
                bool un = PadMap.IsPhysicalDownUnity(i);
                if (xi || un)
                {
                    if (s_pressFrame[i] == 0)
                    {
                        s_pressFrame[i] = frame;
                        s_seenXi[i] = xi;
                        s_seenUnity[i] = un;
                    }
                    else
                    {
                        if (xi) s_seenXi[i] = true;
                        if (un && !s_seenUnity[i])
                        {
                            s_seenUnity[i] = true;
                            int lead = frame - s_pressFrame[i];
                            if (lead >= 2)
                            {
                                Plugin.Log.LogInfo(string.Format("[XiLead] t={0} {1}: Unity saw it {2} frames after XInput", InputDiag.T(), PadMap.PhysicalNames[i], lead));
                            }
                        }
                    }
                }
                else if (s_pressFrame[i] != 0)
                {
                    int held = frame - s_pressFrame[i];
                    if (!s_seenUnity[i])
                    {
                        Plugin.Log.LogInfo(string.Format("[XiDrop] t={0} {1}: held {2} frames, Unity never saw it (fed from XInput)", InputDiag.T(), PadMap.PhysicalNames[i], held));
                    }
                    else if (!s_seenXi[i] && XInputPad.AnyConnected)
                    {
                        Plugin.Log.LogInfo(string.Format("[UnityOnly] t={0} {1}: held {2} frames, XInput never saw it", InputDiag.T(), PadMap.PhysicalNames[i], held));
                    }
                    else if (s_xiReleaseFrame[i] != 0 && frame - s_xiReleaseFrame[i] >= 2)
                    {
                        // Unity 側が離したと報告するのが XInput より 2 フレーム以上遅い (押したまま固まる現象。2026-10-08 に 0.56 秒の例)
                        Plugin.Log.LogInfo(string.Format("[UnityLate] t={0} {1}: Unity released {2} frames after XInput (held {3})", InputDiag.T(), PadMap.PhysicalNames[i], frame - s_xiReleaseFrame[i], held));
                    }
                    s_pressFrame[i] = 0;
                    s_xiReleaseFrame[i] = 0;
                }
                if (s_pressFrame[i] != 0 && s_seenXi[i] && !xi && s_xiReleaseFrame[i] == 0) s_xiReleaseFrame[i] = frame;
            }
        }

        private static void Postfix(int __result)
        {
            if (!InputDiag.Enabled) return;

            // [Hitch] 論理フレームの間隔が大きく空いた (本体側の処理落ち・描画停止の疑い)
            float now = Time.realtimeSinceStartup;
            if (s_prevT >= 0f && now - s_prevT > 0.08f)
            {
                Plugin.Log.LogInfo(string.Format("[Hitch] t={0} gap={1}ms", InputDiag.T(), (int)((now - s_prevT) * 1000f)));
            }
            s_prevT = now;

            float h1 = Input.GetAxis("STEAM_Horizontal");
            float v1 = Input.GetAxis("STEAM_Vertical");
            float h2 = Input.GetAxis("STEAM_Horizontal_sub");
            float v2 = Input.GetAxis("STEAM_Vertical_sub");
            int axes = Q(h1) * 1000003 + Q(v1) * 10007 + Q(h2) * 101 + Q(v2);
            int xi = XInputPad.ReadDpad(); // XInput で読んだ十字キー (Unity の軸と食い違えば読み取り経路の問題)
            int xb = XInputPad.LastButtons & 0xF330; // XInput のボタン (A/B/X/Y/LB/RB/Start/Back)。[Pad] 行の xb=
            TrackButtonSources();

            // Select は ButtonRemapPatch で MOD 設定画面に割り当てられ 0x200 では来なくなったので、
            // 目印は Start+Select の同時押しにする (物理ボタンを直接見るので割り当てに依らない)
            bool mark = PadMap.IsPhysicalDown(6) && PadMap.IsPhysicalDown(7);
            if (mark && !s_prevMark) InputDiag.Mark("Start+Select");
            s_prevMark = mark;
            // xb (XInput のボタン) も変化条件に入れる: MOD 側 (確認ダイアログや閉じた直後のガード) が飲み込んで key に出ない押下も、
            // パッドが押されたこと自体は残すため
            if (__result != s_prevKey || axes != s_prevAxes || xi != s_prevXi || xb != s_prevXb)
            {
                s_prevKey = __result;
                s_prevAxes = axes;
                s_prevXi = xi;
                s_prevXb = xb;
                // fg= ゲームのウィンドウがフォアグラウンドか (0 なら XInput は止まっていて Unity の HID 経路だけで読んでいる)
                Plugin.Log.LogInfo(string.Format(
                    "[Pad] t={0} key=0x{1:X} dir=0x{2:X} stick=({3:F2},{4:F2}) dpad=({5:F2},{6:F2}) xi=0x{7:X} xb=0x{8:X} xiconn={9} fg={10}",
                    InputDiag.T(), __result, __result & 0xF0000, h1, v1, h2, v2, xi, xb,
                    XInputPad.AnyConnected ? 1 : 0, WindowMode.IsGameForeground() ? 1 : 0));
            }
        }

        private static int Q(float v)
        {
            return (int)(v * 100f);
        }
    }

    [HarmonyPatch(typeof(Sys), nameof(Sys.syncKey))]
    internal static class InputDiagKeyPatch
    {
        private static int s_prev = -1;

        private static void Postfix()
        {
            if (!InputDiag.Enabled) return;
            int d = Sys.key[0] & 0xF0000;
            if (d != s_prev)
            {
                s_prev = d;
                Plugin.Log.LogInfo(string.Format("[Key] t={0} key[0] dir=0x{1:X} edge=0x{2:X}", InputDiag.T(), d, Sys.key[1] & 0xF0000));
            }
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.move_by_key))]
    internal static class InputDiagFieldPatch
    {
        private static int s_prev = -1;

        private static void Postfix(Core __instance)
        {
            if (!InputDiag.Enabled) return;
            int d = __instance.human_key & 0xF;
            if (d != s_prev)
            {
                s_prev = d;
                Plugin.Log.LogInfo(string.Format(
                    "[Field] t={0} human_key={1} move_dir={2} scrol(v={3},h={4}) cant_move={5} ivent={6} jump={7}",
                    InputDiag.T(), d, __instance.move_direction,
                    __instance.map_scrol_flag_v, __instance.map_scrol_flag_h,
                    __instance.man_cant_move, __instance.ivent_sw, __instance.jump_flag));
            }
        }
    }
}

namespace RS2QoL.Patches
{
    /// <summary>
    /// [STALL] 検出器: フィールドで方向キーを押しているのにマス移動が始まらない状態が
    /// 6 フレーム以上続いたら 1 回だけ記録する (再び動き出したら [RESUME])。
    /// 「押し続けているのに止まった」の瞬間の阻害フラグを見るため。
    /// </summary>
    [HarmonyPatch(typeof(Core), "move_human_sub_r")]
    internal static class InputDiagStallPatch
    {
        private static int s_stallFrames;
        private static bool s_stalled;
        private static bool s_prevStepping;

        private static void Postfix(Core __instance)
        {
            if (!InputDiag.Enabled) return;
            Core c = __instance;
            bool holdingDir = (c.joy_data[0] & 0xF0000) != 0;
            bool stepping = c.map_scrol_flag_v != 0 || c.map_scrol_flag_h != 0
                         || c.man_move_flag_v != 0 || c.man_move_flag_h != 0;

            // [Step] マス移動が始まった瞬間 (方向と速度)。止まって見えた区間にこれが無ければ本当に止まっている
            if (stepping && !s_prevStepping)
            {
                Plugin.Log.LogInfo(string.Format("[Step] t={0} dir={1} speed={2} joy=0x{3:X}",
                    InputDiag.T(), c.move_direction, c.speed_count, c.joy_data[0] & 0xF0000));
            }
            s_prevStepping = stepping;

            if (holdingDir && !stepping)
            {
                s_stallFrames++;
                if (s_stallFrames == 6 && !s_stalled)
                {
                    s_stalled = true;
                    Plugin.Log.LogInfo(string.Format(
                        "[STALL] t={0} joy=0x{1:X} human_key={2} cant_move={3} ivent={4} ivent_stop={5} mess={6} road={7} demo={8} pause={9} dash={10} jump={11} work_p0={12} menukey_off={13} fade={14}/{15} exevt={16} battle={17} tono={18} exdemo={19} proc_nmi={20} vehicle={21} nagasare={22}",
                        InputDiag.T(), c.joy_data[0], c.human_key, c.man_cant_move, c.ivent_sw, c.ivent_stop_flag,
                        c.mess_flag, c.man_road_sw, c.demo_flag, c.pause_sw, c.dash_flag, c.jump_flag, c.work_p[0],
                        c.menukey_off, c.fade_now, Sys.fadeflg, c.exevt_flg, c.battle_flg, c.tono_flg, c.exdemo_flg,
                        c.proc_nmi, c.vehicle_flag, c.nagasare_flag));
                }
            }
            else
            {
                if (s_stalled && stepping)
                {
                    Plugin.Log.LogInfo(string.Format("[RESUME] t={0} after {1} frames joy=0x{2:X}", InputDiag.T(), s_stallFrames, c.joy_data[0]));
                }
                if (!holdingDir || stepping)
                {
                    s_stallFrames = 0;
                    s_stalled = false;
                }
            }
        }
    }
}
