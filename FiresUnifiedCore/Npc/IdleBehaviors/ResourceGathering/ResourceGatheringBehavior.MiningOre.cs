using UnityEngine;
using System.Collections;
using System.Collections.Generic;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Ore detection, mining logic, and attack mechanics.
    /// </summary>
    public partial class ResourceGatheringBehavior
    {
        #region Ore Detection Prefabs
        
        private static readonly Dictionary<string, int> OreDepositPrefabs = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "MineRock_Copper", 0 },
            { "MineRock_Stone", 0 },
            { "MineRock_Iron", 1 },
            { "rock4_copper_frac", 2 },
            { "mudpile_frac", 3 },
            { "mudpile2_frac", 3 },
            { "mudpile_old", 0 },
            { "MineRock_Meteorite", 3 },
            { "LeviathanLava", 3 },
            { "FlametalRockstand_frac", 0 },
            { "gold_Rock_frac_bal", 3 },
            { "rock_nickel_frac_bal", 3 },
            { "rock_nickel_nomoss_frac_bal", 3 },
            { "iceLump_frac_bal", 3 },
            { "silvervein_frac", 4 },
            { "rock3_silver_frac", 4 },
            { "rock_silver_small_frac_bal", 4 },
            { "iceblock_rock_frac_bal", 4 },
            { "coal_rock1_frac_bal", 1 },
            { "coal_rock_nomoss_frac_bal", 2 },
        };
        
        private static readonly string[] OreNamePatterns = new string[]
        {
            "copper", "tin", "iron", "silver", "obsidian", "blackmetal", "flametal",
            "ore", "mudpile", "coal", "nickel", "gold"
        };
        
        #endregion
        
        /// <summary>
        /// Finds a nearby mineable ore deposit.
        /// </summary>
        private GameObject FindNearbyOreDeposit(Vector3 position, float radius)
        {
            var colliders = Physics.OverlapSphere(position, radius);
            float closestDist = float.MaxValue;
            GameObject closest = null;
            var processed = new HashSet<GameObject>();
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var mineRock = col.GetComponent<MineRock>() ?? col.GetComponentInParent<MineRock>();
                var mineRock5 = col.GetComponent<MineRock5>() ?? col.GetComponentInParent<MineRock5>();
                
                GameObject target = null;
                if (mineRock != null) target = mineRock.gameObject;
                else if (mineRock5 != null) target = mineRock5.gameObject;
                
                if (target == null) continue;
                if (processed.Contains(target)) continue;
                processed.Add(target);
                
                string prefabName = target.name;
                if (prefabName.EndsWith("(Clone)"))
                    prefabName = prefabName.Substring(0, prefabName.Length - 7).Trim();
                
                bool isOreDeposit = false;
                
                if (OreDepositPrefabs.ContainsKey(prefabName))
                {
                    isOreDeposit = true;
                }
                else
                {
                    string nameLower = prefabName.ToLowerInvariant();
                    foreach (string pattern in OreNamePatterns)
                    {
                        if (nameLower.Contains(pattern))
                        {
                            isOreDeposit = true;
                            break;
                        }
                    }
                }
                
                if (isOreDeposit)
                {
                    float dist = Vector3.Distance(position, target.transform.position);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closest = target;
                    }
                }
            }
            
            return closest;
        }
        
        private bool UpdateAttacking()
        {
            // CRITICAL: Check if current target is a stump BEFORE checking if it's destroyed
            // We need to save the stump info while the GameObject still exists
            if (_targetResource != null && _targetResource.GameObject != null && !_wasTargetingStump)
            {
                if (IsStump(_targetResource))
                {
                    _wasTargetingStump = true;
                    _targetStumpName = _targetResource.GameObject.name;
                    _targetStumpPosition = _targetResource.InteractionPosition;
                    
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion?.companionName} targeting stump: {_targetStumpName} at {_targetStumpPosition}");
                }
            }
            
            if (_targetResource == null || _targetResource.GameObject == null)
            {
                // Resource was destroyed - check if it was a stump
                if (_wasTargetingStump)
                {
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion?.companionName} destroyed stump: {_targetStumpName}");
                    
                    OnStumpDestroyed(_targetStumpPosition, _targetStumpName);
                    _wasTargetingStump = false;
                    _targetStumpName = null;
                    
                    // Stump destroyed - check for more stumps or logs nearby
                    _resourcesGathered++;
                    SetPhase(GatherPhase.WaitingForLogs);
                    return false;
                }
                
                if (_wasTargetingTree)
                {
                    if (VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion.companionName} tree destroyed, waiting {LOG_CHECK_WAIT}s before checking for logs");
                    
                    _resourcesGathered++;
                    _aoeDamageAttempts = 0;
                    SetPhase(GatherPhase.WaitingForLogs);
                    return false;
                }
                
                _resourcesGathered++;
                SetPhase(GatherPhase.WaitingForDrops);
                return false;
            }
            
            if (_targetResource.Destructible == null)
            {
                // Destructible component is gone but GameObject still exists (rare case)
                if (_wasTargetingStump)
                {
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion?.companionName} stump destructible gone: {_targetStumpName}");
                    
                    OnStumpDestroyed(_targetStumpPosition, _targetStumpName);
                    _wasTargetingStump = false;
                    _targetStumpName = null;
                    
                    _resourcesGathered++;
                    SetPhase(GatherPhase.WaitingForLogs);
                    return false;
                }
                
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} destructible gone for {_targetResource.Name}");
                
                if (_wasTargetingTree)
                {
                    if (VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion.companionName} tree destructible gone, waiting {LOG_CHECK_WAIT}s before checking for logs");
                    
                    _resourcesGathered++;
                    _aoeDamageAttempts = 0;
                    SetPhase(GatherPhase.WaitingForLogs);
                    return false;
                }
                
                _resourcesGathered++;
                SetPhase(GatherPhase.WaitingForDrops);
                return false;
            }
            
            // Check if this is an unreachable log (stuck in air on another tree)
            bool useAOEDamage = _targetResource.TreeLog != null && IsLogUnreachable(_targetResource);
            
            if (_consecutiveNoColliderHits >= MAX_NO_COLLIDER_BEFORE_REPOSITION && !useAOEDamage)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} repositioning after {_consecutiveNoColliderHits} failed hits");
                
                _consecutiveNoColliderHits = 0;
                SetPhase(GatherPhase.Repositioning);
                return false;
            }
            
            // CRITICAL: Don't move or rotate while attack animation is playing!
            // Check if the character is currently in an attack animation
            bool isAttacking = _character != null && _character.InAttack();
            
            if (!isAttacking)
            {
                if (useAOEDamage)
                {
                    // For AOE damage, get as close as possible but don't need to reach the exact spot
                    float dist = Vector3.Distance(Transform.position, _targetResource.InteractionPosition);
                    if (dist > AOE_DAMAGE_RADIUS * 2f)
                    {
                        // Move closer but don't try to reach the exact position
                        Vector3 dirToLog = (_targetResource.InteractionPosition - Transform.position).normalized;
                        dirToLog.y = 0; // Keep horizontal
                        Vector3 approachPoint = Transform.position + dirToLog * 2f;
                        MoveToPosition(approachPoint);
                    }
                    else
                    {
                        StopMovement();
                    }
                }
                else
                {
                    // Normal movement - try to reach the target
                    float dist = Vector3.Distance(Transform.position, _targetResource.InteractionPosition);
                    if (dist > ATTACK_RANGE)
                    {
                        MoveToPosition(_targetResource.InteractionPosition);
                    }
                    else
                    {
                        StopMovement();
                    }
                }
                
                // Only face target when NOT attacking to prevent turning mid-swing
                FaceTarget(_targetResource.InteractionPosition);
            }
            else
            {
                // During attack, ensure movement is stopped (no sliding)
                StopMovement();
            }
            
            if (Time.time - _lastAttackTime >= ATTACK_INTERVAL)
            {
                if (useAOEDamage)
                {
                    // Use AOE damage for unreachable logs
                    var weapon = GetEquippedWeaponOrTool();
                    PlayAttackAnimation(weapon);
                    Companion.StartCoroutine(ApplyAOEDamageDelayed(weapon, DAMAGE_DELAY));
                }
                else
                {
                    // Normal direct attack
                    AttackResource();
                }
                _lastAttackTime = Time.time;
            }
            
            if (Time.time - _phaseStartTime > 60f)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} attack phase timeout");
                _aoeDamageAttempts = 0;
                SetPhase(GatherPhase.WaitingForDrops);
            }
            
            return false;
        }
        
        /// <summary>
        /// Coroutine to apply AOE damage after animation delay.
        /// </summary>
        private System.Collections.IEnumerator ApplyAOEDamageDelayed(ItemDrop.ItemData weapon, float delay)
        {
            yield return new WaitForSeconds(delay);
            
            if (_targetResource == null || _targetResource.GameObject == null)
            {
                yield break;
            }
            
            ApplyAOEDamageToLog(weapon);
        }
        
        private void AttackResource()
        {
            if (_targetResource == null || _targetResource.GameObject == null || _humanoid == null) return;
            
            var weapon = GetEquippedWeaponOrTool();
            
            if (_targetResource.RequiresCombat && _targetResource.RequiredTool != ResourceDataHelper.ToolType.None)
            {
                bool hasRightTool = ResourceDataHelper.IsToolAppropriate(weapon, _targetResource.RequiredTool, _targetResource.MinToolTier);
                if (!hasRightTool)
                {
                    if (VerboseLogging)
                    {
                        string weaponName = weapon?.m_shared?.m_name ?? "nothing";
                        Debug.Log($"[ResourceGathering] {Companion.companionName} has {weaponName} but needs {_targetResource.RequiredTool} tier {_targetResource.MinToolTier}");
                    }
                    
                    if (TryEquipToolFromStorage(_targetResource.RequiredTool, _targetResource.MinToolTier))
                    {
                        weapon = GetEquippedWeaponOrTool();
                        if (VerboseLogging)
                            Debug.Log($"[ResourceGathering] {Companion.companionName} equipped {weapon?.m_shared?.m_name} from storage");
                    }
                    else
                    {
                        if (VerboseLogging)
                            Debug.Log($"[ResourceGathering] {Companion.companionName} has no appropriate tool for {_targetResource.Name} - stopping");
                        
                        var owner = Companion.GetOwner();
                        if (owner != null && owner == Player.m_localPlayer)
                        {
                            string toolName = _targetResource.RequiredTool == ResourceDataHelper.ToolType.Pickaxe ? "pickaxe" : "axe";
                            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, 
                                $"{Companion.GetDisplayName()} needs a {toolName} to gather {_targetResource.Name}");
                        }
                        
                        SetPhase(GatherPhase.Complete);
                        return;
                    }
                }
            }
            
            PlayAttackAnimation(weapon);
            
            Companion.StartCoroutine(ApplyDamageDelayed(weapon, DAMAGE_DELAY));
        }
        
        private IEnumerator ApplyDamageDelayed(ItemDrop.ItemData weapon, float delay)
        {
            yield return new WaitForSeconds(delay);
            
            if (_targetResource == null || _targetResource.GameObject == null || _targetResource.Destructible == null)
            {
                yield break;
            }
            
            Collider hitCollider = null;
            Vector3 hitPoint = _targetResource.InteractionPosition;
            Vector3 hitDir = (hitPoint - Transform.position).normalized;
            
            Vector3 rayOrigin = Transform.position + Vector3.up * 1.0f;
            Vector3 rayDir = (_targetResource.InteractionPosition - rayOrigin).normalized;
            float rayDistance = Vector3.Distance(rayOrigin, _targetResource.InteractionPosition) + 1f;
            
            int hitMask = LayerMask.GetMask("piece", "Default", "static_solid", "Default_small", "terrain", "piece_nonsolid");
            
            RaycastHit[] hits = Physics.RaycastAll(rayOrigin, rayDir, rayDistance, hitMask);
            foreach (var hit in hits)
            {
                if (hit.collider != null)
                {
                    if (hit.collider.transform == _targetResource.GameObject.transform || 
                        hit.collider.transform.IsChildOf(_targetResource.GameObject.transform) ||
                        _targetResource.GameObject.transform.IsChildOf(hit.collider.transform))
                    {
                        hitCollider = hit.collider;
                        hitPoint = hit.point;
                        break;
                    }
                    
                    var parentDestructible = hit.collider.GetComponentInParent<IDestructible>();
                    if (parentDestructible != null && 
                        parentDestructible == _targetResource.Destructible)
                    {
                        hitCollider = hit.collider;
                        hitPoint = hit.point;
                        break;
                    }
                }
            }
            
            if (hitCollider == null)
            {
                Collider[] colliders = Physics.OverlapSphere(_targetResource.InteractionPosition, 0.5f, hitMask);
                foreach (var col in colliders)
                {
                    if (col.transform == _targetResource.GameObject.transform || 
                        col.transform.IsChildOf(_targetResource.GameObject.transform) ||
                        _targetResource.GameObject.transform.IsChildOf(col.transform))
                    {
                        hitCollider = col;
                        hitPoint = col.bounds.center;
                        break;
                    }
                }
            }
            
            if (hitCollider == null)
            {
                _consecutiveNoColliderHits++;
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} hit {_targetResource.Name} with no collider (count: {_consecutiveNoColliderHits})");
            }
            else
            {
                _consecutiveNoColliderHits = 0;
            }
            
            var hitData = ResourceDataHelper.CreateResourceHitData(
                _targetResource,
                _character,
                weapon,
                hitPoint,
                hitDir,
                hitCollider
            );
            
            _targetResource.Destructible?.Damage(hitData);
            
            if (hitCollider == null && _targetResource.MineRock5 != null)
            {
                ApplyFallbackMineRock5Damage(_targetResource.MineRock5, hitData);
            }
            
            if (hitCollider == null && _targetResource.MineRock != null)
            {
                ApplyFallbackMineRockDamage(_targetResource.MineRock, hitData);
            }
            
            var skills = Companion.GetSkills();
            if (skills != null && weapon != null)
            {
                skills.RaiseSkill(weapon.m_shared.m_skillType, 1f);
            }
            
            if (VerboseLogging)
            {
                string weaponName = weapon?.m_shared?.m_name ?? "unarmed";
                string colliderInfo = hitCollider != null ? hitCollider.name : "NO COLLIDER";
                int toolTier = weapon?.m_shared?.m_toolTier ?? 0;
                Debug.Log($"[ResourceGathering] {Companion.companionName} hit {_targetResource.Name} with {weaponName} (tier {toolTier}, collider: {colliderInfo})");
            }
        }
        
        private void PlayAttackAnimation(ItemDrop.ItemData weapon)
        {
            if (_zanim == null && _animator == null) return;
            
            string trigger = "unarmed_attack0";
            
            if (weapon != null)
            {
                string weaponName = weapon.m_shared.m_name?.ToLowerInvariant() ?? "";
                string prefabName = weapon.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
                
                if (weaponName.Contains("pickaxe") || prefabName.Contains("pickaxe"))
                {
                    trigger = "swing_pickaxe";
                }
                else if (weaponName.Contains("axe") || prefabName.Contains("axe"))
                {
                    int comboIndex = Random.Range(0, 3);
                    trigger = $"swing_axe{comboIndex}";
                }
            }
            
            if (VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion.companionName} playing animation: {trigger}");
            
            if (_zanim != null)
            {
                _zanim.SetTrigger(trigger);
            }
            else if (_animator != null)
            {
                bool hasParam = false;
                foreach (var param in _animator.parameters)
                {
                    if (param.name == trigger)
                    {
                        hasParam = true;
                        break;
                    }
                }
                
                if (hasParam)
                {
                    _animator.SetTrigger(trigger);
                }
                else
                {
                    if (trigger.StartsWith("swing_axe"))
                        _animator.SetTrigger("swing_axe");
                    else
                        _animator.SetTrigger("unarmed_attack0");
                }
            }
        }
        
        private void ApplyFallbackMineRock5Damage(MineRock5 rock, HitData hitData)
        {
            if (rock == null) return;
            
            var colliders = rock.GetComponentsInChildren<Collider>();
            if (colliders == null || colliders.Length == 0) return;
            
            Collider closestArea = null;
            float closestDist = float.MaxValue;
            
            foreach (var col in colliders)
            {
                if (col == null || !col.enabled || !col.gameObject.activeInHierarchy) continue;
                
                float dist = Vector3.Distance(Transform.position, col.bounds.center);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestArea = col;
                }
            }
            
            if (closestArea != null && closestDist < 5f)
            {
                hitData.m_hitCollider = closestArea;
                hitData.m_point = closestArea.bounds.center;
                hitData.m_radius = 0f;
                
                rock.Damage(hitData);
                
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] Applied fallback damage to MineRock5 area: {closestArea.name}");
            }
        }
        
        private void ApplyFallbackMineRockDamage(MineRock rock, HitData hitData)
        {
            if (rock == null) return;
            
            var colliders = rock.GetComponentsInChildren<Collider>();
            if (colliders == null || colliders.Length == 0) return;
            
            Collider closestCollider = null;
            float closestDist = float.MaxValue;
            
            foreach (var col in colliders)
            {
                if (col == null || !col.enabled) continue;
                
                float dist = Vector3.Distance(Transform.position, col.bounds.center);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestCollider = col;
                }
            }
            
            if (closestCollider != null && closestDist < 5f)
            {
                hitData.m_hitCollider = closestCollider;
                hitData.m_point = closestCollider.bounds.center;
                hitData.m_radius = 0f;
                
                rock.Damage(hitData);
                
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] Applied fallback damage to MineRock collider: {closestCollider.name}");
            }
        }
    }
}
