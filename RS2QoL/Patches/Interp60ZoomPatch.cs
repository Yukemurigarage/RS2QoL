using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 60fps 表示 (Interp60) で、敵シンボルに接触した時の拡大演出も補間する。
    ///
    /// 本体は Core.sync の exdemo (EXDEMO_into_kakudai) で、通常の描画 (put_people / draw) を止め、赤い塗りの上に
    /// 接触した敵シンボルの Sys.Sprite だけを Sys.ordersprite.draw(32769) で描く。大きさは exdemo_proc[3] を毎論理フレーム
    /// 1.3 倍にして m_scl / SetScale に入れるので、30 コマ/秒の段階的な拡大になる (16 コマ、約 0.5 秒)。
    /// Util.Sprite.Draw は位置も大きさも _Pos (x, y, w, h) に入れるので、このスコープの中では Interp60 が 4 成分とも
    /// 前の論理フレームとの中点にする (Interp60.DrawMeshPrefix の s_zoomDepth の分岐)。
    /// </summary>
    [HarmonyPatch(typeof(Sys.OrderingSprite), nameof(Sys.OrderingSprite.draw))]
    internal static class Interp60ZoomPatch
    {
        private static bool s_scoped;

        private static void Prefix(int mode)
        {
            if (mode != 32769 || !Interp60.Active) return;
            if (Main.core == null || (Main.core.exdemo_flg & Core.EXDEMO_into_kakudai) == 0) return;
            Interp60.EnterZoom();
            s_scoped = true;
        }

        private static void Postfix()
        {
            if (!s_scoped) return;
            s_scoped = false;
            Interp60.LeaveZoom();
        }
    }
}
