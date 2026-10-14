using BepInEx.Configuration;
using UnityEngine;

namespace RS2QoL.Patches
{
    /// <summary>
    /// マウスカーソルの自動非表示。本体はカーソルを一切触らない (常に表示) ので、
    /// マウスを動かすかクリックした時だけ出し、一定時間触らなければ消す。
    /// 起動直後の 1 フレームは Input.mousePosition が確定していないので、位置の記録だけして動きとは見なさない。
    /// </summary>
    internal static class MouseCursor
    {
        public static ConfigEntry<bool> AutoHide;
        public static ConfigEntry<float> HideAfter;

        private static Vector3 s_lastPos;
        private static bool s_init;
        private static float s_lastActivity = -1000f;
        private static bool s_visible = true;

        public static void Bind(ConfigFile config)
        {
            AutoHide = config.Bind("Window", "HideCursor", true,
                "マウスを動かすかクリックした時だけマウスカーソルを出し、しばらく触らなければ消す");
            HideAfter = config.Bind("Window", "HideCursorSeconds", 2f,
                new ConfigDescription("マウスを触らなくなってからカーソルを消すまでの秒数", new AcceptableValueRange<float>(0f, 30f)));
        }

        /// <summary>Plugin.Update から毎 Unity フレーム呼ぶ。</summary>
        public static void Update()
        {
            if (AutoHide == null) return;
            if (!AutoHide.Value)
            {
                if (!s_visible) SetVisible(true);
                return;
            }
            Vector3 pos = Input.mousePosition;
            if (!s_init)
            {
                s_init = true;
                s_lastPos = pos;
            }
            bool active = pos != s_lastPos || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
            s_lastPos = pos;
            float now = Time.realtimeSinceStartup;
            if (active) s_lastActivity = now;
            bool want = now - s_lastActivity <= HideAfter.Value;   // 0 秒でも動かした瞬間は出す
            if (want != s_visible) SetVisible(want);
        }

        private static void SetVisible(bool v)
        {
            s_visible = v;
            Cursor.visible = v;
        }
    }
}
