using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Validated, configurable companion settings (wander, return-home, chest search and auto-sort radii and the like);
    /// companion systems read these instead of hard-coding values.
    /// </summary>
    public static class CompanionSettings
    {
        #region Default Values
        
        // These are used if ConfigManager is not initialized
        private const float DefaultIdleWanderRadius = 20f;
        private const float DefaultReturnHomeRadius = 25f;
        private const float DefaultChestSearchRadius = 50f;  // INCREASED: 50m default to find chests across base
        private const float DefaultAutoSortRadius = 10f;
        
        /// <summary>
        /// STAY MODE WORK SEARCH RADIUS:
        /// When companions are set to "Stay", they should search for work (smelters, fires, 
        /// resources, chests) within a 50 meter radius of their home position.
        /// This is separate from the wander radius (where they can walk to) and allows
        /// companions to find and travel to work sites further away.
        /// </summary>
        private const float DefaultStayModeWorkRadius = 50f;
        
        // Hard limits to prevent abuse
        private const float MinWanderRadius = 5f;
        private const float MaxWanderRadius = 100f;
        private const float MinReturnRadius = 10f;
        private const float MaxReturnRadius = 100f;
        private const float MinChestRadius = 5f;
        private const float MaxChestRadius = 100f;  // INCREASED: Allow larger search for commanded behaviors
        private const float MinSortRadius = 5f;
        private const float MaxSortRadius = 30f;
        private const float MinWorkRadius = 10f;
        private const float MaxWorkRadius = 100f;

        // Stationed self-defense (defend-the-post combat for static/patrol NPCs)
        private const float DefaultStationedDefendRange = 14f;
        private const float DefaultStationedLeashRange = 24f;
        private const float DefaultStationedCombatHold = 10f;
        private const float MinStationedDefend = 4f;
        private const float MaxStationedDefend = 40f;
        private const float MinStationedLeash = 6f;
        private const float MaxStationedLeash = 60f;
        private const float MinStationedHold = 0f;
        private const float MaxStationedHold = 60f;

        #endregion
        
        #region Public Properties
        
        /// <summary>
        /// Default radius (in meters) that companions wander from home position when in Stay mode.
        /// Used by CompanionNpcModule, CompanionIdleBehavior, and CompanionAI.
        /// </summary>
        public static float IdleWanderRadius
        {
            get
            {
                float value = FiresCore.Bridge.NpcConfigBridge.GetFloat("CompanionIdleWanderRadius", DefaultIdleWanderRadius);
                return Mathf.Clamp(value, MinWanderRadius, MaxWanderRadius);
            }
        }
        
        /// <summary>
        /// Radius (in meters) beyond which companions automatically return to home position.
        /// This is the SINGLE source of truth for all return-home logic.
        /// Used by CompanionNpcModule, CompanionIdleBehavior, CompanionCombatMovement, and CompanionAI.
        /// </summary>
        public static float ReturnHomeRadius
        {
            get
            {
                float value = FiresCore.Bridge.NpcConfigBridge.GetFloat("CompanionReturnHomeRadius", DefaultReturnHomeRadius);
                return Mathf.Clamp(value, MinReturnRadius, MaxReturnRadius);
            }
        }
        
        /// <summary>
        /// Radius (in meters) to search for chests when operating smelters, kilns, or depositing items.
        /// Used by SmelterOperatorBehavior, FireTendingBehavior, ChestDepositBehavior, ResourceGatheringBehavior.
        /// </summary>
        public static float ChestSearchRadius
        {
            get
            {
                float value = FiresCore.Bridge.NpcConfigBridge.GetFloat("CompanionChestSearchRadius", DefaultChestSearchRadius);
                return Mathf.Clamp(value, MinChestRadius, MaxChestRadius);
            }
        }
        
        /// <summary>
        /// Radius (in meters) for auto-sorting items across multiple chests.
        /// When interacting with one chest, all chests within this radius are included in the sort.
        /// Used by ChestDepositBehavior for organizing items.
        /// </summary>
        public static float ChestAutoSortRadius
        {
            get
            {
                float value = FiresCore.Bridge.NpcConfigBridge.GetFloat("ChestAutoSortRadius", DefaultAutoSortRadius);
                return Mathf.Clamp(value, MinSortRadius, MaxSortRadius);
            }
        }
        
        /// <summary>
        /// Radius (in meters) that staying companions search for work opportunities.
        /// This is the distance from home position within which companions will look for:
        /// - Smelters/kilns to operate
        /// - Fires to tend  
        /// - Resources to gather
        /// - Chests to organize
        /// - Archery targets for training
        /// 
        /// Default: 50m - allows companions to find and travel to work sites across a base.
        /// This is intentionally larger than IdleWanderRadius to allow work-focused movement.
        /// </summary>
        public static float StayModeWorkRadius
        {
            get
            {
                float value = FiresCore.Bridge.NpcConfigBridge.GetFloat("CompanionStayModeWorkRadius", DefaultStayModeWorkRadius);
                return Mathf.Clamp(value, MinWorkRadius, MaxWorkRadius);
            }
        }
        
        /// <summary>
        /// Hard limit for wander distance - companions beyond this MUST return home.
        /// This is always 2x the ReturnHomeRadius to provide a buffer zone.
        /// </summary>
        public static float HardWanderLimit => ReturnHomeRadius * 2f;
        
        #endregion
        
        #region Utility Methods
        
        /// <summary>
        /// Checks if a position is within the allowed wander radius from home.
        /// </summary>
        public static bool IsWithinWanderRadius(Vector3 position, Vector3 homePosition)
        {
            return Vector3.Distance(position, homePosition) <= IdleWanderRadius;
        }
        
        /// <summary>
        /// Checks if a companion should return home based on distance.
        /// Returns true if distance exceeds ReturnHomeRadius.
        /// </summary>
        public static bool ShouldReturnHome(Vector3 position, Vector3 homePosition)
        {
            return Vector3.Distance(position, homePosition) > ReturnHomeRadius;
        }
        
        /// <summary>
        /// Checks if a companion is beyond the hard wander limit and MUST return home.
        /// </summary>
        public static bool MustReturnHome(Vector3 position, Vector3 homePosition)
        {
            return Vector3.Distance(position, homePosition) > HardWanderLimit;
        }
        
        /// <summary>
        /// Gets the effective wander radius, potentially modified by a behavior multiplier.
        /// Work behaviors like resource gathering may use larger radii.
        /// </summary>
        public static float GetEffectiveWanderRadius(float behaviorMultiplier = 1f)
        {
            return Mathf.Min(IdleWanderRadius * behaviorMultiplier, HardWanderLimit);
        }
        
        /// <summary>
        /// Gets the effective work search radius for a staying companion.
        /// This checks if the companion is in a territory and uses the larger of:
        /// - StayModeWorkRadius (default 50m)
        /// - Territory bounds size (if in a territory)
        /// 
        /// This ensures companions can work anywhere within their designated area.
        /// </summary>
        /// <param name="homePosition">The companion's home/stay position</param>
        /// <returns>The radius to search for work opportunities</returns>
        public static float GetStayModeWorkSearchRadius(Vector3 homePosition)
        {
            float baseRadius = StayModeWorkRadius;
            
            // Check if position is in a territory
            var territoryBounds = FiresCore.Bridge.NpcModeBridge.GetTerritoryRadius(homePosition);
            if (territoryBounds.HasValue)
            {
                // Get the territory's effective radius (largest dimension / 2)
                // Territories can be larger than our default 50m
                float territoryRadius = territoryBounds.Value;
                
                // Use the larger of territory radius or default work radius
                return Mathf.Max(baseRadius, territoryRadius);
            }
            
            return baseRadius;
        }
        
        /// <summary>
        /// Logs the current companion settings (for debugging).
        /// </summary>
        public static void LogSettings()
        {
            Debug.Log($"[CompanionSettings] IdleWanderRadius: {IdleWanderRadius}m");
            Debug.Log($"[CompanionSettings] ReturnHomeRadius: {ReturnHomeRadius}m");
            Debug.Log($"[CompanionSettings] HardWanderLimit: {HardWanderLimit}m");
            Debug.Log($"[CompanionSettings] StayModeWorkRadius: {StayModeWorkRadius}m");
            Debug.Log($"[CompanionSettings] ChestSearchRadius: {ChestSearchRadius}m");
            Debug.Log($"[CompanionSettings] ChestAutoSortRadius: {ChestAutoSortRadius}m");
        }
        
        #endregion
        
        #region Formation Settings
        
        /// <summary>
        /// Minimum distance between companions before personal space repulsion activates.
        /// Companions closer than this will be gently pushed apart.
        /// </summary>
        public static float FormationPersonalSpaceRadius => 1.5f;
        
        /// <summary>
        /// How strongly companions push away from each other when personal space is violated.
        /// Higher values = faster separation.
        /// </summary>
        public static float FormationSeparationStrength => 2.0f;
        
        /// <summary>
        /// Blend weight for personal space separation (0-1).
        /// 0.2 = 20% separation, 80% intended movement direction.
        /// </summary>
        public static float FormationSeparationBlendWeight => 0.2f;
        
        /// <summary>
        /// Base distance behind the player for the first companion in V-formation.
        /// </summary>
        public static float FormationFollowOffsetBehind => 2.5f;
        
        /// <summary>
        /// Lateral spacing between companions in V-formation.
        /// </summary>
        public static float FormationFollowOffsetSpacing => 1.5f;
        
        /// <summary>
        /// Maximum distance behind the player for the last companion in formation.
        /// </summary>
        public static float FormationFollowOffsetMaxBehind => 5.0f;
        
        /// <summary>
        /// Minimum player direction change (degrees) before formation offsets are recalculated.
        /// Prevents jitter when player rotates slightly.
        /// </summary>
        public static float FormationDirectionChangeThreshold => 15f;
        
        /// <summary>
        /// Radius around the stopped player within which companions spread randomly.
        /// </summary>
        public static float FormationIdleSpreadRadius => 3.0f;
        
        /// <summary>
        /// Minimum distance between spread positions when player is idle.
        /// Ensures companions don't overlap when spreading out.
        /// </summary>
        public static float FormationIdleSpreadMinSeparation => 1.5f;
        
        /// <summary>
        /// How often (seconds) the GroupFormationManager recalculates formation positions.
        /// Lower = more responsive but more CPU. 0.5s is a good balance.
        /// </summary>
        public static float FormationUpdateInterval => 0.5f;
        
        /// <summary>
        /// How often (seconds) personal space separation is checked per companion.
        /// </summary>
        public static float FormationSeparationCheckInterval => 0.2f;
        
        #endregion

        #region Stationed Combat Settings

        /// <summary>
        /// Aggro radius (meters) around a stationed/patrol NPC's post within which it engages hostile enemies.
        /// Read by CompanionAI's stationed self-defense pass (UpdateStationedCombat / FindStationedThreat).
        /// </summary>
        public static float StationedDefendRange
        {
            get
            {
                float value = FiresCore.Bridge.NpcConfigBridge.GetFloat("CompanionStationedDefendRange", DefaultStationedDefendRange);
                return Mathf.Clamp(value, MinStationedDefend, MaxStationedDefend);
            }
        }

        /// <summary>
        /// Distance (meters) from its post past which a stationed/patrol NPC gives up pursuing a threat.
        /// Should exceed <see cref="StationedDefendRange"/> to give chase headroom beyond the aggro bubble.
        /// </summary>
        public static float StationedLeashRange
        {
            get
            {
                float value = FiresCore.Bridge.NpcConfigBridge.GetFloat("CompanionStationedLeashRange", DefaultStationedLeashRange);
                return Mathf.Clamp(value, MinStationedLeash, MaxStationedLeash);
            }
        }

        /// <summary>
        /// How long (seconds) a stationed/patrol NPC stays engaged/alert after the last threat clears before
        /// handing back to its patrol/idle sub-behavior.
        /// </summary>
        public static float StationedCombatHold
        {
            get
            {
                float value = FiresCore.Bridge.NpcConfigBridge.GetFloat("CompanionStationedCombatHold", DefaultStationedCombatHold);
                return Mathf.Clamp(value, MinStationedHold, MaxStationedHold);
            }
        }

        #endregion

        #region Combat Coordination Settings
        
        /// <summary>
        /// How often (seconds) the GroupCombatCoordinator ticks to update threat tables and directives.
        /// </summary>
        public static float CombatCoordinationTickRate => 0.3f;
        
        /// <summary>
        /// Radius to scan for enemies during coordinated combat.
        /// </summary>
        public static float CombatCoordinationThreatScanRadius => 40f;
        
        /// <summary>
        /// How long (seconds) before a combat directive expires and needs to be reissued.
        /// </summary>
        public static float CombatCoordinationDirectiveExpiryTime => 3.0f;
        
        /// <summary>
        /// Maximum number of enemies before DPS companions split targets instead of focus-firing.
        /// </summary>
        public static int CombatCoordinationFocusFireThreshold => 3;
        
        /// <summary>
        /// Player health percentage below which all companions regroup around the player.
        /// </summary>
        public static float CombatCoordinationRegroupHealthThreshold => 0.3f;
        
        /// <summary>
        /// Optimal engagement distance for melee companions around a target.
        /// </summary>
        public static float CombatCoordinationMeleeEngageDistance => 2.5f;
        
        /// <summary>
        /// Safe distance for ranged DPS companions from enemies during combat.
        /// </summary>
        public static float CombatCoordinationRangedSafeDistance => 12f;
        
        /// <summary>
        /// Safe distance for healer companions from enemies during combat.
        /// </summary>
        public static float CombatCoordinationHealerSafeDistance => 15f;
        
        /// <summary>
        /// Maximum number of companions that can be assigned to attack the same target.
        /// Prevents dogpiling on a single weak enemy.
        /// </summary>
        public static int CombatCoordinationMaxCompanionsPerTarget => 3;
        
        #endregion
    }
}
