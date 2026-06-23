using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Healer
{
    /// <summary>
    /// Sanctuary effect - a protective aura that reduces incoming damage for allies.
    /// Used by: Healer archetype (group defense ability)
    /// 
    /// DESIGN: Creates a sanctuary that provides damage reduction and slow health
    /// regeneration to all allies within range.
    /// </summary>
    public class SanctuaryEffect : Base.BuffEffect
    {
        /// <summary>Whether to apply effect to nearby allies.</summary>
        public bool IsAura { get; set; } = true;
        
        /// <summary>Range of the sanctuary aura.</summary>
        public float AuraRange { get; set; } = 10f;
        
        /// <summary>Interval to reapply aura to allies.</summary>
        public float AuraTickInterval { get; set; } = 2f;
        
        /// <summary>Whether the sanctuary blocks projectiles (visual only).</summary>
        public bool BlocksProjectiles { get; set; } = false;
        
        private float _lastAuraTick;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"-{(1f - DefenseMultiplier) * 100:F0}% damage taken\n" +
            $"+{HealthRegenBonus:F1} HP/s regeneration";
        
        public SanctuaryEffect()
        {
            m_name = "Sanctuary";
            Duration = 10f;
            DefenseMultiplier = 0.7f; // 30% damage reduction
            HealthRegenBonus = 2f; // Slow regen
            IsGroupBuff = true;
            GroupBuffRange = 10f;
        }
        
        protected override void OnEffectApplied()
        {
            base.OnEffectApplied();
            _lastAuraTick = Time.time;
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Sanctuary!");
                
                if (VerboseLogging)
                {
                    Debug.Log($"[SanctuaryEffect] {m_character.m_name} protected by sanctuary - {(1f - DefenseMultiplier) * 100}% damage reduction");
                }
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Periodically reapply aura to keep allies protected
            if (IsAura && Time.time - _lastAuraTick >= AuraTickInterval)
            {
                _lastAuraTick = Time.time;
                // Note: The aura application is handled by the healer's AI/ability system
                // This effect is what gets applied to characters within the sanctuary
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Sanctuary fades...");
            }
            
            base.OnEffectRemoved();
        }
        
        /// <summary>
        /// Applies sanctuary to all allies within range of the healer.
        /// </summary>
        public static int ApplySanctuary(Character source, float range, float duration)
        {
            if (source == null) return 0;
            
            int protectedCount = 0;
            Vector3 center = source.transform.position;
            
            // Get skill system for XP tracking
            var skillSystem = source.GetComponent<ArchetypeSkillSystem>();
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(source, character)) continue;
                
                float distance = Vector3.Distance(center, character.transform.position);
                if (distance <= range)
                {
                    ApplyToCharacter(character, source, duration);
                    
                    // Grant skill XP for buffing ally
                    skillSystem?.OnAbilityBuffedAlly("sanctuary", character);
                    
                    protectedCount++;
                }
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[SanctuaryEffect] {source.m_name} sanctuary protecting {protectedCount} allies");
            }
            
            return protectedCount;
        }
        
        private static void ApplyToCharacter(Character target, Character source, float duration)
        {
            var seman = target.GetSEMan();
            if (seman == null) 
            {
                Debug.LogWarning($"[SanctuaryEffect] Cannot apply to {target.m_name} - no SEMan!");
                return;
            }
            
            var effect = ScriptableObject.CreateInstance<SanctuaryEffect>();
            effect.name = StatusEffectManager.EFFECT_SANCTUARY;
            effect.Duration = duration;
            effect.SourceCharacter = source;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_SANCTUARY);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            // Also set m_ttl for duration
            effect.m_ttl = duration;
            
            int existingHash = StatusEffectManager.EFFECT_SANCTUARY.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            // Verify the effect was actually added
            bool wasAdded = seman.HaveStatusEffect(existingHash);
            Debug.Log($"[SanctuaryEffect] Applied to {target.m_name}, hasIcon={icon != null}, isPlayer={target.IsPlayer()}, wasAdded={wasAdded}");
        }
        
        public new SanctuaryEffect Clone()
        {
            var clone = (SanctuaryEffect)base.Clone();
            if (clone != null)
            {
                clone.IsAura = IsAura;
                clone.AuraRange = AuraRange;
                clone.AuraTickInterval = AuraTickInterval;
                clone.BlocksProjectiles = BlocksProjectiles;
            }
            return clone;
        }
    }
}
