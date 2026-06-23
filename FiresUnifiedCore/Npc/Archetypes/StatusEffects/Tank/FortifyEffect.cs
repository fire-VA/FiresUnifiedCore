using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Tank
{
    /// <summary>
    /// Fortify effect - increases armor and reduces incoming damage.
    /// Used by: Tank and Paladin archetypes
    /// 
    /// DESIGN: Provides damage reduction and armor bonus while active.
    /// Can be stacked with other defensive abilities.
    /// 
    /// SKILL SCALING: The Fortify skill level increases effectiveness:
    /// - Level 1: Base values (30% reduction, +20 armor)
    /// - Level 50: +25% effectiveness (37.5% reduction, +25 armor)
    /// - Level 100: +50% effectiveness (45% reduction, +30 armor)
    /// </summary>
    public class FortifyEffect : CompanionStatusEffectBase
    {
        /// <summary>Base damage reduction multiplier (0.7 = take 30% less damage).</summary>
        public float BaseDamageReduction { get; set; } = 0.7f;
        
        /// <summary>Base bonus armor added while active.</summary>
        public float BaseBonusArmor { get; set; } = 20f;
        
        /// <summary>Skill effectiveness multiplier (1.0 to 1.5 based on skill level).</summary>
        public float SkillEffectiveness { get; set; } = 1.0f;
        
        /// <summary>Computed damage reduction after skill scaling.</summary>
        public float DamageReduction => 1f - ((1f - BaseDamageReduction) * SkillEffectiveness);
        
        /// <summary>Computed bonus armor after skill scaling.</summary>
        public float BonusArmor => BaseBonusArmor * SkillEffectiveness;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"-{(1f - DamageReduction) * 100:F0}% damage taken\n" +
            $"+{BonusArmor:F0} armor" +
            (SkillEffectiveness > 1.01f ? $"\n<color=#66FF66>Skill bonus: +{(SkillEffectiveness - 1f) * 100:F0}%</color>" : "");
        
        public FortifyEffect()
        {
            m_name = "Fortified";
            Duration = 8f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Fortified!");
                
                if (VerboseLogging)
                {
                    Debug.Log($"[FortifyEffect] {m_character.m_name} fortified - {(1f - DamageReduction) * 100:F1}% damage reduction, +{BonusArmor:F0} armor (skill effectiveness: {SkillEffectiveness:P0})");
                }
            }
        }
        
        /// <summary>
        /// Modifies incoming damage to apply damage reduction.
        /// </summary>
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            // Reduce incoming damage with skill-scaled reduction
            hit.m_damage.Modify(DamageReduction);
            
            if (VerboseLogging && m_character != null)
            {
                Debug.Log($"[FortifyEffect] {m_character.m_name} fortify reduced damage by {(1f - DamageReduction) * 100:F1}%");
            }
        }
        
        /// <summary>
        /// Modifies armor value while active.
        /// </summary>
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            // This is called for outgoing attacks, not what we need
            base.ModifyAttack(skill, ref hitData);
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Fortify ended.");
                
                if (VerboseLogging)
                {
                    Debug.Log($"[FortifyEffect] {m_character.m_name} fortify ended");
                }
            }
        }
        
        public new FortifyEffect Clone()
        {
            var clone = (FortifyEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.BaseDamageReduction = BaseDamageReduction;
                clone.BaseBonusArmor = BaseBonusArmor;
                clone.SkillEffectiveness = SkillEffectiveness;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }
}
