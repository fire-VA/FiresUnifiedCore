using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Rogue
{
    /// <summary>
    /// Caltrops effect - applied to enemies when they step on rogue-placed caltrops.
    /// Slows and damages enemies over time.
    /// Used by: Rogue archetype
    /// 
    /// DESIGN: When an enemy steps on caltrops, they are slowed and take minor
    /// piercing damage over time (like stepping on sharp objects).
    /// </summary>
    public class CaltropsEffect : CompanionStatusEffectBase
    {
        /// <summary>Speed multiplier (0.4 = 40% speed, very slow).</summary>
        public float SpeedMultiplier { get; set; } = 0.4f;
        
        /// <summary>Damage per tick.</summary>
        public float DamagePerTick { get; set; } = 2f;
        
        /// <summary>Time between damage ticks.</summary>
        public float TickInterval { get; set; } = 1f;
        
        private float _lastTickTime;
        private Rigidbody _rb;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"Speed reduced to {SpeedMultiplier * 100:F0}%\n" +
            $"{DamagePerTick:F0} pierce damage every {TickInterval:F1}s";
        
        public CaltropsEffect()
        {
            m_name = "Caltrops";
            m_tooltip = "Stepping on caltrops - slowed and taking damage";
            Duration = 5f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                _rb = m_character.GetComponent<Rigidbody>();
                _lastTickTime = Time.time;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[CaltropsEffect] {m_character.m_name} stepped on caltrops - slowed to {SpeedMultiplier * 100}% for {Duration}s");
                }
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null || m_character.IsDead()) return;
            
            // Apply slowdown by reducing velocity
            if (_rb != null && !_rb.isKinematic)
            {
                Vector3 velocity = _rb.linearVelocity;
                velocity.x *= SpeedMultiplier;
                velocity.z *= SpeedMultiplier;
                _rb.linearVelocity = velocity;
            }
            
            // Apply damage on tick interval
            if (Time.time - _lastTickTime >= TickInterval)
            {
                _lastTickTime = Time.time;
                
                // Apply piercing damage (caltrops are sharp!)
                HitData hitData = new HitData();
                hitData.m_damage.m_pierce = DamagePerTick;
                hitData.m_point = m_character.transform.position;
                hitData.m_dir = Vector3.down;
                hitData.m_pushForce = 0f;
                hitData.m_staggerMultiplier = 0f;
                hitData.m_dodgeable = false;
                hitData.m_blockable = false;
                
                // Set attacker if we have a source
                if (SourceCharacter != null)
                {
                    var zview = SourceCharacter.GetComponent<ZNetView>();
                    if (zview != null && zview.GetZDO() != null)
                    {
                        hitData.m_attacker = zview.GetZDO().m_uid;
                    }
                }
                
                m_character.Damage(hitData);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[CaltropsEffect] {m_character.m_name} took {DamagePerTick} pierce damage from caltrops");
                }
            }
        }
        
        protected override void OnEffectRemoved()
        {
            _rb = null;
            
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[CaltropsEffect] {m_character.m_name} escaped the caltrops");
            }
        }
        
        public new CaltropsEffect Clone()
        {
            var clone = (CaltropsEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.SpeedMultiplier = SpeedMultiplier;
                clone.DamagePerTick = DamagePerTick;
                clone.TickInterval = TickInterval;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }
}
