using System;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Open / query hooks for the companion UI screens, which live in the frontend (the host mod, or
    /// FiresCompanions standalone) — NOT in Core. The frontend registers these on init; while none is
    /// present every call is a null-safe no-op (false / nothing opens). Lets Core engine code drive
    /// the screens (open inventory, check open-state for idle behavior) without a hard reference to
    /// the frontend screen types.
    /// </summary>
    public static class NpcUiBridge
    {
        /// <summary>Open the companion inventory screen for this NPC and the viewing player.</summary>
        public static Action<GameObject, Player> ShowInventory;

        /// <summary>True when the inventory screen is currently open for this NPC.</summary>
        public static Func<GameObject, bool> InventoryOpenFor;

        /// <summary>True when the stats screen is currently open for this NPC.</summary>
        public static Func<GameObject, bool> StatsOpenFor;

        public static void RaiseShowInventory(GameObject npc, Player viewer)
        {
            try { ShowInventory?.Invoke(npc, viewer); } catch { }
        }

        public static bool IsInventoryOpenFor(GameObject npc)
        {
            try { return InventoryOpenFor != null && InventoryOpenFor(npc); } catch { return false; }
        }

        public static bool IsStatsOpenFor(GameObject npc)
        {
            try { return StatsOpenFor != null && StatsOpenFor(npc); } catch { return false; }
        }
    }
}
