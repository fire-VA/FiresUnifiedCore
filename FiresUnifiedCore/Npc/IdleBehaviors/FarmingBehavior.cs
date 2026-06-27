using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Farming behavior â€” autonomous farm work loop:
    ///   1. Harvest beehives with honey (no tool needed, highest value)
    ///   2. Harvest mature crops (no tool needed)
    ///   3. Plant seeds on cultivated ground:
    ///      a. Look for Cultivator in inventory
    ///      b. If missing, search nearby chests
    ///      c. If not in chests, craft one at a Forge (5 RoundLog + 5 Bronze)
    ///      d. Plant seeds on any open cultivated soil
    ///   4. Deposit harvest items to nearby chests when over half-full
    ///
    /// Requires Stay mode with a home position.
    /// Player can disable gathering/farming from the radial menu.
    /// </summary>
    public partial class FarmingBehavior : IdleSubBehavior
    {
        public override string BehaviorName => "Farming";
        public override bool SupportsResumption => true;
        public override bool AvailableForIdleRotation => true;
        public override int InventoryPriority => IsInventoryCompletelyFull() ? -10 : 0;

        // â”€â”€ Detection ranges â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private const float BEEHIVE_DETECTION_RANGE   = 30f;
        private const float CROP_DETECTION_RANGE      = 30f;
        private const float PLANTING_DETECTION_RANGE  = 20f;
        private const float INTERACTION_DISTANCE      = 2.5f;
        private const float ARRIVAL_DISTANCE          = 2.5f;
        private const float MOVEMENT_TIMEOUT          = 30f;
        private const float MAX_FARM_TIME             = 300f;
        private const float PLANT_SPACING             = 1f;
        private const float CRAFTING_STATION_RADIUS   = 30f;

        // â”€â”€ Cultivator â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Valheim prefab name for the Cultivator item.
        private const string CULTIVATOR_PREFAB = "Cultivator";

        // Forge recipe: 5 Core Wood (RoundLog) + 5 Bronze.
        // Requires a Forge nearby.
        private const string RECIPE_COREWOOD       = "RoundLog";
        private const string RECIPE_BRONZE         = "Bronze";
        private const int    RECIPE_COREWOOD_COUNT = 5;
        private const int    RECIPE_BRONZE_COUNT   = 5;

        private float ChestSearchRadius => CompanionSettings.ChestSearchRadius;

        // â”€â”€ Phase enum â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

        // â”€â”€ State â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
        private string  _seedToPlant;
        private Vector3 _plantPosition;

        // Drop collection
        private List<ItemDrop> _pendingPickups   = new List<ItemDrop>();
        private ItemDrop       _currentPickupTarget;

        // Set by the command system (Shift+MMB on a Beehive, Pickable crop, or
        // cultivated soil). Bypasses the Stay-mode + home-position gates and
        // forces this companion to act on that target first. Cleared once
        // consumed in Start().
        private GameObject _commandedTarget;

        // Stats
        private int _honeyHarvested;
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

        // â”€â”€ Lifecycle â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            _character      = companion.GetComponent<Character>();
            _humanoid       = companion.GetComponent<Humanoid>();
            _inventory      = companion.GetComponent<CompanionInventory>();
            _combatMovement = companion.GetComponent<CompanionCombatMovement>();
            _autoPickup     = companion.GetComponent<CompanionAutoPickup>();
            _zanim          = companion.GetComponent<ZSyncAnimation>();
            MaxDuration     = MAX_FARM_TIME + 60f;
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
            // checked when Start() resolves the target â€” a fresh CanStart with
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
            if (FarmingDataHelper.FindNearbyHarvestableBeehives(home, BEEHIVE_DETECTION_RANGE).Count > 0)
                return true;
            if (FarmingDataHelper.FindNearbyHarvestableCrops(home, CROP_DETECTION_RANGE).Count > 0)
                return true;

            // Planting: need seeds (inventory or nearby chests) + cultivated ground + cultivator source.
            RefreshNearbyChests();
            if (!HasSeedsToPlant() && !ChestHasSeeds()) return false;

            var plantSpots = FarmingDataHelper.FindPlantablePositions(home, PLANTING_DETECTION_RANGE, PLANT_SPACING);
            if (plantSpots.Count == 0) return false;

            return HasCultivator() || ChestHasCultivator() || CanCraftCultivator();
        }

        public override void Start()
        {
            base.Start();

            _currentPhase              = FarmPhase.Idle;
            _phaseStartTime            = Time.time;
            _honeyHarvested            = 0;
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

            if (_honeyHarvested > 0 || _cropsHarvested > 0 || _seedsPlanted > 0)
                Debug.Log($"[Farming] {Companion.companionName} done â€” honey:{_honeyHarvested} crops:{_cropsHarvested} planted:{_seedsPlanted}");

            base.Cancel();
        }

        // â”€â”€ Update dispatch â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

        // â”€â”€ Status â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public override string GetStatusDescription() => _currentPhase switch
        {
            FarmPhase.GettingCultivator       => "Looking for cultivator",
            FarmPhase.MovingToCultivatorChest => "Getting cultivator from chest",
            FarmPhase.MovingToForge           => "Walking to forge",
            FarmPhase.CraftingCultivator      => "Crafting cultivator",
            FarmPhase.GettingSeeds            => "Looking for seeds",
            FarmPhase.MovingToSeedChest       => "Getting seeds from chest",
            FarmPhase.MovingToBeehive         => "Walking to beehive",
            FarmPhase.HarvestingBeehive       => "Harvesting honey",
            FarmPhase.MovingToCrop            => "Walking to crops",
            FarmPhase.HarvestingCrop          => "Harvesting crops",
            FarmPhase.MovingToPlantSpot       => "Walking to plant",
            FarmPhase.Planting                => "Planting seeds",
            FarmPhase.CollectingDrops         => "Collecting items",
            FarmPhase.MovingToChest           => "Walking to storage",
            FarmPhase.Depositing              => "Storing harvest",
            _                                 => "Farming"
        };

        // â”€â”€ Task finding â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private bool UpdateIdle()
        {
            if (FindNextTask()) return false;

            if (ShouldDeposit()) { StartDeposit(); return false; }

            return true; // nothing left â€” signal complete
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
            // Cultivated soil â€” recognised by name on the underlying piece/terrain.
            string n = obj.name?.ToLowerInvariant() ?? "";
            if (n.Contains("cultivated") || n.Contains("plant") || n.Contains("seed")) return true;
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
                if (hive != null && FarmingDataHelper.HasPrivateAreaAccess(hive.transform.position))
                {
                    _targetBeehive  = hive;
                    _targetPosition = InteractionPointHelper.GetInteractionPoint(hive.gameObject, Transform.position, INTERACTION_DISTANCE);
                    SetPhase(FarmPhase.MovingToBeehive);
                    TryMoveToPosition(_targetPosition);
                    return true;
                }

                var crop = _commandedTarget.GetComponent<Pickable>()
                        ?? _commandedTarget.GetComponentInParent<Pickable>();
                if (crop != null && FarmingDataHelper.HasPrivateAreaAccess(crop.transform.position))
                {
                    _targetCrop     = crop;
                    _targetPosition = InteractionPointHelper.GetInteractionPoint(crop.gameObject, Transform.position, INTERACTION_DISTANCE);
                    SetPhase(FarmPhase.MovingToCrop);
                    TryMoveToPosition(_targetPosition);
                    return true;
                }

                // Cultivated soil â†’ fall through to the planting branch of the
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
            var beehives = FarmingDataHelper.FindNearbyHarvestableBeehives(home, BEEHIVE_DETECTION_RANGE);
            foreach (var hive in beehives)
            {
                if (!FarmingDataHelper.HasPrivateAreaAccess(hive.transform.position)) continue;
                if (!IsReachable(hive.transform.position)) continue;

                _targetBeehive  = hive;
                _targetPosition = InteractionPointHelper.GetInteractionPoint(hive.gameObject, Transform.position, INTERACTION_DISTANCE);
                SetPhase(FarmPhase.MovingToBeehive);
                TryMoveToPosition(_targetPosition);
                return true;
            }

            // Priority 2: harvestable crops (no tool required)
            var crops = FarmingDataHelper.FindNearbyHarvestableCrops(home, CROP_DETECTION_RANGE);
            foreach (var crop in crops)
            {
                if (!FarmingDataHelper.HasPrivateAreaAccess(crop.transform.position)) continue;
                if (!IsReachable(crop.transform.position)) continue;

                _targetCrop     = crop;
                _targetPosition = InteractionPointHelper.GetInteractionPoint(crop.gameObject, Transform.position, INTERACTION_DISTANCE);
                SetPhase(FarmPhase.MovingToCrop);
                TryMoveToPosition(_targetPosition);
                return true;
            }

            // Priority 3: planting (requires seeds + cultivator)
            bool hasSeedsInInventory = HasSeedsToPlant();
            bool hasSeedsInChests    = !hasSeedsInInventory && ChestHasSeeds();
            if (!hasSeedsInInventory && !hasSeedsInChests) return false;

            var plantSpots = FarmingDataHelper.FindPlantablePositions(home, PLANTING_DETECTION_RANGE, PLANT_SPACING);
            if (plantSpots.Count == 0) return false;

            // Retrieve seeds from a chest if we don't have any in inventory.
            if (!hasSeedsInInventory)
            {
                SetPhase(FarmPhase.GettingSeeds);
                return true;
            }

            // Ensure we have a cultivator â€” trigger acquisition if not.
            if (!_cultivatorReadyThisSession && !HasCultivator())
            {
                SetPhase(FarmPhase.GettingCultivator);
                return true;
            }

            return TryFindPlantingTask(plantSpots);
        }

        private bool TryFindPlantingTask(List<Vector3> plantSpots)
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;

            foreach (var item in storage.GetAllItems())
            {
                if (!FarmingDataHelper.CanBePlanted(item)) continue;
                string seedPrefab = item.m_dropPrefab?.name;
                if (string.IsNullOrEmpty(seedPrefab)) continue;

                foreach (var pos in plantSpots)
                {
                    if (!FarmingDataHelper.IsValidPlantingPosition(pos, seedPrefab, PLANT_SPACING)) continue;

                    _seedToPlant    = seedPrefab;
                    _plantPosition  = pos;
                    _targetPosition = pos;
                    SetPhase(FarmPhase.MovingToPlantSpot);
                    TryMoveToPosition(_targetPosition);
                    return true;
                }
            }
            return false;
        }

        // â”€â”€ Cultivator helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private bool HasCultivator()
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;
            foreach (var item in storage.GetAllItems())
            {
                if (item?.m_dropPrefab?.name?.IndexOf("cultivator", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private bool ChestHasCultivator()
        {
            foreach (var chest in _nearbyChests)
            {
                if (chest == null) continue;
                foreach (var item in chest.GetInventory()?.GetAllItems() ?? new List<ItemDrop.ItemData>())
                {
                    if (item?.m_dropPrefab?.name?.IndexOf("cultivator", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            return false;
        }

        private Container FindChestWithCultivator()
        {
            foreach (var chest in _nearbyChests)
            {
                if (chest == null) continue;
                foreach (var item in chest.GetInventory()?.GetAllItems() ?? new List<ItemDrop.ItemData>())
                {
                    if (item?.m_dropPrefab?.name?.IndexOf("cultivator", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return chest;
                }
            }
            return null;
        }

        private bool ChestHasSeeds()
        {
            foreach (var chest in _nearbyChests)
            {
                if (chest == null) continue;
                foreach (var item in chest.GetInventory()?.GetAllItems() ?? new List<ItemDrop.ItemData>())
                    if (FarmingDataHelper.CanBePlanted(item)) return true;
            }
            return false;
        }

        private Container FindChestWithSeeds()
        {
            foreach (var chest in _nearbyChests)
            {
                if (chest == null) continue;
                foreach (var item in chest.GetInventory()?.GetAllItems() ?? new List<ItemDrop.ItemData>())
                    if (FarmingDataHelper.CanBePlanted(item)) return chest;
            }
            return null;
        }

        private bool CanCraftCultivator()
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;
            return CountItems(storage, RECIPE_COREWOOD) >= RECIPE_COREWOOD_COUNT
                && CountItems(storage, RECIPE_BRONZE)   >= RECIPE_BRONZE_COUNT;
        }

        private CraftingStation FindNearestForge()
        {
            var cols = Physics.OverlapSphere(Transform.position, CRAFTING_STATION_RADIUS);
            CraftingStation best    = null;
            float           bestDist = float.MaxValue;
            var             seen    = new HashSet<CraftingStation>();

            foreach (var col in cols)
            {
                if (col == null) continue;
                var st = col.GetComponent<CraftingStation>() ?? col.GetComponentInParent<CraftingStation>();
                if (st == null || seen.Contains(st)) continue;
                seen.Add(st);

                string stName = (st.m_name ?? st.gameObject.name ?? "").ToLowerInvariant();
                if (!stName.Contains("forge")) continue;

                float d = Vector3.Distance(Transform.position, st.transform.position);
                if (d < bestDist) { bestDist = d; best = st; }
            }
            return best;
        }

        // â”€â”€ General helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void SetPhase(FarmPhase phase)
        {
            _currentPhase   = phase;
            _phaseStartTime = Time.time;
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[Farming] {Companion?.companionName} â†’ {phase}");
        }

        private bool MovementTimedOut() => Time.time - _phaseStartTime > MOVEMENT_TIMEOUT;

        private void RefreshNearbyChests()
        {
            Vector3 pos = IdleBehavior?.HomePosition ?? Transform.position;
            _nearbyChests = ChestHelper.FindNearbyChests(pos, ChestSearchRadius);
        }

        private bool HasSeedsToPlant()
        {
            var storage = _inventory?.GetStorageInventory();
            if (storage == null) return false;
            foreach (var item in storage.GetAllItems())
                if (FarmingDataHelper.CanBePlanted(item)) return true;
            return false;
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
                _targetPosition = InteractionPointHelper.GetContainerInteractionPoint(chest, Transform.position, INTERACTION_DISTANCE);
                SetPhase(FarmPhase.MovingToChest);
                TryMoveToPosition(_targetPosition);
                return;
            }

            // No reachable chest with space â€” end session.
            SetPhase(FarmPhase.Complete);
        }

        private int CountItems(Inventory inv, string prefabName)
        {
            int n = 0;
            foreach (var item in inv.GetAllItems())
                if (item?.m_dropPrefab?.name == prefabName) n += item.m_stack;
            return n;
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
                if (facing.TryAcquireFacing(FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.SubBehavior, BehaviorName, 0.4f))
                    facing.SetLookDirection(BehaviorName, dir);
                return;
            }

            dir.Normalize();
            Transform.rotation = Quaternion.LookRotation(dir);
        }

        private void PlayInteractAnimation() => _zanim?.SetTrigger("interact");
    }
}
