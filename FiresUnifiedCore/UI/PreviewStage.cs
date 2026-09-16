using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Reusable 3D stage for UI previews: parks a supplied object far above the player inside an invisible collision
    /// container, freezes its physics and AI, forces LOD0 with every renderer on, and frames it with outlier rejection
    /// after the mandatory two-frame delay while skinned bounds settle. The caller owns the camera. Never created on a
    /// headless server.
    /// </summary>
    public class PreviewStage
    {
        private const float ContainerPadding = 5f;
        private const float MinContainerSize = 10f;
        private const float WallThickness = 1f;
        private const float StageAltitude = 2000f;
        private const float MaxRendererExtent = 100f;

        private GameObject _container;
        private GameObject _staged;
        private Bounds _bounds;
        private bool _ownsStaged;

        /// <summary>The container GameObject (placed at altitude). Null until <see cref="Stage"/> succeeds.</summary>
        public GameObject Container => _container;

        /// <summary>The currently staged object. Null when nothing is staged.</summary>
        public GameObject Staged => _staged;

        /// <summary>Framing bounds of the staged object (world space). Valid after <see cref="Stage"/>.</summary>
        public Bounds Bounds => _bounds;

        /// <summary>Center of the staged object's bounds — the camera focus point.</summary>
        public Vector3 FocusPoint => _bounds.center;

        /// <summary>True when an object is staged.</summary>
        public bool HasStaged => _staged != null;

        /// <summary>True on a dedicated/headless server — the rig is never created there.</summary>
        public static bool IsHeadless =>
            Application.isBatchMode ||
            SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null ||
            (ZNet.instance != null && ZNet.instance.IsDedicated());

        /// <summary>
        /// Stages <paramref name="target"/> inside a fresh container ~2000 m above the player. The
        /// existing container (if any) is cleared first. <paramref name="ownsTarget"/> controls whether
        /// <see cref="Clear"/> destroys the target. Returns false on dedi/headless or null target.
        /// </summary>
        public bool Stage(GameObject target, bool ownsTarget)
        {
            if (IsHeadless || target == null) return false;

            Clear();

            try
            {
                _staged = target;
                _ownsStaged = ownsTarget;

                Bounds targetBounds = CalculateBounds(target);
                float innerX = Mathf.Max(targetBounds.size.x + ContainerPadding * 2f, MinContainerSize);
                float innerY = Mathf.Max(targetBounds.size.y + ContainerPadding * 2f, MinContainerSize);
                float innerZ = Mathf.Max(targetBounds.size.z + ContainerPadding * 2f, MinContainerSize);

                Vector3 playerPos = Player.m_localPlayer != null
                    ? Player.m_localPlayer.transform.position
                    : Vector3.zero;
                Vector3 center = new Vector3(playerPos.x, playerPos.y + StageAltitude, playerPos.z);

                _container = new GameObject("FiresPreviewContainer");
                _container.transform.position = center;
                BuildWalls(_container.transform, innerX, innerY, innerZ);

                float floorTopY = center.y - innerY * 0.5f;
                Vector3 spawnPos = new Vector3(center.x, floorTopY + 0.1f, center.z);

                target.transform.SetParent(_container.transform, true);
                target.transform.position = spawnPos;
                target.transform.rotation = Quaternion.identity;
                if (!target.activeSelf) target.SetActive(true);

                FreezePhysics(target);
                PrepareVisuals(target);

                _bounds = CalculateBounds(target);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[PreviewStage] Failed to stage '{(target != null ? target.name : "null")}': {ex.Message}");
                Clear();
                return false;
            }
        }

        /// <summary>
        /// Coroutine the caller starts (it needs a MonoBehaviour host). Waits two frames for the animator
        /// to bind skinned-mesh bones, then recomputes bounds. Calls <paramref name="onReframed"/> with the
        /// fresh (better) bounds so the camera can re-frame and render once. Mandatory after staging a
        /// SkinnedMeshRenderer-bearing object — its bounds are degenerate on the spawn frame.
        /// </summary>
        public IEnumerator ReframeAfterDelay(System.Action<Bounds> onReframed)
        {
            yield return null;
            yield return null;

            if (_staged == null) yield break;

            foreach (var childRenderer in _staged.GetComponentsInChildren<Renderer>(true))
                if (childRenderer != null) childRenderer.enabled = true;
            foreach (var lod in _staged.GetComponentsInChildren<LODGroup>(true))
                if (lod != null) lod.ForceLOD(0);

            var newBounds = CalculateBounds(_staged);
            if (newBounds.size.sqrMagnitude > _bounds.size.sqrMagnitude)
                _bounds = newBounds;

            onReframed?.Invoke(_bounds);
        }

        /// <summary>Destroys the container and (if owned) the staged object.</summary>
        public void Clear()
        {
            if (_staged != null)
            {
                if (_ownsStaged)
                {
                    var netView = _staged.GetComponent<ZNetView>();
                    if (netView != null && netView.IsValid() && ZNetScene.instance != null)
                    {
                        try { ZNetScene.instance.Destroy(_staged); }
                        catch { Object.Destroy(_staged); }
                    }
                    else
                    {
                        Object.Destroy(_staged);
                    }
                }
                _staged = null;
            }

            if (_container != null)
            {
                Object.Destroy(_container);
                _container = null;
            }
        }

        // ── helpers (mirror QuestPreviewRenderer) ────────────────────────────

        private static void BuildWalls(Transform parent, float innerX, float innerY, float innerZ)
        {
            CreateWall(parent, "Floor",
                Vector3.down * (innerY * 0.5f + WallThickness * 0.5f),
                new Vector3(innerX + WallThickness * 2f, WallThickness, innerZ + WallThickness * 2f));
            CreateWall(parent, "Ceiling",
                Vector3.up * (innerY * 0.5f + WallThickness * 0.5f),
                new Vector3(innerX + WallThickness * 2f, WallThickness, innerZ + WallThickness * 2f));
            CreateWall(parent, "WallNX",
                Vector3.left * (innerX * 0.5f + WallThickness * 0.5f),
                new Vector3(WallThickness, innerY, innerZ));
            CreateWall(parent, "WallPX",
                Vector3.right * (innerX * 0.5f + WallThickness * 0.5f),
                new Vector3(WallThickness, innerY, innerZ));
            CreateWall(parent, "WallNZ",
                Vector3.back * (innerZ * 0.5f + WallThickness * 0.5f),
                new Vector3(innerX, innerY, WallThickness));
            CreateWall(parent, "WallPZ",
                Vector3.forward * (innerZ * 0.5f + WallThickness * 0.5f),
                new Vector3(innerX, innerY, WallThickness));
        }

        private static void CreateWall(Transform parent, string name, Vector3 localPos, Vector3 size)
        {
            var wall = new GameObject(name);
            wall.transform.SetParent(parent, false);
            wall.transform.localPosition = localPos;
            wall.layer = LayerMask.NameToLayer("Default");
            var box = wall.AddComponent<BoxCollider>();
            box.size = size;
        }

        /// <summary>
        /// Freezes physics and disables movement/AI MonoBehaviours on the staged object so it holds a
        /// static pose. Renderers are never touched. Matches QuestPreviewRenderer.FreezePhysics.
        /// </summary>
        public static void FreezePhysics(GameObject go)
        {
            foreach (var body in go.GetComponentsInChildren<Rigidbody>(true))
            {
                if (body == null) continue;
                body.isKinematic = true;
                body.useGravity = false;
                body.constraints = RigidbodyConstraints.FreezeAll;
            }

            foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                switch (behaviour.GetType().Name)
                {
                    case "StaticPhysics":
                    case "Floating":
                    case "Ship":
                    case "ShipEffects":
                    case "Fish":
                    case "Projectile":
                    case "Aoe":
                    case "BaseAI":
                    case "AnimalAI":
                    case "MonsterAI":
                    case "Humanoid":
                    case "Character":
                    case "Destructible":
                    case "TreeBase":
                    case "TreeLog":
                    case "Ragdoll":
                    case "FootStep":
                    case "Windmill":
                    case "WaterVolume":
                    case "ZNetView":
                        behaviour.enabled = false;
                        break;
                }
            }
        }

        /// <summary>Forces LOD0, enables all renderers. Matches QuestPreviewRenderer.PrepareVisuals.</summary>
        public static void PrepareVisuals(GameObject go)
        {
            foreach (var lod in go.GetComponentsInChildren<LODGroup>(true))
                if (lod != null) lod.ForceLOD(0);

            foreach (var childRenderer in go.GetComponentsInChildren<Renderer>(true))
                if (childRenderer != null) childRenderer.enabled = true;
        }

        /// <summary>
        /// Multi-renderer bounds with outlier rejection (skips particle renderers, oversized renderers,
        /// and renderers far from the object origin). Falls back to mesh-filter bounds, then a unit box.
        /// Matches QuestPreviewRenderer.CalculateBounds.
        /// </summary>
        public static Bounds CalculateBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(go.transform.position, Vector3.one);

            Bounds? result = null;

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                if (renderers[i] is ParticleSystemRenderer) continue;

                Bounds rendererBounds = renderers[i].bounds;
                if (rendererBounds.size.x > MaxRendererExtent || rendererBounds.size.y > MaxRendererExtent || rendererBounds.size.z > MaxRendererExtent) continue;
                if (Vector3.Distance(rendererBounds.center, go.transform.position) > MaxRendererExtent * 2f) continue;

                if (result == null) result = rendererBounds;
                else { var merged = result.Value; merged.Encapsulate(rendererBounds); result = merged; }
            }

            if (result == null)
            {
                var meshFilters = go.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < meshFilters.Length; i++)
                {
                    if (meshFilters[i]?.sharedMesh == null) continue;
                    var meshBounds = meshFilters[i].sharedMesh.bounds;
                    Vector3 worldCenter = meshFilters[i].transform.TransformPoint(meshBounds.center);
                    Vector3 worldSize = Vector3.Scale(meshBounds.size, meshFilters[i].transform.lossyScale);
                    var worldBounds = new Bounds(worldCenter, worldSize);
                    if (result == null) result = worldBounds;
                    else { var merged = result.Value; merged.Encapsulate(worldBounds); result = merged; }
                }
            }

            if (result == null)
                return new Bounds(go.transform.position, Vector3.one);

            var bounds = result.Value;
            if (bounds.size.sqrMagnitude < 0.01f)
                bounds.size = Vector3.one;
            return bounds;
        }
    }
}
