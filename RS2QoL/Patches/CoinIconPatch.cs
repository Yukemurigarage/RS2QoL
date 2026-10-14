using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 所持金の横のクラウンの絵 (SpriteStudio の部品 icon_coin。メニュー 2_a、店 1_b_1 など) の大きさ
    /// ([Display] CoinIconScalePercent、既定 70) と上へのずらし ([Display] CoinIconOffsetY、既定 1)。
    ///
    /// 画面の SpriteStudio データ (menu/sspj/*.ssb) は、部品ごとの四角形の頂点座標を共通の頂点配列 (SSObject.m_vtx_array / m_uv_array)
    /// に持ち、各アニメの各フレームのポリゴン (Anime.Frame.Poly) がその添字 4 つを指す。部品はマテリアル (m_render_state[poly.m_mtl_id].m_parts_id)
    /// で分かる。クラウンは画像アトラスの一部なので矢印のようにテクスチャを差し替えても小さくならず、ここでは SSObject.Load の後ろで
    /// icon_coin の四角形の 4 頂点を中心に向けて縮めた頂点を配列の末尾に足し、ポリゴンの添字をそこへ付け替える
    /// (元の頂点は他の部品と共有している可能性があるので触らない)。同じ添字は 1 回だけ作る (フレームごとに同じ四角形を指すため)。
    /// 画面はメニューを開くたびに作り直される (SpriteStudioManager.AddSpriteStudio → Load) ので、設定を変えたら次に開いた画面から効く。
    /// </summary>
    internal static class CoinIcon
    {
        public static ConfigEntry<int> Scale;
        private const string PART = "icon_coin";

        public static void Bind(ConfigFile config)
        {
            Scale = config.Bind("Display", "CoinIconScalePercent", 70,
                new ConfigDescription("所持金の横のクラウンの絵 (メニュー・店など) の大きさ (%)。100 が本体どおり、既定 70。絵の中心を動かさずに縮める。設定を変えたら次に開いた画面から効く",
                    new AcceptableValueRange<int>(30, 100)));
            OffsetY = config.Bind("Display", "CoinIconOffsetY", 1,
                new ConfigDescription("所持金の横のクラウンの絵を上にずらすドット数 (仮想 960x540 の座標。負なら下)。縮めた時に金額の数字と高さを合わせる用 (既定 1)。設定を変えたら次に開いた画面から効く",
                    new AcceptableValueRange<int>(-30, 30)));
        }

        /// <summary>上にずらすドット数 (仮想 960x540 の座標。正で上、負で下)。頂点の y は上が正 (画面 y = 270 - 頂点 y) なのでそのまま足す。</summary>
        public static ConfigEntry<int> OffsetY;

        public static int Percent { get { return Scale != null ? Mathf.Clamp(Scale.Value, 30, 100) : 100; } }
        public static int Up { get { return OffsetY != null ? Mathf.Clamp(OffsetY.Value, -30, 30) : 0; } }

        public static void CycleUp(int dir)
        {
            int v = Up + dir;
            if (v > 30) v = -30; else if (v < -30) v = 30;
            OffsetY.Value = v;
        }

        public static string UpLabel(int v) { return v == 0 ? "0 (本体どおり)" : (v > 0 ? "上に " + v + " ドット" : "下に " + (-v) + " ドット"); }

        public static void Cycle(int dir)
        {
            int v = Percent + dir * 10;
            if (v > 100) v = 30; else if (v < 30) v = 100;
            Scale.Value = v;
        }

        public static string Label(int v) { return v == 100 ? "100 % (本体どおり)" : v + " %"; }

        private static bool s_logged;

        /// <summary>fname の画面の icon_coin を percent % に縮める。作った頂点の数を返す (0 = その画面に無い)。</summary>
        internal static int Apply(SSObject obj, string fname, int percent, int up)
        {
            if (obj == null || obj.m_anime == null || obj.m_render_state == null || obj.m_vtx_array == null || obj.m_parts_list == null) return 0;
            SSObject.Parts parts = obj.GetParts(PART);
            if (parts == null) return 0;
            float k = percent / 100f;
            List<Vector3> addV = new List<Vector3>();
            List<Vector2> addUV = new List<Vector2>();
            Dictionary<long, int> made = new Dictionary<long, int>();   // (四角形の 4 添字の組, 位置) → 新しい添字
            int baseIndex = obj.m_vtx_array.Length;
            foreach (SSObject.Anime anime in obj.m_anime)
            {
                if (anime == null || anime.m_frames == null) continue;
                foreach (SSObject.Frame frame in anime.m_frames)
                {
                    if (frame.m_meshes == null) continue;
                    foreach (SSObject.Poly poly in frame.m_meshes)
                    {
                        if (poly.m_vtx == null || poly.m_mtl_id < 0 || poly.m_mtl_id >= obj.m_render_state.Length) continue;
                        if (obj.m_render_state[poly.m_mtl_id].m_parts_id != parts.m_id) continue;
                        for (int q = 0; q + 3 < poly.m_vtx.Length; q += 4)
                        {
                            int i0 = poly.m_vtx[q], i1 = poly.m_vtx[q + 1], i2 = poly.m_vtx[q + 2], i3 = poly.m_vtx[q + 3];
                            if (i0 >= baseIndex) continue;   // もう付け替え済み
                            Vector3 c = (obj.m_vtx_array[i0] + obj.m_vtx_array[i1] + obj.m_vtx_array[i2] + obj.m_vtx_array[i3]) / 4f;
                            long key = ((long)i0 << 48) | ((long)i1 << 32) | ((long)i2 << 16) | (long)i3;
                            int first;
                            if (!made.TryGetValue(key, out first))
                            {
                                first = baseIndex + addV.Count;
                                made[key] = first;
                                for (int t = 0; t < 4; t++)
                                {
                                    int src = poly.m_vtx[q + t];
                                    Vector3 v = obj.m_vtx_array[src];
                                    addV.Add(new Vector3(c.x + (v.x - c.x) * k, c.y + (v.y - c.y) * k + up, v.z));
                                    addUV.Add(obj.m_uv_array[src]);
                                }
                            }
                            for (int t = 0; t < 4; t++) poly.m_vtx[q + t] = first + t;   // m_vtx は配列 (参照) なので struct 越しでも書ける
                        }
                    }
                }
            }
            if (addV.Count == 0) return 0;
            Vector3[] nv = new Vector3[baseIndex + addV.Count];
            Vector2[] nuv = new Vector2[baseIndex + addUV.Count];
            Array.Copy(obj.m_vtx_array, nv, baseIndex);
            Array.Copy(obj.m_uv_array, nuv, baseIndex);
            addV.CopyTo(nv, baseIndex);
            addUV.CopyTo(nuv, baseIndex);
            obj.m_vtx_array = nv;
            obj.m_uv_array = nuv;
            obj.m_nvtx = nv.Length;
            if (!s_logged)
            {
                s_logged = true;
                Plugin.Log.LogInfo("[CoinIcon] applied: " + fname + " " + percent + "% up " + up + " (" + addV.Count / 4 + " quads)");
            }
            return addV.Count;
        }
    }

    [HarmonyPatch(typeof(SSObject), nameof(SSObject.Load), new Type[] { typeof(string), typeof(bool) })]
    internal static class CoinIconLoadPatch
    {
        private static void Postfix(SSObject __instance, string fname, bool __result)
        {
            if (!__result) return;
            int percent = CoinIcon.Percent, up = CoinIcon.Up;
            if (percent == 100 && up == 0) return;
            try { CoinIcon.Apply(__instance, fname, percent, up); }
            catch (Exception e) { Plugin.Log.LogWarning("[CoinIcon] failed: " + fname + ": " + e.Message); }
        }
    }
}
