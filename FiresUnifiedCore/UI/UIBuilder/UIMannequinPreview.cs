using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using FiresCore.Npc;
using FiresCore.Storage;

namespace FiresCore.UI
{
    /// <summary>
    /// Render widget for a REMOTE player's appearance, used by the leaderboard preview box. Sibling to
    /// <see cref="UIOverridePlayerPreview"/> (which hard-binds the LOCAL player and culls the shared
    /// "character" layer) — this one owns an isolated mannequin staged ~2000 m above the player and
    /// renders it with distance isolation (cull ~0, transparent clear), so it can coexist with the live
    /// local player without any bleed.
    ///
    /// It builds ONE rig (camera + RenderTexture + PreviewStage + dedicated light) and reuses it across
    /// rows: <see cref="SetAppearance"/> re-dresses the SAME stage with a freshly-built mannequin rather
    /// than rebuilding the rig. Rendering is on-demand (camera disabled; explicit Render() after the
    /// deferred reframe and while dragging) to avoid per-frame cost/throw surface.
    ///
    /// Attach to a GameObject with a RawImage, or create one via <see cref="Create"/>. Dedi/headless
    /// never creates the rig.
    /// </summary>
    public class UIMannequinPreview : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IScrollHandler
    {
        public int TextureWidth = 512;
        public int TextureHeight = 768;

        public float FieldOfView = 28f;
        public float MinDistance = 0.5f;
        public float MaxDistance = 20f;

        public float YawSpeed = 0.4f;
        public float PitchSpeed = 0.4f;
        public float ZoomSpeed = 1.0f;

        private RawImage _rawImage;
        private Camera _camera;
        private GameObject _cameraGO;
        private RenderTexture _renderTexture;

        private PreviewStage _stage;
        private GameObject _mannequin;
        private Light _keyLight;

        private float _yaw = 180f;
        private float _pitch = 8f;
        private float _distance = 4f;
        private Vector3 _focus = Vector3.zero;

        private bool _isDragging;
        private bool _hasMannequin;

        // SetAppearance may be called before Start() builds the rig (a cached record applied the same frame the
        // widget is created). Hold the latest request and apply it once the rig exists.
        private PlayerAppearance _pendingAppearance;
        private bool _hasPending;

        /// <summary>The RenderTexture for binding to a RawImage directly, if the widget isn't on one.</summary>
        public RenderTexture Texture => _renderTexture;

        /// <summary>True when a mannequin is currently staged and renderable.</summary>
        public bool HasPreview => _hasMannequin;

        /// <summary>
        /// Adds a <see cref="UIMannequinPreview"/> to <paramref name="rawImage"/> and binds it. Returns
        /// null on dedi/headless (no rig is created server-side).
        /// </summary>
        public static UIMannequinPreview Create(RawImage rawImage)
        {
            if (PreviewStage.IsHeadless || rawImage == null) return null;
            var widget = rawImage.gameObject.GetComponent<UIMannequinPreview>();
            if (widget == null) widget = rawImage.gameObject.AddComponent<UIMannequinPreview>();
            return widget;
        }

        private void Start()
        {
            if (PreviewStage.IsHeadless) { enabled = false; return; }

            _rawImage = GetComponent<RawImage>();
            BuildRig();

            if (_hasPending)
            {
                _hasPending = false;
                var pending = _pendingAppearance;
                _pendingAppearance = null;
                SetAppearance(pending);
            }
        }

        private void OnEnable()
        {
            if (_cameraGO != null) _cameraGO.SetActive(true);
        }

        private void OnDisable()
        {
            if (_cameraGO != null) _cameraGO.SetActive(false);
        }

        private void OnDestroy()
        {
            StopAllCoroutines();

            _stage?.Clear();
            _stage = null;
            _mannequin = null;

            if (_camera != null) _camera.targetTexture = null;
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Destroy(_renderTexture);
                _renderTexture = null;
            }
            if (_cameraGO != null) { Destroy(_cameraGO); _cameraGO = null; }
            _camera = null;
            _keyLight = null;
        }

        // ── rig ────────────────────────────────────────────────────────────────

        private void BuildRig()
        {
            _stage = new PreviewStage();

            _renderTexture = new RenderTexture(TextureWidth, TextureHeight, 16, RenderTextureFormat.ARGB32);
            _renderTexture.antiAliasing = 2;
            _renderTexture.Create();

            _cameraGO = new GameObject("FiresMannequinPreviewCamera");
            _cameraGO.transform.SetParent(null);

            _camera = _cameraGO.AddComponent<Camera>();
            _camera.targetTexture = _renderTexture;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0, 0, 0, 0); // transparent
            _camera.cullingMask = ~0;                        // distance isolation, not layer
            _camera.fieldOfView = FieldOfView;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 50f;
            _camera.depth = -10;
            _camera.enabled = false;                         // render-on-demand

            if (_rawImage != null)
            {
                _rawImage.texture = _renderTexture;
                _rawImage.color = Color.white;
            }
        }

        private void EnsureLight()
        {
            // A dedicated light parented to the stage container — scene lighting at +2000 m under a
            // transparent clear can't be relied on. Soft key + low ambient fill.
            if (_stage == null || _stage.Container == null) return;
            if (_keyLight != null)
            {
                _keyLight.transform.SetParent(_stage.Container.transform, false);
                return;
            }

            var lightGO = new GameObject("FiresMannequinKeyLight");
            lightGO.transform.SetParent(_stage.Container.transform, false);
            lightGO.transform.localRotation = Quaternion.Euler(35f, 200f, 0f);
            _keyLight = lightGO.AddComponent<Light>();
            _keyLight.type = LightType.Directional;
            _keyLight.color = Color.white;
            _keyLight.intensity = 1.1f;
            _keyLight.shadows = LightShadows.None;
            _keyLight.renderMode = LightRenderMode.ForcePixel;
        }

        // ── public API ───────────────────────────────────────────────────────

        /// <summary>
        /// (Re)dresses the staged mannequin from <paramref name="appearance"/> and renders once. A null
        /// or empty record clears the box to the transparent "unavailable" state. The rig is reused —
        /// only the mannequin GameObject inside the stage is rebuilt.
        /// </summary>
        public void SetAppearance(PlayerAppearance appearance)
        {
            if (PreviewStage.IsHeadless) return;
            if (_stage == null)
            {
                // Rig not built yet (Start hasn't run); remember the request and apply it then.
                _pendingAppearance = appearance;
                _hasPending = true;
                return;
            }

            StopAllCoroutines();

            if (IsEmpty(appearance))
            {
                ShowUnavailable();
                return;
            }

            var built = FiresMannequinBuilder.Build(appearance);
            if (built == null)
            {
                ShowUnavailable();
                return;
            }

            // Reuse the rig: stage the new mannequin (PreviewStage.Stage clears the previous one).
            if (!_stage.Stage(built, ownsTarget: true))
            {
                Destroy(built);
                ShowUnavailable();
                return;
            }
            _mannequin = built;
            _hasMannequin = true;

            EnsureLight();
            FrameToBounds(_stage.Bounds);
            RenderOnce();

            // Two-frame deferred reframe — SkinnedMeshRenderer bounds are degenerate until bones bind.
            StartCoroutine(_stage.ReframeAfterDelay(b =>
            {
                FrameToBounds(b);
                RenderOnce();
            }));
        }

        /// <summary>Clears the box to the transparent "preview unavailable" state.</summary>
        public void ShowUnavailable()
        {
            _hasMannequin = false;
            _stage?.Clear();
            _mannequin = null;
            RenderOnce(); // transparent clear → empty box
        }

        // ── camera / render ────────────────────────────────────────────────────

        private void FrameToBounds(Bounds bounds)
        {
            _focus = bounds.center;
            float size = bounds.size.magnitude;
            _distance = Mathf.Clamp(Mathf.Max(size * 1.2f, 2f), MinDistance, MaxDistance);
            if (_camera != null)
            {
                _camera.nearClipPlane = Mathf.Max(_distance * 0.01f, 0.02f);
                _camera.farClipPlane = _distance * 10f + 50f;
            }
            ApplyCameraTransform();
        }

        private void ApplyCameraTransform()
        {
            if (_camera == null) return;
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 dir = rot * Vector3.forward;
            _camera.transform.position = _focus - dir * _distance;
            _camera.transform.LookAt(_focus);
        }

        private void RenderOnce()
        {
            if (_camera == null) return;
            ApplyCameraTransform();
            try { _camera.Render(); } catch { }
        }

        // ── input ────────────────────────────────────────────────────────────

        public void OnBeginDrag(PointerEventData eventData) => _isDragging = true;

        public void OnDrag(PointerEventData eventData)
        {
            if (!_isDragging || !_hasMannequin) return;
            _yaw += eventData.delta.x * YawSpeed;
            _pitch = Mathf.Clamp(_pitch - eventData.delta.y * PitchSpeed, -80f, 80f);
            ApplyCameraTransform();
            RenderOnce();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _isDragging = false;
            RenderOnce();
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (!_hasMannequin) return;
            _distance -= eventData.scrollDelta.y * _distance * ZoomSpeed * 0.1f;
            _distance = Mathf.Clamp(_distance, MinDistance, MaxDistance);
            ApplyCameraTransform();
            RenderOnce();
        }

        // ── helpers ────────────────────────────────────────────────────────────

        private static bool IsEmpty(PlayerAppearance a)
        {
            if (a == null) return true;
            // A record with no items, no hair/beard, and no colors is effectively a blank capture.
            return string.IsNullOrEmpty(a.RightItem) && string.IsNullOrEmpty(a.LeftItem)
                && string.IsNullOrEmpty(a.ChestItem) && string.IsNullOrEmpty(a.LegItem)
                && string.IsNullOrEmpty(a.HelmetItem) && string.IsNullOrEmpty(a.ShoulderItem)
                && string.IsNullOrEmpty(a.UtilityItem) && string.IsNullOrEmpty(a.LeftBackItem)
                && string.IsNullOrEmpty(a.RightBackItem) && string.IsNullOrEmpty(a.HairItem)
                && string.IsNullOrEmpty(a.BeardItem)
                && string.IsNullOrEmpty(a.SkinColorRgba) && string.IsNullOrEmpty(a.HairColorRgba);
        }
    }
}
