using System;
using System.Collections;
using FiresCore.Logging;

namespace FiresCore.Net
{
    // Guard-rail wrapper around ZRoutedRpc.InvokeRoutedRPC.
    //
    // Valheim's Steam transport has a hard per-message ceiling of 524 288
    // bytes; multiple queued routed RPCs ship together as one
    // SendMessageToConnection call and tripping that limit throws
    // SteamNetworkingSockets assertion floods that desync downstream state.
    //
    // InvokeSafe size-gates every dispatch; PacedChunkedSend yields TWICE
    // between chunks so each chunk lands in its own SendQueuedPackages
    // flush. InvokeSafeToAllExcept implements server-side fan-out that
    // excludes the origin client, removing the need for per-op dedup caches.
    public static class SafeRoutedRpc
    {
        private const string LogPrefix = "[SafeRoutedRpc]";

        // Steam's per-message ceiling is 524 288 bytes; we leave ~44 KB of
        // headroom for the routed-RPC envelope plus same-frame coalescing.
        public const int MaxSafeMessageBytes = 480 * 1024;

        // Matches the 400 KB convention used elsewhere in the family
        // (RuntimeSpriteSync, chunked log send).
        public const int DefaultChunkBytes = 400 * 1024;

        public static bool InvokeSafe(long target, string method, ZPackage pkg)
        {
            if (!CanDispatch(method, pkg, hasExplicitTarget: true, target)) return false;
            if (!CheckSizeOrLog(method, pkg, target.ToString())) return false;

            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(target, method, pkg);
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} InvokeRoutedRPC('{method}') threw: {ex.Message}");
                return false;
            }
        }

        public static bool InvokeSafe(string method, ZPackage pkg)
        {
            if (!CanDispatch(method, pkg, hasExplicitTarget: false, target: 0L)) return false;
            if (!CheckSizeOrLog(method, pkg, target: null)) return false;

            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(method, pkg);
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} InvokeRoutedRPC('{method}') threw: {ex.Message}");
                return false;
            }
        }

        // Splits payload into chunkBytes-sized slices and ships each as its
        // own routed RPC, yielding TWICE between chunks so each lands in its
        // own SendQueuedPackages flush. A single `yield return null` is not
        // enough — when our coroutine runs after ZNet's socket Update pass
        // within a frame, chunk N stays queued until the NEXT frame's flush;
        // resuming immediately on that frame would queue N+1 before the
        // flush, concatenating both into one Steam send and blowing past
        // the 524 288 cap.
        public static IEnumerator PacedChunkedSend(
            long target,
            string method,
            byte[] payload,
            int chunkBytes,
            Action<ZPackage, int, int, byte[]> fillPackage,
            Action<int> onComplete = null)
        {
            if (fillPackage == null)
            {
                FiresLogger.LogWarning($"{LogPrefix} PacedChunkedSend('{method}') — fillPackage delegate is null, aborting.");
                onComplete?.Invoke(0);
                yield break;
            }

            if (payload == null) payload = Array.Empty<byte>();
            if (chunkBytes <= 0 || chunkBytes > MaxSafeMessageBytes)
                chunkBytes = DefaultChunkBytes;

            int totalChunks = Math.Max(1, (int)Math.Ceiling((double)payload.Length / chunkBytes));
            int sent = 0;

            for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
            {
                if (ZRoutedRpc.instance == null)
                {
                    FiresLogger.LogWarning(
                        $"{LogPrefix} PacedChunkedSend('{method}') aborted at chunk " +
                        $"{chunkIndex + 1}/{totalChunks} — ZRoutedRpc gone.");
                    break;
                }

                byte[] slice = SliceChunk(payload, chunkIndex, chunkBytes);
                ZPackage pkg = BuildChunkPackage(method, chunkIndex, totalChunks, slice, fillPackage);
                if (pkg == null) break;

                if (!InvokeSafe(target, method, pkg)) break;
                sent++;

                yield return null;
                yield return null;
            }

            onComplete?.Invoke(sent);
        }

        public static bool IsLocalSender(long senderId)
        {
            return ZRoutedRpc.instance != null && senderId == ZRoutedRpc.instance.m_id;
        }

        public static int InvokeSafeToAllExcept(long excludePeerId, string method, ZPackage pkg)
        {
            if (!CanFanOutFromServer(method, pkg)) return 0;
            byte[] snapshot = SnapshotPackageBytes(method, pkg);
            if (snapshot == null) return 0;
            if (snapshot.Length > MaxSafeMessageBytes)
            {
                FiresLogger.LogError(
                    $"{LogPrefix} REFUSING fan-out of oversize RPC '{method}' " +
                    $"({snapshot.Length:N0} B > {MaxSafeMessageBytes:N0} B cap). " +
                    "Caller must chunk via SafeRoutedRpc.PacedChunkedSend.");
                return 0;
            }

            var peers = ZNet.instance.GetPeers();
            if (peers == null || peers.Count == 0) return 0;

            int sent = 0;
            for (int i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                if (peer == null) continue;
                if (peer.m_uid == excludePeerId) continue;
                if (DispatchToPeer(method, snapshot, peer)) sent++;
            }
            return sent;
        }

        private static bool CanDispatch(string method, ZPackage pkg, bool hasExplicitTarget, long target)
        {
            if (ZRoutedRpc.instance == null)
            {
                string targetSuffix = hasExplicitTarget ? $" (target={target})." : ".";
                FiresLogger.LogWarning($"{LogPrefix} Dropping '{method}' — ZRoutedRpc.instance is null{targetSuffix}");
                return false;
            }
            if (pkg == null)
            {
                string targetSuffix = hasExplicitTarget ? $" (target={target})." : ".";
                FiresLogger.LogWarning($"{LogPrefix} Dropping '{method}' — ZPackage is null{targetSuffix}");
                return false;
            }
            return true;
        }

        private static bool CheckSizeOrLog(string method, ZPackage pkg, string target)
        {
            int size;
            try { size = pkg.GetArray()?.Length ?? 0; }
            catch
            {
                FiresLogger.LogWarning($"{LogPrefix} Dropping '{method}' — ZPackage.GetArray() threw{FormatTarget(target)}.");
                return false;
            }

            if (size <= MaxSafeMessageBytes) return true;

            FiresLogger.LogError(
                $"{LogPrefix} REFUSING to send oversize RPC '{method}' " +
                $"({size:N0} B > {MaxSafeMessageBytes:N0} B cap{FormatTarget(target)}). " +
                "Caller must chunk this payload via SafeRoutedRpc.PacedChunkedSend.");
            return false;
        }

        private static string FormatTarget(string target)
            => string.IsNullOrEmpty(target) ? string.Empty : $", target={target}";

        private static byte[] SliceChunk(byte[] payload, int chunkIndex, int chunkBytes)
        {
            int offset = chunkIndex * chunkBytes;
            int length = Math.Min(chunkBytes, payload.Length - offset);
            if (length <= 0) return Array.Empty<byte>();

            var slice = new byte[length];
            Array.Copy(payload, offset, slice, 0, length);
            return slice;
        }

        private static ZPackage BuildChunkPackage(string method, int chunkIndex, int totalChunks,
                                                   byte[] slice, Action<ZPackage, int, int, byte[]> fillPackage)
        {
            try
            {
                var pkg = new ZPackage();
                fillPackage(pkg, chunkIndex, totalChunks, slice);
                return pkg;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning(
                    $"{LogPrefix} PacedChunkedSend('{method}') fillPackage threw on chunk " +
                    $"{chunkIndex + 1}/{totalChunks}: {ex.Message}");
                return null;
            }
        }

        private static bool CanFanOutFromServer(string method, ZPackage pkg)
        {
            if (ZRoutedRpc.instance == null)
            {
                FiresLogger.LogWarning($"{LogPrefix} InvokeSafeToAllExcept('{method}') — ZRoutedRpc.instance is null.");
                return false;
            }
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                FiresLogger.LogWarning($"{LogPrefix} InvokeSafeToAllExcept('{method}') — caller is not the server, refusing to fan out.");
                return false;
            }
            if (pkg == null)
            {
                FiresLogger.LogWarning($"{LogPrefix} InvokeSafeToAllExcept('{method}') — ZPackage is null.");
                return false;
            }
            return true;
        }

        private static byte[] SnapshotPackageBytes(string method, ZPackage pkg)
        {
            try { return pkg.GetArray() ?? Array.Empty<byte>(); }
            catch
            {
                FiresLogger.LogWarning($"{LogPrefix} InvokeSafeToAllExcept('{method}') — ZPackage.GetArray() threw.");
                return null;
            }
        }

        // Fresh ZPackage per dispatch — the type carries a stateful
        // read/write cursor so reusing one instance across multiple
        // InvokeRoutedRPC calls can deliver a half-read payload to later peers.
        private static bool DispatchToPeer(string method, byte[] snapshot, ZNetPeer peer)
        {
            ZPackage perPeer;
            try { perPeer = new ZPackage(snapshot); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} Failed to rebuild ZPackage for peer {peer.m_uid}: {ex.Message}");
                return false;
            }

            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, method, perPeer);
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} InvokeRoutedRPC('{method}') to peer {peer.m_uid} threw: {ex.Message}");
                return false;
            }
        }
    }
}
