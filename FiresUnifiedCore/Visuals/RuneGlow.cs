using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Visuals
{
    /// <summary>
    /// Station glow (the same glowing runes and extra FX as the Chunk Generator), for FiresRPGCrafting and others. The
    /// generator's glow, as a helper any mod can call: emission on SHARED per-colour copies of a piece's materials (a vanilla material is never
    /// edited), optionally only where the texture is bright (a rune mask made from the albedo's bright pixels, read back from the GPU off the
    /// main thread), a fixed colour or a slow cycle through several, a gentle pulse, an optional pulsing point light, and optional particle
    /// tint. One shared driver animates every glow and skips the ones out of sight range; nothing runs on a headless server.
    /// CONTRACT: RuneGlow.Attach / Detach, RuneGlowOptions' public fields, RuneGlowHandle. Add, never change.
    /// <code>FiresCore.Visuals.RuneGlow.Attach(station, station.GetComponentsInChildren&lt;Renderer&gt;(),
    ///     new FiresCore.Visuals.RuneGlowOptions { Color = new Color(0.4f, 0.8f, 1f), RuneMask = true, Light = true });</code>
    /// </summary>
    public static class RuneGlow
    {
        /// <summary>Glows <paramref name="runeRenderers"/> under <paramref name="root"/>. Calling it again on the same root replaces the glow.</summary>
        public static RuneGlowHandle Attach(GameObject root, Renderer[] runeRenderers, RuneGlowOptions options)
        {
            if (root == null) return null;
            options = options ?? new RuneGlowOptions();
            Detach(root);
            var handle = root.AddComponent<RuneGlowHandle>();
            handle.Init(runeRenderers ?? Array.Empty<Renderer>(), options);
            return handle;
        }

        /// <summary>Removes the glow and gives every renderer its own materials back.</summary>
        public static void Detach(GameObject root)
        {
            var handle = root != null ? root.GetComponent<RuneGlowHandle>() : null;
            if (handle == null) return;
            handle.Restore();
            UnityEngine.Object.Destroy(handle);
        }

        // Glows stay subtle even with many benches and upgrades in one room:
        // every glow x "Rune glow strength" (0.3 = 30% of the old look), and a soft room budget:
        // with more than CrowdBudget glows within CrowdRadius of the camera each is scaled by CrowdBudget / N, so a room full of benches glows
        // about as much as three. Emission and the point lights both. Client-local.
        private static BepInEx.Configuration.ConfigEntry<float> s_strength;
        internal const int CrowdBudget = 3; internal const float CrowdRadius = 10f;
        private const float DefaultStrength = 0.3f;
        internal static float Strength => s_strength != null ? s_strength.Value : DefaultStrength;
        public static void Bind(BepInEx.Configuration.ConfigFile cfg)
        {
            if (s_strength != null) return;
            s_strength = cfg.Bind("Visuals", "Rune glow strength", DefaultStrength, new BepInEx.Configuration.ConfigDescription("How bright glowing runes on crafting stations and upgrades are (1 = the old full glow, 0 = off). Several glows in one room share a budget, so a full workshop stays soft.", new BepInEx.Configuration.AcceptableValueRange<float>(0f, 1f)));
        }

        internal static bool NoGraphics => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || Application.isBatchMode;
    }

    /// <summary>How a RuneGlow looks. Public fields are part of the contract (add, never rename or retype).</summary>
    public sealed class RuneGlowOptions
    {
        /// <summary>The glow colour (used when Cycle is null or empty).</summary>
        public Color Color = new Color(0.45f, 0.8f, 1f);
        /// <summary>Colours to walk through in turn, each swelling and fading, blending through the dim half (the Chunk Generator's plain recipe).</summary>
        public Color[] Cycle;
        /// <summary>Seconds per colour (Cycle) or per pulse (a single colour).</summary>
        public float SecondsPerColour = 6f;
        /// <summary>Emission strength at the top of the pulse.</summary>
        public float Intensity = 2.5f;
        /// <summary>Brightness at the bottom of the pulse, 0..1 (1 = no pulse).</summary>
        public float PulseMin = 0.55f;
        /// <summary>Glow only where the albedo is bright (painted runes / carvings); off = the whole material glows.</summary>
        public bool RuneMask = true;
        /// <summary>Albedo brightness (0..1) below which a texel never glows when RuneMask is on.</summary>
        public float MaskThreshold = 0.55f;
        /// <summary>Add a pulsing point light at the root (or LightAnchor).</summary>
        public bool Light = true;
        public float LightRange = 4f, LightIntensity = 1.2f;
        public Vector3 LightOffset = new Vector3(0f, 1f, 0f);
        public Transform LightAnchor;
        /// <summary>Tint the particle systems under the root to the glow colour.</summary>
        public bool TintParticles = true;
        /// <summary>Beyond this distance from the camera the glow holds still (no per-frame work).</summary>
        public float SightRange = 60f;

        internal string Key => $"{Color}|{(Cycle == null ? "" : string.Join(",", Cycle.Select(c => c.ToString()).ToArray()))}|{RuneMask}|{MaskThreshold:0.00}";
    }

    /// <summary>The glow on one root; RuneGlow.Detach removes it.</summary>
    public sealed class RuneGlowHandle : MonoBehaviour
    {
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private static readonly int EmissionMap = Shader.PropertyToID("_EmissionMap");
        private static readonly int MainTex = Shader.PropertyToID("_MainTex");

        // Shared copies: (source material, look key) -> glowing copy. Every handle with the same look animates the same copies.
        private static readonly Dictionary<(Material, string), Material> s_copies = new Dictionary<(Material, string), Material>();
        private static readonly Dictionary<string, HashSet<Material>> s_byKey = new Dictionary<string, HashSet<Material>>();

        private readonly List<(Renderer renderer, Material[] original)> _swapped = new List<(Renderer, Material[])>();
        private RuneGlowOptions _options;
        private Light _light;
        private ParticleSystem[] _particles = Array.Empty<ParticleSystem>();

        public RuneGlowOptions Options => _options;

        internal void Init(Renderer[] renderers, RuneGlowOptions options)
        {
            _options = options;
            int swapped = 0, masked = 0, skipped = 0, particlesSkipped = 0;
            if (!RuneGlow.NoGraphics)
            {
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    if (r is ParticleSystemRenderer) { particlesSkipped++; continue; }   // Particles take the colour through TintParticles, never emission
                    var original = r.sharedMaterials;
                    var glowing = new Material[original.Length];
                    for (int i = 0; i < original.Length; i++)
                    {
                        glowing[i] = CopyFor(original[i], options, out bool isMasked);
                        if (original[i] != null && ReferenceEquals(glowing[i], original[i])) skipped++;
                        if (isMasked) masked++;
                    }
                    _swapped.Add((r, original));
                    r.sharedMaterials = glowing;
                    swapped++;
                }
                if (options.Light)
                {
                    var host = new GameObject("RuneGlowLight");
                    host.transform.SetParent(options.LightAnchor != null ? options.LightAnchor : transform, false);
                    host.transform.localPosition = options.LightAnchor != null ? Vector3.zero : options.LightOffset;
                    _light = host.AddComponent<Light>();
                    _light.type = LightType.Point;
                    _light.range = options.LightRange;
                    _light.intensity = options.LightIntensity;
                    _light.color = options.Color;
                    _light.shadows = LightShadows.None;
                }
                if (options.TintParticles) _particles = GetComponentsInChildren<ParticleSystem>(true);
                RuneGlowDriver.Add(this);
                if (s_probes < ProbeLimit && isActiveAndEnabled) { s_probes++; StartCoroutine(Probe()); }   // Never on an inactive prefab
            }
            FiresLogger.LogDebug($"[RuneGlow] {name}: {swapped} renderer(s) glowing ({skipped} material(s) skipped: no emission in their shader; {particlesSkipped} particle renderer(s) left to TintParticles), rune mask {(options.RuneMask ? $"on ({masked} material(s) masked)" : "off")}, "
                + $"light {(_light != null ? "on" : "off")}, {_particles.Length} particle system(s) tinted{(RuneGlow.NoGraphics ? " (no graphics: nothing drawn)" : "")}");
        }

        // The swapped renderers'
        // materials on the attach frame and the two after it - null slots, unsupported / error shaders - for the first ProbeLimit attaches.
        private const int ProbeLimit = 200;   // high enough that bag swaps late in a launch are still probed
        private const int ProbeExtraFrames = 2;
        private static int s_probes;

        private System.Collections.IEnumerator Probe()
        {
            for (int frame = 0; frame <= ProbeExtraFrames; frame++)
            {
                var bad = new System.Text.StringBuilder();
                int mats = 0;
                foreach (var (renderer, _) in _swapped)
                {
                    if (renderer == null) { bad.Append(" [renderer gone]"); continue; }
                    var cur = renderer.sharedMaterials;
                    for (int i = 0; i < cur.Length; i++)
                    {
                        mats++;
                        var m = cur[i];
                        if (m == null) bad.Append($" {renderer.name}[{i}] NULL;");
                        else if (m.shader == null || !m.shader.isSupported || m.shader.name == "Hidden/InternalErrorShader")
                            bad.Append($" {renderer.name}[{i}] '{m.name}' shader '{(m.shader != null ? m.shader.name : "null")}' supported {(m.shader != null && m.shader.isSupported)};");
                    }
                }
                // Every submesh slot of a
                // multi-submesh renderer - slot vs submesh count, the material, its shader, supported, _EMISSION on, and the original it replaced
                var multi = new System.Text.StringBuilder();
                foreach (var (renderer, original) in _swapped)
                {
                    if (renderer == null) continue;
                    Mesh mesh = renderer is SkinnedMeshRenderer sk ? sk.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mesh == null || mesh.subMeshCount <= 1) continue;
                    var cur = renderer.sharedMaterials;
                    multi.Append($" {renderer.name}: {mesh.subMeshCount} submesh(es), {cur.Length} slot(s) (was {original.Length})");
                    for (int i = 0; i < cur.Length; i++)
                    {
                        var m = cur[i];
                        string was = i < original.Length && original[i] != null ? original[i].name : "none";
                        multi.Append(m == null ? $" [{i}] NULL (was '{was}')"
                            : $" [{i}] '{m.name}' {(m.shader != null ? m.shader.name : "no shader")} supported {(m.shader != null && m.shader.isSupported)} emission {m.IsKeywordEnabled("_EMISSION")} (was '{was}')");
                    }
                    multi.Append(';');
                }
                FiresLogger.LogDebug($"[RuneGlow] probe {name} frame +{frame} (t {Time.frameCount}): {mats} material(s) on {_swapped.Count} renderer(s), {(bad.Length == 0 ? "all render" : "BAD:" + bad)}"
                    + (multi.Length > 0 ? $"; multi-submesh:{multi}" : ""));
                yield return null;
            }
        }

        private static Material CopyFor(Material source, RuneGlowOptions o, out bool masked)
        {
            masked = false;
            if (source == null) return null;
            // A shader without emission (snow, terrain blends) has no '_EmissionMap' texture property,
            // so it keeps its own material and is counted as skipped.
            if (!source.HasProperty(EmissionColor)) return source;
            var key = (source, o.Key);
            if (s_copies.TryGetValue(key, out var copy) && copy != null)
            {
                masked = o.RuneMask && copy.HasProperty(EmissionMap) && copy.GetTexture(EmissionMap) != null;
                return copy;
            }
            copy = new Material(source) { name = source.name + " (RuneGlow)" };
            copy.EnableKeyword("_EMISSION");
            copy.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (o.RuneMask && copy.HasProperty(EmissionMap))
            {
                var albedo = source.HasProperty(MainTex) ? source.GetTexture(MainTex) : null;
                if (albedo != null) { RuneMasks.Request(albedo, o.MaskThreshold, mask => { if (copy != null) copy.SetTexture(EmissionMap, mask); }); masked = true; }
            }
            if (copy.HasProperty(EmissionColor)) copy.SetColor(EmissionColor, o.Color * (o.Intensity * RuneGlow.Strength));
            s_copies[key] = copy;
            if (!s_byKey.TryGetValue(o.Key, out var set)) s_byKey[o.Key] = set = new HashSet<Material>();
            set.Add(copy);
            return copy;
        }

        // The pulse and colour at a time: one colour swells and fades; a cycle blends into the next through the dim half.
        private const float MinPulsePeriodSeconds = 0.5f;
        private const float CycleBlendStartPhase = 0.5f;   // a cycle starts blending to the next colour halfway through the pulse
        private const float LightOffScale = 0.001f;

        internal static void Look(RuneGlowOptions o, float time, out Color color, out float brightness)
        {
            float period = Mathf.Max(MinPulsePeriodSeconds, o.SecondsPerColour);
            float step = time / period;
            int index = Mathf.FloorToInt(step);
            float phase = step - index;
            brightness = Mathf.Lerp(Mathf.Clamp01(o.PulseMin), 1f, Mathf.Sin(phase * Mathf.PI));
            if (o.Cycle == null || o.Cycle.Length == 0) { color = o.Color; return; }
            int n = o.Cycle.Length;
            var current = o.Cycle[((index % n) + n) % n];
            var next = o.Cycle[(((index + 1) % n) + n) % n];
            color = Color.Lerp(current, next, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(CycleBlendStartPhase, 1f, phase)));
        }

        internal void Animate(Color color, float brightness, HashSet<string> keysDone, float scale)
        {
            if (keysDone.Add(_options.Key) && s_byKey.TryGetValue(_options.Key, out var set))
                foreach (var m in set) if (m != null && m.HasProperty(EmissionColor)) m.SetColor(EmissionColor, color * (_options.Intensity * brightness * scale));
            if (_light != null) { _light.color = color; _light.intensity = _options.LightIntensity * brightness * scale; _light.enabled = scale > LightOffScale; }
            foreach (var ps in _particles)
            {
                if (ps == null) continue;
                var main = ps.main;
                main.startColor = color;
            }
        }

        internal void Restore()
        {
            // The detach side of a swap (bag to bag), logged with the probe's cap: what each renderer gets back
            if (s_probes < ProbeLimit && FiresLogger.DebugEnabled)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var (renderer, original) in _swapped)
                {
                    if (renderer == null) { sb.Append(" [renderer gone]"); continue; }
                    int nulls = 0, glowCopies = 0;
                    foreach (var m in original) { if (m == null) nulls++; else if (m.name.EndsWith(" (RuneGlow)", StringComparison.Ordinal)) glowCopies++; }
                    sb.Append($" {renderer.name} {renderer.sharedMaterials.Length} -> {original.Length} slot(s){(nulls > 0 ? $", {nulls} NULL" : "")}{(glowCopies > 0 ? $", {glowCopies} still a RuneGlow copy" : "")};");
                }
                FiresLogger.LogDebug($"[RuneGlow] restore {name} (t {Time.frameCount}):{sb}");
            }
            foreach (var (renderer, original) in _swapped) if (renderer != null) renderer.sharedMaterials = original;
            _swapped.Clear();
            if (_light != null) Destroy(_light.gameObject);
            RuneGlowDriver.Remove(this);
        }

        private void OnDestroy() => RuneGlowDriver.Remove(this);
    }

    /// <summary>One driver for every glow: animates those within their sight range of the camera, once per shared look.</summary>
    internal sealed class RuneGlowDriver : MonoBehaviour
    {
        private static readonly HashSet<RuneGlowHandle> s_live = new HashSet<RuneGlowHandle>();
        private static readonly HashSet<string> s_keysDone = new HashSet<string>();
        private static RuneGlowDriver s_driver;
        private static int s_loggedNear = -1; private static float s_loggedStrength = -1f;

        internal static void Add(RuneGlowHandle h)
        {
            s_live.Add(h);
            if (s_driver != null) return;
            var go = new GameObject("FiresRuneGlowDriver");
            DontDestroyOnLoad(go);
            s_driver = go.AddComponent<RuneGlowDriver>();
        }

        internal static void Remove(RuneGlowHandle h) => s_live.Remove(h);

        private void Update()
        {
            if (s_live.Count == 0) return;
            var cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
            s_keysDone.Clear();
            s_live.RemoveWhere(h => h == null);
            float t = Time.time;
            // Strength x the room budget
            int near = 0;
            float crowdRadiusSqr = RuneGlow.CrowdRadius * RuneGlow.CrowdRadius;
            foreach (var h in s_live) if ((h.transform.position - eye).sqrMagnitude <= crowdRadiusSqr) near++;
            float strength = RuneGlow.Strength;
            float crowdShare = near > RuneGlow.CrowdBudget ? (float)RuneGlow.CrowdBudget / near : 1f;
            float scale = strength * crowdShare;
            if (near != s_loggedNear || !Mathf.Approximately(strength, s_loggedStrength))
            {
                s_loggedNear = near; s_loggedStrength = strength;
                FiresLogger.LogDebug($"[RuneGlow] strength {strength:0.00}, {near} piece(s) within {RuneGlow.CrowdRadius:0} m -> each x {scale:0.00}");
            }
            foreach (var h in s_live)
            {
                var o = h.Options;
                if (cam != null && (h.transform.position - eye).sqrMagnitude > o.SightRange * o.SightRange) continue;
                RuneGlowHandle.Look(o, t, out Color color, out float brightness);
                h.Animate(color, brightness, s_keysDone, scale);
            }
        }
    }

    /// <summary>
    /// Rune masks from an albedo's bright texels, made once per (texture, threshold): the texture is blitted to a 256-square render texture
    /// (so a game texture need not be readable), read back asynchronously, turned into alpha on a worker, and uploaded on the main thread.
    /// Until then the copy glows unmasked.
    /// </summary>
    internal static class RuneMasks
    {
        private const int Size = 256;
        private static readonly Dictionary<(Texture, int), Texture2D> s_done = new Dictionary<(Texture, int), Texture2D>();
        private static readonly Dictionary<(Texture, int), List<Action<Texture2D>>> s_waiting = new Dictionary<(Texture, int), List<Action<Texture2D>>>();

        // Each texel's brightest channel above the cut, stretched to 0..255, into all four channels.
        private static Color32[] BrightnessToAlpha(Color32[] pixels, int cut)
        {
            var alpha = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                int brightest = Math.Max(p.r, Math.Max(p.g, p.b));
                int a = brightest <= cut ? 0 : Math.Min(255, (brightest - cut) * 255 / Math.Max(1, 255 - cut));
                alpha[i] = new Color32((byte)a, (byte)a, (byte)a, (byte)a);
            }
            return alpha;
        }

        internal static void Request(Texture albedo, float threshold, Action<Texture2D> ready)
        {
            var key = (albedo, Mathf.RoundToInt(Mathf.Clamp01(threshold) * 255f));
            if (s_done.TryGetValue(key, out var mask) && mask != null) { ready(mask); return; }
            if (s_waiting.TryGetValue(key, out var list)) { list.Add(ready); return; }
            s_waiting[key] = new List<Action<Texture2D>> { ready };
            if (!SystemInfo.supportsAsyncGPUReadback) { FiresLogger.LogDebug($"[RuneGlow] no async GPU readback: '{albedo.name}' glows unmasked"); s_waiting.Remove(key); return; }
            var target = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(albedo, target);
            int cut = key.Item2;
            UnityEngine.Rendering.AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32, request =>
            {
                try { target.Release(); } catch { }
                UnityEngine.Object.Destroy(target);
                if (request.hasError) { FiresLogger.LogDebug($"[RuneGlow] mask readback of '{albedo.name}' failed: it glows unmasked"); s_waiting.Remove(key); return; }
                var pixels = request.GetData<Color32>().ToArray();
                System.Threading.Tasks.Task.Run(() =>
                {
                    var alpha = BrightnessToAlpha(pixels, cut);
                    FiresCore.Async.MainThreadDispatcher.Enqueue(() =>
                    {
                        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true) { name = albedo.name + " (rune mask)", wrapMode = TextureWrapMode.Repeat };
                        tex.SetPixels32(alpha);
                        tex.Apply(true, true);
                        s_done[key] = tex;
                        if (s_waiting.TryGetValue(key, out var callbacks)) foreach (var cb in callbacks) cb(tex);
                        s_waiting.Remove(key);
                        FiresLogger.LogDebug($"[RuneGlow] rune mask of '{albedo.name}' ready ({callbacks?.Count ?? 0} material(s))");
                    });
                });
            });
        }
    }
}
