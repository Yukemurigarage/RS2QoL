using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 味方の成長 (HP・技レベル・術レベル・全体レベル) の速さを、それぞれ % で調整する。
    ///
    /// 本体の仕組み (decompiled/Battle.cs):
    /// - 成長の元は 1 戦で倒した敵の技術点 got_exp (Battle.kill_exp が Data.mon_exp_table を足す) 1 つ。
    ///   戦闘後 (proc 20 → 30) に grow_party (全体レベル) → distribute_exp (仲間ごとに check_hp → check_skill) が使う。
    /// - HP (check_hp / calc_hp): 仲間ごとの hp_exp カウンタ memberdata[104] から got_exp を引き、got_exp 以下なら
    ///   HP += (乱数 1〜体力 + 体力) / 2、次の必要量 = Data.hp_exp_table[HP / 10]。上限 999。
    /// - 技・術レベル (check_skill / calc_skill): その戦闘で使った種類 (使用回数 memberdata[49 + i] != 0、11 枠 = 武器 5 + 術 6) に
    ///   D1 = 使用回数 + (got_exp (+ スキルリング id180 で got_exp / 2)) / 使った種類数 を配り、必要量 memberdata[106 + i*2] 以下なら
    ///   レベル +1 (memberdata[24 + i]、上限 50) で必要量 = Data.exp_table[レベル]。上がらなければ必要量 -= D1。
    ///   ダイアログ表示をまたぐ状態機械 (proc[procstg][0] = 0 / 10、[1] = 処理中の枠) で、1 回の呼び出しで複数枠を処理する。
    /// - 全体レベル (grow_party → check_skillmem(n, val)): val = 全員の使用回数の合計 (+ got_exp、合計が 0 なら 0) を枠ごとの
    ///   必要量 partydata[396 + n*2] から引き、以下なら partydata[385 + n] +1 (上限 40)、必要量 = Data.ura_exp_table[レベル]。
    ///   新しく加入する仲間の初期レベルに引き継がれる。
    /// - 腕力・素早さなどの能力値は成長しない (クラス固定)。WP / JP はレベルから自動計算。
    ///
    /// MOD: 技術点を読む処理の間だけ got_exp を「× % / 100」に読み替える (必要量の表はセーブが 16 bit で 65,000 の値があるので触らない)。
    /// - HP: check_hp の Prefix で got_exp を差し替え、Postfix で戻す。
    /// - 技・術: 武器と術で別の % にするため check_skill を同じ手順で書き直し、Prefix で本体を止める (return false)。
    ///   本体は 1 回の呼び出しで D0 = got_exp / 種類数 を全枠に使うので、got_exp の差し替えだけでは枠ごとに変えられない。
    ///   両方 100 % のときは本体をそのまま走らせる。
    /// - 全体: check_skillmem の Prefix で val を × % / 100 にする。
    /// 上限 (999 / 50 / 40) は本体の判定がそのまま効く。敵の強さ ([Growth]) と税収 ([Economy]) には触らない。
    /// </summary>
    internal static class PlayerGrowth
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<int> HpPercent;
        public static ConfigEntry<int> WeaponPercent;
        public static ConfigEntry<int> MagicPercent;
        public static ConfigEntry<int> PartyPercent;

        public const int MIN_PCT = 10, MAX_PCT = 500, STEP_PCT = 10;
        private const int WEAPON_SLOTS = 5;      // memberdata[24..28] = 武器、[29..34] = 術
        private const int SLOTS = 11;

        /// <summary>直前の戦闘の技術点 (設定画面の表示用。ゲーム起動後に戦闘していなければ -1)。</summary>
        public static int LastExp = -1;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("PlayerGrowth", "Enabled", true,
                "味方の成長の速さを MOD で調整する。いいえ なら本体そのまま");
            HpPercent = config.Bind("PlayerGrowth", "HpPercent", 100,
                new ConfigDescription("HP が上がる速さ (%)。本体は 100。200 なら半分の戦闘で上がり、50 なら 2 倍かかる。"
                    + "1 回に上がる量と上限 999 は本体のまま。10 刻み",
                    new AcceptableValueRange<int>(MIN_PCT, MAX_PCT)));
            WeaponPercent = config.Bind("PlayerGrowth", "WeaponPercent", 100,
                new ConfigDescription("技レベル (武器) が上がる速さ (%)。本体は 100。上限 50 は本体のまま。10 刻み",
                    new AcceptableValueRange<int>(MIN_PCT, MAX_PCT)));
            MagicPercent = config.Bind("PlayerGrowth", "MagicPercent", 100,
                new ConfigDescription("術レベルが上がる速さ (%)。本体は 100。上限 50 は本体のまま。10 刻み",
                    new AcceptableValueRange<int>(MIN_PCT, MAX_PCT)));
            PartyPercent = config.Bind("PlayerGrowth", "PartyPercent", 100,
                new ConfigDescription("全体レベル (新しく加入する仲間の初期レベルになる) が上がる速さ (%)。本体は 100。上限 40 は本体のまま。10 刻み",
                    new AcceptableValueRange<int>(MIN_PCT, MAX_PCT)));
        }

        public static bool IsEnabled { get { return Enabled != null && Enabled.Value; } }

        private static int Pct(ConfigEntry<int> e) { return IsEnabled && e != null ? e.Value : 100; }

        public static int Scale(int v, ConfigEntry<int> e)
        {
            int p = Pct(e);
            if (p == 100 || v <= 0) return v;
            return (int)((long)v * p / 100);
        }

        public static bool SlotIsWeapon(int i) { return i < WEAPON_SLOTS; }

        // ---- 設定画面用 ----

        public static string PercentLabel(ConfigEntry<int> e)
        {
            int p = e != null ? e.Value : 100;
            return p == 100 ? "100 % (本体どおり)" : p + " %";
        }

        public static bool PercentChanged(ConfigEntry<int> e) { return e != null && e.Value != 100; }

        public static void CyclePercent(ConfigEntry<int> e, int delta)
        {
            if (e == null) return;
            int p = (e.Value / STEP_PCT) * STEP_PCT + delta;
            if (p < MIN_PCT) p = MIN_PCT;
            if (p > MAX_PCT) p = MAX_PCT;
            e.Value = p;
        }

        public static string LastExpLabel()
        {
            if (LastExp < 0) return "(まだ戦っていない)";
            string s = LastExp + " 点";
            if (IsEnabled)
            {
                string conv = "";
                if (Pct(HpPercent) != 100) conv += " HP " + Scale(LastExp, HpPercent);
                if (Pct(WeaponPercent) != 100) conv += " 技 " + Scale(LastExp, WeaponPercent);
                if (Pct(MagicPercent) != 100) conv += " 術 " + Scale(LastExp, MagicPercent);
                if (Pct(PartyPercent) != 100) conv += " 全体 " + Scale(LastExp, PartyPercent);
                if (conv.Length > 0) s += " (換算:" + conv + ")";
            }
            return s;
        }

        // ---- 技・術レベルの書き直し (本体 Battle.check_skill と同じ手順。D0 だけ枠ごとの % にする) ----

        private static readonly Func<Battle, int> s_calcSkill = MakeIntCall("calc_skill");
        private static readonly Func<Battle, int> s_dialogSkill = MakeIntCall("dialog_skill");

        private static Func<Battle, int> MakeIntCall(string name)
        {
            var m = AccessTools.Method(typeof(Battle), name);
            if (m == null) { Plugin.Log.LogError("[PGrowth] Battle." + name + " not found"); return null; }
            return (Func<Battle, int>)Delegate.CreateDelegate(typeof(Func<Battle, int>), m);
        }

        public static bool CanReplaceCheckSkill { get { return s_calcSkill != null && s_dialogSkill != null; } }

        /// <summary>本体 check_skill の置き換え。戻り値は本体と同じ (1 = この仲間の分は終わり)。</summary>
        public static int CheckSkill(Battle b)
        {
            int[] XA0 = b.XA0;
            int ring = 0;
            for (int i = 0; i < 4; i++)
            {
                if (XA0[81 + i] == 180) { ring = b.got_exp / 2; break; }
            }
            int[] p = b.proc[b.procstg];
            if (p[0] == 0)
            {
                int kinds = 0;
                for (int i = 0; i < SLOTS; i++) if (XA0[49 + i] != 0) kinds++;
                b.d0 = kinds;
                if (kinds == 0) return 1;
                int baseExp = b.got_exp + ring;
                for (int i = p[1]; i < SLOTS; i++)
                {
                    if (XA0[49 + i] != 0)
                    {
                        int D0 = Scale(baseExp, SlotIsWeapon(i) ? WeaponPercent : MagicPercent) / kinds;
                        int D1 = XA0[49 + i] + D0;
                        b.D0 = D0;
                        b.D1 = D1;
                        if (XA0[106 + i * 2] <= D1)
                        {
                            int r = s_calcSkill(b);     // d0 を枠の番号に書き換え、レベル +1 と必要量の更新、ダイアログの開始 (3) か上限 (1)
                            if (r == 3)
                            {
                                p[0] = 10;
                                break;
                            }
                        }
                        else
                        {
                            XA0[106 + i * 2] -= D1;
                        }
                    }
                    p[1]++;
                }
            }
            if (p[0] == 10 && b.animeflg == 0)
            {
                if (s_dialogSkill(b) == 1)
                {
                    p[0] = 0;
                    p[1]++;
                }
            }
            int result = 0;
            if (p[1] >= SLOTS)
            {
                for (int i = 0; i < SLOTS; i++) XA0[49 + i] = 0;
                result = 1;
            }
            return result;
        }
    }

    /// <summary>戦闘後の成長の入口 (1 戦 1 回)。技術点を覚えてログに出す。</summary>
    [HarmonyPatch(typeof(Battle), "grow_party")]
    internal static class PlayerGrowth_grow_party_Patch
    {
        private static bool s_announced;

        private static void Prefix(Battle __instance)
        {
            int e = __instance.got_exp;
            PlayerGrowth.LastExp = e;
            if (!s_announced)
            {
                s_announced = true;
                Plugin.Log.LogInfo("[PGrowth] patched: Battle.grow_party / check_hp / check_skill / check_skillmem (replace check_skill = "
                    + PlayerGrowth.CanReplaceCheckSkill + ")");
            }
            if (!PlayerGrowth.IsEnabled) return;
            Plugin.Log.LogInfo("[PGrowth] exp " + e + " -> hp " + PlayerGrowth.Scale(e, PlayerGrowth.HpPercent)
                + " / weapon " + PlayerGrowth.Scale(e, PlayerGrowth.WeaponPercent)
                + " / magic " + PlayerGrowth.Scale(e, PlayerGrowth.MagicPercent)
                + " / party " + PlayerGrowth.Scale(e, PlayerGrowth.PartyPercent));
        }
    }

    /// <summary>HP: 処理の間だけ got_exp を HpPercent で読み替える。</summary>
    [HarmonyPatch(typeof(Battle), "check_hp")]
    internal static class PlayerGrowth_check_hp_Patch
    {
        private static void Prefix(Battle __instance, out int __state)
        {
            __state = __instance.got_exp;
            if (PlayerGrowth.IsEnabled) __instance.got_exp = PlayerGrowth.Scale(__state, PlayerGrowth.HpPercent);
        }

        private static void Postfix(Battle __instance, int __state)
        {
            __instance.got_exp = __state;
        }
    }

    /// <summary>技・術レベル: 武器と術で別の % にするため本体を止めて書き直しを走らせる。両方 100 % なら本体のまま。</summary>
    [HarmonyPatch(typeof(Battle), "check_skill")]
    internal static class PlayerGrowth_check_skill_Patch
    {
        private static bool Prefix(Battle __instance, ref int __result)
        {
            if (!PlayerGrowth.IsEnabled || !PlayerGrowth.CanReplaceCheckSkill) return true;
            if (PlayerGrowth.WeaponPercent.Value == 100 && PlayerGrowth.MagicPercent.Value == 100) return true;
            __result = PlayerGrowth.CheckSkill(__instance);
            return false;
        }
    }

    /// <summary>全体レベル: 枠 n に配る点 val を PartyPercent で読み替える。</summary>
    [HarmonyPatch(typeof(Battle), "check_skillmem")]
    internal static class PlayerGrowth_check_skillmem_Patch
    {
        private static void Prefix(int n, ref int val)
        {
            if (!PlayerGrowth.IsEnabled) return;
            val = PlayerGrowth.Scale(val, PlayerGrowth.PartyPercent);
        }
    }
}
