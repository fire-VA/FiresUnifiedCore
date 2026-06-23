using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Mage
{
    /// <summary>
    /// Mage + Tank = Battlemage - Arcane Armor
    /// Magical shields absorb damage and explode when broken.
    /// 
    /// THEME: A heavily armored spellcaster who wades into melee.
    /// The Battlemage combines magical power with physical resilience.
    /// </summary>
    public class BattlemageEffect : HybridAbilityEffect
    {
        public float ShieldAmount { get; set; } = 120f;
        public float DamageReduction { get; set; } = 0.85f;
        public float ShieldExplosionDamage { get; set; } = 80f;
        public float ExplosionRange { get; set; } = 6f;
        
        private float _shieldRemaining;
        
        public override string Description => 
            $"Arcane Armor: {_shieldRemaining:F0}/{ShieldAmount:F0}\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken\n" +
            $"Explodes for {ShieldExplosionDamage:F0} when broken";
        
        public BattlemageEffect()
        {
            m_name = "Arcane Armor";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Mage;
            SubArchetype = ArchetypeClass.Tank;
        }
        
        protected override void OnEffectApplied()
        {
            _shieldRemaining = ShieldAmount;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Arcane Armor!");
            AbilityFXManager.SpawnEffect("vfx_StaffShield", m_character?.transform.position ?? Vector3.zero, null, 1.2f);
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            float totalDamage = hit.GetTotalDamage();
            if (_shieldRemaining > 0 && totalDamage > 0)
            {
                float absorbed = Mathf.Min(_shieldRemaining, totalDamage);
                _shieldRemaining -= absorbed;
                hit.m_damage.Modify(1f - (absorbed / totalDamage));
                
                if (_shieldRemaining <= 0)
                {
                    // Shield broken - explode!
                    TriggerShieldExplosion();
                }
            }
            
            hit.m_damage.Modify(DamageReduction);
        }
        
        private void TriggerShieldExplosion()
        {
            if (m_character == null) return;
            
            m_character.Message(MessageHud.MessageType.Center, "Shield Explodes!");
            AbilityFXManager.SpawnEffect("vfx_fireball_explosion", m_character.transform.position, null, ExplosionRange / 8f);
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (!BaseAI.IsEnemy(m_character, character)) continue;
                if (character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= ExplosionRange)
                {
                    var hit = new HitData
                    {
                        m_damage = { m_fire = ShieldExplosionDamage * 0.5f, m_lightning = ShieldExplosionDamage * 0.5f },
                        m_attacker = m_character.GetZDOID(),
                        m_point = character.transform.position
                    };
                    character.ApplyDamage(hit, true, true, HitData.DamageModifier.Normal);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<BattlemageEffect>();
            effect.name = "CompanionArcaneArmor";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Mage + Paladin = Hierophant - Divine Wrath
    /// Call down holy fire that damages enemies and blesses allies.
    /// 
    /// THEME: A divine spellcaster channeling the power of the gods.
    /// The Hierophant combines arcane and divine magic.
    /// </summary>
    public class HierophantEffect : HybridAbilityEffect
    {
        public float SpiritDamageBonus { get; set; } = 25f;
        public float FireDamageBonus { get; set; } = 20f;
        public float AllyBuffDuration { get; set; } = 10f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Divine Wrath active\n" +
            $"+{SpiritDamageBonus:F0} spirit + {FireDamageBonus:F0} fire damage\n" +
            $"Allies gain Holy Smite ({AllyBuffDuration:F0}s)";
        
        public HierophantEffect()
        {
            m_name = "Divine Wrath";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Mage;
            SubArchetype = ArchetypeClass.Paladin;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.Center, "DIVINE WRATH!");
            AbilityFXManager.SpawnEffect("vfx_spiritbolt_explosion", m_character?.transform.position ?? Vector3.zero, null, 1f);
            AbilityFXManager.SpawnEffect("vfx_fireball_explosion", m_character?.transform.position ?? Vector3.zero, null, 0.8f);
            
            // Buff nearby allies
            BuffAllies();
        }
        
        private void BuffAllies()
        {
            if (m_character == null) return;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= GroupRange)
                {
                    StatusEffectManager.ApplyHolySmite(character, AllyBuffDuration);
                }
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.m_spirit += SpiritDamageBonus;
            hitData.m_damage.m_fire += FireDamageBonus;
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<HierophantEffect>();
            effect.name = "CompanionDivineWrath";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Mage + Berserker = Pyromancer - Inferno
    /// Surround yourself with flames, damaging nearby enemies.
    /// 
    /// THEME: A fire mage consumed by the flames they wield.
    /// The Pyromancer sacrifices control for devastating fire power.
    /// </summary>
    public class PyromancerEffect : HybridAbilityEffect
    {
        public float FireDamageBonus { get; set; } = 40f;
        public float AuraDamagePerSecond { get; set; } = 15f;
        public float AuraRange { get; set; } = 5f;
        
        private float _lastAuraTick;
        
        public override string Description => 
            $"Inferno active\n" +
            $"+{FireDamageBonus:F0} fire damage\n" +
            $"Burn nearby enemies for {AuraDamagePerSecond:F0}/sec";
        
        public PyromancerEffect()
        {
            m_name = "Inferno";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Mage;
            SubArchetype = ArchetypeClass.Berserker;
        }
        
        protected override void OnEffectApplied()
        {
            _lastAuraTick = Time.time;
            m_character?.Message(MessageHud.MessageType.Center, "INFERNO!");
            AbilityFXManager.SpawnEffect("vfx_spray_fire", m_character?.transform.position ?? Vector3.zero, null, 1f);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (Time.time - _lastAuraTick >= 1f)
            {
                _lastAuraTick = Time.time;
                DealAuraDamage();
            }
        }
        
        private void DealAuraDamage()
        {
            if (m_character == null) return;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (!BaseAI.IsEnemy(m_character, character)) continue;
                if (character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= AuraRange)
                {
                    var hit = new HitData
                    {
                        m_damage = { m_fire = AuraDamagePerSecond },
                        m_attacker = m_character.GetZDOID(),
                        m_point = character.transform.position
                    };
                    character.ApplyDamage(hit, true, true, HitData.DamageModifier.Normal);
                }
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.m_fire += FireDamageBonus;
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<PyromancerEffect>();
            effect.name = "CompanionInferno";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Mage + Rogue = Shadowcaster - Shadow Clone
    /// Create illusory copies that confuse enemies and cast spells.
    /// 
    /// THEME: A mage who manipulates shadows and illusions.
    /// The Shadowcaster blends arcane magic with deceptive tactics.
    /// </summary>
    public class ShadowcasterEffect : HybridAbilityEffect
    {
        public float DamageBonus { get; set; } = 1.15f;
        public float EvasionBonus { get; set; } = 0.2f;
        public float CritChanceBonus { get; set; } = 0.1f;
        
        public override string Description => 
            $"Shadow Clone active\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"+{EvasionBonus * 100:F0}% evasion\n" +
            $"+{CritChanceBonus * 100:F0}% crit chance";
        
        public ShadowcasterEffect()
        {
            m_name = "Shadow Clone";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Mage;
            SubArchetype = ArchetypeClass.Rogue;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Shadow Clone!");
            AbilityFXManager.SpawnEffect("vfx_ghost_death", m_character?.transform.position ?? Vector3.zero, null, 0.8f);
            AbilityFXManager.SpawnEffect("vfx_MeadHasty", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            // Chance to evade
            if (UnityEngine.Random.value < EvasionBonus)
            {
                hit.m_damage.Modify(0f); // Full evasion
                m_character?.Message(MessageHud.MessageType.TopLeft, "Clone absorbs hit!");
                AbilityFXManager.SpawnEffect("vfx_odin_despawn", m_character?.transform.position ?? Vector3.zero, null, 0.4f);
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<ShadowcasterEffect>();
            effect.name = "CompanionShadowClone";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Mage + Ranger = Storm Caller - Chain Lightning
    /// Lightning arcs between enemies, dealing increasing damage.
    /// 
    /// THEME: A mage who commands lightning and wind.
    /// The Storm Caller unleashes devastating electrical attacks.
    /// </summary>
    public class StormCallerEffect : HybridAbilityEffect
    {
        public float LightningDamage { get; set; } = 30f;
        public float ChainDamageIncrease { get; set; } = 1.15f;
        public int MaxChains { get; set; } = 4;
        public float ChainRange { get; set; } = 8f;
        
        public override string Description => 
            $"Chain Lightning active\n" +
            $"{LightningDamage:F0} lightning damage\n" +
            $"Chains up to {MaxChains} targets\n" +
            $"+{(ChainDamageIncrease - 1f) * 100:F0}% per chain";
        
        public StormCallerEffect()
        {
            m_name = "Chain Lightning";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Mage;
            SubArchetype = ArchetypeClass.Ranger;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Storm power!");
            AbilityFXManager.SpawnEffect("vfx_thunderbolt_explosion", m_character?.transform.position ?? Vector3.zero, null, 0.8f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.m_lightning += LightningDamage;
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<StormCallerEffect>();
            effect.name = "CompanionChainLightning";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Mage + Healer = Sage - Balance
    /// Damage dealt to enemies heals nearby allies.
    /// 
    /// THEME: A wise spellcaster balancing destruction and restoration.
    /// The Sage converts offensive magic into healing energy.
    /// </summary>
    public class SageEffect : HybridAbilityEffect
    {
        public float DamageToHealPercent { get; set; } = 0.25f;
        public float HealRange { get; set; } = 12f;
        public float DamageBonus { get; set; } = 1.1f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Balance active\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"{DamageToHealPercent * 100:F0}% of damage heals allies";
        
        public SageEffect()
        {
            m_name = "Balance";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Mage;
            SubArchetype = ArchetypeClass.Healer;
            GroupRange = 12f;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Balance achieved!");
            AbilityFXManager.SpawnEffect("vfx_MeadHasty", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
            AbilityFXManager.SpawnEffect("fx_creature_tamed", m_character?.transform.position ?? Vector3.zero, null, 0.4f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
            
            // Heal allies based on damage dealt
            float healAmount = hitData.GetTotalDamage() * DamageToHealPercent;
            if (m_character != null && healAmount > 0)
            {
                var characters = Character.GetAllCharacters();
                foreach (var character in characters)
                {
                    if (character == null || character.IsDead()) continue;
                    if (BaseAI.IsEnemy(m_character, character)) continue;
                    
                    float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                    if (dist <= HealRange)
                    {
                        character.Heal(healAmount, true);
                    }
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<SageEffect>();
            effect.name = "CompanionBalance";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Mage + Monk = Mystic - Arcane Meditation
    /// Enter a trance that rapidly regenerates eitr and empowers next spell.
    /// 
    /// THEME: A mage who has achieved perfect harmony of mind and magic.
    /// The Mystic combines arcane power with monastic discipline.
    /// </summary>
    public class MysticEffect : HybridAbilityEffect
    {
        public float EitrRegenBonus { get; set; } = 3f;
        public float StaminaRegenBonus { get; set; } = 1.5f;
        public float NextSpellMultiplier { get; set; } = 1.5f;
        
        private bool _empoweredSpellReady = true;
        
        public override string Description => 
            $"Arcane Meditation\n" +
            $"+{EitrRegenBonus:F0}x eitr regen\n" +
            $"+{StaminaRegenBonus:F0}x stamina regen\n" +
            $"Next spell: {(_empoweredSpellReady ? $"+{(NextSpellMultiplier - 1f) * 100:F0}%" : "used")}";
        
        public MysticEffect()
        {
            m_name = "Arcane Meditation";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Mage;
            SubArchetype = ArchetypeClass.Monk;
        }
        
        protected override void OnEffectApplied()
        {
            _empoweredSpellReady = true;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Arcane Meditation...");
            AbilityFXManager.SpawnEffect("vfx_Potion_eitr_minor", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
            AbilityFXManager.SpawnEffect("vfx_Cold", m_character?.transform.position ?? Vector3.zero, null, 0.3f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (_empoweredSpellReady)
            {
                hitData.m_damage.Modify(NextSpellMultiplier);
                _empoweredSpellReady = false;
                
                m_character?.Message(MessageHud.MessageType.TopLeft, "Empowered!");
                AbilityFXManager.SpawnEffect("fx_crit", m_character?.transform.position ?? Vector3.zero, null, 1f);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<MysticEffect>();
            effect.name = "CompanionArcaneMeditation";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
}
