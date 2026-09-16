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

                ZNetView.m_forceDisableInit = true;
                try { model = UnityEngine.Object.Instantiate(prefab); }
                finally { ZNetView.m_forceDisableInit = false; }
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
                if (model != null) UnityEngine.Object.Destroy(model);
                if (cam != null) { cam.targetTexture = null; UnityEngine.Object.Destroy(cam.gameObject); }
                if (light != null) UnityEngine.Object.Destroy(light.gameObject);
                if (renderTexture != null) RenderTexture.ReleaseTemporary(renderTexture);
            }
        }
    }
}
