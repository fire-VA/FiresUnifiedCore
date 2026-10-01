using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc;
using FiresCore.Npc.Core;
using FiresCore.Npc.IdleBehaviors;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Base upkeep for any body (Core 0.2.234; Fire 23:0x: "a primary fix needs to be the 'one brain' thing we talked about earlier with
    /// companions/bots because we have SO MANY working chores for companions and the bots suck at working around base"). The chores a
    /// base-watching companion rotates through (CompanionIdleBehavior's sub-behaviours), handed out one <see cref="TaskOrder"/> at a time
    /// for any <see cref="ITaskBody"/> (the FDT bot's player body, a companion), on the Core helpers the companions use (ChestHelper,
    /// FarmingDataHelper, PieceDataHelper). In companion priority: fires, smelters / kilns, loose drops, crops and hives, replanting,
    /// damaged pieces, stashing the bag into base chests, worn carried gear.
    /// </summary>
    public static partial class ChoreBrain
    {
        /// <summary>Which base chores <see cref="NextBaseChore(ITaskBody, Vector3, float, BaseChores, out string)"/> may hand out.</summary>
        [Flags]
        public enum BaseChores
        {
            None = 0,
            /// <summary>Fires under half their fuel (FireTendingBehaviorV2).</summary>
            Fuel = 1,
            /// <summary>Smelters, kilns, charcoal: fuel and ore (SmelterOperatorBehavior).</summary>
            Smelters = 2,
            /// <summary>Loose drops near the base, never a player's own drops (LootPickupBehaviorV2).</summary>
            Pickups = 4,
            /// <summary>Ripe crops and beehives with honey (FarmingBehavior).</summary>
            Harvest = 8,
            /// <summary>Seeds onto free cultivated ground, with a Cultivator carried (FarmingBehavior).</summary>
            Plant = 16,
            /// <summary>Damaged player-built pieces, with a Hammer carried (BuildingRepairBehavior).</summary>
            RepairPieces = 32,
            /// <summary>The bag into base chests (ChestDepositBehaviorV2).</summary>
            Deposit = 64,
            /// <summary>Worn carried gear at a station (<see cref="NextRepair"/>).</summary>
            RepairGear = 128,
            /// <summary>Cooking stations over a lit fire: done food off, raw food on (CompanionCookingBehavior; 0.2.235).</summary>
            Cook = 256,
            /// <summary>Worn weapons and armour upgraded at a base workbench / forge from the bag (CraftingUpgradeBehaviorV2; 0.2.235).</summary>
            Upgrade = 512,
            /// <summary>The owner's beds kept warm: vanilla lets a player lie down only inside a fire's Heat area (Bed.CheckFire); a fire_pit placed where its heat covers a cold bed (0.2.246).</summary>
            BedHeat = 1024,
            All = Fuel | Smelters | Pickups | Harvest | Plant | RepairPieces | Deposit | RepairGear | Cook | Upgrade | BedHeat,
        }

        /// <summary>A player-built piece under this health share is repaired (BuildingRepairBehavior's threshold).</summary>
        public const float PieceRepairBelow = 0.95f;
        /// <summary>A smelter under this share of its max fuel is fed.</summary>
        public const float SmelterFuelBelow = 0.5f;
        /// <summary>A charcoal kiln is fed only plain Wood, and only what the base chests hold beyond this many.</summary>
        public const int KilnWoodReserve = 40;
        private const float InteractReach = 2.2f, PieceReach = 3f;
        /// <summary>A target walked to this long (s) without arriving is skipped for <see cref="SkipSeconds"/>.</summary>
        private const float MoveGiveUpSeconds = 30f, SkipSeconds = 60f, ScanSeconds = 4f, PlantScanSeconds = 20f;
        private const int InteractTries = 4, MaxActionsPerCall = 8;

        private static readonly Dictionary<GameObject, float> s_skipUntil = new Dictionary<GameObject, float>();
        private static readonly Dictionary<GameObject, Vector2> s_moveSince = new Dictionary<GameObject, Vector2>();   // (first asked, last asked)
        private static readonly Dictionary<GameObject, int> s_interactTries = new Dictionary<GameObject, int>();

        /// <summary>What the last <see cref="NextBaseChore(ITaskBody, Vector3, float, BaseChores, out string)"/> call did in reach ("fuel fire_pit +8 Wood; pickup …"), or "".</summary>
        public static string LastBaseActions { get; private set; } = "";

        /// <summary>Leave <paramref name="target"/> out of base chores for <paramref name="seconds"/> (the executor's walk to it failed, say).</summary>
        public static void SkipBaseTarget(GameObject target, float seconds = SkipSeconds)
        {
            if (target == null) return;
            if (s_skipUntil.Count > 256) s_skipUntil.Clear();
            s_skipUntil[target] = Time.time + seconds;
            s_moveSince.Remove(target);
            s_interactTries.Remove(target);
        }

        private static bool Skipped(GameObject target) =>
            target == null || (s_skipUntil.TryGetValue(target, out float until) && Time.time < until);

        private delegate TaskOrder BaseStep(ITaskBody body, Vector3 basePos, float radius, out string action);

        /// <summary>
        /// The next base chore for <paramref name="body"/> within <paramref name="radius"/> of <paramref name="basePos"/>, among
        /// <paramref name="chores"/>, in companion priority. Work in reach is done inside the call (fuel and ore fed, seeds planted, pieces
        /// repaired, the bag stashed; <see cref="LastBaseActions"/> and a "[ChoreBrain]" line say what) and the call goes on to the next
        /// chore; what it returns is a Move (walk to <see cref="TaskOrder.Point"/>), an Interact (pick up a drop, harvest a crop or a hive:
        /// the body's own interact), or <see cref="NextRepair"/>'s Repair / Equip; Done ("base: nothing to do", after "did: …" when work
        /// was done in this call) when nothing is left; Failed only without a body. A target the body can't finish (a walk over 30 s, four
        /// interacts, a refused feed) is skipped for a minute, never returned as Failed. <paramref name="chore"/>: the chore of the returned
        /// order ("fuel", "smelter", "pickup", "harvest", "plant", "repair-piece", "deposit", "repair-gear"), "none" with Done.
        /// </summary>
        public static TaskOrder NextBaseChore(ITaskBody body, Vector3 basePos, float radius, BaseChores chores, out string chore)
        {
            chore = "none";
            LastBaseActions = "";
            if (body?.Inventory == null) return TaskOrder.Failed("no body");
            var steps = new List<(BaseChores flag, string name, BaseStep step)>
            {
                (BaseChores.Fuel, "fuel", FuelStep),
                (BaseChores.BedHeat, "bed-heat", BedHeatStep),
                (BaseChores.Smelters, "smelter", SmelterStep),
                (BaseChores.Cook, "cook", CookStep),
                (BaseChores.Pickups, "pickup", PickupStep),
                (BaseChores.Harvest, "harvest", HarvestStep),
                (BaseChores.Plant, "plant", PlantStep),
                (BaseChores.RepairPieces, "repair-piece", PieceRepairStep),
                (BaseChores.Upgrade, "upgrade", UpgradeStep),
                (BaseChores.Deposit, "deposit", DepositStep),
                (BaseChores.RepairGear, "repair-gear", GearRepairStep),
            };
            var did = new List<string>();
            for (int pass = 0; pass < MaxActionsPerCall; pass++)
            {
                TaskOrder order = null;
                string action = null, name = null;
                foreach (var (flag, stepName, step) in steps)
                {
                    if ((chores & flag) == 0) continue;
                    order = step(body, basePos, radius, out action);
                    if (order == null && action == null) continue;
                    name = stepName;
                    break;
                }
                if (order == null && action != null)
                {
                    did.Add($"{name} {action}");
                    continue;   // done in reach: on to the next chore
                }
                LastBaseActions = string.Join("; ", did);
                if (order == null) break;
                chore = name;
                if (did.Count > 0) order.Reason = $"did: {LastBaseActions}; next: {order.Reason}";
                return order;
            }
            LastBaseActions = string.Join("; ", did);
            return TaskOrder.Done(did.Count > 0 ? $"did: {LastBaseActions}; base: nothing else to do" : "base: nothing to do");
        }

        // ---- Shared target finders (0.2.237, the target-selection consolidation): which base targets exist and qualify, one set of
        // rules for the companions' sub-behaviours and the bot's base chores. What differs per body is passed in: where to search, who
        // is working (InteractableOccupancyManager reservations, so a bot and a companion never take the same station), how fuel is
        // found, which wards apply ----

        /// <summary>A fire wants fuel: refillable, finite, with a fuel item, under <see cref="RefuelBelow"/> of its max (FireTendingBehaviorV2.NeedsFuel).</summary>
        public static bool FireWantsFuel(Fireplace fire)
        {
            if (fire == null || !fire.m_canRefill || fire.m_infiniteFuel || fire.m_fuelItem == null) return false;
            ZNetView view = fire.GetComponent<ZNetView>();
            return view != null && view.IsValid() && view.GetZDO().GetFloat(ZDOVars.s_fuel, 0f) < fire.m_maxFuel * RefuelBelow;
        }

        /// <summary>
        /// The fires within <paramref name="radius"/> of <paramref name="center"/> that want fuel (<see cref="FireWantsFuel"/>), that
        /// <paramref name="worker"/> may use (not reserved by another worker; null = anyone) and whose fuel <paramref name="fuelAvailable"/>
        /// says the worker can get (its fuel prefab name in; null = any), nearest to <paramref name="from"/> on the ground plane first (wall
        /// torches hang at all heights). FireTendingBehaviorV2 takes the first; the bot's fuel chore walks the list.
        /// </summary>
        public static List<Fireplace> FiresToFuel(Vector3 center, float radius, Vector3 from, Character worker, Func<string, bool> fuelAvailable)
        {
            var result = new List<Fireplace>();
            var seen = new HashSet<Fireplace>();
            foreach (Collider collider in Physics.OverlapSphere(center, radius))
            {
                Fireplace fire = collider == null ? null : collider.GetComponent<Fireplace>() ?? collider.GetComponentInParent<Fireplace>();
                if (fire == null || !seen.Add(fire) || !FireWantsFuel(fire)) continue;
                if (!UsableBy(fire.gameObject, worker)) continue;
                if (fuelAvailable != null && !fuelAvailable(fire.m_fuelItem.gameObject.name)) continue;
                result.Add(fire);
            }
            result.Sort((a, b) => FlatSq(a.transform.position, from).CompareTo(FlatSq(b.transform.position, from)));
            return result;
        }

        /// <summary>A piece wants repair: loaded, player-built (a creator), under <see cref="PieceRepairBelow"/> health (BuildingRepairBehavior.IsRepairCandidate, wards aside).</summary>
        public static bool PieceWantsRepair(WearNTear piece)
        {
            if (piece == null || piece.GetHealthPercentage() >= PieceRepairBelow) return false;
            ZNetView view = piece.GetComponent<ZNetView>();
            Piece info = piece.GetComponent<Piece>();
            return view != null && view.IsValid() && info != null && info.GetCreator() != 0L;
        }

        /// <summary>
        /// The damaged pieces within <paramref name="radius"/> of <paramref name="center"/> (<see cref="PieceWantsRepair"/>) that
        /// <paramref name="wardsAllow"/> lets the worker touch (null = all), most damaged first (BuildingRepairBehavior's scan).
        /// </summary>
        public static List<WearNTear> DamagedPieces(Vector3 center, float radius, Func<Vector3, bool> wardsAllow)
        {
            var result = new List<WearNTear>();
            var seen = new HashSet<WearNTear>();
            foreach (Collider collider in Physics.OverlapSphere(center, radius))
            {
                WearNTear piece = collider == null ? null : collider.GetComponent<WearNTear>() ?? collider.GetComponentInParent<WearNTear>();
                if (piece == null || !seen.Add(piece) || !PieceWantsRepair(piece)) continue;
                if (wardsAllow != null && !wardsAllow(piece.transform.position)) continue;
                result.Add(piece);
            }
            result.Sort((a, b) => a.GetHealthPercentage().CompareTo(b.GetHealthPercentage()));
            return result;
        }

        /// <summary>
        /// The cooking stations within <paramref name="radius"/> of <paramref name="center"/> that are fed directly (not through an
        /// add-food switch: the oven), that <paramref name="worker"/> may use, nearest to <paramref name="from"/> first
        /// (CompanionCookingBehavior's scan).
        /// </summary>
        public static List<CookingStation> CookingStationsNear(Vector3 center, float radius, Vector3 from, Character worker)
        {
            var result = new List<CookingStation>();
            var seen = new HashSet<CookingStation>();
            foreach (Collider collider in Physics.OverlapSphere(center, radius))
            {
                CookingStation station = collider == null ? null : collider.GetComponent<CookingStation>() ?? collider.GetComponentInParent<CookingStation>();
                if (station == null || !seen.Add(station) || !CompanionCookingBehavior.TakesFoodDirectly(station)) continue;
                if (!UsableBy(station.gameObject, worker)) continue;
                result.Add(station);
            }
            result.Sort((a, b) => (a.transform.position - from).sqrMagnitude.CompareTo((b.transform.position - from).sqrMagnitude));
            return result;
        }

        /// <summary>
        /// The smelters, kilns and charcoal kilns within <paramref name="radius"/> of <paramref name="center"/> a worker runs
        /// (PieceDataHelper.IsOperableStation: not a battering ram or the bathtub) that <paramref name="worker"/> may use. The companions'
        /// SmelterOperatorBehavior scores them for attention; the bot feeds them in this order.
        /// </summary>
        public static List<Smelter> SmeltersNear(Vector3 center, float radius, Character worker)
        {
            var result = new List<Smelter>();
            var seen = new HashSet<Smelter>();
            foreach (Collider collider in Physics.OverlapSphere(center, radius))
            {
                Smelter smelter = collider == null ? null : collider.GetComponent<Smelter>() ?? collider.GetComponentInParent<Smelter>();
                if (smelter == null || !seen.Add(smelter) || !PieceDataHelper.IsOperableStation(smelter)) continue;
                if (!UsableBy(smelter.gameObject, worker)) continue;
                result.Add(smelter);
            }
            return result;
        }

        // ---- Shared finders, slice 2 (0.2.239): loot, farming, the smelter attention score ----

        /// <summary>
        /// How much a loose item is worth picking up first (LootPickupBehaviorV2's priority): trophies 100, metals and bars 80, other
        /// materials 50, one/two-handed weapons 40, consumables 30, anything else 10.
        /// </summary>
        public static float LootPriority(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return 0f;
            ItemDrop.ItemData.ItemType type = item.m_shared.m_itemType;
            string name = item.m_shared.m_name?.ToLowerInvariant() ?? "";
            if (type == ItemDrop.ItemData.ItemType.Trophy || name.Contains("trophy")) return 100f;
            if (name.Contains("black") || name.Contains("silver") || name.Contains("gold") || name.Contains("flametal") || name.Contains("iron") || name.Contains("copper")) return 80f;
            if (type == ItemDrop.ItemData.ItemType.Material) return 50f;
            if (type == ItemDrop.ItemData.ItemType.OneHandedWeapon || type == ItemDrop.ItemData.ItemType.TwoHandedWeapon) return 40f;
            if (type == ItemDrop.ItemData.ItemType.Consumable) return 30f;
            return 10f;
        }

        /// <summary>
        /// The loose items within <paramref name="radius"/> of <paramref name="center"/> worth walking to (ChestHelper.FindLooseItems: never a
        /// player's own drop, never a piece or tarred), not floating or sunk in water deeper than a body stands in, that
        /// <paramref name="accept"/> takes (null = all), by <see cref="LootPriority"/> and then nearest to <paramref name="from"/>.
        /// LootPickupBehaviorV2 adds its pickup chances and looted spots in accept; the bot adds what fits in its bag.
        /// </summary>
        public static List<ItemDrop> LootToTake(Vector3 center, float radius, Vector3 from, Func<ItemDrop, bool> accept)
        {
            var result = new List<ItemDrop>();
            foreach (ItemDrop drop in ChestHelper.FindLooseItems(center, radius))
            {
                if (drop == null || drop.m_itemData == null) continue;
                if (Swimming.GoalInDeepWater(drop.transform.position, out float depth))
                {
                    // 0.2.240 evidence: once per item per two minutes (new for the companions in 0.2.239).
                    if (LogDue(drop.gameObject.GetInstanceID(), 120f))
                        Debug.Log($"[ChoreBrain] loot: {Utils.GetPrefabName(drop.gameObject)} at ({drop.transform.position.x:0}, {drop.transform.position.z:0}) " +
                                  $"skipped: in water {depth:0.0} m deep (no body wades in for it)");
                    continue;
                }
                if (accept != null && !accept(drop)) continue;
                result.Add(drop);
            }
            result.Sort((a, b) =>
            {
                int byPriority = LootPriority(b.m_itemData).CompareTo(LootPriority(a.m_itemData));
                return byPriority != 0 ? byPriority : (a.transform.position - from).sqrMagnitude.CompareTo((b.transform.position - from).sqrMagnitude);
            });
            return result;
        }

        /// <summary>
        /// The beehives with honey within <paramref name="radius"/> of <paramref name="center"/> the wards allow (null = all), nearest to
        /// <paramref name="from"/> first (FarmingBehavior's priority 1). With <paramref name="worker"/>, a line once a minute names the nearest.
        /// </summary>
        public static List<Beehive> HivesToHarvest(Vector3 center, float radius, Vector3 from, Func<Vector3, bool> wardsAllow, string worker = null)
        {
            List<Beehive> hives = FarmingDataHelper.FindNearbyHarvestableBeehives(center, radius);
            hives.RemoveAll(h => h == null || (wardsAllow != null && !wardsAllow(h.transform.position)));
            hives.Sort((a, b) => (a.transform.position - from).sqrMagnitude.CompareTo((b.transform.position - from).sqrMagnitude));
            NoteNearest(worker, "beehive(s) with honey", hives.Count > 0 ? hives[0].gameObject : null, hives.Count, from);
            return hives;
        }

        /// <summary>
        /// The ripe crops within <paramref name="radius"/> of <paramref name="center"/> the wards allow (null = all), nearest to
        /// <paramref name="from"/> first (FarmingBehavior's priority 2). With <paramref name="worker"/>, a line once a minute names the nearest.
        /// </summary>
        public static List<Pickable> CropsToHarvest(Vector3 center, float radius, Vector3 from, Func<Vector3, bool> wardsAllow, string worker = null)
        {
            List<Pickable> crops = FarmingDataHelper.FindNearbyHarvestableCrops(center, radius);
            crops.RemoveAll(c => c == null || (wardsAllow != null && !wardsAllow(c.transform.position)));
            crops.Sort((a, b) => (a.transform.position - from).sqrMagnitude.CompareTo((b.transform.position - from).sqrMagnitude));
            NoteNearest(worker, "ripe crop(s)", crops.Count > 0 ? crops[0].gameObject : null, crops.Count, from);
            return crops;
        }

        // 0.2.240 evidence (nearest-first farm targets, new for the companions in 0.2.239): once a minute per worker and kind.
        private static void NoteNearest(string worker, string kind, GameObject nearest, int count, Vector3 from)
        {
            if (worker == null || nearest == null || !LogDue((worker + kind).GetHashCode(), 60f)) return;
            Vector3 p = nearest.transform.position;
            Debug.Log($"[ChoreBrain] {worker}: {count} {kind}; nearest first: {Utils.GetPrefabName(nearest)} at ({p.x:0}, {p.z:0}), {Vector3.Distance(from, p):0} m off");
        }

        private static readonly Dictionary<int, float> s_logDue = new Dictionary<int, float>();

        // True (and the clock restarts) when the line keyed by key hasn't been written in the last seconds.
        private static bool LogDue(int key, float seconds)
        {
            if (s_logDue.TryGetValue(key, out float next) && Time.time < next) return false;
            if (s_logDue.Count > 512) s_logDue.Clear();
            s_logDue[key] = Time.time + seconds;
            return true;
        }

        /// <summary>How far round the base (m) free cultivated ground is looked for (FarmingBehavior's PlantingDetectionRange; 0.2.241: one radius).</summary>
        public const float PlantSearchRadius = 20f;

        /// <summary>A cultivator, by its prefab name (FarmingBehavior.IsCultivator: any prefab with "cultivator" in it, so modded ones count).</summary>
        public static bool IsCultivator(ItemDrop.ItemData item) =>
            item?.m_dropPrefab?.name?.IndexOf("cultivator", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// <paramref name="item"/> is a seed one of whose saplings can grow on one of <paramref name="spots"/> (biome, heat, cold:
        /// FarmingBehavior.IsSeedForPlantSpots), so carrying it means planting can happen here.
        /// </summary>
        public static bool IsSeedFor(ItemDrop.ItemData item, IEnumerable<Vector3> spots)
        {
            if (spots == null) return false;
            foreach (FarmingDataHelper.CropSapling crop in FarmingDataHelper.SaplingsForSeed(item?.m_dropPrefab?.name))
                foreach (Vector3 spot in spots)
                    if (FarmingDataHelper.CanGrowAt(crop, spot)) return true;
            return false;
        }

        /// <summary>
        /// What to plant where (FarmingBehavior.TryFindPlantingTask): for each seed in <paramref name="storage"/>, its saplings least-planted
        /// first (<paramref name="plantedCount"/> by sapling prefab; null = all 0: a seed that grows two crops alternates), one whose seed cost
        /// the storage covers, on the nearest of <paramref name="spots"/> to <paramref name="from"/> that is valid for it. False when nothing fits.
        /// </summary>
        public static bool ChoosePlanting(Inventory storage, IEnumerable<Vector3> spots, Vector3 from, Func<string, int> plantedCount,
            out FarmingDataHelper.CropSapling crop, out Vector3 spot)
        {
            crop = null;
            spot = Vector3.zero;
            if (storage == null || spots == null) return false;
            List<Vector3> nearest = spots.OrderBy(p => (p - from).sqrMagnitude).ToList();
            foreach (ItemDrop.ItemData item in storage.GetAllItems())
            {
                IReadOnlyList<FarmingDataHelper.CropSapling> saplings = FarmingDataHelper.SaplingsForSeed(item?.m_dropPrefab?.name);
                foreach (FarmingDataHelper.CropSapling candidate in saplings.OrderBy(c => plantedCount != null ? plantedCount(c.Prefab.name) : 0))
                {
                    if (candidate.Seeds != null && candidate.Seeds.Any(s => CountPrefab(storage, s.Item) < s.Amount)) continue;
                    foreach (Vector3 p in nearest)
                    {
                        if (!FarmingDataHelper.IsValidPlantingPosition(p, candidate, 1f)) continue;
                        crop = candidate;
                        spot = p;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// How much <paramref name="smelter"/> needs a worker at <paramref name="workerPos"/> carrying <paramref name="carried"/>
        /// (SmelterOperatorBehavior's score, now one copy): 0 = nothing to do. Output waiting at its output point +2; room in the queue and
        /// an input it takes in the chests round the smelter and the worker (within <paramref name="chestRadius"/> of their midpoint) or
        /// carried +1; under half its fuel with fuel in those chests or carried +0.5; empty and something to do +0.5. <paramref name="why"/>
        /// lists what scored.
        /// </summary>
        public static float SmelterAttention(Smelter smelter, Vector3 workerPos, float chestRadius, Inventory carried, out string why)
        {
            why = "";
            if (!PieceDataHelper.IsOperableStation(smelter)) return 0f;
            ZNetView view = smelter.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) return 0f;
            var reasons = new List<string>();
            float score = 0f;
            int queued = view.GetZDO().GetInt(ZDOVars.s_queued, 0);
            float fuel = view.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
            bool empty = queued == 0, needsFuel = smelter.m_maxFuel > 0 && fuel < smelter.m_maxFuel * 0.5f, room = queued < smelter.m_maxOre;
            if (smelter.m_spawnStack && smelter.m_outputPoint != null &&
                Physics.OverlapSphere(smelter.m_outputPoint.position, 2f).Any(c => c != null && c.GetComponent<ItemDrop>() != null))
            {
                score += 2f;
                reasons.Add("output waiting");
            }
            Vector3 at = smelter.transform.position;
            List<Container> chests = ChestHelper.FindNearbyChests((at + workerPos) * 0.5f, chestRadius + Vector3.Distance(at, workerPos) * 0.5f);
            bool Have(string prefab) => ChestHelper.GetAvailableItemCount(chests, prefab) > 0 || (carried != null && ChestHelper.CountPrefabInInventory(carried, prefab) > 0);
            if (room)
            {
                IEnumerable<string> inputs = PieceDataHelper.IsCharcoalKiln(smelter)
                    ? PieceDataHelper.GetStationInputs(smelter)
                    : smelter.m_conversion.Where(c => c?.m_from != null).Select(c => c.m_from.name);
                string input = inputs.FirstOrDefault(Have);
                if (input != null) { score += 1f; reasons.Add($"room and {input} to hand"); }
            }
            if (needsFuel && smelter.m_fuelItem != null && Have(smelter.m_fuelItem.name)) { score += 0.5f; reasons.Add($"fuel {fuel:0}/{smelter.m_maxFuel} and {smelter.m_fuelItem.name} to hand"); }
            if (empty && score > 0f) { score += 0.5f; reasons.Add("empty"); }
            why = string.Join(", ", reasons);
            return score;
        }

        // A station the body is working on is reserved for it (as a companion reserves it), so a companion picks another; the
        // reservation lapses after ReserveSeconds or is released once the work in reach is done.
        private const float ReserveSeconds = 30f;

        private static bool MayUse(ITaskBody body, GameObject target) =>
            UsableBy(target, body?.Character);

        private static readonly Dictionary<(int, int), float> s_takenLogged = new Dictionary<(int, int), float>();

        // Whether worker may use target (InteractableOccupancyManager.CanUseInteractable: nobody else holds it, nobody stands on it;
        // a null worker or target: yes). When another worker holds it, an always-on line says so once a minute per worker and target
        // (0.2.238: the reservation proof, for bots and companions alike since both go through the shared finders).
        private static bool UsableBy(GameObject target, Character worker)
        {
            if (worker == null || target == null || InteractableOccupancyManager.CanUseInteractable(target, worker)) return true;
            string occupant = InteractableOccupancyManager.OccupantName(target, worker);
            if (occupant == null) return false;   // crowded, not held: no line
            var key = (worker.GetInstanceID(), target.GetInstanceID());
            if (s_takenLogged.TryGetValue(key, out float next) && Time.time < next) return false;
            if (s_takenLogged.Count > 256) s_takenLogged.Clear();
            s_takenLogged[key] = Time.time + 60f;
            Vector3 p = target.transform.position;
            CompanionController companion = worker.GetComponent<CompanionController>();
            string who = companion != null ? companion.companionName : worker is Player player ? player.GetPlayerName() : worker.m_name;
            Debug.Log($"[ChoreBrain] {who}: {Utils.GetPrefabName(target)} at ({p.x:0}, {p.z:0}) is taken by {occupant}; looking past it");
            return false;
        }

        private static void Reserve(ITaskBody body, GameObject target)
        {
            if (body?.Character != null && target != null) InteractableOccupancyManager.TryOccupy(target, body.Character, ReserveSeconds);
        }

        private static void Unreserve(ITaskBody body, GameObject target)
        {
            if (body?.Character != null && target != null) InteractableOccupancyManager.Release(target, body.Character);
        }

        private static float FlatSq(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        // ---- Orders with give-up guards ----

        // Walk to point (target keys the give-up clock; null: none). Null when the walk has been asked for over MoveGiveUpSeconds.
        private static TaskOrder MoveOrder(ITaskBody body, GameObject target, Vector3 point, string reason)
        {
            if (target != null)
            {
                float now = Time.time;
                if (!s_moveSince.TryGetValue(target, out Vector2 since) || now - since.y > 5f) since = new Vector2(now, now);
                if (now - since.x > MoveGiveUpSeconds)
                {
                    Debug.Log($"[ChoreBrain] {BodyName(body)}: gave up walking to {Utils.GetPrefabName(target)} after {MoveGiveUpSeconds:0} s ({reason}); skipping it {SkipSeconds:0} s");
                    SkipBaseTarget(target);
                    return null;
                }
                if (s_moveSince.Count > 256) s_moveSince.Clear();
                s_moveSince[target] = new Vector2(since.x, now);
            }
            return new TaskOrder { Kind = TaskKind.Move, Point = point, Target = target, Reason = reason };
        }

        // Interact with target from within InteractReach (a Move first). Null after InteractTries interacts that left it there.
        private static TaskOrder InteractOrder(ITaskBody body, GameObject target, Vector3 point, string reason)
        {
            if (Vector3.Distance(body.Position, point) > InteractReach) return MoveOrder(body, target, point, reason);
            s_moveSince.Remove(target);
            s_interactTries.TryGetValue(target, out int tries);
            if (tries >= InteractTries)
            {
                Debug.Log($"[ChoreBrain] {BodyName(body)}: {Utils.GetPrefabName(target)} still there after {InteractTries} interacts ({reason}); skipping it {SkipSeconds:0} s");
                SkipBaseTarget(target);
                return null;
            }
            if (s_interactTries.Count > 256) s_interactTries.Clear();
            s_interactTries[target] = tries + 1;
            return new TaskOrder { Kind = TaskKind.Interact, Target = target, Point = point, Reason = reason };
        }

        // The item for station from the bag, else out of a base chest (keeping reserve there), then fed in reach through feed(n) -> units fed.
        private static TaskOrder FetchThenFeed(ITaskBody body, Vector3 basePos, float radius, GameObject station, string prefab, string sharedName,
            int need, string state, Func<int, int> feed, int reserve, out string action)
        {
            action = null;
            Inventory bag = body.Inventory;
            if (need <= 0 || !MayUse(body, station)) return null;
            Reserve(body, station);   // a companion picks another station meanwhile
            if (bag.CountItems(sharedName) <= 0)
            {
                Container chest = FindChestWith(basePos, radius, body.OwnerPlayerId, prefab);
                int spare = chest == null || Skipped(chest.gameObject) ? 0 : InventoryTransferService.CountItem(chest, prefab) - reserve;
                if (spare <= 0) { Unreserve(body, station); return null; }
                if (Vector3.Distance(body.Position, chest.transform.position) > BaseUseDistance)
                    return MoveOrder(body, chest.gameObject, chest.transform.position, $"{state}: fetching {Mathf.Min(need, spare)} {prefab} from {Utils.GetPrefabName(chest.gameObject)}");
                s_moveSince.Remove(chest.gameObject);
                if (Withdraw(chest, bag, prefab, Mathf.Min(need, spare), body.OwnerPlayerId) <= 0) { SkipBaseTarget(chest.gameObject); Unreserve(body, station); return null; }
            }
            if (Vector3.Distance(body.Position, station.transform.position) > BaseUseDistance)
                return MoveOrder(body, station, station.transform.position, $"{state}: bringing {prefab}");
            s_moveSince.Remove(station);
            int fed = feed(Mathf.Min(need, bag.CountItems(sharedName)));
            Unreserve(body, station);
            if (fed <= 0) { SkipBaseTarget(station); return null; }
            action = $"{Utils.GetPrefabName(station)} +{fed} {prefab}";
            Debug.Log($"[ChoreBrain] {BodyName(body)}: {state}: +{fed} {prefab}");
            return null;
        }

        // ---- 1. Fires ----

        private static TaskOrder FuelStep(ITaskBody body, Vector3 basePos, float radius, out string action)
        {
            action = null;
            Inventory bag = body.Inventory;
            // FiresToFuel: the companions' fire rules (FireWantsFuel, reservations, fuel the body can get from its bag or a base chest).
            foreach (Fireplace fire in FiresToFuel(basePos, radius, body.Position, body.Character,
                         prefab => CountPrefab(bag, prefab) > 0 || FindChestWith(basePos, radius, body.OwnerPlayerId, prefab) != null))
            {
                if (Skipped(fire.gameObject)) continue;
                ZNetView view = fire.GetComponent<ZNetView>();
                int fuel = Mathf.FloorToInt(view.GetZDO().GetFloat(ZDOVars.s_fuel, 0f)), max = Mathf.RoundToInt(fire.m_maxFuel);
                TaskOrder order = FetchThenFeed(body, basePos, radius, fire.gameObject, fire.m_fuelItem.gameObject.name, fire.m_fuelItem.m_itemData.m_shared.m_name,
                    max - fuel, $"fuel: {Utils.GetPrefabName(fire.gameObject)} {fuel}/{max}", n => AddFuel(body, fire, n, out _), 0, out action);
                if (order != null || action != null) return order;
            }
            return RelightStep(body, basePos, radius);
        }

        // 0.2.248 ([seasons], overnight 05:05 "no burning fire within 20 m of base" while the fuel step said nothing to do): a vanilla
        // fire with m_canTurnOff switches ITSELF off when wet (Fireplace.UpdateState: RPC_ToggleOn -> s_state 2) and stays off after
        // the rain with its fuel in it; IsBurning is false for state != 1. Such a fire, dry now and not smothered, is lit again the way
        // a player does it: the body's own interact (Fireplace.Interact with fuel in it toggles it on). One still wet is left off (vanilla
        // would switch it off again); a smothered one can't burn there. Each said once per fire per 5 min.
        private static readonly Dictionary<GameObject, float> s_relitAt = new Dictionary<GameObject, float>();

        private static TaskOrder RelightStep(ITaskBody body, Vector3 basePos, float radius)
        {
            foreach (Fireplace fire in FiresNear(basePos, radius))
            {
                if (!fire.m_canTurnOff || Skipped(fire.gameObject) || fire.m_nview == null || !fire.m_nview.IsValid()) continue;
                ZDO zdo = fire.m_nview.GetZDO();
                if (fire.IsBurning() || zdo.GetInt(ZDOVars.s_state, 1) == 1 || (zdo.GetFloat(ZDOVars.s_fuel) <= 0f && !fire.m_infiniteFuel)) continue;
                string name = $"{Utils.GetPrefabName(fire.gameObject)} at ({fire.transform.position.x:0}, {fire.transform.position.z:0})";
                if (fire.m_blocked) { FireSay(body, fire, $"fuel: {name} is switched off and smothered (a roof, terrain or blocked smoke over it); not relit"); continue; }
                if (FireWet(fire.transform.position, fire.m_coverCheckOffset)) { FireSay(body, fire, $"fuel: {name} is switched off: wet (rain or wind, no roof over it); left off"); continue; }
                if (s_relitAt.TryGetValue(fire.gameObject, out float at) && Time.time - at < 4f) continue;   // the last interact's toggle on its way
                if (!MayUse(body, fire.gameObject)) continue;
                TaskOrder order = InteractOrder(body, fire.gameObject, fire.transform.position, $"fuel: {name} was switched off (rain), lighting it again");
                if (order == null) continue;
                if (order.Kind == TaskKind.Interact)
                {
                    if (s_relitAt.Count > 64) s_relitAt.Clear();
                    s_relitAt[fire.gameObject] = Time.time;
                }
                return order;
            }
            return null;
        }

        /// <summary>
        /// The fires within <paramref name="radius"/> of <paramref name="center"/> the rain switched off that can be lit again now
        /// (0.2.255, the companions' fire tending; the bot's relight pass uses the same rule): m_canTurnOff, fuel in it, switched off,
        /// not smothered, dry now (<see cref="FireWet"/>), and free for <paramref name="worker"/> (InteractableOccupancyManager).
        /// Nearest to <paramref name="from"/> first.
        /// </summary>
        public static List<Fireplace> FiresToRelight(Vector3 center, float radius, Vector3 from, Character worker)
        {
            var fires = new List<Fireplace>();
            foreach (Fireplace fire in FiresNear(center, radius))
            {
                if (!fire.m_canTurnOff || fire.m_nview == null || !fire.m_nview.IsValid() || fire.IsBurning() || fire.m_blocked) continue;
                ZDO zdo = fire.m_nview.GetZDO();
                if (zdo.GetInt(ZDOVars.s_state, 1) == 1 || (zdo.GetFloat(ZDOVars.s_fuel) <= 0f && !fire.m_infiniteFuel)) continue;
                if (FireWet(fire.transform.position, fire.m_coverCheckOffset)) continue;
                if (worker != null && !InteractableOccupancyManager.CanUseInteractable(fire.gameObject, worker)) continue;
                fires.Add(fire);
            }
            fires.Sort((a, b) => (a.transform.position - from).sqrMagnitude.CompareTo((b.transform.position - from).sqrMagnitude));
            return fires;
        }

        /// <summary>
        /// One always-on line per chore outcome (0.2.255, Fire's homestead test: "[Chore] &lt;who&gt;: &lt;chore&gt;: &lt;what&gt;"), the same tag
        /// for every companion chore so a drill counts them per companion and chore.
        /// </summary>
        public static void ChoreDone(string who, string chore, string what) => Debug.Log($"[Chore] {who ?? "?"}: {chore}: {what}");

        /// <summary>
        /// Vanilla's Fireplace.CheckWet for a fire at <paramref name="at"/> (cover tested <paramref name="coverOffset"/> above it): wind
        /// at 0.8+ with under 70 % cover, or rain with no roof. A fire with m_canTurnOff switches itself off then.
        /// </summary>
        public static bool FireWet(Vector3 at, float coverOffset)
        {
            if (EnvMan.instance == null) return false;
            bool windy = EnvMan.instance.GetWindIntensity() >= 0.8f, wet = EnvMan.IsWet();
            if (!windy && !wet) return false;
            Cover.GetCoverForPoint(at + Vector3.up * coverOffset, out float cover, out bool underRoof);
            return (windy && cover < 0.7f) || (wet && !underRoof);
        }

        /// <summary>Why <paramref name="fire"/> gives no heat now, or "" while it burns: smothered, out of fuel, switched off (wet or not), under water.</summary>
        public static string FireOffReason(Fireplace fire)
        {
            if (fire == null || fire.m_nview == null || !fire.m_nview.IsValid() || fire.IsBurning()) return "";
            ZDO zdo = fire.m_nview.GetZDO();
            if (fire.m_blocked) return "smothered (a roof, terrain or blocked smoke over it)";
            if (zdo.GetFloat(ZDOVars.s_fuel) <= 0f && !fire.m_infiniteFuel) return "out of fuel";
            if (zdo.GetInt(ZDOVars.s_state, 1) != 1)
                return FireWet(fire.transform.position, fire.m_coverCheckOffset) ? "switched off: wet (rain or wind, no roof over it)" : "switched off (rain earlier)";
            return "under water";
        }

        private static readonly Dictionary<GameObject, float> s_fireSaid = new Dictionary<GameObject, float>();

        private static void FireSay(ITaskBody body, Fireplace fire, string line)
        {
            if (s_fireSaid.TryGetValue(fire.gameObject, out float at) && Time.time - at < BedSkipSeconds) return;
            if (s_fireSaid.Count > 64) s_fireSaid.Clear();
            s_fireSaid[fire.gameObject] = Time.time;
            Debug.Log($"[ChoreBrain] {BodyName(body)}: {line}");
        }

        // ---- 1b. Warm beds ([seasons] 10-01: the overnight Coop1 got "no lit fire near the bed" every minute after "base: nothing to do") ----

        /// <summary>The fire placed to warm a cold bed (the game's campfire: 5 Stone, 2 Wood, no station).</summary>
        public const string BedFirePrefab = "fire_pit";
        private const float BedMinCover = 0.8f, BedFireMinGap = 1.4f, BedHeatMargin = 0.3f, BedSkipSeconds = 300f;
        private static readonly List<Bed> s_beds = new List<Bed>();
        private static readonly Dictionary<GameObject, float> s_bedSaid = new Dictionary<GameObject, float>();

        /// <summary>
        /// The beds <paramref name="ownerId"/> sleeps in within <paramref name="radius"/> of <paramref name="center"/> (vanilla's owner, or
        /// the local player's spawn bed when <paramref name="localSpawn"/>) that vanilla's sleep checks would refuse for want of heat:
        /// under a roof with 80 % cover (Bed.CheckExposure, else heat is not what stops it) and outside every Heat area (Bed.CheckFire:
        /// the bed's own position).
        /// </summary>
        public static List<Bed> ColdBeds(Vector3 center, float radius, long ownerId, bool localSpawn)
        {
            List<Bed> beds = SleepBeds(center, radius, ownerId, localSpawn);
            beds.RemoveAll(b => EffectArea.IsPointInsideArea(b.transform.position, EffectArea.Type.Heat) != null);
            return beds;
        }

        /// <summary>The beds <paramref name="ownerId"/> sleeps in near <paramref name="center"/> (its own, or the local player's spawn bed) that pass vanilla's roof check (Bed.CheckExposure: a roof and 80 % cover).</summary>
        public static List<Bed> SleepBeds(Vector3 center, float radius, long ownerId, bool localSpawn)
        {
            s_beds.Clear();
            foreach (Bed bed in UnityEngine.Object.FindObjectsByType<Bed>(FindObjectsSortMode.None))
            {
                if (bed == null || bed.m_nview == null || !bed.m_nview.IsValid()) continue;
                if ((bed.transform.position - center).sqrMagnitude > radius * radius) continue;
                if (bed.GetOwner() != ownerId && !(localSpawn && bed.IsCurrent())) continue;
                Cover.GetCoverForPoint(bed.GetSpawnPoint(), out float cover, out bool underRoof);
                if (!underRoof || cover < BedMinCover) continue;
                s_beds.Add(bed);
            }
            return s_beds.ToList();
        }

        /// <summary>The burning fire whose Heat area covers <paramref name="bed"/>'s position (what vanilla's Bed.CheckFire finds), or null.</summary>
        public static Fireplace HeatingFire(Bed bed)
        {
            EffectArea heat = bed != null ? EffectArea.IsPointInsideArea(bed.transform.position, EffectArea.Type.Heat) : null;
            return heat != null ? heat.GetComponentInParent<Fireplace>() : null;
        }

        /// <summary>From vanilla's afternoon on (EnvMan.IsAfternoon) and through the night: the bed's fire is kept full for the night.</summary>
        public static bool NightComing() => EnvMan.instance != null && (EnvMan.IsAfternoon() || EnvMan.IsNight());

        /// <summary>The Heat area a fire prefab carries (inactive until it burns): its centre from the piece's pivot and its radius.</summary>
        public static bool HeatOf(GameObject firePrefab, out Vector3 offset, out float radius)
        {
            offset = Vector3.zero;
            radius = 0f;
            if (firePrefab == null) return false;
            foreach (EffectArea area in firePrefab.GetComponentsInChildren<EffectArea>(true))
            {
                if ((area.m_type & EffectArea.Type.Heat) == 0) continue;
                SphereCollider sphere = area.GetComponent<SphereCollider>();
                if (sphere == null) continue;
                Transform t = area.transform;
                offset = firePrefab.transform.InverseTransformPoint(t.TransformPoint(sphere.center));
                Vector3 scale = t.lossyScale, root = firePrefab.transform.lossyScale;
                radius = sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z))
                         / Mathf.Max(0.001f, Mathf.Max(Mathf.Abs(root.x), Mathf.Abs(root.y), Mathf.Abs(root.z)));
                return radius > 0f;
            }
            return false;
        }

        /// <summary>
        /// Where a fire of <paramref name="firePrefab"/> warms <paramref name="bed"/>: its Heat sphere covering the bed's position by
        /// 0.3 m, at least 1.4 m from it, on dry footing, free of pieces and bodies, either under open sky or under a rain-proof roof with
        /// room over the flame (Fireplace.CheckUnderTerrain) and an open side for the smoke (a closed roof lets it build up), where the
        /// owner may build (wards, no-build locations) and, for a ground-only piece, on the terrain. A fire that switches itself off in
        /// the rain (m_canTurnOff) takes the covered spot first, one that doesn't the open sky (0.2.248). Heat goes through walls, so a
        /// spot just outside the wall by the bed counts. Then most margin, then nearest to <paramref name="from"/>. "" with the spot, or
        /// why there is none.
        /// </summary>
        public static string BedFireSpot(Bed bed, GameObject firePrefab, Vector3 from, out Vector3 spot)
        {
            spot = default;
            if (!HeatOf(firePrefab, out Vector3 offset, out float radius)) return $"{Utils.GetPrefabName(firePrefab)} has no heat area";
            Piece piece = firePrefab.GetComponent<Piece>();
            Fireplace fireplace = firePrefab.GetComponent<Fireplace>();
            // 0.2.248 ([seasons]): a fire with m_canTurnOff switches itself off in the rain without a roof (Fireplace.CheckWet), so for one
            // a rain-proof spot ranks first: under a roof (70 % cover), nothing within 0.5 m over the flame (not smothered) and an open side
            // the smoke drifts out of; open sky second. A fire that stays lit in the rain takes open sky first.
            bool rainOff = fireplace != null && fireplace.m_canTurnOff;
            float coverOffset = fireplace != null ? fireplace.m_coverCheckOffset : 0.5f;
            Vector3 bedAt = bed.transform.position;
            int solid = FiresCore.World.Surface.Mask, terrain = LayerMask.GetMask("terrain");
            string why = $"no spot within {radius:0.0} m of the bed where a fire's heat reaches it";
            float bestMargin = -1f, bestDist = float.MaxValue;
            int bestRank = int.MaxValue;
            for (float d = BedFireMinGap; d <= radius - BedHeatMargin + 0.01f; d += 0.4f)
                for (int k = 0; k < 16; k++)
                {
                    float a = k * Mathf.PI / 8f;
                    Vector3 p = bedAt + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * d;
                    if (!FiresCore.World.Surface.Height(p, bedAt.y, out float ground)) continue;
                    p.y = ground;
                    float margin = radius - Vector3.Distance(p + offset, bedAt);
                    if (margin < BedHeatMargin) continue;
                    if (FishingBrain.WaterAt(p, out _, out float depth) && depth > 0.1f) { why = "the spots in reach of the bed are in water"; continue; }
                    bool openSky = !Physics.Raycast(p + Vector3.up * 1f, Vector3.up, 40f, solid, QueryTriggerInteraction.Ignore);
                    int rank;
                    if (openSky) rank = rainOff ? 1 : 0;
                    else
                    {
                        // Covered: rain-proof (the cover vanilla's wet check reads), not smothered (Fireplace.CheckUnderTerrain's 0.5 m over
                        // the flame), and an open side: a level ray 4 m out from 1 m over the flame that meets nothing, for the smoke.
                        Cover.GetCoverForPoint(p + Vector3.up * coverOffset, out float cover, out bool underRoof);
                        bool smothered = Physics.Raycast(p + Vector3.up * coverOffset, Vector3.up, 0.5f, solid, QueryTriggerInteraction.Ignore);
                        bool openSide = false;
                        for (int s = 0; s < 8 && !openSide; s++)
                        {
                            float sa = s * Mathf.PI / 4f;
                            openSide = !Physics.Raycast(p + Vector3.up * (coverOffset + 1f), new Vector3(Mathf.Cos(sa), 0f, Mathf.Sin(sa)), 4f, solid, QueryTriggerInteraction.Ignore);
                        }
                        if (smothered || !openSide || !underRoof || cover < 0.7f)
                        {
                            why = smothered ? "the covered spots in reach of the bed are too low under the roof (the fire would be smothered)"
                                : !openSide ? "every spot in reach of the bed is under a closed roof (smoke would build up)"
                                : "the covered spots in reach of the bed let the rain in";
                            continue;
                        }
                        rank = rainOff ? 0 : 1;
                    }
                    if (Physics.CheckSphere(p + Vector3.up * 0.8f, 0.6f, solid & ~terrain, QueryTriggerInteraction.Ignore)) { why = "the open spots in reach of the bed are taken"; continue; }
                    if (piece != null && piece.m_groundOnly && !Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, 1f, terrain, QueryTriggerInteraction.Ignore)) { why = "the fire needs bare ground and the spots in reach are floored"; continue; }
                    if (Location.IsInsideNoBuildLocation(p) || !PrivateArea.CheckAccess(p, 0f, false, false)) { why = "the spots in reach of the bed are warded or in a no-build area"; continue; }
                    if (piece != null && piece.m_craftingStation != null && CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, p) == null) { why = $"no {piece.m_craftingStation.m_name} in range to build the fire"; continue; }
                    float dist = Vector3.Distance(from, p);
                    if (rank < bestRank || (rank == bestRank && (margin > bestMargin + 0.25f || (Mathf.Abs(margin - bestMargin) <= 0.25f && dist < bestDist))))
                    {
                        bestMargin = rank < bestRank ? margin : Mathf.Max(bestMargin, margin);
                        bestRank = rank;
                        bestDist = dist;
                        spot = p;
                    }
                }
            return bestMargin >= 0f ? "" : why;
        }

        private static TaskOrder BedHeatStep(ITaskBody body, Vector3 basePos, float radius, out string action)
        {
            action = null;
            bool localSpawn = body.Character != null && body.Character == Player.m_localPlayer;
            // 0.2.250, Fire: "on return home they cannot sleep if there's not a lit fire nearby so they have to keep the fire fueled"
            // (overnight: 81 bed refusals, 0 sleeps). From the afternoon on, the fire that warms each bed is topped up to full, not
            // only under the fuel step's 70 %, so it lasts the night; one switched off is relit by the fuel step's relight pass.
            if (NightComing())
                foreach (Bed bed in SleepBeds(basePos, radius, body.OwnerPlayerId, localSpawn))
                {
                    Fireplace fire = HeatingFire(bed);
                    if (fire == null || Skipped(fire.gameObject) || !fire.m_canRefill || fire.m_infiniteFuel || fire.m_fuelItem == null) continue;
                    ZNetView view = fire.GetComponent<ZNetView>();
                    if (view == null || !view.IsValid()) continue;
                    int fuel = Mathf.FloorToInt(view.GetZDO().GetFloat(ZDOVars.s_fuel, 0f)), max = Mathf.RoundToInt(fire.m_maxFuel);
                    if (fuel >= max - 1) continue;
                    Vector3 b = bed.transform.position;
                    TaskOrder topUp = FetchThenFeed(body, basePos, radius, fire.gameObject, fire.m_fuelItem.gameObject.name, fire.m_fuelItem.m_itemData.m_shared.m_name,
                        max - fuel, $"bed-heat: topping up {Utils.GetPrefabName(fire.gameObject)} {fuel}/{max} for the night (it warms the bed at ({b.x:0}, {b.z:0}))",
                        n => AddFuel(body, fire, n, out _), 0, out action);
                    if (topUp != null || action != null) return topUp;
                }
            foreach (Bed bed in ColdBeds(basePos, radius, body.OwnerPlayerId, localSpawn).ToList())
            {
                if (Skipped(bed.gameObject)) continue;
                Vector3 bedAt = bed.transform.position;
                string where = $"bed ({bedAt.x:0}, {bedAt.z:0})";
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(BedFirePrefab) : null;
                if (prefab == null || !HeatOf(prefab, out _, out float heat)) { BedSay(body, bed, $"bed-heat: {where} has no heat and there is no {BedFirePrefab} prefab"); SkipBaseTarget(bed.gameObject, BedSkipSeconds); continue; }
                // A fire already near enough to warm it once lit: unlit for want of fuel is the fuel step's (it ran first); one that
                // can't burn there (a roof, terrain or smoke over it) doesn't count.
                Fireplace near = FiresNear(bedAt, heat + 1f).FirstOrDefault(f => !f.m_blocked);
                if (near != null && !near.IsBurning())
                {
                    // 0.2.248: out of fuel / switched off and dry are the fuel step's (it ran first: no fuel to hand, or the relight is on
                    // its way); one the rain keeps switched off can't warm the bed until it stops, so a rain-proof fire is placed for it.
                    string off = FireOffReason(near);
                    if (!off.StartsWith("switched off: wet"))
                    {
                        BedSay(body, bed, $"bed-heat: {where} has no heat: {Utils.GetPrefabName(near.gameObject)} at ({near.transform.position.x:0}, {near.transform.position.z:0}) is {off}" +
                                          $"{(off == "out of fuel" ? " and no fuel is to hand" : off.StartsWith("switched off") ? "; the fuel step relights it" : "")}");
                        SkipBaseTarget(bed.gameObject, off.StartsWith("switched off") ? 10f : BedSkipSeconds);
                        continue;
                    }
                }
                string refusal = BedFireSpot(bed, prefab, body.Position, out Vector3 spot);
                if (refusal != "") { BedSay(body, bed, $"bed-heat: {where} has no heat; no fire placed: {refusal}"); SkipBaseTarget(bed.gameObject, BedSkipSeconds); continue; }
                TaskOrder order = BuildForBed(body, basePos, radius, bed, prefab, spot, where, out action);
                if (order != null || action != null) return order;
            }
            return null;
        }

        // The fires (any Fireplace) within reach of a point.
        private static IEnumerable<Fireplace> FiresNear(Vector3 at, float reach) =>
            UnityEngine.Object.FindObjectsByType<Fireplace>(FindObjectsSortMode.None)
                .Where(f => f != null && (f.transform.position - at).sqrMagnitude <= reach * reach)
                .OrderBy(f => (f.transform.position - at).sqrMagnitude);

        // The fire for a cold bed: the piece's own cost from the bag, fetched from base chests first when bag and chests cover all of it
        // (never a part), a hammer that builds it carried (the bag or a chest), walked to, placed in reach the way Core places for a body
        // without a build GUI (ChoreBrain.PlacePiece: paid, creator = the owner, the placed hooks). Null when it can't be built.
        private static TaskOrder BuildForBed(ITaskBody body, Vector3 basePos, float radius, Bed bed, GameObject prefab, Vector3 spot, string where, out string action)
        {
            action = null;
            Inventory bag = body.Inventory;
            long owner = body.OwnerPlayerId;
            Piece piece = prefab.GetComponent<Piece>();
            if (piece == null) return null;
            Predicate<ItemDrop.ItemData> builds = i => i?.m_shared?.m_buildPieces != null && i.m_shared.m_buildPieces.m_pieces.Any(p => p != null && p.name == prefab.name);
            var cost = piece.m_resources.Where(r => r.m_resItem != null && !r.m_upgraderResource && r.m_amount > 0).ToList();
            string shortOf = string.Join(", ", cost.Select(r => (r, have: bag.CountItems(r.m_resItem.m_itemData.m_shared.m_name)
                    + ChestHelper.FindNearbyChests(basePos, radius).Where(c => c != null && ChestHelper.OwnerMayWrite(c, owner)).Sum(c => InventoryTransferService.CountItem(c, r.m_resItem.gameObject.name))))
                .Where(t => t.have < t.r.m_amount).Select(t => $"{t.r.m_resItem.gameObject.name} {t.have}/{t.r.m_amount}"));
            bool hammer = bag.GetAllItems().Any(i => builds(i));
            if (shortOf.Length > 0 || (!hammer && !ChestHolds(body, basePos, radius, builds)))
            {
                BedSay(body, bed, $"bed-heat: {where} has no heat; no {prefab.name}: {(shortOf.Length > 0 ? $"short of {shortOf} in the bag and base chests" : "no hammer")}");
                SkipBaseTarget(bed.gameObject, BedSkipSeconds);
                return null;
            }
            if (!hammer) return FetchFromChest(body, basePos, radius, builds, false, $"bed-heat: fetching a hammer for a {prefab.name} by {where}", out action);
            foreach (Piece.Requirement req in cost)
            {
                string material = req.m_resItem.gameObject.name;
                int missing = req.m_amount - bag.CountItems(req.m_resItem.m_itemData.m_shared.m_name);
                if (missing <= 0) continue;
                Container chest = FindChestWith(basePos, radius, owner, material);
                if (chest == null || Skipped(chest.gameObject)) { SkipBaseTarget(bed.gameObject, BedSkipSeconds); return null; }
                if (Vector3.Distance(body.Position, chest.transform.position) > BaseUseDistance)
                    return MoveOrder(body, chest.gameObject, chest.transform.position, $"bed-heat: fetching {missing} {material} for a {prefab.name} by {where}");
                s_moveSince.Remove(chest.gameObject);
                if (Withdraw(chest, bag, material, missing, owner) <= 0) { SkipBaseTarget(chest.gameObject); return null; }
                action = $"took {material} for a {prefab.name}";
                return null;
            }
            if (Vector3.Distance(body.Position, spot) > PieceReach)
                return MoveOrder(body, bed.gameObject, spot, $"bed-heat: {where} has no heat; placing {prefab.name} at ({spot.x:0}, {spot.z:0})");
            s_moveSince.Remove(bed.gameObject);
            Quaternion facing = Quaternion.LookRotation(Flatten(bed.transform.position - spot));
            if (PlacePiece(prefab.name, spot, facing, bag, owner, out string reason) == null)
            {
                BedSay(body, bed, $"bed-heat: {where}: placing {prefab.name} at ({spot.x:0}, {spot.z:0}) refused: {reason}");
                SkipBaseTarget(bed.gameObject, BedSkipSeconds);
                return null;
            }
            HeatOf(prefab, out Vector3 offset, out float heat);
            action = $"{where} had no heat; placed {prefab.name} at ({spot.x:0}, {spot.z:0}), {Vector3.Distance(spot, bed.transform.position):0.0} m off " +
                     $"(heat reaches {heat - Vector3.Distance(spot + facing * offset, bed.transform.position):0.0} m past the bed)";
            Debug.Log($"[ChoreBrain] {BodyName(body)}: bed-heat: {action}");
            SkipBaseTarget(bed.gameObject, 10f);   // the fire lights on its start fuel; look again once it burns
            return null;
        }

        private static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.0001f ? v : Vector3.forward;
        }

        // A bed's refusal line, once per bed per 5 min (the planner reads them; the chore loop asks every few seconds).
        private static void BedSay(ITaskBody body, Bed bed, string line)
        {
            if (s_bedSaid.TryGetValue(bed.gameObject, out float at) && Time.time - at < BedSkipSeconds) return;
            if (s_bedSaid.Count > 64) s_bedSaid.Clear();
            s_bedSaid[bed.gameObject] = Time.time;
            Debug.Log($"[ChoreBrain] {BodyName(body)}: {line}");
        }

        // ---- 2. Smelters, kilns, charcoal ----

        private static readonly List<Smelter> s_smelters = new List<Smelter>();
        private static Vector3 s_smelterScanAt;
        private static float s_smelterScanTime = -100f;

        private static List<Smelter> BaseSmelters(Vector3 basePos, float radius)
        {
            if (Time.time - s_smelterScanTime < ScanSeconds && (s_smelterScanAt - basePos).sqrMagnitude < 1f) return s_smelters;
            s_smelterScanTime = Time.time;
            s_smelterScanAt = basePos;
            s_smelters.Clear();
            s_smelters.AddRange(SmeltersNear(basePos, radius, null));   // reservations are asked per tick (MayUse), the scan is cached
            return s_smelters;
        }

        private static TaskOrder SmelterStep(ITaskBody body, Vector3 basePos, float radius, out string action)
        {
            action = null;
            // The companions' attention score (output waiting, inputs to hand with room, fuel, empty), most urgent first; 0 = nothing to do.
            Inventory carried = body.Inventory;
            float chestRadius = Mathf.Max(CompanionSettings.ChestSearchRadius, radius);
            List<Smelter> ranked = BaseSmelters(basePos, radius).Where(s => s != null)
                .Select(s => (smelter: s, score: SmelterAttention(s, body.Position, chestRadius, carried, out _)))
                .Where(t => t.score > 0f).OrderByDescending(t => t.score).Select(t => t.smelter).ToList();
            foreach (Smelter smelter in ranked)
            {
                if (smelter == null || Skipped(smelter.gameObject) || !MayUse(body, smelter.gameObject)) continue;
                ZNetView view = smelter.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;
                string name = Utils.GetPrefabName(smelter.gameObject);
                TaskOrder order;
                if (smelter.m_maxFuel > 0 && smelter.m_fuelItem != null)
                {
                    float fuel = view.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
                    if (fuel < smelter.m_maxFuel * SmelterFuelBelow)
                    {
                        string fuelName = smelter.m_fuelItem.m_itemData.m_shared.m_name;
                        int need = smelter.m_maxFuel - 1 - Mathf.CeilToInt(fuel);
                        order = FetchThenFeed(body, basePos, radius, smelter.gameObject, smelter.m_fuelItem.gameObject.name, fuelName, need,
                            $"smelter: {name} fuel {fuel:0}/{smelter.m_maxFuel}", n => FeedSmelterFuel(smelter, body.Inventory, fuelName, n), 0, out action);
                        if (order != null || action != null) return order;
                    }
                }
                int queued = view.GetZDO().GetInt(ZDOVars.s_queued);
                if (smelter.m_maxOre <= 0 || queued >= smelter.m_maxOre) continue;
                bool kiln = PieceDataHelper.IsCharcoalKiln(smelter);
                foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
                {
                    if (conversion?.m_from == null || conversion.m_to == null) continue;
                    string ore = conversion.m_from.gameObject.name, oreName = conversion.m_from.m_itemData.m_shared.m_name;
                    // A kiln burns plain Wood only, and leaves the base KilnWoodReserve of it (FineWood and RoundLog are worth more).
                    if (kiln && ore != "Wood") continue;
                    order = FetchThenFeed(body, basePos, radius, smelter.gameObject, ore, oreName, smelter.m_maxOre - queued,
                        $"smelter: {name} {queued}/{smelter.m_maxOre} queued", n => FeedSmelterOre(smelter, body.Inventory, ore, oreName, n),
                        kiln ? KilnWoodReserve : 0, out action);
                    if (order != null || action != null) return order;
                }
            }
            return null;
        }

        // ---- One copy of the station actions (0.2.236): the companions' SmelterOperatorBehavior and the bot's base chores both call these ----

        /// <summary>
        /// One unit of <paramref name="item"/> (the station's fuel) out of <paramref name="bag"/> into <paramref name="smelter"/>, the way
        /// vanilla Smelter.OnAddFuel does it: refused when the fuel is over max - 1, the item removed first, then RPC_AddFuel to the
        /// station's owner. True when a unit went in.
        /// </summary>
        public static bool AddSmelterFuelOne(Smelter smelter, Inventory bag, ItemDrop.ItemData item)
        {
            ZNetView view = smelter != null ? smelter.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid() || bag == null || item == null || smelter.m_maxFuel <= 0) return false;
            if (view.GetZDO().GetFloat(ZDOVars.s_fuel, 0f) > smelter.m_maxFuel - 1) return false;
            if (!bag.RemoveOneItem(item)) return false;
            view.InvokeRPC("RPC_AddFuel");
            return true;
        }

        /// <summary>
        /// One unit of <paramref name="item"/> (an input of the station) out of <paramref name="bag"/> into <paramref name="smelter"/>, the way
        /// vanilla Smelter.OnAddOre does it: refused when the queue is full, the item removed first, then RPC_AddOre(prefab, cheated) to the
        /// station's owner. True when a unit went in.
        /// </summary>
        public static bool AddSmelterOreOne(Smelter smelter, Inventory bag, ItemDrop.ItemData item)
        {
            ZNetView view = smelter != null ? smelter.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid() || bag == null || item?.m_dropPrefab == null) return false;
            if (view.GetZDO().GetInt(ZDOVars.s_queued) >= smelter.m_maxOre) return false;
            string prefab = item.m_dropPrefab.name;
            bool cheated = item.m_cheated;
            if (!bag.RemoveOneItem(item)) return false;
            view.InvokeRPC("RPC_AddOre", prefab, cheated);
            return true;
        }

        private static int FeedSmelterFuel(Smelter smelter, Inventory bag, string fuelName, int units)
        {
            int added = 0;
            while (added < units)
            {
                ItemDrop.ItemData item = bag.GetAllItems().FirstOrDefault(i => i?.m_shared != null && i.m_shared.m_name == fuelName && i.m_stack > 0);
                if (item == null || !AddSmelterFuelOne(smelter, bag, item)) break;
                added++;
            }
            return added;
        }

        private static int FeedSmelterOre(Smelter smelter, Inventory bag, string orePrefab, string oreName, int units)
        {
            int added = 0;
            while (added < units)
            {
                ItemDrop.ItemData item = bag.GetAllItems().FirstOrDefault(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == orePrefab && i.m_stack > 0);
                if (item == null || !AddSmelterOreOne(smelter, bag, item)) break;
                added++;
            }
            return added;
        }

        /// <summary>
        /// Repair <paramref name="piece"/> to full (the companions' BuildingRepairBehavior way, now one copy for every body, 0.2.236): claim
        /// it so vanilla WearNTear.Repair's owner-side RPC_Repair runs here, try vanilla Repair (syncs to every peer); ownership isn't
        /// instant, so fall back to the same write RPC_Repair does (the health ZDO key + RPC_HealthChanged to everybody). Wards are the
        /// caller's to check. True when it was repaired (or written to full).
        /// </summary>
        public static bool RepairPiece(WearNTear piece)
        {
            ZNetView view = piece != null ? piece.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid()) return false;
            view.ClaimOwnership();
            if (piece.Repair()) return true;
            float max = piece.m_health;   // world-level-scaled max, the value RPC_Repair uses
            if (max <= 0f) return false;
            ZDO zdo = view.GetZDO();
            if (zdo == null || zdo.GetFloat(ZDOVars.s_health, max) >= max) return false;
            zdo.Set(ZDOVars.s_health, max);
            view.InvokeRPC(ZNetView.Everybody, "RPC_HealthChanged", (object)max);
            return true;
        }

        // ---- 3. Loose drops ----

        private static TaskOrder PickupStep(ITaskBody body, Vector3 basePos, float radius, out string action)
        {
            action = null;
            Inventory bag = body.Inventory;
            // LootToTake: the companions' loot rules and priority (trophies, metals, materials …), what fits in this bag.
            foreach (ItemDrop drop in LootToTake(basePos, radius, body.Position, d => !Skipped(d.gameObject) && bag.CanAddItem(d.m_itemData)))
            {
                TaskOrder order = InteractOrder(body, drop.gameObject, drop.transform.position, $"pickup: {Utils.GetPrefabName(drop.gameObject)} x{drop.m_itemData.m_stack}");
                if (order != null) return order;
            }
            return null;
        }

        // ---- 4. Crops and hives ----

        private static TaskOrder HarvestStep(ITaskBody body, Vector3 basePos, float radius, out string action)
        {
            action = null;
            // The companions' farming order: hives first, then crops, nearest first, the owner's wards.
            long owner = body.OwnerPlayerId;
            Func<Vector3, bool> wards = p => ChestHelper.WardsAllow(p, owner);
            var targets = new List<(GameObject go, string what)>();
            foreach (Beehive hive in HivesToHarvest(basePos, radius, body.Position, wards, BodyName(body)))
                targets.Add((hive.gameObject, $"harvest: {Utils.GetPrefabName(hive.gameObject)} ({FarmingDataHelper.GetStoredProduce(hive)} {FarmingDataHelper.GetProduceName(hive)})"));
            foreach (Pickable crop in CropsToHarvest(basePos, radius, body.Position, wards, BodyName(body)))
                targets.Add((crop.gameObject, $"harvest: {Utils.GetPrefabName(crop.gameObject)}"));
            foreach (var (go, what) in targets)
            {
                if (Skipped(go)) continue;
                TaskOrder order = InteractOrder(body, go, go.transform.position, what);
                if (order != null) return order;
            }
            return null;
        }

        // ---- 5. Replanting ----

        private static readonly List<Vector3> s_plantSpots = new List<Vector3>();
        private static Vector3 s_plantScanAt;
        private static float s_plantScanTime = -100f;
        // Saplings planted by bodies this session, by prefab (a seed that grows two crops alternates, as FarmingBehavior does).
        private static readonly Dictionary<string, int> s_planted = new Dictionary<string, int>();

        private static TaskOrder PlantStep(ITaskBody body, Vector3 basePos, float radius, out string action)
        {
            action = null;
            Inventory bag = body.Inventory;
            if (Time.time - s_plantScanTime >= PlantScanSeconds || (s_plantScanAt - basePos).sqrMagnitude >= 1f)
            {
                s_plantScanTime = Time.time;
                s_plantScanAt = basePos;
                s_plantSpots.Clear();
                s_plantSpots.AddRange(FarmingDataHelper.FindPlantablePositions(basePos, Mathf.Min(radius, PlantSearchRadius), 1f));
            }
            if (s_plantSpots.Count == 0) return null;
            Predicate<ItemDrop.ItemData> seedHere = item => IsSeedFor(item, s_plantSpots);
            // The companions' sourcing (FarmingBehavior: seeds that grow here and a cultivator, from storage or a nearby chest), from
            // the bag or a base chest the owner may use. Seeds come first: with nothing to plant no cultivator is fetched. Both must be
            // had before either is fetched (fetched seeds would only go back into the chest).
            bool cultivator = bag.GetAllItems().Any(IsCultivator), seeds = bag.GetAllItems().Any(i => seedHere(i));
            if (!seeds && !ChestHolds(body, basePos, radius, seedHere)) return null;   // nothing to plant: no cultivator wanted either
            if (!cultivator && !ChestHolds(body, basePos, radius, IsCultivator))
            {
                // None to carry or fetch: the bot crafts one through its planner's craft verb (FDT, [seasons]' ifstocked) on the shared
                // CraftFromStock decision, not here (one craft path per body). The decision, once a minute, as evidence.
                if (LogDue((BodyName(body) + "plant-craft").GetHashCode(), 60f))
                {
                    string why = CraftFromStock("Cultivator", bag, body.Position, basePos, radius, body.OwnerPlayerId, out StockCraft plan);
                    Debug.Log($"[ChoreBrain] {BodyName(body)}: plant: no cultivator in the bag or base chests; craft from stock: " +
                              (why == "" ? $"yes at {plan.StationName} ({plan.Cost}; fetch {plan.Fetch.Count} take(s))" : $"no ({why})"));
                }
                return null;
            }
            if (!cultivator) return FetchFromChest(body, basePos, radius, IsCultivator, false, "plant: fetching a cultivator", out action);
            if (!seeds) return FetchFromChest(body, basePos, radius, seedHere, true, "plant: fetching seeds", out action);
            // ChoosePlanting: the companions' planting choice (least-planted sapling first, seed cost covered, nearest valid spot).
            if (!ChoosePlanting(bag, s_plantSpots, body.Position, s => s_planted.TryGetValue(s, out int n) ? n : 0, out FarmingDataHelper.CropSapling crop, out Vector3 spot))
                return null;
            if (Vector3.Distance(body.Position, spot) > InteractReach)
                return MoveOrder(body, null, spot, $"plant: {crop.Prefab.name} at ({spot.x:0}, {spot.z:0})");
            if (crop.Seeds != null) foreach (var (item, amount) in crop.Seeds) RemovePrefab(bag, item, amount);
            FarmingDataHelper.PlantSapling(crop, spot);
            s_plantSpots.Remove(spot);
            s_planted.TryGetValue(crop.Prefab.name, out int planted);
            s_planted[crop.Prefab.name] = planted + 1;
            action = $"{crop.Prefab.name} at ({spot.x:0}, {spot.z:0})";
            Debug.Log($"[ChoreBrain] {BodyName(body)}: planted {action}");
            return null;
        }

        /// <summary>The game's recipe for the item prefab <paramref name="itemPrefab"/> (ObjectDB.GetRecipe), or null.</summary>
        public static Recipe RecipeFor(string itemPrefab)
        {
            if (ObjectDB.instance == null || string.IsNullOrEmpty(itemPrefab)) return null;
            ItemDrop drop = ObjectDB.instance.GetItemPrefab(itemPrefab)?.GetComponent<ItemDrop>();
            return drop != null ? ObjectDB.instance.GetRecipe(drop.m_itemData) : null;
        }

        /// <summary>What crafting one item from base stock takes (see <c>CraftFromStock</c>).</summary>
        public sealed class StockCraft
        {
            /// <summary>The game's recipe (ObjectDB.GetRecipe).</summary>
            public Recipe Recipe;
            /// <summary>The station to craft at (its kind, level, roof and fire checked), or null for a recipe made by hand.</summary>
            public CraftingStation Station;
            /// <summary>What to take into the bag first, chest by chest, nearest first; empty when the bag already covers the cost.</summary>
            public readonly List<(Container Chest, string Prefab, int Amount)> Fetch = new List<(Container Chest, string Prefab, int Amount)>();
            /// <summary>The cost, "RoundLog 5, Bronze 5" (for lines).</summary>
            public string Cost = "";
            /// <summary>The station's prefab name, or "hand".</summary>
            public string StationName => Station != null ? Utils.GetPrefabName(Station.gameObject) : "hand";
        }

        /// <summary>
        /// The one craft-from-stock decision for every body (0.2.242; the companions' farming, the FDT planner): can
        /// <paramref name="itemPrefab"/> be made by its game recipe at a station of its kind and level within <paramref name="radius"/>
        /// of <paramref name="basePos"/> (<see cref="CraftStationRefusal"/>: level, roof, fire), paid from <paramref name="bag"/> plus the
        /// base chests <paramref name="mayUse"/> allows (null = all), with the whole cost covered (never a part, so nothing is fetched
        /// only to go back, and no trip away from base for a material)? "" when yes, with <paramref name="plan"/> (what to fetch from
        /// which chest, nearest to <paramref name="from"/> first); otherwise a coded reason: "missing: …" (no such recipe),
        /// "nostation: …", "station: …", "roof: …", "stock: Bronze 2/5 (bag 0, chests 2)". The craft itself is the body's:
        /// ResourceDataHelper.CraftTool for a companion, the craft input for the bot.
        /// </summary>
        public static string CraftFromStock(string itemPrefab, Inventory bag, Vector3 from, Vector3 basePos, float radius,
            Func<Container, bool> mayUse, out StockCraft plan)
        {
            plan = null;
            Recipe recipe = RecipeFor(itemPrefab);
            if (recipe == null) return $"missing: no recipe for {itemPrefab}";
            if (bag == null) return "missing: no bag";
            string refusal = CraftStationRefusal(recipe, basePos, radius, out CraftingStation station);
            if (refusal != "") return refusal;
            var result = new StockCraft { Recipe = recipe, Station = station };
            List<Piece.Requirement> cost = UpgradeRequirements(recipe, 1);
            result.Cost = string.Join(", ", cost.Select(r => $"{r.m_resItem.gameObject.name} {r.m_amount}"));
            List<Container> chests = ChestHelper.FindNearbyChests(basePos, radius)
                .Where(c => c != null && (mayUse == null || mayUse(c)))
                .OrderBy(c => (c.transform.position - from).sqrMagnitude).ToList();
            var shortOf = new List<string>();
            foreach (Piece.Requirement req in cost)
            {
                string prefab = req.m_resItem.gameObject.name;
                int inBag = bag.CountItems(req.m_resItem.m_itemData.m_shared.m_name);
                int inChests = chests.Sum(c => InventoryTransferService.CountItem(c, prefab));
                if (inBag + inChests < req.m_amount) { shortOf.Add($"{prefab} {inBag + inChests}/{req.m_amount} (bag {inBag}, chests {inChests})"); continue; }
                int missing = req.m_amount - inBag;
                foreach (Container chest in chests)
                {
                    if (missing <= 0) break;
                    int take = Mathf.Min(missing, InventoryTransferService.CountItem(chest, prefab));
                    if (take <= 0) continue;
                    result.Fetch.Add((chest, prefab, take));
                    missing -= take;
                }
            }
            if (shortOf.Count > 0) return "stock: " + string.Join(", ", shortOf);
            plan = result;
            return "";
        }

        /// <summary>CraftFromStock with the base chests the owner may write to (the bot's rule).</summary>
        public static string CraftFromStock(string itemPrefab, Inventory bag, Vector3 from, Vector3 basePos, float radius, long ownerId, out StockCraft plan) =>
            CraftFromStock(itemPrefab, bag, from, basePos, radius, c => ChestHelper.OwnerMayWrite(c, ownerId), out plan);

        // A base chest the owner may use holds an item matching match.
        private static bool ChestHolds(ITaskBody body, Vector3 basePos, float radius, Predicate<ItemDrop.ItemData> match) =>
            ChestHelper.FindNearbyChests(basePos, radius).Any(c => c != null && !Skipped(c.gameObject) && ChestHelper.OwnerMayWrite(c, body.OwnerPlayerId)
                                                                   && (c.GetInventory()?.GetAllItems().Any(i => match(i)) ?? false));

        // From a base chest the owner may use that holds an item matching match: walk to it, then take every matching stack (all = true:
        // seeds, as TakeAllSeedsFromChest does) or one item (a tool). Null with no such chest, or after taking (action says what).
        private static TaskOrder FetchFromChest(ITaskBody body, Vector3 basePos, float radius, Predicate<ItemDrop.ItemData> match, bool all,
            string reason, out string action)
        {
            action = null;
            foreach (Container chest in ChestHelper.FindNearbyChests(basePos, radius).OrderBy(c => (c.transform.position - body.Position).sqrMagnitude))
            {
                if (chest == null || Skipped(chest.gameObject) || !ChestHelper.OwnerMayWrite(chest, body.OwnerPlayerId)) continue;
                List<ItemDrop.ItemData> held = chest.GetInventory()?.GetAllItems().Where(i => match(i)).ToList();
                if (held == null || held.Count == 0) continue;
                string chestName = Utils.GetPrefabName(chest.gameObject);
                if (Vector3.Distance(body.Position, chest.transform.position) > BaseUseDistance)
                    return MoveOrder(body, chest.gameObject, chest.transform.position, $"{reason} from {chestName}");
                s_moveSince.Remove(chest.gameObject);
                var taken = new List<string>();
                foreach (var stack in (all ? held : held.Take(1)).GroupBy(i => i.m_dropPrefab.name))
                {
                    int amount = all ? stack.Sum(i => i.m_stack) : 1;
                    int got = Withdraw(chest, body.Inventory, stack.Key, amount, body.OwnerPlayerId);
                    if (got > 0) taken.Add($"{got} {stack.Key}");
                }
                if (taken.Count == 0) { SkipBaseTarget(chest.gameObject); continue; }
                action = $"took {string.Join(", ", taken)} from {chestName}";
                Debug.Log($"[ChoreBrain] {BodyName(body)}: {reason.Replace("fetching", "fetched")}: {action}");
                return null;
            }
            return null;
        }

        private static int CountPrefab(Inventory bag, string prefab) =>
            bag.GetAllItems().Where(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == prefab).Sum(i => i.m_stack);

        private static void RemovePrefab(Inventory bag, string prefab, int amount)
        {
            foreach (ItemDrop.ItemData item in bag.GetAllItems().Where(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == prefab).ToList())
            {
                if (amount <= 0) break;
                int take = Mathf.Min(amount, item.m_stack);
                bag.RemoveItem(item, take);
                amount -= take;
            }
        }

        // ---- 6. Damaged pieces ----

        private static readonly List<WearNTear> s_damaged = new List<WearNTear>();
        private static Vector3 s_damagedScanAt;
        private static float s_damagedScanTime = -100f;
        private static int s_pieceMask;
        private static int PieceMask => s_pieceMask != 0 ? s_pieceMask : (s_pieceMask = LayerMask.GetMask("piece", "piece_nonsolid"));

        private static TaskOrder PieceRepairStep(ITaskBody body, Vector3 basePos, float radius, out string action)
        {
            action = null;
            if (!body.Inventory.GetAllItems().Any(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == "Hammer")) return null;
            if (Time.time - s_damagedScanTime >= ScanSeconds || (s_damagedScanAt - basePos).sqrMagnitude >= 1f)
            {
                s_damagedScanTime = Time.time;
                s_damagedScanAt = basePos;
                s_damaged.Clear();
                long owner = body.OwnerPlayerId;
                s_damaged.AddRange(DamagedPieces(basePos, radius, p => ChestHelper.WardsAllow(p, owner)));   // the companions' repair rules
            }
            foreach (WearNTear piece in s_damaged.ToList())
            {
                if (piece == null || Skipped(piece.gameObject) || !MayUse(body, piece.gameObject)) continue;
                float health = piece.GetHealthPercentage();
                if (health >= PieceRepairBelow) continue;
                Reserve(body, piece.gameObject);
                string name = $"{Utils.GetPrefabName(piece.gameObject)} {health * 100f:0} %";
                Collider shape = piece.GetComponentInChildren<Collider>();
                Vector3 near = shape != null ? shape.bounds.ClosestPoint(body.Position) : piece.transform.position;
                if (Vector3.Distance(body.Position, near) > PieceReach)
                {
                    TaskOrder walk = MoveOrder(body, piece.gameObject, near, $"repair-piece: {name}");
                    if (walk != null) return walk;
                    continue;
                }
                s_moveSince.Remove(piece.gameObject);
                // RepairPiece: vanilla WearNTear.Repair (the hammer's repair, material-free), one copy with the companions.
                bool repaired = RepairPiece(piece);
                Unreserve(body, piece.gameObject);
                if (!repaired) { SkipBaseTarget(piece.gameObject, 2f); continue; }
                action = $"{name} -> repaired";
                Debug.Log($"[ChoreBrain] {BodyName(body)}: repaired {name}");
                return null;
            }
            return null;
        }

        // ---- Cooking stations (0.2.235; CompanionCookingBehavior's rules: stations fed directly, not through an add-food switch) ----

        private static readonly List<CookingStation> s_cookers = new List<CookingStation>();
        private static Vector3 s_cookerScanAt;
        private static float s_cookerScanTime = -100f;
        private static readonly Dictionary<CookingStation, int> s_cookerState = new Dictionary<CookingStation, int>();

        private static TaskOrder CookStep(ITaskBody body, Vector3 basePos, float radius, out string action)
        {
            action = null;
            if (Time.time - s_cookerScanTime >= ScanSeconds || (s_cookerScanAt - basePos).sqrMagnitude >= 1f)
            {
                s_cookerScanTime = Time.time;
                s_cookerScanAt = basePos;
                s_cookers.Clear();
                s_cookers.AddRange(CookingStationsNear(basePos, radius, basePos, null));   // the companions' station rules; reservations per tick
            }
            Inventory bag = body.Inventory;
            Vector3 at = body.Position;
            foreach (CookingStation station in s_cookers.OrderBy(c => c != null ? (c.transform.position - at).sqrMagnitude : float.MaxValue).ToList())
            {
                if (station == null || Skipped(station.gameObject) || !MayUse(body, station.gameObject)) continue;
                ZNetView view = station.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;
                string name = Utils.GetPrefabName(station.gameObject);
                bool done = CompanionCookingBehavior.HasFinishedFood(station), free = CompanionCookingBehavior.HasFreeSlot(station);
                // Several interacts on one station are normal (one slot each): the tries count restarts whenever its slots change.
                int state = (done ? 1 : 0) + (free ? 2 : 0) + 4 * CountFilledSlots(station);
                if (!s_cookerState.TryGetValue(station, out int last) || last != state)
                {
                    if (s_cookerState.Count > 64) s_cookerState.Clear();
                    s_cookerState[station] = state;
                    s_interactTries.Remove(station.gameObject);
                }
                // Vanilla CookingStation.OnInteract takes done (or burnt) food off first; the drop lands for the pickup chore.
                if (done)
                {
                    TaskOrder take = InteractOrder(body, station.gameObject, station.transform.position, $"cook: taking the done food off {name}");
                    if (take != null) { Reserve(body, station.gameObject); return take; }
                    continue;
                }
                if (!free || (station.m_requireFire && !station.IsFireLit())) continue;
                string raw = CookableIn(station, bag);
                if (raw == null)
                {
                    // Raw food from a base chest the owner may use (the companions pull it from chests too).
                    foreach (CookingStation.ItemConversion conversion in station.m_conversion)
                    {
                        if (conversion?.m_from == null) continue;
                        string prefab = conversion.m_from.gameObject.name;
                        Container chest = FindChestWith(basePos, radius, body.OwnerPlayerId, prefab);
                        if (chest == null || Skipped(chest.gameObject)) continue;
                        if (Vector3.Distance(at, chest.transform.position) > BaseUseDistance)
                            return MoveOrder(body, chest.gameObject, chest.transform.position, $"cook: fetching {prefab} for {name}");
                        s_moveSince.Remove(chest.gameObject);
                        if (Withdraw(chest, bag, prefab, station.m_slots.Length, body.OwnerPlayerId) > 0) { raw = prefab; break; }
                        SkipBaseTarget(chest.gameObject);
                    }
                    if (raw == null) continue;
                }
                TaskOrder put = InteractOrder(body, station.gameObject, station.transform.position, $"cook: putting {raw} on {name}");
                if (put != null) { Reserve(body, station.gameObject); return put; }
            }
            return null;
        }

        // The first input of station the bag holds (its prefab name), or null.
        private static string CookableIn(CookingStation station, Inventory bag)
        {
            foreach (CookingStation.ItemConversion conversion in station.m_conversion)
                if (conversion?.m_from != null && bag.CountItems(conversion.m_from.m_itemData.m_shared.m_name) > 0)
                    return conversion.m_from.gameObject.name;
            return null;
        }

        private static int CountFilledSlots(CookingStation station)
        {
            ZNetView view = station.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || station.m_slots == null) return 0;
            int filled = 0;
            for (int i = 0; i < station.m_slots.Length; i++)
                if (view.GetZDO().GetString("slot" + i) != "") filled++;
            return filled;
        }

        // ---- Bench upgrades (0.2.235; CraftingUpgradeBehaviorV2 on ChoreBrain.FindBestUpgrade: what the body wears, materials from its bag) ----

        private static TaskOrder UpgradeStep(ITaskBody body, Vector3 basePos, float radius, out string action)
        {
            action = null;
            CraftingStation station = FindWorkstation(basePos, radius);
            if (station == null || Skipped(station.gameObject)) return null;
            Inventory bag = body.Inventory;
            // What is worn first, weapons before armour (the companions' PrioritySlots order).
            List<ItemDrop.ItemData> worn = bag.GetAllItems().Where(i => i != null && i.m_equipped)
                .OrderBy(i => i.IsWeapon() ? 0 : i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield ? 1 : 2).ToList();
            if (worn.Count == 0) return null;
            UpgradeChoice choice = FindBestUpgrade(station, worn, bag);
            if (choice?.Item == null) return null;
            string stationName = Utils.GetPrefabName(station.gameObject);
            string item = choice.Item.m_dropPrefab != null ? choice.Item.m_dropPrefab.name : choice.Item.m_shared.m_name;
            float reach = Mathf.Max(BaseUseDistance, station.m_useDistance - 0.5f);
            if (Vector3.Distance(body.Position, station.transform.position) > reach)
                return MoveOrder(body, station.gameObject, station.transform.position, $"upgrade: {item} to quality {choice.TargetQuality} at {stationName}");
            s_moveSince.Remove(station.gameObject);
            // What vanilla's crafting window does for an upgrade (InventoryGui.DoCrafting): the materials out of the bag, the quality up,
            // full durability.
            if (!ConsumeRequirements(choice.Requirements, bag)) { SkipBaseTarget(station.gameObject); return null; }
            choice.Item.m_quality = choice.TargetQuality;
            choice.Item.m_durability = choice.Item.GetMaxDurability();
            action = $"{item} -> quality {choice.TargetQuality} at {stationName}";
            Debug.Log($"[ChoreBrain] {BodyName(body)}: upgraded {action}");
            return null;
        }

        // ---- 7. Stash the bag, 8. worn gear ----

        // The companions' deposit rule (ChestHelper.SmartDeposit, 0.2.241): every item into a base chest that already holds it, so stacks
        // stay together; the rest into the smart-storage pick (FindBestDepositChest: chests near the matching station, then stacking,
        // then the nearest with room). A player body walks to each chest in turn: the nearest chest with work first.
        private static TaskOrder DepositStep(ITaskBody body, Vector3 basePos, float radius, out string action)
        {
            action = null;
            Inventory bag = body.Inventory;
            List<ItemDrop.ItemData> items = ChestHelper.GetDepositableItems(bag);
            if (items.Count == 0) return null;
            long owner = body.OwnerPlayerId;
            List<Container> chests = ChestHelper.FindNearbyChests(basePos, radius)
                .Where(c => c != null && !Skipped(c.gameObject) && ChestHelper.OwnerMayWrite(c, owner)).ToList();
            if (chests.Count == 0) return null;
            Container fallback = ChestHelper.FindBestDepositChest(basePos, chests, bag);
            var plan = new Dictionary<Container, HashSet<string>>();
            foreach (ItemDrop.ItemData item in items)
            {
                string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : null;
                if (prefab == null) continue;
                Container target = ChestHelper.FindChestWithItem(chests, item);
                if (target == null || !(target.GetInventory()?.CanAddItem(item) ?? false))
                    target = fallback != null && (fallback.GetInventory()?.CanAddItem(item) ?? false) ? fallback
                        : chests.OrderBy(c => (c.transform.position - body.Position).sqrMagnitude).FirstOrDefault(c => c.GetInventory()?.CanAddItem(item) ?? false);
                if (target == null) continue;
                if (!plan.TryGetValue(target, out HashSet<string> prefabs)) plan[target] = prefabs = new HashSet<string>();
                prefabs.Add(prefab);
            }
            if (plan.Count == 0) return null;
            Container next = plan.Keys.OrderBy(c => (c.transform.position - body.Position).sqrMagnitude).First();
            string chestName = Utils.GetPrefabName(next.gameObject);
            if (Vector3.Distance(body.Position, next.transform.position) > BaseUseDistance)
                return MoveOrder(body, next.gameObject, next.transform.position, $"deposit: {string.Join(", ", plan[next])} into {chestName}");
            s_moveSince.Remove(next.gameObject);
            int moved = 0;
            foreach (string prefab in plan[next])
            {
                var result = InventoryTransferService.DepositItem(bag, next, prefab, int.MaxValue, owner);
                if (result.Success) moved += result.AmountTransferred;
            }
            if (moved <= 0) { SkipBaseTarget(next.gameObject); return null; }
            action = $"{moved} item(s) into {chestName} ({string.Join(", ", plan[next])})";
            Debug.Log($"[ChoreBrain] {BodyName(body)}: deposited {action}");
            return null;
        }

        private static TaskOrder GearRepairStep(ITaskBody body, Vector3 basePos, float radius, out string action)
        {
            action = null;
            TaskOrder order = NextRepair(body);
            return order.Kind == TaskKind.Done || order.Kind == TaskKind.Failed ? null : order;
        }

        private static string BodyName(ITaskBody body) => body?.Character is Player p ? p.GetPlayerName() : body?.Character != null ? body.Character.m_name : "body";
    }
}
