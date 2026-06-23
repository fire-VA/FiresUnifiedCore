using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Berserker
{
    /// <summary>
    /// Berserk Rage effect - increases damage and speed but reduces defense.
    /// Used by: Berserker archetype
    /// 
    /// DESIGN: When activated (typically when health drops below threshold),
    /// the berserker gains massive damage and speed bonuses but takes more damage.
    /// Can optionally grant brief immunity at activation.
    /// </summary>
    public class BerserkRageEffect : CompanionStatusEffectBase
    {
        /// <summary>Damage multiplier (1.5 = 50% more damage).</summary>
        public float DamageMultiplier { get; set; } = 1.5f;
        
        /// <summary>Attack speed multiplier.</summary>
        public float AttackSpeedMultiplier { get; set; } = 1.25f;
        
        /// <summary>Movement speed multiplier.</summary>
        public float MoveSpeedMultiplier { get; set; } = 1.15f;
        
        /// <summary>Incoming damage multiplier (1.2 = take 20% more damage).</summary>
        public float IncomingDamageMultiplier { get; set; } = 1.2f;
        
        /// <summary>Whether to grant brief immunity when rage activates.</summary>
        public bool GrantImmunityOnActivate { get; set; } = true;
        
        /// <summary>Duration of immunity on activation.</summary>
        public float ImmunityDuration { get; set; } = 1.0f;
        
        private Rigidbody _rb;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"+{(DamageMultiplier - 1f) * 100:F0}% damage, +{(MoveSpeedMultiplier - 1f) * 100:F0}% speed\n" +
            $"+{(IncomingDamageMultiplier - 1f) * 100:F0}% damage taken";
        
        public BerserkRageEffect()
        {
            m_name = "Berserk Rage";
            Duration = 10f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            _rb = m_character.GetComponent<Rigidbody>();
            
            // Show rage message
            m_character.Message(MessageHud.MessageType.TopLeft, "RAGE!");
            
            // Grant brief immunity if enabled
            if (GrantImmunityOnActivate && ImmunityDuration > 0)
            {
                ApplyBriefImmunity();
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[BerserkRageEffect] {m_character.m_name} entered berserk rage! " +
                    $"Damage x{DamageMultiplier}, Speed x{MoveSpeedMultiplier}, Defense x{1f/IncomingDamageMultiplier:F2}");
            }
        }
        
        /// <summary>
        /// Applies brief immunity when rage activates.
        /// </summary>
        private void ApplyBriefImmunity()
        {
            var seman = m_character.GetSEMan();
            if (seman == null) return;
            
            var invuln = ScriptableObject.CreateInstance<Common.InvulnerableEffect>();
            invuln.name = "BerserkImmunity";
            invuln.Duration = ImmunityDuration;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_BERSERK_RAGE);
            invuln.m_icon = icon;
            invuln.EffectIcon = icon;
            
            int existingHash = "BerserkImmunity".GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(invuln, true);
            
            if (VerboseLogging)
            {
                Debug.Log($"[BerserkRageEffect] {m_character.m_name} granted {ImmunityDuration}s immunity on rage activation");
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null) return;
            
            // Boost movement speed by modifying velocity
            // Note: The actual damage boost is handled by ArchetypeController checking for this effect
            if (_rb != null && !_rb.isKinematic)
            {
                Vector3 velocity = _rb.linearVelocity;
                float horizontalMag = new Vector3(velocity.x, 0, velocity.z).magnitude;
                
                // Only boost if actually moving
                if (horizontalMag > 0.1f)
                {
                    Vector3 horizontalDir = new Vector3(velocity.x, 0, velocity.z).normalized;
                    float boostedSpeed = horizontalMag * MoveSpeedMultiplier;
                    _rb.linearVelocity = horizontalDir * boostedSpeed + Vector3.up * velocity.y;
                }
            }
        }
        
        /// <summary>
        /// Modifies incoming damage - berserkers take more damage while raging.
        /// </summary>
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            // Increase incoming damage
            hit.m_damage.Modify(IncomingDamageMultiplier);
            
            if (VerboseLogging)
            {
                Debug.Log($"[BerserkRageEffect] {m_character?.m_name} took {IncomingDamageMultiplier:F1}x damage while raging");
            }
        }
        
        protected override void OnEffectRemoved()
        {
            _rb = null;
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Rage subsided...");
                
                if (VerboseLogging)
                {
                    Debug.Log($"[BerserkRageEffect] {m_character.m_name} berserk rage ended");
                }
            }
        }
        
        public new BerserkRageEffect Clone()
        {
            var clone = (BerserkRageEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.DamageMultiplier = DamageMultiplier;
                clone.AttackSpeedMultiplier = AttackSpeedMultiplier;
                clone.MoveSpeedMultiplier = MoveSpeedMultiplier;
                clone.IncomingDamageMultiplier = IncomingDamageMultiplier;
                clone.GrantImmunityOnActivate = GrantImmunityOnActivate;
                clone.ImmunityDuration = ImmunityDuration;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }
}
