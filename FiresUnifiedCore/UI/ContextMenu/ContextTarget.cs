using UnityEngine;

namespace FiresCore.UI.ContextMenu
{
    /// <summary>
    /// What the cursor is pointing at when a context menu is requested. Physics hits fill
    /// GameObject/Collider/Normal; non-physics "markers" (e.g. patrol node spheres, which have no colliders)
    /// fill Marker/MarkerKind via a registered <see cref="FiresContextMenu.MarkerHitTester"/>. Providers read
    /// this and decide whether to contribute menu rows.
    /// </summary>
    public sealed class ContextTarget
    {
        public GameObject GameObject;   // root the hit resolved to (Character / ZNetView / collider GO)
        public Vector3 Point;           // world hit point
        public Vector3 Normal;          // surface normal (handy for "place here")
        public float Distance;          // along the cursor ray — used to pick the nearest of several hits
        public Collider Collider;       // raw collider (null for non-physics markers)

        public object Marker;           // typed payload for non-physics pickables (e.g. PatrolNodeMarker)
        public string MarkerKind;       // cheap gate before casting Marker (e.g. "PatrolNode")

        /// <summary>Walks up from the resolved GameObject to the requested component (null if absent).</summary>
        public T Find<T>() where T : Component
            => GameObject != null ? GameObject.GetComponentInParent<T>() : null;

        public ZNetView NView => Find<ZNetView>();

        /// <summary>Prefab name behind the hit (via ZDO), falling back to the GameObject name.</summary>
        public string PrefabName
        {
            get
            {
                var nv = NView;
                if (nv != null && nv.GetZDO() != null && ZNetScene.instance != null)
                {
                    var p = ZNetScene.instance.GetPrefab(nv.GetZDO().GetPrefab());
                    if (p != null) return p.name;
                }
                return GameObject != null ? GameObject.name : null;
            }
        }
    }
}
