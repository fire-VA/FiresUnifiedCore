using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Logging;
using HarmonyLib;

namespace FiresCore.Movement
{
    /// <summary>
    /// Every custom animation / movement feature in Core lives in one
    /// [Movement] section, advanced-gated, tri-state Auto / On / Off. Auto = active only once a consumer has registered for it AT
    /// RUNTIME (<see cref="Register"/>), or (features that had owners before) a post-load CoreFeatures owner is installed. The first
    /// key is a master switch: off = every feature off, plain vanilla movement. Contract: add-only; feature names are stable.
    /// </summary>
    public sealed class MovementFeature
    {
        public const string Section = "Movement";
        public const string Climbing = "Climbing", ClipSlots = "Clip slots", ClimbingSkill = "Climbing skill",
            DoubleJump = "Double jump", RunSlide = "Run slide", MovementStyles = "Movement styles";

        private static ConfigEntry<bool> s_master;
        private static readonly Dictionary<string, HashSet<string>> s_consumers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private static bool s_masterLogged;

        /// <summary>Raised when a consumer registers (feature name): cached "is it on" answers re-resolve.</summary>
        public static event Action<string> Changed;

        internal static ConfigDescription Advanced(string text, AcceptableValueBase range = null) =>
            new ConfigDescription(text, range, new FiresCore.Sync.ConfigurationManagerAttributes { IsAdvanced = true });

        /// <summary>Binds the master switch; call before any [Movement] feature key so it is the first one written.</summary>
        internal static void Bind(ConfigFile config)
        {
            if (s_master != null) return;
            s_master = config.Bind(Section, "0 Custom movement (master)", true,
                Advanced("Master switch for every custom movement and animation feature in this section (climbing, clip slots, double jump, run slide, "
                    + "climbing skill, movement styles). Off: all of them are off, whatever their own setting says, and movement is plain vanilla."));
            s_master.SettingChanged += (_, __) => { s_masterLogged = false; Changed?.Invoke(null); };
        }

        public static bool MasterOn
        {
            get
            {
                bool on = s_master == null || s_master.Value;
                if (!on && !s_masterLogged) { s_masterLogged = true; FiresLogger.LogInfo("[Movement] master OFF: all custom movement disabled"); }
                return on;
            }
        }

        /// <summary>A consumer mod says it uses <paramref name="feature"/> (call at load, on every peer). Idempotent.</summary>
        public static void Register(string feature, string consumer)
        {
            if (string.IsNullOrEmpty(feature)) return;
            if (!s_consumers.TryGetValue(feature, out var set)) s_consumers[feature] = set = new HashSet<string>(StringComparer.Ordinal);
            if (set.Add(string.IsNullOrEmpty(consumer) ? "?" : consumer))
            {
                FiresLogger.LogDebug($"[Movement] {feature}: consumer {consumer} registered");
                Changed?.Invoke(feature);
            }
        }

        public static bool HasConsumer(string feature) => s_consumers.TryGetValue(feature, out var set) && set.Count > 0;
        public static string ConsumersOf(string feature) => s_consumers.TryGetValue(feature, out var set) ? string.Join(", ", set) : "";

        /// <summary>
        /// Moves the values of <paramref name="keys"/> from an old section into [Movement] (same key names) the first time the new keys
        /// are bound: call BEFORE binding them. Only a key the file does not already have under [Movement] takes the old value; the old
        /// entries are dropped; never a silent reset. Returns the (key, old value) pairs it will apply after the bind.
        /// </summary>
        internal static Dictionary<string, string> TakeOldValues(ConfigFile config, string oldSection, params string[] keys)
        {
            var taken = new Dictionary<string, string>(StringComparer.Ordinal);
            var orphans = Orphans(config);
            if (orphans == null) return taken;
            foreach (string key in keys)
            {
                var oldDef = new ConfigDefinition(oldSection, key);
                var newDef = new ConfigDefinition(Section, key);
                if (!orphans.TryGetValue(oldDef, out string value)) continue;
                orphans.Remove(oldDef);
                if (orphans.ContainsKey(newDef)) continue;   // the file already has the new key: keep it, never overwrite
                taken[key] = value;
            }
            return taken;
        }

        /// <summary>Applies <see cref="TakeOldValues"/>'s values to the freshly bound entries, saves, logs the migration once.</summary>
        internal static void ApplyOldValues(ConfigFile config, Dictionary<string, string> taken, string oldSection, params ConfigEntryBase[] entries)
        {
            if (taken == null || taken.Count == 0) return;
            var applied = new List<string>();
            foreach (var entry in entries)
            {
                if (entry == null || !taken.TryGetValue(entry.Definition.Key, out string value)) continue;
                try { entry.SetSerializedValue(value); applied.Add($"{entry.Definition.Key}={value}"); }
                catch (Exception ex) { FiresLogger.LogWarning($"[Movement] could not migrate {oldSection}.{entry.Definition.Key}='{value}': {ex.Message} (kept the default)"); }
            }
            try { config.Save(); } catch { }
            FiresLogger.LogInfo($"[Movement] migrated {applied.Count} key(s) from [{oldSection}]: {string.Join(", ", applied.ToArray())}");
        }

        // ---- instance API (CoreMovement's features) ----
        public enum Mode { Auto, On, Off }
        public readonly string Name;
        private ConfigEntry<Mode> _key;
        private string _lastState;

        public MovementFeature(string name) { Name = name; }

        public static void BindMaster(ConfigFile cfg) => Bind(cfg);

        public void Bind(ConfigFile cfg, string description)
        {
            Bind(cfg);
            if (_key != null) return;
            _key = cfg.Bind(Section, Name, Mode.Auto, Advanced(description + " Auto = on only when a mod registers for it at runtime; On / Off force it."));
            _key.SettingChanged += (_, __) => Changed?.Invoke(Name);
        }

        /// <summary>A mod that uses this feature (its plugin name, for the log).</summary>
        public void MarkConsumer(string who) { if (!string.IsNullOrEmpty(who) && who != "Core") Register(Name, who); }

        public bool IsActive
        {
            get
            {
                if (!MasterOn) return false;
                Mode m = _key != null ? _key.Value : Mode.Auto;
                return m == Mode.On || (m == Mode.Auto && HasConsumer(Name));
            }
        }

        /// <summary>World-load line: "[Movement] &lt;feature&gt;: Auto -&gt; active (consumer X)" / "-&gt; off (no consumer)" / "On/Off (forced)".</summary>
        public void LogState()
        {
            Mode m = _key != null ? _key.Value : Mode.Auto;
            string st = !MasterOn ? "off (master OFF)"
                : m != Mode.Auto ? $"{m} (forced)"
                : HasConsumer(Name) ? $"Auto -> active (consumer {ConsumersOf(Name)})" : "Auto -> off (no consumer)";
            if (st == _lastState) return;
            _lastState = st;
            FiresLogger.LogInfo($"[Movement] {Name}: {st}");
        }

        internal static Dictionary<ConfigDefinition, string> Orphans(ConfigFile config)   // Internal (Climbing's split migration)
        {
            try { return AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(config) as Dictionary<ConfigDefinition, string>; }
            catch { return null; }
        }
    }
}
