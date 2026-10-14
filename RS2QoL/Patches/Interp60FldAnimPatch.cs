using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 60fps 表示で、フィールドの 3D オブジェクト (FldObject) の鍵フレームアニメを複製フレームでも半歩進んだ姿で描く。
    /// 主な対象はワールドマップ (大陸・地域画面) の開閉で広がる / 縮む巻物 (Core.m_fld_world_open / m_fld_world_close、
    /// fld/wld_waku_open / wld_waku_close、SetFlameSpeed(4))。フィールドの動く 3D オブジェクトも同じ仕組みで動く。
    ///
    /// FldObject のアニメは FLD_MOTION (鍵フレームの列) を fld_motion_update が cur_frame で評価する。cur_frame は論理フレームごとに
    /// s_seq_frame_rate_step (= m_frame_rate_speed。巻物は 4、既定 2) 進み、鍵と鍵の間は t = (cur_frame - k1.frame) / (k2.frame - k1.frame)
    /// の float で補間される。つまりデータは cur_frame の中間 (半歩先) の姿をそのまま持っていて、cur_frame + step/2 で評価すれば
    /// 本体が 60 コマで作ったのと同じ正確な中間姿勢が出る (スクロールやスプライトの「半フレーム遅れの中点」と違い、遅れも行き過ぎもない)。
    ///
    /// FLdDrawer.Update (MonoBehaviour、root ごとに 1 つ) は論理フレームで m_fld.Update() を呼んで姿を進め、複製フレームでは
    /// Interp60FldDrawerPatch が止めている。ここでは複製フレームに、直前の Unity フレームで「次に姿が変わる」アニメを評価した root の
    /// FldObject だけ、プレビューとして m_fld.Update() をもう一度呼ぶ (m_fld_out の子もその中で回る)。プレビュー中は
    /// fld_motion_update の Prefix で cur_frame を「直前の論理フレームが評価に使った値 + step/2」にして評価させ、Postfix で
    /// cur_frame / start_delay / repeat_num を元に戻す (本体の進行は変えない)。fld_motion_update は評価の後に cur_frame を step 進めるので、
    /// 評価に使った値は論理フレームの Prefix で motion ごとに覚えておく (評価後の値に足すと次の論理フレームより先の姿になって前後に揺れる)。パレットアニメ (update_plp) は _cur_frame を進める以外に姿を変えないのでプレビュー中は飛ばす。
    /// 姿勢 (pmv / c_pmv / seq_srt → model の localPosition 等、seq → 頂点と _ShapeWeight、sra → 色、emv → Camera.main) は全て
    /// 鍵からの純粋な関数で、次の論理フレームの Update が上書きするので、戻す必要はない。
    /// FLdDrawer の MeshRenderer の有効 / 無効と m_visible には触らない (複製フレームで物が消えないように前の状態を保つ)。
    /// 動きの無い (全ての FLD_MOTION が止まっている・終わっている) root は何もしない。倍速 x2 以上では複製フレームが無いので何もしない。
    /// </summary>
    internal static class Interp60FldAnim
    {
        public static ConfigEntry<bool> Enabled;

        private static readonly FieldInfo s_speedField = AccessTools.Field(typeof(FldObject), "m_frame_rate_speed");

        private static bool s_preview;        // プレビュー評価中 (fld_motion_update が cur_frame を半歩進めて評価し、戻す)
        private static int s_step, s_half;    // プレビュー中の root の 1 論理フレームの進み (m_frame_rate_speed) とその半分
        private static FldObject s_cur;       // FLdDrawer.Update の元処理の中: その root の FldObject
        // root の FldObject → 最後に「次の論理フレームで姿が変わる」アニメを評価した Unity フレーム
        private static readonly Dictionary<FldObject, int> s_animated = new Dictionary<FldObject, int>();
        private static readonly List<FldObject> s_prune = new List<FldObject>();
        // FLD_MOTION → 最後に姿を進めた論理フレームで評価した cur_frame (評価後に step 進むので、評価に使った値は cur_frame - step。
        // 終端で止まる・繰り返しで巻き戻る時は引き算で戻せないので覚えておく)。プレビューはこの値 + half で評価する
        private class MotTrack { public int frame = -10; public int evalCur; }
        private static readonly Dictionary<FldObject.FLD_MOTION, MotTrack> s_mots = new Dictionary<FldObject.FLD_MOTION, MotTrack>();
        private static readonly List<FldObject.FLD_MOTION> s_pruneMot = new List<FldObject.FLD_MOTION>();

        private static int s_statPreview, s_statPreviewMot, s_statHold, s_statHoldStep;

        // 診断ダンプ (Diagnostics ON): データパスごとに最初のプレビューで、各 FLD_MOTION の種類・鍵・対象の形を [I60fld] 行で出す
        private static readonly Dictionary<string, bool> s_dumped = new Dictionary<string, bool>();
        private static int s_dumpLeft;
        private static string s_curPath;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Display", "Experimental60FldAnim", true,
                "60fps 表示 (Experimental60 が SmoothAll の時) で、フィールドの 3D オブジェクトの鍵フレームアニメ (ワールドマップ開閉の巻物など) も半歩先の姿で描いて 60 コマにする (既定 On)");
        }

        /// <summary>プレビューを行う条件 (60fps 表示が SmoothAll で効いていて、このオプションが On)。</summary>
        public static bool Active
        {
            get { return Enabled != null && Enabled.Value && Interp60.Active && Interp60.Mode.Value == Interp60Kind.SmoothAll; }
        }

        internal static bool Previewing { get { return s_preview; } }

        /// <summary>
        /// FLdDrawer.Update の Prefix。論理フレームでは root を覚えて元処理へ (true)。複製フレームでは元処理を止め (false)、
        /// 直前の Unity フレームで動きのあるアニメを評価した root ならプレビューを 1 回回す。
        /// </summary>
        internal static bool OnDrawerUpdate(FLdDrawer drawer)
        {
            if (!Interp60.Skipping)
            {
                s_cur = drawer.m_fld;
                if ((Time.frameCount & 511) == 0) Prune();
                return true;
            }
            FldObject fld = drawer.m_fld;
            if (fld == null || !Active) return false;
            int f;
            if (!s_animated.TryGetValue(fld, out f) || f != Time.frameCount - 1) return false;
            try
            {
                int speed = s_speedField != null ? (int)s_speedField.GetValue(fld) : 2;
                s_step = speed; s_half = speed / 2;
                if (s_half <= 0) return false;
                s_curPath = fld.m_data_path ?? "?";
                s_dumpLeft = 0;
                if (Plugin.DiagEnabled != null && Plugin.DiagEnabled.Value && !s_dumped.ContainsKey(s_curPath))
                {
                    s_dumped[s_curPath] = true;
                    s_dumpLeft = 40;
                    Plugin.Log.LogInfo(string.Format("[I60fld] ==== {0} speed={1} half={2} frame={3}", s_curPath, speed, s_half, Time.frameCount));
                }
                s_preview = true;
                fld.Update();
                s_statPreview++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[Interp60FldAnim] preview: " + e + "\n-> Experimental60FldAnim を Off にします");
                if (Enabled != null) Enabled.Value = false;
            }
            finally { s_preview = false; }
            return false;
        }

        /// <summary>FLdDrawer.Update の Postfix (Prefix で止めた時も呼ばれる)。</summary>
        internal static void OnDrawerUpdateEnd() { s_cur = null; }

        public struct MotState { public bool saved; public int cur; public short delay, repeat; }

        /// <summary>この評価で姿が進むか (鍵があり、繰り返しが残り、開始待ちでなく、保持 (flag bit 2) でなく、終端でない)。</summary>
        private static bool WillMove(FldObject.FLD_MOTION mot)
        {
            return mot.nkey != 0 && mot._repeat_num != 0 && mot.start_delay <= 0 && (mot.flag & 2) == 0 && mot.cur_frame != mot.end_frame;
        }

        /// <summary>fld_motion_update の Prefix。論理フレームでは root に「動きあり」の印、プレビュー中は cur_frame を半歩進める。</summary>
        internal static void OnMotion(FldObject.FLD_MOTION mot, object keyType, FldObject.FLD_MAP_OBJ obj, out MotState st)
        {
            st = default(MotState);
            if (mot == null) return;
            if (!s_preview)
            {
                if (s_cur != null && WillMove(mot))
                {
                    s_animated[s_cur] = Time.frameCount;
                    MotTrack mt;
                    if (!s_mots.TryGetValue(mot, out mt)) { mt = new MotTrack(); s_mots[mot] = mt; }
                    mt.frame = Time.frameCount; mt.evalCur = mot.cur_frame;
                }
                return;
            }
            st.saved = true; st.cur = mot.cur_frame; st.delay = mot.start_delay; st.repeat = mot.repeat_num;
            // 直前の論理フレームがこの motion を evalCur で評価して進めたなら、その半歩先 (evalCur + half、終端で止める) を評価する。
            // 評価後の cur_frame に足すのは誤り (次の論理フレームより先の姿になり、0 → 6 → 4 → 10 → 8 と前後に揺れる)
            MotTrack t;
            int target = -1;
            if (s_mots.TryGetValue(mot, out t) && t.frame == Time.frameCount - 1 && mot.nkey != 0 && mot._repeat_num != 0 && (mot.flag & 2) == 0)
            {
                int next = t.evalCur + s_step;   // 次の論理フレームが評価する値 (終端で止まる・繰り返しで巻き戻る時は本体が直す)
                bool consistent = mot.cur_frame == next || (next > mot.end_frame && (mot.cur_frame == mot.end_frame || mot.cur_frame == next - mot.end_frame));
                if (consistent)
                {
                    target = t.evalCur + s_half;
                    if (target > mot.end_frame) target = mot.end_frame;
                    // 鍵と鍵の間が 1 論理フレームの進み (step) 以下の区間は、本体が中間の姿を一度も見せない「パラパラ絵」
                    // (城の炎 m01c1f1_fire の SEQ: 6 つの形を 0:s0 2:s0 4:s1 6:s1 … と 2 フレームずつ持つ)。そこで半歩先を評価すると
                    // 2 つの形を半々に混ぜた炎になるので、その区間では評価を進めない (本体と同じ姿のまま)。巻物のような長い区間はこれまでどおり
                    if (SegmentShorterThanStep(mot, target)) { target = -1; s_statHoldStep++; }
                }
            }
            if (s_dumpLeft > 0) Dump(mot, keyType, obj, t != null ? t.evalCur : -1, target);
            if (target >= 0) { mot.cur_frame = target; mot.start_delay = 0; s_statPreviewMot++; }
            else s_statHold++;
        }

        /// <summary>frame を含む鍵の区間 (key[i].frame ≤ frame &lt; key[i+1].frame) の長さが 1 論理フレームの進み以下か。鍵が無い・範囲外なら false。</summary>
        private static bool SegmentShorterThanStep(FldObject.FLD_MOTION mot, int frame)
        {
            FldObject.FLD_KEY[] keys = mot.key;
            if (keys == null) return false;
            for (int i = 0; i + 1 < keys.Length && i + 1 < mot.nkey; i++)
            {
                if (keys[i] == null || keys[i + 1] == null) return false;
                if (frame >= keys[i].frame && frame < keys[i + 1].frame) return keys[i + 1].frame - keys[i].frame <= s_step;
            }
            return false;
        }

        private static void Dump(FldObject.FLD_MOTION mot, object keyType, FldObject.FLD_MAP_OBJ obj, int evalCur, int target)
        {
            s_dumpLeft--;
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
                sb.AppendFormat("[I60fld] {0} {1} nkey={2} flag={3} cur={4} end={5} delay={6} rep={7}/{8} eval={9} target={10} keys=",
                    s_curPath, keyType, mot.nkey, mot.flag, mot.cur_frame, mot.end_frame, mot.start_delay, mot.repeat_num, mot._repeat_num, evalCur, target);
                FldObject.FLD_KEY[] keys = mot.key;
                if (keys != null)
                    for (int i = 0; i < keys.Length && i < 24; i++)
                    {
                        if (keys[i] == null) { sb.Append("null "); continue; }
                        sb.Append(keys[i].frame);
                        FldObject.FLD_SEQ_KEY sk = keys[i] as FldObject.FLD_SEQ_KEY;
                        FldObject.FLD_SEQ_KEY_SRT srt = keys[i] as FldObject.FLD_SEQ_KEY_SRT;
                        FldObject.FLD_PMV_KEY pk = keys[i] as FldObject.FLD_PMV_KEY;
                        if (sk != null) sb.Append(":s").Append(sk.shape_id);
                        else if (srt != null) sb.Append(":s").Append(srt.shape_id).Append(srt.pos.ToString("F1")).Append(srt.scl.ToString("F2"));
                        else if (pk != null) sb.Append(":p").Append(pk.pos.ToString("F1")).Append("r").Append(pk.rot.ToString("F0")).Append("s").Append(pk.scl.ToString("F2"));
                        sb.Append(' ');
                    }
                if (obj != null && obj.model != null)
                {
                    sb.AppendFormat(" obj={0} oflag={1:X} vtx={2} children={3}", obj.model.name, obj.flag, (obj.flag & FldObject.FLD_OBJ_FLAG_SEQ_VTX) != 0, obj.model.childCount);
                    MeshFilter mf = obj.model.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) sb.Append(" base=").Append(mf.sharedMesh.vertexCount);
                    for (int i = 0; i < obj.model.childCount && i < 6; i++)
                    {
                        MeshFilter cf = obj.model.GetChild(i).GetComponent<MeshFilter>();
                        sb.Append(" c").Append(i).Append('=').Append(cf != null && cf.sharedMesh != null ? cf.sharedMesh.vertexCount : -1);
                    }
                    Renderer r = obj.model.GetComponent<Renderer>();
                    if (r != null && r.sharedMaterial != null)
                        sb.Append(" sh=").Append(r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "null").Append(" weight=").Append(r.sharedMaterial.HasProperty("_ShapeWeight"));
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("[I60fld] dump: " + e.Message); }
        }

        /// <summary>fld_motion_update の Postfix。プレビューで進めた分と元処理の前進を全て戻す。</summary>
        internal static void OnMotionEnd(FldObject.FLD_MOTION mot, MotState st)
        {
            if (!st.saved || mot == null) return;
            mot.cur_frame = st.cur; mot.start_delay = st.delay; mot.repeat_num = st.repeat;
        }

        private static void Prune()
        {
            int now = Time.frameCount;
            foreach (KeyValuePair<FldObject, int> kv in s_animated)
                if (now - kv.Value > 600) s_prune.Add(kv.Key);
            for (int i = 0; i < s_prune.Count; i++) s_animated.Remove(s_prune[i]);
            s_prune.Clear();
            foreach (KeyValuePair<FldObject.FLD_MOTION, MotTrack> kv in s_mots)
                if (now - kv.Value.frame > 600) s_pruneMot.Add(kv.Key);
            for (int i = 0; i < s_pruneMot.Count; i++) s_mots.Remove(s_pruneMot[i]);
            s_pruneMot.Clear();
        }

        public static string StatText()
        {
            string s = string.Format(" | fldAnim={0} preview={1} (mot={2} hold={3} flip={4}) tracked={5}",
                Enabled != null && Enabled.Value ? "on" : "off", s_statPreview, s_statPreviewMot, s_statHold, s_statHoldStep, s_animated.Count);
            s_statPreview = s_statPreviewMot = s_statHold = s_statHoldStep = 0;
            return s;
        }
    }

    /// <summary>FldObject.fld_motion_update (private) の Prefix/Postfix。プレビュー中だけ cur_frame を半歩進めて評価し、状態を戻す。</summary>
    [HarmonyPatch(typeof(FldObject), "fld_motion_update")]
    internal static class Interp60FldMotionPatch
    {
        private static void Prefix(FldObject.FLD_MOTION mot, object[] __args, out Interp60FldAnim.MotState __state)
        {
            Interp60FldAnim.OnMotion(mot, __args[1], __args[2] as FldObject.FLD_MAP_OBJ, out __state);
        }

        private static void Postfix(FldObject.FLD_MOTION mot, Interp60FldAnim.MotState __state)
        {
            Interp60FldAnim.OnMotionEnd(mot, __state);
        }
    }

    /// <summary>
    /// FldObject.update_plp (パレットアニメ) の Prefix。プレビュー中は止める (元処理を止める理由: _cur_frame / _cur_key を
    /// 進めてしまい本体の進行が速くなるため。テクスチャは前の論理フレームで設定済みで姿は変わらない)。
    /// </summary>
    [HarmonyPatch(typeof(FldObject), "update_plp")]
    internal static class Interp60FldPlpPatch
    {
        private static bool Prefix()
        {
            return !Interp60FldAnim.Previewing;
        }
    }
}
