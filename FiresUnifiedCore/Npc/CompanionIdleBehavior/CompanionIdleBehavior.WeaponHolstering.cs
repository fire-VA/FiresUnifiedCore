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

            // 10-30 ([fgn] proved: idle wander / emote / sit cycling holstered + unholstered every few seconds, a 35 ms VisEquipment
            // model swap each): with "Holster only on combat change" ON the holster state follows COMBAT only (unholster when a fight
            // starts, holster again once it's been over for the cooldown), ignoring idle sub-states, and never swaps twice within 30 s.
            bool combatOnly = CombatOnlyHolster;
            bool shouldBeHolstered = combatOnly ? ShouldWeaponsBeHolsteredCombatOnly() : ShouldWeaponsBeHolstered();
            if (combatOnly && shouldBeHolstered != _weaponsHolstered && Time.time - _lastHolsterSwap < MinSwapSeconds
                && !(_combatMovement != null && _combatMovement.IsInCombat)) return;   // a fight always unholsters at once
            LogSwapRate();

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

        private bool ShouldWeaponsBeHolsteredCombatOnly()
        {
            if (_combatMovement != null && _combatMovement.IsInCombat) return false;
            return Time.time - _lastCombatTime >= combatCooldownForIdle;
        }

        private const float MinSwapSeconds = 30f;
        private float _lastHolsterSwap = -999f;
        private static int s_holsters, s_unholsters; private static float s_rateNext;
        // 1086 ([generator], Fire 11-01: configs live in their own mods): bound by FiresCompanions; Core reads the owner's value (old default without it).
        private static readonly FiresCore.Config.OwnedSetting<bool> s_combatOnly = new FiresCore.Config.OwnedSetting<bool>("Companions", "Holster only on combat change", true);
        private static bool CombatOnlyHolster
        {
            get
            {
                return s_combatOnly.Value;
            }
        }
        private static void LogSwapRate()
        {
            if (Time.unscaledTime < s_rateNext) return;
            if (s_rateNext > 0f && (s_holsters + s_unholsters) > 0)
                global::FiresCore.Logging.FiresLogger.LogDebug($"[CompanionHolster] last minute: {s_holsters + s_unholsters} weapon swap(s) (holster {s_holsters}, unholster {s_unholsters}); combat-only {(CombatOnlyHolster ? "ON" : "off")}");
            s_holsters = s_unholsters = 0; s_rateNext = Time.unscaledTime + 60f;
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
            s_holsters++; _lastHolsterSwap = Time.time;
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
            s_unholsters++; _lastHolsterSwap = Time.time;
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
