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

        private static string _databasePath;
        private static bool _mapperConfigured;

        public static bool IsConfigured => !string.IsNullOrEmpty(_databasePath);

        public static string DatabasePath => _databasePath;

        public static void Configure(string databasePath)
        {
            _databasePath = databasePath;
            ConfigureMapperOnce();
            EnsureContainingDirectory(databasePath);
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
