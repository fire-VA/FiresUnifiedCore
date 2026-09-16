using System;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Self-contained equipment panel state manager for the UI override system.
    /// Tracks dirty state for the override's visual sync loop. This is NOT the
    /// VAInventory EquipmentPanel — it is a lightweight state flag that the
    /// override slot mirrors use to coordinate visual refreshes.
    /// </summary>
    public static class UIOverrideEquipmentPanel
    {
        private static bool _isDirty = true;
        private static int _lastDirtyFrame = -1;

        /// <summary>
        /// Marks the equipment panel visuals as needing a refresh.
        /// Called after any inventory mutation (drop, equip, swap, etc.).
        /// </summary>
        public static void MarkDirty()
        {
            _isDirty = true;
            _lastDirtyFrame = Time.frameCount;
        }

        /// <summary>
        /// Returns true if the panel was marked dirty since the last consume.
        /// </summary>
        public static bool IsDirty => _isDirty;

        /// <summary>
        /// Consumes the dirty flag (sets it to false). Call this after
        /// the visual sync pass has completed.
        /// </summary>
        public static void ConsumeDirty()
        {
            _isDirty = false;
        }

        /// <summary>
        /// Frame number of the last MarkDirty call. Useful for throttling.
        /// </summary>
        public static int LastDirtyFrame => _lastDirtyFrame;
    }
}
