using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// 0.2.890 ([ghost], Fire 10-27: "Assigning a circle as a logging camp should cause the companions to use the tree seeds ... Placement of
    /// the seeds and saplings would link to their gardening skill ... The same concept should be applied to mining camps"): replanting inside
    /// a homestead-map work zone during its board run.
    ///  - Seeds: tree seeds (whatever the Cultivator plants as a tree: BeechSeeds, FirCone, PineCone, BirchSeeds, Acorn, ...) or Core's
    ///    MineralSeed_&lt;ore&gt;. At the run's start (at base) up to <see cref="PullMax"/> are taken from the base chests into the bag; planting uses the bag.
    ///  - After each stump cleared (logging) / rock mined out (mining) inside the zone: one sapling, placed by the Farming skill:
    ///    spacing = grow radius x lerp(1.0, 1.6, skill/100) on a hex-offset grid; below skill 30 the spot jitters (+-(1 - skill/100) x spacing)
    ///    and a low-skill planter sometimes plants a spot vanilla's grow-space test rejects (it won't grow); skill 60+ tries 3 spots and keeps
    ///    the roomiest. Biome via CanGrowAt, room via GrowSpaceFree (FarmingDataHelper, the farmer's own checks). Each success raises Farming.
    /// Line: "[Chore] replant &lt;zone&gt;: &lt;name&gt; planted &lt;seed&gt; at x,z (skill s -> spacing d m, ok | failed: why)".
    /// </summary>
    internal static class ZoneReplant
    {
        private const int PullMax = 6;
        private const int PerRun = 2;   // 0.2.897: saplings per camp run (stump / rock plants + the run-end top-up)
        private static readonly Dictionary<CompanionController, int> s_plantedThisRun = new Dictionary<CompanionController, int>();
        private const float ChestRadius = 30f, NearCompanion = 8f;
        private const string MineralSeedPrefix = "MineralSeed_";

        private static bool IsMineral(string seed) => seed.StartsWith(MineralSeedPrefix);

        private static IEnumerable<string> SeedsFor(bool mineral)
            => FarmingDataHelper.GroundSeedItems().Where(s => IsMineral(s) == mineral);

        private static float Skill(CompanionController c) => c?.GetSkills()?.GetSkillLevel(Skills.SkillType.Farming) ?? 0f;

        /// <summary>At the zone run's start (the companion is at base): seeds from the base chests into its bag.</summary>
        internal static void PullSeeds(CompanionController c, string focus, Vector3 home, string zone)
        {
            if (c == null || (focus != "chop" && focus != "mine")) return;
            s_plantedThisRun[c] = 0;
            var bag = c.GetInventory()?.GetStorageInventory();
            if (bag == null) return;
            bool mineral = focus == "mine";
            var kinds = new HashSet<string>(SeedsFor(mineral));
            int have = bag.GetAllItems().Where(i => i?.m_dropPrefab != null && kinds.Contains(i.m_dropPrefab.name)).Sum(i => i.m_stack);
            int took = 0;
            foreach (var chest in ChestHelper.FindNearbyChests(home, ChestRadius))
            {
                var inv = chest?.GetInventory();
                if (inv == null) continue;
                foreach (var it in inv.GetAllItems().ToList())
                {
                    if (have + took >= PullMax) break;
                    if (it?.m_dropPrefab == null || !kinds.Contains(it.m_dropPrefab.name)) continue;
                    int n = Mathf.Min(it.m_stack, PullMax - have - took);
                    var copy = it.Clone(); copy.m_stack = n;
                    if (!bag.CanAddItem(copy) || !bag.AddItem(copy)) continue;
                    inv.RemoveItem(it, n);
                    took += n;
                }
            }
            FiresCore.Logging.FiresLogger.LogDebug($"[Chore] replant {zone}: {c.GetDisplayName()} carries {have + took} {(mineral ? "mineral" : "tree")} seed(s) ({took} from the base chests)");
        }

        /// <summary>After a harvest inside the zone: plant one sapling near it. Returns true when one was planted.</summary>
        internal static bool AfterHarvest(CompanionController c, CompanionIdleBehavior idle, bool mineral, Vector3 near)
        {
            if (c == null || idle == null || !idle.HasWorkZone) return false;
            string zone = idle.WorkZoneName;
            var bag = c.GetInventory()?.GetStorageInventory();
            if (bag == null) return false;
            var kinds = new HashSet<string>(SeedsFor(mineral));
            Vector3 centre = idle.WorkZoneCenter; float radius = idle.WorkZoneRadius;
            float skill = Skill(c), k = Mathf.Clamp01(skill / 100f);
            string why = null;
            foreach (var item in bag.GetAllItems().Where(i => i?.m_dropPrefab != null && kinds.Contains(i.m_dropPrefab.name)).ToList())
            {
                string seed = item.m_dropPrefab.name;
                foreach (var crop in FarmingDataHelper.GroundSaplingsForSeed(seed))
                {
                    int cost = crop.Seeds.Where(s => s.Item == seed).Select(s => s.Amount).DefaultIfEmpty(1).First();
                    if (bag.CountItems(item.m_shared.m_name) < cost) { why = $"needs {cost} {seed}"; continue; }
                    float grow = crop.Plant.m_growRadius > 0f ? crop.Plant.m_growRadius : 0.5f;
                    float spacing = grow * 2f * Mathf.Lerp(1f, 1.6f, k);
                    if (!Pick(crop, centre, radius, near, spacing, skill, out Vector3 spot, out bool bad, out why)) continue;
                    var go = FarmingDataHelper.PlantSapling(crop, spot);
                    if (go == null) { why = "spawn failed"; continue; }
                    bag.RemoveItem(item.m_shared.m_name, cost);
                    c.GetSkills()?.RaiseSkill(Skills.SkillType.Farming, bad ? 0.25f : 1f);
                    s_plantedThisRun[c] = (s_plantedThisRun.TryGetValue(c, out int done0) ? done0 : 0) + 1;
                    FiresCore.Logging.FiresLogger.LogDebug($"[Chore] replant {zone}: {c.GetDisplayName()} planted {seed} -> {crop.Prefab.name} at {spot.x:0.0},{spot.z:0.0} (skill {skill:0} -> spacing {spacing:0.0} m, {(bad ? "failed: " + why + " (a poor spot: it won't grow)" : "ok")})");
                    return true;
                }
            }
            FiresCore.Logging.FiresLogger.LogDebug($"[Chore] replant {zone}: {c.GetDisplayName()} planted nothing (skill {skill:0}; {(why ?? $"no {(mineral ? "mineral" : "tree")} seeds in the bag")})");
            return false;
        }

        /// <summary>0.2.897: a camp run that ended well tops up to <see cref="PerRun"/> saplings where the companion stands (inside the zone).</summary>
        internal static void AtRunEnd(CompanionController c, CompanionIdleBehavior idle)
        {
            if (c == null || idle == null || !idle.HasWorkZone) return;
            bool mineral = idle.WorkZoneFocus == "mine";
            int done = s_plantedThisRun.TryGetValue(c, out int d) ? d : 0;
            Vector3 at = c.transform.position;
            Vector3 off = at - idle.WorkZoneCenter; off.y = 0f;
            if (done >= PerRun) { FiresCore.Logging.FiresLogger.LogDebug($"[Chore] replant {idle.WorkZoneName}: {c.GetDisplayName()} run end, {done} already planted this run"); return; }
            if (off.magnitude > idle.WorkZoneRadius)
            {
                FiresCore.Logging.FiresLogger.LogDebug($"[Chore] replant {idle.WorkZoneName}: {c.GetDisplayName()} run end, planted nothing (ended {off.magnitude:0} m from the centre, outside its {idle.WorkZoneRadius:0} m)");
                return;
            }
            FiresCore.Logging.FiresLogger.LogDebug($"[Chore] replant {idle.WorkZoneName}: {c.GetDisplayName()} run end at ({at.x:0}, {at.z:0}), {done} planted so far, topping up to {PerRun}");
            for (int i = done; i < PerRun; i++) if (!AfterHarvest(c, idle, mineral, at)) break;
        }

        // 0.2.897 (lead, Fire's "replant what you cut"): per run, standing trees / rocks inside the zone at the start vs the end, and
        // saplings planted, so Fire can see whether the camp keeps up with the cutting
        private static readonly Dictionary<CompanionController, int> s_standingAtStart = new Dictionary<CompanionController, int>();

        private static int Standing(string focus, Vector3 centre, float radius)
        {
            var seen = new HashSet<GameObject>();
            foreach (var col in Physics.OverlapSphere(centre, radius))
            {
                if (col == null) continue;
                Component k = focus == "mine" ? (Component)col.GetComponentInParent<MineRock5>() ?? col.GetComponentInParent<MineRock>() : col.GetComponentInParent<TreeBase>();
                if (k != null) seen.Add(k.gameObject);
            }
            return seen.Count;
        }

        internal static void RunStart(CompanionController c, string focus, Vector3 centre, float radius)
        {
            if (c == null || (focus != "chop" && focus != "mine")) return;
            s_standingAtStart[c] = Standing(focus, centre, radius);
        }

        internal static void RunSummary(CompanionController c, CompanionIdleBehavior idle, string how)
        {
            if (c == null || idle == null || !idle.HasWorkZone) return;
            bool mine = idle.WorkZoneFocus == "mine";
            int before = s_standingAtStart.TryGetValue(c, out int b) ? b : -1;
            int after = Standing(idle.WorkZoneFocus, idle.WorkZoneCenter, idle.WorkZoneRadius);
            int planted = s_plantedThisRun.TryGetValue(c, out int p) ? p : 0;
            int cut = before >= 0 ? Mathf.Max(0, before - after) : -1;
            string what = mine ? "rock(s) mined out" : "tree(s) felled";
            string verdict = cut < 0 ? "start count unknown" : planted >= cut ? "keeps up" : $"short by {cut - planted}";
            FiresCore.Logging.FiresLogger.LogDebug($"[Chore] replant {idle.WorkZoneName}: {c.GetDisplayName()} run summary ({how}): {(cut >= 0 ? cut.ToString() : "?")} {what}, {planted} sapling(s) planted - {verdict} (standing {(before >= 0 ? before.ToString() : "?")} -> {after})");
            s_standingAtStart.Remove(c); s_plantedThisRun.Remove(c);
        }

        /// <summary>A spot inside the zone, close to the harvest, by skill. <paramref name="bad"/> = a low-skill plant in a rejected spot.</summary>
        private static bool Pick(FarmingDataHelper.CropSapling crop, Vector3 centre, float radius, Vector3 near, float spacing, float skill,
                                 out Vector3 spot, out bool bad, out string why)
        {
            spot = Vector3.zero; bad = false; why = "no free spot near the harvest inside the zone";
            float k = Mathf.Clamp01(skill / 100f);
            // hex-offset grid anchored on the zone centre, cells nearest the harvest first
            var cells = new List<Vector3>();
            int n = Mathf.CeilToInt(NearCompanion / spacing) + 1;
            float rowH = spacing * 0.866f;
            int r0 = Mathf.RoundToInt((near.z - centre.z) / rowH), c0 = Mathf.RoundToInt((near.x - centre.x) / spacing);
            for (int r = r0 - n; r <= r0 + n; r++)
                for (int q = c0 - n; q <= c0 + n; q++)
                {
                    var p = new Vector3(centre.x + q * spacing + ((r & 1) != 0 ? spacing * 0.5f : 0f), near.y, centre.z + r * rowH);
                    if (skill < 30f) { float j = (1f - k) * spacing; p.x += Random.Range(-j, j); p.z += Random.Range(-j, j); }
                    Vector3 d = p - centre; d.y = 0f;
                    Vector3 e = p - near; e.y = 0f;
                    if (d.magnitude > radius - 0.5f || e.magnitude > NearCompanion) continue;
                    cells.Add(p);
                }
            cells = cells.OrderBy(p => (p - near).sqrMagnitude).ToList();
            int tries = skill >= 60f ? 3 : 1, found = 0; float bestRoom = -1f;
            string lastWhy = null;
            foreach (var p0 in cells)
            {
                var p = p0;
                if (FiresCore.World.Surface.GroundNear(p, out float h)) p.y = h;
                if (!FarmingDataHelper.CanGrowAt(crop, p)) { lastWhy = $"{crop.Prefab.name} doesn't grow in {Heightmap.FindBiome(p)}"; continue; }
                if (!FarmingDataHelper.GrowSpaceFree(p, crop, out string w))
                {
                    lastWhy = w;
                    // a low-skill planter sometimes doesn't see it: plants anyway (vanilla's grow test fails it later)
                    if (skill < 30f && Random.value < (1f - skill / 30f) * 0.5f) { spot = p; bad = true; why = w; return true; }
                    continue;
                }
                float room = Room(p);
                if (room > bestRoom) { bestRoom = room; spot = p; }
                if (++found >= tries) break;
            }
            if (bestRoom >= 0f) { why = null; return true; }
            why = lastWhy ?? why;
            return false;
        }

        /// <summary>Distance to the nearest plant / tree / rock collider (capped), the "roomiest spot" score.</summary>
        private static float Room(Vector3 p)
        {
            float best = 6f;
            foreach (var col in Physics.OverlapSphere(p, 6f))
            {
                if (col == null || col.GetComponentInParent<Character>() != null || col.GetComponentInParent<Heightmap>() != null) continue;
                Vector3 d = col.ClosestPoint(p) - p; d.y = 0f;
                best = Mathf.Min(best, d.magnitude);
            }
            return best;
        }
    }
}
