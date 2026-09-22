using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Config
{
    /// <summary>
    /// Reloads a mod's .cfg when it changes on disk, so edits apply without a restart. The file watcher only
    /// raises a flag; the reload runs on the main thread once the file has been quiet for a short window.
    /// </summary>
    public sealed class ConfigHotReload : MonoBehaviour
    {
        private const string HostObjectName = "FiresCore_ConfigHotReload";
        private const float QuietPeriodSeconds = 0.3f;

        private static ConfigHotReload _host;

        private readonly List<WatchedConfigFile> _watchedFiles = new List<WatchedConfigFile>();

        public static void Watch(ConfigFile config)
        {
            string fileName = Path.GetFileName(config.ConfigFilePath);
            try
            {
                var watchedFile = new WatchedConfigFile(config);
                EnsureHost();
                _host._watchedFiles.Add(watchedFile);
                FiresLogger.LogVerbose($"[ConfigHotReload] Watching {fileName}");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[ConfigHotReload] Cannot watch {fileName}, edits need a restart: {ex.Message}");
            }
        }

        private static void EnsureHost()
        {
            if (_host != null) return;
            var hostObject = new GameObject(HostObjectName) { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(hostObject);
            _host = hostObject.AddComponent<ConfigHotReload>();
        }

        private void Update()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var watchedFile in _watchedFiles)
                watchedFile.ReloadOnceQuiet(now);
        }

        private void OnDestroy()
        {
            foreach (var watchedFile in _watchedFiles)
                watchedFile.Dispose();
            _watchedFiles.Clear();
            if (_host == this) _host = null;
        }

        private sealed class WatchedConfigFile : IDisposable
        {
            private readonly ConfigFile _config;
            private readonly FileSystemWatcher _watcher;
            private volatile bool _changePending;
            private bool _reloadArmed;
            private float _quietSince;

            public WatchedConfigFile(ConfigFile config)
            {
                _config = config;
                _watcher = new FileSystemWatcher(Path.GetDirectoryName(config.ConfigFilePath), FileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                };
                _watcher.Changed += MarkChangePending;
                _watcher.Created += MarkChangePending;
                _watcher.Renamed += MarkChangePending;
                _watcher.EnableRaisingEvents = true;
            }

            private string FileName => Path.GetFileName(_config.ConfigFilePath);

            private void MarkChangePending(object sender, FileSystemEventArgs e) => _changePending = true;

            public void ReloadOnceQuiet(float now)
            {
                if (_changePending)
                {
                    _changePending = false;
                    _quietSince = now;
                    _reloadArmed = true;
                }

                if (!_reloadArmed || now - _quietSince < QuietPeriodSeconds) return;
                _reloadArmed = false;
                ReloadAndLogChangedKeys();
            }

            private void ReloadAndLogChangedKeys()
            {
                var changedKeys = new List<string>();
                void RecordChangedKey(object sender, SettingChangedEventArgs e) => changedKeys.Add(e.ChangedSetting.Definition.Key);

                bool saveOnSet = _config.SaveOnConfigSet;
                _config.SettingChanged += RecordChangedKey;
                _config.SaveOnConfigSet = false;
                try
                {
                    _config.Reload();
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[ConfigHotReload] Reloading {FileName} failed: {ex.Message}");
                }
                finally
                {
                    _config.SaveOnConfigSet = saveOnSet;
                    _config.SettingChanged -= RecordChangedKey;
                }

                if (changedKeys.Count > 0)
                    FiresLogger.LogInfo($"[ConfigHotReload] Reloaded {FileName}, changed: {string.Join(", ", changedKeys)}");
            }

            public void Dispose()
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
        }
    }
}
