using System;
using System.Linq;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Shared transparent-background item-icon renderer for the whole Fires mod family — the battle-tested
    /// ItemManager.SnapshotItem recipe. A throwaway camera cleared to <see cref="Color.clear"/> and its directional
    /// light both cull to layer 30 ONLY, and the instantiated model is moved wholesale onto layer 30, so the shot is
    /// perfectly isolated from the live world (object opaque, everything else transparent) — no far-away render stage
    /// needed. A tiny FOV makes it near-orthographic for clean icon framing.
    ///
    /// Call at/after ObjectDB.Awake once the item's materials are real. Headless (no graphics device) returns null so
    /// callers keep their own fallback. Any mod: <c>FiresCore.UI.ItemIconRenderer.Render(prefab)</c> or
    /// <c>RenderItem(itemDrop)</c>.
    /// </summary>
    /// <summary>
    /// Removes components from an INACTIVE hierarchy in [RequireComponent] order. Unity refuses to DestroyImmediate a
    /// component another one requires (it only logs "Can't remove X because Y depends on it"; no exception), so each pass removes
    /// only targets nothing remaining requires; a component that requires a target becomes a target too. Returns how many targets
    /// are still there (0 = clean).
    /// </summary>
    internal static class ComponentStrip
    {
        private static readonly System.Collections.Generic.Dictionary<Type, Type[]> s_requires = new System.Collections.Generic.Dictionary<Type, Type[]>();

        private static Type[] Requires(Type t)
        {
            if (s_requires.TryGetValue(t, out var r)) return r;
            var list = new System.Collections.Generic.List<Type>();
            foreach (RequireComponent a in t.GetCustomAttributes(typeof(RequireComponent), true))
                foreach (var x in new[] { a.m_Type0, a.m_Type1, a.m_Type2 }) if (x != null) list.Add(x);
            return s_requires[t] = list.ToArray();
        }

        private static bool NeedsAny(Component c, System.Collections.Generic.HashSet<Component> targets, GameObject go)
        {
            foreach (var req in Requires(c.GetType()))
                foreach (var other in targets)
                    if (other != null && other != c && other.gameObject == go && req.IsAssignableFrom(other.GetType())) return true;
            return false;
        }

        internal static int Strip(GameObject root, Func<Component, bool> remove)
        {
            var targets = new System.Collections.Generic.HashSet<Component>();
            for (int pass = 0; pass < 12; pass++)
            {
                var all = root.GetComponentsInChildren<Component>(true);
                targets.Clear();
                foreach (var c in all) if (c != null && !(c is Transform) && remove(c)) targets.Add(c);
                // Anything that requires a target must go too (it could never stay without it).
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (var c in all)
                        if (c != null && !(c is Transform) && !targets.Contains(c) && NeedsAny(c, targets, c.gameObject)) { targets.Add(c); grew = true; }
                }
                if (targets.Count == 0) return 0;
                int removed = 0;
                foreach (var t in new System.Collections.Generic.List<Component>(targets))
                {
                    if (t == null) continue;
                    bool required = false;
                    foreach (var other in all)
                        if (other != null && other != t && other.gameObject == t.gameObject)
                            foreach (var req in Requires(other.GetType()))
                                if (req.IsAssignableFrom(t.GetType())) { required = true; break; }
                    if (required) continue;
                    try { UnityEngine.Object.DestroyImmediate(t); } catch { }
                    if (t == null) removed++;
                }
                if (removed == 0) break;
            }
            int left = 0;
            foreach (var c in root.GetComponentsInChildren<Component>(true)) if (c != null && !(c is Transform) && remove(c)) left++;
            return left;
        }
    }

    public static class ItemIconRenderer
    {
        private const int IconLayer = 30;
        private const int IconMask = 1 << IconLayer;

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        /// <summary>Render an <see cref="ItemDrop"/>'s prefab to a transparent icon Sprite. Null on headless/failure.</summary>
        public static Sprite RenderItem(ItemDrop item, int res = 128) => item != null ? Render(item.gameObject, res) : null;

        /// <summary>
        /// BINARY-COMPAT overload — this exact (GameObject, int) signature is what every Fires mod
        /// compiled against pre-0.1.7 Core calls (optional parameters are baked into the CALLER's IL,
        /// so adding a parameter to the method in place broke them all with MissingMethodException).
        /// Core public APIs grow by adding overloads, never by editing signatures.
        /// </summary>
        public static Sprite Render(GameObject prefab, int res = 128) => Render(prefab, res, false);

        /// <summary>
        /// Render <paramref name="prefab"/> (any GameObject with mesh renderers) to a transparent icon Sprite.
        /// Null on a headless server or on failure — the caller should fall back to a placeholder.
        /// <paramref name="frontView"/> switches the standard top-down item framing to a face-on
        /// portrait (camera in front of the model, slight downward tilt) — the right framing for
        /// NPC/character previews, where top-down would show only the top of the head.
        /// </summary>
        public static Sprite Render(GameObject prefab, int res, bool frontView)
        {
            if (IsClientOnly() || prefab == null) return null;
            // No per-frame cap HERE: FDM, FATI and Mausoleum try once and keep a placeholder forever on null. Throttle via RenderQueued.

            Camera cam = null; Light light = null; GameObject model = null; RenderTexture renderTexture = null;
            var prevActive = RenderTexture.active;
            // The tiny-FOV framing puts the camera 70–400m from the model, far enough that Valheim's DISTANCE FOG
            // whites the whole frame out (icons rendered as blank squares whenever the live environment had fog).
            // Fog is world RenderSettings, so disable it for the one Render() call and restore.
            bool prevFog = RenderSettings.fog;
            try
            {
                RenderSettings.fog = false;
                cam = new GameObject("FiresIconCam").AddComponent<Camera>();
                cam.backgroundColor = Color.clear;
                cam.clearFlags = CameraClearFlags.Color;
                cam.fieldOfView = 0.5f;                 // tiny FOV from far back ≈ orthographic
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 1e7f;
                cam.cullingMask = IconMask;
                cam.enabled = false;
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 45f);   // top-down 45° — the standard item-icon angle

                light = new GameObject("FiresIconLight").AddComponent<Light>();
                light.type = LightType.Directional;
                light.cullingMask = IconMask;
                light.intensity = 1.3f;
                light.transform.rotation = Quaternion.Euler(150f, 0f, -5f);

                // Build-menu native crash (ntdll access violation; FATI renders hundreds of Hammer
                // pieces): the copy is made under an INACTIVE parent so no Awake/OnEnable runs (no Piece.s_allPieces, WearNTear,
                // Container, Light, ZNetView...), stripped to its renderers, and only then made active for the shot.
                Trace("ItemIconRenderer.Render begin", prefab.name);
                model = StrippedCopy(prefab);
                foreach (var child in model.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = IconLayer;

                var rends = model.GetComponentsInChildren<Renderer>()
                                 .Where(r => r != null && r.GetType().Name != "ParticleSystemRenderer").ToArray();
                if (rends.Length == 0) return null;
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                foreach (var childRenderer in rends) { min = Vector3.Min(min, childRenderer.bounds.min); max = Vector3.Max(max, childRenderer.bounds.max); }
                Vector3 size = max - min;

                Vector3 center = (min + max) / 2f;
                if (frontView)
                {
                    // Portrait framing: camera on the model's +Z axis looking back at it with a slight
                    // downward tilt; fit the model's HEIGHT (characters are tall, not wide).
                    cam.transform.rotation = Quaternion.Euler(4f, 180f, 0f);
                    float fit = Mathf.Max(size.y, Mathf.Max(size.x, size.z)) * 1.1f
                                / Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad);
                    cam.transform.position = center + new Vector3(0f, size.y * 0.03f, fit);
                    light.transform.rotation = Quaternion.Euler(30f, 200f, 0f);
                    light.transform.position = cam.transform.position;
                }
                else
                {
                    // ItemManager's framing math: pull the camera straight up over the model's top by a distance that fits
                    // its diagonal footprint into the tiny FOV.
                    float dist = (Mathf.Max(size.x, size.z) + Mathf.Min(size.x, size.z)) / Mathf.Sqrt(2f)
                                 / Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad);
                    cam.transform.position = new Vector3(center.x, max.y, center.z) + new Vector3(0f, dist, 0f);
                    light.transform.position = cam.transform.position + new Vector3(-2f, 0f, 0.2f) / 3f * -dist;
                }

                // Skinned characters draw NOTHING on a same-frame manual Render(): the skeleton has
                // never been posed, so the SMR has no valid skinning matrices (items worked, every NPC
                // portrait came out blank). Marketplace's PhotoManager recipe: enable each Animator,
                // play the Movement state when present, and force a synchronous Update(0) so the bones
                // are posed before the camera shot.
                int movementState = Animator.StringToHash("Movement");
                foreach (var animator in model.GetComponentsInChildren<Animator>(true))
                {
                    animator.enabled = true;
                    if (animator.HasState(0, movementState)) animator.Play(movementState);
                    animator.Update(0f);
                }
                foreach (var skinnedRenderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    skinnedRenderer.updateWhenOffscreen = true;

                renderTexture = RenderTexture.GetTemporary(res, res, 16);
                cam.targetTexture = renderTexture;
                cam.Render();

                RenderTexture.active = renderTexture;
                var tex = new Texture2D(res, res, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                tex.ReadPixels(new Rect(0, 0, res, res), 0, 0);
                tex.Apply();
                return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception ex)
            {
                FiresUnifiedCore.Log?.LogWarning($"[FiresIconRenderer] render failed for '{prefab.name}': {ex.Message}");
                return null;
            }
            finally
            {
                RenderSettings.fog = prevFog;
                RenderTexture.active = prevActive;
                if (model != null) UnityEngine.Object.DestroyImmediate(model);
                if (cam != null) { cam.targetTexture = null; UnityEngine.Object.DestroyImmediate(cam.gameObject); }
                if (light != null) UnityEngine.Object.DestroyImmediate(light.gameObject);
                if (renderTexture != null) RenderTexture.ReleaseTemporary(renderTexture);
                Trace("ItemIconRenderer.Render end", prefab.name);
            }
        }

        // ── hardening ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Renders <see cref="RenderQueued"/> does per frame.</summary>
        public const int MaxRendersPerFrame = 4;

        /// <summary>Queues a render; <paramref name="done"/> gets the Sprite (null on headless/failure) on a later frame, at most
        /// <see cref="MaxRendersPerFrame"/> per frame. Add-only API.</summary>
        public static void RenderQueued(GameObject prefab, int res, bool frontView, Action<Sprite> done)
        {
            if (prefab == null || IsClientOnly()) { done?.Invoke(null); return; }
            s_queue.Enqueue(new QueuedRender { Prefab = prefab, Res = res, FrontView = frontView, Done = done });
            if (s_pump == null)
            {
                var go = new GameObject("FiresIconRenderPump") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                s_pump = go.AddComponent<Pump>();
            }
        }

        /// <summary>Frees a Sprite this class made (its Texture2D too). Callers that replace or drop icons should call it.</summary>
        public static void Free(Sprite sprite)
        {
            if (sprite == null) return;
            var tex = sprite.texture;
            UnityEngine.Object.Destroy(sprite);
            if (tex != null) UnityEngine.Object.Destroy(tex);
        }

        private struct QueuedRender { public GameObject Prefab; public int Res; public bool FrontView; public Action<Sprite> Done; }
        private static readonly System.Collections.Generic.Queue<QueuedRender> s_queue = new System.Collections.Generic.Queue<QueuedRender>();
        private static Pump s_pump;

        private sealed class Pump : MonoBehaviour
        {
            private void LateUpdate()
            {
                for (int n = 0; s_queue.Count > 0 && n < MaxRendersPerFrame; n++)
                {
                    var job = s_queue.Dequeue();
                    Sprite s = null;
                    try { s = job.Prefab != null ? Render(job.Prefab, job.Res, job.FrontView) : null; }
                    catch (Exception ex) { FiresUnifiedCore.Log?.LogDebug($"[FiresIconRenderer] queued render failed: {ex.Message}"); }
                    try { job.Done?.Invoke(s); } catch (Exception ex) { FiresUnifiedCore.Log?.LogDebug($"[FiresIconRenderer] queued callback threw: {ex.Message}"); }
                }
            }
        }

        private static GameObject s_inactiveHolder;
        private static readonly Type[] KeepTypes = { typeof(Transform), typeof(MeshFilter), typeof(MeshRenderer), typeof(SkinnedMeshRenderer), typeof(Animator), typeof(LODGroup) };

        // A copy of the prefab with nothing but transforms, meshes, renderers, animators and LOD groups; no script ever woke on it.
        private static GameObject StrippedCopy(GameObject prefab)
        {
            if (s_inactiveHolder == null)
            {
                s_inactiveHolder = new GameObject("FiresIconInactiveHolder") { hideFlags = HideFlags.HideAndDontSave };
                s_inactiveHolder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(s_inactiveHolder);
            }
            var copy = UnityEngine.Object.Instantiate(prefab, s_inactiveHolder.transform, false);
            // Unity refused to remove Humanoid while CharacterDrop depended on it, then the creature woke with no
            // ZNetView): dependency-ordered strip, verified, so nothing that can Awake is left when the copy activates.
            int left = ComponentStrip.Strip(copy, c => Array.IndexOf(KeepTypes, c.GetType()) < 0);
            if (left > 0) { UnityEngine.Object.DestroyImmediate(copy); throw new InvalidOperationException($"{left} component(s) could not be stripped"); }
            copy.transform.SetParent(null, false);   // now active: only renderers and animators wake
            if (!copy.activeSelf) copy.SetActive(true);   // a prefab stored inactive
            copy.transform.position = new Vector3(0f, -9000f, 0f);
            return copy;
        }

        // One flushed line per render into FAT's crash trace (BepInEx\FiresTests\fat_native_trace.log) when FAT is loaded.
        private static Action<string, int, string> s_traceStep;
        private static bool s_traceResolved;
        private static void Trace(string step, string detail)
        {
            try
            {
                if (!s_traceResolved)
                {
                    s_traceResolved = true;
                    var t = FiresCore.Pieces.BuildMenuPages.FatType("VerdantsAscent.Terrain.Voxel.FatNativeTrace");   // no full scan
                    var m = t != null ? HarmonyLib.AccessTools.Method(t, "Step", new[] { typeof(string), typeof(int), typeof(string) }) : null;
                    if (m != null) s_traceStep = (Action<string, int, string>)Delegate.CreateDelegate(typeof(Action<string, int, string>), m);
                }
                s_traceStep?.Invoke("Core " + step, 0, "'" + detail + "'");
            }
            catch { s_traceStep = null; }
        }
    }
}
