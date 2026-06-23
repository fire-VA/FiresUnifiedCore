using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Tank
{
    /// <summary>
    /// Tank + Rogue = Shadow Guardian - Counter Shadow
    /// After blocking, teleport behind the attacker and strike.
    /// 
    /// THEME: A defender who strikes from unexpected angles while protecting allies.
    /// The Shadow Guardian combines stalwart defense with rogue trickery,
    /// turning enemy attacks into opportunities for devastating counters.
    /// </summary>
    public class ShadowGuardianEffect : HybridAbilityEffect
    {
        public float CounterDamageMultiplier { get; set; } = 2.0f;
        public int CounterCharges { get; set; } = 3;
        private int _remainingCharges;
        
        public override string Description => 
            $"After blocking, teleport behind attacker\n" +
            $"Counter strikes deal {CounterDamageMultiplier:F1}x damage\n" +
            $"Charges: {_remainingCharges}";
        
        public ShadowGuardianEffect()
        {
            m_name = "Counter Shadow";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Tank;
            SubArchetype = ArchetypeClass.Rogue;
        }
        
        protected override void OnEffectApplied()
        {
            _remainingCharges = CounterCharges;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Counter Shadow ready...");
            
            // Subtle stealth effect
            AbilityFXManager.SpawnEffect("vfx_ghost_death", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            // Check if we blocked and have charges
            if (_remainingCharges > 0 && attacker != null && hit.m_blockable && m_character != null)
            {
                // Check if character is blocking
                if (m_character.IsBlocking())
                {
                    // Teleport behind attacker
                    TeleportBehind(attacker);
                    _remainingCharges--;
                    
                    m_character.Message(MessageHud.MessageType.TopLeft, $"Counter! ({_remainingCharges} remaining)");
                    
                    if (_remainingCharges <= 0)
                    {
                        // Remove effect when charges depleted
                        m_character.GetSEMan()?.RemoveStatusEffect(name.GetStableHashCode(), true);
                    }
                }
            }
        }
        
        private void TeleportBehind(Character attacker)
        {
            if (m_character == null || attacker == null) return;
            
            // Calculate position behind attacker
            Vector3 behindPos = attacker.transform.position - attacker.transform.forward * 2f;
            
            // Spawn vanish effect at current position
            AbilityFXManager.SpawnEffect("vfx_odin_despawn", m_character.transform.position, null, 0.5f);
            
            // Move character
            m_character.transform.position = behindPos;
            m_character.transform.LookAt(attacker.transform);
            
            // Spawn appear effect
            AbilityFXManager.SpawnEffect("vfx_ghost_death", behindPos, null, 0.5f);
            AbilityFXManager.SpawnEffect("fx_backstab", attacker.transform.position, null, 1f);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<ShadowGuardianEffect>();
            effect.name = "CompanionCounterShadow";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionCounterShadow");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
}
