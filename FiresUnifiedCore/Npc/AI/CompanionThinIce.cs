using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Companions treat thin ice (under <see cref="ThinIceMetres"/>, the thickness that cracks) as water ([lead] overnight 10-28, the
    /// parked Fire item): a NEW companion path that crosses it gets a side detour around the ice, and when no detour is clear the path
    /// is refused (the companion's usual unreachable handling then applies) instead of walking over and through the crack.
    /// The ice comes from FAT (FatApi.TryIceAt, bound by reflection); without FAT or its ice API this does nothing. Only CompanionAI.
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class CompanionThinIce
    {
        internal const float ThinIceMetres = 0.15f;
        private const float SampleStep = 1f;
        private static readonly float[] DetourOffsets = { 3f, -3f, 6f, -6f, 10f, -10f };

        private delegate bool IceQuery(Vector3 p, out float coverage01, out float thicknessM);
        private static IceQuery s_tryIceAt;
        private static bool s_looked;

        private static bool TryIce(Vector3 p, out float thickness)
        {
            thickness = 0f;
            if (!s_looked) Look();
            if (s_tryIceAt == null) return false;
            try { return s_tryIceAt(p, out float cov, out thickness) && cov > 0.5f; } catch { return false; }
        }

        private static void Look()
        {
            s_looked = true;
            MethodInfo m = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("VerdantsAscent.FatApi", false);
                m = t?.GetMethod("TryIceAt", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() }, null);
                if (m != null) break;
            }
            if (m != null) s_tryIceAt = (IceQuery)Delegate.CreateDelegate(typeof(IceQuery), m);
            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionPath] thin-ice rule: {(m != null ? "FAT ice API found (FatApi.TryIceAt); companions route around ice under " + ThinIceMetres + " m" : "no FAT ice API - rule off")}");
        }

        private static bool IsThin(Vector3 p, out float thickness) => TryIce(p, out thickness) && thickness < ThinIceMetres;

        /// <summary>First thin-ice point on the segment a→b (sampled every metre), if any.</summary>
        private static bool FirstThin(Vector3 a, Vector3 b, out Vector3 hit, out float thickness)
        {
            hit = default; thickness = 0f;
            float len = Vector3.Distance(a, b);
            int n = Mathf.Max(1, Mathf.CeilToInt(len / SampleStep));
            for (int i = 1; i <= n; i++)
            {
                Vector3 p = Vector3.Lerp(a, b, i / (float)n);
                if (IsThin(p, out thickness)) { hit = p; return true; }
            }
            return false;
        }

        [HarmonyPatch(typeof(BaseAI), "FindPath")]
        private static class BaseAI_FindPath_ThinIce
        {
            [HarmonyPriority(Priority.Low)]   // after the corner easing (CompanionPathRules)
            private static void Postfix(BaseAI __instance, ref bool __result)
            {
                if (!__result || !(__instance is CompanionAI) || __instance.m_lastFindPathTime != Time.time) return;
                Character body = __instance.m_character;
                List<Vector3> path = __instance.m_path;
                if (body == null || body.IsFlying() || path == null || path.Count < 2) return;
                if (!s_looked) Look();
                if (s_tryIceAt == null) return;
                if (IsThin(body.transform.position, out _)) return;   // already on it: let it walk off
                try
                {
                    for (int i = 0; i + 1 < path.Count; i++)
                    {
                        if (!FirstThin(path[i], path[i + 1], out Vector3 hit, out float th)) continue;
                        Vector3 seg = path[i + 1] - path[i];
                        Vector3 side = Vector3.Cross(Vector3.up, new Vector3(seg.x, 0f, seg.z).normalized);
                        bool rerouted = false;
                        foreach (float off in DetourOffsets)
                        {
                            Vector3 via = hit + side * off;
                            if (IsThin(via, out _) || FirstThin(path[i], via, out _, out _) || FirstThin(via, path[i + 1], out _, out _)) continue;
                            if (ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(via, out float y, 1000)) via.y = y;
                            path.Insert(i + 1, via);
                            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionPath] thin ice treated as water at ({hit.x:0},{hit.z:0}) ({th:0.00} m < {ThinIceMetres} m): {body.m_name} detours {off:+0;-0} m");
                            rerouted = true;
                            break;
                        }
                        if (!rerouted)
                        {
                            path.Clear();
                            __result = false;
                            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionPath] thin ice treated as water at ({hit.x:0},{hit.z:0}) ({th:0.00} m < {ThinIceMetres} m): {body.m_name} has no clear detour - path refused");
                            return;
                        }
                        i++;   // the detour leg was checked when it was picked
                    }
                }
                catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[CompanionPath] thin-ice check skipped: " + e.Message); }
            }
        }
    }
}
