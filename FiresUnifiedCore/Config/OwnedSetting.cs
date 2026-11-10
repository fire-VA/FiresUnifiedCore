using System;
using System.Collections.Generic;

namespace FiresCore.Config
{
    /// <summary>
    /// 1086 ([generator], Fire 11-01: configs live in their own mods): a setting another mod binds, read by Core's shared code. Value =
    /// CoreSettings.Get(Section, Key, Fallback): the owner's live value when it provides the key, else Fallback (the old Core default).
    /// For hot paths (read per frame): the value is cached while the key has a change event (provided as a ConfigEntry) and dropped on
    /// CoreSettings.Changed / Provided for the key; a getter-only provider has no change event, so it is read again every time.
    /// Main thread.
    /// </summary>
    public sealed class OwnedSetting<T>
    {
        public readonly string Section, Key;
        public readonly T Fallback;
        private T m_value;
        private bool m_valid;

        /// <summary>Raised after the owner's value for this key changed (or a provider took it).</summary>
        public event Action Changed;

        public OwnedSetting(string section, string key, T fallback)
        {
            Section = section; Key = key; Fallback = fallback;
            OwnedSettingHub.Register(section, key, Invalidate);
        }

        public T Value
        {
            get
            {
                if (m_valid) return m_value;
                T v = CoreSettings.Get(Section, Key, Fallback);
                if (CoreSettings.HasChangeEvent(Section, Key)) { m_value = v; m_valid = true; }
                return v;
            }
        }

        public bool IsProvided => CoreSettings.IsProvided(Section, Key);

        private void Invalidate()
        {
            m_valid = false;
            var h = Changed;
            if (h == null) return;
            try { h(); } catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug($"[OwnedSetting] {Section}/{Key}: a Changed listener threw: {e.Message}"); }
        }
    }

    /// <summary>1086 ([generator], Fire 11-01: configs live in their own mods): an owned setting read with CoreSettings.Get on EVERY use, no
    /// cache (for owners that provide a plain getter, e.g. FiresAdminTerrain's Snow / Loot / MineralSeeds / Undo keys).</summary>
    public sealed class LiveSetting<T>
    {
        public readonly string Section, Key;
        public readonly T Fallback;
        public LiveSetting(string section, string key, T fallback) { Section = section; Key = key; Fallback = fallback; }
        public T Value => CoreSettings.Get(Section, Key, Fallback);
        public bool IsProvided => CoreSettings.IsProvided(Section, Key);
    }

    /// <summary>1086: routes CoreSettings.Changed / Provided to the OwnedSetting caches of that key.</summary>
    internal static class OwnedSettingHub
    {
        private static readonly Dictionary<string, List<Action>> s_byKey = new Dictionary<string, List<Action>>(StringComparer.Ordinal);
        private static bool s_hooked;

        private static string K(string section, string key) => (section ?? "") + "/" + (key ?? "");

        internal static void Register(string section, string key, Action invalidate)
        {
            if (!s_hooked)
            {
                s_hooked = true;
                CoreSettings.Changed += Fire;
                CoreSettings.Provided += (s, k, _) => Fire(s, k);
            }
            string id = K(section, key);
            if (!s_byKey.TryGetValue(id, out List<Action> list)) s_byKey[id] = list = new List<Action>();
            list.Add(invalidate);
        }

        private static void Fire(string section, string key)
        {
            if (!s_byKey.TryGetValue(K(section, key), out List<Action> list)) return;
            for (int i = 0; i < list.Count; i++) list[i]();
        }
    }
}
