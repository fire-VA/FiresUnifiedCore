using HarmonyLib;
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc.AI;
using FiresCore.Npc.Movement;
using FiresCore.Npc.NpcMode;
using FiresCore.Npc.Vault;
using FiresCore.Lifecycle;
using Newtonsoft.Json;

namespace FiresCore.Npc
{
    /// <summary>
    /// Harmony patches for companion system integration with Valheim.
    /// Handles hover text, interactions, damage modification, and companion restoration on login.
    /// </summary>
    [HarmonyPatch]
    public static class CompanionPatches
    {
        // Track which players have had their companions restored this session
        private static HashSet<long> _restoredPlayers = new HashSet<long>();

        // PERF CACHE: Avoid per-frame GetComponent<CompanionController>() in Harmony patches.
        // Maps Character instance ID ? CompanionController (null means "not a companion").
        // Entries are added on first lookup and removed when Character is destroyed.
        private static readonly Dictionary<int, CompanionController> _companionLookupCache = new Dictionary<int, CompanionController>();
        private static readonly Dictionary<int, Rigidbody> _rigidbodyCache = new Dictionary<int, Rigidbody>();
        private static readonly Dictionary<int, CompanionStateController> _stateControllerCache = new Dictionary<int, CompanionStateController>();

        // PERF CACHE: Reusable list for EnemyHud key iteration (avoids per-frame allocation).
        private static readonly List<object> _tempHudKeys = new List<object>();

        /// <summary>
        /// Cached companion lookup ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â avoids GetComponent on every Character every frame.
        /// Returns null for non-companions (cached negative result).
        /// </summary>
        private static CompanionController GetCachedCompanion(Character instance)
        {
            int id = instance.GetInstanceID();
            if (!_companionLookupCache.TryGetValue(id, out var cached))
            {
                cached = instance.GetComponent<CompanionController>();
                _companionLookupCache[id] = cached;
            }
            return cached;
        }

        private static Rigidbody GetCachedRigidbody(Character instance)
        {
            int id = instance.GetInstanceID();
            if (!_rigidbodyCache.TryGetValue(id, out var cached))
            {
                cached = instance.GetComponent<Rigidbody>();
                _rigidbodyCache[id] = cached;
            }
            return cached;
        }

        private static CompanionStateController GetCachedStateController(Character instance)
        {
            int id = instance.GetInstanceID();
            if (!_stateControllerCache.TryGetValue(id, out var cached))
            {
                cached = instance.GetComponent<CompanionStateController>();
                _stateControllerCache[id] = cached;
            }
            return cached;
        }

        /// <summary>
        /// Clears cache entries for a destroyed Character.
        /// Called from Character.OnDestroy patch or when ZNetScene resets.
        /// </summary>
        internal static void ClearCacheForCharacter(int instanceId)
        {
            _companionLookupCache.Remove(instanceId);
            _rigidbodyCache.Remove(instanceId);
            _stateControllerCache.Remove(instanceId);
        }

        // NOTE: Companion login restoration is now handled server-side by
        // CompanionRestoreService (ZNet.OnNewConnection postfix). The old
        // Game.SpawnPlayer trigger and its DelayedCompanionRestore coroutine
        // were removed because they ran on the connecting client before the
        // client's ZDOMan had received every companion ZDO from the server,
        // which caused the duplication bug. _restoredPlayers below is still
        // used by Player_OnSpawned_Postfix to distinguish first login (server
        // is restoring) from death respawn (companions are stranded ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â reel
        // them in).

        /// <summary>
        /// Pre-arm the companion-teleport suppression at the moment the local
        /// player dies. Player_OnSpawned_Postfix already raises this flag for
        /// the death-respawn branch, but OnSpawned only fires AFTER the new
        /// player object is constructed ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â leaving a multi-second gap (death ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢
        /// "Starting respawn" ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ fade ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ "Local player destroyed" ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ new player
        /// spawned ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ OnSpawned) during which the previous flag has expired and
        /// the new one isn't set yet. CheckFollowTeleport ticks fire during
        /// that gap, hit owner==null, and used to dismiss companions wholesale.
        /// Setting the flag from OnDeath closes the gap at its earliest point.
        ///
        /// The 30 s window is intentionally generous: it covers fade-out
        /// (~2 s), loading screen (variable, observed up to ~25 s on heavy
        /// saves), and the wakeup settle. Player_OnSpawned_Postfix replaces
        /// this with its own narrower 10 s window once the new player is up.
        /// </summary>
        [HarmonyPatch(typeof(Player), "OnDeath")]
        [HarmonyPostfix]
        public static void Player_OnDeath_Postfix(Player __instance)
        {
            try
            {
                if (__instance != Player.m_localPlayer) return;
                SuppressCompanionTeleportsUntil = Time.unscaledTime + SUPPRESS_DURATION_DEATH;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] Player_OnDeath_Postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Patch Player.OnSpawned to:
        ///  1. Re-register NPC data sync RPCs (they go stale across logout/login).
        ///  2. Trigger the companion reconcile-on-arrival flow for the
        ///     transitions that DO fire OnSpawned: initial login (engine's
        ///     Game.SpawnPlayer ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ Player.AwakeAndSpawn) and death-respawn.
        ///     Wayshrine / portal / dungeon teleports go through
        ///     Player.TeleportTo, which reuses the existing player object
        ///     and never re-invokes OnSpawned ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â those are picked up by
        ///     Player_TeleportTo_Postfix instead. Both entry points run
        ///     the same <see cref="ReconcileFollowersAfterArrival"/>
        ///     coroutine, which waits for IsTeleporting=false and
        ///     CanMove=true and then dispatches
        ///     <see cref="CompanionTeleportService.RequestReconcileFollowers"/>.
        /// </summary>
        [HarmonyPatch(typeof(Player), "OnSpawned")]
        [HarmonyPostfix]
        public static void Player_OnSpawned_Postfix(Player __instance)
        {
            try
            {
                if (__instance != Player.m_localPlayer) return;

                // DIAG (login-freeze)
                Debug.Log("[LoginFreeze][DIAG] ENTER CompanionPatches.Player_OnSpawned_Postfix");

                // Re-register NPC data sync RPCs if needed (they go stale
                // after logout/login because ZRoutedRpc.instance gets
                // destroyed during scene transition).
                FiresCore.Bridge.NpcHostBridge.RegisterServerRpcs();

                long playerId = __instance.GetPlayerID();
                if (!_restoredPlayers.Contains(playerId))
                    _restoredPlayers.Add(playerId);

                // Suppress local CheckFollowTeleport for the wakeup window
                // so it doesn't fire spurious teleports while the player
                // is still mid-transition. The reconcile coroutine below
                // is the authoritative path for moving companions to the
                // player's new position.
                SuppressCompanionTeleportsUntil = Time.unscaledTime + SUPPRESS_DURATION_RESPAWN;

                __instance.StartCoroutine(ReconcileFollowersAfterArrival(__instance));

                // DIAG (login-freeze)
                Debug.Log("[LoginFreeze][DIAG] EXIT CompanionPatches.Player_OnSpawned_Postfix (ReconcileFollowersAfterArrival coroutine started)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] Player_OnSpawned error: {ex.Message}");
            }
        }

        /// <summary>
        /// Coroutine that waits for the local player to fully arrive at a
        /// new position (loading screen finished, wakeup animation done,
        /// CanMove returns true) and then dispatches a single reconcile
        /// request to the server.
        ///
        /// Why we wait: <c>Player.OnSpawned</c> fires near the end of the
        /// transition but <c>IsTeleporting()</c> can still be true and
        /// <c>CanMove()</c> can still be false for several frames (longer
        /// for heavy saves). Sending the reconcile request before those
        /// settle would race with the destination zone load ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â the server
        /// gets the position the local player REPORTED but the
        /// destination ZDOMan sectors may not be fully populated yet, so
        /// the companion ZDO position write goes out into a half-loaded
        /// zone. Waiting for stable IsTeleporting=false + CanMove=true
        /// guarantees the server-side teleport lands cleanly.
        ///
        /// Also extends the local CheckFollowTeleport suppression every
        /// frame while IsTeleporting is still true, so individual
        /// companion controllers never compete with the in-flight
        /// reconcile.
        /// </summary>
        private static IEnumerator ReconcileFollowersAfterArrival(Player player)
        {
            const float TIMEOUT = 30f;
            float waitStart = Time.realtimeSinceStartup;

            // Phase 1: wait for IsTeleporting to clear. Loading screen
            // takes a variable amount of time depending on world size.
            while (player != null && Player.m_localPlayer == player
                   && player.IsTeleporting()
                   && (Time.realtimeSinceStartup - waitStart) < TIMEOUT)
            {
                SuppressCompanionTeleportsUntil = Time.unscaledTime + SUPPRESS_DURATION_LOADING_REARM;
                yield return null;
            }

            if (player == null || Player.m_localPlayer != player) yield break;

            // Phase 2: wait for the wakeup animation. CanMove() returns
            // true once the character has full control.
            float wakeupStart = Time.realtimeSinceStartup;
            while (player != null && Player.m_localPlayer == player
                   && !player.CanMove()
                   && (Time.realtimeSinceStartup - wakeupStart) < TIMEOUT)
            {
                yield return null;
            }

            if (player == null || Player.m_localPlayer != player) yield break;

            // One extra frame so the world finishes the first update tick
            // at full player control before we fire the RPC.
            yield return null;

            if (player == null || Player.m_localPlayer != player) yield break;

            // DIAG (login-freeze): bracket the reconcile dispatch + coroutine end so
            // we know the OLD char repro freezes AFTER the reconcile coroutine itself
            // returns (i.e. in subsequent Update ticks, not inside the dispatch path).
            Debug.Log("[CompanionPatches][DIAG] ReconcileFollowersAfterArrival: about to dispatch reconcile RPC");
            Core.CompanionTeleportService.RequestReconcileFollowers(player);
            Debug.Log("[CompanionPatches][DIAG] ReconcileFollowersAfterArrival: reconcile dispatched, coroutine ending");
        }

        // TeleportFollowersToPlayerAfterRespawn was the death-respawn-only
        // companion reel-in coroutine. It ran two passes (loaded
        // companions via TeleportToOwner, then unloaded ZDOs via direct
        // position rewrite) and was wired only into the death-respawn
        // branch of Player.OnSpawned.
        //
        // Replaced by reconcile-on-arrival, dispatched from two hooks:
        //   Player.OnSpawned (login, death-respawn) and
        //   Player.TeleportTo (wayshrine, portal, dungeon).
        // Both paths run ReconcileFollowersAfterArrival, which dispatches
        // CompanionTeleportService.RequestReconcileFollowers; the server
        // scans ZDOMan for the player's followers and teleports the ones
        // far from the player position.
        //
        // The server-side ZDOMan scan is functionally equivalent to the
        // old pass-2 (and a superset of pass-1, since the server is the
        // authoritative writer for any ZDO it claims, regardless of
        // whether a client has the GameObject loaded).

        /// <summary>
        /// Patch ZNetScene.Awake to handle scene reloads
        /// This ensures companion prefabs are re-registered when the ZNetScene is recreated
        /// CRITICAL: Without this, companions cannot be placed with hammer after logout/login
        /// </summary>
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        [HarmonyPostfix]
        public static void ZNetScene_Awake_Postfix(ZNetScene __instance)
        {
            try
            {
                // Clear restoration tracking on scene reload so companions can be restored again
                Debug.Log("[CompanionPatches] ZNetScene awakened - clearing restoration tracking for scene change");
                _restoredPlayers.Clear();
                CompanionRestoreService.ResetSessionTracking();

                // PERF: Clear component caches ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â all Characters are destroyed on scene reload
                _companionLookupCache.Clear();
                _rigidbodyCache.Clear();
                _stateControllerCache.Clear();

                // CRITICAL: Re-register companion prefabs with the new ZNetScene instance
                // This mirrors what ZNetScenePatches does for piecePrefabs/itemPrefabs
                CompanionPrefabManager.RegisterWithZNetScene();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] ZNetScene_Awake error: {ex.Message}");
            }
        }

        /// <summary>
        /// Patch ZNet.RPC_CharacterID (server-side) to push all config files ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â including
        /// UILayouts ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â to the newly connected client. RPC_CharacterID fires once the
        /// client's character ZDO has spawned and the server has its UID ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â i.e. the
        /// connecting player is fully alive on the server side. This is the right
        /// trigger for heavy server-side work that needs the player's character
        /// established.
        ///
        /// Runs inline at RPC_CharacterID time ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â right after the eager RPC_PeerInfo push
        /// at <see cref="VaultPatches"/>. FiresSteamworksPatcher's network-rate bumps
        /// make the config stream fast enough that we no longer need the previous
        /// AutoTune-coordinated defer; the bulk push completes well before
        /// FiresGhettoNetworkMod's probe begins competing for bandwidth (probe gates on
        /// Game.m_playerInitialSpawn + 30s settle delay, which fires later in the
        /// connection sequence than RPC_CharacterID).
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
        [HarmonyPostfix]
        public static void ZNet_RPC_CharacterID_ConfigSync_Postfix(ZNet __instance, ZRpc rpc)
        {
            try
            {
                if (!__instance.IsServer()) return;

                ZNetPeer peer = __instance.GetPeer(rpc);
                if (peer == null) return;

                long peerUid = peer.m_uid;
                if (peerUid == 0L) return;

                // Ensure UILayoutSyncRPC handlers are registered (safe to call multiple times).
                FiresCore.UI.UILayoutSyncRPC.EnsureRpcsRegistered();

                // Push all server config files (Quests, Dialogues, UILayouts, etc.) to client.
                FiresCore.Bridge.NpcHostBridge.PushConfigsToClient(peerUid);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] ZNet_RPC_CharacterID config sync error: {ex.Message}");
            }
        }

        // DelayedCompanionRestore was the client-side coroutine that ran the
        // dedupe + RestoreCompanionsFromVault on a 3-second delay after the
        // local player spawned. It was removed when restoration moved to
        // CompanionRestoreService (server-authoritative). The wait conditions
        // (ZNetScene readiness, PlayerSpawnGate, post-handshake delay) are
        // preserved in CompanionRestoreService.WaitForPeerAndRestore.

        /// <summary>
        /// Restores companions from the player's vault data.
        ///
        /// As of the server-restoration refactor this is the single source of
        /// truth for "spawn the missing companions, leave the existing ones,
        /// reconcile follow state."
        ///
        /// CALLED FROM:
        /// - <c>CompanionRestoreService.RestoreForPlayerServerSide</c> on the
        ///   dedicated server, with <paramref name="player"/> = <c>null</c>
        ///   and <paramref name="ownerPos"/> resolved from the peer's
        ///   character ZDO position.
        /// - <c>CompanionRestoreService.ForceRestoreForLocalPlayer</c> on
        ///   listen-host / single-player, with <paramref name="player"/> =
        ///   <c>Player.m_localPlayer</c> and <paramref name="ownerPos"/> =
        ///   the player's transform position.
        ///
        /// NULL-PLAYER HANDLING:
        /// When <paramref name="player"/> is null, every code path that
        /// would otherwise touch <c>m_customData</c> (vault ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ roster
        /// migrator, follow-registry import / read) skips with a fallback
        /// to the vault entry's own <c>IsFollowing</c> flag. Spawn
        /// positions use <paramref name="ownerPos"/> directly. Follow
        /// commands degrade to a direct <c>companion_wasfollowing</c> ZDO
        /// write ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â the client-side <c>CompanionController</c> reads that
        /// flag in <c>LoadFromZDO</c> when its own zone loads and
        /// reconciles the in-memory follow target with the local Player.
        /// </summary>
        internal static void RestoreCompanionsFromVault(Player player, long playerId, Vector3 ownerPos = default)
        {
            // CRITICAL: Get the current realm ID to filter companions by world/server
            // This prevents companions tamed on one server from appearing on a different server
            long currentRealmId = CompanionController.GetCurrentRealmId();
            if (currentRealmId == 0)
            {
                Debug.LogWarning("[CompanionPatches] Could not determine current realm ID - companion restoration may include cross-server companions");
            }

            // Phase 7 (one-shot migration): if this player has nothing on
            // their roster but the vault still has companion entries,
            // copy vault ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ roster before the resolver runs. After this
            // returns the resolver finds the migrated entries on the
            // roster and can stop consulting the vault on subsequent
            // logins. Idempotent: re-runs are no-ops once the roster has
            // any entries.
            // Derive the effective owner position: caller-supplied takes
            // priority, fall back to player.transform when we have a
            // Player object, fall back to Vector3.zero otherwise (caller
            // already logged a warning when ownerPos defaulted).
            Vector3 effectiveOwnerPos = ownerPos != default
                ? ownerPos
                : (player != null ? player.transform.position : Vector3.zero);

            // Vault ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ roster migration only meaningful when we have a
            // live Player object (it writes to m_customData). Skipping
            // server-side is fine: the migration is just a one-time copy
            // of legacy vault entries into the roster, and the roster is
            // only read client-side via PlayerCompanionStorage.TryGetRoster
            // (which falls back to the vault when the roster is empty).
            if (player != null)
            {
                try
                {
                    PlayerCompanionMigrator.MigrateFromVaultIfNeeded(player);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionPatches] PlayerCompanionMigrator.MigrateFromVaultIfNeeded failed (non-fatal): {ex.Message}");
                }
            }

            // Phase 4 read switch: prefer the per-player roster on
            // Player.m_customData; fall back to JSON vault if the roster
            // is empty / unavailable. The resolver also handles:
            //   ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¢ server-binding filter (entries from other server world UIDs are skipped),
            //   ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¢ Dismissed entries excluded (no auto-spawn on login),
            //   ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€šÃ‚Â¢ respawn-deadline ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ seconds-remaining translation
            //     so downstream code below sees consistent fields
            //     regardless of source.
            // The legacy realm-id filter further down still runs as
            // belt-and-suspenders during the side-by-side migration window.
            List<CompanionSaveData> savedCompanions = null;
            try
            {
                savedCompanions = CompanionSavedDataResolver.ResolveAllForPlayer(playerId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] Failed to resolve companions: {ex.Message}");
                return;
            }

            if (!FiresCore.Bridge.CompanionVaultBridge.IsVaultAvailable() && (savedCompanions == null || savedCompanions.Count == 0))
            {
                Debug.LogWarning("[CompanionPatches] VaultOfKnowledge not available and roster empty - cannot restore companions");
                return;
            }

            if (savedCompanions == null || savedCompanions.Count == 0)
            {
                Debug.Log($"[CompanionPatches] No companions to restore for player {playerId}");
                return;
            }

            // CRITICAL: Filter companions by realm ID to prevent cross-server contamination
            // Only restore companions that were tamed on this specific world/server
            int originalCount = savedCompanions.Count;
            if (currentRealmId != 0)
            {
                savedCompanions = savedCompanions.Where(c =>
                    c.RealmId == 0 || // Legacy companions without realm ID (before this fix)
                    c.RealmId == currentRealmId // Companions from this realm
                ).ToList();

                int filteredCount = originalCount - savedCompanions.Count;
                if (filteredCount > 0)
                {
                    Debug.Log($"[CompanionPatches] Filtered out {filteredCount} companion(s) from different realms (current realm: {currentRealmId})");
                }
            }

            Debug.Log($"[CompanionPatches] Found {savedCompanions.Count} companion(s) in vault for player {playerId} (realm: {currentRealmId})");

            // ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ PLAYER FOLLOWING REGISTRY MIGRATION & SELF-HEAL ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬
            // The authoritative "is this companion set to follow" flag now
            // lives on the OWNER PLAYER'S ZDO via PlayerFollowingRegistry.
            // On first login under the new system the registry is empty, so
            // import every vault entry that says IsFollowing=true.  Re-running
            // every login is also a self-heal in case the registry ever drifts.
            // After this point the registry is authoritative for restore
            // decisions ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â vault.IsFollowing is just the cached mirror.
            //
            // Server-side path (player == null): skipped because the
            // registry lives on the owner Player's m_customData and we
            // don't have one. We fall back to vault.IsFollowing as the
            // authoritative source for the per-companion loop below
            // (see ResolveRegistryFollowing helper), and the registry
            // self-heals client-side on the next normal client restore.
            if (player != null)
            {
                var followingIds = new List<string>();
                foreach (var sd in savedCompanions)
                {
                    if (sd == null) continue;
                    if (sd.IsStationedAsNpc) continue;
                    if (!sd.IsFollowing) continue;
                    if (string.IsNullOrEmpty(sd.CompanionId)) continue;
                    followingIds.Add(sd.CompanionId);
                }
                if (followingIds.Count > 0)
                {
                    PlayerFollowingRegistry.ImportLegacyIds(player, followingIds);
                }
            }

            // Companion-persistence mode flag. When true, we adopt existing ZDOs
            // (live or unloaded) rather than destroying and re-spawning. The
            // destroy-stale sweeps below are skipped in persistent mode because
            // those ZDOs are no longer "stale" ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â they're the canonical source.
            bool persistent = FiresCore.Bridge.NpcConfigBridge.GetBool("PersistentCompanions", true);

            // Persistent mode: index every existing companion ZDO in the world by
            // its companion_id. Walks ZDOMan once for all known companion prefabs;
            // server has full visibility so this catches both loaded and unloaded
            // zones. Used by the per-companion restore loop to adopt existing
            // ZDOs instead of falling through to the spawn-from-vault path.
            // Non-persistent mode leaves the dict empty (and the adopt branch is
            // never taken).
            var existingZdoByCompanionId = new Dictionary<string, ZDO>();
            if (persistent && ZDOMan.instance != null)
            {
                var scanBuf = new List<ZDO>();
                foreach (var prefabName in _companionPrefabNamesForScan)
                {
                    scanBuf.Clear();
                    int idx = 0;
                    while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, scanBuf, ref idx)) { }
                    for (int i = 0; i < scanBuf.Count; i++)
                    {
                        var zdo = scanBuf[i];
                        if (zdo == null || !zdo.IsValid()) continue;
                        string cid = zdo.GetString("companion_id");
                        if (string.IsNullOrEmpty(cid)) continue;
                        // First match wins. The dedup sweep that runs alongside
                        // restore consolidates duplicates, so subsequent
                        // collisions for the same id are stale-by-now.
                        if (!existingZdoByCompanionId.ContainsKey(cid))
                            existingZdoByCompanionId[cid] = zdo;
                    }
                }
                if (existingZdoByCompanionId.Count > 0)
                    Debug.Log($"[CompanionPatches] Persistent mode: indexed {existingZdoByCompanionId.Count} existing companion ZDO(s) for adoption");
            }

            // Build a "preserve" set: following companions whose live Unity instance is
            // already in the world (their ZDO loaded with the zone). We do NOT want to
            // destroy + re-spawn these ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â we just teleport them to the player.
            // Anything in preserveIds is excluded from the destroy sweeps below.
            // (In persistent mode this is subsumed by the adopt-existing-ZDO path,
            // but the set is still built for the legacy-mode code path.)
            var preserveIds = new HashSet<string>();
            {
                var liveById = new Dictionary<string, CompanionController>();
                foreach (var c in UnityEngine.Object.FindObjectsByType<CompanionController>(UnityEngine.FindObjectsSortMode.None))
                {
                    if (c == null || c.ownerPlayerId != playerId) continue;
                    if (string.IsNullOrEmpty(c.companionId)) continue;
                    if (c.isStaticPlacement) continue;
                    var npcMod = c.GetComponent<CompanionNpcModule>();
                    if (npcMod != null && npcMod.IsStationedAsNpc) continue;

                    var nv = c.GetComponent<ZNetView>();
                    if (nv == null || !nv.IsValid()) continue;

                    // Only the first live instance per ID is preserved; any later
                    // duplicates will be cleaned up by the dedup sweep below.
                    if (!liveById.ContainsKey(c.companionId))
                        liveById[c.companionId] = c;
                }

                foreach (var sd in savedCompanions)
                {
                    if (sd == null || string.IsNullOrEmpty(sd.CompanionId)) continue;
                    if (sd.IsStationedAsNpc) continue;
                    if (sd.IsPendingRespawn) continue; // dead ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ must respawn fresh
                    // Server-side (player == null): registry lookup unavailable;
                    // fall back to the vault entry's own IsFollowing flag.
                    bool follows = player != null
                        ? PlayerFollowingRegistry.IsFollowing(player, sd.CompanionId)
                        : sd.IsFollowing;
                    if (!follows) continue;
                    if (!liveById.ContainsKey(sd.CompanionId)) continue;
                    preserveIds.Add(sd.CompanionId);
                }

                if (preserveIds.Count > 0)
                    Debug.Log($"[CompanionPatches] Preserving {preserveIds.Count} live following companion(s) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â will teleport instead of re-spawning");
            }

            // Build the set of companion IDs that we'll actually RE-SPAWN from vault.
            // Only following or pending-respawn companions get re-spawned, so only
            // those need their stale ZDOs destroyed. Stay-mode companions should keep
            // their ZDOs ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â we skip them in the restore loop anyway. Preserved (live
            // following) companions are also excluded so we don't destroy them.
            //
            // Use the REGISTRY here (not vault.IsFollowing) so a clobbered vault
            // flag can no longer un-follow a companion the player explicitly set.
            var vaultIds = new HashSet<string>();
            foreach (var c in savedCompanions)
            {
                if (c == null || string.IsNullOrEmpty(c.CompanionId)) continue;
                if (c.IsStationedAsNpc) continue;
                if (preserveIds.Contains(c.CompanionId)) continue; // alive ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â leave alone
                // Server-side fallback to vault flag, same as preserveIds loop above.
                bool registrySaysFollowing = player != null
                    ? PlayerFollowingRegistry.IsFollowing(player, c.CompanionId)
                    : c.IsFollowing;
                if (!registrySaysFollowing && !c.IsPendingRespawn) continue; // stay-mode ? leave ZDO alone
                vaultIds.Add(c.CompanionId);
            }

            // ZDO-level "stale" sweep ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â LEGACY MODE ONLY.
            // In legacy mode, vault is the source of truth and any existing
            // ZDO matching a vault entry must be destroyed so we re-spawn
            // fresh. In persistent mode the existing ZDO IS the source of
            // truth (we just adopt it), so this sweep would destroy the
            // canonical entity. Skip it in persistent mode.
            if (!persistent)
            {
                DestroyStaleCompanionZDOs(playerId, vaultIds);
            }

            // Same-companion-id dedup sweep ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â runs in BOTH modes.
            // Catches actual duplicates (two CompanionControllers with the
            // same companion_id, e.g. legacy data left over from earlier
            // bug-fix rounds). Persistent mode benefits from this too ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â any
            // duplicates produced by the old destroy/re-spawn flow get
            // consolidated to one instance on first persistent-mode login.
            {
                var allControllers = UnityEngine.Object.FindObjectsByType<CompanionController>(UnityEngine.FindObjectsSortMode.None);
                var seen = new HashSet<string>();
                int dupesDestroyed = 0;
                foreach (var c in allControllers)
                {
                    if (c == null || c.ownerPlayerId != playerId) continue;
                    if (string.IsNullOrEmpty(c.companionId)) continue;
                    if (!seen.Add(c.companionId))
                    {
                        CompanionNetworkHelper.Destroy(c.gameObject);
                        dupesDestroyed++;
                    }
                }
                if (dupesDestroyed > 0)
                    Debug.Log($"[CompanionPatches] Dedup sweep destroyed {dupesDestroyed} duplicate companion(s)");
            }

            // Destroy-instance-matching-vault-IDs sweep ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â LEGACY MODE ONLY.
            // Same reason as above: in persistent mode the existing instance
            // is the canonical entity and we adopt it; destroying it would
            // be the very bug we're trying to eliminate.
            if (!persistent)
            {
                var allControllers = UnityEngine.Object.FindObjectsByType<CompanionController>(UnityEngine.FindObjectsSortMode.None);
                int staleDestroyed = 0;
                foreach (var c in allControllers)
                {
                    if (c == null || c.ownerPlayerId != playerId) continue;
                    if (string.IsNullOrEmpty(c.companionId)) continue;
                    if (!vaultIds.Contains(c.companionId)) continue;

                    // Skip static placed NPCs and stationed NPCs
                    if (c.isStaticPlacement) continue;
                    var npcModule = c.GetComponent<CompanionNpcModule>();
                    if (npcModule != null && npcModule.IsStationedAsNpc) continue;

                    CompanionNetworkHelper.Destroy(c.gameObject);
                    staleDestroyed++;
                }
                if (staleDestroyed > 0)
                    Debug.Log($"[CompanionPatches] Destroyed {staleDestroyed} stale companion instance(s) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â will re-spawn from vault");
            }

            int restoredCount = 0;
            int skippedCount = 0;
            int failedCount = 0;

            foreach (var companionData in savedCompanions)
            {
                try
                {
                    if (companionData == null)
                    {
                        Debug.LogWarning("[CompanionPatches] Null companion data in vault");
                        failedCount++;
                        continue;
                    }

                    Debug.Log($"[CompanionPatches] Processing companion: {companionData.CompanionName} (ID: {companionData.CompanionId}), IsFollowing={companionData.IsFollowing}, IsPendingRespawn={companionData.IsPendingRespawn}");

                    // Stationed NPCs live permanently in the world ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â never touch them.
                    if (companionData.IsStationedAsNpc)
                    {
                        Debug.Log($"[CompanionPatches] Companion {companionData.CompanionName} is stationed as NPC - skipping restoration (exists in world)");
                        skippedCount++;
                        continue;
                    }

                    // AUTHORITATIVE follow flag for this companion = registry.
                    // Vault.IsFollowing is only the migrated mirror ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â except
                    // server-side (player == null), where the registry isn't
                    // accessible and the vault flag IS the source of truth.
                    bool registryFollowing = player != null
                        ? PlayerFollowingRegistry.IsFollowing(player, companionData.CompanionId)
                        : companionData.IsFollowing;

                    // Stay-mode companions (not following, not pending respawn) have a fixed
                    // home position in the world. The correct behaviour is:
                    //   * Instance still present  ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ leave it completely alone.
                    //   * Instance gone (ZDO loss) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ re-spawn at home position.
                    // NEVER destroy a stay-mode instance and then skip re-spawning it ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â that
                    // is the exact bug that caused companions to vanish on login.
                    if (!registryFollowing && !companionData.IsPendingRespawn)
                    {
                        // CompanionExistsAnywhere covers BOTH a live CompanionController
                        // (zone loaded) AND a persistent ZDO whose zone isn't loaded.
                        // Re-spawning when the original is just unloaded creates a
                        // duplicate that materialises later as a second tamed
                        // CompanionNpc/CompanionNpc_Wild not in the roster.
                        if (CompanionExistsAnywhere(companionData.CompanionId))
                        {
                            Debug.Log($"[CompanionPatches] Stay-mode companion {companionData.CompanionName} already present in world (loaded or unloaded) - leaving in place");
                            skippedCount++;
                            continue;
                        }

                        // Instance and ZDO are both gone ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â re-spawn at the saved home position.
                        Debug.Log($"[CompanionPatches] Stay-mode companion {companionData.CompanionName} has no world instance OR ZDO - re-spawning at home position");

                        string stayPrefab = companionData.PrefabName ?? "CompanionNpc";
                        if (!CompanionPrefabManager.HasCompanionPrefab(stayPrefab))
                            stayPrefab = "CompanionNpc";

                        Vector3 stayPos = companionData.HasHomePosition
                            ? new Vector3(companionData.HomePositionX, companionData.HomePositionY, companionData.HomePositionZ)
                            : GetSafeSpawnPosition(player, effectiveOwnerPos);

                        var stayGO = CompanionPrefabManager.SpawnCompanion(stayPrefab, stayPos, Quaternion.identity);
                        if (stayGO != null)
                        {
                            var stayCtrl = stayGO.GetComponent<CompanionController>();
                            if (stayCtrl != null)
                            {
                                stayCtrl.companionId         = companionData.CompanionId;
                                stayCtrl.companionName       = companionData.CompanionName;
                                stayCtrl.displayNameOverride = companionData.DisplayNameOverride;
                                stayCtrl.isTamed             = true;
                                stayCtrl.ownerPlayerId       = playerId;

                                // CRITICAL: Set vanilla Character.IsTamed() too. CompanionController.UpdateCompanionBehavior
                                // polls _character.IsTamed() every frame and overwrites isTamed back to whatever vanilla
                                // says ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â without this call, our isTamed=true above flips back to false next tick and the
                                // companion shows the wild "give X coins to recruit" prompt despite following.
                                var stayChar = stayGO.GetComponent<Character>();
                                if (stayChar != null) stayChar.SetTamed(true);
                                var stayNview = stayGO.GetComponent<ZNetView>();
                                if (stayNview != null && stayNview.IsValid())
                                {
                                    var stayZdo = stayNview.GetZDO();
                                    if (stayZdo != null) stayZdo.Set(ZDOVars.s_tamed, true);
                                }

                                var idleBehavior = stayCtrl.GetComponent<CompanionIdleBehavior>();
                                if (idleBehavior != null && companionData.HasHomePosition)
                                    idleBehavior.SetHomePosition(stayPos);

                                RestoreProgressionAndStatsFromVault(stayCtrl, companionData);
                                ClearRespawnStateInVault(playerId, companionData.CompanionId);
                                stayCtrl.SaveToZDO();
                                restoredCount++;
                                Debug.Log($"[CompanionPatches] Re-spawned stay-mode companion {companionData.CompanionName} at {stayPos}");
                            }
                            else
                            {
                                // Spawn succeeded but controller wiring failed ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â the
                                // ZNetView is already registered with ZNetScene, so
                                // raw Object.Destroy here would leave a stale m_instances
                                // entry that NREs in ZNetScene.RemoveObjects later.
                                CompanionNetworkHelper.Destroy(stayGO);
                                failedCount++;
                            }
                        }
                        else
                        {
                            failedCount++;
                        }
                        continue;
                    }

                    // Pending respawn ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â check whether the timer expired offline.
                    if (companionData.IsPendingRespawn)
                    {
                        Debug.Log($"[CompanionPatches] Companion {companionData.CompanionName} was pending respawn - checking timer");

                        long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        long timeSinceDeath   = currentTimestamp - companionData.DeathTimestamp;
                        float remainingTime   = companionData.RespawnTimeRemaining - timeSinceDeath;

                        if (remainingTime <= 0)
                        {
                            // Timer expired while offline ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â fall through to spawn logic below.
                            Debug.Log($"[CompanionPatches] Respawn timer expired ({timeSinceDeath}s since death) - spawning {companionData.CompanionName}");
                        }
                        else
                        {
                            Debug.Log($"[CompanionPatches] Respawn timer still active ({remainingTime:F0}s remaining) - scheduling respawn for {companionData.CompanionName}");

                            CompanionRespawnManager.Instance?.ScheduleRespawn(
                                companionData.CompanionId,
                                companionData.OwnerPlayerId,
                                companionData.PrefabName ?? "CompanionNpc",
                                remainingTime,
                                ZDOID.None
                            );

                            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                                $"{companionData.CompanionName ?? "Companion"} respawning in {remainingTime:F0}s...");

                            skippedCount++;
                            continue;
                        }
                    }

                    // Following companion (or expired respawn): destroy any stale instance
                    // then re-spawn fresh from vault so progression data is authoritative.
                    //
                    // EXCEPTION: if a live instance is already in the world (preserved
                    // above), just teleport it to the player and update follow state.
                    // Re-spawning a healthy companion on every login is wasteful and
                    // was causing the "we logged in following, then got re-spawned" bug.
                    if (preserveIds.Contains(companionData.CompanionId))
                    {
                        var live = FindExistingCompanion(companionData.CompanionId);
                        if (live != null)
                        {
                            Vector3 tpPos = GetSafeSpawnPosition(player, effectiveOwnerPos);
                            live.transform.position = tpPos;

                            // Reset velocity so they don't keep their pre-teleport momentum
                            var rb = live.GetComponent<Rigidbody>();
                            if (rb != null)
                            {
                                rb.linearVelocity = Vector3.zero;
                                rb.angularVelocity = Vector3.zero;
                            }

                            // Make sure they're following (registry is authoritative
                            // when present; vault flag is the source of truth
                            // server-side). When player is null we can't call
                            // CommandFollow ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â set the persistent follow flag on
                            // the ZDO directly so the client-side controller
                            // reconciles when it sees the companion.
                            if (registryFollowing)
                            {
                                if (player != null)
                                {
                                    live.CommandFollow(player);
                                }
                                else
                                {
                                    var nv = live.GetComponent<ZNetView>();
                                    var z = nv != null && nv.IsValid() ? nv.GetZDO() : null;
                                    if (z != null) z.Set("companion_wasfollowing", true);
                                }
                            }

                            ClearRespawnStateInVault(playerId, companionData.CompanionId);
                            live.SaveToZDO();
                            restoredCount++;
                            Debug.Log($"[CompanionPatches] Teleported live following companion {companionData.CompanionName} to player at {tpPos} (skipped re-spawn)");
                            continue;
                        }
                        // Fallthrough: no live instance found despite preserve flag ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â re-spawn as normal.
                    }

                    // PERSISTENT MODE: adopt existing ZDO (unloaded zone) by
                    // rewriting its position so it materialises near the player
                    // when its zone loads (the zone-load loop is driven by
                    // TamedCompanionZoneLoader on the server). Skips the
                    // destroy + spawn-fresh path entirely ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â the existing ZDO is
                    // the canonical entity.
                    //
                    // The "live instance" case is already handled by the
                    // preserveIds branch above. This branch covers the case
                    // where the companion's GameObject was unloaded server-side
                    // (zone went out of scope) but the ZDO is still in ZDOMan.
                    if (persistent && existingZdoByCompanionId.TryGetValue(companionData.CompanionId, out var adoptZdo))
                    {
                        try
                        {
                            // Server claims ownership so it has authority to
                            // write the new position.
                            adoptZdo.SetOwner(ZDOMan.GetSessionID());

                            if (registryFollowing)
                            {
                                Vector3 tpPos = GetSafeSpawnPosition(player, effectiveOwnerPos);
                                adoptZdo.SetPosition(tpPos);
                                adoptZdo.DataRevision++;
                                ZDOMan.instance?.ForceSendZDO(adoptZdo.m_uid);
                                Debug.Log($"[CompanionPatches] Adopted unloaded companion ZDO {companionData.CompanionName} ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ {tpPos} (will materialise on zone load)");
                            }
                            else
                            {
                                // Stay-mode reaches this branch only if the stay-mode
                                // CompanionExistsAnywhere check above was bypassed
                                // (registryFollowing flipped mid-flight), which is
                                // unusual. Just leave the ZDO at its current position.
                                Debug.Log($"[CompanionPatches] Adopted unloaded companion ZDO {companionData.CompanionName} (left at saved position)");
                            }

                            ClearRespawnStateInVault(playerId, companionData.CompanionId);
                            restoredCount++;
                            continue;
                        }
                        catch (Exception adoptEx)
                        {
                            Debug.LogWarning($"[CompanionPatches] Adopt path threw for {companionData.CompanionName}: {adoptEx.Message} ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â falling through to spawn-from-vault");
                            // Fallthrough to spawn-from-vault as a recovery path.
                        }
                    }

                    var existing = DestroyDuplicateCompanions(companionData.CompanionId);
                    if (existing != null)
                    {
                        Debug.Log($"[CompanionPatches] Destroying surviving instance of {companionData.CompanionId} to re-spawn from vault");
                        CompanionNetworkHelper.Destroy(existing.gameObject);
                    }

                    // Verify prefab exists before spawning
                    string prefabName = companionData.PrefabName ?? "CompanionNpc";
                    if (!CompanionPrefabManager.HasCompanionPrefab(prefabName))
                    {
                        Debug.LogWarning($"[CompanionPatches] Companion prefab '{prefabName}' not found - trying default");
                        prefabName = "CompanionNpc";

                        if (!CompanionPrefabManager.HasCompanionPrefab(prefabName))
                        {
                            Debug.LogError($"[CompanionPatches] Default companion prefab not available - cannot restore {companionData.CompanionName}");
                            failedCount++;
                            continue;
                        }
                    }

                    // CRITICAL: One more check right before spawning ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â a stale ZDO may have
                    // loaded between the dedup sweep and now. Destroy it to prevent duplication.
                    var stale = FindExistingCompanion(companionData.CompanionId);
                    if (stale != null)
                    {
                        Debug.Log($"[CompanionPatches] Destroying stale companion {companionData.CompanionId} before fresh spawn");
                        CompanionNetworkHelper.Destroy(stale.gameObject);
                    }

                    // Spawn companion near player
                    Vector3 spawnPos = GetSafeSpawnPosition(player, effectiveOwnerPos);
                    var companionGO = CompanionPrefabManager.SpawnCompanion(prefabName, spawnPos, Quaternion.identity);

                    if (companionGO != null)
                    {
                        // Get the CompanionController component from the spawned GameObject
                        var controller = companionGO.GetComponent<CompanionController>();
                        if (controller != null)
                        {
                            // Restore companion state
                            controller.companionId = companionData.CompanionId;
                            controller.companionName = companionData.CompanionName;
                            controller.displayNameOverride = companionData.DisplayNameOverride;
                            controller.isTamed = true;
                            controller.ownerPlayerId = playerId;

                            // CRITICAL: Set vanilla Character.IsTamed() too. CompanionController.UpdateCompanionBehavior
                            // polls _character.IsTamed() every frame and overwrites isTamed back to whatever vanilla
                            // says ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â without this call, our isTamed=true above flips back to false next tick and the
                            // companion shows the wild "give X coins to recruit" prompt despite following.
                            var respChar = companionGO.GetComponent<Character>();
                            if (respChar != null) respChar.SetTamed(true);
                            var respNview = companionGO.GetComponent<ZNetView>();
                            if (respNview != null && respNview.IsValid())
                            {
                                var respZdo = respNview.GetZDO();
                                if (respZdo != null) respZdo.Set(ZDOVars.s_tamed, true);
                            }

                            // Restore follow state - this also sets _wasFollowing
                            // Use REGISTRY (authoritative when client-side, vault
                            // flag when server-side player==null). Same null-Player
                            // handling as the preserveIds branch: write
                            // companion_wasfollowing to the ZDO directly so the
                            // client-side controller reconciles when it loads.
                            if (registryFollowing)
                            {
                                if (player != null)
                                {
                                    controller.CommandFollow(player);
                                }
                                else
                                {
                                    if (respNview != null && respNview.IsValid())
                                    {
                                        var rzdo = respNview.GetZDO();
                                        if (rzdo != null) rzdo.Set("companion_wasfollowing", true);
                                    }
                                }
                            }
                            else
                            {
                                // Companion was in stay mode - restore home position
                                if (companionData.HasHomePosition)
                                {
                                    var idleBehavior = controller.GetComponent<CompanionIdleBehavior>();
                                    if (idleBehavior != null)
                                    {
                                        var homePos = new Vector3(companionData.HomePositionX, companionData.HomePositionY, companionData.HomePositionZ);
                                        idleBehavior.SetHomePosition(homePos);
                                        Debug.Log($"[CompanionPatches] Restored home position for spawned companion {companionData.CompanionName} at {homePos}");
                                    }
                                }
                            }

                            // CRITICAL: Restore progression, stats, skills, and kill tracker from vault
                            // This is what was missing before - companions lost their levels/stats on logout/login
                            RestoreProgressionAndStatsFromVault(controller, companionData);

                            // CRITICAL: Clear respawn state now that companion is restored
                            ClearRespawnStateInVault(playerId, companionData.CompanionId);

                            controller.SaveToZDO();
                            restoredCount++;

                            Debug.Log($"[CompanionPatches] Restored companion {companionData.CompanionName} (ID: {companionData.CompanionId}) at {spawnPos}, following={companionData.IsFollowing}");
                        }
                        else
                        {
                            Debug.LogWarning($"[CompanionPatches] Spawned companion has no CompanionController component");
                            // Same reason as the stay-mode branch above ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â go through
                            // ZNetScene so the ZDO is unregistered cleanly.
                            CompanionNetworkHelper.Destroy(companionGO);
                            failedCount++;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[CompanionPatches] Failed to spawn companion {companionData.CompanionName}");
                        failedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionPatches] Failed to restore companion: {ex.Message}");
                    failedCount++;
                }
            }

            Debug.Log($"[CompanionPatches] Companion restoration complete: {restoredCount} restored, {skippedCount} skipped, {failedCount} failed");

            // MessageHud is client-side UI; only run when we have a Player.
            // On dedicated server (player == null) the user sees their own
            // client-side notifications driven by the companion-restore
            // / vault sync flow; the server doesn't need to (and can't)
            // surface a popup.
            if (player != null && restoredCount > 0)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
         $"Restored {restoredCount} companion(s)");
            }
        }

        /// <summary>
        /// Gets a safe spawn position near the given anchor position.
        /// On the listen-host / single-player path the caller passes a
        /// <see cref="Player"/> object and we use its transform for both
        /// position AND forward direction (so the spawn ring is oriented
        /// in front of the player). On the dedicated-server path we have
        /// only a position (peer's character ZDO position), no forward ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â
        /// we synthesize a forward of <c>Vector3.forward</c>. The ring
        /// search itself is direction-agnostic so this just affects the
        /// "preferred" first-attempt direction.
        /// </summary>
        private static Vector3 GetSafeSpawnPosition(Player player, Vector3 anchorPos)
        {
            Vector3 basePos = anchorPos;
            Vector3 forward = player != null ? player.transform.forward : Vector3.forward;

            // Try to find a valid ground position
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f;
                Vector3 direction = Quaternion.Euler(0, angle, 0) * forward;
                Vector3 testPos = basePos + direction * 3f;

                // Check for ground
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

            // Fallback to simple offset
            return basePos + forward * 3f + Vector3.up * 0.5f;
        }

        private static CompanionController FindExistingCompanion(string companionId)
        {
            if (string.IsNullOrEmpty(companionId)) return null;

            foreach (var controller in UnityEngine.Object.FindObjectsByType<CompanionController>(UnityEngine.FindObjectsSortMode.None))
            {
                if (controller != null && controller.companionId == companionId)
                    return controller;
            }
            return null;
        }

        // Companion prefabs we care about for the ZDO scan. Must match every
        // prefab name a CompanionController-bearing instance can be cloned
        // from. Update this list when a new variant is added.
        private static readonly string[] _companionPrefabNamesForScan =
        {
            "CompanionNpc",
            "CompanionNpc_Wild",
            "BaseNpc",
        };

        /// <summary>
        /// True if a companion with this id exists anywhere in the world,
        /// loaded OR not. Live CompanionController lookup first (cheap,
        /// authoritative when zones are loaded), then a
        /// <see cref="ZDOMan.GetAllZDOsWithPrefabIterative"/> scan over the
        /// known companion prefab names checking each ZDO's "companion_id"
        /// string field. Without the ZDO scan, restoring a player whose
        /// companion is in an unloaded zone re-spawns a duplicate that
        /// resolves into two CompanionControllers with the same id once
        /// the original zone reloads.
        /// </summary>
        private static bool CompanionExistsAnywhere(string companionId)
        {
            if (string.IsNullOrEmpty(companionId)) return false;
            if (FindExistingCompanion(companionId) != null) return true;
            if (ZDOMan.instance == null) return false;

            var temp = new List<ZDO>();
            foreach (var prefabName in _companionPrefabNamesForScan)
            {
                temp.Clear();
                int idx = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, temp, ref idx)) { }
                for (int i = 0; i < temp.Count; i++)
                {
                    var zdo = temp[i];
                    if (zdo == null || !zdo.IsValid()) continue;
                    if (zdo.GetString("companion_id") == companionId) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Scans ZDOMan for every companion ZDO that shares a
        /// <c>companion_id</c> with another and destroys the extras. Cleans
        /// up duplicates left over from earlier sessions where the
        /// stay-mode restore re-spawned a companion whose home zone was
        /// unloaded ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â the original ZDO came back online when the zone
        /// activated, leaving two ZDOs for the same id. Runs once per
        /// player connect from <see cref="CompanionRestoreService"/>; only
        /// the host actually does the destroy (each ZDO is destroyed by
        /// its current owner via <c>SetOwner</c> + <c>DestroyZDO</c>).
        /// </summary>
        internal static void DedupeCompanionZdosInWorld()
        {
            if (ZDOMan.instance == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            // companion_id -> [zdo, zdo, ...]
            var byId = new Dictionary<string, List<ZDO>>();
            var temp = new List<ZDO>();
            foreach (var prefabName in _companionPrefabNamesForScan)
            {
                temp.Clear();
                int idx = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, temp, ref idx)) { }
                for (int i = 0; i < temp.Count; i++)
                {
                    var zdo = temp[i];
                    if (zdo == null || !zdo.IsValid()) continue;
                    string cid = zdo.GetString("companion_id");
                    if (string.IsNullOrEmpty(cid)) continue;
                    if (!byId.TryGetValue(cid, out var bucket))
                        byId[cid] = bucket = new List<ZDO>();
                    bucket.Add(zdo);
                }
            }

            int totalDestroyed = 0;
            foreach (var kv in byId)
            {
                var dups = kv.Value;
                if (dups.Count < 2) continue;

                // Keep the one whose live CompanionController exists (most
                // likely the active instance the player has been using). If
                // none is loaded, keep the first by ZDO id.
                ZDO keeper = null;
                var live = FindExistingCompanion(kv.Key);
                if (live != null)
                {
                    var liveNview = live.GetComponent<ZNetView>();
                    if (liveNview != null && liveNview.IsValid())
                        keeper = liveNview.GetZDO();
                }
                if (keeper == null) keeper = dups[0];

                for (int i = 0; i < dups.Count; i++)
                {
                    var zdo = dups[i];
                    if (zdo == keeper) continue;
                    try
                    {
                        // Become owner so DestroyZDO has authority.
                        zdo.SetOwner(ZDOMan.GetSessionID());
                        ZDOMan.instance.DestroyZDO(zdo);
                        totalDestroyed++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CompanionPatches] Failed to destroy duplicate ZDO for companion_id={kv.Key}: {ex.Message}");
                    }
                }
            }

            if (totalDestroyed > 0)
                Debug.Log($"[CompanionPatches] Dedupe sweep destroyed {totalDestroyed} duplicate companion ZDO(s) across {byId.Count} companion id(s).");
        }

        /// <summary>
        /// Finds and destroys ALL duplicate companions with the given ID, keeping at most one.
        /// Returns the one survivor (if any).
        /// </summary>
        private static CompanionController DestroyDuplicateCompanions(string companionId)
        {
            if (string.IsNullOrEmpty(companionId)) return null;

            CompanionController keeper = null;
            int destroyed = 0;

            foreach (var controller in UnityEngine.Object.FindObjectsByType<CompanionController>(UnityEngine.FindObjectsSortMode.None))
            {
                if (controller == null || controller.companionId != companionId) continue;

                if (keeper == null)
                {
                    keeper = controller;
                }
                else
                {
                    // Duplicate ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â destroy it
                    Debug.LogWarning($"[CompanionPatches] Destroying duplicate companion {companionId} (name: {controller.companionName})");
                    CompanionNetworkHelper.Destroy(controller.gameObject);
                    destroyed++;
                }
            }

            if (destroyed > 0)
                Debug.Log($"[CompanionPatches] Cleaned up {destroyed} duplicate(s) of companion {companionId}");

            return keeper;
        }

        /// <summary>
        /// Scans ALL CompanionNpc ZDOs in the world and destroys any whose companion_id
        /// matches one of the IDs we're about to restore from vault. This catches ghost
        /// ZDOs that survived a failed logout destroy (network torn down before ZDO
        /// destruction propagated). Unlike FindObjectsOfType, this works even if the
        /// ZDO's zone hasn't loaded or CompanionController.Start/LoadFromZDO hasn't run.
        /// </summary>
        private static void DestroyStaleCompanionZDOs(long playerId, HashSet<string> vaultCompanionIds)
        {
            if (ZDOMan.instance == null || vaultCompanionIds == null || vaultCompanionIds.Count == 0)
                return;

            int destroyed = 0;
            var zdoList = new List<ZDO>();
            int index = 0;

            // Iterate all CompanionNpc ZDOs in the world
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative("CompanionNpc", zdoList, ref index))
            {
                // yield-friendly but we're not in a coroutine, just let it run
            }

            foreach (var zdo in zdoList)
            {
                if (zdo == null || !zdo.IsValid()) continue;

                long owner = zdo.GetLong("companion_owner", 0);
                if (owner != playerId) continue;

                string id = zdo.GetString("companion_id", "");
                if (string.IsNullOrEmpty(id)) continue;

                // Skip stationed NPCs
                bool stationed = zdo.GetBool("npc_stationed", false);
                if (stationed) continue;

                if (vaultCompanionIds.Contains(id))
                {
                    // This ZDO matches a companion we're about to restore from vault.
                    // Destroy the stale ZDO so we don't get a duplicate.
                    Debug.Log($"[CompanionPatches] Destroying stale ZDO for companion {id} (ZDOID: {zdo.m_uid})");

                    // Also destroy the Unity object if it's instantiated
                    var existingView = ZNetScene.instance?.FindInstance(zdo);
                    if (existingView != null)
                    {
                        CompanionNetworkHelper.Destroy(existingView.gameObject);
                    }
                    else
                    {
                        // No Unity object yet ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â destroy the ZDO directly
                        ZDOMan.instance.DestroyZDO(zdo);
                    }
                    destroyed++;
                }
            }

            if (destroyed > 0)
                Debug.Log($"[CompanionPatches] ZDO sweep destroyed {destroyed} stale companion ZDO(s) for player {playerId}");
        }

        /// <summary>
        /// Clears the respawn state for a companion in the vault after successful restoration.
        /// </summary>
        private static void ClearRespawnStateInVault(long playerId, string companionId)
        {
            try
            {
                if (!FiresCore.Bridge.CompanionVaultBridge.IsVaultAvailable()) return;

                var companions = FiresCore.Bridge.CompanionVaultBridge.GetCompanionsFor(playerId);
                var companionData = companions?.Find(c => c.CompanionId == companionId);

                if (companionData != null && companionData.IsPendingRespawn)
                {
                    companionData.IsPendingRespawn = false;
                    companionData.RespawnTimeRemaining = 0;
                    companionData.DeathTimestamp = 0;

                    FiresCore.Bridge.CompanionVaultBridge.Save(playerId, companionData);
                    Debug.Log($"[CompanionPatches] Cleared respawn state for {companionData.CompanionName}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] Failed to clear respawn state: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores progression, stats, skills, and kill tracker data from vault.
        /// CRITICAL: Without this, companions lose all progress (levels, attributes, kills) on respawn!
        /// </summary>
        private static void RestoreProgressionAndStatsFromVault(CompanionController controller, CompanionSaveData savedData)
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
                        Debug.Log($"[CompanionPatches] Restored skills from vault for {savedData.CompanionName}");
                    }
                }

                // Restore progression (level, XP, attributes)
                if (!string.IsNullOrEmpty(savedData.ProgressionData))
                {
                    var progression = controller.GetProgression();
                    if (progression != null)
                    {
                        progression.RestoreProgressionFromVault(savedData.ProgressionData);
                        Debug.Log($"[CompanionPatches] Restored progression from vault for {savedData.CompanionName}: {savedData.ProgressionData}");
                    }
                }

                // Restore stats (death count, etc.)
                if (!string.IsNullOrEmpty(savedData.StatsData))
                {
                    var stats = controller.GetStats();
                    if (stats != null)
                    {
                        stats.RestoreStatsFromVault(savedData.StatsData);
                        Debug.Log($"[CompanionPatches] Restored stats from vault for {savedData.CompanionName}");
                    }
                }

                // Restore kill tracker
                if (!string.IsNullOrEmpty(savedData.KillsData))
                {
                    var killTracker = controller.GetKillTracker();
                    if (killTracker != null)
                    {
                        killTracker.RestoreKillsFromVault(savedData.KillsData);
                        Debug.Log($"[CompanionPatches] Restored kill tracker from vault for {savedData.CompanionName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] Failed to restore progression/stats from vault: {ex.Message}");
            }
        }

        private static CompanionSaveData ParseCompanionData(object obj)
        {
            if (obj == null) return null;
            if (obj is CompanionSaveData csd) return csd;

            try
            {
                return JsonConvert.DeserializeObject<CompanionSaveData>(JsonConvert.SerializeObject(obj));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Clear restored players tracking when disconnecting AND destroy non-stationed companions.
        /// 
        /// CRITICAL MULTIPLAYER FIX:
        /// Destroy owned companions on logout so vault-based restore is authoritative on login.
        /// Uses Prefix (not Postfix) so ZNetScene and ZDOMan are still alive for proper
        /// network-aware destruction. A Postfix runs after Game.Logout tears down the
        /// network layer, causing ZDO destruction to silently fail and leaving ghost ZDOs
        /// on the server that duplicate on the next login.
        /// </summary>
        [HarmonyPatch(typeof(Game), "Logout")]
        [HarmonyPrefix]
        public static void Game_Logout_Prefix()
        {
            long localPlayerId = 0;
            try
            {
                localPlayerId = Player.m_localPlayer?.GetPlayerID() ?? 0;
            }
            catch { }

            if (localPlayerId != 0)
            {
                DestroyPlayerCompanionsOnLogout(localPlayerId);
            }

            CompanionRespawnManager.Instance?.OnPlayerLogout(localPlayerId);

            _restoredPlayers.Clear();
            CompanionRestoreService.ResetSessionTracking();

            // Reset NPC manager RPC state so RPCs re-register on next login
            FiresCore.Bridge.NpcHostBridge.ResetServerRpcs();

            Debug.Log("[CompanionPatches] Logout prefix: companions destroyed and tracking cleared");
        }

        /// <summary>
        /// Per-player logout cleanup for companions.
        ///
        /// PERSISTENT MODE (Companions.Persistence.PersistentCompanions = true, default):
        ///   1. Flush the player's authoritative state to vault as a backup mirror.
        ///   2. Refresh per-companion roster snapshots so in-session progression
        ///      (kills, levels, equipment) is captured before the GameObject
        ///      potentially unloads when its zone goes out of scope.
        ///   3. Release ZDO ownership (SetOwner(0L)). ZDOMan's natural
        ///      closest-peer rule promotes the server to owner because
        ///      TamedCompanionZoneLoader keeps companion zones loaded
        ///      server-side. Companions stay alive in the world like
        ///      vanilla tames; restoration on next login adopts existing
        ///      ZDOs instead of re-spawning from vault.
        ///
        /// LEGACY MODE (PersistentCompanions = false):
        ///   1. Same vault flush + roster refresh.
        ///   2. Then destroy the ZDO + Unity object. Restore on next login
        ///      re-spawns fresh from vault. Smaller server footprint, but
        ///      companions don't behave as living entities while their owner
        ///      is offline.
        ///
        /// Stationed NPCs are exempted from BOTH paths.
        /// </summary>
        private static void DestroyPlayerCompanionsOnLogout(long playerId)
        {
            if (playerId == 0) return;

            bool persistent = FiresCore.Bridge.NpcConfigBridge.GetBool("PersistentCompanions", true);

            try
            {
                var allCompanions = UnityEngine.Object.FindObjectsByType<CompanionController>(UnityEngine.FindObjectsSortMode.None);
                int destroyedCount = 0;
                int releasedCount = 0;
                int skippedStationedCount = 0;

                // Phase 6 (save refactor): the legacy "world has 0 / vault
                // has N ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ skip" safety guard is gone. The roster on the
                // player's m_customData is now authoritative for owned
                // companions, so a session with 0 live companions simply
                // doesn't refresh any roster entries (RefreshLiveSnapshot
                // is per-companion). The vault, being a debug mirror only,
                // is rebuilt by the periodic FlushDebugMirror and by the
                // explicit FlushPlayerToVault call below; the destroy
                // loop cannot clobber authoritative state.

                // Phase 6: flush this player's authoritative state to the
                // vault ONCE up front. The periodic flush would catch up
                // within DIRTY_FLUSH_INTERVAL seconds, but logout means we
                // won't tick again for this player \u2014 do it now so the
                // on-disk debug mirror is current.
                try
                {
                    var localPlayer = Player.m_localPlayer;
                    if (localPlayer != null && localPlayer.GetPlayerID() == playerId)
                        CompanionVault.FlushPlayerToVault(localPlayer);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionPatches] Pre-logout vault flush failed: {ex.Message}");
                }

                foreach (var companion in allCompanions)
                {
                    if (companion == null) continue;
                    if (companion.ownerPlayerId != playerId) continue;

                    // Skip static placed NPCs and stationed companions in
                    // both modes ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â they're permanent world fixtures.
                    if (companion.isStaticPlacement)
                    {
                        skippedStationedCount++;
                        continue;
                    }
                    var npcModule = companion.GetComponent<CompanionNpcModule>();
                    if (npcModule != null && npcModule.IsStationedAsNpc)
                    {
                        skippedStationedCount++;
                        Debug.Log($"[CompanionPatches] Skipping stationed NPC {companion.companionName} on logout (persists for all players)");
                        continue;
                    }

                    // ROSTER MIRROR: refresh the per-player roster snapshot
                    // so any in-session progression (kills, skill ups,
                    // equipment swaps) is captured. Preserves existing
                    // FollowState and IsPendingRespawn (a logout doesn't
                    // change either). Required in both modes.
                    try
                    {
                        var localPlayer = Player.m_localPlayer;
                        if (localPlayer != null)
                            CompanionRosterWriter.RefreshLiveSnapshot(localPlayer, companion);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CompanionPatches] Roster refresh on logout failed (non-fatal): {ex.Message}");
                    }

                    // CORE DORMANT STORE (replaces the former KennelLifecycle logout prefix): capture
                    // FOLLOWING companions to the dormant store before release/destroy. In persistent
                    // mode the live ZDO also survives and the login adopt-pass wins (then clears this
                    // entry); in legacy mode this is the sole restore source. Null-safe, harmless either way.
                    if (companion.ShouldBeFollowing)
                        companion.StoreDormant(playerId, FiresCore.Bridge.DormancyKind.LoggedOutFollower, 0L);

                    var nview = companion.GetComponent<ZNetView>();

                    if (persistent)
                    {
                        // PERSISTENT MODE: release ZDO ownership and let the
                        // server claim it via ZDOMan's closest-peer rule
                        // (TamedCompanionZoneLoader keeps the zone loaded
                        // server-side so the server qualifies). Do NOT
                        // destroy the ZDO or GameObject. ZNetScene may unload
                        // the GameObject if no peer keeps the zone loaded;
                        // the ZDO survives in ZDOMan either way and re-
                        // materialises when somebody comes near.
                        if (nview != null && nview.IsValid())
                        {
                            var zdo = nview.GetZDO();
                            if (zdo != null && nview.IsOwner())
                            {
                                zdo.SetOwner(0L);
                                releasedCount++;
                            }
                        }
                    }
                    else
                    {
                        // LEGACY MODE: belt-and-suspenders destruction so
                        // the server drops the ZDO even if ZNetScene.Destroy
                        // doesn't propagate in time. Vault has the snapshot
                        // for next-login re-spawn.
                        if (nview != null && nview.IsValid())
                        {
                            var zdo = nview.GetZDO();
                            if (zdo != null)
                            {
                                if (!nview.IsOwner())
                                    nview.ClaimOwnership();
                                if (ZDOMan.instance != null)
                                    ZDOMan.instance.DestroyZDO(zdo);
                            }
                        }

                        CompanionNetworkHelper.Destroy(companion.gameObject);
                        destroyedCount++;
                        Debug.Log($"[CompanionPatches] Destroyed companion {companion.companionName} on logout (legacy mode)");
                    }
                }

                if (persistent)
                {
                    if (releasedCount > 0 || skippedStationedCount > 0)
                        Debug.Log($"[CompanionPatches] Logout (persistent mode): released ownership of {releasedCount} companion(s), {skippedStationedCount} stationed NPC(s) untouched");
                }
                else
                {
                    if (destroyedCount > 0 || skippedStationedCount > 0)
                        Debug.Log($"[CompanionPatches] Logout (legacy mode): destroyed {destroyedCount} companion(s), {skippedStationedCount} stationed NPC(s) preserved");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] Error during logout companion cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// Client-only patches that reference FejdStartup (unavailable on dedicated servers).
        /// Separated into a nested class so the server skip-list can exclude these without
        /// losing server-critical patches in the parent class.
        /// </summary>
        [HarmonyPatch]
        public static class CompanionClientPatches_FejdStartup
        {
            /// <summary>
            /// Also clear on returning to start menu
            /// </summary>
            [HarmonyPatch(typeof(FejdStartup), "Awake")]
            [HarmonyPostfix]
            public static void FejdStartup_Awake_Postfix()
            {
                _restoredPlayers.Clear();
                CompanionRestoreService.ResetSessionTracking();
                Debug.Log("[CompanionPatches] Cleared companion restoration tracking on main menu");
            }
        }

        /// <summary>
        /// Public method to manually trigger companion restoration (for debug/testing).
        /// Delegates to <see cref="CompanionRestoreService.ForceRestoreForLocalPlayer"/>;
        /// the actual restoration is server-only as of the refactor.
        /// </summary>
        public static void ForceRestoreCompanions()
        {
            CompanionRestoreService.ForceRestoreForLocalPlayer();
        }

        #region Interaction Patches

        /// <summary>
        /// Patch Tameable.GetHoverText to show our custom hover text for companions
        /// </summary>
        [HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverText))]
        [HarmonyPostfix]
        public static void Tameable_GetHoverText_Postfix(Tameable __instance, ref string __result)
        {
            try
            {
                var companion = __instance.GetComponent<CompanionController>();
                if (companion == null) return;

                // Static placed NPCs use NpcController for hover text, not the companion stack
                if (companion.isStaticPlacement)
                {
                    var hover = FiresCore.Bridge.NpcInteractionBridge.GetHoverText(__instance.gameObject);
                    if (hover != null)
                    {
                        __result = hover;
                        return;
                    }
                }

                // Check if stationed as NPC - let CompanionNpcModule handle hover text
                var npcModule = __instance.GetComponent<CompanionNpcModule>();
                if (npcModule != null && npcModule.IsStationedAsNpc)
                {
                    __result = npcModule.GetHoverText();
                    return;
                }

                // Build custom hover text for our companions
                __result = BuildCompanionHoverText(companion, __instance);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] Tameable_GetHoverText error: {ex.Message}");
            }
        }

        /// <summary>
        /// Patch Tameable.Interact to handle our custom companion interactions
        /// </summary>
        [HarmonyPatch(typeof(Tameable), nameof(Tameable.Interact))]
        [HarmonyPrefix]
        public static bool Tameable_Interact_Prefix(Tameable __instance, Humanoid user, bool hold, bool alt, ref bool __result)
        {
            try
            {
                var companion = __instance.GetComponent<CompanionController>();
                if (companion == null) return true; // Not our companion, let normal handling proceed

                if (hold)
                {
                    __result = false;
                    return false;
                }

                var player = user as Player;
                if (player == null)
                {
                    __result = false;
                    return false;
                }

                // Static placed NPCs use NpcController for interaction, not the companion stack
                if (companion.isStaticPlacement)
                {
                    var handled = FiresCore.Bridge.NpcInteractionBridge.InteractStatic(__instance.gameObject, user, hold, alt);
                    if (handled.HasValue)
                    {
                        __result = handled.Value;
                        return false;
                    }
                }

                // Check if stationed as NPC - let CompanionNpcModule handle interaction
                var npcModule = __instance.GetComponent<CompanionNpcModule>();
                if (npcModule != null && npcModule.IsStationedAsNpc)
                {
                    __result = npcModule.Interact(user, hold, alt);
                    return false; // Don't run original
                }

                bool shiftHeld = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);

                Debug.Log($"[CompanionPatches] Interact with {companion.GetDisplayName()}, tamed={companion.isTamed}, shift={shiftHeld}");

                // Not tamed - try auto-tame from full inventory (including VAInventory's coin purse)
                if (!companion.isTamed)
                {
                    // Admin shift+E to instant tame
                    if (shiftHeld && companion.IsPlayerAdmin(player))
                    {
                        companion.AdminTame(player);
                        __result = true;
                        return false;
                    }

                    // E-interact: walk the player's whole inventory looking for
                    // tamingItemPrefab (Coins by default). Works whether coins
                    // live in the regular grid, equipment slots, or the VAInventory
                    // coin purse ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â the helper inspects every stack on the player.
                    // The helper itself shows a "Need X (have Y)" message on
                    // shortfall, so we don't duplicate the prompt here.
                    companion.TryAutoTameWithInventoryItems(player);
                    __result = true;
                    return false;
                }

                // Tamed - handle commands
                bool isOwner = companion.IsOwner(player);
                bool isAdmin = companion.IsPlayerAdmin(player);

                if (!isOwner && !isAdmin)
                {
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                        $"{companion.GetDisplayName()} belongs to someone else.");
                    __result = true;
                    return false;
                }

                // CRITICAL: Stop companion movement when interacting
                StopCompanionForInteraction(companion, player);

                // Shift+E = toggle follow/stay
                if (shiftHeld)
                {
                    companion.ToggleFollowMode(player);
                    __result = true;
                    return false;
                }

                // Normal E = open inventory
                companion.OpenInventory(player);
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CompanionPatches] Tameable_Interact error: {ex}");
                return true; // Let normal handling proceed on error
            }
        }

        /// <summary>
        /// Patch Tameable.UseItem to handle taming with items
        /// </summary>
        [HarmonyPatch(typeof(Tameable), nameof(Tameable.UseItem))]
        [HarmonyPrefix]
        public static bool Tameable_UseItem_Prefix(Tameable __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            try
            {
                var companion = __instance.GetComponent<CompanionController>();
                if (companion == null) return true; // Not our companion

                // Static placed NPCs don't accept items (no taming)
                if (companion.isStaticPlacement)
                {
                    __result = false;
                    return false;
                }

                var player = user as Player;
                if (player == null)
                {
                    __result = false;
                    return false;
                }

                // Let our companion handle the item use
                __result = companion.OnUseItem(player, item);
                return false; // Don't run original
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CompanionPatches] Tameable_UseItem error: {ex}");
                return true;
            }
        }

        #endregion

        #region Damage/Death Patches

        /// <summary>
        /// Track companion tombstones by ZDOID to prevent corpse run.
        /// </summary>
        private static HashSet<ZDOID> _companionTombstones = new HashSet<ZDOID>();

        /// <summary>
        /// Registers a tombstone as belonging to a companion (not a player death).
        /// </summary>
        public static void RegisterCompanionTombstone(ZDOID zdoid)
        {
            if (zdoid != ZDOID.None)
            {
                _companionTombstones.Add(zdoid);
            }
        }

        /// <summary>
        /// Checks if a tombstone is from a companion death.
        /// </summary>
        public static bool IsCompanionTombstone(TombStone tombstone)
        {
            if (tombstone == null) return false;

            var nview = tombstone.GetComponent<ZNetView>();
            if (nview == null) return false;

            var zdo = nview.GetZDO();
            if (zdo == null) return false;

            // Check the ZDO flag we set when creating companion tombstones
            return zdo.GetBool("companion_tombstone", false);
        }

        /// <summary>
        /// Patch Player.OnTombstoneInteract to prevent corpse run from companion tombstones.
        /// When a player interacts with their own Player_tombstone, the game applies corpse run.
        /// We need to skip this for companion tombstones.
        /// </summary>
        [HarmonyPatch(typeof(TombStone), nameof(TombStone.Interact))]
        [HarmonyPrefix]
        public static void TombStone_Interact_Prefix(TombStone __instance, out bool __state)
        {
            // Track if this is a companion tombstone so postfix can handle it
            __state = IsCompanionTombstone(__instance);

            if (__state)
            {
                Debug.Log("[CompanionPatches] Interacting with companion tombstone - will prevent corpse run");
            }
        }

        [HarmonyPatch(typeof(TombStone), nameof(TombStone.Interact))]
        [HarmonyPostfix]
        public static void TombStone_Interact_Postfix(TombStone __instance, Humanoid character, bool __state)
        {
            // If this was a companion tombstone, remove corpse run effect if it was applied
            if (__state && character is Player player)
            {
                // Check if corpse run was just applied and remove it
                var seman = player.GetSEMan();
                if (seman != null)
                {
                    // Look for corpse run status effect - try both the hash and direct lookup
                    int corpseRunHash = "CorpseRun".GetStableHashCode();
                    if (seman.HaveStatusEffect(corpseRunHash))
                    {
                        seman.RemoveStatusEffect(corpseRunHash, true); // quiet = true to avoid message
                        Debug.Log("[CompanionPatches] Removed corpse run effect from companion tombstone interaction");
                    }
                }
            }
        }

        /// <summary>
        /// Patch TombStone.GiveBoost to completely prevent corpse run for companion tombstones.
        /// This is more reliable than removing it after the fact.
        /// </summary>
        [HarmonyPatch(typeof(TombStone), "GiveBoost")]
        [HarmonyPrefix]
        public static bool TombStone_GiveBoost_Prefix(TombStone __instance)
        {
            // If this is a companion tombstone, skip the corpse run effect entirely
            if (IsCompanionTombstone(__instance))
            {
                Debug.Log("[CompanionPatches] Blocking corpse run boost from companion tombstone");
                return false; // Skip the original method - no corpse run
            }

            return true; // Normal player tombstone - allow corpse run
        }

        /// <summary>
        /// Patch Tameable.TamingUpdate to skip the MonsterAI check for companions with CompanionAI.
        /// This prevents the "is tamed but missing tameable or monster AI script!" warning.
        /// </summary>
        [HarmonyPatch(typeof(Tameable), "TamingUpdate")]
        [HarmonyPrefix]
        public static bool Tameable_TamingUpdate_Prefix(Tameable __instance)
        {
            try
            {
                // Check if this is one of our companions with CompanionAI
                var companion = __instance.GetComponent<CompanionController>();
                var companionAI = __instance.GetComponent<CompanionAI>();

                if (companion != null && companionAI != null)
                {
                    // Skip the original TamingUpdate which expects MonsterAI
                    // Our CompanionAI handles all the taming behavior directly
                    return false;
                }

                return true; // Not our companion, run original
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Patch Tameable.GetStatusString to handle companions with CompanionAI.
        /// </summary>
        [HarmonyPatch(typeof(Tameable), "GetStatusString")]
        [HarmonyPrefix]
        public static bool Tameable_GetStatusString_Prefix(Tameable __instance, ref string __result)
        {
            try
            {
                var companion = __instance.GetComponent<CompanionController>();
                if (companion != null && companion.isTamed)
                {
                    // Return a simple status for tamed companions
                    __result = "";
                    return false;
                }
                return true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Patch Character.RaiseSkill to short-circuit it for our companions.
        ///
        /// Vanilla RaiseSkill expects every tamed Character to have BOTH a
        /// <see cref="Tameable"/> AND a <see cref="MonsterAI"/> component.  Our
        /// companions use <see cref="CompanionAI"/> instead and have neither, so
        /// every time a companion landed a skill-granting hit Valheim spammed:
        ///
        ///   "{name} is tamed but missing tameable or monster AI script!"
        ///
        /// into the log (multiple times per second during combat).  We don't use
        /// vanilla skill leveling on companions ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â they have their own
        /// CompanionSkills system that's driven by CompanionController hooks ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â
        /// so we can safely skip the entire vanilla method for any Character
        /// that is one of our companions.  This both kills the warning spam
        /// and saves the per-call overhead of the now-noop method.
        /// </summary>
        [HarmonyPatch(typeof(Character), nameof(Character.RaiseSkill))]
        [HarmonyPrefix]
        public static bool Character_RaiseSkill_Prefix(Character __instance)
        {
            if (__instance != null && __instance.GetComponent<CompanionController>() != null)
            {
                // Companion ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ skip vanilla.  CompanionSkills handles XP separately.
                return false;
            }
            return true;
        }

        /// <summary>
        /// Patch Character.RPC_Damage to allow non-owners to damage companions
        /// and track direct attacks for stationed NPCs.
        /// Stationed NPCs react to owner hits with a stumble and single retaliation.
        /// </summary>
        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        [HarmonyPrefix]
        public static bool Character_RPC_Damage_Prefix(Character __instance, long sender, HitData hit)
        {
            try
            {
                var companion = __instance.GetComponent<CompanionController>();
                if (companion == null) return true; // Not our companion

                // FIRE IMMUNITY: Companions that SPAWNED in Ashlands are permanently immune to fire damage
                // This is a benefit of recruiting Ashlands companions - they retain fire immunity after taming
                // Check spawn biome from ZDO, not current location
                if (hit != null && hit.m_damage.m_fire > 0)
                {
                    var nview = __instance.GetComponent<ZNetView>();
                    if (nview != null && CompanionRandomLoadout.HasFireImmunity(nview))
                    {
                        // Nullify fire damage for Ashlands-spawned companions
                        hit.m_damage.m_fire = 0f;

                        // If there's no other damage, skip this hit entirely
                        if (hit.GetTotalDamage() <= 0.1f)
                        {
                            return false; // Block zero-damage hit
                        }
                    }
                }

                // IMPORTANT: Notify NpcModule that this NPC was directly attacked
                // This allows stationed NPCs to enter alert/combat state
                var npcModule = __instance.GetComponent<CompanionNpcModule>();
                if (npcModule != null && npcModule.IsStationedAsNpc)
                {
                    npcModule.OnDirectlyAttacked();

                    // Check if hit was from owner - they should react but not take damage
                    if (hit.GetAttacker() is Player attackerPlayer)
                    {
                        if (companion.IsOwner(attackerPlayer))
                        {
                            // React to being hit by owner - stumble and retaliate once
                            ReactToOwnerHit(companion, __instance, attackerPlayer, hit);
                            return false; // Block damage but we handled the reaction
                        }
                    }
                }

                // Check if damage should be allowed
                if (!companion.ShouldAllowDamage(hit))
                {
                    Debug.Log($"[CompanionPatches] Blocked damage to {companion.GetDisplayName()} from owner");
                    return false; // Block damage
                }

                return true; // Allow normal damage processing
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] Character_RPC_Damage error: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Makes a stationed NPC react to being hit by their owner.
        /// They stumble back, play a reaction animation, and swing back once.
        /// </summary>
        private static void ReactToOwnerHit(CompanionController companion, Character character, Player attacker, HitData hit)
        {
            try
            {
                // Play stagger/hit reaction animation
                var zanim = character.GetComponent<ZSyncAnimation>();
                if (zanim != null)
                {
                    zanim.SetTrigger("stagger");
                }

                // Apply knockback force to simulate stumbling
                var rigidbody = character.GetComponent<Rigidbody>();
                if (rigidbody != null && !rigidbody.isKinematic)
                {
                    Vector3 knockbackDir = (character.transform.position - attacker.transform.position).normalized;
                    knockbackDir.y = 0.2f; // Slight upward component
                    rigidbody.AddForce(knockbackDir * 3f, ForceMode.Impulse);
                }

                // Face the attacker
                Vector3 toAttacker = (attacker.transform.position - character.transform.position);
                toAttacker.y = 0;
                if (toAttacker.sqrMagnitude > 0.01f)
                {
                    character.transform.rotation = Quaternion.LookRotation(toAttacker);
                }

                // Schedule a single retaliation attack after a short delay
                companion.StartCoroutine(DelayedRetaliation(companion, character, attacker));

                // Show a message
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                    $"{companion.GetDisplayName()} doesn't appreciate that!");

                Debug.Log($"[CompanionPatches] Stationed NPC {companion.GetDisplayName()} reacted to owner hit");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] ReactToOwnerHit error: {ex.Message}");
            }
        }

        /// <summary>
        /// Coroutine to delay retaliation attack so the stumble plays first.
        /// </summary>
        private static IEnumerator DelayedRetaliation(CompanionController companion, Character character, Player attacker)
        {
            // Wait for stumble animation
            yield return new WaitForSeconds(0.5f);

            // Check if still valid
            if (companion == null || character == null || attacker == null) yield break;
            if (character.IsDead()) yield break;

            // Check distance - only retaliate if close enough
            float dist = Vector3.Distance(character.transform.position, attacker.transform.position);
            if (dist > 4f) yield break;

            // Face the attacker again
            Vector3 toAttacker = (attacker.transform.position - character.transform.position);
            toAttacker.y = 0;
            if (toAttacker.sqrMagnitude > 0.01f)
            {
                character.transform.rotation = Quaternion.LookRotation(toAttacker);
            }

            // Trigger a single attack animation (but deal no damage to owner)
            var zanim = character.GetComponent<ZSyncAnimation>();
            if (zanim != null)
            {
                // Try common attack triggers
                zanim.SetTrigger("attack");
            }

            // Also try via Humanoid if available
            var humanoid = character as Humanoid;
            if (humanoid != null)
            {
                // Get the current weapon
                var weapon = humanoid.GetCurrentWeapon();
                if (weapon != null)
                {
                    // Start the attack animation
                    humanoid.StartAttack(null, false);
                }
                else
                {
                    // Unarmed - still try to attack
                    humanoid.StartAttack(null, false);
                }
            }

            Debug.Log($"[CompanionPatches] {companion.GetDisplayName()} retaliated against owner");
        }

        // Companions die for real: vanilla death → ragdoll + CompanionDeathHandler.OnCharacterDeath
        // (capture → respawn via the lossless CompanionVault.RestoreCompanion). The former
        // Character.SetHealth keep-alive prefix that pinned health to max and called OnDefeated() was
        // removed — it caused a multi-hit death loop and left the companion alive at max HP long enough
        // for a raw ZDO reload to bypass the restore (the "bare CompanionNpc" bug). Death now flows
        // solely through the _isDying-guarded OnCharacterDeath path.

        #endregion

        #region Input Blocking for Companion Commands

        /// <summary>
        /// Tracks if the companion command ping was just used this frame.
        /// Used to prevent player attacks (kick) when using Shift+MMB to ping.
        /// </summary>
        private static bool _pingUsedThisFrame = false;
        private static int _lastPingFrame = -1;

        /// <summary>
        /// Called by CompanionCommandSystem when a ping command is issued.
        /// Sets a flag to block attacks for this frame.
        /// </summary>
        public static void NotifyPingUsed()
        {
            _pingUsedThisFrame = true;
            _lastPingFrame = Time.frameCount;
        }

        /// <summary>
        /// Checks if a ping was used this frame or very recently.
        /// </summary>
        public static bool WasPingUsedRecently()
        {
            // Block for current frame and next frame to account for timing
            return Time.frameCount - _lastPingFrame <= 1;
        }

        /// <summary>
        /// Patch PlayerController.FixedUpdate to block kick/attack when using companion ping.
        /// The player's unarmed kick is triggered by MMB (attack3) which conflicts with our ping.
        /// </summary>
        [HarmonyPatch(typeof(Player), "UpdateDodge")]
        [HarmonyPrefix]
        public static void Player_UpdateDodge_Prefix()
        {
            // Reset the flag each frame in a consistent location
            if (Time.frameCount > _lastPingFrame + 2)
            {
                _pingUsedThisFrame = false;
            }
        }

        /// <summary>
        /// Patch Player.PlayerAttackInput to suppress the unarmed kick when the
        /// player is using Shift+MMB to ping a companion.
        ///
        /// IMPORTANT ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â only block when the player is UNARMED. With a weapon
        /// equipped, all vanilla input (LMB primary, RMB secondary, sprint
        /// modifier, etc.) must keep working ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â otherwise holding Shift to ping
        /// would also cancel weapon attacks. The unarmed kick is the only
        /// vanilla action mapped to MMB by default, so that's all we suppress.
        /// </summary>
        [HarmonyPatch(typeof(Player), "PlayerAttackInput")]
        [HarmonyPrefix]
        public static bool Player_PlayerAttackInput_Prefix(Player __instance, float dt)
        {
            if (__instance == null) return true;

            // If a weapon is equipped in either hand, never block ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â the player
            // is armed, so MMB has no vanilla unarmed-kick action to suppress.
            if (__instance.m_rightItem != null || __instance.m_leftItem != null)
            {
                return true;
            }

            // Unarmed path: block the kick when Shift+MMB is currently held
            // (companion ping in progress) or was just used in the last 1-2
            // frames (released-MMB triggers leaking through).
            bool shiftHeld = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            bool mmbHeld = UnityEngine.Input.GetMouseButton(2);

            if (shiftHeld && mmbHeld)
            {
                return false;
            }

            if (WasPingUsedRecently())
            {
                return false;
            }

            return true;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Stops companion movement and makes them face the player when interacted with.
        /// CRITICAL: This uses CompanionStateController to enter UIInteraction state,
        /// which then blocks ALL movement at the Harmony patch level.
        /// </summary>
        private static void StopCompanionForInteraction(CompanionController companion, Player player)
        {
            if (companion == null || player == null) return;

            try
            {
                // CRITICAL: Enter UIInteraction state in the state controller
                // This is the authoritative freeze - the Harmony patches will block all movement
                var stateController = companion.GetStateController();
                if (stateController != null)
                {
                    // Clear any stuck emote first ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â must happen before entering UIInteraction
                    // so the animation reset doesn't fight the new state.
                    stateController.ForceStopEmote();


                    // Enter UI interaction state - this will block ALL movement at the Character level
                    stateController.TryEnterState(CompanionStateController.CompanionState.UIInteraction, 999f, "PlayerInteraction");
                }

                // Zero rigidbody velocity - only if NOT kinematic (Unity 6 doesn't allow setting velocity on kinematic bodies)
                var rigidbody = companion.GetComponent<Rigidbody>();
                bool isKinematic = rigidbody != null && rigidbody.isKinematic;

                if (rigidbody != null && !isKinematic)
                {
                    rigidbody.linearVelocity = Vector3.zero;
                    rigidbody.angularVelocity = Vector3.zero;
                }

                // Cancel any active idle behaviors (this also enters UIInteraction state)
                var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
                if (idleBehavior != null)
                {
                    // This will cancel all idle activities and trigger freeze
                    idleBehavior.FreezeForInteraction();
                }

                // Force detach if attached to something (will be re-frozen by state controller)
                var interactionBehavior = companion.GetComponent<Interactions.CompanionInteractionBehavior>();
                interactionBehavior?.ForceDetach();

                // Clear any command destination
                var companionAI = companion.GetCompanionAI();
                companionAI?.ClearIdleDestination();
                companionAI?.ClearCommandDestination();

                // Lock movement via combat movement as well (belt and suspenders)
                var combatMovement = companion.GetComponent<CompanionCombatMovement>();
                if (combatMovement != null)
                {
                    combatMovement.LockMovement("PlayerInteraction", 999f);
                }

                // Face the player smoothly
                Vector3 toPlayer = (player.transform.position - companion.transform.position);
                toPlayer.y = 0;
                if (toPlayer.sqrMagnitude > 0.01f)
                {
                    companion.transform.rotation = Quaternion.LookRotation(toPlayer);
                }

                Debug.Log($"[CompanionPatches] Stopped {companion.companionName} for interaction via StateController");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] StopCompanionForInteraction error: {ex.Message}");
            }
        }

        /// <summary>
        /// Build custom hover text for companions - matching NpcController format exactly
        /// </summary>
        private static string BuildCompanionHoverText(CompanionController companion, Tameable tameable)
        {
            // Match the exact format used by NpcController which works correctly
            string name = companion.GetDisplayName();

            // Build using the same pattern as NpcController.GetHoverText()
            string text = $"<color=yellow><b>{name}</b></color>";

            // CRITICAL FIX: Use CompanionStats for health percentage, not Character.GetHealthPercentage()
            // Character.GetHealthPercentage() doesn't account for food bonuses and our custom health tracking
            var stats = companion.GetStats();
            if (stats != null)
            {
                float healthPercent = stats.HealthPercentage * 100f;
                string healthColor = healthPercent > 50 ? "#00FF00" : (healthPercent > 25 ? "#FFFF00" : "#FF0000");
                text += $" <color={healthColor}>({healthPercent:F0}%)</color>";
            }
            else
            {
                // Fallback to Character if no stats component
                var character = companion.GetCharacter();
                if (character != null)
                {
                    float healthPercent = character.GetHealthPercentage() * 100f;
                    string healthColor = healthPercent > 50 ? "#00FF00" : (healthPercent > 25 ? "#FFFF00" : "#FF0000");
                    text += $" <color={healthColor}>({healthPercent:F0}%)</color>";
                }
            }

            if (!companion.isTamed)
            {
                // Show whether the player can afford the recruitment cost
                // right now. We walk the entire inventory (so coin-purse
                // contents count) using the same matching rule as
                // CompanionController.TryAutoTameWithInventoryItems.
                var player = Player.m_localPlayer;
                int playerHas = 0;
                if (player != null)
                {
                    var inv = player.GetInventory();
                    if (inv != null)
                    {
                        foreach (var stack in inv.GetAllItems())
                        {
                            if (stack == null) continue;
                            string prefabName = stack.m_dropPrefab?.name;
                            if (string.IsNullOrEmpty(prefabName)) continue;
                            if (string.Equals(prefabName, companion.tamingItemPrefab,
                                              StringComparison.OrdinalIgnoreCase))
                                playerHas += stack.m_stack;
                        }
                    }
                }

                bool canAfford = playerHas >= companion.tamingItemAmount;
                if (canAfford)
                {
                    text += $"\n<color=#90EE90>Wild - Costs {companion.tamingItemAmount} {companion.tamingItemPrefab} (have {playerHas})</color>";
                    text += "\n[<color=yellow><b>$KEY_Use</b></color>] Recruit";
                }
                else
                {
                    text += $"\n<color=#FFA500>Wild - Need {companion.tamingItemAmount} {companion.tamingItemPrefab} (have {playerHas})</color>";
                    text += "\n[<color=yellow><b>$KEY_Use</b></color>] Recruit (when affordable)";
                }

                if (player != null && companion.IsPlayerAdmin(player))
                {
                    text += "\n[<color=#00FFFF><b>L.Shift + $KEY_Use</b></color>] Admin tame";
                }
            }
            else
            {
                var companionAI = companion.GetCompanionAI();
                bool isFollowing = companionAI?.GetFollowTarget() != null;

                if (isFollowing)
                    text += " <color=#00FF00>(Following)</color>";
                else
                    text += " <color=#808080>(Staying)</color>";

                var owner = companion.GetOwner();
                if (owner != null)
                {
                    text += $"\n<color=#808080>Owner: {owner.GetPlayerName()}</color>";
                }

                text += "\n[<color=yellow><b>$KEY_Use</b></color>] Inventory";

                if (isFollowing)
                    text += "\n[<color=#00FFFF><b>L.Shift + $KEY_Use</b></color>] Stay";
                else
                    text += "\n[<color=#00FFFF><b>L.Shift + $KEY_Use</b></color>] Follow";
            }

            // Use Localization to process $KEY_Use tokens - this is what NpcController does
            return Localization.instance.Localize(text);
        }

        #endregion

        /// <summary>
        /// Client-only patches that reference EnemyHud (unavailable on dedicated servers).
        /// Separated into a nested class so the server skip-list can exclude these without
        /// losing server-critical patches in the parent class.
        /// </summary>
        [HarmonyPatch]
        public static class CompanionClientPatches_EnemyHud
        {
            #region EnemyHud Billboard Patches

            // Cache reflection info for EnemyHud.HudData fields - using Type object since HudData is private
            private static Type _hudDataType;
            private static System.Reflection.FieldInfo _hudDataHealthFastField;
            private static System.Reflection.FieldInfo _hudDataHealthSlowField;
            private static System.Reflection.FieldInfo _hudDataGuiField;
            private static System.Reflection.FieldInfo _hudDataCharacterField;
            private static System.Reflection.FieldInfo _enemyHudHudsField;
            private static bool _enemyHudReflectionInitialized = false;

            private static void InitializeEnemyHudReflection()
            {
                if (_enemyHudReflectionInitialized) return;
                _enemyHudReflectionInitialized = true;

                try
                {
                    // Get the m_huds field from EnemyHud
                    _enemyHudHudsField = typeof(EnemyHud).GetField("m_huds",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    if (_enemyHudHudsField == null)
                    {
                        Debug.LogWarning("[CompanionPatches] Could not find EnemyHud.m_huds field");
                        return;
                    }

                    // Get the nested HudData type - it's a PRIVATE nested class
                    _hudDataType = typeof(EnemyHud).GetNestedType("HudData", System.Reflection.BindingFlags.NonPublic);
                    if (_hudDataType != null)
                    {
                        // The fields inside HudData are PUBLIC (no binding flags needed for public fields)
                        _hudDataHealthFastField = _hudDataType.GetField("m_healthFast");
                        _hudDataHealthSlowField = _hudDataType.GetField("m_healthSlow");
                        _hudDataGuiField = _hudDataType.GetField("m_gui");
                        _hudDataCharacterField = _hudDataType.GetField("m_character");

                        Debug.Log($"[CompanionPatches] EnemyHud reflection initialized: HudData={_hudDataType != null}, " +
                            $"hudsField={_enemyHudHudsField != null}, healthFast={_hudDataHealthFastField != null}, " +
                            $"healthSlow={_hudDataHealthSlowField != null}, gui={_hudDataGuiField != null}");
                    }
                    else
                    {
                        Debug.LogWarning("[CompanionPatches] Could not find EnemyHud.HudData nested type");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionPatches] Failed to initialize EnemyHud reflection: {ex.Message}");
                }
            }

            /// <summary>
            /// Patch EnemyHud.ShowHud to optionally hide companion health bar billboards based on config.
            /// The billboard is the floating health bar and name above the companion's head.
            /// </summary>
            [HarmonyPatch(typeof(EnemyHud), "ShowHud")]
            [HarmonyPrefix]
            public static bool EnemyHud_ShowHud_Prefix(EnemyHud __instance, Character c, bool isMount)
            {
                try
                {
                    // PERF: Use cached lookup
                    var companion = c != null ? GetCachedCompanion(c) : null;
                    if (companion == null) return true; // Not a companion, show normal hud

                    // Check config - if billboard is disabled for companions, skip showing HUD
                    if (FiresCore.Bridge.NpcConfigBridge.GetBool("ShowCompanionBillboard", true) == false)
                    {
                        return false; // Skip showing billboard for companions
                    }

                    return true; // Config enabled or not set, show billboard normally
                }
                catch
                {
                    return true; // On error, show normally
                }
            }

            /// <summary>
            /// Patch EnemyHud.TestShow to correctly determine if a companion should show their health bar.
            /// 
            /// The problem: TestShow uses Character.GetHealthPercentage() which returns wrong values
            /// for companions because we track health separately in CompanionStats.
            /// This causes the HUD to hide when companions are "damaged" but Character thinks they're full.
            /// 
            /// Solution: For companions, check CompanionStats.HealthPercentage instead.
            /// </summary>
            [HarmonyPatch(typeof(EnemyHud), "TestShow")]
            [HarmonyPostfix]
            public static void EnemyHud_TestShow_Postfix(EnemyHud __instance, Character c, ref bool __result)
            {
                try
                {
                    // PERF: Use cached lookup
                    var companion = c != null ? GetCachedCompanion(c) : null;
                    if (companion == null) return;

                    // If the original TestShow already returned true, no need to override
                    if (__result) return;

                    // Check config - if billboard is disabled, respect that
                    if (FiresCore.Bridge.NpcConfigBridge.GetBool("ShowCompanionBillboard", true) == false)
                    {
                        __result = false;
                        return;
                    }

                    // Get the correct health percentage from CompanionStats
                    var stats = companion.GetStats();
                    if (stats == null) return;

                    float healthPercent = stats.HealthPercentage;

                    // Show HUD if companion is damaged (health < 100%)
                    // This overrides the original TestShow which used Character.GetHealthPercentage()
                    if (healthPercent < 0.999f)
                    {
                        __result = true;
                    }
                }
                catch
                {
                    // On error, don't change the result
                }
            }

            /// <summary>
            /// Patch EnemyHud.UpdateHuds to properly update companion health bars.
            /// This fixes the issue where companion overhead health bars show wrong values.
            /// 
            /// The problem: EnemyHud calls character.GetHealthPercentage() which returns the wrong value
            /// for companions because we use CompanionStats for health tracking.
            /// 
            /// Solution: After the original UpdateHuds runs, we override the health bar values
            /// for companions using the correct values from CompanionStats.
            /// </summary>
            [HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
            [HarmonyPostfix]
            public static void EnemyHud_UpdateHuds_Postfix(EnemyHud __instance, Player player, float dt)
            {
                try
                {
                    // Initialize reflection if not done
                    InitializeEnemyHudReflection();

                    // Check if reflection worked
                    if (_enemyHudHudsField == null || _hudDataType == null)
                    {
                        return; // Reflection failed, can't update bars
                    }

                    if (_hudDataHealthFastField == null && _hudDataHealthSlowField == null)
                    {
                        return; // No health bar fields found
                    }

                    // Get the m_huds dictionary - it's Dictionary<Character, HudData>
                    var hudsObj = _enemyHudHudsField.GetValue(__instance);
                    if (hudsObj == null) return;

                    // If billboard is disabled, hide all companion huds
                    bool billboardDisabled = FiresCore.Bridge.NpcConfigBridge.GetBool("ShowCompanionBillboard", true) == false;

                    // Cast to IDictionary to iterate (works for Dictionary<K,V>)
                    var hudsDict = hudsObj as System.Collections.IDictionary;
                    if (hudsDict == null) return;

                    // PERF: Reuse static list to avoid per-frame allocation
                    _tempHudKeys.Clear();
                    foreach (var key in hudsDict.Keys)
                    {
                        _tempHudKeys.Add(key);
                    }

                    // Process each hud entry
                    foreach (var keyObj in _tempHudKeys)
                    {
                        var character = keyObj as Character;
                        if (character == null) continue;

                        // PERF: Use cached lookup
                        var companion = GetCachedCompanion(character);
                        if (companion == null) continue;

                        var hudData = hudsDict[keyObj];
                        if (hudData == null) continue;

                        // Get the GUI to check if visible and to ensure it stays visible for companions
                        GameObject gui = null;
                        if (_hudDataGuiField != null)
                        {
                            gui = _hudDataGuiField.GetValue(hudData) as GameObject;
                        }

                        // Hide if billboard disabled
                        if (billboardDisabled)
                        {
                            if (gui != null && gui.activeSelf)
                            {
                                gui.SetActive(false);
                            }
                            continue;
                        }

                        // CRITICAL FIX: Force update health bar using CompanionStats values
                        // This ensures the overhead bar shows the correct health, not the stale Character value
                        var stats = companion.GetStats();
                        if (stats == null) continue;

                        // Calculate correct health percentage from CompanionStats
                        float maxHealth = stats.MaxHealth;
                        float currentHealth = stats.CurrentHealth;
                        float healthPercent = maxHealth > 0 ? Mathf.Clamp01(currentHealth / maxHealth) : 0f;

                        // Update the fast (instant) health bar - this is what shows current health
                        if (_hudDataHealthFastField != null)
                        {
                            var healthFast = _hudDataHealthFastField.GetValue(hudData) as GuiBar;
                            if (healthFast != null)
                            {
                                healthFast.SetValue(healthPercent);
                            }
                        }

                        // Update the slow (damage indicator) health bar
                        // Set it to the same value as fast bar - the original UpdateHuds handles the lerping
                        // We just need to ensure both bars show the correct CompanionStats health
                        if (_hudDataHealthSlowField != null)
                        {
                            var healthSlow = _hudDataHealthSlowField.GetValue(hudData) as GuiBar;
                            if (healthSlow != null)
                            {
                                healthSlow.SetValue(healthPercent);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log error only once by using a flag
                    Debug.LogWarning($"[CompanionPatches] EnemyHud_UpdateHuds_Postfix error: {ex.Message}");
                }
            }

            #endregion
        } // end CompanionClientPatches_EnemyHud

        #region Character.SetMoveDir Kinematic Fix

        /// <summary>
        /// Companion movement coordination - ONLY blocks for TRUE frozen states.
        /// 
        /// IMPORTANT: We do NOT block vanilla pathfinding!
        /// The authority system coordinates WHICH system calls MoveTo(),
        /// but once a system has authority, vanilla pathfinding handles the movement.
        /// 
        /// This patch only blocks movement for:
        /// - UI interaction (inventory open, etc.)
        /// - Emote playing
        /// - Chair sitting
        /// - Kinematic rigidbody (teleporting, etc.)
        /// </summary>
        [HarmonyPatch(typeof(Character), nameof(Character.SetMoveDir))]
        [HarmonyPrefix]
        public static bool Character_SetMoveDir_Prefix(Character __instance, Vector3 dir)
        {
            try
            {
                // PERF: Use cached lookup ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â avoids GetComponent on every Character every frame
                var companion = GetCachedCompanion(__instance);
                if (companion == null) return true; // Not a companion, allow normal execution

                // Check if rigidbody is kinematic (Unity 6 compatibility)
                var rigidbody = GetCachedRigidbody(__instance);
                if (rigidbody != null && rigidbody.isKinematic)
                {
                    return false; // Skip SetMoveDir for kinematic bodies
                }

                // Check state controller for TRUE frozen states only
                var stateController = GetCachedStateController(__instance);
                if (stateController != null && stateController.IsInFrozenState)
                {
                    return false; // True frozen state blocks all movement
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Patches Character.UpdateMotion to skip motion updates for companions that should be frozen.
        /// This prevents sliding during UI interaction, emotes, and chair sitting.
        /// 
        /// CRITICAL: Only blocks for TRUE frozen states (UI, Emote, Chair), not for animation blocking.
        /// Animation blocking is handled by the authority's smooth movement transitions.
        /// </summary>
        [HarmonyPatch(typeof(Character), "UpdateMotion")]
        [HarmonyPrefix]
        public static bool Character_UpdateMotion_Prefix(Character __instance, float dt)
        {
            try
            {
                // PERF: Use cached lookup
                var companion = GetCachedCompanion(__instance);
                if (companion == null) return true;

                var rigidbody = GetCachedRigidbody(__instance);
                if (rigidbody != null && rigidbody.isKinematic)
                {
                    return false;
                }

                var stateController = GetCachedStateController(__instance);
                if (stateController != null && stateController.IsInFrozenState)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Patches Character.SyncVelocity to skip velocity sync for companions that should be frozen.
        /// 
        /// CRITICAL: Only blocks for TRUE frozen states (UI, Emote, Chair), not for animation blocking.
        /// </summary>
        [HarmonyPatch(typeof(Character), "SyncVelocity")]
        [HarmonyPrefix]
        public static bool Character_SyncVelocity_Prefix(Character __instance)
        {
            try
            {
                // PERF: Use cached lookup
                var companion = GetCachedCompanion(__instance);
                if (companion == null) return true;

                var rigidbody = GetCachedRigidbody(__instance);
                if (rigidbody != null && rigidbody.isKinematic)
                {
                    return false;
                }

                var stateController = GetCachedStateController(__instance);
                if (stateController != null && stateController.IsInFrozenState)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        #endregion

        #region MonsterAI Stub Prevention

        /// <summary>
        /// Prevents MonsterAI from running on companions.
        /// The stub MonsterAI no longer exists on companion prefabs, so this guard
        /// is a safety net in case a companion somehow ends up with a MonsterAI
        /// (e.g. an old ZDO from a previous version of the mod).
        /// </summary>
        [HarmonyPatch(typeof(MonsterAI), "UpdateAI")]
        [HarmonyPrefix]
        public static bool MonsterAI_UpdateAI_SkipForCompanions(MonsterAI __instance)
        {
            try
            {
                var character = __instance.m_character;
                if (character != null)
                {
                    var companion = GetCachedCompanion(character);
                    if (companion != null)
                        return false; // Skip MonsterAI ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â CompanionAI handles this
                }
            }
            catch { }

            return true;
        }

        #endregion

        #region VisEquipment Guards

        private static readonly HashSet<string> _ourPrefabNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
               {
                   "CompanionNpc",
                   "BaseNpc",
                   "StaticNpc"
               };

        private static bool IsOurPrefab(GameObject go)
        {
            if (go == null) return false;
            string name = go.name;
            foreach (var prefabName in _ourPrefabNames)
            {
                if (name.Equals(prefabName, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(prefabName + "(", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(prefabName + " (", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return go.GetComponent<CompanionController>() != null
                || go.GetComponent<NpcVisEquipment>() != null
                || FiresCore.Bridge.NpcInteractionBridge.HasInteractionController(go);
        }

        /// <summary>
        /// Returns false (skip original) when our prefab's VisEquipment isn't ready.
        /// Covers models, body, shader, ZDO, and attachment-point checks in one place.
        /// </summary>
        private static bool IsVisEquipmentReady(VisEquipment ve)
        {
            if (ve.m_models == null || ve.m_models.Length == 0)
                return false;
            if (ve.m_bodyModel == null)
                return false;

            var mat = ve.m_bodyModel.sharedMaterial;
            if (mat == null || mat.shader == null || !NpcVisEquipment.IsPlayerCompatibleShader(mat.shader.name))
                return false;

            var nview = ve.m_nview;
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
                return false;

            // Guard model index bounds ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â UpdateColors does m_models[GetModelIndex()]
            // and the ZDO can store an index that's >= m_models.Length on freshly
            // spawned companions whose model array hasn't fully initialised yet.
            int modelIndex = ve.GetModelIndex();
            if (modelIndex < 0 || modelIndex >= ve.m_models.Length)
                return false;

            // Validate the specific model entry that will be accessed.
            var modelEntry = ve.m_models[modelIndex];
            if (modelEntry == null || modelEntry.m_mesh == null)
                return false;

            return true;
        }

        // --- AttachItem: guard null joint (applies to ALL prefabs, not just ours) ---

        [HarmonyPatch(typeof(VisEquipment), "AttachItem")]
        [HarmonyPrefix]
        public static bool VisEquipment_AttachItem_Prefix(Transform joint, ref GameObject __result)
        {
            if (joint == null)
            {
                __result = null;
                return false;
            }
            return true;
        }

        // --- Start: skip for our prefabs so NpcVisEquipment controls init ---

        [HarmonyPatch(typeof(VisEquipment), "Start")]
        [HarmonyPrefix]
        public static bool VisEquipment_Start_Prefix(VisEquipment __instance)
        {
            try
            {
                if (!IsOurPrefab(__instance.gameObject))
                    return true;

                var nview = __instance.GetComponent<ZNetView>();
                bool isGhost = nview == null || !nview.IsValid() || nview.GetZDO() == null;
                bool hasWrapper = __instance.GetComponent<NpcVisEquipment>() != null;

                // Block when this is a ghost/preview OR when NpcVisEquipment is present.
                // NpcVisEquipment is now added to all NPC prefabs at build time (VAPieceManager),
                // so hasWrapper is true from the very first frame and Start() is always blocked
                // for our NPCs. NpcVisEquipment.ForceReinitialize() owns the full init sequence.
                if (isGhost || hasWrapper)
                    return false;
            }
            catch { /* allow original on error */ }
            return true;
        }

        // --- GetModelIndex: called independently by CharacterAnimEvent ---

        [HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.GetModelIndex))]
        [HarmonyPrefix]
        public static bool VisEquipment_GetModelIndex_Prefix(VisEquipment __instance, ref int __result)
        {
            try
            {
                if (!IsOurPrefab(__instance.gameObject))
                    return true;

                // Guard m_nview ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â the original method does m_nview.GetZDO().GetInt()
                // which NREs on freshly spawned companions before Start() runs.
                var nview = __instance.m_nview;
                if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
                {
                    __result = 0;
                    return false;
                }

                if (__instance.m_models == null || __instance.m_models.Length == 0)
                {
                    __result = 0;
                    return false;
                }
            }
            catch
            {
                if (IsOurPrefab(__instance.gameObject))
                {
                    __result = 0;
                    return false;
                }
            }
            return true;
        }

        // --- Unified guard for the four visual-update methods ---
        // UpdateVisuals calls UpdateBaseModel, UpdateEquipmentVisuals, and UpdateColors.
        // All four need the same readiness check, so we use one shared predicate.

        [HarmonyPatch(typeof(VisEquipment), "UpdateVisuals")]
        [HarmonyPrefix]
        public static bool VisEquipment_UpdateVisuals_Prefix(VisEquipment __instance)
        {
            return GuardVisualUpdate(__instance);
        }

        [HarmonyPatch(typeof(VisEquipment), "UpdateBaseModel")]
        [HarmonyPrefix]
        public static bool VisEquipment_UpdateBaseModel_Prefix(VisEquipment __instance)
        {
            return GuardVisualUpdate(__instance);
        }

        [HarmonyPatch(typeof(VisEquipment), "UpdateEquipmentVisuals")]
        [HarmonyPrefix]
        public static bool VisEquipment_UpdateEquipmentVisuals_Prefix(VisEquipment __instance)
        {
            return GuardVisualUpdate(__instance);
        }

        [HarmonyPatch(typeof(VisEquipment), "UpdateColors")]
        [HarmonyPrefix]
        public static bool VisEquipment_UpdateColors_Prefix(VisEquipment __instance)
        {
            return GuardVisualUpdate(__instance);
        }

        private static bool GuardVisualUpdate(VisEquipment ve)
        {
            try
            {
                if (!IsOurPrefab(ve.gameObject))
                    return true;
                return IsVisEquipmentReady(ve);
            }
            catch
            {
                if (IsOurPrefab(ve.gameObject))
                    return false;
                return true;
            }
        }

        #endregion

        #region Player Teleport - Companion Follow

        /// <summary>
        /// Patch Player.TeleportTo to teleport following companions with the player.
        /// 
        /// PROBLEM: When a player uses a portal, bed, or any teleport, their following
        /// companions are left behind. The companion eventually loses follow state or
        /// gets stuck trying to pathfind across the world.
        /// 
        /// SOLUTION: Hook into Player.TeleportTo and teleport all following companions
        /// to the player's new position after a short delay.
        ///
        /// DUNGEON / DISTANT TELEPORT NOTE:
        /// Dungeon doors (interior at YÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â°Ãƒâ€¹Ã¢â‚¬Â 5000) sometimes call TeleportTo with
        /// distantTeleport=FALSE despite the huge Y jump, so trusting the flag
        /// alone is unreliable.  We capture the player's position in a Prefix
        /// and compute the actual delta in the Postfix ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â anything larger than
        /// LONG_JUMP_THRESHOLD (XZ) or LONG_JUMP_VERTICAL (Y) is treated as a
        /// distant teleport for our purposes, even when the engine flag says
        /// otherwise.  Forcibly moving companions on the SAME frame as one of
        /// these jumps causes simultaneous ZNetView ownership transfers + ZDO
        /// position writes + RPC broadcasts while the destination zone is still
        /// streaming, which has been observed to deadlock the loading screen
        /// (black screen forever, requires close + relaunch).
        /// </summary>
        private const float LONG_JUMP_THRESHOLD = 200f;   // XZ-distance threshold for "distant teleport" detection
        private const float LONG_JUMP_VERTICAL  = 500f;   // Y-distance threshold (catches dungeon interiors at YÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â°Ãƒâ€¹Ã¢â‚¬Â 5000)
        private static Vector3 _teleportSourcePos;
        private static bool _teleportSourceCaptured;

        /// <summary>
        /// Global timestamp (Time.unscaledTime) until which CompanionController.CheckFollowTeleport
        /// must NOT auto-teleport companions on its own.  Set when we detect a long-jump
        /// player teleport (dungeon entry / portal / etc.) so the deferred Phase 2
        /// coroutine is the only thing that moves companions during the loading
        /// screen + zone-stream window.  Without this, the companion's own 1Hz
        /// stranded-distance tick fires TeleportToOwner before the destination zone
        /// is even loaded ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â and ZoneSystem.GetGroundHeight returns the OUTSIDE
        /// world's terrain Y for dungeon-interior XZ coordinates, putting the
        /// companion at the surface (YÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â°Ãƒâ€¹Ã¢â‚¬Â 38) instead of next to the player at YÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â°Ãƒâ€¹Ã¢â‚¬Â 5000.
        /// That mismatch then re-triggers the stranded check every tick ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ infinite
        /// loop / black screen.
        /// </summary>
        public static float SuppressCompanionTeleportsUntil = 0f;
        public const float SUPPRESS_DURATION_LONG_JUMP = 6f; // seconds ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â covers loading screen + zone settle

        // ── Suppression durations (named, co-located, doc-commented) ──
        // Every place in this file that writes SuppressCompanionTeleportsUntil uses one of
        // these constants. Previously the values were scattered as magic numbers (30/35/6/15/
        // 5/2) at the write sites; consolidated here so future tuning has one place to look
        // and any "why is suppression still on?" investigation can see all six durations
        // side-by-side instead of having to grep them out. SUPPRESS_DURATION_LONG_JUMP above
        // is the historical first const; the rest were added at the same time as this block.

        /// <summary>Death window: covers fade-out + loading screen + wakeup-settle. Generous —
        /// observed loading screens up to ~25s on heavy saves; <see cref="Player.OnSpawned"/>
        /// re-arms with <see cref="SUPPRESS_DURATION_RESPAWN"/> as soon as the new Player object
        /// exists, so this only has to outlive the gap where the OLD player is dead and the
        /// NEW player hasn't constructed yet.</summary>
        public const float SUPPRESS_DURATION_DEATH         = 30f;

        /// <summary>Login / post-death-respawn wakeup window. Starts at OnSpawned and runs
        /// long enough for the reconcile-on-arrival coroutine to confirm IsTeleporting=false +
        /// CanMove=true and dispatch the reconcile RPC.</summary>
        public const float SUPPRESS_DURATION_RESPAWN       = 35f;

        /// <summary>Set after <see cref="CompanionController.CheckFollowTeleport"/>'s stranded
        /// branch fires the reconcile RPC; keeps the other companion controllers from also
        /// firing competing teleports during the same in-flight arrival window.</summary>
        public const float SUPPRESS_DURATION_STRANDED_SETTLE = 15f;

        /// <summary>Re-armed inside <see cref="AreCompanionTeleportsSuppressed"/> when the
        /// local player is null while Game.instance is alive — the auto-respawn coroutine has
        /// destroyed the OLD Player but not yet constructed the NEW one. Short and continuously
        /// re-armed every poll until the new Player exists.</summary>
        public const float SUPPRESS_DURATION_LP_NULL_REARM = 5f;

        /// <summary>Re-armed inside <see cref="AreCompanionTeleportsSuppressed"/> when the
        /// player exists but <c>IsTeleporting</c> or <c>!CanMove</c>. Very short because it's
        /// re-armed every frame the condition holds; the value just has to outlive one tick of
        /// CheckFollowTeleport so the gate doesn't briefly open mid-loading-screen.</summary>
        public const float SUPPRESS_DURATION_LOADING_REARM = 2f;

        public static bool AreCompanionTeleportsSuppressed()
        {
            // Hard window from the long-jump trigger.
            if (Time.unscaledTime < SuppressCompanionTeleportsUntil)
                return true;

            try
            {
                var lp = Player.m_localPlayer;

                // m_localPlayer == null while in-game (Game.instance != null) means
                // we're inside the auto-respawn coroutine ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â the old Player object was
                // destroyed and the new one has not been constructed yet. This window
                // covers the entire respawn sequence: fade-out, zone-load, spawn-point
                // selection, new-Player instantiation. It can run for tens of seconds
                // on a heavy save and the OnDeath timer (30s) may expire inside it.
                // Companion ZDO writes / ability RPCs during this window deadlock the
                // zone stream ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â the canonical respawn-freeze mechanism. Hold the gate
                // closed for the whole window and re-arm on every call so we don't
                // fall through if the timer happens to lapse mid-respawn.
                if (lp == null)
                {
                    if (Game.instance != null)
                    {
                        SuppressCompanionTeleportsUntil = Time.unscaledTime + SUPPRESS_DURATION_LP_NULL_REARM;
                        return true;
                    }
                    // No Game.instance ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â main menu / character select ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â let things proceed.
                    return false;
                }

                // Loading screen still active.
                if (lp.IsTeleporting())
                {
                    SuppressCompanionTeleportsUntil = Time.unscaledTime + SUPPRESS_DURATION_LOADING_REARM;
                    return true;
                }

                // Player spawned but wakeup animation hasn't finished ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â
                // CanMove() returns false until the character has full control.
                // Keep suppressing so CheckFollowTeleport can't fire early.
                if (!lp.CanMove())
                {
                    SuppressCompanionTeleportsUntil = Time.unscaledTime + SUPPRESS_DURATION_LOADING_REARM;
                    return true;
                }
            }
            catch { }

            return false;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        [HarmonyPrefix]
        public static void Player_TeleportTo_Prefix(Player __instance)
        {
            try
            {
                if (__instance != Player.m_localPlayer) return;
                _teleportSourcePos = __instance.transform.position;
                _teleportSourceCaptured = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] Player_TeleportTo_Prefix error: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        [HarmonyPostfix]
        public static void Player_TeleportTo_Postfix(Player __instance, Vector3 pos, Quaternion rot, bool distantTeleport)
        {
            try
            {
                if (__instance != Player.m_localPlayer) return;

                // ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ robust distant-teleport detection ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬
                // The engine flag is unreliable (dungeons sometimes pass false),
                // so we measure the delta ourselves. Any jump bigger than the
                // thresholds is treated as a distant teleport regardless of the
                // engine flag, which is what actually triggers the loading-screen
                // deadlock when companions move on the same frame.
                bool isLongJump = distantTeleport;
                if (_teleportSourceCaptured)
                {
                    Vector3 src = _teleportSourcePos;
                    float dxz = Vector2.Distance(new Vector2(src.x, src.z), new Vector2(pos.x, pos.z));
                    float dy  = Mathf.Abs(src.y - pos.y);
                    if (dxz > LONG_JUMP_THRESHOLD || dy > LONG_JUMP_VERTICAL)
                    {
                        isLongJump = true;
                    }
                    _teleportSourceCaptured = false;
                }

                if (isLongJump)
                {
                    // SUPPRESS the companion controllers' own CheckFollowTeleport
                    // for the next several seconds. Their stranded-distance
                    // check will see the player ~5000m away (because the
                    // player just jumped) and try to teleport locally ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â
                    // those local writes race with the destination zone
                    // load.
                    SuppressCompanionTeleportsUntil = Time.unscaledTime + SUPPRESS_DURATION_LONG_JUMP;
                    Debug.Log($"[CompanionPatches] Long-jump teleport (engineFlag={distantTeleport}) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â suppressing local CheckFollowTeleport for {SUPPRESS_DURATION_LONG_JUMP:F1}s; reconcile-on-arrival coroutine started.");

                    // Player.TeleportTo reuses the existing player object,
                    // so Player.OnSpawned does NOT fire for wayshrine /
                    // portal / dungeon teleports ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â only for login and
                    // death-respawn. Without this start, reconcile never
                    // dispatches and followers stay stranded at the source
                    // zone. Same coroutine the OnSpawned hook uses; waits
                    // for IsTeleporting=false + CanMove=true before
                    // firing, so the server-side ZDO position write lands
                    // after the destination zone is loaded.
                    __instance.StartCoroutine(ReconcileFollowersAfterArrival(__instance));
                }

                // No depart-time companion dispatch. The previous design
                // (MoveCompanionsTo here, dispatching the position the
                // player is teleporting to) raced with the destination
                // zone load on long jumps ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â the server got the RPC before
                // the destination zone was loaded server-side, the ZDO
                // position write went out into nowhere, and the companion
                // either snapped back via ownership flap or stayed at the
                // source. Reconcile-on-arrival (the coroutine started
                // above) replaces this ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â by the time it dispatches, the
                // loading screen is done, the destination zone is loaded,
                // and the player has control. The server then scans
                // ZDOMan for the player's followers and reels in the ones
                // that are far away.
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionPatches] Player_TeleportTo_Postfix error: {ex.Message}");
            }
        }

        // ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬
        //  Phase 2 cleanup: the deferred-sweep teleport machinery is gone.
        //
        //  Removed in Phase 2 of the companion refactor (see
        //  COMPANION_REFACTOR_PLAN.md). Every callsite now flows through
        //  CompanionTeleportService (Modules/Companions/Core/), which:
        //    1. Releases local ZDO ownership for long-jumps so the server
        //       promotes via ZDOMan's closest-peer rule.
        //    2. Routes a targeted RPC to the SERVER peer specifically (not
        //       Everybody), so only the verified-authoritative peer runs
        //       the write. No more ownership-flap revert loop.
        //    3. Server claims ownership explicitly, then calls the
        //       canonical CompanionController.TeleportToDestination, which
        //       broadcasts RPC_TeleportToPosition Everybody for visual
        //       sync on every other peer.
        //
        //  The old code that lived here:
        //    - TeleportFollowingCompanionsDelayed (deferred 2-sweep coroutine)
        //    - SweepFollowingCompanions (per-sweep distance-and-RPC iterator)
        //    - GetSafeTeleportPositionNearPlayer (now unused ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â service
        //      lets CompanionController pick the safe spot via its own
        //      GetSafeTeleportPositionNearPoint helper, which already has
        //      dungeon-altitude awareness)
        //    - RequestRemoteCompanionTeleport (Everybody-broadcast helper)
        //    - OnRequestCompanionTeleport + RPC_RequestCompanionTeleport
        //      (the IsOwner-on-receive RPC handler)
        //    - ZNet_Start_RegisterCompanionTeleportRPC (registered the
        //      legacy RPC at ZNet.Start)
        // ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚ÂÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬


        /// <summary>
        /// Gives crouching companions a proper stealth factor so vanilla BaseAI.CanSenseTarget
        /// scales back their detection range the same way it does for crouching players.
        /// Without this, companions always return 1f (fully visible) even while sneaking
        /// alongside the player, so enemies ignore the crouch entirely.
        ///
        /// Formula mirrors Player.UpdateStealth with skill = 0 (companions are not skilled
        /// sneakers). Tamed and wild companions both benefit ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â a following companion that
        /// crouches with its owner should actually be harder to detect.
        /// </summary>
        [HarmonyPatch(typeof(Character), nameof(Character.GetStealthFactor))]
        [HarmonyPostfix]
        public static void Character_GetStealthFactor_Postfix(Character __instance, ref float __result)
        {
            try
            {
                if (__instance.GetComponent<CompanionController>() == null) return;
                if (!__instance.IsCrouching()) return;

                float lightFactor = StealthSystem.instance != null
                    ? StealthSystem.instance.GetLightFactor(__instance.GetCenterPoint())
                    : 0.5f;

                // Skill = 0  ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢  lerp factor 0, so result = 0.5 + lightFactor * 0.5
                __result = Mathf.Clamp01(0.5f + lightFactor * 0.5f);
            }
            catch { /* never break vanilla stealth code */ }
        }

        /// <summary>
        /// Patches ArcheryTarget.OnProjectileHit so that when a companion hits the target,
        /// the score popup appears in world space above the target instead of on the
        /// player's HUD. Vanilla always calls Player.m_localPlayer.Message() which shows
        /// the score on the owner's screen regardless of distance.
        /// </summary>
        [HarmonyPatch(typeof(ArcheryTarget), nameof(ArcheryTarget.OnProjectileHit))]
        private static class ArcheryTarget_OnProjectileHit_Patch
        {
            static bool Prefix(
                ArcheryTarget __instance,
                Character owner,
                Projectile projectile,
                Vector3 hitPoint,
                ref bool __result)
            {
                // Only intercept when the shooter is NOT the local player
                if (owner == null || owner is Player p && p == Player.m_localPlayer) return true;

                // Check if the owner is a companion
                var companion = owner.GetComponent<CompanionController>();
                if (companion == null) return true;

                // Handle projectile stay TTL (before anything else, same as vanilla)
                if (__instance.m_projectileStayTTL >= 0f)
                    projectile.SetStayTTL(__instance.m_projectileStayTTL);

                // Calculate score the same way vanilla does
                float dist = Vector3.Distance(__instance.m_center.transform.position, hitPoint);
                float norm = dist / __instance.m_targetSize;
                int points = Mathf.Max(0, Mathf.CeilToInt((1f - norm) * __instance.m_points));

                // Show score as world-space floating text at the hit point instead of on player HUD
                if (DamageText.instance != null)
                {
                    DamageText.instance.ShowText(
                        points >= __instance.m_points ? DamageText.TextType.Heal : DamageText.TextType.Normal,
                        hitPoint + Vector3.up * 0.3f,
                        points.ToString(),
                        points >= __instance.m_points
                    );
                }

                // Trigger the scoring RPC (same as vanilla)
                int ammoIndex = __instance.FindAmmoIndex(projectile);
                var nview = __instance.GetComponentInParent<ZNetView>();
                if (nview != null)
                {
                    if (nview.IsOwner())
                    {
                        var method = typeof(ArcheryTarget).GetMethod("ProjectileHit",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        method?.Invoke(__instance, new object[] { points, ammoIndex, hitPoint });
                    }
                    else
                    {
                        nview.InvokeRPC("RPC_ProjectileHit", points, ammoIndex, hitPoint);
                    }
                }

                // Trigger hit effects (same as vanilla)
                foreach (var effect in __instance.m_projectileHitEffects)
                {
                    if (projectile.m_type.HasFlag(effect.m_type))
                        effect.m_effect.Create(hitPoint, __instance.transform.rotation);
                }

                // Raise companion's skill (same as vanilla)
                if (__instance.m_raiseSkillMultiplier > 0f)
                {
                    owner.RaiseSkill(projectile.m_skill,
                        projectile.m_raiseSkillAmount * __instance.m_raiseSkillMultiplier * (1f - norm));
                }

                __result = !__instance.m_killProjectile;
                return false; // Skip vanilla ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â we handled everything
            }
        }
    }
}

#endregion