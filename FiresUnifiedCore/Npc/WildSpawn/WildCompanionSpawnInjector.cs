using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.WildSpawn
{
    /// <summary>
    /// Adds our wild companion to the persistent <c>_ZoneCtrl</c> prefab's
    /// <c>_SpawnList_base.m_spawners</c> the same way
    /// BalrondExtendedAnimals adds its monsters: a single <c>SpawnData</c>
    /// with all biomes flagged, registered during <c>ZNetScene.Awake</c>
    /// while the prefab is still pristine. ExpandWorldSpawns picks the
    /// entry up naturally on its first dump (<c>Manager.ToFile</c> writes
    /// <c>expand_spawns.yaml</c> only if the file is missing) and treats
    /// it as a first-class spawn from then on.
    ///
    /// We do NOT write our own YAML and we do NOT mutate
    /// ExpandWorldSpawns' state. Previous versions shipped a YAML exporter
    /// and a sibling SpawnSystemList holder; both are removed because they
    /// caused EWS to load a partial spawn list (vanilla Deer/Boar/etc.
    /// stripped), which made BalrondExtendedAnimals' setupMonster NRE on
    /// the second login per Valheim process when its
    /// <c>FindSpawnData(spawnSystemList, "Deer").Clone()</c> hit a null
    /// SpawnData. Cascade: Balrond NRE â†’ ItemManager NRE â†’ EWD NRE â†’
    /// ZDOMan.FilterZDO NRE â†’ world load failure â†’ respawn loop.
    ///
    /// MIGRATION FROM OLD VERSIONS
    /// ---------------------------
    /// The old <c>WildCompanionYamlExporter</c> wrote
    /// <c>expand_spawns_firescompanions.yaml</c>. We delete that file once
    /// at startup if it exists (it's ours, and leaving it behind keeps EWS
    /// applying our 8 separate biome entries on top of vanilla, doubling
    /// up on what this injector now adds). The user's
    /// <c>expand_spawns.yaml</c> we don't touch â€” that file is theirs to
    /// own. If it currently contains only our wild-companion entries
    /// (a side effect of EWS's first dump while the old injector had
    /// already corrupted the live state), deleting it manually lets EWS
    /// re-dump a clean baseline on next launch.
    /// </summary>
    [HarmonyPatch]
    public static class WildCompanionSpawnInjector
    {
        private const string BaseListName = "_SpawnList_base";
        private const string LegacyYamlFileName = "expand_spawns_firescompanions.yaml";
        private static bool _legacyYamlCleanupAttempted;

        /// <summary>
        /// Postfix on ZNetScene.Awake â€” runs AFTER Balrond's Prefix, so
        /// the spawn list it reads from already contains every vanilla
        /// SpawnData Balrond's setupMonster needs to clone from. We just
        /// append one entry alongside theirs.
        /// </summary>
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        [HarmonyPostfix]
        [HarmonyAfter("balrond.astafaraios.BalrondMonsterMayhem", "balrond.MonsterMayhem")]
        public static void ZNetScene_Awake_Postfix_InjectWildCompanion()
        {
            try
            {
                CleanupLegacyYamlOnce();

                var zs = ZoneSystem.instance;
                if (zs == null) return;
                var zoneCtrlPrefab = zs.m_zoneCtrlPrefab;
                if (zoneCtrlPrefab == null) return;

                var spawnSystem = zoneCtrlPrefab.GetComponent<SpawnSystem>()
                                  ?? zoneCtrlPrefab.GetComponentInChildren<SpawnSystem>(true);
                if (spawnSystem?.m_spawnLists == null) return;

                SpawnSystemList baseList = null;
                foreach (var list in spawnSystem.m_spawnLists)
                {
                    if (list == null) continue;
                    if (list.gameObject != null && list.gameObject.name == BaseListName)
                    {
                        baseList = list;
                        break;
                    }
                }
                if (baseList == null) baseList = spawnSystem.m_spawnLists[0];
                if (baseList?.m_spawners == null) return;

                var wildPrefab = ZNetScene.instance?.GetPrefab(WildCompanionPrefabs.WildPrefabName);
                if (wildPrefab == null) return;

                bool already = baseList.m_spawners.Find(sd =>
                    sd != null && sd.m_prefab != null &&
                    sd.m_prefab.name == WildCompanionPrefabs.WildPrefabName) != null;
                if (already) return;

                var biomes = Heightmap.Biome.Meadows
                           | Heightmap.Biome.BlackForest
                           | Heightmap.Biome.Swamp
                           | Heightmap.Biome.Mountain
                           | Heightmap.Biome.Plains
                           | Heightmap.Biome.Mistlands
                           | Heightmap.Biome.AshLands
                           | Heightmap.Biome.DeepNorth;

                baseList.m_spawners.Add(new SpawnSystem.SpawnData
                {
                    m_name              = "wild companions",
                    m_enabled           = true,
                    m_prefab            = wildPrefab,
                    m_biome             = biomes,
                    m_biomeArea         = Heightmap.BiomeArea.Everything,
                    m_spawnInterval     = 600f,
                    m_spawnChance       = 50f,
                    m_maxSpawned        = 2,
                    m_spawnDistance     = 40f,
                    m_groupSizeMin      = 1,
                    m_groupSizeMax      = 4,
                    m_groupRadius       = 6f,
                    m_spawnAtDay        = true,
                    m_spawnAtNight      = true,
                    m_minTilt           = 0f,
                    m_maxTilt           = 35f,
                    m_minAltitude       = 0f,
                    m_maxAltitude       = 1000f,
                    m_groundOffset      = 0.5f,
                    m_groundOffsetRandom= 0f,
                    m_canSpawnCloseToPlayer = true,
                    m_insidePlayerBase      = false,
                    m_huntPlayer        = false,
                    m_minLevel          = 1,
                    m_maxLevel          = 1,
                    m_levelUpMinCenterDistance = 0f,
                });

                Debug.Log($"[WildCompanionSpawnInjector] Registered '{WildCompanionPrefabs.WildPrefabName}' " +
                          $"in _SpawnList_base alongside vanilla and Balrond entries.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WildCompanionSpawnInjector] ZNetScene.Awake postfix failed: {ex.Message}");
            }
        }

        private static void CleanupLegacyYamlOnce()
        {
            if (_legacyYamlCleanupAttempted) return;
            _legacyYamlCleanupAttempted = true;
            try
            {
                string ewDir = Path.Combine(BepInEx.Paths.ConfigPath, "expand_world");
                if (!Directory.Exists(ewDir)) return;
                string legacyPath = Path.Combine(ewDir, LegacyYamlFileName);
                if (!File.Exists(legacyPath)) return;
                File.Delete(legacyPath);
                Debug.Log($"[WildCompanionSpawnInjector] Deleted legacy YAML '{LegacyYamlFileName}' " +
                          $"(written by older versions; superseded by direct prefab injection).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WildCompanionSpawnInjector] Legacy YAML cleanup failed: {ex.Message}");
            }
        }
    }
}
