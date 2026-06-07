using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Tracks health state of all group members (companions + player) and provides
    /// healing priority calculations for healer companions.
    /// 
    /// PERFORMANCE:
    /// - Health snapshots are updated in-place (no allocations)
    /// - Damage rate uses exponential moving average (lightweight)
    /// - Priority list is sorted only when requested
    /// </summary>
    public class GroupHealthMonitor
    {
        #region Data Types

        public class HealthSnapshot
        {
            public string Id;                    // CompanionId or "player"
            public Character Character;
            public CompanionController Companion; // Null for player
            public float HealthPercent;
            public float MaxHealth;
            public float CurrentHealth;
            public float PreviousHealth;
            public float DamageRatePerSecond;    // Smoothed damage rate (EMA)
            public float EstimatedTimeToDown;    // Seconds until 0 HP at current damage rate
            public bool IsBeingTargeted;         // An enemy is targeting this member
            public bool IsPlayer;
            public bool IsAlive;
            public float LastUpdateTime;

            public void Reset()
            {
                Character = null;
                Companion = null;
                HealthPercent = 1f;
                MaxHealth = 100f;
                CurrentHealth = 100f;
                PreviousHealth = 100f;
                DamageRatePerSecond = 0f;
                EstimatedTimeToDown = 999f;
                IsBeingTargeted = false;
                IsPlayer = false;
                IsAlive = true;
                LastUpdateTime = 0f;
            }
        }

        #endregion

        #region State

        private readonly Dictionary<string, HealthSnapshot> _snapshots = new Dictionary<string, HealthSnapshot>();
        private readonly List<HealthSnapshot> _sortedPriority = new List<HealthSnapshot>(8);
        private bool _priorityDirty = true;

        // Group-level stats
        private float _groupAverageHealth;
        private float _lowestCompanionHealth;
        private string _mostDamagedId;
        private float _playerHealthPercent;
        private bool _isGroupInDanger;    // Average health < 40%
        private bool _isGroupCritical;    // Any member < 20%

        // Smoothing factor for damage rate EMA
        private const float DAMAGE_RATE_SMOOTHING = 0.3f;

        public static bool VerboseLogging = false;

        #endregion

        #region Properties

        public float GroupAverageHealth => _groupAverageHealth;
        public float LowestCompanionHealth => _lowestCompanionHealth;
        public float PlayerHealthPercent => _playerHealthPercent;
        public bool IsGroupInDanger => _isGroupInDanger;
        public bool IsGroupCritical => _isGroupCritical;
        public string MostDamagedMemberId => _mostDamagedId;

        #endregion

        #region Update

        /// <summary>
        /// Updates health snapshots for all group members.
        /// Call once per coordinator tick.
        /// </summary>
        public void UpdateHealth(IReadOnlyList<CompanionController> companions, Player player,
            SharedThreatTable threatTable)
        {
            _priorityDirty = true;
            float totalHealth = 0f;
            int memberCount = 0;
            _lowestCompanionHealth = 1f;
            _mostDamagedId = null;

            // Update player snapshot
            if (player != null)
            {
                var playerChar = player.GetComponent<Character>();
                if (playerChar != null)
                {
                    var snap = GetOrCreateSnapshot("player");
                    UpdateSnapshot(snap, playerChar, null, true, threatTable);
                    _playerHealthPercent = snap.HealthPercent;
                    totalHealth += snap.HealthPercent;
                    memberCount++;
                }
            }

            // Update companion snapshots
            foreach (var comp in companions)
            {
                if (comp == null) continue;
                var character = comp.GetCharacter();
                if (character == null || character.IsDead()) continue;

                string id = comp.companionId ?? comp.companionName;
                var snap = GetOrCreateSnapshot(id);
                UpdateSnapshot(snap, character, comp, false, threatTable);

                totalHealth += snap.HealthPercent;
                memberCount++;

                if (snap.HealthPercent < _lowestCompanionHealth)
                {
                    _lowestCompanionHealth = snap.HealthPercent;
                    _mostDamagedId = id;
                }
            }

            // Group-level metrics
            _groupAverageHealth = memberCount > 0 ? totalHealth / memberCount : 1f;
            _isGroupInDanger = _groupAverageHealth < 0.4f;
            _isGroupCritical = _lowestCompanionHealth < 0.2f || _playerHealthPercent < 0.2f;

            // Clean up snapshots for removed members
            CleanupStaleSnapshots(companions, player);
        }

        private void UpdateSnapshot(HealthSnapshot snap, Character character,
            CompanionController companion, bool isPlayer, SharedThreatTable threatTable)
        {
            float dt = Time.time - snap.LastUpdateTime;
            if (dt <= 0f) dt = 0.3f; // First update

            snap.Character = character;
            snap.Companion = companion;
            snap.IsPlayer = isPlayer;
            snap.MaxHealth = character.GetMaxHealth();
            snap.CurrentHealth = character.GetHealth();
            snap.HealthPercent = snap.MaxHealth > 0f ? snap.CurrentHealth / snap.MaxHealth : 0f;
            snap.IsAlive = !character.IsDead();

            // Calculate damage rate using exponential moving average
            float healthDelta = snap.PreviousHealth - snap.CurrentHealth;
            float instantRate = dt > 0f ? healthDelta / dt : 0f;
            instantRate = Mathf.Max(0f, instantRate); // Only track damage, not healing

            snap.DamageRatePerSecond = Mathf.Lerp(snap.DamageRatePerSecond, instantRate, DAMAGE_RATE_SMOOTHING);
            snap.PreviousHealth = snap.CurrentHealth;

            // Estimate time to down
            if (snap.DamageRatePerSecond > 0.1f)
                snap.EstimatedTimeToDown = snap.CurrentHealth / snap.DamageRatePerSecond;
            else
                snap.EstimatedTimeToDown = 999f;

            // Check if being targeted
            snap.IsBeingTargeted = false;
            if (threatTable != null)
            {
                foreach (var threat in threatTable.AllThreats)
                {
                    if (!threat.IsValid) continue;

                    if (isPlayer && threat.IsTargetingPlayer)
                    {
                        snap.IsBeingTargeted = true;
                        break;
                    }
                    else if (!isPlayer && companion != null && threat.IsTargetingCompanion
                        && threat.TargetedCompanionId == companion.companionId)
                    {
                        snap.IsBeingTargeted = true;
                        break;
                    }
                }
            }

            snap.LastUpdateTime = Time.time;
        }

        #endregion

        #region Healing Priority

        /// <summary>
        /// Gets members sorted by healing priority (highest priority first).
        /// Only includes members below the given health threshold.
        /// </summary>
        public IReadOnlyList<HealthSnapshot> GetHealingPriority(float healthThreshold = 0.9f)
        {
            if (_priorityDirty)
            {
                _sortedPriority.Clear();
                foreach (var kvp in _snapshots)
                {
                    var snap = kvp.Value;
                    if (!snap.IsAlive) continue;
                    if (snap.HealthPercent >= healthThreshold) continue;
                    _sortedPriority.Add(snap);
                }

                _sortedPriority.Sort((a, b) =>
                {
                    float priorityA = CalculateHealingPriority(a);
                    float priorityB = CalculateHealingPriority(b);
                    return priorityB.CompareTo(priorityA); // Descending
                });

                _priorityDirty = false;
            }

            return _sortedPriority;
        }

        /// <summary>
        /// Gets the member most in need of healing, or null if everyone is healthy.
        /// </summary>
        public HealthSnapshot GetMostUrgentHealTarget(float healthThreshold = 0.8f)
        {
            var priority = GetHealingPriority(healthThreshold);
            return priority.Count > 0 ? priority[0] : null;
        }

        /// <summary>
        /// Gets the health snapshot for a specific member.
        /// </summary>
        public HealthSnapshot GetSnapshot(string id)
        {
            _snapshots.TryGetValue(id, out var snap);
            return snap;
        }

        private float CalculateHealingPriority(HealthSnapshot snap)
        {
            float priority = (1f - snap.HealthPercent) * 100f;

            // Player gets highest priority multiplier
            if (snap.IsPlayer) priority += 40f;

            // Tank bonus — keeping tank alive is critical for group survival
            if (snap.Companion != null)
            {
                var archetype = snap.Companion.GetArchetypeController();
                if (archetype != null && archetype.IsTank)
                    priority += 30f;
            }

            // Under attack bonus
            if (snap.IsBeingTargeted) priority += 20f;

            // Heavy damage rate bonus
            priority += snap.DamageRatePerSecond * 10f;

            // About to die — emergency!
            if (snap.EstimatedTimeToDown < 5f) priority += 50f;

            return priority;
        }

        #endregion

        #region Helpers

        private HealthSnapshot GetOrCreateSnapshot(string id)
        {
            if (!_snapshots.TryGetValue(id, out var snap))
            {
                snap = new HealthSnapshot { Id = id };
                _snapshots[id] = snap;
            }
            return snap;
        }

        private void CleanupStaleSnapshots(IReadOnlyList<CompanionController> companions, Player player)
        {
            var toRemove = new List<string>();
            foreach (var kvp in _snapshots)
            {
                if (kvp.Key == "player")
                {
                    if (player == null) toRemove.Add(kvp.Key);
                    continue;
                }

                bool found = false;
                foreach (var comp in companions)
                {
                    if (comp != null && (comp.companionId == kvp.Key || comp.companionName == kvp.Key))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) toRemove.Add(kvp.Key);
            }

            foreach (var key in toRemove)
                _snapshots.Remove(key);
        }

        /// <summary>
        /// Clears all health data. Call when combat ends.
        /// </summary>
        public void Clear()
        {
            _snapshots.Clear();
            _sortedPriority.Clear();
            _groupAverageHealth = 1f;
            _lowestCompanionHealth = 1f;
            _playerHealthPercent = 1f;
            _isGroupInDanger = false;
            _isGroupCritical = false;
            _mostDamagedId = null;
        }

        #endregion
    }
}
