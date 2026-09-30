using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// A body the companion brain can drive (Tools\COMPANION_BRAIN_PLAN.md, "one brain, two bodies"): a companion through its BaseAI,
    /// or a Player driven through inputs (the FDT autoplay bot). The brain decides; the body carries it out. Slice 2 (chests, gathering,
    /// workbench) and slice 3 (stations, farming, repair) are written against this contract.
    /// </summary>
    public interface ITaskBody
    {
        Humanoid Character { get; }
        Vector3 Position { get; }
        Vector3 Forward { get; }

        /// <summary>The vanilla inventory the body carries and deposits from (a companion's storage, a player's own).</summary>
        Inventory Inventory { get; }

        /// <summary>The player this body answers to, for chest and ward ownership (a player's own id).</summary>
        long OwnerPlayerId { get; }

        /// <summary>Still carrying out the last order (walking, swinging, interacting).</summary>
        bool IsBusy { get; }

        void MoveTo(Vector3 point, bool run);
        void LookAt(Vector3 point);
        bool Interact(GameObject target);
        bool Equip(ItemDrop.ItemData item);
        void Attack(bool secondary);
        void Block(bool on);
        void Dodge(Vector3 direction);
        void Stop();
    }
}
