using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// Server-side ONE-PER-WORLD enforcement for unique world-gen dungeons (DungeonSpec.NearSpawnLocation with
    /// Unique=true). Vanilla's own guarantee is fragile in exactly the ways that bite a modded location:
    ///
    ///  1. A world whose location generation already ran (every pre-existing world) has m_locationsGenerated=true,
    ///     so a location added later NEVER gets an instance — the dungeon simply never places.
    ///  2. ZoneSystem.Load resolves saved instances by hash via m_locationsByHash; one session with the mod absent
    ///     (or the bundle failing) silently DROPS our instance from the save (a DevLog nobody sees). The physical
    ///     ZDOs remain, and the next location-generation pass (vanilla bumps m_locationVersion on updates) counts
    ///     zero instances and places a SECOND dungeon.
    ///
    /// <see cref="ReconcileUnique"/> is the desired-state reconciler that closes both holes: the live world ZDOs
    /// (the caller's physical anchor scan) are ground truth, vanilla's m_locationInstances is the ledger that the
    /// unique/quantity gates read, and this makes the ledger match the world — healing dropped records, and
    /// auto-placing the dungeon once, near the world centre, when neither record nor structure exists.
    /// </summary>
    public static class FiresDungeonUniquePlacement
    {
        private static MethodInfo _registerLocationMethod;

        private static bool Matches(ZoneSystem.LocationInstance li, DungeonSpec spec)
        {
            return li.m_location != null && li.m_location.m_prefabName == spec.CryptLocationPrefabName;
        }

        /// <summary>Count this spec's entries in vanilla's m_locationInstances (the record the unique gate reads).</summary>
        public static int CountInstances(DungeonSpec spec, out int placed, out Vector3 firstPos, out Vector2s firstZone)
        {
            placed = 0; firstPos = Vector3.zero; firstZone = new Vector2s(0, 0);
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem?.m_locationInstances == null || spec == null) return 0;
            int total = 0;
            foreach (var kv in zoneSystem.m_locationInstances)
            {
                if (!Matches(kv.Value, spec)) continue;
                if (total == 0) { firstPos = kv.Value.m_position; firstZone = kv.Key; }
                total++;
                if (kv.Value.m_placed) placed++;
            }
            return total;
        }

        /// <summary>
        /// Record the dungeon as PLACED at <paramref name="pos"/> in vanilla's instance ledger, so every future
        /// generation pass counts it and the m_unique gate holds. Runs vanilla's private RegisterLocation (which
        /// also fills the ID/group caches), then flips m_placed on the struct. If the zone already holds OUR
        /// instance it is marked placed in place; a foreign location in the zone fails loudly (vanilla allows one
        /// instance per zone).
        /// </summary>
        public static bool RegisterPlacedInstance(DungeonSpec spec, Vector3 pos) => RegisterInstance(spec, pos, placed: true);

        /// <summary>Record an instance at <paramref name="pos"/>; placed=false leaves it PENDING, so vanilla builds
        /// the dungeon there the first time the zone populates (the world-gen path).</summary>
        public static bool RegisterInstance(DungeonSpec spec, Vector3 pos, bool placed)
        {
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null || spec == null) return false;

            FiresDungeonCore.EnsureLocationRegistered(spec);
            ZoneSystem.ZoneLocation loc =
                zoneSystem.m_locations?.Find(l => l != null && l.m_prefabName == spec.CryptLocationPrefabName);
            if (loc == null)
            {
                Debug.LogError($"{spec.LogTag} RegisterPlacedInstance: ZoneLocation missing (bundle not loaded?).");
                return false;
            }

            Vector2s zone = ZoneSystem.GetZone(pos);
            if (zoneSystem.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance existing))
            {
                if (!Matches(existing, spec))
                {
                    Debug.LogError($"{spec.LogTag} RegisterPlacedInstance: zone {zone} already holds location " +
                                   $"'{existing.m_location?.m_prefabName}' — cannot record ours there.");
                    return false;
                }
                existing.m_placed = placed || existing.m_placed;
                existing.m_position = pos;
                zoneSystem.m_locationInstances[zone] = existing;
                return true;
            }

            if (_registerLocationMethod == null)
                _registerLocationMethod = AccessTools.Method(typeof(ZoneSystem), "RegisterLocation");
            if (_registerLocationMethod == null)
            {
                Debug.LogError($"{spec.LogTag} RegisterPlacedInstance: ZoneSystem.RegisterLocation not found.");
                return false;
            }
            _registerLocationMethod.Invoke(zoneSystem, new object[] { loc, pos, placed });
            return zoneSystem.m_locationInstances.ContainsKey(zone);
        }

        /// <summary>
        /// Remove EVERY instance record of this spec's location (and its entries in the private ID/group caches).
        /// Used before re-anchoring a healed record and after a manual teardown, so no stale entry can either spawn
        /// a second dungeon when its zone builds or fool the unique gate.
        /// </summary>
        public static int RemoveInstances(DungeonSpec spec)
        {
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem?.m_locationInstances == null || spec == null) return 0;

            var doomed = new List<Vector2s>();
            foreach (var kv in zoneSystem.m_locationInstances)
                if (Matches(kv.Value, spec)) doomed.Add(kv.Key);
            foreach (var zone in doomed)
                zoneSystem.m_locationInstances.Remove(zone);

            if (doomed.Count > 0)
            {
                PurgeCache(zoneSystem, "m_locationIDCache", spec);
                PurgeCache(zoneSystem, "m_locationGroupCache", spec);
                PurgeCache(zoneSystem, "m_locationMaxGroupCache", spec);
            }
            return doomed.Count;
        }

        private static void PurgeCache(ZoneSystem zoneSystem, string fieldName, DungeonSpec spec)
        {
            try
            {
                var field = typeof(ZoneSystem).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                if (!(field?.GetValue(zoneSystem) is System.Collections.IDictionary dict)) return;
                foreach (var value in dict.Values)
                    (value as List<ZoneSystem.LocationInstance>)?.RemoveAll(li => Matches(li, spec));
            }
            catch (Exception ex) { Debug.LogWarning($"{spec.LogTag} cache purge ({fieldName}) failed: {ex.Message}"); }
        }

        /// <summary>
        /// The desired-state pass. <paramref name="physicalAnchor"/> is the caller's ground truth — the surface
        /// position of the dungeon's live ZDOs, or null when a whole-map scan found none. Decision matrix:
        /// physical + no record → heal the record; physical + record elsewhere → re-anchor the record; no physical +
        /// pending (unplaced) record → vanilla will build it, leave it; no physical + no record → auto-place once
        /// near the world centre per the spec's descriptor. Returns a human-readable status for logs/commands.
        /// </summary>
        public static string ReconcileUnique(DungeonSpec spec, Vector3? physicalAnchor)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return "server only.";
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null) return "pending: ZoneSystem not ready.";
            if (!zoneSystem.LocationsGenerated) return "pending: location generation still running.";
            if (spec?.NearSpawnLocation == null || !spec.NearSpawnLocation.Unique)
                return "spec is not a unique world-gen dungeon.";

            int total = CountInstances(spec, out int placed, out Vector3 instPos, out Vector2s instZone);

            if (physicalAnchor.HasValue)
            {
                Vector3 phys = physicalAnchor.Value;
                Vector2s physZone = ZoneSystem.GetZone(phys);

                if (total == 0)
                {
                    bool ok = RegisterPlacedInstance(spec, phys);
                    Debug.LogWarning($"{spec.LogTag} ONE-PER-WORLD HEAL: dungeon exists at {phys} but vanilla had NO " +
                                     $"instance record (dropped at a load while the mod/location was missing). " +
                                     $"Re-registered placed instance: {ok}. Without it the next location-generation " +
                                     "pass would have placed a SECOND dungeon.");
                    return ok ? $"healed: instance record restored at {phys}." : "heal FAILED (see log).";
                }

                bool anchored = zoneSystem.m_locationInstances.TryGetValue(physZone, out ZoneSystem.LocationInstance atPhys)
                                && Matches(atPhys, spec);
                if (anchored)
                {
                    if (!atPhys.m_placed)
                    {
                        atPhys.m_placed = true;
                        zoneSystem.m_locationInstances[physZone] = atPhys;
                    }
                    if (total > 1)
                    {
                        // extra records in OTHER zones are second-dungeon seeds — drop them, keep the real one.
                        var doomed = new List<Vector2s>();
                        foreach (var kv in zoneSystem.m_locationInstances)
                            if (Matches(kv.Value, spec) && !kv.Key.Equals(physZone)) doomed.Add(kv.Key);
                        foreach (var zone in doomed) zoneSystem.m_locationInstances.Remove(zone);
                        Debug.LogWarning($"{spec.LogTag} ONE-PER-WORLD: pruned {doomed.Count} stale extra instance record(s); " +
                                         $"kept the placed one at zone {physZone}.");
                    }
                    return $"healthy: one placed instance at {physZone} matches the world.";
                }

                RemoveInstances(spec);
                bool reok = RegisterPlacedInstance(spec, phys);
                Debug.LogWarning($"{spec.LogTag} ONE-PER-WORLD RE-ANCHOR: instance record was at {instPos} (zone {instZone}) " +
                                 $"but the dungeon physically sits at {phys} (zone {physZone}). Record moved: {reok}. " +
                                 "The stale record would have spawned a second dungeon when its zone built.");
                return reok ? $"re-anchored: record moved to {phys}." : "re-anchor FAILED (see log).";
            }

            // ── no physical dungeon anywhere on the map ──────────────────────────
            if (total > 0)
            {
                if (placed > 0)
                {
                    // The record claims placed but the world holds nothing — a rolled-back world or an external wipe.
                    // Do NOT auto-place over a possibly-wrong scan; surface it and let an admin decide.
                    Debug.LogWarning($"{spec.LogTag} ONE-PER-WORLD: instance record at {instPos} says PLACED but no live " +
                                     "structure was found on the map. Not auto-placing (would risk a duplicate if the " +
                                     "scan missed it). Verify, then use the manual spawn command after removing the record.");
                    return $"mismatch: placed record at {instPos} but no structure found — manual review.";
                }
                // Zone-mode world: the pending zone must be OPEN for vanilla to ever populate it. Opening is idempotent,
                // so a zone that could not open last boot (world not seeded yet, open refused) is simply asked again.
                // A PARKED zone is different: opening restores the original world's objects and marks it generated,
                // so vanilla would never build the dungeon there — drop that reservation and pick a fresh zone below.
                bool dropped = false;
                if (Bridge.FiresZoneModeBridge.Active && !Bridge.FiresZoneModeBridge.IsZoneOpen(instZone))
                {
                    if (Bridge.FiresZoneModeBridge.IsZoneParked(instZone))
                    {
                        int removed = RemoveInstances(spec);
                        Debug.LogWarning($"{spec.LogTag} ONE-PER-WORLD: the pending reservation in zone {instZone} sits in a PARKED " +
                                         $"zone (a converted world's explored land) — dropped {removed} record(s); picking a fresh zone.");
                        dropped = true;
                    }
                    else
                    {
                        if (!Bridge.FiresZoneModeBridge.OpenZoneForLocation(instZone, ZoneLabel(spec)))
                            return $"pending: own zone {instZone} reserved but not open yet — asking again.";
                        return $"pending: opened own zone {instZone}; the dungeon will build when that zone populates.";
                    }
                }
                if (!dropped)
                    return $"pending: world-gen instance at {instPos} (zone {instZone}) will build when its zone first loads.";
            }

            // Nothing recorded, nothing built: this world's location generation predates the spec.
            bool zoneMode = Bridge.FiresZoneModeBridge.Active;
            if (!TryPickAutoSpot(spec, zoneMode, out Vector3 pos, out Quaternion rot, out string why))
                return $"auto-place failed{(zoneMode ? " (zone-mode world)" : "")}: {why}";

            if (zoneMode)
            {
                // Its own zone: record a PENDING instance in a closed zone beside the open land and have zone mode open
                // it — the location-island path. Vanilla builds the dungeon on the zone's real ground once every peer
                // shows the new terrain, and the record keeps it the only one.
                Vector2s zone = ZoneSystem.GetZone(pos);
                if (!RegisterInstance(spec, pos, placed: false))
                    return $"auto-place failed: could not record the instance in zone {zone}.";
                bool opened = Bridge.FiresZoneModeBridge.OpenZoneForLocation(zone, ZoneLabel(spec));
                Debug.LogWarning($"{spec.LogTag} ONE-PER-WORLD OWN ZONE: zone-mode world — reserved zone {zone} for the dungeon " +
                                 $"at {pos} ({new Vector2(pos.x, pos.z).magnitude:F0}m from centre, beside the open land) and " +
                                 (opened ? "opened it; vanilla builds the dungeon when the zone populates."
                                         : "asked to open it (not open yet; asked again on the next pass)."));
                return opened
                    ? $"pending: opened own zone {zone} for the dungeon; it will build when that zone populates."
                    : $"pending: own zone {zone} reserved but not open yet — asking again.";
            }

            string spawn = FiresDungeonCore.SpawnDungeonAt(spec, pos, rot);
            bool spawned = spawn != null && spawn.StartsWith("dungeon spawned", StringComparison.Ordinal);
            if (!spawned) return $"auto-place spawn failed: {spawn}";

            bool recorded = RegisterPlacedInstance(spec, pos);
            Debug.LogWarning($"{spec.LogTag} ONE-PER-WORLD AUTO-PLACE: world predates this location (generation already " +
                             $"done, no instance ever recorded) — placed the dungeon at {pos} ({Vector3.Distance(pos, Vector3.zero):F0}m " +
                             $"from centre) and recorded the instance (ok={recorded}). {spawn}");
            return $"auto-placed at {pos}. {spawn}";
        }

        private static string ZoneLabel(DungeonSpec spec) => spec.CryptLocationPrefabName ?? "dungeon";

        private sealed class PickStats
        {
            public int Checked, Instance, Builds, Water, River, Slope, Biome, Open, Detached, Parked;
            public override string ToString() =>
                $"checked {Checked} zone(s): {Instance} already hold a location, {Builds} have player builds, " +
                $"{Water} water/shoreline, {River} river or lake, {Slope} too steep, {Biome} wrong biome" +
                (Open + Detached + Parked > 0 ? $", {Open} already open, {Detached} not beside the open land, {Parked} parked (explored land of a converted world)" : "");
        }

        /// <summary>
        /// Pick a placement spot from worldgen math alone (no zones need to be loaded — this runs on a headless dedi
        /// at boot). Candidates are ZONE CENTRES searched outward in rings (the descriptor's radius, then 2x, 3.5x,
        /// 5x) so a crowded or wet centre pushes the dungeon further out instead of failing; the first ring with a
        /// valid zone wins, flattest first, closer breaking ties. Skipped: zones holding any location instance (the
        /// spawn temple included) and, on normal worlds, zones with player builds (the dungeon and its ground
        /// leveler must never land on a base). On a zone-mode world only CLOSED zones beside the open land qualify
        /// and their ground comes from the world's own plan — the dungeon gets a zone of its own.
        /// </summary>
        private static bool TryPickAutoSpot(DungeonSpec spec, bool zoneMode, out Vector3 pos, out Quaternion rot, out string why)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            var worldGen = WorldGenerator.instance;
            var zoneSystem = ZoneSystem.instance;
            if (worldGen == null || zoneSystem == null) { why = "WorldGenerator/ZoneSystem not ready."; return false; }

            var d = spec.NearSpawnLocation;
            float minR = Mathf.Max(0f, d.MinDistanceFromCenter);
            float baseR = Mathf.Max(minR + 64f, d.MaxDistanceFromCenter);
            float[] rings = { baseR, baseR * 2f, baseR * 3.5f, baseR * 5f };
            var stats = new PickStats();
            float inner = minR;
            foreach (float outer in rings)
            {
                if (TryRing(spec, zoneMode, worldGen, zoneSystem, inner, outer, stats, out pos))
                {
                    Vector3 outward = new Vector3(pos.x, 0f, pos.z);
                    rot = outward.sqrMagnitude > 0.01f ? Quaternion.LookRotation(-outward.normalized, Vector3.up) : Quaternion.identity;
                    why = null;
                    return true;
                }
                inner = outer;
            }
            why = $"no spot within {rings[rings.Length - 1]:F0}m of the centre — {stats}.";
            return false;
        }

        private static bool TryRing(DungeonSpec spec, bool zoneMode, WorldGenerator worldGen, ZoneSystem zoneSystem,
            float inner, float outer, PickStats stats, out Vector3 best)
        {
            best = Vector3.zero;
            var d = spec.NearSpawnLocation;
            float water = zoneSystem.m_waterLevel;
            float minAlt = Mathf.Max(1f, d.MinAltitude);
            float pad = Mathf.Max(8f, d.ExteriorRadius);
            int range = Mathf.CeilToInt(outer / 64f);
            float bestScore = float.MaxValue;

            for (int zx = -range; zx <= range; zx++)
            for (int zy = -range; zy <= range; zy++)
            {
                var zone = new Vector2s(zx, zy);
                Vector3 c = ZoneSystem.GetZonePos(zone);
                float dist = new Vector2(c.x, c.z).magnitude;
                if (dist < inner || dist >= outer) continue;
                stats.Checked++;
                if (zoneSystem.m_locationInstances.ContainsKey(zone)) { stats.Instance++; continue; }

                if (zoneMode)
                {
                    if (Bridge.FiresZoneModeBridge.IsZoneOpen(zone)) { stats.Open++; continue; }
                    if (!Bridge.FiresZoneModeBridge.HasOpenEdgeNeighbour(zone)) { stats.Detached++; continue; }
                    if (Bridge.FiresZoneModeBridge.IsZoneParked(zone)) { stats.Parked++; continue; }
                }
                else
                {
                    if (d.Biome != Heightmap.Biome.All && (worldGen.GetBiome(c.x, c.z) & d.Biome) == 0) { stats.Biome++; continue; }
                    if (ZoneHasPlayerBuilds(zone)) { stats.Builds++; continue; }
                }

                if (!SampleHeight(worldGen, zoneMode, c.x, c.z, out float h0)) { stats.Water++; continue; }
                if (h0 - water < minAlt || h0 - water > 250f) { stats.Water++; continue; }
                float hMin = h0, hMax = h0;
                bool ok = true;
                for (int i = 0; i < 4 && ok; i++)
                {
                    float ox = (i % 2 == 0 ? pad : -pad), oz = (i < 2 ? pad : -pad);
                    if (!SampleHeight(worldGen, zoneMode, c.x + ox, c.z + oz, out float h)) { ok = false; break; }
                    if (h < hMin) hMin = h;
                    if (h > hMax) hMax = h;
                }
                if (!ok || hMin - water < minAlt) { stats.Water++; continue; }   // a corner in the water = shoreline
                float slope = hMax - hMin;
                if (slope > 10f) { stats.Slope++; continue; }
                if (TouchesGeneratedWater(c.x, c.z, pad)) { stats.River++; continue; }

                float score = slope + dist * 0.005f;
                if (score < bestScore) { bestScore = score; best = new Vector3(c.x, h0, c.z); }
            }
            return bestScore < float.MaxValue;
        }

        // Zone-mode worlds read a closed zone's ground from the world's own plan (what it becomes once opened); the
        // live WorldGenerator answer there is sea floor.
        private static bool SampleHeight(WorldGenerator worldGen, bool zoneMode, float x, float z, out float height)
        {
            if (zoneMode) return Bridge.FiresZoneModeBridge.TryPlanningHeight(x, z, out height);
            height = worldGen.GetHeight(x, z);
            return true;
        }

        // Generated rivers/lakes carve channels the height samples above can miss; test the centre, the four
        // footprint corners and the four edge midpoints.
        private static bool TouchesGeneratedWater(float cx, float cz, float pad)
        {
            for (int ix = -1; ix <= 1; ix++)
            for (int iz = -1; iz <= 1; iz++)
                if (Bridge.FiresZoneModeBridge.IsGeneratedWater(cx + ix * pad, cz + iz * pad)) return true;
            return false;
        }

        private static readonly List<ZDO> s_sectorScratch = new List<ZDO>();
        private static readonly List<ZDO> s_distantScratch = new List<ZDO>();

        // Any player-built object (a ZDO with a creator) in the zone.
        private static bool ZoneHasPlayerBuilds(Vector2s zone)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return false;
            s_sectorScratch.Clear();
            s_distantScratch.Clear();
            zdoMan.FindSectorObjects(zone, new SimulationDistance(0, 0), s_sectorScratch, s_distantScratch);
            foreach (var z in s_sectorScratch)
                if (z != null && z.GetLong(ZDOVars.s_creator, 0L) != 0L) return true;
            foreach (var z in s_distantScratch)
                if (z != null && z.GetLong(ZDOVars.s_creator, 0L) != 0L) return true;
            return false;
        }
    }
}
