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
            }
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
                    IssueTankDirective(companion, archetype, player, threatTable, healthMonitor);
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
            AssignFlankingAngles();
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
        /// Assigns flanking angles to melee companions attacking the same target.
        /// Ensures they spread around the enemy instead of stacking.
        /// </summary>
        private void AssignFlankingAngles()
        {
            if (_meleeCompanions.Count < 2) return;

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

            // Assign angle offsets for groups with 2+ melee
            foreach (var kvp in targetGroups)
            {
                var group = kvp.Value;
                if (group.Count < 2) continue;

                float angleStep = 360f / group.Count;
                for (int i = 0; i < group.Count; i++)
                {
                    var directive = GetDirective(group[i].companionId);
                    if (directive == null) continue;

                    directive.Directive = CombatDirective.FlankTarget;
                    directive.FlankAngle = angleStep * i;
                }

                if (VerboseLogging)
                    Debug.Log($"[CombatRoleDirector] Assigned flanking angles to {group.Count} melee companions on {kvp.Key.m_name}: step={angleStep:F0}°");
            }
        }

        #endregion

        #region Emergency

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
