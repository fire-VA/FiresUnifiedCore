using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using FiresCore.Sync;
using HarmonyLib;

namespace FiresCore.Config
{
    /// <summary>
    /// Core settings that only matter when another mod is installed. Each bind site calls <see cref="Gate"/> next to
    /// its Bind with the owner mods' GUIDs. Once every plugin has loaded (<see cref="Apply"/>: Core's TitleScene, and WorldStart
    /// on a dedicated server), an entry none of whose owners is loaded is either REMOVED from the ConfigFile (local, non-synced
    /// keys: the window, ConfigurationManager and the .cfg no longer show them) or tagged Advanced (server-synced keys: they stay
    /// bound so ServerSync still finds them, and show only with Show advanced on). With an owner loaded nothing changes.
    /// A removed ConfigEntry keeps its value, so code reading entry.Value still gets the default (or the value the file had).
    /// </summary>
    public static class OwnerGatedConfig
    {
        public static class Owners
        {
            public static readonly string[] COMP = { "com.Fire.FiresCompanions", "com.Fire.FiresRPGmaker" };
            public static readonly string[] CLASSES = { "com.Fire.FiresRPGClasses", "com.Fire.FiresCompanions", "com.Fire.FiresRPGmaker" };
            public static readonly string[] FLIGHT = { "com.Fire.firesvalcast", "com.Fire.FiresMounts" };
            public static readonly string[] VALCAST = { "com.Fire.firesvalcast" };
            public static readonly string[] HD = { "Badgers.HDValheimTextures" };
            public static readonly string[] BALROND = { "balrond.astafaraios.BalrondAmazingNature" };

            // Owner map (Tools\CORE_CONFIG_GATING_PLAN.md). FAT = com.Fire.FiresAdminTerrain, FAP = com.Fire.adminprefabs.
            public const string FatGuid = "com.Fire.FiresAdminTerrain", FapGuid = "com.Fire.adminprefabs", GalaxiesGuid = "com.Fire.FiresValheimGalaxies",
                RpgClassesGuid = "com.Fire.FiresRPGClasses", CompanionsGuid = "com.Fire.FiresCompanions", RpgMakerGuid = "com.Fire.FiresRPGmaker",
                GuildsGuid = "com.Fire.FiresGuilds", FmoGuid = "com.Fire.FiresMonsterOverhaul";
            public const string ApmGuid = "com.Fire.FiresAdvancedPlayerMovement";   // 0.2.1139: APM owns movement, so APM alone switches these on
            public static readonly string[] SWIMMING = { GalaxiesGuid, ApmGuid };
            public static readonly string[] MOVEMENT = { FatGuid, ApmGuid };
            // FCC uses climbing too (size scaling), so its presence makes the climb live (patches applied).
            public static readonly string[] CLIMBING = { FatGuid, RpgClassesGuid, "com.Fire.FiresCharacterCreator", ApmGuid };
            // 0.2.1190 (Fire 11-06: "apm holds the configs and decides if it can run or not"): APM alone switches crawling on (was FAT, RPGClasses, VikHavn too).
            public static readonly string[] CRAWLING = { ApmGuid };
            public static readonly string[] LOOT = { FapGuid, FatGuid };
            public static readonly string[] BUILDING = { FapGuid };
            public static readonly string[] RPGCLASSES = { RpgClassesGuid };
            public static readonly string[] COMPANIONS = { CompanionsGuid, RpgMakerGuid };
            public static readonly string[] GROUPHUD = { CompanionsGuid, RpgMakerGuid, GuildsGuid };
            public static readonly string[] GROUNDRINGS = { CompanionsGuid, RpgClassesGuid, FmoGuid };
            public static readonly string[] VAULT = { GuildsGuid, RpgMakerGuid, FapGuid };
            // The shared NPC brain (combat AI, speech, death memory, fishing, container registry) serves companions, RPGmaker NPCs,
            // FDT's test bots and FMO's agents.
            // 0.2.964: auto-on consumers (AutoOnWith). Snow's indoor rule serves FAT's voxel snow and Vedr's seasons; the measured monster armour
            // fit serves FMO's monster gear.
            public static readonly string[] SNOW = { FatGuid, "Norger.Vedr" };
            public static readonly string[] ARMORFIT = { FmoGuid };
            // the far view (FarLands moved up, 11-08): FAT feeds the far forest and terrain, FAP its far pieces
            public static readonly string[] FAR = { FatGuid, FapGuid };
            public static readonly string[] NPCBRAIN = { CompanionsGuid, RpgMakerGuid, "com.Fire.FiresDebugginTools", FmoGuid };
        }

        private sealed class Gated
        {
            public ConfigEntryBase Entry;
            public string[] Owners;
            public bool Synced;
            public bool Done;
        }

        private static readonly List<Gated> s_gated = new List<Gated>();

        /// <summary>Registers <paramref name="entry"/> as owned by <paramref name="owners"/>; returns the entry for inline use.</summary>
        public static T Gate<T>(T entry, string[] owners, bool synced) where T : ConfigEntryBase
        {
            if (entry == null || owners == null || owners.Length == 0) return entry;
            lock (s_gated)
            {
                foreach (var gated in s_gated)
                    if (ReferenceEquals(gated.Entry, entry)) return entry;
                s_gated.Add(new Gated { Entry = entry, Owners = owners, Synced = synced });
            }
            return entry;
        }

        /// <summary>Gates every listed entry the same way.</summary>
        public static void Gate(string[] owners, bool synced, params ConfigEntryBase[] entries)
        {
            if (entries == null) return;
            foreach (var entry in entries) Gate(entry, owners, synced);
        }

        private static bool AnyLoaded(string[] owners)
        {
            foreach (string guid in owners)
                if (guid != null && Chainloader.PluginInfos.ContainsKey(guid)) return true;
            return false;
        }

        /// <summary>
        /// After every plugin's Awake: removes the absent owners' local keys and tags their synced keys Advanced, saves each
        /// touched ConfigFile once, logs one Debug line. Runs only the entries not handled yet, so calling it again is harmless.
        /// </summary>
        public static void Apply()
        {
            ApplyAutoOn();
            List<Gated> pending;
            lock (s_gated) pending = s_gated.Where(g => !g.Done).ToList();
            if (pending.Count == 0) return;

            int skipped = 0, advanced = 0;
            var absent = new SortedSet<string>(StringComparer.Ordinal);
            var touched = new HashSet<ConfigFile>();
            foreach (var gated in pending)
            {
                gated.Done = true;
                try
                {
                    if (AnyLoaded(gated.Owners)) continue;
                    foreach (string guid in gated.Owners) absent.Add(guid);
                    var file = gated.Entry.ConfigFile;
                    if (gated.Synced)
                    {
                        // A synced entry leaves the
                        // file and the UIs too. The ConfigEntry stays bound and registered with ConfigSync, so a server's value still
                        // reaches it in memory; BepInEx's Save writes only the file's own entries, so it never comes back to the file.
                        MarkAdvanced(gated.Entry);
                        if (file != null && Remove(file, gated.Entry.Definition))
                        {
                            advanced++;
                            touched.Add(file);
                            WatchSynced(file, gated.Entry);
                        }
                    }
                    else if (file != null && Remove(file, gated.Entry.Definition))
                    {
                        skipped++;
                        touched.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    FiresCore.FiresUnifiedCore.Log?.LogDebug($"[OwnerGatedConfig] [{gated.Entry?.Definition?.Section}] {gated.Entry?.Definition?.Key}: {ex.Message}");
                }
            }

            foreach (var file in touched)
            {
                try { file.Save(); }
                catch (Exception ex) { FiresCore.FiresUnifiedCore.Log?.LogDebug($"[OwnerGatedConfig] save failed: {ex.Message}"); }
            }
            if (skipped > 0 || advanced > 0) FiresCore.UI.CfgDiscovery.Invalidate();

            FiresCore.FiresUnifiedCore.Log?.LogDebug($"[OwnerGatedConfig] removed {skipped} local + {advanced} synced keys from the cfg (absent: {(absent.Count == 0 ? "none" : string.Join(", ", absent.ToArray()))})");
        }

        // 0.2.964 (Fire 10-28, via [lead]: "None of these should be on by default considering they are all meant to control other mods
        // that core uses. They should all be off unless those mods are installed"): a Core key that serves a consumer mod stays default
        // OFF and is switched ON once, at the first load that finds its consumer installed. A hidden marker ([Auto defaults] "<Section> -
        // <Key>") records that, so the player's later choice, on or off, is kept and never forced again. Without the consumer nothing changes.
        private sealed class AutoOn
        {
            public ConfigEntry<bool> Entry;
            public string[] Owners;
            public bool Done;
        }

        private const string AutoSection = "Auto defaults";
        private static readonly List<AutoOn> s_autoOn = new List<AutoOn>();

        /// <summary>Registers <paramref name="entry"/> to switch ON once when any of <paramref name="owners"/> is installed; returns the entry.</summary>
        public static ConfigEntry<bool> AutoOnWith(ConfigEntry<bool> entry, string[] owners)
        {
            if (entry == null || owners == null || owners.Length == 0) return entry;
            lock (s_autoOn)
            {
                foreach (var auto in s_autoOn)
                    if (ReferenceEquals(auto.Entry, entry)) return entry;
                s_autoOn.Add(new AutoOn { Entry = entry, Owners = owners });
            }
            return entry;
        }

        private static void ApplyAutoOn()
        {
            List<AutoOn> pending;
            lock (s_autoOn) pending = s_autoOn.Where(a => !a.Done).ToList();
            if (pending.Count == 0) return;

            var turned = new List<string>();
            int kept = 0, absent = 0;
            var touched = new HashSet<ConfigFile>();
            foreach (var auto in pending)
            {
                auto.Done = true;
                try
                {
                    if (!AnyLoaded(auto.Owners)) { absent++; continue; }
                    var definition = auto.Entry.Definition;
                    var file = auto.Entry.ConfigFile;
                    if (file == null) continue;
                    var marker = file.Bind(AutoSection, $"{definition.Section} - {definition.Key}", false, new ConfigDescription(
                        "Set by Core once this setting was switched on because the mod it serves is installed. Leave it alone.", null,
                        new ConfigurationManagerAttributes { IsAdvanced = true, Browsable = false }));
                    if (marker.Value) { kept++; continue; }
                    if (!auto.Entry.Value)
                    {
                        auto.Entry.Value = true;
                        turned.Add($"[{definition.Section}] {definition.Key}");
                    }
                    marker.Value = true;
                    touched.Add(file);
                }
                catch (Exception ex)
                {
                    FiresCore.FiresUnifiedCore.Log?.LogDebug($"[OwnerGatedConfig] auto on [{auto.Entry?.Definition?.Section}] {auto.Entry?.Definition?.Key}: {ex.Message}");
                }
            }

            foreach (var file in touched)
            {
                try { file.Save(); }
                catch (Exception ex) { FiresCore.FiresUnifiedCore.Log?.LogDebug($"[OwnerGatedConfig] save failed: {ex.Message}"); }
            }

            FiresCore.FiresUnifiedCore.Log?.LogDebug($"[OwnerGatedConfig] auto on: switched on {turned.Count} ({(turned.Count == 0 ? "none" : string.Join(", ", turned.ToArray()))}) "
                + $"because their consumer mod is installed; {kept} already decided earlier (player's value kept); {absent} with no consumer installed (left as is)");
        }

        // The edge case: a client without the owner joins a server with it. ServerSync sets the removed entry's
        // value; that raises the file's SettingChanged (and a Save that no longer writes the entry). Logged once per key at Debug.
        private static readonly HashSet<ConfigEntryBase> s_removedSynced = new HashSet<ConfigEntryBase>();
        private static readonly HashSet<ConfigFile> s_watchedFiles = new HashSet<ConfigFile>();
        private static readonly HashSet<ConfigEntryBase> s_arrivalLogged = new HashSet<ConfigEntryBase>();

        private static void WatchSynced(ConfigFile file, ConfigEntryBase entry)
        {
            s_removedSynced.Add(entry);
            if (!s_watchedFiles.Add(file)) return;
            file.SettingChanged += (sender, args) =>
            {
                var changed = args?.ChangedSetting;
                if (changed == null || !s_removedSynced.Contains(changed) || !s_arrivalLogged.Add(changed)) return;
                FiresCore.FiresUnifiedCore.Log?.LogDebug($"[OwnerGatedConfig] [{changed.Definition.Section}] {changed.Definition.Key} = {changed.BoxedValue} "
                    + "arrived (server value, in memory only): owner absent here, so the feature stays inert and the key is not written to the cfg");
            };
        }

        private static bool Remove(ConfigFile file, ConfigDefinition definition)
        {
            bool removed = file.Remove(definition);
            // A later Save writes OrphanedEntries back out; drop the key there too so it leaves the .cfg.
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                object orphans = typeof(ConfigFile).GetProperty("OrphanedEntries", flags)?.GetValue(file, null)
                                 ?? typeof(ConfigFile).GetField("OrphanedEntries", flags)?.GetValue(file)
                                 ?? typeof(ConfigFile).GetField("<OrphanedEntries>k__BackingField", flags)?.GetValue(file);
                if (orphans is IDictionary dictionary && dictionary.Contains(definition))
                {
                    dictionary.Remove(definition);
                    removed = true;
                }
            }
            catch { }
            return removed;
        }

        // The same attribute object ConfigSync puts in the Tags (it reads ReadOnly from it): IsAdvanced on it reaches both the
        // Fires window (CfgTags) and ConfigurationManager, which copy attribute members by name.
        private static bool MarkAdvanced(ConfigEntryBase entry)
        {
            var description = entry.Description;
            if (description == null) return false;
            var tags = description.Tags ?? Array.Empty<object>();
            var attribute = tags.OfType<ConfigurationManagerAttributes>().FirstOrDefault();
            if (attribute == null)
            {
                attribute = new ConfigurationManagerAttributes();
                AccessTools.Field(typeof(ConfigDescription), "<Tags>k__BackingField")
                    ?.SetValue(description, new object[] { attribute }.Concat(tags).ToArray());
            }
            attribute.IsAdvanced = true;
            return true;
        }
    }
}
