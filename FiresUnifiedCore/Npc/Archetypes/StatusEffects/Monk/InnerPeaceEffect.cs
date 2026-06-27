using UnityEngine;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.Archetypes.StatusEffects.Monk
{
    /// <summary>
    /// Inner Peace effect - a meditative state that provides healing and buffs to nearby allies.
    /// Used by: Monk archetype (group buff ability)
    /// 
    /// DESIGN: The monk enters a meditative state, providing an aura that heals
    /// and increases stamina regeneration for nearby allies. The monk is immobile
    /// during meditation but gains increased defense.
    /// </summary>
    public class InnerPeaceEffect : Base.BuffEffect
    {
        /// <summary>Heal per tick for allies in range.</summary>
        public float AllyHealPerTick { get; set; } = 5f;
        
        /// <summary>Tick interval for aura effects.</summary>
        public float AuraTickInterval { get; set; } = 2f;
        
        /// <summary>Range of the meditation aura.</summary>
        public float AuraRange { get; set; } = 12f;
        
        /// <summary>Stamina regen multiplier for allies.</summary>
        public float AllyStaminaRegenBonus { get; set; } = 1.5f;
        
        /// <summary>Whether the monk is immobilized during meditation.</summary>
        public bool ImmobilizeDuringMeditation { get; set; } = true;
        
        /// <summary>Brief immunity at start of meditation.</summary>
        public float InitialImmunityDuration { get; set; } = 1.5f;
        
        private float _lastAuraTick;
        private Rigidbody _rb;
        private UnifiedMovementAuthority _uma;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"-{(1f - DefenseMultiplier) * 100:F0}% damage taken\n" +
            $"+{AllyHealPerTick:F0} HP/{AuraTickInterval:F1}s to nearby allies";
        
        public InnerPeaceEffect()
        {
            m_name = "Inner Peace";
            Duration = 8f;
            DefenseMultiplier = 0.5f; // 50% damage reduction while meditating
            StaminaRegenMultiplier = 2.0f; // Double stamina regen for self
            IsGroupBuff = true;
            GroupBuffRange = 12f;
        }
        
        protected override void OnEffectApplied()
        {
            base.OnEffectApplied();
            _lastAuraTick = Time.time;
            
            if (m_character != null)
            {
                _rb = m_character.GetComponent<Rigidbody>();

                // Single-writer: the monk is immobile during meditation. Own the standstill with a
                // one-time freeze instead of fighting UMA with a per-frame SetMoveDir(0). The freeze
                // carries the meditation duration so it auto-expires even if removal is missed; only
                // Forced (teleport/knockback) can break it.
                _uma = m_character.GetComponent<UnifiedMovementAuthority>();
                if (ImmobilizeDuringMeditation && _uma != null)
                    _uma.FreezeMovement("InnerPeace", Duration);

                m_character.Message(MessageHud.MessageType.TopLeft, "Inner Peace...");
                
                // Apply brief immunity at start
                if (InitialImmunityDuration > 0f)
                {
                    ApplyInitialImmunity();
                }
                
                if (VerboseLogging)
                {
                    Debug.Log($"[InnerPeaceEffect] {m_character.m_name} entered meditation - {AuraRange}m healing aura");
                }
            }
        }
        
        private void ApplyInitialImmunity()
        {
            var seman = m_character.GetSEMan();
            if (seman == null) return;
            
            var immunity = ScriptableObject.CreateInstance<Common.InvulnerableEffect>();
            immunity.name = "InnerPeaceImmunity";
            immunity.Duration = InitialImmunityDuration;
            
            // CRITICAL: Set m_icon directly
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_INVULNERABLE);
            immunity.m_icon = icon;
            immunity.EffectIcon = icon;
            
            seman.AddStatusEffect(immunity, true);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null || m_character.IsDead()) return;
            
            // Immobilize during meditation. For a companion (has UMA) the one-time FreezeMovement from
            // OnEffectApplied owns the standstill and re-enforces zero each LateUpdate — nothing to do
            // here. Only the non-UMA fallback (should not happen for a real monk companion) hard-zeros.
            if (ImmobilizeDuringMeditation && _uma == null && _rb != null && !_rb.isKinematic)
            {
                _rb.linearVelocity = Vector3.zero;
                m_character.SetMoveDir(Vector3.zero);
            }
            
            // Apply aura effects periodically
            if (Time.time - _lastAuraTick >= AuraTickInterval)
            {
                _lastAuraTick = Time.time;
                ApplyAuraEffects();
            }
        }
        
        private void ApplyAuraEffects()
        {
            if (m_character == null) return;
            
            Vector3 center = m_character.transform.position;
            var characters = Character.GetAllCharacters();
            
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float distance = Vector3.Distance(center, character.transform.position);
                if (distance <= AuraRange)
                {
                    // Heal ally
                    character.Heal(AllyHealPerTick, true);
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[InnerPeaceEffect] Aura healed {character.m_name} for {AllyHealPerTick} HP");
                    }
                }
            }
        }
        
        protected override void OnEffectRemoved()
        {
            _rb = null;

            // Release the owned standstill so the monk can move again the instant meditation ends.
            if (_uma != null)
            {
                _uma.UnfreezeMovement("InnerPeace");
                _uma = null;
            }

            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Meditation complete.");
            }
            
            base.OnEffectRemoved();
        }
        
        /// <summary>
        /// Applies inner peace meditation to a monk.
        /// </summary>
        public static bool ApplyInnerPeace(Character monk, float duration)
        {
            if (monk == null) return false;
            
            var seman = monk.GetSEMan();
            if (seman == null) return false;
            
            var effect = ScriptableObject.CreateInstance<InnerPeaceEffect>();
            effect.name = StatusEffectManager.EFFECT_INNER_PEACE;
            effect.Duration = duration;
            effect.SourceCharacter = monk;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_INNER_PEACE);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            int existingHash = StatusEffectManager.EFFECT_INNER_PEACE.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            Debug.Log($"[InnerPeaceEffect] Applied to {monk.m_name}, hasIcon={icon != null}");
            
            return true;
        }
        
        public new InnerPeaceEffect Clone()
        {
            var clone = (InnerPeaceEffect)base.Clone();
            if (clone != null)
            {
                clone.AllyHealPerTick = AllyHealPerTick;
                clone.AuraTickInterval = AuraTickInterval;
                clone.AuraRange = AuraRange;
                clone.AllyStaminaRegenBonus = AllyStaminaRegenBonus;
                clone.ImmobilizeDuringMeditation = ImmobilizeDuringMeditation;
                clone.InitialImmunityDuration = InitialImmunityDuration;
            }
            return clone;
        }
    }
}
