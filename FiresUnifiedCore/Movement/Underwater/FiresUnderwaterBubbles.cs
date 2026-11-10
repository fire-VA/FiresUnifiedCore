using FiresCore.Logging;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Movement.Underwater
{
    /// <summary>
    /// Our own underwater bubble particles (replaces the old red-Yagluth borrow).
    /// Builds a MIX of emitters: the procedurally-drawn bubble sprite plus any
    /// authored bubble PNGs found in the bundle (BubblePng1/BubblePng3/…), so the
    /// field has variety. Bubbles are world-simulated (stay put as you swim past),
    /// rise + wobble (noise) + spin + grow-over-life so they read as alive, blue,
    /// and emit only while submerged. Density + rise speed are live-config.
    /// </summary>
    public class FiresUnderwaterBubbles : MonoBehaviour
    {
        public static bool Enabled = true;
        public static float Density = 1f;     // emission-rate multiplier
        public static float Speed = 1f;     // rise-speed multiplier
        public static Color Tint = new Color(0.62f, 0.90f, 1.0f, 0.5f);

        // Authored bubble textures to mix in if present (first list = the names
        // you bundled). The procedural sprite is always included alongside them.
        private static readonly string[] BubbleTexNames = { "BubblePng1", "BubblePng3", "UnderwaterBubble" };
        private const float TargetTotalEmission = 30f;   // bubbles/sec across all emitters at Density 1
        private const int MaxParticlesPer = 220;
        private const float WaterLevel = 30f;   // sea level — bubbles fade out + die here
        private const float SurfaceFadeBand = 2.0f;  // metres below the surface they fade over

        private readonly List<GameObject> m_psGOs = new List<GameObject>();
        private readonly List<ParticleSystem> m_systems = new List<ParticleSystem>();
        private ParticleSystem.Particle[] m_buf;
        private Texture2D m_proceduralTex;
        private bool m_built;

        private void Build()
        {
            m_built = true;
            m_buf = new ParticleSystem.Particle[MaxParticlesPer];

            var sprites = new List<Texture2D> { BuildProceduralBubble() };   // always include the procedural one
            foreach (var name in BubbleTexNames)
            {
                var t = LoadBundleTexture(name);
                if (t != null) { sprites.Add(t); FiresCore.Logging.FiresLogger.LogDebug($"[FiresUnderwaterBubbles] Mixing in bundle bubble '{name}'."); }
            }

            for (int i = 0; i < sprites.Count; i++)
            {
                // alternate the size band so the mix isn't uniform
                float lo = 0.03f + 0.012f * (i % 2);
                float hi = 0.13f + 0.06f * (i % 2);
                BuildEmitter(sprites[i], lo, hi);
            }
        }

        private void BuildEmitter(Texture2D tex, float sizeMin, float sizeMax)
        {
            var go = new GameObject("FiresUnderwaterBubbles_PS");
            Object.DontDestroyOnLoad(go);
            var ps = go.AddComponent<ParticleSystem>();
            var renderer = go.GetComponent<ParticleSystemRenderer>();

            var main = ps.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 4.5f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
            main.startColor = Tint;
            main.gravityModifier = 0f;                                       // rise comes from velocityOverLifetime
            main.simulationSpace = ParticleSystemSimulationSpace.World;      // stay in world as the camera moves
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.2832f);
            main.maxParticles = MaxParticlesPer;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(16f, 9f, 16f);                         // a volume around the camera

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            // All three axis curves must share a mode (TwoConstants) or Unity
            // logs "Particle Velocity curves must all be in the same mode".
            vel.x = new ParticleSystem.MinMaxCurve(0f, 0f);
            vel.y = new ParticleSystem.MinMaxCurve(0.18f, 0.55f);            // gentle upward drift
            vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            // Wobble + distort: noise pushes the rise off a straight line, a slow
            // spin + grow-over-life make each bubble feel alive and expanding as
            // it ascends.
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.35f;
            noise.frequency = 0.4f;
            noise.scrollSpeed = 0.3f;

            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-0.8f, 0.8f);

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            var grow = new AnimationCurve(new Keyframe(0f, 0.7f), new Keyframe(0.5f, 1.0f), new Keyframe(1f, 1.25f));
            sol.size = new ParticleSystem.MinMaxCurve(1f, grow);

            var mat = new Material(Shader.Find("Sprites/Default")) { mainTexture = tex };
            renderer.material = mat;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.sortMode = ParticleSystemSortMode.None;

            ps.Stop();
            m_psGOs.Add(go);
            m_systems.Add(ps);
        }

        // ── ONE BUNDLE SCAN, EVER ─────────────────────────────────────────────────────────────────────────
        // MEASURED on the voxel traverse: without the cache this method was a 1,512 ms MAIN-THREAD STALL the first
        // frame the camera went under water - AssetBundle.LoadAsset x11 plus LoadAllAssets x5 in a single
        // frame, attributed straight to LateUpdate -> Build -> LoadBundleTexture.
        //
        // Two separate reasons, both fixed by caching:
        //   1. The old fallback ran LoadAllAssets<Texture2D>() ONCE PER MISSING NAME. A bundle load is
        //      synchronous, so each of the three names that is not in the bundle paid for all 58 assets again.
        //   2. m_built is per-INSTANCE, so roughly four live components each ran Build() and repeated the lot.
        // A static map, built once from a single scan, serves hits AND misses for the whole session.
        private static Dictionary<string, Texture2D> s_bundleTextures;

        private static Texture2D LoadBundleTexture(string name)
        {
            var bundle = UnderwaterAssets.Bundle;
            if (bundle == null) return null;

            if (s_bundleTextures == null)
            {
                var map = new Dictionary<string, Texture2D>(System.StringComparer.Ordinal);
                try
                {
                    foreach (var x in bundle.LoadAllAssets<Texture2D>())
                        if (x != null && !map.ContainsKey(x.name)) map[x.name] = x;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[FiresUnderwaterBubbles] could not read textures from the water bundle: {ex.Message}");
                }
                // Assigned even when the scan threw, so a broken bundle costs one failure and not one per frame.
                s_bundleTextures = map;
            }

            return s_bundleTextures.TryGetValue(name, out var tex) ? tex : null;
        }

        // Soft bubble: bright thin rim + faint interior + a small offset glint.
        // White (the particle startColor tints it blue).
        private Texture2D BuildProceduralBubble()
        {
            const int size = 64;
            m_proceduralTex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            Vector2 c = new Vector2(size * 0.5f, size * 0.5f);
            float maxR = size * 0.5f - 1f;
            Vector2 glint = c + new Vector2(-size * 0.16f, size * 0.16f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float r = Vector2.Distance(p, c) / maxR;
                    float rim = Mathf.Exp(-Mathf.Pow((r - 0.82f) / 0.10f, 2f));
                    float fill = Mathf.SmoothStep(1f, 0f, r) * 0.10f;
                    float hl = Mathf.Exp(-Mathf.Pow(Vector2.Distance(p, glint) / (size * 0.11f), 2f)) * 0.75f;
                    float a = (r > 1f) ? 0f : Mathf.Clamp01(rim + fill + hl);
                    m_proceduralTex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            m_proceduralTex.Apply();
            return m_proceduralTex;
        }

        private void LateUpdate()
        {
            var cam = Utils.GetMainCamera();
            if (!Enabled || !FiresUnderwaterEnv.Enabled || cam == null) { SetStopped(); return; }
            if (!m_built) Build();
            if (m_systems.Count == 0) return;

            if (!FiresUnderwaterEnv.LocalSubmerged) { SetStopped(); return; }

            float ratePer = TargetTotalEmission * Mathf.Max(0f, Density) / m_systems.Count;
            for (int i = 0; i < m_systems.Count; i++)
            {
                var ps = m_systems[i];
                if (ps == null) continue;
                m_psGOs[i].transform.position = cam.transform.position;
                var em = ps.emission; em.enabled = true; em.rateOverTime = ratePer;
                var vel = ps.velocityOverLifetime;
                vel.y = new ParticleSystem.MinMaxCurve(0.18f * Speed, 0.55f * Speed);
                var noise = ps.noise;
                noise.scrollSpeed = 0.3f * Speed;   // wobble slows with the bubbles, not just the rise
                noise.strength = 0.35f * Mathf.Clamp(Speed, 0.25f, 1.5f);  // calmer wander when slow
                if (!ps.isPlaying) ps.Play();
            }
            FadeAndCullAtSurface();
        }

        // Fade bubbles out as they approach the surface and kill any that cross
        // it, so looking up out of the water you never catch bubbles escaping
        // into the sky. World-simulated, so position.y is true world height.
        private void FadeAndCullAtSurface()
        {
            if (m_buf == null) return;
            for (int s = 0; s < m_systems.Count; s++)
            {
                var ps = m_systems[s];
                if (ps == null || !ps.isPlaying) continue;
                int cnt = ps.GetParticles(m_buf);
                bool changed = false;
                for (int i = 0; i < cnt; i++)
                {
                    float d = WaterLevel - m_buf[i].position.y;   // >0 = below surface
                    if (d <= 0f)
                    {
                        m_buf[i].remainingLifetime = 0f;          // crossed surface — kill
                        changed = true;
                    }
                    else if (d < SurfaceFadeBand)
                    {
                        var c = m_buf[i].startColor;              // fade alpha to 0 over the top band
                        c.a = (byte)(Mathf.Clamp01(d / SurfaceFadeBand) * Tint.a * 255f);
                        m_buf[i].startColor = c;
                        changed = true;
                    }
                }
                if (changed) ps.SetParticles(m_buf, cnt);
            }
        }

        private void SetStopped()
        {
            for (int i = 0; i < m_systems.Count; i++)
            {
                var ps = m_systems[i];
                if (ps == null || !ps.isPlaying) continue;
                var em = ps.emission; em.enabled = false;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);   // let existing bubbles finish rising
            }
        }

        private void OnDestroy()
        {
            foreach (var go in m_psGOs) if (go != null) Object.Destroy(go);
            if (m_proceduralTex != null) Object.Destroy(m_proceduralTex);
        }
    }
}
