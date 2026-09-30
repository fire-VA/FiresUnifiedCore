using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Materials
{
    // Bundles built from the Valheim 1.0 rip carry vanilla shaders with no compiled programs, which draw magenta; their
    // materials are rebound to the game's working shader of the same name. Compiled into FiresUnifiedCore and
    // source-linked into the standalone mods (VABackpacks, TechPriestDhakharsPrefabs): one implementation, no coupling.
    internal static class VanillaShaderRebind
    {
        private const int DefaultUnresolvedSampleLimit = 5;
        private const string UnnamedShaderLabel = "<unnamed shader>";
        private const string UnnamedMaterialLabel = "<unnamed material>";

        private const string SampleLimitSection = "Logging";
        private const string SampleLimitKey = "UnresolvedShaderSampleLimit";
        private const string SampleLimitDescription =
            "How many material names to list per unresolved shader in the \"no working game shader loaded for\" warning. "
            + "0 lists every material, which is what a capture run wants. The standalone mods that compile this file "
            + "keep the default, since only Core binds it.";

        private static ConfigEntry<int> _sampleLimit;

        internal static void BindConfig(ConfigFile config)
        {
            if (config == null) return;
            _sampleLimit = config.Bind(SampleLimitSection, SampleLimitKey, DefaultUnresolvedSampleLimit, SampleLimitDescription);
        }

        private static int SampleLimit => _sampleLimit?.Value ?? DefaultUnresolvedSampleLimit;

        private static readonly Dictionary<Material, string> vanillaShaderNames = new Dictionary<Material, string>();
        private static readonly HashSet<string> reportedUnresolved = new HashSet<string>();

        // The instance the game's own objects draw with, by shader name; null when unknown. Only Core sets it. With it,
        // a SUPPORTED shader that is not that instance is also rebound: a ripped copy that names a Fallback reports
        // isSupported and draws as the fallback instead of magenta.
        internal static Func<string, Shader> LiveShaderLookup;

        // A material the rip stripped carries a shader with an empty name, and one Unity already gave up on carries
        // Hidden/InternalErrorShader. Neither name identifies anything, so the warning names the materials instead.
        private sealed class UnresolvedShader
        {
            internal int MaterialCount;
            internal readonly List<string> SampleMaterials = new List<string>();
        }

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
            Dictionary<Material, string> owners = null;
            var liveFor = new Dictionary<Shader, Shader>();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int checkedCount = 0;
            bool liveIndexed = false;
            int reboundCount = 0;
            int movedCount = 0;
            var rebound = new SortedSet<string>();
            var moved = new SortedSet<string>();
            var unresolved = new SortedDictionary<string, UnresolvedShader>();

            foreach (var material in materials)
            {
                if (material == null) continue;
                checkedCount++;
                var current = material.shader;
                if (current != null && current.isSupported)
                {
                    var live = LiveReplacementFor(current, liveFor, ref liveIndexed);
                    if (live == null) continue;
                    MoveToLive(material, live);
                    movedCount++;
                    moved.Add(live.name);
                    continue;
                }
                if (!TryGetVanillaShaderName(material, out string shaderName)) continue;

                Shader gameShader = LiveShaderLookup?.Invoke(shaderName);
                if (gameShader == null)
                {
                    gameShaders ??= LoadedSupportedShaders();
                    gameShaders.TryGetValue(shaderName, out gameShader);
                }
                if (gameShader != null)
                {
                    MoveToLive(material, gameShader);
                    reboundCount++;
                    rebound.Add(shaderName);
                }
                else
                {
                    if (reportedUnresolved.Add(shaderName)) unresolved[shaderName] = new UnresolvedShader();
                    if (unresolved.TryGetValue(shaderName, out UnresolvedShader failure))
                    {
                        failure.MaterialCount++;
                        int limit = SampleLimit;
                        if (limit <= 0 || failure.SampleMaterials.Count < limit)
                        {
                            owners ??= BuildOwnerIndex();
                            failure.SampleMaterials.Add(DescribeMaterial(material, owners));
                        }
                    }
                }
            }

            if (reboundCount > 0)
                Debug.Log($"{logPrefix}: using the game's own shaders for {reboundCount} material(s): {string.Join(", ", rebound)}");
            if (liveIndexed)
                Debug.Log($"{logPrefix}: checked {checkedCount} material(s) against the game's own shaders in "
                    + $"{timer.ElapsedMilliseconds} ms; moved {movedCount} off bundle copies of game shaders (a ripped copy "
                    + $"draws through its Fallback) onto the game's own"
                    + (movedCount > 0 ? $": {string.Join(", ", moved)}" : "."));
            if (unresolved.Count > 0)
                Debug.LogWarning($"{logPrefix}: no working game shader loaded for: {string.Join("; ", unresolved.Select(DescribeUnresolved))}");
        }

        // "indexed" turns true once the lookup knows any game shader, so the timing line is written only for sweeps that
        // actually compared against the game's instances (the in-world one, not the main menu's).
        private static Shader LiveReplacementFor(Shader current, Dictionary<Shader, Shader> known, ref bool indexed)
        {
            if (LiveShaderLookup == null) return null;
            if (known.TryGetValue(current, out Shader cached)) return cached;
            Shader live = LiveShaderLookup(current.name);
            if (live != null) indexed = true;
            if (live == current) live = null;
            known[current] = live;
            return live;
        }

        // Keywords are re-applied after the swap: one the copy's shader did not declare was filed as invalid, and a new
        // shader alone does not revive it (a cutout's _ALPHATEST_ON, for one).
        private static void MoveToLive(Material material, Shader live)
        {
            string[] keywords = material.shaderKeywords;
            material.shader = live;
            material.shaderKeywords = keywords;
        }

        private static string DescribeUnresolved(KeyValuePair<string, UnresolvedShader> failure)
        {
            string shaderName = string.IsNullOrWhiteSpace(failure.Key) ? UnnamedShaderLabel : failure.Key;
            string samples = string.Join(", ", failure.Value.SampleMaterials);
            if (failure.Value.MaterialCount > failure.Value.SampleMaterials.Count) samples += ", ...";
            return $"{shaderName} on {failure.Value.MaterialCount} material(s): {samples}";
        }

        private static string DescribeMaterial(Material material, Dictionary<Material, string> owners)
        {
            string name = string.IsNullOrEmpty(material.name) ? UnnamedMaterialLabel : material.name;
            return owners != null && owners.TryGetValue(material, out string owner) && owner != name
                ? $"{name} (on {owner})"
                : name;
        }

        // A material reached through Resources.FindObjectsOfTypeAll carries no owner, so the prefab behind a broken
        // material can only be recovered by walking the loaded renderers back to their roots. Built at most once per
        // sweep and only when something failed to resolve, so a healthy load never pays for it.
        private static Dictionary<Material, string> BuildOwnerIndex()
        {
            var owners = new Dictionary<Material, string>();
            var slots = new List<Material>();
            foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (renderer == null) continue;
                renderer.GetSharedMaterials(slots);
                foreach (var material in slots)
                {
                    if (material == null || owners.ContainsKey(material)) continue;
                    owners[material] = renderer.transform.root.name;
                }
            }
            return owners;
        }

        internal static bool IsUnsupported(Shader shader) => !IsHeadless && shader != null && !shader.isSupported;

        internal static bool TryFindGameShader(string shaderName, out Shader shader)
        {
            shader = null;
            if (IsHeadless) return false;
            shader = LiveShaderLookup?.Invoke(shaderName);
            return shader != null || LoadedSupportedShaders().TryGetValue(shaderName, out shader);
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
