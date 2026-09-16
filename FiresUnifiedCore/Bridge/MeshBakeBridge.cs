using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Opt-out seam between mods that own build pieces and the mesh baker (FiresEasyBakeMeshes). A mod calls
    /// <see cref="ExcludeFromBaking(GameObject)"/> for prefabs the bake must leave alone; the baker asks
    /// <see cref="IsExcludedFromBaking(GameObject)"/> before it combines, instances or skips a piece. Works whether or
    /// not the baker is installed and in either load order, as long as the call happens before the pieces spawn.
    /// </summary>
    public static class MeshBakeBridge
    {
        private static readonly HashSet<int> s_excludedPrefabHashes = new HashSet<int>();

        /// <summary>Raised after a prefab is newly excluded, so a baker can drop per-prefab verdicts it cached.</summary>
        public static event Action ExclusionsChanged;

        public static void ExcludeFromBaking(GameObject prefab)
        {
            if (prefab != null) ExcludeFromBaking(prefab.name);
        }

        public static void ExcludeFromBaking(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName) || !s_excludedPrefabHashes.Add(prefabName.GetStableHashCode())) return;
            try { ExclusionsChanged?.Invoke(); }
            catch (Exception ex) { Debug.LogWarning($"[FiresUnifiedCore] MeshBakeBridge.ExclusionsChanged handler threw: {ex.Message}"); }
        }

        /// <summary>By prefab hash, as <c>ZDO.GetPrefab()</c> returns it.</summary>
        public static bool IsExcludedFromBaking(int prefabHash) => s_excludedPrefabHashes.Contains(prefabHash);

        /// <summary>For a prefab or a spawned instance of it ("Name(Clone)").</summary>
        public static bool IsExcludedFromBaking(GameObject go)
        {
            if (go == null || s_excludedPrefabHashes.Count == 0) return false;
            string name = go.name;
            int cloneSuffix = name.IndexOf('(');
            if (cloneSuffix > 0) name = name.Substring(0, cloneSuffix).TrimEnd();
            return s_excludedPrefabHashes.Contains(name.GetStableHashCode());
        }
    }
}
