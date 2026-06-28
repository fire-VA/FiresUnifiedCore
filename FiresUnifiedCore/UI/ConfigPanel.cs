using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using FiresCore.Help;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    // The Fires config window, rebuilt as a real uGUI Canvas overlay (the same construction the help panel
    // uses) instead of fragile IMGUI. A ScreenSpaceOverlay canvas with a GraphicRaycaster renders
    // deterministically and handles clicks natively; a left sidebar groups mod -> section, the right pane
    // shows the selected section's settings as typed controls (toggle / slider / dropdown / input / keybind)
    // wired straight to the live ConfigEntry. Data comes from CfgDiscovery; input is gated by the shared
    // InputBlock (suppresses Player.TakeInput, pins the camera, frees the cursor) like every other Fires modal.
    public class ConfigPanel : MonoBehaviour
    {
        private const int OverlaySortingOrder = 350;
        private const float SidebarWidth = 230f;

        private static ConfigPanel _instance;
        private static bool _cmdRegistered;

        public static void EnsureHost()
        {
            if (_instance != null) return;
            var go = new GameObject("FiresConfigPanelHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<ConfigPanel>();
        }

        public static void Toggle()
        {
            EnsureHost();
            _instance.ToggleInternal();
        }

        public static bool IsOpen => _instance != null && _instance._root != null && _instance._root.activeSelf;

        // ----- instance -----
        private GameObject _root;
        private RectTransform _sideContent;
        private RectTransform _content;
        private ScrollRect _contentScroll;
        private TMP_InputField _searchField;
        private string _search = "";

        private string _mod;
        private string _section;

        private bool _capturing;
        private ConfigEntryBase _captureTarget;
        private TextMeshProUGUI _captureLabel;
        private string _captureRestore;

        private static readonly KeyCode[] s_modifiers =
        {
            KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift,
            KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr, KeyCode.LeftCommand, KeyCode.RightCommand,
        };

        private void Update()
        {
            if (!_cmdRegistered) { try { RegisterCommands(); _cmdRegistered = true; } catch { } }

            if (_capturing) { PollKeyCapture(); return; }

            bool toggle = FiresConfigUI.CfgHotkey != null
                ? FiresConfigUI.CfgHotkey.Value.IsDown()
                : UnityEngine.Input.GetKeyDown(FiresConfigUI.ToggleKey);
            if (toggle) ToggleInternal();

            if (IsOpen && UnityEngine.Input.GetKeyDown(KeyCode.Escape)) Hide();
        }

        private static void RegisterCommands()
        {
            new Terminal.ConsoleCommand("va_config", "Toggle the Fires configuration window.", _ => Toggle(), isCheat: false);
            new Terminal.ConsoleCommand("va_config_dump", "Log every discovered Fires config (N mods, M entries).", _ => CfgDiscovery.DumpToLog(), isCheat: false);
        }

        private void ToggleInternal() { if (IsOpen) Hide(); else Show(); }

        public void Show()
        {
            try
            {
                CfgDiscovery.Rebuild();
                if (_root == null) BuildPanel();
                _root.SetActive(true);
                _root.transform.SetAsLastSibling();
                RebuildSidebar();
                if (string.IsNullOrEmpty(_mod) || CfgDiscovery.SectionsOf(_mod).Count == 0) SelectFirst();
                Navigate(_mod, _section);
                InputBlock.Block(true);
                FiresConfigUI.Log.LogInfo($"ConfigPanel opened: {CfgDiscovery.ModNames.Count} mod(s), {CfgDiscovery.Descriptors.Count} entries.");
            }
            catch (Exception ex) { FiresConfigUI.Log.LogError("ConfigPanel.Show failed: " + ex); }
        }

        public void Hide()
        {
            _capturing = false; _captureTarget = null;
            if (_root != null) _root.SetActive(false);
            InputBlock.Block(false);
        }

        private void SelectFirst()
        {
            var mods = CfgDiscovery.ModNames;
            _mod = mods.Count > 0 ? mods[0] : null;
            var sections = _mod != null ? CfgDiscovery.SectionsOf(_mod) : null;
            _section = sections != null && sections.Count > 0 ? sections[0] : null;
        }

        // ---------------------------------------------------------------- panel scaffold
        private void BuildPanel()
        {
            _root = new GameObject("FiresConfigPanelRoot", typeof(RectTransform));
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = OverlaySortingOrder;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();
            Stretch(_root.GetComponent<RectTransform>());

            var backdrop = _root.AddComponent<Image>();
            backdrop.color = HelpTheme.Backdrop;
            var backBtn = _root.AddComponent<Button>();
            backBtn.targetGraphic = backdrop;
            var bc = backBtn.colors; bc.normalColor = bc.highlightedColor = bc.pressedColor = bc.selectedColor = HelpTheme.Backdrop; backBtn.colors = bc;
            backBtn.onClick.AddListener(Hide);

            var dialog = NewRect("Dialog", _root.transform);
            dialog.anchorMin = new Vector2(0.07f, 0.06f);
            dialog.anchorMax = new Vector2(0.93f, 0.94f);
            dialog.offsetMin = dialog.offsetMax = Vector2.zero;
            var dialogImg = dialog.gameObject.AddComponent<Image>();
            dialogImg.color = HelpTheme.PanelBg;
            dialog.gameObject.AddComponent<Button>().targetGraphic = dialogImg;   // eat backdrop clicks

            BuildTitleBar(dialog);
            BuildSidebar(dialog);
            BuildContent(dialog);
        }

        private void BuildTitleBar(RectTransform dialog)
        {
            var bar = NewRect("TitleBar", dialog);
            bar.anchorMin = new Vector2(0, 1); bar.anchorMax = new Vector2(1, 1); bar.pivot = new Vector2(0.5f, 1);
            bar.sizeDelta = new Vector2(0, 38);
            bar.gameObject.AddComponent<Image>().color = HelpTheme.SidebarBg;

            var title = Label(bar, "Fires Configuration", 15f, HelpTheme.HeaderColor, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            title.anchorMin = new Vector2(0, 0); title.anchorMax = new Vector2(0, 1); title.pivot = new Vector2(0, 0.5f);
            title.sizeDelta = new Vector2(260, 0); title.anchoredPosition = new Vector2(16, 0);

            // search box
            var search = NewRect("Search", bar);
            search.anchorMin = new Vector2(0, 0); search.anchorMax = new Vector2(0, 1); search.pivot = new Vector2(0, 0.5f);
            search.anchoredPosition = new Vector2(290, 0); search.sizeDelta = new Vector2(320, -10);
            var searchImg = search.gameObject.AddComponent<Image>(); searchImg.color = HelpTheme.CodeBg;
            var sTextRt = Label(search, "", 12f, HelpTheme.TextLight, TextAlignmentOptions.MidlineLeft);
            Stretch(sTextRt, 8, 4, 8, 4);
            var placeholder = Label(search, "search settings...", 12f, HelpTheme.TextMuted, TextAlignmentOptions.MidlineLeft);
            Stretch(placeholder, 8, 4, 8, 4);
            _searchField = search.gameObject.AddComponent<TMP_InputField>();
            _searchField.textComponent = sTextRt.GetComponent<TextMeshProUGUI>();
            _searchField.placeholder = placeholder.GetComponent<TextMeshProUGUI>();
            _searchField.text = "";
            _searchField.onValueChanged.AddListener(s => { _search = s ?? ""; Navigate(_mod, _section); });

            // close
            var close = NewRect("Close", bar);
            close.anchorMin = new Vector2(1, 0); close.anchorMax = new Vector2(1, 1); close.pivot = new Vector2(1, 0.5f);
            close.anchoredPosition = new Vector2(-4, 0); close.sizeDelta = new Vector2(34, -6);
            var closeImg = close.gameObject.AddComponent<Image>(); closeImg.color = new Color(0.5f, 0.15f, 0.1f, 0.85f);
            var closeBtn = close.gameObject.AddComponent<Button>(); closeBtn.targetGraphic = closeImg; closeBtn.onClick.AddListener(Hide);
            var cx = Label(close, "X", 13f, HelpTheme.TextGold, TextAlignmentOptions.Center, FontStyles.Bold);
            Stretch(cx);
        }

        private void BuildSidebar(RectTransform dialog)
        {
            var side = NewRect("Sidebar", dialog);
            side.anchorMin = new Vector2(0, 0); side.anchorMax = new Vector2(0, 1); side.pivot = new Vector2(0, 0.5f);
            side.sizeDelta = new Vector2(SidebarWidth, 0); side.offsetMin = new Vector2(0, 0); side.offsetMax = new Vector2(SidebarWidth, -38);
            side.gameObject.AddComponent<Image>().color = HelpTheme.SidebarBg;

            _sideContent = BuildScroll(side, out _, out _);
            var vlg = _sideContent.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(4, 4, 6, 6); vlg.spacing = 2;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            _sideContent.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private void BuildContent(RectTransform dialog)
        {
            var area = NewRect("ContentArea", dialog);
            area.anchorMin = Vector2.zero; area.anchorMax = Vector2.one;
            area.offsetMin = new Vector2(SidebarWidth, 0); area.offsetMax = new Vector2(0, -38);
            area.gameObject.AddComponent<Image>().color = HelpTheme.ContentBg;

            _content = BuildScroll(area, out _contentScroll, out _);
            var vlg = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 14, 10, 16); vlg.spacing = 4;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            _content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        // ---------------------------------------------------------------- sidebar nav
        private void RebuildSidebar()
        {
            if (_sideContent == null) return;
            for (int i = _sideContent.childCount - 1; i >= 0; i--) Destroy(_sideContent.GetChild(i).gameObject);

            foreach (var mod in CfgDiscovery.ModNames)
            {
                AddNavHeader(mod);
                foreach (var section in CfgDiscovery.SectionsOf(mod))
                {
                    string m = mod, s = section;
                    AddNavButton(CleanSection(section), mod == _mod && section == _section, () => { _search = ""; if (_searchField != null) _searchField.SetTextWithoutNotify(""); Navigate(m, s); });
                }
            }
        }

        private void AddNavHeader(string text)
        {
            var go = NewRect("Header", _sideContent);
            go.gameObject.AddComponent<LayoutElement>().minHeight = 24f;
            var t = Label(go, text, 11.5f, HelpTheme.HeaderColor, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            Stretch(t, 8, 0, 4, 0);
        }

        private void AddNavButton(string text, bool active, Action onClick)
        {
            var go = NewRect("Nav", _sideContent);
            go.gameObject.AddComponent<LayoutElement>().minHeight = 26f;
            var img = go.gameObject.AddComponent<Image>(); img.color = active ? HelpTheme.NavActive : HelpTheme.NavNormal;
            var btn = go.gameObject.AddComponent<Button>(); btn.targetGraphic = img;
            var c = btn.colors; c.normalColor = active ? HelpTheme.NavActive : HelpTheme.NavNormal; c.highlightedColor = HelpTheme.NavHover; c.pressedColor = HelpTheme.NavActive; c.selectedColor = c.normalColor; btn.colors = c;
            btn.onClick.AddListener(() => onClick());
            var t = Label(go, "  " + text, 11f, active ? HelpTheme.TextGold : HelpTheme.TextLight, TextAlignmentOptions.MidlineLeft);
            Stretch(t, 10, 0, 4, 0);
        }

        // ---------------------------------------------------------------- content
        private void Navigate(string mod, string section)
        {
            _mod = mod; _section = section;
            RebuildSidebar();
            if (_content == null) return;
            for (int i = _content.childCount - 1; i >= 0; i--) Destroy(_content.GetChild(i).gameObject);
            if (_contentScroll != null) _contentScroll.verticalNormalizedPosition = 1f;

            bool searching = !string.IsNullOrEmpty(_search);
            AddContentTitle(searching ? "Results for \"" + _search + "\"" : (mod == null ? "" : mod + "    " + CleanSection(section ?? "")));

            int shown = 0;
            string lastGroup = null;
            foreach (var d in CfgDiscovery.Descriptors)
            {
                if (searching)
                {
                    if (!Matches(d, _search)) continue;
                    string g = d.ModName + "  /  " + CleanSection(d.Section);
                    if (g != lastGroup) { lastGroup = g; AddContentTitle(g); }
                }
                else if (d.ModName != mod || d.Section != section) continue;

                try { BuildRow(d); shown++; }
                catch (Exception ex) { FiresConfigUI.Log.LogWarning($"row '{d.Section}/{d.Key}' failed: {ex.Message}"); }
            }
            if (shown == 0) AddContentTitle(searching ? "No matches." : "No settings.");
        }

        private void AddContentTitle(string text)
        {
            var go = NewRect("Title", _content);
            go.gameObject.AddComponent<LayoutElement>().minHeight = 26f;
            var t = Label(go, text, 13.5f, HelpTheme.HeaderColor, TextAlignmentOptions.BottomLeft, FontStyles.Bold);
            Stretch(t);
        }

        private void BuildRow(CfgDescriptor d)
        {
            var row = NewRect("Row", _content);
            row.gameObject.AddComponent<Image>().color = HelpTheme.NavNormal;
            var rowV = row.gameObject.AddComponent<VerticalLayoutGroup>();
            rowV.padding = new RectOffset(8, 8, 6, 6); rowV.spacing = 2;
            rowV.childControlWidth = rowV.childControlHeight = true;
            rowV.childForceExpandWidth = true; rowV.childForceExpandHeight = false;
            row.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var top = NewRect("Top", row);
            var topH = top.gameObject.AddComponent<HorizontalLayoutGroup>();
            topH.spacing = 8; topH.childControlWidth = true; topH.childControlHeight = true;
            topH.childForceExpandWidth = false; topH.childForceExpandHeight = false; topH.childAlignment = TextAnchor.MiddleLeft;
            top.gameObject.AddComponent<LayoutElement>().minHeight = 26f;

            var nameT = Label(top, d.Label, 12f, HelpTheme.TextLight, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            nameT.gameObject.AddComponent<LayoutElement>().preferredWidth = 230f;

            BuildControl(top, d);

            var spacer = NewRect("Spacer", top);
            spacer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            AddTextButton(top, "reset", 60f, () => { try { d.Entry.BoxedValue = d.Entry.DefaultValue; } catch { } Navigate(_mod, _section); });

            if (!string.IsNullOrEmpty(d.Description))
            {
                var desc = Label(row, d.Description, 10.5f, HelpTheme.TextMuted, TextAlignmentOptions.TopLeft);
                desc.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            }
        }

        private void BuildControl(RectTransform parent, CfgDescriptor d)
        {
            object val = d.BoxedValue;
            switch (d.Kind)
            {
                case CtrlKind.Bool:
                {
                    var t = AddToggle(parent, (bool)val);
                    t.onValueChanged.AddListener(v => Set(d, v));
                    break;
                }
                case CtrlKind.IntRange:
                case CtrlKind.FloatRange:
                {
                    var valLbl = Label(parent, FormatNum(d, Convert.ToSingle(val)), 11f, HelpTheme.TextGold, TextAlignmentOptions.MidlineLeft);
                    valLbl.gameObject.AddComponent<LayoutElement>().preferredWidth = 58f;
                    var slider = AddSlider(parent, (float)d.Min, (float)d.Max, Convert.ToSingle(val));
                    slider.onValueChanged.AddListener(v =>
                    {
                        float snapped = SnapStep(v, d);
                        if (d.Kind == CtrlKind.IntRange) Set(d, Convert.ChangeType((int)Math.Round(snapped), d.Type));
                        else Set(d, Convert.ChangeType(snapped, d.Type));
                        valLbl.GetComponent<TextMeshProUGUI>().text = FormatNum(d, snapped);
                    });
                    break;
                }
                case CtrlKind.Enum:
                case CtrlKind.ValueList:
                {
                    var names = d.Options ?? Array.Empty<string>();
                    int cur = Math.Max(0, Array.IndexOf(names, val?.ToString()));
                    var dd = AddDropdown(parent, names, cur);
                    dd.onValueChanged.AddListener(i => SetOption(d, names.Length > 0 ? names[Mathf.Clamp(i, 0, names.Length - 1)] : null));
                    break;
                }
                case CtrlKind.IntField:
                case CtrlKind.FloatField:
                {
                    var f = AddInput(parent, val?.ToString() ?? "", 150f);
                    f.onEndEdit.AddListener(s => { if (TryParseNumber(s, d.Type, out object p)) Set(d, p); });
                    break;
                }
                case CtrlKind.String:
                {
                    var f = AddInput(parent, (string)val ?? "", 320f);
                    f.onEndEdit.AddListener(s => Set(d, s ?? ""));
                    break;
                }
                case CtrlKind.Color:
                {
                    Color c = (Color)val;
                    var f = AddInput(parent, ColorToText(c), 200f);
                    f.onEndEdit.AddListener(s => { if (TryParseColor(s, out Color nc)) Set(d, nc); });
                    break;
                }
                case CtrlKind.KeyBind:
                {
                    string cur = val?.ToString() ?? "None";
                    var lbl = AddTextButton(parent, cur, 220f, null);
                    var tmp = lbl.GetComponentInChildren<TextMeshProUGUI>();
                    var btn = lbl.GetComponent<Button>();
                    btn.onClick.AddListener(() => BeginCapture(d.Entry, tmp));
                    break;
                }
                default:
                {
                    var lbl = Label(parent, val?.ToString() ?? "null", 11f, HelpTheme.TextMuted, TextAlignmentOptions.MidlineLeft);
                    lbl.gameObject.AddComponent<LayoutElement>().preferredWidth = 220f;
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- write-back
        private void Set(CfgDescriptor d, object value)
        {
            try { d.Entry.BoxedValue = value; }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning($"set '{d.Key}' failed: {ex.Message}"); }
        }

        private void SetOption(CfgDescriptor d, string chosen)
        {
            if (chosen == null) return;
            try
            {
                if (d.Type.IsEnum) { d.Entry.BoxedValue = Enum.Parse(d.Type, chosen); return; }
                d.Entry.SetSerializedValue(chosen);
            }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning($"set option '{d.Key}'='{chosen}' failed: {ex.Message}"); }
        }

        // ---------------------------------------------------------------- keybind capture
        private void BeginCapture(ConfigEntryBase entry, TextMeshProUGUI label)
        {
            _capturing = true; _captureTarget = entry; _captureLabel = label;
            _captureRestore = label != null ? label.text : null;
            if (label != null) label.text = "press a key... (Esc)";
        }

        private void PollKeyCapture()
        {
            if (_captureTarget == null) { _capturing = false; return; }
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { EndCapture(_captureRestore); return; }
            foreach (KeyCode kc in Enum.GetValues(typeof(KeyCode)))
            {
                if (!UnityEngine.Input.GetKeyDown(kc)) continue;
                if (Array.IndexOf(s_modifiers, kc) >= 0) continue;
                try
                {
                    if (_captureTarget.SettingType == typeof(KeyboardShortcut))
                    {
                        var mods = new List<KeyCode>();
                        foreach (var m in s_modifiers) if (UnityEngine.Input.GetKey(m)) mods.Add(m);
                        _captureTarget.BoxedValue = new KeyboardShortcut(kc, mods.ToArray());
                    }
                    else _captureTarget.BoxedValue = kc;
                }
                catch (Exception ex) { FiresConfigUI.Log.LogWarning("keybind capture failed: " + ex.Message); }
                EndCapture(_captureTarget.BoxedValue?.ToString());
                return;
            }
        }

        private void EndCapture(string labelText)
        {
            if (_captureLabel != null && labelText != null) _captureLabel.text = labelText;
            _capturing = false; _captureTarget = null; _captureLabel = null;
        }

        // ---------------------------------------------------------------- uGUI builders
        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        private static void Stretch(RectTransform rt, float l = 0, float t = 0, float r = 0, float b = 0)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(-r, -t);
        }

        private static RectTransform Label(Transform parent, string text, float size, Color color, TextAlignmentOptions align, FontStyles style = FontStyles.Normal)
        {
            var rt = NewRect("Text", parent);
            var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
            tmp.text = text; tmp.fontSize = size; tmp.color = color; tmp.alignment = align; tmp.fontStyle = style;
            tmp.raycastTarget = false; tmp.richText = true;
            ApplyFont(tmp);
            return rt;
        }

        private static void ApplyFont(TMP_Text tmp)
        {
            try { var f = FiresCore.Help.HelpPanel.FontProvider?.Invoke(); if (f != null) tmp.font = f; }
            catch { }
        }

        private static Toggle AddToggle(Transform parent, bool on)
        {
            var rt = NewRect("Toggle", parent);
            rt.gameObject.AddComponent<LayoutElement>().preferredWidth = 26f;
            var bg = rt.gameObject.AddComponent<Image>(); bg.color = HelpTheme.CodeBg;
            var checkRt = NewRect("Check", rt); Stretch(checkRt, 4, 4, 4, 4);
            var check = checkRt.gameObject.AddComponent<Image>(); check.color = HelpTheme.TextGold;
            var tog = rt.gameObject.AddComponent<Toggle>();
            tog.targetGraphic = bg; tog.graphic = check; tog.isOn = on;
            return tog;
        }

        private static Slider AddSlider(Transform parent, float min, float max, float val)
        {
            var rt = NewRect("Slider", parent);
            var le = rt.gameObject.AddComponent<LayoutElement>(); le.preferredWidth = 240f; le.minHeight = 18f;
            var bg = NewRect("BG", rt); Stretch(bg); bg.gameObject.AddComponent<Image>().color = HelpTheme.CodeBg;
            var fillArea = NewRect("FillArea", rt); Stretch(fillArea, 2, 2, 2, 2);
            var fill = NewRect("Fill", fillArea); fill.sizeDelta = Vector2.zero;
            fill.gameObject.AddComponent<Image>().color = HelpTheme.NavActive;
            var handleArea = NewRect("HandleArea", rt); Stretch(handleArea, 2, 0, 2, 0);
            var handle = NewRect("Handle", handleArea); handle.sizeDelta = new Vector2(10, 0);
            handle.gameObject.AddComponent<Image>().color = HelpTheme.TextGold;
            var s = rt.gameObject.AddComponent<Slider>();
            s.fillRect = fill; s.handleRect = handle; s.targetGraphic = handle.GetComponent<Image>();
            s.direction = Slider.Direction.LeftToRight; s.minValue = min; s.maxValue = max; s.value = Mathf.Clamp(val, min, max);
            return s;
        }

        private TMP_Dropdown AddDropdown(Transform parent, string[] options, int value)
        {
            var rt = NewRect("Dropdown", parent);
            var le = rt.gameObject.AddComponent<LayoutElement>(); le.preferredWidth = 220f; le.minHeight = 24f;
            rt.gameObject.AddComponent<Image>().color = HelpTheme.CodeBg;

            var labelRt = Label(rt, "", 11f, HelpTheme.TextLight, TextAlignmentOptions.MidlineLeft);
            Stretch(labelRt, 8, 2, 22, 2);
            var arrow = Label(rt, "v", 11f, HelpTheme.TextMuted, TextAlignmentOptions.MidlineRight);
            Stretch(arrow, 0, 0, 6, 0);

            // template (built once, hidden) - required by TMP_Dropdown
            var template = NewRect("Template", rt);
            template.anchorMin = new Vector2(0, 0); template.anchorMax = new Vector2(1, 0); template.pivot = new Vector2(0.5f, 1);
            template.anchoredPosition = new Vector2(0, 2); template.sizeDelta = new Vector2(0, 150);
            template.gameObject.AddComponent<Image>().color = HelpTheme.PanelBg;
            var tScroll = template.gameObject.AddComponent<ScrollRect>();
            var vp = NewRect("Viewport", template); Stretch(vp); vp.gameObject.AddComponent<Image>().color = Color.clear; vp.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            var cont = NewRect("Content", vp); cont.anchorMin = new Vector2(0, 1); cont.anchorMax = new Vector2(1, 1); cont.pivot = new Vector2(0.5f, 1); cont.sizeDelta = new Vector2(0, 28);
            var item = NewRect("Item", cont); item.anchorMin = new Vector2(0, 0.5f); item.anchorMax = new Vector2(1, 0.5f); item.sizeDelta = new Vector2(0, 24);
            var itemBg = item.gameObject.AddComponent<Image>(); itemBg.color = HelpTheme.NavNormal;
            var itemTog = item.gameObject.AddComponent<Toggle>(); itemTog.targetGraphic = itemBg;
            var itemLbl = Label(item, "Option", 11f, HelpTheme.TextLight, TextAlignmentOptions.MidlineLeft); Stretch(itemLbl, 10, 1, 10, 1);
            tScroll.content = cont; tScroll.viewport = vp; tScroll.horizontal = false; tScroll.movementType = ScrollRect.MovementType.Clamped;
            template.gameObject.SetActive(false);

            var dd = rt.gameObject.AddComponent<TMP_Dropdown>();
            dd.targetGraphic = rt.GetComponent<Image>();
            dd.template = template;
            dd.captionText = labelRt.GetComponent<TextMeshProUGUI>();
            dd.itemText = itemLbl.GetComponent<TextMeshProUGUI>();
            dd.options.Clear();
            foreach (var o in options) dd.options.Add(new TMP_Dropdown.OptionData(o));
            dd.value = Mathf.Clamp(value, 0, Math.Max(0, options.Length - 1));
            dd.RefreshShownValue();
            return dd;
        }

        private TMP_InputField AddInput(Transform parent, string text, float width)
        {
            var rt = NewRect("Input", parent);
            var le = rt.gameObject.AddComponent<LayoutElement>(); le.preferredWidth = width; le.minHeight = 24f;
            rt.gameObject.AddComponent<Image>().color = HelpTheme.CodeBg;
            var textRt = Label(rt, text, 11.5f, HelpTheme.TextLight, TextAlignmentOptions.MidlineLeft);
            Stretch(textRt, 8, 3, 8, 3);
            var f = rt.gameObject.AddComponent<TMP_InputField>();
            f.textComponent = textRt.GetComponent<TextMeshProUGUI>();
            f.text = text;
            return f;
        }

        private Button AddTextButton(Transform parent, string text, float width, Action onClick)
        {
            var rt = NewRect("Btn", parent);
            var le = rt.gameObject.AddComponent<LayoutElement>(); le.preferredWidth = width; le.minHeight = 24f;
            var img = rt.gameObject.AddComponent<Image>(); img.color = HelpTheme.NavActive;
            var btn = rt.gameObject.AddComponent<Button>(); btn.targetGraphic = img;
            var c = btn.colors; c.normalColor = HelpTheme.NavActive; c.highlightedColor = HelpTheme.NavHover; c.pressedColor = HelpTheme.NavNormal; btn.colors = c;
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            var t = Label(rt, text, 11f, HelpTheme.TextGold, TextAlignmentOptions.Center); Stretch(t);
            return btn;
        }

        private RectTransform BuildScroll(RectTransform parent, out ScrollRect scroll, out RectTransform viewport)
        {
            var scrollGo = NewRect("Scroll", parent); Stretch(scrollGo);
            var vp = NewRect("Viewport", scrollGo); Stretch(vp);
            vp.gameObject.AddComponent<RectMask2D>(); vp.gameObject.AddComponent<Image>().color = Color.clear;
            var content = NewRect("Content", vp);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0, 1);
            content.sizeDelta = Vector2.zero;
            scroll = scrollGo.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true; scroll.viewport = vp; scroll.content = content;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 110f;
            viewport = vp;
            return content;
        }

        // ---------------------------------------------------------------- helpers
        private static bool Matches(CfgDescriptor d, string q)
        {
            q = q.ToLowerInvariant();
            return d.Key.ToLowerInvariant().Contains(q) || d.Section.ToLowerInvariant().Contains(q)
                || d.ModName.ToLowerInvariant().Contains(q)
                || (!string.IsNullOrEmpty(d.Description) && d.Description.ToLowerInvariant().Contains(q));
        }

        private static float SnapStep(float v, CfgDescriptor d)
        {
            if (d.Step <= 0.0) return v;
            double n = Math.Round((v - d.Min) / d.Step);
            return (float)Math.Min(d.Max, Math.Max(d.Min, d.Min + n * d.Step));
        }

        private static string FormatNum(CfgDescriptor d, float v)
            => d.Kind == CtrlKind.IntRange ? ((int)Math.Round(v)).ToString() : v.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool TryParseNumber(string text, Type t, out object value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (t == typeof(float) || t == typeof(double))
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double dv)
                    && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) return false;
                value = Convert.ChangeType(dv, t); return true;
            }
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out long lv)
                && !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv)) return false;
            try { value = Convert.ChangeType(lv, t); return true; } catch { return false; }
        }

        private static string ColorToText(Color c)
            => $"{c.r:0.##},{c.g:0.##},{c.b:0.##},{c.a:0.##}";

        private static bool TryParseColor(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var p = s.Split(',');
            if (p.Length < 3) return false;
            float R, G, B, A = 1f;
            if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out R)) return false;
            if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out G)) return false;
            if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out B)) return false;
            if (p.Length >= 4) float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out A);
            c = new Color(R, G, B, A); return true;
        }

        private static string CleanSection(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int dash = s.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash <= 5)
            {
                bool digit = false, ok = true;
                foreach (char ch in s.Substring(0, dash)) { if (char.IsDigit(ch)) digit = true; else if (!char.IsLetter(ch)) { ok = false; break; } }
                if (digit && ok) return s.Substring(dash + 3);
            }
            return s;
        }
    }
}
