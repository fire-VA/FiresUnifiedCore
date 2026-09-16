using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// NPC portraits, a deliberately faithful port of kg.Marketplace's PhotoManager because hand-rolled offscreen
    /// renderers photographed blank frames. A persistent camera and light far from the world cull only their own layer;
    /// the subject is instantiated under an inactive holder, stripped to visuals (animators disabled, not destroyed),
    /// centered, posed and shot with a narrow-FOV render. Adds a headless guard, fog disabled during the shot, and a
    /// per-shot opaque-pixel log.
    /// </summary>
    public static class NpcPhotoManager
    {
        private const int MainLayer = 31;
        private static readonly Vector3 SpawnPoint = new Vector3(10000f, 10000f, 10000f);
        private static readonly int Movement = Animator.StringToHash("Movement");

        private static GameObject _inactiveHolder;
        private static Camera _camera;
        private static Light _light;
        private static bool _rigBuilt;

        private static readonly Dictionary<Type, List<Type>> _componentDependencies = new Dictionary<Type, List<Type>>();

        private static bool IsHeadless() => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        private static void EnsureRig()
        {
            if (_rigBuilt && _camera != null && _light != null && _inactiveHolder != null) return;
            _rigBuilt = true;

            _inactiveHolder = new GameObject("FiresNpcPhotoINACTIVE") { layer = MainLayer };
            _inactiveHolder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_inactiveHolder);

            _camera = new GameObject("FiresNpcPhotoCamera", typeof(Camera)).GetComponent<Camera>();
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _camera.clearFlags = CameraClearFlags.Color;
            _camera.transform.position = SpawnPoint;
            _camera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            _camera.fieldOfView = 0.5f;
            _camera.farClipPlane = 100000f;
            _camera.cullingMask = int.MinValue;   // ONLY layer 31
            UnityEngine.Object.DontDestroyOnLoad(_camera.gameObject);

            _light = new GameObject("FiresNpcPhotoLight", typeof(Light)).GetComponent<Light>();
            _light.transform.position = SpawnPoint;
            _light.transform.rotation = Quaternion.Euler(5f, 180f, 5f);
            _light.type = LightType.Directional;
            _light.intensity = 0.5f;
            _light.cullingMask = int.MinValue;
            UnityEngine.Object.DontDestroyOnLoad(_light.gameObject);

            _camera.gameObject.SetActive(false);
            _light.gameObject.SetActive(false);
        }

        private static void ClearRendering()
        {
            if (_camera != null) _camera.gameObject.SetActive(false);
            if (_light != null) _light.gameObject.SetActive(false);
        }

        // ── kg component strip: keep visuals, disable animators, destroy the rest in
        //    RequireComponent-safe order ──────────────────────────────────────────────

        private static bool IsVisualComponent(Component component)
        {
            switch (component)
            {
                case Renderer _:
                case MeshFilter _:
                case Transform _:
                case LevelEffects _:
                    return true;
                default:
                    return false;
            }
        }

        private static List<Type> GetComponentDependencies(Type node)
        {
            if (!_componentDependencies.TryGetValue(node, out var list))
            {
                var source = new List<Type>();
                foreach (RequireComponent attr in node.GetCustomAttributes<RequireComponent>(true))
                {
                    source.Add(attr.m_Type0);
                    source.Add(attr.m_Type1);
                    source.Add(attr.m_Type2);
                }
                list = source.Where(t => t != null).Distinct().ToList();
                _componentDependencies[node] = list;
            }
            return list;
        }

        private static bool TopologicalSortUtil(Type node, HashSet<Type> visited, HashSet<Type> recursionStack, List<Type> result)
        {
            if (visited.Contains(node)) return true;
            List<Type> tail = null;
            int index = result.FindIndex(node.IsSubclassOf);
            if (index != -1)
            {
                tail = result.Skip(index).ToList();
                result.RemoveRange(index, result.Count - index);
            }
            if (recursionStack.Contains(node)) return false;
            recursionStack.Add(node);
            foreach (Type dependency in GetComponentDependencies(node))
                if (!TopologicalSortUtil(dependency, visited, recursionStack, result))
                    return false;
            if (tail != null) result.AddRange(tail);
            else result.Add(node);
            recursionStack.Remove(node);
            visited.Add(node);
            return true;
        }

        private static List<Type> GetTopologicalSort(Transform transform)
        {
            var result = new List<Type>();
            var visited = new HashSet<Type>();
            var recursionStack = new HashSet<Type>();
            foreach (Component component in transform.gameObject.GetComponents<Component>())
                if (!TopologicalSortUtil(component.GetType(), visited, recursionStack, result))
                    return null;
            result.Reverse();
            return result;
        }

        private static bool RecursivelyRemoveComponents(Transform parentTransform)
        {
            GameObject go = parentTransform.gameObject;
            for (int i = 0; i < parentTransform.childCount; ++i)
                RecursivelyRemoveComponents(parentTransform.GetChild(i));
            var order = GetTopologicalSort(parentTransform);
            if (order == null) return false;
            foreach (Type type in order)
            {
                foreach (Component component in go.GetComponents(type))
                {
                    if (component is Animator animator)
                        animator.enabled = false;
                    else if (!IsVisualComponent(component))
                    {
                        try { UnityEngine.Object.DestroyImmediate(component); }
                        catch { return false; }
                    }
                }
            }
            return true;
        }

        private static GameObject SpawnAndRemoveComponents(GameObject target)
        {
            GameObject spawn = UnityEngine.Object.Instantiate(target, _inactiveHolder.transform);
            if (!RecursivelyRemoveComponents(spawn.transform))
            {
                UnityEngine.Object.DestroyImmediate(spawn);
                return null;
            }
            spawn.layer = MainLayer;
            foreach (Transform child in spawn.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = MainLayer;
            spawn.transform.SetParent(null);
            spawn.SetActive(true);
            spawn.name = target.name;
            return spawn;
        }

        /// <summary>
        /// Photographs <paramref name="target"/> (a prefab, an inactive clone, or a LIVE body — the
        /// copy is stripped to pure visuals before activation) into a transparent portrait sprite.
        /// Null on headless or when the subject can't be stripped/framed.
        /// </summary>
        public static Sprite MakeSprite(GameObject target, int width = 128, int height = 128, float fieldOfView = 0.5f)
        {
            if (target == null || IsHeadless()) return null;
            EnsureRig();

            GameObject spawn = null;
            try
            {
                _camera.gameObject.SetActive(true);
                _light.gameObject.SetActive(true);

                spawn = SpawnAndRemoveComponents(target);
                if (spawn == null)
                {
                    Debug.LogWarning($"[NpcPhoto] component strip failed for '{target.name}' — no icon");
                    return null;
                }

                var renderers = spawn.GetComponentsInChildren<Renderer>();
                spawn.transform.position = Vector3.zero;
                spawn.transform.rotation = Quaternion.Euler(0f, -24f, 0f);
                Vector3 min = new Vector3(1000f, 1000f, 1000f);
                Vector3 max = new Vector3(-1000f, -1000f, -1000f);
                foreach (Renderer renderer in renderers)
                {
                    if (renderer is ParticleSystemRenderer) continue;
                    min = Vector3.Min(min, renderer.bounds.min);
                    max = Vector3.Max(max, renderer.bounds.max);
                }
                spawn.transform.position = SpawnPoint - (min + max) / 2f;
                Vector3 size = new Vector3(
                    Mathf.Abs(min.x) + Mathf.Abs(max.x),
                    Mathf.Abs(min.y) + Mathf.Abs(max.y),
                    Mathf.Abs(min.z) + Mathf.Abs(max.z));

                return RenderSprite(spawn, size, width, height, fieldOfView);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcPhoto] MakeSprite({target.name}) failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (spawn != null) UnityEngine.Object.DestroyImmediate(spawn);
                ClearRendering();
            }
        }

        private static Sprite RenderSprite(GameObject spawn, Vector3 size, int width, int height, float fieldOfView)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture renderTexture = RenderTexture.GetTemporary(width, height, 32);
            bool previousFog = RenderSettings.fog;
            try
            {
                RenderSettings.fog = false;   // distance fog has whited out far-camera icons before
                _camera.targetTexture = renderTexture;
                _camera.fieldOfView = fieldOfView;
                RenderTexture.active = renderTexture;

                float z = Mathf.Max(size.x, size.y) / Mathf.Tan(fieldOfView * Mathf.Deg2Rad);
                _camera.transform.position = SpawnPoint + new Vector3(0f, 0f, z);

                foreach (Animator animator in spawn.GetComponentsInChildren<Animator>())
                {
                    animator.enabled = true;
                    if (animator.HasState(0, Movement)) animator.Play(Movement);
                    animator.Update(0f);
                }

                _camera.Render();

                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                texture.Apply();

                int opaque = 0;
                const int Grid = 16;
                for (int y = 0; y < Grid; y++)
                    for (int x = 0; x < Grid; x++)
                        if (texture.GetPixel(width * x / Grid, height * y / Grid).a > 0.1f) opaque++;
                Debug.Log($"[NpcPhoto] '{spawn.name}' shot {width}x{height} — opaque samples {opaque}/{Grid * Grid} (camera z offset {Mathf.Max(size.x, size.y) / Mathf.Tan(fieldOfView * Mathf.Deg2Rad):0.#}m)");

                return Sprite.Create(texture, new Rect(0f, 0f, width, height), Vector2.one / 2f);
            }
            finally
            {
                RenderSettings.fog = previousFog;
                RenderTexture.active = previous;
                _camera.targetTexture = null;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }
    }
}
