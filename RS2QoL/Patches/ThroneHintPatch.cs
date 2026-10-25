using BepInEx.Configuration;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 玉座のヒント ([Avalon] ThroneHint、既定 On)。「今、玉座に座ると何かが起こるか」を本体のフラグから読んで、
    ///  (1) どこでもアバロンの報告書に 1 行 (「玉座」 座ると何かが起こりそうです / 今は何も起こりません)、
    ///  (2) 起こらない → 起こる に変わった時、玉座の間の女官 (map 24 (43,14) msg 81、絵は obj 116) が本体の台詞
    ///      「陛下、たまには玉座でひと息リラックスしてはいかがですか。」をそのまま言う小窓 (開発所の知らせと同じ) を出す。
    /// 何回で起こるかは出さない (ユーザー決定 2026-10-25)。本体の説明書も「アバロンに戻ったときは、マメに座ってみるといいでしょう」と
    /// しか言わないので、ヒントは「今か否か」だけ。
    ///
    /// 本体の事実 (script.bin、tools/script-analysis/out/ops.txt の msg 80 = 玉座の床イベントと、そこから CALL される各 msg。2026-10-25 調査):
    ///  msg 80 は flag125 bit0 が立っていなければ msg 756 (座って「ふー」だけ)。立っていれば物語の分岐 5 つを順に見て、
    ///  どれでもなければ座る (EV SPECIAL 240) → CALL 750, 748, 751, 752, 745, 746, 747, 749, 3088 の順に条件を見る。
    ///  各 msg は条件が立たなければ END で戻るだけ。どれも立たなければ「ふー」。
    ///   - 物語: flag65 in 1..2 → 742 (先帝の無念)、flag114 bit0 → 744 (やるぞ)、flag129 == 9 → 741 (ゴブリンの住みか)、
    ///     flag123 == 3 → 736 (ヴィクトール運河 → 南バレンヌ進出)、flag138 == 13 → 743 (ほうび)。
    ///   - 750 最終決戦: flag190 == 14 → ALL_END (以降は何も見ない)、== 13 → msg 1252 (「地下から‥」)、1..15 → END、
    ///     flag179 ≤ 9 → END、皇帝 (memberdata[0][2]) が 32 / 33 (最終皇帝) なら flag190 = 1 (見た目は無い解禁。ヒントには含めない、ユーザー 2026-10-25)。
    ///   - 748 大氷原の報告: flag39 bit0 が 0 で、七英雄を倒した数 (flag88 + flag89 の立っている bit の数) ≥ 6。
    ///   - 751 使者 (→ msg 110): flag167 bit1 が 0、(flag166 == 7、または flag122 bit1 が 0 かつ flag120 == 15)、flag165 が 12..15 でない、flag164 in 6..9。
    ///   - 752 使者 (→ msg 110): flag39 bit1 が 0、flag164 が 6..15 でない、flag161 == 15、最終皇帝、(751 と同じ 166/122/120 の条件)、flag165 が 12..15 でない。
    ///   - 施設 (どれも flag128 == 13 なら無し): 745 術研究所 = flag132 が 14..15 でない、flag123 ≥ 4、冷却 flag132 == 0、
    ///     (7 + 皇帝の術レベルの合計 memberdata[0][21]) / 16 ≥ 2 (= 合計 25 以上)。746 大学 = flag134 が 14..15 でない、flag123 ≥ 4、冷却 flag134 == 0、
    ///     併合した地域の数 (flag4..7 の bit の数) ≥ 5。747 兵団 = flag142 が 14..15 でない、冷却 flag142 == 0、地域 ≥ 9。
    ///     749 新市街 = flag135 が 14..15 でない、flag4 bit3 と flag6 bit3 と flag7 bit0、冷却 flag135 == 0。
    ///     冷却 = 提案した時に 13、以後は座るたび 3 ずつ減る (FLAG_DEC は 0 で止まる) → 断っても 5 回座れば再提案。建設すると 14。
    ///   - 3088 アバロンの木: flag123 ≥ 2、flag128 != 13、Sys.pcf bit5 (本体の追加コンテンツ 0。Steam 版は購入済)、
    ///     new_flag 1 (木の段階) が 2..3 / 5..6 / 8..15 のどれでもない (= 0, 1, 4, 7)、冷却 new_flag 3 == 0 (提案で 13、座るたび 3 減)。
    ///  フラグの読みは本体と同じニブル (ScriptReader.Flag / NewFlag)。読むだけで書かない。
    ///
    /// 知らせを出す時: フィールドで自由に動ける瞬間 (Wallet.IsFieldFree) に毎フレーム判定し、前回 false → 今回 true の時だけ。
    /// タイトル (proc[0] != 1) に戻ったら前回の値を忘れ、次にフィールドに出た最初の判定は知らせずに控えるだけ
    /// (ロードした時点で既に起こる状態なら小窓は出さず、報告書の行だけ)。MOD の窓や他の知らせが出ている間は待つ。
    /// </summary>
    internal static class ThroneHint
    {
        public static ConfigEntry<bool> Enabled;

        public const string NAME = "玉座の間";
        public const string LADY_LINE = "陛下、たまには玉座でひと息リラックスしてはいかがですか。";   // msg 81 の台詞そのまま (改行を外した)
        public const string ROW_LABEL = "玉座";
        public const string ROW_YES = "座ると何かが起こりそうです";
        public const string ROW_NO = "今は何も起こりません";
        private const int LADY_OBJ = 116;          // アバロン城 (map 24) の玉座の間 (43,14) の女官 (msg 81) の絵の番号 (maptbl.bin の obj)
        private const float SECONDS = 6f;
        private const float ANIM_SECONDS = 8f / 30f;

        private static int s_last = -1;            // 前回の判定 (−1 = まだ控えていない)
        private static string s_lastWhy = "";
        private static Texture2D s_lady;
        private static bool s_ladyTried;
        private static float s_shown;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Avalon", "ThroneHint", true,
                "玉座に座ると何かが起こる時、どこでもアバロンの報告書に「玉座: 座ると何かが起こりそうです」と出し、"
                + "そうなった瞬間に玉座の間の女官が「たまには玉座でひと息…」と言う小窓を出す。何回で起こるかは出さない。false なら行も小窓も出さない");
        }

        public static bool On { get { return Enabled != null && Enabled.Value; } }

        /// <summary>今の報告書の行の文 (On でなければ null)。</summary>
        public static string RowText()
        {
            if (!On || Work.partydata == null) return null;
            string why;
            return Evaluate(out why) ? ROW_YES : ROW_NO;
        }

        // ---------------------------------------------------------------- 判定 (msg 80 の読み替え)

        private static int Bits(int v) { v &= 0xF; return (v & 1) + ((v >> 1) & 1) + ((v >> 2) & 1) + ((v >> 3) & 1); }

        private static bool In(int v, int lo, int hi) { return v >= lo && v <= hi; }

        /// <summary>今、玉座に座ると何かが起こるか。why = 起こる物の短い名前 (ログ用。起こらなければ理由)。</summary>
        public static bool Evaluate(out string why)
        {
            int[] m = Work.memberdata != null && Work.memberdata.Length > 0 ? Work.memberdata[0] : null;
            if (Work.partydata == null || Work.partydata.Length < 1700 || m == null || m.Length < 35) { why = "no data"; return false; }
            int cls = m[2] & 0xFF;
            bool finalEmperor = cls == 32 || cls == 33;
            if ((ScriptReader.Flag(125) & 1) == 0) { why = "flag125 bit0 = 0 (msg 756)"; return false; }
            // 物語の分岐 (座る前に飛ぶ)
            if (In(ScriptReader.Flag(65), 1, 2)) { why = "742 先帝の無念"; return true; }
            if ((ScriptReader.Flag(114) & 1) != 0) { why = "744 やるぞ"; return true; }
            if (ScriptReader.Flag(129) == 9) { why = "741 ゴブリンの住みか"; return true; }
            if (ScriptReader.Flag(123) == 3) { why = "736 ヴィクトール運河"; return true; }
            if (ScriptReader.Flag(138) == 13) { why = "743 ほうび"; return true; }
            // 750 最終決戦
            int f190 = ScriptReader.Flag(190);
            if (f190 == 14) { why = "flag190 = 14 (ALL_END)"; return false; }
            if (f190 == 13) { why = "1252 最終決戦"; return true; }
            // flag190 == 0 && flag179 >= 10 && 最終皇帝 → 750 が flag190 = 1 にするが、画面上は何も起きないのでヒントには含めない (ユーザー 2026-10-25)
            // 748 大氷原
            if ((ScriptReader.Flag(39) & 1) == 0 && Bits(ScriptReader.Flag(88)) + Bits(ScriptReader.Flag(89)) >= 6) { why = "748 大氷原の報告"; return true; }
            // 751 / 752 使者 (msg 110)
            bool pass = ScriptReader.Flag(166) == 7 || ((ScriptReader.Flag(122) & 2) == 0 && ScriptReader.Flag(120) == 15);
            bool not165 = !In(ScriptReader.Flag(165), 12, 15);
            int f164 = ScriptReader.Flag(164);
            if ((ScriptReader.Flag(167) & 2) == 0 && pass && not165 && In(f164, 6, 9)) { why = "751 使者"; return true; }
            if ((ScriptReader.Flag(39) & 2) == 0 && !In(f164, 6, 15) && ScriptReader.Flag(161) == 15 && finalEmperor && pass && not165) { why = "752 使者"; return true; }
            // 施設の提案
            if (ScriptReader.Flag(128) != 13)
            {
                int f123 = ScriptReader.Flag(123);
                int regions = Bits(ScriptReader.Flag(4)) + Bits(ScriptReader.Flag(5)) + Bits(ScriptReader.Flag(6)) + Bits(ScriptReader.Flag(7));
                int f132 = ScriptReader.Flag(132), f134 = ScriptReader.Flag(134), f142 = ScriptReader.Flag(142), f135 = ScriptReader.Flag(135);
                int magic = m[21] & 0xFF;   // 皇帝の術レベルの合計 (Menu._lp_cal が memberdata[29..34] の合計を入れる)。script が読むのもこの欄なのでそのまま読む
                if (!In(f132, 14, 15) && f123 >= 4 && f132 == 0 && (7 + magic) / 16 >= 2) { why = "745 術研究所 (術 " + magic + ")"; return true; }
                if (!In(f134, 14, 15) && f123 >= 4 && f134 == 0 && regions >= 5) { why = "746 大学 (地域 " + regions + ")"; return true; }
                if (!In(f142, 14, 15) && f142 == 0 && regions >= 9) { why = "747 兵団 (地域 " + regions + ")"; return true; }
                if (!In(f135, 14, 15) && (ScriptReader.Flag(4) & 8) != 0 && (ScriptReader.Flag(6) & 8) != 0 && (ScriptReader.Flag(7) & 1) != 0 && f135 == 0) { why = "749 新市街"; return true; }
                int tree = ScriptReader.NewFlag(1), cool = ScriptReader.NewFlag(3);
                if (f123 >= 2 && (Sys.pcf & 32) != 0 && !In(tree, 2, 3) && !In(tree, 5, 6) && !In(tree, 8, 15) && cool == 0) { why = "3088 アバロンの木 (段階 " + tree + ")"; return true; }
                why = "nothing (flag123 " + f123 + " regions " + regions + " magic " + magic + " cool " + f132 + "/" + f134 + "/" + f142 + "/" + f135 + " tree " + tree + "/" + cool + ")";
                return false;
            }
            why = "flag128 = 13";
            return false;
        }

        // ---------------------------------------------------------------- 毎フレーム (Plugin.OnGUI。Wallet.DrawNotice の前)

        public static void Tick()
        {
            try { TickInner(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("[ThroneHint] failed: " + e.Message); s_last = -1; }
        }

        private static void TickInner()
        {
            Core c = Main.core;
            if (c == null || c.proc == null || Work.partydata == null) { s_last = -1; return; }
            if (Main.proc != 2 || c.proc[0] != 1) { if (s_last != -1) { s_last = -1; if (Plugin.DiagOn) Plugin.Log.LogInfo("[ThroneHint] baseline cleared (title)"); } return; }
            if (!Wallet.IsFieldFree(c) || SoftReset.Active) return;
            if (ConfigMenu.IsOpen || TreasuryScreen.Active || BattleReview.IsOpen || RecruitInfo.IsOpen || MourningScene.IsOpen || Wallet.NoticeVisible) return;
            string why;
            int now = Evaluate(out why) ? 1 : 0;
            if (s_last == -1)
            {
                s_last = now;
                s_lastWhy = why;
                Plugin.Log.LogInfo("[ThroneHint] baseline: " + (now == 1 ? "event ready: " : "none: ") + why);
                return;
            }
            if (now == s_last) { if (why != s_lastWhy) { s_lastWhy = why; if (Plugin.DiagOn) Plugin.Log.LogInfo("[ThroneHint] state: " + why); } return; }
            s_last = now;
            s_lastWhy = why;
            Plugin.Log.LogInfo("[ThroneHint] applied: " + (now == 1 ? "event ready: " : "none: ") + why + " (enabled " + On + ")");
            if (now != 1 || !On) return;
            s_shown = Time.realtimeSinceStartup;
            if (HasLady) Wallet.ShowNotice(LADY_LINE, NAME, DrawLady, TreasuryScreen.MinisterDots, SECONDS);
            else Wallet.ShowNotice(LADY_LINE, NAME, false, SECONDS);
        }

        // ---------------------------------------------------------------- 女官の絵 (大臣と同じ組み方: 正面の 1 コマを左右反転で交互に)

        private static bool HasLady
        {
            get
            {
                if (!s_ladyTried)
                {
                    s_ladyTried = true;
                    s_lady = PartyWeight.BuildFrontFrame(LADY_OBJ, 0, "rs2qol_throne_lady");
                    Plugin.Log.LogInfo("[ThroneHint] lady image " + (s_lady != null ? "ready" : "unavailable (obj " + LADY_OBJ + ")"));
                }
                return s_lady != null;
            }
        }

        private static void DrawLady(Rect r, int dot)
        {
            if (s_lady == null) return;
            float spr = TreasuryScreen.MinisterDots * dot;
            Rect sr = new Rect(r.x, r.y + Mathf.Round((r.height - spr) / 2f), spr, spr);
            bool flip = ((int)((Time.realtimeSinceStartup - s_shown) / ANIM_SECONDS) & 1) != 0;
            Color col = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(sr, s_lady, flip ? new Rect(1f, 0f, -1f, 1f) : new Rect(0f, 0f, 1f, 1f), true);
            GUI.color = col;
        }
    }
}
