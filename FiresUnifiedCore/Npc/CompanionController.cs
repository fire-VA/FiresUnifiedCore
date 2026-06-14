using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc.AI;
using FiresCore.Npc.Movement;
using FiresCore.Npc.NpcMode;
using FiresCore.Npc.IdleBehaviors;
using FiresCore.Npc.Interactions;
using FiresCore.Npc.Vault;
using FiresCore.Npc.Archetypes;
using FiresCore.Npc.Core;
using FiresCore.Npc.Formation;
using FiresCore.Lifecycle;
using Newtonsoft.Json;
using FiresCore.Npc;

using FiresCore.Logging;
namespace FiresCore.Npc
{
    /// <summary>
    /// Controller for AI companion NPCs using custom CompanionAI.
    /// These companions can follow, fight, and be equipped by players.
    /// Uses proper death/respawn system like players (destroy + respawn from vault).
    /// 
    /// AI ARCHITECTURE:
    /// - CompanionAI extends BaseAI (not MonsterAI) for complete control
    /// - Clean state machine: Idle, Following, Combat, Returning, Fleeing
    /// - Owner-centric behavior with proactive protection
    /// - No fighting with MonsterAI's hidden state or patrol logic
    /// 
    /// THREAT PRIORITY (handled by CompanionAI):
    /// 1. Enemies actively targeting the owner (IMMEDIATE response)
    /// 2. Enemies close to the owner
    /// 3. Enemies in front of the owner (blocking their path)
    /// 4. Enemies targeting the companion
    /// 5. Nearest enemy
    /// </summary>
    public class CompanionController : MonoBehaviour
    {
        // Static collection of all active companions for easy lookup
        private static readonly List<CompanionController> _allCompanions = new List<CompanionController>();
        
        /// <summary>
        /// Gets all currently active companions in the world.
        /// </summary>
        public static IReadOnlyList<CompanionController> AllCompanions => _allCompanions;
        
        /// <summary>
        /// Gets the active companion for a specific player (the one following them).
        /// Returns null if no companion is following.
        /// </summary>
        public static CompanionController GetActiveCompanionForPlayer(Player player)
        {
            if (player == null) return null;
            long playerId = player.GetPlayerID();
            
            // First, look for a companion that's actively following
            foreach (var companion in _allCompanions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (companion.ownerPlayerId != playerId) continue;
                if (!companion.isTamed) continue;
                
                // Check if actually following
                if (companion.ShouldBeFollowing)
                {
                    return companion;
                }
            }
            
            // If no companion is following, return the closest owned companion
            CompanionController closest = null;
            float closestDist = float.MaxValue;
            
            foreach (var companion in _allCompanions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (companion.ownerPlayerId != playerId) continue;
                if (!companion.isTamed) continue;
                
                float dist = Vector3.Distance(player.transform.position, companion.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = companion;
                }
            }
            
            return closest;
        }

        [Header("Companion Identity")]
        public string companionName = "Companion";
        public string displayNameOverride;
        public string companionId;

[Header("Placement")]
        /// <summary>When true, this NPC was placed via hammer as a static quest NPC — not a wild companion.</summary>
        public bool isStaticPlacement = false;

[Header("Taming")]
        public string tamingItemPrefab = "Coins";
        public int tamingItemAmount = 100;
        public bool isTamed = false;
        public long ownerPlayerId = 0;

[Header("Combat")]
public bool canFight = true;
public float aggroRange = 30f;
public float attackRange = 2f;
public bool allowNonOwnerDamage = true;
public float targetScanInterval = 0.25f;  // Scan 4x per second for threats
        
[Header("Protection Settings")]
public float protectionRange = 20f;      // How far ahead to scan for threats to owner
public float interceptDistance = 15f;    // How far companion will move to intercept threats
public bool proactiveProtection = true;  // Move ahead to engage threats before they reach owner

        [Header("Follow Teleport Settings")]
        [Tooltip("Distance at which companion teleports to owner. Should match CompanionAI.teleportDistance.")]
        public float maxFollowDistance = 30f;
        [Tooltip("How often to check if companion is too far. Lower = more responsive but more CPU.")]
        public float teleportCheckInterval = 1f;

         // Legacy death state - now delegates to CompanionDeathHandler
        // Kept for backward compatibility with other systems
        [HideInInspector]
        public bool isDefeated = false;
        [HideInInspector]
        public float defeatedTime = 0f;
        [HideInInspector]
        public float respawnDelay = 120f;
        [HideInInspector]
        public float respawnRadius = 5f;
        private bool _isRespawning = false;

        // Core Valheim components
        private ZNetView _nview;
        private Humanoid _humanoid;
        private CompanionAI _companionAI;
        private Tameable _tameable;
        private Character _character;
        private Animator _animator;

        // Equipment/Inventory
        private CompanionInventory _inventory;
        private NpcVisEquipment _visEquipment;
        private CompanionCombat _combat;
        private CompanionSkills _skills;
        private CompanionStats _stats;
        private CompanionProgression _progression;
        private CompanionConsumables _consumables;
        private CompanionEquipmentData _equipmentData;
        private CompanionDeathHandler _deathHandler;
        private CompanionCombatMovement _combatMovement;
        private CompanionKillTracker _killTracker;
        private CompanionStateController _stateController;
        private ArchetypeController _archetypeController;
        private BehaviorCoordinator _behaviorCoordinator;
        private UnifiedMovementAuthority _movementAuthority;
        private CompanionFormationController _formationController;

        // State
        private bool _initialized = false;
        private float _lastTeleportCheck;

        // DEFERRED FOLLOW RESTORE
        // Set in LoadFromZDO when companion_wasfollowing=true but the owner Player
        // wasn't yet in the game (race between zone-stream and player spawn).  The
        // Update() watcher polls GetOwner() and flips ShouldFollow on the moment
        // the player actually shows up.  Cleared once follow has been activated.
        private bool _pendingDeferredFollowRestore = false;
        private float _lastDeferredFollowCheck = 0f;
        private const float DEFERRED_FOLLOW_CHECK_INTERVAL = 0.5f; // seconds between owner-presence polls

        // CATCH-UP DETECTION (was: STRANDED DETECTION)
        //
        // Behavioural intent:
        //   When a following companion drifts past <see cref="maxFollowDistance"/>, the OLD code
        //   teleported them after only 2 s (STRANDED_GRACE). On a fresh world that produced the
        //   "companion snaps to me every time I take a few steps" UX the user reported — they
        //   could have just turned around and walked back, no teleport needed.
        //
        //   The refined model is a two-phase catch-up:
        //     1. CATCH-UP — the moment distance exceeds the follow radius, snap the AI out of any
        //        idle wander/work behaviour via <see cref="AI.CompanionAI.SetFollowTarget"/> (FSM
        //        Idle→Following) and let the companion path back on foot. Track the closest
        //        distance achieved this session (<see cref="_catchupBestDistance"/>) as a
        //        progress signal.
        //     2. TELEPORT (last resort) — only when one of the following is true:
        //        • <see cref="HARD_STRAND_MULTIPLIER"/> hit (player did a portal / long jump),
        //        • CATCHUP_WINDOW elapsed AND closing-the-gap progress was below
        //          <see cref="CATCHUP_PROGRESS_REQUIRED"/> (companion is stuck / blocked terrain),
        //        • CATCHUP_WINDOW elapsed AND current distance > start distance (companion is
        //          actively losing ground — owner is moving faster than they can path).
        //
        //   When the companion gets back inside the follow radius on foot, all catch-up state
        //   resets and the next drift starts a fresh attempt.
        private float _strandedSinceTime         = -1f;  // time catchup began (-1 = not in catchup)
        private float _catchupStartDistance      = 0f;   // distance to owner at catchup entry
        private float _catchupBestDistance       = 0f;   // closest we've gotten during this catchup session
        private bool  _catchupInterruptIssued    = false;// SetFollowTarget already called this session?
        private const float CATCHUP_WINDOW            = 15f;  // seconds to attempt walking back before considering teleport
        private const float CATCHUP_PROGRESS_REQUIRED = 5f;   // metres of net inward progress that counts as "they're making it"
        private const float HARD_STRAND_MULTIPLIER    = 2.5f; // distance > maxFollowDistance * this = bypass catchup, teleport now

        // GLITCH PREVENTION - Teleport loop detection
        private int _consecutiveTeleports = 0;
        private float _lastTeleportTime = 0f;
        private float _lastTeleportDistance = 0f;
        private Vector3 _ownerPosAtLastTeleport;   // owner position when we last triggered a teleport
        private const int MAX_CONSECUTIVE_TELEPORTS = 3;
        private const float TELEPORT_LOOP_WINDOW = 10f;        // seconds in which we check for loops
        private const float TELEPORT_SUCCESS_THRESHOLD = 10f;  // companion must be within this distance to count as success
        private const float LOOP_OWNER_MOVED_THRESHOLD = 8f;   // if owner moved this far since last teleport it is NOT a loop

        // POST-TELEPORT SETTLE WINDOW
        // After a successful teleport, suppress further pulls for this long even if
        // the companion drifts past maxFollowDistance again. Combat-induced drift
        // during active fights would otherwise re-pull every 3-5s, producing the
        // "teleport, vanish, teleport, vanish" loop the user observed and the
        // associated FPS spike from running OnTeleportedFar + GetSafeTeleportPosition
        // + RPC broadcast on multiple companions every few seconds.
        // The catastrophic-distance override below still kicks in for true
        // dungeon-entry / cross-map gaps.
        private const float POST_TELEPORT_SETTLE       = 6f;
        private const float CATASTROPHIC_DISTANCE_MULT = 4f;   // distance > maxFollow * this bypasses settle window

        // Set to true by TeleportToDestination so CheckFollowTeleport skips the owner-speed
        // block on the very next tick (portal teleports look like "owner is flying").
        private bool _portalTeleportPending;

        // GLITCH PREVENTION - Owner velocity tracking.
        // We track the owner's recent speed and refuse to teleport while they are
        // moving faster than OWNER_TELEPORT_MAX_SPEED — that range is reserved for
        // admin-flight, portal jumps, and other instant-translation effects where
        // teleporting toward the owner would just produce a chase loop.
        // Normal walking / running / sprinting are all well below this threshold,
        // so the companion no longer requires the owner to come to a full stop.
        private Vector3 _lastOwnerPosition;
        private float _lastOwnerPositionTime;
        private float _ownerBecameStillTime = -1f; // legacy, kept for telemetry only
        private const float OWNER_STILL_SPEED          = 1.0f;  // legacy "stopped" threshold (telemetry only)
        private const float OWNER_STILL_REQUIRED       = 1.5f;  // legacy still-time gate (no longer required for normal teleports)
        private const float OWNER_TELEPORT_MAX_SPEED   = 15f;   // m/s - above this we assume owner is flying / portal-jumping
        private const float OWNER_VELOCITY_CHECK_INTERVAL = 0.5f; // how often to sample owner position


        // Static counter for generating truly unique IDs
        private static int _idCounter = 0;

    #region Unity Lifecycle

    private void Awake()
   {
       _nview = GetComponent<ZNetView>();
     _animator = GetComponentInChildren<Animator>(true);
            _character = GetComponent<Character>();
       _humanoid = GetComponent<Humanoid>();
       
       // Register this companion in the global list
       if (!_allCompanions.Contains(this))
       {
           _allCompanions.Add(this);
       }
  }

        private void Start()
        {
     if (_nview != null && _nview.IsValid())
        {
    InitializeCompanion();
          LoadFromZDO();
        }
        }

     private void Update()
        {
           if (!_initialized || _nview == null || !_nview.IsValid())
    return;

            // Tick shared systems once per frame (uses static frame counter to avoid redundant calls)
            TickSharedSystems();

   UpdateCompanionBehavior();

            // DEFERRED FOLLOW RESTORE WATCHER
            // If LoadFromZDO ran before the owner Player spawned, _pendingDeferredFollowRestore
            // was set and the AI was left untouched.  Poll for the owner here and
            // activate follow as soon as they actually exist.
            if (_pendingDeferredFollowRestore && Time.time - _lastDeferredFollowCheck >= DEFERRED_FOLLOW_CHECK_INTERVAL)
            {
                _lastDeferredFollowCheck = Time.time;
                TryActivateDeferredFollow();
            }

            // CRITICAL MULTIPLAYER FIX: Only the ZDO owner should run teleport checks
            // Without this check, ALL clients run CheckFollowTeleport which causes:
            // 1. Duplicate companions (old visual stays on remote client)
            // 2. Position desync (each client calculates different spawn positions)
            // 3. Teleport loops (clients fight over companion position)
            if (_nview.IsOwner())
            {
                CheckFollowTeleport();
            }
   }

        /// <summary>
        /// Polls for the owner Player and activates follow once they're actually
        /// in the game.  Called from Update() when LoadFromZDO determined we
        /// should be following but the owner hadn't spawned yet.
        /// </summary>
        private void TryActivateDeferredFollow()
        {
            if (!isTamed || ownerPlayerId == 0)
            {
                _pendingDeferredFollowRestore = false;
                return;
            }

            var owner = GetOwner();
            if (owner == null) return; // still not in game, keep waiting

            // Owner is finally here — flip on follow.
            if (_companionAI != null)
            {
                _companionAI.SetShouldFollow(true);
                _companionAI.SetFollowTarget(owner.gameObject);
            }
            _pendingDeferredFollowRestore = false;

            Debug.Log($"[CompanionController] Deferred follow restore activated for {companionName} - owner {owner.GetPlayerName()} now present");
        }

   private void OnDestroy()
        {
            // Unregister from global list
            _allCompanions.Remove(this);

     if (isTamed && ownerPlayerId != 0)
        {
            // Skip the vault save if the local player is mid-respawn / loading.
            // Zone-unload during the player's teleport teardown destroys the
            // companion's GameObject; saving here writes to ZDO/customData while
            // IsTeleporting=true and deadlocks the zone stream. The periodic
            // CompanionVault.Tick covers the in-memory state up to this point;
            // the most we lose is changes since the last tick.
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;
         SaveCompanionToVault();
       }
        }

        // Static frame counter to ensure shared systems tick only once per frame
        private static int _lastTickedFrame = -1;
        
        /// <summary>
        /// Ticks shared companion systems (vault dirty flush, etc.) once per frame.
        /// Called from every CompanionController.Update but uses a static frame counter
        /// so only the first companion to tick each frame actually does the work.
        /// </summary>
        private static void TickSharedSystems()
        {
            int currentFrame = Time.frameCount;
            if (currentFrame == _lastTickedFrame) return;
            _lastTickedFrame = currentFrame;
            
            // Periodic dirty flush for batched vault saves
            Vault.CompanionVault.Tick();
            
            // Periodic cleanup of destroyed containers from the registry
            ContainerRegistry.Cleanup();
            
            // Ensure GroupCombatCoordinator singleton exists for multi-companion coordination
            Combat.GroupCombatCoordinator.EnsureInitialized();
        }

   #endregion

      #region Initialization

        private void InitializeCompanion()
        {
    if (_initialized) return;

          try
            {
          if (_character == null)
                {
         Debug.LogError($"[CompanionController] {companionName} missing Character component!");
       return;
            }

    if (_humanoid == null)
     {
      Debug.LogWarning($"[CompanionController] {companionName} missing Humanoid - combat may be limited");
          }

     // Get or add our custom CompanionAI (replaces MonsterAI)
        _companionAI = GetComponent<CompanionAI>();
        if (_companionAI == null)
          {
          // If there's an existing MonsterAI, we need to work around it
          // In prefab setup, MonsterAI should be replaced with CompanionAI
          var existingMonsterAI = GetComponent<MonsterAI>();
          if (existingMonsterAI != null)
          {
              Debug.LogWarning($"[CompanionController] {companionName} has MonsterAI - consider replacing with CompanionAI in prefab");
              // Disable it - we'll use our own AI
              existingMonsterAI.enabled = false;
          }
          
          _companionAI = gameObject.AddComponent<CompanionAI>();
          }

        _tameable = GetComponent<Tameable>();
  if (_tameable == null)
          {
          _tameable = gameObject.AddComponent<Tameable>();
    }
         ConfigureTameable();

 _inventory = GetComponent<CompanionInventory>();
        if (_inventory == null)
       {
  _inventory = gameObject.AddComponent<CompanionInventory>();
     }

       _visEquipment = GetComponent<NpcVisEquipment>();
         if (_visEquipment == null)
             {
      _visEquipment = gameObject.AddComponent<NpcVisEquipment>();
       }

     _combat = GetComponent<CompanionCombat>();
                if (_combat == null)
             {
              _combat = gameObject.AddComponent<CompanionCombat>();
        }

      _skills = GetComponent<CompanionSkills>();
           if (_skills == null)
              {
   _skills = gameObject.AddComponent<CompanionSkills>();
            }

        // Stats and Progression systems
        _stats = GetComponent<CompanionStats>();
        if (_stats == null)
        {
            _stats = gameObject.AddComponent<CompanionStats>();
        }

        _progression = GetComponent<CompanionProgression>();
        if (_progression == null)
        {
            _progression = gameObject.AddComponent<CompanionProgression>();
        }

       _consumables = GetComponent<CompanionConsumables>();
           if (_consumables == null)
      {
          _consumables = gameObject.AddComponent<CompanionConsumables>();
        }

              _equipmentData = GetComponent<CompanionEquipmentData>();
                if (_equipmentData == null)
           {
         _equipmentData = gameObject.AddComponent<CompanionEquipmentData>();
        }

         // Add death handler for proper death/respawn
              _deathHandler = GetComponent<CompanionDeathHandler>();
    if (_deathHandler == null)
    {
    _deathHandler = gameObject.AddComponent<CompanionDeathHandler>();
       }

     // Add combat movement handler for smooth combat behavior
     _combatMovement = GetComponent<CompanionCombatMovement>();
      if (_combatMovement == null)
            {
       _combatMovement = gameObject.AddComponent<CompanionCombatMovement>();
            }

            // Add kill tracker for tracking combat statistics
            _killTracker = GetComponent<CompanionKillTracker>();
            if (_killTracker == null)
            {
                _killTracker = gameObject.AddComponent<CompanionKillTracker>();
            }

            // Add NPC module for quest giver/dialogue functionality
            var npcModule = GetComponent<CompanionNpcModule>();
            if (npcModule == null)
            {
                npcModule = gameObject.AddComponent<CompanionNpcModule>();
            }
            
            // Add centralized state controller for movement/emote coordination
            _stateController = GetComponent<CompanionStateController>();
            if (_stateController == null)
            {
                _stateController = gameObject.AddComponent<CompanionStateController>();
            }
            
            // Add archetype controller for role-based combat behaviors (Tank, Support, DPS)
            _archetypeController = GetComponent<ArchetypeController>();
            if (_archetypeController == null)
            {
                _archetypeController = gameObject.AddComponent<ArchetypeController>();
            }
            
            // Add hybrid ability manager for special abilities when companion has both main and sub archetypes
            var hybridAbilityManager = GetComponent<HybridAbilityManager>();
            if (hybridAbilityManager == null)
            {
                hybridAbilityManager = gameObject.AddComponent<HybridAbilityManager>();
            }
            
            // Add behavior coordinator for unified behavior state management
            _behaviorCoordinator = GetComponent<BehaviorCoordinator>();
            if (_behaviorCoordinator == null)
            {
                _behaviorCoordinator = gameObject.AddComponent<BehaviorCoordinator>();
            }
            
            // Add unified movement authority - SINGLE SOURCE OF TRUTH for all movement
            // All movement systems (AI, combat, idle behaviors) must go through this
            _movementAuthority = GetComponent<UnifiedMovementAuthority>();
            if (_movementAuthority == null)
            {
                _movementAuthority = gameObject.AddComponent<UnifiedMovementAuthority>();
            }
            
            // Add formation controller for group following and personal space
            _formationController = GetComponent<CompanionFormationController>();
            if (_formationController == null)
            {
                _formationController = gameObject.AddComponent<CompanionFormationController>();
            }

            // Personal-space enforcer keeps the companion from physically
            // shoving the player around or piling onto sibling companions
            // during combat. Pure physics nudge \u2014 see component summary.
            if (GetComponent<CompanionPersonalSpaceEnforcer>() == null)
            {
                gameObject.AddComponent<CompanionPersonalSpaceEnforcer>();
            }

            ConfigureCompanionAI();
            RegisterRPCs();
            
            // CRITICAL: Ensure CharacterAnimEvent has proper reference to Character
            // This is required for vanilla attack animation events to fire correctly
            EnsureCharacterAnimEventReference();

            _initialized = true;
            // Only log initialization for tamed companions or when verbose logging is enabled
            if (isTamed && FiresLogger.VerboseEnabled)
                Debug.Log($"[CompanionController] Initialized companion: {companionName} (ID: {companionId})");
     }
      catch (Exception ex)
    {
    Debug.LogError($"[CompanionController] Failed to initialize {companionName}: {ex}");
     }
        }

      private void ConfigureTameable()
    {
            if (_tameable == null) return;

         _tameable.m_commandable = true;
         _tameable.m_tamingTime = 0f;
         _tameable.m_fedDuration = 600f;
         _tameable.m_startsTamed = false;
        }

        private void ConfigureCompanionAI()
        {
            if (_companionAI == null) return;

            // ============================================================
            // CONFIGURE COMPANION AI
            // All settings are now on CompanionAI directly - no reflection needed!
            // ============================================================
            
            // Combat ranges
            _companionAI.aggroRange = aggroRange;
            _companionAI.attackRange = attackRange;
            _companionAI.maxChaseDistance = 100f;
            _companionAI.combatLeashDistance = 40f;
            _companionAI.giveUpTime = 15f;
            
            // Protection settings
            _companionAI.ownerProtectionRange = protectionRange;
            _companionAI.proactiveProtection = proactiveProtection;
            _companionAI.interceptPriority = 2f;
            
            // Follow settings - buffer zones
            _companionAI.stopDistanceInner = 2f;
            _companionAI.stopDistanceOuter = 5f;
            _companionAI.walkDistanceOuter = 8f;
            _companionAI.runDistanceInner = 12f;
            _companionAI.runDistanceOuter = 18f;
            _companionAI.catchUpDistance = 30f;
            
            // Self-preservation
            _companionAI.fleeHealthPercent = 0.2f;
            _companionAI.fleeTime = 10f;
            _companionAI.healReturnPercent = 0.5f;
            
            // BaseAI detection ranges (inherited)
            _companionAI.m_viewRange = aggroRange;
            _companionAI.m_viewAngle = 120f;
            _companionAI.m_hearRange = aggroRange * 1.5f;
            
            // Only log AI configuration for tamed companions when verbose is enabled
            if (isTamed && FiresLogger.VerboseEnabled)
                Debug.Log($"[CompanionController] Configured CompanionAI for {companionName}: aggroRange={aggroRange}, protectionRange={protectionRange}");
        }

        private void RegisterRPCs()
        {
    if (_nview == null) return;

            _nview.Register<string>("RPC_SetCompanionName", RPC_SetCompanionName);
            _nview.Register<long>("RPC_SetOwner", RPC_SetOwner);
            _nview.Register("RPC_TameCompanion", RPC_TameCompanion);
            _nview.Register<long>("RPC_Command", RPC_Command);
            _nview.Register("RPC_TeleportToOwner", RPC_TeleportToOwner); // Legacy - kept for backwards compatibility
            _nview.Register<Vector3>("RPC_TeleportToPosition", RPC_TeleportToPosition); // New - uses server position
            _nview.Register("RPC_Respawn", RPC_Respawn);
            _nview.Register<string>("RPC_SetTarget", RPC_SetTarget);
    }

        #endregion

   #region Combat API

        /// <summary>
        /// Force the companion to target a specific enemy.
        /// </summary>
        public void ForceTarget(Character target)
        {
            if (_companionAI != null && target != null && !target.IsDead())
            {
                _companionAI.ForceTarget(target);
            }
        }

        /// <summary>
        /// Get the current combat target.
        /// </summary>
        public Character GetCurrentTarget()
        {
            return _companionAI?.GetTargetCreature();
        }

        /// <summary>
        /// Check if the companion is currently in combat.
        /// </summary>
        public bool IsInCombat
        {
            get { return _companionAI?.IsInCombat ?? false; }
        }

        /// <summary>
        /// Check if a character is a valid target for this companion.
        /// </summary>
        public bool IsValidTarget(Character target)
        {
            if (target == null || target.IsDead()) return false;
            if (target.IsPlayer()) return false;
            if (target.IsTamed()) return false;
            if (_companionAI != null)
            {
                return _companionAI.IsEnemy(target);
            }
            return true;
        }

        private void RPC_SetTarget(long sender, string zdoidStr)
        {
            if (_nview != null && _nview.IsOwner()) return;
            // Remote clients receive target updates
            // Parse ZDOID and find the target
        }

        #endregion

        #region Behavior & Updates

     private void UpdateCompanionBehavior()
  {
       if (_character != null && _tameable != null)
     {
         bool valheimTamed = _character.IsTamed();
      if (valheimTamed != isTamed)
{
       isTamed = valheimTamed;
if (isTamed && ownerPlayerId == 0)
     {
          var nearestPlayer = Player.GetClosestPlayer(transform.position, 10f);
       if (nearestPlayer != null)
     {
          ownerPlayerId = nearestPlayer.GetPlayerID();
    SaveToZDO();
   SaveCompanionToVault();
       }
}
    }
  }
      }

        /// <summary>
        /// Clear all catch-up bookkeeping. Called in every early-exit of
        /// <see cref="CheckFollowTeleport"/> (respawn guards, suppression gate, owner not
        /// ready, in-radius), and after a teleport fires. Leaves the next catch-up session
        /// to re-stamp <see cref="_strandedSinceTime"/> + <see cref="_catchupStartDistance"/>
        /// fresh when distance crosses <see cref="maxFollowDistance"/> again.
        /// </summary>
        private void ResetCatchupState()
        {
            _strandedSinceTime      = -1f;
            _catchupStartDistance   = 0f;
            _catchupBestDistance    = 0f;
            _catchupInterruptIssued = false;
        }

        private void CheckFollowTeleport()
        {
            if (!isTamed || ownerPlayerId == 0) return;

            // Always consume throttle
            if (Time.time - _lastTeleportCheck < teleportCheckInterval) return;
            _lastTeleportCheck = Time.time;

            if (_companionAI == null || !_companionAI.ShouldBeFollowing) return;

            // === LAYER 1: SPAWN GATE + ULTRA-EARLY RESPAWN / LOADING GUARD ===
            // Covers in one place: lp==null, lp.IsDead, lp.InBed, lp.IsSleeping, AND
            // PlayerSpawnGate.IsReadyForCustomDataWrite (the authoritative "is the player
            // safe to mutate?" predicate shared with VaultPatches and every other ZDO writer).
            // If any of these is true we are NOT allowed to touch follow state — bail without
            // incrementing _consecutiveTeleports (so glitch-recovery cannot fire during a
            // legitimate respawn window) and reset catch-up so a long death/login fade-out
            // doesn't leave a stale 15-second catchup timer running underneath the gate.
            if (IsInRespawnOrLoadingState())
            {
                _consecutiveTeleports = 0;
                ResetCatchupState();
                return;
            }

            // === LAYER 2: Global patch suppression
            // Closed by Player.OnDeath / OnSpawned / long-jump TeleportTo postfixes, and
            // self-re-armed inside AreCompanionTeleportsSuppressed while IsTeleporting / !CanMove.
            if (CompanionPatches.AreCompanionTeleportsSuppressed())
            {
                _consecutiveTeleports = 0;
                ResetCatchupState();
                return;
            }

            // Animation block — sitting in a chair, mid-attack swing, frozen by effect, etc.
            // Resetting catch-up here matters: a companion that sits while stranded would
            // otherwise wake up with a long-expired CATCHUP_WINDOW behind it and immediately
            // teleport instead of starting a fresh walk-back attempt.
            if (IsInAnimationThatBlocksTeleport())
            {
                _consecutiveTeleports = 0;
                ResetCatchupState();
                return;
            }

            // Owner lookup (now protected by the two layers above)
            var owner = GetOwner();
            if (owner == null)
            {
                // BELT-AND-BRACES re-check the spawn gate — Layer 1 should have caught any
                // not-ready state, but if we got here with owner==null AND the gate just
                // flipped, refuse to mutate follow state. Reset catch-up too: the brief
                // owner-lookup gap during a death/respawn should not poison the next
                // CATCHUP_WINDOW after the player comes back.
                if (Player.m_localPlayer == null ||
                    !PlayerSpawnGate.IsReadyForCustomDataWrite(Player.m_localPlayer))
                {
                    _consecutiveTeleports = 0;
                    ResetCatchupState();
                    return;
                }

                _consecutiveTeleports++;
                if (_consecutiveTeleports >= MAX_CONSECUTIVE_TELEPORTS)
                {
                    Debug.LogWarning($"[CompanionController] {companionName} can't find owner after {_consecutiveTeleports} attempts - pausing follow until owner returns (vault state preserved)");
                    if (_companionAI != null)
                    {
                        _companionAI.SetShouldFollow(false);
                        _companionAI.SetStayPosition(transform.position);
                    }
                    _consecutiveTeleports = 0;
                }
                return;
            }

            // Final safety net
            if (!IsOwnerRespawnReady(owner))
            {
                _consecutiveTeleports = 0;
                ResetCatchupState();
                return;
            }

            // === Normal teleport logic starts here ===
            Vector3 ownerPos = owner.transform.position;
            float distance = Vector3.Distance(transform.position, ownerPos);

            // Inside the follow radius → reset all catch-up state. Pathing continues to keep the
            // companion close via the AI's normal Following state; we don't intervene.
            if (distance <= maxFollowDistance)
            {
                ResetCatchupState();
                return;
            }

            float timeSinceTeleport = Time.time - _lastTeleportTime;

            // Owner velocity sample (used to skip teleports while the owner is portal-jumping).
            float ownerSpeed = 0f;
            if (_lastOwnerPositionTime > 0f)
            {
                float timeDelta = Time.time - _lastOwnerPositionTime;
                if (timeDelta > 0.01f)
                    ownerSpeed = Vector3.Distance(ownerPos, _lastOwnerPosition) / timeDelta;
            }
            if (Time.time - _lastOwnerPositionTime >= OWNER_VELOCITY_CHECK_INTERVAL)
            {
                _lastOwnerPosition = ownerPos;
                _lastOwnerPositionTime = Time.time;
            }

            // === CATCH-UP PHASE ENTRY ===
            // First tick past maxFollowDistance: stamp the entry time + start/best distance.
            if (_strandedSinceTime < 0f)
            {
                _strandedSinceTime    = Time.time;
                _catchupStartDistance = distance;
                _catchupBestDistance  = distance;
                _catchupInterruptIssued = false;
            }
            // Track closest approach so we can tell if walking-back is working.
            if (distance < _catchupBestDistance)
                _catchupBestDistance = distance;

            // Interrupt-and-pursue: ONE-SHOT per catchup session, the moment we entered the phase.
            // SetFollowTarget snaps the AI FSM Idle→Following (drops wandering / resource-gather /
            // smelter-operator etc) so the companion starts pathing toward the owner immediately
            // instead of waiting for the next idle-behaviour cycle. Combat / Returning states are
            // intentionally NOT clobbered — the companion finishes its fight, then catches up.
            if (!_catchupInterruptIssued && _companionAI != null)
            {
                try { _companionAI.SetFollowTarget(owner.gameObject); }
                catch { }
                _catchupInterruptIssued = true;
            }

            // === TELEPORT DECISION ===
            // The "should we give up and teleport?" predicate:
            //   • hardStrand: owner ran > 2.5× the follow radius away → almost certainly a portal /
            //     dungeon entry / admin-fly. Teleport now; walking would never close it.
            //   • catchupWindowElapsed + notMakingProgress: we gave them 15 s to walk back and
            //     they haven't closed at least CATCHUP_PROGRESS_REQUIRED metres of ground. They're
            //     blocked (terrain, snag, lost path) → teleport.
            //   • catchupWindowElapsed + losingGround: distance is GROWING despite catchup. Owner
            //     is outpacing them → teleport.
            // Otherwise: return, keep letting them walk.
            bool hardStrand          = distance > maxFollowDistance * HARD_STRAND_MULTIPLIER;
            float catchupElapsed     = Time.time - _strandedSinceTime;
            bool catchupWindowElapsed = catchupElapsed >= CATCHUP_WINDOW;
            float progressMade        = _catchupStartDistance - _catchupBestDistance; // positive = closing
            bool notMakingProgress    = progressMade < CATCHUP_PROGRESS_REQUIRED;
            bool losingGround         = distance > _catchupStartDistance;

            bool isStranded = hardStrand
                           || (catchupWindowElapsed && notMakingProgress)
                           || (catchupWindowElapsed && losingGround);

            if (!isStranded)
                return;  // still attempting catchup on foot — no teleport this tick

            // Owner-velocity gate: skip teleport while the owner appears to be portal-jumping
            // (above human-running speed), UNLESS we ourselves just fired a portal teleport
            // on the previous tick — _portalTeleportPending is a single-use bypass marker set
            // by TeleportToPositionInternal(isPortal=true).
            //
            // FIX (prev. bug): the flag used to be cleared unconditionally on EVERY pass through
            // this point, including ticks where the speed gate wasn't triggered (owner moving
            // normally). That silently consumed the bypass before the tick that actually needed
            // it could see it. Now we only consume the flag on the tick that ACTUALLY uses it
            // to bypass the speed gate. On any tick where owner is moving normally, the flag
            // is left intact so it can still rescue a future "owner instantly accelerated"
            // portal-jump sample.
            if (ownerSpeed > OWNER_TELEPORT_MAX_SPEED)
            {
                if (!_portalTeleportPending)
                    return;          // owner is flying / portal-jumping, no bypass available — wait
                _portalTeleportPending = false;  // consume the bypass exactly here, where it was needed
            }

            // HARD THROTTLE — one teleport, then a 5-second cooldown before
            // we'll fire another, regardless of distance. The previous
            // implementation had a catastrophic-distance bypass that let us
            // fire every tick when the companion was stranded thousands of
            // meters away. That's exactly the visual "companion teleports in
            // for one frame, gets reverted, teleports in again" symptom — at
            // catastrophic distances the next CheckFollowTeleport tick fires
            // a brand-new teleport before the network has had time to
            // confirm whether the previous one stuck. With the bypass gone,
            // we trust the previous teleport for a full 5 seconds, and only
            // fire a retry if the companion genuinely failed to make it.
            const float TELEPORT_THROTTLE_TIME = 5f;
            if (timeSinceTeleport < TELEPORT_THROTTLE_TIME) return;

            // Loop detector removed in Phase 2. The legacy detector tracked
            // _consecutiveTeleports across teleport ticks and switched to
            // the cross-peer RPC after 3 failed local writes to break the
            // ownership-flap loop. With CompanionTeleportService routing
            // every stranded teleport server-authoritatively from the
            // first attempt, the loop scenario can no longer arise — the
            // server is the verified ZDO owner before it writes, so the
            // write sticks. _consecutiveTeleports is still maintained by
            // the owner-not-found branch above (different concern: pause
            // follow if we genuinely can't find the owner) but the
            // distance-based loop detector is gone.
            _consecutiveTeleports = 0;

            if (isStranded && _companionAI != null)
            {
                try
                {
                    _companionAI.OnTeleportedFar();
                    _companionAI.SetFollowTarget(owner.gameObject);
                    _combatMovement?.OnTeleportedFar();
                }
                catch { }
            }

            Debug.Log($"[CompanionController] {companionName} too far from owner ({distance:F0}m, stranded={isStranded}), teleporting...");
            _lastTeleportDistance = distance;
            _lastTeleportTime = Time.time;
            _ownerPosAtLastTeleport = ownerPos;
            ResetCatchupState();

            ReleaseAllMovementLocks();

            // STRANDED → fire the reconcile RPC. Server scans ZDOMan for
            // EVERY follower of this owner (not just this one companion)
            // and teleports any that are far from the player. The
            // distance gate inside the reconcile handler skips
            // companions that are already close, so even though we send
            // the bulk request the only ZDO that actually moves is this
            // one (assuming the owner's other followers are nearby —
            // which is the typical case for a single-companion stranded
            // event). Single RPC, single handler, no ownership flap.
            //
            // Non-stranded (small drift) keeps the local-write path
            // because ownership is stable for in-zone follows and the
            // local write makes the visual update instant.
            if (isStranded)
            {
                Core.CompanionTeleportService.RequestReconcileFollowers(owner);

                CompanionPatches.SuppressCompanionTeleportsUntil =
                    Time.unscaledTime + CompanionPatches.SUPPRESS_DURATION_STRANDED_SETTLE;

                _consecutiveTeleports = 0;
                ResetCatchupState();
                _lastTeleportTime = Time.time;
                _ownerPosAtLastTeleport = ownerPos;
                _companionAI?.ResetPathfindingState();
                return;
            }

            TeleportToOwner();
        }

        // ==================== NEW HELPER ====================
        // The respawn-window gate. We delegate to PlayerSpawnGate (the canonical
        // "is the local player safe to mutate right now?" predicate used by
        // VaultPatches and other ZDO/customData writers) so every guard in this
        // codebase agrees on what "ready" means. The previous hand-rolled checks
        // (IsDead / GetHealth / IsTeleporting / InBed / IsSleeping) missed the
        // post-OnSpawned-but-not-finalised window: the new player object exists,
        // IsDead=false, health>0, IsTeleporting=false — but m_nview / m_customData /
        // PlayerID haven't been wired up yet, so Player.GetAllPlayers() can return
        // a list that doesn't yet contain the respawned owner. That's how 19
        // companions all hit "can't find owner after 3 attempts" the instant a
        // respawn starts — Layer 1 wasn't catching the gap.
        private bool IsInRespawnOrLoadingState()
        {
            // Cheap pre-checks first (also covers IsDead — the local player's
            // dead body is still m_localPlayer for a moment, with m_dead=true).
            var lp = Player.m_localPlayer;
            if (lp == null) return true;
            try
            {
                if (lp.IsDead()) return true;
                if (lp.GetHealth() <= 0f) return true;
                if (lp.InBed() || lp.IsSleeping()) return true;
            }
            catch { return true; }

            // Authoritative readiness check. Mirrors what writers use, so a
            // companion that decides "owner is ready, look them up" is using
            // the same predicate as the code that's safe to write at that moment.
            return !PlayerSpawnGate.IsReadyForCustomDataWrite(lp);
        }

        /// <summary>
        /// HARDENED respawn protection. Covers the entire window from Player death
        /// until the player is fully awake at their bed (including "Local player destroyed").
        /// </summary>
        private bool IsOwnerRespawnReady(Player owner)
        {
            if (owner == null) return false;

            // Core death/teleport flags
            if (owner.IsDead() || owner.IsTeleporting()) return false;
            if (owner.GetHealth() <= 0f) return false;

            // ZDO / NetworkView must be fully valid and owned
            var nview = owner.m_nview;
            if (nview == null || !nview.IsValid() || !nview.IsOwner())
                return false;

            var zdo = nview.GetZDO();
            if (zdo == null || !zdo.IsValid())
                return false;

            // Extra Valheim states active during respawn / bed wake-up
            try
            {
                if (owner.InBed() || owner.IsSleeping()) return false;
                if (owner.IsAttached()) return false;

                // Reflection checks (safe if they don't exist)
                var isLoadingProp = typeof(Player).GetProperty("IsLoading", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (isLoadingProp != null && (bool)isLoadingProp.GetValue(owner) == true)
                    return false;

                var respawningField = typeof(Player).GetField("m_respawning", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (respawningField != null && (bool)respawningField.GetValue(owner) == true)
                    return false;
            }
            catch { /* Field may not exist - ignore */ }

            return true;
        }

        /// <summary>
        /// Checks if the companion is in an animation state that should block teleporting.
        /// This prevents teleport loops when companions are sitting, laying down, emoting, etc.
        /// </summary>
        private bool IsInAnimationThatBlocksTeleport()
        {
            // Check state controller first (cached in field)
            if (_stateController != null)
            {
                // If in a frozen state (sitting, emoting, UI), don't teleport
                if (_stateController.IsInFrozenState)
                {
                    return true;
                }

                // If animation is blocking, don't teleport
                if (_stateController.IsAnimationBlocking)
                {
                    return true;
                }
            }

            // Check interaction behavior for chair sitting
            var interactionBehavior = GetComponent<Interactions.CompanionInteractionBehavior>();
            if (interactionBehavior != null && interactionBehavior.IsAttached)
            {
                return true;
            }

            // Check idle behavior for emotes
            var idleBehavior = GetComponent<CompanionIdleBehavior>();
            if (idleBehavior != null && idleBehavior.IsEmoteFrozen)
            {
                return true;
            }

            // Check if character is in an attack animation
            if (_character != null && _character.InAttack())
            {
                return true;
            }

            return false;
        }

        private void CheckRespawn()
        {
            if (!isDefeated || _isRespawning) return;
       if (ownerPlayerId == 0) return;

       if (Time.time - defeatedTime >= respawnDelay)
            {
 var owner = GetOwner();
       if (owner != null)
        {
        Debug.Log($"[CompanionController] {companionName} respawning near owner after defeat");
        Respawn();
        }
        }
 }

        /// <summary>
        /// Emergency recovery from a glitched teleport loop state.
        /// Uses the same respawn flow as death to ensure proper restoration from vault.
        /// 
        /// CRITICAL: This method does NOT save to vault before destruction!
        /// The companion is in a corrupted/glitched state - saving would overwrite good vault data.
        /// Instead, we use CompanionRespawnManager which properly restores from existing vault data.
        /// </summary>
        private void ForceRecoveryFromGlitch()
        {
            Debug.LogWarning($"[CompanionController] {companionName} IMMEDIATE GLITCH RECOVERY - using respawn manager (NOT saving corrupted state)");
            
            var owner = GetOwner();
            if (owner == null)
            {
                // No owner - just force stay mode as fallback
                Debug.LogWarning($"[CompanionController] {companionName} glitch recovery failed - no owner found, forcing stay mode");
                if (_companionAI != null)
                {
                    _companionAI.SetShouldFollow(false);
                    _companionAI.SetStayPosition(transform.position);
                }
                // DON'T save to vault - companion is in corrupted state
                return;
            }
            
            // Remember if we should be following after respawn — Phase 4:
            // resolver prefers roster, falls back to vault. Roster is the
            // authoritative source for follow-state intent now.
            bool wasFollowing = false;
            CompanionSaveData existingVaultData = null;

            try
            {
                existingVaultData = CompanionSavedDataResolver.ResolveByCompanionId(ownerPlayerId, companionId);
                if (existingVaultData != null)
                {
                    wasFollowing = existingVaultData.IsFollowing;
                    Debug.Log($"[CompanionController] Glitch recovery: Found save data for {companionName}, wasFollowing={wasFollowing}");
                }
                else
                {
                    // Fall back to AI state if no save data found
                    wasFollowing = _companionAI?.ShouldBeFollowing ?? false;
                    Debug.LogWarning($"[CompanionController] Glitch recovery: No save data found, using AI state wasFollowing={wasFollowing}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionController] Glitch recovery: Failed to read vault data: {ex.Message}");
                wasFollowing = _companionAI?.ShouldBeFollowing ?? false;
            }
            
            // Store identity data
            string savedCompanionId = companionId;
            string savedPrefabName = GetPrefabName();
            long savedOwnerId = ownerPlayerId;
            
            Debug.Log($"[CompanionController] Glitch recovery: Scheduling respawn via CompanionRespawnManager for {companionName}");
            
            // CRITICAL: Use CompanionRespawnManager to handle the respawn
            // This uses the SAME code path as death, ensuring proper vault restoration
            // Schedule with 0 delay for immediate respawn
            CompanionRespawnManager.Instance.ScheduleRespawn(
                savedCompanionId,
                savedOwnerId,
                savedPrefabName,
                0.1f, // Very short delay to allow destruction to complete
                ZDOID.None
            );
            
            // Mark this companion as defeated so OnDestroy doesn't save corrupted state
            isDefeated = true;
            isTamed = false; // CRITICAL: Prevent OnDestroy from saving to vault
            
            // Notify player
            if (owner == Player.m_localPlayer)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                    $"{GetDisplayName()} is recovering...");
            }
            
            // Now destroy the old glitched instance
            // Because isTamed is now false, OnDestroy won't save corrupted data to vault
            CompanionNetworkHelper.Destroy(gameObject);
        }
        
        /// <summary>
        /// Forcibly dismisses this companion, destroying it from the world.
        /// Used as an emergency recovery for completely stuck companions.
        /// The companion can be re-summoned from the Companion Roster.
        /// </summary>
        public void ForceDismiss()
        {
            Debug.LogWarning($"[CompanionController] {companionName} being force-dismissed by emergency recovery");

            // Clear follow state in AI and vault
            if (_companionAI != null)
            {
                _companionAI.SetShouldFollow(false);
            }

            // Owner explicitly dismissed — write persistent intent.
            SetPersistentFollowIntent(false);

            // Save to vault with IsFollowing = false so it won't auto-spawn next login
            SaveFollowStateToVault(false);

            // ROSTER MIRROR (Phase 3): mark Dismissed and capture snapshot
            // BEFORE destroying the live ZDO. The roster keeps the entry
            // (full snapshot retained) so the player can recall the
            // companion later from the roster screen — only the explicit
            // Remove button on that screen actually wipes the entry.
            try
            {
                var ownerForRoster = GetOwner();
                if (ownerForRoster != null)
                {
                    CompanionRosterWriter.OnDismissed(ownerForRoster, this);
                    // CORE DORMANT STORE (replaces KennelLifecycle.OnDismissed): retain the snapshot so
                    // the player can recall later. Seam is null-safe when no dormant provider exists.
                    StoreDormant(ownerForRoster.GetPlayerID(), FiresCore.Bridge.DormancyKind.Dismissed, 0L);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionController] Roster write failed for Dismiss (non-fatal): {ex.Message}");
            }

            // Notify player
            var owner = GetOwner();
            if (owner != null && owner == Player.m_localPlayer)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    $"{GetDisplayName()} has been dismissed. Use Companion Roster to re-summon.");
            }

            // Destroy the companion
            CompanionNetworkHelper.Destroy(gameObject);
        }

        /// <summary>
        /// Capture this companion's current state and write it to the Core dormant store via the
        /// provider-agnostic <see cref="FiresCore.Bridge.NpcDormancyBridge"/> seam. No-op when no
        /// dormant provider is registered. Single Core entry point for going dormant on dismiss and
        /// logout; death keeps its own inline capture because it also honours drop-on-death clearing.
        /// </summary>
        public bool StoreDormant(long playerId, FiresCore.Bridge.DormancyKind kind, long recallDeadlineUtcTicks)
        {
            if (playerId == 0L || !FiresCore.Bridge.NpcDormancyBridge.IsAvailable) return false;
            try
            {
                var snap = CaptureState();
                if (snap == null) return false;
                FiresCore.Bridge.NpcDormancyBridge.Store(playerId, new FiresCore.Bridge.DormantNpcEntry
                {
                    NpcId                  = snap.NpcId,
                    Kind                   = kind,
                    RecallDeadlineUtcTicks = recallDeadlineUtcTicks,
                    Snapshot               = snap,
                });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionController] StoreDormant failed ({kind}) for {companionName}: {ex.Message}");
                return false;
            }
        }

     #endregion

        #region Commands

        public void CommandFollow(Player player)
        {
            if (_companionAI == null || player == null) return;

            // CRITICAL: Release any movement locks from interaction or other sources
            // This fixes the bug where companions commanded via Shift+E interaction stay frozen
            ReleaseAllMovementLocks();

            // Set follow target - this sets _shouldFollow = true internally
            _companionAI.SetFollowTarget(player.gameObject);

            // Clear home position when following - on BOTH movement systems
            if (_combatMovement != null)
            {
                _combatMovement.ClearHomePosition();
            }
            
            // CRITICAL: Also clear home position on idle behavior
            // This ensures sub-behaviors know we're NOT in stay mode
            var idleBehavior = GetComponent<CompanionIdleBehavior>();
            if (idleBehavior != null)
            {
                idleBehavior.ClearHomePosition();
            }

            // Owner explicitly chose follow — write persistent intent.
            SetPersistentFollowIntent(true);

            SaveFollowStateToVault(true);

            // ROSTER MIRROR (Phase 3): persistent intent = Following.
            try
            {
                if (player != null)
                {
                    CompanionRosterWriter.OnFollowCommand(player, this);
                    // CORE DORMANT STORE (replaces KennelLifecycle.OnFollowCommand): the companion is
                    // live again, so clear any stale dormant copy. Idempotent + null-safe.
                    FiresCore.Bridge.NpcDormancyBridge.Remove(player.GetPlayerID(), companionId);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionController] Roster write failed for CommandFollow (non-fatal): {ex.Message}");
            }

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                $"{GetDisplayName()} is following you!");

            Debug.Log($"[CompanionController] {companionName} now following {player.GetPlayerName()}");
        }

        public void CommandStay()
        {
            CommandStay(transform.position);
        }

        /// <summary>
        /// Authoritative Stay command. Always go through this method (or the
        /// parameterless overload) when an owner-issued Stay should persist:
        /// it sets the runtime AI flag, the runtime stay anchor, the home
        /// positions on both movement systems, the persistent follow intent
        /// (vault + ZDO via <see cref="SetPersistentFollowIntent"/>), and
        /// the player-roster snapshot via <see cref="CompanionRosterWriter.OnStayCommand"/>.
        ///
        /// Skipping any of those writes will leave the persistent state out
        /// of sync with the runtime state — e.g. the death/respawn pipeline
        /// reads <c>CompanionVault</c> and would see <c>IsFollowing = true</c>
        /// even though the companion is currently in Stay mode, causing the
        /// respawned companion to walk back to the player instead of
        /// returning to its stay anchor.
        /// </summary>
        public void CommandStay(Vector3 stayPosition)
        {
            if (_companionAI == null) return;

            // CRITICAL: Release any movement locks from interaction or other sources
            // This fixes the bug where companions commanded via Shift+E interaction stay frozen
            ReleaseAllMovementLocks();

            // CRITICAL FIX: Explicitly set shouldFollow to false BEFORE SetStayPosition
            // This ensures the AI state machine properly transitions to Idle state
            // and releases any Following authority that was being held.
            // This was missing before and caused companions set to stay via Shift+E
            // to not enter proper idle movement state.
            _companionAI.SetShouldFollow(false);
            _companionAI.SetStayPosition(stayPosition);

            // Set home position for idle wandering radius - on BOTH movement systems
            // CompanionCombatMovement - for movement boundaries and combat leashing
            // CompanionIdleBehavior - for sub-behaviors (resource gathering, smelter, etc.)
            if (_combatMovement != null)
            {
                _combatMovement.SetHomePosition(stayPosition);
                // CRITICAL: Also set move destination so companion moves to the stay position
                // This matches what CompanionCommandSystem.CommandCompanionStay() does
                _combatMovement.SetMoveDestination(stayPosition);
            }

            // CRITICAL: Also set home position on idle behavior
            // This enables sub-behaviors (ResourceGathering, SmelterOperator) to work
            var idleBehavior = GetComponent<CompanionIdleBehavior>();
            if (idleBehavior != null)
            {
                idleBehavior.SetHomePosition(stayPosition);
                Debug.Log($"[CompanionController] Set home position on IdleBehavior for {companionName} at {stayPosition}");
            }

            // Owner explicitly chose stay — write persistent intent.
            SetPersistentFollowIntent(false);

            SaveFollowStateToVault(false);

            // ROSTER MIRROR (Phase 3): persistent intent = Staying. Capture
            // happens here AFTER home position is set on the movement
            // systems so the snapshot picks it up.
            try
            {
                var ownerForRoster = GetOwner();
                if (ownerForRoster != null) CompanionRosterWriter.OnStayCommand(ownerForRoster, this);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionController] Roster write failed for CommandStay (non-fatal): {ex.Message}");
            }

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                $"{GetDisplayName()} is staying here.");

            // Use chat helper for consistent feedback (matches hotkey behavior)
            CompanionChatHelper.QuickMessages.StayingHere(this);

            Debug.Log($"[CompanionController] {companionName} commanded to stay at {stayPosition}");
        }

        public void ToggleFollowMode(Player player)
        {
            if (_companionAI == null) return;

            if (_companionAI.GetFollowTarget() != null)
            {
                CommandStay();
            }
            else
            {
                CommandFollow(player);
            }
        }
        
        /// <summary>
        /// Releases all movement locks that may have been applied by interaction, UI, or other systems.
        /// Call this when the companion needs to start moving again (follow/stay commands, combat, etc.)
        /// This fixes the bug where companions commanded via Shift+E interaction stay frozen.
        /// </summary>
        private void ReleaseAllMovementLocks()
        {
            try
            {
                // 1. Release StateController locks and clear frozen state
                if (_stateController != null)
                {
                    _stateController.ReleaseMovementPriority("PlayerInteraction");
                    _stateController.ReleaseMovementPriority("UIInteraction");
                    _stateController.ReleaseMovementPriority("PlayerCommand");
                    
                    // Force reset any frozen/emote states
                    if (_stateController.IsInFrozenState || _stateController.IsAnimationBlocking)
                    {
                        _stateController.ForceReset();
                    }
                }
                
                // 2. Release CombatMovement locks
                if (_combatMovement != null)
                {
                    _combatMovement.UnlockMovement();
                    _combatMovement.ClearMoveDestination();
                }
                
                // 3. Clear any idle behavior freeze state
                var idleBehavior = GetComponent<CompanionIdleBehavior>();
                if (idleBehavior != null)
                {
                    idleBehavior.UnfreezeAfterInteraction();
                }
                
                // 4. Clear any interactable occupancy (chairs, etc.)
                if (_character != null)
                {
                    IdleBehaviors.InteractableOccupancyManager.ReleaseAllForOccupant(_character);
                }
                
                // 5. Force detach from any attachment point
                var interactionBehavior = GetComponent<Interactions.CompanionInteractionBehavior>();
                interactionBehavior?.ForceDetach();
                
                // 6. Clear AI destinations
                if (_companionAI != null)
                {
                    _companionAI.ClearIdleDestination();
                    _companionAI.ClearCommandDestination();
                }
                
                Debug.Log($"[CompanionController] Released all movement locks for {companionName}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionController] Error releasing movement locks: {ex.Message}");
            }
        }

        #endregion

        #region Taming

        /// <summary>
        /// Generates a unique companion ID based on timestamp and counter.
        /// Format: {timestamp_hex}_{counter}_{random}
        /// </summary>
        private static string GenerateUniqueCompanionId()
   {
            _idCounter++;
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
          int random = UnityEngine.Random.Range(1000, 9999);
            return $"C{timestamp:X}_{_idCounter}_{random}";

        }
        
        /// <summary>
        /// Gets the current realm (world/server) identifier.
        /// Used to ensure companions are only restored on the world where they were tamed.
        /// Returns 0 if ZNet is not available.
        /// </summary>
        public static long GetCurrentRealmId()
        {
            try
            {
                if (ZNet.instance != null)
                {
                    return ZNet.instance.GetWorldUID();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionController] Failed to get realm ID: {ex.Message}");
            }
            return 0;
        }

        public bool OnUseItem(Player player, ItemDrop.ItemData item)
        {
            if (player == null || item == null) return false;

            Debug.Log($"[CompanionController] OnUseItem: player={player.GetPlayerName()}, item={item.m_shared.m_name}, isTamed={isTamed}");

            if (isTamed) return false;

            // Reject items that aren't the taming-currency item type.
            string itemName = item.m_dropPrefab?.name ?? item.m_shared.m_name;
            bool isCorrectItem = itemName.IndexOf(tamingItemPrefab, StringComparison.OrdinalIgnoreCase) >= 0
                              || item.m_shared.m_name.IndexOf(tamingItemPrefab, StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isCorrectItem)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    $"{GetDisplayName()} wants {tamingItemAmount} {tamingItemPrefab}");
                return false;
            }

            // Both the use-item and E-interact paths share this helper so the
            // count/deduct/tame logic stays in one place. Walks the entire
            // inventory (including the VAInventory coin-purse slot if installed),
            // not just the active hotbar slot.
            return TryAutoTameWithInventoryItems(player);
        }

        /// <summary>
        /// Try to tame this companion using <see cref="tamingItemPrefab"/> from
        /// anywhere in the player's inventory — regular grid, equipment panel,
        /// VAInventory coin purse, etc. Walks every stack matched by prefab name
        /// (case-insensitive), deducts up to <see cref="tamingItemAmount"/> across
        /// stacks, and tames on success. Shows a center-screen reason message and
        /// returns false on shortfall.
        /// </summary>
        public bool TryAutoTameWithInventoryItems(Player player)
        {
            if (player == null) return false;
            if (isTamed) return false;

            var inventory = player.GetInventory();
            if (inventory == null) return false;

            // Collect candidate stacks by prefab name. Doing the prefab-name
            // match (rather than CountItems by m_shared.m_name) makes us
            // independent of localization and matches OnUseItem's existing
            // identifier.
            var candidates = new List<ItemDrop.ItemData>();
            int total = 0;
            foreach (var stack in inventory.GetAllItems())
            {
                if (stack == null) continue;
                string prefabName = stack.m_dropPrefab?.name;
                if (string.IsNullOrEmpty(prefabName)) continue;
                if (!string.Equals(prefabName, tamingItemPrefab, StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add(stack);
                total += stack.m_stack;
            }

            if (total < tamingItemAmount)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    $"Need {tamingItemAmount} {tamingItemPrefab} (have {total})");
                return false;
            }

            // Deduct. Drain stacks oldest-first; remove any that hit zero.
            int remaining = tamingItemAmount;
            foreach (var stack in candidates)
            {
                if (remaining <= 0) break;
                int take = Math.Min(stack.m_stack, remaining);
                stack.m_stack -= take;
                remaining -= take;
                if (stack.m_stack <= 0)
                {
                    inventory.RemoveItem(stack);
                }
            }

            // RemoveItem already triggers internal Inventory bookkeeping; the
            // m_stack mutations above will be picked up by the next UI redraw.
            // (We avoided calling Changed() because it isn't a method on the
            //  current Valheim Inventory class in this build.)

            TameCompanion(player);
            return true;
        }

           public void TameCompanion(Player owner)
          {
         if (owner == null) return;

      Debug.Log($"[CompanionController] Taming {companionName} to {owner.GetPlayerName()}");

      // Wild-faction gate: hostile wild companions (Bandit / Cultist) refuse to
      // be recruited regardless of currency or items offered. See
      // Docs/WILD_COMPANION_SPAWN_PLAN.md ?10 ? the design calls for bandits to
      // be kill-and-loot content, not recruit content. Neutrals (faction == 0)
      // fall through to the normal tame flow. If the ZDO has no
      // companion_wild_faction key at all (non-wild spawn: placed NPC, admin
      // spawn, etc.) we also fall through ? this gate only refuses things that
      // the WildCompanionDresser has positively tagged as hostile.
      if (_nview != null)
      {
        var factionZdo = _nview.GetZDO();
        if (factionZdo != null)
        {
          int factionInt = factionZdo.GetInt(
              FiresCore.Npc.WildSpawn.WildCompanionDresser.ZDO_FACTION, -1);
          if (factionInt > 0)
          {
            // Anything non-zero in our CompanionFaction enum is hostile by
            // definition (see CompanionFactionExtensions.IsHostileByDefault).
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
              $"{GetDisplayName()} refuses to join you!");
            Debug.Log($"[CompanionController] Refused recruitment: " +
                  $"{companionName} is hostile-faction (ZDO faction={factionInt})");
            return;
          }
        }
      }

      // Generate unique ID on taming if not already set
      if (string.IsNullOrEmpty(companionId))
    {
companionId = GenerateUniqueCompanionId();
   Debug.Log($"[CompanionController] Generated unique ID: {companionId}");
 }

            isTamed = true;
           ownerPlayerId = owner.GetPlayerID();
       isDefeated = false;

            // Apply low mass now that the companion is tamed so the player
            // can physically push it without the companion shoving back.
            GetComponent<CompanionPersonalSpaceEnforcer>()?.ApplyTamedMassIfNeeded();

           if (_character != null)
       {
           _character.SetTamed(true);

                // Flip faction/group to player team on tame so vanilla friendly-fire
                // gates protect them from player attacks. Wild companions ship with
                // m_faction=Dverger / m_group="" so they're hittable in the wild;
                // once tamed they should behave like every other player-team unit.
                _character.m_faction = Character.Faction.Players;
                var tamedHumanoid = _character as Humanoid;
                if (tamedHumanoid != null) tamedHumanoid.m_group = "player";
          }

       // Set tamed name on ZDO for health bar display
    if (_nview != null)
     {
  var zdo = _nview.GetZDO();
  if (zdo != null)
  {
        zdo.Set(ZDOVars.s_tamed, true);
           zdo.Set(ZDOVars.s_tamedName, GetDisplayName());
   }
       }
       
     // Update Character.m_name for proper display
            UpdateCharacterName(GetDisplayName());

  // Reconfigure AI for tamed behavior
 ConfigureCompanionAI();
 
            // Notify random loadout system that companion was tamed (clears wild behavior)
            var randomLoadout = GetComponent<CompanionRandomLoadout>();
            if (randomLoadout != null)
            {
                randomLoadout.OnTamed();
            }
            
            // Clear any home position from idle behavior (will be controlled by follow/stay commands)
            var idleBehavior = GetComponent<CompanionIdleBehavior>();
            if (idleBehavior != null)
            {
                idleBehavior.ClearHomePosition();
            }

   CommandFollow(owner);

         SaveToZDO();
           SaveCompanionToVault();

           // ROSTER MIRROR (Phase 3): first time this companion enters the
           // player's ownership. Stamps ServerWorldUid (never mutated again)
           // and creates the roster entry with FollowState=Following. Done
           // explicitly here in addition to CommandFollow's own roster write
           // to be robust against the early-return path inside CommandFollow
           // when _companionAI hasn't been wired up yet at tame time.
           try
           {
               CompanionRosterWriter.OnFirstOwned(owner, this);
           }
           catch (Exception ex)
           {
               UnityEngine.Debug.LogWarning($"[CompanionController] Roster OnFirstOwned write failed (non-fatal): {ex.Message}");
           }

    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
    $"{GetDisplayName()} is now your companion!");

Debug.Log($"[CompanionController] {companionName} (ID: {companionId}) successfully tamed by {owner.GetPlayerName()}");
  }

        public void AdminTame(Player player)
      {
  if (player == null) return;

      if (!IsPlayerAdmin(player))
     {
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
 "Admin privileges required!");
          return;
          }

            TameCompanion(player);
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
            $"[Admin] Force-tamed {GetDisplayName()}!");
    }

     #endregion

        #region Death/Respawn System

public void OnDefeated()
  {
   if (!isTamed) return;
        
            Debug.Log($"[CompanionController] {companionName} has been defeated!");

            // Trigger death handler - this will manage destruction and respawn
          _deathHandler.HandleDeath();
     }

 public void Respawn()
      {
    if (_isRespawning) return;
     _isRespawning = true;

     var owner = GetOwner();
            if (owner == null)
         {
    Debug.LogWarning($"[CompanionController] Cannot respawn {companionName} - owner not found");
_isRespawning = false;
     return;
       }

       Vector3 spawnPos = GetSpawnPositionNearPlayer(owner);
   transform.position = spawnPos;

        isDefeated = false;
            defeatedTime = 0f;

       if (_character != null)
            {
    _character.SetHealth(_character.GetMaxHealth());
          }

    SetDefeatedVisuals(false);

        if (_companionAI != null && owner != null)
            {
             _companionAI.SetFollowTarget(owner.gameObject);
   }

            _isRespawning = false;

  if (owner == Player.m_localPlayer)
            {
  MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
   $"{GetDisplayName()} has respawned!");
   }

            SaveToZDO();
     Debug.Log($"[CompanionController] {companionName} respawned at {spawnPos}");
        }

        private void SetDefeatedVisuals(bool defeated)
        {
          foreach (var renderer in GetComponentsInChildren<Renderer>(true))
 {
       renderer.enabled = !defeated;
            }

          foreach (var collider in GetComponentsInChildren<Collider>(true))
            {
     collider.enabled = !defeated;
            }
       
            var rb = GetComponent<Rigidbody>();
    if (rb != null)
            {
                if (defeated)
    {
  // Only set velocity BEFORE making kinematic (Unity 6 doesn't allow setting velocity on kinematic bodies)
  rb.linearVelocity = Vector3.zero;
  rb.isKinematic = true;
       }
                else
     {
         rb.isKinematic = false;
         }
    }

      if (_companionAI != null)
   {
      _companionAI.enabled = !defeated;
            }
   
    if (_character != null && defeated)
            {
   transform.position = new Vector3(transform.position.x, transform.position.y - 1000f, transform.position.z);
            }
        }

  private Vector3 GetSpawnPositionNearPlayer(Player player)
        {
        Vector3 basePos = player.transform.position;

        // DUNGEON / INTERIOR DETECTION (see GetSafeTeleportPosition)
        // Inside dungeons, ZoneSystem.GetGroundHeight returns the OUTSIDE
        // surface terrain Y instead of the dungeon floor.  Skip the ground
        // override entirely when the owner is at interior altitude.
        bool ownerAtInteriorAltitude = basePos.y > 1000f;

           for (int i = 0; i < 10; i++)
            {
     Vector2 randomOffset = UnityEngine.Random.insideUnitCircle * respawnRadius;
            Vector3 testPos = basePos + new Vector3(randomOffset.x, 0f, randomOffset.y);

            if (ownerAtInteriorAltitude)
            {
                // Use owner's Y directly inside dungeons.
                return testPos;
            }

         if (ZoneSystem.instance != null)
  {
   float groundHeight;
         if (ZoneSystem.instance.GetGroundHeight(testPos, out groundHeight))
      {
   testPos.y = groundHeight + 0.5f;
              return testPos;
         }
           }
       }

            return basePos + player.transform.forward * 3f + Vector3.up * 0.5f;
        }

        public void TeleportToOwner()
        {
            var owner = GetOwner();
            if (owner == null) return;
            Vector3 spawnPos = GetSafeTeleportPosition(owner);
            TeleportToPositionInternal(spawnPos, owner, isPortal: false);
            Debug.Log($"[CompanionController] {companionName} teleported to owner at {spawnPos}");
        }

        /// <summary>
        /// Teleports this companion to a specific destination (e.g., a portal exit).
        /// Uses the same safe procedure as TeleportToOwner() but a caller-supplied
        /// position instead of computing one near the owner. Used by portal jumps
        /// where the owner's transform hasn't moved yet when this is called.
        /// </summary>
        public void TeleportToDestination(Vector3 destination)
        {
            if (_companionAI == null || !_companionAI.ShouldBeFollowing) return;
            Vector3 spawnPos = GetSafeTeleportPositionNearPoint(destination);
            TeleportToPositionInternal(spawnPos, GetOwner(), isPortal: true);
            Debug.Log($"[CompanionController] {companionName} teleported to destination {spawnPos}");
        }

        /// <summary>
        /// SHARED safe-teleport procedure used by every teleport-to-owner / teleport-to-position
        /// callsite (login restore, respawn, portal jump, follow-stranded recovery).
        /// Guarantees identical: lock release, physics zero-out, AI/combat state reset,
        /// transform.position assignment, ZDO position sync, follow target reattach,
        /// and Everybody-RPC broadcast so all clients move the companion to the same spot.
        /// </summary>
        private void TeleportToPositionInternal(Vector3 spawnPos, Player owner, bool isPortal)
        {
            // 1. Release any animation/interaction locks that would block movement.
            ReleaseAllMovementLocks();

            // 2. Stop physics BEFORE the position change (kinematic body can't be moved otherwise).
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                if (rb.isKinematic) rb.isKinematic = false;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            // 3. Drop pre-teleport AI / combat / pathing state so the companion doesn't
            //    sprint back to a now-thousands-of-metres-away target the moment we land.
            if (_companionAI != null)
            {
                _companionAI.ClearIdleDestination();
                _companionAI.ClearCommandDestination();
                _companionAI.ResetPathfindingState();
                _companionAI.OnTeleportedFar();
            }
            if (_combatMovement != null)
            {
                _combatMovement.ClearMoveDestination();
                _combatMovement.ClearCommandPriority();
                _combatMovement.UnlockMovement();
                _combatMovement.OnTeleportedFar();
            }
            if (_character != null)
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }

            // 4. Move the transform.
            transform.position = spawnPos;

            // 5. Push the new position into the ZDO so other clients sync correctly.
            //
            // Three things have to happen for the position to actually stick on
            // a long-jump teleport, where the server's locally-cached ZDO copy
            // still holds the OLD position and is in the middle of broadcasting
            // it to every peer (including us) on its next sync tick:
            //
            //   a) Write the new position to our local ZDO (vanilla SetPosition).
            //   b) BUMP the ZDO's data revision. Vanilla SetPosition does NOT
            //      bump DataRevision (it only invalidates the sector cache),
            //      so without this our write would have the same version as
            //      the server's stale copy. The server's incoming sync would
            //      then look like a "new" update and overwrite our local
            //      write back to the OLD position — the exact silent revert
            //      the user observed. Bumping ensures our write is provably
            //      newer; any stale incoming sync gets rejected by ZDOMan's
            //      version comparison.
            //   c) ForceSendZDO so the bump + new position propagates to every
            //      peer on the very next sync tick instead of whenever the
            //      periodic broadcast happens to run.
            //
            // Together these win the race against the post-teleport ownership
            // flap window where the server momentarily still believes it owns
            // a stale snapshot of the companion.
            if (_nview != null && _nview.IsValid())
            {
                var zdoForWrite = _nview.GetZDO();
                if (zdoForWrite != null)
                {
                    zdoForWrite.SetPosition(spawnPos);
                    zdoForWrite.DataRevision++;     // (b) — beats stale incoming syncs
                    if (ZDOMan.instance != null)
                        ZDOMan.instance.ForceSendZDO(zdoForWrite.m_uid); // (c)
                }
            }

            // 6. Re-attach the follow target so the AI knows where to keep tracking.
            if (owner != null && _companionAI != null && _companionAI.ShouldBeFollowing)
                _companionAI.SetFollowTarget(owner.gameObject);

            // 7. Portal teleports: tell the next CheckFollowTeleport tick to skip the
            //    owner-velocity gate (the owner's huge apparent speed IS the portal jump).
            if (isPortal)
                _portalTeleportPending = true;

            // 8. Broadcast to every other client so they apply the SAME spawnPos
            //    (without recalculating, which would desync). The owner client of
            //    this ZDO has already moved the transform above and skips this RPC
            //    via the IsOwner() check inside RPC_TeleportToPosition.
            if (_nview != null)
                _nview.InvokeRPC(ZNetView.Everybody, "RPC_TeleportToPosition", spawnPos);
        }

        /// <summary>
        /// Finds a safe spawn position near the given world point using the same
        /// obstacle / water / ground-height logic as GetSafeTeleportPosition.
        /// </summary>
        private Vector3 GetSafeTeleportPositionNearPoint(Vector3 center)
        {
            // DUNGEON / INTERIOR DETECTION.
            //
            // GetGroundHeight ignores the input Y and returns the surface terrain
            // height for the XZ. If the player is inside a dungeon, that surface
            // value is wildly different from the player's actual Y, and snapping
            // to it dumps the companion on the outside ground while the player
            // is inside the dungeon — which then triggers the 911m / 944m
            // catastrophic-distance teleport loop (vertical mismatch only).
            //
            // The previous heuristic was `center.y > 1000f` — works for vanilla
            // Burial Chambers / Sunken Crypts (Y > 4000) but fails for custom
            // dungeons placed at lower altitudes (the user's custom content sits
            // around Y=900). Replace with a comparison against the actual local
            // terrain: if the input Y is meaningfully ABOVE the local heightmap
            // (more than 50m), treat it as interior and skip the snap. That
            // catches dungeons at any altitude AND tall structures (towers,
            // Yggdrasil branches, ship masts) without false positives at
            // surface-level steep terrain.
            bool destinationAtInteriorAltitude;
            if (ZoneSystem.instance != null
                && ZoneSystem.instance.GetGroundHeight(center, out float centerGroundHeight))
            {
                destinationAtInteriorAltitude = (center.y - centerGroundHeight) > 50f;
            }
            else
            {
                // Heightmap not loaded for this XZ — fall back to the legacy
                // absolute threshold but lower it to catch sub-1000m dungeons.
                destinationAtInteriorAltitude = center.y > 200f;
            }

            Vector3[] offsets =
            {
                new Vector3(-2f, 0f,  0f),
                new Vector3( 2f, 0f,  0f),
                new Vector3( 0f, 0f, -2f),
                new Vector3( 0f, 0f,  2f),
                new Vector3(-1.5f, 0f, -1.5f),
                new Vector3( 1.5f, 0f, -1.5f),
                new Vector3(-1.5f, 0f,  1.5f),
            };

            foreach (var offset in offsets)
            {
                Vector3 testPos = center + offset;

                if (!destinationAtInteriorAltitude && ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetGroundHeight(testPos, out groundHeight))
                        testPos.y = groundHeight + 0.5f;
                    else
                        continue;
                }

                if (testPos.y < center.y - 5f) continue;

                // Water never exists in dungeon interiors; the API may also
                // return junk values for altitudes far above the world.
                if (!destinationAtInteriorAltitude)
                {
                    try
                    {
                        WaterVolume wv = null;
                        if (Floating.GetWaterLevel(testPos, ref wv) > testPos.y) continue;
                    }
                    catch { }
                }

                if (!Physics.CheckSphere(testPos + Vector3.up * 0.5f, 0.3f,
                    LayerMask.GetMask("static_solid", "Default")))
                    return testPos;
            }

            return center + Vector3.up * 0.5f;
        }

        /// <summary>
        /// Gets a safe teleport position near the owner.
        /// Tries multiple positions to find one that:
        /// 1. Has valid ground
        /// 2. Is not in water/lava
        /// 3. Is reachable (no walls in the way)
        /// 4. Is close enough to the owner
        /// </summary>
        private Vector3 GetSafeTeleportPosition(Player owner)
        {
            Vector3 ownerPos = owner.transform.position;
            Vector3 ownerForward = owner.transform.forward;

            // DUNGEON / INTERIOR DETECTION
            // Vanilla dungeons (Crypts, Burial Chambers, Sunken Crypts, Mountain Caves,
            // Frost Caves, Mistlands fortresses, Ashlands fortresses) place the
            // interior at Y > ~3000.  ZoneSystem.GetGroundHeight ignores the input
            // Y and returns the OUTSIDE world terrain Y for the same XZ, so using
            // it inside a dungeon produces a position at Y?30 (surface) while the
            // player is at Y?5000.  Detect this and skip the ground-height
            // override entirely — just use the owner's actual Y.
            bool ownerAtInteriorAltitude = ownerPos.y > 1000f;

            // Try positions in a spiral pattern around the owner
            // Start with positions behind/beside the owner (safer than in front)
            Vector3[] offsets = new Vector3[]
            {
                -ownerForward * 2f,                           // Behind
                -ownerForward * 2f + owner.transform.right * 1.5f,  // Behind-right
                -ownerForward * 2f - owner.transform.right * 1.5f,  // Behind-left
                owner.transform.right * 2.5f,                 // Right
                -owner.transform.right * 2.5f,                // Left
                -ownerForward * 3f,                           // Further behind
                ownerForward * 2f,                            // In front (last resort)
            };

            foreach (var offset in offsets)
            {
                Vector3 testPos = ownerPos + offset;

                // Get ground height — but ONLY for outdoor / surface positions.
                // Dungeon interiors must use the owner's Y as-is.
                if (!ownerAtInteriorAltitude && ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetGroundHeight(testPos, out groundHeight))
                    {
                        testPos.y = groundHeight + 0.5f;
                    }
                    else
                    {
                        continue; // No valid ground - skip this position
                    }
                }

                // Check if position is too far below owner (cliff/pit).
                // Doubles as a sanity check against any leaked surface-Y.
                if (testPos.y < ownerPos.y - 5f)
                {
                    continue;
                }

                // Check if position is in water (use Floating API).  Water never
                // exists in dungeon interiors so skip the test there.
                if (!ownerAtInteriorAltitude)
                {
                    try
                    {
                        WaterVolume waterVolume = null;
                        float waterLevel = Floating.GetWaterLevel(testPos, ref waterVolume);
                        if (waterLevel > testPos.y)
                        {
                            continue; // Underwater - skip
                        }
                    }
                    catch { }
                }

                // Check for obstacles between owner and spawn point
                Vector3 rayStart = ownerPos + Vector3.up * 1f;
                Vector3 rayDir = (testPos + Vector3.up * 1f) - rayStart;
                if (Physics.Raycast(rayStart, rayDir.normalized, rayDir.magnitude, LayerMask.GetMask("Default", "static_solid", "terrain")))
                {
                    continue; // Obstacle in the way - skip
                }

                // This position looks good!
                return testPos;
            }

            // Fallback to the old method if no good position found
            return GetSpawnPositionNearPlayer(owner);
        }

        #endregion

        #region RPCs

     private void RPC_SetCompanionName(long sender, string name)
        {
   companionName = name;
            SaveToZDO();
        }

   private void RPC_SetOwner(long sender, long newOwner)
        {
     ownerPlayerId = newOwner;
  SaveToZDO();
        }

private void RPC_TameCompanion(long sender)
     {
 isTamed = true;
     if (_character != null)
   {
    _character.SetTamed(true);
          }
        SaveToZDO();
        }

        private void RPC_Command(long sender, long commandType)
        {
    var player = Player.m_localPlayer;
            if (player == null) return;

  switch (commandType)
     {
     case 0: // Follow
              CommandFollow(player);
       break;
   case 1: // Stay
  CommandStay();
           break;
     }
        }

      /// <summary>
        /// LEGACY RPC - kept for backwards compatibility with old clients.
        /// New teleports use RPC_TeleportToPosition which includes the position.
        /// </summary>
        private void RPC_TeleportToOwner(long sender)
        {
            // Only process if we're NOT the owner (owner already did the teleport)
            if (_nview != null && _nview.IsOwner()) return;
            
            var owner = GetOwner();
            if (owner == null) return;
            
            Vector3 spawnPos = GetSpawnPositionNearPlayer(owner);
            transform.position = spawnPos;
            
            // Stop physics to prevent jittering
            var rb = GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
        
        /// <summary>
        /// RPC handler for teleporting to a specific position.
        /// CRITICAL MULTIPLAYER FIX: Uses server-provided position to ensure all clients sync.
        /// </summary>
        private void RPC_TeleportToPosition(long sender, Vector3 position)
        {
            // Skip the IsOwner bail intentionally. The owner has already
            // written transform.position locally inside TeleportToPositionInternal
            // before the broadcast, so re-applying the same value here is a
            // harmless no-op for the owner — but the bail used to lock us
            // out of fixing transform on the LOCAL CLIENT during the post-
            // long-jump ownership-flap window (the symptom: "loaded-and-
            // owned" sweep RPC handler runs server-side, server broadcasts
            // RPC_TeleportToPosition, local incorrectly thinks it's still
            // owner because ZDOMan ownership transition hadn't fully
            // propagated, handler bailed, transform stayed at OLD position).
            //
            // Apply unconditionally and trust the broadcasted value.

            // Use the position provided by the broadcaster (the actual ZDO
            // owner that ran the teleport) — don't recalculate.
            transform.position = position;

            // Stop physics to prevent jittering after teleport.
            var rb = GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            // Stop character movement.
            if (_character != null)
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }

            // AI / pathing cleanup on the RECEIVING side. Without this, the
            // local AI keeps thinking its destination is the pre-teleport
            // spot — companion lands at the new position and immediately
            // tries to walk back to the old one. The owner already runs
            // these cleanups inside TeleportToPositionInternal; mirror them
            // here so non-owner peers visualize a clean teleport instead of
            // a snap-and-walk-back.
            try
            {
                ReleaseAllMovementLocks();
                if (_companionAI != null)
                {
                    _companionAI.ClearIdleDestination();
                    _companionAI.ClearCommandDestination();
                    _companionAI.ResetPathfindingState();
                    _companionAI.OnTeleportedFar();
                }
                if (_combatMovement != null)
                {
                    _combatMovement.ClearMoveDestination();
                    _combatMovement.UnlockMovement();
                    _combatMovement.OnTeleportedFar();
                }
            }
            catch { /* never break the RPC handler on AI cleanup edge cases */ }

            if (FiresLogger.VerboseEnabled)
                Debug.Log($"[CompanionController] {companionName} received teleport RPC to {position}");
        }

        private void RPC_Respawn(long sender)
        {
      Respawn();
}

        #endregion

      #region Persistence

        public void SaveToZDO()
 {
       // Skip the entire save during local player respawn / loading-screen window —
       // ZDO writes (and the cascading SaveToZDO calls on consumables/inventory/skills)
       // during IsTeleporting=true deadlock the zone stream. Live in-memory state is
       // unchanged; the next caller post-respawn picks up the latest values.
       if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;

       var zdo = _nview?.GetZDO();
            if (zdo == null) return;

            zdo.Set("companion_id", companionId);
            zdo.Set("companion_name", companionName);
            zdo.Set("companion_displayname", displayNameOverride ?? "");
            zdo.Set("companion_tamed", isTamed);
            zdo.Set("companion_owner", ownerPlayerId);
            zdo.Set("companion_defeated", isDefeated);
            zdo.Set("companion_defeatedtime", defeatedTime);
            zdo.Set("companion_canfight", canFight);
            zdo.Set("companion_aggrorange", aggroRange);

            // PERSISTENT FOLLOW INTENT
            // companion_wasfollowing represents the OWNER'S explicit choice.  Only
            // CommandFollow / CommandStay / ForceDismiss / SetFollowMode (owner-driven
            // paths) are allowed to write it — see SetPersistentFollowIntent().
            //
            // SaveToZDO is called by every routine save (inventory change, equipment
            // change, idle ticks, etc.) and used to overwrite this key from the
            // RUNTIME _companionAI.ShouldBeFollowing flag.  But that runtime flag is
            // legitimately flipped to false by transient situations (owner not yet
            // loaded, glitch recovery, etc.) — saving those values clobbered the
            // persistent intent and caused companions to forget they were following
            // across death/logout.  We deliberately do NOT touch the key here now.

            // Read the persisted intent so the rest of this function (home / stay
            // serialization) reflects the owner's choice, not transient runtime.
            bool shouldFollow = zdo.GetBool("companion_wasfollowing", false);

            // Save home position for companions in "Stay" mode
            var idleBehavior = GetComponent<CompanionIdleBehavior>();
            if (idleBehavior != null && idleBehavior.HasHomePosition && !shouldFollow)
            {
                var homePos = idleBehavior.GetHomePosition();
                zdo.Set("companion_hashome", true);
                zdo.Set("companion_homepos", homePos);
            }
            else
            {
                zdo.Set("companion_hashome", false);
            }

             // CRITICAL: Set the tamed name for health bar display
            // This is what EnemyHud reads to show the name above the character
            string displayName = GetDisplayName();
            zdo.Set(ZDOVars.s_tamedName, displayName);
     
            // Also update the Character.m_name field for other systems
            UpdateCharacterName(displayName);

            // Also save consumables state
   _consumables?.SaveToZDO();
 
       // Save inventory data
            _inventory?.SaveToZDO();
          
      // Save skills
            _skills?.SaveToZDO();

            // Only log ZDO saves for tamed companions when verbose is enabled
            if (isTamed && FiresLogger.VerboseEnabled)
                Debug.Log($"[CompanionController] Saved to ZDO: {companionName} (ID: {companionId}), tamed={isTamed}, owner={ownerPlayerId}, following={shouldFollow}");
        }

        public void LoadFromZDO()
   {
            var zdo = _nview?.GetZDO();
   if (zdo == null) return;

            // CHECKPOINT: log entry to LoadFromZDO so we can pinpoint where a
            // hard client freeze (no managed exception, log truncates mid-spawn)
            // hands. Gated behind FiresLogger.VerboseEnabled — was
            // unconditional but the wild-companion zone-stream pattern fires
            // this 5-30 times per minute as the player moves through populated
            // areas, which dwarfs the "freeze investigation" use case. Turn on
            // VerboseLogging in config when actually debugging a freeze.
            if (FiresLogger.VerboseEnabled)
                Debug.Log($"[CompanionController.LoadFromZDO] ENTER zdoUid={zdo.m_uid} owner={zdo.GetLong("companion_owner", 0L)} prefabHash={zdo.GetPrefab()}");

    companionId = zdo.GetString("companion_id", companionId);
            companionName = zdo.GetString("companion_name", companionName);
            displayNameOverride = zdo.GetString("companion_displayname", "");
       isTamed = zdo.GetBool("companion_tamed", false);
  ownerPlayerId = zdo.GetLong("companion_owner", 0);
            isDefeated = zdo.GetBool("companion_defeated", false);
        defeatedTime = zdo.GetFloat("companion_defeatedtime", 0f);
            canFight = zdo.GetBool("companion_canfight", true);
      aggroRange = zdo.GetFloat("companion_aggrorange", aggroRange);

            // Set taming cost based on spawn biome — only applies to untamed wild companions.
            // Tamed companions ignore tamingItemAmount (OnUseItem returns early if isTamed).
            if (!isTamed)
            {
                int spawnBiomeInt = zdo.GetInt("companion_spawn_biome", (int)Heightmap.Biome.None);
                tamingItemAmount = (Heightmap.Biome)spawnBiomeInt switch
                {
                    Heightmap.Biome.Meadows     => 100,
                    Heightmap.Biome.BlackForest => 150,
                    Heightmap.Biome.Swamp       => 200,
                    Heightmap.Biome.Mountain    => 300,
                    Heightmap.Biome.Plains      => 500,
                    Heightmap.Biome.Mistlands   => 999,
                    Heightmap.Biome.AshLands    => 1500,
                    Heightmap.Biome.DeepNorth   => 1500,
                    _                           => 100,  // fallback / unknown biome
                };
            }
     
    // Load follow state and apply to CompanionAI
         bool wasFollowing = zdo.GetBool("companion_wasfollowing", false);

         // CRITICAL: Only the owner's direct command (CommandFollow / CommandStay /
         // ForceDismiss / SetFollowMode / radial menu) is allowed to mutate the
         // persistent follow flag.  If the owner isn't in the game right now we still
         // skip restoring the runtime follow target this load (so we don't teleport
         // toward nothing), BUT we leave companion_wasfollowing alone in the ZDO so
         // the saved state is intact when the owner comes back.
         bool ownerPresent = true;
         if (wasFollowing && isTamed && ownerPlayerId != 0)
         {
             var owner = GetOwner();
             if (owner == null)
             {
                 Debug.Log($"[CompanionController] {companionName} was following but owner not in game right now - deferring follow restore (saved state preserved)");
                 ownerPresent = false; // skip the runtime follow restoration below
             }
         }

 if (string.IsNullOrEmpty(displayNameOverride))
   displayNameOverride = null;

                     // Sync with Valheim's tamed state
             if (_character != null && isTamed)
                     {
                         _character.SetTamed(true);

                        // Re-apply player faction/group on load. The wild prefab
                        // baseline is Dverger / m_group="" so a saved tamed
                        // companion that re-spawns from CompanionNpc_Wild would
                        // otherwise reset to non-player team and take friendly fire.
                        _character.m_faction = Character.Faction.Players;
                        var loadedHumanoid = _character as Humanoid;
                        if (loadedHumanoid != null) loadedHumanoid.m_group = "player";
            }

       // Update the display name on Character component and ZDO for health bar
     string displayName = GetDisplayName();
   UpdateCharacterName(displayName);
         
        // Ensure s_tamedName is set (may have been loaded from an older save)
if (isTamed)
      {
     zdo.Set(ZDOVars.s_tamedName, displayName);
       }

      // Also load consumables state
       _consumables?.LoadFromZDO();
   
            // Load inventory from ZDO first
       _inventory?.LoadFromZDO();
    
      // Check if equipment was loaded from ZDO - if not, try to restore from vault
     // But DON'T save back to vault during this restore to avoid overwriting good data
    if (isTamed && ownerPlayerId != 0 && _inventory != null)
            {
     if (!_inventory.HasAnyEquipmentLoaded())
            {
    Debug.Log($"[CompanionController] ZDO equipment empty for {companionName}, attempting vault restore...");
     TryRestoreEquipmentFromVault();
 }
            }
     
 // Load skills
  _skills?.LoadFromZDO();
 
                    // Reconfigure AI if tamed
             if (isTamed)
            {
              ConfigureCompanionAI();

              // Restore follow state.
              // wasFollowing reflects the persistent owner-set flag.
              // ownerPresent gates whether we actually start following this load —
              // if the owner isn't in the game we keep the persistent flag (handled
              // above) and just don't set a runtime follow target this load.
              if (wasFollowing && ownerPresent && _companionAI != null)
               {
                   // CRITICAL: Set BOTH the follow target AND the shouldFollow flag
                   // SetShouldFollow(true) is the persistent flag that survives combat/other states
                   _companionAI.SetShouldFollow(true);

                   var owner = GetOwner();
                   if (owner != null)
                   {
                       _companionAI.SetFollowTarget(owner.gameObject);
                   }
               }
               else if (wasFollowing && !ownerPresent)
               {
                   // Persistent flag says FOLLOW but the owner isn't loaded yet.
                   // DO NOT touch the AI at all — don't set ShouldFollow(false),
                   // don't call SetStayPosition.  Either of those would put the
                   // companion into stay-mode and silently override the saved
                   // follow intent.  The deferred-follow watcher in Update()
                   // (_pendingDeferredFollowRestore) flips the AI on as soon as
                   // GetOwner() actually returns the player.  Until then the
                   // companion just sits in whatever pose it loaded in — that's
                   // fine; doing NOTHING is the correct answer here.
                   _pendingDeferredFollowRestore = true;
               }
               else if (_companionAI != null)
              {
                  // Persistent flag is genuinely stay (owner-set) — apply it.
                  _companionAI.SetShouldFollow(false);

                  // CRITICAL: Restore stay position and home position for non-following companions
                  bool hasHome = zdo.GetBool("companion_hashome", false);
                  if (hasHome)
                  {
                      Vector3 homePos = zdo.GetVec3("companion_homepos", transform.position);

                      // Set stay position on CompanionAI
                      _companionAI.SetStayPosition(homePos);

                      // Set home position on idle behavior for sub-behaviors to work
                      var idleBehavior = GetComponent<CompanionIdleBehavior>();
                      if (idleBehavior != null)
                      {
                          idleBehavior.SetHomePosition(homePos);
                      }

                      // Also set on combat movement
                      if (_combatMovement != null)
                      {
                          _combatMovement.SetHomePosition(homePos);
                      }

                      Debug.Log($"[CompanionController] Restored stay position for {companionName} at {homePos}");
                  }
              }
                  }

            // Only log ZDO loads for tamed companions when verbose is enabled
            if (isTamed && FiresLogger.VerboseEnabled)
                Debug.Log($"[CompanionController] Loaded from ZDO: {companionName} (ID: {companionId}), tamed={isTamed}, owner={ownerPlayerId}, following={wasFollowing}");

            // CHECKPOINT: pair with the ENTER log above to bracket the
            // synchronous body of LoadFromZDO. Same gating as ENTER —
            // VerboseLogging flag, off by default.
            if (FiresLogger.VerboseEnabled)
                Debug.Log($"[CompanionController.LoadFromZDO] EXIT companionId={companionId} name={companionName} tamed={isTamed} owner={ownerPlayerId} wasFollowing={wasFollowing}");
        }

        /// <summary>
        /// Updates the <c>Character.m_name</c> field which drives EnemyHud's floating health-bar
        /// label, plus the matching <c>Humanoid.m_name</c> (some lookups read that one instead).
        ///
        /// <para>Made public so frontends outside Core (e.g. RPGMaker's StaticNpcInitializer)
        /// can keep the floating label in sync with the assigned display name without taking the
        /// full <see cref="SetDisplayName"/> path (which also persists CompanionController state
        /// — undesirable for static NPCs whose persistence is owned by NpcController).</para>
        /// </summary>
        public void UpdateCharacterName(string name)
        {
            if (_character == null || string.IsNullOrEmpty(name)) return;
  
            // Update Character.m_name via reflection since it may be read-only
       try
         {
    var nameField = typeof(Character).GetField("m_name", 
          System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (nameField != null)
        {
        nameField.SetValue(_character, name);
      }
            }
            catch (Exception ex)
            {
       Debug.LogWarning($"[CompanionController] Failed to update Character.m_name: {ex.Message}");
  }
            
   // Also update Humanoid.m_name if present
       if (_humanoid != null)
            {
     try
         {
       var nameField = typeof(Humanoid).GetField("m_name", 
  System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (nameField != null)
            {
           nameField.SetValue(_humanoid, name);
           }
             }
           catch { }
     }
     }

      /// <summary>
        /// Sets the companion's display name and syncs it to all systems (ZDO, Character, etc.)
        /// Call this when renaming a companion.
     /// </summary>
  public void SetDisplayName(string newName)
    {
         if (string.IsNullOrEmpty(newName)) return;
    
   displayNameOverride = newName;
    companionName = newName;
            
        // Sync to all systems
         UpdateCharacterName(newName);
      SaveToZDO();
 
      Debug.Log($"[CompanionController] Display name set to: {newName}");
        }

        /// <summary>
  /// Attempts to restore equipment from vault data when ZDO data is incomplete.
        /// This handles the case where companions despawn on logout and lose their ZDO equipment data.
        /// </summary>
        private void TryRestoreEquipmentFromVault()
  {
 if (!FiresCore.Bridge.CompanionVaultBridge.IsVaultAvailable() || ownerPlayerId == 0 || string.IsNullOrEmpty(companionId))
    {
     Debug.Log($"[CompanionController] Cannot restore from vault - missing prerequisites");
     return;
}

            // CHECKPOINT: bracket TryRestoreEquipmentFromVault. If a freeze repro
            // log shows ENTER without EXIT, the resolver / inventory restore is
            // the cause.
            Debug.Log($"[CompanionController.TryRestoreEquipmentFromVault] ENTER companionId={companionId} ownerPlayerId={ownerPlayerId}");

   try
         {
 // Phase 4: roster-first via resolver, vault as fallback.
 var savedData = CompanionSavedDataResolver.ResolveByCompanionId(ownerPlayerId, companionId);

            if (savedData == null)
         {
    Debug.Log($"[CompanionController] No save data found for companion {companionId}");
        return;
}

Debug.Log($"[CompanionController] Found save data for {companionName} with {savedData.EquipmentPrefabs?.Count ?? 0} equipment items");

  // Restore equipment from vault
           if (savedData.EquipmentPrefabs != null && savedData.EquipmentPrefabs.Count > 0)
     {
        foreach (var kvp in savedData.EquipmentPrefabs)
       {
       try
 {
       var slot = (CompanionInventory.EquipmentSlot)Enum.Parse(typeof(CompanionInventory.EquipmentSlot), kvp.Key);
    var quality = savedData.EquipmentQualities != null && savedData.EquipmentQualities.ContainsKey(kvp.Key) 
   ? savedData.EquipmentQualities[kvp.Key] : 1;
  
    _inventory.RestoreEquipmentFromVault(slot, kvp.Value, quality);
     Debug.Log($"[CompanionController] Restored equipment {slot} = {kvp.Value} (quality {quality})");
       }
  catch (Exception ex)
    {
     Debug.LogWarning($"[CompanionController] Failed to restore equipment slot {kvp.Key}: {ex.Message}");
                }
      }

           _inventory.RecalculateEquipmentBonusesPublic();
            Invoke(nameof(ApplyVisualEquipmentDelayed), 0.5f);
 }

   // Restore storage inventory from vault
   if (!string.IsNullOrEmpty(savedData.StorageInventoryData))
     {
    try
           {
    var storageInv = _inventory.GetStorageInventory();
         if (storageInv != null)
      {
 var pkg = new ZPackage(savedData.StorageInventoryData);
   storageInv.Load(pkg);
  Debug.Log($"[CompanionController] Restored storage inventory with {storageInv.GetAllItems().Count} items from vault");
    }
   }
             catch (Exception ex)
      {
      Debug.LogWarning($"[CompanionController] Failed to restore storage inventory: {ex.Message}");
       }
     }
         
  // Restore skills from vault
          if (!string.IsNullOrEmpty(savedData.SkillsData) && _skills != null)
    {
   _skills.RestoreSkillsFromVault(savedData.SkillsData);
     Debug.Log($"[CompanionController] Restored skills from vault for {companionName}");
}

            // Restore progression data from vault
            if (!string.IsNullOrEmpty(savedData.ProgressionData) && _progression != null)
            {
                _progression.RestoreProgressionFromVault(savedData.ProgressionData);
                Debug.Log($"[CompanionController] Restored progression from vault for {companionName}");
            }

            // Restore stats data from vault
            if (!string.IsNullOrEmpty(savedData.StatsData) && _stats != null)
            {
                _stats.RestoreStatsFromVault(savedData.StatsData);
                Debug.Log($"[CompanionController] Restored stats from vault for {companionName}");
            }

            // Restore kill tracker data from vault
            if (!string.IsNullOrEmpty(savedData.KillsData) && _killTracker != null)
            {
                _killTracker.RestoreKillsFromVault(savedData.KillsData);
                Debug.Log($"[CompanionController] Restored kill tracker from vault for {companionName}");
            }

 _inventory?.SaveToZDO();
  _skills?.SaveToZDO();
            _progression?.SaveToZDO();
            _stats?.SaveToZDO();
            _killTracker?.SaveToZDO();
       SaveToZDO();
     
        Debug.Log($"[CompanionController] Successfully restored companion data from vault for {companionName}");
   }
   catch (Exception ex)
  {
      Debug.LogWarning($"[CompanionController] TryRestoreEquipmentFromVault failed: {ex.Message}");
       }

            Debug.Log($"[CompanionController.TryRestoreEquipmentFromVault] EXIT companionId={companionId}");
        }

        private void ApplyVisualEquipmentDelayed()
    {
     _inventory?.ApplyVisualEquipment();
        }

        #region Core State Capture / Apply (persistence inversion)

        // CaptureState / ApplyState are the Core-shaped persistence entry points the companion
        // kennel (FiresCompanions) uses to store / recall an NpcSaveState blob. They wrap the
        // existing, battle-tested CompanionVault capture+restore and translate to/from the
        // world-agnostic FiresCore.Npc.NpcSaveState. RealmId and respawn-timer state are
        // deliberately not part of NpcSaveState (the kennel owns world-scoping; respawn state
        // lives on the live ZDO), so they are sourced / defaulted in the map below.

        /// <summary>Capture this companion's intrinsic state as a portable <see cref="NpcSaveState"/>.</summary>
        public NpcSaveState CaptureState()
        {
            var sd = CompanionVault.BuildSaveData(this);
            return sd == null ? null : ToNpcSaveState(sd);
        }

        /// <summary>Apply a portable <see cref="NpcSaveState"/> onto this companion (recall / spawn-from-roster).</summary>
        public void ApplyState(NpcSaveState state)
        {
            if (state == null) return;
            CompanionVault.RestoreCompanion(this, ToCompanionSaveData(state));
        }

        private static NpcSaveState ToNpcSaveState(CompanionSaveData sd)
        {
            return new NpcSaveState
            {
                NpcId = sd.CompanionId,
                PrefabName = sd.PrefabName,
                DisplayName = !string.IsNullOrEmpty(sd.DisplayNameOverride) ? sd.DisplayNameOverride : sd.CompanionName,
                OwnerPlayerId = sd.OwnerPlayerId,
                IsFollowing = sd.IsFollowing,

                IsStationed = sd.IsStationedAsNpc,
                StationedPositionX = sd.StationedPositionX,
                StationedPositionY = sd.StationedPositionY,
                StationedPositionZ = sd.StationedPositionZ,
                StationedRotationY = sd.StationedRotationY,
                AllowIdleWandering = sd.AllowIdleWandering,
                HasHomePosition = sd.HasHomePosition,
                HomePositionX = sd.HomePositionX,
                HomePositionY = sd.HomePositionY,
                HomePositionZ = sd.HomePositionZ,

                ModelIndex = sd.ModelIndex,
                HairStyle = sd.HairStyle,
                BeardStyle = sd.BeardStyle,
                HairColorR = sd.HairColorR, HairColorG = sd.HairColorG, HairColorB = sd.HairColorB,
                SkinColorR = sd.SkinColorR, SkinColorG = sd.SkinColorG, SkinColorB = sd.SkinColorB,
                EyeColorR = sd.EyeColorR, EyeColorG = sd.EyeColorG, EyeColorB = sd.EyeColorB,
                HasAppearanceData = sd.HasAppearanceData,
                Scale = sd.Scale, IsGiant = sd.IsGiant, IsDwarf = sd.IsDwarf,

                EquipmentPrefabs = sd.EquipmentPrefabs != null ? new Dictionary<string, string>(sd.EquipmentPrefabs) : new Dictionary<string, string>(),
                EquipmentQualities = sd.EquipmentQualities != null ? new Dictionary<string, int>(sd.EquipmentQualities) : new Dictionary<string, int>(),
                StorageInventoryData = sd.StorageInventoryData,
                SkillsData = sd.SkillsData,
                ProgressionData = sd.ProgressionData,
                StatsData = sd.StatsData,
                KillsData = sd.KillsData,
                LuckData = sd.LuckData,
            };
        }

        private static CompanionSaveData ToCompanionSaveData(NpcSaveState ns)
        {
            return new CompanionSaveData
            {
                CompanionId = ns.NpcId,
                CompanionName = ns.DisplayName,
                DisplayNameOverride = ns.DisplayName,
                PrefabName = ns.PrefabName,
                OwnerPlayerId = ns.OwnerPlayerId,
                IsFollowing = ns.IsFollowing,
                RealmId = GetCurrentRealmId(),

                IsStationedAsNpc = ns.IsStationed,
                StationedPositionX = ns.StationedPositionX,
                StationedPositionY = ns.StationedPositionY,
                StationedPositionZ = ns.StationedPositionZ,
                StationedRotationY = ns.StationedRotationY,
                AllowIdleWandering = ns.AllowIdleWandering,
                HasHomePosition = ns.HasHomePosition,
                HomePositionX = ns.HomePositionX,
                HomePositionY = ns.HomePositionY,
                HomePositionZ = ns.HomePositionZ,

                ModelIndex = ns.ModelIndex,
                HairStyle = ns.HairStyle,
                BeardStyle = ns.BeardStyle,
                HairColorR = ns.HairColorR, HairColorG = ns.HairColorG, HairColorB = ns.HairColorB,
                SkinColorR = ns.SkinColorR, SkinColorG = ns.SkinColorG, SkinColorB = ns.SkinColorB,
                EyeColorR = ns.EyeColorR, EyeColorG = ns.EyeColorG, EyeColorB = ns.EyeColorB,
                HasAppearanceData = ns.HasAppearanceData,
                Scale = ns.Scale, IsGiant = ns.IsGiant, IsDwarf = ns.IsDwarf,

                EquipmentPrefabs = ns.EquipmentPrefabs != null ? new Dictionary<string, string>(ns.EquipmentPrefabs) : new Dictionary<string, string>(),
                EquipmentQualities = ns.EquipmentQualities != null ? new Dictionary<string, int>(ns.EquipmentQualities) : new Dictionary<string, int>(),
                StorageInventoryData = ns.StorageInventoryData,
                SkillsData = ns.SkillsData,
                ProgressionData = ns.ProgressionData,
                StatsData = ns.StatsData,
                KillsData = ns.KillsData,
                LuckData = ns.LuckData,
            };
        }

        #endregion

    public void SaveCompanionToVault()
    {
        // Phase 6 (save refactor): vault is a debug mirror only. The
        // live ZDO is authoritative for in-world state and the
        // per-player roster is authoritative for follow / pending /
        // dismissed state. CompanionVault.FlushDebugMirror runs on a
        // periodic tick and rebuilds the vault JSON from those
        // sources, so per-event saves like this one are no longer
        // needed. Public method retained as a no-op for back-compat
        // with any callers we haven't migrated yet.
    }

    /// <summary>
    /// Phase 6: no-op. Persistent follow intent lives on the player's
    /// ZDO via PlayerFollowingRegistry; the per-player roster mirrors
    /// the high-level FollowState. The periodic vault flush picks the
    /// changes up automatically.
    /// </summary>
    private void SaveFollowStateToVault(bool isFollowing)
    {
        // intentionally empty — see SaveCompanionToVault
    }

        /// <summary>
        /// Sets the PERSISTENT follow intent.  Only owner-explicit commands
        /// (CommandFollow, CommandStay, ForceDismiss, SetFollowMode, vault restore
        /// on login, hammer-place adoption) are allowed to call this.
        ///
        /// The runtime <see cref="CompanionAI.ShouldBeFollowing"/> flag may be
        /// flipped on/off transiently for many reasons (owner not loaded yet,
        /// glitch recovery, mid-teleport), but the persistent intent stored here
        /// only changes when the player explicitly says so.  This is what survives
        /// player death and logout.
        /// </summary>
        public void SetPersistentFollowIntent(bool follow)
        {
            // AUTHORITATIVE STORAGE: the owner player's ZDO via PlayerFollowingRegistry.
            // The companion's own ZDO (companion_wasfollowing key) is also written as
            // a fallback for the load path (where the owner may not be loaded yet
            // when the companion respawns), but the player's ZDO is the source of
            // truth.  This survives logout / login intact because it's bundled into
            // the player's character save (.fch) — it cannot be clobbered by any
            // companion-side transient state.
            var owner = GetOwner();
            if (owner != null && !string.IsNullOrEmpty(companionId))
            {
                PlayerFollowingRegistry.SetFollowing(owner, companionId, follow);
            }

            // Mirror to the companion's own ZDO so the LOAD path still works when
            // the player isn't in game yet (e.g. the companion's prefab streams in
            // before the player's character spawns on initial login).
            var nview = _nview ?? GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            if (!nview.IsOwner()) nview.ClaimOwnership();

            var zdo = nview.GetZDO();
            if (zdo == null) return;

            zdo.Set("companion_wasfollowing", follow);
            Debug.Log($"[CompanionController] Persistent follow intent for {companionName} ({companionId}) ? {follow}");
        }

        /// <summary>
        /// Reads the PERSISTENT follow intent.  This is the owner's explicit
        /// choice, not the transient runtime AI flag.  Used by anything that
        /// cares about "should this companion be following me right now from a
        /// player-intent perspective" (vault save, post-respawn reel-in,
        /// post-login restore).
        ///
        /// Source of truth: the owner player's ZDO via PlayerFollowingRegistry.
        /// Fall back to the companion's own ZDO key only when the owner isn't
        /// loaded yet (early in login, before player.fch has streamed in).
        /// </summary>
        public bool GetPersistentFollowIntent()
        {
            // Check the player-side registry FIRST.  This is the runtime fast
            // path while the owner is in the world.
            var owner = GetOwner();
            if (owner != null && !string.IsNullOrEmpty(companionId))
            {
                if (PlayerFollowingRegistry.IsFollowing(owner, companionId))
                    return true;
                // NOTE: deliberately fall through to the companion-side mirror
                // even when the registry says false.  The registry lives on
                // the player ZDO, which Valheim DESTROYS on player death and
                // recreates on bed-respawn — meaning every player death wipes
                // every companion's registry entry.  If we returned false here
                // we'd lose follow-intent across player death and any
                // BuildSaveData call (e.g. companion dies AFTER the owner
                // respawned) would write IsFollowing=false to the vault, which
                // then drops the companion out of the group HUD (the HUD's
                // pending-respawn filter requires IsFollowing).  The companion-
                // side ZDO flag does survive the player respawn, so it's the
                // canonical persistent intent — checking it second means we
                // honour an explicit owner-issued "stop following" (which
                // SetPersistentFollowIntent(false) writes to BOTH stores) but
                // we don't get poisoned by a transient registry wipe.
            }

            var nview = _nview ?? GetComponent<ZNetView>();
            var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            return zdo?.GetBool("companion_wasfollowing", false) ?? false;
        }

  private string GetPrefabName()
        {
            var zdo = _nview?.GetZDO();
            if (zdo != null)
      {
        var prefabHash = zdo.GetPrefab();
   var prefab = ZNetScene.instance?.GetPrefab(prefabHash);
  if (prefab != null)
            return prefab.name;
    }
       return "CompanionNpc";
        }

      #endregion

        #region Helper Methods

        public string GetDisplayName()
 {
      if (!string.IsNullOrEmpty(displayNameOverride))
      return displayNameOverride;
  return companionName;
        }

        public Player GetOwner()
        {
            if (ownerPlayerId == 0) return null;
            if (IsInRespawnOrLoadingState()) return null;   

            foreach (var player in Player.GetAllPlayers())
            {
                if (player != null && player.GetPlayerID() == ownerPlayerId)
                    return player;
            }
            return null;
        }

        public bool IsOwner(Player player)
        {
       if (player == null) return false;
        return player.GetPlayerID() == ownerPlayerId;
        }

        public bool IsPlayerAdmin(Player player)
        {
            if (player == null) return false;
            if (ZNet.instance == null) return false;

            // Use Valheim's built-in admin check which properly resolves platform IDs
            return ZNet.instance.LocalPlayerIsAdminOrHost();
        }

        public bool IsFollowing
        {
            get { return _companionAI?.GetFollowTarget() != null; }
        }
        
        /// <summary>
        /// Returns true if the companion SHOULD be following, even if temporarily not
        /// (e.g., during combat engagement when follow target is cleared).
        /// Use this instead of IsFollowing for behavior decisions.
        /// </summary>
        public bool ShouldBeFollowing => _companionAI?.ShouldBeFollowing ?? false;

        /// <summary>
        /// Sets whether this companion should be following or staying.
        /// </summary>
        public void SetFollowMode(bool follow)
        {
            if (follow)
            {
                // CRITICAL: Always set the shouldFollow flag first
                // This ensures the companion will follow even if owner isn't found immediately
                if (_companionAI != null)
                {
                    _companionAI.SetShouldFollow(true);
                }
                
                var owner = GetOwner();
                if (owner != null)
                {
                    CommandFollow(owner);
                }
                else
                {
                    // Owner not found but flag is set - companion will find owner later
                    Debug.LogWarning($"[CompanionController] SetFollowMode(true) called but owner not found - shouldFollow flag set, will find owner later");
                    SetPersistentFollowIntent(true);
                    SaveFollowStateToVault(true);
                    SaveToZDO();
                }
            }
            else
            {
                CommandStay();
            }
        }

        public Character GetCharacter() => _character;
        public CompanionAI GetCompanionAI() => _companionAI;
        
        /// <summary>
        /// DEPRECATED: Returns null. Use GetCompanionAI() instead.
        /// Kept for backward compatibility during migration.
        /// </summary>
        [System.Obsolete("Use GetCompanionAI() instead. MonsterAI has been replaced with CompanionAI.")]
        public MonsterAI GetMonsterAI() => null;
        
        public Humanoid GetHumanoid() => _humanoid;
        public CompanionInventory GetInventory() => _inventory;
        public CompanionCombat GetCombat() => _combat;
        public CompanionSkills GetSkills() => _skills;
        public CompanionStats GetStats() => _stats;
        public CompanionProgression GetProgression() => _progression;
     public CompanionConsumables GetConsumables() => _consumables;
        public CompanionCombatMovement GetCombatMovement() => _combatMovement;
        public CompanionKillTracker GetKillTracker() => _killTracker;
        public CompanionStateController GetStateController() => _stateController;
        public ArchetypeController GetArchetypeController() => _archetypeController;
        public BehaviorCoordinator GetBehaviorCoordinator() => _behaviorCoordinator;
        public UnifiedMovementAuthority GetMovementAuthority() => _movementAuthority;
        public CompanionFormationController GetFormationController() => _formationController;

        public bool ShouldAllowDamage(HitData hit)
  {
       if (!isTamed) return true;
      if (allowNonOwnerDamage) return true;

  if (hit.m_attacker.IsNone()) return true;

     var attacker = ZNetScene.instance?.FindInstance(hit.m_attacker)?.GetComponent<Character>();
         if (attacker == null) return true;

            var attackerPlayer = attacker as Player;
            if (attackerPlayer != null && attackerPlayer.GetPlayerID() == ownerPlayerId)
            {
           return false;
 }

            return true;
        }

        public void OpenInventory(Player player)
        {
            if (_inventory == null || player == null) return;

            FiresCore.Bridge.NpcUiBridge.RaiseShowInventory(gameObject, player);
        }

        /// <summary>
        /// Ensures CharacterAnimEvent has a valid reference to the Character component.
        /// This is critical for vanilla attack animation events to work properly.
        /// CharacterAnimEvent.Awake() normally uses GetComponentInParent<Character>(),
        /// but this can fail if the component hierarchy wasn't set up correctly in the prefab.
        /// </summary>
        private void EnsureCharacterAnimEventReference()
        {
            if (_character == null) return;
            
            // Find CharacterAnimEvent - it's typically on the Visual child with the Animator
            var animEvent = GetComponentInChildren<CharacterAnimEvent>(true);
            if (animEvent == null)
            {
                // Try to add it if there's an Animator
                if (_animator != null)
                {
                    animEvent = _animator.gameObject.AddComponent<CharacterAnimEvent>();
                    Debug.Log($"[CompanionController] Added CharacterAnimEvent to {_animator.gameObject.name} at runtime");
                }
                else
                {
                    Debug.LogWarning($"[CompanionController] No Animator found for CharacterAnimEvent on {companionName}");
                    return;
                }
            }
            
            // Use reflection to verify and set the m_character reference
            try
            {
                var charField = typeof(CharacterAnimEvent).GetField("m_character", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                if (charField != null)
                {
                    var currentCharRef = charField.GetValue(animEvent) as Character;
                    
                    if (currentCharRef == null)
                    {
                        // The reference is null - set it manually
                        charField.SetValue(animEvent, _character);
                        Debug.Log($"[CompanionController] Set CharacterAnimEvent.m_character reference for {companionName}");
                    }
                    else if (currentCharRef != _character)
                    {
                        // Wrong reference - fix it
                        charField.SetValue(animEvent, _character);
                        Debug.Log($"[CompanionController] Fixed CharacterAnimEvent.m_character reference for {companionName}");
                    }
                }
                
                // Also ensure m_nview is set (CharacterAnimEvent needs it for RPC calls)
                var nviewField = typeof(CharacterAnimEvent).GetField("m_nview", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                if (nviewField != null && _nview != null)
                {
                    var currentNView = nviewField.GetValue(animEvent) as ZNetView;
                    if (currentNView == null)
                    {
                        nviewField.SetValue(animEvent, _nview);
                        Debug.Log($"[CompanionController] Set CharacterAnimEvent.m_nview reference for {companionName}");
                    }
                }
                
                // Ensure m_animator is set
                var animatorField = typeof(CharacterAnimEvent).GetField("m_animator", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                if (animatorField != null && _animator != null)
                {
                    var currentAnimator = animatorField.GetValue(animEvent) as Animator;
                    if (currentAnimator == null)
                    {
                        animatorField.SetValue(animEvent, _animator);
                        Debug.Log($"[CompanionController] Set CharacterAnimEvent.m_animator reference for {companionName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionController] Failed to ensure CharacterAnimEvent references: {ex.Message}");
            }
        }

        #endregion
    }

    /// <summary>
    /// Data structure for saving companion info to vault
    /// </summary>
    [Serializable]
    public class CompanionSaveData
    {
        public string CompanionId;
        public string CompanionName;
        public string DisplayNameOverride;
        public string PrefabName;
        public long OwnerPlayerId;
        public bool IsFollowing;
        
        // CRITICAL: Realm identifier to prevent cross-server companion contamination
        // This is the world UID from ZNet.instance.GetWorldUID()
        // Companions tamed on one server should NOT appear on a different server
        public long RealmId;
        
        // Stationed as NPC - companion should not be restored to player on login
        public bool IsStationedAsNpc;
        
        // Stationed NPC position and rotation - respawn at this location instead of near owner
        public float StationedPositionX;
        public float StationedPositionY;
        public float StationedPositionZ;
        public float StationedRotationY;
        public bool AllowIdleWandering;
        
        // Home position for companions in "Stay" mode (not following)
        // This is separate from StationedPosition which is for permanent NPC stations
        public bool HasHomePosition;
        public float HomePositionX;
        public float HomePositionY;
        public float HomePositionZ;
        
        // Appearance data (from CompanionRandomLoadout generation)
        public int ModelIndex;  // 0 = male, 1 = female
        public string HairStyle;
        public string BeardStyle;
        public float HairColorR;
        public float HairColorG;
        public float HairColorB;
        public float SkinColorR;
        public float SkinColorG;
        public float SkinColorB;
        public float EyeColorR;
        public float EyeColorG;
        public float EyeColorB;
        public bool HasAppearanceData;  // True if appearance data is present
        
        // Scale data for giant/dwarf companions
        public float Scale = 1.0f;      // Scale factor (0.5 to 1.3)
        public bool IsGiant;            // True if scale >= 1.15
        public bool IsDwarf;            // True if scale <= 0.65

      // Equipment prefab names and qualities for full persistence
        public Dictionary<string, string> EquipmentPrefabs = new Dictionary<string, string>();
 public Dictionary<string, int> EquipmentQualities = new Dictionary<string, int>();

  // Storage inventory as base64 ZPackage
  public string StorageInventoryData;
      
        // Skills data - maps skill type name to level and accumulator
        // Format: "SkillType:level:accumulator"
   public string SkillsData;
        
        // Progression data - level, XP, attributes
        // Format: "level|xp|unspent|kills|totalXp|str,spd,hp,end,int"
        public string ProgressionData;
        
        // Stats data - death count and other persistent stats
        public string StatsData;
        
        // Kill tracker data - kills, deaths, creature breakdown
        public string KillsData;
        
        // Luck data - the companion's luck stat for level scaling
        public string LuckData;
        
        // Respawn state - allows companion to respawn after logout during respawn timer
        public bool IsPendingRespawn;
        public float RespawnTimeRemaining; // Seconds remaining at time of save
        public long DeathTimestamp; // Unix timestamp of when companion died
    }
}
