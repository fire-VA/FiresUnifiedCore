using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using FiresCore.UI;
using UnityEngine;

namespace FiresCore.Input
{
    /// <summary>
    /// Every key the game and its mods are listening for, in one list: Core <see cref="KeyBinding"/>s (which
    /// self-register), every plugin's KeyboardShortcut and KeyCode config entries (through
    /// <see cref="CfgDiscovery"/>), and Valheim's own ZInput buttons. The conflict detector, the config
    /// window's Conflicts view and key capture all read from here.
    ///
    /// Mods do not have to call anything: constructing a <see cref="KeyBinding"/> registers it, and plain
    /// config entries are discovered. Declaring a context or a hand-off is what <see cref="KeyBinding"/>'s
    /// WithContext / Replaces are for.
    /// </summary>
    public static class KeybindRegistry
    {
        private const string UnknownGuid = "unknown";
        private const string UnknownModName = "Mod";

        private static readonly List<KeyBinding> s_coreBindings = new List<KeyBinding>();
        private static readonly List<KeybindEntry> s_entries = new List<KeybindEntry>();
        private static readonly HashSet<ConfigFile> s_watchedFiles = new HashSet<ConfigFile>();
        private static readonly Dictionary<ConfigFile, ModIdentity> s_identityByFile = new Dictionary<ConfigFile, ModIdentity>();

        private static bool s_stale = true;

        private struct ModIdentity
        {
            public string Guid;
            public string ModName;
        }

        /// <summary>The last scan. Rebuilt by <see cref="Rescan"/>; never mutated in place.</summary>
        public static IReadOnlyList<KeybindEntry> Entries
        {
            get
            {
                EnsureScanned();
                return s_entries;
            }
        }

        /// <summary>Raised after a rescan produced a new entry list.</summary>
        public static event Action Changed;

        public static void Register(KeyBinding binding)
        {
            if (binding == null || s_coreBindings.Contains(binding)) return;
            s_coreBindings.Add(binding);
            Invalidate();
        }

        public static void Unregister(KeyBinding binding)
        {
            if (binding == null || !s_coreBindings.Remove(binding)) return;
            Invalidate();
        }

        /// <summary>Marks the scan stale. The next <see cref="Entries"/> read rebuilds it.</summary>
        public static void Invalidate() => s_stale = true;

        /// <summary>Bumped by every rescan, so a view can cache derived data (the conflict list) against it.</summary>
        public static int ScanVersion { get; private set; }

        public static void EnsureScanned()
        {
            if (s_stale) Rescan();
        }

        public static void Rescan()
        {
            s_stale = false;
            s_entries.Clear();
            if (CfgDiscovery.Descriptors.Count == 0) CfgDiscovery.Rebuild();
            BuildIdentityMap();
            CollectCoreBindings(s_entries);
            CollectPluginEntries(s_entries);
            try { VanillaKeybinds.Collect(s_entries); }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning("keybinds: vanilla scan failed: " + ex.Message); }
            ScanVersion++;
            try { Changed?.Invoke(); }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning("keybinds: Changed subscriber threw: " + ex.Message); }
        }

        /// <summary>Every binding, conflicting or not, sorted by key then by mod - the "All bound keys" keymap.</summary>
        public static List<KeybindEntry> BoundEntriesSortedByKey()
        {
            var sorted = new List<KeybindEntry>();
            foreach (var entry in Entries)
                if (entry.IsBound) sorted.Add(entry);
            sorted.Sort(CompareByKeyThenMod);
            return sorted;
        }

        private static int CompareByKeyThenMod(KeybindEntry a, KeybindEntry b)
        {
            int byKey = string.Compare(a.Key.ToString(), b.Key.ToString(), StringComparison.OrdinalIgnoreCase);
            if (byKey != 0) return byKey;
            int byModifiers = ((int)a.Modifiers).CompareTo((int)b.Modifiers);
            if (byModifiers != 0) return byModifiers;
            int byMod = string.Compare(a.ModName, b.ModName, StringComparison.OrdinalIgnoreCase);
            return byMod != 0 ? byMod : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Who else is already listening for this combination, ignoring <paramref name="exclude"/>. Used by key
        /// capture to warn before a rebind is committed.
        /// </summary>
        public static List<KeybindEntry> UsersOf(KeyCode key, KeyModifiers modifiers, BindingContext context, KeybindEntry exclude)
        {
            var users = new List<KeybindEntry>();
            if (key == KeyCode.None) return users;

            foreach (var entry in Entries)
            {
                if (!entry.IsBound || entry.IsChord) continue;
                if (exclude != null && entry.Id == exclude.Id) continue;
                if (entry.Key != key) continue;
                if (!BindingContexts.Overlap(entry.Context, context)) continue;
                bool sameModifiers = entry.Modifiers == modifiers;
                bool looseWildcard = entry.Match == BindingMatch.Loose && entry.Modifiers == KeyModifiers.None;
                if (sameModifiers || looseWildcard) users.Add(entry);
            }
            return users;
        }

        public static string DescribeUsers(IReadOnlyList<KeybindEntry> users)
        {
            var text = new StringBuilder();
            for (int i = 0; i < users.Count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(users[i].SourceLabel);
            }
            return text.ToString();
        }

        /// <summary>Clears a binding: KeyboardShortcut.Empty, KeyCode.None, or a KeyBinding's key.</summary>
        public static bool Unbind(KeybindEntry entry)
        {
            if (entry == null || !entry.CanChange) return false;
            try
            {
                if (entry.CoreBinding != null)
                {
                    entry.CoreBinding.Key.Value = KeyCode.None;
                    entry.CoreBinding.Ctrl.Value = false;
                    entry.CoreBinding.Shift.Value = false;
                    entry.CoreBinding.Alt.Value = false;
                }
                else if (entry.Entry == null) return false;
                else if (entry.Entry.SettingType == typeof(KeyboardShortcut)) entry.Entry.BoxedValue = KeyboardShortcut.Empty;
                else entry.Entry.BoxedValue = KeyCode.None;
            }
            catch (Exception ex)
            {
                FiresConfigUI.Log.LogWarning($"keybinds: unbind '{entry.Id}' failed: {ex.Message}");
                return false;
            }
            Invalidate();
            return true;
        }

        /// <summary>Writes a captured combination back to whichever kind of entry this is.</summary>
        public static bool Apply(KeybindEntry entry, KeyCode key, KeyModifiers modifiers)
        {
            if (entry == null || !entry.CanChange) return false;
            try
            {
                if (entry.CoreBinding != null)
                {
                    entry.CoreBinding.Key.Value = key;
                    entry.CoreBinding.Ctrl.Value = (modifiers & KeyModifiers.Ctrl) != 0;
                    entry.CoreBinding.Shift.Value = (modifiers & KeyModifiers.Shift) != 0;
                    entry.CoreBinding.Alt.Value = (modifiers & KeyModifiers.Alt) != 0;
                }
                else if (entry.Entry == null) return false;
                else if (entry.Entry.SettingType == typeof(KeyboardShortcut))
                    entry.Entry.BoxedValue = KeyCombination.ToShortcut(key, modifiers);
                else entry.Entry.BoxedValue = key;
            }
            catch (Exception ex)
            {
                FiresConfigUI.Log.LogWarning($"keybinds: set '{entry.Id}' failed: {ex.Message}");
                return false;
            }
            Invalidate();
            return true;
        }

        public static KeybindEntry FindByConfigEntry(ConfigEntryBase configEntry)
        {
            if (configEntry == null) return null;
            foreach (var entry in Entries)
            {
                if (ReferenceEquals(entry.Entry, configEntry)) return entry;
                if (entry.CoreBinding != null && ReferenceEquals(entry.CoreBinding.Key, configEntry)) return entry;
            }
            return null;
        }

        // ---------------------------------------------------------------- sources
        private static void BuildIdentityMap()
        {
            s_identityByFile.Clear();
            foreach (var descriptor in CfgDiscovery.Descriptors)
            {
                var file = descriptor.Entry?.ConfigFile;
                if (file == null || s_identityByFile.ContainsKey(file)) continue;
                s_identityByFile[file] = new ModIdentity { Guid = descriptor.ModGuid, ModName = descriptor.ModName };
            }
        }

        private static void CollectCoreBindings(List<KeybindEntry> into)
        {
            foreach (var binding in s_coreBindings)
            {
                if (binding == null || binding.Key == null) continue;
                Watch(binding.Config);
                var identity = IdentityOf(binding.Config);

                into.Add(new KeybindEntry
                {
                    Origin = BindingOrigin.CoreKeyBinding,
                    PluginGuid = identity.Guid,
                    ModName = identity.ModName,
                    Section = binding.Section,
                    Name = binding.Name,
                    Label = binding.Name,
                    Context = binding.Context,
                    Match = BindingMatch.Strict,
                    Key = binding.Key.Value,
                    Modifiers = binding.Modifiers,
                    CanChange = true,
                    CoreBinding = binding,
                    Entry = binding.Key,
                    Replacements = binding.Replacements,
                });
            }
        }

        // The three modifier ConfigEntries of a Core KeyBinding are part of that binding, not bindings of their
        // own, so the plugin scan must not list them a second time.
        private static void CollectPluginEntries(List<KeybindEntry> into)
        {
            var owned = new HashSet<ConfigEntryBase>();
            foreach (var binding in s_coreBindings)
            {
                if (binding == null || binding.Key == null) continue;
                owned.Add(binding.Key);
                owned.Add(binding.Ctrl);
                owned.Add(binding.Shift);
                owned.Add(binding.Alt);
            }

            foreach (var descriptor in CfgDiscovery.Descriptors)
            {
                if (descriptor.Kind != CtrlKind.KeyBind) continue;
                if (owned.Contains(descriptor.Entry)) continue;

                descriptor.RefreshTags();
                Watch(descriptor.Entry?.ConfigFile);

                KeyCode key;
                KeyModifiers modifiers;
                bool isChord = false;
                BindingOrigin origin;
                BindingMatch match;

                if (descriptor.Type == typeof(KeyboardShortcut))
                {
                    var shortcut = descriptor.BoxedValue is KeyboardShortcut value ? value : KeyboardShortcut.Empty;
                    key = shortcut.MainKey;
                    modifiers = KeyCombination.FromShortcut(shortcut, out isChord);
                    origin = BindingOrigin.PluginShortcut;
                    match = BindingMatch.Strict;
                }
                else
                {
                    key = descriptor.BoxedValue is KeyCode code ? code : KeyCode.None;
                    modifiers = KeyModifiers.None;
                    origin = BindingOrigin.PluginKeyCode;
                    match = BindingMatch.Loose;
                }

                into.Add(new KeybindEntry
                {
                    Origin = origin,
                    PluginGuid = descriptor.ModGuid,
                    ModName = descriptor.ModName,
                    Section = descriptor.Section,
                    Name = descriptor.Key,
                    Label = descriptor.Label,
                    Context = BindingContexts.Default,
                    Match = match,
                    Key = key,
                    Modifiers = modifiers,
                    IsChord = isChord,
                    CanChange = !descriptor.IsLocked,
                    IsLocked = descriptor.IsLocked,
                    Entry = descriptor.Entry,
                });
            }
        }

        private static ModIdentity IdentityOf(ConfigFile config)
        {
            if (config != null && s_identityByFile.TryGetValue(config, out var identity)) return identity;
            return new ModIdentity { Guid = UnknownGuid, ModName = UnknownModName };
        }

        // One subscription per ConfigFile (SettingChanged is a ConfigFile event; the per-entry one only exists
        // on the generic ConfigEntry<T>), so a rebind made anywhere - our window, ConfigurationManager, a
        // hand-edited .cfg reload - invalidates the scan.
        private static void Watch(ConfigFile config)
        {
            if (config == null || !s_watchedFiles.Add(config)) return;
            config.SettingChanged += OnWatchedSettingChanged;
            config.ConfigReloaded += OnWatchedConfigReloaded;
        }

        private static void OnWatchedSettingChanged(object sender, SettingChangedEventArgs args)
        {
            if (args?.ChangedSetting == null) return;
            var type = args.ChangedSetting.SettingType;
            if (type == typeof(KeyboardShortcut) || type == typeof(KeyCode) || type == typeof(bool)) Invalidate();
        }

        private static void OnWatchedConfigReloaded(object sender, EventArgs args) => Invalidate();
    }
}
