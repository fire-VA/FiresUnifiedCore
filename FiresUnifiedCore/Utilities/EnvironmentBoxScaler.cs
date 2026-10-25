using HarmonyLib;
using UnityEngine;

namespace FiresCore.Utilities
{
    /// <summary>
    /// Handles scaling input for environment boxes.
    /// TEAM RULE: Scroll = width/depth, Shift + Scroll = height (Core RadiusWheel).
    /// While the tool owns the wheel (admin, unlocked box hovered) the game's wheel reads 0, so the camera does not zoom.
    /// </summary>
    public class EnvironmentBoxScaler : MonoBehaviour
    {
        [Header("Scale Settings")]
        public float ScaleStep = 1f;
        public float FineScaleStep = 0.1f;

        private static EnvironmentBoxScaler _instance;
        public static EnvironmentBoxScaler Instance => _instance;

        private EnvironmentBoxController _hoveredBox;
        private float _scrollCooldown = 0f;

        // True while an admin looks at an unlocked box: ZInput.GetMouseScrollWheel then reads 0 for vanilla (camera zoom, hammer).
        internal static bool WheelOwned;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void Update()
        {
            if (_scrollCooldown > 0)
            {
                _scrollCooldown -= Time.deltaTime;
            }

            WheelOwned = false;
            // Only process when player exists and isn't in menu
            if (Player.m_localPlayer == null) return;
            if (InventoryGui.IsVisible()) return;
            if (Menu.IsVisible()) return;
            if (Console.IsVisible()) return;
            if (TextInput.IsVisible()) return;

            // Update hovered box
            UpdateHoveredBox();

            // Lock toggle (Ctrl+L). This must run BEFORE the scale/visibility
            // gates so admins can always unlock a locked box without first
            // unlocking it. The toggle itself enforces admin privileges.
            ProcessLockToggleInput();

            WheelOwned = _hoveredBox != null && !_hoveredBox.Locked && IsLocalPlayerAdmin();
            // Process scaling input (no-op when the hovered box is locked)
            ProcessScaleInput();

            // Process visibility toggle (no-op when the hovered box is locked)
            ProcessVisibilityInput();
        }

        private void ProcessLockToggleInput()
        {
            if (_hoveredBox == null) return;
            if (!IsLocalPlayerAdmin()) return;
            if (FiresCore.Input.FiresInputBlock.IsTyping()) return;   // Any text field, not only chat

            bool ctrl = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
            bool shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            bool alt = UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt);

            // Strict modifier match: Ctrl+L only, no extra modifiers, to avoid
            // colliding with anything else (e.g. Ctrl+Shift+L bound elsewhere).
            if (ctrl && !shift && !alt && UnityEngine.Input.GetKeyDown(KeyCode.L))
            {
                _hoveredBox.ToggleLock();
            }
        }

        private void UpdateHoveredBox()
        {
            _hoveredBox = null;

            var cam = GameCamera.instance?.transform;
            if (cam == null) return;

            Ray ray = new Ray(cam.position, cam.forward);
            RaycastHit hit;

            // Use layermask that includes triggers
            if (Physics.Raycast(ray, out hit, 50f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
            {
                _hoveredBox = hit.collider.GetComponent<EnvironmentBoxController>();
                if (_hoveredBox == null)
                {
                    _hoveredBox = hit.collider.GetComponentInParent<EnvironmentBoxController>();
                }
            }

            // Also check if we're inside an env box (fallback)
            if (_hoveredBox == null)
            {
                _hoveredBox = EnvironmentBoxController.GetPlayerEnvBox();
            }
        }

        private void ProcessScaleInput()
        {
            if (_hoveredBox == null) return;
            if (_scrollCooldown > 0) return;

            // Admin-only scaling
            if (!IsLocalPlayerAdmin()) return;

            // Locked boxes cannot be resized. Silent no-op so an admin
            // accidentally scrolling near a finished box doesn't bump it.
            if (_hoveredBox.Locked) return;

            // Team rule: wheel = width/depth (the radius), Shift+wheel = height; no Ctrl/Alt combos.
            if (!FiresCore.Input.RadiusWheel.Read(out float dRadius, out float dHeight)) return;
            if (dRadius != 0f) _hoveredBox.ScaleHorizontal(dRadius * ScaleStep);
            else _hoveredBox.ScaleHeight(dHeight * ScaleStep);

            // Show feedback
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                $"Size: {_hoveredBox.BoxSize.x:F1} x {_hoveredBox.BoxSize.y:F1} x {_hoveredBox.BoxSize.z:F1}");

            _scrollCooldown = 0.1f;
        }

        private void ProcessVisibilityInput()
        {
            if (_hoveredBox == null) return;

            // Admin-only visibility toggle
            if (!IsLocalPlayerAdmin()) return;

            // Locked boxes block visibility cycle too.
            if (_hoveredBox.Locked) return;

            // Make sure we're not typing
            if (FiresCore.Input.FiresInputBlock.IsTyping()) return;   // Any text field, not only chat

            // Check if shift is held
            bool shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            if (!shift) return;

            // Shift+H key to toggle visibility
            if (UnityEngine.Input.GetKeyDown(KeyCode.H))
            {
                Debug.Log("[EnvironmentBoxScaler] Shift+H pressed, cycling visibility");
                _hoveredBox.CycleVisibility();
            }
        }

        /// <summary>
        /// Checks if the local player has admin privileges.
        /// Uses the same admin check pattern as NpcController.
        /// </summary>
        private static bool IsLocalPlayerAdmin()
        {
            // Server is always admin
            if (ZNet.instance?.IsServer() == true)
                return true;

            // Core admin status: AdminSyncing pushes the server's admin list to ConfigSync.lockExempt per peer.
            if (FiresCore.Sync.ConfigSync.lockExempt)
                return true;

            return false;
        }

        /// <summary>
        /// Ensures the scaler exists.
        /// </summary>
        public static void EnsureExists()
        {
            if (_instance == null)
            {
                var go = new GameObject("EnvironmentBoxScaler");
                go.AddComponent<EnvironmentBoxScaler>();
            }
        }
    }

    // Vanilla's wheel (GameCamera zoom, hammer piece select) reads 0 while the box tool owns it.
    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
    internal static class EnvironmentBoxWheelSwallow
    {
        private static void Postfix(ref float __result)
        {
            if (EnvironmentBoxScaler.WheelOwned) __result = 0f;
        }
    }
}
