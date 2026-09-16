using UnityEngine;
using System;

namespace FiresCore.Npc
{
    /// <summary>
    /// A companion's random 0-100 Luck stat, which biases every level-based scaling roll between the minimum
    /// and maximum per-level bonus. Anything that scales with level should use GetLevelScalingMultiplier.
    /// </summary>
    public class CompanionLuck : MonoBehaviour
    {
        #region Constants
        
        /// <summary>Minimum luck value</summary>
        public const int MIN_LUCK = 0;
        
        /// <summary>Maximum luck value</summary>
        public const int MAX_LUCK = 100;
        
        /// <summary>Minimum scaling per level (at 0 luck)</summary>
        public const float MIN_SCALING_PER_LEVEL = 0.0025f; // 0.25%
        
        /// <summary>Maximum scaling per level (at 100 luck)</summary>
        public const float MAX_SCALING_PER_LEVEL = 0.005f; // 0.5%
        
        /// <summary>Threshold for "Lucky" status (shows special indicator)</summary>
        public const int LUCKY_THRESHOLD = 75;
        
        /// <summary>Threshold for "Unlucky" status</summary>
        public const int UNLUCKY_THRESHOLD = 25;
        
        #endregion
        
        #region State
        
        private CompanionController _companion;
        private CompanionProgression _progression;
        private ZNetView _nview;
        
        // Core luck value (0-100)
        private int _baseLuck;
        
        // Cached scaling value for this companion
        private float _scalingPerLevel;
        
        // Tracking
        private bool _initialized;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Properties
        
        /// <summary>Base luck value (0-100)</summary>
        public int BaseLuck => _baseLuck;
        
        /// <summary>Effective luck (base + any bonuses from equipment/buffs)</summary>
        public int EffectiveLuck => CalculateEffectiveLuck();
        
        /// <summary>The scaling per level this companion uses (determined by luck)</summary>
        public float ScalingPerLevel => _scalingPerLevel;
        
        /// <summary>Whether this companion is considered "Lucky" (luck >= 75)</summary>
        public bool IsLucky => _baseLuck >= LUCKY_THRESHOLD;
        
        /// <summary>Whether this companion is considered "Unlucky" (luck <= 25)</summary>
        public bool IsUnlucky => _baseLuck <= UNLUCKY_THRESHOLD;
        
        /// <summary>
        /// Descriptive tier for the luck value.
        /// </summary>
        public string LuckTier
        {
            get
            {
                if (_baseLuck >= 90) return "Blessed";
                if (_baseLuck >= 75) return "Lucky";
                if (_baseLuck >= 60) return "Fortunate";
                if (_baseLuck >= 40) return "Average";
                if (_baseLuck >= 25) return "Unlucky";
                if (_baseLuck >= 10) return "Cursed";
                return "Doomed";
            }
        }
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _progression = GetComponent<CompanionProgression>();
            _nview = GetComponent<ZNetView>();
        }
        
        private void Start()
        {
            Initialize();
        }
        
        #endregion
        
        #region Initialization
        
        /// <summary>
        /// Tracks whether vault data has been restored.
        /// If true, we skip ZDO loading since vault has the authoritative data.
        /// </summary>
        private bool _vaultDataRestored = false;
        
        private void Initialize()
        {
            if (_initialized) return;
            
            // CRITICAL: Only load from ZDO if vault data wasn't already restored
            if (!_vaultDataRestored)
            {
                if (!LoadFromZDO())
                {
                    // No saved luck - generate new random luck
                    GenerateRandomLuck();
                }
            }
            
            // Calculate the scaling value based on luck
            CalculateScalingFromLuck();
            
            _initialized = true;
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionLuck] Initialized {_companion?.companionName}: " +
                    $"Luck={_baseLuck} ({LuckTier}), Scaling={_scalingPerLevel * 100:F3}%/level, " +
                    $"vaultRestored={_vaultDataRestored}");
            }
        }
        
        /// <summary>
        /// Generates a random luck value for a new companion.
        /// Uses a weighted distribution - extreme luck (very high or low) is rarer.
        /// </summary>
        private void GenerateRandomLuck()
        {
            // Use a bell-curve-ish distribution centered around 50
            // This makes average luck common and extreme luck rare
            float roll1 = UnityEngine.Random.value;
            float roll2 = UnityEngine.Random.value;
            float roll3 = UnityEngine.Random.value;
            
            // Average of 3 rolls creates a bell curve
            float averageRoll = (roll1 + roll2 + roll3) / 3f;
            
            _baseLuck = Mathf.RoundToInt(averageRoll * MAX_LUCK);
            _baseLuck = Mathf.Clamp(_baseLuck, MIN_LUCK, MAX_LUCK);
            
            // Save to ZDO
            SaveToZDO();
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionLuck] Generated luck {_baseLuck} for {_companion?.companionName}");
            }
        }
        
        /// <summary>
        /// Calculates the per-level scaling value based on luck.
        /// This value is cached and used for all scaling calculations.
        /// </summary>
        private void CalculateScalingFromLuck()
        {
            // Interpolate between min and max scaling based on luck
            float luckFactor = (float)_baseLuck / MAX_LUCK;
            _scalingPerLevel = Mathf.Lerp(MIN_SCALING_PER_LEVEL, MAX_SCALING_PER_LEVEL, luckFactor);
        }
        
        #endregion
        
        #region Scaling API
        
        /// <summary>
        /// Gets the level-based scaling multiplier for this companion.
        /// This is the PRIMARY method that all systems should use for level scaling.
        /// 
        /// Returns a multiplier like 1.0 (level 1) to 1.25-1.50 (level 100 depending on luck).
        /// </summary>
        /// <param name="level">The companion's current level. If -1, uses current level from progression.</param>
        /// <returns>Multiplier to apply to base values (1.0 = no bonus)</returns>
        public float GetLevelScalingMultiplier(int level = -1)
        {
            if (!_initialized)
            {
                Initialize();
            }
            
            // Get level from progression if not specified
            if (level < 0)
            {
                if (_progression == null)
                {
                    _progression = GetComponent<CompanionProgression>();
                }
                level = _progression?.Level ?? 1;
            }
            
            if (level <= 1) return 1f;
            
            // Apply scaling: base multiplier + (levels above 1) * scaling per level
            return 1f + (level - 1) * _scalingPerLevel;
        }
        
        /// <summary>
        /// Scales a base value by level and luck.
        /// Convenience method for GetLevelScalingMultiplier().
        /// </summary>
        /// <param name="baseValue">The base value to scale</param>
        /// <param name="level">The level to use (-1 for current level)</param>
        /// <returns>The scaled value</returns>
        public float ScaleByLevel(float baseValue, int level = -1)
        {
            return baseValue * GetLevelScalingMultiplier(level);
        }
        
        /// <summary>
        /// Gets the maximum possible bonus at level 100 for this companion.
        /// Useful for UI display.
        /// </summary>
        public float GetMaxBonusPercent()
        {
            // Bonus at level 100 = 99 levels * scaling per level * 100
            return 99 * _scalingPerLevel * 100f;
        }
        
        /// <summary>
        /// Gets a description of this companion's luck for UI display.
        /// </summary>
        public string GetLuckDescription()
        {
            float maxBonus = GetMaxBonusPercent();
            return $"{LuckTier} ({_baseLuck}) - Up to +{maxBonus:F1}% at max level";
        }
        
        #endregion
        
        #region Luck Modifiers
        
        /// <summary>
        /// Calculates effective luck including any bonuses.
        /// Future: Equipment, buffs, and other modifiers can affect this.
        /// </summary>
        private int CalculateEffectiveLuck()
        {
            int effective = _baseLuck;
            
            // TODO: Add equipment luck bonuses
            // TODO: Add status effect luck modifiers
            // TODO: Add territory/biome luck modifiers
            
            return Mathf.Clamp(effective, MIN_LUCK, MAX_LUCK);
        }
        
        /// <summary>
        /// Adds a permanent luck bonus (from special events, achievements, etc.)
        /// </summary>
        public void AddPermanentLuckBonus(int amount, string source)
        {
            int oldLuck = _baseLuck;
            _baseLuck = Mathf.Clamp(_baseLuck + amount, MIN_LUCK, MAX_LUCK);
            
            if (_baseLuck != oldLuck)
            {
                CalculateScalingFromLuck();
                SaveToZDO();
                
                Debug.Log($"[CompanionLuck] {_companion?.companionName} gained {amount} luck from {source}. " +
                    $"New luck: {_baseLuck}");
            }
        }
        
        #endregion
        
        #region Persistence
        
        public void SaveToZDO()
        {
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;

            zdo.Set("companion_luck", _baseLuck);
        }
        
        public bool LoadFromZDO()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return false;
            
            // Check if luck was ever saved (use -1 as sentinel)
            int savedLuck = zdo.GetInt("companion_luck", -1);
            
            if (savedLuck >= 0)
            {
                _baseLuck = Mathf.Clamp(savedLuck, MIN_LUCK, MAX_LUCK);
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Gets luck data for vault storage.
        /// </summary>
        public string GetLuckDataForVault()
        {
            return $"{_baseLuck}";
        }
        
        /// <summary>
        /// Restores luck from vault data.
        /// </summary>
        public void RestoreLuckFromVault(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                Debug.LogWarning($"[CompanionLuck] RestoreLuckFromVault called with empty data");
                return;
            }
            
            try
            {
                _baseLuck = int.Parse(data);
                _baseLuck = Mathf.Clamp(_baseLuck, MIN_LUCK, MAX_LUCK);
                
                // Mark vault data as restored
                _vaultDataRestored = true;
                
                // Recalculate scaling
                CalculateScalingFromLuck();
                
                // Save to ZDO
                SaveToZDO();
                
                Debug.Log($"[CompanionLuck] Restored luck {_baseLuck} from vault for {_companion?.companionName}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionLuck] Failed to restore from vault: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Debug
        
        /// <summary>
        /// Sets luck directly (for testing/admin commands).
        /// </summary>
        public void SetLuck(int luck)
        {
            _baseLuck = Mathf.Clamp(luck, MIN_LUCK, MAX_LUCK);
            CalculateScalingFromLuck();
            SaveToZDO();
            
            Debug.Log($"[CompanionLuck] Set luck to {_baseLuck} for {_companion?.companionName}");
        }
        
        /// <summary>
        /// Gets a summary for debugging.
        /// </summary>
        public string GetDebugSummary()
        {
            int level = _progression?.Level ?? 1;
            float currentMultiplier = GetLevelScalingMultiplier(level);
            float maxMultiplier = GetLevelScalingMultiplier(100);
            
            return $"Luck: {_baseLuck} ({LuckTier})\n" +
                   $"Scaling: {_scalingPerLevel * 100:F3}%/level\n" +
                   $"Current (L{level}): x{currentMultiplier:F3}\n" +
                   $"Max (L100): x{maxMultiplier:F3} (+{GetMaxBonusPercent():F1}%)";
        }
        
        #endregion
    }
}
