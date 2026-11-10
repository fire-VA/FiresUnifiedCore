using FiresCore.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Movement.Underwater
{
    /// <summary>
    /// Stage 2 seafloor caustics — ALWAYS ON, camera-decoupled. Composites
    /// Custom/FiresCaustics as a fullscreen pass at AfterForwardOpaque every
    /// frame, painting caustic light + wave shadows onto the real seafloor
    /// before the transparent water draws over it. The shader reconstructs each
    /// pixel's world position from the depth texture and only lights pixels
    /// BELOW the live tide surface — so caustics appear on any submerged ocean
    /// floor on screen whether you're on the shore looking down through the
    /// shallows, flying overhead, or actually diving, with NO dependence on
    /// where the camera is.
    ///
    /// (The old version gated the whole pass on camera-Y / submersion, which
    /// toggled it off whenever the 3rd-person camera orbited across the
    /// waterline — "caustics only show at some angles." Removing that gate is
    /// the fix.)
    ///
    /// The projection surface is the LIVE tide level (ZoneSystem.c_WaterLevel +
    /// _FiresTideOffset), so at low tide the caustics fall from the lowered
    /// surface onto the floor. Reads the same _Fires* caustic globals as the V1
    /// in-shader surface caustics; with this always on, turn V1 off
    /// (04 - FiresWater Surface / SurfaceCausticsEnabled) to avoid doubling.
    ///
    /// The depth->world reconstruction is the one piece that can need a tweak
    /// per platform; CausticDebug visualises the raw caustic field so the math
    /// can be validated on first run.
    /// </summary>
    public class FiresCausticsPass : MonoBehaviour
    {
        public static bool Enabled = true;
        public static float Shadow = 0.35f;   // how dark the wave-shadow cells go
        public static float DepthFade = 28f;     // metres below the surface the effect fades over
        public static bool DebugView = false;   // visualise the caustic field
        // Horizontal reach of the caustics around the PLAYER (the loaded zones).
        // Beyond Radius the seafloor shows none; the last FadeWidth metres ramp
        // out so there's no hard ring. Keeping this to the near field is also the
        // main per-frame saver (the shader early-outs past the radius).
        public static float Radius = 110f;
        // The pass's OWN amplitude (config "10b CausticPassStrength") — previously the pass had
        // no strength lever at all, so users chased decoy sliders that fed the surface term only.
        public static float Strength = 1f;
        // 0.3.333: how strong the seabed light reads from above the water vs from below (Fire: slightly harder to see from the surface)
        public static float SurfaceView = 0.8f, UnderwaterView = 1f;
        private static readonly int P_ViewScale = Shader.PropertyToID("_FiresCausticViewScale");
        private static readonly int P_Submerged = Shader.PropertyToID("_FiresPlayerSubmerged");
        // Extra user scale on the NIGHT (moon-lit) caustics. The light drive below already dims
        // night hard via the live _SunColor luminance (the moon is dim); this scales that
        // contribution: 1 = physical, 0 = no caustics at night.
        public static float MoonScale = 1f;
        public static float FadeWidth = 30f;
        // The whole pass is torn down (zero cost) when the player is more than this
        // far ABOVE the water surface — flying high, on a peak, or out in space —
        // where seafloor caustics aren't visible anyway. Generous so normal
        // near-water play (shore, swimming, diving, low flight) always keeps them.
        public static float AltitudeCutoff = 220f;

        private const string CausticShaderName = "Custom/FiresUnderwaterCaustics";   // Core port: Core's own copy (firescoreunderwater)
        // AfterForwardAlpha (was AfterForwardOpaque): composite the caustic AFTER
        // the transparent water has drawn, so the underwater water layer (which is
        // brighter toward the sun via sun-side fog + the luminance preserve-mask)
        // can no longer VEIL the seafloor caustic. That veiling was the cause of
        // "caustics only show when looking toward the sun" — the field is uniform,
        // but the water on top hid it directionally. We still reconstruct the
        // seafloor world pos from the depth texture (water is ZWrite Off, so the
        // depth buffer is the floor) and only light below-water pixels.
        // 0.3.333 (Fire 11-07): BeforeImageEffects = after the volumetric fog (also AfterForwardAlpha, order there was whichever hooked
        // first), so underwater the fog no longer washes the caustics out; the shader applies the underwater fog's own distance law
        private const CameraEvent Evt = CameraEvent.BeforeImageEffects;
        // Caustics are cut off this many metres BELOW the tide surface so wave
        // rise/fall can't expose the hard edge in the shallows.
        private const float SurfaceClampMeters = 1f;

        private Camera m_cam;
        private CommandBuffer m_cb;
        private Material m_mat;
        private Shader m_shader;
        private bool m_warned;

        private static readonly Rect s_fullView = new Rect(0f, 0f, 1f, 1f);
        private readonly Vector3[] m_corners = new Vector3[4];
        private static readonly int P_RayBL = Shader.PropertyToID("_FiresRayBL");
        private static readonly int P_RayTL = Shader.PropertyToID("_FiresRayTL");
        private static readonly int P_RayTR = Shader.PropertyToID("_FiresRayTR");
        private static readonly int P_RayBR = Shader.PropertyToID("_FiresRayBR");
        private static readonly int P_WaterLevel = Shader.PropertyToID("_FiresWaterLevel");
        private static readonly int P_TideOffset = Shader.PropertyToID("_FiresTideOffset");
        private static readonly int P_Origin = Shader.PropertyToID("_FiresCausticOrigin");
        private static readonly int P_Radius = Shader.PropertyToID("_FiresCausticRadius");
        private static readonly int P_FadeWidth = Shader.PropertyToID("_FiresCausticFadeWidth");
        private static readonly int P_Day = Shader.PropertyToID("_FiresUwCausticDay");   // Core port: FTS keeps _FiresCausticDayMask for its surface caustics
        private static readonly int P_PassStrength = Shader.PropertyToID("_FiresCausticPassStrength");
        private static readonly int P_SunColorG = Shader.PropertyToID("_SunColor");

        // Caustics are focused SUNLIGHT — drive the pass by the live light, not a constant:
        //   elevation ramp  = smooth 0..1 as the sun/moon climbs past the horizon (dawn/dusk dim)
        //   luminance ramp  = live _SunColor (dirLight colour x intensity, EnvMan-published every
        //                     frame) normalised against a clear-noon nominal — storms/fog dim it,
        //                     and at night it IS the moon light, already ~10x dimmer
        //   MoonScale       = user scale on the night contribution (0 = none, 1 = physical)
        private static float ComputeLightDrive()
        {
            var em = EnvMan.instance;
            if (em == null) return 1f;
            Vector3 travel = em.GetSunDirection();                 // light-travel dir (down toward ground)
            float elev = Mathf.Clamp01(-travel.y);                 // 0 at horizon, 1 overhead
            float elevRamp = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.06f, 0.35f, elev));
            Color sun = Shader.GetGlobalColor(P_SunColorG);
            float lum = sun.r * 0.299f + sun.g * 0.587f + sun.b * 0.114f;
            float lumRamp = Mathf.Clamp01(lum / 0.8f);
            float drive = elevRamp * lumRamp;
            if (EnvMan.IsNight()) drive *= Mathf.Max(0f, MoonScale);
            return drive;
        }
        private static readonly int P_Shadow = Shader.PropertyToID("_FiresCausticShadow");
        private static readonly int P_DepthFade = Shader.PropertyToID("_FiresCausticDepthFade");
        private static readonly int P_Debug = Shader.PropertyToID("_FiresCausticDebug");

        private static bool s_caveLogged;   // the last "[Caustics] cave factor" state logged

        private void LateUpdate()
        {
            var cam = Utils.GetMainCamera();

            // Light drive + pass strength are pushed UNCONDITIONALLY (before the enable gate):
            // the water shaders' SURFACE caustic term multiplies the same _FiresCausticDayMask,
            // so it must stay live even when the seabed pass is toggled off.
            Shader.SetGlobalFloat(P_Day, ComputeLightDrive());
            // Fire 10-23 "block underwater caustics from showing inside caves": voxel caves (FAT 0.2.1017+) write depth, so the seabed
            // pass lit cave floors below the tide as if they were seafloor. Scaled by (1 - FAT's CameraCaveFactor), the fog's own
            // smoothed 0 open sky .. 1 in a cave, so it fades at a cave mouth. The water SURFACE term (_FiresCausticDayMask) is unchanged.
            float caveK = 0f;
            try { caveK = WaterSurface.CaveFactor(); } catch { }
            Shader.SetGlobalFloat(P_PassStrength, Mathf.Max(0f, Strength) * (1f - caveK));
            Shader.SetGlobalFloat(P_ViewScale, Mathf.Lerp(Mathf.Max(0f, SurfaceView), Mathf.Max(0f, UnderwaterView), Mathf.Clamp01(Shader.GetGlobalFloat(P_Submerged))));   // 0.3.333
            bool inCave = caveK >= 0.5f;
            if (inCave != s_caveLogged)
            {
                s_caveLogged = inCave;
                FiresCore.Logging.FiresLogger.LogDebug($"[Caustics] cave factor {caveK:0.00}: seafloor caustics {(inCave ? "off (FAT CameraCaveFactor)" : "back on")} (pass strength {Mathf.Max(0f, Strength) * (1f - caveK):0.00})");
            }

            // ALWAYS ON — no camera / submersion gate. The shader skips skybox
            // and above-water pixels itself, so this is one cheap fullscreen
            // blit per frame and the caustics never flicker as the camera
            // crosses the waterline. (The old `LocalSubmerged || cam.y < 30`
            // gate is exactly what made them "only show at some angles.")
            if (!Enabled || cam == null || !Underwater.Live || !EnsureMaterial())   // Core port: lives with Swimming (Fire 11-08)
            {
                Teardown();
                return;
            }

            // Anchor the caustic field to the PLAYER (the loaded zones), not the
            // camera (which orbits). When the player is far ABOVE the water —
            // flying high, on a peak, or out in space — tear the whole pass down so
            // it costs nothing; seafloor caustics aren't visible from up there.
            var player = Player.m_localPlayer;
            Vector3 anchor = player != null ? player.transform.position : cam.transform.position;
            float surface = ZoneSystem.c_WaterLevel + Shader.GetGlobalFloat(P_TideOffset);
            if (anchor.y > surface + AltitudeCutoff)
            {
                Teardown();
                return;
            }

            if (m_cam != cam) { Teardown(); Hook(cam); }
            if (m_cam == null) return;

            m_cam.depthTextureMode |= DepthTextureMode.Depth;

            // Project from the LIVE tide surface (sea level + tide offset), so the
            // caustic field falls from the lowered/raised water plane onto the
            // floor and fades by depth below it. _FiresTideOffset is published by
            // the Galaxies tide system (0 when no tide mod is loaded).
            // c_WaterLevel is a compile-time const, so this is null-safe without
            // ZoneSystem.instance. The reconstruction rays are pushed in
            // OnCamPreRender (render-time); the rest are camera-independent.
            // Clamp the caustic CUTOFF ~1m below the live tide surface so the
            // rise/fall of the waves never drags the hard caustic edge across the
            // shallows (the shader also ramps the first 1.5m for a soft join).
            Shader.SetGlobalFloat(P_WaterLevel, surface - SurfaceClampMeters);

            // Restrict caustics to a radius around the player with a soft edge.
            Shader.SetGlobalVector(P_Origin, anchor);
            Shader.SetGlobalFloat(P_Radius, Radius);
            Shader.SetGlobalFloat(P_FadeWidth, Mathf.Max(0.01f, FadeWidth));
            Shader.SetGlobalFloat(P_Shadow, Shadow);
            Shader.SetGlobalFloat(P_DepthFade, DepthFade);
            Shader.SetGlobalFloat(P_Debug, DebugView ? 1f : 0f);
        }

        private static bool s_fromSaid;   // 0.3.333
        private bool EnsureMaterial()
        {
            if (m_mat != null) return true;
            // Core port 11-08: the shader comes only from Core's own firescoreunderwater bundle (LINEAR, FatShaderLinear); one try, one line
            if (m_shader == null && !m_warned)
            {
                try { m_shader = UnderwaterAssets.LinearShader(CausticShaderName); } catch { m_shader = null; }
                if (m_shader == null)
                {
                    m_warned = true;
                    FiresLogger.LogWarning($"[Caustics] {CausticShaderName} is not in Core's firescoreunderwater bundle ({UnderwaterAssets.Why ?? "shader missing"}): seabed caustics off");
                    return false;
                }
                if (!s_fromSaid) { s_fromSaid = true; FiresLogger.LogDebug($"[Caustics] {CausticShaderName} loaded from Core's firescoreunderwater bundle"); }
            }
            if (m_shader == null) return false;
            m_mat = new Material(m_shader);
            return true;
        }

        private void Hook(Camera cam)
        {
            m_cam = cam;
            m_cam.depthTextureMode |= DepthTextureMode.Depth;
            m_cb = new CommandBuffer { name = "FiresCaustics" };
            int tmp = Shader.PropertyToID("_FiresCausticScene");
            m_cb.GetTemporaryRT(tmp, -1, -1, 0, FilterMode.Bilinear);
            m_cb.Blit(BuiltinRenderTextureType.CameraTarget, tmp);
            m_cb.Blit(tmp, BuiltinRenderTextureType.CameraTarget, m_mat);
            m_cb.ReleaseTemporaryRT(tmp);
            m_cam.AddCommandBuffer(Evt, m_cb);
        }

        internal void Teardown()
        {
            if (m_cam != null && m_cb != null) m_cam.RemoveCommandBuffer(Evt, m_cb);
            m_cb = null;
            m_cam = null;
        }

        private void OnEnable() { Camera.onPreRender += OnCamPreRender; }
        private void OnDisable() { Camera.onPreRender -= OnCamPreRender; Teardown(); }
        private void OnDestroy() { Camera.onPreRender -= OnCamPreRender; Teardown(); }

        // Push the world-space far-plane frustum corner rays from the camera's
        // CURRENT render transform, right before it renders. The shader does
        // worldPos = cameraPos + bilerp(rays)*linearDepth — purely geometric, so
        // the caustic field lands on the same world floor regardless of view
        // direction (fixes "caustics in random spots / vanish when you turn").
        private void OnCamPreRender(Camera cam)
        {
            if (m_cam == null || cam != m_cam) return;
            cam.CalculateFrustumCorners(s_fullView, cam.farClipPlane, Camera.MonoOrStereoscopicEye.Mono, m_corners);
            Transform t = cam.transform;
            Shader.SetGlobalVector(P_RayBL, t.TransformVector(m_corners[0]));   // bottom-left
            Shader.SetGlobalVector(P_RayTL, t.TransformVector(m_corners[1]));   // top-left
            Shader.SetGlobalVector(P_RayTR, t.TransformVector(m_corners[2]));   // top-right
            Shader.SetGlobalVector(P_RayBR, t.TransformVector(m_corners[3]));   // bottom-right
        }
    }
}
