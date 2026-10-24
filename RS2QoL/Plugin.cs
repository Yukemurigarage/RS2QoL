using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace RS2QoL
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> DiagEnabled;
        /// <summary>[Diagnostics] Enabled が ON か (動作時の調査ログを出すかの共通の条件)</summary>
        internal static bool DiagOn { get { return DiagEnabled != null && DiagEnabled.Value; } }
        internal static ConfigEntry<int> DiagonalGraceFrames;
        internal static ConfigEntry<int> DiagonalSwitchFrames;
        internal static ConfigEntry<Patches.DiagonalPolicy> DiagonalPolicy;
        internal static ConfigEntry<int> InputBufferFrames;
        internal static ConfigEntry<int> BufferTapMinFrames;
        internal static ConfigEntry<int> WallTurnFrames;
        internal static ConfigEntry<bool> XInputDpad;
        internal static ConfigEntry<bool> XInputButtons;
        internal static ConfigEntry<bool> XInputDpadExclusive;
        internal static ConfigEntry<bool> XInputButtonsExclusive;

        private void Awake()
        {
            Log = Logger;
            // BepInEx/LogOutput.log と コンソールに出力される
            Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

            DiagEnabled = Config.Bind("Diagnostics", "Enabled", false,
                "入力の診断ログ ([Pad]/[Key]/[Field]/[MARK]) を出す。調査が終わったら false にする");
            Patches.InputDiag.ToggleKey = Config.Bind("Diagnostics", "ToggleKey", UnityEngine.KeyCode.F8,
                "診断ログを ON/OFF するキーボードのキー (None で無し)。OFF にした時はログを BepInEx\\RS2QoL-diag\\ に取り分けてそのフォルダを開く");
            Patches.InputDiag.MarkKey = Config.Bind("Diagnostics", "MarkKey", UnityEngine.KeyCode.F7,
                "診断ログに目印 ([MARK]) を書くキーボードのキー (None で無し)。パッドの Start+Select と同じ");

            DiagonalPolicy = Config.Bind("Input", "DiagonalPolicy", Patches.DiagonalPolicy.Keep,
                "斜め入力の扱い。Keep=今の方向を維持し単独方向になったら転換 (親指のブレに強い) / Newest=新しく押した方向へ即転換");
            DiagonalGraceFrames = Config.Bind("Input", "DiagonalGraceFrames", 4,
                "ニュートラルから斜めに入った時、単独方向になるのを待つフレーム数 (1 フレーム ≒ 33ms)。0 で待たない");
            DiagonalSwitchFrames = Config.Bind("Input", "DiagonalSwitchFrames", 3,
                "Keep の時、同じ斜めがこのフレーム数より長く続いたら新しく足した方向へ転換する (一瞬のブレは無視)。0 で転換しない");
            InputBufferFrames = Config.Bind("Input", "InputBufferFrames", 10,
                "フィールドで、マス移動中に押した方向を離した後も覚えておくフレーム数 (先行入力。1 フレーム ≒ 33ms、歩き 1 マス = 8 フレーム)。押したままなら忘れない。0 で無効");
            BufferTapMinFrames = Config.Bind("Input", "BufferTapMinFrames", 1,
                "先行入力として採用するのに必要な押し続けフレーム数。1 で 1 フレームしか読めない短い入力も拾う。親指のブレで一瞬混ざった方向まで拾うなら 2 にする");
            WallTurnFrames = Config.Bind("Input", "WallTurnFrames", 3,
                "斜めを押していて採用中の方向で進めない時、このフレーム数で斜めのもう片方に曲がる (壁ずり)。0 で無効");
            XInputDpad = Config.Bind("Input", "XInputDpad", true,
                "XInput でも十字キーと左右スティックを読んで合成する (Unity 側の読み取りが押したままなのに無入力になる対策)。Windows 専用");
            XInputDpadExclusive = Config.Bind("Input", "XInputDpadExclusive", true,
                "XInput のパッドが接続されている間は、十字キーと左右スティックの方向を XInput だけから読む (Unity 側の読み取りが斜めや無入力のまま固まる対策)。キーボードの方向キーは残る。XInput 対応パッドと非対応パッドを同時に繋いで非対応の方を使う時だけ false にする");
            XInputButtons = Config.Bind("Input", "XInputButtons", true,
                "XInput でもボタン (A/B/X/Y/LB/RB/Start/Select) を読んで合成する (Unity 側で押下が丸ごと落ちる対策)。Windows 専用");
            XInputButtonsExclusive = Config.Bind("Input", "XInputButtonsExclusive", true,
                "XInput のパッドが接続されている間は、ボタンを XInput だけから読む (Unity 側の読み取りが離したのに押したままになる対策)。キーボードは残る。XInputDpadExclusive と同じ考え方");
            Patches.XInputPad.TriggerThreshold = Config.Bind("Input", "TriggerThreshold", 64,
                new ConfigDescription("LT / RT を押下とみなす引き量 (0..255、既定 64 = 1/4)", new AcceptableValueRange<int>(1, 255)));
            Patches.XInputPad.TriggerDeepThreshold = Config.Bind("Input", "TriggerDeepThreshold", 240,
                new ConfigDescription("ソフトリセット・ゲーム終了を割り当てた LT / RT を押下とみなす引き量 (0..255、既定 240 ≒ 94%。触れただけで発動しないように奥まで引いた時だけ効く。255 ちょうどを要求しないのは個体差で届かないパッドがあるため)", new AcceptableValueRange<int>(1, 255)));

            // ボタン割り当て ([ButtonMap] 物理ボタン → 機能)。MOD 設定画面 (既定: L3 / F1) からも変更できる
            Patches.PadMap.Bind(Config);
            // 音量 ([Audio] Master/BGM/SE、%)。MOD 設定画面からも変更できる
            Patches.AudioVolume.Bind(Config);
            // ゲーム速度 ([Speed] Multiplier / StartEnabled)。ボタン割り当ての SpeedToggle か設定画面で ON/OFF
            Patches.GameSpeed.Bind(Config);
            // 戦闘後の結果表示をボタンを新たに押すまで待つ ([Battle] ResultWaitForPress)
            Patches.BattleResult.Bind(Config);
            // ダメージ数字の跳ねの速さ ([Battle] DamageAnimStep、既定 2) と消えるまでの長さ ([Battle] DamageHoldStep、既定 4 = 本体どおり)
            Patches.DamagePop.Bind(Config);
            // ダメージ数字の縦位置 ([Battle] DamageYOffsetMonster / DamageYOffsetPlayer、既定 0 = 本体どおり)
            Patches.DamagePos.Bind(Config);
            // ダメージ数字をスーパーファミコン版のドット文字で出す ([Battle] DamageDigitsSfc、既定 On)
            Patches.DamageFont.Bind(Config);
            // 弱点をついた時のモンスターのダメージ数字を黄色に ([Battle] WeakPointDamageColor、既定 On)
            Patches.WeakPointColor.Bind(Config);
            // 味方の HP / LP をスーパーファミコン版と同じ「現在値/最大値」のドット文字で出す ([Battle] PartyHpSfc、既定 On)
            Patches.PartyHpSfc.Bind(Config);
            // 戦闘のモンスターの絵の大きさ ([Battle] MonsterScalePercent、既定 107 = スーパーファミコン版と同じ見かけ)
            Patches.MonsterScale.Bind(Config);
            // 戦闘の味方キャラの絵の大きさ ([Battle] PlayerScalePercent、既定 107 = スーパーファミコン版と同じ見かけ)
            Patches.PlayerScale.Bind(Config);
            // 技・攻撃アニメの速さ ([Battle] AttackAnimSpeed、既定 0 = 倍速に従う。% 指定で倍速に関係なく固定)
            Patches.AttackAnim.Bind(Config);
            // 戦闘でアニメーションするモンスターの動きを倍速中も等倍にする ([Battle] MonsterAnimRealtime)
            Patches.MonsterAnim.Bind(Config);
            // メニューで選んでいる文字の点滅を倍速中も等倍の速さに ([Menu] CursorBlinkRealTime、既定 true。戦闘のコマンド窓・会話の選択肢も含む)
            Patches.CursorBlink.Bind(Config);
            // 店の買う / 売るの確認画面で、すでに装備している仲間の絵の横に E を出す ([Menu] ShopEquipMark、既定 true)
            Patches.ShopEquipMark.Bind(Config);
            // 店で買う武器・防具・盾を仲間それぞれの今の装備と比べた差 (▲N / ▼N) を絵の横に出す ([Menu] ShopEquipCompare、既定 true)
            Patches.ShopEquipCompare.Bind(Config);
            // 店で E が付く仲間の絵を、戦闘でコマンドを決めた時と同じ「回って構える」動きに (武器は攻撃の構え、防具・道具は選択中の構え) ([Menu] ShopEquipSpin、既定 true)
            Patches.ShopEquipSpin.Bind(Config);
            // ステータス画面の素早さを防具の重さ (÷2) を引いた戦闘の値にする ([Menu] StatusAgilityAfterWeight、既定 true)。どこでもアバロンの 2 ページ目 (体重計) も同じ計算
            Patches.PartyWeight.Bind(Config);
            // 戦闘の L / R をスーファミ版と同じ「退却 / 防御の呼び出し」にする ([Battle] SfcLR、既定 true)
            Patches.BattleLR.Bind(Config);
            // 会話ウィンドウの送りと選択肢の決定をボタンを新たに押すまで待つ ([Field] DialogueWaitForPress)
            Patches.DialogueAdvance.Bind(Config);
            // 会話ウィンドウの文字送りの速さ ([Field] MessageSpeed、既定 Default = 本体のまま。x2 / x3 / Instant は倍速に関わらず実時間で同じ速さ)
            Patches.MessageSpeed.Bind(Config);
            // NPC に十字キーを押し当て続けると決定なしで話しかける ([Field] TalkOnBump / TalkOnBumpFrames / TalkOnBumpCooldownFrames)
            Patches.TalkOnBump.Bind(Config);
            // ダッシュの基準をマップのモンスターの有無で自動的に切り替える ([Field] AutoDash)
            Patches.AutoDash.Bind(Config);
            Patches.PauseHint.Bind(Config);
            // ワールドマップのカーソルを十字キーで自由に動かす ([WorldMap] FreeCursor / CursorSpeed)
            Patches.WorldMapCursor.Bind(Config);
            // ワールドマップの巻物の開閉の速さを倍速でも同じに ([WorldMap] ScrollSpeedFixed、既定 On)
            Patches.WorldScrollSpeed.Bind(Config);
            // キーボードの割り当て ([KeyMap] 機能 → KeyCode、既定は本体と同じ)
            Patches.KeyMap.Bind(Config);
            // 画面モード ([Window] Mode / WindowWidth / WindowHeight)。起動数フレーム後と設定変更時に適用 (Update)
            Patches.WindowMode.Bind(Config);
            // マウスカーソルの自動非表示 ([Window] HideCursor / HideCursorSeconds)
            Patches.MouseCursor.Bind(Config);
            // スマホ版のタッチ用ボタンの絵を消す ([Window] HideTouchButtons、既定 Off)。フィールド / メニュー / 戦闘
            Patches.TouchButtons.Bind(Config);
            // 所持金のクラウンの絵の大きさ ([Display] CoinIconScalePercent、既定 100)。SpriteStudio の頂点を縮める
            Patches.CoinIcon.Bind(Config);
            // 画面の同期 ([Display] Sync / FrameRate)。VRR 向けの正確なフレームリミッタと VSync。GameMain.Start の後に適用
            Patches.DisplaySync.Bind(Config);

            // 60fps 表示 (実験的、[Display] Experimental60)。Unity を 60fps、論理フレームは 2 回に 1 回、間は前の描画を複製 + 補間
            Patches.Interp60.Bind(Config);
            // 60fps 表示の戦闘用補間 (実験的、[Display] Experimental60Battle、既定 Off)。味方キャラの移動とダメージ数字の跳ね
            Patches.Interp60Battle.Bind(Config);
            // 60fps 表示の 3D オブジェクトの鍵フレームアニメ (ワールドマップ開閉の巻物など、[Display] Experimental60FldAnim、既定 On)
            Patches.Interp60FldAnim.Bind(Config);
            // タイトル画面に戻る ([Reset] Confirm)。ボタン割り当ての SoftReset か設定画面から
            Patches.SoftReset.Bind(Config);
            Patches.TitleSkip.Bind(Config);
            // 防具・盾の詳しい防御力 (8 種・特性・耐性・重さ) を持ち物・装備・店・預かり所で出す ([Menu] ArmorDetail、既定 On)
            Patches.ArmorInfo.Bind(Config);
            // 仲間にするかの 2 択に「ステータスを見る」を足し、候補の能力・適性・ひらめきの傾向を窓で見せる ([Recruit] StatusOption、既定 On)
            Patches.RecruitInfo.Bind(Config);
            // 会話の選択肢でキャンセルボタンを「いいえ」側 (最後の行) の決定にする ([Dialogue] CancelPicksDecline、既定 On)
            Patches.DialogueCancel.Bind(Config);
            // 敵が強くなる速さ (戦闘回数カウンタの進み方) を % で調整・一時停止 ([Growth]、既定 100% = 本体どおり)
            Patches.EnemyGrowth.Bind(Config);
            // 味方の成長 (HP / 技レベル / 術レベル / 全体レベル) の速さを % で調整 ([PlayerGrowth]、既定 100% = 本体どおり)
            Patches.PlayerGrowth.Bind(Config);
            // ヒーローモード: 瀕死 (膝をつく HP) か最後の 1 人の味方が攻撃した時だけ技をひらめく確率を倍にする ([Spark] HeroMode、既定 Off = 本体どおり)
            Patches.SparkBoost.Bind(Config);
            // 全滅した時に、継承の画面の前に「戦闘の振り返り」の窓 (敵の弱点 / 敵の技と見切り / こちらの行動) を出す ([BattleReview] Enabled、既定 On)
            Patches.BattleReview.Bind(Config);
            // 戦闘のたびに国庫に入る税収の倍率 ([Economy] TaxPercent、既定 100% = 本体どおり)
            Patches.TaxIncome.Bind(Config);
            // 所持金の上限 ([Economy] WalletMax、既定 65535) と、どこでも金庫番 (国庫 ⇄ 所持金、[Economy] TreasuryFloor、ボタン割り当て Withdraw)
            Patches.Wallet.Bind(Config);
            // どこでもアバロンの窓と、フィールドの LB / RB の役目 ([Economy] FieldLB / FieldRB、既定 LB = 画面、RB = 引き出す)
            Patches.TreasuryScreen.Bind(Config);
            // 弔いの鍛冶: 皇帝が死んで継承すると 1 回たまり、開発所で国庫を使わずに開発できる ([MourningForge] Enabled、既定 Off = 本体どおり)
            Patches.MourningForge.Bind(Config);
            // 長時間プレイで重くなる問題の計測 ([Diagnostics] MemoryLogSeconds、Enabled とは独立、既定 0 = 出さない)
            Patches.MemDiag.Bind(Config);
            // 画面のボタンの丸文字 (Ⓐ Ⓑ Ⓧ) と矢印の L / R の文字を消す ([Display] HideButtonGlyphs、既定 On)
            Patches.ButtonGlyph.Bind(Config);

            // Patches/ 以下の [HarmonyPatch] をすべて適用する
            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            Logger.LogInfo("Harmony patches applied");
            Patches.MourningForge.LogPatched();
            Patches.WeakPointColor.LogPatched();
            Logger.LogInfo("[TitleSkip] patched: InputManager.GetMenuInput (AnyButtonSkip=" + Patches.TitleSkip.AnyButton.Value + ")");
            Logger.LogInfo("[MessageSpeed] patched: Core.message_lop (MessageSpeed=" + Patches.MessageSpeed.Mode.Value + ")");
            Logger.LogInfo("[AutoDash] patched: Core.move_human_sub_r (AutoDash=" + Patches.AutoDash.Enabled.Value + ")");
            Logger.LogInfo("[MonsterAnim] patched: Effect.draw / SSObject.Update (MonsterAnimRealtime=" + Patches.MonsterAnim.Enabled.Value + ")");
            Logger.LogInfo("[DialogueAdvance] patched: Core.pause_sa / message_lop2 / put_select / search_ivent / its_enter_sa / its_ivent_move / its_touch_sa (WaitForPress=" + Patches.DialogueAdvance.WaitEnabled + " ByDpad=" + Patches.DialogueAdvance.DpadEnabled + ")");
            Logger.LogInfo("[Growth] patched: Core.btl_counter_add / Work.getBattleCount / Battle.init / Battle.dest (Percent=" + Patches.EnemyGrowth.Percent.Value + " Paused=" + Patches.EnemyGrowth.Paused.Value + " SkipEscape=" + Patches.EnemyGrowth.SkipEscape.Value + ")");
            Logger.LogInfo("[Economy] patched: Core.add_budget (TaxPercent=" + Patches.TaxIncome.Percent.Value + " TaxOnEscape=" + Patches.TaxIncome.OnEscape.Value + ")");
            Logger.LogInfo("[Spark] patched: Battle.check_get + test_mikiri (HeroMode=" + Patches.SparkBoost.Enabled.Value + " LastOneStanding=" + Patches.SparkBoost.LastOneStanding.Value + " Multiplier=" + Patches.SparkBoost.Multiplier.Value + " HpPercent=" + Patches.SparkBoost.HpPercent.Value + " LevelBonus=" + Patches.SparkBoost.LevelBonus.Value + " Floor=" + Patches.SparkBoost.FloorPercent.Value + "% Gap=" + Patches.SparkBoost.FloorMaxGap.Value + ")");
            Patches.PartyWeight.LogPatched();
            Patches.WorldScrollSpeed.LogPatched();
            Patches.KeyMap.LogPatched();

            // UnityEngine.Graphics.DrawMeshImpl への Prefix は Experimental60 が Off 以外の時だけ入れる (PatchAll とは別に try/catch で)
            Patches.Interp60.Init(harmony);

            // フリーズの診断 (Diagnostics が ON の時だけ別スレッドで監視)
            Patches.FreezeWatchdog.Start();

            // 有効な機能と設定値を起動時に 1 行で出す (不具合報告のログから設定が分かるように)
            Logger.LogInfo(string.Format(
                "Settings: DiagonalPolicy={0} DiagonalSwitchFrames={1} DiagonalGraceFrames={2} WallTurnFrames={3} InputBufferFrames={4} BufferTapMinFrames={8} XInputDpad={5} XInputDpadExclusive={9} XInputButtons={7} XInputButtonsExclusive={10} Diagnostics={6}",
                DiagonalPolicy.Value, DiagonalSwitchFrames.Value, DiagonalGraceFrames.Value, WallTurnFrames.Value,
                InputBufferFrames.Value, XInputDpad.Value, DiagEnabled.Value, XInputButtons.Value,
                BufferTapMinFrames.Value, XInputDpadExclusive.Value, XInputButtonsExclusive.Value));
        }

        // 画面モードの適用 (Screen.SetResolution の反映を待ってから枠を変える)
        private void Update()
        {
            Patches.InputDiag.UpdateHotkey();
            Patches.WindowMode.Update();
            Patches.MouseCursor.Update();
        }

        // ウィンドウのアクティブ切り替え (非アクティブ中は Unity が更新を止めることがあるので、[Freeze] との区別用)
        private void OnApplicationFocus(bool focus)
        {
            if (DiagEnabled != null && DiagEnabled.Value) Logger.LogInfo("[Focus] " + (focus ? "gained" : "lost"));
        }

        // 終了時に BepInEx のログファイルを書き切る。BepInEx の DiskLogListener は数秒ごとにまとめて書くので、
        // ウィンドウの × で閉じると最後の数秒分 (調べたい部分) が LogOutput.log に残らないことがある
        private void OnApplicationQuit()
        {
            Patches.InputDiag.OnQuit();
            Patches.DisplaySync.Shutdown();
            FlushLog();
        }

        /// <summary>BepInEx のログファイルを今すぐ書き切る (DiskLogListener は 2 秒ごとにまとめて書く)。終了時と診断ログの複製の前に。</summary>
        internal static void FlushLog()
        {
            foreach (ILogListener l in BepInEx.Logging.Logger.Listeners)
            {
                DiskLogListener d = l as DiskLogListener;
                if (d != null && d.LogWriter != null)
                {
                    try { d.LogWriter.Flush(); } catch { }
                }
            }
        }

        private void OnApplicationPause(bool pause)
        {
            if (DiagEnabled != null && DiagEnabled.Value) Logger.LogInfo("[Focus] " + (pause ? "paused" : "resumed"));
        }

        // MOD 設定画面の描画。入力は ConfigMenuGatePatch (Util.GetKeypadState の最終段) で処理する
        private void OnGUI()
        {
            Patches.MourningScene.Draw();     // 弔いの鍛冶の死の演出 (暗転の中の皇帝と窓)。画面を黒く塗るので一番下に
            Patches.BattleReview.Draw();      // 背景を黒く塗るので一番下に (診断ログの印などの小窓が隠れないように。ユーザー 2026-10-21)
            Patches.GameSpeed.DrawIndicator();
            Patches.InputDiag.DrawBadge();
            Patches.Wallet.DrawNotice();
            Patches.ArmorInfo.Draw();
            Patches.TreasuryScreen.Draw();
            Patches.RecruitInfo.Draw();
            ConfigMenu.Draw();
            Patches.SoftReset.Draw();
        }
    }
}
