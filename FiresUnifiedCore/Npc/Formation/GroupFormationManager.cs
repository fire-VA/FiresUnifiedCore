using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FiresCore.Npc.Archetypes;

namespace FiresCore.Npc.Formation
{
    /// <summary>
    /// Centralized formation coordinator for all companions belonging to a player.
    /// Manages formation slot assignment, offset computation, and mode transitions.
    /// 
    /// PERFORMANCE:
    /// - Offsets are pre-computed every UPDATE_INTERVAL (0.5s), not per-frame
    /// - Companion lists are cached and only refreshed on add/remove events
    /// - Early exit when group has only 1 companion (no formation needed)
    /// - Formation offsets only recomputed when player direction changes significantly
    /// 
    /// FORMATION MODES:
    /// - Following: V-pattern behind/beside the player during travel
    /// - IdleSpread: Random spread ±3m around stopped player, with minimum separation
    /// - Combat: Role-based positioning (tank front, ranged/healer back)
    /// - None: Companion not in formation (Stay mode, working, etc.)
    /// </summary>
    public class GroupFormationManager
    {
        #region Singleton

        private static GroupFormationManager _instance;
        public static GroupFormationManager Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new GroupFormationManager();
                return _instance;
            }
        }

        #endregion

        #region Settings

        // Settings are read from CompanionSettings for centralized configuration.
        // Local fields are synced on each update tick to avoid per-call property lookups.
        public float PersonalSpaceRadius;
        public float SeparationStrength;
        public float SeparationBlendWeight;

        public float FollowOffsetBehind;
        public float FollowOffsetSpacing;
        public float FollowOffsetMaxBehind;
        public float DirectionChangeThreshold;

        public float IdleSpreadRadius;
        public float IdleSpreadMinSeparation;

        public float UpdateInterval;

        public static bool VerboseLogging = false;

        /// <summary>
        /// Syncs local fields from CompanionSettings. Called once per update tick.
        /// </summary>
        private void SyncSettings()
        {
            PersonalSpaceRadius = CompanionSettings.FormationPersonalSpaceRadius;
            SeparationStrength = CompanionSettings.FormationSeparationStrength;
            SeparationBlendWeight = CompanionSettings.FormationSeparationBlendWeight;
            FollowOffsetBehind = CompanionSettings.FormationFollowOffsetBehind;
            FollowOffsetSpacing = CompanionSettings.FormationFollowOffsetSpacing;
            FollowOffsetMaxBehind = CompanionSettings.FormationFollowOffsetMaxBehind;
            DirectionChangeThreshold = CompanionSettings.FormationDirectionChangeThreshold;
            IdleSpreadRadius = CompanionSettings.FormationIdleSpreadRadius;
            IdleSpreadMinSeparation = CompanionSettings.FormationIdleSpreadMinSeparation;
            UpdateInterval = CompanionSettings.FormationUpdateInterval;
        }

        #endregion

        #region State

        // Per-player group data
        private readonly Dictionary<long, PlayerFormationGroup> _groups = new Dictionary<long, PlayerFormationGroup>();

        // Cached slot lookups by companion ID for O(1) reads
        private readonly Dictionary<string, FormationSlot> _slotsByCompanionId = new Dictionary<string, FormationSlot>();

        // Reusable list to avoid allocations during separation calculations
        private static readonly List<Vector3> _tempPositions = new List<Vector3>(8);

        #endregion

        #region Internal Data

        private class PlayerFormationGroup
        {
            public long PlayerId;
            public List<CompanionController> Companions = new List<CompanionController>();
            public List<FormationSlot> Slots = new List<FormationSlot>();
            public FormationMode CurrentMode = FormationMode.None;
            public float LastUpdateTime = -999f;
            public Vector3 LastPlayerForward;
            public Vector3 LastPlayerPosition;
            public bool IdleSpreadAssigned;
        }

        #endregion

        #region Registration

        /// <summary>
        /// Registers a companion into its owner's formation group.
        /// Called from CompanionFormationController.OnEnable().
        /// </summary>
        public void Register(CompanionController companion)
        {
            if (companion == null || companion.ownerPlayerId == 0) return;

            long playerId = companion.ownerPlayerId;
            if (!_groups.TryGetValue(playerId, out var group))
            {
                group = new PlayerFormationGroup { PlayerId = playerId };
                _groups[playerId] = group;
            }

            if (group.Companions.Contains(companion)) return;

            group.Companions.Add(companion);

            // Create a new slot
            var slot = new FormationSlot
            {
                CompanionId = companion.companionId,
                SlotIndex = group.Slots.Count,
                Mode = FormationMode.None
            };
            group.Slots.Add(slot);
            _slotsByCompanionId[companion.companionId] = slot;

            // Reassign slot indices based on role priority
            ReassignSlotIndices(group);

            if (VerboseLogging)
                Debug.Log($"[GroupFormationManager] Registered {companion.companionName} into group for player {playerId} (slot {slot.SlotIndex}, {group.Companions.Count} total)");
        }

        /// <summary>
        /// Unregisters a companion from its owner's formation group.
        /// Called from CompanionFormationController.OnDisable/OnDestroy().
        /// </summary>
        public void Unregister(CompanionController companion)
        {
            if (companion == null) return;

            long playerId = companion.ownerPlayerId;
            if (!_groups.TryGetValue(playerId, out var group)) return;

            int idx = group.Companions.IndexOf(companion);
            if (idx < 0) return;

            group.Companions.RemoveAt(idx);
            if (idx < group.Slots.Count)
                group.Slots.RemoveAt(idx);

            _slotsByCompanionId.Remove(companion.companionId ?? "");

            // Reassign slot indices
            ReassignSlotIndices(group);

            // Clean up empty groups
            if (group.Companions.Count == 0)
                _groups.Remove(playerId);

            if (VerboseLogging)
                Debug.Log($"[GroupFormationManager] Unregistered {companion.companionName} from group for player {playerId} ({group.Companions.Count} remaining)");
        }

        #endregion

        #region Slot Queries

        /// <summary>
        /// Gets the formation slot for a companion by ID. Returns null if not registered.
        /// This is the primary read path — called by CompanionFormationController.
        /// </summary>
        public FormationSlot GetSlot(string companionId)
        {
            if (string.IsNullOrEmpty(companionId)) return null;
            _slotsByCompanionId.TryGetValue(companionId, out var slot);
            return slot;
        }

        /// <summary>
        /// Gets all companions in a player's group. Returns empty list if no group.
        /// </summary>
        public IReadOnlyList<CompanionController> GetGroupCompanions(long playerId)
        {
            if (_groups.TryGetValue(playerId, out var group))
                return group.Companions;
            return System.Array.Empty<CompanionController>();
        }

        /// <summary>
        /// Gets the number of companions in a player's group.
        /// </summary>
        public int GetGroupSize(long playerId)
        {
            if (_groups.TryGetValue(playerId, out var group))
                return group.Companions.Count;
            return 0;
        }

        #endregion

        #region Update (Throttled)

        /// <summary>
        /// Updates formation positions for all groups.
        /// Called from CompanionFormationController at a throttled rate.
        /// </summary>
        public void UpdateFormations()
        {
            SyncSettings();
            
            foreach (var kvp in _groups)
            {
                var group = kvp.Value;
                if (group.Companions.Count <= 0) continue;

                float timeSinceUpdate = Time.time - group.LastUpdateTime;
                if (timeSinceUpdate < UpdateInterval) continue;
                group.LastUpdateTime = Time.time;

                // Find the player
                Player player = null;
                foreach (var comp in group.Companions)
                {
                    if (comp != null)
                    {
                        player = comp.GetOwner();
                        if (player != null) break;
                    }
                }
                if (player == null) continue;

                // Determine formation mode from companions' states
                FormationMode mode = DetermineGroupMode(group);
                bool modeChanged = mode != group.CurrentMode;
                group.CurrentMode = mode;

                switch (mode)
                {
                    case FormationMode.Following:
                        UpdateFollowingFormation(group, player, modeChanged);
                        break;
                    case FormationMode.IdleSpread:
                        UpdateIdleSpreadFormation(group, player, modeChanged);
                        break;
                    case FormationMode.Combat:
                        // Combat positioning handled by GroupCombatCoordinator (Phase 3)
                        // For now, clear offsets so companions use their default combat movement
                        ClearFormationOffsets(group);
                        break;
                    default:
                        ClearFormationOffsets(group);
                        break;
                }
            }
        }

        #endregion

        #region Mode Detection

        private FormationMode DetermineGroupMode(PlayerFormationGroup group)
        {
            bool anyInCombat = false;
            bool anyFollowing = false;
            bool allIdle = true;

            foreach (var comp in group.Companions)
            {
                if (comp == null) continue;

                if (comp.IsInCombat)
                {
                    anyInCombat = true;
                    allIdle = false;
                }
                else if (comp.ShouldBeFollowing)
                {
                    anyFollowing = true;
                }

                // If companion is not following (Stay mode, working), it's excluded from formation
                if (!comp.ShouldBeFollowing)
                {
                    // Mark this companion's slot as None
                    var slot = GetSlot(comp.companionId);
                    if (slot != null) slot.Mode = FormationMode.None;
                }
            }

            if (anyInCombat) return FormationMode.Combat;

            if (anyFollowing)
            {
                // Check if the player is idle
                Player player = null;
                foreach (var comp in group.Companions)
                {
                    if (comp != null)
                    {
                        player = comp.GetOwner();
                        if (player != null) break;
                    }
                }

                if (player != null && IsPlayerIdle(player, group))
                    return FormationMode.IdleSpread;

                return FormationMode.Following;
            }

            return FormationMode.None;
        }

        private bool IsPlayerIdle(Player player, PlayerFormationGroup group)
        {
            Vector3 currentPos = player.transform.position;
            float moved = Vector3.Distance(currentPos, group.LastPlayerPosition);

            // Player moved significantly — not idle
            if (moved > 0.3f)
            {
                group.LastPlayerPosition = currentPos;
                group.IdleSpreadAssigned = false; // Reset spread so new positions are picked
                return false;
            }

            return true;
        }

        #endregion

        #region Following Formation

        private void UpdateFollowingFormation(PlayerFormationGroup group, Player player, bool modeChanged)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 playerForward = player.transform.forward;
            playerForward.y = 0f;
            if (playerForward.sqrMagnitude < 0.01f)
                playerForward = Vector3.forward;
            playerForward.Normalize();

            // Only recompute if player direction changed significantly or mode just changed
            float angleDelta = Vector3.Angle(playerForward, group.LastPlayerForward);
            if (!modeChanged && angleDelta < DirectionChangeThreshold && group.LastPlayerForward.sqrMagnitude > 0.01f)
            {
                // Direction hasn't changed enough — just update world positions with current offsets
                UpdateWorldPositions(group, playerPos, playerForward);
                return;
            }

            group.LastPlayerForward = playerForward;
            group.LastPlayerPosition = playerPos;

            Vector3 playerRight = Vector3.Cross(Vector3.up, playerForward).normalized;
            Vector3 playerBack = -playerForward;

            // Compute offsets for each following companion
            int followingIndex = 0;
            for (int i = 0; i < group.Companions.Count; i++)
            {
                var comp = group.Companions[i];
                if (comp == null || !comp.ShouldBeFollowing) continue;

                var slot = (i < group.Slots.Count) ? group.Slots[i] : null;
                if (slot == null) continue;

                slot.Mode = FormationMode.Following;

                // V-formation: alternating left/right, increasing distance behind
                // Index 0 = directly behind, Index 1 = right, Index 2 = left, etc.
                float behindDist = FollowOffsetBehind + followingIndex * 0.5f;
                behindDist = Mathf.Min(behindDist, FollowOffsetMaxBehind);

                float side = 0f;
                if (followingIndex > 0)
                {
                    int sideIndex = (followingIndex + 1) / 2; // 1,1,2,2,3,3...
                    bool isRight = (followingIndex % 2 == 1);
                    side = sideIndex * FollowOffsetSpacing * (isRight ? 1f : -1f);
                }

                slot.LocalOffset = playerBack * behindDist + playerRight * side;
                slot.WorldPosition = playerPos + slot.LocalOffset;
                slot.LastUpdated = Time.time;

                followingIndex++;
            }
        }

        private void UpdateWorldPositions(PlayerFormationGroup group, Vector3 playerPos, Vector3 playerForward)
        {
            Vector3 playerRight = Vector3.Cross(Vector3.up, playerForward).normalized;
            Vector3 playerBack = -playerForward;

            int followingIndex = 0;
            for (int i = 0; i < group.Companions.Count; i++)
            {
                var comp = group.Companions[i];
                if (comp == null || !comp.ShouldBeFollowing) continue;

                var slot = (i < group.Slots.Count) ? group.Slots[i] : null;
                if (slot == null || slot.Mode != FormationMode.Following) continue;

                // Recalculate world position from the same local offset logic
                float behindDist = FollowOffsetBehind + followingIndex * 0.5f;
                behindDist = Mathf.Min(behindDist, FollowOffsetMaxBehind);

                float side = 0f;
                if (followingIndex > 0)
                {
                    int sideIndex = (followingIndex + 1) / 2;
                    bool isRight = (followingIndex % 2 == 1);
                    side = sideIndex * FollowOffsetSpacing * (isRight ? 1f : -1f);
                }

                slot.WorldPosition = playerPos + playerBack * behindDist + playerRight * side;
                slot.LastUpdated = Time.time;

                followingIndex++;
            }
        }

        #endregion

        #region Idle Spread Formation

        private void UpdateIdleSpreadFormation(PlayerFormationGroup group, Player player, bool modeChanged)
        {
            // Only assign spread positions once when entering idle, or when mode changes
            if (group.IdleSpreadAssigned && !modeChanged) return;

            Vector3 playerPos = player.transform.position;

            // Collect assigned positions to ensure minimum separation
            _tempPositions.Clear();
            _tempPositions.Add(playerPos); // Player counts as an obstacle

            for (int i = 0; i < group.Companions.Count; i++)
            {
                var comp = group.Companions[i];
                if (comp == null || !comp.ShouldBeFollowing) continue;

                var slot = (i < group.Slots.Count) ? group.Slots[i] : null;
                if (slot == null) continue;

                slot.Mode = FormationMode.IdleSpread;

                // Pick a random position within IdleSpreadRadius, ensuring minimum separation
                Vector3 spreadPos = FindSpreadPosition(playerPos, _tempPositions);
                _tempPositions.Add(spreadPos);

                slot.LocalOffset = spreadPos - playerPos;
                slot.WorldPosition = spreadPos;
                slot.LastUpdated = Time.time;
            }

            group.IdleSpreadAssigned = true;

            if (VerboseLogging)
                Debug.Log($"[GroupFormationManager] Assigned idle spread positions for {_tempPositions.Count - 1} companions");
        }

        private Vector3 FindSpreadPosition(Vector3 center, List<Vector3> occupied)
        {
            // Try up to 20 random positions to find one with minimum separation
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Vector2 rand = Random.insideUnitCircle * IdleSpreadRadius;
                Vector3 candidate = center + new Vector3(rand.x, 0f, rand.y);

                // Snap to ground
                if (ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetGroundHeight(candidate, out groundHeight))
                    {
                        candidate.y = groundHeight;
                    }
                }

                // Check separation from all occupied positions
                bool tooClose = false;
                foreach (var pos in occupied)
                {
                    float dist = Vector3.Distance(
                        new Vector3(candidate.x, 0f, candidate.z),
                        new Vector3(pos.x, 0f, pos.z));
                    if (dist < IdleSpreadMinSeparation)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                    return candidate;
            }

            // Fallback: use a deterministic ring position based on occupied count
            float angle = occupied.Count * 137.5f * Mathf.Deg2Rad; // Golden angle for even spread
            float radius = IdleSpreadMinSeparation + (occupied.Count * 0.5f);
            radius = Mathf.Min(radius, IdleSpreadRadius);
            return center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
        }

        #endregion

        #region Helpers

        private void ClearFormationOffsets(PlayerFormationGroup group)
        {
            foreach (var slot in group.Slots)
            {
                if (slot.Mode == FormationMode.Following || slot.Mode == FormationMode.IdleSpread)
                {
                    slot.Mode = FormationMode.None;
                    slot.LocalOffset = Vector3.zero;
                }
            }
        }

        /// <summary>
        /// Reassigns slot indices based on companion archetype priority.
        /// Tanks get lowest index (front), supports get highest (back).
        /// </summary>
        private void ReassignSlotIndices(PlayerFormationGroup group)
        {
            // Simple stable sort: Tanks first, then melee DPS, then ranged DPS, then support/healer
            group.Companions.Sort((a, b) =>
            {
                int priorityA = GetRoleSortPriority(a);
                int priorityB = GetRoleSortPriority(b);
                return priorityA.CompareTo(priorityB);
            });

            // Rebuild slots in new order
            group.Slots.Clear();
            _slotsByCompanionId.Clear();

            for (int i = 0; i < group.Companions.Count; i++)
            {
                var comp = group.Companions[i];
                if (comp == null) continue;

                var slot = new FormationSlot
                {
                    CompanionId = comp.companionId,
                    SlotIndex = i,
                    Mode = group.CurrentMode
                };
                group.Slots.Add(slot);

                if (!string.IsNullOrEmpty(comp.companionId))
                    _slotsByCompanionId[comp.companionId] = slot;
            }
        }

        private int GetRoleSortPriority(CompanionController companion)
        {
            if (companion == null) return 99;

            var archetype = companion.GetArchetypeController();
            if (archetype == null) return 50;

            if (archetype.IsTank) return 0;        // Tank = front
            if (archetype.IsMelee && archetype.IsDPS) return 10; // Melee DPS = near front
            if (archetype.IsDPS) return 20;         // Ranged DPS = middle
            if (archetype.IsSupport) return 30;     // Support = back
            return 50;                               // Unknown = middle
        }

        /// <summary>
        /// Computes a personal space separation vector for a companion.
        /// Returns a direction to push away from overlapping companions.
        /// Returns Vector3.zero if no separation needed.
        /// </summary>
        public Vector3 ComputeSeparation(CompanionController companion, Vector3 myPosition)
        {
            if (companion == null) return Vector3.zero;

            long playerId = companion.ownerPlayerId;
            if (!_groups.TryGetValue(playerId, out var group)) return Vector3.zero;
            if (group.Companions.Count <= 1) return Vector3.zero; // No one to separate from

            Vector3 separation = Vector3.zero;
            int neighborCount = 0;

            foreach (var other in group.Companions)
            {
                if (other == null || other == companion) continue;

                Vector3 otherPos = other.transform.position;
                float dist = Vector3.Distance(
                    new Vector3(myPosition.x, 0f, myPosition.z),
                    new Vector3(otherPos.x, 0f, otherPos.z));

                if (dist < PersonalSpaceRadius && dist > 0.01f)
                {
                    Vector3 away = (myPosition - otherPos);
                    away.y = 0f;
                    away = away.normalized / dist; // Stronger push when closer
                    separation += away;
                    neighborCount++;
                }
            }

            if (neighborCount > 0)
            {
                separation /= neighborCount;
                separation = separation.normalized * SeparationStrength;
            }

            return separation;
        }

        /// <summary>
        /// Cleans up any destroyed or null companion references across all groups.
        /// Called periodically from CompanionFormationController.
        /// </summary>
        public void CleanupDestroyedCompanions()
        {
            var toRemove = new List<long>();

            foreach (var kvp in _groups)
            {
                var group = kvp.Value;
                bool removed = false;

                for (int i = group.Companions.Count - 1; i >= 0; i--)
                {
                    if (group.Companions[i] == null)
                    {
                        if (i < group.Slots.Count)
                        {
                            var slot = group.Slots[i];
                            if (slot != null && !string.IsNullOrEmpty(slot.CompanionId))
                                _slotsByCompanionId.Remove(slot.CompanionId);
                            group.Slots.RemoveAt(i);
                        }
                        group.Companions.RemoveAt(i);
                        removed = true;
                    }
                }

                if (removed)
                    ReassignSlotIndices(group);

                if (group.Companions.Count == 0)
                    toRemove.Add(kvp.Key);
            }

            foreach (var key in toRemove)
                _groups.Remove(key);
        }

        #endregion
    }
}
