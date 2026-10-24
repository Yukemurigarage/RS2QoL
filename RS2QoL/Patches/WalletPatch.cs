using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 所持金 (財布) まわりの作り直し。
    ///
    /// 本体の仕組み:
    /// - 所持金 = Work.partydata[25]。上限 10,000 は C# 側の 4 か所 (宝箱 Core.get_takara_okane / get_okane_sep、店の売却 Menu.shop_uru_a2、
    ///   N 万クラウンの宝箱 Core.takara_gold_ivent と追憶の迷宮の同種 (Core.appnew_ivent)) にそれぞれ 10000 のリテラルで埋め込まれている。
    ///   セーブでは 2 バイト (Menu.save_adr_table2 の (2, 2, 25))、スクリプトの書き込み (kou2save_work → partydata_chktbl) も 16 bit なので、
    ///   上限は 65,535 までしか上げられない (それ以上はセーブで下位 16 bit だけ残り、壊れたように見える)。表示は 5 桁 (Menu.make_money_font) で足りる。
    /// - 国庫 = Work.partydata[418] (上限 16,777,215)。所持金を補充する唯一の手段は玉座の間の金庫番 (msg 88: 国庫 ≥ 99,840 かつ所持金 &lt; 1 万のとき、
    ///   所持金 = 1 万・国庫 −1 万の一律)。遠征中に金が切れるとアバロンまで戻る必要があり、これがスマホ移植でも SFC でも面倒の元。
    ///
    /// MOD:
    ///    N 万クラウンの宝箱 (本体: 国庫に N 万、所持金 = 1 万) は所持金を上限まで満たし、1 万を超えて満たした分は国庫行きから差し引く (総額は本体と同じ)。
    ///    その時のメッセージの金額も実際に動かした額に差し替える (Wallet_ten_put_Patch)。
    /// 2. どこでも金庫番: 国庫 → 所持金 (上限まで。国庫には [Economy] TreasuryFloor の額を残す) と、所持金 → 国庫 (全額)。
    ///    設定画面「国庫」ページの項目と、ボタン割り当ての「国庫から引き出す」(フィールドで歩ける時かポーズ中だけ) から。
    ///    スクリプトの msg 88 を遠隔で走らせるのではなく C# で直接動かす (msg 88 は最終決戦前の分岐 (msg 1253) などの早期終了を持つため)。
    ///    城の金庫番は本体どおり (1 万まで) 残る。
    /// </summary>
    internal static class Wallet
    {
        public static ConfigEntry<int> Max;          // 所持金の上限
        public static ConfigEntry<int> TreasuryFloor; // 引き出す時に国庫に残す額

        public const int WALLET = 25;                // Work.PARTY_party_money
        public const int TREASURY = 418;             // Work.PARTY_budget
        public const int VANILLA_MAX = 10000;
        public const int HARD_MAX = 65535;           // セーブが 2 バイト
        public const int TREASURY_MAX = 16777215;
        private const int MAX_STEP = 5000;
        private const int FLOOR_MAX = 1000000, FLOOR_STEP = 50000;
        private const float NOTICE_SECONDS = 3f;

        public static void Bind(ConfigFile config)
        {
            Max = config.Bind("Economy", "WalletMax", HARD_MAX,
                new ConfigDescription("所持金 (財布) の上限。本体は 10000。宝箱・店の売却で入る金はここまで持てる。セーブの形式上 65535 が最大。"
                    + "城の金庫番の資金補充は本体どおり 1 万まで (遠隔の引き出しは上限まで入る)",
                    new AcceptableValueRange<int>(VANILLA_MAX, HARD_MAX)));
            TreasuryFloor = config.Bind("Economy", "TreasuryFloor", 100000,
                new ConfigDescription("国庫から所持金へ引き出す時に、国庫に残しておく額 (施設の費用 30 万〜100 万を取り崩さないため)。"
                    + "0 なら全部引き出せる。本体の金庫番は国庫が 99,840 未満だと補充しないので、既定はそれに近い 100,000",
                    new AcceptableValueRange<int>(0, FLOOR_MAX)));
        }

        public static int Cap
        {
            get
            {
                int v = Max != null ? Max.Value : VANILLA_MAX;
                if (v < VANILLA_MAX) v = VANILLA_MAX;
                if (v > HARD_MAX) v = HARD_MAX;
                return v;
            }
        }

        public static int Floor { get { return TreasuryFloor != null ? Mathf.Clamp(TreasuryFloor.Value, 0, FLOOR_MAX) : 0; } }

        private static bool HasData { get { return Work.partydata != null && Work.partydata.Length > TREASURY; } }
        public static int Money { get { return HasData ? Work.partydata[WALLET] : 0; } }
        public static int Treasury { get { return HasData ? Work.partydata[TREASURY] : 0; } }

        /// <summary>所持金を上限で切って書く。</summary>
        private static void SetMoney(long v)
        {
            if (v < 0) v = 0;
            if (v > Cap) v = Cap;
            Work.partydata[WALLET] = (int)v;
        }

        // ---------------------------------------------------------------- どこでも金庫番

        /// <summary>ゲーム本編に入っていてセーブデータが載っているか (タイトルでは partydata が空)。</summary>
        public static bool InGame()
        {
            return Main.proc == 2 && Main.core != null && Main.core.proc[0] == 1 && HasData;
        }

        /// <summary>フィールドで歩ける時かポーズ中 (店・メニュー・戦闘・会話中は所持金を外から変えない)。</summary>
        public static bool CanOperateNow()
        {
            return InGame() && (IsFieldFree(Main.core) || FieldPause.IsPaused());
        }

        /// <summary>
        /// フィールドで自由に動ける状態か。SoftReset.IsFieldIdle から歩行中のマップスクロール (map_scrol_flag_v/h) の条件を外したもの
        /// (歩きながら押しても引き出せるように。所持金の増減は歩行と無関係)。戦闘・メニュー/店 (tono_flg)・メッセージ・イベント・
        /// 自動歩行 (man_road_sw)・マップ切り替え・フェード中は不可。
        /// </summary>
        public static bool IsFieldFree(Core c)
        {
            if (c == null) return false;
            return c.proc[0] == 1
                && c.battle_flg == 0 && c.tono_flg == 0 && c.exevt_flg == 0 && c.exdemo_flg == 0
                && c.mess_flag == 0 && c.ivent_sw == 0 && c.ivent_stop_flag == 0
                && c.man_cant_move == 0 && c.mapchgflg == 0 && c.demo_flag == 0 && c.man_road_sw == 0
                && !Sys.fadeflg;
        }

        /// <summary>今引き出せる額 (国庫 − 残す額、所持金の空き、のうち小さい方。0 以下なら引き出せない)。</summary>
        public static int WithdrawableAmount()
        {
            if (!HasData) return 0;
            int room = Cap - Money;
            int avail = Treasury - Floor;
            return room < avail ? room : avail;
        }

        /// <summary>国庫 → 所持金。戻り値は画面に出す結果の文。</summary>
        public static string Withdraw(out bool ok)
        {
            ok = false;
            if (!HasData) return "セーブデータがありません";
            int money = Money, treasury = Treasury;
            if (money >= Cap) return "財布はいっぱいです (" + money.ToString("N0") + ")";
            if (treasury <= Floor) return "国庫に残す額 " + Floor.ToString("N0") + " を超える分がありません (国庫 " + treasury.ToString("N0") + ")";
            int amount = WithdrawableAmount();
            if (amount <= 0) return "引き出せる分がありません";
            Work.partydata[TREASURY] = treasury - amount;
            SetMoney((long)money + amount);
            ok = true;
            Plugin.Log.LogInfo("[Economy] withdraw " + amount + " (wallet " + money + " -> " + Money + ", treasury " + treasury + " -> " + Treasury + ", floor " + Floor + ")");
            return "国庫から " + amount.ToString("N0") + " 引き出した (財布 " + Money.ToString("N0") + "、国庫 " + Treasury.ToString("N0") + ")";
        }

        /// <summary>所持金 → 国庫 (全額。国庫の上限で入らない分は財布に残す)。</summary>
        public static string Deposit(out bool ok)
        {
            ok = false;
            if (!HasData) return "セーブデータがありません";
            int money = Money, treasury = Treasury;
            if (money <= 0) return "財布は空です";
            int room = TREASURY_MAX - treasury;
            int amount = money < room ? money : room;
            if (amount <= 0) return "国庫がいっぱいです (" + treasury.ToString("N0") + ")";
            Work.partydata[TREASURY] = treasury + amount;
            SetMoney((long)money - amount);
            ok = true;
            Plugin.Log.LogInfo("[Economy] deposit " + amount + " (wallet " + money + " -> " + Money + ", treasury " + treasury + " -> " + Treasury + ")");
            return "国庫に " + amount.ToString("N0") + " 預けた (財布 " + Money.ToString("N0") + "、国庫 " + Treasury.ToString("N0") + ")";
        }

        // ---------------------------------------------------------------- 設定画面用

        public static string MoneyLabel() { return Money.ToString("N0") + " / " + Cap.ToString("N0") + " クラウン"; }

        public static string MaxLabel()
        {
            int c = Cap;
            return c == VANILLA_MAX ? "10,000 (本体どおり)" : c.ToString("N0");
        }

        public static string FloorLabel()
        {
            int f = Floor;
            return f == 0 ? "0 (全部引き出せる)" : f.ToString("N0") + " クラウン";
        }

        public static void CycleMax(int dir)
        {
            if (Max == null) return;
            int v = Cap;
            if (dir > 0) v = (v >= HARD_MAX) ? HARD_MAX : System.Math.Min(HARD_MAX, (v / MAX_STEP) * MAX_STEP + MAX_STEP);
            else v = (v == HARD_MAX) ? (HARD_MAX / MAX_STEP) * MAX_STEP : System.Math.Max(VANILLA_MAX, (v / MAX_STEP) * MAX_STEP - MAX_STEP);
            Max.Value = v;
        }

        public static void CycleFloor(int dir)
        {
            if (TreasuryFloor == null) return;
            int v = (Floor / FLOOR_STEP) * FLOOR_STEP + dir * FLOOR_STEP;
            TreasuryFloor.Value = Mathf.Clamp(v, 0, FLOOR_MAX);
        }

        // ---------------------------------------------------------------- 割り当てボタン (PadFunction.Withdraw)

        private static bool s_prevBtn;
        private static int s_lastBtnFrame = -1;
        private static string s_notice = "";
        private static float s_noticeUntil;
        private static GUIStyle s_style;
        private static int s_styleSize = -1;

        /// <summary>
        /// 割り当てボタンの押下エッジ。ConfigMenu.Tick から毎論理フレーム呼ぶ (倍速中は同じ Unity フレームで複数回呼ばれるのでフレーム番号で 1 回に絞る)。
        /// </summary>
        public static void HandleButton()
        {
            // ボタン割り当ての Withdraw か、フィールドの LB / RB ([Economy] FieldLB / FieldRB = Withdraw。歩ける時とポーズ中だけ)
            bool down = TreasuryScreen.IsActionDown(FieldLRAction.Withdraw, PadFunction.Withdraw);
            bool edge = down && !s_prevBtn && Time.frameCount != s_lastBtnFrame;
            s_prevBtn = down;
            if (!edge) return;
            s_lastBtnFrame = Time.frameCount;
            if (ConfigMenu.IsOpen || SoftReset.Active || QuitGame.Active || QuitGame.DialogOpen || TreasuryScreen.Active) return;
            if (!CanOperateNow())
            {
                Plugin.Log.LogInfo("[Economy] withdraw button ignored (not on the field) " + FieldPause.StateText());
                UiParts.SeCancel();
                ShowNotice("国庫からの引き出しはフィールドで (ポーズ中でも可)");
                return;
            }
            bool ok;
            string msg = Withdraw(out ok);
            if (ok) UiParts.SeOk(); else UiParts.SeCancel();
            ShowNotice(msg);
        }

        public static void ShowNotice(string text) { ShowNotice(text, TITLE, true, NOTICE_SECONDS); }

        /// <summary>見出しと大臣の絵の有無を指定して出す (弔いの鍛冶など、どこでも金庫番以外の知らせ用)。</summary>
        public static void ShowNotice(string text, string title, bool withMinister, float seconds)
        {
            s_notice = text;
            s_noticeTitle = title;
            s_noticeMinister = withMinister;
            s_noticePicture = null;
            s_noticeUntil = Time.realtimeSinceStartup + seconds;
        }

        /// <summary>大臣の代わりに別の絵を添えて出す (開発所の職人など)。picture は (枠, 1 ドットの px) で呼ばれ、枠の幅は pictureDots ドット、高さは大臣と同じ。</summary>
        public static void ShowNotice(string text, string title, System.Action<Rect, int> picture, int pictureDots, float seconds)
        {
            ShowNotice(text, title, false, seconds);
            s_noticePicture = picture;
            s_noticePictureDots = pictureDots;
        }
        private static string s_noticeTitle = TITLE;
        private static bool s_noticeMinister = true;
        private static System.Action<Rect, int> s_noticePicture;
        private static int s_noticePictureDots;

        private static GUIStyle s_stylePaper;
        private static GUIStyle s_styleTitle;
        private static GUIStyle s_styleTitlePaper;
        private const string TITLE = "どこでも金庫番";

        /// <summary>
        /// 画面上部中央に結果を数秒出す (Plugin.OnGUI から)。どこでもアバロンの窓と同じ灰色の紙の小窓に、大臣の絵 (歩きアニメ) を添える
        /// (窓を開かずに引き出した時も大臣が出てくるように)。部品が無ければ暗い箱に黄色の文字。
        /// </summary>
        public static void DrawNotice()
        {
            if (s_notice.Length == 0 || Time.realtimeSinceStartup > s_noticeUntil) return;
            if (ConfigMenu.IsOpen || TreasuryScreen.IsOpen) return; // 設定画面・大臣の窓の中では s_status に出る
            int size = Mathf.Max(12, Screen.height / 36);
            if (s_style == null || s_styleSize != size)
            {
                s_styleSize = size;
                s_style = new GUIStyle(GUI.skin.label);
                s_style.fontSize = size;
                s_style.fontStyle = FontStyle.Bold;
                s_style.alignment = TextAnchor.MiddleLeft;
                s_style.wordWrap = false;
                s_style.normal.textColor = new Color(1f, 0.92f, 0.45f);
                s_stylePaper = new GUIStyle(s_style);
                s_stylePaper.normal.textColor = new Color(0.45f, 0.05f, 0.02f);   // 灰色の紙の上では窓の結果の文と同じ暗い赤
                s_styleTitle = new GUIStyle(s_style);
                s_styleTitle.normal.textColor = Color.white;
                s_styleTitlePaper = new GUIStyle(s_style);
                s_styleTitlePaper.normal.textColor = Color.black;                  // どこでもアバロンの窓の見出しと同じ黒
            }
            // 大臣の絵 (どこでもアバロンの窓と同じ大きさ = TreasuryScreen.MinisterDot 倍) + 文字 2 行 (見出し「どこでも金庫番」と結果。
            // 文字は小さいまま、2 行まとめて絵の中央の高さに)。幅は文字の実測、画面に入らなければ切る
            bool minister = s_noticeMinister && TreasuryScreen.HasMinister;
            bool picture = minister || s_noticePicture != null;
            string title = s_noticeTitle;
            int dot = TreasuryScreen.MinisterDot;
            float sprH = picture ? dot * TreasuryScreen.MinisterDots : 0f;                       // 絵の高さ (大臣と同じ)
            float spr = minister ? sprH : s_noticePicture != null ? dot * s_noticePictureDots : 0f;   // 絵の幅
            // 左右の余白は上下よりたっぷり取る (お金の話なので窮屈に見せない)
            float padY = Mathf.Round(size * 0.5f);
            float padX = Mathf.Round(size * 1.6f);
            float gap = picture ? Mathf.Round(size * 0.9f) : 0f;
            float lineH = Mathf.Round(size * 1.3f);
            float textW = Mathf.Max(s_style.CalcSize(new GUIContent(title)).x, s_style.CalcSize(new GUIContent(s_notice)).x) + size * 0.3f;
            float w = Mathf.Min(Screen.width - size * 2, padX * 2 + spr + gap + textW);
            float h = Mathf.Max(sprH + (s_noticePicture != null ? dot : 0f), lineH * 2) + padY * 2;   // 職人の歩きのコマは 1 ドット下がる
            Rect r = new Rect(Mathf.Round((Screen.width - w) / 2), Mathf.Round(size * 1.5f), Mathf.Round(w), Mathf.Round(h));
            bool paper = UiParts.DrawPaperWindow(r, true);
            if (!paper) UiParts.DrawDarkBox(r);
            UiParts.Shadow = !paper;
            if (minister) TreasuryScreen.DrawMinister(new Rect(r.x + padX, r.y + Mathf.Round((h - sprH) / 2f), spr, sprH));
            else if (s_noticePicture != null)
            {
                try { s_noticePicture(new Rect(r.x + padX, r.y + padY, spr, h - padY * 2), dot); }
                catch (System.Exception e) { s_noticePicture = null; Plugin.Log.LogWarning("[Economy] notice picture failed: " + e.Message); }
            }
            float tx = r.x + padX + spr + gap;
            float ty = r.y + Mathf.Round((h - lineH * 2) / 2f);
            UiParts.Label(new Rect(tx, ty, r.xMax - padX - tx, lineH), title, paper ? s_styleTitlePaper : s_styleTitle);
            UiParts.Label(new Rect(tx, ty + lineH, r.xMax - padX - tx, lineH), s_notice, paper ? s_stylePaper : s_style);
        }
    }

    /// <summary>宝箱のクラウン: 本体は 10,000 で切る。切る前の額から上限で切り直す。</summary>
    [HarmonyPatch(typeof(Core), nameof(Core.get_takara_okane))]
    internal static class Wallet_get_takara_okane_Patch
    {
        private static void Prefix(out int __state) { __state = Wallet.Money; }

        private static void Postfix(Core __instance, int __state)
        {
            if (Wallet.Cap <= Wallet.VANILLA_MAX || Work.partydata == null) return;
            long v = (long)__state + __instance.get_gold;
            if (v > Wallet.Cap) v = Wallet.Cap;
            if (Work.partydata[Wallet.WALLET] != (int)v)
            {
                Plugin.Log.LogInfo("[Economy] chest +" + __instance.get_gold + ": wallet " + __state + " -> " + v + " (vanilla cap would give " + Work.partydata[Wallet.WALLET] + ")");
                Work.partydata[Wallet.WALLET] = (int)v;
            }
        }
    }

    /// <summary>
    /// 宝箱のクラウンの分岐: 本体は「所持金 ≥ 10,000 なら『いっぱいで入らない』側へ飛ぶ」。
    /// 比較だけを上限に合わせたいので、1 万以上で上限未満の間だけ所持金を一時的に 0 に見せて (このメソッドは所持金を比較にしか使わない)、
    /// Postfix で元に戻す。処理そのものは本体のまま。
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.get_okane_sep))]
    internal static class Wallet_get_okane_sep_Patch
    {
        private static void Prefix(out int __state)
        {
            __state = -1;
            if (Work.partydata == null) return;
            int m = Work.partydata[Wallet.WALLET];
            if (m >= Wallet.VANILLA_MAX && m < Wallet.Cap)
            {
                __state = m;
                Work.partydata[Wallet.WALLET] = 0;
            }
        }

        private static void Postfix(int __state)
        {
            if (__state >= 0) Work.partydata[Wallet.WALLET] = __state;
        }
    }

    /// <summary>店で売る: 本体は売値を足して 10,000 で切る。切る前の額から上限で切り直す。</summary>
    [HarmonyPatch(typeof(Menu), "shop_uru_a2")]
    internal static class Wallet_shop_uru_a2_Patch
    {
        private static void Prefix(Menu __instance, out long __state)
        {
            __state = -1;
            // waza_te2 != 1 は確認で「いいえ」側 (shop_kau_b2 へ) で所持金は変わらない
            if (__instance.waza_te2 != 1 || Work.partydata == null) return;
            __state = (long)Work.partydata[Wallet.WALLET] + (long)__instance.equip_work[5] * __instance.value1;
        }

        private static void Postfix(long __state)
        {
            if (__state < 0 || Wallet.Cap <= Wallet.VANILLA_MAX) return;
            long v = __state > Wallet.Cap ? Wallet.Cap : __state;
            if (Work.partydata[Wallet.WALLET] != (int)v)
            {
                Plugin.Log.LogInfo("[Economy] sell: wallet " + Work.partydata[Wallet.WALLET] + " -> " + v);
                Work.partydata[Wallet.WALLET] = (int)v;
            }
        }
    }

    /// <summary>
    /// N 万クラウンの宝箱 (本体: 国庫に N × 10,000 を足し、所持金を 1 万ちょうどにして「財布もいっぱいになった」)。
    /// MOD: 所持金を上限まで満たす。ただし増える総額は本体と同じにし、1 万を超えて満たした分は宝箱の国庫行きから差し引く
    /// (つまり宝箱の前で引き出しを済ませたのと同じ)。1 万を超えて持っていた分も消えない。
    /// 追憶の迷宮の同種の宝箱は Core.appnew_ivent の中にあるので、そちらも同じ Postfix (所持金を 10,000 ちょうどに書くのはこの 2 か所だけ)。
    /// </summary>
    [HarmonyPatch]
    internal static class Wallet_gold_ivent_Patch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Core), nameof(Core.takara_gold_ivent));
            yield return AccessTools.Method(typeof(Core), nameof(Core.appnew_ivent));
        }

        private static void Prefix(out long[] __state)
        {
            __state = Work.partydata == null ? null : new long[] { Wallet.Money, Wallet.Treasury };
        }

        private static void Postfix(long[] __state)
        {
            if (__state == null || Work.partydata == null) return;
            long before = __state[0];
            long treasuryBefore = __state[1];
            long chest = Wallet.Treasury - treasuryBefore;                     // 国庫に入った額 = N × 10,000 (上限で切れていれば少ない)
            // appnew_ivent は宝箱以外でも呼ばれるので、「所持金が 1 万ちょうどになり国庫が万単位で増えた」時だけ宝箱と見る
            if (Work.partydata[Wallet.WALLET] != Wallet.VANILLA_MAX || chest <= 0 || chest % 10000 != 0) return;

            long gain = chest + System.Math.Max(0, Wallet.VANILLA_MAX - before); // 本体で増える総額 (財布が 1 万に満たない分は本体でも無料で埋まる)
            long fill = System.Math.Min(Wallet.Cap - before, gain);              // 所持金に入れる分 (上限まで、ただし宝箱の総額まで)
            if (fill < 0) fill = 0;                                            // 上限を下げた後に上限より多く持っていた時は減らさない
            long wallet = before + fill;
            long treasury = treasuryBefore + (gain - fill);
            if (treasury > Wallet.TREASURY_MAX) treasury = Wallet.TREASURY_MAX;
            Work.partydata[Wallet.WALLET] = (int)wallet;
            Work.partydata[Wallet.TREASURY] = (int)treasury;
            // 宝箱のメッセージの金額も実際に動かした額にする (Wallet_ten_put_Patch)。満たしていなければ本体の文のまま
            if (fill > 0) Wallet_ten_put_Patch.SetPending(fill, treasury - treasuryBefore, wallet >= Wallet.Cap); else Wallet_ten_put_Patch.Clear();
            Plugin.Log.LogInfo("[Economy] gold chest +" + chest + ": wallet " + before + " -> " + wallet + ", treasury " + treasuryBefore + " -> " + treasury
                + " (vanilla: wallet 10000, treasury " + (treasuryBefore + chest) + ")");
        }
    }

    /// <summary>
    /// N 万クラウンの宝箱のメッセージ (msg 2062、追憶の迷宮は msg 3126) の金額を、MOD が実際に動かした額に差し替える。
    ///
    /// 本体のスクリプト (script.bin。2 つとも同じ並び):
    ///   「財宝を見つけた！」改行 ten_put(255,n) 「００００」 work0=132 APPNEW 225 「クラウンが」改行「帝国の金庫に入った！」改行「財布もいっぱいになった。」
    /// ten_put (命令 61) は koukan_work[n] (= N) を 3 桁で name_work に書き、サブレベル (mess_type 2) として表示させる命令。
    /// 続く「００００」は本文のリテラルで、APPNEW 225 は直前の数値 (ten_put_param) × 10^4 の桁数に応じてカンマを描く。
    /// MOD が所持金を満たした時は国庫に入った額が N 万ではなくなるので、ten_put の Prefix で
    /// 「{財布に入れた額}クラウンが財布に、/{国庫に入った額}クラウンが/帝国の金庫に入った！(/財布もいっぱいになった。)」を name_work に組み、
    /// スクリプト側の数値〜本文 (51 バイト) を飛ばして、その先 (set_taka_f / 効果音) から続ける。
    /// バイト列が想定と違えば (別の版など) 何もしないで本体のまま。
    /// name_work は 32 バイトしかないので、足りなければ大きい配列に差し替える (mestbl[2] も同じ配列を指す。参照はこの 2 か所だけ)。
    /// 文字は Window.fonttbl の添字で書く: 添字 &lt; 256 は 1 バイト、256 以上は +7936 して 2 バイト (上位 32〜35)。
    /// 数字は本体の ten_to_work と同じ全角 (8960 + 値)。カンマは本体のように重ね描きできないので付けない。
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.ten_put))]
    internal static class Wallet_ten_put_Patch
    {
        // 直前の宝箱で MOD が動かした額。s_fill < 0 = 差し替え不要
        private static long s_fill = -1, s_treasuryGain;
        private static bool s_walletFull;

        public static void SetPending(long fill, long treasuryGain, bool walletFull)
        {
            s_fill = fill; s_treasuryGain = treasuryGain; s_walletFull = walletFull;
        }
        public static void Clear() { s_fill = -1; }

        private const int CMD_TEN_PUT = 61, NL = 36;
        // ten_put(255,n) の 3 バイトの後に続くはずのバイト列
        private static readonly byte[] ZEROS = { 35, 0, 35, 0, 35, 0, 35, 0 };                 // 「００００」
        private static readonly byte[] TAIL = {
            64, 0, 132, 13, 225,                                                                 // work0 = 132、APPNEW 225 (カンマ描画)
            137, 168, 132, 174, 184, 36, 235, 238, 105, 32, 194, 35, 121, 102, 32, 207, 129, 96, 234, 36, // クラウンが/帝国の金庫に入った！/
            35, 95, 35, 166, 114, 82, 129, 33, 170, 82, 102, 101, 129, 96, 231 };               // 財布もいっぱいになった。

        private static bool Prefix(Core __instance)
        {
            if (s_fill < 0) return true;
            long fill = s_fill, gain = s_treasuryGain;
            bool full = s_walletFull;
            s_fill = -1;                                                                         // 宝箱の後の最初の ten_put がその宝箱のもの。1 回だけ

            Core c = __instance;
            int p = Core.mess_adrs;                                                              // 命令 61 の位置 (読み進める前)
            byte[] script = c.mestbl[c.mess_type];
            int skip = 3 + ZEROS.Length + TAIL.Length;
            if (script == null || p < 0 || p + skip > script.Length || script[p] != CMD_TEN_PUT || script[p + 1] != 255
                || !Match(script, p + 3, ZEROS) || !Match(script, p + 3 + ZEROS.Length, TAIL))
            {
                Plugin.Log.LogWarning("[Economy] gold chest message: unexpected script bytes at " + p + ", leaving vanilla text");
                return true;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if (gain > 0)
            {
                sb.Append(fill).Append("クラウンが財布に、").Append('\n')
                  .Append(gain).Append("クラウンが").Append('\n')
                  .Append("帝国の金庫に入った！");
            }
            else
            {
                sb.Append(fill).Append("クラウンが").Append('\n').Append("財布に入った！");
            }
            if (full) sb.Append('\n').Append("財布もいっぱいになった。");

            byte[] body = ScriptText.Encode(sb.ToString(), "[Economy] gold chest message:");
            if (body == null) return true;

            // 本体の ten_put と同じ「サブレベルに入る」手順。戻り先だけ、飛ばした本文の次にする (ScriptText.EnterSublevel)
            ScriptText.EnterSublevel(c, body, p + skip);
            Plugin.Log.LogInfo("[Economy] gold chest message: wallet +" + fill + ", treasury +" + gain + (full ? " (wallet full)" : ""));
            return false;
        }

        private static bool Match(byte[] a, int at, byte[] pat) { return ScriptText.Match(a, at, pat); }
    }
}
