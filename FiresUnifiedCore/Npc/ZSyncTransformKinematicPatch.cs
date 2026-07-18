using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Vanilla <see cref="ZSyncTransform"/> caches whether its Rigidbody is kinematic ONCE at init
    /// (<c>m_isKinematicBody = m_body.isKinematic</c>) and never refreshes it. A body's kinematic state,
    /// however, changes at runtime: vanilla <c>Character</c> flips <c>m_body.isKinematic = true</c> for
    /// sleeping creatures (the <c>m_disableWhileSleeping</c> optimisation) and companion code toggles it for
    /// defeat / teleport. Once a body goes kinematic while the cache still reads <c>false</c>, the non-owner
    /// <c>ClientSync</c> path keeps writing <c>linearVelocity</c>/<c>angularVelocity</c> to it every
    /// FixedUpdate, which Unity rejects with "Setting linear/angular velocity of a kinematic body is not
    /// supported" — one pair per tick, on every non-owner machine (client and non-owner server), indefinitely.
    ///
    /// Refreshing the cached flag from the live body before each sync makes ClientSync take vanilla's own
    /// kinematic branch (<c>MovePosition</c>/<c>MoveRotation</c>), which drives the body correctly with no
    /// velocity writes. The field is meant to track the body state, so this is strictly more correct than the
    /// stale cache and applies to every entity — it also closes the same latent case for vanilla sleepers.
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
