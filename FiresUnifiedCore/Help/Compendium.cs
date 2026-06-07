using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FiresCore.Help
{
    // Shared compendium engine. Mods register CompendiumEntry definitions; discovered entries are written into
    // the player's vanilla known-texts (Player.m_knownTexts) so they appear in the inventory Texts tab and
    // persist with the character. Entries render with a clean Topic — the old implementation jammed a
    // "! [VA] {sort} - " prefix into the key, which vanilla TextsDialog shows verbatim as the title.
    //
    // Per-mod specifics are parameterized:
    //   SuppressDiscovery - returns true during the respawn/teleport window; writes are skipped and replay
    //                       later (player-state writes during that window can deadlock the zone stream)
    //   OnDiscovered      - optional extra notification hook invoked after the default TopLeft message
    //
    // Admin-only documentation lives in the Help panel's admin-gated sections, not here, so the compendium
    // has no admin concept.
    public static class Compendium
    {
        public static Func<bool> SuppressDiscovery;
        public static Action<CompendiumEntry, Player> OnDiscovered;

        // Topic prefix written by the pre-Core implementation. Swept out of existing characters on sync so the
        // old cruft-prefixed entries don't linger beside the clean ones.
        public const string LegacyTopicPrefix = "! [VA] ";

        private static readonly Dictionary<string, CompendiumEntry> _entries = new Dictionary<string, CompendiumEntry>();
        private static readonly HashSet<string> _discovered = new HashSet<string>();

        public static int Count => _entries.Count;
        public static int DiscoveredCount => _discovered.Count;

        public static IEnumerable<CompendiumEntry> All =>
            _entries.Values.OrderBy(e => e.SortOrder).ThenBy(e => e.Title);

        public static IEnumerable<CompendiumEntry> ByCategory(string category) =>
            _entries.Values.Where(e => e.Category == category).OrderBy(e => e.SortOrder).ThenBy(e => e.Title);

        public static CompendiumEntry Get(string key) =>
            _entries.TryGetValue(key, out var entry) ? entry : null;

        public static void Register(CompendiumEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Key))
            {
                Logging.FiresLogger.LogWarning("[Compendium] Attempted to register a null or keyless entry");
                return;
            }
            _entries[entry.Key] = entry;
        }

        public static bool IsDiscovered(string key) => _discovered.Contains(key);

        // Unlocks an entry for the player by writing it into known-texts. Returns true only when this call
        // newly discovered it (so callers can count and notify).
        public static bool Discover(string key, Player player = null)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (player == null) player = Player.m_localPlayer;
            if (player == null) return false;
            if (SuppressDiscovery != null && SuppressDiscovery()) return false;

            if (!_entries.TryGetValue(key, out var entry)) return false;

            var knownTexts = player.m_knownTexts;
            if (knownTexts == null) return false;

            string topic = entry.Topic;
            if (knownTexts.ContainsKey(topic))
            {
                _discovered.Add(key);
                return false;
            }

            knownTexts[topic] = entry.Body;
            _discovered.Add(key);
            Notify(entry, player);
            return true;
        }

        public static void DiscoverByTrigger(string discoveryKey, Player player = null)
        {
            if (string.IsNullOrEmpty(discoveryKey)) return;
            foreach (var entry in _entries.Values)
                if (entry.DiscoveryKey == discoveryKey)
                    Discover(entry.Key, player);
        }

        public static int UnlockAll(Player player = null)
        {
            if (player == null) player = Player.m_localPlayer;
            if (player == null) return 0;

            int unlocked = 0;
            foreach (var entry in _entries.Values)
                if (Discover(entry.Key, player)) unlocked++;
            return unlocked;
        }

        public static void ResetAll(Player player = null)
        {
            if (player == null) player = Player.m_localPlayer;
            if (player == null) return;

            var knownTexts = player.m_knownTexts;
            if (knownTexts == null) return;

            foreach (var entry in _entries.Values)
                knownTexts.Remove(entry.Topic);
            _discovered.Clear();
        }

        // Rebuilds discovered state from what's already in the player's known-texts, sweeps any legacy
        // prefixed entries, and (for admins) ensures admin entries are present. Call on player spawn.
        public static void SyncOnPlayerSpawn(Player player)
        {
            if (player == null || player != Player.m_localPlayer) return;

            RemoveLegacyEntries(player);
            LoadDiscoveredFromPlayer(player);
        }

        private static void LoadDiscoveredFromPlayer(Player player)
        {
            _discovered.Clear();
            var knownTexts = player.m_knownTexts;
            if (knownTexts == null) return;

            foreach (var entry in _entries.Values)
                if (knownTexts.ContainsKey(entry.Topic))
                    _discovered.Add(entry.Key);
        }

        private static void RemoveLegacyEntries(Player player)
        {
            var knownTexts = player.m_knownTexts;
            if (knownTexts == null) return;

            var stale = knownTexts.Keys.Where(k => k != null && k.StartsWith(LegacyTopicPrefix)).ToList();
            foreach (var key in stale)
                knownTexts.Remove(key);
            if (stale.Count > 0)
                Logging.FiresLogger.LogInfo($"[Compendium] Cleared {stale.Count} legacy known-text entries");
        }

        private static void Notify(CompendiumEntry entry, Player player)
        {
            player.Message(MessageHud.MessageType.TopLeft, $"<color=yellow>Compendium Updated:</color> {entry.Title}");
            try { OnDiscovered?.Invoke(entry, player); }
            catch (Exception ex) { Logging.FiresLogger.LogWarning($"[Compendium] OnDiscovered hook threw: {ex.Message}"); }
        }
    }
}
