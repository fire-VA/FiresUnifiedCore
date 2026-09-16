using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Distributes roles across a player's companions: one tank at a time (shield and melee weapon, giants first),
    /// support for support-staff users, and DPS for the rest by equipment. A fallen tank is replaced by the next
    /// candidate, a fallen support is not, and roles stay fixed until the fight ends.
    /// </summary>
    public class GroupRoleManager
    {
        private const float TankReplacementPriorityMargin = 20f;

        private static GroupRoleManager _instance;
        public static GroupRoleManager Instance => _instance ??= new GroupRoleManager();
        
        // Track assigned roles per player
        private readonly Dictionary<long, PlayerGroupRoles> _playerGroups = new Dictionary<long, PlayerGroupRoles>();
        
        // Configuration
        public static bool VerboseLogging = false;
        
        /// <summary>
        /// Tracks role assignments for a single player's companion group.
        /// </summary>
        private class PlayerGroupRoles
        {
            public long PlayerId;
            public string TankCompanionId;
            public HashSet<string> SupportCompanionIds = new HashSet<string>();
            public Dictionary<string, CompanionArchetypeType> AssignedRoles = new Dictionary<string, CompanionArchetypeType>();
            public bool RolesLocked; // True during active combat
            public float LastRoleAssignmentTime;
        }
        
        /// <summary>
        /// Gets the assigned archetype for a companion.
        /// If not assigned, evaluates and assigns one.
        /// </summary>
        public CompanionArchetypeType GetAssignedArchetype(CompanionController companion)
        {
            if (companion == null || companion.ownerPlayerId == 0) 
                return CompanionArchetypeType.None;
            
            var group = GetOrCreatePlayerGroup(companion.ownerPlayerId);
            
            // Check if already assigned
            if (group.AssignedRoles.TryGetValue(companion.companionId, out var assigned))
            {
                return assigned;
            }
            
            // Not assigned yet - evaluate and assign
            return AssignArchetype(companion);
        }
        
        /// <summary>
        /// Assigns an archetype to a companion based on equipment and group needs.
        /// </summary>
        public CompanionArchetypeType AssignArchetype(CompanionController companion)
        {
            if (companion == null || companion.ownerPlayerId == 0)
                return CompanionArchetypeType.None;
            
            var group = GetOrCreatePlayerGroup(companion.ownerPlayerId);
            
            // If roles are locked (combat), don't reassign
            if (group.RolesLocked && group.AssignedRoles.ContainsKey(companion.companionId))
            {
                return group.AssignedRoles[companion.companionId];
            }
            
            // Evaluate what archetype this companion could be
            var preferredArchetype = ArchetypeUtils.EvaluateArchetype(companion);
            var finalArchetype = preferredArchetype;
            
            // Check if Tank role is available
            if (preferredArchetype == CompanionArchetypeType.Tank)
            {
                if (string.IsNullOrEmpty(group.TankCompanionId))
                {
                    // No tank yet - this companion becomes tank
                    group.TankCompanionId = companion.companionId;
                    finalArchetype = CompanionArchetypeType.Tank;
                    
                    // ALWAYS log tank assignments - important for group role diagnostics
                    Debug.Log($"[GroupRole] TANK ASSIGNED: {companion.companionName} is now the group tank");
                }
                else if (group.TankCompanionId == companion.companionId)
                {
                    // Already the tank
                    finalArchetype = CompanionArchetypeType.Tank;
                }
                else
                {
                    // Another companion is already tank - check if we should replace
                    var currentTank = FindCompanionById(group.TankCompanionId, companion.ownerPlayerId);
                    
                    if (currentTank == null || currentTank.isDefeated || !currentTank.isTamed)
                    {
                        // Current tank is gone - take over
                        group.TankCompanionId = companion.companionId;
                        finalArchetype = CompanionArchetypeType.Tank;
                        
                        // ALWAYS log tank takeover
                        Debug.Log($"[GroupRole] TANK TAKEOVER: {companion.companionName} taking tank role (previous tank unavailable)");
                    }
                    else
                    {
                        // Current tank is valid - check priority
                        float myPriority = ArchetypeUtils.GetArchetypePriority(companion, CompanionArchetypeType.Tank);
                        float theirPriority = ArchetypeUtils.GetArchetypePriority(currentTank, CompanionArchetypeType.Tank);
                        
                        if (myPriority > theirPriority + TankReplacementPriorityMargin && !group.RolesLocked)
                        {
                            // Significantly better tank - take over (but not during combat)
                            group.TankCompanionId = companion.companionId;
                            finalArchetype = CompanionArchetypeType.Tank;
                            
                            // Demote previous tank to DPS
                            group.AssignedRoles[currentTank.companionId] = 
                                ArchetypeUtils.HasRangedWeapon(currentTank.GetInventory()) 
                                    ? CompanionArchetypeType.RangedDPS 
                                    : CompanionArchetypeType.MeleeDPS;
                            
                            // ALWAYS log tank reassignment
                            Debug.Log($"[GroupRole] TANK CHANGED: {companion.companionName} taking tank from {currentTank.companionName} (better suited)");
                        }
                        else
                        {
                            // Tank slot taken - become DPS
                            finalArchetype = ArchetypeUtils.HasRangedWeapon(companion.GetInventory())
                                ? CompanionArchetypeType.RangedDPS
                                : CompanionArchetypeType.MeleeDPS;
                        }
                    }
                }
            }
            
            // Track support companions
            if (finalArchetype == CompanionArchetypeType.Support)
            {
                group.SupportCompanionIds.Add(companion.companionId);
            }
            else
            {
                group.SupportCompanionIds.Remove(companion.companionId);
            }
            
            // Record assignment
            group.AssignedRoles[companion.companionId] = finalArchetype;
            group.LastRoleAssignmentTime = Time.time;
            
            if (VerboseLogging)
            {
                Debug.Log($"[GroupRoleManager] {companion.companionName} assigned archetype: {finalArchetype}");
            }
            
            return finalArchetype;
        }
        
        /// <summary>
        /// Locks roles for a player's group (call when entering combat).
        /// Prevents role shuffling mid-fight.
        /// </summary>
        public void LockRoles(long playerId)
        {
            var group = GetOrCreatePlayerGroup(playerId);
            group.RolesLocked = true;
            
            if (VerboseLogging)
            {
                Debug.Log($"[GroupRoleManager] Roles locked for player {playerId}");
            }
        }
        
        /// <summary>
        /// Unlocks roles for a player's group (call when combat ends).
        /// Allows role reassignment on next evaluation.
        /// </summary>
        public void UnlockRoles(long playerId)
        {
            var group = GetOrCreatePlayerGroup(playerId);
            group.RolesLocked = false;
            
            if (VerboseLogging)
            {
                Debug.Log($"[GroupRoleManager] Roles unlocked for player {playerId}");
            }
        }
        
        /// <summary>
        /// Called when a companion dies or is dismissed.
        /// Clears their role assignment.
        /// </summary>
        public void OnCompanionRemoved(CompanionController companion)
        {
            if (companion == null || companion.ownerPlayerId == 0) return;
            
            if (!_playerGroups.TryGetValue(companion.ownerPlayerId, out var group)) return;
            
            // Clear tank if this was the tank
            if (group.TankCompanionId == companion.companionId)
            {
                group.TankCompanionId = null;
                
                // ALWAYS log tank loss
                Debug.Log($"[GroupRole] TANK LOST: {companion.companionName} removed - tank role now vacant");
                
                // Try to find a replacement tank
                if (!group.RolesLocked) // Only during non-combat
                {
                    FindReplacementTank(companion.ownerPlayerId);
                }
            }
            
            // Remove from support list
            group.SupportCompanionIds.Remove(companion.companionId);
            
            // Remove role assignment
            group.AssignedRoles.Remove(companion.companionId);
        }
        
        /// <summary>
        /// Called when tank is defeated in combat - tries to assign new tank immediately.
        /// </summary>
        public void OnTankDefeated(long playerId)
        {
            if (!_playerGroups.TryGetValue(playerId, out var group)) return;
            
            group.TankCompanionId = null;
            
            // Find replacement even during combat
            var replacement = FindBestTankCandidate(playerId);
                if (replacement != null)
                {
                    group.TankCompanionId = replacement.companionId;
                    group.AssignedRoles[replacement.companionId] = CompanionArchetypeType.Tank;
                    
                    // Notify the companion - ArchetypeController is in same namespace
                    var archController = replacement.GetComponent<FiresCore.Npc.Archetypes.ArchetypeController>();
                    archController?.OnArchetypeChanged(CompanionArchetypeType.Tank);
                    
                    // ALWAYS log mid-combat tank promotion - critical event
                    Debug.Log($"[GroupRole] EMERGENCY TANK: {replacement.companionName} promoted to TANK mid-combat!");
                }
        }
        
        /// <summary>
        /// Gets the current tank companion for a player.
        /// </summary>
        public CompanionController GetTank(long playerId)
        {
            if (!_playerGroups.TryGetValue(playerId, out var group)) return null;
            if (string.IsNullOrEmpty(group.TankCompanionId)) return null;
            
            return FindCompanionById(group.TankCompanionId, playerId);
        }
        
        /// <summary>
        /// Gets all support companions for a player.
        /// </summary>
        public List<CompanionController> GetSupports(long playerId)
        {
            var supports = new List<CompanionController>();
            
            if (!_playerGroups.TryGetValue(playerId, out var group)) return supports;
            
            foreach (var id in group.SupportCompanionIds)
            {
                var companion = FindCompanionById(id, playerId);
                if (companion != null && !companion.isDefeated)
                {
                    supports.Add(companion);
                }
            }
            
            return supports;
        }
        
        /// <summary>
        /// Gets all companions with a specific archetype for a player.
        /// </summary>
        public List<CompanionController> GetCompanionsByArchetype(long playerId, CompanionArchetypeType archetype)
        {
            var result = new List<CompanionController>();
            
            if (!_playerGroups.TryGetValue(playerId, out var group)) return result;
            
            foreach (var kvp in group.AssignedRoles)
            {
                if (kvp.Value == archetype)
                {
                    var companion = FindCompanionById(kvp.Key, playerId);
                    if (companion != null && !companion.isDefeated)
                    {
                        result.Add(companion);
                    }
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Refreshes all role assignments for a player (call after equipment changes).
        /// </summary>
        public void RefreshRoles(long playerId)
        {
            if (!_playerGroups.TryGetValue(playerId, out var group)) return;
            if (group.RolesLocked) return; // Don't refresh during combat
            
            // Get all active companions
            var companions = GetPlayerCompanions(playerId);
            
            // Re-evaluate all roles
            foreach (var companion in companions)
            {
                AssignArchetype(companion);
            }
        }
        
        /// <summary>
        /// Gets all role assignments for a player's group.
        /// Returns a dictionary mapping companionId to assigned archetype.
        /// Used by CombatRoleDirector to know each companion's role.
        /// </summary>
        public Dictionary<string, CompanionArchetypeType> GetAllRoleAssignments(long playerId)
        {
            if (!_playerGroups.TryGetValue(playerId, out var group))
                return new Dictionary<string, CompanionArchetypeType>();
            
            return new Dictionary<string, CompanionArchetypeType>(group.AssignedRoles);
        }
        
        /// <summary>
        /// Clears all role data for a player (call on logout).
        /// </summary>
        public void ClearPlayer(long playerId)
        {
            _playerGroups.Remove(playerId);
        }
        
        #region Private Helpers
        
        private PlayerGroupRoles GetOrCreatePlayerGroup(long playerId)
        {
            if (!_playerGroups.TryGetValue(playerId, out var group))
            {
                group = new PlayerGroupRoles { PlayerId = playerId };
                _playerGroups[playerId] = group;
            }
            return group;
        }
        
        private void FindReplacementTank(long playerId)
        {
            var replacement = FindBestTankCandidate(playerId);
            if (replacement != null)
            {
                var group = GetOrCreatePlayerGroup(playerId);
                group.TankCompanionId = replacement.companionId;
                group.AssignedRoles[replacement.companionId] = CompanionArchetypeType.Tank;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[GroupRoleManager] {replacement.companionName} assigned as replacement TANK");
                }
            }
        }
        
        private CompanionController FindBestTankCandidate(long playerId)
        {
            var companions = GetPlayerCompanions(playerId);
            
            CompanionController bestCandidate = null;
            float bestPriority = 0f;
            
            foreach (var companion in companions)
            {
                // Skip if already has a non-tank role that's important
                if (_playerGroups.TryGetValue(playerId, out var group))
                {
                    if (group.SupportCompanionIds.Contains(companion.companionId))
                        continue; // Don't pull supports into tank role
                }
                
                float priority = ArchetypeUtils.GetArchetypePriority(companion, CompanionArchetypeType.Tank);
                if (priority > bestPriority)
                {
                    bestPriority = priority;
                    bestCandidate = companion;
                }
            }
            
            // Only accept if they have tank equipment (non-zero priority)
            return bestPriority > 0 ? bestCandidate : null;
        }
        
        private CompanionController FindCompanionById(string companionId, long playerId)
        {
            if (string.IsNullOrEmpty(companionId)) return null;
            
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion != null && 
                    companion.companionId == companionId && 
                    companion.ownerPlayerId == playerId &&
                    !companion.isDefeated)
                {
                    return companion;
                }
            }
            
            return null;
        }
        
        private List<CompanionController> GetPlayerCompanions(long playerId)
        {
            var result = new List<CompanionController>();
            
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion != null && 
                    companion.ownerPlayerId == playerId && 
                    companion.isTamed &&
                    !companion.isDefeated)
                {
                    result.Add(companion);
                }
            }
            
            return result;
        }
        
        #endregion
    }
}
