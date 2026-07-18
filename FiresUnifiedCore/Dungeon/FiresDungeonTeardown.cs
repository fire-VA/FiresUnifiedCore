using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// Server-authoritative, COMPLETE removal of a spawned Fires dungeon — the ground entrance and the dungeon in
    /// the air are linked exactly like vanilla's location pair, so removing the entrance tears down everything:
    ///
    ///   • the INTERIOR at +InteriorYOffset (rooms, portal, env box, memorials, player-built props) — both live
    ///     instantiated objects AND orphan ZDOs that aren't currently instantiated (the classic stale-ZDO trap:
    ///     a loaded-objects-only sweep leaves unloaded ZDOs behind to re-materialize later);
    ///   • the spec's SURFACE structures (location building / portal root), matched by ZDO PREFAB HASH within an
    ///     XZ radius of the entrance so neighbouring player builds are never touched;
    ///   • the ZoneSystem location-instance record for the zone (no ghost map/hunt entry left behind).
    ///
    /// Interior spatial filter: the dungeon's own env box bounds when present (grown boxes cover sprawl), else a
    /// generous fallback AABB; a hard Y floor guard makes it impossible to touch anything on the surface.
    /// </summary>
    public static class FiresDungeonTeardown
    {
        private const float DefaultInteriorYOffset = 5000f;
        private const float InteriorFloorGuard = 300f;   // interior sweep never reaches below interiorCenter - this
        private const int SectorSweepArea = 2;           // 5x5 zones around the entrance — covers env boxes grown past the zone edge

        /// <summary>Tear down the dungeon whose entrance sits at <paramref name="surfacePos"/>. Returns objects destroyed.</summary>
        public static int DestroyDungeonAt(DungeonSpec spec, Vector3 surfacePos)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return 0;
            var znet = ZNetScene.instance;
            var zman = ZDOMan.instance;
            if (znet == null || zman == null) return 0;

            float yOffset = spec != null && spec.InteriorYOffset > 0f ? spec.InteriorYOffset : DefaultInteriorYOffset;
            Vector2i zone = ZoneSystem.GetZone(surfacePos);
            Vector3 zoneCentre = ZoneSystem.GetZonePos(zone);
            Vector3 interiorCenter = new Vector3(zoneCentre.x, surfacePos.y + yOffset, zoneCentre.z);

            var box = FiresCore.Utilities.EnvironmentBoxController.GetEnvBoxAtPosition(interiorCenter);
            Bounds bounds = box != null ? box.GetWorldBounds() : new Bounds(interiorCenter, new Vector3(180f, 600f, 180f));
            bounds.Expand(12f);
            float interiorFloorY = interiorCenter.y - InteriorFloorGuard;

            var surfaceHashes = new HashSet<int>();
            string[] names = spec != null ? spec.SurfaceCleanupPrefabNames : null;
            if ((names == null || names.Length == 0) && spec != null && !string.IsNullOrEmpty(spec.CryptLocationPrefabName))
                names = new[] { spec.CryptLocationPrefabName };
            if (names != null)
                foreach (string n in names)
                    if (!string.IsNullOrEmpty(n)) surfaceHashes.Add(n.GetStableHashCode());
            float surfR = spec != null && spec.SurfaceCleanupRadius > 0f ? spec.SurfaceCleanupRadius : 32f;
            float surfRSqr = surfR * surfR;

            int destroyed = 0;

            // 1) live instantiated objects (interior volume + surface prefab set).
            foreach (var nv in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
            {
                if (nv == null || !nv.IsValid()) continue;
                if (!ShouldDestroy(nv.GetZDO(), nv.transform.position, bounds, interiorFloorY, surfaceHashes, surfacePos, surfRSqr)) continue;
                nv.ClaimOwnership();
                znet.Destroy(nv.gameObject);
                destroyed++;
            }

            // 2) orphan / not-currently-instantiated ZDOs in the surrounding sectors. ZNetScene.Destroy above resets
            // each destroyed object's ZDO, so nothing double-hits; FindInstance skips anything still live.
            var sectorZdos = new List<ZDO>();
            zman.FindSectorObjects(zone, SectorSweepArea, 0, sectorZdos);
            foreach (var z in sectorZdos)
            {
                if (z == null || !z.IsValid()) continue;
                if (znet.FindInstance(z) != null) continue;
                if (!ShouldDestroy(z, z.GetPosition(), bounds, interiorFloorY, surfaceHashes, surfacePos, surfRSqr)) continue;
                z.SetOwner(ZDOMan.GetSessionID());
                zman.DestroyZDO(z);
                destroyed++;
            }

            // 3) the zone's location-instance record.
            RemoveLocationInstance(spec, zone);

            Debug.Log($"{(spec != null ? spec.LogTag : "[FiresDungeon]")} teardown at {surfacePos}: destroyed {destroyed} object(s) " +
                      $"(interior bounds {bounds}; surface set {surfaceHashes.Count} prefab(s) within {surfR:0}m XZ).");
            return destroyed;
        }

        private static bool ShouldDestroy(ZDO z, Vector3 p, Bounds interior, float interiorFloorY,
            HashSet<int> surfaceHashes, Vector3 surfacePos, float surfRSqr)
        {
            // interior: everything inside the box bounds, hard-guarded so the sweep can never dip to the surface.
            if (p.y >= interiorFloorY && interior.Contains(p)) return true;

            // surface: ONLY the spec's own structures, by prefab hash, near the entrance. XZ distance — a portal
            // root can sit at the surface while its exit child rides ~5000m above it.
            if (z != null && surfaceHashes.Count > 0 && surfaceHashes.Contains(z.GetPrefab()))
            {
                float dx = p.x - surfacePos.x, dz = p.z - surfacePos.z;
                if (dx * dx + dz * dz <= surfRSqr) return true;
            }
            return false;
        }

        private static void RemoveLocationInstance(DungeonSpec spec, Vector2i zone)
        {
            try
            {
                if (spec == null || string.IsNullOrEmpty(spec.CryptLocationPrefabName)) return;
                var zs = ZoneSystem.instance;
                if (zs == null || zs.m_locationInstances == null) return;
                if (zs.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance inst)
                    && inst.m_location != null
                    && inst.m_location.m_prefabName == spec.CryptLocationPrefabName)
                {
                    zs.m_locationInstances.Remove(zone);
                    Debug.Log($"{spec.LogTag} removed location-instance record for zone {zone} ('{spec.CryptLocationPrefabName}').");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresDungeon] location-instance removal skipped: {ex.Message}");
            }
        }
    }
}
