using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// The companion bag laid out like VA Inventory's player inventory. ONE vanilla Inventory, 8 x 11:
    /// rows 0-3 STORAGE (32 cells); row 4 TOOL pouch (cols 0-3); row 5 AMMO pouch (cols 0-2); row 6 FOOD pouch (cols 0-2);
    /// rows 7-9 EQUIPMENT (see the cell map below); row 10 spare. A worn item is the item in its equip cell that vanilla
    /// Humanoid.EquipItem has equipped (CompanionInventory.Reconcile keeps the two together). The back cells have no vanilla
    /// field: an item there is "on the back" by sitting in the cell. FiresCompanions' panel binds its cells through this table.
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    public static class CompanionGridLayout
    {
        public const int Width = 8;
        public const int Height = 18;   // companions get every player slot but the trash can: rows 11-13 Misc, 14-17 BackpackStorage
        /// <summary>The overflow/parking row (was Height - 1 while the grid had 11 rows); kept at row 10 so old saves load in place.</summary>
        public const int SpareRow = 10;
        /// <summary>Misc rows (the player's max: VAInventory's 0-3 config rows; the active count comes from FiresSlotCatalog).</summary>
        public const int MiscRow = 11, MiscMaxRows = 3;
        /// <summary>The bag grid rows (the active size is the equipped backpack's capacity, as for the player).</summary>
        public const int BagRow = 14, BagMaxRows = 4;
        public const int StorageRows = 4;
        public const int StorageCells = Width * StorageRows;

        public const int ToolRow = 4, AmmoRow = 5, FoodRow = 6;
        /// <summary>The pouch rows' MAX cells (the player's ranges: ToolHotbar 0-8, Food/Ammo one per equipment row, ~3-4); the
        /// active count is <see cref="PouchCapacity"/> (the player's own, registered by VAInventory; 4 / 3 / 3 without it).</summary>
        public const int ToolCount = 8, AmmoCount = 4, FoodCount = 4;
        private const int ToolDefault = 4, AmmoDefault = 3, FoodDefault = 3;

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
            { CompanionInventory.EquipmentSlot.Trinket,   new Vector2i(4, 7) },
        };

        /// <summary>All equipment slots (nine + Trinket) in cell-table order.</summary>
        public static IEnumerable<CompanionInventory.EquipmentSlot> Slots => s_cells.Keys;

        public static bool IsStorage(Vector2i pos) => pos.x >= 0 && pos.x < Width && pos.y >= 0 && pos.y < StorageRows;

        public static int PouchCapacity(CompanionPouchRules.PouchKind kind) =>
            kind == CompanionPouchRules.PouchKind.Tool ? Mathf.Clamp(FiresSlotCatalog.CountOf("Tools", ToolDefault), 0, ToolCount)
            : kind == CompanionPouchRules.PouchKind.Ammo ? Mathf.Clamp(FiresSlotCatalog.CountOf("Ammo", AmmoDefault), 0, AmmoCount)
            : Mathf.Clamp(FiresSlotCatalog.CountOf("Food", FoodDefault), 0, FoodCount);

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

        // ---- Sections, keyed by VAInventory's Slots ids (Managers\Slots.cs), so a companion slot is the player slot type ----
        // Old cells keep their place (v1 saves load as they are); the new ones fill the free cells of rows 7-9 and rows 11-17.
        // Rules: a validator VAInventory registered in FiresSlotCatalog wins; without one, Core's fallback (by vanilla type).
        public sealed class Section
        {
            public readonly string Name; public readonly Vector2i[] Cells;
            internal Section(string name, params Vector2i[] cells) { Name = name; Cells = cells; }
        }

        private static Vector2i[] Rows(int y, int rows, int width)
        {
            var a = new Vector2i[rows * width];
            for (int r = 0; r < rows; r++) for (int x = 0; x < width; x++) a[r * width + x] = new Vector2i(x, y + r);
            return a;
        }

        private static readonly List<Section> s_sections = new List<Section>
        {
            new Section("Helmet", new Vector2i(0, 7)), new Section("Shoulder", new Vector2i(1, 7)),
            new Section("Chest", new Vector2i(0, 8)), new Section("Utility", new Vector2i(1, 8)),
            new Section("Legs", new Vector2i(0, 9)),
            new Section("FrontWeapon", new Vector2i(2, 7)), new Section("FrontOffhand", new Vector2i(3, 7)),
            new Section("BackWeapon", new Vector2i(2, 8)), new Section("BackOffhand", new Vector2i(3, 8)),
            new Section("Trinket", new Vector2i(4, 7)), new Section("Torch", new Vector2i(5, 7)),
            new Section("Necklace", new Vector2i(6, 7)), new Section("ExtraUtility", new Vector2i(7, 7)),
            new Section("Ring", new Vector2i(4, 8)), new Section("RingRight", new Vector2i(5, 8)),
            new Section("CoinPurse", new Vector2i(6, 8)), new Section("TeleportBypass", new Vector2i(7, 8)),
            new Section("Pet", new Vector2i(1, 9)), new Section("Backpack", new Vector2i(2, 9)),
            new Section("Tools", Rows(ToolRow, 1, ToolCount)), new Section("Ammo", Rows(AmmoRow, 1, AmmoCount)), new Section("Food", Rows(FoodRow, 1, FoodCount)),
            new Section("Misc", Rows(MiscRow, MiscMaxRows, Width)),
            new Section("BackpackStorage", Rows(BagRow, BagMaxRows, Width)),
        };

        /// <summary>Every section in panel order (VAInventory Slots ids), each with its MAX cells. Add-only.</summary>
        public static IReadOnlyList<Section> Sections => s_sections;

        public static Section FindSection(string name)
        {
            foreach (var s in s_sections) if (s.Name == name) return s;
            return null;
        }

        public static bool TryGetSection(Vector2i pos, out Section section, out int index)
        {
            foreach (var s in s_sections)
                for (int i = 0; i < s.Cells.Length; i++)
                    if (s.Cells[i].x == pos.x && s.Cells[i].y == pos.y) { section = s; index = i; return true; }
            section = null; index = -1; return false;
        }

        internal static bool IsNewSection(string name) =>
            name == "Torch" || name == "Necklace" || name == "ExtraUtility" || name == "Ring" || name == "RingRight"
            || name == "CoinPurse" || name == "TeleportBypass" || name == "Backpack" || name == "Misc" || name == "BackpackStorage";

        /// <summary>A cell whose rule only VAInventory knows (Ring, Necklace, Backpack, TeleportBypass...) while it has not
        /// registered yet (it registers on its own schedule): Reconcile leaves its item where it is instead of calling it invalid.</summary>
        public static bool IsUndecided(Vector2i pos) =>
            TryGetSection(pos, out var s, out _) && IsNewSection(s.Name) && !FiresSlotCatalog.IsRegistered(s.Name)
            && s.Name != "Torch" && s.Name != "ExtraUtility" && s.Name != "CoinPurse" && s.Name != "Misc" && s.Name != "BackpackStorage";

        /// <summary>Whether the item fits a section: the registered VAInventory validator, else Core's type fallback. Jewelry,
        /// Backpack and TeleportBypass need VAInventory's rule (nothing fits them without it, never a guess).</summary>
        public static bool AcceptsNewSection(string name, ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            if (FiresSlotCatalog.TryGet(name, out var rule)) { try { return rule(item); } catch { return false; } }
            var t = item.m_shared.m_itemType;
            switch (name)
            {
                case "Trinket": return t == ItemDrop.ItemData.ItemType.Trinket;
                case "Torch": return t == ItemDrop.ItemData.ItemType.Torch;
                case "ExtraUtility": return t == ItemDrop.ItemData.ItemType.Utility;
                case "CoinPurse": return item.m_shared.m_name == "$item_coins";
                case "Misc": case "BackpackStorage": return true;
                default: return false;
            }
        }

        // The cosmetic Pet cell. Not an EquipmentSlot (no vanilla field, never EquipItem'd): TryGetEquipSlot and Slots
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
            // Pet cell
            if (IsPetCell(pos)) return AcceptsPet(item);
            // The new sections (Misc / BackpackStorage only within their active size)
            if (TryGetSection(pos, out var sec, out _) && IsNewSection(sec.Name)) return AcceptsNewSection(sec.Name, item);
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

    /// <summary>
    /// The shared slot rules: companions get every player slot, identical to the player. VAInventory registers its
    /// player-free Slots validators (IsXSlotItem), the player's Misc cell count, and its backpack capacity rule here at startup, so a
    /// companion's cells take exactly what the player's do; Core keeps no parallel list. Add-only.
    /// </summary>
    public static class FiresSlotCatalog
    {
        private sealed class Entry { public string Group; public System.Func<ItemDrop.ItemData, bool> Validator; public int Count; }
        private static readonly Dictionary<string, Entry> s_entries = new Dictionary<string, Entry>();
        private static System.Func<ItemDrop.ItemData, int> s_bagCapacity;

        /// <summary>Register (or replace) the rule for a VAInventory Slots id; <paramref name="count"/> = the player's cell count (Misc: rows x 8).</summary>
        public static void Register(string id, string group, System.Func<ItemDrop.ItemData, bool> validator, int count = 1)
        {
            if (string.IsNullOrEmpty(id) || validator == null) return;
            s_entries[id] = new Entry { Group = group, Validator = validator, Count = count };
            FiresCore.Logging.FiresLogger.LogDebug($"[SlotCatalog] '{id}' registered (group {group}, {count} cell(s))");
        }

        /// <summary>The bag size (cells) a backpack item gives, as VA_Backpacks gives the player; 0 = not a backpack.</summary>
        public static void RegisterBackpackCapacity(System.Func<ItemDrop.ItemData, int> cellsForBackpack)
        {
            s_bagCapacity = cellsForBackpack;
            FiresCore.Logging.FiresLogger.LogDebug("[SlotCatalog] backpack capacity rule registered");
        }

        public static bool TryGet(string id, out System.Func<ItemDrop.ItemData, bool> validator)
        {
            validator = null;
            if (id == null || !s_entries.TryGetValue(id, out var e)) return false;
            validator = e.Validator;
            return true;
        }

        public static bool IsRegistered(string id) => id != null && s_entries.ContainsKey(id);

        /// <summary>The player's cell count for <paramref name="id"/>, or <paramref name="fallback"/> when VAInventory did not say.</summary>
        public static int CountOf(string id, int fallback) => id != null && s_entries.TryGetValue(id, out var e) ? e.Count : fallback;

        public static int BagCells(ItemDrop.ItemData backpack)
        {
            var size = BagSize(backpack);
            return size.x * size.y;
        }

        private static System.Func<ItemDrop.ItemData, Vector2i> s_bagSize;

        /// <summary>The bag's (columns, rows) as VA_Backpacks gives the player (BackpackBridge.GetConfigDimensions); preferred over
        /// <see cref="RegisterBackpackCapacity"/> because the bag's stored items keep their (x, y) inside it.</summary>
        public static void RegisterBackpackSize(System.Func<ItemDrop.ItemData, Vector2i> columnsRows)
        {
            s_bagSize = columnsRows;
            FiresCore.Logging.FiresLogger.LogDebug("[SlotCatalog] backpack size rule registered");
        }

        /// <summary>(columns, rows) of the bag a backpack gives, (0, 0) for none; from the size rule, else the capacity rule as
        /// full 8-wide rows.</summary>
        public static Vector2i BagSize(ItemDrop.ItemData backpack)
        {
            if (backpack == null) return new Vector2i(0, 0);
            try
            {
                if (s_bagSize != null) { var v = s_bagSize(backpack); return new Vector2i(Mathf.Max(0, v.x), Mathf.Max(0, v.y)); }
                if (s_bagCapacity != null)
                {
                    int cells = Mathf.Max(0, s_bagCapacity(backpack));
                    return new Vector2i(Mathf.Min(cells, CompanionGridLayout.Width), (cells + CompanionGridLayout.Width - 1) / CompanionGridLayout.Width);
                }
            }
            catch { }
            return new Vector2i(0, 0);
        }
    }

    // ---- Auto-place: a position-less add into a companion bag (pickup, haul, loot, AI, vanilla AddItem) fills STORAGE only ----

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.FindEmptySlot))]
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
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
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
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
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
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
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
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

    // ---- Safety net: log (never block) a worn item leaving a companion bag through a non-equip remove path ----

    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
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
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class CompanionBagRemoveItemGuard
    {
        private static void Prefix(Inventory __instance, ItemDrop.ItemData item) => CompanionBagWornRemoveGuard.Check(__instance, item, "Inventory.RemoveItem(item)");
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData), typeof(int) })]
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class CompanionBagRemoveItemAmountGuard
    {
        private static void Prefix(Inventory __instance, ItemDrop.ItemData item) => CompanionBagWornRemoveGuard.Check(__instance, item, "Inventory.RemoveItem(item, amount)");
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveOneItem), new[] { typeof(ItemDrop.ItemData) })]
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class CompanionBagRemoveOneItemGuard
    {
        private static void Prefix(Inventory __instance, ItemDrop.ItemData item) => CompanionBagWornRemoveGuard.Check(__instance, item, "Inventory.RemoveOneItem");
    }

    /// <summary>Name-based remove: vanilla walks every cell, so a worn piece of that name could be spent.</summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(string), typeof(int), typeof(int), typeof(bool) })]
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
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
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class CompanionBagHaveEmptySlot
    {
        private static void Postfix(Inventory __instance, ref bool __result)
        {
            if (CompanionGridLayout.IsCompanionBag(__instance)) __result = CompanionGridLayout.FreeStorageCells(__instance) > 0;
        }
    }
}
