using System;
using UnityEngine;

namespace FiresCore.Services
{
    // Lets gameplay mods that build water, ice or snow meshes get the FiresWater look for a captured vanilla water
    // material without referencing the shader mod: FiresTossinShade registers a wrap delegate and callers use
    // Wrap(material). With no delegate registered Wrap returns the material unchanged, so vanilla water renders.
    // A delegate rather than a lookup table, because each wrap builds a fresh material from the instance passed in.
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
