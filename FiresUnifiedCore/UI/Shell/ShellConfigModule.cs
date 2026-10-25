// Fires UI Shell, Core
// the hub's SETTINGS tab as a real module. It reuses CfgDiscovery
// (the same mods / sections / rows, the same Write path with its log line and the server lock) and draws with kit widgets.
// Override OFF: never registered, F1 / va_config open today's IMGUI ConfigPanel unchanged.
// v1: Mods + Keybinds views here; Conflicts and Files open today's ConfigPanel in that view (it stays the full tool for them).
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using FiresCore.UI.Shell.Kit;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    internal sealed class ShellConfigModule : FiresShellModuleBase
    {
        internal const string ModuleId = "fires.config";
        public override string Id => ModuleId;
        public override string Title => "Config settings";
        public override int Order => 1000;
        public override bool Dense => true;

        private const float NavWidth = 250f, RowH = 82f, DescH = 36f, HeaderH = 40f, ControlW = 330f, ResetW = 30f, TopBar = 44f;
        // gap between the list content and its scrollbar; font scale section header > row name > description.
        private const float ListRightPad = FiresTokens.S16, SectionHeaderSize = 21f, RowNameSize = 16f;

        private RectTransform _root, _nav, _body, _list, _viewport, _toolbar, _titleRow;
        private ScrollRect _scroll, _navScroll;
        private TMP_InputField _search;
        private KitLabel _titleLabel, _titleNote;
        private IShellContext _ctx;
        private string _view = "mods", _mod, _query = "";
        private readonly Dictionary<string, bool> _collapsed = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly List<Item> _items = new List<Item>();
        private readonly Dictionary<int, RectTransform> _built = new Dictionary<int, RectTransform>();
        private int _firstBuilt = -1, _lastBuilt = -1;
        private CfgDescriptor _capturing;
        private ShellConfigDriver _driver;

        private sealed class Item { public bool Header; public string Section; public int Count; public CfgDescriptor Row; public float Y, H; }

        public override void BuildTab(RectTransform content, IShellContext ctx)
        {
            _ctx = ctx;
            _root = content;
            s_live = this;
            _driver = content.gameObject.AddComponent<ShellConfigDriver>();
            _driver.Module = this;

            ctx.SetSubTabs(new (string id, string title, bool badge)[] { ("mods", "Mods", false), ("keybinds", "Keybinds", false), ("conflicts", "Conflicts", false), ("files", "Files", false) }, OnSubTab);
            ctx.SetFooterHints(("Ctrl+F", "search"), (FiresTokens.GlyphReset, "reset to default"), ("Esc", "close"));

            // left: mods
            // the mod list scrolls inside its own clipped viewport, never past the frame.
            var navVpGo = new GameObject("NavViewport", typeof(RectTransform));
            var navVp = (RectTransform)navVpGo.transform; navVp.SetParent(content, false);
            navVp.anchorMin = new Vector2(0f, 0f); navVp.anchorMax = new Vector2(0f, 1f); navVp.pivot = new Vector2(0f, 1f);
            navVp.anchoredPosition = Vector2.zero; navVp.sizeDelta = new Vector2(NavWidth, 0f);
            navVpGo.AddComponent<RectMask2D>();
            var navHit = navVpGo.AddComponent<Image>(); navHit.color = new Color(0f, 0f, 0f, 0f);
            _nav = Widgets.Node(navVp, "Nav");
            _nav.anchorMin = new Vector2(0f, 1f); _nav.anchorMax = new Vector2(1f, 1f); _nav.pivot = new Vector2(0.5f, 1f);
            _nav.anchoredPosition = Vector2.zero; _nav.sizeDelta = new Vector2(0f, 0f);
            _navScroll = navVpGo.AddComponent<ScrollRect>();
            _navScroll.viewport = navVp; _navScroll.content = _nav; _navScroll.horizontal = false; _navScroll.scrollSensitivity = 40f;
            _navScroll.movementType = ScrollRect.MovementType.Clamped;
            AddScrollbar(_navScroll, navVp, "NavScrollbar");

            // right: toolbar, title row, list
            _body = Widgets.Node(content, "Body");
            _body.anchorMin = Vector2.zero; _body.anchorMax = Vector2.one;
            _body.offsetMin = new Vector2(NavWidth + FiresTokens.S16, 0f); _body.offsetMax = Vector2.zero;

            _toolbar = Widgets.Node(_body, "Toolbar");
            TopStrip(_toolbar, 0f, TopBar);
            _search = MakeInput(_toolbar, "Search names, sections, keys and descriptions...", s => { _query = s ?? ""; Rebuild(); });
            var sr = (RectTransform)_search.transform;
            sr.anchorMin = new Vector2(0f, 0f); sr.anchorMax = new Vector2(1f, 1f); sr.offsetMin = new Vector2(0f, 4f); sr.offsetMax = new Vector2(-420f, -4f);

            _titleRow = Widgets.Node(_body, "TitleRow");
            TopStrip(_titleRow, TopBar + 6f, 34f);
            _titleLabel = KitText.Create(_titleRow, "Title", "", FontRole.SerifBold, 22f, FiresTokens.Gold, KitAlign.Left, 0f, false, false);
            Widgets.Stretch(_titleLabel.Rect, 0, 0, 0, 0);
            _titleNote = KitText.Create(_titleRow, "Note", "", FontRole.SansRegular, FiresTokens.HintSize, FiresTokens.TextMuted, KitAlign.Right, 0f, false, false);
            Widgets.Stretch(_titleNote.Rect, 0, 0, 0, 0);

            // virtualised list
            var vpGo = new GameObject("Viewport", typeof(RectTransform));
            _viewport = (RectTransform)vpGo.transform; _viewport.SetParent(_body, false);
            _viewport.anchorMin = Vector2.zero; _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = Vector2.zero; _viewport.offsetMax = new Vector2(0f, -(TopBar + 46f));
            vpGo.AddComponent<RectMask2D>();
            var hit = vpGo.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f);
            _list = Widgets.Node(_viewport, "List");
            _list.anchorMin = new Vector2(0f, 1f); _list.anchorMax = new Vector2(1f, 1f); _list.pivot = new Vector2(0.5f, 1f);
            _scroll = vpGo.AddComponent<ScrollRect>();
            _scroll.viewport = _viewport; _scroll.content = _list; _scroll.horizontal = false; _scroll.scrollSensitivity = 40f;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.onValueChanged.AddListener(_ => BuildVisible());
            _viewport.offsetMax = new Vector2(-(BarWidth + 4f + ListRightPad), _viewport.offsetMax.y);   // room for the bar at the right edge; + a gap so rows never touch the bar
            AddScrollbar(_scroll, _viewport, "ListScrollbar");

            BuildToolbarToggles();
            CfgDiscovery.RebuildIfChanged();
            _mod = FirstMod();
            Rebuild();
            ShellLog.Info("config module: built ("+ CfgDiscovery.ModNames.Count + " mods, " + CfgDiscovery.Descriptors.Count + " settings)");
        }

        private static void TopStrip(RectTransform rt, float y, float h)
        {
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -y); rt.sizeDelta = new Vector2(0f, h);
        }

        public override void OnShow(string subView)
        {
            if (CfgDiscovery.RebuildIfChanged()) Rebuild();
        }

        public override bool OnEscape()
        {
            if (_capturing != null) { _capturing = null; BuildVisible(true); return true; }
            if (_typing != null) { CancelTyped(); return true; }   // Esc cancels a typed slider value (the hub stays open)
            return false;
        }

        // ---- sub-tabs / toolbar ------------------------------------------------------------------------

        private void OnSubTab(string id)
        {
            // both are now views INSIDE this
            // page. Conflicts = only settings in an unresolved keybind conflict (the IMGUI Conflicts view's data: KeybindConflictDetector
            // + KeybindConflictStore); Files = every setting grouped by its .cfg file (ConfigEntryBase.ConfigFile.ConfigFilePath, the
            // file the IMGUI Files view edits). A second click on the active one toggles back to Mods. Never opens the IMGUI window.
            if ((id == "conflicts" || id == "files") && _view == id)
            {
                id = "mods";
                _ctx.SelectSubTab("mods");
            }
            if (id == "conflicts") RefreshConflictEntries();
            _view = id;
            ShellLog.Info("config module: view -> '" + id + "'" + (id == "conflicts" ? " (" + _conflictEntries.Count + " settings in a keybind conflict)" : ""));
            _scroll.verticalNormalizedPosition = 1f;
            Rebuild();
        }

        // the config entries that sit in an unresolved keybind conflict, same source as ConfigPanel's Conflicts view.
        private readonly HashSet<ConfigEntryBase> _conflictEntries = new HashSet<ConfigEntryBase>();
        private void RefreshConflictEntries()
        {
            _conflictEntries.Clear();
            try
            {
                FiresCore.Input.KeybindRegistry.EnsureScanned();
                var unresolved = FiresCore.Input.KeybindConflictStore.UnresolvedOf(FiresCore.Input.KeybindConflictDetector.Detect());
                foreach (var c in unresolved)
                    foreach (var m in c.Members)
                        if (m != null && m.Entry != null) _conflictEntries.Add(m.Entry);
            }
            catch (Exception e) { ShellLog.Warn("config module: conflict scan failed: " + e.Message); }
        }

        private static string FileOf(CfgDescriptor d)
        {
            try { return d.Entry?.ConfigFile?.ConfigFilePath ?? "(no file)"; } catch { return "(no file)"; }
        }

        private RectTransform _togglesRoot;
        private void BuildToolbarToggles()
        {
            if (_togglesRoot != null) UnityEngine.Object.Destroy(_togglesRoot.gameObject);
            _togglesRoot = Widgets.Node(_toolbar, "Toggles");
            _togglesRoot.anchorMin = new Vector2(1f, 0f); _togglesRoot.anchorMax = new Vector2(1f, 1f); _togglesRoot.pivot = new Vector2(1f, 0.5f);
            _togglesRoot.anchoredPosition = Vector2.zero; _togglesRoot.sizeDelta = new Vector2(410f, 0f);
            var adv = Widgets.DiamondToggle(_togglesRoot, "advanced", "Show advanced", FiresConfigUI.ShowAdvanced, 210f, 8f, 26f, true);
            ShellWidgets.Clickable(adv, () => FlipBool(FiresConfigUI.CfgShowAdvanced));
            var fires = Widgets.DiamondToggle(_togglesRoot, "firesonly", "Fires mods only", CfgDiscovery.FiresOnly, 0f, 8f, 26f, true);
            ShellWidgets.Clickable(fires, () => { FlipBool(FiresConfigUI.CfgFiresOnly); CfgDiscovery.Rebuild(); });
        }

        private void FlipBool(ConfigEntry<bool> e)
        {
            if (e == null) return;
            CfgDiscovery.Write(e, !e.Value, "hub settings toolbar");
            BuildToolbarToggles();
            Rebuild();
        }

        // ---- data ----------------------------------------------------------------------------------------

        private string FirstMod()
        {
            foreach (var m in CfgDiscovery.ModNames) return m;
            return null;
        }

        private bool Visible(CfgDescriptor d)
        {
            if (d == null || d.Entry == null) return false;
            if (!FiresConfigUI.ShowAdvanced && d.Tags != null && d.Tags.IsAdvanced) return false;
            if (_view == "keybinds" && d.Kind != CtrlKind.KeyBind) return false;
            if (_view == "conflicts" && !_conflictEntries.Contains(d.Entry)) return false;   // Conflicts filter
            if (_query.Length >= 2)
            {
                string q = _query;
                return Has(d.Label, q) || Has(d.Key, q) || Has(d.Section, q) || Has(d.Description, q) || Has(d.LongDescription, q) || Has(d.ModName, q);
            }
            return true;
        }

        private static bool Has(string s, string q) => !string.IsNullOrEmpty(s) && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        private bool Searching => _query.Length >= 2;

        internal void Rebuild()
        {
            if (_list == null) return;
            BuildNav();
            _items.Clear();
            float y = 0f; int shown = 0, changed = 0;
            bool allMods = Searching || _view == "keybinds" || _view == "conflicts" || _view == "files";   // conflicts / files span every mod
            var mods = allMods ? new List<string>(CfgDiscovery.ModNames) : new List<string> { _mod };
            if (_view == "files")
            {   // one collapsible group per .cfg file, in first-seen order
                var order = new List<string>();
                var byFile = new Dictionary<string, List<CfgDescriptor>>(StringComparer.OrdinalIgnoreCase);
                foreach (var mod in mods)
                    foreach (var section in CfgDiscovery.SectionsOf(mod))
                        foreach (var d in CfgDiscovery.RowsOf(mod, section))
                        {
                            if (!Visible(d)) continue;
                            string f = FileOf(d);
                            if (!byFile.TryGetValue(f, out var list)) { list = new List<CfgDescriptor>(); byFile[f] = list; order.Add(f); }
                            list.Add(d);
                        }
                foreach (var f in order)
                {
                    var rows = byFile[f];
                    string key = "file|" + f;
                    bool collapsed = !Searching && (!_collapsed.TryGetValue(key, out bool c) || c);
                    _items.Add(new Item { Header = true, Section = System.IO.Path.GetFileName(f), Count = rows.Count, Y = y, H = HeaderH });
                    _headerKeys[_items.Count - 1] = key;
                    y += HeaderH;
                    if (collapsed) continue;
                    foreach (var d in rows)
                    {
                        _items.Add(new Item { Row = d, Y = y, H = RowH });
                        y += RowH; shown++;
                        if (IsChanged(d)) changed++;
                    }
                }
                mods.Clear();
            }
            foreach (var mod in mods)
            {
                if (mod == null) continue;
                foreach (var section in CfgDiscovery.SectionsOf(mod))
                {
                    var rows = new List<CfgDescriptor>();
                    foreach (var d in CfgDiscovery.RowsOf(mod, section)) if (Visible(d)) rows.Add(d);
                    if (rows.Count == 0) continue;
                    string key = mod + "|" + section;
                    bool collapsed = !Searching && (!_collapsed.TryGetValue(key, out bool c) || c);   // sections start COLLAPSED (622-setting mods were unnavigable); search opens the ones with matches
                    _items.Add(new Item { Header = true, Section = (mods.Count > 1 ? mod + " - " : "") + CfgDiscovery.CleanSection(section), Count = rows.Count, Row = rows[0], Y = y, H = HeaderH });
                    _items[_items.Count - 1].Row = null; _items[_items.Count - 1].Count = rows.Count;
                    _headerKeys[_items.Count - 1] = key;
                    y += HeaderH;
                    if (collapsed) continue;
                    foreach (var d in rows)
                    {
                        _items.Add(new Item { Row = d, Y = y, H = RowH });
                        y += RowH; shown++;
                        if (IsChanged(d)) changed++;
                    }
                }
            }
            _list.sizeDelta = new Vector2(0f, Mathf.Max(y, 1f));
            _titleLabel.Value = Searching ? "Results for \"" + _query + "\"" : _view == "keybinds" ? "Keybinds"
                : _view == "conflicts" ? "Keybind conflicts" : _view == "files" ? "Config files" : (_mod ?? "");
            _titleNote.Value = shown + " settings" + (changed > 0 ? " - " + changed + " changed" : "");
            foreach (var kv in _built) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value.gameObject);
            _built.Clear(); _firstBuilt = _lastBuilt = -1;
            BuildVisible(true);
            if (_items.Count == 0) ShellLog.Debug("config module: no settings to show (" + (Searching ? "search '" + _query + "'" : "mod " + _mod) + ")");
        }

        private readonly Dictionary<int, string> _headerKeys = new Dictionary<int, string>();

        private static bool IsChanged(CfgDescriptor d)
        {
            try { return d.Default != null && !Equals(d.Entry.BoxedValue, d.Default); } catch { return false; }
        }

        // ---- left nav --------------------------------------------------------------------------------------

        private void BuildNav()
        {
            for (int i = _nav.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_nav.GetChild(i).gameObject);
            float y = 0f;
            foreach (var mod in CfgDiscovery.ModNames)
            {
                int n = 0;
                foreach (var section in CfgDiscovery.SectionsOf(mod)) foreach (var d in CfgDiscovery.RowsOf(mod, section)) if (FiresConfigUI.ShowAdvanced || d.Tags == null || !d.Tags.IsAdvanced) n++;
                bool on = mod == _mod && !Searching && _view == "mods";
                var row = Widgets.Node(_nav, "Mod");
                row.anchorMin = new Vector2(0f, 1f); row.anchorMax = new Vector2(1f, 1f); row.pivot = new Vector2(0.5f, 1f);
                row.anchoredPosition = new Vector2(0f, -y); row.sizeDelta = new Vector2(0f, 36f);
                if (on)
                {
                    var sel = Widgets.Box(row, "Sel", KitSprites.RoundedFill(FiresTokens.RadiusSmall), FiresTokens.Selected, true);
                    Widgets.Stretch(sel.rectTransform, 0, 0, 0, 0);
                }
                var hitImg = row.gameObject.AddComponent<Image>(); hitImg.color = new Color(0f, 0f, 0f, 0f);
                var name = KitText.Create(row, "Name", mod, FontRole.SerifRegular, FiresTokens.BodySize, on ? FiresTokens.Gold : FiresTokens.Text, KitAlign.Left, 0f, false, false);
                Widgets.Stretch(name.Rect, 14f, 0, 50f, 0);
                var cnt = KitText.Create(row, "Count", n.ToString(CultureInfo.InvariantCulture), FontRole.SansRegular, FiresTokens.HintSize, FiresTokens.TextFaint, KitAlign.Right, 0f, false, false);
                Widgets.Stretch(cnt.Rect, 0, 0, 10f, 0);
                string m = mod;
                ShellWidgets.Clickable(row, () => { _mod = m; _query = ""; if (_search != null) _search.SetTextWithoutNotify(""); _view = "mods"; _ctx.SelectSubTab("mods"); _scroll.verticalNormalizedPosition = 1f; Rebuild(); });
                y += 38f;
            }
            _nav.sizeDelta = new Vector2(0f, y);   // the content height the nav ScrollRect clips + scrolls
        }

        /// <summary>test hook: opens the setting's
        /// mod, expands its section and scrolls it into view. True once the row is built (its controls exist). Read by FDT by reflection.</summary>
        public static bool TestScrollTo(string modName, string key)
        {
            var m = s_live;
            if (m == null) return false;
            CfgDescriptor d = null;
            foreach (var x in CfgDiscovery.Descriptors) if (x.ModName == modName && x.Key == key) { d = x; break; }
            if (d == null) return false;
            if (!FiresConfigUI.ShowAdvanced && d.Tags != null && d.Tags.IsAdvanced) return false;
            m._mod = modName; m._query = ""; m._view = "mods";
            if (m._search != null) m._search.SetTextWithoutNotify("");
            m._collapsed[modName + "|" + d.Section] = false;
            m.Rebuild();
            for (int i = 0; i < m._items.Count; i++)
            {
                if (m._items[i].Row != d) continue;
                float top = Mathf.Max(0f, m._items[i].Y - 8f);
                m._list.anchoredPosition = new Vector2(m._list.anchoredPosition.x, top);
                m.BuildVisible(true);
                bool built = m._built.ContainsKey(i) && m._built[i] != null;
                ShellLog.Info("config module: test scroll to " + modName + " / " + key + " (row " + i + ", " + (built ? "built" : "NOT built") + ", control " + d.Kind + ")");
                return built;
            }
            return false;
        }

        // ---- virtualised rows ------------------------------------------------------------------------------

        internal void BuildVisible(bool force = false)
        {
            if (_list == null || _viewport == null) return;
            float top = _list.anchoredPosition.y, bottom = top + _viewport.rect.height;
            int first = -1, last = -1;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.Y + it.H < top - RowH) continue;
                if (it.Y > bottom + RowH) break;
                if (first < 0) first = i;
                last = i;
            }
            if (!force && first == _firstBuilt && last == _lastBuilt) return;
            var keep = new HashSet<int>();
            for (int i = first; i >= 0 && i <= last; i++) keep.Add(i);
            var drop = new List<int>();
            foreach (var kv in _built) if (force || !keep.Contains(kv.Key)) drop.Add(kv.Key);
            foreach (int i in drop) { if (_built[i] != null) UnityEngine.Object.Destroy(_built[i].gameObject); _built.Remove(i); }
            for (int i = first; i >= 0 && i <= last; i++) if (!_built.ContainsKey(i)) _built[i] = BuildItem(i);
            _firstBuilt = first; _lastBuilt = last;
        }

        private RectTransform BuildItem(int index)
        {
            var it = _items[index];
            var rt = Widgets.Node(_list, it.Header ? "Section" : "Row");
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -it.Y); rt.sizeDelta = new Vector2(-12f, it.H);
            if (it.Header) { BuildHeader(rt, index, it); return rt; }
            BuildRow(rt, it.Row);
            return rt;
        }

        private void BuildHeader(RectTransform rt, int index, Item it)
        {
            _headerKeys.TryGetValue(index, out string key);
            bool collapsed = key != null && !Searching && (!_collapsed.TryGetValue(key, out bool c) || c);   // collapsed by default; the player's choice kept for the session
            var l = KitText.Create(rt, "Title", it.Section.ToUpperInvariant() + "   <size=80%><color=#B3A181>" + it.Count + " settings</color></size>",
                FontRole.SansBold, SectionHeaderSize, FiresTokens.Gold, KitAlign.Left, 1.5f, false, false);   // was 14, smaller than the 18 row names under it
            Widgets.Stretch(l.Rect, 0, 0, 40f, 0);
            var g = KitText.Create(rt, "Fold", collapsed ? "+" : "-", FontRole.SansBold, 16f, FiresTokens.GoldDim, KitAlign.Right, 0f, false, false);
            Widgets.Stretch(g.Rect, 0, 0, 6f, 0);
            Widgets.Hairline(rt, "Rule", 0, it.H - 1f, 2000f, FiresTokens.Hairline);
            var hitImg = rt.gameObject.AddComponent<Image>(); hitImg.color = new Color(0f, 0f, 0f, 0f);
            if (key != null) ShellWidgets.Clickable(rt, () => { _collapsed[key] = !collapsed; Rebuild(); });
        }

        private void BuildRow(RectTransform rt, CfgDescriptor d)
        {
            bool locked = d.IsLocked, changed = IsChanged(d);
            var name = KitText.Create(rt, "Name", d.Label, FontRole.SerifRegular, RowNameSize,   // below the section header
                locked ? FiresTokens.TextFaint : changed ? FiresTokens.Gold : FiresTokens.Text, KitAlign.Left, 0f, false, false);
            Place(name.Rect, 0f, 6f, -ControlW - ResetW - 16f, 22f);
            // the whole description wraps to two lines (was the first line, cut off at the row edge); hover shows it all.
            string full = (d.Description ?? d.DescriptionLine ?? "").Replace("\r", "").Replace("\n", " ").Trim();
            var desc = KitText.Create(rt, "Desc", full, FontRole.SerifRegular, FiresTokens.DescSize, FiresTokens.TextMuted, KitAlign.Left, 0f, false, true);
            if (desc.Tmp != null) { desc.Tmp.overflowMode = TextOverflowModes.Ellipsis; desc.Tmp.maxVisibleLines = 2; }
            else if (desc.Legacy != null) desc.Legacy.verticalOverflow = VerticalWrapMode.Truncate;
            Place(desc.Rect, 0f, 27f, -ControlW - ResetW - 16f, DescH);
            string hover = string.IsNullOrEmpty(d.LongDescription) ? full : d.LongDescription.Replace("\r", "").Trim();   // the full original text
            string def = locked ? d.Tags.LockText() : "Default: " + Show(d.Default, d);
            var df = KitText.Create(rt, "Default", def, FontRole.SansRegular, FiresTokens.HintSize - 1f, locked ? FiresTokens.Warn : FiresTokens.TextFaint, KitAlign.Left, 0f, false, false);
            Place(df.Rect, 0f, 27f + DescH + 2f, -ControlW - ResetW - 16f, 16f);

            var area = Widgets.Node(rt, "Control");
            area.anchorMin = new Vector2(1f, 0f); area.anchorMax = new Vector2(1f, 1f); area.pivot = new Vector2(1f, 0.5f);
            area.anchoredPosition = new Vector2(-ResetW, 0f); area.sizeDelta = new Vector2(ControlW, 0f);
            try { BuildControl(area, d, locked); }
            catch (Exception e) { ShellLog.Warn("config module: control for " + d.ModName + " / " + d.Key + " failed: " + e.Message); }
            if (locked) foreach (var gr in area.GetComponentsInChildren<Graphic>(true)) gr.raycastTarget = false;

            if (changed && !locked && (d.Tags == null || !d.Tags.HideDefaultButton))
            {
                var rs = KitText.Create(rt, "Reset", FiresTokens.GlyphReset, FontRole.SansRegular, 18f, FiresTokens.GoldDim, KitAlign.Center, 0f, false, false);
                var rr = rs.Rect; rr.anchorMin = new Vector2(1f, 0f); rr.anchorMax = new Vector2(1f, 1f); rr.pivot = new Vector2(1f, 0.5f);
                rr.anchoredPosition = Vector2.zero; rr.sizeDelta = new Vector2(ResetW, 0f);
                rs.Graphic.raycastTarget = true;
                ShellWidgets.Clickable(rr, () => Set(d, d.Default, "reset to default"));
            }
            Widgets.Hairline(rt, "Rule", 0, RowH - 1f, 2000f, FiresTokens.Hairline);
        }

        private static void Place(RectTransform r, float x, float y, float rightInset, float h)
        {
            r.anchorMin = new Vector2(0f, 1f); r.anchorMax = new Vector2(1f, 1f); r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(rightInset, h);
        }

        // ---- controls ----------------------------------------------------------------------------------------

        private void BuildControl(RectTransform area, CfgDescriptor d, bool locked)
        {
            object v = d.Entry.BoxedValue;
            float cy = (RowH - 26f) * 0.5f;
            switch (d.Kind)
            {
                case CtrlKind.Bool:
                {
                    bool on = v is bool b && b;
                    var t = Widgets.DiamondToggle(area, "t", on ? "ON" : "OFF", on, 0f, cy, 26f, true);
                    if (!locked) ShellWidgets.Clickable(t, () => Set(d, !on, "toggle"));
                    return;
                }
                case CtrlKind.Enum:
                case CtrlKind.ValueList:
                {
                    var opts = d.Options ?? new string[0];
                    int sel = Array.IndexOf(d.OptionValues ?? new object[0], v);
                    if (sel < 0) sel = Array.IndexOf(opts, Convert.ToString(v, CultureInfo.InvariantCulture));
                    if (opts.Length > 0 && opts.Length <= 4)
                    {
                        var seg = Widgets.Segmented(area, "s", opts, sel, 0f, (RowH - FiresTokens.SegmentHeight) * 0.5f, FiresTokens.SegmentHeight, true);
                        var leaves = seg.GetComponentsInChildren<KitLeaf>(true);
                        for (int i = 0; i < leaves.Length && i < opts.Length; i++)
                        {
                            int k = i;
                            var lr = (RectTransform)leaves[i].transform;
                            var img = lr.gameObject.GetComponent<Image>() ?? lr.gameObject.AddComponent<Image>(); img.color = img.sprite == null ? new Color(0f, 0f, 0f, 0f) : img.color;
                            if (!locked) ShellWidgets.Clickable(lr, () => Set(d, d.OptionValues != null && k < d.OptionValues.Length ? d.OptionValues[k] : (object)opts[k], "option"));
                        }
                    }
                    else
                    {
                        string cur = sel >= 0 && sel < opts.Length ? opts[sel] : Convert.ToString(v, CultureInfo.InvariantCulture);
                        var btn = Widgets.Button(area, "cycle", cur + "  v", ButtonKind.Primary, 0f, cy, 180f, 30f);
                        PinRight(btn);
                        if (!locked && opts.Length > 0)
                            ShellWidgets.Clickable(btn, () => { int n = (sel + 1) % opts.Length; Set(d, d.OptionValues != null && n < d.OptionValues.Length ? d.OptionValues[n] : (object)opts[n], "next option"); });
                    }
                    return;
                }
                case CtrlKind.IntRange:
                case CtrlKind.FloatRange:
                {
                    double val = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                    float t01 = d.Max > d.Min ? (float)((val - d.Min) / (d.Max - d.Min)) : 0f;
                    string text = d.Kind == CtrlKind.IntRange ? ((long)Math.Round(val)).ToString(CultureInfo.InvariantCulture) : val.ToString("0.##", CultureInfo.InvariantCulture);
                    var sl = Widgets.Slider(area, "sl", t01, text, 0f, (RowH - 26f) * 0.5f, ControlW, 26f);
                    if (!locked)
                    {
                        var drag = sl.gameObject.AddComponent<ShellSliderDrag>();
                        var hit = sl.gameObject.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f);
                        drag.TrackWidth = ControlW - 52f - FiresTokens.S12;
                        drag.OnValue = f01 =>
                        {
                            double nv = d.Min + (d.Max - d.Min) * f01;
                            if (d.Step > 0) nv = Math.Round(nv / d.Step) * d.Step;
                            object boxed = d.Kind == CtrlKind.IntRange ? Convert.ChangeType(Math.Round(nv), d.Type, CultureInfo.InvariantCulture) : Convert.ChangeType(nv, d.Type, CultureInfo.InvariantCulture);
                            Set(d, boxed, "slider", rebuild: false);
                        };
                        drag.OnEnd = () => BuildVisible(true);
                        // click the NUMBER to type a value; Enter / leaving the field applies it (clamped to the range,
                        // rounded to the step), Esc cancels. The text-capture gate is held only while this field is focused.
                        var areaRt = area; var rowD = d; string curText = text;
                        drag.OnNumber = () => BeginTyped(areaRt, rowD, curText);
                    }
                    return;
                }
                case CtrlKind.KeyBind:
                {
                    bool cap = _capturing == d;
                    var well = Widgets.Well(area, "key", ControlW - 150f - 110f, (RowH - 32f) * 0.5f, 150f, 32f, cap ? FiresTokens.Gold : (Color?)null);
                    var kl = KitText.Create(well, "L", cap ? "press keys..." : Show(v, d), FontRole.SansBold, 14f, cap ? FiresTokens.GoldBright : FiresTokens.Text, KitAlign.Center, 0f, false, false);
                    Widgets.Stretch(kl.Rect, 0, 0, 0, 0);
                    var set = Widgets.Button(area, "set", cap ? "Cancel" : "Set", ButtonKind.Primary, ControlW - 104f, (RowH - 30f) * 0.5f, 60f, 30f);
                    var clr = Widgets.Button(area, "clear", "×", ButtonKind.Primary, ControlW - 38f, (RowH - 30f) * 0.5f, 34f, 30f);
                    if (!locked)
                    {
                        ShellWidgets.Clickable(set, () => { _capturing = cap ? null : d; BuildVisible(true); });
                        ShellWidgets.Clickable(clr, () => Set(d, NoneKey(d), "unbind"));
                    }
                    return;
                }
                default:
                {
                    var input = MakeInput(area, "", null);
                    var ir = (RectTransform)input.transform;
                    ir.anchorMin = new Vector2(0f, 0.5f); ir.anchorMax = new Vector2(1f, 0.5f); ir.pivot = new Vector2(0.5f, 0.5f);
                    ir.anchoredPosition = Vector2.zero; ir.sizeDelta = new Vector2(0f, 32f);
                    input.SetTextWithoutNotify(Serialized(d));
                    input.interactable = !locked;
                    input.onEndEdit.AddListener(text =>
                    {
                        try { Set(d, TomlTypeConverter.ConvertToValue(text, d.Type), "typed"); }
                        catch (Exception e) { ShellLog.Debug("config module: '" + text + "' is not a valid " + d.Type.Name + " for " + d.Key + " (" + e.Message + ")"); BuildVisible(true); }
                    });
                    return;
                }
            }
        }

        private static void PinRight(RectTransform r)
        {
            r.anchorMin = r.anchorMax = new Vector2(1f, 0.5f); r.pivot = new Vector2(1f, 0.5f); r.anchoredPosition = Vector2.zero;
        }

        private static object NoneKey(CfgDescriptor d)
        {
            if (d.Type == typeof(KeyboardShortcut)) return KeyboardShortcut.Empty;
            if (d.Type == typeof(KeyCode)) return KeyCode.None;
            return "";
        }

        private static string Serialized(CfgDescriptor d)
        {
            try { return d.Entry.GetSerializedValue(); } catch { return Convert.ToString(d.Entry.BoxedValue, CultureInfo.InvariantCulture); }
        }

        private static string Show(object v, CfgDescriptor d)
        {
            if (v == null) return "-";
            if (v is KeyboardShortcut ks) return ks.MainKey == KeyCode.None ? "None" : ks.ToString();
            if (v is float f) return f.ToString("0.##", CultureInfo.InvariantCulture);
            if (v is double db) return db.ToString("0.##", CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "On" : "Off";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private void Set(CfgDescriptor d, object value, string why, bool rebuild = true)
        {
            if (d.IsLocked) return;
            bool ok = CfgDiscovery.Write(d.Entry, value, "hub settings (" + why + ")");
            // the point-of-use proof line at Info.
            ShellLog.Info("config module: set " + d.ModName + "/" + d.Key + " = " + Show(d.Entry.BoxedValue, d) + " (" + why + ")" + (ok ? "" : " - REFUSED"));
            if (rebuild) { _titleNote.Value = ""; Rebuild(); }
        }

        // Key capture (raw Input: the hub's input block does not swallow it), driven per frame by ShellConfigDriver.
        internal void Tick()
        {
            if (_capturing == null) return;
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) return;   // OnEscape cancels
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            {
                if (k == KeyCode.None || k == KeyCode.Escape || k == KeyCode.Mouse0) continue;
                if (IsModifier(k)) continue;
                if (!UnityEngine.Input.GetKeyDown(k)) continue;
                var mods = new List<KeyCode>();
                foreach (var m in new[] { KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftAlt, KeyCode.RightAlt })
                    if (UnityEngine.Input.GetKey(m)) mods.Add(m);
                Capture(k, mods.ToArray());
                return;
            }
        }

        // ---- test hook --------------------------
        private static ShellConfigModule s_live;

        /// <summary>Test hook: arms capture on the setting (mod display name + key) and feeds it one key, through the same capture
        /// path a real key press takes (Set -> CfgDiscovery.Write, logged "via hub settings (key capture)"). False when the module is
        /// not built, the setting is unknown / locked / not a keybind. Read by FDT by reflection.</summary>
        public static bool TestInjectKeyCapture(string modName, string key, KeyCode main, KeyCode[] modifiers = null)
        {
            var m = s_live;
            if (m == null) return false;
            CfgDescriptor d = null;
            foreach (var x in CfgDiscovery.Descriptors) if (x.ModName == modName && x.Key == key) { d = x; break; }
            if (d == null || d.IsLocked || d.Kind != CtrlKind.KeyBind) return false;
            m._capturing = d;
            m.Capture(main, modifiers ?? new KeyCode[0]);
            ShellLog.Info("config module: test key capture injected for " + modName + " / " + key + " = " + main);
            return true;
        }

        // test hooks: the same Set path a click / drag / « takes, without depending on the row's object layout.
        private static CfgDescriptor FindRow(string modName, string key)
        {
            foreach (var x in CfgDiscovery.Descriptors) if (x.ModName == modName && x.Key == key) return x;
            return null;
        }

        /// <summary>Test hook: flips a bool setting as a toggle click does (logs "config module: set … (toggle)").</summary>
        public static bool TestClickToggle(string modName, string key)
        {
            var m = s_live; var d = FindRow(modName, key);
            if (m == null || d == null || d.IsLocked || d.Kind != CtrlKind.Bool) return false;
            m.Set(d, !(d.Entry.BoxedValue is bool b && b), "toggle");
            return true;
        }

        /// <summary>Test hook: sets a ranged setting to a 0..1 position along its slider, as a drag does (logs "(slider)").</summary>
        public static bool TestSetSlider(string modName, string key, float value01)
        {
            var m = s_live; var d = FindRow(modName, key);
            if (m == null || d == null || d.IsLocked || (d.Kind != CtrlKind.IntRange && d.Kind != CtrlKind.FloatRange)) return false;
            double nv = d.Min + (d.Max - d.Min) * Mathf.Clamp01(value01);
            if (d.Step > 0) nv = Math.Round(nv / d.Step) * d.Step;
            object boxed = d.Kind == CtrlKind.IntRange ? Convert.ChangeType(Math.Round(nv), d.Type, CultureInfo.InvariantCulture) : Convert.ChangeType(nv, d.Type, CultureInfo.InvariantCulture);
            m.Set(d, boxed, "slider");
            return true;
        }

        /// <summary>Test hook: resets a setting to its default, as the « does (logs "(reset to default)").</summary>
        public static bool TestReset(string modName, string key)
        {
            var m = s_live; var d = FindRow(modName, key);
            if (m == null || d == null || d.IsLocked || d.Default == null) return false;
            m.Set(d, d.Default, "reset to default");
            return true;
        }

        private void Capture(KeyCode k, KeyCode[] mods)
        {
            var d = _capturing; _capturing = null;
            if (d == null) return;
            object value = d.Type == typeof(KeyCode) ? (object)k : d.Type == typeof(KeyboardShortcut) ? new KeyboardShortcut(k, mods) : (object)new KeyboardShortcut(k, mods).ToString();
            Set(d, value, "key capture");
        }

        private static bool IsModifier(KeyCode k) => k == KeyCode.LeftControl || k == KeyCode.RightControl || k == KeyCode.LeftShift || k == KeyCode.RightShift || k == KeyCode.LeftAlt || k == KeyCode.RightAlt;

        // ---- typed slider value ----------------------------------------------------------------------------
        private TMP_InputField _typing;
        private CfgDescriptor _typingRow;
        private bool _typingDone;

        private void BeginTyped(RectTransform area, CfgDescriptor d, string current)
        {
            if (_typing != null) CancelTyped();
            var input = MakeInput(area, "", null);
            var rt = (RectTransform)input.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f); rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(ControlW - 64f, 0f); rt.sizeDelta = new Vector2(64f, 28f);
            input.text = current;
            input.contentType = d.Kind == CtrlKind.IntRange ? TMP_InputField.ContentType.IntegerNumber : TMP_InputField.ContentType.DecimalNumber;
            _typing = input; _typingRow = d; _typingDone = false;
            input.onEndEdit.AddListener(s => { if (input.wasCanceled) CancelTyped(); else ApplyTyped(s); });
            input.Select(); input.ActivateInputField();
            ShellLog.Debug($"settings: typing a value for {d.Key} (was {current})");
        }

        private void ApplyTyped(string s)
        {
            if (_typingDone || _typingRow == null) return;
            _typingDone = true;
            var d = _typingRow;
            if (double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double nv))
            {
                nv = Math.Max(d.Min, Math.Min(d.Max, nv));
                if (d.Step > 0) nv = Math.Round(nv / d.Step) * d.Step;
                object boxed = d.Kind == CtrlKind.IntRange ? Convert.ChangeType(Math.Round(nv), d.Type, CultureInfo.InvariantCulture) : Convert.ChangeType(nv, d.Type, CultureInfo.InvariantCulture);
                Set(d, boxed, "typed", rebuild: false);
                ShellLog.Debug($"settings: {d.Key} typed '{s}' -> {Convert.ToString(boxed, CultureInfo.InvariantCulture)}");
            }
            else ShellLog.Debug($"settings: {d.Key} typed '{s}' is not a number; unchanged");
            EndTyped();
        }

        private void CancelTyped()
        {
            if (_typingRow != null) ShellLog.Debug($"settings: typing {_typingRow.Key} cancelled");
            _typingDone = true;
            EndTyped();
        }

        private void EndTyped()
        {
            var t = _typing; _typing = null; _typingRow = null;
            if (t != null) { try { t.DeactivateInputField(); } catch { } UnityEngine.Object.Destroy(t.gameObject); }   // blur: the gate's auto token goes with it
            BuildVisible(true);
        }

        // ---- input field (TMP) ---------------------------------------------------------------------------------

        internal static TMP_InputField MakeInput(RectTransform parent, string placeholder, Action<string> onChange)
        {
            var root = Widgets.Well(parent, "Input", 0f, 0f, 100f, 32f);
            var area = Widgets.Node(root, "TextArea");
            Widgets.Stretch(area, 10f, 4f, 10f, 4f);
            area.gameObject.AddComponent<RectMask2D>();
            var text = KitText.Create(area, "Text", "", FontRole.SansRegular, 14f, FiresTokens.Text, KitAlign.Left, 0f, false, false);
            Widgets.Stretch(text.Rect, 0, 0, 0, 0);
            var ph = KitText.Create(area, "Placeholder", placeholder, FontRole.SansRegular, 14f, FiresTokens.TextFaint, KitAlign.Left, 0f, false, false);
            Widgets.Stretch(ph.Rect, 0, 0, 0, 0);
            var input = root.gameObject.AddComponent<TMP_InputField>();
            input.textViewport = area;
            input.textComponent = text.Tmp;
            input.placeholder = ph.Graphic;
            input.fontAsset = text.Tmp != null ? text.Tmp.font : null;
            input.caretColor = FiresTokens.Gold;
            // TMP_InputField builds its "Caret" object in OnEnable, and only when
            // textComponent is already set. AddComponent ran OnEnable with no textComponent, so no caret ever existed. Re-enable after
            // wiring so OnEnable runs again and creates it; the gold colour is opted in (customCaretColor) and 2 px wide.
            input.customCaretColor = true;
            input.caretWidth = 2;
            input.caretBlinkRate = 0.85f;
            input.enabled = false;
            input.enabled = true;
            var bg = root.GetComponentInChildren<Image>();
            if (bg != null) input.targetGraphic = bg;
            if (onChange != null) input.onValueChanged.AddListener(s => onChange(s));
            return input;
        }

        // a rounded kit scrollbar with a draggable thumb, inside the right
        // edge of a ScrollRect's viewport. Hidden while everything fits (AutoHide), so short lists stay clean.
        internal const float BarWidth = 8f;
        internal static Scrollbar AddScrollbar(ScrollRect scroll, RectTransform viewport, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(viewport.parent, false);
            rt.anchorMin = viewport.anchorMin; rt.anchorMax = viewport.anchorMax; rt.pivot = new Vector2(1f, 1f);
            // same vertical span as the viewport, pinned to its right edge
            rt.offsetMin = new Vector2(viewport.offsetMax.x - BarWidth + (viewport.anchorMax.x == viewport.anchorMin.x ? viewport.sizeDelta.x : 0f), viewport.offsetMin.y);
            rt.offsetMax = viewport.offsetMax;
            if (viewport.anchorMax.x == viewport.anchorMin.x)
            {   // a fixed-width viewport (the nav): sit just inside its right edge
                rt.anchorMin = new Vector2(viewport.anchorMin.x, viewport.anchorMin.y); rt.anchorMax = new Vector2(viewport.anchorMin.x, viewport.anchorMax.y);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = new Vector2(viewport.anchoredPosition.x + viewport.sizeDelta.x, viewport.anchoredPosition.y);
                rt.sizeDelta = new Vector2(BarWidth, viewport.sizeDelta.y);
            }
            else
            {
                rt.anchorMin = new Vector2(1f, viewport.anchorMin.y); rt.anchorMax = new Vector2(1f, viewport.anchorMax.y);
                rt.pivot = new Vector2(1f, 1f);
                rt.offsetMin = new Vector2(-BarWidth, viewport.offsetMin.y);
                rt.offsetMax = new Vector2(0f, viewport.offsetMax.y);
            }
            var track = go.AddComponent<Image>();
            track.sprite = KitSprites.RoundedFill(BarWidth * 0.5f); track.type = Image.Type.Sliced;
            track.color = FiresTokens.WithAlpha(FiresTokens.Hairline, 0.6f);
            var area = Widgets.Node(rt, "SlidingArea");
            Widgets.Stretch(area, 0, 0, 0, 0);
            var handle = Widgets.Node(area, "Handle");
            handle.anchorMin = Vector2.zero; handle.anchorMax = Vector2.one; handle.offsetMin = Vector2.zero; handle.offsetMax = Vector2.zero;
            var thumb = handle.gameObject.AddComponent<Image>();
            thumb.sprite = KitSprites.RoundedFill(BarWidth * 0.5f); thumb.type = Image.Type.Sliced;
            thumb.color = FiresTokens.GoldDim;
            var sb = go.AddComponent<Scrollbar>();
            sb.handleRect = handle; sb.targetGraphic = thumb; sb.direction = Scrollbar.Direction.BottomToTop;
            var colors = sb.colors; colors.normalColor = Color.white; colors.highlightedColor = new Color(1.25f, 1.15f, 0.9f, 1f); colors.pressedColor = new Color(1.4f, 1.25f, 0.9f, 1f); sb.colors = colors;
            scroll.verticalScrollbar = sb;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scroll.verticalScrollbarSpacing = 2f;
            ShellLog.Info("config module: scrollbar '" + name + "' added (rounded kit, draggable thumb, auto-hide when everything fits)");
            return sb;
        }

        internal bool FocusSearch()
        {
            if (_search == null) return false;
            _search.Select(); _search.ActivateInputField();
            return true;
        }
    }

    // Per-frame hook for the module (key capture, Ctrl+F), alive only while the hub's content is.
    internal sealed class ShellConfigDriver : MonoBehaviour
    {
        internal ShellConfigModule Module;
        private void Update()
        {
            if (Module == null) return;
            long t0 = ShellPerfProbe.Now();
            try
            {
                Module.Tick();
                if ((UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl)) && UnityEngine.Input.GetKeyDown(KeyCode.F)) Module.FocusSearch();
            }
            catch (Exception e) { ShellLog.Warn("config module tick threw " + e.GetType().Name + ": " + e.Message); }
            ShellPerfProbe.Add(ShellPerfProbe.Slot.ConfigDriver, t0);
        }
    }

    // A slider track you can click or drag: maps the pointer's x on the track to 0..1.
    internal sealed class ShellSliderDrag : MonoBehaviour, IPointerDownHandler, IDragHandler, IEndDragHandler, IPointerUpHandler
    {
        internal float TrackWidth = 200f;
        internal Action<float> OnValue;
        internal Action OnEnd;
        internal Action OnNumber;   // a press on the number (right of the track) types a value instead
        private bool _numberPress;
        public void OnPointerDown(PointerEventData e)
        {
            _numberPress = OnNumber != null && LocalX(e) > TrackWidth + 4f;
            if (_numberPress) { OnNumber(); return; }
            Apply(e);
        }
        public void OnDrag(PointerEventData e) { if (!_numberPress) Apply(e); }
        public void OnEndDrag(PointerEventData e) { if (!_numberPress) OnEnd?.Invoke(); }
        public void OnPointerUp(PointerEventData e) { if (!_numberPress) OnEnd?.Invoke(); _numberPress = false; }
        private float LocalX(PointerEventData e)
        {
            var rt = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out Vector2 local)) return -1f;
            return local.x + rt.rect.width * rt.pivot.x;   // from the left edge
        }
        private void Apply(PointerEventData e)
        {
            float x = LocalX(e);
            if (x < 0f) return;
            OnValue?.Invoke(Mathf.Clamp01(x / Mathf.Max(1f, TrackWidth)));
        }
    }
}
