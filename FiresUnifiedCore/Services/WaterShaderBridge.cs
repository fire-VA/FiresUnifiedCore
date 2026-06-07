using System;
using UnityEngine;

namespace FiresCore.Services
{
    // Cross-mod bridge for "give this captured vanilla water material the
    // custom water look." The shader mod (FiresTossinShade) owns the actual
    // wrap — it alone knows the FiresWater shader's properties (foam globals,
    // wind, depth array, underwater tint). Gameplay mods that build custom
    // water/ice/snow meshes (e.g. FiresAdminTerrain) capture the vanilla water
    // material and call Wrap() to get the FiresWater-shaded version WITHOUT
    // referencing the shader mod, its bundle, or its shader.
    //
    // Stays a complete no-op until the shader mod registers a wrapper: Wrap()
    // returns the source material unchanged, so an install WITHOUT the shader
    // mod renders plain vanilla water (independent install). With the shader
    // mod present, it registers on load:
    //
    //   // shader mod (FiresTossinShade), on load and whenever its toggle flips:
    //   WaterShaderBridge.Register(FiresWaterShaderLoader.MaybeWrap);
    //
    //   // gameplay mod (FiresAdminTerrain), per captured material:
    //   var mat = WaterShaderBridge.Wrap(capturedVanillaWater);
    //
    // Unlike ShaderSwapper/MaterialSwapper (static lookup tables), the wrap is
    // dynamic — it builds a fresh material from the specific captured instance
    // handed in — so this is a registered delegate rather than a registry.
    public static class WaterShaderBridge
    {
        // Set by the shader mod. Takes a source (captured vanilla) material and
        // returns a custom-shaded material — typically a NEW material so the
        // source stays untouched for the caller's fallback / re-tint paths. May
        // return the source unchanged if the wrap can't be built yet (bundle not
        // loaded), in which case the caller should retry on a later call.
        private static Func<Material, Material> s_wrapper;

        // True once the shader mod has registered a wrapper. Callers can use
        // this to avoid caching a "wrapped" result before a provider exists.
        public static bool HasWrapper => s_wrapper != null;

        public static void Register(Func<Material, Material> wrapper) => s_wrapper = wrapper;

        public static void Clear() => s_wrapper = null;

        // Returns a custom-shaded version of source, or source unchanged when no
        // shader mod is present / the wrap isn't ready yet / source is null.
        // Never throws — a misbehaving wrapper falls back to the source so a bug
        // in the shader mod can't break a gameplay mod's water meshes.
        public static Material Wrap(Material source)
        {
            if (source == null) return null;
            var wrapper = s_wrapper;
            if (wrapper == null) return source;
            try
            {
                var wrapped = wrapper(source);
                return wrapped != null ? wrapped : source;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WaterShaderBridge] wrapper threw, returning source: {ex}");
                return source;
            }
        }
    }
}
