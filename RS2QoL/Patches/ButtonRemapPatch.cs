using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>パッドの物理ボタンに割り当てる機能。ゲームが見るビットは PadMap.FunctionBit を参照。</summary>
    public enum PadFunction
    {
        None,
        OK,         // 決定 (フィールドでは調べる/話す)
        Cancel,     // キャンセル + フィールドでダッシュ (ゲーム既定の B)
        Info,       // 情報・切替 (本体の X。フィールドでは SFC 版のマップ移動にもなる。MapMove と同じ)
        Menu,       // メニュー開閉
        Pause,      // ポーズ (Start)
        L,          // 前ページ (LB)。戦闘では SfcLR で退却の呼び出し
        R,          // 次ページ (RB)。戦闘では SfcLR で防御の呼び出し
        ConfigMenu, // MOD 設定画面を開く (ゲームには渡らない)
        SpeedToggle, // 倍速の ON/OFF (ゲームには渡らない)
        SoftReset,  // タイトル画面に戻る (確認あり。ゲームには渡らない)
        CancelOnly, // キャンセルだけ (フィールドでダッシュしない。FieldButtonsPatch)
        Dash,       // ダッシュ (フィールド) / とじる (メニュー・戦闘。ビット 0x200000 = 本体の Y)。フィールドでは FieldButtonsPatch がメニューを開かせずダッシュにする
        MapMove,    // SFC 版の「マップ移動」(建物から出る / 町からワールドマップへ) / 情報・切替。ビット 0x20 = 本体の X と同じ (Info と同じ働き)
        Quit,       // ゲームを終了 (本体の Esc と同じ終了ダイアログを開く。ゲームには渡らない。QuitGamePatch)
        Withdraw,   // 国庫から所持金へ引き出す (どこでも金庫番。フィールドで歩ける時とポーズ中だけ。ゲームには渡らない。WalletPatch)
        Treasury,   // どこでもアバロンの窓: フィールドで自動ポーズして専用の窓 (国庫 ⇄ 所持金) を開く (ゲームには渡らない。TreasuryScreen)
    }

    /// <summary>
    /// パッドの物理ボタン ↔ ゲーム内の機能ビットの対応表。
    /// Util.GetKeypadState は Unity の仮想ボタン STEAM_Button0..3/Start/Select/LB/RB を固定のビットに変換するので、
    /// ここで「物理ボタン名 → 既定のビット (= ゲーム既定の機能)」と「機能 → ビット」を持つ。
    /// </summary>
    internal static class PadMap
    {
        /// <summary>
        /// 物理ボタン名。先頭 8 つは Unity 側の仮想ボタン名 (STEAM_ 接頭辞なし) でゲームが使う。
        /// LT/RT/L3/R3 はゲームが使わない追加分: LT/RT は Unity 側に軸定義が無いので XInput だけで読む、
        /// L3/R3 は KeyCode.JoystickButton8/9 と XInput で読む。順番は設定画面の表示順。
        /// </summary>
        /// <summary>ゲームが Unity の仮想ボタンで読む物理ボタンの数 (PhysicalNames の先頭から)。</summary>
        public const int UNITY_BUTTONS = 8;
        /// <summary>
        /// LB / RB の添字と、その同時押し (仮想の 13 個目、v0.28.0)。フィールドでは LB / RB に本体の役目が無いので、
        /// 同時押しをそのまま「どこでもアバロン」に使える (両方のビットはゲームにも渡るが、フィールドでは何も起きない。
        /// メニュー・戦闘では本体のページ送り / 退却・防御がそのまま効き、どこでもアバロンは開かない)。
        /// </summary>
        public const int PHYS_LB = 4, PHYS_RB = 5, CHORD = 12;
        public static readonly string[] PhysicalNames = { "Button0", "Button1", "Button2", "Button3", "LB", "RB", "Start", "Select", "LT", "RT", "L3", "R3", "LB+RB" };
        /// <summary>設定画面で見せる表示名 (Xbox 配列を想定。実機で違えば「入力テスト」行で確認できる)。</summary>
        public static readonly string[] PhysicalLabels = { "A (Button0)", "B (Button1)", "X (Button2)", "Y (Button3)", "LB", "RB", "Start", "Select (Back)", "LT (ZL)", "RT (ZR)", "L3 (左スティック押し込み)", "R3 (右スティック押し込み)", "LB + RB 同時押し" };
        /// <summary>Util.GetKeypadState が各物理ボタンに立てるビット (ゲーム既定の割り当て)。</summary>
        public static readonly int[] PhysicalBits = { 0x100000, 0x400000, 0x20, 0x200000, 0x10, 0x40, 0x100, 0x200, 0, 0, 0, 0, 0 };
        /// <summary>
        /// 既定の機能。v0.9.0 で SFC 版の配置 (物理位置で対応: 下 = キャンセル、右 = 決定、左 = ダッシュ、上 = マップ移動) に変えた。
        /// A = キャンセルのみ、B = 決定、X = ダッシュのみ、Y = マップ移動、Select = メニュー、LT = タイトルに戻る、RT = 倍速、
        /// L3 = MOD 設定画面、R3 = ゲームを終了 (v0.21.0。それまでは R3 も MOD 設定画面)。移動に使う左スティックの押し込みで終了ダイアログが出ないよう、終了は右側に置く。
        /// LB + RB 同時押し = なし (v0.28.0。どこでもアバロンは既定ではフィールドの LB 単独 ([Economy] FieldLB)。同時押しに割り当てるなら FieldLB / FieldRB を None に)。
        /// 以前の既定は DefaultsHistory (Bind の移行判定に使う)。
        /// </summary>
        public static readonly PadFunction[] DefaultFunctions =
            { PadFunction.CancelOnly, PadFunction.OK, PadFunction.Dash, PadFunction.MapMove, PadFunction.L, PadFunction.R, PadFunction.Pause, PadFunction.Menu,
              PadFunction.SoftReset, PadFunction.SpeedToggle, PadFunction.ConfigMenu, PadFunction.Quit, PadFunction.None };
        /// <summary>
        /// 過去の既定。[0] = v0.8.1 まで (DefaultsVersion 0/1)、[1] = v0.9.0〜v0.20.0 (DefaultsVersion 2)。
        /// ユーザーの設定が「その世代の既定のまま」の項目だけを今の既定に移す。
        /// </summary>
        private static readonly PadFunction[][] DefaultsHistory =
        {
            new[] { PadFunction.OK, PadFunction.Cancel, PadFunction.Info, PadFunction.Menu, PadFunction.L, PadFunction.R, PadFunction.Pause, PadFunction.ConfigMenu,
                    PadFunction.None, PadFunction.SpeedToggle, PadFunction.None, PadFunction.None },
            new[] { PadFunction.CancelOnly, PadFunction.OK, PadFunction.Dash, PadFunction.MapMove, PadFunction.L, PadFunction.R, PadFunction.Pause, PadFunction.Menu,
                    PadFunction.SoftReset, PadFunction.SpeedToggle, PadFunction.ConfigMenu, PadFunction.ConfigMenu },
        };
        /// <summary>既定の世代。config [ButtonMap] DefaultsVersion がこれより古ければ、旧既定のままの項目だけ新既定に移す。</summary>
        private const int DEFAULTS_VERSION = 3;

        public static readonly PadFunction[] AllFunctions = (PadFunction[])Enum.GetValues(typeof(PadFunction));

        /// <summary>
        /// 設定画面のボタン割り当てで、十字キーの左右で機能を送る順番。
        /// 既定の割り当て (DefaultFunctions) を上から順に並べ、既定に無い機能は近い仲間の隣に入れる
        /// (キャンセル + ダッシュ はキャンセルのみの次、情報 / 切替 はマップ移動の次。なし は最後)。
        /// 順番を変えたい時はこの並びを書き換える。ここに書き忘れた機能は末尾に足される。
        /// </summary>
        public static readonly PadFunction[] CycleOrder = BuildCycleOrder(new[]
        {
            PadFunction.CancelOnly, PadFunction.Cancel,
            PadFunction.OK,
            PadFunction.Dash,
            PadFunction.MapMove, PadFunction.Info,
            PadFunction.L, PadFunction.R,
            PadFunction.Pause,
            PadFunction.Menu,
            PadFunction.SoftReset,
            PadFunction.SpeedToggle,
            PadFunction.ConfigMenu,
            PadFunction.Quit,
            PadFunction.Withdraw,
            PadFunction.Treasury,
            PadFunction.None,
        });

        private static PadFunction[] BuildCycleOrder(PadFunction[] order)
        {
            var list = new System.Collections.Generic.List<PadFunction>(order);
            foreach (PadFunction f in AllFunctions) if (!list.Contains(f)) list.Add(f);
            return list.ToArray();
        }

        public static int FunctionBit(PadFunction f)
        {
            switch (f)
            {
                case PadFunction.OK: return 0x100000;
                case PadFunction.Cancel: return 0x400000;
                case PadFunction.CancelOnly: return 0x400000; // フィールドのダッシュは FieldButtonsPatch が外す
                case PadFunction.Info: return 0x20;
                case PadFunction.Dash: return 0x200000; // フィールドでは FieldButtonsPatch が外してダッシュにする
                case PadFunction.Menu: return 0x200000;
                case PadFunction.MapMove: return 0x20; // 本体の X と同じ (フィールドでマップ移動、メニューで情報・切替)
                case PadFunction.Pause: return 0x100;
                case PadFunction.L: return 0x10;
                case PadFunction.R: return 0x40;
                default: return 0; // None / ConfigMenu はゲームに渡さない
            }
        }

        public static string FunctionLabel(PadFunction f)
        {
            switch (f)
            {
                case PadFunction.None: return "なし";
                case PadFunction.OK: return "決定";
                case PadFunction.Cancel: return "キャンセル + ダッシュ";
                case PadFunction.Info: return "情報 / 切替 (本体の X)";
                case PadFunction.Menu: return "メニュー";
                case PadFunction.Pause: return "ポーズ";
                // 機能名だけにする (パッドの肩ボタン名 L / R はキー割り当てページでは意味が無い。v0.32.0 までは "L (前ページ / 戦闘: 退却)")
                case PadFunction.L: return BattleLR.On ? "前のページ / 戦闘: 退却" : "前のページ";
                case PadFunction.R: return BattleLR.On ? "次のページ / 戦闘: 防御" : "次のページ";
                case PadFunction.ConfigMenu: return "MOD 設定画面";
                case PadFunction.SpeedToggle: return "倍速 ON/OFF";
                case PadFunction.SoftReset: return "ソフトリセット (タイトルへ)";
                case PadFunction.CancelOnly: return "キャンセルのみ";
                case PadFunction.Dash: return "ダッシュ / とじる";
                case PadFunction.MapMove: return "マップ移動 (SFC) / 情報・切替";
                case PadFunction.Quit: return "ゲームを終了 (Steam へ)";
                case PadFunction.Withdraw: return "国庫から引き出す";
                case PadFunction.Treasury: return "どこでもアバロン (金庫番・倉庫・情報室などの窓)";
                default: return f.ToString();
            }
        }

        /// <summary>config に束縛した各物理ボタンの機能。Plugin.Awake で埋める。</summary>
        public static ConfigEntry<PadFunction>[] Entries;

        public static void Bind(ConfigFile config)
        {
            Entries = new ConfigEntry<PadFunction>[PhysicalNames.Length];
            for (int i = 0; i < PhysicalNames.Length; i++)
            {
                Entries[i] = config.Bind("ButtonMap", PhysicalNames[i], DefaultFunctions[i],
                    "パッドの " + PhysicalLabels[i] + " に割り当てる機能。ゲーム内の MOD 設定画面からも変更できる");
            }
            MigrateDefaults(config);
        }

        /// <summary>
        /// 既定の変更 (v0.9.0: SFC 配置。DefaultFunctions 参照) を既存の設定ファイルにも反映する。
        /// BepInEx の config は保存済みの値を優先するので、既定を変えただけでは既存ユーザーには届かない。
        /// ユーザーが変えていない (= 旧既定のままの) 項目だけを新既定に移し、変えてある項目はそのまま残す。
        /// </summary>
        private static void MigrateDefaults(ConfigFile config)
        {
            var ver = config.Bind("ButtonMap", "DefaultsVersion", 0,
                "ボタン割り当ての既定の世代 (MOD が管理する。手で変えない)。これより新しい既定が出た時、旧既定のままの項目だけ新既定へ移す");
            if (ver.Value >= DEFAULTS_VERSION) return;
            // その設定ファイルが作られた世代の既定 (0/1 → [0]、2 → [1])
            PadFunction[] was = DefaultsHistory[Mathf.Clamp(ver.Value - 1, 0, DefaultsHistory.Length - 1)];
            for (int i = 0; i < Entries.Length; i++)
            {
                // 旧世代に無かった物理ボタン (LT+RT 同時押し) は Bind の既定がそのまま入る
                if (i >= was.Length || was[i] == DefaultFunctions[i] || Entries[i].Value != was[i]) continue;
                Plugin.Log.LogInfo("[ButtonMap] default changed: " + PhysicalLabels[i] + " " + was[i] + " -> " + DefaultFunctions[i]);
                Entries[i].Value = DefaultFunctions[i];
            }
            ver.Value = DEFAULTS_VERSION;
        }

        public static void ResetToDefault()
        {
            for (int i = 0; i < Entries.Length; i++) Entries[i].Value = DefaultFunctions[i];
        }

        /// <summary>
        /// 物理ボタン i が今押されているか。
        /// Unity の仮想ボタン (押している) に加えて、
        ///  - GetButtonDown (このフレーム内に押して離した短いタップも 1 フレームは「押している」扱いにする)
        ///  - XInput の併読 (Unity の HID 経路で押下が丸ごと落ちる現象の対策。config [Input] XInputButtons)
        /// を OR する。
        /// </summary>
        public static bool IsPhysicalDown(int i)
        {
            // LB + RB 同時押し (仮想の物理ボタン): 両方が押されている間。LB / RB 単独はそのまま報告する (フィールドでは何も起きない)
            if (i == CHORD) return IsPhysicalDown(PHYS_LB) && IsPhysicalDown(PHYS_RB);
            // 複製フレームで見た押下を、その直後の論理フレームでは「押している」扱いにする (LatchSkippedFrame 参照)
            if ((s_latchMask & (1 << i)) != 0 && Time.frameCount - s_latchFrame <= 1) return true;
            return IsPhysicalDownNow(i);
        }

        // ---------------------------------------------------------------- 複製フレームの押下の持ち越し
        // 60fps 表示 (Interp60) では論理フレーム (Util.GetKeypadState) は 2 Unity フレームに 1 回 (33ms 間隔) しか回らず、
        // パッドの読み取りもその時だけ。Unity の GetButtonDown はその Unity フレームしか true でなく、XInput は状態の
        // ポーリングなので、33ms より短いタップが複製フレームに収まると両方から落ちてゲームに届かない
        // (2026-10-09 実機ログ: 30ms のタップが Unity・XInput 両方に見えずプロローグを飛ばせなかった)。
        // そこで複製フレームでも全ボタンを読み、押されていれば次の論理フレームで「押している」と報告する。
        private static int s_latchMask;
        private static int s_latchFrame = -1;
        private static int s_latchRescued;
        private static int s_lastNoteFrame = -1;

        /// <summary>複製フレーム (論理フレームを回さない Unity フレーム) から呼ぶ。押されているボタンを覚える。</summary>
        public static void LatchSkippedFrame()
        {
            if (Entries == null) return;
            int m = 0;
            for (int i = 0; i < PhysicalNames.Length; i++)
            {
                if (IsPhysicalDownNow(i)) m |= 1 << i;
            }
            if (m == 0) return;
            if (Time.frameCount - s_latchFrame > 1) s_latchMask = 0; // 古い持ち越しは捨てる
            s_latchMask |= m;
            s_latchFrame = Time.frameCount;
        }

        /// <summary>
        /// 論理フレームの先頭 (ButtonRemapPatch の Prefix) から呼ぶ。持ち越した押下のうち今は離されているものを数えて診断に出す。
        /// </summary>
        public static void NoteLatchAtLogicFrame()
        {
            if (s_latchMask == 0 || Time.frameCount - s_latchFrame > 1) return;
            if (Time.frameCount == s_lastNoteFrame) return; // 倍速中は同じ Unity フレームで複数回呼ばれる
            s_lastNoteFrame = Time.frameCount;
            int rescued = 0;
            for (int i = 0; i < PhysicalNames.Length; i++)
            {
                if ((s_latchMask & (1 << i)) != 0 && !IsPhysicalDownNow(i)) rescued |= 1 << i;
            }
            if (rescued != 0)
            {
                s_latchRescued++;
                if (InputDiag.Enabled)
                {
                    Plugin.Log.LogInfo(string.Format("[Pad] latched tap from skipped frame: mask=0x{0:X} (total {1})", rescued, s_latchRescued));
                }
            }
        }

        /// <summary>物理ボタン i が「この Unity フレームの時点で」押されているか (持ち越しを含まない)。</summary>
        private static bool IsPhysicalDownNow(int i)
        {
            if (i == CHORD) return false; // 同時押しは IsPhysicalDown / UpdateChord で判定する (生の状態としては扱わない)
            bool xi = Plugin.XInputButtons == null || Plugin.XInputButtons.Value;
            if (i >= UNITY_BUTTONS)
            {
                if (ButtonsExclusive) return XInputPad.IsButtonDown(i);
                if (i == 10 && (Input.GetKey(KeyCode.JoystickButton8) || Input.GetKeyDown(KeyCode.JoystickButton8))) return true;
                if (i == 11 && (Input.GetKey(KeyCode.JoystickButton9) || Input.GetKeyDown(KeyCode.JoystickButton9))) return true;
                return XInputPad.IsButtonDown(i);
            }
            if (ButtonsExclusive) return XInputPad.IsButtonDown(i);
            if (IsPhysicalDownUnity(i)) return true;
            return xi && XInputPad.IsButtonDown(i);
        }

        /// <summary>
        /// XInput のパッドが繋がっている間はボタンを XInput だけから読むか (config [Input] XInputButtons + XInputButtonsExclusive)。
        /// 2026-10-08 実測: 「はい」で押した A を XInput は 0.14 秒で離したと見たのに、Unity 側は 0.56 秒押したままと報告し続けた。
        /// OR では離れないので「全ボタンを離すまで入力を渡さない」ガードが解けず、その間に押した A が消えた
        /// (十字キーで見つかった「Unity の読みが固まる」現象と同じ)。
        /// </summary>
        public static bool ButtonsExclusive
        {
            get
            {
                return (Plugin.XInputButtons == null || Plugin.XInputButtons.Value)
                    && (Plugin.XInputButtonsExclusive == null || Plugin.XInputButtonsExclusive.Value)
                    && XInputPad.AnyConnected;
            }
        }

        /// <summary>Unity の仮想ボタンがあるパッドのボタン (先頭 8 個) のゲーム既定ビットの集合。</summary>
        public static int PadBits
        {
            get
            {
                int bits = 0;
                for (int i = 0; i < UNITY_BUTTONS; i++) bits |= PhysicalBits[i];
                return bits;
            }
        }

        /// <summary>キーボード (KEY_* 仮想ボタン。Util.GetKeypadState と同じ) が立てるゲーム既定ビット。割り当て変更の対象外。</summary>
        public static int KeyboardBits()
        {
            int bits = 0;
            for (int i = 0; i < UNITY_BUTTONS; i++)
            {
                if (Input.GetButton("KEY_" + PhysicalNames[i])) bits |= PhysicalBits[i];
            }
            return bits;
        }

        /// <summary>診断用: Unity 側だけで見た押下 (XInput なし)。</summary>
        public static bool IsPhysicalDownUnity(int i)
        {
            if (i >= UNITY_BUTTONS) return false;
            string name = "STEAM_" + PhysicalNames[i];
            return Input.GetButton(name) || Input.GetButtonDown(name);
        }

        /// <summary>今押されている物理ボタンのうち、機能 f が割り当てられているものがあるか。</summary>
        public static bool IsFunctionDown(PadFunction f)
        {
            if (KeyMap.IsDown(f)) return true;   // キーボードの割り当て (KeyMapPatch)
            if (Entries == null) return false;
            for (int i = 0; i < Entries.Length; i++)
            {
                if (Entries[i].Value == f && IsPhysicalDown(i)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// ボタン割り当ての入れ替え (候補 #1)。
    /// Util.GetKeypadState の結果から「パッドで押されている物理ボタン」の既定ビットを外し、
    /// 設定された機能のビットを立て直す。キーボード (KEY_*) の分はそのまま残す。
    /// 方向ビットには触らないので AxisThresholdPatch (Priority.High、先に走る) と干渉しない。
    /// </summary>
    [HarmonyPatch(typeof(Util), nameof(Util.GetKeypadState))]
    [HarmonyPriority(Priority.Normal)]
    internal static class ButtonRemapPatch
    {
        private static void Postfix(ref int __result)
        {
            if (PadMap.Entries == null) return;
            PadMap.NoteLatchAtLogicFrame();
            int add = 0;
            for (int i = 0; i < PadMap.PhysicalNames.Length; i++)
            {
                if (PadMap.IsPhysicalDown(i)) add |= PadMap.FunctionBit(PadMap.Entries[i].Value);
            }
            if (PadMap.ButtonsExclusive)
            {
                // XInput 専用: Unity 側 (STEAM_*) が立てたパッドのビットは固まっている可能性があるので全部外し、
                // キーボード (KEY_*) の分を読み直して残し、XInput で押しているボタンの機能ビットを立てる
                // キーボードは本体の KEY_* の分 (PadBits に含まれて外れる) の代わりに、キー割り当て (KeyMap) の機能ビットを立てる
                __result = (__result & ~PadMap.PadBits) | KeyMap.Bits() | add;
                return;
            }
            int remove = 0;
            for (int i = 0; i < PadMap.UNITY_BUTTONS; i++)
            {
                if (PadMap.IsPhysicalDownUnity(i)) remove |= PadMap.PhysicalBits[i];
            }
            // 本体の KEY_* (固定のキー) の分は外し、キー割り当て (KeyMap) の機能ビットを立てる
            __result = (__result & ~remove & ~PadMap.KeyboardBits()) | add | KeyMap.Bits();
        }
    }
}
