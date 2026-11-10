using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FiresCore.UI.Kit;
using FiresTokens = FiresCore.UI.Shell.Kit.FiresTokens;
using KitSprites = FiresCore.UI.Shell.Kit.KitSprites;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// 0.2.856 ([ghost], Fire 10-27, approved design Tools\DESIGN_homestead_map_20261027.md, phase 2 = read-only): the interactive homestead map.
    /// Unlocked by a Surveyor's Chart installed on a Job Board at base (FiresCompanions 0.1.113+, board ZDO "jb_up_chart"). Opened from the
    /// Job Board ("Map"): the large map centred on the base (the live map from FiresRPGmaker 0.0.777+, by reflection), markers over it for
    /// every layer, and the LEDGER window: collapsible sections (Stations, Storage, Garden, Resources, Companions) with a show toggle per
    /// section and per item type inside, counts beside each, remembered per player in BepInEx\cache\FiresCore\homestead_map.json.
    /// 0.2.858 (Fire 10-27 shot 556: "the live map shows basically nothing, and choosing the map should close, or minimize the job board ui
    /// and dock it to the side, and the territory popup doesnt need to show in our jobboard homestead map"):
    ///  - markers live in their own layer, a copy of the map image's rect made the LAST sibling of the map image's parent, so nothing
    ///    (the live map's overlay is inserted right after the map image) draws over them; each type has an icon (the piece's own icon for
    ///    stations / storage / crops, the drop's icon for trees, ore and pickables) on a coloured disc, 24 px; companions 28 px with name + job.
    ///  - the full Job Board hides while the map shows; a small docked "Job Board" panel (counts + Expand) sits where the Territory pill sits
    ///    (under FiresRPGmaker's LiveMapApi.DockAnchor), and that pill is hidden (LiveMapApi.SetHomesteadMode). Closing the map, or Expand,
    ///    brings the full board back.
    /// Lines: "[HomeMap] opened at (x, z) ...", "[HomeMap] refused: why", "[HomeMap] drew N markers (by layer ...)" (first draw, then every
    /// 30 s while open, with the layer's sibling order and the first marker's position inside the map rect), "[HomeMap] job board docked",
    /// "[HomeMap] job board restored".
    /// </summary>
    internal static class HomesteadMap
    {
        private const float Radius = 60f, ScanSeconds = 2f, Icon = 24f, CompanionIcon = 12f;   // 0.2.x ([ghost], Fire 11-01: "markers for the companions themselves should be much smaller"): a dot, half the static icons
        private const int MaxMarkers = 400;
        private const string ChartKey = "jb_up_chart";

        // layer type -> (section, label, colour, fallback icon item)
        private static readonly (string type, string section, string label, Color color, string icon)[] Types =
        {
            ("Workbench", "Stations", "Workbenches / forges", new Color(0.95f, 0.75f, 0.3f), "Hammer"),
            ("Smelter", "Stations", "Smelters / kilns", new Color(1f, 0.45f, 0.2f), "Coal"),
            ("Fire", "Stations", "Fires / hearths", new Color(1f, 0.3f, 0.1f), "Resin"),
            ("Cooking", "Stations", "Cooking / fermenters / hives", new Color(0.9f, 0.6f, 0.4f), "CookedMeat"),
            ("Chest", "Storage", "Chests", new Color(0.65f, 0.45f, 0.25f), "Wood"),
            ("Terminal", "Storage", "Storage terminals", new Color(0.4f, 0.8f, 1f), "Crystal"),
            ("Crop", "Garden", "Crops / saplings growing", new Color(0.5f, 0.9f, 0.3f), "Carrot"),
            ("Tree", "Resources", "Trees", new Color(0.2f, 0.6f, 0.2f), "Wood"),
            ("Ore", "Resources", "Ore / rock deposits", new Color(0.75f, 0.55f, 0.45f), "Stone"),
            ("Pickable", "Resources", "Pickables", new Color(0.9f, 0.3f, 0.6f), "Raspberry"),
            ("Companion", "Companions", "Companions (with their job)", new Color(0.95f, 0.9f, 0.4f), null),
            // 0.2.944 (Fire 10-27: "we should be able to show/hide the rings as well"): one row per area type (the circles are not markers)
            ("Area.gather", "Areas", "Gathering areas", new Color(0.55f, 0.85f, 0.35f), null),
            ("Area.chop", "Areas", "Logging areas", new Color(0.80f, 0.55f, 0.30f), null),
            ("Area.mine", "Areas", "Mining areas", new Color(0.70f, 0.75f, 0.85f), null),
            ("Area.hunt", "Areas", "Hunting areas", new Color(0.90f, 0.35f, 0.30f), null),
            ("Area.tame", "Areas", "Taming areas", new Color(0.85f, 0.50f, 0.85f), null),
            ("Area.loot", "Areas", "Loot pickup areas", new Color(0.40f, 0.80f, 0.90f), null),
            ("Area.none", "Areas", "Areas not designated yet", new Color(0.95f, 0.75f, 0.3f), null),
        };
        private static readonly string[] Sections = { "Stations", "Storage", "Garden", "Resources", "Companions", "Areas" };
        /// <summary>0.2.944: is this ledger row on (HomesteadMapCircles asks per area type).</summary>
        internal static bool Shows(string type) => Get(type) && Get(Types.FirstOrDefault(t => t.type == type).section ?? "");
        /// <summary>0.2.944: the count shown on a ledger row that is not a scanned marker type (the areas).</summary>
        internal static void SetCount(string type, int n) => s_counts[type] = n;

        private sealed class Settings { public Dictionary<string, bool> Show = new Dictionary<string, bool>(); public List<string> Collapsed = new List<string>(); }
        private static Settings s_set;
        private static bool s_open;
        private static Vector3 s_center;
        private static float s_nextScan, s_nextCostLog, s_nextDrawLog;
        private static double s_scanMs; private static int s_scanCount;

        private struct Mark { public Vector3 Pos; public string Type; public Sprite Icon; public string Name; public ZDOID Id; public GameObject Go; }
        // 0.2.864: pool slot -> the mark it shows this frame (the marker's click reads it)
        private static readonly List<Mark> s_placed = new List<Mark>();
        internal static string NameOf(GameObject go)
        {
            var hov = go.GetComponent<Hoverable>(); string n = null;
            try { n = hov?.GetHoverName(); } catch { }
            if (string.IsNullOrEmpty(n)) n = go.GetComponent<Piece>()?.m_name;
            return string.IsNullOrEmpty(n) ? Utils.GetPrefabName(go.name) : Localization.instance.Localize(n);
        }
        private static readonly List<Mark> s_marks = new List<Mark>();
        private static readonly Dictionary<string, int> s_counts = new Dictionary<string, int>();
        private static readonly List<RectTransform> s_pool = new List<RectTransform>();
        private static readonly Dictionary<string, Sprite> s_iconCache = new Dictionary<string, Sprite>();
        private static RectTransform s_root;
        private static KitWindow s_ledger, s_dock;
        private static bool s_ledgerDefaulted;
        private static TextMeshProUGUI s_dockText, s_dockText2;
        private static readonly Dictionary<string, TextMeshProUGUI> s_countLabels = new Dictionary<string, TextMeshProUGUI>();

        // ---- gate + open ------------------------------------------------------------------------------------------------------
        /// <summary>
        /// 0.2.862 (Fire 10-27 shot 559: "we cannot open the map despite it having the surveyors chart installed", board hover "(Chart)"):
        /// the board search no longer goes through Physics.OverlapSphere (it skips Ignore Raycast colliders) and no longer needs the board
        /// within 60 m of JobBoard.BaseCenter() (on a new world that point can be a companion's old home or the player). Every loaded
        /// FiresJobBoard_* instance (ZNetScene) is checked: a charted board within 60 m of the base, else a charted board within 20 m of
        /// the player, opens the map; the map centres on the base when the board is in it, else on the board. `info` = what was seen.
        /// </summary>
        internal static GameObject ChartBoard(Vector3 basePos, out Vector3 center, out string info)
        {
            center = basePos;
            var p = Player.m_localPlayer;
            Vector3 pp = p != null ? p.transform.position : basePos;
            GameObject nearBase = null, nearPlayer = null;
            int boards = 0, charted = 0;
            float nearest = float.MaxValue;
            var seen = new List<string>();
            var scene = ZNetScene.instance;
            if (scene != null)
                foreach (var nv in Traverse.Create(scene).Field("m_instances").GetValue<Dictionary<ZDO, ZNetView>>().Values)
                {
                    if (nv == null || !nv.IsValid() || !nv.gameObject.name.StartsWith("FiresJobBoard_")) continue;
                    boards++;
                    bool chart = nv.GetZDO().GetBool(ChartKey, false);
                    if (chart) charted++;
                    Vector3 at = nv.transform.position;
                    float db = Vector3.Distance(at, basePos), dp = Vector3.Distance(at, pp);
                    nearest = Mathf.Min(nearest, db);
                    if (seen.Count < 4) seen.Add($"({at.x:0}, {at.z:0}) chart {(chart ? "yes" : "no")} {db:0} m from base {dp:0} m from you");
                    if (!chart) continue;
                    if (db <= Radius && nearBase == null) nearBase = nv.gameObject;
                    if (dp <= 20f && nearPlayer == null) nearPlayer = nv.gameObject;
                }
            var pick = nearBase ?? nearPlayer;
            if (pick != null && nearBase == null) center = pick.transform.position;
            info = $"boards loaded {boards}, with a chart {charted}; base point ({basePos.x:0}, {basePos.z:0}) from {BaseSource()}; you at ({pp.x:0}, {pp.z:0})"
                 + (boards > 0 ? $"; nearest board {nearest:0} m from base; " + string.Join("; ", seen) : "")
                 + (pick != null ? $"; using the board at ({pick.transform.position.x:0}, {pick.transform.position.z:0}) ({(nearBase != null ? "within 60 m of base" : "within 20 m of you")})" : "");
            return pick;
        }

        private static string BaseSource()
        {
            if (FiresCore.Npc.AI.HomeBase.TryGet(out _, out _)) return "the named home base";
            var p = Player.m_localPlayer;
            foreach (var c in CompanionController.AllCompanions)
            {
                if (c == null || p == null || !c.isTamed || !c.IsOwner(p) || c.ShouldBeFollowing) continue;
                var idle = c.GetComponent<CompanionIdleBehavior>();
                if (idle != null && idle.HasHomePosition) return $"{c.GetDisplayName()}'s stay point";
            }
            return "your position (no home base and no staying companion)";
        }

        internal static void Open()
        {
            var mm = Minimap.instance;
            Vector3 b = JobBoard.BaseCenter();
            string info = "";
            string why = mm == null ? "no map"
                : ChartBoard(b, out b, out info) == null ? "install a Surveyor's Chart on a Job Board at your base (or stand within 20 m of a charted board) to unlock the homestead map" : null;
            if (why != null)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Homestead map: " + why);
                FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] refused: {why} ({info})");
                return;
            }
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] gate passed: {info}");
            LoadSettings();
            s_center = b; s_open = true; s_nextScan = 0f; s_nextDrawLog = 0f;
            s_openedAt = Time.realtimeSinceStartup; s_firstDrawn = false; s_firstScanMs = -1f;   // 0.2.863: open -> first draw timing
            mm.ShowPointOnMap(b);
            LiveMapCall("Focus", new[] { typeof(Vector3), typeof(float), typeof(bool) }, b, Radius * 3f, true);
            LiveMapCall("SetHomesteadMode", new[] { typeof(bool) }, true);
            ShowLedger();
            JobBoardWindow.HideForMap();
            ShowDock();
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] opened at ({b.x:0}, {b.z:0}) r {Radius:0} m");
        }

        // FiresRPGmaker's LiveMapApi (0.0.777+), by reflection; one "found / not found" line per method
        private static readonly Dictionary<string, System.Reflection.MethodInfo> s_live = new Dictionary<string, System.Reflection.MethodInfo>();
        private static object LiveMapCall(string name, Type[] sig, params object[] args)
        {
            if (!s_live.TryGetValue(name, out var m))
            {
                m = FiresCore.Compat.OptionalTypes.Find("FiresRPGmaker.UI.Map.LiveMapApi")?.GetMethod(name, sig);
                s_live[name] = m;
                FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] live map {name} {(m != null ? "found (FiresRPGmaker LiveMapApi)" : "not found (needs FiresRPGmaker 0.0.777+)")}");
            }
            try { return m?.Invoke(null, args); } catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] live map {name} failed: {e.Message}"); return null; }
        }

        // 0.2.875: test hooks (homemap_test)
        internal static bool TestIsOpen => s_open;
        internal static void TestClose() => Close(false);
        internal static KitWindow TestLedger => s_ledger;
        // 0.2.881 (round #6a: "chest no marker" - Fire's saved ledger had Storage / Garden / Resources off): a test shows every layer for its run
        // and puts the player's own choices back after, in memory and in the settings file
        private static Dictionary<string, bool> s_testSavedShow;
        internal static string TestShowAllLayers(bool on)
        {
            LoadSettings();
            if (on)
            {
                if (s_testSavedShow == null) s_testSavedShow = new Dictionary<string, bool>(s_set.Show);
                string hidden = string.Join(", ", s_set.Show.Where(kv => !kv.Value).Select(kv => kv.Key));
                s_set.Show.Clear();
                if (s_ledger != null && s_ledger.Root != null && s_ledger.IsShowing) BuildLedger();
                return hidden.Length > 0 ? $"showing all layers for the test (saved off: {hidden})" : "all layers were already shown";
            }
            if (s_testSavedShow == null) return "nothing to restore";
            s_set.Show = s_testSavedShow; s_testSavedShow = null;
            SaveSettings();
            if (s_ledger != null && s_ledger.Root != null && s_ledger.IsShowing) BuildLedger();
            return "the player's layer choices restored";
        }
        internal static int TestCount(string type) => s_counts.TryGetValue(type, out int n) ? n : 0;
        /// <summary>A shown marker of that type (its GameObject carries the MarkerClick), or null.</summary>
        internal static GameObject TestMarker(string type)
        {
            for (int i = 0; i < s_placed.Count && i < s_pool.Count; i++)
                if (s_placed[i].Type == type && s_pool[i] != null && s_pool[i].gameObject.activeInHierarchy) return s_pool[i].gameObject;
            return null;
        }

        private static void Close(bool showBoard = true)
        {
            if (!s_open) return;
            s_open = false;
            foreach (var m in s_pool) if (m != null) m.gameObject.SetActive(false);
            s_ledger?.Hide();
            HomesteadMapActions.HideCard();
            s_dock?.Hide();
            LiveMapCall("SetHomesteadMode", new[] { typeof(bool) }, false);
            JobBoardWindow.RestoreAfterMap(showBoard);
            SaveSettings();
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] job board restored ({(showBoard ? "full board shown" : "left closed")})");
        }

        // ---- scan --------------------------------------------------------------------------------------------------------------
        private static bool Shown(string type)
        {
            string section = Types.First(t => t.type == type).section;
            return Get(section) && Get(type);
        }
        private static bool Get(string key) => s_set == null || !s_set.Show.TryGetValue(key, out bool v) || v;

        // 0.2.863 (lead, Fire 10-27 "finally loaded"): "[HomeMap] first draw N ms after open (first scan done at S ms, M markers; live map ...)"
        private static float s_openedAt, s_firstScanMs = -1f; private static bool s_firstDrawn;

        private static void Scan()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            s_marks.Clear(); s_counts.Clear();
            var seen = new HashSet<int>();
            foreach (var col in Physics.OverlapSphere(s_center, Radius))
            {
                if (col == null) continue;
                var go = col.attachedRigidbody != null ? col.attachedRigidbody.gameObject : col.transform.root.gameObject;
                var nv = col.GetComponentInParent<ZNetView>();
                if (nv != null) go = nv.gameObject;
                // 0.2.868 (Fire 10-27 shot 585: "none of the minerock resources are regestering"): a deposit is the nearest MineRock5 /
                // MineRock / ore Destructible ABOVE the collider (a deposit's area children and a ZNetView-less placed copy resolved to
                // the wrong root before), and its marker sits on the deposit's mesh centre, not the ZDO origin
                var dep = (Component)col.GetComponentInParent<MineRock5>() ?? (Component)col.GetComponentInParent<MineRock>() ?? OreDestructible(col);
                if (dep != null) go = dep.gameObject;
                if (!seen.Add(go.GetInstanceID())) continue;
                string type = TypeOf(go);
                if (type == null) continue;
                s_counts[type] = (s_counts.TryGetValue(type, out int n) ? n : 0) + 1;
                if (type != "Companion") s_marks.Add(new Mark { Pos = type == "Ore" ? MarkPos(go) : go.transform.position, Type = type, Icon = IconOf(go, type), Go = go, Name = NameOf(go), Id = nv != null && nv.IsValid() ? nv.GetZDO().m_uid : ZDOID.None });
            }
            foreach (var c in CompanionController.AllCompanions)
            {
                if (c == null || !c.isTamed || Vector3.Distance(c.transform.position, s_center) > Radius * 1.5f) continue;
                s_counts["Companion"] = (s_counts.TryGetValue("Companion", out int n) ? n : 0) + 1;
                var cnv = c.GetComponent<ZNetView>();
                s_marks.Add(new Mark { Pos = c.transform.position, Type = "Companion", Go = c.gameObject, Name = c.GetDisplayName(), Id = cnv != null && cnv.IsValid() ? cnv.GetZDO().m_uid : ZDOID.None });
            }
            s_marks.Sort((a, b) => Vector3.Distance(a.Pos, s_center).CompareTo(Vector3.Distance(b.Pos, s_center)));
            // Fire 11-01: companions are never dropped at the cap and draw last (slot = sibling order), so they sit on top of static icons
            var comps = s_marks.FindAll(x => x.Type == "Companion"); s_marks.RemoveAll(x => x.Type == "Companion");
            if (s_marks.Count + comps.Count > MaxMarkers) s_marks.RemoveRange(Mathf.Max(0, MaxMarkers - comps.Count), s_marks.Count - Mathf.Max(0, MaxMarkers - comps.Count));
            s_marks.AddRange(comps);
            s_scanMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; s_scanCount++;
            if (Time.unscaledTime >= s_nextCostLog)
            {
                s_nextCostLog = Time.unscaledTime + 30f;
                FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] scan {s_marks.Count} markers in {s_scanMs / Mathf.Max(1, s_scanCount):0.00} ms avg ({string.Join(", ", s_counts.Select(kv => kv.Key + " " + kv.Value))})");
                s_scanMs = 0; s_scanCount = 0;
            }
            if (s_firstScanMs < 0f) s_firstScanMs = (Time.realtimeSinceStartup - s_openedAt) * 1000f;
            LogOre();
            UpdateCounts();
        }

        // "[HomeMap] ore scan: found N (by type ...), markers drawn D, at <pos list>" - first scan of an opening, then every 30 s
        private static float s_nextOreLog;
        private static void LogOre()
        {
            if (s_firstScanMs >= 0f && Time.unscaledTime < s_nextOreLog) return;
            s_nextOreLog = Time.unscaledTime + 30f;
            var ores = s_marks.Where(m => m.Type == "Ore").ToList();
            var byType = ores.GroupBy(m => m.Go == null ? "?" : m.Go.GetComponent<MineRock5>() != null ? "MineRock5" : m.Go.GetComponent<MineRock>() != null ? "MineRock" : "Destructible").Select(g => g.Key + " " + g.Count());
            int drawn = s_placed.Count(m => m.Type == "Ore");
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] ore scan: found {ores.Count} ({string.Join(", ", byType)}), markers drawn {drawn} last frame (layer {(Shown("Ore") ? "shown" : "hidden")}), at {string.Join("; ", ores.Take(12).Select(m => $"{(m.Go != null ? Utils.GetPrefabName(m.Go) : "?")} ({m.Pos.x:0}, {m.Pos.z:0})"))}");
        }

        private static bool IsOreName(string n)
        {
            n = (n ?? "").ToLowerInvariant();
            return n.Contains("minerock") || n.Contains("copper") || n.Contains("tin") || n.Contains("silver") || n.Contains("iron") || n.Contains("obsidian") || n.Contains("flametal");
        }
        private static Component OreDestructible(Collider col)
        {
            var d = col.GetComponentInParent<Destructible>();
            return d != null && IsOreName(d.gameObject.name) ? d : null;
        }
        // the deposit's mesh centre (its renderers' bounds), else its transform
        private static Vector3 MarkPos(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return go.transform.position;
            var b = rs[0].bounds; for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b.center;
        }

        internal static string TypeOf(GameObject go)
        {
            if (go.GetComponent<CompanionController>() != null || go.GetComponent<Character>() != null) return null;
            if (go.GetComponent<Smelter>() != null) return "Smelter";
            if (go.GetComponent<CraftingStation>() != null) return "Workbench";
            if (go.GetComponent<Fireplace>() != null) return "Fire";
            if (go.GetComponent<CookingStation>() != null || go.GetComponent<Fermenter>() != null || go.GetComponent<Beehive>() != null) return "Cooking";
            if (go.GetComponents<MonoBehaviour>().Any(m => m != null && m.GetType().Name == "StorageTerminal")) return "Terminal";
            if (go.GetComponent<Container>() != null && go.GetComponentInParent<Vagon>() == null && go.GetComponentInParent<Ship>() == null) return "Chest";
            if (go.GetComponent<Plant>() != null) return "Crop";
            if (go.GetComponent<TreeBase>() != null || go.GetComponent<TreeLog>() != null) return "Tree";
            if (go.GetComponent<MineRock5>() != null || go.GetComponent<MineRock>() != null || (go.GetComponent<Destructible>() != null && IsOreName(go.name))) return "Ore";
            if (go.GetComponent<Pickable>() != null) return "Pickable";
            return null;
        }

        // the object's own icon: a piece's icon (stations, storage, crops), else its first drop's icon; else the type's fallback item
        private static Sprite IconOf(GameObject go, string type)
        {
            string key = Utils.GetPrefabName(go.name);
            if (s_iconCache.TryGetValue(key, out var s)) return s;
            try
            {
                s = go.GetComponent<Piece>()?.m_icon;
                if (s == null) { var p = go.GetComponent<Pickable>(); s = ItemIcon(p != null && p.m_itemPrefab != null ? p.m_itemPrefab.name : null); }
                if (s == null) { var d = go.GetComponent<MineRock5>()?.m_dropItems ?? go.GetComponent<MineRock>()?.m_dropItems ?? go.GetComponent<TreeBase>()?.m_dropWhenDestroyed ?? go.GetComponent<TreeLog>()?.m_dropWhenDestroyed;
                    s = ItemIcon(d != null && d.m_drops != null && d.m_drops.Count > 0 && d.m_drops[0].m_item != null ? d.m_drops[0].m_item.name : null); }
                if (s == null) s = ItemIcon(Types.First(t => t.type == type).icon);
            }
            catch { s = null; }
            s_iconCache[key] = s;
            return s;
        }
        private static Sprite ItemIcon(string item)
        {
            if (string.IsNullOrEmpty(item) || ObjectDB.instance == null) return null;
            var d = ObjectDB.instance.GetItemPrefab(item)?.GetComponent<ItemDrop>();
            return d != null && d.m_itemData.m_shared.m_icons != null && d.m_itemData.m_shared.m_icons.Length > 0 ? d.m_itemData.m_shared.m_icons[0] : null;
        }

        // ---- markers -----------------------------------------------------------------------------------------------------------
        private static readonly System.Reflection.MethodInfo s_w2m = AccessTools.Method(typeof(Minimap), "WorldToMapPoint");
        private static readonly System.Reflection.MethodInfo s_m2g = AccessTools.Method(typeof(Minimap), "MapPointToLocalGuiPos", new[] { typeof(float), typeof(float), typeof(RawImage) });
        // 0.2.890: for HomesteadMapCircles - the markers' layer and world -> marker-layer GUI position (the same maths as the markers)
        internal static RectTransform MarkerLayer => s_root;
        internal static Vector2 WorldToGui(Vector3 world) => Gui(Minimap.instance, world);

        private static Vector2 Gui(Minimap mm, Vector3 world)
        {
            var args = new object[] { world, 0f, 0f };
            s_w2m.Invoke(mm, args);
            return (Vector2)s_m2g.Invoke(mm, new object[] { (float)args[1], (float)args[2], mm.m_mapImageLarge });
        }

        // the marker layer: the map image's rect, last sibling of its parent (above the live map overlay and the painted map)
        private static void SyncRoot(Minimap mm)
        {
            var img = mm.m_mapImageLarge.rectTransform;
            if (s_root == null)
            {
                var go = new GameObject("FiresHomesteadMarkers", typeof(RectTransform));
                s_root = (RectTransform)go.transform;
                s_root.SetParent(img.parent, false);
            }
            if (s_root.parent != img.parent) s_root.SetParent(img.parent, false);
            if (s_root.GetSiblingIndex() != s_root.parent.childCount - 1) s_root.SetAsLastSibling();
            s_root.anchorMin = img.anchorMin; s_root.anchorMax = img.anchorMax; s_root.pivot = img.pivot;
            s_root.anchoredPosition = img.anchoredPosition; s_root.sizeDelta = img.sizeDelta; s_root.localScale = img.localScale; s_root.localRotation = img.localRotation;
        }

        private static HomesteadMapActions.Target ToTarget(Mark m)
            => new HomesteadMapActions.Target { Type = m.Type, Name = m.Name ?? m.Type, Pos = m.Go != null ? m.Go.transform.position : m.Pos, Id = m.Id, Go = m.Go };

        private static RectTransform Marker(int i)
        {
            while (s_pool.Count <= i)
            {
                var m = new GameObject("Mark", typeof(RectTransform));
                var rt = (RectTransform)m.transform;
                rt.SetParent(s_root, false);
                rt.anchorMin = rt.anchorMax = Vector2.zero; rt.pivot = new Vector2(0.5f, 0.5f); rt.sizeDelta = new Vector2(Icon, Icon);
                var disc = m.AddComponent<Image>(); disc.sprite = KitSprites.RoundedFill(Icon * 0.5f); disc.type = Image.Type.Sliced; disc.raycastTarget = true;   // 0.2.864: clickable
                int slot = s_pool.Count;
                m.AddComponent<HomesteadMapActions.MarkerClick>().Get = () => slot < s_placed.Count ? ToTarget(s_placed[slot]) : null;
                var ig = new GameObject("Icon", typeof(RectTransform)); var irt = (RectTransform)ig.transform; irt.SetParent(rt, false);
                irt.anchorMin = new Vector2(0.12f, 0.12f); irt.anchorMax = new Vector2(0.88f, 0.88f); irt.offsetMin = irt.offsetMax = Vector2.zero;
                var icon = ig.AddComponent<Image>(); icon.raycastTarget = false; icon.preserveAspect = true;
                // Fire 11-01: the activity text shows only on hover, on top of everything (FiresHoverTip's own canvas); read when the tip opens
                var tip = FiresCore.UI.FiresHoverTip.Attach(m, "", "", () => slot < s_placed.Count ? CompanionJob(s_placed[slot]) : null);
                if (tip != null) tip.IgnoreMaster = true;
                s_pool.Add(rt);
            }
            return s_pool[i];
        }

        // The hovered companion's job, localized (status text carries tokens like $item_pickable_branch); null for static icons
        private static string CompanionJob(Mark m)
        {
            if (m.Type != "Companion" || m.Go == null) return null;
            var c = m.Go.GetComponent<CompanionController>();
            if (c == null) return null;
            string job = JobBoard.StateOf(c);
            return Localization.instance != null ? Localization.instance.Localize(job) : job;
        }

        private static void Place()
        {
            var mm = Minimap.instance;
            SyncRoot(mm);
            int used = 0, inside = 0;
            var byLayer = new Dictionary<string, int>();
            Rect r = s_root.rect;
            Vector2 first = Vector2.zero;
            foreach (var m in s_marks)
            {
                if (!Shown(m.Type)) continue;
                var rt = Marker(used);
                while (s_placed.Count <= used) s_placed.Add(default);
                s_placed[used] = m;
                Vector2 p = Gui(mm, m.Pos);
                if (used == 0) first = p;
                used++;
                bool on = p.x >= 0f && p.y >= 0f && p.x <= r.width && p.y <= r.height;
                rt.gameObject.SetActive(on);
                if (!on) continue;
                inside++;
                byLayer[m.Type] = (byLayer.TryGetValue(m.Type, out int n) ? n : 0) + 1;
                rt.anchoredPosition = p;
                var spec = Types.First(t => t.type == m.Type);
                bool comp = m.Type == "Companion";
                float size = comp ? CompanionIcon : Icon;
                rt.sizeDelta = new Vector2(size, size);
                bool sel = HomesteadMapActions.Selected != null && HomesteadMapActions.Selected.Go == m.Go && m.Go != null;
                if (sel) rt.sizeDelta = new Vector2(size * 1.35f, size * 1.35f);
                var disc = rt.GetComponent<Image>(); disc.color = sel ? FiresTokens.Gold : comp ? spec.color : new Color(spec.color.r * 0.45f, spec.color.g * 0.45f, spec.color.b * 0.45f, 0.85f);
                var icon = rt.Find("Icon").GetComponent<Image>(); icon.sprite = m.Icon; icon.enabled = m.Icon != null;
                disc.raycastPadding = comp ? new Vector4(-3f, -3f, -3f, -3f) : Vector4.zero;   // 18 px hover and click area around the 12 px dot
                var tip = rt.GetComponent<FiresCore.UI.FiresHoverTip>(); if (tip != null) tip.Topic = comp ? m.Name : "";   // empty topic + body = no tip on static icons
            }
            for (int i = used; i < s_pool.Count; i++) if (s_pool[i] != null) s_pool[i].gameObject.SetActive(false);
            if (!s_firstDrawn && inside > 0)
            {
                s_firstDrawn = true;
                var lm = FiresCore.Compat.OptionalTypes.Find("FiresRPGmaker.UI.Map.LiveMap");
                object shown = null; try { shown = lm != null ? AccessTools.Property(lm, "TestShown")?.GetValue(null) : null; } catch { }
                FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] first draw {(Time.realtimeSinceStartup - s_openedAt) * 1000f:0} ms after open (first scan done at {s_firstScanMs:0} ms, {inside} markers on the map; live map render {(shown == null ? "not found" : (bool)shown ? "showing" : "not showing yet ([Live map] Enabled key off or the Live header not chosen)")})");
            }
            if (Time.unscaledTime >= s_nextDrawLog && s_marks.Count > 0)
            {
                s_nextDrawLog = Time.unscaledTime + 30f;
                var img = mm.m_mapImageLarge.transform;
                FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] drew {inside} markers of {used} shown (by layer {string.Join(", ", byLayer.Select(kv => kv.Key + " " + kv.Value))}); layer sibling {s_root.GetSiblingIndex()}/{s_root.parent.childCount - 1} under '{s_root.parent.name}' (map image {img.GetSiblingIndex()}), rect {r.width:0}x{r.height:0}, first at ({first.x:0}, {first.y:0})");
            }
        }

        // ---- docked job board --------------------------------------------------------------------------------------------------
        private static void ShowDock()
        {
            if (s_dock == null || s_dock.Root == null)
            {
                s_dock = KitWindow.Create("FiresCore.HomesteadMapDock", "Job Board", "", new Vector2(260f, 150f), new Vector2(200f, 60f));
                s_dock.OnClosed += () => { if (s_open) { Close(false); Minimap.instance?.SetMapMode(Minimap.MapMode.Small); } };
                // 0.2.863 (Fire 10-27 shot 570: "not fitting all its info corectly with padding between the elements (not expanding to fit
                // its text)"): no subtitle (it overlapped the first row); a note, two count rows and the button each their own row in the
                // body column with 8 px spacing; the window's height follows the body's preferred height (FitDock)
                var vlg = s_dock.Body.GetComponent<VerticalLayoutGroup>(); if (vlg != null) vlg.spacing = Mathf.Max(vlg.spacing, 8f);
                KitUI.Note(s_dock.Body, "Docked while the homestead map shows");
                s_dockText = KitUI.Text(s_dock.Body, "");
                s_dockText2 = KitUI.Text(s_dock.Body, "");
                KitUI.Button(s_dock.Body, "Expand Job Board", true, () => { FiresCore.Logging.FiresLogger.LogDebug("[HomeMap] Expand pressed"); Close(true); Minimap.instance?.SetMapMode(Minimap.MapMode.Small); });
            }
            s_dock.Show();
            DockPosition(true);
            FiresCore.Logging.FiresLogger.LogDebug("[HomeMap] job board docked (full board hidden, Territory pill hidden)");
        }

        // under the live map Level widget (or vanilla's biome box), right edges aligned, 8 px gap: the Territory pill's spot
        private static void DockPosition(bool log)
        {
            if (s_dock == null || s_dock.Root == null) return;
            var anchor = LiveMapCall("DockAnchor", Type.EmptyTypes) as RectTransform;
            if (anchor == null && Minimap.instance != null && Minimap.instance.m_biomeNameLarge != null) anchor = Minimap.instance.m_biomeNameLarge.rectTransform;
            if (anchor == null) { if (log) FiresCore.Logging.FiresLogger.LogDebug("[HomeMap] dock: no anchor, window left where it was"); return; }
            var a = new Vector3[4]; anchor.GetWorldCorners(a);
            var w = new Vector3[4]; s_dock.Root.GetWorldCorners(w);
            float gap = 8f * (s_dock.Root.lossyScale.y > 0f ? s_dock.Root.lossyScale.y : 1f);
            var delta = new Vector3(a[3].x - w[2].x, (a[3].y - gap) - w[2].y, 0f);
            if (delta.sqrMagnitude > 0.25f) s_dock.Root.position += delta;
            if (log) FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] dock under '{anchor.name}' at screen ({a[3].x:0}, {a[3].y:0})");
        }

        // the dock's height = (window top to body top) + the body's preferred height + 12 px bottom; a line when it changes:
        // "[HomeMap] dock fitted: body WxH (top T), window WxH"
        private static float s_dockFitH = -1f;
        private static void FitDock()
        {
            if (s_dock == null || s_dock.Root == null || !s_dock.IsShowing) return;
            var body = s_dock.Body;
            LayoutRebuilder.ForceRebuildLayoutImmediate(body);
            float pref = LayoutUtility.GetPreferredHeight(body);
            var wc = new Vector3[4]; s_dock.Root.GetWorldCorners(wc);
            var bc = new Vector3[4]; body.GetWorldCorners(bc);
            float scale = s_dock.Root.lossyScale.y > 0f ? s_dock.Root.lossyScale.y : 1f;
            float top = (wc[1].y - bc[1].y) / scale;
            float h = Mathf.Ceil(top + pref + 12f);
            if (Mathf.Abs(h - s_dockFitH) < 0.5f) return;
            s_dockFitH = h;
            var size = s_dock.Root.sizeDelta; s_dock.Root.sizeDelta = new Vector2(size.x, h);
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] dock fitted: body {body.rect.width:0}x{pref:0} (top {top:0}), window {size.x:0}x{h:0}");
        }

        private static void UpdateDock()
        {
            if (s_dockText == null) return;
            int c(string k) => s_counts.TryGetValue(k, out int n) ? n : 0;
            s_dockText.text = $"Companions {c("Companion")}   Chests {c("Chest")}";
            if (s_dockText2 != null) s_dockText2.text = $"Stations {c("Workbench") + c("Smelter") + c("Fire") + c("Cooking")}   Crops {c("Crop")}";
            FitDock();
        }

        // ---- ledger ------------------------------------------------------------------------------------------------------------
        private static void ShowLedger()
        {
            if (s_ledger == null || s_ledger.Root == null)
            {
                s_ledger = KitWindow.Create("FiresCore.HomesteadMapLedger", "Homestead Ledger", "show / hide what the map marks", new Vector2(360f, 620f), new Vector2(280f, 300f));
                s_ledger.OnClosed += () => { if (s_open) Close(); };
                // 0.2.868 (Fire 10-27: "the homestead ledger shows up in the center of the fucking screen every time we open the map, put it
                // on the edge by default please and save the position we may move it to so it persists"): it had no DefaultPlacement, so
                // KitWindow centred it whenever no rect was saved. Default = the LEFT edge, 24 px in, top 120 px down (under the map's
                // title bar), used only when KitWindow has no saved rect; a drag / resize is kept by KitWindow (RememberRect, the
                // kitwindows.txt cache every window uses) across map opens, sessions and relogs.
                s_ledger.DefaultPlacement = px => { s_ledgerDefaulted = true; return new Rect(24f, Mathf.Max(24f, Screen.height - 120f - px.y), px.x, px.y); };
            }
            BuildLedger();
            s_ledgerDefaulted = false;
            s_ledger.Show();
            var lr = s_ledger.Root;
            FiresCore.Logging.FiresLogger.LogDebug(s_ledgerDefaulted
                ? $"[HomeMap] ledger placed: default once (left edge; size {lr.sizeDelta.x:0}x{lr.sizeDelta.y:0}; a drag or resize is remembered)"
                : $"[HomeMap] ledger placed: restored saved ({lr.anchoredPosition.x:0}, {lr.anchoredPosition.y:0}, {lr.sizeDelta.x:0}, {lr.sizeDelta.y:0})");
        }

        private static void BuildLedger()
        {
            var body = s_ledger.Body;
            for (int i = body.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(body.GetChild(i).gameObject);
            s_countLabels.Clear();
            var scroll = KitUI.ScrollView(body, 200f);
            foreach (string sec in Sections)
            {
                bool collapsed = s_set.Collapsed.Contains(sec);
                var head = KitUI.Row(scroll, (collapsed ? "▶ " : "▼ ") + sec.ToUpperInvariant());
                head.Label.fontStyle = FontStyles.Bold; head.Label.color = FiresTokens.Gold;
                string s = sec;
                var click = head.Label.gameObject.AddComponent<Button>();
                click.onClick.AddListener(() => { if (!s_set.Collapsed.Remove(s)) s_set.Collapsed.Add(s); SaveSettings(); BuildLedger(); });
                // 0.2.863 (lead, shot 569: section pills OFF while their rows show ON): the section pill drives its rows - switching it sets
                // every row in the section to the same value and redraws the ledger, so the pills always agree
                KitUI.PillToggle(head.Control, Get(sec), v => { s_set.Show[s] = v; foreach (var t in Types.Where(x => x.section == s)) s_set.Show[t.type] = v; SaveSettings(); FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] section {s} {(v ? "shown" : "hidden")} with all its rows"); BuildLedger(); });
                if (collapsed) continue;
                foreach (var t in Types.Where(x => x.section == sec))
                {
                    string type = t.type;
                    var row = KitUI.Row(scroll, "    " + t.label);
                    s_countLabels[type] = KitUI.Label(row.Control, "Count", "", KitUI.SmallSize, FiresTokens.TextMuted, TextAlignmentOptions.MidlineRight);
                    KitUI.PillToggle(row.Control, Get(type), v => { s_set.Show[type] = v; SaveSettings(); FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] layer {type} {(v ? "shown" : "hidden")}"); });
                }
            }
            KitUI.Note(scroll, "Left-click a marker for its info; right-click it for actions.");
            UpdateCounts();
        }

        private static void UpdateCounts()
        {
            foreach (var kv in s_countLabels)
                if (kv.Value != null) kv.Value.text = (s_counts.TryGetValue(kv.Key, out int n) ? n : 0).ToString();
            UpdateDock();
        }

        // ---- settings ----------------------------------------------------------------------------------------------------------
        private static string SettingsPath => Path.Combine(FiresCore.Storage.FiresConfigPaths.CacheRoot, "homestead_map.json");
        private static void LoadSettings()
        {
            if (s_set != null) return;
            try { s_set = File.Exists(SettingsPath) ? JsonConvert.DeserializeObject<Settings>(File.ReadAllText(SettingsPath)) : null; } catch { s_set = null; }
            if (s_set == null) s_set = new Settings();
            if (s_set.Show == null) s_set.Show = new Dictionary<string, bool>();
            if (s_set.Collapsed == null) s_set.Collapsed = new List<string>();
        }
        private static void SaveSettings()
        {
            if (s_set == null) return;
            try { File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(s_set)); } catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[HomeMap] settings save failed: " + e.Message); }
        }

        // ---- pump --------------------------------------------------------------------------------------------------------------
        private sealed class Pump : MonoBehaviour
        {
            private void LateUpdate()
            {
                if (!s_open) return;
                try
                {
                    var mm = Minimap.instance;
                    if (mm == null || mm.m_mode != Minimap.MapMode.Large) { FiresCore.Logging.FiresLogger.LogDebug("[HomeMap] closed (map closed)"); Close(); return; }
                    if (Time.unscaledTime >= s_nextScan) { s_nextScan = Time.unscaledTime + ScanSeconds; Scan(); }
                    Place();
                    HomesteadMapActions.Tick();   // 0.2.864: "Send to..." waits for a map click
                    DockPosition(false);
                }
                catch (Exception e) { Debug.LogWarning("[HomeMap] " + e.Message); Close(); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_HomesteadMap
        {
            private static void Postfix()
            {
                s_open = false; s_pool.Clear(); s_root = null; s_iconCache.Clear();
                if (UnityEngine.Object.FindObjectOfType<Pump>() == null) { var go = new GameObject("FiresHomesteadMap"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<Pump>(); }
            }
        }
    }
}
