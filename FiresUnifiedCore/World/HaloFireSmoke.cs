using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// Halo ring, [visual]'s plan A10 (the Core part; [classes-ui] for [generator], lead 10-24): fire and smoke inside a GravityUp field.
    /// Vanilla's Fireplace casts its roof / blocked checks along world +Y and reads the flat world's heightmap, so on the ring wall a
    /// fire "in the rain" never found its roof (went out under one) or read the heightmap as burying it. Smoke rose along world +Y.
    /// Inside a field (only there): Fireplace.CheckUnderTerrain / CheckWet run along the local up (no heightmap test: it is the flat
    /// world's; the roof is a solid ray straight up, the cover percentage stays vanilla's Cover for the wind test), and each Smoke
    /// puff's velocity is turned into the local frame and only its UP part fades with age. Outside every field these prefixes return true
    /// at once and vanilla runs unchanged (no field active = byte-identical behaviour).
    /// </summary>
    [HarmonyPatch]
    // 0.2.823 ([lead] 10-24): Fireplace part only. The Smoke rise / fade patches were dropped: FTS 0.3.207 already makes smoke rise along the local up, so both would double it.
    public static class HaloFireSmoke
    {
        /// <summary>How far straight up (m) a ring fire looks for its roof (vanilla's Cover reads world up).</summary>
        public const float RoofReach = 30f;
        private static readonly HashSet<string> s_logged = new HashSet<string>();
        private static int s_roofMask;

        private static bool Field(Vector3 p, out Vector3 up)
        {
            up = Vector3.up;
            if (!GravityUp.Active || !GravityUp.InField(p, out up) || up.sqrMagnitude < 0.5f) { up = Vector3.up; return false; }
            up.Normalize();
            return true;
        }

        private static void Once(string key, string line)
        {
            if (s_logged.Count < 40 && s_logged.Add(key)) FiresCore.Logging.FiresLogger.LogDebug(line);
        }

        // ---- Fireplace -----------------------------------------------------------------------------------------------------------
        [HarmonyPatch(typeof(Fireplace), "CheckUnderTerrain")]
        [HarmonyPrefix]
        private static bool FireplaceUnderTerrain(Fireplace __instance)
        {
            if (__instance == null || !Field(__instance.transform.position, out Vector3 up)) return true;
            __instance.m_blocked = false;
            if (__instance.m_disableCoverCheck) return false;
            if (Fireplace.m_solidRayMask == 0) Fireplace.m_solidRayMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
            if (Physics.Raycast(__instance.transform.position + up * __instance.m_coverCheckOffset, up, out RaycastHit _, 0.5f, Fireplace.m_solidRayMask))
                __instance.m_blocked = true;
            else if (__instance.m_smokeSpawner != null && __instance.m_smokeSpawner.IsBlocked())
                __instance.m_blocked = true;
            Once("fire_blocked", $"[Halo] Fireplace {Utils.GetPrefabName(__instance.gameObject)}: blocked / under-terrain check along the local up {up} (no heightmap in the field): blocked {__instance.m_blocked}");
            return false;
        }

        [HarmonyPatch(typeof(Fireplace), "CheckWet")]
        [HarmonyPrefix]
        private static bool FireplaceWet(Fireplace __instance)
        {
            if (__instance == null || EnvMan.instance == null || !Field(__instance.transform.position, out Vector3 up)) return true;
            __instance.m_wet = false;
            bool windy = EnvMan.instance.GetWindIntensity() >= 0.8f;
            bool wet = EnvMan.IsWet();
            if (!(windy | wet)) return false;
            Vector3 p = __instance.transform.position + up * __instance.m_coverCheckOffset;
            Cover.GetCoverForPoint(p, out float coverPercentage, out bool _);   // vanilla's cover share for the wind test
            if (s_roofMask == 0) s_roofMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
            bool underRoof = Physics.Raycast(p, up, out RaycastHit roof, RoofReach, s_roofMask, QueryTriggerInteraction.Ignore);
            if (windy && coverPercentage < 0.7f) __instance.m_wet = true;
            else if (wet && !underRoof) __instance.m_wet = true;
            Once("fire_wet", $"[Halo] Fireplace {Utils.GetPrefabName(__instance.gameObject)}: roof check along the local up {up}: " +
                             (underRoof ? $"roof '{roof.collider.transform.root.name}' {roof.distance:0.0} m up" : $"no roof within {RoofReach:0} m") + $", wet {__instance.m_wet}");
            return false;
        }

    }
}
