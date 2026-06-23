using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Archetypes.StatusEffects;
using FiresCore.Npc.Archetypes.StatusEffects.Tank;
using FiresCore.Npc.Archetypes.StatusEffects.Healer;

namespace FiresCore.Npc
{
    /// <summary>
    /// Centralized stat calculation for companion NPCs.
    /// All UI screens and systems should use this class to get consistent stat values.
    /// 
    /// This class aggregates stats from:
    /// - Base character stats (prefab configured)
    /// - Attribute bonuses (from CompanionProgression)
    /// - Food bonuses (from CompanionConsumables)
    /// - Equipment bonuses (from CompanionInventory)
    /// - Status effect bonuses (from active effects like Fortify, Sanctuary, etc.)
    /// 
    /// USAGE:
    /// var calc = new CompanionStatCalculator(companion);
    /// float maxHealth = calc.MaxHealth;
    /// float currentStamina = calc.CurrentStamina;
    /// </summary>
    public class CompanionStatCalculator
    {
        #region Fields
        
        private readonly CompanionController _companion;
        private readonly CompanionStats _stats;
        private readonly CompanionProgression _progression;
        private readonly CompanionConsumables _consumables;
        private readonly CompanionInventory _inventory;
        private readonly Character _character;
        
        #endregion
        
        #region Constructor
        
        public CompanionStatCalculator(CompanionController companion)
        {
            _companion = companion;
            
            if (companion != null)
            {
                _stats = companion.GetStats();
                _progression = companion.GetProgression();
                _consumables = companion.GetComponent<CompanionConsumables>();
                _inventory = companion.GetInventory();
                _character = companion.GetCharacter();
            }
        }
        
        /// <summary>
        /// Creates a calculator from a GameObject (convenience method for components).
        /// </summary>
        public CompanionStatCalculator(GameObject gameObject)
            : this(gameObject?.GetComponent<CompanionController>())
        {
        }
        
        /// <summary>
        /// Creates a calculator from a MonoBehaviour (convenience method for components).
        /// </summary>
        public CompanionStatCalculator(MonoBehaviour component)
            : this(component?.GetComponent<CompanionController>())
        {
        }
        
        #endregion
        
        #region Base Stats
        
        /// <summary>
        /// Base max health from the character prefab (before any bonuses).
        /// </summary>
        public float BaseMaxHealth
        {
            get
            {
                if (_stats != null)
                    return _stats.baseMaxHealth;
                if (_consumables != null)
                    return _consumables.GetBaseMaxHealth();
                if (_character != null)
                    return _character.GetMaxHealth();
                return 100f;
            }
        }
        
        /// <summary>
        /// Base max stamina (before any bonuses).
        /// </summary>
        public float BaseMaxStamina
        {
            get
            {
                if (_stats != null)
                    return _stats.baseMaxStamina;
                return 100f;
            }
        }
        
        /// <summary>
        /// Base max eitr (before any bonuses).
        /// </summary>
        public float BaseMaxEitr
        {
            get
            {
                if (_stats != null)
                    return _stats.baseMaxEitr;
                return 100f;
            }
        }
        
        #endregion
        
        #region Attribute Bonuses
        
        /// <summary>
        /// Health bonus from Health attribute points.
        /// </summary>
        public float AttributeHealthBonus
        {
            get
            {
                if (_progression == null) return 0f;
                int healthPoints = _progression.GetAttributeValue(CompanionProgression.AttributeType.Health);
                return healthPoints * _progression.healthBonusPerPoint;
            }
        }
        
        /// <summary>
        /// Stamina bonus from Endurance attribute points.
        /// </summary>
        public float AttributeStaminaBonus
        {
            get
            {
                if (_progression == null) return 0f;
                int endurancePoints = _progression.GetAttributeValue(CompanionProgression.AttributeType.Endurance);
                return endurancePoints * _progression.staminaBonusPerPoint;
            }
        }
        
        /// <summary>
        /// Eitr bonus from Intelligence attribute points.
        /// </summary>
        public float AttributeEitrBonus
        {
            get
            {
                if (_progression == null) return 0f;
                int intelligencePoints = _progression.GetAttributeValue(CompanionProgression.AttributeType.Intelligence);
                return intelligencePoints * _progression.eitrBonusPerPoint;
            }
        }
        
        /// <summary>
        /// Damage bonus multiplier from Strength attribute (1.0 = no bonus).
        /// </summary>
        public float AttributeDamageMultiplier
        {
            get
            {
                if (_progression == null) return 1f;
                return _progression.GetMeleeDamageMultiplier();
            }
        }
        
        /// <summary>
        /// Armor bonus from equipment (no Toughness attribute exists).
        /// </summary>
        public float AttributeArmorBonus
        {
            get
            {
                // Note: There is no Toughness attribute in the current implementation
                // This returns 0 - armor comes from equipment only
                return 0f;
            }
        }
        
        #endregion
        
        #region Food Bonuses
        
        /// <summary>
        /// Health bonus from active food effects.
        /// </summary>
        public float FoodHealthBonus => _consumables?.GetFoodHealthBonus() ?? 0f;
        
        /// <summary>
        /// Stamina bonus from active food effects.
        /// </summary>
        public float FoodStaminaBonus => _consumables?.GetFoodStaminaBonus() ?? 0f;
        
        /// <summary>
        /// Eitr bonus from active food effects.
        /// </summary>
        public float FoodEitrBonus => _consumables?.GetFoodEitrBonus() ?? 0f;
        
        /// <summary>
        /// Health regeneration per second from food.
        /// </summary>
        public float FoodHealthRegen => _consumables?.GetFoodHealthRegen() ?? 0f;
        
        /// <summary>
        /// Number of active food effects.
        /// </summary>
        public int ActiveFoodCount => _consumables?.GetActiveFoodCount() ?? 0;
        
        #endregion
        
        #region Equipment Bonuses
        
        /// <summary>
        /// Total armor from equipped items.
        /// </summary>
        public float EquipmentArmor
        {
            get
            {
                // Get armor from CompanionEquipmentData which tracks equipment stats
                var equipData = _companion?.GetComponent<CompanionEquipmentData>();
                return equipData?.TotalArmor ?? 0f;
            }
        }
        
        #endregion
        
        #region Status Effect Bonuses
        
        /// <summary>
        /// Armor bonus from active status effects (Fortify, etc.).
        /// </summary>
        public float StatusEffectArmorBonus
        {
            get
            {
                if (_character == null) return 0f;
                
                var seman = _character.GetSEMan();
                if (seman == null) return 0f;
                
                float bonus = 0f;
                
                // Check for Fortify effect
                var fortify = seman.GetStatusEffect(StatusEffectManager.EFFECT_FORTIFY.GetStableHashCode()) as FortifyEffect;
                if (fortify != null)
                {
                    bonus += fortify.BonusArmor;
                }
                
                return bonus;
            }
        }
        
        /// <summary>
        /// Damage reduction multiplier from active status effects (1.0 = no reduction).
        /// </summary>
        public float StatusEffectDamageReduction
        {
            get
            {
                if (_character == null) return 1f;
                
                var seman = _character.GetSEMan();
                if (seman == null) return 1f;
                
                float multiplier = 1f;
                
                // Check for Fortify effect
                var fortify = seman.GetStatusEffect(StatusEffectManager.EFFECT_FORTIFY.GetStableHashCode()) as FortifyEffect;
                if (fortify != null)
                {
                    multiplier *= fortify.DamageReduction;
                }
                
                // Check for Sanctuary effect (uses DefenseMultiplier from BuffEffect base class)
                var sanctuary = seman.GetStatusEffect(StatusEffectManager.EFFECT_SANCTUARY.GetStableHashCode()) as SanctuaryEffect;
                if (sanctuary != null)
                {
                    multiplier *= sanctuary.DefenseMultiplier;
                }
                
                // Check for Divine Protection effect
                var divineProtection = seman.GetStatusEffect(StatusEffectManager.EFFECT_DIVINE_PROTECTION.GetStableHashCode());
                if (divineProtection != null)
                {
                    // Divine Protection gives 20% damage reduction
                    multiplier *= 0.8f;
                }
                
                return multiplier;
            }
        }
        
        /// <summary>
        /// Attack damage multiplier from active status effects (1.0 = no bonus).
        /// </summary>
        public float StatusEffectDamageBonus
        {
            get
            {
                if (_character == null) return 1f;
                
                var seman = _character.GetSEMan();
                if (seman == null) return 1f;
                
                float multiplier = 1f;
                
                // Check for Berserk Rage effect
                var berserk = seman.GetStatusEffect(StatusEffectManager.EFFECT_BERSERK_RAGE.GetStableHashCode());
                if (berserk != null)
                {
                    // Berserk gives 50% damage bonus
                    multiplier *= 1.5f;
                }
                
                // Check for Warcry effect
                var warcry = seman.GetStatusEffect(StatusEffectManager.EFFECT_WARCRY.GetStableHashCode());
                if (warcry != null)
                {
                    // Warcry gives 15% damage bonus
                    multiplier *= 1.15f;
                }
                
                // Check for Elemental Infusion effect
                var elementalInfusion = seman.GetStatusEffect(StatusEffectManager.EFFECT_ELEMENTAL_INFUSION.GetStableHashCode());
                if (elementalInfusion != null)
                {
                    // Elemental Infusion gives 25% magic damage bonus
                    multiplier *= 1.25f;
                }
                
                // Check for Chi Strike effect
                var chiStrike = seman.GetStatusEffect(StatusEffectManager.EFFECT_CHI_STRIKE.GetStableHashCode());
                if (chiStrike != null)
                {
                    // Chi Strike gives 30% unarmed damage bonus
                    multiplier *= 1.3f;
                }
                
                // Check for Holy Smite effect
                var holySmite = seman.GetStatusEffect(StatusEffectManager.EFFECT_HOLY_SMITE.GetStableHashCode());
                if (holySmite != null)
                {
                    // Holy Smite gives 20% spirit damage bonus
                    multiplier *= 1.2f;
                }
                
                return multiplier;
            }
        }
        
        /// <summary>
        /// Health regeneration bonus from active status effects (per second).
        /// </summary>
        public float StatusEffectHealthRegen
        {
            get
            {
                if (_character == null) return 0f;
                
                var seman = _character.GetSEMan();
                if (seman == null) return 0f;
                
                float regen = 0f;
                
                // Check for Sanctuary effect
                var sanctuary = seman.GetStatusEffect(StatusEffectManager.EFFECT_SANCTUARY.GetStableHashCode()) as SanctuaryEffect;
                if (sanctuary != null)
                {
                    regen += sanctuary.HealthRegenBonus;
                }
                
                // Check for Inner Peace effect
                var innerPeace = seman.GetStatusEffect(StatusEffectManager.EFFECT_INNER_PEACE.GetStableHashCode());
                if (innerPeace != null)
                {
                    // Inner Peace gives 5 HP/s regen
                    regen += 5f;
                }
                
                return regen;
            }
        }
        
        /// <summary>
        /// Returns a list of active companion status effects with their remaining durations and descriptions.
        /// Used for UI display.
        /// </summary>
        public List<(string name, float duration, Color color, string description)> GetActiveStatusEffects()
        {
            var effects = new List<(string name, float duration, Color color, string description)>();
            
            if (_character == null) return effects;
            
            var seman = _character.GetSEMan();
            if (seman == null) return effects;
            
            foreach (var effect in seman.GetStatusEffects())
            {
                if (effect == null) continue;
                
                // Check if it's a companion effect
                if (IsCompanionEffect(effect.name))
                {
                    float remaining = effect.GetRemaningTime();
                    Color effectColor = GetEffectColor(effect.name);
                    string displayName = GetEffectDisplayName(effect.name);
                    string description = GetEffectDescription(effect.name);
                    
                    effects.Add((displayName, remaining, effectColor, description));
                }
            }
            
            return effects;
        }
        
        private bool IsCompanionEffect(string effectName)
        {
            if (string.IsNullOrEmpty(effectName)) return false;
            
            return effectName.StartsWith("Companion") ||
                   effectName == StatusEffectManager.EFFECT_FORTIFY ||
                   effectName == StatusEffectManager.EFFECT_DIVINE_PROTECTION ||
                   effectName == StatusEffectManager.EFFECT_HOLY_SMITE ||
                   effectName == StatusEffectManager.EFFECT_BERSERK_RAGE ||
                   effectName == StatusEffectManager.EFFECT_WARCRY ||
                   effectName == StatusEffectManager.EFFECT_STEALTH ||
                   effectName == StatusEffectManager.EFFECT_HUNTERS_MARK ||
                   effectName == StatusEffectManager.EFFECT_EAGLE_EYE ||
                   effectName == StatusEffectManager.EFFECT_ELEMENTAL_INFUSION ||
                   effectName == StatusEffectManager.EFFECT_ARCANE_SHIELD ||
                   effectName == StatusEffectManager.EFFECT_PURIFY ||
                   effectName == StatusEffectManager.EFFECT_SANCTUARY ||
                   effectName == StatusEffectManager.EFFECT_PURIFYING_CIRCLE ||
                   effectName == StatusEffectManager.EFFECT_CHI_STRIKE ||
                   effectName == StatusEffectManager.EFFECT_INNER_PEACE;
        }
        
        private Color GetEffectColor(string effectName)
        {
            switch (effectName)
            {
                case StatusEffectManager.EFFECT_FORTIFY: return new Color(0.3f, 0.3f, 0.8f);
                case StatusEffectManager.EFFECT_DIVINE_PROTECTION: return new Color(1f, 0.85f, 0.4f);
                case StatusEffectManager.EFFECT_HOLY_SMITE: return new Color(1f, 1f, 0.6f);
                case StatusEffectManager.EFFECT_BERSERK_RAGE: return new Color(0.8f, 0.2f, 0.2f);
                case StatusEffectManager.EFFECT_WARCRY: return new Color(0.9f, 0.3f, 0.1f);
                case StatusEffectManager.EFFECT_STEALTH: return new Color(0.3f, 0.3f, 0.4f);
                case StatusEffectManager.EFFECT_HUNTERS_MARK: return new Color(0.8f, 0.2f, 0.2f);
                case StatusEffectManager.EFFECT_EAGLE_EYE: return new Color(0.4f, 0.7f, 0.3f);
                case StatusEffectManager.EFFECT_ELEMENTAL_INFUSION: return new Color(0.5f, 0.3f, 0.8f);
                case StatusEffectManager.EFFECT_ARCANE_SHIELD: return new Color(0.3f, 0.5f, 0.9f);
                case StatusEffectManager.EFFECT_PURIFY: return new Color(0.8f, 0.8f, 0.2f);
                case StatusEffectManager.EFFECT_SANCTUARY: return new Color(0.8f, 0.8f, 1f);
                case StatusEffectManager.EFFECT_PURIFYING_CIRCLE: return new Color(0.9f, 0.9f, 0.5f);
                case StatusEffectManager.EFFECT_CHI_STRIKE: return new Color(0.2f, 0.8f, 0.9f);
                case StatusEffectManager.EFFECT_INNER_PEACE: return new Color(0.3f, 0.9f, 0.5f);
                case "CompanionTaunting": return new Color(1f, 0.6f, 0.2f);
                default: return Color.white;
            }
        }
        
        private string GetEffectDisplayName(string effectName)
        {
            // Remove "Companion" prefix if present
            if (effectName.StartsWith("Companion"))
            {
                effectName = effectName.Substring(9);
            }
            
            // Add spaces before capital letters
            var result = new System.Text.StringBuilder();
            foreach (char c in effectName)
            {
                if (char.IsUpper(c) && result.Length > 0)
                {
                    result.Append(' ');
                }
                result.Append(c);
            }
            
            return result.ToString();
        }
        
        /// <summary>
        /// Gets a short description of what the effect does.
        /// </summary>
        private string GetEffectDescription(string effectName)
        {
            switch (effectName)
            {
                // Common
                case StatusEffectManager.EFFECT_INVULNERABLE: return "Immune to all damage";
                case StatusEffectManager.EFFECT_ROOTED: return "Cannot move";
                case StatusEffectManager.EFFECT_SLOWDOWN: return "Movement speed reduced";
                
                // Tank
                case StatusEffectManager.EFFECT_FORTIFY: return "-30% damage taken, +armor";
                
                // Paladin
                case StatusEffectManager.EFFECT_DIVINE_PROTECTION: return "-20% damage taken for group";
                case StatusEffectManager.EFFECT_HOLY_SMITE: return "+20% spirit damage on attacks";
                
                // Berserker
                case StatusEffectManager.EFFECT_BERSERK_RAGE: return "+50% damage, brief immunity";
                case StatusEffectManager.EFFECT_WARCRY: return "+15% damage, fear immunity";
                
                // Rogue
                case StatusEffectManager.EFFECT_CALTROPS: return "Slowed + damage over time";
                case StatusEffectManager.EFFECT_POISON: return "Poison damage over time";
                case StatusEffectManager.EFFECT_STEALTH: return "Invisible, +crit chance";
                
                // Ranger
                case StatusEffectManager.EFFECT_HUNTERS_MARK: return "Target takes +25% damage";
                case StatusEffectManager.EFFECT_EAGLE_EYE: return "+crit chance, +ranged damage";
                
                // Mage
                case StatusEffectManager.EFFECT_ELEMENTAL_INFUSION: return "+25% elemental damage";
                case StatusEffectManager.EFFECT_ARCANE_SHIELD: return "Damage absorption barrier";
                
                // Healer
                case StatusEffectManager.EFFECT_PURIFY: return "Cleanses debuffs, heals";
                case StatusEffectManager.EFFECT_PURIFYING_CIRCLE: return "Area heal + cleanse";
                case StatusEffectManager.EFFECT_SANCTUARY: return "-15% damage, +HP regen";
                
                // Monk
                case StatusEffectManager.EFFECT_CHI_STRIKE: return "+30% unarmed damage";
                case StatusEffectManager.EFFECT_INNER_PEACE: return "Meditation aura, +5 HP/s";
                
                // Expert
                case StatusEffectManager.EFFECT_IRON_WALL: return "Cannot be staggered";
                case StatusEffectManager.EFFECT_CONSECRATION: return "Holy ground damages undead";
                case StatusEffectManager.EFFECT_DIVINE_SHIELD: return "Absorbs next hit";
                case StatusEffectManager.EFFECT_EXECUTE: return "Massive damage to low HP targets";
                case StatusEffectManager.EFFECT_RAMPAGE: return "+damage per kill";
                case StatusEffectManager.EFFECT_EVASION: return "+50% dodge chance";
                case StatusEffectManager.EFFECT_MULTISHOT: return "Attacks hit multiple targets";
                case StatusEffectManager.EFFECT_OVERCHARGE: return "+50% magic damage, +eitr cost";
                case StatusEffectManager.EFFECT_CHAIN_CASTING: return "No cooldown on spells";
                case StatusEffectManager.EFFECT_RESURRECTION: return "Can revive fallen allies";
                case StatusEffectManager.EFFECT_FLURRY_OF_BLOWS: return "+75% attack speed";
                case StatusEffectManager.EFFECT_IRON_BODY: return "+50% armor, immune to knockback";
                
                // Master
                case StatusEffectManager.EFFECT_UNYIELDING: return "Cannot die for duration";
                case StatusEffectManager.EFFECT_LAY_ON_HANDS: return "Full heal + cleanse";
                case StatusEffectManager.EFFECT_DEATH_WISH: return "+100% damage at low HP";
                case StatusEffectManager.EFFECT_DEATH_MARK: return "Guaranteed crit on target";
                case StatusEffectManager.EFFECT_RAIN_OF_ARROWS: return "Arrows rain from sky";
                case StatusEffectManager.EFFECT_METEOR: return "Devastating AoE fire damage";
                case StatusEffectManager.EFFECT_DIVINE_HYMN: return "Group heal over time";
                case StatusEffectManager.EFFECT_CHI_EXPLOSION: return "AoE damage around self";
                
                // Ultimate
                case StatusEffectManager.EFFECT_IMMORTAL_STANCE: return "Invulnerable + reflect damage";
                case StatusEffectManager.EFFECT_AVATAR_OF_LIGHT: return "Ultimate holy form";
                case StatusEffectManager.EFFECT_AVATAR_OF_WAR: return "Ultimate rage form";
                case StatusEffectManager.EFFECT_SHADOW_DANCE: return "Ultimate stealth + damage";
                case StatusEffectManager.EFFECT_PERFECT_SHOT: return "Guaranteed crit, max damage";
                case StatusEffectManager.EFFECT_ARCANE_FORM: return "Ultimate magic form";
                case StatusEffectManager.EFFECT_AVATAR_OF_LIFE: return "Ultimate healing form";
                case StatusEffectManager.EFFECT_WAY_OF_PERFECTION: return "Ultimate martial arts form";
                
                case "CompanionTaunting": return "Forcing enemies to attack";
                
                default: return "";
            }
        }
        
        #endregion
        
        #region Final Calculated Stats
        
        /// <summary>
        /// Maximum health including all bonuses (scale/biome multiplier + attributes + food).
        /// This is the value that should be displayed in UI.
        /// 
        /// CRITICAL: Uses CompanionStats.MaxHealth when available because it correctly applies
        /// the scale/biome multiplier. The calculator's own BaseMaxHealth calculation doesn't
        /// include these multipliers which caused display issues (420/380 bug).
        /// </summary>
        public float MaxHealth
        {
            get
            {
                // PREFER CompanionStats.MaxHealth - it includes the scale/biome multiplier
                // which we don't have access to here without duplicating complex logic
                if (_stats != null)
                    return _stats.MaxHealth;
                
                // Fallback to simple calculation (won't include scale/biome multiplier)
                return BaseMaxHealth + AttributeHealthBonus + FoodHealthBonus;
            }
        }
        
        /// <summary>
        /// Maximum stamina including all bonuses.
        /// Uses CompanionStats.MaxStamina when available as it includes archetype multipliers.
        /// </summary>
        public float MaxStamina
        {
            get
            {
                // PREFER CompanionStats.MaxStamina - it includes archetype multipliers
                if (_stats != null)
                    return _stats.MaxStamina;
                
                // Fallback
                return BaseMaxStamina + AttributeStaminaBonus + FoodStaminaBonus;
            }
        }
        
        /// <summary>
        /// Maximum eitr including all bonuses.
        /// Uses CompanionStats.MaxEitr when available.
        /// </summary>
        public float MaxEitr
        {
            get
            {
                // PREFER CompanionStats.MaxEitr for consistency
                if (_stats != null)
                    return _stats.MaxEitr;
                
                // Fallback
                return BaseMaxEitr + AttributeEitrBonus + FoodEitrBonus;
            }
        }
        
        /// <summary>
        /// Total armor including equipment, attributes, and status effect bonuses.
        /// </summary>
        public float TotalArmor => EquipmentArmor + AttributeArmorBonus + StatusEffectArmorBonus;
        
        /// <summary>
        /// Total health regeneration per second (from food and status effects).
        /// </summary>
        public float TotalHealthRegen => FoodHealthRegen + StatusEffectHealthRegen;
        
        /// <summary>
        /// Effective damage reduction multiplier from all sources (lower = more reduction).
        /// Includes status effects like Fortify, Sanctuary, etc.
        /// </summary>
        public float EffectiveDamageReduction => StatusEffectDamageReduction;
        
        /// <summary>
        /// Effective damage multiplier from all sources (higher = more damage).
        /// Includes attributes and status effects like Berserk, Warcry, etc.
        /// </summary>
        public float EffectiveDamageMultiplier => AttributeDamageMultiplier * StatusEffectDamageBonus;
        
        #endregion
        
        #region Current Values
        
        /// <summary>
        /// Current health from CompanionStats (our tracked value, not capped by Character).
        /// </summary>
        public float CurrentHealth => _stats?.CurrentHealth ?? (_character?.GetHealth() ?? 0f);
        
        /// <summary>
        /// Current stamina from CompanionStats.
        /// </summary>
        public float CurrentStamina => _stats?.CurrentStamina ?? MaxStamina;
        
        /// <summary>
        /// Current eitr from CompanionStats.
        /// </summary>
        public float CurrentEitr => _stats?.CurrentEitr ?? MaxEitr;
        
        #endregion
        
        #region Percentages
        
        /// <summary>
        /// Health as a percentage (0-1).
        /// </summary>
        public float HealthPercentage
        {
            get
            {
                float max = MaxHealth;
                return max > 0 ? Mathf.Clamp01(CurrentHealth / max) : 0f;
            }
        }
        
        /// <summary>
        /// Stamina as a percentage (0-1).
        /// </summary>
        public float StaminaPercentage
        {
            get
            {
                float max = MaxStamina;
                return max > 0 ? Mathf.Clamp01(CurrentStamina / max) : 0f;
            }
        }
        
        /// <summary>
        /// Eitr as a percentage (0-1).
        /// </summary>
        public float EitrPercentage
        {
            get
            {
                float max = MaxEitr;
                return max > 0 ? Mathf.Clamp01(CurrentEitr / max) : 0f;
            }
        }
        
        #endregion
        
        #region Progression Info
        
        /// <summary>
        /// Current companion level.
        /// </summary>
        public int Level => _progression?.Level ?? 0;
        
        /// <summary>
        /// Current XP.
        /// </summary>
        public float CurrentXP => _progression?.CurrentXp ?? 0f;
        
        /// <summary>
        /// XP needed for next level.
        /// </summary>
        public float XPToNextLevel => _progression?.XpForNextLevel ?? 100f;
        
        /// <summary>
        /// Available attribute points.
        /// </summary>
        public int AvailableAttributePoints => _progression?.UnspentAttributePoints ?? 0;
        
        /// <summary>
        /// Gets the value of a specific attribute.
        /// </summary>
        public int GetAttributeValue(CompanionProgression.AttributeType type)
        {
            return _progression?.GetAttributeValue(type) ?? 0;
        }
        
        #endregion
        
        #region Stat Breakdown (for UI display)
        
        /// <summary>
        /// Gets a formatted breakdown of health bonuses for UI display.
        /// </summary>
        public string GetHealthBreakdown()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Base: {BaseMaxHealth:F0}");
            
            if (AttributeHealthBonus > 0)
                sb.AppendLine($"Attributes: +{AttributeHealthBonus:F0}");
            
            if (FoodHealthBonus > 0)
                sb.AppendLine($"Food: +{FoodHealthBonus:F0}");
            
            sb.AppendLine($"<color=yellow>Total: {MaxHealth:F0}</color>");
            
            return sb.ToString();
        }
        
        /// <summary>
        /// Gets a formatted breakdown of stamina bonuses for UI display.
        /// </summary>
        public string GetStaminaBreakdown()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Base: {BaseMaxStamina:F0}");
            
            if (AttributeStaminaBonus > 0)
                sb.AppendLine($"Attributes: +{AttributeStaminaBonus:F0}");
            
            if (FoodStaminaBonus > 0)
                sb.AppendLine($"Food: +{FoodStaminaBonus:F0}");
            
            sb.AppendLine($"<color=yellow>Total: {MaxStamina:F0}</color>");
            
            return sb.ToString();
        }
        
        /// <summary>
        /// Gets a formatted breakdown of eitr bonuses for UI display.
        /// </summary>
        public string GetEitrBreakdown()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Base: {BaseMaxEitr:F0}");
            
            if (AttributeEitrBonus > 0)
                sb.AppendLine($"Attributes: +{AttributeEitrBonus:F0}");
            
            if (FoodEitrBonus > 0)
                sb.AppendLine($"Food: +{FoodEitrBonus:F0}");
            
            sb.AppendLine($"<color=yellow>Total: {MaxEitr:F0}</color>");
            
            return sb.ToString();
        }
        
        /// <summary>
        /// Gets a formatted breakdown of armor for UI display.
        /// </summary>
        public string GetArmorBreakdown()
        {
            var sb = new System.Text.StringBuilder();
            
            if (EquipmentArmor > 0)
                sb.AppendLine($"Equipment: {EquipmentArmor:F0}");
            
            if (AttributeArmorBonus > 0)
                sb.AppendLine($"Toughness: +{AttributeArmorBonus:F0}");
            
            if (StatusEffectArmorBonus > 0)
                sb.AppendLine($"<color=#66AAFF>Buffs: +{StatusEffectArmorBonus:F0}</color>");
            
            sb.AppendLine($"<color=yellow>Total: {TotalArmor:F0}</color>");
            
            // Show damage reduction if active
            float damageReduction = StatusEffectDamageReduction;
            if (damageReduction < 1f)
            {
                float reductionPercent = (1f - damageReduction) * 100f;
                sb.AppendLine($"<color=#66FF66>Damage Reduction: {reductionPercent:F0}%</color>");
            }
            
            return sb.ToString();
        }
        
        /// <summary>
        /// Gets a formatted breakdown of damage modifiers for UI display.
        /// </summary>
        public string GetDamageBreakdown()
        {
            var sb = new System.Text.StringBuilder();
            
            float attrMultiplier = AttributeDamageMultiplier;
            float effectMultiplier = StatusEffectDamageBonus;
            float totalMultiplier = EffectiveDamageMultiplier;
            
            if (attrMultiplier != 1f)
                sb.AppendLine($"Strength: x{attrMultiplier:F2}");
            
            if (effectMultiplier != 1f)
                sb.AppendLine($"<color=#FF8866>Buffs: x{effectMultiplier:F2}</color>");
            
            if (totalMultiplier != 1f)
                sb.AppendLine($"<color=yellow>Total: x{totalMultiplier:F2}</color>");
            
            return sb.ToString();
        }
        
        /// <summary>
        /// Gets a formatted list of active status effects for UI display.
        /// </summary>
        public string GetStatusEffectsBreakdown()
        {
            var effects = GetActiveStatusEffects();
            if (effects.Count == 0)
                return "No active buffs.";
            
            var sb = new System.Text.StringBuilder();
            foreach (var effect in effects)
            {
                string colorHex = ColorUtility.ToHtmlStringRGB(effect.color);
                sb.AppendLine($"<color=#{colorHex}>{effect.name}</color>: {effect.duration:F0}s");
            }
            
            return sb.ToString();
        }
        
        #endregion
        
        #region Validation
        
        /// <summary>
        /// Returns true if the calculator has valid companion data.
        /// </summary>
        public bool IsValid => _companion != null;
        
        /// <summary>
        /// Forces a recalculation of all stats in CompanionStats.
        /// Call this after attribute or equipment changes.
        /// </summary>
        public void ForceRecalculate()
        {
            _stats?.RecalculateMaxStats();
        }
        
        #endregion
    }
}
