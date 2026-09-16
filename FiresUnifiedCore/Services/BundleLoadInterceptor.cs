using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Services
{
    // Redirects AssetBundle.LoadFromMemory(bytes) to LoadFromFile(path) when the bytes came from a recent
    // File.ReadAllBytes(path), skipping the managed read and native copy that the common mod pattern pays for.
    // Only mods that load bundles after Core's patches attach benefit. Arrays are tracked in a
    // ConditionalWeakTable, and any LoadFromFile failure falls back to the original call.
    public static class BundleLoadInterceptor
    {
        private const int HitSummaryInterval = 10;

        // byte[] → originating path. Weak-keyed so a mod releasing its
        // byte[] frees the entry automatically.
        private static readonly ConditionalWeakTable<byte[], string> PathByBytes
            = new ConditionalWeakTable<byte[], string>();

        private static int _captures;
        private static int _hits;
        private static int _misses;
        private static int _failures;

        public static int CapturedReads => _captures;
        public static int Hits          => _hits;
        public static int Misses        => _misses;
        public static int Failures      => _failures;

        // Returns true when the byte[] is in our capture table and we
        // know the originating path. Caller decides what to do with it.
        public static bool TryGetSourcePath(byte[] bytes, out string path)
        {
            path = null;
            if (bytes == null) return false;
            return PathByBytes.TryGetValue(bytes, out path);
        }

        internal static void RecordCapture(string path, byte[] bytes)
        {
            if (string.IsNullOrEmpty(path) || bytes == null || bytes.Length == 0) return;
            try
            {
                // Remove any prior entry under the same key — possible on
                // weird code paths that pool / reuse the same byte[]. Add
                // can throw on duplicates, hence the explicit remove.
                PathByBytes.Remove(bytes);
                PathByBytes.Add(bytes, path);
                Interlocked.Increment(ref _captures);
            }
            catch
            {
                // never break File.ReadAllBytes on a capture race
            }
        }

        internal static void RecordHit()
        {
            int hitCount = Interlocked.Increment(ref _hits);
            if (hitCount == 1 || hitCount % HitSummaryInterval == 0)
            {
                Debug.Log($"[FiresCore.BundleLoadInterceptor] Redirected {hitCount} AssetBundle.LoadFromMemory call(s) to LoadFromFile fast path ({_misses} miss(es), {_failures} failure(s)).");
            }
        }

        internal static void RecordMiss()    => Interlocked.Increment(ref _misses);
        internal static void RecordFailure() => Interlocked.Increment(ref _failures);

        internal static bool VerbosePassThrough()
        {
            try { return FiresCore.Logging.FiresLogger.VerboseEnabled; }
            catch { return false; }
        }
    }

    // Postfix on File.ReadAllBytes(string). Records the (path, bytes)
    // pair so a subsequent LoadFromMemory(bytes) call can be redirected.
    // Postfix (not prefix) because we need the returned array to use as
    // the table key.
    [HarmonyPatch(typeof(File), nameof(File.ReadAllBytes), new[] { typeof(string) })]
    internal static class FileReadAllBytesCapturePatch
    {
        private static void Postfix(string path, byte[] __result)
        {
            try { BundleLoadInterceptor.RecordCapture(path, __result); }
            catch { /* never break ReadAllBytes */ }
        }
    }

    // Prefix on AssetBundle.LoadFromMemory(byte[]). When the byte[] came
    // from a tracked ReadAllBytes call, swap to LoadFromFile(path) and
    // skip the original (return false). Otherwise pass through.
    [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadFromMemory), new[] { typeof(byte[]) })]
    internal static class LoadFromMemoryRedirectPatch
    {
        private static bool Prefix(byte[] binary, ref AssetBundle __result)
        {
            return TryRedirect(binary, crc: 0u, hasCrc: false, ref __result);
        }

        internal static bool TryRedirect(byte[] binary, uint crc, bool hasCrc, ref AssetBundle __result)
        {
            if (binary == null) return true;
            if (BundleLoadInterceptor.VerbosePassThrough()) return true;
            if (!BundleLoadInterceptor.TryGetSourcePath(binary, out var path))
            {
                BundleLoadInterceptor.RecordMiss();
                return true;
            }
            if (!File.Exists(path))
            {
                BundleLoadInterceptor.RecordMiss();
                return true;
            }

            try
            {
                AssetBundle bundle = hasCrc
                    ? AssetBundle.LoadFromFile(path, crc)
                    : AssetBundle.LoadFromFile(path);
                if (bundle != null)
                {
                    __result = bundle;
                    BundleLoadInterceptor.RecordHit();
                    return false;
                }
            }
            catch
            {
                // fall through to the original LoadFromMemory path
            }
            BundleLoadInterceptor.RecordFailure();
            return true;
        }
    }

    // Prefix on AssetBundle.LoadFromMemory(byte[], uint crc) — the CRC-
    // validating overload. Same redirect logic, calls the matching
    // LoadFromFile(path, crc) overload to preserve the integrity check.
    [HarmonyPatch(typeof(AssetBundle), nameof(AssetBundle.LoadFromMemory), new[] { typeof(byte[]), typeof(uint) })]
    internal static class LoadFromMemoryCrcRedirectPatch
    {
        private static bool Prefix(byte[] binary, uint crc, ref AssetBundle __result)
        {
            return LoadFromMemoryRedirectPatch.TryRedirect(binary, crc, hasCrc: true, ref __result);
        }
    }
}
