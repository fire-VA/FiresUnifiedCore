using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using FiresCore.Npc.AI;
using FiresCore.Npc.Interactions;
using FiresCore.Npc.IdleBehaviors;
using FiresCore.Npc.NpcMode;
using FiresCore.Npc.Movement;
using FiresCore.Npc.Vault;

namespace FiresCore.Npc.Commands
{
    /// <summary>
    /// Handles player-issued commands to companions via a "ping" system.
    /// 
    /// USAGE:
    /// - Shift + Middle Mouse Button on a target directs the companion to interact with it.
    ///   Vanilla MMB unarmed-kick is suppressed while Shift is held (see
    ///   CompanionPatches.Player_PlayerAttackInput_Prefix). Armed weapon attacks
    ///   and sprint are NOT suppressed â€” Shift remains usable for normal play.
    ///
    /// SUPPORTED TARGETS:
    /// - Enemy/Creature        â†’ AttackTarget    (CompanionAI.ForceTarget)
    /// - Chair/Bench/Stool     â†’ SitOnChair      (CompanionInteractionBehavior)
    /// - Archery target        â†’ TrainArchery    (BowTrainingBehavior)
    /// - Smelter/Kiln          â†’ OperateSmelter  (SmelterOperatorBehavior)
    /// - CookingStation        â†’ CookFood        (CompanionCookingBehavior)
    /// - Fireplace             â†’ TendFire        (FireTendingBehaviorV2)
    /// - Crafting Station      â†’ UseWorkstation  (WorkstationInteractionBehavior)
    /// - Container/Chest       â†’ DepositToChest  (ChestDepositBehavior)
    /// - Beehive               â†’ Farm            (FarmingBehavior)
    /// - Plant / Pickable crop â†’ Farm            (FarmingBehavior)
    /// - Cultivated soil       â†’ Farm            (FarmingBehavior â€” planting)
    /// - Trees / rocks / ore   â†’ GatherResource  (CompanionInteractionBehavior)
    /// - Damaged building      â†’ RepairBuilding  (BuildingRepairBehavior)
    /// - Water surface         â†’ Fish            (FishingBehavior)
    /// - Other destructible    â†’ AttackTarget
    /// - Ground / piece / etc. â†’ MoveToPosition
    ///
    /// DESIGN:
    /// - Commands are temporary overrides that clear after completion.
    /// - Each idle behavior exposes SetCommandedTarget(GameObject) (or
    ///   SetCommandedSpot(Vector3) for fishing) which bypasses the autonomous
    ///   discovery + Stay-mode gates so the companion will leave Follow mode
    ///   to honor the player's request.
    /// - Companion returns to normal behavior after executing command.
    /// - Visual/audio feedback when command is issued.
    /// </summary>
    public class CompanionCommandSystem : MonoBehaviour
    {
        #region Singleton

        private static CompanionCommandSystem _instance;
        public static CompanionCommandSystem Instance => _instance;

        #endregion

        #region Settings

        [Header("Keybind Settings")]
        [Tooltip("Modifier key required (Shift by default)")]
        public KeyCode modifierKey = KeyCode.LeftShift;
        [Tooltip("Mouse button for ping (2 = Middle Mouse)")]
        public int mouseButton = 2;
        [Tooltip("Maximum raycast distance for targeting")]
        public float maxTargetDistance = 50f;
        [Tooltip("Key to trigger NPC placement mode (P by default)")]
        public KeyCode npcPlacementKey = KeyCode.P;
        [Tooltip("Key to unstation an NPC and return to companion mode (U by default)")]
        public KeyCode npcUnstationKey = KeyCode.U;
        [Tooltip("Key to toggle idle wandering for stationed NPCs (I by default)")]
        public KeyCode npcToggleIdleKey = KeyCode.I;

        [Header("Command Settings")]
        [Tooltip("How long a priority target command lasts before expiring")]
        public float priorityTargetDuration = 30f;
        [Tooltip("How close companion needs to be to interact with target")]
        public float interactionDistance = 2f;
        [Tooltip("How close companion needs to be to reach a move destination")]
        public float moveDestinationThreshold = 1.5f;

        [Header("Feedback Settings")]
        [Tooltip("Show visual ping marker at target location")]
        public bool showPingMarker = true;
        [Tooltip("How long the ping marker is visible")]
        public float pingMarkerDuration = 3f;

        public static bool VerboseLogging = true;  // ENABLED FOR DEBUGGING

        #endregion

        #region State

        // Currently active commands per companion
        private Dictionary<CompanionController, ActiveCommand> _activeCommands = new Dictionary<CompanionController, ActiveCommand>();

        // Ping marker object pool
        private GameObject _pingMarkerPrefab;
        private List<GameObject> _activePingMarkers = new List<GameObject>();

        // Layer masks for raycasting
        private int _targetLayerMask;

        #endregion

        #region Command Types

        public enum CommandType
        {
            None,
            AttackTarget,
            MoveToPosition,
            SitOnChair,
            TrainArchery,
            InteractWithObject,
            GatherResource,
            OperateSmelter,
            TendFire,
            UseWorkstation,
            DepositToChest,
            // Newer command types â€” wired to the matching idle behaviors via
            // SetCommandedTarget on each behavior class.
            CookFood,           // â†’ CompanionCookingBehavior   (CookingStation)
            RepairBuilding,     // â†’ BuildingRepairBehavior     (damaged Piece/WearNTear)
            Fish,               // â†’ FishingBehavior            (water surface)
            Farm,               // â†’ FarmingBehavior            (Beehive / Pickable / cultivated)
        }

        public class ActiveCommand
        {
            public CommandType Type;
            public Vector3 TargetPosition;
            public GameObject TargetObject;
            public Character TargetCharacter;
            public float CommandTime;
            public float ExpirationTime;
            public bool IsComplete;

            public ActiveCommand(CommandType type, Vector3 position, GameObject targetObj = null, Character targetChar = null, float duration = 30f)
            {
                Type = type;
                TargetPosition = position;
                TargetObject = targetObj;
                TargetCharacter = targetChar;
                CommandTime = Time.time;
                ExpirationTime = Time.time + duration;
                IsComplete = false;
            }
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;

            // Setup layer mask for raycasting (characters, default, pieces, terrain)
            _targetLayerMask = LayerMask.GetMask("character", "Default", "piece", "terrain", "static_solid", "piece_nonsolid");
            if (_targetLayerMask == 0)
            {
                // Fallback if named layers don't exist
                _targetLayerMask = ~0; // All layers
            }
        }

        private void Update()
        {
            // Check for command input
            if (ShouldProcessInput())
            {
                ProcessCommandInput();
            }

            // Update active commands
            UpdateActiveCommands();
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;

            // Clean up ping markers
            foreach (var marker in _activePingMarkers)
            {
                if (marker != null)
                    Destroy(marker);
            }
            _activePingMarkers.Clear();
        }

        #endregion

        #region Input Processing

        private bool ShouldProcessInput()
        {
            // Don't process if UI is open or game is paused
            if (Menu.IsVisible()) 
            {
                return false;
            }
            if (Console.IsVisible()) 
            {
                return false;
            }
            if (TextInput.IsVisible()) 
            {
                return false;
            }
            if (Minimap.IsOpen()) 
            {
                return false;
            }
            if (InventoryGui.IsVisible()) 
            {
                return false;
            }
            if (StoreGui.IsVisible()) 
            {
                return false;
            }

            var player = Player.m_localPlayer;
            if (player == null) 
            {
                return false;
            }

            return true;
        }

        private void ProcessCommandInput()
        {
            // Check for modifier + mouse button (ping command)
            bool shiftHeld = UnityEngine.Input.GetKey(modifierKey) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            bool altHeld = UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt);
            bool mousePressed = UnityEngine.Input.GetMouseButtonDown(mouseButton);

            // Check for NPC placement mode (Shift+P for admins)
            if (shiftHeld && UnityEngine.Input.GetKeyDown(npcPlacementKey))
            {
                ProcessNpcPlacementInput();
                return;
            }

            // Check for unstation NPC (Shift+U for admins)
            if (shiftHeld && UnityEngine.Input.GetKeyDown(npcUnstationKey))
            {
                ProcessNpcUnstationInput();
                return;
            }

            // Check for toggle idle wandering (Shift+I for admins)
            if (shiftHeld && UnityEngine.Input.GetKeyDown(npcToggleIdleKey))
            {
                ProcessNpcToggleIdleInput();
                return;
            }
            
            // Check for Alt+Shift+MMB - Stay/Follow toggle command
            if (altHeld && shiftHeld && mousePressed)
            {
                ProcessStayFollowCommand();
                return;
            }

            if (!shiftHeld || !mousePressed) return;

            // Notify patches that ping was used - blocks player kick attack
            CompanionPatches.NotifyPingUsed();

            var player = Player.m_localPlayer;
            if (player == null) return;

            // Get all owned companions
            var companions = GetOwnedCompanions(player);
            if (companions.Count == 0)
            {
                ShowMessage("No companions to command");
                return;
            }

            // Raycast from camera to find target
            var target = RaycastForTarget();
            if (target == null)
            {
                ShowMessage("No valid target");
                return;
            }

            // Use coordinator to select the best companion for this command
            // This prevents duplicate commands and selects the closest available companion
            var coordinator = CompanionCommandCoordinator.Instance;
            if (coordinator != null)
            {
                // CRITICAL: Move commands should ALWAYS work - they have absolute priority
                // Use allowInterrupt=true for move commands so they can interrupt busy companions
                bool isMoveCommand = target.Value.type == CommandType.MoveToPosition;
                
                var selectedCompanion = coordinator.SelectBestCompanionForCommand(
                    player,
                    target.Value.targetObj,
                    target.Value.position,
                    allowInterrupt: isMoveCommand); // Move commands can interrupt
                
                if (selectedCompanion != null)
                {
                    IssueCommand(selectedCompanion, target.Value);
                }
                else
                {
                    // Check if target is already assigned
                    if (target.Value.targetObj != null && 
                        coordinator.IsTargetAssigned(target.Value.targetObj, out var assignedCompanion))
                    {
                        ShowMessage($"{assignedCompanion?.GetDisplayName()} is already handling that");
                    }
                    else
                    {
                        ShowMessage("No available companion for this task");
                    }
                }
            }
            else
            {
                // Fallback: Issue command to first companion if coordinator unavailable
                if (companions.Count > 0)
                {
                    IssueCommand(companions[0], target.Value);
                }
            }
        }

        /// <summary>
        /// Processes the NPC placement keybind (Shift+P).
        /// Allows admins to station companions as NPCs via placement mode.
        /// </summary>
        private void ProcessNpcPlacementInput()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            // Check if admin
            bool isAdmin = ZNet.instance?.IsServer() == true ||
                FiresCore.Bridge.NpcConfigBridge.IsAdmin();

            if (!isAdmin)
            {
                ShowMessage("Admin privileges required for NPC placement");
                return;
            }

            // Get the closest owned companion
            var companions = GetOwnedCompanions(player);
            if (companions.Count == 0)
            {
                ShowMessage("No companions to place");
                return;
            }

            // Find the closest companion
            CompanionController closest = null;
            float closestDist = float.MaxValue;
            foreach (var companion in companions)
            {
                float dist = Vector3.Distance(player.transform.position, companion.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = companion;
                }
            }

            if (closest == null) return;

            // Get or add the NPC module
            var npcModule = closest.GetComponent<CompanionNpcModule>();
            if (npcModule == null)
            {
                npcModule = closest.gameObject.AddComponent<CompanionNpcModule>();
            }

            // Start placement mode
            npcModule.StartHammerPlacement();
        }

        /// <summary>
        /// Processes the NPC unstation keybind (Shift+U).
        /// Allows admins to return a stationed NPC to normal companion mode.
        /// </summary>
        private void ProcessNpcUnstationInput()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            // Check if admin
            bool isAdmin = ZNet.instance?.IsServer() == true ||
                FiresCore.Bridge.NpcConfigBridge.IsAdmin();

            if (!isAdmin)
            {
                ShowMessage("Admin privileges required");
                return;
            }

            // Find the NPC we're looking at via crosshair raycast
            var npcModule = RaycastForStationedNpc();
            if (npcModule == null)
            {
                ShowMessage("Look at a stationed NPC");
                return;
            }

            npcModule.Unstation();
        }

        /// <summary>
        /// Processes the toggle idle keybind (Shift+I).
        /// Allows admins to toggle idle wandering for stationed NPCs.
        /// </summary>
        private void ProcessNpcToggleIdleInput()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            // Check if admin
            bool isAdmin = ZNet.instance?.IsServer() == true ||
                FiresCore.Bridge.NpcConfigBridge.IsAdmin();

            if (!isAdmin)
            {
                ShowMessage("Admin privileges required");
                return;
            }

            // Find the NPC we're looking at via crosshair raycast
            var npcModule = RaycastForStationedNpc();
            if (npcModule == null)
            {
                ShowMessage("Look at a stationed NPC");
                return;
            }

            npcModule.ToggleIdleWandering();
        }

        /// <summary>
        /// Processes Alt+Shift+MMB - commands a companion to Stay at cursor position,
        /// or commands a stayed companion under cursor to Follow.
        /// </summary>
        private void ProcessStayFollowCommand()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            
            // Notify patches that ping was used
            CompanionPatches.NotifyPingUsed();
            
            Camera cam = Camera.main;
            if (cam == null) return;
            
            Ray ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
            RaycastHit hit;
            
            if (!Physics.Raycast(ray, out hit, maxTargetDistance, _targetLayerMask))
            {
                ShowMessage("No valid target for Stay/Follow command");
                return;
            }
            
            // Check if we hit a companion (to toggle Stay/Follow)
            var hitCompanion = hit.collider.GetComponent<CompanionController>() ?? 
                               hit.collider.GetComponentInParent<CompanionController>();
            
            if (hitCompanion != null && hitCompanion.isTamed && hitCompanion.IsOwner(player))
            {
                // Skip if this is a stationed NPC
                var npcModule = hitCompanion.GetComponent<CompanionNpcModule>();
                if (npcModule != null && npcModule.IsStationedAsNpc)
                {
                    ShowMessage($"{hitCompanion.GetDisplayName()} is stationed as NPC - use Shift+U to unstation");
                    return;
                }
                
                // CRITICAL: Use ShouldBeFollowing instead of IsFollowing
                // IsFollowing checks if there's an active follow target, which can be null during combat
                // ShouldBeFollowing is the persistent flag that indicates the companion's intended state
                if (!hitCompanion.ShouldBeFollowing)
                {
                    // Companion is stayed/not following - command them to follow
                    Debug.Log($"[CompanionCommandSystem] Alt+Shift+MMB on stayed companion {hitCompanion.companionName} - commanding FOLLOW");
                    CommandCompanionFollow(hitCompanion);
                    return;
                }
                else
                {
                    // Companion is following - command them to stay at current position
                    Debug.Log($"[CompanionCommandSystem] Alt+Shift+MMB on following companion {hitCompanion.companionName} - commanding STAY");
                    CommandCompanionStay(hitCompanion, hitCompanion.transform.position);
                    return;
                }
            }
            
            // Hit ground or object - command following companion to stay at that position
            Vector3 stayPosition = hit.point;
            
            // Get all following companions
            var followingCompanions = GetOwnedCompanions(player);
            if (followingCompanions.Count == 0)
            {
                ShowMessage("No following companions to command");
                return;
            }
            
            // Command the closest following companion to stay at cursor position
            CompanionController closestCompanion = null;
            float closestDist = float.MaxValue;
            
            foreach (var companion in followingCompanions)
            {
                float dist = Vector3.Distance(player.transform.position, companion.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestCompanion = companion;
                }
            }
            
            if (closestCompanion != null)
            {
                CommandCompanionStay(closestCompanion, stayPosition);
            }
        }
        
        /// <summary>
        /// Commands a companion to stay at the specified position.
        ///
        /// CRITICAL: routes through <see cref="CompanionController.CommandStay(Vector3)"/>
        /// (the authoritative path) so the persistent follow intent and
        /// vault/ZDO state are written. Earlier this method duplicated the
        /// stay logic locally and missed <c>SetPersistentFollowIntent</c> /
        /// <c>SaveFollowStateToVault</c>, which left <c>CompanionVault.IsFollowing</c>
        /// stale at <c>true</c>; on death the respawn pipeline read the
        /// stale value and put the respawned companion back into Follow
        /// mode, overriding the owner's Stay command across the death/
        /// respawn cycle.
        /// </summary>
        private void CommandCompanionStay(CompanionController companion, Vector3 position)
        {
            if (companion == null) return;

            // Cancel any active commands
            CancelCommand(companion);

            // Force stop all behaviors
            ForceStopAllBehaviors(companion);

            // Authoritative stay path ï¿½ sets runtime flag, stay anchor, home
            // positions on both movement systems, persistent follow intent
            // (vault + ZDO), and the roster snapshot.
            companion.CommandStay(position);

            // Ping marker for the player.
            ShowPingMarker(position, CommandType.MoveToPosition);

            // Show the same status message we used to (CommandStay shows its
            // own MessageHud.Center already, this is the smaller corner one).
            ShowMessage($"{companion.GetDisplayName()}: Staying at position");

            Debug.Log($"[CompanionCommandSystem] {companion.companionName} commanded to STAY at {position}");
        }
        
        /// <summary>
        /// Commands a companion to follow the player.
        /// </summary>
        private void CommandCompanionFollow(CompanionController companion)
        {
            if (companion == null) return;
            
            // Cancel any active commands
            CancelCommand(companion);
            
            // Force stop all behaviors
            ForceStopAllBehaviors(companion);
            
            // Clear stay mode via CompanionAI - this also sets _shouldFollow = true
            var companionAI = companion.GetCompanionAI();
            if (companionAI != null)
            {
                companionAI.ClearStayPosition();
                
                // Set the follow target to the player
                var player = Player.m_localPlayer;
                if (player != null)
                {
                    companionAI.SetFollowTarget(player.gameObject);
                }
            }
            
            // CRITICAL: Clear home position on BOTH movement systems
            // CompanionCombatMovement - must be cleared first (IdleBehavior syncs from it)
            var combatMovement = companion.GetComponent<CompanionCombatMovement>();
            if (combatMovement != null)
            {
                combatMovement.ClearHomePosition();
            }
            
            // Clear home position in idle behavior
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            if (idleBehavior != null)
            {
                idleBehavior.ClearHomePosition();
            }
            
            // Use chat helper for feedback
            CompanionChatHelper.QuickMessages.FollowingOwner(companion);
            ShowMessage($"{companion.GetDisplayName()}: Following you");

            Debug.Log($"[CompanionCommandSystem] {companion.companionName} commanded to FOLLOW");

            // ROSTER MIRROR (Phase 3): persistent intent = Following.
            try
            {
                var ownerPlayer = companion.GetOwner();
                if (ownerPlayer != null) CompanionRosterWriter.OnFollowCommand(ownerPlayer, companion);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionCommandSystem] Roster write failed for Follow (non-fatal): {ex.Message}");
            }
        }
        
        /// <summary>
        /// Raycasts from the crosshair to find a stationed NPC.
        /// </summary>
        private CompanionNpcModule RaycastForStationedNpc()
        {
            Camera cam = Camera.main;
            if (cam == null) return null;

            Ray ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
            RaycastHit hit;

            // Use a layer mask that includes characters
            int layerMask = LayerMask.GetMask("character", "Default", "piece");
            if (layerMask == 0) layerMask = ~0;

            if (Physics.Raycast(ray, out hit, 10f, layerMask))
            {
                // Check hit object and parents for CompanionNpcModule
                var npcModule = hit.collider.GetComponent<CompanionNpcModule>();
                if (npcModule == null)
                    npcModule = hit.collider.GetComponentInParent<CompanionNpcModule>();

                if (npcModule != null && npcModule.IsStationedAsNpc)
                {
                    return npcModule;
                }
            }

            return null;
        }

        private List<CompanionController> GetOwnedCompanions(Player player)
        {
            var result = new List<CompanionController>();

            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null) continue;
                if (!companion.isTamed) continue;
                if (!companion.IsOwner(player)) continue;
                
                // Skip companions stationed as NPCs (they have their own interaction system)
                var npcModule = companion.GetComponent<NpcMode.CompanionNpcModule>();
                if (npcModule != null && npcModule.IsStationedAsNpc) continue;
                
                // CRITICAL: Only companions that SHOULD BE following respond to ping commands
                // Use ShouldBeFollowing (persistent flag) instead of IsFollowing (can be null during combat)
                // Stayed companions should NOT respond to ping commands - they are intentionally stationary
                if (!companion.ShouldBeFollowing)
                {
                    continue;
                }
                
                result.Add(companion);
            }

            return result;
        }

        #endregion

        #region Raycasting

        private (CommandType type, Vector3 position, GameObject targetObj, Character targetChar)? RaycastForTarget()
        {
            Camera cam = Camera.main;
            if (cam == null) return null;

            Ray ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
            RaycastHit hit;

            if (!Physics.Raycast(ray, out hit, maxTargetDistance, _targetLayerMask))
            {
                return null;
            }

            GameObject hitObj = hit.collider.gameObject;
            Vector3 hitPoint = hit.point;

            // Check what we hit and determine command type

            // 1. Check for Character (enemy, creature, NPC)
            var character = hitObj.GetComponent<Character>() ?? hitObj.GetComponentInParent<Character>();
            if (character != null && !character.IsTamed() && !character.IsPlayer())
            {
                return (CommandType.AttackTarget, character.transform.position, character.gameObject, character);
            }

            // 2. Check for Chair/Bench/Stool
            var chair = hitObj.GetComponent<Chair>() ?? hitObj.GetComponentInParent<Chair>();
            if (chair != null)
            {
                Vector3 chairPos = chair.m_attachPoint != null ? chair.m_attachPoint.position : chair.transform.position;
                return (CommandType.SitOnChair, chairPos, chair.gameObject, null);
            }

            // 3. Check for Archery Target (ArmorStand with specific name or custom component)
            if (IsArcheryTarget(hitObj))
            {
                return (CommandType.TrainArchery, hitObj.transform.position, hitObj, null);
            }

            // 4. Check for Smelter/Kiln (processing stations)
            var smelter = hitObj.GetComponent<Smelter>() ?? hitObj.GetComponentInParent<Smelter>();
            if (smelter != null)
            {
                return (CommandType.OperateSmelter, smelter.transform.position, smelter.gameObject, null);
            }

            // 5. CookingStation â†’ cook food (separate from fire-tending; the
            //    cooking behavior has its own raw-food / cooked-food loop)
            var cookingStation = hitObj.GetComponent<CookingStation>() ?? hitObj.GetComponentInParent<CookingStation>();
            if (cookingStation != null)
            {
                return (CommandType.CookFood, cookingStation.transform.position, cookingStation.gameObject, null);
            }

            // 5b. Fireplace (no food slots) â†’ tend fire / fuel it
            var fireplace = hitObj.GetComponent<Fireplace>() ?? hitObj.GetComponentInParent<Fireplace>();
            if (fireplace != null)
            {
                return (CommandType.TendFire, fireplace.transform.position, fireplace.gameObject, null);
            }

            // 6. Check for Crafting Station (workbench, forge, etc.)
            var craftingStation = hitObj.GetComponent<CraftingStation>() ?? hitObj.GetComponentInParent<CraftingStation>();
            if (craftingStation != null)
            {
                return (CommandType.UseWorkstation, craftingStation.transform.position, craftingStation.gameObject, null);
            }

            // 6.3. Beehive â†’ harvest (farming behavior)
            var beehive = hitObj.GetComponent<Beehive>() ?? hitObj.GetComponentInParent<Beehive>();
            if (beehive != null)
            {
                return (CommandType.Farm, beehive.transform.position, beehive.gameObject, null);
            }

            // 6.5. Check for Container/Chest (for depositing items)
            var container = hitObj.GetComponent<Container>() ?? hitObj.GetComponentInParent<Container>();
            if (container != null)
            {
                // Skip if it's a ship container (m_wagon check) or other special containers
                var nview = container.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    return (CommandType.DepositToChest, container.transform.position, container.gameObject, null);
                }
            }

            // 6.7. Plant / cultivated soil â†’ farming. Plant component covers
            //      grown crops and saplings; a Pickable flagged as a crop is
            //      handled by the GatherResource path below if it's not a Plant.
            var plantComp = hitObj.GetComponent<Plant>() ?? hitObj.GetComponentInParent<Plant>();
            if (plantComp != null)
            {
                return (CommandType.Farm, plantComp.transform.position, plantComp.gameObject, null);
            }
            if (IsCultivatedSoil(hitObj))
            {
                return (CommandType.Farm, hitPoint, hitObj, null);
            }

            // 7. Check for gatherable resources (trees, rocks, ore, pickable crops)
            if (IsGatherableResource(hitObj))
            {
                // A Pickable that names like a crop ("carrot", "turnip", "barley",
                // "flax", etc.) routes to Farm so the FarmingBehavior gets to
                // walk the harvest â†’ deposit loop instead of the generic gather.
                if (IsCropPickable(hitObj))
                {
                    return (CommandType.Farm, hitObj.transform.position, hitObj, null);
                }
                return (CommandType.GatherResource, hitObj.transform.position, hitObj, null);
            }

            // 7.5. Damaged building piece â†’ repair (only if WearNTear is below
            //      the repair threshold; an undamaged piece falls through to
            //      MoveToPosition like before)
            var wnt = hitObj.GetComponent<WearNTear>() ?? hitObj.GetComponentInParent<WearNTear>();
            if (wnt != null && wnt.GetHealthPercentage() < 0.95f)
            {
                return (CommandType.RepairBuilding, wnt.transform.position, wnt.gameObject, null);
            }

            // 7.7. Water at this position â†’ fishing. Water in Valheim has no
            //      collider, so Physics.Raycast hits the underwater terrain.
            //      We sample the terrain height at the hit's X,Z and check
            //      whether sea level (y=30) is at least 0.5m above it.
            //      Returned position is snapped to sea level so the fishing
            //      behavior treats it as a water-surface point.
            if (TryResolveWaterSurface(hitPoint, out Vector3 waterSurfacePoint))
            {
                return (CommandType.Fish, waterSurfacePoint, null, null);
            }

            // 8. Check for Destructible (can be attacked) - but not resources
            var destructible = hitObj.GetComponent<IDestructible>();
            if (destructible != null && hitObj.GetComponent<Character>() == null && !IsGatherableResource(hitObj))
            {
                // It's a destructible object - treat as attack target
                return (CommandType.AttackTarget, hitObj.transform.position, hitObj, null);
            }

            // 9. Check for Piece (built structure) - could expand for specific interactions
            var piece = hitObj.GetComponent<Piece>() ?? hitObj.GetComponentInParent<Piece>();
            if (piece != null)
            {
                // For now, just move to it
                return (CommandType.MoveToPosition, hitPoint, hitObj, null);
            }

            // Default: Move to ground position.
            return (CommandType.MoveToPosition, hitPoint, null, null);
        }

        /// <summary>
        /// True if the object is a Pickable that names like a player-grown
        /// crop. Used to route player Shift+MMB on a harvestable carrot or
        /// turnip into the Farm command (which deposits afterward) rather
        /// than the generic GatherResource path.
        /// </summary>
        private bool IsCropPickable(GameObject obj)
        {
            var pickable = obj.GetComponent<Pickable>() ?? obj.GetComponentInParent<Pickable>();
            if (pickable == null) return false;

            string n = (pickable.gameObject.name ?? "").ToLowerInvariant();
            // Common crop name fragments. Berries / mushrooms are wild gather,
            // not farming; carrots / turnips / onions / barley / flax are.
            return n.Contains("carrot")
                || n.Contains("turnip")
                || n.Contains("onion")
                || n.Contains("barley")
                || n.Contains("flax")
                || n.Contains("seedcarrot")
                || n.Contains("seedturnip")
                || n.Contains("seedonion");
        }

        /// <summary>
        /// True if the object looks like a cultivated-soil tile or a freshly
        /// planted seed marker. Recognised by name fragments because Valheim
        /// doesn't expose a clean component for "this terrain has been
        /// cultivated".
        /// </summary>
        private bool IsCultivatedSoil(GameObject obj)
        {
            string n = (obj.name ?? "").ToLowerInvariant();
            return n.Contains("cultivat");
        }

        /// <summary>
        /// Returns true if the ray-cast position is over a water column â€”
        /// i.e., the terrain at that X,Z is at least 0.5m below Valheim's
        /// sea level (y=30). When true, <paramref name="waterSurface"/> is
        /// the point snapped to sea level so the fishing behavior receives
        /// a real water-surface coordinate instead of an underwater hit.
        /// Mirrors <c>FishingBehavior.IsWaterAt</c>'s sampling logic.
        /// </summary>
        private bool TryResolveWaterSurface(Vector3 hitPoint, out Vector3 waterSurface)
        {
            const float SEA_LEVEL = 30f;
            const float MIN_DEPTH = 0.5f;
            waterSurface = hitPoint;

            if (ZoneSystem.instance == null) return false;
            ZoneSystem.instance.GetSolidHeight(hitPoint, out float groundY);
            if (SEA_LEVEL - groundY < MIN_DEPTH) return false;

            // Snap to the water surface so FishingBehavior.FindShoreNearWaterPoint
            // can sweep outward and locate dry land near y=31.
            waterSurface = new Vector3(hitPoint.x, SEA_LEVEL, hitPoint.z);
            return true;
        }

        private bool IsArcheryTarget(GameObject obj)
        {
            if (obj == null) return false;

            // PRIORITY 1: Check for actual ArcheryTarget component (Valheim's built-in target)
            var archeryTarget = obj.GetComponent<ArcheryTarget>() ?? obj.GetComponentInParent<ArcheryTarget>();
            if (archeryTarget != null)
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionCommandSystem] Found ArcheryTarget component on {obj.name}");
                return true;
            }

            string name = obj.name.ToLowerInvariant();

            // Check for common archery target names
            if (name.Contains("target") && (name.Contains("archery") || name.Contains("arrow") || name.Contains("practice")))
                return true;

            // Check for ArmorStand being used as target
            var armorStand = obj.GetComponent<ArmorStand>() ?? obj.GetComponentInParent<ArmorStand>();
            if (armorStand != null && name.Contains("target"))
                return true;

            // Check parent names too
            Transform parent = obj.transform.parent;
            while (parent != null)
            {
                // Also check parents for ArcheryTarget component
                archeryTarget = parent.GetComponent<ArcheryTarget>();
                if (archeryTarget != null)
                {
                    if (VerboseLogging)
                        Debug.Log($"[CompanionCommandSystem] Found ArcheryTarget component on parent {parent.name}");
                    return true;
                }
                
                string parentName = parent.name.ToLowerInvariant();
                if (parentName.Contains("target") && (parentName.Contains("archery") || parentName.Contains("arrow")))
                    return true;
                parent = parent.parent;
            }

            return false;
        }

        /// <summary>
        /// Checks if the object is a gatherable resource (tree, rock, ore deposit, or pickable).
        /// </summary>
        private bool IsGatherableResource(GameObject obj)
        {
            if (obj == null) return false;

            // Check for pickable items (berries, mushrooms, flax, etc.)
            var pickable = obj.GetComponent<Pickable>() ?? obj.GetComponentInParent<Pickable>();
            if (pickable != null) return true;

            var pickableItem = obj.GetComponent<PickableItem>() ?? obj.GetComponentInParent<PickableItem>();
            if (pickableItem != null) return true;

            // Check for tree components
            var treeBase = obj.GetComponent<TreeBase>() ?? obj.GetComponentInParent<TreeBase>();
            if (treeBase != null) return true;

            var treeLog = obj.GetComponent<TreeLog>() ?? obj.GetComponentInParent<TreeLog>();
            if (treeLog != null) return true;

            // Check for rock/ore components
            var mineRock = obj.GetComponent<MineRock>() ?? obj.GetComponentInParent<MineRock>();
            if (mineRock != null) return true;

            var mineRock5 = obj.GetComponent<MineRock5>() ?? obj.GetComponentInParent<MineRock5>();
            if (mineRock5 != null) return true;

            // Check by name for other resources
            string name = obj.name.ToLowerInvariant();
            if (name.Contains("tree") || name.Contains("beech") || name.Contains("birch") ||
                name.Contains("oak") || name.Contains("pine") || name.Contains("fir") ||
                name.Contains("rock") || name.Contains("stone") || name.Contains("ore") ||
                name.Contains("copper") || name.Contains("tin") || name.Contains("iron") ||
                name.Contains("silver") || name.Contains("obsidian"))
            {
                // Must also be destructible
                var destructible = obj.GetComponent<IDestructible>();
                return destructible != null;
            }

            return false;
        }

        #endregion

        #region Command Issuing

        private void IssueCommand(CompanionController companion, (CommandType type, Vector3 position, GameObject targetObj, Character targetChar) target)
        {
            if (companion == null) return;

            // Cancel any existing command
            CancelCommand(companion);
            
            // CRITICAL: Force stop ALL current behaviors before issuing new command
            // This ensures the command takes absolute priority
            ForceStopAllBehaviors(companion);
            
            // CRITICAL: Register the command with the StateController so IsPlayerCommandActive returns true
            // This is what blocks idle behaviors from interrupting the command
            var stateController = companion.GetComponent<CompanionStateController>();
            if (stateController != null)
            {
                // Map our CommandType to StateController.CommandType
                var stateCommandType = MapToStateCommandType(target.type);
                stateController.StartCommand(
                    stateCommandType,
                    target.position,
                    target.targetObj,
                    target.targetChar,
                    timeout: 120f, // 2 minutes for complex commands
                    onComplete: () => CompleteCommand(companion, "Success"),
                    onFailed: (reason) => Debug.Log($"[CompanionCommandSystem] Command failed for {companion.companionName}: {reason}")
                );
            }

            // Create new command (also tracked locally for backward compatibility)
            float duration = target.type == CommandType.AttackTarget ? priorityTargetDuration : 120f;
            var command = new ActiveCommand(target.type, target.position, target.targetObj, target.targetChar, duration);
            _activeCommands[companion] = command;

            // Execute the command
            ExecuteCommand(companion, command);

            // Show feedback
            ShowPingMarker(target.position, target.type);
            ShowCommandFeedback(companion, target.type, target.targetObj);

            if (VerboseLogging)
            {
                Debug.Log($"[CompanionCommandSystem] Issued {target.type} command to {companion.companionName} at {target.position}");
            }
        }
        
        /// <summary>
        /// Maps CompanionCommandSystem.CommandType to CompanionStateController.CommandType.
        /// </summary>
        private CompanionStateController.CommandType MapToStateCommandType(CommandType type)
        {
            return type switch
            {
                CommandType.AttackTarget => CompanionStateController.CommandType.Attack,
                CommandType.MoveToPosition => CompanionStateController.CommandType.Move,
                CommandType.SitOnChair => CompanionStateController.CommandType.Sit,
                CommandType.TrainArchery => CompanionStateController.CommandType.Train,
                CommandType.GatherResource => CompanionStateController.CommandType.Gather,
                CommandType.InteractWithObject => CompanionStateController.CommandType.Interact,
                CommandType.OperateSmelter => CompanionStateController.CommandType.SubBehavior,
                CommandType.TendFire => CompanionStateController.CommandType.SubBehavior,
                CommandType.UseWorkstation => CompanionStateController.CommandType.SubBehavior,
                _ => CompanionStateController.CommandType.None
            };
        }
        
        /// <summary>
        /// Forces the companion to stop ALL current behaviors immediately.
        /// This ensures player commands have absolute priority over AI behaviors.
        /// </summary>
        private void ForceStopAllBehaviors(CompanionController companion)
        {
            if (companion == null) return;
            
            try
            {
                Debug.Log($"[COMMAND] ForceStopAllBehaviors for {companion.companionName}");
                
                // 0. FIRST: Acquire command movement priority via state controller
                var stateController = companion.GetComponent<CompanionStateController>();
                if (stateController != null)
                {
                    // Force acquire command priority - this overrides everything
                    stateController.TryAcquireMovementPriority(
                        CompanionStateController.MovementPriority.Command, 
                        "PlayerCommand", 
                        120f); // 2 minutes default
                    
                    // Force reset any frozen/emote states
                    if (stateController.IsInFrozenState || stateController.IsAnimationBlocking)
                    {
                        stateController.ForceReset();
                    }
                }
                
                // 1. Cancel all idle behaviors (sitting, training, wandering)
                var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
                idleBehavior?.OnCombatStarted();
                
                // 2. Force detach from any attachment point (chair, bed, etc.)
                var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
                interactionBehavior?.ForceDetach();
                
                // 3. Release all interactable occupancies
                var character = companion.GetCharacter();
                if (character != null)
                {
                    IdleBehaviors.InteractableOccupancyManager.ReleaseAllForOccupant(character);
                    character.SetMoveDir(Vector3.zero);
                }
                
                // 4. Clear any current AI targets and alert state
                var companionAI = companion.GetCompanionAI();
                if (companionAI != null)
                {
                    companionAI.ClearIdleDestination();
                    companionAI.ClearCommandDestination();
                    // Clear current target so command target takes priority
                    companionAI.ClearForceTarget();
                }
                
                // 5. Set command priority on combat movement
                var combatMovement = companion.GetComponent<CompanionCombatMovement>();
                if (combatMovement != null)
                {
                    combatMovement.ClearPriorityTarget();
                    combatMovement.ClearMoveDestination();
                    // Set command priority for extended duration
                    combatMovement.SetCommandPriorityDuration(120f);
                }
                
                // 6. Clear any blocking/dodging in progress
                var combat = companion.GetComponent<CompanionCombat>();
                if (combat != null)
                {
                    combat.RequestStopBlocking();
                }
                
                // 7. Zero rigidbody velocity (only if not kinematic - Unity 6 warning fix)
                var rigidbody = companion.GetComponent<Rigidbody>();
                if (rigidbody != null && !rigidbody.isKinematic)
                {
                    rigidbody.linearVelocity = Vector3.zero;
                    rigidbody.angularVelocity = Vector3.zero;
                }
                
                Debug.Log($"[COMMAND] ForceStopAllBehaviors COMPLETE for {companion.companionName}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionCommandSystem] Error in ForceStopAllBehaviors: {ex.Message}");
            }
        }

        private void ExecuteCommand(CompanionController companion, ActiveCommand command)
        {
            switch (command.Type)
            {
                case CommandType.AttackTarget:
                    ExecuteAttackCommand(companion, command);
                    break;

                case CommandType.MoveToPosition:
                    ExecuteMoveCommand(companion, command);
                    break;

                case CommandType.SitOnChair:
                    ExecuteSitCommand(companion, command);
                    break;

                case CommandType.TrainArchery:
                    ExecuteTrainCommand(companion, command);
                    break;

                case CommandType.InteractWithObject:
                    ExecuteInteractCommand(companion, command);
                    break;

                case CommandType.GatherResource:
                    ExecuteGatherCommand(companion, command);
                    break;

                case CommandType.OperateSmelter:
                    ExecuteSmelterCommand(companion, command);
                    break;

                case CommandType.TendFire:
                    ExecuteFireTendCommand(companion, command);
                    break;

                case CommandType.UseWorkstation:
                    ExecuteWorkstationCommand(companion, command);
                    break;
                    
                case CommandType.DepositToChest:
                    ExecuteDepositToChestCommand(companion, command);
                    break;

                case CommandType.CookFood:
                    ExecuteCookCommand(companion, command);
                    break;

                case CommandType.RepairBuilding:
                    ExecuteRepairCommand(companion, command);
                    break;

                case CommandType.Fish:
                    ExecuteFishCommand(companion, command);
                    break;

                case CommandType.Farm:
                    ExecuteFarmCommand(companion, command);
                    break;
            }
        }

        private void ExecuteAttackCommand(CompanionController companion, ActiveCommand command)
        {
            // Get combat movement component
            var combatMovement = companion.GetComponent<CompanionCombatMovement>();
            if (combatMovement == null) return;

            // Cancel any idle behaviors
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            // Force detach if sitting
            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();

            // Set priority target if it's a character
            if (command.TargetCharacter != null)
            {
                combatMovement.SetPriorityTarget(command.TargetCharacter, priorityTargetDuration);
                
                // Use CompanionAI.ForceTarget for proper target setting
                var companionAI = companion.GetComponent<AI.CompanionAI>();
                if (companionAI != null)
                {
                    companionAI.ForceTarget(command.TargetCharacter);
                    Debug.Log($"[CompanionCommandSystem] {companion.companionName} targeting {command.TargetCharacter.m_name} via CompanionAI");
                }
            }
            else if (command.TargetObject != null)
            {
                // For destructibles, move to position and attack
                combatMovement.SetMoveDestination(command.TargetPosition);
            }
        }

        private void ExecuteMoveCommand(CompanionController companion, ActiveCommand command)
        {
            // Get the state controller for centralized command management
            var stateController = companion.GetComponent<CompanionStateController>();
            if (stateController == null)
            {
                ShowMessage($"{companion.companionName}: Cannot execute command (no state controller)");
                return;
            }

            // Cancel any idle behaviors first
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            // Force detach if sitting
            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();

            // Check if there are dropped items at the destination - if so, pick them up too
            bool hasLootNearby = CheckForLootAtPosition(command.TargetPosition, out int lootCount);

            // NOTE: The command was already started by IssueCommand ? StartCommand
            // a few lines up the call stack.  Re-issuing it here used to call
            // StartCommand a SECOND time (with a different 60 s timeout, which then
            // got stomped by SetMoveDestination's third call with 120 s) ï¿½ every
            // re-issue tore down + rebuilt PlayerCommand authority and was the
            // root cause of the "[COMMAND] cancelling existing command" log
            // tornado.  We just attach the on-arrival loot pickup callback to the
            // existing command instead.
            if (hasLootNearby)
            {
                stateController.RegisterCommandCompletionHook(() =>
                {
                    StartCoroutine(PickupLootAtDestination(companion, command.TargetPosition));
                });
            }

            // Set movement destination via CompanionCombatMovement.
            // Pass skipCommandOverride: true so SetMoveDestination's internal
            // StartCommand call is skipped ï¿½ the command is ALREADY active from
            // IssueCommand and we don't want a third teardown/rebuild.
            var combatMovement = companion.GetComponent<CompanionCombatMovement>();
            if (combatMovement != null)
            {
                combatMovement.SetMoveDestination(command.TargetPosition, useWalk: false, skipCommandOverride: true);
            }

            string lootMsg = hasLootNearby ? $" ({lootCount} items nearby)" : "";
            Debug.Log($"[CompanionCommandSystem] {companion.companionName} moving to {command.TargetPosition}{lootMsg}");

            // Start coroutine to monitor arrival
            StartCoroutine(MonitorMoveCommandWithStateController(companion, command, stateController));
        }
        
        /// <summary>
        /// Checks if there are dropped items near a position.
        /// </summary>
        private bool CheckForLootAtPosition(Vector3 position, out int count)
        {
            count = 0;
            float pickupRadius = 3f;
            
            Collider[] colliders = Physics.OverlapSphere(position, pickupRadius);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                var itemDrop = col.GetComponent<ItemDrop>();
                if (itemDrop != null && itemDrop.CanPickup())
                {
                    count++;
                }
            }
            
            return count > 0;
        }
        
        /// <summary>
        /// Picks up loot at the destination after arriving.
        /// </summary>
        private IEnumerator PickupLootAtDestination(CompanionController companion, Vector3 position)
        {
            var inventory = companion.GetComponent<CompanionInventory>();
            if (inventory == null) yield break;
            
            var storageInv = inventory.GetStorageInventory();
            if (storageInv == null) yield break;
            
            int itemsCollected = 0;
            float collectTime = 5f;
            float startTime = Time.time;
            float pickupRadius = 4f;
            
            while (Time.time - startTime < collectTime && companion != null)
            {
                Collider[] colliders = Physics.OverlapSphere(position, pickupRadius);
                bool foundItem = false;
                
                foreach (var col in colliders)
                {
                    if (col == null) continue;
                    
                    var itemDrop = col.GetComponent<ItemDrop>();
                    if (itemDrop == null || !itemDrop.CanPickup()) continue;
                    
                    foundItem = true;
                    
                    // Move toward item if needed
                    float dist = Vector3.Distance(companion.transform.position, itemDrop.transform.position);
                    if (dist > 1.5f)
                    {
                        var character = companion.GetCharacter();
                        if (character != null)
                        {
                            Vector3 dir = (itemDrop.transform.position - companion.transform.position).normalized;
                            dir.y = 0;
                            character.SetMoveDir(dir);
                            character.SetWalk(true);
                        }
                        yield return new WaitForSeconds(0.2f);
                        continue;
                    }
                    
                    // Pick up the item
                    var itemData = itemDrop.m_itemData;
                    if (itemData != null && storageInv.CanAddItem(itemData))
                    {
                        storageInv.AddItem(itemData.Clone());
                        itemsCollected++;

                        // Remove from world. ItemDrop carries a ZNetView, so this MUST
                        // route through ZNetScene.Destroy (via the helper) ï¿½ raw
                        // Object.Destroy leaves a stale m_instances entry that NREs in
                        // ZNetScene.RemoveObjects every tick afterwards.
                        CompanionNetworkHelper.Destroy(itemDrop.gameObject, disableFirst: false);
                    }

                    break; // Process one item per tick
                }

                if (!foundItem) break;
                
                yield return new WaitForSeconds(0.3f);
            }
            
            // Stop movement
            var charStop = companion.GetCharacter();
            if (charStop != null)
            {
                charStop.SetMoveDir(Vector3.zero);
                charStop.SetWalk(false);
            }
            
            if (itemsCollected > 0)
            {
                ShowMessage($"{companion.companionName} collected {itemsCollected} item{(itemsCollected > 1 ? "s" : "")}");
            }
        }
        
        /// <summary>
        /// Monitors a move command using the centralized state controller.
        /// </summary>
        private IEnumerator MonitorMoveCommandWithStateController(
            CompanionController companion, 
            ActiveCommand command,
            CompanionStateController stateController)
        {
            float arrivalThreshold = moveDestinationThreshold;
            float stuckCheckInterval = 2f;
            float lastStuckCheck = Time.time;
            Vector3 lastPosition = companion.transform.position;
            int stuckCount = 0;
            
            while (companion != null && stateController != null && stateController.HasActiveCommand)
            {
                // Check if arrived
                float dist = Vector3.Distance(companion.transform.position, command.TargetPosition);
                if (dist <= arrivalThreshold)
                {
                    stateController.CompleteCommand();
                    yield break;
                }
                
                // Check if stuck (not making progress)
                if (Time.time - lastStuckCheck >= stuckCheckInterval)
                {
                    float progressMade = Vector3.Distance(companion.transform.position, lastPosition);
                    if (progressMade < 0.5f)
                    {
                        stuckCount++;
                        if (stuckCount >= 3)
                        {
                            stateController.FailCommand("Path blocked");
                            yield break;
                        }
                    }
                    else
                    {
                        stuckCount = 0;
                    }
                    
                    lastPosition = companion.transform.position;
                    lastStuckCheck = Time.time;
                }
                
                yield return new WaitForSeconds(0.5f);
            }
        }

        private void ExecuteSitCommand(CompanionController companion, ActiveCommand command)
        {
            // Cancel combat/idle first
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();

            // Check distance to chair
            float dist = Vector3.Distance(companion.transform.position, command.TargetPosition);

            if (dist <= interactionDistance)
            {
                // Close enough, try to sit immediately
                TrySitOnTarget(companion, command);
            }
            else
            {
                // Need to move first
                var combatMovement = companion.GetComponent<CompanionCombatMovement>();
                combatMovement?.SetMoveDestination(command.TargetPosition);

                // Monitor and sit when close
                StartCoroutine(MonitorSitCommand(companion, command));
            }
        }

        private void ExecuteTrainCommand(CompanionController companion, ActiveCommand command)
        {
            // Cancel current activities
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();

            // Check distance to target
            float dist = Vector3.Distance(companion.transform.position, command.TargetPosition);

            if (dist <= interactionDistance * 2f)
            {
                // Close enough, start training
                TryStartTraining(companion, command);
            }
            else
            {
                // Need to move first
                var combatMovement = companion.GetComponent<CompanionCombatMovement>();
                combatMovement?.SetMoveDestination(command.TargetPosition);

                // Monitor and train when close
                StartCoroutine(MonitorTrainCommand(companion, command));
            }
        }

        private void ExecuteInteractCommand(CompanionController companion, ActiveCommand command)
        {
            // Generic interaction - just move to position for now
            ExecuteMoveCommand(companion, command);
        }

        private void ExecuteGatherCommand(CompanionController companion, ActiveCommand command)
        {
            // Cancel current activities
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();
            
            // Set command priority for the full duration of gathering
            var combatMovement = companion.GetComponent<CompanionCombatMovement>();
            if (combatMovement != null)
            {
                combatMovement.SetCommandPriorityDuration(120f); // 2 minutes for gathering
            }

            // Check if target is a tree (use WoodGatheringBehavior) or ore/rock (use ResourceGatheringBehavior)
            bool isTree = command.TargetObject != null && (
                command.TargetObject.GetComponent<TreeBase>() != null ||
                command.TargetObject.GetComponentInParent<TreeBase>() != null ||
                command.TargetObject.GetComponent<TreeLog>() != null ||
                command.TargetObject.GetComponentInParent<TreeLog>() != null);
            
            if (isTree)
            {
                // Use dedicated WoodGatheringBehavior for trees
                var woodBehavior = idleBehavior?.GetSubBehavior<WoodGatheringBehavior>();
                if (woodBehavior != null)
                {
                    woodBehavior.SetCommandedTarget(command.TargetObject);
                    
                    if (idleBehavior.TryStartSubBehavior<WoodGatheringBehavior>())
                    {
                        Debug.Log($"[COMMAND] {companion.companionName} started WoodGatheringBehavior");
                        ShowMessage($"{companion.companionName}: Chopping wood");
                        return;
                    }
                    else
                    {
                        Debug.LogWarning($"[COMMAND] {companion.companionName} WoodGatheringBehavior.CanStart() returned false");
                    }
                }
            }
            
            // Use ResourceGatheringBehavior for ores, stones, pickables, or as fallback
            var resourceBehavior = idleBehavior?.GetSubBehavior<ResourceGatheringBehavior>();
            if (resourceBehavior != null)
            {
                resourceBehavior.SetCommandedTarget(command.TargetObject);
                
                if (idleBehavior.TryStartSubBehavior<ResourceGatheringBehavior>())
                {
                    Debug.Log($"[COMMAND] {companion.companionName} started ResourceGatheringBehavior for {command.TargetObject?.name}");
                    ShowMessage($"{companion.companionName}: Gathering resources");
                    return;
                }
                else
                {
                    Debug.LogWarning($"[COMMAND] {companion.companionName} ResourceGatheringBehavior.CanStart() returned false");
                }
            }
            
            // Fallback: move to resource and attack it directly
            Debug.Log($"[CompanionCommandSystem] {companion.companionName} using fallback attack for resource gathering");
            
            if (combatMovement != null)
            {
                combatMovement.SetMoveDestination(command.TargetPosition);
            }
            
            StartCoroutine(MonitorResourceGatherCommand(companion, command));
        }
        
        /// <summary>
        /// Monitors resource gathering command and triggers attacks when close.
        /// Also handles loot pickup after resource is destroyed.
        /// </summary>
        private IEnumerator MonitorResourceGatherCommand(CompanionController companion, ActiveCommand command)
        {
            float startTime = Time.time;
            float timeout = 120f; // 2 minutes total
            float attackInterval = 1.5f;
            float lastAttackTime = 0f;
            Vector3 resourcePosition = command.TargetPosition;
            bool resourceDestroyed = false;
            
            while (companion != null && !command.IsComplete)
            {
                // Check timeout
                if (Time.time - startTime > timeout)
                {
                    CompleteCommand(companion, "Gathering timed out");
                    yield break;
                }
                
                // Check if resource still exists
                if (command.TargetObject == null && !resourceDestroyed)
                {
                    resourceDestroyed = true;
                    Debug.Log($"[CompanionCommandSystem] {companion.companionName} destroyed resource, collecting loot");
                    
                    // Collect dropped items
                    yield return StartCoroutine(CollectDroppedItems(companion, resourcePosition));
                    
                    CompleteCommand(companion, "Resource gathered");
                    yield break;
                }
                
                if (!resourceDestroyed && command.TargetObject != null)
                {
                    float dist = Vector3.Distance(companion.transform.position, command.TargetObject.transform.position);
                    
                    if (dist <= 2.5f)
                    {
                        // Close enough to attack
                        if (Time.time - lastAttackTime >= attackInterval)
                        {
                            // Attack the resource
                            var humanoid = companion.GetComponent<Humanoid>();
                            var character = companion.GetCharacter();
                            
                            if (humanoid != null && character != null)
                            {
                                // Face the target
                                Vector3 dir = (command.TargetObject.transform.position - companion.transform.position).normalized;
                                dir.y = 0;
                                if (dir.sqrMagnitude > 0.01f)
                                {
                                    companion.transform.rotation = Quaternion.LookRotation(dir);
                                }
                                
                                // Trigger attack animation
                                var zanim = companion.GetComponent<ZSyncAnimation>();
                                if (zanim != null)
                                {
                                    zanim.SetTrigger("swing_axe");
                                }
                                
                                // Get equipped weapon for damage calculation
                                var inventory = companion.GetComponent<CompanionInventory>();
                                var weapon = inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
                                
                                // Create hit data
                                HitData hitData = new HitData();
                                if (weapon != null)
                                {
                                    hitData.m_damage = weapon.GetDamage();
                                    hitData.m_toolTier = (short)weapon.m_shared.m_toolTier;
                                }
                                else
                                {
                                    // Unarmed fallback
                                    hitData.m_damage.m_blunt = 10f;
                                    hitData.m_toolTier = 0;
                                }
                                hitData.m_attacker = character.GetZDOID();
                                hitData.m_point = command.TargetObject.transform.position;
                                hitData.m_dir = dir;
                                
                                // Apply damage
                                var destructible = command.TargetObject.GetComponent<IDestructible>();
                                destructible?.Damage(hitData);
                            }
                            
                            lastAttackTime = Time.time;
                        }
                    }
                    else
                    {
                        // Keep moving toward target
                        var combatMovement = companion.GetComponent<CompanionCombatMovement>();
                        combatMovement?.SetMoveDestination(command.TargetObject.transform.position);
                    }
                }
                
                yield return new WaitForSeconds(0.2f);
            }
        }
        
        /// <summary>
        /// Collects dropped items near a position into companion's inventory.
        /// </summary>
        private IEnumerator CollectDroppedItems(CompanionController companion, Vector3 position)
        {
            float collectTime = 10f; // Spend up to 10 seconds collecting
            float startTime = Time.time;
            int itemsCollected = 0;
            
            var inventory = companion.GetComponent<CompanionInventory>();
            if (inventory == null) yield break;
            
            var storageInv = inventory.GetStorageInventory();
            if (storageInv == null) yield break;
            
            while (Time.time - startTime < collectTime)
            {
                // Find nearby item drops
                var colliders = Physics.OverlapSphere(position, 5f);
                bool foundItems = false;
                
                foreach (var col in colliders)
                {
                    if (col == null) continue;
                    
                    var itemDrop = col.GetComponent<ItemDrop>();
                    if (itemDrop == null || !itemDrop.CanPickup()) continue;
                    
                    foundItems = true;
                    
                    // Move toward item if too far
                    float dist = Vector3.Distance(companion.transform.position, itemDrop.transform.position);
                    if (dist > 1.5f)
                    {
                        var character = companion.GetCharacter();
                        if (character != null)
                        {
                            Vector3 dir = (itemDrop.transform.position - companion.transform.position).normalized;
                            dir.y = 0;
                            character.SetMoveDir(dir);
                            character.SetWalk(true);
                        }
                    }
                    else
                    {
                        // Pick up the item
                        var itemData = itemDrop.m_itemData;
                        if (itemData != null && storageInv.CanAddItem(itemData))
                        {
                            storageInv.AddItem(itemData.Clone());
                            itemsCollected++;

                            // Remove from world via ZNetScene-routed destroy. See the
                            // sibling pickup site (~line 1242) for the rationale: raw
                            // Object.Destroy on a ZNetView'd ItemDrop strands an entry
                            // in ZNetScene.m_instances that NREs in RemoveObjects.
                            CompanionNetworkHelper.Destroy(itemDrop.gameObject, disableFirst: false);
                        }
                    }

                    break; // Process one item per tick
                }
                
                if (!foundItems)
                {
                    // No more items nearby
                    break;
                }
                
                yield return new WaitForSeconds(0.3f);
            }
            
            // Stop movement
            var charStop = companion.GetCharacter();
            if (charStop != null)
            {
                charStop.SetMoveDir(Vector3.zero);
                charStop.SetWalk(false);
            }
            
            if (itemsCollected > 0)
            {
                ShowMessage($"{companion.companionName} collected {itemsCollected} items");
            }
        }

        private void ExecuteSmelterCommand(CompanionController companion, ActiveCommand command)
        {
            Debug.Log($"[COMMAND] ExecuteSmelterCommand called for {companion.companionName}");
            
            // Cancel current activities
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();

            // Find the smelter operator behavior
            var smelterBehavior = idleBehavior?.GetSubBehavior<SmelterOperatorBehavior>();
            Debug.Log($"[COMMAND] {companion.companionName} smelterBehavior={smelterBehavior != null}, idleBehavior={idleBehavior != null}");
            
            if (smelterBehavior != null)
            {
                smelterBehavior.SetCommandedTarget(command.TargetObject);
                Debug.Log($"[COMMAND] {companion.companionName} SetCommandedTarget, attempting TryStartSubBehavior...");
                
                if (idleBehavior.TryStartSubBehavior<SmelterOperatorBehavior>())
                {
                    Debug.Log($"[COMMAND] {companion.companionName} TryStartSubBehavior SUCCEEDED! IsInSubBehavior={idleBehavior.IsInSubBehavior}");
                    ShowMessage($"{companion.companionName}: Operating smelter");
                    
                    // DON'T mark as complete - the sub-behavior will run until done
                    // Command priority is set by StartAsCommand() in the sub-behavior
                    return;
                }
                else
                {
                    Debug.LogWarning($"[COMMAND] {companion.companionName} TryStartSubBehavior FAILED! Falling back to move command.");
                }
            }
            
            // Fallback: move to smelter
            Debug.Log($"[COMMAND] {companion.companionName} falling back to ExecuteMoveCommand");
            ExecuteMoveCommand(companion, command);
        }

        private void ExecuteFireTendCommand(CompanionController companion, ActiveCommand command)
        {
            Debug.Log($"[COMMAND] ExecuteFireTendCommand called for {companion.companionName}");
            
            // Cancel current activities
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();

            // CRITICAL: Try V2 behavior first (the active one), then fall back to V1
            var fireBehaviorV2 = idleBehavior?.GetSubBehavior<FireTendingBehaviorV2>();
            if (fireBehaviorV2 != null)
            {
                Debug.Log($"[COMMAND] {companion.companionName} found FireTendingBehaviorV2");
                fireBehaviorV2.SetCommandedTarget(command.TargetObject);
                
                if (idleBehavior.TryStartSubBehavior<FireTendingBehaviorV2>())
                {
                    Debug.Log($"[COMMAND] {companion.companionName} started FireTendingBehaviorV2 successfully");
                    ShowMessage($"{companion.companionName}: Tending fire");
                    return;
                }
                else
                {
                    Debug.LogWarning($"[COMMAND] {companion.companionName} FireTendingBehaviorV2.CanStart() returned false");
                }
            }
            
            // Try V1 behavior as fallback
            var fireBehavior = idleBehavior?.GetSubBehavior<FireTendingBehavior>();
            if (fireBehavior != null)
            {
                Debug.Log($"[COMMAND] {companion.companionName} trying fallback FireTendingBehavior (V1)");
                fireBehavior.SetCommandedTarget(command.TargetObject);
                
                if (idleBehavior.TryStartSubBehavior<FireTendingBehavior>())
                {
                    ShowMessage($"{companion.companionName}: Tending fire");
                    return;
                }
            }
            
            // Fallback: move to fire
            Debug.Log($"[COMMAND] {companion.companionName} fire tending behaviors unavailable, falling back to move command");
            ExecuteMoveCommand(companion, command);
        }

        private void ExecuteWorkstationCommand(CompanionController companion, ActiveCommand command)
        {
            // Cancel current activities
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();

            // Find the workstation behavior
            var workstationBehavior = idleBehavior?.GetSubBehavior<WorkstationInteractionBehavior>();
            if (workstationBehavior != null)
            {
                // Get the crafting station from the target object
                var station = command.TargetObject?.GetComponent<CraftingStation>();
                if (station != null)
                {
                    workstationBehavior.SetCommandedStation(station);
                    
                    if (idleBehavior.TryStartSubBehavior<WorkstationInteractionBehavior>())
                    {
                        ShowMessage($"{companion.companionName}: Using workstation");
                        
                        // DON'T mark as complete - the sub-behavior will run until done
                        return;
                    }
                }
            }
            
            // Fallback: move to workstation
            ExecuteMoveCommand(companion, command);
        }
        
        private void ExecuteDepositToChestCommand(CompanionController companion, ActiveCommand command)
        {
            // Cancel current activities
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();
            
            // Set command priority for the deposit operation
            var combatMovement = companion.GetComponent<CompanionCombatMovement>();
            if (combatMovement != null)
            {
                combatMovement.SetCommandPriorityDuration(60f); // 1 minute for deposit
            }

            // Find the chest deposit behavior
            var depositBehavior = idleBehavior?.GetSubBehavior<ChestDepositBehavior>();
            if (depositBehavior != null)
            {
                // Set the target chest - create a method for this
                depositBehavior.SetCommandedTarget(command.TargetObject);
                
                if (idleBehavior.TryStartSubBehavior<ChestDepositBehavior>())
                {
                    ShowMessage($"{companion.companionName}: Depositing items to chest");
                    return;
                }
            }
            
            // Fallback: manual deposit via coroutine
            Debug.Log($"[CompanionCommandSystem] {companion.companionName} using fallback deposit method");
            StartCoroutine(ManualDepositToChest(companion, command));
        }

        // â”€â”€ New behavior dispatchers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Each of these mirrors ExecuteSmelterCommand's pattern:
        //   1. Cancel current idle activity + force-detach interaction.
        //   2. Locate the matching behavior on the companion.
        //   3. Set the commanded target, try to start the sub-behavior.
        //   4. On failure, fall back to ExecuteMoveCommand so the companion
        //      at least walks to the target (player gets visible feedback).

        private void ExecuteCookCommand(CompanionController companion, ActiveCommand command)
        {
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();

            var cookBehavior = idleBehavior?.GetSubBehavior<CompanionCookingBehavior>();
            if (cookBehavior != null)
            {
                cookBehavior.SetCommandedTarget(command.TargetObject);
                if (idleBehavior.TryStartSubBehavior<CompanionCookingBehavior>())
                {
                    ShowMessage($"{companion.companionName}: Cooking");
                    return;
                }
            }

            Debug.Log($"[COMMAND] {companion.companionName} cooking unavailable, falling back to move");
            ExecuteMoveCommand(companion, command);
        }

        private void ExecuteRepairCommand(CompanionController companion, ActiveCommand command)
        {
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();

            var repairBehavior = idleBehavior?.GetSubBehavior<BuildingRepairBehavior>();
            if (repairBehavior != null)
            {
                repairBehavior.SetCommandedTarget(command.TargetObject);
                if (idleBehavior.TryStartSubBehavior<BuildingRepairBehavior>())
                {
                    ShowMessage($"{companion.companionName}: Repairing");
                    return;
                }
            }

            Debug.Log($"[COMMAND] {companion.companionName} repair unavailable, falling back to move");
            ExecuteMoveCommand(companion, command);
        }

        private void ExecuteFishCommand(CompanionController companion, ActiveCommand command)
        {
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();

            var fishBehavior = idleBehavior?.GetSubBehavior<FishingBehavior>();
            if (fishBehavior != null)
            {
                // Fishing takes a position, not a GameObject (water has no
                // collider component to set as target).
                fishBehavior.SetCommandedSpot(command.TargetPosition);
                if (idleBehavior.TryStartSubBehavior<FishingBehavior>())
                {
                    ShowMessage($"{companion.companionName}: Fishing");
                    return;
                }
            }

            Debug.Log($"[COMMAND] {companion.companionName} fishing unavailable, falling back to move");
            ExecuteMoveCommand(companion, command);
        }

        private void ExecuteFarmCommand(CompanionController companion, ActiveCommand command)
        {
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();

            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();

            var farmBehavior = idleBehavior?.GetSubBehavior<FarmingBehavior>();
            if (farmBehavior != null)
            {
                farmBehavior.SetCommandedTarget(command.TargetObject);
                if (idleBehavior.TryStartSubBehavior<FarmingBehavior>())
                {
                    ShowMessage($"{companion.companionName}: Farming");
                    return;
                }
            }

            Debug.Log($"[COMMAND] {companion.companionName} farming unavailable, falling back to move");
            ExecuteMoveCommand(companion, command);
        }

        /// <summary>
        /// Manual deposit to chest when behavior is not available.
        /// </summary>
        private IEnumerator ManualDepositToChest(CompanionController companion, ActiveCommand command)
        {
            // Move to chest first
            float startTime = Time.time;
            float timeout = 30f;
            
            var combatMovement = companion.GetComponent<CompanionCombatMovement>();
            combatMovement?.SetMoveDestination(command.TargetPosition);
            
            // Wait until we're close enough
            while (companion != null && Time.time - startTime < timeout)
            {
                float dist = Vector3.Distance(companion.transform.position, command.TargetPosition);
                if (dist < 2.5f) break;
                yield return new WaitForSeconds(0.3f);
            }
            
            // Stop movement
            combatMovement?.ClearMoveDestination();
            var character = companion.GetCharacter();
            character?.SetMoveDir(Vector3.zero);
            
            // Face the chest
            Vector3 dir = (command.TargetPosition - companion.transform.position).normalized;
            dir.y = 0;
            if (dir.sqrMagnitude > 0.01f)
            {
                companion.transform.rotation = Quaternion.LookRotation(dir);
            }
            
            yield return new WaitForSeconds(0.3f);
            
            // Get the container and inventory
            var container = command.TargetObject?.GetComponent<Container>();
            var inventory = companion.GetComponent<CompanionInventory>();
            
            if (container == null || inventory == null)
            {
                ShowMessage($"{companion.companionName}: Can't access chest");
                CompleteCommand(companion, "Chest not accessible");
                yield break;
            }
            
            var storageInv = inventory.GetStorageInventory();
            var chestInv = container.GetInventory();
            
            if (storageInv == null || chestInv == null)
            {
                ShowMessage($"{companion.companionName}: Can't access inventories");
                CompleteCommand(companion, "Inventory not accessible");
                yield break;
            }
            
            // Deposit items
            int deposited = 0;
            var itemsToDeposit = new List<ItemDrop.ItemData>(storageInv.GetAllItems());
            
            foreach (var item in itemsToDeposit)
            {
                if (item == null) continue;
                
                // Skip consumables (food) - companion needs these
                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable)
                    continue;
                    
                // Skip equipped items
                if (item.m_equipped)
                    continue;
                    
                // Skip quest items
                if (item.m_shared.m_questItem)
                    continue;
                
                // Try to add to chest
                if (chestInv.CanAddItem(item))
                {
                    var clone = item.Clone();
                    if (chestInv.AddItem(clone))
                    {
                        storageInv.RemoveItem(item);
                        deposited++;
                    }
                }
                
                // Stop if chest is full
                if (chestInv.GetEmptySlots() == 0) break;
            }
            
            // Save inventory changes
            if (deposited > 0)
            {
                inventory.SaveToZDO();
                ShowMessage($"{companion.companionName}: Deposited {deposited} items");
            }
            else
            {
                ShowMessage($"{companion.companionName}: Nothing to deposit");
            }

            // ?? Organize the chest cluster ?????????????????????????????????????
            // Whenever the player explicitly orders a companion to a chest we also
            // run the storage organizer over every chest in the local cluster so
            // partial stacks of the same item across multiple chests get
            // consolidated.  Without this, manually commanding a deposit just
            // dumps items into one chest and the world ends up with multiple
            // half-stacks of the same thing scattered around.
            int organized = 0;
            try
            {
                var clusterChests = ChestHelper.FindNearbyChests(
                    container.transform.position, CompanionSettings.ChestAutoSortRadius);
                if (clusterChests != null && clusterChests.Count >= 2)
                {
                    var orgResult = SmartStorageOrganizer.OrganizeChestCluster(
                        clusterChests,
                        container.transform.position,
                        stationSearchRadius: 25f);
                    organized = orgResult.ItemsMoved + orgResult.StacksConsolidated;
                    if (organized > 0)
                    {
                        ShowMessage($"{companion.companionName}: Organized {organized} item(s) across {clusterChests.Count} chest(s)");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionCommandSystem] Chest organization failed: {ex.Message}");
            }

            CompleteCommand(companion, deposited > 0 ? "Items deposited" : (organized > 0 ? "Chests organized" : "Nothing to deposit"));
        }

        #endregion

        #region Command Monitoring

        private IEnumerator MonitorMoveCommand(CompanionController companion, ActiveCommand command)
        {
            while (companion != null && _activeCommands.ContainsKey(companion))
            {
                var activeCmd = _activeCommands[companion];
                if (activeCmd != command || activeCmd.IsComplete) yield break;

                // Check if arrived
                float dist = Vector3.Distance(companion.transform.position, command.TargetPosition);
                if (dist <= moveDestinationThreshold)
                {
                    CompleteCommand(companion, "Arrived at destination");
                    yield break;
                }

                // Check expiration
                if (Time.time >= command.ExpirationTime)
                {
                    CompleteCommand(companion, "Command timed out");
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
            }
        }

        private IEnumerator MonitorSitCommand(CompanionController companion, ActiveCommand command)
        {
            float startTime = Time.time;
            float timeout = 15f; // 15 seconds to reach chair

            while (companion != null && _activeCommands.ContainsKey(companion))
            {
                var activeCmd = _activeCommands[companion];
                if (activeCmd != command || activeCmd.IsComplete) yield break;

                // Check if close enough to sit
                float dist = Vector3.Distance(companion.transform.position, command.TargetPosition);
                if (dist <= interactionDistance)
                {
                    TrySitOnTarget(companion, command);
                    yield break;
                }

                // Check timeout
                if (Time.time - startTime > timeout)
                {
                    CompleteCommand(companion, "Couldn't reach chair");
                    yield break;
                }

                yield return new WaitForSeconds(0.3f);
            }
        }

        private IEnumerator MonitorTrainCommand(CompanionController companion, ActiveCommand command)
        {
            float startTime = Time.time;
            float timeout = 15f;

            while (companion != null && _activeCommands.ContainsKey(companion))
            {
                var activeCmd = _activeCommands[companion];
                if (activeCmd != command || activeCmd.IsComplete) yield break;

                // Check if close enough to train
                float dist = Vector3.Distance(companion.transform.position, command.TargetPosition);
                if (dist <= interactionDistance * 2f)
                {
                    TryStartTraining(companion, command);
                    yield break;
                }

                // Check timeout
                if (Time.time - startTime > timeout)
                {
                    CompleteCommand(companion, "Couldn't reach training target");
                    yield break;
                }

                yield return new WaitForSeconds(0.3f);
            }
        }

        private void TrySitOnTarget(CompanionController companion, ActiveCommand command)
        {
            var interactionBehavior = companion.GetComponent<CompanionInteractionBehavior>();
            if (interactionBehavior != null)
            {
                // Pass isCommanded=true so the companion stays seated until timeout, not when owner moves
                bool sat = interactionBehavior.TryFindAndSit(isCommanded: true);
                if (sat)
                {
                    CompleteCommand(companion, "Sitting");
                }
                else
                {
                    CompleteCommand(companion, "Couldn't sit on chair");
                }
            }
            else
            {
                CompleteCommand(companion, "No interaction behavior");
            }
        }

        private void TryStartTraining(CompanionController companion, ActiveCommand command)
        {
            var stateController = companion.GetComponent<CompanionStateController>();
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            
            if (idleBehavior == null)
            {
                string reason = "No behavior component";
                if (stateController != null)
                    stateController.FailCommand(reason);
                else
                    ShowMessage($"{companion.companionName}: Can't train - {reason}");
                
                if (_activeCommands.TryGetValue(companion, out var cmd))
                    cmd.IsComplete = true;
                return;
            }
            
            // Check prerequisites BEFORE attempting to start
            var inventory = companion.GetComponent<CompanionInventory>();
            bool hasBow = false;
            if (inventory != null)
            {
                var leftHand = inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
                var leftBack = inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
                hasBow = (leftHand?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow) ||
                         (leftBack?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow);
            }
            
            if (!hasBow)
            {
                string reason = "No bow equipped";
                if (stateController != null)
                    stateController.FailCommand(reason);
                else
                    ShowMessage($"{companion.companionName}: Can't train - {reason}");
                
                if (_activeCommands.TryGetValue(companion, out var cmd))
                    cmd.IsComplete = true;
                return;
            }

            Debug.Log($"[COMMAND] {companion.companionName} attempting to start bow training...");
            
            // Use the public method to start bow training (uses StartAsCommand internally)
            bool started = idleBehavior.TryStartBowTraining();
            
            if (started)
            {
                // Training started - register with state controller for tracking
                if (stateController != null)
                {
                    stateController.StartCommand(
                        CompanionStateController.CommandType.SubBehavior,
                        command.TargetPosition,
                        command.TargetObject,
                        null,
                        180f // 3 minute timeout for training
                    );
                }
                
                Debug.Log($"[COMMAND] {companion.companionName} training STARTED successfully");
                ShowMessage($"{companion.companionName}: Training");
            }
            else
            {
                // Training couldn't start - find out why
                string reason = "No archery target nearby";
                
                if (stateController != null)
                    stateController.FailCommand(reason);
                else
                    ShowMessage($"{companion.companionName}: Can't train - {reason}");
                
                if (_activeCommands.TryGetValue(companion, out var cmd))
                    cmd.IsComplete = true;
                
                Debug.Log($"[COMMAND] {companion.companionName} training failed: {reason}");
            }
        }

        #endregion

        #region Command Completion

        private void UpdateActiveCommands()
        {
            var toRemove = new List<CompanionController>();

            foreach (var kvp in _activeCommands)
            {
                var companion = kvp.Key;
                var command = kvp.Value;

                if (companion == null || command.IsComplete)
                {
                    toRemove.Add(companion);
                    continue;
                }

                // Check expiration for attack commands
                if (command.Type == CommandType.AttackTarget)
                {
                    if (Time.time >= command.ExpirationTime)
                    {
                        CompleteCommand(companion, "Attack command expired");
                        toRemove.Add(companion);
                        continue;
                    }

                    // Check if target is dead
                    if (command.TargetCharacter != null && command.TargetCharacter.IsDead())
                    {
                        CompleteCommand(companion, "Target eliminated");
                        toRemove.Add(companion);
                    }
                }
            }

            foreach (var companion in toRemove)
            {
                _activeCommands.Remove(companion);
            }
        }

        private void CompleteCommand(CompanionController companion, string reason)
        {
            if (companion == null) return;
            
            Debug.Log($"[COMMAND] CompleteCommand for {companion.companionName}: {reason}");

            CommandType completedType = CommandType.None;
            if (_activeCommands.TryGetValue(companion, out var command))
            {
                command.IsComplete = true;
                completedType = command.Type;
            }

            // CRITICAL: Only clear command priority for commands that don't delegate to sub-behaviors
            // Sub-behaviors (training, gathering, smelting, etc.) manage their OWN command priority
            // via StartAsCommand() and Complete()/Cancel() - we must not interfere
            bool isSubBehaviorCommand = completedType == CommandType.TrainArchery ||
                                        completedType == CommandType.GatherResource ||
                                        completedType == CommandType.OperateSmelter ||
                                        completedType == CommandType.TendFire ||
                                        completedType == CommandType.UseWorkstation;
            
            // Release movement priority via state controller
            var stateController = companion.GetComponent<CompanionStateController>();
            if (stateController != null && !isSubBehaviorCommand)
            {
                stateController.ReleaseMovementPriority("PlayerCommand");
            }
            
            var combatMovement = companion.GetComponent<CompanionCombatMovement>();
            if (combatMovement != null && combatMovement.HasCommandPriority && !isSubBehaviorCommand)
            {
                Debug.Log($"[COMMAND] Clearing command priority for {companion.companionName} (non-subbehavior command: {completedType})");
                combatMovement.ClearCommandPriority();
            }
            else if (isSubBehaviorCommand)
            {
                Debug.Log($"[COMMAND] NOT clearing priority for {companion.companionName} - sub-behavior command {completedType} manages its own priority");
            }
            
            // Clear target assignment in coordinator
            if (_activeCommands.TryGetValue(companion, out var completedCommand) && completedCommand.TargetObject != null)
            {
                CompanionCommandCoordinator.Instance?.ClearTargetAssignment(completedCommand.TargetObject);
            }
        }

        private void CancelCommand(CompanionController companion)
        {
            if (companion == null) return;
            
            Debug.Log($"[COMMAND] CancelCommand for {companion.companionName}");

            // Clear target assignment in coordinator before removing command
            if (_activeCommands.TryGetValue(companion, out var command))
            {
                if (command.TargetObject != null)
                {
                    CompanionCommandCoordinator.Instance?.ClearTargetAssignment(command.TargetObject);
                }
                command.IsComplete = true;
                _activeCommands.Remove(companion);
            }

            // Release movement priority via state controller
            var stateController = companion.GetComponent<CompanionStateController>();
            if (stateController != null)
            {
                stateController.ReleaseMovementPriority("PlayerCommand");
                stateController.CancelCommand("Cancelled", silent: true);
            }

            // Clear command priority - this clears priority target AND move destination
            var combatMovement = companion.GetComponent<CompanionCombatMovement>();
            if (combatMovement != null)
            {
                combatMovement.ClearCommandPriority();
            }
        }

        #endregion

        #region Feedback

        private void ShowPingMarker(Vector3 position, CommandType type)
        {
            if (!showPingMarker) return;

            // Create a simple ping marker effect
            StartCoroutine(ShowPingMarkerCoroutine(position, type));
        }

        private IEnumerator ShowPingMarkerCoroutine(Vector3 position, CommandType type)
        {
            // Create ping effect using Valheim's effect system if available
            GameObject effect = null;
            
            // Try to spawn a visual effect (outside try-catch for the yield)
            var effectPrefab = ZNetScene.instance?.GetPrefab("vfx_lootspawn");
            if (effectPrefab != null)
            {
                try
                {
                    effect = Instantiate(effectPrefab, position + Vector3.up * 0.5f, Quaternion.identity);
                    _activePingMarkers.Add(effect);
                }
                catch { }
            }
            
            // Wait for ping duration
            yield return new WaitForSeconds(pingMarkerDuration);
            
            // Clean up
            if (effect != null)
            {
                try
                {
                    _activePingMarkers.Remove(effect);
                    Destroy(effect);
                }
                catch { }
            }
        }

        private void ShowCommandFeedback(CompanionController companion, CommandType type, GameObject target)
        {
            string message = type switch
            {
                CommandType.AttackTarget => $"{companion.companionName}: Attacking!",
                CommandType.MoveToPosition => $"{companion.companionName}: Moving!",
                CommandType.SitOnChair => $"{companion.companionName}: Going to sit",
                CommandType.TrainArchery => $"{companion.companionName}: Training",
                CommandType.InteractWithObject => $"{companion.companionName}: On it!",
                CommandType.GatherResource => $"{companion.companionName}: Gathering!",
                CommandType.OperateSmelter => $"{companion.companionName}: Operating smelter",
                CommandType.TendFire => $"{companion.companionName}: Tending fire",
                CommandType.UseWorkstation => $"{companion.companionName}: Working",
                CommandType.DepositToChest => $"{companion.companionName}: Storing items",
                CommandType.CookFood => $"{companion.companionName}: Cooking",
                CommandType.RepairBuilding => $"{companion.companionName}: Repairing",
                CommandType.Fish => $"{companion.companionName}: Fishing",
                CommandType.Farm => $"{companion.companionName}: Farming",
                _ => $"{companion.companionName}: Acknowledged"
            };

            ShowMessage(message);

            // Play a sound effect
            try
            {
                // Try to play the companion's acknowledge sound
                var character = companion.GetComponent<Character>();
                if (character != null)
                {
                    var zanim = companion.GetComponent<ZSyncAnimation>();
                    zanim?.SetTrigger("alert");
                }
            }
            catch { }
        }

        private void ShowMessage(string message)
        {
            // Show message in the game's message system
            if (MessageHud.instance != null)
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, message);
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Checks if a companion has an active command.
        /// </summary>
        public bool HasActiveCommand(CompanionController companion)
        {
            return _activeCommands.ContainsKey(companion) && !_activeCommands[companion].IsComplete;
        }

        /// <summary>
        /// Gets the active command for a companion.
        /// </summary>
        public ActiveCommand GetActiveCommand(CompanionController companion)
        {
            return _activeCommands.TryGetValue(companion, out var cmd) ? cmd : null;
        }

        /// <summary>
        /// Cancels all active commands.
        /// </summary>
        public void CancelAllCommands()
        {
            foreach (var companion in new List<CompanionController>(_activeCommands.Keys))
            {
                CancelCommand(companion);
            }
            _activeCommands.Clear();
        }

        /// <summary>
        /// Manually issues a command to a specific companion.
        /// </summary>
        public void IssueManualCommand(CompanionController companion, CommandType type, Vector3 position, GameObject targetObj = null, Character targetChar = null)
        {
            if (companion == null) return;

            var target = (type, position, targetObj, targetChar);
            IssueCommand(companion, target);
        }

        #endregion

        #region Static Initialization

        /// <summary>
        /// Ensures the command system exists in the scene.
        /// Call this from the main mod initialization.
        /// </summary>
        public static void EnsureInitialized()
        {
            if (_instance != null) return;

            var go = new GameObject("CompanionCommandSystem");
            go.AddComponent<CompanionCommandSystem>();
            DontDestroyOnLoad(go);

            Debug.Log("[CompanionCommandSystem] Initialized");
        }

        #endregion
    }
}
