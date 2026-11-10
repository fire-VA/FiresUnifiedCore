using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.1073 (Fire: "shouldnt it just be rechecked after the ebm swap?"): EBM moves a baked piece's colliders onto an invisible stand-in
    /// (and back) with no Piece Awake / OnDestroy, so the snow shelter of that zone is recomputed right after EBM changes its stand-ins:
    ///  - FiresEasyBakeMeshes.EasyBake.StandInColliders.Build (EBM 1.2.155 StandInColliders.cs:27; every stand-in EBM builds - the
    ///    load-time bake, on-demand and F2) -> the zone of the built stand-in;
    ///  - ZoneTracker.DestroyStandIns / DropStandIn / ReleaseSkipped / UnparkStandIns (ZoneTracker.cs:2206 / 2231 / 2178 / 1666) -> the
    ///    zone of their ZoneState (field Coord).
    /// Bound by name (no compile reference to EBM); a missing method is skipped with one Debug line. The zone is marked dirty and the
    /// existing budgeted shelter job picks it up after its 1 s quiet, which also batches a burst of builds into one recompute.
    /// </summary>
    internal static class SnowShelterEbm
    {
        private const string Ns = "FiresEasyBakeMeshes.EasyBake.";

        // 0.2.1218 ([generator], EBM 1.2.208): EbmApi.StandInsChanged (built AND removed, once per touched zone) replaces the five postfixes
        // below; they stay only for an older EBM and do nothing once the event is bound.
        private static bool s_viaApi;

        /// <summary>Once from CoreLateOwners.RunOnce: bind EBM's public event when EBM has it.</summary>
        internal static void BindApi()
        {
            if (s_viaApi) return;
            if (!FiresCore.Compat.EbmStandIns.Subscribe(OnStandInsChanged)) return;
            s_viaApi = true;
            FiresCore.Logging.FiresLogger.LogInfo("[Snow] shelter: EBM stand-in changes come from EbmApi.StandInsChanged (Core's patches on EBM internals stand down)");
        }

        private static void OnStandInsChanged(Vector2i zone, int left)
        {
            try
            {
                var z = new Vector2s(zone.x, zone.y);
                SnowShelter.EbmChanged(z, left > 0 ? 1 : 0);
                FiresCore.Events.CoreEvents.RaiseEbmStandInsChanged(z);
            }
            catch { }
        }

        [HarmonyPatch]
        private static class StandInBuilt
        {
            private static bool Prepare() => Target() != null;
            private static MethodBase TargetMethod() => Target();
            private static MethodBase Target() => FiresCore.Compat.OptionalTypes.Find(Ns + "StandInColliders") is Type t ? AccessTools.Method(t, "Build") : null;
            private static void Postfix(GameObject __result)
            {
                try
                {
                    if (s_viaApi || __result == null) return;
                    var z = ZoneSystem.GetZone(__result.transform.position);
                    SnowShelter.EbmChanged(z, 1);
                    FiresCore.Events.CoreEvents.RaiseEbmStandInsChanged(z);   // 0.2.1074
                }
                catch { }
            }
        }

        [HarmonyPatch]
        private static class StandInsChanged
        {
            private static readonly string[] Names = { "DestroyStandIns", "DropStandIn", "ReleaseSkipped", "UnparkStandIns" };
            private static bool Prepare()
            {
                var t = FiresCore.Compat.OptionalTypes.Find(Ns + "ZoneTracker");
                if (t == null) { FiresCore.Logging.FiresLogger.LogDebug("[Snow] shelter: EBM not found (no stand-in recompute hooks)"); return false; }
                foreach (var n in Names) if (AccessTools.Method(t, n) != null) return true;
                return false;
            }
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var t = FiresCore.Compat.OptionalTypes.Find(Ns + "ZoneTracker");
                if (t == null) yield break;
                foreach (var n in Names)
                {
                    var m = AccessTools.Method(t, n);
                    if (m != null) yield return m;
                    else FiresCore.Logging.FiresLogger.LogDebug($"[Snow] shelter: EBM ZoneTracker.{n} not found (that recompute trigger is skipped)");
                }
            }
            private static void Postfix(object[] __args)
            {
                try
                {
                    if (s_viaApi || __args == null || __args.Length == 0 || __args[0] == null) return;
                    object coord = Traverse.Create(__args[0]).Field("Coord").GetValue();
                    if (coord is Vector2s z) { SnowShelter.EbmChanged(z, 0); FiresCore.Events.CoreEvents.RaiseEbmStandInsChanged(z); }
                }
                catch { }
            }
        }
    }
}
