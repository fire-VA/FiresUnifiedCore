using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Base
{
    /// <summary>
    /// Base class for buff effects that provide stat bonuses.
    /// Used as a foundation for damage buffs, defense buffs, speed buffs, etc.
    /// </summary>
    public class BuffEffect : CompanionStatusEffectBase
    {
        /// <summary>Damage multiplier (1.0 = no change, 1.5 = 50% more damage).</summary>
        public float DamageMultiplier { get; set; } = 1.0f;
        
        /// <summary>Defense multiplier (0.8 = take 20% less damage).</summary>
        public float DefenseMultiplier { get; set; } = 1.0f;
        
        /// <summary>Speed multiplier (1.2 = 20% faster).</summary>
        public float SpeedMultiplier { get; set; } = 1.0f;
        
        /// <summary>Attack speed multiplier.</summary>
        public float AttackSpeedMultiplier { get; set; } = 1.0f;
        
        /// <summary>Stamina regen multiplier.</summary>
        public float StaminaRegenMultiplier { get; set; } = 1.0f;
        
        /// <summary>Health regen bonus per second.</summary>
        public float HealthRegenBonus { get; set; } = 0f;
        
        /// <summary>Whether this buff affects nearby allies (group buff).</summary>
        public bool IsGroupBuff { get; set; } = false;
        
        /// <summary>Range for group buff application.</summary>
        public float GroupBuffRange { get; set; } = 10f;
        
        private Rigidbody _rb;
        
        public BuffEffect()
        {
            m_name = "Buff";
            m_tooltip = "Enhanced abilities";
            Duration = 30f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                _rb = m_character.GetComponent<Rigidbody>();
                
                if (VerboseLogging)
                {
                    Debug.Log($"[BuffEffect] {m_character.m_name} buffed - Dmg x{DamageMultiplier}, Def x{DefenseMultiplier}, Spd x{SpeedMultiplier}");
                }
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null || m_character.IsDead()) return;
            
            // Apply speed modifier if not 1.0
            if (SpeedMultiplier != 1.0f && _rb != null && !_rb.isKinematic)
            {
                Vector3 velocity = _rb.linearVelocity;
                float horizontalMag = new Vector3(velocity.x, 0, velocity.z).magnitude;
                
                if (horizontalMag > 0.1f)
                {
                    Vector3 horizontalDir = new Vector3(velocity.x, 0, velocity.z).normalized;
                    _rb.linearVelocity = horizontalDir * horizontalMag * SpeedMultiplier + Vector3.up * velocity.y;
                }
            }
            
            // Apply health regen if set
            if (HealthRegenBonus > 0f)
            {
                m_character.Heal(HealthRegenBonus * dt, false);
            }
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            // Apply defense multiplier
            if (DefenseMultiplier != 1.0f)
            {
                hit.m_damage.Modify(DefenseMultiplier);
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            // Apply damage multiplier to outgoing attacks
            if (DamageMultiplier != 1.0f)
            {
                hitData.m_damage.Modify(DamageMultiplier);
            }
        }
        
        protected override void OnEffectRemoved()
        {
            _rb = null;
            
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[BuffEffect] {m_character.m_name} buff ended");
            }
        }
        
        public new BuffEffect Clone()
        {
            var clone = (BuffEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.DamageMultiplier = DamageMultiplier;
                clone.DefenseMultiplier = DefenseMultiplier;
                clone.SpeedMultiplier = SpeedMultiplier;
                clone.AttackSpeedMultiplier = AttackSpeedMultiplier;
                clone.StaminaRegenMultiplier = StaminaRegenMultiplier;
                clone.HealthRegenBonus = HealthRegenBonus;
                clone.IsGroupBuff = IsGroupBuff;
                clone.GroupBuffRange = GroupBuffRange;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }
}
