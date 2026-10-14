using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// ワールドマップ (大陸の一覧 / 地域の地名一覧) をキャンセルで閉じる (町へ戻る)。
    ///
    /// 本体 (Core.select_chiikimap) は「とじる」のビット 0x200000 (本体の Y / 画面の BID_WLD_CLOSE) で町へ戻る
    /// (大陸画面 exevt_proc[1] == 120: b_trig_event の町へ its_outworld → 905。地域画面 == 20: exevt_proc[0] == 3 の時だけ its_outworld → 900)。
    /// キャンセルのビット 0x400000 は select_chiikimap のどこでも読まれず、押しても何も起きない。
    /// SFC 版の B と同じく、キャンセル (キーボードの Esc / 左 Shift、パッドの A) でも閉じられるよう、この呼び出しの間だけ
    /// Sys.key[1] (押下エッジ) の 0x400000 を 0x200000 にも写す。Postfix で外す (同じフレームの他の処理に「とじる」を見せない)。
    /// 見るだけ (world_r_mode == 0 かつ exevt_proc[0] != 3) とズーム中・フェード中は本体も入力を見ないので触らない。
    /// 地域画面の「大陸一覧へ戻る」は本体どおり 0x20 (マップ移動 / 情報の機能。既定 M) のまま。
    /// WorldMapCursorPatch も同じメソッドの Prefix / Postfix で Sys.key[1] を保存・復元するので、こちらは Priority.Low で
    /// Postfix を後に回し、足した分を最後に確実に外す。
    /// </summary>
    [HarmonyPatch(typeof(Core), nameof(Core.select_chiikimap))]
    [HarmonyPriority(Priority.Low)]
    internal static class WorldMapCancelPatch
    {
        private const int BIT_CANCEL = 0x400000, BIT_CLOSE = 0x200000;
        private const int ST_AREA = 20, ST_WORLD = 120;
        private static bool s_added, s_logged, s_loggedApplied;

        private static void Prefix(Core __instance)
        {
            if (!s_logged) { s_logged = true; Plugin.Log.LogInfo("[WorldMapCancel] patched: Core.select_chiikimap (キャンセルでワールドマップを閉じる)"); }
            s_added = false;
            Core c = __instance;
            if (c.exevt_proc == null) return;
            int st = c.exevt_proc[1];
            if (st != ST_AREA && st != ST_WORLD) return;
            if (c.world_r_mode == 0 && c.exevt_proc[0] != 3) return;   // 見るだけ
            if (c.world_size != 240 || Sys.fadeflg) return;             // ズーム中・フェード中
            if ((Sys.key[1] & BIT_CANCEL) == 0 || (Sys.key[1] & BIT_CLOSE) != 0) return;
            Sys.key[1] |= BIT_CLOSE;
            s_added = true;
            if (!s_loggedApplied) { s_loggedApplied = true; Plugin.Log.LogInfo("[WorldMapCancel] applied: cancel → close (state " + st + ")"); }
        }

        private static void Postfix()
        {
            if (!s_added) return;
            Sys.key[1] &= ~BIT_CLOSE;
            s_added = false;
        }
    }
}
