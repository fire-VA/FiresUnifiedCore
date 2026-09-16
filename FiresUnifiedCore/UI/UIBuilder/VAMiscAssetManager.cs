using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Manages miscellaneous assets like UI prefabs, sprites, and external resources.
    /// Supports loading from asset bundle and external config folders.
    /// </summary>
    public static class VAMiscAssetManager
    {
        private static readonly string UIAssetsDir = Path.Combine(FiresCore.Storage.FiresConfigPaths.UiAssets);
        private static readonly Dictionary<string, Sprite> _cachedSprites = new();
        private static readonly HashSet<string> _negativeSpriteCache = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Clears the negative sprite cache so that previously-missing sprites are re-checked on disk.
        /// Called after RuntimeSpriteSync delivers new UIAssets files from the server.
        /// </summary>
        public static void ClearNegativeCache()
        {
            _negativeSpriteCache.Clear();
        }
        private static readonly Dictionary<string, GameObject> _cachedPrefabs = new();
        private static readonly Dictionary<string, Material> _cachedMaterials = new();

        // Asset bundle base path - matches other managers (VAItemManager, etc.)
        private const string AssetBundleBasePath = "Assets/Custom/VAitems/UI/";
        private const string IconsPath = "Assets/Custom/VAitems/Icons/";

    // Known UI prefab names
        public static readonly List<string> UIPrefabNames = new List<string>
        {
            "NPCMainPanelTest",
            "DialogueEntryPrefab",
            "QuestEntryPrefab",
            "DialoguePanel",
            "QuestPanel",
            "HealthBarOverlayVAD",
            "TraderUI",

  };

        // Known sprite asset names
     public static readonly List<string> SpriteAssetNames = new List<string>
        {
          "npc_icon_default",
          "quest_icon_default",
          "dialogue_icon_default",
          "panel_background",
          "npcuibkg",  // NPC panel background
          "npcuibkg1" , // Companion attributes screen background
          "npcuibkg4",
        };

        #region Initialization

        /// <summary>
        /// Initializes the asset manager and loads all external assets.
        /// </summary>
        public static void Initialize()
        {
    EnsureDirectoriesExist();
       LoadAllExternalSprites();
      Debug.Log($"[VAMiscAssetManager] Initialized. Cached {_cachedSprites.Count} sprites.");
        }

        private static void EnsureDirectoriesExist()
        {
       try
            {
          if (!Directory.Exists(UIAssetsDir))
               Directory.CreateDirectory(UIAssetsDir);

            string spritesDir = Path.Combine(UIAssetsDir, "Sprites");
       if (!Directory.Exists(spritesDir))
            Directory.CreateDirectory(spritesDir);
       }
            catch (Exception ex)
  {
            Debug.LogWarning($"[VAMiscAssetManager] Failed to create directories: {ex.Message}");
       }
        }

        #endregion

#region UI Prefab Loading

        /// <summary>
        /// Loads a UI prefab by name. Tries asset bundle first, then falls back to cache.
   /// Uses path: Assets/Custom/VAitems/UI/{prefabName}
 /// </summary>
        public static GameObject LoadUIPrefab(string prefabName)
        {
  if (string.IsNullOrWhiteSpace(prefabName))
             return null;

        // Check cache first
    if (_cachedPrefabs.TryGetValue(prefabName, out var cached) && cached != null)
                return cached;

     // Try loading from asset bundle
    var prefab = LoadPrefabFromAssetBundle(prefabName);
      if (prefab != null)
            {
       _cachedPrefabs[prefabName] = prefab;
         Debug.Log($"[VAMiscAssetManager] Loaded UI prefab '{prefabName}' from asset bundle");
  return prefab;
        }

 Debug.Log($"[VAMiscAssetManager] UI prefab '{prefabName}' not found in asset bundle (may be code-generated)");
            return null;
    }

        private static GameObject LoadPrefabFromAssetBundle(string prefabName)
{
            var bundle = UIBuilderHost.AssetBundle;
            if (bundle == null)
   return null;

        // Try paths matching your other managers (Assets/Custom/VAitems/UI/)
   string[] searchPaths = {
            $"{AssetBundleBasePath}{prefabName}",
    $"{AssetBundleBasePath}{prefabName}.prefab",
                $"{AssetBundleBasePath}{prefabName.ToLower()}",
     $"{AssetBundleBasePath}{prefabName.ToLower()}.prefab",
         // Fallback paths
 $"Assets/Custom/NPCs/UI/{prefabName}",
        $"Assets/Custom/NPCs/UI/{prefabName}.prefab",
       prefabName,
      $"{prefabName}.prefab"
        };

       foreach (var path in searchPaths)
   {
           try
    {
     var prefab = bundle.LoadAsset<GameObject>(path);
        if (prefab != null)
        {
        Debug.Log($"[VAMiscAssetManager] Found prefab at path: {path}");
      return prefab;
    }
           }
   catch { }
    }

        return null;
     }

        /// <summary>
        /// Checks if a UI prefab exists in the asset bundle.
     /// </summary>
        public static bool HasUIPrefab(string prefabName)
        {
        if (_cachedPrefabs.ContainsKey(prefabName))
   return true;

  return LoadPrefabFromAssetBundle(prefabName) != null;
 }

 /// <summary>
     /// Clears the prefab cache (useful when reloading asset bundles).
    /// </summary>
      public static void ClearPrefabCache()
        {
 _cachedPrefabs.Clear();
        }

       /// <summary>Clears the negative sprite cache (call after loading new external sprites).</summary>
       public static void ClearNegativeSpriteCache()
       {
           _negativeSpriteCache.Clear();
       }

        #endregion

  #region Sprite Loading

        /// <summary>
        /// Loads external sprites from the UIAssets/Sprites folder (custom mod sprites).
        /// CapturedAssets/sprites are NOT bulk-loaded at startup - they are loaded
        /// lazily on demand via GetSprite() -> TryLoadFromCapturedAssets(), since
        /// most of those are vanilla/mod textures already available in memory.
        /// </summary>
        public static void LoadAllExternalSprites()
        {
            _negativeSpriteCache.Clear(); // new files may satisfy previously-failed lookups
            LoadSpritesFromDirectory(Path.Combine(UIAssetsDir, "Sprites"));
        }

        private static void LoadSpritesFromDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return;

            foreach (var file in Directory.GetFiles(dir, "*.png"))
            {
                string key = Path.GetFileNameWithoutExtension(file);
                if (_cachedSprites.ContainsKey(key)) continue; // don't override earlier entries
                var sprite = LoadSpriteFromFile(file);
                if (sprite != null)
                    _cachedSprites[key] = sprite;
            }

            foreach (var file in Directory.GetFiles(dir, "*.jpg"))
            {
                string key = Path.GetFileNameWithoutExtension(file);
                if (_cachedSprites.ContainsKey(key)) continue;
                var sprite = LoadSpriteFromFile(file);
                if (sprite != null)
                    _cachedSprites[key] = sprite;
            }
        }

        /// <summary>
        /// Gets a sprite by name. Checks cache, then asset bundle, then CapturedAssets on disk.
        /// </summary>
        public static Sprite GetSprite(string spriteName)
        {
            if (string.IsNullOrWhiteSpace(spriteName))
                return null;

            string cleanName = spriteName.Trim();

            // Short-circuit: skip expensive bundle/disk lookups for known-missing names
            if (_negativeSpriteCache.Contains(cleanName))
                return null;

            // Check cache first (includes UIAssets/Sprites + CapturedAssets/sprites)
            if (_cachedSprites.TryGetValue(cleanName, out var cached))
                return cached;

            // Try without extension
            string nameNoExt = Path.GetFileNameWithoutExtension(cleanName);
            if (_cachedSprites.TryGetValue(nameNoExt, out cached))
                return cached;

            // Try loading from asset bundle
            var sprite = LoadSpriteFromAssetBundle(cleanName);
            if (sprite != null)
            {
                _cachedSprites[cleanName] = sprite;
                return sprite;
            }

            // Last resort: try loading directly from CapturedAssets/sprites on disk
            sprite = TryLoadFromCapturedAssets(nameNoExt);
            if (sprite != null)
            {
                _cachedSprites[nameNoExt] = sprite;
                return sprite;
            }

            // Search Valheim's loaded sprites in memory (vanilla UI sprites like crafting_panel_bkg)
            sprite = FindLoadedSprite(nameNoExt);
            if (sprite != null)
            {
                _cachedSprites[nameNoExt] = sprite;
                return sprite;
            }

            // Remember this name was not found - avoid repeating expensive lookups
            _negativeSpriteCache.Add(cleanName);
            return null;
        }

        /// <summary>
        /// Searches all sprites currently loaded in Unity (including vanilla Valheim UI sprites).
        /// Results are cached so this expensive search only runs once per sprite name.
        /// </summary>
        private static Sprite FindLoadedSprite(string name)
        {
            try
            {
                var allSprites = Resources.FindObjectsOfTypeAll<Sprite>();
                foreach (var sprite in allSprites)
                {
                    if (sprite != null && string.Equals(sprite.name, name, StringComparison.OrdinalIgnoreCase))
                        return sprite;
                }
            }
            catch { }
            return null;
        }

        private static Sprite TryLoadFromCapturedAssets(string name)
        {
            string capturedDir = Path.Combine(UIAssetsDir, "CapturedAssets", "sprites");
            if (!Directory.Exists(capturedDir)) return null;

            string pngPath = Path.Combine(capturedDir, name + ".png");
            if (File.Exists(pngPath))
                return LoadSpriteFromFile(pngPath);

            string jpgPath = Path.Combine(capturedDir, name + ".jpg");
            if (File.Exists(jpgPath))
                return LoadSpriteFromFile(jpgPath);

            return null;
        }

        private static Sprite LoadSpriteFromAssetBundle(string spriteName)
        {
      var bundle = UIBuilderHost.AssetBundle;
       if (bundle == null)
     return null;

   // Try paths matching your other managers (Assets/Custom/VAitems/Icons/)
     string[] searchPaths = {
                $"{IconsPath}{spriteName}",
         $"{IconsPath}{spriteName}.png",
    $"{IconsPath}{spriteName.ToLower()}",
                $"{IconsPath}{spriteName.ToLower()}.png",
      // Also check UI folder for UI-specific sprites
   $"{AssetBundleBasePath}{spriteName}",
     $"{AssetBundleBasePath}{spriteName}.png",
   // Fallback
        spriteName,
  $"{spriteName}.png"
            };

            foreach (var path in searchPaths)
      {
      try
                {
  var sprite = bundle.LoadAsset<Sprite>(path);
  if (sprite != null)
        {
     Debug.Log($"[VAMiscAssetManager] Found sprite at path: {path}");
  return sprite;
        }
                }
           catch { }
    }

         return null;
  }

        /// <summary>
/// Loads a sprite from an external file.
        /// </summary>
        public static Sprite LoadSpriteFromFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
    return null;

       filePath = filePath.Trim().Trim('"').TrimStart('/', '\\').TrimEnd('/', '\\');

        if (filePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
       {
    Debug.LogWarning($"[VAMiscAssetManager] File path contains illegal characters: {filePath}");
         return null;
       }

 string ext;
            try
   {
    ext = Path.GetExtension(filePath).ToLowerInvariant();
      }
     catch (Exception ex)
    {
                Debug.LogWarning($"[VAMiscAssetManager] Exception getting file extension: {filePath}\n{ex}");
    return null;
            }

         if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
            {
   Debug.LogWarning($"[VAMiscAssetManager] Unsupported file type: {filePath}");
            return null;
            }

     if (!File.Exists(filePath))
            {
                Debug.LogWarning($"[VAMiscAssetManager] File not found: {filePath}");
    return null;
      }

     try
       {
       byte[] fileData = File.ReadAllBytes(filePath);
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);

          // Try using ImageConversion.LoadImage if available
     var imageConversionType = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
      if (imageConversionType != null)
 {
      var loadImageMethod = imageConversionType.GetMethod(
             "LoadImage",
   BindingFlags.Static | BindingFlags.Public,
           null,
        new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) },
        null);

   if (loadImageMethod != null)
                    {
        bool loaded = (bool)loadImageMethod.Invoke(null, new object[] { tex, fileData, false });
if (!loaded)
            {
       Debug.LogWarning($"[VAMiscAssetManager] Failed to load image: {filePath}");
               return null;
   }

     tex.name = Path.GetFileNameWithoutExtension(filePath);
         Sprite sprite = Sprite.Create(
      tex,
         new Rect(0, 0, tex.width, tex.height),
 new Vector2(0.5f, 0.5f),
    100f);

        Debug.Log($"[VAMiscAssetManager] Loaded sprite from file: {filePath}");
  return sprite;
         }
       }

      // Fallback: try tex.LoadImage directly (older Unity)
       try
    {
   var loadImageDirect = typeof(Texture2D).GetMethod(
                 "LoadImage",
          BindingFlags.Instance | BindingFlags.Public,
 null,
     new[] { typeof(byte[]) },
             null);

          if (loadImageDirect != null)
      {
       bool loaded = (bool)loadImageDirect.Invoke(tex, new object[] { fileData });
        if (loaded)
  {
    tex.name = Path.GetFileNameWithoutExtension(filePath);
           Sprite sprite = Sprite.Create(
         tex,
        new Rect(0, 0, tex.width, tex.height),
         new Vector2(0.5f, 0.5f),
             100f);
               return sprite;
      }
      }
}
             catch { }

          Debug.LogError($"[VAMiscAssetManager] Could not find LoadImage method for this Unity version.");
        return null;
  }
            catch (Exception ex)
            {
                Debug.LogError($"[VAMiscAssetManager] Exception loading sprite: {filePath}\n{ex}");
     return null;
            }
        }

        /// <summary>
        /// Refreshes a specific sprite from disk.
        /// </summary>
        public static void RefreshSprite(string spriteName)
    {
        if (string.IsNullOrWhiteSpace(spriteName))
    return;

   string nameNoExt = Path.GetFileNameWithoutExtension(spriteName.Trim());

            // Remove from cache
    var keysToRemove = new List<string>();
      foreach (var key in _cachedSprites.Keys)
            {
    if (Path.GetFileNameWithoutExtension(key).Equals(nameNoExt, StringComparison.OrdinalIgnoreCase))
        keysToRemove.Add(key);
            }
       foreach (var key in keysToRemove)
  _cachedSprites.Remove(key);

            // Try to reload from disk - check Sprites folder first, then CapturedAssets
            string spritesDir = Path.Combine(UIAssetsDir, "Sprites");
            string capturedDir = Path.Combine(UIAssetsDir, "CapturedAssets", "sprites");
            string pngPath = Path.Combine(spritesDir, nameNoExt + ".png");
            string jpgPath = Path.Combine(spritesDir, nameNoExt + ".jpg");
            string capturedPng = Path.Combine(capturedDir, nameNoExt + ".png");
            string capturedJpg = Path.Combine(capturedDir, nameNoExt + ".jpg");

            string fileToLoad = File.Exists(pngPath) ? pngPath
                : File.Exists(jpgPath) ? jpgPath
                : File.Exists(capturedPng) ? capturedPng
                : File.Exists(capturedJpg) ? capturedJpg
                : null;

            if (fileToLoad != null)
            {
                var sprite = LoadSpriteFromFile(fileToLoad);
                if (sprite != null)
                    _cachedSprites[nameNoExt] = sprite;
            }
        }

        #endregion

        #region Generic Asset Loading

        /// <summary>
        /// Loads any asset from the asset bundle by name and type.
   /// Searches in Assets/Custom/VAitems/UI/ and Assets/Custom/VAitems/Icons/
        /// </summary>
        public static T LoadAsset<T>(string assetName) where T : UnityEngine.Object
        {
        var bundle = UIBuilderHost.AssetBundle;
 if (bundle == null)
       return null;

      string[] searchPaths = {
     $"{AssetBundleBasePath}{assetName}",
              $"{IconsPath}{assetName}",
                $"Assets/Custom/NPCs/{assetName}",
       assetName
            };

            foreach (var path in searchPaths)
            {
      try
      {
var asset = bundle.LoadAsset<T>(path);
     if (asset != null)
     {
       Debug.Log($"[VAMiscAssetManager] Loaded {typeof(T).Name} '{assetName}' from {path}");
         return asset;
             }
    }
     catch { }
        }

  return null;
      }

        /// <summary>
        /// Lists all asset names in the asset bundle (for debugging).
        /// </summary>
    public static string[] ListAllAssetNames()
        {
      var bundle = UIBuilderHost.AssetBundle;
 if (bundle == null)
  return Array.Empty<string>();

     try
    {
         return bundle.GetAllAssetNames();
}
       catch
     {
          return Array.Empty<string>();
            }
        }

    /// <summary>
        /// Debug helper - prints all assets in the bundle to log.
        /// </summary>
    public static void DebugListAllAssets()
        {
  var names = ListAllAssetNames();
            Debug.Log($"[VAMiscAssetManager] Asset bundle contains {names.Length} assets:");
        foreach (var name in names)
       {
  Debug.Log($"  - {name}");
       }
        }

        #endregion

        #region Material Loading

        /// <summary>
        /// Gets a material by name from the asset bundle.
        /// Used for eye overlay materials (FiresEyes, FiresEyesFem) and other custom materials.
        /// </summary>
        public static Material GetMaterial(string materialName)
        {
            if (string.IsNullOrWhiteSpace(materialName))
                return null;

            // Check cache first
            if (_cachedMaterials.TryGetValue(materialName, out var cached) && cached != null)
                return cached;

            // Try loading from asset bundle
            var material = LoadMaterialFromAssetBundle(materialName);
            if (material != null)
            {
                _cachedMaterials[materialName] = material;
                Debug.Log($"[VAMiscAssetManager] Loaded material '{materialName}' from asset bundle");
                return material;
            }

            return null;
        }

        private static Material LoadMaterialFromAssetBundle(string materialName)
        {
            var bundle = UIBuilderHost.AssetBundle;
            if (bundle == null)
                return null;

            // Try various paths where materials might be stored
            string[] searchPaths = {
                $"{AssetBundleBasePath}{materialName}",
                $"{AssetBundleBasePath}{materialName}.mat",
                $"Assets/Custom/VAitems/Materials/{materialName}",
                $"Assets/Custom/VAitems/Materials/{materialName}.mat",
                $"Assets/Custom/NPCs/Materials/{materialName}",
                $"Assets/Custom/NPCs/Materials/{materialName}.mat",
                materialName,
                $"{materialName}.mat"
            };

            foreach (var path in searchPaths)
            {
                try
                {
                    var material = bundle.LoadAsset<Material>(path);
                    if (material != null)
                    {
                        Debug.Log($"[VAMiscAssetManager] Found material at path: {path}");
                        return material;
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Clears the material cache.
        /// </summary>
        public static void ClearMaterialCache()
        {
            _cachedMaterials.Clear();
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Clears all cached assets.
        /// </summary>
        public static void ClearAllCaches()
        {
            _cachedSprites.Clear();
            _negativeSpriteCache.Clear();
            _cachedPrefabs.Clear();
            _cachedMaterials.Clear();
        }

        #endregion
    }
}
