using System;

namespace FiresCore.Npc.Patrol
{
    /// <summary>
    /// Shared DBSM authoring state across the two editing surfaces — the world node context menu
    /// (mark section start/end, toggle stops) and the dressing-room preset editor (envelope sliders,
    /// preset CRUD). Tracks which route + preset is being edited and a pending "section start" index
    /// while marking a span, and is the single choke-point that mutates a route's presets and persists
    /// them (<see cref="PatrolRouteManager.SaveRouteFull"/>), keeping the live color preview in sync.
    ///
    /// SaveRouteFull reloads the whole route dict, so callers must not hold a route reference across a
    /// mutate — always go through <see cref="Mutate"/>, which fetches, edits, saves, then refreshes.
    /// </summary>
    public static class PatrolEditSession
    {
        public static string RouteName;
        public static string PresetName;
        public static int PendingSectionStart = -1; // set by "mark section start", consumed by "mark section end"

        /// <summary>The selected editing preset on <paramref name="route"/>, falling back to its first preset (null if none).</summary>
        public static SpeedPreset GetEditingPreset(PatrolRoute route)
        {
            if (route == null) return null;
            var p = route.GetPreset(PresetName);
            if (p == null && route.Presets.Count > 0) { p = route.Presets[0]; PresetName = p.Name; }
            return p;
        }

        /// <summary>Like <see cref="GetEditingPreset"/> but creates a fresh preset when the route has none.</summary>
        public static SpeedPreset EnsureEditingPreset(PatrolRoute route)
        {
            var p = GetEditingPreset(route);
            if (p == null)
            {
                p = new SpeedPreset { Name = UniquePresetName(route, "Speeds") };
                route.Presets.Add(p);
                PresetName = p.Name;
            }
            return p;
        }

        /// <summary>
        /// Fetch the route → resolve the editing preset → run <paramref name="mutate"/> → persist → refresh the
        /// preview. Returns false if the route no longer exists. Set <paramref name="ensurePreset"/> false when
        /// the edit shouldn't auto-create a preset (e.g. deleting the last one).
        /// </summary>
        public static bool Mutate(string routeName, Action<PatrolRoute, SpeedPreset> mutate, bool ensurePreset = true)
        {
            var route = PatrolRouteManager.GetRoute(routeName);
            if (route == null) return false;
            RouteName = routeName;
            var preset = ensurePreset ? EnsureEditingPreset(route) : GetEditingPreset(route);
            mutate?.Invoke(route, preset);
            PatrolRouteManager.SaveRouteFull(route);
            PatrolRouteVisualizer.RefreshSpeedPreview();
            return true;
        }

        /// <summary>A preset name unique on the route (case-insensitive), derived from <paramref name="basis"/>.</summary>
        public static string UniquePresetName(PatrolRoute route, string basis)
        {
            if (string.IsNullOrEmpty(basis)) basis = "Preset";
            if (route == null || route.GetPreset(basis) == null) return basis;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = basis + " " + i;
                if (route.GetPreset(candidate) == null) return candidate;
            }
            return basis + " " + Guid.NewGuid().ToString("N").Substring(0, 4);
        }
    }
}
