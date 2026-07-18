using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Shared IMGUI style set for Fires windows (the F8 config manager, FiresDebugginTools' inspector):
    /// CineHUD's window type with Configuration-Manager polish — rounded corners everywhere in the Fires
    /// brown/gold palette. Rounding comes from runtime-generated rounded-rect textures (signed-distance
    /// anti-aliased coverage) 9-sliced via GUIStyle.border. Call <see cref="Ensure"/> at the top of OnGUI;
    /// window background styles are cached per opacity so windows with different opacities never fight.
    /// </summary>
    public static class FiresRoundedSkin
    {
        public static GUIStyle Window, SectionBar, Button, ButtonSmall, Field, Label, ValueText, Title, Hint;
        public static GUIStyle Slider, SliderThumb;
        public static GUIStyle NavItem, NavSel, NavSub, NavSubOn, TextInput, Desc, Swatch, Tip;

        private static bool _built;
        private static readonly Dictionary<int, GUIStyle> WindowByAlpha = new Dictionary<int, GUIStyle>();

        private static readonly Color PanelBrown  = new Color(0.09f, 0.075f, 0.055f);
        private static readonly Color BarBrown    = new Color(0.30f, 0.22f, 0.10f, 0.95f);
        private static readonly Color BarHover    = new Color(0.40f, 0.29f, 0.13f, 0.95f);
        private static readonly Color BarSoft     = new Color(0.30f, 0.22f, 0.10f, 0.6f);
        private static readonly Color BtnBrown    = new Color(0.34f, 0.23f, 0.10f, 0.95f);
        private static readonly Color BtnHover    = new Color(0.47f, 0.32f, 0.14f, 0.95f);
        private static readonly Color BtnActive   = new Color(0.24f, 0.16f, 0.07f, 0.95f);
        private static readonly Color FieldFill   = new Color(0.05f, 0.045f, 0.035f, 0.88f);
        private static readonly Color FieldBorder = new Color(0.62f, 0.42f, 0.16f, 0.9f);
        private static readonly Color TrackFill   = new Color(0.05f, 0.045f, 0.035f, 0.9f);
        private static readonly Color ThumbFill   = new Color(0.85f, 0.62f, 0.26f, 1f);
        private static readonly Color TextGold    = new Color(1f, 0.85f, 0.5f);
        private static readonly Color TextLight   = new Color(0.88f, 0.79f, 0.6f);
        private static readonly Color TextDim     = new Color(0.8f, 0.72f, 0.56f, 1f);
        private static readonly Color TextInputFg = new Color(0.97f, 0.94f, 0.85f, 1f);

        /// <summary>Build styles once and select the window-background style for this opacity (cached per alpha).</summary>
        public static void Ensure(float windowAlpha)
        {
            if (!_built)
            {
                _built = true;

                var bar = Rounded(24, 24, 7, BarBrown);
                var barHover = Rounded(24, 24, 7, BarHover);
                var barSoft = Rounded(24, 24, 7, BarSoft);
                var btn = Rounded(24, 24, 7, BtnBrown);
                var btnHover = Rounded(24, 24, 7, BtnHover);
                var btnActive = Rounded(24, 24, 7, BtnActive);
                var field = Rounded(24, 24, 7, FieldFill, FieldBorder, 1);
                var track = Rounded(12, 12, 5, TrackFill, FieldBorder, 1);
                var thumb = Rounded(16, 16, 8, ThumbFill);
                var swatch = Rounded(20, 20, 6, Color.white);

                SectionBar = new GUIStyle(GUI.skin.button)
                {
                    border = new RectOffset(8, 8, 8, 8),
                    padding = new RectOffset(10, 10, 4, 4),
                    alignment = TextAnchor.MiddleLeft,
                    richText = true,
                    fontSize = 13,
                };
                SectionBar.normal.background = bar;
                SectionBar.hover.background = barHover;
                SectionBar.active.background = barHover;
                SectionBar.normal.textColor = SectionBar.hover.textColor = SectionBar.active.textColor = TextGold;

                Button = new GUIStyle(SectionBar)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 12,
                    padding = new RectOffset(8, 8, 3, 3),
                };
                Button.normal.background = btn;
                Button.hover.background = btnHover;
                Button.active.background = btnActive;

                ButtonSmall = new GUIStyle(Button) { fontSize = 11, padding = new RectOffset(6, 6, 2, 2) };

                Field = new GUIStyle(GUI.skin.label)
                {
                    border = new RectOffset(8, 8, 8, 8),
                    padding = new RectOffset(8, 8, 2, 2),
                    alignment = TextAnchor.MiddleLeft,
                    richText = true,
                    fontSize = 12,
                };
                Field.normal.background = field;
                Field.normal.textColor = TextGold;

                TextInput = new GUIStyle(GUI.skin.textField)
                {
                    border = new RectOffset(8, 8, 8, 8),
                    padding = new RectOffset(8, 8, 3, 3),
                    fontSize = 12,
                };
                TextInput.normal.background = TextInput.hover.background = field;
                TextInput.focused.background = TextInput.active.background = field;
                TextInput.normal.textColor = TextInput.hover.textColor = TextInputFg;
                TextInput.focused.textColor = TextInput.active.textColor = TextInputFg;

                Label = new GUIStyle(GUI.skin.label)
                {
                    richText = true,
                    fontSize = 12,
                    alignment = TextAnchor.MiddleLeft,
                    padding = new RectOffset(2, 2, 2, 2),
                };
                Label.normal.textColor = TextLight;

                ValueText = new GUIStyle(Label);
                ValueText.normal.textColor = TextGold;

                Title = new GUIStyle(Label) { fontSize = 14, fontStyle = FontStyle.Bold };
                Title.normal.textColor = TextGold;

                Hint = new GUIStyle(Label) { fontSize = 10, alignment = TextAnchor.MiddleCenter };
                Hint.normal.textColor = TextDim;

                Desc = new GUIStyle(Label) { fontSize = 11, wordWrap = true };
                Desc.normal.textColor = TextDim;

                NavItem = new GUIStyle(GUI.skin.button)
                {
                    border = new RectOffset(8, 8, 8, 8),
                    padding = new RectOffset(8, 8, 4, 4),
                    alignment = TextAnchor.MiddleLeft,
                    richText = true,
                    fontSize = 12,
                };
                NavItem.normal.background = null;
                NavItem.hover.background = barSoft;
                NavItem.active.background = barHover;
                NavItem.normal.textColor = NavItem.hover.textColor = NavItem.active.textColor = TextLight;

                NavSel = new GUIStyle(NavItem) { fontStyle = FontStyle.Bold };
                NavSel.normal.background = bar;
                NavSel.hover.background = barHover;
                NavSel.normal.textColor = NavSel.hover.textColor = NavSel.active.textColor = TextGold;

                NavSub = new GUIStyle(NavItem)
                {
                    fontSize = 11,
                    padding = new RectOffset(22, 8, 3, 3),
                };
                NavSub.normal.textColor = TextDim;

                NavSubOn = new GUIStyle(NavSub) { fontStyle = FontStyle.Bold };
                NavSubOn.normal.background = barSoft;
                NavSubOn.normal.textColor = NavSubOn.hover.textColor = TextGold;

                Slider = new GUIStyle(GUI.skin.horizontalSlider)
                {
                    border = new RectOffset(5, 5, 5, 5),
                    fixedHeight = 12f,
                };
                Slider.normal.background = track;

                SliderThumb = new GUIStyle(GUI.skin.horizontalSliderThumb)
                {
                    fixedWidth = 14f,
                    fixedHeight = 14f,
                };
                SliderThumb.normal.background = thumb;
                SliderThumb.hover.background = thumb;
                SliderThumb.active.background = thumb;

                Swatch = new GUIStyle(GUIStyle.none);
                Swatch.normal.background = swatch;

                var tipBg = Rounded(24, 24, 6, new Color(0.05f, 0.045f, 0.035f, 0.98f), FieldBorder, 1);
                Tip = new GUIStyle(GUI.skin.label)
                {
                    border = new RectOffset(8, 8, 8, 8),
                    padding = new RectOffset(8, 8, 5, 6),
                    richText = true,
                    wordWrap = true,
                    fontSize = 11,
                    alignment = TextAnchor.UpperLeft,
                };
                Tip.normal.background = tipBg;
                Tip.normal.textColor = TextLight;
            }

            int key = Mathf.RoundToInt(Mathf.Clamp01(windowAlpha) * 100f);
            if (!WindowByAlpha.TryGetValue(key, out var win))
            {
                var c = new Color(PanelBrown.r, PanelBrown.g, PanelBrown.b, key / 100f);
                win = new GUIStyle(GUI.skin.window)
                {
                    border = new RectOffset(14, 14, 14, 14),
                    padding = new RectOffset(12, 12, 10, 12),
                };
                var tex = Rounded(40, 40, 12, c);
                win.normal.background = tex;
                win.onNormal.background = tex;
                WindowByAlpha[key] = win;
            }
            Window = win;
        }

        // ── IMGUI hover tooltip ───────────────────────────────────────────────────────────────────────────
        // Immediate-mode has no pointer-enter events, so a hovered hint is RECORDED while controls draw (during
        // the Repaint pass, when GetLastRect is valid) and DRAWN once after the window. Respects the same master
        // switch as the uGUI FiresHoverTip so one toggle governs all Fires hint text.
        private static string _pendingTip;

        /// <summary>Call at the top of the window body (during Repaint) so a hint that's no longer hovered clears.</summary>
        public static void ResetTooltip()
        {
            if (Event.current != null && Event.current.type == EventType.Repaint) _pendingTip = null;
        }

        /// <summary>Call right after drawing a control/row: if the cursor is over its last rect, records the hint.
        /// GetLastRect and mousePosition are both in the CURRENT local space (window/scroll), so Contains is valid
        /// here — but we deliberately do NOT record the position; the draw uses the live top-level mouse instead.</summary>
        public static void MarkHint(string hint)
        {
            if (string.IsNullOrEmpty(hint) || !FiresHoverTip.Enabled) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition)) _pendingTip = hint;
        }

        /// <summary>Draw the recorded hint near the cursor. Call once per OnGUI AFTER GUILayout.Window returns —
        /// at that point mousePosition is back in top-level space (matching the active GUI.matrix), NOT the
        /// scroll-local space MarkHint may have seen (recording that position put the tip way off-screen). Clamps
        /// to the LOGICAL screen (physical / matrix scale) so it stays on-screen under a scaled window.</summary>
        public static void DrawPendingTooltip()
        {
            if (string.IsNullOrEmpty(_pendingTip) || Tip == null) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var mouse = Event.current.mousePosition;
            var content = new GUIContent(_pendingTip);
            float w = Mathf.Min(280f, Tip.CalcSize(content).x + 2f);
            float h = Tip.CalcHeight(content, w);
            float sx = GUI.matrix.m00 != 0f ? GUI.matrix.m00 : 1f;
            float sy = GUI.matrix.m11 != 0f ? GUI.matrix.m11 : 1f;
            float logW = Screen.width / sx, logH = Screen.height / sy;
            float x = Mathf.Clamp(mouse.x + 14f, 4f, logW - w - 4f);
            float y = Mathf.Clamp(mouse.y + 16f, 4f, logH - h - 4f);
            GUI.Label(new Rect(x, y, w, h), content, Tip);
        }

        // 9-sliceable rounded-rect SPRITES for uGUI Images (the context menu etc.) in the same visual
        // language as the IMGUI styles above. Cached per (radius, colors) — sprites and their textures
        // survive scene loads (HideAndDontSave).
        private static readonly Dictionary<string, Sprite> SpriteCache = new Dictionary<string, Sprite>();

        public static Sprite RoundedSprite(int radius, Color fill, Color? border = null, int borderPx = 0)
        {
            string key = $"{radius}_{fill}_{(border.HasValue ? border.Value.ToString() : "-")}_{borderPx}";
            if (SpriteCache.TryGetValue(key, out var cached) && cached != null) return cached;
            int size = radius * 2 + 10;
            var tex = Rounded(size, size, radius, fill, border, borderPx);
            float slice = radius + 2f;
            var sprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(slice, slice, slice, slice));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            SpriteCache[key] = sprite;
            return sprite;
        }

        // Anti-aliased rounded rect via signed-distance coverage; optional border ring baked in.
        private static Texture2D Rounded(int w, int h, int radius, Color fill, Color? border = null, int borderPx = 0)
        {
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float outer = Coverage(x + 0.5f, y + 0.5f, 0f, w, h, radius);
                    Color c = fill;
                    if (border.HasValue && borderPx > 0)
                    {
                        float inner = Coverage(x + 0.5f, y + 0.5f, borderPx, w, h, Mathf.Max(1, radius - borderPx));
                        c = Color.Lerp(border.Value, fill, inner);
                    }
                    tex.SetPixel(x, y, new Color(c.r, c.g, c.b, c.a * outer));
                }
            }
            tex.Apply();
            return tex;
        }

        private static float Coverage(float px, float py, float inset, float w, float h, float radius)
        {
            float halfW = w * 0.5f - inset, halfH = h * 0.5f - inset;
            float qx = Mathf.Abs(px - w * 0.5f) - (halfW - radius);
            float qy = Mathf.Abs(py - h * 0.5f) - (halfH - radius);
            float dx = Mathf.Max(qx, 0f), dy = Mathf.Max(qy, 0f);
            float dist = Mathf.Sqrt(dx * dx + dy * dy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
            return Mathf.Clamp01(0.5f - dist);
        }
    }
}
