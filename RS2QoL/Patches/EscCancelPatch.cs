using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// Esc をキーボードの機能 (既定 = キャンセルのみ) に使えるようにする。
    ///
    /// 本体は Main.Disp.run の case 2 (ゲーム中) で `else if (Input.GetKeyUp(KeyCode.Escape)) { m_isEndDialog = true; m_endgame.Initialize(); }`
    /// と Esc を直接読んで終了ダイアログを開く。Input.GetKeyUp 自体 (ネイティブ) は Harmony で差し替えられないが、run の IL の中の
    /// その呼び出し 1 か所を Transpiler で VanillaEscUp に付け替えることはできる。VanillaEscUp は Esc がどの機能にも割り当てられて
    /// いない時だけ本体どおり true を返し、割り当てられている間は false (= 本体は Esc を見なかったことになる)。
    /// Esc で立つキャンセルのビットは KeyMap.Bits がゲームに渡す。終了は Quit 機能 (既定 R3) か設定画面「その他」から。
    /// 60fps 表示の複製フレームで見た Esc のキーアップは Interp60RunStartPatch が同じ条件で捨てる。
    /// 終了ダイアログが開いている間の Esc (EndGameSteam.DialogInput の GetKeyUp(Escape) で閉じる) はそのまま (= キャンセルと同じ動き)。
    /// 設定画面が使った Esc の分は EscGuardPatch のまま (Esc が割り当てられていない時だけ意味を持つ)。
    /// </summary>
    [HarmonyPatch(typeof(Main.Disp), nameof(Main.Disp.run))]
    internal static class EscCancelPatch
    {
        private static readonly MethodInfo s_getKeyUp = AccessTools.Method(typeof(Input), nameof(Input.GetKeyUp), new[] { typeof(KeyCode) });
        private static readonly MethodInfo s_gate = AccessTools.Method(typeof(EscCancelPatch), nameof(VanillaEscUp));
        private static bool s_logged, s_loggedPatched;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 1; i < list.Count; i++)
            {
                if (!list[i].Calls(s_getKeyUp) || !IsLdcEscape(list[i - 1])) continue;
                list[i].operand = s_gate;   // call (static → static) なので opcode はそのまま
                replaced++;
            }
            // 同じメソッドに掛かる他のパッチ (Interp60RunStartPatch / EscGuardPatch) が適用されるたびに Transpiler も走り直すので、ログは 1 回だけ
            if (replaced != 1) Plugin.Log.LogWarning("[EscCancel] NOT patched: Main.Disp.run の Input.GetKeyUp(Escape) が " + replaced + " 箇所 (1 のはず)。Esc は本体どおり終了ダイアログ");
            else if (!s_loggedPatched) { s_loggedPatched = true; Plugin.Log.LogInfo("[EscCancel] patched: Main.Disp.run (Input.GetKeyUp(Escape) → VanillaEscUp。Esc を割り当てている間は本体の終了ダイアログを開かない)"); }
            return list;
        }

        private static bool IsLdcEscape(CodeInstruction c)
        {
            // KeyCode.Escape = 27 → ldc.i4.s 27 (operand は sbyte)。念のため ldc.i4 も見る
            if (c.opcode != OpCodes.Ldc_I4_S && c.opcode != OpCodes.Ldc_I4) return false;
            return Convert.ToInt32(c.operand) == (int)KeyCode.Escape;
        }

        /// <summary>本体の Input.GetKeyUp(Escape) の代わり。Esc が機能に割り当てられている間は false。</summary>
        public static bool VanillaEscUp(KeyCode k)
        {
            bool up = Input.GetKeyUp(k);
            if (!up) return false;
            if (!KeyMap.EscMapped) return true;
            if (!s_logged) { s_logged = true; Plugin.Log.LogInfo("[EscCancel] applied: Esc のキーアップを本体に見せない (Esc = " + KeyMap.Label(KeyCode.Escape) + " 割り当て中)"); }
            return false;
        }
    }
}
