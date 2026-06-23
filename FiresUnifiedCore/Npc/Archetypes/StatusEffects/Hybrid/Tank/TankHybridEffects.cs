using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Tank
{
    /// <summary>
    /// Tank + Ranger = Warden - Guardian's Volley
    /// Fire multiple arrows while maintaining a defensive stance.
    /// 
    /// THEME: A versatile protector who can engage at any range.
    /// The Warden guards allies from afar while remaining ready to
    /// close the distance and take hits when needed.
    /// </summary>
    public class WardenEffect : HybridAbilityEffect
    {
        public float DamageReduction { get; set; } = 0.8f;
        public float RangedDamageBonus { get; set; } = 1.2f;
        public int ArrowCount { get; set; } = 3;
        
        public override string Description => 
            $"Defensive stance with ranged capability\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken\n" +
            $"+{(RangedDamageBonus - 1f) * 100:F0}% ranged damage";
        
        public WardenEffect()
        {
            m_name = "Guardian's Volley";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Tank;
            SubArchetype = ArchetypeClass.Ranger;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Guardian's Volley active!");
            
            // Combined VFX
            AbilityFXManager.SpawnEffect("fx_shaman_protect", m_character?.transform.position ?? Vector3.zero, null, 0.8f);
            AbilityFXManager.SpawnEffect("fx_Lightning", m_character?.transform.position ?? Vector3.zero, null, 0.4f);
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            hit.m_damage.Modify(DamageReduction);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            // Bonus damage for ranged attacks
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows)
            {
                hitData.m_damage.Modify(RangedDamageBonus);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<WardenEffect>();
            effect.name = "CompanionGuardiansVolley";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionGuardiansVolley");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Tank + Mage = Spellbreaker - Arcane Fortress
    /// Creates a magical barrier that absorbs damage and reflects spells.
    /// 
    /// THEME: A warrior who uses magic to enhance their defenses.
    /// The Spellbreaker disrupts enemy magic while standing firm
    /// against physical assaults.
    /// </summary>
    public class SpellbreakerEffect : HybridAbilityEffect
    {
        public float ShieldAmount { get; set; } = 150f;
        public float DamageReduction { get; set; } = 0.85f;
        public float MagicReflect { get; set; } = 0.3f;
        
        private float _shieldRemaining;
        
        public override string Description => 
            $"Arcane barrier: {_shieldRemaining:F0}/{ShieldAmount:F0}\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken\n" +
            $"Reflects {MagicReflect * 100:F0}% magic damage";
        
        public SpellbreakerEffect()
        {
            m_name = "Arcane Fortress";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Tank;
            SubArchetype = ArchetypeClass.Mage;
        }
        
        protected override void OnEffectApplied()
        {
            _shieldRemaining = ShieldAmount;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Arcane Fortress!");
            
            AbilityFXManager.SpawnEffect("vfx_StaffShield", m_character?.transform.position ?? Vector3.zero, null, 1.5f);
            AbilityFXManager.SpawnEffect("fx_shaman_protect", m_character?.transform.position ?? Vector3.zero, null, 1f);
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            // Absorb damage with shield first
            float totalDamage = hit.GetTotalDamage();
            if (_shieldRemaining > 0 && totalDamage > 0)
            {
                float absorbed = Mathf.Min(_shieldRemaining, totalDamage);
                _shieldRemaining -= absorbed;
                hit.m_damage.Modify(1f - (absorbed / totalDamage));
                
                AbilityFXManager.SpawnEffect("vfx_blocked", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
                
                if (_shieldRemaining <= 0)
                {
                    m_character?.Message(MessageHud.MessageType.TopLeft, "Arcane shield depleted!");
                }
            }
            
            // Apply damage reduction to remaining damage
            hit.m_damage.Modify(DamageReduction);
            
            // Reflect magic damage
            if (attacker != null && !attacker.IsPlayer())
            {
                float magicDamage = hit.m_damage.m_fire + hit.m_damage.m_frost + hit.m_damage.m_lightning + hit.m_damage.m_spirit;
                if (magicDamage > 0)
                {
                    float reflectDamage = magicDamage * MagicReflect;
                    var reflectHit = new HitData
                    {
                        m_damage = { m_spirit = reflectDamage },
                        m_attacker = m_character.GetZDOID(),
                        m_point = attacker.transform.position
                    };
                    attacker.ApplyDamage(reflectHit, true, false, HitData.DamageModifier.Normal);
                    
                    AbilityFXManager.SpawnEffect("vfx_spiritbolt_explosion", attacker.transform.position, null, 0.5f);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<SpellbreakerEffect>();
            effect.name = "CompanionArcaneFortress";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionArcaneFortress");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Tank + Healer = Bastion - Protective Aura
    /// Nearby allies gain damage reduction and slow health regeneration.
    /// 
    /// THEME: A stalwart defender who heals allies while absorbing damage.
    /// The Bastion is the ultimate defensive support, creating a safe
    /// zone around themselves where allies can recover.
    /// </summary>
    public class BastionEffect : HybridAbilityEffect
    {
        public float AllyDamageReduction { get; set; } = 0.85f;
        public float HealPerSecond { get; set; } = 3f;
        public float AuraRange { get; set; } = 10f;
        
        private float _lastTickTime;
        private const float TICK_INTERVAL = 1f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Allies within {AuraRange:F0}m:\n" +
            $"-{(1f - AllyDamageReduction) * 100:F0}% damage taken\n" +
            $"+{HealPerSecond:F0} HP/sec";
        
        public BastionEffect()
        {
            m_name = "Protective Aura";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Tank;
            SubArchetype = ArchetypeClass.Healer;
            GroupRange = 10f;
        }
        
        protected override void OnEffectApplied()
        {
            _lastTickTime = Time.time;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Protective Aura active!");
            
            AbilityFXManager.SpawnEffect("fx_DvergerMage_Support_start", m_character?.transform.position ?? Vector3.zero, null, GroupRange / 15f);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (Time.time - _lastTickTime >= TICK_INTERVAL)
            {
                _lastTickTime = Time.time;
                HealNearbyAllies();
            }
        }
        
        private void HealNearbyAllies()
        {
            if (m_character == null) return;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= AuraRange)
                {
                    character.Heal(HealPerSecond, true);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<BastionEffect>();
            effect.name = "CompanionProtectiveAura";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionProtectiveAura");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Tank + Monk = Iron Monk - Iron Stance
    /// Becomes immovable, reflecting a portion of blocked damage.
    /// 
    /// THEME: A disciplined warrior with unshakeable defense and swift counters.
    /// The Iron Monk combines martial discipline with defensive mastery,
    /// turning perfect defense into offense.
    /// </summary>
    public class IronMonkEffect : HybridAbilityEffect
    {
        public float DamageReduction { get; set; } = 0.6f;
        public float ReflectPercent { get; set; } = 0.25f;
        public float CounterDamageBonus { get; set; } = 1.5f;
        
        private bool _wasHitRecently;
        private float _counterWindowEnd;
        
        public override string Description => 
            $"Iron Stance active\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken\n" +
            $"Reflect {ReflectPercent * 100:F0}% of blocked damage\n" +
            $"Counters deal +{(CounterDamageBonus - 1f) * 100:F0}% damage";
        
        public IronMonkEffect()
        {
            m_name = "Iron Stance";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Tank;
            SubArchetype = ArchetypeClass.Monk;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.Center, "Iron Stance!");
            
            AbilityFXManager.SpawnEffect("fx_shaman_protect", m_character?.transform.position ?? Vector3.zero, null, 0.8f);
            AbilityFXManager.SpawnEffect("vfx_Cold", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            float originalDamage = hit.GetTotalDamage();
            hit.m_damage.Modify(DamageReduction);
            
            // Reflect damage when blocking
            if (m_character != null && m_character.IsBlocking() && attacker != null && !attacker.IsPlayer())
            {
                float reflectDamage = originalDamage * ReflectPercent;
                var reflectHit = new HitData
                {
                    m_damage = { m_blunt = reflectDamage },
                    m_attacker = m_character.GetZDOID(),
                    m_point = attacker.transform.position
                };
                attacker.ApplyDamage(reflectHit, true, false, HitData.DamageModifier.Normal);
                
                AbilityFXManager.SpawnEffect("fx_fenring_frost", attacker.transform.position, null, 0.5f);
                
                // Enable counter window
                _wasHitRecently = true;
                _counterWindowEnd = Time.time + 2f;
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            // Bonus damage during counter window
            if (_wasHitRecently && Time.time < _counterWindowEnd)
            {
                hitData.m_damage.Modify(CounterDamageBonus);
                _wasHitRecently = false;
                
                AbilityFXManager.SpawnEffect("fx_crit", m_character?.transform.position ?? Vector3.zero, null, 1f);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<IronMonkEffect>();
            effect.name = "CompanionIronStance";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionIronStance");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
}
