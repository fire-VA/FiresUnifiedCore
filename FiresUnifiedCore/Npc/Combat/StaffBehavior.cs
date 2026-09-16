using UnityEngine;
using System.Collections;
using System.Collections.Generic;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Staff combat. Offensive staves kite like BowBehavior: retreat from the danger range, backpedal while
    /// casting when close, plant and cast at optimal range, and close the gap when too far. Support staves
    /// (shield, roots and similar) only target the owner, the owner's other companions and their tamed
    /// creatures, with healers holding near the group and away from enemies.
    /// </summary>
    public class StaffBehavior : WeaponBehavior
    {
        #region Enums
        
        public enum StaffCombatPhase
        {
            Idle,
            Approaching,
            Retreating,
            Planting,       // Stopping to cast
            Casting,        // Actively casting
            Repositioning   // Moving after a cast
        }
        
        public enum MovementRequest
        {
            None,
            Stop,
            RunAway,
            Backpedal,
            Strafe,
            Approach,
            MoveToAllies    // Special: move toward tank/group
        }
        
        #endregion
        
        #region Constants
        
        // Staff-specific distance zones
        private const float DangerRange = 4f;           // Too close! Retreat!
        private const float CloseRange = 8f;            // Uncomfortable - back up while casting
        private const float OptimalMin = 8f;            // Good range starts here
        private const float OptimalRange = 12f;         // Preferred distance from target
        private const float OptimalMax = 15f;           // Good range ends here  
        private const float MaxRange = 18f;             // Don't cast if further than this
        private const float SupportStaffRange = 15f;   // Range for buff staves
        
        // Legacy compatibility (used by ShouldAttack)
        private const float MinRange = DangerRange;    // Alias for DangerRange
        private const float RepositionThreshold = 2f;   // How close to min before repositioning
        
        // Healer-specific - stay even further back
        private const float HealerDangerRange = 6f;    // Healers flee earlier
        private const float HealerOptimalMin = 10f;    // Healers prefer more distance
        private const float HealerOptimalMax = 15f;
        
        // Timing
        private const float CastPlantDuration = 0.15f; // Quick stop before casting
        private const float RepositionDuration = 1.0f;
        private const float ThreatCheckInterval = 0.2f;

        private const float ApproachingDotThreshold = 0.3f;
        private const float ApproachMarginBeyondOptimal = 3f;
        private const float OwnerShieldPriorityBase = 200f;
        private const float OwnerShieldHealthWeight = 100f;
        private const float SelfShieldPriorityBase = 50f;
        private const float OtherCompanionShieldPriorityBase = 30f;
        private const float CompanionShieldHealthWeight = 80f;
        private const float OwnerBuffPriorityBase = 100f;
        private const float OwnerBuffHealthWeight = 50f;
        private const float FriendlyBuffHealthWeight = 80f;
        private const float CompanionBuffPriorityBonus = 20f;
        private const float EyeHeight = 1.5f;
        private const float ProjectileSpawnHeight = 1.5f;

        #endregion
        
        #region State
        
        // Support staff state
        private bool _isSupportStaff;
        private float _lastFriendlyScan;
        private const float FriendlyScanInterval = 0.5f;
        private Character _friendlyTarget;
        
        // Kiting state (mirrors BowBehavior)
        private StaffCombatPhase _currentPhase = StaffCombatPhase.Idle;
        private MovementRequest _currentMovementRequest = MovementRequest.None;
        private float _phaseStartTime;
        private float _lastThreatCheck;
        
        // Target tracking
        private float _lastKnownTargetDistance;
        private Vector3 _lastKnownTargetPosition;
        private Vector3 _lastKnownTargetVelocity;
        private bool _targetIsApproaching;
        
        // Group awareness
        private Vector3 _groupCenterPosition;
        private float _lastGroupCenterUpdate;
        private const float GroupCenterUpdateInterval = 1f;
        private const float MaxDistanceFromGroup = 25f;
        
        // Shield re-application during combat
        private float _lastShieldCheck;
        private const float ShieldCheckInterval = 3f;
        private const float ShieldReapplyCooldown = 8f;
        private float _lastShieldApplication;
        private string _cachedShieldEffectName;
        private int _cachedShieldEffectHash;
        
        #endregion
        
        #region Public Properties
        
        public StaffCombatPhase CurrentPhase => _currentPhase;
        public MovementRequest CurrentMovementNeed => _currentMovementRequest;
        public float TargetDistance => _lastKnownTargetDistance;
        public bool IsSupportStaff => _isSupportStaff;
        
        /// <summary>Event fired when movement is requested. CompanionCombatMovement subscribes to this.</summary>
        public System.Action<MovementRequest, Vector3> OnMovementRequested;
        
        #endregion
        
        #region Initialization
        
        public override void OnActivate()
        {
            base.OnActivate();
            _currentPhase = StaffCombatPhase.Idle;
            _currentMovementRequest = MovementRequest.None;
        }
        
        public override void OnDeactivate()
        {
            _currentPhase = StaffCombatPhase.Idle;
            _currentMovementRequest = MovementRequest.None;
            base.OnDeactivate();
        }
        
        public override void ConfigureAI()
        {
            // Staves are ranged weapons - configure like bows
            Context.AttackRange = MaxRange;
            
            // Detect if this is a support/protection staff
            _isSupportStaff = IsProtectionOrBuffStaff();
            
            // Cache the shield effect name for re-application checks
            if (_isSupportStaff)
            {
                CacheShieldEffectName();
            }
            
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[StaffBehavior] Configured for STAFF combat: attackRange={MaxRange}, " +
                    $"optimalRange={OptimalRange}, isSupportStaff={_isSupportStaff}, " +
                    $"shieldEffect={_cachedShieldEffectName ?? "none"}");
            }
        }
        
        #endregion
        
        #region Update Loop
        
        public override void Update()
        {
            base.Update();
            
            // Check StaminaManager - if in recovery, let CompanionCombatMovement handle retreat
            var staminaManager = Owner?.GetComponent<StaminaManager>();
            if (staminaManager != null)
            {
                if (staminaManager.IsInCriticalRecovery() || staminaManager.ShouldTreatAsLowHealth())
                {
                    SetPhase(StaffCombatPhase.Idle);
                    _currentMovementRequest = MovementRequest.None;
                    return;
                }
            }
            
            // Update group center periodically
            UpdateGroupCenter();
            
            // For support staves, handle friendly targeting
            if (_isSupportStaff)
            {
                UpdateSupportStaffBehavior();
                return;
            }
            
            // Offensive staff behavior
            var target = Context.CompanionAI?.GetTargetCreature();
            
            if (target == null || target.IsDead())
            {
                SetPhase(StaffCombatPhase.Idle);
                RequestMovement(MovementRequest.None, Vector3.zero);
                return;
            }
            
            // Update target tracking
            UpdateTargetTracking(target);
            
            // Check for threats frequently
            if (Time.time - _lastThreatCheck >= ThreatCheckInterval)
            {
                _lastThreatCheck = Time.time;
                CheckThreatLevel(target);
            }
            
            // State machine update
            UpdateCombatPhase(target);
        }
        
        private void UpdateTargetTracking(Character target)
        {
            Vector3 currentPos = target.transform.position;
            _lastKnownTargetVelocity = (currentPos - _lastKnownTargetPosition) / Mathf.Max(0.01f, Time.deltaTime);
            _lastKnownTargetPosition = currentPos;
            _lastKnownTargetDistance = Vector3.Distance(Context.Transform.position, currentPos);
            
            Vector3 toUs = (Context.Transform.position - currentPos).normalized;
            _targetIsApproaching = Vector3.Dot(_lastKnownTargetVelocity.normalized, toUs) > ApproachingDotThreshold;
        }
        
        private void UpdateGroupCenter()
        {
            if (Time.time - _lastGroupCenterUpdate < GroupCenterUpdateInterval) return;
            _lastGroupCenterUpdate = Time.time;
            
            var companion = Context.Companion;
            if (companion == null) return;
            
            var owner = companion.GetOwner();
            if (owner != null)
            {
                // Group center is near the owner
                _groupCenterPosition = owner.transform.position;
            }
        }
        
        private void CheckThreatLevel(Character target)
        {
            // Get effective danger range based on archetype
            float dangerRange = GetEffectiveDangerRange();
            float closeRange = GetEffectiveCloseRange();
            
            // DANGER ZONE - retreat!
            if (_lastKnownTargetDistance < dangerRange)
            {
                HandleDangerZone(target);
                return;
            }
            
            // Close range with approaching enemy - back up
            if (_lastKnownTargetDistance < closeRange && _targetIsApproaching)
            {
                SetPhase(StaffCombatPhase.Retreating);
                RequestMovement(MovementRequest.Backpedal, -GetDirectionToTarget(target));
            }
            
            // Check if we're too far from group
            if (_groupCenterPosition != Vector3.zero)
            {
                float distFromGroup = Vector3.Distance(Context.Transform.position, _groupCenterPosition);
                if (distFromGroup > MaxDistanceFromGroup)
                {
                    // Return to group
                    RequestMovement(MovementRequest.MoveToAllies, (_groupCenterPosition - Context.Transform.position).normalized);
                }
            }
        }
        
        private void HandleDangerZone(Character target)
        {
            if (_currentPhase != StaffCombatPhase.Retreating)
            {
                if (CompanionCombat.VerboseLogging)
                {
                    Debug.Log($"[StaffBehavior] DANGER! Enemy at {_lastKnownTargetDistance:F1}m - retreating!");
                }
                
                SetPhase(StaffCombatPhase.Retreating);
                RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
            }
        }
        
        private void UpdateCombatPhase(Character target)
        {
            float timeSincePhaseStart = Time.time - _phaseStartTime;
            float optimalMin = GetEffectiveOptimalMin();
            float optimalMax = GetEffectiveOptimalMax();
            
            switch (_currentPhase)
            {
                case StaffCombatPhase.Idle:
                    DecideNextAction(target);
                    break;
                    
                case StaffCombatPhase.Approaching:
                    UpdateApproaching(target, optimalMax);
                    break;
                    
                case StaffCombatPhase.Retreating:
                    UpdateRetreating(target, optimalMin);
                    break;
                    
                case StaffCombatPhase.Planting:
                    if (timeSincePhaseStart >= CastPlantDuration)
                    {
                        // Ready to cast - handled by ShouldAttack
                        SetPhase(StaffCombatPhase.Casting);
                    }
                    break;
                    
                case StaffCombatPhase.Casting:
                    // Casting handled by attack system
                    if (!_isAttacking)
                    {
                        DecidePostCastAction(target);
                    }
                    break;
                    
                case StaffCombatPhase.Repositioning:
                    UpdateRepositioning(target, timeSincePhaseStart, optimalMin);
                    break;
            }
        }
        
        private void DecideNextAction(Character target)
        {
            if (Context != null && Context.IsAnimationLocked) return;
            
            float dangerRange = GetEffectiveDangerRange();
            float closeRange = GetEffectiveCloseRange();
            float optimalMin = GetEffectiveOptimalMin();
            float optimalMax = GetEffectiveOptimalMax();
            
            if (_lastKnownTargetDistance > MaxRange)
            {
                // Too far - approach
                SetPhase(StaffCombatPhase.Approaching);
                RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
            }
            else if (_lastKnownTargetDistance < dangerRange)
            {
                // Too close - retreat!
                HandleDangerZone(target);
            }
            else if (_lastKnownTargetDistance < closeRange)
            {
                // Close but not danger - backpedal while casting
                SetPhase(StaffCombatPhase.Retreating);
                RequestMovement(MovementRequest.Backpedal, -GetDirectionToTarget(target));
            }
            else if (_lastKnownTargetDistance >= optimalMin && _lastKnownTargetDistance <= optimalMax)
            {
                // Perfect range - plant and cast
                SetPhase(StaffCombatPhase.Planting);
                RequestMovement(MovementRequest.Stop, Vector3.zero);
            }
            else if (_lastKnownTargetDistance > optimalMax)
            {
                // A bit far but castable - approach slightly or just cast
                if (_lastKnownTargetDistance > optimalMax + ApproachMarginBeyondOptimal)
                {
                    SetPhase(StaffCombatPhase.Approaching);
                    RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
                }
                else
                {
                    SetPhase(StaffCombatPhase.Planting);
                    RequestMovement(MovementRequest.Stop, Vector3.zero);
                }
            }
        }
        
        private void UpdateApproaching(Character target, float optimalMax)
        {
            if (_lastKnownTargetDistance <= optimalMax)
            {
                SetPhase(StaffCombatPhase.Planting);
                RequestMovement(MovementRequest.Stop, Vector3.zero);
                return;
            }
            
            // Check if approaching enemy - maybe reconsider
            if (_targetIsApproaching && _lastKnownTargetDistance < MaxRange)
            {
                // Enemy is coming to us - just stop and cast
                SetPhase(StaffCombatPhase.Planting);
                RequestMovement(MovementRequest.Stop, Vector3.zero);
                return;
            }
            
            RequestMovement(MovementRequest.Approach, GetDirectionToTarget(target));
        }
        
        private void UpdateRetreating(Character target, float optimalMin)
        {
            if (_lastKnownTargetDistance >= optimalMin)
            {
                // Safe distance achieved - can cast now
                SetPhase(StaffCombatPhase.Planting);
                RequestMovement(MovementRequest.Stop, Vector3.zero);
                return;
            }
            
            // Still too close - keep retreating
            RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
        }
        
        private void DecidePostCastAction(Character target)
        {
            float dangerRange = GetEffectiveDangerRange();
            float closeRange = GetEffectiveCloseRange();
            
            // Danger zone - retreat immediately
            if (_lastKnownTargetDistance < dangerRange)
            {
                SetPhase(StaffCombatPhase.Retreating);
                RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
                return;
            }
            
            // Close and enemy approaching - backpedal
            if (_lastKnownTargetDistance < closeRange && _targetIsApproaching)
            {
                SetPhase(StaffCombatPhase.Repositioning);
                RequestMovement(MovementRequest.Backpedal, -GetDirectionToTarget(target));
                return;
            }
            
            // Safe - cast again
            SetPhase(StaffCombatPhase.Planting);
            RequestMovement(MovementRequest.Stop, Vector3.zero);
        }
        
        private void UpdateRepositioning(Character target, float timeSinceStart, float optimalMin)
        {
            if (timeSinceStart >= RepositionDuration * 0.5f)
            {
                if (_lastKnownTargetDistance >= optimalMin)
                {
                    SetPhase(StaffCombatPhase.Planting);
                    RequestMovement(MovementRequest.Stop, Vector3.zero);
                }
                else
                {
                    SetPhase(StaffCombatPhase.Retreating);
                    RequestMovement(MovementRequest.RunAway, -GetDirectionToTarget(target));
                }
            }
        }
        
        #endregion
        
        #region Support Staff Behavior
        
        private void UpdateSupportStaffBehavior()
        {
            // Support staves (healers) have special behavior:
            // 1. Stay near allies
            // 2. Actively avoid enemies
            // 3. Target friendlies for buffs
            
            // First, check for enemy threats
            Character nearestEnemy = FindNearestEnemy();
            if (nearestEnemy != null)
            {
                float distToEnemy = Vector3.Distance(Context.Transform.position, nearestEnemy.transform.position);
                float healerDanger = HealerDangerRange;
                
                if (distToEnemy < healerDanger)
                {
                    // Enemy too close - retreat toward allies!
                    if (CompanionCombat.VerboseLogging)
                    {
                        Debug.Log($"[StaffBehavior] HEALER DANGER! Enemy {nearestEnemy.m_name} at {distToEnemy:F1}m - retreating to allies!");
                    }
                    
                    SetPhase(StaffCombatPhase.Retreating);
                    
                    // Retreat toward group center, not just away from enemy
                    Vector3 retreatDir;
                    if (_groupCenterPosition != Vector3.zero)
                    {
                        Vector3 toGroup = (_groupCenterPosition - Context.Transform.position).normalized;
                        Vector3 awayFromEnemy = (Context.Transform.position - nearestEnemy.transform.position).normalized;
                        // Blend toward group and away from enemy
                        retreatDir = (toGroup + awayFromEnemy).normalized;
                    }
                    else
                    {
                        retreatDir = (Context.Transform.position - nearestEnemy.transform.position).normalized;
                    }
                    
                    RequestMovement(MovementRequest.RunAway, retreatDir);
                    return;
                }
                else if (distToEnemy < HealerOptimalMin)
                {
                    // Enemy approaching - back up while buffing
                    SetPhase(StaffCombatPhase.Repositioning);
                    RequestMovement(MovementRequest.Backpedal, (Context.Transform.position - nearestEnemy.transform.position).normalized);
                }
            }
            
            // If safe, proceed with support behavior
            if (_currentPhase != StaffCombatPhase.Retreating)
            {
                // Find friendly to buff
                if (Time.time - _lastFriendlyScan > FriendlyScanInterval)
                {
                    _lastFriendlyScan = Time.time;
                    _friendlyTarget = FindBestFriendlyTarget();
                }
                
                // If we have a friendly target but they're far, move toward them
                if (_friendlyTarget != null && !_friendlyTarget.IsDead())
                {
                    float distToFriendly = Vector3.Distance(Context.Transform.position, _friendlyTarget.transform.position);
                    if (distToFriendly > SupportStaffRange)
                    {
                        RequestMovement(MovementRequest.MoveToAllies, (_friendlyTarget.transform.position - Context.Transform.position).normalized);
                    }
                    else
                    {
                        RequestMovement(MovementRequest.Stop, Vector3.zero);
                    }
                }
            }
        }
        
        private Character FindNearestEnemy()
        {
            Character nearest = null;
            float nearestDist = float.MaxValue;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (Context.Character != null && !BaseAI.IsEnemy(Context.Character, character)) continue;
                
                float dist = Vector3.Distance(Context.Transform.position, character.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = character;
                }
            }
            
            return nearest;
        }
        
        #endregion
        
        #region Distance Helpers
        
        /// <summary>Gets effective danger range based on archetype (healers flee earlier).</summary>
        private float GetEffectiveDangerRange()
        {
            if (_isSupportStaff) return HealerDangerRange;
            return DangerRange;
        }
        
        /// <summary>Gets effective close range based on archetype.</summary>
        private float GetEffectiveCloseRange()
        {
            if (_isSupportStaff) return HealerOptimalMin;
            return CloseRange;
        }
        
        /// <summary>Gets effective optimal minimum range.</summary>
        private float GetEffectiveOptimalMin()
        {
            if (_isSupportStaff) return HealerOptimalMin;
            return OptimalMin;
        }
        
        /// <summary>Gets effective optimal maximum range.</summary>
        private float GetEffectiveOptimalMax()
        {
            if (_isSupportStaff) return HealerOptimalMax;
            return OptimalMax;
        }
        
        /// <summary>Called by CompanionCombatMovement to get the preferred combat distance.</summary>
        public float GetPreferredRange()
        {
            if (_isSupportStaff) return (HealerOptimalMin + HealerOptimalMax) / 2f;
            return OptimalRange;
        }
        
        /// <summary>Called by CompanionCombatMovement to check if we're too close and need to back up.</summary>
        public bool ShouldBackUp(float distanceToTarget)
        {
            float minRange = _isSupportStaff ? HealerDangerRange : DangerRange;
            return distanceToTarget < minRange;
        }
        
        #endregion
        
        #region Movement Helpers
        
        private void SetPhase(StaffCombatPhase newPhase)
        {
            if (newPhase == _currentPhase) return;
            
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[StaffBehavior] Phase: {_currentPhase} -> {newPhase}");
            }
            
            _currentPhase = newPhase;
            _phaseStartTime = Time.time;
        }
        
        private void RequestMovement(MovementRequest request, Vector3 direction)
        {
            if (request == _currentMovementRequest && 
                (request == MovementRequest.None || request == MovementRequest.Stop))
            {
                return;
            }
            
            _currentMovementRequest = request;
            OnMovementRequested?.Invoke(request, direction);
        }
        
        private Vector3 GetDirectionToTarget(Character target)
        {
            if (target == null) return Context.Transform.forward;
            Vector3 dir = (target.transform.position - Context.Transform.position).normalized;
            dir.y = 0;
            return dir.normalized;
        }
        
        #endregion
        
        /// <summary>
        /// Caches the shield status effect name for efficient re-application checks.
        /// </summary>
        private void CacheShieldEffectName()
        {
            var attack = Context.CurrentAttack;
            if (attack?.m_attackProjectile != null)
            {
                var aoe = attack.m_attackProjectile.GetComponent<Aoe>();
                if (aoe != null && !string.IsNullOrEmpty(aoe.m_statusEffect))
                {
                    _cachedShieldEffectName = aoe.m_statusEffect;
                    _cachedShieldEffectHash = _cachedShieldEffectName.GetStableHashCode();
                }
            }
        }
        
        /// <summary>
        /// Detects if the current staff is a protection/buff staff that should target friendlies.
        /// </summary>
        private bool IsProtectionOrBuffStaff()
        {
            var weapon = Context.CurrentWeapon;
            if (weapon?.m_shared == null) return false;
            
            string weaponName = weapon.m_shared.m_name?.ToLowerInvariant() ?? "";
            
            // Also check the prefab name (m_dropPrefab or item name)
            string prefabName = weapon.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
            if (string.IsNullOrEmpty(prefabName))
            {
                // Try to get from shared name without the localization prefix
                prefabName = weaponName;
            }
            
            // Check for known protection/buff staves by name or prefab
            // StaffShield is the Staff of Protection
            if (prefabName.Contains("staffshield") ||
                prefabName.Contains("staff_shield") ||
                weaponName.Contains("shield") || 
                weaponName.Contains("protection") ||
                weaponName.Contains("greenroots") ||
                weaponName.Contains("gentle"))
            {
                if (CompanionCombat.VerboseLogging)
                {
                    Debug.Log($"[StaffBehavior] Detected SUPPORT staff: {prefabName} / {weaponName}");
                }
                return true;
            }
            
            // Check if the attack applies a beneficial status effect
            var attack = Context.CurrentAttack;
            if (attack?.m_attackProjectile != null)
            {
                string projName = attack.m_attackProjectile.name?.ToLowerInvariant() ?? "";
                
                // Check projectile name for buff indicators
                if (projName.Contains("shield") || projName.Contains("protection") || projName.Contains("buff"))
                {
                    if (CompanionCombat.VerboseLogging)
                    {
                        Debug.Log($"[StaffBehavior] Detected SUPPORT staff from projectile name: {projName}");
                    }
                    return true;
                }
                
                // Check the projectile's status effect
                var aoe = attack.m_attackProjectile.GetComponent<Aoe>();
                if (aoe != null && !string.IsNullOrEmpty(aoe.m_statusEffect))
                {
                    string effectName = aoe.m_statusEffect.ToLowerInvariant();
                    if (effectName.Contains("shield") || 
                        effectName.Contains("protection") ||
                        effectName.Contains("rested") ||
                        effectName.Contains("heal"))
                    {
                        if (CompanionCombat.VerboseLogging)
                        {
                            Debug.Log($"[StaffBehavior] Detected SUPPORT staff from status effect: {effectName}");
                        }
                        return true;
                    }
                }
            }
            
            return false;
        }
     
        public override bool ShouldAttack(Character target)
        {
            if (_isAttacking) return false;
            if (Time.time - _lastAttackTime < _effectiveCooldown) return false;
            if (Context.CompanionAI == null) return false;
            if (Context.Character != null && (Context.Character.IsStaggering() || !Context.Character.CanMove())) return false;
            
            // Support staves target friendlies, not enemies
            if (_isSupportStaff)
            {
                return ShouldUseSupportStaff();
            }
            
            if (target == null || target.IsDead()) return false;
            
            float distance = Vector3.Distance(Context.Transform.position, target.transform.position);
            
            // Don't attack if too far
            if (distance > MaxRange) return false;
            
            // Don't attack if too close - we should be repositioning
            if (distance < MinRange - RepositionThreshold) return false;
            
            // Check line of sight
            if (!HasLineOfSight(target)) return false;
            
            return true;
        }
        
        /// <summary>
        /// Checks if we should use a support/buff staff on a friendly target.
        /// During combat, periodically checks if party members need shields re-applied.
        /// </summary>
        private bool ShouldUseSupportStaff()
        {
            // Only actively use support staff during combat
            bool isInCombat = IsPartyInCombat();
            if (!isInCombat)
            {
                _friendlyTarget = null;
                return false;
            }
            
            // Check if we need to re-apply shields (every few seconds during combat)
            if (Time.time - _lastShieldCheck > ShieldCheckInterval)
            {
                _lastShieldCheck = Time.time;
                
                // Check if any party member needs shields re-applied
                if (PartyNeedsShields())
                {
                    // Respect the cooldown to prevent spam
                    if (Time.time - _lastShieldApplication >= ShieldReapplyCooldown)
                    {
                        _friendlyTarget = FindPartyMemberNeedingShield();
                        
                        if (_friendlyTarget != null)
                        {
                            if (CompanionCombat.VerboseLogging)
                            {
                                Debug.Log($"[StaffBehavior] Party member {_friendlyTarget.m_name} needs shield - will re-apply");
                            }
                            return true;
                        }
                    }
                }
            }
            
            // Standard friendly scan for initial application
            if (Time.time - _lastFriendlyScan > FriendlyScanInterval)
            {
                _lastFriendlyScan = Time.time;
                _friendlyTarget = FindBestFriendlyTarget();
            }
            
            if (_friendlyTarget == null || _friendlyTarget.IsDead())
            {
                _friendlyTarget = null;
                return false;
            }
            
            float distance = Vector3.Distance(Context.Transform.position, _friendlyTarget.transform.position);
            if (distance > SupportStaffRange) return false;
            
            // Check line of sight to friendly
            if (!HasLineOfSight(_friendlyTarget)) return false;
            
            return true;
        }
        
        /// <summary>
        /// Checks if the party (owner or companions) is currently in combat.
        /// </summary>
        private bool IsPartyInCombat()
        {
            var companion = Context.Companion;
            if (companion == null) return false;
            
            // Check if we have a target
            var myTarget = Context.CompanionAI?.GetTargetCreature();
            if (myTarget != null && !myTarget.IsDead())
            {
                return true;
            }
            
            // Check if owner is in combat
            var owner = companion.GetOwner();
            if (owner != null)
            {
                var ownerHumanoid = owner as Humanoid;
                if (ownerHumanoid != null && ownerHumanoid.InAttack())
                {
                    return true;
                }
            }
            
            // Check for nearby enemies
            Vector3 myPos = Context.Transform.position;
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(myPos, character.transform.position);
                if (dist <= SupportStaffRange)
                {
                    // There's an enemy nearby - we're in combat
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if any party member is missing their shield buff.
        /// </summary>
        private bool PartyNeedsShields()
        {
            if (string.IsNullOrEmpty(_cachedShieldEffectName)) return false;
            
            var companion = Context.Companion;
            if (companion == null) return false;
            
            long ownerId = companion.ownerPlayerId;
            Vector3 myPos = Context.Transform.position;
            
            // Check owner
            var owner = companion.GetOwner();
            if (owner != null && !owner.IsDead())
            {
                float dist = Vector3.Distance(myPos, owner.transform.position);
                if (dist <= SupportStaffRange && !HasShieldBuff(owner))
                {
                    return true;
                }
            }
            
            // Check self
            if (Context.Character != null && !Context.Character.IsDead() && !HasShieldBuff(Context.Character))
            {
                return true;
            }
            
            // Check other party companions
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == Context.Character) continue;
                if (character.IsPlayer()) continue;
                
                var otherCompanion = character.GetComponent<CompanionController>();
                if (otherCompanion != null && otherCompanion.ownerPlayerId == ownerId)
                {
                    float dist = Vector3.Distance(myPos, character.transform.position);
                    if (dist <= SupportStaffRange && !HasShieldBuff(character))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Finds a party member who needs the shield buff re-applied.
        /// Prioritizes: Owner without shield > Low health without shield > Any without shield
        /// </summary>
        private Character FindPartyMemberNeedingShield()
        {
            if (string.IsNullOrEmpty(_cachedShieldEffectName)) return null;
            
            var companion = Context.Companion;
            if (companion == null) return null;
            
            long ownerId = companion.ownerPlayerId;
            Vector3 myPos = Context.Transform.position;
            
            Character bestTarget = null;
            float bestScore = float.MinValue;
            
            // Check owner first - highest priority
            var owner = companion.GetOwner();
            if (owner != null && !owner.IsDead())
            {
                float dist = Vector3.Distance(myPos, owner.transform.position);
                if (dist <= SupportStaffRange && !HasShieldBuff(owner))
                {
                    float healthMissing = 1f - owner.GetHealthPercentage();
                    float score = OwnerShieldPriorityBase + (healthMissing * OwnerShieldHealthWeight); // Owner gets huge priority
                    
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTarget = owner;
                    }
                }
            }
            
            // Check self
            if (Context.Character != null && !Context.Character.IsDead() && !HasShieldBuff(Context.Character))
            {
                float healthMissing = 1f - Context.Character.GetHealthPercentage();
                float score = SelfShieldPriorityBase + (healthMissing * CompanionShieldHealthWeight);
                
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = Context.Character;
                }
            }
            
            // Check other party companions
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == Context.Character) continue;
                if (character.IsPlayer()) continue;
                
                var otherCompanion = character.GetComponent<CompanionController>();
                if (otherCompanion != null && otherCompanion.ownerPlayerId == ownerId)
                {
                    float dist = Vector3.Distance(myPos, character.transform.position);
                    if (dist <= SupportStaffRange && !HasShieldBuff(character))
                    {
                        float healthMissing = 1f - character.GetHealthPercentage();
                        float score = OtherCompanionShieldPriorityBase + (healthMissing * CompanionShieldHealthWeight);
                        
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestTarget = character;
                        }
                    }
                }
            }
            
            return bestTarget;
        }
        
        /// <summary>
        /// Checks if a character currently has the shield buff active.
        /// </summary>
        private bool HasShieldBuff(Character character)
        {
            if (character == null || _cachedShieldEffectHash == 0) return true; // Assume buffed if we can't check
            
            var seman = character.GetSEMan();
            if (seman == null) return true;
            
            return seman.HaveStatusEffect(_cachedShieldEffectHash);
        }
        
        /// <summary>
        /// Finds the best friendly target for a support staff.
        /// Prioritizes: Owner > Low health friendlies > Nearby friendlies
        /// </summary>
        private Character FindBestFriendlyTarget()
        {
            var companion = Context.Companion;
            if (companion == null) return null;
            
            long ownerId = companion.ownerPlayerId;
            Vector3 myPos = Context.Transform.position;
            
            Character bestTarget = null;
            float bestScore = float.MinValue;
            
            // Check owner player first
            var owner = companion.GetOwner();
            if (owner != null && !owner.IsDead())
            {
                float dist = Vector3.Distance(myPos, owner.transform.position);
                if (dist <= SupportStaffRange)
                {
                    // Owner always gets high priority, especially if damaged
                    float healthMissing = 1f - owner.GetHealthPercentage();
                    float score = OwnerBuffPriorityBase + (healthMissing * OwnerBuffHealthWeight) - (dist * 0.5f);
                    
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTarget = owner;
                    }
                }
            }
            
            // Check other companions and tamed creatures
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == Context.Character) continue; // Don't target self
                
                // Check if this is a friendly
                if (!IsFriendlyTarget(character, ownerId)) continue;
                
                float dist = Vector3.Distance(myPos, character.transform.position);
                if (dist > SupportStaffRange) continue;
                
                // Score based on health missing and distance
                float healthMissing = 1f - character.GetHealthPercentage();
                float score = (healthMissing * FriendlyBuffHealthWeight) - (dist * 0.5f);
                
                // Bonus for other companions
                var otherCompanion = character.GetComponent<CompanionController>();
                if (otherCompanion != null)
                {
                    score += CompanionBuffPriorityBonus;
                }
                
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = character;
                }
            }
            
            return bestTarget;
        }
        
        /// <summary>
        /// Checks if a character is a friendly target for buff staves.
        /// </summary>
        private bool IsFriendlyTarget(Character character, long ownerId)
        {
            if (character == null) return false;
            
            // Players are always potential friendly targets
            if (character.IsPlayer())
            {
                // Check if it's our owner or a party member
                var player = character as Player;
                if (player != null && player.GetPlayerID() == ownerId)
                {
                    return true;
                }
                // TODO: Could add party/group support here
                return true; // For now, all players are friendly
            }
            
            // Check if it's a tamed creature
            if (character.IsTamed())
            {
                // Check if it belongs to the same owner
                var tameable = character.GetComponent<Tameable>();
                if (tameable != null)
                {
                    // Check companion ownership
                    var otherCompanion = character.GetComponent<CompanionController>();
                    if (otherCompanion != null)
                    {
                        return otherCompanion.ownerPlayerId == ownerId;
                    }
                    
                    // Regular tamed creature - assume friendly if tamed
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if we have clear line of sight to the target.
        /// </summary>
        private bool HasLineOfSight(Character target)
        {
            if (target == null) return false;
            
            Vector3 eyePos = Context.Transform.position + Vector3.up * EyeHeight;
            Vector3 targetPos = target.transform.position + Vector3.up * 1f;
            Vector3 direction = targetPos - eyePos;
            float distance = direction.magnitude;
            
            if (Physics.Raycast(eyePos, direction.normalized, out RaycastHit hit, distance))
            {
                // Check if we hit the target or something attached to it
                var hitCharacter = hit.collider.GetComponentInParent<Character>();
                return hitCharacter == target;
            }
            
            return true; // No obstruction
        }

    public override void ExecuteAttack(Character target)
        {
            // For support staves, use the friendly target instead
            Character actualTarget = target;
            if (_isSupportStaff && _friendlyTarget != null && !_friendlyTarget.IsDead())
            {
                actualTarget = _friendlyTarget;
                
                if (CompanionCombat.VerboseLogging)
                {
                    Debug.Log($"[StaffBehavior] Support staff targeting friendly: {_friendlyTarget.m_name}");
                }
            }
            
            FaceTarget(actualTarget);
     
            _lastAttackTime = Time.time;
            _isAttacking = true;
         
            // Check if this is a projectile-based attack
            if (Context.CurrentAttack?.m_attackProjectile != null)
            {
                ExecuteProjectileAttack(actualTarget);
            }
            else
            {
                // Try native attack system
                if (Context.UseNativeAttackSystem && TryStartNativeAttack(actualTarget, false))
                {
                    Context.BroadcastRPC("RPC_CompanionAttack",
                        Context.GetAttackAnimationTrigger(_attackChainLevel),
                        Context.GetAttackAnimationIndex());
                    return;
                }
  
                // Fallback - use base class method
                float hitDelay = 0.4f;
                ExecuteFallbackAttack(actualTarget, hitDelay);
            }
        }
  
        private void ExecuteProjectileAttack(Character target)
        {
            var attackTemplate = Context.CurrentAttack;
            
            // Check and consume eitr/stamina for the magic attack
            if (!TryConsumeAttackStamina(attackTemplate))
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log("[StaffBehavior] Not enough eitr/stamina for attack");
                _isAttacking = false;
                return;
            }
  
            // Play the attack animation
            string animTrigger = attackTemplate.m_attackAnimation;
            if (!string.IsNullOrEmpty(animTrigger))
            {
    // Handle chain/random animations
         if (attackTemplate.m_attackChainLevels > 1)
      {
          animTrigger = $"{attackTemplate.m_attackAnimation}{_attackChainLevel}";
          _attackChainLevel = (_attackChainLevel + 1) % attackTemplate.m_attackChainLevels;
         }
      else if (attackTemplate.m_attackRandomAnimations > 1)
   {
           int randomIndex = UnityEngine.Random.Range(0, attackTemplate.m_attackRandomAnimations);
        animTrigger = $"{attackTemplate.m_attackAnimation}{randomIndex}";
     }
      
             Context.PlayAttackAnimation(animTrigger, (int)Context.CurrentWeapon.m_shared.m_animationState);
    }
     
       // Use coroutine for projectile spawn and attack finish
            float spawnDelay = 0.4f;
 float attackDuration = 1.5f;
            _attackCoroutine = Owner.StartCoroutine(ProjectileAttackCoroutine(target, attackTemplate, spawnDelay, attackDuration));
    
    Context.BroadcastRPC("RPC_CompanionAttack", animTrigger, (int)Context.CurrentWeapon.m_shared.m_animationState);
      
     if (CompanionCombat.VerboseLogging)
    {
     Debug.Log($"[StaffBehavior] Projectile attack started - weapon: {Context.CurrentWeapon.m_shared.m_name}, " +
    $"projectile: {attackTemplate.m_attackProjectile.name}, anim: {animTrigger}");
          }
        }
    
private IEnumerator ProjectileAttackCoroutine(Character target, Attack attackTemplate, float spawnDelay, float attackDuration)
        {
  yield return new WaitForSeconds(spawnDelay);
   
       if (!(Context.Companion?.isDefeated ?? true) && target != null && !target.IsDead())
 {
      SpawnProjectile(target, attackTemplate);
    }
      
 yield return new WaitForSeconds(attackDuration - spawnDelay);
 FinishAttack();
   _attackCoroutine = null;
        }
    
    private void SpawnProjectile(Character target, Attack attackTemplate)
 {
        if (attackTemplate?.m_attackProjectile == null || target == null) return;
        
        // CRITICAL: For support staves, apply the buff directly to the friendly target
        // This prevents the AOE from accidentally buffing enemies
        if (_isSupportStaff)
        {
            SpawnSupportStaffEffect(target, attackTemplate);
            return;
        }
      
    // Calculate spawn position (from character's chest/hands area)
   Vector3 spawnPos = Context.Transform.position + Vector3.up * ProjectileSpawnHeight + Context.Transform.forward * 0.5f;
     
    // Calculate direction to target
            Vector3 targetPos = target.transform.position + Vector3.up * 1f;
   Vector3 direction = (targetPos - spawnPos).normalized;
   
   Quaternion rotation = Quaternion.LookRotation(direction);
            
   // Spawn the projectile
     GameObject projectileObj = UnityEngine.Object.Instantiate(attackTemplate.m_attackProjectile, spawnPos, rotation);
   
         // Configure the projectile
    var projectile = projectileObj.GetComponent<Projectile>();
            if (projectile != null)
       {
HitData hitData = Context.CreateHitData(target);
   
             projectile.Setup(
   Context.Character,
       direction * attackTemplate.m_projectileVel,
              attackTemplate.m_attackHitNoise,
   hitData,
     null,
              Context.CurrentWeapon
           );
          
                if (CompanionCombat.VerboseLogging)
        {
     Debug.Log($"[StaffBehavior] Spawned projectile {attackTemplate.m_attackProjectile.name} " +
      $"at {spawnPos} toward {target.m_name}, damage: {hitData.m_damage.GetTotalDamage():F1}");
   }
  }
       else
            {
        // No Projectile component - might be a different type of attack object (AOE, etc)
      if (CompanionCombat.VerboseLogging)
          {
 Debug.Log($"[StaffBehavior] Spawned attack object {attackTemplate.m_attackProjectile.name} " +
        $"(no Projectile component - may be AOE/special attack)");
                }
      }
  
            // Raise skill
            Context.CompanionSkills?.RaiseSkill(Context.GetWeaponSkillType(), 1f);
        }
        
        /// <summary>
        /// Spawns a support staff effect and applies the buff to ALL party members in range.
        /// This includes: the owner player, all companions owned by the same player, and the caster itself.
        /// The visual effect spawns at the primary target, but the buff is applied to everyone in the party.
        /// </summary>
        private void SpawnSupportStaffEffect(Character friendlyTarget, Attack attackTemplate)
        {
            if (friendlyTarget == null || attackTemplate?.m_attackProjectile == null) return;
            
            // Get the status effect from the projectile's AOE component
            string statusEffectName = null;
            var aoeTemplate = attackTemplate.m_attackProjectile.GetComponent<Aoe>();
            if (aoeTemplate != null)
            {
                statusEffectName = aoeTemplate.m_statusEffect;
            }
            
            // Spawn the visual effect at the friendly target's position
            Vector3 targetPos = friendlyTarget.transform.position;
            GameObject effectObj = UnityEngine.Object.Instantiate(attackTemplate.m_attackProjectile, targetPos, Quaternion.identity);
            
            // CRITICAL: Modify the spawned AOE to only affect friendlies, not enemies
            var aoe = effectObj.GetComponent<Aoe>();
            if (aoe != null)
            {
                // Set a very small radius so the AOE itself doesn't accidentally hit enemies
                aoe.m_radius = 0.5f;
                
                // Make it only affect friendlies
                aoe.m_hitOwner = false;
                aoe.m_hitSame = false;
                aoe.m_hitFriendly = true;
                aoe.m_hitEnemy = false;  // CRITICAL: Don't hit enemies!
                aoe.m_hitCharacters = true;
                
                // Disable damage (it's a buff, not damage)
                aoe.m_damage = new HitData.DamageTypes();
                
                if (CompanionCombat.VerboseLogging)
                {
                    Debug.Log($"[StaffBehavior] Support staff AOE configured: radius=0.5, hitFriendly=true, hitEnemy=false, effect={statusEffectName}");
                }
            }
            
            // Apply the status effect to ALL party members in range
            if (!string.IsNullOrEmpty(statusEffectName))
            {
                ApplyBuffToParty(statusEffectName);
            }
            
            // Raise skill
            Context.CompanionSkills?.RaiseSkill(Context.GetWeaponSkillType(), 1f);
            
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[StaffBehavior] Support staff effect spawned at {friendlyTarget.m_name}, buff applied to party, effect: {statusEffectName ?? "visual only"}");
            }
        }
        
        /// <summary>
        /// Applies a buff status effect to all party members within range.
        /// Party includes: owner player, all companions owned by the same player, and the caster.
        /// </summary>
        private void ApplyBuffToParty(string effectName)
        {
            if (string.IsNullOrEmpty(effectName)) return;
            
            var companion = Context.Companion;
            if (companion == null) return;
            
            long ownerId = companion.ownerPlayerId;
            Vector3 myPos = Context.Transform.position;
            int buffedCount = 0;
            
            // Track when we applied shields for cooldown purposes
            _lastShieldApplication = Time.time;
            
            // 1. Apply to self (the companion casting the buff)
            if (Context.Character != null && !Context.Character.IsDead())
            {
                ApplyStatusEffectDirectly(Context.Character, effectName);
                buffedCount++;
                
                if (CompanionCombat.VerboseLogging)
                {
                    Debug.Log($"[StaffBehavior] Applied {effectName} to self: {Context.Character.m_name}");
                }
            }
            
            // 2. Apply to owner player
            var owner = companion.GetOwner();
            if (owner != null && !owner.IsDead())
            {
                float dist = Vector3.Distance(myPos, owner.transform.position);
                if (dist <= SupportStaffRange)
                {
                    ApplyStatusEffectDirectly(owner, effectName);
                    buffedCount++;
                    
                    if (CompanionCombat.VerboseLogging)
                    {
                        Debug.Log($"[StaffBehavior] Applied {effectName} to owner: {owner.m_name}");
                    }
                }
            }
            
            // 3. Apply to all other companions owned by the same player
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == Context.Character) continue; // Already applied to self
                if (character.IsPlayer()) continue; // Owner already handled above
                
                // Check if this is a companion owned by the same player
                var otherCompanion = character.GetComponent<CompanionController>();
                if (otherCompanion != null && otherCompanion.ownerPlayerId == ownerId)
                {
                    float dist = Vector3.Distance(myPos, character.transform.position);
                    if (dist <= SupportStaffRange)
                    {
                        ApplyStatusEffectDirectly(character, effectName);
                        buffedCount++;
                        
                        if (CompanionCombat.VerboseLogging)
                        {
                            Debug.Log($"[StaffBehavior] Applied {effectName} to party companion: {character.m_name}");
                        }
                    }
                }
            }
            
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[StaffBehavior] Party buff complete: applied {effectName} to {buffedCount} party members");
            }
        }
        
        /// <summary>
        /// Directly applies a status effect to a character.
        /// Used as a fallback for support staves to ensure the buff is applied.
        /// </summary>
        private void ApplyStatusEffectDirectly(Character target, string effectName)
        {
            if (target == null || string.IsNullOrEmpty(effectName)) return;
            
            try
            {
                var seman = target.GetSEMan();
                if (seman == null) return;
                
                // Try to find the status effect by name hash
                int effectHash = effectName.GetStableHashCode();
                
                // Check if effect already active
                if (seman.HaveStatusEffect(effectHash))
                {
                    // Refresh the existing effect
                    var existing = seman.GetStatusEffect(effectHash);
                    if (existing != null)
                    {
                        existing.ResetTime();
                        if (CompanionCombat.VerboseLogging)
                        {
                            Debug.Log($"[StaffBehavior] Refreshed existing status effect: {effectName} on {target.m_name}");
                        }
                    }
                    return;
                }
                
                // Try to add the status effect by hash
                var effectPrefab = ObjectDB.instance?.GetStatusEffect(effectHash);
                if (effectPrefab != null)
                {
                    seman.AddStatusEffect(effectPrefab, true);
                    if (CompanionCombat.VerboseLogging)
                    {
                        Debug.Log($"[StaffBehavior] Applied status effect: {effectName} to {target.m_name}");
                    }
                }
                else
                {
                    // Try finding by iterating through all status effects
                    if (ObjectDB.instance != null)
                    {
                        foreach (var statusEffect in ObjectDB.instance.m_StatusEffects)
                        {
                            if (statusEffect != null && statusEffect.name.Equals(effectName, System.StringComparison.OrdinalIgnoreCase))
                            {
                                seman.AddStatusEffect(statusEffect, true);
                                if (CompanionCombat.VerboseLogging)
                                {
                                    Debug.Log($"[StaffBehavior] Applied status effect by name match: {effectName} to {target.m_name}");
                                }
                                return;
                            }
                        }
                    }
                    
                    if (CompanionCombat.VerboseLogging)
                    {
                        Debug.Log($"[StaffBehavior] Could not find status effect: {effectName}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[StaffBehavior] Failed to apply status effect {effectName}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Override to consume eitr instead of stamina for staff attacks.
        /// </summary>
        protected override bool TryConsumeAttackStamina(Attack attackTemplate)
        {
            if (attackTemplate == null) return true;
            
            var stats = Context.Companion?.GetStats();
            if (stats == null) return true; // No stats system = unlimited resources
            
            // Staffs use eitr, not stamina
            float baseEitrCost = attackTemplate.m_attackEitr;
            
            // If no eitr cost specified, fall back to stamina (some staffs might use stamina)
            if (baseEitrCost <= 0)
            {
                return base.TryConsumeAttackStamina(attackTemplate);
            }
            
            // Get skill-adjusted cost
            float adjustedCost = baseEitrCost;
            var skills = Context.Companion?.GetSkills();
            if (skills != null)
            {
                var magicSkill = Context.GetWeaponSkillType();
                adjustedCost = stats.GetEitrCost(baseEitrCost, magicSkill);
            }
            
            // Try to use eitr
            if (stats.UseEitr(adjustedCost))
            {
                if (CompanionCombat.VerboseLogging)
                {
                    Debug.Log($"[StaffBehavior] Consumed {adjustedCost:F1} eitr (base: {baseEitrCost:F1})");
                }
                return true;
            }
            
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[StaffBehavior] Not enough eitr: need {adjustedCost:F1}, have {stats.CurrentEitr:F1}");
            }
            return false;
        }
    }
}
