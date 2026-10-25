using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Rogue
{
    /// <summary>
    /// Poison effect - damage over time that also slows the target.
    /// Used by: Rogue archetype (applied via attacks)
    /// 
    /// DESIGN: A potent poison that deals damage over time and
    /// slightly reduces the target's movement speed.
    /// </summary>
    public class PoisonEffect : Base.DamageOverTimeEffect
    {
        /// <summary>Speed reduction while poisoned (0.8 = 20% slower).</summary>
        public float SpeedReduction { get; set; } = 0.85f;
        
        /// <summary>Whether the poison can stack (increase damage on reapply).</summary>
        public bool CanStack { get; set; } = true;
        
        /// <summary>Maximum stacks of poison.</summary>
        public int MaxStacks { get; set; } = 3;
        
        /// <summary>Current stack count.</summary>
        public int CurrentStacks { get; set; } = 1;
        
        /// <summary>Damage multiplier per stack.</summary>
        public float DamagePerStack { get; set; } = 1.0f;
        
        private Rigidbody _rb;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"{DamagePerTick * CurrentStacks:F0} poison damage/s\n" +
            $"-{(1f - SpeedReduction) * 100:F0}% movement speed" +
            (CurrentStacks > 1 ? $" ({CurrentStacks} stacks)" : "");
        
        public PoisonEffect()
        {
            m_name = "Poisoned";
            Duration = 8f;
            DamagePerTick = 4f;
            TickInterval = 1f;
            DamageType = HitData.DamageType.Poison;
        }
        
        protected override void OnEffectApplied()
        {
            base.OnEffectApplied();
            
            if (m_character != null)
            {
                _rb = m_character.GetComponent<Rigidbody>();
                
                if (VerboseLogging)
                {
                    Debug.Log($"[PoisonEffect] {m_character.m_name} poisoned - {CurrentStacks} stacks, {DamagePerTick * CurrentStacks} dmg/tick");
                }
            }
        }
        
        protected override void ApplyDamageTick()
        {
            if (m_character == null || m_character.IsDead()) return;
            
            // Apply stacked damage
            float actualDamage = DamagePerTick * CurrentStacks * DamagePerStack;
            
            HitData hitData = new HitData();
            hitData.m_damage.m_poison = actualDamage;
            hitData.m_point = m_character.transform.position;
            hitData.m_dir = Vector3.down;
            hitData.m_pushForce = 0f;
            hitData.m_backstabBonus = 1f;
            hitData.m_staggerMultiplier = 0f;
            hitData.m_dodgeable = false;
            hitData.m_blockable = false;
            
            if (SourceCharacter != null)
            {
                var zview = SourceCharacter.GetComponent<ZNetView>();
                if (zview != null && zview.GetZDO() != null)
                {
                    hitData.m_attacker = zview.GetZDO().m_uid;
                }
            }
            
            m_character.Damage(hitData);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null || m_character.IsDead()) return;
            
            // Apply slowdown
            if (_rb != null && !_rb.isKinematic && SpeedReduction < 1f)
            {
                Vector3 velocity = _rb.linearVelocity;
                velocity.x *= SpeedReduction;
                velocity.z *= SpeedReduction;
                _rb.linearVelocity = velocity;
            }
        }
        
        /// <summary>
        /// Adds a stack of poison or refreshes duration.
        /// </summary>
        public void AddStack()
        {
            if (CurrentStacks < MaxStacks)
            {
                CurrentStacks++;
            }
            
            // Refresh duration
            m_ttl = Duration;
            
            if (VerboseLogging && m_character != null)
            {
                Debug.Log($"[PoisonEffect] {m_character.m_name} poison stacked to {CurrentStacks}");
            }
        }
        
        /// <summary>
        /// Applies poison to a target, stacking if already poisoned.
        /// </summary>
        public static bool ApplyPoison(Character target, Character source, float duration, float damagePerTick)
            => ApplyPoison(target, source, duration, damagePerTick, 3);

        /// <summary>
        /// rogue_virulence (2/3/4 stacks): as above with the caster's stack cap. A re-apply adds one stack up
        /// to the higher of the live cap and <paramref name="maxStacks"/>, and refreshes the duration. Damage per tick x stacks.
        /// </summary>
        public static bool ApplyPoison(Character target, Character source, float duration, float damagePerTick, int maxStacks)
        {
            if (target == null) return false;

            var seman = target.GetSEMan();
            if (seman == null) return false;

            int existingHash = StatusEffectManager.EFFECT_POISON.GetStableHashCode();
            maxStacks = Mathf.Max(1, maxStacks);

            // Check for existing poison to stack
            if (seman.HaveStatusEffect(existingHash))
            {
                var existing = seman.GetStatusEffect(existingHash) as PoisonEffect;
                if (existing != null)
                {
                    existing.MaxStacks = Mathf.Max(existing.MaxStacks, maxStacks);
                    if (duration > 0f) existing.Duration = duration;
                    if (source != null) existing.SourceCharacter = source;
                    existing.AddStack();
                    return true;
                }
            }
            
            // Apply new poison
            var effect = ScriptableObject.CreateInstance<PoisonEffect>();
            effect.name = StatusEffectManager.EFFECT_POISON;
            effect.Duration = duration;
            effect.DamagePerTick = damagePerTick;
            effect.SourceCharacter = source;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_POISON);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            seman.AddStatusEffect(effect, true);

            // SEMan adds a clone, which keeps only serialized fields; the properties set above go back to their defaults on it.
            // Set them on the live instance.
            if (seman.GetStatusEffect(existingHash) is PoisonEffect live)
            {
                live.Duration = duration;
                live.m_ttl = duration;
                live.DamagePerTick = damagePerTick;
                live.SourceCharacter = source;
                live.MaxStacks = maxStacks;
                live.CurrentStacks = 1;
            }

            Debug.Log($"[PoisonEffect] Applied to {target.m_name}, hasIcon={icon != null}");

            return true;
        }

        /// <summary>The target's live poison stacks, 0 when not poisoned.</summary>
        public static int GetStacks(Character target)
        {
            var seman = target != null ? target.GetSEMan() : null;
            return seman != null && seman.GetStatusEffect(StatusEffectManager.EFFECT_POISON.GetStableHashCode()) is PoisonEffect p ? p.CurrentStacks : 0;
        }

        /// <summary>rogue_plague_burst ("half their stacks"): sets the live stack count; 0 or less removes the poison. Returns the new count.</summary>
        public static int SetStacks(Character target, int stacks)
        {
            var seman = target != null ? target.GetSEMan() : null;
            int hash = StatusEffectManager.EFFECT_POISON.GetStableHashCode();
            if (seman == null || !(seman.GetStatusEffect(hash) is PoisonEffect p)) return 0;
            if (stacks <= 0) { seman.RemoveStatusEffect(hash, true); return 0; }
            p.CurrentStacks = Mathf.Min(stacks, p.MaxStacks);
            return p.CurrentStacks;
        }
        
        protected override void OnEffectRemoved()
        {
            _rb = null;
            base.OnEffectRemoved();
        }
        
        public new PoisonEffect Clone()
        {
            var clone = (PoisonEffect)base.Clone();
            if (clone != null)
            {
                clone.SpeedReduction = SpeedReduction;
                clone.CanStack = CanStack;
                clone.MaxStacks = MaxStacks;
                clone.CurrentStacks = CurrentStacks;
                clone.DamagePerStack = DamagePerStack;
            }
            return clone;
        }
    }
}
