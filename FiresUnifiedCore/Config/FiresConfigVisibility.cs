using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using FiresCore.Logging;

namespace FiresCore.Config
{
    /// <summary>
    /// Classifies ALREADY-BOUND config entries as advanced or read-only, so a mod with hundreds of settings
    /// declares the rule once instead of passing tags at every Bind call. Both the Fires window and
    /// ConfigurationManager read the plain string tags this writes ("Advanced" / "ReadOnly").
    ///
    /// Re-runnable: the tags a call does not ask for are removed, so flipping admin state back and forth
    /// never accumulates duplicates.
    /// </summary>
    public static class FiresConfigVisibility
    {
        public const string AdvancedTag = "Advanced";
        public const string ReadOnlyTag = "ReadOnly";

        private const string DescriptionBackingField = "<Description>k__BackingField";

        private static readonly Dictionary<ConfigEntryBase, object[]> s_foreignTags =
            new Dictionary<ConfigEntryBase, object[]>();

        private static readonly Dictionary<ConfigEntryBase, HashSet<string>> s_managedTags =
            new Dictionary<ConfigEntryBase, HashSet<string>>();

        private static FieldInfo s_descriptionField;
        private static bool s_descriptionFieldMissing;

        public static void MarkAdvanced(ConfigFile config, Func<ConfigDefinition, bool> isAdvanced) =>
            Retag(config, isAdvanced, AdvancedTag);

        public static void SetReadOnly(ConfigFile config, Func<ConfigDefinition, bool> isReadOnly) =>
            Retag(config, isReadOnly, ReadOnlyTag);

        private static void Retag(ConfigFile config, Func<ConfigDefinition, bool> predicate, string tag)
        {
            if (config == null || predicate == null || !EnsureDescriptionField()) return;

            int tagged = 0;
            foreach (var definition in config.Keys.ToList())
            {
                ConfigEntryBase entry;
                try { entry = config[definition]; }
                catch { continue; }
                if (entry == null) continue;

                if (ApplyTag(entry, tag, predicate(definition))) tagged++;
            }

            FiresLogger.LogVerbose($"[FiresConfigVisibility] '{tag}' now on {tagged} entr(ies) of {config.ConfigFilePath}.");
        }

        private static bool ApplyTag(ConfigEntryBase entry, string tag, bool wanted)
        {
            var description = entry.Description ?? ConfigDescription.Empty;

            if (!s_foreignTags.TryGetValue(entry, out var foreign))
            {
                foreign = (description.Tags ?? Array.Empty<object>())
                    .Where(existing => !IsManagedTag(existing))
                    .ToArray();
                s_foreignTags[entry] = foreign;
            }

            if (!s_managedTags.TryGetValue(entry, out var managed))
            {
                managed = new HashSet<string>(StringComparer.Ordinal);
                s_managedTags[entry] = managed;
            }

            if (wanted) managed.Add(tag);
            else managed.Remove(tag);

            var tags = new List<object>(foreign);
            tags.AddRange(managed);

            try
            {
                s_descriptionField.SetValue(entry,
                    new ConfigDescription(description.Description, description.AcceptableValues, tags.ToArray()));
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresConfigVisibility] Retagging '{tag}' failed: {ex.Message}");
                return false;
            }

            return wanted;
        }

        private static bool IsManagedTag(object tag) =>
            tag as string == AdvancedTag || tag as string == ReadOnlyTag;

        private static bool EnsureDescriptionField()
        {
            if (s_descriptionField != null) return true;
            if (s_descriptionFieldMissing) return false;

            s_descriptionField = typeof(ConfigEntryBase)
                .GetField(DescriptionBackingField, BindingFlags.Instance | BindingFlags.NonPublic);

            if (s_descriptionField == null)
            {
                s_descriptionFieldMissing = true;
                FiresLogger.LogWarning(
                    "[FiresConfigVisibility] ConfigEntryBase.Description backing field not found; "
                    + "advanced/read-only classification is unavailable on this BepInEx build.");
            }

            return s_descriptionField != null;
        }
    }
}
