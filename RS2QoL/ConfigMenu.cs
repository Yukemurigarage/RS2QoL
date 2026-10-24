using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RS2QoL.Patches;
using UnityEngine;

namespace RS2QoL
{
    /// <summary>
    /// MOD 独自の設定画面 (Unity IMGUI で描く)。
    /// ゲーム本体の環境設定は SpriteStudio のアセットに項目が固定されていて拡張できないため、
    /// パッドの ConfigMenu 機能 (既定: L3。フィールドのポーズ中とタイトル画面の NEW GAME / CONTINUE 選択中) または F1 で開く別画面として持つ。
    ///
    /// 項目はページに分かれていて L / R (LB / RB) で左右に切り替える (本体の持ち物画面などのページ送りと同じ)。
    /// ボタン割り当ては最初のページ (このゲームで最初に気になるのがボタンなので)。他のページからは右下の「ボタン割り当て」ボタンで
    /// 直接飛べる (戻るのは L / R で)。
    /// 見た目は本体の部品 (Esc の終了ダイアログと同じ枠、メニューの手のカーソル、L / R の矢印、とじる ボタン) を
    /// 起動中のゲームから読んで使う (UiParts)。部品が読めなければ文字で代用する。
    ///
    /// 入力は Util.GetKeypadState の最終段 (ConfigMenuGatePatch) から 1 論理フレームに 1 回 Tick で受け取り、
    /// 開いている間はゲームに 0 を返して操作を奪う。ゲーム自体は止めない (描画経路に触らないため)。
    /// 設定値は BepInEx の ConfigEntry に直接書くので、変更した瞬間に cfg へ保存される。
    /// </summary>
    internal static class ConfigMenu
    {
        private const int DIR_LEFT = 0x10000, DIR_UP = 0x20000, DIR_RIGHT = 0x40000, DIR_DOWN = 0x80000;
        private const int BTN_OK = 0x100000, BTN_CANCEL = 0x400000, BTN_L = 0x10, BTN_R = 0x40;
        private const int PHYS_LB = 4, PHYS_RB = 5; // PadMap.PhysicalNames の LB / RB (割り当てを変えていてもページ送りに使う)
        private const int BUTTON_BITS = 0x100000 | 0x400000 | 0x20 | 0x200000 | 0x100 | 0x200 | 0x10 | 0x40;
        private const float REPEAT_FIRST = 0.4f, REPEAT_NEXT = 0.1f;

        /// <summary>設定画面の 1 行。</summary>
        internal class Item
        {
            public string Label;
            public Func<string> Value;        // 右側に出す値 (null なら無し)
            public Action Left, Right;        // 左右で値を変える
            public Action Activate;           // 決定
            public Func<bool> Changed;        // true なら値を赤で描く (既定と違う割り当て)
            public bool Selectable = true;    // 説明行は false
            public string Label2;             // 2 列の行: 右半分に出す見出し (null なら 1 列)
            public Func<string> Value2;       // 2 列の行: 右半分の値
            public bool CloseButton;          // 右下の「とじる」ボタン (行としては描かない)
            public bool MapButton;            // とじる の左の「ボタン割り当て」ボタン (行としては描かない。割り当てページ以外)
            public bool Quill;                // 見出しの頭にワールドマップのカーソル (羽) の絵を出す (自由カーソルの行の目印)
            public int KeyIdx = -1;           // キー割り当ての行: KeyMap.Entries の添字 (取り込み中にその行だけ明るく残すため)
            public BepInEx.Configuration.ConfigEntryBase Entry;   // キャンセルで既定に戻す対象の設定 (null なら Reset を見る)
            public Action Reset;              // キャンセルで既定に戻す (Entry で足りない行用)
            public bool IsButton { get { return CloseButton || MapButton; } }
        }

        /// <summary>1 ページ。最後の項目は必ず とじる ボタン。</summary>
        internal class Page
        {
            public string Title;
            public readonly List<Item> Items = new List<Item>();
            public int Cursor;
            public int Scroll;
            public bool ShowPressed;          // 「押しているボタン」行を出す (ボタン割り当てページ)
            public bool ShowKeys;             // ShowPressed の中身をキーボードにする (キー割り当てページ)
            public Func<string> TitleRight;   // タイトルと同じ行の右側に赤で出す文字 (null か空なら無し。ShowPressed と同じ場所)
            public bool LongPage;             // 行数を枠の大きさに数えない (他のページに合わせた行数でスクロールする)
            /// <summary>右下のボタン (とじる / ボタン割り当て) を除いた行数。ボタンは常に Items の末尾。</summary>
            public int RowCount { get { int n = Items.Count; while (n > 0 && Items[n - 1].IsButton) n--; return n; } }
        }

        public static bool IsOpen { get; private set; }

        private static readonly List<Page> s_pages = new List<Page>();
        private static int s_page;               // 今のページ (閉じても覚えておく)
        private static int s_buttonMapPage = -1;
        private static int s_keyMapPage = -1;
        private static int s_capture = -1;        // キー割り当てで「押して決める」中の KeyMap.Entries の添字 (-1 = していない)
        private static int s_captureFrame;        // 取り込みを始めた Unity フレーム (始めた決定のキー自体を拾わないよう、直後は見ない)
        private static bool s_captureDone;        // 取り込みでキーを割り当てた直後 (次の Tick はそのキーを「押したまま」扱いにして、新しい割り当てで決定や移動が走らないようにする)
        private static Rect s_captureRow;         // 取り込み中の行の矩形 (Draw で毎回求める。幅 0 = この画面に出ていない)
        private static Texture2D s_dimTex;        // 取り込み中に行以外を暗くする半透明の黒
        private static Texture2D[] s_dimCorner;   // 穴の四隅 (左上・右上・左下・右下): 四分円の外側だけ半透明の黒 (角を丸く見せる)
        private const int CORNER_PX = 32;
        /// <summary>設定画面が Esc の押下を使った Unity フレーム (-1 = 無し)。本体はキーを離した時に終了ダイアログを開くので、EscGuardPatch がそれを取り消す。</summary>
        internal static int EscTakenFrame = -1;
        private static int s_prevKey;
        private static int s_lastKey;            // 直近の Tick で受けたビット (下部の入力表示用)
        private static bool s_prevOpenBtn;
        private static bool s_prevL, s_prevR;
        private static int s_heldDir;
        private static float s_nextRepeat;
        private static bool s_closeGuard; // 閉じた直後、全ボタンが離されるまでゲームに入力を渡さない
        private static int s_prevGuardKey; // 診断用: 直前に飲み込んだボタン
        private static string s_status = "";
        private static string s_prevState = "";
        private static int s_lastTickFrame = -1;

        private static GUIStyle s_box, s_title, s_line, s_lineSel, s_lineDim, s_section, s_footer, s_status_style, s_statusRight, s_btnText, s_lineChanged;
        private static GUIStyle s_rightCache, s_rightSelCache, s_rightDimCache, s_rightChangedCache;
        private static Font s_font;
        private static int s_fontSizeBuilt = -1;

        private static Page Cur { get { return s_pages[s_page]; } }

        // ---------------------------------------------------------------- 項目の組み立て

        public static void Build()
        {
            int[] cursors = new int[s_pages.Count];
            for (int i = 0; i < s_pages.Count; i++) cursors[i] = s_pages[i].Cursor;
            s_pages.Clear();

            // ボタン割り当ては最初のページ。他のページの右下の「ボタン割り当て」ボタンからも来られる
            Page p = AddPage("ボタン割り当て");
            p.ShowPressed = true;
            s_buttonMapPage = s_pages.Count - 1;
            for (int i = 0; i < PadMap.PhysicalNames.Length; i++)
            {
                int idx = i;
                p.Items.Add(new Item
                {
                    Label = PadMap.PhysicalLabels[idx],
                    // LB / RB はフィールドで別の役目 ([Economy] FieldLB / FieldRB) を持つので併記する (割り当てとは独立に物理ボタンで判定)
                    Value = () => PadMap.FunctionLabel(PadMap.Entries[idx].Value) + TreasuryScreen.FieldRoleSuffix(idx),
                    Left = () => CycleFunction(idx, -1),
                    Right = () => CycleFunction(idx, +1),
                    // 決定では変えない (割り当てを試しに押して決定が反応すると値が勝手に変わって混乱する)。左右だけで変える
                    Changed = () => PadMap.Entries[idx].Value != PadMap.DefaultFunctions[idx],
                    Reset = () => PadMap.Entries[idx].Value = PadMap.DefaultFunctions[idx],
                });
            }
            p.Items.Add(new Item
            {
                Label = "ボタン割り当てを既定に戻す",
                Activate = () => { PadMap.ResetToDefault(); s_status = "既定に戻しました"; },
            });

            // キーボードの割り当て (機能 → キー)。左右で候補を送るか、決定で「押して決める」
            p = AddPage("キー割り当て");
            p.ShowPressed = true;
            p.ShowKeys = true;
            p.LongPage = true;   // 行が多い (機能 16 + 2) ので枠の大きさには数えず、ボタン割り当ての行数でスクロールする (文字を小さくしない)
            s_keyMapPage = s_pages.Count - 1;
            if (KeyMap.Entries != null)
            {
                for (int i = 0; i < KeyMap.Functions.Length; i++)
                {
                    int idx = i;
                    p.Items.Add(new Item
                    {
                        Label = KeyMap.RowLabel(idx),
                        Value = () => KeyMap.Label(KeyMap.Entries[idx].Value),
                        Left = () => KeyMap.Cycle(idx, -1),
                        Right = () => KeyMap.Cycle(idx, +1),
                        Activate = () => BeginCapture(idx),
                        Changed = () => !KeyMap.IsDefault(idx),
                        Reset = () => KeyMap.Set(idx, KeyMap.Defaults[idx]),
                        KeyIdx = idx,
                    });
                }
            }
            p.Items.Add(new Item
            {
                Label = "キー割り当てを既定に戻す",
                Activate = () => { KeyMap.ResetToDefault(); s_status = "既定に戻しました"; },
            });
            p.Items.Add(new Item { Label = "方向は W A S D と矢印キー (固定)", Selectable = false });
            p.Items.Add(new Item { Label = "F1 設定画面 / Esc 終了 / F8 診断ログ / F7 その目印 (固定)", Selectable = false });   // 全角 30 字ほどまで (長いと 2 行に折れて見切れる。ユーザー 2026-10-22)

            p = AddPage("音量");
            AddPercent(p, "全体", AudioVolume.Master);
            AddPercent(p, "BGM", AudioVolume.Bgm);
            AddPercent(p, "効果音", AudioVolume.Se);

            p = AddPage("画面");
            p.Items.Add(new Item
            {
                Label = "画面モード",
                Value = () => WindowMode.ModeLabel(WindowMode.Mode.Value),
                Left = () => CycleWindowMode(-1),
                Right = () => CycleWindowMode(+1),
                Activate = WindowMode.Request,
            });
            p.Items.Add(new Item
            {
                Label = "ウィンドウサイズ",
                Value = WindowMode.SizeText,
                Left = () => WindowMode.CycleSize(-1),
                Right = () => WindowMode.CycleSize(+1),
                Activate = WindowMode.Request,
            });
            p.Items.Add(new Item
            {
                Label = "画面の同期",
                Value = () => DisplaySync.ModeLabel(DisplaySync.Mode.Value),
                Left = () => CycleDisplaySync(-1),
                Right = () => CycleDisplaySync(+1),
                Activate = DisplaySync.Apply,
            });
            p.Items.Add(new Item
            {
                Label = "60fps 表示",
                Value = () => Interp60.ModeLabel(Interp60.Mode.Value),
                Left = () => CycleInterp60(-1),
                Right = () => CycleInterp60(+1),
                Activate = () => CycleInterp60(+1),
            });
            AddToggle(p, "60fps 戦闘の味方・ダメージも補間", Interp60Battle.Enabled);
            AddToggle(p, "60fps ワールドマップの巻物も補間", Interp60FldAnim.Enabled);
            AddToggle(p, "マウスカーソルを自動で隠す", MouseCursor.AutoHide);
            AddToggle(p, "スマホ版のタッチ用ボタンの絵を消す (フィールド / メニュー / 戦闘)", TouchButtons.Hide);
            AddToggle(p, "ボタンの絵から A B X の丸文字を消す (文字を中央に)", ButtonGlyph.Enabled);
            p.Items.Add(new Item
            {
                Label = "ページ送りの矢印ボタンの大きさ (メニュー・店)",
                Value = () => ButtonGlyph.ScaleLabel(ButtonGlyph.Scale),
                Left = () => ButtonGlyph.CycleScale(-1),
                Entry = ButtonGlyph.ArrowScale,
                Right = () => ButtonGlyph.CycleScale(+1),
                Activate = () => ButtonGlyph.CycleScale(+1),
            });
            p.Items.Add(new Item
            {
                Label = "所持金のクラウンの絵の大きさ (次に開いた画面から)",
                Value = () => CoinIcon.Label(CoinIcon.Percent),
                Left = () => CoinIcon.Cycle(-1),
                Entry = CoinIcon.Scale,
                Right = () => CoinIcon.Cycle(+1),
                Activate = () => CoinIcon.Cycle(+1),
            });
            p.Items.Add(new Item
            {
                Label = "所持金のクラウンの絵を上にずらす (次に開いた画面から)",
                Value = () => CoinIcon.UpLabel(CoinIcon.Up),
                Left = () => CoinIcon.CycleUp(-1),
                Entry = CoinIcon.OffsetY,
                Right = () => CoinIcon.CycleUp(+1),
                Activate = () => CoinIcon.CycleUp(+1),
            });

            p = AddPage("ゲーム速度");
            p.Items.Add(new Item
            {
                Label = "切り替えボタン (ボタン割り当てで変更)",
                Value = () => FunctionButtonsText(PadFunction.SpeedToggle),
                Selectable = false,
            });
            p.Items.Add(new Item
            {
                Label = "倍速",
                Value = () => GameSpeed.Active ? "ON" : "OFF",
                Left = GameSpeed.Toggle,
                Right = GameSpeed.Toggle,
                Activate = GameSpeed.Toggle,
            });
            p.Items.Add(new Item
            {
                Label = "倍率",
                Value = () => "x" + GameSpeed.Multiplier.Value,
                Left = () => GameSpeed.Multiplier.Value = Mathf.Max(1, GameSpeed.Multiplier.Value - 1),
                Entry = GameSpeed.Multiplier,
                Right = () => GameSpeed.Multiplier.Value = Mathf.Min(4, GameSpeed.Multiplier.Value + 1),
            });
            p.Items.Add(new Item
            {
                Label = "メニュー・店の中の速さ",
                Value = () => GameSpeed.PercentLabel(GameSpeed.MenuSpeed.Value),
                Left = () => GameSpeed.CyclePercent(GameSpeed.MenuSpeed, -1),
                Entry = GameSpeed.MenuSpeed,
                Right = () => GameSpeed.CyclePercent(GameSpeed.MenuSpeed, +1),
                Activate = () => GameSpeed.CyclePercent(GameSpeed.MenuSpeed, +1),
            });
            p.Items.Add(new Item
            {
                Label = "倍速中の表示 (右上の x2)",
                Value = () => IndicatorLabel(GameSpeed.Indicator.Value),
                Left = () => CycleIndicator(-1),
                Entry = GameSpeed.Indicator,
                Right = () => CycleIndicator(+1),
                Activate = () => CycleIndicator(+1),
            });
            AddToggle(p, "起動時から倍速 ON", GameSpeed.StartEnabled);
            // 押している間だけモード (トリガーは引いた深さで x2 / x3 / x4)。% の行は 1 刻み
            p.Items.Add(new Item
            {
                Label = "倍速ボタンは押している間だけ",
                Value = () => GameSpeed.TriggerHold.Value ? "ON (今 " + GameSpeed.HoldStateText() + ")" : "OFF (押すたび切替)",
                Left = () => GameSpeed.TriggerHold.Value = !GameSpeed.TriggerHold.Value,
                Entry = GameSpeed.TriggerHold,
                Right = () => GameSpeed.TriggerHold.Value = !GameSpeed.TriggerHold.Value,
                Activate = () => GameSpeed.TriggerHold.Value = !GameSpeed.TriggerHold.Value,
            });
            p.Items.Add(new Item
            {
                Label = "  トリガーの引き量 (今)",
                Value = GameSpeed.TriggerDepthText,
                Selectable = false,
            });
            AddHoldPercent(p, "  x2 になる引き量 (浅く)", GameSpeed.HoldX2Percent, 1, 100);
            AddHoldPercent(p, "  x3 になる引き量", GameSpeed.HoldX3Percent, 0, 100);
            AddHoldPercent(p, "  x4 になる引き量 (深く)", GameSpeed.HoldX4Percent, 0, 100);
            AddHoldPercent(p, "  戻す時のゆとり", GameSpeed.HoldReleasePercent, 0, 50);
            AddToggle(p, "選んでいる文字の点滅は倍速でも等倍の速さに", CursorBlink.RealTime);

            p = AddPage("移動");
            p.Items.Add(new Item
            {
                Label = "先行入力 (マス移動中に押した方向を覚えておくフレーム数)",
                Value = () => Plugin.InputBufferFrames.Value == 0 ? "しない" : Plugin.InputBufferFrames.Value.ToString(),
                Left = () => TalkOnBump.CycleFrames(Plugin.InputBufferFrames, -1, 0, 30),
                Entry = Plugin.InputBufferFrames,
                Right = () => TalkOnBump.CycleFrames(Plugin.InputBufferFrames, +1, 0, 30),
                Activate = () => TalkOnBump.CycleFrames(Plugin.InputBufferFrames, +1, 0, 30),
            });
            p.Items.Add(new Item
            {
                Label = "└採用に必要な押し続けフレーム数 (1 ≒ 33ms。1 = 短い入力も拾う)",
                Value = () => Plugin.BufferTapMinFrames.Value.ToString(),
                Left = () => TalkOnBump.CycleFrames(Plugin.BufferTapMinFrames, -1, 1, 5),
                Entry = Plugin.BufferTapMinFrames,
                Right = () => TalkOnBump.CycleFrames(Plugin.BufferTapMinFrames, +1, 1, 5),
                Activate = () => TalkOnBump.CycleFrames(Plugin.BufferTapMinFrames, +1, 1, 5),
            });

            // ワールドマップのカーソルの行は、見出しの頭に本体の羽の絵を出して「あのカーソルの設定」と分かるようにする
            Item quill = AddToggle(p, "を十字キーで自由に動かす (スーファミ版)", WorldMapCursor.Enabled);
            if (quill != null) quill.Quill = true;
            p.Items.Add(new Item
            {
                Quill = true,
                Label = "の速さ (倍速でも同じ)",
                Value = () => WorldMapCursor.Speed.Value.ToString(),
                Left = () => WorldMapCursor.CycleSpeed(-1),
                Entry = WorldMapCursor.Speed,
                Right = () => WorldMapCursor.CycleSpeed(+1),
                Activate = () => WorldMapCursor.CycleSpeed(+1),
            });
            AddToggle(p, "ワールドマップを開く / 閉じる巻物の速さを倍速でも同じに", WorldScrollSpeed.Enabled);

            p = AddPage("会話");
            AddToggle(p, "会話送りはボタンを押すまで待つ", DialogueAdvance.WaitForPress);
            AddToggle(p, "十字キーでも会話を送る", DialogueAdvance.AdvanceByDpad);
            AddToggle(p, "仲間にするかの 2 択に「ステータスを見る」を足す", RecruitInfo.Enabled);
            AddToggle(p, "会話の選択肢でキャンセルを「いいえ」側 (最後の行) にする", DialogueCancel.Enabled);
            AddToggle(p, "ステータス画面で決定を押すと仲間のステータス窓を開く", RecruitInfo.PartyStatus);
            p.Items.Add(new Item
            {
                Label = "会話の文字送りの速さ (本体のまま以外は倍速でも同じ)",
                Value = () => MessageSpeed.Label(MessageSpeed.Mode.Value),
                Left = () => MessageSpeed.Cycle(-1),
                Entry = MessageSpeed.Mode,
                Right = () => MessageSpeed.Cycle(+1),
                Activate = () => MessageSpeed.Cycle(+1),
            });
            AddToggle(p, "NPC に押し当て続けると決定なしで話しかける", TalkOnBump.Enabled);
            p.Items.Add(new Item
            {
                Label = "└押し当てるフレーム数 (1 ≒ 33ms)",
                Value = () => TalkOnBump.Frames.Value.ToString(),
                Left = () => TalkOnBump.CycleFrames(TalkOnBump.Frames, -1, 1, 30),
                Entry = TalkOnBump.Frames,
                Right = () => TalkOnBump.CycleFrames(TalkOnBump.Frames, +1, 1, 30),
                Activate = () => TalkOnBump.CycleFrames(TalkOnBump.Frames, +1, 1, 30),
            });
            p.Items.Add(new Item
            {
                Label = "└会話が終わってから数え始めるまでの待ち",
                Value = () => TalkOnBump.CooldownFrames.Value.ToString(),
                Left = () => TalkOnBump.CycleFrames(TalkOnBump.CooldownFrames, -1, 0, 60),
                Entry = TalkOnBump.CooldownFrames,
                Right = () => TalkOnBump.CycleFrames(TalkOnBump.CooldownFrames, +1, 0, 60),
                Activate = () => TalkOnBump.CycleFrames(TalkOnBump.CooldownFrames, +1, 0, 60),
            });

            p = AddPage("戦闘");
            p.Items.Add(new Item
            {
                Label = "戦闘中の速さ",
                Value = () => GameSpeed.PercentLabel(GameSpeed.BattleSpeed.Value),
                Left = () => GameSpeed.CyclePercent(GameSpeed.BattleSpeed, -1),
                Entry = GameSpeed.BattleSpeed,
                Right = () => GameSpeed.CyclePercent(GameSpeed.BattleSpeed, +1),
                Activate = () => GameSpeed.CyclePercent(GameSpeed.BattleSpeed, +1),
            });
            p.Items.Add(new Item
            {
                Label = "コマンド選択中の速さ",
                Value = () => GameSpeed.PercentLabel(GameSpeed.CommandSpeed.Value),
                Left = () => GameSpeed.CyclePercent(GameSpeed.CommandSpeed, -1),
                Entry = GameSpeed.CommandSpeed,
                Right = () => GameSpeed.CyclePercent(GameSpeed.CommandSpeed, +1),
                Activate = () => GameSpeed.CyclePercent(GameSpeed.CommandSpeed, +1),
            });
            AddToggle(p, "戦闘が終わったら等倍に戻す", GameSpeed.ResetAfterBattle);
            p.Items.Add(new Item
            {
                Label = "技・攻撃アニメの速さ",
                Value = () => AttackAnim.Label(AttackAnim.Speed.Value),
                Left = () => AttackAnim.Cycle(-1),
                Entry = AttackAnim.Speed,
                Right = () => AttackAnim.Cycle(+1),
                Activate = () => AttackAnim.Cycle(+1),
            });
            AddToggle(p, "トリガーで倍速中は技アニメも倍速に従う", AttackAnim.FollowHold);
            AddToggle(p, "アニメするモンスター (ボス級) の動きは倍速中も等倍", MonsterAnim.Enabled);
            p.Items.Add(new Item
            {
                Label = "ダメージ数字の跳ねの速さ",
                Value = () => DamagePop.Label(DamagePop.Step.Value),
                Left = () => DamagePop.Cycle(-1),
                Entry = DamagePop.Step,
                Right = () => DamagePop.Cycle(+1),
                Activate = () => DamagePop.Cycle(-1),
            });
            p.Items.Add(new Item
            {
                Label = "ダメージ数字が消えるまでの長さ",
                Value = () => DamagePop.HoldLabel(DamagePop.HoldStep.Value),
                Left = () => DamagePop.CycleHold(-1),
                Entry = DamagePop.HoldStep,
                Right = () => DamagePop.CycleHold(+1),
                Activate = () => DamagePop.CycleHold(-1),
            });
            AddToggle(p, "倍速中もダメージ数字の見た目は等倍と同じに", DamagePop.RealTime);
            AddToggle(p, "結果表示はボタンを押すまで待つ", BattleResult.WaitForPress);
            AddToggle(p, "L / R で退却 / 防御 (スーファミ版)", BattleLR.Enabled);
            AddToggle(p, "全滅した時に「戦闘の振り返り」の窓を出す", BattleReview.Enabled);

            p = AddPage("戦闘の見た目");
            AddToggle(p, "ダメージ数字をスーファミ版のドット文字で", DamageFont.Sfc);
            p.Items.Add(new Item
            {
                Label = "ドット文字のダメージ数字の大きさ",
                Value = () => DamageFont.Label(DamageFont.ScalePercent.Value),
                Left = () => DamageFont.Cycle(-1),
                Entry = DamageFont.ScalePercent,
                Right = () => DamageFont.Cycle(+1),
                Activate = () => DamageFont.Cycle(+1),
            });
            AddToggle(p, "弱点をついたダメージ数字を黄色に", WeakPointColor.Enabled);
            AddToggle(p, "味方の HP / LP をスーファミ版の「現在/最大」で", PartyHpSfc.Enabled);
            p.Items.Add(new Item
            {
                Label = "モンスターの大きさ",
                Value = () => MonsterScale.Label(MonsterScale.Percent.Value),
                Left = () => MonsterScale.Cycle(-1),
                Entry = MonsterScale.Percent,
                Right = () => MonsterScale.Cycle(+1),
                Activate = () => MonsterScale.Cycle(+1),
                Changed = () => MonsterScale.Percent.Value != MonsterScale.DEFAULT,
            });
            p.Items.Add(new Item
            {
                Label = "味方の大きさ",
                Value = () => PlayerScale.Label(PlayerScale.Percent.Value),
                Left = () => PlayerScale.Cycle(-1),
                Entry = PlayerScale.Percent,
                Right = () => PlayerScale.Cycle(+1),
                Activate = () => PlayerScale.Cycle(+1),
                Changed = () => PlayerScale.Percent.Value != PlayerScale.DEFAULT,
            });
            p.Items.Add(new Item
            {
                Label = "ダメージ数字の縦位置 (モンスター)",
                Value = () => DamagePos.Label(DamagePos.MonsterY.Value),
                Left = () => DamagePos.Cycle(DamagePos.MonsterY, -1),
                Entry = DamagePos.MonsterY,
                Right = () => DamagePos.Cycle(DamagePos.MonsterY, +1),
                Activate = () => DamagePos.Cycle(DamagePos.MonsterY, +1),
            });
            p.Items.Add(new Item
            {
                Label = "ダメージ数字の縦位置 (味方)",
                Value = () => DamagePos.Label(DamagePos.PlayerY.Value),
                Left = () => DamagePos.Cycle(DamagePos.PlayerY, -1),
                Entry = DamagePos.PlayerY,
                Right = () => DamagePos.Cycle(DamagePos.PlayerY, +1),
                Activate = () => DamagePos.Cycle(DamagePos.PlayerY, +1),
            });

            // 敵の成長: 敵レベルごとの戦闘回数は 2 列に並べて、スクロールせずに全部見えるようにする (12 行)
            p = AddPage("敵の成長");
            p.Items.Add(new Item
            {
                Label = "今の戦闘回数",
                Value = () => EnemyGrowth.CounterLabel(),
                Selectable = false,
            });
            p.Items.Add(new Item
            {
                Label = "敵レベル = 敵の強さ (1〜16)。それより強い敵は乱数で本体どおり出る",
                Selectable = false,
            });
            AddToggle(p, "敵が強くなる速さを MOD で調整する", EnemyGrowth.Enabled);
            AddToggle(p, "敵の成長を一時停止する", EnemyGrowth.Paused);
            AddToggle(p, "退却した戦闘は数えない", EnemyGrowth.SkipEscape);
            p.Items.Add(new Item
            {
                Label = "敵が強くなる速さ",
                Value = () => EnemyGrowth.PercentLabel(),
                Left = () => EnemyGrowth.CyclePercent(-10),
                Entry = EnemyGrowth.Percent,
                Right = () => EnemyGrowth.CyclePercent(+10),
                Activate = () => EnemyGrowth.CyclePercent(+10),
                Changed = () => EnemyGrowth.Percent.Value != 100,
            });
            // 敵レベルごとの行は今の % で何戦目になるかの表示だけ (系統ごとのしきい値は % で一律に伸び縮みする)。
            // 左の列が敵レベル 2〜7、右の列が 8〜11 と上限
            p.Items.Add(new Item
            {
                Label = "敵レベル n 以上になる戦闘回数 (かっこ内は本体)",
                Selectable = false,
            });
            int half = (EnemyGrowth.PointCount + 1) / 2;
            for (int pi = 0; pi < half; pi++)
            {
                int lp = pi, rp = pi + half;
                bool hasR = rp < EnemyGrowth.PointCount;
                p.Items.Add(new Item
                {
                    Label = EnemyGrowth.PointShortName(lp),
                    Value = () => EnemyGrowth.FromLabel(lp),
                    Label2 = hasR ? EnemyGrowth.PointShortName(rp) : "",
                    Value2 = hasR ? (Func<string>)(() => EnemyGrowth.FromLabel(rp)) : null,
                    Selectable = false,
                    Changed = () => EnemyGrowth.FromChanged(lp),
                });
            }

            // 味方の成長: 技術点 (1 戦で倒した敵の点) を HP / 技 / 術 / 全体レベルそれぞれ別の % で読み替える
            p = AddPage("味方の成長");
            p.Items.Add(new Item
            {
                Label = "直前の戦闘の技術点",
                Value = () => PlayerGrowth.LastExpLabel(),
                Selectable = false,
            });
            AddToggle(p, "味方の成長の速さを MOD で調整する", PlayerGrowth.Enabled);
            AddGrowthPercent(p, "HP が上がる速さ", PlayerGrowth.HpPercent);
            AddGrowthPercent(p, "技レベル (武器) が上がる速さ", PlayerGrowth.WeaponPercent);
            AddGrowthPercent(p, "術レベルが上がる速さ", PlayerGrowth.MagicPercent);
            AddGrowthPercent(p, "全体レベルが上がる速さ (新しい仲間の初期レベル)", PlayerGrowth.PartyPercent);
            // 説明行は枠の幅 (全角 30 字ほど) に収まる長さにする
            p.Items.Add(new Item { Label = "上限 (HP 999 / レベル 50 / 全体 40) は本体のまま", Selectable = false });
            p.Items.Add(new Item { Label = "1 回に上がる量と、敵の強さ・税収も変わらない", Selectable = false });
            p.Items.Add(new Item { Label = "技術点は敵の強さで決まるので、敵を遅くすると成長も遅くなる", Selectable = false });

            // ひらめき: 瀕死 (膝をつく HP) か最後の 1 人の味方が攻撃した時だけ、技をひらめく確率を倍にする (ヒーローモード、既定 Off)
            p = AddPage("ひらめき");
            p.Items.Add(new Item
            {
                Label = "直前のひらめき判定",
                Value = () => SparkBoost.LastLabel(),
                Selectable = false,
            });
            AddToggle(p, "瀕死の味方はひらめきやすい (ヒーローモード)", SparkBoost.Enabled);
            AddToggle(p, "戦える仲間が 1 人だけなら HP に関係なく発動", SparkBoost.LastOneStanding);
            p.Items.Add(new Item
            {
                Label = "ひらめく確率の倍率",
                Value = () => SparkBoost.MultLabel(),
                Left = () => SparkBoost.CycleMult(-1),
                Entry = SparkBoost.Multiplier,
                Right = () => SparkBoost.CycleMult(+1),
                Activate = () => SparkBoost.CycleMult(+1),
                Changed = () => SparkBoost.MultChanged(),
            });
            p.Items.Add(new Item
            {
                Label = "瀕死とみなす HP (最大 HP に対する割合)",
                Value = () => SparkBoost.HpLabel(),
                Left = () => SparkBoost.CycleHp(-SparkBoost.STEP_HP),
                Entry = SparkBoost.HpPercent,
                Right = () => SparkBoost.CycleHp(+SparkBoost.STEP_HP),
                Activate = () => SparkBoost.CycleHp(+SparkBoost.STEP_HP),
                Changed = () => SparkBoost.HpChanged(),
            });
            p.Items.Add(new Item
            {
                Label = "瀕死の時に敵の閃きレベルに足す値",
                Value = () => SparkBoost.BonusLabel(),
                Left = () => SparkBoost.CycleBonus(-1),
                Entry = SparkBoost.LevelBonus,
                Right = () => SparkBoost.CycleBonus(+1),
                Activate = () => SparkBoost.CycleBonus(+1),
                Changed = () => SparkBoost.BonusChanged(),
            });
            p.Items.Add(new Item
            {
                Label = "覚醒判定: 0 % の技に 1 攻撃 1 回だけ行う確率",
                Value = () => SparkBoost.FloorLabel(),
                Left = () => SparkBoost.CycleFloor(-1),
                Entry = SparkBoost.FloorPercent,
                Right = () => SparkBoost.CycleFloor(+1),
                Activate = () => SparkBoost.CycleFloor(+1),
                Changed = () => SparkBoost.FloorChanged(),
            });
            p.Items.Add(new Item
            {
                Label = "覚醒判定の届く幅",
                Value = () => SparkBoost.GapLabel(),
                Left = () => SparkBoost.CycleGap(-SparkBoost.STEP_GAP),
                Entry = SparkBoost.FloorMaxGap,
                Right = () => SparkBoost.CycleGap(+SparkBoost.STEP_GAP),
                Activate = () => SparkBoost.CycleGap(+SparkBoost.STEP_GAP),
                Changed = () => SparkBoost.GapChanged(),
            });
            // 説明行は枠の幅 (全角 30 字ほど) に収まる長さにする
            // 仕組みの説明の行は出さない (README に書いてあることを設定画面で繰り返さない。ユーザー 2026-10-22)

            // 国庫: 税収は戦闘のたびに自動で国庫に入る (玉座に戻る必要はない)。その倍率と、今の国庫・1 戦あたりの額の表示
            p = AddPage("国庫");
            p.Items.Add(new Item
            {
                Label = "今の国庫",
                Value = () => TaxIncome.TreasuryLabel(),
                Selectable = false,
            });
            p.Items.Add(new Item
            {
                Label = "1 戦ごとに国庫に入る税収",
                Value = () => TaxIncome.IncomeLabel(),
                Selectable = false,
                Changed = () => TaxIncome.Percent.Value != 100,
            });
            p.Items.Add(new Item
            {
                Label = "税収の倍率",
                Value = () => TaxIncome.PercentLabel(),
                Left = () => TaxIncome.CyclePercent(-10),
                Entry = TaxIncome.Percent,
                Right = () => TaxIncome.CyclePercent(+10),
                Activate = () => TaxIncome.CyclePercent(+10),
                Changed = () => TaxIncome.Percent.Value != 100,
            });
            AddToggle(p, "退却した戦闘でも税収を入れる (本体どおり)", TaxIncome.OnEscape);
            p.Items.Add(new Item
            {
                Label = "敵の強さ・味方の成長・宝箱の N 万クラウン・施設の費用は変わらない",
                Selectable = false,
            });
            // 所持金: 上限と、どこでも金庫番 (国庫 ⇄ 所持金)。設定画面はフィールドのポーズ中かタイトルでしか開かないので、ここでの操作は安全
            p.Items.Add(new Item
            {
                Label = "今の所持金",
                Value = () => Wallet.MoneyLabel(),
                Selectable = false,
            });
            p.Items.Add(new Item
            {
                Label = "所持金の上限",
                Value = () => Wallet.MaxLabel(),
                Left = () => Wallet.CycleMax(-1),
                Entry = Wallet.Max,
                Right = () => Wallet.CycleMax(+1),
                Activate = () => Wallet.CycleMax(+1),
                Changed = () => Wallet.Cap != Wallet.VANILLA_MAX,
            });
            p.Items.Add(new Item
            {
                Label = "引き出す時に国庫に残す額",
                Value = () => Wallet.FloorLabel(),
                Left = () => Wallet.CycleFloor(-1),
                Entry = Wallet.TreasuryFloor,
                Right = () => Wallet.CycleFloor(+1),
                Activate = () => Wallet.CycleFloor(+1),
                Changed = () => Wallet.Floor != 100000,
            });
            p.Items.Add(new Item
            {
                Label = "国庫から引き出す (所持金を上限まで)",
                Value = () => Wallet.InGame() ? (Wallet.WithdrawableAmount() > 0 ? "+" + Wallet.WithdrawableAmount().ToString("N0") : "-") : "",
                Activate = () => { if (!Wallet.InGame()) { s_status = "ゲーム中だけ使えます"; return; } bool ok; s_status = Wallet.Withdraw(out ok); if (!ok) UiParts.SeCancel(); },
            });
            p.Items.Add(new Item
            {
                Label = "所持金を国庫に預ける (全額)",
                Activate = () => { if (!Wallet.InGame()) { s_status = "ゲーム中だけ使えます"; return; } bool ok; s_status = Wallet.Deposit(out ok); if (!ok) UiParts.SeCancel(); },
            });
            // フィールドの LB / RB (歩ける時・ポーズ中だけ別の役目。本体はフィールドで LB / RB を使わない)
            p.Items.Add(new Item
            {
                Label = "フィールドでの LB",
                Value = () => TreasuryScreen.ActionLabel(TreasuryScreen.FieldLB.Value),
                Left = () => TreasuryScreen.CycleAction(TreasuryScreen.FieldLB, -1),
                Entry = TreasuryScreen.FieldLB,
                Right = () => TreasuryScreen.CycleAction(TreasuryScreen.FieldLB, +1),
                Activate = () => TreasuryScreen.CycleAction(TreasuryScreen.FieldLB, +1),
                Changed = () => TreasuryScreen.FieldLB.Value != FieldLRAction.Treasury,
            });
            p.Items.Add(new Item
            {
                Label = "フィールドでの RB",
                Value = () => TreasuryScreen.ActionLabel(TreasuryScreen.FieldRB.Value),
                Left = () => TreasuryScreen.CycleAction(TreasuryScreen.FieldRB, -1),
                Entry = TreasuryScreen.FieldRB,
                Right = () => TreasuryScreen.CycleAction(TreasuryScreen.FieldRB, +1),
                Activate = () => TreasuryScreen.CycleAction(TreasuryScreen.FieldRB, +1),
                Changed = () => TreasuryScreen.FieldRB.Value != FieldLRAction.Withdraw,
            });
            p.Items.Add(new Item
            {
                Label = "どこでもアバロンの窓を開くボタン (歩ける時・ポーズ中に)",
                Value = () => TreasuryScreen.ButtonsText(FieldLRAction.Treasury, FunctionButtonsText(PadFunction.Treasury)),
                Selectable = false,
            });
            p.Items.Add(new Item
            {
                Label = "画面を開かずに引き出すボタン (歩ける時・ポーズ中に)",
                Value = () => TreasuryScreen.ButtonsText(FieldLRAction.Withdraw, FunctionButtonsText(PadFunction.Withdraw)),
                Selectable = false,
            });

            // 弔いの鍛冶: 皇帝が死んで継承すると 1 回たまり、開発所で国庫を使わずに開発できる。回数はセーブに入る
            p = AddPage(MourningForge.NAME);
            // ためている回数はセーブの値なので設定画面からは変えず、タイトルの行の右に赤で出すだけ (ユーザー 2026-10-21)
            p.TitleRight = () => "ためている回数: " + MourningForge.StockLabel();
            AddToggle(p, "皇帝が死んで継承すると、国庫を使わず 1 回開発できる", MourningForge.Enabled);
            p.Items.Add(new Item
            {
                Label = "ためられる上限",
                Value = () => MourningForge.MaxLabel(),
                Left = () => MourningForge.CycleMax(-1),
                Entry = MourningForge.MaxStock,
                Right = () => MourningForge.CycleMax(+1),
                Activate = () => MourningForge.CycleMax(+1),
                Changed = () => MourningForge.Max != 9,
            });
            // 説明行は枠の幅 (全角 30 字ほど) に収まる長さにする (長いと折り返して行からはみ出す)。
            // 本体の仕組み (年代ごとに各種類 1 回など) や年代ジャンプで 0 に戻ることは、分かった上で事務的に使われないよう書かない (ユーザー 2026-10-21)
            p.Items.Add(new Item { Label = "全滅か、勝っても皇帝の LP が 0 の時の継承で 1 回たまる", Selectable = false });
            p.Items.Add(new Item { Label = "開発所で「金は要りません」と申し出て、国庫は減らない", Selectable = false });

            p = AddPage("その他");
            AddToggle(p, "防具・盾の詳しい防御力を表示", ArmorInfo.Enabled);
            AddToggle(p, "店で装備している仲間に E を表示", ShopEquipMark.Enabled);
            AddToggle(p, "店で買う品物と今の装備の差 (▲N / ▼N) を表示", ShopEquipCompare.Enabled);
            AddToggle(p, "店で E の仲間が回って構える", ShopEquipSpin.Enabled);
            AddToggle(p, "ステータスの素早さを防具の重さを引いた戦闘の値に", PartyWeight.StatusEnabled);
            AddToggle(p, "ポーズの窓に MOD 設定画面の開き方を出す", PauseHint.Enabled);
            // 不具合報告用の診断ログ。cfg を手で書き換えなくても設定画面から ON にできるように (利用者からの指摘 2026-10-18)。
            // [Diagnostics] Enabled は各パッチが毎回 Value を読むので即時に効く (フリーズ監視だけは起動時に ON だった時のみ)
            Item diag = AddToggle(p, "診断ログを出す (不具合報告用)", Plugin.DiagEnabled);
            if (diag != null)
            {
                // 切り替えた瞬間から効く。どこに記録されるかを画面の下の行とログに出す (起動のたびに上書きされることも)
                Action onDiag = () => { s_status = InputDiag.SetEnabled(!Plugin.DiagEnabled.Value, "menu"); };
                diag.Left = onDiag; diag.Right = onDiag; diag.Activate = onDiag;
            }
            p.Items.Add(new Item { Label = "  再現の直後に Start+Select か F7 で目印を記録", Selectable = false });
            p.Items.Add(new Item
            {
                Label = "ソフトリセット (タイトル画面に戻る)",
                Activate = () => { if (!SoftReset.RequestFromMenu()) s_status = "今は戻れません"; },
            });
            AddToggle(p, "ソフトリセットはボタンで確認する", SoftReset.Confirm);
            p.Items.Add(new Item
            {
                Label = "ゲームを終了 (Steam に戻る)",
                Activate = () => { if (!QuitGame.RequestFromMenu()) s_status = "今は終了できません"; },
            });
            // 全ページの最後に右下のボタン: 「ボタン割り当て」(割り当てページ以外) と とじる。十字キーで最下行の次に来る
            // (下で ボタン割り当て → とじる → 先頭行に戻る)。ボタンの上では左右で隣のボタンに移る
            for (int i = 0; i < s_pages.Count; i++)
            {
                Page pg0 = s_pages[i];
                if (i != s_buttonMapPage)
                {
                    pg0.Items.Add(new Item
                    {
                        Label = "ボタン割り当て",
                        MapButton = true,
                        Activate = () => SetPage(s_buttonMapPage),
                        Right = () => pg0.Cursor = pg0.Items.Count - 1,   // → とじる (効果音は HandleDir が鳴らす)
                    });
                }
                Item close = new Item { Label = "とじる", Activate = Close, CloseButton = true };
                if (i != s_buttonMapPage) close.Left = () => pg0.Cursor = pg0.Items.Count - 2;   // → ボタン割り当て
                pg0.Items.Add(close);
                if (i < cursors.Length) s_pages[i].Cursor = cursors[i];
                Page pg = s_pages[i];
                if (pg.Cursor < 0 || pg.Cursor >= pg.Items.Count || !pg.Items[pg.Cursor].Selectable) pg.Cursor = FirstSelectable(pg);
            }
            if (s_page < 0 || s_page >= s_pages.Count) s_page = 0;
        }

        private static Page AddPage(string title)
        {
            Page p = new Page { Title = title };
            s_pages.Add(p);
            return p;
        }

        /// <summary>はい / いいえ の bool 設定の行。</summary>
        /// <summary>味方の成長の % 行 (左右で 10 % ずつ、100 % 以外は赤)。</summary>
        private static Item AddGrowthPercent(Page p, string label, BepInEx.Configuration.ConfigEntry<int> entry)
        {
            var item = new Item
            {
                Label = label,
                Value = () => PlayerGrowth.PercentLabel(entry),
                Left = () => PlayerGrowth.CyclePercent(entry, -PlayerGrowth.STEP_PCT),
                Entry = entry,
                Right = () => PlayerGrowth.CyclePercent(entry, +PlayerGrowth.STEP_PCT),
                Activate = () => PlayerGrowth.CyclePercent(entry, +PlayerGrowth.STEP_PCT),
                Changed = () => PlayerGrowth.PercentChanged(entry),
            };
            p.Items.Add(item);
            return item;
        }

        private static Item AddToggle(Page p, string label, BepInEx.Configuration.ConfigEntry<bool> entry)
        {
            if (entry == null) return null;
            Item it = new Item
            {
                Label = label,
                Value = () => entry.Value ? "はい" : "いいえ",
                Left = () => entry.Value = !entry.Value,
                Entry = entry,
                Right = () => entry.Value = !entry.Value,
                Activate = () => entry.Value = !entry.Value,
            };
            p.Items.Add(it);
            return it;
        }

        /// <summary>0〜100 (%) の整数設定を左右 5 刻みで変える行。</summary>
        /// <summary>1 % 刻みの行 (倍速の押している間だけモードの深さ)。</summary>
        private static void AddHoldPercent(Page p, string label, BepInEx.Configuration.ConfigEntry<int> entry, int min, int max)
        {
            if (entry == null) return;
            p.Items.Add(new Item
            {
                Label = label,
                Value = () => entry.Value == 0 && min == 0 ? "使わない" : entry.Value + " %",
                Left = () => entry.Value = Mathf.Max(min, entry.Value - 1),
                Entry = entry,
                Right = () => entry.Value = Mathf.Min(max, entry.Value + 1),
            });
        }

        private static void AddPercent(Page p, string label, BepInEx.Configuration.ConfigEntry<int> entry)
        {
            if (entry == null) return;
            p.Items.Add(new Item
            {
                Label = label,
                Value = () => entry.Value + " %",
                Left = () => entry.Value = Mathf.Max(0, entry.Value - 5),
                Entry = entry,
                Right = () => entry.Value = Mathf.Min(100, entry.Value + 5),
            });
        }

        private static string IndicatorLabel(SpeedIndicator v)
        {
            switch (v)
            {
                case SpeedIndicator.OnToggle: return "切替時だけ";
                case SpeedIndicator.Off: return "表示しない";
                default: return "常に表示";
            }
        }

        private static void CycleIndicator(int delta)
        {
            var all = (SpeedIndicator[])Enum.GetValues(typeof(SpeedIndicator));
            int cur = Array.IndexOf(all, GameSpeed.Indicator.Value);
            GameSpeed.Indicator.Value = all[(cur + delta + all.Length) % all.Length];
        }

        private static void CycleWindowMode(int delta)
        {
            var all = (WindowModeKind[])Enum.GetValues(typeof(WindowModeKind));
            int cur = Array.IndexOf(all, WindowMode.Mode.Value);
            WindowMode.Mode.Value = all[(cur + delta + all.Length) % all.Length]; // 変更時に SettingChanged で適用される
        }

        private static void CycleInterp60(int delta)
        {
            var all = (Interp60Kind[])Enum.GetValues(typeof(Interp60Kind));
            int cur = Array.IndexOf(all, Interp60.Mode.Value);
            Interp60.Mode.Value = all[(cur + delta + all.Length) % all.Length]; // 変更時に SettingChanged で適用される
        }

        private static void CycleDisplaySync(int delta)
        {
            var all = (DisplaySyncKind[])Enum.GetValues(typeof(DisplaySyncKind));
            int cur = Array.IndexOf(all, DisplaySync.Mode.Value);
            DisplaySync.Mode.Value = all[(cur + delta + all.Length) % all.Length]; // 変更時に SettingChanged で適用される
        }

        private static void CycleFunction(int physical, int delta)
        {
            PadFunction[] all = PadMap.CycleOrder;   // 既定の並び順 (ButtonRemapPatch.cs の CycleOrder)
            int cur = Array.IndexOf(all, PadMap.Entries[physical].Value);
            int next = (cur + delta + all.Length) % all.Length;
            PadMap.Entries[physical].Value = all[next];
            s_status = "";
            // ConfigMenu がどのボタンにも無いと二度と開けなくなるので知らせる (F1 では開ける)
            if (!HasFunction(PadFunction.ConfigMenu)) s_status = "注意: 設定画面を開くボタンが無くなりました (F1 で開けます)";
        }

        private static bool HasFunction(PadFunction f)
        {
            for (int i = 0; i < PadMap.Entries.Length; i++) if (PadMap.Entries[i].Value == f) return true;
            return false;
        }

        // ウィンドウ下部の固定行に出す「今押しているボタン」。十字キー / スティックの方向は Tick で受けた key から
        private static string PressedPhysicalText()
        {
            var sb = new StringBuilder();
            int dir = s_lastKey & (DIR_LEFT | DIR_UP | DIR_RIGHT | DIR_DOWN);
            if (dir != 0)
            {
                sb.Append("方向: ");
                if ((dir & DIR_UP) != 0) sb.Append("上");
                if ((dir & DIR_DOWN) != 0) sb.Append("下");
                if ((dir & DIR_LEFT) != 0) sb.Append("左");
                if ((dir & DIR_RIGHT) != 0) sb.Append("右");
            }
            for (int i = 0; i < PadMap.PhysicalNames.Length; i++)
            {
                if (PadMap.IsPhysicalDown(i))
                {
                    sb.Append(sb.Length > 0 ? " + " : "");
                    sb.Append(PadMap.PhysicalLabels[i]);
                }
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        private static int FirstSelectable(Page p)
        {
            for (int i = 0; i < p.Items.Count; i++) if (p.Items[i].Selectable) return i;
            return 0;
        }

        // ---------------------------------------------------------------- 開閉と入力

        // F1 で開く時のフィールドの自動ポーズ (どこでもアバロンと同じ手順: Start を 2 フレーム流してポーズを待ってから開く)。
        // 閉じてもポーズは残す (どこでもアバロンと同じ。ユーザー要望 2026-10-13。解除は Start かキャンセルで、その時に本体のポーズの解除の音が鳴る)
        private enum F1Phase { None, PauseFirst }
        private static F1Phase s_f1Phase;
        private static int s_f1Frames, s_f1Injected;
        private const int KEY_PAUSE = 0x100;      // Start の機能ビット
        private const int INJECT_FRAMES = 2;      // Start を立てる論理フレーム数 (押下エッジが 1 回立てばよい)
        private const int PAUSE_TIMEOUT = 60;     // ポーズの開始を待つ上限 (論理フレーム)

        /// <summary>F1 の自動ポーズの開始待ち中か。この間は本体のポーズの音 (FieldPause.TrackSound) を鳴らさず、設定画面の開く音だけにする。</summary>
        public static bool F1PauseBusy { get { return s_f1Phase != F1Phase.None; } }

        private static void BeginF1Pause()
        {
            Plugin.Log.LogInfo("[Config] F1 on the field: pausing first");
            // 音はここでは鳴らさない: ポーズがかかって開く時に Open が決定音を鳴らす (ポーズ自体の音は F1PauseBusy で止める)
            s_f1Phase = F1Phase.PauseFirst;
            s_f1Frames = 0;
            s_f1Injected = 0;
        }

        /// <summary>自動ポーズの開始待ちの間、Tick から呼ばれる。戻り値がゲームに渡る (Start を流す時以外は 0)。</summary>
        private static int TickF1Pause()
        {
            s_f1Frames++;
            if (IsFieldPaused())
            {
                s_f1Phase = F1Phase.None;
                Open();
                return 0;
            }
            if (s_f1Frames > PAUSE_TIMEOUT)
            {
                // ポーズがかからなくても開く (開いている間の入力は 0 を返して止める。NPC は動く)
                Plugin.Log.LogWarning("[Config] auto pause did not start " + FieldPause.StateText());
                s_f1Phase = F1Phase.None;
                Open();
                return 0;
            }
            // 歩いている途中 (マス移動中) なら、入力を止めて止まるのを待ってから Start を流す
            if (s_f1Injected == 0 && !SoftReset.IsFieldIdle(Main.core)) return 0;
            s_f1Injected++;
            return s_f1Injected <= INJECT_FRAMES ? KEY_PAUSE : 0;
        }

        /// <summary>開く (決定音)。MOD の窓は開く時に決定音、閉じる時にカーソル音で揃える (どこでもアバロン・ソフトリセットも同じ。ユーザー要望 2026-10-13)。</summary>
        public static void Open()
        {
            if (IsOpen) return;
            Build();
            IsOpen = true;
            s_status = "";
            s_capture = -1;
            s_heldDir = 0;
            s_prevL = s_prevR = true; // 開いた時点で押していても、離して押し直すまでページ送りしない
            UiParts.SeOk();
            Plugin.Log.LogInfo("[Config] open page=" + Cur.Title);
        }

        /// <summary>閉じる音 (カーソル音) を鳴らして閉じる。どの閉じ方 (開いたボタン・F1・Esc・とじる) でも同じ。</summary>
        private static void CloseWithSound()
        {
            UiParts.SeClose();
            Close();
        }

        /// <summary>閉じる。F1 でこちらがポーズをかけていても解除しない (どこでもアバロンと同じ。Start かキャンセルで解除する)。</summary>
        public static void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            s_closeGuard = true;
            Plugin.Log.LogInfo("[Config] close");
        }
        /// <summary>全ボタンが離されるまでゲームにボタンを渡さない (MOD 側の画面を閉じた直後の押しっぱなし対策)。</summary>
        /// <summary>設定画面が開いていれば下の案内の行に出す (F8 の診断ログの切り替えなど、画面の外からの操作の結果)。</summary>
        internal static void SetStatus(string s) { if (IsOpen) s_status = s; }

        public static void GuardUntilReleased()
        {
            s_closeGuard = true;
        }

        /// <summary>
        /// Util.GetKeypadState の最終段から毎論理フレーム呼ばれる。key は割り当て変更後のビット。
        /// 戻り値がゲームに渡る (開いている間は 0)。
        /// </summary>
        public static int Tick(int key)
        {
            s_lastKey = key;
            bool openBtn = PadMap.IsFunctionDown(PadFunction.ConfigMenu);
            bool padEdge = openBtn && !s_prevOpenBtn;
            s_prevOpenBtn = openBtn;
            // 倍速中は同じ Unity フレームで Tick が複数回呼ばれる。GetKeyDown はフレーム中ずっと true なので 1 回目だけ見る
            bool firstCallThisFrame = Time.frameCount != s_lastTickFrame;
            s_lastTickFrame = Time.frameCount;
            // 60fps 表示中は論理フレームを回さない Unity フレームがあり、そこでの GetKeyDown は Interp60 が持ち越している
            bool keyEdge = firstCallThisFrame && (Input.GetKeyDown(KeyCode.F1) || Interp60.TakeKeyDown(KeyCode.F1));
            bool escEdge = firstCallThisFrame && (Input.GetKeyDown(KeyCode.Escape) || Interp60.TakeKeyDown(KeyCode.Escape));
            // タイトルに戻る / ゲームを終了 ボタンの押下エッジ (設定画面が開いている間は無視するが、押下状態の追跡は毎回行う)
            SoftReset.HandleButton();
            QuitGame.HandleButton();
            Wallet.HandleButton();
            TreasuryScreen.HandleButton();
            TitleSkip.HandleDemo(key); // タイトル放置後の自動デモの戦闘中など: A/B/X/Y/Start で強制的にタイトルへ
            // 本体のポーズの開始 / 解除に効果音 (本体は無音)。MOD の画面が自分でポーズをかける流れ (どこでもアバロン・終了・ソフトリセット・F1 の設定画面) では
            // 鳴らさず、その画面の開閉の音 (開く = 決定音、閉じる = カーソル音) だけにする
            FieldPause.TrackSound(TreasuryScreen.Active || QuitGame.Active || SoftReset.Active || F1PauseBusy);

            if (InputDiag.Enabled)
            {
                // ポーズ判定の切り分け用: Core の状態が変わった時と、設定画面ボタンを押した時に出す
                string st = FieldPause.StateText();
                if (st != s_prevState)
                {
                    s_prevState = st;
                    Plugin.Log.LogInfo("[State] t=" + InputDiag.T() + " " + st);
                }
                if (padEdge)
                {
                    Plugin.Log.LogInfo("[Config] button pressed t=" + InputDiag.T() + " paused=" + IsFieldPaused() + " open=" + IsOpen + " " + st);
                }
            }

            if (!IsOpen)
            {
                // F1 の自動ポーズの開始待ち: その間は Start 以外を渡さない
                if (s_f1Phase != F1Phase.None)
                {
                    s_prevKey = key;
                    return TickF1Pause();
                }
                // タイトルに戻る: 割り当てボタンの押下 → 確認ダイアログ → ポーズ解除 → go_title。その間は入力を奪う
                if (SoftReset.Active)
                {
                    s_prevKey = key;
                    return SoftReset.Tick(key, escEdge);
                }
                // ゲームを終了: フィールドではポーズをかけてから本体の終了ダイアログ → いいえ ならポーズ解除。その間は Start 以外を渡さない
                if (QuitGame.Active)
                {
                    s_prevKey = key;
                    return QuitGame.Tick(key);
                }
                // 国庫の画面: フィールドではポーズをかけてから開く (閉じてもポーズは残る)。その間は Start 以外を渡さない
                if (TreasuryScreen.Active)
                {
                    s_prevKey = key;
                    return TreasuryScreen.Tick(key);
                }
                // パッドからは「フィールドで Start のポーズ中」と「タイトル画面の NEW GAME / CONTINUE 選択中」だけ開ける
                // (戦闘中は時間が進むので出さない。タイトルで開けるのは、ゲームを始める前に倍速やボタン割り当てを決められるようにするため)。
                // F1 はキーボード用の非常口として場面を問わず開ける。本体の終了ダイアログが出ている間は開かない
                if (((padEdge && (IsFieldPaused() || IsTitleIdle())) || keyEdge) && !QuitGame.DialogOpen)
                {
                    s_prevKey = key;
                    // F1 はフィールドで歩ける時にも開けるが、そのままでは裏でゲームが進む (敵がぶつかる) ので先にポーズをかける
                    if (keyEdge && !IsFieldPaused() && Wallet.InGame() && Wallet.IsFieldFree(Main.core))
                    {
                        BeginF1Pause();
                        return 0;
                    }
                    Open();
                    return 0;
                }
                if (s_closeGuard)
                {
                    if ((key & BUTTON_BITS) == 0 && !openBtn)
                    {
                        s_closeGuard = false;
                        if (InputDiag.Enabled && s_prevGuardKey != 0) Plugin.Log.LogInfo("[Guard] released t=" + InputDiag.T());
                        s_prevGuardKey = 0;
                    }
                    else if (InputDiag.Enabled && (key & BUTTON_BITS) != s_prevGuardKey)
                    {
                        // 閉じた直後の押しっぱなし対策で飲み込んだボタン (ここで消えた入力は [Pad] には出ない)
                        s_prevGuardKey = key & BUTTON_BITS;
                        Plugin.Log.LogInfo(string.Format("[Guard] swallowed t={0} key=0x{1:X} openBtn={2}", InputDiag.T(), key, openBtn));
                    }
                    return key & ~BUTTON_BITS;
                }
                return key;
            }

            HoldTitleAttractTimer();
            // キー割り当ての「押して決める」中: キーは Draw (Unity フレームごと) が拾う。Esc で中止、他は何もしない (ゲームにも渡さない)
            if (s_capture >= 0)
            {
                if (escEdge) { s_capture = -1; s_status = "中止しました"; EscTakenFrame = Time.frameCount; }
                s_prevKey = key;
                return 0;
            }
            // 割り当てた直後: 押したキーは新しい割り当てのビットで key に入ってくる (s_prevKey には無い) ので、そのままだと
            // 決定を割り当てたら同じ押下でまた取り込みが始まり、方向ならカーソルが動く。1 回「押したまま」として飲み込む
            if (s_captureDone)
            {
                s_captureDone = false;
                s_prevKey = key;
                return 0;
            }
            // 閉じるのは開いたのと同じボタン (既定 L3、F1)、Esc、「とじる」ボタンだけ。
            // キャンセルでは閉じない (ポーズ → L3 → 設定 → L3 → Start で解除、の流れを崩さないため)
            if (padEdge || keyEdge || escEdge)
            {
                if (escEdge) EscTakenFrame = Time.frameCount;
                CloseWithSound();
                s_prevKey = key;
                return 0;
            }

            int pressed = key & ~s_prevKey;
            s_prevKey = key;

            // ページ送り: L / R の機能ビットか、割り当てに関わらず物理の LB / RB
            bool lNow = (key & BTN_L) != 0 || PadMap.IsPhysicalDown(PHYS_LB);
            bool rNow = (key & BTN_R) != 0 || PadMap.IsPhysicalDown(PHYS_RB);
            bool lEdge = lNow && !s_prevL, rEdge = rNow && !s_prevR;
            s_prevL = lNow; s_prevR = rNow;
            if (lEdge) { MovePage(-1); return 0; }
            if (rEdge) { MovePage(+1); return 0; }

            // 方向: 押した瞬間 + 長押しリピート
            int dir = key & (DIR_LEFT | DIR_UP | DIR_RIGHT | DIR_DOWN);
            float now = Time.realtimeSinceStartup;
            if (dir == 0)
            {
                s_heldDir = 0;
            }
            else if (dir != s_heldDir)
            {
                s_heldDir = dir;
                s_nextRepeat = now + REPEAT_FIRST;
                HandleDir(dir);
            }
            else if (now >= s_nextRepeat)
            {
                s_nextRepeat = now + REPEAT_NEXT;
                HandleDir(dir);
            }

            if ((pressed & BTN_OK) != 0)
            {
                Item it = Cur.Items[Cur.Cursor];
                if (it.Activate != null)
                {
                    if (it.CloseButton) { CloseWithSound(); return 0; }   // 閉じる音は どの閉じ方でも同じ (カーソル音)
                    UiParts.SeOk();
                    it.Activate();
                }
            }
            // キャンセルは選んでいる行を既定に戻す (閉じもページも戻らない。「ボタン割り当て」ボタンで飛んだ後に戻る仕組みは混乱するのでやめた)
            if ((pressed & BTN_CANCEL) != 0) ResetRow(Cur.Items[Cur.Cursor]);
            return 0;
        }

        /// <summary>フィールドで Start のポーズ中か。判定は FieldPause (PausePatch.cs) 参照。</summary>
        public static bool IsFieldPaused()
        {
            return FieldPause.IsPaused();
        }

        private const int MENU_TITLE1 = 22;          // Menu.TITLE1: NEW GAME / CONTINUE のタイトル画面 (Menu._title1)
        private const int TITLE_SUB_PROC_10 = 6;     // Menu.TITLE_SUB_PROC.PROC_10: 項目を選んでいる待機状態
        private const int TITLE_ATTRACT_WAIT = 1500; // _title1 が無操作でデモに移るまでのフレーム数 (titlework[3] の初期値)
        private static readonly AccessTools.FieldRef<Menu, int[]> s_titlework = AccessTools.FieldRefAccess<Menu, int[]>("titlework");

        /// <summary>
        /// タイトル画面で NEW GAME / CONTINUE を選んでいる待機状態か (Core.proc[0] == 0 のうち Menu._title1 の PROC_10)。
        /// タイトルの入力は InputManager.GetMenuInput → Sys.key (= Util.GetKeypadState の結果) 経由なので、
        /// 開いている間 Tick が 0 を返せばタイトルのカーソルは動かない。
        /// </summary>
        public static bool IsTitleIdle()
        {
            if (Main.proc != 2 || Main.core == null || Main.menu == null) return false;
            if (Main.core.proc[0] != 0) return false;
            return Main.menu.menu_type == MENU_TITLE1 && Main.menu.proc[0] == TITLE_SUB_PROC_10;
        }

        /// <summary>タイトルで開いている間、無操作でデモに移るタイマー (titlework[3]) を進めない。</summary>
        private static void HoldTitleAttractTimer()
        {
            if (!IsTitleIdle()) return;
            try
            {
                int[] w = s_titlework(Main.menu);
                if (w != null && w.Length > 3 && w[3] > 0) w[3] = TITLE_ATTRACT_WAIT;
            }
            catch (Exception) { }
        }

        /// <summary>機能 f が割り当てられている物理ボタンの表示名 (複数なら「・」区切り、無ければ「なし」)。</summary>
        private static string FunctionButtonsText(PadFunction f)
        {
            if (PadMap.Entries == null) return "?";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < PadMap.Entries.Length; i++)
            {
                if (PadMap.Entries[i].Value != f) continue;
                if (sb.Length > 0) sb.Append("・");
                sb.Append(PadMap.PhysicalLabels[i]);
            }
            return sb.Length > 0 ? sb.ToString() : "なし";
        }

        /// <summary>キー割り当ての行で決定: 次に押したキーを割り当てる (Draw の CaptureKey が拾う)。</summary>
        private static void BeginCapture(int idx)
        {
            s_capture = idx;
            s_captureFrame = Time.frameCount;
            s_status = "キーを押してください (Esc で中止)";
        }

        /// <summary>Draw (OnGUI、Unity フレームごと) から: 取り込み中なら押されたキーを割り当てる。始めた決定のキー自体は拾わない (直後の 2 フレームは見ない)。</summary>
        private static void CaptureKey()
        {
            if (s_capture < 0 || KeyMap.Entries == null) return;
            if (Time.frameCount - s_captureFrame < 2) return;
            KeyCode k = KeyMap.FirstKeyDown();
            if (k == KeyCode.None) return;
            int idx = s_capture;
            s_capture = -1;
            s_captureDone = true;
            PadFunction cleared = KeyMap.Set(idx, k);
            s_status = KeyMap.RowLabel(idx) + " = " + KeyMap.Label(k)
                + (cleared != PadFunction.None ? " (" + PadMap.FunctionLabel(cleared) + " は なし に)" : "");
            UiParts.SeOk();
        }

        private static void HandleDir(int dir)
        {
            Item it = Cur.Items[Cur.Cursor];
            if ((dir & DIR_UP) != 0) MoveCursor(-1);
            else if ((dir & DIR_DOWN) != 0) MoveCursor(+1);
            else if ((dir & DIR_LEFT) != 0) { if (it.Left != null) { UiParts.SeCursor(); it.Left(); } }
            else if ((dir & DIR_RIGHT) != 0) { if (it.Right != null) { UiParts.SeCursor(); it.Right(); } }
        }

        /// <summary>行の値を既定に戻す (キャンセルボタン)。Entry があれば config の既定値、無ければ Reset。どちらも無い行は何もしない。</summary>
        private static void ResetRow(Item it)
        {
            if (it == null) return;
            if (it.Entry != null) it.Entry.BoxedValue = it.Entry.DefaultValue;
            else if (it.Reset != null) it.Reset();
            else return;
            UiParts.SeCancel();
            string v = it.Value != null ? it.Value() : "";
            s_status = "既定に戻しました" + (v.Length > 0 ? " (" + v + ")" : "");
        }

        private static void MoveCursor(int delta)
        {
            Page p = Cur;
            int n = p.Items.Count;
            int c = p.Cursor;
            for (int tries = 0; tries < n; tries++)
            {
                c = (c + delta + n) % n;
                if (p.Items[c].Selectable) { p.Cursor = c; UiParts.SeCursor(); return; }
            }
        }

        private static void MovePage(int delta)
        {
            SetPage((s_page + delta + s_pages.Count) % s_pages.Count);
        }

        private static void SetPage(int page)
        {
            if (page < 0 || page >= s_pages.Count) return;
            s_page = page;
            s_status = "";
            s_heldDir = 0;
            UiParts.SeCursor();
        }

        // ---------------------------------------------------------------- 描画 (Plugin.OnGUI から)

        public static void Draw()
        {
            if (!IsOpen || s_pages.Count == 0) return;
            Page page = Cur;

            // 行数は全ページで一番多いページ (ボタン割り当て) に揃え、ページを替えても枠の大きさが変わらないようにする。
            // 枠は画面の上下ぎりぎりまで使い、その行数が全部収まるように行の高さと文字の大きさを決める
            // (タイトル 1 行 + ページ見出し 1.2 行 + 項目 + 0.3 行 + ボタンの段 1.5 行 + 0.2 行 + 操作説明 1 行 = 項目 + 5.2 行、
            // 余白は上下 0.6 行ずつ)。それでも足りない小さな画面ではカーソルに追従してスクロールする
            const float FIXED_LINES = 1f + 1.2f + 0.3f + 1.5f + 0.2f + 1f;
            int rows = 1;
            for (int i = 0; i < s_pages.Count; i++) if (!s_pages[i].LongPage) rows = Mathf.Max(rows, s_pages[i].RowCount);
            float outer = Mathf.Round(UiParts.Unit * 8f);
            float lineH = (Screen.height - 2 * outer) / (rows + FIXED_LINES + 1.2f);
            int fontSize = Mathf.Clamp(Mathf.FloorToInt(lineH / 1.45f), 12, Mathf.Max(12, Screen.height / 30));
            lineH = Mathf.Min(lineH, fontSize * 1.6f); // 大きな画面では文字に合わせる (行間が空きすぎない)
            EnsureStyles(fontSize);
            float pad = lineH * 0.6f;
            float w = Mathf.Min(Screen.width - 2 * outer, fontSize * 36f);
            float fixedH = pad * 2 + lineH * FIXED_LINES;
            int visible = Mathf.Max(1, Mathf.FloorToInt((Screen.height - 2 * outer - fixedH) / lineH));
            if (visible > rows) visible = rows;
            ScrollToCursor(page, visible);
            float h = fixedH + lineH * visible;
            float x = (Screen.width - w) / 2f;
            float y = (Screen.height - h) / 2f;

            // 枠 (Esc の終了ダイアログと同じ) の中に本体メニューの羊皮紙の壁紙を敷く。部品が無ければ単色の箱
            UiParts.Shadow = !UiParts.DrawPaperWindow(new Rect(x, y, w, h));
            if (UiParts.Shadow) GUI.Box(new Rect(x, y, w, h), GUIContent.none, s_box);
            EnsureTextColors(!UiParts.Shadow);
            float cy = y + pad;
            float cx = x + pad, cw = w - 2 * pad;
            float titleY = cy - lineH * 0.25f;   // タイトルの行は少し上に (枠の上縁に寄せる)
            // タイトルの文字は行の文字より 4px 大きいので、枠は行 1.3 本分の高さにして上下が欠けないようにする
            Rect titleR = new Rect(cx, titleY, cw, lineH * 1.3f);
            UiParts.Label(titleR, "RS2QoL 設定  (v" + MyPluginInfo.PLUGIN_VERSION + ")", s_title);
            // ボタン割り当てページでは、タイトルと同じ行の右側に今押しているボタン (割り当ての確認用)
            if (page.ShowPressed)
            {
                string pressed = page.ShowKeys ? "押しているキー: " + KeyMap.PressedText() : "押しているボタン: " + PressedPhysicalText();
                UiParts.Label(titleR, pressed, s_statusRight);
            }
            else if (page.TitleRight != null)
            {
                // 弔いの鍛冶ページでは、ためている回数 (セーブの値。設定画面からは変えない) を同じ場所に赤で出す
                string tr = page.TitleRight();
                if (!string.IsNullOrEmpty(tr)) UiParts.Label(titleR, tr, s_statusRight);
            }
            CaptureKey();   // キー割り当ての「押して決める」中なら拾う (マウスで他のページに移っていても)
            cy += lineH;

            // ページ見出し: [L] ページ名 (n/N) [R]。矢印は枠の左右の端に固定 (マウスでクリックする所なので、ページで見出しの幅が変わっても動かさない)
            float headH = lineH * 1.2f;
            float arrowH = headH;
            float arrowW = arrowH * UiParts.ArrowAspect;
            string head = page.Title + "   " + (s_page + 1) + " / " + s_pages.Count;
            GUIStyle headStyle = Centered(s_section);
            float lX = cx;
            float rX = cx + cw - arrowW;
            Rect lR = new Rect(lX, cy, arrowW, arrowH);
            Rect rR = new Rect(rX, cy, arrowW, arrowH);
            if (!UiParts.DrawArrow(lR, true)) UiParts.Label(lR, "◀", s_section);
            if (!UiParts.DrawArrow(rR, false)) UiParts.Label(rR, "▶", s_section);
            if (UiParts.ClickArea(lR)) MovePage(-1);
            if (UiParts.ClickArea(rR)) MovePage(+1);
            Rect headR = new Rect(cx + arrowW, cy, cw - arrowW * 2, headH);
            UiParts.Label(headR, head, headStyle);
            if (page.Scroll > 0) UiParts.Label(headR, "▲", RightAligned(s_line));
            cy += headH;

            // 項目 (とじる ボタンの行は描かない)
            float curW = lineH * UiParts.CursorAspect; // 手のカーソルの幅 (文字はその右から)
            float textX = cx + curW + fontSize * 0.3f;
            float textW = cw - (textX - cx);
            int end = Mathf.Min(page.RowCount, page.Scroll + visible);
            s_captureRow = new Rect(0, 0, 0, 0);
            for (int i = page.Scroll; i < end; i++)
            {
                Item it = page.Items[i];
                Rect r = new Rect(textX, cy, textW, lineH);
                bool sel = i == page.Cursor;
                // 取り込み中の行の穴。文字は行の矩形の中で中心より下に描かれる (実機で測って行の高さの 1 割ほど) ので、その分下げて少し低くする
                if (s_capture >= 0 && it.KeyIdx == s_capture) s_captureRow = new Rect(x, cy + lineH * 0.125f, w, lineH * 0.95f);
                GUIStyle st = sel ? s_lineSel : (it.Selectable ? s_line : s_lineDim);
                if (sel) DrawHand(cx + curW, cy + lineH * 0.6f, lineH);   // 文字は行の中心より少し下に描かれるので、手も同じ分 (1 割) 下げて揃える (取り込み中の穴とも合う)
                if (it.Label2 != null)
                {
                    // 2 列の行: 左右の半分にそれぞれ 見出し + 右寄せの値 (表示だけの行用)
                    bool ch = it.Changed != null && it.Changed();
                    float gap = fontSize * 1.5f;
                    float halfW = (textW - gap) / 2f;
                    Rect lr = new Rect(textX, cy, halfW, lineH);
                    Rect rr = new Rect(textX + halfW + gap, cy, halfW, lineH);
                    UiParts.Label(lr, it.Label, st);
                    if (it.Value != null) UiParts.Label(lr, it.Value(), RightAligned(ch ? s_lineChanged : st));
                    UiParts.Label(rr, it.Label2, st);
                    if (it.Value2 != null) UiParts.Label(rr, it.Value2(), RightAligned(ch ? s_lineChanged : st));
                    cy += lineH;
                    continue;
                }
                if (it.Quill)
                {
                    // 見出しの頭にワールドマップのカーソル (羽) の絵。行の高さの正方形を行の中心に置き、文字はその右から
                    // (行より大きくすると上の行の文字に羽の先がかかる)
                    float qh = lineH;
                    if (UiParts.DrawQuill(new Rect(textX, cy + (lineH - qh) / 2f, qh, qh)))
                    {
                        float shift = qh + fontSize * 0.2f;
                        r.x += shift;
                        r.width -= shift;
                    }
                }
                UiParts.Label(r, it.Label, st);
                if (it.Value != null)
                {
                    string v = it.Value();
                    if (it.Left != null || it.Right != null) v = "< " + v + " >";
                    bool changed = it.Changed != null && it.Changed();
                    UiParts.Label(r, v, RightAligned(changed ? s_lineChanged : st));   // 既定と違う値は赤 (防具の詳細窓の特性ありと同じ)
                }
                cy += lineH;
            }
            cy += lineH * (visible - (end - page.Scroll)); // 短いページでも下部の位置を揃える
            cy += lineH * 0.3f;

            // 下部は 2 段: ボタンの段 (右に ボタン割り当て / とじる、左に状態メッセージ) と、枠の一番下の操作説明 1 行 (幅いっぱい、収まる大きさの文字)
            float btnH = Mathf.Round(lineH * 1.5f);          // 2 つのボタンの画像は同じ高さ (130px) なので、整数の高さにそろえて 1px の差が出ないようにする
            float footH = lineH;
            float footY = y + h - pad - footH;
            float btnY = Mathf.Round(footY - lineH * 0.2f - btnH);

            // 右: とじる ボタン (画像。マウスでも押せる。カーソルが乗っている時は手を添える)
            float btnW = Mathf.Round(btnH * UiParts.CloseButtonAspect);
            Rect btnR = new Rect(Mathf.Round(cx + cw - btnW), btnY, btnW, btnH);
            int closeIdx = page.Items.Count - 1;
            bool closeSel = page.Cursor == closeIdx;
            if (!UiParts.DrawCloseButton(btnR)) UiParts.Label(btnR, "[ とじる ]", Centered(closeSel ? s_lineSel : s_line));
            if (closeSel) DrawHand(btnR.x, btnR.y + btnR.height / 2f, lineH);
            if (UiParts.ClickArea(btnR)) CloseWithSound();

            // とじる の左: 「ボタン割り当て」ボタン (割り当てページ以外。無地の金のプレートに文字を載せる。どのページからでも割り当てページへ)
            float btnsW = btnW;
            int mapIdx = closeIdx - 1;
            if (mapIdx >= 0 && page.Items[mapIdx].MapButton)
            {
                float mapW = Mathf.Round(btnH * UiParts.PlainButtonAspect);
                float gap = Mathf.Round(lineH * UiParts.CursorAspect + fontSize * 0.4f);   // とじる に添える手のカーソル (幅 lineH*CursorAspect) が ボタン割り当て に被らない間隔
                Rect mapR = new Rect(Mathf.Round(btnR.x - gap - mapW), btnR.y, mapW, btnH);
                bool mapSel = page.Cursor == mapIdx;
                if (UiParts.DrawPlainButton(mapR)) DrawPlateText(mapR, "ボタン割り当て", Mathf.RoundToInt(fontSize * 0.95f));
                else UiParts.Label(mapR, "[ ボタン割り当て ]", Centered(mapSel ? s_lineSel : s_line));
                if (mapSel) DrawHand(mapR.x, mapR.y + mapR.height / 2f, lineH);
                if (UiParts.ClickArea(mapR)) { UiParts.SeOk(); page.Items[mapIdx].Activate(); }
                btnsW += mapW + gap;
            }

            // ボタンの段の左: 状態メッセージ (既定に戻しました 等) と、まだ下に行がある印
            // 状態メッセージ (既定に戻しました / 診断ログの保存先 等)。ボタンの左に置くと長い文がボタンに被る (ユーザー指摘 2026-10-18、
            // docs/shindanlog5.png) ので、ページの最後の行の枠が空いている時 (行数が visible より少ない短いページ) はその空いた行に全幅・
            // 折り返し無しで出す。行が埋まっているページでは従来どおりボタンの左 (短い文しか出ない)。▼ はボタンの左の行の右端
            float statW = cw - btnsW - fontSize * 0.5f;
            bool spareRow = end - page.Scroll < visible;
            Rect statR = spareRow ? new Rect(cx, cy - lineH, cw, lineH) : new Rect(cx, btnY, statW, btnH);
            if (s_status.Length > 0) UiParts.Label(statR, s_status, spareRow ? StatusStyleNoWrap() : s_status_style);
            if (end < page.RowCount) UiParts.Label(new Rect(cx, cy, statW, lineH), "▼", RightAligned(s_line));

            // 一番下: 操作の説明を 1 行で (幅に収まるまで文字を小さくする)
            const string foot = "L / R: ページ  十字キー: 移動  左右: 変更  決定: 実行  キャンセル: 既定に戻す  開いたボタン・F1・Esc: 閉じる";
            UiParts.Label(new Rect(cx, footY, cw, footH), foot, FitFooterStyle(foot, cw, fontSize));

            // キー割り当ての「押して決める」中: 画面全体を暗くして、取り込み中の行と状態メッセージだけ明るく残す
            // (今は他の操作を受け付けないこと、どの行に入るかを一目で分かるように)
            // 状態メッセージの穴: 左に余白を足し (文字が縁に付かないように)、文字の中心 (ボタンの段の中心より少し下) に合わせて行 1.1 本分の高さ
            if (s_capture >= 0)
            {
                float statH = lineH * 1.1f;
                Rect statHole = new Rect(statR.x - pad * 0.5f, statR.y + statR.height / 2f + lineH * 0.13f - statH / 2f, statR.width + pad * 0.5f, statH);
                DrawCaptureDim(statHole, lineH * 0.4f);
            }
        }

        /// <summary>取り込み中の覆い。s_captureRow (幅 0 なら無し) と状態メッセージの所に穴を開けて、残りに半透明の黒を敷く (穴の中は紙のまま明るい)。</summary>
        private static void DrawCaptureDim(Rect statR, float radius)
        {
            if (s_dimTex == null) s_dimTex = SolidTexture(new Color(0f, 0f, 0f, 0.65f));
            float sw = Screen.width, sh = Screen.height;
            Rect row = s_captureRow;
            if (row.width <= 0) row = new Rect(0, 0, 0, 0);   // 他のページに移っている: 行の穴は無し
            if (s_status.Length == 0) statR = new Rect(0, 0, 0, 0);
            // 状態メッセージは行より下にあるので、行の下端で上下に分けてそれぞれ穴を開ける
            DimExcept(new Rect(0, 0, sw, row.yMax), row, radius);
            DimExcept(new Rect(0, row.yMax, sw, sh - row.yMax), statR, radius);
        }

        /// <summary>area を hole (area にかからない部分は切る) を避けて 4 枚の半透明の黒で覆い、穴の四隅は半径 radius で丸める。</summary>
        private static void DimExcept(Rect area, Rect hole, float radius)
        {
            float hx0 = Mathf.Clamp(hole.xMin, area.xMin, area.xMax), hx1 = Mathf.Clamp(hole.xMax, area.xMin, area.xMax);
            float hy0 = Mathf.Clamp(hole.yMin, area.yMin, area.yMax), hy1 = Mathf.Clamp(hole.yMax, area.yMin, area.yMax);
            if (hx1 <= hx0 || hy1 <= hy0) { if (area.width > 0 && area.height > 0) GUI.DrawTexture(area, s_dimTex); return; }
            if (hy0 > area.yMin) GUI.DrawTexture(new Rect(area.xMin, area.yMin, area.width, hy0 - area.yMin), s_dimTex);
            if (area.yMax > hy1) GUI.DrawTexture(new Rect(area.xMin, hy1, area.width, area.yMax - hy1), s_dimTex);
            if (hx0 > area.xMin) GUI.DrawTexture(new Rect(area.xMin, hy0, hx0 - area.xMin, hy1 - hy0), s_dimTex);
            if (area.xMax > hx1) GUI.DrawTexture(new Rect(hx1, hy0, area.xMax - hx1, hy1 - hy0), s_dimTex);
            // 四隅: 穴の内側の角に四分円の外側だけ暗い絵を重ねる
            float r = Mathf.Min(radius, (hx1 - hx0) / 2f, (hy1 - hy0) / 2f);
            if (r < 1f) return;
            if (s_dimCorner == null) s_dimCorner = new[] { CornerTexture(1, 0), CornerTexture(0, 0), CornerTexture(1, 1), CornerTexture(0, 1) };   // 左上・右上・左下・右下の角に置く絵: 円の中心はそれぞれ右下・左下・右上・左上
            GUI.DrawTexture(new Rect(hx0, hy0, r, r), s_dimCorner[0]);
            GUI.DrawTexture(new Rect(hx1 - r, hy0, r, r), s_dimCorner[1]);
            GUI.DrawTexture(new Rect(hx0, hy1 - r, r, r), s_dimCorner[2]);
            GUI.DrawTexture(new Rect(hx1 - r, hy1 - r, r, r), s_dimCorner[3]);
        }

        /// <summary>
        /// 角の絵: 正方形の中で、(cx, cy) (0 か 1 = どの角か。テクスチャ座標なので y=1 が上) を中心とする四分円の外側だけ半透明の黒。
        /// 縁は 1px でなだらかにする。GUI は絵の上辺を矩形の上に描くので、左上の角に置く絵は中心を右下 (1, 0) にする。
        /// </summary>
        private static Texture2D CornerTexture(int cx, int cy)
        {
            const int S = CORNER_PX;
            var t = new Texture2D(S, S, TextureFormat.ARGB32, false);
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            float ccx = cx * S, ccy = cy * S;
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - ccx) * (x + 0.5f - ccx) + (y + 0.5f - ccy) * (y + 0.5f - ccy));
                    float outside = Mathf.Clamp01(d - S + 0.5f);
                    t.SetPixel(x, y, new Color(0f, 0f, 0f, 0.65f * outside));
                }
            }
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        private static GUIStyle s_statusNoWrap;

        /// <summary>状態メッセージを折り返さず 1 行で出す書式 (色と大きさは s_status_style に毎回合わせる)。</summary>
        private static GUIStyle StatusStyleNoWrap()
        {
            if (s_statusNoWrap == null) { s_statusNoWrap = new GUIStyle(s_status_style); s_statusNoWrap.wordWrap = false; s_statusNoWrap.clipping = TextClipping.Overflow; }
            s_statusNoWrap.fontSize = s_status_style.fontSize;
            s_statusNoWrap.normal.textColor = s_status_style.normal.textColor;
            return s_statusNoWrap;
        }

        private static GUIStyle s_footerFit;
        private static int s_footerFitW = -1, s_footerFitBase = -1;

        /// <summary>操作説明の書式。幅 width に 1 行で収まる大きさまで文字を小さくする (下限 10)。幅と基準の大きさが変わった時だけ作り直す。</summary>
        private static GUIStyle FitFooterStyle(string text, float width, int fontSize)
        {
            int w = Mathf.RoundToInt(width);
            if (s_footerFit != null && s_footerFitW == w && s_footerFitBase == fontSize) return s_footerFit;
            s_footerFitW = w; s_footerFitBase = fontSize;
            GUIStyle st = new GUIStyle(s_footer);
            st.wordWrap = false;
            st.clipping = TextClipping.Overflow;
            GUIContent gc = new GUIContent(text);
            for (int size = s_footer.fontSize; size >= 10; size--)
            {
                st.fontSize = size;
                if (st.CalcSize(gc).x <= width) break;
            }
            s_footerFit = st;
            return st;
        }

        /// <summary>
        /// 金のプレートの上の文字。本体のメニュー文字と同じフォント (GS.m_font[0] = Fonts/UDTypos512。とじる の焼き込み文字と同じ書体) で描く。
        /// 固定サイズのビットマップフォントで GUIStyle.fontSize が効かないので、ArmorInfo と同じく GUI.matrix で目的の大きさに縮める。
        /// フォントが取れなければ OS フォント (s_btnText)。
        /// </summary>
        private static void DrawPlateText(Rect r, string text, int px)
        {
            Font f = null;
            try { Font[] fonts = GS.m_font; if (fonts != null && fonts.Length > 0) f = fonts[0]; } catch { }
            if (f == null || f.fontSize <= 0) { GUI.Label(r, text, s_btnText); return; }
            if (s_plateText == null || s_plateText.font != f)
            {
                s_plateText = new GUIStyle(s_btnText) { font = f, fontSize = 0, fontStyle = FontStyle.Normal, clipping = TextClipping.Overflow, wordWrap = false };
                s_plateText.normal.textColor = s_btnText.normal.textColor;
                // とじる の焼き込み文字は、文字の周りの地が薄い黄色ににじんでいる (地 239,193,87 → 際で 247,220,106)。
                // 同じ色を 8 方向にずらして薄く重ね、その上に本文字を描いてにじみを作る。
                s_plateGlow = new GUIStyle(s_plateText);
                s_plateGlow.normal.textColor = new Color(1f, 0.93f, 0.55f, 0.07f);   // ごく薄く (8 方向 x 2 重で重なるので、1 枚は 7% で十分)
            }
            float sc = px / (float)f.fontSize;
            Matrix4x4 oldM = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(sc, sc, 1f)) * oldM;
            Rect sr = new Rect(r.x / sc, r.y / sc, r.width / sc, r.height / sc);
            for (int ring = 1; ring <= 2; ring++)
            {
                float d = px * 0.045f * ring / sc;          // 画面上で文字の高さの 4.5% と 9%
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI / 4f;
                    GUI.Label(new Rect(sr.x + Mathf.Cos(a) * d, sr.y + Mathf.Sin(a) * d, sr.width, sr.height), text, s_plateGlow);
                }
            }
            GUI.Label(sr, text, s_plateText);
            GUI.matrix = oldM;
        }
        private static GUIStyle s_plateText, s_plateGlow;

        /// <summary>手のカーソル (無ければ「>」)。tipX = 指先の x、centerY = 行の中央。</summary>
        private static void DrawHand(float tipX, float centerY, float h)
        {
            if (UiParts.DrawCursor(tipX, centerY, h)) return;
            UiParts.Label(new Rect(tipX - h, centerY - h / 2f, h, h), ">", s_lineSel);
        }

        // カーソル行が見える位置まで Scroll を動かす。カーソルは説明行 (Selectable = false) に乗れないので、
        // カーソル行に隣接する説明行も一緒に見せる (先頭の説明行が、一度下に送ると二度と戻ってこない問題の対策)
        private static void ScrollToCursor(Page p, int visible)
        {
            int cur = Mathf.Min(p.Cursor, p.RowCount - 1); // とじる ボタンは行ではない
            int top = cur, bottom = cur;
            while (top > 0 && !p.Items[top - 1].Selectable) top--;
            while (bottom + 1 < p.RowCount && !p.Items[bottom + 1].Selectable) bottom++;
            if (bottom - top + 1 > visible) { top = cur; bottom = cur; }
            if (top < p.Scroll) p.Scroll = top;
            if (bottom > p.Scroll + visible - 1) p.Scroll = bottom - visible + 1;
            p.Scroll = Mathf.Clamp(p.Scroll, 0, Mathf.Max(0, p.RowCount - visible));
        }

        private static GUIStyle RightAligned(GUIStyle baseStyle)
        {
            if (baseStyle == s_lineSel)
            {
                if (s_rightSelCache == null) { s_rightSelCache = new GUIStyle(baseStyle); s_rightSelCache.alignment = TextAnchor.MiddleRight; }
                return s_rightSelCache;
            }
            if (baseStyle == s_lineChanged)
            {
                if (s_rightChangedCache == null) { s_rightChangedCache = new GUIStyle(baseStyle); s_rightChangedCache.alignment = TextAnchor.MiddleRight; }
                return s_rightChangedCache;
            }
            if (baseStyle == s_lineDim)
            {
                if (s_rightDimCache == null) { s_rightDimCache = new GUIStyle(baseStyle); s_rightDimCache.alignment = TextAnchor.MiddleRight; }
                return s_rightDimCache;
            }
            if (s_rightCache == null) { s_rightCache = new GUIStyle(baseStyle); s_rightCache.alignment = TextAnchor.MiddleRight; }
            return s_rightCache;
        }

        private static GUIStyle s_centerSection, s_centerLine, s_centerSel;

        private static GUIStyle Centered(GUIStyle baseStyle)
        {
            if (baseStyle == s_section)
            {
                if (s_centerSection == null) { s_centerSection = new GUIStyle(baseStyle); s_centerSection.alignment = TextAnchor.MiddleCenter; }
                return s_centerSection;
            }
            if (baseStyle == s_lineSel)
            {
                if (s_centerSel == null) { s_centerSel = new GUIStyle(baseStyle); s_centerSel.alignment = TextAnchor.MiddleCenter; }
                return s_centerSel;
            }
            if (s_centerLine == null) { s_centerLine = new GUIStyle(baseStyle); s_centerLine.alignment = TextAnchor.MiddleCenter; }
            return s_centerLine;
        }

        // 他の MOD 画面 (タイトルに戻る確認ダイアログ) でも同じ見た目を使う
        internal static GUIStyle BoxStyle { get { return s_box; } }
        internal static GUIStyle LineStyle { get { return s_line; } }
        internal static GUIStyle LineSelStyle { get { return s_lineSel; } }
        internal static GUIStyle TitleStyle { get { return s_title; } }
        internal static GUIStyle FooterStyle { get { return s_footer; } }

        private static bool s_paperColors;

        /// <summary>
        /// 文字色を背景に合わせる。paper=true は羊皮紙の壁紙の上 (本体メニューと同じ黒系、影なし)、
        /// false は青灰色の枠や単色の箱の上 (白 + 黒い影)。
        /// </summary>
        internal static void EnsureTextColors(bool paper)
        {
            if (s_paperColors == paper && s_line != null) return;
            s_paperColors = paper;
            if (paper)
            {
                s_line.normal.textColor = new Color(0.08f, 0.06f, 0.04f);
                s_lineSel.normal.textColor = new Color(0f, 0f, 0f);
                s_lineDim.normal.textColor = UiParts.DimPaper;   // 薄い灰色は読みにくい (ユーザー 2026-10-20、2026-10-24 にさらに濃く) → 共通の色
                s_section.normal.textColor = s_lineSel.normal.textColor;
                s_title.normal.textColor = s_lineSel.normal.textColor;
                s_footer.normal.textColor = new Color(0.2f, 0.16f, 0.13f);
                s_status_style.normal.textColor = new Color(0.5f, 0.05f, 0.02f);
                s_statusRight.normal.textColor = new Color(0.6f, 0.04f, 0.02f);   // 防具の詳細窓で特性のある種類に使う赤
                s_lineChanged.normal.textColor = s_statusRight.normal.textColor;
            }
            else
            {
                s_line.normal.textColor = new Color(0.97f, 0.97f, 0.97f);
                s_lineSel.normal.textColor = new Color(1f, 0.92f, 0.45f);
                s_lineDim.normal.textColor = new Color(0.78f, 0.8f, 0.85f);
                s_section.normal.textColor = s_line.normal.textColor;
                s_title.normal.textColor = s_line.normal.textColor;
                s_footer.normal.textColor = new Color(0.85f, 0.87f, 0.9f);
                s_status_style.normal.textColor = new Color(1f, 0.92f, 0.45f);
                s_statusRight.normal.textColor = new Color(1f, 0.45f, 0.35f);
                s_lineChanged.normal.textColor = s_statusRight.normal.textColor;
            }
            // 右寄せ・中央寄せの派生スタイルは色を写しているので作り直す
            s_rightCache = null; s_rightSelCache = null; s_rightDimCache = null; s_rightChangedCache = null;
            s_footerFit = null;
            s_centerSection = null; s_centerLine = null; s_centerSel = null;
        }

        internal static void EnsureStyles(int fontSize)
        {
            if (s_fontSizeBuilt == fontSize && s_box != null) return;
            s_fontSizeBuilt = fontSize;
            s_paperColors = false;
            s_rightCache = null; s_rightSelCache = null; s_rightDimCache = null; s_rightChangedCache = null;
            s_centerSection = null; s_centerLine = null; s_centerSel = null;
            UiParts.ResetStyles();

            if (s_font == null)
            {
                // 日本語が出る OS フォントを優先。無ければ IMGUI 既定 (Arial、Windows なら日本語もフォールバックで出る)
                try { s_font = Font.CreateDynamicFontFromOSFont(new[] { "Meiryo UI", "Yu Gothic UI", "MS UI Gothic" }, fontSize); }
                catch (Exception e) { Plugin.Log.LogWarning("[Config] OS font unavailable: " + e.Message); }
            }

            // 枠の部品が読めない時の代用 (単色の箱)
            s_box = new GUIStyle(GUI.skin.box);
            s_box.normal.background = SolidTexture(new Color(0.05f, 0.05f, 0.12f, 0.92f));

            // 文字は本体のダイアログと同じ白 + 黒い影 (UiParts.Label)。青灰色の枠の上で読めるように
            s_line = new GUIStyle(GUI.skin.label);
            s_line.fontSize = fontSize;
            s_line.alignment = TextAnchor.MiddleLeft;
            s_line.normal.textColor = new Color(0.97f, 0.97f, 0.97f);
            if (s_font != null) s_line.font = s_font;

            s_lineSel = new GUIStyle(s_line);
            s_lineSel.normal.textColor = new Color(1f, 0.92f, 0.45f);

            s_lineDim = new GUIStyle(s_line);
            s_lineDim.normal.textColor = new Color(0.78f, 0.8f, 0.85f);

            s_section = new GUIStyle(s_line);
            s_section.fontStyle = FontStyle.Bold;

            s_title = new GUIStyle(s_section);
            s_title.fontSize = fontSize + 4;

            s_footer = new GUIStyle(s_line);
            s_footer.fontSize = Mathf.Max(10, fontSize - 4);
            s_footer.normal.textColor = new Color(0.85f, 0.87f, 0.9f);
            s_footer.wordWrap = true;

            s_status_style = new GUIStyle(s_line);
            s_status_style.fontSize = Mathf.Max(10, fontSize - 2);
            s_status_style.normal.textColor = new Color(1f, 0.92f, 0.45f);

            // 右上の「押しているボタン」(右寄せ。色は EnsureTextColors で、羊皮紙の上では赤)
            s_statusRight = new GUIStyle(s_status_style);
            s_statusRight.alignment = TextAnchor.MiddleRight;
            s_lineChanged = new GUIStyle(s_line);   // 色は EnsureTextColors で入れる

            // 金のプレートの上の文字 (とじる の焼き込み文字と同じ濃い茶色、太字、中央)
            s_btnText = new GUIStyle(s_section);
            s_btnText.alignment = TextAnchor.MiddleCenter;
            s_btnText.normal.textColor = new Color(0.07f, 0.04f, 0.02f);
        }

        private static Texture2D SolidTexture(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }
    }
}
