using System;
using System.Collections.Generic;
using System.IO;
using FiresCore.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Materials
{
    // The one live copy of each of our custom shaders. A shader two or more mods use ships once, in Core's own
    // "firescoreshaders" bundle; a shader only one mod uses stays in that mod's bundle and is offered here, so
    // FiresTossinShade can list every live one without depending on the mods that carry them. Nothing loads until a mod
    // asks for it, nothing loads without a GPU, and the broker never changes a material: swapping vanilla materials stays
    // with the mod that wants the swap, so nothing is swapped when that mod is not installed.
    public static class FiresShaderBroker
    {
        public const string CoreBundleName = "firescoreshaders";
        private const string CoreSource = "FiresUnifiedCore";
        private const string LogTag = "[FiresShaders]";

        public sealed class LiveShader
        {
            public string Name { get; internal set; }
            public Shader Shader { get; internal set; }
            public string Source { get; internal set; }
        }

        private sealed class ModOffer
        {
            internal string Mod;
            internal Func<Shader> Load;
        }

        private static readonly Dictionary<string, LiveShader> s_live = new Dictionary<string, LiveShader>(StringComparer.Ordinal);
        private static readonly List<LiveShader> s_liveList = new List<LiveShader>();
        private static readonly Dictionary<string, List<ModOffer>> s_offers = new Dictionary<string, List<ModOffer>>(StringComparer.Ordinal);
        private static readonly HashSet<string> s_reportedMissing = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> s_coreAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> s_coreTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Texture2D> s_textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static AssetBundle s_coreBundle;
        private static bool s_coreBundleTried;

        // Raised once per shader, the first time it goes live; FiresTossinShade hooks its sliders here.
        public static event Action<LiveShader> ShaderLoaded;

        public static IReadOnlyList<LiveShader> Live => s_liveList;

        // A mod's own shader, loaded by `load` the first time any mod asks for `shaderName`. A shader Core ships is
        // always Core's copy; among mods the first offer that loads wins.
        public static void Offer(string shaderName, string mod, Func<Shader> load)
        {
            if (string.IsNullOrEmpty(shaderName) || load == null) return;
            if (!s_offers.TryGetValue(shaderName, out var offers)) s_offers[shaderName] = offers = new List<ModOffer>(1);
            offers.Add(new ModOffer { Mod = mod ?? "unnamed mod", Load = load });
        }

        // The live copy of `shaderName` (e.g. "Custom/FiresWater"), loaded on first use: from Core's bundle when Core
        // ships it, else from the first mod that offered it. Null without a GPU, or when nothing installed carries it.
        public static Shader Get(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return null;
            if (s_live.TryGetValue(shaderName, out var live) && live.Shader != null) return live.Shader;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return null;

            string source = CoreSource;
            var shader = FromPreferredBundle(shaderName, ref source);
            if (shader == null) shader = FromCoreBundle(shaderName);
            if (shader == null && s_offers.TryGetValue(shaderName, out var offers))
            {
                foreach (var offer in offers)
                {
                    shader = FromOffer(offer, shaderName);
                    if (shader == null) continue;
                    source = offer.Mod;
                    break;
                }
            }
            if (shader == null)
            {
                if (s_reportedMissing.Add(shaderName))
                    FiresLogger.LogWarning($"{LogTag} {shaderName} is not in Core's shader bundle and no installed mod offers it; whatever asked draws without it.");
                return null;
            }

            live = new LiveShader { Name = shaderName, Shader = shader, Source = source };
            s_live[shaderName] = live;
            s_liveList.Add(live);
            FiresLogger.LogDebug($"{LogTag} {shaderName} is live from {source}" + (shader.isSupported ? "." : ", but this GPU does not support it."));
            try { ShaderLoaded?.Invoke(live); }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogTag} a ShaderLoaded listener threw for {shaderName}: {ex.GetType().Name}: {ex.Message}"); }
            return shader;
        }

        // A texture Core's shader bundle ships beside the shaders that use it (e.g. "FiresFoamHD", "water_nrm"). Null
        // without a GPU, or when this Core does not ship it.
        public static Texture2D GetTexture(string textureName)
        {
            if (string.IsNullOrEmpty(textureName)) return null;
            if (s_textures.TryGetValue(textureName, out var texture) && texture != null) return texture;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return null;
            var bundle = CoreBundle();
            texture = bundle != null && s_coreTextures.Contains(textureName) ? bundle.LoadAsset<Texture2D>(textureName) : null;
            // 0.2.949 ([worldgen]'s ice look, FAT 1181): textures shipped inside a preferred shader bundle (the ice textures in firesinlandwater)
            if (texture == null && s_preferredTextures.TryGetValue(textureName, out string owner) && s_preferred.TryGetValue(owner, out var p))
            {
                if (!p.Tried) { p.Tried = true; p.Loaded = LoadEmbeddedBundle(p.Bundle); }
                if (p.Loaded != null) texture = p.Loaded.LoadAsset<Texture2D>(textureName);
                if (texture != null) FiresLogger.LogDebug($"{LogTag} texture '{textureName}' from the {p.Bundle} bundle");
            }
            if (texture == null)
            {
                if (s_reportedMissing.Add(textureName))
                    FiresLogger.LogWarning($"{LogTag} texture '{textureName}' is not in Core's shader bundle.");
                return null;
            }
            s_textures[textureName] = texture;
            FiresLogger.LogDebug($"{LogTag} texture '{textureName}' ({texture.width}x{texture.height}) is live from {CoreSource}.");
            return texture;
        }

        // A cubemap from Core's shader bundle; null when absent. Add-only.
        private static readonly Dictionary<string, Cubemap> s_cubemaps = new Dictionary<string, Cubemap>(StringComparer.Ordinal);
        public static Cubemap GetCubemap(string textureName)
        {
            if (string.IsNullOrEmpty(textureName)) return null;
            if (s_cubemaps.TryGetValue(textureName, out var cube) && cube != null) return cube;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return null;
            var bundle = CoreBundle();
            cube = bundle != null && s_coreTextures.Contains(textureName) ? bundle.LoadAsset<Cubemap>(textureName) : null;
            if (cube == null)
            {
                if (s_reportedMissing.Add("cube:" + textureName))
                    FiresLogger.LogWarning($"{LogTag} cubemap '{textureName}' is not in Core's shader bundle.");
                return null;
            }
            s_cubemaps[textureName] = cube;
            FiresLogger.LogInfo($"{LogTag} cubemap '{textureName}' ({cube.width}) is live from {CoreSource}.");
            return cube;
        }

        private static Shader FromCoreBundle(string shaderName)
        {
            var bundle = CoreBundle();
            if (bundle == null) return null;
            string assetName = shaderName.Substring(shaderName.LastIndexOf('/') + 1);
            if (!s_coreAssets.Contains(assetName)) return null;
            var shader = bundle.LoadAsset<Shader>(assetName);
            if (shader != null && shader.name == shaderName) return shader;
            FiresLogger.LogWarning($"{LogTag} Core's shader bundle lists '{assetName}' but it loads as {(shader == null ? "nothing" : shader.name)}, not {shaderName}.");
            return null;
        }

        // 0.2.850: a fixed shader can ship in its own small bundle (built alone in the LINEAR project) so firescoreshaders, and every
        // other shader in it, stays byte-identical. Core loads it from there first and falls back to the firescoreshaders copy.
        private sealed class Preferred
        {
            internal string Bundle, Version, Tag;
            internal AssetBundle Loaded;
            internal bool Tried;
        }

        private static readonly Dictionary<string, Preferred> s_preferred = new Dictionary<string, Preferred>(StringComparer.Ordinal)
        {
            // Fire 10-26 ([seasons]' proof): inland water glowed at night and read as haze by day. v5 = v4 + the tint and foam lit by
            // the scene + a 0.06 Fresnel F0 floor.
            ["Custom/FiresInlandWater"] = new Preferred { Bundle = "firesinlandwater", Version = "v10 (v9 + floating ice chips in forming ice, the ice body only on solid)", Tag = "[InlandWater]" },
        };

        // 0.2.949: textures a preferred bundle ships with its shader -> that shader's key (GetTexture looks there after Core's bundle)
        private static readonly Dictionary<string, string> s_preferredTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FatIceInner"] = "Custom/FiresInlandWater", ["FatIceSurfNormal"] = "Custom/FiresInlandWater",
        };

        private static Shader FromPreferredBundle(string shaderName, ref string source)
        {
            if (!s_preferred.TryGetValue(shaderName, out var p)) return null;
            if (!p.Tried)
            {
                p.Tried = true;
                p.Loaded = LoadEmbeddedBundle(p.Bundle);
            }
            Shader shader = null;
            if (p.Loaded != null)
            {
                shader = p.Loaded.LoadAsset<Shader>(shaderName.Substring(shaderName.LastIndexOf('/') + 1));
                if (shader != null && shader.name != shaderName) shader = null;
            }
            if (shader != null)
            {
                source = CoreSource + " (" + p.Bundle + ")";
                FiresLogger.LogDebug($"{p.Tag} shader source: {p.Bundle} {p.Version}");
            }
            else FiresLogger.LogDebug($"{p.Tag} shader source: {CoreBundleName} (fallback: {(p.Loaded == null ? "no " + p.Bundle + " bundle in this Core" : p.Bundle + " has no " + shaderName)})");
            return shader;
        }

        private static AssetBundle LoadEmbeddedBundle(string bundleName)
        {
            var assembly = typeof(FiresShaderBroker).Assembly;
            string resource = null;
            foreach (var name in assembly.GetManifestResourceNames())
                if (name == bundleName || name.EndsWith("." + bundleName, StringComparison.Ordinal)) resource = name;
            if (resource == null) return null;
            try
            {
                using (var stream = assembly.GetManifestResourceStream(resource))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return AssetBundle.LoadFromMemory(memory.ToArray());
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogTag} bundle '{bundleName}' failed to load: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static Shader FromOffer(ModOffer offer, string shaderName)
        {
            try
            {
                var shader = offer.Load();
                if (shader == null || shader.name == shaderName) return shader;
                FiresLogger.LogWarning($"{LogTag} {offer.Mod} offered {shaderName} but handed over {shader.name}; ignored.");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogTag} {offer.Mod} failed to load its {shaderName}: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }

        private static AssetBundle CoreBundle()
        {
            if (s_coreBundleTried) return s_coreBundle;
            s_coreBundleTried = true;
            var assembly = typeof(FiresShaderBroker).Assembly;
            string resource = null;
            foreach (var name in assembly.GetManifestResourceNames())
                if (name == CoreBundleName || name.EndsWith("." + CoreBundleName, StringComparison.Ordinal)) resource = name;
            if (resource == null)
            {
                FiresLogger.LogWarning($"{LogTag} this Core carries no '{CoreBundleName}' bundle; shared shaders come only from mods that offer them.");
                return null;
            }
            try
            {
                using (var stream = assembly.GetManifestResourceStream(resource))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    s_coreBundle = AssetBundle.LoadFromMemory(memory.ToArray());
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogTag} Core's shader bundle failed to load: {ex.GetType().Name}: {ex.Message}");
            }
            if (s_coreBundle == null) return null;
            foreach (var path in s_coreBundle.GetAllAssetNames())
            {
                if (path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)) s_coreAssets.Add(Path.GetFileNameWithoutExtension(path));
                else s_coreTextures.Add(Path.GetFileNameWithoutExtension(path));
            }
            FiresLogger.LogDebug($"{LogTag} Core's shader bundle is loaded: shaders {string.Join(", ", s_coreAssets)}; textures {string.Join(", ", s_coreTextures)}.");
            return s_coreBundle;
        }
    }
}
