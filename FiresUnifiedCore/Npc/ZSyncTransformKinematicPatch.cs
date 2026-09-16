using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Refreshes ZSyncTransform's cached kinematic flag from the live body before each ClientSync. Vanilla caches it
    /// once at init, but bodies turn kinematic at runtime (sleeping creatures, companion defeat and teleport), and
    /// the stale flag made non-owners write velocities to kinematic bodies every tick, which Unity rejects with a
    /// warning. With the live flag, vanilla takes its own kinematic MovePosition path.
    /// </summary>
    [HarmonyPatch(typeof(ZSyncTransform), "ClientSync")]
    internal static class ZSyncTransformKinematicRefreshPatch
    {
        private static readonly AccessTools.FieldRef<ZSyncTransform, Rigidbody> BodyRef =
            AccessTools.FieldRefAccess<ZSyncTransform, Rigidbody>("m_body");
        private static readonly AccessTools.FieldRef<ZSyncTransform, bool> KinematicFlagRef =
            AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_isKinematicBody");

        [HarmonyPrefix]
        private static void RefreshKinematicFlag(ZSyncTransform __instance)
        {
            try
            {
                var body = BodyRef(__instance);
                if (body != null)
                    KinematicFlagRef(__instance) = body.isKinematic;
            }
            catch { }
        }
    }
}
