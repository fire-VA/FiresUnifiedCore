using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using FiresCore.Logging;
using FiresCore.Net;
using FiresCoreRoot = FiresCore.FiresUnifiedCore;

namespace FiresCore.Sync
{
    // Generic admin ← server config PULL — the mirror of ConfigPushService. Lets an
    // admin fetch the server's live copy of ANY mod's config file(s) down to their
    // own BepInEx/config without FTP:
    //
    //   pullconfigs expand_world_spawns.yaml   — one exact file
    //   pullconfigs expand_world               — a whole folder (recursive)
    //   pullconfigs expand_world*              — wildcard: every matching folder + file
    //
    // Same pattern semantics, same tab-completion and the same extension allowlist
    // as pushconfigs — both share ConfigPushService.ResolveMatches / IsSafeRelPath /
    // WriteConfigAtomic / BuildCompletionOptions so the two commands can't drift.
    //
    // Flow: client sends the PATTERN to the server; the server (after verifying the
    // requester is an admin) resolves it against ITS OWN config tree and streams each
    // file back to that one client — never broadcast, so no other player sees or
    // receives anything. The client writes into its own config folder, overwriting
    // its local copy with the server's.
    //
    // Security: the request RPC gates on AdminSyncing.IsAdmin(sender) server-side, and
    // the client only accepts inbound file data while it has a pull in flight (and
    // still validates every relpath against the config root + extension allowlist), so
    // a rogue peer can't write files onto an admin's machine.
    public static class ConfigPullService
    {
        private const string ReqRpc = "FiresCore_PullConfigReq";
        private const string DataRpc = "FiresCore_PullConfigData";
        private const string MsgRpc = "FiresCore_PullConfigMsg";

        private const int ChunkBytes = 350 * 1024;

        // How long the client keeps accepting inbound pull data after asking.
        private static readonly long PullWindowTicks = 180L * TimeSpan.TicksPerSecond;
        private static long _pullDeadlineTicks;
        private static Terminal _replyTerminal;
        private static int _received;

        // ──────────────────────────────────────────────────────────────
        //  Registration
        // ──────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ConfigPull_ZNetAwake_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                try
                {
                    ZRoutedRpc.instance.Register<ZPackage>(ReqRpc, RPC_PullRequest);
                    ZRoutedRpc.instance.Register<ZPackage>(DataRpc, RPC_PullData);
                    ZRoutedRpc.instance.Register<ZPackage>(MsgRpc, RPC_PullMsg);
                }
                catch (Exception ex) { FiresLogger.LogWarning($"[ConfigPull] RPC register failed: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class ConfigPull_InitTerminal_Patch
        {
            private static bool _registered;
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (_registered) return;
                _registered = true;
                Terminal.ConsoleEvent handler = HandlePullCommand;
                Terminal.ConsoleOptionsFetcher fetcher = ConfigPushService.BuildCompletionOptions;
                new Terminal.ConsoleCommand("pullconfigs",
                    "Pull BepInEx config file(s) DOWN from the server (admin only). Pattern = exact file, folder name (whole folder), or prefix* wildcard. e.g. 'pullconfigs expand_world*'",
                    handler, optionsFetcher: fetcher, alwaysRefreshTabOptions: true);
                new Terminal.ConsoleCommand("pullconfig", "(alias) pullconfigs",
                    handler, optionsFetcher: fetcher, alwaysRefreshTabOptions: true);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Client side — request + receive
        // ──────────────────────────────────────────────────────────────

        private static void HandlePullCommand(Terminal.ConsoleEventArgs args)
        {
            void Reply(string msg) { try { args.Context?.AddString(msg); } catch { } }

            if (args.Args == null || args.Args.Length < 2 || string.IsNullOrWhiteSpace(args.Args[1]))
            {
                Reply("Usage: pullconfigs <pattern>");
                Reply("  exact file:   pullconfigs expand_world_spawns.yaml");
                Reply("  whole folder: pullconfigs expand_world");
                Reply("  wildcard:     pullconfigs expand_world*");
                return;
            }

            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                Reply("[ConfigPull] You are the host — config files here already ARE the server's copy. Nothing to pull.");
                return;
            }
            if (!AdminSyncing.IsLocalAdmin())
            {
                Reply("[ConfigPull] Admin required to pull configs from the server.");
                return;
            }
            if (ZRoutedRpc.instance == null)
            {
                Reply("[ConfigPull] Not connected to a server.");
                return;
            }

            string pattern = string.Join(" ", args.Args, 1, args.Args.Length - 1).Trim();

            _replyTerminal = args.Context;
            _received = 0;
            _pullDeadlineTicks = DateTime.UtcNow.Ticks + PullWindowTicks;
            _inbound.Clear();

            var pkg = new ZPackage();
            pkg.Write(pattern);
            SafeRoutedRpc.InvokeSafe(0L, ReqRpc, pkg);

            Reply($"[ConfigPull] Requested '{pattern}' from the server — matching files will be written into your BepInEx/config.");
        }

        // Server → this client: one status/summary line, printed to the console.
        private static void RPC_PullMsg(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (ZNet.instance != null && ZNet.instance.IsServer()) return;   // server doesn't print its own replies
            try { ClientReply(pkg.ReadString()); } catch { }
        }

        private static void ClientReply(string msg)
        {
            FiresLogger.LogInfo(msg);
            try { _replyTerminal?.AddString(msg); }
            catch { }
        }

        private sealed class Reassembly
        {
            public byte[][] Chunks;
            public int Received;
        }

        private static readonly Dictionary<string, Reassembly> _inbound =
            new Dictionary<string, Reassembly>(StringComparer.Ordinal);

        // Server → this client: a chunk of one config file. Written into OUR config folder.
        private static void RPC_PullData(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (ZNet.instance != null && ZNet.instance.IsServer()) return;

            try
            {
                // Only accept while we actually have a pull in flight — otherwise ignore.
                if (DateTime.UtcNow.Ticks > _pullDeadlineTicks)
                {
                    FiresLogger.LogWarning("[ConfigPull] dropped unsolicited config data (no pull in flight).");
                    return;
                }

                string relPath = pkg.ReadString();
                int index = pkg.ReadInt();
                int count = pkg.ReadInt();
                byte[] chunk = pkg.ReadByteArray();

                if (!ConfigPushService.IsSafeRelPath(relPath))
                {
                    FiresLogger.LogWarning($"[ConfigPull] refused unsafe inbound path '{relPath}'.");
                    return;
                }
                if (count <= 0 || index < 0 || index >= count) return;

                if (!_inbound.TryGetValue(relPath, out var asm) || asm.Chunks == null || asm.Chunks.Length != count)
                {
                    asm = new Reassembly { Chunks = new byte[count][], Received = 0 };
                    _inbound[relPath] = asm;
                }
                if (asm.Chunks[index] == null) asm.Received++;
                asm.Chunks[index] = chunk ?? Array.Empty<byte>();

                if (asm.Received < count) return;
                _inbound.Remove(relPath);

                int total = 0;
                for (int i = 0; i < count; i++) total += asm.Chunks[i].Length;
                var full = new byte[total];
                int pos = 0;
                for (int i = 0; i < count; i++)
                {
                    Buffer.BlockCopy(asm.Chunks[i], 0, full, pos, asm.Chunks[i].Length);
                    pos += asm.Chunks[i].Length;
                }

                ConfigPushService.WriteConfigAtomic(relPath, full);
                _received++;
                ClientReply($"   pulled {relPath} ({total} bytes)");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[ConfigPull] RPC_PullData error: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Server side — resolve against the server's config tree, stream back
        // ──────────────────────────────────────────────────────────────

        private static void RPC_PullRequest(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            try
            {
                if (!AdminSyncing.IsAdmin(sender))
                {
                    FiresLogger.LogWarning($"[ConfigPull] non-admin {sender} attempted a config pull — refused.");
                    SendMsg(sender, "[ConfigPull] Admin required — request refused by the server.");
                    return;
                }

                string pattern = pkg.ReadString();
                if (string.IsNullOrWhiteSpace(pattern)) return;

                List<(string rel, string abs)> matches;
                try { matches = ConfigPushService.ResolveMatches(pattern); }
                catch (Exception ex)
                {
                    SendMsg(sender, $"[ConfigPull] server pattern resolve failed: {ex.Message}");
                    return;
                }

                if (matches.Count == 0)
                {
                    SendMsg(sender, $"[ConfigPull] No config file or folder on the SERVER matched '{pattern}'.");
                    if (!pattern.Contains("*"))
                        SendMsg(sender, "  (bare names are matched exactly — add a trailing * for a wildcard, e.g. '" + pattern + "*')");
                    return;
                }

                SendMsg(sender, $"[ConfigPull] Server is sending {matches.Count} file(s):");

                var host = FiresCoreRoot.Instance;
                if (host == null)
                {
                    SendMsg(sender, "[ConfigPull] server core host not ready.");
                    return;
                }
                host.StartCoroutine(SendFilesCoroutine(sender, matches));
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[ConfigPull] RPC_PullRequest error: {ex.Message}");
            }
        }

        private static IEnumerator SendFilesCoroutine(long target, List<(string rel, string abs)> files)
        {
            int sent = 0, failed = 0;
            foreach (var file in files)
            {
                byte[] payload;
                try { payload = File.ReadAllBytes(file.abs); }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[ConfigPull] read failed '{file.rel}': {ex.Message}");
                    failed++;
                    continue;
                }

                string relForWire = file.rel;
                var send = SafeRoutedRpc.PacedChunkedSend(
                    target, DataRpc, payload, ChunkBytes,
                    (p, index, count, slice) =>
                    {
                        p.Write(relForWire);
                        p.Write(index);
                        p.Write(count);
                        p.Write(slice);
                    });
                while (send.MoveNext()) yield return send.Current;
                sent++;
            }

            string tail = failed > 0 ? $" ({failed} failed to read on the server)" : "";
            SendMsg(target, $"[ConfigPull] Done — sent {sent} file(s){tail}. Your local copies now match the server's.");
        }

        // Shared admin-reply channel: ConfigPushService reports its per-file merge outcomes
        // through this same RPC so both commands print results the same way.
        internal static void SetReplyTerminal(Terminal t) => _replyTerminal = t;

        internal static void SendMsg(long target, string message)
        {
            try
            {
                var pkg = new ZPackage();
                pkg.Write(message ?? "");
                SafeRoutedRpc.InvokeSafe(target, MsgRpc, pkg);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[ConfigPull] SendMsg failed: {ex.Message}"); }
        }
    }
}
