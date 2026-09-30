using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Npc.Archetypes.Effects
{
    /// <summary>
    /// Supplies the ground ring's dashes: flat quads of Core's own, one unit long (z) and one unit wide (x), lying on the ground,
    /// scaled by the ring to a thin dash, with a transparent unlit material Core owns.
    ///
    /// Until 0.2.202 the dashes were clones of the workbench circle's segment, with its "station_radius" material (shader Unlit/Color).
    /// Fire, R77 class_test fx_ground: the rings came out as SOLID BLACK slabs. That shader is opaque and has no alpha, so the ring's
    /// fade (the colour multiplied by opacity) could only darken it, and its 1 m segments at 28-36 per ring touched end to end.
    /// A colour Core sets on its own transparent material is the colour drawn, and a dash is as long as the ring says.
    /// </summary>
    public static class GroundRingSegmentSource
    {
        private static Mesh s_quad;
        private static Shader s_shader;
        private static bool s_reported;

        // Transparent, unlit, alpha from _Color: in every Unity build (UI and sprites use it). The others are fallbacks.
        private static readonly string[] ShaderNames = { "Sprites/Default", "Unlit/Transparent", "Legacy Shaders/Particles/Alpha Blended", "Unlit/Color" };

        public static bool Available
        {
            get
            {
                if (s_quad == null) s_quad = BuildQuad();
                if (s_shader == null)
                {
                    foreach (string name in ShaderNames)
                    {
                        s_shader = Shader.Find(name);
                        if (s_shader != null) break;
                    }
                }
                if (!s_reported)
                {
                    s_reported = true;
                    if (s_shader == null) Debug.LogError("[GroundRingSegmentSource] no transparent unlit shader found (tried " + string.Join(", ", ShaderNames) + "); ground rings are off");
                    else Debug.Log($"[GroundRingSegmentSource] ring dashes: Core's own flat quads, shader {s_shader.name}");
                }
                return s_quad != null && s_shader != null;
            }
        }

        /// <summary>The shader the ring material uses (for the evidence line).</summary>
        public static string ShaderName => s_shader != null ? s_shader.name : "none";

        /// <summary>One dash under <paramref name="parent"/>: a flat quad (1 x 1 m before scaling), no collider, no shadows.</summary>
        public static GameObject CreateSegment(Transform parent)
        {
            if (!Available) return null;
            var segment = new GameObject("GroundRingDash");
            segment.transform.SetParent(parent, false);
            segment.AddComponent<MeshFilter>().sharedMesh = s_quad;
            var renderer = segment.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return segment;
        }

        /// <summary>A new transparent unlit material for one ring (the ring sets its colour, alpha included).</summary>
        public static Material CloneSegmentMaterial()
        {
            if (!Available) return null;
            var material = new Material(s_shader) { name = "FiresGroundRing" };
            material.renderQueue = (int)RenderQueue.Transparent;
            return material;
        }

        // A unit quad lying flat (normal up), centred, long along z, drawn from above and below.
        private static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "GroundRingDashQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
