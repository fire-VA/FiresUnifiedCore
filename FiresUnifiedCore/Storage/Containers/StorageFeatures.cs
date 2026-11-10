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
        /// <remarks>0.2.1042 (Fire 10-30: "the config settings go to storage and nothing runs if storage is not installed"): Core binds and
        /// syncs no storage key any more; FiresStorageNetwork binds, syncs (its own ConfigSync) and Provides them. The gate: no provider for
        /// "Storage Features" = FSN not installed = Apply never patches. FSN's master can change at runtime, so it is polled once a second
        /// (and the Hopper Chest piece follows its key the same way).</remarks>
        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            if (s_bound) return;   // once per process
            s_bound = true;
            var host = FiresUnifiedCore.Instance;
            if (host != null) host.gameObject.AddComponent<StoragePoll>();
            Apply();
        }

        private static bool s_bound, s_loggedNoOwner, s_lastHoppers;
        internal const string Owner = "FiresStorageNetwork";

        /// <summary>The master: FSN's "Storage Features" (false while FSN is absent).</summary>
        internal static bool MasterOn => FiresCore.Config.CoreSettings.IsProvided(Section, "Storage Features")
            && FiresCore.Config.CoreSettings.Get(Section, "Storage Features", false);

        private static void Apply()
        {
            if (!FiresCore.Config.CoreSettings.IsProvided(Section, "Storage Features"))
            {
                if (!s_loggedNoOwner && FiresCore.Config.CoreFeatures.Resolved) { s_loggedNoOwner = true; FiresLogger.LogInfo($"{LogPrefix} features off: {Owner} not installed"); }
                Disable();
                return;
            }
            if (MasterOn) Enable(); else Disable();
        }

        /// <summary>Once a second: re-runs Apply when the master changes (FSN providing late, or its value switched live), and adds /
        /// removes the Hopper Chest piece when its key changes while the features are on.</summary>
        private sealed class StoragePoll : UnityEngine.MonoBehaviour
        {
            private float m_next;
            private void Update()
            {
                float now = UnityEngine.Time.unscaledTime;
                if (now < m_next) return;
                m_next = now + 1f;
                if (!s_loggedNoOwner && FiresCore.Config.CoreFeatures.Resolved && !FiresCore.Config.CoreSettings.IsProvided(Section, "Storage Features")) Apply();   // 0.2.1046: the not-installed line only after every plugin loaded
                if (MasterOn != Active) { FiresLogger.LogDebug($"{LogPrefix} master now {(MasterOn ? "on" : "off")} ({FiresCore.Config.CoreSettings.ProviderOf(Section, "Storage Features") ?? "no provider"})"); Apply(); }
                if (!Active) return;
                bool hoppers = StorageConfig.Hoppers.Value;
                if (hoppers == s_lastHoppers) return;
                s_lastHoppers = hoppers;
                if (hoppers) HopperChest.EnsurePiece(); else HopperChest.RemovePiece();
            }
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
            s_lastHoppers = StorageConfig.Hoppers.Value;
            FiresLogger.LogDebug($"{LogPrefix} storage features ON: {s_applied.Count} patch class(es) applied{(failed > 0 ? $", {failed} failed" : "")}"
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
