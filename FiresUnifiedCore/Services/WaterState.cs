using System;
using UnityEngine;

namespace FiresCore.Services
{
    /// <summary>
    /// Cross-mod water-STATE contract: "what is the water doing at this world position?"
    /// Height (tide + waves), horizontal flow (currents), and tide phase — the physics-side
    /// counterpart of <see cref="WaterShaderBridge"/> (which only shares the LOOK).
    ///
    /// The query surface is modeled on Crest Water 4's ICollProvider (MIT, (c) 2019 Wave
    /// Harmonic and contributors — github.com/wave-harmonic/crest): world-point queries for
    /// surface height / flow velocity, provider-owned so the implementation (CPU tide math
    /// today, sampled wave fields later) can evolve without consumers changing.
    ///
    /// Ownership: the tide/current mod (FiresValheimGalaxies) registers the provider; any mod
    /// (swimming, ships, NPC AI, FiresAdminTerrain ponds) consumes through the static
    /// <see cref="WaterState"/> helpers, which never throw and fall back to VANILLA water
    /// (Floating.GetWaterLevel → ZoneSystem baseline) when no provider is present — so every
    /// consumer works on a vanilla install without the tide mod.
    /// </summary>
    public interface IWaterStateProvider
    {
        /// <summary>Absolute world-space Y of the water surface at the position (tide + waves).
        /// waveFactor scales wave contribution exactly like vanilla WaterVolume.GetWaterSurface.</summary>
        float GetWaterSurface(Vector3 worldPos, float waveFactor);

        /// <summary>Horizontal water velocity (m/s, XZ) at the position — ocean/tidal/river
        /// currents. False when no current applies there.</summary>
        bool TryGetFlow(Vector3 worldPos, out Vector3 flow);

        /// <summary>Tide phase, 0 = low, 0.5 = mid, 1 = high.</summary>
        float GetTidePhase01();

        /// <summary>Rate of change of the water surface from tide (m/s; sign = incoming/outgoing).</summary>
        float GetTideVelocity();
    }

    public static class WaterState
    {
        private static IWaterStateProvider s_provider;

        // Vanilla fallback cache — Floating.GetWaterLevel wants a ref WaterVolume it can reuse
        // between calls; main-thread only, like all Valheim water queries.
        private static WaterVolume s_volumeCache;

        public static bool HasProvider => s_provider != null;

        public static void Register(IWaterStateProvider provider) => s_provider = provider;

        public static void Clear() => s_provider = null;

        /// <summary>Optional surface-SHAPING field for the water renderer: given a world XZ,
        /// returns (tideInfluence 0..1, riverRaise metres). The tide/river mod registers it; the
        /// FiresWater shore-field baker samples it per texel so the rendered mesh bends to match
        /// the buoyancy weld (which reads the same numbers on the physics side). Null on a vanilla
        /// install → the baker leaves the surface unshaped (influence 1, raise 0). Additive — does
        /// not touch <see cref="IWaterStateProvider"/>, so existing consumers are unaffected.</summary>
        public static Func<float, float, Vector2> SurfaceFieldSampler;

        /// <summary>Bumped by the field owner whenever the shaping changes (a config toggle/slider),
        /// so the FiresWater shore-field baker knows to re-bake even without the player moving —
        /// live shaping updates, no relog. Cross-mod signal, watched by FiresShoreField.</summary>
        public static int SurfaceFieldVersion;

        /// <summary>Water surface Y at the position. Provider first; vanilla water otherwise.
        /// Never throws — a broken provider degrades to vanilla for that call.</summary>
        public static float GetWaterSurface(Vector3 worldPos, float waveFactor = 1f)
        {
            var p = s_provider;
            if (p != null)
            {
                try { return p.GetWaterSurface(worldPos, waveFactor); }
                catch (Exception ex) { WarnOnce("GetWaterSurface", ex); }
            }
            return VanillaWaterLevel(worldPos);
        }

        /// <summary>Horizontal current at the position; zero/false without a provider.</summary>
        public static bool TryGetFlow(Vector3 worldPos, out Vector3 flow)
        {
            var p = s_provider;
            if (p != null)
            {
                try { return p.TryGetFlow(worldPos, out flow); }
                catch (Exception ex) { WarnOnce("TryGetFlow", ex); }
            }
            flow = Vector3.zero;
            return false;
        }

        public static float GetTidePhase01()
        {
            var p = s_provider;
            if (p != null)
            {
                try { return p.GetTidePhase01(); }
                catch (Exception ex) { WarnOnce("GetTidePhase01", ex); }
            }
            return 0.5f;
        }

        public static float GetTideVelocity()
        {
            var p = s_provider;
            if (p != null)
            {
                try { return p.GetTideVelocity(); }
                catch (Exception ex) { WarnOnce("GetTideVelocity", ex); }
            }
            return 0f;
        }

        /// <summary>How deep the position sits under the surface (positive = submerged by that
        /// many metres, negative = above water).</summary>
        public static float GetSubmersion(Vector3 worldPos, float waveFactor = 1f)
            => GetWaterSurface(worldPos, waveFactor) - worldPos.y;

        /// <summary>Surface normal via central finite differences over the height field —
        /// provider-agnostic (the Crest CPU-provider trick), so it works for any height source.
        /// sampleRadius is the probe half-width in metres.</summary>
        public static Vector3 GetWaterNormal(Vector3 worldPos, float sampleRadius = 0.5f, float waveFactor = 1f)
        {
            float r = Mathf.Max(sampleRadius, 0.01f);
            float hL = GetWaterSurface(worldPos + new Vector3(-r, 0f, 0f), waveFactor);
            float hR = GetWaterSurface(worldPos + new Vector3(r, 0f, 0f), waveFactor);
            float hB = GetWaterSurface(worldPos + new Vector3(0f, 0f, -r), waveFactor);
            float hF = GetWaterSurface(worldPos + new Vector3(0f, 0f, r), waveFactor);
            return Vector3.Normalize(new Vector3(hL - hR, 2f * r, hB - hF));
        }

        /// <summary>Batch height query (Crest ICollProvider-style). Arrays must be equal length.</summary>
        public static void QueryHeights(Vector3[] worldPos, float[] heights, float waveFactor = 1f)
        {
            if (worldPos == null || heights == null) return;
            int n = Mathf.Min(worldPos.Length, heights.Length);
            for (int i = 0; i < n; i++) heights[i] = GetWaterSurface(worldPos[i], waveFactor);
        }

        /// <summary>Vanilla water level: nearest WaterVolume surface (waves included) via
        /// Floating.GetWaterLevel, else the ZoneSystem ocean baseline.</summary>
        public static float VanillaWaterLevel(Vector3 worldPos)
        {
            try { return Floating.GetWaterLevel(worldPos, ref s_volumeCache); }
            catch
            {
                return ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            }
        }

        private static bool s_warned;
        private static void WarnOnce(string call, Exception ex)
        {
            if (s_warned) return;
            s_warned = true;
            Debug.LogWarning($"[FiresCore] WaterState provider threw in {call} (falling back to vanilla; logged once): {ex.Message}");
        }
    }
}
