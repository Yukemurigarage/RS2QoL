using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// ワールドマップの大陸の一覧で、カーソルが乗っている大陸の名前をアイコンの下に出す。
    ///
    /// 本体は地域の一覧では町の名前を GS.DrawString で描く (Core.select_chiikimap の中) が、大陸の一覧では名前を出さない。
    /// Core.now_world_name という変数はあるが 0 を入れるだけで読まれない (スーファミ版の大陸名表示の名残とみられる)。
    /// 名前はメニュー文言のシート area_list (MsgDataManager.GetMenuMsg(3, 大陸番号 + 1)) に 1 北バレンヌ 〜 16 氷海 の順で入っていて、
    /// world_icon_tbl の並びと一致する (各大陸の最初の町 = area_name_msg_id_tbl の先頭で 16 大陸ぶん照合済み)。
    /// 描く位置はタッチ判定の矩形 (world_icon_tbl の [+2..+5]。アイコンの矩形で 32x32 ドット = 72 px) の下に中央寄せ。
    /// 下がマップからはみ出す時は矩形の上に出す。
    /// 自由カーソル (WorldMapCursorPatch) が有効なら乗っている時だけ、無効なら本体が選んでいる大陸 (常にどれかに乗っている) の名前。
    /// </summary>
    internal static class WorldMapContinentName
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<int> SizePercent;
        public const int SIZE_MIN = 50, SIZE_MAX = 100, SIZE_STEP = 10;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("WorldMap", "ContinentName", true,
                "ワールドマップの大陸の一覧で、カーソルの乗っている大陸の名前 (北バレンヌ など) をアイコンの下に出す。本体は地域の一覧では町の名前を出すが、大陸の名前は出さない");
            SizePercent = config.Bind("WorldMap", "ContinentNameSize", 70,
                new ConfigDescription("大陸の名前の文字の大きさ (%)。100 が本体の町名と同じ大きさ、既定 70", new AcceptableValueRange<int>(SIZE_MIN, SIZE_MAX)));
            Plugin.Log.LogInfo("[ContinentName] patched: Core.select_chiikimap Postfix (ContinentName=" + Enabled.Value + ")");
        }

        public static bool On { get { return Enabled != null && Enabled.Value; } }

        public static void CycleSize(int delta)
        {
            if (SizePercent == null) return;
            int v = SizePercent.Value + delta * SIZE_STEP;
            if (v < SIZE_MIN) v = SIZE_MIN; else if (v > SIZE_MAX) v = SIZE_MAX;
            SizePercent.Value = v;
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.select_chiikimap))]
    [HarmonyPriority(Priority.Low)] // WorldMapCursorPatch の Prefix (Hover / putcur_no の更新) より後に動く
    internal static class WorldMapContinentNamePatch
    {
        private const int ST_WORLD = 120;      // 大陸の一覧
        private const int WORLD_ICONS = 16;
        private const int WORLD_STRIDE = 12;
        private const int SHEET_AREA_LIST = 3; // MsgDataManager のメニュー文言シート "area_list"
        private const int Z = 6000;            // 本体の町名と同じ
        private const float GAP_Y = 2f;
        private const float FONT_H = 30f;      // GS.DrawString の 1 行の高さ (960x540 の座標、拡大率 100 % の時)

        private static bool s_announced, s_failed;

        private static void Postfix(Core __instance)
        {
            if (!WorldMapContinentName.On || s_failed) return;
            Core c = __instance;
            try
            {
                if (c.exevt_proc == null || c.exevt_proc[1] != ST_WORLD) return;
                if (c.world_size != 240 || Sys.fadeflg) return;                 // ズーム中・フェード中は本体もカーソルを出さない
                if (c.world_r_mode == 0 && c.exevt_proc[0] != 3) return;        // 見るだけ (帝国記など) も同じ
                if (WorldMapCursor.On && !WorldMapCursorPatch.Hover) return;   // 自由カーソルが何にも乗っていない
                int n = c.putcur_no;
                if (n < 0 || n >= WORLD_ICONS) return;
                int[] t = c.world_icon_tbl;
                int b = n * WORLD_STRIDE;
                if ((c.get_flag_atai(t[b]) & t[b + 1]) == 0) return;          // まだ行けない大陸
                string name = BattleLR.GetMenuMsg(SHEET_AREA_LIST, n + 1); // MsgDataManager は internal なので BattleLR の反射ヘルパーを使う
                if (string.IsNullOrEmpty(name)) return;

                float left = Sys.get_screen_x(t[b + 2]) - 8;
                float top = Sys.get_screen_y(t[b + 3]) - 25;
                float w = t[b + 4] * 36f / 16f, h = t[b + 5] * 36f / 16f;
                // 文字の大きさ: GS.DrawString は m_font_scale_x/y を使う (30/fontSize より大きい値は 30/fontSize に丸められる = 本体の町名の大きさ)。
                // その値に設定の % を掛けて一時的に差し替え、描いたら戻す
                float pct = WorldMapContinentName.SizePercent != null ? WorldMapContinentName.SizePercent.Value / 100f : 0.7f;
                float fontH = FONT_H * pct;
                float savedX = GS.m_font_scale_x, savedY = GS.m_font_scale_y;
                float scale = 30f / GS.m_font[GS.m_font_id].fontSize * pct;
                GS.m_font_scale_x = scale;
                GS.m_font_scale_y = scale;
                try
                {
                    int dot = GS.StrDot(name);
                    float x = left + w / 2f - dot / 2f;
                    float y = top + h + GAP_Y;
                    // マップの範囲 (論理 240x240。画像は (-8, -25) ずれて置かれる) からはみ出さないように
                    float minX = Sys.get_screen_x(0) - 8, maxX = Sys.get_screen_x(240) - 8 - dot;
                    float maxY = Sys.get_screen_y(240) - 25 - fontH;
                    if (x > maxX) x = maxX;
                    if (x < minX) x = minX;
                    if (y > maxY) y = top - GAP_Y - fontH; // 下に出せない時は上に
                    GS.DrawString(name, (int)x, (int)y, Z, Color.white, GS.FontEffect.SHADOW);
                    if (!s_announced)
                    {
                        s_announced = true;
                        Plugin.Log.LogInfo("[ContinentName] applied: " + name + " (world " + n + ") at (" + (int)x + "," + (int)y + ") icon rect " + (int)left + "," + (int)top + " " + (int)w + "x" + (int)h + " dot=" + dot + " size=" + (int)(pct * 100) + "%");
                    }
                }
                finally
                {
                    GS.m_font_scale_x = savedX;
                    GS.m_font_scale_y = savedY;
                }
            }
            catch (System.Exception e)
            {
                s_failed = true;
                Plugin.Log.LogWarning("[ContinentName] disabled after error: " + e);
            }
        }
    }
}
