using System.Collections.Generic;
using FiresCore.Logging;
using FiresCore.Npc.AI;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Companions spend real ammo. BowBehavior/CrossbowBehavior spawn their own projectile (they never run vanilla
    /// Attack.UseAmmo), so each shot takes 1 matching ammo here: the AMMO POUCH cells first, then the STORAGE rows, never an
    /// equip cell. Companion-only: every caller is a companion combat behaviour (the FDT player bot does not use these).
    /// </summary>
    public static class CompanionAmmo
    {
        private const float LogGap = 4f;
        private static readonly Dictionary<string, float> s_lastLog = new Dictionary<string, float>();

        /// <summary>Debug line, at most once per <see cref="LogGap"/> s per companion and key.</summary>
        public static void Log(string who, string key, string line)
        {
            string k = (who ?? "?") + "|" + key;
            float now = Time.time;
            if (s_lastLog.TryGetValue(k, out float last) && now - last < LogGap) return;
            s_lastLog[k] = now;
            FiresLogger.LogDebug(line);
        }

        public static bool NeedsAmmo(ItemDrop.ItemData weapon) => !string.IsNullOrEmpty(weapon?.m_shared?.m_ammoType);

        private static bool IsAmmoFor(ItemDrop.ItemData item, string ammoType) =>
            item?.m_shared != null && item.m_stack > 0 && item.m_shared.m_ammoType == ammoType
            && (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.AmmoNonEquipable)
            && item.m_shared.m_attack?.m_attackProjectile != null;

        /// <summary>The stack the next shot uses: first matching Ammo pouch cell, else the first matching storage stack.</summary>
        public static ItemDrop.ItemData FindShotAmmo(CompanionInventory inv, ItemDrop.ItemData weapon, out bool fromPouch)
        {
            fromPouch = false;
            string ammoType = weapon?.m_shared?.m_ammoType;
            if (inv == null || string.IsNullOrEmpty(ammoType)) return null;
            foreach (var it in inv.GetPouchItems(CompanionPouchRules.PouchKind.Ammo))
                if (IsAmmoFor(it, ammoType)) { fromPouch = true; return it; }
            var bag = inv.GetStorageInventory();
            if (bag == null) return null;
            foreach (var it in bag.GetAllItems())
                if (it != null && CompanionGridLayout.IsStorage(it.m_gridPos) && IsAmmoFor(it, ammoType)) return it;
            return null;
        }

        /// <summary>True when the weapon needs no ammo or matching ammo is carried (pouch or storage).</summary>
        public static bool HasAmmoFor(CompanionInventory inv, ItemDrop.ItemData weapon) =>
            !NeedsAmmo(weapon) || FindShotAmmo(inv, weapon, out _) != null;

        /// <summary>Removes 1 of <paramref name="ammo"/> (vanilla Inventory.RemoveItem) right as the projectile spawns.</summary>
        public static void ConsumeShot(CombatContext ctx, ItemDrop.ItemData weapon, ItemDrop.ItemData ammo, bool fromPouch)
        {
            var bag = ctx?.Inventory?.GetStorageInventory();
            if (bag == null || ammo == null || !bag.ContainsItem(ammo)) return;
            string ammoName = ammo.m_shared.m_name;
            bag.RemoveItem(ammo, 1);
            int left = 0;
            foreach (var it in bag.GetAllItems())
                if (it?.m_shared != null && it.m_shared.m_name == ammoName && !CompanionGridLayout.TryGetEquipSlot(it.m_gridPos, out _)) left += it.m_stack;
            string who = ctx.Companion != null ? ctx.Companion.companionName : "?";
            Log(who, "shot", $"[Ammo] {who} shot {weapon?.m_shared?.m_name} used 1 {ammoName} ({(fromPouch ? "pouch" : "storage")}), left {left}");
        }

        /// <summary>No matching ammo: log, and hand the fight to a melee weapon (WeaponSwapManager).</summary>
        public static void OutOfAmmo(CombatContext ctx, ItemDrop.ItemData weapon)
        {
            string who = ctx?.Companion != null ? ctx.Companion.companionName : "?";
            Log(who, "out", $"[Ammo] {who} out of {weapon?.m_shared?.m_ammoType} ammo; switching to melee");
            var swap = ctx?.Companion != null ? ctx.Companion.GetComponent<WeaponSwapManager>() : null;
            if (swap == null) return;
            swap.ClearCommitment();
            swap.ScanAvailableWeapons();
            swap.ForceSwapToMelee();
        }
    }

    /// <summary>
    /// What happens after a companion's consume-item attack (vanilla Attack.Start with m_consumeItem: bombs and the
    /// spear throw). Vanilla already takes the thrown item out of the bag (Attack.ConsumeItem). Here: a throwable's hand cell is
    /// refilled from the pouch/storage with the same item; a thrown spear's ItemDrop is found where it lands, and once no foe
    /// is targeted the companion walks to it (within 20 m), picks it up into storage and puts it back in its hand cell, where
    /// Reconcile equips it. Lost (despawned, or not reached in 20 s): swap to melee.
    /// </summary>
    public class CompanionThrowTracker : MonoBehaviour
    {
        private const string MoveOwner = "SpearRetrieve";
        private const float FindWindow = 8f, RetrieveRange = 20f, LostAfter = 20f, PickRange = 1.6f;

        private CompanionController _companion;
        private CombatContext _ctx;

        // pending throw
        private bool _pending;
        private float _throwTime;
        private string _itemName, _prefabName;
        private Vector2i _handCell;
        private bool _wasSpear;
        private readonly HashSet<int> _dropsBefore = new HashSet<int>();

        // spear out
        private ItemDrop _spear;
        private bool _spearOut;
        private float _retrieveStart = -1f;
        private bool _moving;

        public bool SpearOut => _spearOut;

        public static CompanionThrowTracker For(CombatContext ctx)
        {
            if (ctx?.Companion == null) return null;
            var t = ctx.Companion.GetComponent<CompanionThrowTracker>() ?? ctx.Companion.gameObject.AddComponent<CompanionThrowTracker>();
            t._companion = ctx.Companion;
            t._ctx = ctx;
            return t;
        }

        private string Who => _companion != null ? _companion.companionName : "?";

        private static int ItemMask => LayerMask.GetMask("item");

        /// <summary>Called as vanilla Attack.Start succeeds for an attack that consumes its item.</summary>
        public void OnThrowStarted(ItemDrop.ItemData weapon, Attack attack)
        {
            if (weapon?.m_shared == null || _companion == null) return;
            _pending = true;
            _throwTime = Time.time;
            _itemName = weapon.m_shared.m_name;
            _prefabName = weapon.m_dropPrefab != null ? weapon.m_dropPrefab.name : null;
            _handCell = weapon.m_gridPos;
            _wasSpear = weapon.m_shared.m_skillType == Skills.SkillType.Spears;
            _dropsBefore.Clear();
            foreach (var c in Physics.OverlapSphere(transform.position, 60f, ItemMask))
            {
                var d = c != null ? c.GetComponentInParent<ItemDrop>() : null;
                if (d != null) _dropsBefore.Add(d.GetInstanceID());
            }
        }

        private void Update()
        {
            if (_companion == null) return;
            var inv = _companion.GetInventory();
            var bag = inv?.GetStorageInventory();
            if (bag == null) return;

            if (_pending && Time.time - _throwTime > 1.2f) ResolveThrow(inv, bag);
            if (_spearOut) TickSpear(inv, bag);
        }

        private bool StillCarried(Inventory bag)
        {
            foreach (var it in bag.GetAllItems())
                if (it?.m_shared != null && it.m_shared.m_name == _itemName && it.m_gridPos.x == _handCell.x && it.m_gridPos.y == _handCell.y) return true;
            return false;
        }

        private void ResolveThrow(CompanionInventory inv, Inventory bag)
        {
            // The spear's ItemDrop only exists once the projectile lands: keep looking for a while.
            if (_wasSpear)
            {
                if (StillCarried(bag)) { _pending = false; CompanionAmmo.Log(Who, "throw", $"[Ammo] {Who} threw {_itemName} (consumed)"); return; }
                var found = FindLandedDrop();
                if (found != null)
                {
                    _pending = false;
                    _spear = found;
                    _spearOut = true;
                    _retrieveStart = -1f;
                    CompanionAmmo.Log(Who, "throw", $"[Ammo] {Who} threw {_itemName} (spear out)");
                }
                else if (Time.time - _throwTime > FindWindow)
                {
                    _pending = false;
                    Lost("no drop found where it landed");
                }
                return;
            }

            _pending = false;
            CompanionAmmo.Log(Who, "throw", $"[Ammo] {Who} threw {_itemName} (consumed)");
            if (StillCarried(bag)) return;
            // The hand stack is spent: refill the hand cell with the same item from the pouches, then storage.
            if (bag.GetItemAt(_handCell.x, _handCell.y) != null) return;
            ItemDrop.ItemData refill = null;
            foreach (var it in bag.GetAllItems())
            {
                if (it?.m_shared == null || it.m_shared.m_name != _itemName) continue;
                if (CompanionGridLayout.TryGetEquipSlot(it.m_gridPos, out _)) continue;
                if (refill == null || (CompanionGridLayout.IsPouch(it.m_gridPos, out _, out _) && CompanionGridLayout.IsStorage(refill.m_gridPos))) refill = it;
            }
            if (refill == null) return;
            bag.MoveItemToThis(bag, refill, refill.m_stack, _handCell.x, _handCell.y);
            inv.Reconcile("throwable refill");
            CompanionAmmo.Log(Who, "refill", $"[Ammo] {Who} refilled the hand with {_itemName} x{refill.m_stack}");
        }

        private ItemDrop FindLandedDrop()
        {
            ItemDrop best = null;
            float bestDist = float.MaxValue;
            foreach (var c in Physics.OverlapSphere(transform.position, 60f, ItemMask))
            {
                var d = c != null ? c.GetComponentInParent<ItemDrop>() : null;
                if (d == null || _dropsBefore.Contains(d.GetInstanceID())) continue;
                bool match = d.m_itemData?.m_shared?.m_name == _itemName
                    || (!string.IsNullOrEmpty(_prefabName) && Utils.GetPrefabName(d.gameObject) == _prefabName);
                if (!match) continue;
                float dist = Vector3.Distance(transform.position, d.transform.position);
                if (dist < bestDist) { bestDist = dist; best = d; }
            }
            return best;
        }

        private void TickSpear(CompanionInventory inv, Inventory bag)
        {
            if (_spear == null || !_spear.gameObject) { Lost("despawned"); return; }
            var ai = _companion.GetCompanionAI();
            if (ai != null && ai.GetTargetCreature() != null)
            {
                // Still fighting: wait (the reach timer only runs while retrieving).
                StopMoving(ai);
                return;
            }
            float dist = Vector3.Distance(transform.position, _spear.transform.position);
            if (dist > RetrieveRange && _retrieveStart < 0f) { Lost($"{dist:0} m away"); return; }
            if (_retrieveStart < 0f) _retrieveStart = Time.time;
            if (Time.time - _retrieveStart > LostAfter) { Lost("unreachable for 20 s"); return; }

            if (dist > PickRange)
            {
                _moving = true;
                ai?.RequestPathfindingMovement(_spear.transform.position, true, PickRange * 0.7f, Core.UnifiedMovementAuthority.MovementSource.SubBehavior, MoveOwner);
                return;
            }

            StopMoving(ai);
            if (!_spear.CanPickup()) { _spear.RequestOwn(); return; }
            _spear.Load();
            var data = _spear.m_itemData;
            if (data == null || !bag.CanAddItem(data, data.m_stack)) { Lost("storage full"); return; }
            if (!bag.AddItem(data)) { Lost("could not add to storage"); return; }
            ZNetScene.instance?.Destroy(_spear.gameObject);
            string name = data.m_shared.m_name;
            if (bag.ContainsItem(data) && bag.GetItemAt(_handCell.x, _handCell.y) == null)
                bag.MoveItemToThis(bag, data, data.m_stack, _handCell.x, _handCell.y);
            inv.Reconcile("spear retrieved");
            inv.SaveToZDO();
            _spearOut = false;
            _spear = null;
            CompanionAmmo.Log(Who, "retrieve", $"[Ammo] {Who} retrieved {name}");
        }

        private void StopMoving(CompanionAI ai)
        {
            if (!_moving) return;
            _moving = false;
            ai?.ReleasePathfindingMovement(MoveOwner);
        }

        private void Lost(string reason)
        {
            StopMoving(_companion != null ? _companion.GetCompanionAI() : null);
            _spearOut = false;
            _spear = null;
            CompanionAmmo.Log(Who, "lost", $"[Ammo] {Who} lost {_itemName} ({reason})");
            var swap = _companion != null ? _companion.GetComponent<WeaponSwapManager>() : null;
            if (swap != null) { swap.ClearCommitment(); swap.ScanAvailableWeapons(); swap.ForceSwapToMelee(); }
        }

        private void OnDestroy() => StopMoving(_companion != null ? _companion.GetCompanionAI() : null);
    }
}
