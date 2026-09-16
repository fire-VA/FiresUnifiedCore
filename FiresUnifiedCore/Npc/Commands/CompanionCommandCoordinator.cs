using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc.Movement;

namespace FiresCore.Npc.Commands
{
    /// <summary>
    /// Coordinates commands across a player's companions: sends each to the closest companion that isn't busy
    /// (or the closest overall), keeps two from taking the same target, and handles the Shift + right-click
    /// whistle that calls the closest companion to stand in front of the player for a while.
    /// </summary>
    public class CompanionCommandCoordinator : MonoBehaviour
    {
        #region Singleton
        
        private static CompanionCommandCoordinator _instance;
        public static CompanionCommandCoordinator Instance => _instance;
        
        #endregion
        
        #region Settings
        
        [Header("Whistle Command Settings")]
        [Tooltip("How long the companion stays in front of player after whistle (seconds)")]
        public float whistleStayDuration = 30f;
        [Tooltip("Distance in front of player where companion will stand")]
        public float whistleStandDistance = 2f;
        [Tooltip("Key modifier required for whistle (Shift by default)")]
        public KeyCode whistleModifier = KeyCode.LeftShift;
        [Tooltip("Mouse button for whistle (1 = Right Mouse)")]
        public int whistleMouseButton = 1;
        
        [Header("Command Coordination Settings")]
        [Tooltip("Minimum time between commands to same target (prevents spam)")]
        public float duplicateCommandCooldown = 5f;
        [Tooltip("Maximum distance a companion can be to receive a command")]
        public float maxCommandDistance = 100f;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region State
        
        // Track which companions are assigned to which targets
        // Key = target object instance ID, Value = (companion, assignment time)
        private Dictionary<int, (CompanionController companion, float assignTime)> _targetAssignments 
            = new Dictionary<int, (CompanionController, float)>();
        
        // Track companions that are in "whistle" mode (staying in front of player)
        private Dictionary<CompanionController, float> _whistledCompanions 
            = new Dictionary<CompanionController, float>();
        
        // Last whistle time to prevent spam
        private float _lastWhistleTime;
        private const float WhistleCooldown = 1f;
        
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
        }
        
        private void Update()
        {
            // Check for whistle input
            if (ShouldProcessInput())
            {
                ProcessWhistleInput();
            }
            
            // Update whistled companions
            UpdateWhistledCompanions();
            
            // Clean up stale target assignments
            CleanupStaleAssignments();
        }
        
        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
        
        #endregion
        
        #region Input Processing
        
        private bool ShouldProcessInput()
        {
            // Don't process if UI is open or game is paused
            if (Menu.IsVisible()) return false;
            if (Console.IsVisible()) return false;
            if (TextInput.IsVisible()) return false;
            if (Minimap.IsOpen()) return false;
            if (InventoryGui.IsVisible()) return false;
            if (StoreGui.IsVisible()) return false;
            
            var player = Player.m_localPlayer;
            if (player == null) return false;
            
            return true;
        }
        
        /// <summary>
        /// Optional external gate: return true to SUPPRESS the whistle this frame. A dependent mod sets this when
        /// Shift+RightClick means something else in its context (e.g. FiresDungeonMaster's build-box staging area,
        /// where the player is placing/editing a section). Null = never suppress.
        /// </summary>
        public static System.Func<bool> WhistleSuppressor;

        private void ProcessWhistleInput()
        {
            bool shiftHeld = UnityEngine.Input.GetKey(whistleModifier) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            bool altHeld = UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt);
            bool rightMousePressed = UnityEngine.Input.GetMouseButtonDown(whistleMouseButton);

            // Alt+Shift+RightClick is the in-world context-menu / editor combo (FiresContextMenuDriver), never a whistle.
            if (!shiftHeld || altHeld || !rightMousePressed) return;

            // External staging-area gate (e.g. near a FiresDungeonMaster footprint box) suppresses the whistle entirely.
            if (WhistleSuppressor != null) { try { if (WhistleSuppressor()) return; } catch { } }
            
            // Check cooldown
            if (Time.time - _lastWhistleTime < WhistleCooldown) return;
            
            // Notify patches that shift-click was used (blocks player kick)
            CompanionPatches.NotifyPingUsed();
            
            var player = Player.m_localPlayer;
            if (player == null) return;
            
            // Find companions to whistle
            ExecuteWhistleCommand(player);
        }
        
        #endregion
        
        #region Whistle Command
        
        /// <summary>
        /// Executes the whistle command - summons the closest available companion
        /// to stand in front of the player for inventory management.
        /// </summary>
        private void ExecuteWhistleCommand(Player player)
        {
            _lastWhistleTime = Time.time;
            
            // Get all owned companions that are following
            var companions = GetAvailableCompanions(player);
            if (companions.Count == 0)
            {
                ShowMessage("No companions to summon");
                return;
            }
            
            // Find the closest companion that isn't already whistled
            CompanionController bestCompanion = null;
            float bestDistance = float.MaxValue;
            
            foreach (var companion in companions)
            {
                // Skip companions already in whistle mode
                if (_whistledCompanions.ContainsKey(companion)) continue;
                
                float dist = Vector3.Distance(player.transform.position, companion.transform.position);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestCompanion = companion;
                }
            }
            
            // If all companions are whistled, pick the one whose whistle is expiring soonest
            if (bestCompanion == null && companions.Count > 0)
            {
                float earliestExpire = float.MaxValue;
                foreach (var kvp in _whistledCompanions)
                {
                    if (companions.Contains(kvp.Key) && kvp.Value < earliestExpire)
                    {
                        earliestExpire = kvp.Value;
                        bestCompanion = kvp.Key;
                    }
                }
            }
            
            if (bestCompanion == null)
            {
                ShowMessage("No companions available");
                return;
            }
            
            // Execute the whistle
            WhistleCompanion(bestCompanion, player);
        }
        
        /// <summary>
        /// Whistles a specific companion to come to the player.
        /// </summary>
        private void WhistleCompanion(CompanionController companion, Player player)
        {
            if (companion == null || player == null) return;
            
            // Calculate position in front of player
            Vector3 standPosition = GetStandPositionInFrontOfPlayer(player);
            
            // Cancel any current sub-behaviors
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            idleBehavior?.OnCombatStarted();
            
            // Force detach from any attachment (chairs, etc.)
            var interactionBehavior = companion.GetComponent<Interactions.CompanionInteractionBehavior>();
            interactionBehavior?.ForceDetach();
            
            // Clear current AI destinations
            var companionAI = companion.GetCompanionAI();
            if (companionAI != null)
            {
                companionAI.ClearIdleDestination();
                companionAI.ClearCommandDestination();
            }
            
            // Set move destination via combat movement (with command priority)
            var combatMovement = companion.GetComponent<CompanionCombatMovement>();
            if (combatMovement != null)
            {
                // Set command priority for the duration
                combatMovement.SetCommandPriorityDuration(whistleStayDuration + 10f);
                combatMovement.SetMoveDestination(standPosition, useWalk: false, skipCommandOverride: false);
            }
            
            // Register this companion as whistled
            float expireTime = Time.time + whistleStayDuration;
            _whistledCompanions[companion] = expireTime;
            
            // Show feedback
            ShowMessage($"{companion.GetDisplayName()}: Coming!");
            CompanionChatHelper.QuickMessages.MovingToPosition(companion);
            
            // Play alert animation
            var zanim = companion.GetComponent<ZSyncAnimation>();
            zanim?.SetTrigger("alert");
            
            if (VerboseLogging)
                Debug.Log($"[CompanionCommandCoordinator] Whistled {companion.companionName} to {standPosition}, staying for {whistleStayDuration}s");
        }
        
        /// <summary>
        /// Gets a good position in front of the player for the companion to stand.
        /// </summary>
        private Vector3 GetStandPositionInFrontOfPlayer(Player player)
        {
            Vector3 basePos = player.transform.position;
            Vector3 forward = player.transform.forward;
            forward.y = 0;
            forward.Normalize();
            
            Vector3 targetPos = basePos + forward * whistleStandDistance;
            
            // Get ground height at target position
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(targetPos, out groundHeight))
                {
                    targetPos.y = groundHeight + 0.1f;
                }
            }
            
            return targetPos;
        }
        
        /// <summary>
        /// Updates whistled companions - releases them when duration expires.
        /// </summary>
        private void UpdateWhistledCompanions()
        {
            var toRemove = new List<CompanionController>();
            
            foreach (var kvp in _whistledCompanions)
            {
                var companion = kvp.Key;
                float expireTime = kvp.Value;
                
                // Check if companion is still valid
                if (companion == null || companion.isDefeated)
                {
                    toRemove.Add(companion);
                    continue;
                }
                
                // Check if whistle duration has expired
                if (Time.time >= expireTime)
                {
                    ReleaseWhistledCompanion(companion);
                    toRemove.Add(companion);
                    continue;
                }
                
                // Keep companion in "stay" mode near their current position
                // (they may have arrived and are just waiting)
                var combatMovement = companion.GetComponent<CompanionCombatMovement>();
                if (combatMovement != null && !combatMovement.HasMoveDestination)
                {
                    // They've arrived - just let them idle in place
                    // No need to re-issue commands
                }
            }
            
            foreach (var companion in toRemove)
            {
                _whistledCompanions.Remove(companion);
            }
        }
        
        /// <summary>
        /// Releases a whistled companion back to normal behavior.
        /// </summary>
        private void ReleaseWhistledCompanion(CompanionController companion)
        {
            if (companion == null) return;
            
            // Clear command priority
            var combatMovement = companion.GetComponent<CompanionCombatMovement>();
            combatMovement?.ClearCommandPriority();
            
            // Clear move destination
            combatMovement?.ClearMoveDestination();
            
            // Let companion resume following
            if (VerboseLogging)
                Debug.Log($"[CompanionCommandCoordinator] Released {companion.companionName} from whistle mode");
        }
        
        #endregion
        
        #region Command Coordination
        
        /// <summary>
        /// The best companion for a command: the closest one that isn't busy, or the closest busy one when
        /// <paramref name="allowInterrupt"/> is set (always the case for move commands). Null when none qualifies.
        /// </summary>
        public CompanionController SelectBestCompanionForCommand(
            Player player, 
            GameObject targetObject, 
            Vector3 targetPosition,
            bool allowInterrupt = false)
        {
            if (player == null) return null;
            
            var companions = GetAvailableCompanions(player);
            if (companions.Count == 0)
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionCommandCoordinator] No companions available (GetAvailableCompanions returned empty)");
                return null;
            }
            
            // Check if this target is already assigned
            if (targetObject != null)
            {
                int targetId = targetObject.GetInstanceID();
                if (_targetAssignments.TryGetValue(targetId, out var assignment))
                {
                    // Target is already being handled - check if assignment is still valid
                    if (assignment.companion != null && !assignment.companion.isDefeated)
                    {
                        if (Time.time - assignment.assignTime < duplicateCommandCooldown)
                        {
                            if (VerboseLogging)
                                Debug.Log($"[CompanionCommandCoordinator] Target already assigned to {assignment.companion.companionName}");
                            return null; // Don't reassign
                        }
                    }
                }
            }
            
            // Find the best companion
            CompanionController bestAvailable = null;
            float bestAvailableDist = float.MaxValue;
            
            CompanionController bestBusy = null;
            float bestBusyDist = float.MaxValue;
            
            foreach (var companion in companions)
            {
                // Skip whistled companions
                if (_whistledCompanions.ContainsKey(companion)) continue;
                
                float dist = Vector3.Distance(companion.transform.position, targetPosition);
                if (dist > maxCommandDistance) continue;
                
                bool isBusy = IsCompanionBusy(companion);
                
                if (!isBusy)
                {
                    if (dist < bestAvailableDist)
                    {
                        bestAvailableDist = dist;
                        bestAvailable = companion;
                    }
                }
                else
                {
                    if (dist < bestBusyDist)
                    {
                        bestBusyDist = dist;
                        bestBusy = companion;
                    }
                }
            }
            
            // Prefer available companion, fall back to busy if allowed
            CompanionController selected = bestAvailable;
            if (selected == null && allowInterrupt)
            {
                selected = bestBusy;
                if (VerboseLogging && selected != null)
                    Debug.Log($"[CompanionCommandCoordinator] All companions busy - interrupting {selected.companionName}");
            }
            
            // CRITICAL: If still no selection and we have companions, log why
            if (selected == null && VerboseLogging)
            {
                Debug.Log($"[CompanionCommandCoordinator] Could not select companion: " +
                    $"{companions.Count} available, bestAvailable={bestAvailable?.companionName ?? "none"}, " +
                    $"bestBusy={bestBusy?.companionName ?? "none"}, allowInterrupt={allowInterrupt}");
            }
            
            // Register the assignment
            if (selected != null && targetObject != null)
            {
                RegisterTargetAssignment(targetObject, selected);
            }
            
            if (VerboseLogging && selected != null)
                Debug.Log($"[CompanionCommandCoordinator] Selected {selected.companionName} for command (busy={selected == bestBusy})");
            
            return selected;
        }
        
        /// <summary>
        /// Checks if a companion is currently busy with a task.
        /// CRITICAL: This should NOT block Move commands - those have absolute priority
        /// and will interrupt whatever the companion is doing.
        /// </summary>
        private bool IsCompanionBusy(CompanionController companion)
        {
            if (companion == null) return false;
            
            // Check state controller for active commands
            var stateController = companion.GetComponent<CompanionStateController>();
            if (stateController != null)
            {
                // CRITICAL FIX: If companion has an active command, they ARE busy
                // But HasActiveCommand should be checked, not just the state
                if (stateController.HasActiveCommand)
                    return true;
                
                // Also check if in PlayerCommand state (may have command without HasActiveCommand briefly)
                if (stateController.CurrentState == CompanionStateController.CompanionState.PlayerCommand)
                    return true;
            }
            
            // Check idle behavior for active sub-behaviors
            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            if (idleBehavior != null && idleBehavior.IsInSubBehavior)
                return true;
            
            // Check combat movement for active command priority
            var combatMovement = companion.GetComponent<CompanionCombatMovement>();
            if (combatMovement != null && combatMovement.HasCommandPriority)
                return true;
            
            // Check if in combat - companions fighting enemies are busy
            if (companion.IsInCombat)
                return true;
            
            return false;
        }
        
        /// <summary>
        /// Registers that a companion has been assigned to a target.
        /// </summary>
        private void RegisterTargetAssignment(GameObject target, CompanionController companion)
        {
            if (target == null || companion == null) return;
            
            int targetId = target.GetInstanceID();
            _targetAssignments[targetId] = (companion, Time.time);
            
            if (VerboseLogging)
                Debug.Log($"[CompanionCommandCoordinator] Registered {companion.companionName} -> {target.name}");
        }
        
        /// <summary>
        /// Clears the assignment for a target (call when command completes).
        /// </summary>
        public void ClearTargetAssignment(GameObject target)
        {
            if (target == null) return;
            
            int targetId = target.GetInstanceID();
            if (_targetAssignments.Remove(targetId) && VerboseLogging)
                Debug.Log($"[CompanionCommandCoordinator] Cleared assignment for {target.name}");
        }
        
        /// <summary>
        /// Checks if a target already has a companion assigned.
        /// </summary>
        public bool IsTargetAssigned(GameObject target, out CompanionController assignedCompanion)
        {
            assignedCompanion = null;
            if (target == null) return false;
            
            int targetId = target.GetInstanceID();
            if (_targetAssignments.TryGetValue(targetId, out var assignment))
            {
                if (assignment.companion != null && !assignment.companion.isDefeated)
                {
                    assignedCompanion = assignment.companion;
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Cleans up stale target assignments.
        /// </summary>
        private void CleanupStaleAssignments()
        {
            var toRemove = new List<int>();
            
            foreach (var kvp in _targetAssignments)
            {
                var companion = kvp.Value.companion;
                var assignTime = kvp.Value.assignTime;
                
                // Remove if companion is invalid
                if (companion == null || companion.isDefeated)
                {
                    toRemove.Add(kvp.Key);
                    continue;
                }
                
                // Remove if assignment is very old (2 minutes)
                if (Time.time - assignTime > 120f)
                {
                    toRemove.Add(kvp.Key);
                }
            }
            
            foreach (var id in toRemove)
            {
                _targetAssignments.Remove(id);
            }
        }
        
        #endregion
        
        #region Helper Methods
        
        /// <summary>
        /// Gets all companions that are available to receive commands.
        /// </summary>
        private List<CompanionController> GetAvailableCompanions(Player player)
        {
            var result = new List<CompanionController>();
            
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null) continue;
                if (!companion.isTamed) continue;
                if (!companion.IsOwner(player)) continue;
                if (companion.isDefeated) continue;
                
                // Skip companions stationed as NPCs
                var npcModule = companion.GetComponent<NpcMode.CompanionNpcModule>();
                if (npcModule != null && npcModule.IsStationedAsNpc) continue;
                
                // Only include companions that should be following
                // Stayed companions should not respond to group commands
                if (!companion.ShouldBeFollowing) continue;
                
                result.Add(companion);
            }
            
            return result;
        }
        
        private void ShowMessage(string message)
        {
            if (MessageHud.instance != null)
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, message);
            }
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Checks if a companion is currently in whistle mode.
        /// </summary>
        public bool IsCompanionWhistled(CompanionController companion)
        {
            return _whistledCompanions.ContainsKey(companion);
        }
        
        /// <summary>
        /// Manually whistle a specific companion.
        /// </summary>
        public void WhistleCompanion(CompanionController companion)
        {
            var player = Player.m_localPlayer;
            if (player == null || companion == null) return;
            
            WhistleCompanion(companion, player);
        }
        
        /// <summary>
        /// Releases a companion from whistle mode early.
        /// </summary>
        public void ReleaseFromWhistle(CompanionController companion)
        {
            if (companion == null) return;
            
            if (_whistledCompanions.ContainsKey(companion))
            {
                ReleaseWhistledCompanion(companion);
                _whistledCompanions.Remove(companion);
            }
        }
        
        #endregion
        
        #region Static Initialization
        
        /// <summary>
        /// Ensures the coordinator exists in the scene.
        /// </summary>
        public static void EnsureInitialized()
        {
            if (_instance != null) return;
            
            var go = new GameObject("CompanionCommandCoordinator");
            go.AddComponent<CompanionCommandCoordinator>();
            DontDestroyOnLoad(go);
            
            Debug.Log("[CompanionCommandCoordinator] Initialized");
        }
        
        #endregion
    }
}
