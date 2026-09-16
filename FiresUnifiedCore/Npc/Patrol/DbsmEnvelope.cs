using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Patrol
{
    /// <summary>
    /// Pure envelope math for DBSM <see cref="Section"/>s — maps a section's normalized progress
    /// u∈[0,1] to a speed multiplier (multiple of the NPC's base walk speed). Shared by the runtime
    /// mover (PatrolBehavior) and the editor preview (PatrolRouteVisualizer) so both agree exactly.
    /// No route/world state here — arc-length (world position → u) is computed by the caller.
    ///
    /// NOTE (needs in-game tuning): the exact curve *feel* of each shape is authored to be sane and
    /// monotone-safe, then tuned live via the ShapeK slider. The values below are starting points.
    /// </summary>
    public static class DbsmEnvelope
    {
        private const float MinShapeK = 0.05f;

        /// <summary>Speed multiplier at local progress u∈[0,1] along a section's span.</summary>
        public static float SampleSection(Section s, float u)
        {
            if (s == null) return 1f;
            u = Mathf.Clamp01(u);

            if (s.Shape == SpeedShape.Custom && s.CustomCurve != null && s.CustomCurve.Count > 0)
                return SampleCustom(s.CustomCurve, u);

            float weight = EvaluateShape(s.Shape, u, s.ShapeK); // 0..1 envelope weight
            return Mathf.LerpUnclamped(s.Floor, s.Peak, weight);
        }

        /// <summary>Envelope weight C(u)∈[0,1] for the parametric shapes (Custom is handled separately).</summary>
        public static float EvaluateShape(SpeedShape shape, float u, float k)
        {
            u = Mathf.Clamp01(u);
            k = Mathf.Max(MinShapeK, k);
            switch (shape)
            {
                case SpeedShape.Flat:
                    return 1f; // constant Peak (set Floor=Peak for a flat non-peak speed)

                case SpeedShape.Ramp:
                    // Monotonic Floor→Peak. k=1 linear; k>1 ease-in (slow start); k<1 ease-out.
                    return Mathf.Pow(u, k);

                case SpeedShape.Arch:
                    // Hump: Floor at both ends, Peak in the middle. k controls how pointed the top is
                    // (k=1 triangle, k>1 rounder/flatter top).
                    return 1f - Mathf.Pow(Mathf.Abs(2f * u - 1f), k);

                case SpeedShape.EaseInOut:
                default:
                {
                    // Smoothstep hump: Floor at both ends, Peak in the middle, eased at ends. k widens
                    // (k<1) or narrows (k>1) the peak plateau.
                    float hump = Mathf.Clamp01(1f - Mathf.Abs(2f * u - 1f));
                    hump = Mathf.Pow(hump, k);
                    return hump * hump * (3f - 2f * hump);
                }
            }
        }

        /// <summary>Piecewise-linear read of a custom (u, speedMul) curve. Assumes points sorted by u; clamps outside.</summary>
        public static float SampleCustom(List<Vector2> pts, float u)
        {
            if (pts == null || pts.Count == 0) return 1f;
            if (pts.Count == 1) return pts[0].y;
            if (u <= pts[0].x) return pts[0].y;
            int last = pts.Count - 1;
            if (u >= pts[last].x) return pts[last].y;
            for (int i = 1; i < pts.Count; i++)
            {
                if (u <= pts[i].x)
                {
                    Vector2 start = pts[i - 1], end = pts[i];
                    float span = end.x - start.x;
                    float blend = span > 1e-5f ? (u - start.x) / span : 0f;
                    return Mathf.Lerp(start.y, end.y, blend);
                }
            }
            return pts[last].y;
        }
    }
}
