using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Creates a camera that renders the local player model to a RenderTexture,
    /// then displays it on the attached RawImage. Supports drag-to-rotate.
    ///
    /// Attach to any GameObject with a RawImage component tagged
    /// <see cref="UIOverrideElementTags.PlayerPreview"/> or
    /// <see cref="UIOverrideElementTags.CameraDisplay"/>.
    ///
    /// The camera is positioned relative to the player's visual transform and
    /// renders only the Player layer so the background is transparent.
    /// </summary>
    public class UIOverridePlayerPreview : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        /// <summary>Resolution of the render texture.</summary>
        public int TextureWidth = 256;
        public int TextureHeight = 512;

        /// <summary>Camera offset from the player's chest.</summary>
        // Aim at body center (y=1.0, the LookAt target) and stand back (z) to frame a standing player head-to-foot
        // with a little margin at FOV 22. z pulled back slightly from 4.6 → 5.2 for a touch more headroom/footroom.
        public Vector3 CameraOffset = new Vector3(0f, 1.0f, 5.2f);

        /// <summary>Current rotation angle around the player (Y axis).</summary>
        public float RotationAngle = 180f;

        /// <summary>Drag sensitivity for rotation.</summary>
        public float RotationSpeed = 0.5f;

        private RawImage _rawImage;
        private Camera _camera;
        private RenderTexture _renderTexture;
        private GameObject _cameraGO;
        private bool _isDragging;

        // The layer the preview camera renders. Attached equipment (helmet, cape, shoulders, weapons) is instantiated
        // from item prefabs that keep their OWN layer, not the player's "character" layer — so a character-only cull
        // shows the body + skinned armor but drops every attached piece. We re-stamp the whole visual subtree onto
        // this layer each frame so the camera renders the full equipped look. The main camera already renders
        // "character", so this is invisible in-game; equipment visuals have no gameplay colliders, so it's side-effect free.
        private int _previewLayer = -1;

        private void Start()
        {
            _rawImage = GetComponent<RawImage>();
            if (_rawImage == null)
            {
                Debug.LogWarning("[UIOverridePlayerPreview] No RawImage component found. Cannot display player preview.");
                enabled = false;
                return;
            }

            CreateCameraAndTexture();
        }

        private void OnEnable()
        {
            if (_cameraGO != null)
                _cameraGO.SetActive(true);
        }

        private void OnDisable()
        {
            if (_cameraGO != null)
                _cameraGO.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_cameraGO != null)
                Destroy(_cameraGO);
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Destroy(_renderTexture);
            }
        }

        private void LateUpdate()
        {
            if (_camera == null || Player.m_localPlayer == null) return;

            // Position camera relative to the player
            var player = Player.m_localPlayer;
            Transform visual = player.transform;
            try
            {
                var fi_visual = AccessTools.Field(typeof(Character), "m_visual");
                if (fi_visual != null)
                {
                    var visualGO = fi_visual.GetValue(player) as GameObject;
                    if (visualGO != null) visual = visualGO.transform;
                }
            }
            catch { }

            // Pull every renderer in the player's visual onto the camera's render layer so attached equipment shows.
            if (_previewLayer >= 0)
            {
                var renderers = visual.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    var go = renderers[i].gameObject;
                    if (go.layer != _previewLayer) go.layer = _previewLayer;
                }
            }

            Vector3 center = visual.position + Vector3.up * CameraOffset.y;

            float rad = RotationAngle * Mathf.Deg2Rad;
            Vector3 camPos = center + new Vector3(
                Mathf.Sin(rad) * CameraOffset.z,
                0f,
                Mathf.Cos(rad) * CameraOffset.z
            );

            _camera.transform.position = camPos;
            _camera.transform.LookAt(center);
        }

        private void CreateCameraAndTexture()
        {
            // Create render texture
            _renderTexture = new RenderTexture(TextureWidth, TextureHeight, 16, RenderTextureFormat.ARGB32);
            _renderTexture.antiAliasing = 2;
            _renderTexture.Create();

            // Create camera
            _cameraGO = new GameObject("UIOverride_PlayerPreviewCamera");
            _cameraGO.transform.SetParent(null);

            _camera = _cameraGO.AddComponent<Camera>();
            _camera.targetTexture = _renderTexture;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0, 0, 0, 0); // transparent
            _camera.cullingMask = LayerMask.GetMask("character"); // Valheim player layer
            _camera.fieldOfView = 22f;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 50f;
            _camera.depth = -10; // don't affect main camera
            _camera.enabled = true;

            // If "character" layer doesn't exist, try common alternatives
            if (_camera.cullingMask == 0)
            {
                _camera.cullingMask = LayerMask.GetMask("Character", "player", "Player");
            }
            // Last resort: try the layer the player is on
            if (_camera.cullingMask == 0 && Player.m_localPlayer != null)
            {
                _camera.cullingMask = 1 << Player.m_localPlayer.gameObject.layer;
            }

            // The single layer we re-stamp the visual onto (first layer in the final cull mask).
            _previewLayer = FirstLayerInMask(_camera.cullingMask);

            _rawImage.texture = _renderTexture;
            _rawImage.color = Color.white;

            Debug.Log($"[UIOverridePlayerPreview] Created preview camera (layer mask: {_camera.cullingMask})");
        }

        private static int FirstLayerInMask(int mask)
        {
            if (mask == 0) return -1;
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) return i;
            return -1;
        }

        // ---------------------------------------
        //  Drag to rotate
        // ---------------------------------------

        public void OnBeginDrag(PointerEventData eventData)
        {
            _isDragging = true;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_isDragging) return;
            RotationAngle += eventData.delta.x * RotationSpeed;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _isDragging = false;
        }
    }
}
