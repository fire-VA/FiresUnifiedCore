using System;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Cosmetic pet seam for the companion bag's Pet cell. The pet owner mod assigns the three delegates at its Awake; Core
    /// never references that mod. When unassigned, the Pet cell accepts nothing and every call site is a no-op.
    /// </summary>
    // Added for the companion panel's Pet cell.
    public static class PetBridge
    {
        /// <summary>Returns null on success, else a refusal reason. Must be called on the owner's ZDO owner.
        /// Args: (owner, pet item prefab name or "" to clear).</summary>
        public static Func<Character, string, string> SetPet;

        /// <summary>The owner's current pet creature prefab, or "".</summary>
        public static Func<Character, string> GetPet;

        /// <summary>True for pet items.</summary>
        public static Func<ItemDrop.ItemData, bool> IsPetItem;

        public static bool IsAvailable => SetPet != null && GetPet != null && IsPetItem != null;
    }
}
