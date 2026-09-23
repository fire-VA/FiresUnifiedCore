using FiresCore.Sync;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// Console commands for scanning and removing orphaned ZDOs.
    ///
    /// When a mod that added prefabs to the world is removed, the ZDOs for those
    /// prefabs remain in the world save. Their prefab hash no longer resolves to
    /// any registered prefab in ZNetScene.m_namedPrefabs, so they are invisible
    /// and non-functional - but still consume save space and will reappear if
    /// the mod is reinstalled.
    ///
    /// Architecture:
    ///   - Console commands always run on the CLIENT.
    ///   - If the client IS the server (single-player / local), scan runs directly.
    ///   - If the client is connected to a DEDICATED server, an RPC is sent to the
    ///     server which validates admin status, runs the scan/destroy, and sends
    ///     results back via a response RPC.
    ///
    /// Commands:
    ///   zdo_scan_orphans              - Dry run: report orphaned ZDOs
    ///   zdo_clean_orphans             - Destroy all orphaned ZDOs
    ///   zdo_clean_orphans_hash &lt;hash&gt; - Destroy orphans with a specific prefab hash
    /// </summary>
    public static class ZdoOrphanService
    {
        private static bool _registered;
        private static ZRoutedRpc _rpcsRegisteredOn;
        private static bool _isRunning;

        // RPC names
        private const string RpcRequest = "FiresCore_ZdoOrphanRequest";
        private const string RpcResult = "FiresCore_ZdoOrphanResult";

        // Cached reflection for ZDOMan.m_objectsByID (private)
        private static FieldInfo _objectsByIdField;

        private const int ConsoleHashLimit = 25;
        private const double MaxBulkDestroyFraction = 0.05;
        private const string ReportFileName = "zdo_orphan_report.txt";

        // Written next to Core's own config so it survives the session and can be
        // diffed between scans. Every hash, with its count, is what makes a
        // targeted zdo_clean_orphans_hash possible instead of a blind bulk wipe.
        private static string WriteOrphanReport(List<KeyValuePair<int, int>> ranked, int totalOrphans, int totalZdos)
        {
            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "FiresUnifiedCore");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, ReportFileName);

                var sb = new StringBuilder();
                sb.AppendLine($"# ZDO orphan report — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"# world ZDOs: {totalZdos}, orphaned: {totalOrphans}, distinct prefab hashes: {ranked.Count}");
                sb.AppendLine("# An orphan is a ZDO whose prefab hash resolves to nothing in ZNetScene.");
                sb.AppendLine("# Removing one is permanent; a hash whose mod you intend to reinstall should be left alone.");
                sb.AppendLine("# hash\tzdos\tzdo_clean_orphans_hash <hash>");
                foreach (var kv in ranked) sb.AppendLine($"{kv.Key}\t{kv.Value}");

                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch (Exception ex)
            {
                FiresCore.Logging.FiresLogger.LogWarning($"[ZDO Cleanup] Could not write the orphan report: {ex.Message}");
                return null;
            }
        }

        // ───────────────────────────────────────────
        //  Registration
        // ───────────────────────────────────────────
        //
        // Self-registering, the way the other Core services do it, so no mod has
        // to remember to call in. The RPCs must exist on the SERVER for a client
        // command to reach anything: the scan walks ZDOMan, which only the server
        // holds in full, so the client merely asks and the server answers.

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ZdoOrphan_ZNetAwake_Patch
        {
            [HarmonyPostfix]
            private static void Postfix() => EnsureRpcsRegistered();
        }

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class ZdoOrphan_InitTerminal_Patch
        {
            [HarmonyPostfix]
            private static void Postfix() => Register();
        }

        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand("zdo_scan_orphans",
                "Scans all ZDOs and reports orphaned ones (no matching prefab). Dry run, nothing deleted.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (!ValidateInWorld(args)) return;
                    DispatchCommand(args, mode: 0);
                });

            new Terminal.ConsoleCommand("zdo_clean_orphans",
                "Scans and DESTROYS all orphaned ZDOs. World save recommended first.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (!ValidateInWorld(args)) return;
                    DispatchCommand(args, mode: 1);
                });

            // Takes a list, because a retired mod orphans one hash per prefab and
            // clearing it a hash at a time means dozens of identical commands —
            // exactly the point at which someone reaches for the bulk wipe instead.
            new Terminal.ConsoleCommand("zdo_clean_orphans_hash",
                "Destroys all ZDOs with the given prefab hash(es). Usage: zdo_clean_orphans_hash <hash> [hash2 hash3 ...]",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (!ValidateInWorld(args)) return;
                    if (args.Length < 2)
                    {
                        args.Context.AddString("Usage: zdo_clean_orphans_hash <hash> [hash2 hash3 ...]");
                        args.Context.AddString("Get hashes from zdo_scan_orphans, or from zdo_orphan_report.txt.");
                        return;
                    }

                    var hashes = new List<int>();
                    for (int i = 1; i < args.Length; i++)
                    {
                        string token = args[i]?.Trim().TrimEnd(',');
                        if (string.IsNullOrEmpty(token)) continue;
                        int parsed;
                        if (!int.TryParse(token, out parsed))
                        {
                            args.Context.AddString($"Invalid hash: {args[i]} — nothing was deleted.");
                            return;
                        }
                        if (!hashes.Contains(parsed)) hashes.Add(parsed);
                    }
                    if (hashes.Count == 0)
                    {
                        args.Context.AddString("No hashes given — nothing was deleted.");
                        return;
                    }
                    DispatchCommand(args, mode: 2, filterHashes: hashes);
                });
        }

        /// <summary>
        /// Register the RPCs. Must be called after ZRoutedRpc is available
        /// (from WaitForZNetReady or similar).
        /// </summary>
        public static void EnsureRpcsRegistered()
        {
            var current = ZRoutedRpc.instance;
            if (current == null) return;
            if (ReferenceEquals(_rpcsRegisteredOn, current)) return;

            try
            {
                current.Register<ZPackage>(RpcRequest, RPC_OnRequest);
                current.Register<ZPackage>(RpcResult, RPC_OnResult);
                _rpcsRegisteredOn = current;
                FiresCore.Logging.FiresLogger.LogInfo("[ZDO Cleanup] RPCs registered");
            }
            catch (Exception ex)
            {
                FiresCore.Logging.FiresLogger.LogWarning($"[ZDO Cleanup] Failed to register RPCs: {ex.Message}");
            }
        }

        // -----------------------------------------
        //  Validation
        // -----------------------------------------

        private static bool ValidateInWorld(Terminal.ConsoleEventArgs args)
        {
            if (_isRunning)
            {
                args.Context.AddString("[ZDO Cleanup] A scan is already in progress.");
                return false;
            }
            if (ZNet.instance == null || ZDOMan.instance == null || ZNetScene.instance == null)
            {
                args.Context.AddString("[ZDO Cleanup] Must be in a world.");
                return false;
            }
            return true;
        }

        // -----------------------------------------
        //  Command dispatch
        // -----------------------------------------

        /// <summary>
        /// Routes the command: run locally if we are the server, or send RPC if on a remote server.
        /// mode: 0=scan, 1=clean all, 2=clean by hash
        /// </summary>
        private static void DispatchCommand(Terminal.ConsoleEventArgs args, int mode, List<int> filterHashes = null)
        {
            bool isServer = ZNet.instance.IsServer();
            var hashes = (mode == 2 && filterHashes != null) ? filterHashes : new List<int>();

            if (isServer)
            {
                bool destroy = mode > 0;
                args.Context.AddString("[ZDO Cleanup] Running locally (you are the server)...");
                RunScanLocal(args.Context, destroy, hashes);
            }
            else
            {
                args.Context.AddString("[ZDO Cleanup] Sending request to server...");
                var pkg = new ZPackage();
                pkg.Write(mode);
                pkg.Write(hashes.Count);
                foreach (int h in hashes) pkg.Write(h);
                _awaitingServerResult = true;
                ZRoutedRpc.instance.InvokeRoutedRPC(RpcRequest, pkg);
                BeginServerResultWatchdog(args.Context);
            }
        }

        private const float ServerResultTimeoutSeconds = 20f;

        private static bool _awaitingServerResult;

        // A routed RPC to a server that never registered the handler is dropped
        // without any error, so the command looks like it did nothing at all.
        // Saying so after a timeout turns that silence into the actual diagnosis.
        private static void BeginServerResultWatchdog(Terminal context)
        {
            var host = FiresCore.FiresUnifiedCore.Instance;
            if (host == null) return;
            host.StartCoroutine(WatchForServerResult(context));
        }

        private static IEnumerator WatchForServerResult(Terminal context)
        {
            float deadline = Time.realtimeSinceStartup + ServerResultTimeoutSeconds;
            while (_awaitingServerResult && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (!_awaitingServerResult) yield break;

            _awaitingServerResult = false;
            _isRunning = false;
            string message =
                $"[ZDO Cleanup] The server did not acknowledge the request within {ServerResultTimeoutSeconds:0}s. " +
                "This is about the request never being ANSWERED, not about the scan being slow — the server " +
                "acknowledges first and only then starts scanning, so a long scan never lands here. " +
                "The likely cause is that FiresUnifiedCore is missing or out of date on the dedicated server: " +
                "the cleanup runs server-side and the client only asks for it. " +
                "Update it there, or run the command while hosting locally.";
            context?.AddString(message);
            FiresCore.Logging.FiresLogger.LogWarning(message);
        }

        // -----------------------------------------
        //  Server-side RPC handler
        // -----------------------------------------

        /// <summary>
        /// Called on the SERVER when a client sends a scan/clean request.
        /// Validates admin, runs the operation, sends results back.
        /// </summary>
        private static void RPC_OnRequest(long sender, ZPackage pkg)
        {
            // Only the server should process this
            if (!ZNet.instance.IsServer()) return;

            // Validate sender is admin
            if (!AdminSyncing.IsAdmin(sender))
            {
                FiresCore.Logging.FiresLogger.LogWarning($"[ZDO Cleanup] Non-admin {sender} tried to run orphan cleanup. Denied.");
                SendResult(sender, "[ZDO Cleanup] Permission denied. You must be an admin.");
                return;
            }

            int mode = pkg.ReadInt();
            int hashCount = pkg.ReadInt();
            var hashes = new List<int>(hashCount);
            for (int i = 0; i < hashCount; i++) hashes.Add(pkg.ReadInt());
            bool destroy = mode > 0;

            FiresCore.Logging.FiresLogger.LogInfo($"[ZDO Cleanup] Admin {sender} requested orphan {(destroy ? "clean" : "scan")}, " +
                                                  $"hash filter: {(hashes.Count == 0 ? "none" : string.Join(",", hashes))}");

            // Answer immediately, before the scan starts. Walking millions of ZDOs
            // takes far longer than any sane "did the server hear me" timeout, and
            // from the client the two failures look identical. This ack is what the
            // watchdog waits for, which leaves the scan free to take as long as it
            // needs without ever looking like a dropped request.
            SendResult(sender, "[ZDO Cleanup] Server accepted the request — scanning now. " +
                               "A multi-million-ZDO world takes a while; results follow when it finishes.");

            // Run on server - results collected as strings, sent back to client
            var host = FiresCore.FiresUnifiedCore.Instance;
            if (host != null && host.gameObject.activeInHierarchy)
            {
                host.StartCoroutine(ScanOnServerCoroutine(sender, destroy, hashes));
            }
            else
            {
                // Synchronous fallback
                var results = RunScanAndCollectResults(destroy, hashes);
                foreach (var line in results)
                    SendResult(sender, line);
            }
        }

        private static IEnumerator ScanOnServerCoroutine(long sender, bool destroy, List<int> filterHashes)
        {
            _isRunning = true;

            var results = RunScanAndCollectResults(destroy, filterHashes);

            // Send results back in batches to avoid overwhelming the network
            int batchCount = 0;
            foreach (var line in results)
            {
                SendResult(sender, line);
                batchCount++;
                if (batchCount >= 5)
                {
                    batchCount = 0;
                    yield return null;
                }
            }

            _isRunning = false;
        }

        private static void SendResult(long targetPeer, string message)
        {
            if (ZRoutedRpc.instance == null) return;
            var pkg = new ZPackage();
            pkg.Write(message);
            ZRoutedRpc.instance.InvokeRoutedRPC(targetPeer, RpcResult, pkg);
        }

        // -----------------------------------------
        //  Client-side result handler
        // -----------------------------------------

        /// <summary>
        /// Called on the CLIENT when the server sends back a result line.
        /// Prints it to the F5 console.
        /// </summary>
        private static void RPC_OnResult(long sender, ZPackage pkg)
        {
            _awaitingServerResult = false;
            string message = pkg.ReadString();
            // Print to the F5 console if it's open
            try
            {
                if (Console.instance != null)
                    Console.instance.AddString(message);
            }
            catch { }
            FiresCore.Logging.FiresLogger.LogInfo(message);
        }

        // -----------------------------------------
        //  Local execution (single-player / server-side)
        // -----------------------------------------

        /// <summary>
        /// Runs scan locally and prints results directly to the given Terminal.
        /// Used when the command runner IS the server.
        /// </summary>
        private static void RunScanLocal(Terminal terminal, bool destroy, List<int> filterHashes)
        {
            var host = FiresCore.FiresUnifiedCore.Instance;
            if (host != null && host.gameObject.activeInHierarchy)
            {
                host.StartCoroutine(RunScanLocalCoroutine(terminal, destroy, filterHashes));
            }
            else
            {
                var results = RunScanAndCollectResults(destroy, filterHashes);
                foreach (var line in results)
                    terminal.AddString(line);
            }
        }

        private static IEnumerator RunScanLocalCoroutine(Terminal terminal, bool destroy, List<int> filterHashes)
        {
            _isRunning = true;

            terminal.AddString("[ZDO Cleanup] Scanning...");
            yield return null;

            var results = RunScanAndCollectResults(destroy, filterHashes);
            foreach (var line in results)
            {
                terminal.AddString(line);
            }

            _isRunning = false;
        }

        // -----------------------------------------
        //  Core scan/destroy logic (runs on server)
        // -----------------------------------------

        /// <summary>
        /// Runs the full scan, optionally destroying orphans.
        /// Returns a list of result strings to display.
        /// This must run on the server (or single-player host) where ZDOMan has all ZDOs.
        /// </summary>
        private static List<string> RunScanAndCollectResults(bool destroy, List<int> filterHashes)
        {
            var results = new List<string>();

            // Get all ZDOs via reflection
            var allZdos = GetAllZdos();
            if (allZdos == null)
            {
                results.Add("[ZDO Cleanup] ERROR: Failed to access ZDOMan.m_objectsByID.");
                return results;
            }

            // Get the known prefabs lookup
            var namedPrefabs = GetNamedPrefabs();
            if (namedPrefabs == null)
            {
                results.Add("[ZDO Cleanup] ERROR: Failed to access ZNetScene.m_namedPrefabs.");
                return results;
            }

            // Snapshot to avoid concurrent modification
            var zdoSnapshot = new List<ZDO>(allZdos.Count);
            foreach (var kv in allZdos)
            {
                if (kv.Value != null)
                    zdoSnapshot.Add(kv.Value);
            }

            var filterSet = new HashSet<int>(filterHashes ?? new List<int>());

            string mode = destroy ? "CLEAN" : "SCAN";
            if (filterSet.Count > 0) mode += $" hash={string.Join(",", filterHashes)}";
            results.Add($"[ZDO Cleanup] {mode}: Scanning {zdoSnapshot.Count} ZDOs...");
            if (!destroy)
            {
                // Show available commands BEFORE the scan results so users
                // see the cleanup path immediately on long outputs without
                // scrolling. Repeated at the bottom (in RunScanAndCollectResults
                // tail) when orphans are found.
                results.Add("[ZDO Cleanup] Commands: zdo_scan_orphans | zdo_clean_orphans | zdo_clean_orphans_hash <hash>");
            }

            int totalOrphans = 0;
            int totalDestroyed = 0;
            long sessionId = ZDOMan.GetSessionID();

            // hash -> count
            var orphanCounts = new Dictionary<int, int>();
            var orphansToDestroy = new List<ZDO>();

            for (int i = 0; i < zdoSnapshot.Count; i++)
            {
                var zdo = zdoSnapshot[i];
                int prefabHash = zdo.GetPrefab();
                if (prefabHash == 0) continue;

                if (!namedPrefabs.ContainsKey(prefabHash))
                {
                    totalOrphans++;

                    int count;
                    orphanCounts.TryGetValue(prefabHash, out count);
                    orphanCounts[prefabHash] = count + 1;

                    if (destroy)
                    {
                        if (filterSet.Count > 0 && !filterSet.Contains(prefabHash))
                            continue;
                        orphansToDestroy.Add(zdo);
                    }
                }
            }

            // Report
            results.Add($"[ZDO Cleanup] Found {totalOrphans} orphaned ZDOs across {orphanCounts.Count} unknown prefab hashes.");

            if (orphanCounts.Count > 0)
            {
                var ranked = new List<KeyValuePair<int, int>>(orphanCounts);
                ranked.Sort((a, b) => b.Value.CompareTo(a.Value));

                // A real world can orphan hundreds of prefab hashes, and the
                // console keeps no scrollback worth reading at that size. The
                // console gets the worst offenders, the file gets all of them,
                // so the list can actually be worked through afterwards.
                results.Add("[ZDO Cleanup] Orphaned prefab hashes (worst first):");
                int shown = 0;
                foreach (var kv in ranked)
                {
                    if (shown++ >= ConsoleHashLimit) break;
                    results.Add($"  Hash {kv.Key} : {kv.Value} ZDOs");
                }
                if (ranked.Count > ConsoleHashLimit)
                    results.Add($"  ... {ranked.Count - ConsoleHashLimit} more hash(es) not shown here.");

                string reportPath = WriteOrphanReport(ranked, totalOrphans, zdoSnapshot.Count);
                results.Add(reportPath != null
                    ? $"[ZDO Cleanup] Full list of all {ranked.Count} hash(es) written to: {reportPath}"
                    : "[ZDO Cleanup] Could not write the report file (see log).");
            }

            // A ZDO is only "orphaned" relative to the prefabs THIS process has
            // registered, and the scan runs on the server. A server missing the
            // mod assets its clients have therefore sees a correct, fully-populated
            // world as a million orphans, and a bulk destroy would delete all of it
            // permanently. A healthy world loses a few strays; losing a large
            // fraction means the prefabs are missing here, not from the world.
            if (destroy && filterSet.Count == 0 && zdoSnapshot.Count > 0)
            {
                double orphanFraction = (double)totalOrphans / zdoSnapshot.Count;
                if (orphanFraction > MaxBulkDestroyFraction)
                {
                    results.Add($"[ZDO Cleanup] REFUSING to destroy {totalOrphans} of {zdoSnapshot.Count} ZDOs " +
                                $"({orphanFraction:P1} of the world, limit is {MaxBulkDestroyFraction:P0}).");
                    results.Add("[ZDO Cleanup] That is far too much to be genuine junk. The usual cause is that THIS");
                    results.Add("[ZDO Cleanup] machine is missing mod assets the world was built with — on a dedicated");
                    results.Add("[ZDO Cleanup] server, check that its own config folders hold the same bundles/manifests");
                    results.Add("[ZDO Cleanup] the clients have. Those ZDOs are valid; they just cannot resolve here.");
                    results.Add("[ZDO Cleanup] Nothing was deleted. Use zdo_clean_orphans_hash <hash> for a specific");
                    results.Add("[ZDO Cleanup] prefab you have confirmed is genuinely gone for good.");
                    return results;
                }
            }

            // Destroy
            if (destroy && orphansToDestroy.Count > 0)
            {
                results.Add($"[ZDO Cleanup] Destroying {orphansToDestroy.Count} orphaned ZDOs...");

                for (int i = 0; i < orphansToDestroy.Count; i++)
                {
                    var zdo = orphansToDestroy[i];
                    try
                    {
                        if (!zdo.IsOwner())
                            zdo.SetOwner(sessionId);

                        ZDOMan.instance.DestroyZDO(zdo);
                        totalDestroyed++;
                    }
                    catch (Exception ex)
                    {
                        FiresCore.Logging.FiresLogger.LogWarning($"[ZDO Cleanup] Failed to destroy ZDO {zdo.m_uid}: {ex.Message}");
                    }
                }

                results.Add($"[ZDO Cleanup] Destroyed {totalDestroyed}/{orphansToDestroy.Count} orphaned ZDOs.");
                results.Add("[ZDO Cleanup] Changes will persist on next world save.");
                FiresCore.Logging.FiresLogger.LogInfo($"[ZDO Cleanup] Destroyed {totalDestroyed} orphaned ZDOs (requested by admin)");
            }
            else if (!destroy && totalOrphans > 0)
            {
                // ALWAYS show the available follow-up commands when orphans
                // are found — previously this was easy to miss in a wall
                // of "Hash X : Y ZDOs" lines, and users reported scanning
                // successfully but not seeing how to clean.
                results.Add("");
                results.Add("[ZDO Cleanup] ┌─── Available cleanup commands ─────────────────────────");
                results.Add("[ZDO Cleanup] │  zdo_clean_orphans              ← destroy ALL orphans above");
                results.Add("[ZDO Cleanup] │  zdo_clean_orphans_hash <hash>  ← destroy only one prefab hash");
                results.Add("[ZDO Cleanup] └────────────────────────────────────────────────────────");
                results.Add("[ZDO Cleanup] Tip: world save recommended before bulk cleanup.");
            }
            else if (totalOrphans == 0)
            {
                results.Add("[ZDO Cleanup] World is clean — no orphaned ZDOs found.");
            }

            return results;
        }

        // -----------------------------------------
        //  Reflection helpers
        // -----------------------------------------

        private static Dictionary<ZDOID, ZDO> GetAllZdos()
        {
            if (ZDOMan.instance == null) return null;

            if (_objectsByIdField == null)
            {
                _objectsByIdField = typeof(ZDOMan).GetField("m_objectsByID",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_objectsByIdField == null)
            {
                FiresCore.Logging.FiresLogger.LogError("[ZDO Cleanup] Could not find ZDOMan.m_objectsByID field.");
                return null;
            }

            return _objectsByIdField.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
        }

        private static Dictionary<int, GameObject> GetNamedPrefabs()
        {
            if (ZNetScene.instance == null) return null;

            var field = typeof(ZNetScene).GetField("m_namedPrefabs",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

            if (field != null)
                return field.GetValue(ZNetScene.instance) as Dictionary<int, GameObject>;

            FiresCore.Logging.FiresLogger.LogError("[ZDO Cleanup] Could not find ZNetScene.m_namedPrefabs field.");
            return null;
        }
    }
}
