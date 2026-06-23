using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Paladin
{
    /// <summary>
    /// Holy Smite effect - empowers the paladin's next attack with holy damage.
    /// Used by: Paladin archetype (self buff / attack ability)
    /// 
    /// DESIGN: Channels holy energy into the weapon, adding spirit damage
    /// to the next attack and potentially stunning undead enemies.
    /// </summary>
    public class HolySmiteEffect : Base.BuffEffect
    {
        /// <summary>Bonus spirit damage added to attacks.</summary>
        public float BonusSpiritDamage { get; set; } = 25f;
        
        /// <summary>Number of empowered attacks.</summary>
        public int EmpoweredAttacksRemaining { get; set; } = 2;
        
        /// <summary>Stagger multiplier against undead.</summary>
        public float UndeadStaggerBonus { get; set; } = 2.0f;
        
        /// <summary>Whether to consume charges or just use duration.</summary>
        public bool UseCharges { get; set; } = true;
        
        private int _initialCharges;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"+{BonusSpiritDamage:F0} spirit damage\n" +
            (UseCharges ? $"{EmpoweredAttacksRemaining} empowered attacks" : $"+{(DamageMultiplier - 1f) * 100:F0}% damage");
        
        public HolySmiteEffect()
        {
            m_name = "Holy Smite";
            Duration = 15f;
            DamageMultiplier = 1.15f; // 15% damage boost
        }
        
        protected override void OnEffectApplied()
        {
            base.OnEffectApplied();
            _initialCharges = EmpoweredAttacksRemaining;
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Holy Smite!");
                
                if (VerboseLogging)
                {
                    Debug.Log($"[HolySmiteEffect] {m_character.m_name} weapon blessed - {EmpoweredAttacksRemaining} charges, +{BonusSpiritDamage} spirit damage");
                }
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (EmpoweredAttacksRemaining > 0 || !UseCharges)
            {
                // Add spirit damage
                hitData.m_damage.m_spirit += BonusSpiritDamage;
                
                if (UseCharges)
                {
                    EmpoweredAttacksRemaining--;
                    
                    if (VerboseLogging && m_character != null)
                    {
                        Debug.Log($"[HolySmiteEffect] {m_character.m_name} holy smite! {EmpoweredAttacksRemaining} charges remaining");
                    }
                    
                    if (EmpoweredAttacksRemaining <= 0)
                    {
                        m_ttl = 0.1f;
                    }
                }
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null && VerboseLogging)
            {
                int usedCharges = _initialCharges - EmpoweredAttacksRemaining;
                Debug.Log($"[HolySmiteEffect] {m_character.m_name} holy smite ended - used {usedCharges}/{_initialCharges} charges");
            }
            
            base.OnEffectRemoved();
        }
        
        /// <summary>
        /// Applies holy smite to a paladin.
        /// </summary>
        public static bool ApplyHolySmite(Character target, float duration, int charges = 2)
        {
            if (target == null) return false;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            var effect = ScriptableObject.CreateInstance<HolySmiteEffect>();
            effect.name = StatusEffectManager.EFFECT_HOLY_SMITE;
            effect.Duration = duration;
            effect.EmpoweredAttacksRemaining = charges;
            effect.SourceCharacter = target;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_HOLY_SMITE);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            int existingHash = StatusEffectManager.EFFECT_HOLY_SMITE.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            Debug.Log($"[HolySmiteEffect] Applied to {target.m_name}, charges={charges}, hasIcon={icon != null}");
            
            return true;
        }
        
        public new HolySmiteEffect Clone()
        {
            var clone = (HolySmiteEffect)base.Clone();
            if (clone != null)
            {
                clone.BonusSpiritDamage = BonusSpiritDamage;
                clone.EmpoweredAttacksRemaining = EmpoweredAttacksRemaining;
                clone.UndeadStaggerBonus = UndeadStaggerBonus;
                clone.UseCharges = UseCharges;
            }
            return clone;
        }
    }
}
