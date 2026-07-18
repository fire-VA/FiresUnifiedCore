using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Loads a panel background from a user-supplied PNG/JPG dropped into a BepInEx config folder — the same
    /// idea as the VA Inventory mod's configurable backgrounds, so a server/player can re-skin a Fires panel
    /// without rebuilding a bundle. Returns a cached <see cref="Sprite"/>, or null when the file is
    /// absent/invalid (the caller then keeps its baked default). Shared across the Fires family.
    /// </summary>
    public static class UIBackgroundLoader
    {
        public const string FolderName = "FiresUIBackgrounds";

        private static readonly Dictionary<string, Sprite> _cache =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        public static string Folder => Path.Combine(BepInEx.Paths.ConfigPath, FolderName);

        /// <summary>Load <paramref name="fileName"/> from <see cref="Folder"/> as a 9-slice-friendly sprite (cached).</summary>
        public static Sprite Load(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            fileName = fileName.Trim().Trim('"');
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") return null;
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

            if (_cache.TryGetValue(fileName, out var cached)) return cached;

            try
            {
                Directory.CreateDirectory(Folder);
                string path = Path.Combine(Folder, fileName);
                if (!File.Exists(path)) { _cache[fileName] = null; return null; }

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                // Call ImageConversion.LoadImage by reflection — referencing it at compile time pulls
                // UnityEngine.ImageConversionModule (netstandard 2.1) and clashes with Core's netstandard 2.0.
                if (!LoadImageReflected(tex, File.ReadAllBytes(path))) { _cache[fileName] = null; return null; }

                // A uniform border keeps Image.Type.Sliced corners crisp when the panel stretches the art.
                float b = Mathf.Min(tex.width, tex.height) * 0.25f;
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f),
                    100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
                _cache[fileName] = sprite;
                return sprite;
            }
            catch (Exception ex)
            {
                FiresCore.Logging.FiresLogger.LogWarning($"[UIBackgroundLoader] load '{fileName}' failed: {ex.Message}");
                _cache[fileName] = null;
                return null;
            }
        }

        /// <summary>Drop the cache so an edited PNG (or a changed config filename) is re-read on next request.</summary>
        public static void ForceRefresh() => _cache.Clear();

        private static System.Reflection.MethodInfo _loadImage;
        private static bool _loadImageResolved;

        private static bool LoadImageReflected(Texture2D tex, byte[] data)
        {
            if (!_loadImageResolved)
            {
                _loadImageResolved = true;
                var t = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                _loadImage = t?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
            }
            if (_loadImage == null) return false;
            try { return (bool)_loadImage.Invoke(null, new object[] { tex, data }); }
            catch { return false; }
        }
    }
}
