using System.Collections.Generic;
using FiresCore.Npc.Core;
using FiresCore.Npc.IdleBehaviors;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>What a task order asks the body to do this step.</summary>
    public enum TaskKind
    {
        /// <summary>Walk to <see cref="TaskOrder.Point"/>.</summary>
        Move,
        /// <summary>Put <see cref="TaskOrder.Item"/> in hand.</summary>
        Equip,
        /// <summary>Face <see cref="TaskOrder.Point"/> and swing the item in hand (a tool at a tree or rock).</summary>
        Swing,
        /// <summary>Use <see cref="TaskOrder.Target"/> (pick a pickable, open a station).</summary>
        Interact,
        /// <summary>The task is finished.</summary>
        Done,
        /// <summary>The task can't go on; <see cref="TaskOrder.Reason"/> says why.</summary>
        Failed,
        /// <summary>
        /// Repair <see cref="TaskOrder.Item"/> at the station <see cref="TaskOrder.Target"/> the body is standing at: a player
        /// opens it and repairs as a player does (vanilla InventoryGui repair); <see cref="ChoreBrain.RepairAt"/> does it directly.
        /// </summary>
        Repair,
    }

    /// <summary>One step of a chore, from <see cref="ChoreBrain"/>; the body carries it out and asks again.</summary>
    public sealed class TaskOrder
    {
        public TaskKind Kind;
        public Vector3 Point;
        public GameObject Target;
        public ItemDrop.ItemData Item;
        public string Reason = "";

        public static TaskOrder Move(Vector3 point) => new TaskOrder { Kind = TaskKind.Move, Point = point };
        public static TaskOrder Done(string reason = "") => new TaskOrder { Kind = TaskKind.Done, Reason = reason };
        public static TaskOrder Failed(string reason) => new TaskOrder { Kind = TaskKind.Failed, Reason = reason };

        public override string ToString() => $"{Kind}{(Target != null ? " " + Target.name : "")}{(Item != null ? " " + Item.m_shared.m_name : "")}{(Reason.Length > 0 ? " (" + Reason + ")" : "")}";
    }

    /// <summary>What a workbench can upgrade for a body, and what it costs.</summary>
    public sealed class UpgradeChoice
    {
        public ItemDrop.ItemData Item;
        public Recipe Recipe;
        public int TargetQuality;
        public List<Piece.Requirement> Requirements;
    }

    /// <summary>
    /// The companions' chore decisions for any body (Tools\COMPANION_BRAIN_PLAN.md slice 2b): which chest to fill or empty, which
    /// tree, rock or pickable to gather with which tool, and which station can upgrade which item. Chest writes go through
    /// ChestHelper's claim with the body's owner id, so a player body (the FDT bot) and a companion use the same rights.
    /// </summary>
    public static class ChoreBrain
    {
        // ---- Chests ----

        /// <summary>
        /// The chest to empty <paramref name="inventory"/> into, near <paramref name="at"/>: one <paramref name="ownerId"/> may write
        /// (not open, privacy, wards), preferring one that already stacks what the body carries (ChestHelper.FindBestDepositChest).
        /// </summary>
        public static Container FindDepositChest(Vector3 at, float radius, long ownerId, Inventory inventory)
        {
            var chests = ChestHelper.FindNearbyChests(at, radius);
            chests.RemoveAll(chest => !ChestHelper.OwnerMayWrite(chest, ownerId));
            return chests.Count == 0 ? null : ChestHelper.FindBestDepositChest(at, chests, inventory);
        }

        /// <summary>The nearest chest <paramref name="ownerId"/> may write that holds <paramref name="prefab"/>.</summary>
        public static Container FindChestWith(Vector3 at, float radius, long ownerId, string prefab)
        {
            Container best = null;
            float bestDistance = float.MaxValue;
            foreach (Container chest in ChestHelper.FindNearbyChests(at, radius))
            {
                if (!ChestHelper.OwnerMayWrite(chest, ownerId)) continue;
                if (InventoryTransferService.CountItem(chest, prefab) <= 0) continue;
                float distance = Vector3.Distance(at, chest.transform.position);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = chest;
            }
            return best;
        }

        /// <summary>
        /// Moves everything depositable (not food, weapons, armour, tools; ChestHelper.GetDepositableItems) from
        /// <paramref name="inventory"/> into <paramref name="chest"/> with <paramref name="ownerId"/>'s rights. Returns the units moved.
        /// </summary>
        public static int DepositAll(Inventory inventory, Container chest, long ownerId)
        {
            if (inventory == null || chest == null) return 0;
            int moved = 0;
            var seen = new HashSet<string>();
            foreach (ItemDrop.ItemData item in ChestHelper.GetDepositableItems(inventory))
            {
                string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : null;
                if (string.IsNullOrEmpty(prefab) || !seen.Add(prefab)) continue;
                var result = InventoryTransferService.DepositItem(inventory, chest, prefab, int.MaxValue, ownerId);
                if (result.Success) moved += result.AmountTransferred;
            }
            if (moved > 0) Debug.Log($"[ChoreBrain] deposited {moved} item(s) into {chest.m_name} for {ownerId}");
            return moved;
        }

        /// <summary>Takes up to <paramref name="amount"/> of <paramref name="prefab"/> out of <paramref name="chest"/>. Returns the units taken.</summary>
        public static int Withdraw(Container chest, Inventory inventory, string prefab, int amount, long ownerId)
        {
            if (chest == null || inventory == null) return 0;
            var result = InventoryTransferService.PullItem(chest, inventory, prefab, amount, ownerId);
            if (result.Success) Debug.Log($"[ChoreBrain] took {result.AmountTransferred} {prefab} from {chest.m_name} for {ownerId}");
            return result.Success ? result.AmountTransferred : 0;
        }

        /// <summary>
        /// The next step of emptying a body's pack: walk to a writable chest, then deposit (done at once, the way a companion's
        /// transfer is). Done when nothing depositable is left; Failed when no chest is in reach.
        /// </summary>
        public static TaskOrder NextDeposit(ITaskBody body, float radius = 30f, float useDistance = 2.5f)
        {
            if (body == null || body.Inventory == null) return TaskOrder.Failed("no body");
            if (ChestHelper.GetDepositableItems(body.Inventory).Count == 0) return TaskOrder.Done("nothing to deposit");
            Container chest = FindDepositChest(body.Position, radius, body.OwnerPlayerId, body.Inventory);
            if (chest == null) return TaskOrder.Failed("no chest with room this body may use");
            if (Vector3.Distance(body.Position, chest.transform.position) > useDistance)
                return new TaskOrder { Kind = TaskKind.Move, Point = chest.transform.position, Target = chest.gameObject };
            int moved = DepositAll(body.Inventory, chest, body.OwnerPlayerId);
            return moved > 0 ? TaskOrder.Done($"deposited {moved}") : TaskOrder.Failed("the chest took nothing");
        }

        // ---- Gathering ----

        /// <summary>The best item in <paramref name="items"/> for <paramref name="tool"/> work at <paramref name="minTier"/> or above (ResourceDataHelper.IsBetterTool).</summary>
        public static ItemDrop.ItemData BestTool(IEnumerable<ItemDrop.ItemData> items, ResourceDataHelper.ToolType tool, int minTier)
        {
            ItemDrop.ItemData best = null;
            if (items == null) return null;
            foreach (ItemDrop.ItemData item in items)
            {
                if (item == null || !ResourceDataHelper.IsToolAppropriate(item, tool, minTier)) continue;
                if (ResourceDataHelper.IsBetterTool(item, best, tool)) best = item;
            }
            return best;
        }

        /// <summary>
        /// The nearest thing a body can gather with what it carries: a pickable, or a tree / log / rock its best tool can hurt, where
        /// <paramref name="ownerId"/>'s wards allow. With <paramref name="wanted"/>, only what drops one of those prefabs.
        /// </summary>
        public static ResourceDataHelper.ResourceData FindGatherTarget(Vector3 at, float range, IEnumerable<ItemDrop.ItemData> carried,
            long ownerId, ICollection<string> wanted = null)
        {
            var candidates = GatherCandidates(at, range, carried, ownerId, wanted);
            ResourceDataHelper.ResourceData noPathFallback = null;
            foreach (var candidate in candidates)
            {
                if (candidate.Usable) return candidate.Data;
                // The navmesh may not be built around an unvisited spot yet: a node that only failed the path check still beats none.
                if (noPathFallback == null && candidate.Reason == "no path") noPathFallback = candidate.Data;
            }
            return noPathFallback;
        }

        /// <summary>
        /// Whether a body carrying <paramref name="carried"/> can gather <paramref name="target"/> (a tree, log, rock, destructible or
        /// pickable) with its best tool, and why not: "not a gatherable resource", "needs an axe", "needs an axe of tier N (best
        /// carried M)", "needs a pickaxe …"; "ok" when it can. The tool rule <see cref="GatherCandidates"/> uses, for any body (FDT's
        /// clear / gather verbs and the companions agree). Wards, drops and paths are not asked here.
        /// </summary>
        public static bool CanGather(GameObject target, IEnumerable<ItemDrop.ItemData> carried, out string reason)
        {
            var data = target != null ? ResourceDataHelper.GetResourceData(target) : null;
            if (data == null)
            {
                reason = "not a gatherable resource";
                return false;
            }
            var items = carried != null ? new List<ItemDrop.ItemData>(carried) : new List<ItemDrop.ItemData>();
            reason = ToolReason(data, ResourceDataHelper.BestToolTier(items, ResourceDataHelper.ToolType.Axe),
                ResourceDataHelper.BestToolTier(items, ResourceDataHelper.ToolType.Pickaxe)) ?? "ok";
            return reason == "ok";
        }

        // Null when the best axe / pickaxe carried can hurt it (or it needs none).
        private static string ToolReason(ResourceDataHelper.ResourceData data, int axeTier, int pickTier)
        {
            if (data.RequiredTool == ResourceDataHelper.ToolType.Axe && !(axeTier >= 0 && data.CanGatherWithTier(axeTier)))
                return axeTier < 0 ? "needs an axe" : $"needs an axe of tier {data.MinToolTier} (best carried {axeTier})";
            if (data.RequiredTool == ResourceDataHelper.ToolType.Pickaxe && !(pickTier >= 0 && data.CanGatherWithTier(pickTier)))
                return pickTier < 0 ? "needs a pickaxe" : $"needs a pickaxe of tier {data.MinToolTier} (best carried {pickTier})";
            return null;
        }

        /// <summary>One gatherable node near a body, with the verdict on it (<see cref="GatherCandidates"/>).</summary>
        public struct GatherCandidate
        {
            public ResourceDataHelper.ResourceData Data;
            public GameObject Object;
            public string Prefab;
            public Vector3 Position;
            /// <summary>Straight-line metres from the asking point.</summary>
            public float Distance;
            public ResourceDataHelper.ResourceType Type;
            public bool Usable;
            /// <summary>"ok", or why not: "wards", "doesn't drop X", "needs an axe of tier N (best carried M)", "no path", "path … x the straight …".</summary>
            public string Reason;
            /// <summary>Metres along the navmesh path when it was checked, else -1.</summary>
            public float PathLength;
        }

        // Path checks cost a navmesh search each: only the nearest few candidates that pass everything else get one.
        private const int PathChecks = 4;
        private const float DetourFactor = 2f;
        private const float DetourSlack = 5f;
        private static readonly List<GatherCandidate> s_candidates = new List<GatherCandidate>();
        private static readonly List<Vector3> s_path = new List<Vector3>();

        /// <summary>
        /// Every gatherable node within <paramref name="range"/> of <paramref name="at"/> (up to <paramref name="max"/>, nearest first)
        /// with a verdict: usable or the reason not (wards, doesn't drop what is <paramref name="wanted"/>, the tool carried is missing or
        /// too weak, no navmesh path or a path over twice the straight line). <see cref="FindGatherTarget"/> takes the first usable one,
        /// so a planner asking this and a body gathering agree. With <paramref name="log"/>, one "[ChoreBrain] candidate" line each.
        /// The list is reused: copy it to keep it.
        /// </summary>
        public static IReadOnlyList<GatherCandidate> GatherCandidates(Vector3 at, float range, IEnumerable<ItemDrop.ItemData> carried,
            long ownerId, ICollection<string> wanted, int max = 16, bool log = false)
        {
            s_candidates.Clear();
            var items = carried != null ? new List<ItemDrop.ItemData>(carried) : new List<ItemDrop.ItemData>();
            int axeTier = ResourceDataHelper.BestToolTier(items, ResourceDataHelper.ToolType.Axe);
            int pickTier = ResourceDataHelper.BestToolTier(items, ResourceDataHelper.ToolType.Pickaxe);
            bool filterDrops = wanted != null && wanted.Count > 0;

            var found = ResourceDataHelper.FindResourcesInRange(at, range);
            found.Sort((a, b) => (a.InteractionPosition - at).sqrMagnitude.CompareTo((b.InteractionPosition - at).sqrMagnitude));
            int pathChecked = 0;
            foreach (var data in found)
            {
                if (s_candidates.Count >= max) break;
                string reason = null;
                if (!ChestHelper.WardsAllow(data.InteractionPosition, ownerId)) reason = "wards";
                else if (filterDrops && !ResourceDataHelper.YieldsAnyOf(data.GameObject, wanted)) reason = "doesn't drop " + string.Join("/", wanted);
                else reason = ToolReason(data, axeTier, pickTier);

                float straight = Vector3.Distance(at, data.InteractionPosition);
                float pathLength = -1f;
                if (reason == null && pathChecked < PathChecks && Pathfinding.instance != null)
                {
                    pathChecked++;
                    if (!Pathfinding.instance.GetPath(at, data.InteractionPosition, s_path, Pathfinding.AgentType.Humanoid, requireFullPath: true))
                        reason = "no path";
                    else
                    {
                        pathLength = 0f;
                        for (int i = 1; i < s_path.Count; i++) pathLength += Vector3.Distance(s_path[i - 1], s_path[i]);
                        if (pathLength > straight * DetourFactor + DetourSlack)
                            reason = $"path {pathLength:0} m, over {DetourFactor:0}x the straight {straight:0} m";
                    }
                }

                var candidate = new GatherCandidate
                {
                    Data = data,
                    Object = data.GameObject,
                    Prefab = Utils.GetPrefabName(data.GameObject),
                    Position = data.InteractionPosition,
                    Distance = straight,
                    Type = data.Type,
                    Usable = reason == null,
                    Reason = reason ?? "ok",
                    PathLength = pathLength,
                };
                s_candidates.Add(candidate);
                if (log) Debug.Log($"[ChoreBrain] candidate {candidate.Prefab} {straight:0.0} m: {candidate.Reason}");
            }
            return s_candidates;
        }

        /// <summary>
        /// The next step of gathering <paramref name="target"/>: walk into reach, put the right tool in hand, then swing at it (a
        /// player body's real swings; the hits land through vanilla) or use it (a pickable). Done when it is gone.
        /// </summary>
        public static TaskOrder NextGather(ITaskBody body, ResourceDataHelper.ResourceData target)
        {
            if (body == null) return TaskOrder.Failed("no body");
            if (target == null || target.GameObject == null || !target.GameObject.activeInHierarchy) return TaskOrder.Done("gone");

            float reach = Mathf.Max(1.5f, target.InteractionRadius);
            if (Vector3.Distance(body.Position, target.InteractionPosition) > reach)
                return new TaskOrder { Kind = TaskKind.Move, Point = target.InteractionPosition, Target = target.GameObject };

            if (target.IsPickable)
                return new TaskOrder { Kind = TaskKind.Interact, Target = target.GameObject, Point = target.InteractionPosition };

            ItemDrop.ItemData held = body.Character != null ? body.Character.GetCurrentWeapon() : null;
            ItemDrop.ItemData tool = BestTool(body.Inventory != null ? body.Inventory.GetAllItems() : null, target.RequiredTool, target.MinToolTier);
            if (tool == null) return TaskOrder.Failed($"no {target.RequiredTool} of tier {target.MinToolTier}");
            if (held != tool) return new TaskOrder { Kind = TaskKind.Equip, Item = tool, Target = target.GameObject };

            // Aim at the middle of the thing, not its root at the ground.
            Vector3 aim = target.GameObject.transform.position + Vector3.up * 1f;
            return new TaskOrder { Kind = TaskKind.Swing, Point = aim, Target = target.GameObject, Item = tool };
        }

        // ---- Repair + re-equip (Fire, 2026-09-29: "we need to make sure the bot repairs and re-equips its armor when it breaks") ----

        /// <summary>Repair what has worn below this share of its durability (a broken item is at 0).</summary>
        public const float DefaultRepairThreshold = 0.5f;

        private sealed class RepairMemory
        {
            public ItemDrop.ItemData Item;
            public float Before;
            public string Station;
            public ItemDrop.ItemData EquipItem;
        }

        private static readonly Dictionary<Character, RepairMemory> s_repairMemory = new Dictionary<Character, RepairMemory>();
        private static readonly List<ItemDrop.ItemData> s_worn = new List<ItemDrop.ItemData>();

        /// <summary>Whether <paramref name="item"/> uses durability, can be repaired, and is below <paramref name="threshold"/> of its maximum.</summary>
        public static bool NeedsRepair(ItemDrop.ItemData item, float threshold = DefaultRepairThreshold)
        {
            if (item == null || item.m_shared == null || !item.m_shared.m_useDurability || !item.m_shared.m_canBeReparied) return false;
            float max = item.GetMaxDurability();
            return max > 0f && item.m_durability < max * threshold;
        }

        /// <summary>
        /// Vanilla InventoryGui.CanRepair for <paramref name="station"/>: the item's recipe names this station as its repair or crafting
        /// station (or the item is from a lower world level) and the station's level (capped at 4) reaches the recipe's minimum.
        /// </summary>
        public static bool CanRepairAt(ItemDrop.ItemData item, CraftingStation station)
        {
            if (item == null || station == null || !item.m_shared.m_canBeReparied) return false;
            Recipe recipe = ObjectDB.instance != null ? ObjectDB.instance.GetRecipe(item) : null;
            if (recipe == null || (recipe.m_craftingStation == null && recipe.m_repairStation == null)) return false;
            bool named = (recipe.m_repairStation != null && recipe.m_repairStation.m_name == station.m_name)
                         || (recipe.m_craftingStation != null && recipe.m_craftingStation.m_name == station.m_name)
                         || item.m_worldLevel < Game.m_worldLevel;
            return named && Mathf.Min(station.GetLevel(), 4) >= recipe.m_minStationLevel;
        }

        /// <summary>One worn item and where it can be repaired (<see cref="RepairPlan"/>).</summary>
        public struct RepairPlanEntry
        {
            public ItemDrop.ItemData Item;
            /// <summary>Durability left, 0..1.</summary>
            public float Share;
            /// <summary>The nearest usable station that can repair it, or null.</summary>
            public CraftingStation Station;
            public bool CanRepair;
            /// <summary>"ok", or what is missing, e.g. "needs $piece_forge level 2 within 40 m".</summary>
            public string Reason;
        }

        private static readonly List<RepairPlanEntry> s_plan = new List<RepairPlanEntry>();

        /// <summary>What a station must be to repair <paramref name="item"/>: the recipe's repair station, else its crafting station, and level.</summary>
        public static string RepairStationNeeded(ItemDrop.ItemData item)
        {
            Recipe recipe = item != null && ObjectDB.instance != null ? ObjectDB.instance.GetRecipe(item) : null;
            if (recipe == null) return "no recipe";
            CraftingStation needed = recipe.m_repairStation != null ? recipe.m_repairStation : recipe.m_craftingStation;
            if (needed == null) return "no station repairs it";
            return recipe.m_minStationLevel > 1 ? $"{needed.m_name} level {recipe.m_minStationLevel}" : needed.m_name;
        }

        /// <summary>
        /// Every item in <paramref name="inventory"/> worn below <paramref name="threshold"/>, most worn first, with the nearest station
        /// within <paramref name="radius"/> that can repair it or the reason none can. The list is reused: copy it to keep it.
        /// </summary>
        public static IReadOnlyList<RepairPlanEntry> RepairPlan(Vector3 at, Inventory inventory, float radius = 40f, float threshold = DefaultRepairThreshold)
        {
            s_plan.Clear();
            if (inventory == null) return s_plan;
            foreach (var item in inventory.GetAllItems())
            {
                if (!NeedsRepair(item, threshold)) continue;
                CraftingStation station = FindRepairStation(at, radius, item);
                s_plan.Add(new RepairPlanEntry
                {
                    Item = item,
                    Share = item.m_durability / Mathf.Max(1f, item.GetMaxDurability()),
                    Station = station,
                    CanRepair = station != null,
                    Reason = station != null ? "ok" : WhyNoRepairStation(at, radius, item),
                });
            }
            s_plan.Sort((a, b) => a.Share.CompareTo(b.Share));
            return s_plan;
        }

        /// <summary>
        /// Why no station within <paramref name="range"/> can repair <paramref name="item"/>: the nearest station of the right kind and
        /// what is wrong with it ("$piece_workbench at 6 m has no roof", "… is exposed (cover 40 %)", "… needs a fire", "… is level 1,
        /// needs 2"), or that there is none of that kind in range. R61: "needs $piece_workbench within 40 m" with a workbench at the
        /// drill base, and nothing said why it didn't count.
        /// </summary>
        public static string WhyNoRepairStation(Vector3 at, float range, ItemDrop.ItemData item)
        {
            string needed = RepairStationNeeded(item);
            Recipe recipe = item != null && ObjectDB.instance != null ? ObjectDB.instance.GetRecipe(item) : null;
            if (recipe == null) return needed;
            CraftingStation nearest = null;
            float nearestDist = float.MaxValue;
            foreach (var collider in Physics.OverlapSphere(at, range))
            {
                if (collider == null) continue;
                var station = collider.GetComponent<CraftingStation>() ?? collider.GetComponentInParent<CraftingStation>();
                if (station == null || station.m_upgrader) continue;
                bool named = (recipe.m_repairStation != null && recipe.m_repairStation.m_name == station.m_name)
                             || (recipe.m_craftingStation != null && recipe.m_craftingStation.m_name == station.m_name);
                if (!named) continue;
                float dist = Vector3.Distance(at, station.transform.position);
                if (dist < nearestDist) { nearestDist = dist; nearest = station; }
            }
            if (nearest == null) return $"needs {needed} within {range:0} m (none found)";
            string where = $"{nearest.m_name} at {nearestDist:0} m";
            if (Mathf.Min(nearest.GetLevel(), 4) < recipe.m_minStationLevel)
                return $"{where} is level {nearest.GetLevel()}, needs {recipe.m_minStationLevel}";
            if (nearest.m_craftRequireRoof)
            {
                Cover.GetCoverForPoint(nearest.m_roofCheckPoint.position, out float cover, out bool underRoof);
                if (!underRoof) return $"{where} has no roof";
                if (cover < MinStationCover) return $"{where} is exposed (cover {cover * 100f:0} %, needs {MinStationCover * 100f:0} %)";
            }
            if (nearest.m_craftRequireFire && !EffectArea.IsPointPlus025InsideBurningArea(nearest.transform.position))
                return $"{where} needs a fire next to it";
            return $"{where} should do: report this line";
        }

        /// <summary>The nearest usable station within <paramref name="range"/> that can repair <paramref name="item"/>.</summary>
        public static CraftingStation FindRepairStation(Vector3 at, float range, ItemDrop.ItemData item)
        {
            CraftingStation best = null;
            float bestDist = float.MaxValue;
            foreach (var collider in Physics.OverlapSphere(at, range))
            {
                if (collider == null) continue;
                var station = collider.GetComponent<CraftingStation>() ?? collider.GetComponentInParent<CraftingStation>();
                if (station == null || station.m_upgrader) continue;
                float dist = Vector3.Distance(at, station.transform.position);
                if (dist >= bestDist || !CanRepairAt(item, station) || !IsStationUsable(station)) continue;
                bestDist = dist;
                best = station;
            }
            return best;
        }

        /// <summary>
        /// Repairs <paramref name="item"/> at <paramref name="station"/> the way vanilla's repair button does (full durability, the
        /// crafting skill raised for a player, the station's repair effect) and logs it. For a body with no UI (a companion) or as a
        /// fallback; a player body normally repairs through the station.
        /// </summary>
        public static bool RepairAt(Humanoid body, ItemDrop.ItemData item, CraftingStation station)
        {
            if (body == null || item == null || !CanRepairAt(item, station)) return false;
            float before = item.m_durability;
            float max = item.GetMaxDurability();
            if (body is Player player) player.RaiseSkill(Skills.SkillType.Crafting, 1f - before / Mathf.Max(1f, max));
            item.m_durability = max;
            station.m_repairItemDoneEffects.Create(station.transform.position, Quaternion.identity);
            Debug.Log($"[ChoreBrain] repair: {item.m_shared.m_name} {before:0}->{max:0} at {station.m_name}");
            s_repairMemory.Remove(body);   // logged here; NextRepair must not log it again
            s_lastRepaired[body] = Time.time;
            return true;
        }

        /// <summary>
        /// The next step of keeping a body's gear whole: for the most worn item a station within <paramref name="radius"/> can
        /// repair, walk to that station, then Repair there (<see cref="TaskKind.Repair"/>); once nothing needs it, put back on
        /// anything that came off: an empty armour slot gets its best whole piece, an empty hand its weapon (Equip). Done when all
        /// is whole and worn; Failed (reason) when worn gear has no station in reach. Logs "[ChoreBrain] repair: …" and
        /// "[ChoreBrain] re-equipped …" as it sees each step land.
        /// </summary>
        public static TaskOrder NextRepair(ITaskBody body, float radius = 40f, float threshold = DefaultRepairThreshold)
        {
            if (body == null || body.Inventory == null || body.Character == null) return TaskOrder.Failed("no body");
            Humanoid self = body.Character;
            NoteRepairProgress(self);

            s_worn.Clear();
            foreach (var item in body.Inventory.GetAllItems())
                if (NeedsRepair(item, threshold)) s_worn.Add(item);
            s_worn.Sort((a, b) => (a.m_durability / Mathf.Max(1f, a.GetMaxDurability())).CompareTo(b.m_durability / Mathf.Max(1f, b.GetMaxDurability())));

            string missing = null;
            int skipped = 0;
            foreach (var item in s_worn)
            {
                CraftingStation station = FindRepairStation(body.Position, radius, item);
                if (station == null)
                {
                    // Skip it and repair the rest (R61: one forge item failed the whole plan and no workbench item got repaired).
                    string why = WhyNoRepairStation(body.Position, radius, item);
                    NoteSkipped(self, item, why);
                    skipped++;
                    if (missing == null) missing = $"{item.m_shared.m_name}: {why}";
                    continue;
                }
                if (Vector3.Distance(body.Position, station.transform.position) > station.m_useDistance + 0.5f)
                    return new TaskOrder { Kind = TaskKind.Move, Point = station.transform.position, Target = station.gameObject, Item = item };
                s_repairMemory[self] = new RepairMemory { Item = item, Before = item.m_durability, Station = station.m_name };
                return new TaskOrder { Kind = TaskKind.Repair, Item = item, Target = station.gameObject, Point = station.transform.position };
            }

            ItemDrop.ItemData wear = NextToReEquip(self, body.Inventory);
            if (wear != null)
            {
                s_repairMemory[self] = new RepairMemory { EquipItem = wear };
                return new TaskOrder { Kind = TaskKind.Equip, Item = wear };
            }
            s_repairMemory.Remove(self);
            if (missing == null) return TaskOrder.Done("gear whole and worn");
            // Something was repaired in this chore: a partial success, with what is left and why.
            if (s_lastRepaired.TryGetValue(self, out float when) && Time.time - when < PartialWindow)
                return TaskOrder.Done($"repaired what the stations here can; {skipped} skipped ({missing})");
            return TaskOrder.Failed(missing);
        }

        private const float PartialWindow = 120f;
        private const float SkipLogInterval = 60f;
        private static readonly Dictionary<Character, float> s_lastRepaired = new Dictionary<Character, float>();
        private static readonly Dictionary<ItemDrop.ItemData, float> s_skipLogged = new Dictionary<ItemDrop.ItemData, float>();

        // One "[ChoreBrain] repair skipped …" line per item per minute.
        private static void NoteSkipped(Humanoid self, ItemDrop.ItemData item, string why)
        {
            if (s_skipLogged.TryGetValue(item, out float last) && Time.time - last < SkipLogInterval) return;
            if (s_skipLogged.Count > 128) s_skipLogged.Clear();
            s_skipLogged[item] = Time.time;
            Debug.Log($"[ChoreBrain] repair skipped {item.m_shared.m_name}: {why}");
        }

        // Logs the repair or re-equip the body carried out since the last order.
        private static void NoteRepairProgress(Humanoid self)
        {
            if (!s_repairMemory.TryGetValue(self, out RepairMemory memory)) return;
            if (memory.Item != null && memory.Item.m_durability > memory.Before)
            {
                Debug.Log($"[ChoreBrain] repair: {memory.Item.m_shared.m_name} {memory.Before:0}->{memory.Item.m_durability:0} at {memory.Station}");
                s_lastRepaired[self] = Time.time;
            }
            if (memory.EquipItem != null && self.IsItemEquiped(memory.EquipItem))
                Debug.Log($"[ChoreBrain] re-equipped {memory.EquipItem.m_shared.m_name}");
            s_repairMemory.Remove(self);
        }

        private static readonly ItemDrop.ItemData.ItemType[] ArmourTypes =
        {
            ItemDrop.ItemData.ItemType.Helmet, ItemDrop.ItemData.ItemType.Chest, ItemDrop.ItemData.ItemType.Legs, ItemDrop.ItemData.ItemType.Shoulder,
        };
        private static readonly List<ItemDrop.ItemData> s_carried = new List<ItemDrop.ItemData>();

        /// <summary>
        /// What a body should put back on: for an armour slot with nothing worn, the best whole piece of that kind it carries; with
        /// no weapon in hand, the weapon <see cref="CompanionBrain.ChooseWeapons"/> picks (melee first). Null when nothing is missing.
        /// </summary>
        public static ItemDrop.ItemData NextToReEquip(Humanoid self, Inventory inventory)
        {
            if (self == null || inventory == null) return null;
            s_carried.Clear();
            s_carried.AddRange(inventory.GetAllItems());
            foreach (var type in ArmourTypes)
            {
                bool worn = false;
                ItemDrop.ItemData best = null;
                foreach (var item in s_carried)
                {
                    if (item.m_shared.m_itemType != type) continue;
                    if (item.m_equipped) { worn = true; break; }
                    if (item.m_shared.m_useDurability && item.m_durability <= 0f) continue;
                    if (best == null || item.GetArmor() > best.GetArmor()) best = item;
                }
                if (!worn && best != null) return best;
            }
            ItemDrop.ItemData held = self.GetCurrentWeapon();
            ItemDrop.ItemData unarmed = self.m_unarmedWeapon != null ? self.m_unarmedWeapon.m_itemData : null;
            if (held == null || held == unarmed)
            {
                CompanionBrain.ChooseWeapons(s_carried, out ItemDrop.ItemData melee, out ItemDrop.ItemData ranged);
                ItemDrop.ItemData weapon = melee ?? ranged;
                if (weapon != null && !weapon.m_equipped && !(weapon.m_shared.m_useDurability && weapon.m_durability <= 0f)) return weapon;
            }
            return null;
        }

        // ---- Workbench ----

        private const float MinStationCover = 0.7f;

        /// <summary>The highest-level usable (roof, fire) non-upgrader station within <paramref name="range"/>, nearest first on a tie.</summary>
        public static CraftingStation FindWorkstation(Vector3 at, float range)
        {
            CraftingStation best = null;
            int bestLevel = -1;
            float bestDist = float.MaxValue;
            foreach (var collider in Physics.OverlapSphere(at, range))
            {
                if (collider == null) continue;
                var station = collider.GetComponent<CraftingStation>() ?? collider.GetComponentInParent<CraftingStation>();
                if (station == null || station.m_upgrader) continue;
                int level = station.GetLevel();
                float dist = Vector3.Distance(at, station.transform.position);
                if ((level > bestLevel || (level == bestLevel && dist < bestDist)) && IsStationUsable(station))
                {
                    bestLevel = level;
                    bestDist = dist;
                    best = station;
                }
            }
            return best;
        }

        /// <summary>
        /// The first item of <paramref name="itemsInPriority"/> <paramref name="station"/> can take up one quality level with the
        /// materials in <paramref name="materials"/> (the companions' order: right hand, left hand, back, chest, legs, helmet, cape).
        /// </summary>
        public static UpgradeChoice FindBestUpgrade(CraftingStation station, IEnumerable<ItemDrop.ItemData> itemsInPriority, Inventory materials)
        {
            if (station == null || itemsInPriority == null || materials == null) return null;
            foreach (ItemDrop.ItemData item in itemsInPriority)
            {
                if (item == null || item.m_quality >= item.m_shared.m_maxQuality) continue;
                int targetQuality = item.m_quality + 1;
                Recipe recipe = ObjectDB.instance != null ? ObjectDB.instance.GetRecipe(item) : null;
                if (recipe == null || !CanStationUpgrade(station, recipe, targetQuality)) continue;
                var requirements = UpgradeRequirements(recipe, targetQuality);
                if (requirements.Count == 0 || !HasMaterials(requirements, materials)) continue;
                return new UpgradeChoice { Item = item, Recipe = recipe, TargetQuality = targetQuality, Requirements = requirements };
            }
            return null;
        }

        /// <summary>
        /// Why <see cref="FindBestUpgrade"/> found nothing: one reason per item, e.g. "$item_chest_leather q1: needs $item_deerhide x10
        /// (have 6)", "… is at max quality 4", "… needs $piece_workbench level 3", "… has no recipe". Joined with "; ".
        /// </summary>
        public static string ExplainNoUpgrade(CraftingStation station, IEnumerable<ItemDrop.ItemData> itemsInPriority, Inventory materials)
        {
            if (station == null) return "no station";
            if (itemsInPriority == null) return "no items";
            var parts = new List<string>();
            foreach (ItemDrop.ItemData item in itemsInPriority)
            {
                if (item == null) continue;
                string name = $"{item.m_shared.m_name} q{item.m_quality}";
                if (item.m_quality >= item.m_shared.m_maxQuality) { parts.Add($"{name} is at max quality {item.m_shared.m_maxQuality}"); continue; }
                int target = item.m_quality + 1;
                Recipe recipe = ObjectDB.instance != null ? ObjectDB.instance.GetRecipe(item) : null;
                if (recipe == null) { parts.Add($"{name} has no recipe"); continue; }
                if (!CanStationUpgrade(station, recipe, target))
                {
                    CraftingStation needed = recipe.GetRequiredStation(target);
                    parts.Add($"{name} needs {(needed != null ? needed.m_name : "no station")} level {recipe.GetRequiredStationLevel(target)} (this is {station.m_name} level {station.GetLevel()})");
                    continue;
                }
                var requirements = UpgradeRequirements(recipe, target);
                if (requirements.Count == 0) { parts.Add($"{name} has no upgrade cost at q{target}"); continue; }
                foreach (var req in requirements)
                {
                    if (req.m_resItem == null) continue;
                    int have = materials != null ? materials.CountItems(req.m_resItem.m_itemData.m_shared.m_name) : 0;
                    if (have < req.m_amount) { parts.Add($"{name} needs {req.m_resItem.m_itemData.m_shared.m_name} x{req.m_amount} (have {have})"); break; }
                }
                if (parts.Count >= 4) break;
            }
            return parts.Count == 0 ? "nothing to upgrade" : string.Join("; ", parts);
        }

        /// <summary>Takes <paramref name="requirements"/> out of <paramref name="materials"/> (by item name). False, taking nothing, when short.</summary>
        public static bool ConsumeRequirements(List<Piece.Requirement> requirements, Inventory materials)
        {
            if (!HasMaterials(requirements, materials)) return false;
            foreach (var req in requirements)
            {
                if (req.m_resItem == null) continue;
                materials.RemoveItem(req.m_resItem.m_itemData.m_shared.m_name, req.m_amount);
            }
            return true;
        }

        /// <summary>What a normal (non-upgrader) station charges: GetAmount(q) of every non-upgrader resource (Player.ConsumeResources).</summary>
        public static List<Piece.Requirement> UpgradeRequirements(Recipe recipe, int targetQuality)
        {
            var requirements = new List<Piece.Requirement>();
            foreach (var req in recipe.m_resources)
            {
                if (req.m_resItem == null || req.m_upgraderResource) continue;
                int amount = req.GetAmount(targetQuality);
                if (amount > 0) requirements.Add(new Piece.Requirement { m_resItem = req.m_resItem, m_amount = amount });
            }
            return requirements;
        }

        /// <summary>Whether <paramref name="storage"/> holds every requirement.</summary>
        public static bool HasMaterials(List<Piece.Requirement> requirements, Inventory storage)
        {
            if (requirements == null || storage == null) return false;
            foreach (var req in requirements)
            {
                if (req.m_resItem == null) continue;
                if (storage.CountItems(req.m_resItem.m_itemData.m_shared.m_name) < req.m_amount) return false;
            }
            return true;
        }

        /// <summary>Vanilla Player.RequiredCraftingStation(recipe, quality, checkLevel: true) with this (non-upgrader) station as the current one.</summary>
        public static bool CanStationUpgrade(CraftingStation station, Recipe recipe, int targetQuality)
        {
            CraftingStation requiredStation = recipe.GetRequiredStation(targetQuality);
            if (requiredStation == null) return station.m_showBasicRecipies;
            return requiredStation.m_name == station.m_name
                && station.GetLevel() >= recipe.GetRequiredStationLevel(targetQuality);
        }

        /// <summary>Vanilla CraftingStation.CheckUsable: roof cover and fire where the station requires them.</summary>
        public static bool IsStationUsable(CraftingStation station)
        {
            if (station.m_craftRequireRoof)
            {
                Cover.GetCoverForPoint(station.m_roofCheckPoint.position, out float coverPercentage, out bool underRoof);
                if (!underRoof || coverPercentage < MinStationCover) return false;
            }
            return !station.m_craftRequireFire || EffectArea.IsPointPlus025InsideBurningArea(station.transform.position);
        }
    }
}
