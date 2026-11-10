using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Sync;

namespace FiresCore.Npc
{
    /// <summary>
    /// The configurable "hunt list": creature name/prefab keywords that companions treat as passive
    /// prey. A creature on this list is only engaged proactively when the owner has Hunt toggled ON
    /// (see <see cref="CompanionBehaviorToggles.IsHuntingEnabled"/>) or after it has directly attacked
    /// — so companions don't wander off chasing deer while the owner is being mauled.
    ///
    /// This replaces what used to be a hardcoded array inside the targeting code, so server admins can
    /// add/remove creatures (incl. modded wildlife) without a rebuild. Matching is case-insensitive
    /// substring on either the localized name or the prefab name, mirroring the original behavior.
    /// Server-locked so every client's companions agree on what is huntable.
    /// </summary>
    public static class HuntListConfig
    {
        public const string DefaultList =
            "boar,deer,doe,stag,neck,hare,crow,seagull,fish,leviathan,chicken,hen,lox,sheep,cow,rabbit";

        // 1086 ([generator], Fire 11-01: configs live in their own mods): bound and synced by FiresCompanions; Core reads the owner's
        // value (DefaultList without it). Same member name; .Value still reads (type is now OwnedSetting<string>).
        public static readonly FiresCore.Config.OwnedSetting<string> HuntableCreatures = new FiresCore.Config.OwnedSetting<string>("Companions", "HuntableCreatures", DefaultList);

        private static string _cachedRaw;
        private static HashSet<string> _keywords;

        public static void Initialize(ConfigFile config) { }

        public static void BindToSync(ConfigSync configSync) { }

        /// <summary>
        /// True if either the (lowercased) creature name or prefab name contains a hunt-list keyword.
        /// Re-parses lazily when the config string changes, so live edits apply without a restart.
        /// </summary>
        public static bool IsHuntable(string creatureNameLower, string prefabNameLower)
        {
            var raw = HuntableCreatures?.Value ?? DefaultList;
            if (_keywords == null || !string.Equals(raw, _cachedRaw, StringComparison.Ordinal))
            {
                _cachedRaw = raw;
                _keywords = Parse(raw);
            }

            foreach (var keyword in _keywords)
            {
                if ((!string.IsNullOrEmpty(creatureNameLower) && creatureNameLower.Contains(keyword)) ||
                    (!string.IsNullOrEmpty(prefabNameLower) && prefabNameLower.Contains(keyword)))
                    return true;
            }
            return false;
        }

        private static HashSet<string> Parse(string raw)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(raw)) return set;
            foreach (var token in raw.Split(','))
            {
                var normalized = token.Trim().ToLowerInvariant();
                if (normalized.Length > 0) set.Add(normalized);
            }
            return set;
        }
    }
}
