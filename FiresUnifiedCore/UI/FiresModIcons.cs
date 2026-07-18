using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Client-side: resolves a loaded plugin's Thunderstore <c>icon.png</c> into a cached Unity Sprite,
    /// keyed by BepInEx GUID. A Thunderstore package ships an <c>icon.png</c> in its package root next to
    /// (or a level above) the DLL, so we look up the plugin's assembly location and walk up until we find
    /// it, stopping at the shared <c>plugins</c> root so we never grab a neighbour's icon. Mods installed
    /// without one (dev builds, hand-dropped DLLs) resolve to <c>null</c> and the caller draws a placeholder.
    /// Results — including null misses — are cached, so a popup listing many mods decodes each PNG at most once.
    /// </summary>
    public static class FiresModIcons
    {
        private static readonly Dictionary<string, Sprite> _cache =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Sprite for <paramref name="guid"/>'s icon.png, or null if the mod isn't loaded / has no icon.</summary>
        public static Sprite Get(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (_cache.TryGetValue(guid, out var cached)) return cached;

            Sprite sprite = null;
            try { sprite = Load(guid); }
            catch (Exception ex) { Debug.LogWarning($"[FiresModIcons] {guid}: {ex.Message}"); }
            _cache[guid] = sprite;
            return sprite;
        }

        private static Sprite Load(string guid)
        {
            var infos = BepInEx.Bootstrap.Chainloader.PluginInfos;
            if (infos == null || !infos.TryGetValue(guid, out var info) || info == null) return null;

            string dll = null;
            try { dll = info.Location; } catch { }
            if (string.IsNullOrEmpty(dll) && info.Instance != null)
                dll = info.Instance.GetType().Assembly.Location;
            if (string.IsNullOrEmpty(dll)) return null;

            string iconPath = FindIcon(Path.GetDirectoryName(dll));
            if (iconPath == null) return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!LoadImageReflected(tex, File.ReadAllBytes(iconPath))) { UnityEngine.Object.Destroy(tex); return null; }
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }

        // Call ImageConversion.LoadImage by reflection: a compile-time reference pulls
        // UnityEngine.ImageConversionModule (netstandard 2.1) and clashes with Core's netstandard 2.0.
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

        // Walk up from the DLL folder to the package root's icon.png; stop once the parent is the shared
        // "plugins" folder so we don't climb into a sibling mod or BepInEx itself.
        private static string FindIcon(string dir)
        {
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "icon.png");
                if (File.Exists(candidate)) return candidate;

                string parent = Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(parent) &&
                    string.Equals(Path.GetFileName(parent), "plugins", StringComparison.OrdinalIgnoreCase))
                    return null;
                dir = parent;
            }
            return null;
        }
    }
}
