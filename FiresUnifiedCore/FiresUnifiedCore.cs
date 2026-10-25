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
    [BepInDependency("Norger.Vedr", BepInDependency.DependencyFlags.SoftDependency)]
    public class FiresUnifiedCore : FiresMod<FiresUnifiedCore>
    {
        public const string PluginGUID = "com.Fire.FiresUnifiedCore";
        public const string PluginName = "FiresUnifiedCore";
        public const string PluginVersion = "0.2.823";   // 0.2.823 (halo): Fireplace under-terrain / wet+roof along the local up ([classes-ui]; Smoke left to FTS); possession flight, hover and yaw in the ring frame ([wishbone]; add-only FiresPossession.FlightModel.UpAt hook, set by Core, null in Valcast's copy); ring object sectors ([wishbone] rev B, [fgn] design: terrain zones stay x,z; World.RingZones.ObjectSector = FatApi.HaloZone on the ring for FindSectorObjects/FindObjects callers + KeepAlive; vanilla elsewhere); companion ring pass ([ghost]: PathWalker / Steering / CompanionPathRules / CompanionTraversal / chore placement along the local up; add-only GravityUp.Flat / UpComponent; Indoors false on a ring); ring map ([classes-ui]: minimap wind sock along FAT's strip, biome paint refused on the ring, trip area as a ring-floor ellipse). 0.2.822 ([classes-ui] files, [generator] build): player climb / vault / pull-up measure along the local up on a ring (add-only Movement.UpFrame, Climbing.TopSolidAt); off a ring bit-identical; [wishbone] files: ability ground rings / samplers / water drop, run slide, emergency evasion, summons and the carried body along up. 0.2.821 ([generator] + [visual]): THE ring motor moves into Core (World\Halo: RingMotor / RingCreatures / RingBoot, from FDT 1.1.1021 HaloSpike; patches only once a GravityUp field is active). 0.2.820 ([generator], halo A6): WearNTear support judged along the local up (FindSupportPoint along -up, read in the ring frame), roof / ash-roof checks cast along up. 0.2.819 ([generator], halo creatures #10): attack launch arcs / spread / attack heights follow the attacker's local up (Attack transpile), melee aim split in the ring frame; possession flight = GravityUp.BodyOverride. 0.2.818 ([generator], halo A4 + A9): projectiles / cinders fall along -GravityUp; fall damage measured along up (GravityUp-aware m_maxAirAltitude; add-only RingGravityPatches.ResetFall); UnderWorldCheck + EdgeOfWorldKill skipped in a field; GravityBody never doubles FDT HaloGravityBody. 0.2.817 ([generator], halo A1-A3): GravityBody - drops, logs, ragdolls, networked bodies fall along -GravityUp in a field (every peer); ItemDrop.TerrainCheck skipped there; add-only GravityUp.BodyOverride / SetBodyOverride. 0.2.816 ([generator], Fire 10-24 halo ring): Surface probes along GravityUp (add-only Surface.Point; no heightmap fallback in a field), companion spawn/teleport/catch-up snap on the ring, grounded check along the local up
        // The oldest Core whose synced config keys still match this one. A fixed constant, raised only when a
        // synced key's name, type or meaning changes; tying it to PluginVersion forced every player and server to update together.
        public const string SyncMinimumVersion = "0.2.387";   // the last change to a synced key's presence or meaning

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
        // Storage patches are applied only by StorageFeatures.Enable (master toggle).
        protected override System.Collections.Generic.IReadOnlyCollection<string> DormantPatchNamespaces =>
            new[] { FiresCore.Storage.StorageFeatures.PatchNamespace };

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
                // Pause-menu panel + layout element (Menu.Update postfix, client-only UI)
                "FiresCore.UI.Shell.ShellPauseMenu",
                // Intro-cinematic skip: CinematicsManager is a VideoPlayer/Camera host that does not
                // exist headless, same IL-rewriter crash class as the FejdStartup patches above.
                "FiresCore.Lifecycle.IntroCinematicSkip",
            };

        // One bind step that throws (a locked cfg, a bad value) is logged and retried once instead of ending Setup there.
        private static void SetupStep(string name, Action step)
        {
            try { step(); return; }
            catch (Exception ex) { Log.LogWarning($"Setup step {name} threw, retrying once: {ex.Message}"); }
            try { step(); }
            catch (Exception ex) { Log.LogError($"Setup step {name} failed twice, Core continues without it: {ex}"); }
        }

        protected override void Setup()
        {
            Log = Logger;
            // Before the first Bind: a cfg locked for a moment by another program must not kill Core's (or any later mod's) Awake.
            FiresCore.IO.ConfigSaveGuard.Install(Harmony);
            FiresCore.Logging.FiresLogColorPatch.TakeOwnership();
            FiresCoreBanner.PrintBig();
            Debug.Log($"[{PluginName}] Awake() - version {PluginVersion}");

            // Consolidate + group the whole Fires-family config folder before anything reads it this session.
            FiresCore.Storage.FiresConfigPaths.Migrate();

            // [Logging] Log Level first, so the family's info lines are held back (or not) from the next one on.
            SetupStep("Logging.FiresLogLevel.Bind", () => FiresCore.Logging.FiresLogLevel.Bind(Config));
            SetupStep("Logging.StatusBanner.BindConfig", () => FiresCore.Logging.StatusBanner.BindConfig(Config));
            SetupStep("Logging.BepInExLogSuppressionPatch.BindConfig", () => FiresCore.Logging.BepInExLogSuppressionPatch.BindConfig(Config));
            SetupStep("Logging.LoadSummary.BindConfig", () => FiresCore.Logging.LoadSummary.BindConfig(Config));
            SetupStep("Logging.RateLimitedLogHandler.BindConfig", () => FiresCore.Logging.RateLimitedLogHandler.BindConfig(Config));
            SetupStep("Materials.VanillaShaderRebind.BindConfig", () => FiresCore.Materials.VanillaShaderRebind.BindConfig(Config));
            SetupStep("Npc.JobBoard.JobBoardWindow.BindConfig", () => FiresCore.Npc.JobBoard.JobBoardWindow.BindConfig(Config));
            SetupStep("Npc.Debugging.CompanionIntentView.BindConfig", () => FiresCore.Npc.Debugging.CompanionIntentView.BindConfig(Config));
            SetupStep("Npc.IdleBehaviors.TripBehavior.BindConfig", () => FiresCore.Npc.IdleBehaviors.TripBehavior.BindConfig(Config));
            SetupStep("Loot.MineralSeeds.BindConfig", () => FiresCore.Loot.MineralSeeds.BindConfig(Config));
            FiresCore.Materials.VanillaShaderSources.Install();
            FiresCore.IO.FileWatchHubConfig.Initialize(Harmony, Config);
            FiresCore.Lifecycle.CollisionCallbackReuse.Initialize(Config);
            FiresCore.Lifecycle.GroundDataThrottle.Initialize(Config);
            FiresCore.Lifecycle.IntroCinematicSkip.Initialize(Config);
            FiresCore.World.DistantSectorSkip.Initialize(Config);
            FiresCore.Bridge.SeasonBridge.Bind();
            InstallLogFilter();
            FiresCore.Logging.FiresLogLevel.Announce(Log);
            InitializeConfigAndSync();

            // The Diagnostics guards attach EXPLICITLY with read-back verification - the
            // attribute-based versions compiled but never attached in the
            // field (EnemyHud.TestShow NRE persisted with zero guard log lines). The client
            // gate mirrors the DedicatedServerSkipPatchTypes rationale: patching the
            // client-only EnemyHud type on a headless server native-crashes the IL rewriter;
            // the Character.OnDestroy finalizer + s_characters sweep stay active on the dedi.
            bool guardsClientSide = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
            FiresCore.Diagnostics.CharacterListLeakGuard.Register(Harmony, guardsClientSide);
            FiresCore.Diagnostics.VisEquipmentDropPrefabHeal.Register(Harmony);
            FiresCore.Diagnostics.PathfindingStats.Register(Harmony);
            FiresCore.Compat.HdTexturesLoadSpread.Register(Harmony, Config);
            FiresCore.Compat.HdAdditionsShaderScan.Register(Harmony);
            // Locational damage: dormant until a mod (FiresRPGClasses, the tests) enables it. Plan: Tools\LOCATIONAL_HITS.md
            FiresCore.Combat.LocationalDamage.Register(Harmony, Config);
            // Melee swings pitch toward the crosshair (local player; patch class auto-applies via PatchAll).
            FiresCore.Combat.MeleeCrosshairAim.Bind(Config);
            // ZSync: attaches the ClientSync kinematic prefix, or with option B a 2 s sweep instead (default OFF).
            FiresCore.Npc.KinematicSync.Bind(Config, Harmony);
            // [Companions] Rebuild body skin on reject (fixes an invisible body after a mesh switch).
            FiresCore.Npc.NpcBodyMeshGuard.Bind(Config);
            // Scroll speed in Fires windows (roots mods register with FiresUi.Register); the hook is client-only.
            FiresCore.UI.FiresUi.Install(Harmony, Config);
            // Fires UI shell (Core step 0/1): binds [UI] Fires UI override + hub key / reduce motion / sky drift. With the master
            // OFF (default) this binds the keys and nothing else: no patch, MonoBehaviour, camera or GameObject.
            FiresCore.UI.Shell.FiresShell.Bind(Config);
            // The vault rolls back with the world after a crash, so vault and world saves stay consistent.
            FiresCore.Storage.VaultWorldSnapshots.Register(Harmony, Config);

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

                // Core's own sections for the shared help panel (registration only; the
                // panel is created by whichever mod calls HelpPanel.Initialize).
                try { FiresCore.Help.FiresCoreHelpContent.Register(); }
                catch (System.Exception ex) { Debug.LogWarning($"[{PluginName}] Help content registration failed: {ex.Message}"); }
            }
        }

        protected override void TitleScene(bool isFirstBoot)
        {
            // Every mod is loaded by the title scene: log once which water source Core reads (FAT water query or vanilla only).
            FiresCore.World.Water.Resolve();
            // Every plugin has run Awake: drop / hide the settings whose owner mod is absent.
            FiresCore.Config.OwnerGatedConfig.Apply();
            FiresCore.UI.StaleEntries.PruneCoreKnown(Config);   // Core's own retired keys (orphans only)
        }

        protected override void WorldStart()
        {
            FiresCore.World.Water.Resolve();   // the dedicated server has no title scene
            FiresCore.Config.OwnerGatedConfig.Apply();   // ditto; a no-op once the title scene ran it
            FiresCore.UI.StaleEntries.PruneCoreKnown(Config);   // Core's own retired keys (orphans only)
            FiresCoreBanner.Print();
            FiresCore.Npc.CompanionGroupHudProvider.RegisterIfCompanionHostLoaded();

            // Re-enumerate config now that every mod has bound (some bind after Core.Setup). The window also
            // rebuilds on open, but this primes the cache so va_config_dump is accurate before first open.
            FiresCore.UI.CfgDiscovery.Rebuild();

            FiresCore.Terrain.HeightmapOverrideStatus.ReportLimits();
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
                MinimumRequiredVersion = SyncMinimumVersion,
            };
            configSync.lockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;
            configSync.AddLockingConfigEntry(ConfigManager.Instance.configServerAuthority);
            configSync.AddConfigEntry(ConfigManager.Instance.configVerboseLogging);

            // HeightmapOverride: server-locked terrain height limits. Plan: Docs/HEIGHTMAP_OVERRIDE_PLAN.md
            FiresCore.Terrain.HeightmapOverrideConfig.Initialize(Config);
            FiresCore.Terrain.HeightmapOverrideConfig.BindToSync(configSync);
            FiresCore.Terrain.HeightmapOverrideStatus.ReportPatches();

            // Progression: one character level then Valor, shared by FiresRPGClasses and the companion
            // engine. Every rate is server-locked. Plan: Docs/PLAN_ClassesFoundation.md
            FiresCore.Classes.ProgressionSettings.Initialize(Config);
            FiresCore.Classes.ProgressionSettings.BindToSync(configSync);

            // Locational damage: the server's on/off state and table, so every peer agrees.
            FiresCore.Combat.LocationalDamage.BindToSync(configSync);
            // Companion hits on players, server-set damage multiplier.
            FiresCore.Npc.Combat.CompanionPvpDamage.Bind(Config, configSync);
            // Wild companions' no-park places (dungeon doors, test spots), server-set.
            FiresCore.Npc.WildSpawn.WildNoPark.Bind(Config, configSync);
            // Companion attachments: the skinned-rescale switch (server-set).
            FiresCore.Npc.NpcAttachmentScaleFix.Bind(Config, configSync);
            FiresCore.Npc.CompanionPassThrough.Bind(Config, configSync);   // pass through friendly companions
            // Climbing: building pieces climbable (server-set) and the players' input switch.
            FiresCore.Movement.Climbing.Bind(Config, configSync);
            // Clip slots + double jump / run slide / styles registries. All Auto = off without a consumer.
            FiresCore.Animation.ClipSlots.Bind(Config);
            FiresCore.Movement.CoreMovement.Bind(Config, configSync);
            FiresCore.Visuals.RuneGlow.Bind(Config);
            FiresCore.Storage.RemoteStore.Bind(Config);
            FiresCore.Movement.Headroom.Bind(Config);
            FiresCore.Movement.MonsterFallDamage.Bind(Config, configSync);
            FiresCore.Loot.LocationLootRefresh.Bind(Config, configSync);
            FiresCore.Visuals.FitMeasure.Bind(Config);   // [MonsterArmorFit] "Measured helmet fit" (client, default off)
            FiresCore.Possession.FlyerAutoLock.Bind(Config);   // [Possession] "Flyer attack auto-lock" (client, default on, flyers only)
            FiresCore.Visuals.ArmourPaint.Bind(Config);   // [MonsterArmorFit] "Paint armour textures on monsters" (client, default off)
            FiresCore.Visuals.MonsterArmorFit.Bind(Config);   // [MonsterArmorFit] "Vanilla bind on identical rigs" (client, default off)
            // The companion barber's fee and size range (server-locked).
            FiresCore.Npc.CompanionAppearanceService.Bind(Config, configSync);
            FiresCore.Npc.Races.CompanionRaces.Bind(Config, configSync);
            FiresCore.Storage.StorageFeatures.Bind(Config, configSync);
            FiresCore.Npc.CompanionPillionDriver.Bind(Config, configSync);
            FiresCore.Building.NewPieceFacesAway.Bind(Config);
            // Creature possession (admin + class grants), flight keys.
            FiresCore.Possession.PossessionRules.Bind(Config, configSync);
            FiresCore.Flight.FlightKeys.Bind(Config);
            FiresCore.Movement.Crawl.Bind(Config);
            FiresCore.Movement.CrawlPose.CrawlPose.Bind(Config);
            FiresCore.Movement.CrawlCamera.Bind(Config);
            FiresCore.Movement.PlayerSwim.Bind(Config);
            FiresCore.Tutorials.TutorialBridge.Bind(Config);
            FiresCore.Undo.UndoHistory.Bind(Config);

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

            // HuntList: server-synced, admin-editable list of passive "hunt-only" prey (deer/boar/...)
            // that companions ignore unless Hunt is toggled on or the creature attacks first.
            FiresCore.Npc.HuntListConfig.Initialize(Config);
            FiresCore.Npc.HuntListConfig.BindToSync(configSync);

            // Pause-menu "Need help? Join our Discord": [Help] config, server-synced; Core claims the one button.
            FiresShared.DiscordHelp.DiscordHelpButton.Initialize(Config);
            FiresShared.DiscordHelp.DiscordHelpButton.BindToSync(configSync);

            // [Audio] kill switch for routing mod / NPC sounds to the game mixer.
            FiresCore.Services.LiveAudioRouting.Initialize(Config);

            // MovementGate: opt-in HARD enforcement of the movement single-writer rule (default OFF).
            // The SetMoveDir prefix only enforces when this is true; flip it on in-game after verifying
            // pathfinding still moves, flip off to revert instantly.
            FiresCore.Npc.Core.MovementGateConfig.Initialize(Config);
            FiresCore.Npc.Core.MovementGateConfig.BindToSync(configSync);

            // SmoothSpeedRamp: ease companion run speed up to full instead of snapping (ported from
            // FiresValcast NaturalWalk; min/max = the companion's own walk/run speed). Default ON.
            FiresCore.Npc.Core.MovementRampConfig.Initialize(Config);
            FiresCore.Npc.Core.MovementRampConfig.BindToSync(configSync);

            // GroundRings: viewer preferences for the shared ability ground rings, deliberately NOT
            // server-synced. Plan: Docs/PLAN_GroundRings.md
            FiresCore.Npc.Archetypes.Effects.GroundRingConfig.Initialize(Config);
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

