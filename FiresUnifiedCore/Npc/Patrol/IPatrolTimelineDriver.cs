namespace FiresCore.Npc.Patrol
{
    /// <summary>
    /// Add-only: something other than the point-follower that drives a
    /// patrolling NPC for its route, e.g. RPGmaker's ghost replay (a recorded timeline of the player's moves, attacks,
    /// interactions and emotes). Set it on <see cref="PatrolAssignment.TimelineDriver"/>. While <see cref="WantsControl"/> is true,
    /// <c>PatrolBehavior</c> keeps every gate it already has (owner only, started as the patrol command, cancelled by combat and
    /// restarted after it) but hands its ticks to the driver instead of walking the points itself.
    /// Calls arrive on the NPC's ZDO owner only.
    /// </summary>
    public interface IPatrolTimelineDriver
    {
        /// <summary>True when this driver should run the NPC's patrol (checked every tick; false = the normal point walk).</summary>
        bool WantsControl { get; }

        /// <summary>The patrol (re)started: first start, after combat, after an owner change or a command.</summary>
        void OnPatrolStart();

        /// <summary>One patrol tick (Update) while active and not in combat.</summary>
        void OnPatrolTick();

        /// <summary>The patrol is held or ended: "combat", "inactive" (every held tick) or "cancelled" (once).</summary>
        void OnPatrolPaused(string reason);
    }
}
