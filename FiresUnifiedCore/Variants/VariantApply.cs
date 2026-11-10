using System;
using System.Collections.Generic;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Variants
{
    /// <summary>
    /// Puts a variant on one instance (a pet clone, a mount) and takes it off again (Tools\FIRES_VARIANTS_PLAN.md sections 2-4).
    /// - Colour variant: a MaterialPropertyBlock per body renderer on vanilla Custom/Creature's own knobs (_Color, _Hue,
    ///   _Saturation, _Value, _EmissionColor). No material copies; values add to whatever block was there (star levels).
    /// - Material variant: the body renderers' own sharedMaterials slots point at the vanilla variant material. The vanilla
    ///   material is never written.
    /// - Body particle systems, lights and trails under the root are recoloured toward the FX colour (VariantFx).
    /// Clear restores exactly what was on the renderers WHEN Apply ran (FiresMounts re-applies a horse coat between Clear
    /// and Apply), including each renderer's original property block and each particle system's original colours.
    /// The root is registered so its attacks' FX follow it (VariantHooks).
    /// </summary>
    public static class VariantApply
    {
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int HueId = Shader.PropertyToID("_Hue");
        private static readonly int SaturationId = Shader.PropertyToID("_Saturation");
        private static readonly int ValueId = Shader.PropertyToID("_Value");
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        /// <summary>The variant on this root ("" when none).</summary>
        public static string Current(GameObject root)
        {
            VariantState state = root != null ? root.GetComponent<VariantState>() : null;
            return state != null ? state.Id : "";
        }

        /// <summary>
        /// Applies variant <paramref name="id"/> to <paramref name="root"/>; null, "" or the reset id just clears.
        /// <paramref name="isBody"/> picks the body renderers (null = every mesh renderer under the root). Particle,
        /// trail and line renderers are never body. Returns false when the id is unknown or nothing could be drawn.
        /// </summary>
        public static bool Apply(GameObject root, string id, Func<Renderer, bool> isBody = null)
        {
            if (root == null) return false;
            if (Current(root) == (id ?? "")) return true;
            Clear(root);
            if (!VariantTable.TryGet(id, out VariantDef def)) return string.IsNullOrEmpty(id) || id == VariantTable.Clear;

            Material swap = null;
            if (!string.IsNullOrEmpty(def.Material))
            {
                swap = FindMaterial(def.Material);
                if (swap == null)
                {
                    FiresLogger.LogDebug($"[Variants] {root.name}: variant '{def.Id}' needs material '{def.Material}', not loaded; left as is.");
                    return false;
                }
            }

            VariantState state = root.AddComponent<VariantState>();
            state.Id = def.Id;
            int bodies = 0;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (isBody != null && !isBody(r)) continue;
                bodies++;
                var saved = new VariantState.SavedRenderer { Renderer = r, Materials = r.sharedMaterials };
                if (r.HasPropertyBlock())
                {
                    saved.Block = new MaterialPropertyBlock();
                    r.GetPropertyBlock(saved.Block);
                }
                state.Renderers.Add(saved);

                if (swap != null)
                {
                    var mats = new Material[saved.Materials.Length];
                    for (int i = 0; i < mats.Length; i++) mats[i] = swap;
                    r.sharedMaterials = mats;
                    r.SetPropertyBlock(null);
                    continue;
                }

                var block = new MaterialPropertyBlock();
                if (saved.Block != null) r.GetPropertyBlock(block);
                Material mat = r.sharedMaterial;
                if (mat != null && mat.HasProperty(ColorId)) block.SetColor(ColorId, mat.GetColor(ColorId) * def.Tint);
                if (mat != null && mat.HasProperty(HueId)) block.SetFloat(HueId, block.GetFloat(HueId) + def.Hue);
                if (mat != null && mat.HasProperty(SaturationId)) block.SetFloat(SaturationId, block.GetFloat(SaturationId) + def.Saturation);
                if (mat != null && mat.HasProperty(ValueId)) block.SetFloat(ValueId, block.GetFloat(ValueId) + def.Value);
                if (def.HasEmission && mat != null && mat.HasProperty(EmissionId))
                {
                    block.SetColor(EmissionId, def.Emission);
                    // Vanilla emission maps are coloured (the Moder's is cyan); the shader multiplies map x colour, so a red
                    // glow on them came out near black. A greyscale copy lets any colour show true.
                    Texture map = mat.HasProperty(EmissionMapId) ? mat.GetTexture(EmissionMapId) : null;
                    Texture grey = map != null ? Greyscale(map) : null;
                    if (grey != null) block.SetTexture(EmissionMapId, grey);
                }
                r.SetPropertyBlock(block);
            }

            // Body FX (the Fader's chest fire): their colours are kept so Clear can put them back.
            foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystem.MainModule main = ps.main;
                state.Particles.Add(new VariantState.SavedParticles
                {
                    System = ps,
                    Start = main.startColor,
                    Life = ps.colorOverLifetime.color,
                    Speed = ps.colorBySpeed.color,
                });
            }
            foreach (Light light in root.GetComponentsInChildren<Light>(true)) state.Lights.Add((light, light.color));
            foreach (TrailRenderer trail in root.GetComponentsInChildren<TrailRenderer>(true)) state.Trails.Add((trail, trail.colorGradient));
            foreach (LineRenderer line in root.GetComponentsInChildren<LineRenderer>(true)) state.Lines.Add((line, line.colorGradient));
            VariantFx.Recolour(root, def.FxColor);

            VariantFx.Register(root.transform, def.FxColor);
            FiresLogger.LogDebug($"[Variants] {root.name}: variant '{def.Id}' applied to {bodies} body renderer(s)"
                + (swap != null ? $" (material {swap.name})" : "") + $", {state.Particles.Count} particle system(s) recoloured.");
            return bodies > 0;
        }

        /// <summary>Takes the variant off: every renderer, particle system and light back to what it was at Apply.</summary>
        public static void Clear(GameObject root)
        {
            VariantState state = root != null ? root.GetComponent<VariantState>() : null;
            if (state == null) return;
            state.Restore();
            UnityEngine.Object.DestroyImmediate(state);
        }

        private static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");
        private static readonly Dictionary<int, Texture2D> GreyMaps = new Dictionary<int, Texture2D>();

        /// <summary>
        /// A luminance-only copy of a texture, made once and cached. Vanilla textures aren't CPU-readable, so it goes through
        /// the GPU: a blit to a temporary RenderTexture, read back, converted, uploaded. Null if that fails (then the map stays).
        /// </summary>
        private static Texture2D Greyscale(Texture source)
        {
            // A headless dedicated server draws nothing, so no GPU blit/readback there.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return null;
            int key = source.GetInstanceID();
            if (GreyMaps.TryGetValue(key, out Texture2D cached)) return cached;
            Texture2D grey = null;
            RenderTexture previous = RenderTexture.active;
            RenderTexture rt = null;
            try
            {
                rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                grey = new Texture2D(source.width, source.height, TextureFormat.RGBA32, true) { name = source.name + "_fires_grey" };
                grey.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                Color32[] pixels = grey.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 p = pixels[i];
                    byte l = (byte)Mathf.Clamp(0.299f * p.r + 0.587f * p.g + 0.114f * p.b, 0f, 255f);
                    pixels[i] = new Color32(l, l, l, p.a);
                }
                grey.SetPixels32(pixels);
                grey.Apply(true, true);
                FiresLogger.LogDebug($"[Variants] greyscale emission map made for '{source.name}' ({source.width}x{source.height}).");
            }
            catch (Exception e)
            {
                FiresLogger.LogDebug($"[Variants] greyscale emission map for '{source.name}' failed: {e.Message}; the coloured map stays.");
                grey = null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
            GreyMaps[key] = grey;
            return grey;
        }

        private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>(StringComparer.Ordinal);

        /// <summary>A loaded vanilla material by name (the boss aspects', the ghost's), found once and cached.</summary>
        private static Material FindMaterial(string name)
        {
            if (Materials.TryGetValue(name, out Material found) && found != null) return found;
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.name == name) { found = m; break; }
            if (found != null) Materials[name] = found;
            return found;
        }
    }

    /// <summary>What a variant changed on one root, so Clear can put it back exactly. Unregisters the root when destroyed.</summary>
    public sealed class VariantState : MonoBehaviour
    {
        internal string Id = "";
        internal readonly List<SavedRenderer> Renderers = new List<SavedRenderer>();
        internal readonly List<SavedParticles> Particles = new List<SavedParticles>();
        internal readonly List<(Light light, Color color)> Lights = new List<(Light, Color)>();
        internal readonly List<(TrailRenderer trail, Gradient gradient)> Trails = new List<(TrailRenderer, Gradient)>();
        internal readonly List<(LineRenderer line, Gradient gradient)> Lines = new List<(LineRenderer, Gradient)>();

        internal sealed class SavedRenderer
        {
            public Renderer Renderer;
            public Material[] Materials;
            public MaterialPropertyBlock Block;
        }

        internal sealed class SavedParticles
        {
            public ParticleSystem System;
            public ParticleSystem.MinMaxGradient Start, Life, Speed;
        }

        internal void Restore()
        {
            foreach (SavedRenderer s in Renderers)
            {
                if (s.Renderer == null) continue;
                s.Renderer.sharedMaterials = s.Materials;
                s.Renderer.SetPropertyBlock(s.Block);
            }
            foreach (SavedParticles p in Particles)
            {
                if (p.System == null) continue;
                ParticleSystem.MainModule main = p.System.main;
                main.startColor = p.Start;
                ParticleSystem.ColorOverLifetimeModule life = p.System.colorOverLifetime;
                life.color = p.Life;
                ParticleSystem.ColorBySpeedModule speed = p.System.colorBySpeed;
                speed.color = p.Speed;
                ParticleSystemRenderer pr = p.System.GetComponent<ParticleSystemRenderer>();
                if (pr != null) pr.SetPropertyBlock(null);
            }
            foreach ((Light light, Color color) in Lights) if (light != null) light.color = color;
            foreach ((TrailRenderer trail, Gradient gradient) in Trails) if (trail != null) trail.colorGradient = gradient;
            foreach ((LineRenderer line, Gradient gradient) in Lines) if (line != null) line.colorGradient = gradient;
            Renderers.Clear();
            Particles.Clear();
            Lights.Clear();
            Trails.Clear();
            Lines.Clear();
            VariantFx.Unregister(transform);
        }

        private void OnDestroy() => VariantFx.Unregister(transform);
    }
}
