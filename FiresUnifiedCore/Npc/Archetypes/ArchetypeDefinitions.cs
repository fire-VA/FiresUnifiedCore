using UnityEngine;
using System;
using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Expanded archetype definitions with RPG-style roles.
    /// Each archetype has unique stat bonuses, weapon preferences, and combat behaviors.
    /// 
    /// ARCHETYPE HIERARCHY:
    /// - Guardian: Shield + melee, high defense, uses taunt, protects allies
    /// - Berserker: Two-handed or dual wield, high offense, aggressive combos
    /// - Rogue: Daggers/knives, high mobility, backstab bonuses, evasion
    /// - Ranger: Bows/crossbows, kiting, trap awareness, long-range support
    /// - Mage: Staves, eitr-based, elemental damage, AoE attacks
    /// - Cleric: Support staves, buff/heal allies, stays at range
    /// - Paladin: Shield + mace/sword, balanced offense/defense, can heal
    /// - Monk: Unarmed/clubs, high mobility, chi abilities, meditation
    /// </summary>
    public enum ArchetypeClass
    {
        None = 0,
        
        // === DEFENSIVE ===
        /// <summary>
        /// Guardian - Shield-bearer focused on protecting allies.
        /// +Stamina, +Block efficiency, uses Taunt.
        /// Requires: Shield + one-handed melee weapon
        /// </summary>
        Tank = 1,
        
        /// <summary>
        /// Balanced fighter with some healing capability.
        /// +Block, +Holy damage, can use healing items on allies.
        /// Requires: Shield + mace or sword
        /// </summary>
        Paladin = 2,
        
        // === OFFENSIVE MELEE ===
        /// <summary>
        /// Aggressive two-handed fighter, high damage output.
        /// +Attack speed when health low, +Damage, reduced defense.
        /// Requires: Two-handed weapon OR dual wield
        /// </summary>
        Berserker = 3,
        
        /// <summary>
        /// Stealthy fighter with critical hit bonuses.
        /// +Dodge, +Backstab damage, +Movement speed.
        /// Requires: Knives, daggers, or light weapons
        /// </summary>
        Rogue = 4,
        
        /// <summary>
        /// Martial artist with chi-based abilities.
        /// +Attack speed, +Dodge, +Stamina regen.
        /// Requires: Unarmed or clubs/fists
        /// </summary>
        Monk = 5,
        
        // === RANGED ===
        /// <summary>
        /// Long-range physical damage dealer.
        /// +Draw speed, +Accuracy, +Kiting ability.
        /// Requires: Bow or crossbow
        /// </summary>
        Ranger = 6,
        
        // === MAGIC ===
        /// <summary>
        /// Offensive magic user, elemental damage.
        /// +Eitr regen, +Elemental damage, +AoE range.
        /// Requires: Offensive staff (fire, ice, lightning)
        /// </summary>
        Mage = 7,
        
        /// <summary>
        /// Cleric - Support magic user focused on buffing allies.
        /// +Buff duration, +Heal effectiveness, +Eitr pool.
        /// Requires: Support staff (shield staff, heal staff)
        /// </summary>
        Healer = 8
    }
    
    /// <summary>
    /// Base configuration for all archetypes.
    /// Override in specific archetype classes for customization.
    /// </summary>
    [Serializable]
    public class ArchetypeDefinition
    {
        public ArchetypeClass Class;
        public string DisplayName;
        public string Description;
        public Color IconColor;
        
        // === STAT BONUSES (multipliers, 1.0 = no change) ===
        public float MaxStaminaMultiplier = 1.0f;
        public float StaminaRegenMultiplier = 1.0f;
        public float BlockStaminaCostMultiplier = 1.0f;
        public float DodgeStaminaCostMultiplier = 1.0f;
        public float AttackStaminaCostMultiplier = 1.0f;
        
        public float MaxHealthMultiplier = 1.0f;
        public float HealthRegenMultiplier = 1.0f;
        public float ArmorMultiplier = 1.0f;
        
        public float MaxEitrMultiplier = 1.0f;
        public float EitrRegenMultiplier = 1.0f;
        public float EitrCostMultiplier = 1.0f;
        
        public float AttackDamageMultiplier = 1.0f;
        public float AttackSpeedMultiplier = 1.0f;
        public float CriticalChanceBonus = 0f;
        public float CriticalDamageMultiplier = 1.0f;
        
        public float MovementSpeedMultiplier = 1.0f;
        public float DodgeDistanceMultiplier = 1.0f;
        
        // === SPECIAL ABILITIES ===
        public bool CanTaunt = false;
        public bool CanHealAllies = false;
        public bool CanBuffAllies = false;
        public bool HasBackstabBonus = false;
        public bool HasBerserkMode = false;
        public bool HasChiAbilities = false;
        public bool HasMarkTarget = false;
        public bool HasElementalMastery = false;
        public bool HasDivineProtection = false;
        
        // === LEVEL SCALING ===
        /// <summary>How much bonuses increase per companion level (0.01 = 1% per level)</summary>
        public float LevelScalingRate = 0.02f;
        
        // === WEAPON REQUIREMENTS ===
        public List<Skills.SkillType> PreferredSkills = new List<Skills.SkillType>();
        public List<ItemDrop.ItemData.ItemType> RequiredItemTypes = new List<ItemDrop.ItemData.ItemType>();
        public bool RequiresShield = false;
        public bool RequiresTwoHanded = false;
        public bool RequiresDualWield = false;
        
        /// <summary>
        /// Gets the scaled bonus value based on companion level.
        /// </summary>
        public float GetScaledBonus(float baseBonus, int level)
        {
            // Base + (level * scaling rate * base)
            return baseBonus + (level * LevelScalingRate * (baseBonus - 1.0f));
        }
    }
    
    /// <summary>
    /// Static registry of all archetype definitions.
    /// </summary>
    public static class ArchetypeRegistry
    {
        private static Dictionary<ArchetypeClass, ArchetypeDefinition> _definitions;
        
        public static void Initialize()
        {
            _definitions = new Dictionary<ArchetypeClass, ArchetypeDefinition>
            {
                { ArchetypeClass.Tank, CreateTankDefinition() },
                { ArchetypeClass.Paladin, CreatePaladinDefinition() },
                { ArchetypeClass.Berserker, CreateBerserkerDefinition() },
                { ArchetypeClass.Rogue, CreateRogueDefinition() },
                { ArchetypeClass.Monk, CreateMonkDefinition() },
                { ArchetypeClass.Ranger, CreateRangerDefinition() },
                { ArchetypeClass.Mage, CreateMageDefinition() },
                { ArchetypeClass.Healer, CreateHealerDefinition() }
            };
        }
        
        public static ArchetypeDefinition GetDefinition(ArchetypeClass archetype)
        {
            if (_definitions == null) Initialize();
            return _definitions.TryGetValue(archetype, out var def) ? def : null;
        }
        
        public static IEnumerable<ArchetypeDefinition> GetAllDefinitions()
        {
            if (_definitions == null) Initialize();
            return _definitions.Values;
        }
        
        #region Definition Creators
        
        private static ArchetypeDefinition CreateTankDefinition()
        {
            return new ArchetypeDefinition
            {
                Class = ArchetypeClass.Tank,
                DisplayName = "Guardian",
                Description = "Shield-bearer focused on protecting allies. Uses taunt to draw enemy attention.",
                IconColor = new Color(0.2f, 0.4f, 0.8f), // Blue
                
                // Tank gets stamina bonuses
                MaxStaminaMultiplier = 1.25f,        // +25% max stamina
                StaminaRegenMultiplier = 1.15f,     // +15% stamina regen
                BlockStaminaCostMultiplier = 0.70f, // -30% block stamina cost
                DodgeStaminaCostMultiplier = 1.10f, // +10% dodge cost (tanks don't dodge much)
                
                // Slightly tanky
                MaxHealthMultiplier = 1.10f,
                ArmorMultiplier = 1.15f,
                
                // Reduced offense
                AttackDamageMultiplier = 0.90f,
                AttackSpeedMultiplier = 0.95f,
                
                // Special abilities
                CanTaunt = true,
                
                // Level scaling
                LevelScalingRate = 0.025f, // 2.5% per level
                
                // Requirements
                RequiresShield = true,
                PreferredSkills = new List<Skills.SkillType>
                {
                    Skills.SkillType.Blocking,
                    Skills.SkillType.Swords,
                    Skills.SkillType.Axes,
                    Skills.SkillType.Clubs
                },
                RequiredItemTypes = new List<ItemDrop.ItemData.ItemType>
                {
                    ItemDrop.ItemData.ItemType.Shield
                }
            };
        }
        
        private static ArchetypeDefinition CreatePaladinDefinition()
        {
            return new ArchetypeDefinition
            {
                Class = ArchetypeClass.Paladin,
                DisplayName = "Paladin",
                Description = "Holy warrior with balanced offense and defense. Divine protection for allies.",
                IconColor = new Color(1f, 0.84f, 0f), // Gold
                
                // Balanced stamina
                MaxStaminaMultiplier = 1.10f,
                StaminaRegenMultiplier = 1.10f,
                BlockStaminaCostMultiplier = 0.85f,
                
                // Balanced stats
                MaxHealthMultiplier = 1.15f,
                ArmorMultiplier = 1.10f,
                AttackDamageMultiplier = 1.0f,
                
                // Special abilities
                CanHealAllies = true,
                HasDivineProtection = true, // Group damage reduction
                CanBuffAllies = true,
                
                // Requirements
                RequiresShield = true,
                PreferredSkills = new List<Skills.SkillType>
                {
                    Skills.SkillType.Blocking,
                    Skills.SkillType.Clubs, // Maces
                    Skills.SkillType.Swords
                }
            };
        }
        
        private static ArchetypeDefinition CreateBerserkerDefinition()
        {
            return new ArchetypeDefinition
            {
                Class = ArchetypeClass.Berserker,
                DisplayName = "Berserker",
                Description = "Aggressive fighter who deals more damage as health drops. High risk, high reward.",
                IconColor = new Color(0.8f, 0.2f, 0.2f), // Red
                
                // High stamina consumption, but fast regen
                MaxStaminaMultiplier = 1.0f,
                StaminaRegenMultiplier = 1.25f,
                AttackStaminaCostMultiplier = 0.85f, // Efficient attacks
                BlockStaminaCostMultiplier = 1.20f, // Bad at blocking
                
                // Glass cannon
                MaxHealthMultiplier = 0.90f,
                ArmorMultiplier = 0.85f,
                
                // High offense
                AttackDamageMultiplier = 1.25f,
                AttackSpeedMultiplier = 1.15f,
                CriticalChanceBonus = 0.05f, // +5% crit
                CriticalDamageMultiplier = 1.25f,
                
                // Special abilities
                HasBerserkMode = true, // More damage at low health
                
                // Requirements - two-handed or no shield
                RequiresTwoHanded = true,
                PreferredSkills = new List<Skills.SkillType>
                {
                    Skills.SkillType.Axes,
                    Skills.SkillType.Clubs,
                    Skills.SkillType.Polearms,
                    Skills.SkillType.Swords
                }
            };
        }
        
        private static ArchetypeDefinition CreateRogueDefinition()
        {
            return new ArchetypeDefinition
            {
                Class = ArchetypeClass.Rogue,
                DisplayName = "Rogue",
                Description = "Stealthy fighter with high mobility and backstab bonuses.",
                IconColor = new Color(0.4f, 0.2f, 0.6f), // Purple
                
                // High mobility, low sustain
                MaxStaminaMultiplier = 1.15f,
                StaminaRegenMultiplier = 1.20f,
                DodgeStaminaCostMultiplier = 0.70f, // Great at dodging
                BlockStaminaCostMultiplier = 1.30f, // Bad at blocking
                
                // Squishy
                MaxHealthMultiplier = 0.85f,
                ArmorMultiplier = 0.80f,
                
                // High burst damage
                AttackDamageMultiplier = 1.10f,
                AttackSpeedMultiplier = 1.20f,
                CriticalChanceBonus = 0.10f, // +10% crit
                CriticalDamageMultiplier = 1.50f, // Big crits
                
                // High mobility
                MovementSpeedMultiplier = 1.10f,
                DodgeDistanceMultiplier = 1.20f,
                
                // Special abilities
                HasBackstabBonus = true,
                
                // Requirements
                PreferredSkills = new List<Skills.SkillType>
                {
                    Skills.SkillType.Knives,
                    Skills.SkillType.Swords // Light swords
                }
            };
        }
        
        private static ArchetypeDefinition CreateMonkDefinition()
        {
            return new ArchetypeDefinition
            {
                Class = ArchetypeClass.Monk,
                DisplayName = "Monk",
                Description = "Martial artist who channels chi for devastating attacks and healing meditation.",
                IconColor = new Color(0.2f, 0.8f, 0.5f), // Teal/Green
                
                // Excellent stamina management
                MaxStaminaMultiplier = 1.20f,
                StaminaRegenMultiplier = 1.30f,
                DodgeStaminaCostMultiplier = 0.75f, // Good at dodging
                AttackStaminaCostMultiplier = 0.80f, // Efficient attacks
                BlockStaminaCostMultiplier = 1.10f, // Doesn't use shields
                
                // Moderate health, relies on evasion
                MaxHealthMultiplier = 1.0f,
                ArmorMultiplier = 0.85f,
                HealthRegenMultiplier = 1.15f,
                
                // Fast attacks, moderate damage
                AttackDamageMultiplier = 1.15f,
                AttackSpeedMultiplier = 1.25f, // Very fast attacks
                CriticalChanceBonus = 0.08f,
                CriticalDamageMultiplier = 1.30f,
                
                // High mobility
                MovementSpeedMultiplier = 1.10f,
                DodgeDistanceMultiplier = 1.15f,
                
                // Special abilities
                HasChiAbilities = true,
                CanBuffAllies = true, // Inner Peace aura
                
                // Level scaling
                LevelScalingRate = 0.025f,
                
                // Requirements - unarmed or clubs (fist weapons)
                PreferredSkills = new List<Skills.SkillType>
                {
                    Skills.SkillType.Unarmed,
                    Skills.SkillType.Clubs // Includes fist weapons
                }
            };
        }
        
        private static ArchetypeDefinition CreateRangerDefinition()
        {
            return new ArchetypeDefinition
            {
                Class = ArchetypeClass.Ranger,
                DisplayName = "Ranger",
                Description = "Expert archer who excels at long-range combat and marking targets.",
                IconColor = new Color(0.2f, 0.6f, 0.2f), // Green
                
                // Good stamina for kiting
                MaxStaminaMultiplier = 1.15f,
                StaminaRegenMultiplier = 1.15f,
                DodgeStaminaCostMultiplier = 0.85f,
                
                // Light armor, decent health
                MaxHealthMultiplier = 0.95f,
                ArmorMultiplier = 0.90f,
                
                // Ranged bonuses
                AttackDamageMultiplier = 1.15f, // For ranged
                AttackSpeedMultiplier = 1.10f, // Faster draw
                CriticalChanceBonus = 0.08f,
                
                // Mobile
                MovementSpeedMultiplier = 1.05f,
                
                // Special abilities
                HasMarkTarget = true, // Mark target for bonus damage
                
                // Requirements
                PreferredSkills = new List<Skills.SkillType>
                {
                    Skills.SkillType.Bows,
                    Skills.SkillType.Crossbows
                },
                RequiredItemTypes = new List<ItemDrop.ItemData.ItemType>
                {
                    ItemDrop.ItemData.ItemType.Bow
                }
            };
        }
        
        private static ArchetypeDefinition CreateMageDefinition()
        {
            return new ArchetypeDefinition
            {
                Class = ArchetypeClass.Mage,
                DisplayName = "Mage",
                Description = "Elemental magic user who deals devastating AoE damage.",
                IconColor = new Color(0.2f, 0.8f, 0.9f), // Cyan
                
                // Low physical stamina, high eitr
                MaxStaminaMultiplier = 0.90f,
                StaminaRegenMultiplier = 0.95f,
                MaxEitrMultiplier = 1.30f,
                EitrRegenMultiplier = 1.25f,
                EitrCostMultiplier = 0.85f, // Efficient casting
                
                // Fragile
                MaxHealthMultiplier = 0.80f,
                ArmorMultiplier = 0.75f,
                
                // High magic damage
                AttackDamageMultiplier = 1.30f, // For magic
                CriticalChanceBonus = 0.05f,
                
                // Special abilities
                HasElementalMastery = true, // Elemental damage bonuses
                
                // Requirements
                PreferredSkills = new List<Skills.SkillType>
                {
                    Skills.SkillType.ElementalMagic,
                    Skills.SkillType.BloodMagic
                }
            };
        }
        
        private static ArchetypeDefinition CreateHealerDefinition()
        {
            return new ArchetypeDefinition
            {
                Class = ArchetypeClass.Healer,
                DisplayName = "Cleric",
                Description = "Support magic user who buffs and heals allies.",
                IconColor = new Color(0.9f, 0.9f, 0.5f), // Light yellow
                
                // Balanced stamina, high eitr
                MaxStaminaMultiplier = 1.0f,
                StaminaRegenMultiplier = 1.10f,
                MaxEitrMultiplier = 1.25f,
                EitrRegenMultiplier = 1.30f,
                EitrCostMultiplier = 0.80f, // Very efficient
                
                // Moderately fragile
                MaxHealthMultiplier = 0.90f,
                ArmorMultiplier = 0.85f,
                
                // Low offense
                AttackDamageMultiplier = 0.85f,
                
                // Special abilities
                CanBuffAllies = true,
                CanHealAllies = true,
                
                // Requirements
                PreferredSkills = new List<Skills.SkillType>
                {
                    Skills.SkillType.ElementalMagic // Support staves use this
                }
            };
        }
        
        #endregion
    }
}
