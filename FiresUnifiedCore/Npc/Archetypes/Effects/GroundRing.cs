using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Archetypes.Effects
{
    /// <summary>
    /// A vanilla CircleProjector rebuilt as a pooled, emissive, pattern-driven ability ring. Vanilla raycasts every
    /// segment every frame; this samples a fixed height profile around the circle and refreshes a couple of samples
    /// per frame, so a dozen rings cost about what one vanilla build circle does.
    /// </summary>
    public class GroundRing : MonoBehaviour
    {
        private const float RingHeightOffset = 0.05f;
        private const float FadeInSeconds = 0.2f;
        private const float FadeOutSeconds = 0.4f;
        private const float PulsePeriodSeconds = 1.2f;
        private const float PulseDepth = 0.25f;
        private const float BaseEmissionIntensity = 2.2f;
        private const float RippleSeconds = 1.1f;
        private const int RippleSegmentCount = 16;
        private const float RippleOpacityScale = 0.7f;
        private const int CrossTickSegmentsPerTick = 2;
        private const float CrossTickScale = 0.55f;
        private const int GroundSamplesPerFrame = 2;
        private const float MinimumDuration = 0.1f;

        private readonly List<Transform> _mainSegments = new List<Transform>();
        private readonly List<Transform> _rippleSegments = new List<Transform>();
        private readonly List<Transform> _tickSegments = new List<Transform>();
        private readonly List<float> _groundHeights = new List<float>();

        private GroundRingKind _kind;
        private GroundRingPattern _pattern;
        private Material _mainMaterial;
        private Material _rippleMaterial;
        private Vector3 _center;
        private float _radius;
        private float _duration;
        private float _startTime;
        private float _opacityScale;
        private long _casterPlayerId;
        private int _nextSampleIndex;
        private bool _built;

        public bool Aiming { get; set; }
        public long CasterPlayerId => _casterPlayerId;
        public float StartTime => _startTime;

        /// <summary>Rebuilds the segment sets when the kind changes and restarts the ring's timeline.</summary>
        public void Configure(GroundRingKind kind, Vector3 center, float radius, float duration,
            long casterPlayerId, bool aiming, float opacityScale)
        {
            bool kindChanged = !_built || _kind != kind;
            _kind = kind;
            _pattern = GroundRingPalette.PatternFor(kind);
            _center = center;
            _radius = Mathf.Max(radius, Mathf.Epsilon);
            _duration = Mathf.Max(duration, MinimumDuration);
            _casterPlayerId = casterPlayerId;
            _opacityScale = opacityScale;
            Aiming = aiming;
            _startTime = Time.time;
            _nextSampleIndex = 0;

            transform.position = center;

            if (kindChanged)
            {
                BuildSegments();
                _built = true;
            }

            ApplyPaletteColor();
            SampleWholeGroundProfile();
            PlaceSegments(0f, 1f);
        }

        private void BuildSegments()
        {
            ClearSegments();

            if (_mainMaterial == null) _mainMaterial = GroundRingSegmentSource.CloneSegmentMaterial();
            if (_rippleMaterial == null) _rippleMaterial = GroundRingSegmentSource.CloneSegmentMaterial();
            if (_mainMaterial == null || _rippleMaterial == null) return;

            for (int i = 0; i < _pattern.SegmentCount; i++)
            {
                bool visible = _pattern.DashStride <= 1 || (i % _pattern.DashStride) != 0;
                var segment = CreateSegment(_mainMaterial, Vector3.one);
                if (segment == null) return;
                segment.gameObject.SetActive(visible);
                _mainSegments.Add(segment);
            }

            if (_pattern.Ripple != GroundRingRipple.None)
            {
                for (int i = 0; i < RippleSegmentCount; i++)
                {
                    var segment = CreateSegment(_rippleMaterial, Vector3.one);
                    if (segment == null) return;
                    _rippleSegments.Add(segment);
                }
            }

            int tickSegments = _pattern.CrossTickCount * CrossTickSegmentsPerTick;
            for (int i = 0; i < tickSegments; i++)
            {
                var segment = CreateSegment(_mainMaterial, Vector3.one * CrossTickScale);
                if (segment == null) return;
                _tickSegments.Add(segment);
            }

            _groundHeights.Clear();
            for (int i = 0; i < _pattern.SegmentCount; i++) _groundHeights.Add(_center.y);
        }

        private Transform CreateSegment(Material material, Vector3 localScale)
        {
            var segment = GroundRingSegmentSource.CreateSegment(transform);
            if (segment == null) return null;

            segment.transform.localScale = localScale;
            var renderers = segment.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null) renderers[i].sharedMaterial = material;

            return segment.transform;
        }

        private void ClearSegments()
        {
            DestroyAll(_mainSegments);
            DestroyAll(_rippleSegments);
            DestroyAll(_tickSegments);
        }

        private static void DestroyAll(List<Transform> segments)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i] == null) continue;
                segments[i].gameObject.SetActive(false);
                Destroy(segments[i].gameObject);
            }
            segments.Clear();
        }

        private void ApplyPaletteColor()
        {
            Color color = GroundRingPalette.ColorFor(_kind, GroundRingConfig.UseColorBlindPalette);
            FxTint.ApplyToMaterial(_mainMaterial, color, BaseEmissionIntensity);
            FxTint.ApplyToMaterial(_rippleMaterial, color, BaseEmissionIntensity);
        }

        private void SampleWholeGroundProfile()
        {
            for (int i = 0; i < _groundHeights.Count; i++) SampleGroundAt(i);
        }

        private void SampleGroundAt(int index)
        {
            if (index < 0 || index >= _groundHeights.Count) return;
            float angle = index * Mathf.PI * 2f / _groundHeights.Count;
            Vector3 point = _center + new Vector3(Mathf.Sin(angle) * _radius, 0f, Mathf.Cos(angle) * _radius);
            _groundHeights[index] = GroundHeightSampler.Sample(point, _center.y);
        }

        private float GroundHeightAtAngle(float angle)
        {
            int count = _groundHeights.Count;
            if (count == 0) return _center.y;

            float normalized = angle / (Mathf.PI * 2f) * count;
            int lower = Mathf.FloorToInt(normalized);
            float fraction = normalized - lower;
            int lowerIndex = ((lower % count) + count) % count;
            int upperIndex = (lowerIndex + 1) % count;
            return Mathf.Lerp(_groundHeights[lowerIndex], _groundHeights[upperIndex], fraction);
        }

        private void Update()
        {
            float elapsed = Time.time - _startTime;
            if (elapsed >= _duration)
            {
                GroundRingManager.Release(this);
                return;
            }

            RefreshGroundSamples();

            float envelope = Envelope(elapsed);
            PlaceSegments(elapsed, envelope);
        }

        private void RefreshGroundSamples()
        {
            if (_groundHeights.Count == 0) return;
            for (int i = 0; i < GroundSamplesPerFrame; i++)
            {
                SampleGroundAt(_nextSampleIndex);
                _nextSampleIndex = (_nextSampleIndex + 1) % _groundHeights.Count;
            }
        }

        private float Envelope(float elapsed)
        {
            if (elapsed < FadeInSeconds) return elapsed / FadeInSeconds;
            float remaining = _duration - elapsed;
            if (remaining < FadeOutSeconds) return Mathf.Max(remaining / FadeOutSeconds, 0f);
            return 1f;
        }

        private void PlaceSegments(float elapsed, float envelope)
        {
            float spin = elapsed * _pattern.TurnsPerSecond * Mathf.PI * 2f;
            float pulse = 1f + PulseDepth * Mathf.Sin(elapsed / PulsePeriodSeconds * Mathf.PI * 2f);
            float opacity = envelope * _opacityScale * GroundRingConfig.Opacity;

            PlaceMainRing(spin);
            PlaceRipple(elapsed, spin, opacity);
            PlaceCrossTicks(spin);

            Color color = GroundRingPalette.ColorFor(_kind, GroundRingConfig.UseColorBlindPalette);
            FxTint.ApplyToMaterial(_mainMaterial, ScaleColor(color, opacity), BaseEmissionIntensity * pulse);
        }

        private void PlaceMainRing(float spin)
        {
            int count = _mainSegments.Count;
            for (int i = 0; i < count; i++)
            {
                var segment = _mainSegments[i];
                if (segment == null) continue;

                float angle = spin + i * Mathf.PI * 2f / count;
                float segmentRadius = _radius * (1f - (i % 2) * _pattern.SawtoothFraction);
                PlaceSegmentOnCircle(segment, angle, segmentRadius, false);
            }
        }

        private void PlaceRipple(float elapsed, float spin, float opacity)
        {
            if (_rippleSegments.Count == 0) return;

            float phase = Mathf.Repeat(elapsed, RippleSeconds) / RippleSeconds;
            float rippleRadius = _pattern.Ripple == GroundRingRipple.Inward
                ? _radius * (1f - phase)
                : _radius * phase;
            float rippleFade = Mathf.Sin(phase * Mathf.PI) * RippleOpacityScale;

            for (int i = 0; i < _rippleSegments.Count; i++)
            {
                var segment = _rippleSegments[i];
                if (segment == null) continue;
                float angle = -spin + i * Mathf.PI * 2f / _rippleSegments.Count;
                PlaceSegmentOnCircle(segment, angle, rippleRadius, false);
            }

            Color color = GroundRingPalette.ColorFor(_kind, GroundRingConfig.UseColorBlindPalette);
            FxTint.ApplyToMaterial(_rippleMaterial, ScaleColor(color, opacity * rippleFade), BaseEmissionIntensity);
        }

        private void PlaceCrossTicks(float spin)
        {
            if (_tickSegments.Count == 0) return;

            for (int tick = 0; tick < _pattern.CrossTickCount; tick++)
            {
                float angle = spin + tick * Mathf.PI * 2f / _pattern.CrossTickCount;
                int baseIndex = tick * CrossTickSegmentsPerTick;
                if (baseIndex + 1 >= _tickSegments.Count) return;

                PlaceSegmentOnCircle(_tickSegments[baseIndex], angle, _radius, false);
                PlaceSegmentOnCircle(_tickSegments[baseIndex + 1], angle, _radius, true);
            }
        }

        private void PlaceSegmentOnCircle(Transform segment, float angle, float segmentRadius, bool radialFacing)
        {
            if (segment == null) return;

            float sin = Mathf.Sin(angle);
            float cos = Mathf.Cos(angle);
            Vector3 position = _center + new Vector3(sin * segmentRadius, 0f, cos * segmentRadius);
            position.y = GroundHeightAtAngle(Mathf.Repeat(angle, Mathf.PI * 2f)) + RingHeightOffset;
            segment.position = position;

            Vector3 tangent = new Vector3(cos, 0f, -sin);
            Vector3 radial = new Vector3(sin, 0f, cos);
            segment.rotation = Quaternion.LookRotation(radialFacing ? radial : tangent, Vector3.up);
        }

        private static Color ScaleColor(Color color, float factor)
        {
            return new Color(color.r * factor, color.g * factor, color.b * factor, color.a * factor);
        }

        private void OnDestroy()
        {
            if (_mainMaterial != null) Destroy(_mainMaterial);
            if (_rippleMaterial != null) Destroy(_rippleMaterial);
        }
    }
}
