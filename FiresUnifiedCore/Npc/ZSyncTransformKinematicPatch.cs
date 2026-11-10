using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// 0.2.1217 ([generator], Fire 11-08: perf only in FGN / EBM / FiresFixes; FGN 1.6.64 owns it): the ZSyncTransform kinematic refresh
    /// (the staggered ClientSync prefix, or the 2 s sweeper) runs in FGN now, from FGN's own "[Performance] Kinematic sync sweep" key.
    /// Core keeps only the field access its own Set needs.
    /// </summary>
    internal static class ZSyncTransformKinematicRefreshPatch
    {
        private static readonly AccessTools.FieldRef<ZSyncTransform, bool> KinematicFlagRef =
            AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_isKinematicBody");

        internal static void SetFlag(ZSyncTransform sync, bool kinematic) => KinematicFlagRef(sync) = kinematic;
    }

    /// <summary>
    /// Core's own companion freeze / unfreeze / wake: sets the body's isKinematic AND its ZSyncTransform's cached flag in the same call,
    /// so vanilla's ClientSync takes the right path at once. FGN's sweep (when it runs) then finds the flag already correct.
    /// </summary>
    internal static class KinematicSync
    {
        private static float s_setLogAt;

        /// <summary>Kept for callers (FiresUnifiedCore.Setup). 0.2.1217: nothing to attach; FGN owns the refresh.</summary>
        public static void Bind(BepInEx.Configuration.ConfigFile config, Harmony harmony) { }

        /// <summary>Kept for CoreLateOwners. 0.2.1217: no prefix-or-sweeper choice left in Core.</summary>
        internal static void Decide() { }

        /// <summary>Sets <paramref name="body"/>.isKinematic and its ZSyncTransform's cached flag in the same call.</summary>
        public static void Set(Rigidbody body, bool kinematic, string site)
        {
            if (body == null) return;
            body.isKinematic = kinematic;
            var sync = body.GetComponent<ZSyncTransform>();
            if (sync != null) ZSyncTransformKinematicRefreshPatch.SetFlag(sync, kinematic);
            if (Time.unscaledTime >= s_setLogAt)
            {
                s_setLogAt = Time.unscaledTime + 1f;
                FiresCore.Logging.FiresLogger.LogDebug($"[KinematicSync] set {body.name} kinematic {kinematic} ({site}{(sync == null ? ", no ZSyncTransform" : "")})");
            }
        }
    }
}
