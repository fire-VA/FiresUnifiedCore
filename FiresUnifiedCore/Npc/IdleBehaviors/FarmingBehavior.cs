using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Autonomous farm work for a staying companion with a home position: empty hives (honey, feathers), harvest ripe
    /// crops, plant seeds on cultivated soil (fetching a cultivator from inventory or a chest, or crafting one at a
    /// forge), and deposit the harvest once half full. Players can turn it off from the radial menu.
    /// </summary>
    public partial class FarmingBehavior : IdleSubBehavior
    {
        public override string BehaviorName => "Farming";
        public override bool SupportsResumption => true;
        public override bool AvailableForIdleRotation => true;
        public override int InventoryPriority => IsInventoryCompletelyFull() ? -10 : 0;

        // ── Detection ranges ──────────────────────────────────────────────────

        private const float BeehiveDetectionRange   = 30f;
        private const float CropDetectionRange      = 30f;
        private const float PlantingDetectionRange  = 20f;
        private const float InteractionDistance      = 2.5f;
        private const float ArrivalDistance          = 2.5f;
        private const float MovementTimeout          = 30f;
        private const float MaxFarmTime             = 300f;
        private const float PlantSpacing             = 1f;
        private const float CraftingStationRadius   = 30f;
        private const float FacingLeaseDuration      = 0.4f;

        // ── Cultivator ────────────────────────────────────────────────────────

        // Valheim prefab name for the Cultivator item.
        private const string CultivatorPrefab = "Cultivator";

        // Forge recipe: 5 Core Wood (RoundLog) + 5 Bronze.
        // Requires a Forge nearby.
        private const string RecipeCorewood       = "RoundLog";
        private const string RecipeBronze         = "Bronze";
        private const int    RecipeCorewoodCount = 5;
        private const int    RecipeBronzeCount   = 5;

        private float ChestSearchRadius => CompanionSettings.ChestSearchRadius;

        // ── Phase enum ────────────────────────────────────────────────────────

        private enum FarmPhase
        {
            Idle,

            // Cultivator acquisition (only entered when planting is needed)
            GettingCultivator,
            MovingToCultivatorChest,
            MovingToForge,
            CraftingCultivator,

            // Seed retrieval from nearby chests
            GettingSeeds,
            MovingToSeedChest,

            // Harvest beehives
            MovingToBeehive,
            HarvestingBeehive,

            // Harvest mature crops
            MovingToCrop,
            HarvestingCrop,

            // Plant seeds
            MovingToPlantSpot,
            Planting,

            // Pick up world drops after harvest
            CollectingDrops,

            // Deposit to chest when inventory is getting full
            MovingToChest,
            Depositing,

            Complete
        }

        // ── State ─────────────────────────────────────────────────────────────

        private FarmPhase _currentPhase;
        private float     _phaseStartTime;

        // Movement targets
        private Beehive         _targetBeehive;
        private Pickable        _targetCrop;
        private Vector3         _targetPosition;
        private Container       _targetChest;

        // Cultivator acquisition
        private Container       _cultivatorChest;
        private CraftingStation _targetForge;
        private bool            _cultivatorReadyThisSession; // true once we know we have (or got) one

        // Seed retrieval
        private Container       _seedChest;

        // Planting
        private List<Vector3>                    _plantSpots = new List<Vector3>();
        private FarmingDataHelper.CropSapling    _cropToPlant;
        private Vector3                          _plantPosition;
        private readonly Dictionary<string, int> _plantedCountBySapling = new Dictionary<string, int>();

        // Drop collection
        private List<ItemDrop> _pendingPickups   = new List<ItemDrop>();
        private ItemDrop       _currentPickupTarget;

        // Set by the command system (Shift+MMB on a Beehive, Pickable crop, or
        // cultivated soil). Bypasses the Stay-mode + home-position gates and
        // forces this companion to act on that target first. Cleared once
        // consumed in Start().
        private GameObject _commandedTarget;

        // Stats
        private int _hiveProduceHarvested;
        private int _cropsHarvested;
        private int _seedsPlanted;
        private int _itemsDeposited;

        // Component refs
        private Character             _character;
        private Humanoid              _humanoid;
        private CompanionInventory    _inventory;
        private CompanionCombatMovement _combatMovement;
        private CompanionAutoPickup   _autoPickup;
        private ZSyncAnimation        _zanim;

        private List<Container> _nearbyChests = new List<Container>();

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            _character      = companion.GetComponent<Character>();
            _humanoid       = companion.GetComponent<Humanoid>();
            _inventory      = companion.GetComponent<CompanionInventory>();
            _combatMovement = companion.GetComponent<CompanionCombatMovement>();
            _autoPickup     = companion.GetComponent<CompanionAutoPickup>();
            _zanim          = companion.GetComponent<ZSyncAnimation>();
            MaxDuration     = MaxFarmTime + 60f;
        }

        /// <summary>
        /// Set by the command system when the player Shift+MMBs a Beehive,
        /// Pickable crop, or cultivated soil. Forces this companion to handle
        /// that target, bypassing the Stay-mode + home-position gates.
        /// </summary>
        public void SetCommandedTarget(GameObject target)
        {
            _commandedTarget = target;
        }

        public override bool CanStart()
        {
            if (Companion == null) return false;

            // Commanded path: skip the toggle + Stay-mode gates. Validity is
            // checked when Start() resolves the target — a fresh CanStart with
            // a wonky target falls through to the autonomous logic.
            if (_commandedTarget != null && IsValidFarmingTarget(_commandedTarget))
            {
                return true;
            }

            if (!CompanionBehaviorToggles.IsGatherEnabled(Companion)) return false;
            if (IdleBehavior == null || !IdleBehavior.HasHomePosition) return false;
            if (Companion.ShouldBeFollowing) return false;

            Vector3 home = IdleBehavior.HomePosition;

            // Harvesting never requires a cultivator.
            if (FarmingDataHelper.FindNearbyHarvestableBeehives(home, BeehiveDetectionRange).Count > 0)
                return true;
            if (FarmingDataHelper.FindNearbyHarvestableCrops(home, CropDetectionRange).Count > 0)
                return true;

            // Planting: need seeds (inventory or nearby chests) that grow on cultivated ground here + cultivator source.
            RefreshNearbyChests();
            if (!HasAnySeeds()) return false;

            RefreshPlantSpots(home);
            if (_plantSpots.Count == 0) return false;
            if (!StorageHas(IsSeedForPlantSpots) && FindChestWith(IsSeedForPlantSpots) == null) return false;

            return HasCultivator() || FindChestWith(IsCultivator) != null || CanCraftCultivator();
        }

        public override void Start()
        {
            base.Start();

            _currentPhase              = FarmPhase.Idle;
            _phaseStartTime            = Time.time;
            _hiveProduceHarvested      = 0;
            _cropsHarvested            = 0;
            _seedsPlanted              = 0;
            _itemsDeposited            = 0;
            _cultivatorReadyThisSession = HasCultivator();
            _pendingPickups.Clear();

            RefreshNearbyChests();
            _autoPickup?.StartGatheringSession();
            _combatMovement?.SetCommandPriorityDuration(MaxDuration);

            Debug.Log($"[Farming] {Companion.companionName} starting farming session (cultivator:{_cultivatorReadyThisSession})");
            FindNextTask();
        }

        public override void Cancel()
        {
            CompanionChatHelper.ClearWorkingStatus(Companion);
            _autoPickup?.EndGatheringSession();
            _combatMovement?.UnlockMovement();
            _combatMovement?.ClearCommandPriority();

            if (_hiveProduceHarvested > 0 || _cropsHarvested > 0 || _seedsPlanted > 0)
                Debug.Log($"[Farming] {Companion.companionName} done - hive:{_hiveProduceHarvested} crops:{_cropsHarvested} planted:{_seedsPlanted}");

            base.Cancel();
        }

        // ── Update dispatch ───────────────────────────────────────────────────

        public override bool Update()
        {
            if (!IsActive) return true;
            if (IsTimedOut()) { Complete(); return true; }

            bool done = _currentPhase switch
            {
                FarmPhase.Idle                    => UpdateIdle(),
                FarmPhase.GettingCultivator       => UpdateGettingCultivator(),
                FarmPhase.MovingToCultivatorChest => UpdateMovingToCultivatorChest(),
                FarmPhase.MovingToForge           => UpdateMovingToForge(),
                FarmPhase.CraftingCultivator      => UpdateCraftingCultivator(),
                FarmPhase.GettingSeeds            => UpdateGettingSeeds(),
                FarmPhase.MovingToSeedChest       => UpdateMovingToSeedChest(),
                FarmPhase.MovingToBeehive         => UpdateMovingToBeehive(),
                FarmPhase.HarvestingBeehive       => UpdateHarvestingBeehive(),
                FarmPhase.MovingToCrop            => UpdateMovingToCrop(),
                FarmPhase.HarvestingCrop          => UpdateHarvestingCrop(),
                FarmPhase.MovingToPlantSpot       => UpdateMovingToPlantSpot(),
                FarmPhase.Planting                => UpdatePlanting(),
                FarmPhase.CollectingDrops         => UpdateCollectingDrops(),
                FarmPhase.MovingToChest           => UpdateMovingToChest(),
                FarmPhase.Depositing              => UpdateDepositing(),
                FarmPhase.Complete                => true,
                _                                 => false
            };

            if (done) { Complete(); return true; }
            return false;
        }

        // ── Status ────────────────────────────────────────────────────────────

        public override string GetStatusDescription() => _currentPhase switch
        {
            FarmPhase.GettingCultivator       => "Looking for cultivator",
            FarmPhase.MovingToCultivatorChest => "Getting cultivator from chest",
            FarmPhase.MovingToForge           => "Walking to forge",
            FarmPhase.CraftingCultivator      => "Crafting cultivator",
            FarmPhase.GettingSeeds            => "Looking for seeds",
            FarmPhase.MovingToSeedChest       => "Getting seeds from chest",
            FarmPhase.MovingToBeehive   when _targetBeehive != null => $"Walking to {FarmingDataHelper.GetHiveName(_targetBeehive)}",
            FarmPhase.HarvestingBeehive when _targetBeehive != null => $"Collecting {FarmingDataHelper.GetProduceName(_targetBeehive)}",
            FarmPhase.MovingToCrop            => "Walking to crops",
            FarmPhase.HarvestingCrop          => "Harvesting crops",
            FarmPhase.MovingToPlantSpot       => "Walking to plant",
            FarmPhase.Planting                => "Planting seeds",
            FarmPhase.CollectingDrops         => "Collecting items",
            FarmPhase.MovingToChest           => "Walking to storage",
            FarmPhase.Depositing              => "Storing harvest",
            _                                 => "Farming"
        };

        // ── Task finding ──────────────────────────────────────────────────────

        private bool UpdateIdle()
        {
            if (FindNextTask()) return false;

            if (ShouldDeposit()) { StartDeposit(); return false; }

            return true; // nothing left — signal complete
        }

        /// <summary>
        /// Picks the highest-priority task and transitions to the appropriate phase.
        /// Returns true if a task was found.
        /// </summary>
        /// <summary>
        /// Returns true if the given object can be acted on by farming: a
        /// harvestable Beehive, a Pickable crop, or cultivated soil.
        /// Used by CanStart's commanded-path validity check.
        /// </summary>
        private bool IsValidFarmingTarget(GameObject obj)
        {
            if (obj == null) return false;
            if (obj.GetComponent<Beehive>() != null || obj.GetComponentInParent<Beehive>() != null) return true;
            if (obj.GetComponent<Pickable>() != null || obj.GetComponentInParent<Pickable>() != null) return true;
            // Cultivated soil — recognised by name on the underlying piece/terrain.
            string objectName = obj.name?.ToLowerInvariant() ?? "";
            if (objectName.Contains("cultivated") || objectName.Contains("plant") || objectName.Contains("seed")) return true;
            return false;
        }

        /// <summary>
        /// If a commanded target is set, dispatch directly to the appropriate
        /// phase (harvest beehive, harvest crop, or move to plant spot) so it
        /// runs before the autonomous task loop scans for nearby work.
        /// Returns true if a commanded task was queued.
        /// </summary>
        private bool TryStartCommandedTask()
        {
            if (_commandedTarget == null) return false;

            try
            {
                var hive = _commandedTarget.GetComponent<Beehive>()
                        ?? _commandedTarget.GetComponentInParent<Beehive>();
                if (hive != null && ChestHelper.WardsAllow(hive.transform.position, Companion))
                {
                    _targetBeehive  = hive;
                    _targetPosition = InteractionPointHelper.GetInteractionPoint(hive.gameObject, Transform.position, InteractionDistance);
                    SetPhase(FarmPhase.MovingToBeehive);
                    TryMoveToPosition(_targetPosition);
                    return true;
                }

                var crop = _commandedTarget.GetComponent<Pickable>()
                        ?? _commandedTarget.GetComponentInParent<Pickable>();
                if (crop != null && ChestHelper.WardsAllow(crop.transform.position, Companion))
                {
                    _targetCrop     = crop;
                    _targetPosition = InteractionPointHelper.GetInteractionPoint(crop.gameObject, Transform.position, InteractionDistance);
                    SetPhase(FarmPhase.MovingToCrop);
                    TryMoveToPosition(_targetPosition);
                    return true;
                }

                // Cultivated soil → fall through to the planting branch of the
                // normal task loop, which will pick a plant spot near the target.
                return false;
            }
            finally
            {
                _commandedTarget = null;
            }
        }

        private bool FindNextTask()
        {
            // Honor an explicit player command before the autonomous priority list.
            if (TryStartCommandedTask()) return true;

            Vector3 home = IdleBehavior?.HomePosition ?? Transform.position;

            // Priority 1: beehives (no tool required)
            var beehives = FarmingDataHelper.FindNearbyHarvestableBeehives(home, BeehiveDetectionRange);
            foreach (var hive in beehives)
            {
                if (!ChestHelper.WardsAllow(hive.transform.position, Companion)) continue;
                if (!IsReachable(hive.transform.position)) continue;

                _targetBeehive  = hive;
                _targetPosition = InteractionPointHelper.GetInteractionPoint(hive.gameObject, Transform.position, InteractionDistance);
                SetPhase(FarmPhase.MovingToBeehive);
                TryMoveToPosition(_targetPosition);
                return true;
            }

            // Priority 2: harvestable crops (no tool required)
            var crops = FarmingDataHelper.FindNearbyHarvestableCrops(home, CropDetectionRange);
            foreach (var crop in crops)
            {
                if (!ChestHelper.WardsAllow(crop.transform.position, Companion)) continue;
                if (!IsReachable(crop.transform.position)) continue;

                _targetCrop     = crop;
                _targetPosition = InteractionPointHelper.GetInteractionPoint(crop.gameObject, Transform.position, InteractionDistance);
                SetPhase(FarmPhase.MovingToCrop);
                TryMoveToPosition(_targetPosition);
                return true;
            }

            // Priority 3: planting (requires seeds that grow here + cultivator)
            if (!HasAnySeeds()) return false;

            RefreshPlantSpots(home);
            if (_plantSpots.Count == 0) return false;

            // Retrieve seeds from a chest if we don't carry any that grow on these spots.
            if (!StorageHas(IsSeedForPlantSpots))
            {
                if (FindChestWith(IsSeedForPlantSpots) == null) return false;
                SetPhase(FarmPhase.GettingSeeds);
                return true;
            }

            // Ensure we have a cultivator — trigger acquisition if not.
            if (!_cultivatorReadyThisSession && !HasCultivator())
            {
                SetPhase(FarmPhase.GettingCultivator);
                return true;
            }

            return TryFindPlantingTask();
        }

        private bool TryFindPlantingTask()
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;

            foreach (var item in storage.GetAllItems())
            {
                foreach (var crop in LeastPlantedFirst(FarmingDataHelper.SaplingsForSeed(item?.m_dropPrefab?.name)))
                {
                    if (!HasSeedsFor(storage, crop)) continue;

                    foreach (var pos in _plantSpots)
                    {
                        if (!FarmingDataHelper.IsValidPlantingPosition(pos, crop, PlantSpacing)) continue;

                        _cropToPlant    = crop;
                        _plantPosition  = pos;
                        _targetPosition = pos;
                        SetPhase(FarmPhase.MovingToPlantSpot);
                        TryMoveToPosition(_targetPosition);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>A seed that grows into more than one sapling (1.0 KaleSeeds: kale or seed kale) alternates between them.</summary>
        private IEnumerable<FarmingDataHelper.CropSapling> LeastPlantedFirst(IReadOnlyList<FarmingDataHelper.CropSapling> crops)
            => crops.OrderBy(crop => _plantedCountBySapling.TryGetValue(crop.Prefab.name, out int planted) ? planted : 0);

        private bool HasSeedsFor(Inventory storage, FarmingDataHelper.CropSapling crop)
        {
            foreach (var seed in crop.Seeds)
                if (CountItems(storage, seed.Item) < seed.Amount) return false;
            return true;
        }

        private bool IsSeedForPlantSpots(ItemDrop.ItemData item)
        {
            foreach (var crop in FarmingDataHelper.SaplingsForSeed(item?.m_dropPrefab?.name))
                foreach (var spot in _plantSpots)
                    if (FarmingDataHelper.CanGrowAt(crop, spot)) return true;
            return false;
        }

        private bool HasAnySeeds()
            => StorageHas(FarmingDataHelper.CanBePlanted) || FindChestWith(FarmingDataHelper.CanBePlanted) != null;

        private void RefreshPlantSpots(Vector3 home)
            => _plantSpots = FarmingDataHelper.FindPlantablePositions(home, PlantingDetectionRange, PlantSpacing);

        // ── Cultivator helpers ────────────────────────────────────────────────

        private static bool IsCultivator(ItemDrop.ItemData item)
            => item?.m_dropPrefab?.name?.IndexOf("cultivator", System.StringComparison.OrdinalIgnoreCase) >= 0;

        private bool HasCultivator() => StorageHas(IsCultivator);

        private bool CanCraftCultivator()
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;
            return CountItems(storage, RecipeCorewood) >= RecipeCorewoodCount
                && CountItems(storage, RecipeBronze)   >= RecipeBronzeCount;
        }

        private CraftingStation FindNearestForge()
        {
            var cols = Physics.OverlapSphere(Transform.position, CraftingStationRadius);
            CraftingStation best    = null;
            float           bestDist = float.MaxValue;
            var             seen    = new HashSet<CraftingStation>();

            foreach (var collider in cols)
            {
                if (collider == null) continue;
                var station = collider.GetComponent<CraftingStation>() ?? collider.GetComponentInParent<CraftingStation>();
                if (station == null || seen.Contains(station)) continue;
                seen.Add(station);

                string stName = (station.m_name ?? station.gameObject.name ?? "").ToLowerInvariant();
                if (!stName.Contains("forge")) continue;

                float stationDistance = Vector3.Distance(Transform.position, station.transform.position);
                if (stationDistance < bestDist) { bestDist = stationDistance; best = station; }
            }
            return best;
        }

        // ── General helpers ───────────────────────────────────────────────────

        private void SetPhase(FarmPhase phase)
        {
            _currentPhase   = phase;
            _phaseStartTime = Time.time;
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[Farming] {Companion?.companionName} → {phase}");
        }

        private bool MovementTimedOut() => Time.time - _phaseStartTime > MovementTimeout;

        private void RefreshNearbyChests()
        {
            Vector3 pos = IdleBehavior?.HomePosition ?? Transform.position;
            _nearbyChests = ChestHelper.FindNearbyChests(pos, ChestSearchRadius);
        }

        private bool StorageHas(System.Predicate<ItemDrop.ItemData> match)
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;
            foreach (var item in storage.GetAllItems())
                if (match(item)) return true;
            return false;
        }

        private Container FindChestWith(System.Predicate<ItemDrop.ItemData> match)
        {
            foreach (var chest in _nearbyChests)
            {
                if (chest == null) continue;
                foreach (var item in chest.GetInventory()?.GetAllItems() ?? new List<ItemDrop.ItemData>())
                    if (match(item)) return chest;
            }
            return null;
        }

        private bool ShouldDeposit()
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;
            float used  = storage.GetAllItems().Count;
            float total = storage.GetWidth() * storage.GetHeight();
            return used / total > 0.5f;
        }

        private void StartDeposit()
        {
            if (_nearbyChests.Count == 0) RefreshNearbyChests();

            foreach (var chest in _nearbyChests)
            {
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv == null || inv.GetEmptySlots() == 0) continue;
                if (!IsReachable(chest.transform.position)) continue;

                _targetChest    = chest;
                _targetPosition = InteractionPointHelper.GetContainerInteractionPoint(chest, Transform.position, InteractionDistance);
                SetPhase(FarmPhase.MovingToChest);
                TryMoveToPosition(_targetPosition);
                return;
            }

            // No reachable chest with space — end session.
            SetPhase(FarmPhase.Complete);
        }

        private int CountItems(Inventory inv, string prefabName)
        {
            int count = 0;
            foreach (var item in inv.GetAllItems())
                if (item?.m_dropPrefab?.name == prefabName) count += item.m_stack;
            return count;
        }

        private void RemoveItems(Inventory inv, string prefabName, int amount)
        {
            int remaining = amount;
            foreach (var item in new List<ItemDrop.ItemData>(inv.GetAllItems()))
            {
                if (remaining <= 0) break;
                if (item?.m_dropPrefab?.name != prefabName) continue;
                int take = Mathf.Min(item.m_stack, remaining);
                item.m_stack -= take;
                remaining    -= take;
                if (item.m_stack <= 0) inv.RemoveItem(item);
            }
        }

        private void FaceTarget(Vector3 target)
        {
            Vector3 dir = target - Transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;

            // Single facing-writer: face the crop through the FacingAuthority (SubBehavior); combat
            // preempts if a fight interrupts. Direct write fallback only.
            var facing = Companion != null ? Companion.GetFacingAuthority() : null;
            if (facing != null)
            {
                if (facing.TryAcquireFacing(FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.SubBehavior, BehaviorName, FacingLeaseDuration))
                    facing.SetLookDirection(BehaviorName, dir);
                return;
            }

            dir.Normalize();
            Transform.rotation = Quaternion.LookRotation(dir);
        }

        private void PlayInteractAnimation() => _zanim?.SetTrigger("interact");
    }
}
