using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Fire 10-24: "anything we plant says needs more room to grow" - even a lone sapling in open grass. When vanilla's
    /// Plant.HaveGrowSpace says no, this names every collider its own test (grow radius, the same layer mask) sees, once per
    /// plant: name, layer, root object, distance. Debug only; it changes nothing.
    /// </summary>
    [HarmonyPatch(typeof(Plant), "HaveGrowSpace")]
    internal static class PlantGrowSpaceProbe
    {
        private static int s_mask;
        private static readonly HashSet<int> s_logged = new HashSet<int>();

        private static void Postfix(Plant __instance, bool __result)
        {
            if (__result || __instance == null || !s_logged.Add(__instance.GetInstanceID())) return;
            try
            {
                if (s_mask == 0) s_mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
                Vector3 p = __instance.transform.position;
                var sb = new StringBuilder();
                int n = 0;
                foreach (var c in Physics.OverlapSphere(p, __instance.m_growRadius, s_mask))
                {
                    if (c == null) continue;
                    var pl = c.GetComponentInParent<Plant>();
                    if (pl == __instance) continue;
                    string plant = pl != null ? $", plant {pl.GetStatus()}" : "";
                    sb.Append($"\n    '{c.name}' layer {LayerMask.LayerToName(c.gameObject.layer)}, root '{c.transform.root.name}', {Vector3.Distance(p, c.bounds.ClosestPoint(p)):0.00} m{(c.isTrigger ? ", trigger" : "")}{plant}");
                    n++;
                }
                FiresCore.Logging.FiresLogger.LogDebug($"[Farming] NoSpace {Utils.GetPrefabName(__instance.gameObject)} at ({p.x:0.0},{p.y:0.0},{p.z:0.0}), grow radius {__instance.m_growRadius:0.00} m: {n} collider(s) in vanilla's grow test:{sb}");
            }
            catch (System.Exception e) { FiresCore.Logging.FiresLogger.LogDebug($"[Farming] NoSpace probe failed: {e.Message}"); }
        }
    }
}
