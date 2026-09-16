using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using FiresCore.Npc.Combat;
using FiresCore.Npc.Vault;

namespace FiresCore.Npc
{
    /// <summary>
    /// Companion death handled like a player's: save state, drop a tombstone with the inventory, destroy the body
    /// through CompanionNetworkHelper, start the respawn timer, and after it spawn a fresh companion that goes back
    /// to loot its tombstone.
    /// </summary>
    public class CompanionDeathHandler : MonoBehaviour
    {
        [Header("Death Settings")]
        public float respawnDelay = 120f;  // 2 minutes default
        // Tamed companions KEEP their equipment on death. Only wild companions
        // drop equipped gear, and that's handled by WildCompanionLootOnDeath
        // with star-scaled chances. The tombstone only carries STORAGE items.
        public bool dropEquipmentOnDeath = false;  // If true, equipment goes to tombstone
        public bool dropInventoryOnDeath = true;   // Storage items go to tombstone
        public float tombstoneLifetime = 3600f;    // 1 hour before tombstone despawns (not yet wired to Container)

        [Header("Respawn Settings")]
        public float respawnRadius = 5f;
        public bool respawnAtOwner = true;
        public bool seekTombstoneOnRespawn = true;
        public float tombstoneSeekRange = 100f;

        [Header("Effects")]
        public GameObject deathEffectPrefab;
        public GameObject respawnEffectPrefab;

        // References
        private CompanionController _companion;
        private CompanionInventory _inventory;
        private CompanionEquipmentData _equipmentData;
        private Character _character;
        private ZNetView _nview;
        private CombatMemory _combatMemory;

        // Death state
        private bool _isDying = false;
        private ZDOID _tombstoneZDOID = ZDOID.None;
        private Character _lastAttacker;

        // Static tracking for respawn management
        private static Dictionary<string, float> _pendingRespawns = new Dictionary<string, float>();
        private static Dictionary<string, ZDOID> _tombstoneLocations = new Dictionary<string, ZDOID>();

        public static bool VerboseLogging = false;

        #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _inventory = GetComponent<CompanionInventory>();
            _equipmentData = GetComponent<CompanionEquipmentData>();
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
            _combatMemory = GetComponent<CombatMemory>();
        }

        private void Start()
        {
            // Hook into character death
            if (_character != null)
            {
                _character.m_onDeath += OnCharacterDeath;
                _character.m_onDamaged += OnDamageReceived;
            }

            // Register RPCs
            if (_nview != null)
            {
                _nview.Register("RPC_CompanionDeath", RPC_CompanionDeath);
                _nview.Register<string>("RPC_SetTombstone", RPC_SetTombstone);
                _nview.Register<long, string, string, float>("RPC_CompanionDeathNotice", RPC_CompanionDeathNotice);
            }

            // Check if we should seek a tombstone (just respawned)
            if (_companion != null && seekTombstoneOnRespawn)
            {
                StartCoroutine(CheckForTombstoneOnSpawn());
            }
        }

        private void OnDamageReceived(float damage, Character attacker)
        {
            if (attacker != null)
                _lastAttacker = attacker;
        }

        private void OnDestroy()
        {
            if (_character != null)
            {
                _character.m_onDeath -= OnCharacterDeath;
                _character.m_onDamaged -= OnDamageReceived;
            }
        }

        #endregion

        #region Death Handling

        private void OnCharacterDeath()
        {
            // CRITICAL: Vanilla Character.OnDeath invokes m_onDeath as a plain
            // multicast delegate BEFORE its final ZNetScene.instance.Destroy(this.gameObject).
            // Any exception that escapes us here aborts the rest of vanilla's
            // OnDeath — including the destroy call — and we end up with an
            // invisible-but-still-targetable corpse that mobs continue to attack.
            // EVERY branch in this method must be wrapped so vanilla always
            // gets to its destroy line.
            try
            {
                if (_isDying) return;
                if (_companion == null) return;

                _isDying = true;

                if (_companion.isTamed && VerboseLogging)
                {
                    Debug.Log($"[CompanionDeathHandler] {_companion.companionName} has died! (tamed: {_companion.isTamed})");
                }

                // Wild companions die for good. If vanilla's own destroy doesn't happen the body lingers, so a safety
                // net destroys it through ZNetScene a moment later; it dies with the body when vanilla did its job.
                if (!_companion.isTamed)
                {
                    if (_nview != null && _nview.IsOwner())
                    {
                        // Static (placed) NPCs ragdoll like companions and leave a joke gravestone instead
                        // of silently vanishing. Wild roamers keep the plain destroy (no body clutter on the
                        // map). Both branches still fall through to the safety-net destroy below.
                        var npcModule = GetComponent<FiresCore.Npc.NpcMode.CompanionNpcModule>();
                        if (npcModule != null && npcModule.isStaticPlacement)
                        {
                            try { PlayDeathEffects(); }
                            catch (Exception ex) { Debug.LogWarning($"[CompanionDeathHandler] static ragdoll failed: {ex.Message}"); }
                            try { CreateStaticNpcTombstone(); }
                            catch (Exception ex) { Debug.LogWarning($"[CompanionDeathHandler] static tombstone failed: {ex.Message}"); }
                        }
                        StartCoroutine(DestroyAfterDelay(1.0f));
                    }
                    return;
                }

                // Only the owner runs full death logic. HandleDeath has its
                // own internal try/catch so a partial failure (e.g. tombstone
                // creation throws) doesn't take out the rest of the chain.
                if (_nview != null && _nview.IsOwner())
                {
                    try { HandleDeath(); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[CompanionDeathHandler] HandleDeath threw for {_companion?.companionName}: {ex.Message}\n{ex.StackTrace}");
                    }
                }

                // Broadcast death to clients for effects.
                // Skip during local player respawn / loading-screen — RPC broadcasts
                // during that window deadlock the zone stream. Other clients still
                // see the death via the destroyed GameObject + ZDO state.
                if (_nview != null && !CompanionPatches.AreCompanionTeleportsSuppressed())
                {
                    try { _nview.InvokeRPC(ZNetView.Everybody, "RPC_CompanionDeath"); }
                    catch (Exception rpcEx)
                    {
                        Debug.LogWarning($"[CompanionDeathHandler] RPC_CompanionDeath broadcast failed: {rpcEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CompanionDeathHandler] OnCharacterDeath fatal error: {ex.Message}\n{ex.StackTrace}");
                // Swallowed deliberately so vanilla Character.OnDeath can
                // continue to its ZNetScene.Destroy line. Better to log here
                // and let vanilla clean the body up than to leave invisible
                // corpses scattered around the world.
            }
        }

        public void HandleDeath()
        {
            // 0. Record death in combat memory
            if (_combatMemory != null && _lastAttacker != null)
            {
                _combatMemory.OnDeathBy(_lastAttacker);
            }

            // 0b. Record death in kill tracker FIRST
            var killTracker = GetComponent<CompanionKillTracker>();
            if (killTracker != null)
            {
                killTracker.OnCompanionDeath();
                killTracker.SaveToZDO();
                Debug.Log($"[CompanionDeathHandler] Death recorded for {_companion.companionName}: total deaths = {killTracker.Deaths}");
            }

            // 1. Save companion state to vault (BEFORE tombstone creation)
            SaveDeathState();

            // 2. Create tombstone (only if we are dropping anything)
            if (dropInventoryOnDeath || dropEquipmentOnDeath)
            {
                CreateTombstone();
            }

            // 3. Now that tombstone ZDOID is known, save death metadata
            SaveDeathMetadata();

            // 4. Play death effects
            PlayDeathEffects();

            // 5. Schedule respawn
            ScheduleRespawn();

            // 6. Notify owner
            NotifyOwnerOfDeath();

            // Vanilla OnDeath should destroy the body right after this callback, but tamed companions have been seen to
            // survive it. This coroutine lives on the body, so it dies with it if vanilla did its job; otherwise it
            // destroys the body through ZNetScene after a delay.
            if (_nview != null && _nview.IsOwner())
            {
                StartCoroutine(DestroyAfterDelay(1.0f));
            }
        }

        /// <summary>True when the snapshot carries only prefab-placeholder identity (no real name).</summary>
        private static bool IsPlaceholderSnapshot(NpcSaveState snap)
        {
            if (snap == null) return true;
            var name = snap.DisplayName;
            return string.IsNullOrWhiteSpace(name)
                || name == "Companion"
                || name == "Companion NPC"
                || name == "CompanionNpc"
                || name == "CompanionNpc_Wild";
        }

        private void SaveDeathState()
        {
            if (_companion == null || _companion.ownerPlayerId == 0) return;

            // ── Dormant store (kennel standalone / vault integrated) — the authoritative write ──
            // Capture the Core NpcSaveState, apply the same drop-to-tombstone mutations as the
            // legacy path below, and store a DeadPendingRespawn entry with an ABSOLUTE wall-clock
            // deadline (crash-resumable). This is ungated by the legacy vault on purpose: the old
            // `!IsVaultAvailable() => return` at the top meant STANDALONE death persisted NOTHING —
            // dead companions were silently lost (the kennel's OnDeath hook never fires because the
            // death flow uses OnDeathWithSnapshot). The dormancy seam is what the reworked restore
            // engine reads, so this is now the source of truth for the death snapshot.
            try
            {
                long deadlineTicks = DateTime.UtcNow.AddSeconds(Mathf.Max(0f, respawnDelay)).Ticks;
                var deathSnap = _companion.CaptureState();
                if (deathSnap != null && FiresCore.Bridge.NpcDormancyBridge.IsAvailable)
                {
                    // A capture taken from an instance whose deferred ZDO load hadn't populated yet
                    // (fresh zone-stream, the ~1s CompanionInventory window) carries only the prefab
                    // placeholder name and empty gear. Never let that overwrite a good kennel entry:
                    // if the capture is blank and the kennel already holds a real snapshot for this
                    // companion, keep the stored identity and only update the death bookkeeping.
                    if (IsPlaceholderSnapshot(deathSnap))
                    {
                        var existing = FiresCore.Bridge.NpcDormancyBridge.Get(_companion.ownerPlayerId, deathSnap.NpcId);
                        if (existing?.Snapshot != null && !IsPlaceholderSnapshot(existing.Snapshot))
                        {
                            Debug.LogWarning($"[CompanionDeathHandler] Death capture for {deathSnap.NpcId} was a blank placeholder — preserving the kennel's last good snapshot ('{existing.Snapshot.DisplayName}')");
                            deathSnap = existing.Snapshot;
                        }
                    }

                    if (dropEquipmentOnDeath)
                    {
                        deathSnap.EquipmentPrefabs?.Clear();
                        deathSnap.EquipmentQualities?.Clear();
                    }
                    if (dropInventoryOnDeath)
                        deathSnap.StorageInventoryData = null;

                    FiresCore.Bridge.NpcDormancyBridge.Store(_companion.ownerPlayerId, new FiresCore.Bridge.DormantNpcEntry
                    {
                        NpcId                  = deathSnap.NpcId,
                        Kind                   = FiresCore.Bridge.DormancyKind.DeadPendingRespawn,
                        RecallDeadlineUtcTicks = deadlineTicks,
                        Snapshot               = deathSnap,
                    });
                    if (VerboseLogging)
                        Debug.Log($"[CompanionDeathHandler] Stored dormant death entry for {_companion.companionName} (respawn in {respawnDelay:F0}s)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionDeathHandler] Dormant-store death write failed (non-fatal): {ex.Message}");
            }

            // ── Legacy vault / m_customData roster mirror (integrated mode only; deleted in Phase 5) ──
            if (!FiresCore.Bridge.CompanionVaultBridge.IsVaultAvailable()) return;

            try
            {
                var saveData = CompanionVault.BuildSaveData(_companion);
                if (saveData == null)
                {
                    Debug.LogError($"[CompanionDeathHandler] Failed to build save data for {_companion.companionName}");
                    return;
                }

                Debug.Log($"[CompanionDeathHandler] SaveDeathState - KillsData being saved: '{saveData.KillsData}' for {_companion.companionName}");

                // Add death-specific state
                saveData.IsPendingRespawn = true;
                saveData.RespawnTimeRemaining = respawnDelay;
                saveData.DeathTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Clear saved data for anything we're dropping to the tombstone
                if (dropEquipmentOnDeath)
                {
                    saveData.EquipmentPrefabs.Clear();
                    saveData.EquipmentQualities.Clear();
                }
                if (dropInventoryOnDeath)
                {
                    saveData.StorageInventoryData = null;
                }

                bool saved = false;
                // Phase 6: vault save is handled by the periodic
                // FlushDebugMirror which reads from the per-player
                // roster. The roster mirror write below is the
                // authoritative store for the death snapshot.

                // ── ROSTER MIRROR (Phase 3 of save refactor) ──────────────
                // Authoritative store for the death snapshot. Uses the
                // post-mutation saveData (with dropped-to-tombstone
                // equipment / inventory already cleared above) and
                // converts the seconds-remaining respawn delay into a
                // wall-clock deadline so a crash mid-respawn-timer is
                // recoverable on next login.
                try
                {
                    var ownerPlayer = _companion.GetOwner();
                    if (ownerPlayer != null)
                    {
                        saved = CompanionRosterWriter.OnDeathWithSnapshot(ownerPlayer, saveData, respawnDelay);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionDeathHandler] Roster mirror write failed (non-fatal): {ex.Message}");
                }

                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionDeathHandler] Saved death state for {_companion.companionName} (rosterSaved={saved}, respawnIn={respawnDelay}s)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CompanionDeathHandler] Failed to save death state: {ex.Message}");
            }
        }

        private void SaveDeathMetadata()
        {
            float respawnTime = Time.time + respawnDelay;
            _pendingRespawns[_companion.companionId] = respawnTime;

            var zdo = _nview?.GetZDO();
            if (zdo != null)
            {
                zdo.Set("companion_death_time", Time.time);
                zdo.Set("companion_respawn_time", respawnTime);
                zdo.Set("companion_tombstone", _tombstoneZDOID.ToString());
            }
        }

        #endregion

        #region Tombstone System

        /// <summary>
        /// SAFE VERSION - Uses CompanionNetworkHelper.Spawn so the tombstone is
        /// properly registered with ZNetScene. This is the most likely fix for
        /// the ZNetScene.RemoveObjects NullReferenceException you were seeing.
        /// </summary>
        private void CreateTombstone()
        {
            if (_inventory == null) return;

            GameObject tombstonePrefab = GetTombstonePrefab();
            if (tombstonePrefab == null)
            {
                Debug.LogWarning("[CompanionDeathHandler] No tombstone prefab found, dropping items on ground");
                DropItemsOnGround();
                return;
            }

            // Safe spawn position (snap to ground)
            Vector3 spawnPos = transform.position;
            if (ZoneSystem.instance != null)
            {
                if (ZoneSystem.instance.GetGroundHeight(spawnPos, out float groundHeight))
                {
                    spawnPos.y = groundHeight + 0.5f;
                }
            }

            // <<< THIS IS THE KEY CHANGE >>>
            // Use the safe networked spawn helper instead of raw Instantiate
            GameObject tombstoneObj = CompanionNetworkHelper.Spawn(tombstonePrefab, spawnPos, Quaternion.identity);

            if (tombstoneObj == null)
            {
                Debug.LogError("[CompanionDeathHandler] Failed to spawn tombstone!");
                DropItemsOnGround();
                return;
            }

            // Configure the tombstone component
            var tombstone = tombstoneObj.GetComponent<TombStone>();
            if (tombstone != null)
            {
                tombstone.Setup($"{_companion.companionName}'s Gravestone", _companion.ownerPlayerId);
            }

            // Mark as companion tombstone in ZDO
            var nview = tombstoneObj.GetComponent<ZNetView>();
            if (nview != null)
            {
                var zdo = nview.GetZDO();
                if (zdo != null)
                {
                    zdo.Set("companion_tombstone", true);
                    zdo.Set("companion_id", _companion.companionId);
                    zdo.Set("companion_name", _companion.companionName);
                }
            }

            // Transfer loot if we have a container
            var container = tombstoneObj.GetComponent<Container>();
            if (container != null)
            {
                var tombstoneInv = container.GetInventory();
                if (tombstoneInv != null)
                {
                    TransferItemsToTombstone(tombstoneInv);
                }
            }

            // Store ZDOID for later loot / respawn logic
            if (nview != null)
            {
                _tombstoneZDOID = nview.GetZDO().m_uid;
                _tombstoneLocations[_companion.companionId] = _tombstoneZDOID;

                // Broadcast to other clients (skip during respawn freeze window).
                if (_nview != null && !CompanionPatches.AreCompanionTeleportsSuppressed())
                {
                    _nview.InvokeRPC(ZNetView.Everybody, "RPC_SetTombstone", _tombstoneZDOID.ToString());
                }
            }

            if (VerboseLogging)
            {
                Debug.Log($"[CompanionDeathHandler] Created tombstone for {_companion.companionName} at {spawnPos}");
            }
        }

        /// <summary>
        /// Spawns a gravestone for a dead STATIC NPC holding exactly one Pukeberries - a joke drop so killed
        /// static NPCs leave a body + marker instead of vanishing. Mirrors CreateTombstone but never touches
        /// the NPC's own inventory. Degrades gracefully if the tombstone or Pukeberries prefab is missing.
        /// </summary>
        private void CreateStaticNpcTombstone()
        {
            var tombstonePrefab = GetTombstonePrefab();
            if (tombstonePrefab == null) return;

            Vector3 spawnPos = transform.position;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(spawnPos, out float groundHeight))
                spawnPos.y = groundHeight + 0.5f;

            var tombstoneObj = CompanionNetworkHelper.Spawn(tombstonePrefab, spawnPos, Quaternion.identity);
            if (tombstoneObj == null) return;

            var tombstone = tombstoneObj.GetComponent<TombStone>();
            if (tombstone != null)
                tombstone.Setup($"Here lies {_companion.companionName}", _companion.ownerPlayerId);

            var container = tombstoneObj.GetComponent<Container>();
            var inv = container != null ? container.GetInventory() : null;
            if (inv != null)
            {
                var pukePrefab = ZNetScene.instance?.GetPrefab("Pukeberries");
                var pukeDrop = pukePrefab != null ? pukePrefab.GetComponent<ItemDrop>() : null;
                if (pukeDrop != null && pukeDrop.m_itemData != null)
                {
                    var puke = pukeDrop.m_itemData.Clone();
                    puke.m_stack = 1;
                    inv.AddItem(puke);
                }
            }
        }

        private void TransferItemsToTombstone(Inventory tombstoneInv)
        {
            // Storage inventory
            if (dropInventoryOnDeath)
            {
                var storageInv = _inventory.GetStorageInventory();
                if (storageInv != null)
                {
                    foreach (var item in storageInv.GetAllItems().ToArray())
                    {
                        if (tombstoneInv.AddItem(item.Clone()))
                        {
                            storageInv.RemoveItem(item);
                        }
                    }
                }
            }

            // Equipment
            if (dropEquipmentOnDeath)
            {
                foreach (CompanionInventory.EquipmentSlot slot in Enum.GetValues(typeof(CompanionInventory.EquipmentSlot)))
                {
                    var item = _inventory.GetEquippedItem(slot);
                    if (item != null)
                    {
                        tombstoneInv.AddItem(item.Clone());
                        _inventory.UnequipSlotSilent(slot);
                    }
                }
            }
        }

        private void DropItemsOnGround()
        {
            var storageInv = _inventory?.GetStorageInventory();
            if (storageInv == null) return;

            foreach (var item in storageInv.GetAllItems().ToArray())
            {
                Vector3 dropPos = transform.position + UnityEngine.Random.insideUnitSphere * 2f;
                dropPos.y = transform.position.y + 1f;

                ItemDrop.DropItem(item, 1, dropPos, Quaternion.identity);
                storageInv.RemoveItem(item);
            }
        }

        private GameObject GetTombstonePrefab()
        {
            if (ZNetScene.instance == null) return null;

            var prefab = ZNetScene.instance.GetPrefab("Player_tombstone");
            if (prefab != null) return prefab;

            return ZNetScene.instance.GetPrefab("TombStone");
        }

        #endregion

        #region Respawn System

        private void ScheduleRespawn()
        {
            if (_companion.ownerPlayerId == 0) return;

            float respawnTime = Time.time + respawnDelay;
            _pendingRespawns[_companion.companionId] = respawnTime;

            // The respawn must run where the kennel is READABLE — the server. On a dedicated server a
            // client-owned companion dies on the CLIENT, which cannot read the server-owned kennel ZDO,
            // so a client-scheduled respawn loops "Kennel has no entry yet" forever (the exact symptom).
            // The DeadPendingRespawn entry SaveDeathState just forwarded to the server triggers the
            // server-side respawn there (CompanionKennel.RPC_Store). Only the server schedules directly.
            if (ZNet.instance != null && !ZNet.instance.IsServer())
            {
                Debug.Log($"[CompanionDeathHandler] {_companion.companionName} died client-side — server will respawn it from the forwarded kennel entry.");
                return;
            }

            CompanionRespawnManager.Instance?.ScheduleRespawn(
                _companion.companionId,
                _companion.ownerPlayerId,
                GetPrefabName(),
                respawnDelay,
                _tombstoneZDOID
            );

            if (VerboseLogging)
            {
                Debug.Log($"[CompanionDeathHandler] Scheduled respawn for {_companion.companionName} in {respawnDelay}s");
            }
        }

        private IEnumerator CheckForTombstoneOnSpawn()
        {
            yield return new WaitForSeconds(2f);

            if (_companion == null || string.IsNullOrEmpty(_companion.companionId)) yield break;

            if (_tombstoneLocations.TryGetValue(_companion.companionId, out var tombstoneZDOID))
            {
                StartCoroutine(PassiveTombstoneMonitor(tombstoneZDOID));
            }
        }

        private IEnumerator PassiveTombstoneMonitor(ZDOID tombstoneZDOID)
        {
            const float AutoLootRange = 5f;
            const float CheckInterval = 1f;
            const float MaxMonitorTime = 3600f;

            if (VerboseLogging)
                Debug.Log($"[CompanionDeathHandler] {_companion.companionName} passively monitoring for tombstone");

            float monitorTime = 0f;

            while (monitorTime < MaxMonitorTime)
            {
                yield return new WaitForSeconds(CheckInterval);
                monitorTime += CheckInterval;

                if (_companion == null || transform == null)
                {
                    _tombstoneLocations.Remove(_companion?.companionId ?? "");
                    yield break;
                }

                GameObject tombstoneObj = ZNetScene.instance?.FindInstance(tombstoneZDOID);
                if (tombstoneObj == null)
                {
                    if (VerboseLogging) Debug.Log($"[CompanionDeathHandler] Tombstone no longer exists");
                    _tombstoneLocations.Remove(_companion.companionId);
                    yield break;
                }

                float distance = Vector3.Distance(transform.position, tombstoneObj.transform.position);
                if (distance <= AutoLootRange)
                {
                    Debug.Log($"[CompanionDeathHandler] {_companion.companionName} auto-looting tombstone!");
                    LootTombstone(tombstoneObj);
                    _tombstoneLocations.Remove(_companion.companionId);
                    yield break;
                }
            }

            Debug.Log($"[CompanionDeathHandler] Tombstone monitor timed out for {_companion.companionName}");
        }

        private void LootTombstone(GameObject tombstoneObj)
        {
            var container = tombstoneObj.GetComponent<Container>();
            if (container == null) return;

            var tombstoneInv = container.GetInventory();
            if (tombstoneInv == null) return;

            var companionInv = _inventory?.GetStorageInventory();
            if (companionInv == null) return;

            int itemsLooted = 0;
            foreach (var item in tombstoneInv.GetAllItems().ToArray())
            {
                if (companionInv.AddItem(item.Clone()))
                {
                    tombstoneInv.RemoveItem(item);
                    itemsLooted++;
                }
            }

            if (VerboseLogging)
                Debug.Log($"[CompanionDeathHandler] {_companion.companionName} looted {itemsLooted} items from tombstone");

            // NOTE: We deliberately do NOT destroy the empty tombstone ourselves.
            // Vanilla TombStone has its own auto-cleanup (m_emptiedTime + a hammered
            // delay) and will remove the GameObject through ZNetScene the proper way.
            // Force-destroying it from here was a strong suspect for the
            // ZNetScene.RemoveObjects NullReferenceException seen around companion
            // deaths: tearing down a vanilla-registered ZNetView mid-flow can leave
            // a stale entry in ZNetScene.m_instances that NREs on the next zone
            // refresh tick. Per the rule "never force-remove anything that's
            // registered with ZNetScene — let vanilla handle it", this is a no-op.

            var owner = _companion.GetOwner();
            if (owner != null && owner == Player.m_localPlayer)
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    $"{_companion.GetDisplayName()} recovered items from gravestone!");
            }
        }

        #endregion

        #region Effects & Notifications

        private void PlayDeathEffects()
        {
            // Spawn a Player_ragdoll if vanilla isn't already going to do it
            // for us via the character's m_deathEffects list. Most mod-added
            // companion prefabs ship with no Ragdoll prefab in m_deathEffects,
            // so vanilla Character.OnDeath spawns nothing and we provide one;
            // if a future prefab DOES wire one up, we get out of the way to
            // avoid double-spawning.
            if (!VanillaWillSpawnRagdoll())
            {
                SpawnPlayerRagdoll();
            }

            if (deathEffectPrefab != null)
            {
                UnityEngine.Object.Instantiate(deathEffectPrefab, transform.position, Quaternion.identity);
            }
            else
            {
                var effectPrefab = ZNetScene.instance?.GetPrefab("fx_creature_tamed_death");
                if (effectPrefab != null)
                {
                    UnityEngine.Object.Instantiate(effectPrefab, transform.position, Quaternion.identity);
                }
            }
        }

        /// <summary>
        /// Returns true if vanilla <see cref="Character.OnDeath"/> will already
        /// spawn a ragdoll for us via <c>m_deathEffects</c>. The vanilla code
        /// iterates that list, calls <c>GetComponent&lt;Ragdoll&gt;()</c> on each
        /// spawned prefab, and if it finds one it does the full Setup + drop
        /// disable. We mirror that check here so we never double-spawn.
        /// </summary>
        private bool VanillaWillSpawnRagdoll()
        {
            try
            {
                if (_character == null) return false;
                var effects = _character.m_deathEffects?.m_effectPrefabs;
                if (effects == null) return false;

                foreach (var fx in effects)
                {
                    if (fx == null || fx.m_prefab == null) continue;
                    if (fx.m_prefab.GetComponent<Ragdoll>() != null) return true;
                }
            }
            catch { /* defensive — if we can't tell, assume vanilla won't and we'll spawn */ }
            return false;
        }

        private void SpawnPlayerRagdoll()
        {
            try
            {
                if (_nview == null || !_nview.IsOwner()) return;
                if (ZNetScene.instance == null) return;

                var ragdollPrefab = ZNetScene.instance.GetPrefab("Player_ragdoll");
                if (ragdollPrefab == null) return;

                var pos = transform.position;
                var rot = transform.rotation;

                var ragdollObj = UnityEngine.Object.Instantiate(ragdollPrefab, pos, rot);
                var ragdoll = ragdollObj.GetComponent<Ragdoll>();

                Vector3 velocity = Vector3.zero;
                var ourBody = _character != null ? _character.m_body : null;
                if (ourBody != null) velocity = ourBody.linearVelocity;

                if (_character != null && _character.m_lastHit != null)
                {
                    var lastHit = _character.m_lastHit;
                    Vector3 push = lastHit.m_dir * lastHit.m_pushForce;
                    if (push.magnitude * 0.5f > velocity.magnitude)
                        velocity = push * 0.5f;
                }

                if (ragdoll != null)
                {
                    var ourDrop = GetComponent<CharacterDrop>();
                    var ourLevelFx = GetComponentInChildren<LevelEffects>();
                    float hue = 0f, sat = 0f, value = 0f;
                    if (ourLevelFx != null)
                        ourLevelFx.GetColorChanges(out hue, out sat, out value);

                    ragdoll.Setup(velocity, hue, sat, value, ourDrop);

                    if (ourDrop != null && ragdoll.m_dropItems)
                        ourDrop.SetDropsEnabled(false);
                }

                var ourVis = GetComponent<VisEquipment>();
                var ragVis = ragdollObj.GetComponent<VisEquipment>();
                var ourHumanoid = GetComponent<Humanoid>();
                if (ourVis != null && ragVis != null)
                {
                    try
                    {
                        ragVis.SetSkinColor(ourVis.m_skinColor);
                        ragVis.SetHairColor(ourVis.m_hairColor);
                        ragVis.SetModel(ourVis.m_modelIndex);
                        // m_hairItem / m_beardItem flipped string→int across builds. Mirror them
                        // via reflection so the value passes through with its native type intact.
                        VisEquipmentCompat.CopyHairItem(ourVis, ragVis);
                        VisEquipmentCompat.CopyBeardItem(ourVis, ragVis);

                        if (ourHumanoid != null)
                        {
                            VisEquipmentCompat.SetChestItem(ragVis, ourHumanoid.m_chestItem?.m_dropPrefab?.name ?? "");
                            VisEquipmentCompat.SetLegItem(ragVis, ourHumanoid.m_legItem?.m_dropPrefab?.name ?? "");
                            VisEquipmentCompat.SetHelmetItem(ragVis, ourHumanoid.m_helmetItem?.m_dropPrefab?.name ?? "");
                            VisEquipmentCompat.SetShoulderItem(ragVis,
                                ourHumanoid.m_shoulderItem?.m_dropPrefab?.name ?? "",
                                ourHumanoid.m_shoulderItem != null ? ourHumanoid.m_shoulderItem.m_variant : 0);
                            VisEquipmentCompat.SetUtilityItem(ragVis, ourHumanoid.m_utilityItem?.m_dropPrefab?.name ?? "");
                        }
                    }
                    catch (Exception copyEx)
                    {
                        if (VerboseLogging)
                            Debug.LogWarning($"[CompanionDeathHandler] VisEquipment copy to ragdoll failed: {copyEx.Message}");
                    }
                }

                foreach (var childRenderer in GetComponentsInChildren<Renderer>(includeInactive: false))
                {
                    if (childRenderer != null) childRenderer.enabled = false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionDeathHandler] SpawnPlayerRagdoll failed: {ex.Message}");
            }
        }

        private void NotifyOwnerOfDeath()
        {
            if (_nview == null || _companion.ownerPlayerId == 0) return;
            // RPC broadcasts during the local-player respawn/loading window deadlock the zone stream.
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;

            // Broadcast so the OWNING player gets the message whether they're the listen-host or a
            // remote client on a dedicated server — HandleDeath runs on the ZDO owner (the server),
            // which has no local Player. Each client filters by owner id and localizes the killer
            // name itself, so it reads correctly per-language even when the server is headless.
            string killerRaw = _lastAttacker != null ? (_lastAttacker.m_name ?? "") : "";
            try
            {
                _nview.InvokeRPC(ZNetView.Everybody, "RPC_CompanionDeathNotice",
                    _companion.ownerPlayerId, _companion.GetDisplayName(), killerRaw, respawnDelay);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionDeathHandler] death notice RPC failed: {ex.Message}");
            }
        }

        #endregion

        #region RPCs

        private void RPC_CompanionDeath(long sender)
        {
            if (_nview != null && _nview.IsOwner()) return;
            PlayDeathEffects();
        }

        // Shown on every client; only the owning player actually displays it. Killer name is localized
        // here (client-side) so it respects each player's language even when the server is headless.
        private void RPC_CompanionDeathNotice(long sender, long ownerPlayerId, string companionName, string killerRaw, float delay)
        {
            var localPlayer = Player.m_localPlayer;
            if (localPlayer == null || localPlayer.GetPlayerID() != ownerPlayerId) return;

            string killer = null;
            if (!string.IsNullOrEmpty(killerRaw))
            {
                try { killer = Localization.instance.Localize(killerRaw); }
                catch { killer = killerRaw; }
            }

            string msg = string.IsNullOrEmpty(killer)
                ? $"{companionName} died! Respawning in {delay:F0}s..."
                : $"{companionName} died to {killer}! Respawning in {delay:F0}s...";

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, msg);
        }

        private void RPC_SetTombstone(long sender, string zdoidStr)
        {
            if (string.IsNullOrEmpty(zdoidStr)) return;

            try
            {
                var parts = zdoidStr.Split(':');
                if (parts.Length == 2 &&
                    long.TryParse(parts[0], out var userId) &&
                    uint.TryParse(parts[1], out var id))
                {
                    _tombstoneZDOID = new ZDOID(userId, id);
                    if (_companion != null)
                        _tombstoneLocations[_companion.companionId] = _tombstoneZDOID;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionDeathHandler] Failed to parse tombstone ZDOID: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// <summary>
        /// Safety-net destroy after a short delay. See HandleDeath step 7 and
        /// OnCharacterDeath's wild-companion branch for the rationale: this
        /// is a no-op when vanilla Character.OnDeath destroys the GameObject
        /// itself (the coroutine dies with the GO before WaitForSeconds
        /// elapses), and a fallback destroy when vanilla doesn't reach its
        /// ZNetScene.Destroy line. Always routes through
        /// CompanionNetworkHelper.Destroy — i.e. ZNetScene.Destroy under
        /// the hood — so the ZDO is unregistered cleanly from m_instances.
        /// </summary>
        private IEnumerator DestroyAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (this == null || gameObject == null) yield break;

            // Clean up any in-flight attack VFX (or other child ZNetView'd
            // GameObjects) BEFORE the parent destroy. Vanilla Unity will
            // otherwise cascade-destroy them via plain Object.Destroy when
            // the parent transform goes — that path NEVER calls
            // ZNetScene.Destroy, so the children's ZDOs stay stranded in
            // ZNetScene.m_instances and surface later as the
            // ZNetSceneStaleInstanceDiagnostic warnings (most reproducibly
            // 'sfx_atgeir_attack_secondary' on a companion that dies mid-
            // secondary-swing — the swirl VFX is parented to the weapon
            // attach node and gets cascade-destroyed with the corpse).
            DestroyChildZNetViewsCleanly();

            CompanionNetworkHelper.Destroy(gameObject);
        }

        /// <summary>
        /// Detaches and destroys every child ZNetView through CompanionNetworkHelper, so child ZDOs leave
        /// ZNetScene.m_instances instead of being orphaned by the parent's destruction cascade.
        /// </summary>
        private void DestroyChildZNetViewsCleanly()
        {
            try
            {
                var ownNview = _nview;
                var childViews = GetComponentsInChildren<ZNetView>(includeInactive: true);
                if (childViews == null || childViews.Length == 0) return;

                for (int i = 0; i < childViews.Length; i++)
                {
                    var childView = childViews[i];
                    if (childView == null) continue;
                    if (childView == ownNview) continue;            // parent's own — handled by the destroy below
                    if (!childView.IsValid()) continue;             // already torn down
                    var go = childView.gameObject;
                    if (go == null || go == this.gameObject) continue;

                    try
                    {
                        // Detach from the dying parent so Unity's cascade
                        // can't touch this child after we destroy it.
                        go.transform.SetParent(null, worldPositionStays: true);
                    }
                    catch
                    {
                        // ignore — the destroy call below is the important part
                    }

                    try
                    {
                        CompanionNetworkHelper.Destroy(go);
                    }
                    catch (Exception destroyEx)
                    {
                        Debug.LogWarning($"[CompanionDeathHandler] Failed to clean-destroy child ZNetView '{go.name}' on {_companion?.companionName}: {destroyEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionDeathHandler] DestroyChildZNetViewsCleanly failed for {_companion?.companionName}: {ex.Message}");
            }
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

        public static ZDOID GetTombstoneForCompanion(string companionId)
        {
            return _tombstoneLocations.TryGetValue(companionId, out var zdoid) ? zdoid : ZDOID.None;
        }

        public static bool HasPendingRespawn(string companionId)
        {
            return _pendingRespawns.ContainsKey(companionId);
        }

        public static float GetRespawnTime(string companionId)
        {
            return _pendingRespawns.TryGetValue(companionId, out var time) ? time : 0f;
        }

        #endregion
    }
}
