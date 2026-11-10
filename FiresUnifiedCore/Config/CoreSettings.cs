using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FiresCore.Logging;

namespace FiresCore.Config
{
    /// <summary>
    /// 0.2.1036 (Fire 10-30: "Core shouldn't even have configs for other mods ... The only configs that should be in core are legitimate
    /// core functions"; plan Tools\CORE_CONFIG_MOVE_PLAN_20261030.md, approved). A setting that belongs to another mod is bound THERE;
    /// that mod hands Core the live value with <see cref="Provide"/>, and Core's shared code reads it with <see cref="Get{T}"/>. No
    /// provider = the fallback the call site passes (OFF for a gameplay change, so Core alone changes nothing; the current default where
    /// the plan says so). Keys are (section, key), the exact names Core used, so a moved key keeps its name.
    /// Several mods may provide one key: the highest priority wins (ties: the first), e.g. FAT 10 over FiresSnowmen 1 for the snow keys;
    /// one Info line per replacement. Add-only API. Main thread.
    /// Migration helper: <see cref="TryReadCoreSaved"/> reads a value a player saved in Core's own cfg file (read-only, by section + key),
    /// for the owner's one-time copy into its own cfg. Core never writes it.
    /// </summary>
    public static class CoreSettings
    {
        private sealed class Provider { public Func<object> Getter; public string Owner; public int Priority; }
        private static readonly Dictionary<string, Provider> s_providers = new Dictionary<string, Provider>(StringComparer.Ordinal);
        private static readonly HashSet<string> s_failedOnce = new HashSet<string>(StringComparer.Ordinal);

        private static string K(string section, string key) => (section ?? "") + "/" + (key ?? "");

        /// <summary>Hands Core the live value of a setting this mod binds. True when this provider is now the one Core reads.</summary>
        public static bool Provide(string section, string key, Func<object> getter, string ownerMod, int priority = 0)
        {
            if (getter == null || string.IsNullOrEmpty(key)) return false;
            string k = K(section, key);
            if (s_providers.TryGetValue(k, out Provider had))
            {
                if (priority <= had.Priority && !string.Equals(had.Owner, ownerMod, StringComparison.Ordinal))
                {
                    FiresLogger.LogDebug($"[CoreSettings] {k}: {ownerMod} (priority {priority}) not used - {had.Owner} (priority {had.Priority}) provides it");
                    return false;
                }
                if (!string.Equals(had.Owner, ownerMod, StringComparison.Ordinal))
                    FiresLogger.LogInfo($"[CoreSettings] {k}: {ownerMod} replaces {had.Owner}");
            }
            s_providers[k] = new Provider { Getter = getter, Owner = ownerMod ?? "?", Priority = priority };
            s_evented.Remove(k);   // 0.2.1074: the ConfigEntry overload re-adds it; a getter provider has no change event
            FiresLogger.LogDebug($"[CoreSettings] {k} provided by {ownerMod} (priority {priority})");
            try { Provided?.Invoke(section, key, ownerMod); } catch (Exception e) { FiresLogger.LogDebug($"[CoreSettings] a Provided listener threw: {e.Message}"); }   // 0.2.1069
            RaiseChanged(section, key);   // 0.2.1074: a new provider can change the value
            return true;
        }

        /// <summary>0.2.1069 (add-only): raised after a provider takes a key (section, key, owner mod), so Core code that resolved a value early re-resolves.</summary>
        public static event Action<string, string, string> Provided;

        /// <summary>0.2.1074 (add-only, Fire's event-driven GO): hands Core a BepInEx ConfigEntry as the live value. Core subscribes its
        /// SettingChanged, which BepInEx raises for every path that sets the value: an in-game / config-manager edit, ConfigFile.Reload
        /// (file hot reload) and a ServerSync push (it sets Value). So <see cref="Changed"/> fires on every change of this key.</summary>
        public static bool Provide(string section, string key, BepInEx.Configuration.ConfigEntryBase entry, string ownerMod, int priority = 0)
        {
            if (entry == null) return false;
            if (!Provide(section, key, () => entry.BoxedValue, ownerMod, priority)) return false;
            string k = K(section, key);
            s_evented.Add(k);
            // BepInEx 5: ConfigEntryBase has no event; its file raises SettingChanged for every entry whose BoxedValue / Value is set
            entry.ConfigFile.SettingChanged += (_, e) =>
            {
                if (e?.ChangedSetting == entry && s_providers.TryGetValue(k, out Provider p) && string.Equals(p.Owner, ownerMod ?? "?", StringComparison.Ordinal)) RaiseChanged(section, key);
            };
            return true;
        }

        /// <summary>0.2.1074 (add-only): an owner that provides a plain getter calls this when its value changes.</summary>
        public static void NotifyChanged(string section, string key) => RaiseChanged(section, key);

        /// <summary>0.2.1074 (add-only): raised (section, key) when a provided value changes, and when a new provider takes a key. Complete
        /// only for keys provided as a ConfigEntry (<see cref="HasChangeEvent"/>); a getter-only key fires only if its owner calls
        /// <see cref="NotifyChanged"/>, so a cache of such a key must keep re-reading.</summary>
        public static event Action<string, string> Changed;

        /// <summary>0.2.1074: true when every change of this key raises <see cref="Changed"/> (provided as a ConfigEntry).</summary>
        public static bool HasChangeEvent(string section, string key) => s_evented.Contains(K(section, key)) && s_providers.ContainsKey(K(section, key));

        private static readonly HashSet<string> s_evented = new HashSet<string>(StringComparer.Ordinal);

        private static void RaiseChanged(string section, string key)
        {
            var h = Changed;
            if (h == null) return;
            foreach (Action<string, string> d in h.GetInvocationList())
            {
                try { d(section, key); } catch (Exception e) { FiresLogger.LogDebug($"[CoreSettings] a Changed listener threw: {e.Message}"); }
            }
        }

        public static bool IsProvided(string section, string key) => s_providers.ContainsKey(K(section, key));

        /// <summary>The mod whose value Core reads for this setting, or null.</summary>
        public static string ProviderOf(string section, string key) => s_providers.TryGetValue(K(section, key), out Provider p) ? p.Owner : null;

        /// <summary>The setting's live value from its provider, converted to T; <paramref name="fallback"/> with no provider (or a bad value).</summary>
        public static T Get<T>(string section, string key, T fallback)
        {
            string k = K(section, key);
            if (!s_providers.TryGetValue(k, out Provider p)) return fallback;
            try
            {
                object v = p.Getter();
                if (v is T t) return t;
                if (v == null) return fallback;
                return (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                if (s_failedOnce.Add(k)) FiresLogger.LogWarning($"[CoreSettings] {k}: {p.Owner}'s value could not be read ({e.GetType().Name}: {e.Message}); the fallback {fallback} is used");
                return fallback;
            }
        }

        /// <summary>A value saved in Core's own cfg (BepInEx\config\com.Fire.FiresUnifiedCore.cfg) under [section] key = value, read-only, for an
        /// owner's one-time migration. False when the file or the key isn't there.</summary>
        public static bool TryReadCoreSaved(string section, string key, out string value) =>
            TryReadSaved("com.Fire.FiresUnifiedCore.cfg", section, key, out value);

        /// <summary>0.2.1043 (add-only; Fires Advanced Player Movement's migration from the mod that provided a key before it, e.g. FAT):
        /// the same read-only lookup in any BepInEx\config file, by its file name (e.g. "com.Fire.FiresAdminTerrain.cfg").</summary>
        public static bool TryReadSaved(string cfgFileName, string section, string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(cfgFileName) || cfgFileName.IndexOfAny(new[] { '/', '\\' }) >= 0 || cfgFileName.Contains("..")) return false;   // a file name in BepInEx\config only
            try
            {
                string path = Path.Combine(BepInEx.Paths.ConfigPath, cfgFileName);
                if (!File.Exists(path)) return false;
                string current = null;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line[0] == '[' && line[line.Length - 1] == ']') { current = line.Substring(1, line.Length - 2); continue; }
                    if (!string.Equals(current, section, StringComparison.Ordinal)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.Ordinal)) continue;
                    value = line.Substring(eq + 1).Trim();
                    return true;
                }
            }
            catch (Exception e) { FiresLogger.LogDebug($"[CoreSettings] reading Core's saved {K(section, key)} failed: {e.Message}"); }
            return false;
        }
    }
}
