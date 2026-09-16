using UnityEngine;
using System.Collections;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// One- and two-handed melee combat using each weapon's own timing data. Multi-chain weapons combo until a
    /// pause resets the chain, and secondary attacks go to staggered or blocking targets and occasionally for
    /// variety, with the target tracked through the combo.
    /// </summary>
    public class MeleeBehavior : WeaponBehavior
    {
    private bool _isTwoHanded;
  private Coroutine _targetTrackingCoroutine;
        
    // Track combo state for continuous facing
        
        // Aggression settings for melee
        private float _aggressionLevel = 0.5f; // 0-1, affects secondary attack frequency
        private float _lastAggressionUpdate;

  public override void OnActivate()
        {
 base.OnActivate();
          
     // Determine if two-handed from weapon type
    _isTwoHanded = Context.CurrentWeapon?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
          Context.CurrentWeapon?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
            
 // Adjust secondary attack chance based on weapon type
            if (_isTwoHanded)
   {
     // Two-handed weapons have more impactful secondaries
       _secondaryAttackChance = 0.2f;
      _secondaryCooldown = 4f;
   }
 else
            {
    // One-handed weapons can use secondaries more often
        _secondaryAttackChance = 0.15f;
    _secondaryCooldown = 5f;
            }
        
      if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[MeleeBehavior] Activated - TwoHanded: {_isTwoHanded}, " +
     $"ChainLevels: {_maxChainLevel}, HasSecondary: {_hasSecondaryAttack}, " +
              $"SecondaryChance: {_secondaryAttackChance:P0}");
            }
      }
      
        public override void OnDeactivate()
        {
 StopTargetTracking();
 base.OnDeactivate();
  }
   
        public override void Update()
  {
            base.Update();
      
 // Continue facing target during combo attacks
            if (_isAttacking && _currentTarget != null && !_currentTarget.IsDead())
 {
       // Smoothly track target during attack wind-up
         TrackTarget(_currentTarget);
         }
    
     // Update aggression based on combat situation
   UpdateAggression();
  }
        
        public override void ConfigureAI()
        {
            // CompanionAI handles combat behavior directly - no MonsterAI settings needed
            // We just configure our attack settings from weapon data
            float meleeRange = Context.AIAttackRange > 0 ? Context.AIAttackRange : 2.5f;
            float attackInterval = Context.AIAttackInterval > 0 ? Context.AIAttackInterval : 0.5f;
            
            Context.AttackRange = meleeRange;
            
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[MeleeBehavior] Configured for {(_isTwoHanded ? "TWO-HANDED" : "ONE-HANDED")} MELEE: " +
                    $"attackRange={meleeRange}, interval={attackInterval}, " +
                    $"animState={Context.WeaponAnimationState}");
            }
        }
        
      public override void ExecuteAttack(Character target)
        {
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log($"[MeleeBehavior] ========== ExecuteAttack START ==========");
                Debug.Log($"[MeleeBehavior] Target: {target?.m_name ?? "NULL"}");
            }
    
            _currentTarget = target;
   
            // Face target immediately
            FaceTarget(target);
          
            // Start target tracking for the duration of the attack
            StartTargetTracking(target);
      
            // Decide whether to use secondary attack
            bool useSecondary = ShouldUseSecondaryAttack(target);
            
            if (CompanionCombat.VerboseLogging)
                Debug.Log($"[MeleeBehavior] UseSecondary: {useSecondary}, UseNativeAttackSystem: {Context.UseNativeAttackSystem}");
    
            // Try native attack system first
            if (Context.UseNativeAttackSystem)
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[MeleeBehavior] Attempting NATIVE attack system...");
                    
                bool nativeSuccess = TryStartNativeAttack(target, useSecondary);
                
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[MeleeBehavior] Native attack result: {nativeSuccess}");
          
                if (nativeSuccess)
                {
                    string animTrigger = useSecondary ? 
                        GetSecondaryAttackAnimation() : 
                        Context.GetAttackAnimationTrigger(_attackChainLevel);
         
                    Context.BroadcastRPC("RPC_CompanionAttack",
                        animTrigger,
                        Context.GetAttackAnimationIndex());
        
                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[MeleeBehavior] ========== ExecuteAttack END (NATIVE) ==========");
                    return;
                }
                else
                {
                    if (CompanionCombat.VerboseLogging)
                        Debug.Log($"[MeleeBehavior] Native attack failed, falling back to manual system...");
                }
            }
            else
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log($"[MeleeBehavior] Native attack system DISABLED, using fallback...");
            }
    
            // Fallback to manual system with timing from weapon data
            float hitDelay = Context.GetEstimatedHitDelay();
   
            if (CompanionCombat.VerboseLogging)
                Debug.Log($"[MeleeBehavior] Using FALLBACK attack - hitDelay: {hitDelay:F2}s, weapon: {Context.CurrentWeapon?.m_shared?.m_name}");
      
            // ExecuteFallbackAttack now uses _animationDuration internally
            ExecuteFallbackAttack(target, hitDelay, useSecondary);
         
            if (CompanionCombat.VerboseLogging)
                Debug.Log($"[MeleeBehavior] ========== ExecuteAttack END (FALLBACK) ==========");
        }
      
      protected override bool ShouldUseSecondaryAttack(Character target)
   {
            // First check base conditions
   if (!base.ShouldUseSecondaryAttack(target))
    {
    // Additional melee-specific checks
          
     // Use secondary more often when aggression is high
         if (_aggressionLevel > 0.7f && Random.value < _secondaryAttackChance * 1.5f)
       {
     if (CompanionCombat.VerboseLogging)
            Debug.Log($"[MeleeBehavior] Using secondary attack - high aggression ({_aggressionLevel:F2})");
  return true;
     }
           
    // Use secondary to finish off low health targets
    float targetHealthPercent = target.GetHealthPercentage();
      if (targetHealthPercent < 0.25f && Random.value < 0.3f)
         {
  if (CompanionCombat.VerboseLogging)
    Debug.Log($"[MeleeBehavior] Using secondary attack - target low health ({targetHealthPercent:P0})");
     return true;
  }
            
 // Use secondary at end of combo for a finisher
     if (_comboActive && _attackChainLevel == _maxChainLevel - 1 && Random.value < 0.25f)
                {
   if (CompanionCombat.VerboseLogging)
             Debug.Log($"[MeleeBehavior] Using secondary attack - combo finisher");
          return true;
}
    
            return false;
 }
          
        return true;
        }
        
        public override void CancelAttack()
        {
            StopTargetTracking();
    base.CancelAttack();
      }
        
        /// <summary>
        /// Updates aggression level based on combat situation.
        /// Uses ThreatAnalyzer for intelligent aggression adjustments.
        /// </summary>
        private void UpdateAggression()
        {
            if (Time.time - _lastAggressionUpdate < 1f) return;
            _lastAggressionUpdate = Time.time;
    
            float newAggression = 0.5f; // Base aggression
            
            // Use ThreatAnalyzer for smarter aggression
            if (Context.ThreatAnalyzer != null)
            {
                var situation = Context.ThreatAnalyzer.GetCurrentSituation();
                
                // Base aggression on recommended stance
                switch (situation.RecommendedStance)
                {
                    case ThreatAnalyzer.CombatStance.Aggressive:
                        newAggression = 0.9f;
                        break;
                    case ThreatAnalyzer.CombatStance.Balanced:
                        newAggression = 0.6f;
                        break;
                    case ThreatAnalyzer.CombatStance.Defensive:
                        newAggression = 0.4f;
                        break;
                    case ThreatAnalyzer.CombatStance.Survival:
                        newAggression = 0.25f;
                        break;
                    case ThreatAnalyzer.CombatStance.Protective:
                        newAggression = 0.7f; // Aggressive to protect owner
                        break;
                    case ThreatAnalyzer.CombatStance.Retreat:
                        newAggression = 0.1f;
                        break;
                }
                
                // Adjust based on current target
                if (_currentTarget != null && !_currentTarget.IsDead())
                {
                    var profile = Context.ThreatAnalyzer.GetThreatProfile(_currentTarget);
                    
                    // Against low health targets, be more aggressive to finish them
                    if (profile.HealthPercent < 0.25f)
                    {
                        newAggression += 0.2f;
                    }
                    
                    // Against bosses, be more cautious unless they're low
                    if (profile.IsBoss && profile.HealthPercent > 0.3f)
                    {
                        newAggression -= 0.15f;
                    }
                }
            }
            else
            {
                // Fallback behavior without ThreatAnalyzer
                
                // Increase aggression if we're landing hits (combo active)
                if (_comboActive)
                {
                    newAggression += 0.2f;
                }
                
                // Increase aggression if target is low health
                if (_currentTarget != null && !_currentTarget.IsDead())
                {
                    float targetHealth = _currentTarget.GetHealthPercentage();
                    if (targetHealth < 0.5f)
                    {
                        newAggression += 0.15f;
                    }
                    if (targetHealth < 0.25f)
                    {
                        newAggression += 0.15f;
                    }
                }
            }
            
            _aggressionLevel = Mathf.Clamp01(Mathf.Lerp(_aggressionLevel, newAggression, 0.3f));
        }
        
        /// <summary>
        /// Starts tracking the target during the attack animation.
        /// This allows the NPC to adjust aim during combo wind-ups.
        /// </summary>
        private void StartTargetTracking(Character target)
        {
  StopTargetTracking();
     _targetTrackingCoroutine = Owner.StartCoroutine(TargetTrackingCoroutine(target));
        }
        
      private void StopTargetTracking()
        {
       if (_targetTrackingCoroutine != null)
 {
              Owner.StopCoroutine(_targetTrackingCoroutine);
    _targetTrackingCoroutine = null;
            }
        }
        
        /// <summary>
 /// Coroutine that continuously tracks the target during attack wind-up.
        /// Allows NPC to adjust facing as target moves.
   /// </summary>
    private IEnumerator TargetTrackingCoroutine(Character target)
        {
            float hitDelay = Context.GetEstimatedHitDelay();
float trackingDuration = hitDelay * 0.8f; // Track for 80% of wind-up
          float elapsed = 0f;
    
            while (elapsed < trackingDuration && target != null && !target.IsDead() && _isAttacking)
            {
     TrackTarget(target);
      elapsed += Time.deltaTime;
         yield return null;
  }
            
 _targetTrackingCoroutine = null;
}
        
        /// <summary>
        /// Smoothly rotates to face the target.
    /// </summary>
        private void TrackTarget(Character target)
        {
  if (target == null) return;
   
            Vector3 dirToTarget = (target.transform.position - Context.Transform.position).normalized;
      dirToTarget.y = 0;
 
            if (dirToTarget != Vector3.zero)
            {
    Quaternion targetRotation = Quaternion.LookRotation(dirToTarget);
        // Smooth rotation during wind-up, instant during actual swing
       float rotSpeed = _isAttacking ? 15f : 10f;
           Context.Transform.rotation = Quaternion.Slerp(
                    Context.Transform.rotation, 
           targetRotation, 
        Time.deltaTime * rotSpeed
     );
            }
        }
        
      /// <summary>
     /// Override to provide weapon-specific hit timing from SharedData.
        /// </summary>
        protected override float GetHitDelayForAttackType(Attack.AttackType attackType)
        {
          // Use our context's weapon-aware timing
       return Context.GetEstimatedHitDelay();
}
     
    /// <summary>
        /// Gets the secondary attack animation for melee weapons.
        /// </summary>
   protected override string GetSecondaryAttackAnimation()
     {
            var secondaryAttack = Context.EquipmentData?.SecondaryAttack;
      if (secondaryAttack != null && !string.IsNullOrEmpty(secondaryAttack.m_attackAnimation))
       {
    return secondaryAttack.m_attackAnimation;
        }
  
            // Fallback based on melee weapon animation state
 return Context.WeaponAnimationState switch
            {
        ItemDrop.ItemData.AnimationState.OneHanded => "sword_secondary",
                ItemDrop.ItemData.AnimationState.TwoHandedClub => "sledge_secondary",
                ItemDrop.ItemData.AnimationState.TwoHandedAxe => "battleaxe_secondary",
    ItemDrop.ItemData.AnimationState.Greatsword => "greatsword_secondary",
       ItemDrop.ItemData.AnimationState.Atgeir => "atgeir_secondary",
         ItemDrop.ItemData.AnimationState.Knives => "knife_secondary",
  ItemDrop.ItemData.AnimationState.DualAxes => "dualaxes_secondary",
       ItemDrop.ItemData.AnimationState.Scythe => "scythe_secondary",
      _ => "attack_secondary"
       };
        }
  }
}
