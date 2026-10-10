using System;
using System.IO;
using LiteDB;

namespace FiresCore.Storage
{
    // Owns the shared LiteDB vault database. Uses the same engine, file format, BsonMapper settings, and
    // open-per-operation pattern as Marketplace's DB.cs, so an existing Marketplace SavedData/DB.db opens
    // unchanged (true drop-in). The mapper settings are mandatory: without IncludeFields the field-backed
    // dictionaries on LeaderboardEntry deserialize empty, and EmptyStringToNull/TrimWhitespace must match
    // or string fields round-trip differently than the data Marketplace wrote.
    public static class VaultDatabase
    {
        public const string BankCollection = "Bank";
        public const string MarketplaceCollection = "Marketplace";
        public const string MailUsersCollection = "Users";
        public const string MailEntriesCollection = "Mails";
        public const string LeaderboardCollection = "Leaderboard";
        // Season archive: rows copied here (tagged with their Season) when a season is rolled; the live
        // Leaderboard collection is then wiped for the new season. Meta holds the current season number.
        public const string LeaderboardArchiveCollection = "LeaderboardArchive";
        public const string LeaderboardMetaCollection = "LeaderboardMeta";
        public const string GuildCollection = "Guild";
        public const string PlayerIdentityCollection = "PlayerIdentity";
        public const string PlayerAppearanceCollection = "PlayerAppearance";

        private static string _databasePath;
        private static bool _mapperConfigured;

        public static bool IsConfigured => !string.IsNullOrEmpty(_databasePath);

        public static string DatabasePath => _databasePath;

        public static void Configure(string databasePath)
        {
            VaultWriter.Drain();
            VaultWorldSnapshots.BeforeVaultConfigured(databasePath);
            _databasePath = databasePath;
            ConfigureMapperOnce();
            EnsureContainingDirectory(databasePath);
            LeaderboardRepository.OnVaultConfigured();
            FiresCore.Identity.PlayerIdentity.OnVaultConfigured();
        }

        /// <summary>
        /// Configures the vault for the loaded world if nobody has yet: FiresConfigPaths.Worlds\&lt;world&gt;\vault.db, the
        /// per-world rule FiresNPCs' VaultOfKnowledge.EnsureVaultConfigured has used (0.2.329, for [seasons]' camp store,
        /// so a mod can use the vault without FiresNPCs). Idempotent and server-only. False off-server, before the world
        /// name is known (call again later) or when Configure throws; true when the vault is configured.
        /// </summary>
        public static bool EnsureConfiguredForWorld()
        {
            if (IsConfigured) return true;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return false;
            string world = ZNet.instance.GetWorldName();
            if (string.IsNullOrEmpty(world)) return false;
            try
            {
                string path = WorldVaultPath(world);
                Configure(path);
                FiresCore.Logging.FiresLogger.LogInfo($"[VaultDatabase] configured (per-world): {path}");
                return true;
            }
            catch (Exception ex)
            {
                FiresCore.Logging.FiresLogger.LogWarning($"[VaultDatabase] EnsureConfiguredForWorld failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>The vault file for a world: FiresConfigPaths.Worlds\&lt;world name, invalid file characters as '_'&gt;\vault.db.</summary>
        public static string WorldVaultPath(string worldName) => Path.Combine(FiresConfigPaths.Worlds, WorldFolderName(worldName), "vault.db");

        /// <summary>A world name as a folder name: invalid file-name characters become '_'; empty becomes "default".</summary>
        public static string WorldFolderName(string worldName)
        {
            if (string.IsNullOrEmpty(worldName)) return "default";
            foreach (char c in Path.GetInvalidFileNameChars()) worldName = worldName.Replace(c, '_');
            worldName = worldName.Trim();
            return worldName.Length == 0 ? "default" : worldName;
        }

        public static LiteDatabase Open()
        {
            if (!IsConfigured)
                throw new InvalidOperationException("VaultDatabase.Configure(path) must be called before Open().");

            ConfigureMapperOnce();
            return new LiteDatabase(new ConnectionString
            {
                Filename = _databasePath,
                Connection = ConnectionType.Shared
            });
        }

        private static void ConfigureMapperOnce()
        {
            if (_mapperConfigured) return;
            BsonMapper.Global.IncludeFields = true;
            BsonMapper.Global.EmptyStringToNull = false;
            BsonMapper.Global.TrimWhitespace = false;
            _mapperConfigured = true;
        }

        private static void EnsureContainingDirectory(string databasePath)
        {
            string directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }
    }
}
