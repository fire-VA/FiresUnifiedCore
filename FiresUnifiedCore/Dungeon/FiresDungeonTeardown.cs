using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// Server-side removal of a whole Fires dungeon, since the entrance and the interior above are linked like
    /// vanilla's location pair: the interior's live objects and unloaded ZDOs (so nothing re-materializes later), the
    /// spec's surface structures matched by prefab hash within a radius of the entrance, and the zone's location
    /// record. The interior is bounded by its environment box or a fallback volume, with a hard floor that keeps the
    /// sweep off the surface.
    /// </summary>
    public static class FiresDungeonTeardown
    {
        private const float DefaultInteriorYOffset = 5000f;
        private const float InteriorFloorGuard = 300f;   // interior sweep never reaches below interiorCenter - this
        private const int SectorSweepArea = 2;           // 5x5 zones around the entrance — covers env boxes grown past the zone edge
        private const float FallbackInteriorWidth = 180f;
        private const float FallbackInteriorHeight = 600f;
        private const float InteriorBoundsPadding = 12f;
        private const float DefaultSurfaceCleanupRadius = 32f;

        /// <summary>Tear down the dungeon whose entrance sits at <paramref name="surfacePos"/>. Returns objects destroyed.</summary>
        public static int DestroyDungeonAt(DungeonSpec spec, Vector3 surfacePos)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return 0;
            var znet = ZNetScene.instance;
            var zman = ZDOMan.instance;
            if (znet == null || zman == null) return 0;

            float yOffset = spec != null && spec.InteriorYOffset > 0f ? spec.InteriorYOffset : DefaultInteriorYOffset;
            Vector2s zone = ZoneSystem.GetZone(surfacePos);
            Vector3 zoneCentre = ZoneSystem.GetZonePos(zone);
            Vector3 interiorCenter = new Vector3(zoneCentre.x, surfacePos.y + yOffset, zoneCentre.z);

            var box = FiresCore.Utilities.EnvironmentBoxController.GetEnvBoxAtPosition(interiorCenter);
            Bounds bounds = box != null ? box.GetWorldBounds() : new Bounds(interiorCenter, new Vector3(FallbackInteriorWidth, FallbackInteriorHeight, FallbackInteriorWidth));
            bounds.Expand(InteriorBoundsPadding);
            float interiorFloorY = interiorCenter.y - InteriorFloorGuard;

            var surfaceHashes = new HashSet<int>();
            string[] names = spec != null ? spec.SurfaceCleanupPrefabNames : null;
            if ((names == null || names.Length == 0) && spec != null && !string.IsNullOrEmpty(spec.CryptLocationPrefabName))
                names = new[] { spec.CryptLocationPrefabName };
            if (names != null)
                foreach (string prefabName in names)
                    if (!string.IsNullOrEmpty(prefabName)) surfaceHashes.Add(prefabName.GetStableHashCode());
            float surfR = spec != null && spec.SurfaceCleanupRadius > 0f ? spec.SurfaceCleanupRadius : DefaultSurfaceCleanupRadius;
            float surfRSqr = surfR * surfR;

            int destroyed = 0;

            // 1) live instantiated objects (interior volume + surface prefab set).
            foreach (var netView in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None))
            {
                if (netView == null || !netView.IsValid()) continue;
                if (!ShouldDestroy(netView.GetZDO(), netView.transform.position, bounds, interiorFloorY, surfaceHashes, surfacePos, surfRSqr)) continue;
                netView.ClaimOwnership();
                znet.Destroy(netView.gameObject);
                destroyed++;
            }

            // 2) orphan / not-currently-instantiated ZDOs in the surrounding sectors. ZNetScene.Destroy above resets
            // each destroyed object's ZDO, so nothing double-hits; FindInstance skips anything still live.
            var sectorZdos = new List<ZDO>();
            zman.FindSectorObjects(zone, new SimulationDistance(SectorSweepArea, 0), sectorZdos);
            foreach (var zdo in sectorZdos)
            {
                if (zdo == null || !zdo.IsValid()) continue;
                if (znet.FindInstance(zdo) != null) continue;
                if (!ShouldDestroy(zdo, zdo.GetPosition(), bounds, interiorFloorY, surfaceHashes, surfacePos, surfRSqr)) continue;
                zdo.SetOwner(ZDOMan.GetSessionID());
                zman.DestroyZDO(zdo);
                destroyed++;
            }

            // 3) the zone's location-instance record.
            RemoveLocationInstance(spec, zone);

            Debug.Log($"{(spec != null ? spec.LogTag : "[FiresDungeon]")} teardown at {surfacePos}: destroyed {destroyed} object(s) " +
                      $"(interior bounds {bounds}; surface set {surfaceHashes.Count} prefab(s) within {surfR:0}m XZ).");
            return destroyed;
        }

        private static bool ShouldDestroy(ZDO zdo, Vector3 position, Bounds interior, float interiorFloorY,
            HashSet<int> surfaceHashes, Vector3 surfacePos, float surfRSqr)
        {
            // interior: everything inside the box bounds, hard-guarded so the sweep can never dip to the surface.
            if (position.y >= interiorFloorY && interior.Contains(position)) return true;

            // surface: ONLY the spec's own structures, by prefab hash, near the entrance. XZ distance — a portal
            // root can sit at the surface while its exit child rides ~5000m above it.
            if (zdo != null && surfaceHashes.Count > 0 && surfaceHashes.Contains(zdo.GetPrefab()))
            {
                float dx = position.x - surfacePos.x, dz = position.z - surfacePos.z;
                if (dx * dx + dz * dz <= surfRSqr) return true;
            }
            return false;
        }

        private static void RemoveLocationInstance(DungeonSpec spec, Vector2s zone)
        {
            try
            {
                if (spec == null || string.IsNullOrEmpty(spec.CryptLocationPrefabName)) return;
                var zoneSystem = ZoneSystem.instance;
                if (zoneSystem == null || zoneSystem.m_locationInstances == null) return;
                if (zoneSystem.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance inst)
                    && inst.m_location != null
                    && inst.m_location.m_prefabName == spec.CryptLocationPrefabName)
                {
                    zoneSystem.m_locationInstances.Remove(zone);
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
