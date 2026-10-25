using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// The ground something would STAND on at a flat position, near a given height. ZoneSystem.GetGroundHeight casts down from the
    /// sky and returns the topmost surface, so under FAT's voxel ledges, overhangs and cave roofs it gave the top of whatever is
    /// highest (drill goals at y 65 while the bot stood at 40, 18 m away; companion follow, wander and
    /// work spots landed on roofs). Here every solid hit along one ray (from near + ProbeAbove down to near - ProbeBelow) is looked
    /// at, and the upward-facing surface (a floor, not a wall face) closest to 'near' wins; GetGroundHeight only when nothing
    /// solid is loaded there. No allocations. Shared from Core so the bot and the
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
            // 0.2.816 (Fire 10-24, halo ring: "some things that need gravity and 'up' are still not re targeted"): inside a GravityUp field
            // the probe runs along the local up, and the flat heightmap (ZoneSystem.GetGroundHeight) is never used. A height is only a
            // world-y answer when the ring surface found sits at this x/z (within half a metre); anywhere else on the wall it fails
            // cleanly (false) and the caller keeps its own position. Ring-aware callers use Point() instead.
            Vector3 probeAt = new Vector3(at.x, near, at.z);
            if (GravityUp.InField(probeAt, out Vector3 fieldUp))
            {
                height = 0f;
                if (!Point(probeAt, out Vector3 p, out _)) return false;
                if ((p.x - at.x) * (p.x - at.x) + (p.z - at.z) * (p.z - at.z) > 0.25f) return false;
                height = p.y;
                return true;
            }
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

        /// <summary>
        /// 0.2.816 (add-only): the floor a body would stand on nearest <paramref name="at"/>, along the LOCAL up there
        /// (FiresCore.World.GravityUp; world up outside every field, where this is the same answer as <see cref="Height"/> at at.y).
        /// The ray runs from at + up x ProbeAbove to at - up x ProbeBelow; a floor = a hit whose normal faces up (dot >= 0.3).
        /// Inside a field there is no heightmap fallback: false when nothing solid is loaded there.
        /// </summary>
        public static bool Point(Vector3 at, out Vector3 point, out Vector3 up)
        {
            bool inField = GravityUp.InField(at, out up);
            if (!inField)
            {
                bool ok = Height(at, at.y, out float h);
                point = new Vector3(at.x, ok ? h : at.y, at.z);
                return ok;
            }
            if (!s_ringLogged) { s_ringLogged = true; Debug.Log("[GravityUp] Surface probe: ring frame (floors along the local up; no heightmap fallback in the field)"); }
            Vector3 top = at + up * ProbeAbove;
            int n = Physics.RaycastNonAlloc(top, -up, s_hits, ProbeAbove + ProbeBelow, Mask, QueryTriggerInteraction.Ignore);
            point = at;
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                RaycastHit hit = s_hits[i];
                if (Vector3.Dot(hit.normal, up) < FloorNormalY) continue;
                Rigidbody body = hit.collider.attachedRigidbody;
                if (body != null && body.GetComponent<Character>() != null) continue;
                float off = Mathf.Abs(Vector3.Dot(hit.point - at, up));
                if (off < best) { best = off; point = hit.point; found = true; }
            }
            return found;
        }
        private static bool s_ringLogged;
    }

    /// <summary>
    /// Water awareness beyond vanilla. Vanilla's Floating.GetLiquidLevel only
    /// knows vanilla water volumes. FAT's generated lakes and rivers, tool pours, WaterDisk ponds and voxel sheet are invisible to it, so
    /// a deep-water search at a FAT pond found no cells at all. FAT 0.2.673 or later answers all
    /// of them in one query, FatWaterChecks.TryWaterAt. Core asks it through this soft hook (by name, no reference to FAT), so Core
    /// still runs alone, on vanilla water only. Callers take the higher of vanilla's surface and FAT's.
    /// </summary>
    public static class Water
    {
        private const string FatAssembly = "FiresAdminTerrain", FatType = "VerdantsAscent.Water.Core.FatWaterChecks", FatMethod = "TryWaterAt";
        private delegate bool TryWaterAtFn(float x, float z, float minDepth, bool includeSea, out float surface, out float bed);
        private static TryWaterAtFn s_fatWaterAt;
        private static bool s_resolved;

        /// <summary>True once FAT's water query is hooked.</summary>
        public static bool FatHooked { get { Resolve(); return s_fatWaterAt != null; } }

        /// <summary>Finds FAT's query once and says which water Core reads ("[Water] …"). Safe to call again.</summary>
        public static void Resolve()
        {
            if (s_resolved) return;
            s_resolved = true;
            string why = "";
            try
            {
                System.Reflection.Assembly fat = null;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    if (asm.GetName().Name == FatAssembly) { fat = asm; break; }
                System.Type type = fat?.GetType(FatType, false);
                var method = type?.GetMethod(FatMethod, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(float), typeof(float), typeof(float), typeof(bool), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() }, null);
                if (method != null && method.ReturnType == typeof(bool))
                    s_fatWaterAt = (TryWaterAtFn)System.Delegate.CreateDelegate(typeof(TryWaterAtFn), method, false);
                why = fat == null ? $"{FatAssembly} not loaded" : type == null ? $"{FatType} not in {FatAssembly} (FAT older than 0.2.673?)"
                    : method == null ? $"{FatMethod}(float, float, float, bool, out float, out float) not found (FAT older than 0.2.673?)" : "";
            }
            catch (System.Exception e) { s_fatWaterAt = null; why = e.GetType().Name + ": " + e.Message; }
            Debug.Log(s_fatWaterAt != null ? "[Water] FAT water query hooked: FatWaterChecks.TryWaterAt"
                                            : $"[Water] vanilla only ({why})");
        }

        /// <summary>
        /// FAT's water at (x, z) at least <paramref name="minDepth"/> deep, sea included: its surface and the solid bed under it. False
        /// without FAT 0.2.673 or later, or where FAT knows no water. A throwing query is unhooked once (one line) and Core goes on with vanilla water.
        /// </summary>
        public static bool FatWaterAt(float x, float z, float minDepth, out float surface, out float bed)
        {
            surface = bed = 0f;
            Resolve();
            if (s_fatWaterAt == null) return false;
            try { return s_fatWaterAt(x, z, minDepth, true, out surface, out bed); }
            catch (System.Exception e)
            {
                s_fatWaterAt = null;
                Debug.LogWarning($"[Water] FAT water query threw ({e.GetType().Name}: {e.Message}); vanilla only from now on");
                surface = bed = 0f;
                return false;
            }
        }

        /// <summary>The higher of <paramref name="vanillaSurface"/> and FAT's surface at (x, z) (FAT's only where it knows water).</summary>
        public static float SurfaceOr(float x, float z, float vanillaSurface) =>
            FatWaterAt(x, z, 0f, out float surface, out _) ? Mathf.Max(vanillaSurface, surface) : vanillaSurface;
    }
}
