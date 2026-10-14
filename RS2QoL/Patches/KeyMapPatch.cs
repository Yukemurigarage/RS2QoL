using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// キーボードの割り当て (機能 → キー)。設定画面の「キー割り当て」ページで変える。
    ///
    /// 本体のキーボード操作は Unity の InputManager の仮想ボタン KEY_Button0..3 / Start / Select / LB / RB (globalgamemanagers に固定:
    /// 決定 = X / Enter、キャンセル = 右 Shift、X 相当 = V、Y 相当 = C、Start = BackSpace、Select = 左 Ctrl、LB = Q、RB = E) で、
    /// 本体には変える画面が無い。ButtonRemapPatch が Util.GetKeypadState の結果からその分のビット (PadMap.KeyboardBits) を外し、
    /// ここで押されているキーの機能ビット (Bits) を立て直す。ゲームに渡らない機能 (設定画面・倍速・タイトルへ・終了・大臣) は
    /// PadMap.IsFunctionDown が IsDown を併せて見る。
    /// 方向 (W A S D と矢印キー、KEY_Up 等) はそのまま本体に任せる。F1 (設定画面) と F11 (本体の画面切替) は固定。
    /// Esc は割り当てられる (既定 = キャンセルのみ)。Esc がどれかの機能に付いている間は、本体が Esc で開く終了ダイアログを
    /// 開かせない (EscCancelPatch。終了は Quit 機能か設定画面「その他」から)。Esc がどの機能にも無ければ本体どおり終了ダイアログ。
    /// 既定は WASD 系の PC ゲームの共通配置 (v0.32.0。それまでは本体のキー操作 X / 右 Shift / V / C / BackSpace / Q / E):
    /// 左手を W A S D から動かさずに届くキーに寄せる (Space = 決定、左 Shift = 走る、Tab = 持ち物、M = 地図、Q / E = 肩ボタン)。
    /// </summary>
    internal static class KeyMap
    {
        /// <summary>ページに並べる機能 (上から順)。決定だけ 2 行 (本体も X と Enter の 2 つが決定だったので、2 つ目を Enter に)。</summary>
        public static readonly PadFunction[] Functions =
        {
            PadFunction.OK, PadFunction.OK, PadFunction.Cancel, PadFunction.CancelOnly, PadFunction.Dash, PadFunction.MapMove, PadFunction.Menu,
            PadFunction.Pause, PadFunction.L, PadFunction.R,
            PadFunction.ConfigMenu, PadFunction.SpeedToggle, PadFunction.SoftReset, PadFunction.Quit, PadFunction.Withdraw, PadFunction.Treasury,
        };
        /// <summary>config の項目名 (Functions と同じ順。2 つ目の決定は "OK2")。</summary>
        public static readonly string[] Names =
        {
            "OK", "OK2", "Cancel", "CancelOnly", "Dash", "MapMove", "Menu",
            "Pause", "L", "R",
            "ConfigMenu", "SpeedToggle", "SoftReset", "Quit", "Withdraw", "Treasury",
        };
        /// <summary>設定画面の行の表示名 (2 つ目の決定だけ添え書き)。</summary>
        public static string RowLabel(int idx)
        {
            return PadMap.FunctionLabel(Functions[idx]) + (Names[idx] == "OK2" ? " (2 つ目)" : "");
        }
        /// <summary>
        /// 既定 (Functions と同じ順)。決定 = Space と Enter (2 つ目)、キャンセル + ダッシュ = 左 Shift、キャンセルのみ = Esc、ダッシュ = なし、
        /// マップ移動 = M、メニュー = Tab、ポーズ = P、前 / 次のページ = Q / E、倍速 = F、どこでも大臣 = T、
        /// ゲームを終了 = F10 (PC ゲームの古典的な慣習: Doom / Wolfenstein 3D の F10 = Quit。確認ダイアログが出るので誤爆しても止まる。
        /// Alt+F4 は Unity が自分で処理して確認なしに即終了するので、MOD では扱わない)。
        /// MOD 設定画面は F1 固定なので なし。ソフトリセット / 引き出しは誤爆が重いので なし。
        /// </summary>
        public static readonly KeyCode[] Defaults =
        {
            KeyCode.Space, KeyCode.Return, KeyCode.LeftShift, KeyCode.Escape, KeyCode.None, KeyCode.M, KeyCode.Tab,
            KeyCode.P, KeyCode.Q, KeyCode.E,
            KeyCode.None, KeyCode.F, KeyCode.None, KeyCode.F10, KeyCode.None, KeyCode.T,
        };
        public static ConfigEntry<KeyCode>[] Entries;

        /// <summary>
        /// 割り当てられるキー (左右で送る順 / 押して決める時に受け付けるもの)。方向の W A S D と矢印、固定の F1 / F11 (本体の画面切替) は入れない。
        /// Esc は入れるが「押して決める」では取り込みの中止に使うので、左右で送って付ける (FirstKeyDown が飛ばす)。
        /// </summary>
        public static readonly KeyCode[] Candidates =
        {
            KeyCode.None, KeyCode.Escape,
            KeyCode.X, KeyCode.Z, KeyCode.C, KeyCode.V, KeyCode.B, KeyCode.N, KeyCode.M,
            KeyCode.Q, KeyCode.E, KeyCode.R, KeyCode.T, KeyCode.Y, KeyCode.U, KeyCode.I, KeyCode.O, KeyCode.P,
            KeyCode.F, KeyCode.G, KeyCode.H, KeyCode.J, KeyCode.K, KeyCode.L,
            KeyCode.Return, KeyCode.Space, KeyCode.Backspace, KeyCode.Tab,
            KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftAlt, KeyCode.RightAlt,
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0,
            KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5, KeyCode.F6, KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F12,
            KeyCode.Insert, KeyCode.Delete, KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown,
            KeyCode.Keypad0, KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4, KeyCode.Keypad5, KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9,
            KeyCode.KeypadEnter, KeyCode.KeypadPlus, KeyCode.KeypadMinus, KeyCode.KeypadMultiply, KeyCode.KeypadDivide, KeyCode.KeypadPeriod,
            KeyCode.Comma, KeyCode.Period, KeyCode.Slash, KeyCode.Semicolon, KeyCode.Minus, KeyCode.Equals,
            KeyCode.LeftBracket, KeyCode.RightBracket, KeyCode.Backslash, KeyCode.Quote, KeyCode.BackQuote,
        };

        public static void Bind(ConfigFile config)
        {
            Entries = new ConfigEntry<KeyCode>[Functions.Length];
            for (int i = 0; i < Functions.Length; i++)
            {
                Entries[i] = config.Bind("KeyMap", Names[i], Defaults[i],
                    "キーボードで「" + RowLabel(i) + "」にするキー (Unity の KeyCode 名。None = なし)。ゲーム内の MOD 設定画面「キー割り当て」からも変更できる");
            }
        }

        public static void LogPatched()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Functions.Length; i++)
            {
                if (Entries[i].Value == KeyCode.None) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(Names[i]).Append('=').Append(Entries[i].Value);
            }
            Plugin.Log.LogInfo("[KeyMap] patched: Util.GetKeypadState (本体の KEY_* を外して " + sb + ")");
        }

        /// <summary>今押されているキーの機能ビット (ゲームに渡す分)。</summary>
        public static int Bits()
        {
            if (Entries == null) return 0;
            int bits = 0;
            for (int i = 0; i < Entries.Length; i++)
            {
                KeyCode k = Entries[i].Value;
                if (k != KeyCode.None && Input.GetKey(k)) bits |= PadMap.FunctionBit(Functions[i]);
            }
            return bits;
        }

        /// <summary>機能 f のキー (決定は 2 つ) のどれかが押されているか。</summary>
        public static bool IsDown(PadFunction f)
        {
            if (Entries == null) return false;
            for (int i = 0; i < Entries.Length; i++)
            {
                if (Functions[i] != f) continue;
                KeyCode k = Entries[i].Value;
                if (k != KeyCode.None && Input.GetKey(k)) return true;
            }
            return false;
        }

        public static bool IsDefault(int idx) { return Entries[idx].Value == Defaults[idx]; }

        /// <summary>Esc がどれかの機能に割り当てられているか。true の間は本体の終了ダイアログを Esc で開かない (EscCancelPatch / Interp60RunStartPatch)。</summary>
        public static bool EscMapped
        {
            get
            {
                if (Entries == null) return false;
                for (int i = 0; i < Entries.Length; i++)
                {
                    if (Entries[i].Value == KeyCode.Escape) return true;
                }
                return false;
            }
        }

        /// <summary>割り当てる。同じキーが他の機能に付いていればそちらを「なし」にする。戻り値は外した機能 (無ければ None)。</summary>
        public static PadFunction Set(int idx, KeyCode k)
        {
            PadFunction cleared = PadFunction.None;
            if (k != KeyCode.None)
            {
                for (int i = 0; i < Entries.Length; i++)
                {
                    if (i != idx && Entries[i].Value == k) { Entries[i].Value = KeyCode.None; cleared = Functions[i]; }
                }
            }
            Entries[idx].Value = k;
            return cleared;
        }

        /// <summary>左右で候補を送る。</summary>
        public static void Cycle(int idx, int delta)
        {
            int cur = System.Array.IndexOf(Candidates, Entries[idx].Value);
            if (cur < 0) cur = 0;
            int n = Candidates.Length;
            Set(idx, Candidates[(cur + delta + n) % n]);
        }

        public static void ResetToDefault()
        {
            for (int i = 0; i < Entries.Length; i++) Entries[i].Value = Defaults[i];
        }

        /// <summary>割り当てられるキーか (Candidates にあるもの)。</summary>
        public static bool IsAssignable(KeyCode k)
        {
            return k != KeyCode.None && System.Array.IndexOf(Candidates, k) >= 0;
        }

        /// <summary>今押されている (割り当てられる) キーのうち最初のもの。無ければ None。押して決める時に使う (Esc は中止に使うので拾わない)。</summary>
        public static KeyCode FirstKeyDown()
        {
            for (int i = 1; i < Candidates.Length; i++)
            {
                if (Candidates[i] == KeyCode.Escape) continue;
                if (Input.GetKeyDown(Candidates[i])) return Candidates[i];
            }
            return KeyCode.None;
        }

        /// <summary>設定画面の「押しているキー」行。</summary>
        public static string PressedText()
        {
            var sb = new StringBuilder();
            for (int i = 1; i < Candidates.Length; i++)
            {
                if (!Input.GetKey(Candidates[i])) continue;
                if (sb.Length > 0) sb.Append(" + ");
                sb.Append(Label(Candidates[i]));
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        /// <summary>表示名。</summary>
        public static string Label(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.None: return "なし";
                case KeyCode.Escape: return "Esc";
                case KeyCode.Return: return "Enter";
                case KeyCode.Space: return "Space";
                case KeyCode.Backspace: return "BackSpace";
                case KeyCode.Tab: return "Tab";
                case KeyCode.LeftShift: return "左 Shift";
                case KeyCode.RightShift: return "右 Shift";
                case KeyCode.LeftControl: return "左 Ctrl";
                case KeyCode.RightControl: return "右 Ctrl";
                case KeyCode.LeftAlt: return "左 Alt";
                case KeyCode.RightAlt: return "右 Alt";
                case KeyCode.KeypadEnter: return "テンキー Enter";
                case KeyCode.KeypadPlus: return "テンキー +";
                case KeyCode.KeypadMinus: return "テンキー -";
                case KeyCode.KeypadMultiply: return "テンキー *";
                case KeyCode.KeypadDivide: return "テンキー /";
                case KeyCode.KeypadPeriod: return "テンキー .";
                case KeyCode.Comma: return ",";
                case KeyCode.Period: return ".";
                case KeyCode.Slash: return "/";
                case KeyCode.Semicolon: return ";";
                case KeyCode.Minus: return "-";
                case KeyCode.Equals: return "=";
                case KeyCode.LeftBracket: return "[";
                case KeyCode.RightBracket: return "]";
                case KeyCode.Backslash: return "\\";
                case KeyCode.Quote: return "'";
                case KeyCode.BackQuote: return "`";
                case KeyCode.PageUp: return "PageUp";
                case KeyCode.PageDown: return "PageDown";
            }
            string s = k.ToString();
            if (s.StartsWith("Alpha")) return s.Substring(5);
            if (s.StartsWith("Keypad")) return "テンキー " + s.Substring(6);
            return s;
        }
    }
}
