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
        private const float StumpSearchRadius = 15f;  // Increased from 8f - logs can roll far
        private const float SaplingSpawnChance = 0.70f; // 70% chance to spawn sapling when stump is destroyed
        
        // AOE damage fallback for unreachable logs
        private const float AoeDamageRadius = 3f;
        private const float LogUnreachableDistance = 4f; // If log is this far above/away, use AOE
        private int _aoeDamageAttempts = 0;
        private const int MaxAoeAttempts = 10;
        
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
            int axeTier = ReachableAxeTier();

            foreach (var collider in colliders)
            {
                if (collider == null) continue;

                var treeBase = collider.GetComponent<TreeBase>() ?? collider.GetComponentInParent<TreeBase>();
                var treeLog = collider.GetComponent<TreeLog>() ?? collider.GetComponentInParent<TreeLog>();

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

                // A tree no reachable axe can cut (a tier-2 birch nearest home) would block chopping for good.
                if (target == null || !ResourceDataHelper.CanChop(target, axeTier)) continue;
                
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
                closest = ResourceDataHelper.FindNearestTreeStump(position, radius);
            }
            
            if (closest == null && (VerboseLogging || CompanionIdleBehavior.VerboseLogging))
            {
                Debug.Log($"[ResourceGathering] No trees found within {radius}m of {position} (checked {colliders.Length} colliders)");
            }
            
            return closest;
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
            if (verticalDiff > LogUnreachableDistance)
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} log is {verticalDiff:F1}m above - using AOE damage");
                return true;
            }
            
            // Check if we've been trying to reach it too long (stuck on terrain)
            if (Time.time - _phaseStartTime > 10f)
            {
                float distToLog = Vector3.Distance(companionPos, logPos);
                if (distToLog > AttackRange * 1.5f)
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
            
            Vector3 aoeCenter = Transform.position + Transform.forward * 1.5f + Vector3.up * 1f;
            
            // Find all destructibles in AOE radius
            var colliders = Physics.OverlapSphere(aoeCenter, AoeDamageRadius);
            bool hitSomething = false;
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                // Check if this is our target log
                var treeLog = collider.GetComponent<TreeLog>() ?? collider.GetComponentInParent<TreeLog>();
                if (treeLog != null && treeLog == _targetResource.TreeLog)
                {
                    var hitData = CreateAoeHit(weapon, collider);
                    treeLog.Damage(hitData);
                    hitSomething = true;
                    
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion?.companionName} AOE hit {treeLog.name} for {hitData.m_damage.m_chop:F0} chop damage");
                }
                
                // Also check for generic destructibles that might be tree parts
                var destructible = collider.GetComponent<Destructible>() ?? collider.GetComponentInParent<Destructible>();
                if (destructible != null && ReferenceEquals(destructible, _targetResource.Destructible))
                {
                    destructible.Damage(CreateAoeHit(weapon, collider));
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
            if (_aoeDamageAttempts >= MaxAoeAttempts)
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} exceeded max AOE attempts ({MaxAoeAttempts}), moving on");
                
                _aoeDamageAttempts = 0;
                SetPhase(GatherPhase.WaitingForDrops);
            }
        }
        
        /// <summary>The area swing hits like a direct swing with the tool, so the log's damage modifiers apply.</summary>
        private HitData CreateAoeHit(ItemDrop.ItemData weapon, Collider collider)
        {
            Vector3 point = collider.bounds.center;
            return ResourceDataHelper.CreateResourceHitData(_targetResource, _character, weapon, point, (point - Transform.position).normalized, collider);
        }

        /// <summary>
        /// Called when a stump is destroyed - tries to spawn a sapling.
        /// </summary>
        private void OnStumpDestroyed(Vector3 stumpPosition, string stumpName)
        {
            if (Random.value > SaplingSpawnChance)
            {
                if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion?.companionName} cleared stump - no sapling (chance: {SaplingSpawnChance * 100}%)");
                return;
            }
            
            string saplingPrefab = ResourceDataHelper.GetSaplingForStump(stumpName);
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
                _combatMovement.LockMovement("WaitingForLogs", LogCheckWait + 2f);
            }
            
            StopMovement();
            
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                Vector3 vel = _rigidbody.linearVelocity;
                _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
            }
            
            FaceTarget(_lastTreePosition);
            
            if (Time.time - _phaseStartTime < LogCheckWait)
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
            var stump = ResourceDataHelper.FindNearestTreeStump(_lastTreePosition, StumpSearchRadius);
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
                Debug.Log($"[ResourceGathering] {Companion.companionName} no logs or stumps found within {LogSearchRadius}m of tree position");
            
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
            
            Collider[] colliders = Physics.OverlapSphere(position, LogSearchRadius);
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var treeLog = collider.GetComponent<TreeLog>() ?? collider.GetComponentInParent<TreeLog>();
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
                
                var destructible = collider.GetComponent<Destructible>() ?? collider.GetComponentInParent<Destructible>();
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
                Debug.Log($"[ResourceGathering] Found {results.Count} logs within {LogSearchRadius}m of {position}");
            
            return results;
        }
    }
}
