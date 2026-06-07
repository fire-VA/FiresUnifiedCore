using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// A single shared threat table for a player's entire companion group.
    /// Replaces per-companion independent targeting with coordinated target assignment.
    /// 
    /// PERFORMANCE:
    /// - Uses a single Physics.OverlapSphere per tick (not per companion)
    /// - Reuses pooled lists to avoid GC allocations
    /// - Entries are updated in-place rather than reallocated
    /// - Scoring is O(enemies * companions) but runs only every 0.3s
    /// </summary>
    public class SharedThreatTable
    {
        #region Data Types

        public enum EnemyClassification
        {
            Minion,      // Very weak — barely a threat
            Normal,      // Standard enemy
            Dangerous,   // High damage or health
            Elite,       // Mini-boss level
            Boss         // Full boss encounter
        }

        public class ThreatEntry
        {
            public Character Enemy;
            public float ThreatScore;
            public float HealthPercent;
            public float MaxHealth;
            public bool IsTargetingPlayer;
            public bool IsTargetingCompanion;
            public string TargetedCompanionId;
            public EnemyClassification Classification;
            public int AssignedCompanionCount;
            public float LastDamageDealtTime;
            public float DistanceToPlayer;
            public bool IsValid;  // False if enemy died or was destroyed

            public void Reset()
            {
                Enemy = null;
                ThreatScore = 0f;
                HealthPercent = 1f;
                MaxHealth = 100f;
                IsTargetingPlayer = false;
                IsTargetingCompanion = false;
                TargetedCompanionId = null;
                Classification = EnemyClassification.Normal;
                AssignedCompanionCount = 0;
                LastDamageDealtTime = -999f;
                DistanceToPlayer = 999f;
                IsValid = false;
            }
        }

        #endregion

        #region State

        // Pooled threat entries to avoid allocation
        private readonly List<ThreatEntry> _entries = new List<ThreatEntry>(16);
        private readonly List<ThreatEntry> _entryPool = new List<ThreatEntry>(16);
        private int _activeEntryCount;

        // Assignment tracking: companionId ? enemy Character
        private readonly Dictionary<string, Character> _assignments = new Dictionary<string, Character>();

        // Cached primary target
        private ThreatEntry _primaryTarget;

        // Reusable collider array for Physics scan
        private static readonly Collider[] _scanBuffer = new Collider[64];

        // Classification thresholds (matching ThreatAnalyzer)
        private const float DANGEROUS_HEALTH_THRESHOLD = 300f;
        private const float ELITE_HEALTH_THRESHOLD = 800f;
        private const float BOSS_HEALTH_THRESHOLD = 2000f;

        public static bool VerboseLogging = false;

        #endregion

        #region Properties

        /// <summary>Number of active threat entries.</summary>
        public int ThreatCount => _activeEntryCount;

        /// <summary>The highest-priority target for the group.</summary>
        public ThreatEntry PrimaryTarget => _primaryTarget;

        /// <summary>All active threat entries (read-only slice).</summary>
        public IReadOnlyList<ThreatEntry> AllThreats => _entries;

        #endregion

        #region Update

        /// <summary>
        /// Scans for enemies around the group center and scores all threats.
        /// Call this once per coordinator tick (0.3s).
        /// </summary>
        /// <param name="groupCenter">Center of the group (typically player position).</param>
        /// <param name="scanRadius">Radius to scan for enemies.</param>
        /// <param name="companions">All companions in the group.</param>
        /// <param name="player">The owning player.</param>
        public void UpdateThreats(Vector3 groupCenter, float scanRadius,
            IReadOnlyList<CompanionController> companions, Player player)
        {
            // Reset all entries
            for (int i = 0; i < _entries.Count; i++)
                _entries[i].IsValid = false;
            _activeEntryCount = 0;

            // Single physics scan for the whole group
            int hitCount = Physics.OverlapSphereNonAlloc(groupCenter, scanRadius, _scanBuffer);

            for (int i = 0; i < hitCount; i++)
            {
                if (_scanBuffer[i] == null) continue;

                var character = _scanBuffer[i].GetComponent<Character>();
                if (character == null) character = _scanBuffer[i].GetComponentInParent<Character>();
                if (character == null) continue;

                // Filter: only enemies
                if (!IsValidEnemy(character, player)) continue;

                // Check if we already have an entry for this enemy (dedup from multiple colliders)
                if (HasEntryFor(character)) continue;

                // Get or create entry
                var entry = AcquireEntry();
                PopulateEntry(entry, character, groupCenter, companions, player);
                _activeEntryCount++;
            }

            // Sort by threat score descending
            _entries.Sort((a, b) =>
            {
                if (!a.IsValid && !b.IsValid) return 0;
                if (!a.IsValid) return 1;
                if (!b.IsValid) return -1;
                return b.ThreatScore.CompareTo(a.ThreatScore);
            });

            // Cache primary target
            _primaryTarget = _activeEntryCount > 0 && _entries[0].IsValid ? _entries[0] : null;

            if (VerboseLogging && _activeEntryCount > 0)
                Debug.Log($"[SharedThreatTable] Updated: {_activeEntryCount} threats, primary={_primaryTarget?.Enemy?.m_name ?? "none"} (score={_primaryTarget?.ThreatScore:F0})");
        }

        #endregion

        #region Scoring

        private void PopulateEntry(ThreatEntry entry, Character enemy, Vector3 groupCenter,
            IReadOnlyList<CompanionController> companions, Player player)
        {
            entry.Enemy = enemy;
            entry.IsValid = true;
            entry.MaxHealth = enemy.GetMaxHealth();
            entry.HealthPercent = enemy.GetHealthPercentage();
            entry.DistanceToPlayer = player != null ? Vector3.Distance(enemy.transform.position, player.transform.position) : 999f;
            entry.Classification = ClassifyEnemy(enemy);

            // Check what the enemy is targeting
            entry.IsTargetingPlayer = false;
            entry.IsTargetingCompanion = false;
            entry.TargetedCompanionId = null;

            var enemyAI = enemy.GetComponent<BaseAI>();
            if (enemyAI != null)
            {
                var aiTarget = enemyAI.GetTargetCreature();
                if (aiTarget != null)
                {
                    if (aiTarget.IsPlayer())
                    {
                        entry.IsTargetingPlayer = true;
                    }
                    else
                    {
                        // Check if targeting one of our companions
                        foreach (var comp in companions)
                        {
                            if (comp == null) continue;
                            var compChar = comp.GetCharacter();
                            if (compChar != null && compChar == aiTarget)
                            {
                                entry.IsTargetingCompanion = true;
                                entry.TargetedCompanionId = comp.companionId;
                                break;
                            }
                        }
                    }
                }
            }

            // Preserve assignment count from previous tick
            entry.AssignedCompanionCount = CountAssignmentsTo(enemy);

            // Calculate threat score
            entry.ThreatScore = CalculateThreatScore(entry);
        }

        private float CalculateThreatScore(ThreatEntry entry)
        {
            // Base: closer enemies are higher threat
            float score = Mathf.Max(0f, 100f - entry.DistanceToPlayer * 2f);

            // Targeting modifiers
            if (entry.IsTargetingPlayer) score += 200f;
            if (entry.IsTargetingCompanion) score += 50f;

            // Classification modifiers
            switch (entry.Classification)
            {
                case EnemyClassification.Boss: score += 80f; break;
                case EnemyClassification.Elite: score += 40f; break;
                case EnemyClassification.Dangerous: score += 20f; break;
            }

            // Almost dead: lower priority (don't waste coordination on dying enemies)
            if (entry.HealthPercent < 0.2f) score -= 30f;

            // Unhandled threats get priority (no companions assigned)
            if (entry.AssignedCompanionCount == 0) score += 15f;

            // Currently being attacked — slight bonus for target consistency
            if (entry.LastDamageDealtTime > 0f && (Time.time - entry.LastDamageDealtTime) < 3f)
                score += 10f;

            return Mathf.Max(0f, score);
        }

        private EnemyClassification ClassifyEnemy(Character enemy)
        {
            float maxHp = enemy.GetMaxHealth();

            if (maxHp >= BOSS_HEALTH_THRESHOLD) return EnemyClassification.Boss;
            if (maxHp >= ELITE_HEALTH_THRESHOLD) return EnemyClassification.Elite;
            if (maxHp >= DANGEROUS_HEALTH_THRESHOLD) return EnemyClassification.Dangerous;
            if (maxHp < 80f) return EnemyClassification.Minion;
            return EnemyClassification.Normal;
        }

        #endregion

        #region Assignments

        /// <summary>
        /// Assigns a companion to attack a specific enemy.
        /// </summary>
        public void AssignCompanionToTarget(string companionId, Character enemy)
        {
            if (string.IsNullOrEmpty(companionId) || enemy == null) return;

            // Remove old assignment
            _assignments.Remove(companionId);

            _assignments[companionId] = enemy;

            // Update entry assignment count
            foreach (var entry in _entries)
            {
                if (entry.IsValid && entry.Enemy == enemy)
                {
                    entry.AssignedCompanionCount = CountAssignmentsTo(enemy);
                    break;
                }
            }
        }

        /// <summary>
        /// Clears a companion's target assignment.
        /// </summary>
        public void ClearAssignment(string companionId)
        {
            if (string.IsNullOrEmpty(companionId)) return;

            if (_assignments.TryGetValue(companionId, out var oldTarget))
            {
                _assignments.Remove(companionId);

                // Update entry
                foreach (var entry in _entries)
                {
                    if (entry.IsValid && entry.Enemy == oldTarget)
                    {
                        entry.AssignedCompanionCount = CountAssignmentsTo(oldTarget);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Gets the assigned target for a companion, or null if none.
        /// </summary>
        public Character GetAssignedTarget(string companionId)
        {
            if (string.IsNullOrEmpty(companionId)) return null;
            _assignments.TryGetValue(companionId, out var target);
            if (target != null && (target.IsDead() || !target.gameObject.activeInHierarchy))
            {
                _assignments.Remove(companionId);
                return null;
            }
            return target;
        }

        /// <summary>
        /// Gets all threats that have no companions assigned to them.
        /// </summary>
        public void GetUnassignedThreats(List<ThreatEntry> results)
        {
            results.Clear();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].IsValid && _entries[i].AssignedCompanionCount == 0)
                    results.Add(_entries[i]);
            }
        }

        /// <summary>
        /// Notifies the table that a companion dealt damage to an enemy (for focus fire tracking).
        /// </summary>
        public void RecordDamageDealt(Character enemy)
        {
            if (enemy == null) return;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].IsValid && _entries[i].Enemy == enemy)
                {
                    _entries[i].LastDamageDealtTime = Time.time;
                    return;
                }
            }
        }

        /// <summary>
        /// Clears all assignments and entries. Call when combat ends.
        /// </summary>
        public void Clear()
        {
            _assignments.Clear();
            for (int i = 0; i < _entries.Count; i++)
                _entries[i].Reset();
            _activeEntryCount = 0;
            _primaryTarget = null;
        }

        #endregion

        #region Helpers

        private bool IsValidEnemy(Character character, Player player)
        {
            if (character == null || character.IsDead()) return false;
            if (character.IsPlayer()) return false;
            if (character.IsTamed()) return false;

            // Must be hostile to player
            if (player != null)
            {
                var playerChar = player.GetComponent<Character>();
                if (playerChar != null && !BaseAI.IsEnemy(playerChar, character))
                    return false;
            }

            return true;
        }

        private bool HasEntryFor(Character enemy)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].IsValid && _entries[i].Enemy == enemy)
                    return true;
            }
            return false;
        }

        private int CountAssignmentsTo(Character enemy)
        {
            int count = 0;
            foreach (var kvp in _assignments)
            {
                if (kvp.Value == enemy)
                    count++;
            }
            return count;
        }

        private ThreatEntry AcquireEntry()
        {
            // Try to reuse an invalid entry
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!_entries[i].IsValid)
                    return _entries[i];
            }

            // Try pool
            if (_entryPool.Count > 0)
            {
                var entry = _entryPool[_entryPool.Count - 1];
                _entryPool.RemoveAt(_entryPool.Count - 1);
                entry.Reset();
                _entries.Add(entry);
                return entry;
            }

            // Allocate new
            var newEntry = new ThreatEntry();
            _entries.Add(newEntry);
            return newEntry;
        }

        #endregion
    }
}
