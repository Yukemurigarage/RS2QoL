using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 長時間プレイで徐々に重くなる ("もっさり") 問題の計測 ([Diagnostics] MemoryLogSeconds、Diagnostics の Enabled とは独立、既定 0 = 出さない)。
    ///
    /// 一定秒ごとに 1 行 ([Mem]) を出す: Mono (Boehm GC) のヒープ使用量と GC 回数、Unity 側の確保量、プロセスのワーキングセット、
    /// Unity オブジェクトの数 (Material / Mesh / Texture2D / GameObject / 全体)、フォントのアトラスの大きさ、本体の
    /// 増えうるリスト (タッチ判定の矩形、SpriteStudio オブジェクト、SSObject のテクスチャ、GS の一時 Mesh/Material 解放待ち、
    /// 登録テクスチャ)、MOD の追跡表 (Interp60)、そしてその間のフレーム時間 (Unity フレーム間隔の平均・最大・33ms 超の数) と
    /// GameMain.Update 自体の所要時間 (平均・最大)。
    ///
    /// 見方: 「Update の所要時間が伸びる」なら本体の CPU 側 (リストが増えて毎フレームの走査が重くなる等)、
    /// 「Update は変わらずフレーム間隔だけ伸びる」なら GC の停止 (Mono ヒープの肥大) か GPU / ドライバ側、
    /// 「オブジェクト数や Mono ヒープが右肩上がり」ならリーク。再起動で直るという報告と突き合わせる。
    /// </summary>
    internal static class MemDiag
    {
        public static ConfigEntry<int> IntervalSeconds;

        private static readonly Stopwatch s_clock = Stopwatch.StartNew();
        private static double s_nextMs = -1;
        private static double s_prevFrameMs = -1;
        private static double s_updateStartMs = -1;
        private static int s_frames, s_longFrames;
        private static double s_frameSum, s_frameMax;
        private static int s_updates;
        private static double s_updateSum, s_updateMax;
        private static int s_prevGc0 = -1;

        // 本体の private static フィールド (無ければ null のまま、その項目は出さない)
        private static readonly FieldInfo s_gsMeshRelease = AccessTools.Field(typeof(GS), "m_mesh_release_list");
        private static readonly FieldInfo s_gsMtlRelease = AccessTools.Field(typeof(GS), "m_material_release_list");
        private static readonly FieldInfo s_gsFont = AccessTools.Field(typeof(GS), "m_font");
        private static readonly FieldInfo s_utilRegistTex = AccessTools.Field(typeof(Util), "m_regist_texture");

        public static void Bind(ConfigFile config)
        {
            IntervalSeconds = config.Bind("Diagnostics", "MemoryLogSeconds", 0,
                new ConfigDescription("この秒数ごとにメモリ・オブジェクト数・フレーム時間を 1 行 ([Mem]) で出す (長時間プレイで重くなる問題の計測用。Diagnostics の Enabled とは独立で、これだけ ON にできる)。0 (既定) で出さない", new AcceptableValueRange<int>(0, 3600)));
        }

        private static bool Enabled
        {
            get { return IntervalSeconds != null && IntervalSeconds.Value > 0; }
        }

        /// <summary>GameMain.Update の Prefix (複製フレームで止めた時も呼ばれる)。フレーム間隔を積む。</summary>
        internal static void OnUpdateStart()
        {
            if (!Enabled) { s_nextMs = -1; return; }
            double now = s_clock.Elapsed.TotalMilliseconds;
            if (s_prevFrameMs >= 0)
            {
                double dt = now - s_prevFrameMs;
                s_frames++;
                s_frameSum += dt;
                if (dt > s_frameMax) s_frameMax = dt;
                if (dt > 33.0) s_longFrames++;
            }
            s_prevFrameMs = now;
            s_updateStartMs = now;
        }

        /// <summary>GameMain.Update の Postfix。Update の所要時間を積み、間隔が来たら 1 行出す。</summary>
        internal static void OnUpdateEnd()
        {
            if (!Enabled) return;
            double now = s_clock.Elapsed.TotalMilliseconds;
            if (s_updateStartMs >= 0)
            {
                double dt = now - s_updateStartMs;
                s_updates++;
                s_updateSum += dt;
                if (dt > s_updateMax) s_updateMax = dt;
            }
            if (s_nextMs < 0) { s_nextMs = now + IntervalSeconds.Value * 1000.0; return; }
            if (now < s_nextMs) return;
            s_nextMs = now + IntervalSeconds.Value * 1000.0;
            try { Report(); }
            catch (Exception e) { Plugin.Log.LogWarning("[Mem] " + e); }
            s_frames = s_longFrames = 0; s_frameSum = s_frameMax = 0;
            s_updates = 0; s_updateSum = s_updateMax = 0;
        }

        private static void Report()
        {
            var sb = new System.Text.StringBuilder(512);
            sb.Append("[Mem] t=").Append(InputDiag.T()).Append(" up=").Append((int)Time.realtimeSinceStartup).Append('s');

            // Mono (Boehm GC) ヒープ
            long monoUsed = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            long monoHeap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
            int gc0 = GC.CollectionCount(0);
            sb.Append(" | mono used/heap=").Append(Mb(monoUsed)).Append('/').Append(Mb(monoHeap)).Append("MB gcTotal=").Append(Mb(GC.GetTotalMemory(false))).Append("MB gc=").Append(gc0);
            if (s_prevGc0 >= 0) sb.Append(" (+").Append(gc0 - s_prevGc0).Append(')');
            s_prevGc0 = gc0;

            // Unity 側 (ネイティブ) とプロセス
            sb.Append(" | unity alloc/reserved=").Append(Mb(UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong())).Append('/').Append(Mb(UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong())).Append("MB");
            try
            {
                Process p = Process.GetCurrentProcess();
                sb.Append(" ws/private=").Append(Mb(p.WorkingSet64)).Append('/').Append(Mb(p.PrivateMemorySize64)).Append("MB");
            }
            catch { }

            // Unity オブジェクトの数 (FindObjectsOfTypeAll は数 ms かかるので間隔ごとにだけ)
            sb.Append(" | objs all=").Append(Resources.FindObjectsOfTypeAll(typeof(UnityEngine.Object)).Length)
              .Append(" mtl=").Append(Resources.FindObjectsOfTypeAll(typeof(Material)).Length)
              .Append(" mesh=").Append(Resources.FindObjectsOfTypeAll(typeof(Mesh)).Length)
              .Append(" tex2d=").Append(Resources.FindObjectsOfTypeAll(typeof(Texture2D)).Length)
              .Append(" go=").Append(Resources.FindObjectsOfTypeAll(typeof(GameObject)).Length);

            // フォントのアトラス (動的フォントは使った文字が増えると大きくなる)
            Font[] fonts = s_gsFont != null ? s_gsFont.GetValue(null) as Font[] : null;
            if (fonts != null)
            {
                sb.Append(" | font=");
                for (int i = 0; i < fonts.Length; i++)
                {
                    Texture t = fonts[i] != null && fonts[i].material != null ? fonts[i].material.mainTexture : null;
                    if (i > 0) sb.Append(',');
                    sb.Append(t != null ? t.width + "x" + t.height : "-");
                }
            }

            // 本体の増えうるリスト
            sb.Append(" | game touchRects=").Append(InputManager.get_register_touch_num())
              .Append(" ss=").Append(SpriteStudioManager.GetSpriteStudioCount())
              .Append(" ssTex=").Append(SSObject.m_tex_list != null ? SSObject.m_tex_list.Count : -1)
              .Append(" imgDel=").Append(Image.m_delete_list != null ? Image.m_delete_list.Count : -1);
            sb.Append(" gsRelease=").Append(ListTotal(s_gsMeshRelease)).Append('/').Append(ListTotal(s_gsMtlRelease));
            Hashtable reg = s_utilRegistTex != null ? s_utilRegistTex.GetValue(null) as Hashtable : null;
            sb.Append(" regTex=").Append(reg != null ? reg.Count : -1);

            // MOD の追跡表
            sb.Append(" | mod ").Append(Interp60.TrackText());

            // フレーム時間
            sb.Append(" | frames n=").Append(s_frames)
              .Append(" avg=").Append((s_frames > 0 ? s_frameSum / s_frames : 0).ToString("F2")).Append("ms max=").Append(s_frameMax.ToString("F1")).Append("ms >33ms=").Append(s_longFrames)
              .Append(" | update n=").Append(s_updates)
              .Append(" avg=").Append((s_updates > 0 ? s_updateSum / s_updates : 0).ToString("F2")).Append("ms max=").Append(s_updateMax.ToString("F1")).Append("ms");
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static string Mb(long bytes) { return (bytes / 1048576.0).ToString("F1"); }

        private static int ListTotal(FieldInfo f)
        {
            if (f == null) return -1;
            IList[] lists = f.GetValue(null) as IList[];
            if (lists == null) return -1;
            int n = 0;
            for (int i = 0; i < lists.Length; i++) if (lists[i] != null) n += lists[i].Count;
            return n;
        }
    }

    /// <summary>GameMain.Update の前後で時間を測る (Interp60 が止めたフレームでも Prefix / Postfix は呼ばれる)。</summary>
    [HarmonyPatch(typeof(GameMain), "Update")]
    internal static class MemDiagUpdatePatch
    {
        private static void Prefix() { MemDiag.OnUpdateStart(); }
        private static void Postfix() { MemDiag.OnUpdateEnd(); }
    }
}
