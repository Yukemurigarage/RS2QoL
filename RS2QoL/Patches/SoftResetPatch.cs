using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// ソフトリセット (タイトル画面に戻る)。
    ///
    /// 本体は「メニュー → 設定 → タイトルへ」でしか戻れず、階層が深い。ここではボタン割り当ての SoftReset 機能
    /// (既定: LT) か MOD 設定画面の「ソフトリセット (タイトル画面に戻る)」から、確認ダイアログを経てタイトルへ戻す。
    /// v0.12.0 からはゲーム中ならどこからでも戻せる: フィールドの待機状態・ポーズ中・戦闘のコマンド入力待ちは下記の本体の経路で、
    /// それ以外 (メニュー・店・ワールドマップ・会話中・イベント中・戦闘の演出中など) は ForceTeardown で本体の各場面の後片付けを
    /// まとめて行ってから Core.reset() へ進ませる。
    ///
    /// 戻し方はゲーム自身のイベント命令 routine_reset と本体メニューの「タイトルへ」(Menu.config_a2) と同じ reset_flg=2 →
    /// Core.reset() → first_routine の proc[1]=100 (タイトル)。reset() が音を止め、タイトル初期化で BGM 16 が鳴る。
    /// (go_title() は使わない。タイトルメニューを tono 経路で一度動かしてから reset する作りで、BGM が止まらずタイトルが二度出る)
    /// フィールドでは呼ぶ場所をイベント命令と同じ move_ivent_routine の直後 (Postfix) にし、同じ opening_sa の末尾で reset() される。
    /// 戦闘・メニュー・イベント・マップ切替・メッセージ表示中は呼ばない (フィールドで主人公が動ける状態でだけ呼ぶ)。
    ///
    /// Start のポーズ中 (設定画面はここから開く) はメッセージイベントの途中なので、先に Start を 1 論理フレーム分
    /// ゲームに流してポーズを解除し、待機状態になってから reset_flg を立てる。
    ///
    /// 戦闘中は入力待ち (コマンド入力中 Battle.stage==1、または戦闘開始時の「現れた」の後のボタン待ち proc 10。エフェクト再生なし) の時だけ受け付ける。本体には戦闘中にタイトルへ戻る
    /// 経路が無いので、戦闘終了時にゲーム自身が行う後始末 (Battle.sync の最終段 300 と Core.sync の battle_proc[1]==200 の処理、
    /// ただし戦闘結果の反映は除く) を Core.sync の Prefix で行って battle_flg を下ろし、その場で reset() する。
    /// </summary>
    internal static class SoftReset
    {
        /// <summary>
        /// 進行段階。PauseFirst: ダイアログの前に Start を流して自動ポーズ (敵シンボルを止める) /
        /// Confirm: 確認ダイアログ / ReleasePause: Start を流してポーズ解除 (その後 WaitIdle か Idle) / WaitIdle: 待機状態になったら go_title /
        /// BattleReset: 戦闘中。次の Core.sync の先頭で戦闘の後始末をして go_title
        /// </summary>
        private enum Phase { Idle, PauseFirst, Confirm, ReleasePause, WaitIdle, BattleReset, ForceReset }

        private const int KEY_OK = 0x100000, KEY_CANCEL = 0x400000, KEY_MENU = 0x200000, KEY_PAUSE = 0x100;
        private const int DIR_UP = 0x20000, DIR_DOWN = 0x80000;
        private static bool s_choiceYes;        // 確認ダイアログのカーソル (true=はい / false=いいえ)
        private static GUIStyle s_choice, s_choiceSel;
        private const int INJECT_FRAMES = 2;           // Start を立てる論理フレーム数 (押下エッジが 1 回立てばよい)
        private const int PAUSE_TIMEOUT = 60;          // ポーズの開始 / 解除を待つ上限 (論理フレーム)
        private const int IDLE_TIMEOUT = 120;          // 待機状態になるのを待つ上限 (論理フレーム、約 4 秒)

        public static ConfigEntry<bool> Confirm;

        /// <summary>確認ダイアログを表示中か (表示中はゲームに入力を渡さない)。</summary>
        public static bool ConfirmOpen { get { return s_phase == Phase.Confirm; } }
        /// <summary>ダイアログ表示中か、タイトルへ戻す処理の途中か。</summary>
        public static bool Active { get { return s_phase != Phase.Idle; } }

        private static Phase s_phase = Phase.Idle;
        private static int s_phaseFrames;
        private static bool s_goAfterRelease;  // ReleasePause の後: true=タイトルへ (WaitIdle) / false=やめる (Idle)
        private static int s_prevSwallowed;    // 診断用: 直前に飲み込んだボタン
        private const int BUTTON_MASK = 0x100000 | 0x400000 | 0x20 | 0x200000 | 0x100 | 0x200 | 0x10 | 0x40;
        private static int s_prevKey;
        private static bool s_prevBtn;
        private static int s_lastBtnFrame = -1;
        private static GUIStyle s_text;
        private static int s_textSize = -1;
        private static bool s_paperStyles;

        public static void Bind(ConfigFile config)
        {
            Confirm = config.Bind("Reset", "Confirm", true,
                "ボタン割り当ての「タイトルに戻る」を押した時に確認ダイアログを出す。false なら即座にタイトルへ戻る");
        }

        /// <summary>フィールドで主人公が動ける待機状態か (本体がメニューを開ける条件と同じ + マップ切替・フェード中を除く)。</summary>
        public static bool IsFieldIdle(Core c)
        {
            if (c == null) return false;
            return c.proc[0] == 1
                && c.battle_flg == 0 && c.tono_flg == 0 && c.exevt_flg == 0 && c.exdemo_flg == 0
                && c.mess_flag == 0 && c.ivent_sw == 0 && c.ivent_stop_flag == 0
                && c.man_cant_move == 0 && c.mapchgflg == 0 && c.demo_flag == 0
                && c.map_scrol_flag_v == 0 && c.map_scrol_flag_h == 0 && c.man_road_sw == 0
                && !Sys.fadeflg;
        }

        /// <summary>
        /// 戦闘中で、入力待ちか (Core.sync が Main.battle.sync() を回していて、エフェクト再生もフェードもしていない状態で、
        /// Battle が stage 1 = p_command の味方のコマンド入力中、または戦闘開始時の「○○が現れた」の後の
        /// 何かボタンを押すまでの待ち (Battle.sync の proc 10。味方の HP が並んだ最初の画面。UI の読み込みが済んでいる時だけ))。
        /// この状態なら戦闘の UI とエフェクトを安全に破棄できる。
        /// </summary>
        public static bool IsBattleCommandInput(Core c)
        {
            Battle b = Main.battle;
            if (c == null || b == null) return false;
            if (c.battle_flg == 0 || c.battle_proc[1] != 110 || b.animeflg != 0 || Sys.fadeflg) return false;
            int p = b.proc[0][0];
            if (p == 100) return b.stage == 1;
            if (p == 10)
            {
                CSpriteStudioObject ui = BattleLR.UiBattle(b);
                return ui != null && ui.IsReady();
            }
            return false;
        }

        /// <summary>
        /// 今タイトルへ戻せる場面か。v0.12.0 からはゲーム中 (Main.proc == 2) ならどこでも (タイトル画面の NEW GAME / CONTINUE 選択中と、
        /// 本体の終了ダイアログ中を除く)。フィールドの待機状態・ポーズ中・戦闘のコマンド入力待ちは本体の経路で安全に戻し、
        /// それ以外 (メニュー・店・ワールドマップ・会話中・イベント中・戦闘の演出中など) は ForceTeardown で強制的に片付ける。
        /// </summary>
        public static bool CanStart()
        {
            return Main.proc == 2 && Main.core != null && Main.menu != null && !ConfigMenu.IsTitleIdle() && !QuitGame.DialogOpen && !QuitGame.Active;
        }

        /// <summary>MOD 設定画面の項目から。設定画面を閉じてダイアログ (または即実行) へ。項目の決定音が鳴っているのでダイアログの開く音は省く。</summary>
        public static bool RequestFromMenu()
        {
            if (!CanStart()) return false;
            ConfigMenu.Close();
            Request(false);
            return true;
        }

        private static bool s_openSound;   // ダイアログが開いた時に決定音を鳴らすか (設定画面の項目からは項目の音で済ませる)

        private static void Request(bool openSound)
        {
            s_openSound = openSound;
            if (Confirm != null && !Confirm.Value)
            {
                Begin();
            }
            else if (IsFieldIdle(Main.core))
            {
                // フィールドで動ける時は、ダイアログ中に敵シンボルが動かないよう、先に Start を流してポーズしてから出す
                SetPhase(Phase.PauseFirst);
            }
            else
            {
                // ポーズ中・戦闘のコマンド入力待ちはこちらが入力を返さなければ止まったまま。メニュー・会話・イベント中なども
                // 入力待ちで止まる (戦闘の演出中は演出が進んで次の入力待ちで止まる)。本体の Esc ダイアログと同じくゲームは止めずに上に出す
                OpenConfirm();
            }
        }

        /// <summary>確認ダイアログを出す。MOD の窓は開く時に決定音、閉じる時にカーソル音 (どこでもアバロン・設定画面と同じ。ユーザー要望 2026-10-13)。</summary>
        private static void OpenConfirm()
        {
            SetPhase(Phase.Confirm);
            s_prevKey = -1; // 開いた時点で押しているボタンは無視する (押しっぱなしの決定で進まないように)
            s_choiceYes = false; // 初期カーソルは「いいえ」(誤操作で戻らないように)
            if (s_openSound) UiParts.SeOk();
        }

        /// <summary>
        /// 決定: 戦闘のコマンド入力待ちなら次の Core.sync で後始末してタイトルへ。ポーズ中なら解除してから、待機状態になったらタイトルへ。
        /// フィールドの待機状態ならそのまま待機状態の確認を経てタイトルへ。それ以外の場面は次の Core.sync で強制的に片付けてタイトルへ。
        /// </summary>
        private static void Begin()
        {
            s_goAfterRelease = true;
            Core c = Main.core;
            if (IsBattleCommandInput(c)) SetPhase(Phase.BattleReset);
            else if (FieldPause.IsPaused()) SetPhase(Phase.ReleasePause);
            else if (IsFieldIdle(c)) SetPhase(Phase.WaitIdle);
            else SetPhase(Phase.ForceReset);
            ConfigMenu.GuardUntilReleased();
        }

        /// <summary>
        /// タイトル放置後の自動デモの戦闘中など、本体が Start を見ていない場面から確認なしで強制的に片付けてタイトルへ (TitleSkip.HandleDemo から)。
        /// Begin の ForceReset と同じ経路: 次の Core.sync の先頭で ForceTeardown → reset()。
        /// </summary>
        public static bool ForceToTitle(string why)
        {
            if (!CanStart() || Active) return false;
            Plugin.Log.LogInfo("[Reset] force to title: " + why);
            s_goAfterRelease = true;
            SetPhase(Phase.ForceReset);
            ConfigMenu.GuardUntilReleased();
            return true;
        }

        /// <summary>キャンセル (いいえ): カーソル音で閉じ、ポーズ中なら解除して終わる (自動でかけたポーズを残さない)。</summary>
        private static void Cancel()
        {
            UiParts.SeCursor();
            s_goAfterRelease = false;
            SetPhase(FieldPause.IsPaused() ? Phase.ReleasePause : Phase.Idle);
            ConfigMenu.GuardUntilReleased();
        }

        private static void SetPhase(Phase p)
        {
            s_phase = p;
            s_phaseFrames = 0;
            Plugin.Log.LogInfo("[Reset] " + p);
        }

        private static void Abort(string why)
        {
            Plugin.Log.LogWarning("[Reset] aborted: " + why + " " + FieldPause.StateText());
            s_phase = Phase.Idle;
            ConfigMenu.GuardUntilReleased();
        }

        /// <summary>
        /// 割り当てボタンの押下エッジ。Util.GetKeypadState の最終段 (ConfigMenu.Tick) から毎論理フレーム呼ぶ。
        /// 倍速中は同じ Unity フレームで複数回呼ばれるのでフレーム番号で 1 回に絞る。
        /// </summary>
        public static void HandleButton()
        {
            bool down = PadMap.IsFunctionDown(PadFunction.SoftReset);
            bool edge = down && !s_prevBtn && Time.frameCount != s_lastBtnFrame;
            s_prevBtn = down;
            if (!edge) return;
            s_lastBtnFrame = Time.frameCount;
            if (ConfigMenu.IsOpen || ConfigMenu.F1PauseBusy || Active || TreasuryScreen.Active) return;
            if (!CanStart())
            {
                Plugin.Log.LogInfo("[Reset] button ignored (cannot start here: proc=" + Main.proc + ") " + FieldPause.StateText());
                return;
            }
            Request(true);
        }

        /// <summary>Active の間、ConfigMenu.Tick から呼ばれる。戻り値がゲームに渡る (Start を流す時以外は 0)。</summary>
        public static int Tick(int key, bool escEdge)
        {
            s_phaseFrames++;
            if (InputDiag.Enabled && s_phase != Phase.Confirm && (key & BUTTON_MASK) != s_prevSwallowed)
            {
                // 確認ダイアログ以外の段階で飲み込んだボタン (ここで消えた入力は [Pad] には出ない)
                s_prevSwallowed = key & BUTTON_MASK;
                if (s_prevSwallowed != 0) Plugin.Log.LogInfo(string.Format("[Reset] swallowed t={0} key=0x{1:X} phase={2}", InputDiag.T(), key, s_phase));
            }
            switch (s_phase)
            {
                case Phase.PauseFirst:
                    if (FieldPause.IsPaused()) { OpenConfirm(); return 0; }
                    if (s_phaseFrames > PAUSE_TIMEOUT)
                    {
                        // ポーズがかからなくてもダイアログは出す (戻る操作自体は待機状態の確認で守られている)
                        Plugin.Log.LogWarning("[Reset] auto pause did not start " + FieldPause.StateText());
                        OpenConfirm();
                        return 0;
                    }
                    return s_phaseFrames <= INJECT_FRAMES ? KEY_PAUSE : 0;

                case Phase.Confirm:
                {
                    int pressed = s_prevKey < 0 ? 0 : key & ~s_prevKey;
                    s_prevKey = key;
                    // 十字キーの上下で はい / いいえ を選び (Esc の終了ダイアログと同じ縦並び)、決定で実行。キャンセル・Esc は いいえ
                    if ((pressed & (DIR_UP | DIR_DOWN)) != 0) s_choiceYes = !s_choiceYes;
                    else if ((pressed & KEY_OK) != 0) { if (s_choiceYes) { UiParts.SeOk(); Begin(); } else Cancel(); }
                    else if ((pressed & (KEY_CANCEL | KEY_MENU | KEY_PAUSE)) != 0 || escEdge) Cancel();
                    return 0;
                }

                case Phase.ReleasePause:
                    if (!FieldPause.IsPaused())
                    {
                        SetPhase(s_goAfterRelease ? Phase.WaitIdle : Phase.Idle);
                        return 0;
                    }
                    if (s_phaseFrames > PAUSE_TIMEOUT) { Abort("pause not released"); return 0; }
                    // Start だけを流してポーズを解除する (方向や他のボタンは渡さない)
                    return s_phaseFrames <= INJECT_FRAMES ? KEY_PAUSE : 0;

                case Phase.WaitIdle:
                    // 待機状態にならない (ポーズ解除の直後にイベントが始まった等) なら、強制的に片付ける経路に切り替える
                    if (s_phaseFrames > IDLE_TIMEOUT)
                    {
                        Plugin.Log.LogWarning("[Reset] field not idle, switching to force " + FieldPause.StateText());
                        SetPhase(Phase.ForceReset);
                    }
                    return 0;

                case Phase.BattleReset:
                case Phase.ForceReset:
                    // 実行は OnCoreSync (同じ論理フレームの直後に来る)。来ないまま時間が経てば諦める
                    if (s_phaseFrames > IDLE_TIMEOUT) Abort("reset not executed");
                    return 0;
            }
            return key;
        }

        /// <summary>戦闘の状態 (ログ用)。</summary>
        private static string BattleStateText()
        {
            Core c = Main.core;
            Battle b = Main.battle;
            if (c == null || b == null) return "battle=null";
            return string.Format("battle_flg={0} battle_proc=({1},{2}) bproc={3} stage={4} anime={5} fade={6}",
                c.battle_flg, c.battle_proc[0], c.battle_proc[1], b.proc[0][0], b.stage, b.animeflg, Sys.fadeflg);
        }

        /// <summary>
        /// Core.sync の先頭 (Prefix)。戦闘中の決定なら、ゲームが戦闘終了時に行う後始末をここで行って battle_flg を下ろし、
        /// go_title を呼ぶ。すると同じ Core.sync で battle ではなく tono (メニュー) の分岐に入り、フィールドの場合と同じ手順でタイトルへ進む。
        /// </summary>
        internal static void OnCoreSync(Core core)
        {
            if (s_phase == Phase.ForceReset)
            {
                s_phase = Phase.Idle;
                ForceTeardown(core);
                Plugin.Log.LogInfo("[Reset] request reset (force)");
                RequestReset(core, true);
                return;
            }
            if (s_phase != Phase.BattleReset) return;
            if (!IsBattleCommandInput(core))
            {
                // 戦闘の状態が変わっていても (演出が始まった等)、強制的に片付ける経路なら戻せる
                Plugin.Log.LogWarning("[Reset] battle state changed, forcing " + BattleStateText());
                s_phase = Phase.Idle;
                ForceTeardown(core);
                RequestReset(core, true);
                return;
            }
            s_phase = Phase.Idle;
            Plugin.Log.LogInfo("[Reset] end battle for title " + BattleStateText());
            EndBattleForReset(core);
            // ここは opening_sa の外なので、tono 経路が menu.sync()==0 の後にするのと同じく、その場で reset() する。
            // 続く Core.sync は proc[0]==0 → first_routine (proc[1]=100: タイトル) に入る
            Plugin.Log.LogInfo("[Reset] request reset (battle)");
            RequestReset(core, true);
        }

        private static readonly System.Reflection.MethodInfo s_releaseBattleUI = AccessTools.Method(typeof(Battle), "ReleaseBattleUI");
        private static readonly System.Reflection.MethodInfo s_moveTitle = AccessTools.Method(typeof(Menu), "move_title");
        private static readonly System.Reflection.FieldInfo s_pauseFlg = AccessTools.Field(typeof(Core), "pause_flg");

        /// <summary>
        /// どの場面からでもタイトルへ戻すための強制的な後片付け (Core.sync の先頭で呼ぶ。直後に RequestReset(immediate) で Core.reset())。
        /// 本体には「メニューが自分を畳んでから reset する」経路しか無いので、本体が各場面を閉じる時に行っている後片付けを
        /// 場面を問わずまとめて行う:
        ///   戦闘 = 戦闘のコマンド入力待ちからの戻りと同じ EndBattleForReset (演出中でも Main.effect.dest が効果を捨てる)。
        ///         戦闘開始前のフェード中 (battle_proc[1] が 0 / 10 / 100) はまだ何も作られていないので reset() が battle_flg を下ろすだけ、
        ///         戦闘の片付け済み (200) なら dest だけ。
        ///   メニュー・店・ワールドマップ・タイトル系 = 本体がメニューを閉じる時の Menu.menu_kill (画像・タッチ矩形・カーソル・背景・下ボタン) と、
        ///         本体に未使用のまま残っている Menu.move_title (メニューの階層を全部捨てて SpriteStudio の UI を全消去)。
        ///   ワールドマップ = Core.worldImageKill (3D の枠とカーソル、画面のボタン。null 安全なので常に呼ぶ)。
        ///   会話・イベント = 開いているメッセージ窓を閉じ (ok_mes_finish2 の窓の部分)、メッセージ・イベント・移動制限のフラグを下ろす。
        ///   タッチ用のポーズ (画面の stop ボタン) とソフトラベルも戻す。予約されていたオートセーブは行わない (リセットなので)。
        /// その後は本体の起動時と同じ first_routine (フェード解除・音停止・再初期化) が走ってタイトルへ。
        /// 各段は try/catch で独立させ、どれかが失敗しても reset() までは進める。
        /// </summary>
        internal static void ForceTeardown(Core core)
        {
            Battle b = Main.battle;
            Plugin.Log.LogInfo(string.Format("[Reset] force teardown tono={0}/{1} exevt={2}/{3} exdemo={4} proc=({5},{6}) mapchg={7} fade={8} {9} {10}",
                core.tono_flg, core.tono_proc[0], core.exevt_flg, core.exevt_proc[0], core.exdemo_flg, core.proc[0], core.proc[1], core.mapchgflg,
                Sys.fadeflg, FieldPause.StateText(), BattleStateText()));
            if (core.battle_flg != 0 && b != null)
            {
                if (core.battle_proc[1] == 110) Try("battle", () => EndBattleForReset(core));
                else if (core.battle_proc[1] == 200) Try("battle dest", () => b.dest());
            }
            Try("menu_kill", () => Main.menu.menu_kill());
            Try("move_title", () => { if (s_moveTitle != null) s_moveTitle.Invoke(Main.menu, null); });
            Try("sprite studio", () => SpriteStudioManager.RemoveSpriteStudioAll());
            Try("world", () => core.worldImageKill());
            Try("touch", () => InputManager.remove_touch_rect_all());
            Try("menu button", () => SpriteStudioManager.m_MenuButton.Release());
            Try("cursor", () => SpriteStudioCursor.Reset());
            Try("message", () =>
            {
                if (core.w_size_y[0] != 0) { core.wind_which = 0; core.close_wind_sub(); }
                if (core.w_size_y[1] != 0) { core.wind_which = 1; core.close_wind_sub(); }
                core.meswin[0].w = 0; core.w_size_y[0] = 0;
                core.meswin[1].w = 0; core.w_size_y[1] = 0;
                core.window_small_flag = 0;
                core.mess_flag = 0;
                core.mess_no = -1;
                core.pauseflg = 0;
                core.ivent_sw = 0;
                core.ivent_stop_flag = 0;
                core.man_cant_move = 0;
                core.demo_flag = 0;
                core.map_scrol_flag_v = 0;
                core.map_scrol_flag_h = 0;
                core.man_road_sw = 0;
            });
            Try("pause", () =>
            {
                if (s_pauseFlg != null) s_pauseFlg.SetValue(core, 0);
                if (Core.ui_field != null) Core.ui_field.ChangeTexturePause(0);
            });
            Try("softlabel", () => { Sys.setSoftLabel(0, -1); Sys.setSoftLabel(1, -1); });
            Try("autosave", () => { AutoSave.m_IsAutoSave = false; AutoSave.m_Wait = 0; });
            core.joy_data[0] = 0;
            core.joy_data[1] = 0;
            core.key_is_what = 0;
        }

        private static void Try(string what, System.Action a)
        {
            try { a(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[Reset] teardown step failed: " + what + ": " + e.Message); }
        }

        /// <summary>
        /// 戦闘を結果なしで畳む。Battle.sync の最終段 (proc 300) と Core.sync の battle_proc[1]==200 がやっている後始末のうち、
        /// 戦闘結果の反映 (partydata への書き込み、戦闘後イベント、オートセーブ) を除いたもの。
        /// </summary>
        private static void EndBattleForReset(Core core)
        {
            Battle b = Main.battle;
            // --- Battle.sync proc 300 相当 ---
            b.fade_count = 128;
            Main.sys.delete_special_bgm();
            Monster.SystemTerm();
            s_releaseBattleUI.Invoke(b, null);   // SpriteStudio "1_b_2_a_Control"、コマンド窓、戻るボタン、カーソルを破棄
            BattleUIDamage.Release();
            // --- Core.sync battle_proc[1]==200 相当 ---
            b.dest();                             // Main.effect.dest() + GC
            core.power_fade = 255;
            core.attribute_check_flag = 0;
            core.once_make_screen2();
            Main.map.Redraw();
            core.fade_now = 1;
            if (core.mess_flag == 1)
            {
                core.meswin[0].w = 0;
                core.w_size_y[0] = 0;
                core.meswin[1].w = 0;
                core.w_size_y[1] = 0;
            }
            core.man_cant_move = 0;
            core.battle_flg = 0;
            core.joy_data[0] = 0;
            core.joy_data[1] = 0;
            core.key_is_what = 0;
        }

        /// <summary>
        /// Core.move_ivent_routine の直後 (イベント命令 routine_reset と同じ位置)。待機状態なら reset_flg を立て、
        /// 同じ opening_sa の末尾で Core.reset() → first_routine の proc[1]=100 (タイトル) へ進ませる。
        /// </summary>
        internal static void OnIventRoutine(Core core)
        {
            if (s_phase != Phase.WaitIdle || !IsFieldIdle(core)) return;
            s_phase = Phase.Idle;
            Plugin.Log.LogInfo("[Reset] request reset (field)");
            RequestReset(core, false);
        }

        /// <summary>
        /// タイトルへ戻す。本体メニューの「タイトルへ」(Menu.config_a2) と同じく reset_flg=2 で Core.reset() に進ませる。
        /// go_title() は使わない: あれはタイトルメニュー (menu_type 22) を一度 tono 経路で動かし、そこで選んだ後に reset() して
        /// 改めてタイトルを初期化する作りで、BGM は reset() まで止まらず、タイトルが二度出る。
        /// go_title が reset_flg の他に行う初期化 (UiField.initPrevMap / RecordAssistReset) はここでも行う。
        /// immediate=true なら今すぐ reset() (opening_sa の末尾を通らない場所から呼ぶ時)。
        /// </summary>
        private static void RequestReset(Core core, bool immediate)
        {
            UiField.initPrevMap();
            RecordAssist.RecordAssistReset();
            BattleReview.OnSoftReset();   // 全滅の振り返りの窓が開いたままならタイトルに持ち越さない (実機 2026-10-25)
            Try("title bgm", StopTitleBgmForReset);
            core.reset_flg = 2;
            if (immediate) core.reset();
        }

        private const int BGM_TITLE = 16;
        private static readonly System.Reflection.FieldInfo s_bgmFileNames = AccessTools.Field(AccessTools.TypeByName("CSoundData"), "BGMFileName");
        private static readonly System.Reflection.FieldInfo s_bgmDirectory = AccessTools.Field(AccessTools.TypeByName("CSoundData"), "BGMDirectory");

        /// <summary>
        /// タイトル BGM (16) が既に鳴っている場面 (CONTINUE のセーブ選択画面など) からの reset では、本体の Sound.stopAll が
        /// 1 秒のフェードで止め、フェード中も再生リスト (CSoundList) に名前が残るので、タイトル初期化の setBgm(16) が
        /// 「同じ曲が鳴っている」と見て何もせず、無音になる (実機で確認)。その曲だけ即時に止めてリストから外しておく。
        /// </summary>
        private static void StopTitleBgmForReset()
        {
            if (Sound.bgmwork == null || Sound.bgmwork.Length < 2 || Sound.bgmwork[1] != BGM_TITLE) return;
            if (s_bgmFileNames == null || s_bgmDirectory == null || SoundManager.m_SoundList_BGM == null) return;
            string[] names = s_bgmFileNames.GetValue(null) as string[];
            string dir = s_bgmDirectory.GetValue(null) as string;
            if (names == null || names.Length <= BGM_TITLE || dir == null) return;
            string file = dir + names[BGM_TITLE];
            SoundManager.m_SoundList_BGM.RemoveSound(file); // 即時停止 + MODE.END
            SoundManager.m_SoundList_BGM.Update();          // END をリストから除く (同じ論理フレーム内でも次の setBgm が通るように)
            Plugin.Log.LogInfo("[Reset] title bgm stopped immediately (" + file + ")");
        }

        /// <summary>確認ダイアログの描画 (Plugin.OnGUI から)。</summary>
        public static void Draw()
        {
            if (!ConfirmOpen) return;
            int fontSize = Mathf.Max(12, Screen.height / 32);
            ConfigMenu.EnsureStyles(fontSize);
            float lineH = fontSize * 1.6f;
            float pad = fontSize;
            float w = Mathf.Min(Screen.width - 2 * pad, fontSize * 26f);
            float h = pad * 2 + lineH * 6.3f;
            float x = (Screen.width - w) / 2f;
            float y = (Screen.height - h) / 2f;
            // 枠・羊皮紙・手のカーソルは設定画面と同じ部品 (UiParts。読めなければ単色の箱と「>」で、文字は白 + 影)
            bool paper = UiParts.DrawPaperWindow(new Rect(x, y, w, h));
            if (!paper) GUI.Box(new Rect(x, y, w, h), GUIContent.none, ConfigMenu.BoxStyle);
            UiParts.Shadow = !paper;
            ConfigMenu.EnsureTextColors(paper);
            if (s_text == null || s_textSize != fontSize || s_paperStyles != paper)
            {
                s_textSize = fontSize;
                s_paperStyles = paper;
                s_text = new GUIStyle(ConfigMenu.LineStyle);
                s_text.alignment = TextAnchor.MiddleCenter;
                s_text.wordWrap = true;
                s_choice = new GUIStyle(s_text);
                s_choice.alignment = TextAnchor.MiddleLeft; // 手のカーソルの右に左揃えで並べる
                s_choice.wordWrap = false;
                s_choiceSel = new GUIStyle(ConfigMenu.LineSelStyle);
                s_choiceSel.alignment = TextAnchor.MiddleLeft;
                s_choiceSel.fontStyle = FontStyle.Bold;
            }
            float cy = y + pad;
            UiParts.Label(new Rect(x + pad, cy, w - 2 * pad, lineH), "ソフトリセット (タイトル画面に戻る)", ConfigMenu.TitleStyle);
            cy += lineH;
            UiParts.Label(new Rect(x + pad, cy, w - 2 * pad, lineH * 2), "セーブしていない進行は失われます。\nタイトル画面に戻りますか？", s_text);
            cy += lineH * 2;
            // はい / いいえ を縦に並べ (Esc の終了ダイアログと同じ)、カーソル側に手を添える
            float handW = lineH * UiParts.CursorAspect;
            float choiceX = x + w / 2f - handW;
            DrawChoice(choiceX, cy, w / 2f, lineH, handW, "はい", s_choiceYes);
            cy += lineH;
            DrawChoice(choiceX, cy, w / 2f, lineH, handW, "いいえ", !s_choiceYes);
            cy += lineH * 1.3f;
            UiParts.Label(new Rect(x + pad, cy, w - 2 * pad, lineH), "上下: 選ぶ   /   決定: 実行   /   キャンセル・Esc: いいえ", ConfigMenu.FooterStyle);
        }

        private static void DrawChoice(float x, float y, float w, float lineH, float handW, string text, bool selected)
        {
            if (selected && !UiParts.DrawCursor(x + handW, y + lineH / 2f, lineH))
                UiParts.Label(new Rect(x, y, handW, lineH), ">", s_choiceSel);
            UiParts.Label(new Rect(x + handW + lineH * 0.2f, y, w, lineH), text, selected ? s_choiceSel : s_choice);
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.move_ivent_routine))]
    internal static class SoftResetTriggerPatch
    {
        private static void Postfix(Core __instance)
        {
            SoftReset.OnIventRoutine(__instance);
        }
    }

    /// <summary>戦闘中・それ以外の場面からタイトルへ戻る時の実行点 (Core.sync が各分岐に入る前)。</summary>
    [HarmonyPatch(typeof(Core), nameof(Core.sync))]
    internal static class SoftResetBattleTriggerPatch
    {
        private static void Prefix(Core __instance)
        {
            SoftReset.OnCoreSync(__instance);
        }
    }
}
