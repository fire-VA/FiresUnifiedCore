using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresCore.UI.Minigames
{
    /// <summary>
    /// Loads embedded-PNG art into Sprites for the Fires minigame overlays (lock-pick housing/pins, dice cup, wheel,
    /// runes…). Art is embedded in the CALLING mod's assembly, so every entry point takes that assembly — this shared
    /// helper never assumes FUC's own manifest.
    ///
    /// Texture2D.LoadImage lives in UnityEngine.ImageConversionModule, which drags in netstandard 2.1 and breaks the
    /// net48 build when referenced directly. It is driven by reflection instead (the module resolves at runtime).
    /// </summary>
    public static class FiresMinigameSprites
    {
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();
        private static MethodInfo _loadImage;
        private static bool _loadImageResolved;

        /// <summary>Embedded PNG (matched by resource-name suffix) → Sprite, cached per assembly+name. Null on failure.</summary>
        public static Sprite Load(Assembly asm, string endsWith, float pixelsPerUnit = 100f)
            => Load(asm, endsWith, Vector4.zero, pixelsPerUnit);

        /// <summary>As <see cref="Load(Assembly,string,float)"/> but bakes a 9-slice border (left,bottom,right,top px) into the Sprite for Image.Type.Sliced.</summary>
        public static Sprite Load(Assembly asm, string endsWith, Vector4 border, float pixelsPerUnit = 100f)
        {
            if (asm == null || string.IsNullOrEmpty(endsWith)) return null;
            string key = asm.FullName + "|" + endsWith + "|" + border;
            if (_cache.TryGetValue(key, out var cached) && cached != null) return cached;
            try
            {
                string res = null;
                foreach (var resourceName in asm.GetManifestResourceNames())
                    if (resourceName.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase)) { res = resourceName; break; }
                if (res == null) { FiresCore.Logging.FiresLogger.LogError($"[FiresMinigame] sprite '{endsWith}' not embedded in {asm.GetName().Name}."); return null; }

                byte[] data;
                using (var stream = asm.GetManifestResourceStream(res))
                using (var ms = new System.IO.MemoryStream()) { stream.CopyTo(ms); data = ms.ToArray(); }

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!LoadImageReflect(tex, data)) return null;
                tex.wrapMode = TextureWrapMode.Clamp; tex.filterMode = FilterMode.Bilinear;
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), pixelsPerUnit, 0, SpriteMeshType.FullRect, border);
                _cache[key] = sprite;
                return sprite;
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogError($"[FiresMinigame] sprite load '{endsWith}' failed: {ex.Message}"); return null; }
        }

        private static bool LoadImageReflect(Texture2D tex, byte[] data)
        {
            try
            {
                if (!_loadImageResolved)
                {
                    _loadImageResolved = true;
                    Type conversionType = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                    if (conversionType == null)
                        foreach (var loadedAssembly in AppDomain.CurrentDomain.GetAssemblies()) { conversionType = loadedAssembly.GetType("UnityEngine.ImageConversion"); if (conversionType != null) break; }
                    if (conversionType != null)
                        _loadImage = conversionType.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) })
                                     ?? conversionType.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                }
                if (_loadImage == null) { FiresCore.Logging.FiresLogger.LogError("[FiresMinigame] ImageConversion.LoadImage not found"); return false; }
                var args = _loadImage.GetParameters().Length == 3 ? new object[] { tex, data, false } : new object[] { tex, data };
                var loaded = _loadImage.Invoke(null, args);
                return !(loaded is bool succeeded) || succeeded;
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogError($"[FiresMinigame] LoadImage reflect failed: {ex.Message}"); return false; }
        }
    }
}
