using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Stands in for AzuExtendedPlayerInventory's UI behavior (visibility, buttons, player preview, panel toggles) when a
    /// layout depended on it and it is no longer installed. Inventory and equipment slot wiring still needs the real mod.
    /// Attached by <see cref="UIVanillaOverrideManager"/>.
    /// </summary>
    public class UIOverrideAzuEPICompat : MonoBehaviour
    {
        //  Public configuration — set by the wiring code

        public GameObject InventoryRoot;
        public int ExtraRows = 2;
        public bool DisplayEquipmentRowSeparate = true;

        //  Constants matching AzuEPI's naming

        private const string EquipmentBkgName = "AzuEPI_EquipmentBkg";
        private const string PlayerBkgName = "AzuEPI_PlayerBkg";
        private const string PlayerPreviewName = "AzuEPI_PlayerPreview";
        private const string VanityPanelName = "AzuEPI_VanityPanel";
        private const string StatsPanelName = "AzuEPI_StatsPanel";
        private const string LoadoutPanelName = "AzuEPI_LoadoutPanel";
        private const string DropAllButtonName = "AzuEPI_DropAllButton";
        private const string ToggleButtonsName = "AzuEPI_ToggleButtonsGlg";

        //  Managed GameObjects — excluded from SyncInjectedVisibility

        public readonly HashSet<GameObject> ManagedObjects = new HashSet<GameObject>();

        //  Cached references

        private bool _initialized;
        private GameObject _playerPreviewGO;
        private GameObject _vanityPanelGO;
        private GameObject _statsPanelGO;
        private GameObject _loadoutPanelGO;
        private GameObject _equipmentBkgGO;
        private GameObject _playerBkgGO;
        private GameObject _dropAllButtonGO;
        private GameObject _toggleButtonsGO;

        // Player preview camera + drag-to-rotate
        private Camera _previewCamera;
        private RenderTexture _previewRT;
        private RawImage _previewRawImage;
        private float _previewRotationY = 180f; // Start facing camera
        private float _previewDistance = 5.5f;
        private float _previewHeight = 0.85f;

        // Update throttle
        private int _lastUpdateFrame = -1;

        // Panel toggle state
        private bool _previewVisible = true;
        private bool _statsVisible;
        private bool _vanityVisible;
        private bool _loadoutVisible;

        //  Init

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            CacheReferences();
            WireButtons();
            SetupPlayerPreview();
            ApplyPanelStates();
        }

        private void CacheReferences()
        {
            if (InventoryRoot == null) return;

            // Search from Inventory_screen level so we find elements under both
            // Player and Crafting panels
            Transform searchRoot = InventoryRoot.transform;
            if (searchRoot.parent != null)
                searchRoot = searchRoot.parent;

            _playerPreviewGO = FindInHierarchy(searchRoot, PlayerPreviewName);
            _vanityPanelGO = FindInHierarchy(searchRoot, VanityPanelName);
            _statsPanelGO = FindInHierarchy(searchRoot, StatsPanelName);
            _loadoutPanelGO = FindInHierarchy(searchRoot, LoadoutPanelName);
            _equipmentBkgGO = FindInHierarchy(searchRoot, EquipmentBkgName);
            _playerBkgGO = FindInHierarchy(searchRoot, PlayerBkgName);
            _dropAllButtonGO = FindInHierarchy(searchRoot, DropAllButtonName);
            _toggleButtonsGO = FindInHierarchy(searchRoot, ToggleButtonsName);

            // Register ONLY the named AzuEPI panels as managed — NOT entire subtrees.
            // This prevents us from claiming hundreds of vanilla grid elements.
            RegisterManaged(_playerPreviewGO);
            RegisterManaged(_vanityPanelGO);
            RegisterManaged(_statsPanelGO);
            RegisterManaged(_loadoutPanelGO);
            RegisterManaged(_equipmentBkgGO);
            RegisterManaged(_playerBkgGO);
            RegisterManaged(_dropAllButtonGO);
            RegisterManaged(_toggleButtonsGO);

            // Register toggle button children
            if (_toggleButtonsGO != null)
            {
                for (int i = 0; i < _toggleButtonsGO.transform.childCount; i++)
                    ManagedObjects.Add(_toggleButtonsGO.transform.GetChild(i).gameObject);
            }

            Debug.Log($"[UIOverrideAzuEPICompat] Found {ManagedObjects.Count} managed element(s), " +
                $"Preview={_playerPreviewGO != null}, Stats={_statsPanelGO != null}, " +
                $"Vanity={_vanityPanelGO != null}, Loadout={_loadoutPanelGO != null}, " +
                $"DropAll={_dropAllButtonGO != null}, ToggleGroup={_toggleButtonsGO != null}, " +
                $"EquipBkg={_equipmentBkgGO != null}, PlayerBkg={_playerBkgGO != null}");
        }

        private void RegisterManaged(GameObject go)
        {
            if (go != null)
                ManagedObjects.Add(go);
        }

        //  Button wiring

        private void WireButtons()
        {
            // Wire Drop All — use the simple Inventory API, no reflection ambiguity
            if (_dropAllButtonGO != null)
            {
                var btn = _dropAllButtonGO.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(OnDropAllClicked);
                    Debug.Log("[UIOverrideAzuEPICompat] Wired Drop All button");
                }
            }

            // Wire toggle buttons by name
            if (_toggleButtonsGO != null)
            {
                for (int i = 0; i < _toggleButtonsGO.transform.childCount; i++)
                {
                    var child = _toggleButtonsGO.transform.GetChild(i);
                    var btn = child.GetComponent<Button>();
                    if (btn == null) continue;

                    string nameLower = child.name.ToLowerInvariant();
                    btn.onClick.RemoveAllListeners();

                    // AzuEPI naming: AzuEPI_CraftingToggleButton = preview toggle
                    //                AzuEPI_StatsToggleButton, AzuEPI_VanityToggleButton, AzuEPI_LoadoutsToggleButton
                    if (nameLower.Contains("crafting") || nameLower.Contains("preview") ||
                        nameLower.Contains("camera") || nameLower.Contains("model"))
                    {
                        btn.onClick.AddListener(OnTogglePreview);
                        Debug.Log($"[UIOverrideAzuEPICompat] Wired '{child.name}' -> Preview");
                    }
                    else if (nameLower.Contains("stat"))
                    {
                        btn.onClick.AddListener(OnToggleStats);
                        Debug.Log($"[UIOverrideAzuEPICompat] Wired '{child.name}' -> Stats");
                    }
                    else if (nameLower.Contains("vanity") || nameLower.Contains("transmog"))
                    {
                        btn.onClick.AddListener(OnToggleVanity);
                        Debug.Log($"[UIOverrideAzuEPICompat] Wired '{child.name}' -> Vanity");
                    }
                    else if (nameLower.Contains("loadout") || nameLower.Contains("preset"))
                    {
                        btn.onClick.AddListener(OnToggleLoadout);
                        Debug.Log($"[UIOverrideAzuEPICompat] Wired '{child.name}' -> Loadout");
                    }
                    else
                    {
                        Debug.LogWarning($"[UIOverrideAzuEPICompat] Unknown toggle button '{child.name}' — not wired");
                    }
                }
            }
        }

        //  Player preview camera

        private void SetupPlayerPreview()
        {
            if (_playerPreviewGO == null) return;

            _previewRawImage = _playerPreviewGO.GetComponentInChildren<RawImage>(true);
            if (_previewRawImage == null)
            {
                Debug.LogWarning("[UIOverrideAzuEPICompat] PlayerPreview has no RawImage");
                return;
            }

            // Higher-res render texture for full-body view
            _previewRT = new RenderTexture(512, 1024, 24, RenderTextureFormat.ARGB32);
            _previewRT.antiAliasing = 2;
            _previewRT.Create();

            var camGO = new GameObject("AzuEPI_PreviewCamera_Compat");
            // Don't parent to UI — parent to the scene so world-space positioning works
            _previewCamera = camGO.AddComponent<Camera>();
            _previewCamera.targetTexture = _previewRT;
            _previewCamera.clearFlags = CameraClearFlags.SolidColor;
            _previewCamera.backgroundColor = new Color(0, 0, 0, 0);

            // Only render the player character layers
            int charLayer = LayerMask.NameToLayer("character");
            int charTrigLayer = LayerMask.NameToLayer("character_trigger");
            _previewCamera.cullingMask = (charLayer >= 0 ? (1 << charLayer) : 0)
                                       | (charTrigLayer >= 0 ? (1 << charTrigLayer) : 0);
            _previewCamera.fieldOfView = 12f; // Narrow FOV for full-body framing at distance
            _previewCamera.nearClipPlane = 0.1f;
            _previewCamera.farClipPlane = 100f;
            _previewCamera.depth = -10;
            _previewCamera.enabled = false;

            _previewRawImage.texture = _previewRT;
            ManagedObjects.Add(camGO);

            // Attach drag handler for rotate interaction
            var dragHandler = _previewRawImage.gameObject.GetComponent<UIPreviewDragHandler>();
            if (dragHandler == null)
                dragHandler = _previewRawImage.gameObject.AddComponent<UIPreviewDragHandler>();
            dragHandler.Compat = this;

            Debug.Log("[UIOverrideAzuEPICompat] Created player preview camera (FOV=12, dist=5.5)");
        }

        /// <summary>Called by the drag handler when the user drags on the preview RawImage.</summary>
        public void OnPreviewDrag(Vector2 delta)
        {
            _previewRotationY += delta.x * 0.5f;
        }

        //  Toggle callbacks

        private void OnTogglePreview()
        {
            _previewVisible = !_previewVisible;
            ApplyPanelStates();
        }

        private void OnToggleStats()
        {
            _statsVisible = !_statsVisible;
            ApplyPanelStates();
        }

        private void OnToggleVanity()
        {
            _vanityVisible = !_vanityVisible;
            ApplyPanelStates();
        }

        private void OnToggleLoadout()
        {
            _loadoutVisible = !_loadoutVisible;
            ApplyPanelStates();
        }

        private void OnDropAllClicked()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            try
            {
                // Use the public Inventory API directly — no reflection ambiguity
                var inventory = player.GetInventory();
                if (inventory == null) return;

                // Get a snapshot copy of the item list to avoid mutation during iteration
                var items = new List<ItemDrop.ItemData>(inventory.GetAllItems());
                if (items.Count == 0) return;

                int dropped = 0;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var item = items[i];
                    if (item == null) continue;
                    if (item.m_equipped) continue;

                    // Use Player.DropItem which handles creating the ItemDrop in the world
                    player.DropItem(inventory, item, item.m_stack);
                    dropped++;
                }

                Debug.Log($"[UIOverrideAzuEPICompat] Drop All: dropped {dropped} items");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIOverrideAzuEPICompat] Drop All error: {ex.Message}");
            }
        }

        //  Panel visibility

        private void ApplyPanelStates()
        {
            bool inventoryVisible = InventoryGui.IsVisible();

            // PlayerBkg (behind inventory grid) — always follows inventory
            SetPanelActive(_playerBkgGO, inventoryVisible);
            // Equipment bkg — always follows inventory
            SetPanelActive(_equipmentBkgGO, inventoryVisible);
            // Drop All button — follows inventory
            SetPanelActive(_dropAllButtonGO, inventoryVisible);
            // Toggle buttons row — follows inventory
            SetPanelActive(_toggleButtonsGO, inventoryVisible);

            // Toggleable side panels
            SetPanelActive(_playerPreviewGO, inventoryVisible && _previewVisible);
            SetPanelActive(_statsPanelGO, inventoryVisible && _statsVisible);
            SetPanelActive(_vanityPanelGO, inventoryVisible && _vanityVisible);
            SetPanelActive(_loadoutPanelGO, inventoryVisible && _loadoutVisible);
        }

        //  Per-frame update

        private void Update()
        {
            if (Time.frameCount == _lastUpdateFrame) return;
            _lastUpdateFrame = Time.frameCount;

            var player = Player.m_localPlayer;
            if (player == null) return;

            ApplyPanelStates();
            UpdatePlayerPreviewCamera(player);
        }

        private void UpdatePlayerPreviewCamera(Player player)
        {
            if (_previewCamera == null || _previewRT == null) return;
            if (_playerPreviewGO == null || !_playerPreviewGO.activeInHierarchy) return;

            // Calculate camera orbit around the player
            var playerPos = player.transform.position + Vector3.up * _previewHeight;

            float yawRadians = _previewRotationY * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(
                Mathf.Sin(yawRadians) * _previewDistance,
                0.4f,
                Mathf.Cos(yawRadians) * _previewDistance
            );

            _previewCamera.transform.position = playerPos + offset;
            _previewCamera.transform.LookAt(playerPos);

            _previewCamera.Render();
        }

        //  Helpers

        private static void SetPanelActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active)
                go.SetActive(active);
        }

        private static GameObject FindInHierarchy(Transform root, string name)
        {
            if (string.Equals(root.name, name, StringComparison.OrdinalIgnoreCase))
                return root.gameObject;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindInHierarchy(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        //  Cleanup

        private void OnDestroy()
        {
            _initialized = false;

            if (_previewRT != null)
            {
                _previewRT.Release();
                Destroy(_previewRT);
                _previewRT = null;
            }
            if (_previewCamera != null)
            {
                Destroy(_previewCamera.gameObject);
                _previewCamera = null;
            }

            ManagedObjects.Clear();
        }

        //  Static helpers for the wiring system

        public static bool IsAzuEPILoaded()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(asm.GetName().Name, "AzuExtendedPlayerInventory",
                        StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
            return false;
        }

        public static bool WasCapturedWithAzuEPI(UILayoutDefinition layout)
        {
            if (layout == null) return false;
            string deps = layout.GetMeta("dependency_mods");
            if (string.IsNullOrEmpty(deps)) return false;
            return deps.IndexOf("AzuExtendedPlayerInventory", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool TryAttach(UILayoutDefinition layout, GameObject vanillaRoot)
        {
            if (layout == null || vanillaRoot == null) return false;
            if (!WasCapturedWithAzuEPI(layout)) return false;
            if (IsAzuEPILoaded()) return false;

            var existing = vanillaRoot.GetComponent<UIOverrideAzuEPICompat>();
            if (existing != null) return false;

            var compat = vanillaRoot.AddComponent<UIOverrideAzuEPICompat>();
            compat.InventoryRoot = vanillaRoot;

            string extraRowsMeta = layout.GetMeta("azuepi_extra_rows");
            if (!string.IsNullOrEmpty(extraRowsMeta) && int.TryParse(extraRowsMeta, out int extraRows))
                compat.ExtraRows = extraRows;

            string separateMeta = layout.GetMeta("azuepi_separate_equip");
            if (!string.IsNullOrEmpty(separateMeta))
                compat.DisplayEquipmentRowSeparate = string.Equals(separateMeta, "true", StringComparison.OrdinalIgnoreCase);

            compat.Initialize();

            Debug.Log($"[UIOverrideAzuEPICompat] Attached to '{vanillaRoot.name}' " +
                $"(ExtraRows={compat.ExtraRows}, SeparateEquip={compat.DisplayEquipmentRowSeparate})");
            return true;
        }
    }

    /// <summary>
    /// Drag handler for the player preview RawImage. Allows drag-to-rotate the
    /// camera around the player model, matching AzuEPI's interactive preview behavior.
    /// </summary>
    public class UIPreviewDragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IScrollHandler
    {
        public UIOverrideAzuEPICompat Compat;

        public void OnBeginDrag(PointerEventData eventData) { }

        public void OnDrag(PointerEventData eventData)
        {
            if (Compat != null)
                Compat.OnPreviewDrag(eventData.delta);
        }

        public void OnEndDrag(PointerEventData eventData) { }

        public void OnScroll(PointerEventData eventData)
        {
            // Reserved for future zoom support
        }
    }
}
