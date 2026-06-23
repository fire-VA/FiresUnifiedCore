using UnityEngine;
using System.Collections;
using FiresCore.Npc.Combat;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Bow training sub-behavior. When idle with a bow equipped and near an archery target,
    /// the companion will practice their archery skills.
    /// 
    /// FLOW:
    /// 1. Find nearby ArcheryTarget component (within 20m)
    /// 2. Walk to a good firing position (5-10m from target, facing it)
    /// 3. Fire 5-10 practice shots at the target's center (m_center)
    /// 4. Walk to target to "retrieve arrows"
    /// 5. Complete and transition to another idle behavior
    /// 
    /// SKILL GAIN:
    /// Uses the ArcheryTarget's m_raiseSkillMultiplier for proper skill gain.
    /// Practice shots give skill based on accuracy (distance from center).
    /// </summary>
    public class BowTrainingBehavior : IdleSubBehavior
    {
        public override string BehaviorName => "BowTraining";
        
        /// <summary>
        /// Bow training has below-average priority so productive tasks (smelting,
        /// farming, gathering) always win when there is work to do. Training only
        /// kicks in when nothing more useful can start.
        /// </summary>
        public override int InventoryPriority => -1;
        
        #region Settings
        
        private const float TARGET_DETECTION_RANGE = 25f;
        private const float MIN_FIRING_DISTANCE = 6f;
        private const float MAX_FIRING_DISTANCE = 15f;
        private const float OPTIMAL_FIRING_DISTANCE = 10f;
        private const int MIN_SHOTS = 5;
        private const int MAX_SHOTS = 10;
        private const float DRAW_DURATION = 1.5f;
        private const float SHOT_INTERVAL = 2.5f;
        private const float POSITION_TOLERANCE = 1.5f;
        private const float ARROW_RETRIEVE_TIME = 2f;  // Time to wait after interacting with target
        
        #endregion
        
        #region State
        
        private enum TrainingPhase
        {
            FindingTarget,
            MovingToPosition,
            Aiming,
            Drawing,
            Shooting,
            BetweenShots,
            RetrievingArrows,
            Complete
        }
        
        private TrainingPhase _currentPhase = TrainingPhase.FindingTarget;
        private ArcheryTarget _archeryTarget;
        private Vector3 _targetCenterPosition;
        private Vector3 _firingPosition;
        private int _totalShots;
        private int _shotsFired;
        private float _phaseStartTime;
        private float _drawStartTime;
        private float _lastShotTime;
        
        // Components
        private Character _character;
        private Humanoid _humanoid;
        private Animator _animator;
        private ZSyncAnimation _zanim;
        private CompanionInventory _inventory;
        private CompanionSkills _skills;
        private CompanionCombatMovement _combatMovement;
        private CompanionStats _stats;
        private Rigidbody _rigidbody;
        
        // Arrow retrieval
        private bool _hasRetrievedArrows = false;
        
        // Perpendicular firing angle (80-100 degrees to target face)
        private const float MIN_FIRING_ANGLE = 80f;
        private const float MAX_FIRING_ANGLE = 100f;
        
        #endregion
   
        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            
            _character = companion.GetComponent<Character>();
            _humanoid = companion.GetComponent<Humanoid>();
            _animator = companion.GetComponentInChildren<Animator>(true);
            _zanim = companion.GetComponent<ZSyncAnimation>();
            _inventory = companion.GetComponent<CompanionInventory>();
            _skills = companion.GetComponent<CompanionSkills>();
            _combatMovement = companion.GetComponent<CompanionCombatMovement>();
            _rigidbody = companion.GetComponent<Rigidbody>();
            _stats = companion.GetComponent<CompanionStats>();
            
            MaxDuration = 180f; // 3 minute max for training session
        }
        
        public override bool CanStart()
        {
            if (Companion == null) return false;
            
            // Check if we have a bow equipped
            if (!HasBowEquipped())
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[BowTraining] {Companion.companionName} has no bow equipped");
                return false;
            }
            
            // Check if there's a nearby archery target
            var target = FindNearbyArcheryTarget();
            if (target == null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[BowTraining] {Companion.companionName} found no archery target within {GetEffectiveSearchRadius(TARGET_DETECTION_RANGE)}m");
                return false;
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[BowTraining] {Companion.companionName} CAN start training (target: {target.m_name})");
            return true;
        }
        
        public override void Start()
        {
            base.Start();
            
            _currentPhase = TrainingPhase.FindingTarget;
            _shotsFired = 0;
            _totalShots = Random.Range(MIN_SHOTS, MAX_SHOTS + 1);
            _phaseStartTime = Time.time;
            _hasRetrievedArrows = false;
            
            // Ensure bow is equipped (not holstered) - CRITICAL: Must verify before animations
            EnsureBowEquipped();
            
            // CRITICAL: Verify bow is ACTUALLY in left hand before proceeding
            // If not, we need to abort or wait for the swap to complete
            if (!VerifyBowInHand())
            {
                Debug.LogWarning($"[BowTraining] {Companion?.companionName} - bow not in hand after equip attempt, aborting");
                Complete();
                return;
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[BowTraining] {Companion?.companionName} starting training - {_totalShots} shots planned, bow verified in hand");
        }
        
        public override bool Update()
        {
            if (!IsActive) return true;
            
            // Check for timeout
            if (IsTimedOut())
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[BowTraining] {Companion?.companionName} timed out");
                Complete();
                return true;
            }

            // Re-equip bow if it left the hand during active training phases
            if (_currentPhase == TrainingPhase.Aiming   ||
                _currentPhase == TrainingPhase.Drawing   ||
                _currentPhase == TrainingPhase.Shooting  ||
                _currentPhase == TrainingPhase.BetweenShots)
            {
                if (!VerifyBowInHand())
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[BowTraining] {Companion?.companionName} bow left hand â€” re-equipping");
                    EnsureBowEquipped();
                }
            }

            switch (_currentPhase)
            {
                case TrainingPhase.FindingTarget:
                    return UpdateFindingTarget();
                    
                case TrainingPhase.MovingToPosition:
                    return UpdateMovingToPosition();
                    
                case TrainingPhase.Aiming:
                    return UpdateAiming();
                    
                case TrainingPhase.Drawing:
                    return UpdateDrawing();
                    
                case TrainingPhase.Shooting:
                    return UpdateShooting();
                    
                case TrainingPhase.BetweenShots:
                    return UpdateBetweenShots();
                    
                case TrainingPhase.RetrievingArrows:
                    return UpdateRetrievingArrows();
                    
                case TrainingPhase.Complete:
                    Complete();
                    return true;
            }
            
            return false;
        }
        
        public override void Cancel()
        {
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[BowTraining] {Companion?.companionName} cancelled (phase: {_currentPhase})");
            
            ResetBowAnimation();
            _combatMovement?.UnlockMovement();
            base.Cancel();
        }
        
        public override string GetStatusDescription()
        {
            return _currentPhase switch
            {
                TrainingPhase.FindingTarget => "Looking for target",
                TrainingPhase.MovingToPosition => "Moving to firing position",
                TrainingPhase.Aiming => "Aiming",
                TrainingPhase.Drawing => "Drawing bow",
                TrainingPhase.Shooting => "Firing",
                TrainingPhase.BetweenShots => $"Practicing archery ({_shotsFired}/{_totalShots})",
                TrainingPhase.RetrievingArrows => "Retrieving arrows",
                _ => "Training"
            };
        }
        
        #region Phase Updates
        
        private bool UpdateFindingTarget()
        {
            _archeryTarget = FindNearbyArcheryTarget();
            
            if (_archeryTarget == null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[BowTraining] {Companion.companionName} couldn't find archery target");
                Complete();
                return true;
            }
            
            // Use the ArcheryTarget's center point for aiming
            _targetCenterPosition = _archeryTarget.m_center != null 
                ? _archeryTarget.m_center.transform.position 
                : _archeryTarget.transform.position;
            
            _firingPosition = CalculateFiringPosition();
            
            SetPhase(TrainingPhase.MovingToPosition);
            MoveToPosition(_firingPosition);
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[BowTraining] {Companion.companionName} found target '{_archeryTarget.m_name}' " +
                    $"(size: {_archeryTarget.m_targetSize}, points: {_archeryTarget.m_points})");
            
            return false;
        }
        
        private bool UpdateMovingToPosition()
        {
            float distToPosition = Vector3.Distance(Transform.position, _firingPosition);
            
            // Check if we've arrived
            if (distToPosition < POSITION_TOLERANCE)
            {
                StopMovement();
                SetPhase(TrainingPhase.Aiming);
                return false;
            }
            
            // CRITICAL FIX: Call MoveToPosition EVERY FRAME!
            // Vanilla pathfinding (MoveTo) only moves one waypoint at a time per call.
            // Without this, the companion walks to the first waypoint then stops.
            MoveToPosition(_firingPosition);
            
            // Check for timeout on movement (10 seconds)
            if (Time.time - _phaseStartTime > 10f)
            {
                // Try to shoot from current position if close enough to target
                float distToTarget = Vector3.Distance(Transform.position, _targetCenterPosition);
                if (distToTarget >= MIN_FIRING_DISTANCE && distToTarget <= MAX_FIRING_DISTANCE * 1.5f)
                {
                    StopMovement();
                    SetPhase(TrainingPhase.Aiming);
                }
                else
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[BowTraining] {Companion.companionName} couldn't reach firing position");
                    Complete();
                    return true;
                }
            }
            
            return false;
        }
        
        private bool UpdateAiming()
        {
            // Lock movement during aiming phase
            if (_combatMovement != null && !_combatMovement.IsMovementLocked)
            {
                _combatMovement.LockMovement("BowTraining_Aiming", 60f);
            }
            
            // Stop any residual movement
            StopAllMovement();
            
            // Face the target and lock rotation
            FaceTargetAndLock(_targetCenterPosition);

            // Brief pause to aim
            if (Time.time - _phaseStartTime > 0.5f)
            {
                SetPhase(TrainingPhase.Drawing);
                StartBowDraw();
            }

            return false;
        }
        
        private bool UpdateDrawing()
        {
            StopAllMovement();
            FaceTargetAndLock(_targetCenterPosition);
            
            // Update draw animation
            float drawProgress = Mathf.Clamp01((Time.time - _drawStartTime) / DRAW_DURATION);
            UpdateDrawAnimation(drawProgress);
            
            // Fire when fully drawn
            if (drawProgress >= 1f)
            {
                SetPhase(TrainingPhase.Shooting);
                FirePracticeShot();
            }
            
            return false;
        }
        
        private bool UpdateShooting()
        {
            StopAllMovement();
            FaceTargetAndLock(_targetCenterPosition);
            
            // Brief pause after shooting
            if (Time.time - _phaseStartTime > 0.3f)
            {
                _shotsFired++;
                
                if (_shotsFired >= _totalShots)
                {
                    // Done shooting, go retrieve arrows
                    SetPhase(TrainingPhase.RetrievingArrows);
                    
                    // Move to the target's return point if available, otherwise to target position
                    Vector3 retrievePos = _archeryTarget.m_returnPoint != null 
                        ? _archeryTarget.m_returnPoint.transform.position 
                        : _archeryTarget.transform.position;
                    MoveToPosition(retrievePos);
                }
                else
                {
                    SetPhase(TrainingPhase.BetweenShots);
                }
            }
            
            return false;
        }
        
        private bool UpdateBetweenShots()
        {
            StopAllMovement();
            FaceTargetAndLock(_targetCenterPosition);

            // Wait between shots
            if (Time.time - _lastShotTime >= SHOT_INTERVAL)
            {
                SetPhase(TrainingPhase.Aiming);
            }
            
            return false;
        }
        
        private bool UpdateRetrievingArrows()
        {
            // Unlock movement for walking to target
            if (_combatMovement != null && _combatMovement.IsMovementLocked)
            {
                _combatMovement.UnlockMovement();
            }
            Vector3 targetPos = _archeryTarget.m_returnPoint != null 
                ? _archeryTarget.m_returnPoint.transform.position 
                : _archeryTarget.transform.position;
            float distToTarget = Vector3.Distance(Transform.position, targetPos);
            
            // Check if we've arrived at target
            if (distToTarget < 2.5f)
            {
                StopMovement();
                
                // Interact with target to retrieve arrows (if we haven't already)
                if (!_hasRetrievedArrows)
                {
                    RetrieveArrowsFromTarget();
                    _hasRetrievedArrows = true;
                }
                
                // Wait a moment after retrieving arrows
                if (Time.time - _phaseStartTime > ARROW_RETRIEVE_TIME)
                {
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[BowTraining] {Companion.companionName} finished training - {_shotsFired} shots fired, arrows retrieved");
                    
                    SetPhase(TrainingPhase.Complete);
                    return true;
                }
            }
            else
            {
                // CRITICAL FIX: Call MoveToPosition EVERY FRAME!
                MoveToPosition(targetPos);
            }
            
            // Timeout on retrieval
            if (Time.time - _phaseStartTime > 15f)
            {
                SetPhase(TrainingPhase.Complete);
                return true;
            }
            
            return false;
        }
        
        #endregion
        
        #region Bow Actions
        
        private void StartBowDraw()
        {
            _drawStartTime = Time.time;
            
            if (_zanim != null)
            {
                _zanim.SetBool("bow_aim", true);
                _zanim.SetFloat("drawpercent", 0f);
            }
            else if (_animator != null)
            {
                if (HasAnimatorParameter("bow_aim"))
                    _animator.SetBool("bow_aim", true);
                if (HasAnimatorParameter("drawpercent"))
                    _animator.SetFloat("drawpercent", 0f);
            }
        }
        
        private void UpdateDrawAnimation(float progress)
        {
            if (_zanim != null)
            {
                _zanim.SetFloat("drawpercent", progress);
            }
            else if (_animator != null && HasAnimatorParameter("drawpercent"))
            {
                _animator.SetFloat("drawpercent", progress);
            }
        }
        
        private void FirePracticeShot()
        {
            _lastShotTime = Time.time;
            
            // Consume stamina for the shot (bow attacks typically cost 20-25 stamina)
            float staminaCost = 20f;
            if (_stats != null)
            {
                staminaCost = _stats.GetStaminaCost(staminaCost, Skills.SkillType.Bows);
                if (!_stats.UseStamina(staminaCost))
                {
                    // Not enough stamina - take a break instead of firing
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[BowTraining] {Companion.companionName} needs to rest - not enough stamina");
                    return;
                }
            }
            
            // Trigger fire animation
            if (_zanim != null)
            {
                _zanim.SetTrigger("bow_fire");
                _zanim.SetBool("bow_aim", false);
                _zanim.SetFloat("drawpercent", 0f);
            }
            else if (_animator != null)
            {
                if (HasAnimatorParameter("bow_fire"))
                    _animator.SetTrigger("bow_fire");
                if (HasAnimatorParameter("bow_aim"))
                    _animator.SetBool("bow_aim", false);
                if (HasAnimatorParameter("drawpercent"))
                    _animator.SetFloat("drawpercent", 0f);
            }
            
            // Spawn a practice projectile (aimed at target center)
            SpawnPracticeArrow();
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[BowTraining] {Companion.companionName} fired practice shot {_shotsFired + 1}/{_totalShots} (stamina cost: {staminaCost:F1})");
        }
        
        private void SpawnPracticeArrow()
        {
            // Get arrow projectile
            GameObject projectilePrefab = GetArrowProjectile();
            if (projectilePrefab == null) return;
            
            Vector3 spawnPos = Transform.position + Vector3.up * 1.5f + Transform.forward * 0.3f;
            
            // Aim at the target's center point
            Vector3 targetPos = _targetCenterPosition;
            Vector3 direction = (targetPos - spawnPos).normalized;
            
            // Calculate proper arc for gravity compensation
            float distance = Vector3.Distance(spawnPos, targetPos);
            float velocity = 50f; // Standard arrow speed
            float flightTime = distance / velocity;
            float gravityCompensation = 0.5f * 9.81f * flightTime * flightTime;
            float arcHeight = gravityCompensation / Mathf.Max(1f, distance);
            arcHeight = Mathf.Clamp(arcHeight, 0f, 0.3f);
            
            // Add skill-based accuracy variation
            float skillLevel = _skills?.GetSkillLevel(Skills.SkillType.Bows) ?? 0f;
            float accuracyBonus = Mathf.Lerp(0.03f, 0.005f, skillLevel / 100f);
            
            // Add slight randomness to simulate imperfect aim (based on skill)
            Vector3 randomOffset = new Vector3(
                Random.Range(-accuracyBonus, accuracyBonus),
                Random.Range(-accuracyBonus, accuracyBonus),
                Random.Range(-accuracyBonus, accuracyBonus)
            );
            
            direction = (direction + Vector3.up * arcHeight + randomOffset).normalized;
            
            Quaternion rotation = Quaternion.LookRotation(direction);
            GameObject projectileObj = Object.Instantiate(projectilePrefab, spawnPos, rotation);
            
            var projectile = projectileObj.GetComponent<Projectile>();
            if (projectile != null)
            {
                // Set up projectile - the ArcheryTarget's OnProjectileHit will handle scoring
                HitData hitData = new HitData();
                hitData.m_damage.m_pierce = 1f; // Minimal damage
                hitData.m_skill = Skills.SkillType.Bows;
                
                // Set the skill raise amount - ArcheryTarget will multiply by m_raiseSkillMultiplier
                projectile.m_skill = Skills.SkillType.Bows;
                projectile.m_raiseSkillAmount = 1f;
                
                projectile.Setup(
                    _character,
                    direction * velocity,
                    0f, // No noise
                    hitData,
                    null,
                    null
                );
            }
        }
        
        private void ResetBowAnimation()
        {
            if (_zanim != null)
            {
                _zanim.SetBool("bow_aim", false);
                _zanim.SetFloat("drawpercent", 0f);
            }
            else if (_animator != null)
            {
                if (HasAnimatorParameter("bow_aim"))
                    _animator.SetBool("bow_aim", false);
                if (HasAnimatorParameter("drawpercent"))
                    _animator.SetFloat("drawpercent", 0f);
            }
        }
        
        private GameObject GetArrowProjectile()
        {
            // Try to get from equipped bow's attack data
            var bowItem = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (bowItem?.m_shared?.m_attack?.m_attackProjectile != null)
            {
                return bowItem.m_shared.m_attack.m_attackProjectile;
            }
            
            // Check right back for holstered bow
            var rightBack = _inventory?.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
            if (rightBack?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Bow)
            {
                if (rightBack.m_shared?.m_attack?.m_attackProjectile != null)
                {
                    return rightBack.m_shared.m_attack.m_attackProjectile;
                }
            }
            
            // Fallback to basic wood arrow
            if (ObjectDB.instance != null)
            {
                var arrowPrefab = ObjectDB.instance.GetItemPrefab("ArrowWood");
                if (arrowPrefab != null)
                {
                    var itemDrop = arrowPrefab.GetComponent<ItemDrop>();
                    if (itemDrop?.m_itemData?.m_shared?.m_attack?.m_attackProjectile != null)
                    {
                        return itemDrop.m_itemData.m_shared.m_attack.m_attackProjectile;
                    }
                }
            }
            
            return null;
        }
        
        #endregion
        
        #region Helpers
        
        private bool HasBowEquipped()
        {
            if (_inventory == null) return false;
            
            // Check left hand (where bows are equipped when in use)
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand != null && leftHand.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                return true;
            
            // Check left back (holstered bow)
            var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
            if (leftBack != null && leftBack.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                return true;
            
            // Also check right back (some configurations might put bow there)
            var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
            if (rightBack != null && rightBack.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                return true;
            
            return false;
        }
        
        /// <summary>
        /// Ensures the bow is equipped in hand (not holstered) for training.
        /// Will unequip any melee weapon from right hand first.
        /// </summary>
        private void EnsureBowEquipped()
        {
            if (_inventory == null || _humanoid == null) return;
            
            // Check if bow is already in left hand
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand != null && leftHand.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
            {
                // Bow is already equipped, but we should still unequip right hand weapon
                UnequipRightHandWeapon();
                // Force visual update to ensure visuals match inventory state
                FinalizeEquipmentChange();
                return;
            }
            
            // First, unequip any weapon from right hand (move to back or storage)
            UnequipRightHandWeapon();
            
            // Check if bow is holstered on LeftBack first (primary location for bows)
            var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
            if (leftBack != null && leftBack.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
            {
                EquipBowFromSlot(CompanionInventory.EquipmentSlot.LeftBack, leftBack);
                return;
            }
            
            // Check if bow is holstered on RightBack (secondary location)
            var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
            if (rightBack != null && rightBack.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
            {
                EquipBowFromSlot(CompanionInventory.EquipmentSlot.RightBack, rightBack);
                return;
            }
            
            // Try WeaponSwapManager as fallback (handles storage inventory weapons)
            var weaponSwapManager = Companion.GetComponent<WeaponSwapManager>();
            if (weaponSwapManager != null)
            {
                weaponSwapManager.ScanAvailableWeapons();
                if (weaponSwapManager.ForceSwapToRanged())
                {
                    // WeaponSwapManager already calls FinalizeWeaponSwap which handles visuals
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[BowTraining] {Companion.companionName} swapped to bow via WeaponSwapManager");
                }
            }
        }
        
        /// <summary>
        /// Verifies that a bow is actually equipped in the left hand slot.
        /// Call this after EnsureBowEquipped() to confirm the swap succeeded.
        /// </summary>
        private bool VerifyBowInHand()
        {
            if (_inventory == null) return false;
            
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand == null) return false;
            if (leftHand.m_shared == null) return false;
            
            return leftHand.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow;
        }
        
        /// <summary>
        /// Finalizes equipment changes by recalculating bonuses, applying visuals, and saving.
        /// </summary>
        private void FinalizeEquipmentChange()
        {
            _inventory.RecalculateEquipmentBonusesPublic();
            _inventory.ApplyVisualEquipment();
            _inventory.SaveToZDO();
        }
        
        /// <summary>
        /// Unequips any weapon from right hand slot, moving it to back or storage.
        /// CRITICAL: Also clears any shield from left hand since we need left hand for bow.
        /// </summary>
        private void UnequipRightHandWeapon()
        {
            bool madeChanges = false;
            
            // Check right hand for weapon
            var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (rightHand != null && rightHand.IsWeapon())
            {
                // Unequip from right hand
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);
                madeChanges = true;
                
                // Try to put in right back slot
                var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                if (rightBack == null)
                {
                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightBack, rightHand);
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[BowTraining] Moved {rightHand.m_shared?.m_name} to RightBack for training");
                }
                else
                {
                    // Back slot occupied, put in storage
                    var storageInv = _inventory.GetStorageInventory();
                    if (storageInv != null && storageInv.AddItem(rightHand))
                    {
                        if (CompanionIdleBehavior.VerboseLogging)
                            Debug.Log($"[BowTraining] Moved {rightHand.m_shared?.m_name} to storage for training");
                    }
                    else
                    {
                        // Couldn't store, re-equip
                        _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, rightHand);
                        Debug.LogWarning($"[BowTraining] Could not store melee weapon for bow training");
                    }
                }
            }
            
            // ALSO check left hand for shield (shields must be removed for bow)
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand != null && leftHand.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Shield)
            {
                _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.LeftHand);
                madeChanges = true;
                
                // Try to put shield in left back slot
                var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
                if (leftBack == null)
                {
                    _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftBack, leftHand);
                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[BowTraining] Moved shield {leftHand.m_shared?.m_name} to LeftBack for training");
                }
                else
                {
                    // Put in storage
                    var storageInv = _inventory.GetStorageInventory();
                    storageInv?.AddItem(leftHand);
                }
            }
            
            if (madeChanges)
            {
                FinalizeEquipmentChange();
            }
        }
        
        /// <summary>
        /// Equips a bow from a back slot to the left hand.
        /// </summary>
        private void EquipBowFromSlot(CompanionInventory.EquipmentSlot fromSlot, ItemDrop.ItemData bow)
        {
            // Remove from back slot
            _inventory.UnequipSlotSilent(fromSlot);
            
            // Equip to left hand
            _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftHand, bow);
            
            // Apply changes
            _inventory.RecalculateEquipmentBonusesPublic();
            _inventory.ApplyVisualEquipment();
            _inventory.SaveToZDO();
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[BowTraining] {Companion.companionName} equipped bow from {fromSlot} for training");
        }
        
        /// <summary>
        /// Interacts with the archery target to retrieve all arrows stuck in it.
        /// Uses the same interaction that players use.
        /// </summary>
        private void RetrieveArrowsFromTarget()
        {
            if (_archeryTarget == null || _humanoid == null) return;
            
            try
            {
                // ArcheryTarget implements Interactable - calling Interact removes all stuck arrows
                bool interacted = _archeryTarget.Interact(_humanoid, false, false);
                
                if (CompanionIdleBehavior.VerboseLogging)
                {
                    Debug.Log($"[BowTraining] {Companion.companionName} retrieved arrows from target (interact result: {interacted})");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BowTraining] Failed to retrieve arrows: {ex.Message}");
            }
        }
        
        private ArcheryTarget FindNearbyArcheryTarget()
        {
            // Look for ArcheryTarget components within range
            // This is the proper Valheim component for archery targets
            
            ArcheryTarget bestTarget = null;
            float bestDistance = float.MaxValue;
            
            // Use the effective search radius (50m for staying companions)
            float searchRadius = GetEffectiveSearchRadius(TARGET_DETECTION_RANGE);
            
            // Find all ArcheryTarget components in the scene
            var allTargets = Object.FindObjectsByType<ArcheryTarget>(FindObjectsSortMode.None);
            
            foreach (var target in allTargets)
            {
                if (target == null) continue;
                
                // Get distance to target from SearchCenter (home position for staying companions)
                Vector3 targetPos = target.m_center != null 
                    ? target.m_center.transform.position 
                    : target.transform.position;
                    
                float dist = Vector3.Distance(SearchCenter, targetPos);
                
                if (dist <= searchRadius && dist < bestDistance)
                {
                    bestDistance = dist;
                    bestTarget = target;
                }
            }
            
            // Fallback: Also check by collider overlap for targets that might not be found via FindObjectsOfType
            if (bestTarget == null)
            {
                Collider[] colliders = Physics.OverlapSphere(SearchCenter, searchRadius);
                
                foreach (var col in colliders)
                {
                    if (col == null) continue;
                    
                    // Check for ArcheryTarget component
                    var archeryTarget = col.GetComponent<ArcheryTarget>() ?? col.GetComponentInParent<ArcheryTarget>();
                    if (archeryTarget != null)
                    {
                        Vector3 targetPos = archeryTarget.m_center != null 
                            ? archeryTarget.m_center.transform.position 
                            : archeryTarget.transform.position;
                            
                        float dist = Vector3.Distance(Transform.position, targetPos);
                        
                        if (dist < bestDistance)
                        {
                            bestDistance = dist;
                            bestTarget = archeryTarget;
                        }
                    }
                }
            }
            
            return bestTarget;
        }
        
        private Vector3 CalculateFiringPosition()
        {
            // Calculate a good position to fire from
            // MUST be at 80-100 degrees (perpendicular) to target's facing direction
            // and at OPTIMAL_FIRING_DISTANCE from target
            
            Vector3 targetPos = _targetCenterPosition;
            
            // Get the target's forward direction (the direction the target "faces")
            // ArcheryTarget typically faces the direction players should shoot from
            Vector3 targetForward = _archeryTarget.transform.forward;
            targetForward.y = 0;
            targetForward.Normalize();
            
            // The ideal firing position is BEHIND the target's forward (shooting into the target)
            // i.e., we shoot FROM the direction the target is facing
            Vector3 idealShootFromDir = -targetForward;
            
            // Add a small random angle variation within the 80-100 degree range
            // 90 degrees = perfectly perpendicular, so we vary by +/- 10 degrees
            float angleVariation = UnityEngine.Random.Range(-10f, 10f);
            idealShootFromDir = Quaternion.Euler(0, angleVariation, 0) * idealShootFromDir;
            
            // Calculate position at optimal distance
            Vector3 firingPos = targetPos + idealShootFromDir * OPTIMAL_FIRING_DISTANCE;
            
            // Get ground height at that position
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(firingPos, out groundHeight))
                {
                    firingPos.y = groundHeight;
                }
            }
            
            if (CompanionIdleBehavior.VerboseLogging)
            {
                float actualAngle = Vector3.Angle(targetForward, (Transform.position - targetPos).normalized);
                Debug.Log($"[BowTraining] Calculated firing position: angle to target face = {actualAngle:F1} degrees");
            }
            
            return firingPos;
        }
        
        private void SetPhase(TrainingPhase newPhase)
        {
            if (newPhase == _currentPhase) return;
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[BowTraining] {Companion?.companionName} phase: {_currentPhase} -> {newPhase}");
            
            _currentPhase = newPhase;
            _phaseStartTime = Time.time;
        }
        
        private void MoveToPosition(Vector3 position)
        {
            // Store target position for distance checks in Update
            _firingPosition = position;
            
            // CRITICAL FIX: Use base class TryMoveToPosition for proper vanilla pathfinding
            // This uses CompanionAI.RequestPathfindingMovement() which calls BaseAI.MoveTo()
            // and properly navigates around obstacles using Valheim's pathfinding system.
            // The old _combatMovement.SetMoveDestination() was using direct movement without pathfinding.
            TryMoveToPosition(position, walk: true, run: false);
        }
        
        private new void StopMovement()
        {
            // Use base class StopMovement which properly releases authority
            base.StopMovement();
            
            // Also clear combat movement destination as backup
            if (_combatMovement != null)
            {
                _combatMovement.ClearMoveDestination();
            }
        }
        
        // Recalculates and directly sets rotation toward target every call.
        // Called every frame during all shooting phases so no other system can win a frame.
        private void FaceTargetAndLock(Vector3 targetPos)
        {
            Vector3 dir = targetPos - Transform.position;
            dir.y = 0;
            if (dir.sqrMagnitude < 0.0001f) return;
            dir.Normalize();
            Quaternion rot = Quaternion.LookRotation(dir);
            if (float.IsNaN(rot.x) || float.IsNaN(rot.y) || float.IsNaN(rot.z) || float.IsNaN(rot.w)) return;
            Transform.rotation = rot;
        }
        
        /// <summary>
        /// Stops all movement including rigidbody velocity.
        /// </summary>
        private void StopAllMovement()
        {
            // DON'T call Character.SetMoveDir() if rigidbody is kinematic
            // This causes Unity 6 warnings because Character internally tries to set velocity
            if (_rigidbody != null && _rigidbody.isKinematic)
            {
                // Rigidbody is kinematic - skip all movement commands to avoid warnings
                return;
            }
            
            if (_character != null)
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
            
            // Only set velocity if NOT kinematic (Unity 6 doesn't allow setting velocity on kinematic bodies)
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                // Zero horizontal velocity, keep vertical for gravity
                Vector3 vel = _rigidbody.linearVelocity;
                _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
            }
        }
        
        private bool HasAnimatorParameter(string paramName)
        {
            if (_animator == null) return false;
            foreach (var param in _animator.parameters)
            {
                if (param.name == paramName) return true;
            }
            return false;
        }
        
        #endregion
    }
}
