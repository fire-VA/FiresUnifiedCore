using UnityEngine;
using System;

namespace FiresCore.Npc
{
    // Weapon holstering logic
    public partial class CompanionIdleBehavior
    {
        #region Weapon Holstering

        private void UpdateWeaponHolstering()
        {
            if (!holsterWeaponsWhenIdle) return;
            if (_inventory == null) return;

            bool shouldBeHolstered = ShouldWeaponsBeHolstered();

            if (shouldBeHolstered && !_weaponsHolstered)
            {
                if (_idleStartTime == 0f)
                    _idleStartTime = Time.time;

                if (Time.time - _idleStartTime >= holsterDelay)
                {
                    if (!_holsterDecisionMade)
                    {
                        _holsterDecisionMade = true;

                        if (UnityEngine.Random.value < holsterChance)
                        {
                            HolsterWeapons();
                        }
                        else if (VerboseLogging)
                        {
                            Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} decided not to holster weapons (random chance)");
                        }
                    }
                }
            }
            else if (!shouldBeHolstered && _weaponsHolstered)
            {
                UnholsterWeapons();
                _holsterDecisionMade = false;
                _idleStartTime = 0f;
            }
            else if (!shouldBeHolstered)
            {
                _holsterDecisionMade = false;
                _idleStartTime = 0f;
            }
        }

        private bool ShouldWeaponsBeHolstered()
        {
            if (_combatMovement != null && _combatMovement.IsInCombat)
                return false;

            if (Time.time - _lastCombatTime < combatCooldownForIdle)
                return false;

            if (_activeSubBehavior != null)
                return false;

            return _currentIdleState == IdleState.Standing;
        }

        private void HolsterWeapons()
        {
            if (_weaponsHolstered) return;
            if (_inventory == null) return;

            try
            {
                bool movedAny = false;

                var rightHandItem = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
                if (rightHandItem != null)
                {
                    var rightBackItem = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                    if (rightBackItem == null)
                    {
                        _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);
                        _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightBack, rightHandItem);
                        movedAny = true;

                        if (VerboseLogging)
                            Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} holstered right hand weapon to back");
                    }
                }

                var leftHandItem = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
                if (leftHandItem != null)
                {
                    var leftBackItem = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
                    if (leftBackItem == null)
                    {
                        _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.LeftHand);
                        _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftBack, leftHandItem);
                        movedAny = true;

                        if (VerboseLogging)
                            Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} holstered left hand item to back");
                    }
                }

                if (movedAny)
                {
                    _weaponsHolstered = true;
                    
                    _inventory.RecalculateEquipmentBonusesPublic();
                    _inventory.ApplyVisualEquipment();
                    _inventory.TriggerSaveToZDO();

                    if (VerboseLogging)
                        Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} weapons holstered and synced to network");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionIdleBehavior] Failed to holster weapons: {ex.Message}");
            }
        }

        private void UnholsterWeapons()
        {
            if (!_weaponsHolstered) return;
            if (_inventory == null) return;

            try
            {
                bool movedAny = false;

                var rightBackItem = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightBack);
                if (rightBackItem != null)
                {
                    if (IsWeaponItem(rightBackItem))
                    {
                        var rightHandItem = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
                        if (rightHandItem == null)
                        {
                            _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightBack);
                            _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, rightBackItem);
                            movedAny = true;

                            if (VerboseLogging)
                                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} unholstered weapon to right hand");
                        }
                    }
                }

                var leftBackItem = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftBack);
                if (leftBackItem != null)
                {
                    if (IsOffhandItem(leftBackItem))
                    {
                        var leftHandItem = _inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
                        if (leftHandItem == null)
                        {
                            _inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.LeftBack);
                            _inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.LeftHand, leftBackItem);
                            movedAny = true;

                            if (VerboseLogging)
                                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} unholstered item to left hand");
                        }
                    }
                }

                if (movedAny)
                {
                    _weaponsHolstered = false;
                    
                    _inventory.RecalculateEquipmentBonusesPublic();
                    _inventory.ApplyVisualEquipment();
                    _inventory.TriggerSaveToZDO();

                    if (VerboseLogging)
                        Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} weapons unholstered and synced to network");
                }
                else
                {
                    _weaponsHolstered = false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionIdleBehavior] Failed to unholster weapons: {ex.Message}");
                _weaponsHolstered = false;
            }
        }

        private bool IsWeaponItem(ItemDrop.ItemData item)
        {
            if (item == null) return false;

            var itemType = item.m_shared.m_itemType;
            return itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                itemType == ItemDrop.ItemData.ItemType.Bow ||
                itemType == ItemDrop.ItemData.ItemType.Tool;
        }

        private bool IsOffhandItem(ItemDrop.ItemData item)
        {
            if (item == null) return false;

            var itemType = item.m_shared.m_itemType;
            return itemType == ItemDrop.ItemData.ItemType.Shield ||
                itemType == ItemDrop.ItemData.ItemType.Torch;
        }

        public void ForceUnholsterWeapons()
        {
            if (_weaponsHolstered)
            {
                UnholsterWeapons();
            }
        }

        #endregion
    }
}
