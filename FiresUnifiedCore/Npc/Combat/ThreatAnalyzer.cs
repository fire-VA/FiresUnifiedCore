using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Comprehensive threat analysis system that evaluates enemies and combat situations.
    /// Provides tactical recommendations based on:
    /// - Enemy health pools (higher = more dangerous, more sustained damage likely)
    /// - Enemy level/stars (stronger variants deal more damage)
    /// - Boss detection (requires special tactics)
    /// - Companion health state (affects aggression/caution)
    /// - Number of simultaneous threats
    /// - Enemy targeting behavior (who are they attacking?)
    /// 
    /// OUTPUT:
    /// - ThreatProfile: Detailed analysis of a single enemy
    /// - CombatSituation: Overall tactical assessment
    /// - CombatStance: Recommended playstyle (Aggressive, Balanced, Defensive, Survival)
    /// </summary>
    public class ThreatAnalyzer : MonoBehaviour
    {
        #region Configuration

        [Header("Health Thresholds")]
        [Tooltip("Below this %, companion becomes cautious")]
        public float cautiousHealthThreshold = 0.6f;
        
        [Tooltip("Below this %, companion prioritizes survival")]
        public float survivalHealthThreshold = 0.35f;
        
        [Tooltip("Below this %, companion tries to flee/kite")]
        public float criticalHealthThreshold = 0.2f;

        [Header("Enemy Classification")]
        [Tooltip("Health pool above this = dangerous enemy")]
        public float dangerousEnemyHealthThreshold = 200f;
        
        [Tooltip("Health pool above this = elite/mini-boss")]
        public float eliteEnemyHealthThreshold = 500f;
        
        [Tooltip("Health pool above this = boss")]
        public float bossHealthThreshold = 1000f;

        [Header("Threat Detection")]
        [Tooltip("Range to scan for threats")]
        public float threatScanRange = 25f;
        
        [Tooltip("How often to update threat analysis")]
        public float analysisInterval = 0.25f;

        [Header("Tactical Settings")]
        [Tooltip("When outnumbered by this many, consider retreating")]
        public int overwhelmedThreshold = 4;
        
        [Tooltip("Damage spike detection window (seconds)")]
        public float damageTrackingWindow = 5f;

        #endregion

        #region Enums

        /// <summary>
        /// Classification of enemy danger level.
        /// </summary>
        public enum EnemyClass
        {
            Trivial,        // Low health, low damage (boars, necks)
            Normal,         // Standard enemies (greydwarfs, skeletons)
            Dangerous,      // High health or damage (trolls, fulings)
            Elite,          // Mini-boss tier (2-star enemies, brutes)
            Boss            // World bosses, dungeon bosses
        }

        /// <summary>
        /// Recommended combat stance based on situation.
        /// </summary>
        public enum CombatStance
        {
            Aggressive,     // Low threat, high companion health - attack freely
            Balanced,       // Moderate situation - attack with caution
            Defensive,      // High threat or low health - prioritize blocking/dodging
            Survival,       // Critical health - hit and run, kiting
            Protective,     // Owner in danger - prioritize intercepting threats
            Retreat         // Overwhelmed - tactical withdrawal
        }

        /// <summary>
        /// Specific tactical recommendations.
        /// </summary>
        public enum TacticalAction
        {
            EngageFreely,       // Attack without restraint
            AttackAndDodge,     // Attack but be ready to dodge
            HitAndRun,          // Quick attacks, then retreat
            BlockAndCounter,    // Focus on blocking, counter when safe
            Kite,               // Maintain distance, use ranged if possible
            FocusBoss,          // Prioritize boss target
            ProtectOwner,       // Intercept threats to owner
            Retreat,            // Disengage and fall back
            UseSecondary,       // Use special/secondary attacks
            Parry               // Attempt parry for stagger
        }

        #endregion

        #region Data Structures

        /// <summary>
        /// Detailed threat profile for a single enemy.
        /// </summary>
        public struct ThreatProfile
        {
            public Character Enemy;
            public EnemyClass Classification;
            public float ThreatScore;           // 0-100 composite threat score
            public float MaxHealth;
            public float CurrentHealth;
            public float HealthPercent;
            public int Level;                   // 1 = normal, 2 = 1-star, 3 = 2-star
            public bool IsBoss;
            public bool IsTargetingCompanion;
            public bool IsTargetingOwner;
            public float DistanceToCompanion;
            public float DistanceToOwner;
            public float EstimatedDamagePerHit;
            public float AttackSpeed;           // Estimated attacks per second
            public bool IsCurrentlyAttacking;
            public bool CanBeStaggered;
            public bool CanBeParried;
            public string PrefabName;

            public static ThreatProfile Empty => new ThreatProfile { ThreatScore = 0 };
        }

        /// <summary>
        /// Overall combat situation assessment.
        /// </summary>
        public struct CombatSituation
        {
            public CombatStance RecommendedStance;
            public TacticalAction PrimaryAction;
            public TacticalAction SecondaryAction;
            public ThreatProfile PrimaryThreat;
            public ThreatProfile OwnerThreat;       // Most dangerous threat to owner
            public int TotalEnemies;
            public int DangerousEnemies;
            public int EnemiesTargetingCompanion;
            public int EnemiesTargetingOwner;
            public float CompanionHealthPercent;
            public float OwnerHealthPercent;
            public float OverallThreatLevel;        // 0-100
            public float RecentDamageReceived;
            public bool IsOwnerInDanger;
            public bool IsCompanionInDanger;
            public bool ShouldUseShield;
            public bool ShouldDodgeMore;
            public float AggressionModifier;        // 0.0 (defensive) to 2.0 (aggressive)
            public float BlockChanceModifier;       // Multiplier for block chance
            public float DodgeChanceModifier;       // Multiplier for dodge chance

            public static CombatSituation Safe => new CombatSituation
            {
                RecommendedStance = CombatStance.Aggressive,
                PrimaryAction = TacticalAction.EngageFreely,
                AggressionModifier = 1.5f,
                BlockChanceModifier = 0.5f,
                DodgeChanceModifier = 0.5f
            };
        }

        /// <summary>
        /// Tracks damage received over time for spike detection.
        /// </summary>
        private struct DamageEvent
        {
            public float Time;
            public float Damage;
            public Character Source;
        }

        #endregion

        #region State

        private CompanionController _companion;
        private CompanionCombat _combat;
        private CompanionAI _ai;
        private Character _character;
        private EnemyAttackRecognition _attackRecognition;
        private CombatMemory _combatMemory;

        private CombatSituation _currentSituation;
        private Dictionary<Character, ThreatProfile> _threatProfiles = new Dictionary<Character, ThreatProfile>();
        private List<DamageEvent> _recentDamage = new List<DamageEvent>();
        private float _lastAnalysisTime;

        // Known boss prefab patterns
        private static readonly HashSet<string> _bossPatterns = new HashSet<string>
        {
            "eikthyr", "elder", "bonemass", "moder", "yagluth", "queen", "fader",
            "dvergr_boss", "seeker_queen", "gd_king", "troll", "goblinbrute",
            "stone_golem", "serpent", "lox", "growth"
        };

        // Known high-damage enemy patterns
        private static readonly HashSet<string> _highDamagePatterns = new HashSet<string>
        {
            "deathsquito", "fuling_berserker", "goblin_brute", "draugr_elite",
            "wolf", "fenring", "ulv", "growth", "gjall", "tick", "seeker"
        };

        public static bool VerboseLogging = false;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _combat = GetComponent<CompanionCombat>();
            _ai = GetComponent<CompanionAI>();
            _character = GetComponent<Character>();
            _attackRecognition = GetComponent<EnemyAttackRecognition>();
            _combatMemory = GetComponent<CombatMemory>();
            
            // Add CombatMemory if missing
            if (_combatMemory == null)
            {
                _combatMemory = gameObject.AddComponent<CombatMemory>();
            }
        }

        private void Update()
        {
            // Allow both tamed AND wild companions to analyze threats
            if (_companion == null) return;

            // Throttled analysis
            if (Time.time - _lastAnalysisTime >= analysisInterval)
            {
                _lastAnalysisTime = Time.time;
                AnalyzeCombatSituation();
            }

            // Clean up old damage events
            CleanupDamageHistory();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Gets the current combat situation assessment.
        /// </summary>
        public CombatSituation GetCurrentSituation()
        {
            return _currentSituation;
        }

        /// <summary>
        /// Gets the threat profile for a specific enemy.
        /// </summary>
        public ThreatProfile GetThreatProfile(Character enemy)
        {
            if (enemy != null && _threatProfiles.TryGetValue(enemy, out var profile))
            {
                return profile;
            }
            return ThreatProfile.Empty;
        }

        /// <summary>
        /// Records damage received for tracking.
        /// Call this from damage handlers.
        /// </summary>
        public void RecordDamageReceived(float damage, Character source)
        {
            _recentDamage.Add(new DamageEvent
            {
                Time = Time.time,
                Damage = damage,
                Source = source
            });

            // Immediate re-analysis on significant damage
            if (damage > _character.GetMaxHealth() * 0.1f)
            {
                AnalyzeCombatSituation();
            }
        }

        /// <summary>
        /// Checks if a target should be engaged based on current situation.
        /// </summary>
        public bool ShouldEngageTarget(Character target)
        {
            if (target == null || target.IsDead()) return false;

            var profile = GetThreatProfile(target);
            
            // Always engage if targeting owner
            if (profile.IsTargetingOwner) return true;

            // Don't engage bosses alone if health is low
            if (profile.IsBoss && _currentSituation.CompanionHealthPercent < survivalHealthThreshold)
            {
                return false;
            }

            // Don't engage if in retreat mode
            if (_currentSituation.RecommendedStance == CombatStance.Retreat)
            {
                return profile.IsTargetingOwner; // Only engage to protect owner
            }

            return true;
        }

        /// <summary>
        /// Gets the recommended attack frequency modifier.
        /// Lower values = more cautious, higher = more aggressive.
        /// </summary>
        public float GetAttackFrequencyModifier()
        {
            return _currentSituation.AggressionModifier;
        }

        /// <summary>
        /// Gets the recommended block chance modifier.
        /// </summary>
        public float GetBlockChanceModifier()
        {
            return _currentSituation.BlockChanceModifier;
        }

        /// <summary>
        /// Gets the recommended dodge chance modifier.
        /// </summary>
        public float GetDodgeChanceModifier()
        {
            return _currentSituation.DodgeChanceModifier;
        }

        /// <summary>
        /// Checks if companion should use defensive tactics against this enemy.
        /// </summary>
        public bool ShouldPlayDefensive(Character enemy)
        {
            var profile = GetThreatProfile(enemy);
            
            // Check combat memory for known dangerous enemies
            if (_combatMemory != null && _combatMemory.IsKnownDangerous(enemy))
            {
                return true;
            }
            
            return profile.Classification >= EnemyClass.Dangerous ||
                   _currentSituation.RecommendedStance >= CombatStance.Defensive ||
                   _currentSituation.CompanionHealthPercent < cautiousHealthThreshold;
        }
        
        /// <summary>
        /// Gets combat modifiers based on learned enemy behavior.
        /// Combines ThreatAnalyzer assessment with CombatMemory experience.
        /// </summary>
        public CombatMemory.CombatModifiers GetLearnedCombatModifiers(Character enemy)
        {
            if (_combatMemory == null)
            {
                return new CombatMemory.CombatModifiers();
            }
            
            return _combatMemory.GetCombatModifiers(enemy);
        }

        /// <summary>
        /// Checks if this enemy is worth using secondary attacks on.
        /// </summary>
        public bool ShouldUseSecondaryAttack(Character enemy)
        {
            var profile = GetThreatProfile(enemy);
            
            // Use secondaries on dangerous+ enemies
            if (profile.Classification >= EnemyClass.Dangerous) return true;
            
            // Use on low health enemies to finish them
            if (profile.HealthPercent < 0.25f) return true;
            
            // Use if we're healthy and enemy can be staggered
            if (_currentSituation.CompanionHealthPercent > 0.7f && profile.CanBeStaggered) return true;
            
            return false;
        }

        /// <summary>
        /// Gets kiting distance recommendation for this enemy.
        /// </summary>
        public float GetKitingDistance(Character enemy)
        {
            var profile = GetThreatProfile(enemy);
            
            // Base distance on enemy danger and our health
            float baseDistance = 8f;
            
            if (profile.IsBoss) baseDistance = 15f;
            else if (profile.Classification == EnemyClass.Elite) baseDistance = 12f;
            else if (profile.Classification == EnemyClass.Dangerous) baseDistance = 10f;
            
            // Increase distance if low health
            if (_currentSituation.CompanionHealthPercent < survivalHealthThreshold)
            {
                baseDistance *= 1.5f;
            }
            
            return baseDistance;
        }

        #endregion

        #region Analysis

        private void AnalyzeCombatSituation()
        {
            // Reset profiles
            _threatProfiles.Clear();

            // Get references
            var owner = _companion?.GetOwner();
            var ownerCharacter = owner?.GetComponent<Character>();
            Vector3 myPos = transform.position;
            Vector3 ownerPos = owner != null ? owner.transform.position : myPos;

            // Scan for enemies
            var enemies = new List<Character>();
            Character.GetCharactersInRange(myPos, threatScanRange, enemies);

            // Build threat profiles
            ThreatProfile primaryThreat = ThreatProfile.Empty;
            ThreatProfile ownerThreat = ThreatProfile.Empty;
            int totalEnemies = 0;
            int dangerousEnemies = 0;
            int targetingCompanion = 0;
            int targetingOwner = 0;

            foreach (var enemy in enemies)
            {
                if (!IsValidEnemy(enemy)) continue;

                var profile = BuildThreatProfile(enemy, ownerCharacter, myPos, ownerPos);
                _threatProfiles[enemy] = profile;
                totalEnemies++;

                if (profile.Classification >= EnemyClass.Dangerous)
                    dangerousEnemies++;
                if (profile.IsTargetingCompanion)
                    targetingCompanion++;
                if (profile.IsTargetingOwner)
                    targetingOwner++;

                // Track highest threats
                if (profile.ThreatScore > primaryThreat.ThreatScore)
                    primaryThreat = profile;
                if (profile.IsTargetingOwner && profile.ThreatScore > ownerThreat.ThreatScore)
                    ownerThreat = profile;
            }

            // Calculate damage received recently
            float recentDamage = CalculateRecentDamage();
            float companionHealthPercent = _character?.GetHealthPercentage() ?? 1f;
            float ownerHealthPercent = ownerCharacter?.GetHealthPercentage() ?? 1f;

            // Build situation assessment
            _currentSituation = new CombatSituation
            {
                PrimaryThreat = primaryThreat,
                OwnerThreat = ownerThreat,
                TotalEnemies = totalEnemies,
                DangerousEnemies = dangerousEnemies,
                EnemiesTargetingCompanion = targetingCompanion,
                EnemiesTargetingOwner = targetingOwner,
                CompanionHealthPercent = companionHealthPercent,
                OwnerHealthPercent = ownerHealthPercent,
                RecentDamageReceived = recentDamage,
                IsOwnerInDanger = targetingOwner > 0 || ownerHealthPercent < 0.5f,
                IsCompanionInDanger = companionHealthPercent < survivalHealthThreshold || dangerousEnemies > 1
            };

            // Calculate overall threat level (0-100)
            _currentSituation.OverallThreatLevel = CalculateOverallThreat(
                totalEnemies, dangerousEnemies, primaryThreat, companionHealthPercent, recentDamage);

            // Determine stance and actions
            DetermineStanceAndActions(ref _currentSituation);

            if (VerboseLogging && totalEnemies > 0)
            {
                Debug.Log($"[ThreatAnalyzer] Situation: {_currentSituation.RecommendedStance}, " +
                    $"Threat: {_currentSituation.OverallThreatLevel:F0}, " +
                    $"Enemies: {totalEnemies} ({dangerousEnemies} dangerous), " +
                    $"Health: {companionHealthPercent:P0}, " +
                    $"Action: {_currentSituation.PrimaryAction}");
            }
        }

        private ThreatProfile BuildThreatProfile(Character enemy, Character ownerCharacter, Vector3 myPos, Vector3 ownerPos)
        {
            var profile = new ThreatProfile
            {
                Enemy = enemy,
                MaxHealth = enemy.GetMaxHealth(),
                CurrentHealth = enemy.GetHealth(),
                DistanceToCompanion = Vector3.Distance(myPos, enemy.transform.position),
                DistanceToOwner = Vector3.Distance(ownerPos, enemy.transform.position),
                PrefabName = GetPrefabName(enemy)
            };

            profile.HealthPercent = profile.MaxHealth > 0 ? profile.CurrentHealth / profile.MaxHealth : 1f;
            profile.Level = GetEnemyLevel(enemy);
            profile.IsBoss = IsBossEnemy(enemy, profile.PrefabName, profile.MaxHealth);
            profile.Classification = ClassifyEnemy(enemy, profile);
            profile.EstimatedDamagePerHit = EstimateDamageOutput(enemy, profile);
            profile.AttackSpeed = EstimateAttackSpeed(profile.PrefabName);
            profile.CanBeStaggered = CanBeStaggered(enemy, profile);
            profile.CanBeParried = CanBeParried(profile);

            // Check targeting
            var enemyAI = enemy.GetComponent<BaseAI>();
            if (enemyAI != null)
            {
                var target = enemyAI.GetTargetCreature();
                profile.IsTargetingCompanion = target == _character;
                profile.IsTargetingOwner = target == ownerCharacter;
            }

            // Check if currently attacking
            if (_attackRecognition != null)
            {
                profile.IsCurrentlyAttacking = _attackRecognition.IsEnemyAttacking(enemy);
            }

            // Calculate composite threat score
            profile.ThreatScore = CalculateThreatScore(profile);

            return profile;
        }

        private float CalculateThreatScore(ThreatProfile profile)
        {
            float score = 0f;

            // Base score from classification
            switch (profile.Classification)
            {
                case EnemyClass.Trivial: score = 5f; break;
                case EnemyClass.Normal: score = 15f; break;
                case EnemyClass.Dangerous: score = 35f; break;
                case EnemyClass.Elite: score = 55f; break;
                case EnemyClass.Boss: score = 80f; break;
            }

            // Level multiplier (stars)
            score *= 1f + (profile.Level - 1) * 0.3f;

            // Distance modifier (closer = more threatening)
            if (profile.DistanceToCompanion < 5f)
                score += 15f;
            else if (profile.DistanceToCompanion < 10f)
                score += 8f;

            // Targeting modifier
            if (profile.IsTargetingCompanion)
                score += 20f;
            if (profile.IsTargetingOwner)
                score += 30f; // High priority to protect owner

            // Currently attacking modifier
            if (profile.IsCurrentlyAttacking)
                score += 15f;

            // Health modifier (healthy enemies are more threatening)
            score *= 0.5f + profile.HealthPercent * 0.5f;
            
            // NEW: Combat memory modifier - enemies we've learned are dangerous get bonus threat
            if (_combatMemory != null && profile.Enemy != null)
            {
                float dangerLevel = _combatMemory.GetEnemyDangerLevel(profile.Enemy);
                if (dangerLevel > 0)
                {
                    score += dangerLevel * 25f; // Up to 25 bonus threat from memory
                    
                    if (VerboseLogging && dangerLevel >= 0.3f)
                    {
                        Debug.Log($"[ThreatAnalyzer] {profile.PrefabName} has danger memory: {dangerLevel:P0}, adding {dangerLevel * 25f:F0} to threat score");
                    }
                }
                
                // One-shotters get massive threat bonus
                if (_combatMemory.IsKnownOneShotter(profile.Enemy))
                {
                    score += 30f;
                }
            }

            return Mathf.Clamp(score, 0f, 100f);
        }

        private float CalculateOverallThreat(int totalEnemies, int dangerousEnemies, 
            ThreatProfile primaryThreat, float companionHealth, float recentDamage)
        {
            float threat = 0f;

            // Base from primary threat
            threat += primaryThreat.ThreatScore * 0.5f;

            // Number of enemies
            threat += Mathf.Min(totalEnemies * 5f, 30f);
            threat += dangerousEnemies * 10f;

            // Health modifier (lower health = higher perceived threat)
            if (companionHealth < criticalHealthThreshold)
                threat += 30f;
            else if (companionHealth < survivalHealthThreshold)
                threat += 20f;
            else if (companionHealth < cautiousHealthThreshold)
                threat += 10f;

            // Recent damage taken
            float maxHealth = _character?.GetMaxHealth() ?? 100f;
            float damagePercent = recentDamage / maxHealth;
            threat += damagePercent * 30f;

            return Mathf.Clamp(threat, 0f, 100f);
        }

        private void DetermineStanceAndActions(ref CombatSituation situation)
        {
            // Determine recommended stance
            if (situation.IsOwnerInDanger && situation.EnemiesTargetingOwner > 0)
            {
                situation.RecommendedStance = CombatStance.Protective;
                situation.PrimaryAction = TacticalAction.ProtectOwner;
                situation.SecondaryAction = TacticalAction.AttackAndDodge;
            }
            else if (situation.CompanionHealthPercent < criticalHealthThreshold)
            {
                situation.RecommendedStance = CombatStance.Survival;
                situation.PrimaryAction = TacticalAction.Kite;
                situation.SecondaryAction = TacticalAction.HitAndRun;
            }
            else if (situation.TotalEnemies >= overwhelmedThreshold || 
                     (situation.DangerousEnemies >= 2 && situation.CompanionHealthPercent < cautiousHealthThreshold))
            {
                situation.RecommendedStance = CombatStance.Retreat;
                situation.PrimaryAction = TacticalAction.Retreat;
                situation.SecondaryAction = TacticalAction.BlockAndCounter;
            }
            else if (situation.PrimaryThreat.IsBoss)
            {
                situation.RecommendedStance = CombatStance.Defensive;
                situation.PrimaryAction = TacticalAction.FocusBoss;
                situation.SecondaryAction = TacticalAction.BlockAndCounter;
            }
            else if (situation.CompanionHealthPercent < survivalHealthThreshold)
            {
                situation.RecommendedStance = CombatStance.Defensive;
                situation.PrimaryAction = TacticalAction.HitAndRun;
                situation.SecondaryAction = TacticalAction.BlockAndCounter;
            }
            else if (situation.DangerousEnemies > 0 || situation.CompanionHealthPercent < cautiousHealthThreshold)
            {
                situation.RecommendedStance = CombatStance.Balanced;
                situation.PrimaryAction = TacticalAction.AttackAndDodge;
                situation.SecondaryAction = TacticalAction.BlockAndCounter;
            }
            else
            {
                situation.RecommendedStance = CombatStance.Aggressive;
                situation.PrimaryAction = TacticalAction.EngageFreely;
                situation.SecondaryAction = TacticalAction.UseSecondary;
            }

            // Check for parry opportunity
            if (situation.PrimaryThreat.CanBeParried && 
                situation.PrimaryThreat.IsCurrentlyAttacking &&
                situation.CompanionHealthPercent > survivalHealthThreshold)
            {
                situation.SecondaryAction = TacticalAction.Parry;
            }

            // Calculate modifiers based on stance
            switch (situation.RecommendedStance)
            {
                case CombatStance.Aggressive:
                    situation.AggressionModifier = 1.5f;
                    situation.BlockChanceModifier = 0.5f;
                    situation.DodgeChanceModifier = 0.5f;
                    situation.ShouldUseShield = false;
                    situation.ShouldDodgeMore = false;
                    break;

                case CombatStance.Balanced:
                    situation.AggressionModifier = 1.0f;
                    situation.BlockChanceModifier = 1.0f;
                    situation.DodgeChanceModifier = 1.0f;
                    situation.ShouldUseShield = true;
                    situation.ShouldDodgeMore = false;
                    break;

                case CombatStance.Defensive:
                    situation.AggressionModifier = 0.6f;
                    situation.BlockChanceModifier = 1.5f;
                    situation.DodgeChanceModifier = 1.5f;
                    situation.ShouldUseShield = true;
                    situation.ShouldDodgeMore = true;
                    break;

                case CombatStance.Survival:
                    situation.AggressionModifier = 0.3f;
                    situation.BlockChanceModifier = 2.0f;
                    situation.DodgeChanceModifier = 2.0f;
                    situation.ShouldUseShield = true;
                    situation.ShouldDodgeMore = true;
                    break;

                case CombatStance.Protective:
                    situation.AggressionModifier = 1.2f;
                    situation.BlockChanceModifier = 1.2f;
                    situation.DodgeChanceModifier = 0.8f;
                    situation.ShouldUseShield = true;
                    situation.ShouldDodgeMore = false;
                    break;

                case CombatStance.Retreat:
                    situation.AggressionModifier = 0.2f;
                    situation.BlockChanceModifier = 1.5f;
                    situation.DodgeChanceModifier = 2.5f;
                    situation.ShouldUseShield = true;
                    situation.ShouldDodgeMore = true;
                    break;
            }
        }

        #endregion

        #region Enemy Classification

        private EnemyClass ClassifyEnemy(Character enemy, ThreatProfile profile)
        {
            // Check for boss first
            if (profile.IsBoss)
                return EnemyClass.Boss;

            // Check max health thresholds
            float effectiveHealth = profile.MaxHealth * (1f + (profile.Level - 1) * 0.5f);

            if (effectiveHealth >= eliteEnemyHealthThreshold)
                return EnemyClass.Elite;
            if (effectiveHealth >= dangerousEnemyHealthThreshold)
                return EnemyClass.Dangerous;

            // Check for known high-damage enemies
            if (_highDamagePatterns.Contains(profile.PrefabName.ToLower()))
                return EnemyClass.Dangerous;

            // Level-based escalation
            if (profile.Level >= 3) // 2-star
                return EnemyClass.Dangerous;
            if (profile.Level >= 2) // 1-star
                return EnemyClass.Normal;

            // Low health = trivial
            if (profile.MaxHealth < 50f)
                return EnemyClass.Trivial;

            return EnemyClass.Normal;
        }

        private bool IsBossEnemy(Character enemy, string prefabName, float maxHealth)
        {
            // Check name patterns
            string lowerName = prefabName.ToLower();
            foreach (var pattern in _bossPatterns)
            {
                if (lowerName.Contains(pattern))
                    return true;
            }

            // Very high health = probably a boss
            if (maxHealth >= bossHealthThreshold)
                return true;

            // Check Character.m_boss field if accessible
            try
            {
                var bossField = typeof(Character).GetField("m_boss", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (bossField != null)
                {
                    return (bool)bossField.GetValue(enemy);
                }
            }
            catch { }

            return false;
        }

        private int GetEnemyLevel(Character enemy)
        {
            // Level 1 = normal, 2 = 1-star, 3 = 2-star
            try
            {
                return enemy.GetLevel();
            }
            catch
            {
                return 1;
            }
        }

        private float EstimateDamageOutput(Character enemy, ThreatProfile profile)
        {
            // Try to get from Humanoid if available
            var humanoid = enemy.GetComponent<Humanoid>();
            if (humanoid != null)
            {
                var weapon = humanoid.GetCurrentWeapon();
                if (weapon != null)
                {
                    return weapon.GetDamage().GetTotalDamage();
                }
            }

            // Estimate based on classification and health
            float baseDamage = profile.Classification switch
            {
                EnemyClass.Trivial => 5f,
                EnemyClass.Normal => 15f,
                EnemyClass.Dangerous => 35f,
                EnemyClass.Elite => 60f,
                EnemyClass.Boss => 100f,
                _ => 20f
            };

            // Level multiplier
            return baseDamage * (1f + (profile.Level - 1) * 0.5f);
        }

        private float EstimateAttackSpeed(string prefabName)
        {
            string lower = prefabName.ToLower();

            // Fast attackers
            if (lower.Contains("deathsquito") || lower.Contains("wolf") || lower.Contains("fenring"))
                return 1.5f;

            // Slow attackers
            if (lower.Contains("troll") || lower.Contains("golem") || lower.Contains("boss"))
                return 0.4f;

            // Medium
            return 0.7f;
        }

        private bool CanBeStaggered(Character enemy, ThreatProfile profile)
        {
            // Bosses and high-level elites can't be staggered easily
            if (profile.IsBoss) return false;
            if (profile.Classification == EnemyClass.Elite && profile.Level >= 2) return false;

            // Check for stagger immunity (some creatures have it)
            string lower = profile.PrefabName.ToLower();
            if (lower.Contains("golem") || lower.Contains("boss")) return false;

            return true;
        }

        private bool CanBeParried(ThreatProfile profile)
        {
            // Most attacks can be parried except boss/projectile attacks
            if (profile.IsBoss) return false;

            string lower = profile.PrefabName.ToLower();
            if (lower.Contains("deathsquito")) return true; // Actually can be parried!
            if (lower.Contains("troll")) return false; // Too big
            if (lower.Contains("golem")) return false;

            return true;
        }

        #endregion

        #region Utility

        private bool IsValidEnemy(Character character)
        {
            if (character == null || character.IsDead()) return false;
            if (character == _character) return false;
            if (character.IsPlayer()) return false;
            if (character.IsTamed()) return false;
            return BaseAI.IsEnemy(_character, character);
        }

        private string GetPrefabName(Character character)
        {
            if (character == null) return "";
            string name = character.gameObject.name;
            int cloneIndex = name.IndexOf("(Clone)");
            if (cloneIndex > 0)
                name = name.Substring(0, cloneIndex);
            return name;
        }

        private float CalculateRecentDamage()
        {
            float total = 0f;
            float cutoff = Time.time - damageTrackingWindow;

            foreach (var evt in _recentDamage)
            {
                if (evt.Time >= cutoff)
                    total += evt.Damage;
            }

            return total;
        }

        private void CleanupDamageHistory()
        {
            float cutoff = Time.time - damageTrackingWindow;
            _recentDamage.RemoveAll(e => e.Time < cutoff);
        }

        #endregion
    }
}
