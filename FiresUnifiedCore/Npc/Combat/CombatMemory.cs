using UnityEngine;
using System;
using System.Collections.Generic;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Tracks combat experiences and learns from dangerous encounters.
    /// Companions remember enemies that hurt them badly and adapt their behavior.
    /// 
    /// LEARNING TRIGGERS:
    /// - Taking massive damage (>50% health in one hit)
    /// - Dying to an enemy type
    /// - Getting one-shot or nearly one-shot
    /// - Repeated deaths to same enemy type
    /// 
    /// BEHAVIORAL ADAPTATIONS:
    /// - Play more defensively against known dangerous enemies
    /// - Prefer ranged attacks when possible
    /// - Stay at greater distance
    /// - Block/dodge more often
    /// - Retreat earlier
    /// 
    /// PERSISTENCE:
    /// Memory is stored in the companion's vault data and persists across sessions.
    /// </summary>
    public class CombatMemory : MonoBehaviour
    {
        [Header("Memory Settings")]
        [Tooltip("How much damage (% of max health) triggers memory of danger")]
        public float dangerousDamageThreshold = 0.4f;
        
        [Tooltip("How much damage (% of max health) is considered a one-shot threat")]
        public float oneshotThreshold = 0.8f;
        
        [Tooltip("Number of deaths before an enemy is considered very dangerous")]
        public int deathsForHighDanger = 2;
        
        [Tooltip("How long (seconds) before danger memory starts to fade")]
        public float memoryFadeTime = 3600f; // 1 hour
        
        [Header("Behavior Modifiers")]
        [Tooltip("Distance multiplier when facing dangerous enemies")]
        public float dangerousEnemyDistanceMultiplier = 1.5f;
        
        [Tooltip("Block/dodge chance bonus against dangerous enemies")]
        public float dangerousEnemyDefenseBonus = 0.3f;
        
        [Tooltip("Health threshold to retreat against dangerous enemies")]
        public float dangerousEnemyRetreatThreshold = 0.5f;
        
        // Components
        private CompanionController _companion;
        private Character _character;
        private CompanionCombat _combat;
        private ThreatAnalyzer _threatAnalyzer;
        
        // Memory storage - keyed by enemy prefab name
        private Dictionary<string, EnemyMemory> _enemyMemories = new Dictionary<string, EnemyMemory>();
        
        // Recent damage tracking
        private float _lastDamageTime;
        private string _lastDamageSource;
        private float _recentDamageTotal;
        private const float DAMAGE_WINDOW = 2f;
        
        public static bool VerboseLogging = false;
        
        /// <summary>
        /// Memory data for a specific enemy type.
        /// </summary>
        [Serializable]
        public class EnemyMemory
        {
            public string EnemyPrefabName;
            public string EnemyDisplayName;
            public int TimesKilledBy;
            public int TimesNearlyKilledBy;
            public float HighestDamageReceived;
            public float AverageDamageReceived;
            public int DamageInstanceCount;
            public long LastEncounterTimestamp;
            public bool IsOneShotter;
            public bool IsBoss;
            
            /// <summary>
            /// Danger level from 0-1 based on memories.
            /// </summary>
            public float DangerLevel
            {
                get
                {
                    float danger = 0f;
                    
                    // Deaths are very dangerous
                    danger += Mathf.Min(TimesKilledBy * 0.3f, 0.6f);
                    
                    // Near-deaths are concerning
                    danger += Mathf.Min(TimesNearlyKilledBy * 0.15f, 0.3f);
                    
                    // One-shotters are extremely dangerous
                    if (IsOneShotter) danger += 0.4f;
                    
                    // Bosses always get extra caution
                    if (IsBoss) danger += 0.2f;
                    
                    return Mathf.Clamp01(danger);
                }
            }
        }
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _character = GetComponent<Character>();
            _combat = GetComponent<CompanionCombat>();
            _threatAnalyzer = GetComponent<ThreatAnalyzer>();
        }
        
        private void Start()
        {
            // Subscribe to damage events
            if (_character != null)
            {
                _character.m_onDamaged += OnDamageReceived;
            }
            
            // Load memories from vault
            LoadMemoriesFromVault();
        }
        
        private void OnDestroy()
        {
            if (_character != null)
            {
                _character.m_onDamaged -= OnDamageReceived;
            }
        }
        
        #endregion
        
        #region Damage Tracking
        
        private void OnDamageReceived(float damage, Character attacker)
        {
            if (attacker == null) return;
            if (_character == null) return;
            
            float maxHealth = _character.GetMaxHealth();
            float damagePercent = damage / maxHealth;
            
            string enemyKey = GetEnemyKey(attacker);
            
            // Track cumulative damage in short window
            if (Time.time - _lastDamageTime < DAMAGE_WINDOW && _lastDamageSource == enemyKey)
            {
                _recentDamageTotal += damage;
            }
            else
            {
                _recentDamageTotal = damage;
            }
            
            _lastDamageTime = Time.time;
            _lastDamageSource = enemyKey;
            
            // Get or create memory for this enemy
            var memory = GetOrCreateMemory(attacker);
            
            // Update average damage
            memory.DamageInstanceCount++;
            memory.AverageDamageReceived = 
                ((memory.AverageDamageReceived * (memory.DamageInstanceCount - 1)) + damage) / memory.DamageInstanceCount;
            
            // Track highest damage
            if (damage > memory.HighestDamageReceived)
            {
                memory.HighestDamageReceived = damage;
            }
            
            // Check for dangerous damage
            if (damagePercent >= dangerousDamageThreshold)
            {
                memory.TimesNearlyKilledBy++;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[CombatMemory] {_companion?.companionName} took heavy hit ({damagePercent:P0}) from {memory.EnemyDisplayName}! " +
                        $"Near-deaths: {memory.TimesNearlyKilledBy}");
                }
            }
            
            // Check for one-shot potential
            if (damagePercent >= oneshotThreshold)
            {
                memory.IsOneShotter = true;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[CombatMemory] {_companion?.companionName} learned {memory.EnemyDisplayName} is a ONE-SHOTTER!");
                }
            }
            
            memory.LastEncounterTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            
            // Save to vault periodically
            SaveMemoriesToVault();
        }
        
        /// <summary>
        /// Called when the companion dies. Records which enemy killed them.
        /// </summary>
        public void OnDeathBy(Character killer)
        {
            if (killer == null) return;
            
            var memory = GetOrCreateMemory(killer);
            memory.TimesKilledBy++;
            memory.LastEncounterTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            
            if (VerboseLogging)
            {
                Debug.Log($"[CombatMemory] {_companion?.companionName} killed by {memory.EnemyDisplayName}! " +
                    $"Total deaths to this enemy: {memory.TimesKilledBy}");
            }
            
            // Always save after death
            SaveMemoriesToVault();
        }
        
        #endregion
        
        #region Memory Access
        
        /// <summary>
        /// Gets the danger level of an enemy based on past experiences.
        /// </summary>
        public float GetEnemyDangerLevel(Character enemy)
        {
            if (enemy == null) return 0f;
            
            string key = GetEnemyKey(enemy);
            if (_enemyMemories.TryGetValue(key, out var memory))
            {
                // Apply time decay - memories fade after a long time without encounters
                float timeSinceEncounter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - memory.LastEncounterTimestamp;
                float fadeMultiplier = Mathf.Clamp01(1f - (timeSinceEncounter / memoryFadeTime) * 0.5f);
                
                return memory.DangerLevel * fadeMultiplier;
            }
            
            return 0f;
        }
        
        /// <summary>
        /// Checks if an enemy is known to be dangerous.
        /// </summary>
        public bool IsKnownDangerous(Character enemy)
        {
            return GetEnemyDangerLevel(enemy) >= 0.3f;
        }
        
        /// <summary>
        /// Checks if an enemy has one-shot potential.
        /// </summary>
        public bool IsKnownOneShotter(Character enemy)
        {
            if (enemy == null) return false;
            
            string key = GetEnemyKey(enemy);
            if (_enemyMemories.TryGetValue(key, out var memory))
            {
                return memory.IsOneShotter;
            }
            
            return false;
        }
        
        /// <summary>
        /// Gets the memory for a specific enemy type.
        /// </summary>
        public EnemyMemory GetMemory(Character enemy)
        {
            if (enemy == null) return null;
            
            string key = GetEnemyKey(enemy);
            _enemyMemories.TryGetValue(key, out var memory);
            return memory;
        }
        
        /// <summary>
        /// Gets recommended combat modifiers based on enemy memories.
        /// </summary>
        public CombatModifiers GetCombatModifiers(Character enemy)
        {
            var modifiers = new CombatModifiers();
            
            if (enemy == null) return modifiers;
            
            float dangerLevel = GetEnemyDangerLevel(enemy);
            
            if (dangerLevel >= 0.3f)
            {
                // Increase preferred distance
                modifiers.PreferredDistanceMultiplier = 1f + (dangerLevel * (dangerousEnemyDistanceMultiplier - 1f));
                
                // Increase defense chance
                modifiers.DefenseChanceBonus = dangerLevel * dangerousEnemyDefenseBonus;
                
                // Earlier retreat threshold
                modifiers.RetreatHealthThreshold = Mathf.Lerp(0.2f, dangerousEnemyRetreatThreshold, dangerLevel);
                
                // Prefer ranged if available
                modifiers.PreferRanged = dangerLevel >= 0.5f;
                
                // More cautious aggression
                modifiers.AggressionMultiplier = 1f - (dangerLevel * 0.4f);
            }
            
            // One-shotters get extra caution
            if (IsKnownOneShotter(enemy))
            {
                modifiers.PreferredDistanceMultiplier *= 1.5f;
                modifiers.DefenseChanceBonus += 0.2f;
                modifiers.RetreatHealthThreshold = Mathf.Max(modifiers.RetreatHealthThreshold, 0.6f);
                modifiers.PreferRanged = true;
            }
            
            return modifiers;
        }
        
        #endregion
        
        #region Helpers
        
        private string GetEnemyKey(Character enemy)
        {
            if (enemy == null) return "";
            
            // Try to get prefab name
            var nview = enemy.GetComponent<ZNetView>();
            if (nview != null)
            {
                var zdo = nview.GetZDO();
                if (zdo != null)
                {
                    var prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
                    if (prefab != null)
                    {
                        return prefab.name;
                    }
                }
            }
            
            // Fallback to name
            string name = enemy.gameObject.name;
            if (name.EndsWith("(Clone)"))
            {
                name = name.Substring(0, name.Length - 7).Trim();
            }
            return name;
        }
        
        private EnemyMemory GetOrCreateMemory(Character enemy)
        {
            string key = GetEnemyKey(enemy);
            
            if (!_enemyMemories.TryGetValue(key, out var memory))
            {
                memory = new EnemyMemory
                {
                    EnemyPrefabName = key,
                    EnemyDisplayName = enemy.m_name ?? key,
                    IsBoss = enemy.IsBoss()
                };
                _enemyMemories[key] = memory;
            }
            
            return memory;
        }
        
        #endregion
        
        #region Persistence
        
        private void LoadMemoriesFromVault()
        {
            // TODO: Load from companion vault data
            // For now, memories are session-only but the infrastructure is here
        }
        
        private void SaveMemoriesToVault()
        {
            // TODO: Save to companion vault data
            // For now, memories are session-only but the infrastructure is here
        }
        
        #endregion
        
        #region Data Classes
        
        /// <summary>
        /// Combat behavior modifiers based on enemy memories.
        /// </summary>
        public struct CombatModifiers
        {
            /// <summary>Multiplier for preferred combat distance.</summary>
            public float PreferredDistanceMultiplier;
            
            /// <summary>Bonus chance to block/dodge.</summary>
            public float DefenseChanceBonus;
            
            /// <summary>Health threshold at which to retreat.</summary>
            public float RetreatHealthThreshold;
            
            /// <summary>Whether to prefer ranged attacks.</summary>
            public bool PreferRanged;
            
            /// <summary>Multiplier for aggression (lower = more cautious).</summary>
            public float AggressionMultiplier;
            
            public CombatModifiers()
            {
                PreferredDistanceMultiplier = 1f;
                DefenseChanceBonus = 0f;
                RetreatHealthThreshold = 0.2f;
                PreferRanged = false;
                AggressionMultiplier = 1f;
            }
        }
        
        #endregion
    }
}
