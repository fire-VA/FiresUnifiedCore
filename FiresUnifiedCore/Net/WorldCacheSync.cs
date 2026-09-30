using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FiresCore.Logging;
using FiresCore.Sync;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Net
{
    // ── Ship a world's generation cache to the clients that need it ──────
    // A world whose terrain is rebuilt from a local cache file is only shared
    // if every machine has that file. Without it the server and its clients
    // generate DIFFERENT GROUND from the same seed, which is not a rendering
    // difference — the server places vegetation, spawns and locations against
    // terrain no client can see.
    //
    // The transfer follows BetterContinents' model: send it once on first
    // join, and never again. ManifestDiffSync already answers "does this peer
    // have it" from a per-peer hash, so a returning client costs one hash
    // comparison and no bytes.
    //
    // Two things this deliberately does NOT do:
    //
    //  * It never loads the file into memory. SafeRoutedRpc.PacedChunkedSend
    //    takes a byte[], and a world cache is measured in hundreds of MB — the
    //    same full-size-array habit that made FiresWorldGenBase.TryLoad throw
    //    OutOfMemoryException on a loaded machine. Both ends stream through one
    //    small reusable buffer instead, so peak cost is the chunk, not the file.
    //  * It never fights FGN. Chunks go out one per SendQueuedPackages flush
    //    through the same double-yield pacing SafeRoutedRpc uses, so a transfer
    //    shares the connection rather than flooding it.
    //
    // Core owns the transfer; it does not own the paths. A mod registers what
    // its cache is and where it lives on each side, so Core needs to know
    // nothing about terrain.
    public static class WorldCacheSync
    {
        private const string RpcOffer = "FiresCore_WorldCacheOffer";
        private const string RpcWant = "FiresCore_WorldCacheWant";
        private const string RpcChunk = "FiresCore_WorldCacheChunk";
        private const string LogPrefix = "[WorldCacheSync]";
        private const string TempSuffix = ".partial";
        private const string VerifySuffix = ".verify";

        // Well under the 512 KB routed-RPC ceiling, matching ConfigPushService.
        private const int ChunkBytes = 350 * 1024;

        private sealed class CacheEntry
        {
            public string Key;
            public string DisplayName;
            public Func<string> ServerPath;
            public Func<string> ClientPath;
            public Action<string> OnClientCurrent;
        }

        private sealed class Incoming
        {
            public string TargetPath;
            public string TempPath;
            public FileStream Stream;
            public string ExpectedHash;
            public int TotalChunks;
            public int Received;
            public long Bytes;
        }

        private static readonly Dictionary<string, CacheEntry> _entries =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Incoming> _incoming =
            new Dictionary<string, Incoming>(StringComparer.OrdinalIgnoreCase);

        private sealed class KnownHash
        {
            public long Length;
            public DateTime WrittenUtc;
            public string Hash;
        }

        // Each file's SHA-256 by full path, valid while its length and write time are unchanged.
        private static readonly Dictionary<string, KnownHash> _hashes =
            new Dictionary<string, KnownHash>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _hashLock = new object();

        private static ZRoutedRpc _registeredOn;

        /// <summary>
        /// Declare a cache file this mod wants every client to hold. Both path
        /// providers are resolved lazily, because the answer depends on which
        /// world is loaded. Safe to call more than once for the same key.
        /// </summary>
        public static void Register(string key, string displayName, Func<string> serverPath, Func<string> clientPath)
            => RegisterEntry(key, displayName, serverPath, clientPath, null);

        /// <summary>
        /// As <see cref="Register(string, string, Func{string}, Func{string})"/>, and also calls
        /// <paramref name="onClientCurrent"/> with the client path once this client's copy is confirmed
        /// identical to the server's: immediately if the local file already matched the offer, or when a
        /// transfer finishes and verifies. For data a client must not act on while stale or missing — it
        /// can wait for exactly that moment instead of for a restart. Client side only; never called on
        /// the server, and never called for a transfer that failed verification.
        /// </summary>
        public static void Register(string key, string displayName, Func<string> serverPath, Func<string> clientPath,
                                    Action<string> onClientCurrent)
            => RegisterEntry(key, displayName, serverPath, clientPath, onClientCurrent);

        private static void RegisterEntry(string key, string displayName, Func<string> serverPath, Func<string> clientPath,
                                          Action<string> onClientCurrent)
        {
            if (string.IsNullOrEmpty(key) || serverPath == null || clientPath == null) return;
            _entries[key] = new CacheEntry
            {
                Key = key,
                DisplayName = string.IsNullOrEmpty(displayName) ? key : displayName,
                ServerPath = serverPath,
                ClientPath = clientPath,
                OnClientCurrent = onClientCurrent,
            };
            ManifestDiffSync.RegisterPersistedBlob(NamespaceFor(key));
        }

        // A consumer that throws must not break the sync, and must not go quiet either.
        private static void NotifyClientCurrent(CacheEntry entry, string path)
        {
            var callback = entry.OnClientCurrent;
            if (callback == null) return;
            try { callback(path); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} {entry.DisplayName}: the consumer threw while taking delivery: {ex}");
            }
        }

        private static string NamespaceFor(string key) => "worldcache:" + key;

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class WorldCacheSync_ZNetAwake_Patch
        {
            [HarmonyPostfix]
            private static void Postfix() => EnsureRegistered();
        }

        // A peer is offered the caches once, when it connects. Nothing is sent
        // unless the peer answers that it lacks the file, so a returning client
        // costs one small offer and no payload.
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class WorldCacheSync_OnNewConnection_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(ZNetPeer peer)
            {
                if (peer == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                var host = FiresUnifiedCore.Instance;
                if (host == null) return;
                host.StartCoroutine(OfferWhenPeerReady(peer));
            }
        }

        // The peer has to finish its handshake before a routed RPC will reach it.
        private static IEnumerator OfferWhenPeerReady(ZNetPeer peer)
        {
            float deadline = Time.realtimeSinceStartup + PeerReadyTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (peer.m_socket == null || !peer.m_socket.IsConnected()) yield break;
                if (peer.m_uid != 0L) break;
                yield return null;
            }
            if (peer.m_uid == 0L) yield break;
            OfferAllTo(peer.m_uid);
        }

        private const float PeerReadyTimeoutSeconds = 30f;

        public static void EnsureRegistered()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(_registeredOn, rpc)) return;
            try
            {
                rpc.Register<ZPackage>(RpcOffer, RPC_Offer);
                rpc.Register<ZPackage>(RpcWant, RPC_Want);
                rpc.Register<ZPackage>(RpcChunk, RPC_Chunk);
                _registeredOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} RPC register failed: {ex.Message}"); }
        }

        /// <summary>
        /// Server side: offer every registered cache to one peer. Hashing a large
        /// file is not free, so this is meant for a peer that just joined.
        /// </summary>
        public static void OfferAllTo(long peerUid)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var host = FiresUnifiedCore.Instance;
            if (host == null) return;

            var offers = new List<KeyValuePair<long, KeyValuePair<CacheEntry, string>>>(_entries.Count);
            foreach (var entry in _entries.Values)
            {
                string path;
                try { path = entry.ServerPath(); } catch { continue; }
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                long size;
                try { size = new FileInfo(path).Length; } catch { continue; }
                offers.Add(new KeyValuePair<long, KeyValuePair<CacheEntry, string>>(
                    size, new KeyValuePair<CacheEntry, string>(entry, path)));
            }

            // Smallest first: a small file is often the one a client is blocked on,
            // and its worker hash is queued ahead of the hundreds-of-MB ones.
            offers.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var offer in offers)
                host.StartCoroutine(OfferOne(peerUid, offer.Value.Key, offer.Value.Value));
        }

        /// <summary>
        /// Server side: re-offer ONE registered cache to every connected client, for data that changes
        /// during a session. <see cref="OfferAllTo"/> would hash every registered file — including caches
        /// hundreds of MB large — so a small file that changes often must not go through it.
        /// Clients whose copy already matches answer nothing, so a no-op change costs one hash and one
        /// small message per peer.
        /// </summary>
        public static void OfferKeyToAll(string key)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (string.IsNullOrEmpty(key) || !_entries.TryGetValue(key, out var entry)) return;
            var host = FiresUnifiedCore.Instance;
            if (host == null) return;

            string path;
            try { path = entry.ServerPath(); } catch { return; }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            var peers = ZNet.instance.GetConnectedPeers();
            if (peers == null) return;
            int offered = 0;
            foreach (var peer in peers)
            {
                if (peer == null || peer.m_uid == 0L) continue;
                host.StartCoroutine(OfferOne(peer.m_uid, entry, path));
                offered++;
            }
            if (offered > 0)
                FiresLogger.LogInfo($"{LogPrefix} {entry.DisplayName}: re-offered to {offered} client(s) after a change.");
        }

        private static IEnumerator OfferOne(long peerUid, CacheEntry entry, string path)
        {
            // Yield first so the peer finishes connecting; the hash itself never runs on the main thread.
            yield return null;
            string hash = null;
            yield return HashAsync(path, result => hash = result);
            if (string.IsNullOrEmpty(hash)) yield break;

            long length;
            try { length = new FileInfo(path).Length; }
            catch { yield break; }

            var pkg = new ZPackage();
            pkg.Write(entry.Key);
            pkg.Write(hash);
            pkg.Write(length);
            pkg.Write(entry.DisplayName);
            SafeRoutedRpc.InvokeSafe(peerUid, RpcOffer, pkg);
        }

        // Client side: the server says what it has; reply only if we lack it. The local copy is hashed off the main thread
        // (a 377 MB bake held each client's RPC handler for about 3 s at every join, R22), so the answer comes a moment later.
        private static void RPC_Offer(long sender, ZPackage pkg)
        {
            if (ZNet.instance != null && ZNet.instance.IsServer()) return;

            string key = pkg.ReadString();
            string serverHash = pkg.ReadString();
            long size = pkg.ReadLong();
            string displayName = pkg.ReadString();

            if (!_entries.TryGetValue(key, out var entry)) return;

            string target;
            try { target = entry.ClientPath(); } catch { return; }
            if (string.IsNullOrEmpty(target)) return;

            var host = FiresUnifiedCore.Instance;
            if (host != null) host.StartCoroutine(AnswerOffer(entry, key, serverHash, size, displayName, target));
            else RunNow(AnswerOffer(entry, key, serverHash, size, displayName, target));
        }

        private static IEnumerator AnswerOffer(CacheEntry entry, string key, string serverHash, long size, string displayName, string target)
        {
            if (File.Exists(target))
            {
                string local = null;
                yield return HashAsync(target, result => local = result);
                if (string.Equals(local, serverHash, StringComparison.OrdinalIgnoreCase))
                {
                    ManifestDiffSync.RememberBlobHash(NamespaceFor(key), serverHash);
                    FiresLogger.LogInfo($"{LogPrefix} {displayName}: already current, nothing to transfer.");
                    NotifyClientCurrent(entry, target);
                    yield break;
                }
            }

            FiresLogger.LogWarning($"{LogPrefix} {displayName}: this client does not have the server's copy " +
                                   $"({size / (1024 * 1024)} MB). Requesting it — this happens once; later joins are instant.");

            var want = new ZPackage();
            want.Write(key);
            want.Write(serverHash);
            SafeRoutedRpc.InvokeSafe(RpcWant, want);
        }

        // Server side: a client asked for a file; stream it to that client only.
        private static void RPC_Want(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string key = pkg.ReadString();
            string hash = pkg.ReadString();
            if (!_entries.TryGetValue(key, out var entry)) return;

            string path;
            try { path = entry.ServerPath(); } catch { return; }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            var host = FiresUnifiedCore.Instance;
            if (host == null) return;
            host.StartCoroutine(StreamFileTo(sender, entry, path, hash));
        }

        // One chunk per flush, read straight off disk. Never holds the file.
        private static IEnumerator StreamFileTo(long target, CacheEntry entry, string path, string hash)
        {
            long length = new FileInfo(path).Length;
            int totalChunks = (int)Math.Max(1, (length + ChunkBytes - 1) / ChunkBytes);
            var buffer = new byte[ChunkBytes];
            int index = 0;

            FiresLogger.LogInfo($"{LogPrefix} sending {entry.DisplayName} to {target}: " +
                                $"{length / (1024 * 1024)} MB in {totalChunks} chunk(s).");

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes))
            {
                while (index < totalChunks)
                {
                    int read = fs.Read(buffer, 0, ChunkBytes);
                    if (read <= 0) break;

                    var chunk = new ZPackage();
                    chunk.Write(entry.Key);
                    chunk.Write(hash);
                    chunk.Write(index);
                    chunk.Write(totalChunks);
                    chunk.Write(read);
                    chunk.Write(read == buffer.Length ? buffer : Trim(buffer, read));
                    if (!SafeRoutedRpc.InvokeSafe(target, RpcChunk, chunk)) yield break;

                    index++;
                    // Two yields: one flush per chunk. A single yield can queue the
                    // next chunk before the socket flush and concatenate both past
                    // the message cap — the reason SafeRoutedRpc does the same.
                    yield return null;
                    yield return null;
                }
            }
            FiresLogger.LogInfo($"{LogPrefix} {entry.DisplayName}: sent {index}/{totalChunks} chunk(s) to {target}.");
        }

        private static byte[] Trim(byte[] source, int count)
        {
            var trimmed = new byte[count];
            Buffer.BlockCopy(source, 0, trimmed, 0, count);
            return trimmed;
        }

        // Client side: append to a temp file, verify, then swap into place.
        private static void RPC_Chunk(long sender, ZPackage pkg)
        {
            string key = pkg.ReadString();
            string hash = pkg.ReadString();
            int index = pkg.ReadInt();
            int total = pkg.ReadInt();
            int count = pkg.ReadInt();
            byte[] data = pkg.ReadByteArray();
            if (!_entries.TryGetValue(key, out var entry)) return;

            try
            {
                if (!_incoming.TryGetValue(key, out var state) || state.ExpectedHash != hash)
                {
                    CloseIncoming(key);
                    string target = entry.ClientPath();
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    state = new Incoming
                    {
                        TargetPath = target,
                        TempPath = target + TempSuffix,
                        ExpectedHash = hash,
                        TotalChunks = total,
                    };
                    state.Stream = new FileStream(state.TempPath, FileMode.Create, FileAccess.Write, FileShare.None, ChunkBytes);
                    _incoming[key] = state;
                }

                state.Stream.Write(data, 0, Math.Min(count, data.Length));
                state.Received++;
                state.Bytes += count;
                if (state.Received < state.TotalChunks) return;

                state.Stream.Flush();
                state.Stream.Dispose();
                state.Stream = null;
                string verifyPath = state.TargetPath + VerifySuffix;
                TryDelete(verifyPath);
                File.Move(state.TempPath, verifyPath);
                state.TempPath = verifyPath;
                _incoming.Remove(key);

                var host = FiresUnifiedCore.Instance;
                if (host != null) host.StartCoroutine(Install(entry, state, hash));
                else RunNow(Install(entry, state, hash));
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} {entry.DisplayName}: chunk {index + 1}/{total} failed: {ex.Message}");
                CloseIncoming(key);
            }
        }

        // Verify the finished transfer off the main thread, then swap it into place; the installed file's hash is then
        // remembered, so the next offer answers at once.
        private static IEnumerator Install(CacheEntry entry, Incoming state, string hash)
        {
            string actual = null;
            yield return HashAsync(state.TempPath, result => actual = result);
            if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
            {
                FiresLogger.LogWarning($"{LogPrefix} {entry.DisplayName}: transfer finished but the hash does not match " +
                                       "(expected the server's copy). Discarding rather than installing a corrupt cache.");
                TryDelete(state.TempPath);
                yield break;
            }

            try
            {
                var verified = new FileInfo(state.TempPath);
                long length = verified.Length;
                DateTime writtenUtc = verified.LastWriteTimeUtc;
                TryDelete(state.TargetPath);
                File.Move(state.TempPath, state.TargetPath);
                RememberHash(Path.GetFullPath(state.TargetPath), length, writtenUtc, hash);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} {entry.DisplayName}: could not install the verified copy: {ex.Message}");
                TryDelete(state.TempPath);
                yield break;
            }

            ManifestDiffSync.RememberBlobHash(NamespaceFor(entry.Key), hash);
            if (entry.OnClientCurrent != null)
            {
                FiresLogger.LogInfo($"{LogPrefix} {entry.DisplayName}: received and verified " +
                                    $"({state.Bytes / 1024} KB) — handing it to its owner now.");
                NotifyClientCurrent(entry, state.TargetPath);
            }
            else
            {
                FiresLogger.LogWarning($"{LogPrefix} {entry.DisplayName}: received and verified " +
                                       $"({state.Bytes / (1024 * 1024)} MB). Restart the world to generate on it; " +
                                       "later joins will not transfer anything.");
            }
        }

        private static void CloseIncoming(string key)
        {
            if (!_incoming.TryGetValue(key, out var state)) return;
            try { state.Stream?.Dispose(); } catch { }
            TryDelete(state.TempPath);
            _incoming.Remove(key);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // The file's SHA-256 without holding the main thread: remembered by full path, length and write time, so a re-offer,
        // a rejoin or a later join costs nothing; the first hash of each file's content runs on a worker while the caller
        // yields. A 377 MB bake hashed inline at every join stalled the server and each client about 3 s (R22).
        private static IEnumerator HashAsync(string path, Action<string> done)
        {
            string full;
            FileInfo before;
            try
            {
                full = Path.GetFullPath(path);
                before = new FileInfo(full);
                if (!before.Exists) { done(null); yield break; }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} could not hash '{path}': {ex.Message}");
                done(null);
                yield break;
            }

            lock (_hashLock)
            {
                if (_hashes.TryGetValue(full, out var known) && known.Length == before.Length && known.WrittenUtc == before.LastWriteTimeUtc)
                {
                    done(known.Hash);
                    yield break;
                }
            }

            var task = Task.Run(() => HashFileStreaming(full));
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted || task.IsCanceled)
            {
                FiresLogger.LogWarning($"{LogPrefix} could not hash '{path}': {task.Exception?.GetBaseException().Message}");
                done(null);
                yield break;
            }

            RememberHash(full, before.Length, before.LastWriteTimeUtc, task.Result);
            done(task.Result);
        }

        // Only a file that did not change while it was being hashed is remembered.
        private static void RememberHash(string full, long length, DateTime writtenUtc, string hash)
        {
            try
            {
                var after = new FileInfo(full);
                if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != writtenUtc) return;
                lock (_hashLock) _hashes[full] = new KnownHash { Length = after.Length, WrittenUtc = after.LastWriteTimeUtc, Hash = hash };
            }
            catch { }
        }

        // Runs a routine to its end right here, nested routines included: only the no-host fallback, which then holds the
        // caller exactly as the old synchronous hash did.
        private static void RunNow(IEnumerator routine)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                if (!top.MoveNext()) { stack.Pop(); continue; }
                if (top.Current is IEnumerator nested) stack.Push(nested);
            }
        }

        // Hashes without reading the file into memory — the whole point.
        private static string HashFileStreaming(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
