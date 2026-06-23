using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Defines all hybrid archetype combinations with unique names, bonuses, and abilities.
    /// 
    /// DESIGN: Each hybrid gets:
    /// - A unique thematic name
    /// - Blended stat bonuses (70% main, 30% sub)
    /// - A special hybrid ability that combines aspects of both roles
    /// 
    /// NAMING CONVENTIONS:
    /// - Tank hybrids: Defensive themes (Guardian, Sentinel, Bulwark)
    /// - DPS hybrids: Offensive themes (Slayer, Reaper, Assassin)
    /// - Support hybrids: Utility themes (Warden, Sage, Mystic)
    /// </summary>
    public static class HybridArchetypeDefinitions
    {
        #region Hybrid Definition Structure
        
        public class HybridDefinition
        {
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public Color IconColor { get; set; }
            public string SpecialAbilityName { get; set; }
            public string SpecialAbilityDescription { get; set; }
            public float SpecialAbilityCooldown { get; set; } = 60f;
            
            // Bonus modifiers (applied on top of main archetype)
            public float BonusDamageMultiplier { get; set; } = 1.0f;
            public float BonusDefenseMultiplier { get; set; } = 1.0f;
            public float BonusSpeedMultiplier { get; set; } = 1.0f;
            public float BonusHealthRegen { get; set; } = 0f;
            public float BonusStaminaRegen { get; set; } = 0f;
            public float BonusEitrRegen { get; set; } = 0f;
            public float BonusCritChance { get; set; } = 0f;
            public float BonusBlockEfficiency { get; set; } = 0f;
        }
        
        #endregion
        
        #region Hybrid Registry
        
        private static readonly Dictionary<(ArchetypeClass, ArchetypeClass), HybridDefinition> _hybrids 
            = new Dictionary<(ArchetypeClass, ArchetypeClass), HybridDefinition>();
        
        static HybridArchetypeDefinitions()
        {
            InitializeAllHybrids();
        }
        
        /// <summary>
        /// Gets the hybrid definition for a main/sub archetype combination.
        /// </summary>
        public static HybridDefinition GetHybrid(ArchetypeClass main, ArchetypeClass sub)
        {
            if (main == sub || main == ArchetypeClass.None || sub == ArchetypeClass.None)
                return null;
            
            if (_hybrids.TryGetValue((main, sub), out var hybrid))
                return hybrid;
            
            return null;
        }
        
        /// <summary>
        /// Gets all registered hybrid definitions.
        /// </summary>
        public static IEnumerable<KeyValuePair<(ArchetypeClass, ArchetypeClass), HybridDefinition>> GetAllHybrids()
        {
            return _hybrids;
        }
        
        #endregion
        
        #region Hybrid Initialization
        
        private static void InitializeAllHybrids()
        {
            // ========================================
            // TANK HYBRIDS (Tank + X)
            // ========================================
            
            RegisterHybrid(ArchetypeClass.Tank, ArchetypeClass.Paladin, new HybridDefinition
            {
                DisplayName = "Crusader",
                Description = "A holy warrior combining unwavering defense with divine power.",
                IconColor = new Color(0.8f, 0.7f, 0.3f), // Gold-steel
                SpecialAbilityName = "Divine Bulwark",
                SpecialAbilityDescription = "Creates a holy shield that blocks all damage for 3s and heals nearby allies.",
                SpecialAbilityCooldown = 90f,
                BonusDefenseMultiplier = 1.15f,
                BonusHealthRegen = 2f,
                BonusBlockEfficiency = 0.1f
            });
            
            RegisterHybrid(ArchetypeClass.Tank, ArchetypeClass.Berserker, new HybridDefinition
            {
                DisplayName = "Juggernaut",
                Description = "An unstoppable force that grows stronger as battle rages.",
                IconColor = new Color(0.6f, 0.2f, 0.2f), // Dark red-steel
                SpecialAbilityName = "Unstoppable Charge",
                SpecialAbilityDescription = "Charges forward, stunning enemies and gaining damage reduction.",
                SpecialAbilityCooldown = 45f,
                BonusDamageMultiplier = 1.1f,
                BonusDefenseMultiplier = 1.1f,
                BonusStaminaRegen = 1.2f
            });
            
            RegisterHybrid(ArchetypeClass.Tank, ArchetypeClass.Rogue, new HybridDefinition
            {
                DisplayName = "Shadow Guardian",
                Description = "A defender who strikes from unexpected angles while protecting allies.",
                IconColor = new Color(0.3f, 0.3f, 0.5f), // Dark blue-grey
                SpecialAbilityName = "Counter Shadow",
                SpecialAbilityDescription = "After blocking, teleport behind the attacker and strike.",
                SpecialAbilityCooldown = 30f,
                BonusDefenseMultiplier = 1.05f,
                BonusCritChance = 0.1f,
                BonusSpeedMultiplier = 1.05f
            });
            
            RegisterHybrid(ArchetypeClass.Tank, ArchetypeClass.Ranger, new HybridDefinition
            {
                DisplayName = "Warden",
                Description = "A versatile protector who can engage at any range.",
                IconColor = new Color(0.4f, 0.5f, 0.3f), // Forest steel
                SpecialAbilityName = "Guardian's Volley",
                SpecialAbilityDescription = "Fire multiple arrows while maintaining a defensive stance.",
                SpecialAbilityCooldown = 40f,
                BonusDefenseMultiplier = 1.08f,
                BonusDamageMultiplier = 1.05f
            });
            
            RegisterHybrid(ArchetypeClass.Tank, ArchetypeClass.Mage, new HybridDefinition
            {
                DisplayName = "Spellbreaker",
                Description = "A warrior who uses magic to enhance their defenses.",
                IconColor = new Color(0.4f, 0.3f, 0.6f), // Purple-steel
                SpecialAbilityName = "Arcane Fortress",
                SpecialAbilityDescription = "Creates a magical barrier that absorbs damage and reflects spells.",
                SpecialAbilityCooldown = 60f,
                BonusDefenseMultiplier = 1.1f,
                BonusEitrRegen = 1f
            });
            
            RegisterHybrid(ArchetypeClass.Tank, ArchetypeClass.Healer, new HybridDefinition
            {
                DisplayName = "Bastion",
                Description = "A stalwart defender who heals allies while absorbing damage.",
                IconColor = new Color(0.5f, 0.7f, 0.8f), // Light blue-steel
                SpecialAbilityName = "Protective Aura",
                SpecialAbilityDescription = "Nearby allies gain damage reduction and slow health regeneration.",
                SpecialAbilityCooldown = 50f,
                BonusDefenseMultiplier = 1.1f,
                BonusHealthRegen = 3f
            });
            
            RegisterHybrid(ArchetypeClass.Tank, ArchetypeClass.Monk, new HybridDefinition
            {
                DisplayName = "Iron Monk",
                Description = "A disciplined warrior with unshakeable defense and swift counters.",
                IconColor = new Color(0.5f, 0.5f, 0.55f), // Silver-grey
                SpecialAbilityName = "Iron Stance",
                SpecialAbilityDescription = "Becomes immovable, reflecting a portion of blocked damage.",
                SpecialAbilityCooldown = 45f,
                BonusDefenseMultiplier = 1.12f,
                BonusStaminaRegen = 1.3f
            });
            
            // ========================================
            // PALADIN HYBRIDS (Paladin + X)
            // ========================================
            
            RegisterHybrid(ArchetypeClass.Paladin, ArchetypeClass.Tank, new HybridDefinition
            {
                DisplayName = "Templar",
                Description = "A holy knight focused on protecting the faithful.",
                IconColor = new Color(0.9f, 0.85f, 0.5f), // Bright gold
                SpecialAbilityName = "Sacred Shield",
                SpecialAbilityDescription = "Grants a divine shield to all nearby allies.",
                SpecialAbilityCooldown = 70f,
                BonusDefenseMultiplier = 1.12f,
                BonusBlockEfficiency = 0.15f
            });
            
            RegisterHybrid(ArchetypeClass.Paladin, ArchetypeClass.Berserker, new HybridDefinition
            {
                DisplayName = "Zealot",
                Description = "A fanatic warrior empowered by righteous fury.",
                IconColor = new Color(1f, 0.5f, 0.2f), // Burning gold
                SpecialAbilityName = "Holy Fury",
                SpecialAbilityDescription = "Enter a divine rage, dealing spirit damage with each hit.",
                SpecialAbilityCooldown = 50f,
                BonusDamageMultiplier = 1.2f,
                BonusHealthRegen = 1f
            });
            
            RegisterHybrid(ArchetypeClass.Paladin, ArchetypeClass.Rogue, new HybridDefinition
            {
                DisplayName = "Inquisitor",
                Description = "A holy hunter who strikes down evil from the shadows.",
                IconColor = new Color(0.6f, 0.5f, 0.3f), // Dark gold
                SpecialAbilityName = "Divine Judgment",
                SpecialAbilityDescription = "Mark an enemy; your next attack deals massive spirit damage.",
                SpecialAbilityCooldown = 35f,
                BonusDamageMultiplier = 1.1f,
                BonusCritChance = 0.15f
            });
            
            RegisterHybrid(ArchetypeClass.Paladin, ArchetypeClass.Ranger, new HybridDefinition
            {
                DisplayName = "Holy Archer",
                Description = "A blessed marksman whose arrows carry divine light.",
                IconColor = new Color(0.9f, 0.9f, 0.6f), // Light gold
                SpecialAbilityName = "Radiant Arrow",
                SpecialAbilityDescription = "Fire a holy arrow that damages enemies and heals allies it passes.",
                SpecialAbilityCooldown = 25f,
                BonusDamageMultiplier = 1.08f,
                BonusHealthRegen = 1.5f
            });
            
            RegisterHybrid(ArchetypeClass.Paladin, ArchetypeClass.Mage, new HybridDefinition
            {
                DisplayName = "Battle Priest",
                Description = "A warrior-mage wielding both steel and holy magic.",
                IconColor = new Color(0.7f, 0.6f, 0.9f), // Purple-gold
                SpecialAbilityName = "Divine Nova",
                SpecialAbilityDescription = "Release a burst of holy energy that damages enemies and buffs allies.",
                SpecialAbilityCooldown = 55f,
                BonusDamageMultiplier = 1.1f,
                BonusEitrRegen = 1.5f
            });
            
            RegisterHybrid(ArchetypeClass.Paladin, ArchetypeClass.Healer, new HybridDefinition
            {
                DisplayName = "High Priest",
                Description = "A master healer with powerful protective abilities.",
                IconColor = new Color(1f, 1f, 0.8f), // Bright white-gold
                SpecialAbilityName = "Mass Restoration",
                SpecialAbilityDescription = "Heal all nearby allies and grant temporary damage immunity.",
                SpecialAbilityCooldown = 90f,
                BonusHealthRegen = 5f,
                BonusDefenseMultiplier = 1.05f
            });
            
            RegisterHybrid(ArchetypeClass.Paladin, ArchetypeClass.Monk, new HybridDefinition
            {
                DisplayName = "Ascetic Knight",
                Description = "A disciplined holy warrior who has mastered body and spirit.",
                IconColor = new Color(0.8f, 0.8f, 0.7f), // Pale gold
                SpecialAbilityName = "Enlightened Strike",
                SpecialAbilityDescription = "A powerful attack that restores stamina and eitr on hit.",
                SpecialAbilityCooldown = 30f,
                BonusDamageMultiplier = 1.05f,
                BonusStaminaRegen = 1.5f,
                BonusEitrRegen = 1f
            });
            
            // ========================================
            // BERSERKER HYBRIDS (Berserker + X)
            // ========================================
            
            RegisterHybrid(ArchetypeClass.Berserker, ArchetypeClass.Tank, new HybridDefinition
            {
                DisplayName = "Ravager",
                Description = "A rampaging warrior who becomes harder to kill as they fight.",
                IconColor = new Color(0.7f, 0.2f, 0.1f), // Blood red
                SpecialAbilityName = "Bloodthirst",
                SpecialAbilityDescription = "Attacks heal for a portion of damage dealt while active.",
                SpecialAbilityCooldown = 40f,
                BonusDamageMultiplier = 1.15f,
                BonusDefenseMultiplier = 1.08f
            });
            
            RegisterHybrid(ArchetypeClass.Berserker, ArchetypeClass.Paladin, new HybridDefinition
            {
                DisplayName = "Avenger",
                Description = "A holy warrior fueled by righteous anger.",
                IconColor = new Color(0.9f, 0.4f, 0.2f), // Orange-red
                SpecialAbilityName = "Wrathful Smite",
                SpecialAbilityDescription = "Deal massive damage increased by how much health you're missing.",
                SpecialAbilityCooldown = 35f,
                BonusDamageMultiplier = 1.2f,
                BonusHealthRegen = 2f
            });
            
            RegisterHybrid(ArchetypeClass.Berserker, ArchetypeClass.Rogue, new HybridDefinition
            {
                DisplayName = "Reaper",
                Description = "A frenzied killer who strikes vital points in their rage.",
                IconColor = new Color(0.5f, 0.1f, 0.2f), // Dark crimson
                SpecialAbilityName = "Frenzy Strike",
                SpecialAbilityDescription = "Attack speed increases with each hit, critical hits restore stamina.",
                SpecialAbilityCooldown = 30f,
                BonusDamageMultiplier = 1.15f,
                BonusCritChance = 0.2f,
                BonusSpeedMultiplier = 1.1f
            });
            
            RegisterHybrid(ArchetypeClass.Berserker, ArchetypeClass.Ranger, new HybridDefinition
            {
                DisplayName = "Beast Hunter",
                Description = "A savage hunter who excels at taking down large prey.",
                IconColor = new Color(0.6f, 0.3f, 0.2f), // Brown-red
                SpecialAbilityName = "Predator's Mark",
                SpecialAbilityDescription = "Mark a target; deal increased damage and gain speed when attacking it.",
                SpecialAbilityCooldown = 25f,
                BonusDamageMultiplier = 1.18f,
                BonusSpeedMultiplier = 1.08f
            });
            
            RegisterHybrid(ArchetypeClass.Berserker, ArchetypeClass.Mage, new HybridDefinition
            {
                DisplayName = "Blood Mage",
                Description = "A dangerous practitioner who fuels magic with their own life force.",
                IconColor = new Color(0.6f, 0.1f, 0.4f), // Purple-red
                SpecialAbilityName = "Blood Sacrifice",
                SpecialAbilityDescription = "Consume health to unleash a devastating magical attack.",
                SpecialAbilityCooldown = 45f,
                BonusDamageMultiplier = 1.25f,
                BonusEitrRegen = 2f
            });
            
            RegisterHybrid(ArchetypeClass.Berserker, ArchetypeClass.Healer, new HybridDefinition
            {
                DisplayName = "Blood Knight",
                Description = "A warrior who heals through violence.",
                IconColor = new Color(0.8f, 0.3f, 0.4f), // Pink-red
                SpecialAbilityName = "Sanguine Fury",
                SpecialAbilityDescription = "Enter a state where damage dealt heals you and nearby allies.",
                SpecialAbilityCooldown = 60f,
                BonusDamageMultiplier = 1.12f,
                BonusHealthRegen = 4f
            });
            
            RegisterHybrid(ArchetypeClass.Berserker, ArchetypeClass.Monk, new HybridDefinition
            {
                DisplayName = "Battle Rager",
                Description = "A disciplined berserker who channels rage into precise strikes.",
                IconColor = new Color(0.7f, 0.3f, 0.3f), // Muted red
                SpecialAbilityName = "Focused Fury",
                SpecialAbilityDescription = "Enter a controlled rage with faster attacks and no stamina cost.",
                SpecialAbilityCooldown = 40f,
                BonusDamageMultiplier = 1.15f,
                BonusStaminaRegen = 2f
            });
            
            // ========================================
            // ROGUE HYBRIDS (Rogue + X)
            // ========================================
            
            RegisterHybrid(ArchetypeClass.Rogue, ArchetypeClass.Tank, new HybridDefinition
            {
                DisplayName = "Duelist",
                Description = "A nimble fighter who parries and ripostes with deadly precision.",
                IconColor = new Color(0.4f, 0.4f, 0.5f), // Steel grey
                SpecialAbilityName = "Perfect Riposte",
                SpecialAbilityDescription = "After a successful parry, counter with a guaranteed critical hit.",
                SpecialAbilityCooldown = 20f,
                BonusCritChance = 0.15f,
                BonusDefenseMultiplier = 1.1f
            });
            
            RegisterHybrid(ArchetypeClass.Rogue, ArchetypeClass.Paladin, new HybridDefinition
            {
                DisplayName = "Shadow Priest",
                Description = "A holy assassin who punishes the wicked.",
                IconColor = new Color(0.5f, 0.4f, 0.6f), // Purple-grey
                SpecialAbilityName = "Holy Assassination",
                SpecialAbilityDescription = "Teleport to target and strike with spirit damage that heals allies.",
                SpecialAbilityCooldown = 35f,
                BonusDamageMultiplier = 1.15f,
                BonusCritChance = 0.1f,
                BonusHealthRegen = 1f
            });
            
            RegisterHybrid(ArchetypeClass.Rogue, ArchetypeClass.Berserker, new HybridDefinition
            {
                DisplayName = "Blade Dancer",
                Description = "A whirlwind of blades that grows more deadly with each kill.",
                IconColor = new Color(0.6f, 0.2f, 0.3f), // Dark red-grey
                SpecialAbilityName = "Dance of Death",
                SpecialAbilityDescription = "Rapidly attack all nearby enemies, gaining speed with each hit.",
                SpecialAbilityCooldown = 30f,
                BonusDamageMultiplier = 1.2f,
                BonusCritChance = 0.15f,
                BonusSpeedMultiplier = 1.15f
            });
            
            RegisterHybrid(ArchetypeClass.Rogue, ArchetypeClass.Ranger, new HybridDefinition
            {
                DisplayName = "Scout",
                Description = "A versatile operative skilled in both melee and ranged combat.",
                IconColor = new Color(0.4f, 0.5f, 0.4f), // Olive grey
                SpecialAbilityName = "Ambush",
                SpecialAbilityDescription = "Fire a poisoned arrow then dash in for a melee strike.",
                SpecialAbilityCooldown = 25f,
                BonusDamageMultiplier = 1.12f,
                BonusCritChance = 0.12f
            });
            
            RegisterHybrid(ArchetypeClass.Rogue, ArchetypeClass.Mage, new HybridDefinition
            {
                DisplayName = "Spellthief",
                Description = "A cunning infiltrator who steals magical energy from foes.",
                IconColor = new Color(0.4f, 0.3f, 0.6f), // Purple-grey
                SpecialAbilityName = "Mana Drain",
                SpecialAbilityDescription = "Strike an enemy to steal eitr and briefly silence them.",
                SpecialAbilityCooldown = 30f,
                BonusDamageMultiplier = 1.1f,
                BonusEitrRegen = 2f,
                BonusCritChance = 0.1f
            });
            
            RegisterHybrid(ArchetypeClass.Rogue, ArchetypeClass.Healer, new HybridDefinition
            {
                DisplayName = "Medicine Man",
                Description = "A shadowy healer who uses forbidden arts.",
                IconColor = new Color(0.3f, 0.5f, 0.4f), // Dark teal
                SpecialAbilityName = "Life Steal",
                SpecialAbilityDescription = "Attacks drain life from enemies and transfer it to the lowest health ally.",
                SpecialAbilityCooldown = 35f,
                BonusDamageMultiplier = 1.05f,
                BonusHealthRegen = 3f,
                BonusCritChance = 0.1f
            });
            
            RegisterHybrid(ArchetypeClass.Rogue, ArchetypeClass.Monk, new HybridDefinition
            {
                DisplayName = "Shadow Monk",
                Description = "A martial artist who strikes from the shadows.",
                IconColor = new Color(0.3f, 0.3f, 0.4f), // Dark grey-blue
                SpecialAbilityName = "Shadow Step",
                SpecialAbilityDescription = "Teleport behind target and deliver a stunning chi strike.",
                SpecialAbilityCooldown = 20f,
                BonusDamageMultiplier = 1.1f,
                BonusCritChance = 0.15f,
                BonusStaminaRegen = 1.5f
            });
            
            // ========================================
            // RANGER HYBRIDS (Ranger + X)
            // ========================================
            
            RegisterHybrid(ArchetypeClass.Ranger, ArchetypeClass.Tank, new HybridDefinition
            {
                DisplayName = "Sentinel",
                Description = "A stalwart defender who can engage threats at any distance.",
                IconColor = new Color(0.4f, 0.5f, 0.4f), // Forest green-grey
                SpecialAbilityName = "Defensive Volley",
                SpecialAbilityDescription = "Fire arrows that create a barrier slowing enemies who pass through.",
                SpecialAbilityCooldown = 45f,
                BonusDefenseMultiplier = 1.1f,
                BonusDamageMultiplier = 1.05f
            });
            
            RegisterHybrid(ArchetypeClass.Ranger, ArchetypeClass.Paladin, new HybridDefinition
            {
                DisplayName = "Divine Hunter",
                Description = "A blessed archer whose arrows carry holy light.",
                IconColor = new Color(0.7f, 0.8f, 0.5f), // Yellow-green
                SpecialAbilityName = "Blessed Arrows",
                SpecialAbilityDescription = "Arrows deal spirit damage and mark enemies, increasing ally damage.",
                SpecialAbilityCooldown = 35f,
                BonusDamageMultiplier = 1.12f,
                BonusHealthRegen = 1.5f
            });
            
            RegisterHybrid(ArchetypeClass.Ranger, ArchetypeClass.Berserker, new HybridDefinition
            {
                DisplayName = "Wild Hunter",
                Description = "A savage archer who unleashes devastating barrages.",
                IconColor = new Color(0.6f, 0.4f, 0.2f), // Brown-orange
                SpecialAbilityName = "Rain of Fury",
                SpecialAbilityDescription = "Fire a rapid volley of arrows with increasing damage.",
                SpecialAbilityCooldown = 30f,
                BonusDamageMultiplier = 1.2f,
                BonusSpeedMultiplier = 1.1f
            });
            
            RegisterHybrid(ArchetypeClass.Ranger, ArchetypeClass.Rogue, new HybridDefinition
            {
                DisplayName = "Sniper",
                Description = "A precision marksman who strikes from concealment.",
                IconColor = new Color(0.3f, 0.4f, 0.3f), // Dark forest
                SpecialAbilityName = "Assassin's Shot",
                SpecialAbilityDescription = "A powerful shot from stealth that deals massive critical damage.",
                SpecialAbilityCooldown = 25f,
                BonusDamageMultiplier = 1.15f,
                BonusCritChance = 0.25f
            });
            
            RegisterHybrid(ArchetypeClass.Ranger, ArchetypeClass.Mage, new HybridDefinition
            {
                DisplayName = "Arcane Archer",
                Description = "A marksman who infuses arrows with elemental magic.",
                IconColor = new Color(0.4f, 0.5f, 0.7f), // Blue-green
                SpecialAbilityName = "Elemental Arrow",
                SpecialAbilityDescription = "Fire an arrow that explodes with elemental damage on impact.",
                SpecialAbilityCooldown = 20f,
                BonusDamageMultiplier = 1.15f,
                BonusEitrRegen = 1f
            });
            
            RegisterHybrid(ArchetypeClass.Ranger, ArchetypeClass.Healer, new HybridDefinition
            {
                DisplayName = "Forest Keeper",
                Description = "A nature guardian who protects allies while striking foes.",
                IconColor = new Color(0.4f, 0.7f, 0.4f), // Bright green
                SpecialAbilityName = "Nature's Blessing",
                SpecialAbilityDescription = "Arrows plant seeds that heal allies and damage enemies over time.",
                SpecialAbilityCooldown = 40f,
                BonusDamageMultiplier = 1.05f,
                BonusHealthRegen = 3f
            });
            
            RegisterHybrid(ArchetypeClass.Ranger, ArchetypeClass.Monk, new HybridDefinition
            {
                DisplayName = "Zen Archer",
                Description = "A meditative marksman with perfect aim and endless stamina.",
                IconColor = new Color(0.5f, 0.6f, 0.5f), // Sage green
                SpecialAbilityName = "Perfect Shot",
                SpecialAbilityDescription = "Enter a focused state; your next arrow cannot miss and deals bonus damage.",
                SpecialAbilityCooldown = 25f,
                BonusDamageMultiplier = 1.1f,
                BonusCritChance = 0.15f,
                BonusStaminaRegen = 1.5f
            });
            
            // ========================================
            // MAGE HYBRIDS (Mage + X)
            // ========================================
            
            RegisterHybrid(ArchetypeClass.Mage, ArchetypeClass.Tank, new HybridDefinition
            {
                DisplayName = "Battlemage",
                Description = "A heavily armored spellcaster who wades into melee.",
                IconColor = new Color(0.5f, 0.4f, 0.7f), // Steel purple
                SpecialAbilityName = "Arcane Armor",
                SpecialAbilityDescription = "Magical shields absorb damage and explode when broken.",
                SpecialAbilityCooldown = 50f,
                BonusDefenseMultiplier = 1.15f,
                BonusEitrRegen = 1.5f
            });
            
            RegisterHybrid(ArchetypeClass.Mage, ArchetypeClass.Paladin, new HybridDefinition
            {
                DisplayName = "Hierophant",
                Description = "A divine spellcaster channeling the power of the gods.",
                IconColor = new Color(0.8f, 0.7f, 0.9f), // Light purple-gold
                SpecialAbilityName = "Divine Wrath",
                SpecialAbilityDescription = "Call down holy fire that damages enemies and blesses allies.",
                SpecialAbilityCooldown = 60f,
                BonusDamageMultiplier = 1.15f,
                BonusEitrRegen = 2f,
                BonusHealthRegen = 2f
            });
            
            RegisterHybrid(ArchetypeClass.Mage, ArchetypeClass.Berserker, new HybridDefinition
            {
                DisplayName = "Pyromancer",
                Description = "A fire mage consumed by the flames they wield.",
                IconColor = new Color(1f, 0.4f, 0.2f), // Bright orange
                SpecialAbilityName = "Inferno",
                SpecialAbilityDescription = "Surround yourself with flames, damaging nearby enemies.",
                SpecialAbilityCooldown = 35f,
                BonusDamageMultiplier = 1.25f,
                BonusEitrRegen = 1f
            });
            
            RegisterHybrid(ArchetypeClass.Mage, ArchetypeClass.Rogue, new HybridDefinition
            {
                DisplayName = "Shadowcaster",
                Description = "A mage who manipulates shadows and illusions.",
                IconColor = new Color(0.3f, 0.2f, 0.5f), // Dark purple
                SpecialAbilityName = "Shadow Clone",
                SpecialAbilityDescription = "Create illusory copies that confuse enemies and cast spells.",
                SpecialAbilityCooldown = 45f,
                BonusDamageMultiplier = 1.1f,
                BonusEitrRegen = 1.5f,
                BonusCritChance = 0.1f
            });
            
            RegisterHybrid(ArchetypeClass.Mage, ArchetypeClass.Ranger, new HybridDefinition
            {
                DisplayName = "Storm Caller",
                Description = "A mage who commands lightning and wind.",
                IconColor = new Color(0.4f, 0.6f, 0.9f), // Storm blue
                SpecialAbilityName = "Chain Lightning",
                SpecialAbilityDescription = "Lightning arcs between enemies, dealing increasing damage.",
                SpecialAbilityCooldown = 30f,
                BonusDamageMultiplier = 1.18f,
                BonusEitrRegen = 1.5f
            });
            
            RegisterHybrid(ArchetypeClass.Mage, ArchetypeClass.Healer, new HybridDefinition
            {
                DisplayName = "Sage",
                Description = "A wise spellcaster balancing destruction and restoration.",
                IconColor = new Color(0.6f, 0.7f, 0.9f), // Light blue-purple
                SpecialAbilityName = "Balance",
                SpecialAbilityDescription = "Damage dealt to enemies heals nearby allies.",
                SpecialAbilityCooldown = 50f,
                BonusDamageMultiplier = 1.08f,
                BonusHealthRegen = 3f,
                BonusEitrRegen = 2f
            });
            
            RegisterHybrid(ArchetypeClass.Mage, ArchetypeClass.Monk, new HybridDefinition
            {
                DisplayName = "Mystic",
                Description = "A mage who has achieved perfect harmony of mind and magic.",
                IconColor = new Color(0.5f, 0.5f, 0.8f), // Mystic purple
                SpecialAbilityName = "Arcane Meditation",
                SpecialAbilityDescription = "Enter a trance that rapidly regenerates eitr and empowers next spell.",
                SpecialAbilityCooldown = 40f,
                BonusDamageMultiplier = 1.1f,
                BonusEitrRegen = 3f,
                BonusStaminaRegen = 1.5f
            });
            
            // ========================================
            // HEALER HYBRIDS (Healer + X)
            // ========================================
            
            RegisterHybrid(ArchetypeClass.Healer, ArchetypeClass.Tank, new HybridDefinition
            {
                DisplayName = "War Cleric",
                Description = "A healer who protects allies with shield and faith.",
                IconColor = new Color(0.6f, 0.7f, 0.8f), // Light blue-steel
                SpecialAbilityName = "Divine Protection",
                SpecialAbilityDescription = "Grant damage immunity to yourself and nearby allies briefly.",
                SpecialAbilityCooldown = 90f,
                BonusDefenseMultiplier = 1.15f,
                BonusHealthRegen = 4f
            });
            
            RegisterHybrid(ArchetypeClass.Healer, ArchetypeClass.Paladin, new HybridDefinition
            {
                DisplayName = "Oracle",
                Description = "A divine healer with prophetic powers.",
                IconColor = new Color(1f, 1f, 0.9f), // Pure white-gold
                SpecialAbilityName = "Divine Foresight",
                SpecialAbilityDescription = "Predict incoming damage and pre-heal allies, granting brief immunity.",
                SpecialAbilityCooldown = 70f,
                BonusHealthRegen = 6f,
                BonusDefenseMultiplier = 1.08f
            });
            
            RegisterHybrid(ArchetypeClass.Healer, ArchetypeClass.Berserker, new HybridDefinition
            {
                DisplayName = "Pain Shaman",
                Description = "A healer who channels pain into restoration.",
                IconColor = new Color(0.8f, 0.4f, 0.5f), // Pink-red
                SpecialAbilityName = "Blood Pact",
                SpecialAbilityDescription = "Take damage to heal allies for double the amount.",
                SpecialAbilityCooldown = 40f,
                BonusHealthRegen = 5f,
                BonusDamageMultiplier = 1.1f
            });
            
            RegisterHybrid(ArchetypeClass.Healer, ArchetypeClass.Rogue, new HybridDefinition
            {
                DisplayName = "Shadow Healer",
                Description = "A healer who works from the shadows, unseen.",
                IconColor = new Color(0.4f, 0.5f, 0.5f), // Dark teal-grey
                SpecialAbilityName = "Phantom Touch",
                SpecialAbilityDescription = "Become invisible while healing, cannot be targeted by enemies.",
                SpecialAbilityCooldown = 50f,
                BonusHealthRegen = 4f,
                BonusSpeedMultiplier = 1.1f
            });
            
            RegisterHybrid(ArchetypeClass.Healer, ArchetypeClass.Ranger, new HybridDefinition
            {
                DisplayName = "Nature Priest",
                Description = "A healer drawing power from nature itself.",
                IconColor = new Color(0.5f, 0.8f, 0.5f), // Natural green
                SpecialAbilityName = "Regrowth",
                SpecialAbilityDescription = "Create a healing zone that grows stronger over time.",
                SpecialAbilityCooldown = 45f,
                BonusHealthRegen = 5f,
                BonusStaminaRegen = 1.3f
            });
            
            RegisterHybrid(ArchetypeClass.Healer, ArchetypeClass.Mage, new HybridDefinition
            {
                DisplayName = "Arcane Healer",
                Description = "A healer who uses arcane magic to restore and protect.",
                IconColor = new Color(0.6f, 0.7f, 1f), // Light blue-purple
                SpecialAbilityName = "Mana Transfusion",
                SpecialAbilityDescription = "Convert eitr into powerful heals and magical shields.",
                SpecialAbilityCooldown = 35f,
                BonusHealthRegen = 4f,
                BonusEitrRegen = 3f
            });
            
            RegisterHybrid(ArchetypeClass.Healer, ArchetypeClass.Monk, new HybridDefinition
            {
                DisplayName = "Chi Master",
                Description = "A healer who channels life energy through meditation.",
                IconColor = new Color(0.6f, 0.9f, 0.7f), // Light green-cyan
                SpecialAbilityName = "Chi Restoration",
                SpecialAbilityDescription = "Channel chi to heal all allies and restore their stamina.",
                SpecialAbilityCooldown = 55f,
                BonusHealthRegen = 5f,
                BonusStaminaRegen = 2f
            });
            
            // ========================================
            // MONK HYBRIDS (Monk + X)
            // ========================================
            
            RegisterHybrid(ArchetypeClass.Monk, ArchetypeClass.Tank, new HybridDefinition
            {
                DisplayName = "Stone Fist",
                Description = "A martial artist with unbreakable defense.",
                IconColor = new Color(0.5f, 0.5f, 0.5f), // Stone grey
                SpecialAbilityName = "Stone Stance",
                SpecialAbilityDescription = "Become immovable, blocking all damage and countering attacks.",
                SpecialAbilityCooldown = 40f,
                BonusDefenseMultiplier = 1.2f,
                BonusStaminaRegen = 1.5f
            });
            
            RegisterHybrid(ArchetypeClass.Monk, ArchetypeClass.Paladin, new HybridDefinition
            {
                DisplayName = "Temple Guardian",
                Description = "A holy martial artist protecting sacred places.",
                IconColor = new Color(0.8f, 0.8f, 0.6f), // Temple gold
                SpecialAbilityName = "Sacred Fist",
                SpecialAbilityDescription = "Attacks deal spirit damage and heal nearby allies.",
                SpecialAbilityCooldown = 35f,
                BonusDamageMultiplier = 1.1f,
                BonusHealthRegen = 2f,
                BonusStaminaRegen = 1.3f
            });
            
            RegisterHybrid(ArchetypeClass.Monk, ArchetypeClass.Berserker, new HybridDefinition
            {
                DisplayName = "Drunken Master",
                Description = "An unpredictable fighter whose movements confuse foes.",
                IconColor = new Color(0.7f, 0.5f, 0.4f), // Earthy brown
                SpecialAbilityName = "Drunken Frenzy",
                SpecialAbilityDescription = "Attack wildly with bonus damage and dodge chance.",
                SpecialAbilityCooldown = 30f,
                BonusDamageMultiplier = 1.18f,
                BonusSpeedMultiplier = 1.15f,
                BonusStaminaRegen = 1.5f
            });
            
            RegisterHybrid(ArchetypeClass.Monk, ArchetypeClass.Rogue, new HybridDefinition
            {
                DisplayName = "Ninja",
                Description = "A silent warrior combining martial arts with stealth.",
                IconColor = new Color(0.2f, 0.2f, 0.3f), // Night black
                SpecialAbilityName = "Vanishing Strike",
                SpecialAbilityDescription = "Disappear and reappear behind target with a stunning blow.",
                SpecialAbilityCooldown = 25f,
                BonusDamageMultiplier = 1.12f,
                BonusCritChance = 0.2f,
                BonusSpeedMultiplier = 1.1f
            });
            
            RegisterHybrid(ArchetypeClass.Monk, ArchetypeClass.Ranger, new HybridDefinition
            {
                DisplayName = "Wind Walker",
                Description = "A swift martial artist who moves like the wind.",
                IconColor = new Color(0.7f, 0.8f, 0.9f), // Sky blue
                SpecialAbilityName = "Wind Step",
                SpecialAbilityDescription = "Gain massive movement speed and attack speed briefly.",
                SpecialAbilityCooldown = 30f,
                BonusSpeedMultiplier = 1.2f,
                BonusStaminaRegen = 2f
            });
            
            RegisterHybrid(ArchetypeClass.Monk, ArchetypeClass.Mage, new HybridDefinition
            {
                DisplayName = "Elementalist",
                Description = "A martial artist who channels elemental chi.",
                IconColor = new Color(0.5f, 0.6f, 0.8f), // Elemental blue
                SpecialAbilityName = "Elemental Fist",
                SpecialAbilityDescription = "Attacks cycle through fire, frost, and lightning damage.",
                SpecialAbilityCooldown = 25f,
                BonusDamageMultiplier = 1.15f,
                BonusEitrRegen = 1.5f
            });
            
            RegisterHybrid(ArchetypeClass.Monk, ArchetypeClass.Healer, new HybridDefinition
            {
                DisplayName = "Chi Healer",
                Description = "A martial artist who heals through touch.",
                IconColor = new Color(0.6f, 0.9f, 0.8f), // Healing cyan
                SpecialAbilityName = "Healing Palm",
                SpecialAbilityDescription = "Touch an ally to instantly restore health and cure ailments.",
                SpecialAbilityCooldown = 35f,
                BonusHealthRegen = 4f,
                BonusStaminaRegen = 1.5f
            });
        }
        
        private static void RegisterHybrid(ArchetypeClass main, ArchetypeClass sub, HybridDefinition definition)
        {
            _hybrids[(main, sub)] = definition;
        }
        
        #endregion
    }
}
