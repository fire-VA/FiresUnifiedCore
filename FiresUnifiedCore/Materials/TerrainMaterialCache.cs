using System;
using System.Reflection;
using FiresCore.Lifecycle;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Materials
{
    // The one shared terrain material for mod-built terrain surfaces (tiles, voxel chunks, lod quilts).
    // sharedMaterial lacks the per-instance state RebuildRenderMesh adds (corner biomes, splat bindings), so
    // clones of it render invisible; this clones the material from the first near RebuildRenderMesh, sets
    // Meadows corner-biome defaults and a blank paint mask, and keeps it for the session. Recapture exists for
    // HD shader mods that swap later, and heightmap shaders are recognized by their _ClearedMaskTex property
    // rather than by name. Headless servers render nothing, so they never capture.
    public static class TerrainMaterialCache
    {
        private const string LogPrefix = "[TerrainMaterialCache]";
        private const string CapturedMaterialName = "FiresUnifiedCore_TileMaterial";
        private const string PaintMaskTextureName = "FiresUnifiedCore_PaintMaskNothing";
        private const string FallbackStandardName = "FiresUnifiedCore_TileFallback_Standard";
        private const string FallbackSpriteName = "FiresUnifiedCore_TileFallback_Sprite";
        private const string VanillaHeightmapShaderName = "Custom/Heightmap";
        private const string ClearedMaskTexPropertyName = "_ClearedMaskTex";
        private const string ClearedMaskTexStPropertyName = "_ClearedMaskTex_ST";
        private const string MainTexPropertyName = "_MainTex";
        private const string MeadowsScalarPropertyName = "_BiomeMeadows";
        private const string DistantLodKeyword = "_ISDISTANTLOD_ON";
        private const string DistantLodPropertyName = "_IsDistantLod";
        private const string LodHideDistancePropertyName = "_LodHideDistance";
        private const string StandardShaderName = "Standard";
        private const string SpritesDefaultShaderName = "Sprites/Default";
        private const int CornerCount = 4;
        private const int PaintMaskSize = 1;
        // Vanilla's near material dither-discards past _LodHideDistance (~180m) because vanilla hands off to its
        // distant lod there. Mod surfaces render to the horizon, so the discard is pushed out of reach.
        private const float NoLodHideDistance = 100000f;
        private const float LodHideDistanceFloor = 10000f;

        private static readonly Color FallbackTileColor = new Color(0.35f, 0.55f, 0.25f, 1f);
        private static readonly Vector2 UnitTextureScale = Vector2.one;
        private static readonly Vector2 ZeroTextureOffset = Vector2.zero;

        private static readonly FieldInfo s_heightmapMaterialField =
            typeof(Heightmap).GetField("m_material",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly int s_ClearedMaskTexProp = Shader.PropertyToID(ClearedMaskTexPropertyName);
        private static readonly int s_LodHideDistanceProp = Shader.PropertyToID(LodHideDistancePropertyName);

        private static Material s_cachedMaterial;
        private static Material s_fallbackMaterial;
        private static Texture2D s_paintMaskNothing;
        private static Shader s_shaderOverride;
        private static Shader s_loggedShaderOverride;

        public static bool HasMaterial => s_cachedMaterial != null;

        // Raised after every capture (recaptures included) with the new shared material, so a mod can move
        // renderers that still hold the fallback or the previous capture in one pass.
        public static event Action<Material> MaterialCaptured;

        // A replacement for the shared material's shader. Its property names must match the vanilla heightmap
        // shader so the captured textures and values carry over. Applied on the next GetCurrentMaterial.
        public static void SetShaderOverride(Shader shader) => s_shaderOverride = shader;

        public static Material GetCurrentMaterial()
        {
            if (s_cachedMaterial != null)
            {
                ApplyShaderOverride(s_cachedMaterial);
                DisableLodHideDistance(s_cachedMaterial);
                return s_cachedMaterial;
            }
            if (s_fallbackMaterial == null) BuildFallback();
            return s_fallbackMaterial;
        }

        // Manual force-capture entry. Called by console commands for
        // emergency re-scans (e.g. player teleported to an admin yard
        // before any vanilla terrain loaded).
        public static int ForceCaptureFromScene()
        {
            if (s_cachedMaterial != null || FiresMod.IsHeadless) return 0;
            int scanned = 0;
            var heightmaps = UnityEngine.Object.FindObjectsByType<Heightmap>(FindObjectsSortMode.None);
            foreach (var heightmap in heightmaps)
            {
                if (heightmap == null) continue;
                scanned++;
                TryCaptureFrom(heightmap);
                if (s_cachedMaterial != null) break;
            }
            return scanned;
        }

        public static int ForceRecaptureFromScene()
        {
            s_cachedMaterial = null;
            return ForceCaptureFromScene();
        }

        private static void TryCaptureFrom(Heightmap heightmap)
        {
            if (s_cachedMaterial != null) return;
            if (heightmap == null || FiresMod.IsHeadless) return;

            Material source = ResolveSourceMaterial(heightmap);
            if (source == null || source.shader == null) return;
            if (!IsHeightmapShader(source)) return;
            if (IsDistantLodMaterial(source))
            {
                FiresLogger.LogVerbose($"{LogPrefix} Skipping distant-lod heightmap material from {heightmap.name}; waiting for a near material.");
                return;
            }

            var mat = CloneAndConfigure(source);
            UnityEngine.Object.DontDestroyOnLoad(mat);
            s_cachedMaterial = mat;

            FiresLogger.LogInfo(
                $"{LogPrefix} Captured heightmap material from {heightmap.name} | " +
                $"shader='{source.shader.name}' (per-instance m_material clone). " +
                $"Meadows defaults applied.");

            RaiseMaterialCaptured(GetCurrentMaterial());
        }

        private static void RaiseMaterialCaptured(Material material)
        {
            var handlers = MaterialCaptured;
            if (handlers == null) return;
            foreach (Action<Material> handler in handlers.GetInvocationList())
            {
                try { handler(material); }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"{LogPrefix} MaterialCaptured handler {handler.Method.DeclaringType?.FullName}.{handler.Method.Name} threw: {ex.Message}");
                }
            }
        }

        private static Material ResolveSourceMaterial(Heightmap heightmap)
        {
            try
            {
                if (s_heightmapMaterialField != null)
                {
                    var fromField = s_heightmapMaterialField.GetValue(heightmap) as Material;
                    if (fromField != null) return fromField;
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} m_material reflection read threw on {heightmap.name}: {ex.Message}");
            }

            var rend = heightmap.GetComponent<MeshRenderer>();
            return rend != null ? rend.sharedMaterial : null;
        }

        // Heightmap shaders (vanilla + replacements) expose _ClearedMaskTex
        // for the per-tile paint mask. Using the property check instead of
        // the shader-name literal keeps us compatible with HD-terrain mods
        // that swap the shader.
        private static bool IsHeightmapShader(Material source)
        {
            if (source.shader.name == VanillaHeightmapShaderName) return true;
            return source.HasProperty(s_ClearedMaskTexProp);
        }

        // The vanilla distant-lod material also has _ClearedMaskTex (with _UVScale 0.2), so it passes the
        // heightmap check; capturing it put 2.5x-frequency textures on every voxel chunk and tile.
        private static bool IsDistantLodMaterial(Material source)
        {
            return source.IsKeywordEnabled(DistantLodKeyword)
                || (source.HasProperty(DistantLodPropertyName) && source.GetFloat(DistantLodPropertyName) > 0.5f);
        }

        private static Material CloneAndConfigure(Material source)
        {
            var mat = new Material(source) { name = CapturedMaterialName };
            BindPaintMaskNothing(mat);
            ResetTextureTransform(mat, ClearedMaskTexPropertyName, ClearedMaskTexStPropertyName);
            ResetTextureTransform(mat, MainTexPropertyName, MainTexPropertyName + "_ST");
            DisableLodHideDistance(mat);
            ApplyMeadowsDefault(mat);
            return mat;
        }

        private static void ApplyShaderOverride(Material mat)
        {
            if (s_shaderOverride == null || mat.shader == s_shaderOverride) return;
            mat.shader = s_shaderOverride;
            if (s_loggedShaderOverride == s_shaderOverride) return;
            s_loggedShaderOverride = s_shaderOverride;
            FiresLogger.LogInfo($"{LogPrefix} Shared terrain material now uses shader '{s_shaderOverride.name}'.");
        }

        private static void DisableLodHideDistance(Material mat)
        {
            if (mat.HasProperty(s_LodHideDistanceProp) && mat.GetFloat(s_LodHideDistanceProp) < LodHideDistanceFloor)
                mat.SetFloat(s_LodHideDistanceProp, NoLodHideDistance);
        }

        private static void BindPaintMaskNothing(Material mat)
        {
            if (s_paintMaskNothing == null) s_paintMaskNothing = BuildPaintMaskNothing();
            if (mat.HasProperty(s_ClearedMaskTexProp))
                mat.SetTexture(s_ClearedMaskTexProp, s_paintMaskNothing);
        }

        private static void ResetTextureTransform(Material mat, string textureName, string scaleOffsetPropertyName)
        {
            if (!mat.HasProperty(scaleOffsetPropertyName)) return;
            mat.SetTextureScale(textureName, UnitTextureScale);
            mat.SetTextureOffset(textureName, ZeroTextureOffset);
        }

        // Overrides whatever cornerBiomes the source heightmap had so the
        // captured material renders uniform Meadows green regardless of
        // where the player was standing when capture fired.
        private static void ApplyMeadowsDefault(Material mat)
        {
            float meadows = (float)(int)Heightmap.Biome.Meadows;

            for (int i = 0; i < CornerCount; i++)
            {
                string prop = $"_Biome{i}";
                if (mat.HasProperty(prop)) mat.SetFloat(prop, meadows);
            }
            if (mat.HasProperty(MeadowsScalarPropertyName))
                mat.SetFloat(MeadowsScalarPropertyName, 1f);

            for (int i = 1; i <= CornerCount; i++)
            {
                string prop = $"_CornerBiomes{i}";
                if (mat.HasProperty(prop)) mat.SetFloat(prop, meadows);
            }
        }

        private static Texture2D BuildPaintMaskNothing()
        {
            var tex = new Texture2D(PaintMaskSize, PaintMaskSize, TextureFormat.RGBA32, mipChain: false)
            {
                name = PaintMaskTextureName,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixel(0, 0, Heightmap.m_paintMaskNothing);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return tex;
        }

        private static void BuildFallback()
        {
            var stdShader = Shader.Find(StandardShaderName);
            if (stdShader != null)
            {
                s_fallbackMaterial = new Material(stdShader)
                {
                    name = FallbackStandardName,
                    color = FallbackTileColor,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                return;
            }

            var spriteShader = Shader.Find(SpritesDefaultShaderName);
            if (spriteShader != null)
            {
                s_fallbackMaterial = new Material(spriteShader)
                {
                    name = FallbackSpriteName,
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
        }

        // Multiple capture postfixes maximize the chance of an early hit;
        // each short-circuits once s_cachedMaterial is set so they're cheap.

        [HarmonyPatch(typeof(Heightmap), "Awake")]
        private static class Heightmap_Awake_Capture
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Heightmap __instance)
            {
                if (s_cachedMaterial != null) return;
                TryCaptureFrom(__instance);
            }
        }

        [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
        private static class Heightmap_RebuildRenderMesh_Capture
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Heightmap __instance)
            {
                if (s_cachedMaterial != null) return;
                TryCaptureFrom(__instance);
            }
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
        private static class ZNetScene_Awake_ScanHeightmaps
        {
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            private static void Postfix()
            {
                VanillaShaderRebind.RebindAllLoadedMaterials(FiresUnifiedCore.PluginName);
                if (s_cachedMaterial != null) return;
                ForceCaptureFromScene();
            }
        }
    }
}
