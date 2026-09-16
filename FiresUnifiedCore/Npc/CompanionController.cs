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
    /// Controller for companion NPCs driven by CompanionAI, a BaseAI subclass with its own state machine (idle,
    /// following, combat, returning, fleeing) instead of MonsterAI's hidden state. Companions follow, fight and
    /// can be equipped, and die and respawn like players. Threats are prioritized by whether they target the
    /// owner, then proximity to the owner, then whether they block the owner's path or target the companion.
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
        private CompanionFacingAuthority _facingAuthority;
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
        private const float DeferredFollowCheckInterval = 0.5f; // seconds between owner-presence polls

        // RUN-BACK / SNAP STATE (the leash). All thresholds + the snap decision live in the single source of
        // truth FiresCore.Npc.Core.CompanionLeash; CheckFollowTeleport just drives the per-companion run-back
        // with the three progress fields below. Within CompanionLeash.LeashDistance the AI follows normally;
        // past it the companion drops combat/idle and runs back (CommitReturnToOwner), and only snaps (via the
        // server-authoritative reconcile) when CompanionLeash.ShouldSnap says running back won't work.
        private float _strandedSinceTime         = -1f;  // time run-back began (-1 = not leashed)
        private float _catchupStartDistance      = 0f;   // owner distance when the leash first engaged
        private float _catchupBestDistance       = 0f;   // closest we've reached since (run-back progress signal)
        // Leash distances/timing + the snap predicate live in FiresCore.Npc.Core.CompanionLeash (single source of truth).

        // GLITCH PREVENTION - Teleport loop detection
        private int _consecutiveTeleports = 0;
        private float _lastTeleportTime = 0f;
        private float _lastTeleportDistance = 0f;
        private Vector3 _ownerPosAtLastTeleport;   // owner position when we last triggered a teleport
        private const int MaxConsecutiveTeleports = 3;
        private const float TeleportLoopWindow = 10f;        // seconds in which we check for loops
        private const float TeleportSuccessThreshold = 10f;  // companion must be within this distance to count as success
        private const float LoopOwnerMovedThreshold = 8f;   // if owner moved this far since last teleport it is NOT a loop

        // POST-TELEPORT SETTLE WINDOW
        // After a successful teleport, suppress further pulls for this long even if
        // the companion drifts past maxFollowDistance again. Combat-induced drift
        // during active fights would otherwise re-pull every 3-5s, producing the
        // "teleport, vanish, teleport, vanish" loop the user observed and the
        // associated FPS spike from running OnTeleportedFar + GetSafeTeleportPosition
        // + RPC broadcast on multiple companions every few seconds.
        // The catastrophic-distance override below still kicks in for true
        // dungeon-entry / cross-map gaps.
        private const float PostTeleportSettle       = 6f;
        private const float CatastrophicDistanceMult = 4f;   // distance > maxFollow * this bypasses settle window

        // Set to true by TeleportToDestination so CheckFollowTeleport skips the owner-speed
        // block on the very next tick (portal teleports look like "owner is flying").
        private bool _portalTeleportPending;

        // GLITCH PREVENTION - Owner velocity tracking.
        // We track the owner's recent speed and refuse to teleport while they are
        // moving faster than OwnerTeleportMaxSpeed — that range is reserved for
        // admin-flight, portal jumps, and other instant-translation effects where
        // teleporting toward the owner would just produce a chase loop.
        // Normal walking / running / sprinting are all well below this threshold,
        // so the companion no longer requires the owner to come to a full stop.
        private Vector3 _lastOwnerPosition;
        private float _lastOwnerPositionTime;
        private const float OwnerStillSpeed          = 1.0f;  // legacy "stopped" threshold (telemetry only)
        private const float OwnerStillRequired       = 1.5f;  // legacy still-time gate (no longer required for normal teleports)
        private const float OwnerTeleportMaxSpeed   = 15f;   // m/s - above this we assume owner is flying / portal-jumping
        private const float OwnerVelocityCheckInterval = 0.5f; // how often to sample owner position


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
            if (_pendingDeferredFollowRestore && Time.time - _lastDeferredFollowCheck >= DeferredFollowCheckInterval)
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

            // Facing authority - SINGLE SOURCE OF TRUTH for body rotation (the rotational sibling of
            // UnifiedMovementAuthority). Facing writers (combat/bow/work/AI) request a look direction
            // through this instead of slamming transform.rotation directly; only one owns facing at a time.
            _facingAuthority = GetComponent<CompanionFacingAuthority>();
            if (_facingAuthority == null)
            {
                _facingAuthority = gameObject.AddComponent<CompanionFacingAuthority>();
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
            // Escort-scale leash: companions fight WITH and AROUND the owner. 40/100 let a chase drift
            // 40 m from the owner before the Returning state kicked in — "running off doing their own
            // thing". 15 m keeps every engagement inside the fight around the owner; the Returning
            // re-engage guard (immediate-threat only) already prevents leash yo-yo.
            _companionAI.maxChaseDistance = 30f;
            _companionAI.combatLeashDistance = 15f;
            _companionAI.giveUpTime = 15f;
            
            // Protection settings
            _companionAI.ownerProtectionRange = protectionRange;
            _companionAI.proactiveProtection = proactiveProtection;
            _companionAI.interceptPriority = 2f;
            
            // Follow settings - buffer zones. Tight run threshold so the companion runs to catch up the
            // moment it trails ~6m (base gaits walk=2/jog=4/run=7; a running owner outpaces the jog tier,
            // so a lenient threshold left it slow-walking and never catching up). Keep in sync with the
            // CompanionAI field defaults and CompanionPrefabManager.
            _companionAI.stopDistanceInner = 2f;
            _companionAI.stopDistanceOuter = 4f;
            _companionAI.walkDistanceOuter = 5f;
            _companionAI.runDistanceInner = 4f;
            _companionAI.runDistanceOuter = 6f;
            _companionAI.catchUpDistance = 12f;
            
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
// Static NPCs are tamed (Dverger faction) but must stay OWNERLESS — auto-adopting the nearest player as
// owner made them immune to that player's hits (the owner-damage block), so they could never be killed or
// respawn. Skip the auto-owner assignment for static placements.
if (isTamed && ownerPlayerId == 0 &&
    GetComponent<FiresCore.Npc.NpcMode.CompanionNpcModule>()?.isStaticPlacement != true)
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
                // Owner not resolvable from THIS peer's scene. This is a VISIBILITY condition — the owner is in
                // another/unloaded zone, or their Player object is briefly absent during a respawn / zone
                // transition — NEVER a signal that the player wants the companion to stop following. The follow
                // latch is owner-command-only (Stay / Dismiss), so we leave it completely alone here. We also
                // can't teleport locally (we don't know where the owner is); the server-authoritative leash
                // heartbeat in CompanionTeleportService — the one peer that always sees every player — reels this
                // follower in if it has genuinely drifted too far.
                ResetCatchupState();

                // Diagnostic ONLY (never disables follow): surface a sustained owner-lookup gap so the log shows
                // WHY a companion isn't teleporting. Logs once per sustained gap, not every tick.
                _consecutiveTeleports++;
                if (_consecutiveTeleports == MaxConsecutiveTeleports)
                    Debug.Log($"[CompanionController] {companionName}: owner not in local scene — follow latch preserved, awaiting server leash / owner return.");
                return;
            }

            // Final safety net
            if (!IsOwnerRespawnReady(owner))
            {
                _consecutiveTeleports = 0;
                ResetCatchupState();
                return;
            }

            // === Leash (single authority: FiresCore.Npc.Core.CompanionLeash) ===
            Vector3 ownerPos = owner.transform.position;
            float distance = Vector3.Distance(transform.position, ownerPos);

            // Within the leash radius: normal following. The AI keeps the companion close at its own gait and may
            // fight along the way; the tether does not intervene.
            if (distance <= Core.CompanionLeash.LeashDistance)
            {
                ResetCatchupState();
                return;
            }

            // Owner velocity sample (used to skip snapping while the owner is portal-jumping / admin-flying).
            float ownerSpeed = 0f;
            if (_lastOwnerPositionTime > 0f)
            {
                float timeDelta = Time.time - _lastOwnerPositionTime;
                if (timeDelta > 0.01f)
                    ownerSpeed = Vector3.Distance(ownerPos, _lastOwnerPosition) / timeDelta;
            }
            if (Time.time - _lastOwnerPositionTime >= OwnerVelocityCheckInterval)
            {
                _lastOwnerPosition = ownerPos;
                _lastOwnerPositionTime = Time.time;
            }

            // === LEASHED (past LeashDistance): drop everything and run back ===
            // Stamp the run-back entry (time + start/best distance) the first tick we cross the leash.
            if (_strandedSinceTime < 0f)
            {
                _strandedSinceTime    = Time.time;
                _catchupStartDistance = distance;
                _catchupBestDistance  = distance;
            }
            if (distance < _catchupBestDistance)
                _catchupBestDistance = distance;

            // Commit to returning EVERY leashed tick: drop combat + idle work and pursue the owner at full follow
            // speed. Unlike the old one-shot interrupt this keeps combat suppressed for the whole run-back, so a
            // fight cannot re-steal the companion while it is supposed to be sprinting home.
            CommitReturnToOwner(owner);

            // === SNAP (last resort) ===
            // Snap only when running back will not work: hit the SnapDistance ceiling, or had the full run-back
            // window without closing the gap (blocked / outpaced). Otherwise keep running back on foot.
            float secondsLeashed = Time.time - _strandedSinceTime;
            if (!Core.CompanionLeash.ShouldSnap(distance, _catchupStartDistance, _catchupBestDistance, secondsLeashed))
                return;

            // Do not snap while the owner is portal-jumping / admin-flying (chasing a teleporting owner just
            // loops), unless we ourselves just fired a portal teleport last tick (_portalTeleportPending bypass).
            if (ownerSpeed > Core.CompanionLeash.OwnerTooFastToSnap)
            {
                if (!_portalTeleportPending)
                    return;
                _portalTeleportPending = false;
            }

            // Throttle: one snap, then a settle cooldown before another.
            if (Time.time - _lastTeleportTime < Core.CompanionLeash.SnapThrottle)
                return;

            // SNAP = server-authoritative reconcile (the single teleport path). The server scans ZDOMan for every
            // follower of this owner and reels in any that are far, claiming ZDO ownership first so the write
            // cannot lose to closest-peer ownership flap. Drop combat, fire it, settle.
            CommitReturnToOwner(owner);
            Debug.Log($"[CompanionController] {companionName} can't close the gap ({distance:F0}m) - snapping to owner.");
            _lastTeleportDistance = distance;
            _lastTeleportTime = Time.time;
            _ownerPosAtLastTeleport = ownerPos;
            ResetCatchupState();
            ReleaseAllMovementLocks();

            Core.CompanionTeleportService.RequestReconcileFollowers(owner);
            CompanionPatches.SuppressCompanionTeleportsUntil =
                Time.unscaledTime + CompanionPatches.SUPPRESS_DURATION_STRANDED_SETTLE;
            _companionAI?.ResetPathfindingState();
        }

        /// <summary>
        /// Drop combat + idle work and dedicate the companion to returning to its owner at full follow speed —
        /// the run-back phase of the leash. Cheap to call every leashed tick: it reuses the post-teleport
        /// combat-drop plumbing (OnTeleportedFar clears the target, refreshes the combat-suppression window, and
        /// forces the FSM back to Following) so a fight cannot re-steal the body mid run-back. Does NOT teleport.
        /// </summary>
        private void CommitReturnToOwner(Player owner)
        {
            if (_companionAI == null || owner == null) return;
            try
            {
                _companionAI.OnTeleportedFar();
                _companionAI.SetFollowTarget(owner.gameObject);
                _combatMovement?.OnTeleportedFar();
            }
            catch { }
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
            var localPlayer = Player.m_localPlayer;
            if (localPlayer == null) return true;
            try
            {
                if (localPlayer.IsDead()) return true;
                if (localPlayer.GetHealth() <= 0f) return true;
                if (localPlayer.InBed() || localPlayer.IsSleeping()) return true;
            }
            catch { return true; }

            // Authoritative readiness check. Mirrors what writers use, so a
            // companion that decides "owner is ready, look them up" is using
            // the same predicate as the code that's safe to write at that moment.
            return !PlayerSpawnGate.IsReadyForCustomDataWrite(localPlayer);
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
                // Can't resolve the owner right now (not in this peer's scene). We do NOT force stay — the follow
                // latch is owner-command-only and must survive glitch recovery. Bail without saving corrupted
                // state; the companion keeps its follow intent and recovers on a later tick / reload (LoadFromZDO
                // restores follow from the sticky companion_wasfollowing flag).
                Debug.LogWarning($"[CompanionController] {companionName} glitch recovery deferred - owner not resolvable; follow intent preserved.");
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
            if (player == null) return;

            // PERSISTENT INTENT FIRST — this is a ZDO/registry write that needs only the player, NOT the
            // AI. CommandFollow runs at tame time when _companionAI may not be wired yet; the old
            // `_companionAI == null` early-return skipped this entirely, leaving companion_wasfollowing
            // FALSE forever. That single flag gates the server leash reel-in (TryReelIn) AND the login
            // follow-restore (LoadFromZDO), so a companion that looked like it was following was silently
            // never reeled in after a teleport and never re-followed after a relog.
            SetPersistentFollowIntent(true);
            SaveFollowStateToVault(true);

            // Roster mirror + stale-dormant clear also need only the player, so they must not sit behind
            // the AI guard either.
            try
            {
                CompanionRosterWriter.OnFollowCommand(player, this);
                // CORE DORMANT STORE: the companion is live again, so clear any stale dormant copy.
                FiresCore.Bridge.NpcDormancyBridge.Remove(player.GetPlayerID(), companionId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionController] Roster write failed for CommandFollow (non-fatal): {ex.Message}");
            }

            if (_companionAI == null)
            {
                // AI not wired yet (tame-time race). The intent is recorded, so the server leash and the
                // next load will both honour it; arm the deferred watcher so runtime follow engages as
                // soon as the AI and owner exist.
                _pendingDeferredFollowRestore = true;
                Debug.Log($"[CompanionController] CommandFollow: persistent follow intent recorded for {companionName} before AI was wired — deferred watcher will engage follow.");
                return;
            }

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

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                $"{GetDisplayName()} is following you!");

            Debug.Log($"[CompanionController] {companionName} now following {player.GetPlayerName()}");
        }

        public void CommandStay()
        {
            CommandStay(transform.position);
        }

        /// <summary>
        /// The owner's Stay command. It sets the AI flag, stay anchor, both movement systems' home positions, the
        /// persistent follow intent (<see cref="SetPersistentFollowIntent"/>) and the roster
        /// (<see cref="CompanionRosterWriter.OnStayCommand"/>) together; skipping any of them left a respawned
        /// companion walking back to the player instead of its anchor.
        /// </summary>
        public void CommandStay(Vector3 stayPosition)
        {
            // PERSISTENT INTENT FIRST — a ZDO/registry write that needs only this companion, not the AI.
            // Same failure mode as CommandFollow: the old `_companionAI == null` early-return could skip
            // the intent write entirely, leaving companion_wasfollowing TRUE on a companion the owner had
            // told to stay — so the server leash heartbeat would keep yanking it back to the player.
            SetPersistentFollowIntent(false);
            SaveFollowStateToVault(false);

            if (_companionAI == null)
            {
                Debug.Log($"[CompanionController] CommandStay: persistent stay intent recorded for {companionName} before AI was wired.");
                return;
            }

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

            // Deduct. Drain stacks oldest-first. Go through Inventory.RemoveItem
            // rather than writing m_stack directly: it decrements (or removes the
            // stack when it hits zero) AND fires Inventory.Changed, which both
            // refreshes the UI and lets VAInventory re-persist the coin purse's
            // over-cap stack. A raw m_stack write is invisible to that re-persist,
            // so spending from the purse would otherwise be refunded on the next
            // load/respawn (vanilla clamps the over-cap stack on Load).
            int remaining = tamingItemAmount;
            foreach (var stack in candidates)
            {
                if (remaining <= 0) break;
                int take = Math.Min(stack.m_stack, remaining);
                inventory.RemoveItem(stack, take);
                remaining -= take;
            }

            TameCompanion(player);
            return true;
        }

           public void TameCompanion(Player owner)
          {
         if (owner == null) return;

      Debug.Log($"[CompanionController] Taming {companionName} to {owner.GetPlayerName()}");

      // Wild-faction gate: hostile wild companions (Bandit / Cultist) refuse to
      // be recruited regardless of currency or items offered. See
      // Docs/WILD_COMPANION_SPAWN_PLAN.md ?10 - the design calls for bandits to
      // be kill-and-loot content, not recruit content. Neutrals (faction == 0)
      // fall through to the normal tame flow. If the ZDO has no
      // companion_wild_faction key at all (non-wild spawn: placed NPC, admin
      // spawn, etc.) we also fall through - this gate only refuses things that
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

      // IDENTITY COMPLETENESS (fixes "tamed before it got a name"): a wild companion's
      // name / appearance / gear are assigned by CompanionRandomLoadout on a ~0.5s delay after
      // spawn. Taming inside that window used to lock in a nameless companion — the loadout's own
      // isTamed guard then refuses to ever run. Force the wild loadout to finalize NOW, while
      // isTamed is still false so its guards allow it, so every tamed companion starts with a
      // complete, persistable identity (which the kennel mirror below then stores authoritatively).
      if (!HasCompleteIdentity())
      {
          var pendingLoadout = GetComponent<CompanionRandomLoadout>();
          if (pendingLoadout != null)
          {
              try { pendingLoadout.GenerateRandomLoadout(); }
              catch (Exception ex) { Debug.LogWarning($"[CompanionController] Pre-tame identity finalize failed: {ex.Message}"); }
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

            // Taming is a user action on THIS machine — claim the ZDO so the identity writes below
            // (and SaveToZDO) are authoritative. Without this, a non-owned write under broad
            // server-ownership transfer is overwritten by the owner's blank copy, and the tame
            // silently never sticks (the root of "companion lost its name/owner after death").
            if (_nview != null && _nview.IsValid() && !_nview.IsOwner())
                _nview.ClaimOwnership();

           if (_character != null)
       {
           _character.SetTamed(true);

                // Stay Dverger (attackable) even when tamed — making them Players would gate the owner
                // (and everyone) out of PvP companion battles. Owner + allied-side friendly-fire
                // protection is enforced in code (ShouldAllowDamage), not by faction.
                _character.m_faction = Character.Faction.Dverger;
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
       
            var body = GetComponent<Rigidbody>();
    if (body != null)
            {
                if (defeated)
    {
  // Only set velocity BEFORE making kinematic (Unity 6 doesn't allow setting velocity on kinematic bodies)
  body.linearVelocity = Vector3.zero;
  body.isKinematic = true;
       }
                else
     {
         body.isKinematic = false;
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
            var body = GetComponent<Rigidbody>();
            if (body != null)
            {
                if (body.isKinematic) body.isKinematic = false;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
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

            // Write the position, bump the data revision (SetPosition alone does not) and force-send, so on a
            // long jump the server's stale cached copy is provably older and cannot revert the teleport on its
            // next sync.
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
            // GetGroundHeight returns the surface height whatever the Y, so snapping an interior destination dumped
            // the companion outside the dungeon and set off the long-distance teleport loop. A destination well above
            // local terrain (dungeons at any altitude, towers, masts) is treated as interior and not snapped.
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
                        WaterVolume waterVolume = null;
                        if (Floating.GetWaterLevel(testPos, ref waterVolume) > testPos.y) continue;
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
            var body = GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
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
            var body = GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
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

        /// <summary>
        /// Persists EVERY companion sub-system (inventory + equipment, skills, progression, stats, kill
        /// tracker) plus core identity to the ZDO in one call. The vault/kennel restore path loads all of
        /// this into MEMORY but must then write it to the ZDO, or (a) remote clients read an empty ZDO
        /// (naked, level 1) and (b) the companion's own deferred LoadFromZDO reads the empty fields back
        /// and wipes the freshly-restored state. Mirrors the persist block in TryRestoreEquipmentFromVault.
        /// </summary>
        public void PersistAllToZDO()
        {
            _inventory?.SaveToZDO();
            _skills?.SaveToZDO();
            _progression?.SaveToZDO();
            _stats?.SaveToZDO();
            _killTracker?.SaveToZDO();
            SaveToZDO();
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
            // Like id/name above, tamed/owner keep the CURRENT field values when the ZDO keys are
            // absent. A restore path that set these in memory but couldn't complete its ZDO write
            // (fresh ZNetView not yet valid) used to get reset to untamed/unowned here — producing
            // the "recruit me for coins" / "belongs to someone else" blank companion.
       isTamed = zdo.GetBool("companion_tamed", isTamed);
  ownerPlayerId = zdo.GetLong("companion_owner", ownerPlayerId);
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

                        // Tamed companions stay Dverger (attackable) on load; owner/ally immunity is
                        // enforced in code (ShouldAllowDamage), not via Faction.Players.
                        _character.m_faction = Character.Faction.Dverger;
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
    
      // (Legacy per-load vault equipment-restore removed — the kennel identity restore below is the
      // single authoritative recovery path now that the companion store is the kennel, not the vault.)

 // Load skills
  _skills?.LoadFromZDO();

            // ── KENNEL RESTORE (authoritative identity recovery) ─────────────────────────────────────
            // A tamed companion whose world ZDO reloaded WITHOUT a finalized identity (no real name —
            // a pre-fix corrupt save, or a ZDO written before its kennel mirror ran) restores its OWN
            // name / loadout / appearance / stats from its kennel entry, keyed by companionId. This is
            // the recovery path the vault used to provide. SERVER-ONLY: the server ApplyStates and
            // re-persists to the world ZDO, which then syncs the recovered identity to every client.
            // Placed AFTER inventory + skills load so ApplyState is the final authority — nothing
            // reloads the empty ZDO over it. Keyed on "no real name" (the clear corruption signal); a
            // named companion's live ZDO stays authoritative for its current gear.
            if (isTamed && ownerPlayerId != 0L
                && ZNet.instance != null && ZNet.instance.IsServer()
                && FiresCore.Bridge.NpcDormancyBridge.IsAvailable
                && !string.IsNullOrEmpty(companionId)
                && !HasCompleteIdentity())
            {
                var kennelEntry = FiresCore.Bridge.NpcDormancyBridge.Get(ownerPlayerId, companionId);
                if (kennelEntry?.Snapshot != null && !string.IsNullOrEmpty(kennelEntry.Snapshot.DisplayName))
                {
                    Debug.Log($"[CompanionController] Tamed companion {companionId} loaded incomplete — restoring identity " +
                              $"'{kennelEntry.Snapshot.DisplayName}' from kennel (authoritative store).");
                    try
                    {
                        ApplyState(kennelEntry.Snapshot);
                        UpdateCharacterName(GetDisplayName());
                        SaveToZDO(); // persist the recovered identity back to the world ZDO
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CompanionController] Kennel identity restore failed for {companionId}: {ex.Message}");
                    }
                }
            }
 
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

            // CRITICAL: the floating EnemyHud label for a TAMED NPC reads Tameable.GetHoverName() →
            // ZDOVars.s_tamedName, NOT m_name. So renaming only updated the hover prompt, never the billboard.
            // Push the name onto s_tamedName (owner-side) so the floating name above the head actually changes.
            var nview = _character.m_nview;
            if (nview != null && nview.IsValid() && nview.IsOwner())
                nview.GetZDO()?.Set(ZDOVars.s_tamedName, name);
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
    var stack = savedData.EquipmentStacks != null && savedData.EquipmentStacks.ContainsKey(kvp.Key)
   ? savedData.EquipmentStacks[kvp.Key] : 1;

    _inventory.RestoreEquipmentFromVault(slot, kvp.Value, quality, stack);
     Debug.Log($"[CompanionController] Restored equipment {slot} = {kvp.Value} (quality {quality}, stack {stack})");
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
            var saveData = CompanionVault.BuildSaveData(this);
            return saveData == null ? null : ToNpcSaveState(saveData);
        }

        /// <summary>Apply a portable <see cref="NpcSaveState"/> onto this companion (recall / spawn-from-roster).</summary>
        public void ApplyState(NpcSaveState state)
        {
            if (state == null) return;
            // Dormancy restore calls this synchronously in the same frame as Instantiate — before
            // Start() has run InitializeCompanion(), so _inventory/_skills/_stats/_progression are
            // still null and RestoreInventory/RestoreProgressionSystems silently no-op: the companion
            // came back named but naked at level 1. Init is idempotent (_initialized guard), so make
            // sure the components exist before applying the snapshot.
            InitializeCompanion();
            CompanionVault.RestoreCompanion(this, ToCompanionSaveData(state));
        }

        private static NpcSaveState ToNpcSaveState(CompanionSaveData saveData)
        {
            return new NpcSaveState
            {
                NpcId = saveData.CompanionId,
                PrefabName = saveData.PrefabName,
                DisplayName = !string.IsNullOrEmpty(saveData.DisplayNameOverride) ? saveData.DisplayNameOverride : saveData.CompanionName,
                BaseName = saveData.CompanionName,
                OwnerPlayerId = saveData.OwnerPlayerId,
                IsFollowing = saveData.IsFollowing,

                IsStationed = saveData.IsStationedAsNpc,
                StationedPositionX = saveData.StationedPositionX,
                StationedPositionY = saveData.StationedPositionY,
                StationedPositionZ = saveData.StationedPositionZ,
                StationedRotationY = saveData.StationedRotationY,
                AllowIdleWandering = saveData.AllowIdleWandering,
                HasHomePosition = saveData.HasHomePosition,
                HomePositionX = saveData.HomePositionX,
                HomePositionY = saveData.HomePositionY,
                HomePositionZ = saveData.HomePositionZ,

                ModelIndex = saveData.ModelIndex,
                HairStyle = saveData.HairStyle,
                BeardStyle = saveData.BeardStyle,
                HairColorR = saveData.HairColorR, HairColorG = saveData.HairColorG, HairColorB = saveData.HairColorB,
                SkinColorR = saveData.SkinColorR, SkinColorG = saveData.SkinColorG, SkinColorB = saveData.SkinColorB,
                EyeColorR = saveData.EyeColorR, EyeColorG = saveData.EyeColorG, EyeColorB = saveData.EyeColorB,
                HasAppearanceData = saveData.HasAppearanceData,
                Scale = saveData.Scale, IsGiant = saveData.IsGiant, IsDwarf = saveData.IsDwarf,

                EquipmentPrefabs = saveData.EquipmentPrefabs != null ? new Dictionary<string, string>(saveData.EquipmentPrefabs) : new Dictionary<string, string>(),
                EquipmentQualities = saveData.EquipmentQualities != null ? new Dictionary<string, int>(saveData.EquipmentQualities) : new Dictionary<string, int>(),
                EquipmentStacks = saveData.EquipmentStacks != null ? new Dictionary<string, int>(saveData.EquipmentStacks) : new Dictionary<string, int>(),
                StorageInventoryData = saveData.StorageInventoryData,
                SkillsData = saveData.SkillsData,
                ProgressionData = saveData.ProgressionData,
                StatsData = saveData.StatsData,
                KillsData = saveData.KillsData,
                LuckData = saveData.LuckData,
                ArchetypeSkillsData = saveData.ArchetypeSkillsData,
            };
        }

        private static CompanionSaveData ToCompanionSaveData(NpcSaveState saveState)
        {
            return new CompanionSaveData
            {
                CompanionId = saveState.NpcId,
                // Round-trip both name fields distinctly. BaseName carries the underlying companionName;
                // DisplayName carries the effective shown name. If they differ, there was a rename override.
                // Old snapshots (no BaseName) fall back to DisplayName for both = prior behavior.
                CompanionName = !string.IsNullOrEmpty(saveState.BaseName) ? saveState.BaseName : saveState.DisplayName,
                DisplayNameOverride = (!string.IsNullOrEmpty(saveState.BaseName) && saveState.DisplayName != saveState.BaseName) ? saveState.DisplayName : "",
                PrefabName = saveState.PrefabName,
                OwnerPlayerId = saveState.OwnerPlayerId,
                IsFollowing = saveState.IsFollowing,
                RealmId = GetCurrentRealmId(),

                IsStationedAsNpc = saveState.IsStationed,
                StationedPositionX = saveState.StationedPositionX,
                StationedPositionY = saveState.StationedPositionY,
                StationedPositionZ = saveState.StationedPositionZ,
                StationedRotationY = saveState.StationedRotationY,
                AllowIdleWandering = saveState.AllowIdleWandering,
                HasHomePosition = saveState.HasHomePosition,
                HomePositionX = saveState.HomePositionX,
                HomePositionY = saveState.HomePositionY,
                HomePositionZ = saveState.HomePositionZ,

                ModelIndex = saveState.ModelIndex,
                HairStyle = saveState.HairStyle,
                BeardStyle = saveState.BeardStyle,
                HairColorR = saveState.HairColorR, HairColorG = saveState.HairColorG, HairColorB = saveState.HairColorB,
                SkinColorR = saveState.SkinColorR, SkinColorG = saveState.SkinColorG, SkinColorB = saveState.SkinColorB,
                EyeColorR = saveState.EyeColorR, EyeColorG = saveState.EyeColorG, EyeColorB = saveState.EyeColorB,
                HasAppearanceData = saveState.HasAppearanceData,
                Scale = saveState.Scale, IsGiant = saveState.IsGiant, IsDwarf = saveState.IsDwarf,

                EquipmentPrefabs = saveState.EquipmentPrefabs != null ? new Dictionary<string, string>(saveState.EquipmentPrefabs) : new Dictionary<string, string>(),
                EquipmentQualities = saveState.EquipmentQualities != null ? new Dictionary<string, int>(saveState.EquipmentQualities) : new Dictionary<string, int>(),
                EquipmentStacks = saveState.EquipmentStacks != null ? new Dictionary<string, int>(saveState.EquipmentStacks) : new Dictionary<string, int>(),
                StorageInventoryData = saveState.StorageInventoryData,
                SkillsData = saveState.SkillsData,
                ProgressionData = saveState.ProgressionData,
                StatsData = saveState.StatsData,
                KillsData = saveState.KillsData,
                LuckData = saveState.LuckData,
                ArchetypeSkillsData = saveState.ArchetypeSkillsData,
            };
        }

        /// <summary>
        /// Re-establishes stationed-NPC placement or the stay-mode home anchor after a kennel/dormancy
        /// restore. ApplyState → CompanionVault.RestoreCompanion did NOT do this (only the legacy
        /// RestoreCompanionFromVault path did), so on a dedi a "stay and guard" companion respawned at the
        /// owner and wandered off, and a stationed NPC lost its post. Mirrors the stationed/stay branches of
        /// CompanionRespawnManager. Follow intent is restored elsewhere (companion_wasfollowing + the
        /// follow-teleport pipeline), so this only covers the stationed + stay-home cases.
        /// </summary>
        public void RestoreStationingAndHome(CompanionSaveData saveData)
        {
            if (saveData == null) return;
            try
            {
                if (saveData.IsStationedAsNpc)
                {
                    var npcModule = GetComponent<FiresCore.Npc.NpcMode.CompanionNpcModule>()
                                    ?? gameObject.AddComponent<FiresCore.Npc.NpcMode.CompanionNpcModule>();
                    var pos = new Vector3(saveData.StationedPositionX, saveData.StationedPositionY, saveData.StationedPositionZ);
                    var rot = Quaternion.Euler(0f, saveData.StationedRotationY, 0f);
                    transform.position = pos;
                    transform.rotation = rot;
                    npcModule.allowIdleWandering = saveData.AllowIdleWandering;
                    npcModule.StationAtPosition(pos, rot);
                    npcModule.SaveToZDO();
                    return;
                }

                // Not stationed and NOT following → restore the stay-home anchor so a guarding companion
                // holds its post instead of running back to the owner. SaveToZDO (via PersistAllToZDO)
                // then writes companion_hashome/homepos from the idle behavior.
                if (!saveData.IsFollowing && saveData.HasHomePosition)
                {
                    var home = new Vector3(saveData.HomePositionX, saveData.HomePositionY, saveData.HomePositionZ);
                    var combatMovement = GetComponent<CompanionCombatMovement>();
                    if (combatMovement != null) { combatMovement.SetHomePosition(home); combatMovement.SetMoveDestination(home); }
                    var idle = GetComponent<CompanionIdleBehavior>();
                    if (idle != null) idle.SetHomePosition(home);
                    var ai = GetCompanionAI();
                    if (ai != null) { ai.SetShouldFollow(false); ai.SetStayPosition(home); }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionController] RestoreStationingAndHome failed for {companionName}: {ex.Message}");
            }
        }

        #endregion

    public void SaveCompanionToVault()
    {
        // KENNEL-AUTHORITATIVE persistence (post-vault decouple). This is the alive-save hook,
        // called at tame + every identity / gear / skill / stat change. It mirrors the companion's
        // current state into the Core kennel as its Alive entry, making the kennel the single
        // authoritative persistent companion store the way the old vault was — so a live companion
        // whose world ZDO reloads incomplete can restore its OWN name/loadout/items from it.
        //
        // The legacy vault (debug mirror) and m_customData roster are gone from this path.
        MirrorToKennel();
    }

    /// <summary>
    /// True when this companion has a finalized, real identity — a proper name, not the prefab /
    /// default placeholder written by CompanionPrefabManager before CompanionRandomLoadout dresses a
    /// wild spawn. Gates kennel mirroring (never store a nameless snapshot) and drives the load-time
    /// restore-from-kennel decision.
    /// </summary>
    public bool HasCompleteIdentity()
    {
        if (string.IsNullOrEmpty(companionName)) return false;
        switch (companionName)
        {
            case "CompanionNpc_Wild":
            case "CompanionNpc":
            case "Companion NPC":
            case "Companion":
                return false;
            default:
                return true;
        }
    }

    /// <summary>
    /// Upsert this companion's current snapshot into the kennel as its <see cref="DormancyKind.Alive"/>
    /// entry. Captures ONLY on the companion's ZDO owner (the machine whose live state is
    /// authoritative); CompanionKennel.Store handles transport — server-side it writes the kennel ZDO
    /// directly, client-side it forwards over routed RPC. This closes the window where a companion
    /// tamed and killed while client-owned never got an Alive kennel entry (the old server-only gate
    /// meant only the 20s server reconciler wrote one, and it skipped client-owned companions).
    /// No-op for untamed / unowned / defeated (the death handler owns the DeadPendingRespawn entry) /
    /// not-yet-dressed companions.
    /// </summary>
    internal void MirrorToKennel(bool backstopFillIn = false)
    {
        if (!isTamed || ownerPlayerId == 0L) return;
        if (isDefeated) return; // dead → death handler owns the DeadPendingRespawn entry
        if (!FiresCore.Bridge.NpcDormancyBridge.IsAvailable) return;
        if (!HasCompleteIdentity()) return; // never write a nameless placeholder

        // Capture only when THIS peer authoritatively OWNS the companion — the owner's live state is
        // fresh by definition; any other machine's copy can lag and would risk overwriting a good kennel
        // entry with stale data.
        if (_nview != null && !_nview.IsOwner())
        {
            // On a dedi the server is NOT the owner of a client-owned companion, so the per-companion
            // MirrorToKennel above never fires server-side — meaning the server backstop can't recover
            // if the client's per-event forward was dropped. As a SAFE backstop, fill in the kennel from
            // the server's ZDO-backed copy ONLY when there is NO entry at all: this recovers a missing
            // entry (the identity-loss case) without ever overwriting a good, fresher client entry.
            if (!backstopFillIn) return;
            if (FiresCore.Bridge.NpcDormancyBridge.Get(ownerPlayerId, companionId) != null) return;
            try
            {
                FiresCore.Npc.Persistence.CompanionKennel.CaptureAndStore(
                    ownerPlayerId, this, FiresCore.Bridge.DormancyKind.Alive, 0L);
                Debug.Log($"[CompanionController] Kennel backstop: filled missing Alive entry for {companionName} (client-owned, no prior entry).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionController] Kennel backstop failed for {companionName}: {ex.Message}");
            }
            return;
        }

        try
        {
            FiresCore.Npc.Persistence.CompanionKennel.CaptureAndStore(
                ownerPlayerId, this, FiresCore.Bridge.DormancyKind.Alive, 0L);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CompanionController] Kennel mirror failed for {companionName}: {ex.Message}");
        }
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
                // Fall through to the companion-side flag even when the registry says false: the registry lives on the
                // player ZDO, which is recreated when the player dies, so trusting it alone would drop follow intent
                // and hide companions from the group HUD after every owner death.
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

        // --- Effective level (vanilla-style stat scaling) -----------------------------------
        // ONE level source for base health/damage scaling, shared by CompanionStats and
        // CompanionEquipmentData so the two pipelines agree. Wild companions read their star
        // level (the dresser's Character.SetLevel(1+stars)); tamed companions GROW with their
        // progression level, banded into the same range so investment out-scales wild stars
        // without going absurd. Vanilla-steep: health x level, damage x(1+(level-1)*0.5).
        private const int TamedProgressionLevelsPerTier = 20;  // every N progression levels = +1 effective level
        private const int MaxEffectiveLevel = 6;               // cap (mirrors the 0..6 biome gear tiers)

        public int GetEffectiveLevel()
        {
            if (isTamed && _progression != null)
                return Mathf.Clamp(1 + (_progression.Level / TamedProgressionLevelsPerTier), 1, MaxEffectiveLevel);
            int starLevel = _character != null ? _character.GetLevel() : 1;
            return Mathf.Clamp(starLevel, 1, MaxEffectiveLevel);
        }

     public CompanionConsumables GetConsumables() => _consumables;
        public CompanionCombatMovement GetCombatMovement() => _combatMovement;
        public CompanionKillTracker GetKillTracker() => _killTracker;
        public CompanionStateController GetStateController() => _stateController;
        public ArchetypeController GetArchetypeController() => _archetypeController;
        public BehaviorCoordinator GetBehaviorCoordinator() => _behaviorCoordinator;
        public UnifiedMovementAuthority GetMovementAuthority() => _movementAuthority;
        public CompanionFacingAuthority GetFacingAuthority() => _facingAuthority;
        public CompanionFormationController GetFormationController() => _formationController;

        public bool ShouldAllowDamage(HitData hit)
        {
            if (!isTamed) return true;                              // wild / untamed → always damageable
            if (hit == null || hit.m_attacker.IsNone()) return true; // environmental → allow

            var attacker = ZNetScene.instance?.FindInstance(hit.m_attacker)?.GetComponent<Character>();
            if (attacker == null) return true;

            // Immune ONLY to our own owner's side: the owner, the owner's other companions, and
            // (when a party/guild supplies the hook) allied players + their companions. Every other
            // attacker — monsters AND other players' companions — can hit us, which is what makes
            // player-vs-player companion battles possible. allowNonOwnerDamage is a per-companion
            // master toggle (default true) for callers that want a fully invulnerable companion.
            long attackerOwnerId = ResolveOwnerSide(attacker);
            if (FiresCore.Bridge.NpcCompanionBridge.AreOwnersAllied(attackerOwnerId, ownerPlayerId))
                return false;

            return allowNonOwnerDamage;
        }

        // A player's "owner side" is themselves; a companion's is its owner; anything else is unowned (0).
        private static long ResolveOwnerSide(Character attacker)
        {
            if (attacker == null) return 0L;
            if (attacker is Player player) return player.GetPlayerID();
            var attackerCompanion = attacker.GetComponent<CompanionController>();
            return attackerCompanion != null ? attackerCompanion.ownerPlayerId : 0L;
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
        public Dictionary<string, int> EquipmentStacks = new Dictionary<string, int>();

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

        // Archetype skill XP ("skill:level:xp,...") - owner-gated ZDO save never fires server-side, so
        // this snapshot field is what carries it across a kennel-driven respawn.
        public string ArchetypeSkillsData;

        // Respawn state - allows companion to respawn after logout during respawn timer
        public bool IsPendingRespawn;
        public float RespawnTimeRemaining; // Seconds remaining at time of save
        public long DeathTimestamp; // Unix timestamp of when companion died
    }
}
