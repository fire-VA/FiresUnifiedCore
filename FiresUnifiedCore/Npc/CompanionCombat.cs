using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.Combat;

namespace FiresCore.Npc
{
    /// <summary>
    /// Handles combat mechanics for companion NPCs.
    /// Manages weapon attacks, armor stats, and combat animations.
    /// Delegates weapon-specific behavior to WeaponBehavior subclasses.
    /// Uses CompanionEquipmentData for all equipment information.
    /// </summary>
    public class CompanionCombat : MonoBehaviour
    {
   [Header("Combat Settings")]
     public float baseAttackCooldown = 2f;
        public float attackRange = 2.5f;
        public float attackAngle = 90f;
      public bool useNativeAttackSystem = true;

        [Header("Ranged Settings")]
        public float rangedAttackRange = 25f;
        public float rangedMinRange = 12f;
     public float defaultBowDrawTime = 1.5f;
        public float defaultCrossbowReloadTime = 2f;
    public float bowAimHoldTime = 0.3f;
        public float earlyReleaseMinDraw = 0.5f;
public float earlyReleaseThreatRange = 6f;
        public float rangedRetreatRange = 8f;

        [Header("Blocking Settings")]
 public float blockChance = 0.3f;
        public float parryWindow = 0.25f;
        public float parryChance = 0.15f;
      public float blockStaminaCost = 10f;
        public float blockDamageReduction = 0.5f;
        public float parryDamageReduction = 0.9f;

   [Header("Dodge Settings")]
  public float dodgeChance = 0.2f;
   public float dodgeCooldown = 3f;
        public float dodgeDistance = 4f;
        public float dodgeThreatRange = 5f;
public float dodgeStaminaCost = 15f;

  [Header("Aggression Settings")]
    public float threatDetectionRange = 30f;
        public float threatUpdateInterval = 0.5f;
        public float aggroStickyTime = 5f;
   public float opportunisticAttackRange = 1.5f;

        [Header("Movement Settings")]
        public float attackMovementSpeedMultiplier = 0.4f;

        [Header("Debug")]
        public static bool VerboseLogging = false;

        // Core components
        private CompanionController _companion;
        private CompanionInventory _inventory;
        private CompanionSkills _skills;
        private Humanoid _humanoid;
        private MonsterAI _monsterAI;
private Character _character;
  private Animator _animator;
        private ZNetView _nview;
        private ZSyncAnimation _zanim;
        private CharacterAnimEvent _animEvent;

        // Equipment data system
        private CompanionEquipmentData _equipmentData;

        // Combat context shared with behaviors
        private CombatContext _context;

      // Weapon behaviors
        private Dictionary<WeaponType, WeaponBehavior> _behaviors;
    private WeaponBehavior _activeBehavior;
        private BlockingBehavior _blockingBehavior;
        private DodgeBehavior _dodgeBehavior;

        // Weapon state
        private WeaponType _weaponType = WeaponType.Unarmed;

        // Threat tracking
      private float _lastThreatUpdate;
private Character _currentThreat;
        private float _lastSeenThreatTime;

        // Original AI settings backup state. The original-* values turned
        // out never to be restored (companion AI overrides them every frame),
        // so the backup fields are gone â€” only the "have we run BackupAISettings
        // once for this monster AI" guard remains.
        private bool _aiSettingsBackedUp;

        public enum WeaponType
        {
            Unarmed,
       OneHandedMelee,
            TwoHandedMelee,
 Bow,
       Crossbow,
            Staff,
       Tool
        }

        #region Unity Lifecycle

        private void Awake()
        {
   _companion = GetComponent<CompanionController>();
       _inventory = GetComponent<CompanionInventory>();
      _skills = GetComponent<CompanionSkills>();
          _humanoid = GetComponent<Humanoid>();
      _monsterAI = GetComponent<MonsterAI>();
            _character = GetComponent<Character>();
            _animator = GetComponentInChildren<Animator>(true);
      _nview = GetComponent<ZNetView>();
 _zanim = GetComponent<ZSyncAnimation>();
_animEvent = GetComponentInChildren<CharacterAnimEvent>(true);
      
         // Get or add the equipment data component
            _equipmentData = GetComponent<CompanionEquipmentData>();
            if (_equipmentData == null)
            {
  _equipmentData = gameObject.AddComponent<CompanionEquipmentData>();
            }

InitializeCombatSystem();

            if (VerboseLogging)
        {
     Debug.Log($"[CompanionCombat] Awake - Components found: " +
   $"Humanoid={_humanoid != null}, MonsterAI={_monsterAI != null}, " +
        $"Animator={_animator != null}, ZSyncAnimation={_zanim != null}, " +
   $"CharacterAnimEvent={_animEvent != null}, EquipmentData={_equipmentData != null}");
       }
        }

        private void Start()
        {
if (_nview != null)
 {
     _nview.Register<string, int>("RPC_CompanionAttack", RPC_CompanionAttack);
    _nview.Register<bool>("RPC_CompanionBlock", RPC_CompanionBlock);
     _nview.Register<bool>("RPC_CompanionBowAim", RPC_CompanionBowAim);
    _nview.Register("RPC_CompanionDodge", RPC_CompanionDodge);
            }

   // Subscribe to equipment changes
      if (_equipmentData != null)
       {
    _equipmentData.OnEquipmentChanged += OnEquipmentDataChanged;
       }

     BackupAISettings();

      if (VerboseLogging && _animator != null)
       {
      LogAnimatorParameters();
         }
        }

        private void OnDestroy()
        {
            if (_equipmentData != null)
        {
                _equipmentData.OnEquipmentChanged -= OnEquipmentDataChanged;
            }
        }

        private void Update()
     {
        if (!_companion?.isTamed ?? true) return;
            if (_nview == null || !_nview.IsValid()) return;
if (_companion.isDefeated) return;

            UpdateCombat();
            UpdateThreatDetection();

       // Update behaviors
   _activeBehavior?.Update();
            _blockingBehavior?.Update(_activeBehavior?.IsAttacking ?? false, _dodgeBehavior?.IsDodging ?? false, _weaponType);
    _dodgeBehavior?.Update(_activeBehavior?.IsAttacking ?? false, _weaponType, CancelBowDrawIfNeeded);

            UpdateOpportunisticAttacks();
       
 // Update food timers
     _equipmentData?.UpdateFoodTimers();
 }

        private void InitializeCombatSystem()
        {
       // Create combat context
            _context = new CombatContext
{
     Companion = _companion,
   Inventory = _inventory,
  CompanionSkills = _skills,
          Humanoid = _humanoid,
           MonsterAI = _monsterAI,
           Character = _character,
         Animator = _animator,
          NView = _nview,
      ZAnim = _zanim,
  AnimEvent = _animEvent,
  Transform = transform,
        EquipmentData = _equipmentData,

     // Copy settings
  BaseAttackCooldown = baseAttackCooldown,
         AttackRange = attackRange,
      AttackAngle = attackAngle,
    UseNativeAttackSystem = useNativeAttackSystem,
              RangedAttackRange = rangedAttackRange,
        RangedMinRange = rangedMinRange,
 DefaultBowDrawTime = defaultBowDrawTime,
       DefaultCrossbowReloadTime = defaultCrossbowReloadTime,
          BowAimHoldTime = bowAimHoldTime,
              EarlyReleaseMinDraw = earlyReleaseMinDraw,
          EarlyReleaseThreatRange = earlyReleaseThreatRange,
                RangedRetreatRange = rangedRetreatRange,
         BlockChance = blockChance,
        ParryWindow = parryWindow,
         ParryChance = parryChance,
    BlockDamageReduction = blockDamageReduction,
         ParryDamageReduction = parryDamageReduction,
         DodgeChance = dodgeChance,
     DodgeCooldown = dodgeCooldown,
      DodgeDistance = dodgeDistance,
           DodgeThreatRange = dodgeThreatRange
            };

     // Create support behaviors first so we can wire them up
            _dodgeBehavior = new DodgeBehavior();
            _dodgeBehavior.Initialize(_context, this);

            _blockingBehavior = new BlockingBehavior();
          _blockingBehavior.Initialize(_context, this);

            // Create weapon behaviors
        var bowBehavior = new BowBehavior();
         var crossbowBehavior = new CrossbowBehavior();

      _behaviors = new Dictionary<WeaponType, WeaponBehavior>
         {
        { WeaponType.Unarmed, new UnarmedBehavior() },
        { WeaponType.OneHandedMelee, new MeleeBehavior() },
    { WeaponType.TwoHandedMelee, new MeleeBehavior() },
            { WeaponType.Bow, bowBehavior },
      { WeaponType.Crossbow, crossbowBehavior },
 { WeaponType.Staff, new StaffBehavior() },
            { WeaponType.Tool, new MeleeBehavior() }
            };

    // Initialize all behaviors
            foreach (var behavior in _behaviors.Values)
    {
           behavior.Initialize(_context, this);
      }

      // Wire up bidirectional references between dodge and ranged behaviors
            bowBehavior.SetDodgeBehavior(_dodgeBehavior);
crossbowBehavior.SetDodgeBehavior(_dodgeBehavior);
    _dodgeBehavior.SetBowBehavior(bowBehavior);

       // Start with unarmed
          _activeBehavior = _behaviors[WeaponType.Unarmed];
         _activeBehavior.OnActivate();
        }

        private void BackupAISettings()
  {
       if (_monsterAI == null || _aiSettingsBackedUp) return;

   _aiSettingsBackedUp = true;
    }

        #endregion

        #region Combat Update

        /// <summary>
        /// Called when equipment data changes.
        /// </summary>
        private void OnEquipmentDataChanged()
        {
  if (_equipmentData == null) return;
            
       // Determine weapon type from animation state
      WeaponType newType = DetermineWeaponTypeFromAnimState(_equipmentData.WeaponAnimationState);
            
if (newType != _weaponType)
            {
        _weaponType = newType;

    // Switch behavior
   _activeBehavior?.OnDeactivate();
    _activeBehavior = _behaviors.TryGetValue(newType, out var behavior) ? behavior : _behaviors[WeaponType.Unarmed];
     _activeBehavior.OnActivate();
      _activeBehavior.UpdateAttackData();

          // Update animation state
      UpdateAnimationState();
            }
 
          // Refresh context from equipment data
  _context.RefreshFromEquipmentData();

            if (VerboseLogging)
    {
          Debug.Log($"[CompanionCombat] Equipment changed - Weapon: {_equipmentData.WeaponShared?.m_name ?? "Unarmed"}, Type: {_weaponType}");
          }
        }

   private void UpdateCombat()
        {
            // Check if equipment needs refresh (inventory might have changed)
 CheckForEquipmentChanges();

    // Check if we should attack
          if (_activeBehavior != null && !_blockingBehavior.IsBlocking && !_dodgeBehavior.IsDodging)
  {
     var target = _monsterAI?.GetTargetCreature();
                if (target != null && _activeBehavior.ShouldAttack(target))
       {
       if (_nview != null && _nview.IsOwner())
    {
           _blockingBehavior.StopBlocking();
       _activeBehavior.ExecuteAttack(target);
      }
      }
            }
  }

        private void CheckForEquipmentChanges()
        {
            if (_inventory == null || _equipmentData == null) return;

  // Check if weapon changed
 var rightHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            var leftHand = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);

    ItemDrop.ItemData currentWeapon = null;
 if (leftHand != null && (IsBow(leftHand) || leftHand.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft))
   {
         currentWeapon = leftHand;
            }
            else if (rightHand != null && rightHand.IsWeapon())
     {
       currentWeapon = rightHand;
            }
     else if (leftHand != null && leftHand.IsWeapon())
         {
   currentWeapon = leftHand;
   }

      // If weapon changed, refresh equipment data
 if (currentWeapon != _equipmentData.WeaponItem)
    {
          _equipmentData.RefreshAllEquipmentData();
   }
        }

        private void UpdateOpportunisticAttacks()
 {
            if (_activeBehavior?.IsAttacking ?? false) return;
            if (_dodgeBehavior?.IsDodging ?? false) return;
            if (_weaponType == WeaponType.Bow || _weaponType == WeaponType.Crossbow) return;

            // Find any enemy very close to us
            var characters = Character.GetAllCharacters();
      foreach (var character in characters)
            {
  if (character == null || character.IsDead()) continue;
         if (character == _character) continue;
      if (!IsValidTarget(character)) continue;

              float dist = Vector3.Distance(transform.position, character.transform.position);
      if (dist <= opportunisticAttackRange)
        {
       SetTargetCreature(character);
           if (VerboseLogging)
             {
  Debug.Log($"[CompanionCombat] Opportunistic attack on {character.m_name} at {dist:F1}m");
           }
   _activeBehavior?.ExecuteAttack(character);
   return;
             }
 }
        }

        private WeaponType DetermineWeaponTypeFromAnimState(ItemDrop.ItemData.AnimationState animState)
   {
            return animState switch
            {
       ItemDrop.ItemData.AnimationState.Unarmed => WeaponType.Unarmed,
  ItemDrop.ItemData.AnimationState.OneHanded => WeaponType.OneHandedMelee,
   ItemDrop.ItemData.AnimationState.TwoHandedClub => WeaponType.TwoHandedMelee,
          ItemDrop.ItemData.AnimationState.TwoHandedAxe => WeaponType.TwoHandedMelee,
  ItemDrop.ItemData.AnimationState.Greatsword => WeaponType.TwoHandedMelee,
            ItemDrop.ItemData.AnimationState.Atgeir => WeaponType.TwoHandedMelee,
          ItemDrop.ItemData.AnimationState.Knives => WeaponType.OneHandedMelee,
      ItemDrop.ItemData.AnimationState.DualAxes => WeaponType.TwoHandedMelee,
        ItemDrop.ItemData.AnimationState.Scythe => WeaponType.TwoHandedMelee,
          ItemDrop.ItemData.AnimationState.Bow => WeaponType.Bow,
          ItemDrop.ItemData.AnimationState.Crossbow => WeaponType.Crossbow,
       ItemDrop.ItemData.AnimationState.Staves => WeaponType.Staff,
         ItemDrop.ItemData.AnimationState.MagicItem => WeaponType.Staff,
         ItemDrop.ItemData.AnimationState.Torch => WeaponType.OneHandedMelee,
       _ => WeaponType.Unarmed
      };
    }

        private bool IsBow(ItemDrop.ItemData item)
      {
      if (item?.m_shared == null) return false;
            return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow ||
        item.m_shared.m_skillType == Skills.SkillType.Bows;
     }

        private void UpdateAnimationState()
        {
            if (_animator == null && _zanim == null) return;

            var animState = _equipmentData?.WeaponAnimationState ?? ItemDrop.ItemData.AnimationState.Unarmed;

  if (_zanim != null)
        {
    _zanim.SetFloat(CombatContext.Hash_statef, (float)animState);
      _zanim.SetInt(CombatContext.Hash_statei, (int)animState);
  }
            else if (_animator != null)
       {
              _animator.SetFloat("statef", (float)animState);
        _animator.SetInteger("statei", (int)animState);
            }

          if (VerboseLogging)
            {
             Debug.Log($"[CompanionCombat] Animation state set to: {animState} ({(int)animState})");
            }
   }

   private void LogAnimatorParameters()
        {
  if (!VerboseLogging) return;
         if (_animator == null) return;

 Debug.Log($"[CompanionCombat] Animator parameters for {gameObject.name}:");
   foreach (var param in _animator.parameters)
{
              Debug.Log($"  - {param.name} ({param.type})");
     }
        }

        private void CancelBowDrawIfNeeded()
        {
          if (_activeBehavior is BowBehavior bowBehavior && bowBehavior.IsBowDrawing)
     {
        bowBehavior.CancelAttack();
   }
  }

        #endregion

        #region Threat Detection

    private void UpdateThreatDetection()
        {
            if (Time.time - _lastThreatUpdate < threatUpdateInterval) return;
            _lastThreatUpdate = Time.time;

            if (_monsterAI == null || _character == null) return;

     var currentTarget = _monsterAI.GetTargetCreature();

     if (currentTarget != null && !currentTarget.IsDead())
          {
 _currentThreat = currentTarget;
          _lastSeenThreatTime = Time.time;
      return;
   }

         if (_currentThreat != null && !_currentThreat.IsDead() &&
    Time.time - _lastSeenThreatTime < aggroStickyTime)
     {
      float distToThreat = Vector3.Distance(transform.position, _currentThreat.transform.position);
     if (distToThreat < threatDetectionRange)
 {
         SetTargetCreature(_currentThreat);
  return;
 }
          }

      Character newThreat = FindNearestThreat();
  if (newThreat != null)
{
         _currentThreat = newThreat;
    _lastSeenThreatTime = Time.time;
                SetTargetCreature(newThreat);
      SetAlerted(true);

    if (VerboseLogging)
      {
           Debug.Log($"[CompanionCombat] Acquired new threat: {newThreat.m_name}");
   }
    }
        }

        private Character FindNearestThreat()
    {
          Character bestThreat = null;
            float bestScore = float.MaxValue;

       Vector3 ownerPos = transform.position;
            if (_companion != null)
{
           var owner = _companion.GetOwner();
   if (owner != null)
      {
    ownerPos = owner.transform.position;
                }
 }

            var characters = Character.GetAllCharacters();
        foreach (var character in characters)
            {
       if (character == null || character.IsDead()) continue;
  if (character == _character) continue;
             if (!IsValidTarget(character)) continue;

    float distToCompanion = Vector3.Distance(transform.position, character.transform.position);
         if (distToCompanion > threatDetectionRange) continue;

 float distToOwner = Vector3.Distance(ownerPos, character.transform.position);
        float score = Mathf.Min(distToCompanion, distToOwner);

       if (score < bestScore)
        {
          bestScore = score;
             bestThreat = character;
                }
 }

            return bestThreat;
        }

        private bool IsValidTarget(Character target)
        {
    if (target == null) return false;
            if (target.IsDead()) return false;
   if (target == _character) return false;
   if (target.IsTamed()) return false;
            if (target.IsPlayer()) return false;

  if (_monsterAI != null)
  {
            return BaseAI.IsEnemy(_character, target);
            }

     return true;
   }

        private void SetTargetCreature(Character target)
        {
  if (_monsterAI == null || target == null) return;

  try
       {
      var field = typeof(MonsterAI).GetField("m_targetCreature",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
 field?.SetValue(_monsterAI, target);

      var posField = typeof(MonsterAI).GetField("m_lastKnownTargetPos",
   System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    posField?.SetValue(_monsterAI, target.transform.position);

                var beenAtField = typeof(MonsterAI).GetField("m_beenAtLastPos",
  System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
 beenAtField?.SetValue(_monsterAI, false);
}
            catch (Exception ex)
  {
                if (VerboseLogging)
   Debug.LogWarning($"[CompanionCombat] Failed to set target: {ex.Message}");
    }
        }

      private void SetAlerted(bool alert)
        {
            if (_monsterAI == null) return;

    try
  {
        var method = typeof(BaseAI).GetMethod("SetAlerted",
         System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance |
 System.Reflection.BindingFlags.Public);
        method?.Invoke(_monsterAI, new object[] { alert });
       }
    catch (Exception ex)
    {
     if (VerboseLogging)
     Debug.LogWarning($"[CompanionCombat] Failed to set alerted: {ex.Message}");
    }
        }

      #endregion

        #region RPCs

     private void RPC_CompanionAttack(long sender, string animTrigger, int attackIndex)
        {
     if (_nview != null && _nview.IsOwner()) return;
      _context.PlayAttackAnimation(animTrigger, attackIndex);
   }

   private void RPC_CompanionBlock(long sender, bool blocking)
        {
            _blockingBehavior?.HandleBlockRPC(blocking);
        }

 private void RPC_CompanionBowAim(long sender, bool aiming)
        {
            if (_nview != null && _nview.IsOwner()) return;

   if (_zanim != null)
            {
      _zanim.SetBool(CombatContext.Hash_bow_aim, aiming);
            }
         else if (_animator != null && _context.HasAnimatorParameter("bow_aim"))
       {
     _animator.SetBool("bow_aim", aiming);
         }
        }

        private void RPC_CompanionDodge(long sender)
        {
            _dodgeBehavior?.HandleDodgeRPC();
        }

      #endregion

        #region Public API

        /// <summary>Gets the equipment data system.</summary>
        public CompanionEquipmentData GetEquipmentData() => _equipmentData;

     /// <summary>Gets the current weapon item.</summary>
        public ItemDrop.ItemData GetCurrentWeapon() => _equipmentData?.WeaponItem;

        public bool IsAttacking() => _activeBehavior?.IsAttacking ?? false ||
            (_activeBehavior is BowBehavior bow && bow.IsBowDrawing) ||
     (_activeBehavior is CrossbowBehavior xbow && xbow.IsReloading);

        public bool IsBlocking() => _blockingBehavior?.IsBlocking ?? false;

        public bool IsRangedWeapon() => _weaponType == WeaponType.Bow || _weaponType == WeaponType.Crossbow;

   public bool IsBowDrawing() => _activeBehavior is BowBehavior bow && bow.IsBowDrawing;

        public bool IsDodging() => _dodgeBehavior?.IsDodging ?? false;

        public WeaponType GetWeaponType() => _weaponType;

        /// <summary>Gets total armor from equipment.</summary>
        public float GetTotalArmor() => _equipmentData?.TotalArmor ?? 0f;

        /// <summary>Gets movement modifier from equipment.</summary>
        public float GetMovementModifier() => _equipmentData?.TotalMovementModifier ?? 0f;

 /// <summary>Calculates damage reduction using equipment armor.</summary>
        public float CalculateDamageReduction(float damage)
        {
            return _equipmentData?.CalculateDamageReduction(damage) ?? damage;
  }

/// <summary>Forces equipment data refresh.</summary>
   public void ReconfigureAIForWeapon()
     {
       _equipmentData?.RefreshAllEquipmentData();
        }

        public void ForceAttack()
        {
     var target = _monsterAI?.GetTargetCreature();
  if (target != null && !(_activeBehavior?.IsAttacking ?? false))
            {
             _activeBehavior?.ExecuteAttack(target);
        }
 }

    public void CancelAttack()
     {
            _activeBehavior?.CancelAttack();
         _blockingBehavior?.StopBlocking();
        }

        public float ProcessIncomingDamage(HitData hit)
        {
      return _blockingBehavior?.ProcessIncomingDamage(hit) ?? hit?.GetTotalDamage() ?? 0f;
}

     #endregion
    }
}
