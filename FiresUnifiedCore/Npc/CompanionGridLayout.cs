using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// 0.2.361 (Fire 10-08): the companion bag laid out like VA Inventory's player inventory. ONE vanilla Inventory, 8 x 11:
    /// rows 0-3 STORAGE (32 cells); row 4 TOOL pouch (cols 0-3); row 5 AMMO pouch (cols 0-2); row 6 FOOD pouch (cols 0-2);
    /// rows 7-9 EQUIPMENT (see the cell map below); row 10 spare. A worn item is the item in its equip cell that vanilla
    /// Humanoid.EquipItem has equipped (CompanionInventory.Reconcile keeps the two together). The back cells have no vanilla
    /// field: an item there is "on the back" by sitting in the cell. FiresCompanions' panel binds its cells through this table.
    /// </summary>
    public static class CompanionGridLayout
    {
        public const int Width = 8;
        public const int Height = 11;
        public const int StorageRows = 4;
        public const int StorageCells = Width * StorageRows;

        public const int ToolRow = 4, AmmoRow = 5, FoodRow = 6;
        public const int ToolCount = 4, AmmoCount = 3, FoodCount = 3;

        private static readonly Dictionary<CompanionInventory.EquipmentSlot, Vector2i> s_cells = new Dictionary<CompanionInventory.EquipmentSlot, Vector2i>
        {
            { CompanionInventory.EquipmentSlot.Helmet,    new Vector2i(0, 7) },
            { CompanionInventory.EquipmentSlot.Shoulder,  new Vector2i(1, 7) },
            { CompanionInventory.EquipmentSlot.Chest,     new Vector2i(0, 8) },
            { CompanionInventory.EquipmentSlot.Utility,   new Vector2i(1, 8) },
            { CompanionInventory.EquipmentSlot.Legs,      new Vector2i(0, 9) },
            { CompanionInventory.EquipmentSlot.RightHand, new Vector2i(2, 7) },
            { CompanionInventory.EquipmentSlot.LeftHand,  new Vector2i(3, 7) },
            { CompanionInventory.EquipmentSlot.RightBack, new Vector2i(2, 8) },
            { CompanionInventory.EquipmentSlot.LeftBack,  new Vector2i(3, 8) },
        };

        /// <summary>All nine equipment slots in cell-table order.</summary>
        public static IEnumerable<CompanionInventory.EquipmentSlot> Slots => s_cells.Keys;

        public static bool IsStorage(Vector2i pos) => pos.x >= 0 && pos.x < Width && pos.y >= 0 && pos.y < StorageRows;

        public static int PouchCapacity(CompanionPouchRules.PouchKind kind) =>
            kind == CompanionPouchRules.PouchKind.Tool ? ToolCount : kind == CompanionPouchRules.PouchKind.Ammo ? AmmoCount : FoodCount;

        public static int PouchRow(CompanionPouchRules.PouchKind kind) =>
            kind == CompanionPouchRules.PouchKind.Tool ? ToolRow : kind == CompanionPouchRules.PouchKind.Ammo ? AmmoRow : FoodRow;

        public static Vector2i PouchCell(CompanionPouchRules.PouchKind kind, int index) => new Vector2i(index, PouchRow(kind));

        public static bool IsPouch(Vector2i pos, out CompanionPouchRules.PouchKind kind, out int index)
        {
            kind = CompanionPouchRules.PouchKind.Tool;
            index = pos.x;
            switch (pos.y)
            {
                case ToolRow: kind = CompanionPouchRules.PouchKind.Tool; return pos.x >= 0 && pos.x < ToolCount;
                case AmmoRow: kind = CompanionPouchRules.PouchKind.Ammo; return pos.x >= 0 && pos.x < AmmoCount;
                case FoodRow: kind = CompanionPouchRules.PouchKind.Food; return pos.x >= 0 && pos.x < FoodCount;
            }
            return false;
        }

        public static bool TryGetEquipSlot(Vector2i pos, out CompanionInventory.EquipmentSlot slot)
        {
            foreach (var kv in s_cells)
                if (kv.Value.x == pos.x && kv.Value.y == pos.y) { slot = kv.Key; return true; }
            slot = default;
            return false;
        }

        public static Vector2i CellFor(CompanionInventory.EquipmentSlot slot) => s_cells[slot];

        // 0.2.382: the cosmetic Pet cell. Not an EquipmentSlot (no vanilla field, never EquipItem'd): TryGetEquipSlot and Slots
        // skip it, so equip loops, tool lookup and auto-place never see it. Reconcile drives FiresCore.Bridge.PetBridge from it.
        public static readonly Vector2i PetCell = new Vector2i(1, 9);

        public static bool IsPetCell(Vector2i pos) => pos.x == PetCell.x && pos.y == PetCell.y;

        /// <summary>Only pet items, and only while a pet mod has filled PetBridge.</summary>
        public static bool AcceptsPet(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null || !FiresCore.Bridge.PetBridge.IsAvailable) return false;
            try { return FiresCore.Bridge.PetBridge.IsPetItem(item); }
            catch { return false; }
        }

        public static bool IsBackSlot(CompanionInventory.EquipmentSlot slot) =>
            slot == CompanionInventory.EquipmentSlot.RightBack || slot == CompanionInventory.EquipmentSlot.LeftBack;

        /// <summary>Whether the item may sit in the slot's cell (armour by type; hands as CompanionInventory.CanEquipToSlot:
        /// right = one/two-handed weapons, tools, torches; left = shields, bows, left two-handers, torches, one-handers, which
        /// vanilla may move to the right hand, and Reconcile follows; back = any weapon or shield).</summary>
        public static bool AcceptsSlot(CompanionInventory.EquipmentSlot slot, ItemDrop.ItemData item)
        {
            var shared = item?.m_shared;
            if (shared == null) return false;
            var t = shared.m_itemType;
            switch (slot)
            {
                case CompanionInventory.EquipmentSlot.RightBack:
                case CompanionInventory.EquipmentSlot.LeftBack:
                    return item.IsWeapon() || t == ItemDrop.ItemData.ItemType.Shield;
                default:
                    return CompanionInventory.CanEquipToSlot(item, slot);
            }
        }

        /// <summary>Whether the item may sit at <paramref name="pos"/>: storage takes anything, a pouch cell its pouch's filter,
        /// an equip cell <see cref="AcceptsSlot"/>, the spare row and anything outside nothing.</summary>
        public static bool Accepts(Vector2i pos, ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            if (IsStorage(pos)) return true;
            if (IsPouch(pos, out var kind, out _)) return CompanionPouchRules.Matches(kind, item);
            if (TryGetEquipSlot(pos, out var slot)) return AcceptsSlot(slot, item);
            // 0.2.382: Pet cell
            if (IsPetCell(pos)) return AcceptsPet(item);
            return false;
        }

        // ---- Which Inventory objects are companion bags (auto-place and free-space patches act on these only) ----

        private static readonly ConditionalWeakTable<Inventory, CompanionInventory> s_bags = new ConditionalWeakTable<Inventory, CompanionInventory>();

        internal static void Register(Inventory bag, CompanionInventory owner)
        {
            if (bag == null || owner == null) return;
            s_bags.Remove(bag);
            s_bags.Add(bag, owner);
        }

        /// <summary>The CompanionInventory that owns the bag, or null for every other inventory (players, chests).</summary>
        public static CompanionInventory OwnerOf(Inventory bag)
        {
            if (bag == null) return null;
            return s_bags.TryGetValue(bag, out var owner) && owner != null ? owner : null;
        }

        public static bool IsCompanionBag(Inventory bag) => OwnerOf(bag) != null;

        /// <summary>First empty storage cell (rows 0-3, top first), or (-1, -1).</summary>
        public static Vector2i FirstFreeStorageCell(Inventory bag)
        {
            if (bag == null) return new Vector2i(-1, -1);
            for (int y = 0; y < StorageRows && y < bag.GetHeight(); y++)
                for (int x = 0; x < Width && x < bag.GetWidth(); x++)
                    if (bag.GetItemAt(x, y) == null) return new Vector2i(x, y);
            return new Vector2i(-1, -1);
        }

        public static int FreeStorageCells(Inventory bag)
        {
            if (bag == null) return 0;
            int used = 0;
            foreach (var it in bag.GetAllItems())
                if (it != null && IsStorage(it.m_gridPos)) used++;
            return Mathf.Max(0, StorageCells - used);
        }
    }

    // ---- Auto-place: a position-less add into a companion bag (pickup, haul, loot, AI, vanilla AddItem) fills STORAGE only ----

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.FindEmptySlot))]
    internal static class CompanionBagFindEmptySlot
    {
        private static void Postfix(Inventory __instance, ref Vector2i __result)
        {
            if (!CompanionGridLayout.IsCompanionBag(__instance)) return;
            if (__result.x >= 0 && CompanionGridLayout.IsStorage(__result)) return;
            __result = CompanionGridLayout.FirstFreeStorageCell(__instance);
        }
    }

    /// <summary>Stacking onto an existing stack only merges into STORAGE stacks of a companion bag (pouch and equip cells are
    /// filled on purpose only, through the panel or CompanionInventory).</summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.FindFreeStackItem))]
    internal static class CompanionBagFindFreeStackItem
    {
        private static void Postfix(Inventory __instance, string name, int quality, float worldLevel, ref ItemDrop.ItemData __result)
        {
            if (__result == null || CompanionGridLayout.IsStorage(__result.m_gridPos) || !CompanionGridLayout.IsCompanionBag(__instance)) return;
            __result = null;
            foreach (var it in __instance.GetAllItems())
            {
                if (it == null || !CompanionGridLayout.IsStorage(it.m_gridPos)) continue;
                if (it.m_shared.m_name == name && it.m_quality == quality && it.m_stack < it.m_shared.m_maxStackSize
                    && Mathf.Approximately(it.m_worldLevel, worldLevel))
                { __result = it; return; }
            }
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetEmptySlots))]
    internal static class CompanionBagGetEmptySlots
    {
        private static void Postfix(Inventory __instance, ref int __result)
        {
            if (CompanionGridLayout.IsCompanionBag(__instance)) __result = CompanionGridLayout.FreeStorageCells(__instance);
        }
    }

    /// <summary>Vanilla CanAddItem counts every empty cell of the bag; a companion bag counts only free STORAGE cells (and
    /// free space on STORAGE stacks).</summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.CanAddItem), new[] { typeof(ItemDrop.ItemData), typeof(int) })]
    internal static class CompanionBagCanAddItem
    {
        private static void Postfix(Inventory __instance, ItemDrop.ItemData item, int stack, ref bool __result)
        {
            if (item?.m_shared == null || !CompanionGridLayout.IsCompanionBag(__instance)) return;
            if (stack <= 0) stack = item.m_stack;
            int space = CompanionGridLayout.FreeStorageCells(__instance) * item.m_shared.m_maxStackSize;
            foreach (var it in __instance.GetAllItems())
            {
                if (it == null || !CompanionGridLayout.IsStorage(it.m_gridPos)) continue;
                if (it.m_shared.m_name == item.m_shared.m_name && it.m_worldLevel == item.m_worldLevel)
                    space += Mathf.Max(0, it.m_shared.m_maxStackSize - it.m_stack);
            }
            __result = space >= stack;
        }
    }

    // ---- 0.2.362 safety net: log (never block) a worn item leaving a companion bag through a non-equip remove path ----

    internal static class CompanionBagWornRemoveGuard
    {
        internal static void Check(Inventory bag, ItemDrop.ItemData item, string path)
        {
            if (item == null) return;
            var owner = CompanionGridLayout.OwnerOf(bag);
            if (owner == null || owner.InReconcile) return;
            if (!CompanionGridLayout.TryGetEquipSlot(item.m_gridPos, out var slot) || !bag.ContainsItem(item)) return;
            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionInventory] worn-item remove: {item.m_shared?.m_name} from {slot} cell ({item.m_gridPos.x},{item.m_gridPos.y}) via {path} (not blocked)");
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData) })]
    internal static class CompanionBagRemoveItemGuard
    {
        private static void Prefix(Inventory __instance, ItemDrop.ItemData item) => CompanionBagWornRemoveGuard.Check(__instance, item, "Inventory.RemoveItem(item)");
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData), typeof(int) })]
    internal static class CompanionBagRemoveItemAmountGuard
    {
        private static void Prefix(Inventory __instance, ItemDrop.ItemData item) => CompanionBagWornRemoveGuard.Check(__instance, item, "Inventory.RemoveItem(item, amount)");
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveOneItem), new[] { typeof(ItemDrop.ItemData) })]
    internal static class CompanionBagRemoveOneItemGuard
    {
        private static void Prefix(Inventory __instance, ItemDrop.ItemData item) => CompanionBagWornRemoveGuard.Check(__instance, item, "Inventory.RemoveOneItem");
    }

    /// <summary>Name-based remove: vanilla walks every cell, so a worn piece of that name could be spent.</summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(string), typeof(int), typeof(int), typeof(bool) })]
    internal static class CompanionBagRemoveByNameGuard
    {
        private static void Prefix(Inventory __instance, string name)
        {
            var owner = CompanionGridLayout.OwnerOf(__instance);
            if (owner == null || owner.InReconcile || string.IsNullOrEmpty(name)) return;
            foreach (var it in __instance.GetAllItems())
                if (it?.m_shared != null && it.m_shared.m_name == name && !CompanionGridLayout.IsStorage(it.m_gridPos))
                {
                    CompanionBagWornRemoveGuard.Check(__instance, it, $"Inventory.RemoveItem(\"{name}\", amount)");
                    if (CompanionGridLayout.IsPouch(it.m_gridPos, out var kind, out _))
                        FiresCore.Logging.FiresLogger.LogDebug($"[CompanionInventory] name-based remove of {name} can reach the {kind} pouch stack (not blocked)");
                }
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveEmptySlot))]
    internal static class CompanionBagHaveEmptySlot
    {
        private static void Postfix(Inventory __instance, ref bool __result)
        {
            if (CompanionGridLayout.IsCompanionBag(__instance)) __result = CompanionGridLayout.FreeStorageCells(__instance) > 0;
        }
    }
}
