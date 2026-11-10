using System;
using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// Lee drifts (0.2.829, Fire 10-26: "make drifts pile up behind walls and slopes"); 0.2.830 (Fire: "the snow drift changes to
    /// depth happen with a hard cut ... sections filling in as we walk"): a CONTINUOUS world field. lee / windward are computed at
    /// WORLD grid points (every GridM metres, the same points for every chunk, so chunk borders agree) and sampled bilinearly with a
    /// smoothstep, so there are no cells, no chunk edges and no steps.
    ///   lee      0..1: downwind of a rise (the ground UpwindM upwind is higher) or of an occluder (a piece / rock / tree hit by a ray
    ///            from knee height toward the wind within OccluderM; closer = deeper),
    ///   windward 0..1: the ground rises downwind (a face the wind hits) -> thinner snow.
    /// Wind = vanilla EnvMan.GetWindDir (deterministic from world time on every peer), bucketed to Sectors, changing at most every
    /// MinWindRebuildSeconds; on a change the field EASES from the old sector to the new over BlendSeconds (SnowTicker repaints
    /// the loaded snow chunks a few times meanwhile), so drifts move gradually. Points are dropped near a piece the player places
    /// or destroys (not when pieces stream in). Nothing synced.
    /// </summary>
    internal static class SnowDrift
    {
        private const int Sectors = 8;
        private const float GridM = 4f, UpwindM = 6f, RiseM = 2.5f, OccluderM = 10f, MinWindRebuildSeconds = 60f, BlendSeconds = 20f;
        private static readonly int s_occMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece");

        // key = grid point + sector; value x = lee, y = windward
        private static readonly Dictionary<long, Vector2> s_points = new Dictionary<long, Vector2>();
        private static int s_sector = -1, s_prevSector = -1;
        private static float s_sectorAt = -999f;
        private static int s_built; private static float s_summaryAt;

        /// <summary>The current wind sector, changing at most every MinWindRebuildSeconds.</summary>
        internal static int Sector()
        {
            if (s_sector >= 0 && Time.unscaledTime - s_sectorAt < MinWindRebuildSeconds) return s_sector;
            int now = RawSector();
            if (now != s_sector) { s_prevSector = s_sector < 0 ? now : s_sector; s_sector = now; s_sectorAt = Time.unscaledTime; }
            return s_sector;
        }

        /// <summary>0..1 how far the field has eased from the previous sector to the current one.</summary>
        internal static float Blend => s_prevSector < 0 || s_prevSector == s_sector ? 1f : Mathf.Clamp01((Time.unscaledTime - s_sectorAt) / BlendSeconds);

        /// <summary>True while a wind change is still easing (SnowTicker repaints the loaded snow chunks every few seconds then).</summary>
        internal static bool Easing => Blend < 1f;

        private static int RawSector()
        {
            var env = EnvMan.instance;
            Vector3 w = env != null ? env.GetWindDir() : Vector3.forward;
            w.y = 0f;
            if (w.sqrMagnitude < 1e-4f) return 0;
            float a = Mathf.Atan2(w.x, w.z) * Mathf.Rad2Deg;
            return ((Mathf.RoundToInt(a / (360f / Sectors)) % Sectors) + Sectors) % Sectors;
        }

        private static Vector3 Dir(int sector)
        {
            float a = sector * (360f / Sectors) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
        }

        /// <summary>True when the wind sector moved (the caller starts repainting the loaded snow chunks).</summary>
        internal static bool WindChanged(ref int seen)
        {
            int s = Sector();
            if (s == seen) return false;
            bool first = seen < 0;
            seen = s;
            if (s_points.Count > 60000) s_points.Clear();
            return !first;
        }

        /// <summary>lee / windward 0..1 at a world point: bilinear over the 4 surrounding grid points, smoothstepped, eased
        /// between the previous and current wind sector.</summary>
        internal static void At(Vector3 p, out float lee, out float windward)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), h0 = s_heightTicks, r0 = s_rayTicks; int m0 = s_misses;   // 0.2.1090: timing (lead 11-02, one call took 30 ms in r11)
            int sec = Sector();
            float b = Blend;
            Sample(p, sec, out lee, out windward);
            if (b < 1f && s_prevSector >= 0)
            {
                Sample(p, s_prevSector, out float l0, out float w0);
                float t = b * b * (3f - 2f * b);
                lee = Mathf.Lerp(l0, lee, t); windward = Mathf.Lerp(w0, windward, t);
            }
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (dt > s_slowTicks) { s_slowTicks = dt; s_slowHeight = s_heightTicks - h0; s_slowRay = s_rayTicks - r0; s_slowMisses = s_misses - m0; s_slowAt = p; }
            if (Time.unscaledTime >= s_slowLogAt)
            {
                s_slowLogAt = Time.unscaledTime + 10f;
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                FiresLogger.LogDebug($"[Snow] drift At slowest last 10 s: {s_slowTicks * ms:0.00} ms at ({s_slowAt.x:0},{s_slowAt.z:0}): {s_slowMisses} point(s) built, GetHeight {s_slowHeight * ms:0.00} ms, raycast {s_slowRay * ms:0.00} ms");
                s_slowTicks = 0;
            }
        }

        private static long s_heightTicks, s_rayTicks, s_slowTicks, s_slowHeight, s_slowRay;
        private static int s_misses, s_slowMisses;
        private static Vector3 s_slowAt;
        private static float s_slowLogAt;

        private static void Sample(Vector3 p, int sec, out float lee, out float windward)
        {
            float fx = p.x / GridM, fz = p.z / GridM;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            float tx = fx - x0, tz = fz - z0;
            tx = tx * tx * (3f - 2f * tx); tz = tz * tz * (3f - 2f * tz);
            var a = Point(x0, z0, sec); var bb = Point(x0 + 1, z0, sec); var c = Point(x0, z0 + 1, sec); var d = Point(x0 + 1, z0 + 1, sec);
            var v = Vector2.Lerp(Vector2.Lerp(a, bb, tx), Vector2.Lerp(c, d, tx), tz);
            lee = v.x; windward = v.y;
        }

        private static long Key(int gx, int gz, int sec) => ((long)(gx & 0x1FFFFFF) << 28) | ((long)(gz & 0x1FFFFFF) << 3) | (long)(sec & 7);

        private static Vector2 Point(int gx, int gz, int sec)
        {
            long k = Key(gx, gz, sec);
            if (s_points.TryGetValue(k, out var v)) return v;
            var p = new Vector3(gx * GridM, 0f, gz * GridM);
            v = Vector2.zero;
            s_misses++;
            long ht = System.Diagnostics.Stopwatch.GetTimestamp();
            bool hasGround = Heightmap.GetHeight(p, out float h);
            float hUp = h, hDown = h;
            Vector3 w = Dir(sec);
            if (hasGround)
            {
                Heightmap.GetHeight(p - w * UpwindM, out hUp);
                Heightmap.GetHeight(p + w * UpwindM, out hDown);
            }
            s_heightTicks += System.Diagnostics.Stopwatch.GetTimestamp() - ht;
            if (hasGround)
            {
                float slopeLee = Mathf.Clamp01((hUp - h) / RiseM);
                float windward = Mathf.Clamp01((hDown - h) / RiseM);
                float occ = 0f;
                long rt = System.Diagnostics.Stopwatch.GetTimestamp();
                if (Physics.Raycast(new Vector3(p.x, h + 0.6f, p.z), -w, out var hit, OccluderM, s_occMask, QueryTriggerInteraction.Ignore))
                    occ = 1f - hit.distance / OccluderM;
                s_rayTicks += System.Diagnostics.Stopwatch.GetTimestamp() - rt;
                v = new Vector2(Mathf.Max(slopeLee, occ), windward * (1f - occ));
            }
            s_points[k] = v;
            s_built++;
            if (Time.unscaledTime >= s_summaryAt)
            {
                FiresLogger.LogDebug($"[Snow] drift field: {s_built} grid point(s) built (wind sector {s_sector}, blend {Blend:0.00}), {s_points.Count} cached");
                s_built = 0; s_summaryAt = Time.unscaledTime + 10f;
            }
            return v;
        }

        private static void ForgetNear(Vector3 p)
        {
            int r = Mathf.CeilToInt((OccluderM + UpwindM) / GridM);
            int cx = Mathf.FloorToInt(p.x / GridM), cz = Mathf.FloorToInt(p.z / GridM);
            for (int z = cz - r; z <= cz + r; z++)
                for (int x = cx - r; x <= cx + r; x++)
                    for (int s = 0; s < Sectors; s++) s_points.Remove(Key(x, z, s));
        }

        // building changes the PLAYER makes (placing / destroying a piece): points near it re-test, that zone repaints. Pieces
        // streaming in on load don't (0.2.829 did that on Piece.Awake: repaints while walking = "filling in").
        [HarmonyPatch(typeof(Player), "PlacePiece")]
        private static class Placed
        {
            private static void Postfix(Vector3 pos) { try { Changed(pos); } catch { } }
        }

        [HarmonyPatch(typeof(WearNTear), "Destroy")]
        private static class PieceGone
        {
            private static void Prefix(WearNTear __instance) { try { if (__instance != null) Changed(__instance.transform.position); } catch { } }
        }

        private static readonly HashSet<Vector2s> s_dirtyZones = new HashSet<Vector2s>();
        private static void Changed(Vector3 at)
        {
            if (!SnowField.Enabled || !SnowField.DeepGroundAt(at)) return;
            ForgetNear(at);
            s_dirtyZones.Add(ZoneSystem.GetZone(at));
        }

        /// <summary>Zones whose drifts changed through building since the last call (SnowTicker repaints them on its 2 s poll).</summary>
        internal static HashSet<Vector2s> TakeDirtyZones()
        {
            if (s_dirtyZones.Count == 0) return null;
            var z = new HashSet<Vector2s>(s_dirtyZones);
            s_dirtyZones.Clear();
            return z;
        }
    }
}
