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
        public const string PluginVersion = "0.2.80";

        // Core's BepInEx log source. The shared LoadSummary banner emitter routes
        // through this (not Debug.Log) so banner lines don't also stdout-echo a raw
        // white duplicate in the console next to BepInEx's formatted line.
        public static BepInEx.Logging.ManualLogSource Log;

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
                // Shared input-block: Minimap/InventoryGui Update gates are client-only.
                "FiresCore.UI.InputBlockClientGates",
                // Text-capture gate: the ZInput hotkey suppressors + the
                // PlayerController.TakeInput movement/jump gate are client-only
                // (patching them on a headless build native-crashes the IL rewriter).
                "FiresCore.Input.FiresInputBlockClientGates",
                // Connection-reject panel patches FejdStartup.ShowConnectError (client-only menu);
                // same IL-rewriter crash class as the other FejdStartup patches above.
                "FiresCore.Bridge.FiresConnectReasonPanel",
                // Env-box config popup: Hud.Update Esc/cursor driver + logout guard touch client-only UI. Skip headless.
                "FiresCore.UI.EnvironmentBoxPanel+Hud_Update_Patch",
                "FiresCore.UI.EnvironmentBoxPanel+ZNet_Shutdown_PanelGuard",
                // Config-window menu entry: clones the FejdStartup/Menu Settings button, both client-only.
                "FiresCore.UI.ConfigMenuButton+FejdStartup_Start_AddEntry",
                "FiresCore.UI.ConfigMenuButton+Menu_Start_AddEntry",
                "FiresCore.UI.ConfigMenuButton+Menu_UpdateNavigation_AddEntry",
            };

        protected override void Setup()
        {
            Log = Logger;
            FiresCore.Logging.FiresLogColorPatch.TakeOwnership();
            FiresCoreBanner.PrintBig();
            Debug.Log($"[{PluginName}] Awake() - version {PluginVersion}");

            // Consolidate + group the whole Fires-family config folder before anything reads it this session.
            FiresCore.Storage.FiresConfigPaths.Migrate();

            FiresCore.Logging.StatusBanner.BindConfig(Config);
            FiresCore.Logging.BepInExLogSuppressionPatch.BindConfig(Config);
            FiresCore.Logging.LoadSummary.BindConfig(Config);
            FiresCore.Logging.RateLimitedLogHandler.BindConfig(Config);
            FiresCore.IO.FileWatchHubConfig.Initialize(Harmony, Config);
            FiresCore.Lifecycle.CollisionCallbackReuse.Initialize(Config);
            FiresCore.Lifecycle.GroundDataThrottle.Initialize(Config);
            InstallLogFilter();
            InitializeConfigAndSync();

            // The Diagnostics guards attach EXPLICITLY with read-back verification - the
            // attribute-based versions compiled into 0.1.60-0.1.65 but never attached in the
            // field (EnemyHud.TestShow NRE persisted with zero guard log lines). The client
            // gate mirrors the DedicatedServerSkipPatchTypes rationale: patching the
            // client-only EnemyHud type on a headless server native-crashes the IL rewriter;
            // the Character.OnDestroy finalizer + s_characters sweep stay active on the dedi.
            bool guardsClientSide = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
            FiresCore.Diagnostics.CharacterListLeakGuard.Register(Harmony, guardsClientSide);
            FiresCore.Diagnostics.VisEquipmentDropPrefabHeal.Register(Harmony);
            FiresCore.Diagnostics.PathfindingStats.Register(Harmony);

            // Bind the Fires config window's own appearance (font/opacity/accent) into Core's config and
            // register it, so an "Appearance" section shows right in the window and restyles live.
            FiresCore.UI.FiresConfigUI.BindAppearance(Config);
            FiresCore.UI.ConfigHiddenSettings.Initialize(configSync);

            // Auto-engage the shared text-capture gate whenever a UI text field is focused, so typing into any
            // Fires field stops leaking keystrokes to vanilla / other-mod hotkeys (e.g. a 'g' firing another
            // mod's [G] toggle). Client-only - a headless server has no EventSystem or typing UI.
            if (!Application.isBatchMode)
            {
                FiresCore.Input.FiresInputBlockDriver.Ensure();

                // Hold-modifier right-click context menus. Core owns the system + input gate; mods register
                // providers. Bind the (client-local) enable + rebindable-modifier config before starting the driver.
                FiresCore.UI.ContextMenu.ContextMenuConfig.Initialize(Config);
                FiresCore.UI.ContextMenu.FiresContextMenuDriver.Ensure();

                // Core's own sections for the shared help panel (registration only â€” the
                // panel is created by whichever mod calls HelpPanel.Initialize).
                try { FiresCore.Help.FiresCoreHelpContent.Register(); }
                catch (System.Exception ex) { Debug.LogWarning($"[{PluginName}] Help content registration failed: {ex.Message}"); }
            }
        }

        protected override void TitleScene(bool isFirstBoot)
        {
        }

        protected override void WorldStart()
        {
            FiresCoreBanner.Print();
            FiresCore.Npc.CompanionGroupHudProvider.RegisterIfCompanionHostLoaded();

            // Re-enumerate config now that every mod has bound (some bind after Core.Setup). The window also
            // rebuilds on open, but this primes the cache so va_config_dump is accurate before first open.
            FiresCore.UI.CfgDiscovery.Rebuild();
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
            // each one self-gates on its config entry - default-on so a fresh install gets the
            // fixes (e.g. Mistlands locations stay in Mistlands, not spilling mist into DeepNorth).
            FiresCore.Compat.Balrond.BalrondCompatConfig.Initialize(Config);
            FiresCore.Compat.Balrond.BalrondCompatConfig.BindToSync(configSync);

            // GroupHud: the shared below-minimap member panel. It stays hidden until a mod that feeds it
            // registers rows, and it also hides while any Fires panel is open.
            FiresCore.UI.GroupHud.GroupHudConfig.Initialize(Config);
            FiresCore.Bridge.GroupHudBridge.IsBlockingUiOpen = FiresCore.Bridge.ModUiRegistry.IsAnyOpen;

            // HuntList: server-synced, admin-editable list of passive "hunt-only" prey (deer/boar/â€¦)
            // that companions ignore unless Hunt is toggled on or the creature attacks first.
            FiresCore.Npc.HuntListConfig.Initialize(Config);
            FiresCore.Npc.HuntListConfig.BindToSync(configSync);

            // MovementGate: opt-in HARD enforcement of the movement single-writer rule (default OFF).
            // The SetMoveDir prefix only enforces when this is true; flip it on in-game after verifying
            // pathfinding still moves, flip off to revert instantly.
            FiresCore.Npc.Core.MovementGateConfig.Initialize(Config);
            FiresCore.Npc.Core.MovementGateConfig.BindToSync(configSync);

            // SmoothSpeedRamp: ease companion run speed up to full instead of snapping (ported from
            // FiresValcast NaturalWalk; min/max = the companion's own walk/run speed). Default ON.
            FiresCore.Npc.Core.MovementRampConfig.Initialize(Config);
            FiresCore.Npc.Core.MovementRampConfig.BindToSync(configSync);
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

