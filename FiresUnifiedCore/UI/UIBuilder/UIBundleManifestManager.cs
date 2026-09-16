using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Manages per-bundle manifests for cached asset bundles.
    /// 
    /// When we "yoink" a mod's asset bundle, we save:
    ///   1. The raw bundle data file (no extension change - the actual UnityFS bytes)
    ///   2. A per-bundle manifest JSON listing all assets in the bundle
    ///   3. Bundle metadata (source mod, internal name, file size, etc.)
    /// 
    /// When the source mod is removed, this manager loads the cached bundle using the
    /// manifest to know exactly what assets are available and how to resolve them,
    /// rather than blindly calling LoadAllAssets which is expensive and error-prone.
    /// </summary>
    public static class UIBundleManifestManager
    {
        //  Paths

        private static readonly string BundleCacheDir = Path.Combine(
            FiresCore.Storage.FiresConfigPaths.UiAssets, "CapturedAssets", "bundles");

        private static readonly string ManifestDir = Path.Combine(
            FiresCore.Storage.FiresConfigPaths.UiAssets, "CapturedAssets", "bundle_manifests");

        //  Data structures

        /// <summary>Describes a single asset inside a cached bundle.</summary>
        public class BundleAssetEntry
        {
            public string Name;
            public string Type; // "Sprite", "Texture2D", "Material", "GameObject", etc.
            public string Path; // Asset path inside the bundle (if available)

            // Sprite-specific metadata
            public int Width;
            public int Height;
            public float BorderL, BorderB, BorderR, BorderT;
            public float PivotX = 0.5f;
            public float PivotY = 0.5f;
            public float PixelsPerUnit = 100f;
            public bool IsPacked; // Part of a sprite atlas
            public string TextureName; // Parent texture name for atlas sprites
        }

        /// <summary>Describes a cached bundle and all its assets.</summary>
        public class BundleManifest
        {
            public int Version = 1;
            public string BundleName;       // Internal bundle name (from UnityFS header)
            public string SourceMod;        // BepInPlugin GUID of the source mod
            public string SourceAssembly;   // Assembly name the bundle was extracted from
            public string CachedFileName;   // Filename of the raw data file on disk
            public long FileSizeBytes;       // Size of the raw bundle file
            public long CachedTimestamp;     // UTC timestamp when cached
            public bool FromEmbeddedResource; // True if extracted from an assembly resource
            public List<BundleAssetEntry> Assets = new List<BundleAssetEntry>();

            // Quick lookup counts
            public int SpriteCount;
            public int TextureCount;
            public int MaterialCount;
            public int PrefabCount;
            public int OtherCount;
        }

        //  In-memory cache

        private static readonly Dictionary<string, BundleManifest> _manifestCache =
            new Dictionary<string, BundleManifest>(StringComparer.OrdinalIgnoreCase);

        private static bool _manifestsLoaded;

        //  Public API

        /// <summary>
        /// Loads all bundle manifests from disk into memory.
        /// Safe to call multiple times - only loads once.
        /// </summary>
        public static void LoadAllManifests()
        {
            if (_manifestsLoaded) return;
            _manifestsLoaded = true;

            if (!Directory.Exists(ManifestDir)) return;

            try
            {
                string[] files = Directory.GetFiles(ManifestDir, "*.json");
                int loaded = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        string json = File.ReadAllText(files[i]);
                        var manifest = DeserializeManifest(json);
                        if (manifest != null && !string.IsNullOrEmpty(manifest.BundleName))
                        {
                            _manifestCache[manifest.BundleName] = manifest;
                            loaded++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[UIBundleManifest] Failed to load manifest {files[i]}: {ex.Message}");
                    }
                }

                if (loaded > 0)
                    Debug.Log($"[UIBundleManifest] Loaded {loaded} bundle manifest(s) from disk.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBundleManifest] Error loading manifests: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates and saves a manifest for a cached bundle by inspecting the live AssetBundle.
        /// Call this right after caching the raw bundle bytes to disk.
        /// </summary>
        public static BundleManifest CreateAndSaveManifest(AssetBundle bundle, string cachedFileName,
            string sourceMod, string sourceAssembly, bool fromEmbeddedResource, long fileSizeBytes)
        {
            if (bundle == null) return null;

            string bundleName;
            try { bundleName = bundle.name; } catch { return null; }
            if (string.IsNullOrEmpty(bundleName)) return null;

            var manifest = new BundleManifest
            {
                BundleName = bundleName,
                SourceMod = sourceMod ?? "",
                SourceAssembly = sourceAssembly ?? "",
                CachedFileName = cachedFileName,
                FileSizeBytes = fileSizeBytes,
                CachedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                FromEmbeddedResource = fromEmbeddedResource
            };

            // Index all sprites
            try
            {
                Sprite[] sprites = bundle.LoadAllAssets<Sprite>();
                if (sprites != null)
                {
                    for (int i = 0; i < sprites.Length; i++)
                    {
                        if (sprites[i] == null) continue;
                        var entry = new BundleAssetEntry
                        {
                            Name = sprites[i].name ?? "",
                            Type = "Sprite",
                            Width = (int)sprites[i].rect.width,
                            Height = (int)sprites[i].rect.height,
                            PivotX = sprites[i].pivot.x / sprites[i].rect.width,
                            PivotY = sprites[i].pivot.y / sprites[i].rect.height,
                            PixelsPerUnit = sprites[i].pixelsPerUnit,
                            IsPacked = sprites[i].packed
                        };

                        if (sprites[i].texture != null)
                            entry.TextureName = sprites[i].texture.name;

                        var border = sprites[i].border;
                        entry.BorderL = border.x;
                        entry.BorderB = border.y;
                        entry.BorderR = border.z;
                        entry.BorderT = border.w;

                        manifest.Assets.Add(entry);
                        manifest.SpriteCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBundleManifest] Error indexing sprites from '{bundleName}': {ex.Message}");
            }

            // Index all textures (that aren't already covered by sprite textures)
            try
            {
                Texture2D[] textures = bundle.LoadAllAssets<Texture2D>();
                if (textures != null)
                {
                    var spriteTextureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < manifest.Assets.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(manifest.Assets[i].TextureName))
                            spriteTextureNames.Add(manifest.Assets[i].TextureName);
                    }

                    for (int i = 0; i < textures.Length; i++)
                    {
                        if (textures[i] == null || string.IsNullOrEmpty(textures[i].name)) continue;
                        // Skip textures that are already represented by a sprite entry
                        if (spriteTextureNames.Contains(textures[i].name)) continue;

                        manifest.Assets.Add(new BundleAssetEntry
                        {
                            Name = textures[i].name,
                            Type = "Texture2D",
                            Width = textures[i].width,
                            Height = textures[i].height
                        });
                        manifest.TextureCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBundleManifest] Error indexing textures from '{bundleName}': {ex.Message}");
            }

            // Index materials
            try
            {
                Material[] materials = bundle.LoadAllAssets<Material>();
                if (materials != null)
                {
                    for (int i = 0; i < materials.Length; i++)
                    {
                        if (materials[i] == null) continue;
                        manifest.Assets.Add(new BundleAssetEntry
                        {
                            Name = materials[i].name ?? "",
                            Type = "Material"
                        });
                        manifest.MaterialCount++;
                    }
                }
            }
            catch { }

            // Index prefabs
            try
            {
                GameObject[] prefabs = bundle.LoadAllAssets<GameObject>();
                if (prefabs != null)
                {
                    for (int i = 0; i < prefabs.Length; i++)
                    {
                        if (prefabs[i] == null) continue;
                        manifest.Assets.Add(new BundleAssetEntry
                        {
                            Name = prefabs[i].name ?? "",
                            Type = "GameObject"
                        });
                        manifest.PrefabCount++;
                    }
                }
            }
            catch { }

            // Get asset paths for additional metadata
            try
            {
                string[] allPaths = bundle.GetAllAssetNames();
                if (allPaths != null)
                {
                    // Cross-reference paths with existing entries
                    var nameToEntry = new Dictionary<string, BundleAssetEntry>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < manifest.Assets.Count; i++)
                    {
                        if (!nameToEntry.ContainsKey(manifest.Assets[i].Name))
                            nameToEntry[manifest.Assets[i].Name] = manifest.Assets[i];
                    }

                    for (int i = 0; i < allPaths.Length; i++)
                    {
                        string assetPath = allPaths[i];
                        string assetName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                        if (nameToEntry.TryGetValue(assetName, out var entry))
                        {
                            entry.Path = assetPath;
                        }
                    }
                }
            }
            catch { }

            // Save to disk
            SaveManifest(manifest);
            _manifestCache[bundleName] = manifest;

            Debug.Log($"[UIBundleManifest] Created manifest for '{bundleName}': " +
                $"{manifest.SpriteCount} sprites, {manifest.TextureCount} textures, " +
                $"{manifest.MaterialCount} materials, {manifest.PrefabCount} prefabs");

            return manifest;
        }

        /// <summary>
        /// Returns the manifest for a given bundle name, or null if not cached.
        /// </summary>
        public static BundleManifest GetManifest(string bundleName)
        {
            if (string.IsNullOrEmpty(bundleName)) return null;

            if (!_manifestsLoaded) LoadAllManifests();

            _manifestCache.TryGetValue(bundleName, out var manifest);
            return manifest;
        }

        /// <summary>
        /// Returns all cached bundle manifests.
        /// </summary>
        public static List<BundleManifest> GetAllManifests()
        {
            if (!_manifestsLoaded) LoadAllManifests();
            return new List<BundleManifest>(_manifestCache.Values);
        }

        /// <summary>
        /// Checks if a specific sprite name exists in any cached bundle manifest.
        /// Returns the bundle name if found, or null.
        /// </summary>
        public static string FindBundleForSprite(string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName)) return null;
            if (!_manifestsLoaded) LoadAllManifests();

            foreach (var kvp in _manifestCache)
            {
                var manifest = kvp.Value;
                for (int i = 0; i < manifest.Assets.Count; i++)
                {
                    var asset = manifest.Assets[i];
                    if (asset.Type != "Sprite" && asset.Type != "Texture2D") continue;

                    if (string.Equals(asset.Name, spriteName, StringComparison.OrdinalIgnoreCase))
                        return manifest.BundleName;

                    // Check qualified "texture:sprite" format
                    if (!string.IsNullOrEmpty(asset.TextureName))
                    {
                        string qualified = asset.TextureName + ":" + asset.Name;
                        if (string.Equals(qualified, spriteName, StringComparison.OrdinalIgnoreCase))
                            return manifest.BundleName;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the sprite metadata from a bundle manifest entry.
        /// Useful for creating sprites from cached bundles with correct pivot, border, PPU.
        /// </summary>
        public static BundleAssetEntry GetSpriteMetadata(string bundleName, string spriteName)
        {
            var manifest = GetManifest(bundleName);
            if (manifest == null) return null;

            for (int i = 0; i < manifest.Assets.Count; i++)
            {
                var asset = manifest.Assets[i];
                if (asset.Type != "Sprite") continue;

                if (string.Equals(asset.Name, spriteName, StringComparison.OrdinalIgnoreCase))
                    return asset;
            }

            return null;
        }

        /// <summary>
        /// Gets the raw data file path for a cached bundle.
        /// Returns null if the bundle isn't cached or the file doesn't exist.
        /// </summary>
        public static string GetCachedBundleFilePath(string bundleName)
        {
            var manifest = GetManifest(bundleName);
            if (manifest == null || string.IsNullOrEmpty(manifest.CachedFileName))
                return null;

            string filePath = Path.Combine(BundleCacheDir, manifest.CachedFileName);
            return File.Exists(filePath) ? filePath : null;
        }

        /// <summary>
        /// Loads a cached bundle from disk using the manifest.
        /// Returns the loaded AssetBundle, or null on failure.
        /// </summary>
        public static AssetBundle LoadCachedBundle(string bundleName)
        {
            string filePath = GetCachedBundleFilePath(bundleName);
            if (filePath == null)
            {
                Debug.LogWarning($"[UIBundleManifest] No cached file for bundle '{bundleName}'");
                return null;
            }

            try
            {
                var bundle = AssetBundle.LoadFromFile(filePath);
                if (bundle != null)
                {
                    Debug.Log($"[UIBundleManifest] Loaded cached bundle '{bundleName}' from {filePath}");
                    return bundle;
                }
                else
                {
                    Debug.LogWarning($"[UIBundleManifest] Failed to load bundle from {filePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBundleManifest] Error loading cached bundle '{bundleName}': {ex.Message}");
            }

            return null;
        }

        //  Serialization

        private static void SaveManifest(BundleManifest manifest)
        {
            if (manifest == null || string.IsNullOrEmpty(manifest.BundleName)) return;

            try
            {
                if (!Directory.Exists(ManifestDir))
                    Directory.CreateDirectory(ManifestDir);

                string safeName = MakeSafeFileName(manifest.BundleName);
                string filePath = Path.Combine(ManifestDir, safeName + ".json");

                string json = SerializeManifest(manifest);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBundleManifest] Failed to save manifest for '{manifest.BundleName}': {ex.Message}");
            }
        }

        private static string SerializeManifest(BundleManifest manifest)
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine("{");
            sb.AppendLine($"  \"version\": {manifest.Version},");
            sb.AppendLine($"  \"bundleName\": \"{EscapeJson(manifest.BundleName)}\",");
            sb.AppendLine($"  \"sourceMod\": \"{EscapeJson(manifest.SourceMod)}\",");
            sb.AppendLine($"  \"sourceAssembly\": \"{EscapeJson(manifest.SourceAssembly)}\",");
            sb.AppendLine($"  \"cachedFileName\": \"{EscapeJson(manifest.CachedFileName)}\",");
            sb.AppendLine($"  \"fileSizeBytes\": {manifest.FileSizeBytes},");
            sb.AppendLine($"  \"cachedTimestamp\": {manifest.CachedTimestamp},");
            sb.AppendLine($"  \"fromEmbeddedResource\": {(manifest.FromEmbeddedResource ? "true" : "false")},");
            sb.AppendLine($"  \"spriteCount\": {manifest.SpriteCount},");
            sb.AppendLine($"  \"textureCount\": {manifest.TextureCount},");
            sb.AppendLine($"  \"materialCount\": {manifest.MaterialCount},");
            sb.AppendLine($"  \"prefabCount\": {manifest.PrefabCount},");
            sb.AppendLine($"  \"otherCount\": {manifest.OtherCount},");
            sb.AppendLine("  \"assets\": [");

            for (int i = 0; i < manifest.Assets.Count; i++)
            {
                var asset = manifest.Assets[i];
                sb.Append("    { ");
                sb.Append($"\"name\": \"{EscapeJson(asset.Name)}\", ");
                sb.Append($"\"type\": \"{EscapeJson(asset.Type)}\"");

                if (!string.IsNullOrEmpty(asset.Path))
                    sb.Append($", \"path\": \"{EscapeJson(asset.Path)}\"");

                if (asset.Width > 0) sb.Append($", \"width\": {asset.Width}");
                if (asset.Height > 0) sb.Append($", \"height\": {asset.Height}");

                if (asset.Type == "Sprite")
                {
                    sb.Append($", \"pivotX\": {asset.PivotX.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    sb.Append($", \"pivotY\": {asset.PivotY.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    sb.Append($", \"ppu\": {asset.PixelsPerUnit.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

                    if (asset.BorderL != 0 || asset.BorderB != 0 || asset.BorderR != 0 || asset.BorderT != 0)
                    {
                        sb.Append($", \"borderL\": {asset.BorderL.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                        sb.Append($", \"borderB\": {asset.BorderB.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                        sb.Append($", \"borderR\": {asset.BorderR.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                        sb.Append($", \"borderT\": {asset.BorderT.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    }

                    if (asset.IsPacked) sb.Append(", \"isPacked\": true");
                    if (!string.IsNullOrEmpty(asset.TextureName))
                        sb.Append($", \"textureName\": \"{EscapeJson(asset.TextureName)}\"");
                }

                sb.Append(" }");
                if (i < manifest.Assets.Count - 1) sb.AppendLine(",");
                else sb.AppendLine();
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static BundleManifest DeserializeManifest(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            var manifest = new BundleManifest
            {
                Version = ExtractJsonInt(json, "version"),
                BundleName = ExtractJsonString(json, "bundleName"),
                SourceMod = ExtractJsonString(json, "sourceMod"),
                SourceAssembly = ExtractJsonString(json, "sourceAssembly"),
                CachedFileName = ExtractJsonString(json, "cachedFileName"),
                FileSizeBytes = ExtractJsonLong(json, "fileSizeBytes"),
                CachedTimestamp = ExtractJsonLong(json, "cachedTimestamp"),
                FromEmbeddedResource = ExtractJsonBool(json, "fromEmbeddedResource"),
                SpriteCount = ExtractJsonInt(json, "spriteCount"),
                TextureCount = ExtractJsonInt(json, "textureCount"),
                MaterialCount = ExtractJsonInt(json, "materialCount"),
                PrefabCount = ExtractJsonInt(json, "prefabCount"),
                OtherCount = ExtractJsonInt(json, "otherCount")
            };

            // Parse assets array
            int assetsIdx = json.IndexOf("\"assets\"", StringComparison.Ordinal);
            if (assetsIdx >= 0)
            {
                int arrayStart = json.IndexOf('[', assetsIdx);
                int arrayEnd = json.LastIndexOf(']');
                if (arrayStart >= 0 && arrayEnd > arrayStart)
                {
                    string arrayContent = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
                    ParseAssetEntries(arrayContent, manifest.Assets);
                }
            }

            return manifest;
        }

        private static void ParseAssetEntries(string arrayContent, List<BundleAssetEntry> entries)
        {
            int idx = 0;
            while (idx < arrayContent.Length)
            {
                int objStart = arrayContent.IndexOf('{', idx);
                if (objStart < 0) break;
                int objEnd = arrayContent.IndexOf('}', objStart);
                if (objEnd < 0) break;

                string objStr = arrayContent.Substring(objStart, objEnd - objStart + 1);

                var entry = new BundleAssetEntry
                {
                    Name = ExtractJsonString(objStr, "name"),
                    Type = ExtractJsonString(objStr, "type"),
                    Path = ExtractJsonString(objStr, "path"),
                    Width = ExtractJsonInt(objStr, "width"),
                    Height = ExtractJsonInt(objStr, "height"),
                    PivotX = ExtractJsonFloat(objStr, "pivotX", 0.5f),
                    PivotY = ExtractJsonFloat(objStr, "pivotY", 0.5f),
                    PixelsPerUnit = ExtractJsonFloat(objStr, "ppu", 100f),
                    BorderL = ExtractJsonFloat(objStr, "borderL"),
                    BorderB = ExtractJsonFloat(objStr, "borderB"),
                    BorderR = ExtractJsonFloat(objStr, "borderR"),
                    BorderT = ExtractJsonFloat(objStr, "borderT"),
                    IsPacked = ExtractJsonBool(objStr, "isPacked"),
                    TextureName = ExtractJsonString(objStr, "textureName")
                };

                entries.Add(entry);
                idx = objEnd + 1;
            }
        }

        //  JSON helpers (minimal parser, no deps)

        private static string EscapeJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static string ExtractJsonString(string json, string key)
        {
            string search = "\"" + key + "\"";
            int keyIdx = json.IndexOf(search, StringComparison.Ordinal);
            if (keyIdx < 0) return "";

            int colonIdx = json.IndexOf(':', keyIdx + search.Length);
            if (colonIdx < 0) return "";

            int quoteStart = json.IndexOf('"', colonIdx + 1);
            if (quoteStart < 0) return "";

            int quoteEnd = quoteStart + 1;
            while (quoteEnd < json.Length)
            {
                if (json[quoteEnd] == '"' && json[quoteEnd - 1] != '\\')
                    break;
                quoteEnd++;
            }

            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1)
                       .Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r");
        }

        private static int ExtractJsonInt(string json, string key)
        {
            string val = ExtractJsonNumber(json, key);
            int.TryParse(val, out int result);
            return result;
        }

        private static long ExtractJsonLong(string json, string key)
        {
            string val = ExtractJsonNumber(json, key);
            long.TryParse(val, out long result);
            return result;
        }

        private static float ExtractJsonFloat(string json, string key, float defaultValue = 0f)
        {
            string val = ExtractJsonNumber(json, key);
            if (string.IsNullOrEmpty(val) || val == "0")
            {
                string search = "\"" + key + "\"";
                if (json.IndexOf(search, StringComparison.Ordinal) < 0)
                    return defaultValue;
            }
            if (float.TryParse(val, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float result))
                return result;
            return defaultValue;
        }

        private static bool ExtractJsonBool(string json, string key)
        {
            string search = "\"" + key + "\"";
            int keyIdx = json.IndexOf(search, StringComparison.Ordinal);
            if (keyIdx < 0) return false;

            int colonIdx = json.IndexOf(':', keyIdx + search.Length);
            if (colonIdx < 0) return false;

            int start = colonIdx + 1;
            while (start < json.Length && json[start] == ' ') start++;

            if (start < json.Length - 3 &&
                json[start] == 't' && json[start + 1] == 'r' &&
                json[start + 2] == 'u' && json[start + 3] == 'e')
                return true;

            return false;
        }

        private static string ExtractJsonNumber(string json, string key)
        {
            string search = "\"" + key + "\"";
            int keyIdx = json.IndexOf(search, StringComparison.Ordinal);
            if (keyIdx < 0) return "0";

            int colonIdx = json.IndexOf(':', keyIdx + search.Length);
            if (colonIdx < 0) return "0";

            int start = colonIdx + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t'))
                start++;

            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-' || json[end] == '.'))
                end++;

            return json.Substring(start, end - start);
        }

        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            return sb.ToString();
        }
    }
}
