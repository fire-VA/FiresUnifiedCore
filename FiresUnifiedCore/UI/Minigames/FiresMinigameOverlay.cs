using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using HarmonyLib;
using FiresCore.Input;
using FiresCore.UI.ContextMenu;

namespace FiresCore.UI.Minigames
{
    /// <summary>
    /// The shared modal shell every Fires minigame overlay is built on (lock-picking, the gambler's dice/wheel/runes).
    /// A standalone top-level ScreenSpaceOverlay canvas with a dim backdrop and a centred <see cref="PlayField"/> the
    /// game fills with its own art + a driver component. Owns the modality the whole family needs: InputBlock +
    /// FiresInputBlock capture, context-menu stand-down, a freed cursor kept alive every frame, a full-screen flash
    /// overlay, playfield screen-shake, and a ZNet.Shutdown guard so a modal open at logout can't leak the input block.
    /// Client-only — <see cref="Create"/> returns null on a headless server. One overlay is modal at a time.
    /// </summary>
    public sealed class FiresMinigameOverlay : MonoBehaviour
    {
        private static FiresMinigameOverlay _current;
        public static FiresMinigameOverlay Current => _current;

        private readonly object _token = new object();
        private bool _blocked, _closed;

        /// <summary>Centred container the game builds its visuals under. Screen-shake nudges this; flash sits above it.</summary>
        public RectTransform PlayField { get; private set; }
        /// <summary>Invoked exactly once when the overlay closes (Esc/game-driven/logout).</summary>
        public Action OnClosed;

        private Image _flash;
        private Color _flashColor;
        private float _flashPeak, _flashT, _flashDur;
        private float _shakeAmp, _shakeT, _shakeDur;

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        /// <summary>Build + show a fresh modal overlay. Null on a headless server. Closes any overlay already open.</summary>
        public static FiresMinigameOverlay Create(string name, int sortingOrder = 6600) => Create(name, sortingOrder, 0.6f);

        /// <summary>Overload for overlays that live OVER the world (the 3D chess seat): <paramref name="dimAlpha"/> 0
        /// keeps the scene visible and lets clicks through everywhere the game hasn't built UI.</summary>
        public static FiresMinigameOverlay Create(string name, int sortingOrder, float dimAlpha)
        {
            if (IsClientOnly()) return null;
            _current?.Close();

            var go = new GameObject(string.IsNullOrEmpty(name) ? "FiresMinigameOverlay" : name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();

            var overlay = go.AddComponent<FiresMinigameOverlay>();

            var dim = NewImage(go.transform, "Dim", new Color(0f, 0f, 0f, Mathf.Clamp01(dimAlpha)));
            dim.raycastTarget = dimAlpha > 0.001f;
            var dimRect = dim.rectTransform; dimRect.anchorMin = Vector2.zero; dimRect.anchorMax = Vector2.one; dimRect.offsetMin = dimRect.offsetMax = Vector2.zero;

            var playField = new GameObject("PlayField", typeof(RectTransform)).GetComponent<RectTransform>();
            playField.SetParent(go.transform, false);
            playField.anchorMin = playField.anchorMax = new Vector2(0.5f, 0.5f); playField.pivot = new Vector2(0.5f, 0.5f);
            playField.anchoredPosition = Vector2.zero; playField.sizeDelta = Vector2.zero;
            overlay.PlayField = playField;

            var flash = NewImage(go.transform, "Flash", new Color(1f, 1f, 1f, 0f));
            flash.raycastTarget = false;
            var flashRect = flash.rectTransform; flashRect.anchorMin = Vector2.zero; flashRect.anchorMax = Vector2.one; flashRect.offsetMin = flashRect.offsetMax = Vector2.zero;
            overlay._flash = flash;

            overlay.AcquireModal();
            _current = overlay;
            return overlay;
        }

        /// <summary>Apply Averia to every TMP under the overlay (call after the game finishes building its text).</summary>
        public void ApplyFont()
        {
            var averia = UIFontConfig.GetFont(UIFontConfig.FontStyle.AveriaLibre);
            if (averia != null) foreach (var text in GetComponentsInChildren<TMPro.TMP_Text>(true)) text.font = averia;
        }

        /// <summary>Full-screen colour flash that fades over <paramref name="dur"/> (gold on a win, red on a break…).</summary>
        public void Flash(Color color, float peakAlpha = 0.4f, float dur = 0.5f)
        {
            _flashColor = color; _flashPeak = peakAlpha; _flashDur = Mathf.Max(0.01f, dur); _flashT = _flashDur;
            if (_flash != null) _flash.rectTransform.SetAsLastSibling();
        }

        /// <summary>Screen-shake the playfield, decaying to zero over <paramref name="dur"/>.</summary>
        public void Shake(float amplitude = 9f, float dur = 0.4f)
        {
            _shakeAmp = amplitude; _shakeDur = Mathf.Max(0.01f, dur); _shakeT = _shakeDur;
        }

        private void Update()
        {
            if (_closed) return;

            if (_flash != null)
            {
                float alpha = _flashT > 0f ? _flashPeak * (_flashT / _flashDur) : 0f;
                _flash.color = new Color(_flashColor.r, _flashColor.g, _flashColor.b, alpha);
                if (_flashT > 0f) _flashT -= Time.deltaTime;
            }

            if (PlayField != null)
            {
                if (_shakeT > 0f)
                {
                    float shakeStrength = _shakeAmp * (_shakeT / _shakeDur);
                    PlayField.anchoredPosition = new Vector2(Mathf.Sin(Time.time * 71f), Mathf.Cos(Time.time * 47f)) * shakeStrength;
                    _shakeT -= Time.deltaTime;
                }
                else PlayField.anchoredPosition = Vector2.zero;
            }

            // Valheim re-hides the cursor every frame; force it free while the modal is up.
            if (!Cursor.visible || Cursor.lockState != CursorLockMode.None)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        private void AcquireModal()
        {
            FiresContextMenu.SuppressDriver = true;
            if (!InputBlock.IsBlocked) InputBlock.Block(true);
            _blocked = true;
            FiresInputBlock.Acquire(_token);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        /// <summary>Tear down the overlay, release both input gates, fire <see cref="OnClosed"/> once. Idempotent.</summary>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            FiresContextMenu.SuppressDriver = false;
            FiresInputBlock.Release(_token);
            if (_blocked) { InputBlock.Block(false); _blocked = false; }
            if (_current == this) _current = null;
            var callback = OnClosed; OnClosed = null;
            try { callback?.Invoke(); } catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[FiresMinigame] OnClosed threw: {ex.Message}"); }
            if (gameObject != null) UnityEngine.Object.Destroy(gameObject);
        }

        private static Image NewImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>(); img.color = color; return img;
        }

        // Force-clear on logout so a modal open at logout can't leak the input block (matches EntryDoorPanel's guard).
        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        internal static class ZNet_Shutdown_Guard
        {
            private static void Postfix() { if (!IsClientOnly()) { try { _current?.Close(); } catch { } } }
        }
    }
}
