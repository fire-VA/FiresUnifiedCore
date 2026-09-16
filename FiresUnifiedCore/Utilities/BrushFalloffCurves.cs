using UnityEngine;

namespace FiresCore.Utilities
{
    // Port of Blender's BKE_brush_curve_strength presets (source/blender/
    // blenkernel/intern/brush.cc:2463-2528). Each curve maps p = 1 - dist/radius
    // (1 at brush center, 0 at edge) onto a [0, 1] falloff weight. Curves are
    // interchangeable — every preset returns 0 at p=0 and 1 at p=1 so swapping
    // doesn't require recalibrating brush intensity. Smooth (Hermite smoothstep)
    // is Blender's default; Smoother + Sphere have zero endpoint derivatives so
    // their stamps blend invisibly into untouched terrain.
    public static class BrushFalloffCurves
    {
        public enum Preset
        {
            Smooth = 0,
            Smoother = 1,
            Sharp = 2,
            Linear = 3,
            Root = 4,
            Constant = 5,
            Sphere = 6,
            Pow4 = 7,
            InvSquare = 8,
        }

        private const float RadiusEpsilon = 0.0001f;

        public static float Evaluate(Preset preset, float p)
        {
            p = Mathf.Clamp01(p);
            if (p <= 0f) return 0f;
            if (p >= 1f) return 1f;

            switch (preset)
            {
                case Preset.Smooth:    return SmoothStep(p);
                case Preset.Smoother:  return SmootherStep(p);
                case Preset.Sharp:     return p * p;
                case Preset.Linear:    return p;
                case Preset.Root:      return Mathf.Sqrt(p);
                case Preset.Constant:  return 1f;
                case Preset.Sphere:    return SphereCap(p);
                case Preset.Pow4:      return p * p * p * p;
                case Preset.InvSquare: return p * (2f - p);
                default:               return SmoothStep(p);
            }
        }

        public static float EvaluateAtDist(Preset preset, float dist, float radius)
        {
            if (radius <= RadiusEpsilon) return 0f;
            if (dist >= radius) return 0f;
            float proximity = 1f - dist / radius;
            return Evaluate(preset, proximity);
        }

        public static Preset Parse(string name)
        {
            if (string.IsNullOrEmpty(name)) return Preset.Smooth;
            switch (name.ToLowerInvariant())
            {
                case "smooth":    return Preset.Smooth;
                case "smoother":  return Preset.Smoother;
                case "sharp":     return Preset.Sharp;
                case "linear":
                case "lin":       return Preset.Linear;
                case "root":      return Preset.Root;
                case "constant":
                case "flat":      return Preset.Constant;
                case "sphere":
                case "dome":      return Preset.Sphere;
                case "pow4":      return Preset.Pow4;
                case "invsquare":
                case "invsq":     return Preset.InvSquare;
                default:          return Preset.Smooth;
            }
        }

        private static float SmoothStep(float p) => p * p * (3f - 2f * p);

        private static float SmootherStep(float p) => p * p * p * (p * (6f * p - 15f) + 10f);

        private static float SphereCap(float p) => Mathf.Sqrt(Mathf.Max(0f, 2f * p - p * p));
    }
}
