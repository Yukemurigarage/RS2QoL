using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 60fps 表示の戦闘用補間 (実験的、[Display] Experimental60Battle、既定 On)。
    ///
    /// 戦闘の描画 (Battle.sync → Effect.draw) で連続的に動くのは、味方キャラ (前に出る・戻る・逃げる歩き) と
    /// ダメージ数字の跳ね (1px/フレーム) くらいで、それ以外 (モンスターの焼き込み頂点アニメ、エフェクトのタイル、
    /// 背景の揺れ) は手付けのコマ送りなので複製のまま。
    ///
    /// フィールドの SmoothAll はマテリアルの同一性で「同じスプライトの前回位置」を引くが、戦闘の味方は
    /// Sys.drawImageDepth の一時スプライトリング (256 個を順繰り、毎回 Init → Term で Material を作り直す) で
    /// 描かれるのでマテリアルでは追えない。そこで描画スコープから「論理キー」を決めて前回位置を引く:
    /// - Effect.drawPlayer(no, ...) の中: キー = 味方番号 ((no/256)&7) (+8 で flg=1 の別描画)。
    ///   味方の足元の影は (no & 0xFF) == 255 の別の OAM エントリで、本体が GS.FillRect2 (位置を頂点に焼き込んだ矩形 2 枚、
    ///   _Pos なし、単位行列) で描く。これを補間しないと歩く間だけ体と影が半歩ずれて影がちらつく (等倍以外の戦闘速度で目立つ)
    ///   ので、キー = 32 + 味方キー*2 + 矩形の番号 で頂点の位置を覚え、描画行列の平行移動で体と同じ半歩を戻す。
    /// - BattleUIDamage.Draw(value, x, y) の中: キー = 100 + その論理フレーム内の通し番号 (UpdateDamageUI で 0 に戻る)。
    ///   通し番号は前に表示中の数字が消えるとずれるので、ダメージは「x が同じで y が数 px 以内」の時だけ補間する
    ///   (跳ねは縦 1px/フレームなので十分。隣の桁 (横 9px) や別の対象の数字とは混ざらない)。
    /// 補間のやり方は SmoothAll と同じ半フレーム遅れの中点 (論理フレームで _Pos を中点にし、複製フレームで元の値に戻す)。
    /// 一時スプライトの Material は次の論理フレームの Init まで生きるので複製フレームでの再発行は問題ない。
    /// </summary>
    internal static class Interp60Battle
    {
        public static ConfigEntry<bool> Enabled;

        private const float DamageMaxStep = 10f;  // ダメージ数字の縦移動の上限 (単位: 960x540 の px。本体の跳ね (dam_poyon_sub) は 1 論理フレーム 4 ゲーム px = 9 単位)

        private static int s_key = -1;             // 今の論理キー (-1 = スコープ外)
        private static bool s_shadow;              // drawPlayer の中で、今描いているのが足元の影 ((no & 0xFF) == 255 の FillRect2) か
        private static int s_shadowOrdinal;        // その影の何枚目の矩形か (0, 1)
        private static int s_damageOrdinal;        // この論理フレームで何個目のダメージ数字か
        private static int s_effectDepth;          // Effect.draw の中か (診断用)

        private class Track { public int frame = -10; public Vector4 pos; }
        private static readonly Dictionary<int, Track> s_tracks = new Dictionary<int, Track>();

        // 診断
        private static int s_statEffectCalls, s_statPlayer, s_statDamage, s_statNoPos;
        private static int s_statPlayerMoved, s_statPlayerInterp, s_statPlayerTooFar, s_statDmgMoved, s_statDmgInterp, s_statDmgTooFar;
        private static int s_statShadow, s_statShadowInterp;
        private static int s_moveLogLeft = 60;      // 味方が動いた最初の数十回は (キー, dx, dy) を出す (歩幅の実測用)
        // モンスター (SSObject の焼き込み頂点アニメ) が毎論理フレームで頂点を変えているか (次段のモンスター補間の価値を測る)
        private static readonly Dictionary<SSObject.Anime, int> s_ssLast = new Dictionary<SSObject.Anime, int>();
        private static int s_statSsDraw, s_statSsAdvanced, s_statSsVtxChanged, s_statSsTopoMatch;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Display", "Experimental60Battle", true,
                "60fps 表示 (Experimental60 が SmoothMap / SmoothAll の時) で、戦闘の味方キャラの移動とダメージ数字の跳ねも補間する (実験的、既定 On)。モンスターやエフェクトは 30 コマのまま");
        }

        /// <summary>補間を行う条件 (60fps 表示が補間ありで効いていて、このオプションが On)。</summary>
        public static bool Active
        {
            get
            {
                return Enabled != null && Enabled.Value && Interp60.Active
                    && Interp60.Mode.Value != Interp60Kind.Duplicate;
            }
        }

        /// <summary>今の論理キー (-1 ならスコープ外)。Interp60.DrawMeshPrefix から参照。</summary>
        public static int Key { get { return s_key; } }
        public static bool InEffect { get { return s_effectDepth > 0; } }

        /// <summary>Interp60.OnFrameStart から毎 Unity フレーム。例外などでスコープが残っても引きずらないよう戻す。</summary>
        public static void OnFrameStart()
        {
            s_key = -1; s_shadow = false; s_damageOrdinal = 0; s_effectDepth = 0;
        }

        public static void EnterPlayer(int no, int flg)
        {
            if (!Active) return;
            s_key = ((no / 256) & 7) + (flg != 0 ? 8 : 0);
            s_shadow = (no & 0xFF) == 255;
            s_shadowOrdinal = 0;
            s_statPlayer++;
        }

        public static void EnterDamage()
        {
            if (!Active) return;
            s_key = 100 + s_damageOrdinal++;
            s_statDamage++;
        }

        public static void Leave() { s_key = -1; s_shadow = false; }
        public static void ResetDamage() { s_damageOrdinal = 0; }
        public static void EnterEffect() { s_effectDepth++; }
        public static void LeaveEffect() { if (s_effectDepth > 0) s_effectDepth--; }
        public static void CountEffectCall() { s_statEffectCalls++; }

        /// <summary>
        /// Interp60.DrawMeshPrefix から (補間する論理フレームで、キーがある時だけ)。_Pos を持つマテリアルなら
        /// 元の値を hasPos/orig に返し (複製フレームで戻す)、前の論理フレームにも同じキーで描かれていれば中点にする。
        /// </summary>
        public static void Interpolate(Mesh mesh, ref Matrix4x4 matrix, Material material, int logicFrame, float maxStep, ref bool hasPos, ref Vector4 orig)
        {
            if (!material.HasProperty("_Pos"))
            {
                s_statNoPos++;
                if (s_shadow) InterpolateShadow(mesh, ref matrix, logicFrame, maxStep);   // drawPlayer の 255 番 (足元の影の FillRect2)
                return;
            }
            Vector4 cur = material.GetVector("_Pos");
            hasPos = true; orig = cur;
            Track t;
            if (!s_tracks.TryGetValue(s_key, out t)) { t = new Track(); s_tracks[s_key] = t; }
            if (t.frame == logicFrame - 1)
            {
                float dx = cur.x - t.pos.x, dy = cur.y - t.pos.y;
                if (dx != 0f || dy != 0f)
                {
                    bool damage = s_key >= 100;
                    bool ok = damage
                        ? dx == 0f && Mathf.Abs(dy) <= DamageMaxStep
                        : Mathf.Abs(dx) <= maxStep && Mathf.Abs(dy) <= maxStep;
                    if (damage) s_statDmgMoved++; else s_statPlayerMoved++;
                    if (!damage && s_moveLogLeft > 0 && Plugin.DiagEnabled != null && Plugin.DiagEnabled.Value)
                    {
                        s_moveLogLeft--;
                        Plugin.Log.LogInfo(string.Format("[Interp60B] player key={0} moved dx={1:F2} dy={2:F2} ({3})", s_key, dx, dy, ok ? "interp" : "tooFar"));
                    }
                    if (ok)
                    {
                        material.SetVector("_Pos", new Vector4(cur.x - dx * 0.5f, cur.y - dy * 0.5f, cur.z, cur.w));
                        if (damage) s_statDmgInterp++; else s_statPlayerInterp++;
                    }
                    else if (damage) s_statDmgTooFar++; else s_statPlayerTooFar++;
                }
            }
            t.frame = logicFrame; t.pos = cur;
        }

        /// <summary>
        /// 味方の足元の影 (GS.FillRect2 の矩形。位置は頂点に焼き込まれ、行列は単位行列) を体と同じ半歩戻す。
        /// 頂点 0 (矩形の左上) の位置を 32 + 味方キー*2 + 矩形番号 のキーで覚え、前の論理フレームから 1 歩 (maxStep) 以内に
        /// 動いていれば描画行列の平行移動で中点へ。記録側 (Interp60.DrawMeshPrefix) は変更前の行列を覚えるので複製フレームでは元の位置に戻る。
        /// 頂点は 960x540 の画面単位で _Pos と同じ向き (x 右、y 上) なので、体の補間と同じ量になる。
        /// </summary>
        private static void InterpolateShadow(Mesh mesh, ref Matrix4x4 matrix, int logicFrame, float maxStep)
        {
            if (mesh == null) return;
            Vector3[] v = mesh.vertices;
            if (v == null || v.Length == 0) return;
            int key = 32 + s_key * 2 + (s_shadowOrdinal++ & 1);
            s_statShadow++;
            Track t;
            if (!s_tracks.TryGetValue(key, out t)) { t = new Track(); s_tracks[key] = t; }
            Vector4 cur = new Vector4(v[0].x, v[0].y, 0f, 0f);
            if (t.frame == logicFrame - 1)
            {
                float dx = cur.x - t.pos.x, dy = cur.y - t.pos.y;
                if ((dx != 0f || dy != 0f) && Mathf.Abs(dx) <= maxStep && Mathf.Abs(dy) <= maxStep)
                {
                    matrix.m03 -= dx * 0.5f;
                    matrix.m13 -= dy * 0.5f;
                    s_statShadowInterp++;
                }
            }
            t.frame = logicFrame; t.pos = cur;
        }

        /// <summary>
        /// SSObject.Anime.Draw の Prefix (診断のみ)。戦闘画面の中で、前回描いた時からアニメのフレーム番号が進んでいれば、
        /// 前のフレームと今のフレームで頂点 (共有頂点配列への添字) が変わっているか、ポリゴン構成 (メッシュ数・各ポリ数・
        /// マテリアル) が同じで頂点ブレンドできる形かを数える。
        /// </summary>
        public static void CountSsDraw(SSObject.Anime anime)
        {
            if (!InEffect || anime == null || anime.m_frames == null) return;
            s_statSsDraw++;
            int cur = anime.m_cur_frame;
            int last;
            if (s_ssLast.TryGetValue(anime, out last) && last != cur && last >= 0 && last < anime.m_frames.Length && cur >= 0 && cur < anime.m_frames.Length)
            {
                s_statSsAdvanced++;
                SSObject.Poly[] a = anime.m_frames[last].m_meshes, b = anime.m_frames[cur].m_meshes;
                bool topo = a != null && b != null && a.Length == b.Length;
                bool changed = !topo;
                for (int i = 0; topo && i < a.Length; i++)
                {
                    if (a[i].m_npoly != b[i].m_npoly || a[i].m_mtl_id != b[i].m_mtl_id || a[i].m_vtx == null || b[i].m_vtx == null || a[i].m_vtx.Length != b[i].m_vtx.Length) { topo = false; changed = true; break; }
                    for (int j = 0; j < a[i].m_vtx.Length; j++) if (a[i].m_vtx[j] != b[i].m_vtx[j]) { changed = true; break; }
                }
                if (changed) s_statSsVtxChanged++;
                if (topo) s_statSsTopoMatch++;
            }
            s_ssLast[anime] = cur;
            if (s_ssLast.Count > 256) s_ssLast.Clear();
        }

        /// <summary>[Interp60] の診断行に足す部分。呼ぶたびにカウンタを 0 に戻す。</summary>
        public static string StatText()
        {
            string s = string.Format(
                " | battle={0} effectCalls={1} player={2} (moved={3} interp={4} tooFar={5}) shadow={16} (interp={17}) damage={6} (moved={7} interp={8} tooFar={9}) noPos={10} keys={11} ss: draw={12} advanced={13} vtxChanged={14} topoMatch={15}",
                Enabled != null && Enabled.Value ? "on" : "off", s_statEffectCalls,
                s_statPlayer, s_statPlayerMoved, s_statPlayerInterp, s_statPlayerTooFar,
                s_statDamage, s_statDmgMoved, s_statDmgInterp, s_statDmgTooFar,
                s_statNoPos, s_tracks.Count, s_statSsDraw, s_statSsAdvanced, s_statSsVtxChanged, s_statSsTopoMatch,
                s_statShadow, s_statShadowInterp);
            s_statEffectCalls = s_statPlayer = s_statDamage = s_statNoPos = 0;
            s_statPlayerMoved = s_statPlayerInterp = s_statPlayerTooFar = s_statDmgMoved = s_statDmgInterp = s_statDmgTooFar = 0;
            s_statShadow = s_statShadowInterp = 0;
            s_statSsDraw = s_statSsAdvanced = s_statSsVtxChanged = s_statSsTopoMatch = 0;
            return s;
        }
    }

    /// <summary>SSObject.Anime.Draw (モンスター等の SpriteStudio 描画) の Prefix: 診断カウンタのみ (Diagnostics が ON の時)。</summary>
    [HarmonyPatch(typeof(SSObject.Anime), nameof(SSObject.Anime.Draw))]
    internal static class Interp60BattleSsDiagPatch
    {
        private static void Prefix(SSObject.Anime __instance)
        {
            if (Plugin.DiagEnabled == null || !Plugin.DiagEnabled.Value) return;
            try { Interp60Battle.CountSsDraw(__instance); } catch { }
        }
    }

    /// <summary>Effect.drawPlayer (戦闘の味方キャラ 1 体の描画) の中をキー = 味方番号のスコープにする。</summary>
    [HarmonyPatch(typeof(Effect), nameof(Effect.drawPlayer))]
    internal static class Interp60BattlePlayerPatch
    {
        private static void Prefix(int no, int flg) { Interp60Battle.EnterPlayer(no, flg); }
        private static void Postfix() { Interp60Battle.Leave(); }
    }

    /// <summary>BattleUIDamage.Draw(value, x, y) (ダメージ数字 1 桁) の中をキー = 通し番号のスコープにする。</summary>
    [HarmonyPatch(typeof(BattleUIDamage), nameof(BattleUIDamage.Draw), typeof(int), typeof(int), typeof(int))]
    internal static class Interp60BattleDamagePatch
    {
        private static void Prefix() { Interp60Battle.EnterDamage(); }
        private static void Postfix() { Interp60Battle.Leave(); }
    }

    /// <summary>
    /// Sys.drawImageDamage (ダメージ表示の数字以外のグリフ: MISS 等。一時スプライトリングで描く) も同じ通し番号のキーにする。
    /// Effect.draw のダメージ描画ループからしか呼ばれず、BattleUIDamage.Draw と同じ順で回るので通し番号は一貫する。
    /// </summary>
    [HarmonyPatch(typeof(Sys), nameof(Sys.drawImageDamage))]
    internal static class Interp60BattleDamageGlyphPatch
    {
        private static void Prefix() { Interp60Battle.EnterDamage(); }
        private static void Postfix() { Interp60Battle.Leave(); }
    }

    /// <summary>BattleUIDamage.UpdateDamageUI (毎論理フレーム、描画の前) で通し番号を 0 に戻す。</summary>
    [HarmonyPatch(typeof(BattleUIDamage), nameof(BattleUIDamage.UpdateDamageUI))]
    internal static class Interp60BattleDamageResetPatch
    {
        private static void Postfix() { Interp60Battle.ResetDamage(); }
    }

    /// <summary>Effect.draw (戦闘画面の描画全体) の中の DrawMesh 数を数える (診断用)。</summary>
    [HarmonyPatch(typeof(Effect), nameof(Effect.draw))]
    internal static class Interp60BattleEffectScopePatch
    {
        private static void Prefix() { Interp60Battle.EnterEffect(); }
        private static void Postfix() { Interp60Battle.LeaveEffect(); }
    }
}
