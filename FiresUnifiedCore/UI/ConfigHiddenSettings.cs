using System;
using System.Collections.Generic;
using System.IO;
using FiresCore.Storage;
using FiresCore.Sync;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// A server-owned list of settings that ordinary players should not see in the config window. The file
    /// lives at <c>BepInEx/config/FiresRPGmaker/UI/hidden_settings.yml</c>, is watched for live edits, and is
    /// synced to every client through Core's ConfigSync — so an admin edits one file on the server and every
    /// connected player's window updates. Admins and the host always see everything.
    ///
    /// One entry per line, <c>ModGuid=Section=Key</c>, with <c>*</c> allowed for any of the three parts.
    /// A leading <c>- </c> (YAML list style), surrounding quotes and <c>#</c> comments are all tolerated.
    /// </summary>
    public sealed class ConfigHiddenSettings : MonoBehaviour
    {
        private const string HostObjectName = "FiresCore_HiddenSettings";
        private const string FileName = "hidden_settings.yml";
        private const string SyncIdentifier = "Hidden settings";
        private const string Wildcard = "*";
        private const char PartSeparator = '=';
        private const float QuietPeriodSeconds = 0.3f;

        private static readonly string[] FileHeader =
        {
            "# Settings hidden from non-admin players in the Fires config window.",
            "# One per line: ModGuid=Section=Key   ('*' matches any part)",
            "#",
            "# com.Fire.FiresAdminTerrain=01 - World Generation=*",
            "# com.Fire.FiresUnifiedCore=00 - Config UI=07 MenuButton",
        };

        private static ConfigHiddenSettings _host;
        private static CustomSyncedValue<string> _synced;
        private static readonly List<Rule> _rules = new List<Rule>();

        private FileSystemWatcher _watcher;
        private float _reloadAt;

        public static int Count => _rules.Count;

        public static string FilePath => Path.Combine(FiresConfigPaths.Ui, FileName);

        /// <summary>Called once from Core setup, after its ConfigSync exists.</summary>
        public static void Initialize(ConfigSync configSync)
        {
            if (_synced != null || configSync == null) return;

            _synced = new CustomSyncedValue<string>(configSync, SyncIdentifier, "");
            _synced.ValueChanged += () => ParseInto(_rules, _synced.Value);

            EnsureHost();
            PublishFromDisk();
        }

        /// <summary>
        /// True when this row should be hidden from the local player. Mirrors ConfigurationManager: nothing is
        /// hidden in the menu or single-player-before-connect (no ZNet), and never from an admin.
        /// </summary>
        public static bool IsHidden(CfgDescriptor descriptor)
        {
            if (_rules.Count == 0 || ZNet.instance == null) return false;
            if (IsLocalAdmin()) return false;

            foreach (var rule in _rules)
                if (rule.Matches(descriptor)) return true;
            return false;
        }

        private static bool IsLocalAdmin()
        {
            try { return AdminSyncing.IsLocalAdmin(); }
            catch { return false; }
        }

        private static void EnsureHost()
        {
            if (_host != null) return;
            var hostObject = new GameObject(HostObjectName) { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(hostObject);
            _host = hostObject.AddComponent<ConfigHiddenSettings>();
            _host.StartWatching();
        }

        private void StartWatching()
        {
            try
            {
                _watcher = new FileSystemWatcher(FiresConfigPaths.Ui, FileName) { EnableRaisingEvents = true };
                _watcher.Changed += (sender, args) => MarkDirty();
                _watcher.Created += (sender, args) => MarkDirty();
                _watcher.Deleted += (sender, args) => MarkDirty();
                _watcher.Renamed += (sender, args) => MarkDirty();
            }
            catch (Exception ex)
            {
                FiresConfigUI.Log.LogWarning($"hidden settings: cannot watch {FileName}, edits need a restart ({ex.Message}).");
            }
        }

        private void MarkDirty() => _reloadAt = Time.realtimeSinceStartup + QuietPeriodSeconds;

        private void Update()
        {
            if (_reloadAt <= 0f || Time.realtimeSinceStartup < _reloadAt) return;
            _reloadAt = 0f;
            PublishFromDisk();
        }

        private void OnDestroy()
        {
            _watcher?.Dispose();
            _watcher = null;
            if (_host == this) _host = null;
        }

        // The local file is what a server (or a solo host) publishes; on a client the server's package
        // overwrites it, which is the point — the admin edits one file and everybody's window agrees.
        private static void PublishFromDisk()
        {
            string text = ReadOrCreateFile();
            ParseInto(_rules, text);
            try { _synced?.AssignLocalValue(text); }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning("hidden settings: sync assign failed: " + ex.Message); }
            FiresConfigUI.Log.LogInfo($"hidden settings: {_rules.Count} rule(s) from {FileName}.");
        }

        private static string ReadOrCreateFile()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    File.WriteAllLines(path, FileHeader);
                    return "";
                }
                return File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                FiresConfigUI.Log.LogWarning($"hidden settings: cannot read {FileName}: {ex.Message}");
                return "";
            }
        }

        private static void ParseInto(List<Rule> rules, string text)
        {
            rules.Clear();
            if (string.IsNullOrEmpty(text)) return;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = CleanLine(rawLine);
                if (line.Length == 0) continue;

                var parts = line.Split(PartSeparator);
                if (parts.Length != 3) continue;
                rules.Add(new Rule(parts[0].Trim(), parts[1].Trim(), parts[2].Trim()));
            }
        }

        private static string CleanLine(string rawLine)
        {
            string line = rawLine.Trim().TrimEnd('\r');
            int comment = line.IndexOf('#');
            if (comment >= 0) line = line.Substring(0, comment).Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal)) line = line.Substring(2).Trim();
            return line.Trim('"', '\'').Trim();
        }

        private readonly struct Rule
        {
            private readonly string _modGuid;
            private readonly string _section;
            private readonly string _key;

            public Rule(string modGuid, string section, string key)
            {
                _modGuid = modGuid;
                _section = section;
                _key = key;
            }

            public bool Matches(CfgDescriptor descriptor)
                => PartMatches(_modGuid, descriptor.ModGuid)
                && PartMatches(_section, descriptor.Section)
                && PartMatches(_key, descriptor.Key);

            private static bool PartMatches(string rulePart, string value)
                => rulePart == Wildcard || string.Equals(rulePart, value, StringComparison.OrdinalIgnoreCase);
        }
    }
}
