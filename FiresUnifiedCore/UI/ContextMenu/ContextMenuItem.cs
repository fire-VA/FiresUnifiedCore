using System;
using System.Collections.Generic;

namespace FiresCore.UI.ContextMenu
{
    /// <summary>
    /// One row in a context menu. A leaf row carries an <see cref="OnPick"/> action; a separator renders a thin
    /// divider; sub-items are reserved for a future flyout (Phase 1 leaf-only).
    /// </summary>
    public sealed class ContextMenuItem
    {
        public string Label;
        public Action OnPick;
        public bool Enabled = true;
        public string Tooltip;
        public bool IsSeparator;
        public List<ContextMenuItem> SubItems;

        public static ContextMenuItem Sep() => new ContextMenuItem { IsSeparator = true };

        public static ContextMenuItem Row(string label, Action onPick, bool enabled = true)
            => new ContextMenuItem { Label = label, OnPick = onPick, Enabled = enabled };
    }
}
