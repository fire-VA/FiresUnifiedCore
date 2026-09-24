using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc.Archetypes;

namespace FiresCore.Classes
{
    /// <summary>
    /// The class roster's ability data, shared by companions and players. Lifted out of the
    /// companion-internal AbilityUnlockSystem so both sides read one table.
    /// Plan: Docs/PLAN_ClassesFoundation.md
    /// </summary>
    public static class ClassAbilityRegistry
    {
        /// <summary>Level a companion or player may take a second archetype.</summary>
        public const int HybridUnlockLevel = 25;

        /// <summary>Level the hybrid pair's own ability becomes usable.</summary>
        public const int HybridAbilityLevel = 30;

        /// <summary>Level the emergency evasion ability becomes usable.</summary>
        public const int EvasionUnlockLevel = 20;

        private static Dictionary<ArchetypeClass, List<ClassAbility>> s_abilitiesByArchetype;

        static ClassAbilityRegistry()
        {
            BuildAbilityTable();
        }

        private static void BuildAbilityTable()
        {
            s_abilitiesByArchetype = new Dictionary<ArchetypeClass, List<ClassAbility>>();
            
            // ===== TANK ABILITIES =====
            s_abilitiesByArchetype[ArchetypeClass.Tank] = new List<ClassAbility>
            {
                // Starter
                new ClassAbility("tank_block", "Shield Block", 
                    "Hold your shield to block incoming attacks, reducing damage taken.", 
                    1, ArchetypeClass.Tank, AbilityCategory.Starter, "#4488FF"),
                
                // Basic
                new ClassAbility("tank_taunt", "Taunt", 
                    "Unleash a battle cry that forces nearby enemies to attack you for 30 seconds. Creates a shockwave that affects all enemies within 5m.", 
                    5, ArchetypeClass.Tank, AbilityCategory.Basic, "#FF4444"),
                
                new ClassAbility("tank_fortify", "Fortify", 
                    "Brace yourself, reducing all damage taken by 40% for 10 seconds. Automatically triggers when health drops below 40%.", 
                    10, ArchetypeClass.Tank, AbilityCategory.Basic, "#66AAFF"),
                
                // Advanced
                new ClassAbility("tank_parry_restore", "Parry Mastery", 
                    "Perfect parries restore stamina. Higher levels restore more stamina per parry.", 
                    15, ArchetypeClass.Tank, AbilityCategory.Advanced, "#FFCC00", true),
                
                new ClassAbility("tank_intercept", "Intercept", 
                    "Automatically move to protect allies when enemies get too close to them.", 
                    20, ArchetypeClass.Tank, AbilityCategory.Advanced, "#88FF88", true),
                
                // Expert
                new ClassAbility("tank_shield_charge", "Shield Charge", 
                    "Emergency evasion: Charge through enemies, knocking them back and creating distance.", 
                    EvasionUnlockLevel, ArchetypeClass.Tank, AbilityCategory.Expert, "#FF8800"),
                
                new ClassAbility("tank_iron_wall", "Iron Wall", 
                    "While blocking, you take 60% reduced damage instead of 40%. Stacks with Fortify.", 
                    40, ArchetypeClass.Tank, AbilityCategory.Expert, "#AAAAFF", true),
                
                // Master
                new ClassAbility("tank_unyielding", "Unyielding", 
                    "When you would take fatal damage, instead survive with 1 HP. 5 minute cooldown.", 
                    75, ArchetypeClass.Tank, AbilityCategory.Master, "#FFD700"),
                
                // Ultimate
                new ClassAbility("tank_immortal_stance", "Immortal Stance", 
                    "Become completely immune to damage for 5 seconds. All enemies within 10m are taunted.", 
                    100, ArchetypeClass.Tank, AbilityCategory.Ultimate, "#FF00FF"),
            };
            
            // ===== PALADIN ABILITIES =====
            s_abilitiesByArchetype[ArchetypeClass.Paladin] = new List<ClassAbility>
            {
                new ClassAbility("paladin_block", "Blessed Shield", 
                    "Block attacks with your shield. Blocked attacks have a chance to heal you slightly.", 
                    1, ArchetypeClass.Paladin, AbilityCategory.Starter, "#FFD700"),
                
                new ClassAbility("paladin_smite", "Holy Smite", 
                    "Imbue your weapon with holy energy, dealing bonus spirit damage on your next attacks for 15 seconds.", 
                    5, ArchetypeClass.Paladin, AbilityCategory.Basic, "#FFFF66"),
                
                new ClassAbility("paladin_protection", "Divine Protection", 
                    "Create an aura that reduces damage taken by all nearby allies by 20% for 12 seconds.", 
                    10, ArchetypeClass.Paladin, AbilityCategory.Basic, "#66FF66"),
                
                new ClassAbility("paladin_heal_block", "Blessed Defense", 
                    "Successful blocks have a 20% chance to heal you for 5% of max health.", 
                    15, ArchetypeClass.Paladin, AbilityCategory.Advanced, "#88FF88", true),
                
                new ClassAbility("paladin_divine_retreat", "Divine Retreat", 
                    "Emergency evasion: Flash of holy light blinds nearby enemies while you reposition.", 
                    EvasionUnlockLevel, ArchetypeClass.Paladin, AbilityCategory.Advanced, "#FFFFFF"),
                
                new ClassAbility("paladin_consecration", "Consecration", 
                    "Bless the ground beneath you, healing allies and damaging undead enemies who stand in it.", 
                    35, ArchetypeClass.Paladin, AbilityCategory.Expert, "#FFCC00"),
                
                new ClassAbility("paladin_divine_shield", "Divine Shield", 
                    "Become immune to all damage for 3 seconds. Cannot attack while shielded.", 
                    50, ArchetypeClass.Paladin, AbilityCategory.Expert, "#FFD700"),
                
                new ClassAbility("paladin_lay_hands", "Lay on Hands", 
                    "Fully heal yourself or an ally. 5 minute cooldown.", 
                    75, ArchetypeClass.Paladin, AbilityCategory.Master, "#00FF00"),
                
                new ClassAbility("paladin_avatar", "Avatar of Light", 
                    "Transform into a divine warrior. All abilities are enhanced for 30 seconds.", 
                    100, ArchetypeClass.Paladin, AbilityCategory.Ultimate, "#FFFFFF"),
            };
            
            // ===== BERSERKER ABILITIES =====
            s_abilitiesByArchetype[ArchetypeClass.Berserker] = new List<ClassAbility>
            {
                new ClassAbility("berserker_fury", "Fury", 
                    "Your attacks build fury. At max fury, your attacks deal bonus damage.", 
                    1, ArchetypeClass.Berserker, AbilityCategory.Starter, "#FF4444"),
                
                new ClassAbility("berserker_warcry", "Warcry", 
                    "Let out a fearsome shout, increasing attack speed and damage for all nearby allies.", 
                    5, ArchetypeClass.Berserker, AbilityCategory.Basic, "#FF6600"),
                
                new ClassAbility("berserker_rage", "Berserk Rage", 
                    "When below 35% health, enter a rage state. Deal 50% more damage but take 20% more damage.", 
                    10, ArchetypeClass.Berserker, AbilityCategory.Basic, "#FF0000"),
                
                new ClassAbility("berserker_bloodlust", "Bloodlust", 
                    "Killing enemies restores 5% of your max health.", 
                    15, ArchetypeClass.Berserker, AbilityCategory.Advanced, "#AA0000", true),
                
                new ClassAbility("berserker_leap", "Savage Leap", 
                    "Emergency evasion: Leap away from danger (or into enemies when healthy), dealing AoE damage on landing.", 
                    EvasionUnlockLevel, ArchetypeClass.Berserker, AbilityCategory.Advanced, "#FF8800"),
                
                new ClassAbility("berserker_execute", "Execute", 
                    "Deal massive bonus damage to enemies below 20% health.", 
                    35, ArchetypeClass.Berserker, AbilityCategory.Expert, "#880000", true),
                
                new ClassAbility("berserker_rampage", "Rampage", 
                    "Each kill within 10 seconds increases your damage by 10%, stacking up to 5 times.", 
                    50, ArchetypeClass.Berserker, AbilityCategory.Expert, "#FF4400", true),
                
                new ClassAbility("berserker_deathwish", "Death Wish", 
                    "Deal 100% more damage when below 10% health. Cannot be healed during this state.", 
                    75, ArchetypeClass.Berserker, AbilityCategory.Master, "#660000"),
                
                new ClassAbility("berserker_avatar_war", "Avatar of War", 
                    "Become an unstoppable force. Immune to stagger and knockback, 75% increased damage for 20s.", 
                    100, ArchetypeClass.Berserker, AbilityCategory.Ultimate, "#FF0000"),
            };
            
            // ===== ROGUE ABILITIES =====
            s_abilitiesByArchetype[ArchetypeClass.Rogue] = new List<ClassAbility>
            {
                new ClassAbility("rogue_backstab", "Backstab", 
                    "Attacks from behind deal 50% bonus damage.", 
                    1, ArchetypeClass.Rogue, AbilityCategory.Starter, "#8800FF", true),
                
                new ClassAbility("rogue_poison", "Poison Blade", 
                    "Your attacks have a 50% chance to poison enemies, dealing damage over time.", 
                    5, ArchetypeClass.Rogue, AbilityCategory.Basic, "#00FF00"),
                
                new ClassAbility("rogue_stealth", "Stealth", 
                    "Become harder to detect. Your next attack from stealth deals bonus damage.", 
                    10, ArchetypeClass.Rogue, AbilityCategory.Basic, "#666666"),
                
                new ClassAbility("rogue_caltrops", "Caltrops", 
                    "Drop caltrops behind you when dodging, slowing and damaging enemies who walk over them.", 
                    15, ArchetypeClass.Rogue, AbilityCategory.Advanced, "#888888"),
                
                new ClassAbility("rogue_shadow_escape", "Shadow Escape", 
                    "Emergency evasion: Vanish in a puff of smoke, becoming invisible and repositioning.", 
                    EvasionUnlockLevel, ArchetypeClass.Rogue, AbilityCategory.Advanced, "#440044"),
                
                new ClassAbility("rogue_assassinate", "Assassinate", 
                    "Attacks from stealth deal 100% bonus damage instead of 50%.", 
                    35, ArchetypeClass.Rogue, AbilityCategory.Expert, "#FF00FF", true),
                
                new ClassAbility("rogue_evasion", "Evasion", 
                    "50% chance to completely avoid attacks for 8 seconds.", 
                    50, ArchetypeClass.Rogue, AbilityCategory.Expert, "#AAAAAA"),
                
                new ClassAbility("rogue_death_mark", "Death Mark", 
                    "Mark an enemy for death. All attacks against them deal 30% bonus damage.", 
                    75, ArchetypeClass.Rogue, AbilityCategory.Master, "#FF0066"),
                
                new ClassAbility("rogue_shadow_dance", "Shadow Dance", 
                    "Enter a state of perfect evasion. Dodge all attacks and deal double damage for 10 seconds.", 
                    100, ArchetypeClass.Rogue, AbilityCategory.Ultimate, "#440066"),
            };
            
            // ===== RANGER ABILITIES =====
            s_abilitiesByArchetype[ArchetypeClass.Ranger] = new List<ClassAbility>
            {
                new ClassAbility("ranger_aim", "Steady Aim", 
                    "Draw your bow to increase accuracy and damage. Fully drawn shots deal bonus damage.", 
                    1, ArchetypeClass.Ranger, AbilityCategory.Starter, "#00AA00", true),
                
                new ClassAbility("ranger_mark", "Hunter's Mark", 
                    "Mark an enemy target. All attacks against marked enemies deal bonus damage.", 
                    5, ArchetypeClass.Ranger, AbilityCategory.Basic, "#FF8800"),
                
                new ClassAbility("ranger_eagle_eye", "Eagle Eye", 
                    "Greatly increase your accuracy and critical hit chance for 15 seconds.", 
                    10, ArchetypeClass.Ranger, AbilityCategory.Basic, "#FFFF00"),
                
                new ClassAbility("ranger_kiting", "Kiting Expertise", 
                    "Move faster while a ranged weapon is drawn. Automatically maintain distance from enemies.", 
                    15, ArchetypeClass.Ranger, AbilityCategory.Advanced, "#88FF88", true),
                
                new ClassAbility("ranger_disengage", "Disengage", 
                    "Emergency evasion: Backflip away from enemies while firing a volley of arrows.", 
                    EvasionUnlockLevel, ArchetypeClass.Ranger, AbilityCategory.Advanced, "#66FF66"),
                
                new ClassAbility("ranger_multishot", "Multishot", 
                    "Fire 3 arrows in a spread. Each arrow deals 60% damage.", 
                    35, ArchetypeClass.Ranger, AbilityCategory.Expert, "#00FF88"),
                
                new ClassAbility("ranger_headshot", "Headshot", 
                    "Critical hits deal 50% more damage.", 
                    50, ArchetypeClass.Ranger, AbilityCategory.Expert, "#FF4444", true),
                
                new ClassAbility("ranger_rain_arrows", "Rain of Arrows", 
                    "Fire a volley into the sky that rains down on an area, dealing damage over time.", 
                    75, ArchetypeClass.Ranger, AbilityCategory.Master, "#88FF00"),
                
                new ClassAbility("ranger_perfect_shot", "Perfect Shot", 
                    "Your next shot is guaranteed to critically hit for triple damage.", 
                    100, ArchetypeClass.Ranger, AbilityCategory.Ultimate, "#FFFF00"),
            };
            
            // ===== MAGE ABILITIES =====
            s_abilitiesByArchetype[ArchetypeClass.Mage] = new List<ClassAbility>
            {
                new ClassAbility("mage_cast", "Spellcasting", 
                    "Channel your staff to cast elemental spells. Costs eitr to cast.", 
                    1, ArchetypeClass.Mage, AbilityCategory.Starter, "#6666FF"),
                
                new ClassAbility("mage_infusion", "Elemental Infusion", 
                    "Enhance your magic, increasing spell damage by 30% for 20 seconds.", 
                    5, ArchetypeClass.Mage, AbilityCategory.Basic, "#FF6600"),
                
                new ClassAbility("mage_shield", "Arcane Shield", 
                    "Create a magical barrier that absorbs damage. Shield strength scales with eitr.", 
                    10, ArchetypeClass.Mage, AbilityCategory.Basic, "#66FFFF"),
                
                new ClassAbility("mage_eitr_regen", "Eitr Mastery", 
                    "Eitr regenerates 30% faster.", 
                    15, ArchetypeClass.Mage, AbilityCategory.Advanced, "#0066FF", true),
                
                new ClassAbility("mage_blink", "Arcane Blink", 
                    "Emergency evasion: Teleport a short distance, leaving an explosion at your origin.", 
                    EvasionUnlockLevel, ArchetypeClass.Mage, AbilityCategory.Advanced, "#AA00FF"),
                
                new ClassAbility("mage_overcharge", "Overcharge", 
                    "Your spells cost 50% more eitr but deal 75% more damage.", 
                    35, ArchetypeClass.Mage, AbilityCategory.Expert, "#FF00FF"),
                
                new ClassAbility("mage_chain", "Chain Casting", 
                    "Spells have a 30% chance to not consume eitr.", 
                    50, ArchetypeClass.Mage, AbilityCategory.Expert, "#00FFFF", true),
                
                new ClassAbility("mage_meteor", "Meteor", 
                    "Call down a meteor that deals massive AoE damage. Long cooldown.", 
                    75, ArchetypeClass.Mage, AbilityCategory.Master, "#FF4400"),
                
                new ClassAbility("mage_arcane_form", "Arcane Form", 
                    "Transform into pure magical energy. Spells cost no eitr and deal double damage for 15s.", 
                    100, ArchetypeClass.Mage, AbilityCategory.Ultimate, "#FF00FF"),
            };
            
            // ===== HEALER ABILITIES =====
            s_abilitiesByArchetype[ArchetypeClass.Healer] = new List<ClassAbility>
            {
                new ClassAbility("healer_heal", "Healing Staff", 
                    "Use your staff to heal allies. Healing scales with intelligence.", 
                    1, ArchetypeClass.Healer, AbilityCategory.Starter, "#00FF00"),
                
                new ClassAbility("healer_purify", "Purify", 
                    "Remove debuffs from yourself and gain a regeneration effect.", 
                    5, ArchetypeClass.Healer, AbilityCategory.Basic, "#88FF88"),
                
                new ClassAbility("healer_sanctuary", "Sanctuary", 
                    "Create an aura that reduces damage taken and slowly heals all nearby allies.", 
                    10, ArchetypeClass.Healer, AbilityCategory.Basic, "#AAFFAA"),
                
                new ClassAbility("healer_eitr_regen", "Spiritual Connection", 
                    "Eitr regenerates 30% faster.", 
                    15, ArchetypeClass.Healer, AbilityCategory.Advanced, "#66FF66", true),
                
                new ClassAbility("healer_fade", "Sanctuary Fade", 
                    "Emergency evasion: Teleport toward allies, creating a healing pulse at your destination.", 
                    EvasionUnlockLevel, ArchetypeClass.Healer, AbilityCategory.Advanced, "#00FFAA"),
                
                new ClassAbility("healer_purify_circle", "Purifying Circle", 
                    "Create a circle that cleanses debuffs from all allies inside and prevents new ones.", 
                    25, ArchetypeClass.Healer, AbilityCategory.Advanced, "#66FFAA"),
                
                new ClassAbility("healer_emergency", "Emergency Heal", 
                    "Automatically prioritize healing allies below 20% health.", 
                    35, ArchetypeClass.Healer, AbilityCategory.Expert, "#00FF66", true),
                
                new ClassAbility("healer_resurrection", "Resurrection", 
                    "Revive a defeated companion with 50% health. Very long cooldown.", 
                    50, ArchetypeClass.Healer, AbilityCategory.Expert, "#FFFFFF"),
                
                new ClassAbility("healer_divine_hymn", "Divine Hymn", 
                    "Channel a powerful healing song that rapidly heals all nearby allies.", 
                    75, ArchetypeClass.Healer, AbilityCategory.Master, "#AAFFFF"),
                
                new ClassAbility("healer_avatar_life", "Avatar of Life", 
                    "Become a font of healing energy. All healing is doubled and you cannot die for 20s.", 
                    100, ArchetypeClass.Healer, AbilityCategory.Ultimate, "#00FF00"),
            };
            
            // ===== MONK ABILITIES =====
            s_abilitiesByArchetype[ArchetypeClass.Monk] = new List<ClassAbility>
            {
                new ClassAbility("monk_martial_arts", "Martial Arts", 
                    "Your unarmed and club attacks are enhanced. Attack speed increases with combo hits.", 
                    1, ArchetypeClass.Monk, AbilityCategory.Starter, "#FFAA00"),
                
                new ClassAbility("monk_chi_strike", "Chi Strike", 
                    "Infuse your strikes with chi energy, dealing bonus damage for 15 seconds.", 
                    5, ArchetypeClass.Monk, AbilityCategory.Basic, "#FFFF00"),
                
                new ClassAbility("monk_inner_peace", "Inner Peace", 
                    "Enter a meditative state that regenerates health and cleanses debuffs.", 
                    10, ArchetypeClass.Monk, AbilityCategory.Basic, "#88FF88"),
                
                new ClassAbility("monk_stamina", "Endurance Training", 
                    "Stamina regenerates 25% faster and stamina costs are reduced by 15%.", 
                    15, ArchetypeClass.Monk, AbilityCategory.Advanced, "#FFCC00", true),
                
                new ClassAbility("monk_wind_step", "Wind Step", 
                    "Emergency evasion: Rapidly dodge in any direction, leaving afterimages that deal minor damage.", 
                    EvasionUnlockLevel, ArchetypeClass.Monk, AbilityCategory.Advanced, "#88FFFF"),
                
                new ClassAbility("monk_flurry", "Flurry of Blows", 
                    "Attack 5 times in rapid succession. Each hit deals reduced damage but builds combo.", 
                    35, ArchetypeClass.Monk, AbilityCategory.Expert, "#FF8800"),
                
                new ClassAbility("monk_iron_body", "Iron Body", 
                    "Harden your body, reducing all damage taken by 30% for 10 seconds.", 
                    50, ArchetypeClass.Monk, AbilityCategory.Expert, "#AAAAAA"),
                
                new ClassAbility("monk_chi_explosion", "Chi Explosion", 
                    "Release all your chi in a devastating AoE attack.", 
                    75, ArchetypeClass.Monk, AbilityCategory.Master, "#FFFF00"),
                
                new ClassAbility("monk_perfection", "Way of Perfection", 
                    "Achieve martial perfection. Attack speed doubled, all attacks critically hit for 15 seconds.", 
                    100, ArchetypeClass.Monk, AbilityCategory.Ultimate, "#FFD700"),
            };
        }

        /// <summary>Every archetype the roster defines.</summary>
        public static IEnumerable<ArchetypeClass> Archetypes => s_abilitiesByArchetype.Keys;

        /// <summary>Every ability in the roster, across all archetypes.</summary>
        public static IEnumerable<ClassAbility> AllAbilities => s_abilitiesByArchetype.Values.SelectMany(list => list);

        public static bool HasUnlockedHybrid(int level) => level >= HybridUnlockLevel;

        public static bool HasUnlockedHybridAbility(int level) => level >= HybridAbilityLevel;

        public static bool HasUnlockedEvasion(int level) => level >= EvasionUnlockLevel;

        /// <summary>An archetype's abilities, ordered by unlock level.</summary>
        public static List<ClassAbility> GetAbilitiesForArchetype(ArchetypeClass archetype) =>
            s_abilitiesByArchetype.TryGetValue(archetype, out var abilities)
                ? abilities.OrderBy(ability => ability.UnlockLevel).ToList()
                : new List<ClassAbility>();

        public static List<ClassAbility> GetUnlockedAbilities(ArchetypeClass archetype, int level) =>
            GetAbilitiesForArchetype(archetype).Where(ability => level >= ability.UnlockLevel).ToList();

        public static List<ClassAbility> GetLockedAbilities(ArchetypeClass archetype, int level) =>
            GetAbilitiesForArchetype(archetype).Where(ability => level < ability.UnlockLevel).ToList();

        public static ClassAbility GetNextUnlock(ArchetypeClass archetype, int currentLevel) =>
            GetLockedAbilities(archetype, currentLevel).OrderBy(ability => ability.UnlockLevel).FirstOrDefault();

        public static ClassAbility GetAbility(string abilityId, ArchetypeClass archetype) =>
            GetAbilitiesForArchetype(archetype).FirstOrDefault(ability => ability.Id == abilityId);

        /// <summary>Looks an ability up by id across every archetype, for callers that only hold the id.</summary>
        public static ClassAbility GetAbility(string abilityId) =>
            AllAbilities.FirstOrDefault(ability => ability.Id == abilityId);

        public static bool IsAbilityUnlocked(string abilityId, ArchetypeClass archetype, int level)
        {
            var ability = GetAbility(abilityId, archetype);
            return ability != null && level >= ability.UnlockLevel;
        }

        public static List<ClassAbility> GetPassives(ArchetypeClass archetype) =>
            GetAbilitiesForArchetype(archetype).Where(ability => ability.IsPassive).ToList();

        public static List<ClassAbility> GetAbilitiesInCategory(ArchetypeClass archetype, AbilityCategory category) =>
            GetAbilitiesForArchetype(archetype).Where(ability => ability.Category == category).ToList();
    }
}
