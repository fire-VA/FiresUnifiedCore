using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace FiresCore.Async
{
    // Frame-budgeted batch prefab Instantiate. Walks a prefab list,
    // Instantiates each into a hidden world position, and Destroys at the
    // next frame boundary — the goal is to force Unity's shader-variant
    // compilation, material warm-up, and asset import to happen during a
    // controlled coroutine instead of mid-gameplay when the first real
    // instance spawns.
    //
    // Why this matters: first-Instantiate cost on a bundled prefab is
    // dominated by shader-variant compilation which BLOCKS the main thread
    // synchronously. A 4-second compile in the middle of a player swing
    // animation freezes the game. Warming during a controlled phase
    // (loading screen, world-init banner) absorbs that cost where the
    // user expects it.
    //
    // Decoupled from any mod's config: pass PrewarmOptions in. The skip
    // mechanisms (stateful components + name-substring + runtime-slow)
    // are all optional and configurable.
    //
    // ZNetView.m_forceDisableInit scoping is the critical bit: without it,
    // every Instantiate creates a ZDO that ZNetScene.RemoveObjects then
    // tries to traverse forever — NRE spam. The flag is global static, so
    // we set per-Instantiate in try/finally to avoid collision with any
    // OTHER code path Instantiating prefabs during our yields.
    public static class PrewarmCoroutine
    {
        private static readonly Vector3 BelowWorld = new Vector3(0f, -1000f, 0f);

        public static IEnumerator Run(
            IList<GameObject> prefabs,
            PrewarmOptions options = null,
            Action<string> logInfo = null,
            Action<string> logWarn = null,
            Action<string> logDebug = null)
        {
            options ??= PrewarmOptions.Default;
            logInfo  ??= UnityEngine.Debug.Log;
            logWarn  ??= UnityEngine.Debug.LogWarning;
            logDebug ??= _ => { };

            if (prefabs == null) yield break;

            int perFrame = Math.Max(1, options.PrefabsPerFrame);
            float budgetMs = Math.Max(1f, options.FrameBudgetMs);
            long budgetTicks = (long)(budgetMs * Stopwatch.Frequency / 1000.0);
            int total = prefabs.Count;

            int processed = 0, skipped = 0, errors = 0;
            long totalTicks = 0, worstTicks = 0;
            string worstName = string.Empty;

            var runtimeSlowSkips = new HashSet<string>(StringComparer.Ordinal);
            LoadPersistedSkips(options.PersistSlowSkipPath, runtimeSlowSkips, logInfo);
            var nameTokens = ParseSkipTokens(options.SkipNameContains);

            var wall = Stopwatch.StartNew();
            var frameBudget = Stopwatch.StartNew();
            logInfo($"[Prewarm] Starting prefab warm-up: {total} prefabs, budget {budgetMs:F1}ms/frame (hard cap {perFrame}/frame).");

            var frameInstances = new List<GameObject>(perFrame);

            for (int i = 0; i < prefabs.Count; i++)
            {
                var prefab = prefabs[i];
                if (prefab == null) { skipped++; continue; }

                // Cheapest first: known-slow (persisted from prior sessions) +
                // config name tokens are string checks — a 29s mega-prefab on
                // the persisted list must not even pay a component scan.
                if (ShouldSkipByName(prefab.name, nameTokens, runtimeSlowSkips))
                {
                    skipped++;
                    if (options.Verbose) logDebug($"[Prewarm] Skipped {prefab.name} (name-pattern / known-slow).");
                    continue;
                }

                // Pre-flight size gate: a single Instantiate is atomic on the
                // main thread — no frame budget can split it. Instantiate cost
                // scales with hierarchy size, so counting transforms (a cheap
                // traversal, no Awake, no shader compile) rejects mega-prefabs
                // (combined-build pieces with thousands of children) BEFORE the
                // first-ever 20-30s freeze, not after.
                if (options.MaxTransformCount > 0)
                {
                    int cap = options.MaxTransformCount;
                    int nodes = CountTransforms(prefab.transform, cap + 1);
                    if (nodes > cap)
                    {
                        skipped++;
                        if (runtimeSlowSkips.Add(prefab.name))
                        {
                            logWarn($"[Prewarm] '{prefab.name}' exceeds {cap} transforms — skipping warm-up "
                                + "(a single mega-prefab Instantiate can freeze the client for tens of seconds; "
                                + "it will warm on first real spawn instead). Persisted for future sessions.");
                            AppendPersistedSkip(options.PersistSlowSkipPath, prefab.name, logWarn);
                        }
                        continue;
                    }
                }

                if (options.SkipStatefulComponents && HasStatefulComponent(prefab))
                {
                    skipped++;
                    if (options.Verbose) logDebug($"[Prewarm] Skipped {prefab.name} (stateful component).");
                    continue;
                }

                long t0 = Stopwatch.GetTimestamp();
                GameObject inst = null;

                // ZNetView.m_forceDisableInit scoping: per-Instantiate
                // try/finally so the global flag is on for as little time
                // as possible. Concurrent Instantiates from other code
                // paths during our yields would otherwise see the flag.
                ZNetView.m_forceDisableInit = true;
                try
                {
                    inst = UnityEngine.Object.Instantiate(prefab, BelowWorld, Quaternion.identity);
                    inst.SetActive(false);
                    frameInstances.Add(inst);
                }
                catch (Exception ex)
                {
                    errors++;
                    logWarn($"[Prewarm] Instantiate failed for '{prefab.name}': {ex.Message}");
                }
                finally
                {
                    ZNetView.m_forceDisableInit = false;
                }

                long elapsed = Stopwatch.GetTimestamp() - t0;
                totalTicks += elapsed;
                if (elapsed > worstTicks) { worstTicks = elapsed; worstName = prefab.name; }
                processed++;

                if (options.SlowInstantiateThresholdMs > 0f)
                {
                    double elapsedMs = elapsed * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs >= options.SlowInstantiateThresholdMs && runtimeSlowSkips.Add(prefab.name))
                    {
                        // Persist: without this the stall repeats on EVERY login
                        // (each prefab warms once per session, so a session-only
                        // list never actually saved anyone anything).
                        logWarn(
                            $"[Prewarm] '{prefab.name}' took {elapsedMs:F0}ms to instantiate " +
                            $"(threshold {options.SlowInstantiateThresholdMs:F0}ms). " +
                            $"Persisted to the slow-prefab skip list — future sessions won't warm it.");
                        AppendPersistedSkip(options.PersistSlowSkipPath, prefab.name, logWarn);
                    }
                }

                if (options.Verbose)
                {
                    double ms = elapsed * 1000.0 / Stopwatch.Frequency;
                    if (ms > 1.0) logDebug($"[Prewarm]   {prefab.name} = {ms:F2}ms");
                }

                bool frameFull = frameBudget.ElapsedTicks >= budgetTicks
                                 || frameInstances.Count >= perFrame;
                if (frameFull)
                {
                    yield return null;
                    DestroyBatch(frameInstances);
                    frameBudget.Restart();
                }
            }

            if (frameInstances.Count > 0)
            {
                yield return null;
                DestroyBatch(frameInstances);
            }

            wall.Stop();
            double wallTotalMs = totalTicks * 1000.0 / Stopwatch.Frequency;
            double worstMs = worstTicks * 1000.0 / Stopwatch.Frequency;
            logInfo(
                $"[Prewarm] DONE in {wall.Elapsed.TotalSeconds:F2}s wall — " +
                $"{processed} processed, {skipped} skipped, {errors} errors. " +
                $"Total Instantiate cost: {wallTotalMs:F0}ms. " +
                $"Worst single: {worstMs:F1}ms ({worstName}).");
        }

        private static void DestroyBatch(List<GameObject> batch)
        {
            for (int j = 0; j < batch.Count; j++)
                if (batch[j] != null) UnityEngine.Object.Destroy(batch[j]);
            batch.Clear();
        }

        private static string[] ParseSkipTokens(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();
            var parts = raw.Split(',');
            var tokens = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                string t = part?.Trim();
                if (!string.IsNullOrEmpty(t)) tokens.Add(t);
            }
            return tokens.ToArray();
        }

        private static bool ShouldSkipByName(string prefabName, string[] tokens, HashSet<string> runtimeSlowSkips)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (runtimeSlowSkips.Contains(prefabName)) return true;
            for (int i = 0; i < tokens.Length; i++)
                if (prefabName.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        // Transform-hierarchy node count with an early-exit cap, so probing a
        // 10k-node mega-prefab costs cap+1 visits instead of a full traversal
        // (and no array allocation like GetComponentsInChildren would).
        private static int CountTransforms(Transform t, int cap)
        {
            int count = 1;
            for (int i = 0; i < t.childCount && count < cap; i++)
                count += CountTransforms(t.GetChild(i), cap - count);
            return count;
        }

        // One prefab name per line; '#' comments allowed. Missing file = empty.
        private static void LoadPersistedSkips(string path, HashSet<string> into, Action<string> logInfo)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!System.IO.File.Exists(path)) return;
                int added = 0;
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    string t = line?.Trim();
                    if (string.IsNullOrEmpty(t) || t.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (into.Add(t)) added++;
                }
                if (added > 0)
                    logInfo($"[Prewarm] Loaded {added} persisted slow-prefab skip(s) from {System.IO.Path.GetFileName(path)}.");
            }
            catch (Exception ex)
            {
                logInfo($"[Prewarm] Could not read slow-skip file '{path}': {ex.Message}");
            }
        }

        private static void AppendPersistedSkip(string path, string prefabName, Action<string> logWarn)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(prefabName)) return;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(path, prefabName + Environment.NewLine);
            }
            catch (Exception ex)
            {
                logWarn($"[Prewarm] Could not persist slow-skip '{prefabName}': {ex.Message}");
            }
        }

        // Prefabs whose Awake / OnDestroy depends on world state that
        // doesn't exist at our (0,-1000,0) prewarm position. Each of these
        // was caught throwing NRE during prewarm in production logs.
        // Skipping costs us shader pre-compile on these specific prefabs
        // (worth it — they're rarely spawned in the early game anyway).
        private static bool HasStatefulComponent(GameObject prefab)
        {
            if (prefab.GetComponentInChildren<Player>()           != null) return true;
            if (prefab.GetComponentInChildren<Character>()        != null) return true;
            if (prefab.GetComponentInChildren<MineRock>()         != null) return true;
            if (prefab.GetComponentInChildren<MineRock5>()        != null) return true;
            if (prefab.GetComponentInChildren<Fire>()             != null) return true;
            if (prefab.GetComponentInChildren<Fireplace>()        != null) return true;
            if (prefab.GetComponentInChildren<ShieldGenerator>()  != null) return true;
            if (prefab.GetComponentInChildren<TombStone>()        != null) return true;
            if (prefab.GetComponentInChildren<Valkyrie>()         != null) return true;
            if (prefab.GetComponentInChildren<RandomFlyingBird>() != null) return true;
            if (prefab.GetComponentInChildren<Ragdoll>()          != null) return true;
            if (prefab.GetComponentInChildren<DungeonGenerator>() != null) return true;
            if (prefab.GetComponentInChildren<LocationProxy>()    != null) return true;
            return false;
        }
    }

    public class PrewarmOptions
    {
        public static readonly PrewarmOptions Default = new PrewarmOptions();

        // Maximum prefabs Instantiated per frame even if the time budget
        // hasn't been spent. Hard upper bound — typical config is the
        // time budget governing, this only matters when many prefabs
        // Instantiate cheaply enough to overflow the frame.
        public int PrefabsPerFrame = 24;

        // Wall-time budget per frame in milliseconds. Yield as soon as
        // this elapses, regardless of how many prefabs that took.
        public float FrameBudgetMs = 8f;

        // When a single prefab's Instantiate exceeds this, remember its
        // name (and persist it via PersistSlowSkipPath when set) so it is
        // never warmed again. Zero = disabled.
        public float SlowInstantiateThresholdMs = 500f;

        // Pre-flight hierarchy gate: skip any prefab with more transform
        // nodes than this BEFORE Instantiating. A single Instantiate is
        // atomic on the main thread — no frame budget can split it — and a
        // combined-build mega-prefab can freeze the client for 20-30s. The
        // count is a cheap capped traversal. Zero = disabled.
        public int MaxTransformCount = 0;

        // File persisting slow/oversize prefab names across sessions (one
        // name per line, '#' comments). Loaded at Run start; appended when
        // the threshold or the transform gate trips. Null/empty = in-memory
        // only (the pre-persistence behavior, which re-pays every stall on
        // every login).
        public string PersistSlowSkipPath = null;

        // Per-line debug logging during the pass.
        public bool Verbose = false;

        // Comma-separated substring tokens. Any prefab whose name
        // contains any token (case-insensitive) is skipped. Use for
        // known-slow / known-broken bundle prefabs.
        public string SkipNameContains = string.Empty;

        // Apply the built-in stateful-component skip list (Player,
        // Character, MineRock, Fire, Valkyrie, etc.). Set false to
        // attempt Instantiate on every prefab (rare — used for shader
        // pre-warm runs that don't care about Awake errors).
        public bool SkipStatefulComponents = true;
    }
}
