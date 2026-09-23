using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        FlagsEnum,
        ValueList,
        Color,
        KeyBind,
        String,
        Vector2,
        Vector3,
        Vector4,
        Quaternion,
        Serialized,
        Unsupported,
    }

    /// <summary>
    /// One config setting, flattened to everything the config window needs. Type, label, range, options and
    /// control kind are reflected ONCE at discovery (never per-frame); the live value, and the read-only flag
    /// that ConfigSync toggles as lock/admin state moves, are read each draw.
    /// </summary>
    public sealed class CfgDescriptor
    {
        public string ModGuid;
        public string ModName;
        public bool IsFires;
        public string Section;
        public string SectionDisplay;
        public string Key;
        public string Label;
        public Type Type;
        public string Description;
        public string DescriptionLine;
        public CtrlKind Kind;
        public double Min;
        public double Max;
        public double Step;
        public string[] Options;
        public object[] OptionValues;
        public object Default;
        public int Order;
        public ConfigEntryBase Entry;
        public BepInEx.Configuration.TypeConverter Converter;

        public CfgTags Tags = CfgTags.None;
        private ConfigDescription _tagsReadFrom;

        public object BoxedValue => Entry.BoxedValue;

        public bool IsLocked => Tags.IsReadOnlyNow();

        /// <summary>
        /// FiresConfigVisibility swaps in a whole new ConfigDescription when it retags an entry (advanced /
        /// admin-only), so a cached tag read goes stale by reference rather than in place.
        /// </summary>
        public void RefreshTags()
        {
            var description = Entry.Description;
            if (ReferenceEquals(description, _tagsReadFrom)) return;
            _tagsReadFrom = description;
            Tags = CfgTags.Read(description);
        }

        internal void SetTags(ConfigDescription description, CfgTags tags)
        {
            _tagsReadFrom = description;
            Tags = tags;
        }
    }

    /// <summary>
    /// Walks every loaded plugin's live <see cref="ConfigFile"/> and builds the cached descriptor list that
    /// drives the config window, honouring the same ConfigurationManager annotations a mod already ships
    /// (Browsable, ReadOnly, Advanced, Order, Category, DispName). Rebuild() is called on window-open and on
    /// world-start, since plugins bind configs well after Core.Setup.
    /// </summary>
    public static class CfgDiscovery
    {
        public const string FiresGuidPrefix = "com.Fire.";
        private const string BepInExModName = "BepInEx";
        private const int MaxSectionPrefixLength = 5;
        private const double DefaultFloatStep = 0.01;
        private const double StepsPerRange = 100.0;

        private static readonly List<CfgDescriptor> s_descriptors = new List<CfgDescriptor>();
        public static IReadOnlyList<CfgDescriptor> Descriptors => s_descriptors;

        private static readonly List<string> s_modNames = new List<string>();
        private static readonly Dictionary<string, List<string>> s_sectionsByMod = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<CfgDescriptor>> s_rowsBySection = new Dictionary<string, List<CfgDescriptor>>(StringComparer.Ordinal);
        private static readonly List<CfgDescriptor> s_noRows = new List<CfgDescriptor>();

        public static IReadOnlyList<string> ModNames => s_modNames;

        public static IReadOnlyList<string> SectionsOf(string modName)
            => modName != null && s_sectionsByMod.TryGetValue(modName, out var list) ? list : (IReadOnlyList<string>)Array.Empty<string>();

        public static IReadOnlyList<CfgDescriptor> RowsOf(string modName, string section)
            => s_rowsBySection.TryGetValue(RowKey(modName, section), out var list) ? list : s_noRows;

        private static string RowKey(string modName, string section) => (modName ?? "") + "|" + (section ?? "");

        private static readonly Dictionary<ConfigFile, string> s_nameOverrides = new Dictionary<ConfigFile, string>();

        public static int ModCount { get; private set; }

        public static void SetNameOverride(ConfigFile config, string modName)
        {
            if (config == null) return;
            s_nameOverrides[config] = modName;
        }

        /// <summary>True when the window is filtered down to the Fires family instead of every loaded plugin.</summary>
        public static bool FiresOnly => FiresConfigUI.CfgFiresOnly != null && FiresConfigUI.CfgFiresOnly.Value;

        /// <summary>Re-enumerate every live ConfigFile and rebuild the cached descriptor and nav lists.</summary>
        public static void Rebuild()
        {
            s_descriptors.Clear();

            var seen = new HashSet<ConfigFile>();
            var sources = new List<PluginSource>();
            CollectSources(sources, seen);

            bool firesOnly = FiresOnly;
            int mods = 0;
            foreach (var source in sources)
            {
                if (firesOnly && !source.IsFires) continue;
                int before = s_descriptors.Count;
                try { AddConfig(source); }
                catch (Exception ex) { FiresConfigUI.Log.LogWarning($"skipped '{source.Name}': {ex.Message}"); }
                if (s_descriptors.Count > before) mods++;
            }
            ModCount = mods;
            BuildNav();
        }

        private struct PluginSource
        {
            public string Guid;
            public string Name;
            public ConfigFile Config;
            public bool IsFires;
            public bool AllAdvanced;
        }

        private static void BuildNav()
        {
            s_modNames.Clear();
            s_sectionsByMod.Clear();
            s_rowsBySection.Clear();

            var sectionsByMod = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var firesMods = new List<string>();
            var otherMods = new List<string>();

            foreach (var descriptor in s_descriptors)
            {
                if (!sectionsByMod.TryGetValue(descriptor.ModName, out var sections))
                {
                    sections = new SortedSet<string>(NaturalComparer.Instance);
                    sectionsByMod[descriptor.ModName] = sections;
                    (descriptor.IsFires ? firesMods : otherMods).Add(descriptor.ModName);
                }
                sections.Add(descriptor.Section);

                string rowKey = RowKey(descriptor.ModName, descriptor.Section);
                if (!s_rowsBySection.TryGetValue(rowKey, out var rows))
                {
                    rows = new List<CfgDescriptor>();
                    s_rowsBySection[rowKey] = rows;
                }
                rows.Add(descriptor);
            }

            firesMods.Sort(StringComparer.OrdinalIgnoreCase);
            otherMods.Sort(StringComparer.OrdinalIgnoreCase);
            s_modNames.AddRange(firesMods);
            s_modNames.AddRange(otherMods);

            foreach (var modName in s_modNames)
                s_sectionsByMod[modName] = new List<string>(sectionsByMod[modName]);

            foreach (var rows in s_rowsBySection.Values) rows.Sort(CompareRows);
        }

        // ConfigurationManager's within-category order: explicit Order first (descending), then by label.
        private static int CompareRows(CfgDescriptor a, CfgDescriptor b)
        {
            if (a.Order != b.Order) return b.Order.CompareTo(a.Order);
            return NaturalComparer.Instance.Compare(a.Label, b.Label);
        }

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
                        int numberStartA = i, numberStartB = j;
                        while (i < a.Length && char.IsDigit(a[i])) i++;
                        while (j < b.Length && char.IsDigit(b[j])) j++;
                        string numberA = a.Substring(numberStartA, i - numberStartA).TrimStart('0');
                        string numberB = b.Substring(numberStartB, j - numberStartB).TrimStart('0');
                        if (numberA.Length != numberB.Length) return numberA.Length - numberB.Length;
                        int comparison = string.CompareOrdinal(numberA, numberB);
                        if (comparison != 0) return comparison;
                    }
                    else
                    {
                        int comparison = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                        if (comparison != 0) return comparison;
                        i++; j++;
                    }
                }
                return (a.Length - i) - (b.Length - j);
            }
        }

        private static void CollectSources(List<PluginSource> sources, HashSet<ConfigFile> seen)
        {
            foreach (var kvp in FiresMod.Instances)
            {
                var plugin = kvp.Value;
                if (plugin == null || plugin.Config == null) continue;
                if (!seen.Add(plugin.Config)) continue;
                sources.Add(SourceOf(plugin.Info.Metadata.GUID, plugin.Info.Metadata.Name, plugin));
            }

            foreach (var kvp in Chainloader.PluginInfos)
            {
                var info = kvp.Value;
                var plugin = info?.Instance;
                if (plugin == null || plugin.Config == null || plugin.Config.Count == 0) continue;
                if (IsPluginUnbrowsable(plugin)) continue;
                if (!seen.Add(plugin.Config)) continue;
                sources.Add(SourceOf(info.Metadata.GUID, info.Metadata.Name, plugin));
            }

            AddBepInExCoreConfig(sources, seen);
        }

        private static PluginSource SourceOf(string guid, string name, BaseUnityPlugin plugin) => new PluginSource
        {
            Guid = guid,
            Name = ResolveName(plugin.Config, name),
            Config = plugin.Config,
            IsFires = guid != null && guid.StartsWith(FiresGuidPrefix, StringComparison.OrdinalIgnoreCase),
        };

        private static bool IsPluginUnbrowsable(BaseUnityPlugin plugin)
        {
            foreach (BrowsableAttribute attribute in plugin.GetType().GetCustomAttributes(typeof(BrowsableAttribute), false))
                if (!attribute.Browsable) return true;
            return false;
        }

        // BepInEx's own logging/chainloader settings live on a private static ConfigFile that no plugin owns;
        // ConfigurationManager surfaces them as a "BepInEx" entry and so do we.
        private static void AddBepInExCoreConfig(List<PluginSource> sources, HashSet<ConfigFile> seen)
        {
            try
            {
                var coreConfigProperty = typeof(ConfigFile).GetProperty("CoreConfig", BindingFlags.Static | BindingFlags.NonPublic);
                if (!(coreConfigProperty?.GetValue(null, null) is ConfigFile coreConfig)) return;
                if (!seen.Add(coreConfig)) return;
                sources.Add(new PluginSource
                {
                    Guid = BepInExModName,
                    Name = BepInExModName,
                    Config = coreConfig,
                    IsFires = false,
                    AllAdvanced = true,
                });
            }
            catch { }
        }

        private static string ResolveName(ConfigFile config, string fallback)
        {
            if (config != null && s_nameOverrides.TryGetValue(config, out var name) && !string.IsNullOrEmpty(name)) return name;
            return string.IsNullOrEmpty(fallback) ? "Mod" : fallback;
        }

        private static void AddConfig(PluginSource source)
        {
            foreach (var definition in source.Config.Keys)
            {
                ConfigEntryBase entry;
                try { entry = source.Config[definition]; }
                catch { continue; }
                if (entry == null) continue;

                var description = entry.Description;
                var tags = CfgTags.Read(description);
                if (!tags.Browsable) continue;
                if (source.AllAdvanced && !tags.IsAdvanced)
                {
                    if (ReferenceEquals(tags, CfgTags.None)) tags = new CfgTags();
                    tags.IsAdvanced = true;
                }

                var descriptor = Describe(source, definition, entry, tags);
                descriptor.SetTags(description, tags);
                s_descriptors.Add(descriptor);
            }
        }

        private static CfgDescriptor Describe(PluginSource source, ConfigDefinition definition, ConfigEntryBase entry, CfgTags tags)
        {
            Type settingType = entry.SettingType;
            string description = tags.Description ?? entry.Description?.Description ?? "";

            GetAcceptable(entry, out bool hasRange, out double min, out double max, out object[] listValues);
            string[] options = listValues != null ? ToDisplayNames(listValues) : null;
            if (options == null && settingType.IsEnum)
            {
                listValues = ToObjectArray(Enum.GetValues(settingType));
                options = ToDisplayNames(listValues);
            }

            CtrlKind kind = ResolveKind(settingType, hasRange, options);
            double step = kind == CtrlKind.IntRange ? 1.0 : kind == CtrlKind.FloatRange ? NiceStep(min, max) : 0.0;
            string section = tags.Category ?? definition.Section;

            return new CfgDescriptor
            {
                ModGuid = source.Guid,
                ModName = source.Name,
                IsFires = source.IsFires,
                Section = section,
                SectionDisplay = CleanSection(section),
                Key = definition.Key,
                Label = tags.DispName ?? Prettify(definition.Key),
                Type = settingType,
                Description = description,
                DescriptionLine = FirstLine(description),
                Kind = kind,
                Min = min,
                Max = max,
                Step = step,
                Options = options,
                OptionValues = listValues,
                Default = tags.DefaultValue ?? entry.DefaultValue,
                Order = tags.Order,
                Entry = entry,
                Converter = kind == CtrlKind.Serialized ? TomlTypeConverter.GetConverter(settingType) : null,
            };
        }

        private static CtrlKind ResolveKind(Type settingType, bool hasRange, string[] options)
        {
            if (settingType == typeof(bool)) return CtrlKind.Bool;
            // KeyBind BEFORE the enum check — KeyCode IS an enum, and classifying it as Enum gives the
            // cycle-one-value-per-click widget instead of the click-then-press-a-key recorder.
            if (settingType == typeof(KeyboardShortcut) || settingType == typeof(KeyCode)) return CtrlKind.KeyBind;
            if (settingType.IsEnum)
                return settingType.IsDefined(typeof(FlagsAttribute), false) ? CtrlKind.FlagsEnum : CtrlKind.Enum;
            if (options != null && options.Length > 0) return CtrlKind.ValueList;
            if (settingType == typeof(Color)) return CtrlKind.Color;
            if (settingType == typeof(Vector2)) return CtrlKind.Vector2;
            if (settingType == typeof(Vector3)) return CtrlKind.Vector3;
            if (settingType == typeof(Vector4)) return CtrlKind.Vector4;
            if (settingType == typeof(Quaternion)) return CtrlKind.Quaternion;
            if (IsInteger(settingType)) return hasRange ? CtrlKind.IntRange : CtrlKind.IntField;
            if (IsFloating(settingType)) return hasRange ? CtrlKind.FloatRange : CtrlKind.FloatField;
            if (settingType == typeof(string)) return CtrlKind.String;
            return TomlTypeConverter.CanConvert(settingType) ? CtrlKind.Serialized : CtrlKind.Unsupported;
        }

        private static bool IsInteger(Type valueType)
            => valueType == typeof(int) || valueType == typeof(long) || valueType == typeof(short) || valueType == typeof(byte)
            || valueType == typeof(uint) || valueType == typeof(ulong) || valueType == typeof(ushort) || valueType == typeof(sbyte);

        private static bool IsFloating(Type valueType)
            => valueType == typeof(float) || valueType == typeof(double) || valueType == typeof(decimal);

        private static object[] ToObjectArray(Array values)
        {
            var result = new object[values.Length];
            for (int i = 0; i < values.Length; i++) result[i] = values.GetValue(i);
            return result;
        }

        // An enum member decorated with [Description] shows that text instead of its identifier, matching
        // ConfigurationManager so a mod's curated option labels survive.
        private static string[] ToDisplayNames(object[] values)
        {
            var names = new string[values.Length];
            for (int i = 0; i < values.Length; i++) names[i] = DisplayNameOf(values[i]);
            return names;
        }

        private static string DisplayNameOf(object value)
        {
            if (value == null) return "";
            if (!(value is Enum)) return value.ToString();
            try
            {
                var member = value.GetType().GetMember(value.ToString());
                if (member.Length > 0)
                    foreach (DescriptionAttribute attribute in member[0].GetCustomAttributes(typeof(DescriptionAttribute), false))
                        return attribute.Description;
            }
            catch { }
            return value.ToString();
        }

        private static void GetAcceptable(ConfigEntryBase entry, out bool hasRange, out double min, out double max, out object[] values)
        {
            hasRange = false; min = 0.0; max = 1.0; values = null;
            var acceptableValues = entry.Description?.AcceptableValues;
            if (acceptableValues == null) return;
            try
            {
                var type = acceptableValues.GetType();
                if (type.GetProperty("AcceptableValues", BindingFlags.Instance | BindingFlags.Public)?.GetValue(acceptableValues, null) is Array list)
                {
                    values = ToObjectArray(list);
                    return;
                }
                var minValue = type.GetProperty("MinValue", BindingFlags.Instance | BindingFlags.Public)?.GetValue(acceptableValues, null);
                var maxValue = type.GetProperty("MaxValue", BindingFlags.Instance | BindingFlags.Public)?.GetValue(acceptableValues, null);
                if (minValue == null || maxValue == null) return;
                min = Convert.ToDouble(minValue, CultureInfo.InvariantCulture);
                max = Convert.ToDouble(maxValue, CultureInfo.InvariantCulture);
                hasRange = max > min;
            }
            catch { }
        }

        private static double NiceStep(double min, double max)
        {
            double range = Math.Abs(max - min);
            if (range <= 0) return DefaultFloatStep;
            double raw = range / StepsPerRange;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double normalized = raw / magnitude;
            double nice = normalized < 1.5 ? 1 : normalized < 3.5 ? 2 : normalized < 7.5 ? 5 : 10;
            return nice * magnitude;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int newline = text.IndexOf('\n');
            return newline >= 0 ? text.Substring(0, newline).TrimEnd() : text;
        }

        private static string Prettify(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            var builder = new StringBuilder(key.Length + 6);
            for (int i = 0; i < key.Length; i++)
            {
                char character = key[i];
                if (character == '_') { builder.Append(' '); continue; }
                if (i > 0 && char.IsUpper(character) && (!char.IsUpper(key[i - 1]) || (i + 1 < key.Length && char.IsLower(key[i + 1]))))
                    builder.Append(' ');
                builder.Append(character);
            }
            return builder.ToString();
        }

        /// <summary>Strip a leading "NN - " ordering prefix from a section for display.</summary>
        public static string CleanSection(string section)
        {
            if (string.IsNullOrEmpty(section)) return section;
            int dash = section.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash <= MaxSectionPrefixLength && IsOrderingPrefix(section.Substring(0, dash)))
                return section.Substring(dash + 3);
            return section;
        }

        private static bool IsOrderingPrefix(string prefix)
        {
            bool digit = false;
            foreach (char character in prefix)
            {
                if (char.IsDigit(character)) { digit = true; continue; }
                if (char.IsLetter(character)) continue;
                return false;
            }
            return digit;
        }

        /// <summary>Log "(N mods, M entries)" plus a per-mod count, for the va_config_dump console command.</summary>
        public static void DumpToLog()
        {
            Rebuild();
            var perMod = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var descriptor in s_descriptors)
            {
                perMod.TryGetValue(descriptor.ModName, out int count);
                perMod[descriptor.ModName] = count + 1;
            }
            FiresConfigUI.Log.LogInfo($"va_config_dump: ({ModCount} mods, {s_descriptors.Count} entries){(FiresOnly ? " [Fires only]" : "")}");
            foreach (var modName in s_modNames)
                FiresConfigUI.Log.LogInfo($"  {modName}: {perMod[modName]} entries");
        }
    }
}
