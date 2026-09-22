using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Sync;
using UnityEngine;

namespace FiresCore.Config
{
    /// <summary>
    /// Keeps a mod's "admins only" settings locked for non-admins. Local admin status arrives a beat after
    /// connect and can change mid-session, so this re-evaluates on a slow poll rather than deciding once —
    /// a one-shot check gates a real admin out of their own settings during that window.
    /// </summary>
    public sealed class FiresAdminConfigGate : MonoBehaviour
    {
        private const string HostObjectName = "FiresCore_AdminConfigGate";
        private const float PollSeconds = 1f;

        private static FiresAdminConfigGate _host;

        private readonly List<GatedConfig> _gated = new List<GatedConfig>();
        private float _nextPoll;

        /// <summary>
        /// <paramref name="restrictionEnabled"/> is the mod's own "admin only" switch; <paramref name="isRestricted"/>
        /// picks which entries it covers. Entries are locked only when the switch is on AND we are not admin.
        /// </summary>
        public static void Register(ConfigFile config, Func<bool> restrictionEnabled,
                                    Func<ConfigDefinition, bool> isRestricted)
        {
            if (config == null || restrictionEnabled == null || isRestricted == null) return;

            EnsureHost();
            _host._gated.Add(new GatedConfig(config, restrictionEnabled, isRestricted));
            _host.ApplyAll();
        }

        private static void EnsureHost()
        {
            if (_host != null) return;
            var hostObject = new GameObject(HostObjectName) { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(hostObject);
            _host = hostObject.AddComponent<FiresAdminConfigGate>();
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + PollSeconds;
            ApplyAll();
        }

        private void ApplyAll()
        {
            foreach (var gated in _gated) gated.ApplyIfChanged();
        }

        private void OnDestroy()
        {
            _gated.Clear();
            if (_host == this) _host = null;
        }

        private sealed class GatedConfig
        {
            private readonly ConfigFile _config;
            private readonly Func<bool> _restrictionEnabled;
            private readonly Func<ConfigDefinition, bool> _isRestricted;
            private bool? _lockedNow;

            public GatedConfig(ConfigFile config, Func<bool> restrictionEnabled, Func<ConfigDefinition, bool> isRestricted)
            {
                _config = config;
                _restrictionEnabled = restrictionEnabled;
                _isRestricted = isRestricted;
            }

            public void ApplyIfChanged()
            {
                bool locked = ShouldLock();
                if (_lockedNow == locked) return;
                _lockedNow = locked;
                FiresConfigVisibility.SetReadOnly(_config, definition => locked && _isRestricted(definition));
            }

            private bool ShouldLock()
            {
                try { return _restrictionEnabled() && !AdminSyncing.IsLocalAdmin(); }
                catch { return false; }
            }
        }
    }
}
