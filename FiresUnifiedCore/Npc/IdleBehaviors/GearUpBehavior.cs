using System.Collections.Generic;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Companions at base craft themselves clothing/tools or put on extra clothing that is
    /// in the base chests (never touching private chests or chests behind warded doors). A staying (at base) companion
    /// with nothing else to do:
    ///   take    for each armour slot (helmet, chest, legs, shoulder/cape, utility) a strictly better spare (more armour, then more durability)
    ///           from a base chest its owner may write (ChestHelper.OwnerMayWrite: privacy + ward) and that it can reach (IsReachable, which
    ///           also covers a door it may not open); walks there, takes it, equips it, puts its old piece back into that chest when it fits
    ///   craft   when no spare fits: a vanilla recipe the OWNER knows (owner online), for an empty or weaker armour slot, at a usable station
    ///           in the base (ChoreBrain.FindWorkstation, level checked by ResourceDataHelper.CraftTool), materials pulled from base chests
    ///           (never the last "Self-craft keeps materials" of any), at most "Companion self-craft per day" crafts per in-game day
    /// Never hoppers, carts, ships, companion bags, the player's inventory or another companion; never weapons. Runs on the companion's
    /// owner peer (where its AI runs); chest writes go through ChestHelper.TryClaimForWrite.
    /// </summary>
    public class GearUpBehavior : WorkBehaviorBase<GearUpBehavior.GearPhase>
    {
        public override string BehaviorName => "GearUp";
        public override bool AvailableForIdleRotation => false;   // staying (at base) companions only

        public enum GearPhase { Scanning, MovingToChest, Taking, Gathering, MovingToStation, Crafting, Complete }

        private static readonly CompanionInventory.EquipmentSlot[] ArmourSlots =
        {
            CompanionInventory.EquipmentSlot.Helmet, CompanionInventory.EquipmentSlot.Chest, CompanionInventory.EquipmentSlot.Legs,
            CompanionInventory.EquipmentSlot.Shoulder, CompanionInventory.EquipmentSlot.Utility,
        };
        private const float ChestSearchRadius = 30f, StationSearchRadius = 30f, ScanCooldown = 60f;
        private const float WalkPhaseTimeout = 25f, CraftPhaseTimeout = 8f, DefaultPhaseTimeout = 5f, MaxBehaviorSeconds = 80f;
        private const float ChestReachDistance = 2.2f, StationReachDistance = 2.5f, CraftAnimationSeconds = 2f;
        private const float ArmourTieEpsilon = 0.01f;          // armour values this close count as equal
        private const float DurabilityUpgradeShare = 0.25f;    // same item, same armour: swap only when this much more durability is left
        private const float EmptySlotBonus = 1f;               // an empty slot outranks an equal armour gain on a filled one
        private const int DefaultSelfCraftKeep = 5;
        private const string DayKey = "core_gearup_day", CountKey = "core_gearup_crafts";

        private Container _chest;
        private ItemDrop.ItemData _item;
        private CompanionInventory.EquipmentSlot _slot;
        private Recipe _recipe;
        private CraftingStation _station;
        private float _nextScanAt;
        private static readonly HashSet<string> s_skipSaid = new HashSet<string>();

        protected override GearPhase InitialPhase => GearPhase.Scanning;
        protected override float GetPhaseTimeout(GearPhase p) => p switch { GearPhase.MovingToChest => WalkPhaseTimeout, GearPhase.MovingToStation => WalkPhaseTimeout, GearPhase.Crafting => CraftPhaseTimeout, _ => DefaultPhaseTimeout };
        protected override GearPhase OnPhaseTimeout(GearPhase t) => GearPhase.Complete;
        protected override string GetPhaseDescription(GearPhase p) => p switch
        {
            GearPhase.MovingToChest => $"Fetching {_item?.m_shared?.m_name ?? "gear"}",
            GearPhase.MovingToStation or GearPhase.Crafting => $"Crafting {_recipe?.m_item?.m_itemData?.m_shared?.m_name ?? "gear"}",
            _ => "Checking gear",
        };
        public override string GetStatusDescription() => GetPhaseDescription(CurrentPhase);

        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior) { base.Initialize(companion, idleBehavior); MaxDuration = MaxBehaviorSeconds; }

        public override bool CanStart()
        {
            var cfg = Config.ConfigManager.Instance;
            if (cfg?.configGearUp == null || !cfg.configGearUp.Value || Companion == null || Inventory == null) return false;
            if (Time.time < _nextScanAt) return false;
            _nextScanAt = Time.time + ScanCooldown;
            _chest = null; _item = null; _recipe = null; _station = null;
            return FindSpare() || FindCraft();
        }

        protected override bool UpdatePhase(GearPhase phase)
        {
            switch (phase)
            {
                case GearPhase.Scanning:
                    if (_item != null && _chest != null) { MoveToPosition(_chest.transform.position); SetPhase(GearPhase.MovingToChest); }
                    else if (_recipe != null && _station != null) SetPhase(GearPhase.Gathering);
                    else SetPhase(GearPhase.Complete);
                    return false;
                case GearPhase.MovingToChest:
                    if (_chest == null) { SetPhase(GearPhase.Complete); return false; }
                    if ((ContinueMovement() || Vector3.Distance(Transform.position, _chest.transform.position) < ChestReachDistance) && Core.CompanionReach.CanTouch(Transform, _chest, out _)) { StopMovement(); SetPhase(GearPhase.Taking); }   // reach = no wall in between
                    return false;
                case GearPhase.Taking: TakeAndEquip(); SetPhase(GearPhase.Complete); return false;
                case GearPhase.Gathering:
                    if (!GatherMaterials()) { SetPhase(GearPhase.Complete); return false; }
                    MoveToPosition(_station.transform.position); SetPhase(GearPhase.MovingToStation); return false;
                case GearPhase.MovingToStation:
                    if (_station == null) { SetPhase(GearPhase.Complete); return false; }
                    if ((ContinueMovement() || Vector3.Distance(Transform.position, _station.transform.position) < StationReachDistance) && Core.CompanionReach.CanTouch(Transform, _station, out _)) { StopMovement(); FaceTarget(_station.transform.position); PlayInteractAnimation(); SetPhase(GearPhase.Crafting); }
                    return false;
                case GearPhase.Crafting:
                    if (TimeInCurrentPhase < CraftAnimationSeconds) return false;
                    CraftAndEquip(); SetPhase(GearPhase.Complete); return false;
                default: Complete(); return true;
            }
        }

        // ---- choosing ----

        private static float ArmourOf(ItemDrop.ItemData it) => it == null ? 0f : it.m_shared.m_armor + it.m_shared.m_armorPerLevel * Mathf.Max(0, it.m_quality - 1);
        private static float DurabilityShare(ItemDrop.ItemData it) => it == null || it.GetMaxDurability() <= 0f ? 1f : it.m_durability / it.GetMaxDurability();

        private bool IsUpgrade(ItemDrop.ItemData candidate, ItemDrop.ItemData worn)
        {
            if (candidate == null) return false;
            if (worn == null) return true;
            float candidateArmour = ArmourOf(candidate), wornArmour = ArmourOf(worn);
            if (candidateArmour > wornArmour + ArmourTieEpsilon) return true;
            return Mathf.Abs(candidateArmour - wornArmour) <= ArmourTieEpsilon && candidate.m_shared.m_name == worn.m_shared.m_name && DurabilityShare(candidate) > DurabilityShare(worn) + DurabilityUpgradeShare;
        }

        private List<Container> FindUsableBaseChests()
        {
            long owner = ChestHelper.ChestOwnerIdFor(Companion);
            var list = new List<Container>();
            foreach (Container c in ChestHelper.FindNearbyChests(SearchCenter, ChestSearchRadius))
            {
                if (c == null || !FiresCore.Storage.StorageNetwork.IsMemberCandidate(c)) continue;   // no hoppers, carts, ships, companion bags
                string why = !ChestHelper.OwnerMayWrite(c, owner) ? ChestHelper.WhyNotWritable(c, owner) : (!IsReachable(c.transform.position) ? "out of reach (or behind a door it may not open)" : null);
                if (why != null) { if (s_skipSaid.Add(c.GetInstanceID() + why)) FiresLogger.LogDebug($"[Chores] {Companion.companionName} skipped '{c.m_name}': {why}"); continue; }
                list.Add(c);
            }
            return list;
        }

        private bool FindSpare()
        {
            float bestGain = 0f;
            foreach (Container c in FindUsableBaseChests())
            {
                Inventory inv = c.GetInventory();
                if (inv == null) continue;
                foreach (ItemDrop.ItemData it in inv.GetAllItems())
                {
                    var slot = CompanionInventory.GetSlotForItem(it);
                    if (slot == null || System.Array.IndexOf(ArmourSlots, slot.Value) < 0) continue;
                    var worn = Inventory.GetEquippedItem(slot.Value);
                    if (!IsUpgrade(it, worn)) continue;
                    float gain = ArmourOf(it) - ArmourOf(worn) + (worn == null ? EmptySlotBonus : 0f);
                    if (gain > bestGain) { bestGain = gain; _chest = c; _item = it; _slot = slot.Value; }
                }
            }
            return _item != null;
        }

        private bool FindCraft()
        {
            var cfg = Config.ConfigManager.Instance;
            int perDay = cfg?.configSelfCraftPerDay != null ? cfg.configSelfCraftPerDay.Value : 0;
            if (perDay <= 0 || ObjectDB.instance == null || EnvMan.instance == null) return false;
            ZDO zdo = Companion.GetComponent<ZNetView>()?.GetZDO();
            if (zdo == null) return false;
            int day = EnvMan.instance.GetDay();
            if (zdo.GetInt(DayKey, -1) == day && zdo.GetInt(CountKey, 0) >= perDay) return false;
            Player owner = Companion.GetOwner();
            if (owner == null) return false;   // known recipes are the owner's: only while the owner is online
            CraftingStation station = AI.ChoreBrain.FindWorkstation(Transform.position, StationSearchRadius);
            if (station == null) return false;
            List<Container> chests = FindUsableBaseChests();
            int keep = cfg.configSelfCraftKeep != null ? cfg.configSelfCraftKeep.Value : DefaultSelfCraftKeep;
            float bestGain = 0f;
            foreach (Recipe r in ObjectDB.instance.m_recipes)
            {
                if (r == null || r.m_item == null || !r.m_enabled) continue;
                var data = r.m_item.m_itemData;
                var slot = CompanionInventory.GetSlotForItem(data);
                if (slot == null || System.Array.IndexOf(ArmourSlots, slot.Value) < 0) continue;
                if (!owner.IsRecipeKnown(data.m_shared.m_name)) continue;
                if (r.m_craftingStation == null || r.m_craftingStation.m_name != station.m_name || station.GetLevel() < r.m_minStationLevel) continue;
                var worn = Inventory.GetEquippedItem(slot.Value);
                if (!IsUpgrade(data, worn)) continue;
                if (!CanAffordFromBagAndChests(r, chests, keep)) continue;
                float gain = ArmourOf(data) - ArmourOf(worn) + (worn == null ? EmptySlotBonus : 0f);
                if (gain > bestGain) { bestGain = gain; _recipe = r; _station = station; _slot = slot.Value; }
            }
            return _recipe != null;
        }

        private bool CanAffordFromBagAndChests(Recipe r, List<Container> chests, int keep)
        {
            Inventory bag = GetStorageInventory();
            foreach (Piece.Requirement req in r.m_resources)
            {
                if (req?.m_resItem == null || req.m_amount <= 0) continue;
                string prefab = req.m_resItem.gameObject.name;
                int inBag = bag != null ? bag.CountItems(req.m_resItem.m_itemData.m_shared.m_name) : 0;
                int inChests = ChestHelper.GetAvailableItemCount(chests, prefab);
                if (inBag + Mathf.Max(0, inChests - keep) < req.m_amount) return false;
            }
            return true;
        }

        // ---- doing ----

        private void TakeAndEquip()
        {
            if (_chest == null || _item == null || !ChestHelper.TryClaimForWrite(_chest, Companion)) return;
            Inventory chestInv = _chest.GetInventory(), bag = GetStorageInventory();
            ItemDrop.ItemData item = ChestHelper.FindSameItem(chestInv, _item);   // a claim reloads the chest: find the item again
            if (item == null || bag == null || !IsUpgrade(item, Inventory.GetEquippedItem(_slot))) return;
            if (ChestHelper.MoveItem(chestInv, bag, item, item.m_stack) == 0) return;
            ItemDrop.ItemData mine = ChestHelper.FindSameItem(bag, item) ?? item;
            ItemDrop.ItemData old = Inventory.GetEquippedItem(_slot);
            string oldName = old?.m_shared?.m_name;
            float oldArmour = ArmourOf(old);
            if (!Inventory.EquipItem(_slot, mine)) { ChestHelper.MoveItem(bag, chestInv, mine, mine.m_stack); return; }
            FiresLogger.LogInfo($"[Chores] {Companion.companionName} took {mine.m_shared.m_name} from '{_chest.m_name}' (better: armour {ArmourOf(mine):0} > {oldArmour:0})");
            if (old != null)
            {
                ItemDrop.ItemData oldInBag = ChestHelper.FindSameItem(bag, old) ?? old;
                if (chestInv.CanAddItem(oldInBag) && ChestHelper.MoveItem(bag, chestInv, oldInBag, oldInBag.m_stack) > 0)
                    FiresLogger.LogInfo($"[Chores] {Companion.companionName} put {oldName} into '{_chest.m_name}'");
            }
            Inventory.TriggerSaveToZDO();
            SaveInventory();
        }

        private bool GatherMaterials()
        {
            if (_recipe == null || _station == null) return false;
            Inventory bag = GetStorageInventory();
            if (bag == null) return false;
            List<Container> chests = FindUsableBaseChests();
            int keep = Config.ConfigManager.Instance?.configSelfCraftKeep != null ? Config.ConfigManager.Instance.configSelfCraftKeep.Value : DefaultSelfCraftKeep;
            if (!CanAffordFromBagAndChests(_recipe, chests, keep)) return false;
            foreach (Piece.Requirement req in _recipe.m_resources)
            {
                if (req?.m_resItem == null || req.m_amount <= 0) continue;
                int need = req.m_amount - bag.CountItems(req.m_resItem.m_itemData.m_shared.m_name);
                if (need <= 0) continue;
                int got = ChestHelper.PullItemsByPrefabName(chests, bag, req.m_resItem.gameObject.name, need);
                if (got < need) { FiresLogger.LogDebug($"[Chores] {Companion.companionName} self-craft stopped: got {got}/{need} {req.m_resItem.m_itemData.m_shared.m_name}"); return false; }
            }
            SaveInventory();
            return true;
        }

        private void CraftAndEquip()
        {
            if (_recipe == null || _station == null) return;
            Inventory bag = GetStorageInventory();
            string name = _recipe.m_item.m_itemData.m_shared.m_name;
            if (bag == null || !ResourceDataHelper.CraftTool(_recipe, _station, bag)) { FiresLogger.LogDebug($"[Chores] {Companion.companionName} could not craft {name} at {_station.m_name}"); return; }
            ZDO zdo = Companion.GetComponent<ZNetView>()?.GetZDO();
            if (zdo != null)
            {
                int day = EnvMan.instance != null ? EnvMan.instance.GetDay() : 0;
                int count = zdo.GetInt(DayKey, -1) == day ? zdo.GetInt(CountKey, 0) : 0;
                zdo.Set(DayKey, day); zdo.Set(CountKey, count + 1);
            }
            FiresLogger.LogInfo($"[Chores] {Companion.companionName} crafted {name} at {_station.m_name} from base chests");
            foreach (ItemDrop.ItemData it in bag.GetAllItems())
                if (it.m_shared.m_name == name && !Inventory.IsWorn(it)) { Inventory.EquipItem(_slot, it); break; }
            Inventory.TriggerSaveToZDO();
            SaveInventory();
        }
    }
}
