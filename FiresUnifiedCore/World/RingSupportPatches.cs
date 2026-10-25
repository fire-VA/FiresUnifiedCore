using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// 0.2.820 (Fire 10-24, halo ring; [visual]'s plan A6, "Fire's base risk: without it pieces lose support and collapse"):
    /// building support and roof checks along the LOCAL up. Off the ring (no GravityUp field) every path is the vanilla one.
    /// Support: vanilla WearNTear.UpdateSupport judges each neighbour's support point against the piece's centre of mass in world y
    /// ("below" = point.y &lt; com.y + 0.05, the vertical share from the direction's y, the spread of supports with y zeroed). The one
    /// FindSupportPoint call there is swapped for RingSupportPoint: the same point, found along -up, and handed back ROTATED into the
    /// ring frame around the centre of mass. Every later test in UpdateSupport only uses the point relative to the centre of mass,
    /// and the rotation keeps distances, so "below", "vertical" and the spread all read along the local up with no other change.
    /// Roof: RoofCheck (rain / snow cover) and HaveAshRoof cast along the local up instead of world up.
    /// </summary>
    [HarmonyPatch]
    public static class RingSupportPatches
    {
        private static readonly RaycastHit[] s_hits = new RaycastHit[128];
        private static readonly AccessTools.FieldRef<int> s_rayMask = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(WearNTear), "s_rayMask"));
        private static readonly AccessTools.FieldRef<WearNTear, GameObject> s_ashroof = AccessTools.FieldRefAccess<WearNTear, GameObject>("m_ashroof");
        private static readonly MethodInfo s_findSupport = AccessTools.Method(typeof(WearNTear), "FindSupportPoint");
        // vanilla FindSupportPoint as a delegate, bound once (this runs for every support point of every piece, on and off the ring)
        private static readonly System.Func<Vector3, WearNTear, Collider, Vector3> s_vanillaSupport = AccessTools.MethodDelegate<System.Func<Vector3, WearNTear, Collider, Vector3>>(s_findSupport);
        private static readonly HashSet<string> s_logged = new HashSet<string>();
        private static void Once(string key, string line) { if (s_logged.Count < 40 && s_logged.Add(key)) Debug.Log(line); }

        /// <summary>FindSupportPoint along the local up, returned in the ring frame around com (vanilla's answer off the ring).</summary>
        public static Vector3 RingSupportPoint(Vector3 com, WearNTear wnt, Collider other)
        {
            if (!GravityUp.Active || !GravityUp.InField(com, out Vector3 up))
                return s_vanillaSupport(com, wnt, other);
            Vector3 p;
            MeshCollider mc = other as MeshCollider;
            if (mc != null && !mc.convex)
                p = mc.Raycast(new Ray(com, -up), out RaycastHit hit, 10f) ? hit.point : (com + wnt.GetCOM()) * 0.5f;
            else
                p = other.ClosestPoint(com);
            Quaternion toLocal = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up, up));
            Once("support", $"[GravityUp] building support along the local up (ring frame): first piece {Utils.GetPrefabName(wnt.gameObject)}");
            return com + toLocal * (p - com);
        }

        [HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> SupportTranspile(IEnumerable<CodeInstruction> code)
        {
            var ring = AccessTools.Method(typeof(RingSupportPatches), nameof(RingSupportPoint));
            int n = 0;
            foreach (var ins in code)
            {
                if (s_findSupport != null && ins.Calls(s_findSupport)) { ins.operand = ring; n++; }
                yield return ins;
            }
            Debug.Log($"[GravityUp] WearNTear.UpdateSupport: {n} FindSupportPoint call(s) read along the local up{(n == 0 ? " - NONE FOUND (support stays world-y)" : "")}");
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.RoofCheck))]
        [HarmonyPrefix]
        private static bool RoofCheck(Transform obj, Vector3 position, ref GameObject roofObject, float heightOffset, ref bool __result)
        {
            if (!GravityUp.Active || !GravityUp.InField(position, out Vector3 up)) return true;
            roofObject = null;
            __result = false;
            int n = Physics.SphereCastNonAlloc(position + up * heightOffset, 0.1f, up, s_hits, 100f, s_rayMask());
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = s_hits[i];
                if (!h.collider.transform.IsAncestor(obj) && !h.collider.gameObject.CompareTag("leaky"))
                {
                    roofObject = h.collider.gameObject;
                    __result = true;
                    break;
                }
            }
            Once("roof", "[GravityUp] roof checks cast along the local up (ring frame)");
            return false;
        }

        [HarmonyPatch(typeof(WearNTear), "HaveAshRoof")]
        [HarmonyPrefix]
        private static bool AshRoof(WearNTear __instance, ref bool __result)
        {
            if (__instance == null || !GravityUp.Active || !GravityUp.InField(__instance.transform.position, out Vector3 up)) return true;
            GameObject cached = s_ashroof(__instance);
            if (cached) { __result = true; return false; }
            __result = false;
            int n = Physics.SphereCastNonAlloc(__instance.transform.position, 0.1f, up, s_hits, 100f, s_rayMask());
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = s_hits[i];
                if (h.collider.gameObject != __instance.gameObject && (h.collider.transform.parent == null || h.collider.transform.parent.gameObject != __instance.gameObject))
                {
                    s_ashroof(__instance) = h.collider.gameObject;
                    __result = true;
                    break;
                }
            }
            return false;
        }
    }
}
