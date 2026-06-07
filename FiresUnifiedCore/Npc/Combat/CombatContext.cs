using UnityEngine;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Combat
{
  /// <summary>
    /// Shared context data passed to all weapon behaviors.
    /// Now delegates to CompanionEquipmentData for all weapon/equipment information.
    /// This is a lightweight wrapper that provides combat-specific access to the data.
    /// 
    /// ANIMATION LOCK SYSTEM:
    /// Prevents animations from being interrupted mid-play.
    /// - Use TryLockAnimation() before starting any animation
    /// - Animation will complete before another can start
    /// - Emergency overrides (dodge, block) can interrupt if needed
    /// </summary>
    public class CombatContext
    {
        // Core components
        public CompanionController Companion { get; set; }
        public CompanionInventory Inventory { get; set; }
        public CompanionSkills CompanionSkills { get; set; }
        public Humanoid Humanoid { get; set; }
        public CompanionAI CompanionAI { get; set; }
        public Character Character { get; set; }
        public Animator Animator { get; set; }
        public ZNetView NView { get; set; }
        public ZSyncAnimation ZAnim { get; set; }
        public CharacterAnimEvent AnimEvent { get; set; }
        public Transform Transform { get; set; }
        
        // Reference to the centralized equipment data system
        public CompanionEquipmentData EquipmentData { get; set; }
        
        // Reference to threat analyzer for tactical decisions
        public ThreatAnalyzer ThreatAnalyzer { get; set; }
        
        // ============================================
 // ANIMATION LOCK SYSTEM
     // ============================================
        
      private bool _isAnimationLocked;
        private float _animationLockEndTime;
     private string _currentAnimationName;
        private AnimationPriority _currentAnimationPriority;
        
        /// <summary>
        /// Priority levels for animations. Higher priority can interrupt lower.
    /// </summary>
        public enum AnimationPriority
   {
   None = 0,
       Movement = 1,       // Walking, idle transitions
 Attack = 2,         // Melee swings, bow shots
            Block = 3,// Blocking stance
            Dodge = 4,        // Dodge rolls - can interrupt attacks
     Jump = 4,      // Jumping - same priority as dodge
            EmergencyDodge = 5, // Emergency dodge from high damage - highest priority
            Death = 10          // Death animation - always plays
        }
   
  /// <summary>
        /// Returns true if an animation is currently locked and should not be interrupted.
        /// </summary>
        public bool IsAnimationLocked
        {
       get
            {
                if (!_isAnimationLocked) return false;
        
           // Check if lock has expired
    if (Time.time >= _animationLockEndTime)
       {
         _isAnimationLocked = false;
       _currentAnimationName = null;
              _currentAnimationPriority = AnimationPriority.None;
        return false;
  }
     
  return true;
    }
        }
        
        /// <summary>
      /// Gets the remaining time on the current animation lock.
     /// </summary>
  public float AnimationLockRemainingTime => 
       _isAnimationLocked ? Mathf.Max(0f, _animationLockEndTime - Time.time) : 0f;
        
        /// <summary>
        /// Gets the name of the currently locked animation.
        /// </summary>
        public string CurrentLockedAnimation => _isAnimationLocked ? _currentAnimationName : null;
      
      /// <summary>
    /// Gets the priority of the currently locked animation.
    /// </summary>
        public AnimationPriority CurrentAnimationPriority => 
            _isAnimationLocked ? _currentAnimationPriority : AnimationPriority.None;
      
        /// <summary>
        /// Attempts to lock for a new animation. Returns true if successful.
  /// Will fail if a higher or equal priority animation is already playing.
    /// </summary>
        /// <param name="animationName">Name of the animation (for debugging)</param>
 /// <param name="duration">How long to lock for</param>
        /// <param name="priority">Priority level of this animation</param>
        /// <returns>True if lock acquired, false if blocked by existing animation</returns>
        public bool TryLockAnimation(string animationName, float duration, AnimationPriority priority = AnimationPriority.Attack)
    {
   // If not currently locked, acquire lock
        if (!IsAnimationLocked)
            {
      SetAnimationLock(animationName, duration, priority);
           return true;
        }
          
    // If new animation has higher priority, it can interrupt
   if (priority > _currentAnimationPriority)
   {
         if (CompanionCombat.VerboseLogging)
           {
    Debug.Log($"[CombatContext] Animation '{animationName}' (priority {priority}) interrupting '{_currentAnimationName}' (priority {_currentAnimationPriority})");
                }
      SetAnimationLock(animationName, duration, priority);
         return true;
     }
    
            // Cannot acquire lock - existing animation has equal or higher priority
   if (CompanionCombat.VerboseLogging)
      {
         Debug.Log($"[CombatContext] Animation '{animationName}' blocked by '{_currentAnimationName}' ({AnimationLockRemainingTime:F2}s remaining)");
            }
            return false;
        }
   
   /// <summary>
        /// Forces an animation lock regardless of current state. Use sparingly.
        /// </summary>
        public void ForceAnimationLock(string animationName, float duration, AnimationPriority priority)
    {
            SetAnimationLock(animationName, duration, priority);
   }
        
        /// <summary>
        /// Releases the current animation lock early.
   /// </summary>
        public void ReleaseAnimationLock()
        {
    _isAnimationLocked = false;
    _currentAnimationName = null;
     _currentAnimationPriority = AnimationPriority.None;
            _animationLockEndTime = 0f;
        }
  
        /// <summary>
        /// Checks if an animation with the given priority can play right now.
 /// Does not acquire a lock - use TryLockAnimation for that.
   /// </summary>
        public bool CanPlayAnimation(AnimationPriority priority)
        {
            if (!IsAnimationLocked) return true;
          return priority > _currentAnimationPriority;
   }
     
      private void SetAnimationLock(string animationName, float duration, AnimationPriority priority)
     {
  _isAnimationLocked = true;
 _animationLockEndTime = Time.time + duration;
            _currentAnimationName = animationName;
            _currentAnimationPriority = priority;
        }
        
        // ============================================
        // WEAPON DATA - Delegated to EquipmentData
        // ============================================
        
     // Current weapon data (direct references for compatibility)
   public ItemDrop.ItemData CurrentWeapon => EquipmentData?.WeaponItem;
   public Attack CurrentAttack => EquipmentData?.PrimaryAttack;
        public GameObject WeaponPrefab => EquipmentData?.WeaponPrefab;
      public ItemDrop WeaponItemDrop => EquipmentData?.WeaponItemDrop;
        
  // Attack settings from EquipmentData
     public float AIAttackRange => EquipmentData?.AIAttackRange ?? 2.5f;
        public float AIAttackRangeMin => EquipmentData?.AIAttackRangeMin ?? 0f;
        public float AIAttackInterval => EquipmentData?.AIAttackInterval ?? 2f;
        public float AIAttackMaxAngle => EquipmentData?.AIAttackMaxAngle ?? 5f;
   public float AttackStamina => EquipmentData?.AttackStamina ?? 20f;
        public int AttackChainLevels => EquipmentData?.AttackChainLevels ?? 1;
        public int AttackRandomAnimations => EquipmentData?.AttackRandomAnimations ?? 1;
        public string AttackAnimation => EquipmentData?.AttackAnimation ?? "";
      public Attack.AttackType AttackType => EquipmentData?.AttackType ?? Attack.AttackType.Horizontal;

        // Bow/Crossbow settings from EquipmentData
        public bool IsBowDraw => EquipmentData?.IsBowDraw ?? false;
        public float DrawDurationMin => EquipmentData?.DrawDurationMin ?? DefaultBowDrawTime;
        public string DrawAnimationState => EquipmentData?.DrawAnimationState ?? "";
public bool RequiresReload => EquipmentData?.RequiresReload ?? false;
     public float ReloadTime => EquipmentData?.ReloadTime ?? DefaultCrossbowReloadTime;
        public string ReloadAnimation => EquipmentData?.ReloadAnimation ?? "";
        public float ProjectileVelocity => EquipmentData?.ProjectileVelocity ?? 50f;
        public GameObject AttackProjectile => EquipmentData?.AttackProjectile;

        // Block/Shield settings from EquipmentData
        public float BlockPower => EquipmentData?.ShieldBlockPower ?? 0f;
        public float BlockPowerPerLevel => EquipmentData?.ShieldBlockPowerPerLevel ?? 0f;
      public float TimedBlockBonus => EquipmentData?.ShieldTimedBlockBonus ?? 1.5f;
        public float DeflectionForce => EquipmentData?.ShieldDeflectionForce ?? 0f;
      public StatusEffect PerfectBlockStatusEffect => EquipmentData?.ShieldPerfectBlockEffect;

        // Animation state from EquipmentData
        public ItemDrop.ItemData.AnimationState WeaponAnimationState => EquipmentData?.WeaponAnimationState ?? ItemDrop.ItemData.AnimationState.Unarmed;

        // ============================================
        // CONFIGURABLE DEFAULTS
        // ============================================

        // Combat settings (from CompanionCombat)
        public float BaseAttackCooldown { get; set; } = 2f;
        public float AttackRange { get; set; } = 2.5f;
     public float AttackAngle { get; set; } = 90f;
public bool UseNativeAttackSystem { get; set; } = true;

        // Ranged settings
   public float RangedAttackRange { get; set; } = 25f;
        public float RangedMinRange { get; set; } = 12f;
     public float DefaultBowDrawTime { get; set; } = 1.5f;
        public float DefaultCrossbowReloadTime { get; set; } = 2f;
  public float BowAimHoldTime { get; set; } = 0.3f;
        public float EarlyReleaseMinDraw { get; set; } = 0.5f;
        public float EarlyReleaseThreatRange { get; set; } = 6f;
        public float RangedRetreatRange { get; set; } = 8f;

        // Blocking settings
 public float BlockChance { get; set; } = 0.3f;
        public float ParryWindow { get; set; } = 0.25f;
     public float ParryChance { get; set; } = 0.15f;
  public float BlockDamageReduction { get; set; } = 0.5f;
  public float ParryDamageReduction { get; set; } = 0.9f;

        // Dodge settings
        public float DodgeChance { get; set; } = 0.2f;
        public float DodgeCooldown { get; set; } = 3f;
        public float DodgeDistance { get; set; } = 4f;
        public float DodgeThreatRange { get; set; } = 5f;

        // Animation state hashes
        public static readonly int Hash_statef = Animator.StringToHash("statef");
     public static readonly int Hash_statei = Animator.StringToHash("statei");
public static readonly int Hash_blocking = Animator.StringToHash("blocking");
        public static readonly int Hash_attacking = Animator.StringToHash("attacking");
    public static readonly int Hash_attack = Animator.StringToHash("attack");
        public static readonly int Hash_attack_type = Animator.StringToHash("attack_type");
        public static readonly int Hash_bow_aim = Animator.StringToHash("bow_aim");
        public static readonly int Hash_forward_speed = Animator.StringToHash("forward_speed");
        public static readonly int Hash_drawpercent = Animator.StringToHash("drawpercent");

    /// <summary>
        /// Updates attack range and cooldown from equipment data.
        /// Call this after equipment changes.
        /// </summary>
public void RefreshFromEquipmentData()
        {
            if (EquipmentData == null) return;
    
     // Update attack range from weapon
  AttackRange = EquipmentData.AttackRange > 0 ? EquipmentData.AttackRange : 2.5f;
   AttackAngle = EquipmentData.AttackAngle > 0 ? EquipmentData.AttackAngle : 90f;
          
      // Update cooldown from AI interval
       if (EquipmentData.AIAttackInterval > 0)
        {
    BaseAttackCooldown = EquipmentData.AIAttackInterval;
            }
            else if (EquipmentData.AttackStamina > 0)
       {
            BaseAttackCooldown = Mathf.Max(0.5f, EquipmentData.AttackStamina / 20f);
        }
        }

        /// <summary>
        /// Gets the estimated hit delay based on weapon animation state.
        /// Delegates to EquipmentData.
        /// </summary>
        public float GetEstimatedHitDelay()
        {
            return EquipmentData?.GetWeaponHitDelay() ?? 0.3f;
        }

        /// <summary>
    /// Gets the estimated attack duration.
   /// Delegates to EquipmentData.
        /// </summary>
        public float GetEstimatedAttackDuration()
        {
            return EquipmentData?.GetWeaponAttackDuration() ?? 1f;
        }

        /// <summary>
        /// Checks if the animator has a specific parameter.
        /// </summary>
        public bool HasAnimatorParameter(string paramName)
        {
       if (Animator == null) return false;

          foreach (var param in Animator.parameters)
            {
    if (param.name == paramName)
          return true;
     }
            return false;
      }

        /// <summary>
        /// Gets the skill type for the current weapon.
      /// </summary>
        public global::Skills.SkillType GetWeaponSkillType()
        {
      return EquipmentData?.WeaponSkillType ?? global::Skills.SkillType.Unarmed;
        }

 /// <summary>
        /// Plays an attack animation using the best available method.
        /// Now checks animation lock before playing.
        /// </summary>
        /// <param name="animTrigger">Animation trigger name</param>
        /// <param name="attackIndex">Attack chain index</param>
        /// <param name="duration">Duration to lock animation (default: auto-calculated)</param>
        /// <param name="priority">Animation priority (default: Attack)</param>
        /// <returns>True if animation was played, false if blocked</returns>
        public bool PlayAttackAnimation(string animTrigger, int attackIndex, float duration = -1f, AnimationPriority priority = AnimationPriority.Attack)
        {
   if (Animator == null && ZAnim == null)
       {
        if (CompanionCombat.VerboseLogging)
         Debug.LogWarning("[CombatContext] No animator available for attack animation!");
      return false;
 }

            // Calculate duration if not specified
if (duration < 0f)
  {
        duration = GetEstimatedAttackDuration();
            }

            // Try to acquire animation lock
        if (!TryLockAnimation(animTrigger, duration, priority))
            {
   return false;
            }

            try
         {
                if (ZAnim != null)
    {
         try
       {
     ZAnim.SetTrigger(animTrigger);
    if (CompanionCombat.VerboseLogging)
             Debug.Log($"[CombatContext] ZSyncAnimation SetTrigger: '{animTrigger}'");
return true;
        }
 catch { }
     }

           if (Animator != null)
           {
    if (HasAnimatorParameter(animTrigger))
           {
        Animator.SetTrigger(animTrigger);
    if (CompanionCombat.VerboseLogging)
    Debug.Log($"[CombatContext] Animator SetTrigger: '{animTrigger}'");
      return true;
       }

        int stateHash = UnityEngine.Animator.StringToHash(animTrigger);
      if (Animator.HasState(0, stateHash))
            {
         Animator.CrossFadeInFixedTime(stateHash, 0.1f, 0);
    if (CompanionCombat.VerboseLogging)
  Debug.Log($"[CombatContext] Animator CrossFade to state: '{animTrigger}'");
    return true;
      }

         try
              {
              Animator.Play(animTrigger, 0, 0f);
    if (CompanionCombat.VerboseLogging)
     Debug.Log($"[CombatContext] Animator Play state: '{animTrigger}'");
    return true;
             }
        catch (System.Exception ex)
      {
             Debug.LogWarning($"[CombatContext] Could not play animation '{animTrigger}': {ex.Message}");
 }
                }
          }
catch (System.Exception ex)
         {
       Debug.LogWarning($"[CombatContext] Attack animation failed: {ex.Message}");
         }
       
            // If we failed to play, release the lock
            ReleaseAnimationLock();
            return false;
   }

    /// <summary>
        /// Legacy overload for backwards compatibility.
        /// </summary>
        public void PlayAttackAnimation(string animTrigger, int attackIndex)
    {
            PlayAttackAnimation(animTrigger, attackIndex, -1f, AnimationPriority.Attack);
        }

        /// <summary>
        /// Gets the attack animation trigger name with chain/random handling.
        /// </summary>
        public string GetAttackAnimationTrigger(int chainLevel)
  {
     string animName = AttackAnimation;
    
            if (!string.IsNullOrEmpty(animName))
        {
  if (AttackChainLevels > 1)
        {
        animName = $"{AttackAnimation}{chainLevel}";
     }
          else if (AttackRandomAnimations > 1)
             {
             int randomIndex = UnityEngine.Random.Range(0, AttackRandomAnimations);
   animName = $"{AttackAnimation}{randomIndex}";
     }

         return animName;
   }

            return GetGenericAttackTrigger();
   }

        /// <summary>
        /// Gets a generic attack trigger based on weapon animation state.
        /// </summary>
        public string GetGenericAttackTrigger()
  {
            return WeaponAnimationState switch
        {
     ItemDrop.ItemData.AnimationState.Unarmed => "unarmed_attack0",
      ItemDrop.ItemData.AnimationState.OneHanded => "swing_longsword0",
            ItemDrop.ItemData.AnimationState.TwoHandedClub => "swing_sledge",
          ItemDrop.ItemData.AnimationState.TwoHandedAxe => "battleaxe_attack0",
                ItemDrop.ItemData.AnimationState.Greatsword => "greatsword_attack0",
       ItemDrop.ItemData.AnimationState.Atgeir => "atgeir_attack0",
     ItemDrop.ItemData.AnimationState.Knives => "knife_stab0",
            ItemDrop.ItemData.AnimationState.DualAxes => "dualaxes_attack0",
      ItemDrop.ItemData.AnimationState.Scythe => "scythe_attack0",
                ItemDrop.ItemData.AnimationState.Bow => "bow_fire",
       ItemDrop.ItemData.AnimationState.Crossbow => "crossbow_fire",
         ItemDrop.ItemData.AnimationState.Staves => "staff_rapidfire",
        ItemDrop.ItemData.AnimationState.MagicItem => "staff_summon",
       ItemDrop.ItemData.AnimationState.Torch => "swing_longsword0",
             _ => "swing_longsword0"
      };
        }

        /// <summary>
        /// Gets the animation state index for the current weapon.
        /// </summary>
        public int GetAttackAnimationIndex()
        {
            return (int)WeaponAnimationState;
     }

        /// <summary>
 /// Creates HitData for damage dealing.
        /// Delegates to EquipmentData for accurate damage calculation.
    /// </summary>
   public HitData CreateHitData(Character target, float damageMultiplier = 1f)
     {
      if (EquipmentData != null && Character != null)
  {
   return EquipmentData.CreateWeaponHitData(target, Character, damageMultiplier);
  }
            
            // Fallback if no equipment data
            HitData hit = new HitData();
  hit.m_point = target.transform.position + Vector3.up;
            hit.m_dir = (target.transform.position - Transform.position).normalized;
            hit.m_attacker = Character?.GetZDOID() ?? ZDOID.None;
       hit.m_skill = GetWeaponSkillType();
        hit.m_damage.m_blunt = 5f * damageMultiplier;
            return hit;
     }

      /// <summary>
        /// Broadcasts an RPC to all clients.
/// </summary>
        public void BroadcastRPC(string rpcName, params object[] args)
    {
     if (NView == null || !NView.IsOwner()) return;

    if (args.Length == 0)
     NView.InvokeRPC(ZNetView.Everybody, rpcName);
   else if (args.Length == 1)
    NView.InvokeRPC(ZNetView.Everybody, rpcName, args[0]);
else if (args.Length == 2)
       NView.InvokeRPC(ZNetView.Everybody, rpcName, args[0], args[1]);
        }
    }
}
