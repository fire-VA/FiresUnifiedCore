using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Movement.Underwater
{
    /// <summary>
    /// The underwater fog's live colour and density (Core port 11-08 of FTS 0.3.361 FiresFogEnvReactive's underwater branch and
    /// FiresWaterMurkProfiles.ApplyUnderwater; [seasons] reviews, their 0.3.358/359 work). Every frame, in this order (keep it):
    ///  1. the base: [Underwater fog] 02 / 02b / 03 / 04 (APM's keys);
    ///  2. the surface env, when "05c UnderwaterEnvReactive" and "06 AffectUnderwater" are on and FTS publishes its weather: colours lerped
    ///     toward the env fog by UwColorBlend 0.6, density x lerp(0.45, 1, weather01), max opacity max(base, stormOp x 0.9);
    ///  3. the biome water murk, when FTS publishes it (its [03] 50 BiomeMurk on): far colour lerp(.., murk x 0.30, 0.8), near colour
    ///     lerp(.., murk x 0.45, 0.75), density x clamp(biomeMurk x lerp(1, stir, 0.5), 0.3, 2.5).
    /// FTS keeps ONE owner of the weather and murk maths and publishes the results as globals Core only reads:
    ///   _FiresFogEnvColor (VECTOR, rgb = the env's blended fog colour), _FiresFogWeather (x = weather01, y = stormOp = Clamp01(StormOpacity)
    ///   x weather01, z = 1 while FTS's env-reactive fog runs), _FiresWaterMurkColor (VECTOR, rgb after its fog darkening), _FiresWaterMurkStir
    ///   (x = stir, y = biome murk x, z = 1 while on AND a profile has been sampled). The colours are vectors so no gamma/linear conversion
    ///   happens on the way ([seasons] 11-08 review: SetGlobalColor/GetGlobalColor convert in a linear project).
    /// A z of 0 (FTS absent, or that FTS feature off) leaves the step out, so the base look stays.
    /// Debug every 30 s while under: "[Underwater] fog: colour #.. / near #.., density .. (env .., murk ..)".
    /// </summary>
    internal static class FiresUnderwaterMurk
    {
        internal static bool AffectUnderwater = true;
        private const float UwColorBlend = 0.6f, UwClearDayFactor = 0.45f;   // FTS FiresFogEnvReactive constants
        private static readonly int P_EnvColor = Shader.PropertyToID("_FiresFogEnvColor"), P_Weather = Shader.PropertyToID("_FiresFogWeather"),
            P_MurkColor = Shader.PropertyToID("_FiresWaterMurkColor"), P_MurkStir = Shader.PropertyToID("_FiresWaterMurkStir");
        private static float s_sayAt;

        internal static void BaseChanged() => Tick(false);

        /// <summary>Every frame from Underwater (before the fog pass pushes its globals). say = allow the 30 s Debug line.</summary>
        internal static void Tick(bool say)
        {
            Color col = UnderwaterSettings.FogColor.Value, thin = UnderwaterSettings.FogColorThin.Value;
            float density = UnderwaterSettings.Density.Value, maxOp = UnderwaterSettings.MaxOp.Value;
            string envSaid = "off", murkSaid = "off";

            Vector4 weather = Shader.GetGlobalVector(P_Weather);
            if (AffectUnderwater && weather.z > 0.5f)
            {
                // [seasons] 11-08: published / read as a VECTOR, so no gamma->linear conversion on the way (GetGlobalColor in a linear
                // project returns the converted value, and the fog pass's own SetGlobalColor would convert it a second time)
                Color envFog = (Color)Shader.GetGlobalVector(P_EnvColor);
                col = Color.Lerp(col, envFog, UwColorBlend);
                thin = Color.Lerp(thin, envFog, UwColorBlend);
                density *= Mathf.Lerp(UwClearDayFactor, 1f, Mathf.Clamp01(weather.x));
                maxOp = Mathf.Max(maxOp, weather.y * 0.9f);
                envSaid = $"weather {weather.x:0.00}";
            }

            Vector4 stir = Shader.GetGlobalVector(P_MurkStir);
            if (stir.z > 0.5f)
            {
                Color m = (Color)Shader.GetGlobalVector(P_MurkColor);   // a vector, as above
                Color deep = m * 0.3f; deep.a = 1f;
                Color near = m * 0.45f; near.a = 1f;
                col = Color.Lerp(col, deep, 0.8f);
                thin = Color.Lerp(thin, near, 0.75f);
                density *= Mathf.Clamp(stir.y * Mathf.Lerp(1f, stir.x, 0.5f), 0.3f, 2.5f);
                murkSaid = $"biome x{stir.y:0.00}, stir x{stir.x:0.00}";
            }

            FiresUnderwaterFogPass.UwColor = col;
            FiresUnderwaterFogPass.UwColorThin = thin;
            FiresUnderwaterFogPass.UwDensity = density;
            FiresUnderwaterFogPass.UwMaxOp = maxOp;
            FiresUnderwaterFogPass.UwPatch = UnderwaterSettings.Patch.Value;
            FiresUnderwaterFogPass.UwCoverage = UnderwaterSettings.Coverage.Value;
            FiresUnderwaterFogPass.UwClearOp = UnderwaterSettings.ClearOp.Value;
            FiresUnderwaterFogPass.UwPatchFade = UnderwaterSettings.PatchFade.Value;
            FiresUnderwaterFogPass.UwPatchScale = UnderwaterSettings.PatchScale.Value;
            FiresUnderwaterFogPass.UwSwirl = UnderwaterSettings.Swirl.Value;
            FiresUnderwaterFogPass.UwCurrent = UnderwaterSettings.Current.Value;
            FiresUnderwaterFogPass.UwDistStart = UnderwaterSettings.DistStart.Value;
            FiresUnderwaterFogPass.UwDistRange = UnderwaterSettings.DistRange.Value;
            FiresUnderwaterFogPass.UwFarCut = UnderwaterSettings.FarCut.Value;
            FiresUnderwaterFogPass.UwDepthDensity = UnderwaterSettings.DepthDensity.Value;
            FiresUnderwaterFogPass.UwDepthSpan = UnderwaterSettings.DepthSpan.Value;

            if (say && Time.unscaledTime >= s_sayAt)
            {
                s_sayAt = Time.unscaledTime + 30f;
                FiresLogger.LogDebug($"[Underwater] fog: colour #{ColorUtility.ToHtmlStringRGB(col)} / near #{ColorUtility.ToHtmlStringRGB(thin)}, density {density:0.00}, max opacity {maxOp:0.00} (env {envSaid}; murk {murkSaid})");
            }
        }
    }
}
