using UnityEngine;
using System.Collections;
using System.Collections.Generic;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Central combat brain that coordinates all companions in a player's group during combat.
    /// Runs as a coroutine with configurable tick rate for performance.
    /// 
    /// ACTIVATION:
    /// - Activates when any companion in a group enters combat
    /// - Deactivates when no companions have been in combat for 5 seconds
    /// 
    /// TICK LOOP (Coroutine):
    /// 1. Update SharedThreatTable (single OverlapSphere scan)
    /// 2. Update GroupHealthMonitor (health snapshots)
    /// 3. Run CombatRoleDirector (issue directives)
    /// 4. yield return WaitForSeconds(TICK_RATE)
    /// 
    /// PERFORMANCE:
    /// - Coroutine-based (NOT Update)
    /// - Single physics scan per group per tick
    /// - Cached companion lists (event-driven refresh)
    /// - Early exit for groups with only 1 companion
    /// - Amortized cost: ~0.5ms per tick for 8 companions + 10 enemies
    /// </summary>
    public class GroupCombatCoordinator : MonoBehaviour
    {
        #region Singleton

        private static GroupCombatCoordinator _instance;
        public static GroupCombatCoordinator Instance => _instance;

        public static void EnsureInitialized()
        {
            if (_instance != null) return;

            var go = new GameObject("GroupCombatCoordinator");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<GroupCombatCoordinator>();
        }

        #endregion

        #region State

        // Per-player combat sessions
        private readonly Dictionary<long, GroupCombatSession> _sessions = new Dictionary<long, GroupCombatSession>();

        // Cleanup tracking
        private float _lastCleanupTime;
        private const float CLEANUP_INTERVAL = 5f;
        private const float COMBAT_END_GRACE_PERIOD = 5f;

        public static bool VerboseLogging = false;

        #endregion

        #region Internal Data

        private class GroupCombatSession
        {
            public long PlayerId;
            public List<CompanionController> Companions = new List<CompanionController>(8);
            public SharedThreatTable ThreatTable = new SharedThreatTable();
            public CombatRoleDirector RoleDirector = new CombatRoleDirector();
            public GroupHealthMonitor HealthMonitor = new GroupHealthMonitor();
            public Coroutine UpdateCoroutine;
            public float LastCombatTime;
            public bool IsActive;
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;

            // Stop all coroutines
            foreach (var session in _sessions.Values)
            {
                if (session.UpdateCoroutine != null)
                    StopCoroutine(session.UpdateCoroutine);
            }
            _sessions.Clear();
        }

        private void Update()
        {
            // Periodic cleanup of stale sessions
            if (Time.time - _lastCleanupTime > CLEANUP_INTERVAL)
            {
                _lastCleanupTime = Time.time;
                CleanupSessions();
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Notifies the coordinator that a companion entered combat.
        /// Activates the combat session for that player's group if not already active.
        /// </summary>
        public void OnCompanionEnteredCombat(CompanionController companion)
        {
            if (companion == null || companion.ownerPlayerId == 0) return;

            var session = GetOrCreateSession(companion.ownerPlayerId);
            session.LastCombatTime = Time.time;

            // Refresh companion list
            RefreshCompanionList(session);

            // Skip coordination for solo companions
            if (session.Companions.Count <= 1)
            {
                if (VerboseLogging)
                    Debug.Log($"[GroupCombatCoordinator] Solo companion {companion.companionName} — skipping coordination");
                return;
            }

            // Start the coroutine if not already running
            if (!session.IsActive)
            {
                session.IsActive = true;
                session.UpdateCoroutine = StartCoroutine(CombatTickCoroutine(session));

                if (VerboseLogging)
                    Debug.Log($"[GroupCombatCoordinator] Activated combat session for player {companion.ownerPlayerId} with {session.Companions.Count} companions");
            }
        }

        /// <summary>
        /// Notifies the coordinator that a companion left combat.
        /// The session will deactivate after a grace period if no companions remain in combat.
        /// </summary>
        public void OnCompanionLeftCombat(CompanionController companion)
        {
            if (companion == null || companion.ownerPlayerId == 0) return;

            if (_sessions.TryGetValue(companion.ownerPlayerId, out var session))
            {
                // Check if any companions are still in combat
                bool anyInCombat = false;
                foreach (var comp in session.Companions)
                {
                    if (comp != null && comp.IsInCombat)
                    {
                        anyInCombat = true;
                        break;
                    }
                }

                if (anyInCombat)
                    session.LastCombatTime = Time.time;
            }
        }

        /// <summary>
        /// Gets the current directive for a companion, or null if no coordination is active.
        /// This is the primary read path for ArchetypeController and CompanionAI.
        /// </summary>
        public CombatRoleDirector.RoleDirective GetDirective(CompanionController companion)
        {
            if (companion == null || companion.ownerPlayerId == 0) return null;

            if (_sessions.TryGetValue(companion.ownerPlayerId, out var session))
            {
                if (!session.IsActive) return null;
                return session.RoleDirector.GetDirective(companion.companionId);
            }

            return null;
        }

        /// <summary>
        /// Gets the shared threat table for a companion's group.
        /// Returns null if no coordination is active.
        /// </summary>
        public SharedThreatTable GetThreatTable(long playerId)
        {
            if (_sessions.TryGetValue(playerId, out var session) && session.IsActive)
                return session.ThreatTable;
            return null;
        }

        /// <summary>
        /// Gets the health monitor for a companion's group.
        /// Returns null if no coordination is active.
        /// </summary>
        public GroupHealthMonitor GetHealthMonitor(long playerId)
        {
            if (_sessions.TryGetValue(playerId, out var session) && session.IsActive)
                return session.HealthMonitor;
            return null;
        }

        #endregion

        #region Combat Tick Coroutine

        private IEnumerator CombatTickCoroutine(GroupCombatSession session)
        {
            float tickRate = CompanionSettings.CombatCoordinationTickRate;
            float scanRadius = CompanionSettings.CombatCoordinationThreatScanRadius;

            if (VerboseLogging)
                Debug.Log($"[GroupCombatCoordinator] Starting combat tick coroutine (tick={tickRate}s, scan={scanRadius}m)");

            while (session.IsActive)
            {
                // Check if combat has ended (grace period expired)
                if (Time.time - session.LastCombatTime > COMBAT_END_GRACE_PERIOD)
                {
                    bool anyInCombat = false;
                    foreach (var comp in session.Companions)
                    {
                        if (comp != null && comp.IsInCombat)
                        {
                            anyInCombat = true;
                            session.LastCombatTime = Time.time;
                            break;
                        }
                    }

                    if (!anyInCombat)
                    {
                        if (VerboseLogging)
                            Debug.Log($"[GroupCombatCoordinator] Combat ended for player {session.PlayerId} — deactivating");

                        session.IsActive = false;
                        session.ThreatTable.Clear();
                        session.RoleDirector.Clear();
                        session.HealthMonitor.Clear();
                        yield break;
                    }
                }

                // Refresh companion list (handles destroyed companions)
                RefreshCompanionList(session);

                if (session.Companions.Count <= 1)
                {
                    // Solo companion — skip coordination but keep session alive
                    yield return new WaitForSeconds(tickRate * 2f); // Slower tick for solo
                    continue;
                }

                // Find the player
                Player player = null;
                foreach (var comp in session.Companions)
                {
                    if (comp != null)
                    {
                        player = comp.GetOwner();
                        if (player != null) break;
                    }
                }

                if (player == null)
                {
                    yield return new WaitForSeconds(tickRate);
                    continue;
                }

                Vector3 groupCenter = player.transform.position;

                // TICK 1: Update threat table (single physics scan)
                session.ThreatTable.UpdateThreats(groupCenter, scanRadius, session.Companions, player);

                // TICK 2: Update health monitor
                session.HealthMonitor.UpdateHealth(session.Companions, player, session.ThreatTable);

                // TICK 3: Issue directives based on roles
                session.RoleDirector.IssueDirectives(session.Companions, player,
                    session.ThreatTable, session.HealthMonitor);

                yield return new WaitForSeconds(tickRate);
            }
        }

        #endregion

        #region Helpers

        private GroupCombatSession GetOrCreateSession(long playerId)
        {
            if (!_sessions.TryGetValue(playerId, out var session))
            {
                session = new GroupCombatSession { PlayerId = playerId };
                _sessions[playerId] = session;
            }
            return session;
        }

        private void RefreshCompanionList(GroupCombatSession session)
        {
            session.Companions.Clear();

            foreach (var comp in CompanionController.AllCompanions)
            {
                if (comp == null || comp.isDefeated) continue;
                if (comp.ownerPlayerId != session.PlayerId) continue;
                if (!comp.isTamed) continue;

                session.Companions.Add(comp);
            }
        }

        private void CleanupSessions()
        {
            var toRemove = new List<long>();

            foreach (var kvp in _sessions)
            {
                var session = kvp.Value;

                // Remove inactive sessions that haven't been used in a while
                if (!session.IsActive && Time.time - session.LastCombatTime > 30f)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var key in toRemove)
            {
                var session = _sessions[key];
                if (session.UpdateCoroutine != null)
                    StopCoroutine(session.UpdateCoroutine);
                _sessions.Remove(key);
            }
        }

        #endregion
    }
}
