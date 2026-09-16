using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.AI;
using FiresCore.Npc.Archetypes.StatusEffects;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Uses abilities when the fight calls for them rather than all at once on engage: openers set up the fight,
    /// heals wait for damage, taunts wait for enemies to commit, evasion reacts to danger, and damage cooldowns
    /// wait for an opening.
    /// </summary>
    public class SkillDecisionSystem : MonoBehaviour
    {
        private const float LowHealthFraction = 0.4f;
        private const float CriticalHealthFraction = 0.2f;
        private const float UrgentHealthFraction = 0.6f;
        private const float AoeEnemySearchRange = 10f;
        private const float SurroundedRange = 5f;
        private const int SurroundedEnemyCount = 3;

        #region Skill Categories
        
        public enum SkillCategory
        {
            /// <summary>Used at combat start (buffs, marks, war cries)</summary>
            Opener,
            
            /// <summary>Used in response to damage/danger</summary>
            Reactive,
            
            /// <summary>Used throughout combat on cooldown</summary>
            Sustained,
            
            /// <summary>Used only when specific conditions met</summary>
            Situational
        }
        
        public enum SkillTrigger
        {
            /// <summary>Combat just started (first 3 seconds)</summary>
            CombatStart,
            
            /// <summary>Ally took damage</summary>
            AllyDamaged,
            
            /// <summary>Ally is low health (< 40%)</summary>
            AllyLowHealth,
            
            /// <summary>Ally is critical (< 20%)</summary>
            AllyCritical,
            
            /// <summary>Self took damage</summary>
            SelfDamaged,
            
            /// <summary>Self is low health</summary>
            SelfLowHealth,
            
            /// <summary>Self is being targeted by multiple enemies</summary>
            BeingFocused,
            
            /// <summary>Enemy is in range for attack</summary>
            EnemyInRange,
            
            /// <summary>Multiple enemies nearby</summary>
            Surrounded,
            
            /// <summary>Tank is actively taunting</summary>
            TankTaunting,
            
            /// <summary>Enemy is low health (execute range)</summary>
            EnemyLowHealth,
            
            /// <summary>High value target available</summary>
            HighValueTarget,
            
            /// <summary>On cooldown ready</summary>
            CooldownReady,
            
            /// <summary>Not in combat (for stealth opener)</summary>
            PreCombat
        }
        
        #endregion
        
        #region Skill Timing Configuration
        
        /// <summary>
        /// Defines timing and conditions for each skill type.
        /// </summary>
        public class SkillTiming
        {
            public string SkillName;
            public SkillCategory Category;
            public SkillTrigger[] Triggers;
            public float DelayAfterTrigger;  // Delay before using skill after trigger
            public float MinCombatDuration;  // Don't use before this much combat time
            public float MaxCombatDuration;  // Don't use after this much combat time (0 = no limit)
            public float HealthThreshold;    // For reactive skills - target health threshold
            public int MinEnemiesNearby;     // For AoE skills - minimum enemies
            public bool RequiresLineOfSight; // For ranged skills
        }
        
        // Timing configurations for different skill types
        private static readonly Dictionary<string, SkillTiming> _skillTimings = new Dictionary<string, SkillTiming>
        {
            // === TANK ===
            ["taunt"] = new SkillTiming
            {
                SkillName = "Taunt",
                Category = SkillCategory.Opener,
                Triggers = new[] { SkillTrigger.CombatStart, SkillTrigger.AllyDamaged },
                DelayAfterTrigger = 0.5f,  // Short delay to let enemies pick initial targets
                MinCombatDuration = 0.5f,
            },
            ["fortify"] = new SkillTiming
            {
                SkillName = "Fortify",
                Category = SkillCategory.Reactive,
                Triggers = new[] { SkillTrigger.SelfLowHealth, SkillTrigger.BeingFocused },
                DelayAfterTrigger = 0f,
                HealthThreshold = 0.4f,
            },
            
            // === BERSERKER ===
            ["warcry"] = new SkillTiming
            {
                SkillName = "Warcry",
                Category = SkillCategory.Opener,
                Triggers = new[] { SkillTrigger.CombatStart },
                DelayAfterTrigger = 0.2f,  // Use early to buff team
                MinCombatDuration = 0f,
            },
            ["berserkrage"] = new SkillTiming
            {
                SkillName = "BerserkRage",
                Category = SkillCategory.Reactive,
                Triggers = new[] { SkillTrigger.SelfLowHealth },
                DelayAfterTrigger = 0f,
                HealthThreshold = 0.35f,
            },
            
            // === HEALER ===
            ["sanctuary"] = new SkillTiming
            {
                SkillName = "Sanctuary",
                Category = SkillCategory.Reactive,
                Triggers = new[] { SkillTrigger.AllyDamaged, SkillTrigger.TankTaunting },
                DelayAfterTrigger = 0.5f,  // Wait a moment to see if damage is serious
                MinCombatDuration = 1f,    // Don't use instantly
            },
            ["purify"] = new SkillTiming
            {
                SkillName = "Purify",
                Category = SkillCategory.Reactive,
                Triggers = new[] { SkillTrigger.SelfDamaged },
                DelayAfterTrigger = 0f,
                HealthThreshold = 0.7f,
            },
            ["emergencyheal"] = new SkillTiming
            {
                SkillName = "EmergencyHeal",
                Category = SkillCategory.Reactive,
                Triggers = new[] { SkillTrigger.AllyCritical },
                DelayAfterTrigger = 0f,  // Immediate!
                HealthThreshold = 0.2f,
            },
            
            // === ROGUE ===
            ["stealth"] = new SkillTiming
            {
                SkillName = "Stealth",
                Category = SkillCategory.Opener,
                Triggers = new[] { SkillTrigger.PreCombat, SkillTrigger.CombatStart },
                DelayAfterTrigger = 0f,
                MinCombatDuration = 0f,
            },
            ["poison"] = new SkillTiming
            {
                SkillName = "Poison",
                Category = SkillCategory.Sustained,
                Triggers = new[] { SkillTrigger.EnemyInRange },
                DelayAfterTrigger = 0f,
            },
            ["evasion"] = new SkillTiming
            {
                SkillName = "Evasion",
                Category = SkillCategory.Reactive,
                Triggers = new[] { SkillTrigger.BeingFocused, SkillTrigger.SelfLowHealth },
                DelayAfterTrigger = 0f,
                HealthThreshold = 0.5f,
            },
            
            // === RANGER ===
            ["huntersmark"] = new SkillTiming
            {
                SkillName = "HuntersMark",
                Category = SkillCategory.Opener,
                Triggers = new[] { SkillTrigger.CombatStart, SkillTrigger.HighValueTarget },
                DelayAfterTrigger = 0f,
            },
            ["eagleeye"] = new SkillTiming
            {
                SkillName = "EagleEye",
                Category = SkillCategory.Opener,
                Triggers = new[] { SkillTrigger.CombatStart },
                DelayAfterTrigger = 0.5f,
            },
            
            // === MAGE ===
            ["elementalinfusion"] = new SkillTiming
            {
                SkillName = "ElementalInfusion",
                Category = SkillCategory.Opener,
                Triggers = new[] { SkillTrigger.CombatStart },
                DelayAfterTrigger = 0f,
            },
            ["arcaneshield"] = new SkillTiming
            {
                SkillName = "ArcaneShield",
                Category = SkillCategory.Reactive,
                Triggers = new[] { SkillTrigger.SelfDamaged, SkillTrigger.BeingFocused },
                DelayAfterTrigger = 0f,
                HealthThreshold = 0.6f,
            },
            
            // === PALADIN ===
            ["holysmite"] = new SkillTiming
            {
                SkillName = "HolySmite",
                Category = SkillCategory.Opener,
                Triggers = new[] { SkillTrigger.CombatStart, SkillTrigger.EnemyInRange },
                DelayAfterTrigger = 0.3f,
            },
            ["divineprotection"] = new SkillTiming
            {
                SkillName = "DivineProtection",
                Category = SkillCategory.Reactive,
                Triggers = new[] { SkillTrigger.AllyDamaged, SkillTrigger.AllyLowHealth },
                DelayAfterTrigger = 0.5f,
                HealthThreshold = 0.5f,
            },
            
            // === MONK ===
            ["chistrike"] = new SkillTiming
            {
                SkillName = "ChiStrike",
                Category = SkillCategory.Opener,
                Triggers = new[] { SkillTrigger.CombatStart, SkillTrigger.EnemyInRange },
                DelayAfterTrigger = 0f,
            },
            ["innerpeace"] = new SkillTiming
            {
                SkillName = "InnerPeace",
                Category = SkillCategory.Reactive,
                Triggers = new[] { SkillTrigger.AllyDamaged },
                DelayAfterTrigger = 1f,  // Wait a bit before meditating
                MinCombatDuration = 2f,
            },
        };
        
        #endregion
        
        #region State
        
        private CompanionController _companion;
        private CompanionAI _ai;
        private ArchetypeController _archetypeController;
        private Character _character;
        
        // Combat tracking
        private bool _inCombat;
        private float _combatStartTime;
        private float _lastDamageTakenTime;
        private float _lastAllyDamagedTime;
        private Character _lastDamageSource;
        
        // Pending skill decisions (delayed execution)
        private class PendingSkill
        {
            public string SkillName;
            public float ExecuteTime;
            public SkillTrigger Trigger;
        }
        private List<PendingSkill> _pendingSkills = new List<PendingSkill>();
        
        // Tracking which skills have been used this combat
        private HashSet<string> _skillsUsedThisCombat = new HashSet<string>();
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _ai = GetComponent<CompanionAI>();
            _archetypeController = GetComponent<ArchetypeController>();
            _character = GetComponent<Character>();
        }
        
        private void Start()
        {
            // Subscribe to damage events
            if (_character != null)
            {
                _character.m_onDamaged += OnDamageTaken;
            }
        }
        
        private void OnDestroy()
        {
            if (_character != null)
            {
                _character.m_onDamaged -= OnDamageTaken;
            }
        }
        
        private void Update()
        {
            if (_companion == null || !_companion.isTamed) return;
            
            // Track combat state
            bool wasInCombat = _inCombat;
            _inCombat = _ai != null && _ai.IsInCombat;
            
            // Combat just started
            if (_inCombat && !wasInCombat)
            {
                OnCombatStarted();
            }
            // Combat just ended
            else if (!_inCombat && wasInCombat)
            {
                OnCombatEnded();
            }
            
            // Process pending skills
            ProcessPendingSkills();
            
            // Update situational triggers
            if (_inCombat)
            {
                UpdateSituationalTriggers();
            }
        }
        
        #endregion
        
        #region Combat Events
        
        private void OnCombatStarted()
        {
            _combatStartTime = Time.time;
            _skillsUsedThisCombat.Clear();
            _pendingSkills.Clear();
            
            if (VerboseLogging)
            {
                Debug.Log($"[SkillDecision] {_companion.companionName} combat started - evaluating opener skills");
            }
            
            // Queue opener skills
            QueueSkillsForTrigger(SkillTrigger.CombatStart);
        }
        
        private void OnCombatEnded()
        {
            _pendingSkills.Clear();
            _skillsUsedThisCombat.Clear();
            
            if (VerboseLogging)
            {
                Debug.Log($"[SkillDecision] {_companion.companionName} combat ended");
            }
        }
        
        private void OnDamageTaken(float damage, Character attacker)
        {
            _lastDamageTakenTime = Time.time;
            _lastDamageSource = attacker;
            
            // Trigger reactive skills
            QueueSkillsForTrigger(SkillTrigger.SelfDamaged);
            
            // Check for low health triggers
            if (_character.GetHealthPercentage() < LowHealthFraction)
            {
                QueueSkillsForTrigger(SkillTrigger.SelfLowHealth);
            }
        }
        
        /// <summary>
        /// Called by external systems when an ally takes damage.
        /// Uses GroupHealthMonitor for intelligent triage when available.
        /// </summary>
        public void OnAllyDamaged(Character ally, float damage)
        {
            _lastAllyDamagedTime = Time.time;
            
            // GROUP COMBAT: Check if GroupHealthMonitor considers this urgent
            // This prevents healers from panic-healing every scratch
            if (_companion != null && _archetypeController != null && _archetypeController.IsSupport)
            {
                var coordinator = Combat.GroupCombatCoordinator.Instance;
                if (coordinator != null)
                {
                    var healthMonitor = coordinator.GetHealthMonitor(_companion.ownerPlayerId);
                    if (healthMonitor != null)
                    {
                        // Only trigger heal skills if there's actually an urgent target
                        var urgentTarget = healthMonitor.GetMostUrgentHealTarget(UrgentHealthFraction);
                        if (urgentTarget == null)
                        {
                            // No one is below 60% — just do a mild AllyDamaged trigger
                            QueueSkillsForTrigger(SkillTrigger.AllyDamaged);
                            return;
                        }
                        
                        // Someone urgent — check severity
                        if (urgentTarget.HealthPercent < CriticalHealthFraction)
                        {
                            QueueSkillsForTrigger(SkillTrigger.AllyCritical);
                        }
                        else if (urgentTarget.HealthPercent < LowHealthFraction)
                        {
                            QueueSkillsForTrigger(SkillTrigger.AllyLowHealth);
                        }
                        else
                        {
                            QueueSkillsForTrigger(SkillTrigger.AllyDamaged);
                        }
                        return;
                    }
                }
            }
            
            // Fallback: no coordinator — use simple threshold checks
            QueueSkillsForTrigger(SkillTrigger.AllyDamaged);
            
            float allyHealth = ally?.GetHealthPercentage() ?? 1f;
            if (allyHealth < LowHealthFraction)
            {
                QueueSkillsForTrigger(SkillTrigger.AllyLowHealth);
            }
            if (allyHealth < CriticalHealthFraction)
            {
                QueueSkillsForTrigger(SkillTrigger.AllyCritical);
            }
        }
        
        #endregion
        
        #region Skill Queueing
        
        /// <summary>
        /// Queues skills that match the given trigger for the current archetype.
        /// </summary>
        private void QueueSkillsForTrigger(SkillTrigger trigger)
        {
            if (_archetypeController == null) return;
            
            foreach (var kvp in _skillTimings)
            {
                var timing = kvp.Value;
                
                // Check if this skill matches the trigger
                bool matchesTrigger = false;
                foreach (var timingTrigger in timing.Triggers)
                {
                    if (timingTrigger == trigger)
                    {
                        matchesTrigger = true;
                        break;
                    }
                }
                
                if (!matchesTrigger) continue;
                
                // Check if skill is available for this archetype
                if (!IsSkillAvailableForArchetype(kvp.Key)) continue;
                
                // Check if already used this combat (for openers)
                if (timing.Category == SkillCategory.Opener && _skillsUsedThisCombat.Contains(kvp.Key))
                {
                    continue;
                }
                
                // Check combat duration requirements
                float combatDuration = Time.time - _combatStartTime;
                if (combatDuration < timing.MinCombatDuration) continue;
                if (timing.MaxCombatDuration > 0 && combatDuration > timing.MaxCombatDuration) continue;
                
                // Check if already pending
                bool alreadyPending = false;
                foreach (var pending in _pendingSkills)
                {
                    if (pending.SkillName == kvp.Key)
                    {
                        alreadyPending = true;
                        break;
                    }
                }
                if (alreadyPending) continue;
                
                // Queue the skill
                _pendingSkills.Add(new PendingSkill
                {
                    SkillName = kvp.Key,
                    ExecuteTime = Time.time + timing.DelayAfterTrigger,
                    Trigger = trigger
                });
                
                if (VerboseLogging)
                {
                    Debug.Log($"[SkillDecision] {_companion.companionName} queued {kvp.Key} for {trigger} (delay: {timing.DelayAfterTrigger}s)");
                }
            }
        }
        
        /// <summary>
        /// Processes pending skills and executes them when ready.
        /// </summary>
        private void ProcessPendingSkills()
        {
            if (_pendingSkills.Count == 0) return;
            
            for (int i = _pendingSkills.Count - 1; i >= 0; i--)
            {
                var pending = _pendingSkills[i];
                
                if (Time.time >= pending.ExecuteTime)
                {
                    // Remove from pending
                    _pendingSkills.RemoveAt(i);
                    
                    // Try to execute
                    if (ShouldExecuteSkill(pending.SkillName, pending.Trigger))
                    {
                        ExecuteSkill(pending.SkillName);
                        _skillsUsedThisCombat.Add(pending.SkillName);
                        
                        if (VerboseLogging)
                        {
                            Debug.Log($"[SkillDecision] {_companion.companionName} executed {pending.SkillName}");
                        }
                    }
                }
            }
        }
        
        #endregion
        
        #region Skill Execution
        
        /// <summary>
        /// Final check before executing a skill - validates all conditions are still met.
        /// Also checks with GroupSynergyManager to avoid stacking identical buffs/debuffs.
        /// </summary>
        private bool ShouldExecuteSkill(string skillName, SkillTrigger trigger)
        {
            if (!_inCombat && trigger != SkillTrigger.PreCombat) return false;
            
            if (!_skillTimings.TryGetValue(skillName, out var timing)) return true;
            
            // GROUP SYNERGY: Check if another companion just used this same ability
            // Prevents stacking identical buffs/debuffs within a 3-second window
            if (_companion != null)
            {
                var synergyManager = GroupSynergyManager.Instance;
                if (synergyManager != null && synergyManager.ShouldDelayAbility(
                    _companion.companionId, skillName))
                {
                    if (VerboseLogging)
                        Debug.Log($"[SkillDecision] {_companion.companionName} delaying {skillName} - another companion just used it");
                    return false;
                }
            }
            
            // Check health threshold for reactive skills
            if (timing.Category == SkillCategory.Reactive && timing.HealthThreshold > 0)
            {
                float selfHealth = _character?.GetHealthPercentage() ?? 1f;
                
                // For self-damage triggers, check self health
                if (trigger == SkillTrigger.SelfLowHealth || trigger == SkillTrigger.SelfDamaged)
                {
                    if (selfHealth > timing.HealthThreshold) return false;
                }
            }
            
            // Check enemy count for AoE skills
            if (timing.MinEnemiesNearby > 0)
            {
                int enemyCount = CountEnemiesNearby(AoeEnemySearchRange);
                if (enemyCount < timing.MinEnemiesNearby) return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// Signals the ArchetypeAbilitySystem to execute the skill.
        /// This doesn't execute directly - it sets a flag that the ability system checks.
        /// </summary>
        private void ExecuteSkill(string skillName)
        {
            // The actual execution is handled by ArchetypeAbilitySystem
            // This system just decides WHEN to use skills
            // We notify the ability system through a simple mechanism
            
            var abilitySystem = GetComponent<ArchetypeAbilitySystem>();
            if (abilitySystem != null)
            {
                // Set a flag or property that the ability system will check
                // For now, we'll use a simple approach - the ability system will 
                // check ShouldUseSkillNow() during its update
                _recommendedSkill = skillName;
                _recommendedSkillTime = Time.time;
            }
        }
        
        // Communication with ArchetypeAbilitySystem
        private string _recommendedSkill;
        private float _recommendedSkillTime;
        private const float RecommendationValidity = 2f; // Recommendation expires after 2 seconds
        
        /// <summary>
        /// Called by ArchetypeAbilitySystem to check if this system recommends using a specific skill now.
        /// </summary>
        public bool ShouldUseSkillNow(string skillName)
        {
            if (string.IsNullOrEmpty(_recommendedSkill)) return false;
            if (Time.time - _recommendedSkillTime > RecommendationValidity) return false;
            
            if (_recommendedSkill.Equals(skillName, System.StringComparison.OrdinalIgnoreCase))
            {
                _recommendedSkill = null; // Clear after use
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Gets the currently recommended skill (if any).
        /// </summary>
        public string GetRecommendedSkill()
        {
            if (string.IsNullOrEmpty(_recommendedSkill)) return null;
            if (Time.time - _recommendedSkillTime > RecommendationValidity) return null;
            return _recommendedSkill;
        }
        
        #endregion
        
        #region Situational Triggers
        
        /// <summary>
        /// Checks for situational triggers that should queue skills.
        /// </summary>
        private void UpdateSituationalTriggers()
        {
            // Check if being focused by multiple enemies
            int targetingCount = CountEnemiesTargetingMe();
            if (targetingCount >= 2)
            {
                QueueSkillsForTrigger(SkillTrigger.BeingFocused);
            }
            
            // Check if surrounded
            int nearbyEnemies = CountEnemiesNearby(SurroundedRange);
            if (nearbyEnemies >= SurroundedEnemyCount)
            {
                QueueSkillsForTrigger(SkillTrigger.Surrounded);
            }
            
            // Check for tank taunting
            if (IsTankTaunting())
            {
                QueueSkillsForTrigger(SkillTrigger.TankTaunting);
            }
        }
        
        #endregion
        
        #region Helper Methods
        
        private bool IsSkillAvailableForArchetype(string skillName)
        {
            if (_archetypeController == null) return false;
            
            var archetype = _archetypeController.CurrentArchetypeClass;
            string lowerName = skillName.ToLower();
            
            // Map skills to archetypes
            switch (archetype)
            {
                case ArchetypeClass.Tank:
                    return lowerName == "taunt" || lowerName == "fortify";
                case ArchetypeClass.Berserker:
                    return lowerName == "warcry" || lowerName == "berserkrage";
                case ArchetypeClass.Healer:
                    return lowerName == "sanctuary" || lowerName == "purify" || lowerName == "emergencyheal";
                case ArchetypeClass.Rogue:
                    return lowerName == "stealth" || lowerName == "poison" || lowerName == "evasion";
                case ArchetypeClass.Ranger:
                    return lowerName == "huntersmark" || lowerName == "eagleeye";
                case ArchetypeClass.Mage:
                    return lowerName == "elementalinfusion" || lowerName == "arcaneshield";
                case ArchetypeClass.Paladin:
                    return lowerName == "holysmite" || lowerName == "divineprotection";
                case ArchetypeClass.Monk:
                    return lowerName == "chistrike" || lowerName == "innerpeace";
                default:
                    return false;
            }
        }
        
        private int CountEnemiesNearby(float range)
        {
            if (_character == null) return 0;
            
            int count = 0;
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(transform.position, character.transform.position);
                if (dist <= range) count++;
            }
            return count;
        }
        
        private int CountEnemiesTargetingMe()
        {
            if (_character == null) return 0;
            
            int count = 0;
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                var ai = character.GetComponent<BaseAI>();
                if (ai != null && ai.GetTargetCreature() == _character)
                {
                    count++;
                }
            }
            return count;
        }
        
        private bool IsTankTaunting()
        {
            if (_companion == null) return false;
            
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (companion.ownerPlayerId != _companion.ownerPlayerId) continue;
                
                var archController = companion.GetArchetypeController();
                if (archController != null && archController.IsTank)
                {
                    var tankChar = companion.GetCharacter();
                    if (tankChar != null)
                    {
                        var seman = tankChar.GetSEMan();
                        if (seman != null && seman.HaveStatusEffect("CompanionTaunting".GetStableHashCode()))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        
        #endregion
    }
}
