using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// The ground something would STAND on at a flat position, near a given height. ZoneSystem.GetGroundHeight casts down from the
    /// sky and returns the topmost surface, so under FAT's voxel ledges, overhangs and cave roofs it gave the top of whatever is
    /// highest (R60/R61 on ATomTest: drill goals at y 65 while the bot stood at 40, 18 m away; companion follow, wander and
    /// work spots landed on roofs). Here every solid hit along one ray (from near + ProbeAbove down to near - ProbeBelow) is looked
    /// at, and the upward-facing surface (a floor, not a wall face) closest to 'near' wins; GetGroundHeight only when nothing
    /// solid is loaded there. No allocations. By [visual] (FDT Utilities\Surface.cs), shared from Core so the bot and the
    /// companions agree (one brain).
    /// </summary>
    public static class Surface
    {
        private const float ProbeAbove = 40f, ProbeBelow = 80f, FloorNormalY = 0.3f;
        private static readonly RaycastHit[] s_hits = new RaycastHit[32];
        private static int s_mask;

        /// <summary>Default, static_solid, Default_small, piece, terrain, vehicle.</summary>
        public static int Mask => s_mask != 0 ? s_mask
            : (s_mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle"));

        /// <summary>The ground at at.x/at.z nearest <paramref name="near"/>; false when nothing solid is loaded there and the heightmap has nothing either.</summary>
        public static bool Height(Vector3 at, float near, out float height)
        {
            Vector3 top = new Vector3(at.x, near + ProbeAbove, at.z);
            int n = Physics.RaycastNonAlloc(top, Vector3.down, s_hits, ProbeAbove + ProbeBelow, Mask, QueryTriggerInteraction.Ignore);
            bool found = false;
            height = 0f;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                RaycastHit hit = s_hits[i];
                if (hit.normal.y < FloorNormalY) continue;
                Rigidbody body = hit.collider.attachedRigidbody;
                if (body != null && body.GetComponent<Character>() != null) continue;
                float off = Mathf.Abs(hit.point.y - near);
                if (off < best) { best = off; height = hit.point.y; found = true; }
            }
            if (found) return true;
            return ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(at, out height);
        }

        /// <summary><see cref="Height"/> near <paramref name="at"/>'s own height: for a point made from a real position plus an offset.</summary>
        public static bool GroundNear(Vector3 at, out float height) => Height(at, at.y, out height);

        /// <summary>As <see cref="Height"/>, or <paramref name="near"/> itself when nothing is there.</summary>
        public static float HeightOr(Vector3 at, float near) => Height(at, near, out float h) ? h : near;
    }
}
