using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Materials
{
    // Bundles built from the Valheim 1.0 rip carry vanilla shaders with no compiled programs, which draw magenta; their
    // materials are rebound to the game's working shader of the same name. Compiled into FiresUnifiedCore and
    // source-linked into the standalone mods (VABackpacks, TechPriestDhakharsPrefabs): one implementation, no coupling.
    internal static class VanillaShaderRebind
    {
        private static readonly Dictionary<Material, string> vanillaShaderNames = new Dictionary<Material, string>();
        private static readonly HashSet<string> reportedUnresolved = new HashSet<string>();

        internal static void RebindPrefabs(IEnumerable<GameObject> prefabs, string logPrefix)
        {
            var materials = prefabs
                .Where(prefab => prefab != null)
                .SelectMany(prefab => prefab.GetComponentsInChildren<Renderer>(true))
                .SelectMany(renderer => renderer.sharedMaterials);
            Rebind(materials, logPrefix);
        }

        internal static void RebindAllLoadedMaterials(string logPrefix) =>
            Rebind(Resources.FindObjectsOfTypeAll<Material>(), logPrefix);

        private static void Rebind(IEnumerable<Material> materials, string logPrefix)
        {
            if (IsHeadless) return;

            Dictionary<string, Shader> gameShaders = null;
            int reboundCount = 0;
            var rebound = new SortedSet<string>();
            var unresolved = new SortedSet<string>();

            foreach (var material in materials)
            {
                if (material == null) continue;
                if (material.shader != null && material.shader.isSupported) continue;
                if (!TryGetVanillaShaderName(material, out string shaderName)) continue;

                gameShaders ??= LoadedSupportedShaders();
                if (gameShaders.TryGetValue(shaderName, out Shader gameShader))
                {
                    material.shader = gameShader;
                    reboundCount++;
                    rebound.Add(shaderName);
                }
                else if (reportedUnresolved.Add(shaderName))
                {
                    unresolved.Add(shaderName);
                }
            }

            if (reboundCount > 0)
                Debug.Log($"{logPrefix}: using the game's own shaders for {reboundCount} material(s): {string.Join(", ", rebound)}");
            if (unresolved.Count > 0)
                Debug.LogWarning($"{logPrefix}: no working game shader loaded for: {string.Join(", ", unresolved)}");
        }

        internal static bool IsUnsupported(Shader shader) => !IsHeadless && shader != null && !shader.isSupported;

        internal static bool TryFindGameShader(string shaderName, out Shader shader)
        {
            shader = null;
            return !IsHeadless && LoadedSupportedShaders().TryGetValue(shaderName, out shader);
        }

        private static bool IsHeadless => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        private static bool TryGetVanillaShaderName(Material material, out string shaderName)
        {
            if (vanillaShaderNames.TryGetValue(material, out shaderName)) return true;
            if (material.shader == null) return false;

            shaderName = material.shader.name;
            vanillaShaderNames[material] = shaderName;
            return true;
        }

        private static Dictionary<string, Shader> LoadedSupportedShaders()
        {
            var shaders = new Dictionary<string, Shader>();
            foreach (var shader in Resources.FindObjectsOfTypeAll<Shader>())
            {
                if (shader.isSupported && !shaders.ContainsKey(shader.name))
                    shaders[shader.name] = shader;
            }
            return shaders;
        }
    }
}
