// Fires UI Shell, Core: the DEVBAR (spec + mocks: Tools\golden\ui_overhaul\devbar\BUILD_SPEC.md, INVENTORY.md).
// The hub's top-left pill area shows the PINNED dev toggles (default: Devcommands, Debug mode, No cost, God, the four pills the hub
// had before). They flow left to right and wrap, up to 4 rows; what doesn't fit folds into "+N", and "v" opens the dropdown: every
// vanilla dev toggle, the ServerDevcommands keys (admins, when SDC is loaded) and the world controls (time of day, weather, wind),
// each with a pin. Everything runs through the vanilla console commands (the server's rules apply) or SDC's own config entries;
// nothing is forced. Pins are saved per character at <BepInEx>\cache\FiresUnifiedCore\devbar\pins_<playerId>.json.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    internal static class Devbar
    {
        private const float AreaW = 760f, PillH = 26f, Gap = 8f, RowPitch = 32f;
        private const int MaxRows = 4;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal enum Group { Vanilla, Sdc, World }

        internal sealed class Item
        {
            public string Id, Label, Cmd;
            public Group Group;
            public Func<bool> On;
            public Func<string> PillText;        // null = Label
            public Action Toggle;                // null = display only
            public bool Server;                  // SDC: changes the whole server
            public bool NeedsPlayer;
        }

        // ---- items --------------------------------------------------------------------------------------------
        private static List<Item> s_items;
        private static FieldInfo s_freeFly;

        internal static List<Item> Items()
        {
            if (s_items != null) return s_items;
            s_items = new List<Item>();
            void V(string id, string label, string cmd, Func<bool> on, bool needsPlayer = false)
                => s_items.Add(new Item { Id = id, Label = label, Cmd = cmd, Group = Group.Vanilla, On = on, NeedsPlayer = needsPlayer, Toggle = () => Run(cmd) });
            V("devcommands", "Devcommands", "devcommands", () => Terminal.m_cheat);
            V("debug", "Debug mode", "debugmode", () => Player.m_debugMode);
            V("god", "God", "god", () => P() != null && Safe(() => P().InGodMode()), true);
            V("ghost", "Ghost", "ghost", () => P() != null && Safe(() => P().InGhostMode()), true);
            V("nocost", "No cost", "nocost", () => P() != null && Safe(() => P().NoCostCheat()), true);
            V("fly", "Fly", "fly", () => P() != null && Safe(() => P().InDebugFlyMode()), true);
            V("freefly", "Free fly", "freefly", FreeFly);
            V("nospawn", "No spawn", "nospawn", () => SpawnSystem.m_nospawn);
            V("nomap", "No map", "nomap", () => ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey("nomap"));
            V("noportals", "No portals", "noportals", () => ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey("noportals"));

            // world (admin; vanilla commands)
            s_items.Add(new Item { Id = "tod", Label = "Time of day", Cmd = "tod", Group = Group.World,
                On = () => EnvMan.instance != null && EnvMan.instance.m_debugTimeOfDay, PillText = TodText });
            s_items.Add(new Item { Id = "weather", Label = "Weather", Cmd = "env", Group = Group.World,
                On = () => EnvMan.instance != null && !string.IsNullOrEmpty(EnvMan.instance.m_debugEnv), PillText = WeatherText });
            s_items.Add(new Item { Id = "wind", Label = "Wind", Cmd = "wind", Group = Group.World,
                On = () => EnvMan.instance != null && EnvMan.instance.m_debugWind, PillText = WindText });

            AddSdc();
            return s_items;
        }

        private static Player P() => Player.m_localPlayer;
        private static bool Safe(Func<bool> f) { try { return f(); } catch { return false; } }

        private static bool FreeFly()
        {
            var gc = GameCamera.instance;
            if (gc == null) return false;
            if (s_freeFly == null) s_freeFly = typeof(GameCamera).GetField("m_freeFly", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            try { return s_freeFly != null && (bool)s_freeFly.GetValue(gc); } catch { return false; }
        }

        private static string TodText()
        {
            var em = EnvMan.instance;
            if (em == null) return "TOD -";
            float f = em.m_debugTimeOfDay ? em.m_debugTime : em.GetDayFraction();
            int mins = Mathf.FloorToInt(f * 24f * 60f);
            if (!em.m_debugTimeOfDay) mins = mins / 10 * 10;   // the running clock in 10-minute steps: the hub chrome rebuilds when a pill's text changes
            string t = (mins / 60).ToString("00", Inv) + ":" + (mins % 60).ToString("00", Inv);
            return em.m_debugTimeOfDay ? "TOD " + t : "TOD " + t + " (auto)";
        }

        private static string WeatherText()
        {
            var em = EnvMan.instance;
            if (em == null) return "Weather";
            if (!string.IsNullOrEmpty(em.m_debugEnv)) return PlatePills.Pretty(em.m_debugEnv);
            var cur = em.GetCurrentEnvironment();
            return cur != null ? PlatePills.Pretty(cur.m_name) + " (auto)" : "Weather";
        }

        private static string WindText()
        {
            var em = EnvMan.instance;
            if (em == null) return "Wind";
            float v = em.m_debugWind ? em.m_debugWindIntensity : em.GetWindIntensity();
            return "Wind " + Mathf.RoundToInt(Mathf.Clamp01(v) * 10f) + (em.m_debugWind ? "" : " (auto)");
        }

        // ServerDevcommands (soft dependency): its ConfigEntry<bool> settings, found by their config key, labelled with the approved pills.
        private static readonly (string key, string pill, bool server)[] SdcKeys =
        {
            ("Automatic devcommands", "Auto dev", false), ("No clip with fly mode", "No clip", false), ("Access warded areas", "Wards", false),
            ("Access private chests", "Private chests", false), ("Automatic item pick up", "Auto pickup", false),
            ("No stamina usage with god mode", "God: stamina", false), ("No weight limit with god mode", "God: weight", false),
            ("No knockback with god mode", "God: knockback", false), ("Invisible to other players with ghost mode", "Ghost: invisible", false),
            ("No creature drops", "No drops", false), ("Disable random events", "No events", true), ("Show map coordinates", "Map coords", false),
            ("Automatic debug mode", "Auto debug", false), ("Automatic god mode", "Auto god", false), ("Automatic fly mode", "Auto fly", false),
            ("Automatic ghost mode", "Auto ghost", false), ("Automatic no cost mode", "Auto no cost", false),
            ("Always parry with god mode (when not blocking)", "God: parry", false),
        };
        internal static bool SdcLoaded { get; private set; }

        private static void AddSdc()
        {
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("org.bepinex.plugins.serverdevcommands", out var info) || info.Instance == null) return;
                var cfg = info.Instance.Config;
                var byKey = new Dictionary<string, ConfigEntry<bool>>(StringComparer.Ordinal);
                foreach (var kv in cfg) if (kv.Value is ConfigEntry<bool> b) byKey[kv.Key.Key] = b;
                int found = 0;
                foreach (var (key, pill, server) in SdcKeys)
                {
                    if (!byKey.TryGetValue(key, out var entry)) continue;
                    var e = entry;
                    s_items.Add(new Item { Id = "sdc_" + pill.ToLowerInvariant().Replace(": ", "_").Replace(' ', '_'), Label = pill, Cmd = key, Group = Group.Sdc,
                        Server = server, On = () => e.Value,
                        Toggle = () => { e.Value = !e.Value; ShellLog.Info($"[devbar] devbar: {pill} -> {(e.Value ? "on" : "off")} (via ServerDevcommands config '{key}')"); } });
                    found++;
                }
                SdcLoaded = true;
                ShellLog.Info($"devbar: ServerDevcommands found, {found}/{SdcKeys.Length} settings mapped");
            }
            catch (Exception ex) { ShellLog.Warn("devbar: ServerDevcommands lookup failed (" + ex.Message + "), its group stays hidden"); }
        }

        internal static bool IsAdmin()
        {
            try { var zn = ZNet.instance; return zn != null && (zn.IsServer() || zn.LocalPlayerIsAdminOrHost()); } catch { return false; }
        }

        private static bool Visible(Item it, bool admin) => it.Group == Group.Vanilla || admin;
        private static bool Clickable(Item it, bool admin) => admin && it.Toggle != null && (!it.NeedsPlayer || P() != null);

        private static bool? s_adminLogged;

        internal static void Run(string cmd)
        {
            var console = global::Console.instance;
            if (console == null) { ShellLog.Warn("devbar: no console to run '" + cmd + "'"); return; }
            // vanilla's cheat commands are refused silently while devcommands is off
            if (cmd != "devcommands" && !Terminal.m_cheat) { console.TryRunCommand("devcommands"); ShellLog.Info("devbar: devcommands turned on first (vanilla needs it for '" + cmd + "')"); }
            console.TryRunCommand(cmd);
            ShellLog.Info("devbar: ran '" + cmd + "' (via the vanilla console)");
            var hub = ShellRuntime.Instance?.Hub;
            if (hub != null) hub.PokeChrome();
        }

        // ---- pins (per character) ----------------------------------------------------------------------------
        private static List<string> s_pins;
        private static long s_pinsFor = long.MinValue;
        private static readonly string[] DefaultPins = { "devcommands", "debug", "nocost", "god" };

        private static long PlayerId()
        {
            try { return Game.instance != null && Game.instance.GetPlayerProfile() != null ? Game.instance.GetPlayerProfile().GetPlayerID() : 0L; } catch { return 0L; }
        }

        private static string PinsPath(long id) => Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "devbar", "pins_" + id.ToString(Inv) + ".json");

        internal static List<string> Pins()
        {
            long id = PlayerId();
            if (s_pins != null && s_pinsFor == id) return s_pins;
            s_pinsFor = id; s_pins = new List<string>();
            string path = PinsPath(id);
            try
            {
                if (File.Exists(path))
                {
                    string txt = File.ReadAllText(path);
                    foreach (var part in txt.Trim().TrimStart('[').TrimEnd(']').Split(','))
                    {
                        string s = part.Trim().Trim('"');
                        if (s.Length > 0 && !s_pins.Contains(s)) s_pins.Add(s);
                    }
                    ShellLog.Info($"devbar: pins loaded {path} ({s_pins.Count})");
                }
                else { s_pins.AddRange(DefaultPins); ShellLog.Info("devbar: default pins (Devcommands, Debug mode, No cost, God) for player " + id); }
            }
            catch (Exception e) { s_pins.Clear(); s_pins.AddRange(DefaultPins); ShellLog.Warn("devbar: pins unreadable (" + e.Message + "), using the default"); }
            return s_pins;
        }

        internal static void TogglePin(string id)
        {
            var pins = Pins();
            bool now = !pins.Contains(id);
            if (now) pins.Add(id); else pins.Remove(id);
            try
            {
                string path = PinsPath(PlayerId());
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder("[");
                for (int i = 0; i < pins.Count; i++) sb.Append(i > 0 ? "," : "").Append('"').Append(pins[i].Replace("\"", "")).Append('"');
                File.WriteAllText(path, sb.Append(']').ToString());
                ShellLog.Info($"devbar: {(now ? "pinned" : "unpinned")} {id} ({pins.Count} pinned) -> {path}");
            }
            catch (Exception e) { ShellLog.Warn("devbar: pins not saved (" + e.Message + ")"); }
            ShellRuntime.Instance?.Hub?.PokeChrome(rebuild: true);
        }

        // ---- header pills --------------------------------------------------------------------------------------
        internal static bool Open;
        private static string s_lastFlowSig;

        /// <summary>Draws the pinned pills (then the module's context pills), "+N" and "v" into the header's pill area. Returns the
        /// height the rows take beyond the first one.</summary>
        internal static float DrawPills(RectTransform pillsArea, IList<ShellPill> modulePills)
        {
            if (s_sliderDrag) s_rebuildsDuring++;   // proof for the drag line
            bool admin = IsAdmin();
            // say once whether they are clickable; not an admin = display only
            if (admin != s_adminLogged) { s_adminLogged = admin; ShellLog.Info(admin ? "devbar: pills clickable (admin / host)" : "devbar: pills display only (not an admin on this server: add your id to adminlist.txt)"); }
            var drawn = new List<(string id, string text, bool on, Action click)>();
            var byId = new Dictionary<string, Item>();
            foreach (var it in Items()) byId[it.Id] = it;
            foreach (var id in Pins())
            {
                if (!byId.TryGetValue(id, out var it) || !Visible(it, admin)) continue;   // unknown ids (SDC missing) stay in the file
                bool on = Safe(it.On);
                Action click = null;
                if (Clickable(it, admin)) click = it.Toggle;
                else if (it.Group == Group.World && admin) click = () => SetOpen(true);
                drawn.Add((it.Id, it.PillText != null ? SafeText(it.PillText) : it.Label, on, click));
            }
            if (modulePills != null) foreach (var p in modulePills) drawn.Add((p.Id ?? "pill", p.Label ?? "", p.On, p.OnClick));

            // measure with hidden labels (the pills' own font + tracking), flow, fold into +N on row 4
            s_meas = KitText.Create(pillsArea, "MeasureC", "", FontRole.SansBold, FiresTokens.PillTextSize, Color.clear, KitAlign.Left, 0f, false, false);
            s_measTracked = KitText.Create(pillsArea, "MeasureP", "", FontRole.SansBold, FiresTokens.PillTextSize, Color.clear, KitAlign.Left, 4f, false, false);
            s_meas.Graphic.raycastTarget = false; s_measTracked.Graphic.raycastTarget = false;
            float x = 0f, y = 2f; int row = 0, hidden = 0;
            float vW = MeasureControl("v"), plusW = MeasureControl("+99");
            var placed = new List<(int idx, float x, float y)>();
            for (int i = 0; i < drawn.Count; i++)
            {
                float w = MeasurePill(drawn[i].text);
                if (x > 0f && x + w > AreaW) { row++; x = 0f; y += RowPitch; }
                if (row >= MaxRows - 1)
                {
                    bool last = i == drawn.Count - 1;
                    float need = x + w + Gap + vW + (last ? 0f : Gap + plusW);
                    if (row > MaxRows - 1 || need > AreaW) { hidden = drawn.Count - i; break; }
                }
                placed.Add((i, x, y));
                x += w + Gap;
            }
            foreach (var (idx, px, py) in placed)
            {
                var d = drawn[idx];
                var rt = Widgets.Pill(pillsArea, d.id, d.text, d.on, px, py);
                if (d.click != null) ShellWidgets.Clickable(rt, d.click);
                else foreach (var g in rt.GetComponentsInChildren<Graphic>()) g.raycastTarget = false;
            }
            if (hidden > 0)
            {
                var plus = ControlPill(pillsArea, "more", "+" + hidden, false, x, y);
                ShellWidgets.Clickable(plus, () => SetOpen(true));
                x += plus.sizeDelta.x + Gap;
            }
            var v = ControlPill(pillsArea, "devbar", "v", Open, x, y);
            ShellWidgets.Clickable(v, () => SetOpen(!Open));
            UnityEngine.Object.Destroy(s_meas.Rect.gameObject); UnityEngine.Object.Destroy(s_measTracked.Rect.gameObject);
            int rows = row + 1;
            pillsArea.sizeDelta = new Vector2(AreaW, y + PillH + 2f);
            string sig = drawn.Count + "/" + rows + "/" + hidden;
            if (sig != s_lastFlowSig) { s_lastFlowSig = sig; ShellLog.Info($"devbar: {Pins().Count} pinned, {placed.Count} drawn, {rows} row(s), overflow {hidden}"); }
            return (rows - 1) * RowPitch;
        }

        private static string SafeText(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

        private static float MeasurePill(string text)
        {
            // Widgets.Pill: 24 + label + 14, SansBold PillTextSize, tracking 4
            s_measTracked.Value = text;
            return 24f + Mathf.Ceil(s_measTracked.PreferredWidth) + 2f + 14f;
        }

        private static float MeasureControl(string text) { s_meas.Value = text; return Mathf.Ceil(s_meas.PreferredWidth) + 24f; }
        private static KitLabel s_meas, s_measTracked;   // hidden labels under the pill area, same font + tracking as the pills

        // compact control pill: centred label, Edge outline; open = Selected fill + Gold outline + GoldBright label
        private static RectTransform ControlPill(RectTransform parent, string id, string label, bool open, float x, float y)
        {
            var root = Widgets.PathNode(parent, "Control", id);
            float w = MeasureControl(label);
            Widgets.Place(root, x, y, w, PillH);
            var fill = Widgets.Box(root, "Fill", KitSprites.RoundPill(13f, false), open ? FiresTokens.Selected : FiresTokens.WithAlpha(FiresTokens.Well, FiresTokens.PillWellAlpha));
            Widgets.Stretch(fill.rectTransform, 0, 0, 0, 0);
            var ol = Widgets.Box(root, "Outline", KitSprites.RoundPill(13f, true), open ? FiresTokens.Gold : FiresTokens.Edge);
            Widgets.Stretch(ol.rectTransform, 0, 0, 0, 0);
            var l = KitText.Create(root, "Label", label, FontRole.SansBold, FiresTokens.PillTextSize, open ? FiresTokens.GoldBright : FiresTokens.Gold, KitAlign.Center, 0f, false, false);
            Widgets.Stretch(l.Rect, 0, 0, 0, 0); l.Graphic.raycastTarget = false;
            return root;
        }

        internal static void SetOpen(bool open)
        {
            if (Open == open) return;
            Open = open;
            if (!open) s_envListOpen = false;
            ShellLog.Info("devbar: dropdown " + (open ? "open" : "closed"));
            ShellRuntime.Instance?.Hub?.PokeChrome(rebuild: true);
        }

        /// <summary>A signature of every pinned pill's state + text (the hub rebuilds its chrome when it changes).</summary>
        // ---- slider drags (time of day / wind) --------------------------------------------------------------------------
        private static bool s_sliderDrag; private static int s_sliderTicks, s_rebuildsDuring; private static string s_sigFrozen;
        private static void SliderTick(RectTransform root, float v, string text)
        {
            if (!s_sliderDrag) { s_sigFrozen = SignatureNow(); s_sliderDrag = true; s_sliderTicks = 0; s_rebuildsDuring = 0; }   // frozen BEFORE the first preview write
            s_sliderTicks++;
            if (root == null) return;
            float width = root.rect.width, height = root.rect.height;
            float trackW = width - 52f - FiresTokens.S12, th = FiresTokens.SliderTrack, k = FiresTokens.SliderKnob, cy = height * 0.5f;
            v = Mathf.Clamp01(v);
            var fill = root.Find("Fill") as RectTransform; if (fill != null) Widgets.Place(fill, 0, cy - th * 0.5f, Mathf.Max(th, trackW * v), th);
            var knob = root.Find("Knob") as RectTransform; if (knob != null) Widgets.Place(knob, Mathf.Clamp(trackW * v - k * 0.5f, 0f, trackW - k), cy - k * 0.5f, k, k);
            var val = root.Find("Value");
            if (val != null) { var tmp = val.GetComponent<TMPro.TMP_Text>(); if (tmp != null) tmp.text = text; else { var lt = val.GetComponent<UnityEngine.UI.Text>(); if (lt != null) lt.text = text; } }
        }
        private static void SliderEnd(string cmd)
        {
            if (!s_sliderDrag) return;   // OnEndDrag and OnPointerUp both end a drag: the command runs once
            s_sliderDrag = false; s_sigFrozen = null;
            ShellLog.Debug($"[Devbar] slider drag: {s_sliderTicks} ticks, {s_rebuildsDuring} chrome rebuilds, command on release ('{cmd}')");
            Run(cmd);
        }
        private static string TimeText(float f) { int mins = Mathf.FloorToInt(Mathf.Clamp01(f) * 24f * 60f); return (mins / 60).ToString("00", Inv) + ":" + (mins % 60).ToString("00", Inv); }

        internal static string Signature()
        {
            // frozen while a slider is dragged (its live text would rebuild the whole chrome every tick)
            if (s_sliderDrag && s_sigFrozen != null) return s_sigFrozen;
            return SignatureNow();
        }
        private static string SignatureNow()
        {
            bool admin = IsAdmin();
            var sb = new StringBuilder(admin ? "A" : "-").Append(Open ? 'O' : 'c');
            var byId = new Dictionary<string, Item>();
            foreach (var it in Items()) byId[it.Id] = it;
            foreach (var id in Pins())
                if (byId.TryGetValue(id, out var it) && Visible(it, admin))
                    sb.Append(Safe(it.On) ? '1' : '0').Append(it.PillText != null ? SafeText(it.PillText) : "").Append('|');
            if (Open) foreach (var it in Items()) if (Visible(it, admin)) sb.Append(Safe(it.On) ? '1' : '0');
            return sb.ToString();
        }

        // ---- dropdown ------------------------------------------------------------------------------------------
        private static bool s_envListOpen;   // the weather dropdown's list
        private const float ColW = 420f, RowH = 30f, SdcVisibleRows = 14;

        /// <summary>The dropdown plate on its own overlay layer above the hub content, under the pills.</summary>
        internal static void DrawDropdown(RectTransform content, float top)
        {
            if (!Open) return;
            bool admin = IsAdmin();
            var plate = Widgets.Node(content, "DevbarDropdown");
            plate.SetAsLastSibling();
            var cv = plate.gameObject.AddComponent<Canvas>(); cv.overrideSorting = true; cv.sortingOrder = 32000;
            plate.gameObject.AddComponent<GraphicRaycaster>();
            float width = Mathf.Min(1340f, content.rect.width > 10f ? content.rect.width : 1340f);
            var groups = new List<(string title, List<Item> items)>
            {
                ("Vanilla", Items().FindAll(i => i.Group == Group.Vanilla)),
            };
            if (admin && SdcLoaded) groups.Add(("ServerDevcommands", Items().FindAll(i => i.Group == Group.Sdc)));
            if (admin) groups.Add(("World", null));
            float colH = 0f;
            foreach (var g in groups) colH = Mathf.Max(colH, g.items == null ? 6 * RowH : Mathf.Min(g.items.Count, (int)SdcVisibleRows) * RowH);
            float h = 44f + 34f + colH + 44f;
            Widgets.Place(plate, 0f, top, width, h);
            var fill = Widgets.Box(plate, "Fill", KitSprites.RoundedFill(FiresTokens.Radius), FiresTokens.PanelRaised, true);
            Widgets.Stretch(fill.rectTransform, 0, 0, 0, 0); fill.raycastTarget = true;
            var edge = Widgets.Box(plate, "Edge", KitSprites.RoundedOutline(FiresTokens.Radius), FiresTokens.Edge, true);
            Widgets.Stretch(edge.rectTransform, 0, 0, 0, 0); edge.raycastTarget = false;
            var title = KitText.Create(plate, "Title", "DEVBAR", FontRole.SansBold, FiresTokens.SectionSize, FiresTokens.Gold, KitAlign.Left, FiresTokens.SectionTracking, false, false);
            Widgets.Place(title.Rect, 18f, 12f, 300f, 24f);
            var hint = KitText.Create(plate, "Hint", "Pinned items show as pills in the header (4 rows, then +N)", FontRole.SansRegular, FiresTokens.HintSize, FiresTokens.TextMuted, KitAlign.Right, 0f, false, false);
            Widgets.PlaceRight(hint.Rect, 18f, 12f, 600f, 24f);
            float gx = 18f, gy = 44f;
            float colW = (width - 36f - (groups.Count - 1) * 24f) / Mathf.Max(1, groups.Count);
            colW = Mathf.Min(colW, ColW + 80f);
            foreach (var g in groups)
            {
                var head = KitText.Create(plate, "Group_" + g.title, g.title.ToUpperInvariant(), FontRole.SansBold, 13f, FiresTokens.Gold, KitAlign.Left, FiresTokens.SectionTracking, false, false);
                Widgets.Place(head.Rect, gx, gy, colW, 24f);
                if (g.title == "ServerDevcommands")
                {
                    var tag = KitText.Create(plate, "AdminTag", "ADMIN", FontRole.SansBold, 10f, FiresTokens.Warn, KitAlign.Right, 0f, false, false);
                    Widgets.Place(tag.Rect, gx, gy, colW, 24f);
                }
                if (g.items == null) WorldControls(plate, gx, gy + 34f, colW);
                else ItemColumn(plate, g.title, g.items, gx, gy + 34f, colW, g.title == "ServerDevcommands" ? (int)SdcVisibleRows : 99, admin);
                gx += colW + 24f;
            }
            var foot = KitText.Create(plate, "Footer", "Pins saved for this character", FontRole.SansRegular, FiresTokens.HintSize, FiresTokens.TextFaint, KitAlign.Left, 0f, false, false);
            Widgets.Place(foot.Rect, 18f, h - 34f, 400f, 24f);
            if (admin)
            {
                var av = KitText.Create(plate, "AdminView", "Admin view", FontRole.SansRegular, FiresTokens.HintSize, FiresTokens.TextFaint, KitAlign.Right, 0f, false, false);
                Widgets.PlaceRight(av.Rect, 18f, h - 34f, 200f, 24f);
            }
        }

        private static void ItemColumn(RectTransform plate, string name, List<Item> items, float x, float y, float w, int visibleRows, bool admin)
        {
            RectTransform parent = plate;
            float listH = Mathf.Min(items.Count, visibleRows) * RowH;
            if (items.Count > visibleRows)
            {
                // the SDC group scrolls inside the dropdown (rounded kit scrollbar)
                var vpGo = new GameObject("Scroll_" + name, typeof(RectTransform));
                var vp = (RectTransform)vpGo.transform; vp.SetParent(plate, false);
                Widgets.Place(vp, x, y, w - ShellConfigModule.BarWidth - 4f, listH);
                vpGo.AddComponent<RectMask2D>();
                var hit = vpGo.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f);
                var list = Widgets.Node(vp, "List");
                list.anchorMin = new Vector2(0f, 1f); list.anchorMax = new Vector2(1f, 1f); list.pivot = new Vector2(0.5f, 1f);
                list.anchoredPosition = Vector2.zero; list.sizeDelta = new Vector2(0f, items.Count * RowH);
                var sr = vpGo.AddComponent<ScrollRect>(); sr.viewport = vp; sr.content = list; sr.horizontal = false; sr.scrollSensitivity = 30f; sr.movementType = ScrollRect.MovementType.Clamped;
                ShellConfigModule.AddScrollbar(sr, vp, "DevbarSdcScrollbar");
                parent = list; x = 0f; y = 0f; w -= ShellConfigModule.BarWidth + 4f;
            }
            var pins = Pins();
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var row = Widgets.PathNode(parent, "Row", it.Id);
                Widgets.Place(row, x, y + i * RowH, w, RowH);
                bool pinned = pins.Contains(it.Id);
                // pin toggle: 22 px circle; pinned = Selected + Gold ring + filled Gold diamond, unpinned = Well + Hairline + hollow faint diamond
                var pin = Widgets.Node(row, "Pin"); Widgets.Place(pin, 0f, (RowH - 22f) * 0.5f, 22f, 22f);
                Widgets.Stretch(Widgets.Box(pin, "Fill", KitSprites.Circle(), pinned ? FiresTokens.Selected : FiresTokens.Well, false).rectTransform, 0, 0, 0, 0);
                Widgets.Stretch(Widgets.Box(pin, "Ring", ShellMinimapRing(), pinned ? FiresTokens.Gold : FiresTokens.Hairline, false).rectTransform, 0, 0, 0, 0);
                Widgets.Diamond(pin, "Diamond", 11f, 11f, 8f, pinned ? FiresTokens.Gold : FiresTokens.TextFaint, pinned ? FiresTokens.Gold : Color.clear, pinned);
                string id = it.Id;
                ShellWidgets.Clickable(pin, () => TogglePin(id));
                var label = KitText.Create(row, "Label", it.Label, FontRole.SerifRegular, 15f, FiresTokens.Text, KitAlign.Left, 0f, false, false);
                Widgets.Place(label.Rect, 30f, 0f, w * 0.5f, RowH); label.Graphic.raycastTarget = false;
                if (it.Group == Group.Vanilla)
                {
                    var cmd = KitText.Create(row, "Cmd", it.Cmd, FontRole.SansRegular, 12f, FiresTokens.TextFaint, KitAlign.Left, 0f, false, false);
                    Widgets.Place(cmd.Rect, 30f + Mathf.Ceil(label.PreferredWidth) + 8f, 0f, 120f, RowH); cmd.Graphic.raycastTarget = false;
                }
                if (it.Server)
                {
                    var srv = Widgets.Node(row, "Server");
                    var st = KitText.Create(srv, "L", "SERVER", FontRole.SansBold, 10f, FiresTokens.TextMuted, KitAlign.Center, 0f, false, false);
                    float sw = Mathf.Ceil(st.PreferredWidth) + 12f;
                    Widgets.Place(srv, 30f + Mathf.Ceil(label.PreferredWidth) + 8f, (RowH - 16f) * 0.5f, sw, 16f);
                    Widgets.Stretch(Widgets.Box(srv, "Outline", KitSprites.RoundPill(8f, true), FiresTokens.TextMuted).rectTransform, 0, 0, 0, 0);
                    Widgets.Stretch(st.Rect, 0, 0, 0, 0);
                }
                bool on = Safe(it.On);
                var sw2 = Widgets.DiamondToggle(row, "state", "", on, 0f, 0f, RowH, true);
                if (Clickable(it, admin)) ShellWidgets.Clickable(sw2, it.Toggle);
                else foreach (var g in sw2.GetComponentsInChildren<Graphic>()) g.raycastTarget = false;
            }
        }

        private static Sprite s_ring;
        private static Sprite ShellMinimapRing()
        {
            if (s_ring != null) return s_ring;
            const int N = 64; float c = (N - 1) * 0.5f;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, name = "FiresDevbarRing" };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float r = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float a = Mathf.Clamp01(1f - Mathf.Abs(r - (c - 2.5f)) / 1.6f);
                px[y * N + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
            tex.SetPixels32(px); tex.Apply(false);
            s_ring = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            return s_ring;
        }

        // ---- world controls (time of day / weather / wind; vanilla commands) -----------------------------------------
        private static void WorldControls(RectTransform plate, float x, float y, float w)
        {
            var em = EnvMan.instance;
            foreach (var id in new[] { "tod", "weather", "wind" })
            {
                var it = Items().Find(i => i.Id == id);
                bool pinned = Pins().Contains(id);
                var pinRow = Widgets.PathNode(plate, "WorldPin", id);
                Widgets.Place(pinRow, x, y, 22f, 22f);
                Widgets.Stretch(Widgets.Box(pinRow, "Fill", KitSprites.Circle(), pinned ? FiresTokens.Selected : FiresTokens.Well, false).rectTransform, 0, 0, 0, 0);
                Widgets.Stretch(Widgets.Box(pinRow, "Ring", ShellMinimapRing(), pinned ? FiresTokens.Gold : FiresTokens.Hairline, false).rectTransform, 0, 0, 0, 0);
                Widgets.Diamond(pinRow, "Diamond", 11f, 11f, 8f, pinned ? FiresTokens.Gold : FiresTokens.TextFaint, pinned ? FiresTokens.Gold : Color.clear, pinned);
                string pid = id;
                ShellWidgets.Clickable(pinRow, () => TogglePin(pid));
                var l = KitText.Create(plate, "WorldLabel_" + id, it.Label + ":  " + SafeText(it.PillText), FontRole.SerifRegular, 15f, FiresTokens.Text, KitAlign.Left, 0f, false, false);
                Widgets.Place(l.Rect, x + 30f, y - 2f, w - 30f, 26f);
                y += 30f;
                float ctrlW = w - 30f - 120f;
                if (id == "tod")
                {
                    float f = em != null ? (em.m_debugTimeOfDay ? em.m_debugTime : em.GetDayFraction()) : 0.5f;
                    var sl = Widgets.Slider(plate, "tod", f, TodText().Replace("TOD ", "").Replace(" (auto)", ""), x + 30f, y, ctrlW, 26f);
                    var drag = sl.gameObject.AddComponent<ShellSliderDrag>();
                    var hit = sl.gameObject.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f);
                    drag.TrackWidth = ctrlW - 52f - FiresTokens.S12;
                    float pending = f;
                    // the slider follows the pointer itself (fill, knob, label)
                    // and the time is previewed straight on EnvMan; the pill text is frozen (no chrome rebuild) and the command runs ONCE on release
                    drag.OnValue = v => { pending = v; SliderTick(sl, v, TimeText(v)); if (em != null) { em.m_debugTimeOfDay = true; em.m_debugTime = Mathf.Clamp01(v); } };
                    drag.OnEnd = () => SliderEnd("tod " + Mathf.Clamp01(pending).ToString("0.###", Inv));
                    var auto = Widgets.Button(plate, "todauto", "Auto time", ButtonKind.Primary, x + 30f + ctrlW + 8f, y, 110f, 26f);
                    ShellWidgets.Clickable(auto, () => Run("tod -1"));
                }
                else if (id == "weather")
                {
                    var names = new List<string>();
                    if (em != null) foreach (var e in em.m_environments) if (e != null && !string.IsNullOrEmpty(e.m_name)) names.Add(e.m_name);
                    string cur = em != null && !string.IsNullOrEmpty(em.m_debugEnv) ? em.m_debugEnv : (em?.GetCurrentEnvironment()?.m_name ?? "");
                    // a DROPDOWN of the env names, "Auto" on top (= resetenv)
                    bool overridden = em != null && !string.IsNullOrEmpty(em.m_debugEnv);
                    var box = Widgets.Well(plate, "EnvBox", x + 30f, y, ctrlW, 26f, s_envListOpen ? FiresTokens.Gold : FiresTokens.Hairline);
                    var name = KitText.Create(box, "Value", (overridden ? PlatePills.Pretty(cur) : "Auto") + "   v", FontRole.SansBold, 14f, FiresTokens.Gold, KitAlign.Left, 0f, false, false);
                    Widgets.Stretch(name.Rect, 10f, 0f, 10f, 0f); name.Graphic.raycastTarget = false;
                    ShellWidgets.Clickable(box, () => { s_envListOpen = !s_envListOpen; ShellRuntime.Instance?.Hub?.PokeChrome(rebuild: true); });
                    if (s_envListOpen)
                    {
                        var opts = new List<string> { "" }; opts.AddRange(names);   // "" = Auto
                        const float optH = 24f; int shown = Mathf.Min(opts.Count, 12);
                        var listGo = new GameObject("EnvList", typeof(RectTransform));
                        var vp = (RectTransform)listGo.transform; vp.SetParent(plate, false);
                        Widgets.Place(vp, x + 30f, y + 28f, ctrlW, shown * optH + 6f);
                        var lcv = listGo.AddComponent<Canvas>(); lcv.overrideSorting = true; lcv.sortingOrder = 32010;
                        listGo.AddComponent<GraphicRaycaster>();
                        var bg = Widgets.Box(vp, "Fill", KitSprites.RoundedFill(FiresTokens.RadiusSmall), FiresTokens.PanelRaised, true);
                        Widgets.Stretch(bg.rectTransform, 0, 0, 0, 0);
                        var bo = Widgets.Box(vp, "Edge", KitSprites.RoundedOutline(FiresTokens.RadiusSmall), FiresTokens.Edge, true);
                        Widgets.Stretch(bo.rectTransform, 0, 0, 0, 0); bo.raycastTarget = false;
                        var clip = Widgets.Node(vp, "Clip"); Widgets.Stretch(clip, 3f, 3f, 3f + (opts.Count > shown ? ShellConfigModule.BarWidth + 2f : 0f), 3f);
                        clip.gameObject.AddComponent<RectMask2D>();
                        var hitc = clip.gameObject.AddComponent<Image>(); hitc.color = new Color(0f, 0f, 0f, 0f);
                        var content = Widgets.Node(clip, "Options");
                        content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f); content.pivot = new Vector2(0.5f, 1f);
                        content.anchoredPosition = Vector2.zero; content.sizeDelta = new Vector2(0f, opts.Count * optH);
                        if (opts.Count > shown)
                        {
                            var sr = clip.gameObject.AddComponent<ScrollRect>(); sr.viewport = clip; sr.content = content; sr.horizontal = false; sr.scrollSensitivity = 24f; sr.movementType = ScrollRect.MovementType.Clamped;
                            ShellConfigModule.AddScrollbar(sr, clip, "EnvListScrollbar");
                        }
                        for (int k = 0; k < opts.Count; k++)
                        {
                            string env = opts[k];
                            bool sel = env.Length == 0 ? !overridden : overridden && env == cur;
                            var opt = Widgets.PathNode(content, "Opt", env.Length == 0 ? "auto" : env);
                            opt.anchorMin = new Vector2(0f, 1f); opt.anchorMax = new Vector2(1f, 1f); opt.pivot = new Vector2(0.5f, 1f);
                            opt.anchoredPosition = new Vector2(0f, -k * optH); opt.sizeDelta = new Vector2(0f, optH);
                            if (sel) Widgets.Stretch(Widgets.Box(opt, "Sel", KitSprites.RoundedFill(FiresTokens.RadiusSmall), FiresTokens.Selected, true).rectTransform, 0, 0, 0, 0);
                            var ol = KitText.Create(opt, "L", env.Length == 0 ? "Auto" : PlatePills.Pretty(env), FontRole.SansRegular, 14f, sel ? FiresTokens.Gold : FiresTokens.Text, KitAlign.Left, 0f, false, false);
                            Widgets.Stretch(ol.Rect, 8f, 0f, 4f, 0f); ol.Graphic.raycastTarget = false;
                            var oh = opt.gameObject.AddComponent<Image>(); oh.color = new Color(0f, 0f, 0f, 0f);
                            ShellWidgets.Clickable(opt, () => { s_envListOpen = false; Run(env.Length == 0 ? "resetenv" : "env " + env); });
                        }
                    }
                    var reset = Widgets.Button(plate, "envreset", "Reset weather", ButtonKind.Primary, x + 30f + ctrlW + 8f, y, 110f, 26f);
                    ShellWidgets.Clickable(reset, () => { s_envListOpen = false; Run("resetenv"); });
                }
                else
                {
                    float v = em != null ? Mathf.Clamp01(em.m_debugWind ? em.m_debugWindIntensity : em.GetWindIntensity()) : 0f;
                    var sl = Widgets.Slider(plate, "wind", v, Mathf.RoundToInt(v * 10f).ToString(Inv), x + 30f, y, ctrlW, 26f);
                    var drag = sl.gameObject.AddComponent<ShellSliderDrag>();
                    var hit = sl.gameObject.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f);
                    drag.TrackWidth = ctrlW - 52f - FiresTokens.S12;
                    float pending = v;
                    float angle0 = em != null ? (em.m_debugWind ? em.m_debugWindAngle : Mathf.Atan2(em.GetWindDir().x, em.GetWindDir().z) * Mathf.Rad2Deg) : 0f;
                    // as the time slider (follows the pointer, preview on EnvMan, command once on release)
                    drag.OnValue = f => { pending = Mathf.Round(f * 10f) / 10f; SliderTick(sl, pending, Mathf.RoundToInt(pending * 10f).ToString(Inv)); if (em != null) { em.m_debugWind = true; em.m_debugWindAngle = angle0; em.m_debugWindIntensity = pending; } };
                    drag.OnEnd = () => SliderEnd("wind " + Mathf.Repeat(angle0, 360f).ToString("0", Inv) + " " + pending.ToString("0.0", Inv));
                    var reset = Widgets.Button(plate, "windreset", "Reset wind", ButtonKind.Primary, x + 30f + ctrlW + 8f, y, 110f, 26f);
                    ShellWidgets.Clickable(reset, () => Run("resetwind"));
                }
                y += 40f;
            }
        }
    }
}
