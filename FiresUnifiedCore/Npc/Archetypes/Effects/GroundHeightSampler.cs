using UnityEngine;

namespace FiresCore.Npc.Archetypes.Effects
{
    /// <summary>
    /// Solid-ground height under a world point, true for vanilla heightmap, FiresAdminTerrain voxel chunks and FAT
    /// tiles alike: it asks the colliders, never a prefab name and never the heightmap sampler (whose height is the
    /// clamped vanilla surface, which voxel terrain is only derived from).
    /// </summary>
    public static class GroundHeightSampler
    {
        private const float SampleLiftMeters = 3f;
        private const float SampleDropMeters = 60f;
        private const int HitBufferSize = 16;

        private static readonly string[] SolidLayerNames =
        {
            "terrain", "static_solid", "Default", "Default_small", "piece",
        };

        private static readonly RaycastHit[] _hits = new RaycastHit[HitBufferSize];

        private static int _solidMask;
        private static bool _solidMaskReady;

        /// <summary>Mirrors ZoneSystem.m_solidRayMask, which is what vanilla's own GetSolidHeight uses.</summary>
        public static int SolidMask
        {
            get
            {
                if (!_solidMaskReady)
                {
                    _solidMask = LayerMask.GetMask(SolidLayerNames);
                    _solidMaskReady = true;
                }
                return _solidMask;
            }
        }

        /// <summary>
        /// Height of the first solid surface below <paramref name="point"/> + a small lift. Rigidbody colliders are
        /// skipped so a passing cart or boat never lifts a ring; falls back to ZoneSystem, then to the point itself.
        /// </summary>
        public static float Sample(Vector3 point, float fallbackY) => Sample(point, fallbackY, SolidMask);

        /// <summary>Terrain and build
        /// pieces only - no rocks, trees or other static colliders. Add-only.</summary>
        public static int GroundOnlyMask { get { if (!_groundMaskReady) { _groundMask = LayerMask.GetMask("terrain", "piece"); _groundMaskReady = true; } return _groundMask; } }
        private static int _groundMask; private static bool _groundMaskReady;

        /// <summary>
        /// Halo ring (add-only): the first solid surface below <paramref name="point"/> along the LOCAL up (FiresCore.World.GravityUp).
        /// Outside every field this is exactly (point.x, <see cref="Sample(Vector3, float, int)"/>, point.z) and up = Vector3.up.
        /// In a field the ray runs from point + up x lift along -up, with no heightmap fallback (the flat world floor is not the ring);
        /// nothing solid = <paramref name="point"/> itself.
        /// </summary>
        public static Vector3 SamplePoint(Vector3 point, int mask, out Vector3 up)
        {
            if (!FiresCore.World.GravityUp.InField(point, out up))
            {
                up = Vector3.up;
                return new Vector3(point.x, Sample(point, point.y, mask), point.z);
            }
            if (!_ringLogged) { _ringLogged = true; Debug.Log($"[GravityUp] GroundHeightSampler: ring frame at {point:F0}, up {up:F2} (ground rings and FX sample along -up)"); }
            int count = Physics.RaycastNonAlloc(point + up * SampleLiftMeters, -up, _hits, SampleLiftMeters + SampleDropMeters, mask);
            float bestDistance = float.MaxValue;
            Vector3 best = point;
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (hit.collider == null || hit.collider.attachedRigidbody != null) continue;
                if (hit.distance >= bestDistance) continue;
                bestDistance = hit.distance;
                best = hit.point;
            }
            return best;
        }

        /// <summary><see cref="SamplePoint(Vector3, int, out Vector3)"/> on <see cref="SolidMask"/>. Add-only.</summary>
        public static Vector3 SamplePoint(Vector3 point, out Vector3 up) => SamplePoint(point, SolidMask, out up);
        private static bool _ringLogged;

        /// <summary><see cref="Sample(Vector3, float)"/> with the caller's layer mask (e.g. <see cref="GroundOnlyMask"/>). Add-only.</summary>
        public static float Sample(Vector3 point, float fallbackY, int mask)
        {
            Vector3 origin = new Vector3(point.x, point.y + SampleLiftMeters, point.z);
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, _hits,
                SampleLiftMeters + SampleDropMeters, mask);

            float bestDistance = float.MaxValue;
            float bestY = 0f;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (hit.collider == null || hit.collider.attachedRigidbody != null) continue;
                if (hit.distance >= bestDistance) continue;

                bestDistance = hit.distance;
                bestY = hit.point.y;
                found = true;
            }

            if (found) return bestY;

            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem != null && zoneSystem.GetSolidHeight(origin, out float solidHeight))
                return solidHeight;

            return fallbackY;
        }
    }
}
