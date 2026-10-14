using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using SEAD;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// ゲーム内音量 (マスター / BGM / SE)。本体には音量設定が無い。
    ///
    /// 音は SEAD (ネイティブ音源) で鳴り、CSoundList.Update が再生中の全音に毎フレーム CSound.SetVolume(1f) を呼ぶ。
    /// そこを Prefix で差し替え、設定の倍率を掛けた値を SEAD に流す (元の処理は 1 秒フェード付きなので
    /// 短い効果音に効かない → フェード 0 で直接 seadSoundSetVolume を呼び、元処理は止める)。
    /// 再生開始直後の 1 フレームも鳴り始めは 1.0 になるので、CSound.Play の Postfix でも同じ値を即時適用する。
    /// BGM か SE かは CSound のファイルパス (CSoundData.BGMDirectory で始まるか) で見分ける。
    /// </summary>
    internal static class AudioVolume
    {
        public static ConfigEntry<int> Master, Bgm, Se;

        private static readonly FieldInfo s_fileName = AccessTools.Field(typeof(CSound), "m_FileName");
        private static readonly FieldInfo s_soundId = AccessTools.Field(typeof(CSound), "m_SoundID");

        public static void Bind(ConfigFile config)
        {
            Master = config.Bind("Audio", "MasterVolume", 30, new ConfigDescription("全体の音量 (%)。本体は音量設定が無く起動時から最大なので既定 30", new AcceptableValueRange<int>(0, 100)));
            Bgm = config.Bind("Audio", "BgmVolume", 100, new ConfigDescription("BGM の音量 (%)", new AcceptableValueRange<int>(0, 100)));
            Se = config.Bind("Audio", "SeVolume", 100, new ConfigDescription("効果音の音量 (%)", new AcceptableValueRange<int>(0, 100)));
        }

        /// <summary>この CSound に適用する倍率 (0〜1)。</summary>
        public static float FactorFor(CSound sound)
        {
            if (Master == null) return 1f;
            string name = s_fileName != null ? s_fileName.GetValue(sound) as string : null;
            bool isBgm = name != null && name.StartsWith("Sound/BGM/") /* CSoundData.BGMDirectory (internal クラスなので直接参照できない) */;
            int cat = isBgm ? Bgm.Value : Se.Value;
            return Mathf.Clamp01(Master.Value / 100f) * Mathf.Clamp01(cat / 100f);
        }

        public static void ApplyNow(CSound sound, float baseVolume)
        {
            if (s_soundId == null) return;
            ulong id = (ulong)s_soundId.GetValue(sound);
            if (id == 0uL) return;
            API.seadSoundSetVolume(id, baseVolume * FactorFor(sound), 0f);
        }
    }

    [HarmonyPatch(typeof(CSound), nameof(CSound.SetVolume))]
    internal static class VolumeSetPatch
    {
        // 元処理 (1 秒フェード) を止めて即時反映にするため Prefix で置き換える
        private static bool Prefix(CSound __instance, float Volume)
        {
            AudioVolume.ApplyNow(__instance, Volume);
            return false;
        }
    }

    [HarmonyPatch(typeof(CSound), nameof(CSound.Play))]
    internal static class VolumePlayPatch
    {
        private static void Postfix(CSound __instance)
        {
            AudioVolume.ApplyNow(__instance, 1f);
        }
    }
}
