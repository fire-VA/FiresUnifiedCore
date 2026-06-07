using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Mage
{
    /// <summary>
    /// Elemental Infusion effect - boosts elemental damage output.
    /// Used by: Mage archetype (self buff ability)
    /// 
    /// DESIGN: The mage channels elemental energy, boosting all elemental
    /// damage (fire, frost, lightning) and reducing eitr costs.
    /// </summary>
    public class ElementalInfusionEffect : Base.BuffEffect
    {
        /// <summary>Bonus fire damage multiplier.</summary>
        public float FireDamageBonus { get; set; } = 1.3f;
        
        /// <summary>Bonus frost damage multiplier.</summary>
        public float FrostDamageBonus { get; set; } = 1.3f;
        
        /// <summary>Bonus lightning damage multiplier.</summary>
        public float LightningDamageBonus { get; set; } = 1.3f;
        
        /// <summary>Eitr cost reduction.</summary>
        public float EitrCostReduction { get; set; } = 0.8f;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"+{(FireDamageBonus - 1f) * 100:F0}% fire/frost/lightning damage\n" +
            $"-{(1f - EitrCostReduction) * 100:F0}% eitr cost";
        
        public ElementalInfusionEffect()
        {
            m_name = "Elemental Infusion";
            Duration = 15f;
            DamageMultiplier = 1.0f; // Elemental bonuses are separate
        }
        
        protected override void OnEffectApplied()
        {
            base.OnEffectApplied();
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Elemental Infusion!");
                
                if (VerboseLogging)
                {
                    Debug.Log($"[ElementalInfusionEffect] {m_character.m_name} infused - +{(FireDamageBonus - 1f) * 100}% elemental damage");
                }
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            // Boost elemental damage
            if (hitData.m_damage.m_fire > 0)
            {
                hitData.m_damage.m_fire *= FireDamageBonus;
            }
            if (hitData.m_damage.m_frost > 0)
            {
                hitData.m_damage.m_frost *= FrostDamageBonus;
            }
            if (hitData.m_damage.m_lightning > 0)
            {
                hitData.m_damage.m_lightning *= LightningDamageBonus;
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Infusion fades...");
            }
            
            base.OnEffectRemoved();
        }
        
        /// <summary>
        /// Applies elemental infusion to a mage.
        /// </summary>
        public static bool ApplyElementalInfusion(Character target, float duration)
        {
            if (target == null) return false;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            var effect = ScriptableObject.CreateInstance<ElementalInfusionEffect>();
            effect.name = StatusEffectManager.EFFECT_ELEMENTAL_INFUSION;
            effect.Duration = duration;
            effect.SourceCharacter = target;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_ELEMENTAL_INFUSION);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            int existingHash = StatusEffectManager.EFFECT_ELEMENTAL_INFUSION.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            Debug.Log($"[ElementalInfusionEffect] Applied to {target.m_name}, hasIcon={icon != null}");
            
            return true;
        }
        
        public new ElementalInfusionEffect Clone()
        {
            var clone = (ElementalInfusionEffect)base.Clone();
            if (clone != null)
            {
                clone.FireDamageBonus = FireDamageBonus;
                clone.FrostDamageBonus = FrostDamageBonus;
                clone.LightningDamageBonus = LightningDamageBonus;
                clone.EitrCostReduction = EitrCostReduction;
            }
            return clone;
        }
    }
}
