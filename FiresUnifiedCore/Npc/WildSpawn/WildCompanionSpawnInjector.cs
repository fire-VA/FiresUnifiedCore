using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.WildSpawn
{
    /// <summary>
    /// Adds the CompanionNpc_Wild prefab to world spawns without replacing anyone else's list. Without
    /// ExpandWorldSpawns it appends one all-biome SpawnData to _ZoneCtrl's base spawn list at ZNetScene.Awake.
    /// With EWS, which overwrites every zone's spawners from expand_spawns*.yaml, it writes a sidecar
    /// expand_spawns_firescompanions.yaml once if absent. The user's expand_spawns.yaml is never regenerated:
    /// an old exporter that did so dropped vanilla entries and broke BalrondExtendedAnimals at world load.
    /// </summary>
    [HarmonyPatch]
    public static class WildCompanionSpawnInjector
    {
        private const string BaseListName = "_SpawnList_base";
        private const string SidecarYamlFileName = "expand_spawns_firescompanions.yaml";
        private static bool _sidecarWriteAttempted;

        /// <summary>
        /// Postfix on ZNetScene.Awake — runs AFTER Balrond's Prefix, so
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
                EnsureSidecarYamlOnce();

                var zoneSystem = ZoneSystem.instance;
                if (zoneSystem == null) return;
                var zoneCtrlPrefab = zoneSystem.m_zoneCtrlPrefab;
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

        /// <summary>
        /// Writes the additive ExpandWorldSpawns sidecar
        /// <c>expand_spawns_firescompanions.yaml</c> once per process, but only
        /// when the <c>expand_world</c> config dir exists (EWS installed) and the
        /// file is absent. EWS merges every <c>expand_spawns*.yaml</c>, so this
        /// adds our single <c>CompanionNpc_Wild</c> entry without rewriting the
        /// user's main spawn table. Skipped if the file already exists so user
        /// retuning sticks and we never clobber edits.
        /// </summary>
        private static void EnsureSidecarYamlOnce()
        {
            if (_sidecarWriteAttempted) return;
            _sidecarWriteAttempted = true;
            try
            {
                string expandWorldDir = Path.Combine(BepInEx.Paths.ConfigPath, "expand_world");
                if (!Directory.Exists(expandWorldDir)) return; // EWS not installed - prefab injection below covers spawning.

                string path = Path.Combine(expandWorldDir, SidecarYamlFileName);
                if (File.Exists(path)) return;        // already present - respect user edits, never clobber.

                File.WriteAllText(path, BuildSidecarYaml());
                Debug.Log($"[WildCompanionSpawnInjector] Wrote ExpandWorldSpawns sidecar '{SidecarYamlFileName}' " +
                          $"(adds '{WildCompanionPrefabs.WildPrefabName}' to the spawn table; user's expand_spawns.yaml untouched).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WildCompanionSpawnInjector] Sidecar YAML write failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The one EW Spawns entry, ASCII-only. Field names match ExpandWorld's
        /// Spawn loader; values mirror the <c>SpawnData</c> appended to the prefab
        /// list above so both ingestion paths produce identical spawns.
        /// </summary>
        private static string BuildSidecarYaml()
        {
            return
                "# Fires wild companions - ExpandWorldSpawns sidecar (auto-written by FiresUnifiedCore).\n" +
                "# ExpandWorld merges every expand_spawns*.yaml; this ADDS one entry without touching\n" +
                "# your main expand_spawns.yaml. Delete this file to stop EWS-driven wild spawns, or edit\n" +
                "# the values below to retune density - it is NOT regenerated while it exists. Per-spawn\n" +
                "# faction / gear / stars / recruit-price are rolled at spawn time by biome.\n" +
                "- prefab: " + WildCompanionPrefabs.WildPrefabName + "\n" +
                "  enabled: true\n" +
                "  name: wild companions\n" +
                "  biome: Meadows, BlackForest, Swamp, Mountain, Plains, Mistlands, AshLands, DeepNorth\n" +
                "  spawnChance: 50\n" +
                "  maxSpawned: 2\n" +
                "  spawnInterval: 600\n" +
                "  minLevel: 1\n" +
                "  maxLevel: 1\n" +
                "  minAltitude: 0\n" +
                "  maxAltitude: 1000\n" +
                "  spawnDistance: 40\n" +
                "  groupSizeMin: 1\n" +
                "  groupSizeMax: 4\n" +
                "  groupRadius: 6\n" +
                "  minTilt: 0\n" +
                "  maxTilt: 35\n" +
                "  spawnAtDay: true\n" +
                "  spawnAtNight: true\n" +
                "  canSpawnCloseToPlayer: true\n";
        }
    }
}
