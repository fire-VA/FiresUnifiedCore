using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Materials
{
    /// <summary>
    /// Debug-only proof for the load-time "no working game shader loaded for" warning, which lists every LOADED material,
    /// including bundle copies nothing ever draws. This census asks the in-game question instead: which renderers in the
    /// world draw a broken shader (an empty-named rip stub, an unsupported shader, or Hidden/InternalErrorShader), so are
    /// magenta on screen, which object they sit on, and which loaded asset bundle that object's prefab came from.
    /// Two snapshots after the local player spawns (20 s, then 180 s for creatures and items that loaded later), only at
    /// Log Level = Debug, checked before anything is scanned. Read-only: nothing is changed.
    /// </summary>
    internal static class PinkCensus
    {
        private const float FirstDelay = 20f, SecondDelay = 180f;
        private const string InternalError = "Hidden/InternalErrorShader";

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class AfterJoin
        {
            private static bool _done;
            private static void Postfix(Player __instance)
            {
                if (_done || __instance != Player.m_localPlayer || !FiresLogger.DebugEnabled) return;
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
                _done = true;
                __instance.StartCoroutine(Run());
            }
        }

        private static IEnumerator Run()
        {
            yield return new WaitForSeconds(FirstDelay);
            FiresCore.Async.FrameBudgetScheduler.ScheduleCoroutine(FiresCore.Async.WorkPriority.Low, Census($"{FirstDelay:0} s after join"));
            yield return new WaitForSeconds(SecondDelay - FirstDelay);
            FiresCore.Async.FrameBudgetScheduler.ScheduleCoroutine(FiresCore.Async.WorkPriority.Low, Census($"{SecondDelay:0} s after join"));
        }

        // 0.2.1210 ([visual], lead 11-08: PinkCensus.Run = a 256 ms hitch frame, FDT [Hitch] 311 ms in the TOWN50 cell-0 leg): the census ran
        // in ONE frame over Resources.FindObjectsOfTypeAll<Renderer>() (every renderer Unity holds, prefab and bundle assets included, sorted)
        // with a material read per renderer. Now: one unsorted scene-only snapshot (Object.FindObjectsByType, inactive included, the same
        // set the old scene filter kept), then Core's one FrameBudgetScheduler (Low lane) walks it ChunkSize renderers per step, and the
        // bundle index is its own step. The Debug line reports the snapshot ms, the steps and the main-thread ms in total.
        private const int ChunkSize = 512;

        private static string BrokenLabel(Shader shader)
        {
            if (shader == null) return "no shader";
            if (string.IsNullOrWhiteSpace(shader.name)) return "<unnamed shader>";
            if (shader.name == InternalError) return InternalError;
            return shader.isSupported ? null : shader.name + " (unsupported)";
        }

        private sealed class RootHits
        {
            internal int Drawn, Hidden;
            internal readonly SortedSet<string> Materials = new SortedSet<string>(StringComparer.Ordinal);
        }

        private static IEnumerator Census(string when)
        {
            var timer = new System.Diagnostics.Stopwatch();
            var byRoot = new SortedDictionary<string, RootHits>(StringComparer.Ordinal);
            var slots = new List<Material>();
            int renderers = 0, steps = 0;
            long snapshotMs;
            Renderer[] all;
            timer.Start();
            try { all = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None); }
            catch (Exception ex) { FiresLogger.LogDebug($"[PinkCensus] {when}: snapshot failed: {ex.GetType().Name}: {ex.Message}"); yield break; }
            timer.Stop(); snapshotMs = timer.ElapsedMilliseconds;
            for (int start = 0; start < all.Length; start += ChunkSize)
            {
                timer.Start();
                try { renderers += Scan(all, start, Math.Min(all.Length, start + ChunkSize), byRoot, slots); }
                catch (Exception ex) { FiresLogger.LogDebug($"[PinkCensus] {when}: census failed: {ex.GetType().Name}: {ex.Message}"); yield break; }
                timer.Stop(); steps++;
                yield return null;
            }
            timer.Start();
            Report(when, byRoot, renderers, $"snapshot {snapshotMs} ms, then {steps} step(s) of {ChunkSize} through Core's frame budget, {timer.ElapsedMilliseconds} ms main thread in total");
            timer.Stop();
        }

        private static int Scan(Renderer[] all, int from, int to, SortedDictionary<string, RootHits> byRoot, List<Material> slots)
        {
            int renderers = 0;
            {
                for (int r = from; r < to; r++)
                {
                    var renderer = all[r];
                    if (renderer == null || !renderer.gameObject.scene.IsValid() || !renderer.gameObject.scene.isLoaded) continue;   // prefabs and bundle assets are not in the world
                    renderers++;
                    renderer.GetSharedMaterials(slots);
                    for (int i = 0; i < slots.Count; i++)
                    {
                        var material = slots[i];
                        if (material == null) continue;
                        string broken = BrokenLabel(material.shader);
                        if (broken == null) continue;
                        string root = renderer.transform.root.name;
                        if (!byRoot.TryGetValue(root, out RootHits hits)) byRoot[root] = hits = new RootHits();
                        if (renderer.enabled && renderer.gameObject.activeInHierarchy) hits.Drawn++; else hits.Hidden++;
                        string matName = string.IsNullOrEmpty(material.name) ? "<unnamed material>" : material.name.Replace(" (Instance)", "");
                        hits.Materials.Add($"'{matName}' [{broken}] on '{renderer.name}' slot {i}");
                    }
                }
            }
            return renderers;
        }

        private static void Report(string when, SortedDictionary<string, RootHits> byRoot, int renderers, string cost)
        {
            try
            {
                if (byRoot.Count == 0)
                {
                    FiresLogger.LogDebug($"[PinkCensus] {when}: {renderers} renderer(s) in the world, none draws a broken shader (nothing magenta) ({cost})");
                    return;
                }

                var bundleOf = BundleIndex();
                int drawn = byRoot.Values.Sum(h => h.Drawn), hidden = byRoot.Values.Sum(h => h.Hidden);
                var sb = new StringBuilder();
                sb.Append($"[PinkCensus] {when}: {renderers} renderer(s) in the world; {drawn} material slot(s) DRAWN with a broken shader (magenta on screen), "
                    + $"{hidden} on hidden/disabled renderers, on {byRoot.Count} object(s) ({cost}):");
                foreach (var pair in byRoot)
                {
                    string prefab = pair.Key.Replace("(Clone)", "").Trim();
                    string bundle = bundleOf.TryGetValue(prefab.ToLowerInvariant(), out string b) ? b : "no loaded bundle names this prefab (vanilla, or built in code)";
                    sb.Append($"\n    '{pair.Key}' (bundle: {bundle}): drawn {pair.Value.Drawn}, hidden {pair.Value.Hidden}: {string.Join("; ", pair.Value.Materials)}");
                }
                FiresLogger.LogDebug(sb.ToString());
            }
            catch (Exception ex)
            {
                FiresLogger.LogDebug($"[PinkCensus] {when}: census failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Prefab name (lower case, no extension) -> the loaded bundle(s) that list it, read from each bundle's asset names.
        private static Dictionary<string, string> BundleIndex()
        {
            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (bundle == null || bundle.isStreamedSceneAssetBundle) continue;
                string[] names;
                try { names = bundle.GetAllAssetNames(); } catch { continue; }
                foreach (string path in names)
                {
                    if (!path.EndsWith(".prefab", StringComparison.Ordinal)) continue;
                    int slash = path.LastIndexOf('/');
                    string key = path.Substring(slash + 1, path.Length - slash - 1 - ".prefab".Length);
                    index[key] = index.TryGetValue(key, out string had) && had != bundle.name ? had + ", " + bundle.name : bundle.name;
                }
            }
            return index;
        }
    }
}
