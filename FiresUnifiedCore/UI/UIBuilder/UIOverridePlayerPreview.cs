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
        public Vector3 CameraOffset = new Vector3(0f, 0.8f, 2.5f);

        /// <summary>Current rotation angle around the player (Y axis).</summary>
        public float RotationAngle = 180f;

        /// <summary>Drag sensitivity for rotation.</summary>
        public float RotationSpeed = 0.5f;

        private RawImage _rawImage;
        private Camera _camera;
        private RenderTexture _renderTexture;
        private GameObject _cameraGO;
        private bool _isDragging;

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
            _camera.fieldOfView = 20f;
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

            _rawImage.texture = _renderTexture;
            _rawImage.color = Color.white;

            Debug.Log($"[UIOverridePlayerPreview] Created preview camera (layer mask: {_camera.cullingMask})");
        }

        // ???????????????????????????????????????
        //  Drag to rotate
        // ???????????????????????????????????????

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
