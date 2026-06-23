using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Ranger
{
    /// <summary>
    /// Eagle Eye effect - increases accuracy and critical chance for ranged attacks.
    /// Used by: Ranger archetype (self buff ability)
    /// 
    /// DESIGN: The ranger focuses intensely, gaining bonus critical chance
    /// and damage for ranged attacks. Also increases projectile speed.
    /// </summary>
    public class EagleEyeEffect : Base.BuffEffect
    {
        /// <summary>Bonus critical chance while active.</summary>
        public float BonusCritChance { get; set; } = 0.20f;
        
        /// <summary>Bonus critical damage multiplier.</summary>
        public float BonusCritDamage { get; set; } = 1.5f;
        
        /// <summary>Projectile speed multiplier.</summary>
        public float ProjectileSpeedMultiplier { get; set; } = 1.25f;
        
        /// <summary>Bonus ranged damage.</summary>
        public float RangedDamageBonus { get; set; } = 1.2f;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"+{BonusCritChance * 100:F0}% crit chance\n" +
            $"+{(RangedDamageBonus - 1f) * 100:F0}% ranged damage";
        
        public EagleEyeEffect()
        {
            m_name = "Eagle Eye";
            Duration = 12f;
            DamageMultiplier = 1.0f; // Ranged bonus is separate
        }
        
        protected override void OnEffectApplied()
        {
            base.OnEffectApplied();
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Eagle Eye!");
                
                if (VerboseLogging)
                {
                    Debug.Log($"[EagleEyeEffect] {m_character.m_name} focused - +{BonusCritChance * 100}% crit, +{(RangedDamageBonus - 1f) * 100}% ranged damage");
                }
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            // Apply ranged damage bonus for bow/crossbow skills
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows)
            {
                hitData.m_damage.Modify(RangedDamageBonus);
                
                if (VerboseLogging && m_character != null)
                {
                    Debug.Log($"[EagleEyeEffect] {m_character.m_name} ranged attack boosted by {RangedDamageBonus}x");
                }
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Focus fades...");
            }
            
            base.OnEffectRemoved();
        }
        
        /// <summary>
        /// Applies eagle eye to a ranger.
        /// </summary>
        public static bool ApplyEagleEye(Character target, float duration)
        {
            if (target == null) return false;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            var effect = ScriptableObject.CreateInstance<EagleEyeEffect>();
            effect.name = StatusEffectManager.EFFECT_EAGLE_EYE;
            effect.Duration = duration;
            effect.SourceCharacter = target;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_EAGLE_EYE);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            int existingHash = StatusEffectManager.EFFECT_EAGLE_EYE.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            Debug.Log($"[EagleEyeEffect] Applied to {target.m_name}, hasIcon={icon != null}");
            
            return true;
        }
        
        public new EagleEyeEffect Clone()
        {
            var clone = (EagleEyeEffect)base.Clone();
            if (clone != null)
            {
                clone.BonusCritChance = BonusCritChance;
                clone.BonusCritDamage = BonusCritDamage;
                clone.ProjectileSpeedMultiplier = ProjectileSpeedMultiplier;
                clone.RangedDamageBonus = RangedDamageBonus;
            }
            return clone;
        }
    }
}
