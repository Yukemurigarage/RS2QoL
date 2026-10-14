using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// ワールドマップ (大陸の一覧 / 地域の地名一覧) のカーソルを、スーファミ版のように十字キーで自由に動かす。
    ///
    /// 本体 (Core.select_chiikimap) はカーソルを「アイコンからアイコンへ瞬間移動」させる:
    ///  - 大陸画面 (exevt_proc[1] == 120): putcur_no。十字キーの押下エッジで world_icon_tbl の隣接リンク [+8..+11] をたどる。
    ///  - 地域画面 (exevt_proc[1] == 20):  w_cursor_sw。左上 = 前の項目 / 右下 = 次の項目 (area_name_tbl の [+8] の順)。
    /// どちらも Sys.checkKey2(2, 方向) か InputManager.GetMenuInput() で読み、決定で putcur_no / w_cursor_sw の項目へ進む。
    ///
    /// ここでは、Prefix で
    ///  1. 十字キー / スティックの押しっぱなし (斜め含む。DiagonalInputPatch.LastRaw) から MOD 独自のカーソル座標を毎論理フレーム動かし、
    ///  2. 本体のタッチ判定と同じ矩形 (init_world_vram_sa / init_japan_vram_sa が add_touch_rect に渡すもの) で「カーソルがアイコンの上にいるか」を調べ、
    ///  3. 乗っていれば putcur_no / w_cursor_sw をその項目にして本体の決定処理をそのまま使う。
    ///     乗っていなければ決定を無効にする (大陸画面: Sys.key[1] と GetMenuInput の決定をマスク。地域画面: autoCancel を 1 にして
    ///     本体の名前表示・カーソル描画・決定を飛ばし、カーソルだけ MOD が描く)。
    ///  4. 本体の瞬間移動は Sys.key[2] の方向ビットと GetMenuInput の方向をこの呼び出しの間だけマスクして止める。
    /// Postfix で元に戻す。カーソル画像 (m_world_cursor) の描画は Util.Sprite.Draw(x, y) の Prefix で自由位置へ差し替え、
    /// 何も乗っていない時の大陸のハイライト (set_mask_visible の opt 3) は Prefix で消す。
    ///
    /// マウス (ID_WLD_n のクリック) で本体が項目を選んだ時は Postfix で気付いてカーソルをそこへ寄せる。
    /// 画面の「決定」ボタン (BID_WLD_ENTER) は本体どおり最後に選んでいた項目へ進む (マウス専用の挙動なので触らない)。
    /// 帝国記などの「見るだけ」(world_r_mode == 0 で exevt_proc[0] != 3) は本体がカーソルを出さないので対象外。
    /// </summary>
    internal static class WorldMapCursor
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<int> Speed;

        public const int SPEED_MIN = 1;
        public const int SPEED_MAX = 16;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("WorldMap", "FreeCursor", true,
                "ワールドマップ (大陸の一覧・地域の地名一覧) のカーソルをスーファミ版のように十字キーで自由に動かす。場所の上に乗せた時だけ選べる (アイコンへの瞬間移動はしない)");
            Speed = config.Bind("WorldMap", "CursorSpeed", 4,
                new ConfigDescription("自由カーソルの速さ (実時間 33ms あたりのドット数。マップは 240 ドット幅。倍速中も同じ速さ)", new AcceptableValueRange<int>(SPEED_MIN, SPEED_MAX)));
        }

        public static bool On { get { return Enabled != null && Enabled.Value; } }

        public static void CycleSpeed(int delta)
        {
            if (Speed == null) return;
            int v = Speed.Value + delta;
            if (v < SPEED_MIN) v = SPEED_MIN; else if (v > SPEED_MAX) v = SPEED_MAX;
            Speed.Value = v;
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.select_chiikimap))]
    internal static class WorldMapCursorPatch
    {
        private const int ST_AREA = 20;    // 地域画面 (地名の一覧)
        private const int ST_WORLD = 120;  // 大陸画面
        private const int DIR_MASK = 0xF0000;
        private const int OK_BITS = 0x100001;
        private const int WORLD_ICONS = 16;
        private const int WORLD_STRIDE = 12;
        private const int AREA_STRIDE = 10;
        // 本体のカーソル描画は「基準点 + (8, -16-25)」(大陸画面)。地域画面は矩形の中心 + (8, -40-25) なので基準点 = 中心 + (0, -24)
        private const float QUILL_DX = 8f;
        private const float QUILL_DY = -41f;
        private const float AREA_HOT_DY = -24f;
        private const float AREA_RECT_SCALE = 1.2f; // init_japan_vram_sa のタッチ矩形の拡大率

        private static readonly AccessTools.FieldRef<Core, Util.Sprite> CursorRef = AccessTools.FieldRefAccess<Core, Util.Sprite>("m_world_cursor");

        /// <summary>select_chiikimap の呼び出し中で、自由カーソルが働いている間 true (他のパッチのゲート)。</summary>
        internal static bool Active;
        /// <summary>カーソルが選べる場所の上に乗っている。</summary>
        internal static bool Hover;
        /// <summary>本体のカーソル画像 (描画の差し替え先の判定用)。</summary>
        internal static Util.Sprite CursorSprite;
        /// <summary>カーソル画像を描く位置 (画面座標)。</summary>
        internal static float DrawX, DrawY;

        private static int s_level;           // 0 = 対象外、ST_AREA / ST_WORLD
        private static float s_x, s_y;        // 自由カーソルの基準点 (画面座標。本体の putcur_no の基準点と同じ空間)
        private static int s_setSel = -1;     // Prefix で本体に渡した選択 (Postfix でマウス等による変更を検出する)
        private static bool s_keysMasked;
        private static int s_savedKey1, s_savedKey2;
        private static bool s_autoCancelSet;

        private static void Prefix(Core __instance)
        {
            Core c = __instance;
            Active = false;
            Hover = false;
            s_keysMasked = false;
            s_autoCancelSet = false;
            if (!WorldMapCursor.On || c.exevt_proc == null) { s_level = 0; return; }

            int st = c.exevt_proc[1];
            if ((st != ST_AREA && st != ST_WORLD) || (c.world_r_mode == 0 && c.exevt_proc[0] != 3))
            {
                s_level = 0;
                return;
            }
            CursorSprite = CursorRef(c);
            if (s_level != st)
            {
                // 画面に入った直後: 本体が選んでいる項目の位置から始める (大陸画面 = 今いる大陸、地域画面 = 今いる町)
                s_level = st;
                SnapTo(c, st, st == ST_WORLD ? c.putcur_no : c.w_cursor_sw);
            }
            Active = true;
            SetDraw();
            // ズーム中・フェード中は本体も入力を見ないので、描画の差し替えだけ
            if (c.world_size != 240 || Sys.fadeflg) { s_setSel = -1; return; }

            Move();
            SetDraw();
            int sel = (st == ST_WORLD) ? HitWorld(c) : HitArea(c);
            Hover = sel >= 0;

            s_savedKey1 = Sys.key[1];
            s_savedKey2 = Sys.key[2];
            s_keysMasked = true;
            Sys.key[2] &= ~DIR_MASK; // 本体の瞬間移動を止める
            if (st == ST_WORLD)
            {
                if (Hover) c.putcur_no = sel;
                else Sys.key[1] &= ~OK_BITS; // 何も乗っていなければ決定を無効に
                s_setSel = c.putcur_no;
            }
            else
            {
                if (Hover)
                {
                    c.w_cursor_sw = sel;
                }
                else
                {
                    // autoCancel == 1 の間、本体は名前表示・カーソル描画・方向・決定を飛ばす (戻る / とじる は効く)
                    if (c.autoCancel == 0) { c.autoCancel = 1; s_autoCancelSet = true; }
                    if (CursorSprite != null) CursorSprite.Draw(DrawX, DrawY);
                }
                s_setSel = c.w_cursor_sw;
            }
        }

        private static void Postfix(Core __instance)
        {
            Core c = __instance;
            if (s_keysMasked)
            {
                s_keysMasked = false;
                Sys.key[1] = s_savedKey1;
                Sys.key[2] = s_savedKey2;
            }
            if (s_autoCancelSet)
            {
                s_autoCancelSet = false;
                c.autoCancel = 0;
            }
            if (Active && s_setSel >= 0 && c.exevt_proc[1] == s_level)
            {
                // マウスのクリック (ID_WLD_n) などで本体が別の項目を選んだらカーソルをそこへ寄せる
                int now = (s_level == ST_WORLD) ? c.putcur_no : c.w_cursor_sw;
                if (now != s_setSel) SnapTo(c, s_level, now);
            }
            Active = false;
        }

        private static void Move()
        {
            if (QuitGame.BlockInput) return; // 終了ダイアログ中は羽 (カーソル) も動かさない
            int dirs = DiagonalInputPatch.LastRaw & DIR_MASK;
            if (dirs == 0) return;
            float step = (WorldMapCursor.Speed != null ? WorldMapCursor.Speed.Value : 4) * 36f / 16f; // 論理ドット → 画面ドット
            // ここは論理フレームごとに呼ばれるので、倍速 (x2 なら 1 Unity フレームに 2 回) だと羽もその分速くなる。
            // 速さの設定は「実時間 33ms あたり」の意味にしたいので、1 回の歩幅を倍率で割って実時間の速さを保つ (x1.5 は 1, 2, 1, 2 回と回るが平均で合う)
            step *= 100f / GameSpeed.EffectivePercent();
            if ((dirs & DiagonalInputPatch.DIR_LEFT) != 0) s_x -= step;
            if ((dirs & DiagonalInputPatch.DIR_RIGHT) != 0) s_x += step;
            if ((dirs & DiagonalInputPatch.DIR_UP) != 0) s_y -= step;
            if ((dirs & DiagonalInputPatch.DIR_DOWN) != 0) s_y += step;
            // マップの範囲 (論理 240x240。画像は (-8, -25) ずれて置かれる) に収める
            float minX = Sys.get_screen_x(0) - 8, maxX = Sys.get_screen_x(240) - 8;
            float minY = Sys.get_screen_y(0) - 25, maxY = Sys.get_screen_y(240) - 25;
            if (s_x < minX) s_x = minX; else if (s_x > maxX) s_x = maxX;
            if (s_y < minY) s_y = minY; else if (s_y > maxY) s_y = maxY;
        }

        private static void SetDraw()
        {
            DrawX = s_x + QUILL_DX;
            DrawY = s_y + QUILL_DY;
        }

        private static void SnapTo(Core c, int st, int sel)
        {
            if (st == ST_WORLD)
            {
                if (sel < 0 || sel >= WORLD_ICONS) return;
                int[] t = c.world_icon_tbl;
                s_x = Sys.get_screen_x(t[sel * WORLD_STRIDE + 6]);
                s_y = Sys.get_screen_y(t[sel * WORLD_STRIDE + 7]);
            }
            else
            {
                int num = c.world_jump_pos_sub / 16;
                if (num < 0 || num >= c.area_name_tbl.Length) return;
                int[] t = c.area_name_tbl[num];
                if (sel < 0 || (sel + 1) * AREA_STRIDE > t.Length) return;
                Rect r = AreaObjRect(c, num, sel);
                s_x = r.center.x;
                s_y = r.center.y + AREA_HOT_DY;
            }
            SetDraw();
        }

        /// <summary>大陸画面: カーソルが乗っている有効なアイコン (world_icon_tbl の番号)。無ければ -1。矩形は init_world_vram_sa のタッチ判定と同じ。</summary>
        private static int HitWorld(Core c)
        {
            int[] t = c.world_icon_tbl;
            Vector2 p = new Vector2(s_x, s_y);
            for (int i = 0; i < WORLD_ICONS; i++)
            {
                int b = i * WORLD_STRIDE;
                if ((c.get_flag_atai(t[b]) & t[b + 1]) == 0) continue;
                Rect r = new Rect(Sys.get_screen_x(t[b + 2]) - 8, Sys.get_screen_y(t[b + 3]) - 25, t[b + 4] * 36 / 16, t[b + 5] * 36 / 16);
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        /// <summary>地域画面: カーソルが乗っている有効な地名 (area_name_tbl[num] の番号)。無ければ -1。矩形は init_japan_vram_sa のタッチ判定と同じ。</summary>
        private static int HitArea(Core c)
        {
            int num = c.world_jump_pos_sub / 16;
            if (num < 0 || num >= c.area_name_tbl.Length) return -1;
            int[] t = c.area_name_tbl[num];
            Vector2 p = new Vector2(s_x, s_y);
            for (int j = 0; (j + 1) * AREA_STRIDE <= t.Length && t[j * AREA_STRIDE] != 255; j++)
            {
                int b = j * AREA_STRIDE;
                if ((c.get_flag_atai(t[b]) & t[b + 1]) == 0) continue;
                Rect r = AreaObjRect(c, num, j);
                r.x += 8f;
                r.y -= 25f;
                Vector2 size = r.size;
                r.size *= AREA_RECT_SCALE;
                r.x -= size.x * ((AREA_RECT_SCALE - 1f) * 0.5f);
                r.y -= size.y * ((AREA_RECT_SCALE - 1f) * 0.5f);
                if (r.Contains(p)) return j;
            }
            return -1;
        }

        /// <summary>Core.get_world_map_obj_rect と同じ (private なので再現)。</summary>
        private static Rect AreaObjRect(Core c, int num, int j)
        {
            int[] t = c.area_name_tbl[num];
            int b = j * AREA_STRIDE;
            int obj = t[b + 9];
            if (obj != -1 && Main.map != null && Main.map.m_bg_map != null)
            {
                try { return Main.map.m_bg_map.GetObjectRect(obj); }
                catch (System.Exception) { }
            }
            return new Rect(Sys.get_screen_x(t[b + 5]), Sys.get_screen_y(t[b + 6]), 150f, 36f);
        }
    }

    /// <summary>本体のカーソル画像の描画 (アイコンの位置) を自由カーソルの位置へ差し替える。</summary>
    [HarmonyPatch(typeof(Util.Sprite), nameof(Util.Sprite.Draw), typeof(float), typeof(float))]
    internal static class WorldMapCursorDrawPatch
    {
        private static bool s_scoped;

        private static void Prefix(Util.Sprite __instance, ref float x, ref float y)
        {
            if (!WorldMapCursorPatch.Active || WorldMapCursorPatch.CursorSprite == null) return;
            if (!ReferenceEquals(__instance, WorldMapCursorPatch.CursorSprite)) return;
            x = WorldMapCursorPatch.DrawX;
            y = WorldMapCursorPatch.DrawY;
            // 60fps 表示 (Interp60) で前の論理フレームとの中点に補間してもらう (Core.draw の外なので明示的にスコープに入れる)
            Interp60.EnterCursor();
            s_scoped = true;
        }

        private static void Postfix()
        {
            if (!s_scoped) return;
            s_scoped = false;
            Interp60.LeaveCursor();
        }
    }

    /// <summary>大陸画面で何も乗っていない時は、本体が putcur_no の大陸に掛けるハイライト (opt 3) を消す。</summary>
    [HarmonyPatch(typeof(BgMap), nameof(BgMap.set_mask_visible), typeof(int), typeof(int), typeof(int))]
    internal static class WorldMapCursorMaskPatch
    {
        private static void Prefix(ref int opt)
        {
            if (!WorldMapCursorPatch.Active || WorldMapCursorPatch.Hover) return;
            if (opt == 3) opt = 0;
        }
    }

    /// <summary>自由カーソル中は GetMenuInput の方向 (瞬間移動) を消し、何も乗っていなければ決定も消す。マウスのドラッグ (GetSlideInput) もここで止まる。</summary>
    [HarmonyPatch(typeof(InputManager), nameof(InputManager.GetMenuInput))]
    internal static class WorldMapCursorMenuInputPatch
    {
        private static void Postfix(ref int __result)
        {
            if (!WorldMapCursorPatch.Active) return;
            int v = __result;
            if (v == 0x10000 || v == 0x20000 || v == 0x40000 || v == 0x80000) __result = -1;
            else if (v == 0x100001 && !WorldMapCursorPatch.Hover) __result = -1;
        }
    }
}
