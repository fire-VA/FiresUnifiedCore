using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using FiresCore.Lifecycle;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FiresCore.Compat
{
    // HD_Additions 1.0.0 (Norger.HDAdditionalPieces) puts its pieces' real shaders back in its ZNetScene.Awake prefix, and to
    // find them its ShaderReplacment.getMeShaders loads every Shader from EVERY loaded asset bundle (LoadAllAssets<Shader>):
    // 648 ms inside the world-join frame ([generator], R38/R39). The same scan now runs at the menu, a few bundles a frame.
    // A prefix on getMeShaders hands that result to their own CachedShaders and skips their scan, scanning then only the
    // bundles loaded since. If the world starts before the menu scan is done, their scan runs as it always has. Nothing else
    // of theirs is patched; any other version, or a missing member, leaves the mod alone.
    internal static class HdAdditionsShaderScan
    {
        private const string Tag = "[HD_Additions] ";
        private const string Guid = "Norger.HDAdditionalPieces";
        private const string Version = "1.0.0";
        private const string ScannerType = "BalrondNorgerHDPieces.ShaderReplacment";
        private const string ScanMethod = "getMeShaders";
        private const string CacheField = "CachedShaders";
        private const double MenuBudgetMs = 20.0;

        private static readonly HashSet<Shader> s_shaders = new HashSet<Shader>();
        private static readonly HashSet<AssetBundle> s_scanned = new HashSet<AssetBundle>();
        private static readonly Stopwatch s_work = new Stopwatch();
        private static Harmony s_harmony;
        private static FieldInfo s_cached;
        private static Queue<AssetBundle> s_queue;
        private static bool s_checked;
        private static bool s_done;
        private static bool s_theirsRan;
        private static int s_frames;

        internal static void Register(Harmony harmony)
        {
            if (FiresMod.IsDedicatedServer) return;
            s_harmony = harmony;
            try
            {
                harmony.Patch(AccessTools.Method(typeof(FejdStartup), "Awake"),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(HdAdditionsShaderScan), nameof(FejdStartup_Awake_Prefix))));
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{Tag}not attached: {ex.Message}");
            }
        }

        // The first menu comes after the chainloader has finished, so their types are there; checked once per process.
        private static void FejdStartup_Awake_Prefix()
        {
            if (s_checked) return;
            s_checked = true;
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info.Instance == null) return;
            string version = info.Metadata.Version.ToString();
            if (version != Version)
            {
                FiresLogger.LogInfo($"{Tag}HD_Additions is {version}; the menu-time shader scan is pinned to {Version}, so it scans as it always has.");
                return;
            }
            Type scanner = info.Instance.GetType().Assembly.GetType(ScannerType);
            MethodInfo scan = scanner != null ? AccessTools.Method(scanner, ScanMethod) : null;
            s_cached = scanner != null ? AccessTools.Field(scanner, CacheField) : null;
            if (scan == null || s_cached == null || !(s_cached.GetValue(null) is HashSet<Shader>))
            {
                FiresLogger.LogWarning($"{Tag}{ScannerType}.{(scan == null ? ScanMethod : CacheField)} not found in HD_Additions {version}; it scans as it always has.");
                return;
            }
            try
            {
                s_harmony.Patch(scan, prefix: new HarmonyMethod(AccessTools.Method(typeof(HdAdditionsShaderScan), nameof(GetMeShaders_Prefix))));
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{Tag}not attached to {ScanMethod}: {ex.Message}");
                return;
            }
            s_queue = new Queue<AssetBundle>(Resources.FindObjectsOfTypeAll<AssetBundle>());
            var host = new GameObject("FiresCore.HdAdditionsShaderScan");
            Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<Runner>();
            FiresLogger.LogInfo($"{Tag}armed for HD_Additions {version}: its shader scan ({s_queue.Count} bundles) runs at the menu "
                                + $"({MenuBudgetMs:0} ms a frame) instead of in the world-join frame.");
        }

        private sealed class Runner : MonoBehaviour
        {
            private void Update()
            {
                if (s_theirsRan || s_queue == null)
                {
                    Destroy(gameObject);
                    return;
                }
                s_work.Start();
                var frame = Stopwatch.StartNew();
                do
                {
                    if (s_queue.Count == 0) break;
                    Scan(s_queue.Dequeue());
                } while (frame.Elapsed.TotalMilliseconds < MenuBudgetMs);
                s_work.Stop();
                s_frames++;
                if (s_queue.Count > 0) return;
                s_done = true;
                FiresLogger.LogInfo($"{Tag}menu scan done: {s_shaders.Count} shaders from {s_scanned.Count} bundles in "
                                    + $"{s_work.Elapsed.TotalMilliseconds:0} ms of work over {s_frames} frame(s).");
                Destroy(gameObject);
            }
        }

        // Their scan, for one bundle: a streamed-scene bundle asset by name, any other LoadAllAssets<Shader>.
        private static void Scan(AssetBundle bundle)
        {
            if (bundle == null || !s_scanned.Add(bundle)) return;
            try
            {
                IEnumerable<Shader> found = bundle.isStreamedSceneAssetBundle
                    ? bundle.GetAllAssetNames().Select(bundle.LoadAsset<Shader>).Where(shader => shader != null)
                    : bundle.LoadAllAssets<Shader>();
                foreach (Shader shader in found) s_shaders.Add(shader);
            }
            catch (Exception)
            {
            }
        }

        // Their getMeShaders: after the menu scan, only bundles loaded since are scanned, and the set goes into their cache.
        private static bool GetMeShaders_Prefix()
        {
            if (!s_done)
            {
                s_theirsRan = true;
                FiresLogger.LogInfo($"{Tag}the world started before the menu scan finished ({s_scanned.Count} bundles done); HD_Additions scans itself.");
                return true;
            }
            var late = Stopwatch.StartNew();
            int before = s_scanned.Count;
            foreach (AssetBundle bundle in Resources.FindObjectsOfTypeAll<AssetBundle>()) Scan(bundle);
            var cache = (HashSet<Shader>)s_cached.GetValue(null);
            foreach (Shader shader in s_shaders)
                if (shader != null) cache.Add(shader);
            FiresLogger.LogInfo($"{Tag}its world-join shader scan used the menu's: {s_shaders.Count} shaders; {s_scanned.Count - before} "
                                + $"bundle(s) loaded since were scanned now in {late.Elapsed.TotalMilliseconds:0} ms.");
            return false;
        }
    }
}
