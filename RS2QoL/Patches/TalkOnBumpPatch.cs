using BepInEx.Configuration;
using HarmonyLib;

namespace RS2QoL.Patches
{
    /// <summary>
    /// フィールドで NPC に十字キーを押し当て続けると、決定を押さなくても話しかける。
    ///
    /// 本体の NPC は people_ivent_type が 0 = 決定で話す、1 = 触れると戦闘 (モンスター)、2 = 触れるとイベント開始 の 3 種で、
    /// 「ぶつかるだけで会話が始まる人」は 2 のデータを持つ NPC。0 の NPC は Core.search_ivent (フィールドの毎論理フレーム、
    /// 移動処理の前に呼ばれる) が key_is_what の決定ビットを見て、正面のマスの人を調べて ivent_sw = 1 にする。
    ///
    /// 押し当ての検出は Core.move_check_routine (自分の 1 歩の可否判定。冒頭で目的のマスの人を work_q[1] に入れ、
    /// 1 = モンスター / 2 = 進入イベントはこの中で本体が処理し、進めなければ work_p[0] = 1) の Postfix で
    /// 「進めなかった + 目的のマスに 0 の人がいる」を数える。人がいるマスが自分の入れない地形 (店のカウンターの奥など) だと
    /// 本体は人ではなく地形で止める (cant_move_sa_by_ori_id → cant_move_sa) ので、cant_move_sa_by_peo では拾えない。
    /// 同じ人に TalkOnBumpFrames 論理フレーム分止められたら、次の search_ivent の間だけ key_is_what に決定ビットを足して
    /// 本体の会話開始処理をそのまま通し、Postfix で戻す。本物の決定押しには触らない。
    /// 正面のマスを見るのは本体なので宝箱は対象にならない (宝箱は人ではないので押し当てにならない)。
    ///
    /// 誤爆対策:
    /// - 会話・イベント中はラッチし、十字キーを全部離すか自分が 1 マス歩き出すまで解除しない (会話を閉じた押しのまま押し当てが続いても再開しない。
    ///   歩き去る NPC を押しっぱなしで追う時は 1 歩進んでから阻まれるので、離さなくても話せる。歩き出しでクールダウンも終わる)。
    /// - 会話・イベントが終わってから TalkOnBumpCooldownFrames の間は押し当てを数えない (会話送りの連打の最後が NPC に向いていても拾わない)。
    /// - 押し当ては十字キーを離さない限り同じ人に対して累積する (歩く NPC を後ろから追う時は止められるのが数フレームずつしか続かないため)。
    ///   途切れが 1 秒を超えるか別の人に当たったら数え直し。しきい値は実時間基準 (倍速中は倍率を掛ける)。
    /// </summary>
    internal static class TalkOnBump
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<int> Frames;
        public static ConfigEntry<int> CooldownFrames;

        private const int OK_BIT = 0x100000;
        private const int OK_BITS = 0x100001;
        private const int DIR_BITS = 0xF0000;

        private static int s_frame;                 // search_ivent の呼び出し回数 = フィールドの論理フレーム
        private static int s_lastBumpFrame = -100;  // 最後に人に止められたフレーム
        private static int s_lastBumpPerson = -1;
        private static int s_streak;                // 同じ人に続けて止められた回数
        private static bool s_latched;              // 十字キーを全部離すか 1 マス歩き出すまで発動しない
        private static int s_busyUntil = -100;      // 会話・イベント終了後のクールダウンの終わり
        private static int s_stepAtBusy;            // busy の最後に見た StepSerial (これが進んだら会話の後に 1 マス歩いた)
        private static bool s_blockLogged;          // 診断: 1 回の押し当て (streak) につき「発動できなかった理由」を 1 行だけ出す
        private static int s_streakStartFrame;      // 診断: 今の押し当てが始まったフレーム

        private static string Flags(Core c)
        {
            return "ivent_sw=" + c.ivent_sw + " mess=" + c.mess_flag + " stop=" + c.ivent_stop_flag + " exevt=" + c.exevt_flg
                + " battle=" + c.battle_flg + " tono=" + c.tono_flg + " exdemo=" + c.exdemo_flg;
        }

        private static string Keys(Core c)
        {
            return "joy0=0x" + c.joy_data[0].ToString("X") + " Sys.key0=0x" + Sys.key[0].ToString("X") + " kiw=0x" + c.key_is_what.ToString("X");
        }

        // 診断: 押し当てがしきい値に達しているのに発動しなかった理由 (1 回の押し当てにつき 1 行)
        private static void LogBlocked(Core c, string why)
        {
            if (!Plugin.DiagOn || s_blockLogged) return;
            s_blockLogged = true;
            Plugin.Log.LogInfo("[Bump] blocked(" + why + ") frame=" + s_frame + " person=" + s_lastBumpPerson + " streak=" + s_streak
                + " since=" + s_streakStartFrame + " lastBump=" + s_lastBumpFrame + " latched=" + s_latched + " busyUntil=" + s_busyUntil
                + " held=" + DialogueAdvanceHoldPatch.HeldSinceBusy + " " + Keys(c));
        }

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Field", "TalkOnBump", true,
                "NPC に十字キーを押し当て続けると決定を押さなくても話しかける (決定で話す人が対象。宝箱は対象外)。決定で話すのも今までどおり");
            Frames = config.Bind("Field", "TalkOnBumpFrames", 8,
                new ConfigDescription("話しかけるまでに押し当て続けるフレーム数 (1 フレーム ≒ 33ms。既定 8 ≒ 0.27 秒)。倍速中も実時間基準", new AcceptableValueRange<int>(1, 30)));
            CooldownFrames = config.Bind("Field", "TalkOnBumpCooldownFrames", 10,
                new ConfigDescription("会話・イベントが終わってから押し当てを数え始めるまでの待ちフレーム数 (会話送りの連打の最後が NPC に向いていても拾わないため。既定 10 ≒ 0.33 秒)", new AcceptableValueRange<int>(0, 60)));
        }

        public static bool IsEnabled { get { return Enabled != null && Enabled.Value; } }

        public static void CycleFrames(ConfigEntry<int> e, int delta, int min, int max)
        {
            if (e == null) return;
            int v = e.Value + delta;
            if (v < min) v = min;
            if (v > max) v = max;
            e.Value = v;
        }

        /// <summary>人のいるマスへ進めなかった (Core.move_check_routine の Postfix)。</summary>
        public static void OnBump(Core c, int person)
        {
            if (!IsEnabled || c.people == null || person < 0 || person >= c.people.Length) return;
            if (c.people[person] == null || c.people[person].people_ivent_type != 0) return;
            if ((c.joy_data[0] & DIR_BITS) == 0) return; // 自動歩行 (イベント中など) は数えない
            // 歩いている NPC は 1 歩の始まりでマスの占有が次のマスへ移るので、後ろから追うと
            // 「止められる → NPC が進む → 自分も進む → また止められる」と数フレームずつしか続かない。
            // そこで十字キーを離さない限り同じ人への押し当てを累積する (途切れが 1 秒を超えたら数え直し)
            if (s_lastBumpPerson == person && s_lastBumpFrame >= s_frame - 30 * GameSpeed.FrameScale()) s_streak++;
            else
            {
                s_streak = 1;
                s_blockLogged = false;
                s_streakStartFrame = s_frame;
                if (Plugin.DiagOn)
                    Plugin.Log.LogInfo("[Bump] start frame=" + s_frame + " person=" + person + " latched=" + s_latched + " busyUntil=" + s_busyUntil
                        + " held=" + DialogueAdvanceHoldPatch.HeldSinceBusy + " " + Keys(c) + " " + Flags(c));
            }
            s_lastBumpFrame = s_frame;
            s_lastBumpPerson = person;
        }

        /// <summary>search_ivent の前。決定ビットを足すなら元の key_is_what を返し applied = true。</summary>
        public static int BeforeSearch(Core c, out bool applied)
        {
            applied = false;
            s_frame++;
            if (!IsEnabled) return 0;
            int scale = GameSpeed.FrameScale();
            bool busy = (c.ivent_sw | c.mess_flag | c.ivent_stop_flag | c.exevt_flg | c.battle_flg | c.tono_flg | c.exdemo_flg) != 0;
            if (busy)
            {
                if (!s_latched && Plugin.DiagOn)
                    Plugin.Log.LogInfo("[Bump] latch frame=" + s_frame + " " + Flags(c) + " " + Keys(c));
                s_latched = true;
                s_streak = 0;
                s_busyUntil = s_frame + (CooldownFrames != null ? CooldownFrames.Value : 10) * scale;
                s_stepAtBusy = DialogueAdvanceStepPatch.StepSerial;
                return 0;
            }
            // 会話の後に自分が 1 マス歩き出したらラッチを解く (再開の誤爆では 1 歩も動けないので区別できる)。
            // 歩き去る NPC を押しっぱなしで追う時は「1 歩進む → 阻まれる」の繰り返しなので、離さなくても次の押し当てで話せる
            // クールダウン (TalkOnBumpCooldownFrames) もここで終わらせる。1 マス歩けたのは NPC が動いた証拠なので、閉じた押しのままの
            // 再開 (誤爆) とは区別でき、歩き去る NPC を追う 1 歩目の押し当てですぐ話せる (以前は 1 歩目では話さずクールダウン明けを
            // 待っていたが、ユーザーの決定でやめた 2026-10-25)
            if (s_latched && DialogueAdvanceStepPatch.StepSerial != s_stepAtBusy)
            {
                if (Plugin.DiagOn)
                    Plugin.Log.LogInfo("[Bump] unlatch(stepped) frame=" + s_frame + " busyUntil=" + s_busyUntil + " " + Keys(c));
                s_latched = false;
                s_busyUntil = s_frame;
            }
            // 離したかは Sys.key[0] (今のパッドの状態) で見る。会話の終わり (ok_mes_finish2) が joy_data を 0 にするので、
            // joy_data[0] だと次のフレームで「離した」と誤判定してラッチが外れる (クールダウンが 0 だと押しっぱなしで再発動する)
            if (((c.joy_data[0] | Sys.key[0]) & DIR_BITS) == 0)
            {
                if (s_latched && Plugin.DiagOn)
                    Plugin.Log.LogInfo("[Bump] unlatch frame=" + s_frame + " " + Keys(c));
                s_latched = false;
                s_streak = 0;
                return 0;
            }
            bool ready = s_streak >= (Frames != null ? Frames.Value : 8) * scale;
            if (s_latched) { if (ready) LogBlocked(c, "latched"); return 0; }
            if (s_frame < s_busyUntil) { if (ready) LogBlocked(c, "cooldown"); s_streak = 0; return 0; }
            if (s_lastBumpFrame < s_frame - 2) return 0; // 今この瞬間は止められていない (正面に人がいない可能性が高い)。累積は残す
            if (!ready) return 0;
            if ((c.key_is_what & OK_BITS) != 0) { LogBlocked(c, "realOK"); return 0; } // 本物の決定はそのまま
            int orig = c.key_is_what;
            c.key_is_what = orig | OK_BIT;
            applied = true;
            return orig;
        }

        public static void AfterSearch(Core c, int orig)
        {
            c.key_is_what = orig;
            // 正面に人がいなかった (NPC が歩き出した直後など) なら累積を残して次の押し当てで再試行する
            if (c.ivent_sw == 0) LogBlocked(c, "noIvent dir=" + c.man_direction);
            if (c.ivent_sw != 0)
            {
                if (Plugin.DiagOn)
                    Plugin.Log.LogInfo("[Bump] talk person=" + s_lastBumpPerson + " dir=" + c.man_direction + " ivent=" + c.ivent_no[0] + "," + c.ivent_no[1] + " streak/frames=" + s_streak + "/" + (Frames != null ? Frames.Value : 8));
                s_streak = 0;
                s_latched = true;
            }
        }
    }

    // 自分の 1 歩の判定。work_q[1] = 目的のマスにいる人 (番号+1、冒頭で本体が入れる)、work_p[0] = 1 なら進めなかった。
    // cant_move_sa_by_peo の Postfix だと、人がいるマスが自分の入れない地形 (店のカウンターの奥など) の時に
    // 本体が地形の方で止めて (cant_move_sa_by_ori_id → cant_move_sa) 人にぶつかった扱いにならず、数えられなかった
    [HarmonyPatch(typeof(Core), "move_check_routine")]
    internal static class TalkOnBumpBlockedPatch
    {
        private static void Postfix(Core __instance)
        {
            int no = __instance.work_q[1];
            if (no != 0 && __instance.work_p[0] != 0) TalkOnBump.OnBump(__instance, no - 1);
        }
    }

    [HarmonyPatch(typeof(Core), "search_ivent")]
    internal static class TalkOnBumpSearchPatch
    {
        private struct State { public int orig; public bool applied; }

        private static void Prefix(Core __instance, out State __state)
        {
            __state = default(State);
            __state.orig = TalkOnBump.BeforeSearch(__instance, out __state.applied);
        }

        private static void Postfix(Core __instance, State __state)
        {
            if (__state.applied) TalkOnBump.AfterSearch(__instance, __state.orig);
        }
    }
}
