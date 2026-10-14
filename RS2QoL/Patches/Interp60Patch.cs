using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace RS2QoL.Patches
{
    internal enum Interp60Kind
    {
        Off,        // 30fps (本体どおり)
        Duplicate,  // Unity を 60fps で回し、論理フレームは 2 回に 1 回。間のフレームは前の描画をそのまま複製 (ちらつき確認用。見た目は 30fps と同じ)
        SmoothMap,  // + フィールドの地形 (BgMap) のスクロールを半フレーム補間
        SmoothAll,  // + フィールドのキャラ・物 (スプライト) の位置も半フレーム補間
    }

    /// <summary>
    /// 60fps 表示 (実験的)。
    ///
    /// 本体は 1 Unity フレーム = 1 論理フレーム (33ms) で、歩行 8 フレーム/マスなどカウンタが全てフレーム数なので、
    /// Unity を 60fps で回すとゲームが 2 倍速になる (倍速機能そのもの)。進行速度を変えずに 60fps にするには、
    /// 「Unity は 60fps、論理フレーム (Main.Disp.run) は 2 Unity フレームに 1 回」にして、論理フレームを回さない
    /// フレームにも何かを描かなければならない。本体の描画はほぼ全て即時描画 (Graphics.DrawMesh: 地形・スプライト・
    /// 文字・塗り・SpriteStudio) で 1 フレームしか有効でないため、何もしないと 1 フレームおきに真っ黒になる。
    ///
    /// そこで全ての Graphics.DrawMesh が最終的に通る UnityEngine.Graphics.DrawMeshImpl に Prefix を入れ、論理フレームを
    /// 回した Unity フレームの描画呼び出し (mesh/matrix/material/...) を全部記録し、次の (論理フレームを回さない)
    /// Unity フレームで GameMain.Update を丸ごと止めて (Prefix が false) 記録を再発行する。GameMain.Update を止めるのは
    /// 末尾の GS.SwapBuffer が「2 フレーム前の一時 Mesh/Material を Destroy」するので、止めないと再発行する Mesh が
    /// 描画前に消えるため。入力は論理フレームの中 (Util.GetKeypadState) でしか読まれないので、入力の意味は変わらない。
    ///
    /// 補間: 「論理フレーム N を描く Unity フレーム」で、スクロールに追従する描画を半歩 (N-1 と N の中点) 戻し、
    /// 次の複製フレームで N そのままを出す。表示が半論理フレーム (16ms) 遅れるが、先読み (外挿) のように止まった時に
    /// 行き過ぎて戻る揺れが出ない。地形は BgMap.Draw の中の DrawMesh の行列の平行移動を、同じ BgMap の前回の
    /// 1 発目との差で求めてずらす。スプライト (Util.Sprite) は行列ではなくマテリアルの _Pos で位置を持つので、
    /// Core.draw の中の _Pos を持つマテリアルごとに前回位置を覚えて中点にする (複製フレームでは元の値に戻して発行)。
    /// 1 歩 (MaxStep) より大きく動いた時は瞬間移動として補間しない。戦闘・メニューは複製のみ (30fps のまま)。
    ///
    /// 既知の制限: Input.GetKeyDown/GetKeyUp は Unity の 1 フレームだけ true なので、論理フレームを回さないフレームに
    /// 当たると拾えない (本体の Esc の終了ダイアログは 2 回に 1 回しか開かない)。パッドは押下状態を見るので影響なし。
    /// MOD の F1/Esc は複製フレームで見た押下を次の論理フレームに持ち越す。
    /// Time.deltaTime を論理フレームの中で積む所 (UiField の仮想パッドのタイマー、BgMap のマスクのフェード) は半分の速さになる。
    /// </summary>
    internal static class Interp60
    {
        public static ConfigEntry<Interp60Kind> Mode;
        public static ConfigEntry<int> MaxStep;

        private static Harmony s_harmony;
        private static bool s_patched, s_patchFailed;

        // フレーム制御
        private static int s_acc = 100;        // 半論理フレーム単位の残り (% 単位、200 で 1 論理フレーム。100 から始めて最初の Unity フレームで論理フレームを回す。戦闘の x1.5 などは余りが積もったフレームで回す)
        private static bool s_skipping;        // この Unity フレームは論理フレームを回さず複製する
        private static bool s_recording;       // この Unity フレームの DrawMesh を記録中
        private static bool s_haveFrame;       // 直前の論理フレームの描画を丸ごと記録してある (複製してよい)
        private static bool s_replaying;
        private static bool s_interpolate;     // この論理フレームの描画を半歩戻す
        private static int s_extraRuns;
        private static int s_logicFrame;       // 論理フレームを回した Unity フレームの通番

        /// <summary>今 60fps 表示が効いているか (GameSpeed が追加 run 数の決め方を切り替える)。</summary>
        public static bool Active { get { return Mode != null && Mode.Value != Interp60Kind.Off && s_patched; } }
        /// <summary>この Unity フレームで追加で回す論理フレーム数 (Active の時だけ意味がある)。</summary>
        public static int ExtraRuns { get { return s_extraRuns; } }
        /// <summary>DisplaySync の目標フレームレートに掛ける倍率 (60fps 表示中は 2)。</summary>
        public static int DisplayMultiplier() { return Mode != null && Mode.Value != Interp60Kind.Off && !s_patchFailed ? 2 : 1; }

        // 記録した描画呼び出し
        private struct Call
        {
            public Mesh mesh; public Matrix4x4 matrix; public Material material; public int layer; public Camera camera; public int submesh;
            public MaterialPropertyBlock props; public ShadowCastingMode cast; public bool receive; public Transform anchor; public bool probes;
            public bool hasPos; public Vector4 pos;   // _Pos を補間した時の元の値 (複製フレームで戻す)
        }
        private static readonly List<Call> s_calls = new List<Call>(1024);

        // BgMap.Draw のスコープ (入れ子: m_scroll_bg.Draw が中で呼ばれる)
        private class MapTrack { public int frame = -10; public float tx, ty; }
        private struct Scope { public MapTrack track; public bool known; public float sx, sy; }
        private static readonly Dictionary<object, MapTrack> s_maps = new Dictionary<object, MapTrack>();
        private static readonly Scope[] s_scopes = new Scope[8];
        private static int s_mapDepth;
        private static int s_coreDepth;

        // スプライト (_Pos) の前回位置
        private class PosTrack { public int frame = -10; public Vector4 pos; public bool hasPos; }
        private static readonly Dictionary<Material, PosTrack> s_pos = new Dictionary<Material, PosTrack>();
        private static readonly List<Material> s_prune = new List<Material>();

        // フィールドのキャラ (Sys.spr[n].m_sprite / m_sprite2 / m_sprite3) の前回位置。Sys.Sprite.set が毎論理フレーム
        // Util.Sprite.Init → Term() でマテリアルを破棄して作り直すので、Material をキーにすると履歴が毎フレーム切れる。
        // Util.Sprite のインスタンスは永続なのでそちらをキーにし、同じインスタンスを 1 フレームに複数回描く時 (DrawClone、
        // OrderingSprite のマスク) は描いた順番 (ordinal) で区別する
        private class SpriteTrack { public int frame = -10; public int n; public readonly List<PosTrack> parts = new List<PosTrack>(4); }
        private static readonly Dictionary<Util.Sprite, SpriteTrack> s_sprites = new Dictionary<Util.Sprite, SpriteTrack>();
        private static Util.Sprite s_curSprite;   // Util.Sprite.Draw() の中 (Interp60SpriteScopePatch)

        // Util.Sprite → それを持つ Sys.Sprite (Sys.spr[n] の m_sprite / m_sprite2 / m_sprite3)。キャラの 3 枚は flip (向きごとの
        // 切り出し方) が変わると部品単位の位置が変わるので、部品ごとに補間すると向き / コマが変わった瞬間に部品が裂ける。
        // キャラの基準座標 (Sys.Sprite.x / y) の移動量で 3 枚を同じだけずらす
        private class CharTrack { public int frame = -10; public float ax, ay; public float sx, sy; }
        private static readonly Dictionary<Util.Sprite, Sys.Sprite> s_owner = new Dictionary<Util.Sprite, Sys.Sprite>();
        private static readonly Dictionary<Sys.Sprite, CharTrack> s_chars = new Dictionary<Sys.Sprite, CharTrack>();
        private static Sys.Sprite[] s_ownerSource;   // s_owner を作った時の Sys.spr (差し替わったら作り直す)

        // フィールドの 3D オブジェクト (FldObject: 建物など)。通常描画では Graphics.DrawMesh を通らず MeshRenderer をカメラが描き、
        // 位置は BgMap.SetPosEx → FldObject.SetPositon が論理フレームごとに root の localPosition を動かすだけなので、
        // 地面 (DrawMesh、補間あり) に対して 30fps 刻みでずれる。補間する論理フレームでは root を中点に置き、複製フレームで戻す
        private class FldTrack { public int frame = -10; public Vector3 pos; }
        private struct FldRestore { public Transform tr; public Vector3 pos; }
        private static readonly Dictionary<FldObject, FldTrack> s_flds = new Dictionary<FldObject, FldTrack>();
        private static readonly List<FldRestore> s_fldRestore = new List<FldRestore>(8);
        private static int s_statFldInterp;

        // 診断ダンプ ([MARK] の直後の論理フレーム数回分、Core.draw の中の全描画を 1 行ずつ出す)
        private static int s_dumpFrames, s_dumpIdx;
        private static bool s_dumping;
        internal static void RequestDump() { s_dumpFrames = 3; }
        private static int s_statSprInterp;
        private static int s_statZoomInterp;   // 遭遇時の拡大で位置と大きさを補間した描画数

        // 複製フレームで見たキー押下 (次の論理フレームに持ち越す)
        private static bool s_latchF1, s_latchEsc;
        private static int s_latchEscUpFrame = -10;   // Esc のキーアップを見た Unity フレーム (本体の終了ダイアログ用)

        // 診断
        private static int s_statLogic, s_statSkip, s_statCalls, s_statCallsMax, s_statReplayed, s_statPosInterp, s_statMapShift, s_statDropped, s_statSuppressed;
        private static float s_statLastDx, s_statLastDy;
        private static float s_statTime;

        public static void Bind(ConfigFile config)
        {
            Mode = config.Bind("Display", "Experimental60", Interp60Kind.SmoothAll,
                "60fps 表示 (既定 SmoothAll)。Off=30fps (本体どおり) / Duplicate=Unity を 60fps で回し間のフレームは前の絵を複製 (見た目は 30fps のまま。ちらつき確認用) / SmoothMap=フィールドの地形スクロールを補間してなめらかに / SmoothAll=キャラ・物の位置も補間。ゲームの進行速度は変わらない (倍速 x2 以上では補間なしの本物の 60 コマ描画になる)。Sync が Unchanged でも Unity は 60fps (VSync 扱い) になる。PC が 60fps を出せないとゲームが遅くなるので、その時は Off に");
            MaxStep = config.Bind("Display", "Experimental60MaxStep", 24,
                new ConfigDescription("補間する 1 論理フレームの移動量の上限 (px、960x540 基準)。これより大きく動いた時は瞬間移動とみなして補間しない", new AcceptableValueRange<int>(1, 200)));
            Mode.SettingChanged += (s, e) => { EnsurePatched(); DisplaySync.Apply(); };
        }

        public static string ModeLabel(Interp60Kind k)
        {
            switch (k)
            {
                case Interp60Kind.Duplicate: return "60fps 複製のみ";
                case Interp60Kind.SmoothMap: return "60fps 地形を補間";
                case Interp60Kind.SmoothAll: return "60fps 地形+キャラを補間";
                default: return "しない (30fps)";
            }
        }

        /// <summary>Plugin.Awake の PatchAll の後。Off 以外なら UnityEngine 側のパッチを入れる。</summary>
        public static void Init(Harmony harmony)
        {
            s_harmony = harmony;
            EnsurePatched();
        }

        // Graphics.DrawMeshImpl (UnityEngine.dll の private static) への Prefix は、Off の人に UnityEngine 内部の detour を
        // 触らせないよう、初めて Off 以外になった時にだけ入れる (一度入れたら外さない。Prefix は Off なら即 return)
        private static void EnsurePatched()
        {
            if (s_patched || s_patchFailed || s_harmony == null || Mode == null || Mode.Value == Interp60Kind.Off) return;
            try
            {
                MethodInfo impl = AccessTools.Method(typeof(Graphics), "DrawMeshImpl");
                if (impl == null) throw new MissingMethodException("UnityEngine.Graphics.DrawMeshImpl not found");
                s_harmony.Patch(impl, prefix: new HarmonyMethod(typeof(Interp60), nameof(DrawMeshPrefix)));
                s_patched = true;
                Plugin.Log.LogInfo("[Interp60] Graphics.DrawMeshImpl patched (" + impl + ")");
            }
            catch (Exception e)
            {
                s_patchFailed = true;
                Plugin.Log.LogError("[Interp60] cannot patch Graphics.DrawMeshImpl, feature disabled: " + e);
            }
        }

        /// <summary>
        /// GameMain.Update の Prefix (DisplaySync のリミッタの後)。true なら GameMain.Update (= 論理フレーム) を回す、
        /// false なら止めて前フレームの描画を複製する。
        /// </summary>
        internal static bool OnFrameStart()
        {
            s_skipping = false; s_recording = false; s_interpolate = false; s_extraRuns = 0;
            s_curSprite = null; s_zoomDepth = 0;
            s_coreDepth = 0; s_mapDepth = 0; s_cursorDepth = 0;   // 前フレームの描画が途中で例外を出して Postfix が走らなかった時の保険
            Interp60Battle.OnFrameStart();
            if (!Active)
            {
                s_acc = 100;
                s_haveFrame = false;
                return true;
            }
            try
            {
                int mult = GameSpeed.EffectivePercent();   // % (100 = 等倍)。半論理フレーム = 200
                s_acc += mult;
                int runs = s_acc / 200;
                s_acc -= runs * 200;
                if (runs == 0)
                {
                    if (s_haveFrame)
                    {
                        s_skipping = true;
                        s_statSkip++;
                        LatchKeys();
                        PadMap.LatchSkippedFrame(); // 複製フレームに収まる短いタップを次の論理フレームへ持ち越す
                        Replay();
                        return false;
                    }
                    // 直前の論理フレームの描画を記録できていない (有効にした直後) → 真っ黒を出さず論理フレームを回す
                    runs = 1; s_acc = 0;
                }
                RestoreFld();
                s_extraRuns = runs - 1;
                // 記録は毎論理フレーム行う (倍速を切った直後など、次が複製フレームになるかはこの時点では分からない)
                bool nextIsSkip = s_acc + mult < 200;
                s_recording = true;
                s_interpolate = nextIsSkip && Mode.Value != Interp60Kind.Duplicate;
                s_logicFrame++;
                s_calls.Clear();
                s_statLogic++;
                s_dumping = s_dumpFrames > 0;
                if (s_dumping)
                {
                    s_dumpFrames--; s_dumpIdx = 0;
                    Plugin.Log.LogInfo("[I60dump] ==== logic frame " + s_logicFrame + " interp=" + s_interpolate + " mult=" + mult);
                }
                if ((s_logicFrame & 255) == 0) Prune();
                Stat();
                return true;
            }
            catch (Exception e)
            {
                Fail("OnFrameStart", e);
                return true;
            }
        }

        /// <summary>GameMain.Update の Postfix (止めた時も呼ばれる)。</summary>
        internal static void OnFrameEnd()
        {
            if (s_recording)
            {
                s_haveFrame = true;
                if (s_calls.Count > s_statCallsMax) s_statCallsMax = s_calls.Count;
            }
            s_recording = false;
        }

        /// <summary>Main.Disp.run の Prefix。倍速で 1 Unity フレームに複数回回す時は最後の run の描画だけ残す (二重描きの残像を防ぐ)。</summary>
        internal static void OnRunStart()
        {
            if (s_recording) s_calls.Clear();
        }

        private static void Fail(string where, Exception e)
        {
            Plugin.Log.LogError("[Interp60] " + where + ": " + e + "\n-> 60fps 表示を Off にします");
            s_recording = false; s_replaying = false; s_skipping = false; s_interpolate = false; s_extraRuns = 0; s_haveFrame = false;
            s_calls.Clear();
            if (Mode != null) Mode.Value = Interp60Kind.Off;
        }

        // 複製フレーム: 記録した描画をそのまま発行する。レンダーテクスチャ向け (camera != null、Effect の手動 Render) は
        // 描き先が残っているので再発行しない。Destroy 済みの Mesh/Material (Unity の == null) は飛ばす
        private static void Replay()
        {
            s_replaying = true;
            RestoreFld();
            try
            {
                int n = s_calls.Count;
                for (int i = 0; i < n; i++)
                {
                    Call c = s_calls[i];
                    if (c.camera != null) continue;
                    if (c.mesh == null || c.material == null) { s_statDropped++; continue; }
                    if (c.hasPos) c.material.SetVector("_Pos", c.pos);
                    Graphics.DrawMesh(c.mesh, c.matrix, c.material, c.layer, null, c.submesh, c.props, c.cast, c.receive, c.anchor, c.probes);
                    s_statReplayed++;
                }
            }
            catch (Exception e) { Fail("Replay", e); }
            finally { s_replaying = false; }
        }

        /// <summary>
        /// UnityEngine.Graphics.DrawMeshImpl の Prefix (全ての Graphics.DrawMesh がここを通る)。
        /// 論理フレームを回す Unity フレームでは呼び出しを記録し、補間する時は行列 / _Pos を半歩戻す。
        /// </summary>
        private static bool DrawMeshPrefix(Mesh mesh, ref Matrix4x4 matrix, Material material, int layer, Camera camera, int submeshIndex,
            MaterialPropertyBlock properties, ShadowCastingMode castShadows, bool receiveShadows, Transform probeAnchor, bool useLightProbes)
        {
            // 倍速で 1 Unity フレームに論理フレームを複数回回す時、最後以外の run の描画は捨てる (重ね描きの残像を防ぐ。GameSpeedPatch)
            if (GameSpeed.SuppressDraw) { s_statSuppressed++; return false; }
            if (s_replaying || !s_recording) return true;
            try
            {
                Call c;
                float om03 = matrix.m03, om13 = matrix.m13;
                c.mesh = mesh; c.matrix = matrix; c.material = material; c.layer = layer; c.camera = camera; c.submesh = submeshIndex;
                c.props = properties; c.cast = castShadows; c.receive = receiveShadows; c.anchor = probeAnchor; c.probes = useLightProbes;
                c.hasPos = false; c.pos = Vector4.zero;
                if (s_interpolate && camera == null && material != null)
                {
                    float max = MaxStep.Value;
                    if (s_cursorDepth > 0) max = 128f; // 自由カーソルは速さ設定で 1 歩が MaxStep を超えうる
                    // 遭遇時の拡大 (Core.sync の exdemo。敵シンボルの Sys.Sprite を毎論理フレーム 1.3 倍にして描く。Interp60ZoomPatch):
                    // Util.Sprite.Draw は位置も大きさも _Pos (x, y, w, h) に入れるので、4 成分とも前の論理フレームとの中点にする。
                    // 大きさの変化は MaxStep の対象外 (1 コマで何十 px も変わるのが正常)
                    if (s_zoomDepth > 0 && s_curSprite != null)
                    {
                        PosTrack t = SpritePart(s_curSprite, material);
                        if (t.hasPos)
                        {
                            Vector4 cur = material.GetVector("_Pos");
                            c.hasPos = true; c.pos = cur;
                            if (t.frame == s_logicFrame - 1 && cur != t.pos)
                            {
                                material.SetVector("_Pos", (cur + t.pos) * 0.5f);
                                s_statZoomInterp++;
                            }
                            t.frame = s_logicFrame; t.pos = cur;
                        }
                    }
                    // 戦闘: drawPlayer / BattleUIDamage.Draw のスコープ内は論理キーで前回位置を引く (Interp60Battle)
                    else if (Interp60Battle.Key >= 0)
                    {
                        Interp60Battle.Interpolate(mesh, ref matrix, material, s_logicFrame, max, ref c.hasPos, ref c.pos);
                    }
                    // スプライト: Core.draw の中で _Pos を持つマテリアル (Util.Sprite)。前の論理フレームにも描かれていれば中点へ
                    else if ((Mode.Value == Interp60Kind.SmoothAll && s_coreDepth > 0) || s_cursorDepth > 0)
                    {
                        PosTrack t = null;
                        bool bySprite = s_curSprite != null;
                        Sys.Sprite owner = bySprite ? FindOwner(s_curSprite) : null;
                        if (owner != null)
                        {
                            // キャラ: 基準座標の移動量を 1 フレームに 1 回求め、全ての部品 (DrawClone 含む) を同じだけ半歩戻す
                            CharTrack ct;
                            if (!s_chars.TryGetValue(owner, out ct)) { ct = new CharTrack(); s_chars[owner] = ct; }
                            float ax = Sys.get_screen_xf2(owner.x), ay = Sys.get_screen_y(owner.y);
                            if (ct.frame != s_logicFrame)
                            {
                                ct.sx = ct.sy = 0f;
                                if (ct.frame == s_logicFrame - 1)
                                {
                                    float dx = ax - ct.ax, dy = ay - ct.ay;
                                    if ((dx != 0f || dy != 0f) && Mathf.Abs(dx) <= max && Mathf.Abs(dy) <= max) { ct.sx = -dx * 0.5f; ct.sy = dy * 0.5f; }   // _Pos.y は画面 Y と逆向き (Util.Sprite.Draw: -(y) + SCREEN_H/2)
                                }
                                ct.frame = s_logicFrame; ct.ax = ax; ct.ay = ay;
                            }
                            // 部品ごとの _Pos も覚える (遭遇時の拡大の 1 コマ目で、通常描画との中点を取るため。補間前の値)
                            PosTrack pt = SpritePart(s_curSprite, material);
                            if (pt.hasPos)
                            {
                                Vector4 cur = material.GetVector("_Pos");
                                pt.frame = s_logicFrame; pt.pos = cur;
                                if (ct.sx != 0f || ct.sy != 0f)
                                {
                                    c.hasPos = true; c.pos = cur;
                                    material.SetVector("_Pos", new Vector4(cur.x + ct.sx, cur.y + ct.sy, cur.z, cur.w));
                                    s_statSprInterp++;
                                }
                            }
                        }
                        else if (bySprite)
                        {
                            // Util.Sprite.Draw() の中: インスタンス + 描画順でキーにする (マテリアルは毎論理フレーム作り直される)
                            t = SpritePart(s_curSprite, material);
                        }
                        else if (!s_pos.TryGetValue(material, out t))
                        {
                            t = new PosTrack { hasPos = material.HasProperty("_Pos") };
                            s_pos[material] = t;
                        }
                        if (t != null && t.hasPos)
                        {
                            Vector4 cur = material.GetVector("_Pos");
                            c.hasPos = true; c.pos = cur;
                            if (t.frame == s_logicFrame - 1)
                            {
                                float dx = cur.x - t.pos.x, dy = cur.y - t.pos.y;
                                if ((dx != 0f || dy != 0f) && Mathf.Abs(dx) <= max && Mathf.Abs(dy) <= max)
                                {
                                    material.SetVector("_Pos", new Vector4(cur.x - dx * 0.5f, cur.y - dy * 0.5f, cur.z, cur.w));
                                    if (bySprite) s_statSprInterp++; else s_statPosInterp++;
                                }
                            }
                            t.frame = s_logicFrame; t.pos = cur;
                        }
                    }
                    // 地形など: BgMap.Draw の中の、単位行列でない描画 (単位行列は GS.FillRectZ の全画面デプスクリア)。
                    // スコープ内の 1 発目の平行移動を前の論理フレームの 1 発目と比べてスクロール量とし、スコープ内の全てを半歩戻す
                    if (!c.hasPos && s_mapDepth > 0 && matrix != Matrix4x4.identity)
                    {
                        int d = s_mapDepth - 1;
                        if (!s_scopes[d].known)
                        {
                            s_scopes[d].known = true;
                            MapTrack t = s_scopes[d].track;
                            if (t.frame == s_logicFrame - 1)
                            {
                                float dx = matrix.m03 - t.tx, dy = matrix.m13 - t.ty;
                                if ((dx != 0f || dy != 0f) && Mathf.Abs(dx) <= max && Mathf.Abs(dy) <= max)
                                {
                                    s_scopes[d].sx = -dx * 0.5f; s_scopes[d].sy = -dy * 0.5f;
                                    if (d == 0) { s_statLastDx = dx; s_statLastDy = dy; }
                                }
                            }
                            t.frame = s_logicFrame; t.tx = matrix.m03; t.ty = matrix.m13;
                        }
                        if (s_scopes[d].sx != 0f || s_scopes[d].sy != 0f)
                        {
                            matrix.m03 += s_scopes[d].sx; matrix.m13 += s_scopes[d].sy;
                            s_statMapShift++;
                        }
                    }
                }
                if (s_dumping && s_coreDepth > 0 && s_dumpIdx < 120) DumpCall(mesh, material, camera, om03, om13, ref matrix, ref c);
                s_calls.Add(c);
                s_statCalls++;
                if (Interp60Battle.InEffect) Interp60Battle.CountEffectCall();
            }
            catch (Exception e) { Fail("DrawMeshPrefix", e); }
            return true;
        }

        internal static void EnterMap(object bgMap)
        {
            if (!s_recording) return;
            if (s_mapDepth < s_scopes.Length)
            {
                MapTrack t;
                if (!s_maps.TryGetValue(bgMap, out t)) { t = new MapTrack(); s_maps[bgMap] = t; }
                s_scopes[s_mapDepth] = new Scope { track = t };
            }
            s_mapDepth++;
        }

        internal static void LeaveMap()
        {
            if (s_mapDepth > 0) s_mapDepth--;
        }

        // Core.draw の外で描かれるが補間したいスプライト (ワールドマップの自由カーソル。WorldMapCursorDrawPatch)。SmoothMap でも補間する
        private static int s_cursorDepth;
        internal static void EnterCursor() { s_cursorDepth++; }
        internal static void LeaveCursor() { if (s_cursorDepth > 0) s_cursorDepth--; }
        internal static void EnterCore() { s_coreDepth++; }
        internal static void LeaveCore() { if (s_coreDepth > 0) s_coreDepth--; }
        internal static void EnterSprite(Util.Sprite s) { s_curSprite = s; }
        internal static void LeaveSprite() { s_curSprite = null; }
        // 遭遇時の拡大 (Core.sync の exdemo の中の Sys.ordersprite.draw(32769)。Interp60ZoomPatch)
        private static int s_zoomDepth;
        internal static void EnterZoom() { s_zoomDepth++; }
        internal static void LeaveZoom() { if (s_zoomDepth > 0) s_zoomDepth--; }

        /// <summary>
        /// Util.Sprite のインスタンス + この論理フレーム内の描画順 (DrawClone 含む) で引く部品ごとの前回 _Pos。
        /// マテリアルは毎論理フレーム作り直されるのでインスタンスで引く。
        /// </summary>
        private static PosTrack SpritePart(Util.Sprite s, Material material)
        {
            SpriteTrack st;
            if (!s_sprites.TryGetValue(s, out st)) { st = new SpriteTrack(); s_sprites[s] = st; }
            if (st.frame != s_logicFrame) { st.frame = s_logicFrame; st.n = 0; }
            if (st.n >= st.parts.Count) st.parts.Add(new PosTrack { hasPos = material.HasProperty("_Pos") });
            return st.parts[st.n++];
        }

        /// <summary>Util.Sprite を持つ Sys.spr[n] を返す (無ければ null)。Sys.spr が差し替わった時だけ表を作り直す。</summary>
        private static Sys.Sprite FindOwner(Util.Sprite s)
        {
            Sys.Sprite[] arr = Sys.spr;
            if (!ReferenceEquals(arr, s_ownerSource))
            {
                s_owner.Clear(); s_chars.Clear();
                s_ownerSource = arr;
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                    {
                        Sys.Sprite sp = arr[i];
                        if (sp == null) continue;
                        if (sp.m_sprite != null) s_owner[sp.m_sprite] = sp;
                        if (sp.m_sprite2 != null) s_owner[sp.m_sprite2] = sp;
                        if (sp.m_sprite3 != null) s_owner[sp.m_sprite3] = sp;
                    }
            }
            Sys.Sprite o;
            return s_owner.TryGetValue(s, out o) ? o : null;
        }

        /// <summary>FldObject.SetPositon の Postfix。補間する論理フレームなら root を前の位置との中点に置く (複製フレームで戻す)。</summary>
        internal static void OnFldPosition(FldObject fld, float x, float y, float z)
        {
            if (!s_recording || fld == null) return;
            try
            {
                FldTrack t;
                if (!s_flds.TryGetValue(fld, out t)) { t = new FldTrack(); s_flds[fld] = t; }
                Vector3 pos = new Vector3(x, y, z);
                if (s_interpolate && t.frame == s_logicFrame - 1 && fld.m_root_obj != null)
                {
                    float dx = x - t.pos.x, dy = y - t.pos.y, max = MaxStep.Value;
                    if ((dx != 0f || dy != 0f) && z == t.pos.z && Mathf.Abs(dx) <= max && Mathf.Abs(dy) <= max)
                    {
                        Transform tr = fld.m_root_obj.transform;
                        tr.localPosition = new Vector3(x - dx * 0.5f, y - dy * 0.5f, z);
                        s_fldRestore.Add(new FldRestore { tr = tr, pos = pos });
                        s_statFldInterp++;
                    }
                }
                if (s_dumping) Plugin.Log.LogInfo(string.Format("[I60dump] fld {0} pos=({1:F1},{2:F1},{3:F1}) prev=({4:F1},{5:F1}) root={6} fix={7}", fld.m_data_path, x, y, z, t.pos.x, t.pos.y,
                    fld.m_root_obj != null ? fld.m_root_obj.transform.localPosition.ToString() : "null", fld.m_is_fix_pos));
                t.frame = s_logicFrame; t.pos = pos;
            }
            catch (Exception e) { Fail("OnFldPosition", e); }
        }

        private static void DumpCall(Mesh mesh, Material material, Camera camera, float om03, float om13, ref Matrix4x4 matrix, ref Call c)
        {
            try
            {
                string scope = (s_mapDepth > 0 ? "map" + s_mapDepth : "core");
                if (s_curSprite != null)
                {
                    Sys.Sprite o = FindOwner(s_curSprite);
                    int idx = -1;
                    if (o != null && Sys.spr != null) for (int i = 0; i < Sys.spr.Length; i++) if (ReferenceEquals(Sys.spr[i], o)) { idx = i; break; }
                    scope += o != null ? " chr" + idx + "(" + o.x + "," + o.y + ")" : " spr";
                }
                string pos = "-";
                if (material != null && material.HasProperty("_Pos"))
                {
                    Vector4 p = material.GetVector("_Pos");
                    pos = c.hasPos ? string.Format("({0:F1},{1:F1})->({2:F1},{3:F1})", c.pos.x, c.pos.y, p.x, p.y) : string.Format("({0:F1},{1:F1})", p.x, p.y);
                }
                string tex = "-";
                if (material != null && material.HasProperty("_MainTex") && material.mainTexture != null) tex = material.mainTexture.name;
                Plugin.Log.LogInfo(string.Format("[I60dump] {0} {1} mesh={2} sh={3} tex={4} cam={5} m=({6:F1},{7:F1}) shift=({8:F1},{9:F1}) pos={10}",
                    s_dumpIdx++, scope, mesh != null ? mesh.name : "null", material != null && material.shader != null ? material.shader.name : "null", tex,
                    camera != null, om03, om13, matrix.m03 - om03, matrix.m13 - om13, pos));
            }
            catch (Exception e) { Plugin.Log.LogWarning("[I60dump] " + e.Message); }
        }

        // 中点に置いた FldObject の root を本来の位置に戻す (複製フレームの頭、または次の論理フレームの頭)
        private static void RestoreFld()
        {
            for (int i = 0; i < s_fldRestore.Count; i++)
            {
                Transform tr = s_fldRestore[i].tr;
                if (tr != null) tr.localPosition = s_fldRestore[i].pos;
            }
            s_fldRestore.Clear();
        }

        internal static bool Skipping { get { return s_skipping; } }

        // 複製フレームでのキー押下を次の論理フレームへ持ち越す (GetKeyDown はその Unity フレームしか true でない)
        private static void LatchKeys()
        {
            MouseLatch.OnSkippedFrame(); // 複製フレームに落ちたマウスのクリックを次の論理フレームへ
            if (Input.GetKeyDown(KeyCode.F1)) s_latchF1 = true;
            if (Input.GetKeyDown(KeyCode.Escape)) s_latchEsc = true;
            if (Input.GetKeyUp(KeyCode.Escape)) s_latchEscUpFrame = Time.frameCount;
        }

        /// <summary>複製フレームで Esc のキーアップを見ていれば 1 回だけ true (直後の論理フレームでだけ有効)。</summary>
        public static bool TakeEscUp()
        {
            if (Time.frameCount - s_latchEscUpFrame > 1) return false;
            s_latchEscUpFrame = -10;
            return true;
        }

        /// <summary>複製フレームで押されたキーがあれば 1 回だけ true (ConfigMenu.Tick から)。</summary>
        public static bool TakeKeyDown(KeyCode key)
        {
            if (key == KeyCode.F1 && s_latchF1) { s_latchF1 = false; return true; }
            if (key == KeyCode.Escape && s_latchEsc) { s_latchEsc = false; return true; }
            return false;
        }

        // 使われなくなったマテリアル / BgMap の追跡を捨てる
        private static void Prune()
        {
            s_prune.Clear();
            foreach (KeyValuePair<Material, PosTrack> kv in s_pos)
            {
                if (kv.Key == null || kv.Value.frame < s_logicFrame - 300) s_prune.Add(kv.Key);
            }
            for (int i = 0; i < s_prune.Count; i++) s_pos.Remove(s_prune[i]);
            if (s_flds.Count > 32)
            {
                var oldFld = new List<FldObject>();
                foreach (KeyValuePair<FldObject, FldTrack> kv in s_flds) if (kv.Value.frame < s_logicFrame - 300) oldFld.Add(kv.Key);
                for (int i = 0; i < oldFld.Count; i++) s_flds.Remove(oldFld[i]);
            }
            if (s_sprites.Count > 256)
            {
                var oldSpr = new List<Util.Sprite>();
                foreach (KeyValuePair<Util.Sprite, SpriteTrack> kv in s_sprites) if (kv.Value.frame < s_logicFrame - 300) oldSpr.Add(kv.Key);
                for (int i = 0; i < oldSpr.Count; i++) s_sprites.Remove(oldSpr[i]);
            }
            if (s_maps.Count > 64)
            {
                var old = new List<object>();
                foreach (KeyValuePair<object, MapTrack> kv in s_maps) if (kv.Value.frame < s_logicFrame - 300) old.Add(kv.Key);
                for (int i = 0; i < old.Count; i++) s_maps.Remove(old[i]);
            }
        }

        /// <summary>[Mem] の診断行に足す追跡表の大きさ。</summary>
        internal static string TrackText() { return "interp mtl=" + s_pos.Count + " spr=" + s_sprites.Count + " chars=" + s_chars.Count + " maps=" + s_maps.Count + " calls=" + s_calls.Count; }

        private static void Stat()
        {
            if (Plugin.DiagEnabled == null || !Plugin.DiagEnabled.Value) return;
            float now = Time.realtimeSinceStartup;
            if (s_statTime == 0f) s_statTime = now;
            if (now - s_statTime < 5f) return;
            Plugin.Log.LogInfo(string.Format(
                "[Interp60] {0} logic={1} skip={2} calls/logic={3:F0} (max {4}) replayed={5} dropped={6} suppressed={18} posInterp={7} sprInterp={14} zoomInterp={17} fldInterp={16} mapShift={8} lastMapDelta=({9:F1},{10:F1}) tracked mtl={11} spr={15} maps={12} mult={13}",
                Mode.Value, s_statLogic, s_statSkip, s_statLogic > 0 ? (float)s_statCalls / s_statLogic : 0f, s_statCallsMax, s_statReplayed, s_statDropped,
                s_statPosInterp, s_statMapShift, s_statLastDx, s_statLastDy, s_pos.Count, s_maps.Count, GameSpeed.EffectivePercent(), s_statSprInterp, s_sprites.Count, s_statFldInterp, s_statZoomInterp, s_statSuppressed)
                + Interp60Battle.StatText() + Interp60FldAnim.StatText());
            s_statTime = now;
            s_statLogic = s_statSkip = s_statCalls = s_statCallsMax = s_statReplayed = s_statPosInterp = s_statSprInterp = s_statZoomInterp = s_statFldInterp = s_statMapShift = s_statDropped = s_statSuppressed = 0;
        }
    }

    /// <summary>GameMain.Update の Postfix: 記録を閉じる (Prefix で止めたフレームでも呼ばれる)。</summary>
    [HarmonyPatch(typeof(GameMain), "Update")]
    internal static class Interp60FrameEndPatch
    {
        private static void Postfix()
        {
            Interp60.OnFrameEnd();
        }
    }

    /// <summary>
    /// Main.Disp.run の Prefix: 1 Unity フレームに複数回回す時 (倍速) は最後の run の描画だけを記録に残す。
    /// Postfix: 複製フレームで見た Esc のキーアップで本体の終了ダイアログを開く。本体は run の case 2 (ゲーム中) で
    /// `else if (Input.GetKeyUp(Escape)) { m_isEndDialog = true; m_endgame.Initialize(); }` としており、GetKeyUp は
    /// その Unity フレームしか true でないので複製フレームに当たると拾えない。同じ条件 (proc==2、ダイアログが開いていない) で
    /// 同じことを行う。
    /// </summary>
    [HarmonyPatch(typeof(Main.Disp), nameof(Main.Disp.run))]
    internal static class Interp60RunStartPatch
    {
        // proc / m_isEndDialog / m_endgame は入れ子クラス Disp ではなく外側の Main の static メンバー
        private static readonly FieldInfo s_isEndDialog = AccessTools.Field(typeof(Main), "m_isEndDialog");
        private static readonly FieldInfo s_endgame = AccessTools.Field(typeof(Main), "m_endgame");
        private static readonly MethodInfo s_initialize = AccessTools.Method(AccessTools.TypeByName("EndGameSteam"), "Initialize");

        private static void Prefix()
        {
            Interp60.OnRunStart();
        }

        private static void Postfix()
        {
            if (!Interp60.Active || s_isEndDialog == null || s_endgame == null || s_initialize == null) return;
            if (Main.proc != 2) return;
            if ((bool)s_isEndDialog.GetValue(null))
            {
                // 開いている間の Esc は DialogInput 側 (Interp60EndDialogClosePatch) が使う。残っていれば捨てる
                Interp60.TakeEscUp();
                return;
            }
            // Esc がキーボードの機能に割り当てられている間は本体の終了ダイアログを Esc で開かない (EscCancelPatch と同じ条件)
            if (KeyMap.EscMapped) { Interp60.TakeEscUp(); return; }
            if (!Interp60.TakeEscUp()) return;
            object endgame = s_endgame.GetValue(null);
            if (endgame == null) return;
            s_isEndDialog.SetValue(null, true);
            s_initialize.Invoke(endgame, null);
        }
    }

    /// <summary>
    /// EndGameSteam.DialogInput の Postfix: 複製フレームで見た Esc のキーアップで終了ダイアログを閉じる
    /// (本体は `if (Input.GetKeyUp(Escape)) { Sound.setSe(212, 10); m_isInput = true; }`。開いた Unity フレーム内は本体と同じく見ない)。
    /// </summary>
    [HarmonyPatch]
    internal static class Interp60EndDialogClosePatch
    {
        private static readonly FieldInfo s_isInput = AccessTools.Field(AccessTools.TypeByName("EndGameSteam"), "m_isInput");

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("EndGameSteam"), "DialogInput");
        }

        private static void Postfix(object __instance)
        {
            if (!Interp60.Active || s_isInput == null) return;
            if (Time.frameCount == EndDialogOpenPatch.OpenedFrame) return;
            if ((bool)s_isInput.GetValue(__instance)) return;
            if (!Interp60.TakeEscUp()) return;
            Sound.setSe(212, 10);
            s_isInput.SetValue(__instance, true);
        }
    }

    /// <summary>BgMap.Draw の中の描画を「地形スコープ」として記録する (m_scroll_bg.Draw の入れ子も別スコープ)。</summary>
    [HarmonyPatch(typeof(BgMap), nameof(BgMap.Draw))]
    internal static class Interp60MapScopePatch
    {
        private static void Prefix(BgMap __instance)
        {
            Interp60.EnterMap(__instance);
        }

        private static void Postfix()
        {
            Interp60.LeaveMap();
        }
    }

    /// <summary>
    /// Util.Sprite.Draw() (DrawEx / DrawClone / Draw(x, y) は全てここに来る) の中を「この Util.Sprite の描画」として
    /// Interp60 に知らせる。フィールドのキャラは Sys.Sprite.set が毎論理フレーム Util.Sprite.Init → Term() でマテリアルを
    /// 破棄して作り直すので、Material ではなく Util.Sprite のインスタンスで前回位置を引く。
    /// </summary>
    [HarmonyPatch(typeof(Util.Sprite), "Draw", new Type[0])]
    internal static class Interp60SpriteScopePatch
    {
        private static void Prefix(Util.Sprite __instance)
        {
            Interp60.EnterSprite(__instance);
        }

        private static void Postfix()
        {
            Interp60.LeaveSprite();
        }
    }

    /// <summary>Core.draw (フィールドの描画全体) の中だけスプライトの位置を補間する (メニューの ordersprite は対象外)。</summary>
    [HarmonyPatch(typeof(Core), nameof(Core.draw))]
    internal static class Interp60CoreScopePatch
    {
        private static void Prefix()
        {
            Interp60.EnterCore();
        }

        private static void Postfix()
        {
            Interp60.LeaveCore();
        }
    }

    /// <summary>
    /// FldObject.SetPositon (BgMap.SetPosEx がスクロールに合わせて 3D オブジェクトの root を動かす所) の Postfix。
    /// 3D オブジェクトは MeshRenderer が描くので DrawMesh の補間が届かない。root の位置を補間する (Interp60.OnFldPosition)。
    /// </summary>
    [HarmonyPatch(typeof(FldObject), nameof(FldObject.SetPositon))]
    internal static class Interp60FldPositionPatch
    {
        private static void Postfix(FldObject __instance, float x, float y, float z)
        {
            Interp60.OnFldPosition(__instance, x, y, z);
        }
    }

    /// <summary>
    /// FLdDrawer.Update (フィールドの 3D オブジェクトの MonoBehaviour) は「この論理フレームで BgMap.Draw が見せた物だけ描き、
    /// それ以外は MeshRenderer を無効にする」。複製フレームでは BgMap.Draw が走らないので、そのままだと 3D オブジェクトが
    /// 1 フレームおきに消える。複製フレームでは何もしない (元処理を止める理由: 状態を前フレームのまま保つため)。
    /// ただし動いている鍵フレームアニメ (ワールドマップの巻物など) だけは複製フレームで半歩先の姿をプレビューする (Interp60FldAnim)。
    /// </summary>
    [HarmonyPatch(typeof(FLdDrawer), "Update")]
    internal static class Interp60FldDrawerPatch
    {
        private static bool Prefix(FLdDrawer __instance)
        {
            return Interp60FldAnim.OnDrawerUpdate(__instance);
        }

        private static void Postfix()
        {
            Interp60FldAnim.OnDrawerUpdateEnd();
        }
    }
}
