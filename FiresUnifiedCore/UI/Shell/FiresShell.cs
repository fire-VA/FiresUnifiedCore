// Fires UI Shell, Core step 0/1 (Tools\ui_study_seneal\FIRES_UI_SHELL.md S1a master toggle, S3 module API, S4 lifecycle).
// The API below is the one signed off. IFiresShellModule and FiresShellModuleBase are implemented by
// mods and are frozen at v1 (new members go into IFiresShellModule2 or a new virtual). IShellContext / IShellHandle are
// implemented only by Core, so Core may add members to them later.
//
// HARD RULE: with [UI] Fires UI override OFF, nothing here creates a Harmony patch, a MonoBehaviour, a camera
// or a GameObject. Register only stores the module in a dictionary; every builder runs behind OverrideOn.
//
// Core scratch: step 0/1 authored in _scratch_visual_coreshell (not yet versioned).
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using FiresCore.UI.Shell.Kit;
using UnityEngine;

namespace FiresCore.UI.Shell
{
    // v1 - never edited once shipped; new members go into IFiresShellModule2 : IFiresShellModule.
    public interface IFiresShellModule
    {
        string Id { get; }            // stable, e.g. "rpgclasses.classes"; used for memory + test names
        string Title { get; }         // Latin-1, shown on the tab rail
        Sprite Icon { get; }          // may be null -> text-only tab
        int Order { get; }            // tab position; ties sorted by Id
        ShellHosting Hosting { get; } // Tab (shell owns the chrome) or OwnWindow (shell only launches)

        void BuildTab(RectTransform content, IShellContext ctx);
        void OnShow(string subView);
        void OnHide();
        void OnResize(Vector2 size);

        void OpenOwnWindow();

        string SettingsSection { get; }
    }

    public enum ShellHosting { Tab, OwnWindow }

    /// <summary>No-op defaults, so a Tab module doesn't stub OpenOwnWindow and an OwnWindow module doesn't stub BuildTab.</summary>
    public abstract class FiresShellModuleBase : IFiresShellModule, IShellEscapeHandler
    {
        public abstract string Id { get; }
        public abstract string Title { get; }
        public virtual Sprite Icon => null;
        public virtual int Order => 100;
        public virtual ShellHosting Hosting => ShellHosting.Tab;
        public virtual void BuildTab(RectTransform content, IShellContext ctx) { }
        public virtual void OnShow(string subView) { }
        public virtual void OnHide() { }
        public virtual void OnResize(Vector2 size) { }
        public virtual void OpenOwnWindow() { }
        public virtual string SettingsSection => null;

        /// <summary> Esc while this tab is visible: return true when the module consumed it (closed a popup,
        /// left a sub-view) and the hub stays open. Default false = the hub closes. The hub calls it through
        /// IShellEscapeHandler, which this base class implements.</summary>
        public virtual bool OnEscape() => false;

        /// <summary>true = the hub drops the big title + divider and puts the context strip in the tab-rail row,
        /// so a dense module gets the height. Add-only.</summary>
        public virtual bool Dense => false;

        /// <summary>the module's native page size at 1080 (UI units); zero = any. The hub's minimum window is this plus its
        /// chrome, and a non-zero size is laid out at native size and scaled to fit (never above 1.25). Add-only.</summary>
        public virtual Vector2 MinContentSize => Vector2.zero;

        /// <summary>"player" puts this page under the Player tab
        /// as a pill instead of its own top tab. Null = its own top tab. Add-only.</summary>
        public virtual string ParentTab => null;
        /// <summary>the pill's label under its parent tab (default = Title). Add-only.</summary>
        public virtual string PillLabel => Title;
        /// <summary>left-to-right order of the pills under the parent tab; the lowest is the tab's default page. Add-only.</summary>
        public virtual int PillOrder => Order;
        /// <summary>pages sharing a group form ONE pill (labelled by the group's lowest-PillOrder page) with inner pills to switch
        /// between them (e.g. "leaderboard": Leaderboard | Achievements). Null = a pill of its own. Add-only.</summary>
        public virtual string PillGroup => null;

        bool IShellEscapeHandler.OnEscape() => OnEscape();
    }

    /// <summary>
    /// Optional: a module that wants Esc first implements this (FiresShellModuleBase already does).
    /// The hub checks "module is IShellEscapeHandler" on Esc; true = consumed, the hub stays open. Kept OUT of
    /// IFiresShellModule, which is frozen at v1.
    /// </summary>
    public interface IShellEscapeHandler
    {
        bool OnEscape();
    }

    /// <summary> One sub-view pill: Visible false hides it without removing it; Badge = the dot.</summary>
    public struct ShellSubTab
    {
        public string Id;
        public string Title;
        public bool Visible;
        public bool Badge;

        public ShellSubTab(string id, string title, bool visible = true, bool badge = false)
        {
            Id = id; Title = title; Visible = visible; Badge = badge;
        }
    }

    public interface IShellHandle
    {
        void Unregister();
        void SetBadge(bool on);        // the "something new" dot on the tab
        void SetVisible(bool visible); // hide a tab without unregistering (e.g. admin-only)
        void Toast(string text, ToastKind kind = ToastKind.Info);
    }

    /// <summary>A context pill on the hub's status strip. OnClick null = display only.</summary>
    public struct ShellPill
    {
        public string Id;
        public string Label;
        public bool On;
        public Action OnClick;

        public ShellPill(string id, string label, bool on, Action onClick = null)
        {
            Id = id; Label = label; On = on; OnClick = onClick;
        }
    }

    public interface IShellContext
    {
        // NOTE: the sketch's "FiresTokens Tokens { get; }" cannot compile: FiresTokens is a static class. Modules read
        // FiresTokens.* directly. (Core-implemented interface, so a token accessor can be added later without a break.)
        ShellWidgets Ui { get; }
        void Confirm(string text, Action yes, Action no = null);
        void SetFooterHints(params (string key, string action)[] hints);
        void SetContextPills(params ShellPill[] pills);
        void SetSubTabs((string id, string title, bool badge)[] tabs, Action<string> onSelect);
        // per-item visibility + badge; the tuple overload above stays.
        void SetSubTabs(ShellSubTab[] tabs, Action<string> onSelect);
        void SelectSubTab(string id);
    }

    /// <summary>The Fires UI shell: module registry, the master toggle and the hub entry points.</summary>
    public static class FiresShell
    {
        // ---- config ([UI] section, Core's cfg) ----------------------------------------------------------
        internal const string Section = "UI";
        internal static ConfigEntry<bool> CfgOverride;
        internal static ConfigEntry<KeyboardShortcut> CfgHubKey;
        internal static ConfigEntry<bool> CfgReduceMotion;
        internal static ConfigEntry<float> CfgSkyDrift;
        private static bool s_bound;

        // ---- registry -------------------------------------------------------------------------------------
        internal sealed class Entry : IShellHandle
        {
            public IFiresShellModule Module;
            public bool Visible = true;
            public bool Badge;
            public bool Removed;
            // hub-side state (only touched while the master is ON)
            public RectTransform Content;
            public bool Built;
            public bool Failed;
            public string FailText;
            public ShellContext Ctx;

            public void Unregister()
            {
                if (Removed) return;
                FiresShell.RemoveEntry(this);
            }

            public void SetBadge(bool on)
            {
                if (Badge == on) return;
                Badge = on;
                ShellRuntime.Instance?.Hub?.OnRegistryChanged();
            }

            public void SetVisible(bool visible)
            {
                if (Visible == visible) return;
                Visible = visible;
                ShellRuntime.Instance?.Hub?.OnRegistryChanged();
            }

            public void Toast(string text, ToastKind kind = ToastKind.Info)
            {
                var hub = ShellRuntime.Instance?.Hub;
                if (hub != null && hub.IsOpen) hub.ShowToast(text, kind);
                else Log("toast (hub closed) from " + Module?.Id + ": " + text);
            }
        }

        private sealed class NoopHandle : IShellHandle
        {
            public static readonly NoopHandle Instance = new NoopHandle();
            public void Unregister() { }
            public void SetBadge(bool on) { }
            public void SetVisible(bool visible) { }
            public void Toast(string text, ToastKind kind = ToastKind.Info) { }
        }

        private static readonly Dictionary<string, Entry> s_entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly HashSet<string> s_loggedOnce = new HashSet<string>();

        /// <summary>Safe before the hub exists. Headless: a no-op handle. Same Id twice: the new module replaces the old one.</summary>
        public static IShellHandle Register(IFiresShellModule module)
        {
            if (module == null) return NoopHandle.Instance;
            string id = null;
            try { id = module.Id; } catch (Exception e) { Warn("Register: module.Id threw " + e.GetType().Name); }
            if (string.IsNullOrEmpty(id)) { Warn("Register: a module with an empty Id was ignored (" + module.GetType().FullName + ")"); return NoopHandle.Instance; }

            if (HeadlessGuard.IsHeadless)
            {
                LogOnce("headless:" + id, "Register " + id + ": headless process, no hub (no-op handle)");
                return NoopHandle.Instance;
            }

            if (s_entries.TryGetValue(id, out var old))
            {
                LogOnce("replace:" + id, "Register " + id + ": replaces the module registered earlier (" + old.Module.GetType().FullName
                    + " -> " + module.GetType().FullName + ")");
                ShellRuntime.Instance?.Hub?.DropEntry(old);
                old.Removed = true;
            }

            var entry = new Entry { Module = module };
            s_entries[id] = entry;
            if (!OverrideOn)
                LogOnce("kept:" + id, "Register " + id + ": kept, not shown (Fires UI override is OFF)");
            else
                Log("Register " + id + " (" + SafeTitle(module) + ")");
            ShellRuntime.Instance?.Hub?.OnRegistryChanged();
            return entry;
        }

        internal static void RemoveEntry(Entry e)
        {
            e.Removed = true;
            string id = e.Module?.Id;
            if (id != null && s_entries.TryGetValue(id, out var cur) && cur == e) s_entries.Remove(id);
            ShellRuntime.Instance?.Hub?.DropEntry(e);
            ShellRuntime.Instance?.Hub?.OnRegistryChanged();
            Log("Unregister " + id);
        }

        /// <summary>The registered, visible modules sorted by Order then Id.</summary>
        internal static List<Entry> SortedEntries()
        {
            var list = new List<Entry>();
            foreach (var e in s_entries.Values) if (!e.Removed) list.Add(e);
            list.Sort((a, b) =>
            {
                int oa = SafeOrder(a.Module), ob = SafeOrder(b.Module);
                return oa != ob ? oa.CompareTo(ob) : string.CompareOrdinal(a.Module.Id, b.Module.Id);
            });
            return list;
        }

        internal static Entry Find(string id) => id != null && s_entries.TryGetValue(id, out var e) && !e.Removed ? e : null;

        internal static IEnumerable<Entry> AllEntries() => s_entries.Values;

        /// <summary>
        /// True while the override is ON and Core's config-window key equals the hub key: the config window's own
        /// hotkey yields so one press opens the hub (its Settings tab opens the config window). OFF = false = today.
        /// </summary>
        internal static bool ConfigKeyYieldsToHub()
        {
            if (!OverrideOn || CfgHubKey == null || FiresConfigUI.CfgHotkey == null) return false;
            var hub = CfgHubKey.Value; var cfgKey = FiresConfigUI.CfgHotkey.Value;
            bool yields = hub.MainKey != KeyCode.None && hub.MainKey == cfgKey.MainKey;
            if (yields) LogOnce("cfgkey-yield", "config window key " + cfgKey + " yields to the hub key " + hub + " (override ON)");
            return yields;
        }

        // ---- master toggle ---------------------------------------------------------------------------------
        // the fresh-install default in ONE place.
        // Only new cfgs take it; an existing cfg keeps the player's value.
        internal const bool OverrideDefault = false;
        /// <summary>[UI] Fires UI override. Mods branch on this one value and never read Core's cfg by name.</summary>
        public static bool OverrideOn
        {
            get
            {
                bool v = CfgOverride != null && CfgOverride.Value;
                ShellLog.ConfigRead("Fires UI override", v);
                return v;
            }
        }

        /// <summary>Fired after the master changes live. With false, every hub/module window is already closed and destroyed.</summary>
        public static event Action<bool> OverrideChanged;

        /// <summary>the small on-screen minimap's RectTransform while the override is ON and the map exists, else null.
        /// Anchor HUD boxes (RPGmaker's territory box, the quest tracker) to it; fall back to Minimap.m_smallRoot when null. Add-only.</summary>
        public static RectTransform MinimapRect => ShellMinimap.Rect;

        /// <summary>the info plate under the round minimap (biome / day / time + pills) while the override is ON, else null.
        /// A HUD box that hangs under the minimap (RPGmaker's territory box) anchors below this when it is set. Add-only.</summary>
        public static RectTransform MinimapPlateRect => ShellMinimap.Plate;

        /// <summary>raised when the chat's typing focus opens (true) or closes (false) while the Fires UI override is ON, not
        /// on a message fade-in. Owners hide their own HUD bits over the chat while it is open (RPGClasses bars, FDT's perf overlay).
        /// Add-only contract: the exact shape binds.</summary>
        public static event Action<bool> ChatOpenChanged
        {
            add { ShellChat.Changed += value; }
            remove { ShellChat.Changed -= value; }
        }

        /// <summary>true while the chat is open for typing (override ON). Add-only.</summary>
        public static bool ChatOpen => ShellChat.IsOpen;

        /// <summary>puts a HUD box in the minimap column while the override is ON: Core places it under the plate and the boxes
        /// with a lower order (the previous one's real bottom - 8 px), centred on the map, clamped on screen; its position is put back
        /// on OFF or Unregister. Orders: territory 10, quest tracker 20, Core's group HUD 30. Call it again with the same id to replace.
        /// A no-op headless. Add-only.</summary>
        public static void RegisterMinimapColumn(string id, RectTransform box, int order) { if (!HeadlessGuard.IsHeadless) ShellMinimap.Register(id, box, order); }

        /// <summary>adds a card to the hub's Stats tab (Tools\CORE_STATS_SECTIONS_API.md). rows is called on the main thread, only
        /// while the tab shows, every refreshSeconds (clamped 0.25..60); a throw shows "error - see log" and logs once. Same id = replace.
        /// A no-op headless. Add-only.</summary>
        public static void RegisterStatsSection(string id, string title, int order, Func<IList<StatRow>> rows, float refreshSeconds = 1f)
        {
            if (HeadlessGuard.IsHeadless) return;
            StatsRegistry.Register(id, title, order, rows, refreshSeconds);
        }

        /// <summary>puts a HUD element under Core's dock engine (Tools\CORE_HUD_LAYOUT_PLAN.md): while layout mode is on (the Esc
        /// menu, or the hub toggle) it shows its outline + name and can be dragged, scaled with a corner grip and docked to the nearest HUD
        /// element (snap 24 px, gap 16, Alt or an 80 px pull undocks); saved per character + resolution. Core sets only position +
        /// localScale. Stop self-positioning while registered. A thin wrapper over FiresCore.UI.Dock.DockService (layer "hud"). Add-only.</summary>
        public static void RegisterHudElement(string id, RectTransform rect, HudElementOptions options = null)
        {
            if (HeadlessGuard.IsHeadless || rect == null || string.IsNullOrEmpty(id)) return;
            var o = options ?? new HudElementOptions();
            FiresCore.UI.Dock.DockService.Register(new FiresCore.UI.Dock.DockElementOptions
            {
                Id = id, Owner = o.Owner, Layer = "hud", Rect = rect, ShowGrip = o.Resizable, Label = o.Label,
                DefaultDockTo = o.DefaultDockTo, DefaultEdge = o.DefaultEdge, Visible = o.Visible, ShowInLayout = o.ShowInLayout,
                AlsoInInventoryLayout = o.AlsoInInventoryLayout,
                SizeChanged = o.SizeChanged, MinSize = o.MinSize, SizeReset = o.SizeReset,   // SizeReset
                LiveEdit = o.LiveEdit, DragHandle = o.DragHandle,   // live drag / resize without the editor
                RevealOnDoubleClick = o.RevealOnDoubleClick,
                ScalePerAxis = o.ScalePerAxis,
                HitBounds = o.HitBounds,
            });
        }

        /// <summary>
        /// registers one PANEL of a Fires window (the NPC book, the Dressing
        /// room, a FATI pane...) on that window's own layout layer. While any panel of <paramref name="screenKey"/> is on screen, holding Alt
        /// shows the Edit layout pill; the panels then move / resize / dock / reset like the inventory panels, saved per window + resolution.
        /// Core sets only the rect's position + localScale (the owner keeps its size and contents). Call once per panel after it exists;
        /// re-calling with the same id replaces the registration. Add-only.
        /// </summary>
        public static void RegisterScreenPanel(string screenKey, string id, RectTransform rect, string label, string owner = null, bool resizable = true)
        {
            if (HeadlessGuard.IsHeadless || rect == null || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(screenKey)) return;
            FiresCore.UI.Dock.DockService.Register(new FiresCore.UI.Dock.DockElementOptions
            {
                Id = id, Owner = owner, Layer = FiresCore.UI.Dock.DockService.ScreenLayer(screenKey), Rect = rect, ShowGrip = resizable, Label = label,
            });
        }

        /// <summary>
        /// registers a LIVE
        /// panel of a Fires window. Drag <paramref name="header"/> (title bar / frame) to move it at any time; the bottom-right handle resizes
        /// (scale 0.5-1.5); on release it docks gently to another panel of the same window (Shift = free placement). Saved per window +
        /// resolution like <see cref="RegisterScreenPanel"/>; the Alt editor still adds its extra options. Core sets only the panel's
        /// position + localScale. Add-only.
        /// </summary>
        public static void RegisterLivePanel(string screenKey, string id, RectTransform panel, RectTransform header, string label, string owner = null)
        {
            if (HeadlessGuard.IsHeadless || panel == null || header == null || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(screenKey)) return;
            FiresCore.UI.Dock.DockService.Register(new FiresCore.UI.Dock.DockElementOptions
            {
                Id = id, Owner = owner, Layer = FiresCore.UI.Dock.DockService.ScreenLayer(screenKey), Rect = panel, DragHandle = header,
                ShowGrip = true, Label = label, LiveEdit = true,
            });
        }

        /// <summary><see cref="RegisterLivePanel(string, string, RectTransform, RectTransform, string, string)"/> whose
        /// bottom-right handle resizes the panel's SIZE (not its scale) and reports the new size to <paramref name="sizeChanged"/>. Add-only.</summary>
        public static void RegisterLivePanel(string screenKey, string id, RectTransform panel, RectTransform header, string label, string owner, Action<Vector2> sizeChanged, Vector2 minSize)
        {
            if (HeadlessGuard.IsHeadless || panel == null || header == null || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(screenKey)) return;
            FiresCore.UI.Dock.DockService.Register(new FiresCore.UI.Dock.DockElementOptions
            {
                Id = id, Owner = owner, Layer = FiresCore.UI.Dock.DockService.ScreenLayer(screenKey), Rect = panel, DragHandle = header,
                ShowGrip = true, Label = label, LiveEdit = true, SizeChanged = sizeChanged, MinSize = minSize,
            });
        }

        /// <summary>the hub's visible module id (e.g. "fires.achievements"), or null while the hub is closed. Add-only.</summary>
        public static string ActiveModuleId { get { var hub = ShellRuntime.Instance != null ? ShellRuntime.Instance.Hub : null; return hub != null && hub.IsOpen ? hub.ActiveModuleId : null; } }

        /// <summary>hides the WHOLE HUD (vanilla + every docked HUD element, incl. Core's minimap column)
        /// while <paramref name="owner"/> holds it, e.g. while a config / NPC book window is open; owner-counted, so screens nest. Call
        /// HideHudWhile(owner, false) on close. Vanilla's m_userHidden is put back to what it was before the first holder.</summary>
        public static void HideHudWhile(string owner, bool hide) => FiresCore.UI.Dock.DockService.HideHudWhile(owner, hide);

        /// <summary>true while any owner holds the HUD hidden.</summary>
        public static bool HudHeldHidden => FiresCore.UI.Dock.DockService.HudHeldHidden;

        /// <summary>takes an element out of the dock engine (it stays where it is). Add-only.</summary>
        public static void UnregisterHudElement(string id) => FiresCore.UI.Dock.DockService.Unregister(id);

        /// <summary>every HUD element back to its default spot, size and grouping (and its saved layout dropped). Add-only.</summary>
        public static void ResetHudLayout() => FiresCore.UI.Dock.DockService.ResetLayer("hud");
        /// <summary>adds a pill option for the minimap plate (Tools\CORE_PLATE_PILLS_API.md). At most 3 pills show; the player picks
        /// which (right-click the plate). content runs on the main thread every refreshSeconds (0.25..60) while the pill is shown; an
        /// empty Text hides it for that tick. Same id = replace. A no-op headless. Add-only.</summary>
        public static void RegisterPlatePill(string id, string label, int order, Func<PillContent> content, float refreshSeconds = 1f)
        {
            if (HeadlessGuard.IsHeadless) return;
            PlatePills.Register(id, label, order, content, refreshSeconds);
        }

        /// <summary>removes a plate pill option. Add-only.</summary>
        public static void UnregisterPlatePill(string id) => PlatePills.Unregister(id);

        /// <summary>removes a Stats card. Add-only.</summary>
        public static void UnregisterStatsSection(string id) => StatsRegistry.Unregister(id);

        /// <summary>clicks on a registered column box (same id as RegisterMinimapColumn) while the cursor is free (Esc menu / hub;
        /// the box is raised above the Esc menu then). Every left/right click calls onClick on the main thread; check modifiers and
        /// e.button yourself. null clears. A throw is caught and logged. Add-only.</summary>
        public static void SetMinimapColumnClick(string id, Action<UnityEngine.EventSystems.PointerEventData> onClick) => ShellMinimap.SetColumnClick(id, onClick);

        /// <summary>removes a column box click handler. Add-only.</summary>
        public static void ClearMinimapColumnClick(string id) => ShellMinimap.ClearColumnClick(id);

        /// <summary>takes a box out of the minimap column (its own position comes back). Add-only.</summary>
        public static void UnregisterMinimapColumn(string id) => ShellMinimap.Unregister(id);

        /// <summary>raised when <see cref="MinimapRect"/> appears or goes back to vanilla (null). Add-only.</summary>
        public static event Action<RectTransform> MinimapChanged
        {
            add { ShellMinimap.Changed += value; }
            remove { ShellMinimap.Changed -= value; }
        }

        internal static ConfigEntry<float> CfgMinimapSize;
        internal static ConfigEntry<KeyCode> CfgLayoutPillKey;

        /// <summary>the hub's live night-sky RenderTexture while the hub is open (null otherwise). Draw it under a transparent
        /// module area (RawImage) to show the sky through it. Recreated on a screen resize: listen to SkyTextureChanged. Add-only.</summary>
        public static Texture SkyTexture => ShellSkyBackdrop.Current;

        /// <summary>raised when SkyTexture is created, recreated or goes away. Add-only.</summary>
        public static event Action SkyTextureChanged
        {
            add { ShellSkyBackdrop.SkyChanged += value; }
            remove { ShellSkyBackdrop.SkyChanged -= value; }
        }

        /// <summary>Fired with the new tab id when the hub's tab changes.</summary>
        public static event Action<string> TabChanged;

        internal static void RaiseTabChanged(string id)
        {
            try { TabChanged?.Invoke(id); } catch (Exception e) { Warn("TabChanged handler threw " + e.GetType().Name + ": " + e.Message); }
        }

        public static bool IsOpen => ShellRuntime.Instance != null && ShellRuntime.Instance.Hub != null && ShellRuntime.Instance.Hub.IsOpen;

        /// <summary>Opens the hub on a tab + sub-view (deep link). A no-op while the master is OFF or headless.</summary>
        public static void Open(string moduleId = null, string subView = null)
        {
            if (!OverrideOn) { LogOnce("open-off", "Open(" + moduleId + "): ignored, Fires UI override is OFF"); return; }
            if (HeadlessGuard.IsHeadless) return;
            var rt = ShellRuntime.Ensure();
            rt?.Hub?.Open(moduleId, subView);
        }

        public static void Close()
        {
            var hub = ShellRuntime.Instance?.Hub;
            if (hub != null && hub.IsOpen) hub.Close();
            // The ConfigPanel the Settings tab
            // launched (plus its conflict modal) is part of the hub session, so Close ends it too.
            if (ShellHub.SettingsLaunched)
            {
                ShellHub.SettingsLaunched = false;
                if (FiresCore.Bridge.ModUiRegistry.Close("FiresConfigPanel")) Log("Close: also closed the Settings-launched ConfigPanel");
            }
            var still = FiresCore.Bridge.ModUiRegistry.OpenKeys();
            if (still.Count > 0) Log("Close: mod UIs still open: " + string.Join(", ", still.ToArray()));
        }

        // ---- bind (called once from FiresUnifiedCore.Setup) ------------------------------------------------
        /// <summary>Binds the [UI] shell keys. With the master OFF this is the ONLY thing the shell does.</summary>
        internal static void Bind(ConfigFile cfg)
        {
            // ConfigSync-once rule: never bind twice in one process.
            if (s_bound || cfg == null) return;
            s_bound = true;

            CfgOverride = cfg.Bind(Section, "Fires UI override", OverrideDefault, new ConfigDescription(
                "Master switch for the unified Fires UI (the Fires hub, shared widgets, restyled panels). OFF = every mod keeps its "
                + "current UI, exactly as before. Experimental while the overhaul is in progress.",
                null, new FiresCore.Sync.ConfigurationManagerAttributes { IsAdvanced = true }));
            CfgHubKey = cfg.Bind(Section, "Fires hub key", new KeyboardShortcut(KeyCode.F1), new ConfigDescription(
                "Opens / closes the Fires hub (only while Fires UI override is ON). If another binding already uses this key, "
                + "the hub key is skipped and a warning is logged.",
                null, new FiresCore.Sync.ConfigurationManagerAttributes { IsAdvanced = true }));
            CfgReduceMotion = cfg.Bind(Section, "Reduce motion", false, new ConfigDescription(
                "Stops the hub's moving backdrop (the drifting night sky). Only used while Fires UI override is ON.",
                null, new FiresCore.Sync.ConfigurationManagerAttributes { IsAdvanced = true }));
            CfgSkyDrift = cfg.Bind(Section, "Sky drift speed", 4f, new ConfigDescription(
                "How fast the night sky behind the hub turns, as a multiple of the base speed (default 4). Only used while "
                + "Fires UI override is ON.",
                new AcceptableValueRange<float>(0f, 20f), new FiresCore.Sync.ConfigurationManagerAttributes { IsAdvanced = true }));
            // the small minimap restyle (ShellMinimap).
            CfgMinimapSize = cfg.Bind(Section, "Minimap size", 1f, new ConfigDescription(
                "Size of the small on-screen minimap, as a multiple of vanilla. Only used while Fires UI override is ON.",
                new AcceptableValueRange<float>(0.5f, 2f), new FiresCore.Sync.ConfigurationManagerAttributes { IsAdvanced = true }));
            // the Edit / Reset layout pills only while this key is held (read live)
            CfgLayoutPillKey = cfg.Bind(Section, "Layout pills modifier", KeyCode.LeftAlt, new ConfigDescription(
                "Hold this key (in the Esc menu or the inventory) to show the Edit layout / Reset layout pills. None = always shown.",
                null, new FiresCore.Sync.ConfigurationManagerAttributes { IsAdvanced = true }));

            CfgOverride.SettingChanged += (s, a) => ApplyOverride(live: true);
            CfgHubKey.SettingChanged += (s, a) => ShellHotkey.Invalidate();

            ApplyOverride(live: false);
        }

        private static bool s_applied;   // the last applied master state

        private static void ApplyOverride(bool live)
        {
            bool on = OverrideOn;
            if (live && on == s_applied) return;
            s_applied = on;
            Log("Fires UI override " + (on ? "ON" : "OFF") + (live ? " (changed live)" : " (at load)"));

            if (on)
            {
                // Gate 1: nothing below runs while the master is OFF.
                if (!HeadlessGuard.IsHeadless)
                {
                    ShellRuntime.Ensure();
                    foreach (var e in s_entries.Values) if (!e.Removed) Log("module now shown: " + e.Module.Id);
                }
            }
            else
            {
                // Gate 2: OFF closes + destroys everything the shell made (it is a no-op when nothing was made).
                ShellRuntime.Shutdown();
            }

            if (!live) return;   // at load nobody has subscribed yet; the value is read through OverrideOn
            try { OverrideChanged?.Invoke(on); }
            catch (Exception e) { Warn("OverrideChanged handler threw " + e.GetType().Name + ": " + e.Message); }
        }

        // ---- helpers --------------------------------------------------------------------------------------
        private static string SafeTitle(IFiresShellModule m) { try { return m.Title; } catch { return m.Id; } }
        private static int SafeOrder(IFiresShellModule m) { try { return m.Order; } catch { return 100; } }

        internal static void Log(string msg) => ShellLog.Debug(msg);
        internal static void Warn(string msg) => ShellLog.Warn(msg);

        internal static void LogOnce(string key, string msg)
        {
            if (s_loggedOnce.Add(key)) ShellLog.Info(msg);
        }
    }

    /// <summary>Shell log lines ([Shell] prefix) and the config-read Debug lines (logged when a read value changes).</summary>
    internal static class ShellLog
    {
        private static readonly Dictionary<string, string> s_lastRead = new Dictionary<string, string>();

        public static void Debug(string m) { try { FiresCore.Logging.FiresLogger.LogDebug("[Shell] " + m); } catch { } }
        public static void Info(string m) { try { FiresCore.Logging.FiresLogger.LogInfo("[Shell] " + m); } catch { } }
        public static void Warn(string m) { try { FiresCore.Logging.FiresLogger.LogWarning("[Shell] " + m); } catch { } }

        /// <summary>A Debug line at the point of use for a config read; repeats only when the value read differs.</summary>
        public static void ConfigRead(string key, object value)
        {
            string v = value == null ? "null" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            if (s_lastRead.TryGetValue(key, out var last) && last == v) return;
            s_lastRead[key] = v;
            Debug("config read [UI] " + key + " = " + v);
        }
    }

    /// <summary>
    /// The hub hotkey: [UI] Fires hub key, with the conflict check against every other loaded plugin's KeyboardShortcut /
    /// KeyCode binding. The check runs once every plugin has bound (first poll after the title scene) and again when the key
    /// changes. On a conflict: a warning, and the hotkey is skipped (FiresShell.Open / the fires_hub command still work).
    /// </summary>
    internal static class ShellHotkey
    {
        private static bool s_checked;
        private static bool s_blocked;
        private static KeyboardShortcut s_checkedKey;

        public static void Invalidate() { s_checked = false; }

        public static KeyboardShortcut Current
        {
            get
            {
                var k = FiresShell.CfgHubKey != null ? FiresShell.CfgHubKey.Value : KeyboardShortcut.Empty;
                ShellLog.ConfigRead("Fires hub key", k.ToString());
                return k;
            }
        }

        /// <summary>True when the hub key went down this frame (raw Input, so the hub's own input block can't swallow it).</summary>
        public static bool PressedThisFrame()
        {
            var key = Current;
            if (key.MainKey == KeyCode.None) return false;
            if (!s_checked || !key.Equals(s_checkedKey)) { if (!AllPluginsLoaded()) return false; Check(key); }
            if (s_blocked) return false;
            if (FiresCore.Input.FiresInputBlock.IsTyping(out string typingIn) && typingIn != "fires panel") return false;   // not while typing in chat / console / another mod's field
            if (!UnityEngine.Input.GetKeyDown(key.MainKey)) return false;
            foreach (var m in key.Modifiers) if (!UnityEngine.Input.GetKey(m)) return false;
            return true;
        }

        private static bool AllPluginsLoaded() => FejdStartup.instance != null || ZNet.instance != null;

        private static void Check(KeyboardShortcut key)
        {
            s_checked = true;
            s_checkedKey = key;
            s_blocked = false;
            var hits = new List<string>();
            try
            {
                foreach (var info in Chainloader.PluginInfos.Values)
                {
                    var plugin = info?.Instance;
                    var cfg = plugin != null ? plugin.Config : null;
                    if (cfg == null) continue;
                    foreach (var def in new List<ConfigDefinition>(cfg.Keys))
                    {
                        var entry = cfg[def];
                        if (entry == null || entry == (ConfigEntryBase)FiresShell.CfgHubKey) continue;
                        // Core's own config-window key yields to the hub while the override is ON (ConfigKeyYieldsToHub)
                        if (entry == (ConfigEntryBase)FiresConfigUI.CfgHotkey) continue;
                        bool same = false;
                        if (entry.BoxedValue is KeyboardShortcut ks) same = ks.MainKey == key.MainKey && SameModifiers(ks, key);
                        else if (entry.BoxedValue is KeyCode kc) same = kc == key.MainKey && IsEmpty(key.Modifiers);
                        if (same) hits.Add(info.Metadata.Name + " [" + def.Section + "] " + def.Key);
                    }
                }
            }
            catch (Exception e) { ShellLog.Warn("hub key conflict check failed (" + e.GetType().Name + ": " + e.Message + "); hotkey kept"); return; }

            if (hits.Count > 0)
            {
                s_blocked = true;
                ShellLog.Warn("hub key " + key + " is already used by " + string.Join(", ", hits.ToArray())
                    + ". The hub hotkey is skipped; pick another [UI] Fires hub key (console: fires_hub opens the hub).");
            }
            else ShellLog.Debug("hub key " + key + ": no conflict");
        }

        private static bool SameModifiers(KeyboardShortcut a, KeyboardShortcut b)
        {
            var sa = new HashSet<KeyCode>(a.Modifiers);
            var sb = new HashSet<KeyCode>(b.Modifiers);
            return sa.SetEquals(sb);
        }

        private static bool IsEmpty(IEnumerable<KeyCode> mods) { foreach (var _ in mods) return false; return true; }
    }
}
