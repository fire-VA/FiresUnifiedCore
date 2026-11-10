using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Npc.Archetypes.Effects
{
    /// <summary>
    /// The ability / AoE marker every Fires mod draws through <see cref="GroundRingManager"/>.
    /// One solid disc mesh that lies on the ground, a translucent fill plus a solid rim in the kind's palette colour. A cast's disc
    /// grows from the centre to its full radius (ease-out); an aiming preview (re-shown while the player aims) is full size at
    /// once. The ground is sampled on a polar grid at the full radius when the ring is configured and refreshed a few points
    /// a frame, so a growing disc follows slopes without a raycast per vertex per frame.
    /// </summary>
    public class GroundRing : MonoBehaviour
    {
        private const float HeightOffset = 0.06f;
        private const float FadeInSeconds = 0.15f;
        private const float FadeOutSeconds = 0.35f;
        private const float MaxGrowSeconds = 0.75f, MinGrowSeconds = 0.2f, GrowShareOfDuration = 0.6f;
        private const float RimAlpha = 0.95f;   // the fill's alpha is the player's Ring Fill Opacity
        private const float RimWidth = 0.18f, MinRimShare = 0.04f, MaxRimShare = 0.25f;
        private const float PulsePeriodSeconds = 1.2f, PulseDepth = 0.12f;
        private const int GroundSamplesPerFrame = 4;
        private const float MinimumDuration = 0.1f;
        private static readonly HashSet<GroundRingKind> s_reported = new HashSet<GroundRingKind>();

        private GroundRingKind _kind;
        private Material _material;
        private Mesh _mesh;
        private MeshFilter _filter;
        private Vector3 _center;
        private float _radius, _duration, _startTime, _opacityScale, _growSeconds;
        private long _casterPlayerId;
        private bool _grows;

        // polar grid: ring fractions of the full radius (0 = centre) x spokes; heights sampled at the FULL radius
        private int _spokes;
        private float[] _fractions;           // vertex rings, last two = rim inner edge and the outer edge
        private float[,] _heights;            // [ring, spoke] ground y at fraction * full radius
        private int _nextSample;
        private Vector3[] _verts;
        private float _lastGrowth = -1f;

        public bool Aiming { get; set; }

        /// <summary>The disc's height samples hit only terrain and build pieces (floors), never rocks / trees /
        /// other static colliders. Set it before <see cref="Configure"/> or call <see cref="SetCenter"/> after. Add-only.</summary>
        public bool GroundOnly { get; set; }

        /// <summary>Moves a live ring to <paramref name="center"/> and re-samples its heights WITHOUT restarting its
        /// timeline or fade (Configure restarts them). Add-only.</summary>
        public void SetCenter(Vector3 center)
        {
            _center = center;
            SetFrame();
            if (_heights == null) return;
            SampleAllHeights();
            _lastGrowth = -1f;   // the mesh is rebuilt on the next update at the current growth
        }

        // Halo ring (FiresCore.World.GravityUp): inside a field the disc lies in the tangent plane at the centre and its heights are
        // offsets along the local up; outside every field _onRing is false and every path below is the old world-y one.
        private bool _onRing;
        private Vector3 _up = Vector3.up, _e1 = Vector3.right, _e2 = Vector3.forward;
        private static bool s_ringLogged;
        private void SetFrame()
        {
            _onRing = FiresCore.World.GravityUp.InField(_center, out Vector3 up);
            if (!_onRing) { _up = Vector3.up; _e1 = Vector3.right; _e2 = Vector3.forward; return; }
            _up = up;
            Quaternion frame = Quaternion.FromToRotation(Vector3.up, up);
            _e1 = frame * Vector3.right; _e2 = frame * Vector3.forward;
            if (!s_ringLogged) { s_ringLogged = true; Debug.Log($"[GroundRing] halo ring: disc at {_center:F0} lies in the tangent plane, up {up:F2}"); }
        }
        private Vector3 Spoke(float a) => _e1 * Mathf.Sin(a) + _e2 * Mathf.Cos(a);

        public long CasterPlayerId => _casterPlayerId;
        public float StartTime => _startTime;

        /// <summary>Restarts the ring's timeline at <paramref name="center"/> (the same call the dashed ring had).</summary>
        public void Configure(GroundRingKind kind, Vector3 center, float radius, float duration,
            long casterPlayerId, bool aiming, float opacityScale)
        {
            _kind = kind;
            _center = center;
            SetFrame();
            _radius = Mathf.Max(radius, 0.05f);
            _duration = Mathf.Max(duration, MinimumDuration);
            _casterPlayerId = casterPlayerId;
            _opacityScale = opacityScale;
            Aiming = aiming;
            _startTime = Time.time;
            _grows = !aiming;
            _growSeconds = Mathf.Clamp(_duration * GrowShareOfDuration, MinGrowSeconds, MaxGrowSeconds);
            transform.position = Vector3.zero;   // the mesh is built in world space
            transform.rotation = Quaternion.identity;

            BuildGrid();
            SampleAllHeights();
            _lastGrowth = -1f;
            UpdateMesh(_grows ? 0.05f : 1f);
            ApplyColor(Envelope(0f), 0f);
            ReportOnce();
        }

        private void EnsureRenderer()
        {
            if (_filter != null) return;
            _filter = gameObject.GetComponent<MeshFilter>() ?? gameObject.AddComponent<MeshFilter>();
            var renderer = gameObject.GetComponent<MeshRenderer>() ?? gameObject.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            if (_material == null) _material = GroundRingSegmentSource.CloneSegmentMaterial();
            renderer.sharedMaterial = _material;
            _mesh = new Mesh { name = "FiresGroundDisc" };
            _mesh.MarkDynamic();
            _filter.sharedMesh = _mesh;
        }

        // Spokes and fill rings scale with the radius; the rim is a fixed ~0.18 m band (a share of the radius on small rings).
        private void BuildGrid()
        {
            EnsureRenderer();
            int spokes = Mathf.Clamp(Mathf.RoundToInt(_radius * 10f), 32, 96);
            int fillRings = Mathf.Clamp(Mathf.RoundToInt(_radius * 1.5f), 3, 12);
            float rimShare = Mathf.Clamp(RimWidth / _radius, MinRimShare, MaxRimShare);
            float rimInner = 1f - rimShare;

            var fr = new List<float> { 0f };
            for (int j = 1; j <= fillRings; j++) fr.Add(rimInner * j / fillRings);
            fr.Add(1f);
            bool rebuild = _fractions == null || _spokes != spokes || _fractions.Length != fr.Count;
            _spokes = spokes;
            _fractions = fr.ToArray();
            if (!rebuild) { ApplyVertexAlpha(); return; }

            // vertices: the centre once; each fill ring; the rim inner edge again (rim colour) and the outer edge
            int fillCount = 1 + fillRings * spokes;
            int vertCount = fillCount + 2 * spokes;
            _verts = new Vector3[vertCount];
            _fillCount = fillCount;

            var tris = new List<int>(spokes * (fillRings * 2 + 2) * 3);
            for (int s = 0; s < spokes; s++)
            {
                int n = (s + 1) % spokes;
                tris.Add(0); tris.Add(1 + n); tris.Add(1 + s);   // centre fan
            }
            for (int j = 1; j < fillRings; j++)
                for (int s = 0; s < spokes; s++)
                {
                    int n = (s + 1) % spokes;
                    int a = 1 + (j - 1) * spokes + s, b = 1 + (j - 1) * spokes + n, c = 1 + j * spokes + s, d = 1 + j * spokes + n;
                    tris.Add(a); tris.Add(b); tris.Add(d); tris.Add(a); tris.Add(d); tris.Add(c);
                }
            int rimIn = fillCount, rimOut = fillCount + spokes;
            for (int s = 0; s < spokes; s++)
            {
                int n = (s + 1) % spokes;
                int a = rimIn + s, b = rimIn + n, c = rimOut + s, d = rimOut + n;
                tris.Add(a); tris.Add(b); tris.Add(d); tris.Add(a); tris.Add(d); tris.Add(c);
            }
            // drawn from above and below (the shader may cull back faces)
            int t = tris.Count;
            for (int i = 0; i < t; i += 3) { tris.Add(tris[i]); tris.Add(tris[i + 2]); tris.Add(tris[i + 1]); }

            _mesh.Clear();
            _mesh.vertices = _verts;
            _mesh.triangles = tris.ToArray();
            _heights = new float[_fractions.Length, spokes];
            _appliedFill = -1f;
            ApplyVertexAlpha();
        }

        // fill vs rim through vertex alpha; the fill follows the player's Ring Fill Opacity, live
        private int _fillCount;
        private float _appliedFill = -1f;
        private void ApplyVertexAlpha()
        {
            if (_verts == null || _mesh == null) return;
            float fill = Mathf.Clamp01(GroundRingConfig.FillOpacity);
            if (Mathf.Approximately(fill, _appliedFill)) return;
            var colors = new Color[_verts.Length];
            for (int i = 0; i < colors.Length; i++) colors[i] = new Color(1f, 1f, 1f, i < _fillCount ? fill : RimAlpha);
            _mesh.colors = colors;
            _appliedFill = fill;
        }

        private void SampleAllHeights()
        {
            for (int j = 0; j < _fractions.Length; j++)
                for (int s = 0; s < _spokes; s++) SampleHeight(j, s);
            _nextSample = 0;
        }

        private void SampleHeight(int j, int s)
        {
            float a = s * Mathf.PI * 2f / _spokes, r = _fractions[j] * _radius;
            if (_onRing)
            {
                // halo ring: the spoke lies in the tangent plane; the height is the offset along the local up from the centre
                Vector3 hit = GroundHeightSampler.SamplePoint(_center + Spoke(a) * r, GroundOnly ? GroundHeightSampler.GroundOnlyMask : GroundHeightSampler.SolidMask, out _);
                _heights[j, s] = Vector3.Dot(hit - _center, _up);
                return;
            }
            Vector3 p = _center + new Vector3(Mathf.Sin(a) * r, 0f, Mathf.Cos(a) * r);
            _heights[j, s] = GroundOnly ? GroundHeightSampler.Sample(p, _center.y, GroundHeightSampler.GroundOnlyMask) : GroundHeightSampler.Sample(p, _center.y);
        }

        // ground y at fraction f (of the full radius) on spoke s, linear between the sampled rings
        private float HeightAt(float f, int s)
        {
            int last = _fractions.Length - 1;
            if (f <= 0f) return _heights[0, s];
            for (int j = 1; j <= last; j++)
                if (f <= _fractions[j])
                {
                    float span = _fractions[j] - _fractions[j - 1];
                    float k = span > 1e-5f ? (f - _fractions[j - 1]) / span : 0f;
                    return Mathf.Lerp(_heights[j - 1, s], _heights[j, s], k);
                }
            return _heights[last, s];
        }

        private void UpdateMesh(float growth)
        {
            if (_mesh == null || _verts == null) return;
            int fillRings = _fractions.Length - 2;
            _verts[0] = _onRing ? _center + _up * (HeightAt(0f, 0) + HeightOffset) : new Vector3(_center.x, HeightAt(0f, 0) + HeightOffset, _center.z);
            int v = 1;
            for (int j = 1; j <= fillRings; j++) v = WriteRing(v, _fractions[j] * growth);
            v = WriteRing(v, _fractions[fillRings] * growth);   // rim inner edge (rim colour)
            WriteRing(v, growth);                              // outer edge
            _mesh.vertices = _verts;
            _mesh.RecalculateBounds();
        }

        private int WriteRing(int v, float f)
        {
            float r = f * _radius;
            for (int s = 0; s < _spokes; s++)
            {
                float a = s * Mathf.PI * 2f / _spokes;
                _verts[v++] = _onRing ? _center + Spoke(a) * r + _up * (HeightAt(f, s) + HeightOffset) : new Vector3(_center.x + Mathf.Sin(a) * r, HeightAt(f, s) + HeightOffset, _center.z + Mathf.Cos(a) * r);
            }
            return v;
        }

        private void Update()
        {
            float elapsed = Time.time - _startTime;
            if (elapsed >= _duration)
            {
                GroundRingManager.Release(this);
                return;
            }

            // a few ground points a frame (pieces placed or destroyed under the ring)
            int total = _fractions.Length * _spokes;
            for (int i = 0; i < GroundSamplesPerFrame && total > 0; i++)
            {
                SampleHeight(_nextSample / _spokes, _nextSample % _spokes);
                _nextSample = (_nextSample + 1) % total;
            }

            float growth = _grows ? Grow(elapsed) : 1f;
            UpdateMesh(growth);
            _lastGrowth = growth;
            ApplyColor(Envelope(elapsed), elapsed);
        }

        // ease-out from a small dot to the full circle
        private float Grow(float elapsed)
        {
            float t = Mathf.Clamp01(elapsed / _growSeconds);
            return Mathf.Lerp(0.05f, 1f, 1f - (1f - t) * (1f - t));
        }

        private float Envelope(float elapsed)
        {
            if (elapsed < FadeInSeconds) return elapsed / FadeInSeconds;
            float remaining = _duration - elapsed;
            if (remaining < FadeOutSeconds) return Mathf.Max(remaining / FadeOutSeconds, 0f);
            return 1f;
        }

        // Vertex alpha gives fill vs rim; the material colour carries the kind's colour and the fade (alpha only).
        private void ApplyColor(float envelope, float elapsed)
        {
            if (_material == null) return;
            ApplyVertexAlpha();   // live: Ring Fill Opacity
            Color c = GroundRingConfig.ColorFor(_kind);   // the player's colour (or the colour-blind palette)
            float k = Mathf.Max(0f, GroundRingConfig.Intensity);   // Ring Intensity: above 1 the bloom makes it glow
            float pulse = 1f - PulseDepth * 0.5f * (1f + Mathf.Sin(elapsed / PulsePeriodSeconds * Mathf.PI * 2f));
            float a = Mathf.Clamp01(envelope * _opacityScale * GroundRingConfig.Opacity * pulse);
            FxTint.ApplyToMaterial(_material, new Color(c.r * k, c.g * k, c.b * k, a), 1f);
        }

        // Evidence, once per kind a session: the disc's size, grow time, colour and shader.
        private void ReportOnce()
        {
            if (!s_reported.Add(_kind) || _material == null) return;
            Color c = GroundRingConfig.ColorFor(_kind);
            Debug.Log($"[GroundRing] {_kind} disc r={_radius:0.0} m: fill {GroundRingConfig.FillOpacity:0.00} + rim {RimAlpha:0.00}, intensity {GroundRingConfig.Intensity:0.00} " +
                      $"({_spokes} spokes, {_fractions.Length} rings), {(_grows ? $"grows from the centre in {_growSeconds:0.00} s" : "full size (aiming)")}, " +
                      $"colour {c.r:0.00},{c.g:0.00},{c.b:0.00}, {GroundRingSegmentSource.ShaderName} " +
                      $"(palette {(GroundRingConfig.UseColorBlindPalette ? "colour-blind" : "standard")}, ring opacity {GroundRingConfig.Opacity:0.00})");
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
