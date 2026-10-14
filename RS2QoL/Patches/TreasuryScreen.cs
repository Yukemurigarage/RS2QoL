using System;
using BepInEx.Configuration;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>フィールドで LB / RB を押した時の役目 ([Economy] FieldLB / FieldRB)。</summary>
    public enum FieldLRAction
    {
        None,       // 何もしない (本体どおり。フィールドでは LB / RB に役目が無い)
        Treasury,   // どこでも大臣の画面を開く
        Withdraw,   // 国庫から所持金へ引き出す (画面を開かずに即時)
    }

    /// <summary>
    /// どこでも大臣の画面 (PadFunction.Treasury。既定はフィールドの LB。[Economy] FieldLB / FieldRB 参照)。
    ///
    /// 本体の倉庫画面の壁紙 (灰色の石の紙、menu/wallpaper/wallpaper_05.png) を敷いた専用の窓に、大臣の絵と、今の国庫・所持金、
    /// 国庫に残す額の変更・国庫から引き出す・国庫に預ける・とじる を置く (中身は設定画面「国庫」ページと同じ Wallet の処理)。
    /// 設定画面と同じ部品 (CBattleWindow の枠、手のカーソル、とじる ボタン、OS の日本語フォント) で描く。
    ///
    /// 大臣の絵は本体のフィールド NPC の絵そのもの: アバロン城 (map 24) の玉座の間 (7,13) の大臣 (msg 88) は maptbl.bin で絵の番号 115。
    /// 本体と同じ手順 (ImageEfx.init(4, 115) → Data.setObjPalette → storeImage) で 104x26 のシート (正面 / 背面 / 横 / 横の歩き、
    /// Core.people_chara_tbl と同じ並び) を組み、正面の 26x26 を Texture2D に写す。歩きアニメは本体と同じく正面コマと
    /// その左右反転を交互に出す (people_chara_tbl[4] = 正面を反転。足の出方が入れ替わる)。
    ///
    /// フィールドの LB / RB: フィールドでは LB / RB に本体の役目が無い (Core で 0x10 / 0x40 を見るのは会話送り・ポーズ解除の
    /// 「どのボタンでも」だけで、ポーズは MOD が Start 以外を遮っている) ので、歩ける時とポーズ中に限って別の役目を持たせる。
    /// メニュー・戦闘・会話中は本体どおり (ページ送り / 退却・防御 / 会話送り)。ボタン割り当て (PadFunction) の方は場面を問わず効く。
    ///
    /// 開けるのはフィールドで歩ける時とポーズ中だけ (戦闘・メニュー・店・会話中は所持金を外から変えると画面側の値とずれる)。
    /// 歩ける時に押されたら QuitGame と同じ手順で先に本体のポーズ (Start を 2 論理フレーム流す) をかけ、かかってから開く。
    /// 閉じてもポーズは残す (ユーザーの要望。Start で解除する)。開いている間はゲームに入力を渡さない (ConfigMenu.Tick が Tick に回す)。
    /// 閉じるのは とじる・キャンセル・Esc (開いたボタンの押し直しでは閉じない: LB / RB はページ送り)。
    ///
    /// ページ: 1 = どこでも大臣 (国庫)、2 = どこでも体重計 (仲間ごとの絵・名前・防具の重さ・素早さ → 戦闘での素早さ。PartyWeight 参照)。
    /// 開いている間の LB / RB (物理ボタン。割り当ては見ない) で送る。
    /// </summary>
    internal static class TreasuryScreen
    {
        public static ConfigEntry<FieldLRAction> FieldLB, FieldRB;

        private enum Phase { Idle, PauseFirst, Open }
        private const int KEY_PAUSE = 0x100;
        private const int INJECT_FRAMES = 2;      // Start を立てる論理フレーム数 (押下エッジが 1 回立てばよい)
        private const int PAUSE_TIMEOUT = 60;     // ポーズの開始を待つ上限 (論理フレーム)
        private const int DIR_LEFT = 0x10000, DIR_UP = 0x20000, DIR_RIGHT = 0x40000, DIR_DOWN = 0x80000;
        private const int BTN_OK = 0x100000, BTN_CANCEL = 0x400000;
        private const float REPEAT_FIRST = 0.4f, REPEAT_NEXT = 0.1f;

        // 行 (カーソルが乗るのは ROW_FLOOR 以降。ROW_CLOSE は右下の とじる ボタン)
        private const int ROW_TREASURY = 0, ROW_MONEY = 1, ROW_FLOOR = 2, ROW_WITHDRAW = 3, ROW_DEPOSIT = 4, ROW_CLOSE = 5;
        private const int ROWS = 5;               // 枠の中に描く行数 (とじる は除く)
        private const int PAGE_TREASURY = 0, PAGE_WEIGHT = 1, PAGES = 2;
        private const float WEIGHT_ROW = 1.5f;    // 体重計のページの 1 人分の最低の高さ (行の高さの倍数。実際は WeightRowH = 兵士と同じ大きさの絵が入る高さ)
        private static readonly string[] PAGE_TITLES = { "どこでも大臣", "どこでも体重計" };
        private static int s_page = PAGE_TREASURY;
        private static int s_cursorTreasury = ROW_WITHDRAW;   // 体重計のページから戻った時に戻すカーソル
        private static bool s_prevLB, s_prevRB;   // 開いている間のページ送り (物理 LB / RB の押下エッジ)

        private static Phase s_phase = Phase.Idle;
        private static int s_phaseFrames;
        private static int s_injected;            // PauseFirst で Start を流した回数
        private static bool s_prevBtn;
        private static int s_lastBtnFrame = -1;
        private static int s_prevKey;
        private static int s_cursor = ROW_WITHDRAW;
        private static int s_heldDir;
        private static float s_nextRepeat;
        private static string s_status = "";
        private static int s_lastEscFrame = -1;

        private static GUIStyle s_box, s_title, s_line, s_lineSel, s_lineDim, s_lineRed, s_status_style, s_footer, s_right, s_rightSel, s_rightDim, s_rightRed, s_center;
        private static Font s_font;
        private static int s_fontSizeBuilt = -1;

        public static void Bind(ConfigFile config)
        {
            FieldLB = config.Bind("Economy", "FieldLB", FieldLRAction.Treasury,
                "フィールドで歩ける時とポーズ中に LB を押した時の役目 (フィールドでは本体は LB / RB を使わない)。None = 何もしない / Treasury = どこでも大臣の画面を開く / Withdraw = 国庫から所持金へ引き出す (画面を開かずに即時)。メニュー・戦闘では本体どおり");
            FieldRB = config.Bind("Economy", "FieldRB", FieldLRAction.Withdraw,
                "フィールドで歩ける時とポーズ中に RB を押した時の役目。None / Treasury / Withdraw (FieldLB と同じ)");
        }

        /// <summary>ポーズ開始待ちか、画面を開いている (ConfigMenu.Tick が Tick に回し、入力を奪う)。</summary>
        public static bool Active { get { return s_phase != Phase.Idle; } }
        public static bool IsOpen { get { return s_phase == Phase.Open; } }

        private static void SetPhase(Phase p)
        {
            s_phase = p;
            s_phaseFrames = 0;
            s_injected = 0;
        }

        // ---------------------------------------------------------------- フィールドの LB / RB

        /// <summary>フィールドの LB / RB が別の役目を持つ場面か (歩ける時かポーズ中。会話・メニュー・戦闘では本体の役目のまま)。</summary>
        private static bool FieldLROn
        {
            get { return Wallet.InGame() && (FieldPause.IsPaused() || Wallet.IsFieldFree(Main.core)); }
        }

        /// <summary>
        /// 役目 action (フィールドの LB / RB) か、ボタン割り当ての機能 f のどちらかが押されているか。
        /// Wallet.HandleButton (引き出し) と HandleButton (画面) から。
        /// </summary>
        public static bool IsActionDown(FieldLRAction action, PadFunction f)
        {
            if (PadMap.IsFunctionDown(f)) return true;
            if (FieldLB == null || !FieldLROn) return false;
            if (FieldLB.Value == action && PadMap.IsPhysicalDown(PadMap.PHYS_LB)) return true;
            if (FieldRB.Value == action && PadMap.IsPhysicalDown(PadMap.PHYS_RB)) return true;
            return false;
        }

        /// <summary>設定画面用: 役目 action に使えるボタンの表示 (フィールドの LB / RB と、ボタン割り当ての f)。</summary>
        public static string ButtonsText(FieldLRAction action, string mapped)
        {
            string s = "";
            if (FieldLB != null && FieldLB.Value == action) s = "LB";
            if (FieldRB != null && FieldRB.Value == action) s += (s.Length > 0 ? "・" : "") + "RB";
            if (s.Length > 0) s += " (フィールド)";
            if (mapped != "なし") s += (s.Length > 0 ? "・" : "") + mapped;
            return s.Length > 0 ? s : "なし";
        }

        public static string ActionLabel(FieldLRAction a)
        {
            switch (a)
            {
                case FieldLRAction.Treasury: return "どこでも大臣の画面";
                case FieldLRAction.Withdraw: return "国庫から引き出す";
                default: return "なし";
            }
        }

        /// <summary>設定画面のボタン割り当て用: 物理ボタン LB / RB の行に、フィールドでの役目を併記する文 (無ければ空)。</summary>
        public static string FieldRoleSuffix(int physical)
        {
            ConfigEntry<FieldLRAction> e = physical == PadMap.PHYS_LB ? FieldLB : physical == PadMap.PHYS_RB ? FieldRB : null;
            if (e == null || e.Value == FieldLRAction.None) return "";
            return " / フィールド: " + ActionLabel(e.Value);
        }

        public static void CycleAction(ConfigEntry<FieldLRAction> e, int delta)
        {
            int n = 3;
            e.Value = (FieldLRAction)(((int)e.Value + delta + n) % n);
        }

        // ---------------------------------------------------------------- 開閉

        /// <summary>
        /// 割り当てボタンの押下エッジ。ConfigMenu.Tick から毎論理フレーム呼ぶ
        /// (倍速中は同じ Unity フレームで複数回呼ばれるのでフレーム番号で 1 回に絞る)。
        /// </summary>
        public static void HandleButton()
        {
            // 開いている間は開いたボタンでは閉じない (LB / RB はページ送り。閉じるのは とじる・キャンセル・Esc)。押下の状態だけ追う
            bool down = IsOpen ? (PadMap.IsFunctionDown(PadFunction.Treasury) || FieldDown(FieldLRAction.Treasury)) : IsActionDown(FieldLRAction.Treasury, PadFunction.Treasury);
            bool edge = down && !s_prevBtn && Time.frameCount != s_lastBtnFrame;
            s_prevBtn = down;
            if (!edge) return;
            s_lastBtnFrame = Time.frameCount;
            if (IsOpen) return;
            if (Active || ConfigMenu.IsOpen || SoftReset.Active || QuitGame.Active || QuitGame.DialogOpen) return;
            if (!Wallet.InGame())
            {
                Plugin.Log.LogInfo("[Economy] treasury screen button ignored (not in game) " + FieldPause.StateText());
                return;
            }
            Core c = Main.core;
            if (FieldPause.IsPaused())
            {
                OpenNow();
                return;
            }
            if (Wallet.IsFieldFree(c))
            {
                Plugin.Log.LogInfo("[Economy] treasury screen: pausing the field first");
                SetPhase(Phase.PauseFirst);
                return;
            }
            // ここに来るのはボタン割り当ての機能だけ (フィールドの LB / RB は歩ける時とポーズ中しか数えない)
            Plugin.Log.LogInfo("[Economy] treasury screen button ignored (not on the field) " + FieldPause.StateText());
            UiParts.SeCancel();
            Wallet.ShowNotice("どこでも大臣はフィールドで (ポーズ中でも可)");
        }

        /// <summary>フィールドの LB / RB のうち、役目 action のものが (場面を問わず) 押されているか。開いている間の閉じる判定用。</summary>
        private static bool FieldDown(FieldLRAction action)
        {
            if (FieldLB == null) return false;
            return (FieldLB.Value == action && PadMap.IsPhysicalDown(PadMap.PHYS_LB)) || (FieldRB.Value == action && PadMap.IsPhysicalDown(PadMap.PHYS_RB));
        }

        private static void OpenNow()
        {
            SetPhase(Phase.Open);
            s_status = "";
            s_heldDir = 0;
            s_prevKey = -1;                       // 開いたフレームの押しっぱなしをエッジに見せない
            s_cursor = ROW_WITHDRAW;
            s_cursorTreasury = ROW_WITHDRAW;
            s_page = PAGE_TREASURY;
            s_prevLB = PageBackDown();   // 開いた LB の押しっぱなしをページ送りに見せない
            s_prevRB = PageNextDown();
            s_animStart = Time.realtimeSinceStartup;
            for (int i = 0; i < PartyWeight.Count(); i++) PartyWeight.MemberFrame(i);   // 絵は開いた時に組む (描画中に組まない)
            UiParts.SeOk();                       // MOD の窓は開く時に決定音、閉じる時にカーソル音 (設定画面・ソフトリセットと同じ。ユーザー要望 2026-10-13)
            Plugin.Log.LogInfo("[Economy] treasury screen opened (wallet " + Wallet.Money + ", treasury " + Wallet.Treasury + ")");
        }

        /// <summary>閉じる (どの閉じ方でもカーソル音)。</summary>
        private static void Close()
        {
            if (!IsOpen) return;
            UiParts.SeCursor();
            SetPhase(Phase.Idle);
            ConfigMenu.GuardUntilReleased();      // 閉じた直後、全ボタンを離すまでゲームに渡さない (決定の押しっぱなしでポーズが解けないように)
            Plugin.Log.LogInfo("[Economy] treasury screen closed");
        }

        /// <summary>Active の間、ConfigMenu.Tick から呼ばれる。戻り値がゲームに渡る (Start を流す時以外は 0)。</summary>
        public static int Tick(int key)
        {
            s_phaseFrames++;
            switch (s_phase)
            {
                case Phase.PauseFirst:
                    if (FieldPause.IsPaused())
                    {
                        OpenNow();
                        return 0;
                    }
                    if (s_phaseFrames > PAUSE_TIMEOUT)
                    {
                        // ポーズがかからなくても開く (開いている間の入力は 0 を返して止める。NPC は動く)
                        Plugin.Log.LogWarning("[Economy] treasury screen: auto pause did not start " + FieldPause.StateText());
                        OpenNow();
                        return 0;
                    }
                    // 歩いている途中 (マス移動中) なら、入力を止めて止まるのを待ってから Start を流す
                    if (s_injected == 0 && !SoftReset.IsFieldIdle(Main.core)) return 0;
                    s_injected++;
                    return s_injected <= INJECT_FRAMES ? KEY_PAUSE : 0;

                case Phase.Open:
                    TickOpen(key);
                    return 0;
            }
            return key;
        }

        private static void TickOpen(int key)
        {
            int pressed = s_prevKey < 0 ? 0 : key & ~s_prevKey;
            s_prevKey = key;
            bool escEdge = Time.frameCount != s_lastEscFrame && (Input.GetKeyDown(KeyCode.Escape) || Interp60.TakeKeyDown(KeyCode.Escape));
            s_lastEscFrame = Time.frameCount;
            if ((pressed & BTN_CANCEL) != 0 || escEdge)
            {
                Close();
                return;
            }
            // ページ送り: パッドは物理の LB (戻す) / RB (送る) で割り当ては見ない (フィールドの LB / RB と同じ生の押下)。
            // キーボードは物理ボタンが無いので、L / R の機能に割り当てたキー (既定 Q / E) を見る
            bool lb = PageBackDown(), rb = PageNextDown();
            if (lb && !s_prevLB) TurnPage(-1);
            else if (rb && !s_prevRB) TurnPage(+1);
            s_prevLB = lb; s_prevRB = rb;
            // 方向: 押した瞬間 + 長押しリピート (残す額の左右を押し続けて送れるように)
            int dir = key & (DIR_LEFT | DIR_UP | DIR_RIGHT | DIR_DOWN);
            float now = Time.realtimeSinceStartup;
            if (dir == 0) s_heldDir = 0;
            else if (dir != s_heldDir) { s_heldDir = dir; s_nextRepeat = now + REPEAT_FIRST; HandleDir(dir); }
            else if (now >= s_nextRepeat) { s_nextRepeat = now + REPEAT_NEXT; HandleDir(dir); }

            if ((pressed & BTN_OK) != 0) Activate(s_cursor);
        }

        /// <summary>ページを戻す入力 (物理 LB か、キーボードの L 機能のキー)。</summary>
        private static bool PageBackDown() { return PadMap.IsPhysicalDown(PadMap.PHYS_LB) || KeyMap.IsDown(PadFunction.L); }
        /// <summary>ページを送る入力 (物理 RB か、キーボードの R 機能のキー)。</summary>
        private static bool PageNextDown() { return PadMap.IsPhysicalDown(PadMap.PHYS_RB) || KeyMap.IsDown(PadFunction.R); }

        private static void HandleDir(int dir)
        {
            if ((dir & DIR_UP) != 0) MoveCursor(-1);
            else if ((dir & DIR_DOWN) != 0) MoveCursor(+1);
            else if (s_cursor == ROW_FLOOR && (dir & (DIR_LEFT | DIR_RIGHT)) != 0)
            {
                UiParts.SeCursor();
                Wallet.CycleFloor((dir & DIR_LEFT) != 0 ? -1 : +1);
            }
        }

        private static void MoveCursor(int delta)
        {
            if (s_page != PAGE_TREASURY) return;   // 体重計のページは とじる だけ
            int c = s_cursor + delta;
            if (c < ROW_FLOOR) c = ROW_CLOSE; else if (c > ROW_CLOSE) c = ROW_FLOOR;
            s_cursor = c;
            UiParts.SeCursor();
        }

        /// <summary>ページを delta だけ送る (端で止まる。LB / RB と、見出しの矢印のクリック)。</summary>
        private static void TurnPage(int delta)
        {
            int p = Mathf.Clamp(s_page + delta, 0, PAGES - 1);
            if (p == s_page) return;
            if (s_page == PAGE_TREASURY) s_cursorTreasury = s_cursor;
            s_page = p;
            s_cursor = p == PAGE_TREASURY ? s_cursorTreasury : ROW_CLOSE;
            s_status = "";
            UiParts.SeCursor();
            Plugin.Log.LogInfo("[Economy] treasury screen page " + (p + 1) + " (" + PAGE_TITLES[p] + ")");
        }

        private static void Activate(int row)
        {
            bool ok;
            switch (row)
            {
                case ROW_FLOOR:
                    UiParts.SeCursor();
                    Wallet.CycleFloor(+1);
                    break;
                case ROW_WITHDRAW:
                    s_status = Wallet.Withdraw(out ok);
                    if (ok) UiParts.SeOk(); else UiParts.SeCancel();
                    break;
                case ROW_DEPOSIT:
                    s_status = Wallet.Deposit(out ok);
                    if (ok) UiParts.SeOk(); else UiParts.SeCancel();
                    break;
                case ROW_CLOSE:
                    Close();
                    break;
            }
        }

        // ---------------------------------------------------------------- 大臣の絵

        private const int MINISTER_OBJ = 115;     // アバロン城 (map 24) の玉座の間 (7,13) の大臣 (msg 88) の絵の番号 (maptbl.bin の obj)
        private const int FRAME = 26;             // 1 コマの大きさ (people_chara_tbl: 26x26、正面は x=0)
        private const float ANIM_SECONDS = 8f / 30f; // 歩きのコマ送り (本体の people_anime は 8 論理フレームごと)
        private static Texture2D s_minister;
        private static bool s_ministerTried;
        private static float s_animStart;

        /// <summary>本体の手順で大臣の絵 (シート 104x26) を組み、正面の 1 コマを Texture2D に写す。一度だけ。</summary>
        private static void EnsureMinister()
        {
            if (s_ministerTried) return;
            s_ministerTried = true;
            // 32 以上の番号はパレットも sd_tbl から決まる (pal 引数は使われない)。組み方は仲間の絵と共通 (PartyWeight.BuildFrontFrame)
            s_minister = PartyWeight.BuildFrontFrame(MINISTER_OBJ, 0, "rs2qol_minister");
            if (s_minister != null) Plugin.Log.LogInfo("[Economy] minister image ready");
        }

        private const int GUARD_OBJ = 114;        // アバロン城 (map 24) の倉庫の前 (43,10) の赤い兵士 (実機の [Weight] npc ログで確認)
        private static Texture2D s_guard;
        private static bool s_guardTried;

        /// <summary>体重計のページの見出し用: 倉庫の前の兵士の絵 (大臣と同じ組み方)。</summary>
        private static void DrawGuard(Rect r)
        {
            if (!s_guardTried)
            {
                s_guardTried = true;
                s_guard = PartyWeight.BuildFrontFrame(GUARD_OBJ, 0, "rs2qol_guard");
                if (s_guard != null) Plugin.Log.LogInfo("[Economy] guard image ready");
            }
            if (s_guard == null) { DrawMinister(r); return; }
            bool flip = ((int)((Time.realtimeSinceStartup - s_animStart) / ANIM_SECONDS) & 1) != 0;
            Color col = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(r, s_guard, flip ? new Rect(1f, 0f, -1f, 1f) : new Rect(0f, 0f, 1f, 1f), true);
            GUI.color = col;
        }

        /// <summary>大臣の絵があるか (Wallet.DrawNotice のレイアウト用)。</summary>
        public static bool HasMinister { get { EnsureMinister(); return s_minister != null; } }
        /// <summary>1 コマのドット数 (26)。</summary>
        public const int MinisterDots = FRAME;

        /// <summary>
        /// 大臣の絵の 1 ドットを何画面 px にするか (整数倍)。どこでも大臣の窓の見出し (行の高さ 2.4 行分) に収まる大きさで、
        /// 画面上部の結果の小窓でも同じ大きさにする (1600x900 で 3 倍 = 78 px)。
        /// </summary>
        public static int MinisterDot
        {
            get
            {
                float lineH = Mathf.Clamp(Screen.height / 22f, 18f, 48f);
                return Mathf.Max(1, Mathf.FloorToInt(Mathf.Round(lineH * 2.4f) / FRAME));
            }
        }

        /// <summary>大臣を描く (正面コマと、その左右反転を交互に = 本体の歩きアニメ)。Wallet.DrawNotice (画面上部の結果) からも。</summary>
        public static void DrawMinister(Rect r)
        {
            EnsureMinister();
            if (s_minister == null) return;
            bool flip = ((int)((Time.realtimeSinceStartup - s_animStart) / ANIM_SECONDS) & 1) != 0;
            Color col = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(r, s_minister, flip ? new Rect(1f, 0f, -1f, 1f) : new Rect(0f, 0f, 1f, 1f), true);
            GUI.color = col;
        }

        // ---------------------------------------------------------------- 描画 (Plugin.OnGUI から)

        public static void Draw()
        {
            if (!IsOpen) return;
            // 行の高さは画面の高さから決め、文字はそれに収まる大きさ (設定画面と同じ比率)
            float outer = Mathf.Round(UiParts.Unit * 8f);
            float lineH = Mathf.Clamp(Screen.height / 22f, 18f, 48f);
            int fontSize = Mathf.Clamp(Mathf.FloorToInt(lineH / 1.45f), 12, 32);
            lineH = Mathf.Min(lineH, fontSize * 1.6f);
            EnsureStyles(fontSize);
            float pad = lineH * 0.6f;
            // 幅は結果の文 (「国庫から 55,535 引き出した (財布 65,535、国庫 488,465)」≈ 35 文字) が 1 行に入る広さ。それでも入らなければ文字を小さくする
            float w = Mathf.Min(Screen.width - 2 * outer, fontSize * 40f);
            // 見出し (大臣の絵 2.4 行 + 0.2 行) + 項目 (国庫: ROWS 行 / 体重計: 列の見出し 1 行 + 人数 × WEIGHT_ROW 行) + 0.3 行
            //   + 結果の文 1 行 + ボタンの段 1.5 行 + 0.2 行 + 操作説明 1 行。高さはページごと (体重計の方が高い)
            float headH = Mathf.Round(lineH * 2.4f);
            // 高さはページで変えない (切り替えのたびに窓が上下しないように、高い方 = 体重計に合わせる)
            int members = PartyWeight.Count();
            // 体重計の 1 人分は仲間の絵 (見出しの兵士と同じ大きさ) が入る高さ。1600x900 で 82 px × 5 人 + 見出し → 窓は約 770 px (画面の 85%)
            float bodyH = Mathf.Max(ROWS * lineH, lineH + members * WeightRowH(lineH));
            float h = pad * 2 + headH + lineH * (0.2f + 0.3f + 1f + 1.5f + 0.2f + 1f) + bodyH;
            float x = Mathf.Round((Screen.width - w) / 2f);
            float y = Mathf.Round((Screen.height - h) / 2f);
            Rect win = new Rect(x, y, w, h);

            // 枠 (Esc の終了ダイアログと同じ) の中に本体の倉庫画面の灰色の壁紙を敷く。部品が無ければ単色の箱
            UiParts.Shadow = !UiParts.DrawPaperWindow(win, true);
            if (UiParts.Shadow) GUI.Box(win, GUIContent.none, s_box);
            float cy = y + pad;
            float cx = x + pad, cw = w - 2 * pad;

            // 見出し: 左に大臣の絵 (ドット絵なので整数倍に丸める)、その右にタイトル
            int dot = MinisterDot;
            float spr = dot * FRAME;
            Rect sprR = new Rect(cx, cy + headH - spr - 10f, spr, spr);    // 足元を見出しの下端に合わせる (10 px だけ上に: 下端に付くと窮屈に見える)
            if (s_page == PAGE_WEIGHT) DrawGuard(sprR); else DrawMinister(sprR);
            float titleX = s_minister != null ? cx + spr + fontSize * 0.5f : cx;
            UiParts.Label(new Rect(titleX, cy, cw - (titleX - cx), headH), PAGE_TITLES[s_page], s_title);
            // 見出しの右端にページ: [L] 1 / 2 [R] (設定画面と同じ本体の L / R ボタンの絵。クリックでも送れる)
            {
                float arrowH = Mathf.Round(lineH * 1.2f);
                float arrowW = Mathf.Round(arrowH * UiParts.ArrowAspect);
                string pageText = (s_page + 1) + " / " + PAGES;
                float pageW = s_center.CalcSize(new GUIContent(pageText)).x + fontSize * 0.8f;
                float ay = cy + Mathf.Round((headH - arrowH) / 2f);
                Rect rR = new Rect(cx + cw - arrowW, ay, arrowW, arrowH);
                Rect tR = new Rect(rR.x - pageW, cy, pageW, headH);
                Rect lR = new Rect(tR.x - arrowW, ay, arrowW, arrowH);
                if (!UiParts.DrawArrow(lR, true)) UiParts.Label(lR, "◀", s_center);
                if (!UiParts.DrawArrow(rR, false)) UiParts.Label(rR, "▶", s_center);
                UiParts.Label(tR, pageText, s_center);
                if (UiParts.ClickArea(lR)) TurnPage(-1);
                if (UiParts.ClickArea(rR)) TurnPage(+1);
            }
            cy += headH + lineH * 0.2f;

            float curW = lineH * UiParts.CursorAspect;
            float textX = cx + curW + fontSize * 0.3f;
            float textW = cw - (textX - cx);
            bool inGame = Wallet.InGame();
            int withdrawable = inGame ? Wallet.WithdrawableAmount() : 0;
            if (s_page == PAGE_WEIGHT)
            {
                cy = DrawWeightPage(cx, cy, cw, lineH, fontSize, members);
            }
            else
            for (int i = 0; i < ROWS; i++)
            {
                Rect r = new Rect(textX, cy, textW, lineH);
                bool sel = i == s_cursor;
                bool selectable = i >= ROW_FLOOR;
                GUIStyle st = sel ? s_lineSel : (selectable ? s_line : s_lineDim);
                GUIStyle rt = sel ? s_rightSel : (selectable ? s_right : s_rightDim);
                if (sel) DrawHand(cx + curW, cy + lineH / 2f, lineH);
                string label, value = null;
                switch (i)
                {
                    case ROW_TREASURY: label = "今の国庫"; value = Wallet.Treasury.ToString("N0") + " クラウン"; break;
                    case ROW_MONEY: label = "今の所持金"; value = Wallet.MoneyLabel(); break;
                    case ROW_FLOOR: label = "引き出す時に国庫に残す額"; value = "< " + Wallet.FloorLabel() + " >"; if (Wallet.Floor != 100000) rt = s_rightRed; break;
                    case ROW_WITHDRAW: label = "国庫から引き出す (所持金を上限まで)"; value = withdrawable > 0 ? "+" + withdrawable.ToString("N0") : "-"; break;
                    default: label = "所持金を国庫に預ける (全額)"; value = inGame && Wallet.Money > 0 ? "-" + Wallet.Money.ToString("N0") : "-"; break;
                }
                UiParts.Label(r, label, st);
                if (value != null) UiParts.Label(r, value, rt);
                if (selectable && i != ROW_FLOOR && UiParts.ClickArea(r)) { s_cursor = i; Activate(i); }
                cy += lineH;
            }
            if (s_page != PAGE_WEIGHT) cy += bodyH - ROWS * lineH;   // 体重計のページと同じ位置に結果の文を置く
            cy += lineH * 0.3f;

            // 結果の文 (引き出した / 預けた / できない理由) は幅いっぱいの 1 行。入らなければ文字を小さくする (折り返して切れないように)
            string note = s_page == PAGE_WEIGHT ? "戦闘では防具の重さ ÷ 2 (切り捨て) を素早さから引く (武器の重さは効かない)。ステータス画面の小さい数字 (+N / －N) がその増減" : s_status;
            if (note.Length > 0) UiParts.Label(new Rect(cx, cy, cw, lineH), note, FitStyle(s_status_style, note, cw, ref s_statusFit, ref s_statusFitKey));
            cy += lineH;

            // 下部: ボタンの段 (右に とじる) と、枠の一番下の操作説明 (こちらも幅に収まる大きさで 1 行)
            float btnH = Mathf.Round(lineH * 1.5f);
            float footH = lineH;
            float footY = y + h - pad - footH;
            float btnY = Mathf.Round(footY - lineH * 0.2f - btnH);
            float btnW = Mathf.Round(btnH * UiParts.CloseButtonAspect);
            Rect btnR = new Rect(Mathf.Round(cx + cw - btnW), btnY, btnW, btnH);
            bool closeSel = s_cursor == ROW_CLOSE;
            if (!UiParts.DrawCloseButton(btnR)) UiParts.Label(btnR, "[ とじる ]", s_center);
            if (closeSel) DrawHand(btnR.x, btnR.y + btnR.height / 2f, lineH);
            if (UiParts.ClickArea(btnR)) Close();
            string foot = s_page == PAGE_WEIGHT
                ? "LB / RB: ページ  決定・キャンセル・Esc: 閉じる (ポーズは残る)"
                : "十字キー: 移動  左右: 変更  決定: 実行  LB / RB: ページ  キャンセル・Esc・とじる: 閉じる (ポーズは残る)";
            UiParts.Label(new Rect(cx, footY, cw, footH), foot, FitStyle(s_footer, foot, cw, ref s_footerFit, ref s_footerFitKey));
        }

        /// <summary>
        /// 体重計のページ: 列の見出し 1 行 + 仲間ごとに WEIGHT_ROW 行 (左に歩きの絵、名前、右に 重さ / 素早さ / 戦闘での素早さ)。
        /// 戻り値は描き終えた y。
        /// </summary>
        private static float DrawWeightPage(float cx, float cy, float cw, float lineH, int fontSize, int members)
        {
            float colW1 = fontSize * 4.5f, colW2 = fontSize * 5f, colW3 = fontSize * 8f;
            float x3 = cx + cw - colW3, x2 = x3 - colW2, x1 = x2 - colW1;
            float rowH = WeightRowH(lineH);
            int dot = MinisterDot;                 // 見出しの兵士 (大臣) と同じ大きさ
            float spr = dot * PartyWeightFrame;
            float nameX = cx + spr + fontSize * 0.5f;

            // 列の見出し
            UiParts.Label(new Rect(nameX, cy, x1 - nameX, lineH), "名前", s_lineDim);
            UiParts.Label(new Rect(x1, cy, colW1, lineH), "重さ", s_rightDim);
            UiParts.Label(new Rect(x2, cy, colW2, lineH), "素早さ", s_rightDim);
            UiParts.Label(new Rect(x3, cy, colW3, lineH), "戦闘での素早さ", s_rightDim);
            cy += lineH;

            for (int i = 0; i < members; i++)
            {
                int[] m = Work.memberdata[i];
                int weight = PartyWeight.Weight(m), agi = PartyWeight.MenuAgility(m), battle = PartyWeight.BattleAgility(m);
                Texture2D t = PartyWeight.MemberFrame(i);
                if (t != null) DrawFrame(new Rect(cx, cy + Mathf.Round((rowH - spr) / 2f), spr, spr), t);
                UiParts.Label(new Rect(nameX, cy, x1 - nameX, rowH), PartyWeight.MemberName(i), s_line);
                UiParts.Label(new Rect(x1, cy, colW1, rowH), weight.ToString(), s_right);
                UiParts.Label(new Rect(x2, cy, colW2, rowH), agi.ToString(), s_right);
                UiParts.Label(new Rect(x3, cy, colW3, rowH), "→ " + battle, battle < agi ? s_rightRed : s_right);
                cy += rowH;
            }
            return cy;
        }

        private const int PartyWeightFrame = FRAME;

        /// <summary>体重計の 1 人分の高さ: WEIGHT_ROW 行か、見出しの兵士と同じ大きさの絵 + 少しの余白の大きい方 (1600x900 で 82 px)。</summary>
        private static float WeightRowH(float lineH)
        {
            return Mathf.Max(Mathf.Round(lineH * WEIGHT_ROW), MinisterDot * FRAME + Mathf.Round(lineH * 0.1f));
        }

        /// <summary>仲間の正面の絵を大臣と同じ歩きアニメ (正面と左右反転を交互) で描く。</summary>
        private static void DrawFrame(Rect r, Texture2D t)
        {
            bool flip = ((int)((Time.realtimeSinceStartup - s_animStart) / ANIM_SECONDS) & 1) != 0;
            Color col = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(r, t, flip ? new Rect(1f, 0f, -1f, 1f) : new Rect(0f, 0f, 1f, 1f), true);
            GUI.color = col;
        }

        private static GUIStyle s_statusFit, s_footerFit;
        private static string s_statusFitKey, s_footerFitKey;

        /// <summary>
        /// 文字列 text が幅 width に 1 行で収まる大きさまで文字を小さくしたスタイル (下限 10)。文字列・幅・元の大きさが変わった時だけ作り直す。
        /// </summary>
        private static GUIStyle FitStyle(GUIStyle baseStyle, string text, float width, ref GUIStyle cache, ref string cacheKey)
        {
            string key = text + "|" + Mathf.RoundToInt(width) + "|" + baseStyle.fontSize;
            if (cache != null && cacheKey == key) return cache;
            cacheKey = key;
            GUIStyle st = new GUIStyle(baseStyle);
            st.wordWrap = false;
            st.clipping = TextClipping.Overflow;
            GUIContent gc = new GUIContent(text);
            for (int size = baseStyle.fontSize; size >= 10; size--)
            {
                st.fontSize = size;
                if (st.CalcSize(gc).x <= width) break;
            }
            cache = st;
            return st;
        }

        private static void DrawHand(float tipX, float centerY, float h)
        {
            if (!UiParts.DrawCursor(tipX, centerY, h)) UiParts.Label(new Rect(tipX - h, centerY - h / 2f, h, h), "▶", s_lineSel);
        }

        private static void EnsureStyles(int fontSize)
        {
            if (s_fontSizeBuilt == fontSize && s_line != null) return;
            s_fontSizeBuilt = fontSize;
            if (s_font == null)
            {
                try { s_font = Font.CreateDynamicFontFromOSFont(new[] { "Meiryo UI", "Yu Gothic UI", "MS UI Gothic" }, fontSize); }
                catch (Exception e) { Plugin.Log.LogWarning("[Economy] OS font unavailable: " + e.Message); }
            }
            s_box = new GUIStyle(GUI.skin.box);
            Texture2D t = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            t.SetPixel(0, 0, new Color(0.18f, 0.18f, 0.2f, 0.94f));
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            s_box.normal.background = t;

            // 灰色の石の紙の上なので、文字は黒に近い濃い色 (設定画面の羊皮紙と同じ考え方)
            s_line = new GUIStyle(GUI.skin.label);
            s_line.fontSize = fontSize;
            s_line.alignment = TextAnchor.MiddleLeft;
            s_line.normal.textColor = new Color(0.08f, 0.07f, 0.07f);
            if (s_font != null) s_line.font = s_font;
            s_lineSel = new GUIStyle(s_line);
            s_lineSel.normal.textColor = Color.black;
            s_lineDim = new GUIStyle(s_line);
            s_lineDim.normal.textColor = new Color(0.32f, 0.3f, 0.3f);
            s_lineRed = new GUIStyle(s_line);
            s_lineRed.normal.textColor = new Color(0.6f, 0.04f, 0.02f);
            s_title = new GUIStyle(s_line);
            s_title.fontStyle = FontStyle.Bold;
            s_title.fontSize = fontSize + 4;
            s_title.normal.textColor = Color.black;
            s_status_style = new GUIStyle(s_line);
            s_status_style.fontSize = Mathf.Max(10, fontSize - 2);
            s_status_style.normal.textColor = new Color(0.5f, 0.05f, 0.02f);
            s_status_style.wordWrap = false;
            s_statusFit = null; s_footerFit = null;
            s_footer = new GUIStyle(s_line);
            s_footer.fontSize = Mathf.Max(10, fontSize - 4);
            s_footer.normal.textColor = new Color(0.22f, 0.2f, 0.2f);
            s_right = new GUIStyle(s_line); s_right.alignment = TextAnchor.MiddleRight;
            s_rightSel = new GUIStyle(s_lineSel); s_rightSel.alignment = TextAnchor.MiddleRight;
            s_rightDim = new GUIStyle(s_lineDim); s_rightDim.alignment = TextAnchor.MiddleRight;
            s_rightRed = new GUIStyle(s_lineRed); s_rightRed.alignment = TextAnchor.MiddleRight;
            s_center = new GUIStyle(s_lineSel); s_center.alignment = TextAnchor.MiddleCenter;
            UiParts.ResetStyles();
        }
    }
}
