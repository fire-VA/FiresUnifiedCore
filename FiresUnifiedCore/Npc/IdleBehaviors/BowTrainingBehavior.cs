using UnityEngine;
using System.Collections;
using System.Linq;
using FiresCore.Npc.Combat;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Archery practice while idle with a bow near an archery target: take a firing position facing it, loose a
    /// handful of shots at its center, walk over to collect the arrows, then move on. Skill gain follows the target's
    /// own multiplier and each shot's accuracy.
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

        /// <summary>
        /// In the idle rotation (0.2.255, Fire's homestead test: archery never started on its own, only by command), at its low
        /// priority, so productive work still wins.
        /// </summary>
        public override bool AvailableForIdleRotation => true;
        
        #region Settings
        
        private const float TargetDetectionRange = 25f;
        private const float MinFiringDistance = 6f;
        private const float MaxFiringDistance = 15f;
        private const float OptimalFiringDistance = 10f;
        private const int MinShots = 5;
        private const int MaxShots = 10;
        private const float DrawDuration = 1.5f;
        private const float ShotInterval = 2.5f;
        private const float PositionTolerance = 1.5f;
        private const float ArrowRetrieveTime = 2f;  // Time to wait after interacting with target
        
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
        private AI.ArcheryBrain.Practice _plan;
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
        private bool _hasPickedUpArrows = false;
        private float _retrievedAt;
        
        // Perpendicular firing angle (80-100 degrees to target face)
        private const float MinFiringAngle = 80f;
        private const float MaxFiringAngle = 100f;
        
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
                    Debug.Log($"[BowTraining] {Companion.companionName} found no archery target within {GetEffectiveSearchRadius(TargetDetectionRange)}m");
                return false;
            }
            
            // Real arrows (0.2.244): every practice shot uses one; the target hands back the hits.
            if (AI.ArcheryBrain.ArrowsFor(_inventory?.GetStorageInventory(), TrainingBow(), target) == null)
            {
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[BowTraining] {Companion.companionName} has no arrows for its bow");
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
            _totalShots = Random.Range(MinShots, MaxShots + 1);
            _phaseStartTime = Time.time;
            _hasRetrievedArrows = false;
            _hasPickedUpArrows = false;

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
                        Debug.Log($"[BowTraining] {Companion?.companionName} bow left hand — re-equipping");
                    EnsureBowEquipped();
                    if (!VerifyBowInHand())
                    {
                        Complete();
                        return true;
                    }
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
            // The one practice plan with the bot (0.2.244, ArcheryBrain): the nearest target, a clear reachable spot in front of it,
            // the arrows, and no more shots than arrows carried.
            string why = AI.ArcheryBrain.Plan(_character, _inventory?.GetStorageInventory(), TrainingBow(), SearchCenter,
                GetEffectiveSearchRadius(TargetDetectionRange), IsReachable, out _plan);
            if (why != "")
            {
                Debug.Log($"[BowTraining] {Companion.companionName} cannot practice: {why}");
                Complete();
                return true;
            }

            _archeryTarget = _plan.Target;
            _targetCenterPosition = _plan.Center;
            _firingPosition = _plan.Spot;
            _totalShots = _plan.Shots;

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
            if (distToPosition < PositionTolerance)
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
                if (distToTarget >= MinFiringDistance && distToTarget <= MaxFiringDistance * 1.5f)
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
            float drawProgress = Mathf.Clamp01((Time.time - _drawStartTime) / DrawDuration);
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

                string stop = _shotsFired >= _totalShots ? $"done: {_shotsFired} shot(s)"
                    : AI.ArcheryBrain.ShouldStop(_character, _plan, _inventory?.GetStorageInventory(), _shotsFired);
                if (stop != "")
                {
                    Debug.Log($"[BowTraining] {Companion.companionName} stops shooting ({stop}); collecting arrows");
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
            // Yield body-facing to combat: if an enemy has engaged us, combat (higher movement
            // priority) owns the body — don't re-face the practice target this frame and twitch on
            // the transition before the sub-behavior is interrupted. Otherwise the bow behavior is
            // the sole facing writer here (the idle rotation + head-look now yield to it).
            if (_combatMovement == null || !_combatMovement.IsInCombat)
                FaceTargetAndLock(_targetCenterPosition);

            // Wait between shots
            if (Time.time - _lastShotTime >= ShotInterval)
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
                    _retrievedAt = Time.time;
                }

                // The target drops the arrows at its return point a moment later: pick them up into storage.
                if (!_hasPickedUpArrows && Time.time - _retrievedAt > 0.6f)
                {
                    PickUpReturnedArrows();
                    _hasPickedUpArrows = true;
                }

                // Wait a moment after retrieving arrows
                if (_hasPickedUpArrows && Time.time - _phaseStartTime > ArrowRetrieveTime)
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
                staminaCost = _stats.GetAttackStaminaCost(staminaCost, Skills.SkillType.Bows);
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
        
        // A real arrow out of storage on the arc that reaches the bullseye (0.2.244, ArcheryBrain.Shoot: the arrow's own projectile and
        // gravity, the bow's speed; the target counts it for its return). The old practice shot spent no arrow, yet the target handed
        // arrows back for it.
        private void SpawnPracticeArrow()
        {
            string why = AI.ArcheryBrain.Shoot(_character, _plan, _inventory?.GetStorageInventory(), _shotsFired + 1);
            if (why != "")
            {
                Debug.Log($"[BowTraining] {Companion.companionName} cannot shoot: {why}");
                _totalShots = _shotsFired;   // ends the session at the next count: walk over and collect what hit
                return;
            }
            _inventory?.SaveToZDO();
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
        
        #endregion
        
        #region Helpers
        
        /// <summary>Crossbows are ItemType Bow too, but train the Crossbows skill, not Bows (one rule with the bot: ArcheryBrain).</summary>
        private static bool IsTrainingBow(ItemDrop.ItemData item) => AI.ArcheryBrain.IsTrainingBow(item);

        // The training bow wherever it is equipped (hand or back), or null.
        private ItemDrop.ItemData TrainingBow()
        {
            if (_inventory == null) return null;
            foreach (var slot in new[] { CompanionInventory.EquipmentSlot.LeftHand, CompanionInventory.EquipmentSlot.LeftBack, CompanionInventory.EquipmentSlot.RightBack })
            {
                var item = _inventory.GetEquippedItem(slot);
                if (IsTrainingBow(item)) return item;
            }
            return null;
        }

        /// <summary>A crossbow held in the left hand would be overwritten by equipping a bow from the back.</summary>
        private static bool IsCrossbowType(ItemDrop.ItemData item)
        {
            return item?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Bow && !IsTrainingBow(item);
        }

        private bool HasBowEquipped()
        {
            if (_inventory == null) return false;
            
            // Check left hand (where bows are equipped when in use)
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (IsTrainingBow(leftHand))
                return true;
            
            if (IsCrossbowType(leftHand))
                return false;
            
            // Check left back (holstered bow)
            var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
            if (IsTrainingBow(leftBack))
                return true;
            
            // Also check right back (some configurations might put bow there)
            var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
            if (IsTrainingBow(rightBack))
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
            if (IsTrainingBow(leftHand))
            {
                // Bow is already equipped, but we should still unequip right hand weapon
                UnequipRightHandWeapon();
                // Force visual update to ensure visuals match inventory state
                FinalizeEquipmentChange();
                return;
            }
            if (IsCrossbowType(leftHand)) return;
            
            // First, unequip any weapon from right hand (move to back or storage)
            UnequipRightHandWeapon();
            
            // Check if bow is holstered on LeftBack first (primary location for bows)
            var leftBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
            if (IsTrainingBow(leftBack))
            {
                EquipBowFromSlot(CompanionInventory.EquipmentSlot.LeftBack, leftBack);
                return;
            }
            
            // Check if bow is holstered on RightBack (secondary location)
            var rightBack = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
            if (IsTrainingBow(rightBack))
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
            
            return IsTrainingBow(_inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand));
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
                // ArcheryTarget.Interact drops the arrows that hit at its return point (ArcheryBrain logs hits and points).
                AI.ArcheryBrain.CollectArrows(_humanoid, _plan);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[BowTraining] Failed to retrieve arrows: {ex.Message}");
            }
        }

        // The arrows the target dropped, into storage (the companions' loose-item take).
        private void PickUpReturnedArrows()
        {
            var storage = _inventory?.GetStorageInventory();
            string arrow = _plan?.Arrows?.m_dropPrefab != null ? _plan.Arrows.m_dropPrefab.name : null;
            if (storage == null || arrow == null) return;
            int taken = 0;
            foreach (ItemDrop drop in ChestHelper.FindLooseItems(_plan.ReturnPoint, 3f))
            {
                if (drop == null || Utils.GetPrefabName(drop.gameObject) != arrow) continue;
                taken += ChestHelper.TryTakeLooseItem(drop, storage);
            }
            if (taken > 0) _inventory.SaveToZDO();
            Debug.Log($"[BowTraining] {Companion.companionName} picked up {taken} {arrow} at the target");
        }
        
        // The nearest archery target around the search center (one finder with the bot: ArcheryBrain.TargetsNear).
        private ArcheryTarget FindNearbyArcheryTarget() =>
            AI.ArcheryBrain.TargetsNear(SearchCenter, GetEffectiveSearchRadius(TargetDetectionRange)).FirstOrDefault();
        
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

            // Single facing-writer: own facing at SubBehavior priority while training, snap-locked onto
            // the target. Combat's enemy-facing (Combat-70) still preempts if a fight starts. Direct
            // write only as a no-authority fallback.
            var facing = Companion != null ? Companion.GetFacingAuthority() : null;
            if (facing != null)
            {
                if (facing.TryAcquireFacing(FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.SubBehavior, BehaviorName, 0.4f))
                    facing.SetLookDirection(BehaviorName, dir, instant: true);
                return;
            }

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
