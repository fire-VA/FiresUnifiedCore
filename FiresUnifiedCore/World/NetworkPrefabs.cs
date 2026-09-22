using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.World
{
    // Registration of a mod's own bundle prefabs with ZNetScene by name, for the Fires mods that ship bundles.
    public static class NetworkPrefabs
    {
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<int, GameObject>> NamedPrefabs =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<int, GameObject>>("m_namedPrefabs");

        // A previous session's bundle prefabs are destroyed at logout but stay listed, and other mods NRE iterating
        // them. Removes those dead entries from m_prefabs and m_namedPrefabs; returns how many were removed.
        public static int ScrubDeadEntries(ZNetScene scene)
        {
            if (scene == null) return 0;
            int removed = scene.m_prefabs.RemoveAll(prefab => prefab == null);

            var named = NamedPrefabs(scene);
            if (named == null) return removed;
            var deadHashes = new List<int>();
            foreach (var entry in named)
                if (entry.Value == null) deadHashes.Add(entry.Key);
            foreach (var hash in deadHashes)
                named.Remove(hash);
            return removed + deadHashes.Count;
        }

        // Makes the prefab spawnable and loadable by name. False when the name is already registered.
        public static bool RegisterByName(ZNetScene scene, GameObject prefab)
        {
            if (scene == null || prefab == null || scene.GetPrefab(prefab.name) != null) return false;
            scene.m_prefabs.Add(prefab);

            var named = NamedPrefabs(scene);
            int hash = prefab.name.GetStableHashCode();
            if (named != null && !named.ContainsKey(hash)) named.Add(hash, prefab);
            return true;
        }
    }
}
