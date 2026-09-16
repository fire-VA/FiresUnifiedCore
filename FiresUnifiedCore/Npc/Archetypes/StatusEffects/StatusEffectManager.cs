using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.Archetypes.StatusEffects.Base;
using FiresCore.Npc.Archetypes.StatusEffects.Common;
using FiresCore.Npc.Archetypes.StatusEffects.Tank;
using FiresCore.Npc.Archetypes.StatusEffects.Berserker;
using FiresCore.Npc.Archetypes.StatusEffects.Rogue;
using FiresCore.Npc.Archetypes.StatusEffects.Healer;
using FiresCore.Npc.Archetypes.StatusEffects.Monk;
using FiresCore.Npc.Archetypes.StatusEffects.Paladin;
using FiresCore.Npc.Archetypes.StatusEffects.Ranger;
using FiresCore.Npc.Archetypes.StatusEffects.Mage;
using FiresCore.Npc.Archetypes.StatusEffects.Master;
using FiresCore.Npc.Archetypes.StatusEffects.Ultimate;
using FiresCore.Npc.Archetypes.StatusEffects.Expert;

namespace FiresCore.Npc.Archetypes.StatusEffects
{
    /// <summary>
    /// Registers every companion status effect with ObjectDB (call RegisterAllEffects once ObjectDB is loaded)
    /// and applies them with the right cached HUD icon through the Apply methods. Effects live in folders by
    /// archetype.
    /// </summary>
    public static class StatusEffectManager
    {
        private static bool _effectsRegistered = false;
        
        public static bool VerboseLogging = false;
        
        /// <summary>
        /// Cached icons by effect name - ensures consistent icons across all effect applications.
        /// </summary>
        private static Dictionary<string, Sprite> _iconCache = new Dictionary<string, Sprite>();
        
        /// <summary>
        /// Cached colors by effect name - for creating icons on demand.
        /// </summary>
        private static Dictionary<string, Color> _iconColors = new Dictionary<string, Color>();
        
        // Effect name constants for easy reference
        // Common
        public const string EFFECT_INVULNERABLE = "CompanionInvulnerable";
        public const string EFFECT_ROOTED = "CompanionRooted";
        public const string EFFECT_SLOWDOWN = "CompanionSlowed";
        
        // Tank
        public const string EFFECT_FORTIFY = "CompanionFortify";
        
        // Paladin
        public const string EFFECT_DIVINE_PROTECTION = "CompanionDivineProtection";
        public const string EFFECT_HOLY_SMITE = "CompanionHolySmite";
        
        // Berserker
        public const string EFFECT_BERSERK_RAGE = "CompanionBerserkRage";
        public const string EFFECT_WARCRY = "CompanionWarcry";
        
        // Rogue
        public const string EFFECT_CALTROPS = "CompanionCaltrops";
        public const string EFFECT_POISON = "CompanionPoison";
        public const string EFFECT_STEALTH = "CompanionStealth";
        
        // Ranger
        public const string EFFECT_HUNTERS_MARK = "CompanionHuntersMark";
        public const string EFFECT_EAGLE_EYE = "CompanionEagleEye";
        
        // Mage
        public const string EFFECT_ELEMENTAL_INFUSION = "CompanionElementalInfusion";
        public const string EFFECT_ARCANE_SHIELD = "CompanionArcaneShield";
        
        // Healer
        public const string EFFECT_PURIFY = "CompanionPurify";
        public const string EFFECT_PURIFYING_CIRCLE = "CompanionPurifyingCircle";
        public const string EFFECT_SANCTUARY = "CompanionSanctuary";
        
        // Monk
        public const string EFFECT_CHI_STRIKE = "CompanionChiStrike";
        public const string EFFECT_INNER_PEACE = "CompanionInnerPeace";
        
        // === MASTER ABILITIES (Level 75) ===
        public const string EFFECT_UNYIELDING = "CompanionUnyielding";
        public const string EFFECT_LAY_ON_HANDS = "CompanionLayOnHands";
        public const string EFFECT_DEATH_WISH = "CompanionDeathWish";
        public const string EFFECT_DEATH_MARK = "CompanionDeathMark";
        public const string EFFECT_RAIN_OF_ARROWS = "CompanionRainOfArrows";
        public const string EFFECT_METEOR = "CompanionMeteor";
        public const string EFFECT_DIVINE_HYMN = "CompanionDivineHymn";
        public const string EFFECT_CHI_EXPLOSION = "CompanionChiExplosion";
        
        // === ULTIMATE ABILITIES (Level 100) ===
        public const string EFFECT_IMMORTAL_STANCE = "CompanionImmortalStance";
        public const string EFFECT_AVATAR_OF_LIGHT = "CompanionAvatarOfLight";
        public const string EFFECT_AVATAR_OF_WAR = "CompanionAvatarOfWar";
        public const string EFFECT_SHADOW_DANCE = "CompanionShadowDance";
        public const string EFFECT_PERFECT_SHOT = "CompanionPerfectShot";
        public const string EFFECT_ARCANE_FORM = "CompanionArcaneForm";
        public const string EFFECT_AVATAR_OF_LIFE = "CompanionAvatarOfLife";
        public const string EFFECT_WAY_OF_PERFECTION = "CompanionWayOfPerfection";
        
        // === EXPERT ABILITIES (Level 35-50) ===
        public const string EFFECT_IRON_WALL = "CompanionIronWall";
        public const string EFFECT_CONSECRATION = "CompanionConsecration";
        public const string EFFECT_DIVINE_SHIELD = "CompanionDivineShield";
        public const string EFFECT_EXECUTE = "CompanionExecute";
        public const string EFFECT_RAMPAGE = "CompanionRampage";
        public const string EFFECT_EVASION = "CompanionEvasion";
        public const string EFFECT_MULTISHOT = "CompanionMultishot";
        public const string EFFECT_OVERCHARGE = "CompanionOvercharge";
        public const string EFFECT_CHAIN_CASTING = "CompanionChainCasting";
        public const string EFFECT_RESURRECTION = "CompanionResurrection";
        public const string EFFECT_FLURRY_OF_BLOWS = "CompanionFlurryOfBlows";
        public const string EFFECT_IRON_BODY = "CompanionIronBody";
        
        /// <summary>
        /// Registers all companion status effects with ObjectDB.
        /// Call this after ObjectDB is available (e.g., in Awake or after game loads).
        /// </summary>
        public static void RegisterAllEffects()
        {
            if (_effectsRegistered) return;
            if (ObjectDB.instance == null) return;
            
            try
            {
                // Pre-register all icon colors (ensures icons work even if effects aren't registered yet)
                InitializeIconColors();
                
                // Register Common effects
                RegisterEffect<InvulnerableEffect>(EFFECT_INVULNERABLE, _iconColors[EFFECT_INVULNERABLE]);
                RegisterEffect<RootedEffect>(EFFECT_ROOTED, _iconColors[EFFECT_ROOTED]);
                RegisterEffect<SlowdownEffect>(EFFECT_SLOWDOWN, _iconColors[EFFECT_SLOWDOWN]);
                
                // Register Tank effects
                RegisterEffect<FortifyEffect>(EFFECT_FORTIFY, _iconColors[EFFECT_FORTIFY]);
                
                // Register Paladin effects
                RegisterEffect<DivineProtectionEffect>(EFFECT_DIVINE_PROTECTION, _iconColors[EFFECT_DIVINE_PROTECTION]);
                RegisterEffect<HolySmiteEffect>(EFFECT_HOLY_SMITE, _iconColors[EFFECT_HOLY_SMITE]);
                
                // Register Berserker effects
                RegisterEffect<BerserkRageEffect>(EFFECT_BERSERK_RAGE, _iconColors[EFFECT_BERSERK_RAGE]);
                RegisterEffect<WarcryEffect>(EFFECT_WARCRY, _iconColors[EFFECT_WARCRY]);
                
                // Register Rogue effects
                RegisterEffect<CaltropsEffect>(EFFECT_CALTROPS, _iconColors[EFFECT_CALTROPS]);
                RegisterEffect<PoisonEffect>(EFFECT_POISON, _iconColors[EFFECT_POISON]);
                RegisterEffect<StealthEffect>(EFFECT_STEALTH, _iconColors[EFFECT_STEALTH]);
                
                // Register Ranger effects
                RegisterEffect<HuntersMarkEffect>(EFFECT_HUNTERS_MARK, _iconColors[EFFECT_HUNTERS_MARK]);
                RegisterEffect<EagleEyeEffect>(EFFECT_EAGLE_EYE, _iconColors[EFFECT_EAGLE_EYE]);
                
                // Register Mage effects
                RegisterEffect<ElementalInfusionEffect>(EFFECT_ELEMENTAL_INFUSION, _iconColors[EFFECT_ELEMENTAL_INFUSION]);
                RegisterEffect<ArcaneShieldEffect>(EFFECT_ARCANE_SHIELD, _iconColors[EFFECT_ARCANE_SHIELD]);
                
                // Register Healer effects
                RegisterEffect<PurifyEffect>(EFFECT_PURIFY, _iconColors[EFFECT_PURIFY]);
                RegisterEffect<PurifyingCircleEffect>(EFFECT_PURIFYING_CIRCLE, _iconColors[EFFECT_PURIFYING_CIRCLE]);
                RegisterEffect<SanctuaryEffect>(EFFECT_SANCTUARY, _iconColors[EFFECT_SANCTUARY]);
                
                // Register Monk effects
                RegisterEffect<ChiStrikeEffect>(EFFECT_CHI_STRIKE, _iconColors[EFFECT_CHI_STRIKE]);
                RegisterEffect<InnerPeaceEffect>(EFFECT_INNER_PEACE, _iconColors[EFFECT_INNER_PEACE]);
                
                // === REGISTER EXPERT ABILITIES (Level 35-50) ===
                RegisterEffect<Expert.IronWallEffect>(EFFECT_IRON_WALL, _iconColors[EFFECT_IRON_WALL]);
                RegisterEffect<Expert.ConsecrationEffect>(EFFECT_CONSECRATION, _iconColors[EFFECT_CONSECRATION]);
                RegisterEffect<Expert.DivineShieldEffect>(EFFECT_DIVINE_SHIELD, _iconColors[EFFECT_DIVINE_SHIELD]);
                RegisterEffect<Expert.ExecuteEffect>(EFFECT_EXECUTE, _iconColors[EFFECT_EXECUTE]);
                RegisterEffect<Expert.RampageEffect>(EFFECT_RAMPAGE, _iconColors[EFFECT_RAMPAGE]);
                RegisterEffect<Expert.EvasionEffect>(EFFECT_EVASION, _iconColors[EFFECT_EVASION]);
                RegisterEffect<Expert.MultishotEffect>(EFFECT_MULTISHOT, _iconColors[EFFECT_MULTISHOT]);
                RegisterEffect<Expert.OverchargeEffect>(EFFECT_OVERCHARGE, _iconColors[EFFECT_OVERCHARGE]);
                RegisterEffect<Expert.ChainCastingEffect>(EFFECT_CHAIN_CASTING, _iconColors[EFFECT_CHAIN_CASTING]);
                RegisterEffect<Expert.ResurrectionEffect>(EFFECT_RESURRECTION, _iconColors[EFFECT_RESURRECTION]);
                RegisterEffect<Expert.FlurryOfBlowsEffect>(EFFECT_FLURRY_OF_BLOWS, _iconColors[EFFECT_FLURRY_OF_BLOWS]);
                RegisterEffect<Expert.IronBodyEffect>(EFFECT_IRON_BODY, _iconColors[EFFECT_IRON_BODY]);
                
                // === REGISTER MASTER ABILITIES (Level 75) ===
                RegisterEffect<Master.UnyieldingEffect>(EFFECT_UNYIELDING, _iconColors[EFFECT_UNYIELDING]);
                RegisterEffect<Master.LayOnHandsEffect>(EFFECT_LAY_ON_HANDS, _iconColors[EFFECT_LAY_ON_HANDS]);
                RegisterEffect<Master.DeathWishEffect>(EFFECT_DEATH_WISH, _iconColors[EFFECT_DEATH_WISH]);
                RegisterEffect<Master.DeathMarkEffect>(EFFECT_DEATH_MARK, _iconColors[EFFECT_DEATH_MARK]);
                RegisterEffect<Master.RainOfArrowsEffect>(EFFECT_RAIN_OF_ARROWS, _iconColors[EFFECT_RAIN_OF_ARROWS]);
                RegisterEffect<Master.MeteorEffect>(EFFECT_METEOR, _iconColors[EFFECT_METEOR]);
                RegisterEffect<Master.DivineHymnEffect>(EFFECT_DIVINE_HYMN, _iconColors[EFFECT_DIVINE_HYMN]);
                RegisterEffect<Master.ChiExplosionEffect>(EFFECT_CHI_EXPLOSION, _iconColors[EFFECT_CHI_EXPLOSION]);
                
                // === REGISTER ULTIMATE ABILITIES (Level 100) ===
                RegisterEffect<Ultimate.ImmortalStanceEffect>(EFFECT_IMMORTAL_STANCE, _iconColors[EFFECT_IMMORTAL_STANCE]);
                RegisterEffect<Ultimate.AvatarOfLightEffect>(EFFECT_AVATAR_OF_LIGHT, _iconColors[EFFECT_AVATAR_OF_LIGHT]);
                RegisterEffect<Ultimate.AvatarOfWarEffect>(EFFECT_AVATAR_OF_WAR, _iconColors[EFFECT_AVATAR_OF_WAR]);
                RegisterEffect<Ultimate.ShadowDanceEffect>(EFFECT_SHADOW_DANCE, _iconColors[EFFECT_SHADOW_DANCE]);
                RegisterEffect<Ultimate.PerfectShotEffect>(EFFECT_PERFECT_SHOT, _iconColors[EFFECT_PERFECT_SHOT]);
                RegisterEffect<Ultimate.ArcaneFormEffect>(EFFECT_ARCANE_FORM, _iconColors[EFFECT_ARCANE_FORM]);
                RegisterEffect<Ultimate.AvatarOfLifeEffect>(EFFECT_AVATAR_OF_LIFE, _iconColors[EFFECT_AVATAR_OF_LIFE]);
                RegisterEffect<Ultimate.WayOfPerfectionEffect>(EFFECT_WAY_OF_PERFECTION, _iconColors[EFFECT_WAY_OF_PERFECTION]);
                
                _effectsRegistered = true;
                
                Debug.Log($"[StatusEffectManager] All companion status effects registered ({_iconCache.Count} icons cached)");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[StatusEffectManager] Failed to register effects: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Pre-registers all icon colors. This ensures GetEffectIcon works even before RegisterAllEffects is called.
        /// </summary>
        private static void InitializeIconColors()
        {
            // Common
            _iconColors[EFFECT_INVULNERABLE] = Color.yellow;
            _iconColors[EFFECT_ROOTED] = new Color(0.5f, 0.3f, 0.1f); // Brown
            _iconColors[EFFECT_SLOWDOWN] = new Color(0.4f, 0.6f, 0.8f); // Light blue
            
            // Tank
            _iconColors[EFFECT_FORTIFY] = new Color(0.3f, 0.3f, 0.8f); // Blue
            
            // Paladin
            _iconColors[EFFECT_DIVINE_PROTECTION] = new Color(1f, 0.85f, 0.4f); // Gold
            _iconColors[EFFECT_HOLY_SMITE] = new Color(1f, 1f, 0.6f); // Bright yellow
            
            // Berserker
            _iconColors[EFFECT_BERSERK_RAGE] = new Color(0.8f, 0.2f, 0.2f); // Red
            _iconColors[EFFECT_WARCRY] = new Color(0.9f, 0.3f, 0.1f); // Orange-red
            
            // Rogue
            _iconColors[EFFECT_CALTROPS] = new Color(0.5f, 0.5f, 0.5f); // Gray
            _iconColors[EFFECT_POISON] = new Color(0.4f, 0.8f, 0.2f); // Poison green
            _iconColors[EFFECT_STEALTH] = new Color(0.3f, 0.3f, 0.4f); // Dark gray
            
            // Ranger
            _iconColors[EFFECT_HUNTERS_MARK] = new Color(0.8f, 0.2f, 0.2f); // Red (target)
            _iconColors[EFFECT_EAGLE_EYE] = new Color(0.4f, 0.7f, 0.3f); // Forest green
            
            // Mage
            _iconColors[EFFECT_ELEMENTAL_INFUSION] = new Color(0.5f, 0.3f, 0.8f); // Purple
            _iconColors[EFFECT_ARCANE_SHIELD] = new Color(0.3f, 0.5f, 0.9f); // Blue
            
            // Healer
            _iconColors[EFFECT_PURIFY] = new Color(0.8f, 0.8f, 0.2f); // Yellow-green
            _iconColors[EFFECT_PURIFYING_CIRCLE] = new Color(0.9f, 0.9f, 0.5f); // Light yellow
            _iconColors[EFFECT_SANCTUARY] = new Color(0.8f, 0.8f, 1.0f); // Light blue-white
            
            // Monk
            _iconColors[EFFECT_CHI_STRIKE] = new Color(0.2f, 0.8f, 0.9f); // Cyan
            _iconColors[EFFECT_INNER_PEACE] = new Color(0.3f, 0.9f, 0.5f); // Green
            
            // === HYBRID ABILITIES ===
            // Tank Hybrids
            _iconColors["CompanionDivineBulwark"] = new Color(0.8f, 0.7f, 0.3f); // Gold-steel (Crusader)
            _iconColors["CompanionUnstoppableCharge"] = new Color(0.6f, 0.2f, 0.2f); // Dark red (Juggernaut)
            _iconColors["CompanionCounterShadow"] = new Color(0.3f, 0.3f, 0.5f); // Dark blue-grey (Shadow Guardian)
            
            // Paladin Hybrids
            _iconColors["CompanionHolyFury"] = new Color(1f, 0.5f, 0.2f); // Burning gold (Zealot)
            _iconColors["CompanionMassRestoration"] = new Color(1f, 1f, 0.8f); // Bright white-gold (High Priest)
            
            // Berserker Hybrids
            _iconColors["CompanionFrenzyStrike"] = new Color(0.5f, 0.1f, 0.2f); // Dark crimson (Reaper)
            _iconColors["CompanionBloodSacrifice"] = new Color(0.6f, 0.1f, 0.4f); // Purple-red (Blood Mage)
            _iconColors["CompanionSanguineFury"] = new Color(0.8f, 0.3f, 0.4f); // Pink-red (Blood Knight)
            
            // Rogue Hybrids
            _iconColors["CompanionManaDrain"] = new Color(0.4f, 0.3f, 0.6f); // Purple-grey (Spellthief)
            _iconColors["CompanionVanishingStrike"] = new Color(0.3f, 0.3f, 0.4f); // Dark grey-blue (Ninja)
            
            // Ranger Hybrids
            _iconColors["CompanionElementalArrow"] = new Color(0.4f, 0.5f, 0.7f); // Blue-green (Arcane Archer)
            _iconColors["CompanionAssassinsShot"] = new Color(0.3f, 0.4f, 0.3f); // Dark forest (Sniper)
            
            // Mage Hybrids
            _iconColors["CompanionInferno"] = new Color(1f, 0.4f, 0.2f); // Bright orange (Pyromancer)
            _iconColors["CompanionChainLightning"] = new Color(0.4f, 0.6f, 0.9f); // Storm blue (Storm Caller)
            
            // Healer Hybrids
            _iconColors["CompanionManaTransfusion"] = new Color(0.6f, 0.7f, 1f); // Light blue-purple (Arcane Healer)
            
            // Monk Hybrids
            _iconColors["CompanionDrunkenFrenzy"] = new Color(0.7f, 0.5f, 0.4f); // Earthy brown (Drunken Master)
            _iconColors["CompanionElementalFist"] = new Color(0.5f, 0.6f, 0.8f); // Elemental blue (Elementalist)
            
            // === MASTER ABILITIES (Level 75) ===
            _iconColors[EFFECT_UNYIELDING] = new Color(0.8f, 0.8f, 0.3f); // Gold (Tank survival)
            _iconColors[EFFECT_LAY_ON_HANDS] = new Color(1f, 1f, 0.8f); // Bright holy
            _iconColors[EFFECT_DEATH_WISH] = new Color(0.5f, 0.0f, 0.0f); // Dark red
            _iconColors[EFFECT_DEATH_MARK] = new Color(0.6f, 0.0f, 0.3f); // Dark purple-red
            _iconColors[EFFECT_RAIN_OF_ARROWS] = new Color(0.5f, 0.7f, 0.4f); // Forest green
            _iconColors[EFFECT_METEOR] = new Color(1f, 0.5f, 0.2f); // Fiery orange
            _iconColors[EFFECT_DIVINE_HYMN] = new Color(0.7f, 0.9f, 1f); // Light cyan
            _iconColors[EFFECT_CHI_EXPLOSION] = new Color(0.3f, 0.8f, 0.8f); // Cyan
            
            // === ULTIMATE ABILITIES (Level 100) ===
            _iconColors[EFFECT_IMMORTAL_STANCE] = new Color(1f, 0.85f, 0.0f); // Gold
            _iconColors[EFFECT_AVATAR_OF_LIGHT] = new Color(1f, 1f, 1f); // Pure white
            _iconColors[EFFECT_AVATAR_OF_WAR] = new Color(0.9f, 0.1f, 0.1f); // Bright red
            _iconColors[EFFECT_SHADOW_DANCE] = new Color(0.4f, 0.2f, 0.5f); // Purple
            _iconColors[EFFECT_PERFECT_SHOT] = new Color(1f, 0.9f, 0.3f); // Bright yellow
            _iconColors[EFFECT_ARCANE_FORM] = new Color(0.7f, 0.3f, 0.9f); // Bright purple
            _iconColors[EFFECT_AVATAR_OF_LIFE] = new Color(0.3f, 1f, 0.5f); // Bright green
            _iconColors[EFFECT_WAY_OF_PERFECTION] = new Color(1f, 0.8f, 0.2f); // Golden
            
            // === EXPERT ABILITIES (Level 35-50) ===
            _iconColors[EFFECT_IRON_WALL] = new Color(0.6f, 0.6f, 0.7f); // Steel
            _iconColors[EFFECT_CONSECRATION] = new Color(0.9f, 0.85f, 0.5f); // Holy gold
            _iconColors[EFFECT_DIVINE_SHIELD] = new Color(1f, 0.9f, 0.4f); // Bright gold
            _iconColors[EFFECT_EXECUTE] = new Color(0.5f, 0.1f, 0.1f); // Blood red
            _iconColors[EFFECT_RAMPAGE] = new Color(0.9f, 0.3f, 0.2f); // Orange-red
            _iconColors[EFFECT_EVASION] = new Color(0.5f, 0.5f, 0.6f); // Gray-blue
            _iconColors[EFFECT_MULTISHOT] = new Color(0.4f, 0.7f, 0.5f); // Green
            _iconColors[EFFECT_OVERCHARGE] = new Color(0.8f, 0.3f, 0.8f); // Magenta
            _iconColors[EFFECT_CHAIN_CASTING] = new Color(0.4f, 0.6f, 0.9f); // Blue
            _iconColors[EFFECT_RESURRECTION] = new Color(1f, 1f, 0.9f); // Pure white-gold
            _iconColors[EFFECT_FLURRY_OF_BLOWS] = new Color(0.9f, 0.6f, 0.3f); // Orange
            _iconColors[EFFECT_IRON_BODY] = new Color(0.5f, 0.5f, 0.55f); // Iron gray
        }
        
        /// <summary>
        /// Registers a single status effect type with ObjectDB and caches its icon.
        /// </summary>
        private static void RegisterEffect<T>(string effectName, Color iconColor) where T : CompanionStatusEffectBase
        {
            // Create and cache the icon
            var icon = CompanionStatusEffectBase.CreateFallbackIcon(iconColor);
            _iconCache[effectName] = icon;
            _iconColors[effectName] = iconColor;
            
            // Create the effect template for ObjectDB
            var effect = ScriptableObject.CreateInstance<T>();
            effect.name = effectName;
            effect.EffectIcon = icon;
            ObjectDB.instance.m_StatusEffects.Add(effect);
            
            if (VerboseLogging)
            {
                Debug.Log($"[StatusEffectManager] Registered effect: {effectName} with icon color {iconColor}");
            }
        }
        
        /// <summary>
        /// Gets the cached icon for an effect, creating it if necessary.
        /// </summary>
        public static Sprite GetEffectIcon(string effectName)
        {
            // Return cached icon if available
            if (_iconCache.TryGetValue(effectName, out Sprite cached))
            {
                return cached;
            }
            
            // Create icon from cached color if available
            if (_iconColors.TryGetValue(effectName, out Color color))
            {
                var icon = CompanionStatusEffectBase.CreateFallbackIcon(color);
                _iconCache[effectName] = icon;
                return icon;
            }
            
            // Fallback: create a gray icon
            Debug.LogWarning($"[StatusEffectManager] No icon color registered for {effectName}, using gray fallback");
            var fallback = CompanionStatusEffectBase.CreateFallbackIcon(Color.gray);
            _iconCache[effectName] = fallback;
            return fallback;
        }
        
        #region Apply Methods
        
        /// <summary>
        /// Applies invulnerability to a character.
        /// </summary>
        public static bool ApplyInvulnerable(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<InvulnerableEffect>();
            effect.name = EFFECT_INVULNERABLE;
            effect.Duration = duration;
            
            return ApplyEffect(target, effect);
        }
        
        /// <summary>
        /// Applies root (immobilize) to a character.
        /// </summary>
        public static bool ApplyRoot(Character target, float duration, Character source = null)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<RootedEffect>();
            effect.name = EFFECT_ROOTED;
            effect.Duration = duration;
            effect.SourceCharacter = source;
            
            return ApplyEffect(target, effect);
        }
        
        /// <summary>
        /// Applies slowdown to a character.
        /// </summary>
        public static bool ApplySlowdown(Character target, float duration, float speedMultiplier = 0.5f, Character source = null)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<SlowdownEffect>();
            effect.name = EFFECT_SLOWDOWN;
            effect.Duration = duration;
            effect.SpeedMultiplier = speedMultiplier;
            effect.SourceCharacter = source;
            
            return ApplyEffect(target, effect);
        }
        
        /// <summary>
        /// Applies fortify (damage reduction) to a character.
        /// </summary>
        public static bool ApplyFortify(Character target, float duration, float damageReduction = 0.7f)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<FortifyEffect>();
            effect.name = EFFECT_FORTIFY;
            effect.Duration = duration;
            effect.BaseDamageReduction = damageReduction;
            
            // Check for skill effectiveness if source has ArchetypeSkillSystem
            var skillSystem = target.GetComponent<ArchetypeSkillSystem>();
            if (skillSystem != null)
            {
                effect.SkillEffectiveness = skillSystem.GetSkillEffectiveness(ArchetypeSkillSystem.ArchetypeSkill.Tank_Fortify);
            }
            
            return ApplyEffect(target, effect);
        }
        
        /// <summary>
        /// Applies berserk rage to a character.
        /// </summary>
        public static bool ApplyBerserkRage(Character target, float duration, 
            float damageMultiplier = 1.5f, bool grantImmunity = true)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<BerserkRageEffect>();
            effect.name = EFFECT_BERSERK_RAGE;
            effect.Duration = duration;
            effect.DamageMultiplier = damageMultiplier;
            effect.GrantImmunityOnActivate = grantImmunity;
            
            // Apply skill effectiveness scaling
            var skillSystem = target.GetComponent<ArchetypeSkillSystem>();
            if (skillSystem != null)
            {
                float effectiveness = skillSystem.GetSkillEffectiveness(ArchetypeSkillSystem.ArchetypeSkill.Berserker_Rage);
                effect.DamageMultiplier = 1f + (damageMultiplier - 1f) * effectiveness;
                effect.Duration = duration * effectiveness;
            }
            
            return ApplyEffect(target, effect);
        }
        
        /// <summary>
        /// Applies caltrops effect to a character (slowdown + damage over time).
        /// </summary>
        public static bool ApplyCaltrops(Character target, float duration, Character source = null,
            float speedMultiplier = 0.4f, float damagePerTick = 2f)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<CaltropsEffect>();
            effect.name = EFFECT_CALTROPS;
            effect.Duration = duration;
            effect.SpeedMultiplier = speedMultiplier;
            effect.DamagePerTick = damagePerTick;
            effect.SourceCharacter = source;
            
            return ApplyEffect(target, effect);
        }
        
        /// <summary>
        /// Applies purify to a character (cleanses debuffs and heals).
        /// </summary>
        public static bool ApplyPurify(Character target, float duration, float healPerTick = 5f)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<PurifyEffect>();
            effect.name = EFFECT_PURIFY;
            effect.Duration = duration;
            effect.HealPerTick = healPerTick;
            
            // Apply skill effectiveness scaling
            var skillSystem = target.GetComponent<ArchetypeSkillSystem>();
            if (skillSystem != null)
            {
                float effectiveness = skillSystem.GetSkillEffectiveness(ArchetypeSkillSystem.ArchetypeSkill.Healer_Purify);
                effect.HealPerTick = healPerTick * effectiveness;
            }
            
            return ApplyEffect(target, effect);
        }
        
        /// <summary>
        /// Applies warcry buff to nearby allies (damage + fear immunity).
        /// </summary>
        public static int ApplyWarcry(Character source, float range, float duration)
        {
            return WarcryEffect.ApplyWarcryToAllies(source, range, duration);
        }
        
        /// <summary>
        /// Applies poison to a target (stacking DoT + slow).
        /// </summary>
        public static bool ApplyPoison(Character target, Character source, float duration, float damagePerTick = 4f)
        {
            return PoisonEffect.ApplyPoison(target, source, duration, damagePerTick);
        }
        
        /// <summary>
        /// Applies stealth to a character (invisibility + backstab bonus).
        /// </summary>
        public static bool ApplyStealth(Character target, float duration)
        {
            return StealthEffect.ApplyStealth(target, duration);
        }
        
        /// <summary>
        /// Applies purifying circle to allies in range (area heal + cleanse).
        /// </summary>
        public static int ApplyPurifyingCircle(Character source, float range, float duration)
        {
            return PurifyingCircleEffect.ApplyPurifyingCircle(source, range, duration);
        }
        
        /// <summary>
        /// Applies sanctuary to allies in range (damage reduction aura).
        /// </summary>
        public static int ApplySanctuary(Character source, float range, float duration)
        {
            return SanctuaryEffect.ApplySanctuary(source, range, duration);
        }
        
        /// <summary>
        /// Applies chi strike to a character (empowered attacks).
        /// </summary>
        public static bool ApplyChiStrike(Character target, float duration, int charges = 3)
        {
            return ChiStrikeEffect.ApplyChiStrike(target, duration, charges);
        }
        
        /// <summary>
        /// Applies inner peace meditation (healing aura + defense).
        /// </summary>
        public static bool ApplyInnerPeace(Character monk, float duration)
        {
            return InnerPeaceEffect.ApplyInnerPeace(monk, duration);
        }
        
        // === PALADIN ===
        
        /// <summary>
        /// Applies divine protection to allies in range (damage reduction).
        /// </summary>
        public static int ApplyDivineProtection(Character source, float range, float duration)
        {
            return DivineProtectionEffect.ApplyDivineProtection(source, range, duration);
        }
        
        /// <summary>
        /// Applies holy smite to a paladin (empowered attacks with spirit damage).
        /// </summary>
        public static bool ApplyHolySmite(Character target, float duration, int charges = 2)
        {
            return HolySmiteEffect.ApplyHolySmite(target, duration, charges);
        }
        
        // === RANGER ===
        
        /// <summary>
        /// Applies hunter's mark to an enemy (bonus damage from all sources).
        /// </summary>
        public static bool ApplyHuntersMark(Character target, Character ranger, float duration)
        {
            return HuntersMarkEffect.ApplyHuntersMark(target, ranger, duration);
        }
        
        /// <summary>
        /// Applies eagle eye to a ranger (increased crit and ranged damage).
        /// </summary>
        public static bool ApplyEagleEye(Character target, float duration)
        {
            return EagleEyeEffect.ApplyEagleEye(target, duration);
        }
        
        // === MAGE ===
        
        /// <summary>
        /// Applies elemental infusion to a mage (boosted elemental damage).
        /// </summary>
        public static bool ApplyElementalInfusion(Character target, float duration)
        {
            return ElementalInfusionEffect.ApplyElementalInfusion(target, duration);
        }
        
        /// <summary>
        /// Applies arcane shield to a character (damage absorption barrier).
        /// </summary>
        public static bool ApplyArcaneShield(Character target, float duration, float shieldHealth = 100f)
        {
            return ArcaneShieldEffect.ApplyArcaneShield(target, duration, shieldHealth);
        }
        
        /// <summary>
        /// Applies arcane shield to all allies in range.
        /// </summary>
        public static int ApplyGroupArcaneShield(Character source, float range, float duration, float shieldHealth = 50f)
        {
            return ArcaneShieldEffect.ApplyGroupArcaneShield(source, range, duration, shieldHealth);
        }
        
        #endregion
        
        #region Helper Methods
        
        /// <summary>
        /// Applies a status effect to a character, removing any existing effect of the same type.
        /// IMPORTANT: This sets the icon from the cache before applying.
        /// CRITICAL: m_icon must be set directly on the base StatusEffect for cloning to work.
        /// </summary>
        private static bool ApplyEffect(Character target, StatusEffect effect)
        {
            var seman = target.GetSEMan();
            if (seman == null) 
            {
                Debug.LogWarning($"[StatusEffectManager] Cannot apply {effect.name} - target {target.m_name} has no SEMan");
                return false;
            }
            
            // CRITICAL: Get the icon and set it on BOTH the custom property AND the base m_icon field
            // The clone copies m_icon but NOT custom C# properties
            var icon = GetEffectIcon(effect.name);
            effect.m_icon = icon;  // Set directly on base class - this gets cloned
            
            if (effect is CompanionStatusEffectBase companionEffect)
            {
                companionEffect.EffectIcon = icon;  // Also set on our property for Setup()
            }
            
            // Remove existing if any
            int existingHash = effect.name.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            // Always log when applying to player for debugging
            bool isPlayer = target.IsPlayer();
            if (isPlayer || VerboseLogging)
            {
                string targetType = isPlayer ? "PLAYER" : (target.GetComponent<CompanionController>() != null ? "COMPANION" : "NPC");
                Debug.Log($"[StatusEffectManager] Applied {effect.name} to {target.m_name} ({targetType}), hasIcon={icon != null}, duration={effect.m_ttl}s");
            }
            
            return true;
        }
        
        /// <summary>
        /// Applies a status effect to a character with explicit icon setting and logging.
        /// Use this for effects that need custom configuration.
        /// </summary>
        public static bool ApplyEffectWithIcon(Character target, CompanionStatusEffectBase effect, string effectName)
        {
            if (target == null || effect == null) return false;
            
            effect.name = effectName;
            effect.EffectIcon = GetEffectIcon(effectName);
            
            return ApplyEffect(target, effect);
        }
        
        /// <summary>
        /// Removes a status effect from a character by name.
        /// </summary>
        public static bool RemoveEffect(Character target, string effectName)
        {
            if (target == null) return false;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            int hash = effectName.GetStableHashCode();
            if (seman.HaveStatusEffect(hash))
            {
                seman.RemoveStatusEffect(hash, true);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[StatusEffectManager] Removed {effectName} from {target.m_name}");
                }
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if a character has a specific effect.
        /// </summary>
        public static bool HasEffect(Character target, string effectName)
        {
            if (target == null) return false;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            return seman.HaveStatusEffect(effectName.GetStableHashCode());
        }
        
        /// <summary>
        /// Debug method: Lists all active companion status effects on a character.
        /// </summary>
        public static string DebugListActiveEffects(Character target)
        {
            if (target == null) return "No target";
            
            var seman = target.GetSEMan();
            if (seman == null) return "No SEMan";
            
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Active effects on {target.m_name}:");
            
            var effects = seman.GetStatusEffects();
            int companionEffects = 0;
            
            foreach (var effect in effects)
            {
                bool isCompanionEffect = effect.name.StartsWith("Companion");
                if (isCompanionEffect) companionEffects++;
                
                string iconStatus = effect.m_icon != null ? "HAS ICON" : "NO ICON";
                sb.AppendLine($"  - {effect.name} (ttl: {effect.m_ttl:F1}s) [{iconStatus}] {(isCompanionEffect ? "*COMPANION*" : "")}");
            }
            
            sb.AppendLine($"Total: {effects.Count} effects ({companionEffects} companion effects)");
            return sb.ToString();
        }
        
        /// <summary>
        /// Debug method: Apply a test effect to the local player.
        /// Useful for testing that effects work correctly.
        /// </summary>
        public static bool DebugApplyTestEffect(string effectName, float duration = 10f)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Debug.LogError("[StatusEffectManager] No local player for test");
                return false;
            }
            
            // Ensure icons are initialized
            if (_iconColors.Count == 0)
            {
                InitializeIconColors();
            }
            
            Debug.Log($"[StatusEffectManager] DEBUG: Applying test effect '{effectName}' to player for {duration}s");
            
            switch (effectName)
            {
                case EFFECT_FORTIFY:
                    return ApplyFortify(player, duration);
                case EFFECT_WARCRY:
                    return ApplyWarcry(player, 0f, duration) > 0;
                case EFFECT_SANCTUARY:
                    return ApplySanctuary(player, 0f, duration) > 0;
                case EFFECT_DIVINE_PROTECTION:
                    return ApplyDivineProtection(player, 0f, duration) > 0;
                case EFFECT_BERSERK_RAGE:
                    return ApplyBerserkRage(player, duration);
                case EFFECT_PURIFY:
                    return ApplyPurify(player, duration);
                default:
                    Debug.LogWarning($"[StatusEffectManager] Unknown test effect: {effectName}");
                    return false;
            }
        }
        
        #endregion
    }
}
