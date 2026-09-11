using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

namespace FiresCore.UI
{
    /// <summary>
    /// Self-contained slot management system for the UI override/builder.
    /// Reads live game state (Player inventory, equipped items) and provides
    /// a unified slot abstraction for the override UI to mirror.
    ///
    /// Discovery strategy (in priority order):
    /// 1. Reflect into the live VAInventory Slots class if loaded � reads real
    ///    slot IDs, grid positions, active states and InventoryHeightPlayer.
    /// 2. Fallback: scan items at grid positions beyond the vanilla visible
    ///    height and guess slot types from item data.
    /// </summary>
    public static class UIOverrideSlotSystem
    {
        // ???????????????????????????????????????
        //  Slot ID constants
        // ???????????????????????????????????????

        public const string HelmetSlotID = "Helmet";
        public const string ChestSlotID = "Chest";
        public const string LegsSlotID = "Legs";
        public const string ShoulderSlotID = "Shoulder";
        public const string UtilitySlotID = "Utility";
        public const string TrinketSlotID = "Trinket";
        public const string FoodSlotID = "Food";
        public const string AmmoSlotID = "Ammo";
        public const string MiscSlotID = "Misc";
        public const string QuickSlotID = "Quick";
        public const string EmptySlotID = "Empty";

        // ???????????????????????????????????????
        //  Slot data class
        // ???????????????????????????????????????

        public class Slot
        {
            public string ID;
            public bool IsActive;
            public Vector2i GridPosition;
            public string Name;
            public string ShortcutText;
            public bool IsEquipmentSlot;
            public bool IsHotbarSlot;
            public Func<ItemDrop.ItemData, bool> ItemFits;

            public ItemDrop.ItemData Item
            {
                get
                {
                    var inv = PlayerInventory;
                    if (inv == null || GridPosition.x < 0 || GridPosition.y < 0) return null;
                    return inv.GetItemAt(GridPosition.x, GridPosition.y);
                }
            }

            public bool IsFree => Item == null;

            public bool IsAmmoSlot => string.Equals(ID, AmmoSlotID, StringComparison.OrdinalIgnoreCase);
            public bool IsFoodSlot => string.Equals(ID, FoodSlotID, StringComparison.OrdinalIgnoreCase);
            public bool IsMiscSlot => ID != null && ID.StartsWith(MiscSlotID, StringComparison.OrdinalIgnoreCase);
            public bool IsQuickSlot => string.Equals(ID, QuickSlotID, StringComparison.OrdinalIgnoreCase);
            public bool IsTrinketSlot => string.Equals(ID, TrinketSlotID, StringComparison.OrdinalIgnoreCase);

            public string GetShortcutText() => ShortcutText ?? Name ?? "";
        }

        // ???????????????????????????????????????
        //  Slot registries
        // ???????????????????????????????????????

        public static readonly Dictionary<string, Slot> slots =
            new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);

        public static readonly List<Slot> toolHotbarSlots = new List<Slot>();

        public static Slot[] AllSlots
        {
            get
            {
                var list = new List<Slot>(slots.Values);
                list.AddRange(toolHotbarSlots);
                return list.ToArray();
            }
        }

        // ???????????????????????????????????????
        //  Convenience accessors
        // ???????????????????????????????????????

        public static Player CurrentPlayer => Player.m_localPlayer;
        public static Inventory PlayerInventory => CurrentPlayer?.GetInventory();

        public static int InventoryWidth => PlayerInventory?.GetWidth() ?? 8;

        /// <summary>
        /// The VISIBLE player inventory height (vanilla 4 + any extra rows from
        /// inventory mods). NOT the full extended grid height that includes
        /// hidden equipment rows. Never returns less than 4 (vanilla default).
        /// </summary>
        public static int InventoryHeight
        {
            get
            {
                // Always re-read from the live Slots class if available, so we
                // never latch onto a stale or too-early value from reflection.
                if (_pi_heightPlayer != null)
                {
                    try
                    {
                        int live = (int)_pi_heightPlayer.GetValue(null);
                        if (live >= 4) return live;
                    }
                    catch { }
                }
                return 4;
            }
        }

        public static int InventorySizePlayer => InventoryHeight * InventoryWidth;


        // ???????????????????????????????????????
        //  Reflection cache for VAInventory Slots
        // ???????????????????????????????????????

        private static bool _reflectionAttempted;
        private static Type _slotsType;
        private static FieldInfo _fi_slotsArray;         // Slots.slots (Slot[])
        private static FieldInfo _fi_toolHotbarSlots;    // Slots.toolHotbarSlots (List<Slot>)
        private static PropertyInfo _pi_heightPlayer;    // Slots.InventoryHeightPlayer
        private static PropertyInfo _pi_inventorySizePlayer; // Slots.InventorySizePlayer

        // Slot inner class reflection
        private static PropertyInfo _pi_slotID;
        private static PropertyInfo _pi_slotIsActive;
        private static PropertyInfo _pi_slotGridPosition;
        private static PropertyInfo _pi_slotName;
        private static PropertyInfo _pi_slotIsEquipmentSlot;
        private static PropertyInfo _pi_slotIsFree;
        private static MethodInfo _mi_slotItemFits;
        private static MethodInfo _mi_slotGetShortcutText;

        private static readonly Vector2i EmptyPosition = new Vector2i(-1, -1);

        /// <summary>
        /// Attempts to cache reflection handles for the VAInventory Slots class.
        /// Only runs once. Returns true if the class was found and cached.
        /// </summary>
        private static bool TryReflectSlotsClass()
        {
            if (_reflectionAttempted) return _slotsType != null;
            _reflectionAttempted = true;

            try
            {
                // Search all loaded assemblies for VerdantsAscent.Slots
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        _slotsType = asm.GetType("VerdantsAscent.Slots", false);
                        if (_slotsType != null) break;
                    }
                    catch { }
                }

                if (_slotsType == null) return false;

                _fi_slotsArray = _slotsType.GetField("slots", BindingFlags.Public | BindingFlags.Static);
                _fi_toolHotbarSlots = _slotsType.GetField("toolHotbarSlots", BindingFlags.Public | BindingFlags.Static);
                _pi_heightPlayer = _slotsType.GetProperty("InventoryHeightPlayer", BindingFlags.Public | BindingFlags.Static);
                _pi_inventorySizePlayer = _slotsType.GetProperty("InventorySizePlayer", BindingFlags.Public | BindingFlags.Static);

                // Cache Slot inner class members
                var slotInnerType = _slotsType.GetNestedType("Slot", BindingFlags.Public);
                if (slotInnerType != null)
                {
                    _pi_slotID = slotInnerType.GetProperty("ID");
                    _pi_slotIsActive = slotInnerType.GetProperty("IsActive");
                    _pi_slotGridPosition = slotInnerType.GetProperty("GridPosition");
                    _pi_slotName = slotInnerType.GetProperty("Name");
                    _pi_slotIsEquipmentSlot = slotInnerType.GetProperty("IsEquipmentSlot");
                    _pi_slotIsFree = slotInnerType.GetProperty("IsFree");
                    _mi_slotItemFits = slotInnerType.GetMethod("ItemFits");
                    _mi_slotGetShortcutText = slotInnerType.GetMethod("GetShortcutText");
                }

                Debug.Log($"[UIOverrideSlotSystem] Reflected VAInventory Slots class � " +
                    $"slotsArray={_fi_slotsArray != null}, hotbar={_fi_toolHotbarSlots != null}, " +
                    $"heightPlayer={_pi_heightPlayer != null}");

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIOverrideSlotSystem] Reflection into Slots class failed: {ex.Message}");
                _slotsType = null;
                return false;
            }
        }

        // ???????????????????????????????????????
        //  Item type validators
        // ???????????????????????????????????????

        public static bool IsHelmetItem(ItemDrop.ItemData item) =>
            item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Helmet;
        public static bool IsChestItem(ItemDrop.ItemData item) =>
            item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Chest;
        public static bool IsLegsItem(ItemDrop.ItemData item) =>
            item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Legs;
        public static bool IsShoulderItem(ItemDrop.ItemData item) =>
            item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shoulder;
        public static bool IsUtilityItem(ItemDrop.ItemData item) =>
            item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Utility;
        public static bool IsTrinketItem(ItemDrop.ItemData item) =>
            item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Trinket;
        public static bool IsAmmoItem(ItemDrop.ItemData item) =>
            item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo;
        public static bool IsFoodItem(ItemDrop.ItemData item) =>
            item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable && item.m_shared.m_food > 0;
        public static bool IsToolItem(ItemDrop.ItemData item) =>
            item?.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool;
        public static bool IsEquipmentSlotItem(ItemDrop.ItemData item) =>
            item != null && (IsHelmetItem(item) || IsChestItem(item) || IsLegsItem(item) ||
                            IsShoulderItem(item) || IsUtilityItem(item) || IsTrinketItem(item));

        public static Func<ItemDrop.ItemData, bool> GetValidatorForSlotID(string slotID)
        {
            if (string.IsNullOrEmpty(slotID)) return item => true;
            switch (slotID)
            {
                case HelmetSlotID: return IsHelmetItem;
                case ChestSlotID: return IsChestItem;
                case LegsSlotID: return IsLegsItem;
                case ShoulderSlotID: return IsShoulderItem;
                case UtilitySlotID: return IsUtilityItem;
                case TrinketSlotID: return IsTrinketItem;
                case FoodSlotID: return IsFoodItem;
                case AmmoSlotID: return IsAmmoItem;
                default: return item => true;
            }
        }

        // ???????????????????????????????????????
        //  Slot lookup
        // ???????????????????????????????????????

        public static Slot GetSlotByID(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (slots.TryGetValue(id, out var s)) return s;
            foreach (var hs in toolHotbarSlots)
            {
                if (hs != null && string.Equals(hs.ID, id, StringComparison.OrdinalIgnoreCase))
                    return hs;
            }
            return null;
        }

        public static Slot GetSlotInGrid(Vector2i pos)
        {
            foreach (var s in slots.Values)
            {
                if (s != null && s.IsActive && s.GridPosition == pos)
                    return s;
            }
            foreach (var s in toolHotbarSlots)
            {
                if (s != null && s.IsActive && s.GridPosition == pos)
                    return s;
            }
            return null;
        }

        public static Slot GetItemSlot(ItemDrop.ItemData item)
        {
            if (item == null) return null;
            var inv = PlayerInventory;
            if (inv == null || !inv.ContainsItem(item)) return null;
            return GetSlotInGrid(item.m_gridPos);
        }

        public static bool IsGridPositionASlot(Vector2i gridPos) =>
            GetSlotInGrid(gridPos) != null;

        public static bool IsItemInSlot(ItemDrop.ItemData item) =>
            item != null && IsGridPositionASlot(item.m_gridPos);

        public static bool IsItemInEquipmentSlot(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            var slot = GetItemSlot(item);
            return slot != null && slot.IsEquipmentSlot;
        }

        // ???????????????????????????????????????
        //  Slot finding (for item placement)
        // ???????????????????????????????????????

        public static bool TryFindFreeSlotForItem(ItemDrop.ItemData item, out Slot slot)
        {
            slot = null;
            if (item == null) return false;

            foreach (var s in slots.Values)
            {
                if (s != null && s.IsActive && s.IsEquipmentSlot && s.IsFree && s.ItemFits(item))
                {
                    slot = s;
                    return true;
                }
            }

            foreach (var s in slots.Values)
            {
                if (s != null && s.IsActive && s.IsFree && s.ItemFits(item))
                {
                    slot = s;
                    return true;
                }
            }

            return false;
        }

        // ???????????????????????????????????????
        //  Slot registration (called by wiring code)
        // ???????????????????????????????????????

        public static Slot RegisterSlot(string id, Vector2i gridPos, string name,
            bool isEquipment = false, Func<ItemDrop.ItemData, bool> itemFits = null)
        {
            var slot = new Slot
            {
                ID = id,
                GridPosition = gridPos,
                Name = name ?? id,
                IsActive = true,
                IsEquipmentSlot = isEquipment,
                IsHotbarSlot = false,
                ItemFits = itemFits ?? GetValidatorForSlotID(id)
            };
            slots[id] = slot;
            return slot;
        }

        public static Slot RegisterHotbarSlot(int index, Vector2i gridPos, string name,
            string shortcutText = null, Func<ItemDrop.ItemData, bool> itemFits = null)
        {
            var slot = new Slot
            {
                ID = $"ToolHotbar{index + 1}",
                GridPosition = gridPos,
                Name = name ?? $"Tool {index + 1}",
                ShortcutText = shortcutText,
                IsActive = true,
                IsEquipmentSlot = false,
                IsHotbarSlot = true,
                ItemFits = itemFits ?? IsToolItem
            };

            while (toolHotbarSlots.Count <= index)
                toolHotbarSlots.Add(null);
            toolHotbarSlots[index] = slot;
            return slot;
        }

        public static void ClearSlots()
        {
            slots.Clear();
            toolHotbarSlots.Clear();
        }

        // ???????????????????????????????????????
        //  Inventory helpers
        // ???????????????????????????????????????

        public static bool TryFindFreeInventorySlot(out Vector2i freePos)
        {
            freePos = new Vector2i(-1, -1);
            var inv = PlayerInventory;
            if (inv == null) return false;

            int width = InventoryWidth;
            int visibleHeight = InventoryHeight;

            for (int y = 0; y < visibleHeight; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (inv.GetItemAt(x, y) == null)
                    {
                        freePos = new Vector2i(x, y);
                        return true;
                    }
                }
            }

            for (int y = visibleHeight - 1; y >= 0; y--)
            {
                for (int x = width - 1; x >= 0; x--)
                {
                    var item = inv.GetItemAt(x, y);
                    if (item == null) continue;

                    if (TryFindFreeSlotForItem(item, out var freeSlot))
                    {
                        freePos = item.m_gridPos;
                        item.m_gridPos = freeSlot.GridPosition;
                        return true;
                    }
                }
            }

            return false;
        }

        // ???????????????????????????????????????
        //  Dynamic discovery from live game
        // ???????????????????????????????????????

        /// <summary>
        /// Discovers equipment/extra slots from the live game state.
        /// First attempts to read the VAInventory Slots class via reflection
        /// for accurate data. Falls back to item-type heuristics otherwise.
        /// </summary>
        public static void DiscoverSlotsFromGameState()
        {
            var inv = PlayerInventory;
            if (inv == null) return;

            // Try reading the real Slots system first
            if (TryDiscoverFromVAInventory())
                return;

            // Fallback: heuristic discovery using vanilla visible height
            DiscoverFromHeuristics(inv);
        }

        /// <summary>
        /// Reads the live VerdantsAscent.Slots class via reflection and populates
        /// our registries with accurate slot data. Returns true if successful.
        /// </summary>
        private static bool TryDiscoverFromVAInventory()
        {
            if (!TryReflectSlotsClass()) return false;

            try
            {
                // Read the main slots array
                var slotsArray = _fi_slotsArray?.GetValue(null) as Array;
                if (slotsArray == null) return false;

                int inventorySizePlayer = _pi_inventorySizePlayer != null
                    ? (int)_pi_inventorySizePlayer.GetValue(null)
                    : InventoryHeight * InventoryWidth;

                int discovered = 0;
                foreach (var liveSlot in slotsArray)
                {
                    if (liveSlot == null) continue;

                    string id = _pi_slotID?.GetValue(liveSlot) as string;
                    if (string.IsNullOrEmpty(id)) continue;

                    bool isActive = _pi_slotIsActive != null && (bool)_pi_slotIsActive.GetValue(liveSlot);
                    if (!isActive) continue;

                    Vector2i gridPos = _pi_slotGridPosition != null
                        ? (Vector2i)_pi_slotGridPosition.GetValue(liveSlot)
                        : EmptyPosition;
                    if (gridPos.x < 0 || gridPos.y < 0) continue;

                    string name = _pi_slotName?.GetValue(liveSlot) as string ?? id;
                    bool isEquip = _pi_slotIsEquipmentSlot != null && (bool)_pi_slotIsEquipmentSlot.GetValue(liveSlot);

                    // Build validator that delegates to the live slot's ItemFits
                    Func<ItemDrop.ItemData, bool> validator;
                    if (_mi_slotItemFits != null)
                    {
                        var capturedSlot = liveSlot;
                        validator = item =>
                        {
                            try { return (bool)_mi_slotItemFits.Invoke(capturedSlot, new object[] { item }); }
                            catch { return true; }
                        };
                    }
                    else
                    {
                        validator = GetValidatorForSlotID(id);
                    }

                    // Use grid-position based key to handle duplicate IDs (e.g. multiple Food slots)
                    string key = $"{id}_{gridPos.x}_{gridPos.y}";

                    if (slots.TryGetValue(key, out var existing))
                    {
                        existing.GridPosition = gridPos;
                        existing.IsActive = isActive;
                        existing.IsEquipmentSlot = isEquip;
                        existing.Name = name;
                    }
                    else
                    {
                        slots[key] = new Slot
                        {
                            ID = id,
                            GridPosition = gridPos,
                            Name = name,
                            IsActive = isActive,
                            IsEquipmentSlot = isEquip,
                            IsHotbarSlot = false,
                            ItemFits = validator
                        };
                    }
                    discovered++;
                }

                // Read tool hotbar slots
                toolHotbarSlots.Clear();
                var liveHotbar = _fi_toolHotbarSlots?.GetValue(null) as System.Collections.IList;
                if (liveHotbar != null)
                {
                    for (int i = 0; i < liveHotbar.Count; i++)
                    {
                        var hbSlot = liveHotbar[i];
                        if (hbSlot == null)
                        {
                            toolHotbarSlots.Add(null);
                            continue;
                        }

                        string hbId = _pi_slotID?.GetValue(hbSlot) as string ?? $"ToolHotbar{i + 1}";
                        Vector2i hbPos = _pi_slotGridPosition != null
                            ? (Vector2i)_pi_slotGridPosition.GetValue(hbSlot) : EmptyPosition;
                        string hbName = _pi_slotName?.GetValue(hbSlot) as string ?? $"Tool {i + 1}";
                        string hbShortcut = _mi_slotGetShortcutText != null
                            ? _mi_slotGetShortcutText.Invoke(hbSlot, null) as string : null;

                        toolHotbarSlots.Add(new Slot
                        {
                            ID = hbId,
                            GridPosition = hbPos,
                            Name = hbName,
                            ShortcutText = hbShortcut,
                            IsActive = true,
                            IsEquipmentSlot = false,
                            IsHotbarSlot = true,
                            ItemFits = IsToolItem
                        });
                    }
                }

                Debug.Log($"[UIOverrideSlotSystem] Discovered {discovered} slot(s), " +
                    $"{toolHotbarSlots.Count} hotbar slot(s) from VAInventory " +
                    $"(VisibleHeight={InventoryHeight})");

                return discovered > 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIOverrideSlotSystem] VAInventory reflection discovery failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Fallback discovery when VAInventory is not loaded. Uses vanilla
        /// visible height (4 rows) and scans items beyond that boundary.
        /// </summary>
        private static void DiscoverFromHeuristics(Inventory inv)
        {
            int width = inv.GetWidth();
            // Use vanilla visible height (4) � NOT inv.GetHeight() which may
            // include hidden equipment rows added by inventory mods
            int visibleHeight = InventoryHeight;

            foreach (var item in inv.GetAllItems())
            {
                if (item.m_gridPos.y >= visibleHeight || item.m_gridPos.x >= width)
                {
                    string slotId = GuessSlotID(item);
                    // Use grid-position key to handle duplicate IDs
                    string key = $"{slotId}_{item.m_gridPos.x}_{item.m_gridPos.y}";
                    if (!slots.ContainsKey(key))
                    {
                        slots[key] = new Slot
                        {
                            ID = slotId,
                            GridPosition = item.m_gridPos,
                            Name = slotId,
                            IsActive = true,
                            IsEquipmentSlot = true,
                            IsHotbarSlot = false,
                            ItemFits = MakeValidatorForType(item.m_shared.m_itemType)
                        };
                    }
                    else
                    {
                        var existing = slots[key];
                        if (existing.GridPosition != item.m_gridPos)
                            existing.GridPosition = item.m_gridPos;
                    }
                }
            }

            // Also check InventoryGrid elements for empty slots beyond visible area
            if (InventoryGui.instance != null && InventoryGui.instance.m_playerGrid != null)
            {
                try
                {
                    var fi_elements = AccessTools.Field(typeof(InventoryGrid), "m_elements");
                    if (fi_elements != null)
                    {
                        var elements = fi_elements.GetValue(InventoryGui.instance.m_playerGrid)
                            as System.Collections.IList;
                        if (elements != null)
                        {
                            int visibleCount = width * visibleHeight;
                            // Valheim 1.0 promoted the private nested InventoryGrid.Element to the
                            // public top-level InventoryElement, and its m_pos field to a public
                            // Position property. GetNestedType("Element") now returns null, which
                            // silently skipped this whole discovery pass.
                            for (int i = visibleCount; i < elements.Count; i++)
                            {
                                var elem = elements[i] as InventoryElement;
                                if (elem == null) continue;
                                var pos = elem.Position;
                                if (GetSlotInGrid(pos) == null)
                                {
                                    string id = $"Slot_{pos.x}_{pos.y}";
                                    string key = $"{id}_{pos.x}_{pos.y}";
                                    if (!slots.ContainsKey(key))
                                    {
                                        slots[key] = new Slot
                                        {
                                            ID = id,
                                            GridPosition = pos,
                                            Name = id,
                                            IsActive = true,
                                            IsEquipmentSlot = true,
                                            IsHotbarSlot = false,
                                            ItemFits = item => true
                                        };
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UIOverrideSlotSystem] Failed to discover slots from grid elements: {ex.Message}");
                }
            }
        }

        private static string GuessSlotID(ItemDrop.ItemData item)
        {
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Helmet: return HelmetSlotID;
                case ItemDrop.ItemData.ItemType.Chest: return ChestSlotID;
                case ItemDrop.ItemData.ItemType.Legs: return LegsSlotID;
                case ItemDrop.ItemData.ItemType.Shoulder: return ShoulderSlotID;
                case ItemDrop.ItemData.ItemType.Utility: return UtilitySlotID;
                case ItemDrop.ItemData.ItemType.Consumable:
                    return item.m_shared.m_food > 0 ? FoodSlotID : $"Slot_{item.m_gridPos.x}_{item.m_gridPos.y}";
                case ItemDrop.ItemData.ItemType.Ammo: return AmmoSlotID;
                default: return $"Slot_{item.m_gridPos.x}_{item.m_gridPos.y}";
            }
        }

        private static Func<ItemDrop.ItemData, bool> MakeValidatorForType(ItemDrop.ItemData.ItemType type)
        {
            return item => item != null && item.m_shared.m_itemType == type;
        }
    }
}
