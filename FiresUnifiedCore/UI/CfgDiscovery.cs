using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using FiresCore.Lifecycle;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>The control to render for a setting, decided once at discovery from its CLR type / range / options.</summary>
    public enum CtrlKind
    {
        Bool,
        IntRange,
        FloatRange,
        IntField,
        FloatField,
        Enum,
        ValueList,
        Color,
        KeyBind,
        String,
        ReadOnly,
    }

    /// <summary>
    /// One config setting, flattened to everything the F8 window needs. Type, label, range, options and
    /// control kind are reflected ONCE at discovery (never per-frame); only <see cref="BoxedValue"/> is read
    /// live each draw. The server-lock / admin flags are present but left false this phase (P4 wires them).
    /// </summary>
    public sealed class CfgDescriptor
    {
        public string ModGuid;
        public string ModName;
        public string Section;
        public string SectionDisplay;
        public string Key;
        public string Label;
        public Type Type;
        public string Description;
        public CtrlKind Kind;
        public bool HasRange;
        public double Min;
        public double Max;
        public double Step;
        public string[] Options;
        public object Default;
        public ConfigEntryBase Entry;

        // Computed-but-false this phase; P4 wires real ConfigSync / admin detection.
        public bool IsServerSynced;
        public bool IsServerLocked;
        public bool IsAdminOnly;
        public bool ReadOnlyAttr;

        public object BoxedValue => Entry.BoxedValue;
    }

    /// <summary>
    /// Walks every loaded Fires mod's live <see cref="ConfigFile"/> and builds a cached <see cref="CfgDescriptor"/>
    /// list that drives the F8 window. Fires-only by default (GUID prefix <c>com.Fire.</c>); a "Show all plugins"
    /// toggle widens the source to every instantiated plugin. Rebuild() is cheap (reads live ConfigFiles) and is
    /// called lazily on window-open and on world-start, since some plugins bind configs after Core.Setup.
    /// </summary>
    public static class CfgDiscovery
    {
        public const string FiresGuidPrefix = "com.Fire.";

        private static readonly List<CfgDescriptor> s_descriptors = new List<CfgDescriptor>();
        public static IReadOnlyList<CfgDescriptor> Descriptors => s_descriptors;

        // Nav lists derived from the descriptors ONCE per Rebuild so OnGUI allocates nothing.
        // s_modNames = distinct mod names (discovery order); s_sectionsByMod = each mod's natural-sorted sections.
        private static readonly List<string> s_modNames = new List<string>();
        private static readonly Dictionary<string, List<string>> s_sectionsByMod = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public static IReadOnlyList<string> ModNames => s_modNames;

        public static IReadOnlyList<string> SectionsOf(string modName)
            => modName != null && s_sectionsByMod.TryGetValue(modName, out var list) ? list : (IReadOnlyList<string>)Array.Empty<string>();

        // Display-name override per ConfigFile, supplied by FiresConfigUI.Register so a curated name wins.
        private static readonly Dictionary<ConfigFile, string> s_nameOverrides = new Dictionary<ConfigFile, string>();

        public static int ModCount { get; private set; }

        public static void SetNameOverride(ConfigFile config, string modName)
        {
            if (config == null) return;
            s_nameOverrides[config] = modName;
        }

        /// <summary>True to source ALL instantiated plugins instead of Fires-only.</summary>
        public static bool ShowAll => FiresConfigUI.CfgShowAllPlugins != null && FiresConfigUI.CfgShowAllPlugins.Value;

        /// <summary>Re-enumerate every live ConfigFile and rebuild the cached descriptor list.</summary>
        public static void Rebuild()
        {
            s_descriptors.Clear();

            var seen = new HashSet<ConfigFile>();
            var sources = new List<(string guid, string name, ConfigFile config)>();
            CollectSources(sources, seen);

            int mods = 0;
            foreach (var src in sources)
            {
                int before = s_descriptors.Count;
                AddConfig(src.guid, ResolveName(src.config, src.name), src.config);
                if (s_descriptors.Count > before) mods++;
            }
            ModCount = mods;
            BuildNav();
        }

        // Distinct mod names (discovery order) + each mod's natural-sorted section list, cached so the
        // F8 window's DrawNav/DrawBody/InitSelection allocate nothing per frame. Invalidated only here
        // (called on window-open and world-start) and on a ShowAll flip (FiresConfigUI re-opens to refresh).
        private static void BuildNav()
        {
            s_modNames.Clear();
            s_sectionsByMod.Clear();
            var sectionSets = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var d in s_descriptors)
            {
                if (!sectionSets.TryGetValue(d.ModName, out var set))
                {
                    set = new SortedSet<string>(NaturalComparer.Instance);
                    sectionSets[d.ModName] = set;
                    s_modNames.Add(d.ModName);
                }
                set.Add(d.Section);
            }
            foreach (var name in s_modNames)
                s_sectionsByMod[name] = new List<string>(sectionSets[name]);
        }

        // Natural sort: compares embedded digit runs numerically so "06 - X" sorts after "1 - X".
        private sealed class NaturalComparer : IComparer<string>
        {
            public static readonly NaturalComparer Instance = new NaturalComparer();

            public int Compare(string a, string b)
            {
                if (a == null) return b == null ? 0 : -1;
                if (b == null) return 1;
                int i = 0, j = 0;
                while (i < a.Length && j < b.Length)
                {
                    if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                    {
                        int si = i, sj = j;
                        while (i < a.Length && char.IsDigit(a[i])) i++;
                        while (j < b.Length && char.IsDigit(b[j])) j++;
                        string na = a.Substring(si, i - si).TrimStart('0');
                        string nb = b.Substring(sj, j - sj).TrimStart('0');
                        if (na.Length != nb.Length) return na.Length - nb.Length;
                        int c = string.CompareOrdinal(na, nb);
                        if (c != 0) return c;
                    }
                    else
                    {
                        int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                        if (c != 0) return c;
                        i++; j++;
                    }
                }
                return (a.Length - i) - (b.Length - j);
            }
        }

        private static void CollectSources(List<(string, string, ConfigFile)> sources, HashSet<ConfigFile> seen)
        {
            if (ShowAll)
            {
                foreach (var kvp in Chainloader.PluginInfos)
                {
                    var info = kvp.Value;
                    var plugin = info?.Instance;
                    if (plugin == null) continue;
                    var cfg = plugin.Config;
                    if (cfg == null || cfg.Count == 0) continue;
                    if (!seen.Add(cfg)) continue;
                    sources.Add((info.Metadata.GUID, info.Metadata.Name, cfg));
                }
                return;
            }

            // Primary: every loaded FiresMod (each carries its plugin Config).
            foreach (var kvp in FiresMod.Instances)
            {
                var plugin = kvp.Value;
                if (plugin == null) continue;
                var cfg = plugin.Config;
                if (cfg == null) continue;
                if (!seen.Add(cfg)) continue;
                sources.Add((plugin.Info.Metadata.GUID, plugin.Info.Metadata.Name, cfg));
            }

            // Fallback: Fires-prefixed plugins not deriving from FiresMod that the primary pass missed.
            foreach (var kvp in Chainloader.PluginInfos)
            {
                var info = kvp.Value;
                var plugin = info?.Instance;
                if (plugin == null) continue;
                if (info.Metadata.GUID == null || !info.Metadata.GUID.StartsWith(FiresGuidPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var cfg = plugin.Config;
                if (cfg == null) continue;
                if (!seen.Add(cfg)) continue;
                sources.Add((info.Metadata.GUID, info.Metadata.Name, cfg));
            }
        }

        private static string ResolveName(ConfigFile config, string fallback)
        {
            if (config != null && s_nameOverrides.TryGetValue(config, out var name) && !string.IsNullOrEmpty(name)) return name;
            return string.IsNullOrEmpty(fallback) ? "Mod" : fallback;
        }

        private static void AddConfig(string guid, string modName, ConfigFile config)
        {
            foreach (var def in config.Keys)
            {
                ConfigEntryBase entry;
                try { entry = config[def]; }
                catch { continue; }
                if (entry == null) continue;
                s_descriptors.Add(Describe(guid, modName, def, entry));
            }
        }

        private static CfgDescriptor Describe(string guid, string modName, ConfigDefinition def, ConfigEntryBase entry)
        {
            Type t = entry.SettingType;
            string desc = entry.Description != null ? entry.Description.Description ?? "" : "";
            int nl = desc.IndexOf('\n');
            if (nl >= 0) desc = desc.Substring(0, nl);

            GetAcceptable(entry, out bool hasRange, out double min, out double max, out string[] listOptions);

            string[] options = listOptions;
            if (options == null && t.IsEnum) options = Enum.GetNames(t);

            CtrlKind kind = ResolveKind(t, hasRange, options);
            double step = 0.0;
            if (kind == CtrlKind.IntRange) step = 1.0;
            else if (kind == CtrlKind.FloatRange) step = NiceStep(min, max);

            return new CfgDescriptor
            {
                ModGuid = guid,
                ModName = modName,
                Section = def.Section,
                SectionDisplay = CleanSection(def.Section),
                Key = def.Key,
                Label = Prettify(def.Key),
                Type = t,
                Description = desc,
                Kind = kind,
                HasRange = hasRange,
                Min = min,
                Max = max,
                Step = step,
                Options = options,
                Default = entry.DefaultValue,
                Entry = entry,
            };
        }

        private static CtrlKind ResolveKind(Type t, bool hasRange, string[] options)
        {
            if (t == typeof(bool)) return CtrlKind.Bool;
            if (t.IsEnum) return CtrlKind.Enum;
            if (options != null && options.Length > 0) return CtrlKind.ValueList;
            if (t == typeof(Color)) return CtrlKind.Color;
            if (t == typeof(KeyboardShortcut) || t == typeof(KeyCode)) return CtrlKind.KeyBind;
            if (IsInteger(t)) return hasRange ? CtrlKind.IntRange : CtrlKind.IntField;
            if (IsFloating(t)) return hasRange ? CtrlKind.FloatRange : CtrlKind.FloatField;
            if (t == typeof(string)) return CtrlKind.String;
            return CtrlKind.ReadOnly;
        }

        private static bool IsInteger(Type t)
            => t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

        private static bool IsFloating(Type t)
            => t == typeof(float) || t == typeof(double);

        private static void GetAcceptable(ConfigEntryBase entry, out bool hasRange, out double min, out double max, out string[] options)
        {
            hasRange = false; min = 0.0; max = 1.0; options = null;
            var av = entry.Description != null ? entry.Description.AcceptableValues : null;
            if (av == null) return;
            try
            {
                var at = av.GetType();
                if (!at.IsGenericType) return;
                var gd = at.GetGenericTypeDefinition();
                if (gd == typeof(AcceptableValueRange<>))
                {
                    var lo = at.GetProperty("MinValue")?.GetValue(av);
                    var hi = at.GetProperty("MaxValue")?.GetValue(av);
                    if (lo != null && hi != null)
                    {
                        min = Convert.ToDouble(lo, CultureInfo.InvariantCulture);
                        max = Convert.ToDouble(hi, CultureInfo.InvariantCulture);
                        hasRange = max > min;
                    }
                }
                else if (gd == typeof(AcceptableValueList<>))
                {
                    if (at.GetProperty("AcceptableValues")?.GetValue(av) is Array arr)
                    {
                        options = new string[arr.Length];
                        for (int i = 0; i < arr.Length; i++) options[i] = arr.GetValue(i)?.ToString();
                    }
                }
            }
            catch { }
        }

        // 1 / 2 / 5 * 10^n step for a float range, mirroring SchemaExporter.Step.
        private static double NiceStep(double lo, double hi)
        {
            double range = Math.Abs(hi - lo);
            if (range <= 0) return 0.01;
            double raw = range / 100.0;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double norm = raw / mag;
            double nice = norm < 1.5 ? 1 : norm < 3.5 ? 2 : norm < 7.5 ? 5 : 10;
            return nice * mag;
        }

        // "AutoMaxDistance" -> "Auto Max Distance"; mirrors SchemaExporter.Prettify.
        private static string Prettify(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            var sb = new StringBuilder(key.Length + 6);
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c == '_') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && (!char.IsUpper(key[i - 1]) || (i + 1 < key.Length && char.IsLower(key[i + 1]))))
                    sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Strip a leading "NN - " ordering prefix from a section for display.
        private static string CleanSection(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int dash = s.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash <= 5 && IsOrderingPrefix(s.Substring(0, dash))) return s.Substring(dash + 3);
            return s;
        }

        private static bool IsOrderingPrefix(string p)
        {
            bool digit = false;
            foreach (char c in p)
            {
                if (char.IsDigit(c)) { digit = true; continue; }
                if (char.IsLetter(c)) continue;
                return false;
            }
            return digit;
        }

        /// <summary>Log "(N mods, M entries)" plus a per-mod count. P1 verification for va_config_dump.</summary>
        public static void DumpToLog()
        {
            Rebuild();
            var perMod = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var d in s_descriptors)
            {
                if (!perMod.ContainsKey(d.ModName)) { perMod[d.ModName] = 0; order.Add(d.ModName); }
                perMod[d.ModName]++;
            }
            FiresConfigUI.Log.LogInfo($"va_config_dump: ({ModCount} mods, {s_descriptors.Count} entries){(ShowAll ? " [show-all]" : "")}");
            foreach (var name in order)
                FiresConfigUI.Log.LogInfo($"  {name}: {perMod[name]} entries");
        }
    }
}
