using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Makes companions actually spend consumable weapons. Vanilla's Attack consumes the thrown item from the
    /// Humanoid inventory, but companion equipment lives in CompanionInventory, so every throw minted a new spear
    /// and wild throwers flooded servers with ItemDrop stacks. Tamed companions now consume from their own
    /// inventory, clearing the slot on the last one, while the landed spear stays retrievable. Wild companions
    /// keep monster parity and stay armed, but their projectiles no longer leave an item behind.
    /// </summary>
    [HarmonyPatch]
    internal static class CompanionConsumablePatches
    {
        private static readonly AccessTools.FieldRef<Attack, Humanoid> CharacterRef =
            AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");
        private static readonly AccessTools.FieldRef<Attack, ItemDrop.ItemData> WeaponRef =
            AccessTools.FieldRefAccess<Attack, ItemDrop.ItemData>("m_weapon");

        [HarmonyPatch(typeof(Attack), "ConsumeItem")]
        [HarmonyPrefix]
        private static bool Attack_ConsumeItem_CompanionPrefix(Attack __instance)
        {
            try
            {
                var character = CharacterRef(__instance);
                if (character == null) return true;
                var companion = character.GetComponent<CompanionController>();
                if (companion == null) return true;

                if (!companion.isTamed) return false;

                var weapon = WeaponRef(__instance);
                var inventory = character.GetComponent<CompanionInventory>();
                if (weapon == null || inventory == null) return false;

                var slot = FindEquippedSlot(inventory, weapon);
                if (weapon.m_shared.m_maxStackSize > 1 && weapon.m_stack > 1)
                {
                    --weapon.m_stack;
                    if (slot.HasValue)
                        inventory.SetEquippedStack(slot.Value, weapon.m_stack);
                    inventory.TriggerSaveToZDO();
                    return false;
                }

                if (slot.HasValue)
                    inventory.UnequipSlot(slot.Value);
                try { character.UnequipItem(weapon, false); } catch { }
                inventory.TriggerSaveToZDO();
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static CompanionInventory.EquipmentSlot? FindEquippedSlot(
            CompanionInventory inventory, ItemDrop.ItemData weapon)
        {
            foreach (var kv in inventory.GetAllEquipped())
                if (ReferenceEquals(kv.Value, weapon))
                    return kv.Key;

            string name = weapon.m_shared != null ? weapon.m_shared.m_name : null;
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var kv in inventory.GetAllEquipped())
                if (kv.Value?.m_shared != null && kv.Value.m_shared.m_name == name)
                    return kv.Key;
            return null;
        }

        // Runs after Setup stored the thrown item (the m_respawnItemOnHit path) — wild companions'
        // projectiles must not seed the ground with recoverable items; vanilla monsters' don't either.
        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        [HarmonyPostfix]
        private static void Projectile_Setup_WildNoDrop(Projectile __instance, Character owner)
        {
            if (owner == null || __instance == null || __instance.m_spawnItem == null) return;
            var companion = owner.GetComponent<CompanionController>();
            if (companion == null || companion.isTamed) return;
            __instance.m_spawnItem = null;
        }
    }
}
