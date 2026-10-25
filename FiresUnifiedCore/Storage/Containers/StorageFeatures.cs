using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using FiresCore.Logging;
using HarmonyLib;

namespace FiresCore.Storage
{
    /// <summary>
    /// Craft from containers + auto sort in Core (Tools\CORE_CRAFT_CONTAINERS_SORT_DESIGN.md).
    /// The master toggle "Storage Features" (advanced, OFF by default) and the on/off switch for everything under FiresCore.Storage.
    ///   off  a true no-op: no Harmony patch applied, no RPC used, no object created (Core's loader skips FiresCore.Storage.Patches,
    ///        DormantPatchNamespaces); one Debug line says so.
    ///   on   Enable() applies every [HarmonyPatch] class in FiresCore.Storage.Patches with this feature's own Harmony instance, one class at
    ///        a time (one failing class can't block the rest); Disable() unpatches exactly that instance. Flipping the toggle mid-session
    ///        works both ways.
    /// No-block policy: a feature whose counterpart mod is loaded (AzuCraftyBoxes / CraftFromContainers for crafting, QuickStackStore /
    /// AzuAutoStore for sorting) stands down with one Warning; Feature(...) tells the stage code which parts may run.
    /// </summary>
    public static class StorageFeatures
    {
        public const string HarmonyId = "com.Fire.FiresUnifiedCore.storage";
        public const string PatchNamespace = "FiresCore.Storage.Patches";
        private const string Section = "Storage";
        private const string LogPrefix = "[Storage]";

        public enum Part { CraftFromContainers, Sort, Tags, Hopper, SignLabels }

        // counterpart mods per part (BepInEx GUIDs); a loaded one makes that part stand down
        private static readonly Dictionary<Part, string[]> Counterparts = new Dictionary<Part, string[]>
        {
            { Part.CraftFromContainers, new[] { "Azumatt.AzuCraftyBoxes", "aedenthorn.CraftFromContainers" } },
            { Part.Sort, new[] { "goldenrevolver.quick_stack_store", "Azumatt.AzuAutoStore" } },
        };

        internal static ConfigEntry<bool> Master;

        private static Harmony s_harmony;
        private static readonly List<string> s_applied = new List<string>();
        private static readonly HashSet<Part> s_standDown = new HashSet<Part>();

        /// <summary>True while the master toggle is on and Enable() ran.</summary>
        public static bool Active { get; private set; }
        /// <summary>The storage patch classes applied right now (empty when off), for storage_test's off_noop step.</summary>
        public static IReadOnlyList<string> AppliedPatchClasses => s_applied;

        /// <summary>Whether a part may run: the master toggle on and no counterpart mod loaded for it.</summary>
        public static bool Feature(Part part)
        {
            if (!Active) return false;
            if (!s_counterpartsChecked) CheckCounterparts();   // Lazily, after every plugin has loaded
            return !s_standDown.Contains(part);
        }

        private static bool s_counterpartsChecked;

        private static void CheckCounterparts()
        {
            s_counterpartsChecked = true;
            s_standDown.Clear();
            foreach (var kv in Counterparts)
            {
                string found = kv.Value.FirstOrDefault(g => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(g));
                if (found == null) continue;
                s_standDown.Add(kv.Key);
                FiresLogger.LogWarning($"{LogPrefix} {kv.Key} stands down: '{found}' is installed and does the same job (no-block policy)");
            }
        }

        /// <summary>Marks a config description as advanced (Core's convention: Fires config window + ConfigurationManager hide it
        /// behind "Show advanced"). Every Storage key goes through here so none can miss it.</summary>
        internal static ConfigDescription Advanced(string text, AcceptableValueBase range = null) =>
            new ConfigDescription(text, range, new FiresCore.Sync.ConfigurationManagerAttributes { IsAdvanced = true });

        /// <summary>Called once from FiresUnifiedCore.InitializeConfigAndSync, like the other feature Binds.</summary>
        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            if (Master != null) return;   // once per process (a second bind/sync throws on ZNet.Awake)
            Master = config.Bind(Section, "Storage Features", false,
                Advanced("SERVER. Craft from containers, auto sort, chest tags, hopper chests and sign labels. OFF = nothing of it runs "
                    + "(no patches applied). The parts have their own toggles below."));
            configSync?.AddConfigEntry(Master);
            StorageConfig.Bind(config, configSync);   // Stage 2 keys
            Master.SettingChanged += (s, e) => Apply();
            Apply();
        }

        private static void Apply()
        {
            if (Master != null && Master.Value) Enable(); else Disable();
        }

        private static void Enable()
        {
            if (Active) return;
            s_standDown.Clear();
            s_counterpartsChecked = false;   // Checked lazily by Feature(), after every plugin has loaded
            s_harmony = s_harmony ?? new Harmony(HarmonyId);
            s_applied.Clear();
            int failed = 0;
            foreach (Type t in typeof(StorageFeatures).Assembly.GetTypes())
            {
                if (t.Namespace != PatchNamespace || t.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try { s_harmony.CreateClassProcessor(t).Patch(); s_applied.Add(t.Name); }
                catch (Exception e) { failed++; FiresLogger.LogWarning($"{LogPrefix} patch class {t.Name} not applied: {e.GetType().Name}: {e.Message}"); }
            }
            Active = true;
            if (ZNetScene.instance != null) ContainerRegistry.Seed();   // Containers already loaded when switched on mid-session
            HopperChest.EnsurePiece();   // The Hopper Chest on the Hammer (if its key is on)
            FiresLogger.LogInfo($"{LogPrefix} storage features ON: {s_applied.Count} patch class(es) applied{(failed > 0 ? $", {failed} failed" : "")}"
                );
        }

        private static void Disable()
        {
            bool was = Active;
            Active = false;
            if (s_harmony != null) s_harmony.UnpatchSelf();
            s_applied.Clear();
            s_standDown.Clear();
            s_counterpartsChecked = false;
            ContainerRegistry.Clear();
            HopperChest.RemovePiece();
            if (was) FiresLogger.LogInfo($"{LogPrefix} storage features OFF: every storage patch removed");
            else FiresLogger.LogDebug($"{LogPrefix} storage features off (no-op)");
        }
    }
}
