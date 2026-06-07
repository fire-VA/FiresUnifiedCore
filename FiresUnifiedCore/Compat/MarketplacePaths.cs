using System.IO;
using BepInEx;

namespace FiresCore.Compat
{
    // Resolves Marketplace's on-disk config tree for drop-in compat. Transcribed from kg.Marketplace's
    // Market_Paths.cs (v9.8.1) so an existing BepInEx/config/Marketplace/ tree is read unchanged: the
    // per-module importers point CfgParser at these folders, and DBFile feeds FiresCore.Storage.VaultDatabase.
    //
    // Root = config/Marketplace/. Most module configs live under Configs/; the LiteDB file under SavedData/;
    // PlayerTags + DistancedUI + DiscordWebhooks are their own top-level subfolders. (DB.db path is
    // overridable in MP via the "Database/Database File Path" cfg key; default shown here.)
    public static class MarketplacePaths
    {
        public static string MainPath => Path.Combine(Paths.ConfigPath, "Marketplace");
        private static string ConfigsFolder => Path.Combine(MainPath, "Configs");
        private static string DataFolder => Path.Combine(MainPath, "SavedData");

        public static string MainConfig => Path.Combine(MainPath, "MarketPlace.cfg");
        public static string DBFile => Path.Combine(DataFolder, "DB.db");
        public static string FactionsFile => Path.Combine(ConfigsFolder, "Factions.yml");

        public static string TradersFolder => Path.Combine(ConfigsFolder, "Traders");
        public static string BankersFolder => Path.Combine(ConfigsFolder, "Bankers");
        public static string TeleportersFolder => Path.Combine(ConfigsFolder, "Teleporters");
        public static string GamblersFolder => Path.Combine(ConfigsFolder, "Gamblers");
        public static string ServerInfosFolder => Path.Combine(ConfigsFolder, "ServerInfos");
        public static string TransmogrificationsFolder => Path.Combine(ConfigsFolder, "Transmogrifications");
        public static string QuestProfilesFolder => Path.Combine(ConfigsFolder, "QuestProfiles");
        public static string QuestsFolder => Path.Combine(ConfigsFolder, "Quests");
        public static string QuestEventsFolder => Path.Combine(ConfigsFolder, "QuestEvents");
        public static string DialoguesFolder => Path.Combine(ConfigsFolder, "Dialogues");
        public static string CustomSpawnDataFolder => Path.Combine(ConfigsFolder, "CustomSpawnData");
        public static string TerritoriesFolder => Path.Combine(ConfigsFolder, "Territories");
        public static string AdditionalTerritoriesFolder => Path.Combine(ConfigsFolder, "AdditionalTerritories");
        public static string LeaderboardAchievementsFolder => Path.Combine(ConfigsFolder, "LeaderboardAchievements");
        public static string BuffersFolder => Path.Combine(ConfigsFolder, "Buffers");
        public static string BufferProfilesFolder => Path.Combine(ConfigsFolder, "BufferProfiles");
        public static string LootboxesFolder => Path.Combine(ConfigsFolder, "Lootboxes");

        public static string DistancedUIConfig => Path.Combine(MainPath, "DistancedUI", "DistancedUI.cfg");
        public static string PlayerTagsConfig => Path.Combine(MainPath, "PlayerTags", "PlayerTags.cfg");

        // True when an MP config tree is present to drop-in import from.
        public static bool Exists => Directory.Exists(MainPath);
    }
}
