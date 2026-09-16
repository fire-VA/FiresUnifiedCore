using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Terrain
{
    // Spreads Heightmap.ForceGenerateAll across frames when a large batch of SnapToGround objects arrives at
    // once (zone streams, location spawns) and drops its per-tile "Force generating hmap" log line. Small
    // batches still run vanilla in-frame. The spread path snapshots and clears the snapper list, regenerates
    // queued tiles a few per frame under a frame cap, then snaps. DungeonGenerator.Generate and Spawn stay
    // synchronous because the code after them assumes final positions.
    [HarmonyPatch]
    internal static class VASnapToGroundSpreadPatch
    {
        // Tunables — kept const rather than config-bound. If a future
        // user reports an issue we can promote to config.
        private const int FastPathThreshold = 4;   // <= this many: run vanilla
        private const int RegensPerFrame   = 4;   // heightmaps to regen per frame
        private const int MaxSpreadFrames  = 8;   // hard cap on total spread duration

        // Set true by DungeonGenerator.Generate/Spawn prefixes; cleared by
        // matching postfixes. While true our SnappAll prefix returns true
        // (let vanilla run) so dungeon Generate's expected post-SnappAll
        // invariants hold.
        public static bool _inDungeonGenerateScope;

        // Re-entry guard: when our coroutine eventually calls vanilla
        // SnappAll behaviour (to drain newly-added snappers added during
        // the spread window), we must not re-enter the prefix. We don't
        // currently do that — coroutine snaps directly — but keep the
        // flag for symmetry and future-proofing.
        private static bool _inSpreadCoroutine;

        // Reflection handles — looked up once at first use, cached.
        private static FieldInfo _allSnappersField;
        private static FieldInfo _inListField;
        private static MonoBehaviour _host;

        private static FieldInfo AllSnappersField =>
            _allSnappersField ?? (_allSnappersField = AccessTools.Field(typeof(SnapToGround), "m_allSnappers"));
        private static FieldInfo InListField =>
            _inListField ?? (_inListField = AccessTools.Field(typeof(SnapToGround), "m_inList"));

        private static MonoBehaviour Host
        {
            get
            {
                if (_host != null) return _host;
                var go = new GameObject("VASnapToGroundSpreadHost");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _host = go.AddComponent<HostBehaviour>();
                return _host;
            }
        }

        private class HostBehaviour : MonoBehaviour { }

        // ──────────────────────────────────────────────────────────────
        //  Heightmap.ForceGenerateAll — silent replacement
        // ──────────────────────────────────────────────────────────────

        // Replace vanilla's body with a functionally-identical version
        // that skips the ZLog.Log per tile. Returns false → vanilla body
        // is skipped. Used by BOTH our coroutine AND any other caller
        // (Ship.Awake, Vagon, SnapToGround vanilla fast-path) so the log
        // is silenced uniformly.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.ForceGenerateAll))]
        [HarmonyPrefix]
        private static bool Heightmap_ForceGenerateAll_Prefix()
        {
            // Heightmap.GetAllHeightmaps() is the public accessor for the
            // private s_heightmaps list. Identical reference; no
            // reflection needed.
            var all = Heightmap.GetAllHeightmaps();
            if (all == null) return false;
            for (int i = 0; i < all.Count; i++)
            {
                var heightmap = all[i];
                if (heightmap != null && heightmap.HaveQueuedRebuild()) heightmap.Regenerate();
            }
            return false;   // skip vanilla (we did its work without the log)
        }

        // ──────────────────────────────────────────────────────────────
        //  SnapToGround.SnappAll — fast-path or spread coroutine
        // ──────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(SnapToGround), nameof(SnapToGround.SnappAll))]
        [HarmonyPrefix]
        private static bool SnapToGround_SnappAll_Prefix()
        {
            // Carve-outs that ALWAYS run vanilla:
            //   * Inside DungeonGenerator: positions must be final on return.
            //   * Re-entry from our own coroutine (defensive; not currently
            //     reachable but keeps the flag honest).
            if (_inDungeonGenerateScope) return true;
            if (_inSpreadCoroutine) return true;

            var snappers = AllSnappersField?.GetValue(null) as List<SnapToGround>;
            if (snappers == null) return true;            // can't read list → vanilla
            if (snappers.Count == 0) return true;          // nothing to do → vanilla (early returns)
            if (snappers.Count <= FastPathThreshold) return true;   // small batch → vanilla

            // Spread path. Snapshot the snappers, clear vanilla's list so
            // subsequent SnappAll calls don't double-process, then kick
            // the coroutine. m_inList stays true on each (vanilla normally
            // sets it false inside its own loop); we set it false here
            // to keep OnDestroy's check accurate if a snapper dies during
            // the spread window.
            var snapshot = new List<SnapToGround>(snappers.Count);
            for (int i = 0; i < snappers.Count; i++)
            {
                var snapper = snappers[i];
                if (snapper == null) continue;
                snapshot.Add(snapper);
                try { InListField?.SetValue(snapper, false); }
                catch { /* benign — worst case OnDestroy no-ops */ }
            }
            snappers.Clear();

            try
            {
                Host.StartCoroutine(SpreadRegenAndSnap(snapshot));
            }
            catch (Exception ex)
            {
                // Coroutine kick failed — fall back to vanilla SYNCHRONOUSLY
                // by re-inserting the snappers and returning true.
                Debug.LogWarning($"[VASnapSpread] Failed to start coroutine: {ex.Message} — falling back to vanilla sync.");
                snappers.AddRange(snapshot);
                return true;
            }

            return false;   // skip vanilla; coroutine handles it
        }

        // Spread coroutine: regenerate queued heightmaps N per frame,
        // then snap captured snappers in one cheap pass at the end.
        private static IEnumerator SpreadRegenAndSnap(List<SnapToGround> snappers)
        {
            _inSpreadCoroutine = true;
            try
            {
                // Collect queued heightmaps once at start. New tiles that
                // get queued mid-spread aren't part of our batch — they'll
                // be picked up by the NEXT SnappAll (or by any code that
                // calls Heightmap.ForceGenerateAll directly).
                var all = Heightmap.GetAllHeightmaps();
                var queued = new List<Heightmap>();
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        var heightmap = all[i];
                        if (heightmap != null && heightmap.HaveQueuedRebuild()) queued.Add(heightmap);
                    }
                }

                // Determine per-frame budget. If queued is so large that
                // RegensPerFrame * MaxSpreadFrames < queued.Count, scale up
                // perFrame so we finish within the cap.
                int perFrame = RegensPerFrame;
                if (queued.Count > RegensPerFrame * MaxSpreadFrames)
                {
                    perFrame = (queued.Count + MaxSpreadFrames - 1) / MaxSpreadFrames;
                }

                int idx = 0;
                while (idx < queued.Count)
                {
                    int end = Math.Min(idx + perFrame, queued.Count);
                    for (int i = idx; i < end; i++)
                    {
                        var heightmap = queued[i];
                        if (heightmap == null) continue;
                        // Re-check HaveQueuedRebuild in case another
                        // ForceGenerateAll caller flushed this tile while
                        // we were yielding — avoids a redundant regen.
                        if (heightmap.HaveQueuedRebuild()) heightmap.Regenerate();
                    }
                    idx = end;
                    if (idx < queued.Count) yield return null;
                }

                // Snap snappers. Cheap (one transform.position write +
                // optional ZDO write per snapper) so we do them all in
                // one go at the end of the spread.
                for (int i = 0; i < snappers.Count; i++)
                {
                    var snapper = snappers[i];
                    if (snapper == null) continue;
                    try { snapper.Snap(); }
                    catch (Exception ex)
                    {
                        // A snapper that got destroyed mid-spread or had
                        // its ZNetView torn down may throw; swallow per-
                        // snapper so one bad apple doesn't abort the
                        // batch.
                        Debug.LogWarning($"[VASnapSpread] Snap failed on '{snapper?.name ?? "<null>"}': {ex.Message}");
                    }
                }
            }
            finally
            {
                _inSpreadCoroutine = false;
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  DungeonGenerator carve-outs
        // ──────────────────────────────────────────────────────────────

        // Generate calls SnappAll just before clearing m_placedRooms, so spreading would race that clear; the scope flag makes
        // our prefix defer to vanilla. The (int, SpawnMode) overload holds the real body and the other delegates to it, so
        // only that one is patched.
        [HarmonyPatch(typeof(DungeonGenerator), "Generate", new[] { typeof(int), typeof(ZoneSystem.SpawnMode) })]
        [HarmonyPrefix]
        private static void DungeonGenerator_Generate_Prefix() => _inDungeonGenerateScope = true;

        [HarmonyPatch(typeof(DungeonGenerator), "Generate", new[] { typeof(int), typeof(ZoneSystem.SpawnMode) })]
        [HarmonyPostfix]
        private static void DungeonGenerator_Generate_Postfix() => _inDungeonGenerateScope = false;

        // Spawn() is the second SnappAll site, called after the PlaceRoom
        // loop. It has no overloads so the unparameterized form is fine.
        [HarmonyPatch(typeof(DungeonGenerator), "Spawn")]
        [HarmonyPrefix]
        private static void DungeonGenerator_Spawn_Prefix() => _inDungeonGenerateScope = true;

        [HarmonyPatch(typeof(DungeonGenerator), "Spawn")]
        [HarmonyPostfix]
        private static void DungeonGenerator_Spawn_Postfix() => _inDungeonGenerateScope = false;
    }
}
