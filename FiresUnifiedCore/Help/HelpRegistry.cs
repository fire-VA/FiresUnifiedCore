using System;
using System.Collections.Generic;
using System.Linq;

namespace FiresCore.Help
{
    // One registered help section: a builder callback grouped under a mod + category, with a sort key and
    // optional admin gating.
    public sealed class HelpSection
    {
        public string ModId;
        public string Group;
        public string Title;
        public int SortOrder;
        public bool AdminOnly;
        public Action<HelpContentWriter> Build;
        internal int ModOrder;
    }

    // Where every mod registers its own help content. The shared HelpPanel reads this to build its sidebar
    // and render sections, so the help UI lives in one place while each mod owns only its sections.
    public static class HelpRegistry
    {
        private static readonly List<HelpSection> _sections = new List<HelpSection>();
        private static readonly Dictionary<string, int> _modOrder = new Dictionary<string, int>();

        public static event Action Changed;

        public static int ModCount => _modOrder.Count;

        public static void RegisterSection(string modId, string group, string title, int sortOrder,
            Action<HelpContentWriter> build, bool adminOnly = false)
        {
            if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(title) || build == null) return;

            if (!_modOrder.TryGetValue(modId, out int order))
            {
                order = _modOrder.Count;
                _modOrder[modId] = order;
            }

            _sections.RemoveAll(s => s.ModId == modId && s.Title == title);
            _sections.Add(new HelpSection
            {
                ModId = modId,
                Group = group ?? string.Empty,
                Title = title,
                SortOrder = sortOrder,
                AdminOnly = adminOnly,
                Build = build,
                ModOrder = order,
            });
            Changed?.Invoke();
        }

        // Ordered, admin-filtered sections for rendering: mods in registration order, then by SortOrder.
        public static List<HelpSection> GetVisibleSections(bool isAdmin) =>
            _sections
                .Where(s => isAdmin || !s.AdminOnly)
                .OrderBy(s => s.ModOrder)
                .ThenBy(s => s.SortOrder)
                .ToList();
    }
}
