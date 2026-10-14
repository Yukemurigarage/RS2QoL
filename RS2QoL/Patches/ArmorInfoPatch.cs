using System;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 防具・盾の詳しい防御力 (8 種の防御力、防御特性、状態異常耐性、重さ / 盾の回避) を、
    /// 持ち物・装備・店・預かり所の画面で、カーソルが乗っている防具の横に小さな窓で出す。
    ///
    /// 本体の表示は item_menu_data[no*5+1] の 1 つの数だけで、これは armor_data[(no-96)*12 + 0] = 「斬」の値
    /// (table.bin を読んで全 96 種で一致を確認)。残りの 7 種 (打・突・射・熱・冷・雷・状) と、
    /// item_menu_data[no*5+3] の防御特性ビット (0x80 = 斬 … 0x01 = 状。スーファミ版の「特性」アイコン)、
    /// armor_data[+8] の状態異常耐性ビット、armor_data[+11] の重さは、本体のどの画面にも出ない。
    /// 本体には 8 種を並べて描く item_armor_disp と特性アイコンを描く bfont_display が残っているが
    /// (ガラケー / 旧スマホ版の自前描画の UI)、Steam 版の SpriteStudio の画面からは呼ばれていない。
    ///
    /// 値 128 以上は本体の旧 UI で★のアイコン (無効) を出していたので「無効」と表示する。
    /// 8 種の並びは本体の skill.gif のアイコン順 (剣・槌・槍・矢 / 火球・氷・雷・紫の紋) で確認した。
    ///
    /// どの画面で何が選ばれているかは、本体がその画面の「攻撃力 / 防御力」の文字を埋める関数の Postfix で拾う
    /// (持ち物・店 = item_power_disp(no, sso, id)、装備 = init_soubi / proc_soubi、装備の候補一覧 =
    /// init_soubi_3 / proc_soubi_3、預かり所 = item_mess_azukeru)。窓はその画面の SpriteStudio が生きている間だけ描く。
    /// </summary>
    internal static class ArmorInfo
    {
        public static ConfigEntry<bool> Enabled;

        private const int ARMOR_MIN = 96, ARMOR_MAX = 191, SHIELD_MIN = 192, SHIELD_MAX = 207;
        private static readonly string[] TypeNames = { "斬", "打", "突", "射", "熱", "冷", "雷", "状" };
        // armor_data[+8] の状態異常耐性ビット。本体の品物の説明文 (メッセージのシート 7) と突き合わせて決めた:
        // 0x40 マヒ (エルブンスキン「マヒから守る」)、0x20 眠り (スウェットスーツ・シルバーリング・ラビットフット)、
        // 0x10 失明 (キャッツアイ・ライオンヘッド)、0x08 精神 (ソーモンの指輪・魔石の指輪)、0x04 毒 (ヒドラスーツ・ラビットアーマー)、
        // 0x02 石化 (インタリオリング 0x7E の説明文「マヒ、眠り、失明、精神、毒、石化から守る」で確認。識者のローブ・
        // キャンディリングが持つ)。0x80 と 0x01 はどの防具も持たない
        private static readonly string[] CondNames = { "？", "マヒ", "眠り", "失明", "精神", "毒", "石化", "？" };

        private static int s_item = -1;
        private static string s_screen;
        private static bool s_dumped;

        private static GUIStyle s_text, s_mark, s_small;
        private static int s_fontBuilt = -1;
        private static Font s_font;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Menu", "ArmorDetail", true,
                "持ち物・装備・店・預かり所で、カーソルが乗っている防具・盾の 8 種の防御力 (斬・打・突・射・熱・冷・雷・状)、防御特性、状態異常耐性、重さ (盾は回避率と回避できる攻撃) を小さな窓で出す。本体は「斬」の値だけを防御力として表示している");
        }

        /// <summary>本体がある画面で品物の攻撃力 / 防御力を埋めた直後に呼ばれる。</summary>
        internal static void Record(int item, string screen)
        {
            s_item = item;
            s_screen = screen;
        }

        /// <summary>
        /// 記録してある画面と同じ名前の SpriteStudio が作り直された時に記録を捨てる。
        /// 店で防具の個数画面 (1_b_2_Control) を見てから一覧に戻り、武器を選ぶと同じ名前の画面が作り直されるが、
        /// IsReady になった最初のフレームは本体がまだ品物を埋めておらず (make_item_detail は次のフレーム)、
        /// 前の防具の記録のまま窓が 1 フレームだけ出てしまうのを防ぐ。
        /// </summary>
        internal static void ForgetIfScreen(string name)
        {
            if (!string.IsNullOrEmpty(name) && name == s_screen) { s_item = -1; s_screen = null; }
        }

        private static bool ScreenAlive()
        {
            if (string.IsNullOrEmpty(s_screen)) return false;
            try
            {
                CSpriteStudioObject sso = SpriteStudioManager.GetSpriteStudio(s_screen);
                return sso != null && sso.IsReady();
            }
            catch { return false; }
        }

        /// <summary>Plugin.OnGUI から。</summary>
        public static void Draw()
        {
            if (Enabled == null || !Enabled.Value || s_failed) return;
            if (Event.current.type != EventType.Repaint) return;
            if (Main.menu == null || ConfigMenu.IsOpen) return;
            int no = s_item;
            bool armor = no >= ARMOR_MIN && no <= ARMOR_MAX;
            bool shield = no >= SHIELD_MIN && no <= SHIELD_MAX;
            if (!armor && !shield) return;
            if (Data.armor_data == null || Data.shield_data == null || Data.item_menu_data == null) return;
            if (!ScreenAlive()) return;
            try
            {
                if (!s_dumped && Plugin.DiagEnabled != null && Plugin.DiagEnabled.Value) Dump();
                DrawPanel(no, armor);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[ArmorInfo] draw failed, disabled until restart: " + e);
                s_failed = true;   // 設定 (cfg) は書き換えない。書き換えると一度の失敗で次回以降も黙って OFF のままになる
            }
        }

        private static bool s_failed;

        private static void DrawPanel(int no, bool armor)
        {
            float u = UiParts.Unit;                       // 仮想 960x540 の 1 単位
            float ox = (Screen.width - 960f * u) / 2f;    // 16:9 でない画面は中央合わせ
            int fontSize = Mathf.Max(10, Mathf.RoundToInt(15f * u));
            EnsureStyles(fontSize);
            float pad = 10f * u;

            // 行を組み立てる (縦 1 列。画面右端の飾り枠の上に置くので幅が狭い)。
            // 窓の大きさが品物で変わらないように行数は固定 (8 種 + 回避率 + 重さ + 耐性 3 行)。無い項目は「－」
            const int COND_LINES = 3; // 耐性の名前は 1 行 2 つ、最大 6 つ (インタリオリング) で 3 行
            string[] lines = new string[8 + 2 + 1 + COND_LINES];
            int[] kinds = new int[lines.Length]; // 0 = 通常、1 = 特性あり (別の色)、2 = 小さめ
            int cond = 0;
            if (armor)
            {
                int a = (no - ARMOR_MIN) * 12;
                int flags = Data.item_menu_data[no * 5 + 3] & 0xFF;
                for (int i = 0; i < 8; i++)
                {
                    int v = Data.armor_data[a + i] & 0xFF;
                    lines[i] = TypeNames[i] + "  " + (v >= 128 ? "無効" : v.ToString());
                    kinds[i] = (flags & (0x80 >> i)) != 0 ? 1 : 0;
                }
                lines[8] = "回避率 －";
                lines[9] = "重さ " + (Data.armor_data[a + 11] & 0xFF);
                cond = Data.armor_data[a + 8] & 0xFF;
            }
            else
            {
                int s0 = (no - SHIELD_MIN) * 3;
                int bits = Data.shield_data[s0] & 0xFF;
                for (int i = 0; i < 8; i++)
                {
                    bool can = (bits & (0x80 >> i)) != 0;
                    lines[i] = TypeNames[i] + "  " + (can ? "回避" : "－");
                    kinds[i] = can ? 1 : 0;
                }
                lines[8] = "回避率 " + (Data.shield_data[s0 + 1] & 0x7F);
                lines[9] = "重さ " + (Data.shield_data[s0 + 2] & 7);
            }
            kinds[8] = kinds[9] = kinds[10] = 2;
            lines[10] = "耐性" + (cond == 0 ? " －" : ":");
            {
                int n = 0;
                StringBuilder sb = new StringBuilder();
                int li = 11;
                for (int b = 0; b < 8 && li < lines.Length; b++)
                {
                    if ((cond & (0x80 >> b)) == 0) continue;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(CondNames[b]);
                    if (++n % 2 == 0) { lines[li] = sb.ToString(); kinds[li++] = 2; sb.Length = 0; }
                }
                if (sb.Length > 0 && li < lines.Length) { lines[li] = sb.ToString(); kinds[li++] = 2; }
                for (; li < lines.Length; li++) { lines[li] = string.Empty; kinds[li] = 2; }
            }

            // 画面右端の飾り枠 (どの画面でも同じ絵) の上に置く。本体の文字や矢印と重ならない。
            // 上下は装備画面の品目欄 (武器: / 防具: の暗い角丸、仮想座標で上 161.5・下 467.5。画素で測った値) に揃え、見た目も同じ箱にする。
            // 行の高さは箱の高さから割り出す (行数は固定なので品物によらず同じ)
            float w = 112f * u, h = 306f * u;
            float x = ox + (960f - 112f - 6f) * u, y = 161.5f * u;
            float lineH = (h - pad * 2) / lines.Length;
            Rect r = new Rect(Mathf.Round(x), Mathf.Round(y), Mathf.Round(w), Mathf.Round(h));
            UiParts.DrawDarkBox(r);
            UiParts.Shadow = false;
            EnsureColors();

            // 本体のフォント (UDTypos512) は固定サイズのビットマップフォントで GUIStyle.fontSize が効かないので、
            // GUI.matrix で縮めて目的の大きさにする (本体の GS.DrawString も自前の倍率で縮めている)
            float sc = 1f;
            if (s_font != null && s_font.fontSize > 0) sc = fontSize / (float)s_font.fontSize;
            Matrix4x4 oldM = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(sc, sc, 1f)) * oldM;
            float cx = (r.x + pad) / sc, cy = (r.y + pad) / sc, cw = (w - pad * 2) / sc, lh = lineH / sc;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                GUIStyle st = kinds[i] == 1 ? s_mark : kinds[i] == 2 ? s_small : s_text;
                GUIStyle sh = kinds[i] == 1 ? s_markSh : kinds[i] == 2 ? s_smallSh : s_textSh;
                // 本体の大きな文字を縮めて描くと線が細く薄く見えるので、右下に 1 画素ずらした薄い影を先に描いてから本体を描く
                Rect lr = new Rect(cx, cy + lh * i, cw, lh);
                    UiParts.Label(new Rect(lr.x + 1f / sc, lr.y + 1f / sc, lr.width, lr.height), lines[i], sh);
                UiParts.Label(lr, lines[i], st);
            }
            GUI.matrix = oldM;
        }

        /// <summary>本体のメニュー文字と同じフォント (GS.m_font[0] = Resources の Fonts/UDTypos512)。無ければ GUI 既定。</summary>
        private static Font MenuFont()
        {
            try
            {
                Font[] fonts = GS.m_font;
                if (fonts != null && fonts.Length > 0 && fonts[0] != null) return fonts[0];
            }
            catch { }
            return null;
        }

        private static void EnsureStyles(int fontSize)
        {
            if (s_text != null && s_fontBuilt == fontSize) return;
            s_fontBuilt = fontSize;
            Font f = MenuFont();
            s_font = f;
            s_text = new GUIStyle(GUI.skin.label) { fontSize = fontSize, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Overflow, wordWrap = false };
            if (f != null) s_text.font = f;
            s_text.normal.textColor = Color.white;
            s_mark = new GUIStyle(s_text) { fontStyle = FontStyle.Bold };
            s_small = new GUIStyle(s_text) { fontSize = Mathf.Max(9, fontSize - 2) };
            if (f != null)
            {
                Plugin.Log.LogInfo(string.Format("[ArmorInfo] menu font {0} size={1} dynamic={2}", f.name, f.fontSize, f.dynamic));
            }
            s_colorsSet = false; // 色を入れ直させる
            UiParts.ResetStyles();
        }

        private static bool s_colorsSet;
        private static void EnsureColors()
        {
            if (s_colorsSet) return;
            s_colorsSet = true;
            // 本体の品目欄の文字 (武器: / 防具:) と同じ黒系。特性のある種類は赤で区別 (太字だけでは本体の太い書体と見分けにくい)
            s_text.normal.textColor = Color.black;
            s_mark.normal.textColor = new Color(0.6f, 0.04f, 0.02f);
            s_small.normal.textColor = new Color(0.12f, 0.1f, 0.09f);
            // 影は同じ色を薄くしたもの
            s_textSh = ShadowOf(s_text);
            s_markSh = ShadowOf(s_mark);
            s_smallSh = ShadowOf(s_small);
            UiParts.ResetStyles();
        }

        private static GUIStyle s_textSh, s_markSh, s_smallSh;
        private static GUIStyle ShadowOf(GUIStyle st)
        {
            GUIStyle sh = new GUIStyle(st);
            Color c = st.normal.textColor;
            c.a = SHADOW_ALPHA;
            sh.normal.textColor = c;
            return sh;
        }
        private const float SHADOW_ALPHA = 0.4f;

        // MsgDataManager は internal なので反射で呼ぶ (シート 6 = 品名)
        private static readonly System.Reflection.MethodInfo s_getMenuMsg =
            AccessTools.Method(AccessTools.TypeByName("MsgDataManager"), "GetMenuMsg", new Type[] { typeof(int), typeof(int) });
        private static string ItemName(int no)
        {
            try { return s_getMenuMsg != null ? (string)s_getMenuMsg.Invoke(null, new object[] { 6, no + 1 }) : "?"; }
            catch { return "?"; }
        }
        // シート 7 = 品物の説明文 (持ち物画面の下に出る文)。耐性ビットの名前の突き合わせ用
        private static string ItemDesc(int no)
        {
            try { return s_getMenuMsg != null ? ((string)s_getMenuMsg.Invoke(null, new object[] { 7, no + 1 }) ?? "").Replace("\n", "/") : "?"; }
            catch { return "?"; }
        }

        /// <summary>診断: 全防具・盾の名前と生データを 1 回だけログに出す (耐性ビットの名前の突き合わせ用)。</summary>
        private static void Dump()
        {
            s_dumped = true;
            try
            {
                for (int no = ARMOR_MIN; no <= ARMOR_MAX; no++)
                {
                    int a = (no - ARMOR_MIN) * 12;
                    StringBuilder sb = new StringBuilder();
                    sb.Append("[ArmorData] ").Append(no).Append(' ').Append(ItemName(no)).Append(" def=");
                    for (int i = 0; i < 8; i++) sb.Append(Data.armor_data[a + i] & 0xFF).Append(i < 7 ? "/" : "");
                    sb.Append(" cond=").Append(Data.armor_data[a + 8] & 0xFF)
                      .Append(" where=").Append(Data.armor_data[a + 9] & 0xFF)
                      .Append(" bonus=").Append(Data.armor_data[a + 10] & 0xFF)
                      .Append(" weight=").Append(Data.armor_data[a + 11] & 0xFF)
                      .Append(" menu=");
                    for (int i = 0; i < 5; i++) sb.Append(Data.item_menu_data[no * 5 + i] & 0xFF).Append(i < 4 ? "/" : "");
                    sb.Append(" desc=").Append(ItemDesc(no));
                    Plugin.Log.LogInfo(sb.ToString());
                }
                for (int no = SHIELD_MIN; no <= SHIELD_MAX; no++)
                {
                    int s0 = (no - SHIELD_MIN) * 3;
                    Plugin.Log.LogInfo(string.Format("[ShieldData] {0} {1} parry={2} rate={3} b2={4} menu={5}/{6}/{7}/{8}/{9}",
                        no, ItemName(no), Data.shield_data[s0] & 0xFF, Data.shield_data[s0 + 1] & 0xFF, Data.shield_data[s0 + 2] & 0xFF,
                        Data.item_menu_data[no * 5] & 0xFF, Data.item_menu_data[no * 5 + 1] & 0xFF, Data.item_menu_data[no * 5 + 2] & 0xFF,
                        Data.item_menu_data[no * 5 + 3] & 0xFF, Data.item_menu_data[no * 5 + 4] & 0xFF));
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[ArmorInfo] dump failed: " + e.Message); }
        }
    }

    // 画面の作り直し: 同じ名前の画面がもう一度作られたら、前の品物の記録を捨てる (1 フレームだけ前の窓が出るのを防ぐ)
    [HarmonyPatch(typeof(SpriteStudioManager), nameof(SpriteStudioManager.AddSpriteStudio))]
    internal static class ArmorInfoScreenResetPatch
    {
        static void Postfix(string FileName) { ArmorInfo.ForgetIfScreen(FileName); }
    }

    // 持ち物 (2_c_Control) と店 (1_b_2_Control): 品物の攻撃力 / 防御力の文字を埋める関数
    [HarmonyPatch(typeof(Menu), "item_power_disp", new Type[] { typeof(int), typeof(CSpriteStudioObject), typeof(int) })]
    internal static class ArmorInfoItemPatch
    {
        static void Postfix(int no, CSpriteStudioObject sso)
        {
            ArmorInfo.Record(no, sso != null ? sso.GetFileName() : null);
        }
    }

    // 装備 (2_d_2_Control): 装備欄のカーソル (soubi_te1) の品物
    [HarmonyPatch(typeof(Menu), "init_soubi")]
    internal static class ArmorInfoEquipInitPatch
    {
        static void Postfix(Menu __instance) { ArmorInfoEquipProcPatch.Postfix(__instance); }
    }

    [HarmonyPatch(typeof(Menu), "proc_soubi")]
    internal static class ArmorInfoEquipProcPatch
    {
        internal static void Postfix(Menu __instance)
        {
            try
            {
                int member = __instance.oam_type[0] - 1;
                if (member < 0 || member >= Work.memberdata.Length) return;
                int slot = __instance.soubi_te1;
                if (slot < 0 || slot > 6) return;
                ArmorInfo.Record(Work.memberdata[member][77 + slot], "2_d_2_Control");
            }
            catch { }
        }
    }

    // 装備の候補一覧 (2_d_3_a_Control): 一覧のカーソル (equip_page_no / soubi_te2) の品物
    [HarmonyPatch(typeof(Menu), "init_soubi_3")]
    internal static class ArmorInfoEquipListInitPatch
    {
        static void Postfix(Menu __instance) { ArmorInfoEquipListProcPatch.Postfix(__instance); }
    }

    [HarmonyPatch(typeof(Menu), "proc_soubi_3")]
    internal static class ArmorInfoEquipListProcPatch
    {
        internal static void Postfix(Menu __instance)
        {
            try
            {
                int idx = (__instance.equip_page_no * 5 + __instance.soubi_te2) * 5;
                if (idx < 0 || idx >= __instance.equip_work.Length) return;
                ArmorInfo.Record(__instance.equip_work[idx], "2_d_3_a_Control");
            }
            catch { }
        }
    }

    // 預かり所 (預ける = 持ち物の並び、引き出す = index_work の並び)
    [HarmonyPatch(typeof(Menu), "item_mess_azukeru")]
    internal static class ArmorInfoStoragePatch
    {
        static void Postfix(Menu __instance, CSpriteStudioObject sprite, int type)
        {
            try
            {
                int item;
                if (type != 0) item = __instance.index_work[(__instance.page_no * 4 + __instance.gene_te2) * 2];
                else item = Work.partydata[33 + (__instance.item_page_no * 4 + __instance.item_te1) * 2];
                ArmorInfo.Record(item, sprite != null ? sprite.GetFileName() : null);
            }
            catch { }
        }
    }
}
