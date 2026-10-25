using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// A finalizer on vanilla's MeleeWeaponTrail.CustomFixedUpdate, which can throw ArgumentOutOfRangeException during companion combat:
    /// the FIRST time a trail throws, one line names it (its object path up to the character that holds it, the character and whether
    /// it is a companion, the exception) so the cause can be found; that trail is then turned off (only that one component - the weapon keeps
    /// working, it just draws no swing trail) so one broken trail cannot throw every physics step. Any other trail is untouched.
    /// </summary>
    [HarmonyPatch(typeof(MeleeWeaponTrail), nameof(MeleeWeaponTrail.CustomFixedUpdate))]
    internal static class WeaponTrailGuard
    {
        private static readonly HashSet<int> s_reported = new HashSet<int>();
        private static readonly HashSet<int> s_emptyNoted = new HashSet<int>();
        private static readonly AccessTools.FieldRef<MeleeWeaponTrail, bool> s_emit = AccessTools.FieldRefAccess<MeleeWeaponTrail, bool>("_emit");
        private static readonly AccessTools.FieldRef<MeleeWeaponTrail, List<MeleeWeaponTrail.Point>> s_points = AccessTools.FieldRefAccess<MeleeWeaponTrail, List<MeleeWeaponTrail.Point>>("m_points");
        private static readonly AccessTools.FieldRef<MeleeWeaponTrail, List<MeleeWeaponTrail.Point>> s_smoothed = AccessTools.FieldRefAccess<MeleeWeaponTrail, List<MeleeWeaponTrail.Point>>("m_smoothedPoints");
        private static readonly AccessTools.FieldRef<MeleeWeaponTrail, Vector3> s_lastPos = AccessTools.FieldRefAccess<MeleeWeaponTrail, Vector3>("m_lastPosition");

        // Vanilla's cause: while emitting, a weapon that moved less than the minimum vertex distance since the last point takes the
        // "update the last point" branch, which indexes m_points[Count - 1] and m_smoothedPoints[Count - 1] without a Count check. Once
        // every point has expired (a slow companion swing, e.g. the butcher knife), both lists are empty and that index is -1. With no
        // points the right step is "add a point": moving the last position far away sends vanilla down that branch (Count < 3 adds one).
        [HarmonyPrefix]
        private static void Prefix(MeleeWeaponTrail __instance)
        {
            if (__instance == null || !s_emit(__instance)) return;
            var pts = s_points(__instance); var sm = s_smoothed(__instance);
            if ((pts == null || pts.Count > 0) && (sm == null || sm.Count > 0)) return;
            s_lastPos(__instance) = __instance.transform.position + Vector3.up * 1000f;
            if (s_emptyNoted.Add(__instance.GetInstanceID()))
                FiresCore.Logging.FiresLogger.LogDebug($"[WeaponTrail] '{Path(__instance.transform, __instance.GetComponentInParent<Character>())}' was emitting with {pts?.Count ?? -1} point(s) / {sm?.Count ?? -1} smoothed: a new point is started (vanilla would index -1)");
        }

        private static Exception Finalizer(MeleeWeaponTrail __instance, Exception __exception)
        {
            if (__exception == null || __instance == null) return __exception;
            try
            {
                int id = __instance.GetInstanceID();
                if (s_reported.Add(id))
                {
                    var holder = __instance.GetComponentInParent<Character>();
                    bool companion = holder != null && CompanionController.TryGet(holder, out _);
                    FiresCore.Logging.FiresLogger.LogWarning($"[WeaponTrail] '{Path(__instance.transform, holder)}' threw {__exception.GetType().Name} ({__exception.Message}) at {(__exception.StackTrace ?? "").Split('\n')[0].Trim()} on {(holder != null ? "'" + holder.m_name + "'" + (companion ? " (companion)" : holder.IsPlayer() ? " (player)" : "") : "no character")} - this trail is turned off; the weapon keeps working");
                }
                __instance.enabled = false;
                return null;   // swallowed: the trail is off, nothing else depended on this step
            }
            catch { return __exception; }
        }

        private static string Path(Transform t, Character stop)
        {
            var parts = new List<string>();
            for (var x = t; x != null && parts.Count < 8; x = x.parent)
            {
                parts.Insert(0, x.name);
                if (stop != null && x == stop.transform) break;
            }
            return string.Join("/", parts);
        }
    }
}
