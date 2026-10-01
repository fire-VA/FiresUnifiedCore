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

    /// <summary>
    /// 0.2.256 (Fire 10-01: "make sure the core fix goes in so that it can see the water"). Vanilla's Floating.GetLiquidLevel only
    /// knows vanilla water volumes. FAT's generated lakes and rivers, tool pours, WaterDisk ponds and voxel sheet are invisible to it, so
    /// R36's climb drill at the CoopStream pond read "0 deep-water cell(s) tried" where Coop1 had swum 117 times. FAT 0.2.673 answers all
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
        /// without FAT 0.2.673, or where FAT knows no water. A throwing query is unhooked once (one line) and Core goes on with vanilla water.
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
