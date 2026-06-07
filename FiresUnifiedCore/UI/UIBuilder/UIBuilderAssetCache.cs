using BepInEx;
using BepInEx.Bootstrap;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Caches sprites from captured UIs to disk so layouts work independently of source mods.
    /// During capture, ALL non-trivial sprites (everything except Unity built-ins) are
    /// extracted to PNG and written to:
    ///   Config/FiresRPGmaker/UIAssets/CapturedAssets/sprites/
    /// 
    /// This ensures that modded sprite replacements (e.g., Azumatt's Minimal UI replacing
    /// vanilla HUD icons) are preserved even when the source mod is removed.
    /// Cached sprites are referenced in layout JSON with a "cached:" prefix and loaded
    /// back from disk by the renderer.
    /// </summary>
    public static class UIBuilderAssetCache
    {
        private static readonly string CacheDir = Path.Combine(
            Paths.ConfigPath, "FiresRPGmaker", "UIAssets", "CapturedAssets", "sprites");
        private static readonly string ManifestPath = Path.Combine(
            Paths.ConfigPath, "FiresRPGmaker", "UIAssets", "CapturedAssets", "manifest.json");

        /// <summary>Directory where raw asset bundle files are cached for later embedding.</summary>
        private static readonly string BundleCacheDir = Path.Combine(
            Paths.ConfigPath, "FiresRPGmaker", "UIAssets", "CapturedAssets", "bundles");

        /// <summary>Tracks bundle names already cached this session to avoid redundant disk writes.</summary>
        private static readonly HashSet<string> _cachedBundleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Asset bundles loaded from cached .bundle files on disk.
        /// These are loaded via AssetBundle.LoadFromFile and provide the REAL sprite
        /// assets with full metadata (9-slice borders, pivot, PPU, atlas rects, compression).
        /// Keyed by the safe bundle filename (without extension).
        /// </summary>
        private static readonly Dictionary<string, AssetBundle> _loadedBundles =
            new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Sprites loaded from cached asset bundles, keyed by sprite name.
        /// This is the primary sprite source — provides real sprites with all original metadata.
        /// </summary>
        private static readonly Dictionary<string, Sprite> _bundleSprites =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True once cached bundles have been loaded and their sprites indexed.</summary>
        private static bool _bundleSpritesLoaded;

        /// <summary>Prefix used in sprite names to indicate a cached asset.</summary>
        public const string CachePrefix = "cached:";

        private static CacheManifest _manifest;
        private static readonly Dictionary<string, Sprite> _loadedCache = new Dictionary<string, Sprite>();

        /// <summary>Maximum texture dimension for cached sprites (larger textures are downscaled).</summary>
        private const int MaxCacheDimension = 1024;

        /// <summary>
        /// When true, CacheSprite/CacheRawImageTexture will overwrite existing cache entries
        /// instead of reusing them. Set this before a re-capture to refresh stale assets.
        /// Reset to false after the capture is complete.
        /// </summary>
        public static bool ForceRecache { get; set; }

        // ???????????????????????????????????????
        //  Public API — Caching (used during capture)
        // ???????????????????????????????????????

        /// <summary>
        /// Inspects an Image component's sprite during capture. If the sprite appears to come
        /// from a modded assembly (not Unity, not Valheim, not ours), clones the texture to
        /// disk and returns a "cached:filename" sprite name for the layout data.
        /// Returns the original sprite name unchanged if caching is not needed.
        /// </summary>
        public static string CacheSpriteFromImage(Image img, string fallbackName = null)
        {
            if (img == null || img.sprite == null)
                return fallbackName ?? "";

            return CacheSprite(img.sprite, fallbackName);
        }

        /// <summary>
        /// Caches a sprite to disk if it appears to be from a mod. Returns the sprite name
        /// to store in the layout data — either "cached:filename" or the original name.
        /// </summary>
        public static string CacheSprite(Sprite sprite, string fallbackName = null)
        {
            if (sprite == null)
                return fallbackName ?? "";

            string originalName = sprite.name ?? "";
            if (string.IsNullOrEmpty(originalName) && !string.IsNullOrEmpty(fallbackName))
                originalName = fallbackName;

            // Don't re-cache something already cached
            if (originalName.StartsWith(CachePrefix, StringComparison.Ordinal))
                return originalName;

            // Check if this sprite needs caching (is it from a mod?)
            if (!UIBuilderAutoDiscovery.IsSpriteFromMod(sprite))
                return originalName;

            // Check if we already cached this exact sprite
            EnsureManifest();
            if (!ForceRecache)
            {
                string existingCached = FindExistingCacheEntry(originalName, sprite);
                if (existingCached != null)
                    return CachePrefix + existingCached;
            }

            // Clone the texture to disk
            try
            {
                EnsureCacheDir();

                string safeName = MakeSafeFileName(originalName);
                string filePath = Path.Combine(CacheDir, safeName + ".png");

                // When force-recaching, overwrite the existing file if it matches.
                // Otherwise avoid collisions by appending a counter.
                if (ForceRecache)
                {
                    // Remove any existing manifest entries for this sprite so we don't accumulate duplicates
                    RemoveManifestEntriesByOriginalName(originalName);
                }
                else
                {
                    int counter = 1;
                    while (File.Exists(filePath))
                    {
                        safeName = MakeSafeFileName(originalName) + "_" + counter;
                        filePath = Path.Combine(CacheDir, safeName + ".png");
                        counter++;
                    }
                }

                byte[] pngBytes = ExtractSpriteToPNG(sprite);
                if (pngBytes == null || pngBytes.Length == 0)
                {
                    Debug.LogWarning($"[UIBuilderAssetCache] Failed to extract pixels from sprite '{originalName}'");
                    return originalName;
                }

                File.WriteAllBytes(filePath, pngBytes);

                // Add to manifest
                var entry = new CacheEntry
                {
                    OriginalName = originalName,
                    CachedFile = safeName + ".png",
                    SourceAssembly = DetectSpriteAssembly(sprite),
                    TextureWidth = (int)sprite.rect.width,
                    TextureHeight = (int)sprite.rect.height,
                    CapturedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    BorderL = sprite.border.x,
                    BorderB = sprite.border.y,
                    BorderR = sprite.border.z,
                    BorderT = sprite.border.w,
                    PivotX = sprite.pivot.x / Mathf.Max(1f, sprite.rect.width),
                    PivotY = sprite.pivot.y / Mathf.Max(1f, sprite.rect.height),
                    PixelsPerUnit = sprite.pixelsPerUnit
                };
                _manifest.Entries.Add(entry);
                SaveManifest();

                Debug.Log($"[UIBuilderAssetCache] Cached sprite '{originalName}' ? {safeName}.png ({entry.TextureWidth}x{entry.TextureHeight})");
                return CachePrefix + safeName;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Failed to cache sprite '{originalName}': {ex.Message}");
                return originalName;
            }
        }

        // ???????????????????????????????????????
        //  Public API — Loading (used by renderer)
        // ???????????????????????????????????????

        /// <summary>
        /// Caches a RawImage's texture to disk. RawImages use Texture2D directly instead of
        /// Sprite, so they need separate handling. Returns a "cached:filename" name or the
        /// original texture name if caching is not needed.
        /// Like CacheSprite, this caches ALL textures except known Unity built-ins.
        /// </summary>
        public static string CacheRawImageTexture(RawImage rawImg, string fallbackName = null)
        {
            if (rawImg == null || rawImg.texture == null)
                return fallbackName ?? "";

            var tex = rawImg.texture as Texture2D;
            if (tex == null)
                return fallbackName ?? rawImg.texture.name ?? "";

            string originalName = tex.name ?? "";
            if (string.IsNullOrEmpty(originalName) && !string.IsNullOrEmpty(fallbackName))
                originalName = fallbackName;

            // Don't re-cache
            if (originalName.StartsWith(CachePrefix, StringComparison.Ordinal))
                return originalName;

            // Skip known Unity built-in texture names that are always available
            if (originalName == "UISprite" || originalName == "Background" ||
                originalName == "InputFieldBackground" || originalName == "UnitySplash")
                return originalName;

            // Check if already cached
            EnsureManifest();
            if (!ForceRecache)
            {
                string existingCached = FindExistingCacheEntryByName(originalName, tex.width, tex.height);
                if (existingCached != null)
                    return CachePrefix + existingCached;
            }

            try
            {
                EnsureCacheDir();

                string safeName = MakeSafeFileName(originalName);
                string filePath = Path.Combine(CacheDir, safeName + ".png");

                if (ForceRecache)
                {
                    RemoveManifestEntriesByOriginalName(originalName);
                }
                else
                {
                    int counter = 1;
                    while (File.Exists(filePath))
                    {
                        safeName = MakeSafeFileName(originalName) + "_" + counter;
                        filePath = Path.Combine(CacheDir, safeName + ".png");
                        counter++;
                    }
                }

                byte[] pngBytes = ExtractTextureToPNG(tex);
                if (pngBytes == null || pngBytes.Length == 0)
                {
                    Debug.LogWarning($"[UIBuilderAssetCache] Failed to extract pixels from RawImage texture '{originalName}'");
                    return originalName;
                }

                File.WriteAllBytes(filePath, pngBytes);

                var entry = new CacheEntry
                {
                    OriginalName = originalName,
                    CachedFile = safeName + ".png",
                    SourceAssembly = "",
                    TextureWidth = tex.width,
                    TextureHeight = tex.height,
                    CapturedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    PivotX = 0.5f,
                    PivotY = 0.5f,
                    PixelsPerUnit = 100f
                };
                _manifest.Entries.Add(entry);
                SaveManifest();

                Debug.Log($"[UIBuilderAssetCache] Cached RawImage texture '{originalName}' ? {safeName}.png ({tex.width}x{tex.height})");
                return CachePrefix + safeName;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Failed to cache RawImage texture '{originalName}': {ex.Message}");
                return originalName;
            }
        }

        /// <summary>
        /// Loads a cached sprite from disk by its cache ID (the part after "cached:" prefix).
        /// Returns null if the cached file doesn't exist.
        /// </summary>
        public static Sprite LoadCachedSprite(string cacheId)
        {
            if (string.IsNullOrEmpty(cacheId)) return null;

            // Check in-memory cache first
            if (_loadedCache.TryGetValue(cacheId, out var cached) && cached != null)
                return cached;

            // Load from disk
            string fileName = cacheId;
            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                fileName += ".png";

            string filePath = Path.Combine(CacheDir, fileName);
            if (!File.Exists(filePath))
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Cached sprite file not found: {filePath}");
                return null;
            }

            try
            {
                byte[] fileData = File.ReadAllBytes(filePath);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                bool loaded = LoadImageIntoTexture(tex, fileData);
                if (!loaded)
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }

                tex.name = cacheId;

                // Look up manifest entry for sliced sprite metadata (border, pivot, ppu)
                Vector4 border = Vector4.zero;
                Vector2 pivot = new Vector2(0.5f, 0.5f);
                float ppu = 100f;
                EnsureManifest();
                if (_manifest != null && _manifest.Entries != null)
                {
                    for (int i = 0; i < _manifest.Entries.Count; i++)
                    {
                        var me = _manifest.Entries[i];
                        string meFile = Path.GetFileNameWithoutExtension(me.CachedFile);
                        if (string.Equals(meFile, cacheId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(me.CachedFile, fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            border = new Vector4(me.BorderL, me.BorderB, me.BorderR, me.BorderT);
                            pivot = new Vector2(me.PivotX, me.PivotY);
                            ppu = me.PixelsPerUnit > 0 ? me.PixelsPerUnit : 100f;
                            break;
                        }
                    }
                }

                var sprite = Sprite.Create(tex,
                    new Rect(0, 0, tex.width, tex.height),
                    pivot, ppu, 0, SpriteMeshType.FullRect, border);

                _loadedCache[cacheId] = sprite;
                return sprite;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Failed to load cached sprite '{cacheId}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Checks if a sprite name uses the cache prefix.
        /// </summary>
        public static bool IsCachedName(string spriteName)
        {
            return !string.IsNullOrEmpty(spriteName) &&
                   spriteName.StartsWith(CachePrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Extracts the cache ID from a cached sprite name (strips the "cached:" prefix).
        /// </summary>
        public static string GetCacheId(string spriteName)
        {
            if (IsCachedName(spriteName))
                return spriteName.Substring(CachePrefix.Length);
            return spriteName;
        }

        /// <summary>
        /// Loads a cached sprite by searching the manifest for its original name.
        /// This is used when a layout stores a sprite name without the "cached:" prefix
        /// but the sprite was previously cached from a mod bundle scan.
        /// Returns null if no matching cache entry exists.
        /// </summary>
        public static Sprite LoadCachedSpriteByOriginalName(string originalName)
        {
            if (string.IsNullOrEmpty(originalName)) return null;
            EnsureManifest();
            if (_manifest == null || _manifest.Entries == null) return null;

            for (int i = 0; i < _manifest.Entries.Count; i++)
            {
                var entry = _manifest.Entries[i];
                // Match by original name (e.g., "MUIAtlas:MUI_bar") OR by cached file name
                // without extension (e.g., "MUIAtlas_MUI_bar"). The latter handles the case
                // where a "cached:" prefix was stripped and only the safe filename remains.
                string cachedFileNoExt = Path.GetFileNameWithoutExtension(entry.CachedFile);
                if (string.Equals(entry.OriginalName, originalName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cachedFileNoExt, originalName, StringComparison.OrdinalIgnoreCase))
                {
                    var sprite = LoadCachedSprite(cachedFileNoExt);
                    if (sprite != null)
                        return sprite;
                }
            }
            return null;
        }

        /// <summary>
        /// Returns all cached sprite names (without the "cached:" prefix).
        /// </summary>
        public static List<string> GetAllCachedNames()
        {
            var names = new List<string>();
            if (!Directory.Exists(CacheDir)) return names;

            foreach (var file in Directory.GetFiles(CacheDir, "*.png"))
                names.Add(Path.GetFileNameWithoutExtension(file));

            return names;
        }

        /// <summary>
        /// Clears the in-memory sprite cache. Cached files on disk are preserved.
        /// </summary>
        public static void ClearMemoryCache()
        {
            _loadedCache.Clear();
        }

        // ???????????????????????????????????????
        //  Bundle-based sprite loading (primary source)
        // ???????????????????????????????????????

        /// <summary>
        /// Loads all cached .bundle files from the bundles directory and indexes every
        /// Sprite asset they contain. This gives us the REAL sprite objects with full
        /// original metadata (9-slice borders, pivot, pixels-per-unit, atlas rects,
        /// compression, etc.) — exactly as the source mod shipped them.
        ///
        /// Call this once early (e.g., from ScanAndCacheAllModBundles or Init).
        /// Safe to call multiple times — only loads once unless force is true.
        /// </summary>
        public static void LoadCachedBundles(bool force = false)
        {
            if (_bundleSpritesLoaded && !force) return;
            _bundleSpritesLoaded = true;

            if (!Directory.Exists(BundleCacheDir)) return;

            string[] bundleFiles;
            try { bundleFiles = Directory.GetFiles(BundleCacheDir, "*.bundle"); }
            catch { return; }

            int loadedBundles = 0;
            int indexedSprites = 0;

            for (int i = 0; i < bundleFiles.Length; i++)
            {
                string filePath = bundleFiles[i];
                string safeName = Path.GetFileNameWithoutExtension(filePath);

                // Skip if already loaded
                if (_loadedBundles.ContainsKey(safeName)) continue;

                try
                {
                    var bundle = AssetBundle.LoadFromFile(filePath);
                    if (bundle == null)
                    {
                        Debug.LogWarning($"[UIBuilderAssetCache] Failed to load cached bundle: {filePath}");
                        continue;
                    }

                    _loadedBundles[safeName] = bundle;
                    loadedBundles++;

                    // Index all sprites in this bundle
                    Sprite[] sprites = null;
                    try { sprites = bundle.LoadAllAssets<Sprite>(); } catch { }
                    if (sprites != null)
                    {
                        for (int s = 0; s < sprites.Length; s++)
                        {
                            if (sprites[s] == null || string.IsNullOrEmpty(sprites[s].name)) continue;
                            string spriteName = sprites[s].name;

                            // Store by plain name
                            if (!_bundleSprites.ContainsKey(spriteName))
                            {
                                _bundleSprites[spriteName] = sprites[s];
                                indexedSprites++;
                            }

                            // Also store by qualified "texture:sprite" name for atlas lookups
                            if (sprites[s].texture != null && !string.IsNullOrEmpty(sprites[s].texture.name))
                            {
                                string qualifiedName = sprites[s].texture.name + ":" + spriteName;
                                if (!_bundleSprites.ContainsKey(qualifiedName))
                                    _bundleSprites[qualifiedName] = sprites[s];
                            }
                        }
                    }

                    // Also index raw Texture2D assets as sprites for RawImage lookups
                    Texture2D[] textures = null;
                    try { textures = bundle.LoadAllAssets<Texture2D>(); } catch { }
                    if (textures != null)
                    {
                        for (int t = 0; t < textures.Length; t++)
                        {
                            if (textures[t] == null || string.IsNullOrEmpty(textures[t].name)) continue;
                            if (textures[t].width <= 0 || textures[t].height <= 0) continue;

                            string texName = textures[t].name;
                            // Only create a sprite wrapper if we don't already have a real sprite for this name
                            if (!_bundleSprites.ContainsKey(texName))
                            {
                                try
                                {
                                    var texSprite = Sprite.Create(textures[t],
                                        new Rect(0, 0, textures[t].width, textures[t].height),
                                        new Vector2(0.5f, 0.5f), 100f);
                                    if (texSprite != null)
                                    {
                                        texSprite.name = texName;
                                        _bundleSprites[texName] = texSprite;
                                        indexedSprites++;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UIBuilderAssetCache] Error loading cached bundle '{filePath}': {ex.Message}");
                }
            }

            if (loadedBundles > 0)
            {
                Debug.Log($"[UIBuilderAssetCache] Loaded {loadedBundles} cached bundle(s), " +
                    $"indexed {indexedSprites} sprites from bundle assets");
            }
        }

        /// <summary>
        /// Looks up a sprite by name from the cached asset bundles.
        /// Returns the real sprite object with full original metadata, or null if not found.
        /// This is the PRIMARY sprite source and should be checked before PNG fallbacks.
        /// </summary>
        public static Sprite LoadSpriteFromCachedBundle(string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName)) return null;

            // Only search sprites that have already been indexed via LoadRequiredBundles(),
            // IndexAllLiveModBundles(), or IndexSpritesFromBundle(). We do NOT auto-call
            // LoadCachedBundles() here because that synchronously loads every .bundle file
            // from disk — which is catastrophically expensive during capture/rendering and
            // causes server disconnects.

            // Direct lookup
            if (_bundleSprites.TryGetValue(spriteName, out var sprite) && sprite != null)
                return sprite;

            // Try plain name (strip "texture:sprite" qualifier)
            int colonIdx = spriteName.IndexOf(':');
            if (colonIdx > 0 && colonIdx < spriteName.Length - 1)
            {
                string plainName = spriteName.Substring(colonIdx + 1);
                if (_bundleSprites.TryGetValue(plainName, out sprite) && sprite != null)
                    return sprite;
            }

            // Try stripping "cached:" prefix
            if (spriteName.StartsWith(CachePrefix, StringComparison.Ordinal))
            {
                string stripped = spriteName.Substring(CachePrefix.Length);
                if (_bundleSprites.TryGetValue(stripped, out sprite) && sprite != null)
                    return sprite;
            }

            // Manifest-aware fallback: check if any cached bundle manifest knows about
            // this sprite. If so, load that specific bundle on-demand and index it.
            string lookupName = spriteName;
            if (lookupName.StartsWith(CachePrefix, StringComparison.Ordinal))
                lookupName = lookupName.Substring(CachePrefix.Length);

            string bundleForSprite = UIBundleManifestManager.FindBundleForSprite(lookupName);
            if (!string.IsNullOrEmpty(bundleForSprite) && !_loadedBundles.ContainsKey(MakeSafeFileName(bundleForSprite)))
            {
                // Check it's not already loaded live by the owning mod
                bool loadedLive = false;
                try
                {
                    var allBundles = AssetBundle.GetAllLoadedAssetBundles();
                    if (allBundles != null)
                    {
                        foreach (var b in allBundles)
                        {
                            if (b == null) continue;
                            try
                            {
                                if (string.Equals(b.name, bundleForSprite, StringComparison.OrdinalIgnoreCase))
                                {
                                    IndexSpritesFromBundle(b, b.name);
                                    loadedLive = true;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                if (!loadedLive)
                {
                    // Load from our cached file
                    var cachedBundle = UIBundleManifestManager.LoadCachedBundle(bundleForSprite);
                    if (cachedBundle != null)
                    {
                        string safeName = MakeSafeFileName(bundleForSprite);
                        _loadedBundles[safeName] = cachedBundle;
                        IndexSpritesFromBundle(cachedBundle, safeName);
                        Debug.Log($"[UIBuilderAssetCache] On-demand loaded cached bundle '{bundleForSprite}' for sprite '{spriteName}'");
                    }
                }

                // Re-check after loading
                if (_bundleSprites.TryGetValue(lookupName, out sprite) && sprite != null)
                    return sprite;

                // Try plain name
                if (colonIdx > 0 && colonIdx < lookupName.Length - 1)
                {
                    string plainAfterLoad = lookupName.Substring(colonIdx + 1);
                    if (_bundleSprites.TryGetValue(plainAfterLoad, out sprite) && sprite != null)
                        return sprite;
                }
            }

            return null;
        }

        /// <summary>
        /// Finds the name of the loaded AssetBundle that contains a given sprite.
        /// Iterates all loaded bundles (excluding vanilla/Unity bundles and ours)
        /// and checks if the sprite's texture came from that bundle.
        /// Returns the bundle name, or null if not found.
        /// </summary>
        public static string FindBundleContainingSprite(Sprite sprite)
        {
            if (sprite == null) return null;

            string spriteName = sprite.name;
            Texture2D spriteTex = sprite.texture;
            if (string.IsNullOrEmpty(spriteName) && spriteTex == null) return null;

            try
            {
                var allBundles = AssetBundle.GetAllLoadedAssetBundles();
                if (allBundles == null) return null;

                foreach (var bundle in allBundles)
                {
                    if (bundle == null) continue;
                    if (IsVanillaBundle(bundle)) continue;
                    try { if (UIBuilderHost.AssetBundle != null && bundle == UIBuilderHost.AssetBundle) continue; } catch { }

                    try
                    {
                        // Check if the bundle contains an asset with this sprite name
                        if (!string.IsNullOrEmpty(spriteName) && bundle.Contains(spriteName))
                            return bundle.name;

                        // Also check by texture name (atlas sprites may only match via texture)
                        if (spriteTex != null && !string.IsNullOrEmpty(spriteTex.name) && bundle.Contains(spriteTex.name))
                            return bundle.name;
                    }
                    catch { /* bundle.Contains may fail on streamed scene bundles */ }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Caches the raw .bundle files for the given bundle names to disk so they can
        /// be loaded later by <see cref="LoadRequiredBundles"/> even when the source mod
        /// is no longer installed. Only caches bundles that are currently loaded in memory
        /// and not already cached on disk.
        /// Call this during capture after <see cref="FindBundleContainingSprite"/> has
        /// identified which bundles the layout's sprites come from.
        /// </summary>
        public static void CacheRequiredBundlesToDisk(HashSet<string> bundleNames)
        {
            if (bundleNames == null || bundleNames.Count == 0) return;

            int cached = 0;
            try
            {
                var allBundles = AssetBundle.GetAllLoadedAssetBundles();
                if (allBundles == null) return;

                // Build a quick lookup of live bundles by name
                var liveBundles = new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
                foreach (var bundle in allBundles)
                {
                    if (bundle == null) continue;
                    string bName;
                    try { bName = bundle.name; } catch { continue; }
                    if (string.IsNullOrEmpty(bName)) continue;
                    if (IsVanillaBundle(bundle)) continue;
                    liveBundles[bName] = bundle;
                }

                foreach (string bundleName in bundleNames)
                {
                    if (string.IsNullOrEmpty(bundleName)) continue;

                    // Check if already cached on disk
                    string safeName = MakeSafeFileName(bundleName);
                    string destPath = Path.Combine(BundleCacheDir, safeName + ".bundle");
                    if (File.Exists(destPath)) continue;

                    // Find the live bundle
                    if (!liveBundles.TryGetValue(bundleName, out var liveBundle)) continue;

                    // Try to cache it (uses multiple strategies to find the raw bytes)
                    if (TryCacheRawBundle(liveBundle, null, null))
                        cached++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Error caching required bundles: {ex.Message}");
            }

            if (cached > 0)
                Debug.Log($"[UIBuilderAssetCache] Cached {cached} required bundle file(s) to disk for mod-removal resilience.");
        }

        /// <summary>
        /// Loads only the required asset bundles for a layout override.
        /// For bundles already loaded by their owning mod, indexes sprites from the
        /// LIVE bundle (no need to reload from our cache). For bundles NOT currently
        /// loaded (mod removed), loads from our cached .bundle file on disk.
        /// This replaces the blanket LoadCachedBundles() approach.
        /// </summary>
        public static void LoadRequiredBundles(HashSet<string> requiredBundleNames)
        {
            if (requiredBundleNames == null || requiredBundleNames.Count == 0) return;

            Debug.Log($"[UIBuilderAssetCache] Loading required bundles: {string.Join(", ", requiredBundleNames)}");

            // First pass: index sprites from live bundles already loaded by their mods
            var liveIndexed = IndexLiveModBundles(requiredBundleNames);

            // Second pass: for bundles not found live, try loading from our cached .bundle files
            if (!Directory.Exists(BundleCacheDir)) return;

            int loadedFromCache = 0;
            int indexedFromCache = 0;

            foreach (string bundleName in requiredBundleNames)
            {
                if (liveIndexed.Contains(bundleName)) continue; // Already indexed from live
                if (_loadedBundles.ContainsKey(bundleName)) continue; // Already loaded by us

                string safeName = MakeSafeFileName(bundleName);
                string filePath = Path.Combine(BundleCacheDir, safeName + ".bundle");
                if (!File.Exists(filePath))
                {
                    Debug.LogWarning($"[UIBuilderAssetCache] Required bundle '{bundleName}' not found in cache and not loaded live.");
                    continue;
                }

                try
                {
                    var bundle = AssetBundle.LoadFromFile(filePath);
                    if (bundle == null)
                    {
                        Debug.LogWarning($"[UIBuilderAssetCache] Failed to load cached bundle: {filePath}");
                        continue;
                    }

                    _loadedBundles[safeName] = bundle;
                    loadedFromCache++;
                    indexedFromCache += IndexSpritesFromBundle(bundle, safeName);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UIBuilderAssetCache] Error loading cached bundle '{bundleName}': {ex.Message}");
                }
            }

            if (loadedFromCache > 0 || liveIndexed.Count > 0)
            {
                Debug.Log($"[UIBuilderAssetCache] Required bundles: {liveIndexed.Count} indexed from live mods, " +
                    $"{loadedFromCache} loaded from cache ({indexedFromCache} sprites indexed)");
            }
        }

        /// <summary>
        /// Indexes sprites from asset bundles that are already loaded in memory by their
        /// owning mods. This gives us the REAL sprite objects with full metadata (9-slice,
        /// pivot, PPU, atlas rects) without having to load our cached copy.
        /// Returns the set of bundle names that were successfully indexed.
        /// </summary>
        public static HashSet<string> IndexLiveModBundles(HashSet<string> bundleNames)
        {
            var indexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (bundleNames == null || bundleNames.Count == 0) return indexed;

            try
            {
                var allBundles = AssetBundle.GetAllLoadedAssetBundles();
                if (allBundles == null) return indexed;

                foreach (var bundle in allBundles)
                {
                    if (bundle == null) continue;

                    string bName;
                    try { bName = bundle.name; } catch { continue; }
                    if (string.IsNullOrEmpty(bName)) continue;

                    if (!bundleNames.Contains(bName)) continue;
                    if (IsVanillaBundle(bundle)) continue;
                    try { if (UIBuilderHost.AssetBundle != null && bundle == UIBuilderHost.AssetBundle) continue; } catch { }

                    int count = IndexSpritesFromBundle(bundle, bName);
                    indexed.Add(bName);
                    Debug.Log($"[UIBuilderAssetCache] Indexed {count} sprites from live mod bundle '{bName}'");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Error indexing live mod bundles: {ex.Message}");
            }

            return indexed;
        }

        /// <summary>
        /// Indexes all Sprite and Texture2D assets from a single AssetBundle into
        /// the _bundleSprites dictionary. Returns the number of sprites indexed.
        /// </summary>
        private static int IndexSpritesFromBundle(AssetBundle bundle, string bundleName)
        {
            int indexedSprites = 0;

            Sprite[] sprites = null;
            try { sprites = bundle.LoadAllAssets<Sprite>(); } catch { }
            if (sprites != null)
            {
                for (int s = 0; s < sprites.Length; s++)
                {
                    if (sprites[s] == null || string.IsNullOrEmpty(sprites[s].name)) continue;
                    string spriteName = sprites[s].name;

                    if (!_bundleSprites.ContainsKey(spriteName))
                    {
                        _bundleSprites[spriteName] = sprites[s];
                        indexedSprites++;
                    }

                    if (sprites[s].texture != null && !string.IsNullOrEmpty(sprites[s].texture.name))
                    {
                        string qualifiedName = sprites[s].texture.name + ":" + spriteName;
                        if (!_bundleSprites.ContainsKey(qualifiedName))
                            _bundleSprites[qualifiedName] = sprites[s];
                    }
                }
            }

            Texture2D[] textures = null;
            try { textures = bundle.LoadAllAssets<Texture2D>(); } catch { }
            if (textures != null)
            {
                for (int t = 0; t < textures.Length; t++)
                {
                    if (textures[t] == null || string.IsNullOrEmpty(textures[t].name)) continue;
                    if (textures[t].width <= 0 || textures[t].height <= 0) continue;

                    string texName = textures[t].name;
                    if (!_bundleSprites.ContainsKey(texName))
                    {
                        try
                        {
                            var texSprite = Sprite.Create(textures[t],
                                new Rect(0, 0, textures[t].width, textures[t].height),
                                new Vector2(0.5f, 0.5f), 100f);
                            if (texSprite != null)
                            {
                                texSprite.name = texName;
                                _bundleSprites[texName] = texSprite;
                                indexedSprites++;
                            }
                        }
                        catch { }
                    }
                }
            }

            return indexedSprites;
        }

        /// <summary>
        /// Indexes sprites from ALL currently loaded mod bundles into _bundleSprites.
        /// Used as a fallback when a layout lacks required_bundles metadata.
        /// Unlike LoadCachedBundles(), this never tries to load .bundle files from disk
        /// (which would conflict with already-loaded bundles). It only indexes sprites
        /// from bundles that are already in memory.
        /// Safe to call multiple times — skips bundles already indexed.
        /// </summary>
        public static void IndexAllLiveModBundles()
        {
            try
            {
                var allBundles = AssetBundle.GetAllLoadedAssetBundles();
                if (allBundles == null) return;

                int totalIndexed = 0;
                foreach (var bundle in allBundles)
                {
                    if (bundle == null) continue;

                    string bName;
                    try { bName = bundle.name; } catch { continue; }
                    if (string.IsNullOrEmpty(bName)) continue;
                    if (IsVanillaBundle(bundle)) continue;
                    try { if (UIBuilderHost.AssetBundle != null && bundle == UIBuilderHost.AssetBundle) continue; } catch { }

                    // Skip if we've already indexed this bundle
                    if (_loadedBundles.ContainsKey(bName)) continue;

                    int count = IndexSpritesFromBundle(bundle, bName);
                    if (count > 0)
                        totalIndexed += count;
                }

                if (totalIndexed > 0)
                    Debug.Log($"[UIBuilderAssetCache] Indexed {totalIndexed} sprites from live mod bundles (no-disk fallback)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Error indexing all live mod bundles: {ex.Message}");
            }
        }

        /// <summary>
        /// Unloads all cached asset bundles. Call during cleanup/shutdown.
        /// </summary>
        public static void UnloadCachedBundles()
        {
            foreach (var kvp in _loadedBundles)
            {
                try { if (kvp.Value != null) kvp.Value.Unload(true); } catch { }
            }
            _loadedBundles.Clear();
            _bundleSprites.Clear();
            _bundleSpritesLoaded = false;
        }

        // ???????????????????????????????????????
        //  Proactive mod bundle scanning (chunked / coroutine-safe)
        // ???????????????????????????????????????

        private static bool _modBundlesScanned;

        /// <summary>True while the background coroutine scan is still running.</summary>
        private static bool _scanInProgress;

        /// <summary>True once a full scan has finished (sprites + textures + bundles).</summary>
        public static bool IsScanComplete => _modBundlesScanned && !_scanInProgress;

        /// <summary>Descriptive label of what the scanner is currently doing (for UI display).</summary>
        public static string ScanStatusLabel { get; private set; } = "";

        /// <summary>Rough 0-1 progress of the current scan (for progress bars).</summary>
        public static float ScanProgress { get; private set; }

        /// <summary>
        /// Maximum wall-clock milliseconds the scanner is allowed to spend per frame
        /// before yielding. 4 ms keeps us well under the 16 ms budget for 60 fps and
        /// leaves plenty of headroom for the game's own work. The previous 8 ms budget
        /// was too generous — individual sprite cache operations (GPU blit + ReadPixels +
        /// file I/O) frequently exceed 8 ms each, causing frame spikes that accumulate
        /// into server heartbeat timeouts and disconnects.
        /// </summary>
        private const float ScanFrameBudgetMs = 8f;

        /// <summary>
        /// Callbacks that fire once when the current scan completes.
        /// Cleared after invocation.
        /// </summary>
        private static readonly List<Action> _onScanComplete = new List<Action>();

        /// <summary>
        /// Register a one-shot callback that fires when the current (or next) scan finishes.
        /// If no scan is in progress and one has already completed, fires immediately.
        /// </summary>
        public static void OnScanComplete(Action callback)
        {
            if (callback == null) return;
            if (IsScanComplete)
            {
                callback();
                return;
            }
            _onScanComplete.Add(callback);
        }

        /// <summary>
        /// Known Valheim asset bundle name patterns. These are the game's own bundles
        /// that ship in valheim_Data/ — we never need to cache assets from these because
        /// they're always available at runtime.
        /// </summary>
        private static readonly string[] VanillaBundlePatterns =
        {
            "valheim", "game", "shaders", "fonts", "scenes",
            "gui", "music", "sfx", "sound", "vfx", "fx",
            "lux", "characters", "environment", "locations",
            "vegetation", "ships", "buildpieces", "weapons",
            "armor", "items", "creatures", "dungeons",
            "meadows", "blackforest", "swamp", "mountain",
            "plains", "ocean", "mistlands", "ashlands",
            "deepnorth", "hildir", "ashlands",
            "unity builtin", "unity_builtin", "resources.assets",
            "globalgamemanagers", "sharedassets", "level"
        };

        /// <summary>
        /// Checks if an asset bundle is from the vanilla game (Valheim / Unity).
        /// During bulk scans we skip these entirely since game assets are always available at runtime.
        /// </summary>
        private static bool IsVanillaBundle(AssetBundle bundle)
        {
            if (bundle == null) return true;

            string bundleName;
            try { bundleName = bundle.name; } catch { return true; }
            if (string.IsNullOrEmpty(bundleName)) return true;

            string nameLower = bundleName.ToLowerInvariant();

            // Check against known vanilla/Unity bundle name patterns
            for (int i = 0; i < VanillaBundlePatterns.Length; i++)
            {
                if (nameLower.Contains(VanillaBundlePatterns[i]))
                    return true;
            }

            // Bundles loaded from the game's data directory are vanilla
            // (AssetBundle.name sometimes contains the load path)
            if (nameLower.Contains("valheim_data") || nameLower.Contains("streamingassets"))
                return true;

            return false;
        }

        /// <summary>
        /// Lightweight check for Unity built-in asset names that are always available.
        /// Unlike IsSpriteFromMod (which is aggressive for the capture path), this is
        /// used during bulk scans where we already know the source is a mod bundle.
        /// </summary>
        private static bool IsUnityBuiltinAssetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return name == "UISprite" || name == "Background" || name == "InputFieldBackground" ||
                   name == "Checkmark" || name == "Knob" || name == "UIMask" ||
                   name == "UISpriteLegacy" || name == "DropdownArrow" || name == "UnitySplash";
        }

        /// <summary>
        /// Proactively scans ALL loaded asset bundles from other mods, extracts every
        /// Sprite and Texture2D asset from them, and caches them to our disk cache.
        /// Also scans BepInEx plugins and config directories for loose PNG files
        /// (e.g., Minimal UI's MUI_*.png pattern) and caches those too.
        ///
        /// Only caches MODDED assets — vanilla game bundles and Unity built-in assets
        /// are skipped since they're always available at runtime.
        ///
        /// This is the key method that "steals" assets from other mods so our layouts
        /// work even after those mods are removed. Call this during capture or before
        /// applying overrides.
        ///
        /// Safe to call multiple times — only runs the full scan once per session
        /// unless force is true.
        ///
        /// This is a lightweight kick-off method. The actual heavy scanning runs as a
        /// coroutine over many frames via <see cref="ScanAndCacheAllModBundlesCoroutine"/>.
        /// If no MonoBehaviour host is available (e.g. during unit tests) the scan runs
        /// synchronously on the current frame as a fallback.
        /// </summary>
        public static void ScanAndCacheAllModBundles(bool force = false)
        {
            if (_modBundlesScanned && !force) return;
            if (_scanInProgress) return; // Already running

            _modBundlesScanned = true;

            // Try to run as a coroutine on an existing MonoBehaviour host
            MonoBehaviour host = null;
            if (UIBuilderHost.RootObject != null)
                host = UIBuilderHost.RootObject.GetComponent<UIVanillaOverrideUpdater>();
            if (host == null && UIBuilderHost.CoroutineHost != null)
                host = UIBuilderHost.CoroutineHost;

            if (host != null && host.gameObject.activeInHierarchy)
            {
                host.StartCoroutine(ScanAndCacheAllModBundlesCoroutine());
            }
            else
            {
                // Fallback: run synchronously (step the enumerator without yielding)
                Debug.LogWarning("[UIBuilderAssetCache] No MonoBehaviour host available — running scan synchronously.");
                var enumerator = ScanAndCacheAllModBundlesCoroutine();
                while (enumerator.MoveNext()) { }
            }
        }

        /// <summary>
        /// Coroutine that performs the full mod asset scan over many frames.
        /// Yields periodically based on <see cref="ScanFrameBudgetMs"/> to keep
        /// frame times short and prevent server heartbeat timeouts.
        ///
        /// Phases:
        ///   1. Scan loaded AssetBundles for sprites and textures
        ///   2. Scan BepInEx plugin fields for sprites and bundles
        ///   3. Scan loose PNG files in plugins/config directories
        ///   4. Save raw .bundle files for later embedding
        ///
        /// Each phase yields between individual bundles/plugins/directories, and
        /// within large asset arrays if the per-frame budget is exceeded.
        /// </summary>
        public static System.Collections.IEnumerator ScanAndCacheAllModBundlesCoroutine()
        {
            if (_scanInProgress) yield break;
            _scanInProgress = true;
            ScanProgress = 0f;
            ScanStatusLabel = "Starting mod asset scan…";

            int cachedSprites = 0;
            int cachedTextures = 0;
            int cachedFiles = 0;
            int cachedBundles = 0;

            Debug.Log("[UIBuilderAssetCache] === Starting chunked mod asset scan ===");

            // NOTE: We do NOT call LoadCachedBundles() here. That method synchronously
            // loads ALL .bundle files from disk + calls LoadAllAssets<Sprite/Texture2D>()
            // on each one, which is extremely expensive and causes multi-second freezes
            // that trigger server heartbeat timeouts. Cached bundles are loaded on-demand
            // by LoadRequiredBundles() when an override is actually applied.

            // Collect the list of mod bundles up-front (cheap) so we can calculate progress
            var modBundles = new List<AssetBundle>();
            try
            {
                var allBundles = AssetBundle.GetAllLoadedAssetBundles();
                if (allBundles != null)
                {
                    foreach (var bundle in allBundles)
                    {
                        if (bundle == null) continue;
                        try { if (UIBuilderHost.AssetBundle != null && bundle == UIBuilderHost.AssetBundle) continue; } catch { }
                        if (IsVanillaBundle(bundle)) continue;
                        modBundles.Add(bundle);
                    }
                }
            }
            catch { }

            // Collect plugin infos
            var pluginList = new List<KeyValuePair<string, BepInEx.PluginInfo>>();
            try
            {
                if (Chainloader.PluginInfos != null)
                {
                    foreach (var kvp in Chainloader.PluginInfos)
                    {
                        if (kvp.Value == null || kvp.Value.Instance == null) continue;
                        Type pt = kvp.Value.Instance.GetType();
                        string ns = pt.Namespace ?? "";
                        if (ns.StartsWith("VerdantsAscent", StringComparison.OrdinalIgnoreCase)) continue;
                        pluginList.Add(kvp);
                    }
                }
            }
            catch { }

            // Total work units for progress (rough)
            int totalUnits = modBundles.Count + pluginList.Count + 2 /* loose files + raw bundles */;
            int completedUnits = 0;

            var sw = new System.Diagnostics.Stopwatch();

            // ??? Phase 1: Index sprites from loaded AssetBundles (memory only, no PNG extraction) ???
            // We do NOT call CacheSprite() here — that triggers Graphics.Blit + ReadPixels +
            // File.WriteAllBytes for EVERY sprite, which is extremely expensive and causes
            // server disconnects. Instead, we just index sprites into _bundleSprites for fast
            // lookup. The raw .bundle files are cached to disk in Phase 4, which is the
            // authoritative sprite source. PNG extraction only happens during explicit capture
            // operations (when the user captures a UI in the editor).
            for (int b = 0; b < modBundles.Count; b++)
            {
                var bundle = modBundles[b];
                if (bundle == null) { completedUnits++; continue; }

                string bundleName = "unknown";
                try { bundleName = bundle.name ?? "unnamed"; } catch { }
                ScanStatusLabel = $"Phase 1/{4}: Indexing bundle '{bundleName}' ({b + 1}/{modBundles.Count})";
                ScanProgress = (float)completedUnits / totalUnits;

                sw.Restart();

                try
                {
                    int indexed = IndexSpritesFromBundle(bundle, bundleName);
                    cachedSprites += indexed;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UIBuilderAssetCache] Index error in '{bundleName}': {ex.Message}");
                }

                completedUnits++;
                Debug.Log($"[UIBuilderAssetCache] Indexed mod bundle '{bundleName}'");
                yield return null; // Always yield between bundles
                sw.Restart();
            }

            // ??? Phase 2: Scan BepInEx plugin fields ???
            for (int p = 0; p < pluginList.Count; p++)
            {
                var kvp = pluginList[p];
                var pluginInfo = kvp.Value;
                Type pluginType = pluginInfo.Instance.GetType();

                ScanStatusLabel = $"Phase 2/{4}: Scanning plugin '{kvp.Key}' ({p + 1}/{pluginList.Count})";
                ScanProgress = (float)completedUnits / totalUnits;
                sw.Restart();

                // Load field arrays inside try-catch, iterate outside
                FieldInfo[] staticFields = null;
                FieldInfo[] instanceFields = null;
                try { staticFields = pluginType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); } catch { }
                try { instanceFields = pluginType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); } catch { }

                if (staticFields != null)
                {
                    for (int f = 0; f < staticFields.Length; f++)
                    {
                        try
                        {
                            if (staticFields[f].FieldType == typeof(Sprite))
                            {
                                var sprite = staticFields[f].GetValue(null) as Sprite;
                                if (sprite != null && !string.IsNullOrEmpty(sprite.name) && !IsUnityBuiltinAssetName(sprite.name))
                                {
                                    // Index the sprite for fast lookup. Only cache to PNG if
                                    // it's NOT already in our bundle index (bundle sprites are
                                    // preserved via raw .bundle caching in Phase 4 — far cheaper
                                    // than per-sprite GPU blit + ReadPixels + disk write).
                                    if (!_bundleSprites.ContainsKey(sprite.name))
                                    {
                                        _bundleSprites[sprite.name] = sprite;
                                        cachedTextures++;
                                    }
                                }
                            }
                            else if (staticFields[f].FieldType == typeof(AssetBundle))
                            {
                                var bundle = staticFields[f].GetValue(null) as AssetBundle;
                                if (bundle != null && !IsVanillaBundle(bundle))
                                    cachedSprites += CacheBundleSpritesBatch(bundle, ref cachedTextures, sw);
                            }
                        }
                        catch { }

                        if (sw.Elapsed.TotalMilliseconds >= ScanFrameBudgetMs)
                        {
                            yield return null;
                            sw.Restart();
                        }
                    }
                }

                if (instanceFields != null)
                {
                    for (int f = 0; f < instanceFields.Length; f++)
                    {
                        if (instanceFields[f].FieldType != typeof(AssetBundle)) continue;
                        try
                        {
                            var bundle = instanceFields[f].GetValue(pluginInfo.Instance) as AssetBundle;
                            if (bundle != null && !IsVanillaBundle(bundle))
                                cachedSprites += CacheBundleSpritesBatch(bundle, ref cachedTextures, sw);
                        }
                        catch { }

                        if (sw.Elapsed.TotalMilliseconds >= ScanFrameBudgetMs)
                        {
                            yield return null;
                            sw.Restart();
                        }
                    }
                }

                completedUnits++;
                yield return null; // Yield between plugins
                sw.Restart();
            }

            // ??? Phase 3: Loose PNG files ???
            ScanStatusLabel = "Phase 3/4: Scanning loose image files…";
            ScanProgress = (float)completedUnits / totalUnits;

            try { cachedFiles += ScanDirectoryForModImages(Paths.PluginPath); } catch { }
            yield return null;
            try { cachedFiles += ScanDirectoryForModImages(Paths.ConfigPath); } catch { }
            yield return null;
            completedUnits++;

            // ??? Phase 4: Raw .bundle files ???
            ScanStatusLabel = "Phase 4/4: Caching raw bundle files…";
            ScanProgress = (float)completedUnits / totalUnits;
            sw.Restart();

            // Build a mapping of AssetBundle instance ? owning plugin assembly
            // so TryCacheRawBundle can try embedded resource extraction
            var bundleToAssembly = new Dictionary<AssetBundle, Assembly>();
            for (int p2 = 0; p2 < pluginList.Count; p2++)
            {
                var pkvp = pluginList[p2];
                if (pkvp.Value?.Instance == null) continue;
                Type pt = pkvp.Value.Instance.GetType();
                FieldInfo[] pFields = null;
                try
                {
                    var fl = new List<FieldInfo>();
                    fl.AddRange(pt.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
                    fl.AddRange(pt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                    pFields = fl.ToArray();
                }
                catch { }

                if (pFields != null)
                {
                    for (int fi2 = 0; fi2 < pFields.Length; fi2++)
                    {
                        if (pFields[fi2].FieldType != typeof(AssetBundle)) continue;
                        try
                        {
                            object tgt = pFields[fi2].IsStatic ? null : pkvp.Value.Instance;
                            var bndl = pFields[fi2].GetValue(tgt) as AssetBundle;
                            if (bndl != null && !bundleToAssembly.ContainsKey(bndl))
                                bundleToAssembly[bndl] = pt.Assembly;
                        }
                        catch { }
                    }
                }
            }

            for (int b = 0; b < modBundles.Count; b++)
            {
                var bundle = modBundles[b];
                if (bundle == null) continue;
                bool isScene = false;
                try { isScene = bundle.isStreamedSceneAssetBundle; } catch { }
                if (isScene) continue;

                // Look up the owning assembly for this bundle
                Assembly bundleAsm = null;
                bundleToAssembly.TryGetValue(bundle, out bundleAsm);

                try { if (TryCacheRawBundle(bundle, bundleAsm, null)) cachedBundles++; } catch { }

                if (sw.Elapsed.TotalMilliseconds >= ScanFrameBudgetMs)
                {
                    yield return null;
                    sw.Restart();
                }
            }

            yield return null;
            sw.Restart();

            // Plugin-held bundles (may be embedded resources)
            for (int p = 0; p < pluginList.Count; p++)
            {
                var kvp = pluginList[p];
                var pluginInfo = kvp.Value;
                Type pluginType = pluginInfo.Instance.GetType();

                FieldInfo[] allPluginFields = null;
                try
                {
                    var fl = new List<FieldInfo>();
                    fl.AddRange(pluginType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
                    fl.AddRange(pluginType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                    allPluginFields = fl.ToArray();
                }
                catch { }

                if (allPluginFields != null)
                {
                    for (int fi = 0; fi < allPluginFields.Length; fi++)
                    {
                        if (allPluginFields[fi].FieldType != typeof(AssetBundle)) continue;
                        try
                        {
                            object target = allPluginFields[fi].IsStatic ? null : pluginInfo.Instance;
                            var bundle = allPluginFields[fi].GetValue(target) as AssetBundle;
                            if (bundle == null) continue;
                            if (IsVanillaBundle(bundle)) continue;
                            bool isScene = false;
                            try { isScene = bundle.isStreamedSceneAssetBundle; } catch { }
                            if (isScene) continue;

                            if (TryCacheRawBundle(bundle, pluginType.Assembly, kvp.Key))
                                cachedBundles++;
                        }
                        catch { }
                    }
                }

                if (sw.Elapsed.TotalMilliseconds >= ScanFrameBudgetMs)
                {
                    yield return null;
                    sw.Restart();
                }
            }
            completedUnits++;

            // NOTE: We do NOT call LoadCachedBundles() here. It synchronously loads ALL
            // .bundle files which causes multi-second freezes. The newly cached .bundle
            // files will be loaded on-demand by LoadRequiredBundles() when needed.
            if (cachedBundles > 0)
            {
                Debug.Log($"[UIBuilderAssetCache] {cachedBundles} raw bundle(s) cached to disk. " +
                    "They will be loaded on-demand when overrides need them.");
            }

            // ??? Done ???
            _scanInProgress = false;
            ScanProgress = 1f;
            ScanStatusLabel = "Scan complete";
            Debug.Log($"[UIBuilderAssetCache] === Chunked mod asset scan complete (vanilla assets skipped): " +
                $"{cachedSprites} sprites indexed, {cachedTextures} plugin sprites, {cachedFiles} loose files, {cachedBundles} raw bundles cached ===");

            // Fire completion callbacks
            for (int i = 0; i < _onScanComplete.Count; i++)
            {
                try { _onScanComplete[i]?.Invoke(); } catch (Exception ex)
                {
                    Debug.LogWarning($"[UIBuilderAssetCache] Scan completion callback error: {ex.Message}");
                }
            }
            _onScanComplete.Clear();
        }

        /// <summary>
        /// Helper: indexes sprites from a bundle WITHOUT caching them to PNG.
        /// Used by Phase 2 when scanning plugin fields. Only adds sprites to the
        /// in-memory _bundleSprites index for fast lookup during override application.
        /// The actual PNG caching happens lazily during capture (CacheSprite) or
        /// via the Phase 1 per-bundle scan which properly yields between sprites.
        ///
        /// Previously this method called CacheSprite() for every sprite which
        /// triggered GPU blit + ReadPixels + File.WriteAllBytes for each one —
        /// an extremely expensive operation that caused server disconnects.
        /// Returns the number of sprites indexed.
        /// </summary>
        private static int CacheBundleSpritesBatch(AssetBundle bundle, ref int cachedTextures, System.Diagnostics.Stopwatch sw)
        {
            int indexed = 0;
            try
            {
                // Just index sprites into _bundleSprites for fast lookup.
                // Don't extract to PNG here — that's done during capture or Phase 1.
                indexed = IndexSpritesFromBundle(bundle, bundle.name ?? "unknown");
            }
            catch { }
            return indexed;
        }

        /// <summary>
        /// Scans a directory recursively for PNG image files that look like mod UI assets
        /// and caches them. Targets patterns like MUI_*.png and other common mod image names.
        /// Skips vanilla game directories and our own cache directories.
        /// Returns the number of newly cached files.
        /// </summary>
        private static int ScanDirectoryForModImages(string directory)
        {
            int cached = 0;
            if (!Directory.Exists(directory)) return 0;

            try
            {
                // Search for common mod UI image patterns
                string[] patterns = { "MUI_*.png", "*_icon*.png", "*_bkg*.png", "*_panel*.png",
                                      "*_bar*.png", "*_background*.png", "*UI*.png" };
                var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var pattern in patterns)
                {
                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);
                    }
                    catch { continue; }

                    for (int i = 0; i < files.Length; i++)
                    {
                        string filePath = files[i];
                        if (!seenFiles.Add(filePath)) continue;

                        // Skip files in our own cache directory
                        if (filePath.IndexOf("CapturedAssets", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        // Skip files in our own UIAssets/Sprites directory
                        if (filePath.IndexOf("UIAssets", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            filePath.IndexOf(UIBuilderHost.ModName, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        // Skip files in the vanilla game data directory
                        if (filePath.IndexOf("valheim_Data", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                        string fileName = Path.GetFileNameWithoutExtension(filePath);
                        if (string.IsNullOrEmpty(fileName)) continue;

                        // Check if already cached
                        EnsureManifest();
                        bool alreadyCached = false;
                        if (_manifest != null && _manifest.Entries != null)
                        {
                            for (int m = 0; m < _manifest.Entries.Count; m++)
                            {
                                if (string.Equals(_manifest.Entries[m].OriginalName, fileName, StringComparison.OrdinalIgnoreCase))
                                {
                                    string cachedPath = Path.Combine(CacheDir, _manifest.Entries[m].CachedFile);
                                    if (File.Exists(cachedPath))
                                    {
                                        alreadyCached = true;
                                        break;
                                    }
                                }
                            }
                        }
                        if (alreadyCached) continue;

                        try
                        {
                            byte[] fileData = File.ReadAllBytes(filePath);
                            if (fileData == null || fileData.Length == 0) continue;

                            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                            bool loaded = LoadImageIntoTexture(tex, fileData);
                            if (!loaded || tex.width <= 1 || tex.height <= 1)
                            {
                                UnityEngine.Object.Destroy(tex);
                                continue;
                            }

                            // Save to our cache
                            EnsureCacheDir();
                            string safeName = MakeSafeFileName(fileName);
                            string destPath = Path.Combine(CacheDir, safeName + ".png");

                            int counter = 1;
                            while (File.Exists(destPath))
                            {
                                safeName = MakeSafeFileName(fileName) + "_" + counter;
                                destPath = Path.Combine(CacheDir, safeName + ".png");
                                counter++;
                            }

                            File.WriteAllBytes(destPath, fileData);

                            var entry = new CacheEntry
                            {
                                OriginalName = fileName,
                                CachedFile = safeName + ".png",
                                SourceAssembly = "loose_file",
                                TextureWidth = tex.width,
                                TextureHeight = tex.height,
                                CapturedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                PivotX = 0.5f,
                                PivotY = 0.5f,
                                PixelsPerUnit = 100f
                            };
                            _manifest.Entries.Add(entry);
                            cached++;

                            UnityEngine.Object.Destroy(tex);
                        }
                        catch { /* individual file read may fail */ }
                    }
                }

                if (cached > 0)
                    SaveManifest();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Error scanning directory '{directory}': {ex.Message}");
            }

            return cached;
        }

        // ???????????????????????????????????????
        //  Raw asset bundle caching
        // ???????????????????????????????????????

        /// <summary>
        /// Attempts to save a raw copy of the given AssetBundle to the bundles cache directory.
        /// The file is saved as {safeBundleName}.bundle and can be embedded as a resource later.
        /// Tries multiple strategies to locate the source bytes:
        ///   1. Find the file on disk in BepInEx/plugins by exact bundle name match
        ///   2. Extract from the plugin assembly's embedded resources
        ///   3. Find via plugin GUID -> assembly lookup
        ///   4. Scan ALL plugin assemblies for embedded UnityFS resources
        ///   5. Scan all UnityFS files on disk and match by reading the internal bundle name from the file header
        /// Returns true if the bundle was newly cached.
        /// </summary>
        private static bool TryCacheRawBundle(AssetBundle bundle, Assembly pluginAssembly, string pluginGuid)
        {
            if (bundle == null) return false;

            string bundleName;
            try { bundleName = bundle.name; } catch { return false; }
            if (string.IsNullOrEmpty(bundleName)) return false;

            // Skip if already cached this session
            if (!_cachedBundleNames.Add(bundleName)) return false;

            string safeName = MakeSafeFileName(bundleName);
            string destPath = Path.Combine(BundleCacheDir, safeName + ".bundle");
            if (File.Exists(destPath))
            {
                // Raw file exists but manifest might be missing (pre-manifest cache).
                // Create the manifest if it doesn't exist yet.
                if (UIBundleManifestManager.GetManifest(bundleName) == null)
                {
                    try
                    {
                        var fi = new FileInfo(destPath);
                        UIBundleManifestManager.CreateAndSaveManifest(bundle, safeName + ".bundle",
                            pluginGuid ?? "", pluginAssembly?.GetName()?.Name ?? "", false, fi.Length);
                    }
                    catch { }
                }
                return false; // Already on disk from a previous session
            }

            byte[] rawBytes = null;
            bool fromEmbedded = false;

            // Strategy 1: Find the bundle file on disk in BepInEx/plugins by exact name
            rawBytes = FindBundleFileOnDisk(bundleName);

            // Strategy 2: Extract from the plugin assembly's embedded resources
            if (rawBytes == null && pluginAssembly != null)
            {
                rawBytes = ExtractBundleFromEmbeddedResources(pluginAssembly, bundleName);
                if (rawBytes != null) fromEmbedded = true;
            }

            // Strategy 3: If we have a plugin GUID, try to find the assembly from Chainloader
            if (rawBytes == null && !string.IsNullOrEmpty(pluginGuid) && pluginAssembly == null)
            {
                try
                {
                    if (Chainloader.PluginInfos.TryGetValue(pluginGuid, out var info) &&
                        info?.Instance != null)
                    {
                        rawBytes = ExtractBundleFromEmbeddedResources(
                            info.Instance.GetType().Assembly, bundleName);
                        if (rawBytes != null) fromEmbedded = true;
                    }
                }
                catch { }
            }

            // Strategy 4: Scan ALL plugin assemblies for an embedded resource with the UnityFS header
            if (rawBytes == null)
            {
                try
                {
                    if (Chainloader.PluginInfos != null)
                    {
                        foreach (var kvpScan in Chainloader.PluginInfos)
                        {
                            if (kvpScan.Value?.Instance == null) continue;
                            Type pt = kvpScan.Value.Instance.GetType();
                            string ns = pt.Namespace ?? "";
                            if (ns.StartsWith("VerdantsAscent", StringComparison.OrdinalIgnoreCase)) continue;

                            rawBytes = ExtractBundleFromEmbeddedResources(pt.Assembly, bundleName);
                            if (rawBytes != null)
                            {
                                fromEmbedded = true;
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            // Strategy 5: Scan all UnityFS files on disk and match by reading the internal
            // bundle name from each file's header. This is the most reliable strategy because
            // AssetBundle.name matches the internal name regardless of the on-disk filename.
            if (rawBytes == null)
            {
                rawBytes = FindBundleByInternalNameScan(bundleName);
            }

            if (rawBytes == null || rawBytes.Length == 0)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Could not locate raw bundle file for '{bundleName}'");
                return false;
            }

            try
            {
                if (!Directory.Exists(BundleCacheDir))
                    Directory.CreateDirectory(BundleCacheDir);
                File.WriteAllBytes(destPath, rawBytes);
                Debug.Log($"[UIBuilderAssetCache] Cached raw bundle '{bundleName}' -> {safeName}.bundle ({rawBytes.Length:N0} bytes)");

                // Create a per-bundle manifest with full asset inventory
                try
                {
                    UIBundleManifestManager.CreateAndSaveManifest(bundle, safeName + ".bundle",
                        pluginGuid ?? "", pluginAssembly?.GetName()?.Name ?? "",
                        fromEmbedded, rawBytes.Length);
                }
                catch (Exception mex)
                {
                    Debug.LogWarning($"[UIBuilderAssetCache] Failed to create manifest for '{bundleName}': {mex.Message}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Failed to write raw bundle '{bundleName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Searches the BepInEx plugins directory (and subdirectories) for a file matching
        /// the given bundle name. Checks common extensions and also extensionless files.
        /// Validates the file starts with the UnityFS magic header AND verifies the
        /// internal bundle name matches before returning.
        /// 
        /// Uses targeted filename searches instead of a wildcard scan of all files.
        /// </summary>
        private static byte[] FindBundleFileOnDisk(string bundleName)
        {
            if (string.IsNullOrEmpty(bundleName)) return null;

            string pluginsDir = Paths.PluginPath;
            if (!Directory.Exists(pluginsDir)) return null;

            // Search for specific filenames instead of scanning every file with "*"
            string[] searchPatterns = {
                bundleName,
                bundleName + ".bundle",
                bundleName + ".assets",
                bundleName + ".resource"
            };

            try
            {
                foreach (var pattern in searchPatterns)
                {
                    string[] matchingFiles;
                    try { matchingFiles = Directory.GetFiles(pluginsDir, pattern, SearchOption.AllDirectories); }
                    catch { continue; }

                    for (int i = 0; i < matchingFiles.Length; i++)
                    {
                        try
                        {
                            var fi = new FileInfo(matchingFiles[i]);
                            if (fi.Length < 64) continue;
                        }
                        catch { continue; }

                        byte[] bytes = File.ReadAllBytes(matchingFiles[i]);
                        if (!IsUnityFSBundle(bytes)) continue;

                        // Verify the internal name matches to avoid false positives
                        string internalName = ReadInternalBundleName(bytes);
                        if (internalName != null &&
                            string.Equals(internalName, bundleName, StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.Log($"[UIBuilderAssetCache] Found raw bundle on disk: {matchingFiles[i]} " +
                                $"(internal name: '{internalName}')");
                            return bytes;
                        }

                        // If internal name doesn't match but filename matches exactly,
                        // still return it — the file was explicitly named for this bundle
                        string fileName = Path.GetFileName(matchingFiles[i]);
                        if (string.Equals(fileName, bundleName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fileName, bundleName + ".bundle", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.Log($"[UIBuilderAssetCache] Found raw bundle on disk by exact filename: {matchingFiles[i]} " +
                                $"(internal name: '{internalName ?? "unknown"}')");
                            return bytes;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Error searching plugins dir for bundle '{bundleName}': {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Extracts a raw asset bundle from an assembly's embedded resources.
        /// Searches for resources whose name contains the bundle name, then validates
        /// the UnityFS magic header. The fallback scan also verifies the internal
        /// bundle name matches to avoid returning the wrong bundle.
        /// </summary>
        private static byte[] ExtractBundleFromEmbeddedResources(Assembly assembly, string bundleName)
        {
            if (assembly == null || string.IsNullOrEmpty(bundleName)) return null;

            try
            {
                string[] resourceNames = assembly.GetManifestResourceNames();
                if (resourceNames == null || resourceNames.Length == 0) return null;

                string bundleNameLower = bundleName.ToLowerInvariant();

                // First pass: match by embedded resource name
                for (int i = 0; i < resourceNames.Length; i++)
                {
                    string resName = resourceNames[i];
                    string resNameLower = resName.ToLowerInvariant();

                    // Match: resource name ends with the bundle name (namespace prefix is common)
                    if (!resNameLower.EndsWith(bundleNameLower) &&
                        !resNameLower.EndsWith(bundleNameLower + ".bundle") &&
                        !resNameLower.EndsWith(bundleNameLower + ".assets"))
                        continue;

                    using (var stream = assembly.GetManifestResourceStream(resName))
                    {
                        if (stream == null || stream.Length == 0) continue;

                        byte[] bytes = new byte[stream.Length];
                        stream.Read(bytes, 0, bytes.Length);

                        if (IsUnityFSBundle(bytes))
                        {
                            Debug.Log($"[UIBuilderAssetCache] Extracted raw bundle from embedded resource: {resName} ({bytes.Length:N0} bytes)");
                            return bytes;
                        }
                    }
                }

                // Second pass: check ALL embedded resources for the UnityFS header,
                // but VERIFY the internal bundle name matches to avoid returning
                // the wrong mod's bundle.
                for (int i = 0; i < resourceNames.Length; i++)
                {
                    try
                    {
                        using (var stream = assembly.GetManifestResourceStream(resourceNames[i]))
                        {
                            if (stream == null || stream.Length < 64) continue;

                            // Quick header check without reading the full resource
                            byte[] header = new byte[7];
                            stream.Read(header, 0, 7);
                            if (header[0] != 'U' || header[1] != 'n' || header[2] != 'i' ||
                                header[3] != 't' || header[4] != 'y' || header[5] != 'F' || header[6] != 'S')
                                continue;

                            // Read the full resource and verify the internal name
                            stream.Position = 0;
                            byte[] bytes = new byte[stream.Length];
                            stream.Read(bytes, 0, bytes.Length);

                            string internalName = ReadInternalBundleName(bytes);
                            if (internalName != null &&
                                string.Equals(internalName, bundleName, StringComparison.OrdinalIgnoreCase))
                            {
                                Debug.Log($"[UIBuilderAssetCache] Extracted raw bundle from embedded resource " +
                                    $"(internal name match '{internalName}'): {resourceNames[i]} ({bytes.Length:N0} bytes)");
                                return bytes;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Error extracting bundle from assembly '{assembly.GetName().Name}': {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Checks if a byte array starts with the UnityFS magic header ("UnityFS").
        /// </summary>
        private static bool IsUnityFSBundle(byte[] data)
        {
            if (data == null || data.Length < 7) return false;
            return data[0] == 'U' && data[1] == 'n' && data[2] == 'i' &&
                   data[3] == 't' && data[4] == 'y' && data[5] == 'F' && data[6] == 'S';
        }

        /// <summary>
        /// Reads the internal bundle name from a UnityFS byte array in memory.
        /// Used to verify that an embedded resource actually contains the bundle we want
        /// (prevents false positives where the wrong mod's bundle is returned).
        /// </summary>
        private static string ReadInternalBundleName(byte[] data)
        {
            if (data == null || data.Length < 64) return null;
            if (!IsUnityFSBundle(data)) return null;

            try
            {
                int pos = 8; // skip "UnityFS\0" magic

                // Skip format version (4 bytes big-endian int32)
                if (pos + 4 > data.Length) return null;
                pos += 4;

                // Skip first null-terminated string (player version)
                while (pos < data.Length && data[pos] != 0) pos++;
                if (pos >= data.Length) return null;
                pos++; // skip null terminator

                // Skip second null-terminated string (engine version)
                while (pos < data.Length && data[pos] != 0) pos++;
                if (pos >= data.Length) return null;
                pos++; // skip null terminator

                // Scan remaining data for the internal name
                int remaining = Math.Min(data.Length - pos, 65536);
                if (remaining < 20) return null;

                byte[] buffer = new byte[remaining];
                Array.Copy(data, pos, buffer, 0, remaining);

                var candidateNames = ExtractNullTerminatedStrings(buffer, remaining, 2, 128);

                for (int c = 0; c < candidateNames.Count; c++)
                {
                    string candidate = candidateNames[c];
                    if (candidate.StartsWith("CAB-", StringComparison.Ordinal)) continue;
                    if (candidate.StartsWith("archive:/", StringComparison.Ordinal)) continue;
                    if (candidate.StartsWith("library/", StringComparison.OrdinalIgnoreCase)) continue;
                    bool allDigits = true;
                    for (int ch = 0; ch < candidate.Length; ch++)
                    {
                        if (!char.IsDigit(candidate[ch]) && candidate[ch] != '.') { allDigits = false; break; }
                    }
                    if (allDigits) continue;
                    if (candidate.Contains(" ")) continue;

                    return candidate;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Scans the BepInEx plugins directory for UnityFS files and matches them to the
        /// given bundle name by reading the internal bundle name from each file's header.
        /// This is the most reliable strategy because the internal name stored in the
        /// UnityFS header always matches AssetBundle.name, regardless of what the file
        /// on disk is named (e.g., file "MoreTwoHanders" contains internal name "customassets").
        ///
        /// WARNING: This method scans EVERY file in the plugins directory tree and reads
        /// file headers. It should only be called as a last-resort fallback, never during
        /// startup or early loading. The scan is bounded to common bundle extensions to
        /// minimize I/O impact.
        /// </summary>
        private static byte[] FindBundleByInternalNameScan(string bundleName)
        {
            if (string.IsNullOrEmpty(bundleName)) return null;

            string pluginsDir = Paths.PluginPath;
            if (!Directory.Exists(pluginsDir)) return null;

            try
            {
                // Only scan files with bundle-like extensions instead of ALL files.
                // This dramatically reduces I/O — a typical plugins folder has thousands
                // of .dll, .cfg, .md, .png files that can never be bundles.
                var bundleExtensions = new[] { "*.bundle", "*.assets", "*.resource" };
                var candidateFiles = new List<string>();
                foreach (var pattern in bundleExtensions)
                {
                    try
                    {
                        candidateFiles.AddRange(Directory.GetFiles(pluginsDir, pattern, SearchOption.AllDirectories));
                    }
                    catch { }
                }

                // Also check extensionless files, but only in immediate subdirectories
                // (most mod bundles are at plugins/ModName/bundlefile depth)
                try
                {
                    foreach (var subDir in Directory.GetDirectories(pluginsDir))
                    {
                        try
                        {
                            foreach (var file in Directory.GetFiles(subDir))
                            {
                                if (string.IsNullOrEmpty(Path.GetExtension(file)))
                                    candidateFiles.Add(file);
                            }
                        }
                        catch { }
                    }
                    // Also check root plugins dir for extensionless files
                    foreach (var file in Directory.GetFiles(pluginsDir))
                    {
                        if (string.IsNullOrEmpty(Path.GetExtension(file)))
                            candidateFiles.Add(file);
                    }
                }
                catch { }

                for (int i = 0; i < candidateFiles.Count; i++)
                {
                    // Skip files too small to be a bundle
                    try
                    {
                        var fi = new FileInfo(candidateFiles[i]);
                        if (fi.Length < 64) continue;
                    }
                    catch { continue; }

                    try
                    {
                        string internalName = ReadUnityFSInternalName(candidateFiles[i]);
                        if (internalName == null) continue;

                        if (string.Equals(internalName, bundleName, StringComparison.OrdinalIgnoreCase))
                        {
                            byte[] bytes = File.ReadAllBytes(candidateFiles[i]);
                            Debug.Log($"[UIBuilderAssetCache] Found raw bundle via internal name match: " +
                                $"'{internalName}' in file '{candidateFiles[i]}' ({bytes.Length:N0} bytes)");
                            return bytes;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Reads the internal bundle name from a UnityFS file's header.
        /// The UnityFS format stores two null-terminated version strings after the magic
        /// and format version. The internal bundle name is embedded in the serialized
        /// metadata that follows. We scan the first 64KB after the header for
        /// null-terminated ASCII strings and identify the bundle name by filtering out
        /// known non-name patterns (CAB hashes, version strings, paths, etc.).
        /// Returns null if the file is not a valid UnityFS bundle or no name is found.
        /// </summary>
        private static string ReadUnityFSInternalName(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // Validate UnityFS magic ("UnityFS\0")
                    byte[] magic = new byte[8];
                    if (fs.Read(magic, 0, 8) < 8) return null;
                    if (magic[0] != 'U' || magic[1] != 'n' || magic[2] != 'i' ||
                        magic[3] != 't' || magic[4] != 'y' || magic[5] != 'F' ||
                        magic[6] != 'S' || magic[7] != 0)
                        return null;

                    // Read format version (big-endian int32)
                    byte[] versionBytes = new byte[4];
                    if (fs.Read(versionBytes, 0, 4) < 4) return null;

                    // Read first null-terminated string (player version)
                    string playerVersion = ReadNullTerminatedString(fs, 128);
                    if (playerVersion == null) return null;

                    // Read second null-terminated string (engine version)
                    string engineVersion = ReadNullTerminatedString(fs, 128);
                    if (engineVersion == null) return null;

                    // Read up to 64KB from this point and scan for the internal name
                    long remaining = Math.Min(fs.Length - fs.Position, 65536);
                    if (remaining < 20) return null;

                    byte[] buffer = new byte[remaining];
                    int bytesRead = fs.Read(buffer, 0, buffer.Length);
                    if (bytesRead < 20) return null;

                    var candidateNames = ExtractNullTerminatedStrings(buffer, bytesRead, 2, 128);

                    for (int c = 0; c < candidateNames.Count; c++)
                    {
                        string candidate = candidateNames[c];
                        if (candidate == playerVersion || candidate == engineVersion) continue;
                        if (candidate.StartsWith("CAB-", StringComparison.Ordinal)) continue;
                        if (candidate.StartsWith("archive:/", StringComparison.Ordinal)) continue;
                        if (candidate.StartsWith("library/", StringComparison.OrdinalIgnoreCase)) continue;
                        bool allDigits = true;
                        for (int ch = 0; ch < candidate.Length; ch++)
                        {
                            if (!char.IsDigit(candidate[ch]) && candidate[ch] != '.') { allDigits = false; break; }
                        }
                        if (allDigits) continue;
                        if (candidate.Contains(" ")) continue;

                        return candidate;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Reads a null-terminated ASCII string from a stream, up to maxLength bytes.
        /// </summary>
        private static string ReadNullTerminatedString(FileStream fs, int maxLength)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < maxLength; i++)
            {
                int b = fs.ReadByte();
                if (b < 0) return null;
                if (b == 0) return sb.ToString();
                sb.Append((char)b);
            }
            return null;
        }

        /// <summary>
        /// Extracts all null-terminated ASCII strings from a byte buffer that are
        /// between minLen and maxLen characters long and contain only printable ASCII.
        /// </summary>
        private static List<string> ExtractNullTerminatedStrings(byte[] buffer, int length, int minLen, int maxLen)
        {
            var results = new List<string>();
            int start = -1;
            for (int i = 0; i < length; i++)
            {
                byte b = buffer[i];
                if (b >= 0x20 && b < 0x7F)
                {
                    if (start < 0) start = i;
                }
                else if (b == 0 && start >= 0)
                {
                    int len = i - start;
                    if (len >= minLen && len <= maxLen)
                    {
                        results.Add(Encoding.ASCII.GetString(buffer, start, len));
                    }
                    start = -1;
                }
                else
                {
                    start = -1;
                }
            }
            return results;
        }

        /// <summary>
        /// Returns the paths of all cached raw asset bundle files.
        /// These files can be embedded as resources in a mod build.
        /// </summary>
        public static List<string> GetCachedBundlePaths()
        {
            var paths = new List<string>();
            if (!Directory.Exists(BundleCacheDir)) return paths;

            try
            {
                foreach (var file in Directory.GetFiles(BundleCacheDir, "*.bundle"))
                    paths.Add(file);
            }
            catch { }

            return paths;
        }

        /// <summary>
        /// Returns the directory where raw asset bundle files are cached.
        /// </summary>
        public static string GetBundleCacheDirectory()
        {
            return BundleCacheDir;
        }

        // ???????????????????????????????????????
        //  Manifest management
        // ???????????????????????????????????????

        private static void EnsureManifest()
        {
            if (_manifest != null) return;

            if (File.Exists(ManifestPath))
            {
                try
                {
                    string json = File.ReadAllText(ManifestPath);
                    _manifest = DeserializeManifest(json);
                }
                catch
                {
                    _manifest = new CacheManifest();
                }
            }
            else
            {
                _manifest = new CacheManifest();
            }
        }

        private static void SaveManifest()
        {
            if (_manifest == null) return;

            try
            {
                EnsureCacheDir();
                // Ensure parent directory for manifest also exists
                string manifestDir = Path.GetDirectoryName(ManifestPath);
                if (!string.IsNullOrEmpty(manifestDir) && !Directory.Exists(manifestDir))
                    Directory.CreateDirectory(manifestDir);

                string json = SerializeManifest(_manifest);
                File.WriteAllText(ManifestPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Failed to save manifest: {ex.Message}");
            }
        }

        private static string FindExistingCacheEntry(string originalName, Sprite sprite)
        {
            if (_manifest == null || _manifest.Entries == null) return null;

            int w = (int)sprite.rect.width;
            int h = (int)sprite.rect.height;

            for (int i = 0; i < _manifest.Entries.Count; i++)
            {
                var entry = _manifest.Entries[i];
                if (string.Equals(entry.OriginalName, originalName, StringComparison.OrdinalIgnoreCase) &&
                    entry.TextureWidth == w && entry.TextureHeight == h)
                {
                    // Verify the file still exists
                    string filePath = Path.Combine(CacheDir, entry.CachedFile);
                    if (File.Exists(filePath))
                        return Path.GetFileNameWithoutExtension(entry.CachedFile);
                }
            }
            return null;
        }

        /// <summary>
        /// Finds an existing cache entry by name and dimensions (for RawImage textures).
        /// </summary>
        private static string FindExistingCacheEntryByName(string originalName, int width, int height)
        {
            if (_manifest == null || _manifest.Entries == null) return null;

            for (int i = 0; i < _manifest.Entries.Count; i++)
            {
                var entry = _manifest.Entries[i];
                if (string.Equals(entry.OriginalName, originalName, StringComparison.OrdinalIgnoreCase) &&
                    entry.TextureWidth == width && entry.TextureHeight == height)
                {
                    string filePath = Path.Combine(CacheDir, entry.CachedFile);
                    if (File.Exists(filePath))
                        return Path.GetFileNameWithoutExtension(entry.CachedFile);
                }
            }
            return null;
        }

        /// <summary>
        /// Removes all manifest entries matching the given original sprite name.
        /// Used during force-recache to prevent duplicate entries.
        /// </summary>
        private static void RemoveManifestEntriesByOriginalName(string originalName)
        {
            if (_manifest == null || _manifest.Entries == null) return;

            for (int i = _manifest.Entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_manifest.Entries[i].OriginalName, originalName, StringComparison.OrdinalIgnoreCase))
                    _manifest.Entries.RemoveAt(i);
            }
        }

        /// <summary>
        /// Invalidates all cached entries, removing manifest entries and optionally
        /// deleting the cached PNG files from disk.
        /// Call this before a full re-capture to ensure fresh assets.
        /// </summary>
        public static void InvalidateAllCacheEntries(bool deleteFiles = false)
        {
            EnsureManifest();

            if (deleteFiles && Directory.Exists(CacheDir))
            {
                try
                {
                    foreach (var file in Directory.GetFiles(CacheDir, "*.png"))
                    {
                        try { File.Delete(file); }
                        catch (Exception ex) { Debug.LogWarning($"[UIBuilderAssetCache] Failed to delete '{file}': {ex.Message}"); }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UIBuilderAssetCache] Failed to enumerate cache dir: {ex.Message}");
                }
            }

            if (_manifest != null)
            {
                _manifest.Entries.Clear();
                SaveManifest();
            }

            _loadedCache.Clear();
            Debug.Log("[UIBuilderAssetCache] All cache entries invalidated.");
        }

        /// <summary>
        /// Invalidates cached entries for a specific original sprite name,
        /// removing the manifest entry and optionally the PNG file.
        /// </summary>
        public static void InvalidateCacheEntry(string originalName, bool deleteFile = false)
        {
            if (string.IsNullOrEmpty(originalName)) return;
            EnsureManifest();
            if (_manifest == null) return;

            for (int i = _manifest.Entries.Count - 1; i >= 0; i--)
            {
                var entry = _manifest.Entries[i];
                if (string.Equals(entry.OriginalName, originalName, StringComparison.OrdinalIgnoreCase))
                {
                    if (deleteFile)
                    {
                        string filePath = Path.Combine(CacheDir, entry.CachedFile);
                        try { if (File.Exists(filePath)) File.Delete(filePath); }
                        catch { /* best effort */ }
                    }

                    string cacheId = Path.GetFileNameWithoutExtension(entry.CachedFile);
                    _loadedCache.Remove(cacheId);
                    _manifest.Entries.RemoveAt(i);
                }
            }
            SaveManifest();
        }

        // ???????????????????????????????????????
        //  Texture extraction
        // ???????????????????????????????????????

        /// <summary>
        /// Extracts a sprite's pixels to a PNG byte array. Handles both readable and
        /// non-readable textures via RenderTexture blit fallback.
        /// </summary>
        private static byte[] ExtractSpriteToPNG(Sprite sprite)
        {
            if (sprite == null) return null;

            Rect spriteRect = sprite.packed ? sprite.textureRect : sprite.rect;
            int srcX = (int)spriteRect.x;
            int srcY = (int)spriteRect.y;
            int srcW = (int)spriteRect.width;
            int srcH = (int)spriteRect.height;

            if (srcW <= 0 || srcH <= 0) return null;

            // Downscale if too large
            int outW = srcW;
            int outH = srcH;
            if (outW > MaxCacheDimension || outH > MaxCacheDimension)
            {
                float scale = Mathf.Min((float)MaxCacheDimension / outW, (float)MaxCacheDimension / outH);
                outW = Mathf.Max(1, (int)(outW * scale));
                outH = Mathf.Max(1, (int)(outH * scale));
            }

            return ExtractRegionToPNG(sprite.texture, srcX, srcY, srcW, srcH, outW, outH);
        }

        /// <summary>
        /// Extracts an entire Texture2D to a PNG byte array. Handles both readable and
        /// non-readable textures via RenderTexture blit fallback.
        /// Used for RawImage texture caching.
        /// </summary>
        private static byte[] ExtractTextureToPNG(Texture2D tex)
        {
            if (tex == null) return null;

            int srcW = tex.width;
            int srcH = tex.height;
            if (srcW <= 0 || srcH <= 0) return null;

            int outW = srcW;
            int outH = srcH;
            if (outW > MaxCacheDimension || outH > MaxCacheDimension)
            {
                float scale = Mathf.Min((float)MaxCacheDimension / outW, (float)MaxCacheDimension / outH);
                outW = Mathf.Max(1, (int)(outW * scale));
                outH = Mathf.Max(1, (int)(outH * scale));
            }

            return ExtractRegionToPNG(tex, 0, 0, srcW, srcH, outW, outH);
        }

        /// <summary>
        /// Extracts a region from a texture to PNG bytes. Handles readable and non-readable textures.
        /// </summary>
        private static byte[] ExtractRegionToPNG(Texture2D tex, int srcX, int srcY, int srcW, int srcH, int outW, int outH)
        {
            if (tex == null || srcW <= 0 || srcH <= 0) return null;

            // Try direct pixel read first (works if texture is readable)
            try
            {
                if (tex != null && tex.isReadable)
                {
                    Color[] pixels = tex.GetPixels(srcX, srcY, srcW, srcH);
                    var outTex = new Texture2D(srcW, srcH, TextureFormat.RGBA32, false);
                    outTex.SetPixels(pixels);
                    outTex.Apply();

                    if (outW != srcW || outH != srcH)
                    {
                        var scaled = ScaleTexture(outTex, outW, outH);
                        UnityEngine.Object.Destroy(outTex);
                        outTex = scaled;
                    }

                    byte[] png = EncodeToPNG(outTex);
                    UnityEngine.Object.Destroy(outTex);
                    return png;
                }
            }
            catch
            {
                // Texture not readable — fall through to blit
            }

            // RenderTexture blit fallback for non-readable textures
            try
            {
                return ExtractViaRenderTextureBlit(tex, srcX, srcY, srcW, srcH, outW, outH);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] RenderTexture blit fallback failed: {ex.Message}");
                return null;
            }
        }

        private static byte[] ExtractViaRenderTextureBlit(Texture2D srcTex, int srcX, int srcY,
            int srcW, int srcH, int outW, int outH)
        {
            if (srcTex == null) return null;

            // Blit the full texture to a temporary RenderTexture
            var rt = RenderTexture.GetTemporary(srcTex.width, srcTex.height, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;

            Graphics.Blit(srcTex, rt);
            RenderTexture.active = rt;

            // Read just the sprite region
            var readback = new Texture2D(srcW, srcH, TextureFormat.RGBA32, false);
            readback.ReadPixels(new Rect(srcX, srcY, srcW, srcH), 0, 0);
            readback.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            if (outW != srcW || outH != srcH)
            {
                var scaled = ScaleTexture(readback, outW, outH);
                UnityEngine.Object.Destroy(readback);
                readback = scaled;
            }

            byte[] png = EncodeToPNG(readback);
            UnityEngine.Object.Destroy(readback);
            return png;
        }

        private static Texture2D ScaleTexture(Texture2D source, int targetW, int targetH)
        {
            var rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;

            Graphics.Blit(source, rt);
            RenderTexture.active = rt;

            var result = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
            result.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
            result.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return result;
        }

        private static byte[] EncodeToPNG(Texture2D tex)
        {
            // ImageConversion.EncodeToPNG may be in a separate module
            try
            {
                var imageConvType = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                if (imageConvType != null)
                {
                    var method = imageConvType.GetMethod("EncodeToPNG",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                        null, new[] { typeof(Texture2D) }, null);
                    if (method != null)
                        return (byte[])method.Invoke(null, new object[] { tex });
                }

                // Fallback — try instance method on Texture2D (older Unity)
                var instanceMethod = typeof(Texture2D).GetMethod("EncodeToPNG",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                    null, Type.EmptyTypes, null);
                if (instanceMethod != null)
                    return (byte[])instanceMethod.Invoke(tex, null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] EncodeToPNG failed: {ex.Message}");
            }
            return null;
        }

        private static bool LoadImageIntoTexture(Texture2D tex, byte[] data)
        {
            try
            {
                var imageConvType = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                if (imageConvType != null)
                {
                    var method = imageConvType.GetMethod("LoadImage",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                        null, new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) }, null);
                    if (method != null)
                        return (bool)method.Invoke(null, new object[] { tex, data, false });
                }

                var instanceMethod = typeof(Texture2D).GetMethod("LoadImage",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                    null, new[] { typeof(byte[]) }, null);
                if (instanceMethod != null)
                    return (bool)instanceMethod.Invoke(tex, new object[] { data });
            }
            catch { }
            return false;
        }

        // ???????????????????????????????????????
        //  Helpers
        // ???????????????????????????????????????

        private static void EnsureCacheDir()
        {
            if (!Directory.Exists(CacheDir))
                Directory.CreateDirectory(CacheDir);
        }

        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "sprite";

            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                    sb.Append(c);
                else if (c == ':' || c == ' ' || c == '.')
                    sb.Append('_');
                // Skip other characters
            }

            string result = sb.ToString();
            if (result.Length == 0) return "sprite";
            if (result.Length > 80) result = result.Substring(0, 80);
            return result;
        }

        private static string DetectSpriteAssembly(Sprite sprite)
        {
            // We can't directly determine which assembly a sprite came from at runtime,
            // since sprites are pure data. Return empty — the manifest entry records this
            // based on the context of the capture (the canvas classification).
            return "";
        }

        // ???????????????????????????????????????
        //  Manifest serialization (hand-rolled, no external deps)
        // ???????????????????????????????????????

        private class CacheManifest
        {
            public int Version = 1;
            public List<CacheEntry> Entries = new List<CacheEntry>();
        }

        private class CacheEntry
        {
            public string OriginalName = "";
            public string CachedFile = "";
            public string SourceAssembly = "";
            public int TextureWidth;
            public int TextureHeight;
            public long CapturedTimestamp;
            /// <summary>Sprite border (for sliced/tiled sprites). Stored as left,bottom,right,top.</summary>
            public float BorderL, BorderB, BorderR, BorderT;
            /// <summary>Sprite pivot (0-1 range).</summary>
            public float PivotX = 0.5f, PivotY = 0.5f;
            /// <summary>Pixels per unit for the sprite.</summary>
            public float PixelsPerUnit = 100f;
        }

        private static string SerializeManifest(CacheManifest manifest)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"version\": {manifest.Version},");
            sb.AppendLine("  \"entries\": [");

            for (int i = 0; i < manifest.Entries.Count; i++)
            {
                var e = manifest.Entries[i];
                sb.Append("    {");
                sb.Append($" \"originalName\": \"{EscapeJson(e.OriginalName)}\",");
                sb.Append($" \"cachedFile\": \"{EscapeJson(e.CachedFile)}\",");
                sb.Append($" \"sourceAssembly\": \"{EscapeJson(e.SourceAssembly)}\",");
                sb.Append($" \"textureWidth\": {e.TextureWidth},");
                sb.Append($" \"textureHeight\": {e.TextureHeight},");
                sb.Append($" \"capturedTimestamp\": {e.CapturedTimestamp},");
                sb.Append($" \"borderL\": {e.BorderL:F2},");
                sb.Append($" \"borderB\": {e.BorderB:F2},");
                sb.Append($" \"borderR\": {e.BorderR:F2},");
                sb.Append($" \"borderT\": {e.BorderT:F2},");
                sb.Append($" \"pivotX\": {e.PivotX:F4},");
                sb.Append($" \"pivotY\": {e.PivotY:F4},");
                sb.Append($" \"pixelsPerUnit\": {e.PixelsPerUnit:F1}");
                sb.Append(" }");
                if (i < manifest.Entries.Count - 1) sb.Append(",");
                sb.AppendLine();
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static CacheManifest DeserializeManifest(string json)
        {
            // Minimal hand-rolled parser — just needs to read the entries array
            var manifest = new CacheManifest();
            if (string.IsNullOrEmpty(json)) return manifest;

            try
            {
                int idx = 0;
                // Find "entries" array
                int entriesIdx = json.IndexOf("\"entries\"", StringComparison.Ordinal);
                if (entriesIdx < 0) return manifest;

                int arrayStart = json.IndexOf('[', entriesIdx);
                if (arrayStart < 0) return manifest;

                idx = arrayStart + 1;
                while (idx < json.Length)
                {
                    int objStart = json.IndexOf('{', idx);
                    if (objStart < 0) break;

                    int objEnd = json.IndexOf('}', objStart);
                    if (objEnd < 0) break;

                    string objStr = json.Substring(objStart, objEnd - objStart + 1);
                    var entry = new CacheEntry
                    {
                        OriginalName = ExtractJsonString(objStr, "originalName"),
                        CachedFile = ExtractJsonString(objStr, "cachedFile"),
                        SourceAssembly = ExtractJsonString(objStr, "sourceAssembly"),
                        TextureWidth = ExtractJsonInt(objStr, "textureWidth"),
                        TextureHeight = ExtractJsonInt(objStr, "textureHeight"),
                        CapturedTimestamp = ExtractJsonLong(objStr, "capturedTimestamp"),
                        BorderL = ExtractJsonFloat(objStr, "borderL"),
                        BorderB = ExtractJsonFloat(objStr, "borderB"),
                        BorderR = ExtractJsonFloat(objStr, "borderR"),
                        BorderT = ExtractJsonFloat(objStr, "borderT"),
                        PivotX = ExtractJsonFloat(objStr, "pivotX", 0.5f),
                        PivotY = ExtractJsonFloat(objStr, "pivotY", 0.5f),
                        PixelsPerUnit = ExtractJsonFloat(objStr, "pixelsPerUnit", 100f)
                    };
                    manifest.Entries.Add(entry);

                    idx = objEnd + 1;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAssetCache] Manifest parse error: {ex.Message}");
            }

            return manifest;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
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
                // Distinguish between "key not found" and "key = 0"
                string search = "\"" + key + "\"";
                if (json.IndexOf(search, StringComparison.Ordinal) < 0)
                    return defaultValue;
            }
            if (float.TryParse(val, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float result))
                return result;
            return defaultValue;
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
    }
}
