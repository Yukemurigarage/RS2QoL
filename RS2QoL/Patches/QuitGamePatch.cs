using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// パッドからゲームを終了する。
    ///
    /// 本体はキーボードの Esc (Main.Disp.run の case 2: Input.GetKeyUp(Escape) → m_isEndDialog = true → m_endgame.Initialize())
    /// でしか「ゲームを終了しますか？」のダイアログを出せず、ウィンドウの × 以外にパッドで終える手段が無い。
    /// ここでは MOD 設定画面の「ゲームを終了」行と、ボタン割り当ての Quit 機能から、Esc と同じことをして本体のダイアログを開く。
    /// ダイアログ自体は本体のもの (EndGameSteam。上下で はい / いいえ、決定で実行、キャンセルで閉じる。初期カーソルは いいえ) なので、
    /// 見た目も操作も Esc で開いた時と同じ。開いた後の処理 (はい → Application.Quit、いいえ → Release) も本体の Main.run がそのまま行う。
    /// 開ける場面も Esc と同じ (Main.proc == 2 = ゲーム中ならどこでも)。
    /// </summary>
    internal static class QuitGame
    {
        private static readonly FieldInfo s_isEndDialog = AccessTools.Field(typeof(Main), "m_isEndDialog");
        private static readonly FieldInfo s_endgame = AccessTools.Field(typeof(Main), "m_endgame");
        private static readonly MethodInfo s_initialize = AccessTools.Method(AccessTools.TypeByName("EndGameSteam"), "Initialize");

        private static bool s_prevBtn;
        private static int s_lastBtnFrame = -1;

        /// <summary>
        /// 終了ダイアログ中 (閉じた後、全ボタンを離すまでを含む) で、ゲームにパッドの入力を渡さない期間か (QuitDialogBlockInputPatch が更新)。
        /// Core.sync の間 Sys.key と joy_data を 0 にするほか、生のパッドを読む MOD 側の補助 (先行入力・壁ずり・ダッシュ補正・マップの自由カーソル) もこれを見て止まる。
        /// </summary>
        public static bool BlockInput;

        /// <summary>本体の終了ダイアログが開いているか。</summary>
        public static bool DialogOpen
        {
            get { return s_isEndDialog != null && (bool)s_isEndDialog.GetValue(null); }
        }

        /// <summary>今ダイアログを開けるか (Esc と同じ条件)。</summary>
        public static bool CanOpen()
        {
            return s_isEndDialog != null && s_endgame != null && s_initialize != null
                && Main.proc == 2 && !DialogOpen && !SoftReset.Active;
        }

        /// <summary>Esc と同じ手順で本体の終了ダイアログを開く。</summary>
        public static bool Open()
        {
            if (!CanOpen()) return false;
            object endgame = s_endgame.GetValue(null);
            if (endgame == null) return false;
            s_isEndDialog.SetValue(null, true);
            s_initialize.Invoke(endgame, null); // EndDialogOpenPatch (Postfix) が開いたフレームを覚え、同じフレームの入力を飛ばす
            Plugin.Log.LogInfo("[Quit] end dialog opened");
            return true;
        }

        /// <summary>MOD 設定画面の項目から。設定画面を閉じてダイアログへ。項目の決定音が鳴っているのでダイアログの開く音は省く。</summary>
        public static bool RequestFromMenu()
        {
            if (!CanOpen() || Active) return false;
            ConfigMenu.Close(); // 閉じた直後は全ボタンが離されるまでゲームに渡らないので、この決定の押しっぱなしでダイアログが進むことはない
            Begin(false);
            return true;
        }

        /// <summary>
        /// 割り当てボタン (PadFunction.Quit) の押下エッジ。ConfigMenu.Tick から毎論理フレーム呼ぶ。
        /// 倍速中は同じ Unity フレームで複数回呼ばれるのでフレーム番号で 1 回に絞る。
        /// </summary>
        public static void HandleButton()
        {
            bool down = PadMap.IsFunctionDown(PadFunction.Quit);
            bool edge = down && !s_prevBtn && Time.frameCount != s_lastBtnFrame;
            s_prevBtn = down;
            if (!edge) return;
            s_lastBtnFrame = Time.frameCount;
            if (ConfigMenu.IsOpen || ConfigMenu.F1PauseBusy || Active || TreasuryScreen.Active) return;
            if (!CanOpen())
            {
                Plugin.Log.LogInfo("[Quit] button ignored (proc=" + Main.proc + " dialog=" + DialogOpen + ")");
                return;
            }
            Begin(true);
        }

        // ---------------------------------------------------------------- フィールドでは本体のポーズをかけてからダイアログを出す
        //
        // 本体の Esc はダイアログの下でゲームが動き続ける (NPC は歩き、敵は寄ってくる)。フィールドの待機状態なら、
        // ソフトリセットと同じ手順で先に本体のポーズ (Start) をかけ、かかってからダイアログを出す。いいえ で戻ったら
        // こちらがかけたポーズだけ Start を流して解除する (プレイヤーが自分でポーズしてから押した時は解除しない)。
        // 戦闘中・メニュー中・会話中などポーズがかけられない場面では、そのままダイアログを出す (今までどおり)。
        // 段階の間 (ダイアログ中を除く) はゲームに Start 以外の入力を渡さない。

        private enum Phase { Idle, PauseFirst, InDialog, ReleasePause }
        private const int KEY_PAUSE = 0x100;
        private const int INJECT_FRAMES = 2;      // Start を立てる論理フレーム数 (押下エッジが 1 回立てばよい)
        private const int PAUSE_TIMEOUT = 60;     // ポーズの開始 / 解除を待つ上限 (論理フレーム)
        private static Phase s_phase = Phase.Idle;
        private static int s_phaseFrames;
        private static bool s_autoPaused;         // こちらでポーズをかけた (戻る時に解除する)
        private static bool s_openSound;          // ダイアログが開いた時に決定音を鳴らすか (設定画面の項目からは項目の音で済ませる)

        /// <summary>ポーズ開始待ち・ダイアログ中・ポーズ解除待ちのいずれか (ConfigMenu.Tick が Tick に回す)。</summary>
        public static bool Active { get { return s_phase != Phase.Idle; } }

        private static void SetPhase(Phase p)
        {
            s_phase = p;
            s_phaseFrames = 0;
        }

        private static void Begin(bool openSound)
        {
            s_autoPaused = false;
            s_openSound = openSound;
            Core c = Main.core;
            if (!FieldPause.IsPaused() && SoftReset.IsFieldIdle(c))
            {
                Plugin.Log.LogInfo("[Quit] pausing the field first");
                SetPhase(Phase.PauseFirst);
                return;
            }
            OpenNow();
        }

        /// <summary>
        /// 本体のダイアログを開く。開く音は決定音 (MOD の窓と揃える。本体は Esc の時にカーソル音を鳴らしてから開く)。
        /// 閉じる音は本体のダイアログ自身が鳴らす (EndGameSteam: いいえ・キャンセルは 212 カーソル音、はい は 213) ので触らない。
        /// </summary>
        private static void OpenNow()
        {
            if (Open())
            {
                if (s_openSound) UiParts.SeOk();
                SetPhase(Phase.InDialog);
                return;
            }
            Plugin.Log.LogInfo("[Quit] could not open the dialog (proc=" + Main.proc + " dialog=" + DialogOpen + ")");
            Finish();
        }

        /// <summary>ダイアログが閉じた (いいえ) 後の片付け: こちらでかけたポーズなら解除してから Idle へ。</summary>
        private static void Finish()
        {
            if (s_autoPaused && FieldPause.IsPaused())
            {
                BlockInput = false;   // 解除の Start を joy_data に通す (他のボタンは Tick が飲み込む)
                SetPhase(Phase.ReleasePause);
                return;
            }
            SetPhase(Phase.Idle);
            ConfigMenu.GuardUntilReleased();
        }

        /// <summary>Active の間、ConfigMenu.Tick から呼ばれる。戻り値がゲームに渡る (ダイアログ中はそのまま、Start を流す時以外は 0)。</summary>
        public static int Tick(int key)
        {
            s_phaseFrames++;
            switch (s_phase)
            {
                case Phase.PauseFirst:
                    if (FieldPause.IsPaused())
                    {
                        s_autoPaused = true;
                        OpenNow();
                        return 0;
                    }
                    if (s_phaseFrames > PAUSE_TIMEOUT)
                    {
                        // ポーズがかからなくてもダイアログは出す (入力は QuitDialogBlockInputPatch が遮る)
                        Plugin.Log.LogWarning("[Quit] auto pause did not start " + FieldPause.StateText());
                        OpenNow();
                        return 0;
                    }
                    return s_phaseFrames <= INJECT_FRAMES ? KEY_PAUSE : 0;

                case Phase.InDialog:
                    if (!DialogOpen) { Finish(); return 0; }
                    return key;   // ダイアログは Sys.key を直接読むのでそのまま渡す。下のゲームには QuitDialogBlockInputPatch が渡さない (Core.sync の間 Sys.key を 0 に)

                case Phase.ReleasePause:
                    if (!FieldPause.IsPaused())
                    {
                        SetPhase(Phase.Idle);
                        ConfigMenu.GuardUntilReleased();
                        return 0;
                    }
                    if (s_phaseFrames > PAUSE_TIMEOUT)
                    {
                        Plugin.Log.LogWarning("[Quit] auto pause not released " + FieldPause.StateText());
                        SetPhase(Phase.Idle);
                        ConfigMenu.GuardUntilReleased();
                        return 0;
                    }
                    // Start だけを流してポーズを解除する (方向や他のボタンは渡さない)
                    return s_phaseFrames <= INJECT_FRAMES ? KEY_PAUSE : 0;
            }
            return key;
        }
    }

    /// <summary>
    /// 終了ダイアログが開いている間、下のゲームにパッドの入力を渡さない。
    ///
    /// 本体は Main.run で core.sync() (ゲーム本体) の後に m_endgame.Dialog() (ダイアログ) を回していて、両方が同じ Sys.key を見る。
    /// なので Esc でダイアログを開いても下のゲームは動き続け、フィールドでは十字キーで主人公が歩いて決定で話しかけ、
    /// ワールドマップやメニューでは (joy_data ではなく InputManager.GetMenuInput → Sys.checkKey2 で Sys.key を直接読むので) 項目が動く。
    /// ここでは Core.sync の間だけ Sys.key[0..3] を 0 にして (joy_data も 0)、終わったら元に戻す。
    /// ダイアログは sync の後に Sys.key を読むので影響しない。Sys.syncKey の押下エッジ・リピートの計算は Sys.key の前回値を使うが、
    /// 戻しているので乱れない。閉じた後も、全ボタンが離されるまで 0 のままにする (いいえ の決定を押したままフィールドに戻って話しかけないように)。
    /// 描画やイベントの進行はそのまま (本体と同じ)。生のパッドを読む MOD 側の補助 (先行入力・壁ずり・ダッシュ補正・マップの自由カーソル) は
    /// QuitGame.BlockInput を見て自分で止まる。
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.sync))]
    internal static class QuitDialogBlockInputPatch
    {
        private static readonly int[] s_saved = new int[4];
        private static bool s_masked;

        private static void Prefix(Core __instance)
        {
            if (QuitGame.DialogOpen) QuitGame.BlockInput = true;
            else if (QuitGame.BlockInput && Sys.key[0] == 0) QuitGame.BlockInput = false;
            if (!QuitGame.BlockInput) return;
            for (int i = 0; i < 4; i++) { s_saved[i] = Sys.key[i]; Sys.key[i] = 0; }
            s_masked = true;
            __instance.joy_data[0] = 0;
            __instance.joy_data[1] = 0;
        }

        private static void Postfix()
        {
            if (!s_masked) return;
            s_masked = false;
            for (int i = 0; i < 4; i++) Sys.key[i] = s_saved[i];
        }
    }
}
