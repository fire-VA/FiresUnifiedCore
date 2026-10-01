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

        // 0.2.273: every instance the game's own objects draw with, by shader name. Valheim 1.0's asset bundles each carry their own
        // copy of a shader (piece_workbench's Custom/Piece is not Wayshrine's), so one "live" instance per name is not the game's
        // whole answer. Null when unknown; only Core sets it.
        internal static Func<string, IReadOnlyList<Shader>> GameShadersLookup;

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
            var notTheGames = new Dictionary<Shader, string>();
            var gameCopies = new HashSet<Shader>();
            var unityCopies = new HashSet<Shader>();
            var unityOwnNames = new SortedSet<string>();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int checkedCount = 0;
            bool liveIndexed = false;
            int reboundCount = 0;
            int movedCount = 0;
            int leftAloneCount = 0;
            int onGameCopyCount = 0;
            int unityOwnCount = 0;
            var rebound = new SortedSet<string>();
            var moved = new SortedSet<string>();
            var leftAlone = new SortedDictionary<string, UnresolvedShader>();
            var leftAloneWhy = new Dictionary<string, string>();
            var unresolved = new SortedDictionary<string, UnresolvedShader>();

            foreach (var material in materials)
            {
                if (material == null) continue;
                checkedCount++;
                var current = material.shader;
                if (current != null && current.isSupported)
                {
                    var live = LiveReplacementFor(current, liveFor, notTheGames, gameCopies, unityCopies, ref liveIndexed);
                    if (live == null)
                    {
                        // 0.2.273: a material on any of the game's own copies is the game's and stays as it is.
                        if (gameCopies.Contains(current)) { onGameCopyCount++; continue; }
                        // 0.2.275: a bundle's own copy of a Unity shader stays where it is (it carries its materials' variants).
                        if (unityCopies.Contains(current)) { unityOwnCount++; unityOwnNames.Add(current.name); continue; }
                        // 0.2.269: another mod's own shader under a game shader's name keeps its shader (a texture report: "your core mod
                        // breaks all the textures in my mod").
                        if (notTheGames.TryGetValue(current, out string why))
                        {
                            leftAloneCount++;
                            if (!leftAloneWhy.ContainsKey(current.name)) leftAloneWhy[current.name] = why;
                            if (!leftAlone.TryGetValue(current.name, out UnresolvedShader kept)) leftAlone[current.name] = kept = new UnresolvedShader();
                            kept.MaterialCount++;
                            int limit = SampleLimit;
                            if (limit <= 0 || kept.SampleMaterials.Count < limit)
                            {
                                owners ??= BuildOwnerIndex();
                                kept.SampleMaterials.Add(DescribeMaterial(material, owners));
                            }
                        }
                        continue;
                    }
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
                Debug.Log($"[ShaderRebind] {logPrefix}: checked {checkedCount} material(s) against the game's own shaders in "
                    + $"{timer.ElapsedMilliseconds} ms; rebound {movedCount}: moved off bundle copies of game shaders (a ripped copy "
                    + $"draws through its Fallback) onto the game's own"
                    + (movedCount > 0 ? $" ({string.Join(", ", moved)})" : "")
                    + $"; {onGameCopyCount} already on one of the game's own copies (left as they are)"
                    + $"; {unityOwnCount} on their bundle's own copy of a Unity shader (left there: it carries their variants" + (unityOwnCount > 0 ? $"; {string.Join(", ", unityOwnNames)}" : "") + ")"
                    + $"; left {leftAloneCount} other material(s) alone"
                    + (leftAloneCount > 0
                        ? $" (same name as a game shader, not the same shader as any of the game's copies: "
                          + string.Join("; ", leftAlone.Select(kept => DescribeUnresolved(kept).Replace(" material(s): ",
                              $" material(s) [{(leftAloneWhy.TryGetValue(kept.Key, out string why) ? why : "?")}]: "))) + ")"
                        : "")
                    + ".");
            if (unresolved.Count > 0)
                Debug.LogWarning($"{logPrefix}: no working game shader loaded for: {string.Join("; ", unresolved.Select(DescribeUnresolved))}");
            s_signatures.Clear();   // read again next sweep; holds no shader past this one
        }

        // "indexed" turns true once the lookup knows any game shader, so the timing line is written only for sweeps that
        // actually compared against the game's instances (the in-world one, not the main menu's). 0.2.273: one of the game's own
        // copies goes into gameCopies and is never moved; another copy moves to the first game copy it is the same shader as
        // (IsSameShader); one that matches none goes into notTheGames, with why, and is never moved.
        private static Shader LiveReplacementFor(Shader current, Dictionary<Shader, Shader> known, Dictionary<Shader, string> notTheGames,
            HashSet<Shader> gameCopies, HashSet<Shader> unityCopies, ref bool indexed)
        {
            if (LiveShaderLookup == null) return null;
            if (known.TryGetValue(current, out Shader cached)) return cached;
            if (notTheGames.ContainsKey(current) || gameCopies.Contains(current) || unityCopies.Contains(current)) return null;
            Shader live = LiveShaderLookup(current.name);
            if (live == null) { known[current] = null; return null; }
            indexed = true;
            IReadOnlyList<Shader> copies = GameShadersLookup?.Invoke(current.name);
            if (copies == null || copies.Count == 0) copies = new[] { live };
            foreach (var copy in copies)
                if (copy == current) { gameCopies.Add(current); return null; }
            // 0.2.275: a bundle's copy of one of Unity's own shaders is real, with its materials' variants (the game's copy has only
            // Valheim's): never moved while supported. WW1: WildWeapons' OrcSwordMat had been moved off its Standard onto the game's.
            if (IsUnityShader(current.name)) { unityCopies.Add(current); return null; }
            string firstWhy = null;
            foreach (var copy in copies)
            {
                string why = WhyNotSameShader(current, copy);
                if (why == null) { known[current] = copy; return copy; }
                firstWhy ??= why;
            }
            notTheGames[current] = firstWhy;
            return null;
        }

        // 0.2.269: a supported copy is the game's shader only when everything it declares is the game's (a SUBSET test): each property
        // exists on the game's shader with the same type, and each of its own local keywords is one the game's shader knows. A ripped
        // copy or a mod's stub of a vanilla shader (the usual swap-to-vanilla pattern) declares the game's properties or fewer and is
        // moved as before; a mod's own build under a game shader's name, with texture slots, properties or keywords of its own, is a
        // different shader and keeps its materials. Unity can't say which bundle a material came from, so this is decided by the shader
        // itself. Any failure answers "not the same" (the material is left alone).
        internal static bool IsSameShader(Shader copy, Shader live) => WhyNotSameShader(copy, live) == null;

        // 0.2.275 (WW1: WildWeapons' OrcSwordMat moved off its bundle's Standard onto the game's and drew as a blocky mess): Unity's own
        // shaders. Every bundle carries a real, compiled copy of these with the variants its own materials use, while the game's copy has
        // only Valheim's variants, so a supported copy of one is never moved (an unsupported one still is: it would draw magenta). Only
        // Valheim's own shaders (Custom/*, Standard TwoSided, Particles/Standard Unlit2, Unlit/WeaponGlow, Lux ...) have stub copies.
        private static readonly HashSet<string> UnityShaderNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Standard", "Standard (Specular setup)", "Standard (Roughness setup)", "Autodesk Interactive",
            "Particles/Standard Unlit", "Particles/Standard Surface",
            "Unlit/Color", "Unlit/Texture", "Unlit/Transparent", "Unlit/Transparent Cutout",
        };
        private static readonly string[] UnityShaderPrefixes =
            { "Legacy Shaders/", "Mobile/", "Nature/", "Sprites/", "UI/", "Skybox/", "GUI/", "Hidden/", "TextMeshPro/" };

        internal static bool IsUnityShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            if (UnityShaderNames.Contains(shaderName)) return true;
            foreach (string prefix in UnityShaderPrefixes)
                if (shaderName.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        // 0.2.275: what a shader declares, read once per instance per sweep (0.2.273 re-read both sides for every pair: 1.8 s at load).
        // Property kinds, not types: a rip declares Color as Vector and Range as Float (WW1: "_Color is Vector, the game's is Color"),
        // the same float4 / number to a material.
        private sealed class ShaderSignature
        {
            internal readonly Dictionary<string, int> Properties = new Dictionary<string, int>(StringComparer.Ordinal);
            internal readonly HashSet<string> AllKeywords = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<string> OwnKeywords = new List<string>();
            internal string Failed;
        }

        private static readonly Dictionary<Shader, ShaderSignature> s_signatures = new Dictionary<Shader, ShaderSignature>();
        private const int KindVector = 1, KindNumber = 2, KindTexture = 3;

        private static int PropertyKind(ShaderPropertyType type)
        {
            switch (type)
            {
                case ShaderPropertyType.Color:
                case ShaderPropertyType.Vector: return KindVector;
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                case ShaderPropertyType.Int: return KindNumber;
                case ShaderPropertyType.Texture: return KindTexture;
                default: return 100 + (int)type;
            }
        }

        private static string KindName(int kind) =>
            kind == KindVector ? "Color/Vector" : kind == KindNumber ? "Float/Range/Int" : kind == KindTexture ? "Texture" : ((ShaderPropertyType)(kind - 100)).ToString();

        private static ShaderSignature SignatureOf(Shader shader)
        {
            if (s_signatures.TryGetValue(shader, out ShaderSignature known)) return known;
            var signature = new ShaderSignature();
            try
            {
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                    signature.Properties[shader.GetPropertyName(i)] = PropertyKind(shader.GetPropertyType(i));
                foreach (LocalKeyword keyword in shader.keywordSpace.keywords)
                {
                    signature.AllKeywords.Add(keyword.name);
                    // Only the author's own local keywords count: a stub compiled as a surface shader declares Unity's global-scope
                    // pipeline keywords (DIRECTIONAL, SHADOWS_SCREEN, LIGHTPROBE_SH...), which say nothing about which shader it is.
                    if (!keyword.isOverridable) signature.OwnKeywords.Add(keyword.name);
                }
            }
            catch (Exception ex) { signature.Failed = ex.GetType().Name; }
            s_signatures[shader] = signature;
            return signature;
        }

        // 0.2.270 (shaderrebind_test): null when the copy is the game's shader, else the first thing it declares that the game's doesn't.
        internal static string WhyNotSameShader(Shader copy, Shader live)
        {
            if (copy == null || live == null) return "no shader";
            ShaderSignature mine = SignatureOf(copy), game = SignatureOf(live);
            if (mine.Failed != null || game.Failed != null) return $"couldn't compare ({mine.Failed ?? game.Failed})";
            foreach (var property in mine.Properties)
            {
                if (!game.Properties.TryGetValue(property.Key, out int kind)) return $"property {property.Key} ({KindName(property.Value)}) isn't on the game's";
                if (kind != property.Value) return $"property {property.Key} is {KindName(property.Value)}, the game's is {KindName(kind)}";
            }
            foreach (string keyword in mine.OwnKeywords)
                if (!game.AllKeywords.Contains(keyword)) return $"local keyword {keyword} isn't the game's";
            return null;
        }

        // 0.2.270 (WildWeapons repro): the shader each moved material was on before its first move ("name #instance"), by material
        // instance id, so shaderrebind_test can say what the sweep did to a mod's item.
        private static readonly Dictionary<int, string> s_movedFrom = new Dictionary<int, string>();

        internal static bool TryGetMovedFrom(Material material, out string was)
        {
            was = null;
            return material != null && s_movedFrom.TryGetValue(material.GetInstanceID(), out was);
        }

        // Keywords are re-applied after the swap: one the copy's shader did not declare was filed as invalid, and a new
        // shader alone does not revive it (a cutout's _ALPHATEST_ON, for one). The material's own render queue stays too
        // (-1, "the shader's", stays -1).
        private static void MoveToLive(Material material, Shader live)
        {
            string[] keywords = material.shaderKeywords;
            int queue = material.rawRenderQueue;
            int id = material.GetInstanceID();
            if (!s_movedFrom.ContainsKey(id))
                s_movedFrom[id] = material.shader != null ? $"{material.shader.name} #{material.shader.GetInstanceID()}" : "no shader";
            material.shader = live;
            material.shaderKeywords = keywords;
            material.renderQueue = queue;
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
