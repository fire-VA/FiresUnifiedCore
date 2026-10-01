using System;
using System.Collections.Generic;
using FiresCore.Npc.IdleBehaviors;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// A companion as a task body (Core 0.2.220, the haul): the same questions ChoreBrain and the haul ask the FDT bot's
    /// BotTaskBody, answered from CompanionController / CompanionInventory. HaulBehavior carries the haul's orders out itself (the
    /// swing, the pickup, the placement); these members serve ChoreBrain's other chores and any caller that drives a body directly.
    /// </summary>
    public sealed class CompanionTaskBody : ITaskBody, ITaskBodyHands
    {
        private const string Owner = "TaskBody";
        private static readonly CompanionInventory.EquipmentSlot[] s_slots =
            (CompanionInventory.EquipmentSlot[])Enum.GetValues(typeof(CompanionInventory.EquipmentSlot));

        private readonly CompanionController _companion;

        public CompanionTaskBody(CompanionController companion) => _companion = companion;

        public CompanionController Companion => _companion;
        public Humanoid Character => _companion != null ? _companion.GetHumanoid() : null;
        public Vector3 Position => _companion != null ? _companion.transform.position : Vector3.zero;
        public Vector3 Forward => _companion != null ? _companion.transform.forward : Vector3.forward;
        public Inventory Inventory => _companion != null && _companion.GetInventory() != null ? _companion.GetInventory().GetStorageInventory() : null;
        public long OwnerPlayerId => _companion != null ? ChestHelper.ChestOwnerIdFor(_companion) : 0L;
        public bool IsBusy => Character != null && Character.InAttack();

        public ItemDrop.ItemData HeldItem =>
            _companion != null && _companion.GetInventory() != null ? _companion.GetInventory().GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand) : null;

        public IEnumerable<ItemDrop.ItemData> CarriedItems
        {
            get
            {
                Inventory storage = Inventory;
                if (storage != null)
                    foreach (ItemDrop.ItemData item in storage.GetAllItems()) yield return item;
                CompanionInventory inv = _companion != null ? _companion.GetInventory() : null;
                if (inv == null) yield break;
                foreach (CompanionInventory.EquipmentSlot slot in s_slots)
                {
                    ItemDrop.ItemData equipped = inv.GetEquippedItem(slot);
                    if (equipped != null) yield return equipped;
                }
            }
        }

        public void MoveTo(Vector3 point, bool run) =>
            _companion?.GetCompanionAI()?.RequestPathfindingMovement(point, run, 1.5f, Core.UnifiedMovementAuthority.MovementSource.SubBehavior, Owner);

        public void LookAt(Vector3 point)
        {
            if (_companion == null) return;
            Vector3 dir = point - _companion.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            var facing = _companion.GetFacingAuthority();
            if (facing != null && facing.TryAcquireFacing(Core.UnifiedMovementAuthority.MovementSource.SubBehavior, Owner, 0.4f))
                facing.SetLookDirection(Owner, dir);
        }

        public bool Interact(GameObject target)
        {
            Humanoid me = Character;
            if (me == null || target == null) return false;
            // Owning it makes Interact's RPC run here (ResourceGatheringBehavior's pick does the same).
            target.GetComponentInParent<ZNetView>()?.ClaimOwnership();
            Interactable use = target.GetComponentInParent<Interactable>();
            return use != null && use.Interact(me, false, false);
        }

        public bool Equip(ItemDrop.ItemData item)
        {
            CompanionInventory inv = _companion != null ? _companion.GetInventory() : null;
            if (inv == null || item == null) return false;
            if (inv.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand) == item) return true;
            CompanionInventory.EquipmentSlot? from = null;
            foreach (CompanionInventory.EquipmentSlot slot in s_slots)
                if (inv.GetEquippedItem(slot) == item) { from = slot; break; }
            if (!ResourceDataHelper.TryEquipInRightHand(inv, item, from, holsterWeaponOnBack: true)) return false;
            inv.RecalculateEquipmentBonusesPublic();
            inv.ApplyVisualEquipment();
            inv.SaveToZDO();
            return true;
        }

        // Fights belong to the companion's own combat code; a task body only steps aside.
        public void Attack(bool secondary) { }
        public void Block(bool on) { }
        public void Dodge(Vector3 direction) { }

        public void Stop() => _companion?.GetCompanionAI()?.ReleasePathfindingMovement(Owner);
    }
}
