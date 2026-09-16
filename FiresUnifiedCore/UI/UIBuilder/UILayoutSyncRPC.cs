using BepInEx;
using System;
using FiresCore.Net;
using FiresCore.Sync;
using FiresCore.Logging;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Syncs UI layout JSON between server and clients, GZip-compressed and chunked when it exceeds Steam's message
    /// limit (captured vanilla UIs can reach several megabytes). Admin pushes go to the server, which saves them;
    /// its file watcher then broadcasts the change to every client.
    /// </summary>
    public static class UILayoutSyncRPC
    {
        private const string RpcPush = "FiresRPGmaker_UILayout_Push";
        private const string RpcPushChunk = "FiresRPGmaker_UILayout_PushChunk";
        private const string RpcUpdate = "FiresRPGmaker_UILayout_Update";
        private const string RpcUpdateChunk = "FiresRPGmaker_UILayout_UpdateChunk";
        // Manifest exchange: server asks clients "what do you already have?" before broadcasting
        // the full UILayouts bundle on login. Prevents re-sending multi-MB layout files every
        // connect when the client already has an up-to-date copy on disk.
        private const string RpcRequestManifest = "FiresRPGmaker_UILayout_RequestManifest";
        private const string RpcClientManifest  = "FiresRPGmaker_UILayout_ClientManifest";

        /// <summary>
        /// Chunk size for sending (128KB to stay safely under Steam's internal buffer limits).
        /// Smaller chunks avoid overwhelming the send queue when many are dispatched per frame.
        /// </summary>
        private const int ChunkSize = 128 * 1024;

        private static bool _rpcsRegistered;

        // Track which ZRoutedRpc instance we registered against. After a plugin
        // hot-reload (r2modman / dnSpy) our static `_rpcsRegistered` resets to
        // false, but Valheim's ZRoutedRpc.instance survives — the names are
        // STILL in its handler dict. Without this guard, EnsureRpcsRegistered
        // tries to re-add and every call throws "An item with the same key has
        // already been added. Key: ..." (Dictionary<int,Delegate> stable-hash
        // collision against ITSELF). Same pattern as GuildSyncManager.
        private static ZRoutedRpc _registeredOn;

        /// <summary>
        /// Pending chunked data on receiver side. Key = relativePath.
        /// </summary>
        private static readonly Dictionary<string, PendingChunkedData> _pendingData =
            new Dictionary<string, PendingChunkedData>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Outbound send queue - chunks are enqueued here and drained at a rate
        /// of MaxChunksPerFrame per Update tick to avoid flooding Steam's send buffer.
        /// </summary>
        private static readonly Queue<System.Action> _sendQueue = new Queue<System.Action>();

        /// <summary>Maximum chunk RPCs sent per frame to avoid k_EResultLimitExceeded.
        /// Kept at 1 so ZSteamSocket flushes every chunk as its own Steam message; two
        /// 400 KB chunks in the same frame would concatenate into an 800 KB send and
        /// trip the 512 KB per-message cap.</summary>
        private const int MaxChunksPerFrame = 1;

        private class PendingChunkedData
        {
            public byte[] Data;
            public int TotalSize;
            public int TotalChunks;
            public int ReceivedChunks;
            public bool IsCompressed;
            public long SenderUid;
        }

        /// <summary>
        /// Server-side cache of each peer's UILayout file manifest (relativePath -> SHA256).
        /// Populated when a client replies to <see cref="RpcRequestManifest"/>. Consulted by
        /// <c>ServerConfigFileWatcher.SendAllConfigsToClientCoroutine</c> so we skip layouts
        /// whose server-side hash already matches what the client has on disk.
        /// </summary>
        private static readonly Dictionary<long, Dictionary<string, string>> _peerManifests =
            new Dictionary<long, Dictionary<string, string>>();
        private static readonly object _manifestLock = new object();

        //  Debounced codex reload
        //  ApplyLayoutLocally used to call UILayoutCodex.Reload() on every received
        //  file. During a login burst that can be 10+ files, and each reload walks
        //  the entire UILayouts directory through a hand-written JSON parser, O(N^2)
        //  on the main thread. Instead, flag the codex as dirty and let
        //  DrainSendQueue (called every Update) trigger one reload after the batch
        //  goes quiet.

        /// <summary>True if at least one layout has been written since the last reload.</summary>
        private static bool _codexDirty;
        /// <summary>UTC timestamp of the most recent layout write that set the dirty flag.</summary>
        private static DateTime _codexDirtySinceUtc;
        /// <summary>Quiet window before a deferred reload fires.</summary>
        private static readonly TimeSpan CodexReloadQuiet = TimeSpan.FromMilliseconds(250);

        private static string UILayoutsDir
        {
            get { return Path.Combine(FiresCore.Storage.FiresConfigPaths.UiLayouts); }
        }

        /// <summary>
        /// Register chunked transfer RPCs. Called once during initialization.
        /// </summary>
        public static void EnsureRpcsRegistered()
        {
            var current = ZRoutedRpc.instance;
            if (current == null) return;

            // Skip if we already registered on THIS exact ZRoutedRpc instance.
            // After a world change / server reconnect the instance is a different
            // object and our handlers don't carry over - we must re-register.
            // After a plugin hot-reload `_rpcsRegistered` is false but the instance
            // is the SAME and still has our handlers - this guard prevents the
            // duplicate-key explosion in that case.
            if (_rpcsRegistered && _registeredOn == current) return;

            // Each Register call is wrapped individually. Previously a single shared
            // try/catch aborted the WHOLE registration pass when any one RPC name
            // collided with something already registered (historically this happened
            // when Valheim stable-hashed two distinct names to the same key; it
            // throws "An item with the same key has already been added. Key: ..."
            // on Dictionary<int, Delegate>). That silent abort left the manifest
            // RPCs unregistered and every login thereafter fell back to "no manifest
            // reply - send everything", defeating the entire diff-sync system. Per-
            // call isolation means one bad name still breaks itself but doesn't take
            // the others down with it.
            TryRegister(RpcPush, RPC_OnPush);
            TryRegister(RpcPushChunk, RPC_OnPushChunk);
            TryRegister(RpcUpdate, RPC_OnUpdate);
            TryRegister(RpcUpdateChunk, RPC_OnUpdateChunk);
            TryRegister(RpcRequestManifest, RPC_OnRequestManifest);
            TryRegister(RpcClientManifest,  RPC_OnClientManifest);

            _rpcsRegistered = true;
            _registeredOn = current;
            Debug.Log("[UILayoutSyncRPC] Chunked transfer RPCs registered");
        }

        private static void TryRegister(string rpcName, Action<long, ZPackage> handler)
        {
            try
            {
                ZRoutedRpc.instance.Register<ZPackage>(rpcName, handler);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] Register('{rpcName}') failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Must be called once per frame (e.g., from a MonoBehaviour.Update) to drain
        /// the outbound send queue at a controlled rate. Sends up to MaxChunksPerFrame
        /// queued RPCs per call.
        /// </summary>
        public static void DrainSendQueue()
        {
            int sent = 0;
            while (_sendQueue.Count > 0 && sent < MaxChunksPerFrame)
            {
                var action = _sendQueue.Dequeue();
                try { action(); } catch (Exception ex)
                {
                    Debug.LogWarning($"[UILayoutSyncRPC] Error sending queued chunk: {ex.Message}");
                }
                sent++;
            }

            TickDeferredCodexReload();
        }

        /// <summary>
        /// If any received layout was applied and the quiet window has elapsed, run one
        /// full <see cref="UILayoutCodex.Reload"/> and clear the dirty flag. Collapses a
        /// login-burst's worth of per-file reloads into a single codex rebuild.
        /// </summary>
        private static void TickDeferredCodexReload()
        {
            if (!_codexDirty) return;
            if ((DateTime.UtcNow - _codexDirtySinceUtc) < CodexReloadQuiet) return;

            _codexDirty = false;
            try
            {
                UILayoutCodex.Reload();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] Deferred UILayoutCodex.Reload failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Marks the codex as needing a reload. Call site short-hand so callers that apply
        /// layouts outside <see cref="ApplyLayoutLocally"/> can participate in the debounce.
        /// </summary>
        private static void MarkCodexDirty()
        {
            _codexDirty = true;
            _codexDirtySinceUtc = DateTime.UtcNow;
        }

        //  Admin Push (Client -> Server)

        /// <summary>
        /// Admin client pushes a UI layout JSON file to the server.
        /// Uses compression + chunking if the payload exceeds Steam's max message size.
        /// </summary>
        /// <param name="relativePath">Relative file path within UILayouts/ (e.g., "custom/my_panel.json")</param>
        /// <param name="jsonContent">Full JSON content, or empty/null to delete</param>
        public static void PushLayoutToServer(string relativePath, string jsonContent)
        {
            if (ZRoutedRpc.instance == null || ZNet.instance == null) return;

            try
            {
                // Apply locally immediately
                ApplyLayoutLocally(relativePath, jsonContent);

                // For delete operations, send a small single-message RPC
                if (string.IsNullOrEmpty(jsonContent))
                {
                    var pkg = new ZPackage();
                    pkg.Write(relativePath);
                    pkg.Write(false); // not compressed
                    pkg.Write(0); // totalSize = 0 means delete
                    pkg.Write(new byte[0]);
                    SafeRoutedRpc.InvokeSafe(RpcPush, pkg);
                    Debug.Log($"[UILayoutSyncRPC] Pushed layout delete to server: {relativePath}");
                    return;
                }

                // Compress the JSON content
                byte[] rawBytes = Encoding.UTF8.GetBytes(jsonContent);
                byte[] dataToSend = rawBytes;
                bool isCompressed = false;

                byte[] compressed = CompressBytes(rawBytes);
                if (compressed != null && compressed.Length < rawBytes.Length)
                {
                    dataToSend = compressed;
                    isCompressed = true;
                }

                int totalSize = dataToSend.Length;
                int totalChunks = (int)Math.Ceiling((double)totalSize / ChunkSize);

                if (totalSize <= ChunkSize)
                {
                    // Small enough for a single message
                    var pkg = new ZPackage();
                    pkg.Write(relativePath);
                    pkg.Write(isCompressed);
                    pkg.Write(totalSize);
                    pkg.Write(dataToSend);
                    SafeRoutedRpc.InvokeSafe(RpcPush, pkg);

                    Debug.Log($"[UILayoutSyncRPC] Pushed layout to server: {relativePath} " +
                        $"({totalSize} bytes, compressed={isCompressed}, raw={rawBytes.Length})");
                }
                else
                {
                    // Enqueue chunks for throttled sending - avoids flooding Steam's buffer
                    for (int i = 0; i < totalChunks; i++)
                    {
                        int chunkIndex = i;
                        int offset = chunkIndex * ChunkSize;
                        int chunkLen = Math.Min(ChunkSize, totalSize - offset);
                        byte[] chunk = new byte[chunkLen];
                        Array.Copy(dataToSend, offset, chunk, 0, chunkLen);

                        _sendQueue.Enqueue(() =>
                        {
                            if (ZRoutedRpc.instance == null) return;
                            var pkg = new ZPackage();
                            pkg.Write(relativePath);
                            pkg.Write(isCompressed);
                            pkg.Write(totalSize);
                            pkg.Write(totalChunks);
                            pkg.Write(chunkIndex);
                            pkg.Write(chunkLen);
                            pkg.Write(chunk);
                            // Size-gated dispatch: refuses any chunk that somehow ballooned
                            // past the 480 KB safe ceiling instead of blowing up Steam.
                            SafeRoutedRpc.InvokeSafe(RpcPushChunk, pkg);
                        });
                    }

                    Debug.Log($"[UILayoutSyncRPC] Queued {totalChunks} chunks to push to server: {relativePath} " +
                        $"({totalSize} bytes, compressed={isCompressed}, raw={rawBytes.Length})");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] Failed to push layout to server: {ex.Message}");
            }
        }

        //  Server Broadcast (Server -> Client)

        /// <summary>
        /// Server sends a layout file to a specific client peer, using chunking if needed.
        /// Called from ServerConfigFileWatcher when broadcasting file changes.
        /// </summary>
        /// <param name="peerUid">Target peer UID</param>
        /// <param name="relativePath">Relative path within UILayouts/</param>
        /// <param name="content">JSON content (or empty for delete)</param>
        public static void SendLayoutToClient(long peerUid, string relativePath, string content)
        {
            if (ZRoutedRpc.instance == null) return;

            try
            {
                if (string.IsNullOrEmpty(content))
                {
                    // Delete notification - always fits in one message
                    var pkg = new ZPackage();
                    pkg.Write(relativePath);
                    pkg.Write(false);
                    pkg.Write(0);
                    pkg.Write(new byte[0]);
                    SafeRoutedRpc.InvokeSafe(peerUid, RpcUpdate, pkg);
                    return;
                }

                byte[] rawBytes = Encoding.UTF8.GetBytes(content);
                byte[] dataToSend = rawBytes;
                bool isCompressed = false;

                byte[] compressed = CompressBytes(rawBytes);
                if (compressed != null && compressed.Length < rawBytes.Length)
                {
                    dataToSend = compressed;
                    isCompressed = true;
                }

                int totalSize = dataToSend.Length;
                int totalChunks = (int)Math.Ceiling((double)totalSize / ChunkSize);

                if (totalSize <= ChunkSize)
                {
                    var pkg = new ZPackage();
                    pkg.Write(relativePath);
                    pkg.Write(isCompressed);
                    pkg.Write(totalSize);
                    pkg.Write(dataToSend);
                    SafeRoutedRpc.InvokeSafe(peerUid, RpcUpdate, pkg);
                }
                else
                {
                    // Enqueue chunks for throttled sending
                    for (int i = 0; i < totalChunks; i++)
                    {
                        int chunkIndex = i;
                        int offset = chunkIndex * ChunkSize;
                        int chunkLen = Math.Min(ChunkSize, totalSize - offset);
                        byte[] chunk = new byte[chunkLen];
                        Array.Copy(dataToSend, offset, chunk, 0, chunkLen);

                        _sendQueue.Enqueue(() =>
                        {
                            if (ZRoutedRpc.instance == null) return;
                            var pkg = new ZPackage();
                            pkg.Write(relativePath);
                            pkg.Write(isCompressed);
                            pkg.Write(totalSize);
                            pkg.Write(totalChunks);
                            pkg.Write(chunkIndex);
                            pkg.Write(chunkLen);
                            pkg.Write(chunk);
                            SafeRoutedRpc.InvokeSafe(peerUid, RpcUpdateChunk, pkg);
                        });
                    }

                    Debug.Log($"[UILayoutSyncRPC] Queued {totalChunks} chunks to send to peer {peerUid}: {relativePath} " +
                        $"({totalSize} bytes, compressed={isCompressed}, raw={rawBytes.Length})");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] Failed to send layout to client {peerUid}: {ex.Message}");
            }
        }

        //  RPC Handlers - Server receives push

        private static void RPC_OnPush(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!AdminSyncing.IsAdmin(sender))
            {
                Debug.LogWarning($"[UILayoutSyncRPC] Non-admin {sender} tried to push layout - rejected");
                return;
            }

            try
            {
                string relativePath = pkg.ReadString();
                bool isCompressed = pkg.ReadBool();
                int totalSize = pkg.ReadInt();
                byte[] data = pkg.ReadByteArray();

                string jsonContent = DecodePayload(data, isCompressed, totalSize);
                ApplyLayoutOnServer(relativePath, jsonContent, sender);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] RPC_OnPush failed: {ex.Message}");
            }
        }

        private static void RPC_OnPushChunk(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!AdminSyncing.IsAdmin(sender))
            {
                Debug.LogWarning($"[UILayoutSyncRPC] Non-admin {sender} tried to push layout chunk - rejected");
                return;
            }

            try
            {
                string relativePath = pkg.ReadString();
                bool isCompressed = pkg.ReadBool();
                int totalSize = pkg.ReadInt();
                int totalChunks = pkg.ReadInt();
                int chunkIndex = pkg.ReadInt();
                int chunkLength = pkg.ReadInt();
                byte[] chunkData = pkg.ReadByteArray();

                string key = $"push_{sender}_{relativePath}";
                if (!_pendingData.TryGetValue(key, out var pending))
                {
                    pending = new PendingChunkedData
                    {
                        Data = new byte[totalSize],
                        TotalSize = totalSize,
                        TotalChunks = totalChunks,
                        ReceivedChunks = 0,
                        IsCompressed = isCompressed,
                        SenderUid = sender
                    };
                    _pendingData[key] = pending;
                }

                int offset = chunkIndex * ChunkSize;
                Array.Copy(chunkData, 0, pending.Data, offset, chunkLength);
                pending.ReceivedChunks++;

                if (pending.ReceivedChunks >= pending.TotalChunks)
                {
                    _pendingData.Remove(key);
                    string jsonContent = DecodePayload(pending.Data, pending.IsCompressed, pending.TotalSize);
                    ApplyLayoutOnServer(relativePath, jsonContent, sender);
                    Debug.Log($"[UILayoutSyncRPC] Assembled push from {sender} in {pending.TotalChunks} chunks: {relativePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] RPC_OnPushChunk failed: {ex.Message}");
            }
        }

        //  RPC Handlers - Client receives update

        private static void RPC_OnUpdate(long sender, ZPackage pkg)
        {
            try
            {
                string relativePath = pkg.ReadString();
                bool isCompressed = pkg.ReadBool();
                int totalSize = pkg.ReadInt();
                byte[] data = pkg.ReadByteArray();

                string content = DecodePayload(data, isCompressed, totalSize);
                ApplyLayoutLocally(relativePath, content);

                if (FiresLogger.VerboseEnabled)
                    FiresLogger.LogVerbose($"[UILayoutSyncRPC] Received layout update: {relativePath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] RPC_OnUpdate failed: {ex.Message}");
            }
        }

        private static void RPC_OnUpdateChunk(long sender, ZPackage pkg)
        {
            try
            {
                string relativePath = pkg.ReadString();
                bool isCompressed = pkg.ReadBool();
                int totalSize = pkg.ReadInt();
                int totalChunks = pkg.ReadInt();
                int chunkIndex = pkg.ReadInt();
                int chunkLength = pkg.ReadInt();
                byte[] chunkData = pkg.ReadByteArray();

                string key = $"update_{relativePath}";
                if (!_pendingData.TryGetValue(key, out var pending))
                {
                    pending = new PendingChunkedData
                    {
                        Data = new byte[totalSize],
                        TotalSize = totalSize,
                        TotalChunks = totalChunks,
                        ReceivedChunks = 0,
                        IsCompressed = isCompressed
                    };
                    _pendingData[key] = pending;
                }

                int offset = chunkIndex * ChunkSize;
                Array.Copy(chunkData, 0, pending.Data, offset, chunkLength);
                pending.ReceivedChunks++;

                if (pending.ReceivedChunks >= pending.TotalChunks)
                {
                    _pendingData.Remove(key);
                    string content = DecodePayload(pending.Data, pending.IsCompressed, pending.TotalSize);
                    ApplyLayoutLocally(relativePath, content);
                    Debug.Log($"[UILayoutSyncRPC] Assembled layout update in {pending.TotalChunks} chunks: {relativePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] RPC_OnUpdateChunk failed: {ex.Message}");
            }
        }

        //  Shared helpers

        /// <summary>
        /// Applies a layout file locally (save to disk + reload codex).
        /// Used by both the pushing admin client and receiving clients.
        ///
        /// Fast path: if the target file already exists and its SHA256 matches the
        /// incoming JSON we skip both the disk write and the codex reload - which is the
        /// common case once the manifest-exchange pipeline is warm (see
        /// <see cref="RequestManifestFromPeer"/>).
        /// Otherwise we write the file and mark the codex dirty so
        /// <see cref="TickDeferredCodexReload"/> runs a single reload after the batch
        /// quiesces.
        /// </summary>
        private static void ApplyLayoutLocally(string relativePath, string jsonContent)
        {
            try
            {
                string fullPath = Path.Combine(UILayoutsDir, relativePath);
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (string.IsNullOrEmpty(jsonContent))
                {
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                        MarkCodexDirty();
                    }
                    return;
                }

                // Content-hash short-circuit: skip the write + reload if the on-disk copy
                // is already byte-identical. Avoids rewriting 5 MB JSON on every login.
                if (File.Exists(fullPath))
                {
                    try
                    {
                        string existing = File.ReadAllText(fullPath);
                        if (string.Equals(
                                ComputeSha256(existing),
                                ComputeSha256(jsonContent),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            if (FiresLogger.VerboseEnabled)
                                FiresLogger.LogVerbose($"[UILayoutSyncRPC] Skipped write (hash match): {relativePath}");
                            return;
                        }
                    }
                    catch
                    {
                        // Fall through to write - a corrupt/unreadable file is better replaced.
                    }
                }

                File.WriteAllText(fullPath, jsonContent);

                // Incremental upsert — parse THIS one layout and put it straight into the codex
                // instead of a full disk LoadAll. Files arrive seconds apart over a slow login sync,
                // so the old per-file MarkCodexDirty (250ms debounce) still fired one full reload per
                // file = 29 LoadAll passes = a multi-second main-thread stall + 29 log lines. O(1) now.
                try
                {
                    var def = UILayoutSerializer.Deserialize(jsonContent);
                    if (def != null && !string.IsNullOrEmpty(def.UID)) UILayoutCodex.AddOrReplace(def);
                    else MarkCodexDirty();
                }
                catch { MarkCodexDirty(); }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] Error applying layout locally: {ex.Message}");
            }
        }

        /// <summary>
        /// Server-side: saves the layout file and lets the FileWatcher broadcast to other clients.
        /// </summary>
        private static void ApplyLayoutOnServer(string relativePath, string jsonContent, long senderUid)
        {
            try
            {
                string fullPath = Path.Combine(UILayoutsDir, relativePath);
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // Mark as admin-synced so FileWatcher excludes the sender from broadcast
                UIBuilderHost.MarkFileAdminSynced?.Invoke("UILayouts", relativePath, senderUid);

                if (string.IsNullOrEmpty(jsonContent))
                {
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);
                    Debug.Log($"[UILayoutSyncRPC] Admin {senderUid} deleted layout: {relativePath}");
                }
                else
                {
                    File.WriteAllText(fullPath, jsonContent);
                    Debug.Log($"[UILayoutSyncRPC] Admin {senderUid} saved layout: {relativePath} ({jsonContent.Length} chars)");
                }

                // Reload on server
                UILayoutCodex.Reload();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] Error applying layout on server: {ex.Message}");
            }
        }

        /// <summary>
        /// Decode a received payload (decompress if needed, convert to string).
        /// Returns null/empty for delete operations (totalSize == 0).
        /// </summary>
        private static string DecodePayload(byte[] data, bool isCompressed, int totalSize)
        {
            if (totalSize == 0 || data == null || data.Length == 0)
                return null;

            byte[] rawBytes = isCompressed ? DecompressBytes(data) : data;
            if (rawBytes == null || rawBytes.Length == 0)
            {
                Debug.LogWarning("[UILayoutSyncRPC] Failed to decompress layout data");
                return null;
            }

            return Encoding.UTF8.GetString(rawBytes);
        }

        /// <summary>
        /// Called when a UILayouts file is received from the server via the legacy
        /// FiresRPGmaker_FileUpdate RPC. Kept for backward compatibility.
        /// </summary>
        public static void OnLayoutFileReceived(string relativePath, string content)
        {
            ApplyLayoutLocally(relativePath, content);
        }

        //  Manifest exchange - avoid re-sending unchanged layouts

        /// <summary>
        /// Server-side: ask <paramref name="peerUid"/> to enumerate its local UILayouts/
        /// directory and reply with a {relativePath - sha256} manifest. The reply is stored
        /// in <see cref="_peerManifests"/> and consumed by
        /// <see cref="TryGetPeerManifest"/>. Safe to call multiple times - the latest reply
        /// wins.
        /// </summary>
        public static void RequestManifestFromPeer(long peerUid)
        {
            if (ZRoutedRpc.instance == null) return;
            try
            {
                // Drop any stale manifest for this peer so callers can detect whether a
                // fresh reply has arrived by polling TryGetPeerManifest.
                lock (_manifestLock) { _peerManifests.Remove(peerUid); }
                SafeRoutedRpc.InvokeSafe(peerUid, RpcRequestManifest, new ZPackage());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] RequestManifestFromPeer({peerUid}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns true if we already received a manifest from <paramref name="peerUid"/>.
        /// The dictionary (relative path - sha256 hex) is returned by reference - do not
        /// mutate it.
        /// </summary>
        public static bool TryGetPeerManifest(long peerUid, out Dictionary<string, string> manifest)
        {
            lock (_manifestLock)
            {
                if (_peerManifests.TryGetValue(peerUid, out manifest))
                    return true;
            }
            manifest = null;
            return false;
        }

        /// <summary>
        /// Clears any cached manifest for a peer (on disconnect).
        /// </summary>
        public static void ForgetPeerManifest(long peerUid)
        {
            lock (_manifestLock) { _peerManifests.Remove(peerUid); }
        }

        /// <summary>
        /// Hex-lowercase SHA256 of the UTF-8 encoding of <paramref name="text"/>. Used by
        /// both sides so hashes match byte-for-byte.
        /// </summary>
        public static string ComputeSha256(string text)
        {
            if (text == null) text = string.Empty;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // Server - client
        private static void RPC_OnRequestManifest(long sender, ZPackage pkg)
        {
            // Only clients respond; a server receiving this (e.g. loopback) can ignore it.
            if (ZNet.instance != null && ZNet.instance.IsServer()) return;
            if (ZRoutedRpc.instance == null) return;

            try
            {
                var manifest = BuildLocalManifest();
                var reply = new ZPackage();
                reply.Write(manifest.Count);
                foreach (var kv in manifest)
                {
                    reply.Write(kv.Key);
                    reply.Write(kv.Value);
                }
                // Invoke with peerUid=0 routes to the server (the peer we're connected to).
                SafeRoutedRpc.InvokeSafe(0L, RpcClientManifest, reply);

                if (FiresLogger.VerboseEnabled)
                    FiresLogger.LogVerbose($"[UILayoutSyncRPC] Sent local manifest ({manifest.Count} file(s)) to server");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] RPC_OnRequestManifest failed: {ex.Message}");
            }
        }

        // Client - server
        private static void RPC_OnClientManifest(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            try
            {
                int count = pkg.ReadInt();
                var dict = new Dictionary<string, string>(count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    string rel = pkg.ReadString();
                    string sha = pkg.ReadString();
                    if (!string.IsNullOrEmpty(rel) && sha != null)
                        dict[rel] = sha;
                }
                lock (_manifestLock) { _peerManifests[sender] = dict; }
                Debug.Log($"[UILayoutSyncRPC] Received manifest from peer {sender}: {dict.Count} file(s)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] RPC_OnClientManifest failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Walks <see cref="UILayoutsDir"/> and returns {relativePath - sha256} for every
        /// <c>*.json</c> file present locally. Used by clients to build their reply to
        /// <see cref="RpcRequestManifest"/>.
        /// </summary>
        private static Dictionary<string, string> BuildLocalManifest()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string root = UILayoutsDir;
                if (!Directory.Exists(root)) return result;

                string[] files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        string rel = file.Substring(root.Length)
                                         .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                         .Replace(Path.DirectorySeparatorChar, '/');
                        string content = File.ReadAllText(file);
                        result[rel] = ComputeSha256(content);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[UILayoutSyncRPC] Manifest: skipping '{file}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSyncRPC] BuildLocalManifest failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Compress bytes using GZip.
        /// </summary>
        private static byte[] CompressBytes(byte[] input)
        {
            if (input == null || input.Length == 0) return null;
            try
            {
                using (var output = new MemoryStream())
                {
                    using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
                    {
                        gzip.Write(input, 0, input.Length);
                    }
                    return output.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Decompress GZip bytes.
        /// </summary>
        private static byte[] DecompressBytes(byte[] input)
        {
            if (input == null || input.Length == 0) return null;
            try
            {
                using (var inputMs = new MemoryStream(input))
                using (var gzip = new GZipStream(inputMs, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    int bytesRead;
                    while ((bytesRead = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, bytesRead);
                    }
                    return output.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
