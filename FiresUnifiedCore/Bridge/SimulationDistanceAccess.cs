using System;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// The one place the family asks "how many zones out does the game simulate?".
    ///
    /// Valheim 1.0 deleted <c>ZoneSystem.m_activeArea</c> and <c>ZoneSystem.m_activeDistantArea</c>
    /// (two plain ints, always readable) and replaced them with a <see cref="SimulationDistance"/>
    /// struct that ZoneSystem keeps <b>private</b> and fills from
    /// <c>ZNet.instance.GetSyncedSimulationDistance()</c> - the value is now negotiated with the
    /// server rather than being a client constant.
    ///
    /// Two things make that awkward for callers:
    ///   * <c>ZNet.instance</c> is null before connect and after disconnect, while the old fields
    ///     were readable from the moment ZoneSystem existed. Four Fires mods read the active area
    ///     during startup and teardown, so each one would need its own null dance.
    ///   * ZoneSystem's own copy is private, so there is no way to ask the object that actually
    ///     uses the value.
    ///
    /// Hence one accessor with one documented fallback: <see cref="SimulationDistance.OriginalDistance"/>
    /// (2 near / 2 far), which is what vanilla itself uses as the baseline and what m_activeArea
    /// defaulted to.
    /// </summary>
    public static class SimulationDistanceAccess
    {
        /// <summary>The live negotiated distance, or vanilla's 2/2 baseline when ZNet is not up.</summary>
        public static SimulationDistance Current
        {
            get
            {
                var net = ZNet.instance;
                return net != null ? net.GetSyncedSimulationDistance() : SimulationDistance.OriginalDistance;
            }
        }

        /// <summary>Zone radius that is fully simulated. Replaces <c>ZoneSystem.m_activeArea</c>.</summary>
        public static int Near => Current.NearSimulationDistance;

        /// <summary>Extra zone radius kept as distant objects. Replaces <c>ZoneSystem.m_activeDistantArea</c>.</summary>
        public static int Far => Current.FarSimulationDistance;

        /// <summary>Near + far, i.e. the outermost zone ring the game touches at all.</summary>
        public static int Total => Current.TotalSimulationDistance;

        /// <summary>
        /// 1.0 changed zone ids from <c>Vector2i</c> to <c>Vector2s</c> (short components). Ported code
        /// that still holds a Vector2i zone id converts here rather than casting in a dozen places.
        /// </summary>
        public static Vector2s ToZoneId(Vector2i id) => new Vector2s(id.x, id.y);

        /// <summary>Inverse of <see cref="ToZoneId"/>, for code that still keys caches on Vector2i.</summary>
        public static Vector2i ToVector2i(Vector2s id) => new Vector2i(id.x, id.y);

        // ---- ZoneSystem's own copy -------------------------------------------------------------
        //
        // ZoneSystem keeps a PRIVATE SimulationDistance and refreshes it from ZNet in ApplySettings().
        // Writing it is how a mod extends how far zones LOAD without touching anything else - and in 1.0
        // that is now a genuinely separate axis, because ZNetScene.CreateDestroyObjects asks
        // ZNet.GetSyncedSimulationDistance() directly rather than reading ZoneSystem's copy. Bumping this
        // therefore streams more terrain and water WITHOUT waking the ZDO objects out there; pre-1.0 the
        // two shared m_activeArea and had to be un-shared by hand.
        //
        // Resolved lazily and never in a static ctor: a throwing type initializer would resurface as
        // TypeInitializationException on every later call and bury the real cause.
        private static AccessTools.FieldRef<ZoneSystem, SimulationDistance> _zoneField;
        private static bool _zoneResolved;
        private static bool _zoneWarned;

        private static AccessTools.FieldRef<ZoneSystem, SimulationDistance> ZoneField()
        {
            if (_zoneResolved) return _zoneField;
            _zoneResolved = true;
            try
            {
                _zoneField = AccessTools.FieldRefAccess<ZoneSystem, SimulationDistance>("m_simulationDistance");
            }
            catch (Exception ex)
            {
                _zoneField = null;
                if (!_zoneWarned)
                {
                    _zoneWarned = true;
                    Debug.LogWarning($"[SimulationDistanceAccess] ZoneSystem.m_simulationDistance did not resolve " +
                                     $"({ex.GetType().Name}: {ex.Message}). Zone-loading range is read-only this session.");
                }
            }
            return _zoneField;
        }

        /// <summary>ZoneSystem's live zone-loading range, or false when the field could not be resolved.</summary>
        public static bool TryGetZoneLoading(out SimulationDistance value)
        {
            var field = ZoneField();
            var zs = ZoneSystem.instance;
            if (field == null || zs == null) { value = Current; return false; }
            value = field(zs);
            return true;
        }

        /// <summary>Overwrite ZoneSystem's zone-loading range. ApplySettings() resets it, so re-apply.</summary>
        public static bool TrySetZoneLoading(SimulationDistance value)
        {
            var field = ZoneField();
            var zs = ZoneSystem.instance;
            if (field == null || zs == null) return false;
            field(zs) = value;
            return true;
        }

        /// <summary>
        /// ZoneSystem's CURRENT zone-loading near range - the value a mod's bump actually wrote, not the
        /// one ZNet negotiated. Read this (rather than <see cref="Near"/>) from anything whose geometry has
        /// to track a live bump, e.g. an ocean ring that starts where loaded zone water ends.
        /// </summary>
        public static int ZoneLoadingNear
        {
            get { SimulationDistance d; return TryGetZoneLoading(out d) ? d.NearSimulationDistance : Near; }
        }

        /// <summary>Copy of <paramref name="d"/> with a different near range; far and classic are kept.</summary>
        public static SimulationDistance WithNear(SimulationDistance d, int near)
            => new SimulationDistance(near, d.FarSimulationDistance, d.IsClassic);
    }
}
