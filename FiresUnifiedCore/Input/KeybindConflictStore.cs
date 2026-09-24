using System;
using System.Collections.Generic;
using System.IO;
using FiresCore.Storage;
using FiresCore.UI;
using Newtonsoft.Json;

namespace FiresCore.Input
{
    /// <summary>
    /// The player's "I know, leave it alone" list, kept at
    /// <c>BepInEx/config/FiresRPGmaker/UI/keybind-conflicts.json</c>. Client-side and never synced: which
    /// conflicts a player accepts is their business.
    ///
    /// An ignore is keyed on <see cref="KeybindConflict.Signature"/>, which includes each member's current
    /// combination - so rebinding any member makes it a new situation that can warn again, and rebinding back
    /// to exactly the ignored combination stays ignored.
    /// </summary>
    public static class KeybindConflictStore
    {
        private const string FileName = "keybind-conflicts.json";
        private const int SchemaVersion = 1;

        private static readonly HashSet<string> s_ignored = new HashSet<string>(StringComparer.Ordinal);
        private static bool s_loaded;

        /// <summary>Session-only: "Remind me later" suppresses the popup until the game restarts.</summary>
        public static bool RemindLaterThisSession { get; private set; }

        public static string FilePath => Path.Combine(FiresConfigPaths.Ui, FileName);

        public static int IgnoredCount
        {
            get
            {
                EnsureLoaded();
                return s_ignored.Count;
            }
        }

        public static bool IsIgnored(KeybindConflict conflict)
        {
            if (conflict == null) return false;
            EnsureLoaded();
            return s_ignored.Contains(conflict.Signature);
        }

        public static void Ignore(KeybindConflict conflict)
        {
            if (conflict == null) return;
            EnsureLoaded();
            if (!s_ignored.Add(conflict.Signature)) return;
            Save();
        }

        public static void Unignore(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return;
            EnsureLoaded();
            if (!s_ignored.Remove(signature)) return;
            Save();
        }

        public static void RemindLater() => RemindLaterThisSession = true;

        /// <summary>Every detected conflict the player has not already accepted.</summary>
        public static List<KeybindConflict> UnresolvedOf(IReadOnlyList<KeybindConflict> conflicts)
        {
            var unresolved = new List<KeybindConflict>();
            foreach (var conflict in conflicts)
                if (!IsIgnored(conflict)) unresolved.Add(conflict);
            return unresolved;
        }

        private sealed class StoreFile
        {
            [JsonProperty("version")] public int Version = SchemaVersion;
            [JsonProperty("ignored")] public List<string> Ignored = new List<string>();
        }

        private static void EnsureLoaded()
        {
            if (s_loaded) return;
            s_loaded = true;

            string path = FilePath;
            if (!File.Exists(path)) return;

            try
            {
                var file = JsonConvert.DeserializeObject<StoreFile>(File.ReadAllText(path));
                if (file == null)
                {
                    FiresConfigUI.Log.LogWarning($"keybinds: {FileName} is empty or not an object - starting with no ignored conflicts.");
                    return;
                }
                if (file.Version != SchemaVersion)
                {
                    FiresConfigUI.Log.LogWarning($"keybinds: {FileName} is schema {file.Version}, this build reads {SchemaVersion} - ignoring its contents.");
                    return;
                }
                if (file.Ignored == null) return;
                foreach (string signature in file.Ignored)
                    if (!string.IsNullOrEmpty(signature)) s_ignored.Add(signature);
            }
            catch (Exception ex)
            {
                FiresConfigUI.Log.LogError($"keybinds: cannot read {FileName} ({ex.Message}) - starting with no ignored conflicts.");
            }
        }

        private static void Save()
        {
            var file = new StoreFile { Version = SchemaVersion, Ignored = new List<string>(s_ignored) };
            file.Ignored.Sort(StringComparer.Ordinal);
            try { File.WriteAllText(FilePath, JsonConvert.SerializeObject(file, Formatting.Indented)); }
            catch (Exception ex) { FiresConfigUI.Log.LogError($"keybinds: cannot write {FileName}: {ex.Message}"); }
        }
    }
}
