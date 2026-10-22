using System;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// 弔いの鍛冶の演出 ([MourningForge] Enabled の時だけ)。皇帝が死んだ後の「フィールドで倒れる → 暗転」の暗転で、
    /// 倒れた皇帝の絵だけを暗転の中に残し、少し間を置いてから、文字の左右に開発所の職人 2 人が向かい合う「弔いの鍛冶」の窓を
    /// フェードインで出す。決定 / キャンセルで閉じると窓と皇帝が暗転に溶け、さらに間を置いてから本体の流れ (継承の画面) に進む。
    /// [BattleReview] も On なら、その後 (継承の画面の入口) で戦闘の振り返りの窓が出る
    /// (ユーザー 2026-10-22: 同時に出ていた 2 つの知らせを「弔いの鍛冶 → 振り返り」の順に。間と フェードを入れる)。
    ///
    /// 本体の流れ (script.bin。tools/script-analysis/out/ops.txt の msg 2058 全滅 / 2059 皇帝の LP 0):
    ///   EV SPECIAL 243 (change_obj_poze: 皇帝の向きを 4 = イベントポーズにして man_event_pose = 倒れたコマ) → wait(2) → music3
    ///   → fade_in → wait(64) → fade_out → sub_map_jump 46 → … → koutei_select / koutei_select2 (ここで本体は継承の画面へ)。
    ///   最終皇帝 (memberdata[0][2] が 32 / 33) は msg 2835 / 2836 に分かれて継承しない。
    /// - 暗転 (SUB fade_out = Core.fade_out_on_message) は Sys.drawFade が全画面に黒い板 1 枚 (renderQueue 8000) を重ねるだけで、
    ///   スプライトの色は変えない。OnGUI はその上に描かれるので、皇帝の絵を同じ位置に描き直せば暗転の中に残る。
    /// - fade_out_on_message は message_proc[0] == 0 の最初の 1 回で Sys.setFade(350) を始め、Sys.fadeflg の間は mess_adrs-- で
    ///   同じ命令に留まり、終わると increment_adrs で次へ進む。暗転が終わった後も MOD が mess_adrs-- を続ければ (Prefix で元処理を止める)
    ///   script はそこで待つ。演出が終わったら元処理に戻し、本体が次へ進む。
    /// - 皇帝の絵は Sys.spr[0] (put_main_chara が毎論理フレーム set する。x / y = 基準座標、sx = コマ × 26、flip &amp; 2 = 左右反転)。
    ///   画面の位置は OrderingSprite.draw と同じ get_screen_xf2(x) / get_screen_yf(y) (仮想 960x540、1 ドット = 36/16) を画面の px に直す。
    ///   絵は仲間の絵と同じ組み方 (PartyWeight.BuildFrames: ImageEfx.init(4, obj, 16) + setObjPalette(obj, pal)) で、コマは sx / 26。
    /// - 職人の絵は開発所 (map 384) の武器の職人 (5,6) obj 54 と防具の職人 (7,6) obj 50 (tools/script-analysis/out/maps.txt)。
    ///   横向きは people_chara_tbl のとおり、コマ 2 (x = 52) が左向き、その左右反転が右向き。歩きはコマ 3 (x = 78) を 1 ドット下げて交互に。
    ///   (開発所の背景の絵から作業台を切り出して間に置く案は実機で合わず、ユーザーが取り下げた 2026-10-22。出し方の記録は RS2QoL/README.md)
    /// - 回数は演出を始める時に増やす (MourningForge.GrantForScene)。本体の koutei_select / koutei_select2 の Postfix (元の増やし方) は
    ///   演出で増やした後は飛ばす。演出の条件が揃わない時 (乗り物、絵が組めない等) は元の増やし方と画面上部の知らせのまま。
    /// </summary>
    internal static class MourningScene
    {
        private const string TAG = "[Mourning]";
        private const int WEAPON_SMITH_OBJ = 54, ARMOR_SMITH_OBJ = 50;
        private const int FRAME = 26;                   // 1 コマのドット数
        private const float DOT = 36f / 16f;            // 本体の 1 ドット = 仮想 960x540 の 2.25 単位
        private const float ANIM_SECONDS = 8f / 30f;    // 歩き (作業) のコマ送り (本体の people_anime は 8 論理フレームごと)
        private const float PAUSE_BEFORE_WINDOW = 0.9f; // 暗転が終わってから窓が出始めるまで (皇帝だけが残る間)
        private const float WINDOW_IN = 0.5f;           // 窓のフェードイン
        private const float WINDOW_OUT = 0.35f;         // 閉じた後の窓のフェードアウト
        private const float EMPEROR_OUT = 0.9f;         // 閉じた後、皇帝の絵が暗転に溶ける時間
        private const float PAUSE_AFTER = 0.8f;         // 皇帝が消えてから本体の流れ (→ 振り返り) に進むまでの間
        private const float HOLD_TIMEOUT = 60f;         // 何かの事情で演出が進まない時に script を放す
        private const float TIMEOUT_SECONDS = 15f;      // 暗転が終わらない等で窓が開かない時に絵を捨てる
        private const int BTN_OK = 0x100000, BTN_CANCEL = 0x400000;

        private enum Phase { None, Fading, Pause, WindowIn, Open, Closing, Done }
        private static Phase s_phase = Phase.None;
        private static bool s_guard;       // 閉じた後、決定 / キャンセルが離されるまで入力を飲む
        private static Texture2D s_emperor;
        private static int s_sprX, s_sprY, s_flip;
        private static string s_line1 = "", s_line2 = "";
        private static float s_startTime, s_phaseStart, s_closeTime, s_holdStart;
        private static int s_prevKey = -1, s_lastEscFrame = -1;

        private static readonly Texture2D[][] s_smith = new Texture2D[2][];   // [職人][0 = 立ち (コマ 2), 1 = 歩き (コマ 3)]
        private static bool s_partsTried;

        private static GUIStyle s_title, s_body, s_hint;
        private static int s_styleSize;

        public static bool IsOpen { get { return s_phase == Phase.Open; } }

        // ---------------------------------------------------------------- script 側 (Core.fade_out_on_message の Prefix)

        /// <summary>true = 元処理を行う。false = 演出の間、同じ命令に留まる。</summary>
        public static bool OnFadeOut(Core c)
        {
            try
            {
                if (c.message_proc[0] == 0)
                {
                    if (s_phase == Phase.None) TryBegin(c);
                    return true;
                }
                if (s_phase == Phase.None || s_phase == Phase.Done) return true;
                if (Sys.fadeflg) return true;          // 暗転の途中: 本体に任せる (mess_adrs-- で同じ命令を繰り返す)
                float now = Time.realtimeSinceStartup;
                if (s_phase == Phase.Fading)
                {
                    s_phase = Phase.Pause;
                    s_phaseStart = s_holdStart = now;
                    Plugin.Log.LogInfo(TAG + " fade done, holding the script");
                }
                else if (s_phase == Phase.Closing && now - s_closeTime >= EMPEROR_OUT + PAUSE_AFTER)
                {
                    Finish("scene end");
                    return true;
                }
                else if (now - s_holdStart > HOLD_TIMEOUT)
                {
                    Finish("timeout");
                    return true;
                }
                Core.mess_adrs--;                      // 本体の「待ち」と同じ: 次のフレームもこの命令に来る
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(TAG + " scene failed (" + e.Message + "), vanilla flow");
                Reset();
                return true;
            }
        }

        private static void TryBegin(Core c)
        {
            if (!MourningForge.On || !MourningForge.DeathPending) return;
            if (c.man_direction < 4 || c.vehicle_flag != 0 || c.man_obj_no == 255 || c.main_chr_no_put_flag != 0) return;
            int[] m = Work.memberdata != null && Work.memberdata.Length > 0 ? Work.memberdata[0] : null;
            if (m == null || m.Length < 19) return;
            int cls = m[2] & 0xFF;
            if (cls == 32 || cls == 33) return;        // 最終皇帝は継承しない (msg 2835 / 2836)
            Sys.Sprite sp = Sys.spr != null && Sys.spr.Length > 0 ? Sys.spr[0] : null;
            if (sp == null || sp.img == null) return;
            int col = sp.sx / FRAME;
            Texture2D[] f = PartyWeight.BuildFrames(cls, m[0] & 0xFFFF, new[] { col }, "rs2qol_mourning_emperor");
            if (f == null || f[0] == null) { Plugin.Log.LogWarning(TAG + " emperor image unavailable (obj " + cls + ", col " + col + "), notice only"); return; }
            EnsureParts();
            if (s_emperor != null) UnityEngine.Object.Destroy(s_emperor);
            s_emperor = f[0];
            s_sprX = sp.x; s_sprY = sp.y; s_flip = sp.flip;
            string text = MourningForge.GrantForScene();
            int cut = text.LastIndexOf(" (");
            s_line1 = cut > 0 ? text.Substring(0, cut) : text;
            s_line2 = cut > 0 ? text.Substring(cut + 1) : "";
            s_phase = Phase.Fading;
            s_guard = false;
            s_startTime = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo(TAG + " applied: death scene (obj " + cls + " col " + col + " flip " + s_flip + " at " + s_sprX + "," + s_sprY + ")");
        }

        /// <summary>職人 2 人 (立ち / 歩きのコマ)。一度だけ組む。</summary>
        private static void EnsureParts()
        {
            if (s_partsTried) return;
            s_partsTried = true;
            s_smith[0] = PartyWeight.BuildFrames(WEAPON_SMITH_OBJ, 0, new[] { 2, 3 }, "rs2qol_smith_w");
            s_smith[1] = PartyWeight.BuildFrames(ARMOR_SMITH_OBJ, 0, new[] { 2, 3 }, "rs2qol_smith_a");
            Plugin.Log.LogInfo(TAG + " parts: smith " + (HasSmith(0) ? "ok" : "none") + " / " + (HasSmith(1) ? "ok" : "none"));
        }

        private static bool HasSmith(int i) { return s_smith[i] != null && s_smith[i][0] != null; }

        private static void Close()
        {
            if (s_phase != Phase.Open) return;
            s_phase = Phase.Closing;
            s_guard = true;
            s_closeTime = Time.realtimeSinceStartup;
            UiParts.SeClose();
            Plugin.Log.LogInfo(TAG + " scene window close");
        }

        /// <summary>演出を終えて script を放す (絵は捨てる。次の戦闘まで再び始めない)。</summary>
        private static void Finish(string why)
        {
            s_phase = Phase.Done;
            if (s_emperor != null) { UnityEngine.Object.Destroy(s_emperor); s_emperor = null; }
            Plugin.Log.LogInfo(TAG + " " + why + ", script released");
        }

        /// <summary>戦闘の始まり / routine_reset から。描いているものを全部捨てる。</summary>
        public static void Reset()
        {
            s_phase = Phase.None;
            s_guard = false;
            if (s_emperor != null) { UnityEngine.Object.Destroy(s_emperor); s_emperor = null; }
        }

        // ---------------------------------------------------------------- 入力 (Util.GetKeypadState の最終段。BattleReview.Tick の前)

        public static int Tick(int key)
        {
            if (s_phase == Phase.None || s_phase == Phase.Fading || s_phase == Phase.Done)
            {
                if (!s_guard) return key;
                if ((key & (BTN_OK | BTN_CANCEL)) == 0) s_guard = false;
                return 0;
            }
            int pressed = s_prevKey < 0 ? 0 : key & ~s_prevKey;
            s_prevKey = key;
            bool escEdge = Time.frameCount != s_lastEscFrame && (Input.GetKeyDown(KeyCode.Escape) || Interp60.TakeKeyDown(KeyCode.Escape));
            s_lastEscFrame = Time.frameCount;
            if (s_phase == Phase.Open && ((pressed & (BTN_OK | BTN_CANCEL)) != 0 || escEdge)) Close();
            return 0;
        }

        // ---------------------------------------------------------------- 描画 (Plugin.OnGUI。BattleReview.Draw の前 = 一番下)

        public static void Draw()
        {
            if (s_phase == Phase.None || s_phase == Phase.Done) return;
            try { DrawScene(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(TAG + " scene draw failed (" + e.Message + ")");
                if (s_phase == Phase.Fading) Reset(); else Finish("draw failed");
            }
        }

        /// <summary>皇帝の絵の画面上の位置 (本体の OrderingSprite.draw と同じ式を画面の px に直す)。</summary>
        private static Rect EmperorRect()
        {
            float unit = Screen.height / 540f;
            float offX = (Screen.width - 960f * unit) / 2f;
            float vx = Sys.get_screen_xf2(s_sprX), vy = Sys.get_screen_yf(s_sprY);
            float size = FRAME * DOT * unit;
            return new Rect(offX + vx * unit, vy * unit, size, size);
        }

        private static void DrawScene()
        {
            float now = Time.realtimeSinceStartup;
            if (s_phase == Phase.Fading && now - s_startTime > TIMEOUT_SECONDS) { Reset(); return; }
            // 段階を進める (時間だけで決まるので OnGUI の中で進めてよい)
            if (s_phase == Phase.Pause && now - s_phaseStart >= PAUSE_BEFORE_WINDOW)
            {
                s_phase = Phase.WindowIn;
                s_phaseStart = now;
                s_prevKey = -1;
                UiParts.SeOk();                        // 窓が出始める時に鳴らす
                Plugin.Log.LogInfo(TAG + " scene window open");
            }
            else if (s_phase == Phase.WindowIn && now - s_phaseStart >= WINDOW_IN) s_phase = Phase.Open;

            float emperorAlpha = 1f, windowAlpha = 0f;
            switch (s_phase)
            {
                case Phase.WindowIn: windowAlpha = Mathf.Clamp01((now - s_phaseStart) / WINDOW_IN); break;
                case Phase.Open: windowAlpha = 1f; break;
                case Phase.Closing:
                    windowAlpha = Mathf.Clamp01(1f - (now - s_closeTime) / WINDOW_OUT);
                    emperorAlpha = Mathf.Clamp01(1f - (now - s_closeTime) / EMPEROR_OUT);
                    break;
            }
            Color old = GUI.color;
            if (s_phase != Phase.Fading)
            {
                GUI.color = Color.black;
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            }
            Rect er = EmperorRect();
            if (s_emperor != null && emperorAlpha > 0f)
            {
                GUI.color = new Color(1f, 1f, 1f, emperorAlpha);
                bool mirror = s_flip >= 0 && (s_flip & 2) != 0;
                GUI.DrawTextureWithTexCoords(er, s_emperor, mirror ? new Rect(1f, 0f, -1f, 1f) : new Rect(0f, 0f, 1f, 1f), true);
            }
            GUI.color = old;
            if (windowAlpha > 0f) DrawWindow(er, windowAlpha);
        }

        private static void EnsureStyles(int size)
        {
            if (s_title != null && s_styleSize == size) return;
            s_styleSize = size;
            s_title = new GUIStyle(GUI.skin.label);
            s_title.fontSize = size;
            s_title.fontStyle = FontStyle.Bold;
            s_title.alignment = TextAnchor.MiddleLeft;
            s_title.wordWrap = false;
            s_title.normal.textColor = Color.black;                            // どこでも大臣の知らせの見出しと同じ
            s_body = new GUIStyle(s_title);
            s_body.normal.textColor = new Color(0.45f, 0.05f, 0.02f);         // 灰色の紙の上の暗い赤 (同上)
            s_hint = new GUIStyle(s_title);
            s_hint.fontSize = Mathf.Max(10, size * 3 / 4);
            s_hint.fontStyle = FontStyle.Normal;
            s_hint.alignment = TextAnchor.MiddleRight;
            s_hint.normal.textColor = new Color(0.25f, 0.22f, 0.2f);
        }

        /// <summary>
        /// 文字の左右に職人 2 人が向かい合う窓を、倒れた皇帝の少し上に (画面の中央寄せ)。
        /// alpha はフェード (部品の描画は GUI.color を無視するので、描いた後に黒を (1 − alpha) で重ねる。背景が黒なので同じに見える)。
        /// </summary>
        private static void DrawWindow(Rect emperor, float alpha)
        {
            int size = Mathf.Max(12, Screen.height / 36);                     // Wallet.DrawNotice と同じ文字の大きさ
            EnsureStyles(size);
            int dot = TreasuryScreen.MinisterDot;                            // 本体の 1 ドット = 画面の dot px (整数)
            float spr = FRAME * dot;
            bool left = HasSmith(0), right = HasSmith(1);
            float padY = Mathf.Round(size * 0.6f);
            float padX = Mathf.Round(size * 1.6f);
            float gap = Mathf.Round(size * 1.0f);
            float lineH = Mathf.Round(size * 1.3f);
            float hintH = Mathf.Round(size * 1.0f);
            string title = MourningForge.NAME;
            string hint = "決定・キャンセル・Esc: 進む";
            float textW = Mathf.Max(s_title.CalcSize(new GUIContent(title)).x,
                Mathf.Max(s_body.CalcSize(new GUIContent(s_line1)).x, s_body.CalcSize(new GUIContent(s_line2)).x)) + size * 0.3f;
            float w = Mathf.Min(Screen.width - size * 2, padX * 2 + (left ? spr + gap : 0f) + textW + (right ? gap + spr : 0f));
            float bodyH = Mathf.Max(spr + dot, lineH * 3);                   // 歩きのコマは 1 ドット下がる
            float h = padY * 2 + bodyH + hintH;
            float x = Mathf.Round((Screen.width - w) / 2f);
            float y = Mathf.Round(emperor.y - size * 0.8f - h);               // 皇帝の少し上
            y = Mathf.Clamp(y, Mathf.Round(size * 1.5f), Mathf.Max(Mathf.Round(size * 1.5f), Screen.height - h - size));
            Rect r = new Rect(x, y, Mathf.Round(w), Mathf.Round(h));
            bool paper = UiParts.DrawPaperWindow(r, true);
            if (!paper) UiParts.DrawDarkBox(r);
            UiParts.Shadow = !paper;
            float cy = r.y + padY;
            float sy = cy + Mathf.Round((bodyH - spr - dot) / 2f);
            if (left) DrawSmith(new Rect(r.x + padX, sy, spr, spr), 0, true, dot);              // 左の職人は右を向く
            if (right) DrawSmith(new Rect(r.xMax - padX - spr, sy, spr, spr), 1, false, dot);   // 右の職人は左を向く
            float tx = r.x + padX + (left ? spr + gap : 0f);
            float textRight = r.xMax - padX - (right ? spr + gap : 0f);
            float ty = cy + Mathf.Round((bodyH - lineH * 3) / 2f);
            UiParts.Label(new Rect(tx, ty, textRight - tx, lineH), title, s_title);
            UiParts.Label(new Rect(tx, ty + lineH, textRight - tx, lineH), s_line1, s_body);
            UiParts.Label(new Rect(tx, ty + lineH * 2, textRight - tx, lineH), s_line2, s_body);
            UiParts.Label(new Rect(r.x + padX, r.yMax - padY - hintH, r.width - padX * 2, hintH), hint, s_hint);
            UiParts.Shadow = true;
            if (alpha < 1f)
            {
                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 1f - alpha);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = old;
            }
        }

        /// <summary>職人 i を横向きで (mirror = 右向き = 左向きのコマの反転)。立ちと歩きのコマを交互に (歩きは 1 ドット下) で作業中に見せる。</summary>
        private static void DrawSmith(Rect r, int i, bool mirror, int dot)
        {
            bool walk = s_smith[i][1] != null && ((int)((Time.realtimeSinceStartup - s_phaseStart + i * ANIM_SECONDS / 2f) / ANIM_SECONDS) & 1) != 0;
            Texture2D t = walk ? s_smith[i][1] : s_smith[i][0];
            if (walk) r.y += dot;
            Color old = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTextureWithTexCoords(r, t, mirror ? new Rect(1f, 0f, -1f, 1f) : new Rect(0f, 0f, 1f, 1f), true);
            GUI.color = old;
        }
    }

    /// <summary>script の SUB fade_out (sp_effect_code 13)。死の後の暗転で演出を出し、終わるまで同じ命令に留まる。</summary>
    [HarmonyPatch(typeof(Core), nameof(Core.fade_out_on_message))]
    internal static class MourningScene_fade_out_Patch
    {
        private static bool Prefix(Core __instance) { return MourningScene.OnFadeOut(__instance); }
    }
}
