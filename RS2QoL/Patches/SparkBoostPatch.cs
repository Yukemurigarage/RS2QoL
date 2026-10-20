using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// ヒーローモード (v0.48.0 までの表示名は「漫画の主人公モード」、cfg のキーは HeroMode のまま): 瀕死 (膝をつく HP) の味方、
    /// または戦える仲間が自分 1 人だけになった味方が攻撃した時 (敵の技を受けた時の見切りも) だけ、技をひらめく確率を倍にする。既定 OFF。
    /// 「最後の 1 人」は Work.memberdata_b[0..member-1] のうち 戦える人 (CanFight: HP > 0、戦闘不能・石化・気絶・麻痺・眠り・スタン・魅了でない) が 1 人。
    ///
    /// 本体の仕組み (decompiled/Battle.cs):
    /// - ひらめきの抽選は攻撃のダメージ計算 Calc → check_change → (派生表 Data.waza_level の候補ごとに) test_change → check_get。
    ///   check_get: d2 = 相手 XA1[24..28] の平均 (敵は monster_data[+3] の閃きレベルが 5 つとも入っている)、d1 = 派生先の技の難度。
    ///   差 = d2 − d1 を prob_pos_tbl (差 ≥ 0: 0→50, 1→18, 2→21, 3→24, 4→26, 5→28, 6→29, 7 以上→30) か
    ///   prob_nega_tbl (差 < 0: −1→11, −2→8, −3→5, −4→3, −5→1, −6 以下→0) で値にし、1〜255 の乱数以下なら成功 (= 値/255)。
    ///   味方の HP も武器レベルも式に入らない。候補は表の順に 1 つずつ抽選し、最初の成功で終わる。
    /// - 膝をつくポーズは check_up: HP (XA0[14]) < 最大 HP (XA0[16]) / 4。HP の数字が橙になるのは最大の 1/2 未満 (BattleUIDamage.DrawStatus) で別の基準。
    /// - WP は元々選んだ技の分だけ Calc の最初 (set_exp) で引かれ、ひらめいた技の WP は見ない。最大 WP (calc_wp) もひらめきでは変わらない。
    ///
    /// MOD: check_get の Prefix で、行動した味方本人 (XA0 が Work.memberdata_b のどれか) の HP が 最大 × HpPercent / 100 未満の時だけ、
    /// 敵の閃きレベルを + LevelBonus として読み替えた差でも表を引き、本体の値と大きい方を × Multiplier (上限 255) にして 1 回抽選し、
    /// 本体の check_get を止める (抽選が中にあるので Postfix では変えられない)。敵のデータ (monster_data[+3] は敵の武器レベル 5 つに
    /// コピーされ、set_effective の命中・回避・威力の係数と calc_wp の WP にも使われる) には触らず、判定の中だけで足す。
    /// 読み替えた後も 0 なら 0 なので、「敵の閃きレベル + LevelBonus が技の難度より 6 以上低い技はひらめかない」。
    /// test_counter (XA1 = XA0 に差し替えて自分の武器レベルで引く経路) と敵の抽選は触らない。
    /// 設定画面の表示用に、直前の判定 (差・本体の値・倍にした値) を覚える。
    ///
    /// 見切りも同じ check_get: 敵の攻撃 (技データ +8 の bit 0x4000 = 見切れる技) を味方が受ける時、do_mikiri → check_mikiri2
    /// (状態異常でない、未修得 (修得済みなら mikiri_equip で回避)、道場に無い `partydata[289+]`) → test_mikiri が
    /// d1 = Data.mikiri_level[技] (ソウルスティール 25、なぎ払い 5、残像剣 50 など) を難度にし、XA1 = XA0 (攻撃した敵) に差し替えて
    /// check_get を呼ぶ。つまり差 = 敵の閃きレベル − 見切りの難度で、同じ確率表。覚えた見切りは set_mikiri で味方の技枠 (XA1[61..68]、
    /// XA1[60] の bit が見切り印) に入り、戦闘の結果に関係なく残る。
    /// MOD は test_mikiri の Prefix で守る側 (その時点の XA1 = 味方) を控え、check_get の Prefix で XA0 == XA1 かつ控えがある時は
    /// その味方の HP で瀕死を見て、同じ倍率・敵レベル +N・覚醒判定 (見切りは 1 判定 1 候補なので毎回) で抽選する。
    /// </summary>
    internal static class SparkBoost
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> LastOneStanding;
        public static ConfigEntry<int> Multiplier;
        public static ConfigEntry<int> HpPercent;
        public static ConfigEntry<int> LevelBonus;
        public static ConfigEntry<int> FloorPercent;
        public static ConfigEntry<int> FloorMaxGap;

        public const int MIN_MULT = 1, MAX_MULT = 8, DEFAULT_MULT = 3;
        public const int MIN_HP = 5, MAX_HP = 100, STEP_HP = 5, DEFAULT_HP = 25;
        public const int MIN_BONUS = 0, MAX_BONUS = 20, DEFAULT_BONUS = 5;
        public const int MIN_FLOOR = 0, MAX_FLOOR = 50, DEFAULT_FLOOR = 5;
        public const int MIN_GAP = 5, MAX_GAP = 40, STEP_GAP = 5, DEFAULT_GAP = 15;
        private const int MAX_VALUE = 255;

        /// <summary>覚醒判定 (0 % の技への固定 F % の判定) をこの攻撃でもう使ったか。check_change (1 攻撃 1 回) の Prefix で戻す。</summary>
        private static bool s_floorUsed;

        /// <summary>直前のひらめき判定 (設定画面の表示用)。Diff は本体の差、Base は本体の値、BoostDiff / Boosted は瀕死の読み替え後の差と値 (読み替えなかった時は同じ)。</summary>
        public static bool HasLast;
        public static int LastDiff, LastBase, LastBoostDiff, LastBoosted;
        public static bool LastBoostedFlag;

        /// <summary>直前の判定が見切り (敵の技を受けた時) だったか。</summary>
        public static bool LastMikiriFlag;

        /// <summary>test_mikiri の中だけ: 敵の技を受けている味方 (Work.memberdata_b のどれか)。check_get は XA1 = XA0 (敵) に差し替えて呼ばれるので、ここから瀕死を見る。</summary>
        public static int[] MikiriDefender;

        private static bool s_announced;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Spark", "HeroMode", false,
                "ヒーローモード。瀕死 (膝をつく HP) の味方が攻撃した時だけ、技をひらめく確率を Multiplier 倍にする。false なら本体そのまま。"
                + "本体で 0 % の組み合わせ (敵の閃きレベルが技の難度より 6 以上低い) は倍にしても 0 % のまま。"
                + "見切り (敵の技を受けた時に覚える) も同じ式なので、瀕死の味方が受けた時は同じ倍率・足す値・覚醒判定で判定する");
            LastOneStanding = config.Bind("Spark", "LastOneStanding", true,
                "ヒーローモードの時、戦える仲間が自分 1 人だけになったら HP に関係なく発動する (瀕死と同じ扱い)。"
                + "戦えない = HP 0、戦闘不能、石化、気絶、麻痺、眠り、スタン、魅了。false なら HP だけで見る");
            Multiplier = config.Bind("Spark", "Multiplier", DEFAULT_MULT,
                new ConfigDescription("瀕死の時にひらめく確率を何倍にするか (1〜8)。本体の確率は差 (敵の閃きレベル − 技の難度) で 0〜19.6 %、"
                    + "7 以上は 11.8 % で頭打ち。3 なら 11.8 % → 35 %、差 0 の 19.6 % → 59 %",
                    new AcceptableValueRange<int>(MIN_MULT, MAX_MULT)));
            HpPercent = config.Bind("Spark", "HpPercent", DEFAULT_HP,
                new ConfigDescription("瀕死とみなす HP (最大 HP に対する %、5〜100、5 刻み)。HP がこの割合未満の時に倍にする。"
                    + "25 = 本体で膝をつくポーズになる HP (最大の 1/4 未満)。50 = HP の数字が橙になる HP (最大の 1/2 未満)",
                    new AcceptableValueRange<int>(MIN_HP, MAX_HP)));
            LevelBonus = config.Bind("Spark", "LevelBonus", DEFAULT_BONUS,
                new ConfigDescription("瀕死の時に敵の閃きレベルに足して判定する値 (0〜20)。5 なら本体で届かない (敵が技の難度より 6〜10 低い) 技にも手が届く。"
                    + "判定の中で足すだけで、敵の強さ (命中・回避・威力・WP) は変わらない。0 なら足さない",
                    new AcceptableValueRange<int>(MIN_BONUS, MAX_BONUS)));
            FloorPercent = config.Bind("Spark", "FloorPercent", DEFAULT_FLOOR,
                new ConfigDescription("覚醒判定: 瀕死の時、LevelBonus を足しても 0 % のままの技に対して、1 回の攻撃につき 1 回だけ行う固定の確率 (%、0〜50)。"
                    + "対象は派生表の順で最初に 0 % だった技 (表はおおむね難度の低い順)。0 なら行わない",
                    new AcceptableValueRange<int>(MIN_FLOOR, MAX_FLOOR)));
            FloorMaxGap = config.Bind("Spark", "FloorMaxGap", DEFAULT_GAP,
                new ConfigDescription("覚醒判定の届く幅 (5〜40、設定画面では 5 刻み)。技の難度が 敵の閃きレベル + LevelBonus + この値 より高いと覚醒判定もしない。"
                    + "15 なら レベル 5 の敵に瀕死で挑むと難度 25 まで届く (本体で届くのは 難度 10 まで)",
                    new AcceptableValueRange<int>(MIN_GAP, MAX_GAP)));
        }

        public static void ResetAttack() { s_floorUsed = false; }

        public static bool FloorUsed { get { return s_floorUsed; } set { s_floorUsed = value; } }

        public static bool IsEnabled { get { return Enabled != null && Enabled.Value; } }

        public static bool LastOne { get { return LastOneStanding == null || LastOneStanding.Value; } }

        /// <summary>直前の判定で発動した理由 (設定画面・ログ用): "瀕死" / "最後の 1 人" / "瀕死・最後の 1 人"。</summary>
        public static string LastReason = "";

        /// <summary>
        /// 戦える仲間が 1 人だけか。「戦えない」= HP 0、戦闘不能 (LP 0) か石化 (XA[7] & 0xC0、本体 check_die_mem と同じ)、
        /// 気絶・麻痺・眠り・スタン (XA[4] & 0xF0、本体 check_status_mem = コマンドを入力できない状態)、魅了 (XA[4] & 4、敵側で動く)。
        /// 暴走・混乱・パニックは (敵を殴ることもあるので) 戦える側に数える。
        /// </summary>
        public static bool IsLastStanding(Battle b)
        {
            int[][] m = Work.memberdata_b;
            if (m == null) return false;
            int n = b.member;
            if (n > m.Length) n = m.Length;
            int alive = 0;
            for (int i = 0; i < n; i++)
            {
                int[] x = m[i];
                if (x == null) continue;
                if (!CanFight(x)) continue;
                alive++;
            }
            return alive == 1;
        }

        public static bool CanFight(int[] x)
        {
            if (x[14] <= 0) return false;
            if ((x[7] & (Battle.DEAD | Battle.STONED)) != 0) return false;
            if ((x[4] & Battle.STATUS_A) != 0) return false;      // FAINT | PARALYZE | SLEEP | STAN
            if ((x[4] & Battle.CHARM) != 0) return false;
            return true;
        }

        /// <summary>ヒーローモードが発動する状態か (瀕死、または LastOneStanding で最後の 1 人)。理由を LastReason に入れる。</summary>
        public static bool IsAwake(Battle b, int[] actor)
        {
            bool near = IsNearDeath(actor);
            bool last = LastOne && IsLastStanding(b);
            LastReason = near && last ? "瀕死・最後の 1 人" : near ? "瀕死" : last ? "最後の 1 人" : "";
            return near || last;
        }

        public static int Mult { get { return Multiplier != null ? Multiplier.Value : DEFAULT_MULT; } }

        public static int HpPct { get { return HpPercent != null ? HpPercent.Value : DEFAULT_HP; } }

        public static int Bonus { get { return LevelBonus != null ? LevelBonus.Value : DEFAULT_BONUS; } }

        public static int Floor { get { return FloorPercent != null ? FloorPercent.Value : DEFAULT_FLOOR; } }

        public static int Gap { get { return FloorMaxGap != null ? FloorMaxGap.Value : DEFAULT_GAP; } }

        /// <summary>覚醒判定の値 (F % を 255 段階に)。</summary>
        public static int FloorValue() { return (Floor * MAX_VALUE + 50) / 100; }

        /// <summary>XA0 (行動している側のデータ) が味方のものか。</summary>
        public static bool IsMember(int[] xa0)
        {
            if (xa0 == null) return false;
            int[][] m = Work.memberdata_b;
            if (m == null) return false;
            for (int i = 0; i < m.Length; i++)
            {
                if (object.ReferenceEquals(m[i], xa0)) return true;
            }
            return false;
        }

        /// <summary>瀕死か (HP < 最大 HP × HpPercent / 100。25 % の時は本体の check_up と同じ 最大 / 4 未満)。</summary>
        public static bool IsNearDeath(int[] xa0)
        {
            int hp = xa0[14], max = xa0[16];
            if (max <= 0) return false;
            return hp < (int)((long)max * HpPct / 100);
        }

        /// <summary>本体 check_get と同じ表引き。差 → 1〜255 の値 (0 = ひらめかない)。</summary>
        public static int TableValue(int diff)
        {
            int[] tbl = diff >= 0 ? Battle.prob_pos_tbl : Battle.prob_nega_tbl;
            int key = diff >= 0 ? diff : -diff;
            int i = 0;
            while (tbl[i * 2] < key) i++;
            return tbl[i * 2 + 1];
        }

        public static int Boost(int baseValue)
        {
            if (baseValue <= 0) return 0;
            int v = baseValue * Mult;
            return v > MAX_VALUE ? MAX_VALUE : v;
        }

        public static string Percent(int value)
        {
            if (value <= 0) return "0 %";
            return (value * 100.0 / MAX_VALUE).ToString("0.0") + " %";
        }

        // ---- 設定画面用 ----

        public static string MultLabel()
        {
            int m = Mult;
            return m == 1 ? "1 倍 (本体どおり)" : m + " 倍";
        }

        public static bool MultChanged() { return Mult != DEFAULT_MULT; }

        public static void CycleMult(int delta)
        {
            if (Multiplier == null) return;
            int m = Mult + delta;
            if (m < MIN_MULT) m = MIN_MULT;
            if (m > MAX_MULT) m = MAX_MULT;
            Multiplier.Value = m;
        }

        public static string HpLabel()
        {
            int p = HpPct;
            string s = p + " % 未満";
            if (p == 25) s += " (膝をつく HP)";
            else if (p == 50) s += " (数字が橙の HP)";
            else if (p == 100) s += " (いつでも)";
            return s;
        }

        public static bool HpChanged() { return HpPct != DEFAULT_HP; }

        public static void CycleHp(int delta)
        {
            if (HpPercent == null) return;
            int p = (HpPct / STEP_HP) * STEP_HP + delta;
            if (p < MIN_HP) p = MIN_HP;
            if (p > MAX_HP) p = MAX_HP;
            HpPercent.Value = p;
        }

        public static string BonusLabel()
        {
            int n = Bonus;
            return n == 0 ? "0 (足さない)" : "+" + n;
        }

        public static bool BonusChanged() { return Bonus != DEFAULT_BONUS; }

        public static void CycleBonus(int delta)
        {
            if (LevelBonus == null) return;
            int n = Bonus + delta;
            if (n < MIN_BONUS) n = MIN_BONUS;
            if (n > MAX_BONUS) n = MAX_BONUS;
            LevelBonus.Value = n;
        }

        public static string FloorLabel()
        {
            int f = Floor;
            return f == 0 ? "0 % (しない)" : f + " %";
        }

        public static bool FloorChanged() { return Floor != DEFAULT_FLOOR; }

        public static void CycleFloor(int delta)
        {
            if (FloorPercent == null) return;
            int f = Floor + delta;
            if (f < MIN_FLOOR) f = MIN_FLOOR;
            if (f > MAX_FLOOR) f = MAX_FLOOR;
            FloorPercent.Value = f;
        }

        public static string GapLabel()
        {
            int g = Gap;
            return "敵レベル + " + (Bonus + g) + " の難度まで";
        }

        public static bool GapChanged() { return Gap != DEFAULT_GAP; }

        public static void CycleGap(int delta)
        {
            if (FloorMaxGap == null) return;
            int g = (Gap / STEP_GAP) * STEP_GAP + delta;
            if (g < MIN_GAP) g = MIN_GAP;
            if (g > MAX_GAP) g = MAX_GAP;
            FloorMaxGap.Value = g;
        }

        private static string Signed(int v) { return (v >= 0 ? "+" : "") + v; }

        /// <summary>直前の判定が覚醒判定 (固定 F %) だったか。</summary>
        public static bool LastFloorFlag;

        public static string LastLabel()
        {
            if (!HasLast) return "(まだ判定していない)";
            string s = (LastMikiriFlag ? "見切り 差 " : "差 ") + Signed(LastDiff) + ": " + Percent(LastBase);
            if (LastFloorFlag) s += " → 覚醒 " + Percent(LastBoosted) + " (" + LastReason + ")";
            else if (LastBoostedFlag)
            {
                s += " → ";
                if (LastBoostDiff != LastDiff) s += "差 " + Signed(LastBoostDiff) + ": ";
                s += Percent(LastBoosted) + " (" + LastReason + ")";
            }
            return s;
        }

        /// <summary>check_get の置き換え。戻り値は本体と同じ (1 = ひらめいた)。本体と同じく d2 と calc_a / calc_b を更新する。</summary>
        public static int CheckGet(Battle b, int[] actor, int diff, int baseValue, int boostDiff, int boosted)
        {
            b.d2 = boosted;
            b.calc_a = 1;
            b.calc_b = MAX_VALUE;
            // 本体 get_rand: calc_a = getRandLimit(calc_b - calc_a + 1) + calc_a
            int roll = Sys.getRandLimit(MAX_VALUE) + 1;
            if (roll > MAX_VALUE) roll = MAX_VALUE;
            b.calc_a = roll;
            int hit = boosted >= roll ? 1 : 0;
            // 瀕死の判定は珍しいので毎回出す (敵レベル lv、派生先の技 waza = test_change の d0 (見切りなら受けた敵の技)、難度 need = d1 も添える)
            s_announced = true;
            string how = LastFloorFlag ? " floor " + Floor + "% = " : " -> diff=" + boostDiff + " x" + Mult + " = ";
            Plugin.Log.LogInfo("[Spark] applied" + (LastMikiriFlag ? " (mikiri)" : "") + ": lv=" + (diff + b.d1) + " waza=" + b.d0 + " need=" + b.d1
                + " diff=" + diff + " base=" + baseValue + how + boosted + " /255"
                + " hp=" + actor[14] + "/" + actor[16] + " why=" + LastReason + " roll=" + roll + " hit=" + hit);
            return hit;
        }
    }

    /// <summary>1 回の攻撃のひらめき処理の入口 (候補を順に抽選する)。覚醒判定の「1 攻撃 1 回」を数え直す。</summary>
    [HarmonyPatch(typeof(Battle), "check_change")]
    internal static class SparkBoost_check_change_Patch
    {
        private static void Prefix() { SparkBoost.ResetAttack(); }
    }

    /// <summary>ひらめきの抽選。瀕死の味方の攻撃の時だけ本体を止めて倍にした値で引く。</summary>
    [HarmonyPatch(typeof(Battle), "check_get")]
    internal static class SparkBoost_check_get_Patch
    {
        private static bool Prefix(Battle __instance, ref int __result)
        {
            Battle b = __instance;
            int[] xa0 = b.XA0, xa1 = b.XA1;
            if (xa0 == null || xa1 == null) return true;
            // 瀕死を見る味方。ひらめきは攻撃した XA0 本人、見切りは敵の技を受けている味方 (test_mikiri の Prefix で控えたもの)
            int[] actor = xa0;
            bool mikiri = false;
            if (object.ReferenceEquals(xa0, xa1))
            {
                // XA1 = XA0 に差し替えて呼ぶ経路は 2 つ: test_mikiri (XA0 = 攻撃した敵、控えあり) と
                // test_counter (自分の武器レベルで引く、控えなし)。後者は触らない
                if (SparkBoost.MikiriDefender == null) return true;
                actor = SparkBoost.MikiriDefender;
                mikiri = true;
            }
            if (!SparkBoost.IsMember(actor)) return true;

            int sum = 0;
            for (int i = 0; i < 5; i++) sum += xa1[24 + i];
            int diff = sum / 5 - b.d1;
            int baseValue = SparkBoost.TableValue(diff);
            // 瀕死なら「敵の閃きレベル + LevelBonus」の差で表を引き直し、本体の値と引き直した値の大きい方を倍にする
            // (差 0 の山 (50) が +5 で 28 に下がって「瀕死なのに下がる」逆転が起きないように)。倍率 1 かつ足す値 0 なら本体と同じなので止めない
            bool boost = SparkBoost.IsEnabled && (SparkBoost.Mult > 1 || SparkBoost.Bonus > 0 || SparkBoost.Floor > 0) && SparkBoost.IsAwake(b, actor);
            int boostDiff = diff;
            int boosted = baseValue;
            bool floor = false;
            if (boost)
            {
                int shifted = SparkBoost.TableValue(diff + SparkBoost.Bonus);
                if (shifted > baseValue) { boostDiff = diff + SparkBoost.Bonus; boosted = shifted; }
                boosted = SparkBoost.Boost(boosted);
                // 覚醒判定: それでも 0 % の技は、1 攻撃 1 回だけ固定 F % (届く幅は 難度 ≤ 敵レベル + LevelBonus + FloorMaxGap)
                if (boosted == 0 && SparkBoost.Floor > 0 && !SparkBoost.FloorUsed
                    && diff + SparkBoost.Bonus + SparkBoost.Gap >= 0)
                {
                    SparkBoost.FloorUsed = true;
                    floor = true;
                    boostDiff = diff;
                    boosted = SparkBoost.FloorValue();
                }
            }

            SparkBoost.HasLast = true;
            SparkBoost.LastDiff = diff;
            SparkBoost.LastBase = baseValue;
            SparkBoost.LastBoostDiff = boostDiff;
            SparkBoost.LastBoosted = boosted;
            SparkBoost.LastBoostedFlag = boost;
            SparkBoost.LastFloorFlag = floor;
            SparkBoost.LastMikiriFlag = mikiri;

            if (!boost) return true;        // 本体そのまま (表示用の記録だけ)
            __result = SparkBoost.CheckGet(b, actor, diff, baseValue, boostDiff, boosted);
            return false;
        }
    }

    /// <summary>
    /// 見切りの抽選 (敵の技を受けた味方が 1 回だけ check_get を引く)。入口の XA1 = 守る味方を控え、check_get の Prefix に渡す。
    /// 見切りは 1 判定 1 候補なので覚醒判定も毎回使えるようにフラグを戻す。Postfix で控えを消す (check_get は最初の段階でしか呼ばれない)。
    /// </summary>
    [HarmonyPatch(typeof(Battle), "test_mikiri")]
    internal static class SparkBoost_test_mikiri_Patch
    {
        private static void Prefix(Battle __instance)
        {
            SparkBoost.MikiriDefender = __instance.XA1;
            SparkBoost.ResetAttack();
        }

        private static void Postfix() { SparkBoost.MikiriDefender = null; }
    }
}
