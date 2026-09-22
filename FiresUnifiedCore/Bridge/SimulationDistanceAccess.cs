using System;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// How many zones out the game simulates. Valheim 1.0 replaced ZoneSystem.m_activeArea and
    /// m_activeDistantArea with a private, server-negotiated SimulationDistance, and ZNet.instance is null before
    /// connecting and after leaving, when several Fires mods still read it. This is the single accessor, falling
    /// back to <see cref="SimulationDistance.OriginalDistance"/>, vanilla's own baseline.
    /// </summary>
    public static class SimulationDistanceAccess
    {
        /// <summary>The live negotiated distance, or vanilla's 2/2 baseline when ZNet is not up.</summary>
        public static SimulationDistance Current
        {
            get
            {
                var net = ZNet.instance;
                if (net == null) return SimulationDistance.OriginalDistance;
                try
                {
                    return net.GetSyncedSimulationDistance();
                }
                catch (NullReferenceException)
                {
                    // ZNet outlives its own dependencies on the way down. GetSyncedSimulationDistance
                    // calls the private GetDesiredSimulationDistance, which reads
                    // GraphicsSettingsManager.Instance.ActiveSettings - a MonoBehaviour singleton that is
                    // already destroyed while ZNet.instance still answers. A null check on ZNet cannot see
                    // that, so the baseline promised above has to be honoured here too.
                    //
                    // This is the teardown path: anything restoring vanilla state from OnDisable/OnDestroy
                    // (ocean ring extenders, zone-loading bumps) reads this on the way out, and a throw
                    // there aborts the restore half-done and leaves the setting bumped.
                    if (!_teardownWarned)
                    {
                        _teardownWarned = true;
                        Debug.Log("[SimulationDistanceAccess] simulation distance read while the game was tearing "
                                  + "down (ZNet up, GraphicsSettingsManager gone) - returning vanilla's baseline. "
                                  + "Harmless during logout; logged once per session.");
                    }
                    return SimulationDistance.OriginalDistance;
                }
            }
        }

        private static bool _teardownWarned;

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
            var zoneSystem = ZoneSystem.instance;
            if (field == null || zoneSystem == null) { value = Current; return false; }
            value = field(zoneSystem);
            return true;
        }

        /// <summary>Overwrite ZoneSystem's zone-loading range. ApplySettings() resets it, so re-apply.</summary>
        public static bool TrySetZoneLoading(SimulationDistance value)
        {
            var field = ZoneField();
            var zoneSystem = ZoneSystem.instance;
            if (field == null || zoneSystem == null) return false;
            field(zoneSystem) = value;
            return true;
        }

        /// <summary>
        /// ZoneSystem's CURRENT zone-loading near range - the value a mod's bump actually wrote, not the
        /// one ZNet negotiated. Read this (rather than <see cref="Near"/>) from anything whose geometry has
        /// to track a live bump, e.g. an ocean ring that starts where loaded zone water ends.
        /// </summary>
        public static int ZoneLoadingNear
        {
            get { SimulationDistance zoneLoading; return TryGetZoneLoading(out zoneLoading) ? zoneLoading.NearSimulationDistance : Near; }
        }

        /// <summary>Copy of <paramref name="d"/> with a different near range; far and classic are kept.</summary>
        public static SimulationDistance WithNear(SimulationDistance d, int near)
            => new SimulationDistance(near, d.FarSimulationDistance, d.IsClassic);
    }
}
