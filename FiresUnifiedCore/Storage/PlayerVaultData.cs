using System.Collections.Generic;
using System.Linq;

namespace FiresCore.Storage
{
    // Accessor for the per-character vault data Marketplace keeps in Player.m_customData (NOT LiteDB):
    // quest progress, quest cooldowns, and faction membership. Provides raw get/set of the exact
    // [MPASN]-prefixed keys so an existing character's progress is read unchanged; interpreting the
    // encoded values (quest score strings, completion days) is the consuming module's job.
    //
    // NOTE: writing customData during the spawn/respawn/teleport window can deadlock the zone stream.
    // Route writes through the spawn gate (FiresCore.Lifecycle.PlayerSpawnGate) rather than calling Set
    // directly from inside a spawn-triggered code path.
    public static class PlayerVaultData
    {
        public const string QuestProgressPrefix = "[MPASN]quest=";
        public const string QuestCooldownPrefix = "[MPASN]questCD=";
        public const string FactionKey = "MPASN_Faction";

        public static string QuestProgressKey(string questUid) => QuestProgressPrefix + questUid;
        public static string QuestCooldownKey(string questUid) => QuestCooldownPrefix + questUid;

        public static bool TryGet(Player player, string key, out string value)
        {
            value = null;
            if (player?.m_customData == null || string.IsNullOrEmpty(key)) return false;
            return player.m_customData.TryGetValue(key, out value);
        }

        public static string Get(Player player, string key, string fallback = null) =>
            TryGet(player, key, out string value) ? value : fallback;

        public static void Set(Player player, string key, string value)
        {
            if (player?.m_customData == null || string.IsNullOrEmpty(key)) return;
            player.m_customData[key] = value;
        }

        public static void Remove(Player player, string key)
        {
            if (player?.m_customData == null || string.IsNullOrEmpty(key)) return;
            player.m_customData.Remove(key);
        }

        public static string GetFaction(Player player) => Get(player, FactionKey);

        public static void SetFaction(Player player, string factionId) => Set(player, FactionKey, factionId);

        // Every [MPASN]-prefixed key on the character, for dump/inspection.
        public static IEnumerable<KeyValuePair<string, string>> GetVaultKeys(Player player)
        {
            if (player?.m_customData == null) return Enumerable.Empty<KeyValuePair<string, string>>();
            return player.m_customData.Where(kv => kv.Key.StartsWith("[MPASN]") || kv.Key == FactionKey);
        }
    }
}
