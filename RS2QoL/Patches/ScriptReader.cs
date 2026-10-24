using System.Collections.Generic;
using System.Text;

namespace RS2QoL.Patches
{
    /// <summary>
    /// スクリプト (script.bin) の会話文を、フラグで分岐しながら「読むだけ」で文字列にする。どこでも情報室のページが
    /// 情報官 (アバロン城 map 24 の (42,27) msg 82) の今の台詞を、城に行かずに組み立てるのに使う。
    ///
    /// 本体には「メッセージ番号 → 文字列」の関数が無く、Core.message_lop2 が 1 文字ずつ描きながら命令を実行する。
    /// ここではその命令のうち、文字 (fonttbl)・改行・CALL・JUMP・IF フラグ (51 / 52)・SKIP (78)・work 代入 (64 / 65)・
    /// APPNEW 0 (戦闘回数) / 242 (new_flag の範囲で次の命令を飛ばす) / 245 (new_fonttbl の 1 字) ・PAUSE / close_wind (段落の区切り)・
    /// END (呼び出し元へ戻る) だけを本体と同じ規則で辿り、フラグへの書き込み (53 / 54 / 55 / 56、APPNEW 241) は何もしない。
    /// 戦闘やマップ移動などのイベント命令や、選択肢・名前差し込みなど未対応の命令に当たったらそこで読むのをやめる。
    ///
    /// 本体の分岐の癖もそのまま: IF (51 / 52) は条件が偽なら飛び先 2 バイトを飛ばし、真ならその場に残る。その後、今の位置の
    /// バイトが 12 未満なら (= 飛び先が msg 番号、または偽の時に次の命令が CALL だった時) その msg へ戻り先を積まずに移る (Core.flag_sepa)。
    /// フラグは Work.partydata[97 + n / 2] の 4 ビット (偶数 n が上位)、new_flag は [1567 + id / 2] (Core.get_flag_atai / get_new_flag_atai)。
    /// </summary>
    internal static class ScriptReader
    {
        private const int MAX_STEPS = 20000, MAX_DEPTH = 16;

        public static int Flag(int n)
        {
            int v = Work.partydata[97 + n / 2];
            return (n & 1) != 0 ? v & 0xF : (v >> 4) & 0xF;
        }

        public static int NewFlag(int id)
        {
            int v = Work.partydata[1567 + id / 2];
            return (id & 1) != 0 ? v & 0xF : (v >> 4) & 0xF;
        }

        /// <summary>戦闘回数 (Work.partydata[686]。情報官の APPNEW 0 と Core.btl_counter_add が使う値)。</summary>
        public static int BattleCount { get { return Work.partydata != null && Work.partydata.Length > 686 ? Work.partydata[686] : 0; } }

        /// <summary>
        /// msg から読める段落 (PAUSE / close_wind で区切る。改行命令は '\n')。script.bin が無ければ null。
        /// stopped には未対応の命令で止まった時の説明 (無ければ null)。
        /// </summary>
        public static List<string> Read(int msg, out string stopped)
        {
            stopped = null;
            Core c = Main.core;
            byte[] s = c != null && c.mestbl != null ? c.mestbl[0] : null;
            string ft = Window.fonttbl, nft = Core.new_fonttbl;
            if (s == null || ft == null || Work.partydata == null) return null;
            int adrs, end;
            if (!ScriptText.Range(c, msg, out adrs, out end)) return null;

            List<string> paras = new List<string>();
            StringBuilder cur = new StringBuilder();
            int[] work = new int[256];
            List<int> stack = new List<int>();   // 戻り先 (adrs, end) の対
            int steps = 0;
            while (true)
            {
                if (++steps > MAX_STEPS) { stopped = "命令が多すぎる"; break; }
                if (adrs >= end)
                {
                    if (stack.Count == 0) break;
                    end = stack[stack.Count - 1]; adrs = stack[stack.Count - 2];
                    stack.RemoveRange(stack.Count - 2, 2);
                    continue;
                }
                int b = s[adrs] & 0xFF;
                if (b >= 80) { if (b < ft.Length) cur.Append(ft[b]); adrs++; continue; }
                if (b >= 32 && b <= 35)
                {
                    int k = b * 256 + (adrs + 1 < end ? s[adrs + 1] & 0xFF : 0);
                    if (k >= 8192) k -= 7936;
                    if (k < ft.Length) cur.Append(ft[k]);
                    adrs += 2; continue;
                }
                if (b == 36) { cur.Append('\n'); adrs++; continue; }
                if (b < 32)
                {
                    int a = b, v = adrs + 1 < end ? s[adrs + 1] & 0xFF : 0;
                    adrs += 2;
                    if (a <= 12)
                    {
                        // CALL: 戻り先を積んで msg へ (Core.message_start → message_r)
                        if (stack.Count / 2 >= MAX_DEPTH) { stopped = "呼び出しが深すぎる"; break; }
                        stack.Add(adrs); stack.Add(end);
                        if (!ScriptText.Range(c, a * 256 + v, out adrs, out end)) { stopped = "msg " + (a * 256 + v) + " が無い"; break; }
                        continue;
                    }
                    if (a == 13)
                    {
                        switch (v)
                        {
                            case 0:
                                if (BattleCount == 0) cur.Append("\n戦闘きろくがありません。");
                                else cur.Append(BattleCount.ToString()).Append("回の\n戦闘きろくがあります。");
                                break;
                            case 241: break;   // new_flag への書き込み: 読むだけなので何もしない
                            case 242:
                            {
                                int f = NewFlag(work[0]), lo = (work[1] >> 4) & 0xF, hi = work[1] & 0xF;
                                if (f < lo || f > hi) adrs += 2;
                                break;
                            }
                            case 245:
                                if (nft != null && work[0] >= 0 && work[0] < nft.Length) cur.Append(nft[work[0]]);
                                break;
                            default:
                                stopped = "APPNEW " + v; break;
                        }
                        if (stopped != null) break;
                        continue;
                    }
                    stopped = "イベント " + a;   // 戦闘・マップ移動・音楽など。会話文ではないので止める
                    break;
                }
                switch (b)
                {
                    case 42: case 44:   // close_wind / PAUSE = 段落の区切り
                        Flush(cur, paras); adrs++; break;
                    case 46:            // END: 呼び出し元へ (無ければ終わり)
                        if (stack.Count == 0) { adrs = end; break; }
                        end = stack[stack.Count - 1]; adrs = stack[stack.Count - 2];
                        stack.RemoveRange(stack.Count - 2, 2);
                        break;
                    case 50:            // JUMP msg (戻り先は積まない)
                    {
                        int t = (s[adrs + 1] & 0xFF) * 256 + (s[adrs + 2] & 0xFF);
                        if (!ScriptText.Range(c, t, out adrs, out end)) { stopped = "msg " + t + " が無い"; }
                        break;
                    }
                    case 51: case 52:
                    {
                        int f = Flag(s[adrs + 1] & 0xFF), r = s[adrs + 2] & 0xFF;
                        bool taken;
                        if (b == 51) { int lo = r >> 4, hi = r & 0xF; taken = f >= lo && f <= hi; }
                        else { bool nz = (f & (r & 0xF)) != 0; taken = (r & 0x80) != 0 ? !nz : nz; }
                        adrs += 3;
                        if (!taken) adrs += 2;
                        if (adrs + 1 < end && (s[adrs] & 0xFF) < 12)
                        {
                            int t = (s[adrs] & 0xFF) * 256 + (s[adrs + 1] & 0xFF);
                            if (!ScriptText.Range(c, t, out adrs, out end)) { stopped = "msg " + t + " が無い"; }
                        }
                        break;
                    }
                    case 53: case 54: adrs += 3; break;   // フラグの書き込み: 読むだけなので飛ばす
                    case 55: case 56: adrs += 2; break;
                    case 64: work[s[adrs + 1] & 0xFF] = s[adrs + 2] & 0xFF; adrs += 3; break;
                    case 65: work[s[adrs + 1] & 0xFF] = s[adrs + 2] & 0xFF; work[(s[adrs + 1] & 0xFF) + 1] = s[adrs + 3] & 0xFF; adrs += 4; break;
                    case 78: adrs += 2 + (s[adrs + 1] & 0xFF); break;
                    case 37: case 38: case 39: case 40: case 41: case 45: case 70: case 72: case 77: adrs++; break;
                    case 43: case 63: case 71: case 75: adrs += 2; break;
                    case 59: case 60: adrs += b == 59 ? 2 : 4; break;
                    case 67: adrs = end; stack.Clear(); break;   // ALL_END
                    default:
                        stopped = "命令 " + b;   // 選択肢 (47 / 48)、名前・品物名の差し込み (57 / 58 / 61 / 62 / 74)、品物・金の判定 (66 / 73 / 76)、SUB (79) など
                        break;
                }
                if (stopped != null) break;
            }
            Flush(cur, paras);
            if (stopped != null) Plugin.Log.LogWarning("[Info] msg " + msg + ": stopped at " + stopped + " (adrs " + adrs + ")");
            return paras;
        }

        private static void Flush(StringBuilder cur, List<string> paras)
        {
            if (cur.Length == 0) return;
            string t = cur.ToString().Trim('\n');
            if (t.Length > 0) paras.Add(t);
            cur.Length = 0;
        }
    }
}
