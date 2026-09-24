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
        public static float Sample(Vector3 point, float fallbackY)
        {
            Vector3 origin = new Vector3(point.x, point.y + SampleLiftMeters, point.z);
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, _hits,
                SampleLiftMeters + SampleDropMeters, SolidMask);

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
