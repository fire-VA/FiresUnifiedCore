using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using FiresCore.UI;
using FiresCore.UI.Shell.Kit;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.Storage.Patches
{
    /// <summary>
    /// The "Tags" panel (design §16). Dormant namespace: exists only while Storage Features is on.
    /// With a container open: a small "Tags" button on the container panel; it opens a panel beside it with three sections:
    ///   - TAGS: the chest's tags as chips (x removes one), a text field (comma-separated categories, drop types, item names;
    ///     "!" or "no " excludes), and Save (sent to the chest's owner, ChestTags.RequestSet) / From contents / Clear
    ///   - HOPPER (hoppers, or admins): Pulls All | Inside | Tags, Sort into chests, Radius, and the admin Make / Remove hopper
    ///   - OPTIONS: Sign labels, Locked
    /// Kit look (FiresTokens colours, rounded plates); help text only behind each section's "?".
    /// Built once, lazily, on the container panel; hidden whenever no container is open or the player lacks access.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), "Update")]
    internal static class ChestTagsPanel
    {
        internal static readonly AccessTools.FieldRef<InventoryGui, Container> CurrentContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");

        private static Button s_open;
        internal static Button OpenButton => s_open;   // CraftingSkin pills it
        private static GameObject s_panel;
        private static RectTransform s_body;
        private static TextMeshProUGUI s_title, s_subtitle;
        private static TextMeshProUGUI s_label;   // empty-state line, shown when there are no chips
        private static TMP_InputField s_input;
        private static Container s_shownFor;
        private static RectTransform s_tagsSection, s_hopperSection, s_optionsSection;
        private static GameObject s_hopperRow;   // Pulls / Sort / Radius, shown only for a hopper
        private static Button s_sort;
        private static Button s_adminHopper;
        private static TextMeshProUGUI s_adminNote;
        private static readonly Button[] s_modeSegments = new Button[3];
        private static readonly HopperChest.Mode[] SegmentModes = { HopperChest.Mode.All, HopperChest.Mode.Inside, HopperChest.Mode.Tags };
        private static TextMeshProUGUI s_radius;
        private static GameObject s_signsRow;
        private static Button s_signs;
        private static Button s_autoLock;
        private static RectTransform s_buttonRow, s_pullsRow, s_sortRow, s_radiusRow, s_lockRow;
        private static RectTransform s_tip;
        private static TextMeshProUGUI s_tipText;

        // chips are rebuilt from the shown tags on every Refresh / Send; x on one sends the rest
        private static readonly List<(RectTransform rt, float w)> s_chips = new List<(RectTransform, float)>();
        private static List<ChestTags.Tag> s_chipTags = new List<ChestTags.Tag>();
        // every leaf element, for the layout check (containers would overlap their own children)
        private static readonly List<RectTransform> s_leaves = new List<RectTransform>();

        private const float TagsButtonWidth = 72f, TagsButtonFallbackHeight = 26f, TagsButtonGap = 8f;
        private const float TagsButtonMinStackedHeight = 22f, TagsButtonStackedHeightScale = 0.7f, TagsButtonStackedDrop = 4f;
        private const float PanelGap = 8f;

        // One type scale
        private const float TitleSize = 20f, HeaderSize = 12f, BodySize = 13f, SmallSize = 12f, NoteSize = 11f;
        // Panel and section geometry (mockup CSS)
        private const float PanelWidth = 360f, PadSide = 16f, PadTop = 14f, PadBottom = 14f, ContentWidth = PanelWidth - PadSide * 2f;
        private const float TitleHeight = 26f, TitleGap = 2f, SubtitleHeight = 16f, SubtitleGap = 12f, SectionGap = 10f;
        private const float DividerHeight = 1f, SectionPadTop = 10f, HeaderHeight = 18f, HeaderGap = 8f;
        private const float SectionHeadHeight = DividerHeight + SectionPadTop + HeaderHeight + HeaderGap;
        private const float HelpSize = 18f, HelpGap = 8f;
        private const float ChipHeight = 22f, ChipGap = 6f, ChipPadX = 9f, ChipCloseGap = 6f, ChipRadius = 12f, ChipsAfterGap = 8f;
        private const float NotBorderAlpha = 0.6f;   // the "!" chip's border: Danger, softened like the mockup's
        private const float EmptyHeight = 16f;
        private const float InputHeight = 32f, InputPadX = 9f, InputPadY = 1f, ButtonHeight = 30f;
        private const float OptRowHeight = 32f, SegmentHeight = 24f, SegmentPadX = 10f;
        private const float ToggleWidth = 38f, ToggleKnobInset = 3f;
        private const float StepButtonWidth = 26f, StepValueWidth = 38f, StepHeight = 24f, StepGap = 6f;
        private const float StepperWidth = StepButtonWidth * 2f + StepValueWidth + StepGap * 2f;
        private const float NoteGap = 2f, NoteHeight = 16f, NoteIndent = 4f;
        private const float TipWidth = 250f, TipPadX = 10f, TipPadY = 8f, TipGap = 4f;
        private const int BorderPx = 1;
        private const string Subtitle = "What belongs in this chest";
        private const string HopperHelp = "A hopper pulls dropped items within its radius into this chest, then sorts them into your other chests by their tags.";
        private const string LockedLabel = "Locked";
        private const string LockedNote = "(no hoppers, crafting or companions)";

        private static void RefreshSigns(Container c)
        {
            if (s_signs == null) return;
            s_signsRow.SetActive(SignLabels.On);
            if (c != null) SetToggle(s_signs, SignLabels.ChestWants(c));
            Relayout();
        }

        internal static bool Typing => s_input != null && s_input.isFocused;

        private static void Postfix(InventoryGui __instance)
        {
            Container c = CurrentContainer(__instance);
            bool show = StorageFeatures.Feature(StorageFeatures.Part.Tags) && InventoryGui.IsVisible() && c != null && Player.m_localPlayer != null
                && ContainerRegistry.CheckAccessFn(c, Player.m_localPlayer.GetPlayerID());
            if (!show) { if (s_open != null) s_open.gameObject.SetActive(false); if (s_panel != null) s_panel.SetActive(false); s_shownFor = null; return; }
            if (s_open == null) Build(__instance);
            if (s_open == null) return;
            s_open.gameObject.SetActive(true);
            PlaceBesideStackButton(__instance);
            if (s_panel != null && s_panel.activeSelf) ClampToScreen();   // the chest panel can move or sit low: keep the whole popup on screen
            if (s_panel.activeSelf && s_shownFor != c) Refresh(c);   // a different chest opened while the panel was up
        }

        // The button goes in the header row, 8 px right of Place stacks, same
        // vertical centre, wherever VAInventory lays the row out; vanilla's top-right spot only when there is no Place stacks button.
        // Fire 10-23: on a low chest the popup ran off the bottom (Locked row clipped). It still sits beside the chest panel's top-right,
        // but is pushed up when it would overflow the bottom (never past the top) and left when it would overflow the right.
        private static Vector2 s_lastClamp;
        private static void ClampToScreen()
        {
            var pr = (RectTransform)s_panel.transform;
            pr.anchoredPosition = new Vector2(PanelGap, 0f);   // back to the beside-chest spot first
            var canvas = pr.GetComponentInParent<Canvas>()?.rootCanvas;
            if (canvas == null) return;
            var cr = (RectTransform)canvas.transform;
            var c = new Vector3[4]; pr.GetWorldCorners(c);
            var s = new Vector3[4]; cr.GetWorldCorners(s);
            Vector3 d = Vector3.zero;
            float m = 8f * cr.lossyScale.x;
            if (c[0].y < s[0].y + m) d.y = s[0].y + m - c[0].y;
            if (c[1].y + d.y > s[1].y - m) d.y = s[1].y - m - c[1].y;
            if (c[2].x > s[2].x - m) d.x = s[2].x - m - c[2].x;
            if (d != Vector3.zero) pr.position += d;
            Vector2 shift = new Vector2(d.x, d.y);
            if ((shift - s_lastClamp).sqrMagnitude > 1f) { s_lastClamp = shift; FiresLogger.LogDebug($"[ChestTags] popup kept on screen: shifted {d.x / cr.lossyScale.x:0} px x, {d.y / cr.lossyScale.x:0} px y"); }
        }
        private static void PlaceBesideStackButton(InventoryGui gui)
        {
            var sb = gui != null && gui.m_stackAllButton != null && gui.m_stackAllButton.gameObject.activeInHierarchy ? (RectTransform)gui.m_stackAllButton.transform : null;
            var br = (RectTransform)s_open.transform;
            var parent = br.parent as RectTransform;
            if (sb == null || parent == null) return;
            var corners = new Vector3[4]; sb.GetWorldCorners(corners);   // 0 BL, 1 TL, 2 TR, 3 BR
            float h = sb.rect.height > 1f ? sb.rect.height : TagsButtonFallbackHeight;
            bool unified = false; try { unified = FiresCore.UI.Shell.FiresShell.OverrideOn; } catch { }
            Vector2 want;
            if (unified)
            {
                // unified: in the header row, 8 px right of Place stacks, same height (VAInventory leaves 72 px for it)
                Vector2 local = parent.InverseTransformPoint((corners[2] + corners[3]) * 0.5f);
                br.anchorMin = br.anchorMax = new Vector2(0.5f, 0.5f); br.pivot = new Vector2(0f, 0.5f);
                br.sizeDelta = new Vector2(TagsButtonWidth, h);
                want = local - parent.rect.center + new Vector2(TagsButtonGap, 0f);
            }
            else
            {
                // Non-unified: the tags button sits in the chest UI on the right edge just under the
                // Place stacks button, styled like that vanilla button (orange text and border, darker pill): right-aligned under
                // Place stacks, 4 px below it, a smaller copy of its look (sprite, colours, font)
                Vector2 local = parent.InverseTransformPoint(corners[3]);   // Place stacks' bottom-right
                br.anchorMin = br.anchorMax = new Vector2(0.5f, 0.5f); br.pivot = new Vector2(1f, 1f);
                br.sizeDelta = new Vector2(TagsButtonWidth, Mathf.Max(TagsButtonMinStackedHeight, h * TagsButtonStackedHeightScale));
                want = local - parent.rect.center + new Vector2(0f, -TagsButtonStackedDrop);
                if (!s_vanillaLook) CopyVanillaLook(gui.m_stackAllButton);
            }
            if (unified) s_vanillaLook = false;
            if ((br.anchoredPosition - want).sqrMagnitude > 0.25f) br.anchoredPosition = want;
        }

        private static bool s_vanillaLook;
        // the non-unified look: Place stacks' own face (sprite, type, colour) and label (font, colour) on the Tags button
        private static void CopyVanillaLook(Button src)
        {
            if (src == null || s_open == null) return;
            var si = src.targetGraphic as Image ?? src.GetComponent<Image>();
            var di = s_open.targetGraphic as Image ?? s_open.GetComponent<Image>();
            if (si != null && di != null) { di.sprite = si.sprite; di.type = si.type; di.color = si.color; di.material = si.material; di.canvasRenderer.SetColor(Color.white); }
            s_open.transition = src.transition; s_open.colors = src.colors;
            var st = src.GetComponentInChildren<TMP_Text>(true); var dt = s_open.GetComponentInChildren<TMP_Text>(true);
            if (st != null && dt != null) { dt.font = st.font; dt.fontSharedMaterial = st.fontSharedMaterial; dt.color = st.color; dt.fontStyle = st.fontStyle; dt.enableAutoSizing = true; dt.fontSizeMax = st.fontSize * 0.8f; dt.fontSizeMin = 10f; }
            s_vanillaLook = true;
        }

        private static void Build(InventoryGui gui)
        {
            RectTransform host = gui.m_container;
            if (host == null) return;
            s_open = UIBuilderHelper.CreateButton(host, "Tags", new Vector2(1f, 1f), new Vector2(1f, 1f), () =>
            {
                s_panel.SetActive(!s_panel.activeSelf);
                if (s_panel.activeSelf) Refresh(CurrentContainer(InventoryGui.instance));
            });
            var br = (RectTransform)s_open.transform;
            br.sizeDelta = new Vector2(64f, 26f); br.anchoredPosition = new Vector2(-40f, -16f);

            s_panel = UIBuilderHelper.CreatePanel(host, "CoreChestTagsPanel", new Vector2(1f, 0f), new Vector2(1f, 1f));
            var pr = (RectTransform)s_panel.transform;
            pr.pivot = new Vector2(0f, 1f); pr.anchoredPosition = new Vector2(PanelGap, 0f); pr.sizeDelta = new Vector2(PanelWidth, 0f);
            var bg = s_panel.GetComponent<Image>() ?? s_panel.AddComponent<Image>();
            SetPlate(bg, Plate(FiresTokens.Radius, FiresTokens.PanelRaised, FiresTokens.Edge));
            // CraftingSkin walks the container panel: the plate (KitLeaf) and everything under the NoKitSkin body keep this look
            if (s_panel.GetComponent<KitLeaf>() == null) s_panel.AddComponent<KitLeaf>();
            s_body = NewRect(s_panel.transform, "Body_NoKitSkin");
            s_body.anchorMin = Vector2.zero; s_body.anchorMax = Vector2.one; s_body.offsetMin = Vector2.zero; s_body.offsetMax = Vector2.zero;

            s_title = Leaf(Label(s_body, "Title", "Chest tags", TitleSize, FiresTokens.Gold, TextAlignmentOptions.Center));
            s_title.fontStyle = FontStyles.Bold;
            s_subtitle = Leaf(Label(s_body, "Subtitle", Subtitle, SmallSize, FiresTokens.TextMuted, TextAlignmentOptions.Center));

            BuildTagsSection();
            BuildHopperSection();
            BuildOptionsSection();
            BuildTooltip();
            Relayout();
            s_panel.SetActive(false);
            FiresLogger.LogDebug($"[Storage] chest tags panel built on '{host.name}' (kit plate, {s_leaves.Count} element(s))");
        }

        private static void BuildTagsSection()
        {
            s_tagsSection = Section("TagsSection", "TAGS",
                "Categories: " + string.Join(", ", ChestTags.Categories) + ". Drops: " + string.Join(", ", ChestTags.DropTypes) + ". Or item names; ! excludes.");
            s_label = Leaf(Label(s_tagsSection, "Current", "", SmallSize, FiresTokens.TextFaint, TextAlignmentOptions.MidlineLeft));
            s_input = UIBuilderHelper.CreateInputField(s_tagsSection, "TagsInput", "Add a tag: Ores, Wood, !Copper, Boss drops…", Vector2.zero, Vector2.zero, (int)BodySize);
            StyleInput(s_input);
            Leaf((RectTransform)s_input.transform);
            s_buttonRow = NewRect(s_tagsSection, "Actions");
            StyleButton(Leaf(UIBuilderHelper.CreateButton(s_buttonRow, "Save", Vector2.zero, Vector2.zero, Save)), true);
            StyleButton(Leaf(UIBuilderHelper.CreateButton(s_buttonRow, "From contents", Vector2.zero, Vector2.zero, FromContents)), false);
            StyleButton(Leaf(UIBuilderHelper.CreateButton(s_buttonRow, "Clear", Vector2.zero, Vector2.zero, () => Send(new List<ChestTags.Tag>()))), false);
        }

        // Pulls / Sort / Radius write the open chest's ZDO (the opener owns it)
        private static void BuildHopperSection()
        {
            s_hopperSection = Section("HopperSection", "HOPPER", HopperHelp);
            s_hopperRow = NewRect(s_hopperSection, "HopperRow").gameObject;

            s_pullsRow = OptionRow(s_hopperRow.transform, "Pulls", "Pulls");
            var seg = NewRect(s_pullsRow, "Segments");
            var segImg = seg.gameObject.AddComponent<Image>();
            SetPlate(segImg, Plate(FiresTokens.RadiusSmall, Color.clear, FiresTokens.Edge));
            segImg.raycastTarget = false;
            float sx = BorderPx;
            for (int i = 0; i < SegmentModes.Length; i++)
            {
                HopperChest.Mode mode = SegmentModes[i];
                var b = UIBuilderHelper.CreateButton(seg, mode.ToString(), Vector2.zero, Vector2.zero, () => HopperDo(h => h.SetMode(mode)));
                var img = (Image)b.targetGraphic;
                SetPlate(img, Plate(FiresTokens.RadiusSmall - BorderPx, FiresTokens.Selected, null));
                b.transition = Selectable.Transition.None;
                var t = b.GetComponentInChildren<TMP_Text>();
                StyleText(t, SmallSize, FiresTokens.TextMuted, TextAlignmentOptions.Center);
                float w = Mathf.Ceil(t.GetPreferredValues(t.text).x) + SegmentPadX * 2f;
                Place((RectTransform)b.transform, sx, BorderPx, w, SegmentHeight - BorderPx * 2f);
                sx += w;
                s_modeSegments[i] = Leaf(b);
            }
            Place(seg, ContentWidth - (sx + BorderPx), (OptRowHeight - SegmentHeight) * 0.5f, sx + BorderPx, SegmentHeight);
            SizeRowLabel(s_pullsRow, sx + BorderPx);

            s_sortRow = OptionRow(s_hopperRow.transform, "Sort", "Sort into chests");
            s_sort = Toggle(s_sortRow, "SortToggle", () => HopperDo(h => h.SetSort(!h.SortOn)));   // the auto-sorter switch (default On)
            SizeRowLabel(s_sortRow, ToggleWidth);

            s_radiusRow = OptionRow(s_hopperRow.transform, "Radius", "Radius");
            float rx = ContentWidth - StepperWidth, ry = (OptRowHeight - StepHeight) * 0.5f;
            var minus = UIBuilderHelper.CreateButton(s_radiusRow, "-", Vector2.zero, Vector2.zero, () => HopperDo(h => h.SetRadius(h.Radius - 1f)));
            StyleButton(Leaf(minus), false);
            Place((RectTransform)minus.transform, rx, ry, StepButtonWidth, StepHeight);
            s_radius = Leaf(Label(s_radiusRow, "RadiusValue", "4 m", BodySize, FiresTokens.Text, TextAlignmentOptions.Center));
            Place(s_radius.rectTransform, rx + StepButtonWidth + StepGap, ry, StepValueWidth, StepHeight);
            var plus = UIBuilderHelper.CreateButton(s_radiusRow, "+", Vector2.zero, Vector2.zero, () => HopperDo(h => h.SetRadius(h.Radius + 1f)));
            StyleButton(Leaf(plus), false);
            Place((RectTransform)plus.transform, ContentWidth - StepButtonWidth, ry, StepButtonWidth, StepHeight);
            SizeRowLabel(s_radiusRow, StepperWidth);
            s_hopperRow.SetActive(false);

            // Admins turn any open container into a hopper or back
            s_adminHopper = UIBuilderHelper.CreateButton(s_hopperSection, "Make hopper", Vector2.zero, Vector2.zero, () =>
            {
                Container c = CurrentContainer(InventoryGui.instance);
                if (c == null) return;
                bool admin = false;
                try { admin = FiresCore.Sync.AdminSyncing.IsLocalAdmin(); } catch { }
                if (!admin) return;
                HopperChest.SetFlag(c, c.GetComponent<HopperChest>() == null);
                RefreshHopper(c);
            });
            StyleButton(Leaf(s_adminHopper), false);
            s_adminHopper.gameObject.SetActive(false);
            s_adminNote = Leaf(Label(s_hopperSection, "AdminNote", "Admin only", NoteSize, FiresTokens.TextFaint, TextAlignmentOptions.MidlineLeft));
            s_adminNote.gameObject.SetActive(false);
            s_hopperSection.gameObject.SetActive(false);
        }

        private static void BuildOptionsSection()
        {
            s_optionsSection = Section("OptionsSection", "OPTIONS", null);
            // per-chest sign label toggle (the opener owns the open chest)
            var signs = OptionRow(s_optionsSection, "Signs", "Sign labels");
            s_signsRow = signs.gameObject;
            s_signs = Toggle(signs, "SignsToggle", () =>
            {
                Container c = CurrentContainer(InventoryGui.instance);
                if (c == null) return;
                SignLabels.SetChestToggle(c, !SignLabels.ChestWants(c));
                RefreshSigns(c);
            });
            SizeRowLabel(signs, ToggleWidth);
            // The Locked tag - automation (hoppers, crafting, companions, Deposit All) leaves this chest alone
            s_lockRow = OptionRow(s_optionsSection, "Locked",
                LockedLabel + " <size=" + NoteSize.ToString("0") + "><color=" + FiresTokens.RichHex(FiresTokens.TextFaint) + ">" + LockedNote + "</color></size>");
            s_autoLock = Toggle(s_lockRow, "LockedToggle", () =>
            {
                Container c = CurrentContainer(InventoryGui.instance);
                if (c == null) return;
                ChestTags.SetAutoLock(c, !ChestTags.IsAutoLocked(c));
                RefreshLock(c);
            });
            SizeRowLabel(s_lockRow, ToggleWidth);
        }

        // One tooltip for every "?": shown on hover, under the bubble, right-aligned to it
        private static void BuildTooltip()
        {
            s_tip = NewRect(s_body, "HelpTip");
            var img = s_tip.gameObject.AddComponent<Image>();
            SetPlate(img, Plate(FiresTokens.TooltipRadius, FiresTokens.Panel, FiresTokens.Edge));
            img.raycastTarget = false;
            s_tipText = Label(s_tip, "Text", "", SmallSize, FiresTokens.Text, TextAlignmentOptions.TopLeft);
            s_tipText.textWrappingMode = TextWrappingModes.Normal;
            var tr = s_tipText.rectTransform;
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(TipPadX, TipPadY); tr.offsetMax = new Vector2(-TipPadX, -TipPadY);
            s_tip.gameObject.SetActive(false);
        }

        private static void ShowTip(RectTransform bubble, string text)
        {
            if (s_tip == null || bubble == null) return;
            s_tipText.text = text;
            float h = Mathf.Ceil(s_tipText.GetPreferredValues(text, TipWidth - TipPadX * 2f, 0f).y) + TipPadY * 2f;
            s_tip.anchorMin = s_tip.anchorMax = new Vector2(0.5f, 0.5f); s_tip.pivot = new Vector2(1f, 1f);
            s_tip.sizeDelta = new Vector2(TipWidth, h);
            var w = new Vector3[4]; bubble.GetWorldCorners(w);   // 3 = bottom-right
            s_tip.position = w[3] + bubble.TransformVector(new Vector3(0f, -TipGap, 0f));
            s_tip.SetAsLastSibling();
            s_tip.gameObject.SetActive(true);
        }

        private static void HideTip() { if (s_tip != null) s_tip.gameObject.SetActive(false); }

        private sealed class HelpTip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            internal string Text;
            public void OnPointerEnter(PointerEventData e) => ShowTip((RectTransform)transform, Text);
            public void OnPointerExit(PointerEventData e) => HideTip();
            private void OnDisable() => HideTip();
        }

        // ---- private builders --------------------------------------------------------------------------

        // A section: 1 px divider, a letter-spaced gold header and, when there is help, a round "?" at its right
        private static RectTransform Section(string name, string header, string help)
        {
            var root = NewRect(s_body, name);
            var div = NewRect(root, "Divider");
            var di = div.gameObject.AddComponent<Image>();
            di.color = FiresTokens.Hairline; di.raycastTarget = false;
            Place(Leaf(div), 0f, 0f, ContentWidth, DividerHeight);
            float headerWidth = help != null ? ContentWidth - HelpSize - HelpGap : ContentWidth;
            var h = Leaf(Label(root, "Header", header, HeaderSize, FiresTokens.Gold, TextAlignmentOptions.MidlineLeft));
            h.fontStyle = FontStyles.Bold; h.characterSpacing = FiresTokens.SectionTracking;
            Place(h.rectTransform, 0f, DividerHeight + SectionPadTop, headerWidth, HeaderHeight);
            if (help != null)
            {
                var bubble = NewRect(root, "Help");
                var bi = bubble.gameObject.AddComponent<Image>();
                SetPlate(bi, Plate(HelpSize * 0.5f, Color.clear, FiresTokens.Edge));
                bi.raycastTarget = true;   // the hover target
                Label(bubble, "Mark", "?", NoteSize, FiresTokens.TextMuted, TextAlignmentOptions.Center).rectTransform.Stretch();
                bubble.gameObject.AddComponent<HelpTip>().Text = help;
                Place(Leaf(bubble), ContentWidth - HelpSize, DividerHeight + SectionPadTop, HelpSize, HelpSize);
            }
            return root;
        }

        // "label left / control right": the control is placed by the caller, then SizeRowLabel fits the label beside it
        private static RectTransform OptionRow(Transform parent, string name, string label)
        {
            var row = NewRect(parent, name + "Row");
            Leaf(Label(row, "Label", label, BodySize, FiresTokens.TextMuted, TextAlignmentOptions.MidlineLeft));
            return row;
        }

        private static void SizeRowLabel(RectTransform row, float controlWidth)
        {
            var l = row.Find("Label") as RectTransform;
            if (l != null) Place(l, 0f, 0f, ContentWidth - controlWidth - FiresTokens.RowGap, OptRowHeight);
        }

        // Pill switch: Well track, Edge border (Gold when on), knob left / faint (right / gold when on)
        private static Button Toggle(RectTransform row, string name, UnityEngine.Events.UnityAction onClick)
        {
            var track = NewRect(row, name);
            var img = track.gameObject.AddComponent<Image>();
            var b = track.gameObject.AddComponent<Button>();
            b.targetGraphic = img; b.transition = Selectable.Transition.None;
            b.onClick.AddListener(onClick);
            var knob = NewRect(track, "Knob");
            var ki = knob.gameObject.AddComponent<Image>();
            SetPlate(ki, Plate(FiresTokens.SwitchKnob * 0.5f, Color.white, null));
            ki.raycastTarget = false;
            knob.anchorMin = knob.anchorMax = new Vector2(0f, 0.5f); knob.pivot = new Vector2(0f, 0.5f);
            knob.sizeDelta = new Vector2(FiresTokens.SwitchKnob, FiresTokens.SwitchKnob);
            Place(Leaf(track), ContentWidth - ToggleWidth, (OptRowHeight - FiresTokens.SwitchHeight) * 0.5f, ToggleWidth, FiresTokens.SwitchHeight);
            SetToggle(b, false);
            return b;
        }

        private static void SetToggle(Button toggle, bool on)
        {
            if (toggle == null) return;
            SetPlate((Image)toggle.targetGraphic, Plate(FiresTokens.SwitchHeight * 0.5f, FiresTokens.Well, on ? FiresTokens.Gold : FiresTokens.Edge));
            var knob = toggle.transform.Find("Knob") as RectTransform;
            if (knob == null) return;
            knob.GetComponent<Image>().color = on ? FiresTokens.Gold : FiresTokens.TextFaint;
            knob.anchoredPosition = new Vector2(on ? ToggleWidth - ToggleKnobInset - FiresTokens.SwitchKnob : ToggleKnobInset, 0f);
        }

        private static void SetSegments(HopperChest.Mode current)
        {
            for (int i = 0; i < s_modeSegments.Length; i++)
            {
                var b = s_modeSegments[i];
                if (b == null) continue;
                bool on = SegmentModes[i] == current;
                b.targetGraphic.color = on ? Color.white : Color.clear;   // clear keeps the segment clickable
                var t = b.GetComponentInChildren<TMP_Text>();
                if (t != null) t.color = on ? FiresTokens.Gold : FiresTokens.TextMuted;
            }
        }

        private static void RebuildChips(List<ChestTags.Tag> tags)
        {
            foreach (var (rt, _) in s_chips) { if (rt == null) continue; rt.gameObject.SetActive(false); Object.Destroy(rt.gameObject); }
            s_chips.Clear();
            s_chipTags = new List<ChestTags.Tag>(tags);
            for (int i = 0; i < tags.Count; i++)
            {
                bool not = tags[i].Not;
                var chip = NewRect(s_tagsSection, "Chip_" + i);
                chip.SetSiblingIndex(s_label.transform.GetSiblingIndex());
                var img = chip.gameObject.AddComponent<Image>();
                SetPlate(img, Plate(ChipRadius, FiresTokens.Well, not ? FiresTokens.WithAlpha(FiresTokens.Danger, NotBorderAlpha) : FiresTokens.Edge));
                img.raycastTarget = false;
                string text = (not ? "!" : "") + tags[i].Value;
                var label = Label(chip, "Text", text, SmallSize, not ? FiresTokens.Danger : FiresTokens.Text, TextAlignmentOptions.MidlineLeft);
                float lw = Mathf.Ceil(label.GetPreferredValues(text).x) + 1f;
                int index = i;
                var x = UIBuilderHelper.CreateButton(chip, FiresTokens.GlyphClose, Vector2.zero, Vector2.zero, () =>
                {
                    var rest = new List<ChestTags.Tag>(s_chipTags);
                    if (index < rest.Count) rest.RemoveAt(index);
                    Send(rest);
                });
                x.transition = Selectable.Transition.None;
                x.targetGraphic.color = Color.clear;   // invisible hit area, the glyph shows
                var xt = x.GetComponentInChildren<TMP_Text>();
                StyleText(xt, SmallSize, FiresTokens.TextFaint, TextAlignmentOptions.Center);
                float xw = Mathf.Ceil(xt.GetPreferredValues(FiresTokens.GlyphClose).x) + 1f;
                float maxLabel = ContentWidth - ChipPadX * 2f - ChipCloseGap - xw;
                if (lw > maxLabel) { lw = maxLabel; label.overflowMode = TextOverflowModes.Ellipsis; }
                Place(label.rectTransform, ChipPadX, 0f, lw, ChipHeight);
                Place((RectTransform)x.transform, ChipPadX + lw + ChipCloseGap, 0f, xw, ChipHeight);
                s_chips.Add((chip, ChipPadX * 2f + lw + ChipCloseGap + xw));
            }
        }

        private static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static TextMeshProUGUI Label(Transform parent, string name, string text, float size, Color color, TextAlignmentOptions align)
        {
            var t = UIBuilderHelper.CreateLabel(parent, name, text, size, color, Vector2.zero, Vector2.zero, align);
            StyleText(t, size, color, align);
            return t;
        }

        private static void StyleText(TMP_Text t, float size, Color color, TextAlignmentOptions align)
        {
            if (t == null) return;
            t.fontSize = size; t.color = color; t.alignment = align; t.fontStyle = FontStyles.Normal;
            t.enableAutoSizing = false; t.textWrappingMode = TextWrappingModes.NoWrap; t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
        }

        // The one button plate: PanelRaised face, Edge border (Gold border and text for the primary), Selected face on hover
        private static void StyleButton(Button b, bool primary)
        {
            Color border = primary ? FiresTokens.Gold : FiresTokens.Edge;
            var img = (Image)b.targetGraphic;
            var normal = Plate(FiresTokens.RadiusSmall, FiresTokens.PanelRaised, border);
            var hover = Plate(FiresTokens.RadiusSmall, FiresTokens.Selected, border);
            SetPlate(img, normal);
            b.transition = Selectable.Transition.SpriteSwap;
            b.spriteState = new SpriteState { highlightedSprite = hover, pressedSprite = hover, selectedSprite = normal, disabledSprite = normal };
            StyleText(b.GetComponentInChildren<TMP_Text>(), BodySize, primary ? FiresTokens.Gold : FiresTokens.Text, TextAlignmentOptions.Center);
        }

        private static void StyleInput(TMP_InputField input)
        {
            SetPlate((Image)input.targetGraphic ?? input.GetComponent<Image>(), Plate(FiresTokens.RadiusSmall, FiresTokens.Well, FiresTokens.Edge));
            if (input.textViewport != null) { input.textViewport.offsetMin = new Vector2(InputPadX, InputPadY); input.textViewport.offsetMax = new Vector2(-InputPadX, -InputPadY); }
            if (input.textComponent != null) { input.textComponent.color = FiresTokens.Text; input.textComponent.alignment = TextAlignmentOptions.MidlineLeft; }
            if (input.placeholder is TMP_Text ph) { ph.color = FiresTokens.TextFaint; ph.fontStyle = FontStyles.Normal; ph.alignment = TextAlignmentOptions.MidlineLeft; }
            input.caretColor = FiresTokens.Text;
            input.selectionColor = FiresTokens.WithAlpha(FiresTokens.Gold, FiresTokens.LearnedFillAlpha);
        }

        private static Sprite Plate(float radius, Color fill, Color? border) =>
            FiresRoundedSkin.RoundedSprite(Mathf.RoundToInt(radius), fill, border, border.HasValue ? BorderPx : 0);

        private static void SetPlate(Image img, Sprite s)
        {
            if (img == null) return;
            img.sprite = s; img.type = Image.Type.Sliced; img.color = Color.white; img.material = null; img.pixelsPerUnitMultiplier = 1f;
        }

        private static RectTransform Leaf(RectTransform rt) { s_leaves.Add(rt); return rt; }
        private static TextMeshProUGUI Leaf(TextMeshProUGUI t) { s_leaves.Add(t.rectTransform); return t; }
        private static Button Leaf(Button b) { s_leaves.Add((RectTransform)b.transform); return b; }

        // ---- layout --------------------------------------------------------------------------------------

        // Fixed-pixel stack: every visible row an absolute height, the panel as tall as the rows it shows
        // (chips wrap and sections hide, so this runs on every refresh)
        private static void Relayout()
        {
            if (s_panel == null) return;
            float y = PadTop;
            Place(s_title.rectTransform, PadSide, y, ContentWidth, TitleHeight); y += TitleHeight + TitleGap;
            Place(s_subtitle.rectTransform, PadSide, y, ContentWidth, SubtitleHeight); y += SubtitleHeight + SubtitleGap;
            y = PlaceSection(s_tagsSection, y, LayoutTags());
            if (s_hopperSection.gameObject.activeSelf) y = PlaceSection(s_hopperSection, y + SectionGap, LayoutHopper());
            y = PlaceSection(s_optionsSection, y + SectionGap, LayoutOptions());
            y += PadBottom;
            var pr = (RectTransform)s_panel.transform;
            pr.anchorMin = new Vector2(1f, 1f); pr.anchorMax = new Vector2(1f, 1f); pr.pivot = new Vector2(0f, 1f);
            pr.anchoredPosition = new Vector2(PanelGap, 0f); pr.sizeDelta = new Vector2(PanelWidth, y); ClampToScreen();
        }

        private static float PlaceSection(RectTransform section, float top, float contentHeight)
        {
            float h = SectionHeadHeight + contentHeight;
            Place(section, PadSide, top, ContentWidth, h);
            return top + h;
        }

        // chips flow left to right and wrap; then the field and the three equal buttons
        private static float LayoutTags()
        {
            float y = SectionHeadHeight;
            bool any = s_chips.Count > 0;
            s_label.gameObject.SetActive(!any);
            if (any)
            {
                float x = 0f;
                foreach (var (rt, w) in s_chips)
                {
                    if (x > 0f && x + w > ContentWidth) { x = 0f; y += ChipHeight + ChipGap; }
                    Place(rt, x, y, w, ChipHeight);
                    x += w + ChipGap;
                }
                y += ChipHeight + ChipsAfterGap;
            }
            else { Place(s_label.rectTransform, 0f, y, ContentWidth, EmptyHeight); y += EmptyHeight + ChipsAfterGap; }
            Place((RectTransform)s_input.transform, 0f, y, ContentWidth, InputHeight); y += InputHeight + FiresTokens.RowGap;
            Place(s_buttonRow, 0f, y, ContentWidth, ButtonHeight);
            float bw = (ContentWidth - FiresTokens.RowGap * 2f) / 3f;
            for (int i = 0; i < s_buttonRow.childCount; i++) Place((RectTransform)s_buttonRow.GetChild(i), i * (bw + FiresTokens.RowGap), 0f, bw, ButtonHeight);
            return y + ButtonHeight - SectionHeadHeight;
        }

        private static float LayoutHopper()
        {
            float y = SectionHeadHeight;
            if (s_hopperRow.activeSelf)
            {
                Place((RectTransform)s_hopperRow.transform, 0f, y, ContentWidth, OptRowHeight * 3f);
                Place(s_pullsRow, 0f, 0f, ContentWidth, OptRowHeight);
                Place(s_sortRow, 0f, OptRowHeight, ContentWidth, OptRowHeight);
                Place(s_radiusRow, 0f, OptRowHeight * 2f, ContentWidth, OptRowHeight);
                y += OptRowHeight * 3f;
                if (s_adminHopper.gameObject.activeSelf) y += FiresTokens.RowGap;
            }
            if (s_adminHopper.gameObject.activeSelf)
            {
                Place((RectTransform)s_adminHopper.transform, 0f, y, ContentWidth, ButtonHeight); y += ButtonHeight + NoteGap;
                Place(s_adminNote.rectTransform, NoteIndent, y, ContentWidth - NoteIndent, NoteHeight); y += NoteHeight;
            }
            return y - SectionHeadHeight;
        }

        private static float LayoutOptions()
        {
            float y = SectionHeadHeight;
            if (s_signsRow.activeSelf) { Place((RectTransform)s_signsRow.transform, 0f, y, ContentWidth, OptRowHeight); y += OptRowHeight; }
            Place(s_lockRow, 0f, y, ContentWidth, OptRowHeight); y += OptRowHeight;
            return y - SectionHeadHeight;
        }

        // top-left anchored, absolute size, y down from the parent's top
        private static void Place(RectTransform rt, float x, float top, float width, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(width, height); rt.anchoredPosition = new Vector2(x, -top);
        }

        private static void Stretch(this RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        // ---- refresh -------------------------------------------------------------------------------------

        private static void Refresh(Container c)
        {
            if (c != null && c != s_shownFor)
            {
                // Which container this is to Core, every time one is opened (proves "plain chest" vs "flag set but not attached")
                Vector3 p = c.transform.position;
                FiresLogger.LogInfo($"[Storage] opened '{c.m_name}' at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0}): hopper flag {(HopperChest.IsHopper(c) ? "yes" : "no")}, hopper component {(c.GetComponent<HopperChest>() != null ? "yes" : "no")}");
            }
            s_shownFor = c;
            var tags = ChestTags.TagsOf(c);
            if (s_label != null) s_label.text = tags.Count > 0 ? ChestTags.Label(tags) : "(no tags: sort-into and companions treat it as today)";
            if (s_input != null) s_input.text = string.Join(", ", tags.Select(t => (t.Not ? "!" : "") + t.Value).ToArray());
            RebuildChips(tags);
            // signs and lock first: RefreshHopper runs the layout check, which must see their final visibility
            RefreshSigns(c);
            RefreshLock(c);
            RefreshHopper(c);
        }

        private static void RefreshLock(Container c)
        {
            if (s_autoLock == null) return;
            SetToggle(s_autoLock, ChestTags.IsAutoLocked(c));
        }

        private static void RefreshHopper(Container c)
        {
            HopperChest h = c != null ? c.GetComponent<HopperChest>() : null;
            // A container whose ZDO carries the flag but never got the behaviour (it loaded
            // before the flag was readable) gets it now
            if (h == null && c != null && HopperChest.IsHopper(c)) { HopperChest.TryAttach(c); h = c.GetComponent<HopperChest>(); }
            bool admin = false;
            if (s_adminHopper != null)
            {
                try { admin = FiresCore.Sync.AdminSyncing.IsLocalAdmin(); } catch { }
                s_adminHopper.gameObject.SetActive(admin && c != null);
                s_adminNote.gameObject.SetActive(admin && c != null);
                var t = s_adminHopper.GetComponentInChildren<TMP_Text>();
                if (t != null) t.text = h != null ? "Remove hopper" : "Make hopper";
            }
            if (s_hopperRow == null) return;
            s_hopperRow.SetActive(h != null);
            s_hopperSection.gameObject.SetActive(h != null || (admin && c != null));
            if (h != null)
            {
                SetSegments(h.CurrentMode);
                if (s_radius != null) s_radius.text = h.Radius.ToString("0") + " m";
                SetToggle(s_sort, h.SortOn);
                var sortLabel = s_sortRow != null ? s_sortRow.Find("Label")?.GetComponent<TMPro.TMP_Text>() : null;
                if (sortLabel != null) sortLabel.text = string.IsNullOrEmpty(h.SortScopeShort) ? "Sort into chests" : "Sorts into " + h.SortScopeShort;
            }
            Relayout();
            LogLayoutOnce();
        }

        // Drawn rects of every visible element (chips included), once per session (screen pixels), to prove
        // none overlaps another, each sits inside the background, and no single-line text is wider than its box
        private static bool s_layoutLogged;
        private static void LogLayoutOnce()
        {
            if (s_layoutLogged || s_panel == null || !s_panel.activeInHierarchy) return;
            s_layoutLogged = true;
            Rect bg = DrawnRect(s_panel.transform);
            Rect hr = s_hopperSection != null ? DrawnRect(s_hopperSection) : default, ab = s_adminHopper != null ? DrawnRect(s_adminHopper.transform) : default;
            var rects = new List<(string n, Rect r)>();
            var visible = new List<RectTransform>(s_leaves);
            foreach (var (rt, _) in s_chips) visible.Add(rt);
            visible.RemoveAll(t => t == null || !t.gameObject.activeInHierarchy);
            foreach (var t in visible) rects.Add((t.name, DrawnRect(t)));
            int overlaps = 0, outside = 0, clipped = 0; var bad = new List<string>();
            for (int i = 0; i < rects.Count; i++)
            {
                if (InsideOf(rects[i].r, bg) != "inside") { outside++; bad.Add(rects[i].n + " outside"); }
                for (int j = i + 1; j < rects.Count; j++)
                {
                    Rect a = rects[i].r, b = rects[j].r;
                    if (a.xMin < b.xMax - 1f && b.xMin < a.xMax - 1f && a.yMin < b.yMax - 1f && b.yMin < a.yMax - 1f) { overlaps++; bad.Add(rects[i].n + " x " + rects[j].n); }
                }
            }
            // the field's own text scrolls inside its viewport, so it is not a clip
            foreach (var t in visible)
                foreach (TMP_Text tx in t.GetComponentsInChildren<TMP_Text>(false))
                {
                    if (tx.textWrappingMode != TextWrappingModes.NoWrap || tx.GetComponentInParent<TMP_InputField>() != null) continue;
                    if (tx.preferredWidth > tx.rectTransform.rect.width + 0.5f) { clipped++; bad.Add(t.name + "/" + tx.name + " clipped"); }
                }
            FiresLogger.LogDebug($"[Storage] chest tags layout: background {bg.width:0}x{bg.height:0} at ({bg.xMin:0}, {bg.yMin:0}); {rects.Count} row(s), {overlaps} overlap(s), {outside} outside, {clipped} clipped{(bad.Count > 0 ? ": " + string.Join("; ", bad.ToArray()) : "")}; hopper row {hr.width:0}x{hr.height:0} {InsideOf(hr, bg)}; admin button {ab.width:0}x{ab.height:0} {InsideOf(ab, bg)}");
        }
        private static Rect DrawnRect(Transform t) { var rt = t as RectTransform; var w = new Vector3[4]; rt.GetWorldCorners(w); return new Rect(w[0].x, w[0].y, w[2].x - w[0].x, w[2].y - w[0].y); }
        private static string InsideOf(Rect r, Rect bg) => (r.xMin >= bg.xMin - 1f && r.xMax <= bg.xMax + 1f && r.yMin >= bg.yMin - 1f && r.yMax <= bg.yMax + 1f) ? "inside" : "OUTSIDE";

        private static void HopperDo(System.Action<HopperChest> act)
        {
            Container c = CurrentContainer(InventoryGui.instance);
            HopperChest h = c != null ? c.GetComponent<HopperChest>() : null;
            if (h == null) return;
            act(h);
            RefreshHopper(c);
        }

        private static void Save()
        {
            var tags = ChestTags.FromText(s_input != null ? s_input.text : "", out List<string> unknown);
            if (unknown.Count > 0) Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "Unknown tag(s) skipped: " + string.Join(", ", unknown.ToArray()));
            Send(tags);
        }

        private static void FromContents()
        {
            Container c = CurrentContainer(InventoryGui.instance);
            if (c == null) return;
            var names = c.GetInventory().GetAllItems().Where(i => i.m_dropPrefab != null).Select(i => i.m_dropPrefab.name).Distinct().Take(ChestTags.MaxTags);
            Send(names.Select(n => new ChestTags.Tag { Kind = 'i', Value = n }).ToList());
        }

        private static void Send(List<ChestTags.Tag> tags)
        {
            Container c = CurrentContainer(InventoryGui.instance);
            if (c == null) return;
            ChestTags.RequestSet(c, tags);
            if (s_label != null) s_label.text = tags.Count > 0 ? ChestTags.Label(tags) : "(no tags)";
            if (s_input != null) s_input.text = string.Join(", ", tags.Select(t => (t.Not ? "!" : "") + t.Value).ToArray());
            RebuildChips(tags);
            Relayout();
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, tags.Count > 0 ? "Chest tagged: " + ChestTags.Label(tags) : "Chest tags cleared");
        }
    }

    // Admin console command on the OPEN container (the opener owns it), else the
    // container under the crosshair if this peer owns it: fires_hopper on | off | radius N | filter all|inside|tags
    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    internal static class HopperCommand
    {
        private static bool s_done;
        private static void Postfix()
        {
            if (s_done) return;
            s_done = true;
            new Terminal.ConsoleCommand("fires_hopper",
                "[FiresUnifiedCore] fires_hopper on|off|radius N|filter all|inside|tags - (admin) make the open container (or the one you look at) a hopper, or set it up.",
                args =>
                {
                    bool admin = false;
                    try { admin = FiresCore.Sync.AdminSyncing.IsLocalAdmin(); } catch { }
                    if (!admin) { args.Context?.AddString("fires_hopper: admins only"); return; }
                    Container c = InventoryGui.instance != null && InventoryGui.IsVisible() ? ChestTagsPanel.CurrentContainer(InventoryGui.instance) : null;
                    if (c == null && Player.m_localPlayer != null) { var hov = Player.m_localPlayer.GetHoverObject(); c = hov != null ? hov.GetComponentInParent<Container>() : null; }
                    if (c == null) { args.Context?.AddString("fires_hopper: open a container or look at one"); return; }
                    string verb = args.Length > 1 ? args[1].ToLowerInvariant() : "on";
                    var h = c.GetComponent<HopperChest>();
                    switch (verb)
                    {
                        case "on": args.Context?.AddString(HopperChest.SetFlag(c, true) ? "fires_hopper: '" + c.m_name + "' is a hopper" : "fires_hopper: not yours - open it first"); break;
                        case "off": args.Context?.AddString(HopperChest.SetFlag(c, false) ? "fires_hopper: '" + c.m_name + "' is a plain container again" : "fires_hopper: not yours - open it first"); break;
                        case "radius":
                            if (h == null) { args.Context?.AddString("fires_hopper: not a hopper (fires_hopper on first)"); break; }
                            if (args.Length > 2 && float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r)) { h.SetRadius(r); args.Context?.AddString("fires_hopper: radius " + h.Radius.ToString("0") + " m"); }
                            else args.Context?.AddString("fires_hopper radius N (1.." + (StorageConfig.HopperMaxRadius != null ? StorageConfig.HopperMaxRadius.Value : 8f).ToString("0") + ")");
                            break;
                        case "filter":
                            if (h == null) { args.Context?.AddString("fires_hopper: not a hopper (fires_hopper on first)"); break; }
                            string f = args.Length > 2 ? args[2].ToLowerInvariant() : "";
                            if (f == "all") h.SetMode(HopperChest.Mode.All); else if (f == "inside") h.SetMode(HopperChest.Mode.Inside); else if (f == "tags") h.SetMode(HopperChest.Mode.Tags);
                            else { args.Context?.AddString("fires_hopper filter all|inside|tags"); break; }
                            args.Context?.AddString("fires_hopper: filter " + h.CurrentMode);
                            break;
                        default: args.Context?.AddString("fires_hopper on|off|radius N|filter all|inside|tags"); break;
                    }
                });
        }
    }}
