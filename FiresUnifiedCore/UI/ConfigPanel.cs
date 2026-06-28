using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using FiresCore.Help;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.UI
{
    // The Fires config window: a uGUI Canvas overlay laid out like shudnal's ConfigurationManager — a plugin
    // list on the left, the selected plugin's categories on the right as full-width header bars with dense
    // rows. Interactive controls are built through the tested UIBuilderHelper (sliders + input fields) and
    // cycle-buttons (bool / enum) so they actually function. Resizable + movable. Mouse handling matches the
    // help panel: registers with ModUiRegistry (so the game's input patches free the cursor + suppress input)
    // and forces the cursor unlocked every frame (Update + OnGUI), plus InputBlock + HUD-hide.
    public class ConfigPanel : MonoBehaviour
    {
        private const int OverlaySortingOrder = 350;
        private const float SidebarWidth = 280f;
        private const float TitleBarH = 46f;
        private static readonly Vector2 DefaultSize = new Vector2(1240f, 760f);
        private static readonly Vector2 MinSize = new Vector2(760f, 420f);

        private const float FsTitle = 19f;
        private const float FsSection = 16f;
        private const float FsLabel = 14f;
        private const float FsValue = 13f;
        private const float FsNav = 13.5f;
        private const float RowMinH = 28f;
        private const float LabelW = 300f;

        private static readonly Color RowBg = new Color(0.10f, 0.085f, 0.06f, 0.55f);
        private static readonly Color RowAltBg = new Color(0.13f, 0.11f, 0.07f, 0.55f);
        private static readonly Color CatHeaderBg = new Color(0.24f, 0.18f, 0.09f, 0.97f);
        private static readonly Color FieldBg = new Color(0.05f, 0.05f, 0.07f, 0.95f);

        private static ConfigPanel _instance;
        private static bool _cmdRegistered;

        public static void EnsureHost()
        {
            if (_instance != null) return;
            var go = new GameObject("FiresConfigPanelHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<ConfigPanel>();
            // Register with the shared modal registry so the game's input patches (GUIManager.IsCustomPanelOpen
            // -> ModUiRegistry.IsAnyOpen) free the cursor + suppress player input while we're open — the SAME
            // path the help panel relies on.
            FiresCore.Bridge.ModUiRegistry.Register("FiresConfigPanel", () => IsOpen, () => _instance?.Hide(), () => _instance?._root);
        }

        public static void Toggle() { EnsureHost(); _instance.ToggleInternal(); }

        public static bool IsOpen => _instance != null && _instance._root != null && _instance._root.activeSelf;

        // ----- instance -----
        private GameObject _root;
        private RectTransform _dialog;
        private RectTransform _sideContent;
        private RectTransform _content;
        private ScrollRect _contentScroll;
        private TMP_InputField _searchField;
        private string _search = "";
        private string _mod;
        private int _rowIndex;

        private bool _hudHidden;
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
            if (IsOpen) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }

        // shudnal-style per-frame cursor enforcement: OnGUI runs AFTER Update/LateUpdate, so forcing here wins
        // the race against the game re-locking the cursor.
        private void OnGUI()
        {
            if (!IsOpen) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
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
                if (string.IsNullOrEmpty(_mod) || CfgDiscovery.SectionsOf(_mod).Count == 0) SelectFirst();
                RebuildSidebar();
                Navigate(_mod);
                InputBlock.Block(true);
                HideGameUi(true);
                FiresConfigUI.Log.LogInfo($"ConfigPanel opened: {CfgDiscovery.ModNames.Count} mod(s), {CfgDiscovery.Descriptors.Count} entries.");
            }
            catch (Exception ex) { FiresConfigUI.Log.LogError("ConfigPanel.Show failed: " + ex); }
        }

        public void Hide()
        {
            _capturing = false; _captureTarget = null;
            if (_root != null) _root.SetActive(false);
            InputBlock.Block(false);
            HideGameUi(false);
        }

        private void HideGameUi(bool hide)
        {
            try
            {
                if (hide == _hudHidden) return;
                _hudHidden = hide;
                if (Hud.instance != null && Hud.instance.m_rootObject != null) Hud.instance.m_rootObject.SetActive(!hide);
            }
            catch { }
        }

        private void SelectFirst()
        {
            var mods = CfgDiscovery.ModNames;
            _mod = mods.Count > 0 ? mods[0] : null;
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

            // Fixed-size, top-left-anchored dialog so it can be moved + resized (drag the title / the corner grip).
            _dialog = NewRect("Dialog", _root.transform);
            _dialog.anchorMin = _dialog.anchorMax = new Vector2(0, 1);
            _dialog.pivot = new Vector2(0, 1);
            _dialog.sizeDelta = DefaultSize;
            _dialog.anchoredPosition = new Vector2(90, -50);
            var dialogImg = _dialog.gameObject.AddComponent<Image>();
            dialogImg.color = HelpTheme.PanelBg;
            _dialog.gameObject.AddComponent<Button>().targetGraphic = dialogImg;

            BuildTitleBar(_dialog);
            BuildSidebar(_dialog);
            BuildContent(_dialog);
            BuildResizeGrip(_dialog);
        }

        private void BuildTitleBar(RectTransform dialog)
        {
            var bar = NewRect("TitleBar", dialog);
            bar.anchorMin = new Vector2(0, 1); bar.anchorMax = new Vector2(1, 1); bar.pivot = new Vector2(0.5f, 1);
            bar.sizeDelta = new Vector2(0, TitleBarH);
            var barImg = bar.gameObject.AddComponent<Image>(); barImg.color = HelpTheme.SidebarBg;
            bar.gameObject.AddComponent<DragMove>().Target = dialog;   // drag the title bar to move the window

            var title = Label(bar, "Fires Configuration", FsTitle, HelpTheme.HeaderColor, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            title.anchorMin = new Vector2(0, 0); title.anchorMax = new Vector2(0, 1); title.pivot = new Vector2(0, 0.5f);
            title.sizeDelta = new Vector2(300, 0); title.anchoredPosition = new Vector2(18, 0);

            var search = NewRect("Search", bar);
            search.anchorMin = new Vector2(0, 0); search.anchorMax = new Vector2(0, 1); search.pivot = new Vector2(0, 0.5f);
            search.anchoredPosition = new Vector2(330, 0); search.sizeDelta = new Vector2(400, -12);
            _searchField = UIBuilderHelper.CreateInputField(search, "Search", "search settings...", Vector2.zero, Vector2.one, (int)FsValue);
            _searchField.onValueChanged.AddListener(s => { _search = s ?? ""; Navigate(_mod); });

            var close = NewRect("Close", bar);
            close.anchorMin = new Vector2(1, 0); close.anchorMax = new Vector2(1, 1); close.pivot = new Vector2(1, 0.5f);
            close.anchoredPosition = new Vector2(-6, 0); close.sizeDelta = new Vector2(42, -8);
            UIBuilderHelper.CreateButton(close, "X", Vector2.zero, Vector2.one,
                new Color(0.5f, 0.15f, 0.1f, 0.9f), new Color(0.7f, 0.25f, 0.18f, 0.95f), new Color(0.4f, 0.1f, 0.08f, 1f), Hide);
        }

        private void BuildSidebar(RectTransform dialog)
        {
            var side = NewRect("Sidebar", dialog);
            side.anchorMin = new Vector2(0, 0); side.anchorMax = new Vector2(0, 1); side.pivot = new Vector2(0, 0.5f);
            side.sizeDelta = new Vector2(SidebarWidth, 0); side.offsetMin = new Vector2(0, 0); side.offsetMax = new Vector2(SidebarWidth, -TitleBarH);
            side.gameObject.AddComponent<Image>().color = HelpTheme.SidebarBg;

            _sideContent = BuildScroll(side, out _, out _);
            var vlg = _sideContent.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(6, 6, 8, 8); vlg.spacing = 2;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            _sideContent.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private void BuildContent(RectTransform dialog)
        {
            var area = NewRect("ContentArea", dialog);
            area.anchorMin = Vector2.zero; area.anchorMax = Vector2.one;
            area.offsetMin = new Vector2(SidebarWidth, 0); area.offsetMax = new Vector2(0, -TitleBarH);
            area.gameObject.AddComponent<Image>().color = HelpTheme.ContentBg;

            _content = BuildScroll(area, out _contentScroll, out _);
            var vlg = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(14, 22, 10, 16); vlg.spacing = 3;
            vlg.childControlWidth = vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            _content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private void BuildResizeGrip(RectTransform dialog)
        {
            var grip = NewRect("ResizeGrip", dialog);
            grip.anchorMin = grip.anchorMax = new Vector2(1, 0); grip.pivot = new Vector2(1, 0);
            grip.anchoredPosition = Vector2.zero; grip.sizeDelta = new Vector2(22, 22);
            var img = grip.gameObject.AddComponent<Image>(); img.color = new Color(0.35f, 0.27f, 0.13f, 0.9f);
            grip.gameObject.AddComponent<DragResize>().Target = dialog;
            Stretch(Label(grip, "//", 12f, HelpTheme.TextGold, TextAlignmentOptions.Center, FontStyles.Bold));
        }

        // ---------------------------------------------------------------- sidebar (plugin list)
        private void RebuildSidebar()
        {
            if (_sideContent == null) return;
            for (int i = _sideContent.childCount - 1; i >= 0; i--) Destroy(_sideContent.GetChild(i).gameObject);

            foreach (var mod in CfgDiscovery.ModNames)
            {
                string m = mod;
                AddModButton(mod, mod == _mod, () =>
                {
                    _search = ""; if (_searchField != null) _searchField.SetTextWithoutNotify("");
                    _mod = m; RebuildSidebar(); Navigate(m);
                });
            }
        }

        private void AddModButton(string text, bool active, Action onClick)
        {
            var go = NewRect("Mod", _sideContent);
            go.gameObject.AddComponent<LayoutElement>().minHeight = 34f;
            UIBuilderHelper.CreateButton(go, "  " + text, Vector2.zero, Vector2.one,
                active ? HelpTheme.NavActive : HelpTheme.NavNormal, HelpTheme.NavHover, HelpTheme.NavActive, () => onClick());
            var t = go.GetComponentInChildren<TextMeshProUGUI>();
            if (t != null) { t.color = active ? HelpTheme.TextGold : HelpTheme.TextLight; t.fontSize = FsNav; t.fontStyle = FontStyles.Bold; t.alignment = TextAlignmentOptions.MidlineLeft; }
        }

        // ---------------------------------------------------------------- content (categories + rows)
        private void Navigate(string mod)
        {
            _mod = mod; _rowIndex = 0;
            if (_content == null) return;
            for (int i = _content.childCount - 1; i >= 0; i--) Destroy(_content.GetChild(i).gameObject);
            if (_contentScroll != null) _contentScroll.verticalNormalizedPosition = 1f;

            bool searching = !string.IsNullOrEmpty(_search);
            int shown = 0;

            if (searching)
            {
                AddPluginTitle("Results for \"" + _search + "\"");
                string last = null;
                foreach (var d in CfgDiscovery.Descriptors)
                {
                    if (!Matches(d, _search)) continue;
                    string g = d.ModName + "  -  " + CleanSection(d.Section);
                    if (g != last) { last = g; AddCategoryHeader(g); _rowIndex = 0; }
                    try { BuildRow(d); shown++; } catch (Exception ex) { FiresConfigUI.Log.LogWarning($"row '{d.Section}/{d.Key}' failed: {ex.Message}"); }
                }
            }
            else if (mod != null)
            {
                AddPluginTitle(mod);
                foreach (var section in CfgDiscovery.SectionsOf(mod))
                {
                    AddCategoryHeader(CleanSection(section));
                    _rowIndex = 0;
                    foreach (var d in CfgDiscovery.Descriptors)
                    {
                        if (d.ModName != mod || d.Section != section) continue;
                        try { BuildRow(d); shown++; } catch (Exception ex) { FiresConfigUI.Log.LogWarning($"row '{d.Section}/{d.Key}' failed: {ex.Message}"); }
                    }
                }
            }
            if (shown == 0) AddCategoryHeader(searching ? "No matches." : "No settings.");
        }

        private void AddPluginTitle(string text)
        {
            var go = NewRect("PluginTitle", _content);
            go.gameObject.AddComponent<LayoutElement>().minHeight = 32f;
            Stretch(Label(go, text, FsTitle, HelpTheme.HeaderColor, TextAlignmentOptions.BottomLeft, FontStyles.Bold), 2, 6, 2, 2);
        }

        private void AddCategoryHeader(string text)
        {
            var go = NewRect("CatHeader", _content);
            go.gameObject.AddComponent<LayoutElement>().minHeight = 28f;
            go.gameObject.AddComponent<Image>().color = CatHeaderBg;
            Stretch(Label(go, text, FsSection, HelpTheme.HeaderColor, TextAlignmentOptions.Center, FontStyles.Bold));
        }

        private void BuildRow(CfgDescriptor d)
        {
            var row = NewRect("Row", _content);
            row.gameObject.AddComponent<Image>().color = (_rowIndex++ & 1) == 0 ? RowBg : RowAltBg;
            var rowV = row.gameObject.AddComponent<VerticalLayoutGroup>();
            rowV.padding = new RectOffset(12, 12, 4, 4); rowV.spacing = 1;
            rowV.childControlWidth = rowV.childControlHeight = true;
            rowV.childForceExpandWidth = true; rowV.childForceExpandHeight = false;
            row.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var top = NewRect("Top", row);
            var topH = top.gameObject.AddComponent<HorizontalLayoutGroup>();
            topH.spacing = 10; topH.childControlWidth = topH.childControlHeight = true;
            topH.childForceExpandWidth = topH.childForceExpandHeight = false; topH.childAlignment = TextAnchor.MiddleLeft;
            top.gameObject.AddComponent<LayoutElement>().minHeight = RowMinH;

            var nameT = Label(top, d.Label, FsLabel, HelpTheme.TextLight, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            nameT.gameObject.AddComponent<LayoutElement>().preferredWidth = LabelW;

            BuildControl(top, d);

            var spacer = NewRect("Spacer", top);
            spacer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            CycleButton(top, 72f, "Reset", () => { try { d.Entry.BoxedValue = d.Entry.DefaultValue; } catch { } Navigate(_mod); });

            if (!string.IsNullOrEmpty(d.Description))
            {
                var desc = Label(row, d.Description, FsValue - 1.5f, HelpTheme.TextMuted, TextAlignmentOptions.TopLeft);
                desc.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            }
        }

        // ---------------------------------------------------------------- typed controls (via UIBuilderHelper)
        private void BuildControl(RectTransform parent, CfgDescriptor d)
        {
            object val = d.BoxedValue;
            switch (d.Kind)
            {
                case CtrlKind.Bool:
                {
                    var btn = CycleButton(parent, 130f, ((bool)val) ? "Enabled" : "Disabled", null);
                    var tmp = btn.GetComponentInChildren<TextMeshProUGUI>();
                    btn.onClick.AddListener(() => { bool nv = !(bool)d.BoxedValue; Set(d, nv); if (tmp != null) tmp.text = nv ? "Enabled" : "Disabled"; });
                    break;
                }
                case CtrlKind.IntRange:
                case CtrlKind.FloatRange:
                {
                    var valLbl = Label(parent, FormatNum(d, Convert.ToSingle(val)), FsValue, HelpTheme.TextGold, TextAlignmentOptions.MidlineLeft);
                    valLbl.gameObject.AddComponent<LayoutElement>().preferredWidth = 62f;
                    var slot = Slot(parent, 340f);
                    var slider = UIBuilderHelper.CreateSlider(slot, "Slider", Vector2.zero, Vector2.one, (float)d.Min, (float)d.Max, Convert.ToSingle(val), null);
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
                    var btn = CycleButton(parent, 240f, names.Length > 0 ? names[cur] : (val?.ToString() ?? ""), null);
                    var tmp = btn.GetComponentInChildren<TextMeshProUGUI>();
                    btn.onClick.AddListener(() =>
                    {
                        if (names.Length == 0) return;
                        int now = Math.Max(0, Array.IndexOf(names, d.BoxedValue?.ToString()));
                        int next = (now + 1) % names.Length;
                        SetOption(d, names[next]);
                        if (tmp != null) tmp.text = names[next];
                    });
                    break;
                }
                case CtrlKind.IntField:
                case CtrlKind.FloatField:
                {
                    var f = MakeInput(parent, 200f, val?.ToString() ?? "");
                    f.onEndEdit.AddListener(s => { if (TryParseNumber(s, d.Type, out object p)) Set(d, p); });
                    break;
                }
                case CtrlKind.String:
                {
                    var f = MakeInput(parent, 440f, (string)val ?? "");
                    f.onEndEdit.AddListener(s => Set(d, s ?? ""));
                    break;
                }
                case CtrlKind.Color:
                {
                    var f = MakeInput(parent, 240f, ColorToText((Color)val));
                    f.onEndEdit.AddListener(s => { if (TryParseColor(s, out Color nc)) Set(d, nc); });
                    break;
                }
                case CtrlKind.KeyBind:
                {
                    var btn = CycleButton(parent, 240f, val?.ToString() ?? "None", null);
                    var tmp = btn.GetComponentInChildren<TextMeshProUGUI>();
                    btn.onClick.AddListener(() => BeginCapture(d.Entry, tmp));
                    break;
                }
                default:
                {
                    var lbl = Label(parent, val?.ToString() ?? "null", FsValue, HelpTheme.TextMuted, TextAlignmentOptions.MidlineLeft);
                    lbl.gameObject.AddComponent<LayoutElement>().preferredWidth = 260f;
                    break;
                }
            }
        }

        // A control 'slot' inside the horizontal row layout, sized via LayoutElement; helper controls fill it.
        private RectTransform Slot(RectTransform parent, float width, float height = 26f)
        {
            var slot = NewRect("Ctrl", parent);
            var le = slot.gameObject.AddComponent<LayoutElement>(); le.preferredWidth = width; le.minWidth = width; le.minHeight = height;
            return slot;
        }

        private Button CycleButton(RectTransform parent, float width, string text, Action onClick)
        {
            var slot = Slot(parent, width);
            var btn = UIBuilderHelper.CreateButton(slot, text, Vector2.zero, Vector2.one,
                HelpTheme.NavActive, HelpTheme.NavHover, HelpTheme.NavNormal, onClick != null ? () => onClick() : (UnityEngine.Events.UnityAction)null);
            var t = btn.GetComponentInChildren<TextMeshProUGUI>();
            if (t != null) { t.color = HelpTheme.TextGold; t.fontSize = FsValue; }
            return btn;
        }

        private TMP_InputField MakeInput(RectTransform parent, float width, string text)
        {
            var slot = Slot(parent, width);
            var f = UIBuilderHelper.CreateInputField(slot, "Input", "", Vector2.zero, Vector2.one, (int)FsValue);
            var img = f.GetComponent<Image>(); if (img != null) img.color = FieldBg;
            if (f.textComponent != null) f.textComponent.color = HelpTheme.TextLight;
            f.caretColor = HelpTheme.TextLight;
            f.text = text;
            return f;
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

        // ---------------------------------------------------------------- drag move / resize
        private class DragMove : MonoBehaviour, IDragHandler
        {
            public RectTransform Target;
            public void OnDrag(PointerEventData e)
            {
                if (Target == null) return;
                Target.anchoredPosition += e.delta / CanvasScale(Target);
            }
        }

        private class DragResize : MonoBehaviour, IDragHandler
        {
            public RectTransform Target;
            public void OnDrag(PointerEventData e)
            {
                if (Target == null) return;
                Vector2 d = e.delta / CanvasScale(Target);
                var s = Target.sizeDelta + new Vector2(d.x, -d.y);
                Target.sizeDelta = new Vector2(Mathf.Max(MinSize.x, s.x), Mathf.Max(MinSize.y, s.y));
            }
        }

        private static float CanvasScale(Component c)
        {
            var canvas = c.GetComponentInParent<Canvas>();
            return canvas != null && canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
        }

        // ---------------------------------------------------------------- uGUI primitives
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
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 130f;
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

        private static string ColorToText(Color c) => $"{c.r:0.##},{c.g:0.##},{c.b:0.##},{c.a:0.##}";

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
