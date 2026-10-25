using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Storage.Patches
{
    /// <summary>
    /// Sorting; dormant namespace (applied only while Storage Features is on):
    ///   keys      InventoryGui.Update postfix, only while the inventory is open and no text field has focus: Sort Key sorts the bag
    ///             (Shift: the open container, which this peer owns since it opened it), Quick Stack Key, Restock Key
    ///   favourite InventoryGrid.OnLeftClick prefix: Alt+click on a slot of the player's own bag toggles it as a favourite (never sorted,
    ///             never quick-stacked); the click is consumed only then
    /// </summary>
    internal static class SortKeys
    {
        internal static bool On => StorageFeatures.Feature(StorageFeatures.Part.Sort) && StorageConfig.AutoSort != null && StorageConfig.AutoSort.Value;
    }

    [HarmonyPatch(typeof(InventoryGui), "Update")]
    internal static class InventoryGuiSortKeys
    {
        private static readonly AccessTools.FieldRef<InventoryGui, Container> CurrentContainer = AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer");

        private static void Postfix(InventoryGui __instance)
        {
            if (!SortKeys.On || !InventoryGui.IsVisible() || Console.IsVisible() || Chat.instance != null && Chat.instance.HasFocus()) return;
            if (ChestTagsPanel.Typing) return;   // Typing O/P/U into the tags field must not sort
            Player me = Player.m_localPlayer;
            if (me == null) return;
            if (StorageConfig.SortKey.Value.IsDown())
            {
                bool chest = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
                Container open = CurrentContainer(__instance);
                if (chest && open != null && ContainerRegistry.NView(open) != null && ContainerRegistry.NView(open).IsOwner())
                {
                    int n = InventorySort.Sort(open.GetInventory(), null, StorageConfig.SortMode.Value);
                    me.Message(MessageHud.MessageType.TopLeft, $"Sorted the container ({n} stacks)");
                }
                else
                {
                    int n = InventorySort.Sort(me.GetInventory(), me, StorageConfig.SortMode.Value);
                    me.Message(MessageHud.MessageType.TopLeft, $"Sorted your bag ({n} stacks)");
                }
            }
            else if (StorageConfig.QuickStackKey.Value.IsDown() && !QuickStack.Running) __instance.StartCoroutine(QuickStack.Stack(me));
            else if (StorageConfig.RestockKey.Value.IsDown() && !QuickStack.Running) __instance.StartCoroutine(QuickStack.Restock(me));
        }
    }

    [HarmonyPatch(typeof(InventoryGrid), "OnLeftClick")]
    internal static class FavouriteSlotClick
    {
        private static readonly System.Func<InventoryGrid, GameObject, Vector2i> ButtonPos =
            AccessTools.MethodDelegate<System.Func<InventoryGrid, GameObject, Vector2i>>(AccessTools.Method(typeof(InventoryGrid), "GetButtonPos"));

        private static bool Prefix(InventoryGrid __instance, UIInputHandler clickHandler)
        {
            if (!SortKeys.On || !(UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt))) return true;
            Player me = Player.m_localPlayer;
            if (me == null || __instance.GetInventory() != me.GetInventory() || clickHandler == null) return true;
            Vector2i pos = ButtonPos(__instance, clickHandler.gameObject);
            if (pos.x < 0) return true;
            bool fav = InventorySort.ToggleFavourite(me, pos);
            me.Message(MessageHud.MessageType.TopLeft, fav ? "Slot favourited: sort and quick stack leave it alone" : "Slot no longer a favourite");
            FiresLogger.LogDebug($"[Storage] favourite slot {pos.x},{pos.y}: {fav}");
            return false;
        }
    }
}
