using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FiresCore.Services
{
    // Cross-mod AssetBundle loading service. Exposes the fast-path Unity
    // API (LoadFromFile + LoadFromFileAsync) wrapped in a dedup cache,
    // folder-scan helpers, and header sniffing so consumer mods don't
    // re-implement the same boilerplate.
    //
    // Why this is in FUC:
    // - Most third-party mods (HD textures, content packs) call
    //   `File.ReadAllBytes(path)` and then `AssetBundle.LoadFromMemory`.
    //   That path double-copies the bundle: managed byte[] → unmanaged
    //   bundle data. LoadFromFile mmaps the file into native memory in
    //   one pass — typically 5-10x faster on large bundles, with half
    //   the peak memory usage.
    // - Each mod also tracks its own "already loaded" set to avoid the
    //   "bundle already loaded" error Unity throws on double-load. With
    //   a shared cache here, two mods loading the same bundle path just
    //   share the result.
    //
    // Stays no-op until called — no scanning, no caching, no allocation
    // on import unless a consumer invokes a method.
    public static class BundleLoader
    {
        // Standard extensions Valheim's mod ecosystem ships bundles as.
        // Empty string covers SteamyDumps-style extensionless dumps where
        // we sniff the UnityFS header before attempting LoadFromFile.
        private static readonly string[] DefaultBundleExtensions =
        {
            ".bundle",
            ".assetbundle",
            ".unity3d",
            "",
        };

        // Conservative skip-list — README and other text files that
        // commonly sit next to bundles in mod folders. Reduces noise
        // when a folder-scan helper enumerates a config directory.
        private static readonly string[] DefaultExcludedExtensions =
        {
            ".txt", ".md", ".yaml", ".yml", ".json",
            ".cfg", ".ini", ".log", ".old", ".bak",
            ".manifest", ".meta",
        };

        private const int UnityFsHeaderProbeBytes = 8;
        private static readonly byte[] UnityFsHeaderMagic = { (byte)'U', (byte)'n', (byte)'i', (byte)'t', (byte)'y' };

        // Cache keyed by Path.GetFullPath result so symlinks/relative
        // paths collapse to one entry. Bundles stay loaded for the
        // session — Unloading would strip referenced assets out of every
        // GameObject the engine cloned from this bundle.
        private static readonly Dictionary<string, AssetBundle> Cache
            = new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);

        public static int LoadedCount => Cache.Count;

        public static IEnumerable<string> LoadedPaths => Cache.Keys;

        // Fast synchronous load. Returns null on any failure (logs a
        // warning). Repeated calls for the same path return the cached
        // bundle without re-hitting disk.
        public static AssetBundle LoadFromFileFast(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string key;
            try { key = Path.GetFullPath(path); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresCore.BundleLoader] Path.GetFullPath threw on '{path}': {ex.Message}");
                return null;
            }

            if (Cache.TryGetValue(key, out var cached) && cached != null)
                return cached;

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[FiresCore.BundleLoader] Bundle path not found: '{path}'.");
                return null;
            }

            AssetBundle bundle;
            try { bundle = AssetBundle.LoadFromFile(path); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresCore.BundleLoader] LoadFromFile threw on '{Path.GetFileName(path)}': {ex.Message}");
                return null;
            }

            if (bundle == null)
            {
                Debug.LogWarning($"[FiresCore.BundleLoader] LoadFromFile returned null for '{Path.GetFileName(path)}' (corrupt, wrong Unity version, or already loaded by another mod).");
                return null;
            }

            Cache[key] = bundle;
            return bundle;
        }

        // Async load via Unity's AssetBundleCreateRequest. Caller must
        // start the returned IEnumerator on a MonoBehaviour (typically
        // the calling plugin's StartCoroutine). onComplete fires with
        // the bundle (or null on failure) when the load resolves.
        public static IEnumerator LoadFromFileAsync(string path, Action<AssetBundle> onComplete)
        {
            if (string.IsNullOrEmpty(path))
            {
                onComplete?.Invoke(null);
                yield break;
            }

            string key;
            try { key = Path.GetFullPath(path); }
            catch
            {
                onComplete?.Invoke(null);
                yield break;
            }

            if (Cache.TryGetValue(key, out var cached) && cached != null)
            {
                onComplete?.Invoke(cached);
                yield break;
            }

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[FiresCore.BundleLoader] Bundle path not found: '{path}'.");
                onComplete?.Invoke(null);
                yield break;
            }

            var request = AssetBundle.LoadFromFileAsync(path);
            yield return request;

            var bundle = request.assetBundle;
            if (bundle == null)
            {
                Debug.LogWarning($"[FiresCore.BundleLoader] LoadFromFileAsync resolved null for '{Path.GetFileName(path)}'.");
                onComplete?.Invoke(null);
                yield break;
            }

            Cache[key] = bundle;
            onComplete?.Invoke(bundle);
        }

        // Scans a folder for bundles and loads each via LoadFromFileFast.
        // Extensions default to .bundle/.assetbundle/.unity3d/no-extension;
        // exclusions default to common text/config/metadata extensions.
        // Returns the list of successfully-loaded bundles in folder order.
        public static List<AssetBundle> LoadFolder(
            string folderPath,
            IReadOnlyCollection<string> validExtensions = null,
            IReadOnlyCollection<string> excludedExtensions = null)
        {
            var result = new List<AssetBundle>();
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return result;

            var valid = ToOrdinalSet(validExtensions ?? DefaultBundleExtensions);
            var excluded = ToOrdinalSet(excludedExtensions ?? DefaultExcludedExtensions);

            string[] files;
            try { files = Directory.GetFiles(folderPath); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresCore.BundleLoader] Directory scan failed on '{folderPath}': {ex.Message}");
                return result;
            }

            foreach (var path in files)
            {
                var ext = Path.GetExtension(path);
                if (excluded.Contains(ext)) continue;
                if (!valid.Contains(ext)) continue;

                if (string.IsNullOrEmpty(ext) && !IsUnityBundle(path)) continue;

                var bundle = LoadFromFileFast(path);
                if (bundle != null) result.Add(bundle);
            }

            return result;
        }

        // Async folder load. onEach fires once per successfully-loaded
        // bundle as it resolves; onComplete fires once after the last
        // file in the folder has been processed. Caller drives via
        // StartCoroutine on its own MonoBehaviour.
        public static IEnumerator LoadFolderAsync(
            string folderPath,
            Action<AssetBundle> onEach,
            Action onComplete = null,
            IReadOnlyCollection<string> validExtensions = null,
            IReadOnlyCollection<string> excludedExtensions = null)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                onComplete?.Invoke();
                yield break;
            }

            var valid = ToOrdinalSet(validExtensions ?? DefaultBundleExtensions);
            var excluded = ToOrdinalSet(excludedExtensions ?? DefaultExcludedExtensions);

            string[] files;
            try { files = Directory.GetFiles(folderPath); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresCore.BundleLoader] Directory scan failed on '{folderPath}': {ex.Message}");
                onComplete?.Invoke();
                yield break;
            }

            foreach (var path in files)
            {
                var ext = Path.GetExtension(path);
                if (excluded.Contains(ext)) continue;
                if (!valid.Contains(ext)) continue;

                if (string.IsNullOrEmpty(ext) && !IsUnityBundle(path)) continue;

                yield return LoadFromFileAsync(path, onEach);
            }

            onComplete?.Invoke();
        }

        // Extracts every GameObject asset in the bundle. Returns an
        // empty array on null bundle or load failure (logs a warning).
        public static GameObject[] LoadAllPrefabs(AssetBundle bundle)
        {
            if (bundle == null) return Array.Empty<GameObject>();
            try { return bundle.LoadAllAssets<GameObject>(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresCore.BundleLoader] LoadAllAssets<GameObject> threw on '{bundle.name}': {ex.Message}");
                return Array.Empty<GameObject>();
            }
        }

        // Typed asset extraction. Same contract as LoadAllPrefabs but
        // generic over the asset type — useful for pulling Materials,
        // Textures, Meshes, ScriptableObjects out of bundles.
        public static T[] LoadAllAssets<T>(AssetBundle bundle) where T : UnityEngine.Object
        {
            if (bundle == null) return Array.Empty<T>();
            try { return bundle.LoadAllAssets<T>(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresCore.BundleLoader] LoadAllAssets<{typeof(T).Name}> threw on '{bundle.name}': {ex.Message}");
                return Array.Empty<T>();
            }
        }

        public static T LoadAsset<T>(AssetBundle bundle, string name) where T : UnityEngine.Object
        {
            if (bundle == null || string.IsNullOrEmpty(name)) return null;
            try { return bundle.LoadAsset<T>(name); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresCore.BundleLoader] LoadAsset<{typeof(T).Name}>('{name}') threw on '{bundle.name}': {ex.Message}");
                return null;
            }
        }

        // Sniffs the first bytes for the UnityFS / UnityWeb / UnityRaw
        // header. Lets a folder scan skip random extension-less files
        // (READMEs, scripts) without spuriously logging a load failure
        // for each. Cheap — single 8-byte read + close.
        public static bool IsUnityBundle(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var header = new byte[UnityFsHeaderProbeBytes];
                    int read = fs.Read(header, 0, UnityFsHeaderProbeBytes);
                    if (read < UnityFsHeaderMagic.Length) return false;

                    for (int i = 0; i < UnityFsHeaderMagic.Length; i++)
                        if (header[i] != UnityFsHeaderMagic[i])
                            return false;

                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        // Lookup. Returns the cached bundle for a path if it's already
        // been loaded by this service, null otherwise. Doesn't trigger
        // a load — use LoadFromFileFast for "load if missing" semantics.
        public static AssetBundle GetLoaded(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var key = Path.GetFullPath(path);
                return Cache.TryGetValue(key, out var b) ? b : null;
            }
            catch { return null; }
        }

        public static bool IsLoaded(string path) => GetLoaded(path) != null;

        // Diagnostic snapshot — one row per cached bundle with its full
        // path, in-bundle name, and asset count. For console commands
        // / diagnostic banners.
        public readonly struct LoadedBundleInfo
        {
            public readonly string FullPath;
            public readonly string BundleName;
            public readonly int AssetCount;

            public LoadedBundleInfo(string fullPath, string bundleName, int assetCount)
            {
                FullPath = fullPath;
                BundleName = bundleName;
                AssetCount = assetCount;
            }
        }

        public static List<LoadedBundleInfo> Snapshot()
        {
            var rows = new List<LoadedBundleInfo>(Cache.Count);
            foreach (var kv in Cache)
            {
                if (kv.Value == null) continue;
                int count = 0;
                try { count = kv.Value.GetAllAssetNames()?.Length ?? 0; }
                catch { /* bundle may be in an odd state; surface 0 */ }
                rows.Add(new LoadedBundleInfo(kv.Key, kv.Value.name, count));
            }
            return rows;
        }

        private static HashSet<string> ToOrdinalSet(IReadOnlyCollection<string> source)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in source) set.Add(s ?? string.Empty);
            return set;
        }
    }
}
