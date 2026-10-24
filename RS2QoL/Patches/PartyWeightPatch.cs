using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 防具の重さと、それを引いた「戦闘での素早さ」。
    ///
    /// 本体の事実 (逆コンパイルで確認):
    ///   - 倉庫の体重計 (Core.appnew_ivent 命令 2) は各仲間の防具 4 枠 (memberdata 81..84) の重さの合計を出す。
    ///     鎧類 (品物 96..191) は armor_data[+11]、盾 (192..207) は shield_data[+2] の下位 3 bit。武器の重さは本体はどこでも読まない。
    ///   - 戦闘開始時 (Battle.calc_ability) に重さの合計 ÷ 2 (切り捨て) を素早さ (memberdata 101) から引く (0 未満は 0)。
    ///     この素早さが命中・回避・行動順に使われる。
    ///   - メニューのステータス画面 (Menu.StatusDisply) は memberdata[132] + 10 を大きく、memberdata[101] との差を「+N」で出すだけで、
    ///     重さは引かない (スーファミ版からそう。スマホ版の上書きではない)。
    ///
    /// ここでは (1) どこでもアバロンの 2 ページ目 (どこでも体重計) 用の数字と名前と絵、(2) ステータス画面の素早さを重さを引いた値にする
    /// Postfix ([Menu] StatusAgilityAfterWeight、既定 true) を持つ。
    /// </summary>
    internal static class PartyWeight
    {
        public static ConfigEntry<bool> StatusEnabled;

        private const int SLOT_FIRST = 81, SLOTS = 4;      // 防具の枠 (頭・体・腕・足・盾のうち 4 つ)
        private const int ARMOR_FIRST = 96, SHIELD_FIRST = 192, ITEM_NONE = 255;
        private const int ARMOR_STRIDE = 12, ARMOR_WEIGHT = 11, SHIELD_STRIDE = 3, SHIELD_FLAGS = 2;
        private const int STAT_AGI = 101, BASE_AGI = 132;

        public static void Bind(ConfigFile config)
        {
            StatusEnabled = config.Bind("Menu", "StatusAgilityAfterWeight", true,
                "ステータス画面の素早さの小さい数字 (+N) を、防具の上昇から重さ ÷ 2 を引いた戦闘での増減 (+N / -N) にする。大きい数字 + 小さい数字 = 戦闘で使われる素早さ。本体は上昇だけ出して重さを引かない");
        }

        /// <summary>PatchAll の後に呼ぶ: Menu.StatusDisply に Postfix が付いたかをログに出す。</summary>
        public static void LogPatched()
        {
            System.Reflection.MethodBase target = AccessTools.Method(typeof(Menu), "StatusDisply", new[] { typeof(CSpriteStudioObject), typeof(int), typeof(int) });
            HarmonyLib.Patches info = target != null ? Harmony.GetPatchInfo(target) : null;
            bool ok = info != null && info.Postfixes != null && info.Postfixes.Count > 0;
            if (ok) Plugin.Log.LogInfo("[Weight] patched: Menu.StatusDisply (status agility after weight, enabled=" + (StatusEnabled != null && StatusEnabled.Value) + ")");
            else Plugin.Log.LogWarning("[Weight] NOT patched: Menu.StatusDisply");
        }

        /// <summary>仲間の数 (本体の chk_flag_34h と同じ: ひとり旅 partydata[149] なら 1 人)。</summary>
        public static int Count()
        {
            int n = (Work.partydata[149] == 0) ? Work.partydata[24] : 1;
            return Mathf.Clamp(n, 0, 5);
        }

        /// <summary>防具 4 枠の重さの合計 (本体の体重計と同じ計算)。</summary>
        public static int Weight(int[] m)
        {
            int w = 0;
            for (int s = 0; s < SLOTS; s++)
            {
                int item = m[SLOT_FIRST + s];
                if (item == ITEM_NONE || item < ARMOR_FIRST) continue;
                if (item < SHIELD_FIRST) w += Data.armor_data[(item - ARMOR_FIRST) * ARMOR_STRIDE + ARMOR_WEIGHT] & 0xFF;
                else w += Data.shield_data[(item - SHIELD_FIRST) * SHIELD_STRIDE + SHIELD_FLAGS] & 7;
            }
            return w;
        }

        /// <summary>素の素早さ (ステータス画面の大きい数字)。</summary>
        public static int BaseAgility(int[] m) { return m[BASE_AGI] + 10; }

        /// <summary>
        /// 防具の素早さの上昇の合計 (Battle.calc_bonus と同じ: armor_data[+10] の bit 0x08 が素早さ、量は (下位 2 bit) + 1。盾には無い)。
        /// memberdata[101] は本体がメニューを開いた時 (Menu.menu_init) にしか組み直さないので、そこは見ずにここで足す。
        /// </summary>
        public static int ArmorAgilityBonus(int[] m) { return ArmorBonus(m, 0x08); }

        /// <summary>
        /// 防具の能力値上昇の合計 (Battle.calc_bonus と同じ: armor_data[+10] の bit が能力値 (0x80 腕力 / 0x40 器用さ / 0x20 魔力 / 0x10 理力 / 0x08 素早さ / 0x04 体力)、
        /// 量は (下位 2 bit) + 1。盾には無い)。
        /// </summary>
        public static int ArmorBonus(int[] m, int bit)
        {
            int b = 0;
            for (int s = 0; s < SLOTS; s++)
            {
                int item = m[SLOT_FIRST + s];
                if (item < ARMOR_FIRST || item >= SHIELD_FIRST) continue;
                int f = Data.armor_data[(item - ARMOR_FIRST) * ARMOR_STRIDE + 10] & 0xFF;
                if ((f & bit) != 0) b += (f & 3) + 1;
            }
            return b;
        }

        /// <summary>装備の上昇を足した素早さ (本体の memberdata[101] に当たる値。重さは引いていない)。</summary>
        public static int MenuAgility(int[] m) { return BaseAgility(m) + ArmorAgilityBonus(m); }

        // ---- 理力計 (理力 = SFC 版以来の俗称「魅力」。本体の文章では「理力」だけ。ステータス画面に出ない隠しの能力値 memberdata[131] (素質) → 戦闘では [100] = 素質 + 10 + 防具の上昇) ----
        // 本体の理力計 (Core.appnew_ivent 命令 1、術法研究所の器具) は 素質 + 10 + 防具の上昇 (bit 0x10) を 99 で頭打ちにして出す。
        // 戦闘 (Battle.cs) で理力が効く所:
        //   攻撃術の威力 (damage07) = (魔力 + (魔力 − 理力) × 2) × 技の倍率 / 4 … 理力 ≥ 魔力 なら魔力のまま。つまり理力が魔力より低いほど強い
        //   冥術 ヘルファイア / ポイゾナスブロウ / サンドストーム (damage13) = (魔力 + 理力) × …、ペイン (damage10) = 理力 × … は逆に理力で強くなる
        //   吸収技 (サクション / 吸血 / 体力吸収) と LP 吸収 (ライフスティール / 死神のカマ / 黒点破) の命中に理力を足す。即死・石化への抵抗にも理力
        private const int BASE_MAGIC = 130, BASE_CHARM = 131;

        /// <summary>素の理力 (素質 + 10)。</summary>
        public static int BaseCharm(int[] m) { return m[BASE_CHARM] + 10; }
        /// <summary>防具の上昇を足した理力 (戦闘で使われる値。本体の理力計はこれを 99 で頭打ちにして出す)。</summary>
        public static int MenuCharm(int[] m) { return BaseCharm(m) + ArmorBonus(m, 0x10); }
        /// <summary>防具の上昇を足した魔力 (戦闘で使われる値。memberdata[99] はメニューを開いた時にしか組み直されないので自分で足す)。</summary>
        public static int MenuMagic(int[] m) { return m[BASE_MAGIC] + 10 + ArmorBonus(m, 0x20); }
        /// <summary>攻撃術の威力の基礎値 (Battle.damage07 の 魔力 + (魔力 − 理力) × 2。理力 ≥ 魔力 なら魔力)。</summary>
        public static int SpellBase(int[] m)
        {
            int mag = MenuMagic(m), charm = MenuCharm(m);
            return mag + (mag >= charm ? (mag - charm) * 2 : 0);
        }
        /// <summary>攻撃術の威力が魔力だけの時の何倍か (1.0 = 補正なし)。表示用。</summary>
        public static float SpellRatio(int[] m)
        {
            int mag = MenuMagic(m);
            return mag > 0 ? (float)SpellBase(m) / mag : 1f;
        }
        /// <summary>戦闘で使われる素早さ (Battle.calc_ability と同じ: 重さ ÷ 2 を引いて 0 未満は 0)。</summary>
        public static int BattleAgility(int[] m) { return Mathf.Max(0, MenuAgility(m) - Weight(m) / 2); }

        /// <summary>仲間の名前 (本体の put_human_name2 と同じ: 皇帝 (256 / 257) は partydata[688..] の文字コード、それ以外は名前表)。</summary>
        public static string MemberName(int i)
        {
            try
            {
                int[] m = Work.memberdata[i];
                int id = m[0] & 0x1FF;
                if (id == 256 || id == 257)
                {
                    string tbl = Window.fonttbl;
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(8);
                    for (int k = 0; k < 8; k++)
                    {
                        int code = Work.partydata[688 + k];
                        if (code >= 8192) code -= 7936;             // 2 バイト形 (put_moji2 と同じ)
                        if (code <= 0) continue;
                        if (code == 230) { sb.Append(' '); continue; }
                        if (code < tbl.Length) sb.Append(tbl[code]);
                    }
                    return sb.ToString().TrimEnd();
                }
                return Main.menu.get_name(2, m[0] & 0x7FFF);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Weight] member name failed: " + e.Message);
                return "";
            }
        }

        // ---------------------------------------------------------------- 絵

        /// <summary>
        /// 本体の手順 (Core.storeSdImage / Menu.storePcImage) で絵の帯を組み、正面の 1 コマ (x=0, 26x26) を Texture2D に写す。
        /// 仲間は obj = memberdata[2]、pal = memberdata[0] (32 未満と 203 / 204 は pcpal を pal で引く。それ以外は sd_tbl でパレットが決まり pal は使われない)。
        /// 失敗したら null。
        /// </summary>
        public static Texture2D BuildFrontFrame(int obj, int pal, string name)
        {
            const int FRAME = 26;
            try
            {
                obj &= 0xFF;
                int no = obj > 230 ? 0 : obj;                        // 本体と同じ (範囲外は 0 番)
                ImageEfx efx = new ImageEfx();
                efx.init(4, no, 16);
                Data.setObjPalette(efx, obj, pal);
                efx.storeImage();
                Image img = efx.img;
                if (img == null || img.m_px == null || img.m_palette == null || img.m_width < FRAME || img.m_height < FRAME)
                {
                    Plugin.Log.LogWarning("[Weight] image unavailable (obj " + obj + ")");
                    return null;
                }
                int w = img.m_width;
                Texture2D t = new Texture2D(FRAME, FRAME, TextureFormat.ARGB32, false);
                t.name = name;
                t.hideFlags = HideFlags.HideAndDontSave;
                t.filterMode = FilterMode.Point;
                t.wrapMode = TextureWrapMode.Clamp;
                Color32[] pix = new Color32[FRAME * FRAME];
                for (int y = 0; y < FRAME; y++)
                {
                    for (int x = 0; x < FRAME; x++)
                    {
                        int idx = img.m_px[y * w + x];                  // GIF の並び (上の行から)
                        Color32 c = img.m_palette[idx];
                        if (idx == 0) c.a = 0;                           // 0 番は透明
                        pix[(FRAME - 1 - y) * FRAME + x] = c;           // Texture2D は下の行から
                    }
                }
                t.SetPixels32(pix);
                t.Apply();
                efx.killImage();                                        // 本体側の Image (テクスチャ 2 枚) は捨てる
                return t;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Weight] image failed (obj " + obj + "): " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// BuildFrontFrame と同じ帯から、コマ番号 pats (x = 番号 × 26) のコマを順に Texture2D にする (仲間候補の窓の「回って構える」動き用)。
        /// 帯に無い番号は null。帯が組めなければ null。
        /// </summary>
        public static Texture2D[] BuildFrames(int obj, int pal, int[] pats, string name)
        {
            const int FRAME = 26;
            try
            {
                obj &= 0xFF;
                int no = obj > 230 ? 0 : obj;
                ImageEfx efx = new ImageEfx();
                efx.init(4, no, 16);
                Data.setObjPalette(efx, obj, pal);
                efx.storeImage();
                Image img = efx.img;
                if (img == null || img.m_px == null || img.m_palette == null || img.m_height < FRAME)
                {
                    Plugin.Log.LogWarning("[Weight] image unavailable (obj " + obj + ")");
                    return null;
                }
                int w = img.m_width;
                Texture2D[] frames = new Texture2D[pats.Length];
                for (int p = 0; p < pats.Length; p++)
                {
                    int x0 = pats[p] * FRAME;
                    if (x0 < 0 || x0 + FRAME > w) continue;
                    Texture2D t = new Texture2D(FRAME, FRAME, TextureFormat.ARGB32, false);
                    t.name = name + pats[p];
                    t.hideFlags = HideFlags.HideAndDontSave;
                    t.filterMode = FilterMode.Point;
                    t.wrapMode = TextureWrapMode.Clamp;
                    Color32[] pix = new Color32[FRAME * FRAME];
                    for (int y = 0; y < FRAME; y++)
                    {
                        for (int x = 0; x < FRAME; x++)
                        {
                            int idx = img.m_px[y * w + x0 + x];
                            Color32 c = img.m_palette[idx];
                            if (idx == 0) c.a = 0;
                            pix[(FRAME - 1 - y) * FRAME + x] = c;
                        }
                    }
                    t.SetPixels32(pix);
                    t.Apply();
                    frames[p] = t;
                }
                efx.killImage();
                return frames;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Weight] frames failed (obj " + obj + "): " + e.Message);
                return null;
            }
        }

        private static readonly Texture2D[] s_member = new Texture2D[5];
        private static readonly int[] s_memberKey = new int[5];

        /// <summary>仲間 i の正面の絵 (絵の番号とパレットが変わらない限り使い回す)。無ければ null。</summary>
        public static Texture2D MemberFrame(int i)
        {
            if (i < 0 || i >= 5 || Work.memberdata == null || Work.memberdata[i] == null) return null;
            int obj = Work.memberdata[i][2] & 0xFF, pal = Work.memberdata[i][0] & 0xFFFF;
            int key = (obj << 16) | pal | 0x40000000;                   // 0 と区別するため上位 bit を立てる
            if (s_member[i] != null && s_memberKey[i] == key) return s_member[i];
            if (s_member[i] != null) UnityEngine.Object.Destroy(s_member[i]);
            s_member[i] = BuildFrontFrame(obj, pal, "rs2qol_member" + i);
            s_memberKey[i] = key;
            if (s_member[i] != null) Plugin.Log.LogInfo("[Weight] member " + i + " image ready (obj " + obj + ", pal " + pal + ")");
            return s_member[i];
        }
    }

    /// <summary>
    /// 診断: 本体の新規イベント命令 (Core.appnew_ivent。倉庫の体重計 = 命令 2、「力」の計器 = 命令 1) が呼ばれたら番号をログに出す。
    /// どの場所でどの命令が使われているかはイベントのデータ側にあって逆コンパイルでは追えないので、実機で見る。
    /// </summary>
    [HarmonyPatch(typeof(Core), "appnew_ivent")]
    internal static class NewEventDiagPatch
    {
        private static void Prefix(Core __instance)
        {
            if (Plugin.DiagEnabled == null || !Plugin.DiagEnabled.Value) return;
            if (__instance.ivent_no == null || __instance.ivent_no.Length < 2) return;
            Plugin.Log.LogInfo("[Weight] new event command " + __instance.ivent_no[1] + " (map " + Main.map.mapno + ")");
        }
    }

    /// <summary>
    /// Menu.StatusDisply(sprite, no, flg) の Postfix: 素早さの小さい数字 (mtxt_cmd_257_004 の「+」と sttxt_upspee) を
    /// 装備による増減 (防具の上昇 − 重さ ÷ 2。+N / -N、0 なら空) にする。大きい数字 (素の値) は本体のまま。
    /// 本体はこの関数を画面を開いた時に 1 回だけ呼ぶ (m_is_init_status) ので、書き換えはそのまま残る。皇帝の状態画面も同じ関数。
    /// </summary>
    [HarmonyPatch(typeof(Menu), "StatusDisply", typeof(CSpriteStudioObject), typeof(int), typeof(int))]
    internal static class StatusAgilityPatch
    {
        private static bool s_logged;

        private static void Postfix(Menu __instance, CSpriteStudioObject sprite, int no, int flg)
        {
            if (PartyWeight.StatusEnabled == null || !PartyWeight.StatusEnabled.Value || sprite == null) return;
            try
            {
                // 読み方は魔力などと同じ: 大きい数字 = 素の値 (本体のまま)、小さい数字 = 装備による増減 (防具の上昇 − 重さ ÷ 2)。
                // 「+」は本体の文字、「-」は同じ場所に直接書く (小さい数字の列が他の行とそろう)
                int[] m = __instance.t_member[no];
                int baseAgi = PartyWeight.BaseAgility(m);
                int battle = PartyWeight.BattleAgility(m);
                int delta = battle - baseAgi;
                CSpriteAnalyze plus = sprite.GetAnalyzeData("mtxt_cmd_257_004");
                CSpriteAnalyze small = sprite.GetAnalyzeData("sttxt_upspee");
                if (plus == null || small == null) return;
                if (flg == 0 && delta > 0) { plus.DrawString = plus.Data.GetMessageText(); small.DrawString = delta.ToString(); }
                else if (flg == 0 && delta < 0) { plus.DrawString = "－"; small.DrawString = (-delta).ToString(); }   // 全角の「－」(半角の - は細くて「+」と幅が合わない)
                else { plus.DrawString = " "; small.DrawString = " "; }
                if (!s_logged)
                {
                    s_logged = true;
                    string plusText = plus.Data != null ? plus.Data.GetMessageText() : "?";
                    string plusCode = plusText.Length > 0 ? ((int)plusText[0]).ToString("X4") : "-";
                    Plugin.Log.LogInfo("[Weight] applied: status agility " + PartyWeight.MenuAgility(m) + " -> " + battle + " (base " + baseAgi + ", weight " + PartyWeight.Weight(m) + ", plus glyph U+" + plusCode + ")");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Weight] status agility failed: " + e.Message);
            }
        }
    }
}
