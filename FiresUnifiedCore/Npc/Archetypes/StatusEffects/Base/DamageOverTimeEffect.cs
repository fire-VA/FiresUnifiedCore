using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Base
{
    /// <summary>
    /// Base class for damage over time (DoT) effects.
    /// Used for poison, burning, bleeding, etc.
    /// </summary>
    public class DamageOverTimeEffect : CompanionStatusEffectBase
    {
        /// <summary>Damage per tick.</summary>
        public float DamagePerTick { get; set; } = 5f;
        
        /// <summary>Time between damage ticks in seconds.</summary>
        public float TickInterval { get; set; } = 1f;
        
        /// <summary>Type of damage (for resistance calculations).</summary>
        public HitData.DamageType DamageType { get; set; } = HitData.DamageType.Poison;
        
        /// <summary>Whether the damage can be blocked.</summary>
        public bool Blockable { get; set; } = false;
        
        /// <summary>Whether the damage can be dodged.</summary>
        public bool Dodgeable { get; set; } = false;
        
        /// <summary>Stagger multiplier (0 = no stagger).</summary>
        public float StaggerMultiplier { get; set; } = 0f;
        
        /// <summary>Whether to show damage numbers.</summary>
        public bool ShowDamageText { get; set; } = true;
        
        private float _lastTickTime;
        private int _totalTicks;
        
        public DamageOverTimeEffect()
        {
            m_name = "DoT";
            m_tooltip = "Taking damage over time";
            Duration = 10f;
        }
        
        protected override void OnEffectApplied()
        {
            _lastTickTime = Time.time;
            _totalTicks = 0;
            
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[DamageOverTimeEffect] {m_character.m_name} taking {DamagePerTick} {DamageType} damage every {TickInterval}s for {Duration}s");
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null || m_character.IsDead()) return;
            
            // Apply damage on tick interval
            if (Time.time - _lastTickTime >= TickInterval)
            {
                _lastTickTime = Time.time;
                ApplyDamageTick();
            }
        }
        
        /// <summary>
        /// Applies a single tick of damage.
        /// </summary>
        protected virtual void ApplyDamageTick()
        {
            if (m_character == null || m_character.IsDead()) return;
            
            HitData hitData = new HitData();
            
            // Set damage based on type
            switch (DamageType)
            {
                case HitData.DamageType.Poison:
                    hitData.m_damage.m_poison = DamagePerTick;
                    break;
                case HitData.DamageType.Fire:
                    hitData.m_damage.m_fire = DamagePerTick;
                    break;
                case HitData.DamageType.Frost:
                    hitData.m_damage.m_frost = DamagePerTick;
                    break;
                case HitData.DamageType.Lightning:
                    hitData.m_damage.m_lightning = DamagePerTick;
                    break;
                case HitData.DamageType.Spirit:
                    hitData.m_damage.m_spirit = DamagePerTick;
                    break;
                case HitData.DamageType.Blunt:
                    hitData.m_damage.m_blunt = DamagePerTick;
                    break;
                case HitData.DamageType.Slash:
                    hitData.m_damage.m_slash = DamagePerTick;
                    break;
                case HitData.DamageType.Pierce:
                    hitData.m_damage.m_pierce = DamagePerTick;
                    break;
                default:
                    hitData.m_damage.m_damage = DamagePerTick;
                    break;
            }
            
            hitData.m_point = m_character.transform.position;
            hitData.m_dir = Vector3.down;
            hitData.m_pushForce = 0f;
            hitData.m_backstabBonus = 1f;
            hitData.m_staggerMultiplier = StaggerMultiplier;
            hitData.m_dodgeable = Dodgeable;
            hitData.m_blockable = Blockable;
            
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
            _totalTicks++;
            
            if (VerboseLogging)
            {
                Debug.Log($"[DamageOverTimeEffect] {m_character.m_name} tick #{_totalTicks}: {DamagePerTick} {DamageType} damage");
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[DamageOverTimeEffect] {m_character.m_name} DoT ended after {_totalTicks} ticks");
            }
        }
        
        public new DamageOverTimeEffect Clone()
        {
            var clone = (DamageOverTimeEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.DamagePerTick = DamagePerTick;
                clone.TickInterval = TickInterval;
                clone.DamageType = DamageType;
                clone.Blockable = Blockable;
                clone.Dodgeable = Dodgeable;
                clone.StaggerMultiplier = StaggerMultiplier;
                clone.ShowDamageText = ShowDamageText;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }
}
