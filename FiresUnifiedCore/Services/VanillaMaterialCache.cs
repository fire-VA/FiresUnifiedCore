using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FiresCore.Services
{
    // Shared, build-ONCE cache of vanilla materials keyed by name, scanned from
    // ZNetScene.m_prefabs.
    //
    // Several content mods (VAassets, VApieces, DragonstoneUnified) each shipped
    // their own copy of MaterialSwapper and ran an IDENTICAL ~3600-prefab
    // GetComponentsInChildren<Renderer> scan independently at world load — N×
    // redundant work, all clustered into the spawn frame (a big chunk of the
    // multi-second login stall). Routing every mod's scan through this one cache
    // makes the scan run a single time; the rest reuse the result for free.
    //
    // Scan/filter logic is identical to the per-mod copies so the resulting
    // name→material map is byte-for-byte what each mod produced on its own.
    public static class VanillaMaterialCache
    {
        public static readonly Dictionary<string, Material> ByName =
            new Dictionary<string, Material>();

        private static bool _built;

        public static bool IsBuilt => _built && ByName.Count > 0;

        // Scans every ZNetScene prefab once and caches its vanilla (non-_copy)
        // materials by name. No-op after the first successful build. Returns
        // silently if ZNetScene isn't ready yet (the caller retries later).
        public static void EnsureBuilt()
        {
            if (_built && ByName.Count > 0) return;

            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null || scene.m_prefabs.Count == 0)
                return; // not ready yet

            var stopwatch = Stopwatch.StartNew();
            var prefabs = scene.m_prefabs;
            for (int i = 0; i < prefabs.Count; i++)
            {
                var prefab = prefabs[i];
                if (prefab == null) continue;
                CacheFromPrefab(prefab);
            }
            _built = true;
            stopwatch.Stop();
            Debug.Log($"[FiresCore] VanillaMaterialCache: scanned {prefabs.Count} prefabs → {ByName.Count} vanilla materials in {stopwatch.ElapsedMilliseconds} ms (built ONCE, shared across all mods).");
        }

        private static void CacheFromPrefab(GameObject prefab)
        {
            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                if (renderer == null) continue;

                var mats = renderer.sharedMaterials;
                for (int materialIndex = 0; materialIndex < mats.Length; materialIndex++)
                {
                    var mat = mats[materialIndex];
                    if (mat == null) continue;

                    string matName = mat.name.Replace(" (Instance)", "").Trim();
                    if (matName.ToLower().Contains("_copy")) continue;
                    if (ByName.ContainsKey(matName)) continue;
                    if (mat.shader == null || mat.shader.name.Contains("InternalErrorShader")) continue;

                    ByName[matName] = mat;
                }
            }
        }

        public static void Clear()
        {
            ByName.Clear();
            _built = false;
        }
    }
}
