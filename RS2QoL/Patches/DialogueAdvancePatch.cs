using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// フィールド / イベントの会話ウィンドウの送りを「ボタンを新たに押した瞬間」だけにし、十字キーでも送れるようにする。
    ///
    /// 本体は会話の「▼ 待ち」(Core.pause_sa) と「文末待ち」(Core.message_lop2 の末尾分岐) を
    /// Core.key_is_what で送る。key_is_what は Core.get_key_rep が joy_data[0] (押しっぱなし状態) から作る値で、
    /// 押した瞬間に加えて長押しのリピート (400ms 後から 120ms 間隔) でも立つ。そのため決定を押したままだと
    /// 文章が 120ms ごとに流れて読めない。選択肢 (Core.put_select) も同じ値で決定するので、押したままだと選択肢まで勝手に決まる。
    ///
    /// ここでは対象メソッドの間だけ key_is_what を「押下エッジ (joy_data[1] = Sys.key[1])」に差し替え、終わったら戻す。
    /// 受け付けるボタンの種類は本体のまま (決定 / キャンセル / メニュー / ポーズ / L / R / 情報)。
    /// 本体は十字キーでは送れない (方向ビットがマスク外) ので、十字キーを新たに押した瞬間を決定扱いにして足す (DialogueAdvanceByDpad)。
    /// 選択肢ではカーソル移動 (上下のリピート) は本体のまま残し、決定 / キャンセルのビットだけエッジにする。
    /// 戦闘中のメッセージ (タイマー自動送り) には触らない。
    /// </summary>
    internal static class DialogueAdvance
    {
        public static ConfigEntry<bool> WaitForPress;
        public static ConfigEntry<bool> AdvanceByDpad;

        // Core.pause_sa / message_lop2 が見るビット (決定 0x1|0x100000, キャンセル 0x400000, メニュー 0x200000, ポーズ 0x100, L 0x10, 情報 0x20, R 0x40)
        private const int ADVANCE_BITS = 0x700171;
        private const int OK_BIT = 0x100000;
        private const int DIR_BITS = 0xF0000;
        // Core.put_select が決定 / キャンセルとして見るビット
        private const int SELECT_BITS = 0x100001 | 0x400000;

        public static void Bind(ConfigFile config)
        {
            WaitForPress = config.Bind("Field", "DialogueWaitForPress", true,
                "会話ウィンドウの送り (▼ と文末) と選択肢の決定を、ボタンを新たに押した時だけにする (長押しでは流れない)");
            AdvanceByDpad = config.Bind("Field", "DialogueAdvanceByDpad", true,
                "会話ウィンドウを十字キー (新たに押した瞬間) でも送れるようにする");
        }

        public static bool WaitEnabled { get { return WaitForPress != null && WaitForPress.Value; } }
        public static bool DpadEnabled { get { return AdvanceByDpad != null && AdvanceByDpad.Value; } }

        /// <summary>▼ 待ち / 文末待ち用。戻り値は元の key_is_what。</summary>
        public static int AdvanceMask(Core c)
        {
            int orig = c.key_is_what;
            int edge = c.joy_data[1];
            int v = WaitEnabled ? (orig & ~ADVANCE_BITS) | (edge & ADVANCE_BITS) : orig;
            if (DpadEnabled && (edge & DIR_BITS) != 0) v |= OK_BIT;
            c.key_is_what = v;
            return orig;
        }

        /// <summary>選択肢用。決定 / キャンセルだけエッジにする。戻り値は元の key_is_what。</summary>
        public static int SelectMask(Core c)
        {
            int orig = c.key_is_what;
            c.key_is_what = (orig & ~SELECT_BITS) | (c.joy_data[1] & SELECT_BITS);
            return orig;
        }
    }

    // ▼ 待ち (ページ送り)。受け付けても key_is_what は触らないので常に戻す
    [HarmonyPatch(typeof(Core), "pause_sa")]
    internal static class DialogueAdvancePausePatch
    {
        private static void Prefix(Core __instance, out int __state)
        {
            __state = (DialogueAdvance.WaitEnabled || DialogueAdvance.DpadEnabled) ? DialogueAdvance.AdvanceMask(__instance) : int.MinValue;
        }

        private static void Postfix(Core __instance, int __state)
        {
            if (__state != int.MinValue) __instance.key_is_what = __state;
        }
    }

    // 文末待ち。message_lop2 は毎フレーム文字送りにも使われるので、文末 (mess_adrs >= end_mess_adrs) の時だけ差し替える。
    // 受け付けると本体が key_is_what = 0 にする (二重処理防止) ので、その場合は戻さない
    [HarmonyPatch(typeof(Core), "message_lop2")]
    internal static class DialogueAdvanceEndPatch
    {
        private struct State { public int orig; public int masked; public bool applied; }

        private static void Prefix(Core __instance, out State __state)
        {
            __state = default(State);
            if (!(DialogueAdvance.WaitEnabled || DialogueAdvance.DpadEnabled)) return;
            if (__instance.mess_flag == 0 || Core.mess_adrs < __instance.end_mess_adrs) return;
            __state.orig = DialogueAdvance.AdvanceMask(__instance);
            __state.masked = __instance.key_is_what;
            __state.applied = true;
        }

        private static void Postfix(Core __instance, State __state)
        {
            if (__state.applied && __instance.key_is_what == __state.masked) __instance.key_is_what = __state.orig;
        }
    }

    // 選択肢。決定 / キャンセルだけエッジにし、上下のカーソルリピートは残す。key_is_what は触られないので常に戻す
    [HarmonyPatch(typeof(Core), "put_select")]
    internal static class DialogueAdvanceSelectPatch
    {
        private static void Prefix(Core __instance, out int __state)
        {
            __state = DialogueAdvance.WaitEnabled ? DialogueAdvance.SelectMask(__instance) : int.MinValue;
        }

        private static void Postfix(Core __instance, int __state)
        {
            if (__state != int.MinValue) __instance.key_is_what = __state;
        }
    }

    /// <summary>
    /// 会話・イベントが終わってから十字キーを一度も離していない間を覚える (Core.search_ivent は会話中も毎フレーム、移動処理の前に呼ばれる)。
    /// 会話を十字キー (DialogueAdvanceByDpad) で終えると、同じ押しっぱなしのままその NPC に歩き込む。
    /// 本体の「触れるとイベントが始まる人」(people_ivent_type == 2 → Core.its_enter_sa) にはガードが無く、
    /// 会話が終わった次のフレームにそのまま同じ会話を始めてしまう (MOD の押し当て会話 TalkOnBump は会話中にラッチして
    /// 十字キーを離すまで発動しないので、本体のこの経路だけが即座に再開する)。A で終えていた時は一度離して押し直すので起きなかった。
    /// </summary>
    [HarmonyPatch(typeof(Core), "search_ivent")]
    internal static class DialogueAdvanceHoldPatch
    {
        private const int DIR_BITS = 0xF0000;
        public static bool HeldSinceBusy;   // 会話・イベントの後、十字キーを離していない
        public static int Frame;            // search_ivent の通し番号 (ログの突き合わせ用)
        private static int s_prevIvent;
        private static int s_busyEndFrame;
        private static int s_stepAtBusy;    // busy の最後に見た StepSerial (これが進んだら会話の後に 1 マス歩いた)

        private static void Prefix(Core __instance)
        {
            Core c = __instance;
            Frame++;
            bool busy = (c.ivent_sw | c.mess_flag | c.ivent_stop_flag | c.exevt_flg | c.battle_flg | c.tono_flg | c.exdemo_flg) != 0;
            // 離したかは joy_data[0] ではなく Sys.key[0] (今のパッドの状態) で見る。会話の終わり (ok_mes_finish2) が joy_data を 0 にするので、
            // 次のフレームの search_ivent は joy_data[0] == 0 を見て「離した」と誤判定していた (その次のフレームで歩き込んで本体が再開する)
            bool prevHeld = HeldSinceBusy;
            string why = null;
            if (busy) { HeldSinceBusy = true; s_busyEndFrame = Frame; s_stepAtBusy = DialogueAdvanceStepPatch.StepSerial; }
            else if (((Sys.key[0] | c.joy_data[0]) & DIR_BITS) == 0) { HeldSinceBusy = false; why = "released"; }   // 生の入力と、本体が移動に使う 1 フレーム遅れのコピーの両方が離れてから
            // 会話の後に自分が 1 マス歩き出したら解く。再開の誤爆 (会話を閉じた押しのままその人に阻まれる) では 1 歩も動けないので区別できる。
            // 歩き去る NPC を押しっぱなしで追う時は「1 歩進む → 阻まれる」の繰り返しになるので、離さなくても次の押し当てで話せる
            else if (HeldSinceBusy && DialogueAdvanceStepPatch.StepSerial != s_stepAtBusy) { HeldSinceBusy = false; why = "stepped"; }
            if (prevHeld != HeldSinceBusy && Plugin.DiagOn)
                Plugin.Log.LogInfo(string.Format("[DialogueAdvance] held {0}→{1} ({12}) frame={2} ivent_sw={3} mess={4} stop={5} exevt={6} battle={7} tono={8} exdemo={9} joy0=0x{10:X} Sys.key0=0x{11:X}",
                    prevHeld, HeldSinceBusy, Frame, c.ivent_sw, c.mess_flag, c.ivent_stop_flag, c.exevt_flg, c.battle_flg, c.tono_flg, c.exdemo_flg, c.joy_data[0], Sys.key[0], why ?? "busy"));
            s_prevIvent = c.ivent_sw;
        }

        // Diagnostics: search_ivent でイベントが始まった (ivent_sw 0 → 非 0) 時の入力の状態を出す (会話のたびに 1 行)
        private static void Postfix(Core __instance)
        {
            Core c = __instance;
            if (s_prevIvent != 0 || c.ivent_sw == 0) return;
            if (!Plugin.DiagOn) return;
            Plugin.Log.LogInfo(string.Format("[DialogueAdvance] イベント開始 frame={0} (busy 終了から {1}) ivent_sw={2} no={3},{4} key_is_what=0x{5:X} joy0=0x{6:X} joy1=0x{7:X} Sys.key0=0x{8:X} Sys.key1=0x{9:X} held={10}",
                Frame, Frame - s_busyEndFrame, c.ivent_sw, c.ivent_no[0], c.ivent_no[1], c.key_is_what, c.joy_data[0], c.joy_data[1], Sys.key[0], Sys.key[1], HeldSinceBusy));
        }
    }

    /// <summary>
    /// 自分のマス移動が始まった回数 (Core.move_human_sub_r の Postfix で、止まっている → 動き出した の立ち上がりを数える)。
    /// 会話の後のラッチ (HeldSinceBusy / TalkOnBump) を「十字キーを離すか、1 マス歩き出すまで」にするために使う。
    /// イベントの自動歩行中も数えるが、その間は busy なのでラッチ側が毎フレーム基準値を取り直すため影響しない。
    /// </summary>
    [HarmonyPatch(typeof(Core), "move_human_sub_r")]
    internal static class DialogueAdvanceStepPatch
    {
        public static int StepSerial;
        private static bool s_prevStepping;

        private static void Postfix(Core __instance)
        {
            Core c = __instance;
            bool stepping = c.map_scrol_flag_v != 0 || c.map_scrol_flag_h != 0 || c.man_move_flag_v != 0 || c.man_move_flag_h != 0;
            if (stepping && !s_prevStepping) StepSerial++;
            s_prevStepping = stepping;
        }
    }

    // 会話・イベントの後、十字キーを離す (か 1 マス歩き出す) までは「触れると始まるイベント」を起こさず、普通の人と同じく進めない扱い (work_p[0] = 1) にする。
    // 元処理 (ivent_sw = 2 + そのマスへ進む) を止める必要があるので Prefix
    [HarmonyPatch(typeof(Core), "its_enter_sa")]
    internal static class DialogueAdvanceEnterPatch
    {
        private static bool s_logged;

        private static bool Prefix(Core __instance)
        {
            if (Plugin.DiagOn)
                Plugin.Log.LogInfo(string.Format("[DialogueAdvance] its_enter_sa frame={0} held={1} Sys.key0=0x{2:X} joy0=0x{3:X} person={4}",
                    DialogueAdvanceHoldPatch.Frame, DialogueAdvanceHoldPatch.HeldSinceBusy, Sys.key[0], __instance.joy_data[0], __instance.ivent_people_stok));
            if (!DialogueAdvanceHoldPatch.HeldSinceBusy) return true;
            if (((Sys.key[0] | __instance.joy_data[0]) & 0xF0000) == 0) return true;   // 離した直後の 1 フレームは joy_data がまだ押している (本体の移動はそれを使う)
            __instance.work_p[0] = 1;
            if (!s_logged)
            {
                s_logged = true;
                Plugin.Log.LogInfo("[DialogueAdvance] applied: 会話直後の触れるイベント (its_enter_sa) を十字キーを離すまで抑止");
            }
            return false;
        }
    }

    /// <summary>
    /// NPC 側から始まる会話を、会話・イベントの直後に十字キーを離すまで待たせる。
    /// NPC の行動パターン (people_move_pat) には「イベントを起こす」命令 (obj_move_command 20〜51 → Core.its_ivent_move) があり、
    /// 「追いかけて隣に来たら話す」型の NPC はこれで会話を始める。会話を十字キーで終えると押しっぱなしのまま NPC に阻まれて動けず、
    /// 2 フレーム後にまた捕まって同じ会話が始まる (実機ログ: 会話終了の 2 フレーム後に ivent_sw=1、search_ivent / its_enter_sa / 押し当て会話は通っていない)。
    /// A で終えて歩き去れば捕まらないので本体では目立たなかった。
    /// 本体には「取り込み中 (ivent_sw 等) なら people_move_count = 32 にして後で再試行」の分岐があるので、同じ扱いにする (Prefix で元処理を止める)。
    /// </summary>
    [HarmonyPatch(typeof(Core), "its_ivent_move")]
    internal static class DialogueAdvanceNpcEventPatch
    {
        private static readonly System.Reflection.FieldInfo s_moveNo = AccessTools.Field(typeof(Core), "move_no");
        private static bool s_logged;

        private static bool Prefix(Core __instance)
        {
            if (!DialogueAdvanceHoldPatch.HeldSinceBusy) return true;
            if (((Sys.key[0] | __instance.joy_data[0]) & 0xF0000) == 0) return true;   // 離した直後の 1 フレームは joy_data がまだ押している (本体の移動はそれを使う)
            int no = (int)s_moveNo.GetValue(__instance);
            if (no < 0 || no >= __instance.people.Length) return true;
            __instance.people[no].people_move_count = 32;
            __instance.move_not_mode();
            if (Plugin.DiagOn)
                Plugin.Log.LogInfo("[DialogueAdvance] its_ivent_move を抑止 frame=" + DialogueAdvanceHoldPatch.Frame + " person=" + no + " cmd=" + __instance.obj_move_command);
            if (!s_logged)
            {
                s_logged = true;
                Plugin.Log.LogInfo("[DialogueAdvance] applied: 会話直後の NPC 側から始まる会話 (its_ivent_move) を十字キーを離すまで待たせる (person=" + no + ")");
            }
            return false;
        }
    }

    /// <summary>
    /// 種別 1 (people_ivent_type == 1) の人に歩き込んだ時のイベント開始 (Core.its_touch_sa)。
    /// 種別 1 は敵シンボル (触れると戦闘) だけでなく、「十字キーを押し込むだけで話しかける人」もこれ
    /// (実機ログ: 決定ビット無しの key_is_what=0x20000 で ivent_sw=1、search_ivent / its_enter_sa / its_ivent_move は通っていない)。
    /// 本体の取り込み中の分岐 (work_p[0] = 1 = 進めない) と同じ扱いで、会話・イベントの後に十字キーを離すまで止める。
    /// 敵シンボルにも同じ扱いになるが、影響は「会話直後に十字キーを離さず自分から敵に歩き込んでも離すまで戦闘にならない」だけ (敵から触れてくる方は別経路 cant_move_sa_peo で本体のまま)。
    /// </summary>
    [HarmonyPatch(typeof(Core), "its_touch_sa")]
    internal static class DialogueAdvanceTouchPatch
    {
        private static bool s_logged;

        private static bool Prefix(Core __instance)
        {
            if (Plugin.DiagOn)
                Plugin.Log.LogInfo(string.Format("[DialogueAdvance] its_touch_sa frame={0} held={1} Sys.key0=0x{2:X} joy0=0x{3:X} person={4}",
                    DialogueAdvanceHoldPatch.Frame, DialogueAdvanceHoldPatch.HeldSinceBusy, Sys.key[0], __instance.joy_data[0], __instance.ivent_people_stok));
            if (!DialogueAdvanceHoldPatch.HeldSinceBusy) return true;
            if (((Sys.key[0] | __instance.joy_data[0]) & 0xF0000) == 0) return true;   // 離した直後の 1 フレームは joy_data がまだ押している (本体の移動はそれを使う)
            __instance.work_p[0] = 1;
            if (!s_logged)
            {
                s_logged = true;
                Plugin.Log.LogInfo("[DialogueAdvance] applied: 会話直後の押し込みで始まるイベント (its_touch_sa) を十字キーを離すまで抑止");
            }
            return false;
        }
    }

}
