using UnityEngine;

namespace FiresCore.Npc.Archetypes.Effects
{
    /// <summary>
    /// Supplies ring segments cloned from whatever segment prefab vanilla's own CircleProjector uses (the workbench
    /// and ward build circles), so a ring inherits the exact vanilla shader instead of a hand-built material.
    /// </summary>
    public static class GroundRingSegmentSource
    {
        private static GameObject _template;
        private static bool _templateMissingReported;

        /// <summary>Found by component, not by prefab name: any loaded CircleProjector with a segment will do.</summary>
        private static GameObject FindVanillaSegmentPrefab()
        {
            var projectors = Resources.FindObjectsOfTypeAll<CircleProjector>();
            for (int i = 0; i < projectors.Length; i++)
            {
                var projector = projectors[i];
                if (projector != null && projector.m_prefab != null) return projector.m_prefab;
            }
            return null;
        }

        public static bool Available
        {
            get
            {
                if (_template != null) return true;
                _template = FindVanillaSegmentPrefab();
                if (_template != null) return true;

                if (!_templateMissingReported)
                {
                    _templateMissingReported = true;
                    Debug.LogError(
                        "[GroundRingSegmentSource] No loaded CircleProjector carries a segment prefab - ground " +
                        "rings cannot be built. Vanilla's workbench/ward build circles are the expected source.");
                }
                return false;
            }
        }

        /// <summary>Clones one segment under <paramref name="parent"/>, stripped of everything networked or solid.</summary>
        public static GameObject CreateSegment(Transform parent)
        {
            if (!Available) return null;

            bool previousDisableInit = ZNetView.m_forceDisableInit;
            ZNetView.m_forceDisableInit = true;
            GameObject segment;
            try
            {
                segment = Object.Instantiate(_template, parent.position, Quaternion.identity, parent);
            }
            finally
            {
                ZNetView.m_forceDisableInit = previousDisableInit;
            }

            var netView = segment.GetComponent<ZNetView>();
            if (netView != null) Object.Destroy(netView);

            var colliders = segment.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                if (colliders[i] != null) Object.Destroy(colliders[i]);

            segment.SetActive(true);
            return segment;
        }

        public static Material CloneSegmentMaterial()
        {
            if (!Available) return null;

            var renderer = _template.GetComponentInChildren<Renderer>(true);
            if (renderer == null || renderer.sharedMaterial == null)
            {
                Debug.LogError("[GroundRingSegmentSource] The vanilla segment prefab has no renderer material to clone.");
                return null;
            }
            return new Material(renderer.sharedMaterial);
        }
    }
}
