// Fires UI Shell, Core step 0/1: the Fires hub window (FIRES_UI_SHELL.md S4 lifecycle, S5 memory, S7 layout).
// Built from the kit exactly like the approved mockup's Hub (FiresShellMockup.cs): Window 1420 x 940 at 1080p, Header
// (context pills + context text + FIRES title + divider), TabRail, SubTabs, Body, Footer. The night-sky backdrop sits behind it.
//
// Exists only while [UI] Fires UI override is ON (owned by ShellRuntime). Stable names:
// Window/fires.hub/Content/Header|Tabs/<id>|Pills/<id>|Footer|Body/Tab/<moduleId>/...
//
// Settings tab: LAUNCHES the existing ConfigPanel (closes the hub, then FiresConfigUI.Open). Hosting the IMGUI ConfigPanel
// inside the uGUI hub would need ConfigPanel changes (its window rect, Escape, hotkey and input handling are its own); launching
// changes nothing in ConfigPanel, so it is the safer step-1 choice.
using System;
using System.Collections.Generic;
using System.Text;
using FiresCore.Bridge;
using FiresCore.Input;
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    internal sealed class ShellHub
    {
        internal const string WindowId = "fires.hub";
        internal const string SettingsId = "fires.settings";
        private const string RegistryKey = "fires.hub";
        private static readonly Vector2 DefaultSize = new Vector2(1420f, 940f);   // mockup Hub
        private const float ScreenMargin = 16f;
        private const float TabRailHeight = 38f, SubTabGap = 18f, SubTabHeight = 32f, BodyGap = 28f, FooterReserve = 50f;
        private const int CanvasSortingOrder = 2000;
        private const float SlowTick = 0.5f;

        private readonly ShellRuntime _runtime;
        private GameObject _canvasGo;
        private Canvas _canvas;
        internal Canvas HubCanvas => _canvas;                                                     // the perf probe
        internal string ActiveModuleId => _active != null && _active.Module != null ? _active.Module.Id : "-";
        private RectTransform _root, _backdropRoot, _body;
        private KitWindow _win;
        private ShellSkyBackdrop _sky;
        private RectTransform _toast;
        private float _toastUntil;
        private float _nextSlow;
        private string _pillSig;
        private Vector2Int _screen;

        private FiresShell.Entry _active;
        private string _rememberedTab;
        private float _headerHeight = FiresTokens.HeaderHeight;

        internal bool IsOpen { get; private set; }

        internal ShellHub(ShellRuntime runtime) { _runtime = runtime; }

        internal void RegisterBuiltIns()
        {
            // once per process: after an OFF -> ON cycle the existing entry is reused (its content was dropped by Destroy)
            if (FiresShell.Find(ShellStatsModule.ModuleId) == null) FiresShell.Register(new ShellStatsModule());
            if (FiresShell.Find(ShellAchievementsModule.ModuleId) == null) FiresShell.Register(new ShellAchievementsModule());
            // the Settings tab is a real module (the config rework); it replaces the tab that launched ConfigPanel.
            if (FiresShell.Find(ShellConfigModule.ModuleId) == null) FiresShell.Register(new ShellConfigModule());
        }

        private static bool ConfigModuleLive => FiresShell.Find(ShellConfigModule.ModuleId) != null;

        internal bool IsActive(FiresShell.Entry e) => IsOpen && e != null && _active == e;

        // ---- open / close ---------------------------------------------------------------------------------

        internal void Open(string moduleId, string subView)
        {
            if (moduleId == SettingsId) { if (ConfigModuleLive) moduleId = ShellConfigModule.ModuleId; else { OpenSettings(); return; } }
            if (!IsOpen)
            {
                if (_canvasGo == null) Build();
                _canvasGo.SetActive(true);
                ApplyMemory();
                _sky = new ShellSkyBackdrop(_backdropRoot);
                ModUiRegistry.Register(RegistryKey, () => IsOpen, Close, () => _canvasGo);
                // the hub no longer holds the text-capture gate
                // for its whole life. Player input is the modal InputBlock below; the gate is held only while a text field is
                // actually focused (FiresInputBlockDriver's auto token), so it can never outlive the hub.
                // Core's modal block owns the cursor (its
                // GameCamera.UpdateMouseCapture prefix is the single keeper) and gates Player/PlayerController input + pins the camera,
                // as for every other Fires modal. Released on Close only if the hub set it.
                bool wasBlocked = FiresCore.UI.InputBlock.IsBlocked;
                if (!wasBlocked) { FiresCore.UI.InputBlock.Block(true); _setInputBlock = true; }
                ShellLog.Debug("hub input: " + (wasBlocked ? "InputBlock already held by another UI (kept)" : "InputBlock set (cursor free, player input off)"));
                IsOpen = true;
                _nextSlow = 0f;
                ShellLog.Info("hub opened" + (moduleId != null ? " on " + moduleId : "") + (subView != null ? " / " + subView : ""));
            }
            // F1 with no target opens the Player
            // tab on its lowest pill (Stats). A caller that names a module still gets that module.
            if (moduleId == null) { Select(PlayerDefault() ?? FirstEntry(), subView); return; }
            Select(FiresShell.Find(moduleId) ?? FirstEntry(), subView);
        }

        internal void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (_active != null) SafeCall(_active, "OnHide", () => _active.Module.OnHide());
            SaveMemory();
            FiresInputBlock.Release(this);
            if (_setInputBlock) { _setInputBlock = false; FiresCore.UI.InputBlock.Block(false); ShellLog.Debug("hub input: InputBlock released"); }
            ModUiRegistry.Unregister(RegistryKey);
            _sky?.Dispose();
            _sky = null;
            if (_toast != null) { Kill(_toast.gameObject); _toast = null; }
            if (_canvasGo != null) _canvasGo.SetActive(false);
            ShellLog.Info("hub closed");
        }

        /// <summary>Master OFF: close, then destroy the canvas and every module's content (modules rebuild on the next ON).</summary>
        internal void Destroy()
        {
            Close();
            foreach (var e in FiresShell.AllEntries()) { e.Content = null; e.Built = false; e.Failed = false; e.Ctx = null; }
            _active = null;
            if (_canvasGo != null) UnityEngine.Object.Destroy(_canvasGo);
            _canvasGo = null; _canvas = null; _root = null; _backdropRoot = null; _body = null; _win = null;
        }

        private void OpenSettings()
        {
            ShellLog.Debug("Settings tab: launching the existing ConfigPanel (not hosted in the hub)");
            Close();
            SettingsLaunched = true;   // FiresShell.Close also closes this window (and any modal it opened)
            try { FiresConfigUI.Open(); }
            catch (Exception e) { ShellLog.Warn("Settings: ConfigPanel open failed (" + e.GetType().Name + ": " + e.Message + ")"); }
        }

        // ---- build -----------------------------------------------------------------------------------------

        private void Build()
        {
            _canvasGo = new GameObject("FiresShellHub", typeof(RectTransform)) { layer = 5 };
            _canvasGo.transform.SetParent(_runtime.transform, false);
            _canvas = _canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = CanvasSortingOrder;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;   // mockup: match height
            _canvasGo.AddComponent<GraphicRaycaster>();
            _root = (RectTransform)_canvasGo.transform;

            _backdropRoot = Widgets.Stretch(Widgets.Node(_root, "Backdrop"), 0, 0, 0, 0);
            _win = Widgets.Window(_root, WindowId, DefaultSize, Corner.Rounded);
            _win.Fill.raycastTarget = true;   // clicks on the window body never reach the backdrop
            ShellWidgets.Clickable(_win.Close.Rect, Close);
            _body = Widgets.Node(_win.Content, "Body");
            BuildDragHandles();
            LayoutBody();
            _screen = new Vector2Int(Screen.width, Screen.height);
            ShellLog.Debug("hub built (canvas " + Screen.width + "x" + Screen.height + ")");
        }

        private float BodyTop => _headerHeight + TabRailHeight + SubTabGap + SubTabHeight + BodyGap + _extraRows * (SubTabHeight + 8f);   // + Player pill rows

        private void LayoutBody()
        {
            var size = _win.Root.sizeDelta;
            float contentW = size.x - 2f * (FiresTokens.WindowPadding + FiresTokens.S8);
            float contentH = size.y - FiresTokens.S24 - FiresTokens.S16;
            Widgets.Place(_body, 0f, BodyTop, contentW, Mathf.Max(100f, contentH - BodyTop - FooterReserve));
        }

        private Vector2 CanvasArea()
        {
            Canvas.ForceUpdateCanvases();
            var r = _root.rect.size;
            return new Vector2(Mathf.Max(1f, r.x - 2f * ScreenMargin), Mathf.Max(1f, r.y - 2f * ScreenMargin));
        }

        // ---- move / resize -------------------

        internal static bool SettingsLaunched;
        private const float GripSize = 14f;
        private bool _lastDense;
        private Image _gripArt;

        private void BuildDragHandles()
        {
            // header strip: under the pills / close (later siblings take the click first), above the fill
            var bar = Widgets.Node(_win.Root, "DragBar");
            bar.anchorMin = new Vector2(0f, 1f); bar.anchorMax = new Vector2(1f, 1f); bar.pivot = new Vector2(0.5f, 1f);
            bar.anchoredPosition = Vector2.zero; bar.sizeDelta = new Vector2(0f, FiresTokens.S24 + FiresTokens.ContextStripHeight);
            var barImg = bar.gameObject.AddComponent<Image>(); barImg.color = new Color(0f, 0f, 0f, 0f); barImg.raycastTarget = true;
            bar.SetSiblingIndex(_win.Fill.transform.GetSiblingIndex() + 1);
            var move = bar.gameObject.AddComponent<ShellWindowDrag>();
            move.DragMode = ShellWindowDrag.Mode.Move; move.Target = _win.Root; move.Area = CanvasArea;
            move.Changed = null; move.Ended = OnWindowDragged;

            // bottom-right grip: Core's shared grip art in kit colours (Edge at rest, GoldBright on hover)
            var grip = Widgets.Node(_win.Root, "ResizeGrip");
            grip.anchorMin = grip.anchorMax = grip.pivot = new Vector2(1f, 0f);
            grip.anchoredPosition = new Vector2(-4f, 4f);
            grip.sizeDelta = new Vector2(GripSize + 8f, GripSize + 8f);
            var hit = grip.gameObject.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f); hit.raycastTarget = true;
            var art = Widgets.Node(grip, "Art");
            art.anchorMin = art.anchorMax = art.pivot = new Vector2(1f, 0f); art.anchoredPosition = Vector2.zero; art.sizeDelta = new Vector2(GripSize, GripSize);
            _gripArt = FiresCore.UI.FiresResizeGrip.AddGrip(art, FiresTokens.Edge);
            if (_gripArt != null) _gripArt.raycastTarget = false;
            art.gameObject.AddComponent<KitLeaf>();
            grip.SetAsLastSibling();
            var size = grip.gameObject.AddComponent<ShellWindowDrag>();
            size.DragMode = ShellWindowDrag.Mode.Resize; size.Target = _win.Root; size.Area = CanvasArea; size.MinSize = MinWindowSize;
            size.Changed = OnWindowResizing; size.Ended = OnWindowDragged; size.DoubleClicked = ResetWindow;
            size.Tint = _gripArt; size.Rest = FiresTokens.Edge; size.Hover = FiresTokens.GoldBright;
        }

        // the size a module lays itself out in: its native page (it is scaled as a whole) or, without one, the body.
        private Vector2 ModuleSize() { Vector2 m = ModuleMin(); return m.x > 0f && m.y > 0f ? m : _body.rect.size; }

        private FiresShellModuleBase ActiveBase => _active?.Module as FiresShellModuleBase;

        private Vector2 ModuleMin()
        {
            var b = ActiveBase;
            if (b == null) return Vector2.zero;
            try { return b.MinContentSize; } catch { return Vector2.zero; }
        }

        // The window's chrome around the body (UI units): window size minus the body's laid-out size.
        private Vector2 Chrome()
        {
            if (_win == null || _body == null) return Vector2.zero;
            return _win.Root.sizeDelta - _body.rect.size;
        }

        private Vector2 MinWindowSize()
        {
            Vector2 min = FiresTokens.MinWindowSize, m = ModuleMin();
            if (m.x > 0f && m.y > 0f) min = Vector2.Max(min, m + Chrome());
            return Vector2.Min(min, Vector2.Max(DefaultArea(), FiresTokens.MinWindowSize));   // a module floor never forces edge-to-edge
        }

        private void OnWindowResizing()
        {
            LayoutBody();
            ApplyModuleScale();
            if (_active != null) SafeCall(_active, "OnResize", () => _active.Module.OnResize(ModuleSize()));
        }

        private void OnWindowDragged()
        {
            OnWindowResizing();
            SaveMemory();
            ShellLog.Debug("hub rect shared: " + Mathf.RoundToInt(_win.Root.sizeDelta.x) + "x" + Mathf.RoundToInt(_win.Root.sizeDelta.y) + " @ " + Mathf.RoundToInt(_win.Root.anchoredPosition.x) + "," + Mathf.RoundToInt(_win.Root.anchoredPosition.y) + " (res " + Screen.width + "x" + Screen.height + ", every tab)");
        }

        private void ResetWindow()
        {
            var area = CanvasArea();
            _win.Root.sizeDelta = DefaultWindowSize();
            _win.Root.anchoredPosition = Vector2.zero;
            OnWindowDragged();
            ShellLog.Debug("hub window: reset (double-click on the grip)");
        }

        // Default (no memory): the mockup size, grown to fit the active module's native page when it has one.
        // never larger than DefaultArea.
        private Vector2 DefaultWindowSize() => Vector2.Min(Vector2.Max(DefaultSize, MinWindowSize()), DefaultArea());

        // The screen area minus a margin: 5% of the height top and bottom (at least 40 units at 1080), 2% left and right.
        private Vector2 DefaultArea()
        {
            var area = CanvasArea();
            float my = Mathf.Max(40f, area.y * 0.05f), mx = Mathf.Max(24f, area.x * 0.02f);
            return new Vector2(Mathf.Max(1f, area.x - 2f * mx), Mathf.Max(1f, area.y - 2f * my));
        }

        // Memory is per resolution: a size chosen at 3440 is not the one wanted at 1080.
        // "@2" drops the rects saved under the old edge-to-edge default, so the padded default shows without a reset.
        private static string MemoryKey => WindowId + "@2@" + Screen.width + "x" + Screen.height;

        // A module with a native page size is laid out at that size and scaled to fit the body (never above 1.25); others stretch.
        private string _scaleLogged;
        private void ApplyModuleScale()
        {
            if (_active == null || _active.Content == null || _body == null) return;
            Vector2 native = ModuleMin(), body = _body.rect.size;
            var c = _active.Content;
            if (native.x <= 0f || native.y <= 0f)
            {
                if (c.localScale != Vector3.one) { Widgets.Stretch(c, 0, 0, 0, 0); c.localScale = Vector3.one; }
                return;
            }
            float s = Mathf.Min(body.x / native.x, body.y / native.y, 1.25f);
            s = Mathf.Max(0.1f, s);
            // pinned to the TOP of the body, so slack goes above the footer, not above the content.
            c.anchorMin = c.anchorMax = c.pivot = new Vector2(0.5f, 1f);
            c.anchoredPosition = Vector2.zero;
            c.sizeDelta = native;
            c.localScale = new Vector3(s, s, 1f);
            string line = "content " + Mathf.RoundToInt(body.x) + "x" + Mathf.RoundToInt(body.y) + ", module " + _active.Module.Id
                + " min " + Mathf.RoundToInt(native.x) + "x" + Mathf.RoundToInt(native.y) + ", scale " + s.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + ", binding " + (s >= 1.25f ? "max 1.25" : body.x / native.x <= body.y / native.y ? "width" : "height");
            if (line != _scaleLogged) { _scaleLogged = line; ShellLog.Debug(line); }
        }

        // A tab whose native page is bigger than the window grows the window to its minimum (kept inside the screen).
        private void EnsureWindowFitsModule()
        {
            if (_win == null) return;
            Vector2 min = MinWindowSize(), size = _win.Root.sizeDelta;
            if (size.x >= min.x && size.y >= min.y) return;
            var area = CanvasArea();
            Vector2 want = Vector2.Min(Vector2.Max(size, min), Vector2.Max(area, min));
            _win.Root.sizeDelta = want;
            _win.Root.anchoredPosition = ShellWindowDrag.Clamp(_win.Root.anchoredPosition, want, area);
            LayoutBody();
            ShellLog.Debug("hub window: grown to " + want + " for module " + _active?.Module.Id);
        }

        // ---- window memory ---------------------------------------------------------------------------------

        private void ApplyMemory()
        {
            var area = CanvasArea();
            Vector2 min = MinWindowSize();
            var s = ShellWindows.Restore(MemoryKey, area, min);
            Vector2 size = s != null ? new Vector2(s.W, s.H) : DefaultWindowSize();
            size = Vector2.Min(Vector2.Max(size, min), Vector2.Max(area, min));
            _win.Root.sizeDelta = size;
            _win.Root.anchoredPosition = s != null ? ShellWindowDrag.Clamp(new Vector2(s.X, s.Y), size, area) : Vector2.zero;
            if (s != null && !string.IsNullOrEmpty(s.Tab)) _rememberedTab = s.Tab;
            LayoutBody();
            ApplyModuleScale();
            if (_active != null) SafeCall(_active, "OnResize", () => _active.Module.OnResize(ModuleSize()));
        }

        private void SaveMemory()
        {
            if (_win == null) return;
            ShellWindows.Save(MemoryKey, new ShellWindows.WindowState
            {
                X = _win.Root.anchoredPosition.x, Y = _win.Root.anchoredPosition.y,
                W = _win.Root.sizeDelta.x, H = _win.Root.sizeDelta.y,
                Scale = _canvas != null ? _canvas.scaleFactor : 1f,
                Tab = _active != null ? _active.Module.Id : _rememberedTab,
            });
        }

        // ---- tabs ------------------------------------------------------------------------------------------

        private FiresShell.Entry FirstEntry()
        {
            foreach (var e in FiresShell.SortedEntries()) if (e.Visible) return e;
            return null;
        }

        // ---- (top tabs "Player" / "Info" / "Config settings"; Player = pills Stats / Appearance / Classes /
        // Leaderboard): pages that name ParentTab "player" share ONE rail tab and are switched by pills; a PillGroup is one pill with
        // inner pills (Leaderboard | Achievements) --------------------------------------------------------------------------------
        internal const string PlayerTab = "group:player";
        private readonly Dictionary<string, string> _groupLast = new Dictionary<string, string>();   // pill group -> last page shown
        private string _playerLast;
        private int _extraRows;
        private static FiresShellModuleBase B(FiresShell.Entry e) => e?.Module as FiresShellModuleBase;
        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
        private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
        private static bool InPlayer(FiresShell.Entry e) => e != null && Safe(() => B(e)?.ParentTab) == "player";
        private static string GroupKey(FiresShell.Entry e) => Safe(() => B(e)?.PillGroup) ?? e.Module.Id;
        private static List<FiresShell.Entry> PlayerPages()
        {
            var l = new List<FiresShell.Entry>();
            foreach (var e in FiresShell.SortedEntries()) if (e.Visible && InPlayer(e)) l.Add(e);
            l.Sort((a, b) => { int c = SafeInt(() => B(a).PillOrder).CompareTo(SafeInt(() => B(b).PillOrder)); return c != 0 ? c : string.CompareOrdinal(a.Module.Id, b.Module.Id); });
            return l;
        }
        private FiresShell.Entry PlayerDefault()
        {
            var pages = PlayerPages();
            // the Player tab always opens on its lowest pill (pages are in PillOrder), never the last one used.
            return pages.Count > 0 ? pages[0] : null;
        }
        private FiresShell.Entry GroupPage(string key)
        {
            var pages = PlayerPages().FindAll(p => GroupKey(p) == key);
            if (_groupLast.TryGetValue(key, out var last)) { var r = pages.Find(p => p.Module.Id == last); if (r != null) return r; }
            return pages.Count > 0 ? pages[0] : null;
        }

        private List<(string id, string title)> RailTabs()
        {
            var tabs = new List<(string id, string title)>();
            bool player = false;
            foreach (var e in FiresShell.SortedEntries())
            {
                if (!e.Visible) continue;
                if (InPlayer(e)) { if (!player) { player = true; tabs.Insert(0, (PlayerTab, "Player")); } continue; }
                string title;
                try { title = e.Module.Title; } catch { title = e.Module.Id; }
                tabs.Add((e.Module.Id, string.IsNullOrEmpty(title) ? e.Module.Id : title));
            }
            if (!ConfigModuleLive) tabs.Add((SettingsId, "Config settings"));   // the config module is the Settings tab
            return tabs;
        }
        private string ActiveRailId => _active == null ? null : InPlayer(_active) ? PlayerTab : _active.Module.Id;

        private void Select(FiresShell.Entry e, string subView)
        {
            if (!IsOpen) return;
            if (e == null) { _active = null; RefreshChrome(); return; }

            if (e.Module.Hosting == ShellHosting.OwnWindow)
            {
                // no per-module "keep the hub open" flag in v1: the hub closes, the module opens its own window
                Close();
                SafeCall(e, "OpenOwnWindow", () => e.Module.OpenOwnWindow());
                return;
            }

            if (_active != null && _active != e)
            {
                var old = _active;
                SafeCall(old, "OnHide", () => old.Module.OnHide());
                if (old.Content != null) old.Content.gameObject.SetActive(false);
            }
            _active = e;
            _rememberedTab = e.Module.Id;
            if (InPlayer(e)) { _playerLast = e.Module.Id; _groupLast[GroupKey(e)] = e.Module.Id; }

            if (e.Content == null)
            {
                e.Content = Widgets.Stretch(Widgets.PathNode(_body, "Tab", e.Module.Id), 0, 0, 0, 0);
                e.Built = false;
            }
            e.Content.gameObject.SetActive(true);
            ApplyModuleScale();   // BuildTab already gets the content at the module's native size
            if (!e.Built)
            {
                e.Built = true;
                e.Ctx = new ShellContext(this, e);
                if (!SafeCall(e, "BuildTab", () => e.Module.BuildTab(e.Content, e.Ctx))) ShowError(e);
            }
            RefreshChrome();   // first, so a Dense module's body height is known before it sizes itself
            EnsureWindowFitsModule();
            ApplyModuleScale();
            if (!e.Failed)
            {
                SafeCall(e, "OnShow", () => e.Module.OnShow(subView));
                SafeCall(e, "OnResize", () => e.Module.OnResize(ModuleSize()));
            }
            FiresShell.RaiseTabChanged(e.Module.Id);
        }

        private void Step(int dir)
        {
            var tabs = RailTabs();
            tabs.RemoveAll(t => t.id == SettingsId);   // Tab cycles the module tabs; Settings is a click
            if (tabs.Count == 0) return;
            int i = _active != null ? tabs.FindIndex(t => t.id == ActiveRailId) : -1;
            i = ((i + dir) % tabs.Count + tabs.Count) % tabs.Count;
            Select(tabs[i].id == PlayerTab ? PlayerDefault() : FiresShell.Find(tabs[i].id), null);
        }

        /// <summary>Calls into a module; a throw marks the tab failed (logged), and the hub keeps working.</summary>
        private bool SafeCall(FiresShell.Entry e, string what, Action a)
        {
            try { a(); return true; }
            catch (Exception ex)
            {
                ShellLog.Warn("module " + e.Module.Id + " " + what + " threw " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                if (what == "BuildTab" || what == "OnShow") { e.Failed = true; e.FailText = ex.GetType().Name; }
                return false;
            }
        }

        private void ShowError(FiresShell.Entry e)
        {
            if (e.Content == null) return;
            for (int i = e.Content.childCount - 1; i >= 0; i--) Kill(e.Content.GetChild(i).gameObject);
            var l = KitText.Create(e.Content, "Error", "This module hit an error - see the log (" + e.FailText + ")",
                FontRole.SerifRegular, FiresTokens.BodySize, FiresTokens.Danger, KitAlign.Center);
            Widgets.Stretch(l.Rect, 0, 0, 0, 0);
        }

        internal void DropEntry(FiresShell.Entry e)
        {
            if (e == null) return;
            if (_active == e)
            {
                if (IsOpen) SafeCall(e, "OnHide", () => e.Module.OnHide());
                _active = null;
            }
            if (e.Content != null) Kill(e.Content.gameObject);
            e.Content = null; e.Built = false; e.Ctx = null;
        }

        internal void OnRegistryChanged()
        {
            if (!IsOpen) return;
            if (_active == null || _active.Removed || !_active.Visible)
            {
                if (_active != null && _active.Content != null) _active.Content.gameObject.SetActive(false);
                _active = null;
                Select(FirstEntry(), null);   // falls back to the first tab
                return;
            }
            RefreshChrome();
        }

        // ---- chrome (header, pills, tab rail, sub-tabs, footer) ---------------------------------------------

        internal void RefreshChrome()
        {
            if (_win == null) return;
            var c = _win.Content;
            foreach (var n in new[] { "Header", "Tabs", "Pills", "Footer", "DevbarDropdown", "PlayerPills", "GroupPills" })   // + the Player pill rows
            {
                var t = c.Find(n);
                if (t != null) Kill(t.gameObject);
            }

            // header + context pills
            var header = Widgets.Header(c, ContextText(), "Fires");
            _headerHeight = header.Height;
            // Dense chrome: no big title + divider; the context strip (pills left, context right) shares the
            // tab-rail row, so the module gets the height.
            // every page is Dense
            bool dense = true;
            if (dense)
            {
                if (header.Title != null) header.Title.Rect.gameObject.SetActive(false);
                var div = header.Root.Find("Divider");
                if (div != null) div.gameObject.SetActive(false);
                header.Root.sizeDelta = new Vector2(0f, FiresTokens.ContextStripHeight);
                // the pill strip keeps its own row on EVERY
                // page; the tab rail goes under it (was 0 = the rail shared the pills' row).
                _headerHeight = FiresTokens.ContextStripHeight + 6f;   // + 6 px clear of the tabs (UI no-overlap rule)
            }
            if (dense != _lastDense) { _lastDense = dense; ShellLog.Debug("hub chrome: " + (dense ? "Dense (" + _active?.Module.Id + ")" : "full")); }
            var ctx = _active?.Ctx;
            // DEVBAR (BUILD_SPEC.md): the pinned dev pills (default = the four Core pills it replaces), then the module's
            // context pills, wrapping up to 4 rows with "+N" and "v"; the title, divider and tab rail move down under the last row.
            float extra = Devbar.DrawPills(header.Pills, ctx?.Pills);
            if (extra > 0f)
            {
                header.Root.sizeDelta += new Vector2(0f, extra);
                if (!dense)
                {
                    if (header.Title != null) header.Title.Rect.anchoredPosition -= new Vector2(0f, extra);
                    var dv = header.Root.Find("Divider") as RectTransform;
                    if (dv != null) dv.anchoredPosition -= new Vector2(0f, extra);
                }
                _headerHeight += extra;
            }
            _pillSig = PillSignature();

            // tab rail
            var tabs = RailTabs();
            var badges = new HashSet<string>();
            foreach (var e in FiresShell.SortedEntries()) if (e.Badge) badges.Add(e.Module.Id);
            var rail = Widgets.TabRail(c, _headerHeight, tabs.ToArray(), ActiveRailId, badges);   // a Player page lights "Player"
            foreach (var t in tabs)
            {
                var node = rail.Find(t.id) as RectTransform;
                string id = t.id;
                if (node != null) ShellWidgets.Clickable(node, () => OnTabClicked(id));
            }

            // the Player pills (one per pill group), then a group's inner pills; the module's own sub-tabs go under them
            _extraRows = 0;
            float rowY = _headerHeight + TabRailHeight + SubTabGap;
            if (InPlayer(_active))
            {
                var pages = PlayerPages();
                var groups = new List<(string id, string title)>(); var seen = new HashSet<string>();
                foreach (var p in pages) { string k = GroupKey(p); if (seen.Add(k)) groups.Add((k, Safe(() => B(p).PillLabel) ?? p.Module.Id)); }
                string activeKey = GroupKey(_active);
                var row = Widgets.SubTabs(c, rowY, groups.ToArray(), activeKey);
                row.name = "PlayerPills";
                foreach (var g in groups) { var node = row.Find(g.id) as RectTransform; string gid = g.id; if (node != null) ShellWidgets.Clickable(node, () => { var t = GroupPage(gid); if (t != null && t != _active) Select(t, null); }); }
                var members = pages.FindAll(p => GroupKey(p) == activeKey);
                if (members.Count > 1)
                {
                    rowY += SubTabHeight + 8f; _extraRows++;
                    var inner = new (string id, string title)[members.Count];
                    for (int i = 0; i < inner.Length; i++) inner[i] = (members[i].Module.Id, Safe(() => B(members[i]).PillLabel) ?? members[i].Module.Id);
                    var irow = Widgets.SubTabs(c, rowY, inner, _active.Module.Id);
                    irow.name = "GroupPills";
                    foreach (var m in members) { var node = irow.Find(m.Module.Id) as RectTransform; var mm = m; if (node != null) ShellWidgets.Clickable(node, () => { if (mm != _active) Select(mm, null); }); }
                }
                if (ctx?.SubTabs != null && ctx.SubTabs.Length > 0) { rowY += SubTabHeight + 8f; _extraRows++; }
            }

            // module sub-tabs: visible items only; a badge = a GoldBright dot at the pill's top-right ("Badge")
            if (ctx?.SubTabs != null && ctx.SubTabs.Length > 0)
            {
                var vis = new List<ShellSubTab>();
                foreach (var st in ctx.SubTabs) if (st.Visible && !string.IsNullOrEmpty(st.Id)) vis.Add(st);
                if (vis.Count > 0)
                {
                    var list = new (string id, string title)[vis.Count];
                    for (int i = 0; i < list.Length; i++) list[i] = (vis[i].Id, vis[i].Title ?? vis[i].Id);
                    var pillsRoot = Widgets.SubTabs(c, rowY, list, ctx.ActiveSubTab);   // under the Player pill rows
                    foreach (var st in vis)
                    {
                        var node = pillsRoot.Find(st.Id) as RectTransform;
                        if (node == null) continue;
                        string sid = st.Id;
                        ShellWidgets.Clickable(node, () => ctx.SelectSubTab(sid));
                        if (st.Badge)
                        {
                            var b = Widgets.Box(node, "Badge", KitSprites.Circle(), FiresTokens.GoldBright, false);
                            Widgets.PlaceRight(b.rectTransform, 2f, 2f, FiresTokens.BadgeSize, FiresTokens.BadgeSize);
                        }
                    }
                }
            }

            // footer
            var hints = ctx?.FooterHints;
            if (hints == null || hints.Length == 0)
                hints = new[] { ("Tab", "next tab"), (HubKeyText(), "close"), ("Esc", "close") };
            Widgets.Footer(c, hints, null);
            // the footer sits UNDER the body, so anything a page pops up
            // (tooltips, menus) draws above it. They don't overlap otherwise (the body ends FooterReserve above it).
            var footer = c.Find("Footer");
            if (footer != null && _body != null && _body.parent == footer.parent) footer.SetSiblingIndex(_body.GetSiblingIndex());

            LayoutBody();
            Devbar.DrawDropdown(c, header.Pills.sizeDelta.y + 8f);   // its own overlay layer, under the pills
        }

        /// <summary> (devbar): re-read the pill state on the next tick; rebuild = redraw the chrome now.</summary>
        internal void PokeChrome(bool rebuild = false)
        {
            _nextSlow = 0f;
            if (rebuild && IsOpen) RefreshChrome();
        }

        private void OnTabClicked(string id)
        {
            if (id == SettingsId) { OpenSettings(); return; }
            var e = id == PlayerTab ? PlayerDefault() : FiresShell.Find(id);
            if (e != null && e != _active) Select(e, null);
        }

        private static string HubKeyText()
        {
            var k = ShellHotkey.Current;
            return k.MainKey == KeyCode.None ? "F1" : k.ToString();
        }

        private static string ContextText()
        {
            var parts = new List<string>();
            try
            {
                var p = Player.m_localPlayer;
                if (p != null) parts.Add(p.GetPlayerName());
                if (EnvMan.instance != null && ZNet.instance != null) parts.Add("Day " + EnvMan.instance.GetDay(ZNet.instance.GetTimeSeconds()));
            }
            catch { }
            return parts.Count > 0 ? KitText.Context(parts.ToArray()) : "";
        }

        // Devcommands / Debug mode / No cost / God, read from the vanilla state. Clickable for admins through the vanilla
        // console commands (so the server's rules apply); display-only for everyone else.
        private static List<ShellPill> CorePills()
        {
            bool admin = IsAdmin();
            if (admin != s_pillAdminLogged) { s_pillAdminLogged = admin; ShellLog.Info(admin ? "pills: clickable (admin / host)" : "pills: display only (not an admin on this server: add your id to adminlist.txt)"); }
            var p = Player.m_localPlayer;
            bool dev = Terminal.m_cheat;
            bool debug = Player.m_debugMode;
            bool noCost = p != null && SafeBool(() => p.NoCostCheat());
            bool god = p != null && SafeBool(() => p.InGodMode());
            return new List<ShellPill>
            {
                new ShellPill("devcommands", "Devcommands", dev, admin ? (Action)(() => RunCommand("devcommands")) : null),
                new ShellPill("debug", "Debug mode", debug, admin ? (Action)(() => RunCommand("debugmode")) : null),
                new ShellPill("nocost", "No cost", noCost, admin && p != null ? (Action)(() => RunCommand("nocost")) : null),
                new ShellPill("god", "God", god, admin && p != null ? (Action)(() => RunCommand("god")) : null),
            };
        }

        private static bool IsAdmin()
        {
            try
            {
                var zn = ZNet.instance;
                return zn != null && (zn.IsServer() || zn.LocalPlayerIsAdminOrHost());
            }
            catch { return false; }
        }

        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

        private static bool? s_pillAdminLogged;

        private static bool PillState(string cmd)
        {
            var p = Player.m_localPlayer;
            switch (cmd)
            {
                case "devcommands": return Terminal.m_cheat;
                case "debugmode": return Player.m_debugMode;
                case "nocost": return p != null && SafeBool(() => p.NoCostCheat());
                case "god": return p != null && SafeBool(() => p.InGodMode());                default: return false;
            }
        }

        private static void RunCommand(string cmd)
        {
            var console = global::Console.instance;
            if (console == null) { ShellLog.Warn("pill: no console to run '" + cmd + "'"); return; }
            // vanilla refuses god / nocost / debugmode SILENTLY while devcommands is off, so a
            // click on them did nothing. Devcommands goes on first (one command, the same the player would type); Info line per click.
            if (cmd != "devcommands" && !Terminal.m_cheat) { console.TryRunCommand("devcommands"); ShellLog.Info("pill: devcommands turned on first (vanilla needs it for '" + cmd + "')"); }
            bool before = PillState(cmd);
            console.TryRunCommand(cmd);
            ShellLog.Info($"pill: ran '{cmd}' ({(before ? "on" : "off")} -> {(PillState(cmd) ? "on" : "off")})");
            var hub = ShellRuntime.Instance?.Hub;
            if (hub != null) hub._nextSlow = 0f;   // re-read the state on the next tick
        }

        private string PillSignature()
        {
            var sb = new StringBuilder(Devbar.Signature());   // every pinned pill's state + text
            sb.Append('|').Append(ContextText());
            return sb.ToString();
        }

        // ---- toast -----------------------------------------------------------------------------------------

        internal void ShowToast(string text, ToastKind kind)
        {
            if (!IsOpen || _win == null) return;
            if (_toast != null) Kill(_toast.gameObject);
            _toast = Widgets.Toast(_win.Root, text ?? "", kind, 56f);
            _toastUntil = Time.unscaledTime + FiresTokens.ToastDuration;
        }

        // ---- per frame (ShellRuntime.Update) -----------------------------------------------------------------

        internal void Tick()
        {
            if (IsOpen && (_canvasGo == null || !_canvasGo.activeInHierarchy)) { ShellLog.Debug("hub canvas gone or hidden while open: closing (input released)"); Close(); }
            if (!IsOpen)
            {
                FiresInputBlock.Release(this);   // belt and braces (a token from an older open, any path that skipped Close)
                Devbar.Open = false;   // the dropdown never survives the hub closing
                if (ShellHotkey.PressedThisFrame() && !FiresInputBlock.IsTyping()) FiresShell.Open();
                return;
            }

            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) && Devbar.Open) { Devbar.SetOpen(false); }   // Esc closes the dropdown first
            else if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                // the visible module gets Esc first; true = consumed, the hub stays open
                var esc = _active != null && !_active.Failed ? _active.Module as IShellEscapeHandler : null;
                bool consumed = false;
                if (esc != null)
                {
                    var a = _active;
                    SafeCall(a, "OnEscape", () => consumed = esc.OnEscape());
                }
                if (!consumed) { Close(); return; }
            }
            else if (ShellHotkey.PressedThisFrame()) { Close(); return; }
            if (UnityEngine.Input.GetKeyDown(KeyCode.Tab) && !FiresInputBlock.IsTyping())
            {
                bool back = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
                Step(back ? -1 : 1);
            }

            if (Screen.width != _screen.x || Screen.height != _screen.y)
            {
                _screen = new Vector2Int(Screen.width, Screen.height);
                ApplyMemory();
            }

            _sky?.Tick();

            if (_toast != null && Time.unscaledTime > _toastUntil) { Kill(_toast.gameObject); _toast = null; }

            if (Time.unscaledTime >= _nextSlow)
            {
                _nextSlow = Time.unscaledTime + SlowTick;
                if (PillSignature() != _pillSig) RefreshChrome();
                if (_active != null && !_active.Failed && _active.Module is ShellStatsModule stats)
                {
                    try { stats.Refresh(); } catch (Exception e) { ShellLog.Warn("stats refresh threw " + e.GetType().Name + ": " + e.Message); }
                }
            }
        }

        // Free the cursor every frame while open, the way ConfigPanel.DrawGui does (vanilla re-locks it each frame; the
        // ModUiRegistry -> GUIManager.IsCustomPanelOpen route only exists when the Marketplace host mod is loaded).
        // no cursor writes here any more. UI.InputBlock's UpdateMouseCapture prefix is the single keeper (a second
        // per-frame writer strobes the pointer, see InputBlock.CameraPinPatch).
        private bool _setInputBlock;
        internal void LateTick()
        {
        }

        // Rename + deactivate before Destroy: Destroy is deferred to the end of the frame, and the kit's Group finds nodes by
        // name, so a same-frame rebuild must not find the dying node.
        private static void Kill(GameObject go)
        {
            if (go == null) return;
            go.name = "_dead";
            go.SetActive(false);
            UnityEngine.Object.Destroy(go);
        }
    }
}
