using System;
using System.Reflection;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Materials
{
    // One-time static capture of a fully-finalized vanilla Heightmap material.
    // sharedMaterial alone is missing per-instance state (cornerBiomes, splat
    // texture array bindings) that RebuildRenderMesh writes onto m_material;
    // cloning sharedMaterial leaves clones rendering as invisible / flat-color.
    //
    // Capture happens on the FIRST Heightmap.RebuildRenderMesh postfix (or
    // earlier fallbacks). We clone the captured material, force Meadows
    // cornerBiomes defaults, bind a 1×1 "no paint" mask, and stash it for
    // the rest of the session. Recapture is callable for HD-shader mods
    // that finish swapping after our first capture.
    //
    // Shader detection uses HasProperty("_ClearedMaskTex") rather than a
    // shader-name literal so replacement shaders (ValheimHDTerrain etc.)
    // are still recognized as heightmap shaders.
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
        private const string StandardShaderName = "Standard";
        private const string SpritesDefaultShaderName = "Sprites/Default";
        private const int CornerCount = 4;
        private const int PaintMaskSize = 1;

        private static readonly Color FallbackTileColor = new Color(0.35f, 0.55f, 0.25f, 1f);
        private static readonly Vector2 UnitTextureScale = Vector2.one;
        private static readonly Vector2 ZeroTextureOffset = Vector2.zero;

        private static readonly FieldInfo s_heightmapMaterialField =
            typeof(Heightmap).GetField("m_material",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly int s_ClearedMaskTexProp = Shader.PropertyToID(ClearedMaskTexPropertyName);

        private static Material s_cachedMaterial;
        private static Material s_fallbackMaterial;
        private static Texture2D s_paintMaskNothing;

        public static bool HasMaterial => s_cachedMaterial != null;

        public static Material GetCurrentMaterial()
        {
            if (s_cachedMaterial != null) return s_cachedMaterial;
            if (s_fallbackMaterial == null) BuildFallback();
            return s_fallbackMaterial;
        }

        // Manual force-capture entry. Called by console commands for
        // emergency re-scans (e.g. player teleported to an admin yard
        // before any vanilla terrain loaded).
        public static int ForceCaptureFromScene()
        {
            if (s_cachedMaterial != null) return 0;
            int scanned = 0;
            var heightmaps = UnityEngine.Object.FindObjectsByType<Heightmap>(FindObjectsSortMode.None);
            foreach (var hm in heightmaps)
            {
                if (hm == null) continue;
                scanned++;
                TryCaptureFrom(hm);
                if (s_cachedMaterial != null) break;
            }
            return scanned;
        }

        public static int ForceRecaptureFromScene()
        {
            s_cachedMaterial = null;
            return ForceCaptureFromScene();
        }

        private static void TryCaptureFrom(Heightmap hm)
        {
            if (s_cachedMaterial != null) return;
            if (hm == null) return;

            Material source = ResolveSourceMaterial(hm);
            if (source == null || source.shader == null) return;
            if (!IsHeightmapShader(source)) return;

            var mat = CloneAndConfigure(source);
            UnityEngine.Object.DontDestroyOnLoad(mat);
            s_cachedMaterial = mat;

            FiresLogger.LogInfo(
                $"{LogPrefix} Captured heightmap material from {hm.name} | " +
                $"shader='{source.shader.name}' (per-instance m_material clone). " +
                $"Meadows defaults applied.");
        }

        private static Material ResolveSourceMaterial(Heightmap hm)
        {
            try
            {
                if (s_heightmapMaterialField != null)
                {
                    var fromField = s_heightmapMaterialField.GetValue(hm) as Material;
                    if (fromField != null) return fromField;
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} m_material reflection read threw on {hm.name}: {ex.Message}");
            }

            var rend = hm.GetComponent<MeshRenderer>();
            return rend != null ? rend.sharedMaterial : null;
        }

        // Heightmap shaders (vanilla + replacements) expose _ClearedMaskTex
        // for the per-tile paint mask; LOD-distance variants do not. Using
        // the property check instead of the shader-name literal keeps us
        // compatible with HD-terrain mods that swap the shader.
        private static bool IsHeightmapShader(Material source)
        {
            if (source.shader.name == VanillaHeightmapShaderName) return true;
            return source.HasProperty(s_ClearedMaskTexProp);
        }

        private static Material CloneAndConfigure(Material source)
        {
            var mat = new Material(source) { name = CapturedMaterialName };
            BindPaintMaskNothing(mat);
            ResetTextureTransform(mat, ClearedMaskTexPropertyName, ClearedMaskTexStPropertyName);
            ResetTextureTransform(mat, MainTexPropertyName, MainTexPropertyName + "_ST");
            ApplyMeadowsDefault(mat);
            return mat;
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
                if (s_cachedMaterial != null) return;
                ForceCaptureFromScene();
            }
        }
    }
}
