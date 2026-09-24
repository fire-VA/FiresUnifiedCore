using UnityEngine;

namespace FiresCore.Npc.Archetypes.Effects
{
    /// <summary>
    /// Recolours a spawned vanilla effect instance. Only ever touches the instance: Renderer.materials hands back
    /// per-renderer copies, so the shared vanilla material assets stay untouched.
    /// </summary>
    public static class FxTint
    {
        private const string ColorProperty = "_Color";
        private const string TintColorProperty = "_TintColor";
        private const string EmissionColorProperty = "_EmissionColor";

        private static int _colorId, _tintColorId, _emissionColorId;
        private static bool _propertyIdsReady;

        private static void EnsurePropertyIds()
        {
            if (_propertyIdsReady) return;
            _colorId = Shader.PropertyToID(ColorProperty);
            _tintColorId = Shader.PropertyToID(TintColorProperty);
            _emissionColorId = Shader.PropertyToID(EmissionColorProperty);
            _propertyIdsReady = true;
        }

        public static void Apply(GameObject instance, Color color)
        {
            if (instance == null) return;

            TintParticles(instance, color);
            TintRenderers(instance, color);
            TintLights(instance, color);
        }

        public static void ApplyToMaterial(Material material, Color color, float emissionIntensity)
        {
            if (material == null) return;
            EnsurePropertyIds();

            if (material.HasProperty(_colorId)) material.SetColor(_colorId, color);
            if (material.HasProperty(_tintColorId)) material.SetColor(_tintColorId, color);
            if (material.HasProperty(_emissionColorId))
                material.SetColor(_emissionColorId, new Color(
                    color.r * emissionIntensity, color.g * emissionIntensity, color.b * emissionIntensity, color.a));
        }

        private static void TintParticles(GameObject instance, Color color)
        {
            var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                var system = systems[i];
                if (system == null) continue;
                var main = system.main;
                main.startColor = new ParticleSystem.MinMaxGradient(color);
            }
        }

        private static void TintRenderers(GameObject instance, Color color)
        {
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;

                var materials = renderer.materials;
                for (int m = 0; m < materials.Length; m++)
                    ApplyToMaterial(materials[m], color, 1f);
                renderer.materials = materials;
            }
        }

        private static void TintLights(GameObject instance, Color color)
        {
            var lights = instance.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++)
                if (lights[i] != null) lights[i].color = color;
        }
    }
}
