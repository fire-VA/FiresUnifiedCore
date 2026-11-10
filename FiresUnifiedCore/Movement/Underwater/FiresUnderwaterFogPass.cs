using FiresCore.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Movement.Underwater
{
    /// <summary>
    /// The UNDERWATER zone of FTS's three-zone volumetric fog, as Core's own fullscreen pass (Core port 11-08 of FTS 0.3.361
    /// FiresVolumetricFogPass lines 95-112, 357-369, 454-486; shader Custom/FiresUnderwaterFog in the firescoreunderwater bundle).
    ///
    /// Split with FTS (one look, two passes): FTS's FiresVolFog (AfterForwardAlpha) draws the land/sea/cloud bands; with the camera fully
    /// under it returns the scene untouched, and while the lens straddles the waterline it fogs only the pixels above the line. This pass
    /// runs after it (BeforeImageEffects, hooked BEFORE the caustics pass so the caustics still land on top, FTS 0.3.333's order) and lays
    /// the distance murk over the pixels below the line (all of them once fully under). Same maths as before, except the 0.4 m soft
    /// waterline edge, where the two halves now composite one after the other.
    ///
    /// Owns and publishes (every frame while Underwater is live; 0 once when it stops):
    ///   _FiresFogCamSubmerged (0 above, 0.99 straddling within 5 m of the player, 1 under past the 0.6 m band) — FTS's FiresVolFog reads it;
    ///   _FiresUwWaterY / _FiresUwCamDepth (the camera's true water surface and its depth under it); the _FogUw* uniforms of the shader.
    /// The pass itself is hooked only while the camera is at or under the water (zero cost above).
    /// Debug: "[Underwater] waterline split on|off", "[Underwater] fog diag: ..." once a second while under (FTS's [VolFog] diag line).
    /// </summary>
    public class FiresUnderwaterFogPass : MonoBehaviour
    {
        // live values (UnderwaterSettings / FiresUnderwaterMurk write them)
        public static bool UwOn = true;
        public static Color UwColor = new Color(0.10f, 0.30f, 0.38f, 1f), UwColorThin = new Color(0.10f, 0.30f, 0.38f, 1f);
        public static float UwDensity = 0.7f, UwMaxOp = 0.92f, UwPatchScale = 0.02f, UwSwirl = 0.6f, UwCurrent = 0.05f, UwDistStart = 2f,
            UwDistRange = 40f, UwFarCut = 400f, UwCoverage = 0.5f, UwPatch = 0.3f, UwClearOp = 0.6f, UwPatchFade = 0.3f, UwDepthDensity = 1f, UwDepthSpan = 30f;
        public static float PerObjectUnderwaterScale = 0.15f;
        /// <summary>The fog pass can draw (its key on and its shader present): FiresUnderwaterEnv thins the built-in fog by PerObjectUnderwaterScale then.</summary>
        public static bool Active => UwOn && s_shaderOk;

        private const string ShaderName = "Custom/FiresUnderwaterFog";
        private const CameraEvent Evt = CameraEvent.BeforeImageEffects;
        private const float SplitPlayerRange = 5f;   // FTS 0.3.296: only a camera near the player splits at the waterline
        private static bool s_shaderOk = true, s_straddling, s_said;
        private static float s_diagAt;
        private static readonly Rect s_fullView = new Rect(0f, 0f, 1f, 1f);
        private readonly Vector3[] m_corners = new Vector3[4];
        private Camera m_cam; private CommandBuffer m_cb; private Material m_mat; private Shader m_shader;

        private static readonly int P_Submerged = Shader.PropertyToID("_FiresFogCamSubmerged"), P_WaterY = Shader.PropertyToID("_FiresUwWaterY"),
            P_CamDepth = Shader.PropertyToID("_FiresUwCamDepth"),
            P_RayBL = Shader.PropertyToID("_FiresUwRayBL"), P_RayTL = Shader.PropertyToID("_FiresUwRayTL"),
            P_RayTR = Shader.PropertyToID("_FiresUwRayTR"), P_RayBR = Shader.PropertyToID("_FiresUwRayBR");
        private static int I(string n) => Shader.PropertyToID(n);

        internal static void ClearGlobals()
        {
            Shader.SetGlobalFloat(P_Submerged, 0f);
            Shader.SetGlobalFloat(P_CamDepth, -1f);
        }

        private void LateUpdate()
        {
            var cam = Utils.GetMainCamera();
            // Not live: write nothing (an old FTS that still draws its own underwater owns these globals; Underwater.Stop clears them once).
            if (!Underwater.Live || cam == null) { Teardown(); return; }

            Vector3 c = cam.transform.position;
            float waterY = WaterSurface.TrueY(c, out string waterKind);
            float camDepthBelow = waterY - c.y;
            Shader.SetGlobalFloat(P_WaterY, waterY);
            Shader.SetGlobalFloat(P_CamDepth, camDepthBelow);

            var player = Player.m_localPlayer;
            bool nearPlayer = player != null && Vector3.Distance(c, player.GetHeadPoint()) <= SplitPlayerRange;
            bool straddling = camDepthBelow > 0f && camDepthBelow < SubmersionState.StraddleBand && nearPlayer;
            if (straddling != s_straddling) { s_straddling = straddling; FiresLogger.LogDebug($"[Underwater] waterline split {(straddling ? "on" : "off")}: camera {camDepthBelow:F2} m under (band {SubmersionState.StraddleBand:F1} m)"); }
            float sub = camDepthBelow <= 0f ? 0f : straddling ? 0.99f : camDepthBelow < SubmersionState.StraddleBand ? 0f : 1f;
            Shader.SetGlobalFloat(P_Submerged, sub);

            if (sub <= 0.001f || !UwOn || !EnsureMaterial()) { Teardown(); return; }
            if (m_cam != cam) { Teardown(); Hook(cam); }
            if (m_cam == null) return;

            // the murk eases in/out with the lighting crossfade; the waterline split runs at full murk (FTS 0.3.295)
            float uwEase = FiresUnderwaterEnv.Enabled && !s_straddling ? Mathf.Clamp01(FiresUnderwaterEnv.UwLightBlend) : 1f;
            Shader.SetGlobalFloat(I("_FogUwOn"), 1f);
            Shader.SetGlobalColor(I("_FogUwColor"), UwColor);
            Shader.SetGlobalColor(I("_FogUwColorThin"), UwColorThin);
            Shader.SetGlobalFloat(I("_FogUwDensity"), UwDensity);
            Shader.SetGlobalFloat(I("_FogUwMaxOp"), Mathf.Clamp01(UwMaxOp) * uwEase);
            Shader.SetGlobalFloat(I("_FogUwPatchScale"), UwPatchScale);
            Shader.SetGlobalFloat(I("_FogUwSwirl"), UwSwirl);
            Shader.SetGlobalFloat(I("_FogUwCurrent"), UwCurrent);
            Shader.SetGlobalFloat(I("_FogUwDistStart"), UwDistStart);
            Shader.SetGlobalFloat(I("_FogUwDistRange"), Mathf.Max(1f, UwDistRange));
            Shader.SetGlobalFloat(I("_FogUwFarCut"), Mathf.Max(20f, UwFarCut));
            Shader.SetGlobalFloat(I("_FogUwCoverage"), Mathf.Clamp01(UwCoverage));
            Shader.SetGlobalFloat(I("_FogUwPatch"), Mathf.Clamp01(UwPatch));
            Shader.SetGlobalFloat(I("_FogUwClearOp"), Mathf.Clamp01(UwClearOp));
            Shader.SetGlobalFloat(I("_FogUwPatchFade"), Mathf.Max(0.02f, UwPatchFade));
            Shader.SetGlobalFloat(I("_FogUwDepthDensity"), Mathf.Max(0f, UwDepthDensity));
            Shader.SetGlobalFloat(I("_FogUwDepthSpan"), Mathf.Max(1f, UwDepthSpan));

            if (Time.unscaledTime >= s_diagAt)
            {
                s_diagAt = Time.unscaledTime + 1f;
                FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[Underwater] fog diag: camera {0:F1} m under {1} water (y {2:F1}); submerged {3:F2}; env driver {4}, light blend (ease) {5:F2} -> max opacity {6:F2} of {7:F2}; density {8:F2}, coverage {9:F2}, patch {10:F2}, clear-pocket op {11:F2}, depth density {12:F2}, dist start {13:F0} range {14:F0} far cut {15:F0}; colour {16} thin {17}",
                    camDepthBelow, waterKind, waterY, sub, FiresUnderwaterEnv.Enabled, uwEase, Mathf.Clamp01(UwMaxOp) * uwEase, UwMaxOp, UwDensity,
                    UwCoverage, UwPatch, UwClearOp, UwDepthDensity, UwDistStart, UwDistRange, UwFarCut, ColorUtility.ToHtmlStringRGB(UwColor), ColorUtility.ToHtmlStringRGB(UwColorThin)));
            }
        }

        private bool EnsureMaterial()
        {
            if (m_mat != null) return true;
            if (!s_shaderOk) return false;
            m_shader = UnderwaterAssets.LinearShader(ShaderName);
            if (m_shader == null)
            {
                s_shaderOk = false;
                if (!s_said) { s_said = true; FiresLogger.LogWarning($"[Underwater] {ShaderName} is not in Core's firescoreunderwater bundle ({UnderwaterAssets.Why ?? "shader missing"}): no underwater fog"); }
                return false;
            }
            m_mat = new Material(m_shader) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        private void Hook(Camera cam)
        {
            m_cam = cam;
            m_cam.depthTextureMode |= DepthTextureMode.Depth;
            m_cb = new CommandBuffer { name = "FiresUnderwaterFog" };
            int tmp = Shader.PropertyToID("_FiresUwFogScene");
            m_cb.GetTemporaryRT(tmp, -1, -1, 0, FilterMode.Bilinear);
            m_cb.Blit(BuiltinRenderTextureType.CameraTarget, tmp);
            m_cb.Blit(tmp, BuiltinRenderTextureType.CameraTarget, m_mat);
            m_cb.ReleaseTemporaryRT(tmp);
            m_cam.AddCommandBuffer(Evt, m_cb);
            // The caustics pass must composite AFTER this one (FTS 0.3.333 order): it re-hooks itself next frame, behind us.
            Underwater.ReorderCaustics();
        }

        internal void Teardown()
        {
            if (m_cam != null && m_cb != null) m_cam.RemoveCommandBuffer(Evt, m_cb);
            m_cb = null; m_cam = null;
        }

        private void OnEnable() { Camera.onPreRender += OnCamPreRender; }
        private void OnDisable() { Camera.onPreRender -= OnCamPreRender; Teardown(); }
        private void OnDestroy() { Camera.onPreRender -= OnCamPreRender; Teardown(); if (m_mat != null) Destroy(m_mat); }

        private void OnCamPreRender(Camera cam)
        {
            if (m_cam == null || cam != m_cam) return;
            cam.CalculateFrustumCorners(s_fullView, cam.farClipPlane, Camera.MonoOrStereoscopicEye.Mono, m_corners);
            Transform t = cam.transform;
            Shader.SetGlobalVector(P_RayBL, t.TransformVector(m_corners[0]));
            Shader.SetGlobalVector(P_RayTL, t.TransformVector(m_corners[1]));
            Shader.SetGlobalVector(P_RayTR, t.TransformVector(m_corners[2]));
            Shader.SetGlobalVector(P_RayBR, t.TransformVector(m_corners[3]));
        }
    }
}
