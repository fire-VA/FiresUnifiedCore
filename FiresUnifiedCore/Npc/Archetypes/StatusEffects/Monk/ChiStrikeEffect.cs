using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Monk
{
    /// <summary>
    /// Chi Strike effect - empowers the next few attacks with bonus damage.
    /// Used by: Monk archetype (self buff / attack ability)
    /// 
    /// DESIGN: Channels chi into attacks, providing bonus damage and
    /// a chance to stagger enemies. Limited number of empowered attacks.
    /// </summary>
    public class ChiStrikeEffect : Base.BuffEffect
    {
        /// <summary>Number of empowered attacks remaining.</summary>
        public int EmpoweredAttacksRemaining { get; set; } = 3;
        
        /// <summary>Bonus damage per empowered attack.</summary>
        public float BonusDamageFlat { get; set; } = 15f;
        
        /// <summary>Stagger multiplier for empowered attacks.</summary>
        public float EmpoweredStaggerMultiplier { get; set; } = 1.5f;
        
        /// <summary>Whether to consume attack charges or just use duration.</summary>
        public bool UseCharges { get; set; } = true;
        
        /// <summary>Knockback force on empowered hits.</summary>
        public float EmpoweredKnockback { get; set; } = 5f;
        
        private int _initialCharges;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"+{(DamageMultiplier - 1f) * 100:F0}% damage, +{BonusDamageFlat:F0} flat damage\n" +
            (UseCharges ? $"{EmpoweredAttacksRemaining} empowered attacks" : $"+{(AttackSpeedMultiplier - 1f) * 100:F0}% attack speed");
        
        public ChiStrikeEffect()
        {
            m_name = "Chi Strike";
            Duration = 15f;
            DamageMultiplier = 1.3f; // 30% damage boost
            AttackSpeedMultiplier = 1.15f; // 15% faster attacks
        }
        
        protected override void OnEffectApplied()
        {
            base.OnEffectApplied();
            _initialCharges = EmpoweredAttacksRemaining;
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Chi Strike!");
                
                if (VerboseLogging)
                {
                    Debug.Log($"[ChiStrikeEffect] {m_character.m_name} chi empowered - {EmpoweredAttacksRemaining} charges");
                }
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            // Add flat bonus damage
            if (EmpoweredAttacksRemaining > 0 || !UseCharges)
            {
                hitData.m_damage.m_blunt += BonusDamageFlat;
                hitData.m_staggerMultiplier *= EmpoweredStaggerMultiplier;
                hitData.m_pushForce += EmpoweredKnockback;
                
                if (UseCharges)
                {
                    EmpoweredAttacksRemaining--;
                    
                    if (VerboseLogging && m_character != null)
                    {
                        Debug.Log($"[ChiStrikeEffect] {m_character.m_name} chi strike! {EmpoweredAttacksRemaining} charges remaining");
                    }
                    
                    // End effect if no charges left
                    if (EmpoweredAttacksRemaining <= 0)
                    {
                        m_ttl = 0.1f; // End soon
                    }
                }
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                int usedCharges = _initialCharges - EmpoweredAttacksRemaining;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[ChiStrikeEffect] {m_character.m_name} chi strike ended - used {usedCharges}/{_initialCharges} charges");
                }
            }
            
            base.OnEffectRemoved();
        }
        
        /// <summary>
        /// Applies chi strike to a character.
        /// </summary>
        public static bool ApplyChiStrike(Character target, float duration, int charges = 3)
        {
            if (target == null) return false;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            var effect = ScriptableObject.CreateInstance<ChiStrikeEffect>();
            effect.name = StatusEffectManager.EFFECT_CHI_STRIKE;
            effect.Duration = duration;
            effect.EmpoweredAttacksRemaining = charges;
            effect.SourceCharacter = target;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_CHI_STRIKE);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            int existingHash = StatusEffectManager.EFFECT_CHI_STRIKE.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            Debug.Log($"[ChiStrikeEffect] Applied to {target.m_name}, charges={charges}, hasIcon={icon != null}");
            
            return true;
        }
        
        public new ChiStrikeEffect Clone()
        {
            var clone = (ChiStrikeEffect)base.Clone();
            if (clone != null)
            {
                clone.EmpoweredAttacksRemaining = EmpoweredAttacksRemaining;
                clone.BonusDamageFlat = BonusDamageFlat;
                clone.EmpoweredStaggerMultiplier = EmpoweredStaggerMultiplier;
                clone.UseCharges = UseCharges;
                clone.EmpoweredKnockback = EmpoweredKnockback;
            }
            return clone;
        }
    }
}
