using System;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Seam for loading NPC prefabs from the owning mod's asset bundle (the companion prefab bundle
    /// ships in the host mod, or in FiresCompanions standalone — never in Core). The owner registers
    /// <see cref="LoadPrefab"/> once its bundle is ready; until then the bundle is "not ready" and
    /// loads return null.
    /// </summary>
    public static class NpcAssetBridge
    {
        public static Func<string, GameObject> LoadPrefab;

        /// <summary>True once an owner has registered a prefab loader (its bundle is ready).</summary>
        public static bool IsBundleReady => LoadPrefab != null;

        public static GameObject Load(string prefabPath)
        {
            try { return LoadPrefab?.Invoke(prefabPath); } catch { return null; }
        }
    }
}
