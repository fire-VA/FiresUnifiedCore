using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// 0.2.909 ([ghost], Fire 10-27: "All of the jobs priority levels should be dynamic based off of what the homestead actually needs.... Or
    /// the resources that it is actually lower on"; plan Tools\DYNAMIC_PRIORITIES_PLAN_20261027.md, step 1 of 5). The homestead NEEDS model,
    /// LOG-ONLY in this step (nothing reads it yet). Rebuilt every 10 s on the owner's client for the homestead (the board's base centre),
    /// from real state:
    ///  - products judged by the FINISHED item, raw stock counted as on the way (Fire's bars rule): bars = bars in stock + matching ore in the
    ///    chests / bags + ore queued in the smelters; coal = coal + wood queued in kilns; cooked food = meals + their raw inputs; crops =
    ///    produce + seedlings growing at the base;
    ///  - targets (Fire: about half the first proposal): 25 per bar a homestead smelter makes, coal 50, wood 100 / fine wood 30 / core wood 20,
    ///    stone 50, 30 per crop the base can plant, 10 per food its cooking stations make; "[Homestead Needs] Targets" (item=amount, ...)
    ///    overrides them (per homestead targets come with the Job Board Needs panel step);
    ///  - shortage = clamp01(1 - effective / target);
    ///  - stations: fires (fuel %), smelters / kilns (queued input, fuel), ripe crops.
    /// One Info line every 60 s: "[Homestead] needs at (x, z): IronBar 4/25 +6 on the way (0.60), ...; stations: ...".
    /// </summary>
    internal static class HomesteadNeeds
    {
        internal sealed class Need { public string Item, Kind; public int Stock, OnTheWay, Target; public float Shortage; }
        internal sealed class Snapshot
        {
            public Vector3 Home; public float At;
            public readonly Dictionary<string, Need> Items = new Dictionary<string, Need>();
            public string Stations = "";
            public int FiresLow, Fires, RipeCrops;
        }

        private const float Radius = 60f, RebuildEvery = 10f, SayEvery = 60f;
        private static Snapshot s_last;
        private static float s_sayAt;
        // 1086 ([generator], Fire 11-01: configs live in their own mods): [Homestead Needs] is bound by FiresCompanions; Core reads the owner's value (old default without it).
        internal static void BindConfig(BepInEx.Configuration.ConfigFile cfg) { }

        /// <summary>The newest snapshot (null before the first build).</summary>
        internal static Snapshot Last => s_last;

        // 0.2.1222 ([ghost], public bug report: a 33-90 ms hitch every 10 s even with no companions and need-based chores off).
        // The model is only read by NeedFor (scores on) and the summary log, so it builds only when one of those is on AND the local
        // player has a tamed companion; otherwise it does nothing and drops its snapshot. "Needs model" (Core-side, default on) turns it
        // off outright. The rebuild runs one phase per Job Board tick (2 s apart) over the station registry (HomesteadStations), never a
        // scene search, and publishes the snapshot when the last phase ends.
        private static Builder s_building;
        private static float s_censusAt;
        private static string s_skip = "";
        private static double s_phaseWorstMs;

        private static bool HasCompanion(long me)
        {
            foreach (var c in CompanionController.AllCompanions) if (c != null && c.isTamed && c.ownerPlayerId == me) return true;
            return false;
        }

        /// <summary>Called from the Job Board tick (every 2 s on the owner's client); one rebuild phase per call, says every 60 s.</summary>
        internal static void Tick()
        {
            var p = Player.m_localPlayer;
            if (p == null) return;
            bool say = FiresCore.Config.CoreSettings.Get("Homestead Needs", "Log needs summary", false);
            string skip = !FiresCore.Config.CoreSettings.Get("Homestead Needs", "Needs model", true) ? "Needs model off"
                : !ScoresOn && !say ? "need-based chore scores off" : !HasCompanion(p.GetPlayerID()) ? "no tamed companion" : null;
            if (skip != null)
            {
                if (skip != s_skip) { s_skip = skip; FiresCore.Logging.FiresLogger.LogDebug($"[Homestead] needs model idle ({skip}): no station walk"); }
                s_last = null; s_building = null;
                return;
            }
            if (s_skip != "") { s_skip = ""; FiresCore.Logging.FiresLogger.LogDebug("[Homestead] needs model active"); }
            if (s_building == null)
            {
                if (s_last != null && Time.time - s_last.At < RebuildEvery) return;
                s_building = new Builder(JobBoard.BaseCenter());
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool done;
            try { done = s_building.Step(); }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[Homestead] needs build failed: " + e.Message); s_building = null; return; }
            double ms = sw.Elapsed.TotalMilliseconds; if (ms > s_phaseWorstMs) s_phaseWorstMs = ms;
            if (!done) return;
            s_last = s_building.Snap; s_building = null;
            if (Time.time >= s_censusAt)
            {
                s_censusAt = Time.time + SayEvery;
                FiresCore.Logging.FiresLogger.LogDebug($"[Homestead] needs census: {HomesteadStations.Census()}; worst phase {s_phaseWorstMs:0.00} ms (last 60 s)");
                s_phaseWorstMs = 0;
            }
            if (say && Time.time >= s_sayAt) { s_sayAt = Time.time + SayEvery; FiresCore.Logging.FiresLogger.LogDebug(Line(s_last)); }
        }

        private static Dictionary<string, int> Overrides()
        {
            var r = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in FiresCore.Config.CoreSettings.Get("Homestead Needs", "Targets", "").Split(','))
            {
                var kv = part.Split('=');
                if (kv.Length == 2 && int.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) r[kv[0].Trim()] = Mathf.Max(0, n);
            }
            return r;
        }

        private static int Stock(Vector3 home, string prefab, List<CompanionController> comps)
        {
            if (string.IsNullOrEmpty(prefab)) return 0;
            int n = FiresCore.Npc.Core.ContainerRegistry.GetTotalItemCount(home, prefab, Radius);
            foreach (var c in comps)
            {
                var bag = c != null ? c.GetInventory()?.GetStorageInventory() : null;
                if (bag != null) n += bag.GetAllItems().Where(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == prefab).Sum(i => i.m_stack);
            }
            return n;
        }

        private static List<string> Queue(ZDO zdo)
        {
            var r = new List<string>();
            if (zdo == null) return r;
            int q = zdo.GetInt(ZDOVars.s_queued, 0);
            for (int i = 0; i < q && i < 100; i++) { string s = zdo.GetString("item" + i); if (!string.IsNullOrEmpty(s)) r.Add(s); }
            return r;
        }

        /// <summary>The whole build in one call (diagnostic tests); the live path steps it across Job Board ticks.</summary>
        internal static Snapshot Build(Vector3 home)
        {
            var b = new Builder(home);
            while (!b.Step()) { }
            return b.Snap;
        }

        /// <summary>0.2.1222: the needs build in phases (smelters, crops, cooking, fires, totals), each over the registry's near list.</summary>
        private sealed class Builder
        {
            internal readonly Snapshot Snap;
            private readonly Vector3 home;
            private readonly List<CompanionController> comps;
            private readonly Dictionary<string, int> over;
            private readonly List<string> stations = new List<string>();
            private int phase;

            internal Builder(Vector3 home)
            {
                this.home = home;
                Snap = new Snapshot { Home = home, At = Time.time };
                var p = Player.m_localPlayer; long me = p != null ? p.GetPlayerID() : 0L;
                comps = CompanionController.AllCompanions.Where(c => c != null && c.isTamed && c.ownerPlayerId == me).ToList();
                over = Overrides();
            }

            private void Add(string item, string kind, int target, int onTheWay)
            {
                if (string.IsNullOrEmpty(item)) return;
                if (!Snap.Items.TryGetValue(item, out var n)) { n = new Need { Item = item, Kind = kind, Target = target }; Snap.Items[item] = n; }
                n.OnTheWay += onTheWay;
                if (target > n.Target) n.Target = target;
            }

            /// <summary>Runs one phase; true when the snapshot is complete.</summary>
            internal bool Step()
            {
                switch (phase++)
                {
                    case 0: Smelters(); return false;
                    case 1: Crops(); return false;
                    case 2: Cooking(); return false;
                    case 3: FiresPhase(); return false;
                    default: Totals(); return true;
                }
            }

            private void Smelters()
            {
            var snap = Snap;
            var near = new List<Smelter>(); HomesteadStations.Near(HomesteadStations.Smelters, home, Radius, near);
            // smelters (ore -> bars) and kilns (wood -> coal): targets from what they make, queued input on the way
            foreach (var st in near)
            {
                var zdo = st.GetComponent<ZNetView>()?.GetZDO();
                var queue = Queue(zdo);
                bool burns = st.m_maxFuel > 0 && st.m_fuelItem != null;
                foreach (var cv in st.m_conversion)
                {
                    if (cv?.m_to == null || cv.m_from == null) continue;
                    string to = cv.m_to.gameObject.name, from = cv.m_from.gameObject.name;
                    int queued = queue.Count(q => q == from);
                    if (burns) { int raw = queued + Stock(home, from, comps); if (raw > 0 || Stock(home, to, comps) > 0) Add(to, "bars", 25, raw); }   // 0.2.913: a target only with a path to it (product, its ore, or queued)
                    else if (to == "Coal") Add(to, "coal", 50, queued);
                }
                if (burns && st.m_fuelItem != null) Add(st.m_fuelItem.gameObject.name, "fuel", 50, 0);
                float fuel = zdo != null ? zdo.GetFloat(ZDOVars.s_fuel, 0f) : 0f;
                stations.Add($"{Utils.GetPrefabName(st.gameObject)} ({st.transform.position.x:0}, {st.transform.position.z:0}) in {queue.Count}/{st.m_maxOre}" + (burns ? $" fuel {fuel:0.#}/{st.m_maxFuel}" : ""));
            }
            }

            private void Crops()
            {
            // wood + stone always; crops the base can plant; food its cooking stations make
            Add("Wood", "wood", 100, 0); Add("FineWood", "wood", 30, 0); Add("RoundLog", "wood", 20, 0); Add("Stone", "stone", 50, 0);
            var nearPlants = new List<Plant>(); HomesteadStations.Near(HomesteadStations.Plants, home, Radius, nearPlants);
            var plants = nearPlants.Select(pl => Utils.GetPrefabName(pl.gameObject)).ToList();
            foreach (var crop in FarmingDataHelper.AllSaplings())
            {
                var produce = crop?.Plant?.m_grownPrefabs?.Select(g => g != null ? g.GetComponent<Pickable>() : null).FirstOrDefault(pk => pk != null && pk.m_itemPrefab != null);
                if (produce == null) continue;
                string seed = crop.Seeds.Length > 0 ? crop.Seeds[0].Item : null;
                if (Stock(home, seed, comps) == 0 && Stock(home, produce.m_itemPrefab.name, comps) == 0) continue;   // a crop this base can't plant
                int growing = plants.Count(n => n == crop.Prefab.name);
                Add(produce.m_itemPrefab.name, "crop", 30, growing);
            }
            }

            private void Cooking()
            {
            var near = new List<CookingStation>(); HomesteadStations.Near(HomesteadStations.Cooking, home, Radius, near);
            foreach (var cs in near)
            {
                foreach (var cv in cs.m_conversion) if (cv?.m_to != null && cv.m_from != null) { int raw = Stock(home, cv.m_from.gameObject.name, comps); if (raw > 0 || Stock(home, cv.m_to.gameObject.name, comps) > 0) Add(cv.m_to.gameObject.name, "food", 10, raw); }   // 0.2.913: only foods the base has the raw for (round #30 listed every Ashlands meat)
            }
            }

            private void FiresPhase()
            {
            var snap = Snap;
            var near = new List<Fireplace>(); HomesteadStations.Near(HomesteadStations.Fires, home, Radius, near);
            foreach (var fp in near)
            {
                if (fp.m_maxFuel <= 0f || FiresCore.Npc.Core.ChestReach.IsUnreachable(fp)) continue;   // 0.2.930: an unreachable fire is not a need
                var zdo = fp.GetComponent<ZNetView>()?.GetZDO();
                float f = zdo != null ? zdo.GetFloat(ZDOVars.s_fuel, 0f) : 0f;
                snap.Fires++;
                if (f / fp.m_maxFuel < 0.25f) snap.FiresLow++;
            }
            try { snap.RipeCrops = FarmingDataHelper.FindNearbyHarvestableCrops(home, Radius).Count; } catch { }
            }

            private void Totals()
            {
            var snap = Snap;
            foreach (var n in snap.Items.Values)
            {
                if (over.TryGetValue(n.Item, out int t)) n.Target = t;
                n.Stock = Stock(home, n.Item, comps);
                int eff = n.Stock + n.OnTheWay;
                n.Shortage = n.Target <= 0 ? 0f : Mathf.Clamp01(1f - eff / (float)n.Target);
            }
            snap.Stations = string.Join(", ", stations.Take(6));
            }
        }

        // ---- step 2 (0.2.913): need-based chore scores ---------------------------------------------------------------------------
        // score = the chore's own priority + weight x need (0..1) [+ the existing -25 per nearby companion on it]; the pick stays the one in
        // CompanionIdleBehavior (no parallel system). Weights (plan section 2): FireTending 20, SmelterOperator 16, ResourceGathering 14,
        // WoodGathering 12, Farming 12, CompanionCooking 10; "[Homestead Needs] Weights" overrides.
        // 1086 ([generator], Fire 11-01: configs live in their own mods): owner-bound (FiresCompanions); chore picks read it per pick
        private static readonly FiresCore.Config.OwnedSetting<bool> s_scores = new FiresCore.Config.OwnedSetting<bool>("Homestead Needs", "Need-based chore scores", false);
        private static readonly Dictionary<string, int> DefaultWeights = new Dictionary<string, int>
        {
            { "FireTending", 20 }, { "SmelterOperator", 16 }, { "ResourceGathering", 14 }, { "WoodGathering", 12 }, { "Farming", 12 }, { "CompanionCooking", 10 },
        };

        internal static void BindScores(BepInEx.Configuration.ConfigFile cfg)
        { }   // 1086 ([generator], Fire 11-01: configs live in their own mods): nothing to bind

        internal static bool ScoresOn => s_scores.Value;

        private static int Weight(string chore)
        {
            foreach (var part in FiresCore.Config.CoreSettings.Get("Homestead Needs", "Weights", "").Split(','))
            {
                var kv = part.Split('=');
                if (kv.Length == 2 && kv[0].Trim() == chore && int.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)) return Mathf.Clamp(w, 0, 100);
            }
            return DefaultWeights.TryGetValue(chore, out int d) ? d : 0;
        }

        /// <summary>The need points for a chore (0 when scores are off or nothing is short) and why, from the newest snapshot.</summary>
        internal static int NeedFor(string chore, out string why)
        {
            why = null;
            var s = s_last;
            if (!ScoresOn || s == null) return 0;
            int w = Weight(chore);
            if (w <= 0) return 0;
            Need top = null; float need = 0f; string extra = null;
            Need Worst(Func<Need, bool> f) => s.Items.Values.Where(f).OrderByDescending(n => n.Shortage).FirstOrDefault();
            switch (chore)
            {
                case "FireTending":
                    if (s.Fires > 0 && s.FiresLow > 0) { need = 1f; extra = $"{s.FiresLow}/{s.Fires} fire(s) under 25 % fuel"; }
                    break;
                case "SmelterOperator":
                    if (s.Stations.Contains("fuel 0/")) { need = 1f; extra = "a smelter with no fuel"; }
                    else { top = Worst(n => n.Kind == "bars" || n.Kind == "coal"); need = top?.Shortage ?? 0f; }
                    break;
                case "ResourceGathering":
                    top = Worst(n => n.Kind == "bars" || n.Kind == "stone"); need = top?.Shortage ?? 0f;
                    break;
                case "WoodGathering":
                    top = Worst(n => n.Kind == "wood"); need = top?.Shortage ?? 0f;
                    var coal = Worst(n => n.Kind == "coal"); if (coal != null && coal.Shortage > need) { top = coal; need = coal.Shortage; }
                    break;
                case "Farming":
                    if (s.RipeCrops > 0) { need = 1f; extra = $"{s.RipeCrops} ripe crop(s)"; }
                    else { top = Worst(n => n.Kind == "crop"); need = top?.Shortage ?? 0f; }
                    break;
                case "CompanionCooking":
                    top = Worst(n => n.Kind == "food"); need = top?.Shortage ?? 0f;
                    break;
            }
            if (need < 0.1f) return 0;   // a 10 % dead band: a stock bouncing round its target flips nothing
            int pts = Mathf.RoundToInt(w * need);
            why = extra ?? (top != null ? $"{top.Item} {top.Stock}{(top.OnTheWay > 0 ? $"+{top.OnTheWay}" : "")}/{top.Target}" : null);
            return pts;
        }

        internal static string Line(Snapshot s)
        {
            var parts = s.Items.Values.OrderByDescending(n => n.Shortage).ThenBy(n => n.Item).Take(14)
                .Select(n => $"{n.Item} {n.Stock}{(n.OnTheWay > 0 ? $"+{n.OnTheWay}" : "")}/{n.Target} ({n.Shortage:0.00})");
            return $"[Homestead] needs at ({s.Home.x:0}, {s.Home.z:0}): {string.Join(", ", parts)}; fires {s.Fires - s.FiresLow}/{s.Fires} ok, ripe crops {s.RipeCrops}; stations: {(s.Stations.Length > 0 ? s.Stations : "none")}";
        }
    }
}
