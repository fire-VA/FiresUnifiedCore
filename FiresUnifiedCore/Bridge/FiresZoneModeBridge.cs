using System;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Contract for worlds that open one zone at a time (FiresAdminTerrain zone mode). FAT registers these hooks
    /// on the server when a zone-mode world loads; everything reads them through the null-safe wrappers, so with no
    /// zone mode present every call answers like a normal world. Lets Core place world content (e.g. a unique
    /// dungeon) in a zone of its own instead of sea floor, without any reference to FAT.
    /// </summary>
    public static class FiresZoneModeBridge
    {
        public sealed class Hooks
        {
            /// <summary>True on the authority of an active, seeded zone-mode world.</summary>
            public Func<bool> IsActive;
            public Func<Vector2s, bool> IsZoneOpen;
            /// <summary>True when an edge neighbour of the zone is open (a new zone there joins the playable land).</summary>
            public Func<Vector2s, bool> HasOpenEdgeNeighbour;
            /// <summary>Ground height of the world's own plan at a point, as it will be once its zone opens.</summary>
            public Func<float, float, float> PlanningHeight;
            /// <summary>Open a zone for a location already registered in it; vanilla builds the location once every
            /// peer shows the new terrain. Returns false when the zone could not be opened.</summary>
            public Func<Vector2s, string, bool> OpenZoneForLocation;
            /// <summary>True for a PARKED zone (a converted world's explored land awaiting restore): opening it
            /// brings the original objects back and marks it generated, so vanilla never builds a new location there.</summary>
            public Func<Vector2s, bool> IsZoneParked;
            /// <summary>True where the world's generated water (rivers, lakes and their carve) covers the point —
            /// water the planning heights alone do not show.</summary>
            public Func<float, float, bool> IsGeneratedWater;
        }

        private static Hooks _hooks;

        public static void Register(Hooks hooks) => _hooks = hooks;
        public static void Unregister() => _hooks = null;

        public static bool Active
        {
            get { try { return _hooks?.IsActive != null && _hooks.IsActive(); } catch (Exception ex) { Warn(ex); return false; } }
        }

        public static bool IsZoneOpen(Vector2s zone)
        {
            try { return _hooks?.IsZoneOpen == null || _hooks.IsZoneOpen(zone); } catch (Exception ex) { Warn(ex); return true; }
        }

        public static bool HasOpenEdgeNeighbour(Vector2s zone)
        {
            try { return _hooks?.HasOpenEdgeNeighbour != null && _hooks.HasOpenEdgeNeighbour(zone); } catch (Exception ex) { Warn(ex); return false; }
        }

        public static bool TryPlanningHeight(float x, float z, out float height)
        {
            height = 0f;
            try
            {
                if (_hooks?.PlanningHeight == null) return false;
                height = _hooks.PlanningHeight(x, z);
                return true;
            }
            catch (Exception ex) { Warn(ex); return false; }
        }

        public static bool OpenZoneForLocation(Vector2s zone, string label)
        {
            try { return _hooks?.OpenZoneForLocation != null && _hooks.OpenZoneForLocation(zone, label); } catch (Exception ex) { Warn(ex); return false; }
        }

        public static bool IsZoneParked(Vector2s zone)
        {
            try { return _hooks?.IsZoneParked != null && _hooks.IsZoneParked(zone); } catch (Exception ex) { Warn(ex); return false; }
        }

        /// <summary>Answers on any world FAT generates water for, zone mode or not; false with no hook.</summary>
        public static bool IsGeneratedWater(float x, float z)
        {
            try { return _hooks?.IsGeneratedWater != null && _hooks.IsGeneratedWater(x, z); } catch (Exception ex) { Warn(ex); return false; }
        }

        private static void Warn(Exception ex) => Debug.LogWarning($"[FiresCore] FiresZoneModeBridge: {ex.Message}");
    }
}
