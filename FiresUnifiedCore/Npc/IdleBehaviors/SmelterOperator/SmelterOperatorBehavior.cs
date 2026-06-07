using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Core;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Handles companion operation of smelters, kilns, and other processing stations.
    /// Companion will fill stations with ore/materials and collect outputs.
    /// 
    /// This is a partial class split across multiple files for maintainability:
    /// - SmelterOperatorBehavior.cs: Core class, fields, initialization, CanStart
    /// - SmelterOperatorBehavior.Lifecycle.cs: Start, Update, Cancel, Complete
    /// - SmelterOperatorBehavior.StationPhases.cs: Station interaction phases
    /// - SmelterOperatorBehavior.OutputPhases.cs: Output collection phases
    /// - SmelterOperatorBehavior.KilnWorkflow.cs: Kiln coordination phases
    /// - SmelterOperatorBehavior.Helpers.cs: Helper methods and utilities
    /// 
    /// FEATURES:
    /// - Fills smelters with ore and coal from inventory or nearby chests
    /// - Fills kilns with wood
    /// - Collects processed outputs (bars, coal) - PICKS UP FROM GROUND
    /// - Transfers outputs to nearby chests
    /// - Can be commanded to a specific station via ping
    /// - KILN+SMELTER WORKFLOW: Automatically coordinates kiln to produce coal for smelters
    /// - CONTINUOUS OPERATION: Keeps working until out of materials or interrupted
    /// 
    /// SUPPORTED STATIONS:
    /// - Smelter (smelter)
    /// - Blast Furnace (blastfurnace)
    /// - Charcoal Kiln (charcoal_kiln)
    /// - Spinning Wheel (spinning_wheel)
    /// - Windmill (windmill)
    /// </summary>
    public partial class SmelterOperatorBehavior : IdleSubBehavior
    {
        public override string BehaviorName => "SmelterOperator";
        
        /// <summary>
        /// Smelter operation supports being interrupted by combat and resumed afterwards.
        /// </summary>
        public override bool SupportsResumption => true;
        
        /// <summary>
        /// Smelter operation is available for idle rotation when companion is in Stay mode.
        /// </summary>
        public override bool AvailableForIdleRotation => true;
        
        /// <summary>
        /// Smelter operation can proceed even with a full inventory since it
        /// deposits items to chests. However, we give lower priority if full
        /// so pure deposit behaviors can run first.
        /// </summary>
        public override int InventoryPriority
        {
            get
            {
                // Smelter can handle full inventory (deposits to chests)
                // but we slightly deprioritize if inventory is full
                if (IsInventoryCompletelyFull())
                {
                    return -10; // Slightly lower priority when full
                }
                return 0; // Normal priority
            }
        }
        
        #region Settings
        
        private const float STATION_DETECTION_RANGE = 15f;
        // Use centralized settings from CompanionSettings
        private float CHEST_SEARCH_RADIUS => CompanionSettings.ChestSearchRadius;
        
        // INTERACTION DISTANCES - Use InteractionPointHelper constants for consistency
        // These ensure companions walk to the correct side of objects before interacting
        private static float INTERACTION_DISTANCE => InteractionPointHelper.MAX_INTERACTION_RANGE;  // How close to be to interact
        private static float ARRIVAL_DISTANCE => InteractionPointHelper.ARRIVAL_THRESHOLD;          // How close to consider "arrived"
        
        private const float OPERATION_CHECK_INTERVAL = 2f; // Faster checks
        private const float MAX_OPERATE_TIME = 600f;    // 10 minutes max - continuous operation
        private const float PICKUP_SCAN_RADIUS = 10f;   // Radius to scan for ground items (large to catch all output)
        private const float PICKUP_DISTANCE = 1.5f;     // How close to get to item before picking up
        private const float OUTPUT_WAIT_TIME = 30f;     // Max time to wait for processing
        
        // THRESHOLD SETTINGS - Companions will only refill when station is below these levels
        // This prevents constant hovering and gives time for processing
        private const float ORE_REFILL_THRESHOLD = 0.5f;   // Only add ore when below 50% capacity
        private const float FUEL_REFILL_THRESHOLD = 0.3f;  // Only add fuel when below 30% capacity
        
        #endregion
        
        #region State
        
        private enum OperatePhase
        {
            FindingStation,
            MovingToStation,
            PullingFromChests,
            // Physical chest interaction phases
            MovingToChestForPull,
            InteractingWithChestForPull,
            MovingToChestForDeposit,
            InteractingWithChestForDeposit,
            // Station operation phases
            FillingStation,
            MovingToFuelSwitch,    // Walk to fuel input point
            AddingFuel,            // Add single fuel item
            MovingToOreSwitch,     // Walk to ore input point
            AddingOre,             // Add single ore item
            WaitingForOutput,
            MovingToPickupArea,     // Walk to where items spawn
            PickingUpOutput,        // Actually pickup ground items
            CollectingOutput,
            DepositingToChests,
            // Kiln coordination phases
            FindingKiln,
            MovingToKiln,
            FillingKiln,
            WaitingForKilnOutput,
            CollectingKilnOutput,
            ReturningToSmelter,
            Complete
        }
        
        private OperatePhase _currentPhase = OperatePhase.FindingStation;
        private Smelter _targetSmelter;
        private Smelter _nearbyKiln;         // For kiln coordination
        private Vector3 _targetPosition;
        private Vector3 _pickupAreaPosition; // Where output items spawn
        private float _phaseStartTime;
        private float _lastOperationCheck;
        private int _itemsAdded;
        private int _itemsCollected;
        private int _itemsDeposited;
        private int _operationCycles;        // Track how many fill/collect cycles
        
        // Nearby containers
        private List<Container> _nearbyChests = new List<Container>();
        
        // Components
        private Character _character;
        private Humanoid _humanoid;
        private CompanionInventory _inventory;
        private CompanionCombatMovement _combatMovement;
        private CompanionAutoPickup _autoPickup;
        private Rigidbody _rigidbody;
        private ZSyncAnimation _zanim;
        
        // Resource access service for unified chest/inventory operations
        private ResourceAccessService _resources;
        
        // Chest interaction service for physical pathfinding to chests
        private ChestInteractionService _chestInteraction;
        
        // Target chest for current pull/deposit operation
        private Container _targetChestForPull;
        private Container _targetChestForDeposit;
        private string _pullItemPrefab;
        private int _pullItemAmount;
        
        /// <summary>
        /// Whether to use physical chest interaction (walk to chest) vs instant transfer.
        /// Can be disabled for performance or if pathfinding causes issues.
        /// </summary>
        public static bool UsePhysicalChestInteraction = true;
        
        // Commanded target (set by ping system)
        private GameObject _commandedTarget;
        
        // Kiln workflow tracking
        private bool _needsCoalFromKiln = false;
        private bool _isKilnOperation = false; // True if target is a kiln
        
        // Primary smelter reference (for kiln+smelter workflow - tracks the smelter we need to check for output)
        private Smelter _primarySmelter = null;
        private Vector3 _primarySmelterPosition;
        private float _lastSmelterOutputCheck = 0f;
        private const float SMELTER_OUTPUT_CHECK_INTERVAL = 5f; // Check smelter output every 5 seconds during kiln workflow
        
        // Idle waiting state
        private float _lastEmoteTime;
        private float _nextEmoteDelay;
        private static readonly string[] _waitingEmotes = { "emote_nonono", "emote_think", "emote_comehere", "emote_point", "emote_wave" };
        private const float MIN_EMOTE_INTERVAL = 8f;
        private const float MAX_EMOTE_INTERVAL = 20f;
        
        // ANTI-SPAM: Track when smelter is full to prevent continuous fill attempts
        // Once full, we wait for output pickup before trying to fill again
        private bool _smelterIsFullWaitingForOutput = false;
        private float _lastFillAttemptTime = 0f;
        private const float MIN_FILL_ATTEMPT_INTERVAL = 0.5f; // Minimum seconds between fill attempts
        
        // ANTI-LOOP: Cooldown when materials are unavailable to prevent endlessly retrying
        // This gives ResourceGatheringBehavior a chance to gather the needed materials
        private static Dictionary<string, float> _materialUnavailableCooldowns = new Dictionary<string, float>();
        private const float MATERIAL_UNAVAILABLE_COOLDOWN = 60f; // Wait 60 seconds before retrying after material shortage
        
        // Individual input throttling - prevents rapid button spam
        // This matches how a player would interact with the station
        private float _lastOreAddTime = 0f;
        private float _lastFuelAddTime = 0f;
        private float _lastAnyAddTime = 0f;  // Track ANY add operation for animation pacing
        private const float MIN_SINGLE_INPUT_INTERVAL = 1.0f; // Minimum seconds between individual ore/fuel adds (matches animation time)
        
        // ANTI-LOOP: Track that we just pulled items to prevent immediately going back to PullingFromChests
        // This gives the inventory a frame to sync before we check HasOre() again
        private bool _justPulledItems = false;
        private float _pullCompletedTime = 0f;
        private const float POST_PULL_GRACE_PERIOD = 0.5f; // Don't check chests for this long after pulling
        
        // Switch positions for immersive interaction
        private Vector3 _fuelSwitchPosition;
        private Vector3 _oreSwitchPosition;
        private bool _hasFuelSwitch = false;
        private bool _hasOreSwitch = false;
        
        // Item pickup tracking - for deliberate movement to each item
        private ItemDrop _currentPickupTarget = null;
        private Vector3 _currentPickupTargetPosition;
        private List<ItemDrop> _pendingPickupItems = new List<ItemDrop>();
        
        // AUTONOMOUS SCAN CACHING - avoid expensive Physics.OverlapSphere + chest scan every tick
        private Smelter _cachedAutonomousSmelter;
        private float _lastAutonomousScanTime = -999f;
        private const float AUTONOMOUS_SCAN_COOLDOWN = 15f; // Only re-scan every 15 seconds
        private Vector3 _lastAutonomousScanHomePos;
        
        // PATHFINDING PROGRESS TRACKING - for better stuck detection
        private Vector3 _lastProgressPosition;
        private float _lastProgressTime;
        private float _noProgressDuration = 0f;
        private const float PROGRESS_CHECK_INTERVAL = 1.0f;      // How often to check progress
        private const float PROGRESS_THRESHOLD = 0.3f;           // Min distance to count as progress
        private const float NO_PROGRESS_TIMEOUT = 8.0f;          // How long without progress before stuck
        private const float PATHFINDING_TIMEOUT_BASE = 30f;      // Base timeout (increased from 20)
        private const float PATHFINDING_TIMEOUT_PER_METER = 1.5f; // Extra time per meter of distance
        private const float MAX_PATHFINDING_TIMEOUT = 60f;       // Maximum timeout regardless of distance
        
        #endregion
        
        #region Initialization
        
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            
            _character = companion.GetComponent<Character>();
            _humanoid = companion.GetComponent<Humanoid>();
            _inventory = companion.GetComponent<CompanionInventory>();
            _combatMovement = companion.GetComponent<CompanionCombatMovement>();
            _autoPickup = companion.GetComponent<CompanionAutoPickup>();
            _rigidbody = companion.GetComponent<Rigidbody>();
            _zanim = companion.GetComponent<ZSyncAnimation>();
            
            // Initialize ResourceAccessService for unified chest/inventory operations
            _resources = new ResourceAccessService(_inventory, () => Transform.position);
            _resources.SearchRadius = CHEST_SEARCH_RADIUS;
            _resources.VerboseLogging = CompanionIdleBehavior.VerboseLogging;
            _resources.BehaviorName = "SmelterOperator";
            
            // Initialize ChestInteractionService for physical chest pathfinding
            _chestInteraction = new ChestInteractionService(companion, _resources, "SmelterOperator");
            _chestInteraction.VerboseLogging = CompanionIdleBehavior.VerboseLogging;
            
            MaxDuration = MAX_OPERATE_TIME + 60f; // Longer timeout for continuous operation
        }
        
        /// <summary>
        /// Sets a specific smelter/kiln as target (used by command system).
        /// </summary>
        public void SetCommandedTarget(GameObject target)
        {
            _commandedTarget = target;
        }
        
        #endregion
        
        #region CanStart
        
        public override bool CanStart()
        {
            if (Companion == null)
            {
                Debug.Log($"[SmelterOperator] CanStart FAILED: Companion is null");
                return false;
            }

            // Player can disable smelter operation entirely from the radial menu.
            if (!CompanionBehaviorToggles.IsSmelterEnabled(Companion)) return false;

            // ANTI-LOOP: Check if we're on cooldown from a recent material shortage
            string companionId = Companion.companionId ?? Companion.companionName ?? "unknown";
            if (_materialUnavailableCooldowns.TryGetValue(companionId, out float cooldownEnd))
            {
                if (Time.time < cooldownEnd)
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] {Companion.companionName} CanStart FAILED: on cooldown - {cooldownEnd - Time.time:F0}s remaining");
                    return false;
                }
                else
                {
                    // Cooldown expired, remove entry
                    _materialUnavailableCooldowns.Remove(companionId);
                }
            }
            
            // If commanded, always allow (player explicitly told companion to do this)
            if (_commandedTarget != null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] {Companion.companionName} CanStart: Has commanded target {_commandedTarget.name}");
                
                var smelter = _commandedTarget.GetComponent<Smelter>();
                if (smelter == null)
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] {Companion.companionName} CanStart FAILED: Commanded target has no Smelter component");
                    return false;
                }
                
                // CRITICAL FIX: For COMMANDED operations, BYPASS all occupation checks!
                // The player explicitly told this companion to operate this smelter.
                // If another companion is already there, they should gracefully share or yield.
                // We do NOT check InteractableOccupancyManager at all for commanded targets.
                
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmelterOperator] {Companion.companionName} CanStart SUCCESS: Commanded target bypasses occupation checks");
                return true;
            }
            
            // For autonomous operation during Stay mode:
            // Check if companion is staying (has home position) and not actively following
            bool hasHome = IdleBehavior?.HasHomePosition ?? false;
            bool shouldFollow = Companion?.ShouldBeFollowing ?? true;
            
            if (IdleBehavior != null && IdleBehavior.HasHomePosition && !Companion.ShouldBeFollowing)
            {
                // Find a nearby smelter or kiln to operate
                var nearbySmelter = FindNearbySmelterForAutonomousOperation();
                if (nearbySmelter != null)
                {
                    _commandedTarget = nearbySmelter.gameObject;
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] {Companion.companionName} CanStart SUCCESS: Found smelter/kiln for autonomous operation: {nearbySmelter.m_name}");
                    return true;
                }
                else
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmelterOperator] {Companion.companionName} CanStart FAILED: No smelter/kiln needs attention nearby (home={IdleBehavior.HomePosition})");
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Finds a nearby smelter or kiln that needs attention for autonomous operation.
        /// Results are cached for AUTONOMOUS_SCAN_COOLDOWN seconds to avoid expensive
        /// Physics.OverlapSphere + chest scanning on every idle behavior tick.
        /// The cache is invalidated if the companion's home position changes.
        /// </summary>
        private Smelter FindNearbySmelterForAutonomousOperation()
        {
            if (IdleBehavior == null || !IdleBehavior.HasHomePosition) return null;
            
            Vector3 homePos = IdleBehavior.HomePosition;
            
            // Check cache validity: time-based + position-based
            bool cacheValid = (Time.time - _lastAutonomousScanTime) < AUTONOMOUS_SCAN_COOLDOWN
                && Vector3.Distance(homePos, _lastAutonomousScanHomePos) < 1f;
            
            if (cacheValid)
            {
                // Return cached result (may be null = no smelter found last scan)
                // But verify the cached smelter still exists and is still available
                if (_cachedAutonomousSmelter != null)
                {
                    if (_cachedAutonomousSmelter.gameObject == null || !_cachedAutonomousSmelter.gameObject.activeInHierarchy)
                    {
                        _cachedAutonomousSmelter = null;
                    }
                    else if (!InteractableOccupancyManager.CanUseInteractable(_cachedAutonomousSmelter.gameObject, _character))
                    {
                        _cachedAutonomousSmelter = null;
                    }
                }
                return _cachedAutonomousSmelter;
            }
            
            // Cache miss ï¿½ do the expensive scan
            _lastAutonomousScanTime = Time.time;
            _lastAutonomousScanHomePos = homePos;
            
            // Use the effective search radius: stay-mode work radius (50m) capped by the
            // companion's configured wander radius so they never walk outside their boundary.
            float searchRadius = GetEffectiveSearchRadius(STATION_DETECTION_RANGE);
            
            var colliders = Physics.OverlapSphere(homePos, searchRadius);
            
            Smelter bestSmelter = null;
            float bestScore = 0f;
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var smelter = col.GetComponent<Smelter>() ?? col.GetComponentInParent<Smelter>();
                if (smelter == null) continue;
                
                // Check if available
                if (!InteractableOccupancyManager.CanUseInteractable(smelter.gameObject, _character))
                    continue;
                
                // Score this smelter based on how much it needs attention
                float score = ScoreSmelterNeedsAttention(smelter);

                if (score > bestScore && IsReachable(smelter.transform.position))
                {
                    bestScore = score;
                    bestSmelter = smelter;
                }
            }
            
            // Cache the result (even null = no smelter found)
            _cachedAutonomousSmelter = bestScore > 0f ? bestSmelter : null;
            return _cachedAutonomousSmelter;
        }
        
        /// <summary>
        /// Scores how much a smelter needs attention. Higher = more urgent.
        /// 0 = doesn't need attention, 1+ = needs attention
        /// 
        /// SCORING:
        /// - Has output to collect: +2.0 (highest priority - collect finished products)
        /// - Has capacity AND materials in chests: +1.0 (can add more to process)
        /// - Needs fuel AND fuel in chests: +0.5 (keep it running)
        /// - Completely empty AND has materials: +0.5 (get it started)
        /// </summary>
        private float ScoreSmelterNeedsAttention(Smelter smelter)
        {
            if (smelter == null) return 0f;
            
            var nview = smelter.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0f;
            
            float score = 0f;
            
            // Check current state
            int queued = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
            float fuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
            
            bool isEmpty = queued == 0;
            bool needsFuel = smelter.m_maxFuel > 0 && fuel < smelter.m_maxFuel * 0.5f;
            bool hasCapacity = queued < smelter.m_maxOre;
            
            // CRITICAL: Check for output first - collecting finished products is highest priority
            // Check spawn area for any items (spawned output)
            if (smelter.m_spawnStack && smelter.m_outputPoint != null)
            {
                var outputItems = Physics.OverlapSphere(smelter.m_outputPoint.position, 2f);
                foreach (var item in outputItems)
                {
                    if (item != null && item.GetComponent<ItemDrop>() != null)
                    {
                        score += 2.0f; // High priority - collect output!
                        break;
                    }
                }
            }
            
            // Find nearby chests ï¿½ single scan from midpoint between smelter and companion
            // with enough radius to cover both, instead of doing two separate scans + merge
            Vector3 smelterPos = smelter.transform.position;
            Vector3 companionPos = Transform?.position ?? smelterPos;
            Vector3 midpoint = (smelterPos + companionPos) * 0.5f;
            float halfDist = Vector3.Distance(smelterPos, companionPos) * 0.5f;
            float effectiveRadius = CHEST_SEARCH_RADIUS + halfDist;
            
            var nearbyChests = ChestHelper.FindNearbyChests(midpoint, effectiveRadius);
            
            if (CompanionIdleBehavior.VerboseLogging)
            {
                Debug.Log($"[SmelterOperator] {Companion?.companionName} ScoreSmelterNeedsAttention for {smelter.m_name}:");
                Debug.Log($"[SmelterOperator]   - Smelter pos: {smelterPos}, Companion pos: {companionPos}");
                Debug.Log($"[SmelterOperator]   - Found {nearbyChests.Count} chests within {effectiveRadius:F0}m of midpoint");
                Debug.Log($"[SmelterOperator]   - State: queued={queued}/{smelter.m_maxOre}, fuel={fuel:F0}/{smelter.m_maxFuel}, hasCapacity={hasCapacity}, needsFuel={needsFuel}");
            }
            
            string stationType = PieceDataHelper.GetSmelterType(smelter);
            bool isKiln = stationType.ToLowerInvariant().Contains("kiln");
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator]   - Station type: {stationType}, isKiln={isKiln}");
            
            if (isKiln)
            {
                // Kiln needs wood
                if (hasCapacity)
                {
                    string[] woodTypes = { "Wood", "RoundLog", "FineWood", "ElderBark", "YggdrasilWood" };
                    bool foundWoodInChest = false;
                    foreach (string woodType in woodTypes)
                    {
                        int count = ChestHelper.CountItemInChests(nearbyChests, woodType);
                        if (count > 0)
                        {
                            if (CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[SmelterOperator]   - Found {count}x {woodType} in chests!");
                            score += 1f;
                            foundWoodInChest = true;
                            break;
                        }
                    }
                    
                    if (!foundWoodInChest && CompanionIdleBehavior.VerboseLogging)
                    {
                        Debug.Log($"[SmelterOperator]   - No wood found in {nearbyChests.Count} chests");
                    }
                    
                    // Also check companion's own inventory for wood
                    if (!foundWoodInChest && _inventory != null)
                    {
                        var storage = _inventory.GetStorageInventory();
                        if (storage != null)
                        {
                            foreach (string woodType in woodTypes)
                            {
                                if (storage.HaveItem(woodType))
                                {
                                    if (CompanionIdleBehavior.VerboseLogging)
                                        Debug.Log($"[SmelterOperator]   - Found {woodType} in companion inventory!");
                                    score += 1f;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                // Smelter needs ore and possibly fuel
                if (hasCapacity)
                {
                    // Check for ore in chests
                    bool foundOre = false;
                    foreach (var conversion in smelter.m_conversion)
                    {
                        if (conversion.m_from != null)
                        {
                            string oreName = conversion.m_from.name;
                        int count = ChestHelper.CountItemInChests(nearbyChests, oreName);
                            if (count > 0)
                            {
                                if (CompanionIdleBehavior.VerboseLogging)
                                    Debug.Log($"[SmelterOperator]   - Found {count}x {oreName} in chests!");
                                score += 1f;
                                foundOre = true;
                                break;
                            }
                        }
                    }
                    
                    if (!foundOre && CompanionIdleBehavior.VerboseLogging)
                    {
                        // Log what ores we looked for
                        var oreNames = new List<string>();
                        foreach (var conversion in smelter.m_conversion)
                        {
                            if (conversion.m_from != null)
                                oreNames.Add(conversion.m_from.name);
                        }
                        Debug.Log($"[SmelterOperator]   - No ore found in chests. Searched for: {string.Join(", ", oreNames)}");
                        
                        // Log what IS in the chests
                        foreach (var chest in nearbyChests)
                        {
                            if (chest == null) continue;
                            var inv = chest.GetInventory();
                            if (inv == null) continue;
                            var items = inv.GetAllItems();
                            if (items.Count > 0)
                            {
                                var itemNames = new List<string>();
                                foreach (var item in items)
                                {
                                    if (item?.m_dropPrefab != null)
                                        itemNames.Add($"{item.m_dropPrefab.name}x{item.m_stack}");
                                }
                                Debug.Log($"[SmelterOperator]     Chest '{chest.name}' at {chest.transform.position} contains: {string.Join(", ", itemNames)}");
                            }
                        }
                    }
                    
                    // Also check companion's own inventory for ore
                    if (!foundOre && _inventory != null)
                    {
                        var storage = _inventory.GetStorageInventory();
                        if (storage != null)
                        {
                            foreach (var conversion in smelter.m_conversion)
                            {
                                if (conversion.m_from != null && storage.HaveItem(conversion.m_from.name))
                                {
                                    if (CompanionIdleBehavior.VerboseLogging)
                                        Debug.Log($"[SmelterOperator]   - Found {conversion.m_from.name} in companion inventory!");
                                    score += 1f;
                                    break;
                                }
                            }
                        }
                    }
                }
                
                // Bonus if needs fuel and we have it
                if (needsFuel && smelter.m_fuelItem != null)
                {
                    string fuelName = smelter.m_fuelItem.name;
                    int fuelCount = ChestHelper.CountItemInChests(nearbyChests, fuelName);
                    if (fuelCount > 0)
                    {
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[SmelterOperator]   - Found {fuelCount}x {fuelName} (fuel) in chests!");
                        score += 0.5f;
                    }
                    else if (_inventory != null)
                    {
                        var storage = _inventory.GetStorageInventory();
                        if (storage != null && storage.HaveItem(fuelName))
                        {
                            if (CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[SmelterOperator]   - Found {fuelName} (fuel) in companion inventory!");
                            score += 0.5f;
                        }
                    }
                }
            }
            
            // Bonus for completely empty (more urgent to fill)
            if (isEmpty && score > 0)
            {
                score += 0.5f;
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmelterOperator]   - FINAL SCORE: {score:F1}");
            
            return score;
        }
        
        #endregion
    }
}
