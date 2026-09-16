using System;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc.NpcMode;

// CompanionPatches lives in FiresCore.Npc (parent namespace) — referenced as a fully-qualified name below.

namespace FiresCore.Npc.Vault
{
    /// <summary>
    /// The legacy companion vault, now a read-only JSON mirror for debugging. The live ZDO and the player's
    /// roster in Player.m_customData are authoritative; the JSON is refreshed by <see cref="FlushDebugMirror"/>
    /// on a timer and by <see cref="FlushPlayerToVault"/> at logout. The Save, Get and Remove APIs are obsolete,
    /// while <see cref="BuildSaveData"/> and <see cref="RestoreCompanion"/> remain the shared field copy.
    /// </summary>
    public static class CompanionVault
    {
        #region Configuration

        /// <summary>
        /// Enable verbose logging for debugging save/load operations.
        /// </summary>
        public static bool VerboseLogging = false;

        #endregion

        #region Flush Tracking

        // Last time the periodic vault mirror was flushed.
        private static float _lastDirtyFlushTime = 0f;

        /// <summary>
        /// How often the periodic vault flush runs (seconds). The vault
        /// is a debug mirror as of Phase 6 - nothing reads it on hot
        /// paths - so a 30 s lag is acceptable.
        /// </summary>
        public const float DIRTY_FLUSH_INTERVAL = 30f;

        #endregion
        
        #region Periodic Flush (Phase 6: authoritative ? debug-mirror)

        /// <summary>
        /// Periodic tick driver. Mirrors the authoritative state
        /// (live ZDOs + per-player roster) into the vault JSON every
        /// <see cref="DIRTY_FLUSH_INTERVAL"/> seconds. The vault is
        /// strictly a debug mirror as of Phase 6 - nothing reads it
        /// for restore decisions.
        /// </summary>
        public static void Tick()
        {
            // The vault JSON is only a debug mirror now. Keeping the periodic
            // mirror enabled during normal gameplay rewrites the player file once
            // per companion every interval, producing repeated "Saved player data"
            // spam and unnecessary disk churn in single-player. Explicit logout
            // flushes still keep the mirror current when the player leaves.
            if (!VerboseLogging) return;

            // Don't even arm the timer during the local player's respawn / loading
            // window. FlushDebugMirror writes to VaultOfKnowledge AND to the local
            // player's m_customData (via the roster mirror) — both paths can
            // deadlock the zone stream if they fire mid-teleport. Leaving
            // _lastDirtyFlushTime un-advanced means the next Tick after the gate
            // opens flushes immediately.
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;

            if (Time.time - _lastDirtyFlushTime >= DIRTY_FLUSH_INTERVAL)
            {
                _lastDirtyFlushTime = Time.time;
                FlushDebugMirror();
            }
        }

        /// <summary>
        /// Rewrites the vault mirror from the authoritative sources: every live companion from its own state, then
        /// every roster entry without a live companion (dismissed, pending respawn, out of range) from its snapshot.
        /// Failures on one companion are logged and skipped.
        /// </summary>
        public static void FlushDebugMirror()
        {
            if (!FiresCore.Bridge.CompanionVaultBridge.IsVaultAvailable()) return;

            var seenIds = new HashSet<string>();
            int liveMirrored = 0;
            int rosterMirrored = 0;

            try
            {
                // 1) Mirror live companions (authoritative ZDO state)
                foreach (var companion in CompanionController.AllCompanions)
                {
                    if (companion == null) continue;
                    if (string.IsNullOrEmpty(companion.companionId)) continue;
                    if (companion.ownerPlayerId == 0) continue;

                    try
                    {
                        var saveData = BuildSaveData(companion);
                        if (saveData == null || !ValidateSaveData(saveData)) continue;

                        FiresCore.Bridge.CompanionVaultBridge.Save(companion.ownerPlayerId, saveData);
                        seenIds.Add(companion.companionId);
                        liveMirrored++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CompanionVault] FlushDebugMirror live failed for {companion.companionName}: {ex.Message}");
                    }
                }

                // 2) Mirror roster entries not covered by a live companion.
                //    Only the local player's roster is reachable from
                //    m_customData; on a dedicated host, remote players'
                //    rosters are flushed by their own clients.
                var localPlayer = Player.m_localPlayer;
                if (localPlayer != null)
                {
                    rosterMirrored = MirrorRosterEntries(localPlayer, seenIds);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CompanionVault] FlushDebugMirror exception: {ex.Message}");
            }

            if (VerboseLogging && (liveMirrored > 0 || rosterMirrored > 0))
                Debug.Log($"[CompanionVault] FlushDebugMirror: {liveMirrored} live + {rosterMirrored} roster mirrored to vault");
        }

        /// <summary>
        /// Synchronously flush one player's authoritative state to the
        /// vault. Called from the logout flow so the disk image is current
        /// before the player's companions are despawned and the periodic
        /// flush stops running for this player.
        /// </summary>
        public static void FlushPlayerToVault(Player player)
        {
            if (player == null || !FiresCore.Bridge.CompanionVaultBridge.IsVaultAvailable()) return;

            long playerId = player.GetPlayerID();
            if (playerId == 0) return;

            var seenIds = new HashSet<string>();
            try
            {
                foreach (var companion in CompanionController.AllCompanions)
                {
                    if (companion == null) continue;
                    if (companion.ownerPlayerId != playerId) continue;
                    if (string.IsNullOrEmpty(companion.companionId)) continue;

                    try
                    {
                        var saveData = BuildSaveData(companion);
                        if (saveData == null || !ValidateSaveData(saveData)) continue;
                        FiresCore.Bridge.CompanionVaultBridge.Save(playerId, saveData);
                        seenIds.Add(companion.companionId);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CompanionVault] FlushPlayerToVault live failed for {companion.companionName}: {ex.Message}");
                    }
                }

                MirrorRosterEntries(player, seenIds);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CompanionVault] FlushPlayerToVault exception: {ex.Message}");
            }
        }

        private static int MirrorRosterEntries(Player player, HashSet<string> excludeIds)
        {
            if (player == null) return 0;

            var roster = PlayerCompanionStorage.GetRosterOrEmpty(player);
            if (roster?.Entries == null || roster.Entries.Count == 0) return 0;

            long playerId = player.GetPlayerID();
            int written = 0;

            foreach (var entry in roster.Entries)
            {
                if (entry == null) continue;
                if (string.IsNullOrEmpty(entry.CompanionId)) continue;
                if (excludeIds != null && excludeIds.Contains(entry.CompanionId)) continue;
                if (entry.Snapshot == null) continue;

                var snapshot = entry.Snapshot;

                // Project the wall-clock pending deadline into the snapshot's
                // seconds-remaining field so the vault dump reflects current
                // timer state rather than the at-death recorded duration.
                if (entry.IsPendingRespawn && entry.RespawnDeadlineUtcTicks > 0)
                {
                    snapshot.IsPendingRespawn = true;
                    snapshot.RespawnTimeRemaining = (float)CompanionZdoSnapshot.ComputeRemainingRespawnSeconds(entry.RespawnDeadlineUtcTicks);
                }
                else
                {
                    snapshot.IsPendingRespawn = false;
                    snapshot.RespawnTimeRemaining = 0f;
                }

                if (!ValidateSaveData(snapshot)) continue;

                try
                {
                    FiresCore.Bridge.CompanionVaultBridge.Save(playerId, snapshot);
                    written++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionVault] MirrorRosterEntries failed for {entry.CompanionId}: {ex.Message}");
                }
            }

            return written;
        }

        #endregion

        #region Load Operations

        /// <summary>
        /// Restores a companion controller from vault data.
        /// This applies all saved state to the companion.
        /// </summary>
        public static bool RestoreCompanion(CompanionController companion, CompanionSaveData saveData)
        {
            if (companion == null || saveData == null)
                return false;
            
            try
            {
                // Restore identity
                companion.companionId = saveData.CompanionId;
                companion.companionName = saveData.CompanionName ?? "Companion";
                companion.displayNameOverride = saveData.DisplayNameOverride;
                companion.ownerPlayerId = saveData.OwnerPlayerId;
                companion.isTamed = true;
                companion.isDefeated = false;
                
                // Restore to ZDO
                RestoreToZDO(companion, saveData);
                
                // Restore inventory/equipment
                RestoreInventory(companion, saveData);
                
                // Restore progression systems
                RestoreProgressionSystems(companion, saveData);
                
                // Restore appearance
                RestoreAppearance(companion, saveData);
                
                // Restore scale
                RestoreScale(companion, saveData);

                // Re-establish stationed placement / stay-mode home anchor — the dormancy/ApplyState path
                // skipped these (only the legacy respawn path restored them), so a stationed NPC lost its
                // post and a "stay and guard" companion respawned at the owner and wandered off.
                companion.RestoreStationingAndHome(saveData);

                // CRITICAL: the Restore* helpers above load equipment/skills/stats/progression into
                // MEMORY only (RestoreToZDO wrote just identity/appearance/scale). Persist everything to
                // the ZDO now, or clients read an empty ZDO (naked, level 1) and the companion's own
                // deferred LoadFromZDO (~1s later) reads those empty fields back and wipes the restore.
                // On a dedicated server this now actually writes — the teleport-suppression gate no
                // longer blocks server-side saves (see AreCompanionTeleportsSuppressed).
                companion.PersistAllToZDO();

                Debug.Log($"[CompanionVault] Restored {companion.companionName} from vault and persisted to ZDO");

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CompanionVault] Exception restoring {saveData.CompanionName}: {ex.Message}");
                return false;
            }
        }
        
        #endregion

        #region Build Save Data

        /// <summary>
        /// Builds a CompanionSaveData from a CompanionController.
        /// This is the central method for collecting all companion data.
        /// </summary>
        public static CompanionSaveData BuildSaveData(CompanionController companion)
        {
            if (companion == null) return null;
            
            var saveData = new CompanionSaveData
            {
                CompanionId = companion.companionId,
                CompanionName = companion.companionName,
                DisplayNameOverride = companion.displayNameOverride,
                PrefabName = GetPrefabName(companion),
                OwnerPlayerId = companion.ownerPlayerId,
                // Persistent owner intent - NOT the runtime AI flag.  The runtime
                // flag flips off transiently for many reasons (owner not loaded,
                // glitch recovery, mid-teleport) and writing those values to vault
                // caused companions to forget they were following across logout.
                IsFollowing = companion.GetPersistentFollowIntent(),
                RealmId = CompanionController.GetCurrentRealmId()
            };
            
            // NPC Module state
            BuildNpcModuleData(companion, saveData);

            // Stay-mode home anchor (so a Stay companion that dies respawns
            // at their last commanded spot, not next to the owner).
            BuildHomePositionData(companion, saveData);

            // Appearance data
            BuildAppearanceData(companion, saveData);
            
            // Scale data
            BuildScaleData(companion, saveData);
            
            // Equipment data
            BuildEquipmentData(companion, saveData);
            
            // Progression systems
            BuildProgressionData(companion, saveData);
            
            return saveData;
        }
        
        private static void BuildNpcModuleData(CompanionController companion, CompanionSaveData saveData)
        {
            var npcModule = companion.GetComponent<CompanionNpcModule>();
            if (npcModule != null && npcModule.IsStationedAsNpc)
            {
                saveData.IsStationedAsNpc = true;
                var stationedPos = npcModule.StationedPosition;
                saveData.StationedPositionX = stationedPos.x;
                saveData.StationedPositionY = stationedPos.y;
                saveData.StationedPositionZ = stationedPos.z;
                saveData.StationedRotationY = companion.transform.rotation.eulerAngles.y;
                saveData.AllowIdleWandering = npcModule.allowIdleWandering;
                
                // Stationed NPCs are not following
                saveData.IsFollowing = false;
            }
        }
        
        /// <summary>
        /// Captures the home-position anchor for companions in Stay mode.
        /// Read by <see cref="CompanionRespawnManager"/> at respawn time so
        /// a companion that died while Staying spawns at their last commanded
        /// spot instead of being yanked back to the owner. Skipped for
        /// stationed-as-NPC companions (their <c>StationedPosition</c> fields
        /// already serve the same purpose).
        /// </summary>
        private static void BuildHomePositionData(CompanionController companion, CompanionSaveData saveData)
        {
            if (saveData.IsStationedAsNpc) return;

            var idleBehavior = companion.GetComponent<CompanionIdleBehavior>();
            if (idleBehavior == null || !idleBehavior.HasHomePosition) return;

            var homePos = idleBehavior.GetHomePosition();
            saveData.HasHomePosition = true;
            saveData.HomePositionX = homePos.x;
            saveData.HomePositionY = homePos.y;
            saveData.HomePositionZ = homePos.z;
        }

        private static void BuildAppearanceData(CompanionController companion, CompanionSaveData saveData)
        {
            var nview = companion.GetComponent<ZNetView>();
            var zdo = nview?.GetZDO();
            
            if (zdo == null) return;
            
            string hairStyle = zdo.GetString("companion_hair", "");
            bool isFemale = zdo.GetBool("companion_isfemale", false);
            
            if (!string.IsNullOrEmpty(hairStyle) || isFemale)
            {
                saveData.HasAppearanceData = true;
                saveData.ModelIndex = isFemale ? 1 : 0;
                saveData.HairStyle = hairStyle;
                saveData.BeardStyle = zdo.GetString("companion_beard", "");
                
                var hairColor = zdo.GetVec3(ZDOVars.s_hairColor, Vector3.one);
                saveData.HairColorR = hairColor.x;
                saveData.HairColorG = hairColor.y;
                saveData.HairColorB = hairColor.z;
                
                var skinColor = zdo.GetVec3(ZDOVars.s_skinColor, new Vector3(1f, 0.8f, 0.7f));
                saveData.SkinColorR = skinColor.x;
                saveData.SkinColorG = skinColor.y;
                saveData.SkinColorB = skinColor.z;
                
                var eyeColor = zdo.GetVec3("companion_eyeColor", new Vector3(0.3f, 0.3f, 0.3f));
                saveData.EyeColorR = eyeColor.x;
                saveData.EyeColorG = eyeColor.y;
                saveData.EyeColorB = eyeColor.z;
            }
        }
        
        private static void BuildScaleData(CompanionController companion, CompanionSaveData saveData)
        {
            // Primary source: CompanionRandomLoadout component
            var randomLoadout = companion.GetComponent<CompanionRandomLoadout>();
            if (randomLoadout != null)
            {
                float scale = randomLoadout.GetScale();
                bool isGiant = randomLoadout.IsGiant();
                bool isDwarf = randomLoadout.IsDwarf();
                
                if (Mathf.Abs(scale - 1.0f) > 0.01f || isGiant || isDwarf)
                {
                    saveData.Scale = scale;
                    saveData.IsGiant = isGiant;
                    saveData.IsDwarf = isDwarf;
                    
                    if (VerboseLogging)
                        Debug.Log($"[CompanionVault] BuildScaleData: {companion.companionName} scale={scale:F2}, giant={isGiant}, dwarf={isDwarf}");
                }
            }
            else
            {
                // Fallback: try ZDO
                var nview = companion.GetComponent<ZNetView>();
                var zdo = nview?.GetZDO();
                if (zdo != null)
                {
                    float zdoScale = zdo.GetFloat("companion_scale", 1.0f);
                    bool zdoIsGiant = zdo.GetBool("companion_isgiant", false);
                    bool zdoIsDwarf = zdo.GetBool("companion_isdwarf", false);
                    
                    if (Mathf.Abs(zdoScale - 1.0f) > 0.01f || zdoIsGiant || zdoIsDwarf)
                    {
                        saveData.Scale = zdoScale;
                        saveData.IsGiant = zdoIsGiant;
                        saveData.IsDwarf = zdoIsDwarf;
                    }
                }
            }
        }
        
        private static void BuildEquipmentData(CompanionController companion, CompanionSaveData saveData)
        {
            var inventory = companion.GetInventory();
            if (inventory == null) return;
            
            var allEquipment = inventory.GetAllEquipmentForVault();
            foreach (var kvp in allEquipment)
            {
                var slotStr = kvp.Key.ToString();
                saveData.EquipmentPrefabs[slotStr] = kvp.Value.prefab;
                saveData.EquipmentQualities[slotStr] = kvp.Value.quality;
                saveData.EquipmentStacks[slotStr] = kvp.Value.stack;
            }
            
            // Storage inventory
            var storageInv = inventory.GetStorageInventory();
            if (storageInv != null)
            {
                try
                {
                    var pkg = new ZPackage();
                    storageInv.Save(pkg);
                    saveData.StorageInventoryData = pkg.GetBase64();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionVault] Failed to save storage inventory: {ex.Message}");
                }
            }
        }
        
        private static void BuildProgressionData(CompanionController companion, CompanionSaveData saveData)
        {
            // Skills
            var skills = companion.GetSkills();
            if (skills != null)
            {
                saveData.SkillsData = skills.GetSkillsDataForVault();
            }
            
            // Progression
            var progression = companion.GetProgression();
            if (progression != null)
            {
                saveData.ProgressionData = progression.GetProgressionDataForVault();
            }
            
            // Stats
            var stats = companion.GetStats();
            if (stats != null)
            {
                saveData.StatsData = stats.GetStatsDataForVault();
            }
            
            // Kill tracker
            var killTracker = companion.GetKillTracker();
            if (killTracker != null)
            {
                saveData.KillsData = killTracker.GetKillsDataForVault();
            }

            // Archetype skills (separate component; owner-gated ZDO save can't run server-side)
            var archetypeSkills = companion.GetComponent<FiresCore.Npc.Archetypes.ArchetypeSkillSystem>();
            if (archetypeSkills != null)
            {
                saveData.ArchetypeSkillsData = archetypeSkills.GetSkillsDataForVault();
            }

            // Luck
            var luck = companion.GetComponent<CompanionLuck>();
            if (luck != null)
            {
                saveData.LuckData = luck.GetLuckDataForVault();
            }
        }
        
        #endregion
        
        #region Restore Helpers
        
        private static void RestoreToZDO(CompanionController companion, CompanionSaveData saveData)
        {
            var nview = companion.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            
            var zdo = nview.GetZDO();
            if (zdo == null) return;
            
            // Core companion data
            zdo.Set(ZDOVars.s_tamed, true);
            zdo.Set("companion_tamed", true);
            zdo.Set("companion_id", saveData.CompanionId);
            zdo.Set("companion_owner", saveData.OwnerPlayerId);
            zdo.Set("companion_name", saveData.CompanionName ?? "Companion");
            zdo.Set("companion_displayname", saveData.DisplayNameOverride ?? "");
            zdo.Set(ZDOVars.s_tamedName, saveData.CompanionName ?? "Companion");
            zdo.Set("companion_wasfollowing", saveData.IsFollowing);
            
            // Appearance data
            if (saveData.HasAppearanceData)
            {
                zdo.Set("companion_isfemale", saveData.ModelIndex == 1);
                zdo.Set("companion_hair", saveData.HairStyle ?? "");
                zdo.Set("companion_beard", saveData.BeardStyle ?? "");
                zdo.Set(ZDOVars.s_hairColor, new Vector3(saveData.HairColorR, saveData.HairColorG, saveData.HairColorB));
                zdo.Set(ZDOVars.s_skinColor, new Vector3(saveData.SkinColorR, saveData.SkinColorG, saveData.SkinColorB));
                zdo.Set("companion_eyeColor", new Vector3(saveData.EyeColorR, saveData.EyeColorG, saveData.EyeColorB));
                zdo.Set(ZDOVars.s_modelIndex, saveData.ModelIndex);
            }
            
            // Scale data
            if (Mathf.Abs(saveData.Scale - 1.0f) > 0.01f || saveData.IsGiant || saveData.IsDwarf)
            {
                zdo.Set("companion_scale", saveData.Scale);
                zdo.Set("companion_isgiant", saveData.IsGiant);
                zdo.Set("companion_isdwarf", saveData.IsDwarf);
            }
        }
        
        private static void RestoreInventory(CompanionController companion, CompanionSaveData saveData)
        {
            var inventory = companion.GetInventory();
            if (inventory == null) return;
            
            // Restore equipment
            if (saveData.EquipmentPrefabs != null)
            {
                foreach (var kvp in saveData.EquipmentPrefabs)
                {
                    try
                    {
                        var slot = (CompanionInventory.EquipmentSlot)Enum.Parse(
                            typeof(CompanionInventory.EquipmentSlot), kvp.Key);
                        var quality = saveData.EquipmentQualities != null && saveData.EquipmentQualities.ContainsKey(kvp.Key)
                            ? saveData.EquipmentQualities[kvp.Key] : 1;
                        var stack = saveData.EquipmentStacks != null && saveData.EquipmentStacks.ContainsKey(kvp.Key)
                            ? saveData.EquipmentStacks[kvp.Key] : 1;

                        inventory.RestoreEquipmentFromVault(slot, kvp.Value, quality, stack);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CompanionVault] Failed to restore equipment slot {kvp.Key}: {ex.Message}");
                    }
                }
            }
            
            // Restore storage inventory
            if (!string.IsNullOrEmpty(saveData.StorageInventoryData))
            {
                try
                {
                    var storageInv = inventory.GetStorageInventory();
                    if (storageInv != null)
                    {
                        var pkg = new ZPackage(saveData.StorageInventoryData);
                        storageInv.Load(pkg);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionVault] Failed to restore storage inventory: {ex.Message}");
                }
            }
            
            // Apply visual equipment
            inventory.RecalculateEquipmentBonusesPublic();
        }
        
        private static void RestoreProgressionSystems(CompanionController companion, CompanionSaveData saveData)
        {
            // Skills
            if (!string.IsNullOrEmpty(saveData.SkillsData))
            {
                var skills = companion.GetSkills();
                skills?.RestoreSkillsFromVault(saveData.SkillsData);
            }
            
            // Progression
            if (!string.IsNullOrEmpty(saveData.ProgressionData))
            {
                var progression = companion.GetProgression();
                progression?.RestoreProgressionFromVault(saveData.ProgressionData);
            }
            
            // Stats
            if (!string.IsNullOrEmpty(saveData.StatsData))
            {
                var stats = companion.GetStats();
                stats?.RestoreStatsFromVault(saveData.StatsData);
            }
            
            // Kill tracker
            if (!string.IsNullOrEmpty(saveData.KillsData))
            {
                var killTracker = companion.GetKillTracker();
                killTracker?.RestoreKillsFromVault(saveData.KillsData);
            }
            
            // Luck
            if (!string.IsNullOrEmpty(saveData.LuckData))
            {
                var luck = companion.GetComponent<CompanionLuck>();
                luck?.RestoreLuckFromVault(saveData.LuckData);
            }

            // Archetype skills
            if (!string.IsNullOrEmpty(saveData.ArchetypeSkillsData))
            {
                var archetypeSkills = companion.GetComponent<FiresCore.Npc.Archetypes.ArchetypeSkillSystem>();
                archetypeSkills?.RestoreSkillsFromVault(saveData.ArchetypeSkillsData);
            }
        }
        
        private static void RestoreAppearance(CompanionController companion, CompanionSaveData saveData)
        {
            if (!saveData.HasAppearanceData) return;
            
            var randomLoadout = companion.GetComponent<CompanionRandomLoadout>();
            if (randomLoadout != null)
            {
                // Invoke with delay to ensure ZDO is populated first
                randomLoadout.Invoke("RestoreModelStateFromZDO", 0.2f);
            }
        }
        
        private static void RestoreScale(CompanionController companion, CompanionSaveData saveData)
        {
            if (Mathf.Abs(saveData.Scale - 1.0f) < 0.01f && !saveData.IsGiant && !saveData.IsDwarf)
                return;
            
            var randomLoadout = companion.GetComponent<CompanionRandomLoadout>();
            if (randomLoadout != null)
            {
                // Use ApplyScale with skipHealthAdjustment since health comes from vault
                randomLoadout.ApplyScale(saveData.Scale, skipHealthAdjustment: true);
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionVault] Applied scale {saveData.Scale:F2} to {companion.companionName}");
            }
            else
            {
                // Fallback: direct scale application
                ApplyScaleFallback(companion.gameObject, saveData.Scale);
            }
        }
        
        private static void ApplyScaleFallback(GameObject obj, float scale)
        {
            obj.transform.localScale = new Vector3(scale, scale, scale);
            
            var capsule = obj.GetComponent<CapsuleCollider>();
            if (capsule != null)
            {
                float baseHeight = 1.8f;
                float baseRadius = 0.3f;
                float baseCenter = 0.9f;
                
                capsule.height = baseHeight * scale;
                capsule.radius = baseRadius * scale;
                capsule.center = new Vector3(0, baseCenter * scale, 0);
            }
            
            var rigidbody = obj.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                float baseMass = 50f;
                float massMultiplier = Mathf.Pow(scale, 2);
                rigidbody.mass = baseMass * massMultiplier;
            }
        }
        
        #endregion
        
        #region Utility Methods

        private static bool ValidateSaveData(CompanionSaveData saveData)
        {
            if (saveData == null) return false;
            if (string.IsNullOrEmpty(saveData.CompanionId)) return false;
            if (saveData.OwnerPlayerId == 0) return false;
            return true;
        }

        private static string GetPrefabName(CompanionController companion)
        {
            var nview = companion.GetComponent<ZNetView>();
            if (nview?.GetZDO() != null)
            {
                var prefabHash = nview.GetZDO().GetPrefab();
                var prefab = ZNetScene.instance?.GetPrefab(prefabHash);
                if (prefab != null)
                    return prefab.name;
            }
            return "CompanionNpc";
        }

        #endregion
    }
}
