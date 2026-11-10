using UnityEngine;

namespace FiresCore.UI.Shell.Kit
{
    /// <summary>
    /// The IMGUI twins of KitControls / the config window's uGUI controls, same tokens
    /// (FiresTokens SwitchWidth/Height/Knob, SliderTrack/Knob, Gold/Text/Well/Hairline/TextFaint) and same geometry as
    /// Widgets.DiamondToggle and Widgets.Slider. Rounded shapes are GUI.DrawTexture's native rounded rects (no sprites). Add-only API.
    /// Call from OnGUI only.
    /// </summary>
    public static class FiresImguiWidgets
    {
        private const float ValueW = 52f;       // Widgets.Slider's value column
        private const float LabelSize = 14f;    // DiamondToggle label
        private static GUIStyle s_label, s_value;
        private static int s_dragId;            // the slider control being dragged

        private static void Styles()
        {
            if (s_label != null) return;
            s_label = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = (int)LabelSize, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Overflow };
            s_value = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = (int)(FiresTokens.ValueSize - 1f), alignment = TextAnchor.MiddleRight, wordWrap = false, clipping = TextClipping.Overflow };
        }

        private static void Round(Rect r, Color c, float radius, float border = 0f)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, border, radius);
        }

        /// <summary>Pill switch + ON/OFF label, vertically centred in r (left-aligned). Returns the new value (a click flips it).</summary>
        public static bool PillSwitch(Rect r, bool on, string onLabel = "ON", string offLabel = "OFF")
        {
            Styles();
            float sw = FiresTokens.SwitchWidth, sh = FiresTokens.SwitchHeight, kd = FiresTokens.SwitchKnob;
            var track = new Rect(r.x, r.y + (r.height - sh) * 0.5f, sw, sh);
            Round(track, on ? FiresTokens.WithAlpha(FiresTokens.Gold, 0.25f) : FiresTokens.Well, sh * 0.5f);
            Round(track, on ? FiresTokens.Gold : FiresTokens.TextFaint, sh * 0.5f, 1f);
            float pad = (sh - kd) * 0.5f;
            Round(new Rect(on ? track.xMax - pad - kd : track.x + pad, track.y + pad, kd, kd), on ? FiresTokens.Gold : FiresTokens.TextFaint, kd * 0.5f);
            var old = GUI.contentColor;
            s_label.normal.textColor = on ? FiresTokens.Text : FiresTokens.TextMuted;
            GUI.Label(new Rect(track.xMax + FiresTokens.S8, r.y, Mathf.Max(0f, r.width - sw - FiresTokens.S8), r.height), on ? onLabel : offLabel, s_label);
            GUI.contentColor = old;
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) { GUI.changed = true; return !on; }
            return on;
        }

        public static bool PillSwitchLayout(bool on, params GUILayoutOption[] options)
        {
            var r = GUILayoutUtility.GetRect(FiresTokens.SwitchWidth + FiresTokens.S8 + 40f, 26f, options);
            return PillSwitch(r, on);
        }

        /// <summary>Slim slider in r: Hairline pill track, Gold fill to the round Text knob, valueText right-aligned in the last 52 px.
        /// Click or drag on the track. Returns the new value (min..max).</summary>
        public static float SlimSlider(Rect r, float v, float min, float max, string valueText)
        {
            Styles();
            int id = GUIUtility.GetControlID(FocusType.Passive, r);
            float trackW = Mathf.Max(1f, r.width - ValueW - FiresTokens.S12);
            float th = FiresTokens.SliderTrack, k = FiresTokens.SliderKnob, cy = r.y + r.height * 0.5f;
            var trackHit = new Rect(r.x, r.y, trackW, r.height);
            var e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && trackHit.Contains(e.mousePosition)) { GUIUtility.hotControl = id; s_dragId = id; v = FromX(e.mousePosition.x, r.x, trackW, min, max); GUI.changed = true; e.Use(); }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id) { v = FromX(e.mousePosition.x, r.x, trackW, min, max); GUI.changed = true; e.Use(); }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; s_dragId = 0; e.Use(); }
                    break;
            }
            float t = max > min ? Mathf.Clamp01((v - min) / (max - min)) : 0f;
            Round(new Rect(r.x, cy - th * 0.5f, trackW, th), FiresTokens.Hairline, th * 0.5f);
            Round(new Rect(r.x, cy - th * 0.5f, Mathf.Max(th, trackW * t), th), FiresTokens.Gold, th * 0.5f);
            Round(new Rect(r.x + Mathf.Clamp(trackW * t - k * 0.5f, 0f, trackW - k), cy - k * 0.5f, k, k), FiresTokens.Text, k * 0.5f);
            s_value.normal.textColor = FiresTokens.Text;
            GUI.Label(new Rect(r.xMax - ValueW, r.y, ValueW, r.height), valueText ?? "", s_value);
            return v;
        }

        public static float SlimSliderLayout(float v, float min, float max, string valueText, float width)
        {
            var r = GUILayoutUtility.GetRect(width, 26f, GUILayout.Width(width));
            return SlimSlider(r, v, min, max, valueText);
        }

        private static float FromX(float mx, float x0, float trackW, float min, float max) => min + (max - min) * Mathf.Clamp01((mx - x0) / trackW);
    }
}
