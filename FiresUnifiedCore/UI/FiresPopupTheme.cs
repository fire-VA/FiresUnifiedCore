using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Mechanical FDT-style reskin for runtime popup panels (the ContextMenuView language: dark
    /// rounded shell, bar-brown headers, gold/cream text). One <see cref="Reskin"/> call after a
    /// panel builds converts a parchment window in place — works on hand-built hierarchies and on
    /// instantiated baked prefabs alike, so panels keep their layout/wiring untouched.
    ///
    /// Deliberately NOT applied globally: book pages, dialogue windows and other player-facing
    /// parchment UIs keep their look; only admin runtime popups opt in.
    /// </summary>
    public static class FiresPopupTheme
    {
        // ContextMenuView / FiresRoundedSkin palette.
        public static readonly Color PanelBg    = new Color(0.09f, 0.075f, 0.055f, 0.95f);
        public static readonly Color PanelEdge  = new Color(0.62f, 0.42f, 0.16f, 0.9f);
        public static readonly Color HeaderBg   = new Color(0.30f, 0.22f, 0.10f, 0.95f);
        public static readonly Color BtnNormal  = new Color(0.34f, 0.23f, 0.10f, 0.95f);
        public static readonly Color BtnHover   = new Color(0.47f, 0.32f, 0.14f, 0.95f);
        public static readonly Color BtnPressed = new Color(0.24f, 0.16f, 0.07f, 0.95f);
        public static readonly Color FieldFill  = new Color(0.05f, 0.045f, 0.035f, 0.88f);
        public static readonly Color TrackFill  = new Color(0.05f, 0.045f, 0.035f, 0.9f);
        public static readonly Color ThumbGold  = new Color(0.85f, 0.62f, 0.26f, 1f);
        public static readonly Color RowBrown   = new Color(0.34f, 0.23f, 0.10f, 0.95f);
        public static readonly Color ListBg     = new Color(0.12f, 0.10f, 0.075f, 0.99f);
        public static readonly Color TextGold   = new Color(1f, 0.85f, 0.5f, 1f);
        public static readonly Color TextLight  = new Color(0.88f, 0.79f, 0.6f, 1f);
        public static readonly Color TextDim    = new Color(0.8f, 0.72f, 0.56f, 0.7f);
        public static readonly Color TextCream  = new Color(0.97f, 0.94f, 0.85f, 1f);

        /// <summary>
        /// Restyles <paramref name="panelRoot"/> and everything under it. Idempotent — safe to call
        /// again after a panel rebuilds its rows. Images named *Swatch*/*Icon* are preserved (they
        /// display data, not chrome).
        /// </summary>
        public static void Reskin(GameObject panelRoot)
        {
            if (panelRoot == null) return;
            try
            {
                StylePanelShell(panelRoot);

                foreach (var button in panelRoot.GetComponentsInChildren<Button>(true)) StyleButton(button);
                foreach (var input in panelRoot.GetComponentsInChildren<TMP_InputField>(true)) StyleInput(input);
                foreach (var dropdown in panelRoot.GetComponentsInChildren<TMP_Dropdown>(true)) StyleDropdown(dropdown);
                foreach (var slider in panelRoot.GetComponentsInChildren<Slider>(true)) StyleSlider(slider);
                foreach (var toggle in panelRoot.GetComponentsInChildren<Toggle>(true)) StyleToggle(toggle);

                // Header strips (draggable title bars) that aren't any control's graphic.
                foreach (var img in panelRoot.GetComponentsInChildren<Image>(true))
                {
                    if (img.gameObject == panelRoot) continue;
                    if (IsPreserved(img.gameObject.name)) continue;
                    if (img.GetComponent<Selectable>() != null) continue;
                    if (img.name.IndexOf("Header", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        img.sprite = FiresRoundedSkin.RoundedSprite(7, HeaderBg);
                        img.type = Image.Type.Sliced;
                        img.color = Color.white;
                    }
                }

                // Text last, so control-specific colors above win where they were set.
                foreach (var tmp in panelRoot.GetComponentsInChildren<TMP_Text>(true)) StyleText(tmp);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresPopupTheme] reskin of '{panelRoot.name}' failed: {ex.Message}");
            }
        }

        private static bool IsPreserved(string name)
            => name != null &&
               (name.IndexOf("Swatch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Icon", StringComparison.OrdinalIgnoreCase) >= 0);

        private static void StylePanelShell(GameObject panelRoot)
        {
            var bg = panelRoot.GetComponent<Image>();
            if (bg != null)
            {
                bg.sprite = FiresRoundedSkin.RoundedSprite(10, PanelBg, PanelEdge, 1);
                bg.type = Image.Type.Sliced;
                bg.color = Color.white;
            }
            var outline = panelRoot.GetComponent<Outline>();
            if (outline != null) outline.enabled = false;   // the sprite bakes its own border ring
        }

        private static void StyleButton(Button button)
        {
            var img = button.targetGraphic as Image ?? button.GetComponent<Image>();
            if (img != null)
            {
                img.sprite = FiresRoundedSkin.RoundedSprite(7, Color.white);
                img.type = Image.Type.Sliced;
                img.color = Color.white;
            }
            var colors = button.colors;
            colors.normalColor = BtnNormal;
            colors.highlightedColor = BtnHover;
            colors.pressedColor = BtnPressed;
            colors.selectedColor = BtnNormal;
            colors.disabledColor = new Color(BtnPressed.r, BtnPressed.g, BtnPressed.b, 0.6f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            foreach (var label in button.GetComponentsInChildren<TMP_Text>(true))
                label.color = TextLight;
        }

        private static void StyleInput(TMP_InputField input)
        {
            var img = input.GetComponent<Image>();
            if (img != null)
            {
                img.sprite = FiresRoundedSkin.RoundedSprite(7, FieldFill, PanelEdge, 1);
                img.type = Image.Type.Sliced;
                img.color = Color.white;
            }
            if (input.textComponent != null) input.textComponent.color = TextCream;
            if (input.placeholder is TMP_Text ph) ph.color = TextDim;
            // White caret, wide + steady blink — the gold one vanished against the brown fills.
            input.caretColor = Color.white;
            input.customCaretColor = true;
            input.caretWidth = 2;
            input.caretBlinkRate = 0.85f;
            input.selectionColor = new Color(ThumbGold.r, ThumbGold.g, ThumbGold.b, 0.45f);
        }

        private static void StyleDropdown(TMP_Dropdown dropdown)
        {
            var img = dropdown.GetComponent<Image>();
            if (img != null)
            {
                img.sprite = FiresRoundedSkin.RoundedSprite(7, FieldFill, PanelEdge, 1);
                img.type = Image.Type.Sliced;
                img.color = Color.white;
            }
            if (dropdown.captionText != null) dropdown.captionText.color = TextCream;
            if (dropdown.itemText != null) dropdown.itemText.color = TextLight;
            if (dropdown.template != null)
            {
                var templateBg = dropdown.template.GetComponent<Image>();
                if (templateBg != null)
                {
                    templateBg.sprite = FiresRoundedSkin.RoundedSprite(7, ListBg, PanelEdge, 1);
                    templateBg.type = Image.Type.Sliced;
                    templateBg.color = Color.white;
                }
                foreach (var toggle in dropdown.template.GetComponentsInChildren<Toggle>(true))
                {
                    if (toggle.targetGraphic is Image itemBg)
                    {
                        itemBg.sprite = null;
                        itemBg.color = RowBrown;
                    }
                    var tColors = toggle.colors;
                    tColors.normalColor = Color.white;
                    tColors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
                    tColors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
                    tColors.selectedColor = Color.white;
                    toggle.colors = tColors;
                }
            }
        }

        private static void StyleSlider(Slider slider)
        {
            var bg = slider.GetComponent<Image>();
            if (bg != null)
            {
                bg.sprite = FiresRoundedSkin.RoundedSprite(5, TrackFill, PanelEdge, 1);
                bg.type = Image.Type.Sliced;
                bg.color = Color.white;
            }
            if (slider.fillRect != null)
            {
                var fill = slider.fillRect.GetComponent<Image>();
                if (fill != null)
                {
                    fill.sprite = FiresRoundedSkin.RoundedSprite(5, HeaderBg);
                    fill.type = Image.Type.Sliced;
                    fill.color = Color.white;
                }
            }
            if (slider.handleRect != null)
            {
                var handle = slider.handleRect.GetComponent<Image>();
                if (handle != null)
                {
                    handle.sprite = FiresRoundedSkin.RoundedSprite(8, ThumbGold);
                    handle.type = Image.Type.Sliced;
                    handle.color = Color.white;
                }
            }
        }

        private static void StyleToggle(Toggle toggle)
        {
            // Dropdown item toggles are handled by StyleDropdown; a standalone checkbox gets the
            // dark box + gold check.
            if (toggle.GetComponentInParent<TMP_Dropdown>() != null) return;
            if (toggle.targetGraphic is Image box)
            {
                box.sprite = FiresRoundedSkin.RoundedSprite(5, FieldFill, PanelEdge, 1);
                box.type = Image.Type.Sliced;
                box.color = Color.white;
            }
            if (toggle.graphic is Image check)
            {
                check.sprite = FiresRoundedSkin.RoundedSprite(4, ThumbGold);
                check.type = Image.Type.Sliced;
                check.color = Color.white;
            }
        }

        private static void StyleText(TMP_Text tmp)
        {
            bool isTitle = tmp.name.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0 || tmp.fontSize >= 18f;
            if (isTitle)
            {
                tmp.color = TextGold;
                tmp.fontStyle |= FontStyles.Bold;
                return;
            }
            // Flip parchment-ink (dark) text to the light palette; text already light/gold was set
            // deliberately by a control styler above — leave it.
            Color c = tmp.color;
            float luminance = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            if (luminance < 0.45f) tmp.color = TextLight;
        }
    }
}
