using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Pieces
{
    // Every WearNTear holds its wear until collision is built on this machine in its zone and the eight around it.
    // Vanilla gives a loaded piece only 30 s, then measures support against whatever colliders exist and breaks a piece
    // that finds none: pieces loaded before a zone's ground had collision, and pieces a server owns where it builds none.
    // Vanilla ground is ready once the zone's heightmap collider has its mesh. A mod that builds its own ground answers
    // for its zones through RegisterGround. The ground must stay ready SettleSeconds, which outlasts collider swaps.
    public static class WearGate
    {
        private const float SettleSeconds = 3f;
        private const float RecheckSeconds = 1f;
        private const float StaleSeconds = 120f;
        private const int PruneAbove = 4096;

        private sealed class ZoneState
        {
            public float CheckedAt = float.MinValue;
            public float ReadySince = -1f;
        }

        private static readonly List<Func<Vector2s, bool?>> s_grounds = new List<Func<Vector2s, bool?>>();
        private static readonly HashSet<Func<Vector2s, bool?>> s_failedGrounds = new HashSet<Func<Vector2s, bool?>>();
        private static readonly Dictionary<Vector2s, ZoneState> s_zones = new Dictionary<Vector2s, ZoneState>();
        private static readonly List<Vector2s> s_stale = new List<Vector2s>();

        // The answer for one zone: true once the mod's collision there is built on this machine, false while it is not,
        // null where the ground is vanilla terrain. The first registered non-null answer decides.
        public static void RegisterGround(Func<Vector2s, bool?> ground)
        {
            if (ground != null && !s_grounds.Contains(ground)) s_grounds.Add(ground);
        }

        public static bool IsReady(Vector3 position)
        {
            if (ZoneSystem.instance == null) return false;
            var zone = ZoneSystem.GetZone(position);
            float now = Time.time;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!ZoneReady(new Vector2s(zone.x + dx, zone.y + dz), now)) return false;
            return true;
        }

        private static bool ZoneReady(Vector2s zone, float now)
        {
            if (!s_zones.TryGetValue(zone, out var state))
            {
                if (s_zones.Count >= PruneAbove) Prune(now);
                s_zones[zone] = state = new ZoneState();
            }
            if (now - state.CheckedAt >= RecheckSeconds)
            {
                state.CheckedAt = now;
                if (!GroundBuilt(zone)) state.ReadySince = -1f;
                else if (state.ReadySince < 0f) state.ReadySince = now;
            }
            return state.ReadySince >= 0f && now - state.ReadySince >= SettleSeconds;
        }

        private static bool GroundBuilt(Vector2s zone)
        {
            foreach (var ground in s_grounds)
            {
                bool? answer = null;
                try { answer = ground(zone); }
                catch (Exception ex)
                {
                    if (s_failedGrounds.Add(ground))
                        FiresCore.Logging.FiresLogger.LogWarning($"[WearGate] a ground answer threw ({ex.Message}); its zones fall back to vanilla terrain.");
                }
                if (answer.HasValue) return answer.Value;
            }
            var heightmap = Heightmap.FindHeightmap(ZoneSystem.GetZonePos(zone));
            if (heightmap == null) return false;
            var collider = heightmap.GetComponent<MeshCollider>();
            return collider != null && collider.enabled && collider.gameObject.activeInHierarchy && collider.sharedMesh != null;
        }

        private static void Prune(float now)
        {
            s_stale.Clear();
            foreach (var entry in s_zones)
                if (now - entry.Value.CheckedAt > StaleSeconds) s_stale.Add(entry.Key);
            foreach (var zone in s_stale) s_zones.Remove(zone);
        }
    }

    [HarmonyPatch(typeof(WearNTear), "ShouldUpdate")]
    internal static class WearNTear_ShouldUpdate_WearGate
    {
        private static void Postfix(WearNTear __instance, ref bool __result)
        {
            if (__result && !WearGate.IsReady(__instance.transform.position)) __result = false;
        }
    }
}
