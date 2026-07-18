using System;
using System.Collections.Generic;
using SoftReferenceableAssets;
using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// Holds an owning mod's loaded bundle prefabs and the SoftReference plumbing the custom-dungeon pipeline
    /// needs. One instance per dungeon spec / bundle (the per-prefab cache is instance-scoped to avoid two specs
    /// double-registering the same GameObject), while the underlying AssetBundleLoader registry it writes into is
    /// process-global.
    ///
    /// The dungeon is NOT placed by an Instantiate loop — it rides the vanilla world-gen pipeline. That pipeline
    /// (ZoneSystem.ZoneLocation.m_prefab and DungeonDB.RoomData.m_prefab) addresses prefabs by
    /// <see cref="SoftReference{T}"/>, which normally only resolves assets that live in a registered AssetBundle.
    /// To make already-loaded bundle prefabs addressable that way without shipping them through the real
    /// SoftReferenceableAssets loader, each is registered as a pre-loaded AssetLoader entry whose Load()/Asset
    /// resolve to the in-memory GameObject. Ported verbatim from BalrondNature.LocationConstructor
    /// (AddLoadedSoftReferenceAsset) — formerly FiresMausoleum.Dungeon.MausoleumAssets.
    /// </summary>
    public sealed class FiresDungeonAssets
    {
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
            foreach (string n in names)
                if (Get(n) == null) return false;
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
                Array.Resize(ref instance.m_assetLoaders, count + 256);
            instance.m_assetLoaders[count] = assetLoader;
            instance.m_assetIDToLoaderIndex[assetId] = count;
            return new SoftReference<T>(assetId) { m_name = obj.name };
        }
    }
}
