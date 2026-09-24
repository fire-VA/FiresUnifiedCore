using UnityEngine;
using System;
using System.Globalization;
using FiresCore.Classes;

namespace FiresCore.Npc
{
    /// <summary>
    /// A companion's random 0-100 Luck stat, which biases every level-based scaling roll between the minimum
    /// and maximum per-level bonus. Anything that scales with level should use GetLevelScalingMultiplier.
    /// </summary>
    public class CompanionLuck : MonoBehaviour
    {
        #region Constants
        
        /// <summary>Base-luck range. Effective luck is uncapped - see <see cref="LuckModel"/>.</summary>
        public const int MIN_LUCK = LuckModel.MinBaseLuck;
        public const int MAX_LUCK = LuckModel.MaxBaseLuck;

        /// <summary>Minimum scaling per level (at 0 luck)</summary>
        public const float MIN_SCALING_PER_LEVEL = 0.0025f; // 0.25%

        /// <summary>Maximum scaling per level (at 100 luck)</summary>
        public const float MAX_SCALING_PER_LEVEL = 0.005f; // 0.5%

        /// <summary>Above MAX_LUCK the per-level bonus keeps climbing, at half the rate.</summary>
        private const float ScalingPerLevelPerLuckAboveMax =
            (MAX_SCALING_PER_LEVEL - MIN_SCALING_PER_LEVEL) / MAX_LUCK / 2f;

        private const int DefaultMaxCompanionLevel = 100;
        private const int UnsavedLuckSentinel = -1;
        private const char VaultFieldSeparator = ';';
        private const string ZdoBaseLuckKey = "companion_luck";
        private const string ZdoPermanentBonusKey = "companion_luck_bonus";

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

        // Uncapped, saved: achievements, quests and other permanent rewards
        private float _permanentBonusLuck;

        // Uncapped, not saved: equipment, consumables, curses and other live sources
        private float _transientBonusLuck;

        // Tracking
        private bool _initialized;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Properties
        
        /// <summary>Base luck value (0-100)</summary>
        public int BaseLuck => _baseLuck;
        
        /// <summary>Effective luck: base plus every bonus, uncapped.</summary>
        public float EffectiveLuck => _baseLuck + _permanentBonusLuck + _transientBonusLuck;

        /// <summary>The scaling per level this companion uses (determined by luck)</summary>
        public float ScalingPerLevel => ScalingPerLevelFor(EffectiveLuck);

        /// <summary>Whether this companion is considered "Lucky" (luck >= 75)</summary>
        public bool IsLucky => EffectiveLuck >= LUCKY_THRESHOLD;

        /// <summary>Whether this companion is considered "Unlucky" (luck <= 25)</summary>
        public bool IsUnlucky => EffectiveLuck <= UNLUCKY_THRESHOLD;

        public string LuckTier => LuckModel.LuckTier(EffectiveLuck);
        
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
            
            _initialized = true;

            if (VerboseLogging)
            {
                Debug.Log($"[CompanionLuck] Initialized {_companion?.companionName}: " +
                    $"Luck={_baseLuck} ({LuckTier}), Scaling={ScalingPerLevel * 100:F3}%/level, " +
                    $"vaultRestored={_vaultDataRestored}");
            }
        }

        private void GenerateRandomLuck()
        {
            _baseLuck = LuckModel.RollBaseLuck();
            SaveToZDO();

            if (VerboseLogging)
            {
                Debug.Log($"[CompanionLuck] Generated luck {_baseLuck} for {_companion?.companionName}");
            }
        }

        /// <summary>
        /// Per-level bonus for a luck value: interpolated across the base range, then continuing above
        /// it at half the rate so effective luck past 100 still pays.
        /// </summary>
        private static float ScalingPerLevelFor(float effectiveLuck)
        {
            if (effectiveLuck <= MAX_LUCK)
                return Mathf.Lerp(MIN_SCALING_PER_LEVEL, MAX_SCALING_PER_LEVEL,
                    Mathf.Clamp01(effectiveLuck / MAX_LUCK));

            return MAX_SCALING_PER_LEVEL + (effectiveLuck - MAX_LUCK) * ScalingPerLevelPerLuckAboveMax;
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
            return 1f + (level - 1) * ScalingPerLevel;
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
            int maxLevel = _progression != null ? _progression.maxLevel : DefaultMaxCompanionLevel;
            return (maxLevel - 1) * ScalingPerLevel * 100f;
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
        /// Adds a permanent luck bonus. It raises base luck while there is room, and the remainder
        /// becomes an uncapped bonus, so a reward is never silently swallowed at base 100.
        /// </summary>
        public void AddPermanentLuckBonus(int amount, string source)
        {
            int previousBase = _baseLuck;
            int raisedBase = LuckModel.ClampBase(_baseLuck + amount);
            _baseLuck = raisedBase;
            _permanentBonusLuck += amount - (raisedBase - previousBase);

            SaveToZDO();

            Debug.Log($"[CompanionLuck] {_companion?.companionName} gained {amount} luck from {source}. " +
                $"New luck: {EffectiveLuck:F0} (base {_baseLuck}, permanent bonus {_permanentBonusLuck:F0})");
        }

        /// <summary>Replaces the live bonus from equipment, consumables and curses. Never saved.</summary>
        public void SetTransientLuckBonus(float bonusLuck)
        {
            _transientBonusLuck = bonusLuck;
        }
        
        #endregion
        
        #region Persistence
        
        public void SaveToZDO()
        {
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;

            zdo.Set(ZdoBaseLuckKey, _baseLuck);
            zdo.Set(ZdoPermanentBonusKey, _permanentBonusLuck);
        }
        
        public bool LoadFromZDO()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return false;
            
            // Check if luck was ever saved (use -1 as sentinel)
            int savedLuck = zdo.GetInt(ZdoBaseLuckKey, UnsavedLuckSentinel);
            
            if (savedLuck > UnsavedLuckSentinel)
            {
                _baseLuck = LuckModel.ClampBase(savedLuck);
                _permanentBonusLuck = zdo.GetFloat(ZdoPermanentBonusKey, 0f);
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Gets luck data for vault storage.
        /// </summary>
        public string GetLuckDataForVault()
        {
            return FormattableString.Invariant($"{_baseLuck}{VaultFieldSeparator}{_permanentBonusLuck}");
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
                string[] fields = data.Split(VaultFieldSeparator);
                _baseLuck = LuckModel.ClampBase(int.Parse(fields[0], CultureInfo.InvariantCulture));
                _permanentBonusLuck = fields.Length > 1
                    ? float.Parse(fields[1], CultureInfo.InvariantCulture)
                    : 0f;

                _vaultDataRestored = true;
                SaveToZDO();

                Debug.Log($"[CompanionLuck] Restored luck {EffectiveLuck:F0} from vault for {_companion?.companionName}");
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
            _baseLuck = LuckModel.ClampBase(luck);
            
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
                   $"Scaling: {ScalingPerLevel * 100:F3}%/level\n" +
                   $"Current (L{level}): x{currentMultiplier:F3}\n" +
                   $"Max (L100): x{maxMultiplier:F3} (+{GetMaxBonusPercent():F1}%)";
        }
        
        #endregion
    }
}
