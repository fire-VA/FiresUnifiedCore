using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Tree and log detection, gathering logic.
    /// </summary>
    public partial class ResourceGatheringBehavior
    {
        // Stump clearing settings
        private const float STUMP_SEARCH_RADIUS = 15f;  // Increased from 8f - logs can roll far
        private const float SAPLING_SPAWN_CHANCE = 0.70f; // 70% chance to spawn sapling when stump is destroyed
        
        // AOE damage fallback for unreachable logs
        private const float AOE_DAMAGE_RADIUS = 3f;
        private const float LOG_UNREACHABLE_DISTANCE = 4f; // If log is this far above/away, use AOE
        private int _aoeDamageAttempts = 0;
        private const int MAX_AOE_ATTEMPTS = 10;
        
        /// <summary>
        /// Finds a nearby tree to chop.
        /// Searches for TreeBase (standing trees), TreeLog (fallen logs), and stumps.
        /// </summary>
        private GameObject FindNearbyTree(Vector3 position, float radius)
        {
            var colliders = Physics.OverlapSphere(position, radius);
            float closestDist = float.MaxValue;
            GameObject closest = null;
            var processed = new HashSet<GameObject>();
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var treeBase = col.GetComponent<TreeBase>() ?? col.GetComponentInParent<TreeBase>();
                var treeLog = col.GetComponent<TreeLog>() ?? col.GetComponentInParent<TreeLog>();
                
                GameObject target = null;
                string treeType = null;
                
                if (treeBase != null && !processed.Contains(treeBase.gameObject))
                {
                    target = treeBase.gameObject;
                    treeType = "TreeBase";
                    processed.Add(target);
                }
                else if (treeLog != null && !processed.Contains(treeLog.gameObject))
                {
                    target = treeLog.gameObject;
                    treeType = "TreeLog";
                    processed.Add(target);
                }
                
                if (target == null) continue;
                
                float dist = Vector3.Distance(position, target.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = target;
                    
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] Found {treeType}: {target.name} at distance {dist:F1}m");
                }
            }
            
            // Also check for stumps if no trees/logs found
            if (closest == null)
            {
                closest = FindNearbyStump(position, radius);
            }
            
            if (closest == null && (VerboseLogging || CompanionIdleBehavior.VerboseLogging))
            {
                Debug.Log($"[ResourceGathering] No trees found within {radius}m of {position} (checked {colliders.Length} colliders)");
            }
            
            return closest;
        }
        
        /// <summary>
        /// Finds a nearby tree stump to clear.
        /// Stumps are Destructibles with "stump" or "stub" in the name.
        /// 
        /// VALHEIM STUB NAMING CONVENTION:
        /// - Beech trees: Beech_Stub
        /// - Birch trees: Birch_Stub, Birch1_aut_Stub, Birch2_Stub, Birch2_aut_Stub
        /// - Oak trees: Oak_Stub
        /// - Pine/Fir trees: FirTree_Stub, Pinetree_01_Stub
        /// - Swamp trees: SwampTree1_Stub
        /// - Yggdrasil: YggaShoot_Stub, YggaShoot1_Stub, etc.
        /// </summary>
        private GameObject FindNearbyStump(Vector3 position, float radius)
        {
            var colliders = Physics.OverlapSphere(position, radius);
            float closestDist = float.MaxValue;
            GameObject closest = null;
            var processed = new HashSet<GameObject>();
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                // Check for Destructible stumps (most common type)
                var destructible = col.GetComponent<Destructible>() ?? col.GetComponentInParent<Destructible>();
                if (destructible != null && !processed.Contains(destructible.gameObject))
                {
                    string name = destructible.name.ToLowerInvariant();
                    // Remove (Clone) suffix if present
                    if (name.EndsWith("(clone)"))
                        name = name.Substring(0, name.Length - 7).Trim();
                    
                    // Check for stub or stump in name
                    if (name.Contains("stub") || name.Contains("stump"))
                    {
                        processed.Add(destructible.gameObject);
                        
                        float dist = Vector3.Distance(position, destructible.transform.position);
                        if (dist < closestDist)
                        {
                            closestDist = dist;
                            closest = destructible.gameObject;
                            
                            if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                                Debug.Log($"[ResourceGathering] Found stump (Destructible): {destructible.name} at distance {dist:F1}m");
                        }
                    }
                    continue;
                }
                
                // Also check for ZNetView objects with stub/stump names (some stumps might not have Destructible)
                var zview = col.GetComponent<ZNetView>() ?? col.GetComponentInParent<ZNetView>();
                if (zview != null && !processed.Contains(zview.gameObject))
                {
                    string name = zview.gameObject.name.ToLowerInvariant();
                    if (name.EndsWith("(clone)"))
                        name = name.Substring(0, name.Length - 7).Trim();
                    
                    if (name.Contains("stub") || name.Contains("stump"))
                    {
                        // Make sure it's not just a visual mesh - needs to be damageable
                        var hasDestructible = zview.GetComponent<IDestructible>() != null ||
                                             zview.GetComponentInParent<IDestructible>() != null;
                        if (hasDestructible)
                        {
                            processed.Add(zview.gameObject);
                            
                            float dist = Vector3.Distance(position, zview.transform.position);
                            if (dist < closestDist)
                            {
                                closestDist = dist;
                                closest = zview.gameObject;
                                
                                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                                    Debug.Log($"[ResourceGathering] Found stump (ZNetView): {zview.gameObject.name} at distance {dist:F1}m");
                            }
                        }
                    }
                }
            }
            
            return closest;
        }
        
        /// <summary>
        /// Checks if a target resource is a stump.
        /// </summary>
        private bool IsStump(ResourceDataHelper.ResourceData resource)
        {
            if (resource == null || resource.GameObject == null) return false;
            
            string name = resource.GameObject.name.ToLowerInvariant();
            return name.Contains("stump") || name.Contains("stub");
        }
        
        /// <summary>
        /// Checks if a TreeLog is unreachable (stuck in air, too far to reach).
        /// </summary>
        private bool IsLogUnreachable(ResourceDataHelper.ResourceData resource)
        {
            if (resource == null || resource.TreeLog == null) return false;
            
            Vector3 companionPos = Transform.position;
            Vector3 logPos = resource.InteractionPosition;
            
            // Check vertical distance (log stuck in air on another tree)
            float verticalDiff = logPos.y - companionPos.y;
            if (verticalDiff > LOG_UNREACHABLE_DISTANCE)
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} log is {verticalDiff:F1}m above - using AOE damage");
                return true;
            }
            
            // Check if we've been trying to reach it too long (stuck on terrain)
            if (Time.time - _phaseStartTime > 10f)
            {
                float distToLog = Vector3.Distance(companionPos, logPos);
                if (distToLog > ATTACK_RANGE * 1.5f)
                {
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion?.companionName} can't reach log after 10s (dist: {distToLog:F1}m) - using AOE damage");
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Applies AOE damage to hit unreachable logs.
        /// The companion swings their weapon and does damage in an area around them.
        /// </summary>
        private void ApplyAOEDamageToLog(ItemDrop.ItemData weapon)
        {
            if (_targetResource == null || _targetResource.TreeLog == null) return;
            
            _aoeDamageAttempts++;
            
            // Create hit data for AOE damage
            float damage = 25f; // Base damage
            if (weapon != null)
            {
                damage = weapon.GetDamage().GetTotalBlockableDamage();
                damage = Mathf.Max(damage, 25f); // Minimum damage
            }
            
            Vector3 aoeCenter = Transform.position + Transform.forward * 1.5f + Vector3.up * 1f;
            
            // Find all destructibles in AOE radius
            var colliders = Physics.OverlapSphere(aoeCenter, AOE_DAMAGE_RADIUS);
            bool hitSomething = false;
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                // Check if this is our target log
                var treeLog = col.GetComponent<TreeLog>() ?? col.GetComponentInParent<TreeLog>();
                if (treeLog != null && treeLog == _targetResource.TreeLog)
                {
                    // Create hit data
                    var hitData = new HitData
                    {
                        m_damage = { m_chop = damage, m_pickaxe = 0, m_damage = damage * 0.5f },
                        m_point = col.bounds.center,
                        m_dir = (col.bounds.center - Transform.position).normalized,
                        m_attacker = _character.GetZDOID(),
                        m_toolTier = (short)(weapon?.m_shared?.m_toolTier ?? 0),
                        m_hitCollider = col
                    };
                    
                    treeLog.Damage(hitData);
                    hitSomething = true;
                    
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion?.companionName} AOE hit {treeLog.name} for {damage:F0} chop damage");
                }
                
                // Also check for generic destructibles that might be tree parts
                var destructible = col.GetComponent<Destructible>() ?? col.GetComponentInParent<Destructible>();
                if (destructible != null && destructible == _targetResource.Destructible)
                {
                    var hitData = new HitData
                    {
                        m_damage = { m_chop = damage, m_pickaxe = 0, m_damage = damage * 0.5f },
                        m_point = col.bounds.center,
                        m_dir = (col.bounds.center - Transform.position).normalized,
                        m_attacker = _character.GetZDOID(),
                        m_toolTier = (short)(weapon?.m_shared?.m_toolTier ?? 0),
                        m_hitCollider = col
                    };
                    
                    destructible.Damage(hitData);
                    hitSomething = true;
                }
            }
            
            if (hitSomething)
            {
                // Raise woodcutting skill
                var skills = Companion?.GetSkills();
                if (skills != null)
                {
                    skills.RaiseSkill(Skills.SkillType.WoodCutting, 1f);
                }
            }
            
            // Check if we've exceeded max attempts
            if (_aoeDamageAttempts >= MAX_AOE_ATTEMPTS)
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} exceeded max AOE attempts ({MAX_AOE_ATTEMPTS}), moving on");
                
                _aoeDamageAttempts = 0;
                SetPhase(GatherPhase.WaitingForDrops);
            }
        }
        
        /// <summary>
        /// Called when a stump is destroyed - tries to spawn a sapling.
        /// </summary>
        private void OnStumpDestroyed(Vector3 stumpPosition, string stumpName)
        {
            if (Random.value > SAPLING_SPAWN_CHANCE)
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} cleared stump - no sapling (chance: {SAPLING_SPAWN_CHANCE * 100}%)");
                return;
            }
            
            // Determine sapling type based on stump name
            string saplingPrefab = DetermineSaplingPrefab(stumpName);
            if (string.IsNullOrEmpty(saplingPrefab))
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} cleared stump but no matching sapling for: {stumpName}");
                return;
            }
            
            // Spawn the sapling
            SpawnSapling(saplingPrefab, stumpPosition);
        }
        
        /// <summary>
        /// Determines which sapling prefab to spawn based on the stump name.
        /// 
        /// VALHEIM STUB NAMING CONVENTION:
        /// - Beech trees: Beech_Stub
        /// - Birch trees: Birch_Stub, Birch1_aut_Stub
        /// - Oak trees: Oak_Stub
        /// - Pine/Fir trees: FirTree_Stub, Pinetree_01_Stub
        /// - Swamp trees: SwampTree1_Stub
        /// - Yggdrasil: YggaShoot_Stub
        /// </summary>
        private string DetermineSaplingPrefab(string stumpName)
        {
            if (string.IsNullOrEmpty(stumpName)) return null;
            
            string nameLower = stumpName.ToLowerInvariant();
            
            // Remove (Clone) suffix if present
            if (nameLower.EndsWith("(clone)"))
                nameLower = nameLower.Substring(0, nameLower.Length - 7).Trim();
            
            // Beech trees (Beech_Stub)
            if (nameLower.Contains("beech"))
                return "Beech_Sapling";
            
            // Birch trees (Birch_Stub, Birch1_Stub, Birch1_aut_Stub, Birch2_Stub, Birch2_aut_Stub)
            if (nameLower.Contains("birch"))
                return "Birch_Sapling";
            
            // Oak trees (Oak_Stub)
            if (nameLower.Contains("oak"))
                return "Oak_Sapling";
            
            // Pine trees (Pinetree_01_Stub, PineTree_Stub)
            if (nameLower.Contains("pine"))
                return "FirTree_Sapling"; // Pine and Fir share the same sapling
            
            // Fir trees (FirTree_Stub)
            if (nameLower.Contains("fir"))
                return "FirTree_Sapling";
            
            // Swamp/Ancient trees (SwampTree1_Stub)
            if (nameLower.Contains("swamp") || nameLower.Contains("ancient"))
                return "Ancient_Sapling";
            
            // Yggdrasil shoots (YggaShoot_Stub, YggaShoot1_Stub, etc.)
            if (nameLower.Contains("ygga") || nameLower.Contains("yggdrasil"))
                return "YggdrasilShoot_Sapling";
            
            // Ashlands trees - no sapling available in vanilla
            if (nameLower.Contains("ashland"))
                return null;
            
            // Default fallback for generic stumps/stubs - try to match common patterns
            // If it just says "stub" or "stump" without a tree type, default to beech
            if (nameLower.Contains("stump") || nameLower.Contains("stub"))
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] Unknown stump type: {stumpName}, defaulting to Beech_Sapling");
                return "Beech_Sapling";
            }
            
            return null;
        }
        
        /// <summary>
        /// Spawns a sapling at the given position.
        /// </summary>
        private void SpawnSapling(string prefabName, Vector3 position)
        {
            if (ZNetScene.instance == null) return;
            
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.LogWarning($"[ResourceGathering] Could not find sapling prefab: {prefabName}");
                return;
            }
            
            // Adjust position to be at ground level
            Vector3 spawnPos = position;
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(position, out groundHeight))
                {
                    spawnPos.y = groundHeight;
                }
            }
            
            // Add small random offset
            spawnPos.x += Random.Range(-0.3f, 0.3f);
            spawnPos.z += Random.Range(-0.3f, 0.3f);
            
            // Spawn the sapling via ZNetScene for proper network registration
            var sapling = CompanionNetworkHelper.Spawn(prefab, spawnPos, Quaternion.identity);
            
            if (sapling != null)
            {
                Debug.Log($"[ResourceGathering] {Companion?.companionName} planted a {prefabName} where stump was cleared!");
                
                // Notify player
                var owner = Companion?.GetOwner();
                if (owner != null && owner == Player.m_localPlayer)
                {
                    string saplingName = Localization.instance?.Localize("$item_" + prefabName.ToLower().Replace("_sapling", "sapling")) ?? prefabName;
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, 
                        $"{Companion.GetDisplayName()} planted a sapling");
                }
            }
        }
        
        /// <summary>
        /// Waits after felling a tree before checking for logs.
        /// Trees take time to fall and spawn their log segments.
        /// </summary>
        private bool UpdateWaitingForLogs()
        {
            if (_combatMovement != null && !_combatMovement.IsMovementLocked)
            {
                _combatMovement.LockMovement("WaitingForLogs", LOG_CHECK_WAIT + 2f);
            }
            
            StopMovement();
            
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                Vector3 vel = _rigidbody.linearVelocity;
                _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
            }
            
            FaceTarget(_lastTreePosition);
            
            if (Time.time - _phaseStartTime < LOG_CHECK_WAIT)
            {
                return false;
            }
            
            _combatMovement?.UnlockMovement();
            
            // First check for logs from the felled tree
            var allLogs = FindAllNearbyLogs(_lastTreePosition);
            if (allLogs.Count > 0)
            {
                ResourceDataHelper.ResourceData closestLog = null;
                float closestDist = float.MaxValue;
                foreach (var log in allLogs)
                {
                    float dist = Vector3.Distance(Transform.position, log.InteractionPosition);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closestLog = log;
                    }
                }
                
                if (closestLog != null)
                {
                    if (VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion.companionName} found {allLogs.Count} logs, targeting closest: {closestLog.Name} at {closestLog.InteractionPosition}");
                    
                    _targetResource = closestLog;
                    _targetPosition = closestLog.InteractionPosition;
                    _consecutiveNoColliderHits = 0;
                    _aoeDamageAttempts = 0;
                    _wasTargetingTree = true;
                    SetPhase(GatherPhase.MovingToResource);
                    MoveToPosition(_targetPosition);
                    return false;
                }
            }
            
            // Also check for stumps to clear
            var stump = FindNearbyStump(_lastTreePosition, STUMP_SEARCH_RADIUS);
            if (stump != null)
            {
                var stumpData = ResourceDataHelper.GetResourceData(stump);
                if (stumpData != null && stumpData.IsValid)
                {
                    if (VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion.companionName} found stump to clear: {stump.name}");
                    
                    _targetResource = stumpData;
                    _targetPosition = stumpData.InteractionPosition;
                    _consecutiveNoColliderHits = 0;
                    _wasTargetingTree = true;
                    SetPhase(GatherPhase.MovingToResource);
                    MoveToPosition(_targetPosition);
                    return false;
                }
            }
            
            if (VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion.companionName} no logs or stumps found within {LOG_SEARCH_RADIUS}m of tree position");
            
            SetPhase(GatherPhase.WaitingForDrops);
            return false;
        }
        
        /// <summary>
        /// Finds a TreeLog near where a tree was destroyed.
        /// </summary>
        private ResourceDataHelper.ResourceData FindNearbyLogFromTree(Vector3 treePosition)
        {
            var allLogs = FindAllNearbyLogs(treePosition);
            if (allLogs.Count == 0) return null;
            
            ResourceDataHelper.ResourceData closest = null;
            float closestDist = float.MaxValue;
            foreach (var log in allLogs)
            {
                float dist = Vector3.Distance(treePosition, log.InteractionPosition);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = log;
                }
            }
            return closest;
        }
        
        /// <summary>
        /// Finds ALL TreeLogs near a position.
        /// Used after felling a tree to process all resulting logs.
        /// </summary>
        private List<ResourceDataHelper.ResourceData> FindAllNearbyLogs(Vector3 position)
        {
            var results = new List<ResourceDataHelper.ResourceData>();
            var processed = new HashSet<GameObject>();
            
            Collider[] colliders = Physics.OverlapSphere(position, LOG_SEARCH_RADIUS);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var treeLog = col.GetComponent<TreeLog>() ?? col.GetComponentInParent<TreeLog>();
                if (treeLog != null && !processed.Contains(treeLog.gameObject))
                {
                    processed.Add(treeLog.gameObject);
                    var logData = ResourceDataHelper.GetResourceData(treeLog.gameObject);
                    if (logData != null && logData.IsValid)
                    {
                        results.Add(logData);
                    }
                    continue;
                }
                
                var destructible = col.GetComponent<Destructible>() ?? col.GetComponentInParent<Destructible>();
                if (destructible != null && destructible.name.ToLower().Contains("log") && !processed.Contains(destructible.gameObject))
                {
                    processed.Add(destructible.gameObject);
                    var logData = ResourceDataHelper.GetResourceData(destructible.gameObject);
                    if (logData != null && logData.IsValid)
                    {
                        results.Add(logData);
                    }
                }
            }
            
            if (VerboseLogging && results.Count > 0)
                Debug.Log($"[ResourceGathering] Found {results.Count} logs within {LOG_SEARCH_RADIUS}m of {position}");
            
            return results;
        }
    }
}
