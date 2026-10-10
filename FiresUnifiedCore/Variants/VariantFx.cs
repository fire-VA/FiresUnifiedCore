using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Variants
{
    /// <summary>
    /// Recolours effects toward a variant's FX colour (Fire 10-08: "the tint of the emission also determines the color of its
    /// attack fx"). It's a hue shift that keeps every colour's brightness, saturation and alpha: fire stays bright, smoke stays
    /// grey (a grey has no hue to move), and nothing is flat-filled. Particles (start colour, colour over lifetime / by speed,
    /// their material tint through a property block), lights, trails and lines.
    /// </summary>
    public static class VariantFx
    {
        private static readonly int TintColor = Shader.PropertyToID("_TintColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private static MaterialPropertyBlock _block;

        /// <summary>Recolours everything under <paramref name="fx"/> toward <paramref name="fxColor"/>.</summary>
        public static void Recolour(GameObject fx, Color fxColor)
        {
            if (fx == null) return;
            Color.RGBToHSV(Normalised(fxColor), out float h, out float s, out _);
            foreach (ParticleSystem ps in fx.GetComponentsInChildren<ParticleSystem>(true)) Recolour(ps, h, s);
            foreach (Light light in fx.GetComponentsInChildren<Light>(true)) light.color = Shift(light.color, h, s);
            foreach (TrailRenderer trail in fx.GetComponentsInChildren<TrailRenderer>(true)) trail.colorGradient = Shift(trail.colorGradient, h, s);
            foreach (LineRenderer line in fx.GetComponentsInChildren<LineRenderer>(true)) line.colorGradient = Shift(line.colorGradient, h, s);
        }

        private static void Recolour(ParticleSystem ps, float h, float s)
        {
            ParticleSystem.MainModule main = ps.main;
            main.startColor = Shift(main.startColor, h, s);
            ParticleSystem.ColorOverLifetimeModule life = ps.colorOverLifetime;
            if (life.enabled) life.color = Shift(life.color, h, s);
            ParticleSystem.ColorBySpeedModule speed = ps.colorBySpeed;
            if (speed.enabled) speed.color = Shift(speed.color, h, s);

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            Material mat = renderer != null ? renderer.sharedMaterial : null;
            if (mat == null) return;
            if (_block == null) _block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(_block);
            if (mat.HasProperty(TintColor)) _block.SetColor(TintColor, Shift(mat.GetColor(TintColor), h, s));
            if (mat.HasProperty(ColorId)) _block.SetColor(ColorId, Shift(mat.GetColor(ColorId), h, s));
            if (mat.HasProperty(EmissionColor)) _block.SetColor(EmissionColor, Shift(mat.GetColor(EmissionColor), h, s));
            renderer.SetPropertyBlock(_block);
        }

        /// <summary>The colour with the target hue (and the lesser of the two saturations' gaps), its own brightness and alpha.</summary>
        public static Color Shift(Color c, float h, float s)
        {
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (max <= 0f) return c;
            Color.RGBToHSV(max > 1f ? new Color(c.r / max, c.g / max, c.b / max) : c, out _, out float cs, out float cv);
            Color shifted = Color.HSVToRGB(h, Mathf.Min(cs, Mathf.Max(s, 0.35f)) , cv);
            if (max > 1f) shifted *= max;      // HDR colours keep their intensity
            shifted.a = c.a;
            return shifted;
        }

        private static ParticleSystem.MinMaxGradient Shift(ParticleSystem.MinMaxGradient g, float h, float s)
        {
            switch (g.mode)
            {
                case ParticleSystemGradientMode.Color:
                    return new ParticleSystem.MinMaxGradient(Shift(g.color, h, s));
                case ParticleSystemGradientMode.TwoColors:
                    return new ParticleSystem.MinMaxGradient(Shift(g.colorMin, h, s), Shift(g.colorMax, h, s));
                case ParticleSystemGradientMode.Gradient:
                    return new ParticleSystem.MinMaxGradient(Shift(g.gradient, h, s));
                case ParticleSystemGradientMode.TwoGradients:
                    return new ParticleSystem.MinMaxGradient(Shift(g.gradientMin, h, s), Shift(g.gradientMax, h, s));
                case ParticleSystemGradientMode.RandomColor:
                    var random = new ParticleSystem.MinMaxGradient(Shift(g.gradient, h, s)) { mode = ParticleSystemGradientMode.RandomColor };
                    return random;
                default:
                    return g;
            }
        }

        private static Gradient Shift(Gradient g, float h, float s)
        {
            if (g == null) return null;
            GradientColorKey[] keys = g.colorKeys;
            for (int i = 0; i < keys.Length; i++) keys[i].color = Shift(keys[i].color, h, s);
            var copy = new Gradient { mode = g.mode };
            copy.SetKeys(keys, g.alphaKeys);
            return copy;
        }

        private static Color Normalised(Color c)
        {
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            return max > 1f ? new Color(c.r / max, c.g / max, c.b / max) : c;
        }

        // ---- the registry of tinted characters (read by the FX hooks; no ZDO reads per effect) -----------------------

        private static readonly Dictionary<Transform, Color> Tinted = new Dictionary<Transform, Color>();

        /// <summary>True while any character carries a variant on this client: the hooks' cheap early-out.</summary>
        internal static bool Any => Tinted.Count > 0;

        internal static void Register(Transform root, Color fxColor) { if (root != null) Tinted[root] = fxColor; }

        internal static void Unregister(Transform root) { if (root != null) Tinted.Remove(root); }

        /// <summary>The FX colour of the tinted character this transform belongs to (walks up a few parents).</summary>
        internal static bool TryGetTint(Transform t, out Color fxColor)
        {
            fxColor = default;
            for (int depth = 0; t != null && depth < 8; depth++, t = t.parent)
                if (Tinted.TryGetValue(t, out fxColor)) return true;
            return false;
        }
    }
}
