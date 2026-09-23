using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Movement;
using FiresCore.Npc.Core;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Handles companion resource gathering (mining, chopping trees, picking plants).
    /// 
    /// PARTIAL CLASS STRUCTURE:
    /// - ResourceGatheringBehavior.cs - Core fields, settings, initialization, CanStart
    /// - ResourceGatheringBehavior.Lifecycle.cs - Start, Update, Cancel, phase routing
    /// - ResourceGatheringBehavior.TreeGathering.cs - Tree/log detection and gathering
    /// - ResourceGatheringBehavior.MiningOre.cs - Ore detection and mining logic
    /// - ResourceGatheringBehavior.ItemCollection.cs - Item pickup, chest deposits
    /// - ResourceGatheringBehavior.Tools.cs - Tool management, equipping
    /// </summary>
    public partial class ResourceGatheringBehavior : IdleSubBehavior
    {
        public override string BehaviorName => "ResourceGathering";
        public override bool SupportsResumption => true;
        public override bool AvailableForIdleRotation => true;
        public override float WanderRadiusMultiplier => WORK_WANDER_RADIUS_MULTIPLIER;
        
        /// <summary>
        /// Resource gathering requires inventory space - don't start if full.
        /// </summary>
        public override bool RequiresInventorySpace => true;
        
        /// <summary>
        /// Returns NEGATIVE priority when inventory is full.
        /// This ensures gathering is deprioritized in favor of deposit behaviors.
        /// </summary>
        public override int InventoryPriority
        {
            get
            {
                // If inventory is nearly full, return negative priority
                // so deposit behaviors get priority instead
                if (InventoryNeedsDeposit())
                {
                    return -50; // Low priority - let deposit run first
                }
                return 0; // Normal priority when inventory has room
            }
        }
        
        public const float WORK_WANDER_RADIUS_MULTIPLIER = 3f;
        
        #region Settings
        
        private const float ResourceDetectionRange = 15f;
        private const float AttackRange = 2.5f;
        private const float PickableRange = 2f;
        private const float AttackInterval = 1.8f;
        private const float DamageDelay = 0.6f;
        private const float MaxGatherTime = 120f;
        private const float PostDestroyWait = 2f;
        private const float LogCheckWait = 5f;
        private const float LogSearchRadius = 10f;
        private const float ContinueGatheringChance = 0.5f;
        private const float InventoryFullThreshold = 0.8f;
        private float CHEST_SEARCH_RADIUS => CompanionSettings.ChestSearchRadius;
        
        #endregion
        
        #region State
        
        private enum GatherPhase
        {
            FindingResource,
            MovingToChestForTool,      // Walk to chest to get required tool
            RetrievingToolFromChest,   // Open chest and pull tool
            MovingToWorkbench,         // Walk to workbench to craft a tool
            CraftingTool,              // Stand at workbench and craft
            MovingToResource,
            Interacting,
            Attacking,
            Repositioning,
            WaitingForLogs,
            WaitingForDrops,
            DepositingToChests,
            MovingToChest,
            Complete
        }
        
        private GatherPhase _currentPhase = GatherPhase.FindingResource;
        private ResourceDataHelper.ResourceData _targetResource;
        private Vector3 _targetPosition;
        private float _phaseStartTime;
        private float _lastAttackTime;
        private int _resourcesGathered;
        private readonly ResourceDataHelper.AttackChain _swingChain = new ResourceDataHelper.AttackChain();
        
        // MineRock repositioning
        private int _consecutiveNoColliderHits = 0;
        private const int MaxNoColliderBeforeReposition = 3;
        
        // Tree->log transitions
        private bool _wasTargetingTree = false;
        private Vector3 _lastTreePosition;
        
        // Stump tracking for sapling spawning
        private bool _wasTargetingStump = false;
        private string _targetStumpName;
        private Vector3 _targetStumpPosition;
        
        public static bool VerboseLogging = false;
        
        // Components
        private Character _character;
        private Humanoid _humanoid;
        private Animator _animator;
        private ZSyncAnimation _zanim;
        private CompanionCombatMovement _combatMovement;
        private CompanionInventory _inventory;
        private Combat.WeaponSwapManager _weaponSwapManager;
        private CompanionAutoPickup _autoPickup;
        private Rigidbody _rigidbody;
        
        // Resource access service for unified chest/inventory operations
        private ResourceAccessService _resources;
        
        // Commanded target
        private GameObject _commandedTarget;
        
        // Chest deposit state
        private List<Container> _nearbyChests = new List<Container>();
        private Container _targetChest;
        private Vector3 _chestPosition;
        
        // Tool retrieval state
        private Container _toolChest;           // Chest containing required tool
        private ItemDrop.ItemData _toolToPull;  // The tool item we need to retrieve
        private ResourceDataHelper.ToolType _requiredToolType;
        private int _requiredToolTier;

        // Crafting state
        private Recipe _craftRecipe;
        private CraftingStation _craftWorkbench;
        
        #endregion
        
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            
            _character = companion.GetComponent<Character>();
            _humanoid = companion.GetComponent<Humanoid>();
            _animator = companion.GetComponentInChildren<Animator>(true);
            _zanim = companion.GetComponent<ZSyncAnimation>();
            _combatMovement = companion.GetComponent<CompanionCombatMovement>();
            _inventory = companion.GetComponent<CompanionInventory>();
            _weaponSwapManager = companion.GetComponent<Combat.WeaponSwapManager>();
            _autoPickup = companion.GetComponent<CompanionAutoPickup>();
            _rigidbody = companion.GetComponent<Rigidbody>();
            
            // Initialize ResourceAccessService for unified chest/inventory operations
            _resources = new ResourceAccessService(_inventory, () => Transform.position);
            _resources.SearchRadius = CHEST_SEARCH_RADIUS;
            _resources.VerboseLogging = VerboseLogging || CompanionIdleBehavior.VerboseLogging;
            _resources.BehaviorName = "ResourceGathering";
            
            MaxDuration = MaxGatherTime + 30f;
        }
        
        public void SetCommandedTarget(GameObject target)
        {
            _commandedTarget = target;
        }
        
        public override bool CanStart()
        {
            if (Companion == null) return false;

            bool commanded = _commandedTarget != null;

            // Autonomous gathering is gated by the radial-menu toggle. A player-COMMANDED gather always runs.
            if (!commanded && !CompanionBehaviorToggles.IsGatherEnabled(Companion))
            {
                return false;
            }

            // Inventory-space gate applies to AUTONOMOUS gathering only. A player-commanded gather runs even
            // if storage is full (the resource overflows/drops) rather than being silently refused — which
            // left the companion oscillating in the command's fallback path.
            if (!commanded && !CanStartBase())
            {
                return false;
            }

            if (commanded)
            {
                var resourceData = ResourceDataHelper.GetResourceData(_commandedTarget);
                if (resourceData == null || !resourceData.IsValid)
                {
                    return false;
                }
                
                // CRITICAL: Check if we have the required tool BEFORE starting the behavior!
                // This prevents the behavior from starting and immediately completing as "success"
                if (resourceData.RequiresCombat)
                {
                    // Check if tool is in inventory or equipped
                    if (HasToolEquippedOrInInventory(resourceData))
                    {
                        return true;
                    }
                    
                    // Check if tool is available in nearby chests (just check, don't set up state yet)
                    // Start() will call TryFindToolInNearbyChests() again to set up _toolChest and _toolToPull
                    if (HasToolInNearbyChests(resourceData))
                    {
                        if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[ResourceGathering] {Companion?.companionName} found required tool in nearby chest for {resourceData.Name}");
                        return true;
                    }
                    
                    // No tool available anywhere - cannot start this behavior
                    Debug.LogWarning($"[ResourceGathering] {Companion?.companionName} cannot gather {resourceData.Name} - no {resourceData.RequiredTool} available");
                    CompanionChatHelper.QuickMessages.CantDoTask(Companion, $"I need a {resourceData.RequiredTool.ToString().ToLower()}");
                    return false;
                }
                
                return true;
            }
            
            if (IdleBehavior != null && IdleBehavior.HasHomePosition && !Companion.ShouldBeFollowing)
            {
                var neededResource = FindNeededResourceNearHome();
                if (neededResource != null)
                {
                    _commandedTarget = neededResource;
                    
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion?.companionName} found resource to gather autonomously: {neededResource.name}");
                    
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if a tool for the resource exists in nearby chests (read-only check, doesn't set up state).
        /// </summary>
        private bool HasToolInNearbyChests(ResourceDataHelper.ResourceData resource)
        {
            if (resource == null) return false;
            
            var requiredTool = resource.RequiredTool;
            var minTier = resource.MinToolTier;
            
            // CRITICAL: Search from companion's CURRENT position, not home position!
            // When commanded to gather, the companion might be following the player far from home.
            Vector3 position = Transform.position;
            float searchRadius = CompanionSettings.ChestSearchRadius;
            
            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion?.companionName} HasToolInNearbyChests: searching for {requiredTool} tier>={minTier} within {searchRadius}m of {position}");
            
            var nearbyChests = ChestHelper.FindNearbyChests(position, searchRadius);
            if (nearbyChests == null || nearbyChests.Count == 0)
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} HasToolInNearbyChests: no chests found within {searchRadius}m");
                return false;
            }
            
            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion?.companionName} HasToolInNearbyChests: found {nearbyChests.Count} chests, searching for tool...");
            
            foreach (var chest in nearbyChests)
            {
                if (chest == null) continue;
                
                var chestInv = chest.GetInventory();
                if (chestInv == null) continue;
                
                foreach (var item in chestInv.GetAllItems())
                {
                    if (item == null) continue;
                    
                    if (ResourceDataHelper.IsToolAppropriate(item, requiredTool, minTier))
                    {
                        if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[ResourceGathering] {Companion?.companionName} HasToolInNearbyChests: FOUND {item.m_shared?.m_name} in chest at {chest.transform.position}");
                        return true;
                    }
                }
            }
            
            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion?.companionName} HasToolInNearbyChests: no appropriate tool found in any chest");
            
            return false;
        }
        
        private GameObject FindNeededResourceNearHome()
        {
            if (IdleBehavior == null || !IdleBehavior.HasHomePosition) return null;
            
            Vector3 homePos = IdleBehavior.HomePosition;
            
            // Use the stay mode work search radius (50m default, or territory bounds if larger)
            float searchRadius = CompanionSettings.GetStayModeWorkSearchRadius(homePos);
            
            var neededStationInputs = GetInputsNeededByNearbySmelters(homePos);
            bool needsWood = CheckIfNearbyKilnNeedsWood(homePos);
            int? obtainablePickaxeTier = null;
            int ObtainablePickaxeTier() => obtainablePickaxeTier ??= GetBestObtainableToolTier(ResourceDataHelper.ToolType.Pickaxe);
            
            // Priority 1: Station needs - always try these first
            if (neededStationInputs.Count > 0)
            {
                var oreDeposit = FindNearbyOreDeposit(homePos, searchRadius, neededStationInputs, ObtainablePickaxeTier());
                if (oreDeposit != null)
                {
                    var resourceData = ResourceDataHelper.GetResourceData(oreDeposit);
                    if (resourceData != null && resourceData.IsValid)
                    {
                        if (!resourceData.RequiresCombat || HasToolForResource(resourceData))
                        {
                            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[ResourceGathering] {Companion?.companionName} found ore deposit (smelter needs ore)");
                            return oreDeposit;
                        }
                    }
                }
            }
            
            if (needsWood)
            {
                var tree = FindNearbyTree(homePos, searchRadius);
                if (tree != null)
                {
                    var resourceData = ResourceDataHelper.GetResourceData(tree);
                    if (resourceData != null && resourceData.IsValid)
                    {
                        if (!resourceData.RequiresCombat || HasToolForResource(resourceData))
                        {
                            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[ResourceGathering] {Companion?.companionName} found tree (kiln needs wood)");
                            return tree;
                        }
                    }
                }
            }
            
            // Priority 2: Always pick up pickables (berries, mushrooms, etc.) - these are free!
            var pickable = FindNearbyPickable(homePos, searchRadius);
            if (pickable != null)
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} found pickable: {pickable.name}");
                return pickable;
            }
            
            // Priority 3: Opportunistic gathering - gather resources even without station needs
            // This makes stayed companions actually useful by gathering wood/ore proactively
            // 85% chance to opportunistically gather (was 70% - increased for better worker behavior)
            if (Random.value < 0.85f)
            {
                // Prioritize based on what tools companion has
                bool hasAxe = HasToolOfType(ResourceDataHelper.ToolType.Axe);
                bool hasPickaxe = HasToolOfType(ResourceDataHelper.ToolType.Pickaxe);
                
                // If companion has axe, try trees first
                if (hasAxe)
                {
                    var tree = FindNearbyTree(homePos, searchRadius);
                    if (tree != null)
                    {
                        var resourceData = ResourceDataHelper.GetResourceData(tree);
                        if (resourceData != null && resourceData.IsValid)
                        {
                            if (!resourceData.RequiresCombat || HasToolForResource(resourceData))
                            {
                                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                                    Debug.Log($"[ResourceGathering] {Companion?.companionName} opportunistically gathering tree: {tree.name}");
                                return tree;
                            }
                        }
                    }
                }
                
                // If companion has pickaxe, try ore deposits
                if (hasPickaxe)
                {
                    var oreDeposit = FindNearbyOreDeposit(homePos, searchRadius, ResourceDataHelper.SmeltableItems, ObtainablePickaxeTier());
                    if (oreDeposit != null)
                    {
                        var resourceData = ResourceDataHelper.GetResourceData(oreDeposit);
                        if (resourceData != null && resourceData.IsValid)
                        {
                            if (!resourceData.RequiresCombat || HasToolForResource(resourceData))
                            {
                                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                                    Debug.Log($"[ResourceGathering] {Companion?.companionName} opportunistically gathering ore: {oreDeposit.name}");
                                return oreDeposit;
                            }
                        }
                    }
                }
                
                // Fallback: try both even without explicit tool check (tools might be in chests)
                if (!hasAxe)
                {
                    var tree = FindNearbyTree(homePos, searchRadius);
                    if (tree != null)
                    {
                        var resourceData = ResourceDataHelper.GetResourceData(tree);
                        if (resourceData != null && resourceData.IsValid && HasToolForResource(resourceData))
                        {
                            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[ResourceGathering] {Companion?.companionName} opportunistically gathering tree (tool from chest): {tree.name}");
                            return tree;
                        }
                    }
                }
                
                if (!hasPickaxe)
                {
                    var oreDeposit = FindNearbyOreDeposit(homePos, searchRadius, ResourceDataHelper.SmeltableItems, ObtainablePickaxeTier());
                    if (oreDeposit != null)
                    {
                        var resourceData = ResourceDataHelper.GetResourceData(oreDeposit);
                        if (resourceData != null && resourceData.IsValid && HasToolForResource(resourceData))
                        {
                            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[ResourceGathering] {Companion?.companionName} opportunistically gathering ore (tool from chest): {oreDeposit.name}");
                            return oreDeposit;
                        }
                    }
                }
            }
            
            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion?.companionName} no resources found near home (station inputs needed={neededStationInputs.Count}, needsWood={needsWood})");
            
            return null;
        }
        
        /// <summary>
        /// Checks if companion has a specific tool type in their inventory or equipped.
        /// </summary>
        private bool HasToolOfType(ResourceDataHelper.ToolType tool)
        {
            if (_inventory == null) return false;
            
            if (ResourceDataHelper.IsToolAppropriate(GetEquippedWeaponOrTool(), tool, 0)) return true;
            
            var storage = _inventory.GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    if (ResourceDataHelper.IsToolAppropriate(item, tool, 0)) return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Inputs of the non-kiln smelting stations nearby that have room for more and nothing to load in the nearby
        /// chests; gathering for them may only target what they actually convert.
        /// </summary>
        private HashSet<string> GetInputsNeededByNearbySmelters(Vector3 position)
        {
            var neededInputs = new HashSet<string>();
            var checkedStations = new HashSet<Smelter>();

            // Use stay mode work radius for staying companions
            float searchRadius = IdleBehavior != null && IdleBehavior.HasHomePosition && Companion != null && !Companion.ShouldBeFollowing
                ? CompanionSettings.GetStayModeWorkSearchRadius(position)
                : CompanionSettings.ChestSearchRadius;
            
            var colliders = Physics.OverlapSphere(position, searchRadius);
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var smelter = collider.GetComponent<Smelter>() ?? collider.GetComponentInParent<Smelter>();
                if (smelter == null || !checkedStations.Add(smelter)) continue;
                
                if (!PieceDataHelper.IsOperableStation(smelter) || PieceDataHelper.IsCharcoalKiln(smelter)) continue;
                
                var nview = smelter.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                
                int queued = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
                
                if (queued < smelter.m_maxOre)
                {
                    var nearbyChests = ChestHelper.FindNearbyChests(smelter.transform.position, searchRadius);
                    bool hasOreInChests = false;
                    
                    foreach (var conversion in smelter.m_conversion)
                    {
                        if (conversion.m_from != null && ChestHelper.GetAvailableItemCount(nearbyChests, conversion.m_from.name) > 0)
                        {
                            hasOreInChests = true;
                            break;
                        }
                    }
                    
                    if (!hasOreInChests)
                    {
                        neededInputs.UnionWith(PieceDataHelper.GetStationInputs(smelter));
                    }
                }
            }
            
            return neededInputs;
        }
        
        private bool CheckIfNearbyKilnNeedsWood(Vector3 position)
        {
            // Use stay mode work radius for staying companions
            float searchRadius = IdleBehavior != null && IdleBehavior.HasHomePosition && Companion != null && !Companion.ShouldBeFollowing
                ? CompanionSettings.GetStayModeWorkSearchRadius(position)
                : CompanionSettings.ChestSearchRadius;
            
            var colliders = Physics.OverlapSphere(position, searchRadius);
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var smelter = collider.GetComponent<Smelter>() ?? collider.GetComponentInParent<Smelter>();
                if (smelter == null) continue;
                
                if (!PieceDataHelper.IsCharcoalKiln(smelter)) continue;
                
                var nview = smelter.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                
                int queued = nview.GetZDO().GetInt(ZDOVars.s_queued, 0);
                
                if (queued < smelter.m_maxOre)
                {
                    var nearbyChests = ChestHelper.FindNearbyChests(smelter.transform.position, searchRadius);
                    
                    bool hasWoodInChests = false;

                    foreach (string woodType in PieceDataHelper.GetStationInputs(smelter))
                    {
                        if (ChestHelper.GetAvailableItemCount(nearbyChests, woodType) > 0)
                        {
                            hasWoodInChests = true;
                            break;
                        }
                    }
                    
                    if (!hasWoodInChests)
                    {
                        return true;
                    }
                }
            }
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var fireplace = collider.GetComponent<Fireplace>() ?? collider.GetComponentInParent<Fireplace>();
                if (fireplace == null) continue;
                if (!fireplace.m_canRefill || fireplace.m_infiniteFuel) continue;
                
                var nview = fireplace.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                
                float currentFuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel, 0f);
                
                if (currentFuel < fireplace.m_maxFuel * 0.5f)
                {
                    var nearbyChests = ChestHelper.FindNearbyChests(fireplace.transform.position, searchRadius);
                    // A fire burns exactly its m_fuelItem (Fireplace.UseItem compares against it).
                    string fuelName = fireplace.m_fuelItem != null ? fireplace.m_fuelItem.gameObject.name : null;
                    bool hasFuelInChests = fuelName != null && ChestHelper.GetAvailableItemCount(nearbyChests, fuelName) > 0;

                    if (!hasFuelInChests)
                    {
                        if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[ResourceGathering] Fireplace at {fireplace.transform.position} needs fuel and no fuel in chests");
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        private GameObject FindNearbyPickable(Vector3 position, float radius)
        {
            if (!CanPickHere()) return null;

            var colliders = Physics.OverlapSphere(position, radius);
            float closestDist = float.MaxValue;
            GameObject closest = null;
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var pickable = collider.GetComponent<Pickable>() ?? collider.GetComponentInParent<Pickable>();
                if (pickable == null) continue;
                if (!pickable.CanBePicked()) continue;
                
                float dist = Vector3.Distance(position, pickable.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = pickable.gameObject;
                }
            }
            
            return closest;
        }
        
        /// <summary>Pickable.RPC_Pick runs on the pickable's owner and reads Player.m_localPlayer (Pickable.cs:171).</summary>
        private static bool CanPickHere() => Player.m_localPlayer != null;

        public override string GetStatusDescription()
        {
            string resourceName = _targetResource?.Name ?? "resource";
            return _currentPhase switch
            {
                GatherPhase.MovingToChestForTool => "Getting tool from chest",
                GatherPhase.RetrievingToolFromChest => "Retrieving tool",
                GatherPhase.MovingToWorkbench => "Going to workbench",
                GatherPhase.CraftingTool => "Crafting a tool",
                GatherPhase.MovingToResource => $"Walking to {resourceName}",
                GatherPhase.Interacting => $"Picking {resourceName}",
                GatherPhase.Attacking => $"Gathering {resourceName}",
                GatherPhase.WaitingForDrops => "Collecting drops",
                _ => "Gathering resources"
            };
        }
        
        #region Helpers
        
        private ResourceDataHelper.ResourceData FindNearbyResource()
        {
            return ResourceDataHelper.FindNearestResource(
                Transform.position, 
                ResourceDetectionRange,
                resource => 
                {
                    if (resource.RequiresCombat)
                    {
                        var weapon = GetEquippedWeaponOrTool();
                        if (!ResourceDataHelper.IsToolAppropriate(weapon, resource.RequiredTool, resource.MinToolTier))
                        {
                            return false;
                        }
                    }
                    return true;
                }
            );
        }
        
        private void SetPhase(GatherPhase phase)
        {
            _currentPhase = phase;
            _phaseStartTime = Time.time;
        }
        
        private void MoveToPosition(Vector3 position)
        {
            _targetPosition = position;
            
            // CRITICAL FIX: Use base class TryMoveToPosition for proper vanilla pathfinding
            // This uses CompanionAI.RequestPathfindingMovement() which calls BaseAI.MoveTo()
            // and properly navigates around obstacles using Valheim's pathfinding system.
            TryMoveToPosition(position, walk: true, run: false);
        }
        
        private new void StopMovement()
        {
            // Use base class StopMovement which properly releases authority
            base.StopMovement();
            
            // Also clear combat movement destination as backup
            if (_combatMovement != null)
            {
                _combatMovement.ClearMoveDestination();
            }
            
            // Zero velocity for immediate stop
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                Vector3 vel = _rigidbody.linearVelocity;
                _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
            }
        }
        
        private void FaceTarget(Vector3 targetPos)
        {
            Vector3 dir = targetPos - Transform.position;
            dir.y = 0;
            if (dir.sqrMagnitude < 0.0001f) return;

            // Single facing-writer: face the resource through the FacingAuthority (SubBehavior); combat
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
        
        #endregion
    }
}
