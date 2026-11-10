using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// 0.2.92x ([ghost], Fire 10-27: "add extra homestead chore options to any homestead that is built in the mountains or the deep North:
    /// companions clear snow from an area and keep roofs clear periodically to prevent collapse"; Tools\SNOW_CHORES_PLAN_20261027.md).
    ///  - GATE (every 60 s): the base centre is Mountain / Deep North (painted counts: Heightmap.FindBiome), or Core's snow field is deep
    ///    (> 0.3 m) on over 20 % of 25 samples round it. "[Snow] snow chores ON | OFF at (x, z): biome ..., deep-snow cover n %" on a change.
    ///  - SHOVEL: the snow shovel ($item_snowshovel, found in ObjectDB by name) in the companion's bag; else taken from a base chest (walked to,
    ///    storage-terminal aware); else crafted at its station from stored materials (Fire 10-27: "YES, craft a shovel at the workbench").
    ///  - ROOFS (every 120 s): the base's pieces with FTS RoofRisk >= 0.6, highest first; >= 0.85 JUMPS THE QUEUE (an order-level walk that
    ///    takes over whatever chore the nearest free companion is on; only a fire going out and defence rank above - HomesteadDefence alerts
    ///    stop all snow work). The companion walks into reach (route memory / climb when those are on) and calls FTS ClearRoof; never onto a
    ///    piece at >= 0.95 (it clears from beside / below).
    ///  - PATHS: doors (+ a 2 m apron), stations, chests and beds, and the straight ways from each door to its three nearest of those,
    ///    sampled every 2 m; the deepest sample over 0.4 m first; a companion with no chore walks there and calls FTS ShovelAt.
    ///  - "[Snow] <name> cleared roof <prefab> at (x, y, z): risk a -> b", "[Snow] <name> cleared path (x, z) depth a -> b m (n of m left)",
    ///    "[Snow] <name> skipped <what>: <why>".
    /// Runs on the process that owns the companions (the server sim usually). Needs FTS 0.3.268+ (FtsSnowApi, bound by reflection; without it
    /// the chores stay off and say so once). [Homestead Snow] Snow chores, OFF until core_snow_chores_test passes (Fire).
    /// </summary>
    internal static class SnowChores
    {
        // 1086 ([generator], Fire 11-01: configs live in their own mods): bound by FiresCompanions; Core reads the owner's value (old default OFF without it).
        private static readonly FiresCore.Config.OwnedSetting<bool> s_on = new FiresCore.Config.OwnedSetting<bool>("Homestead Snow", "Snow chores", false);
        internal static bool s_testOn;   // core_snow_chores_test only; never saved to the cfg
        internal static Vector3? s_testCentre;   // the test runs at the snow hut, away from the homestead centre; test companions count as free, walk at order level
        internal static void BindConfig(BepInEx.Configuration.ConfigFile cfg) { }
        internal static bool On => s_testOn || s_on.Value;

        private const float BaseRange = 30f, RoofEvery = 120f, GateEvery = 60f, RoofAt = 0.6f, RoofUrgent = 0.85f, RoofNoStand = 0.95f, PathAt = 0.4f, Reach = 3.5f;
        private const string ShovelShared = "$item_snowshovel";

        // ---- piece-snow API (reflection): Vedr's VedrApi first, then FTS's FtsSnowApi
        // [ghost] 11-07 (Fire: FTS piece snow moved into Vedr; [seasons]: Vedr 0.8.153 / VedrApi.ApiVersion 14 has the same statics
        // with identical signatures + meaning, and FTS 0.3.341's FtsSnowApi forwards to it when Vedr is present). Vedr only -> VedrApi;
        // FTS only -> FtsSnowApi; both -> VedrApi directly (no double hop). An older VedrApi without these methods falls through to FTS.
        private static readonly string[] s_apiTypes = { "Vedr.VedrApi, Norger.Vedr", "VerdantsAscent.Water.Core.FtsSnowApi, FiresTossinShade" };
        private static bool s_bound, s_missingSaid;
        private static Func<WearNTear, float> s_roofRisk;
        private static Func<Character, Vector3, bool> s_shovelAt;
        private static Func<Character, WearNTear, bool> s_clearRoof;
        private static MethodInfo s_testLoad;
        internal static string BoundApi { get; private set; } = "";
        internal static bool Bound { get { Bind(); return s_roofRisk != null && s_shovelAt != null && s_clearRoof != null; } }
        private static void Bind()
        {
            if (s_bound) return;
            s_bound = true;
            // 1197 ([lead] 11-07 proof run: FAT's bind line only logged at the first path dig, so it was unprovable without a digging
            // companion): bind FAT here too, so both bind lines log together at load (the runner's first tick with the chores on).
            FatGround(null, Vector3.zero, out _);
            foreach (var name in s_apiTypes)
            {
                try
                {
                    var t = Type.GetType(name);
                    if (t == null) continue;
                    var risk = t.GetMethod("RoofRisk", new[] { typeof(WearNTear) });
                    var shovel = t.GetMethod("ShovelAt", new[] { typeof(Character), typeof(Vector3) });
                    var clear = t.GetMethod("ClearRoof", new[] { typeof(Character), typeof(WearNTear) });
                    if (risk == null || shovel == null || clear == null)
                    {
                        FiresCore.Logging.FiresLogger.LogDebug($"[Snow] {name}: present but without RoofRisk/ShovelAt/ClearRoof (older build), trying the next");
                        continue;
                    }
                    s_roofRisk = (Func<WearNTear, float>)Delegate.CreateDelegate(typeof(Func<WearNTear, float>), risk);
                    s_shovelAt = (Func<Character, Vector3, bool>)Delegate.CreateDelegate(typeof(Func<Character, Vector3, bool>), shovel);
                    s_clearRoof = (Func<Character, WearNTear, bool>)Delegate.CreateDelegate(typeof(Func<Character, WearNTear, bool>), clear);
                    s_testLoad = t.GetMethod("TestLoadRoof", new[] { typeof(WearNTear), typeof(float) });
                    BoundApi = t.FullName + " (" + t.Assembly.GetName().Name + ")";
                    FiresCore.Logging.FiresLogger.LogInfo($"[Snow] snow chores bound to {(name.StartsWith("Vedr.") ? "Vedr" : "FTS")} ({BoundApi})");
                    return;
                }
                catch (Exception e)
                {
                    FiresCore.Logging.FiresLogger.LogDebug($"[Snow] {name} bind failed: {e.Message}");
                    s_roofRisk = null; s_shovelAt = null; s_clearRoof = null; s_testLoad = null;
                }
            }
        }
        // ---- FAT ground snow (soft): FatApi.ShovelGroundAt (FAT 0.2.1405+, [saddles]: public static bool (Character, Vector3); true = a 1 m
        // snow patch op was sent, false = refused (no snow / water / a snowy piece there), never throws, main thread, any peer, syncs itself).
        // Piece snow is Vedr/FTS's, ground snow is FAT's: with FTS 0.3.341 FTS's own FAT patch inside ShovelAt is gone. A path swing tries
        // FAT first and calls Vedr/FTS ShovelAt only when FAT refused (one swing, one result).
        private static bool s_fatBound;
        private static Func<Character, Vector3, bool> s_fatShovelGround;
        private static bool FatGround(Character who, Vector3 at, out string how)
        {
            if (!s_fatBound)
            {
                s_fatBound = true;
                try
                {
                    var t = Type.GetType("VerdantsAscent.FatApi, FiresAdminTerrain");
                    var m = t?.GetMethod("ShovelGroundAt", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Character), typeof(Vector3) }, null);
                    if (m != null && m.ReturnType == typeof(bool)) s_fatShovelGround = (Func<Character, Vector3, bool>)Delegate.CreateDelegate(typeof(Func<Character, Vector3, bool>), m);
                }
                catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[Snow] FatApi bind failed: " + e.Message); s_fatShovelGround = null; }
                FiresCore.Logging.FiresLogger.LogInfo(s_fatShovelGround != null
                    ? "[Snow] snow chores: FAT ground snow dig bound (VerdantsAscent.FatApi.ShovelGroundAt)"
                    : "[Snow] snow chores: FAT ground snow dig not present (needs FiresAdminTerrain 0.2.1405+): paths clear piece snow only");
            }
            if (s_fatShovelGround == null || who == null) { how = "absent"; return false; }
            try { bool dug = s_fatShovelGround(who, at); how = dug ? "dug" : "not dug (see the [Shovel] line)"; return dug; }
            catch (Exception e) { how = "threw " + e.Message; return false; }
        }

        internal static float RoofRisk(WearNTear w) => Bound && w != null ? s_roofRisk(w) : 0f;
        internal static float TestLoadRoof(WearNTear w, float target) { Bind(); return s_testLoad != null && w != null ? (float)s_testLoad.Invoke(null, new object[] { w, target }) : 0f; }

        // ---- gate
        private static bool s_active, s_gateSaid; private static float s_gateAt;
        internal static bool Active => s_active;
        private static void Gate(Vector3 centre)
        {
            if (Time.time < s_gateAt) return;
            s_gateAt = Time.time + GateEvery;
            var biome = Heightmap.FindBiome(centre);
            int deep = 0, n = 0;
            for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++) { n++; if (FiresCore.Snow.SnowField.Enabled && FiresCore.Snow.SnowField.DepthAt(centre + new Vector3(x * 8f, 0f, z * 8f)) * FiresCore.Snow.SnowField.MaxDepthMetres > 0.3f) deep++; }
            bool on = biome == Heightmap.Biome.Mountain || biome == Heightmap.Biome.DeepNorth || deep * 5 > n;
            if (on != s_active || !s_gateSaid) FiresCore.Logging.FiresLogger.LogDebug($"[Snow] snow chores {(on ? "ON" : "OFF")} at ({centre.x:0}, {centre.z:0}): biome {biome}, deep-snow cover {deep * 100 / n} % (needs Mountain / DeepNorth or over 20 %)"); s_gateSaid = true;
            s_active = on;
        }

        // ---- work
        private enum Kind { Roof, Path }
        private enum Step { GetShovel, ToChest, ToStation, ToWork, Work }
        private sealed class Job
        {
            public Kind Kind; public CompanionController C; public Step Step; public float StepAt; public bool Urgent;
            public WearNTear Piece; public float RiskBefore;
            public Vector3 Spot; public float DepthBefore; public int Left, Total;
            public Container Chest; public string NeedPrefab; public int NeedAmount; public CraftingStation Station;
        }
        private static Job s_job;
        private static float s_roofAt, s_pathAt;
        private static Runner s_runner;
        private static readonly List<WearNTear> s_roofs = new List<WearNTear>();
        internal static int RoofsCleared, PathsCleared;

        [HarmonyLib.HarmonyPatch(typeof(ZNet), "Awake")]
        private static class SnowChores_Boot { private static void Postfix() { try { EnsureRunner(); } catch { } } }

        private static void EnsureRunner()
        {
            if (s_runner != null) return;
            var go = new GameObject("FiresSnowChores");
            UnityEngine.Object.DontDestroyOnLoad(go);
            s_runner = go.AddComponent<Runner>();
        }

        private sealed class Runner : MonoBehaviour
        {
            private float m_tickAt;
            private void Update() { long pf = FiresCore.Diagnostics.CompanionSystemsProfile.Begin(); try { Tick0(); } finally { FiresCore.Diagnostics.CompanionSystemsProfile.End("snow runner", pf); } }
            private void Tick0()
            {
                if (!On || ZNet.instance == null) return;   // 0.2.930 (round #34 test: nothing ran on the client that owns the companions): any peer, its OWN companions only (Staying() checks IsOwner)
                if (FiresCore.Npc.Combat.HomesteadDefence.AnyAlert) { if (s_job != null) Abort("a threat in the base (defence first)"); return; }
                if (s_job != null) { Drive(); return; }
                if (Time.time < m_tickAt) return;
                m_tickAt = Time.time + 2f;
                if (!Bound) { if (!s_missingSaid) { s_missingSaid = true; FiresCore.Logging.FiresLogger.LogInfo("[Snow] snow chores need Vedr 0.8.153+ (VedrApi) or FiresTossinShade 0.3.268+ (FtsSnowApi); neither found: off"); } return; }
                Vector3 centre = s_testCentre ?? FiresCore.Npc.JobBoard.JobBoard.BaseCenter();
                if (centre == Vector3.zero) return;
                Gate(centre);
                if (!s_active) return;
                Next(centre);
            }
        }

        private static IEnumerable<CompanionController> Staying(Vector3 centre)
            => CompanionController.AllCompanions.Where(c => c != null && c.isTamed && !c.ShouldBeFollowing && c.GetCompanionAI() != null && !c.GetCompanionAI().IsInCombat
                    && c.GetComponent<ZNetView>() is ZNetView nv && nv.IsValid() && nv.IsOwner() && Vector3.Distance(c.transform.position, centre) < BaseRange + 30f);

        private static bool Free(CompanionController c)
        {
            if (s_testCentre != null) return true;
            var idle = c.GetComponent<FiresCore.Npc.CompanionIdleBehavior>();
            return idle == null || idle.ActiveSubBehavior == null;
        }

        private static string BusyWith(CompanionController c) => c.GetComponent<FiresCore.Npc.CompanionIdleBehavior>()?.ActiveSubBehavior?.BehaviorName;

        private static void Next(Vector3 centre)
        {
            // roofs every RoofEvery s; an urgent one takes the nearest companion whatever it is doing (except FireTending)
            if (Time.time >= s_roofAt)
            {
                s_roofAt = Time.time + RoofEvery;
                s_roofs.Clear();
                // 0.2.1221 [ghost]: vanilla's own WearNTear instance list (no scene search), distance by sqrMagnitude before RoofRisk
                var sw = FiresCore.Npc.Core.SceneScanCensus.Start();
                IList<WearNTear> all = FiresCore.Npc.Core.SceneScanCensus.UseVanillaLists ? (IList<WearNTear>)WearNTear.GetAllInstances() : UnityEngine.Object.FindObjectsByType<WearNTear>(FindObjectsSortMode.None);
                float br2 = BaseRange * BaseRange;
                for (int i = 0; i < all.Count; i++)
                {
                    var w = all[i];
                    if (w != null && (w.transform.position - centre).sqrMagnitude < br2 && RoofRisk(w) >= RoofAt) s_roofs.Add(w);
                }
                FiresCore.Npc.Core.SceneScanCensus.Note("SnowChores.Roofs", sw, all.Count);
                s_roofs.Sort((a, b) => RoofRisk(b).CompareTo(RoofRisk(a)));
                if (s_roofs.Count > 0) FiresCore.Logging.FiresLogger.LogDebug($"[Snow] roofs at risk at ({centre.x:0}, {centre.z:0}): {string.Join(", ", s_roofs.Take(6).Select(w => $"{Utils.GetPrefabName(w.gameObject)} {RoofRisk(w):0.00}"))}");
            }
            s_roofs.RemoveAll(w => w == null || RoofRisk(w) < RoofAt);
            var top = s_roofs.FirstOrDefault();
            if (top != null)
            {
                bool urgent = RoofRisk(top) >= RoofUrgent;
                var c = Staying(centre).Where(x => (urgent ? BusyWith(x) != "FireTending" : Free(x))).OrderBy(x => Vector3.Distance(x.transform.position, top.transform.position)).FirstOrDefault();
                if (c != null) { Start(new Job { Kind = Kind.Roof, C = c, Piece = top, RiskBefore = RoofRisk(top), Urgent = urgent }); return; }
            }
            // paths: free companions only
            if (Time.time < s_pathAt) return;
            s_pathAt = Time.time + 10f;
            var samples = PathSamples(centre);
            var deep = samples.Select(p => (p, d: FiresCore.Snow.SnowField.DepthAt(p) * FiresCore.Snow.SnowField.MaxDepthMetres)).Where(x => x.d > PathAt).OrderByDescending(x => x.d).ToList();
            if (deep.Count == 0) return;
            var worker = Staying(centre).Where(Free).OrderBy(x => Vector3.Distance(x.transform.position, deep[0].p)).FirstOrDefault();
            if (worker != null) Start(new Job { Kind = Kind.Path, C = worker, Spot = deep[0].p, DepthBefore = deep[0].d, Left = deep.Count, Total = samples.Count });
        }

        // 0.2.1221 [ghost]: vanilla keeps every live CraftingStation in its private static m_allStations (Awake adds, OnDestroy removes)
        private static readonly HarmonyLib.AccessTools.FieldRef<List<CraftingStation>> s_allStations = HarmonyLib.AccessTools.StaticFieldRefAccess<List<CraftingStation>>(HarmonyLib.AccessTools.Field(typeof(CraftingStation), "m_allStations"));
        private static List<CraftingStation> AllStations() => s_allStations() ?? new List<CraftingStation>();

        private static List<Vector3> PathSamples(Vector3 centre)
        {
            var doors = new List<Vector3>(); var spots = new List<Vector3>();
            // 0.2.1221 [ghost]: registries (HomesteadStations; vanilla's CraftingStation list via FindStationsInRange's backing list), not scene searches
            var sw = FiresCore.Npc.Core.SceneScanCensus.Start();
            bool reg = FiresCore.Npc.Core.SceneScanCensus.UseRegistries;
            IList<Door> doorList = reg ? (IList<Door>)FiresCore.Npc.JobBoard.HomesteadStations.Live(FiresCore.Npc.JobBoard.HomesteadStations.Doors) : UnityEngine.Object.FindObjectsByType<Door>(FindObjectsSortMode.None);
            IList<CraftingStation> csList = reg ? (IList<CraftingStation>)AllStations() : UnityEngine.Object.FindObjectsByType<CraftingStation>(FindObjectsSortMode.None);
            IList<Smelter> smList = reg ? (IList<Smelter>)FiresCore.Npc.JobBoard.HomesteadStations.Live(FiresCore.Npc.JobBoard.HomesteadStations.Smelters) : UnityEngine.Object.FindObjectsByType<Smelter>(FindObjectsSortMode.None);
            IList<Bed> bedList = reg ? (IList<Bed>)FiresCore.Npc.JobBoard.HomesteadStations.Live(FiresCore.Npc.JobBoard.HomesteadStations.Beds) : UnityEngine.Object.FindObjectsByType<Bed>(FindObjectsSortMode.None);
            foreach (var d in doorList)
                if (d != null && Vector3.Distance(d.transform.position, centre) < BaseRange) { doors.Add(d.transform.position); spots.Add(d.transform.position + d.transform.forward * 2f); spots.Add(d.transform.position - d.transform.forward * 2f); }
            foreach (var cs in csList) if (cs != null && Vector3.Distance(cs.transform.position, centre) < BaseRange) spots.Add(cs.transform.position);
            foreach (var sm in smList) if (sm != null && Vector3.Distance(sm.transform.position, centre) < BaseRange) spots.Add(sm.transform.position);
            foreach (var b in bedList) if (b != null && Vector3.Distance(b.transform.position, centre) < BaseRange) spots.Add(b.transform.position);
            FiresCore.Npc.Core.SceneScanCensus.Note("SnowChores.PathSamples", sw, doorList.Count + csList.Count + smList.Count + bedList.Count);
            foreach (var ct in FiresCore.Npc.Core.ContainerRegistry.GetNearby(centre, BaseRange)) if (ct != null) spots.Add(ct.transform.position);
            var samples = new List<Vector3>(spots);
            foreach (var door in doors)
                foreach (var s in spots.OrderBy(s => Vector3.Distance(s, door)).Skip(2).Take(3))
                {
                    float len = Vector3.Distance(door, s);
                    for (float t = 2f; t < len; t += 2f) samples.Add(Vector3.Lerp(door, s, t / len));
                }
            // ground points only (Core's field is a ground model): the walkable surface under each sample
            for (int i = 0; i < samples.Count; i++) { var p = samples[i]; if (ZoneSystem.instance != null) p.y = ZoneSystem.instance.GetGroundHeight(p); samples[i] = p; }
            return samples;
        }

        // ---- the shovel
        private static ItemDrop s_shovel;
        private static ItemDrop ShovelPrefab()
        {
            if (s_shovel != null || ObjectDB.instance == null) return s_shovel;
            foreach (var go in ObjectDB.instance.m_items) { var id = go != null ? go.GetComponent<ItemDrop>() : null; if (id != null && id.m_itemData?.m_shared?.m_name == ShovelShared) { s_shovel = id; break; } }
            return s_shovel;
        }
        private static Inventory Bag(CompanionController c) => c?.GetInventory()?.GetStorageInventory();
        private static int InBag(CompanionController c, string prefab) { var b = Bag(c); return b == null ? 0 : b.GetAllItems().Where(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == prefab).Sum(i => i.m_stack); }
        private static bool HasShovel(CompanionController c) { var b = Bag(c); return b != null && b.GetAllItems().Any(i => i?.m_shared?.m_name == ShovelShared); }

        private static void Start(Job j)
        {
            j.StepAt = Time.time;
            j.Step = HasShovel(j.C) ? Step.ToWork : Step.GetShovel;
            s_job = j;
            FiresCore.Logging.FiresLogger.LogDebug($"[Snow] {j.C.companionName} starts {(j.Kind == Kind.Roof ? $"roof {Utils.GetPrefabName(j.Piece.gameObject)} at {RouteMemory.At(j.Piece.transform.position)} (risk {j.RiskBefore:0.00}{(j.Urgent ? ", URGENT: jumps the queue" : "")})" : $"path at ({j.Spot.x:0}, {j.Spot.z:0}) depth {j.DepthBefore:0.00} m")}{(j.Step == Step.GetShovel ? "; fetching a snow shovel first" : "")}");
        }

        private static void Abort(string why)
        {
            if (s_job == null) return;
            try { s_job.C?.GetMovementAuthority()?.ReleaseAuthority("SnowChores"); } catch { }
            FiresCore.Logging.FiresLogger.LogDebug($"[Snow] {s_job.C?.companionName ?? "?"} skipped {(s_job.Kind == Kind.Roof && s_job.Piece != null ? $"roof {Utils.GetPrefabName(s_job.Piece.gameObject)}" : $"path ({s_job.Spot.x:0}, {s_job.Spot.z:0})")}: {why}");
            s_job = null;
        }

        private static bool WalkTo(Job j, Vector3 to, float reach)
        {
            var ai = j.C.GetCompanionAI();
            var src = j.Urgent || s_testCentre != null ? FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.PlayerCommand : FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.SubBehavior;
            return ai.RequestPathfindingMovement(to, j.Urgent, reach, src, "SnowChores") || Vector3.Distance(j.C.transform.position, to) <= reach;
        }

        private static void Drive()
        {
            var j = s_job; var c = j.C;
            if (c == null || c.GetCompanionAI() == null) { s_job = null; return; }
            if (Time.time - j.StepAt > 120f) { Abort($"{j.Step} took over 120 s"); return; }
            if (j.Kind == Kind.Roof && j.Piece == null) { Abort("the roof piece is gone"); return; }
            switch (j.Step)
            {
                case Step.GetShovel:
                {
                    var sp = ShovelPrefab();
                    if (sp == null) { Abort("no snow shovel item in this game (FTS / vanilla Deep North)"); return; }
                    var chest = FiresCore.Npc.Core.ContainerRegistry.GetClosestWithItem(c.transform.position, sp.gameObject.name, BaseRange + 30f);
                    if (chest != null) { j.Chest = chest; j.NeedPrefab = sp.gameObject.name; j.NeedAmount = 1; j.Station = null; j.Step = Step.ToChest; j.StepAt = Time.time; return; }
                    // craft: the recipe's materials from storage, then its station
                    var recipe = ObjectDB.instance.GetRecipe(sp.m_itemData);
                    if (recipe == null) { Abort("no snow shovel in storage and no recipe for one"); return; }
                    foreach (var r in recipe.m_resources)
                    {
                        if (r?.m_resItem == null) continue;
                        string pf = r.m_resItem.gameObject.name; int need = r.GetAmount(1);
                        if (InBag(c, pf) >= need) continue;
                        var mc = FiresCore.Npc.Core.ContainerRegistry.GetClosestWithItem(c.transform.position, pf, BaseRange + 30f);
                        if (mc == null || FiresCore.Npc.Core.ContainerRegistry.GetTotalItemCount(c.transform.position, pf, BaseRange + 30f) + InBag(c, pf) < need) { Abort($"no snow shovel in storage and not enough {pf} to craft one (needs {need})"); return; }
                        j.Chest = mc; j.NeedPrefab = pf; j.NeedAmount = need; j.Step = Step.ToChest; j.StepAt = Time.time; return;
                    }
                    string stationName = recipe.m_craftingStation != null ? recipe.m_craftingStation.m_name : null;
                    j.Station = stationName == null ? null : (FiresCore.Npc.Core.SceneScanCensus.UseVanillaLists ? (IEnumerable<CraftingStation>)AllStations().ToList() : UnityEngine.Object.FindObjectsByType<CraftingStation>(FindObjectsSortMode.None)).Where(s => s != null && s.m_name == stationName).OrderBy(s => Vector3.Distance(s.transform.position, c.transform.position)).FirstOrDefault();
                    if (stationName != null && j.Station == null) { Abort($"no {stationName} to craft a snow shovel"); return; }
                    j.Step = Step.ToStation; j.StepAt = Time.time; return;
                }
                case Step.ToChest:
                {
                    if (j.Chest == null) { j.Step = Step.GetShovel; return; }
                    Vector3 stand = FiresCore.Npc.Core.InteractionPointHelper.GetContainerInteractionPoint(j.Chest, c.transform.position);
                    if (!WalkTo(j, stand, 1.5f) && !FiresCore.Npc.Core.CompanionReach.InInteractReach(c.transform, j.Chest, out _)) return;
                    var bag = Bag(c);
                    var res = FiresCore.Npc.Core.InventoryTransferService.PullItem(j.Chest, bag, j.NeedPrefab, Math.Max(1, j.NeedAmount - InBag(c, j.NeedPrefab)));
                    if (res.Success) c.GetInventory()?.SaveToZDO();
                    var term = FiresCore.Npc.Core.InteractionPointHelper.TerminalUsedFor(j.Chest);
                    if (res.Success) FiresCore.Logging.FiresLogger.LogDebug(term != null ? $"[Chore] {c.companionName} used storage terminal at ({term.transform.position.x:0}, {term.transform.position.z:0}): took {j.NeedPrefab} x{res.AmountTransferred}" : $"[Snow] {c.companionName} took {j.NeedPrefab} x{res.AmountTransferred} from chest {RouteMemory.At(j.Chest.transform.position)}");
                    j.Chest = null;
                    j.Step = HasShovel(c) ? Step.ToWork : Step.GetShovel; j.StepAt = Time.time; return;
                }
                case Step.ToStation:
                {
                    var sp = ShovelPrefab(); var recipe = ObjectDB.instance.GetRecipe(sp.m_itemData); var bag = Bag(c);
                    if (j.Station != null && !WalkTo(j, j.Station.transform.position, 2.5f)) return;
                    foreach (var r in recipe.m_resources) if (r?.m_resItem != null && InBag(c, r.m_resItem.gameObject.name) < r.GetAmount(1)) { Abort($"short of {r.m_resItem.gameObject.name} at the station"); return; }
                    foreach (var r in recipe.m_resources) if (r?.m_resItem != null) bag.RemoveItem(r.m_resItem.m_itemData.m_shared.m_name, r.GetAmount(1));
                    bag.AddItem(sp.gameObject, 1);
                    c.GetInventory()?.SaveToZDO();
                    FiresCore.Logging.FiresLogger.LogDebug($"[Snow] {c.companionName} crafted a snow shovel at {(j.Station != null ? j.Station.m_name : "hand")} ({string.Join(", ", recipe.m_resources.Where(r => r?.m_resItem != null).Select(r => $"{r.m_resItem.gameObject.name} x{r.GetAmount(1)}"))})");
                    j.Step = Step.ToWork; j.StepAt = Time.time; return;
                }
                case Step.ToWork:
                {
                    Vector3 to = j.Kind == Kind.Path ? j.Spot : RoofStand(j);
                    if (!WalkTo(j, to, j.Kind == Kind.Path ? 1.2f : 1.5f)) { if (j.Kind == Kind.Roof && Vector3.Distance(c.transform.position + Vector3.up * 1.6f, j.Piece.transform.position) <= Reach) { j.Step = Step.Work; j.StepAt = Time.time; } return; }
                    j.Step = Step.Work; j.StepAt = Time.time; return;
                }
                case Step.Work:
                {
                    var body = c.GetComponent<Character>();
                    try { c.GetComponent<Humanoid>()?.m_zanim?.SetTrigger("swing_axe0"); } catch { }
                    if (j.Kind == Kind.Roof)
                    {
                        bool ok = s_clearRoof(body, j.Piece); float after = RoofRisk(j.Piece);
                        if (ok) { RoofsCleared++; FiresCore.Logging.FiresLogger.LogDebug($"[Snow] {c.companionName} cleared roof {Utils.GetPrefabName(j.Piece.gameObject)} at {RouteMemory.At(j.Piece.transform.position)}: risk {j.RiskBefore:0.00} -> {after:0.00}{(j.Urgent ? " (urgent)" : "")}"); }
                        else FiresCore.Logging.FiresLogger.LogDebug($"[Snow] {c.companionName} skipped roof {Utils.GetPrefabName(j.Piece.gameObject)}: FTS ClearRoof refused (risk {after:0.00}; see the [SnowApi] line)");
                        s_roofs.Remove(j.Piece);
                    }
                    else
                    {
                        // one swing does one thing ([saddles] 11-07): FAT first - it refuses while a snowy piece is there (ShovelHitsPiece),
                        // so a piece-covered spot falls through to Vedr/FTS ShovelAt, which clears the piece. When FAT dug the ground, ShovelAt
                        // is NOT called (it would add vanilla's ground op to the same swing).
                        bool ground = FatGround(body, j.Spot, out string groundHow);
                        bool piece = !ground && s_shovelAt(body, j.Spot);
                        bool ok = piece || ground;
                        float after = FiresCore.Snow.SnowField.DepthAt(j.Spot) * FiresCore.Snow.SnowField.MaxDepthMetres;
                        if (ok) PathsCleared++;
                        FiresCore.Logging.FiresLogger.LogDebug($"[Snow] {c.companionName} {(ok ? "cleared" : "could not clear")} path ({j.Spot.x:0}, {j.Spot.z:0}) depth {j.DepthBefore:0.00} -> {after:0.00} m ({j.Left - (ok ? 1 : 0)} of {j.Total} left; FAT ground {groundHow}, {(ground ? "Vedr/FTS ShovelAt not called" : $"{(BoundApi.StartsWith("Vedr.") ? "Vedr" : "FTS")} ShovelAt {(piece ? "shovelled" : "did nothing")}")})");
                    }
                    try { c.GetMovementAuthority()?.ReleaseAuthority("SnowChores"); } catch { }
                    s_job = null; return;
                }
            }
        }

        // a stand point within reach of the roof piece: beside / below it (TryGetPathReachPoint's eye-reach candidates), never ON a piece at >= 0.95
        private static Vector3 RoofStand(Job j)
        {
            var w = j.Piece;
            if (FiresCore.Npc.Core.InteractionPointHelper.TryGetPathReachPoint(w.gameObject, w.transform.position, j.C.transform.position, 1.5f, out Vector3 p))
            {
                if (RoofRisk(w) < RoofNoStand || Mathf.Abs(p.y - w.transform.position.y) > 0.6f) return p;
            }
            return w.transform.position + Vector3.down * 2f;
        }

        internal static void ClearForTest() { s_job = null; s_roofs.Clear(); s_active = false; s_gateSaid = false; s_roofAt = 0f; s_pathAt = 0f; s_gateAt = 0f; RoofsCleared = PathsCleared = 0; }
    }
}
