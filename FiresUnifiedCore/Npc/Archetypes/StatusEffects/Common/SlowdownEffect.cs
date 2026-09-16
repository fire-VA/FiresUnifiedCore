using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Common
{
    /// <summary>
    /// Slowdown effect - reduces movement speed by a percentage.
    /// Used by: Rogue caltrops, frost effects, traps
    /// 
    /// DESIGN: Reduces the character's movement speed while active by
    /// continuously reducing velocity each frame.
    /// </summary>
    public class SlowdownEffect : CompanionStatusEffectBase
    {
        private const float SlowdownDuration = 5f;

        /// <summary>Speed multiplier (0.5 = 50% speed, 0.3 = 30% speed)</summary>
        public float SpeedMultiplier { get; set; } = 0.5f;
        
        private Rigidbody _rb;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"Movement speed reduced to {SpeedMultiplier * 100:F0}%";
        
        public SlowdownEffect()
        {
            m_name = "Slowed";
            m_tooltip = "Movement speed reduced";
            Duration = SlowdownDuration;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                _rb = m_character.GetComponent<Rigidbody>();
                
                if (VerboseLogging)
                {
                    Debug.Log($"[SlowdownEffect] {m_character.m_name} slowed to {SpeedMultiplier * 100}% speed for {Duration}s");
                }
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null) return;
            
            // Reduce velocity by the slowdown factor
            // This effectively makes them move slower
            if (_rb != null && !_rb.isKinematic)
            {
                Vector3 velocity = _rb.linearVelocity;
                // Only affect horizontal movement, not falling
                velocity.x *= SpeedMultiplier;
                velocity.z *= SpeedMultiplier;
                _rb.linearVelocity = velocity;
            }
        }
        
        protected override void OnEffectRemoved()
        {
            _rb = null;
            
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[SlowdownEffect] {m_character.m_name} speed restored");
            }
        }
        
        public new SlowdownEffect Clone()
        {
            var clone = (SlowdownEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.SpeedMultiplier = SpeedMultiplier;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }
}
