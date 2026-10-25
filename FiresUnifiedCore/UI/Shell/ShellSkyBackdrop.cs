// Fires UI Shell, Core step 0/1: the live night-sky backdrop behind the hub
// - A dedicated camera renders ONLY the sky: clearFlags Skybox, cullingMask 0, its own Skybox component holding a COPY of the
// live RenderSettings.skybox material (made on open), so the world's sky material is never written.
// - Night is forced for this camera only: the shader globals EnvMan writes (_SkyboxSunDir, _SunDir, _SunColor, _SunFogColor,
// _AmbientColor, _AuroraStrength) and RenderSettings fog/ambient are set to the Clear env's night values right before
// Camera.Render and restored right after, in a finally.
// - Half screen size RenderTexture, rendered ON DEMAND: only while the hub is open, at most every 2nd frame. No light is
// created (a skybox-only camera with cullingMask 0 draws no lit geometry; the world-brightening lesson).
// - The camera yaws at [UI] Sky drift speed x the mockup's base 0.056 deg/s; [UI] Reduce motion stops it.
// - Destroyed when the hub closes. A Debug perf line every 10 s while open.
using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    internal sealed class ShellSkyBackdrop : IDisposable
    {
        // ---- mockup constants -------------------------------------------------------
        private const float DayFraction = 0.9f;          // SkyDayFraction
        private const float BaseYawDegPerSec = 0.056f;   // SkyYawPerSecondV1 (~ v1 star drift); x [UI] Sky drift speed (4)
        private const float Pitch = -28f;                // looks up so the horizon sits low
        private const float FieldOfView = 60f;
        private const string NightEnvName = "Clear";
        // "Clear" night colours as the mockup read them from main.unity (used if EnvMan's own Clear env is not found)
        private static readonly Color FallbackAmbNight = new Color(0.3568339f, 0.36769363f, 0.4852941f);
        private static readonly Color FallbackFogNight = new Color(0.1454909f, 0.14957127f, 0.16911763f);
        private static readonly Color FallbackSunNight = new Color(0.36443123f, 0.38445106f, 0.48607615f);
        private const float FallbackSunAngle = 45f;

        private static readonly int IdSkyboxSunDir = Shader.PropertyToID("_SkyboxSunDir");
        private static readonly int IdSunDir = Shader.PropertyToID("_SunDir");
        private static readonly int IdSunColor = Shader.PropertyToID("_SunColor");
        private static readonly int IdSunFogColor = Shader.PropertyToID("_SunFogColor");
        private static readonly int IdAmbientColor = Shader.PropertyToID("_AmbientColor");
        private static readonly int IdAuroraStrength = Shader.PropertyToID("_AuroraStrength");   // vanilla global
        private static readonly int IdAuroraIntensity = Shader.PropertyToID("_AuroraIntensity"); // FiresSkyboxAurora material
        private static readonly int IdStarFadeBySky = Shader.PropertyToID("_StarFadeBySky");     // TossinShade pushes 20 by day
        private static readonly int IdDaySkyDensity = Shader.PropertyToID("_DaySkyDensity");     // TossinShade day lift

        private readonly RawImage _image;
        private GameObject _camGo;
        private Camera _cam;
        private Material _mat;
        private RenderTexture _rt;
        private float _yaw;
        private bool _failed;
        private bool _disposed;
        private bool _hasFrame;

        // night values (resolved once per open)
        private Vector4 _nightSunDir, _nightLightDir;
        private Color _nightSunColor, _nightSunFog, _nightFog, _nightAmb;
        private float _nightFogDensity;
        private float _baseYaw;

        // perf
        private readonly Stopwatch _sw = new Stopwatch();
        private double _accMs;
        private int _accFrames;
        private float _nextPerfLog;

        internal ShellSkyBackdrop(RectTransform parent)
        {
            var rt = new GameObject("Sky", typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.SetAsFirstSibling();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            _image = rt.gameObject.AddComponent<RawImage>();
            _image.color = Color.black;          // black until the first sky frame lands
            _image.raycastTarget = true;          // the backdrop eats clicks meant for the world
            _nextPerfLog = Time.unscaledTime + 10f;
            if (!Setup()) _failed = true;
        }

        private bool Setup()
        {
            var live = RenderSettings.skybox;
            if (live == null) { ShellLog.Warn("sky backdrop: RenderSettings.skybox is null; plain black backdrop"); return false; }
            try
            {
                // a copy of the LIVE material
                // carries the world's current (daytime) values that EnvMan / TossinShade drive. The approved mockup is the game's skybox.mat
                // values on this shader at night, so the backdrop builds a FRESH material from the live shader (its own defaults), takes
                // only the textures from the live material, and sets skybox.mat's saved values (world\sky\skybox.mat).
                // Core's own copy of the sky shader + its textures from
                // firescoreshaders ("Fires/ShellSky"); the live shader + live textures only when Core's copy is missing.
                LogLiveTextures(live);
                Shader own = FiresCore.Materials.FiresShaderBroker.Get("Fires/ShellSky");
                _mat = new Material(own != null ? own : live.shader) { name = "FiresShellNightSky", hideFlags = HideFlags.DontSave };
                if (own != null) OwnTextures(_mat); else CopyTextures(live, _mat);
                ShellLog.Debug("sky backdrop: shader " + _mat.shader.name + (own != null ? " (Core's own copy)" : " (live, Core's copy missing)"));
                ApplySkyboxMatValues(_mat);
                ResolveNight();
                // mirror the mockup's material edits: aurora off, sun disk ~black at night; TossinShade's day-only lifts reset
                if (_mat.HasProperty(IdAuroraIntensity)) _mat.SetFloat(IdAuroraIntensity, 0f);
                if (_mat.HasProperty(IdSunColor)) _mat.SetColor(IdSunColor, _nightSunColor * 0.05f);
                if (_mat.HasProperty(IdStarFadeBySky)) _mat.SetFloat(IdStarFadeBySky, 3.5f);   // shader default (night)
                if (_mat.HasProperty(IdDaySkyDensity)) _mat.SetFloat(IdDaySkyDensity, 1f);    // shader default
                _mat.SetVector(IdSkyboxSunDir, _nightSunDir);

                _camGo = new GameObject("FiresShellSkyCamera") { hideFlags = HideFlags.DontSave };
                if (ShellRuntime.Instance != null) _camGo.transform.SetParent(ShellRuntime.Instance.transform, false);
                _camGo.transform.position = new Vector3(0f, -50000f, 0f);
                _cam = _camGo.AddComponent<Camera>();
                _cam.enabled = false;                 // never renders on its own; only Render below
                _cam.clearFlags = CameraClearFlags.Skybox;
                _cam.cullingMask = 0;
                _cam.fieldOfView = FieldOfView;
                _cam.nearClipPlane = 0.3f;
                _cam.farClipPlane = 1000f;
                _cam.allowHDR = false;
                _cam.allowMSAA = false;
                _cam.useOcclusionCulling = false;
                _cam.depthTextureMode = DepthTextureMode.None;
                var sky = _camGo.AddComponent<Skybox>();
                sky.material = _mat;

                var moon = -(Vector3)_nightSunDir;
                _baseYaw = Mathf.Atan2(moon.x, moon.z) * Mathf.Rad2Deg;
                EnsureTarget();
                ShellLog.Debug("sky backdrop: camera + " + _rt.width + "x" + _rt.height + " RT, material copy of " + live.name
                    + " (shader " + (live.shader != null ? live.shader.name : "null") + ")");
                return true;
            }
            catch (Exception e)
            {
                ShellLog.Warn("sky backdrop setup failed (" + e.GetType().Name + ": " + e.Message + "); plain black backdrop");
                return false;
            }
        }

        private static readonly string[] SkyTextures = { "_AuroraA", "_AuroraB", "_AuroraC", "_AuroraD", "_CurlTexture", "_MoonTex", "_StarFieldTex" };

        // Core's bundle names: the Aurora/curl textures by file name, the starfield cubemap "dustsky_color".
        private static void OwnTextures(Material m)
        {
            var found = new System.Collections.Generic.List<string>();
            void T(string prop, string asset) { var t = FiresCore.Materials.FiresShaderBroker.GetTexture(asset); if (t != null && m.HasProperty(prop)) { m.SetTexture(prop, t); found.Add(prop); } }
            T("_AuroraA", "AuroraA"); T("_AuroraB", "AuroraB"); T("_AuroraC", "AuroraC"); T("_AuroraD", "AuroraD"); T("_CurlTexture", "Curl2D");
            var cube = FiresCore.Materials.FiresShaderBroker.GetCubemap("dustsky_color");
            if (cube != null && m.HasProperty("_StarFieldTex")) { m.SetTexture("_StarFieldTex", cube); found.Add("_StarFieldTex"); }
            ShellLog.Debug("sky backdrop: Core textures set: " + (found.Count > 0 ? string.Join(", ", found.ToArray()) : "none"));
        }

        // The live sky material's texture properties and which are set.
        private static void LogLiveTextures(Material live)
        {
            try
            {
                var names = live.GetTexturePropertyNames();
                var parts = new System.Collections.Generic.List<string>();
                foreach (var n in names) { var t = live.GetTexture(n); parts.Add(n + "=" + (t != null ? t.name : "-")); }
                ShellLog.Debug("sky backdrop: live sky material '" + live.name + "' (" + (live.shader != null ? live.shader.name : "?") + ") textures: " + string.Join(", ", parts.ToArray()));
            }
            catch (Exception e) { ShellLog.Debug("sky backdrop: live texture list failed: " + e.Message); }
        }

        private static void CopyTextures(Material from, Material to)
        {
            int n = 0;
            foreach (string p in SkyTextures)
                if (from.HasProperty(p) && to.HasProperty(p) && from.GetTexture(p) != null) { to.SetTexture(p, from.GetTexture(p)); n++; }
            ShellLog.Debug("sky backdrop: " + n + " texture(s) taken from the live sky material (" + string.Join(", ", SkyTextures) + ")");
        }

        // Valheim's world\sky\skybox.mat saved properties (the mockup's material). Set only where the shader has the property.
        private static void ApplySkyboxMatValues(Material m)
        {
            void F(string p, float v) { if (m.HasProperty(p)) m.SetFloat(p, v); }
            void C(string p, Color v) { if (m.HasProperty(p)) m.SetColor(p, v); }
            F("_AtmosphereThickness", 0.67f); F("_Exposure", 2f); F("_StarExposure", 0.45f);
            F("_FogHeight", -0.19f); F("_FogHeightMax", 0.6f); F("_FogHeightMin", 0.05f); F("_FogHeightScale", 0.85f);
            F("_MoonSize", 0.045f); F("_MoonTexScale", 114.6f); F("_SunDisk", 2f); F("_SunSize", 0.04f); F("_SunSizeConvergence", 3.1f);
            F("_ColorVariationSize", 7521f); F("_IntensityVariationSize", 9344f); F("_MainStructureSize", 10394f); F("_StriationsSize", 10285f);
            F("_StartHeight", 2000f); F("_TotalHeight", 2000f);
            C("_GroundColor", new Color(0.7058824f, 0.8235294f, 0.49411765f, 1f));
            C("_MoonColor", new Color(0.8382352f, 1.198783f, 1.5f, 1f));
            C("_SkyTint", new Color(0.5058824f, 0.6509804f, 0.67058825f, 1f));
            m.EnableKeyword("_SUNDISK_HIGH_QUALITY");
            ShellLog.Debug("sky backdrop: skybox.mat values applied (exposure " + (m.HasProperty("_Exposure") ? m.GetFloat("_Exposure") : -1f)
                + ", star exposure " + (m.HasProperty("_StarExposure") ? m.GetFloat("_StarExposure") : -1f) + ", shader " + m.shader.name + ")");
        }

        // the live sky RT for modules; null while no sky is drawn.
        internal static RenderTexture Current { get; private set; }
        internal static event Action SkyChanged;
        private static void RaiseSky() { try { SkyChanged?.Invoke(); } catch (Exception e) { ShellLog.Warn("SkyTextureChanged handler threw " + e.GetType().Name + ": " + e.Message); } }

        private void ResolveNight()
        {
            EnvSetup env = null;
            try
            {
                var em = EnvMan.instance;
                if (em != null && em.m_environments != null)
                    foreach (var e in em.m_environments) if (e != null && e.m_name == NightEnvName) { env = e; break; }
            }
            catch { env = null; }

            float sunAngle = env != null ? env.m_sunAngle : FallbackSunAngle;
            _nightAmb = env != null ? env.m_ambColorNight : FallbackAmbNight;
            _nightFog = env != null ? env.m_fogColorNight : FallbackFogNight;
            _nightSunFog = env != null ? env.m_fogColorSunNight : FallbackFogNight;
            _nightSunColor = env != null ? env.m_sunColorNight * env.m_lightIntensityNight : FallbackSunNight;
            _nightFogDensity = env != null ? env.m_fogDensityNight : 0.01f;

            // EnvMan.SetEnv: the light rotation for the day fraction; _SkyboxSunDir = -forward BEFORE the night flip (the real sun,
            // below the horizon), _SunDir = -forward AFTER the 180 deg night flip (the moon light).
            var rot = Quaternion.Euler(-90f + sunAngle, 0f, 0f) * Quaternion.Euler(0f, -90f, 0f) * Quaternion.Euler(-90f + 360f * DayFraction, 0f, 0f);
            var sun = -(rot * Vector3.forward);
            var light = -((rot * Quaternion.Euler(180f, 0f, 0f)) * Vector3.forward);
            _nightSunDir = new Vector4(sun.x, sun.y, sun.z, 0f);
            _nightLightDir = new Vector4(light.x, light.y, light.z, 0f);
            ShellLog.Debug("sky backdrop: night from " + (env != null ? "EnvMan '" + NightEnvName + "'" : "mockup fallback colours")
                + ", TOD " + DayFraction + ", sun dir " + sun.ToString("F2"));
        }

        private void EnsureTarget()
        {
            int w = Mathf.Max(16, Screen.width / 2), h = Mathf.Max(16, Screen.height / 2);
            if (_rt != null && _rt.width == w && _rt.height == h) return;
            if (_rt != null) { _cam.targetTexture = null; _rt.Release(); UnityEngine.Object.Destroy(_rt); }
            _rt = new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32) { name = "FiresShellSkyRT", hideFlags = HideFlags.DontSave };
            _rt.Create();
            _cam.targetTexture = _rt;
            _image.texture = _rt;
            Current = _rt; RaiseSky();   // FiresShell.SkyTexture
        }

        /// <summary>Called once per frame by the hub while it is open. Renders on every 2nd frame.</summary>
        internal void Tick()
        {
            if (_disposed || _failed || _cam == null) return;

            bool reduce = FiresShell.CfgReduceMotion != null && FiresShell.CfgReduceMotion.Value;
            ShellLog.ConfigRead("Reduce motion", reduce);
            float drift = FiresShell.CfgSkyDrift != null ? FiresShell.CfgSkyDrift.Value : 4f;
            ShellLog.ConfigRead("Sky drift speed", drift);
            if (!reduce) _yaw = Mathf.Repeat(_yaw + Time.unscaledDeltaTime * BaseYawDegPerSec * Mathf.Max(0f, drift), 360f);

            if ((Time.frameCount & 1) != 0 && _hasFrame) { PerfLog(); return; }   // every 2nd frame (the first frame always renders)
            if (ShellPerfProbe.SkyPaused && _hasFrame) return;                     // perf A/B window (the last frame stays on screen)
            EnsureTarget();
            _camGo.transform.rotation = Quaternion.Euler(Pitch, _baseYaw + _yaw, 0f);
            RenderNight();
            if (!_hasFrame && !_failed) { _hasFrame = true; _image.color = Color.white; Probe(); }
            PerfLog();
        }

        // One-shot point-of-use probe after the first
        // frame: what the RT holds and whether it reaches the RawImage. An RT with alpha ~0 is invisible through UI/Default (the live
        // Valheim sky shader writes no alpha), so the RawImage then draws through an opaque material instead.
        private bool _probed;
        private Material _opaque;
        private void Probe()
        {
            if (_probed || _rt == null) return;
            _probed = true;
            RenderTexture small = null, prev = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                small = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(_rt, small);
                RenderTexture.active = small;
                tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                tex.Apply(false);
                Color sum = Color.clear; float lumMin = 9f, lumMax = -9f;
                var px = tex.GetPixels();
                foreach (var c in px) { sum += c; float l = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f; lumMin = Mathf.Min(lumMin, l); lumMax = Mathf.Max(lumMax, l); }
                Color avg = sum / px.Length;
                Color centre = tex.GetPixel(4, 4);
                ShellLog.Debug($"sky probe: RT {_rt.GetInstanceID()} {_rt.width}x{_rt.height}, cam target {(_cam.targetTexture != null ? _cam.targetTexture.GetInstanceID() : 0)}, image texture {(_image.texture != null ? _image.texture.GetInstanceID() : 0)} (=RT {(_image.texture == _rt)}), image enabled {_image.enabled} active {_image.gameObject.activeInHierarchy} color {_image.color} sibling {_image.transform.GetSiblingIndex()} rect {((RectTransform)_image.transform).rect.size}; centre RGBA {centre}, avg RGBA {avg}, luminance spread {lumMax - lumMin:0.000}");
                if (avg.a < 0.5f)
                {
                    Shader opaque = FindOpaqueShader();
                    if (opaque != null)
                    {
                        _opaque = new Material(opaque) { name = "FiresShellSkyOpaque", hideFlags = HideFlags.DontSave };
                        _image.material = _opaque;
                        ShellLog.Debug($"sky probe: RT alpha {avg.a:0.00} is see-through; the backdrop now draws opaque through '{opaque.name}'");
                    }
                    else ShellLog.Warn("sky probe: RT alpha is ~0 and no opaque shader (Unlit/Texture) is loaded; the backdrop stays see-through");
                }
            }
            catch (Exception e) { ShellLog.Warn("sky probe failed (" + e.GetType().Name + ": " + e.Message + ")"); }
            finally
            {
                RenderTexture.active = prev;
                if (small != null) RenderTexture.ReleaseTemporary(small);
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }

        private static Shader FindOpaqueShader()
        {
            foreach (string n in new[] { "Unlit/Texture", "Legacy Shaders/Diffuse", "Mobile/Unlit (Supports Lightmap)" })
            {
                var s = Shader.Find(n);
                if (s != null && s.isSupported) return s;
            }
            foreach (var s in Resources.FindObjectsOfTypeAll<Shader>())
                if (s != null && s.name == "Unlit/Texture" && s.isSupported) return s;
            return null;
        }

        private void RenderNight()
        {
            // save
            Vector4 gSkyboxSun = Shader.GetGlobalVector(IdSkyboxSunDir);
            Vector4 gSunDir = Shader.GetGlobalVector(IdSunDir);
            Color gSunColor = Shader.GetGlobalColor(IdSunColor);
            Color gSunFog = Shader.GetGlobalColor(IdSunFogColor);
            Color gAmb = Shader.GetGlobalColor(IdAmbientColor);
            float gAurora = Shader.GetGlobalFloat(IdAuroraStrength);
            Color fogColor = RenderSettings.fogColor;
            float fogDensity = RenderSettings.fogDensity;
            Color ambient = RenderSettings.ambientLight;
            bool fog = RenderSettings.fog;

            _sw.Reset();
            _sw.Start();
            try
            {
                Shader.SetGlobalVector(IdSkyboxSunDir, _nightSunDir);
                Shader.SetGlobalVector(IdSunDir, _nightLightDir);
                Shader.SetGlobalColor(IdSunColor, _nightSunColor);
                Shader.SetGlobalColor(IdSunFogColor, _nightSunFog);
                Shader.SetGlobalColor(IdAmbientColor, _nightAmb);
                Shader.SetGlobalFloat(IdAuroraStrength, 0f);
                RenderSettings.fogColor = _nightFog;
                RenderSettings.fogDensity = _nightFogDensity;
                RenderSettings.ambientLight = _nightAmb;
                // the live sky shader at night lerps toward the fog colour (a flat grey); the mockup's look is fog-free.
                RenderSettings.fog = false;
                _cam.Render();
            }
            catch (Exception e)
            {
                _failed = true;
                ShellLog.Warn("sky backdrop render failed (" + e.GetType().Name + ": " + e.Message + "); backdrop frozen");
            }
            finally
            {
                // restore, so the world's next frame sees exactly what EnvMan set
                Shader.SetGlobalVector(IdSkyboxSunDir, gSkyboxSun);
                Shader.SetGlobalVector(IdSunDir, gSunDir);
                Shader.SetGlobalColor(IdSunColor, gSunColor);
                Shader.SetGlobalColor(IdSunFogColor, gSunFog);
                Shader.SetGlobalColor(IdAmbientColor, gAmb);
                Shader.SetGlobalFloat(IdAuroraStrength, gAurora);
                RenderSettings.fogColor = fogColor;
                RenderSettings.fogDensity = fogDensity;
                RenderSettings.ambientLight = ambient;
                RenderSettings.fog = fog;
                _sw.Stop();
            }
            _accMs += _sw.Elapsed.TotalMilliseconds;
            _accFrames++;
        }

        private void PerfLog()
        {
            if (Time.unscaledTime < _nextPerfLog) return;
            _nextPerfLog = Time.unscaledTime + 10f;
            double avg = _accFrames > 0 ? _accMs / _accFrames : 0.0;
            ShellLog.Debug("sky backdrop render " + avg.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + " ms avg (" + _accFrames + " frames)");
            _accMs = 0.0;
            _accFrames = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Current == _rt) { Current = null; RaiseSky(); }
            if (_cam != null) _cam.targetTexture = null;
            if (_camGo != null) UnityEngine.Object.Destroy(_camGo);
            if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); }
            if (_mat != null) UnityEngine.Object.Destroy(_mat);
            if (_opaque != null) UnityEngine.Object.Destroy(_opaque);
            if (_image != null) UnityEngine.Object.Destroy(_image.gameObject);
            _camGo = null; _cam = null; _rt = null; _mat = null;
            ShellLog.Debug("sky backdrop destroyed");
        }
    }
}
