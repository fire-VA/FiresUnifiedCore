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

        // ---- 0.2.1094 ([seasons]' drop, method from FDM's DungeonLink; Fire 11-02: removing an entrance removes its dungeon, never a neighbour).
        // Add-only and inert until a mod calls Stamped: every ZDO made inside a scope carries the dungeon's id, so teardown needs no volume and nothing loaded.
        public const string KeyId = "fires_dungeon_id";
        private const string KeyIdFdm = "fdm_dungeon_id";   // dungeons FDM generated keep working
        private static readonly int s_hashId = KeyId.GetStableHashCode(), s_hashIdFdm = KeyIdFdm.GetStableHashCode();
        private static long s_scope;
        internal static int ZdosCreated;   // 0.2.1095: FiresDungeonCore.InvokeSpawnLocation tells a Full spawn from a refused one by this

        public static long NewId()
        {
            long id = ((long)UnityEngine.Random.Range(int.MinValue, int.MaxValue) << 32) ^ DateTime.UtcNow.Ticks;
            return id == 0L ? 1L : id;
        }

        /// <summary>Runs a synchronous spawn; every ZDO it creates carries <paramref name="id"/>.</summary>
        public static T Stamped<T>(long id, Func<T> spawn)
        {
            long was = s_scope;
            s_scope = id;
            try { return spawn(); }
            finally { s_scope = was; }
        }

        /// <summary>Server only: destroys every ZDO carrying the id, loaded or not, except <paramref name="keep"/>. Returns the count.</summary>
        public static int DestroyById(long id, ZDOID keep)
        {
            if (id == 0L || ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return 0;
            var all = HarmonyLib.AccessTools.Field(typeof(ZDOMan), "m_objectsByID")?.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
            if (all == null) { Debug.LogWarning("[FiresDungeon] DestroyById: ZDOs not readable; nothing removed"); return 0; }
            var doomed = new List<ZDO>();
            foreach (var z in all.Values)
                if (z != null && z.m_uid != keep && (z.GetLong(s_hashId, 0L) == id || z.GetLong(s_hashIdFdm, 0L) == id)) doomed.Add(z);
            var scene = ZNetScene.instance;
            foreach (var z in doomed)
            {
                ZNetView live = scene != null ? scene.FindInstance(z) : null;
                z.SetOwner(ZDOMan.GetSessionID());
                if (live != null) scene.Destroy(live.gameObject);
                else ZDOMan.instance.DestroyZDO(z);
            }
            FiresCore.Logging.FiresLogger.LogDebug($"[FiresDungeon] dungeon {id} torn down by id: {doomed.Count} object(s)");
            return doomed.Count;
        }

        [HarmonyLib.HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.CreateNewZDO), new[] { typeof(Vector3), typeof(int) })]
        private static class ZDOMan_CreateNewZDO_DungeonStamp
        {
            private static void Postfix(ZDO __result)
            {
                ZdosCreated++;
                if (s_scope != 0L && __result != null) __result.Set(s_hashId, s_scope);
            }
        }

        // Stale-heal and resize regenerate later from a coroutine: a generator whose own ZDO has an id stamps what it generates, whenever it runs.
        [HarmonyLib.HarmonyPatch(typeof(DungeonGenerator), nameof(DungeonGenerator.Generate), new[] { typeof(int), typeof(ZoneSystem.SpawnMode) })]
        private static class DungeonGenerator_Generate_DungeonStamp
        {
            private static void Prefix(DungeonGenerator __instance, out long __state)
            {
                __state = s_scope;
                var view = __instance.GetComponent<ZNetView>();
                var zdo = view != null ? view.GetZDO() : null;
                long id = zdo != null ? zdo.GetLong(s_hashId, 0L) : 0L;
                if (id == 0L && zdo != null) id = zdo.GetLong(s_hashIdFdm, 0L);   // a dungeon FDM generated: its regenerated rooms get the same id
                if (id != 0L) s_scope = id;
            }

            private static Exception Finalizer(Exception __exception, long __state)
            {
                s_scope = __state;
                return __exception;
            }
        }
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
                    FiresCore.Logging.FiresLogger.LogDebug($"{spec.LogTag} removed location-instance record for zone {zone} ('{spec.CryptLocationPrefabName}').");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresDungeon] location-instance removal skipped: {ex.Message}");
            }
        }
    }
}
