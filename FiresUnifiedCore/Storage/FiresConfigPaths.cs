using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FiresCore.Storage
{
    /// <summary>
    /// The shared config root for every Fires mod: BepInEx/config/FiresRPGmaker/, grouped by feature (NPCs,
    /// Marketplace, Leaderboard, Quests, Buffs, World, UI, Territories, Guilds, Debug). All folders resolve through
    /// here so the layout is defined once, and <see cref="Migrate"/> moves existing data when it changes.
    /// </summary>
    public static class FiresConfigPaths
    {
        public const string RootName = "FiresRPGmaker";

        public static string Root => Ensure(Path.Combine(BepInEx.Paths.ConfigPath, RootName));

        // ── groups ──
        public static string Npcs        => Ensure(Path.Combine(Root, "NPCs"));
        public static string Marketplace => Ensure(Path.Combine(Root, "Marketplace"));
        public static string Leaderboard => Ensure(Path.Combine(Root, "Leaderboard"));
        public static string Quests      => Ensure(Path.Combine(Root, "Quests"));
        public static string Buffs       => Ensure(Path.Combine(Root, "Buffs"));
        public static string World       => Ensure(Path.Combine(Root, "World"));
        public static string Ui          => Ensure(Path.Combine(Root, "UI"));
        public static string Classes     => Ensure(Path.Combine(Root, "Classes"));
        public static string Debug       => Ensure(Path.Combine(Root, "Debug"));

        // ── NPCs ──
        public static string SavedNpcs        => Ensure(Path.Combine(Npcs, "SavedNPCs"));
        public static string Dialogues        => Ensure(Path.Combine(Npcs, "Dialogues"));
        public static string ServerInfos      => Ensure(Path.Combine(Npcs, "ServerInfos"));
        public static string PatrolRoutes     => Ensure(Path.Combine(Npcs, "PatrolRoutes"));
        public static string StaticRespawns   => Ensure(Path.Combine(Npcs, "StaticRespawns"));
        public static string StaticPlacements => Ensure(Path.Combine(Npcs, "StaticPlacements"));
        public static string Sounds           => Ensure(Path.Combine(Npcs, "Sounds"));

        // ── Marketplace ──
        public static string Traders            => Ensure(Path.Combine(Marketplace, "Traders"));
        public static string Bankers            => Ensure(Path.Combine(Marketplace, "Bankers"));
        public static string Gamblers           => Ensure(Path.Combine(Marketplace, "Gamblers"));
        public static string Mail               => Ensure(Path.Combine(Marketplace, "Mail"));
        public static string Transmogrifications => Ensure(Path.Combine(Marketplace, "Transmogrifications"));
        public static string Store              => Ensure(Path.Combine(Marketplace, "Store"));

        // ── Leaderboard ──
        public static string LeaderboardAchievements => Ensure(Path.Combine(Leaderboard, "Achievements"));
        public static string LeaderboardLocal        => Ensure(Path.Combine(Leaderboard, "Local"));

        // ── Quests ──
        public static string QuestDatabase => Ensure(Path.Combine(Quests, "Database"));
        public static string QuestProfiles => Ensure(Path.Combine(Quests, "Profiles"));
        public static string QuestSprites  => Ensure(Path.Combine(Quests, "Sprites"));

        // ── Buffs ──
        public static string BufferDefinitions => Ensure(Path.Combine(Buffs, "Definitions"));
        public static string BufferProfiles    => Ensure(Path.Combine(Buffs, "Profiles"));

        // ── World ──
        public static string Worlds     => Ensure(Path.Combine(World, "Worlds"));
        public static string Players    => Ensure(Path.Combine(World, "Players"));
        public static string ServerJson => Path.Combine(World, "server.json");

        // ── UI ──
        public static string UiAssets    => Ensure(Path.Combine(Ui, "Assets"));
        public static string UiLayouts   => Ensure(Path.Combine(Ui, "Layouts"));
        public static string UiOverrides => Ensure(Path.Combine(Ui, "Overrides"));
        public static string UiAudits    => Ensure(Path.Combine(Ui, "Audits"));

        // ── standalone ──
        public static string Territories => Ensure(Path.Combine(Root, "Territories"));
        public static string Guilds      => Ensure(Path.Combine(Root, "Guilds"));
        public static string Presets     => Ensure(Path.Combine(Root, "Presets"));
        public static string Recipes     => Ensure(Path.Combine(Root, "Recipes"));
        public static string Creatures   => Ensure(Path.Combine(Root, "Creatures"));
        public static string Cache       => Ensure(Path.Combine(Debug, "Cache"));

        public static string PresetsFor(string modName) =>
            Ensure(Path.Combine(Presets, string.IsNullOrWhiteSpace(modName) ? "Mod" : modName.Trim()));

        private static string Ensure(string dir)
        {
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        // Legacy FLAT folder key (as used by the file-sync layer / PushSavedConfig / config watcher), optionally
        // with a trailing subpath (e.g. "Dialogues/legacy" or "Territories/drawn"), -> its grouped absolute path.
        // Sync keys stay flat so call sites don't churn; only the physical path is remapped here.
        private static readonly Dictionary<string, string> _topMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Quests"] = "Quests/Database", ["QuestProfiles"] = "Quests/Profiles", ["QuestEvents"] = "Quests/Events",
            ["RuntimeSprites"] = "Quests/Sprites",
            ["Dialogues"] = "NPCs/Dialogues", ["ServerInfos"] = "NPCs/ServerInfos", ["SavedNPCs"] = "NPCs/SavedNPCs",
            ["PatrolRoutes"] = "NPCs/PatrolRoutes", ["StaticRespawns"] = "NPCs/StaticRespawns",
            ["StaticPlacements"] = "NPCs/StaticPlacements", ["Sounds"] = "NPCs/Sounds",
            ["Traders"] = "Marketplace/Traders", ["Bankers"] = "Marketplace/Bankers", ["Gamblers"] = "Marketplace/Gamblers",
            ["Mail"] = "Marketplace/Mail", ["Transmogrifications"] = "Marketplace/Transmogrifications", ["Marketplace"] = "Marketplace/Store",
            ["Buffers"] = "Buffs/Definitions", ["BufferProfiles"] = "Buffs/Profiles",
            ["LeaderboardAchievements"] = "Leaderboard/Achievements", ["leaderboard"] = "Leaderboard/Local",
            ["UILayouts"] = "UI/Layouts", ["UIAssets"] = "UI/Assets", ["UIOverrides"] = "UI/Overrides", ["UIAudits"] = "UI/Audits",
            ["guilds"] = "Guilds", ["Worlds"] = "World/Worlds", ["players"] = "World/Players",
            ["Territories"] = "Territories", ["Debug"] = "Debug", ["cache"] = "Debug/Cache",
        };

        public static string GroupedPath(string flatKey)
        {
            if (string.IsNullOrEmpty(flatKey)) return Root;
            var parts = flatKey.Replace('\\', '/').Trim('/').Split('/');
            string rel = _topMap.TryGetValue(parts[0], out var mapped) ? mapped : parts[0];
            for (int i = 1; i < parts.Length; i++) rel = Path.Combine(rel, parts[i]);
            return Ensure(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)));
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        // One-time migration: fold the old scattered + flat folders into the grouped layout. Runs once per
        // process (guarded), moves files (a file already at the destination wins; the stray copy is dropped),
        // and deletes emptied source folders. Safe + idempotent — a second run finds nothing to move.

        private static bool _migrated;

        // Relative-to-config OLD path -> relative-to-Root NEW subpath.
        private static readonly (string Old, string New)[] _map =
        {
            // pre-consolidation top-level folders
            ("firesnpcs/banker",       "Marketplace/Bankers"),
            ("firesnpcs/gambler",      "Marketplace/Gamblers"),
            ("firesnpcs/transmog",     "Marketplace/Transmogrifications"),
            ("firesnpcs/mail",         "Marketplace/Mail"),
            ("firesnpcs/marketplace",  "Marketplace/Store"),
            ("firesnpcs/achievement",  "Leaderboard/Achievements"),
            ("firesnpcs/leaderboard",  "Leaderboard/Local"),
            ("FiresNPCs_Sounds",       "NPCs/Sounds"),
            ("FiresNPCs_PatrolRoutes", "NPCs/PatrolRoutes"),

            // flat FiresRPGmaker/* -> grouped
            ("FiresRPGmaker/SavedNPCs",            "NPCs/SavedNPCs"),
            ("FiresRPGmaker/Dialogues",            "NPCs/Dialogues"),
            ("FiresRPGmaker/ServerInfos",          "NPCs/ServerInfos"),
            ("FiresRPGmaker/PatrolRoutes",         "NPCs/PatrolRoutes"),
            ("FiresRPGmaker/StaticRespawns",       "NPCs/StaticRespawns"),
            ("FiresRPGmaker/StaticPlacements",     "NPCs/StaticPlacements"),
            ("FiresRPGmaker/Sounds",               "NPCs/Sounds"),
            ("FiresRPGmaker/Traders",              "Marketplace/Traders"),
            ("FiresRPGmaker/Bankers",              "Marketplace/Bankers"),
            ("FiresRPGmaker/Gamblers",             "Marketplace/Gamblers"),
            ("FiresRPGmaker/Mail",                 "Marketplace/Mail"),
            ("FiresRPGmaker/Transmogrifications",  "Marketplace/Transmogrifications"),
            ("FiresRPGmaker/Marketplace",          "Marketplace/Store"),
            ("FiresRPGmaker/LeaderboardAchievements", "Leaderboard/Achievements"),
            ("FiresRPGmaker/leaderboard",          "Leaderboard/Local"),
            ("FiresRPGmaker/Quests",               "Quests/Database"),
            ("FiresRPGmaker/QuestProfiles",        "Quests/Profiles"),
            ("FiresRPGmaker/QuestEvents",          "Quests/Events"),
            ("FiresRPGmaker/RuntimeSprites",       "Quests/Sprites"),
            ("FiresRPGmaker/Buffers",              "Buffs/Definitions"),
            ("FiresRPGmaker/BufferProfiles",       "Buffs/Profiles"),
            ("FiresRPGmaker/Worlds",               "World/Worlds"),
            ("FiresRPGmaker/players",              "World/Players"),
            ("FiresRPGmaker/UIAssets",             "UI/Assets"),
            ("FiresRPGmaker/UILayouts",            "UI/Layouts"),
            ("FiresRPGmaker/UIOverrides",          "UI/Overrides"),
            ("FiresRPGmaker/UIAudits",             "UI/Audits"),
            ("FiresRPGmaker/guilds",               "Guilds"),
            ("FiresRPGmaker/cache",                "Debug/Cache"),
        };

        public static void Migrate()
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                string cfg = BepInEx.Paths.ConfigPath;
                Ensure(Root);

                foreach (var (oldRel, newRel) in _map)
                {
                    string src = Path.Combine(cfg, oldRel.Replace('/', Path.DirectorySeparatorChar));
                    string dst = Path.Combine(Root, newRel.Replace('/', Path.DirectorySeparatorChar));
                    if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                        MoveDirInto(src, dst);
                }

                // Loose server.json parked at the old FiresRPGmaker root -> World/server.json
                MoveFileInto(Path.Combine(Root, "server.json"), ServerJson);

                // Remove the emptied legacy top-level roots if nothing is left in them.
                TryDeleteEmpty(Path.Combine(cfg, "firesnpcs"));
                TryDeleteEmpty(Path.Combine(cfg, "FiresNPCs_Sounds"));
                TryDeleteEmpty(Path.Combine(cfg, "FiresNPCs_PatrolRoutes"));
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("[FiresConfigPaths] Migrate failed: " + ex.Message); }
        }

        private static void MoveDirInto(string src, string dst)
        {
            if (string.IsNullOrEmpty(src) || !Directory.Exists(src)) return;
            string srcFull = Path.GetFullPath(src).TrimEnd(Path.DirectorySeparatorChar);
            string dstFull = Path.GetFullPath(dst).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase)) return;   // same folder, nothing to do
            Ensure(dst);

            foreach (var filePath in Directory.GetFiles(src))
                MoveFileInto(filePath, Path.Combine(dst, Path.GetFileName(filePath)));

            // If the destination lives INSIDE the source (Quests -> Quests/Database, Marketplace -> Marketplace/Store,
            // leaderboard -> Leaderboard/Local), ONLY the top-level files move; never recurse into subfolders — that
            // would move the just-created destination into itself forever (the startup freeze) and drag sibling group
            // members in. The source folder simply becomes the group container.
            if (dstFull.StartsWith(srcFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;

            foreach (var sub in Directory.GetDirectories(src))
                MoveDirInto(sub, Path.Combine(dst, Path.GetFileName(sub)));
            TryDeleteEmpty(src);
        }

        private static void MoveFileInto(string src, string dst)
        {
            try
            {
                if (!File.Exists(src)) return;
                if (File.Exists(dst)) { File.Delete(src); return; }   // destination wins
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Move(src, dst);
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("[FiresConfigPaths] move '" + src + "' failed: " + ex.Message); }
        }

        private static void TryDeleteEmpty(string dir)
        {
            try { if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir, false); }
            catch { }
        }
    }
}
