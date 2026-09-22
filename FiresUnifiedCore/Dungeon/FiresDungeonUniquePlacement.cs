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
        public static bool RegisterPlacedInstance(DungeonSpec spec, Vector3 pos)
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
                existing.m_placed = true;
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
            _registerLocationMethod.Invoke(zoneSystem, new object[] { loc, pos, true });
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
                return $"pending: world-gen instance at {instPos} (zone {instZone}) will build when its zone first loads.";
            }

            // Nothing recorded, nothing built: this world's location generation predates the spec. Place it now,
            // once, through the same vanilla SpawnLocation pipeline, and record it so it stays the only one.
            if (!TryPickAutoSpot(spec, out Vector3 pos, out Quaternion rot, out string why))
                return $"auto-place failed: {why}";

            string spawn = FiresDungeonCore.SpawnDungeonAt(spec, pos, rot);
            bool spawned = spawn != null && spawn.StartsWith("dungeon spawned", StringComparison.Ordinal);
            if (!spawned) return $"auto-place spawn failed: {spawn}";

            bool recorded = RegisterPlacedInstance(spec, pos);
            Debug.LogWarning($"{spec.LogTag} ONE-PER-WORLD AUTO-PLACE: world predates this location (generation already " +
                             $"done, no instance ever recorded) — placed the dungeon at {pos} ({Vector3.Distance(pos, Vector3.zero):F0}m " +
                             $"from centre) and recorded the instance (ok={recorded}). {spawn}");
            return $"auto-placed at {pos}. {spawn}";
        }

        /// <summary>
        /// Pick a placement spot near the world centre from pure worldgen math (WorldGenerator height/biome — no
        /// zones need to be loaded, this runs on a headless dedi at boot). Candidates are ZONE CENTRES in the
        /// descriptor's centre ring, skipping any zone that already holds a location instance (StartTemple included),
        /// scored flattest-first. The entrance is rotated to face the world centre.
        /// </summary>
        private static bool TryPickAutoSpot(DungeonSpec spec, out Vector3 pos, out Quaternion rot, out string why)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            var worldGen = WorldGenerator.instance;
            var zoneSystem = ZoneSystem.instance;
            if (worldGen == null || zoneSystem == null) { why = "WorldGenerator/ZoneSystem not ready."; return false; }

            var d = spec.NearSpawnLocation;
            float minR = Mathf.Max(128f, d.MinDistanceFromCenter);           // never on top of the spawn temple
            float maxR = Mathf.Max(minR + 64f, d.MaxDistanceFromCenter);
            float water = zoneSystem.m_waterLevel;
            float minAlt = Mathf.Max(1f, d.MinAltitude);
            float pad = Mathf.Max(8f, d.ExteriorRadius);

            int zoneRange = Mathf.CeilToInt(maxR / 64f);
            float bestScore = float.MaxValue;
            Vector3 bestPos = Vector3.zero;

            for (int zx = -zoneRange; zx <= zoneRange; zx++)
            for (int zy = -zoneRange; zy <= zoneRange; zy++)
            {
                var zone = new Vector2s(zx, zy);
                Vector3 c = ZoneSystem.GetZonePos(zone);
                float dist = new Vector2(c.x, c.z).magnitude;
                if (dist < minR || dist > maxR) continue;
                if (zoneSystem.m_locationInstances.ContainsKey(zone)) continue;

                float h0 = worldGen.GetHeight(c.x, c.z);
                if (h0 - water < minAlt || h0 - water > 250f) continue;
                if (d.Biome != Heightmap.Biome.All && (worldGen.GetBiome(c.x, c.z) & d.Biome) == 0) continue;

                float hMin = h0, hMax = h0;
                for (int i = 0; i < 4; i++)
                {
                    float ox = (i % 2 == 0 ? pad : -pad), oz = (i < 2 ? pad : -pad);
                    float h = worldGen.GetHeight(c.x + ox, c.z + oz);
                    if (h < hMin) hMin = h;
                    if (h > hMax) hMax = h;
                }
                if (hMin - water < minAlt) continue;                          // a corner in the water = shoreline

                float slope = hMax - hMin;
                if (slope > 10f) continue;
                float score = slope + dist * 0.005f;                          // flattest wins, closer breaks ties
                if (score < bestScore) { bestScore = score; bestPos = new Vector3(c.x, h0, c.z); }
            }

            if (bestScore == float.MaxValue)
            {
                why = $"no flat dry zone centre in the {minR:F0}–{maxR:F0}m centre ring (checked altitude {minAlt}+, slope ≤10m).";
                return false;
            }

            pos = bestPos;
            Vector3 outward = new Vector3(bestPos.x, 0f, bestPos.z).normalized;
            rot = Quaternion.LookRotation(-outward, Vector3.up);              // entrance faces the world centre
            why = null;
            return true;
        }
    }
}
