using System.Collections.Generic;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// A body whose hand and pack aren't the vanilla ones (Core 0.2.220): a companion keeps its equipment in CompanionInventory
    /// beside its storage Inventory and never sets Humanoid.m_rightItem, so GetCurrentWeapon() reads unarmed and the storage
    /// misses the equipped axe. ChoreBrain and the haul ask through <see cref="TaskBodyHands"/>; a player body needs nothing.
    /// </summary>
    public interface ITaskBodyHands
    {
        /// <summary>What is in the right hand (null when empty).</summary>
        ItemDrop.ItemData HeldItem { get; }
        /// <summary>Everything carried: the pack and what is equipped.</summary>
        IEnumerable<ItemDrop.ItemData> CarriedItems { get; }
    }

    public static class TaskBodyHands
    {
        private static readonly List<ItemDrop.ItemData> s_none = new List<ItemDrop.ItemData>();

        /// <summary>The body's held item: <see cref="ITaskBodyHands.HeldItem"/>, else vanilla GetCurrentWeapon().</summary>
        public static ItemDrop.ItemData Held(ITaskBody body) =>
            body is ITaskBodyHands hands ? hands.HeldItem : body?.Character != null ? body.Character.GetCurrentWeapon() : null;

        /// <summary>Everything the body carries: <see cref="ITaskBodyHands.CarriedItems"/>, else its Inventory (a player's holds its equipment).</summary>
        public static IEnumerable<ItemDrop.ItemData> Carried(ITaskBody body) =>
            body is ITaskBodyHands hands ? hands.CarriedItems : body?.Inventory != null ? body.Inventory.GetAllItems() : s_none;
    }
}
