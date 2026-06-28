using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI.ContextMenu
{
    /// <summary>
    /// Core-owned right-click context menu. Any Fires mod registers an <see cref="IContextMenuProvider"/>; the
    /// <see cref="FiresContextMenuDriver"/> opens a menu on hold-Alt + right-click, raycasting from the cursor
    /// and collecting rows from every matching provider. Visuals match the parchment book/tooltip. Client-only
    /// in practice (the driver no-ops headless).
    ///
    /// Providers that draw their own non-physics pickables (e.g. the patrol route's collider-less node spheres)
    /// also register a <see cref="MarkerHitTester"/> so the cursor ray can select them without colliders.
    /// </summary>
    public static class FiresContextMenu
    {
        /// <summary>Tests the cursor ray against a provider's non-physics markers; fills <paramref name="target"/> on a hit.</summary>
        public delegate bool MarkerHitTester(Ray cursorRay, out ContextTarget target);

        private static readonly List<IContextMenuProvider> _providers = new List<IContextMenuProvider>();
        private static readonly List<MarkerHitTester> _markerSources = new List<MarkerHitTester>();
        private static ContextMenuView _view;

        public static void Register(IContextMenuProvider provider)
        {
            if (provider != null && !_providers.Contains(provider)) _providers.Add(provider);
        }

        public static void Unregister(IContextMenuProvider provider) => _providers.Remove(provider);

        public static void RegisterMarkerSource(MarkerHitTester tester)
        {
            if (tester != null && !_markerSources.Contains(tester)) _markerSources.Add(tester);
        }

        public static void UnregisterMarkerSource(MarkerHitTester tester) => _markerSources.Remove(tester);

        internal static IReadOnlyList<MarkerHitTester> MarkerSources => _markerSources;

        public static bool IsOpen => _view != null && _view.IsOpen;

        /// <summary>
        /// While true, the hold-Alt driver stays disengaged. A consumer that enters its own modal sub-mode
        /// (e.g. the patrol node "grab" mode, which uses Alt+arrow for large nudges) sets this so Alt doesn't
        /// re-trigger context mode underneath it. Reset it when the sub-mode ends.
        /// </summary>
        public static bool SuppressDriver { get; set; }

        internal static ContextMenuView View => _view;

        /// <summary>Opens a menu at <paramref name="screenPos"/> for the target, or no-ops if no provider has rows.</summary>
        public static void Open(ContextTarget target, Vector2 screenPos)
        {
            if (target == null) { Close(); return; }
            var collected = Collect(target);
            if (collected.items == null) { Close(); return; }
            if (_view == null) _view = new ContextMenuView();
            _view.Show(collected.title, collected.items, screenPos);
        }

        public static void Close() => _view?.Close();

        private static (string title, List<ContextMenuItem> items) Collect(ContextTarget target)
        {
            string title = null;
            var items = new List<ContextMenuItem>();
            foreach (var p in _providers)
            {
                IEnumerable<ContextMenuItem> group;
                try { group = p.GetItems(target); }
                catch (Exception ex) { Debug.LogWarning($"[FiresContextMenu] provider {p.GetType().Name} threw: {ex.Message}"); continue; }
                if (group == null) continue;

                var list = new List<ContextMenuItem>(group);
                if (list.Count == 0) continue;

                if (items.Count > 0) items.Add(ContextMenuItem.Sep());
                items.AddRange(list);
                if (title == null) { try { title = p.TitleFor(target); } catch { } }
            }
            return items.Count == 0 ? (null, null) : (title, items);
        }
    }
}
