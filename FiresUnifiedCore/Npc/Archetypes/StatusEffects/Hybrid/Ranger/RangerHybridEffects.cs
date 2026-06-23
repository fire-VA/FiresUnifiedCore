using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Ranger
{
    /// <summary>
    /// Ranger + Tank = Sentinel - Defensive Volley
    /// Fire arrows that create a barrier slowing enemies who pass through.
    /// 
    /// THEME: A stalwart defender who can engage threats at any distance.
    /// The Sentinel guards areas with ranged superiority while maintaining defensive posture.
    /// </summary>
    public class SentinelEffect : HybridAbilityEffect
    {
        public float DamageReduction { get; set; } = 0.9f;
        public float RangedDamageBonus { get; set; } = 1.1f;
        public float SlowOnHit { get; set; } = 0.7f;
        public float SlowDuration { get; set; } = 3f;
        
        public override string Description => 
            $"Defensive Volley active\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken\n" +
            $"+{(RangedDamageBonus - 1f) * 100:F0}% ranged damage\n" +
            $"Arrows slow by {(1f - SlowOnHit) * 100:F0}%";
        
        public SentinelEffect()
        {
            m_name = "Defensive Volley";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Ranger;
            SubArchetype = ArchetypeClass.Tank;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Defensive Volley!");
            AbilityFXManager.SpawnEffect("fx_shaman_protect", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
            AbilityFXManager.SpawnEffect("fx_Lightning", m_character?.transform.position ?? Vector3.zero, null, 0.3f);
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            hit.m_damage.Modify(DamageReduction);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows)
            {
                hitData.m_damage.Modify(RangedDamageBonus);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<SentinelEffect>();
            effect.name = "CompanionDefensiveVolley";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Ranger + Paladin = Divine Hunter - Blessed Arrows
    /// Arrows deal spirit damage and mark enemies, increasing ally damage.
    /// 
    /// THEME: A blessed archer whose arrows carry holy light.
    /// The Divine Hunter combines ranged precision with divine power.
    /// </summary>
    public class DivineHunterEffect : HybridAbilityEffect
    {
        public float SpiritDamageBonus { get; set; } = 20f;
        public float MarkedDamageBonus { get; set; } = 1.15f;
        public float MarkDuration { get; set; } = 8f;
        
        public override string Description => 
            $"Blessed Arrows active\n" +
            $"+{SpiritDamageBonus:F0} spirit damage\n" +
            $"Marked targets take +{(MarkedDamageBonus - 1f) * 100:F0}% from allies";
        
        public DivineHunterEffect()
        {
            m_name = "Blessed Arrows";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Ranger;
            SubArchetype = ArchetypeClass.Paladin;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Arrows blessed!");
            AbilityFXManager.SpawnEffect("vfx_spiritbolt_explosion", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows)
            {
                hitData.m_damage.m_spirit += SpiritDamageBonus;
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<DivineHunterEffect>();
            effect.name = "CompanionBlessedArrows";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Ranger + Berserker = Wild Hunter - Rain of Fury
    /// Fire a rapid volley of arrows with increasing damage.
    /// 
    /// THEME: A savage archer who unleashes devastating barrages.
    /// The Wild Hunter combines ranger precision with berserker fury.
    /// </summary>
    public class WildHunterEffect : HybridAbilityEffect
    {
        public float BaseDamageBonus { get; set; } = 1.15f;
        public float DamageBonusPerShot { get; set; } = 0.03f;
        public float MaxDamageBonus { get; set; } = 1.5f;
        public float AttackSpeedBonus { get; set; } = 1.2f;
        
        private int _shotsFired = 0;
        
        public override string Description
        {
            get
            {
                float currentBonus = Mathf.Min(BaseDamageBonus + _shotsFired * DamageBonusPerShot, MaxDamageBonus);
                return $"Rain of Fury\n" +
                       $"+{(currentBonus - 1f) * 100:F0}% damage\n" +
                       $"+{(AttackSpeedBonus - 1f) * 100:F0}% attack speed\n" +
                       $"Shots: {_shotsFired}";
            }
        }
        
        public WildHunterEffect()
        {
            m_name = "Rain of Fury";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Ranger;
            SubArchetype = ArchetypeClass.Berserker;
        }
        
        protected override void OnEffectApplied()
        {
            _shotsFired = 0;
            m_character?.Message(MessageHud.MessageType.Center, "RAIN OF FURY!");
            AbilityFXManager.SpawnEffect("vfx_MeadBzerker", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            float currentBonus = Mathf.Min(BaseDamageBonus + _shotsFired * DamageBonusPerShot, MaxDamageBonus);
            hitData.m_damage.Modify(currentBonus);
            _shotsFired++;
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<WildHunterEffect>();
            effect.name = "CompanionRainOfFury";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Ranger + Rogue = Sniper - Assassin's Shot
    /// A powerful shot from stealth that deals massive critical damage.
    /// 
    /// THEME: A precision marksman who strikes from concealment.
    /// The Sniper combines ranger accuracy with assassin lethality.
    /// </summary>
    public class SniperEffect : HybridAbilityEffect
    {
        public float FirstShotMultiplier { get; set; } = 3.0f;
        public float NormalDamageBonus { get; set; } = 1.15f;
        public float CritChanceBonus { get; set; } = 0.25f;
        
        private bool _firstShotUsed = false;
        
        public override string Description => 
            $"Assassin's Shot {(_firstShotUsed ? "used" : "ready")}\n" +
            $"First shot: {FirstShotMultiplier:F1}x damage\n" +
            $"+{CritChanceBonus * 100:F0}% crit chance";
        
        public SniperEffect()
        {
            m_name = "Assassin's Shot";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Ranger;
            SubArchetype = ArchetypeClass.Rogue;
        }
        
        protected override void OnEffectApplied()
        {
            _firstShotUsed = false;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Assassin's Shot ready...");
            AbilityFXManager.SpawnEffect("vfx_ghost_death", m_character?.transform.position ?? Vector3.zero, null, 0.4f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows)
            {
                if (!_firstShotUsed)
                {
                    hitData.m_damage.Modify(FirstShotMultiplier);
                    _firstShotUsed = true;
                    AbilityFXManager.SpawnEffect("fx_crit", m_character?.transform.position ?? Vector3.zero, null, 1.5f);
                    m_character?.Message(MessageHud.MessageType.TopLeft, "ASSASSIN'S SHOT!");
                }
                else
                {
                    hitData.m_damage.Modify(NormalDamageBonus);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<SniperEffect>();
            effect.name = "CompanionAssassinsShot";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Ranger + Mage = Arcane Archer - Elemental Arrow
    /// Fire an arrow that explodes with elemental damage on impact.
    /// 
    /// THEME: A marksman who infuses arrows with elemental magic.
    /// The Arcane Archer combines ranged precision with destructive magic.
    /// </summary>
    public class ArcaneArcherEffect : HybridAbilityEffect
    {
        public float FireDamage { get; set; } = 15f;
        public float FrostDamage { get; set; } = 15f;
        public float LightningDamage { get; set; } = 15f;
        public float AoERange { get; set; } = 4f;
        
        private int _elementCycle = 0;
        
        public override string Description => 
            $"Elemental Arrow active\n" +
            $"+{FireDamage:F0} fire / {FrostDamage:F0} frost / {LightningDamage:F0} lightning\n" +
            $"{AoERange:F0}m AoE on impact";
        
        public ArcaneArcherEffect()
        {
            m_name = "Elemental Arrow";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Ranger;
            SubArchetype = ArchetypeClass.Mage;
        }
        
        protected override void OnEffectApplied()
        {
            _elementCycle = 0;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Elemental arrows ready!");
            AbilityFXManager.SpawnEffect("vfx_MeadHasty", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows)
            {
                // Cycle through elements
                switch (_elementCycle % 3)
                {
                    case 0:
                        hitData.m_damage.m_fire += FireDamage;
                        break;
                    case 1:
                        hitData.m_damage.m_frost += FrostDamage;
                        break;
                    case 2:
                        hitData.m_damage.m_lightning += LightningDamage;
                        break;
                }
                _elementCycle++;
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<ArcaneArcherEffect>();
            effect.name = "CompanionElementalArrow";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Ranger + Healer = Forest Keeper - Nature's Blessing
    /// Arrows plant seeds that heal allies and damage enemies over time.
    /// 
    /// THEME: A nature guardian who protects allies while striking foes.
    /// The Forest Keeper channels nature's power through their arrows.
    /// </summary>
    public class ForestKeeperEffect : HybridAbilityEffect
    {
        public float PoisonDamage { get; set; } = 8f;
        public float HealPerHit { get; set; } = 10f;
        public float HealRange { get; set; } = 10f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Nature's Blessing active\n" +
            $"+{PoisonDamage:F0} poison damage per hit\n" +
            $"Heal allies for {HealPerHit:F0} HP per hit";
        
        public ForestKeeperEffect()
        {
            m_name = "Nature's Blessing";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Ranger;
            SubArchetype = ArchetypeClass.Healer;
            GroupRange = 10f;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Nature's Blessing!");
            AbilityFXManager.SpawnEffect("fx_natureweapon_hit", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows)
            {
                hitData.m_damage.m_poison += PoisonDamage;
                
                // Heal nearby allies
                if (m_character != null)
                {
                    var characters = Character.GetAllCharacters();
                    foreach (var character in characters)
                    {
                        if (character == null || character.IsDead()) continue;
                        if (BaseAI.IsEnemy(m_character, character)) continue;
                        
                        float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                        if (dist <= HealRange)
                        {
                            character.Heal(HealPerHit, true);
                        }
                    }
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<ForestKeeperEffect>();
            effect.name = "CompanionNaturesBlessing";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Ranger + Monk = Zen Archer - Perfect Shot
    /// Enter a focused state; your next arrow cannot miss and deals bonus damage.
    /// 
    /// THEME: A meditative marksman with perfect aim and endless stamina.
    /// The Zen Archer achieves perfect accuracy through inner focus.
    /// </summary>
    public class ZenArcherEffect : HybridAbilityEffect
    {
        public float DamageBonus { get; set; } = 1.25f;
        public float StaminaRegenBonus { get; set; } = 2f;
        public float PerfectShotMultiplier { get; set; } = 2.0f;
        public int PerfectShots { get; set; } = 3;
        
        private int _remainingPerfectShots;
        
        public override string Description => 
            $"Zen Focus active\n" +
            $"Perfect shots: {_remainingPerfectShots} ({PerfectShotMultiplier:F1}x damage)\n" +
            $"+{StaminaRegenBonus:F0}x stamina regen";
        
        public ZenArcherEffect()
        {
            m_name = "Perfect Shot";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Ranger;
            SubArchetype = ArchetypeClass.Monk;
        }
        
        protected override void OnEffectApplied()
        {
            _remainingPerfectShots = PerfectShots;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Zen focus achieved...");
            AbilityFXManager.SpawnEffect("vfx_Cold", m_character?.transform.position ?? Vector3.zero, null, 0.3f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows)
            {
                if (_remainingPerfectShots > 0)
                {
                    hitData.m_damage.Modify(PerfectShotMultiplier);
                    _remainingPerfectShots--;
                    
                    m_character?.Message(MessageHud.MessageType.TopLeft, $"Perfect! ({_remainingPerfectShots} remaining)");
                    AbilityFXManager.SpawnEffect("fx_crit", m_character?.transform.position ?? Vector3.zero, null, 1f);
                }
                else
                {
                    hitData.m_damage.Modify(DamageBonus);
                }
                
                // Restore stamina on ranged hits
                m_character?.AddStamina(5f);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<ZenArcherEffect>();
            effect.name = "CompanionPerfectShot";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
}
