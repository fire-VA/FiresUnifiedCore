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

        /// <summary>
        /// A claim fully OWNS a target: when it returns true, the aggregated parchment row list is NOT shown — the
        /// claim has opened its own dedicated panel for that target instead. Register one when a consumer wants a
        /// proper window (not the shared row list, which concatenates every provider's rows) for its own world
        /// objects. First claim to return true wins; claims run before row collection.
        /// </summary>
        public delegate bool TargetClaim(ContextTarget target, Vector2 screenPos);

        private static readonly List<IContextMenuProvider> _providers = new List<IContextMenuProvider>();
        private static readonly List<MarkerHitTester> _markerSources = new List<MarkerHitTester>();
        private static readonly List<TargetClaim> _claims = new List<TargetClaim>();
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

        public static void RegisterClaim(TargetClaim claim)
        {
            if (claim != null && !_claims.Contains(claim)) _claims.Add(claim);
        }

        public static void UnregisterClaim(TargetClaim claim) => _claims.Remove(claim);

        internal static IReadOnlyList<MarkerHitTester> MarkerSources => _markerSources;

        public static bool IsOpen => _view != null && _view.IsOpen;

        /// <summary>
        /// While true, the hold-Alt driver stays disengaged. A consumer that enters its own modal sub-mode
        /// (e.g. the patrol node "grab" mode, which uses Alt+arrow for large nudges) sets this so Alt doesn't
        /// re-trigger context mode underneath it. Reset it when the sub-mode ends.
        /// </summary>
        public static bool SuppressDriver { get; set; }

        internal static ContextMenuView View => _view;

        /// <summary>Opens a menu at <paramref name="screenPos"/> for the target, or closes/no-ops if nothing claims it.</summary>
        public static void Open(ContextTarget target, Vector2 screenPos)
        {
            if (!TryOpen(target, screenPos)) Close();
        }

        /// <summary>
        /// Like <see cref="Open"/> but returns whether anything opened (a claim took the target, or some provider
        /// contributed rows) and does NOT close on a miss — so the driver can walk several candidate targets under
        /// the cursor and stop at the first that yields a menu. That is what makes picking forgiving.
        /// </summary>
        public static bool TryOpen(ContextTarget target, Vector2 screenPos)
        {
            if (target == null) return false;
            // A claim can fully own this target and open its own dedicated panel instead of the aggregated row list.
            for (int i = 0; i < _claims.Count; i++)
            {
                try { if (_claims[i](target, screenPos)) { Close(); return true; } }
                catch (Exception ex) { Debug.LogWarning($"[FiresContextMenu] claim threw: {ex.Message}"); }
            }
            var collected = Collect(target);
            if (collected.items == null) return false;
            if (_view == null) _view = new ContextMenuView();
            _view.Show(collected.title, collected.items, screenPos);
            return true;
        }

        public static void Close() => _view?.Close();

        /// <summary>
        /// Opens a standalone menu with caller-provided rows at <paramref name="screenPos"/> (e.g. over
        /// the build HUD's piece grid). No providers/claims run, and the menu pumps its own hover/click
        /// input — the hold-Alt driver only drives menus it opened itself.
        /// </summary>
        public static void OpenCustom(string title, List<ContextMenuItem> items, Vector2 screenPos)
        {
            if (items == null || items.Count == 0) return;
            if (_view == null) _view = new ContextMenuView();
            _view.Show(title, items, screenPos);
            _view.AttachSelfPump();
        }

        private static (string title, List<ContextMenuItem> items) Collect(ContextTarget target)
        {
            string title = null;
            var items = new List<ContextMenuItem>();
            foreach (var provider in _providers)
            {
                IEnumerable<ContextMenuItem> group;
                try { group = provider.GetItems(target); }
                catch (Exception ex) { Debug.LogWarning($"[FiresContextMenu] provider {provider.GetType().Name} threw: {ex.Message}"); continue; }
                if (group == null) continue;

                var list = new List<ContextMenuItem>(group);
                if (list.Count == 0) continue;

                if (items.Count > 0) items.Add(ContextMenuItem.Sep());
                items.AddRange(list);
                if (title == null) { try { title = provider.TitleFor(target); } catch { } }
            }
            return items.Count == 0 ? (null, null) : (title, items);
        }
    }
}
