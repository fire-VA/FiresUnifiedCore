using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Rogue
{
    /// <summary>
    /// Rogue + Tank = Duelist - Perfect Riposte
    /// After a successful parry, counter with a guaranteed critical hit.
    /// 
    /// THEME: A nimble fighter who parries and ripostes with deadly precision.
    /// The Duelist turns defense into offense with elegant counter-attacks.
    /// </summary>
    public class DuelistEffect : HybridAbilityEffect
    {
        private const float ParryWindowDuration = 1.5f;

        public float CritMultiplier { get; set; } = 2.5f;
        public float DamageReduction { get; set; } = 0.9f;
        public int RiposteCharges { get; set; } = 3;
        
        private int _remainingCharges;
        private bool _parryWindow;
        private float _parryWindowEnd;
        
        public override string Description => 
            $"Perfect Riposte ready\n" +
            $"Parry counters deal {CritMultiplier:F1}x damage\n" +
            $"Charges: {_remainingCharges}";
        
        public DuelistEffect()
        {
            m_name = "Perfect Riposte";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Rogue;
            SubArchetype = ArchetypeClass.Tank;
        }
        
        protected override void OnEffectApplied()
        {
            _remainingCharges = RiposteCharges;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Perfect Riposte ready...");
            AbilityFXManager.SpawnEffect("vfx_ghost_death", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            hit.m_damage.Modify(DamageReduction);
            
            // Check for parry
            if (m_character != null && m_character.IsBlocking() && _remainingCharges > 0)
            {
                _parryWindow = true;
                _parryWindowEnd = Time.time + ParryWindowDuration;
                AbilityFXManager.SpawnEffect("vfx_perfectblock", m_character.transform.position, null, 1f);
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (_parryWindow && Time.time < _parryWindowEnd && _remainingCharges > 0)
            {
                hitData.m_damage.Modify(CritMultiplier);
                _remainingCharges--;
                _parryWindow = false;
                
                m_character?.Message(MessageHud.MessageType.TopLeft, $"Riposte! ({_remainingCharges} remaining)");
                AbilityFXManager.SpawnEffect("fx_crit", m_character?.transform.position ?? Vector3.zero, null, 1f);
                
                if (_remainingCharges <= 0)
                {
                    m_character?.GetSEMan()?.RemoveStatusEffect(name.GetStableHashCode(), true);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<DuelistEffect>();
            effect.name = "CompanionPerfectRiposte";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Rogue + Paladin = Shadow Priest - Holy Assassination
    /// Teleport to target and strike with spirit damage that heals allies.
    /// 
    /// THEME: A holy assassin who punishes the wicked.
    /// The Shadow Priest combines stealth with divine judgment.
    /// </summary>
    public class ShadowPriestEffect : HybridAbilityEffect
    {
        public float SpiritDamageBonus { get; set; } = 40f;
        public float AllyHealPercent { get; set; } = 0.2f;
        public float HealRange { get; set; } = 10f;
        
        public override string Description => 
            $"Holy Assassination active\n" +
            $"+{SpiritDamageBonus:F0} spirit damage\n" +
            $"Allies heal for {AllyHealPercent * 100:F0}% of damage";
        
        public ShadowPriestEffect()
        {
            m_name = "Holy Assassination";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Rogue;
            SubArchetype = ArchetypeClass.Paladin;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Holy Assassination!");
            AbilityFXManager.SpawnEffect("vfx_ghost_death", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
            AbilityFXManager.SpawnEffect("vfx_ghost_hit", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.m_spirit += SpiritDamageBonus;
            
            // Heal nearby allies based on damage dealt
            float totalDamage = hitData.GetTotalDamage();
            float healAmount = totalDamage * AllyHealPercent;
            
            if (m_character != null && healAmount > 0)
            {
                var characters = Character.GetAllCharacters();
                foreach (var character in characters)
                {
                    if (character == null || character.IsDead()) continue;
                    if (character == m_character) continue;
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
            var effect = ScriptableObject.CreateInstance<ShadowPriestEffect>();
            effect.name = "CompanionHolyAssassination";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Rogue + Berserker = Blade Dancer - Dance of Death
    /// Rapidly attack all nearby enemies, gaining speed with each hit.
    /// 
    /// THEME: A whirlwind of blades that grows more deadly with each kill.
    /// The Blade Dancer combines rogue agility with berserker ferocity.
    /// </summary>
    public class BladeDancerEffect : HybridAbilityEffect
    {
        public float BaseDamageBonus { get; set; } = 1.2f;
        public float SpeedBonusPerHit { get; set; } = 0.03f;
        public float MaxSpeedBonus { get; set; } = 1.5f;
        public float CritChanceBonus { get; set; } = 0.15f;
        
        private int _hitCount = 0;
        
        public override string Description
        {
            get
            {
                float currentSpeed = 1f + Mathf.Min(_hitCount * SpeedBonusPerHit, MaxSpeedBonus - 1f);
                return $"Dance of Death\n" +
                       $"+{(BaseDamageBonus - 1f) * 100:F0}% damage\n" +
                       $"+{CritChanceBonus * 100:F0}% crit chance\n" +
                       $"Speed: +{(currentSpeed - 1f) * 100:F0}% ({_hitCount} hits)";
            }
        }
        
        public BladeDancerEffect()
        {
            m_name = "Dance of Death";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Rogue;
            SubArchetype = ArchetypeClass.Berserker;
        }
        
        protected override void OnEffectApplied()
        {
            _hitCount = 0;
            m_character?.Message(MessageHud.MessageType.Center, "DANCE OF DEATH!");
            AbilityFXManager.SpawnEffect("vfx_ghost_death", m_character?.transform.position ?? Vector3.zero, null, 0.8f);
            AbilityFXManager.SpawnEffect("vfx_MeadBzerker", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(BaseDamageBonus);
            _hitCount++;
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<BladeDancerEffect>();
            effect.name = "CompanionDanceOfDeath";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Rogue + Ranger = Scout - Ambush
    /// Fire a poisoned arrow then dash in for a melee strike.
    /// 
    /// THEME: A versatile operative skilled in both melee and ranged combat.
    /// The Scout excels at initiating combat and adapting to any situation.
    /// 
    /// FX DESIGN: Lightning effect spawns at the IMPACT point of ranged attacks,
    /// not at the companion's origin. This creates a satisfying visual when arrows hit.
    /// </summary>
    public class ScoutEffect : HybridAbilityEffect
    {
        public float RangedDamageBonus { get; set; } = 1.15f;
        public float MeleeDamageBonus { get; set; } = 1.2f;
        public float PoisonDamage { get; set; } = 5f;
        public float CritChanceBonus { get; set; } = 0.12f;
        
        // Track if we should spawn lightning on next ranged hit
        private bool _nextRangedHitHasLightning = true;
        
        public override string Description => 
            $"Ambush tactics active\n" +
            $"+{(RangedDamageBonus - 1f) * 100:F0}% ranged damage\n" +
            $"+{(MeleeDamageBonus - 1f) * 100:F0}% melee damage\n" +
            $"+{CritChanceBonus * 100:F0}% crit chance";
        
        public ScoutEffect()
        {
            m_name = "Ambush";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Rogue;
            SubArchetype = ArchetypeClass.Ranger;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Ambush ready!");
            // Only spawn the ghost/stealth effect on activation - lightning spawns on arrow impact
            AbilityFXManager.SpawnEffect("vfx_ghost_death", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
            _nextRangedHitHasLightning = true;
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows)
            {
                hitData.m_damage.Modify(RangedDamageBonus);
                hitData.m_damage.m_poison += PoisonDamage;
                
                // Mark that this hit should spawn lightning at impact point
                // The lightning effect will be spawned via OnHitCallback when the projectile lands
                if (_nextRangedHitHasLightning)
                {
                    // Store that we want lightning on this hit - will be read by OnHitCallback
                    hitData.m_skill = skill; // This is already set, but we mark it
                    
                    // Spawn lightning at the hit point (hitData.m_point will be set when projectile hits)
                    // This is handled via the HitData - we add lightning damage to trigger the visual
                    hitData.m_damage.m_lightning += 1f; // Small lightning damage triggers the fx_Lightning effect on hit
                    
                    _nextRangedHitHasLightning = false;
                }
            }
            else
            {
                hitData.m_damage.Modify(MeleeDamageBonus);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<ScoutEffect>();
            effect.name = "CompanionAmbush";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Rogue + Mage = Spellthief - Mana Drain
    /// Strike an enemy to steal eitr and briefly silence them.
    /// 
    /// THEME: A cunning infiltrator who steals magical energy from foes.
    /// The Spellthief disrupts enemy casters while empowering themselves.
    /// </summary>
    public class SpellthiefEffect : HybridAbilityEffect
    {
        public float EitrStealAmount { get; set; } = 30f;
        public float DamageBonus { get; set; } = 1.1f;
        public int DrainCharges { get; set; } = 5;
        
        private int _remainingCharges;
        
        public override string Description => 
            $"Mana Drain active\n" +
            $"Steal {EitrStealAmount:F0} eitr on hit\n" +
            $"Charges: {_remainingCharges}";
        
        public SpellthiefEffect()
        {
            m_name = "Mana Drain";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Rogue;
            SubArchetype = ArchetypeClass.Mage;
        }
        
        protected override void OnEffectApplied()
        {
            _remainingCharges = DrainCharges;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Mana Drain ready...");
            AbilityFXManager.SpawnEffect("vfx_ghost_death", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
            AbilityFXManager.SpawnEffect("vfx_Potion_eitr_minor", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
            
            if (_remainingCharges > 0)
            {
                // Add eitr to player
                if (m_character is Player player)
                {
                    player.AddEitr(EitrStealAmount);
                }
                
                _remainingCharges--;
                AbilityFXManager.SpawnEffect("vfx_Potion_eitr_minor", m_character?.transform.position ?? Vector3.zero, null, 0.4f);
                
                if (_remainingCharges <= 0)
                {
                    m_character?.GetSEMan()?.RemoveStatusEffect(name.GetStableHashCode(), true);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<SpellthiefEffect>();
            effect.name = "CompanionManaDrain";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Rogue + Healer = Medicine Man - Life Steal
    /// Attacks drain life from enemies and transfer it to the lowest health ally.
    /// 
    /// THEME: A shadowy healer who uses forbidden arts.
    /// The Medicine Man sustains allies through stolen life force.
    /// </summary>
    public class MedicineManEffect : HybridAbilityEffect
    {
        public float LifestealPercent { get; set; } = 0.25f;
        public float AllyTransferPercent { get; set; } = 0.5f;
        public float HealRange { get; set; } = 15f;
        
        public override string Description => 
            $"Life Steal active\n" +
            $"{LifestealPercent * 100:F0}% lifesteal\n" +
            $"{AllyTransferPercent * 100:F0}% transferred to lowest ally";
        
        public MedicineManEffect()
        {
            m_name = "Life Steal";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Rogue;
            SubArchetype = ArchetypeClass.Healer;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Life Steal active!");
            AbilityFXManager.SpawnEffect("vfx_ghost_death", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            float totalDamage = hitData.GetTotalDamage();
            float stolenLife = totalDamage * LifestealPercent;
            
            // Self heal
            m_character?.Heal(stolenLife * (1f - AllyTransferPercent), true);
            
            // Transfer to lowest health ally
            if (m_character != null)
            {
                Character lowestAlly = null;
                float lowestHealthPercent = 1f;
                
                var characters = Character.GetAllCharacters();
                foreach (var character in characters)
                {
                    if (character == null || character.IsDead()) continue;
                    if (character == m_character) continue;
                    if (BaseAI.IsEnemy(m_character, character)) continue;
                    
                    float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                    if (dist <= HealRange && character.GetHealthPercentage() < lowestHealthPercent)
                    {
                        lowestHealthPercent = character.GetHealthPercentage();
                        lowestAlly = character;
                    }
                }
                
                if (lowestAlly != null)
                {
                    lowestAlly.Heal(stolenLife * AllyTransferPercent, true);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<MedicineManEffect>();
            effect.name = "CompanionLifeSteal";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Rogue + Monk = Shadow Monk (Ninja) - Shadow Step
    /// Teleport behind target and deliver a stunning chi strike.
    /// 
    /// THEME: A martial artist who strikes from the shadows.
    /// The Ninja combines stealth with devastating martial techniques.
    /// </summary>
    public class NinjaEffect : HybridAbilityEffect
    {
        public float DamageBonus { get; set; } = 1.3f;
        public float CritChanceBonus { get; set; } = 0.2f;
        public float StunDuration { get; set; } = 1.5f;
        public int ShadowStepCharges { get; set; } = 3;
        
        private int _remainingCharges;
        
        public override string Description => 
            $"Shadow Step ready\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"+{CritChanceBonus * 100:F0}% crit, {StunDuration:F1}s stun\n" +
            $"Charges: {_remainingCharges}";
        
        public NinjaEffect()
        {
            m_name = "Shadow Step";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Rogue;
            SubArchetype = ArchetypeClass.Monk;
        }
        
        protected override void OnEffectApplied()
        {
            _remainingCharges = ShadowStepCharges;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Shadow Step ready...");
            AbilityFXManager.SpawnEffect("vfx_odin_despawn", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
            
            if (_remainingCharges > 0)
            {
                // Add stagger
                hitData.m_staggerMultiplier *= 2f;
                _remainingCharges--;
                
                AbilityFXManager.SpawnEffect("fx_fenring_frost", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
                
                if (_remainingCharges <= 0)
                {
                    m_character?.GetSEMan()?.RemoveStatusEffect(name.GetStableHashCode(), true);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<NinjaEffect>();
            effect.name = "CompanionShadowStep";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
}
