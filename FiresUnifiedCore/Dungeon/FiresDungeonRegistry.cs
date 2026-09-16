using System;
using System.Collections.Generic;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// Process-wide registry of every <see cref="DungeonSpec"/> the engine drives, keyed by DG-name PREFIX. The
    /// four Harmony bodies in <see cref="FiresDungeonCore"/> live ONCE in Core and dispatch through here: each
    /// per-DG body asks <see cref="MatchForDg"/> which spec owns a given DungeonGenerator, and the DungeonDB /
    /// ZoneSystem bodies iterate <see cref="All"/>. This is what lets two dungeon mods coexist without fighting
    /// over the single static DungeonGenerator.m_availableRooms.
    /// </summary>
    public static class FiresDungeonRegistry
    {
        private static readonly List<DungeonSpec> _specs = new List<DungeonSpec>();

        public static IReadOnlyList<DungeonSpec> All => _specs;

        /// <summary>
        /// Register (or replace) a dungeon spec. Idempotent by <see cref="DungeonSpec.DgNamePrefix"/>: re-registering
        /// the same prefix swaps the spec in place (relogin / re-setup safe).
        /// </summary>
        public static void Register(DungeonSpec spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.DgNamePrefix)) return;
            int idx = _specs.FindIndex(s => string.Equals(s.DgNamePrefix, spec.DgNamePrefix, StringComparison.Ordinal));
            if (idx >= 0) _specs[idx] = spec;
            else _specs.Add(spec);

            // Index this spec's Location so a remote client can resolve it (LocationProxy → SpawnProxyLocation →
            // GetLocation). Specs built after world load (WorldStart on any peer, or an on-demand server generate) miss
            // the SetupLocations postfix, so register here too. No-op if ZoneSystem isn't up yet — the postfix covers it.
            FiresDungeonCore.EnsureLocationRegistered(spec);
        }

        public static void Unregister(string dgNamePrefix)
        {
            if (string.IsNullOrEmpty(dgNamePrefix)) return;
            _specs.RemoveAll(s => string.Equals(s.DgNamePrefix, dgNamePrefix, StringComparison.Ordinal));
        }

        /// <summary>The first ENABLED spec whose DG-name prefix matches the given DungeonGenerator name, or null.</summary>
        public static DungeonSpec MatchForDg(string dgGameObjectName)
        {
            foreach (var spec in _specs)
            {
                if (spec == null || !spec.IsEnabled()) continue;
                if (spec.MatchesDg(dgGameObjectName)) return spec;
            }
            return null;
        }

        /// <summary>The spec owning a given surface Location prefab name, or null.</summary>
        public static DungeonSpec MatchForLocation(string locationPrefabName)
        {
            if (string.IsNullOrEmpty(locationPrefabName)) return null;
            foreach (var spec in _specs)
            {
                if (spec == null || !spec.IsEnabled()) continue;
                if (string.Equals(spec.CryptLocationPrefabName, locationPrefabName, StringComparison.Ordinal)) return spec;
            }
            return null;
        }
    }
}
