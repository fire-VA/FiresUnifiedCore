using System;
using BepInEx;
using UnityEngine;
using FiresCore.Config;
using FiresCore.Lifecycle;
using FiresCore.Logging;
using FiresCore.Sync;

namespace FiresCore
{
    // Shared library plugin. Hosts the cross-cutting helpers every Fires-*
    // mod needs (NetworkObjectHelper, PlayerSpawnGate, ConfigSync, async
    // scheduling, banner/log primitives). Consuming mods reference
    // FiresUnifiedCore.dll via BepInDependency or source-link individual
    // files per FIRES_CORE_GAMEPLAN.md.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency("Azumatt.InfinityHammer", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.serversync", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.worldeditcommands", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.serverdevcommands", BepInDependency.DependencyFlags.SoftDependency)]
    public class FiresUnifiedCore : FiresMod<FiresUnifiedCore>
    {
        public const string PluginGUID = "com.Fire.FiresUnifiedCore";
        public const string PluginName = "FiresUnifiedCore";
        public const string PluginVersion = "0.1.0";

        public ConfigSync configSync;

        private static ILogHandler _originalLogHandler;

        // The NPC engine cluster (FiresCore.Npc.*) is now the LIVE engine: FiresRPGMaker (and
        // FiresCompanions) dropped their copies and drive it through the FiresCore.Bridge.* seams.
        // On a dedicated server, three client-only CompanionPatches nested classes must be skipped
        // during PatchAll or Mono's IL rewriter native-crashes importing their FejdStartup/EnemyHud
        // references; their server-side siblings stay active.
        protected override System.Collections.Generic.IReadOnlyCollection<string> DedicatedServerSkipPatchTypes =>
            new[]
            {
                "FiresCore.Npc.CompanionPatches+CompanionClientPatches_FejdStartup",
                "FiresCore.Npc.CompanionPatches+CompanionClientPatches_EnemyHud",
                "FiresCore.Npc.CompanionPatches+ArcheryTarget_OnProjectileHit_Patch",
            };

        protected override void Setup()
        {
            Debug.Log($"[{PluginName}] Awake() — version {PluginVersion}");

            InstallLogFilter();
            InitializeConfigAndSync();

            // Bind the Fires config window's own appearance (font/opacity/accent) into Core's config and
            // register it, so an "Appearance" section shows right in the window and restyles live.
            FiresCore.UI.FiresConfigUI.BindAppearance(Config);
        }

        protected override void TitleScene(bool isFirstBoot)
        {
        }

        protected override void WorldStart()
        {
            Debug.Log($"[{PluginName}] Loaded.");
        }

        protected override void Shutdown()
        {
            TryDisposeConfigManager();
            configSync = null;
        }

        private void InstallLogFilter()
        {
            try
            {
                if (_originalLogHandler != null) return;
                _originalLogHandler = Debug.unityLogger.logHandler;
                Debug.unityLogger.logHandler = new RateLimitedLogHandler(_originalLogHandler);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] Failed to install log filter: {ex.Message}");
            }
        }

        private void InitializeConfigAndSync()
        {
            ConfigManager.Instance.Initialize(Config);

            configSync = new ConfigSync(PluginGUID)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                MinimumRequiredVersion = PluginVersion,
            };
            configSync.lockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;
            configSync.AddLockingConfigEntry(ConfigManager.Instance.configServerAuthority);
            configSync.AddConfigEntry(ConfigManager.Instance.configVerboseLogging);

            // HeightmapOverride: built-in terrain height-clamp override (replaces the
            // Jotunn-dependent HeightmapUnlimited). Server-locked; toggle via the
            // [HeightmapOverride] Enabled key. The TerrainComp transpilers auto-activate
            // through Harmony.PatchAll and no-op while Enabled is false.
            FiresCore.Terrain.HeightmapOverrideConfig.Initialize(Config);
            FiresCore.Terrain.HeightmapOverrideConfig.BindToSync(configSync);

            // BalrondCompat: server-locked toggles for our neutralization patches against specific
            // BalrondAmazingNature behaviors. Patches auto-activate through Harmony.PatchAll and
            // each one self-gates on its config entry — default-on so a fresh install gets the
            // fixes (e.g. Mistlands locations stay in Mistlands, not spilling mist into DeepNorth).
            FiresCore.Compat.Balrond.BalrondCompatConfig.Initialize(Config);
            FiresCore.Compat.Balrond.BalrondCompatConfig.BindToSync(configSync);
        }

        private void TryDisposeConfigManager()
        {
            try { ConfigManager.Instance?.Dispose(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] ConfigManager.Dispose threw: {ex.Message}");
            }
        }
    }
}
