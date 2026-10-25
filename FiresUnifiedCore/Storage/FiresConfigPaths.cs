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
        // Config profiles stay small: caches live in <BepInEx>\cache\FiresRPGmaker, same relative layout.
        public static string Cache       => Ensure(Path.Combine(CacheRoot, "Debug", "Cache"));
        public static string CacheRoot   => Ensure(Path.Combine(BepInEx.Paths.BepInExRootPath, "cache", RootName));

        // A folder inside the config Root -> the same relative folder under CacheRoot (FiresServerScope.ScopedDir's per-server copies);
        // anything else is returned unchanged.
        public static string ToCacheTree(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return dir;
                string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                string root = Path.GetFullPath(Path.Combine(BepInEx.Paths.ConfigPath, RootName)).TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return CacheRoot;
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return dir;
                return Ensure(Path.Combine(CacheRoot, full.Substring(root.Length + 1)));
            }
            catch { return dir; }
        }

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
            if (string.Equals(parts[0], "cache", StringComparison.OrdinalIgnoreCase))   // Cache moved to CacheRoot
            {
                string c = Cache;
                for (int i = 1; i < parts.Length; i++) c = Path.Combine(c, parts[i]);
                return Ensure(c);
            }
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

                MigrateToCacheRoot();
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("[FiresConfigPaths] Migrate failed: " + ex.Message); }
        }

        // Debug\Cache and every per-server scoped copy (s<host> / h<worldUID> / unknown folders made by
        // FiresServerScope.ScopedDir) move from config to CacheRoot. Same volume, so each folder is one Directory.Move: a rename,
        // atomic, nothing copied, nothing deleted. A target that already has content is NEVER merged or overwritten: the old folder
        // stays in config, with a warning. One Info line per run with the count and MB.
        private static readonly System.Text.RegularExpressions.Regex _scopedName =
            new System.Text.RegularExpressions.Regex(@"^(s[A-Za-z0-9_]*\d[A-Za-z0-9_]*|h[0-9A-F]+|unknown)$");

        private static void MigrateToCacheRoot()
        {
            int moved = 0, kept = 0; long bytes = 0;
            var names = new List<string>();
            try
            {
                string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
                // 1) Debug\Cache
                MoveWhole(Path.Combine(root, "Debug", "Cache"), Path.Combine(CacheRoot, "Debug", "Cache"), ref moved, ref kept, ref bytes, names);
                // 2) scoped per-server copies anywhere under Root (NPCs\SavedNPCs\s<uid>, Root\s<uid> for dialogue cooldowns, ...)
                foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
                {
                    if (!Directory.Exists(dir)) continue;   // already moved with a parent
                    if (!_scopedName.IsMatch(Path.GetFileName(dir))) continue;
                    string rel = Path.GetFullPath(dir).Substring(root.Length + 1);
                    MoveWhole(dir, Path.Combine(CacheRoot, rel), ref moved, ref kept, ref bytes, names);
                }
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("[FiresConfigPaths] cache move stopped: " + ex.Message + " (what moved stays moved; the rest stays in config)"); }
            // World is real per-world data (player progress), never a cache: it stays in config. Its size is logged each start so
            // a growth toward the profile limit shows before it becomes a problem.
            try
            {
                long worldBytes = 0, rootBytes = 0;
                foreach (var f in Directory.GetFiles(World, "*", SearchOption.AllDirectories)) worldBytes += new FileInfo(f).Length;
                foreach (var f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories)) rootBytes += new FileInfo(f).Length;
                string msg = "[FiresConfigPaths] config\\" + RootName + " is " + (rootBytes / 1048576.0).ToString("0.0") + " MB; World (stays in config) "
                             + (worldBytes / 1048576.0).ToString("0.0") + " MB";
                if (worldBytes > 8L * 1048576L) UnityEngine.Debug.LogWarning(msg + " - nearing the 10 MB profile limit; World should move next to the world saves");
                else UnityEngine.Debug.Log(msg);
            }
            catch { }
            if (moved > 0 || kept > 0)
                UnityEngine.Debug.Log("[FiresConfigPaths] moved " + moved + " cache folder(s), " + (bytes / 1048576.0).ToString("0.0") + " MB, from config\\" + RootName
                    + " to " + CacheRoot + "; " + kept + " left in config (target already had content)"
                    + (names.Count > 0 ? ": " + string.Join(", ", names.GetRange(0, Math.Min(8, names.Count)).ToArray()) + (names.Count > 8 ? " (+" + (names.Count - 8) + ")" : "") : ""));
        }

        private static void MoveWhole(string src, string dst, ref int moved, ref int kept, ref long bytes, List<string> names)
        {
            if (!Directory.Exists(src)) return;
            if (Directory.Exists(dst) && Directory.GetFileSystemEntries(dst).Length > 0)
            {
                kept++;
                UnityEngine.Debug.LogWarning("[FiresConfigPaths] '" + dst + "' already has content; '" + src + "' stays in config (not merged, not deleted)");
                return;
            }
            long size = 0;
            try { foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories)) size += new FileInfo(f).Length; } catch { }
            if (Directory.Exists(dst)) Directory.Delete(dst, false);   // empty (checked above): Ensure() may have created it
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            Directory.Move(src, dst);
            moved++; bytes += size;
            names.Add(Path.GetFileName(src));
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
