using System;
using System.Collections.Generic;
using FiresCore.UI.GroupHud;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Registration seam for the shared group HUD (the below-minimap member panel with name +
    /// distance + health/stamina/eitr bars). Any mod registers a named provider that returns its
    /// current rows; the HUD aggregates every registered provider each refresh. This is what makes
    /// the HUD reusable: FiresCompanions feeds companions, an RPG/party mod can feed players, etc.,
    /// all into one panel without any of them knowing about each other.
    ///
    /// Thread-affinity: register/unregister and provider invocation all happen on the Unity main
    /// thread (the HUD pulls during its Update). Providers must be cheap — they run a few times a
    /// second.
    /// </summary>
    public static class GroupHudBridge
    {
        private static readonly Dictionary<string, Func<IEnumerable<GroupHudMember>>> _providers =
            new Dictionary<string, Func<IEnumerable<GroupHudMember>>>(StringComparer.Ordinal);

        /// <summary>
        /// Optional gate a frontend can set so the HUD also hides while that mod's own full-screen
        /// panels are open (e.g. wired to <c>ModUiRegistry</c>'s any-open check). Null = ignored;
        /// the HUD still always hides for vanilla inventory/map/console/chat on its own.
        /// </summary>
        public static Func<bool> IsBlockingUiOpen;

        /// <summary>Raised whenever the provider set changes, so a live HUD can rebuild promptly.</summary>
        public static event Action ProvidersChanged;

        /// <summary>Register (or replace) a provider under <paramref name="key"/>.</summary>
        public static void RegisterProvider(string key, Func<IEnumerable<GroupHudMember>> provider)
        {
            if (string.IsNullOrEmpty(key) || provider == null) return;
            _providers[key] = provider;
            try { ProvidersChanged?.Invoke(); } catch { }
        }

        /// <summary>Remove the provider registered under <paramref name="key"/> (no-op if absent).</summary>
        public static void UnregisterProvider(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (_providers.Remove(key))
            {
                try { ProvidersChanged?.Invoke(); } catch { }
            }
        }

        public static bool HasProviders => _providers.Count > 0;

        /// <summary>
        /// Pull every provider's rows into one list. Bad providers are isolated (a throwing or
        /// null-returning provider is skipped, not allowed to break the HUD). Null members and
        /// members without an Id are dropped so row diffing stays well-keyed.
        /// </summary>
        internal static List<GroupHudMember> CollectMembers()
        {
            var result = new List<GroupHudMember>();
            foreach (var kvp in _providers)
            {
                IEnumerable<GroupHudMember> rows;
                try { rows = kvp.Value?.Invoke(); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GroupHud] provider '{kvp.Key}' threw: {ex.Message}");
                    continue;
                }
                if (rows == null) continue;
                foreach (var member in rows)
                {
                    if (member != null && !string.IsNullOrEmpty(member.Id))
                        result.Add(member);
                }
            }
            return result;
        }
    }
}
