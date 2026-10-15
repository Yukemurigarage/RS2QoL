using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    internal enum SpeedIndicator { Always, OnToggle, Off }

    /// <summary>
    /// ゲーム速度 (倍速)。
    ///
    /// 本体はガラケー版の移植で、ゲームの進行は 1 Unity フレーム = 1 論理フレーム (Main.Disp.run、33ms 刻み) の
    /// フレーム処理になっている。そこで Main.update (= disp.run を 1 回呼ぶ) の Postfix で disp.run を追加で回し、
    /// 1 フレームに倍率回分だけ進める。描画も毎回行われるが最後の結果が画面に残るだけなので見た目は問題ない。
    /// SpriteStudio 製 UI のアニメは Time.deltaTime 基準なので速くならない (メニューの見た目は据え置き)。
    ///
    /// ON/OFF はパッドの SpeedToggle 機能 (ButtonMap で割り当て) か MOD 設定画面で切り替える。
    /// TriggerHold (既定 Off) にすると、パッドの倍速ボタンは押している間だけ倍速になり、LT / RT に割り当てていれば
    /// 引いた深さ (XInput の 0..255 を %) で x2 / x3 / x4 を決める (HoldX2/X3/X4Percent、下げる時は HoldReleasePercent のゆとり)。
    /// 離すと押す前の状態 (OFF、または戦闘の自動倍速など) に戻る。キーボードの倍速キーと設定画面は切り替え式のまま。
    /// 設定画面を開いている間と、フィールドのポーズ中は追加で回さない。
    /// フィールドのメニュー (店・預かり所なども) を開いている間は MenuSpeed の速さ (既定は等倍) にする (InFieldMenu)。
    /// ロゴ・タイトル・ロード画面 (Core.proc[0] == 0) でも回さない (InGame 参照)。
    /// </summary>
    internal static class GameSpeed
    {
        public static ConfigEntry<int> Multiplier;
        public static ConfigEntry<bool> StartEnabled;
        public static ConfigEntry<SpeedIndicator> Indicator;
        public static ConfigEntry<bool> ResetAfterBattle;
        public static ConfigEntry<int> BattleSpeed;
        public static ConfigEntry<int> CommandSpeed;
        public static ConfigEntry<int> MenuSpeed;
        /// <summary>倍速ボタンを「押している間だけ」にする (トリガーなら引いた深さで x2 / x3 / x4)。</summary>
        public static ConfigEntry<bool> TriggerHold;
        public static ConfigEntry<int> HoldX2Percent, HoldX3Percent, HoldX4Percent, HoldReleasePercent;
        /// <summary>押している間だけモードの今の段 (0 = 離している / 2〜4 = その倍率)。</summary>
        private static int s_holdLevel;
        /// <summary>押し始める前に倍速が ON だったか (離した時に戻すため)。</summary>
        private static bool s_activeBeforeHold;
        private static bool s_holdLogged;
        private static float s_toggleTime = -100f;
        /// <summary>直近の切り替えが「倍速 → 等倍」だったか (右上に x1 を出すため)。</summary>
        private static bool s_toggledOff;

        /// <summary>今 倍速が ON か (トグル)。</summary>
        public static bool Active;

        private static bool s_prevToggleBtn;
        private static int s_lastToggleFrame = -1;
        private static GUIStyle s_style;
        private static int s_styleSize = -1;

        public static void Bind(ConfigFile config)
        {
            Multiplier = config.Bind("Speed", "Multiplier", 2,
                new ConfigDescription("倍速 ON 時の倍率 (1 Unity フレームにゲームを何フレーム進めるか)", new AcceptableValueRange<int>(1, 4)));
            StartEnabled = config.Bind("Speed", "StartEnabled", false,
                "起動時から倍速を ON にする (効き始めるのはタイトルを抜けて本編に入ってから)");
            Indicator = config.Bind("Speed", "Indicator", SpeedIndicator.Always,
                "倍速中の右上の x2 等の表示。Always=常に表示 / OnToggle=切り替えた直後だけ 2 秒表示 / Off=表示しない (有機 EL の焼き付き対策)。等倍に戻した直後は Off 以外で x1 を 2 秒表示");
            ResetAfterBattle = config.Bind("Speed", "ResetAfterBattle", false,
                "戦闘が終わってフィールドに戻る時に倍速を OFF (等倍) に戻す");
            BattleSpeed = config.Bind("Speed", "BattleSpeed", 0,
                new ConfigDescription("戦闘中の速さ (%)。0=変えない (フィールドと同じ) / 100=戦闘が始まったら等倍にする / 125〜400=戦闘が始まったら必ずその速さの倍速にする (25 刻み。150 なら x1.5。戦闘中は Multiplier の代わりにこの速さ)。ResetAfterBattle と組み合わせるとフィールドは等倍・戦闘だけ倍速にできる。v0.14 以前の 1〜4 (倍率) は起動時に % に直す", new AcceptableValueRange<int>(0, PERCENT_MAX)));
            CommandSpeed = config.Bind("Speed", "CommandSpeed", 0,
                new ConfigDescription("戦闘のコマンド選択中 (ターンの頭からコマンドを確定して行動が始まるまで。Battle.stage 0〜2) の速さ (%)。0=変えない (BattleSpeed / Multiplier のまま) / 100=等倍 / 125〜400=その速さ (25 刻み)。行動の実行 (技・攻撃・被弾) と結果表示は BattleSpeed のまま。倍速 ON の時だけ効く (OFF なら全部等倍)。「コマンド選択は x4 で飛ばし、行動は x1.5」のような使い方", new AcceptableValueRange<int>(0, PERCENT_MAX)));
            MenuSpeed = config.Bind("Speed", "MenuSpeed", 100,
                new ConfigDescription("フィールドのメニューを開いている間 (メニュー・店・預かり所・道場・皇帝継承。開く時のフェードから閉じて戻るまで。Core.tono_flg) の速さ (%)。0=変えない (Multiplier のまま) / 100=等倍 (倍速中でもメニューの中は等倍にして、仲間の絵の動きや文字送りを普通の速さにする) / 125〜400=その速さ (25 刻み)。閉じると元の速さに自動で戻る (Multiplier は書き換えない)。倍速 ON の時だけ効く。エンディングには効かない", new AcceptableValueRange<int>(0, PERCENT_MAX)));
            TriggerHold = config.Bind("Speed", "TriggerHold", false,
                "倍速ボタン (ButtonMap の SpeedToggle) を押している間だけ倍速にする。LT / RT に割り当てていれば引いた深さで x2 / x3 / x4 (下の 4 つの %)、他のボタンなら押している間 Multiplier。離すと押す前の状態に戻る。キーボードの倍速キーと設定画面の ON/OFF は今までどおり切り替え式");
            HoldX2Percent = config.Bind("Speed", "HoldX2Percent", 10,
                new ConfigDescription("TriggerHold でトリガーを引いた深さがこの % 以上なら x2 (1〜100)", new AcceptableValueRange<int>(1, 100)));
            HoldX3Percent = config.Bind("Speed", "HoldX3Percent", 0,
                new ConfigDescription("TriggerHold でこの % 以上なら x3 (既定 0 = x3 の段を使わず、浅く引いて x2・深く引いて x4 の 2 段。使うなら x2 と x4 の間の値)", new AcceptableValueRange<int>(0, 100)));
            HoldX4Percent = config.Bind("Speed", "HoldX4Percent", 75,
                new ConfigDescription("TriggerHold でこの % 以上なら x4 (0 = x4 の段を使わない。x3 の % より大きい値)", new AcceptableValueRange<int>(0, 100)));
            HoldReleasePercent = config.Bind("Speed", "HoldReleasePercent", 5,
                new ConfigDescription("TriggerHold で段を下げる時のゆとり (%)。各段は「その段の % − この値」を下回るまで保つ (既定: x2 は 10 % で入り 5 % を下回ると戻る)。トリガーの震えで段が行き来しないため", new AcceptableValueRange<int>(0, 50)));
            MigratePercent(BattleSpeed);
            MigratePercent(CommandSpeed);
            Active = StartEnabled.Value;
            Plugin.Log.LogInfo("[Speed] patched: Util.GetKeypadState (TriggerHold=" + TriggerHold.Value + " x2/x3/x4=" + HoldX2Percent.Value + "/" + HoldX3Percent.Value + "/" + HoldX4Percent.Value + " % release-" + HoldReleasePercent.Value + " %)");
        }

        /// <summary>戦闘中 / コマンド選択中の速さの上限と刻み (%)。</summary>
        public const int PERCENT_MAX = 400, PERCENT_STEP = 25;
        /// <summary>非 60fps 表示で、1 Unity フレームに回す論理フレーム数の累積 (% 単位の余り)。</summary>
        private static int s_runAcc;

        /// <summary>
        /// 1 Unity フレームに論理フレームを複数回回す時、最後以外の run の間 true。
        /// Graphics.DrawMesh は呼んだ Unity フレームの描画にそのまま積まれるので、最後以外の run の描画を捨てないと
        /// 2 コマ分が同じ画面に重なって描かれ、動いたものの縁が残像 (影) になる (x2.5 なら 4 フレームに 1 回だけ重なるのでちらつく)。
        /// Interp60.DrawMeshPrefix がこれを見て描画を落とす (60fps 表示が Off の人は DrawMesh のパッチが無いので残像は残る)。
        /// </summary>
        internal static bool SuppressDraw;

        /// <summary>v0.14 以前の BattleSpeed / CommandSpeed は倍率 (1〜4) だったので % (100〜400) に直す。</summary>
        private static void MigratePercent(ConfigEntry<int> e)
        {
            if (e.Value >= 1 && e.Value <= 4) e.Value *= 100;
        }

        /// <summary>% の倍率の表記 (100 → "1"、150 → "1.5")。</summary>
        public static string MulText(int pct)
        {
            return pct % 100 == 0 ? (pct / 100).ToString() : (pct / 100f).ToString("0.##");
        }

        /// <summary>設定画面の表記 (0 = 変えない / 100 = 等倍 / それ以外 x1.5 など)。</summary>
        public static string PercentLabel(int pct)
        {
            if (pct <= 0) return "変えない";
            if (pct == 100) return "等倍";
            return "x" + MulText(pct);
        }

        /// <summary>設定画面で 0 → 100 → 125 → … → 400 → 0 と回す。</summary>
        public static void CyclePercent(ConfigEntry<int> e, int delta)
        {
            int v = e.Value;
            if (delta > 0) v = v <= 0 ? 100 : (v >= PERCENT_MAX ? 0 : v + PERCENT_STEP);
            else v = v <= 0 ? PERCENT_MAX : (v <= 100 ? 0 : v - PERCENT_STEP);
            e.Value = v;
        }

        private static bool s_prevInGame;

        /// <summary>
        /// ゲーム本編 (フィールド・戦闘・メニュー・イベント) に入っているか。
        /// 本体は Main.proc == 2 で Core.sync を回し、Core.proc[0] が 0 の間はロゴ・タイトル・ロード画面・
        /// 新規ゲーム直後のロード (first_routine)、1 になってからが本編 (opening_sa。新規ゲームの冒頭デモもここ)。リセットで 0 に戻る。
        /// タイトル周りは実時間のタイマーと論理フレームが混ざっていて倍速にすると壊れるので、ここでは倍速を効かせない。
        /// </summary>
        public static bool InGame()
        {
            bool now = Main.proc == 2 && Main.core != null && Main.core.proc[0] == 1;
            if (now != s_prevInGame)
            {
                s_prevInGame = now;
                Plugin.Log.LogInfo("[Speed] " + (now ? "本編に入った (倍速を有効化)" : "タイトル/ロード中 (倍速を保留)"));
            }
            return now;
        }

        /// <summary>
        /// 今効いている速さ (%、100 = 等倍)。倍速 ON で本編中、設定画面もポーズも開いていなければ CurrentPercent、それ以外は 100。
        /// フィールドは Multiplier (整数倍)、戦闘は BattleSpeed / CommandSpeed (25% 刻み) なので % で持つ。
        /// </summary>
        public static int EffectivePercent()
        {
            if (!Active || Multiplier == null) return 100;
            if (!InGame()) return 100;
            if (ConfigMenu.IsOpen || FieldPause.IsPaused()) return 100;
            return Mathf.Clamp(CurrentPercent(), 100, PERCENT_MAX);
        }

        /// <summary>戦闘中か (Core.battle_flg。戦闘へのフェードから、結果表示が終わって Battle.dest を抜けるまで)。</summary>
        public static bool InBattle()
        {
            return Main.core != null && Main.core.battle_flg != 0;
        }

        /// <summary>
        /// 戦闘のコマンド選択中か (Battle.stage: 0=ターンの頭、1=コマンド入力、2=モンスターの行動決定、3=行動の実行、
        /// 4=ターン終わりの毒・溶岩などの自動ダメージ、5〜7=結果表示。0〜2 を「コマンド選択中」とする)。
        /// </summary>
        public static bool InCommand()
        {
            Battle b = Main.battle;
            return b != null && InBattle() && b.stage <= 2;
        }

        /// <summary>
        /// フィールドのメニュー系の処理中か。本体は Core.call_menu (メニュー) / call_tonos_routine (店 17、預かり所 5、道場 14、皇帝継承 2・13・27、
        /// タイトルへ 22、エンディング 29〜32) が tono_proc_set で Core.tono_flg を 1 にし、Core.sync の tono 分岐が
        /// フェードアウト → Main.menu.sync() を毎フレーム → フェードイン と進めて最後に 0 に戻す。なので tono_flg が立っている間 = 開く時の
        /// フェードから閉じて歩けるようになるまで。エンディング (29〜32) はメニューではなく見るものなので除外する (倍速で飛ばせるように)。
        /// 戦闘中は tono_flg は立たない (戦闘のメニューは Battle 側)。
        /// </summary>
        public static bool InFieldMenu()
        {
            Core c = Main.core;
            bool now = c != null && c.tono_flg != 0 && (c.tono_proc[0] < 29 || c.tono_proc[0] > 32);
            if (now != s_prevInMenu)
            {
                s_prevInMenu = now;
                if (MenuSpeed != null && MenuSpeed.Value >= 100)
                    Plugin.Log.LogInfo("[Speed] " + (now ? "メニュー中 (type " + c.tono_proc[0] + ") → x" + MulText(MenuSpeed.Value) : "メニューを閉じた → x" + MulText(Multiplier.Value * 100)));
            }
            return now;
        }
        private static bool s_prevInMenu;

        /// <summary>
        /// 倍速 ON の時に使う速さ (%)。戦闘のコマンド選択中で CommandSpeed が 100 以上ならそれ、戦闘中で BattleSpeed が 125 以上ならそれ、
        /// フィールドのメニュー中で MenuSpeed が 100 以上ならそれ、それ以外は Multiplier × 100。
        /// </summary>
        public static int CurrentPercent()
        {
            if (Multiplier == null) return 100;
            // 押している間だけモードでトリガーを引いている間は、その深さの倍率 (戦闘の BattleSpeed / CommandSpeed より優先。メニューの等倍は保つ)
            if (s_holdLevel > 0 && !(MenuSpeed != null && MenuSpeed.Value >= 100 && !InBattle() && InFieldMenu())) return s_holdLevel * 100;
            if (InBattle())
            {
                if (CommandSpeed != null && CommandSpeed.Value >= 100 && InCommand()) return CommandSpeed.Value;
                if (BattleSpeed != null && BattleSpeed.Value > 100) return BattleSpeed.Value;
            }
            else if (MenuSpeed != null && MenuSpeed.Value >= 100 && InFieldMenu())
            {
                return MenuSpeed.Value;
            }
            return Multiplier.Value * 100;
        }

        /// <summary>
        /// この Unity フレームで追加で回す論理フレーム数。通常は倍率 - 1。x1.5 のような端数は余りを累積し、
        /// 余りが 100 に達した Unity フレームで 1 回多く回す (1, 2, 1, 2 …)。
        /// 60fps 表示 (Interp60) 中は Unity が 2 倍で回っているので、Interp60 が半分ずつ割り振った値を使う。
        /// </summary>
        public static int ExtraRuns()
        {
            if (Interp60.Active) return Interp60.ExtraRuns;
            int pct = EffectivePercent();
            s_runAcc += pct;
            int runs = s_runAcc / 100;
            s_runAcc %= 100;
            return runs - 1;
        }

        /// <summary>
        /// 実時間 33ms あたりのゲームフレーム数 (倍速中は倍率を四捨五入、それ以外は 1)。
        /// 入力補助のフレーム数しきい値は「実時間のフレーム (≒33ms)」で設定されているので、
        /// ゲームフレームで数えている所はこの値を掛けて実時間を保つ。
        /// </summary>
        public static int FrameScale()
        {
            return (EffectivePercent() + 50) / 100;
        }

        public static void Toggle()
        {
            SetActive(!Active, null);
        }

        /// <summary>倍速の ON/OFF を切り替える。切り替えた時刻を覚えて右上に x2 / x1 を出す。</summary>
        public static void SetActive(bool on, string reason)
        {
            if (Active == on) return;
            Active = on;
            s_toggledOff = !on;
            s_toggleTime = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo("[Speed] " + (on ? "ON x" + MulText(CurrentPercent()) : "OFF") + (reason != null ? " (" + reason + ")" : ""));
        }

        /// <summary>
        /// 戦闘が始まる時 (Battle.init。「○○が現れた」の前)。BattleSpeed が 100 なら等倍に、125 以上なら倍速 ON にする。
        /// 既に ON で倍率だけ変わる時も、切り替え表示 (x3 等) を出すために時刻を更新する。
        /// </summary>
        public static void OnBattleStart()
        {
            if (BattleSpeed == null || BattleSpeed.Value <= 0) return;
            if (BattleSpeed.Value <= 100)
            {
                SetActive(false, "戦闘開始");
                return;
            }
            if (Active)
            {
                if (BattleSpeed.Value != Multiplier.Value * 100)
                {
                    s_toggledOff = false;
                    s_toggleTime = Time.realtimeSinceStartup;
                    Plugin.Log.LogInfo("[Speed] 戦闘中は x" + MulText(BattleSpeed.Value));
                }
                return;
            }
            SetActive(true, "戦闘開始");
        }

        /// <summary>戦闘が終わってフィールドに戻る時 (Battle.dest)。設定が ON なら倍速を OFF に戻す。</summary>
        public static void OnBattleEnd()
        {
            if (ResetAfterBattle == null || !ResetAfterBattle.Value) return;
            // 押している間だけモードでトリガーを引いたまま戦闘が終わったら、引いている間は倍速のまま、離した時に OFF にする
            if (s_holdLevel > 0) { s_activeBeforeHold = false; return; }
            SetActive(false, "戦闘終了");
        }

        /// <summary>
        /// 速度切替ボタンの押下エッジを見る。Util.GetKeypadState の最終段 (ConfigMenuGatePatch) から呼ぶ。
        /// 倍速中は同じ Unity フレームで複数回呼ばれるので、フレーム番号で 1 回に絞る。
        /// </summary>
        public static void HandleToggleButton()
        {
            if (TriggerHold != null && TriggerHold.Value)
            {
                HandleHold();
                // パッドのボタンは「押している間」で使うので、切り替えはキーボードの倍速キーだけ
                bool kdown = KeyMap.IsDown(PadFunction.SpeedToggle);
                if (kdown && !s_prevToggleBtn && Time.frameCount != s_lastToggleFrame && !ConfigMenu.IsOpen)
                {
                    s_lastToggleFrame = Time.frameCount;
                    Toggle();
                }
                s_prevToggleBtn = kdown;
                return;
            }
            if (s_holdLevel != 0) { s_holdLevel = 0; s_toggleTime = Time.realtimeSinceStartup; } // 設定画面で切り替え式に戻した
            bool down = PadMap.IsFunctionDown(PadFunction.SpeedToggle);
            if (down && !s_prevToggleBtn && Time.frameCount != s_lastToggleFrame && !ConfigMenu.IsOpen)
            {
                s_lastToggleFrame = Time.frameCount;
                Toggle();
            }
            s_prevToggleBtn = down;
        }

        /// <summary>
        /// 押している間だけモード。倍速ボタンを割り当てた物理ボタンのうち、LT / RT は XInput の引き量 (0..255) を % にして
        /// HoldX2/X3/X4Percent の段に当て、それ以外のボタンは押していれば Multiplier の段。段を下げるのは
        /// 「今の段の % − HoldReleasePercent」を下回った時 (ヒステリシス)。段が 0 → 正になった時に倍速 ON、正 → 0 で押す前の状態に戻す。
        /// 倍速中は同じ Unity フレームで複数回呼ばれるが、同じ入力を見るだけなので絞らなくてよい。
        /// </summary>
        private static void HandleHold()
        {
            int depth = 0;          // 0..100 (%)
            bool digital = false;   // トリガー以外のボタンで押している
            if (PadMap.Entries != null)
            {
                for (int i = 0; i < PadMap.Entries.Length; i++)
                {
                    if (PadMap.Entries[i] == null || PadMap.Entries[i].Value != PadFunction.SpeedToggle) continue;
                    if (i == 8 || i == 9)
                    {
                        int d = XInputPad.TriggerDepth(i) * 100 / 255;
                        if (d > depth) depth = d;
                    }
                    else if (PadMap.IsPhysicalDown(i)) digital = true;
                }
            }
            int level;
            if (digital) level = Mathf.Clamp(Multiplier.Value, 1, 4);
            else
            {
                int margin = HoldReleasePercent.Value;
                int want = LevelForDepth(depth);
                level = want;
                if (want < s_holdLevel)
                {
                    // 下げるのは今の段のしきい値からゆとり分を下回った時だけ
                    int cur = s_holdLevel == 4 ? HoldX4Percent.Value : s_holdLevel == 3 ? HoldX3Percent.Value : HoldX2Percent.Value;
                    if (depth >= cur - margin) level = s_holdLevel;
                }
            }
            if (ConfigMenu.IsOpen) level = s_holdLevel; // 設定画面を開いている間は変えない (閉じた時に今の深さで決め直す)
            if (level == s_holdLevel) return;
            int prev = s_holdLevel;
            s_holdLevel = level;
            s_toggleTime = Time.realtimeSinceStartup;
            if (prev == 0)
            {
                s_activeBeforeHold = Active;
                if (!Active) { Active = true; s_toggledOff = false; }
            }
            else if (level == 0)
            {
                // 押す前が OFF なら OFF に戻す。ON だった (戦闘の自動倍速など) なら ON のまま
                if (!s_activeBeforeHold && Active) { Active = false; s_toggledOff = true; }
            }
            if (!s_holdLogged) { s_holdLogged = true; Plugin.Log.LogInfo("[Speed] applied: TriggerHold (押している間だけ。深さ " + depth + " % → x" + (level == 0 ? 1 : level) + ")"); }
            if (Plugin.DiagEnabled != null && Plugin.DiagEnabled.Value)
                Plugin.Log.LogInfo("[Speed] hold " + depth + " % → x" + (level == 0 ? 1 : level) + (digital ? " (ボタン)" : "") + " active=" + Active);
        }

        /// <summary>この深さ (%) で入れる最も高い段 (ヒステリシス無し。段の % は上の段ほど大きい前提。0 はその段を使わない)。</summary>
        private static int LevelForDepth(int depth)
        {
            int want = 0;
            if (depth >= HoldX2Percent.Value) want = 2;
            if (HoldX3Percent.Value > 0 && depth >= HoldX3Percent.Value) want = 3;
            if (HoldX4Percent.Value > 0 && depth >= HoldX4Percent.Value) want = 4;
            return want;
        }

        /// <summary>
        /// 押している間だけモードの今の段の表記 (設定画面の確認用)。
        /// 設定画面を開いている間は段を更新しない (裏でゲームの速さが変わらないように) ので、
        /// 画面の中では今の引き量から「閉じたらこの段になる」を計算して出す (% を変えた結果がその場で分かる)。
        /// </summary>
        public static string HoldStateText()
        {
            int level = s_holdLevel;
            if (ConfigMenu.IsOpen)
            {
                // 倍速を割り当てたトリガーだけ見る (LT がソフトリセットなら LT を引いても変わらない)
                int depth = 0;
                if (PadMap.Entries != null)
                    for (int i = 8; i <= 9 && i < PadMap.Entries.Length; i++)
                        if (PadMap.Entries[i] != null && PadMap.Entries[i].Value == PadFunction.SpeedToggle)
                            depth = Mathf.Max(depth, XInputPad.TriggerDepth(i) * 100 / 255);
                level = LevelForDepth(depth);
            }
            if (level == 0) return "x1";
            return "x" + level;
        }

        /// <summary>
        /// 今のトリガーの引き量の表記 (設定画面の確認用。XInput の 0..255 を %)。
        /// 離しているのに 0 でなければトリガーの遊び (ドリフト) なので、x2 の % をその上に置く目安になる。
        /// </summary>
        public static string TriggerDepthText()
        {
            if (!XInputPad.AnyConnected) return "パッドなし";
            int lt = XInputPad.TriggerDepth(8), rt = XInputPad.TriggerDepth(9);
            return "LT " + (lt * 100 / 255) + " %   RT " + (rt * 100 / 255) + " %";
        }

        /// <summary>画面右上に倍速中の表示を出す (Plugin.OnGUI から)。</summary>
        public static void DrawIndicator()
        {
            if (Multiplier == null || Indicator.Value == SpeedIndicator.Off) return;
            if (!InGame()) return;
            string text;
            bool recent = Time.realtimeSinceStartup - s_toggleTime <= 2f;
            if (Active)
            {
                // 倍速中: Always なら常に、OnToggle なら切り替えた直後だけ x2 等 (戦闘中は BattleSpeed / CommandSpeed の速さ。x1.5 のように出る)
                int pct = CurrentPercent();
                if (pct <= 100) return;
                if (Indicator.Value == SpeedIndicator.OnToggle && !recent) return;
                text = "x" + MulText(pct);
            }
            else
            {
                // 等倍に戻した直後だけ x1 (常時出すと倍速中の表示と区別がつかず、焼き付きの意味もなくなる)
                if (!s_toggledOff || !recent) return;
                text = "x1";
            }
            int size = Mathf.Max(12, Screen.height / 36);
            if (s_style == null || s_styleSize != size)
            {
                s_styleSize = size;
                s_style = new GUIStyle(GUI.skin.label);
                s_style.fontSize = size;
                s_style.fontStyle = FontStyle.Bold;
                s_style.alignment = TextAnchor.UpperRight;
                s_style.normal.textColor = new Color(1f, 0.85f, 0.3f);
            }
            GUI.Label(new Rect(Screen.width - size * 6, size / 2, size * 5.5f, size * 1.5f), text, s_style);
        }
    }

    /// <summary>
    /// 戦闘の後始末 (Core.sync の battle_proc[1]==200。結果表示が全部終わってフィールドに戻る直前に 1 回だけ呼ばれる)。
    /// ソフトリセットの EndBattleForReset からも呼ばれるが、その時に等倍へ戻っても害はない。
    /// </summary>
    [HarmonyPatch(typeof(Battle), nameof(Battle.dest))]
    internal static class GameSpeedBattleEndPatch
    {
        private static void Postfix()
        {
            GameSpeed.OnBattleEnd();
        }
    }

    /// <summary>戦闘の初期化 (Core.sync の battle_proc[1]==100、フェードアウト後に 1 回)。BattleSpeed に従って倍速を切り替える。</summary>
    [HarmonyPatch(typeof(Battle), nameof(Battle.init))]
    internal static class GameSpeedBattleStartPatch
    {
        private static void Postfix()
        {
            GameSpeed.OnBattleStart();
        }
    }

    /// <summary>
    /// Main.update (= Main.disp.run() 1 回) の前後。Prefix でこの Unity フレームの追加 run 数を決め、追加があるなら本体の run の
    /// 描画は捨てる (GameSpeed.SuppressDraw)。Postfix で追加分を回し、最後の run だけ描画を残す。
    /// 本体は文字 (GS.DrawString) を GameMain.Update の最後の GS.FlushText でまとめて描くので、最後の run の前に捨てる側で一度
    /// 流して (描画は SuppressDraw で落ちる) 前の run の文字が残らないようにする。
    /// </summary>
    [HarmonyPatch(typeof(Main), nameof(Main.update))]
    internal static class GameSpeedPatch
    {
        private static int s_extra;

        private static void Prefix()
        {
            s_extra = GameSpeed.ExtraRuns();
            GameSpeed.SuppressDraw = s_extra > 0;
        }

        private static void Postfix()
        {
            int extra = s_extra;
            s_extra = 0;
            try
            {
                if (extra <= 0 || Main.disp == null) return;
                for (int i = 0; i < extra; i++)
                {
                    if (i == extra - 1)
                    {
                        GS.FlushText();   // 前の run の文字を捨てる (SuppressDraw が true のうちに)
                        GameSpeed.SuppressDraw = false;
                    }
                    Main.disp.run();
                }
            }
            finally
            {
                GameSpeed.SuppressDraw = false;
            }
        }
    }
}
