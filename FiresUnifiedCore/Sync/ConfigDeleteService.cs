using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using FiresCore.Logging;
using FiresCore.Net;

namespace FiresCore.Sync
{
    // Admin config delete, the third of pushconfigs and pullconfigs, sharing their matching, completion and
    // extension allowlist: "deleteconfig <file | folder | wildcard>" only reports the server's matches, and adding
    // "confirm" deletes them. Deleted files move to <BepInEx>/config_deleted_backups/<timestamp>/, outside the
    // config folder so no mod rescans them, and only config extensions can ever match.
    public static class ConfigDeleteService
    {
        private const string ReqRpc = "FiresCore_DeleteConfigReq";

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ConfigDelete_ZNetAwake_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                try { ZRoutedRpc.instance.Register<ZPackage>(ReqRpc, RPC_DeleteRequest); }
                catch (Exception ex) { FiresLogger.LogWarning($"[ConfigDelete] RPC register failed: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class ConfigDelete_InitTerminal_Patch
        {
            private static bool _registered;
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_registered) return;
                _registered = true;
                Terminal.ConsoleEvent handler = HandleDeleteCommand;
                Terminal.ConsoleOptionsFetcher fetcher = ConfigPushService.BuildCompletionOptions;
                const string help = "Delete BepInEx config file(s) ON THE SERVER (admin only). Lists matches first; append 'confirm' to actually delete. e.g. 'deleteconfig expand_world* confirm'";
                new Terminal.ConsoleCommand("deleteconfig", help, handler, optionsFetcher: fetcher, alwaysRefreshTabOptions: true);
                new Terminal.ConsoleCommand("deleteconfigs", "(alias) deleteconfig", handler, optionsFetcher: fetcher, alwaysRefreshTabOptions: true);
                new Terminal.ConsoleCommand("removeconfig", "(alias) deleteconfig", handler, optionsFetcher: fetcher, alwaysRefreshTabOptions: true);
                new Terminal.ConsoleCommand("removeconfigs", "(alias) deleteconfig", handler, optionsFetcher: fetcher, alwaysRefreshTabOptions: true);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Client side
        // ──────────────────────────────────────────────────────────────

        private static void HandleDeleteCommand(Terminal.ConsoleEventArgs args)
        {
            void Reply(string msg) { try { args.Context?.AddString(msg); } catch { } }

            if (args.Args == null || args.Args.Length < 2 || string.IsNullOrWhiteSpace(args.Args[1]))
            {
                Reply("Usage: deleteconfig <pattern> [confirm]");
                Reply("  list what would go:  deleteconfig expand_world*");
                Reply("  actually delete:     deleteconfig expand_world* confirm");
                return;
            }

            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                Reply("[ConfigDelete] You are the host — these config files already ARE the server's. Delete them directly.");
                return;
            }
            if (!AdminSyncing.IsLocalAdmin())
            {
                Reply("[ConfigDelete] Admin required to delete configs on the server.");
                return;
            }
            if (ZRoutedRpc.instance == null)
            {
                Reply("[ConfigDelete] Not connected to a server.");
                return;
            }

            // Trailing 'confirm' is the execute flag; everything before it is the pattern.
            var parts = new List<string>();
            for (int i = 1; i < args.Args.Length; i++) parts.Add(args.Args[i]);
            bool confirm = parts.Count > 0
                && string.Equals(parts[parts.Count - 1], "confirm", StringComparison.OrdinalIgnoreCase);
            if (confirm) parts.RemoveAt(parts.Count - 1);
            string pattern = string.Join(" ", parts.ToArray()).Trim();

            if (string.IsNullOrWhiteSpace(pattern))
            {
                Reply("[ConfigDelete] Give a pattern, e.g. 'deleteconfig expand_world*'");
                return;
            }

            ConfigPullService.SetReplyTerminal(args.Context);

            var pkg = new ZPackage();
            pkg.Write(pattern);
            pkg.Write(confirm);
            SafeRoutedRpc.InvokeSafe(0L, ReqRpc, pkg);

            Reply(confirm
                ? $"[ConfigDelete] Deleting '{pattern}' on the server…"
                : $"[ConfigDelete] Checking what '{pattern}' matches on the server…");
        }

        // ──────────────────────────────────────────────────────────────
        //  Server side
        // ──────────────────────────────────────────────────────────────

        private static void RPC_DeleteRequest(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            try
            {
                if (!AdminSyncing.IsAdmin(sender))
                {
                    FiresLogger.LogWarning($"[ConfigDelete] non-admin {sender} attempted a config delete — refused.");
                    ConfigPullService.SendMsg(sender, "[ConfigDelete] Admin required — request refused by the server.");
                    return;
                }

                string pattern = pkg.ReadString();
                bool confirm = pkg.ReadBool();
                if (string.IsNullOrWhiteSpace(pattern)) return;

                List<(string rel, string abs)> matches;
                try { matches = ConfigPushService.ResolveMatches(pattern); }
                catch (Exception ex)
                {
                    ConfigPullService.SendMsg(sender, $"[ConfigDelete] server pattern resolve failed: {ex.Message}");
                    return;
                }

                if (matches.Count == 0)
                {
                    ConfigPullService.SendMsg(sender, $"[ConfigDelete] No config file or folder on the SERVER matched '{pattern}'.");
                    if (!pattern.Contains("*"))
                        ConfigPullService.SendMsg(sender, "  (bare names are matched exactly — add a trailing * for a wildcard)");
                    return;
                }

                if (!confirm)
                {
                    ConfigPullService.SendMsg(sender, $"[ConfigDelete] '{pattern}' matches {matches.Count} file(s) on the server:");
                    foreach (var match in matches) ConfigPullService.SendMsg(sender, "   " + match.rel);
                    ConfigPullService.SendMsg(sender, $"[ConfigDelete] Nothing deleted yet. Re-run with 'confirm' to delete these {matches.Count} file(s):");
                    ConfigPullService.SendMsg(sender, $"   deleteconfig {pattern} confirm");
                    return;
                }

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string backupRoot = BackupRoot(stamp);
                int deleted = 0, failed = 0;
                var touchedDirs = new List<string>();

                foreach (var match in matches)
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(match.abs);
                        BackupBeforeDelete(match.rel, match.abs, backupRoot);
                        File.Delete(match.abs);
                        deleted++;
                        if (!string.IsNullOrEmpty(dir) && !touchedDirs.Contains(dir)) touchedDirs.Add(dir);
                        ConfigPullService.SendMsg(sender, "   deleted " + match.rel);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        FiresLogger.LogWarning($"[ConfigDelete] delete failed '{match.rel}': {ex.Message}");
                        ConfigPullService.SendMsg(sender, $"   FAILED {match.rel} — {ex.Message}");
                    }
                }

                PruneEmptyDirs(touchedDirs);

                string tail = failed > 0 ? $" ({failed} failed)" : "";
                FiresLogger.LogInfo($"[ConfigDelete] admin {sender} deleted {deleted} config file(s) matching '{pattern}'{tail}. Backup: {backupRoot}");
                ConfigPullService.SendMsg(sender, $"[ConfigDelete] Done — deleted {deleted} file(s){tail}.");
                ConfigPullService.SendMsg(sender, $"   backup kept on the server at BepInEx/config_deleted_backups/{stamp}/");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[ConfigDelete] RPC_DeleteRequest error: {ex.Message}");
            }
        }

        // <BepInEx>/config_deleted_backups/<stamp> — sibling of config, so nothing rescans it.
        private static string BackupRoot(string stamp)
        {
            string cfg = Paths.ConfigPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string bepinex = Path.GetDirectoryName(cfg) ?? cfg;
            return Path.Combine(bepinex, "config_deleted_backups", stamp);
        }

        private static void BackupBeforeDelete(string rel, string abs, string backupRoot)
        {
            try
            {
                string dest = Path.Combine(backupRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                string destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(abs, dest, true);
            }
            catch (Exception ex)
            {
                // Backup is best-effort; never block the delete the admin asked for.
                FiresLogger.LogWarning($"[ConfigDelete] backup failed for '{rel}': {ex.Message}");
            }
        }

        // Remove folders left empty by the delete (never the config root itself).
        private static void PruneEmptyDirs(List<string> dirs)
        {
            string root = Path.GetFullPath(Paths.ConfigPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var start in dirs)
            {
                string directory = start;
                try
                {
                    while (!string.IsNullOrEmpty(directory))
                    {
                        string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) break;
                        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) break;
                        if (!Directory.Exists(full)) break;
                        if (Directory.GetFileSystemEntries(full).Length != 0) break;
                        Directory.Delete(full);
                        directory = Path.GetDirectoryName(full);
                    }
                }
                catch (Exception ex) { FiresLogger.LogWarning($"[ConfigDelete] prune '{directory}' failed: {ex.Message}"); }
            }
        }
    }
}
