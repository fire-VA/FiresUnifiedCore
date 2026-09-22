using System;
using System.Collections.Generic;
using FiresCore.Materials;
using UnityEngine;

namespace FiresCore.Services
{
    // Repairs bundle prefabs whose custom shaders or "_copy" materials lost their backing on export, replacing
    // the per-mod ShaderReplacement and MaterialSwapper copies. Vanilla shaders and named materials are
    // harvested from ZNetScene on first use. A shader resolves to a working one by exact cached name, then Shader.Find,
    // then the game's loaded shader of that name, then the optional name map, then Standard, never to a rip bundle's
    // uncompiled copy; a "_copy" material resolves to its vanilla original. Common material
    // properties are carried across the swap, extendable with RegisterPreservedProperty.
    public static class VanillaAssetResolver
    {
        private const string CopyMaterialSuffix = "_copy";
        private const string InstanceMaterialSuffix = " (Instance)";
        private const string InternalErrorShaderToken = "InternalErrorShader";
        private const string FallbackShaderName = "Standard";

        private static readonly Dictionary<string, Shader> ShaderCache
            = new Dictionary<string, Shader>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Material> MaterialCache
            = new Dictionary<string, Material>(StringComparer.Ordinal);

        private static readonly Dictionary<string, string> ShaderNameMap
            = new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly HashSet<string> PreservedColorProps
            = new HashSet<string>(StringComparer.Ordinal) { "_Color", "_EmissionColor", "_SkinColor" };
        private static readonly HashSet<string> PreservedTextureProps
            = new HashSet<string>(StringComparer.Ordinal) { "_MainTex", "_BumpMap" };
        private static readonly HashSet<string> PreservedFloatProps
            = new HashSet<string>(StringComparer.Ordinal) { "_Cutoff", "_Glossiness", "_Metallic" };

        private static bool _warmed;
        private static Shader _fallbackShader;
        private static ZNetScene _warmedScene;
        private static int _warmedPrefabCount;
        private static readonly HashSet<int> RepairedPrefabIds = new HashSet<int>();

        // ─── Status ────────────────────────────────────────────────────

        public static int ShaderCount => ShaderCache.Count;
        public static int MaterialCount => MaterialCache.Count;
        public static bool IsWarmed => _warmed;
        public static IEnumerable<string> CachedShaderNames => ShaderCache.Keys;
        public static IEnumerable<string> CachedMaterialNames => MaterialCache.Keys;

        // ─── Cache warming ─────────────────────────────────────────────

        // Harvest shaders + non-_copy named materials from the prefabs registered in ZNetScene. A repeat call on the
        // same ZNetScene only reads prefabs appended since the last pass; a new scene, or a list that shrank, is read
        // in full. Returns total cache size after the pass (shaders + materials). Returns 0 if ZNetScene isn't
        // ready; caller can retry on a later tick.
        public static int Warm()
        {
            var scene = ZNetScene.instance;
            if (scene == null) return ShaderCount + MaterialCount;
            var prefabs = scene.m_prefabs;
            if (prefabs == null || prefabs.Count == 0) return ShaderCount + MaterialCount;

            bool sameScene = scene == _warmedScene && prefabs.Count >= _warmedPrefabCount;
            for (int i = sameScene ? _warmedPrefabCount : 0; i < prefabs.Count; i++)
                WarmFromPrefab(prefabs[i]);
            _warmedScene = scene;
            _warmedPrefabCount = prefabs.Count;

            _warmed = ShaderCache.Count > 0 || MaterialCache.Count > 0;
            return ShaderCount + MaterialCount;
        }

        // Targeted warm — pulls shaders + materials off the given prefab
        // into the cache. Useful when a mod loads before ZNetScene is
        // populated and needs the cache primed from a specific source.
        // Returns the number of new entries added across both caches.
        public static int WarmFromPrefab(GameObject prefab)
        {
            if (prefab == null) return 0;

            Renderer[] renderers;
            try { renderers = prefab.GetComponentsInChildren<Renderer>(includeInactive: true); }
            catch { return 0; }
            if (renderers == null) return 0;

            int added = 0;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var mats = renderer.sharedMaterials;
                if (mats == null) continue;

                foreach (var mat in mats)
                {
                    if (mat == null || !IsWorkingShader(mat.shader)) continue;

                    if (!ShaderCache.ContainsKey(mat.shader.name))
                    {
                        ShaderCache[mat.shader.name] = mat.shader;
                        if (_fallbackShader == null) _fallbackShader = mat.shader;
                        added++;
                    }

                    var cleanName = StripInstanceSuffix(mat.name);
                    if (!IsCopyMaterial(cleanName) && !MaterialCache.ContainsKey(cleanName))
                    {
                        MaterialCache[cleanName] = mat;
                        added++;
                    }
                }
            }

            if (added > 0) _warmed = true;
            return added;
        }

        public static void ClearCache()
        {
            ShaderCache.Clear();
            MaterialCache.Clear();
            _fallbackShader = null;
            _fallbackMaterial = null;
            _warmed = false;
            _warmedScene = null;
            _warmedPrefabCount = 0;
        }

        // Register a custom-shader-name → vanilla-shader-name redirect.
        // Used by the priority-3 step of the shader cascade. Example:
        // RegisterShaderAlias("Custom/MyOldExportedRock", "Custom/Piece")
        // — any material whose orphaned shader is "Custom/MyOldExportedRock"
        // will resolve to the cached vanilla Custom/Piece shader.
        public static void RegisterShaderAlias(string fromShaderName, string toShaderName)
        {
            if (string.IsNullOrEmpty(fromShaderName) || string.IsNullOrEmpty(toShaderName)) return;
            ShaderNameMap[fromShaderName] = toShaderName;
        }

        public static void RegisterPreservedProperty(string property, ShaderPropertyType type)
        {
            if (string.IsNullOrEmpty(property)) return;
            switch (type)
            {
                case ShaderPropertyType.Color:   PreservedColorProps.Add(property); break;
                case ShaderPropertyType.Texture: PreservedTextureProps.Add(property); break;
                case ShaderPropertyType.Float:   PreservedFloatProps.Add(property); break;
            }
        }

        // ─── Lookup ────────────────────────────────────────────────────

        public static bool TryResolveShader(string name, out Shader shader)
        {
            shader = null;
            if (string.IsNullOrEmpty(name)) return false;

            if (TryResolveShaderNamed(name, out shader)) return true;
            return ShaderNameMap.TryGetValue(name, out var aliasName) && TryResolveShaderNamed(aliasName, out shader);
        }

        private static bool TryResolveShaderNamed(string name, out Shader shader)
        {
            if (ShaderCache.TryGetValue(name, out shader) && IsWorkingShader(shader))
                return true;

            shader = Shader.Find(name);
            if (!IsWorkingShader(shader) && !VanillaShaderRebind.TryFindGameShader(name, out shader))
            {
                shader = null;
                return false;
            }

            ShaderCache[name] = shader;
            return true;
        }

        private static bool IsWorkingShader(Shader shader) =>
            shader != null
            && !shader.name.Contains(InternalErrorShaderToken)
            && !VanillaShaderRebind.IsUnsupported(shader);

        public static bool TryResolveMaterial(string materialName, out Material material)
        {
            material = null;
            if (string.IsNullOrEmpty(materialName)) return false;

            var cleanName = StripInstanceSuffix(materialName);
            var vanillaName = IsCopyMaterial(cleanName) ? StripCopySuffix(cleanName) : cleanName;

            if (MaterialCache.TryGetValue(vanillaName, out material) && material != null)
                return true;

            foreach (var kv in MaterialCache)
            {
                if (kv.Key.Equals(vanillaName, StringComparison.OrdinalIgnoreCase))
                {
                    material = kv.Value;
                    return true;
                }
            }

            material = null;
            return false;
        }

        // ─── Detection ─────────────────────────────────────────────────

        // Default: shader name starts with "Custom/" or contains the
        // InternalErrorShader sentinel. The first means a bundle author's
        // custom shader that lost its backing on export; the second means
        // Unity couldn't compile a shader at runtime.
        public static bool ShouldReplaceShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return true;
            if (shaderName.Contains(InternalErrorShaderToken)) return true;
            if (shaderName.StartsWith("Custom/", StringComparison.Ordinal)) return true;
            return false;
        }

        public static bool IsCopyMaterial(string materialName)
        {
            if (string.IsNullOrEmpty(materialName)) return false;
            return materialName.IndexOf(CopyMaterialSuffix, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string StripCopySuffix(string materialName)
        {
            if (string.IsNullOrEmpty(materialName)) return materialName;
            int idx = materialName.IndexOf(CopyMaterialSuffix, StringComparison.OrdinalIgnoreCase);
            return idx < 0 ? materialName : materialName.Substring(0, idx).Trim();
        }

        public static string StripInstanceSuffix(string materialName)
        {
            if (string.IsNullOrEmpty(materialName)) return materialName;
            return materialName.Replace(InstanceMaterialSuffix, string.Empty).Trim();
        }

        // ─── Apply ─────────────────────────────────────────────────────

        // Walks every Renderer under the prefab and swaps orphaned shaders
        // + `_copy` materials for their vanilla counterparts. Returns the
        // total number of swaps performed (shader + material combined).
        // Idempotent — a re-run on the same prefab is a no-op once swaps
        // are complete.
        // Repairs each bundle prefab once per prefab instance, warming the cache only when one still needs it. A
        // bundle re-loaded after a relogin yields new instances, which are repaired again. Failures are logged under
        // the caller's tag and never stop the rest.
        public static void RepairPrefabsOnce(IEnumerable<GameObject> prefabs, AssetSwapMode mode, string logTag)
        {
            if (prefabs == null) return;
            bool warmed = false;
            foreach (var prefab in prefabs)
            {
                if (prefab == null || RepairedPrefabIds.Contains(prefab.GetInstanceID())) continue;
                if (!warmed)
                {
                    try { Warm(); }
                    catch (Exception ex) { Debug.LogWarning($"{logTag} shader warm failed: {ex.Message}"); }
                    warmed = true;
                }
                RepairedPrefabIds.Add(prefab.GetInstanceID());
                try { ApplyToPrefab(prefab, mode); }
                catch (Exception ex) { Debug.LogWarning($"{logTag} shader fix {prefab.name}: {ex.Message}"); }
            }
        }

        public static int ApplyToPrefab(GameObject prefab, AssetSwapMode mode = AssetSwapMode.Both)
        {
            if (prefab == null) return 0;

            Renderer[] renderers;
            try { renderers = prefab.GetComponentsInChildren<Renderer>(includeInactive: true); }
            catch { return 0; }
            if (renderers == null) return 0;

            int swaps = 0;
            foreach (var renderer in renderers)
                swaps += ApplyToRenderer(renderer, mode);
            return swaps;
        }

        public static int ApplyToRenderer(Renderer renderer, AssetSwapMode mode = AssetSwapMode.Both)
        {
            if (renderer == null) return 0;
            var materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0) return 0;

            int swaps = 0;
            bool modified = false;

            for (int i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null) continue;

                if ((mode & AssetSwapMode.Material) != 0
                    && IsCopyMaterial(StripInstanceSuffix(material.name))
                    && TryResolveMaterial(material.name, out var vanillaMat))
                {
                    materials[i] = vanillaMat;
                    modified = true;
                    swaps++;
                    continue;
                }

                if ((mode & AssetSwapMode.Shader) != 0
                    && material.shader != null
                    && ShouldReplaceShader(material.shader.name)
                    && TryFindReplacementShader(material.shader.name, out var replacement)
                    && replacement != material.shader)
                {
                    SwapShaderPreservingProperties(material, replacement);
                    swaps++;
                }
            }

            if (modified) renderer.sharedMaterials = materials;
            return swaps;
        }

        /// <summary>Restores renderers whose material lost its shader and fell back to Unity's error shader -- the
        /// magenta seen in game. Equipment and its effects attach AFTER any prefab-level pass, so nothing repaired
        /// them. The vanilla material of the same name is restored whole, bringing its real shader and textures with
        /// it; a material whose shader still works is never touched, and anything unresolvable is named in the log
        /// rather than silently recoloured. Returns the number of slots restored.</summary>
        public static int RepairBrokenShaders(GameObject root, string context)
        {
            if (root == null) return 0;

            Renderer[] renderers;
            try { renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true); }
            catch { return 0; }
            if (renderers == null) return 0;

            if (!_warmed) Warm();

            int repaired = 0;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials;
                if (materials == null) continue;

                bool modified = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null) continue;
                    if (material.shader != null && !material.shader.name.Contains(InternalErrorShaderToken)) continue;

                    if (TryResolveMaterial(material.name, out var vanillaMaterial) && vanillaMaterial != null)
                    {
                        materials[i] = vanillaMaterial;
                        modified = true;
                        repaired++;
                        continue;
                    }

                    Debug.LogWarning($"[VanillaAssetResolver][magenta] {context}: no vanilla material named "
                        + $"'{material.name}' to restore -- '{DescribeHierarchyPath(renderer.transform)}' slot {i} "
                        + $"shader='{(material.shader != null ? material.shader.name : "<null>")}'");
                }

                if (modified) renderer.sharedMaterials = materials;
            }

            return repaired;
        }

        private static string DescribeHierarchyPath(Transform transform)
        {
            var path = new System.Text.StringBuilder(transform.name);
            for (var parent = transform.parent; parent != null; parent = parent.parent)
                path.Insert(0, parent.name + "/");
            return path.ToString();
        }

        // Walks every Renderer under the prefab and replaces null material
        // slots with a fallback (a fresh Material built off the first
        // cached vanilla shader, gray albedo). Without this, bundle prefabs
        // with broken material slots render with InternalErrorShader pink.
        // Returns the number of slots fixed.
        public static int FixNullMaterials(GameObject prefab)
        {
            if (prefab == null) return 0;

            Renderer[] renderers;
            try { renderers = prefab.GetComponentsInChildren<Renderer>(includeInactive: true); }
            catch { return 0; }
            if (renderers == null) return 0;

            int fixedSlots = 0;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                fixedSlots += FixNullMaterialsOnRenderer(renderer);
            }
            return fixedSlots;
        }

        public static int FixNullMaterialsOnRenderer(Renderer renderer)
        {
            if (renderer == null) return 0;
            var materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0) return 0;

            int fixedSlots = 0;
            bool hasNull = false;
            for (int i = 0; i < materials.Length; i++)
                if (materials[i] == null) { hasNull = true; break; }
            if (!hasNull) return 0;

            var fallback = GetOrCreateFallbackMaterial();
            if (fallback == null) return 0;

            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null)
                {
                    materials[i] = fallback;
                    fixedSlots++;
                }
            }
            renderer.sharedMaterials = materials;
            return fixedSlots;
        }

        private static Material _fallbackMaterial;

        private static Material GetOrCreateFallbackMaterial()
        {
            if (_fallbackMaterial != null) return _fallbackMaterial;

            Shader shader = _fallbackShader ?? Shader.Find(FallbackShaderName);
            if (shader == null) return null;

            _fallbackMaterial = new Material(shader)
            {
                name = "FiresCore_FallbackMaterial",
                color = new Color(0.5f, 0.5f, 0.5f, 1f),
            };
            return _fallbackMaterial;
        }

        // ─── Internals ─────────────────────────────────────────────────

        private static bool TryFindReplacementShader(string shaderName, out Shader replacement)
        {
            if (TryResolveShader(shaderName, out replacement)) return true;

            replacement = _fallbackShader ?? Shader.Find(FallbackShaderName);
            return replacement != null;
        }

        private static void SwapShaderPreservingProperties(Material material, Shader replacement)
        {
            var savedColors = new Dictionary<string, Color>();
            var savedTextures = new Dictionary<string, Texture>();
            var savedFloats = new Dictionary<string, float>();

            foreach (var propertyName in PreservedColorProps)
                if (material.HasProperty(propertyName)) savedColors[propertyName] = material.GetColor(propertyName);
            foreach (var propertyName in PreservedTextureProps)
                if (material.HasProperty(propertyName)) savedTextures[propertyName] = material.GetTexture(propertyName);
            foreach (var propertyName in PreservedFloatProps)
                if (material.HasProperty(propertyName)) savedFloats[propertyName] = material.GetFloat(propertyName);
            int savedRenderQueue = material.renderQueue;

            material.shader = replacement;

            foreach (var kv in savedColors)
                if (material.HasProperty(kv.Key)) material.SetColor(kv.Key, kv.Value);
            foreach (var kv in savedTextures)
                if (material.HasProperty(kv.Key)) material.SetTexture(kv.Key, kv.Value);
            foreach (var kv in savedFloats)
                if (material.HasProperty(kv.Key)) material.SetFloat(kv.Key, kv.Value);
            material.renderQueue = savedRenderQueue;
        }

        [Flags]
        public enum AssetSwapMode
        {
            Shader   = 1 << 0,
            Material = 1 << 1,
            Both     = Shader | Material,
        }

        public enum ShaderPropertyType
        {
            Color,
            Texture,
            Float,
        }
    }
}
