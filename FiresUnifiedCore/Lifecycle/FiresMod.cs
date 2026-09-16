using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace FiresCore.Lifecycle
{
    // Base for every Fires-* plugin. Carries the bits that don't depend on
    // the consumer's plugin type — singleton registry, headless probe, and
    // the RunOnServer toggle. The typed lifecycle (Setup / WorldStart /
    // Shutdown, Instance, Harmony) lives on the generic FiresMod<TPlugin>.
    //
    // Inspired by Zen.ModLib's ZenMod pattern (see FIRES_CORE_ZEN_REVIEW.md)
    // but trimmed of the Jotunn-coupled and compiler-generated parts.
    public abstract class FiresMod : BaseUnityPlugin
    {
        // One entry per loaded Fires-* mod. Mostly diagnostic — lets a
        // console command or a Compendium screen list every plugin built
        // on this base without walking BepInEx's chainloader registry.
        public static readonly Dictionary<Assembly, FiresMod> Instances
            = new Dictionary<Assembly, FiresMod>();

        // Headless dedicated build runs Unity with the null graphics device.
        // Cheap to call; safe before ZNet.instance exists.
        public static bool IsHeadless
            => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        // Default true: most Fires-* plugins do real work on dedi.
        // Client-only mods (HUD-only / overlay-only) override to false to
        // skip WorldStart on a dedicated server while still letting Setup
        // run for Harmony patches that benignly no-op on the server side.
        protected virtual bool RunOnServer => true;
    }

    // Generic plugin base. Inherit as `class MyMod : FiresMod<MyMod>` and
    // implement Setup / TitleScene / WorldStart / Shutdown. The base owns
    // Awake, OnDestroy, scene-loaded hookup, the Harmony lifecycle, and a
    // 1Hz CheckInit poll that gates WorldStart on IsSysReady.
    //
    // Lifecycle (per Fires-* mod load):
    //   Awake          → Harmony.PatchAll → Setup → start CheckInit poll
    //   CheckInit (1Hz)→ if IsSysReady → WorldStart → (first time) HotRestore
    //   sceneLoaded("start") → TitleScene → restart CheckInit poll
    //   OnDestroy      → Shutdown → Harmony.UnpatchSelf
    public abstract class FiresMod<TPlugin> : FiresMod
        where TPlugin : FiresMod<TPlugin>
    {
        private const float CheckInitDelaySeconds = 1f;
        private const float CheckInitIntervalSeconds = 1f;
        private const string CheckInitMethodName = nameof(CheckInit);
        private const string TitleSceneName = "start";

        // Typed singleton — resolved through the generic base, so callsite
        // code reads `FiresUnifiedCore.Instance.configSync` with no cast.
        public static TPlugin Instance { get; private set; }

        // True once WorldStart has finished without throwing. Reset to
        // false whenever the engine returns to the title scene, since a
        // new world load triggers another WorldStart cycle.
        public static bool Initialized { get; private set; }

        // Per-mod Harmony instance, keyed by the plugin GUID so a console
        // unload tears down only this mod's patches.
        protected Harmony Harmony { get; private set; }

        private readonly Assembly _assembly;
        private bool _hotRestoreFired;

        protected FiresMod()
        {
            if ((object)Instance != null)
                throw new InvalidOperationException(
                    $"FiresMod<{typeof(TPlugin).Name}> singleton already instantiated. " +
                    "BepInEx loaded the plugin twice — check for duplicate DLLs.");

            Instance = (TPlugin)this;
            _assembly = GetType().Assembly;
            Instances[_assembly] = this;
        }

        // Mod-specific bootstrap. Runs after Harmony.PatchAll, so any
        // patched-into-vanilla behavior is already live. Bind ConfigEntry
        // fields, instantiate ConfigSync, register RPCs / console commands.
        protected abstract void Setup();

        // Fires every time the engine loads the title scene ("start").
        // isFirstBoot is true exactly once per process; subsequent calls
        // come from the user logging out of a world back to the menu.
        protected abstract void TitleScene(bool isFirstBoot);

        // Fires when IsSysReady first returns true after Setup or a title-
        // scene reset. Use for work that needs ZNet / ZoneSystem live —
        // RPC subscription, world-scoped containers, scene-bound caches.
        protected abstract void WorldStart();

        // Fires on OnDestroy. Tear down anything Setup opened that
        // Harmony.UnpatchSelf doesn't cover (subscribers, IDisposables,
        // long-lived GameObjects parented under DontDestroyOnLoad).
        protected abstract void Shutdown();

        // Optional. Fires once, immediately after the first WorldStart.
        // Plugin-DLL mods rarely need this; the hook exists so script-
        // engine consumers can reattach state to already-spawned objects
        // after a hot reload mid-session.
        protected virtual void HotRestore() { }

        // Engine-readiness predicate for the CheckInit poll. Default:
        // ZNet + ZoneSystem live; on clients also Hud + local player.
        // Override only when a mod needs additional readiness (e.g.
        // waiting on ObjectDB.IsValid or a peer-list quorum).
        protected virtual bool IsSysReady()
        {
            if (ZNet.instance == null) return false;
            if (ZoneSystem.instance == null) return false;

            if (ZNet.instance.IsDedicated())
                return true;

            if (Hud.instance == null) return false;
            return Player.m_localPlayer != null;
        }

        /// <summary>
        /// Namespaces whose [HarmonyPatch] classes are NOT auto-patched on Awake — compiled-but-dormant
        /// code (e.g. a Core engine cluster mid-migration that its origin mod still patches, so patching
        /// it here would double-patch). Null/empty (default) = patch everything, identical to
        /// Harmony.PatchAll. Subclasses override to gate.
        /// </summary>
        protected virtual System.Collections.Generic.IReadOnlyCollection<string> DormantPatchNamespaces => null;

        /// <summary>
        /// Full names of Harmony patch classes (and their nested types) whose patch bodies
        /// reference client-only assemblies/types (Hud, Minimap, InventoryGui, FejdStartup, …).
        /// On a dedicated server these are skipped during PatchAll — importing their references
        /// in Mono's IL rewriter native-crashes a headless build. Null/empty = skip nothing.
        /// </summary>
        protected virtual System.Collections.Generic.IReadOnlyCollection<string> DedicatedServerSkipPatchTypes => null;

        // True when a patch type (or any of its nesting parents) is in the dedicated-server skip set.
        private static bool IsDedicatedServerSkip(Type type, System.Collections.Generic.IReadOnlyCollection<string> skipTypes)
        {
            for (var current = type; current != null; current = current.DeclaringType)
            {
                var name = current.FullName;
                foreach (var skipType in skipTypes)
                    if (skipType == name) return true;
            }
            return false;
        }

        private void Awake()
        {
            Harmony = new Harmony(Info.Metadata.GUID);

            try
            {
                var dormant = DormantPatchNamespaces;
                var dediSkip = DedicatedServerSkipPatchTypes;
                bool hasDormant = dormant != null && dormant.Count > 0;
                bool hasDediSkip = dediSkip != null && dediSkip.Count > 0
                    && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

                // Per-type PatchAll (faithful to Harmony.PatchAll semantics — the stock call just
                // runs a PatchClassProcessor over every type) with a per-type catch, so a class
                // that fails to attach is logged BY NAME and cannot abort attachment of every
                // class enumerated after it. A single try around the whole assembly silently lost
                // all later patch classes behind one generic "PatchAll threw" line. Also applies
                // (a) dormant namespaces and (b) on a dedicated server, client-only patch classes
                // that would native-crash Mono's IL rewriter.
                foreach (var type in HarmonyLib.AccessTools.GetTypesFromAssembly(_assembly))
                {
                    if (type == null) continue;

                    if (hasDormant)
                    {
                        var typeNamespace = type.Namespace;
                        bool skip = false;
                        if (typeNamespace != null)
                            foreach (var dormantName in dormant)
                                if (typeNamespace == dormantName || typeNamespace.StartsWith(dormantName + ".", StringComparison.Ordinal)) { skip = true; break; }
                        if (skip) continue;
                    }

                    if (hasDediSkip && IsDedicatedServerSkip(type, dediSkip)) continue;

                    try
                    {
                        new HarmonyLib.PatchClassProcessor(Harmony, type).Patch();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Harmony patch class '{type.FullName}' failed to attach: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Harmony.PatchAll threw: {ex}");
            }

            try
            {
                Setup();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Setup() threw: {ex}");
            }

            BeginInitPoll();
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnDestroy()
        {
            try { Shutdown(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Info.Metadata.Name}] Shutdown() threw: {ex.Message}");
            }

            try { Harmony?.UnpatchSelf(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Info.Metadata.Name}] Harmony.UnpatchSelf threw: {ex.Message}");
            }

            Instances.Remove(_assembly);
            if ((object)Instance == this) Instance = null;
            Initialized = false;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != TitleSceneName) return;

            try
            {
                TitleScene(!_hotRestoreFired);
            }
            catch (Exception ex)
            {
                Logger.LogError($"TitleScene() threw: {ex}");
            }

            BeginInitPoll();
        }

        private void BeginInitPoll()
        {
            Initialized = false;
            CancelInvoke(CheckInitMethodName);
            InvokeRepeating(CheckInitMethodName, CheckInitDelaySeconds, CheckInitIntervalSeconds);
        }

        private void CheckInit()
        {
            if (!IsSysReady()) return;

            if (ZNet.instance.IsDedicated() && !RunOnServer)
            {
                CancelInvoke(CheckInitMethodName);
                return;
            }

            CancelInvoke(CheckInitMethodName);

            try
            {
                WorldStart();

                if (!_hotRestoreFired)
                {
                    HotRestore();
                    _hotRestoreFired = true;
                }

                Initialized = true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"WorldStart() threw: {ex}");
            }
        }
    }
}
