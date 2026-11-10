using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// 0.2.919 ([ghost], Fire 10-27: "They shouldn't have to relearn how to get upstairs after their first successful trip up the stairs ...
    /// remember that path and head towards the closest point of that path ... only dropping the path or altering it if something changes").
    /// The corners a companion actually WALKED on a trip that arrived, shared by every companion of this process (the companions' ZDO owner;
    /// not synced, memory only - Fire). A new trip to the same goal joins the stored route at its nearest corner in sight and walks the corners
    /// with no path search; a return trip walks one reversed. Dropped when a companion gets stuck on it, a piece is placed / destroyed within
    /// 2 m of it, after 30 min / 2 in-game days without a success; a route dropped twice for being stuck is blocked for 10 min.
    /// [Companions] Remember routes (default off until a round proves fewer FindPath calls per trip).
    /// </summary>
    internal static class RouteMemory
    {
        internal sealed class Route
        {
            public long Key;
            public Vector3[] Points;
            public Vector3 Min, Max;
            public float Length;
            public float LastOk;
            public long LastOkDay;
            public string Learner;
            public float LastUse;
        }

        private const int MaxRoutes = 256;
        private const float MinLearnLength = 8f;
        internal const float JoinRange = 15f, EndMatch = 2.5f, BuildMargin = 2f;
        private const float MaxAgeSeconds = 1800f, BlockSeconds = 600f;
        private const long MaxAgeDays = 2;

        private static readonly Dictionary<long, Route> s_routes = new Dictionary<long, Route>();
        private static readonly Dictionary<long, (int fails, float until)> s_blocked = new Dictionary<long, (int, float)>();
        private static readonly FiresCore.Config.OwnedSetting<bool> s_on = new FiresCore.Config.OwnedSetting<bool>("Companions", "Remember routes", false);   // 1086 ([generator], Fire 11-01: configs live in their own mods): bound by FiresCompanions

        // 60 s summary counters
        internal static int Hits, Misses, Learned, DropStuck, DropBuild, DropAge, FindPathCalls, FindPathFollowing;
        private static float s_summaryAt;

        internal static void BindConfig(BepInEx.Configuration.ConfigFile cfg)
        { }   // 1086 ([generator], Fire 11-01: configs live in their own mods): nothing to bind

        internal static bool On => s_testOn || s_on.Value;
        internal static bool s_testOn;   // core_route_memory_test only (like MineralSeeds.EnabledForTest); never saved to the cfg

        private static long Cell(Vector3 p, float size) => ((long)Mathf.FloorToInt(p.x / size) & 0x1FFFFF) << 42 | ((long)Mathf.FloorToInt(p.y / size) & 0x1FFFFF) << 21 | ((long)Mathf.FloorToInt(p.z / size) & 0x1FFFFF);
        internal static long KeyOf(Vector3 start, Vector3 goal) => Cell(start, 8f) * 31 ^ Cell(goal, 1f);
        internal static string At(Vector3 p) => $"({p.x:0}, {p.y:0}, {p.z:0})";

        private static long Day() => EnvMan.instance != null && ZNet.instance != null ? EnvMan.instance.GetDay(ZNet.instance.GetTimeSeconds()) : 0;

        /// <summary>A trip that arrived: keep (or refresh) its walked corners.</summary>
        internal static void Learn(string who, Vector3 goal, List<Vector3> walked, float seconds)
        {
            if (walked == null || walked.Count < 3) return;
            float len = 0f; for (int i = 1; i < walked.Count; i++) len += Vector3.Distance(walked[i - 1], walked[i]);
            if (len < MinLearnLength) return;
            long key = KeyOf(walked[0], goal);
            if (s_blocked.TryGetValue(key, out var b) && Time.time < b.until) return;
            if (s_routes.TryGetValue(key, out var old) && old.Length <= len * 1.1f) { old.LastOk = Time.time; old.LastOkDay = Day(); return; }
            var r = new Route { Key = key, Points = walked.ToArray(), Length = len, LastOk = Time.time, LastOkDay = Day(), Learner = who, LastUse = Time.time };
            Bounds(r);
            if (s_routes.Count >= MaxRoutes && !s_routes.ContainsKey(key)) Evict();
            s_routes[key] = r;
            Learned++;
            FiresCore.Logging.FiresLogger.LogDebug($"[Route] {who} -> {At(goal)}: learned ({r.Points.Length} corners, {len:0} m, {seconds:0} s){(old != null ? $" (shorter than the old {old.Length:0} m)" : "")}");
        }

        /// <summary>A route that ended at the goal again: it still works.</summary>
        internal static void Succeeded(Route r) { if (r != null) { r.LastOk = Time.time; r.LastOkDay = Day(); } }

        private static void Bounds(Route r)
        {
            Vector3 mn = r.Points[0], mx = r.Points[0];
            foreach (var p in r.Points) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            r.Min = mn; r.Max = mx;
        }

        private static void Evict()
        {
            long oldest = 0; float at = float.MaxValue;
            foreach (var kv in s_routes) if (kv.Value.LastUse < at) { at = kv.Value.LastUse; oldest = kv.Key; }
            s_routes.Remove(oldest);
        }

        /// <summary>A stored route to <paramref name="goal"/> (or one from it, walked backwards) with a corner within JoinRange of
        /// <paramref name="from"/> in clear sight. Returns the corner list in walking order and the join index.</summary>
        internal static bool Find(Transform me, Vector3 goal, out Route route, out Vector3[] points, out int join)
        {
            route = null; points = null; join = -1;
            Vector3 from = me.position;
            float best = JoinRange;
            List<long> aged = null;
            foreach (var r in s_routes.Values)
            {
                if (Time.time - r.LastOk > MaxAgeSeconds || Day() - r.LastOkDay >= MaxAgeDays) { (aged ??= new List<long>()).Add(r.Key); continue; }
                bool fwd = Vector3.Distance(r.Points[r.Points.Length - 1], goal) <= EndMatch;
                bool rev = !fwd && Vector3.Distance(r.Points[0], goal) <= EndMatch;
                if (!fwd && !rev) continue;
                for (int i = 0; i < r.Points.Length - 1; i++)
                {
                    int idx = fwd ? i : r.Points.Length - 1 - i;
                    float d = Vector3.Distance(from, r.Points[idx]);
                    if (d >= best) continue;
                    if (d > 1.5f && !Core.CompanionReach.CanSee(me, r.Points[idx] + Vector3.up * 0.5f, out _)) continue;
                    best = d; route = r; join = i;
                    points = fwd ? r.Points : Reversed(r.Points);
                }
            }
            if (aged != null) foreach (long k in aged) { var r = s_routes[k]; s_routes.Remove(k); DropAge++; FiresCore.Logging.FiresLogger.LogDebug($"[Route] dropped: age ({r.Points.Length} corners to {At(r.Points[r.Points.Length - 1])}, last success {Time.time - r.LastOk:0} s ago)"); }
            if (route != null) route.LastUse = Time.time;
            return route != null;
        }

        private static Vector3[] Reversed(Vector3[] p) { var r = (Vector3[])p.Clone(); Array.Reverse(r); return r; }

        internal static void DropStuckRoute(Route r, string who, Vector3 at, int corner)
        {
            if (r == null || !s_routes.Remove(r.Key)) return;
            DropStuck++;
            s_blocked.TryGetValue(r.Key, out var b);
            int fails = b.fails + 1;
            s_blocked[r.Key] = (fails, fails >= 2 ? Time.time + BlockSeconds : 0f);
            FiresCore.Logging.FiresLogger.LogDebug($"[Route] {who}: dropped: stuck at {At(at)} corner {corner}/{r.Points.Length}{(fails >= 2 ? $" (2nd time: not re-learned for {BlockSeconds:0} s)" : "")}");
        }

        /// <summary>A piece placed or destroyed at <paramref name="at"/>: every route passing within BuildMargin is dropped.</summary>
        internal static void BuildChanged(Vector3 at, string what)
        {
            if (s_routes.Count == 0) return;
            List<long> hit = null;
            foreach (var r in s_routes.Values)
            {
                if (at.x < r.Min.x - BuildMargin || at.x > r.Max.x + BuildMargin || at.z < r.Min.z - BuildMargin || at.z > r.Max.z + BuildMargin || at.y < r.Min.y - 4f || at.y > r.Max.y + 4f) continue;
                for (int i = 1; i < r.Points.Length; i++)
                    if (SegmentDistance(at, r.Points[i - 1], r.Points[i]) <= BuildMargin) { (hit ??= new List<long>()).Add(r.Key); break; }
            }
            if (hit == null) return;
            foreach (long k in hit)
            {
                var r = s_routes[k]; s_routes.Remove(k); DropBuild++;
                FiresCore.Logging.FiresLogger.LogDebug($"[Route] dropped: build change ({what} at {At(at)}) on the route to {At(r.Points[r.Points.Length - 1])}");
            }
        }

        private static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a; float t = ab.sqrMagnitude < 1e-4f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector3.Distance(p, a + ab * t);
        }

        internal static void TickSummary()
        {
            if (Time.time < s_summaryAt) return;
            bool first = s_summaryAt == 0f;
            s_summaryAt = Time.time + 60f;
            if (first) return;
            FiresCore.Logging.FiresLogger.LogDebug($"[Route] summary 60 s: routes {s_routes.Count}, hits {Hits}, misses {Misses}, learned {Learned}, drops {DropStuck + DropBuild + DropAge} (stuck {DropStuck}, build {DropBuild}, age {DropAge}); companion FindPath {FindPathCalls} ({FindPathFollowing} while on a route)");
            Hits = Misses = Learned = DropStuck = DropBuild = DropAge = FindPathCalls = FindPathFollowing = 0;
        }

        internal static void ClearForTest() { s_routes.Clear(); s_blocked.Clear(); }
        internal static int Count => s_routes.Count;

        // ---- hooks: building changes (placing / destroying a piece), and the companion FindPath count
        [HarmonyPatch(typeof(Player), "PlacePiece")]
        private static class RouteMemory_Placed
        {
            private static void Postfix(Vector3 pos) { try { if (On) BuildChanged(pos, "piece placed"); } catch { } }
        }

        [HarmonyPatch(typeof(WearNTear), "Destroy")]
        private static class RouteMemory_PieceGone
        {
            private static void Prefix(WearNTear __instance) { try { if (On && __instance != null) BuildChanged(__instance.transform.position, Utils.GetPrefabName(__instance.gameObject) + " destroyed"); } catch { } }
        }

        [HarmonyPatch(typeof(BaseAI), "FindPath", new[] { typeof(Vector3) })]
        private static class RouteMemory_FindPathCount
        {
            private static void Prefix(BaseAI __instance)
            {
                if (!(__instance is CompanionAI c)) return;
                FindPathCalls++;
                if (c.OnStoredRoute) FindPathFollowing++;
            }
        }
    }
}
