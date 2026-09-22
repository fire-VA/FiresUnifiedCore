using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FiresCore.UI.ContextMenu
{
    using Input = UnityEngine.Input;   // FiresCore.Input (a namespace) otherwise shadows UnityEngine.Input here
    using World = global::World;       // likewise FiresCore.World shadows vanilla's World

    // Right-click menus on the main menu's world list. A mod registers a provider that returns rows for one world; a
    // plain right-click on a world in Select World shows every provider's rows in the shared context-menu view, and
    // the list is re-read after a row runs so a world it added or removed shows at once. Core only finds the world
    // under the cursor; what a row does with that world belongs to the provider. Left-click stays vanilla.
    public static class WorldListContextMenu
    {
        public delegate IEnumerable<ContextMenuItem> WorldRows(World world);

        private static readonly List<KeyValuePair<string, WorldRows>> Providers = new List<KeyValuePair<string, WorldRows>>();
        private static readonly List<RaycastResult> Hits = new List<RaycastResult>();

        private static readonly AccessTools.FieldRef<FejdStartup, List<GameObject>> RowObjects =
            AccessTools.FieldRefAccess<FejdStartup, List<GameObject>>("m_worldListElements");
        private static readonly AccessTools.FieldRef<FejdStartup, List<World>> RowWorlds =
            AccessTools.FieldRefAccess<FejdStartup, List<World>>("m_worlds");
        private static readonly MethodInfo UpdateWorldListMethod =
            AccessTools.Method(typeof(FejdStartup), "UpdateWorldList", new[] { typeof(bool) });

        // Adds or replaces the rows an owner (a mod name) contributes for a world.
        public static void Register(string owner, WorldRows rows)
        {
            if (string.IsNullOrEmpty(owner) || rows == null) return;
            Unregister(owner);
            Providers.Add(new KeyValuePair<string, WorldRows>(owner, rows));
        }

        public static void Unregister(string owner) => Providers.RemoveAll(provider => provider.Key == owner);

        // Re-reads the saves and redraws Select World, for a provider that just added, renamed or removed a world.
        public static void RefreshWorldList()
        {
            var menu = FejdStartup.instance;
            if (menu != null) UpdateWorldListMethod?.Invoke(menu, new object[] { true });
        }

        // Polled every frame by FiresContextMenuDriver while the main menu is up.
        internal static void Tick()
        {
            if (!Input.GetMouseButtonDown(1) || Providers.Count == 0 || !ContextMenuConfig.Enabled) return;
            var menu = FejdStartup.instance;
            if (menu == null) return;

            Vector2 mouse = Input.mousePosition;
            World world = WorldUnderPointer(menu, mouse);
            if (world == null) return;

            var items = CollectRows(world);
            if (items.Count == 0) return;
            FiresContextMenu.Close();
            FiresContextMenu.OpenCustom(world.m_name, items, mouse);
        }

        // The topmost UI element under the cursor, walked up to its Select World row. The UI raycast respects the
        // list's scroll mask, so a row scrolled out of view can't be picked through the buttons around the list.
        private static World WorldUnderPointer(FejdStartup menu, Vector2 mouse)
        {
            var eventSystem = EventSystem.current;
            var rows = RowObjects(menu);
            var worlds = RowWorlds(menu);
            if (eventSystem == null || rows == null || worlds == null) return null;

            Hits.Clear();
            eventSystem.RaycastAll(new PointerEventData(eventSystem) { position = mouse }, Hits);
            if (Hits.Count == 0) return null;

            for (Transform node = Hits[0].gameObject.transform; node != null; node = node.parent)
            {
                int index = rows.IndexOf(node.gameObject);
                if (index >= 0) return index < worlds.Count ? worlds[index] : null;
            }
            return null;
        }

        private static List<ContextMenuItem> CollectRows(World world)
        {
            var items = new List<ContextMenuItem>();
            foreach (var provider in Providers)
            {
                IEnumerable<ContextMenuItem> rows;
                try { rows = provider.Value(world); }
                catch (Exception ex) { Debug.LogWarning($"[FiresContextMenu] world-list rows from {provider.Key} threw: {ex.Message}"); continue; }
                if (rows == null) continue;

                var group = new List<ContextMenuItem>(rows);
                if (group.Count == 0) continue;
                if (items.Count > 0) items.Add(ContextMenuItem.Sep());
                foreach (var item in group) items.Add(WithRefreshAfterPick(item, provider.Key));
            }
            return items;
        }

        // A copy of the provider's row whose pick also re-reads the world list; the provider's own row is left alone.
        private static ContextMenuItem WithRefreshAfterPick(ContextMenuItem item, string owner)
        {
            if (item == null || item.IsSeparator || item.OnPick == null) return item;
            var pick = item.OnPick;
            string label = item.Label;
            return new ContextMenuItem
            {
                Label = label,
                Enabled = item.Enabled,
                Tooltip = item.Tooltip,
                SubItems = item.SubItems,
                OnPick = () =>
                {
                    try { pick(); }
                    catch (Exception ex) { Debug.LogWarning($"[FiresContextMenu] world-list row '{label}' from {owner} threw: {ex.Message}"); }
                    RefreshWorldList();
                },
            };
        }
    }
}
