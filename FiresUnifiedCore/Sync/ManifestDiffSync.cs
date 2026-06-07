using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using FiresCore.Logging;
using FiresCore.Net;
using UnityEngine;

namespace FiresCore.Sync
{
    // "Don't re-send what the peer already has" sync primitive. One pair of
    // routed RPCs covers every namespace because the namespace key is
    // carried in the payload.
    //
    // FLOW
    //   1. Server calls RequestFromPeer(peerUid, namespaceKey).
    //   2. Peer responds with its local manifest (relativePath -> sha256).
    //   3. Server caches the manifest per (peer, namespace) and, while
    //      iterating its own files, skips any whose local hash matches.
    //
    // Two registration styles:
    //   - RegisterFolder: enumerate a folder by pattern, hash each file.
    //   - RegisterHashProvider / RegisterPersistedBlob: single opaque blob
    //     (quest databases, snapshots) where the manifest is one entry
    //     keyed by empty-string.
    //
    // This helper never touches the file payload itself — it only brokers
    // the "does the peer already have this?" question.
    public static class ManifestDiffSync
    {
        private const string RpcRequest = "FiresUnifiedCore_Manifest_Request";
        private const string RpcReply = "FiresUnifiedCore_Manifest_Reply";
        private const string LogPrefix = "[ManifestDiffSync]";
        private const string BlobCacheSubfolder = "FiresUnifiedCore";
        private const string BlobCacheSubpath = "cache/manifests";
        private const string BlobCacheExtension = ".hash";
        private const string AnyFilesPattern = "*";
        private const float ManifestPollIntervalSeconds = 0.1f;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, FolderRegistration> _registered =
            new Dictionary<string, FolderRegistration>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashProviderRegistration> _hashProviders =
            new Dictionary<string, HashProviderRegistration>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<ManifestKey, Dictionary<string, string>> _peerManifests =
            new Dictionary<ManifestKey, Dictionary<string, string>>();

        private static bool _rpcsRegistered;

        public static void EnsureRegistered()
        {
            if (_rpcsRegistered) return;
            if (ZRoutedRpc.instance == null) return;

            TryRegisterRpc(RpcRequest, RPC_OnRequest);
            TryRegisterRpc(RpcReply, RPC_OnReply);
            _rpcsRegistered = true;
        }

        public static void RegisterFolder(string namespaceKey, Func<string> folderProvider,
                                          string searchPattern, SearchOption searchOption)
        {
            if (string.IsNullOrEmpty(namespaceKey) || folderProvider == null) return;
            lock (_lock)
            {
                _registered[namespaceKey] = new FolderRegistration
                {
                    FolderProvider = folderProvider,
                    SearchPattern = string.IsNullOrEmpty(searchPattern) ? AnyFilesPattern : searchPattern,
                    Search = searchOption,
                };
            }
        }

        public static void RegisterHashProvider(string namespaceKey, Func<string> hashProvider)
        {
            if (string.IsNullOrEmpty(namespaceKey) || hashProvider == null) return;
            lock (_lock)
            {
                _hashProviders[namespaceKey] = new HashProviderRegistration { HashProvider = hashProvider };
            }
        }

        public static void RegisterPersistedBlob(string namespaceKey)
        {
            if (string.IsNullOrEmpty(namespaceKey)) return;
            RegisterHashProvider(namespaceKey, () => GetPersistedBlobHash(namespaceKey));
        }

        public static void RememberBlobHash(string namespaceKey, string hashHex)
        {
            if (string.IsNullOrEmpty(namespaceKey)) return;
            try
            {
                Directory.CreateDirectory(BlobCacheRoot);
                File.WriteAllText(BlobCachePath(namespaceKey), hashHex ?? string.Empty);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} RememberBlobHash('{namespaceKey}') failed: {ex.Message}");
            }
        }

        public static string GetPersistedBlobHash(string namespaceKey)
        {
            if (string.IsNullOrEmpty(namespaceKey)) return string.Empty;
            try
            {
                var path = BlobCachePath(namespaceKey);
                if (File.Exists(path)) return (File.ReadAllText(path) ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} GetPersistedBlobHash('{namespaceKey}') failed: {ex.Message}");
            }
            return string.Empty;
        }

        public static bool PeerHasBlob(long peerUid, string namespaceKey, string serverHashHex)
            => HasSameHash(peerUid, namespaceKey, string.Empty, serverHashHex);

        public static void RequestFromPeer(long peerUid, string namespaceKey)
        {
            if (string.IsNullOrEmpty(namespaceKey)) return;
            EnsureRegistered();
            lock (_lock) _peerManifests.Remove(new ManifestKey(peerUid, namespaceKey));

            var pkg = new ZPackage();
            pkg.Write(namespaceKey);
            SafeRoutedRpc.InvokeSafe(peerUid, RpcRequest, pkg);
        }

        public static bool TryGetManifest(long peerUid, string namespaceKey,
                                          out Dictionary<string, string> manifest)
        {
            lock (_lock)
            {
                return _peerManifests.TryGetValue(new ManifestKey(peerUid, namespaceKey), out manifest);
            }
        }

        // Returns false when no manifest has been received yet so callers
        // fall back to the legacy "send everything" path — correctness over
        // bandwidth when the diff information isn't available.
        public static bool HasSameHash(long peerUid, string namespaceKey, string relativePath, string localHashHex)
        {
            if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(localHashHex)) return false;
            if (!TryGetManifest(peerUid, namespaceKey, out var manifest) || manifest == null) return false;
            if (!manifest.TryGetValue(relativePath, out var remoteHash) || string.IsNullOrEmpty(remoteHash)) return false;
            return string.Equals(remoteHash, localHashHex, StringComparison.OrdinalIgnoreCase);
        }

        public static IEnumerator WaitForManifest(long peerUid, string namespaceKey, float timeoutSeconds)
        {
            if (string.IsNullOrEmpty(namespaceKey)) yield break;

            float elapsed = 0f;
            var wait = new WaitForSeconds(ManifestPollIntervalSeconds);
            while (elapsed < timeoutSeconds)
            {
                if (TryGetManifest(peerUid, namespaceKey, out _)) yield break;
                elapsed += ManifestPollIntervalSeconds;
                yield return wait;
            }
        }

        public static void ForgetPeer(long peerUid)
        {
            lock (_lock)
            {
                var toRemove = new List<ManifestKey>();
                foreach (var key in _peerManifests.Keys)
                {
                    if (key.PeerUid == peerUid) toRemove.Add(key);
                }
                for (int i = 0; i < toRemove.Count; i++) _peerManifests.Remove(toRemove[i]);
            }
        }

        public static string ComputeSha256(string text)
        {
            if (text == null) text = string.Empty;
            return ComputeSha256(Encoding.UTF8.GetBytes(text));
        }

        public static string ComputeSha256(byte[] bytes)
        {
            if (bytes == null) bytes = Array.Empty<byte>();
            using (var sha = SHA256.Create())
            {
                return HashBytesToHex(sha.ComputeHash(bytes));
            }
        }

        public static Dictionary<string, string> BuildLocalManifest(string namespaceKey)
        {
            var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            HashProviderRegistration hashReg;
            FolderRegistration folderReg;
            lock (_lock)
            {
                _hashProviders.TryGetValue(namespaceKey, out hashReg);
                _registered.TryGetValue(namespaceKey, out folderReg);
            }

            if (hashReg?.HashProvider != null)
            {
                manifest[string.Empty] = SafelyResolveBlobHash(namespaceKey, hashReg.HashProvider);
                return manifest;
            }

            if (folderReg == null) return manifest;
            return BuildFolderManifest(namespaceKey, folderReg);
        }

        private static string SafelyResolveBlobHash(string namespaceKey, Func<string> hashProvider)
        {
            try { return hashProvider() ?? string.Empty; }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} hash provider for '{namespaceKey}' threw: {ex.Message}");
                return string.Empty;
            }
        }

        private static Dictionary<string, string> BuildFolderManifest(string namespaceKey, FolderRegistration reg)
        {
            var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string folder = SafelyResolveFolder(reg);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return manifest;

            string[] files = SafelyEnumerateFiles(namespaceKey, folder, reg);
            if (files == null) return manifest;

            for (int i = 0; i < files.Length; i++)
                AppendFileEntryIfHashable(manifest, folder, files[i]);

            return manifest;
        }

        private static string SafelyResolveFolder(FolderRegistration reg)
        {
            try { return reg.FolderProvider?.Invoke(); }
            catch { return null; }
        }

        private static string[] SafelyEnumerateFiles(string namespaceKey, string folder, FolderRegistration reg)
        {
            try { return Directory.GetFiles(folder, reg.SearchPattern, reg.Search); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} BuildLocalManifest('{namespaceKey}') enumerate failed: {ex.Message}");
                return null;
            }
        }

        private static void AppendFileEntryIfHashable(Dictionary<string, string> manifest, string folder, string file)
        {
            string rel = TryBuildRelativePath(folder, file);
            if (rel == null) return;

            string sha = TryComputeFileSha256(rel, file);
            if (sha == null) return;

            manifest[rel] = sha;
        }

        private static string TryBuildRelativePath(string folder, string file)
        {
            try
            {
                return file.Substring(folder.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
            }
            catch { return null; }
        }

        private static string TryComputeFileSha256(string rel, string file)
        {
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sha256 = SHA256.Create())
                {
                    return HashBytesToHex(sha256.ComputeHash(stream));
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} Hashing '{rel}' failed: {ex.Message}");
                return null;
            }
        }

        private static string HashBytesToHex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        private static void TryRegisterRpc(string rpcName, Action<long, ZPackage> handler)
        {
            try { ZRoutedRpc.instance.Register<ZPackage>(rpcName, handler); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} Register('{rpcName}') failed: {ex.Message}");
            }
        }

        private static void RPC_OnRequest(long sender, ZPackage pkg)
        {
            if (pkg == null) return;

            string namespaceKey;
            try { namespaceKey = pkg.ReadString(); }
            catch { return; }
            if (string.IsNullOrEmpty(namespaceKey)) return;

            Dictionary<string, string> manifest;
            try { manifest = BuildLocalManifest(namespaceKey); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} BuildLocalManifest('{namespaceKey}') threw: {ex.Message}");
                return;
            }

            var reply = new ZPackage();
            reply.Write(namespaceKey);
            reply.Write(manifest.Count);
            foreach (var kv in manifest)
            {
                reply.Write(kv.Key ?? string.Empty);
                reply.Write(kv.Value ?? string.Empty);
            }
            SafeRoutedRpc.InvokeSafe(sender, RpcReply, reply);
        }

        private static void RPC_OnReply(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            try
            {
                string namespaceKey = pkg.ReadString();
                if (string.IsNullOrEmpty(namespaceKey)) return;

                int count = Math.Max(0, pkg.ReadInt());

                var dict = new Dictionary<string, string>(count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    string rel = pkg.ReadString();
                    string sha = pkg.ReadString();
                    if (!string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(sha))
                        dict[rel] = sha;
                }

                lock (_lock)
                {
                    _peerManifests[new ManifestKey(sender, namespaceKey)] = dict;
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} RPC_OnReply decode failed: {ex.Message}");
            }
        }

        private static string BlobCacheRoot =>
            Path.Combine(Paths.ConfigPath, BlobCacheSubfolder, BlobCacheSubpath);

        private static string BlobCachePath(string namespaceKey) =>
            Path.Combine(BlobCacheRoot, SanitizeKey(namespaceKey) + BlobCacheExtension);

        private static string SanitizeKey(string namespaceKey)
        {
            if (string.IsNullOrEmpty(namespaceKey)) return "_";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(namespaceKey.Length);
            for (int i = 0; i < namespaceKey.Length; i++)
            {
                char c = namespaceKey[i];
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }

        private sealed class FolderRegistration
        {
            public Func<string> FolderProvider;
            public string SearchPattern;
            public SearchOption Search;
        }

        private sealed class HashProviderRegistration
        {
            public Func<string> HashProvider;
        }

        private readonly struct ManifestKey : IEquatable<ManifestKey>
        {
            public readonly long PeerUid;
            public readonly string Namespace;

            public ManifestKey(long uid, string ns)
            {
                PeerUid = uid;
                Namespace = ns ?? string.Empty;
            }

            public bool Equals(ManifestKey other)
                => PeerUid == other.PeerUid
                   && string.Equals(Namespace, other.Namespace, StringComparison.OrdinalIgnoreCase);

            public override bool Equals(object obj) => obj is ManifestKey k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (PeerUid.GetHashCode() * 397)
                        ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Namespace);
                }
            }
        }
    }
}
