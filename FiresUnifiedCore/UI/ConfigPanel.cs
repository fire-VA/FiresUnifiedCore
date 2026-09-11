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
            foreach (var d in CfgDiscovery.Descriptors) _byId[IdOf(d)] = d;
        }

        private static string IdOf(CfgDescriptor d) => d.ModGuid + "|" + d.Section + "|" + d.Key;

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

            float bodyH = _rect.height - 92f;
            GUILayout.BeginHorizontal();
            DrawNav(bodyH);
            GUILayout.Space(6f);
            DrawBody(bodyH);
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
                    bool on = section == _navSection;
                    if (GUILayout.Button((i + 1) + ". " + CleanSection(section), on ? FiresRoundedSkin.NavSubOn : FiresRoundedSkin.NavSub))
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
                foreach (var d in CfgDiscovery.Descriptors)
                {
                    if (!Matches(d, _search)) continue;
                    string group = d.ModName + "  -  " + d.SectionDisplay;
                    if (group != lastGroup)
                    {
                        lastGroup = group;
                        GUILayout.Space(3f);
                        GUILayout.Label("<b>" + group + "</b>", FiresRoundedSkin.SectionBar);
                    }
                    DrawRow(d);
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
                        var r = GUILayoutUtility.GetLastRect();
                        _sectionY[key] = r.y;
                        if (_pendingJump == key) { _bodyScroll.y = Mathf.Max(0f, r.y - 6f); _pendingJump = null; }
                    }
                    if (collapsed) continue;

                    foreach (var d in CfgDiscovery.Descriptors)
                    {
                        if (d.ModName != _mod || d.Section != section) continue;
                        DrawRow(d);
                    }
                }
                if (sections.Count == 0) GUILayout.Label("No settings.", FiresRoundedSkin.Label);
            }

            GUILayout.EndScrollView();
        }

        private void DrawRow(CfgDescriptor d)
        {
            try
            {
                GUILayout.BeginHorizontal();
                bool changed = IsChanged(d);
                string name = changed ? "<b><color=#FFD980>" + d.Label + "</color></b>" : d.Label;
                GUILayout.Label(name, FiresRoundedSkin.Label, GUILayout.Width(NameWidth));
                DrawWidget(d);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Reset", FiresRoundedSkin.ButtonSmall, GUILayout.Width(56f)))
                {
                    try { d.Entry.BoxedValue = d.Entry.DefaultValue; } catch { }
                    _editBuf.Remove(IdOf(d));
                }
                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(d.Description))
                    GUILayout.Label(d.Description, FiresRoundedSkin.Desc);
            }
            catch (Exception ex)
            {
                GUILayout.EndHorizontal();
                FiresConfigUI.Log.LogWarning($"row '{d.Section}/{d.Key}' failed: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- typed widgets
        private void DrawWidget(CfgDescriptor d)
        {
            object val = d.BoxedValue;
            switch (d.Kind)
            {
                case CtrlKind.Bool:
                {
                    bool b = val is bool bv && bv;
                    string txt = b ? "<b><color=#66DD66>Enabled</color></b>" : "<color=#997F55>Disabled</color>";
                    if (GUILayout.Button(txt, FiresRoundedSkin.Button, GUILayout.Width(92f))) Set(d, !b);
                    break;
                }
                case CtrlKind.IntRange:
                case CtrlKind.FloatRange:
                {
                    float v = Convert.ToSingle(val, CultureInfo.InvariantCulture);
                    float nv = GUILayout.HorizontalSlider(v, (float)d.Min, (float)d.Max,
                        FiresRoundedSkin.Slider, FiresRoundedSkin.SliderThumb, GUILayout.Width(190f));
                    if (!Mathf.Approximately(nv, v))
                    {
                        float snapped = SnapStep(nv, d);
                        if (d.Kind == CtrlKind.IntRange) Set(d, Convert.ChangeType((int)Math.Round(snapped), d.Type));
                        else Set(d, Convert.ChangeType(snapped, d.Type));
                        v = snapped;
                    }
                    GUILayout.Label(FormatNum(d, v), FiresRoundedSkin.Field, GUILayout.Width(58f));
                    break;
                }
                case CtrlKind.Enum:
                case CtrlKind.ValueList:
                {
                    var names = d.Options ?? Array.Empty<string>();
                    string cur = val?.ToString() ?? "";
                    if (GUILayout.Button(cur, FiresRoundedSkin.Button, GUILayout.Width(170f)) && names.Length > 0)
                    {
                        int now = Math.Max(0, Array.IndexOf(names, cur));
                        SetOption(d, names[(now + 1) % names.Length]);
                    }
                    break;
                }
                case CtrlKind.IntField:
                case CtrlKind.FloatField:
                {
                    string typed = BufferedField(d, val?.ToString() ?? "", 110f);
                    if (typed != null && TryParseNumber(typed, d.Type, out object parsed) && !Equals(parsed, val))
                        Set(d, parsed);
                    break;
                }
                case CtrlKind.String:
                {
                    string typed = BufferedField(d, (string)val ?? "", 280f);
                    if (typed != null && typed != (string)val && !IsFocused(IdOf(d)))
                        Set(d, typed);   // commit on focus loss (CommitOnFocusChange also covers Enter-less blur)
                    break;
                }
                case CtrlKind.Color:
                {
                    var c = val is Color cv ? cv : Color.white;
                    string typed = BufferedField(d, ColorToText(c), 130f);
                    if (typed != null && TryParseColor(typed, out Color nc) && nc != c)
                        Set(d, nc);
                    var old = GUI.color;
                    GUI.color = c;
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
                    bool capturingThis = _capturing && _captureTarget == d.Entry;
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
                        if (capturingThis) CommitCapture(d);
                        else BeginCapture(d.Entry);
                    }
                    if (capturingThis && Event.current.type == EventType.Repaint)
                    {
                        // Remember the box + Apply + X area in SCREEN space so PollKeyCapture
                        // (which runs in Update, outside GUI) can ignore mouse presses that are
                        // really commit/cancel clicks rather than binds.
                        var r = GUILayoutUtility.GetLastRect();
                        var tl = GUIUtility.GUIToScreenPoint(new Vector2(r.x, r.y));
                        _capButtonsScreenRect = new Rect(tl.x - 4f, tl.y - 4f, r.width + 56f + 26f + 20f, r.height + 8f);
                    }
                    var prevBg = GUI.backgroundColor;
                    if (capturingThis) GUI.backgroundColor = new Color(0.35f, 0.75f, 0.30f);
                    if (GUILayout.Button(capturingThis ? "Apply" : "Set", FiresRoundedSkin.ButtonSmall, GUILayout.Width(56f)))
                    {
                        if (capturingThis) CommitCapture(d);
                        else BeginCapture(d.Entry);
                    }
                    GUI.backgroundColor = prevBg;
                    if (GUILayout.Button("X", FiresRoundedSkin.ButtonSmall, GUILayout.Width(26f)))
                    {
                        if (capturingThis) { CancelCapture(); }
                        else Set(d, d.Type == typeof(KeyboardShortcut) ? (object)KeyboardShortcut.Empty : (object)KeyCode.None);
                    }
                    if (_capNotice != null && _capNoticeId == IdOf(d))
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
        private string BufferedField(CfgDescriptor d, string liveText, float width)
        {
            string id = IdOf(d);
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
            if (!_byId.TryGetValue(prev, out var d)) return;
            if (d.Kind == CtrlKind.String && buf != (string)(d.BoxedValue as string)) Set(d, buf ?? "");
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

            foreach (KeyCode kc in Enum.GetValues(typeof(KeyCode)))
            {
                if (!UnityEngine.Input.GetKeyDown(kc)) continue;

                // A mouse press over the Apply/X buttons is a CLICK, not a bind — recording it would
                // overwrite the chord on the way to committing it. (GUI screen space: y grows down.)
                if (kc >= KeyCode.Mouse0 && kc <= KeyCode.Mouse6)
                {
                    var mouseGui = new Vector2(UnityEngine.Input.mousePosition.x,
                        Screen.height - UnityEngine.Input.mousePosition.y);
                    if (_capButtonsScreenRect.Contains(mouseGui)) continue;
                }

                if (Array.IndexOf(s_modifiers, kc) >= 0)
                {
                    if (!_capMods.Contains(kc)) _capMods.Add(kc);
                }
                else
                {
                    _capMain = kc;   // last non-modifier wins; keep recording until Apply
                }
            }
        }

        private void CommitCapture(CfgDescriptor d)
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
                    else if (mods.Count > 0) Notice(d, $"single-key setting — stored {main}, modifiers ignored");
                    _captureTarget.BoxedValue = main;
                }
            }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning("keybind capture failed: " + ex.Message); }

            CancelCapture();
        }

        private void Notice(CfgDescriptor d, string text)
        {
            _capNotice = text;
            _capNoticeId = IdOf(d);
            _capNoticeUntil = Time.unscaledTime + 5f;
        }

        private string FormatChord()
        {
            var parts = new List<string>();
            foreach (var m in _capMods) parts.Add(m.ToString());
            if (_capMain != KeyCode.None) parts.Add(_capMain.ToString());
            return string.Join(" + ", parts);
        }

        // ---------------------------------------------------------------- resize grip
        private void HandleResize()
        {
            var grip = new Rect(_rect.width - 22f, _rect.height - 22f, 20f, 20f);
            GUI.Label(grip, "//", FiresRoundedSkin.Hint);
            var e = Event.current;
            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition)) { _resizing = true; e.Use(); }
            else if (_resizing && e.type == EventType.MouseDrag)
            {
                _size.x = Mathf.Clamp(_size.x + e.delta.x, MinSize.x, Screen.width - 20f);
                _size.y = Mathf.Clamp(_size.y + e.delta.y, MinSize.y, Screen.height - 20f);
                e.Use();
            }
            else if (_resizing && e.type == EventType.MouseUp) { _resizing = false; e.Use(); }
        }

        // ---------------------------------------------------------------- helpers
        private static string CollapseKey(string mod, string section) => (mod ?? "") + "|" + (section ?? "");

        private bool SectionHasChanged(string mod, string section)
        {
            foreach (var d in CfgDiscovery.Descriptors)
                if (d.ModName == mod && d.Section == section && IsChanged(d)) return true;
            return false;
        }

        private static bool IsChanged(CfgDescriptor d)
        {
            try
            {
                object cur = d.BoxedValue, def = d.Entry.DefaultValue;
                if (cur == null) return def != null;
                return !cur.Equals(def);
            }
            catch { return false; }
        }

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
            float r, g, b, a = 1f;
            if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return false;
            if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g)) return false;
            if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return false;
            if (p.Length >= 4) float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out a);
            c = new Color(r, g, b, a); return true;
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
