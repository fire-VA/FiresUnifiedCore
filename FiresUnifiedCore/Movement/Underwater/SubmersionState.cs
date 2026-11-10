using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Movement.Underwater
{
    /// <summary>
    /// Whether the local player's view is underwater, read off the vanilla
    /// <c>Player.m_swimDepth</c> field. Self-contained so the shader mod owns its
    /// underwater-view trigger without depending on the diving mechanic (a separate mod).
    ///
    /// HYSTERESIS: a single depth threshold strobes the underwater view on
    /// and off as each wave rolls the head across the line — worse near shore, where the
    /// visual wave mesh and the physical buoyancy surface don't perfectly agree, so the
    /// body bobs relative to what you SEE. Fix is a hysteresis band plus a minimum hold:
    /// once the view commits above/below it won't flip back until the head clears the
    /// FAR threshold, and never faster than <see cref="MinHoldSeconds"/>. One wave can no
    /// longer toggle it — it takes a sustained, decisive crossing.
    /// </summary>
    internal static class SubmersionState
    {
        private const float DefaultSwimDepth = 1.4f;   // vanilla treading depth

        // Hysteresis band around treading depth: go UNDER only past EnterDepth, come
        // back UP only once shallower than ExitDepth. The 0.3m gap is wider than typical
        // near-shore wave chop, so a passing swell can't strobe the view.
        private const float EnterDepth = DefaultSwimDepth + 0.30f;   // 1.70 — commit to underwater
        private const float ExitDepth  = DefaultSwimDepth + 0.05f;   // 1.45 — commit to above-water
        private const float MinHoldSeconds = 0.4f;                   // floor on how fast the view can flip

        private static bool s_submerged;
        private static float s_lastFlip;

        // "03b Underwater view follows the camera" (default ON). The camera's depth under the water surface it is in
        // (FiresVolumetricFogPass.TrueWaterY: sea + tide, FAT inland water) with its own hysteresis band.
        // Core port: APM's [Underwater] "03b Underwater view follows the camera" (UnderwaterSettings.FollowsCamera, read live)
        // 0.3.295 ([visual], Fire 11-02: tint only the area under the waterline): the first StraddleBand metres keep the surface view; FiresVolumetricFogPass splits that band per pixel
        internal const float StraddleBand = 0.6f;
        private const float CamEnterDepth = StraddleBand, CamExitDepth = StraddleBand - 0.2f;   // 0.3.294 ([visual], Fire 11-02 waterline split): under as soon as the camera is under (was 0.20: the first 0.2 m drew the bed in above-water light)   // under by 0.2 m to go under; above by 0.05 m to surface

        // FAT's cave/shaft signals by reflection (FatCaveSignals, FAT 0.2.835+); absent = only the land-column test.
        private static bool s_fatBound;
        private static System.Reflection.PropertyInfo s_inCave;
        private static System.Reflection.MethodInfo s_shaft;
        private static readonly object[] s_shaftArgs = new object[6];

        private static Vector3 s_seaAt = new Vector3(float.NaN, 0f, 0f);
        private static bool s_seaHere;

        // Cached per 2 m of camera travel (GetHeight is noise math, the shaft list a reflection loop).
        private static bool SeaReaches(Vector3 c)
        {
            if (!float.IsNaN(s_seaAt.x) && (c - s_seaAt).sqrMagnitude < 4f) return s_seaHere;
            s_seaAt = c;
            s_seaHere = SeaReachesUncached(c);
            return s_seaHere;
        }

        private static bool SeaReachesUncached(Vector3 c)
        {
            if (!s_fatBound)
            {
                s_fatBound = true;
                var t = FatCaveSignalsType.Get();   // 0.2.1207: quiet lookup (TypeByName warned players when FAT is not installed)
                s_inCave = t?.GetProperty("CameraInCave", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                s_shaft = t?.GetMethod("TryGetShaft", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            }
            try
            {
                if (s_inCave != null && (bool)s_inCave.GetValue(null)) return false;
                if (s_shaft != null)
                    for (int i = 0; i < 256; i++)
                    {
                        s_shaftArgs[0] = i;
                        if (!(bool)s_shaft.Invoke(null, s_shaftArgs)) break;
                        float dx = c.x - (float)s_shaftArgs[1], dz = c.z - (float)s_shaftArgs[2], r = (float)s_shaftArgs[3];
                        if (dx * dx + dz * dz <= r * r) return false;
                    }
            }
            catch (System.Exception) { s_inCave = null; s_shaft = null; }
            // The ground of this column (WorldGenerator: FAT's own ground on a Fires world): at or above sea level, no sea here.
            if (WorldGenerator.instance != null && WorldGenerator.instance.GetHeight(c.x, c.z) >= ZoneSystem.c_WaterLevel - 0.5f) return false;
            return true;
        }

        public static bool LocalIsSubmerged()
        {
            var p = Player.m_localPlayer;
            if (p == null) { s_submerged = false; return false; }

            if (UnderwaterSettings.FollowsCamera.Value)
            {
                var cam = Utils.GetMainCamera();
                if (cam == null) return s_submerged;
                Vector3 c = cam.transform.position;
                float surface = WaterSurface.TrueY(c, out string kind);
                // "sea" only where the sea
                // can reach the camera (not in a FAT cave, not in a known sinkhole shaft, and over a column whose ground is below
                // sea level). FAT inland water answers for itself and is not filtered.
                if (kind.StartsWith("sea (", System.StringComparison.Ordinal) && !SeaReaches(c))   // 0.3.344: "sea (Galaxies shaped)" / "sea (tide global)"
                { surface = -10000f; kind = "sea not here (cave/shaft/land column)"; }
                float depth = surface > -1000f ? surface - c.y : -1f;
                bool wantCam = s_submerged ? (depth > CamExitDepth) : (depth > CamEnterDepth);
                if (wantCam != s_submerged && (wantCam || Time.time - s_lastFlip >= MinHoldSeconds))   // 0.3.294: the hold only delays surfacing
                {
                    s_submerged = wantCam;
                    s_lastFlip = Time.time;
                    FiresCore.Logging.FiresLogger.LogDebug($"[TossinShade] underwater view {(wantCam ? "ON" : "OFF")}: camera y {c.y:F2}, surface {surface:F2} ({kind}), depth {depth:F2} m (Underwater view follows the camera = ON)");
                }
                return s_submerged;
            }

            float d = p.m_swimDepth;
            // Inside the band [ExitDepth, EnterDepth] the state is held (that's the
            // hysteresis); only a crossing of the far edge proposes a flip.
            bool want = s_submerged ? (d > ExitDepth) : (d > EnterDepth);

            if (want != s_submerged && Time.time - s_lastFlip >= MinHoldSeconds)
            {
                s_submerged = want;
                s_lastFlip = Time.time;
            }
            return s_submerged;
        }
    }
}
