using System;
using FiresCore.Config;
using UnityEngine;

namespace FiresCore.Movement.Underwater
{
    /// <summary>
    /// The underwater keys, owned by APM (Fire 11-08: "if apm owns swimming then it needs to own ... all the underwater logic"; the crawl
    /// pattern: APM binds, Core reads). Same key names, defaults and ranges as FTS 0.3.361 had them; APM's one-time copy carries Fire's
    /// values over from com.Fire.FiresTossinShade.cfg. Sections: [Underwater], [Underwater caustics], [Underwater fog].
    /// Until APM provides a key, the fallback below (= FTS's default) is what Core uses. Every change re-applies live (Apply).
    /// </summary>
    internal static class UnderwaterSettings
    {
        internal const string S = "Underwater", SC = "Underwater caustics", SF = "Underwater fog";

        // [Underwater] (FTS [02 - FiresWater] 03-07, [09 - Volumetric Fog] 02e, [09g] 06, [03] 26)
        internal static readonly OwnedSetting<bool> Env = new OwnedSetting<bool>(S, "03 UnderwaterEnv", true);
        internal static readonly OwnedSetting<bool> FollowsCamera = new OwnedSetting<bool>(S, "03b Underwater view follows the camera", true);
        internal static readonly OwnedSetting<bool> Bubbles = new OwnedSetting<bool>(S, "04 UnderwaterBubbles", true);
        internal static readonly OwnedSetting<float> FogDensity = new OwnedSetting<float>(S, "05 UnderwaterFogDensity", 3f);
        internal static readonly OwnedSetting<bool> DepthFog = new OwnedSetting<bool>(S, "05b UnderwaterDepthFog", true);
        internal static readonly OwnedSetting<bool> EnvReactive = new OwnedSetting<bool>(S, "05c UnderwaterEnvReactive", true);
        internal static readonly OwnedSetting<float> Transition = new OwnedSetting<float>(S, "05d UnderwaterTransitionTime", 1.8f);
        internal static readonly OwnedSetting<float> BubbleDensity = new OwnedSetting<float>(S, "06 BubbleDensity", 3f);
        internal static readonly OwnedSetting<float> BubbleSpeed = new OwnedSetting<float>(S, "07 BubbleSpeed", 0.02f);
        internal static readonly OwnedSetting<bool> AffectUnderwater = new OwnedSetting<bool>(S, "06 AffectUnderwater", true);
        internal static readonly OwnedSetting<bool> Dapple = new OwnedSetting<bool>(S, "26 UnderwaterDappleCaustics", false);

        // [Underwater caustics] (FTS [03 - FiresWater Surface] 10b-10d, 17-25, 28)
        internal static readonly OwnedSetting<bool> Caustics = new OwnedSetting<bool>(SC, "19 BelowWaterCaustics", true);
        internal static readonly OwnedSetting<float> PassStrength = new OwnedSetting<float>(SC, "10b CausticPassStrength", 1f);
        internal static readonly OwnedSetting<float> SurfaceView = new OwnedSetting<float>(SC, "10c CausticSurfaceView", 0.8f);
        internal static readonly OwnedSetting<float> UnderwaterView = new OwnedSetting<float>(SC, "10d CausticUnderwaterView", 1f);
        internal static readonly OwnedSetting<float> MoonScale = new OwnedSetting<float>(SC, "10c CausticMoonScale", 1f);
        internal static readonly OwnedSetting<float> Shadow = new OwnedSetting<float>(SC, "17 CausticShadow", 0f);
        internal static readonly OwnedSetting<float> DepthFade = new OwnedSetting<float>(SC, "18 CausticDepthFade", 21.771f);
        internal static readonly OwnedSetting<bool> CausticDebug = new OwnedSetting<bool>(SC, "20 CausticDebug", false);
        internal static readonly OwnedSetting<float> UwScale = new OwnedSetting<float>(SC, "21 UnderwaterCausticScale", 0.144f);
        internal static readonly OwnedSetting<float> UwSharp = new OwnedSetting<float>(SC, "22 UnderwaterCausticSharpness", 0.706f);
        internal static readonly OwnedSetting<float> UwStrength = new OwnedSetting<float>(SC, "23 UnderwaterCausticStrength", 0.194f);
        internal static readonly OwnedSetting<float> UwSpeed = new OwnedSetting<float>(SC, "24 UnderwaterCausticSpeed", 0.019f);
        internal static readonly OwnedSetting<float> UwWarp = new OwnedSetting<float>(SC, "25 UnderwaterCausticWarp", 0.6f);
        internal static readonly OwnedSetting<float> Radius = new OwnedSetting<float>(SC, "28 CausticRadius", 135.694f);

        // [Underwater fog] (FTS [09c - Fog Underwater] 01-11a, [09] 02e)
        internal static readonly OwnedSetting<bool> FogOn = new OwnedSetting<bool>(SF, "01 Enabled", true);
        internal static readonly OwnedSetting<Color> FogColor = new OwnedSetting<Color>(SF, "02 Color", new Color(0x12 / 255f, 0x2B / 255f, 0x45 / 255f, 1f));
        internal static readonly OwnedSetting<Color> FogColorThin = new OwnedSetting<Color>(SF, "02b ColorThin", new Color(0.16f, 0.28f, 0.30f, 1f));
        internal static readonly OwnedSetting<float> Density = new OwnedSetting<float>(SF, "03 Density", 1.728f);
        internal static readonly OwnedSetting<float> MaxOp = new OwnedSetting<float>(SF, "04 MaxOpacity", 0.847f);
        internal static readonly OwnedSetting<float> Patch = new OwnedSetting<float>(SF, "04a Patchiness", 0.3f);
        internal static readonly OwnedSetting<float> Coverage = new OwnedSetting<float>(SF, "04b Coverage", 0.5f);
        internal static readonly OwnedSetting<float> ClearOp = new OwnedSetting<float>(SF, "04c ThinOpacity", 0.6f);
        internal static readonly OwnedSetting<float> PatchFade = new OwnedSetting<float>(SF, "04d PatchFade", 0.3f);
        internal static readonly OwnedSetting<float> PatchScale = new OwnedSetting<float>(SF, "05 SwirlScale", 0.02f);
        internal static readonly OwnedSetting<float> Swirl = new OwnedSetting<float>(SF, "06 SwirlAmount", 1.558f);
        internal static readonly OwnedSetting<float> Current = new OwnedSetting<float>(SF, "07 CurrentSpeed", 0.05f);
        internal static readonly OwnedSetting<float> DistStart = new OwnedSetting<float>(SF, "08 ClearDistance", 2f);
        internal static readonly OwnedSetting<float> DistRange = new OwnedSetting<float>(SF, "09 FadeInDistance", 263.51f);
        internal static readonly OwnedSetting<float> FarCut = new OwnedSetting<float>(SF, "10 FarCutoff", 400f);
        internal static readonly OwnedSetting<float> DepthDensity = new OwnedSetting<float>(SF, "11 DepthDensity", 1f);
        internal static readonly OwnedSetting<float> DepthSpan = new OwnedSetting<float>(SF, "11a DepthSpan", 30f);
        internal static readonly OwnedSetting<float> PerObject = new OwnedSetting<float>(SF, "02e UnderwaterPerObjectFog", 0.15f);

        private static bool s_hooked;

        /// <summary>Copies every key into the parts' statics; hooks each key's Changed once so a change in the config manager applies live.</summary>
        internal static void Apply()
        {
            if (!s_hooked)
            {
                s_hooked = true;
                Action a = Apply;
                foreach (var f in typeof(UnderwaterSettings).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
                {
                    var ev = f.FieldType.GetEvent("Changed");
                    var v = f.GetValue(null);
                    if (ev != null && v != null) ev.AddEventHandler(v, a);
                }
            }

            FiresUnderwaterEnv.Enabled = Env.Value;
            FiresUnderwaterEnv.SetFogDensityScale(FogDensity.Value);
            FiresUnderwaterEnv.DepthFogEnabled = DepthFog.Value;
            FiresUnderwaterEnv.UwTransitionSeconds = Transition.Value;
            FiresUnderwaterBubbles.Enabled = Bubbles.Value;
            FiresUnderwaterBubbles.Density = BubbleDensity.Value;
            FiresUnderwaterBubbles.Speed = BubbleSpeed.Value;
            // FTS bound two keys to this one switch ([02] 05c and [09g] 06, last one applied won); both default on. Core: on only when both are.
            FiresUnderwaterMurk.AffectUnderwater = EnvReactive.Value && AffectUnderwater.Value;

            FiresCausticsPass.Enabled = Caustics.Value;
            FiresCausticsPass.Strength = PassStrength.Value;
            FiresCausticsPass.SurfaceView = SurfaceView.Value;
            FiresCausticsPass.UnderwaterView = UnderwaterView.Value;
            FiresCausticsPass.MoonScale = MoonScale.Value;
            FiresCausticsPass.Shadow = Shadow.Value;
            FiresCausticsPass.DepthFade = DepthFade.Value;
            FiresCausticsPass.DebugView = CausticDebug.Value;
            FiresCausticsPass.Radius = Radius.Value;

            FiresUnderwaterFogPass.UwOn = FogOn.Value;
            FiresUnderwaterFogPass.PerObjectUnderwaterScale = PerObject.Value;
            FiresUnderwaterMurk.BaseChanged();
        }
    }
}
