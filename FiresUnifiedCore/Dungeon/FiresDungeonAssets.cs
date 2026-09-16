using System;
using System.Collections.Generic;
using SoftReferenceableAssets;
using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// A dungeon spec's loaded bundle prefabs, made addressable to the vanilla world-gen pipeline. ZoneLocation and
    /// RoomData refer to prefabs by SoftReference, which normally resolves only registered bundles, so each prefab is
    /// registered as a pre-loaded AssetLoader entry pointing at the in-memory object (ported from BalrondNature's
    /// LocationConstructor). The prefab cache is per instance so two specs never double-register an object.
    /// </summary>
    public sealed class FiresDungeonAssets
    {
        private const int AssetLoaderCapacityIncrement = 256;

        private readonly Dictionary<string, GameObject> _prefabsByName =
            new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<GameObject, SoftReference<GameObject>> _softRefs =
            new Dictionary<GameObject, SoftReference<GameObject>>();

        /// <summary>Register the bundle's GameObjects so the engine can look them up by name.</summary>
        public void SetPrefabs(IEnumerable<GameObject> prefabs)
        {
            if (prefabs == null) return;
            foreach (GameObject go in prefabs)
            {
                if (go == null) continue;
                _prefabsByName[go.name] = go;
            }
        }

        public GameObject Get(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            _prefabsByName.TryGetValue(prefabName, out GameObject go);
            return go;
        }

        public bool HasAll(params string[] names)
        {
            foreach (string assetName in names)
                if (Get(assetName) == null) return false;
            return true;
        }

        /// <summary>
        /// A SoftReference that resolves to the given already-loaded prefab. Cached per prefab so repeated
        /// registration (DungeonDB.Start re-runs, relogin) reuses one loader entry.
        /// </summary>
        public SoftReference<GameObject> SoftRefFor(GameObject prefab)
        {
            if (prefab == null) return default;
            if (_softRefs.TryGetValue(prefab, out SoftReference<GameObject> existing))
                return existing;
            SoftReference<GameObject> created = AddLoadedSoftReferenceAsset(prefab);
            _softRefs[prefab] = created;
            return created;
        }

        // ── ported from BalrondNature.LocationConstructor ────────────────────────

        private static AssetID AssetIDFromObject(UnityEngine.Object obj)
        {
            return new AssetID(1U, 1U, 1U, (uint)obj.GetInstanceID());
        }

        private static SoftReference<T> AddLoadedSoftReferenceAsset<T>(T obj) where T : UnityEngine.Object
        {
            AssetBundleLoader instance = AssetBundleLoader.Instance;
            instance.m_bundleNameToLoaderIndex[""] = 0;
            AssetID assetId = AssetIDFromObject(obj);
            AssetLoader assetLoader = new AssetLoader(assetId, new AssetLocation("", ""))
            {
                m_asset = obj,
                m_referenceCounter = new ReferenceCounter(2U),
                m_shouldBeLoaded = true,
            };
            int count = instance.m_assetIDToLoaderIndex.Count;
            if (count >= instance.m_assetLoaders.Length)
                Array.Resize(ref instance.m_assetLoaders, count + AssetLoaderCapacityIncrement);
            instance.m_assetLoaders[count] = assetLoader;
            instance.m_assetIDToLoaderIndex[assetId] = count;
            return new SoftReference<T>(assetId) { m_name = obj.name };
        }
    }
}
