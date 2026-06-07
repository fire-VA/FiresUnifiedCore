using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Services
{
    // Cross-mod material-swap registry. Like ShaderSwapper but at the
    // whole-material level — useful for HD-texture mods that ship a
    // complete replacement material (different shader, different maps,
    // different keywords) rather than just a shader swap.
    //
    // Two lookup modes:
    //   - by reference: register a specific vanilla Material → replacement
    //   - by name:     register a Material.name string → replacement
    //
    // Name-based is the common case for bundled prefabs whose materials
    // are loaded as fresh instances each session; reference-based works
    // when the canonical vanilla Material asset is in hand.
    public static class MaterialSwapper
    {
        private static readonly Dictionary<Material, Material> ByReference
            = new Dictionary<Material, Material>();

        private static readonly Dictionary<string, Material> ByName
            = new Dictionary<string, Material>(StringComparer.Ordinal);

        public static bool HasAny => ByReference.Count > 0 || ByName.Count > 0;

        public static int Count => ByReference.Count + ByName.Count;

        public static void Register(Material from, Material to)
        {
            if (from == null || to == null) return;
            ByReference[from] = to;
        }

        public static void RegisterByName(string fromMaterialName, Material to)
        {
            if (string.IsNullOrEmpty(fromMaterialName) || to == null) return;
            ByName[fromMaterialName] = to;
        }

        public static bool Unregister(Material from)
        {
            if (from == null) return false;
            return ByReference.Remove(from);
        }

        public static bool UnregisterByName(string fromMaterialName)
        {
            if (string.IsNullOrEmpty(fromMaterialName)) return false;
            return ByName.Remove(fromMaterialName);
        }

        public static void Clear()
        {
            ByReference.Clear();
            ByName.Clear();
        }

        // Resolve a replacement for the given material if any rule matches.
        // Reference rule wins over name rule when both match.
        public static bool TryResolve(Material source, out Material replacement)
        {
            if (source == null)
            {
                replacement = null;
                return false;
            }
            if (ByReference.TryGetValue(source, out replacement) && replacement != null)
                return true;
            if (!string.IsNullOrEmpty(source.name)
                && ByName.TryGetValue(source.name, out replacement)
                && replacement != null)
                return true;
            replacement = null;
            return false;
        }

        // Returns the number of slots replaced on the renderer.
        public static int ApplyToRenderer(Renderer renderer)
        {
            if (renderer == null) return 0;
            var mats = renderer.sharedMaterials;
            int swapped = 0;
            for (int i = 0; i < mats.Length; i++)
            {
                if (TryResolve(mats[i], out var replacement))
                {
                    mats[i] = replacement;
                    swapped++;
                }
            }
            if (swapped > 0) renderer.sharedMaterials = mats;
            return swapped;
        }
    }
}
