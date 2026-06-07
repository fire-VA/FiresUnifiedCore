using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Services
{
    // Cross-mod shader-swap registry. Stays a complete no-op until at
    // least one consumer mod registers a swap, at which point Apply
    // calls anywhere in the Fires-* family will respect the registered
    // rules.
    //
    // Pattern:
    //   ShaderSwapper.Register("Custom/Water", myCustomWaterShader);
    //   ...
    //   ShaderSwapper.Apply(material);                  // swaps if rule matches
    //   ShaderSwapper.ApplyToRenderer(renderer);        // sweeps every mat
    //   ShaderSwapper.ApplyToMaterials(bundleMats);     // bulk import path
    //
    // Match is by Shader.name (the asset name, not the path), so a swap
    // registered for "Custom/Water" hits any material whose current
    // shader reports that name even if loaded from a different bundle.
    public static class ShaderSwapper
    {
        private static readonly Dictionary<string, Shader> Swaps
            = new Dictionary<string, Shader>(StringComparer.Ordinal);

        // True when at least one swap rule is registered. Callers can use
        // this to short-circuit expensive Apply loops in mods that don't
        // need to scan when nothing's registered.
        public static bool HasAny => Swaps.Count > 0;

        public static int Count => Swaps.Count;

        public static void Register(string fromShaderName, Shader toShader)
        {
            if (string.IsNullOrEmpty(fromShaderName)) return;
            if (toShader == null) return;
            Swaps[fromShaderName] = toShader;
        }

        public static bool Unregister(string fromShaderName)
        {
            if (string.IsNullOrEmpty(fromShaderName)) return false;
            return Swaps.Remove(fromShaderName);
        }

        public static void Clear() => Swaps.Clear();

        public static bool TryGet(string fromShaderName, out Shader toShader)
        {
            if (string.IsNullOrEmpty(fromShaderName))
            {
                toShader = null;
                return false;
            }
            return Swaps.TryGetValue(fromShaderName, out toShader);
        }

        // Returns true if the material's shader was swapped.
        public static bool Apply(Material material)
        {
            if (material == null) return false;
            if (material.shader == null) return false;
            if (!Swaps.TryGetValue(material.shader.name, out var replacement)) return false;
            if (replacement == null) return false;
            if (material.shader == replacement) return false;
            material.shader = replacement;
            return true;
        }

        // Returns the number of materials swapped on the renderer.
        public static int ApplyToRenderer(Renderer renderer)
        {
            if (renderer == null) return 0;
            int swapped = 0;
            var mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
                if (Apply(mats[i])) swapped++;
            return swapped;
        }

        // Bulk path for import-time loops (asset bundles, prefab graphs).
        // Returns the number of materials swapped across the sequence.
        public static int ApplyToMaterials(IEnumerable<Material> materials)
        {
            if (materials == null) return 0;
            int swapped = 0;
            foreach (var m in materials)
                if (Apply(m)) swapped++;
            return swapped;
        }
    }
}
