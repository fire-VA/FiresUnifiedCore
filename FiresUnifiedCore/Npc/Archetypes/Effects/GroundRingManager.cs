using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Archetypes.Effects
{
    /// <summary>
    /// The one way anything in the stack draws an ability ring. There is no ring RPC: the cast already reaches every
    /// peer through AbilityRPCManager, so each client calls Show for itself and the visibility table decides locally.
    /// Rings are pooled and capped; over the cap the oldest ring is recycled.
    /// </summary>
    public static class GroundRingManager
    {
        private const string ContainerName = "FiresUnifiedCore_GroundRings";
        private const int MaxPooledRings = 48;

        private static bool _uninitializedConfigReported;
        private static Transform _container;
        private static readonly List<GroundRing> _active = new List<GroundRing>();
        private static readonly List<GroundRing> _pool = new List<GroundRing>();

        public static int ActiveCount
        {
            get
            {
                PruneDestroyed();
                return _active.Count;
            }
        }

        /// <summary>Draws a ring on this client if this client's player is allowed to see it; returns null otherwise.</summary>
        public static GroundRing Show(GroundRingKind kind, Vector3 center, float radius, float duration,
            long casterPlayerId, bool aiming)
        {
            ReportUninitializedConfigOnce();

            if (!GroundRingVisibility.ShouldShow(kind, casterPlayerId, aiming, out float opacityScale)) return null;
            if (!GroundRingSegmentSource.Available) return null;

            var container = EnsureContainer();
            if (container == null) return null;

            PruneDestroyed();
            EnforceConcurrencyCap();

            var ring = TakeFromPool(container);
            if (ring == null) return null;

            ring.gameObject.SetActive(true);
            ring.Configure(kind, center, radius, duration, casterPlayerId, aiming, opacityScale);
            _active.Add(ring);
            return ring;
        }

        public static GroundRing Show(GroundRingKind kind, Vector3 center, float radius, float duration,
            long casterPlayerId)
        {
            return Show(kind, center, radius, duration, casterPlayerId, false);
        }

        /// <summary>
        /// Attributes the ring through <see cref="AbilityFXManager.ResolveCasterPlayerId"/>, so a companion's ring
        /// follows its owner's ally relationships and only a genuinely unowned creature gets the show-everyone id 0.
        /// </summary>
        public static GroundRing Show(GroundRingKind kind, Vector3 center, float radius, float duration,
            Character caster, bool aiming)
        {
            return Show(kind, center, radius, duration, AbilityFXManager.ResolveCasterPlayerId(caster), aiming);
        }

        public static void Release(GroundRing ring)
        {
            if (ring == null) return;

            _active.Remove(ring);
            ring.gameObject.SetActive(false);

            if (_pool.Count >= MaxPooledRings)
            {
                Object.Destroy(ring.gameObject);
                return;
            }
            if (!_pool.Contains(ring)) _pool.Add(ring);
        }

        public static void HideAll()
        {
            for (int i = _active.Count - 1; i >= 0; i--) Release(_active[i]);
        }

        /// <summary>
        /// Core's bootstrap binds the ring settings. If a ring is asked for before that, say so instead of silently
        /// drawing on built-in defaults.
        /// </summary>
        private static void ReportUninitializedConfigOnce()
        {
            if (_uninitializedConfigReported || GroundRingConfig.Initialized) return;
            _uninitializedConfigReported = true;

            const string message = "[GroundRings] settings were never bound (GroundRingConfig.Initialize was not " +
                "called from the plugin bootstrap) - rings are drawing on built-in defaults";
            if (FiresUnifiedCore.Log != null) FiresUnifiedCore.Log.LogError(message);
            else Debug.LogError(message);
        }

        private static Transform EnsureContainer()
        {
            if (_container != null) return _container;

            _pool.Clear();
            _active.Clear();

            var host = new GameObject(ContainerName);
            _container = host.transform;
            return _container;
        }

        private static GroundRing TakeFromPool(Transform container)
        {
            for (int i = _pool.Count - 1; i >= 0; i--)
            {
                var pooled = _pool[i];
                _pool.RemoveAt(i);
                if (pooled == null) continue;
                pooled.transform.SetParent(container, false);
                return pooled;
            }

            var host = new GameObject("GroundRing");
            host.transform.SetParent(container, false);
            return host.AddComponent<GroundRing>();
        }

        private static void PruneDestroyed()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
                if (_active[i] == null) _active.RemoveAt(i);
            for (int i = _pool.Count - 1; i >= 0; i--)
                if (_pool[i] == null) _pool.RemoveAt(i);
        }

        private static void EnforceConcurrencyCap()
        {
            int cap = GroundRingConfig.ConcurrentRingCap;
            while (_active.Count >= cap)
            {
                GroundRing oldest = null;
                for (int i = 0; i < _active.Count; i++)
                    if (oldest == null || _active[i].StartTime < oldest.StartTime) oldest = _active[i];

                if (oldest == null) return;
                Release(oldest);
            }
        }
    }
}
