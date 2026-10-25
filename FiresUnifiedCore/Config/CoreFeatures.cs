using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace FiresCore.Config
{
    /// <summary>
    /// Marks a Harmony patch class (or an outer class holding patch classes) as part of an owned Core feature. Core's patch loop
    /// (FiresMod) defers it until every plugin has loaded, then patches it only when one of the listed features is live.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class FiresOwnedFeatureAttribute : Attribute
    {
        public string[] Features { get; }
        public FiresOwnedFeatureAttribute(params string[] features) { Features = features ?? Array.Empty<string>(); }
    }

    /// <summary>
    /// Core features that exist for other mods. ONE declaration per
    /// feature lists its owner mods; any owner installed = live. An inert feature has no config keys (OwnerGatedConfig), no
    /// patches (FiresOwnedFeature classes are skipped) and so no logs. One toggle per feature however many owners are installed.
    /// Liveness is final only once every plugin has loaded (<see cref="Resolve"/>, one frame after the chainloader's Awakes):
    /// BepInEx adds a plugin to PluginInfos just before creating it, so mods loading after Core aren't listed in Core's Awake.
    /// </summary>
    public static class CoreFeatures
    {
        public const string Swimming = "Swimming", Movement = "Movement", Climbing = "Climbing", Crawling = "Crawling",
            Loot = "Loot", Building = "Building", Progression = "Progression", LocationalCombat = "LocationalCombat",
            Companions = "Companions", GroupHud = "GroupHud", GroundRings = "GroundRings", Vault = "Vault",
            Archetypes = "Archetypes", Balrond = "Balrond", NpcBrain = "NpcBrain";

        private static readonly Dictionary<string, string[]> s_owners = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { Swimming, OwnerGatedConfig.Owners.SWIMMING },
            { Movement, OwnerGatedConfig.Owners.MOVEMENT },
            { Climbing, OwnerGatedConfig.Owners.CLIMBING },
            { Crawling, OwnerGatedConfig.Owners.CRAWLING },
            { Loot, OwnerGatedConfig.Owners.LOOT },
            { Building, OwnerGatedConfig.Owners.BUILDING },
            { Progression, OwnerGatedConfig.Owners.RPGCLASSES },
            { LocationalCombat, OwnerGatedConfig.Owners.RPGCLASSES },
            { Companions, OwnerGatedConfig.Owners.COMPANIONS },
            { GroupHud, OwnerGatedConfig.Owners.GROUPHUD },
            { GroundRings, OwnerGatedConfig.Owners.GROUNDRINGS },
            { Vault, OwnerGatedConfig.Owners.VAULT },
            { Archetypes, OwnerGatedConfig.Owners.CLASSES },   // class abilities: RPGClasses, and companions (Companions, RPGmaker) use archetypes too
            { Balrond, OwnerGatedConfig.Owners.BALROND },
            { NpcBrain, OwnerGatedConfig.Owners.NPCBRAIN },
        };

        private static Dictionary<string, bool> s_live;
        private static readonly List<Action> s_whenResolved = new List<Action>();

        /// <summary>Runs <paramref name="action"/> once liveness is final (at once if it already is): manual harmony.Patch calls go here.</summary>
        public static void WhenResolved(Action action)
        {
            if (action == null) return;
            if (s_live != null) { action(); return; }
            s_whenResolved.Add(action);
        }

        /// <summary>True once every plugin has loaded and liveness is final.</summary>
        public static bool Resolved => s_live != null;

        /// <summary>The owners of a feature (for OwnerGatedConfig.Gate at bind sites).</summary>
        public static string[] OwnersOf(string feature) => s_owners.TryGetValue(feature, out var o) ? o : Array.Empty<string>();

        /// <summary>
        /// True when an owner of the feature is installed. Before <see cref="Resolve"/> this reads the plugins loaded so far, so a
        /// caller in Core's own Awake may see false for an owner that loads later; runtime code (ticks, RPCs, scenes) is always after.
        /// </summary>
        public static bool IsLive(string feature)
        {
            if (s_live != null) return s_live.TryGetValue(feature, out bool live) && live;
            return AnyLoaded(OwnersOf(feature));
        }

        /// <summary>True when any of the features is live.</summary>
        public static bool AnyLive(string[] features)
        {
            if (features == null) return false;
            foreach (string f in features) if (IsLive(f)) return true;
            return false;
        }

        /// <summary>Fixes liveness for the session (called once every plugin has loaded) and logs one line per feature at Debug.</summary>
        internal static void Resolve()
        {
            if (s_live != null) return;
            var live = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var kv in s_owners) live[kv.Key] = AnyLoaded(kv.Value);
            s_live = live;
            foreach (var kv in s_live)
                FiresCore.FiresUnifiedCore.Log?.LogDebug($"[CoreFeatures] {kv.Key}: {(kv.Value ? "live" : "inert")} (owners: {string.Join(", ", OwnersOf(kv.Key))})");
            foreach (var action in s_whenResolved)
            {
                try { action(); }
                catch (Exception ex) { FiresCore.FiresUnifiedCore.Log?.LogError($"[CoreFeatures] a deferred hook failed: {ex.Message}"); }
            }
            s_whenResolved.Clear();
        }

        internal static int LiveCount { get { int n = 0; if (s_live != null) foreach (var v in s_live.Values) if (v) n++; return n; } }
        internal static int InertCount => s_live == null ? 0 : s_live.Count - LiveCount;

        /// <summary>Gates config entries on a feature's owners (removed or Advanced when none is installed).</summary>
        public static void GateConfig(string feature, bool synced, params ConfigEntryBase[] entries)
            => OwnerGatedConfig.Gate(OwnersOf(feature), synced, entries);

        private static bool AnyLoaded(string[] owners)
        {
            if (owners == null) return false;
            foreach (string guid in owners)
                if (guid != null && Chainloader.PluginInfos.ContainsKey(guid)) return true;
            return false;
        }
    }
}
