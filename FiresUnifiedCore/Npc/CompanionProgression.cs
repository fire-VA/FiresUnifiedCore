using UnityEngine;
using System;
using System.Collections.Generic;

namespace FiresCore.Npc
{
    /// <summary>
    /// Manages companion progression: Experience, Levels (0-100), and Attributes.
    /// 
    /// LEVELING:
    /// - Companions gain XP from kills, skill level-ups, and other activities
    /// - Each level requires progressively more XP
    /// - Level cap is 100
    /// 
    /// ATTRIBUTES (1 point per level):
    /// - Strength: Increases melee damage
    /// - Speed: Increases movement speed
    /// - Health: Increases maximum health
    /// - Endurance: Increases maximum stamina
    /// - Intelligence: Increases maximum eitr and magic damage
    /// </summary>
    public class CompanionProgression : MonoBehaviour
    {
        #region Enums
        
        public enum AttributeType
        {
            Strength,       // +melee damage
            Speed,          // +movement speed
            Health,         // +max health
            Endurance,      // +max stamina
            Intelligence    // +max eitr, +magic damage
        }
        
        #endregion
        
        #region Settings
        
        [Header("Leveling Settings")]
        [Tooltip("Base XP required for level 1")]
        public float baseXpRequired = 100f;
        [Tooltip("XP requirement multiplier per level")]
        public float xpScalingFactor = 1.15f;
        [Tooltip("Maximum level")]
        public int maxLevel = 100;
        
        [Header("XP Rewards")]
        [Tooltip("XP per kill (multiplied by target level)")]
        public float xpPerKill = 10f;
        [Tooltip("XP when a skill levels up")]
        public float xpPerSkillLevelUp = 25f;
        
        [Header("Attribute Bonuses")]
        [Tooltip("Damage bonus per Strength point (percentage)")]
        public float strengthDamageBonus = 0.01f;  // 1% per point
        [Tooltip("Speed bonus per Speed point (percentage)")]
        public float speedBonus = 0.005f;  // 0.5% per point
        [Tooltip("Health bonus per Health point")]
        public float healthBonusPerPoint = 5f;  // 5 HP per point
        [Tooltip("Stamina bonus per Endurance point")]
        public float staminaBonusPerPoint = 3f;  // 3 stamina per point
        [Tooltip("Eitr bonus per Intelligence point")]
        public float eitrBonusPerPoint = 3f;  // 3 eitr per point
        [Tooltip("Magic damage bonus per Intelligence point (percentage)")]
        public float intelligenceMagicBonus = 0.01f;  // 1% per point
        
        #endregion
        
        #region State
        
        private CompanionController _companion;
        private CompanionStats _stats;
        private CompanionSkills _skills;
        private ZNetView _nview;
        
        // Current progression
        private int _level;
        private float _currentXp;
        private int _unspentAttributePoints;
        
        // Attributes
        private Dictionary<AttributeType, int> _attributes = new Dictionary<AttributeType, int>();
        
        // Tracking
        private int _totalKills;
        private float _totalXpEarned;
        
        // Cache
        private float _cachedXpForNextLevel;
        private bool _initialized;
        
        // Level up effect
        private static GameObject _levelUpEffectPrefab;
        
        public static bool VerboseLogging = false;
        
        /// <summary>
        /// Event fired when any attribute changes (including level-ups).
        /// Used by CompanionStats to recalculate max health/stamina/eitr.
        /// </summary>
        public event Action OnAttributeChanged;
        
        #endregion
        
        #region Properties
        
        /// <summary>Current level (0-100)</summary>
        public int Level => _level;
        
        /// <summary>Current XP toward next level</summary>
        public float CurrentXp => _currentXp;
        
        /// <summary>XP required for next level</summary>
        public float XpForNextLevel => _cachedXpForNextLevel;
        
        /// <summary>Progress toward next level (0-1)</summary>
        public float LevelProgress => _cachedXpForNextLevel > 0 ? _currentXp / _cachedXpForNextLevel : 0f;
        
        /// <summary>Unspent attribute points</summary>
        public int UnspentAttributePoints => _unspentAttributePoints;
        
        /// <summary>Total kills</summary>
        public int TotalKills => _totalKills;
        
        /// <summary>Total XP earned lifetime</summary>
        public float TotalXpEarned => _totalXpEarned;
        
        /// <summary>Whether at max level</summary>
        public bool IsMaxLevel => _level >= maxLevel;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _stats = GetComponent<CompanionStats>();
            _skills = GetComponent<CompanionSkills>();
            _nview = GetComponent<ZNetView>();
            
            // Initialize attributes to 0
            foreach (AttributeType attr in Enum.GetValues(typeof(AttributeType)))
            {
                _attributes[attr] = 0;
            }
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
            
            _cachedXpForNextLevel = CalculateXpForLevel(_level + 1);
            
            // CRITICAL: Only load from ZDO if vault data wasn't already restored
            // This prevents ZDO from overwriting vault data with stale/empty values
            // The vault data is restored in CompanionController.TryRestoreEquipmentFromVault()
            // which runs BEFORE this Initialize() method
            if (!_vaultDataRestored)
            {
                LoadFromZDO();
            }
            
            // Cache level up effect
            if (_levelUpEffectPrefab == null)
            {
                _levelUpEffectPrefab = ZNetScene.instance?.GetPrefab("fx_GP_Activation");
            }
            
            _initialized = true;
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionProgression] Initialized {_companion?.companionName}: " +
                    $"Level {_level}, XP {_currentXp:F0}/{_cachedXpForNextLevel:F0}, " +
                    $"Unspent points: {_unspentAttributePoints}, vaultRestored={_vaultDataRestored}");
            }
        }
        
        #endregion
        
        #region XP and Leveling
        
        /// <summary>
        /// Awards XP to the companion.
        /// </summary>
        public void AddXp(float amount, string source = "")
        {
            if (amount <= 0 || IsMaxLevel) return;
            
            _currentXp += amount;
            _totalXpEarned += amount;
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionProgression] {_companion?.companionName} gained {amount:F0} XP from {source}. " +
                    $"Total: {_currentXp:F0}/{_cachedXpForNextLevel:F0}");
            }
            
            // Check for level up
            while (_currentXp >= _cachedXpForNextLevel && _level < maxLevel)
            {
                _currentXp -= _cachedXpForNextLevel;
                LevelUp();
            }
            
            SaveToZDO();
        }
        
        /// <summary>
        /// Called when a level is gained.
        /// </summary>
        private void LevelUp()
        {
            _level++;
            _unspentAttributePoints++;
            _cachedXpForNextLevel = CalculateXpForLevel(_level + 1);
            
            Debug.Log($"[CompanionProgression] {_companion?.companionName} reached level {_level}! " +
                $"Unspent attribute points: {_unspentAttributePoints}");
            
            // Play effect
            PlayLevelUpEffect();
            
            // Notify player
            NotifyLevelUp();
            
            // Trigger stat recalculation
            _stats?.RecalculateMaxStats();
            
            // Fire event for other systems
            OnAttributeChanged?.Invoke();
        }
        
        /// <summary>
        /// Calculates XP required for a specific level.
        /// </summary>
        public float CalculateXpForLevel(int level)
        {
            if (level <= 1) return baseXpRequired;
            
            // Exponential scaling: XP = base * (factor ^ (level-1))
            return baseXpRequired * Mathf.Pow(xpScalingFactor, level - 1);
        }
        
        /// <summary>
        /// Called when the companion kills an enemy.
        /// </summary>
        public void OnKill(Character target)
        {
            if (target == null) return;
            
            _totalKills++;
            
            // Calculate XP based on target level
            int targetLevel = target.GetLevel();
            float xp = xpPerKill * Mathf.Max(1, targetLevel);
            
            // Bonus XP for boss-type enemies
            if (target.IsBoss())
            {
                xp *= 5f;
            }
            
            AddXp(xp, $"killing {target.m_name}");
        }
        
        /// <summary>
        /// Called when a skill levels up.
        /// </summary>
        public void OnSkillLevelUp(Skills.SkillType skill, float newLevel)
        {
            AddXp(xpPerSkillLevelUp, $"{skill} skill level {newLevel}");
        }
        
        private void PlayLevelUpEffect()
        {
            if (_levelUpEffectPrefab != null)
            {
                var pos = transform.position + Vector3.up * 1f;
                UnityEngine.Object.Instantiate(_levelUpEffectPrefab, pos, Quaternion.identity);
            }
        }
        
        private void NotifyLevelUp()
        {
            // Show floating text
            if (DamageText.instance != null)
            {
                DamageText.instance.ShowText(
                    DamageText.TextType.Heal,
                    transform.position + Vector3.up * 2f,
                    $"Level {_level}!",
                    true
                );
            }
            
            // Notify owner
            var owner = _companion?.GetOwner();
            if (owner != null && owner == Player.m_localPlayer)
            {
                MessageHud.instance?.ShowMessage(
                    MessageHud.MessageType.Center,
                    $"{_companion?.GetDisplayName()} reached level {_level}!"
                );
            }
        }
        
        #endregion
        
        #region Attributes
        
        /// <summary>
        /// Gets the current value of an attribute.
        /// </summary>
        public int GetAttributeValue(AttributeType attribute)
        {
            return _attributes.TryGetValue(attribute, out int value) ? value : 0;
        }
        
        /// <summary>
        /// Spends a point to increase an attribute.
        /// Returns true if successful.
        /// </summary>
        public bool SpendAttributePoint(AttributeType attribute)
        {
            if (_unspentAttributePoints <= 0)
            {
                Debug.LogWarning("[CompanionProgression] No unspent attribute points");
                return false;
            }
            
            if (!_attributes.ContainsKey(attribute))
            {
                _attributes[attribute] = 0;
            }
            
            _attributes[attribute]++;
            _unspentAttributePoints--;
            
            Debug.Log($"[CompanionProgression] {_companion?.companionName} increased {attribute} to {_attributes[attribute]}. " +
                $"Remaining points: {_unspentAttributePoints}");
            
            // Recalculate stats
            _stats?.RecalculateMaxStats();
            
            // Fire event for other systems
            OnAttributeChanged?.Invoke();
            
            SaveToZDO();
            return true;
        }
        
        /// <summary>
        /// Gets total attribute points spent.
        /// </summary>
        public int GetTotalAttributePoints()
        {
            int total = 0;
            foreach (var kvp in _attributes)
            {
                total += kvp.Value;
            }
            return total;
        }
        
        /// <summary>
        /// Resets all attributes (gives points back).
        /// </summary>
        public void ResetAttributes()
        {
            int pointsToReturn = GetTotalAttributePoints();
            
            foreach (AttributeType attr in Enum.GetValues(typeof(AttributeType)))
            {
                _attributes[attr] = 0;
            }
            
            _unspentAttributePoints += pointsToReturn;
            
            Debug.Log($"[CompanionProgression] Reset attributes for {_companion?.companionName}. " +
                $"Returned {pointsToReturn} points.");
            
            _stats?.RecalculateMaxStats();
            
            // Fire event for other systems
            OnAttributeChanged?.Invoke();
            
            SaveToZDO();
        }
        
        #endregion
        
        #region Bonus Calculations
        
        // Cache CompanionLuck reference for scaling
        private CompanionLuck _luck;
        
        /// <summary>
        /// Gets the CompanionLuck component, caching it for performance.
        /// </summary>
        private CompanionLuck GetLuck()
        {
            if (_luck == null)
            {
                _luck = GetComponent<CompanionLuck>();
            }
            return _luck;
        }
        
        /// <summary>
        /// Gets the level-based scaling multiplier using the Luck system.
        /// This affects attribute bonuses and other level-scaled values.
        /// </summary>
        public float GetLevelScalingMultiplier()
        {
            var luck = GetLuck();
            if (luck != null)
            {
                return luck.GetLevelScalingMultiplier(_level);
            }
            
            // Fallback if luck not available - use average scaling
            if (_level <= 1) return 1f;
            return 1f + (_level - 1) * 0.00375f;
        }
        
        /// <summary>
        /// Gets the melee damage multiplier from Strength.
        /// Scaled by level and luck.
        /// </summary>
        public float GetMeleeDamageMultiplier()
        {
            int strength = GetAttributeValue(AttributeType.Strength);
            float baseBonus = 1f + (strength * strengthDamageBonus);
            
            // Apply level scaling to the bonus portion
            float scalingMultiplier = GetLevelScalingMultiplier();
            float bonusPortion = baseBonus - 1f;
            
            return 1f + (bonusPortion * scalingMultiplier);
        }
        
        /// <summary>
        /// Gets the movement speed multiplier from Speed.
        /// </summary>
        public float GetSpeedMultiplier()
        {
            int speed = GetAttributeValue(AttributeType.Speed);
            return 1f + (speed * speedBonus);
        }
        
        /// <summary>
        /// Gets the magic damage multiplier from Intelligence.
        /// Scaled by level and luck.
        /// </summary>
        public float GetMagicDamageMultiplier()
        {
            int intelligence = GetAttributeValue(AttributeType.Intelligence);
            float baseBonus = 1f + (intelligence * intelligenceMagicBonus);
            
            // Apply level scaling to the bonus portion
            float scalingMultiplier = GetLevelScalingMultiplier();
            float bonusPortion = baseBonus - 1f;
            
            return 1f + (bonusPortion * scalingMultiplier);
        }
        
        /// <summary>
        /// Gets combined damage multiplier for weapon attacks.
        /// NOTE: Does NOT include skill bonus - that's applied separately in CreateWeaponHitData
        /// to avoid double-applying skill damage.
        /// </summary>
        public float GetCombinedDamageMultiplier(Skills.SkillType weaponSkill, bool isMagic = false)
        {
            float multiplier = 1f;
            
            // Attribute bonus only - skill bonus is applied in CreateWeaponHitData
            if (isMagic)
            {
                multiplier *= GetMagicDamageMultiplier();
            }
            else
            {
                multiplier *= GetMeleeDamageMultiplier();
            }
            
            // NOTE: Skill bonus removed from here - it's already applied in CreateWeaponHitData
            // using vanilla's formula: 1 + (skillLevel * 0.005)
            
            return multiplier;
        }
        
        #endregion
        
        #region Persistence
        
        public void SaveToZDO()
        {
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;

            zdo.Set("companion_level", _level);
            zdo.Set("companion_xp", _currentXp);
            zdo.Set("companion_unspent_points", _unspentAttributePoints);
            zdo.Set("companion_total_kills", _totalKills);
            zdo.Set("companion_total_xp", _totalXpEarned);
            
            // Save attributes
            foreach (var kvp in _attributes)
            {
                zdo.Set($"companion_attr_{kvp.Key}", kvp.Value);
            }
        }
        
        public void LoadFromZDO()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;
            
            _level = zdo.GetInt("companion_level", 0);
            _currentXp = zdo.GetFloat("companion_xp", 0f);
            _unspentAttributePoints = zdo.GetInt("companion_unspent_points", 0);
            _totalKills = zdo.GetInt("companion_total_kills", 0);
            _totalXpEarned = zdo.GetFloat("companion_total_xp", 0f);
            
            // Load attributes
            foreach (AttributeType attr in Enum.GetValues(typeof(AttributeType)))
            {
                _attributes[attr] = zdo.GetInt($"companion_attr_{attr}", 0);
            }
            
            _cachedXpForNextLevel = CalculateXpForLevel(_level + 1);
        }
        
        /// <summary>
        /// Gets progression data for vault storage.
        /// Format: level|xp|unspent|kills|totalXp|str,spd,hp,end,int
        /// </summary>
        public string GetProgressionDataForVault()
        {
            string attributes = $"{GetAttributeValue(AttributeType.Strength)}," +
                $"{GetAttributeValue(AttributeType.Speed)}," +
                $"{GetAttributeValue(AttributeType.Health)}," +
                $"{GetAttributeValue(AttributeType.Endurance)}," +
                $"{GetAttributeValue(AttributeType.Intelligence)}";
            
            return $"{_level}|{_currentXp:F2}|{_unspentAttributePoints}|{_totalKills}|{_totalXpEarned:F2}|{attributes}";
        }
        
        /// <summary>
        /// Restores progression from vault data.
        /// CRITICAL: This sets _vaultDataRestored to prevent ZDO from overwriting vault data.
        /// </summary>
        public void RestoreProgressionFromVault(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                Debug.LogWarning($"[CompanionProgression] RestoreProgressionFromVault called with empty data for {_companion?.companionName}");
                return;
            }
            
            try
            {
                var parts = data.Split('|');
                if (parts.Length >= 6)
                {
                    _level = int.Parse(parts[0]);
                    _currentXp = float.Parse(parts[1]);
                    _unspentAttributePoints = int.Parse(parts[2]);
                    _totalKills = int.Parse(parts[3]);
                    _totalXpEarned = float.Parse(parts[4]);
                    
                    var attrParts = parts[5].Split(',');
                    if (attrParts.Length >= 5)
                    {
                        _attributes[AttributeType.Strength] = int.Parse(attrParts[0]);
                        _attributes[AttributeType.Speed] = int.Parse(attrParts[1]);
                        _attributes[AttributeType.Health] = int.Parse(attrParts[2]);
                        _attributes[AttributeType.Endurance] = int.Parse(attrParts[3]);
                        _attributes[AttributeType.Intelligence] = int.Parse(attrParts[4]);
                    }
                }
                
                // CRITICAL: Mark vault data as restored to prevent ZDO overwrite
                _vaultDataRestored = true;
                
                _cachedXpForNextLevel = CalculateXpForLevel(_level + 1);
                
                // Save vault data to ZDO so it persists correctly
                SaveToZDO();
                
                _stats?.RecalculateMaxStats();
                
                // Fire event for other systems
                OnAttributeChanged?.Invoke();
                
                int totalSpent = GetTotalAttributePoints();
                Debug.Log($"[CompanionProgression] Restored from vault for {_companion?.companionName} (ID: {_companion?.companionId}): " +
                    $"Level {_level}, {_totalKills} kills, {totalSpent} attributes spent " +
                    $"(STR:{_attributes[AttributeType.Strength]} SPD:{_attributes[AttributeType.Speed]} " +
                    $"HP:{_attributes[AttributeType.Health]} END:{_attributes[AttributeType.Endurance]} " +
                    $"INT:{_attributes[AttributeType.Intelligence]}), {_unspentAttributePoints} unspent");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionProgression] Failed to restore from vault for {_companion?.companionName}: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Debug
        
        /// <summary>
        /// Sets level directly (for testing).
        /// </summary>
        public void SetLevel(int level)
        {
            int oldLevel = _level;
            _level = Mathf.Clamp(level, 0, maxLevel);
            _currentXp = 0;
            _cachedXpForNextLevel = CalculateXpForLevel(_level + 1);
            
            // Award attribute points for levels gained
            int pointsToAward = _level - oldLevel;
            if (pointsToAward > 0)
            {
                _unspentAttributePoints += pointsToAward;
            }
            
            SaveToZDO();
            _stats?.RecalculateMaxStats();
            
            Debug.Log($"[CompanionProgression] Set level to {_level} for {_companion?.companionName}");
        }
        
        /// <summary>
        /// Gets a summary of progression for display.
        /// </summary>
        public string GetProgressionSummary()
        {
            return $"Level {_level} ({_currentXp:F0}/{_cachedXpForNextLevel:F0} XP)\n" +
                $"Kills: {_totalKills}\n" +
                $"Attribute Points: {_unspentAttributePoints}\n" +
                $"STR: {GetAttributeValue(AttributeType.Strength)} | " +
                $"SPD: {GetAttributeValue(AttributeType.Speed)} | " +
                $"HP: {GetAttributeValue(AttributeType.Health)}\n" +
                $"END: {GetAttributeValue(AttributeType.Endurance)} | " +
                $"INT: {GetAttributeValue(AttributeType.Intelligence)}";
        }
        
        #endregion
    }
}
