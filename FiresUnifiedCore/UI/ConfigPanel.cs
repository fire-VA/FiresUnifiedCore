using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// The Fires config window (F8 / va_config) — an IMGUI split view laid out like BepInEx ConfigurationManager
    /// with the shared rounded Fires chrome (FiresRoundedSkin). LEFT: every discovered mod; the selected mod
    /// expands in-place to a numbered section index, and clicking a section expands + scrolls to it on the right.
    /// RIGHT: the selected mod's sections as collapsible [-]/[+] bars with rows of
    /// name | typed widget | Reset (+ a muted description line). Search filters across every mod and forces
    /// matches expanded. Gold setting/section names mark non-default values (shudnal's "Changed" tint).
    /// Data source is CfgDiscovery; write-back is ConfigEntry.BoxedValue.
    /// </summary>
    public class ConfigPanel : MonoBehaviour
    {
        private const int WindowId = 0xF1C0;
        private const float WindowAlpha = 0.92f;
        private const float NavWidth = 250f;
        private const float NameWidth = 250f;
        private static readonly Vector2 DefaultSize = new Vector2(1140f, 720f);
        private static readonly Vector2 MinSize = new Vector2(760f, 420f);

        private static ConfigPanel _instance;
        private static bool _cmdRegistered;

        public static void EnsureHost()
        {
            if (_instance != null) return;
            var go = new GameObject("FiresConfigPanelHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<ConfigPanel>();
            FiresCore.Bridge.ModUiRegistry.Register("FiresConfigPanel", () => IsOpen, () => _instance?.Hide(), null);
        }

        public static void Toggle() { EnsureHost(); _instance.ToggleInternal(); }

        public static bool IsOpen => _instance != null && _instance._open;

        // ----- instance -----
        private bool _open;
        private Rect _rect = new Rect(-1f, -1f, 0f, 0f);
        private Vector2 _size = DefaultSize;
        private Vector2 _navScroll;
        private Vector2 _bodyScroll;
        private string _search = "";
        private string _mod;
        private string _navSection;
        private bool _resizing;
        private bool _hudHidden;

        // Collapsed (mod|section) keys; runtime-only, like shudnal's category.Collapsed.
        private readonly HashSet<string> _collapsed = new HashSet<string>();

        // Right-pane Y of each section bar, captured on Repaint so a left-nav click can scroll to it.
        private readonly Dictionary<string, float> _sectionY = new Dictionary<string, float>();
        private string _pendingJump;

        // Per-field edit buffers so a half-typed number ("1.", "-") never reverts mid-edit; keyed by control id.
        private readonly Dictionary<string, string> _editBuf = new Dictionary<string, string>();
        private readonly Dictionary<string, CfgDescriptor> _byId = new Dictionary<string, CfgDescriptor>();
        private string _lastFocus = "";
        private readonly object _textToken = new object();

        private bool _capturing;
        private ConfigEntryBase _captureTarget;
        // Record-then-apply capture: keys accumulate while recording (modifiers collect, the last
        // non-modifier becomes the main key) and NOTHING commits until Apply is clicked. This is
        // what makes multi-key chords like L + LeftAlt settable at all — the old flow committed on
        // the first keydown, so the modifier itself ended the capture.
        private KeyCode _capMain = KeyCode.None;
        private readonly List<KeyCode> _capMods = new List<KeyCode>();
        private Rect _capButtonsScreenRect;          // Apply/X screen rect — mouse presses here aren't recorded
        private string _capNotice;                   // transient per-commit feedback ("modifiers ignored…")
        private string _capNoticeId;
        private float _capNoticeUntil;

        private static readonly KeyCode[] s_modifiers =
        {
            KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift,
            KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr, KeyCode.LeftCommand, KeyCode.RightCommand,
        };

        // ---------------------------------------------------------------- lifecycle
        private void Update()
        {
            if (!_cmdRegistered) { try { RegisterCommands(); _cmdRegistered = true; } catch { } }
            if (_capturing) { PollKeyCapture(); return; }

            bool toggle = FiresConfigUI.CfgHotkey != null
                ? FiresConfigUI.CfgHotkey.Value.IsDown()
                : UnityEngine.Input.GetKeyDown(FiresConfigUI.ToggleKey);
            if (toggle) ToggleInternal();

            if (!_open) return;
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { Hide(); return; }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // While an IMGUI text control is focused, gate hotkeys so typing in search/fields doesn't fire them.
            if (GUIUtility.keyboardControl != 0) Input.FiresInputBlock.Acquire(_textToken);
            else Input.FiresInputBlock.Release(_textToken);
        }

        private static void RegisterCommands()
        {
            new Terminal.ConsoleCommand("va_config", "Toggle the Fires configuration window.", _ => Toggle(), isCheat: false);
            new Terminal.ConsoleCommand("va_config_dump", "Log every discovered Fires config (N mods, M entries).", _ => CfgDiscovery.DumpToLog(), isCheat: false);
        }

        private void ToggleInternal() { if (_open) Hide(); else Show(); }

        public void Show()
        {
            try
            {
                CfgDiscovery.Rebuild();
                BuildIdMap();
                if (string.IsNullOrEmpty(_mod) || CfgDiscovery.SectionsOf(_mod).Count == 0) SelectFirst();
                _open = true;
                InputBlock.Block(true);
                HideGameUi(true);
                FiresConfigUI.Log.LogInfo($"ConfigPanel opened: {CfgDiscovery.ModNames.Count} mod(s), {CfgDiscovery.Descriptors.Count} entries.");
            }
            catch (Exception ex) { FiresConfigUI.Log.LogError("ConfigPanel.Show failed: " + ex); }
        }

        public void Hide()
        {
            _capturing = false; _captureTarget = null;
            _open = false;
            _editBuf.Clear();
            _resizing = false;
            InputBlock.Block(false);
            HideGameUi(false);
            Input.FiresInputBlock.Release(_textToken);
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
            _navSection = null;
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
            if (!_open) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Valheim's own OnGUI can leave GUI.matrix scaled; inheriting it draws the window off-screen
            // (the documented "renders but invisible" trap). Draw in identity space, restore after.
            var matrix = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;
            try
            {
                FiresRoundedSkin.Ensure(WindowAlpha);

                _size.x = Mathf.Clamp(_size.x, MinSize.x, Screen.width - 20f);
                _size.y = Mathf.Clamp(_size.y, MinSize.y, Screen.height - 20f);
                if (_rect.x < 0f)
                    _rect = new Rect((Screen.width - _size.x) / 2f, Mathf.Max(20f, (Screen.height - _size.y) / 2f - 20f), _size.x, _size.y);
                _rect.width = _size.x;
                _rect.height = _size.y;

                _rect = GUILayout.Window(WindowId, _rect, DrawWindow, "", FiresRoundedSkin.Window);
                _rect.x = Mathf.Clamp(_rect.x, 0f, Screen.width - 80f);
                _rect.y = Mathf.Clamp(_rect.y, 0f, Screen.height - 40f);

                CommitOnFocusChange();
            }
            finally { GUI.matrix = matrix; }
        }

        private void DrawWindow(int id)
        {
            DrawHeader();
            GUILayout.Space(4f);

            float bodyHeight = _rect.height - 92f;
            GUILayout.BeginHorizontal();
            DrawNav(bodyHeight);
            GUILayout.Space(6f);
            DrawBody(bodyHeight);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("gold name = changed from default", FiresRoundedSkin.Hint);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            HandleResize();
            GUI.DragWindow(new Rect(0f, 0f, _rect.width - 30f, 26f));
        }

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Fires Configuration", FiresRoundedSkin.Title, GUILayout.Width(150f));
            GUI.SetNextControlName("searchBox");
            _search = GUILayout.TextField(_search ?? "", FiresRoundedSkin.TextInput, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Clear", FiresRoundedSkin.ButtonSmall, GUILayout.Width(52f)))
            {
                _search = "";
                GUIUtility.keyboardControl = 0;
            }
            bool showAll = FiresConfigUI.CfgShowAllPlugins != null && FiresConfigUI.CfgShowAllPlugins.Value;
            string allTxt = showAll ? "<b>All plugins</b>" : "Fires only";
            if (GUILayout.Button(allTxt, FiresRoundedSkin.ButtonSmall, GUILayout.Width(86f)) && FiresConfigUI.CfgShowAllPlugins != null)
            {
                FiresConfigUI.CfgShowAllPlugins.Value = !showAll;   // SettingChanged triggers CfgDiscovery.Rebuild
                BuildIdMap();
                if (string.IsNullOrEmpty(_mod) || CfgDiscovery.SectionsOf(_mod).Count == 0) SelectFirst();
            }
            if (GUILayout.Button("<color=#FFB0B0><b>X</b></color>", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f)))
                Hide();
            GUILayout.EndHorizontal();
        }

        // ---------------------------------------------------------------- left nav (mods -> section index)
        private void DrawNav(float height)
        {
            _navScroll = GUILayout.BeginScrollView(_navScroll, GUILayout.Width(NavWidth), GUILayout.Height(height));
            foreach (var mod in CfgDiscovery.ModNames)
            {
                bool selected = mod == _mod;
                if (GUILayout.Button(mod, selected ? FiresRoundedSkin.NavSel : FiresRoundedSkin.NavItem))
                {
                    _search = "";
                    GUIUtility.keyboardControl = 0;
                    _mod = mod;
                    _navSection = null;
                    _bodyScroll = Vector2.zero;
                }
                if (!selected) continue;

                var sections = CfgDiscovery.SectionsOf(mod);
                for (int i = 0; i < sections.Count; i++)
                {
                    string section = sections[i];
                    bool isCurrent = section == _navSection;
                    if (GUILayout.Button((i + 1) + ". " + CleanSection(section), isCurrent ? FiresRoundedSkin.NavSubOn : FiresRoundedSkin.NavSub))
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

            bool searching = !string.IsNullOrEmpty(_search) && _search.Length > 1;
            if (searching)
            {
                GUILayout.Label("Results for \"" + _search + "\"", FiresRoundedSkin.Title);
                string lastGroup = null;
                int shown = 0;
                foreach (var descriptor in CfgDiscovery.Descriptors)
                {
                    if (!Matches(descriptor, _search)) continue;
                    string group = descriptor.ModName + "  -  " + descriptor.SectionDisplay;
                    if (group != lastGroup)
                    {
                        lastGroup = group;
                        GUILayout.Space(3f);
                        GUILayout.Label("<b>" + group + "</b>", FiresRoundedSkin.SectionBar);
                    }
                    DrawRow(descriptor);
                    shown++;
                }
                if (shown == 0) GUILayout.Label("No matches.", FiresRoundedSkin.Label);
            }
            else if (_mod != null)
            {
                GUILayout.Label(_mod, FiresRoundedSkin.Title);
                var sections = CfgDiscovery.SectionsOf(_mod);
                for (int i = 0; i < sections.Count; i++)
                {
                    string section = sections[i];
                    string key = CollapseKey(_mod, section);
                    bool collapsed = _collapsed.Contains(key);
                    bool changed = SectionHasChanged(_mod, section);

                    GUILayout.Space(3f);
                    string barText = (collapsed ? "[+]  " : "[-]  ") + "<b>" + (i + 1) + ". " + CleanSection(section) + "</b>";
                    if (collapsed && changed) barText = "<color=#FFD980>" + barText + "</color>";
                    if (GUILayout.Button(barText, FiresRoundedSkin.SectionBar))
                    {
                        if (!_collapsed.Remove(key)) _collapsed.Add(key);
                    }
                    if (Event.current.type == EventType.Repaint)
                    {
                        var lastRect = GUILayoutUtility.GetLastRect();
                        _sectionY[key] = lastRect.y;
                        if (_pendingJump == key) { _bodyScroll.y = Mathf.Max(0f, lastRect.y - 6f); _pendingJump = null; }
                    }
                    if (collapsed) continue;

                    foreach (var descriptor in CfgDiscovery.Descriptors)
                    {
                        if (descriptor.ModName != _mod || descriptor.Section != section) continue;
                        DrawRow(descriptor);
                    }
                }
                if (sections.Count == 0) GUILayout.Label("No settings.", FiresRoundedSkin.Label);
            }

            GUILayout.EndScrollView();
        }

        private void DrawRow(CfgDescriptor descriptor)
        {
            try
            {
                GUILayout.BeginHorizontal();
                bool changed = IsChanged(descriptor);
                string name = changed ? "<b><color=#FFD980>" + descriptor.Label + "</color></b>" : descriptor.Label;
                GUILayout.Label(name, FiresRoundedSkin.Label, GUILayout.Width(NameWidth));
                DrawWidget(descriptor);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Reset", FiresRoundedSkin.ButtonSmall, GUILayout.Width(56f)))
                {
                    try { descriptor.Entry.BoxedValue = descriptor.Entry.DefaultValue; } catch { }
                    _editBuf.Remove(IdOf(descriptor));
                }
                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(descriptor.Description))
                    GUILayout.Label(descriptor.Description, FiresRoundedSkin.Desc);
            }
            catch (Exception ex)
            {
                GUILayout.EndHorizontal();
                FiresConfigUI.Log.LogWarning($"row '{descriptor.Section}/{descriptor.Key}' failed: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- typed widgets
        private void DrawWidget(CfgDescriptor descriptor)
        {
            object val = descriptor.BoxedValue;
            switch (descriptor.Kind)
            {
                case CtrlKind.Bool:
                {
                    bool isOn = val is bool flag && flag;
                    string txt = isOn ? "<b><color=#66DD66>Enabled</color></b>" : "<color=#997F55>Disabled</color>";
                    if (GUILayout.Button(txt, FiresRoundedSkin.Button, GUILayout.Width(92f))) Set(descriptor, !isOn);
                    break;
                }
                case CtrlKind.IntRange:
                case CtrlKind.FloatRange:
                {
                    float current = Convert.ToSingle(val, CultureInfo.InvariantCulture);
                    float updated = GUILayout.HorizontalSlider(current, (float)descriptor.Min, (float)descriptor.Max,
                        FiresRoundedSkin.Slider, FiresRoundedSkin.SliderThumb, GUILayout.Width(190f));
                    if (!Mathf.Approximately(updated, current))
                    {
                        float snapped = SnapStep(updated, descriptor);
                        if (descriptor.Kind == CtrlKind.IntRange) Set(descriptor, Convert.ChangeType((int)Math.Round(snapped), descriptor.Type));
                        else Set(descriptor, Convert.ChangeType(snapped, descriptor.Type));
                        current = snapped;
                    }
                    GUILayout.Label(FormatNum(descriptor, current), FiresRoundedSkin.Field, GUILayout.Width(58f));
                    break;
                }
                case CtrlKind.Enum:
                case CtrlKind.ValueList:
                {
                    var names = descriptor.Options ?? Array.Empty<string>();
                    string currentText = val?.ToString() ?? "";
                    if (GUILayout.Button(currentText, FiresRoundedSkin.Button, GUILayout.Width(170f)) && names.Length > 0)
                    {
                        int now = Math.Max(0, Array.IndexOf(names, currentText));
                        SetOption(descriptor, names[(now + 1) % names.Length]);
                    }
                    break;
                }
                case CtrlKind.IntField:
                case CtrlKind.FloatField:
                {
                    string typed = BufferedField(descriptor, val?.ToString() ?? "", 110f);
                    if (typed != null && TryParseNumber(typed, descriptor.Type, out object parsed) && !Equals(parsed, val))
                        Set(descriptor, parsed);
                    break;
                }
                case CtrlKind.String:
                {
                    string typed = BufferedField(descriptor, (string)val ?? "", 280f);
                    if (typed != null && typed != (string)val && !IsFocused(IdOf(descriptor)))
                        Set(descriptor, typed);   // commit on focus loss (CommitOnFocusChange also covers Enter-less blur)
                    break;
                }
                case CtrlKind.Color:
                {
                    var color = val is Color colorValue ? colorValue : Color.white;
                    string typed = BufferedField(descriptor, ColorToText(color), 130f);
                    if (typed != null && TryParseColor(typed, out Color typedColor) && typedColor != color)
                        Set(descriptor, typedColor);
                    var old = GUI.color;
                    GUI.color = color;
                    GUILayout.Label(GUIContent.none, FiresRoundedSkin.Swatch, GUILayout.Width(20f), GUILayout.Height(18f));
                    GUI.color = old;
                    break;
                }
                case CtrlKind.KeyBind:
                {
                    // shudnal-ConfigurationManager parity: the VALUE BOX ITSELF is the capture
                    // control. Click the box → recording starts (live chord shows in gold);
                    // click the box (or Apply) again → commit. Esc / X cancels keeping the old
                    // value; X when idle clears the bind. Keys accumulate while recording —
                    // modifiers collect, the last non-modifier is the main key — so chords like
                    // L + LeftAlt are recordable. Nothing commits until the second click.
                    bool capturingThis = _capturing && _captureTarget == descriptor.Entry;
                    string boxLabel;
                    if (capturingThis)
                    {
                        string chord = FormatChord();
                        boxLabel = chord.Length == 0
                            ? "<b><color=#FFCC66>press keys…</color></b>"
                            : "<b><color=#FFCC66>" + chord + "</color></b>";
                    }
                    else
                    {
                        string live = val?.ToString();
                        boxLabel = string.IsNullOrEmpty(live) || live == "None"
                            ? "<color=#997F55>None</color>" : live;
                    }
                    if (GUILayout.Button(boxLabel, FiresRoundedSkin.Button, GUILayout.Width(200f)))
                    {
                        if (capturingThis) CommitCapture(descriptor);
                        else BeginCapture(descriptor.Entry);
                    }
                    if (capturingThis && Event.current.type == EventType.Repaint)
                    {
                        // Remember the box + Apply + X area in SCREEN space so PollKeyCapture
                        // (which runs in Update, outside GUI) can ignore mouse presses that are
                        // really commit/cancel clicks rather than binds.
                        var lastRect = GUILayoutUtility.GetLastRect();
                        var topLeft = GUIUtility.GUIToScreenPoint(new Vector2(lastRect.x, lastRect.y));
                        _capButtonsScreenRect = new Rect(topLeft.x - 4f, topLeft.y - 4f, lastRect.width + 56f + 26f + 20f, lastRect.height + 8f);
                    }
                    var prevBg = GUI.backgroundColor;
                    if (capturingThis) GUI.backgroundColor = new Color(0.35f, 0.75f, 0.30f);
                    if (GUILayout.Button(capturingThis ? "Apply" : "Set", FiresRoundedSkin.ButtonSmall, GUILayout.Width(56f)))
                    {
                        if (capturingThis) CommitCapture(descriptor);
                        else BeginCapture(descriptor.Entry);
                    }
                    GUI.backgroundColor = prevBg;
                    if (GUILayout.Button("X", FiresRoundedSkin.ButtonSmall, GUILayout.Width(26f)))
                    {
                        if (capturingThis) { CancelCapture(); }
                        else Set(descriptor, descriptor.Type == typeof(KeyboardShortcut) ? (object)KeyboardShortcut.Empty : (object)KeyCode.None);
                    }
                    if (_capNotice != null && _capNoticeId == IdOf(descriptor))
                    {
                        if (Time.unscaledTime > _capNoticeUntil) { _capNotice = null; _capNoticeId = null; }
                        else GUILayout.Label("<color=#CC9944>" + _capNotice + "</color>", FiresRoundedSkin.Hint);
                    }
                    break;
                }
                default:
                {
                    GUILayout.Label("<color=#997F55>" + (val?.ToString() ?? "null") + "</color>", FiresRoundedSkin.Label, GUILayout.Width(220f));
                    break;
                }
            }
        }

        // A text field with a per-setting edit buffer: while focused the raw typed text survives even when it
        // doesn't parse yet ("1.", "-"); when not focused the field always shows the live value.
        private string BufferedField(CfgDescriptor descriptor, string liveText, float width)
        {
            string id = IdOf(descriptor);
            bool focused = IsFocused(id);
            string shown = focused && _editBuf.TryGetValue(id, out var buf) ? buf : liveText;
            GUI.SetNextControlName(id);
            string typed = GUILayout.TextField(shown, FiresRoundedSkin.TextInput, GUILayout.Width(width));
            if (IsFocused(id)) _editBuf[id] = typed;
            return typed;
        }

        private static bool IsFocused(string controlName) => GUI.GetNameOfFocusedControl() == controlName;

        // Commit a string field's buffer when focus leaves it (numbers/colors commit live on successful parse).
        private void CommitOnFocusChange()
        {
            string now = GUI.GetNameOfFocusedControl();
            if (now == _lastFocus) return;
            string prev = _lastFocus;
            _lastFocus = now;
            if (string.IsNullOrEmpty(prev) || !_editBuf.TryGetValue(prev, out var buf)) return;
            _editBuf.Remove(prev);
            if (!_byId.TryGetValue(prev, out var descriptor)) return;
            if (descriptor.Kind == CtrlKind.String && buf != (string)(descriptor.BoxedValue as string)) Set(descriptor, buf ?? "");
        }

        // ---------------------------------------------------------------- write-back
        private void Set(CfgDescriptor d, object value)
        {
            try { d.Entry.BoxedValue = value; }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning($"set '{d.Key}' failed: {ex.Message}"); }
        }

        private void SetOption(CfgDescriptor descriptor, string chosen)
        {
            if (chosen == null) return;
            try
            {
                if (descriptor.Type.IsEnum) { descriptor.Entry.BoxedValue = Enum.Parse(descriptor.Type, chosen); return; }
                descriptor.Entry.SetSerializedValue(chosen);
            }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning($"set option '{descriptor.Key}'='{chosen}' failed: {ex.Message}"); }
        }

        // ---------------------------------------------------------------- keybind capture
        private void BeginCapture(ConfigEntryBase entry)
        {
            _capturing = true;
            _captureTarget = entry;
            _capMain = KeyCode.None;
            _capMods.Clear();
            _capButtonsScreenRect = default;
            GUIUtility.keyboardControl = 0;
        }

        private void CancelCapture()
        {
            _capturing = false;
            _captureTarget = null;
            _capMain = KeyCode.None;
            _capMods.Clear();
        }

        // Record-only poll: keys accumulate here; CommitCapture (the Apply click) does the write.
        private void PollKeyCapture()
        {
            if (_captureTarget == null) { _capturing = false; return; }
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { CancelCapture(); return; }

            foreach (KeyCode keyCode in Enum.GetValues(typeof(KeyCode)))
            {
                if (!UnityEngine.Input.GetKeyDown(keyCode)) continue;

                // A mouse press over the Apply/X buttons is a CLICK, not a bind — recording it would
                // overwrite the chord on the way to committing it. (GUI screen space: y grows down.)
                if (keyCode >= KeyCode.Mouse0 && keyCode <= KeyCode.Mouse6)
                {
                    var mouseGui = new Vector2(UnityEngine.Input.mousePosition.x,
                        Screen.height - UnityEngine.Input.mousePosition.y);
                    if (_capButtonsScreenRect.Contains(mouseGui)) continue;
                }

                if (Array.IndexOf(s_modifiers, keyCode) >= 0)
                {
                    if (!_capMods.Contains(keyCode)) _capMods.Add(keyCode);
                }
                else
                {
                    _capMain = keyCode;   // last non-modifier wins; keep recording until Apply
                }
            }
        }

        private void CommitCapture(CfgDescriptor descriptor)
        {
            bool wantsShortcut = _captureTarget != null && _captureTarget.SettingType == typeof(KeyboardShortcut);
            KeyCode main = _capMain;
            var mods = new List<KeyCode>(_capMods);

            // Nothing recorded → behave like cancel.
            if (main == KeyCode.None && mods.Count == 0) { CancelCapture(); return; }

            try
            {
                if (wantsShortcut)
                {
                    // A lone modifier is a legal shortcut main key (e.g. bind to LeftAlt itself).
                    if (main == KeyCode.None)
                    {
                        main = mods[0];
                        mods.RemoveAt(0);
                    }
                    _captureTarget.BoxedValue = new KeyboardShortcut(main, mods.ToArray());
                }
                else
                {
                    // Plain KeyCode entries hold ONE key. A lone modifier is valid (NES Select =
                    // LeftShift); a chord stores its main key and says so instead of silently
                    // dropping the modifiers.
                    if (main == KeyCode.None) main = mods[0];
                    else if (mods.Count > 0) Notice(descriptor, $"single-key setting — stored {main}, modifiers ignored");
                    _captureTarget.BoxedValue = main;
                }
            }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning("keybind capture failed: " + ex.Message); }

            CancelCapture();
        }

        private void Notice(CfgDescriptor descriptor, string text)
        {
            _capNotice = text;
            _capNoticeId = IdOf(descriptor);
            _capNoticeUntil = Time.unscaledTime + 5f;
        }

        private string FormatChord()
        {
            var parts = new List<string>();
            foreach (var modifier in _capMods) parts.Add(modifier.ToString());
            if (_capMain != KeyCode.None) parts.Add(_capMain.ToString());
            return string.Join(" + ", parts);
        }

        // ---------------------------------------------------------------- resize grip
        private void HandleResize()
        {
            var grip = new Rect(_rect.width - 22f, _rect.height - 22f, 20f, 20f);
            GUI.Label(grip, "//", FiresRoundedSkin.Hint);
            var currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown && grip.Contains(currentEvent.mousePosition)) { _resizing = true; currentEvent.Use(); }
            else if (_resizing && currentEvent.type == EventType.MouseDrag)
            {
                _size.x = Mathf.Clamp(_size.x + currentEvent.delta.x, MinSize.x, Screen.width - 20f);
                _size.y = Mathf.Clamp(_size.y + currentEvent.delta.y, MinSize.y, Screen.height - 20f);
                currentEvent.Use();
            }
            else if (_resizing && currentEvent.type == EventType.MouseUp) { _resizing = false; currentEvent.Use(); }
        }

        // ---------------------------------------------------------------- helpers
        private static string CollapseKey(string mod, string section) => (mod ?? "") + "|" + (section ?? "");

        private bool SectionHasChanged(string mod, string section)
        {
            foreach (var descriptor in CfgDiscovery.Descriptors)
                if (descriptor.ModName == mod && descriptor.Section == section && IsChanged(descriptor)) return true;
            return false;
        }

        private static bool IsChanged(CfgDescriptor descriptor)
        {
            try
            {
                object current = descriptor.BoxedValue, def = descriptor.Entry.DefaultValue;
                if (current == null) return def != null;
                return !current.Equals(def);
            }
            catch { return false; }
        }

        private static bool Matches(CfgDescriptor descriptor, string query)
        {
            query = query.ToLowerInvariant();
            return descriptor.Key.ToLowerInvariant().Contains(query) || descriptor.Section.ToLowerInvariant().Contains(query)
                || descriptor.ModName.ToLowerInvariant().Contains(query)
                || (!string.IsNullOrEmpty(descriptor.Description) && descriptor.Description.ToLowerInvariant().Contains(query));
        }

        private static float SnapStep(float raw, CfgDescriptor descriptor)
        {
            if (descriptor.Step <= 0.0) return raw;
            double steps = Math.Round((raw - descriptor.Min) / descriptor.Step);
            return (float)Math.Min(descriptor.Max, Math.Max(descriptor.Min, descriptor.Min + steps * descriptor.Step));
        }

        private static string FormatNum(CfgDescriptor descriptor, float number)
            => descriptor.Kind == CtrlKind.IntRange ? ((int)Math.Round(number)).ToString() : number.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool TryParseNumber(string text, Type targetType, out object value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (targetType == typeof(float) || targetType == typeof(double))
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsedDouble)
                    && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedDouble)) return false;
                value = Convert.ChangeType(parsedDouble, targetType); return true;
            }
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out long parsedLong)
                && !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedLong)) return false;
            try { value = Convert.ChangeType(parsedLong, targetType); return true; } catch { return false; }
        }

        private static string ColorToText(Color color) => $"{color.r:0.##},{color.g:0.##},{color.b:0.##},{color.a:0.##}";

        private static bool TryParseColor(string text, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(',');
            if (parts.Length < 3) return false;
            float r, g, b, a = 1f;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return false;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g)) return false;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return false;
            if (parts.Length >= 4) float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out a);
            color = new Color(r, g, b, a); return true;
        }

        private static string CleanSection(string section)
        {
            if (string.IsNullOrEmpty(section)) return section;
            int dash = section.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash <= 5)
            {
                bool digit = false, ok = true;
                foreach (char ch in section.Substring(0, dash)) { if (char.IsDigit(ch)) digit = true; else if (!char.IsLetter(ch)) { ok = false; break; } }
                if (digit && ok) return section.Substring(dash + 3);
            }
            return section;
        }
    }
}
