using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Terrain
{
    // VASnapToGroundSpreadPatch — time-slices Heightmap.ForceGenerateAll
    // across multiple frames when a large batch of SnapToGround objects
    // needs ground-snapping at once, AND silences the per-tile
    // "Force generating hmap ..." log line.
    //
    // Lives in FiresRPGmaker (not FiresAdminPrefabs) because this is a
    // terrain-system fix — it has nothing to do with the hammer/build-
    // piece pipeline FAP owns. RPGmaker already owns the
    // HeightmapOverride module and other terrain-aware code, so this is
    // its natural home.
    //
    // Why this exists
    // ---------------
    // Vanilla flow on a zone-stream / location-spawn / dungeon-spawn:
    //
    //   SnapToGround.SnappAll()
    //     → Heightmap.ForceGenerateAll()  // iterates EVERY queued heightmap,
    //         foreach hmap with HaveQueuedRebuild():    // synchronously runs
    //           ZLog.Log("Force generating hmap ...")   // its Regenerate
    //           hmap.Regenerate()                       // (5-30 ms per tile)
    //     → foreach snapper in m_allSnappers:
    //         snapper.Snap()  // cheap; transform.position += groundHeight
    //
    // On heavy modded servers, a single zone-stream batch can have 20-40
    // SnapToGround objects across 6-10 heightmap tiles, all flushed in
    // one frame → 50-300 ms hitch + log spam.
    //
    // Fix
    // ---
    //   * Fast-path: <= 4 snappers waiting → run vanilla unchanged. The
    //     bulk of frames hit this; we don't add overhead.
    //   * Spread-path: > 4 snappers → start a coroutine that:
    //       1. Snapshots the snapper list and clears m_allSnappers (so
    //          subsequent vanilla SnappAll calls don't double-process).
    //       2. Walks the heightmap list and regenerates queued tiles in
    //          batches of N per frame, with a hard cap of MaxSpreadFrames
    //          (8) so even big bursts finish within ~half a second.
    //       3. After all regens, snaps the captured snappers (cheap, ~1ms
    //          for the whole batch).
    //   * DungeonGenerator carve-out: Generate() and Spawn() both call
    //     SnappAll at known synchronization points where deferring would
    //     race with subsequent vanilla code that assumes positions are
    //     final. A Harmony prefix on each sets _inDungeonGenerateScope;
    //     our SnappAll prefix sees the flag and yields to vanilla.
    //   * Log silence: Heightmap.ForceGenerateAll is replaced with a
    //     functionally-identical silent version (no ZLog.Log per tile).
    //     Both the spread-path coroutine and the fast-path / vanilla
    //     callers benefit.
    //
    // Visible tradeoffs in spread mode
    // --------------------------------
    //   * Brief ~50-100 ms visual flicker: snappers stay at their
    //     pre-snap y for up to MaxSpreadFrames before settling. Usually
    //     invisible — most snappers spawn off-camera during zone-stream.
    //     The fast-path absorbs small batches so visible-area cases
    //     where the player is staring stay unchanged.
    //   * ZDO position write delay: Snap() persists to the ZDO at
    //     the end. During the spread window the ZDO holds the pre-snap
    //     position. In real gameplay no other peer is reading that ZDO
    //     yet (still zone-streaming on their end too).
    //
    // No correctness risk for DungeonGenerator — the carve-out keeps
    // its two SnappAll sites synchronous.
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
                var h = all[i];
                if (h != null && h.HaveQueuedRebuild()) h.Regenerate();
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
                var s = snappers[i];
                if (s == null) continue;
                snapshot.Add(s);
                try { InListField?.SetValue(s, false); }
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
                        var h = all[i];
                        if (h != null && h.HaveQueuedRebuild()) queued.Add(h);
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
                        var h = queued[i];
                        if (h == null) continue;
                        // Re-check HaveQueuedRebuild in case another
                        // ForceGenerateAll caller flushed this tile while
                        // we were yielding — avoids a redundant regen.
                        if (h.HaveQueuedRebuild()) h.Regenerate();
                    }
                    idx = end;
                    if (idx < queued.Count) yield return null;
                }

                // Snap snappers. Cheap (one transform.position write +
                // optional ZDO write per snapper) so we do them all in
                // one go at the end of the spread.
                for (int i = 0; i < snappers.Count; i++)
                {
                    var s = snappers[i];
                    if (s == null) continue;
                    try { s.Snap(); }
                    catch (Exception ex)
                    {
                        // A snapper that got destroyed mid-spread or had
                        // its ZNetView torn down may throw; swallow per-
                        // snapper so one bad apple doesn't abort the
                        // batch.
                        Debug.LogWarning($"[VASnapSpread] Snap failed on '{s?.name ?? "<null>"}': {ex.Message}");
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

        // Generate() calls SnappAll just before m_placedRooms.Clear().
        // Spreading would race with the immediately-following Clear in ghost
        // mode (DestroyImmediate-then-list-clear). Mark the scope so our
        // SnappAll prefix yields to vanilla.
        //
        // DungeonGenerator has TWO Generate overloads:
        //   Generate(ZoneSystem.SpawnMode)          — wrapper at line 95
        //   Generate(int seed, ZoneSystem.SpawnMode) — real impl at line 117,
        //                                              contains the SnappAll
        // Without an explicit parameter list, HarmonyPatch hits an
        // AmbiguousMatchException. We target the inner overload directly —
        // the wrapper just delegates to it, so patching only the inner one
        // covers every call path.
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
