using System;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Manages independent skill leveling (1-100) for each archetype's abilities.
    /// Skills level up from use, separate from companion level.
    /// 
    /// DESIGN PHILOSOPHY:
    /// - Each archetype has its own set of skills (Taunt, Fortify, etc.)
    /// - Skills level from 1-100 through use
    /// - Higher skill level = more effective ability
    /// - Switching archetypes preserves skill progress - you can come back later
    /// - Creates meaningful progression within each archetype
    /// 
    /// SKILL XP FORMULA:
    /// - Base XP per use: 1-5 depending on ability tier
    /// - XP to level: 100 * currentLevel (so level 2 = 200 XP, level 50 = 5000 XP)
    /// - Max level: 100
    /// 
    /// SKILL EFFECTIVENESS:
    /// - Level 1: Base effectiveness (1.0x multiplier)
    /// - Level 50: 1.25x multiplier
    /// - Level 100: 1.5x multiplier
    /// - Linear scaling between levels
    /// </summary>
    public class ArchetypeSkillSystem : MonoBehaviour
    {
        #region Skill Definitions
        
        /// <summary>
        /// All archetype skills. Each skill levels independently.
        /// </summary>
        public enum ArchetypeSkill
        {
            // === TANK ===
            Tank_Taunt,
            Tank_Fortify,
            Tank_IronWall,
            Tank_Unyielding,
            Tank_ImmortalStance,
            
            // === PALADIN ===
            Paladin_DivineProtection,
            Paladin_HolySmite,
            Paladin_Consecration,
            Paladin_DivineShield,
            Paladin_LayOnHands,
            Paladin_AvatarOfLight,
            
            // === BERSERKER ===
            Berserker_Rage,
            Berserker_Warcry,
            Berserker_Execute,
            Berserker_Rampage,
            Berserker_DeathWish,
            Berserker_AvatarOfWar,
            
            // === ROGUE ===
            Rogue_Stealth,
            Rogue_Poison,
            Rogue_Caltrops,
            Rogue_Evasion,
            Rogue_DeathMark,
            Rogue_ShadowDance,
            
            // === RANGER ===
            Ranger_HuntersMark,
            Ranger_EagleEye,
            Ranger_Multishot,
            Ranger_RainOfArrows,
            Ranger_PerfectShot,
            
            // === MAGE ===
            Mage_ElementalInfusion,
            Mage_ArcaneShield,
            Mage_Overcharge,
            Mage_ChainCasting,
            Mage_Meteor,
            Mage_ArcaneForm,
            
            // === HEALER ===
            Healer_Purify,
            Healer_Sanctuary,
            Healer_PurifyingCircle,
            Healer_Resurrection,
            Healer_DivineHymn,
            Healer_AvatarOfLife,
            
            // === MONK ===
            Monk_ChiStrike,
            Monk_InnerPeace,
            Monk_FlurryOfBlows,
            Monk_IronBody,
            Monk_ChiExplosion,
            Monk_WayOfPerfection,
            
            // === EMERGENCY EVASION (per archetype) ===
            Evasion_ArcaneBlink,
            Evasion_SanctuaryFade,
            Evasion_SavageLeap,
            Evasion_ShadowEscape,
            Evasion_Disengage,
            Evasion_ShieldCharge,
            Evasion_DivineRetreat,
            Evasion_WindStep
        }
        
        /// <summary>
        /// Data for a single skill.
        /// </summary>
        [Serializable]
        public class SkillData
        {
            public ArchetypeSkill Skill;
            public int Level = 1;
            public float CurrentXP = 0;
            
            public float XPToNextLevel => GetXPToLevel(Level + 1);
            public float Progress => Level >= MAX_LEVEL ? 1f : CurrentXP / XPToNextLevel;
            
            private static float GetXPToLevel(int level)
            {
                // XP required = 100 * level
                // Level 2 = 200 XP, Level 50 = 5000 XP, Level 100 = 10000 XP
                return 100f * level;
            }
        }
        
        #endregion
        
        #region Constants
        
        public const int MAX_LEVEL = 100;
        public const float MIN_EFFECTIVENESS = 1.0f;
        public const float MAX_EFFECTIVENESS = 1.5f;
        
        // Base XP per ability tier (granted on ability USE)
        private const float XP_BASIC = 1f;      // Basic abilities
        private const float XP_ADVANCED = 2f;   // Advanced abilities
        private const float XP_EXPERT = 3f;     // Expert abilities (L35-50)
        private const float XP_MASTER = 4f;     // Master abilities (L75)
        private const float XP_ULTIMATE = 5f;   // Ultimate abilities (L100)
        
        // Bonus XP modifiers (multiplied by base XP)
        private const float XP_PER_ENEMY_HIT = 0.5f;      // +0.5x per enemy hit by offensive ability
        private const float XP_PER_ALLY_BUFFED = 0.3f;    // +0.3x per ally buffed/healed
        private const float XP_PER_ENEMY_KILLED = 2.0f;   // +2.0x per enemy killed by ability
        private const float XP_BOSS_KILL_BONUS = 5.0f;    // +5.0x for killing a boss/mini-boss
        private const float XP_ELITE_HIT_BONUS = 0.25f;   // +0.25x bonus for hitting elite enemies
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region State
        
        private CompanionController _companion;
        private CompanionLuck _luck;
        private ZNetView _nview;
        
        /// <summary>All skill data, indexed by skill enum.</summary>
        private Dictionary<ArchetypeSkill, SkillData> _skills = new Dictionary<ArchetypeSkill, SkillData>();
        
        /// <summary>Dirty flag for saving.</summary>
        private bool _isDirty = false;
        
        /// <summary>Tracks pending XP for abilities that hit/affect multiple targets.</summary>
        private Dictionary<ArchetypeSkill, PendingSkillXP> _pendingXP = new Dictionary<ArchetypeSkill, PendingSkillXP>();
        
        /// <summary>Tracks pending XP data for multi-hit abilities.</summary>
        private class PendingSkillXP
        {
            public float BaseXP;
            public AbilityTier Tier;
            public int EnemiesHit;
            public int AlliesAffected;
            public int EnemiesKilled;
            public int BossesKilled;
            public int ElitesHit;
            public float StartTime;
            public const float WINDOW_DURATION = 3f; // Collect results for 3 seconds
        }
        
        #endregion
        
        #region Initialization
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _luck = GetComponent<CompanionLuck>();
            _nview = GetComponent<ZNetView>();
            
            // Initialize all skills at level 1
            foreach (ArchetypeSkill skill in Enum.GetValues(typeof(ArchetypeSkill)))
            {
                _skills[skill] = new SkillData { Skill = skill, Level = 1, CurrentXP = 0 };
            }
        }
        
        private void Start()
        {
            // Load saved skill data
            LoadFromZDO();
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Gets the current level of a skill.
        /// </summary>
        public int GetSkillLevel(ArchetypeSkill skill)
        {
            return _skills.TryGetValue(skill, out var data) ? data.Level : 1;
        }
        
        /// <summary>
        /// Gets the effectiveness multiplier for a skill based on its level AND luck.
        /// Level 1 = 1.0x, Level 50 = 1.25x, Level 100 = 1.5x
        /// Luck further scales this by 0.75x to 1.25x (at 0 to 100 luck)
        /// 
        /// COMBINED FORMULA:
        /// - Skill Level Bonus: 1.0 to 1.5x (based on skill level 1-100)
        /// - Luck Modifier: 0.75x to 1.25x (based on companion luck 0-100)
        /// - Final: SkillBonus * LuckModifier
        /// 
        /// Example at Level 50, Luck 75:
        /// - Skill Bonus = 1.25x
        /// - Luck Modifier = 1.0 + (75/100 * 0.5 - 0.25) = 1.125x
        /// - Final = 1.25 * 1.125 = 1.406x
        /// </summary>
        public float GetSkillEffectiveness(ArchetypeSkill skill)
        {
            int level = GetSkillLevel(skill);
            
            // Base skill effectiveness from level (1.0 to 1.5)
            float t = (level - 1f) / (MAX_LEVEL - 1f);
            float skillBonus = Mathf.Lerp(MIN_EFFECTIVENESS, MAX_EFFECTIVENESS, t);
            
            // Apply luck modifier (0.75x to 1.25x based on luck 0-100)
            float luckModifier = GetLuckModifier();
            
            return skillBonus * luckModifier;
        }
        
        /// <summary>
        /// Gets the luck modifier for skill effectiveness.
        /// Luck 0 = 0.75x, Luck 50 = 1.0x, Luck 100 = 1.25x
        /// </summary>
        private float GetLuckModifier()
        {
            if (_luck == null)
            {
                _luck = GetComponent<CompanionLuck>();
            }
            
            if (_luck == null) return 1.0f; // No luck component = neutral
            
            // Luck ranges 0-100, we map to 0.75-1.25
            float luckFactor = _luck.BaseLuck / 100f;
            return 0.75f + (luckFactor * 0.5f);
        }
        
        /// <summary>
        /// Gets an XP bonus multiplier based on luck.
        /// Lucky companions gain skill XP faster.
        /// Luck 0 = 0.9x XP, Luck 50 = 1.0x XP, Luck 100 = 1.2x XP
        /// </summary>
        private float GetLuckXPModifier()
        {
            if (_luck == null)
            {
                _luck = GetComponent<CompanionLuck>();
            }
            
            if (_luck == null) return 1.0f;
            
            // Luck ranges 0-100, we map to 0.9-1.2 for XP
            float luckFactor = _luck.BaseLuck / 100f;
            return 0.9f + (luckFactor * 0.3f);
        }
        
        /// <summary>
        /// Gets the skill data for display purposes.
        /// </summary>
        public SkillData GetSkillData(ArchetypeSkill skill)
        {
            return _skills.TryGetValue(skill, out var data) ? data : null;
        }
        
        /// <summary>
        /// Grants XP to a skill from using an ability.
        /// This is the BASE XP - call OnAbilityHitEnemy/OnAbilityBuffedAlly for bonus XP.
        /// XP gain is modified by luck - lucky companions level skills faster.
        /// </summary>
        /// <param name="skill">The skill to grant XP to</param>
        /// <param name="tier">The ability tier (affects XP amount)</param>
        public void GrantSkillXP(ArchetypeSkill skill, AbilityTier tier = AbilityTier.Basic)
        {
            if (!_skills.TryGetValue(skill, out var data)) return;
            if (data.Level >= MAX_LEVEL) return;
            
            float xpGain = GetXPForTier(tier);
            
            // Apply luck XP modifier (0.9x to 1.2x based on luck)
            xpGain *= GetLuckXPModifier();
            
            data.CurrentXP += xpGain;
            
            // Check for level up
            while (data.CurrentXP >= data.XPToNextLevel && data.Level < MAX_LEVEL)
            {
                data.CurrentXP -= data.XPToNextLevel;
                data.Level++;
                
                OnSkillLevelUp(skill, data.Level);
            }
            
            _isDirty = true;
            
            if (VerboseLogging)
            {
                float luckMod = GetLuckXPModifier();
                Debug.Log($"[ArchetypeSkillSystem] {_companion?.companionName} gained {xpGain:F1} XP in {skill} " +
                    $"(luckMod={luckMod:F2}, now L{data.Level}, {data.Progress:P0})");
            }
        }
        
        /// <summary>
        /// Called when an ability is used - starts tracking XP for this ability.
        /// Follow up with OnAbilityHitEnemy/OnAbilityBuffedAlly for bonus XP.
        /// </summary>
        public void OnAbilityUsed(string abilityName)
        {
            var (skill, tier) = GetSkillForAbility(abilityName);
            if (skill.HasValue)
            {
                StartAbilityXPTracking(skill.Value, tier);
            }
        }
        
        /// <summary>
        /// Starts tracking XP for an ability use. Bonus XP is collected over a window.
        /// </summary>
        private void StartAbilityXPTracking(ArchetypeSkill skill, AbilityTier tier)
        {
            float baseXP = GetXPForTier(tier);
            
            _pendingXP[skill] = new PendingSkillXP
            {
                BaseXP = baseXP,
                Tier = tier,
                EnemiesHit = 0,
                AlliesAffected = 0,
                EnemiesKilled = 0,
                BossesKilled = 0,
                ElitesHit = 0,
                StartTime = Time.time
            };
            
            // Grant base XP immediately
            GrantSkillXP(skill, tier);
            
            if (VerboseLogging)
            {
                Debug.Log($"[ArchetypeSkillSystem] Started XP tracking for {skill}");
            }
        }
        
        /// <summary>
        /// Called when an ability hits an enemy - grants bonus XP.
        /// </summary>
        /// <param name="abilityName">Name of the ability that hit</param>
        /// <param name="enemy">The enemy that was hit</param>
        /// <param name="wasKilled">Whether the hit killed the enemy</param>
        public void OnAbilityHitEnemy(string abilityName, Character enemy, bool wasKilled = false)
        {
            var (skill, tier) = GetSkillForAbility(abilityName);
            if (!skill.HasValue) return;
            
            // Get or create pending XP tracking
            if (!_pendingXP.TryGetValue(skill.Value, out var pending))
            {
                // No tracking started - grant XP immediately
                float bonusXP = GetXPForTier(tier) * XP_PER_ENEMY_HIT;
                if (wasKilled) bonusXP += GetXPForTier(tier) * XP_PER_ENEMY_KILLED;
                if (IsEliteEnemy(enemy)) bonusXP += GetXPForTier(tier) * XP_ELITE_HIT_BONUS;
                if (IsBossEnemy(enemy) && wasKilled) bonusXP += GetXPForTier(tier) * XP_BOSS_KILL_BONUS;
                
                GrantBonusXP(skill.Value, bonusXP, "hit/kill");
                return;
            }
            
            // Check if tracking window expired
            if (Time.time - pending.StartTime > PendingSkillXP.WINDOW_DURATION)
            {
                FinalizeAbilityXP(skill.Value);
                return;
            }
            
            // Add to pending
            pending.EnemiesHit++;
            if (wasKilled) pending.EnemiesKilled++;
            if (IsEliteEnemy(enemy)) pending.ElitesHit++;
            if (IsBossEnemy(enemy) && wasKilled) pending.BossesKilled++;
        }
        
        /// <summary>
        /// Called when an ability buffs or heals an ally - grants bonus XP.
        /// </summary>
        /// <param name="abilityName">Name of the ability</param>
        /// <param name="ally">The ally that was affected</param>
        public void OnAbilityBuffedAlly(string abilityName, Character ally)
        {
            var (skill, tier) = GetSkillForAbility(abilityName);
            if (!skill.HasValue) return;
            
            // Get or create pending XP tracking
            if (!_pendingXP.TryGetValue(skill.Value, out var pending))
            {
                // No tracking started - grant XP immediately
                float bonusXP = GetXPForTier(tier) * XP_PER_ALLY_BUFFED;
                GrantBonusXP(skill.Value, bonusXP, "buff/heal");
                return;
            }
            
            // Check if tracking window expired
            if (Time.time - pending.StartTime > PendingSkillXP.WINDOW_DURATION)
            {
                FinalizeAbilityXP(skill.Value);
                return;
            }
            
            // Add to pending
            pending.AlliesAffected++;
        }
        
        /// <summary>
        /// Finalizes XP tracking for an ability and grants all bonus XP.
        /// </summary>
        private void FinalizeAbilityXP(ArchetypeSkill skill)
        {
            if (!_pendingXP.TryGetValue(skill, out var pending))
            {
                return;
            }
            
            // Calculate total bonus XP
            float bonusXP = 0f;
            bonusXP += pending.EnemiesHit * pending.BaseXP * XP_PER_ENEMY_HIT;
            bonusXP += pending.AlliesAffected * pending.BaseXP * XP_PER_ALLY_BUFFED;
            bonusXP += pending.EnemiesKilled * pending.BaseXP * XP_PER_ENEMY_KILLED;
            bonusXP += pending.BossesKilled * pending.BaseXP * XP_BOSS_KILL_BONUS;
            bonusXP += pending.ElitesHit * pending.BaseXP * XP_ELITE_HIT_BONUS;
            
            if (bonusXP > 0)
            {
                GrantBonusXP(skill, bonusXP, 
                    $"{pending.EnemiesHit} hits, {pending.AlliesAffected} buffs, {pending.EnemiesKilled} kills");
            }
            
            _pendingXP.Remove(skill);
        }
        
        /// <summary>
        /// Grants bonus XP to a skill (used for hits/buffs/kills).
        /// XP gain is modified by luck - lucky companions level skills faster.
        /// </summary>
        private void GrantBonusXP(ArchetypeSkill skill, float xp, string reason)
        {
            if (!_skills.TryGetValue(skill, out var data)) return;
            if (data.Level >= MAX_LEVEL) return;
            if (xp <= 0) return;
            
            // Apply luck XP modifier (0.9x to 1.2x based on luck)
            xp *= GetLuckXPModifier();
            
            data.CurrentXP += xp;
            
            // Check for level up
            while (data.CurrentXP >= data.XPToNextLevel && data.Level < MAX_LEVEL)
            {
                data.CurrentXP -= data.XPToNextLevel;
                data.Level++;
                
                OnSkillLevelUp(skill, data.Level);
            }
            
            _isDirty = true;
            
            if (VerboseLogging)
            {
                Debug.Log($"[ArchetypeSkillSystem] {_companion?.companionName} gained {xp:F1} bonus XP in {skill} ({reason})");
            }
        }
        
        /// <summary>
        /// Checks if an enemy is an elite (higher tier enemy).
        /// </summary>
        private bool IsEliteEnemy(Character enemy)
        {
            if (enemy == null) return false;
            
            // Check for star rating (1 or 2 star = elite)
            var level = enemy.GetLevel();
            if (level >= 2) return true;
            
            // Check for specific enemy types that are always elite
            string name = enemy.m_name?.ToLower() ?? "";
            if (name.Contains("troll") || name.Contains("abomination") || name.Contains("golem"))
            {
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if an enemy is a boss or mini-boss.
        /// </summary>
        private bool IsBossEnemy(Character enemy)
        {
            if (enemy == null) return false;
            
            // Check for boss flag
            if (enemy.IsBoss()) return true;
            
            // Check for specific boss names
            string name = enemy.m_name?.ToLower() ?? "";
            if (name.Contains("eikthyr") || name.Contains("elder") || name.Contains("bonemass") ||
                name.Contains("moder") || name.Contains("yagluth") || name.Contains("queen") ||
                name.Contains("fader"))
            {
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Gets all skills for a specific archetype.
        /// </summary>
        public IEnumerable<SkillData> GetSkillsForArchetype(string archetypeName)
        {
            string prefix = archetypeName + "_";
            foreach (var kvp in _skills)
            {
                if (kvp.Key.ToString().StartsWith(prefix))
                {
                    yield return kvp.Value;
                }
            }
        }
        
        /// <summary>
        /// Gets total skill points invested in an archetype (sum of all skill levels - base levels).
        /// </summary>
        public int GetTotalSkillPointsInArchetype(string archetypeName)
        {
            int total = 0;
            foreach (var data in GetSkillsForArchetype(archetypeName))
            {
                total += data.Level - 1; // -1 because everyone starts at level 1
            }
            return total;
        }
        
        /// <summary>
        /// Gets the total of all skill levels across all archetypes.
        /// </summary>
        public float GetTotalSkillLevels()
        {
            float total = 0f;
            foreach (var kvp in _skills)
            {
                if (kvp.Value.Level > 1)
                {
                    total += kvp.Value.Level;
                }
            }
            return total;
        }
        
        /// <summary>
        /// Called when the companion gets a kill with any offensive ability.
        /// Grants bonus kill XP to any recently-used combat skills.
        /// </summary>
        /// <param name="target">The enemy that was killed</param>
        /// <param name="wasElite">Whether the enemy was elite (starred)</param>
        public void OnKillWithAbility(Character target, bool wasElite = false)
        {
            if (target == null) return;
            
            bool isBoss = IsBossEnemy(target);
            
            // Get the current archetype to determine which skill to reward
            var archetypeController = GetComponent<ArchetypeController>();
            if (archetypeController == null) return;
            
            // Determine the primary offensive skill for this archetype
            ArchetypeSkill? primarySkill = GetPrimaryOffensiveSkill(archetypeController.CurrentArchetypeClass);
            if (!primarySkill.HasValue) return;
            
            // Calculate bonus XP
            float bonusXP = XP_PER_ENEMY_KILLED * XP_BASIC;
            if (wasElite) bonusXP += XP_ELITE_HIT_BONUS * XP_BASIC;
            if (isBoss) bonusXP += XP_BOSS_KILL_BONUS * XP_BASIC;
            
            // Grant the bonus XP to the primary offensive skill
            GrantBonusXP(primarySkill.Value, bonusXP, isBoss ? "boss kill" : wasElite ? "elite kill" : "kill");
            
            if (VerboseLogging)
            {
                Debug.Log($"[ArchetypeSkillSystem] {_companion?.companionName} got kill bonus XP for {primarySkill.Value}");
            }
        }
        
        /// <summary>
        /// Gets the primary offensive skill for an archetype.
        /// </summary>
        private static ArchetypeSkill? GetPrimaryOffensiveSkill(ArchetypeClass archetype)
        {
            switch (archetype)
            {
                case ArchetypeClass.Tank: return ArchetypeSkill.Tank_Taunt;
                case ArchetypeClass.Paladin: return ArchetypeSkill.Paladin_HolySmite;
                case ArchetypeClass.Berserker: return ArchetypeSkill.Berserker_Rage;
                case ArchetypeClass.Rogue: return ArchetypeSkill.Rogue_Poison;
                case ArchetypeClass.Ranger: return ArchetypeSkill.Ranger_EagleEye;
                case ArchetypeClass.Mage: return ArchetypeSkill.Mage_ElementalInfusion;
                case ArchetypeClass.Healer: return ArchetypeSkill.Healer_Sanctuary;
                case ArchetypeClass.Monk: return ArchetypeSkill.Monk_ChiStrike;
                default: return null;
            }
        }
        
        #endregion
        
        #region Ability Tier
        
        public enum AbilityTier
        {
            Basic,      // Starter abilities
            Advanced,   // L10-20 abilities
            Expert,     // L35-50 abilities
            Master,     // L75 abilities
            Ultimate    // L100 abilities
        }
        
        private static float GetXPForTier(AbilityTier tier)
        {
            switch (tier)
            {
                case AbilityTier.Basic: return XP_BASIC;
                case AbilityTier.Advanced: return XP_ADVANCED;
                case AbilityTier.Expert: return XP_EXPERT;
                case AbilityTier.Master: return XP_MASTER;
                case AbilityTier.Ultimate: return XP_ULTIMATE;
                default: return XP_BASIC;
            }
        }
        
        /// <summary>
        /// Maps ability names to their corresponding skill and tier.
        /// </summary>
        private static (ArchetypeSkill? skill, AbilityTier tier) GetSkillForAbility(string abilityName)
        {
            switch (abilityName.ToLower())
            {
                // Tank
                case "taunt": return (ArchetypeSkill.Tank_Taunt, AbilityTier.Basic);
                case "fortify": return (ArchetypeSkill.Tank_Fortify, AbilityTier.Basic);
                case "ironwall": return (ArchetypeSkill.Tank_IronWall, AbilityTier.Expert);
                case "unyielding": return (ArchetypeSkill.Tank_Unyielding, AbilityTier.Master);
                case "immortalstance": return (ArchetypeSkill.Tank_ImmortalStance, AbilityTier.Ultimate);
                
                // Paladin
                case "divineprotection": return (ArchetypeSkill.Paladin_DivineProtection, AbilityTier.Basic);
                case "holysmite": return (ArchetypeSkill.Paladin_HolySmite, AbilityTier.Basic);
                case "consecration": return (ArchetypeSkill.Paladin_Consecration, AbilityTier.Expert);
                case "divineshield": return (ArchetypeSkill.Paladin_DivineShield, AbilityTier.Expert);
                case "layonhands": return (ArchetypeSkill.Paladin_LayOnHands, AbilityTier.Master);
                case "avataroflight": return (ArchetypeSkill.Paladin_AvatarOfLight, AbilityTier.Ultimate);
                
                // Berserker
                case "rage":
                case "berserkrage": return (ArchetypeSkill.Berserker_Rage, AbilityTier.Basic);
                case "warcry": return (ArchetypeSkill.Berserker_Warcry, AbilityTier.Basic);
                case "execute": return (ArchetypeSkill.Berserker_Execute, AbilityTier.Expert);
                case "rampage": return (ArchetypeSkill.Berserker_Rampage, AbilityTier.Expert);
                case "deathwish": return (ArchetypeSkill.Berserker_DeathWish, AbilityTier.Master);
                case "avatarofwar": return (ArchetypeSkill.Berserker_AvatarOfWar, AbilityTier.Ultimate);
                
                // Rogue
                case "stealth": return (ArchetypeSkill.Rogue_Stealth, AbilityTier.Basic);
                case "poison": return (ArchetypeSkill.Rogue_Poison, AbilityTier.Basic);
                case "caltrops": return (ArchetypeSkill.Rogue_Caltrops, AbilityTier.Basic);
                case "evasion": return (ArchetypeSkill.Rogue_Evasion, AbilityTier.Expert);
                case "deathmark": return (ArchetypeSkill.Rogue_DeathMark, AbilityTier.Master);
                case "shadowdance": return (ArchetypeSkill.Rogue_ShadowDance, AbilityTier.Ultimate);
                
                // Ranger
                case "huntersmark": return (ArchetypeSkill.Ranger_HuntersMark, AbilityTier.Basic);
                case "eagleeye": return (ArchetypeSkill.Ranger_EagleEye, AbilityTier.Basic);
                case "multishot": return (ArchetypeSkill.Ranger_Multishot, AbilityTier.Expert);
                case "rainofarrows": return (ArchetypeSkill.Ranger_RainOfArrows, AbilityTier.Master);
                case "perfectshot": return (ArchetypeSkill.Ranger_PerfectShot, AbilityTier.Ultimate);
                
                // Mage
                case "elementalinfusion": return (ArchetypeSkill.Mage_ElementalInfusion, AbilityTier.Basic);
                case "arcaneshield": return (ArchetypeSkill.Mage_ArcaneShield, AbilityTier.Basic);
                case "overcharge": return (ArchetypeSkill.Mage_Overcharge, AbilityTier.Expert);
                case "chaincasting": return (ArchetypeSkill.Mage_ChainCasting, AbilityTier.Expert);
                case "meteor": return (ArchetypeSkill.Mage_Meteor, AbilityTier.Master);
                case "arcaneform": return (ArchetypeSkill.Mage_ArcaneForm, AbilityTier.Ultimate);
                
                // Healer
                case "purify": return (ArchetypeSkill.Healer_Purify, AbilityTier.Basic);
                case "sanctuary": return (ArchetypeSkill.Healer_Sanctuary, AbilityTier.Basic);
                case "purifyingcircle": return (ArchetypeSkill.Healer_PurifyingCircle, AbilityTier.Advanced);
                case "resurrection": return (ArchetypeSkill.Healer_Resurrection, AbilityTier.Expert);
                case "divinehymn": return (ArchetypeSkill.Healer_DivineHymn, AbilityTier.Master);
                case "avataroflife": return (ArchetypeSkill.Healer_AvatarOfLife, AbilityTier.Ultimate);
                
                // Monk
                case "chistrike": return (ArchetypeSkill.Monk_ChiStrike, AbilityTier.Basic);
                case "innerpeace": return (ArchetypeSkill.Monk_InnerPeace, AbilityTier.Basic);
                case "flurryofblows": return (ArchetypeSkill.Monk_FlurryOfBlows, AbilityTier.Expert);
                case "ironbody": return (ArchetypeSkill.Monk_IronBody, AbilityTier.Expert);
                case "chiexplosion": return (ArchetypeSkill.Monk_ChiExplosion, AbilityTier.Master);
                case "wayofperfection": return (ArchetypeSkill.Monk_WayOfPerfection, AbilityTier.Ultimate);
                
                // Emergency Evasion
                case "arcaneblink": return (ArchetypeSkill.Evasion_ArcaneBlink, AbilityTier.Advanced);
                case "sanctuaryfade": return (ArchetypeSkill.Evasion_SanctuaryFade, AbilityTier.Advanced);
                case "savageleap": return (ArchetypeSkill.Evasion_SavageLeap, AbilityTier.Advanced);
                case "shadowescape": return (ArchetypeSkill.Evasion_ShadowEscape, AbilityTier.Advanced);
                case "disengage": return (ArchetypeSkill.Evasion_Disengage, AbilityTier.Advanced);
                case "shieldcharge": return (ArchetypeSkill.Evasion_ShieldCharge, AbilityTier.Advanced);
                case "divineretreat": return (ArchetypeSkill.Evasion_DivineRetreat, AbilityTier.Advanced);
                case "windstep": return (ArchetypeSkill.Evasion_WindStep, AbilityTier.Advanced);
                
                default: return (null, AbilityTier.Basic);
            }
        }
        
        #endregion
        
        #region Events
        
        private void OnSkillLevelUp(ArchetypeSkill skill, int newLevel)
        {
            // Notify player
            if (_companion != null && Player.m_localPlayer != null)
            {
                string skillName = GetSkillDisplayName(skill);
                string message = $"<color=cyan>{_companion.companionName}</color>'s <color=yellow>{skillName}</color> increased to level {newLevel}!";
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, message);
                
                // Special messages for milestones
                if (newLevel == 25 || newLevel == 50 || newLevel == 75 || newLevel == 100)
                {
                    float effectiveness = GetSkillEffectiveness(skill);
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, 
                        $"{skillName} Mastery: Level {newLevel}\n" +
                        $"<size=14>Effectiveness: {effectiveness:P0}</size>");
                }
            }
            
            Debug.Log($"[ArchetypeSkillSystem] {_companion?.companionName} reached {skill} level {newLevel}!");
        }
        
        private static string GetSkillDisplayName(ArchetypeSkill skill)
        {
            // Convert enum to readable name
            string name = skill.ToString();
            int underscore = name.IndexOf('_');
            if (underscore >= 0 && underscore < name.Length - 1)
            {
                name = name.Substring(underscore + 1);
            }
            
            // Add spaces before capitals
            var result = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                if (char.IsUpper(c) && result.Length > 0)
                {
                    result.Append(' ');
                }
                result.Append(c);
            }
            
            return result.ToString();
        }
        
        #endregion
        
        #region Persistence
        
        private const string ZDO_KEY_SKILLS = "va_archetype_skills";
        
        private void LoadFromZDO()
        {
            if (_nview == null || !_nview.IsValid()) return;
            
            string serialized = _nview.GetZDO()?.GetString(ZDO_KEY_SKILLS, "");
            if (string.IsNullOrEmpty(serialized)) return;
            
            try
            {
                // Format: "skill1:level:xp,skill2:level:xp,..."
                var parts = serialized.Split(',');
                foreach (var part in parts)
                {
                    var values = part.Split(':');
                    if (values.Length >= 3)
                    {
                        if (Enum.TryParse<ArchetypeSkill>(values[0], out var skill))
                        {
                            if (int.TryParse(values[1], out int level) && float.TryParse(values[2], out float xp))
                            {
                                if (_skills.TryGetValue(skill, out var data))
                                {
                                    data.Level = Mathf.Clamp(level, 1, MAX_LEVEL);
                                    data.CurrentXP = Mathf.Max(0, xp);
                                }
                            }
                        }
                    }
                }
                
                if (VerboseLogging)
                {
                    Debug.Log($"[ArchetypeSkillSystem] Loaded skills for {_companion?.companionName}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ArchetypeSkillSystem] Failed to load skills: {ex.Message}");
            }
        }
        
        public void SaveToZDO()
        {
            if (_nview == null || !_nview.IsValid()) return;
            if (!_nview.IsOwner()) return;
            if (!_isDirty) return;
            
            try
            {
                // Serialize only skills that have been used (level > 1 or XP > 0)
                var parts = new List<string>();
                foreach (var kvp in _skills)
                {
                    if (kvp.Value.Level > 1 || kvp.Value.CurrentXP > 0)
                    {
                        parts.Add($"{kvp.Key}:{kvp.Value.Level}:{kvp.Value.CurrentXP:F1}");
                    }
                }
                
                // Skip the ZDO write during the local player's respawn / loading-screen
                // window â€” writing here has been observed to deadlock the zone stream.
                // _isDirty stays true so the next periodic Update tick retries after the
                // gate opens.
                if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;

                string serialized = string.Join(",", parts);
                _nview.GetZDO()?.Set(ZDO_KEY_SKILLS, serialized);

                _isDirty = false;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[ArchetypeSkillSystem] Saved {parts.Count} skills for {_companion?.companionName}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ArchetypeSkillSystem] Failed to save skills: {ex.Message}");
            }
        }
        
        private void OnDestroy()
        {
            // Save on destroy
            SaveToZDO();
        }
        
        // Periodic save
        private float _lastSaveTime;
        private const float SAVE_INTERVAL = 60f;
        
        private void Update()
        {
            // Save periodically
            if (_isDirty && Time.time - _lastSaveTime > SAVE_INTERVAL)
            {
                _lastSaveTime = Time.time;
                SaveToZDO();
            }
            
            // Finalize expired pending XP
            var expiredSkills = new List<ArchetypeSkill>();
            foreach (var kvp in _pendingXP)
            {
                if (Time.time - kvp.Value.StartTime > PendingSkillXP.WINDOW_DURATION)
                {
                    expiredSkills.Add(kvp.Key);
                }
            }
            
            foreach (var skill in expiredSkills)
            {
                FinalizeAbilityXP(skill);
            }
        }
        
        #endregion
    }
}
