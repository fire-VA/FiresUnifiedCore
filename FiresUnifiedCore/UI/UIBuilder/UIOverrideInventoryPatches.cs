using HarmonyLib;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Harmony patches that keep the UIOverride slot/inventory systems in sync
    /// with the live game. These patches are lightweight — they just trigger
    /// discovery/dirty-flag updates so the per-frame mirror components have
    /// current data to read.
    /// </summary>
    [HarmonyPatch]
    internal static class UIOverrideInventoryPatches
    {
        //  Discover slots when inventory opens

        [HarmonyPatch(typeof(InventoryGui), "Show")]
        [HarmonyPostfix]
        private static void InventoryGui_Show_Postfix()
        {
            if (Player.m_localPlayer == null) return;

            try
            {
                UIOverrideSlotSystem.DiscoverSlotsFromGameState();
                UIOverrideEquipmentPanel.MarkDirty();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[UIOverrideInventoryPatches] Show postfix failed: {ex.Message}");
            }
        }

        //  Mark dirty when inventory changes

        [HarmonyPatch(typeof(InventoryGui), "SetupDragItem")]
        [HarmonyPostfix]
        private static void InventoryGui_SetupDragItem_Postfix()
        {
            UIOverrideEquipmentPanel.MarkDirty();
        }

        //  Mark dirty when player equips/unequips

        [HarmonyPatch(typeof(Humanoid), "EquipItem")]
        [HarmonyPostfix]
        private static void Humanoid_EquipItem_Postfix(Humanoid __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            UIOverrideEquipmentPanel.MarkDirty();
        }

        [HarmonyPatch(typeof(Humanoid), "UnequipItem")]
        [HarmonyPostfix]
        private static void Humanoid_UnequipItem_Postfix(Humanoid __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            UIOverrideEquipmentPanel.MarkDirty();
        }
    }
}
