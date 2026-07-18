using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using FiresCore.Logging;
using FiresCore.Net;
using FiresCoreRoot = FiresCore.FiresUnifiedCore;

namespace FiresCore.Sync
{
    // Generic admin → server config push. Lets an admin edit ANY mod's config
    // file(s) locally (ExpandWorld spawns/locations/events yaml, EWP rules, any
    // .cfg/.yaml/.json) and push them UP to a dedicated server without FTP.
    //
    //   pushconfigs expand_world_spawns.yaml   — one exact file
    //   pushconfigs expand_world               — a whole folder (recursive)
    //   pushconfigs expand_world*              — wildcard: every matching folder + file
    //
    // Cascade-free BY DESIGN. The push is client → SERVER ONLY
    // (InvokeRoutedRPC(0L, …)); the server writes the file and STOPS. It never
    // rebroadcasts, so the sending admin never receives an echo that would
    // re-trip their own file watcher and bounce the push back — the reload
    // cascade the ad-hoc SyncFiles path has to guard against with per-file
    // "admin-synced" exclusion markers can't happen here at all. The owning mod
    // (EWD/EWS/EWL/EWP, etc.) reloads server-side via its OWN file watcher.
    //
    // Security: the RPC verifies the sender is a server admin
    // (AdminSyncing.IsAdmin) and clamps the write to BepInEx/config with a
    // config-extension allowlist and traversal rejection — an admin can
    // overwrite configs, never arbitrary server files.
    public static class ConfigPushService
    {
        private const string RpcName = "FiresCore_PushConfig";

        // Bytes per routed-RPC chunk. Well under Steam's 512 KB per-message
        // ceiling; big ExpandWorld data yaml is split across several and paced
        // by SafeRoutedRpc so a push can't flood the connection.
        private const int ChunkBytes = 350 * 1024;

        // Only these extensions may be pushed — never .dll/.bundle/.pdb even if
        // one lives in a config subfolder.
        private static readonly HashSet<string> AllowedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".yaml", ".yml", ".cfg", ".json", ".txt", ".xml", ".ini" };

        // ──────────────────────────────────────────────────────────────
        //  Registration
        // ──────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ConfigPush_ZNetAwake_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                try { ZRoutedRpc.instance.Register<ZPackage>(RpcName, RPC_PushConfig); }
                catch (Exception ex) { FiresLogger.LogWarning($"[ConfigPush] RPC register failed: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class ConfigPush_InitTerminal_Patch
        {
            private static bool _registered;
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_registered) return;
                _registered = true;
                RegisterCommand();
            }
        }

        private static void RegisterCommand()
        {
            Terminal.ConsoleEvent handler = HandlePushCommand;
            Terminal.ConsoleOptionsFetcher fetcher = BuildCompletionOptions;
            new Terminal.ConsoleCommand("pushconfigs",
                "Push BepInEx config file(s) to the server (admin only). Pattern = exact file, folder name (whole folder), or prefix* wildcard. e.g. 'pushconfigs expand_world*'",
                handler, optionsFetcher: fetcher, alwaysRefreshTabOptions: true);
            new Terminal.ConsoleCommand("pushconfig", "(alias) pushconfigs",
                handler, optionsFetcher: fetcher, alwaysRefreshTabOptions: true);
        }

        // ──────────────────────────────────────────────────────────────
        //  Client side — command handler
        // ──────────────────────────────────────────────────────────────

        private static void HandlePushCommand(Terminal.ConsoleEventArgs args)
        {
            void Reply(string msg) { try { args.Context?.AddString(msg); } catch { } }

            if (args.Args == null || args.Args.Length < 2 || string.IsNullOrWhiteSpace(args.Args[1]))
            {
                Reply("Usage: pushconfigs <pattern>");
                Reply("  exact file:   pushconfigs expand_world_spawns.yaml");
                Reply("  whole folder: pushconfigs expand_world");
                Reply("  wildcard:     pushconfigs expand_world*");
                return;
            }

            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                Reply("[ConfigPush] You are the host — config files here already ARE the server's copy. Nothing to push.");
                return;
            }
            if (!AdminSyncing.IsLocalAdmin())
            {
                Reply("[ConfigPush] Admin required to push configs to the server.");
                return;
            }
            if (ZRoutedRpc.instance == null)
            {
                Reply("[ConfigPush] Not connected to a server.");
                return;
            }

            // Everything after the command name is the pattern (allow unquoted
            // paths with spaces by rejoining, though config names rarely have them).
            string pattern = string.Join(" ", args.Args, 1, args.Args.Length - 1).Trim();

            List<(string rel, string abs)> matches;
            try { matches = ResolveMatches(pattern); }
            catch (Exception ex) { Reply($"[ConfigPush] pattern resolve failed: {ex.Message}"); return; }

            if (matches.Count == 0)
            {
                Reply($"[ConfigPush] No config file or folder matched '{pattern}'.");
                if (!pattern.Contains("*"))
                    Reply("  (bare names are matched exactly — add a trailing * for a wildcard, e.g. '" + pattern + "*')");
                return;
            }

            Reply($"[ConfigPush] Pushing {matches.Count} file(s) to the server:");
            foreach (var m in matches) Reply("   " + m.rel);

            var host = FiresCoreRoot.Instance;
            if (host == null) { Reply("[ConfigPush] core host not ready."); return; }
            host.StartCoroutine(PushCoroutine(matches, Reply));
        }

        private static IEnumerator PushCoroutine(List<(string rel, string abs)> files, Action<string> reply)
        {
            int sent = 0, failed = 0;
            foreach (var file in files)
            {
                byte[] payload;
                try { payload = File.ReadAllBytes(file.abs); }
                catch (Exception ex) { reply?.Invoke($"[ConfigPush] read failed '{file.rel}': {ex.Message}"); failed++; continue; }

                string relForWire = file.rel;   // forward-slash, config-root-relative
                var send = SafeRoutedRpc.PacedChunkedSend(
                    0L, RpcName, payload, ChunkBytes,
                    (pkg, index, count, slice) =>
                    {
                        pkg.Write(relForWire);
                        pkg.Write(index);
                        pkg.Write(count);
                        pkg.Write(slice);
                    });
                while (send.MoveNext()) yield return send.Current;
                sent++;
            }

            string tail = failed > 0 ? $" ({failed} failed to read)" : "";
            reply?.Invoke($"[ConfigPush] Done — pushed {sent} file(s){tail}. The owning mod reloads them server-side via its own file watcher.");
        }

        // ──────────────────────────────────────────────────────────────
        //  Pattern → matched files
        // ──────────────────────────────────────────────────────────────

        private static List<(string rel, string abs)> ResolveMatches(string pattern)
        {
            var root = Paths.ConfigPath;
            var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // rel → abs
            string norm = pattern.Replace('\\', '/').Trim().TrimStart('/');

            // 1) Exact directory → the whole folder, recursively.
            string absDir = Path.Combine(root, norm.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(absDir))
            {
                AddFolder(root, absDir, results);
                return Sorted(results);
            }

            // 2) Exact file — as given, or with a config extension appended.
            string absFile = Path.Combine(root, norm.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absFile) && IsAllowed(absFile))
            {
                AddFile(root, absFile, results);
                return Sorted(results);
            }
            if (!Path.HasExtension(absFile))
            {
                foreach (var ext in AllowedExtensions)
                {
                    string candidate = absFile + ext;
                    if (File.Exists(candidate)) AddFile(root, candidate, results);
                }
                if (results.Count > 0) return Sorted(results);
            }

            // 3) Wildcard ONLY (bare tokens are exact — cases 1/2 above). This
            //    keeps 'expand_prefabs_special' from silently grabbing
            //    'expand_prefabs_special_v2.yaml'.
            if (!norm.Contains("*")) return Sorted(results);

            var regex = GlobToRegex(norm);
            try
            {
                // Folders whose relpath OR name matches → whole folder.
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    string relDir = Rel(root, dir);
                    if (regex.IsMatch(relDir) || regex.IsMatch(Path.GetFileName(dir)))
                        AddFolder(root, dir, results);
                }
                // Files whose relpath OR filename matches.
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!IsAllowed(f)) continue;
                    string rel = Rel(root, f);
                    if (regex.IsMatch(rel) || regex.IsMatch(Path.GetFileName(f)))
                        AddFile(root, f, results);
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[ConfigPush] enumerate failed: {ex.Message}"); }

            return Sorted(results);
        }

        private static void AddFolder(string root, string dir, Dictionary<string, string> results)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    if (IsAllowed(f)) AddFile(root, f, results);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[ConfigPush] folder enum '{dir}' failed: {ex.Message}"); }
        }

        private static void AddFile(string root, string abs, Dictionary<string, string> results)
        {
            string rel = Rel(root, abs);
            if (!string.IsNullOrEmpty(rel)) results[rel] = abs;
        }

        private static string Rel(string root, string abs)
        {
            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(abs);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
            return full.Substring(rootFull.Length + 1).Replace('\\', '/');
        }

        private static bool IsAllowed(string path)
            => AllowedExtensions.Contains(Path.GetExtension(path));

        private static List<(string rel, string abs)> Sorted(Dictionary<string, string> results)
        {
            var list = new List<(string rel, string abs)>(results.Count);
            foreach (var kv in results) list.Add((kv.Key, kv.Value));
            list.Sort((a, b) => string.Compare(a.rel, b.rel, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static Regex GlobToRegex(string glob)
        {
            var sb = new StringBuilder("^");
            foreach (char c in glob)
            {
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append('.');
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // Tab-completion: top-level config folders (bare + prefix*) and top-level
        // config files, plus one level deep for folders whose files sit directly
        // inside (e.g. expand_world/). Bounded so a huge config tree can't lag.
        private static List<string> BuildCompletionOptions()
        {
            var opts = new List<string>();
            try
            {
                var root = Paths.ConfigPath;
                if (!Directory.Exists(root)) return opts;

                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    opts.Add(name);
                    opts.Add(name + "*");
                }
                foreach (var f in Directory.EnumerateFiles(root))
                    if (IsAllowed(f)) opts.Add(Path.GetFileName(f));
            }
            catch { }
            return opts;
        }

        // ──────────────────────────────────────────────────────────────
        //  Server side — receive, validate, write (NO rebroadcast)
        // ──────────────────────────────────────────────────────────────

        private sealed class Reassembly
        {
            public byte[][] Chunks;
            public int Received;
            public long LastTouchTicks;
        }

        private static readonly Dictionary<string, Reassembly> _inbound =
            new Dictionary<string, Reassembly>(StringComparer.Ordinal);
        private static readonly long ReassemblyTtlTicks = 60L * TimeSpan.TicksPerSecond;

        private static void RPC_PushConfig(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            try
            {
                if (!AdminSyncing.IsAdmin(sender))
                {
                    FiresLogger.LogWarning($"[ConfigPush] non-admin {sender} attempted a config push — refused.");
                    return;
                }

                string relPath = pkg.ReadString();
                int index = pkg.ReadInt();
                int count = pkg.ReadInt();
                byte[] chunk = pkg.ReadByteArray();

                if (!IsSafeRelPath(relPath))
                {
                    FiresLogger.LogWarning($"[ConfigPush] {sender} sent unsafe/blocked path '{relPath}' — refused.");
                    return;
                }
                if (count <= 0 || index < 0 || index >= count) return;

                SweepStaleReassemblies();

                string key = sender + ":" + relPath;
                if (!_inbound.TryGetValue(key, out var asm) || asm.Chunks == null || asm.Chunks.Length != count)
                {
                    asm = new Reassembly { Chunks = new byte[count][], Received = 0 };
                    _inbound[key] = asm;
                }
                if (asm.Chunks[index] == null) asm.Received++;
                asm.Chunks[index] = chunk ?? Array.Empty<byte>();
                asm.LastTouchTicks = DateTime.UtcNow.Ticks;

                if (asm.Received < count) return;   // more chunks to come
                _inbound.Remove(key);

                int total = 0;
                for (int i = 0; i < count; i++) total += asm.Chunks[i].Length;
                var full = new byte[total];
                int pos = 0;
                for (int i = 0; i < count; i++)
                {
                    Buffer.BlockCopy(asm.Chunks[i], 0, full, pos, asm.Chunks[i].Length);
                    pos += asm.Chunks[i].Length;
                }

                WriteConfigAtomic(relPath, full);
                FiresLogger.LogInfo($"[ConfigPush] admin {sender} pushed '{relPath}' ({total} bytes). Owning mod's watcher will reload it.");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[ConfigPush] RPC_PushConfig error: {ex.Message}");
            }
        }

        // relPath must be config-root-relative, forward-slash, no traversal, no
        // rooting, config extension only.
        private static bool IsSafeRelPath(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return false;
            if (relPath.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            string norm = relPath.Replace('\\', '/');
            if (norm.StartsWith("/", StringComparison.Ordinal)) return false;
            if (norm.Length > 1 && norm[1] == ':') return false;   // drive-rooted
            if (!AllowedExtensions.Contains(Path.GetExtension(norm))) return false;

            string root = Path.GetFullPath(Paths.ConfigPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(Paths.ConfigPath, norm.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteConfigAtomic(string relPath, byte[] bytes)
        {
            string abs = Path.Combine(Paths.ConfigPath, relPath.Replace('/', Path.DirectorySeparatorChar));
            string dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string tmp = abs + ".pushtmp";
            File.WriteAllBytes(tmp, bytes ?? Array.Empty<byte>());
            if (File.Exists(abs)) File.Delete(abs);
            File.Move(tmp, abs);
        }

        private static void SweepStaleReassemblies()
        {
            if (_inbound.Count == 0) return;
            long now = DateTime.UtcNow.Ticks;
            List<string> stale = null;
            foreach (var kv in _inbound)
                if (now - kv.Value.LastTouchTicks > ReassemblyTtlTicks)
                    (stale ??= new List<string>()).Add(kv.Key);
            if (stale != null) foreach (var k in stale) _inbound.Remove(k);
        }
    }
}
