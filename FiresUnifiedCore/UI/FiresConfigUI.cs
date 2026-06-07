using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// A clean, Fires-only configuration window that lives in Core so every Fires mod can opt in with a
    /// single <see cref="Register"/> call. It is an ADDITIONAL UI - it does not replace shudnal's
    /// ConfigurationManager, it just owns the config hotkey (by setting shudnal's runtime-only
    /// <c>OverrideHotkey</c> flag, so nothing is persisted) and shows OUR layout first. A "Default UI"
    /// button hands off to shudnal's window.
    ///
    /// Layout: a LEFT nav panel groups settings by mod -> section so the categories are easy to scan;
    /// the RIGHT panel edits only the selected section with typed controls (toggle / slider / enum /
    /// color / text / keybind), each with its description and a reset-to-default button. A search box
    /// filters across everything. Pure IMGUI - no asset bundle or layout needed.
    /// </summary>
    public static class FiresConfigUI
    {
        internal class ModEntry
        {
            public string Name;
            public ConfigFile Config;
        }

        private static readonly List<ModEntry> s_mods = new List<ModEntry>();
        internal static IReadOnlyList<ModEntry> Mods => s_mods;

        private static FiresConfigUIHost s_host;

        // Direct BepInEx log source - bypasses FUC's RateLimitedLogHandler so draw errors aren't swallowed.
        internal static readonly BepInEx.Logging.ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("FiresConfigUI");

        /// <summary>Fallback hotkey that opens the Fires window (the bindable CfgHotkey overrides it).
        /// Default F8 so we don't collide with shudnal's F1 - use the Appearance section to rebind.</summary>
        public static KeyCode ToggleKey = KeyCode.F8;

        /// <summary>Title shown at the top of the window.</summary>
        public static string Title = "Fires Configuration";

        /// <summary>Register a mod's config so it appears in the Fires window. Idempotent (re-registering
        /// the same ConfigFile just refreshes its display name).</summary>
        public static void Register(string modName, ConfigFile config)
        {
            if (config == null) return;
            foreach (var m in s_mods)
                if (ReferenceEquals(m.Config, config)) { m.Name = Clean(modName); return; }
            s_mods.Add(new ModEntry { Name = Clean(modName), Config = config });
            s_mods.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            EnsureHost();
            Debug.Log($"[FiresConfigUI] registered '{Clean(modName)}' ({config.Keys.Count} entries, {s_mods.Count} mod(s)); host={(s_host != null)}.");
        }

        private static string Clean(string n) => string.IsNullOrEmpty(n) ? "Mod" : n;

        /// <summary>Create the always-on UI host (safe to call repeatedly).</summary>
        public static void EnsureHost()
        {
            if (s_host != null) return;
            var go = new GameObject("FiresConfigUIHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            s_host = go.AddComponent<FiresConfigUIHost>();
            Debug.Log("[FiresConfigUI] host GameObject created.");
        }

        /// <summary>Open/close the window from code (used by the va_config console command).</summary>
        public static void Toggle()
        {
            EnsureHost();
            if (s_host != null) s_host.ExternalToggle();
        }

        // ----- Appearance: the config window styles ITSELF, bound by Core into its own config so an
        // "00 - Config UI" section shows up right in the window and restyles live. -----
        public enum UiFont { Default, Arial, Consolas, Serif }

        internal static ConfigEntry<KeyboardShortcut> CfgHotkey;
        internal static ConfigEntry<UiFont> CfgFont;
        internal static ConfigEntry<int> CfgFontSize;
        internal static ConfigEntry<float> CfgOpacity;
        internal static ConfigEntry<Color> CfgAccent;

        /// <summary>Bind the window's own appearance settings into <paramref name="cfg"/> and register that
        /// config so the Appearance section appears in the window. Call once (Core does this in Setup).</summary>
        public static void BindAppearance(ConfigFile cfg)
        {
            if (cfg == null || CfgFont != null) return;
            CfgHotkey = cfg.Bind("00 - Config UI", "00 Hotkey", new KeyboardShortcut(KeyCode.F8),
                "Key that opens the Fires config window. (Console command 'va_config' also toggles it.)");
            CfgFont = cfg.Bind("00 - Config UI", "01 Font", UiFont.Default,
                "Font for the Fires config window.");
            CfgFontSize = cfg.Bind("00 - Config UI", "02 FontSize", 13,
                new ConfigDescription("Text size in the Fires config window.", new AcceptableValueRange<int>(9, 24)));
            CfgOpacity = cfg.Bind("00 - Config UI", "03 BackgroundOpacity", 0.97f,
                new ConfigDescription("Panel opacity of the Fires config window.", new AcceptableValueRange<float>(0.25f, 1f)));
            CfgAccent = cfg.Bind("00 - Config UI", "04 AccentColor", new Color(1f, 0.84f, 0.5f),
                "Header / title text color in the Fires config window.");

            EventHandler restyle = (s, e) => InvalidateStyles();
            CfgFont.SettingChanged += restyle;
            CfgFontSize.SettingChanged += restyle;
            CfgOpacity.SettingChanged += restyle;
            CfgAccent.SettingChanged += restyle;

            Register("FiresUnifiedCore", cfg);
        }

        internal static void InvalidateStyles() { if (s_host != null) s_host.MarkStylesDirty(); }
    }

    /// <summary>The MonoBehaviour that draws the window, owns the hotkey, and coexists with shudnal's CM.</summary>
    internal class FiresConfigUIHost : MonoBehaviour
    {
        private const int WinId = 0x1F12E5;

        private bool _open;
        private Rect _win = new Rect(120, 70, 920, 640);
        private Vector2 _navScroll, _bodyScroll;
        private int _modIndex;
        private string _section;
        private string _search = "";

        private bool _capturing;
        private ConfigEntryBase _captureTarget;

        private CursorLockMode _prevLock;
        private bool _prevVisible;

        // shudnal ConfigurationManager (resolved reflectively; no hard dependency).
        private object _shudnal;
        private PropertyInfo _shudnalDisplaying;
        private bool _shudnalResolved;
        private float _nextShudnalScan;

        private bool _cmd;

        private void Awake()
        {
            Debug.Log($"[FiresConfigUI] host Awake - listening for {FiresConfigUI.ToggleKey} (or console: va_config).");
        }

        private void Update()
        {
            if (!_cmd) { try { RegisterCommand(); _cmd = true; } catch { _cmd = false; } }

            ResolveShudnal();

            bool toggle = FiresConfigUI.CfgHotkey != null
                ? FiresConfigUI.CfgHotkey.Value.IsDown()
                : UnityEngine.Input.GetKeyDown(FiresConfigUI.ToggleKey);
            if (!_capturing && toggle)
            {
                Debug.Log($"[FiresConfigUI] hotkey -> {(!_open ? "open" : "close")}.");
                SetOpen(!_open);
            }

            if (_open)
            {
                Cursor.lockState = CursorLockMode.None;   // keep the cursor usable while tuning
                Cursor.visible = true;
            }
        }

        public void ExternalToggle()
        {
            Debug.Log($"[FiresConfigUI] va_config -> {(!_open ? "open" : "close")} (mods registered: {FiresConfigUI.Mods.Count}).");
            SetOpen(!_open);
        }

        private static bool s_cmdRegistered;
        private void RegisterCommand()
        {
            if (s_cmdRegistered) return;
            new Terminal.ConsoleCommand("va_config", "Toggle the Fires configuration window.",
                args => FiresConfigUI.Toggle(), isCheat: false);
            s_cmdRegistered = true;
        }

        private void SetOpen(bool open)
        {
            if (open == _open) return;
            _open = open;
            if (open)
            {
                _prevLock = Cursor.lockState;
                _prevVisible = Cursor.visible;
                SetShudnalOpen(false);   // ours takes over; close shudnal's if it was up
                InitSelection();         // pick a default section NOW (never mutate selection during the draw)
            }
            else
            {
                _capturing = false; _captureTarget = null;
                Cursor.lockState = _prevLock;
                Cursor.visible = _prevVisible;
            }
        }

        // Choose a valid mod + first section so the body has something to draw without mutating state mid-draw.
        private void InitSelection()
        {
            var mods = FiresConfigUI.Mods;
            if (mods.Count == 0) { _section = null; return; }
            _modIndex = Mathf.Clamp(_modIndex, 0, mods.Count - 1);
            if (string.IsNullOrEmpty(_section)) _section = SectionsOf(mods[_modIndex]).FirstOrDefault();
        }

        // ---------- shudnal coexistence ----------
        private bool ShudnalReady => _shudnal != null && _shudnalDisplaying != null;

        private void ResolveShudnal()
        {
            if (_shudnalResolved) return;
            if (Time.unscaledTime < _nextShudnalScan) return;
            _nextShudnalScan = Time.unscaledTime + 3f;
            try
            {
                // shudnal's CM type is "_shudnal.ConfigurationManager" (older forks: "ConfigurationManager.ConfigurationManager").
                Type t = null;
                foreach (var name in new[] { "_shudnal.ConfigurationManager", "ConfigurationManager.ConfigurationManager" })
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try { t = asm.GetType(name); } catch { t = null; }
                        if (t != null) break;
                    }
                    if (t != null) break;
                }
                if (t == null) return;
                var inst = UnityEngine.Object.FindObjectOfType(t);
                if (inst == null) return;
                _shudnal = inst;
                _shudnalDisplaying = t.GetProperty("DisplayingWindow", BindingFlags.Public | BindingFlags.Instance);
                _shudnalResolved = true;
                // NOTE: we deliberately do NOT set shudnal's OverrideHotkey - leaving its F1 intact so it
                // stays usable while our own window is on a separate key. The "Default config UI" button
                // still opens it via DisplayingWindow.
                Debug.Log($"[FiresConfigUI] found shudnal CM ({t.FullName}); its hotkey left intact.");
            }
            catch { /* shudnal not present - our window just runs standalone */ }
        }

        private void SetShudnalOpen(bool open)
        {
            if (!ShudnalReady) return;
            try { _shudnalDisplaying.SetValue(_shudnal, open); } catch { }
        }

        // ---------- drawing ----------
        private void OnGUI()
        {
            if (!_open) return;
            // OnGUI runs after every Update/LateUpdate, so re-assert the cursor here too - otherwise
            // GameCamera's per-frame re-lock can win the race and the window becomes unclickable.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            EnsureStyles();

            // Valheim's own OnGUI leaves GUI.matrix scaled for its resolution; if we inherit it our
            // window renders off-screen / wrong size (it draws but you see nothing). Force identity for
            // our draw and restore afterwards so we don't disturb other IMGUI.
            var prevMatrix = GUI.matrix;
            var prevColor = GUI.color;
            GUI.matrix = Matrix4x4.identity;
            GUI.color = Color.white;

            if (!_loggedDraw) { _loggedDraw = true; FiresConfigUI.Log.LogInfo($"drawing. screen={Screen.width}x{Screen.height} win={_win}"); }

            _win = GUILayout.Window(WinId, _win, DrawWindow, FiresConfigUI.Title, _window);
            GUI.color = prevColor;
            GUI.matrix = prevMatrix;
        }
        private bool _loggedDraw;

        private void DrawWindow(int id)
        {
            if (_winBg != null) GUI.DrawTexture(new Rect(0f, 0f, _win.width, _win.height), _winBg);  // guaranteed opaque panel
            GUI.contentColor = Color.white;   // make default-styled text (labels, buttons) readable on the dark panel

            // EVERY Begin* is paired with its End* via try/finally. If any inner draw throws, the groups
            // still close, so the GUILayout cache can never be left unbalanced (which is what produced the
            // perpetual "control 0 in a group with only 0 controls" spam). Selection state is NOT mutated
            // here (it's set on open / nav-click) so the Layout and Repaint passes stay identical.
            GUILayout.BeginHorizontal();
            try
            {
                GUILayout.Label("Search", _label, GUILayout.Width(54));
                _search = GUILayout.TextField(_search ?? "", _label, GUILayout.Width(240));
                if (GUILayout.Button("clear", GUILayout.Width(54))) _search = "";
                GUILayout.FlexibleSpace();
                if (ShudnalReady && GUILayout.Button("Default config UI", GUILayout.Width(160))) { SetOpen(false); SetShudnalOpen(true); }
                if (GUILayout.Button("Close", GUILayout.Width(70))) SetOpen(false);
            }
            finally { GUILayout.EndHorizontal(); }
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            try
            {
                GUILayout.BeginVertical(GUILayout.Width(250));
                try
                {
                    _navScroll = GUILayout.BeginScrollView(_navScroll, GUILayout.ExpandHeight(true));
                    try { SafeSection(DrawNav); } finally { GUILayout.EndScrollView(); }
                }
                finally { GUILayout.EndVertical(); }

                GUILayout.BeginVertical();
                try
                {
                    _bodyScroll = GUILayout.BeginScrollView(_bodyScroll, GUILayout.ExpandHeight(true));
                    try { SafeSection(DrawBody); } finally { GUILayout.EndScrollView(); }
                }
                finally { GUILayout.EndVertical(); }
            }
            finally { GUILayout.EndHorizontal(); }

            GUILayout.Label("Drag the title to move / double-click it to maximize / drag the corner to resize.", _dim);

            HandleWindowChrome();
            GUI.DragWindow(new Rect(0, 0, 100000, 22));   // title bar (must be LAST so the chrome handlers see clicks first)
            if (!_loggedComplete) { _loggedComplete = true; FiresConfigUI.Log.LogInfo($"content drawn: {FiresConfigUI.Mods.Count} mod(s), section='{_section}'."); }
        }
        private bool _loggedComplete;

        // Run a section drawer; a thrown exception is logged once but the surrounding groups still close.
        private static bool _loggedErr;
        private void SafeSection(System.Action draw)
        {
            try { draw(); }
            catch (Exception ex)
            {
                GUILayout.Label("(section error - see log)", _dim);
                if (!_loggedErr) { _loggedErr = true; FiresConfigUI.Log.LogError("section draw failed: " + ex); }
            }
        }

        private const float MinW = 520f, MinH = 360f;
        private bool _resizing;
        private float _lastTitleClick = -1f;
        private Rect _restoreRect;
        private bool _maximized;

        // Bottom-right drag-to-resize grip + double-click-the-title to maximize/restore (min-size clamped).
        private void HandleWindowChrome()
        {
            var e = Event.current;

            if (e.type == EventType.MouseDown && e.button == 0 && e.mousePosition.y <= 26f && e.mousePosition.x < _win.width - 24f)
            {
                if (Time.unscaledTime - _lastTitleClick < 0.35f) ToggleMaximize();
                _lastTitleClick = Time.unscaledTime;
            }

            var grip = new Rect(_win.width - 22f, _win.height - 22f, 20f, 20f);
            GUI.DrawTexture(grip, _rowBg);
            GUI.Label(new Rect(grip.x + 3f, grip.y - 2f, 18f, 18f), "//", _dim);
            if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition)) { _resizing = true; e.Use(); }
            if (_resizing)
            {
                if (e.type == EventType.MouseDrag)
                {
                    _win.width = Mathf.Max(MinW, _win.width + e.delta.x);
                    _win.height = Mathf.Max(MinH, _win.height + e.delta.y);
                    _maximized = false;
                    e.Use();
                }
                else if (e.type == EventType.MouseUp) { _resizing = false; e.Use(); }
            }
        }

        private void ToggleMaximize()
        {
            if (_maximized) { _win = _restoreRect; _maximized = false; }
            else
            {
                _restoreRect = _win;
                float w = Mathf.Min(Screen.width - 40f, 1500f);
                float h = Mathf.Min(Screen.height - 40f, 950f);
                _win = new Rect(20f, 20f, w, h);
                _maximized = true;
            }
        }

        private void DrawNav()
        {
            var mods = FiresConfigUI.Mods;
            if (mods.Count == 0) { GUILayout.Label("No Fires mods registered yet."); return; }

            for (int mi = 0; mi < mods.Count; mi++)
            {
                var mod = mods[mi];
                GUILayout.Label(mod.Name, _navHeader);
                foreach (var section in SectionsOf(mod))
                {
                    bool sel = mi == _modIndex && section == _section && string.IsNullOrEmpty(_search);
                    var prevBg = GUI.backgroundColor;
                    if (sel) GUI.backgroundColor = new Color(0.30f, 0.52f, 0.85f);
                    if (GUILayout.Button("   " + CleanSection(section), _navItem))
                    {
                        _modIndex = mi; _section = section; _search = ""; _bodyScroll = Vector2.zero;
                    }
                    GUI.backgroundColor = prevBg;
                }
                GUILayout.Space(8);
            }
        }

        private void DrawBody()
        {
            var mods = FiresConfigUI.Mods;
            if (mods.Count == 0) { GUILayout.Label("Register a Fires mod's config to see it here.", _label); return; }

            if (!string.IsNullOrEmpty(_search)) { DrawSearchResults(); return; }

            int mi = Mathf.Clamp(_modIndex, 0, mods.Count - 1);   // read-only - never mutate during draw
            var mod = mods[mi];
            string section = _section;
            if (string.IsNullOrEmpty(section)) { GUILayout.Label("Pick a section on the left.", _label); return; }

            GUILayout.Label(mod.Name + "    " + CleanSection(section), _bodyTitle);
            GUILayout.Space(6);

            foreach (var def in OrderedKeys(mod.Config))
            {
                if (def.Section != section) continue;
                DrawSetting(mod.Config[def], def);
            }
        }

        private void DrawSearchResults()
        {
            string q = _search.ToLowerInvariant();
            GUILayout.Label("Results for \"" + _search + "\"", _bodyTitle);
            GUILayout.Space(6);
            int shown = 0;
            foreach (var mod in FiresConfigUI.Mods)
            {
                string lastSection = null;
                foreach (var def in OrderedKeys(mod.Config))
                {
                    var entry = mod.Config[def];
                    if (!Matches(def, entry, q)) continue;
                    if (def.Section != lastSection)
                    {
                        lastSection = def.Section;
                        GUILayout.Label(mod.Name + "  /  " + CleanSection(def.Section), _navHeader);
                    }
                    DrawSetting(entry, def);
                    shown++;
                }
            }
            if (shown == 0) GUILayout.Label("No matches.", _dim);
        }

        private static bool Matches(ConfigDefinition def, ConfigEntryBase entry, string qLower)
        {
            if (def.Key.ToLowerInvariant().Contains(qLower)) return true;
            if (def.Section.ToLowerInvariant().Contains(qLower)) return true;
            var d = entry.Description?.Description;
            return !string.IsNullOrEmpty(d) && d.ToLowerInvariant().Contains(qLower);
        }

        // ---------- one setting ----------
        private static bool _loggedSettingErr;

        private void DrawSetting(ConfigEntryBase entry, ConfigDefinition def)
        {
            // try/finally guarantees the End* run even if an editor throws, so one bad setting can't
            // unbalance the GUILayout groups (which is what spams the per-frame "group" exception).
            GUILayout.BeginVertical(_settingBox);
            try
            {
                GUILayout.BeginHorizontal();
                try
                {
                    GUILayout.Label(CleanKey(def.Key), _keyLabel, GUILayout.Width(210));
                    DrawEditor(entry);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("reset", GUILayout.Width(56)) && entry.DefaultValue != null)
                        entry.BoxedValue = entry.DefaultValue;
                }
                finally { GUILayout.EndHorizontal(); }

                var desc = entry.Description?.Description;
                if (!string.IsNullOrEmpty(desc)) GUILayout.Label(desc, _dim);
            }
            catch (Exception ex)
            {
                GUILayout.Label("(could not draw '" + def.Key + "')", _dim);
                if (!_loggedSettingErr)
                {
                    _loggedSettingErr = true;
                    FiresConfigUI.Log.LogWarning($"setting '{def.Section}/{def.Key}' ({entry.SettingType}) failed: {ex}");
                }
            }
            finally { GUILayout.EndVertical(); }
            GUILayout.Space(6);
        }

        private void DrawEditor(ConfigEntryBase entry)
        {
            Type t = entry.SettingType;
            object val = entry.BoxedValue;

            if (t == typeof(bool))
            {
                bool b = (bool)val;
                bool nb = GUILayout.Toggle(b, b ? " enabled" : " disabled", GUILayout.Width(110));
                if (nb != b) entry.BoxedValue = nb;
            }
            else if (t == typeof(int) || t == typeof(float))
            {
                DrawNumber(entry, t, val);
            }
            else if (t.IsEnum)
            {
                var names = Enum.GetNames(t);
                int cur = Array.IndexOf(names, val.ToString());
                int nv = GUILayout.SelectionGrid(cur < 0 ? 0 : cur, names, Math.Min(names.Length, 4), GUILayout.Width(320));
                if (nv != cur && nv >= 0) entry.BoxedValue = Enum.Parse(t, names[nv]);
            }
            else if (t == typeof(Color))
            {
                DrawColor(entry, (Color)val);
            }
            else if (t == typeof(KeyboardShortcut))
            {
                DrawKeybind(entry, (KeyboardShortcut)val);
            }
            else if (t == typeof(string))
            {
                string s = (string)val ?? "";
                string ns = GUILayout.TextField(s, GUILayout.Width(300));
                if (ns != s) entry.BoxedValue = ns;
            }
            else
            {
                GUILayout.Label(val?.ToString() ?? "null", GUILayout.Width(200));
            }
        }

        private void DrawNumber(ConfigEntryBase entry, Type t, object val)
        {
            float cur = Convert.ToSingle(val);
            if (TryRange(entry, out float min, out float max))
            {
                float nv = GUILayout.HorizontalSlider(cur, min, max, GUILayout.Width(230));
                if (t == typeof(int)) nv = Mathf.Round(nv);
                GUILayout.Label(t == typeof(int) ? ((int)nv).ToString() : nv.ToString("0.###"), GUILayout.Width(56));
                if (Mathf.Abs(nv - cur) > 1e-6f)
                    entry.BoxedValue = (t == typeof(int)) ? (object)Mathf.RoundToInt(nv) : (object)nv;
            }
            else
            {
                string s = (t == typeof(int)) ? Convert.ToInt32(val).ToString() : cur.ToString("0.######");
                string ns = GUILayout.TextField(s, GUILayout.Width(130));
                if (ns != s)
                {
                    if (t == typeof(int) && int.TryParse(ns, out int iv)) entry.BoxedValue = iv;
                    else if (t == typeof(float) && float.TryParse(ns, out float fv)) entry.BoxedValue = fv;
                }
            }
        }

        private void DrawColor(ConfigEntryBase entry, Color c)
        {
            GUILayout.BeginVertical(GUILayout.Width(300));
            Color nc = c;
            nc.r = Channel("R", c.r);
            nc.g = Channel("G", c.g);
            nc.b = Channel("B", c.b);
            nc.a = Channel("A", c.a);
            GUILayout.EndVertical();
            var sw = GUILayoutUtility.GetRect(38, 38, GUILayout.Width(38), GUILayout.Height(38));
            var old = GUI.color; GUI.color = nc; GUI.DrawTexture(sw, Texture2D.whiteTexture); GUI.color = old;
            if (nc != c) entry.BoxedValue = nc;
        }

        private float Channel(string lbl, float v)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(lbl, GUILayout.Width(14));
            float nv = GUILayout.HorizontalSlider(v, 0f, 1f, GUILayout.Width(210));
            GUILayout.Label(v.ToString("0.00"), GUILayout.Width(40));
            GUILayout.EndHorizontal();
            return nv;
        }

        private void DrawKeybind(ConfigEntryBase entry, KeyboardShortcut sc)
        {
            bool capturing = _capturing && ReferenceEquals(_captureTarget, entry);
            string label = capturing ? "press a key (Esc cancels)" : sc.ToString();
            if (GUILayout.Button(label, GUILayout.Width(230)))
            {
                _capturing = true; _captureTarget = entry;
            }
            if (capturing && Event.current.type == EventType.KeyDown)
            {
                var k = Event.current.keyCode;
                if (k == KeyCode.Escape) { _capturing = false; _captureTarget = null; }
                else if (k != KeyCode.None)
                {
                    entry.BoxedValue = new KeyboardShortcut(k);
                    _capturing = false; _captureTarget = null;
                }
                Event.current.Use();
            }
        }

        // ---------- helpers ----------
        private static IEnumerable<ConfigDefinition> OrderedKeys(ConfigFile cfg)
        {
            // Sort by section then key so the numbered-prefix convention (01, 02, 10b, 10c...) lays out
            // in author order.
            var list = new List<ConfigDefinition>(cfg.Keys);
            list.Sort((a, b) =>
            {
                int s = string.Compare(a.Section, b.Section, StringComparison.Ordinal);
                return s != 0 ? s : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
            return list;
        }

        private static IEnumerable<string> SectionsOf(FiresConfigUI.ModEntry mod)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var def in mod.Config.Keys) set.Add(def.Section);
            return set;
        }

        private static bool TryRange(ConfigEntryBase entry, out float min, out float max)
        {
            min = 0f; max = 1f;
            var av = entry.Description?.AcceptableValues;
            if (av == null) return false;
            var avt = av.GetType();
            if (!avt.IsGenericType || avt.GetGenericTypeDefinition() != typeof(AcceptableValueRange<>)) return false;
            try
            {
                min = Convert.ToSingle(avt.GetProperty("MinValue").GetValue(av));
                max = Convert.ToSingle(avt.GetProperty("MaxValue").GetValue(av));
                return max > min;
            }
            catch { return false; }
        }

        // Strip a leading "NN - " / "NNx - " ordering prefix from a section for display.
        private static string CleanSection(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int dash = s.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash <= 5 && IsOrderingPrefix(s.Substring(0, dash))) return s.Substring(dash + 3);
            return s;
        }

        // Strip a leading "NN " / "NNx " ordering prefix from a key for display.
        private static string CleanKey(string k)
        {
            if (string.IsNullOrEmpty(k)) return k;
            int sp = k.IndexOf(' ');
            if (sp > 0 && sp <= 4 && IsOrderingPrefix(k.Substring(0, sp))) return k.Substring(sp + 1);
            return k;
        }

        private static bool IsOrderingPrefix(string p)
        {
            bool digit = false;
            foreach (char c in p)
            {
                if (char.IsDigit(c)) { digit = true; continue; }
                if (char.IsLetter(c)) continue;
                return false;
            }
            return digit;
        }

        // ---------- styles ----------
        private bool _stylesReady;
        private GUIStyle _window, _panel, _navHeader, _navItem, _bodyTitle, _keyLabel, _dim, _settingBox, _label;
        private Texture2D _winBg, _boxBg, _rowBg;
        private Font _font;
        private FiresConfigUI.UiFont _fontChoice = (FiresConfigUI.UiFont)(-1);

        public void MarkStylesDirty() => _stylesReady = false;

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            float op = FiresConfigUI.CfgOpacity != null ? FiresConfigUI.CfgOpacity.Value : 0.97f;
            Color accent = FiresConfigUI.CfgAccent != null ? FiresConfigUI.CfgAccent.Value : new Color(1f, 0.84f, 0.5f);
            int fs = FiresConfigUI.CfgFontSize != null ? FiresConfigUI.CfgFontSize.Value : 13;
            Font font = GetFont(FiresConfigUI.CfgFont != null ? FiresConfigUI.CfgFont.Value : FiresConfigUI.UiFont.Default);

            // Valheim's GUI.skin window/box backgrounds can be transparent, so we draw our own opaque
            // panels (alpha = configured opacity) - otherwise the window is "drawn" but invisible.
            if (_winBg != null) UnityEngine.Object.Destroy(_winBg);
            if (_boxBg != null) UnityEngine.Object.Destroy(_boxBg);
            if (_rowBg != null) UnityEngine.Object.Destroy(_rowBg);
            _winBg = SolidTex(new Color(0.10f, 0.10f, 0.13f, op));
            _boxBg = SolidTex(new Color(0.15f, 0.15f, 0.18f, op));
            _rowBg = SolidTex(new Color(0.21f, 0.21f, 0.25f, Mathf.Min(op, 0.7f)));

            _window = new GUIStyle(GUI.skin.window);
            _window.normal.background = _window.onNormal.background = _winBg;
            _window.normal.textColor = _window.onNormal.textColor = Color.white;
            _window.font = font; _window.fontSize = fs + 2; _window.fontStyle = FontStyle.Bold;
            _window.alignment = TextAnchor.UpperCenter;
            _window.padding = new RectOffset(10, 10, 28, 10);
            _window.border = new RectOffset(12, 12, 28, 12);

            _label = Mk(GUI.skin.label, font, fs, Color.white);
            _panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(6, 6, 6, 6) };
            _panel.normal.background = _boxBg;
            _navHeader = Mk(GUI.skin.label, font, fs + 1, accent); _navHeader.fontStyle = FontStyle.Bold;
            _navItem = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontSize = fs, font = font };
            _bodyTitle = Mk(GUI.skin.label, font, fs + 3, accent); _bodyTitle.fontStyle = FontStyle.Bold;
            _keyLabel = Mk(GUI.skin.label, font, fs, Color.white); _keyLabel.fontStyle = FontStyle.Bold;
            _dim = Mk(GUI.skin.label, font, Mathf.Max(9, fs - 2), new Color(0.76f, 0.76f, 0.76f)); _dim.wordWrap = true;
            _settingBox = new GUIStyle(GUI.skin.box) { padding = new RectOffset(8, 8, 6, 6) };
            _settingBox.normal.background = _rowBg;
        }

        private static GUIStyle Mk(GUIStyle baseStyle, Font font, int size, Color color)
        {
            var s = new GUIStyle(baseStyle) { font = font, fontSize = size };
            s.normal.textColor = color;
            return s;
        }

        // Cache the dynamic font so dragging the opacity slider (which restyles) doesn't leak a Font each frame.
        private Font GetFont(FiresConfigUI.UiFont choice)
        {
            if (_font != null && _fontChoice == choice) return _font;
            _fontChoice = choice;
            try
            {
                switch (choice)
                {
                    case FiresConfigUI.UiFont.Arial:    _font = Font.CreateDynamicFontFromOSFont("Arial", 16); break;
                    case FiresConfigUI.UiFont.Consolas: _font = Font.CreateDynamicFontFromOSFont("Consolas", 16); break;
                    case FiresConfigUI.UiFont.Serif:    _font = Font.CreateDynamicFontFromOSFont(new[] { "Georgia", "Times New Roman", "serif" }, 16); break;
                    default: _font = null; break;   // null = the skin's default font
                }
            }
            catch { _font = null; }
            return _font;
        }

        private static Texture2D SolidTex(Color c)
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            t.SetPixels(px);
            t.Apply();
            t.wrapMode = TextureWrapMode.Repeat;
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }
    }
}
