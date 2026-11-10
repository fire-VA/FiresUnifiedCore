using System;
using System.Reflection;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Movement.Underwater
{
    /// <summary>
    /// The ONE "where is the water surface over this point" for the underwater view (Core port 11-08 of FTS 0.3.361
    /// FiresVolumetricFogPass.TrueWaterY / SeaY / GalaxiesOffsetAt / CaveFactor; Fire: "if apm owns swimming then it needs to own ... all
    /// the underwater logic"). FTS's sea fog and shore bake call this too once FTS ships its side, so there is one implementation.
    ///
    /// Sources, in order (each bound by reflection, absent = the next one):
    ///  - a halo ring (Core GravityUp.InField): no sea at all;
    ///  - the sea: c_WaterLevel + Galaxies' shaped offset (GalaxiesApi.WaterSurfaceOffsetAt, 0.1.71+, cached per 2 m cell), else the global
    ///    tide FTS publishes (_FiresTideOffset);
    ///  - inland water: FAT's layered answer (FatCaveSignals.TryWaterSurfaceAt, 0.2.835+) wins when above the sea and above the point;
    ///    without FAT, vanilla Floating.GetWaterLevel.
    /// CaveFactor: FAT's continuous CameraCaveFactor when present, else its CameraInCave debounced (3 s hold, 4 s fade: FTS's defaults).
    /// Debug lines (once per bind): "[Underwater] sea level: ...", "[Underwater] FAT TryWaterSurfaceAt found|not found", "cave factor ...".
    /// </summary>
    public static class WaterSurface
    {
        private static readonly int P_TideOffset = Shader.PropertyToID("_FiresTideOffset");

        // ── Galaxies shaped sea ──
        private static bool s_galLooked; private static Func<float, float, float> s_galOffset;
        private static int s_galCx = int.MinValue, s_galCz = int.MinValue, s_galFrame = -1; private static float s_galY;

        /// <summary>Galaxies' shaped sea offset at (x, z) (tide x TideInfluence x ice hold + RiverRaise); false without Galaxies 0.1.71+.</summary>
        public static bool GalaxiesOffsetAt(float x, float z, out float offset)
        {
            offset = 0f;
            BindGalaxies();
            if (s_galOffset == null) return false;
            try { offset = s_galOffset(x, z); return true; }
            catch (Exception e) { s_galOffset = null; FiresLogger.LogDebug($"[Underwater] Galaxies WaterSurfaceOffsetAt threw ({e.Message}): global tide"); return false; }
        }

        private static void BindGalaxies()
        {
            if (s_galLooked) return;
            s_galLooked = true;
            try
            {
                var t = FiresCore.Compat.OptionalTypes.Find("Galaxies.GalaxiesApi");   // 0.2.1208: silent when Galaxies is not installed (TypeByName warned players)
                var m = t?.GetMethod("WaterSurfaceOffsetAt", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(float), typeof(float) }, null);
                if (m != null && m.ReturnType == typeof(float)) s_galOffset = (Func<float, float, float>)Delegate.CreateDelegate(typeof(Func<float, float, float>), m);
            }
            catch (Exception e) { FiresLogger.LogDebug($"[Underwater] Galaxies WaterSurfaceOffsetAt bind failed: {e.Message}"); }
            FiresLogger.LogDebug($"[Underwater] sea level: {(s_galOffset != null ? "Galaxies WaterSurfaceOffsetAt (shaped tide, the swim's own surface)" : "global tide (no Galaxies 0.1.71+)")}");
        }

        private static float SeaY(Vector3 p, out string kind)
        {
            BindGalaxies();
            if (s_galOffset != null)
            {
                int cx = Mathf.FloorToInt(p.x * 0.5f), cz = Mathf.FloorToInt(p.z * 0.5f);
                if (cx != s_galCx || cz != s_galCz || Time.frameCount - s_galFrame > 30)
                {
                    if (GalaxiesOffsetAt(p.x, p.z, out float off)) { s_galY = ZoneSystem.c_WaterLevel + off; s_galCx = cx; s_galCz = cz; s_galFrame = Time.frameCount; }
                }
                if (s_galOffset != null) { kind = "sea (Galaxies shaped)"; return s_galY; }
            }
            kind = "sea (tide global)";
            return ZoneSystem.c_WaterLevel + Shader.GetGlobalFloat(P_TideOffset);
        }

        // ── FAT inland water ──
        private static bool s_wsBound;
        private static MethodInfo s_waterAt;
        private static WaterVolume s_camWv;
        private static readonly object[] s_waterArgs = new object[4];

        /// <summary>The water surface Y over p (-10000 = none), and what it is ("sea (...)", "inland (FAT)", "inland", "none (halo ring)").</summary>
        public static float TrueY(Vector3 p, out string kind)
        {
            if (InHaloField(p)) { kind = "none (halo ring)"; return -10000f; }
            float y = SeaY(p, out kind);
            if (!s_wsBound)
            {
                s_wsBound = true;
                var t = FatCaveSignalsType.Get();   // 0.2.1207: quiet lookup (TypeByName warned players when FAT is not installed)
                s_waterAt = t?.GetMethod("TryWaterSurfaceAt", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(float), typeof(float), typeof(float), typeof(float).MakeByRefType() }, null);
                FiresLogger.LogDebug($"[Underwater] FAT TryWaterSurfaceAt {(s_waterAt != null ? "found" : "not found (FAT older than 0.2.835): sea + GetWaterLevel only")}");
            }
            if (s_waterAt != null)
            {
                try
                {
                    s_waterArgs[0] = p.x; s_waterArgs[1] = p.y; s_waterArgs[2] = p.z; s_waterArgs[3] = 0f;
                    if ((bool)s_waterAt.Invoke(null, s_waterArgs))
                    { float w = (float)s_waterArgs[3]; if (w > y && p.y < w) { y = w; kind = "inland (FAT)"; } }
                }
                catch (Exception) { s_waterAt = null; }
            }
            // Without FAT's layered answer, fall back to Floating (topmost surface: a river over a dry grotto's roof reads as "under").
            if (s_waterAt == null)
            {
                float v = Floating.GetWaterLevel(p, ref s_camWv);
                if (v > -1000f && v > y && p.y < v) { y = v; kind = "inland"; }
            }
            return y;
        }

        // ── Halo ring (Core's own GravityUp: no reflection needed inside Core) ──
        public static bool InHaloField(Vector3 p)
        {
            try { return FiresCore.World.GravityUp.Active && FiresCore.World.GravityUp.InField(p); } catch { return false; }
        }

        // ── Cave factor (also the caustics pass gate) ──
        private const float CaveFadeSeconds = 4f, CaveFlipHoldSeconds = 3f;   // FTS [09] 02d / 02g defaults
        private static PropertyInfo s_inCave;
        private static Func<float> s_caveFactor;
        private static bool s_inCaveBound;
        private static float s_caveK;
        private static bool s_caveLogged, s_caveRaw, s_caveHeld;
        private static float s_caveRawSince;

        public static float CaveFactor()
        {
            if (!s_inCaveBound)
            {
                s_inCaveBound = true;
                var t = FatCaveSignalsType.Get();   // 0.2.1207: quiet lookup (TypeByName warned players when FAT is not installed)
                const BindingFlags bf = BindingFlags.Public | BindingFlags.Static;
                s_inCave = t?.GetProperty("CameraInCave", bf);
                var cfProp = t?.GetProperty("CameraCaveFactor", bf);
                var cfField = cfProp == null ? t?.GetField("CameraCaveFactor", bf) : null;
                if (cfProp != null && cfProp.PropertyType == typeof(float)) { var g = cfProp.GetGetMethod(); s_caveFactor = () => (float)g.Invoke(null, null); }
                else if (cfField != null && cfField.FieldType == typeof(float)) s_caveFactor = () => (float)cfField.GetValue(null);
                FiresLogger.LogDebug($"[Underwater] cave factor: {(s_caveFactor != null ? "FAT's continuous CameraCaveFactor" : s_inCave != null ? "FAT's CameraInCave, debounced" : "no FAT cave signals (always open sky)")}");
            }
            if (s_caveFactor != null)
            {
                try { s_caveK = Mathf.Clamp01(s_caveFactor()); return s_caveK; }
                catch (Exception) { s_caveFactor = null; }
            }
            bool raw = false;
            if (s_inCave != null) { try { raw = (bool)s_inCave.GetValue(null, null); } catch (Exception) { s_inCave = null; } }
            if (raw != s_caveRaw) { s_caveRaw = raw; s_caveRawSince = Time.time; }
            if (s_caveHeld != s_caveRaw && Time.time - s_caveRawSince >= CaveFlipHoldSeconds) s_caveHeld = s_caveRaw;
            s_caveK = Mathf.MoveTowards(s_caveK, s_caveHeld ? 1f : 0f, Time.deltaTime / CaveFadeSeconds);
            if (s_caveHeld != s_caveLogged) { s_caveLogged = s_caveHeld; FiresLogger.LogDebug($"[Underwater] camera {(s_caveHeld ? "entered a cave" : "left the caves")}"); }
            return s_caveK;
        }
    }
}
