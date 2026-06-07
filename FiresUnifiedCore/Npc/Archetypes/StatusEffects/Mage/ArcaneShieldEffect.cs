using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Mage
{
    /// <summary>
    /// Arcane Shield effect - creates a magical barrier that absorbs damage.
    /// Used by: Mage archetype (self/group defensive ability)
    /// 
    /// DESIGN: The mage creates a magical shield that absorbs incoming damage.
    /// Shield has a health pool that depletes before the character takes damage.
    /// </summary>
    public class ArcaneShieldEffect : CompanionStatusEffectBase
    {
        /// <summary>Maximum shield health.</summary>
        public float MaxShieldHealth { get; set; } = 100f;
        
        /// <summary>Current shield health.</summary>
        public float CurrentShieldHealth { get; set; }
        
        /// <summary>Whether the shield can be applied to allies.</summary>
        public bool IsGroupShield { get; set; } = false;
        
        /// <summary>Range for group shield application.</summary>
        public float GroupShieldRange { get; set; } = 8f;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"Absorbs {MaxShieldHealth:F0} damage\n" +
            $"Remaining: {CurrentShieldHealth:F0} HP";
        
        public ArcaneShieldEffect()
        {
            m_name = "Arcane Shield";
            Duration = 20f;
        }
        
        protected override void OnEffectApplied()
        {
            CurrentShieldHealth = MaxShieldHealth;
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Arcane Shield!");
                
                if (VerboseLogging)
                {
                    Debug.Log($"[ArcaneShieldEffect] {m_character.m_name} shielded - {MaxShieldHealth} HP barrier");
                }
            }
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            if (CurrentShieldHealth <= 0) return;
            
            // Calculate total incoming damage
            float totalDamage = hit.GetTotalDamage();
            
            if (totalDamage <= CurrentShieldHealth)
            {
                // Shield absorbs all damage
                CurrentShieldHealth -= totalDamage;
                
                // Zero out the damage
                hit.m_damage.m_damage = 0;
                hit.m_damage.m_blunt = 0;
                hit.m_damage.m_slash = 0;
                hit.m_damage.m_pierce = 0;
                hit.m_damage.m_chop = 0;
                hit.m_damage.m_pickaxe = 0;
                hit.m_damage.m_fire = 0;
                hit.m_damage.m_frost = 0;
                hit.m_damage.m_lightning = 0;
                hit.m_damage.m_poison = 0;
                hit.m_damage.m_spirit = 0;
                
                if (VerboseLogging && m_character != null)
                {
                    Debug.Log($"[ArcaneShieldEffect] {m_character.m_name} shield absorbed {totalDamage} damage - {CurrentShieldHealth} remaining");
                }
            }
            else
            {
                // Shield breaks, remaining damage goes through
                float remainingDamage = totalDamage - CurrentShieldHealth;
                float damageRatio = remainingDamage / totalDamage;
                
                // Scale down all damage types by the ratio
                hit.m_damage.Modify(damageRatio);
                
                CurrentShieldHealth = 0;
                m_ttl = 0.1f; // End effect
                
                if (m_character != null)
                {
                    m_character.Message(MessageHud.MessageType.TopLeft, "Shield broken!");
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[ArcaneShieldEffect] {m_character.m_name} shield broken - {remainingDamage} damage passed through");
                    }
                }
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null && CurrentShieldHealth > 0 && VerboseLogging)
            {
                Debug.Log($"[ArcaneShieldEffect] {m_character.m_name} shield expired with {CurrentShieldHealth} HP remaining");
            }
        }
        
        /// <summary>
        /// Applies arcane shield to a character.
        /// </summary>
        public static bool ApplyArcaneShield(Character target, float duration, float shieldHealth = 100f)
        {
            if (target == null) return false;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            var effect = ScriptableObject.CreateInstance<ArcaneShieldEffect>();
            effect.name = StatusEffectManager.EFFECT_ARCANE_SHIELD;
            effect.Duration = duration;
            effect.MaxShieldHealth = shieldHealth;
            effect.SourceCharacter = target;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_ARCANE_SHIELD);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            int existingHash = StatusEffectManager.EFFECT_ARCANE_SHIELD.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            Debug.Log($"[ArcaneShieldEffect] Applied to {target.m_name}, shield={shieldHealth}HP, hasIcon={icon != null}");
            
            return true;
        }
        
        /// <summary>
        /// Applies arcane shield to all allies in range.
        /// </summary>
        public static int ApplyGroupArcaneShield(Character source, float range, float duration, float shieldHealth = 50f)
        {
            if (source == null) return 0;
            
            int shieldedCount = 0;
            Vector3 center = source.transform.position;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(source, character)) continue;
                
                float distance = Vector3.Distance(center, character.transform.position);
                if (distance <= range)
                {
                    ApplyArcaneShield(character, duration, shieldHealth);
                    shieldedCount++;
                }
            }
            
            return shieldedCount;
        }
        
        public new ArcaneShieldEffect Clone()
        {
            var clone = (ArcaneShieldEffect)base.Clone();
            if (clone != null)
            {
                clone.MaxShieldHealth = MaxShieldHealth;
                clone.CurrentShieldHealth = CurrentShieldHealth;
                clone.IsGroupShield = IsGroupShield;
                clone.GroupShieldRange = GroupShieldRange;
            }
            return clone;
        }
    }
}
