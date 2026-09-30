using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Archetypes;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Issues role-specific combat directives to each companion based on their archetype
    /// and the current battlefield state. Directives are suggestions — companion AI checks
    /// the directive but falls back to independent behavior if no directive is present.
    /// 
    /// PERFORMANCE:
    /// - Directives are issued once per coordinator tick (0.3s)
    /// - Auto-expire after DirectiveExpiryTime (3s) to prevent stale commands
    /// - Dictionary-based lookups for O(1) directive reads per companion
    /// </summary>
    public class CombatRoleDirector
    {
        #region Data Types

        public enum CombatDirective
        {
            // Universal
            FreeAction,       // No directive — act independently
            Retreat,          // Fall back to player
            Regroup,          // All companions gather near player

            // Tank directives
            EngageAndTaunt,   // Move to target, use taunt ability
            InterceptThreat,  // Get between enemy and protected target
            HoldPosition,     // Block and hold current position

            // DPS directives
            FocusTarget,      // Attack the assigned target
            FlankTarget,      // Attack from a different angle than other melee
            Assist,           // Help whoever needs it most

            // Healer directives
            HealTarget,       // Heal a specific ally
            BuffGroup,        // Use group buff ability
            StayProtected,    // Position behind tank, maintain safe distance

            // Support directives
            DebuffEnemy,      // Apply debuff to priority target
            BuffAlly,         // Buff a specific companion
            CrowdControl      // CC adds/secondary targets
        }

        public class RoleDirective
        {
            public CombatDirective Directive;
            public Character TargetEnemy;         // For attack/debuff directives
            public CompanionController TargetAlly; // For heal/buff directives
            public Vector3 TargetPosition;         // For positioning directives
            public float Priority;                 // Higher = more important
            public float IssuedTime;
            public float ExpiryTime;
            public float FlankAngle;               // For FlankTarget: angle offset around enemy

            public bool IsExpired => Time.time > ExpiryTime;
            public bool IsValid => !IsExpired && Directive != CombatDirective.FreeAction;

            public void Set(CombatDirective directive, float expiryDuration)
            {
                Directive = directive;
                IssuedTime = Time.time;
                ExpiryTime = Time.time + expiryDuration;
                TargetEnemy = null;
                TargetAlly = null;
                TargetPosition = Vector3.zero;
                Priority = 0f;
                FlankAngle = 0f;
                HasFlank = false;
            }

            // FlankAngle is a world yaw; 0 (north) is a real angle, so "assigned" is this flag, not FlankAngle != 0.
            public bool HasFlank;
        }

        #endregion

        #region State

        // One directive per companion
        private readonly Dictionary<string, RoleDirective> _directives = new Dictionary<string, RoleDirective>();

        // Reusable lists for melee companion tracking
        private readonly List<CompanionController> _meleeCompanions = new List<CompanionController>(4);

        private float _directiveExpiryTime;

        public static bool VerboseLogging = false;

        #endregion

        #region Issue Directives

        /// <summary>
        /// Analyzes the battlefield and issues directives to all companions.
        /// Call once per coordinator tick.
        /// </summary>
        public void IssueDirectives(
            IReadOnlyList<CompanionController> companions,
            Player player,
            SharedThreatTable threatTable,
            GroupHealthMonitor healthMonitor)
        {
            _directiveExpiryTime = CompanionSettings.CombatCoordinationDirectiveExpiryTime;
            int maxPerTarget = CompanionSettings.CombatCoordinationMaxCompanionsPerTarget;

            // Check for emergency regroup conditions
            if (ShouldRegroup(healthMonitor, player))
            {
                LogSession(companions, player, threatTable, healthMonitor, true);
                IssueRegroupToAll(companions);
                return;
            }

            // Track melee companions for flanking coordination
            _meleeCompanions.Clear();

            foreach (var companion in companions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (!companion.ShouldBeFollowing && !companion.IsInCombat) continue;

                var archetype = companion.GetArchetypeController();
                if (archetype == null)
                {
                    IssueFreeAction(companion);
                    continue;
                }

                if (archetype.IsTank)
                {
                    IssueTankDirective(companion, archetype, player, threatTable, healthMonitor);
                    // Tanks spread with the melee too (they took the same spot as the first DPS before 0.2.201).
                    if (archetype.IsMelee)
                        _meleeCompanions.Add(companion);
                }
                else if (archetype.IsSupport)
                    IssueSupportDirective(companion, archetype, player, threatTable, healthMonitor);
                else if (archetype.IsDPS)
                {
                    IssueDpsDirective(companion, archetype, threatTable, maxPerTarget);
                    if (archetype.IsMelee)
                        _meleeCompanions.Add(companion);
                }
                else
                    IssueFreeAction(companion);
            }

            // Assign flanking angles to melee companions attacking the same target
            AssignFlankingAngles(player);
            LogSession(companions, player, threatTable, healthMonitor, false);
        }

        #endregion

        #region Role-Specific Logic

        private void IssueTankDirective(CompanionController companion, ArchetypeController archetype,
            Player player, SharedThreatTable threatTable, GroupHealthMonitor healthMonitor)
        {
            var directive = GetOrCreateDirective(companion);

            // Priority 1: Intercept enemies targeting the player
            if (threatTable.PrimaryTarget != null && threatTable.PrimaryTarget.IsTargetingPlayer)
            {
                directive.Set(CombatDirective.InterceptThreat, _directiveExpiryTime);
                directive.TargetEnemy = threatTable.PrimaryTarget.Enemy;
                directive.Priority = 100f;
                threatTable.AssignCompanionToTarget(companion.companionId, directive.TargetEnemy);
                return;
            }

            // Priority 2: Intercept enemies targeting support/healer companions
            foreach (var threat in threatTable.AllThreats)
            {
                if (!threat.IsValid || !threat.IsTargetingCompanion) continue;

                // Check if the targeted companion is a support/healer
                var targetedComp = FindCompanionById(threat.TargetedCompanionId, companion);
                if (targetedComp != null)
                {
                    var targetedArchetype = targetedComp.GetArchetypeController();
                    if (targetedArchetype != null && targetedArchetype.IsSupport)
                    {
                        directive.Set(CombatDirective.InterceptThreat, _directiveExpiryTime);
                        directive.TargetEnemy = threat.Enemy;
                        directive.Priority = 80f;
                        threatTable.AssignCompanionToTarget(companion.companionId, directive.TargetEnemy);
                        return;
                    }
                }
            }

            // Priority 3: Tank health is low and healer exists — hold position
            var tankHealth = healthMonitor.GetSnapshot(companion.companionId ?? companion.companionName);
            if (tankHealth != null && tankHealth.HealthPercent < 0.3f)
            {
                directive.Set(CombatDirective.HoldPosition, _directiveExpiryTime);
                directive.Priority = 70f;
                return;
            }

            // Default: Engage and taunt the highest-threat enemy
            if (threatTable.PrimaryTarget != null)
            {
                directive.Set(CombatDirective.EngageAndTaunt, _directiveExpiryTime);
                directive.TargetEnemy = threatTable.PrimaryTarget.Enemy;
                directive.Priority = 60f;
                threatTable.AssignCompanionToTarget(companion.companionId, directive.TargetEnemy);
            }
            else
            {
                IssueFreeAction(companion);
            }
        }

        private void IssueSupportDirective(CompanionController companion, ArchetypeController archetype,
            Player player, SharedThreatTable threatTable, GroupHealthMonitor healthMonitor)
        {
            var directive = GetOrCreateDirective(companion);

            // Priority 1: Heal the most urgent target
            var urgentTarget = healthMonitor.GetMostUrgentHealTarget(0.6f);
            if (urgentTarget != null)
            {
                directive.Set(CombatDirective.HealTarget, _directiveExpiryTime);
                directive.TargetAlly = urgentTarget.Companion;
                directive.Priority = 90f;

                // If target is the player, use player position
                if (urgentTarget.IsPlayer && player != null)
                    directive.TargetPosition = player.transform.position;
                else if (urgentTarget.Companion != null)
                    directive.TargetPosition = urgentTarget.Companion.transform.position;

                return;
            }

            // Priority 2: Healer is being targeted — retreat toward tank
            foreach (var threat in threatTable.AllThreats)
            {
                if (!threat.IsValid) continue;
                if (threat.IsTargetingCompanion && threat.TargetedCompanionId == companion.companionId)
                {
                    directive.Set(CombatDirective.StayProtected, _directiveExpiryTime);
                    directive.Priority = 80f;

                    // Position behind the player
                    if (player != null)
                    {
                        Vector3 playerBack = -player.transform.forward;
                        directive.TargetPosition = player.transform.position + playerBack * CompanionSettings.CombatCoordinationHealerSafeDistance * 0.5f;
                    }
                    return;
                }
            }

            // Priority 3: Everyone healthy — buff group or stay protected
            if (healthMonitor.GroupAverageHealth > 0.8f)
            {
                directive.Set(CombatDirective.BuffGroup, _directiveExpiryTime);
                directive.Priority = 30f;
            }
            else
            {
                directive.Set(CombatDirective.StayProtected, _directiveExpiryTime);
                directive.Priority = 40f;
                if (player != null)
                    directive.TargetPosition = player.transform.position - player.transform.forward * 3f;
            }
        }

        private void IssueDpsDirective(CompanionController companion, ArchetypeController archetype,
            SharedThreatTable threatTable, int maxPerTarget)
        {
            var directive = GetOrCreateDirective(companion);

            // Check if we have a current target from the threat table
            var assignedTarget = threatTable.GetAssignedTarget(companion.companionId);

            // The foe on the owner first (0.2.204, Fire: "two companions on one weak foe while another hits the owner" is wrong): a threat
            // attacking the player beats the current pick unless that pick is on the player too.
            SharedThreatTable.ThreatEntry onOwner = null, assignedEntry = null;
            foreach (var threat in threatTable.AllThreats)
            {
                if (!threat.IsValid) continue;
                if (threat.Enemy == assignedTarget) assignedEntry = threat;
                if (threat.IsTargetingPlayer && threat.AssignedCompanionCount < maxPerTarget && (onOwner == null || threat.ThreatScore > onOwner.ThreatScore))
                    onOwner = threat;
            }
            if (onOwner != null && (assignedEntry == null || !assignedEntry.IsTargetingPlayer) && onOwner.Enemy != assignedTarget)
            {
                directive.Set(CombatDirective.FocusTarget, _directiveExpiryTime);
                directive.TargetEnemy = onOwner.Enemy;
                directive.Priority = 70f;
                threatTable.AssignCompanionToTarget(companion.companionId, onOwner.Enemy);
                Debug.Log($"[GroupTactics] {companion.companionName} target {onOwner.Enemy.m_name} because it is on the owner" +
                          $"{(assignedTarget != null ? $" (was {assignedTarget.m_name})" : "")}");
                return;
            }

            // If current target is still alive and not almost dead, keep it (target consistency)
            if (assignedTarget != null && !assignedTarget.IsDead())
            {
                var currentHealthPct = assignedTarget.GetHealthPercentage();
                if (currentHealthPct > 0.15f)
                {
                    directive.Set(CombatDirective.FocusTarget, _directiveExpiryTime);
                    directive.TargetEnemy = assignedTarget;
                    directive.Priority = 50f;
                    return;
                }
            }

            // Find best target: prefer primary, respect max-per-target
            int focusFireThreshold = CompanionSettings.CombatCoordinationFocusFireThreshold;

            if (threatTable.ThreatCount <= focusFireThreshold && threatTable.PrimaryTarget != null)
            {
                // Few enemies — focus fire on primary
                var primary = threatTable.PrimaryTarget;
                if (primary.AssignedCompanionCount < maxPerTarget)
                {
                    directive.Set(CombatDirective.FocusTarget, _directiveExpiryTime);
                    directive.TargetEnemy = primary.Enemy;
                    directive.Priority = 60f;
                    threatTable.AssignCompanionToTarget(companion.companionId, primary.Enemy);
                    return;
                }
            }

            // Many enemies or primary is full — find least-covered target
            SharedThreatTable.ThreatEntry bestTarget = null;
            float bestScore = -1f;

            foreach (var threat in threatTable.AllThreats)
            {
                if (!threat.IsValid) continue;
                if (threat.AssignedCompanionCount >= maxPerTarget) continue;

                float score = threat.ThreatScore - (threat.AssignedCompanionCount * 50f);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = threat;
                }
            }

            if (bestTarget != null)
            {
                directive.Set(CombatDirective.FocusTarget, _directiveExpiryTime);
                directive.TargetEnemy = bestTarget.Enemy;
                directive.Priority = 50f;
                threatTable.AssignCompanionToTarget(companion.companionId, bestTarget.Enemy);
            }
            else
            {
                directive.Set(CombatDirective.Assist, _directiveExpiryTime);
                directive.Priority = 20f;
            }
        }

        #endregion

        #region Flanking

        /// <summary>
        /// Assigns flanking angles to melee companions (tanks included) attacking the same target, measured from the foe's side
        /// that faces the owner: a tank takes that front (between the foe and the owner), the others spread evenly around, so they
        /// ring the enemy instead of stacking. Without a tank the ring is turned half a step so nobody stands in the owner's line.
        /// </summary>
        private void AssignFlankingAngles(Player player)
        {
            // The STACK watchdog over every melee companion with a target (0.2.204).
            GroupTactics.Watch(_meleeCompanions, c => GetDirective(c.companionId)?.TargetEnemy);
            // One melee companion too (0.2.204): it keeps out of its owner's line of attack.
            if (_meleeCompanions.Count < 1) return;

            // Group melee companions by their target
            var targetGroups = new Dictionary<Character, List<CompanionController>>();

            foreach (var companion in _meleeCompanions)
            {
                var directive = GetDirective(companion.companionId);
                if (directive == null || directive.TargetEnemy == null) continue;

                if (!targetGroups.TryGetValue(directive.TargetEnemy, out var group))
                {
                    group = new List<CompanionController>(4);
                    targetGroups[directive.TargetEnemy] = group;
                }
                group.Add(companion);
            }

            // Slots round each foe (0.2.204, GroupTactics): from the owner's side, the owner's line kept clear, slots taken by anyone
            // already standing there left alone, each companion's slot kept while it's free. "[GroupTactics] <name> slot …" per change.
            foreach (var kvp in targetGroups)
            {
                var group = kvp.Value;
                _slotYaws.Clear();
                GroupTactics.AssignSlots(kvp.Key, player, group, IsTank, _slotYaws);
                foreach (var companion in group)
                {
                    var directive = GetDirective(companion.companionId);
                    if (directive == null || !_slotYaws.TryGetValue(companion, out float yaw)) continue;
                    // A tank keeps its intercept/taunt directive; only the melee DPS switch to FlankTarget.
                    if (!IsTank(companion)) directive.Directive = CombatDirective.FlankTarget;
                    directive.FlankAngle = yaw;
                    directive.HasFlank = true;
                }
            }
        }

        private readonly Dictionary<CompanionController, float> _slotYaws = new Dictionary<CompanionController, float>();

        private readonly Dictionary<int, string> _flankLogged = new Dictionary<int, string>();

        private static bool IsTank(CompanionController companion)
        {
            var archetype = companion != null ? companion.GetArchetypeController() : null;
            return archetype != null && archetype.IsTank;
        }

        #endregion

        #region Emergency

        // One line per group every SessionLogSeconds while its combat session ticks (0.2.208, R79: companions fought and not one
        // "[GroupTactics]" line came out; this names the gate that closed: no threats, a forced regroup, no melee with a target).
        private const float SessionLogSeconds = 10f;
        private float _sessionLoggedAt = -999f;

        private void LogSession(IReadOnlyList<CompanionController> companions, Player player, SharedThreatTable threats, GroupHealthMonitor health, bool regroup)
        {
            if (Time.time - _sessionLoggedAt < SessionLogSeconds) return;
            _sessionLoggedAt = Time.time;
            int withTarget = 0;
            foreach (var c in _meleeCompanions)
            {
                var d = c != null ? GetDirective(c.companionId) : null;
                if (d != null && d.TargetEnemy != null) withTarget++;
            }
            Debug.Log($"[GroupTactics] group of {(player != null ? player.GetPlayerName() : "?")}: {companions.Count} companion(s), {threats.ThreatCount} threat(s), " +
                      $"owner hp {health.PlayerHealthPercent * 100f:0}%, {(regroup ? "REGROUP (no slots this tick)" : $"{_meleeCompanions.Count} melee, {withTarget} with a target")}");
        }

        private bool ShouldRegroup(GroupHealthMonitor healthMonitor, Player player)
        {
            // Player critically low
            if (healthMonitor.PlayerHealthPercent < CompanionSettings.CombatCoordinationRegroupHealthThreshold)
                return true;

            // Group is critical
            if (healthMonitor.IsGroupCritical && healthMonitor.IsGroupInDanger)
                return true;

            return false;
        }

        private void IssueRegroupToAll(IReadOnlyList<CompanionController> companions)
        {
            foreach (var companion in companions)
            {
                if (companion == null) continue;
                var directive = GetOrCreateDirective(companion);
                directive.Set(CombatDirective.Regroup, _directiveExpiryTime);
                directive.Priority = 200f; // Override everything
            }

            if (VerboseLogging)
                Debug.Log("[CombatRoleDirector] EMERGENCY REGROUP issued to all companions!");
        }

        #endregion

        #region Query

        /// <summary>
        /// Gets the current directive for a companion, or null if none/expired.
        /// </summary>
        public RoleDirective GetDirective(string companionId)
        {
            if (string.IsNullOrEmpty(companionId)) return null;
            if (_directives.TryGetValue(companionId, out var directive))
            {
                if (directive.IsExpired)
                    return null;
                return directive;
            }
            return null;
        }

        /// <summary>
        /// Clears all directives. Call when combat ends.
        /// </summary>
        public void Clear()
        {
            _directives.Clear();
            _meleeCompanions.Clear();
            _flankLogged.Clear();
        }

        #endregion

        #region Helpers

        private RoleDirective GetOrCreateDirective(CompanionController companion)
        {
            string id = companion.companionId ?? companion.companionName;
            if (!_directives.TryGetValue(id, out var directive))
            {
                directive = new RoleDirective();
                _directives[id] = directive;
            }
            return directive;
        }

        private void IssueFreeAction(CompanionController companion)
        {
            var directive = GetOrCreateDirective(companion);
            directive.Set(CombatDirective.FreeAction, _directiveExpiryTime);
        }

        private CompanionController FindCompanionById(string companionId, CompanionController anyGroupMember)
        {
            if (string.IsNullOrEmpty(companionId) || anyGroupMember == null) return null;

            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null) continue;
                if (companion.companionId == companionId && companion.ownerPlayerId == anyGroupMember.ownerPlayerId)
                    return companion;
            }
            return null;
        }

        #endregion
    }
}
