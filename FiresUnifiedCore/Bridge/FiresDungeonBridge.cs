using System;
using UnityEngine;
using FiresCore.Dungeon;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Cross-mod entry point for the generic custom-dungeon engine (<see cref="FiresDungeonCore"/>). An owning mod
    /// builds a <see cref="DungeonSpec"/> (room/location prefab names, named theme, env-box config, optional
    /// near-spawn ZoneLocation, content hook) and registers it here; the engine's four Harmony patches (which live
    /// ONCE in Core) then drive it, dispatching by DG-name PREFIX so multiple dungeon mods never fight over the
    /// shared static DungeonGenerator.m_availableRooms. Mirrors the established FiresCore.Bridge.* contract shape;
    /// every wrapper is null-safe and never throws back into the caller.
    /// </summary>
    public static class FiresDungeonBridge
    {
        /// <summary>Register (or replace, by DG-name prefix) a dungeon spec with the Core engine. Idempotent.</summary>
        public static void RegisterDungeon(DungeonSpec spec)
        {
            try { FiresDungeonRegistry.Register(spec); }
            catch (Exception ex) { Warn(ex); }
        }

        public static void UnregisterDungeon(string dgNamePrefix)
        {
            try { FiresDungeonRegistry.Unregister(dgNamePrefix); }
            catch (Exception ex) { Warn(ex); }
        }

        /// <summary>
        /// Server-authoritative manual spawn of a registered dungeon at pos/rot (the SAME vanilla pipeline world-gen
        /// uses). Returns a short status string. Fires the spec's content hook on success.
        /// </summary>
        public static string SpawnDungeonAt(DungeonSpec spec, Vector3 pos, Quaternion rot)
        {
            try { return FiresDungeonCore.SpawnDungeonAt(spec, pos, rot); }
            catch (Exception ex) { Warn(ex); return "spawn failed: " + ex.Message; }
        }

        /// <summary>
        /// Server-authoritative COMPLETE teardown of the dungeon whose entrance sits at <paramref name="surfacePos"/> —
        /// interior (+5000 rooms/portal/env box/props, live AND orphan ZDOs), the spec's surface structures, and the
        /// zone's location-instance record. The ground entrance and the dungeon in the air are linked like vanilla.
        /// Returns objects destroyed (0 off the server / on failure).
        /// </summary>
        public static int DestroyDungeonAt(DungeonSpec spec, Vector3 surfacePos)
        {
            try { return FiresDungeonTeardown.DestroyDungeonAt(spec, surfacePos); }
            catch (Exception ex) { Warn(ex); return 0; }
        }

        /// <summary>Start the server-side coroutine host (stale-heal / deferred actions). No-op off the server.</summary>
        public static void EnsureService()
        {
            try { FiresDungeonService.EnsureInstance(); }
            catch (Exception ex) { Warn(ex); }
        }

        private static void Warn(Exception ex) => Debug.LogWarning($"[FiresCore] FiresDungeonBridge: {ex.Message}");
    }
}
