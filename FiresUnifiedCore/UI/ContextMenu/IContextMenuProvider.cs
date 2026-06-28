using System.Collections.Generic;

namespace FiresCore.UI.ContextMenu
{
    /// <summary>
    /// A mod registers one of these with <see cref="FiresContextMenu.Register"/> to contribute rows when the
    /// player hold-Alt + right-clicks a world target. Implementations should be cheap and side-effect-free in
    /// <see cref="GetItems"/> (it runs on every right-click) and return null/empty when the target isn't theirs.
    /// Items from all matching providers are concatenated, separated by a divider, in registration order.
    /// </summary>
    public interface IContextMenuProvider
    {
        IEnumerable<ContextMenuItem> GetItems(ContextTarget target);

        /// <summary>Optional menu header for this target; the first non-null wins. Return null for no header.</summary>
        string TitleFor(ContextTarget target);
    }
}
