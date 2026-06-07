using System;
using System.Linq;
using System.Text;

namespace FiresCore.Storage
{
    // Builds a human-readable snapshot of a player's vault across all backends (LiteDB bank/leaderboard/
    // mail + character customData), for an admin `dumpvault`-style command. Read-only; resolves bank
    // prefab hashes to names via ZNetScene when available.
    public static class VaultDump
    {
        public static string BuildPlayerReport(string userId, string leaderboardOwnerKey, Player player)
        {
            var report = new StringBuilder();
            report.AppendLine($"=== Vault dump — userId={userId} ===");
            AppendBank(report, userId);
            AppendLeaderboard(report, leaderboardOwnerKey);
            AppendMail(report, userId);
            AppendCustomData(report, player);
            return report.ToString();
        }

        private static void AppendBank(StringBuilder report, string userId)
        {
            var items = BankRepository.GetItems(userId);
            report.AppendLine($"[Bank] {items.Count} stack(s)");
            foreach (var kv in items)
                report.AppendLine($"   {ResolvePrefabName(kv.Key)} x{kv.Value}");
        }

        private static void AppendLeaderboard(StringBuilder report, string ownerKey)
        {
            if (string.IsNullOrEmpty(ownerKey)) return;
            var board = LeaderboardRepository.GetAll();
            if (!board.TryGetValue(ownerKey, out var entry)) { report.AppendLine("[Leaderboard] (no entry)"); return; }
            report.AppendLine($"[Leaderboard] name={entry.PlayerName} deaths={entry.DeathAmount} " +
                              $"explored={entry.MapExplored:P0} kills={entry.KilledCreatures.Values.Sum()} " +
                              $"built={entry.BuiltStructures.Values.Sum()} crafted={entry.ItemsCrafted.Values.Sum()}");
        }

        private static void AppendMail(StringBuilder report, string userId)
        {
            int total = MailRepository.GetTotalMailCount(userId);
            int unread = MailRepository.GetUnreadMailCount(userId);
            report.AppendLine($"[Mail] {total} message(s), {unread} unread");
        }

        private static void AppendCustomData(StringBuilder report, Player player)
        {
            if (player == null) { report.AppendLine("[CustomData] (no player object)"); return; }
            var keys = PlayerVaultData.GetVaultKeys(player).ToList();
            report.AppendLine($"[CustomData] {keys.Count} vault key(s)");
            foreach (var kv in keys)
                report.AppendLine($"   {kv.Key} = {kv.Value}");
        }

        private static string ResolvePrefabName(int prefabHash)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabHash);
                return prefab != null ? prefab.name : prefabHash.ToString();
            }
            catch { return prefabHash.ToString(); }
        }
    }
}
