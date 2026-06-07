using System;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Soft-dependency seam letting the optional Marketplace NPC framework drive the
    /// integrated-only behaviour of a stationed NPC — opening the admin config panel,
    /// routing quest/dialogue/info/trader interaction, and supplying territory bounds.
    /// While the framework is absent every delegate is null: a stationed NPC just stands,
    /// greets, and wanders by its configured radius, with no quest/trader UI. The NPC is
    /// passed as a GameObject so neither side takes a hard reference to the other's types —
    /// the framework handler resolves whatever component it needs from the object.
    /// </summary>
    public static class NpcModeBridge
    {
        /// <summary>Open the admin NPC-configuration panel for this stationed NPC (Shift+E).</summary>
        public static Action<GameObject> OpenConfigPanel;

        /// <summary>Handle a player interacting with a stationed NPC (quests/dialogue/info/trade). Return true if handled.</summary>
        public static Func<GameObject, Player, bool> Interaction;

        /// <summary>Territory radius at a world position, or null when not inside a territory.</summary>
        public static Func<Vector3, float?> TerritoryRadiusAt;

        /// <summary>Territory name at a world position, or null/empty when not inside a territory.</summary>
        public static Func<Vector3, string> TerritoryNameAt;

        public static void RaiseOpenConfigPanel(GameObject npc)
        {
            try { OpenConfigPanel?.Invoke(npc); } catch { }
        }

        public static bool RaiseInteraction(GameObject npc, Player player)
        {
            try { return Interaction != null && Interaction(npc, player); } catch { return false; }
        }

        public static float? GetTerritoryRadius(Vector3 pos)
        {
            try { return TerritoryRadiusAt?.Invoke(pos); } catch { return null; }
        }

        public static string GetTerritoryName(Vector3 pos)
        {
            try { return TerritoryNameAt?.Invoke(pos); } catch { return null; }
        }
    }
}
