namespace RS2QoL.Patches
{
    /// <summary>
    /// スクリプトの会話に MOD の文を差し込む共通部品。
    ///
    /// 本体の仕組み: Core.ten_put / put_10_hidari などは name_work (mestbl[2]) に文字コードを組み、message_stok に今の読み位置を積んで
    /// 「サブレベル」(mess_type 2) として表示させる。サブレベルの終わり (mess_adrs >= end_mess_adrs) で Core.check_finish が積んだ位置に戻る
    /// (戻った時に mess_no は -1 になる)。MOD はこの手順をそのまま使い、戻り先だけを「飛ばしたい本文の次」にする。
    /// 文字コードは Window.fonttbl の添字 (256 以上は +7936 の 2 バイト、全角数字は 35 + 値、改行は 36)。本体の字形表に無い字は出せない。
    /// </summary>
    internal static class ScriptText
    {
        public const int NL = 36;

        /// <summary>文字列を本体の文字コード列にする。'\n' は改行命令、数字は全角、他は fonttbl の添字。無い字があれば null (警告ログ)。</summary>
        public static byte[] Encode(string text, string tag)
        {
            System.Collections.Generic.List<byte> b = new System.Collections.Generic.List<byte>(text.Length * 2);
            string tbl = Window.fonttbl;
            foreach (char ch in text)
            {
                if (ch == '\n') { b.Add(NL); continue; }
                if (ch >= '0' && ch <= '9') { b.Add(35); b.Add((byte)(ch - '0')); continue; }   // 8960 + 値 = 全角数字
                int idx = tbl.IndexOf(ch, 80);                                                   // 80 未満は命令と重なるので 2 バイト形を探す
                if (idx < 0)
                {
                    Plugin.Log.LogWarning(tag + " no glyph for '" + ch + "', leaving vanilla text");
                    return null;
                }
                if (idx < 256) b.Add((byte)idx);
                else { int code = idx + 7936; b.Add((byte)(code >> 8)); b.Add((byte)(code & 0xFF)); }
            }
            return b.ToArray();
        }

        /// <summary>
        /// body をサブレベルとして表示させ、終わったら今のスクリプト (mess_type) の returnAdrs に戻す。
        /// 本体の ten_put と同じ手順。name_work は 32 バイトしかないので足りなければ大きい配列に差し替える (mestbl[2] も同じ配列を指す)。
        /// </summary>
        public static void EnterSublevel(Core c, byte[] body, int returnAdrs)
        {
            if (c.name_work.Length < body.Length)
            {
                c.name_work = new byte[System.Math.Max(256, body.Length)];
                c.mestbl[2] = c.name_work;
            }
            System.Array.Copy(body, c.name_work, body.Length);
            c.message_stok[c.message_level * 5] = returnAdrs;
            c.message_stok[c.message_level * 5 + 3] = c.end_mess_adrs;
            c.message_stok[c.message_level * 5 + 2] = c.mess_type;
            c.message_level++;
            Core.mess_adrs = 0;
            c.end_mess_adrs = body.Length;
            c.mess_type = 2;
        }

        public static bool Match(byte[] a, int at, byte[] pat)
        {
            if (a == null || at < 0 || at + pat.Length > a.Length) return false;
            for (int i = 0; i < pat.Length; i++) if (a[at + i] != pat[i]) return false;
            return true;
        }

        /// <summary>from 以降で pat が最初に現れる位置 (limit 未満)。無ければ -1。</summary>
        public static int Find(byte[] a, int from, int limit, byte[] pat)
        {
            if (a == null) return -1;
            if (limit > a.Length) limit = a.Length;
            for (int p = from; p + pat.Length <= limit; p++) if (Match(a, p, pat)) return p;
            return -1;
        }

        /// <summary>メッセージ n の本文の範囲 (script.bin の先頭の 4 バイト × n のポインタ表から。Core.message_r と同じ)。</summary>
        public static bool Range(Core c, int msg, out int start, out int end)
        {
            start = end = 0;
            byte[] s = c != null && c.mestbl != null ? c.mestbl[0] : null;
            if (s == null || msg < 0 || msg * 4 + 7 >= s.Length) return false;
            start = (s[msg * 4] & 0xFF) | ((s[msg * 4 + 1] & 0xFF) << 8) | ((s[msg * 4 + 2] & 0xFF) << 16);
            end = (s[msg * 4 + 4] & 0xFF) | ((s[msg * 4 + 5] & 0xFF) << 8) | ((s[msg * 4 + 6] & 0xFF) << 16);
            return start > 0 && end > start && end <= s.Length;
        }
    }
}
