using System;
using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// Replaces baked "Prop_&lt;vanillaPrefab&gt;" marker transforms inside a spec dungeon's rooms with VISUAL-ONLY
    /// clones of the live ZNetScene prefab: meshes, lights, flicker, particles and plain audio survive, while
    /// ZNetView, Piece/WearNTear/Fireplace and every collider are stripped — the clone owns no ZDO, is invisible
    /// to networking, and can never be interacted with or destroyed. Rooms therefore carry LIT props (a green
    /// crypt torch with its flame flicker) without shipping a single vanilla asset in the bundle — the same
    /// live-asset borrowing the FAT cave clutter uses. Runs per peer (every peer rebuilds room roots locally);
    /// no-op on a headless server, where nothing renders. Idempotent: a marker that already has a child is left
    /// alone, so Generate + Load double-dressing is harmless.
    /// </summary>
    public static class FiresDungeonPropDresser
    {
        /// <summary>Marker naming contract with the Unity builders: "Prop_" + the exact ZNetScene prefab name.</summary>
        public const string MarkerPrefix = "Prop_";

        public static void Dress(DungeonGenerator generator, DungeonSpec spec)
        {
            if (generator == null || spec == null) return;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            var scene = ZNetScene.instance;
            if (scene == null) return;

            int placed = 0, missing = 0;
            foreach (var marker in generator.GetComponentsInChildren<Transform>(true))
            {
                if (marker == null || !marker.name.StartsWith(MarkerPrefix, StringComparison.Ordinal)) continue;
                if (marker.childCount > 0) continue;
                string prefabName = marker.name.Substring(MarkerPrefix.Length);
                GameObject prefab = scene.GetPrefab(prefabName);
                if (prefab == null) { missing++; continue; }
                if (SpawnVisualClone(prefab, marker) != null) placed++;
            }
            if (placed > 0 || missing > 0)
                Debug.Log($"{spec.LogTag} prop dresser: {placed} visual prop(s) placed" +
                          (missing > 0 ? $", {missing} marker prefab(s) not found in ZNetScene" : "") + ".");
        }

        private static GameObject SpawnVisualClone(GameObject prefab, Transform marker)
        {
            // m_forceDisableInit makes ZNetView.Awake a no-op — the vanilla ghost-placement trick — so the
            // clone never creates a ZDO even though the prefab ships one.
            bool prev = ZNetView.m_forceDisableInit;
            ZNetView.m_forceDisableInit = true;
            GameObject clone;
            try { clone = UnityEngine.Object.Instantiate(prefab, marker.position, marker.rotation, marker); }
            finally { ZNetView.m_forceDisableInit = prev; }
            clone.name = prefab.name + "_visual";

            // A torch's flame FX child is toggled by Fireplace from its fuel ZDO; with the Fireplace stripped
            // the baked (off) state would win, so force the enabled-object ON first.
            var fireplace = clone.GetComponentInChildren<Fireplace>(true);
            if (fireplace != null)
            {
                if (fireplace.m_enabledObject != null) fireplace.m_enabledObject.SetActive(true);
                if (fireplace.m_enabledObjectHigh != null) fireplace.m_enabledObjectHigh.SetActive(true);
                if (fireplace.m_enabledObjectLow != null) fireplace.m_enabledObjectLow.SetActive(false);
            }

            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                if (mb is LightFlicker || mb is LightLod) continue;   // the visual animators we keep
                UnityEngine.Object.Destroy(mb);
            }
            foreach (var col in clone.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(col);
            foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.Destroy(rb);
            return clone;
        }
    }
}
