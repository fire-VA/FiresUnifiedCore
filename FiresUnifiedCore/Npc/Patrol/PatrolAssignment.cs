using UnityEngine;

namespace FiresCore.Npc.Patrol
{
    /// <summary>
    /// Marks an NPC as assigned to a patrol route (by name — a file under
    /// BepInEx/config/FiresNPCs_PatrolRoutes/). The route data itself lives in
    /// <see cref="PatrolRouteManager"/>; this component just carries the assignment so Core's
    /// <c>PatrolBehavior</c> can read it without referencing the FiresNPCs NpcController.
    /// FiresNPCs sets <see cref="RouteName"/> from NpcController.patrolRouteName on spawn.
    /// </summary>
    public class PatrolAssignment : MonoBehaviour
    {
        public string RouteName;

        /// <summary>
        /// Name of the DBSM speed preset selected for this NPC on its route (empty = Default = flat base
        /// walk, today's behaviour). Set from NpcController.patrolSpeedPreset on spawn, alongside RouteName.
        /// </summary>
        public string PresetName;

        public bool HasRoute => !string.IsNullOrEmpty(RouteName)
                                && PatrolRouteManager.GetRoute(RouteName) != null;
    }
}
