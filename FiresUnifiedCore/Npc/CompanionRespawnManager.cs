using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc.NpcMode;
using FiresCore.Npc.Vault;
using FiresCore.Lifecycle;

namespace FiresCore.Npc
{
    /// <summary>
    /// Singleton manager that handles companion respawning and player login restoration.
    /// Persists across scene loads and handles respawn timers.
    /// 
    /// KEY FEATURES:
    /// - Handles respawn timers for dead companions
    /// - On player login: Restores following companions (teleport if alive, spawn if dead/missing)
    /// - Prevents companions from being lost when player logs out during respawn timer
    /// </summary>
    public class CompanionRespawnManager : MonoBehaviour
    {
        private const float LoginRestoreDelay = 3f;
        private const float AppearanceRestoreDelay = 0.2f;
        private const float VaultSyncRetryDelay = 15f;
        private const float OwnerOfflineRetryDelay = 30f;
        private const float KennelSyncRetryDelay = 10f;
        private const float FailedRestoreRetryDelay = 30f;
        private const int SpawnPositionAttempts = 10;
        private const float SpawnScatterRadius = 5f;
        private const float FallbackSpawnForwardDistance = 3f;
        private const float SpawnHeightOffset = 0.5f;

        private static CompanionRespawnManager _instance;
        public static CompanionRespawnManager Instance
        {
  get
        {
            if (_instance == null)
  {
   var go = new GameObject("CompanionRespawnManager");
      _instance = go.AddComponent<CompanionRespawnManager>();
      DontDestroyOnLoad(go);
    }
       return _instance;
       }
   }

        // Pending respawns
        private Dictionary<string, RespawnData> _pendingRespawns = new Dictionary<string, RespawnData>();
        
        // Players we've already processed login for (to avoid duplicate processing)
        private HashSet<long> _processedPlayerLogins = new HashSet<long>();
        
        // Update interval
        private float _updateInterval = 1f;
        private float _lastUpdate;
     
        // Login check interval
        private float _loginCheckInterval = 2f;
        private float _lastLoginCheck;

        // Kennel-mirror reconciler interval. Server-side sweep that keeps every live owned
        // companion's authoritative kennel (Alive) entry current — the catch-all for companions
        // whose per-event MirrorToKennel was skipped because a CLIENT owned the ZDO (dedi taming).
        private float _kennelMirrorInterval = 20f;
        private float _lastKennelMirror;

        public static bool VerboseLogging = false;

        private class RespawnData
        {
            public string CompanionId;
            public long OwnerPlayerId;
            public string PrefabName;
            public float RespawnTime;
            public ZDOID TombstoneZDOID;
        }

  #region Unity Lifecycle

        private void Awake()
 {
   if (_instance != null && _instance != this)
    {
       Destroy(gameObject);
         return;
            }
            _instance = this;
    DontDestroyOnLoad(gameObject);
      }

        private void Update()
        {
          // Check pending respawns
  if (Time.time - _lastUpdate >= _updateInterval)
    {
    _lastUpdate = Time.time;
    CheckPendingRespawns();
         }
 
        // Check for player logins that need companion restoration
        if (Time.time - _lastLoginCheck >= _loginCheckInterval)
         {
           _lastLoginCheck = Time.time;
              CheckPlayerLogins();
      }

        // Keep the authoritative kennel entries for live owned companions current.
        if (Time.time - _lastKennelMirror >= _kennelMirrorInterval)
        {
            _lastKennelMirror = Time.time;
            MirrorLiveCompanionsToKennel();
        }
  }

        /// <summary>
        /// Server-side reconciler: mirror every live, owned, finalized companion into the kennel as
        /// its <see cref="FiresCore.Bridge.DormancyKind.Alive"/> entry so the kennel is the always-
        /// current authoritative store (the role the vault used to fill). This is the catch-all for
        /// companions whose per-event <see cref="CompanionController.MirrorToKennel"/> was skipped
        /// because a CLIENT owned the ZDO at the time (the normal case when a player tames a companion
        /// standing next to them on a dedicated server). Defeated companions are owned by the death
        /// handler's DeadPendingRespawn entry and skipped; not-yet-dressed wild spawns are skipped by
        /// the completeness gate inside MirrorToKennel.
        /// </summary>
        private void MirrorLiveCompanionsToKennel()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!FiresCore.Bridge.NpcDormancyBridge.IsAvailable) return;

            var all = CompanionController.AllCompanions;
            if (all == null) return;

            foreach (var companion in all)
            {
                if (companion == null) continue;
                if (!companion.isTamed || companion.ownerPlayerId == 0L) continue;
                // MirrorToKennel re-checks availability/defeated/completeness itself. backstopFillIn=true
                // lets it fill a MISSING kennel entry for a client-owned companion (the server isn't the
                // owner on a dedi) without ever overwriting a good, fresher client-forwarded entry.
                companion.MirrorToKennel(backstopFillIn: true);
            }
        }

      #endregion

#region Player Login Companion Restoration

        /// <summary>
        /// Checks for players who have logged in and need their companions restored.
        /// </summary>
        private void CheckPlayerLogins()
    {
            // Only run on server/host
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            // NOTE: this loop is the LOCAL-player login trigger and must keep running even when a
            // dormant store is present — the player-announced hook (RPC_CharacterID) that drives
            // CompanionRestoreService does NOT fire for the listen-host's own player, so without
            // this loop the kennel restore never runs in single-player. When the dormant store is
            // available, RestoreCompanionsForPlayer routes to the Core dormancy restore instead of
            // the legacy m_customData path (see there).

   var players = Player.GetAllPlayers();
          if (players == null) return;

            foreach (var player in players)
            {
    if (player == null) continue;
           
                long playerId = player.GetPlayerID();
           if (playerId == 0) continue;
                
     // Skip if we've already processed this player's login
     if (_processedPlayerLogins.Contains(playerId)) continue;
          
              // Mark as processed
     _processedPlayerLogins.Add(playerId);
          
              // Check and restore companions for this player
     StartCoroutine(RestoreCompanionsForPlayer(player));
      }
     }

        /// <summary>
        /// Called when a player logs out - remove them from processed list so we check again on next login.
        /// </summary>
        public void OnPlayerLogout(long playerId)
  {
          _processedPlayerLogins.Remove(playerId);
          _existingZdoCompanions.Clear();

      if (VerboseLogging)
   {
         Debug.Log($"[CompanionRespawnManager] Player {playerId} logged out - will check companions on next login");
            }
        }

        /// <summary>
        /// Restores all following companions for a player who just logged in.
        /// - If companion exists in world and is alive: teleport to player
        /// - If companion is dead or on respawn timer: spawn immediately at player
        /// - If companion doesn't exist but vault says it should be following: spawn at player
        /// </summary>
        private IEnumerator RestoreCompanionsForPlayer(Player player)
     {
            // Wait a moment for the world to fully load
            yield return new WaitForSeconds(LoginRestoreDelay);

            if (player == null) yield break;

            // Wait until the player's spawn gate is open before touching ZDOs or
            // m_customData. Writing while IsTeleporting=true deadlocks the loading screen.
            {
                const float GateTimeout = 35f;
                const float GatePollInterval = 0.25f;
                float gateStart = Time.realtimeSinceStartup;
                while (player != null
                       && !PlayerSpawnGate.IsReadyForCustomDataWrite(player)
                       && (Time.realtimeSinceStartup - gateStart) < GateTimeout)
                {
                    yield return new WaitForSeconds(GatePollInterval);
                }

                if (player == null) yield break;

                if (!PlayerSpawnGate.IsReadyForCustomDataWrite(player))
                {
                    Debug.LogWarning($"[CompanionRespawnManager] Player spawn gate did not open in time for {player.GetPlayerName()} — aborting restore");
                    yield break;
                }
            }
       
            long playerId = player.GetPlayerID();
            if (playerId == 0) yield break;

            // Kennel path: the Core dormant store owns restore. Now that the spawn gate is open,
            // run the unified adopt + dormancy pass for this local player and we're done — skip the
            // legacy m_customData restore below. This is the LOCAL-player trigger for the dormancy
            // restore (the RPC_CharacterID player-announced hook covers remote peers on a dedicated
            // server but never fires for the listen-host's own player). RestoreForPlayerServerSide
            // resolves the local player's position via its m_localPlayer fallback.
            if (FiresCore.Bridge.NpcDormancyBridge.IsAvailable)
            {
                CompanionRestoreService.RestoreForPlayerServerSide(playerId);
                yield break;
            }

            if (VerboseLogging)
        {
         Debug.Log($"[CompanionRespawnManager] Checking companions for player {player.GetPlayerName()} ({playerId})");
       }

    // Phase 6: read via the resolver (roster first, vault fallback).
   List<CompanionSaveData> savedCompanions = null;
              try
   {
           savedCompanions = Vault.CompanionSavedDataResolver.ResolveAllForPlayer(playerId);
              }
    catch (Exception ex)
              {
             Debug.LogWarning($"[CompanionRespawnManager] Failed to resolve companions for player: {ex.Message}");
             yield break;
             }

            // PLAYER FOLLOWING REGISTRY MIGRATION & SELF-HEAL
            // The authoritative source for "is this companion set to follow"
            // is now the player's ZDO (PlayerFollowingRegistry).  On first
            // login under the new system the registry is empty, so we import
            // every vault entry whose IsFollowing flag is true.  Re-running
            // this every login is also a self-heal: any time the registry has
            // drifted from the vault (e.g. a vault file was hand-edited), the
            // union of the two takes precedence and the registry catches up.
            if (savedCompanions != null)
            {
                var followingIds = new List<string>();
                foreach (var saveData in savedCompanions)
                {
                    if (saveData == null) continue;
                    if (saveData.IsStationedAsNpc) continue;
                    if (!saveData.IsFollowing) continue;
                    if (string.IsNullOrEmpty(saveData.CompanionId)) continue;
                    followingIds.Add(saveData.CompanionId);
                }
                if (followingIds.Count > 0)
                {
                    PlayerFollowingRegistry.ImportLegacyIds(player, followingIds);
                }
            }

          if (savedCompanions == null || savedCompanions.Count == 0)
            {
         if (VerboseLogging)
          {
         Debug.Log($"[CompanionRespawnManager] No saved companions found for player {playerId}");
          }
 yield break;
        }

     // Process each companion that should be following
       foreach (var savedData in savedCompanions)
       {
              if (savedData == null) continue;

              // CRITICAL: Skip companions stationed as NPCs - they exist in the world and should not be restored
              if (savedData.IsStationedAsNpc)
              {
                  if (VerboseLogging)
                  {
                      Debug.Log($"[CompanionRespawnManager] Companion {savedData.CompanionName} is stationed as NPC - skipping restoration");
                  }
                  continue;
              }

             // AUTHORITATIVE FOLLOW-INTENT CHECK
             // Source of truth = PlayerFollowingRegistry on the player's ZDO.
             // Vault.IsFollowing is the legacy mirror and was just migrated into
             // the registry above.  Reading the registry instead of the vault
             // here means a clobbered vault flag (from any past bug) cannot
             // un-set a companion the player explicitly chose to follow.
             bool isFollowing = PlayerFollowingRegistry.IsFollowing(player, savedData.CompanionId);
             if (!isFollowing)
             {
                 if (VerboseLogging && savedData.IsFollowing)
                 {
                     Debug.Log($"[CompanionRespawnManager] Vault says {savedData.CompanionName} should follow, but player registry says no - skipping (registry is authoritative)");
                 }
                 continue;
             }

           if (VerboseLogging)
                {
   Debug.Log($"[CompanionRespawnManager] Processing companion {savedData.CompanionName} ({savedData.CompanionId}) - should be following");
      }

   // Check if this companion exists in the world
                CompanionController existingCompanion = FindCompanionInWorld(savedData.CompanionId);

  // Check if the companion's ZDO object exists even though CompanionController hasn't initialized
  if (existingCompanion == null && _existingZdoCompanions.TryGetValue(savedData.CompanionId, out var existingGO))
  {
      _existingZdoCompanions.Remove(savedData.CompanionId);
      if (existingGO != null)
      {
          // The companion object exists from a previous session's ZDO.
          // Teleport it to the player and wait for CompanionController to initialize.
          Debug.Log($"[CompanionRespawnManager] Companion {savedData.CompanionName} exists as ZDO object - teleporting instead of spawning duplicate");

          Vector3 spawnPos = GetSpawnPositionNearPlayer(player);
          existingGO.transform.position = spawnPos;

          // Wait for CompanionController to finish initializing
          float timeout = 5f;
          float waited = 0f;
          CompanionController controller = null;
          while (waited < timeout)
          {
              controller = existingGO.GetComponent<CompanionController>();
              if (controller != null && !string.IsNullOrEmpty(controller.companionId)) break;
              yield return new WaitForSeconds(0.5f);
              waited += 0.5f;
          }

          if (controller != null)
          {
              controller.CommandFollow(player);
              MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                  $"{savedData.CompanionName ?? "Your companion"} has rejoined you!");
          }

          yield return new WaitForSeconds(0.5f);
          continue;
      }
  }

  if (existingCompanion != null)
             {
       // Companion exists - check if alive
                    if (!existingCompanion.isDefeated)
   {
    // Alive - teleport to player
     if (VerboseLogging)
               {
  Debug.Log($"[CompanionRespawnManager] Companion {savedData.CompanionName} is alive - teleporting to player");
         }
                  
   existingCompanion.TeleportToOwner();
           existingCompanion.CommandFollow(player);
               
  MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
      $"{savedData.CompanionName ?? "Your companion"} has rejoined you!");
  }
     else
          {
             // Defeated/dead - destroy and spawn fresh
     if (VerboseLogging)
      {
           Debug.Log($"[CompanionRespawnManager] Companion {savedData.CompanionName} is defeated - destroying and respawning");
         }
     
       CompanionNetworkHelper.Destroy(existingCompanion.gameObject);

             yield return new WaitForSeconds(0.5f);
        yield return StartCoroutine(SpawnCompanionForPlayer(player, savedData));
        }
       }
      else
     {
       // Companion doesn't exist in world - check if pending respawn
          if (_pendingRespawns.ContainsKey(savedData.CompanionId))
             {
      // Has pending respawn in memory - cancel it and spawn immediately
                    if (VerboseLogging)
       {
         Debug.Log($"[CompanionRespawnManager] Companion {savedData.CompanionName} has pending respawn - spawning immediately for login");
    }
    
         var respawnData = _pendingRespawns[savedData.CompanionId];
     _pendingRespawns.Remove(savedData.CompanionId);
       
      yield return StartCoroutine(SpawnCompanionForPlayer(player, savedData));
       }
        else if (savedData.IsPendingRespawn && savedData.DeathTimestamp > 0)
        {
            // Time left on the roster's wall-clock respawn deadline: schedule the respawn for what remains, or spawn
            // now when it has passed or the data came from the vault, which carries no deadline.
            float remaining = savedData.RespawnTimeRemaining;
            if (remaining > 0.5f)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionRespawnManager] Companion {savedData.CompanionName} has {remaining:F1}s remaining on persisted respawn timer - scheduling respawn instead of immediate login spawn");
                }

                ScheduleRespawn(
                    savedData.CompanionId,
                    savedData.OwnerPlayerId,
                    string.IsNullOrEmpty(savedData.PrefabName) ? "CompanionNpc" : savedData.PrefabName,
                    remaining,
                    ZDOID.None
                );
            }
            else
            {
                // DEATH PERSISTENCE FIX: companion died and timer expired
                // while logged out (or was never written to roster, e.g.
                // legacy vault entries). Spawn now and preserve the death
                // metadata so stats aren't lost.
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionRespawnManager] Companion {savedData.CompanionName} has persisted death state with no remaining timer - spawning for login");
                }

                yield return StartCoroutine(SpawnCompanionForPlayer(player, savedData));
            }
        }
        else
        {
         // No pending respawn and not in world - spawn fresh
  if (VerboseLogging)
    {
       Debug.Log($"[CompanionRespawnManager] Companion {savedData.CompanionName} not found in world - spawning for login");
}
   
     yield return StartCoroutine(SpawnCompanionForPlayer(player, savedData));
   }
         }
    
          // Small delay between companions
      yield return new WaitForSeconds(0.5f);
            }
        }

      /// <summary>
      /// Finds a companion in the world by ID.
      /// First checks the fast static list, then falls back to scanning ZDOs
      /// for companions that exist in the world but haven't fully initialized yet.
      /// </summary>
      private CompanionController FindCompanionInWorld(string companionId)
      {
          if (string.IsNullOrEmpty(companionId)) return null;

          // Fast path: check the static companion list (already initialized companions)
          foreach (var companion in CompanionController.AllCompanions)
          {
              if (companion != null && companion.companionId == companionId)
                  return companion;
          }

          // Slow path: the companion's GameObject may exist from a previous session's ZDO
          // but CompanionController.Start()/LoadFromZDO() hasn't run yet, so companionId
          // isn't set on the component. Scan all ZNetView objects for matching ZDO data.
          // This prevents spawning a duplicate when re-logging.
          foreach (var znetView in FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
          {
              if (znetView == null || !znetView.IsValid()) continue;
              var zdo = znetView.GetZDO();
              if (zdo == null) continue;

              var zdoCompanionId = zdo.GetString("companion_id", "");
              if (zdoCompanionId == companionId)
              {
                  // Found the ZDO - return the CompanionController if it exists,
                  // otherwise return a marker (null controller but we know it exists).
                  var controller = znetView.GetComponent<CompanionController>();
                  if (controller != null) return controller;

                  // The object exists but hasn't initialized its CompanionController yet.
                  // We still need to signal "exists" - use a special sentinel approach:
                  // Teleport the existing object to the player and skip spawning.
                  if (VerboseLogging)
                      Debug.Log($"[CompanionRespawnManager] Found existing ZDO for companion {companionId} but CompanionController not yet initialized");

                  // Return null but mark as found via the _existingZdoCompanions set
                  _existingZdoCompanions[companionId] = znetView.gameObject;
                  return null;
              }
          }

          return null;
      }

      // Tracks companions found via ZDO scan that don't have CompanionController yet
      private readonly Dictionary<string, GameObject> _existingZdoCompanions = new Dictionary<string, GameObject>();

        /// <summary>
        /// Spawns a companion from vault data for a player.
        /// </summary>
        private IEnumerator SpawnCompanionForPlayer(Player player, CompanionSaveData savedData)
        {
       if (player == null || savedData == null) yield break;
            
        // Get spawn position near player
   Vector3 spawnPos = GetSpawnPositionNearPlayer(player);

        // Get the prefab
            GameObject prefab = ZNetScene.instance?.GetPrefab(savedData.PrefabName);
if (prefab == null)
        {
   prefab = ZNetScene.instance?.GetPrefab("CompanionNpc");
   }

    if (prefab == null)
            {
   Debug.LogError($"[CompanionRespawnManager] Could not find prefab {savedData.PrefabName}");
       yield break;
   }

            // Spawn the companion via ZNetScene for proper network registration
     GameObject companionObj = CompanionNetworkHelper.Spawn(prefab, spawnPos, Quaternion.identity);
     if (companionObj == null)
     {
         Debug.LogError($"[CompanionRespawnManager] Failed to spawn companion {savedData.PrefabName}");
         yield break;
     }

    // Set tamed state on Character component IMMEDIATELY
     var character = companionObj.GetComponent<Character>();
       if (character != null)
             {
             character.SetTamed(true);
     }

    // Set on ZNetView ZDO immediately
 var nview = companionObj.GetComponent<ZNetView>();
        if (nview != null && nview.IsValid())
            {
     var zdo = nview.GetZDO();
    if (zdo != null)
   {
          zdo.Set(ZDOVars.s_tamed, true);
   zdo.Set("companion_tamed", true);
     zdo.Set("companion_id", savedData.CompanionId);
      zdo.Set("companion_owner", savedData.OwnerPlayerId);
      
      // CRITICAL: Set follow state in ZDO BEFORE Start() runs!
      // SpawnCompanionForPlayer is always for following companions (called from RestoreCompanionsForPlayer)
      zdo.Set("companion_wasfollowing", savedData.IsFollowing);
        }
        }

            // Configure the companion controller
            var controller = companionObj.GetComponent<CompanionController>();
    if (controller != null)
    {
        controller.companionId = savedData.CompanionId;
     controller.ownerPlayerId = savedData.OwnerPlayerId;
      controller.isTamed = true;
              controller.isDefeated = false;
   
          // Wait for initialization
    yield return null;
        
            // Restore from vault
          yield return StartCoroutine(RestoreCompanionFromVaultData(controller, savedData));
         
           // Set follow mode
     controller.CommandFollow(player);
     }

           // Play spawn effect
            PlayRespawnEffect(spawnPos);

           // Notify player
            if (player == Player.m_localPlayer)
            {
       MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
        $"{savedData.CompanionName ?? "Your companion"} has returned!");
        }

     if (VerboseLogging)
     {
      Debug.Log($"[CompanionRespawnManager] Spawned companion {savedData.CompanionName} for player login at {spawnPos}");
   }
        }

        /// <summary>
        /// Restores companion state from vault save data.
        /// </summary>
        private IEnumerator RestoreCompanionFromVaultData(CompanionController controller, CompanionSaveData savedData)
    {
            if (controller == null || savedData == null) yield break;
            
            // Restore identity
            controller.companionName = savedData.CompanionName ?? "Companion";
            controller.displayNameOverride = savedData.DisplayNameOverride;

          // Update ZDO with name
          var nview = controller.GetComponent<ZNetView>();
          if (nview != null && nview.IsValid())
            {
         var zdo = nview.GetZDO();
         if (zdo != null)
                {
        zdo.Set("companion_name", controller.companionName);
        zdo.Set("companion_displayname", controller.displayNameOverride ?? "");
              zdo.Set(ZDOVars.s_tamedName, controller.companionName);
              
              // Restore appearance data to ZDO if present
              if (savedData.HasAppearanceData)
              {
                  zdo.Set("companion_isfemale", savedData.ModelIndex == 1);
                  zdo.Set("companion_hair", savedData.HairStyle ?? "");
                  zdo.Set("companion_beard", savedData.BeardStyle ?? "");
                  // Use the same ZDO keys that CompanionRandomLoadout uses
                  zdo.Set(ZDOVars.s_hairColor, new Vector3(savedData.HairColorR, savedData.HairColorG, savedData.HairColorB));
                  zdo.Set(ZDOVars.s_skinColor, new Vector3(savedData.SkinColorR, savedData.SkinColorG, savedData.SkinColorB));
                  zdo.Set("companion_eyeColor", new Vector3(savedData.EyeColorR, savedData.EyeColorG, savedData.EyeColorB));
                  zdo.Set(ZDOVars.s_modelIndex, savedData.ModelIndex);
                  
                  if (VerboseLogging)
                  {
                      Debug.Log($"[CompanionRespawnManager] Restored appearance data for {controller.companionName}: hair={savedData.HairStyle}, model={savedData.ModelIndex}");
                  }
              }
              
              // Restore scale data if present
              if (Mathf.Abs(savedData.Scale - 1.0f) > 0.01f || savedData.IsGiant || savedData.IsDwarf)
              {
                  zdo.Set("companion_scale", savedData.Scale);
                  zdo.Set("companion_isgiant", savedData.IsGiant);
                  zdo.Set("companion_isdwarf", savedData.IsDwarf);
                  
                  if (VerboseLogging)
                  {
                      Debug.Log($"[CompanionRespawnManager] Restored scale data: {savedData.Scale:F2} (Giant: {savedData.IsGiant}, Dwarf: {savedData.IsDwarf})");
                  }
              }
                }
     }

     // Wait for inventory to be ready
            yield return null;

       var inventory = controller.GetInventory();
  if (inventory != null && savedData.EquipmentPrefabs != null)
            {
                // Restore equipment
        foreach (var kvp in savedData.EquipmentPrefabs)
     {
  RestoreEquipmentSlot(inventory, kvp.Key, kvp.Value, savedData.EquipmentQualities);
       }

                // Restore storage inventory if available
           if (!string.IsNullOrEmpty(savedData.StorageInventoryData))
    {
      try
    {
       var storageInv = inventory.GetStorageInventory();
            if (storageInv != null)
         {
         var pkg = new ZPackage(savedData.StorageInventoryData);
   storageInv.Load(pkg);
   }
           }
         catch (Exception ex)
          {
         Debug.LogWarning($"[CompanionRespawnManager] Failed to restore storage inventory: {ex.Message}");
                    }
                }

   // Apply visual equipment
         inventory.RecalculateEquipmentBonusesPublic();
            }

        yield return new WaitForSeconds(0.5f);

    if (inventory != null)
            {
                inventory.ApplyVisualEquipment();
            }
            
            // Trigger visual appearance update from ZDO data
            // This ensures the model/hair/colors are applied after ZDO is populated
            if (savedData.HasAppearanceData)
            {
                var randomLoadout = controller.GetComponent<CompanionRandomLoadout>();
                if (randomLoadout != null)
                {
                    randomLoadout.Invoke("RestoreModelStateFromZDO", AppearanceRestoreDelay);
                }
            }
            
            // CRITICAL: Apply scale using CompanionRandomLoadout's method for consistency
            // This ensures scale is applied the same way as when wild companions are first created
            Debug.Log($"[CompanionRespawnManager] RestoreCompanionFromVaultData SCALE CHECK: {savedData.CompanionName} - vault scale={savedData.Scale:F2}, isGiant={savedData.IsGiant}, isDwarf={savedData.IsDwarf}");
            
            if (Mathf.Abs(savedData.Scale - 1.0f) > 0.01f || savedData.IsGiant || savedData.IsDwarf)
            {
                var randomLoadoutForScale = controller.GetComponent<CompanionRandomLoadout>();
                if (randomLoadoutForScale != null)
                {
                    Debug.Log($"[CompanionRespawnManager] Calling ApplyScale({savedData.Scale:F2}) on {savedData.CompanionName} via CompanionRandomLoadout");
                    
                    // Use the same ApplyScale method that's used when wild companions are created
                    // Pass skipHealthAdjustment: true because health will be restored from vault separately
                    randomLoadoutForScale.ApplyScale(savedData.Scale, skipHealthAdjustment: true);
                    
                    // Verify scale was applied
                    Debug.Log($"[CompanionRespawnManager] After ApplyScale: localScale={controller.transform.localScale}, GetScale()={randomLoadoutForScale.GetScale():F2}");
                }
                else
                {
                    Debug.LogWarning($"[CompanionRespawnManager] No CompanionRandomLoadout component on {savedData.CompanionName}!");
                    // Fallback if no CompanionRandomLoadout component
                    ApplyScaleToCompanion(controller.gameObject, savedData.Scale);
                }
            }
            else
            {
                Debug.Log($"[CompanionRespawnManager] No scale to apply for {savedData.CompanionName} (scale={savedData.Scale:F2}, isGiant={savedData.IsGiant}, isDwarf={savedData.IsDwarf})");
            }
            
            // CRITICAL: Restore progression, stats, skills, and kill tracker data
            // Without this, companions lose their levels, attributes, and progress on respawn
            RestoreProgressionAndStatsFromVault(controller, savedData);

                      // Save to ZDO (authoritative for in-world state)
            controller.SaveToZDO();

                     // Phase 6: vault is a debug mirror updated by the periodic
                     // FlushDebugMirror; per-event vault writes are not needed
                     // here. The roster mirror write below is the authoritative
                     // "respawn complete, clear pending" signal.
                 }

      #endregion

        #region Respawn Management

        /// <summary>
      /// Schedules a companion for respawn.
        /// </summary>
    public void ScheduleRespawn(string companionId, long ownerPlayerId, string prefabName, float delay, ZDOID tombstoneZDOID)
        {
            if (string.IsNullOrEmpty(companionId)) return;

            var data = new RespawnData
       {
     CompanionId = companionId,
             OwnerPlayerId = ownerPlayerId,
           PrefabName = prefabName,
     RespawnTime = Time.time + delay,
        TombstoneZDOID = tombstoneZDOID
         };

            _pendingRespawns[companionId] = data;

          if (VerboseLogging)
      {
         Debug.Log($"[CompanionRespawnManager] Scheduled respawn for {companionId} in {delay}s");
            }
        }

  /// <summary>
        /// Cancels a pending respawn.
  /// </summary>
      public void CancelRespawn(string companionId)
        {
    if (_pendingRespawns.ContainsKey(companionId))
       {
         _pendingRespawns.Remove(companionId);
    
      if (VerboseLogging)
              {
          Debug.Log($"[CompanionRespawnManager] Cancelled respawn for {companionId}");
             }
 }
        }

        /// <summary>
 /// Checks all pending respawns and spawns companions when ready.
    /// </summary>
   private void CheckPendingRespawns()
        {
      if (_pendingRespawns.Count == 0) return;

    var toRespawn = new List<string>();

            foreach (var kvp in _pendingRespawns)
   {
             if (Time.time >= kvp.Value.RespawnTime)
          {
             toRespawn.Add(kvp.Key);
        }
            }

            foreach (var companionId in toRespawn)
        {
         var data = _pendingRespawns[companionId];
    _pendingRespawns.Remove(companionId);

       StartCoroutine(RespawnCompanion(data));
            }
        }

        /// <summary>
        /// Respawns a companion.
        /// </summary>
        private IEnumerator RespawnCompanion(RespawnData data)
 {
          if (VerboseLogging)
      {
              Debug.Log($"[CompanionRespawnManager] Respawning companion {data.CompanionId}");
            }

 // Get save data first to check if this is a stationed NPC.
 // Phase 4: prefer roster, fall back to vault. Resolver also
 // translates the roster's wall-clock deadline into the
 // snapshot's RespawnTimeRemaining for downstream consistency.
 CompanionSaveData savedData = null;
 try
 {
     savedData = CompanionSavedDataResolver.ResolveByCompanionId(data.OwnerPlayerId, data.CompanionId);
 }
 catch (Exception ex)
 {
     Debug.LogWarning($"[CompanionRespawnManager] Failed to resolve save data for respawn: {ex.Message}");
 }

 // CRITICAL SAFETY GATE: never spawn a "blank" companion when vault data is missing.
 // If we proceed without savedData the player ends up with a level-1 nameless
 // "CompanionNpc" zombie that has none of the original identity, stats, or progression.
 // Reschedule the respawn for a few seconds later so vault sync / login can complete,
 // and bail out without spawning anything yet.
 if (savedData == null)
 {
     // Kennel-backed fallback: the legacy m_customData snapshot is absent (post-redesign, or a
     // kennel-only death). Spawn directly from the Core dormant store if it holds this entry,
     // restoring the snapshot's own follow/stay/stationed state. Following companions reach the
     // owner via the normal follow-teleport pipeline after ApplyState. Additive: in the current
     // dual-mod state savedData is non-null (m_customData still written), so this never fires.
     if (FiresCore.Bridge.NpcDormancyBridge.IsAvailable)
     {
         Vector3 kennelPos = Vector3.zero;
         foreach (var onlinePlayer in Player.GetAllPlayers())
         {
             if (onlinePlayer != null && onlinePlayer.GetPlayerID() == data.OwnerPlayerId) { kennelPos = onlinePlayer.transform.position; break; }
         }
         if (CompanionRestoreService.TrySpawnDormantById(data.OwnerPlayerId, data.CompanionId, kennelPos))
         {
             _pendingRespawns.Remove(data.CompanionId);
             yield break;
         }
     }

     Debug.LogWarning($"[CompanionRespawnManager] Vault data not found for {data.CompanionId} (owner {data.OwnerPlayerId}) - deferring respawn 15s to allow vault sync");
     data.RespawnTime = Time.time + VaultSyncRetryDelay;
     _pendingRespawns[data.CompanionId] = data;
     yield break;
 }

 // I1 (single live instance): if this companion is already alive in the world, do NOT
 // spawn a second one. Guards the diagnosed duplicate-spawn race where the death timer,
 // login restore, and recall can all fire for the same id. A dead/defeated existing
 // instance is the corpse we're replacing — it doesn't count as "already live".
 foreach (var existing in CompanionController.AllCompanions)
 {
     if (existing == null) continue;
     if (!string.Equals(existing.companionId, data.CompanionId, StringComparison.Ordinal)) continue;
     bool deadCorpse = existing.isDefeated || (existing.GetCharacter()?.IsDead() ?? false);
     if (deadCorpse) continue;
     Debug.Log($"[CompanionRespawnManager] {data.CompanionId} is already live in the world — skipping duplicate respawn (I1 single-instance guard).");
     _pendingRespawns.Remove(data.CompanionId);
     yield break;
 }

 // Determine spawn position based on whether this is a stationed NPC
 Vector3 spawnPos;
 Quaternion spawnRot = Quaternion.identity;
 bool isStationedNpc = savedData != null && savedData.IsStationedAsNpc;
 
 if (isStationedNpc)
 {
     // Stationed NPC - spawn at their stationed position
     spawnPos = new Vector3(savedData.StationedPositionX, savedData.StationedPositionY, savedData.StationedPositionZ);
     spawnRot = Quaternion.Euler(0f, savedData.StationedRotationY, 0f);

     if (VerboseLogging)
     {
         Debug.Log($"[CompanionRespawnManager] Stationed NPC {data.CompanionId} will respawn at stationed position {spawnPos}");
     }
 }
 else if (savedData.HasHomePosition && !savedData.IsFollowing)
 {
     // Stay-mode companion - respawn at the last commanded home position,
     // NOT next to the owner. Honours the owner's "stay here" command across
     // the death/respawn cycle. No owner-load check needed; the home position
     // is a static world coordinate independent of the owner being online.
     spawnPos = new Vector3(savedData.HomePositionX, savedData.HomePositionY, savedData.HomePositionZ);

     Debug.Log($"[CompanionRespawnManager] Stay-mode companion {data.CompanionId} respawning at home position {spawnPos}");
 }
 else
 {
     // Following companion - find the owner and spawn near them.
     Player owner = null;
         foreach (var player in Player.GetAllPlayers())
          {
        if (player.GetPlayerID() == data.OwnerPlayerId)
      {
        owner = player;
          break;
        }
         }

            if (owner == null)
            {
      if (VerboseLogging)
   {
  Debug.Log($"[CompanionRespawnManager] Owner not found for {data.CompanionId}, deferring respawn");
    }

   // Re-schedule for later
          data.RespawnTime = Time.time + OwnerOfflineRetryDelay;  // Try again in 30 seconds
     _pendingRespawns[data.CompanionId] = data;
 yield break;
    }

    spawnPos = GetSpawnPositionNearPlayer(owner);
 }

 // KENNEL-PRIMARY death-respawn: now that the spawn position is resolved, try the Core dormant
 // store first (snapshot restored via ApplyState). The legacy RestoreCompanionFromVault path
 // below is now an automatic FALLBACK — if the kennel doesn't hold this id or the spawn fails we
 // fall through and the old path catches it, so a respawn can NEVER be lost. This is what makes
 // the kennel death path actually execute (it was being masked by the legacy path winning).
 // Once "Dormancy-spawned" is confirmed in the log, the legacy path is dead code (Phase 5).
 if (FiresCore.Bridge.NpcDormancyBridge.IsAvailable &&
     CompanionRestoreService.TrySpawnDormantById(data.OwnerPlayerId, data.CompanionId, spawnPos))
 {
     _pendingRespawns.Remove(data.CompanionId);
     yield break;
 }

            // KENNEL-AUTHORITATIVE: with the kennel as the store, a miss here means the entry hasn't
            // synced yet (or this is a pre-fix corrupt companion that has no entry). NEVER spawn a bare,
            // identity-less companion as a fallback — that is exactly the "came back as CompanionNpc_Wild
            // with no name/gear" bug. Defer and retry instead; the death handler always writes a
            // DeadPendingRespawn kennel entry, so a legitimately-dead companion's entry will be present
            // shortly. The legacy bare-spawn path below only remains for the (never, in practice) case
            // of no dormant store registered at all.
            if (FiresCore.Bridge.NpcDormancyBridge.IsAvailable)
            {
                Debug.LogWarning($"[CompanionRespawnManager] Kennel has no entry yet for {data.CompanionId} " +
                                 $"— deferring respawn 10s (never spawning a bare identity-less fallback).");
                data.RespawnTime = Time.time + KennelSyncRetryDelay;
                _pendingRespawns[data.CompanionId] = data;
                yield break;
            }

         // Get the prefab
     GameObject prefab = ZNetScene.instance?.GetPrefab(data.PrefabName);
            if (prefab == null)
     {
         prefab = ZNetScene.instance?.GetPrefab("CompanionNpc");
      }

    if (prefab == null)
     {
              Debug.LogError($"[CompanionRespawnManager] Could not find prefab {data.PrefabName}");
     yield break;
 }

      // Spawn the companion at the determined position and rotation via ZNetScene
    GameObject companionObj = CompanionNetworkHelper.Spawn(prefab, spawnPos, spawnRot);
     if (companionObj == null)
     {
         Debug.LogError($"[CompanionRespawnManager] Failed to spawn companion {data.CompanionId}");
         yield break;
     }

     // CRITICAL: Set tamed state on Character component IMMEDIATELY before Start() runs on other components
    var character = companionObj.GetComponent<Character>();
 if (character != null)
    {
         character.SetTamed(true);
         }

       // Also set on ZNetView ZDO immediately
         var nview = companionObj.GetComponent<ZNetView>();
     if (nview != null && nview.IsValid())
   {
      var zdo = nview.GetZDO();
        if (zdo != null)
   {
        zdo.Set(ZDOVars.s_tamed, true);
           zdo.Set("companion_tamed", true);
         zdo.Set("companion_id", data.CompanionId);
      zdo.Set("companion_owner", data.OwnerPlayerId);
      
      // CRITICAL: Set follow state in ZDO BEFORE Start() runs!
      // This ensures LoadFromZDO() in CompanionController will restore the correct state
      bool shouldFollow = savedData != null && savedData.IsFollowing && !isStationedNpc;
      zdo.Set("companion_wasfollowing", shouldFollow);
      
      if (VerboseLogging)
      {
          Debug.Log($"[CompanionRespawnManager] Set ZDO for {data.CompanionId}: wasfollowing={shouldFollow}, isStationedNpc={isStationedNpc}");
      }
     }
            }

         // Configure the companion controller
  var controller = companionObj.GetComponent<CompanionController>();
            if (controller != null)
            {
      // Set basic identity immediately (before RestoreCompanionFromVault fills in details)
                controller.companionId = data.CompanionId;
                controller.ownerPlayerId = data.OwnerPlayerId;
      controller.isTamed = true;
         controller.isDefeated = false;

    // Restore full state from vault
   yield return StartCoroutine(RestoreCompanionFromVault(controller, data));
    }

            // Play respawn effect
      PlayRespawnEffect(spawnPos);

     // Notify owner (only for companions that are following, not stationed NPCs)
     if (!isStationedNpc)
     {
         // Find the owner to notify
         Player ownerPlayer = null;
         foreach (var player in Player.GetAllPlayers())
         {
             if (player.GetPlayerID() == data.OwnerPlayerId)
             {
                 ownerPlayer = player;
                 break;
             }
         }
         
         if (ownerPlayer == Player.m_localPlayer)
         {
             MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                 $"{controller?.GetDisplayName() ?? "Companion"} has respawned!");
         }
     }

            if (VerboseLogging)
            {
     Debug.Log($"[CompanionRespawnManager] Companion {data.CompanionId} respawned at {spawnPos}");
 }
  }

        /// <summary>
   /// Restores companion state from vault after respawn.
        /// </summary>
      private IEnumerator RestoreCompanionFromVault(CompanionController controller, RespawnData data)
        {
          // Wait a frame for components to initialize
 yield return null;

    if (!FiresCore.Bridge.CompanionVaultBridge.IsVaultAvailable())
        {
       Debug.LogWarning("[CompanionRespawnManager] VaultOfKnowledge not available");
   yield break;
       }

            CompanionSaveData savedData = null;

   // Get save data outside try/catch - Phase 4 prefers roster.
           try
{
               savedData = CompanionSavedDataResolver.ResolveByCompanionId(data.OwnerPlayerId, data.CompanionId);
         }
          catch (Exception ex)
  {
           Debug.LogError($"[CompanionRespawnManager] Failed to resolve save data: {ex.Message}");
    }

  // CRITICAL: Always set these core properties first, even if no vault data found
   controller.companionId = data.CompanionId;
          controller.ownerPlayerId = data.OwnerPlayerId;
            controller.isTamed = true;
            controller.isDefeated = false;

            // Set tamed state on the Valheim Character component IMMEDIATELY
            var character = controller.GetComponent<Character>();
            if (character != null)
   {
      character.SetTamed(true);
            }

            // Set tamed state via ZDO for persistence
         var nview = controller.GetComponent<ZNetView>();
         if (nview != null && nview.IsValid())
          {
            var zdo = nview.GetZDO();
      if (zdo != null)
    {
        zdo.Set(ZDOVars.s_tamed, true);
        zdo.Set("companion_tamed", true);
   zdo.Set("companion_id", data.CompanionId);
        zdo.Set("companion_owner", data.OwnerPlayerId);
 }
            }

    // Also set on Tameable if present
    var tameable = controller.GetComponent<Tameable>();
            if (tameable != null)
     {
         // Use reflection to set tamed state if needed
  try
        {
   var tamedField = typeof(Tameable).GetField("m_tamed", 
           System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
      if (tamedField != null)
 {
     tamedField.SetValue(tameable, true);
            }
     }
      catch { }
            }

   if (savedData == null)
        {
   // Last-resort retry: wait a moment for vault sync and try again before giving up.
   yield return new WaitForSeconds(2f);
   try
   {
       savedData = CompanionSavedDataResolver.ResolveByCompanionId(data.OwnerPlayerId, data.CompanionId);
   }
   catch (Exception ex)
   {
       Debug.LogError($"[CompanionRespawnManager] Retry save-data read failed: {ex.Message}");
   }
   }

   if (savedData == null)
        {
   // CRITICAL: Do NOT keep a half-restored zombie companion in the world.
   // It would appear as a level-1 nameless "CompanionNpc" with full bars and
   // none of the original identity / progression.  Destroy it and reschedule
   // so the player can try again once the vault is healthy.
   Debug.LogError($"[CompanionRespawnManager] Vault data missing for {data.CompanionId} (owner {data.OwnerPlayerId}) after retry - destroying zombie spawn and rescheduling respawn");

   try { CompanionNetworkHelper.Destroy(controller.gameObject); }
   catch (Exception ex) { Debug.LogWarning($"[CompanionRespawnManager] Failed to destroy zombie companion: {ex.Message}"); }

   var rescheduled = new RespawnData
   {
       CompanionId   = data.CompanionId,
       OwnerPlayerId = data.OwnerPlayerId,
       PrefabName    = data.PrefabName,
       RespawnTime   = Time.time + FailedRestoreRetryDelay,
       TombstoneZDOID = data.TombstoneZDOID
   };
   _pendingRespawns[data.CompanionId] = rescheduled;

   if (Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerID() == data.OwnerPlayerId)
   {
       MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
           "Companion vault data not ready - respawn deferred 30s");
   }
   yield break;
   }

  // Restore identity from vault
    controller.companionName = savedData.CompanionName ?? "Companion";
    controller.displayNameOverride = savedData.DisplayNameOverride;

            // Update ZDO with name and appearance
  if (nview != null && nview.IsValid())
 {
         var zdo = nview.GetZDO();
     if (zdo != null)
  {
               zdo.Set("companion_name", controller.companionName);
            zdo.Set("companion_displayname", controller.displayNameOverride ?? "");
   zdo.Set(ZDOVars.s_tamedName, controller.companionName);
   
               // CRITICAL: Restore appearance data to ZDO so it's applied to the model
               if (savedData.HasAppearanceData)
               {
                   zdo.Set("companion_isfemale", savedData.ModelIndex == 1);
                   zdo.Set("companion_hair", savedData.HairStyle ?? "");
                   zdo.Set("companion_beard", savedData.BeardStyle ?? "");
                   zdo.Set(ZDOVars.s_hairColor, new Vector3(savedData.HairColorR, savedData.HairColorG, savedData.HairColorB));
                   zdo.Set(ZDOVars.s_skinColor, new Vector3(savedData.SkinColorR, savedData.SkinColorG, savedData.SkinColorB));
                   zdo.Set("companion_eyeColor", new Vector3(savedData.EyeColorR, savedData.EyeColorG, savedData.EyeColorB));
                   zdo.Set(ZDOVars.s_modelIndex, savedData.ModelIndex);
                   
                   if (VerboseLogging)
                   {
                       Debug.Log($"[CompanionRespawnManager] Restored appearance data for {controller.companionName}: model={savedData.ModelIndex}, hair={savedData.HairStyle}");
                   }
               }
               
               // Restore scale data if present
               if (Mathf.Abs(savedData.Scale - 1.0f) > 0.01f || savedData.IsGiant || savedData.IsDwarf)
               {
                   zdo.Set("companion_scale", savedData.Scale);
                   zdo.Set("companion_isgiant", savedData.IsGiant);
                   zdo.Set("companion_isdwarf", savedData.IsDwarf);
               }
      }
            }

   // Wait for inventory to be ready
            yield return null;

            var inventory = controller.GetInventory();
            if (inventory != null && savedData.EquipmentPrefabs != null)
            {
  // Restore equipment
         foreach (var kvp in savedData.EquipmentPrefabs)
     {
                  RestoreEquipmentSlot(inventory, kvp.Key, kvp.Value, savedData.EquipmentQualities);
         }

           // Restore storage inventory if available
   if (!string.IsNullOrEmpty(savedData.StorageInventoryData))
         {
      try
  {
     var storageInv = inventory.GetStorageInventory();
     if (storageInv != null)
               {
        var pkg = new ZPackage(savedData.StorageInventoryData);
  storageInv.Load(pkg);
             Debug.Log($"[CompanionRespawnManager] Restored storage inventory with {storageInv.GetAllItems().Count} items");
     }
        }
           catch (Exception ex)
       {
        Debug.LogWarning($"[CompanionRespawnManager] Failed to restore storage inventory: {ex.Message}");
   }
          }

   // Apply visual equipment
       inventory.RecalculateEquipmentBonusesPublic();
            }
     
         yield return new WaitForSeconds(0.5f);
      
      if (inventory != null)
   {
       inventory.ApplyVisualEquipment();
   }
   
            // Trigger visual appearance update from ZDO data
            // This ensures the model/hair/colors are applied after ZDO is populated
            if (savedData.HasAppearanceData)
            {
                var randomLoadout = controller.GetComponent<CompanionRandomLoadout>();
                if (randomLoadout != null)
                {
                    randomLoadout.Invoke("RestoreModelStateFromZDO", AppearanceRestoreDelay);
                }
            }
            
            // CRITICAL: Apply scale using CompanionRandomLoadout's method for consistency
            // This ensures scale is applied the same way as when wild companions are first created
            Debug.Log($"[CompanionRespawnManager] RestoreCompanionFromVault SCALE CHECK: {savedData.CompanionName} - vault scale={savedData.Scale:F2}, isGiant={savedData.IsGiant}, isDwarf={savedData.IsDwarf}");
            
            if (Mathf.Abs(savedData.Scale - 1.0f) > 0.01f || savedData.IsGiant || savedData.IsDwarf)
            {
                var randomLoadout = controller.GetComponent<CompanionRandomLoadout>();
                if (randomLoadout != null)
                {
                    Debug.Log($"[CompanionRespawnManager] Calling ApplyScale({savedData.Scale:F2}) on respawned {savedData.CompanionName} via CompanionRandomLoadout");
                    
                    // Use the same ApplyScale method that's used when wild companions are created
                    // This properly updates internal state (_companionScale, _isGiant, _isDwarf)
                    // and applies all the same adjustments (transform, collider, health, rigidbody)
                    // Pass skipHealthAdjustment: true because health will be restored from vault separately
                    randomLoadout.ApplyScale(savedData.Scale, skipHealthAdjustment: true);
                    
                    // Verify scale was applied
                    Debug.Log($"[CompanionRespawnManager] After ApplyScale on respawn: localScale={controller.transform.localScale}, GetScale()={randomLoadout.GetScale():F2}");
                }
                else
                {
                    Debug.LogWarning($"[CompanionRespawnManager] No CompanionRandomLoadout component on respawned {savedData.CompanionName}!");
                    // Fallback if no CompanionRandomLoadout component
                    ApplyScaleToCompanion(controller.gameObject, savedData.Scale);
                }
            }
            else
            {
                Debug.Log($"[CompanionRespawnManager] No scale to apply for respawned {savedData.CompanionName} (scale={savedData.Scale:F2}, isGiant={savedData.IsGiant}, isDwarf={savedData.IsDwarf})");
            }
            
            // CRITICAL: Restore progression, stats, skills, and kill tracker data
            // Without this, companions lose their levels, attributes, and progress on respawn
            RestoreProgressionAndStatsFromVault(controller, savedData);

            // Set follow mode OR restore stationed NPC state
            if (savedData.IsStationedAsNpc)
            {
                // CRITICAL: Restore stationed NPC state
                var npcModule = controller.GetComponent<CompanionNpcModule>();
                if (npcModule == null)
                {
                    npcModule = controller.gameObject.AddComponent<CompanionNpcModule>();
                }
                
                // Restore stationed position and settings
                Vector3 stationedPos = new Vector3(savedData.StationedPositionX, savedData.StationedPositionY, savedData.StationedPositionZ);
                Quaternion stationedRot = Quaternion.Euler(0f, savedData.StationedRotationY, 0f);
                
                // Ensure companion is at the stationed position
                controller.transform.position = stationedPos;
                controller.transform.rotation = stationedRot;
                
                // Apply stationed state
                npcModule.allowIdleWandering = savedData.AllowIdleWandering;
                npcModule.StationAtPosition(stationedPos, stationedRot);
                
                // Re-enable idle wandering if it was enabled before death
                if (savedData.AllowIdleWandering)
                {
                    npcModule.allowIdleWandering = true;
                }
                
                npcModule.SaveToZDO();
                
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionRespawnManager] Restored stationed NPC state for {controller.companionName} at {stationedPos}, allowWander={savedData.AllowIdleWandering}");
                }
                
                // Notify - stationed NPCs don't notify the owner, they just respawn at their post
                Debug.Log($"[CompanionRespawnManager] Stationed NPC {controller.companionName} respawned at their post");
            }
            else
            {
                // Restore the owner's last command rather than always following: a companion told to stay and guard a
                // spot must respawn back to its anchor. savedData.IsFollowing and the home position carry that intent.
                Player owner = null;
                foreach (var player in Player.GetAllPlayers())
                {
                    if (player.GetPlayerID() == data.OwnerPlayerId)
                    {
                        owner = player;
                        break;
                    }
                }

                if (savedData.IsFollowing)
                {
                    // Persistent intent: FOLLOW.  Restore follow state through
                    // multiple channels so it sticks.
                    if (owner != null)
                    {
                        controller.CommandFollow(owner);

                        var companionAI = controller.GetCompanionAI();
                        if (companionAI != null)
                        {
                            companionAI.SetFollowTarget(owner.gameObject);
                            companionAI.SetShouldFollow(true);
                        }

                        Debug.Log($"[CompanionRespawnManager] Restored follow state for {controller.companionName} - following {owner.GetPlayerName()}");
                    }
                    else
                    {
                        // Owner not loaded yet - set persistent flag and let
                        // the deferred-follow watcher pick it up.
                        Debug.LogWarning($"[CompanionRespawnManager] Owner not found for {controller.companionName} after respawn, setting follow state directly");
                        controller.SetFollowMode(true);

                        var companionAI = controller.GetCompanionAI();
                        if (companionAI != null)
                            companionAI.SetShouldFollow(true);
                    }
                }
                else
                {
                    // Persistent intent: STAY.  Restore the home position the
                    // owner had set instead of pulling the companion back to
                    // the player.
                    Vector3 stayPos;
                    if (savedData.HasHomePosition)
                    {
                        stayPos = new Vector3(
                            savedData.HomePositionX,
                            savedData.HomePositionY,
                            savedData.HomePositionZ);
                    }
                    else
                    {
                        // No saved home - fall back to the companion's current
                        // (post-respawn) position so they at least don't run
                        // off looking for the player.  This is best-effort;
                        // a companion that was Staying without a recorded
                        // home is an old-save edge case.
                        stayPos = controller.transform.position;
                        Debug.LogWarning($"[CompanionRespawnManager] {controller.companionName} respawning in Stay mode but had no HomePosition - using current position {stayPos}");
                    }

                    var combatMovement = controller.GetComponent<CompanionCombatMovement>();
                    if (combatMovement != null)
                    {
                        combatMovement.SetHomePosition(stayPos);
                        combatMovement.SetMoveDestination(stayPos);
                    }

                    var idleBehavior = controller.GetComponent<CompanionIdleBehavior>();
                    if (idleBehavior != null)
                        idleBehavior.SetHomePosition(stayPos);

                    var companionAI = controller.GetCompanionAI();
                    if (companionAI != null)
                    {
                        companionAI.SetShouldFollow(false);
                        companionAI.SetStayPosition(stayPos);
                    }

                    Debug.Log($"[CompanionRespawnManager] Restored STAY state for {controller.companionName} at {stayPos} (owner-set intent preserved across death)");
                }
            }

        // Save to ZDO (authoritative for in-world state)
     controller.SaveToZDO();

              // Phase 6: per-event vault writes retired. The roster mirror
              // below is the authoritative "respawn complete, clear
              // pending" signal; the periodic FlushDebugMirror picks up
              // the new state on its next tick.

            // ROSTER MIRROR (Phase 3 of save refactor)
            // Side-by-side write to the player-customData roster: clears
            // IsPendingRespawn, refreshes the snapshot from the now-restored
            // live companion, preserves the prior FollowState (so a
            // Staying-and-pending-respawn companion comes back as Staying,
            // not Following). Vault still mirrored above until Phase 6.
            try
            {
                var ownerPlayer = controller.GetOwner();
                if (ownerPlayer != null)
                {
                    CompanionRosterWriter.OnRespawned(ownerPlayer, controller);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionRespawnManager] Roster mirror write failed (non-fatal): {ex.Message}");
            }

      if (VerboseLogging)
    {
     Debug.Log($"[CompanionRespawnManager] Restored {controller.companionName} from vault - tamed={controller.isTamed}, owner={controller.ownerPlayerId}");
     }
     }

      /// <summary>
 /// Helper to restore a single equipment slot.
        /// </summary>
        private void RestoreEquipmentSlot(CompanionInventory inventory, string slotKey, string prefabName, 
            Dictionary<string, int> qualities)
        {
    try
 {
      var slot = (CompanionInventory.EquipmentSlot)Enum.Parse(
 typeof(CompanionInventory.EquipmentSlot), slotKey);
                var quality = qualities != null && qualities.ContainsKey(slotKey)
             ? qualities[slotKey] : 1;

             inventory.RestoreEquipmentFromVault(slot, prefabName, quality);
    }
            catch (Exception ex)
 {
         Debug.LogWarning($"[CompanionRespawnManager] Failed to restore slot {slotKey}: {ex.Message}");
    }
        }
        
        /// <summary>
        /// Restores progression, stats, skills, and kill tracker data from vault.
        /// CRITICAL: Without this, companions lose all progress (levels, attributes, kills) on respawn!
        /// </summary>
        private void RestoreProgressionAndStatsFromVault(CompanionController controller, CompanionSaveData savedData)
        {
            if (controller == null || savedData == null) return;
            
            try
            {
                // Restore skills
                if (!string.IsNullOrEmpty(savedData.SkillsData))
                {
                    var skills = controller.GetSkills();
                    if (skills != null)
                    {
                        skills.RestoreSkillsFromVault(savedData.SkillsData);
                        if (VerboseLogging)
                            Debug.Log($"[CompanionRespawnManager] Restored skills from vault for {savedData.CompanionName}");
                    }
                }
                
                // Restore progression (level, XP, attributes)
                if (!string.IsNullOrEmpty(savedData.ProgressionData))
                {
                    var progression = controller.GetProgression();
                    if (progression != null)
                    {
                        progression.RestoreProgressionFromVault(savedData.ProgressionData);
                        if (VerboseLogging)
                            Debug.Log($"[CompanionRespawnManager] Restored progression from vault for {savedData.CompanionName}");
                    }
                }
                
                // Restore stats (death count, etc.)
                if (!string.IsNullOrEmpty(savedData.StatsData))
                {
                    var stats = controller.GetStats();
                    if (stats != null)
                    {
                        stats.RestoreStatsFromVault(savedData.StatsData);
                        if (VerboseLogging)
                            Debug.Log($"[CompanionRespawnManager] Restored stats from vault for {savedData.CompanionName}");
                    }
                }
                
                // Restore kill tracker
                if (!string.IsNullOrEmpty(savedData.KillsData))
                {
                    var killTracker = controller.GetKillTracker();
                    if (killTracker != null)
                    {
                        killTracker.RestoreKillsFromVault(savedData.KillsData);
                        if (VerboseLogging)
                            Debug.Log($"[CompanionRespawnManager] Restored kill tracker from vault for {savedData.CompanionName}");
                    }
                }
                
                Debug.Log($"[CompanionRespawnManager] Restored progression/stats data for {savedData.CompanionName}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionRespawnManager] Failed to restore progression/stats: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets a spawn position near the player.
     /// </summary>
private Vector3 GetSpawnPositionNearPlayer(Player player)
        {
            Vector3 basePos = player.transform.position;

 for (int i = 0; i < SpawnPositionAttempts; i++)
   {
 Vector2 randomOffset = UnityEngine.Random.insideUnitCircle * SpawnScatterRadius;
      Vector3 testPos = basePos + new Vector3(randomOffset.x, 0f, randomOffset.y);

                if (ZoneSystem.instance != null)
     {
         float groundHeight;
       if (ZoneSystem.instance.GetGroundHeight(testPos, out groundHeight))
       {
               testPos.y = groundHeight + SpawnHeightOffset;
        return testPos;
 }
        }
     }

       return basePos + player.transform.forward * FallbackSpawnForwardDistance + Vector3.up * SpawnHeightOffset;
    }

        /// <summary>
        /// Plays respawn visual effect.
      /// </summary>
        private void PlayRespawnEffect(Vector3 position)
        {
            Archetypes.AbilityFXManager.SpawnEffect(RespawnEffect, position);
        }

        private const string RespawnEffect = "fx_GP_Activation";
        private const string ResurrectEffect = "vfx_ghost_hit";
        
        /// <summary>
        /// Directly applies scale to a companion GameObject.
        /// This is used during respawn to ensure scale is applied immediately,
        /// not relying on delayed ZDO reads which can fail.
        /// </summary>
        private void ApplyScaleToCompanion(GameObject companionObj, float scale)
        {
            if (companionObj == null) return;
            
            // Apply uniform scale to the root transform
            companionObj.transform.localScale = new Vector3(scale, scale, scale);
            
            // Adjust capsule collider height and radius proportionally
            var capsule = companionObj.GetComponent<CapsuleCollider>();
            if (capsule != null)
            {
                // Base values (from CompanionPrefabManager)
                float baseHeight = 1.8f;
                float baseRadius = 0.3f;
                float baseCenter = 0.9f;
                
                capsule.height = baseHeight * scale;
                capsule.radius = baseRadius * scale;
                capsule.center = new Vector3(0, baseCenter * scale, 0);
            }
            
            // Adjust health based on scale (giants have more HP, dwarves less but not too much)
            var character = companionObj.GetComponent<Character>();
            if (character != null)
            {
                // Note: Don't adjust health here as it's already set from vault data
                // Just ensure the scale is applied visually
            }
            
            // Adjust rigidbody mass based on scale (affects physics interactions)
            var rigidbody = companionObj.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                float baseMass = 50f;
                // Mass scales with volume (scale^3) but we cap it for gameplay
                float massMultiplier = Mathf.Pow(scale, 2); // Use square instead of cube for balance
                rigidbody.mass = baseMass * massMultiplier;
            }
            
            Debug.Log($"[CompanionRespawnManager] Applied scale {scale:F2} to companion, localScale={companionObj.transform.localScale}");
        }

 #endregion

      #region Public API

        /// <summary>
        /// Gets the remaining time until a companion respawns.
        /// </summary>
        public float GetRemainingRespawnTime(string companionId)
        {
     if (_pendingRespawns.TryGetValue(companionId, out var data))
  {
    return Mathf.Max(0f, data.RespawnTime - Time.time);
            }
          return 0f;
        }

        /// <summary>
        /// Checks if a companion has a pending respawn.
        /// </summary>
        public bool HasPendingRespawn(string companionId)
   {
            return _pendingRespawns.ContainsKey(companionId);
        }

        /// <summary>
        /// Forces immediate respawn of a companion (admin/debug).
        /// </summary>
   public void ForceRespawn(string companionId)
   {
        if (_pendingRespawns.TryGetValue(companionId, out var data))
  {
       _pendingRespawns.Remove(companionId);
                StartCoroutine(RespawnCompanion(data));
  }
        }
        
        /// <summary>
        /// Requests immediate respawn of a defeated companion near a specific position.
        /// Used by Resurrection ability to bring back companions instantly.
        /// </summary>
        /// <param name="companionId">The companion to respawn</param>
        /// <param name="respawnPosition">Position to spawn near (e.g., healer's position)</param>
        public static void RequestImmediateRespawn(string companionId, Vector3 respawnPosition)
        {
            if (string.IsNullOrEmpty(companionId))
            {
                Debug.LogWarning("[CompanionRespawnManager] RequestImmediateRespawn called with null companionId");
                return;
            }
            
            var instance = Instance;
            if (instance == null)
            {
                Debug.LogError("[CompanionRespawnManager] Instance is null!");
                return;
            }
            
            // Check if there's a pending respawn and use that data
            if (instance._pendingRespawns.TryGetValue(companionId, out var data))
            {
                instance._pendingRespawns.Remove(companionId);
                
                // Override spawn position - will spawn near the given position
                instance.StartCoroutine(instance.RespawnCompanionAtPosition(data, respawnPosition));
                
                Debug.Log($"[CompanionRespawnManager] Immediate respawn requested for {companionId} at {respawnPosition}");
            }
            else
            {
                // No pending respawn - try to find companion data in vault and spawn
                Debug.Log($"[CompanionRespawnManager] No pending respawn for {companionId}, checking vault...");
                instance.StartCoroutine(instance.TryRespawnFromVault(companionId, respawnPosition));
            }
        }
        
        /// <summary>
        /// Respawns a companion at a specific position (used by Resurrection).
        /// </summary>
        private IEnumerator RespawnCompanionAtPosition(RespawnData data, Vector3 position)
        {
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionRespawnManager] Respawning companion {data.CompanionId} at position {position}");
            }
            
            // Get save data (Phase 4: roster-preferred)
            CompanionSaveData savedData = null;
            try
            {
                savedData = CompanionSavedDataResolver.ResolveByCompanionId(data.OwnerPlayerId, data.CompanionId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionRespawnManager] Failed to resolve save data for resurrection: {ex.Message}");
            }
            
            // Find ground at spawn position
            Vector3 spawnPos = position;
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(position, out groundHeight))
                {
                    spawnPos.y = groundHeight + 0.5f;
                }
            }
            
            // Get the prefab
            GameObject prefab = ZNetScene.instance?.GetPrefab(data.PrefabName);
            if (prefab == null)
            {
                prefab = ZNetScene.instance?.GetPrefab("CompanionNpc");
            }
            
            if (prefab == null)
            {
                Debug.LogError($"[CompanionRespawnManager] Could not find prefab {data.PrefabName}");
                yield break;
            }
            
            // Spawn the companion via ZNetScene for proper network registration
             GameObject companionObj = CompanionNetworkHelper.Spawn(prefab, spawnPos, Quaternion.identity);
             if (companionObj == null)
             {
                 Debug.LogError($"[CompanionRespawnManager] Failed to spawn companion {data.CompanionId}");
                 yield break;
             }

             // Set tamed state immediately
             var character = companionObj.GetComponent<Character>();
             if (character != null)
             {
                 character.SetTamed(true);
             }
            
            // Set on ZNetView ZDO immediately
            var nview = companionObj.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                var zdo = nview.GetZDO();
                if (zdo != null)
                {
                    zdo.Set(ZDOVars.s_tamed, true);
                    zdo.Set("companion_tamed", true);
                    zdo.Set("companion_id", data.CompanionId);
                    zdo.Set("companion_owner", data.OwnerPlayerId);
                    zdo.Set("companion_wasfollowing", savedData?.IsFollowing ?? true);
                }
            }
            
            // Configure the companion controller
            var controller = companionObj.GetComponent<CompanionController>();
            if (controller != null)
            {
                controller.companionId = data.CompanionId;
                controller.ownerPlayerId = data.OwnerPlayerId;
                controller.isTamed = true;
                controller.isDefeated = false;
                
                // Restore full state from vault
                yield return StartCoroutine(RestoreCompanionFromVault(controller, data));
            }
            
            // Play respawn effect
            PlayRespawnEffect(spawnPos);
            
            Archetypes.AbilityFXManager.SpawnEffect(ResurrectEffect, spawnPos);
            
            // Notify
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                $"{controller?.GetDisplayName() ?? "Companion"} has been resurrected!");
            
            Debug.Log($"[CompanionRespawnManager] Companion {data.CompanionId} resurrected at {spawnPos}");
        }
        
        /// <summary>
        /// Tries to respawn a companion from vault data when there's no pending respawn.
        /// </summary>
        private IEnumerator TryRespawnFromVault(string companionId, Vector3 position)
        {
            // Find the companion in vault across all players
            CompanionSaveData savedData = null;
            long ownerPlayerId = 0;
            
            try
            {
                // Walk all loaded players, asking the resolver per player.
                // Resolver prefers each player's roster; vault is fallback.
                foreach (var player in Player.GetAllPlayers())
                {
                    if (player == null) continue;
                    long playerId = player.GetPlayerID();

                    savedData = CompanionSavedDataResolver.ResolveByCompanionId(playerId, companionId);
                    if (savedData != null)
                    {
                        ownerPlayerId = playerId;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionRespawnManager] Failed to find companion via resolver: {ex.Message}");
                yield break;
            }
            
            if (savedData == null)
            {
                Debug.LogWarning($"[CompanionRespawnManager] Could not find companion {companionId} in vault for resurrection");
                yield break;
            }
            
            // Create respawn data
            var data = new RespawnData
            {
                CompanionId = companionId,
                OwnerPlayerId = ownerPlayerId,
                PrefabName = savedData.PrefabName ?? "CompanionNpc",
                RespawnTime = Time.time,
                TombstoneZDOID = ZDOID.None
            };
            
            yield return StartCoroutine(RespawnCompanionAtPosition(data, position));
        }

        /// <summary>
        /// Forces re-check of companion restoration for a player.
        /// Useful after manual login or for debugging.
        /// </summary>
        public void ForceRestoreCompanionsForPlayer(Player player)
        {
   if (player == null) return;

      long playerId = player.GetPlayerID();
             _processedPlayerLogins.Remove(playerId);

             // Will be picked up on next CheckPlayerLogins tick
     if (VerboseLogging)
  {
    Debug.Log($"[CompanionRespawnManager] Forced companion restoration for player {playerId}");
             }
         }

   #endregion
    }
}
