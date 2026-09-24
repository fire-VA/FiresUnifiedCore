using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// The Fires config window (F8 / va_config) — an IMGUI split view laid out like BepInEx ConfigurationManager
    /// with the shared rounded Fires chrome. LEFT: every discovered mod; the selected mod expands in-place to a
    /// numbered section index, and clicking a section expands + scrolls to it on the right. RIGHT: the selected
    /// mod's sections as collapsible bars with rows of name | typed widget | Reset. Search filters across every
    /// mod. Gold names mark values that differ from their default.
    /// Data source is CfgDiscovery; write-back is ConfigEntry.BoxedValue.
    /// </summary>
    public partial class ConfigPanel : MonoBehaviour
    {
        private const int WindowId = 0xF1C0;
        private const float WindowAlpha = 0.92f;
        private const float NavWidthBase = 250f;
        private const float NameWidthBase = 250f;
        private const float ResetButtonWidthBase = 56f;
        private const float DescriptionGutterPadBase = 24f;
        private const float AdvancedCheckboxWidthBase = 94f;
        private const float KeybindCheckboxWidthBase = 92f;
        private const float AllModsCheckboxWidthBase = 92f;
        private const float ConflictsCheckboxWidthBase = 96f;
        private const float WidgetWidthBase = 190f;
        private const float NumberFieldWidthBase = 64f;
        private const float DropdownWidthBase = 190f;
        private const float KeyBindWidthBase = 190f;
        private const float StringFieldWidthBase = 280f;
        private const float VectorFieldWidthBase = 62f;
        private const float BodyChromeHeightBase = 120f;
        private const float TitleBarHeightBase = 30f;
        private const float GripSizeBase = 18f;
        private const float GripInsetBase = 4f;
        private const float ZoomStep = 0.1f;
        // A stable hint keeps the grip's control id from shifting as sections collapse and change the
        // control count drawn before it.
        private const int ResizeControlHint = 0x5F12E;
        private const float DropdownRowHeightBase = 22f;
        private const float DropdownMaxHeightBase = 320f;
        private const float ColorPickerWidthBase = 260f;
        private const float ColorPickerHeightBase = 190f;
        private const float CaptureNoticeSeconds = 5f;
        private const float ScreenPaddingBase = 20f;
        private const float ClearButtonWidthBase = 26f;
        private const float CaptureRectPaddingBase = 4f;
        private const float OnScreenMarginBase = 60f;
        private const float MinBodyHeightBase = 80f;
        private const string EditWindowScope = "#edit";
        private const string NumberVariant = ":n";
        private const int MinSearchLength = 2;
        private const float StartingWidthShare = 0.82f;
        private const float StartingHeightShare = 0.86f;
        private static readonly Vector2 DefaultSizeBase = new Vector2(1140f, 720f);
        private static readonly Vector2 MinSizeBase = new Vector2(760f, 420f);

        // Every size above is written at 100% and read through here, so raising the zoom grows the chrome by
        // the same factor ConfigSkin grows the fonts by.
        private static float Px(float designUnits) => ConfigWindowScale.Px(designUnits);

        // The screen measured in design units, so window sizes can be reasoned about at 100% whatever the zoom.
        private static float DesignWidth => Screen.width / ConfigWindowScale.Factor;
        private static float DesignHeight => Screen.height / ConfigWindowScale.Factor;

        private static float NavWidth => Px(NavWidthBase);
        private static float NameWidth => Px(NameWidthBase);
        private static float ResetButtonWidth => Px(ResetButtonWidthBase);
        private static float DescriptionRightGutter => Px(ResetButtonWidthBase + DescriptionGutterPadBase);
        private static float WidgetWidth => Px(WidgetWidthBase);
        private static float NumberFieldWidth => Px(NumberFieldWidthBase);
        private static float DropdownWidth => Px(DropdownWidthBase);
        private static float KeyBindWidth => Px(KeyBindWidthBase);
        private static float StringFieldWidth => Px(StringFieldWidthBase);
        private static float VectorFieldWidth => Px(VectorFieldWidthBase);
        private static float BodyChromeHeight => Px(BodyChromeHeightBase);
        private static float TitleBarHeight => Px(TitleBarHeightBase);
        private static float GripSize => Px(GripSizeBase);
        private static float GripInset => Px(GripInsetBase);
        private static float DropdownRowHeight => Px(DropdownRowHeightBase);
        private static float DropdownMaxHeight => Px(DropdownMaxHeightBase);
        private static float ColorPickerWidth => Px(ColorPickerWidthBase);
        private static float ColorPickerHeight => Px(ColorPickerHeightBase);
        private static float ScreenPadding => Px(ScreenPaddingBase);
        private static float ClearButtonWidth => Px(ClearButtonWidthBase);
        private static float CaptureRectPadding => Px(CaptureRectPaddingBase);
        private static Vector2 MinSize => ConfigWindowScale.Px(MinSizeBase);

        private static readonly Color CaptureButtonGreen = new Color(0.35f, 0.75f, 0.30f);

        private const string ChangedColor = "#FFD980";
        private const string LockedColor = "#9AA0A6";
        private const string OffColor = "#997F55";
        private const string OnColor = "#66DD66";
        private const string CaptureColor = "#FFCC66";
        private const string NoticeColor = "#CC9944";

        private static ConfigPanel _instance;
        private static bool _cmdRegistered;

        public static void EnsureHost()
        {
            if (_instance != null) return;
            var host = new GameObject("FiresConfigPanelHost");
            UnityEngine.Object.DontDestroyOnLoad(host);
            _instance = host.AddComponent<ConfigPanel>();
            FiresCore.Bridge.ModUiRegistry.Register("FiresConfigPanel", () => IsOpen, () => _instance?.Hide(), null);
        }

        public static void Toggle() { EnsureHost(); _instance.ToggleInternal(); }

        public static void Open() { EnsureHost(); if (!_instance._open) _instance.Show(); }

        public static bool IsOpen => _instance != null && _instance._open;

        private bool _open;
        private Rect _rect = new Rect(-1f, -1f, 0f, 0f);
        private Vector2 _size = DefaultSizeBase;
        private Vector2 _navScroll;
        private Vector2 _bodyScroll;
        private string _search = "";
        private string _mod;
        private string _navSection;
        private int _resizeHotControl;
        private bool _hudHidden;

        private readonly HashSet<string> _collapsed = new HashSet<string>();
        private readonly HashSet<string> _customDrawerFailed = new HashSet<string>();
        private readonly HashSet<string> _modsWithKeybinds = new HashSet<string>();
        private readonly HashSet<string> _sectionsWithKeybinds = new HashSet<string>();
        private readonly List<CfgDescriptor> _searchResults = new List<CfgDescriptor>();
        private readonly ConfigFilesEditor _filesEditor = new ConfigFilesEditor();
        private bool _filesMode;
        private string _controlScope = "";
        private Action<CfgDescriptor> _editWindowWidget;
        private Action<CfgDescriptor> _editWindowReset;

        private string _pendingJump;

        private readonly Dictionary<string, string> _editBuf = new Dictionary<string, string>();
        private readonly Dictionary<string, CfgDescriptor> _byId = new Dictionary<string, CfgDescriptor>();
        private string _lastFocus = "";
        private readonly object _textToken = new object();

        private bool _capturing;
        private ConfigEntryBase _captureTarget;
        // Record-then-apply capture: keys accumulate while recording (modifiers collect, the last
        // non-modifier becomes the main key) and NOTHING commits until Apply is clicked, which is what
        // makes multi-key chords settable — committing on the first keydown ends capture on the modifier.
        private KeyCode _capMain = KeyCode.None;
        private readonly List<KeyCode> _capMods = new List<KeyCode>();
        private Rect _capButtonsScreenRect;
        private string _capNotice;
        private string _capNoticeId;
        private float _capNoticeUntil;

        private static readonly KeyCode[] s_modifiers =
        {
            KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift,
            KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr, KeyCode.LeftCommand, KeyCode.RightCommand,
        };

        // ---------------------------------------------------------------- lifecycle
        private void Awake()
        {
            _editWindowWidget = DrawWidgetInEditWindow;
            _editWindowReset = ResetToDefault;
        }

        private void Update()
        {
            if (!_cmdRegistered) { try { RegisterCommands(); _cmdRegistered = true; } catch { } }
            WatchVanillaControlsMenu();
            LogKeybindConflictsOnce();
            if (_capturing) { PollKeyCapture(); return; }

            if (HotkeyPressed()) ToggleInternal();

            if (!_open) return;
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                if (KeybindPopupOpen) CloseKeybindPopup();
                else if (ConfigPopup.IsOpen) ConfigPopup.Close();
                else if (SettingEditWindow.IsOpen) SettingEditWindow.Close();
                else { Hide(); return; }
            }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (GUIUtility.keyboardControl != 0) Input.FiresInputBlock.Acquire(_textToken);
            else Input.FiresInputBlock.Release(_textToken);
        }

        // Chord.IsDown rather than KeyboardShortcut.IsDown: BepInEx's own check refuses to fire while any
        // unrelated key is held, so the window would not open while the player is walking.
        private static bool HotkeyPressed()
            => FiresConfigUI.CfgHotkey != null
                ? Input.Chord.IsDown(FiresConfigUI.CfgHotkey.Value)
                : UnityEngine.Input.GetKeyDown(FiresConfigUI.ToggleKey);

        private static void RegisterCommands()
        {
            new Terminal.ConsoleCommand("va_config", "Toggle the Fires configuration window.", _ => Toggle(), isCheat: false);
            new Terminal.ConsoleCommand("va_config_dump", "Log every discovered config (N mods, M entries).", _ => CfgDiscovery.DumpToLog(), isCheat: false);
        }

        private void ToggleInternal() { if (_open) Hide(); else Show(); }

        public void Show()
        {
            try
            {
                RebuildDiscovery();
                _open = true;
                InputBlock.Block(true);
                HideGameUi(true);
                ConfigGamePause.Apply(true);
                FiresConfigUI.Log.LogInfo($"ConfigPanel opened: {CfgDiscovery.ModNames.Count} mod(s), {CfgDiscovery.Descriptors.Count} entries.");
                OnWindowOpenedCheckKeybinds();
            }
            catch (Exception ex) { FiresConfigUI.Log.LogError("ConfigPanel.Show failed: " + ex); }
        }

        public void Hide()
        {
            CancelCapture();
            CloseKeybindPopup();
            ConfigPopup.Close();
            SettingEditWindow.Close();
            _open = false;
            _editBuf.Clear();

            InputBlock.Block(false);
            HideGameUi(false);
            ConfigGamePause.Apply(false);
            Input.FiresInputBlock.Release(_textToken);
        }

        private void RebuildDiscovery()
        {
            CfgDiscovery.Rebuild();
            BuildIdMap();
            BuildKeybindIndex();
            if (string.IsNullOrEmpty(_mod) || CfgDiscovery.SectionsOf(_mod).Count == 0) SelectFirstMod();
        }

        // Which mods and sections contain a keybind at all, so the keybinds-only view can drop the rest from
        // the nav instead of leaving the user clicking through empty mods. One pass here beats testing every
        // descriptor per mod per frame.
        private void BuildKeybindIndex()
        {
            _modsWithKeybinds.Clear();
            _sectionsWithKeybinds.Clear();
            foreach (var descriptor in CfgDiscovery.Descriptors)
            {
                if (descriptor.Kind != CtrlKind.KeyBind) continue;
                _modsWithKeybinds.Add(descriptor.ModName);
                _sectionsWithKeybinds.Add(CollapseKey(descriptor.ModName, descriptor.Section));
            }
        }

        private bool ModIsVisible(string modName) => !KeybindsOnly || _modsWithKeybinds.Contains(modName);

        private bool SectionIsVisible(string modName, string section)
            => !KeybindsOnly || _sectionsWithKeybinds.Contains(CollapseKey(modName, section));

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

        private static bool KeybindsOnly => FiresConfigUI.KeybindsOnly;

        private void SelectFirstMod()
        {
            _mod = null;
            foreach (var mod in CfgDiscovery.ModNames)
            {
                if (!ModIsVisible(mod)) continue;
                _mod = mod;
                break;
            }
            _navSection = null;
            _bodyScroll = Vector2.zero;
        }

        private void BuildIdMap()
        {
            _byId.Clear();
            foreach (var descriptor in CfgDiscovery.Descriptors) _byId[IdOf(descriptor)] = descriptor;
        }

        private static string IdOf(CfgDescriptor descriptor) => descriptor.ModGuid + "|" + descriptor.Section + "|" + descriptor.Key;

        // ---------------------------------------------------------------- draw
        private void OnGUI()
        {
            if (!_open) { ReleaseResizeCapture(); return; }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Valheim's own OnGUI can leave GUI.matrix scaled; inheriting it draws the window off-screen
            // (the documented "renders but invisible" trap). Draw in identity space, restore after.
            var matrix = GUI.matrix;
            ConfigWindowScale.Refresh();
            GUI.matrix = Matrix4x4.identity;
            try
            {
                ConfigSkin.Refresh(WindowAlpha, ConfigWindowScale.Factor);
                FiresRoundedSkin.ResetTooltip();

                if (_rect.x < 0f)
                {
                    _size = StartingSize(DesignWidth, DesignHeight);
                    var startPixels = ConfigWindowScale.Px(_size);
                    _rect = new Rect((Screen.width - startPixels.x) / 2f,
                        Mathf.Max(ScreenPadding, (Screen.height - startPixels.y) / 2f - ScreenPadding),
                        startPixels.x, startPixels.y);
                }

                // _size is the size the user dragged to, held in DESIGN units so raising the zoom grows the
                // window with its contents. It is never written back from this clamp, or every A+/A- round
                // trip would eat a little more of it.
                _rect.width = Mathf.Clamp(Px(_size.x), MinSize.x, Screen.width - ScreenPadding);
                _rect.height = Mathf.Clamp(Px(_size.y), MinSize.y, Screen.height - ScreenPadding);

                _rect = GUILayout.Window(WindowId, _rect, DrawWindow, "", ConfigSkin.Window);
                _rect.x = Mathf.Clamp(_rect.x, 0f, Screen.width - Px(OnScreenMarginBase));
                _rect.y = Mathf.Clamp(_rect.y, 0f, Screen.height - Px(OnScreenMarginBase));

                FiresRoundedSkin.DrawPendingTooltip(ConfigSkin.Tip);
                SettingEditWindow.Draw(_editWindowWidget, _editWindowReset);
                ConfigPopup.Draw();
                DrawKeybindPopup();
                CommitOnFocusChange();
            }
            finally { GUI.matrix = matrix; }
        }

        // Scaling shrinks the space the window has to live in, so the first-open size is capped as a share of
        // it rather than being a flat 1140x720 that would end up near-fullscreen at a high GUI scale.
        private static Vector2 StartingSize(float designWidth, float designHeight)
            => new Vector2(Mathf.Min(DefaultSizeBase.x, designWidth * StartingWidthShare),
                           Mathf.Min(DefaultSizeBase.y, designHeight * StartingHeightShare));

        private void DrawWindow(int id)
        {
            DrawHeader();
            GUILayout.Space(4f);

            // A high zoom on a small screen can clamp the window below its own chrome; a negative scroll-view
            // height is an IMGUI layout error rather than a small list.
            float bodyHeight = Mathf.Max(Px(MinBodyHeightBase), _rect.height - BodyChromeHeight);
            if (_filesMode) _filesEditor.Draw(bodyHeight);
            else if (_conflictsOnly) DrawConflictsBody(bodyHeight);
            else
            {
                GUILayout.BeginHorizontal();
                DrawNav(bodyHeight);
                GUILayout.Space(6f);
                DrawBody(bodyHeight);
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(_filesMode
                ? "editing raw files under BepInEx/config — saving reloads the owning mod"
                : _conflictsOnly
                    ? "one row per binding on a contested key   -   Ignore keeps a conflict and stops warning about it"
                    : "gold name = changed from default   ·   grey = locked by the server   ·   double-click a name to open it",
                ConfigSkin.Hint);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            HandleResize();

            // Drawn LAST so every control above it hit-tests the click first; the title row is mostly label
            // and empty space, which is what makes it a dependable grab area.
            GUI.DragWindow(new Rect(0f, 0f, _rect.width, TitleBarHeight));
        }

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Fires Configuration", ConfigSkin.Title, ScaledLayout.Width(Px(170f)));
            GUILayout.FlexibleSpace();
            DrawZoomControls();
            GUILayout.Space(8f);
            if (GUILayout.Button("<color=#FFB0B0><b>X</b></color>", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(28f))))
                Hide();
            GUILayout.EndHorizontal();

            DrawToolbar();
        }

        private void DrawZoomControls()
        {
            float scale = FiresConfigUI.WindowScale;
            if (GUILayout.Button("A-", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(28f)))) NudgeWindowScale(-ZoomStep);
            FiresRoundedSkin.MarkHint("Smaller window and smaller text.");

            if (GUILayout.Button(Mathf.RoundToInt(scale * 100f) + "%", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(50f))))
                SetWindowScale(1f);
            FiresRoundedSkin.MarkHint("Window and text size. Click to reset to 100%.");

            if (GUILayout.Button("A+", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(28f)))) NudgeWindowScale(ZoomStep);
            FiresRoundedSkin.MarkHint("Bigger window and bigger text.");
        }

        private static void NudgeWindowScale(float delta) => SetWindowScale(FiresConfigUI.WindowScale + delta);

        private static void SetWindowScale(float value)
        {
            if (FiresConfigUI.CfgWindowScale == null) return;
            FiresConfigUI.CfgWindowScale.Value = Mathf.Clamp(value, ConfigWindowScale.MinFactor, ConfigWindowScale.MaxFactor);
        }

        // ConfigurationManager's filter toggles are checkboxes, not buttons whose caption reports the current
        // state — a button reading "Simple" is ambiguous about whether that is the state or the action. The
        // box is ASCII: Unity's game font renders Unicode box glyphs as tofu.
        // A checkbox label is a literal at every call site and the colour is a const, so each of its two
        // rendered forms is fixed for the life of the process - they were being rebuilt every IMGUI event.
        private static readonly Dictionary<string, string> _checkboxTextOn =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _checkboxTextOff =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static string CheckboxText(bool value, string label)
        {
            var cache = value ? _checkboxTextOn : _checkboxTextOff;
            if (cache.TryGetValue(label, out var text)) return text;
            text = value
                ? $"<b><color={ChangedColor}>[x] {label}</color></b>"
                : $"[  ] {label}";
            cache[label] = text;
            return text;
        }

        private static bool Checkbox(bool value, string label, float widthBase, string hint)
        {
            string text = CheckboxText(value, label);
            bool clicked = GUILayout.Button(text, ConfigSkin.NavItem, ScaledLayout.Width(Px(widthBase)));
            FiresRoundedSkin.MarkHint(hint);
            return clicked;
        }

        private void OnKeybindFilterChanged()
        {
            ConfigPopup.Close();
            _bodyScroll = Vector2.zero;
            if (!ModIsVisible(_mod)) SelectFirstMod();
        }

        private void DrawToolbar()
        {
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("searchBox");
            _search = GUILayout.TextField(_search ?? "", ConfigSkin.TextInput, ScaledLayout.ExpandedWidth);
            if (GUILayout.Button("Clear", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(52f))))
            {
                _search = "";
                GUIUtility.keyboardControl = 0;
            }

            if (Checkbox(FiresConfigUI.ShowAdvanced, "Advanced", AdvancedCheckboxWidthBase,
                    "Show the deep tuning settings mods tag as advanced.") && FiresConfigUI.CfgShowAdvanced != null)
                FiresConfigUI.CfgShowAdvanced.Value = !FiresConfigUI.ShowAdvanced;

            if (Checkbox(KeybindsOnly, "Keybinds", KeybindCheckboxWidthBase,
                    "Show only keybind settings, across every mod.") && FiresConfigUI.CfgKeybindsOnly != null)
            {
                FiresConfigUI.CfgKeybindsOnly.Value = !KeybindsOnly;
                OnKeybindFilterChanged();
            }

            if (Checkbox(_conflictsOnly, "Conflicts", ConflictsCheckboxWidthBase,
                    "Show only keys more than one mod - or a mod and Valheim - is listening for."))
                SetConflictsOnly(!_conflictsOnly);

            if (Checkbox(!CfgDiscovery.FiresOnly, "All mods", AllModsCheckboxWidthBase,
                    "Unchecked, the list is limited to the Fires family.") && FiresConfigUI.CfgFiresOnly != null)
            {
                FiresConfigUI.CfgFiresOnly.Value = !CfgDiscovery.FiresOnly;
                RebuildDiscovery();
            }

            if (GUILayout.Button(_filesMode ? "<b>Files</b>" : "Files", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(48f))))
                ToggleFilesMode();
            FiresRoundedSkin.MarkHint("Edit the raw .cfg / .yml / .json files under BepInEx/config.");

            if (GUILayout.Button("Collapse", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(66f)))) CollapseAll(true);
            if (GUILayout.Button("Expand", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(60f)))) CollapseAll(false);
            GUILayout.EndHorizontal();
        }

        private void ToggleFilesMode()
        {
            _filesMode = !_filesMode;
            ConfigPopup.Close();
            SettingEditWindow.Close();
            GUIUtility.keyboardControl = 0;
            if (_filesMode) _filesEditor.Refresh();
            else CfgDiscovery.Rebuild();
        }

        private void CollapseAll(bool collapsed)
        {
            if (_mod == null) return;
            foreach (var section in CfgDiscovery.SectionsOf(_mod))
            {
                string key = CollapseKey(_mod, section);
                if (collapsed) _collapsed.Add(key);
                else _collapsed.Remove(key);
            }
        }

        // ---------------------------------------------------------------- left nav (mods -> section index)
        private void DrawNav(float height)
        {
            // Both options in one call, so neither can come from ScaledLayout's single-option arrays - and the
            // height is the live window's, which must not be cached anyway.
            _navScroll = GUILayout.BeginScrollView(_navScroll, GUILayout.Width(NavWidth), GUILayout.Height(height));
            foreach (var mod in CfgDiscovery.ModNames)
            {
                if (!ModIsVisible(mod)) continue;
                bool selected = mod == _mod;
                if (GUILayout.Button(mod, selected ? ConfigSkin.NavSel : ConfigSkin.NavItem))
                {
                    _search = "";
                    GUIUtility.keyboardControl = 0;
                    ConfigPopup.Close();
                    _mod = mod;
                    _navSection = null;
                    _bodyScroll = Vector2.zero;
                }
                if (!selected) continue;

                var sections = CfgDiscovery.SectionsOf(mod);
                for (int i = 0; i < sections.Count; i++)
                {
                    string section = sections[i];
                    if (!SectionIsVisible(mod, section)) continue;
                    bool isCurrent = section == _navSection;
                    string label = (i + 1) + ". " + CfgDiscovery.CleanSection(section);
                    if (GUILayout.Button(label, isCurrent ? ConfigSkin.NavSubOn : ConfigSkin.NavSub))
                    {
                        _navSection = section;
                        string key = CollapseKey(mod, section);
                        _collapsed.Remove(key);
                        _pendingJump = key;
                    }
                }
            }
            GUILayout.EndScrollView();
        }

        // ---------------------------------------------------------------- right body (sections + rows)
        private void DrawBody(float height)
        {
            _bodyScroll = GUILayout.BeginScrollView(_bodyScroll, GUILayout.Height(height));
            if (IsSearching) DrawSearchResults();
            else if (_mod != null) DrawModSections();
            GUILayout.EndScrollView();
        }

        private bool IsSearching => !string.IsNullOrEmpty(_search) && _search.Length >= MinSearchLength;

        private void DrawSearchResults()
        {
            GUILayout.Label("Results for \"" + _search + "\"", ConfigSkin.Title);

            _searchResults.Clear();
            string query = _search.ToLowerInvariant();
            foreach (var descriptor in CfgDiscovery.Descriptors)
            {
                descriptor.RefreshTags();
                if (IsRowHidden(descriptor)) continue;
                if (Matches(descriptor, query)) _searchResults.Add(descriptor);
            }

            string lastGroup = null;
            foreach (var descriptor in _searchResults)
            {
                string group = descriptor.ModName + "  -  " + descriptor.SectionDisplay;
                if (group != lastGroup)
                {
                    lastGroup = group;
                    GUILayout.Space(3f);
                    GUILayout.Label("<b>" + group + "</b>", ConfigSkin.SectionBar);
                }
                DrawRow(descriptor);
            }
            if (_searchResults.Count == 0) GUILayout.Label("No matches.", ConfigSkin.Label);
        }

        private void DrawModSections()
        {
            GUILayout.Label(_mod, ConfigSkin.Title);
            var sections = CfgDiscovery.SectionsOf(_mod);
            for (int i = 0; i < sections.Count; i++)
            {
                string section = sections[i];
                var rows = CfgDiscovery.RowsOf(_mod, section);
                foreach (var descriptor in rows) descriptor.RefreshTags();
                if (!HasVisibleRows(rows)) continue;

                string key = CollapseKey(_mod, section);
                bool collapsed = _collapsed.Contains(key);

                GUILayout.Space(3f);
                string barText = (collapsed ? "[+]  " : "[-]  ") + "<b>" + (i + 1) + ". " + CfgDiscovery.CleanSection(section) + "</b>";
                if (collapsed && HasChangedRows(rows)) barText = $"<color={ChangedColor}>" + barText + "</color>";
                if (GUILayout.Button(barText, ConfigSkin.SectionBar))
                {
                    if (!_collapsed.Remove(key)) _collapsed.Add(key);
                }
                if (Event.current.type == EventType.Repaint && _pendingJump == key)
                {
                    _bodyScroll.y = Mathf.Max(0f, GUILayoutUtility.GetLastRect().y - 6f);
                    _pendingJump = null;
                }
                if (collapsed) continue;

                foreach (var descriptor in rows)
                {
                    if (IsRowHidden(descriptor)) continue;
                    DrawRow(descriptor);
                }
            }
            if (sections.Count == 0) GUILayout.Label("No settings.", ConfigSkin.Label);
        }

        // IMGUI reports repeat clicks on the MouseDown event itself, so the setting name needs no click timer
        // and stays a plain Label rather than becoming a button.
        private static bool WasDoubleClicked()
        {
            var current = Event.current;
            if (current.type != EventType.MouseDown || current.button != 0 || current.clickCount < 2) return false;
            if (!GUILayoutUtility.GetLastRect().Contains(current.mousePosition)) return false;
            current.Use();
            return true;
        }

        private bool IsRowHidden(CfgDescriptor descriptor)
            => (descriptor.Tags.IsAdvanced && !FiresConfigUI.ShowAdvanced)
            || (KeybindsOnly && descriptor.Kind != CtrlKind.KeyBind)
            || ConfigHiddenSettings.IsHidden(descriptor);

        private bool HasVisibleRows(IReadOnlyList<CfgDescriptor> rows)
        {
            foreach (var descriptor in rows)
                if (!IsRowHidden(descriptor)) return true;
            return false;
        }

        private static bool HasChangedRows(IReadOnlyList<CfgDescriptor> rows)
        {
            foreach (var descriptor in rows)
                if (IsChanged(descriptor)) return true;
            return false;
        }

        private void DrawRow(CfgDescriptor descriptor)
        {
            bool wasEnabled = GUI.enabled;
            GUILayout.BeginHorizontal();
            try
            {
                bool locked = descriptor.IsLocked;

                if (!descriptor.Tags.HideSettingName)
                {
                    GUILayout.Label(RowName(descriptor, locked), ConfigSkin.Label, ScaledLayout.Width(NameWidth));
                    FiresRoundedSkin.MarkHint(descriptor.Description);
                    if (WasDoubleClicked()) SettingEditWindow.Open(descriptor);
                }

                if (locked) GUI.enabled = false;
                DrawWidget(descriptor);
                GUILayout.FlexibleSpace();
                if (!descriptor.Tags.HideDefaultButton && GUILayout.Button("Reset", ConfigSkin.ButtonSmall, ScaledLayout.Width(ResetButtonWidth)))
                    ResetToDefault(descriptor);
            }
            catch (Exception ex)
            {
                FiresConfigUI.Log.LogWarning($"row '{descriptor.Section}/{descriptor.Key}' failed: {ex.Message}");
            }
            finally
            {
                GUI.enabled = wasEnabled;
                GUILayout.EndHorizontal();
            }

            if (string.IsNullOrEmpty(descriptor.DescriptionLine)) return;

            // The description wraps under the whole row, so it has to stop short of the Reset column or long
            // text runs straight underneath those buttons.
            GUILayout.BeginHorizontal();
            GUILayout.Label(descriptor.DescriptionLine, ConfigSkin.Desc);
            FiresRoundedSkin.MarkHint(descriptor.Description);
            GUILayout.Space(DescriptionRightGutter);
            GUILayout.EndHorizontal();
        }

        private static string RowName(CfgDescriptor descriptor, bool locked)
        {
            if (locked) return $"<color={LockedColor}>{descriptor.Label}  (locked)</color>";
            return IsChanged(descriptor) ? $"<b><color={ChangedColor}>{descriptor.Label}</color></b>" : descriptor.Label;
        }

        private void ResetToDefault(CfgDescriptor descriptor)
        {
            try { descriptor.Entry.BoxedValue = descriptor.Default; } catch { }
            ClearBuffers(descriptor);
        }

        // The edit window draws the SAME setting as its row, so its controls need their own names or the two
        // fight over IMGUI focus and the shared edit buffer.
        private void DrawWidgetInEditWindow(CfgDescriptor descriptor)
        {
            _controlScope = EditWindowScope;
            try { DrawWidget(descriptor); }
            finally { _controlScope = ""; }
        }

        private string ControlId(CfgDescriptor descriptor, string variant) => IdOf(descriptor) + variant + _controlScope;

        private void ClearBuffers(CfgDescriptor descriptor)
        {
            string id = IdOf(descriptor);
            foreach (string scope in ControlScopes)
            {
                _editBuf.Remove(id + scope);
                _editBuf.Remove(id + NumberVariant + scope);
                for (int i = 0; i < VectorComponentIds.Length; i++) _editBuf.Remove(id + VectorComponentIds[i] + scope);
            }
        }

        private static readonly string[] ControlScopes = { "", EditWindowScope };
        private static readonly string[] VectorComponentIds = { ":x", ":y", ":z", ":w" };
        private static readonly string[] VectorComponentLabels = { "X", "Y", "Z", "W" };

        // ---------------------------------------------------------------- typed widgets
        private void DrawWidget(CfgDescriptor descriptor)
        {
            if (DrawCustomWidget(descriptor)) return;

            object value = descriptor.BoxedValue;
            switch (descriptor.Kind)
            {
                case CtrlKind.Bool: DrawBool(descriptor, value); break;
                case CtrlKind.IntRange:
                case CtrlKind.FloatRange: DrawRange(descriptor, value); break;
                case CtrlKind.Enum:
                case CtrlKind.ValueList: DrawDropdown(descriptor, value); break;
                case CtrlKind.FlagsEnum: DrawFlags(descriptor, value); break;
                case CtrlKind.IntField:
                case CtrlKind.FloatField: DrawNumberField(descriptor, value); break;
                case CtrlKind.String: DrawStringField(descriptor, value); break;
                case CtrlKind.Color: DrawColorField(descriptor, value); break;
                case CtrlKind.KeyBind: DrawKeyBind(descriptor, value); break;
                case CtrlKind.Vector2: DrawVector(descriptor, VectorOf((Vector2)value), 2); break;
                case CtrlKind.Vector3: DrawVector(descriptor, VectorOf((Vector3)value), 3); break;
                case CtrlKind.Vector4: DrawVector(descriptor, (Vector4)value, 4); break;
                case CtrlKind.Quaternion: DrawVector(descriptor, VectorOf((Quaternion)value), 4); break;
                case CtrlKind.Serialized: DrawSerializedField(descriptor, value); break;
                default:
                    GUILayout.Label($"<color={OffColor}>{value?.ToString() ?? "null"}</color>", ConfigSkin.Label, ScaledLayout.Width(Px(220f)));
                    break;
            }
        }

        private bool DrawCustomWidget(CfgDescriptor descriptor)
        {
            var drawer = descriptor.Tags.CustomDrawer;
            if (drawer == null) return false;
            string id = IdOf(descriptor);
            if (_customDrawerFailed.Contains(id)) return false;
            try
            {
                GUILayout.BeginVertical();
                try { drawer(descriptor.Entry); }
                finally { GUILayout.EndVertical(); }
                return true;
            }
            catch (Exception ex)
            {
                _customDrawerFailed.Add(id);
                FiresConfigUI.Log.LogWarning($"custom drawer for '{descriptor.Key}' failed, falling back: {ex.Message}");
                return false;
            }
        }

        private void DrawBool(CfgDescriptor descriptor, object value)
        {
            bool isOn = value is bool flag && flag;
            string text = isOn ? $"<b><color={OnColor}>Enabled</color></b>" : $"<color={OffColor}>Disabled</color>";
            if (GUILayout.Button(text, ConfigSkin.Button, ScaledLayout.Width(Px(92f)))) Set(descriptor, !isOn);
        }

        private void DrawRange(CfgDescriptor descriptor, object value)
        {
            float current = Convert.ToSingle(value, CultureInfo.InvariantCulture);
            float updated = GUILayout.HorizontalSlider(current, (float)descriptor.Min, (float)descriptor.Max,
                ConfigSkin.Slider, ConfigSkin.SliderThumb, ScaledLayout.Width(WidgetWidth));
            if (!Mathf.Approximately(updated, current))
            {
                current = SnapStep(updated, descriptor);
                Set(descriptor, ToRangeType(current, descriptor));
                _editBuf.Remove(ControlId(descriptor, NumberVariant));
            }

            if (descriptor.Tags.ShowRangeAsPercent)
            {
                float span = Mathf.Abs((float)(descriptor.Max - descriptor.Min));
                float fraction = span > 0f ? Mathf.Abs(current - (float)descriptor.Min) / span : 0f;
                GUILayout.Label(fraction.ToString("P0", CultureInfo.InvariantCulture), ConfigSkin.Field, ScaledLayout.Width(NumberFieldWidth));
                return;
            }

            string typed = BufferedField(descriptor, ":n", FormatNumber(descriptor, current), NumberFieldWidth);
            if (typed != null && TryParseNumber(typed, descriptor.Type, out object parsed))
            {
                float clamped = Mathf.Clamp(Convert.ToSingle(parsed, CultureInfo.InvariantCulture), (float)descriptor.Min, (float)descriptor.Max);
                if (!Mathf.Approximately(clamped, current)) Set(descriptor, ToRangeType(clamped, descriptor));
            }
        }

        private static object ToRangeType(float value, CfgDescriptor descriptor)
            => descriptor.Kind == CtrlKind.IntRange
                ? Convert.ChangeType((long)Math.Round(value), descriptor.Type, CultureInfo.InvariantCulture)
                : Convert.ChangeType(value, descriptor.Type, CultureInfo.InvariantCulture);

        private void DrawDropdown(CfgDescriptor descriptor, object value)
        {
            var names = descriptor.Options ?? Array.Empty<string>();
            string id = ControlId(descriptor, "");
            int selected = IndexOfValue(descriptor, value);
            string label = selected >= 0 && selected < names.Length ? names[selected] : value?.ToString() ?? "";

            if (GUILayout.Button(label + "   v", ConfigSkin.Button, ScaledLayout.Width(DropdownWidth)) && names.Length > 0)
                OpenDropdown(id, descriptor, names, selected);
            ConfigPopup.AnchorToLastRect(id);
        }

        private void OpenDropdown(string id, CfgDescriptor descriptor, string[] names, int selected)
        {
            float height = Mathf.Min(DropdownMaxHeight, names.Length * DropdownRowHeight + 24f);
            ConfigPopup.Toggle(id, new Vector2(DropdownWidth + 40f, height), inner =>
            {
                ConfigPopup.Scroll = GUILayout.BeginScrollView(ConfigPopup.Scroll, GUILayout.Height(inner.height - 24f));
                for (int i = 0; i < names.Length; i++)
                {
                    bool isCurrent = i == selected;
                    if (!GUILayout.Button(names[i], isCurrent ? ConfigSkin.NavSubOn : ConfigSkin.NavItem)) continue;
                    SetOption(descriptor, i);
                    ConfigPopup.Close();
                    break;
                }
                GUILayout.EndScrollView();
            });
        }

        private static int IndexOfValue(CfgDescriptor descriptor, object value)
        {
            var values = descriptor.OptionValues;
            if (values == null || value == null) return -1;
            for (int i = 0; i < values.Length; i++)
                if (Equals(values[i], value)) return i;
            return -1;
        }

        private void DrawFlags(CfgDescriptor descriptor, object value)
        {
            var values = descriptor.OptionValues ?? Array.Empty<object>();
            long current = Convert.ToInt64(value, CultureInfo.InvariantCulture);

            GUILayout.BeginVertical(ScaledLayout.Width(WidgetWidth * 2f));
            for (int i = 0; i < values.Length; i++)
            {
                long flag = Convert.ToInt64(values[i], CultureInfo.InvariantCulture);
                if (flag == 0L) continue;
                bool isSet = (current & flag) == flag;
                bool toggled = GUILayout.Toggle(isSet, "  " + descriptor.Options[i], ConfigSkin.Label);
                if (toggled == isSet) continue;
                long updated = toggled ? current | flag : current & ~flag;
                Set(descriptor, Enum.ToObject(descriptor.Type, updated));
                current = updated;
            }
            GUILayout.EndVertical();
        }

        private void DrawNumberField(CfgDescriptor descriptor, object value)
        {
            string typed = BufferedField(descriptor, "", Convert.ToString(value, CultureInfo.InvariantCulture), 110f);
            if (typed != null && TryParseNumber(typed, descriptor.Type, out object parsed) && !Equals(parsed, value))
                Set(descriptor, parsed);
        }

        private void DrawStringField(CfgDescriptor descriptor, object value)
        {
            string current = (string)value ?? "";
            string typed = BufferedField(descriptor, "", current, StringFieldWidth);
            if (typed != null && typed != current && !IsFocused(ControlId(descriptor, ""))) Set(descriptor, typed);
        }

        private void DrawSerializedField(CfgDescriptor descriptor, object value)
        {
            string id = ControlId(descriptor, "");
            string current = SerializeValue(descriptor, value);
            string typed = BufferedField(descriptor, "", current, StringFieldWidth);
            if (typed == null || typed == current || IsFocused(id)) return;
            try { Set(descriptor, descriptor.Converter.ConvertToObject(typed, descriptor.Type)); }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning($"'{descriptor.Key}' rejected '{typed}': {ex.Message}"); }
        }

        private static string SerializeValue(CfgDescriptor descriptor, object value)
        {
            try { return descriptor.Converter.ConvertToString(value, descriptor.Type); }
            catch { return value?.ToString() ?? ""; }
        }

        private void DrawVector(CfgDescriptor descriptor, Vector4 current, int components)
        {
            var updated = current;
            bool changed = false;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < components; i++)
            {
                GUILayout.Label(VectorComponentLabels[i], ConfigSkin.Label, ScaledLayout.Width(Px(14f)));
                string typed = BufferedField(descriptor, VectorComponentIds[i], current[i].ToString("0.###", CultureInfo.InvariantCulture), VectorFieldWidth);
                if (typed == null || !float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) continue;
                if (Mathf.Approximately(parsed, current[i])) continue;
                updated[i] = parsed;
                changed = true;
            }
            GUILayout.EndHorizontal();
            if (changed) Set(descriptor, FromVector(descriptor.Kind, updated));
        }

        private static Vector4 VectorOf(Vector2 value) => new Vector4(value.x, value.y, 0f, 0f);
        private static Vector4 VectorOf(Vector3 value) => new Vector4(value.x, value.y, value.z, 0f);
        private static Vector4 VectorOf(Quaternion value) => new Vector4(value.x, value.y, value.z, value.w);

        private static object FromVector(CtrlKind kind, Vector4 value)
        {
            switch (kind)
            {
                case CtrlKind.Vector2: return new Vector2(value.x, value.y);
                case CtrlKind.Vector3: return new Vector3(value.x, value.y, value.z);
                case CtrlKind.Quaternion: return new Quaternion(value.x, value.y, value.z, value.w);
                default: return value;
            }
        }

        private const float ColorSwatchWidthBase = 20f;
        private const float ColorSwatchHeightBase = 18f;

        // A call passing TWO options cannot take two of ScaledLayout's single-option arrays, so the swatches
        // keep their own pairs, rebuilt only when the window scale changes.
        private static float _swatchOptionsFactor = -1f;
        private static GUILayoutOption[] _swatchSizeOptions;
        private static GUILayoutOption[] _swatchRowOptions;

        private static void EnsureSwatchOptions()
        {
            if (_swatchOptionsFactor == ConfigWindowScale.Factor) return;
            _swatchOptionsFactor = ConfigWindowScale.Factor;
            _swatchSizeOptions = new[]
            {
                GUILayout.Width(Px(ColorSwatchWidthBase)),
                GUILayout.Height(Px(ColorSwatchHeightBase)),
            };
            _swatchRowOptions = new[]
            {
                GUILayout.ExpandWidth(true),
                GUILayout.Height(Px(ColorSwatchHeightBase)),
            };
        }

        private void DrawColorField(CfgDescriptor descriptor, object value)
        {
            EnsureSwatchOptions();
            var color = value is Color colorValue ? colorValue : Color.white;
            string id = ControlId(descriptor, "");

            var previousBackground = GUI.backgroundColor;
            GUI.backgroundColor = color;
            if (GUILayout.Button(ColorToHex(color), ConfigSkin.Button, ScaledLayout.Width(DropdownWidth)))
                OpenColorPicker(id, descriptor);
            GUI.backgroundColor = previousBackground;
            ConfigPopup.AnchorToLastRect(id);

            var previousTint = GUI.color;
            GUI.color = color;
            GUILayout.Label(GUIContent.none, ConfigSkin.Swatch, _swatchSizeOptions);
            GUI.color = previousTint;
        }

        private void OpenColorPicker(string id, CfgDescriptor descriptor)
        {
            ConfigPopup.Toggle(id, new Vector2(ColorPickerWidth, ColorPickerHeight), _ =>
            {
                EnsureSwatchOptions();
                var color = descriptor.BoxedValue is Color live ? live : Color.white;
                var updated = color;
                updated.r = DrawChannelSlider("R", color.r);
                updated.g = DrawChannelSlider("G", color.g);
                updated.b = DrawChannelSlider("B", color.b);
                updated.a = DrawChannelSlider("A", color.a);
                if (updated != color) Set(descriptor, updated);

                GUILayout.Space(4f);
                GUILayout.BeginHorizontal();
                GUILayout.Label(ColorToHex(updated), ConfigSkin.Field, ScaledLayout.Width(Px(90f)));
                var previousTint = GUI.color;
                GUI.color = updated;
                GUILayout.Label(GUIContent.none, ConfigSkin.Swatch, _swatchRowOptions);
                GUI.color = previousTint;
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Done", ConfigSkin.ButtonSmall)) ConfigPopup.Close();
            });
        }

        private static float DrawChannelSlider(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, ConfigSkin.Label, ScaledLayout.Width(Px(14f)));
            float updated = GUILayout.HorizontalSlider(value, 0f, 1f, ConfigSkin.Slider, ConfigSkin.SliderThumb, ScaledLayout.ExpandedWidth);
            GUILayout.Label(Mathf.RoundToInt(updated * 255f).ToString(), ConfigSkin.Label, ScaledLayout.Width(Px(30f)));
            GUILayout.EndHorizontal();
            return updated;
        }

        private static string ColorToHex(Color color)
            => "#" + ColorUtility.ToHtmlStringRGBA(color);

        private void DrawKeyBind(CfgDescriptor descriptor, object value)
        {
            if (DrawCaptureConfirm(descriptor)) return;

            // The value box IS the capture control (ConfigurationManager parity): click it to start
            // recording, click it or Apply to commit, Esc or X to cancel. Keys accumulate while recording,
            // so chords like L + LeftAlt are recordable; nothing commits until the second click.
            bool capturingThis = _capturing && _captureTarget == descriptor.Entry;
            string boxLabel = capturingThis ? CaptureLabel() : BindLabel(value);

            if (GUILayout.Button(boxLabel, ConfigSkin.Button, ScaledLayout.Width(KeyBindWidth)))
            {
                if (capturingThis) CommitCapture(descriptor);
                else BeginCapture(descriptor.Entry);
            }
            if (capturingThis && Event.current.type == EventType.Repaint) RecordCaptureButtonRect();

            var previousBackground = GUI.backgroundColor;
            if (capturingThis) GUI.backgroundColor = CaptureButtonGreen;
            if (GUILayout.Button(capturingThis ? "Apply" : "Set", ConfigSkin.ButtonSmall, ScaledLayout.Width(ResetButtonWidth)))
            {
                if (capturingThis) CommitCapture(descriptor);
                else BeginCapture(descriptor.Entry);
            }
            GUI.backgroundColor = previousBackground;

            if (GUILayout.Button("X", ConfigSkin.ButtonSmall, ScaledLayout.Width(ClearButtonWidth)))
            {
                if (capturingThis) CancelCapture();
                else Set(descriptor, descriptor.Type == typeof(KeyboardShortcut) ? (object)KeyboardShortcut.Empty : (object)KeyCode.None);
            }

            DrawCaptureNotice(descriptor);
        }

        private static string BindLabel(object value)
        {
            string text = value?.ToString();
            return string.IsNullOrEmpty(text) || text == "None" ? $"<color={OffColor}>None</color>" : text;
        }

        private string CaptureLabel()
        {
            string chord = FormatChord();
            return chord.Length == 0
                ? $"<b><color={CaptureColor}>press keys…</color></b>"
                : $"<b><color={CaptureColor}>{chord}</color></b>";
        }

        // PollKeyCapture runs in Update, outside GUI, so the commit/cancel buttons are remembered in SCREEN
        // space and mouse presses over them are not recorded as binds.
        private void RecordCaptureButtonRect()
        {
            var lastRect = GUILayoutUtility.GetLastRect();
            var row = new Rect(lastRect.x - CaptureRectPadding, lastRect.y - CaptureRectPadding,
                lastRect.width + ResetButtonWidth + ClearButtonWidth + CaptureRectPadding * 5f,
                lastRect.height + CaptureRectPadding * 2f);
            _capButtonsScreenRect = ConfigWindowScale.GuiToScreenRect(row);
        }

        private void DrawCaptureNotice(CfgDescriptor descriptor)
        {
            if (_capNotice == null || _capNoticeId != IdOf(descriptor)) return;
            if (Time.unscaledTime > _capNoticeUntil) { _capNotice = null; _capNoticeId = null; return; }
            GUILayout.Label($"<color={NoticeColor}>{_capNotice}</color>", ConfigSkin.Hint);
        }

        // A text field with a per-setting edit buffer: while focused the raw typed text survives even when it
        // doesn't parse yet ("1.", "-"); when not focused the field always shows the live value.
        private string BufferedField(CfgDescriptor descriptor, string suffix, string liveText, float width)
        {
            string id = ControlId(descriptor, suffix);
            bool focused = IsFocused(id);
            string shown = focused && _editBuf.TryGetValue(id, out var buffered) ? buffered : liveText;
            GUI.SetNextControlName(id);
            string typed = GUILayout.TextField(shown, ConfigSkin.TextInput, GUILayout.Width(width));
            if (IsFocused(id)) _editBuf[id] = typed;
            return typed;
        }

        private static bool IsFocused(string controlName) => GUI.GetNameOfFocusedControl() == controlName;

        // Text-shaped settings commit when focus leaves the field; numbers commit live on a successful parse.
        private static string StripControlScope(string controlName)
            => controlName.EndsWith(EditWindowScope, StringComparison.Ordinal)
                ? controlName.Substring(0, controlName.Length - EditWindowScope.Length)
                : controlName;

        private void CommitOnFocusChange()
        {
            string now = GUI.GetNameOfFocusedControl();
            if (now == _lastFocus) return;
            string previous = _lastFocus;
            _lastFocus = now;
            if (string.IsNullOrEmpty(previous) || !_editBuf.TryGetValue(previous, out var buffered)) return;
            _editBuf.Remove(previous);
            if (!_byId.TryGetValue(StripControlScope(previous), out var descriptor)) return;

            if (descriptor.Kind == CtrlKind.String && buffered != descriptor.BoxedValue as string) Set(descriptor, buffered ?? "");
            else if (descriptor.Kind == CtrlKind.Serialized && buffered != SerializeValue(descriptor, descriptor.BoxedValue))
            {
                try { Set(descriptor, descriptor.Converter.ConvertToObject(buffered ?? "", descriptor.Type)); }
                catch (Exception ex) { FiresConfigUI.Log.LogWarning($"'{descriptor.Key}' rejected '{buffered}': {ex.Message}"); }
            }
        }

        // ---------------------------------------------------------------- write-back
        private void Set(CfgDescriptor descriptor, object value)
        {
            try { descriptor.Entry.BoxedValue = value; }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning($"set '{descriptor.Key}' failed: {ex.Message}"); }
        }

        private void SetOption(CfgDescriptor descriptor, int index)
        {
            var values = descriptor.OptionValues;
            if (values == null || index < 0 || index >= values.Length) return;
            try { descriptor.Entry.BoxedValue = values[index]; }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning($"set option '{descriptor.Key}' failed: {ex.Message}"); }
        }

        // ---------------------------------------------------------------- keybind capture
        private void BeginCapture(ConfigEntryBase entry)
        {
            ConfigPopup.Close();
            _capturing = true;
            _captureTarget = entry;
            _captureEntry = null;
            _capMain = KeyCode.None;
            _capMods.Clear();
            _capButtonsScreenRect = default;
            GUIUtility.keyboardControl = 0;
        }

        private void CancelCapture()
        {
            _capturing = false;
            _captureTarget = null;
            _captureEntry = null;
            _capMain = KeyCode.None;
            _capMods.Clear();
        }

        private void PollKeyCapture()
        {
            if (_captureTarget == null) { _capturing = false; return; }
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { CancelCapture(); return; }
            if (UnityEngine.Input.GetKeyDown(KeyCode.Delete)) { UnbindCaptureTarget(); return; }

            foreach (KeyCode keyCode in Enum.GetValues(typeof(KeyCode)))
            {
                if (!UnityEngine.Input.GetKeyDown(keyCode)) continue;
                if (keyCode >= KeyCode.Mouse0 && keyCode <= KeyCode.Mouse6 && IsOverCaptureButtons()) continue;

                if (Array.IndexOf(s_modifiers, keyCode) >= 0)
                {
                    if (!_capMods.Contains(keyCode)) _capMods.Add(keyCode);
                }
                else _capMain = keyCode;
            }
        }

        private bool IsOverCaptureButtons()
        {
            var mouseInGuiSpace = new Vector2(UnityEngine.Input.mousePosition.x, Screen.height - UnityEngine.Input.mousePosition.y);
            return _capButtonsScreenRect.Contains(mouseInGuiSpace);
        }

        private void CommitCapture(CfgDescriptor descriptor)
        {
            var target = _captureTarget;
            bool wantsShortcut = target != null && target.SettingType == typeof(KeyboardShortcut);
            KeyCode main = _capMain;
            var modifiers = new List<KeyCode>(_capMods);

            if (main == KeyCode.None && modifiers.Count == 0) { CancelCapture(); return; }
            if (main == KeyCode.None) { main = modifiers[0]; modifiers.RemoveAt(0); }
            if (!wantsShortcut && modifiers.Count > 0)
            {
                Notice(descriptor, $"single-key setting - stored {main}, modifiers ignored");
                modifiers.Clear();
            }

            CancelCapture();
            if (OfferCaptureConfirm(descriptor, target, main, modifiers, wantsShortcut)) return;
            WriteCapture(target, main, modifiers, wantsShortcut);
        }

        private void WriteCapture(ConfigEntryBase target, KeyCode main, List<KeyCode> modifiers, bool wantsShortcut)
        {
            if (target == null) return;
            try
            {
                if (wantsShortcut) target.BoxedValue = new KeyboardShortcut(main, modifiers.ToArray());
                else target.BoxedValue = main;
            }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning("keybind capture failed: " + ex.Message); }
        }

        private void UnbindCaptureTarget()
        {
            var target = _captureTarget;
            CancelCapture();
            if (target == null) return;
            try
            {
                target.BoxedValue = target.SettingType == typeof(KeyboardShortcut)
                    ? (object)KeyboardShortcut.Empty
                    : (object)KeyCode.None;
            }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning("keybind unbind failed: " + ex.Message); }
        }

        private void Notice(CfgDescriptor descriptor, string text)
        {
            _capNotice = text;
            _capNoticeId = IdOf(descriptor);
            _capNoticeUntil = Time.unscaledTime + CaptureNoticeSeconds;
        }

        private string FormatChord()
        {
            var parts = new List<string>();
            foreach (var modifier in _capMods) parts.Add(modifier.ToString());
            if (_capMain != KeyCode.None) parts.Add(_capMain.ToString());
            return string.Join(" + ", parts);
        }

        // ---------------------------------------------------------------- resize grip
        // Called from OnGUI (never from Update) because writing hotControl outside the GUI loop is not safe:
        // closing the panel mid-drag would otherwise leave IMGUI routing every mouse event to a control that
        // no longer draws.
        private void ReleaseResizeCapture()
        {
            if (_resizeHotControl == 0) return;
            if (GUIUtility.hotControl == _resizeHotControl) GUIUtility.hotControl = 0;
            _resizeHotControl = 0;
        }

        // The grip has to TAKE GUIUtility.hotControl on mouse-down. Without it IMGUI stops routing mouse
        // events to this window the moment the cursor leaves its rect — which dragging a bottom-right grip
        // outwards does immediately — so the resize died after one frame and the released button was never
        // seen, leaving the panel stuck in a resizing state.
        private void HandleResize()
        {
            var grip = new Rect(_rect.width - GripSize - GripInset, _rect.height - GripSize - GripInset, GripSize, GripSize);
            GUI.Label(grip, "//", ConfigSkin.Hint);

            int controlId = GUIUtility.GetControlID(ResizeControlHint, FocusType.Passive);
            var current = Event.current;
            switch (current.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (current.button != 0 || !grip.Contains(current.mousePosition)) break;
                    GUIUtility.hotControl = controlId;
                    _resizeHotControl = controlId;
                    current.Use();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != controlId) break;
                    _size.x = Mathf.Clamp(_size.x + current.delta.x / ConfigWindowScale.Factor, MinSizeBase.x, DesignWidth - ScreenPaddingBase);
                    _size.y = Mathf.Clamp(_size.y + current.delta.y / ConfigWindowScale.Factor, MinSizeBase.y, DesignHeight - ScreenPaddingBase);
                    current.Use();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != controlId) break;
                    GUIUtility.hotControl = 0;
                    _resizeHotControl = 0;
                    current.Use();
                    break;
            }
        }

        // ---------------------------------------------------------------- helpers
        private static string CollapseKey(string mod, string section) => (mod ?? "") + "|" + (section ?? "");

        private static bool IsChanged(CfgDescriptor descriptor)
        {
            try
            {
                object current = descriptor.BoxedValue, defaultValue = descriptor.Default;
                if (current == null) return defaultValue != null;
                return !current.Equals(defaultValue);
            }
            catch { return false; }
        }

        private static bool Matches(CfgDescriptor descriptor, string lowercaseQuery)
        {
            return Contains(descriptor.Key, lowercaseQuery)
                || Contains(descriptor.Label, lowercaseQuery)
                || Contains(descriptor.Section, lowercaseQuery)
                || Contains(descriptor.ModName, lowercaseQuery)
                || Contains(descriptor.ModGuid, lowercaseQuery)
                || Contains(descriptor.Description, lowercaseQuery);
        }

        private static bool Contains(string text, string lowercaseQuery)
            => !string.IsNullOrEmpty(text) && text.IndexOf(lowercaseQuery, StringComparison.OrdinalIgnoreCase) >= 0;

        private static float SnapStep(float raw, CfgDescriptor descriptor)
        {
            if (descriptor.Step <= 0.0) return raw;
            double steps = Math.Round((raw - descriptor.Min) / descriptor.Step);
            return (float)Math.Min(descriptor.Max, Math.Max(descriptor.Min, descriptor.Min + steps * descriptor.Step));
        }

        private static string FormatNumber(CfgDescriptor descriptor, float number)
            => descriptor.Kind == CtrlKind.IntRange
                ? ((long)Math.Round(number)).ToString(CultureInfo.InvariantCulture)
                : number.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool TryParseNumber(string text, Type targetType, out object value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (targetType == typeof(float) || targetType == typeof(double) || targetType == typeof(decimal))
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsedDouble)
                    && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedDouble)) return false;
                try { value = Convert.ChangeType(parsedDouble, targetType, CultureInfo.InvariantCulture); return true; }
                catch { return false; }
            }
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out long parsedLong)
                && !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedLong)) return false;
            try { value = Convert.ChangeType(parsedLong, targetType, CultureInfo.InvariantCulture); return true; }
            catch { return false; }
        }
    }
}
