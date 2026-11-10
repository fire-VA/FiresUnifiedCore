using System;
using System.Collections.Generic;
using FiresCore.Input;
using FiresCore.UI.Shell.Kit;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.UI.Kit
{
    /// <summary>
    /// Builders for <see cref="KitWindow"/> content, in the rounded kit look (FiresTokens colours, FiresRoundedSkin plates, the
    /// chest tags panel's chips, pill switch, segments and stepper). Every builder parents to a layout column or row and sets its
    /// own LayoutElement sizes, so callers just add rows: Section in the Body, Row in a Section, a control in the Row's Control.
    /// One type scale: title 20, header 12 gold letter-spaced, body 13, small 12, notes 11.
    /// </summary>
    public static class KitUI
    {
        /// <summary>Window title size.</summary>
        public const float TitleSize = 20f;
        /// <summary>Section header size (gold, bold, letter-spaced).</summary>
        public const float HeaderSize = 12f;
        /// <summary>Body text size (rows, buttons, fields).</summary>
        public const float BodySize = 13f;
        /// <summary>Small text size (subtitle, chips, segments).</summary>
        public const float SmallSize = 12f;
        /// <summary>Note text size.</summary>
        public const float NoteSize = 11f;

        /// <summary>Height of a label-left / control-right row.</summary>
        public const float RowHeight = 32f;
        /// <summary>Height of a button.</summary>
        public const float ButtonHeight = 30f;
        /// <summary>Height of a text field.</summary>
        public const float InputHeight = 32f;
        /// <summary>Default pouch-cell size (the fuel row's cell).</summary>
        public const float CellSize = 44f;

        private const int BorderPx = 1, CellBorderPx = 2;
        private const float DividerHeight = 1f, SectionPadTop = 10f, HeaderHeight = 18f, HeaderGap = 8f;
        private const float HelpSize = 18f, HelpGap = 8f;
        private const float ChipHeight = 22f, ChipGap = 6f, ChipPadX = 9f, ChipCloseGap = 6f, ChipRadius = 12f, WarnBorderAlpha = 0.6f;
        private const float InputPadX = 9f, InputPadY = 1f;
        private const float SegmentHeight = 24f, SegmentPadX = 10f;
        private const float ToggleWidth = 38f, ToggleKnobInset = 3f;
        private const float StepButtonWidth = 26f, StepValueWidth = 38f, StepHeight = 24f, StepGap = 6f;
        private const float ButtonPadX = 12f, ButtonMinWidth = 64f;
        private const float SliderWidth = 150f, SliderValueWidth = 52f, SliderHeight = 20f;
        private const float TipWidth = 250f, TipPadX = 10f, TipPadY = 8f, TipGap = 4f;
        private const float DisabledAlpha = 0.45f;
        private const float ScrollbarWidth = 8f;

        // ---- sections, rows, notes ----------------------------------------------------------------------------

        /// <summary>A section: 1 px divider, a gold letter-spaced header and (with help) a "?" at its right. Returns the
        /// section's content column; add rows to it.</summary>
        public static RectTransform Section(Transform parent, string header, string helpTooltip = null)
        {
            var root = Column(parent, "Section_" + Safe(header), 0f);
            var div = NewRect(root, "Divider");
            var di = div.gameObject.AddComponent<Image>();
            di.color = FiresTokens.Hairline; di.raycastTarget = false;
            Size(div.gameObject, -1f, DividerHeight);
            Spacer(root, SectionPadTop);

            var head = NewRect(root, "Head");
            var hl = head.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = HelpGap; hl.childAlignment = TextAnchor.MiddleLeft;
            hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            Size(head.gameObject, -1f, HeaderHeight);
            var h = Label(head, "Header", header ?? "", HeaderSize, FiresTokens.Gold, TextAlignmentOptions.MidlineLeft);
            h.fontStyle = FontStyles.Bold; h.characterSpacing = FiresTokens.SectionTracking;
            var hle = h.gameObject.AddComponent<LayoutElement>(); hle.flexibleWidth = 1f; hle.minWidth = 0f; hle.preferredHeight = HeaderHeight;
            MarkLeaf(h.rectTransform);
            if (!string.IsNullOrEmpty(helpTooltip)) HelpMark(head, helpTooltip);
            Spacer(root, HeaderGap);

            var content = Column(root, "Content", 0f);
            return content;
        }

        /// <summary>A label-left / control-right row (32 high). Put controls in <see cref="KitRow.Control"/>.
        /// With a tooltip the label shows it on hover.</summary>
        public static KitRow Row(Transform parent, string label, string tooltip = null)
        {
            var row = NewRect(parent, "Row_" + Safe(label));
            var hl = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = FiresTokens.RowGap; hl.childAlignment = TextAnchor.MiddleLeft;
            hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            var rle = row.gameObject.AddComponent<LayoutElement>(); rle.minHeight = RowHeight;   // grows with a taller control (a pouch cell)
            var l = Label(row, "Label", label ?? "", BodySize, FiresTokens.TextMuted, TextAlignmentOptions.MidlineLeft);
            var le = l.gameObject.AddComponent<LayoutElement>(); le.flexibleWidth = 1f; le.minWidth = 0f; le.preferredHeight = RowHeight;
            MarkLeaf(l.rectTransform);
            if (!string.IsNullOrEmpty(tooltip)) AttachTooltip(l.gameObject, tooltip);

            var ctl = NewRect(row, "Control");
            var cl = ctl.gameObject.AddComponent<HorizontalLayoutGroup>();
            cl.spacing = StepGap; cl.childAlignment = TextAnchor.MiddleRight;
            cl.childControlWidth = true; cl.childControlHeight = true; cl.childForceExpandWidth = false; cl.childForceExpandHeight = false;
            var cle = ctl.gameObject.AddComponent<LayoutElement>(); cle.minHeight = RowHeight;
            return new KitRow(row, l, ctl);
        }

        /// <summary>A wrapping text line: 11, faint (or muted when not faint).</summary>
        public static TextMeshProUGUI Note(Transform parent, string text, bool faint = true)
        {
            var t = Label(parent, "Note", text ?? "", NoteSize, faint ? FiresTokens.TextFaint : FiresTokens.TextMuted, TextAlignmentOptions.TopLeft);
            t.textWrappingMode = TextWrappingModes.Normal;
            MarkLeaf(t.rectTransform);
            return t;
        }

        /// <summary>A single-line body label (13, text colour), for free text in a column or a row's control slot.</summary>
        public static TextMeshProUGUI Text(Transform parent, string text, Color? color = null, float size = BodySize)
        {
            var t = Label(parent, "Text", text ?? "", size, color ?? FiresTokens.Text, TextAlignmentOptions.MidlineLeft);
            float w = Mathf.Ceil(t.GetPreferredValues(t.text).x) + 1f;
            Size(t.gameObject, w, Mathf.Max(16f, size + 6f));
            MarkLeaf(t.rectTransform);
            return t;
        }

        /// <summary>A horizontal strip that lays children out left to right (spacing 8), e.g. a row of buttons that share the width.</summary>
        public static RectTransform Strip(Transform parent, float height = ButtonHeight)
        {
            var s = NewRect(parent, "Strip");
            var hl = s.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = FiresTokens.RowGap; hl.childAlignment = TextAnchor.MiddleLeft;
            hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = true; hl.childForceExpandHeight = false;
            Size(s.gameObject, -1f, height);
            return s;
        }

        /// <summary>A container whose children (chips) flow left to right and wrap.</summary>
        public static RectTransform ChipFlow(Transform parent)
        {
            var f = NewRect(parent, "ChipFlow");
            var flow = f.gameObject.AddComponent<KitFlowLayout>();
            flow.Spacing = new Vector2(ChipGap, ChipGap);
            return f;
        }

        // ---- controls ---------------------------------------------------------------------------------------------

        /// <summary>The one button plate: PanelRaised face, Edge border (Gold border and text for the primary), Selected on hover.</summary>
        public static KitButton Button(Transform parent, string text, bool primary, Action onClick)
        {
            var b = UIBuilderHelper.CreateButton(parent, text ?? "", Vector2.zero, Vector2.zero, () => onClick?.Invoke());
            b.name = "Button_" + Safe(text);
            StyleButton(b, primary);
            var t = b.GetComponentInChildren<TMP_Text>();
            float w = Mathf.Max(ButtonMinWidth, Mathf.Ceil(t != null ? t.GetPreferredValues(t.text).x : 0f) + ButtonPadX * 2f);
            Size(b.gameObject, w, ButtonHeight);
            MarkLeaf((RectTransform)b.transform);
            return new KitButton(b, t, primary);
        }

        /// <summary>The pill switch: Well track, Edge border (Gold when on), knob left faint / right gold. Clicking flips
        /// it and calls onChanged with the new value.</summary>
        public static KitToggle PillToggle(Transform parent, bool value, Action<bool> onChanged)
        {
            var track = NewRect(parent, "PillToggle");
            var img = track.gameObject.AddComponent<Image>();
            var b = track.gameObject.AddComponent<UnityEngine.UI.Button>();
            b.targetGraphic = img; b.transition = Selectable.Transition.None;
            var knob = NewRect(track, "Knob");
            var ki = knob.gameObject.AddComponent<Image>();
            SetPlate(ki, Plate(FiresTokens.SwitchKnob * 0.5f, Color.white, null));
            ki.raycastTarget = false;
            knob.anchorMin = knob.anchorMax = new Vector2(0f, 0.5f); knob.pivot = new Vector2(0f, 0.5f);
            knob.sizeDelta = new Vector2(FiresTokens.SwitchKnob, FiresTokens.SwitchKnob);
            Size(track.gameObject, ToggleWidth, FiresTokens.SwitchHeight);
            MarkLeaf(track);
            var h = new KitToggle(b, img, knob, ki, ToggleWidth, ToggleKnobInset);
            h.SetValue(value);
            b.onClick.AddListener(() => { h.SetValue(!h.Value); onChanged?.Invoke(h.Value); });
            return h;
        }

        /// <summary>A segmented control: Edge-bordered strip, the chosen segment Selected with gold text. Clicking a
        /// segment selects it and calls onChanged with its index.</summary>
        public static KitSegmented Segmented(Transform parent, string[] options, int index, Action<int> onChanged)
        {
            options = options ?? new string[0];
            var seg = NewRect(parent, "Segmented");
            var segImg = seg.gameObject.AddComponent<Image>();
            SetPlate(segImg, Plate(FiresTokens.RadiusSmall, Color.clear, FiresTokens.Edge));
            segImg.raycastTarget = false;
            var hl = seg.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.padding = new RectOffset(BorderPx, BorderPx, BorderPx, BorderPx);
            hl.spacing = 0f; hl.childAlignment = TextAnchor.MiddleLeft;
            hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = true;
            var buttons = new UnityEngine.UI.Button[options.Length];
            float total = BorderPx * 2f;
            KitSegmented handle = null;
            for (int i = 0; i < options.Length; i++)
            {
                int at = i;
                var b = UIBuilderHelper.CreateButton(seg, options[i] ?? "", Vector2.zero, Vector2.zero, () => { handle.SetIndex(at); onChanged?.Invoke(at); });
                b.name = "Segment_" + Safe(options[i]);
                SetPlate((Image)b.targetGraphic, Plate(FiresTokens.RadiusSmall - BorderPx, FiresTokens.Selected, null));
                b.transition = Selectable.Transition.None;
                var t = b.GetComponentInChildren<TMP_Text>();
                StyleText(t, SmallSize, FiresTokens.TextMuted, TextAlignmentOptions.Center);
                float w = Mathf.Ceil(t.GetPreferredValues(t.text).x) + SegmentPadX * 2f;
                Size(b.gameObject, w, SegmentHeight - BorderPx * 2f);
                MarkLeaf((RectTransform)b.transform);
                buttons[i] = b;
                total += w;
            }
            Size(seg.gameObject, total, SegmentHeight);
            handle = new KitSegmented(buttons);
            handle.SetIndex(index);
            return handle;
        }

        /// <summary>A slider: rounded Well track, gold fill and knob, the value and unit to its right. Values snap to step
        /// (0 = continuous); onChanged gets the snapped value.</summary>
        public static KitSlider Slider(Transform parent, float min, float max, float step, float value, string unit, Action<float> onChanged)
        {
            var root = NewRect(parent, "Slider");
            var hl = root.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = StepGap; hl.childAlignment = TextAnchor.MiddleRight;
            hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;

            var area = NewRect(root, "Track");
            Size(area.gameObject, SliderWidth, SliderHeight);
            var bg = NewRect(area, "Background");
            bg.anchorMin = new Vector2(0f, 0.5f); bg.anchorMax = new Vector2(1f, 0.5f); bg.sizeDelta = new Vector2(0f, FiresTokens.SliderTrack);
            var bgi = bg.gameObject.AddComponent<Image>();
            SetPlate(bgi, Plate(FiresTokens.SliderTrack * 0.5f, FiresTokens.Well, FiresTokens.Edge));
            var fillArea = NewRect(area, "FillArea");
            fillArea.anchorMin = new Vector2(0f, 0.5f); fillArea.anchorMax = new Vector2(1f, 0.5f); fillArea.sizeDelta = new Vector2(0f, FiresTokens.SliderTrack);
            var fill = NewRect(fillArea, "Fill");
            fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(0f, 1f); fill.sizeDelta = Vector2.zero;
            var fi = fill.gameObject.AddComponent<Image>();
            SetPlate(fi, Plate(FiresTokens.SliderTrack * 0.5f, FiresTokens.Gold, null)); fi.raycastTarget = false;
            var handleArea = NewRect(area, "HandleArea");
            handleArea.anchorMin = Vector2.zero; handleArea.anchorMax = Vector2.one;
            handleArea.offsetMin = new Vector2(FiresTokens.SliderKnob * 0.5f, 0f); handleArea.offsetMax = new Vector2(-FiresTokens.SliderKnob * 0.5f, 0f);
            var knob = NewRect(handleArea, "Knob");
            knob.sizeDelta = new Vector2(FiresTokens.SliderKnob, FiresTokens.SliderKnob);
            var ki = knob.gameObject.AddComponent<Image>();
            SetPlate(ki, Plate(FiresTokens.SliderKnob * 0.5f, FiresTokens.Gold, FiresTokens.GoldBright));

            var s = area.gameObject.AddComponent<UnityEngine.UI.Slider>();
            s.fillRect = fill; s.handleRect = knob; s.targetGraphic = ki;
            s.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            s.transition = Selectable.Transition.None;
            s.minValue = Mathf.Min(min, max); s.maxValue = Mathf.Max(min, max);
            s.wholeNumbers = false;
            MarkLeaf(area);

            var val = Label(root, "Value", "", BodySize, FiresTokens.Text, TextAlignmentOptions.MidlineRight);
            Size(val.gameObject, SliderValueWidth, SliderHeight);
            MarkLeaf(val.rectTransform);
            var h = new KitSlider(s, val, step, unit);
            h.SetValue(value);
            s.onValueChanged.AddListener(v => { if (h.Applying) return; float snapped = h.Snap(v); h.SetValue(snapped); onChanged?.Invoke(snapped); });
            return h;
        }

        /// <summary>A stepper: "-" button, the value, "+" button.</summary>
        public static KitStepper Stepper(Transform parent, string valueText, Action onMinus, Action onPlus)
        {
            var root = NewRect(parent, "Stepper");
            var hl = root.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = StepGap; hl.childAlignment = TextAnchor.MiddleCenter;
            hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            var minus = UIBuilderHelper.CreateButton(root, "-", Vector2.zero, Vector2.zero, () => onMinus?.Invoke());
            minus.name = "Minus"; StyleButton(minus, false); Size(minus.gameObject, StepButtonWidth, StepHeight); MarkLeaf((RectTransform)minus.transform);
            var val = Label(root, "Value", valueText ?? "", BodySize, FiresTokens.Text, TextAlignmentOptions.Center);
            Size(val.gameObject, StepValueWidth, StepHeight); MarkLeaf(val.rectTransform);
            var plus = UIBuilderHelper.CreateButton(root, "+", Vector2.zero, Vector2.zero, () => onPlus?.Invoke());
            plus.name = "Plus"; StyleButton(plus, false); Size(plus.gameObject, StepButtonWidth, StepHeight); MarkLeaf((RectTransform)plus.transform);
            return new KitStepper(root, minus, val, plus);
        }

        /// <summary>A pouch cell: a dark square with a lighter gold border (dashed for an empty / drop-target cell), the icon
        /// centred and the count bottom-right.</summary>
        public static KitIconCell IconCell(Transform parent, Sprite icon, string count, bool dashed, float size = CellSize)
        {
            var cell = NewRect(parent, "IconCell");
            var img = cell.gameObject.AddComponent<Image>();
            img.raycastTarget = true;
            Size(cell.gameObject, size, size);
            var ic = NewRect(cell, "Icon");
            ic.anchorMin = ic.anchorMax = new Vector2(0.5f, 0.5f);
            ic.sizeDelta = new Vector2(size * FiresTokens.ItemCellIconFraction, size * FiresTokens.ItemCellIconFraction);
            var ii = ic.gameObject.AddComponent<Image>();
            ii.preserveAspect = true; ii.raycastTarget = false;
            var n = Label(cell, "Count", "", SmallSize, Color.white, TextAlignmentOptions.BottomRight);
            n.fontStyle = FontStyles.Bold;
            var nr = n.rectTransform;
            nr.anchorMin = Vector2.zero; nr.anchorMax = Vector2.one; nr.offsetMin = new Vector2(3f, 1f); nr.offsetMax = new Vector2(-4f, -2f);
            MarkLeaf(cell);
            var h = new KitIconCell(cell, img, ii, n);
            h.SetDashed(dashed); h.SetIcon(icon); h.SetCount(count);
            return h;
        }

        /// <summary>A chip: Well pill, Edge border (Danger, softened, and Danger text when warn), and an "x" that calls
        /// onRemove (no "x" when onRemove is null). Put chips in a <see cref="ChipFlow"/> to wrap them.</summary>
        public static RectTransform Chip(Transform parent, string text, bool warn, Action onRemove)
        {
            var chip = NewRect(parent, "Chip_" + Safe(text));
            var img = chip.gameObject.AddComponent<Image>();
            SetPlate(img, Plate(ChipRadius, FiresTokens.Well, warn ? FiresTokens.WithAlpha(FiresTokens.Danger, WarnBorderAlpha) : FiresTokens.Edge));
            img.raycastTarget = false;
            var hl = chip.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.padding = new RectOffset((int)ChipPadX, (int)ChipPadX, 0, 0);
            hl.spacing = ChipCloseGap; hl.childAlignment = TextAnchor.MiddleLeft;
            hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            var label = Label(chip, "Text", text ?? "", SmallSize, warn ? FiresTokens.Danger : FiresTokens.Text, TextAlignmentOptions.MidlineLeft);
            label.overflowMode = TextOverflowModes.Ellipsis;
            float lw = Mathf.Ceil(label.GetPreferredValues(label.text).x) + 1f;
            var lle = label.gameObject.AddComponent<LayoutElement>(); lle.preferredWidth = lw; lle.minWidth = 0f; lle.flexibleWidth = 0f; lle.preferredHeight = ChipHeight;
            float w = ChipPadX * 2f + lw;
            if (onRemove != null)
            {
                var x = UIBuilderHelper.CreateButton(chip, FiresTokens.GlyphClose, Vector2.zero, Vector2.zero, () => onRemove());
                x.name = "Remove";
                x.transition = Selectable.Transition.None;
                x.targetGraphic.color = Color.clear;   // invisible hit area, the glyph shows
                var xt = x.GetComponentInChildren<TMP_Text>();
                StyleText(xt, SmallSize, FiresTokens.TextFaint, TextAlignmentOptions.Center);
                float xw = Mathf.Ceil(xt.GetPreferredValues(FiresTokens.GlyphClose).x) + 1f;
                Size(x.gameObject, xw, ChipHeight);
                w += ChipCloseGap + xw;
            }
            Size(chip.gameObject, w, ChipHeight);
            MarkLeaf(chip);
            return chip;
        }

        /// <summary>A single-line text field (Well plate, Edge border). Enter calls onSubmit with the text. While focused it
        /// holds Core's input block (FiresInputBlock.Acquire), so game keys do not fire.</summary>
        public static KitTextField TextField(Transform parent, string placeholder, Action<string> onSubmit)
        {
            var input = UIBuilderHelper.CreateInputField(parent, "TextField", placeholder ?? "", Vector2.zero, Vector2.zero, (int)BodySize);
            StyleInput(input);
            Size(input.gameObject, -1f, InputHeight);
            var le = input.GetComponent<LayoutElement>(); le.flexibleWidth = 1f; le.minWidth = 80f;
            var guard = input.gameObject.AddComponent<KitFieldGuard>();
            guard.Field = input;
            input.onSelect.AddListener(_ => guard.Take());
            input.onDeselect.AddListener(_ => guard.Drop());
            input.onEndEdit.AddListener(_ => guard.Drop());
            input.onSubmit.AddListener(s => onSubmit?.Invoke(s));
            MarkLeaf((RectTransform)input.transform);
            return new KitTextField(input);
        }

        /// <summary>A scroll view filling the rest of its column; returns the content column (VerticalLayoutGroup, grows with
        /// its rows).</summary>
        public static RectTransform ScrollView(Transform parent, float minHeight = 80f)
        {
            var area = UIBuilderHelper.CreateScrollArea(parent, "ScrollView", Vector2.zero, Vector2.one, null, ScrollbarWidth);
            var rle = area.Root.AddComponent<LayoutElement>(); rle.flexibleHeight = 1f; rle.minHeight = minHeight;
            area.Viewport.offsetMin = Vector2.zero; area.Viewport.offsetMax = new Vector2(-ScrollbarWidth - 4f, 0f);
            area.Viewport.gameObject.AddComponent<KitScrollViewport>();
            if (area.Scrollbar != null)
            {
                var sbi = area.Scrollbar.GetComponent<Image>();
                if (sbi != null) SetPlate(sbi, Plate(ScrollbarWidth * 0.5f - 1f, FiresTokens.Well, null));
                if (area.Scrollbar.handleRect != null)
                {
                    var hi = area.Scrollbar.handleRect.GetComponent<Image>();
                    if (hi != null) SetPlate(hi, Plate(ScrollbarWidth * 0.5f - 1f, FiresTokens.Edge, null));
                }
                area.ScrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            }
            var content = area.Content;
            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 0f; vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true; vlg.childControlHeight = true; vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            var fit = content.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize; fit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            return content;
        }

        /// <summary>A round "?" (Edge ring, muted mark) that shows the tooltip on hover: the vanilla UITooltip when its
        /// prefab is found, else the kit's own tip on the window canvas.</summary>
        public static RectTransform HelpMark(Transform parent, string tooltip)
        {
            var bubble = NewRect(parent, "Help");
            var bi = bubble.gameObject.AddComponent<Image>();
            SetPlate(bi, Plate(HelpSize * 0.5f, Color.clear, FiresTokens.Edge));
            bi.raycastTarget = true;   // the hover target
            var m = Label(bubble, "Mark", "?", NoteSize, FiresTokens.TextMuted, TextAlignmentOptions.Center);
            var mr = m.rectTransform; mr.anchorMin = Vector2.zero; mr.anchorMax = Vector2.one; mr.offsetMin = Vector2.zero; mr.offsetMax = Vector2.zero;
            Size(bubble.gameObject, HelpSize, HelpSize);
            AttachTooltip(bubble.gameObject, tooltip);
            MarkLeaf(bubble);
            return bubble;
        }

        /// <summary>Gives any element a hover tooltip (vanilla UITooltip when available, else the kit tip). The element
        /// becomes a raycast target.</summary>
        public static void AttachTooltip(GameObject target, string tooltip)
        {
            if (target == null || string.IsNullOrEmpty(tooltip)) return;
            var g = target.GetComponent<Graphic>();
            if (g == null) { var img = target.AddComponent<Image>(); img.color = Color.clear; g = img; }
            g.raycastTarget = true;
            var prefab = VanillaTooltipPrefab();
            if (prefab != null)
            {
                var tt = target.GetComponent<UITooltip>() ?? target.AddComponent<UITooltip>();
                tt.m_tooltipPrefab = prefab;
                tt.m_topic = "";
                tt.m_text = tooltip;
                return;
            }
            var own = target.GetComponent<KitOwnTip>() ?? target.AddComponent<KitOwnTip>();
            own.Text = tooltip;
        }

        // ---- shared look helpers (the chest tags panel's, reimplemented) --------------------------------------------

        /// <summary>A rounded kit plate sprite (FiresRoundedSkin), 1 px border when a border colour is given.</summary>
        public static Sprite Plate(float radius, Color fill, Color? border) =>
            FiresRoundedSkin.RoundedSprite(Mathf.RoundToInt(radius), fill, border, border.HasValue ? BorderPx : 0);

        /// <summary>Puts a plate sprite on an image (sliced, white tint).</summary>
        public static void SetPlate(Image img, Sprite s)
        {
            if (img == null) return;
            img.sprite = s; img.type = Image.Type.Sliced; img.color = Color.white; img.material = null; img.pixelsPerUnitMultiplier = 1f;
        }

        /// <summary>The kit button look on any uGUI button (primary = gold border and text).</summary>
        public static void StyleButton(UnityEngine.UI.Button b, bool primary)
        {
            if (b == null) return;
            Color border = primary ? FiresTokens.Gold : FiresTokens.Edge;
            var img = (Image)b.targetGraphic;
            var normal = Plate(FiresTokens.RadiusSmall, FiresTokens.PanelRaised, border);
            var hover = Plate(FiresTokens.RadiusSmall, FiresTokens.Selected, border);
            SetPlate(img, normal);
            b.transition = Selectable.Transition.SpriteSwap;
            b.spriteState = new SpriteState { highlightedSprite = hover, pressedSprite = hover, selectedSprite = normal, disabledSprite = normal };
            StyleText(b.GetComponentInChildren<TMP_Text>(), BodySize, primary ? FiresTokens.Gold : FiresTokens.Text, TextAlignmentOptions.Center);
        }

        /// <summary>The kit text style: size, colour, alignment, no wrap, no auto size, no raycast.</summary>
        public static void StyleText(TMP_Text t, float size, Color color, TextAlignmentOptions align)
        {
            if (t == null) return;
            t.fontSize = size; t.color = color; t.alignment = align; t.fontStyle = FontStyles.Normal;
            t.enableAutoSizing = false; t.textWrappingMode = TextWrappingModes.NoWrap; t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
        }

        /// <summary>A kit label (Valheim font, kit text style).</summary>
        public static TextMeshProUGUI Label(Transform parent, string name, string text, float size, Color color, TextAlignmentOptions align)
        {
            var t = UIBuilderHelper.CreateLabel(parent, name, text, size, color, Vector2.zero, Vector2.zero, align);
            StyleText(t, size, color, align);
            return t;
        }

        /// <summary>Marks an element for <see cref="KitWindow.LayoutReport"/>.</summary>
        public static void MarkLeaf(RectTransform rt) { if (rt != null && rt.GetComponent<KitLeafMark>() == null) rt.gameObject.AddComponent<KitLeafMark>(); }

        /// <summary>The drawn rect of an element in screen pixels.</summary>
        public static Rect DrawnRect(Transform t)
        {
            var rt = t as RectTransform;
            if (rt == null) return default;
            var w = new Vector3[4]; rt.GetWorldCorners(w);
            return new Rect(w[0].x, w[0].y, w[2].x - w[0].x, w[2].y - w[0].y);
        }

        internal static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static RectTransform Column(Transform parent, string name, float spacing)
        {
            var c = NewRect(parent, name);
            var vlg = c.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = spacing; vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true; vlg.childControlHeight = true; vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            return c;
        }

        private static void Spacer(Transform parent, float h) => Size(NewRect(parent, "Gap").gameObject, -1f, h);

        // preferred (and min) size; -1 leaves that axis to the layout
        private static void Size(GameObject go, float w, float h)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            if (w >= 0f) { le.preferredWidth = w; le.minWidth = w; }
            if (h >= 0f) { le.preferredHeight = h; le.minHeight = h; }
        }

        private static string Safe(string s) => string.IsNullOrEmpty(s) ? "_" : (s.Length > 24 ? s.Substring(0, 24) : s);

        private static void StyleInput(TMP_InputField input)
        {
            SetPlate((Image)input.targetGraphic ?? input.GetComponent<Image>(), Plate(FiresTokens.RadiusSmall, FiresTokens.Well, FiresTokens.Edge));
            if (input.textViewport != null) { input.textViewport.offsetMin = new Vector2(InputPadX, InputPadY); input.textViewport.offsetMax = new Vector2(-InputPadX, -InputPadY); }
            if (input.textComponent != null) { input.textComponent.color = FiresTokens.Text; input.textComponent.alignment = TextAlignmentOptions.MidlineLeft; }
            if (input.placeholder is TMP_Text ph) { ph.color = FiresTokens.TextFaint; ph.fontStyle = FontStyles.Normal; ph.alignment = TextAlignmentOptions.MidlineLeft; }
            input.caretColor = FiresTokens.Text;
            input.selectionColor = FiresTokens.WithAlpha(FiresTokens.Gold, FiresTokens.LearnedFillAlpha);
        }

        // the dashed cell border: tiled edges repeat a 4-on / 4-off dash
        private static Sprite s_dashed;
        internal static Sprite DashedCell()
        {
            if (s_dashed != null) return s_dashed;
            const int size = 24, b = 8, r = 6;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            Color fill = FiresTokens.Well, line = FiresTokens.Edge, clear = new Color(0f, 0f, 0f, 0f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, r, size - r), cy = Mathf.Clamp(y + 0.5f, r, size - r);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    if (d > r) { tex.SetPixel(x, y, clear); continue; }
                    bool edge = d > r - CellBorderPx || x < CellBorderPx || y < CellBorderPx || x >= size - CellBorderPx || y >= size - CellBorderPx;
                    bool inCorner = (x < b || x >= size - b) && (y < b || y >= size - b);
                    bool dashOn = inCorner || ((x < CellBorderPx || x >= size - CellBorderPx) ? ((y - b) & 4) == 0 : ((x - b) & 4) == 0);
                    tex.SetPixel(x, y, edge ? (dashOn ? line : fill) : fill);
                }
            tex.Apply();
            s_dashed = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
            s_dashed.hideFlags = HideFlags.HideAndDontSave;
            return s_dashed;
        }

        internal static Sprite SolidCell() => FiresRoundedSkin.RoundedSprite(Mathf.RoundToInt(FiresTokens.RadiusSmall), FiresTokens.Well, FiresTokens.Edge, CellBorderPx);

        // ---- tooltips -------------------------------------------------------------------------------------------------

        private static GameObject s_tipPrefab;
        private static float s_nextPrefabLook;
        private static GameObject VanillaTooltipPrefab()
        {
            if (s_tipPrefab != null) return s_tipPrefab;
            if (Time.unscaledTime < s_nextPrefabLook) return null;
            s_nextPrefabLook = Time.unscaledTime + 5f;
            try
            {
                var grid = InventoryGui.instance != null ? InventoryGui.instance.m_playerGrid : null;
                var tt = grid != null && grid.m_elementPrefab != null ? grid.m_elementPrefab.GetComponent<UITooltip>() : null;
                if (tt != null && tt.m_tooltipPrefab != null) return s_tipPrefab = tt.m_tooltipPrefab;
                foreach (var t in Resources.FindObjectsOfTypeAll<UITooltip>())
                    if (t != null && t.m_tooltipPrefab != null) return s_tipPrefab = t.m_tooltipPrefab;
            }
            catch { }
            return null;
        }

        private static RectTransform s_tip;
        private static TextMeshProUGUI s_tipText;

        internal static void ShowOwnTip(RectTransform anchor, string text)
        {
            var host = KitWindowHost.Instance;
            if (host == null || anchor == null) return;
            if (s_tip == null)
            {
                s_tip = NewRect(host.transform, "KitTip");
                var img = s_tip.gameObject.AddComponent<Image>();
                SetPlate(img, Plate(FiresTokens.TooltipRadius, FiresTokens.Panel, FiresTokens.Edge));
                img.raycastTarget = false;
                s_tipText = Label(s_tip, "Text", "", SmallSize, FiresTokens.Text, TextAlignmentOptions.TopLeft);
                s_tipText.textWrappingMode = TextWrappingModes.Normal;
                var tr = s_tipText.rectTransform;
                tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
                tr.offsetMin = new Vector2(TipPadX, TipPadY); tr.offsetMax = new Vector2(-TipPadX, -TipPadY);
            }
            s_tipText.text = text;
            float h = Mathf.Ceil(s_tipText.GetPreferredValues(text, TipWidth - TipPadX * 2f, 0f).y) + TipPadY * 2f;
            s_tip.anchorMin = s_tip.anchorMax = Vector2.zero; s_tip.pivot = new Vector2(1f, 1f);
            s_tip.sizeDelta = new Vector2(TipWidth, h);
            var w = new Vector3[4]; anchor.GetWorldCorners(w);   // 3 = bottom-right
            s_tip.position = w[3] + anchor.TransformVector(new Vector3(0f, -TipGap, 0f));
            // keep it on screen
            var c = new Vector3[4]; s_tip.GetWorldCorners(c);
            Vector3 shift = Vector3.zero;
            if (c[0].x < 0f) shift.x = -c[0].x;
            if (c[0].y < 0f) shift.y = -c[0].y;
            s_tip.position += shift;
            s_tip.SetAsLastSibling();
            s_tip.gameObject.SetActive(true);
        }

        internal static void HideOwnTip() { if (s_tip != null) s_tip.gameObject.SetActive(false); }
        internal static void OwnTipToFront() { if (s_tip != null) s_tip.SetAsLastSibling(); }
    }

    // ---- handles ------------------------------------------------------------------------------------------------------

    /// <summary>A label-left / control-right row.</summary>
    public sealed class KitRow
    {
        internal KitRow(RectTransform root, TextMeshProUGUI label, RectTransform control) { Root = root; Label = label; Control = control; }
        /// <summary>The row.</summary>
        public RectTransform Root { get; }
        /// <summary>The left label.</summary>
        public TextMeshProUGUI Label { get; }
        /// <summary>The right control slot (a right-aligned HorizontalLayoutGroup): parent controls here.</summary>
        public RectTransform Control { get; }
        /// <summary>Sets the label text.</summary>
        public void SetLabel(string text) { if (Label != null) Label.text = text ?? ""; }
        /// <summary>Shows or hides the row.</summary>
        public void SetVisible(bool on) { if (Root != null) Root.gameObject.SetActive(on); }
    }

    /// <summary>A kit button.</summary>
    public sealed class KitButton
    {
        private readonly bool m_primary;
        private CanvasGroup m_group;
        internal KitButton(UnityEngine.UI.Button b, TMP_Text t, bool primary) { Button = b; Label = t; m_primary = primary; }
        /// <summary>The uGUI button.</summary>
        public UnityEngine.UI.Button Button { get; }
        /// <summary>The button's text.</summary>
        public TMP_Text Label { get; }
        /// <summary>The button's rect.</summary>
        public RectTransform Rect => Button != null ? (RectTransform)Button.transform : null;
        /// <summary>Sets the text (the width stays).</summary>
        public void SetText(string text) { if (Label != null) Label.text = text ?? ""; }
        /// <summary>Enables or disables the button (disabled = faded, not clickable).</summary>
        public void SetEnabled(bool on)
        {
            if (Button == null) return;
            Button.interactable = on;
            if (m_group == null) m_group = Button.GetComponent<CanvasGroup>() ?? Button.gameObject.AddComponent<CanvasGroup>();
            m_group.alpha = on ? 1f : 0.45f;
        }
        /// <summary>True for a primary (gold) button.</summary>
        public bool Primary => m_primary;
    }

    /// <summary>A pill switch.</summary>
    public sealed class KitToggle
    {
        private readonly Image m_track, m_knobImg;
        private readonly RectTransform m_knob;
        private readonly float m_width, m_inset;
        internal KitToggle(UnityEngine.UI.Button b, Image track, RectTransform knob, Image knobImg, float width, float inset)
        { Button = b; m_track = track; m_knob = knob; m_knobImg = knobImg; m_width = width; m_inset = inset; }
        /// <summary>The click target.</summary>
        public UnityEngine.UI.Button Button { get; }
        /// <summary>The current value.</summary>
        public bool Value { get; private set; }
        /// <summary>Sets the value without calling onChanged.</summary>
        public void SetValue(bool on)
        {
            Value = on;
            KitUI.SetPlate(m_track, KitUI.Plate(FiresTokens.SwitchHeight * 0.5f, FiresTokens.Well, on ? FiresTokens.Gold : FiresTokens.Edge));
            m_knobImg.color = on ? FiresTokens.Gold : FiresTokens.TextFaint;
            m_knob.anchoredPosition = new Vector2(on ? m_width - m_inset - FiresTokens.SwitchKnob : m_inset, 0f);
        }
    }

    /// <summary>A segmented control.</summary>
    public sealed class KitSegmented
    {
        private readonly UnityEngine.UI.Button[] m_buttons;
        internal KitSegmented(UnityEngine.UI.Button[] buttons) { m_buttons = buttons; }
        /// <summary>The chosen index.</summary>
        public int Index { get; private set; }
        /// <summary>Selects a segment without calling onChanged.</summary>
        public void SetIndex(int index)
        {
            Index = index;
            for (int i = 0; i < m_buttons.Length; i++)
            {
                var b = m_buttons[i];
                if (b == null) continue;
                bool on = i == index;
                b.targetGraphic.color = on ? Color.white : Color.clear;   // clear keeps the segment clickable
                var t = b.GetComponentInChildren<TMP_Text>();
                if (t != null) t.color = on ? FiresTokens.Gold : FiresTokens.TextMuted;
            }
        }
    }

    /// <summary>A slider with its value label.</summary>
    public sealed class KitSlider
    {
        private readonly float m_step;
        private readonly string m_unit;
        internal bool Applying;
        internal KitSlider(UnityEngine.UI.Slider s, TMP_Text v, float step, string unit) { Slider = s; ValueLabel = v; m_step = step; m_unit = unit; }
        /// <summary>The uGUI slider.</summary>
        public UnityEngine.UI.Slider Slider { get; }
        /// <summary>The value label.</summary>
        public TMP_Text ValueLabel { get; }
        /// <summary>The current value.</summary>
        public float Value => Slider != null ? Slider.value : 0f;
        internal float Snap(float v) => m_step > 0f ? Mathf.Clamp(Slider.minValue + Mathf.Round((v - Slider.minValue) / m_step) * m_step, Slider.minValue, Slider.maxValue) : v;
        /// <summary>Sets the value (snapped) without calling onChanged.</summary>
        public void SetValue(float v)
        {
            if (Slider == null) return;
            v = Snap(Mathf.Clamp(v, Slider.minValue, Slider.maxValue));
            Applying = true;
            try { Slider.value = v; } finally { Applying = false; }
            string fmt = m_step <= 0f || m_step % 1f != 0f ? (m_step > 0f && m_step >= 0.1f ? "0.0" : "0.00") : "0";
            if (ValueLabel != null) ValueLabel.text = v.ToString(fmt) + (string.IsNullOrEmpty(m_unit) ? "" : " " + m_unit);
        }
    }

    /// <summary>A "-" value "+" stepper.</summary>
    public sealed class KitStepper
    {
        internal KitStepper(RectTransform root, UnityEngine.UI.Button minus, TMP_Text value, UnityEngine.UI.Button plus) { Root = root; Minus = minus; ValueLabel = value; Plus = plus; }
        /// <summary>The stepper.</summary>
        public RectTransform Root { get; }
        /// <summary>The "-" button.</summary>
        public UnityEngine.UI.Button Minus { get; }
        /// <summary>The "+" button.</summary>
        public UnityEngine.UI.Button Plus { get; }
        /// <summary>The value label.</summary>
        public TMP_Text ValueLabel { get; }
        /// <summary>Sets the shown value.</summary>
        public void SetValue(string text) { if (ValueLabel != null) ValueLabel.text = text ?? ""; }
    }

    /// <summary>A pouch cell.</summary>
    public sealed class KitIconCell
    {
        internal KitIconCell(RectTransform root, Image plate, Image icon, TMP_Text count) { Root = root; Plate = plate; Icon = icon; Count = count; }
        /// <summary>The cell (a raycast target: add tooltips or drop handlers here).</summary>
        public RectTransform Root { get; }
        /// <summary>The cell plate.</summary>
        public Image Plate { get; }
        /// <summary>The icon image.</summary>
        public Image Icon { get; }
        /// <summary>The count text.</summary>
        public TMP_Text Count { get; }
        /// <summary>Sets the icon (null hides it).</summary>
        public void SetIcon(Sprite s) { if (Icon == null) return; Icon.sprite = s; Icon.enabled = s != null; }
        /// <summary>Sets the count (null or empty hides it).</summary>
        public void SetCount(string c) { if (Count != null) Count.text = c ?? ""; }
        /// <summary>Greys the icon (an empty cell).</summary>
        public void SetDim(bool dim) { if (Icon != null) Icon.color = dim ? new Color(0.6f, 0.6f, 0.6f, 0.5f) : Color.white; }
        /// <summary>Dashed border (empty / drop target) or the solid gold border.</summary>
        public void SetDashed(bool dashed)
        {
            if (Plate == null) return;
            Plate.sprite = dashed ? KitUI.DashedCell() : KitUI.SolidCell();
            Plate.type = dashed ? Image.Type.Tiled : Image.Type.Sliced;
            Plate.color = Color.white; Plate.pixelsPerUnitMultiplier = 1f;
        }
    }

    /// <summary>A text field.</summary>
    public sealed class KitTextField
    {
        internal KitTextField(TMP_InputField input) { Input = input; }
        /// <summary>The TMP input field.</summary>
        public TMP_InputField Input { get; }
        /// <summary>The current text.</summary>
        public string Text => Input != null ? Input.text : "";
        /// <summary>Sets the text.</summary>
        public void SetText(string text) { if (Input != null) Input.text = text ?? ""; }
    }

    // ---- components -------------------------------------------------------------------------------------------------

    /// <summary>Holds Core's input block while its field is focused; releases it on blur, disable and destroy.</summary>
    internal sealed class KitFieldGuard : MonoBehaviour
    {
        internal TMP_InputField Field;
        private bool m_held;
        private static KitFieldGuard s_focused;
        private static int s_leftFrame = -10;

        internal static bool FocusedOrJustLeft => (s_focused != null && s_focused.Field != null && s_focused.Field.isFocused) || Time.frameCount - s_leftFrame <= 1;

        internal static void Unfocus()
        {
            if (s_focused != null && s_focused.Field != null) s_focused.Field.DeactivateInputField();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            if (s_focused != null) s_focused.Drop();
        }

        internal void Take()
        {
            if (!m_held) { FiresInputBlock.Acquire(this, this); m_held = true; }
            s_focused = this;
        }

        internal void Drop()
        {
            if (m_held) { FiresInputBlock.Release(this); m_held = false; s_leftFrame = Time.frameCount; }
            if (s_focused == this) s_focused = null;
        }

        private void Update() { if (m_held && (Field == null || !Field.isFocused)) Drop(); }
        private void OnDisable() => Drop();
        private void OnDestroy() => Drop();
    }

    /// <summary>The kit's own hover tip, used when the vanilla tooltip prefab is not loaded.</summary>
    internal sealed class KitOwnTip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        internal string Text;
        public void OnPointerEnter(PointerEventData e) => KitUI.ShowOwnTip((RectTransform)transform, Text);
        public void OnPointerExit(PointerEventData e) => KitUI.HideOwnTip();
        private void OnDisable() => KitUI.HideOwnTip();
    }

    /// <summary>Lays children out left to right at their preferred sizes, wrapping to a new line when the width runs out.</summary>
    public sealed class KitFlowLayout : LayoutGroup
    {
        /// <summary>Gap between children (x) and lines (y).</summary>
        public Vector2 Spacing = new Vector2(6f, 6f);
        private float m_height;

        /// <inheritdoc/>
        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            float w = 0f;
            foreach (var c in rectChildren) w = Mathf.Max(w, LayoutUtility.GetPreferredWidth(c));
            SetLayoutInputForAxis(padding.horizontal + w, padding.horizontal + w, 1f, 0);
        }

        /// <inheritdoc/>
        public override void CalculateLayoutInputVertical()
        {
            m_height = Flow(false);
            SetLayoutInputForAxis(m_height, m_height, 0f, 1);
        }

        /// <inheritdoc/>
        public override void SetLayoutHorizontal() => Flow(true);
        /// <inheritdoc/>
        public override void SetLayoutVertical() => Flow(true);

        private float Flow(bool apply)
        {
            float width = rectTransform.rect.width - padding.horizontal;
            float x = 0f, y = padding.top, line = 0f;
            foreach (var c in rectChildren)
            {
                float cw = Mathf.Min(LayoutUtility.GetPreferredWidth(c), Mathf.Max(1f, width)), ch = LayoutUtility.GetPreferredHeight(c);
                if (x > 0f && x + cw > width) { x = 0f; y += line + Spacing.y; line = 0f; }
                if (apply) { SetChildAlongAxis(c, 0, padding.left + x, cw); SetChildAlongAxis(c, 1, y, ch); }
                x += cw + Spacing.x; line = Mathf.Max(line, ch);
            }
            return y + line + padding.bottom;
        }
    }
}
