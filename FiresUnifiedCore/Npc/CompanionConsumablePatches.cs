using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Makes companion attacks actually pay for consumable weapons.
    ///
    /// Companions execute vanilla <see cref="Attack"/> instances (CompanionAttackBridge), so a thrown
    /// spear runs the full vanilla flow: FireProjectileBurst hands the weapon ItemData to the projectile
    /// (m_respawnItemOnHit → a recoverable ItemDrop where it lands) and ConsumeItem() removes the weapon
    /// from m_character.GetInventory() — the VANILLA Humanoid inventory. Companion equipment lives in
    /// CompanionInventory, a separate store the vanilla inventory never sees, so vanilla consumption
    /// silently no-oped while the drop still spawned: every throw minted a new spear. Wild spear-throwers
    /// never stop attacking, so the mints merged into massive ItemDrop stacks and flooded the server.
    ///
    /// - TAMED companions consume for real: stacked throwables decrement; the last one clears the slot
    ///   through CompanionInventory.UnequipSlot (persisted name, stats, visuals). The landed ItemDrop
    ///   stays retrievable — a throw moves the item from hand to ground, net zero items in the world.
    /// - WILD companions (isTamed=false, incl. untamed guards) keep vanilla-monster parity: no inventory
    ///   to drain, they stay armed and keep fighting — but their projectiles never leave a recoverable
    ///   item behind (m_spawnItem cleared), so nothing can pile up.
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
