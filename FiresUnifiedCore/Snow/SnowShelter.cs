using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.927 ([wishbone], Fire 10-27 via [worldgen] / lead: "there can be snow indoors under a roof if it blows in from an opening etc,
    /// but if the room is closed off no snow"). The snow field's SHELTER factor, 0..1 (1 = open sky), multiplied into
    /// <see cref="SnowField.DeepSnowG"/> (FAT's depth texture, the heightmap inject, the sink / slow) and the cover (CoverageAt, so FTS's
    /// piece tint follows). One source: Core, from vanilla's own Cover.GetCoverForPoint.
    ///  - Grid: 1 m cells (FAT's texel size, so no sliver of snow inside a thin wall), sampled bilinearly (soft edges, no hard cuts).
    ///    Only zones with build pieces hold a grid; every other zone is 1 (no cost).
    ///  - A cell: a ray up from ground + 0.5 m hits a piece within RoofReach -> under a roof -> vanilla Cover (cover %) -> factor
    ///    clamp01((ClosedCover - cover) / RampWidth): a closed room (cover >= ClosedCover) = 0, near a doorway / open wall vanilla's cover
    ///    drops and the snow drifts in, fading with distance from the opening. No roof = 1. A 4 m block with no piece in it is skipped.
    ///  - When: a zone is (re)computed after a piece in it is placed / loaded / removed (1 s quiet first), plus a quiet 60 s recheck of
    ///    sheltered zones near the viewer (doors). Main thread, 1 ms per frame budget, resumable; timings at Debug. A changed zone is
    ///    re-announced (FAT refills, vanilla chunks repaint) with one Info why-line.
    ///  - [Snow] No snow in closed rooms (server-synced, ships OFF): off = factor 1 everywhere (today's snow).
    /// </summary>
    public static class SnowShelter
    {
        internal const float ClosedCover = 0.95f, RampWidth = 0.35f, RoofReach = 30f, ProbeUp = 0.5f;
        private const int Cells = 64;   // 1 m cells per 64 m zone
        private const double BudgetMs = 1.0;
        private const int QuietMs = 1000, RecheckMs = 60000;
        private static readonly long BudgetTicks = (long)(BudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);

        private static readonly Dictionary<Vector2s, byte[]> s_grids = new Dictionary<Vector2s, byte[]>();   // 255 = open; absent = all open
        private static readonly Dictionary<Vector2s, int> s_dirty = new Dictionary<Vector2s, int>();         // zone -> TickCount when marked
        private static Job s_job;
        private static int s_recheckAt = Environment.TickCount + RecheckMs;
        private static int s_pieceMask, s_roofMask;
        private static Func<Vector3, float> s_provider; private static string s_providerOwner;

        internal static bool On => SnowConfig.ShelterOn;

        /// <summary>The shelter factor 0..1 at a point (1 = open sky, 0 = a closed room). 1 when [Snow] No snow in closed rooms is off.</summary>
        public static float ShelterAt(Vector3 p)
        {
            if (!On) return 1f;
            var zone = ZoneSystem.GetZone(p);
            byte[] g;
            lock (s_grids) if (!s_grids.TryGetValue(zone, out g)) g = null;
            if (g == null) return 1f;
            Vector3 o = ZoneSystem.GetZonePos(zone) - new Vector3(ZoneSystem.c_ZoneSizeHalf, 0f, ZoneSystem.c_ZoneSizeHalf);
            float fx = Mathf.Clamp(p.x - o.x - 0.5f, 0f, Cells - 1), fz = Mathf.Clamp(p.z - o.z - 0.5f, 0f, Cells - 1);
            int x0 = (int)fx, z0 = (int)fz, x1 = Mathf.Min(x0 + 1, Cells - 1), z1 = Mathf.Min(z0 + 1, Cells - 1);
            float tx = fx - x0, tz = fz - z0;
            float a = Mathf.Lerp(g[z0 * Cells + x0], g[z0 * Cells + x1], tx), b = Mathf.Lerp(g[z1 * Cells + x0], g[z1 * Cells + x1], tx);
            float f = Mathf.Lerp(a, b, tz) / 255f;
            System.Threading.Interlocked.Increment(ref s_lookups);   // 0.2.965: proof counts (lookups in a sheltered zone)
            if (f < 0.98f) System.Threading.Interlocked.Increment(ref s_reduced);
            if (f <= 0.02f) System.Threading.Interlocked.Increment(ref s_zeroed);
            return f;
        }

        // 0.2.965 (lead 10-28, proof table): one Debug line a minute while the key is on: the sheltered zones and their closed / partial
        // cells (the rooms detected), and how many snow lookups in them the shelter cut down or to nothing (the snow suppressed).
        private static int s_lookups, s_reduced, s_zeroed;
        private static int s_proofAt = Environment.TickCount + 60000;

        private static void ProofLine()
        {
            int zones, closed = 0, partial = 0;
            lock (s_grids)
            {
                zones = s_grids.Count;
                foreach (var g in s_grids.Values)
                    foreach (byte b in g) { if (b <= 5) closed++; else if (b < 250) partial++; }
            }
            int lookups = System.Threading.Interlocked.Exchange(ref s_lookups, 0), reduced = System.Threading.Interlocked.Exchange(ref s_reduced, 0),
                zeroed = System.Threading.Interlocked.Exchange(ref s_zeroed, 0);
            if (zones == 0 && lookups == 0) return;
            FiresCore.Logging.FiresLogger.LogDebug($"[Snow] shelter last 60 s: {zones} sheltered zone(s), {closed} closed + {partial} partial 1 m cell(s) (rooms detected); "
                + $"{lookups} snow lookup(s) in them, {reduced} reduced, {zeroed} to no snow (snow suppressed)");
        }

        /// <summary>Add-only: replaces the vanilla Cover ramp for roofed cells (1 = open, 0 = enclosed). The last one registered wins.</summary>
        public static void RegisterShelterProvider(string owner, Func<Vector3, float> openness01)
        {
            s_provider = openness01; s_providerOwner = owner ?? "?";
            FiresCore.Logging.FiresLogger.LogDebug($"[Snow] shelter: provider '{s_providerOwner}' {(openness01 != null ? "registered (replaces the vanilla Cover ramp under roofs)" : "removed")}");
        }

        // 0.2.1072 (Fire: "take ebm into account"): EBM swaps a piece's colliders onto a stand-in (no Piece Awake / OnDestroy), so a zone
        // computed before or during a swap could keep a stale answer - and one with NO roofed cell then had no grid and was never rechecked.
        // Every zone that has had pieces is rechecked now (quiet, 60 s, near the viewer), gridded or not.
        private static readonly HashSet<Vector2s> s_pieceZones = new HashSet<Vector2s>();
        private static readonly Dictionary<Vector2s, int> s_ebmSwapped = new Dictionary<Vector2s, int>();
        /// <summary>0.2.1073: EBM built / removed stand-in colliders in this zone (n pieces): recompute its shelter (the 1 s quiet batches a burst).</summary>
        internal static void EbmChanged(Vector2s zone, int n)
        {
            if (!On) return;
            lock (s_dirty) { s_dirty[zone] = Environment.TickCount; s_pieceZones.Add(zone); s_ebmSwapped.TryGetValue(zone, out int had); s_ebmSwapped[zone] = had + n; }
        }

        // ---- piece events ------------------------------------------------------------------------------------------------------
        // 0.2.1211 ([wishbone], [generator]'s piece-create audit rows 2 + 5, TOWN50; 1.3 us of Piece.Awake, worst 0.3 ms): the piece hooks
        // read ONE bool worked out once per frame - the key (was a SnowConfig.P string lookup + lock per piece) and the owner: while Vedr
        // owns the ground snow (SnowVedrGround.Owns) Vedr's own Piece_Awake_Shelter marks its zones and Core's copy does nothing; the
        // GetComponent<ZNetView> runs only past that. A zone load creates hundreds of pieces in one zone in one frame: after the first,
        // the same zone in the same frame is already marked (one lock per zone per frame, not per piece). Not unpatched at runtime (an
        // unpatch rebuilds Piece.Awake with every other mod's patches at each owner flip). Ownership back to Core marks the loaded zones
        // dirty (MarkZonesDirty from SnowTicker), so rooms built meanwhile are found.
        private static int s_hookFrame = -1, s_markFrame = -1;
        private static bool s_hookOn;
        private static Vector2s s_markZone;
        private static bool PieceHooksOn
        {
            get
            {
                int f = Time.frameCount;
                if (f != s_hookFrame) { s_hookFrame = f; s_hookOn = !SnowVedrGround.Owns && On; }
                return s_hookOn;
            }
        }

        private static void MarkPiece(Vector3 p)
        {
            var z = ZoneSystem.GetZone(p);
            int f = Time.frameCount;
            if (f == s_markFrame && z.Equals(s_markZone)) return;   // this zone is already marked this frame (zone fill)
            s_markFrame = f; s_markZone = z;
            lock (s_dirty) { s_dirty[z] = Environment.TickCount; s_pieceZones.Add(z); }
        }

        /// <summary>0.2.1211: the ground-snow owner came back to Core: these zones' shelter is recomputed (pieces placed while Vedr owned it).</summary>
        internal static void MarkZonesDirty(IEnumerable<Vector2s> zones)
        {
            if (!On || zones == null) return;
            int n = 0;
            lock (s_dirty) foreach (var z in zones) { s_dirty[z] = Environment.TickCount; s_pieceZones.Add(z); n++; }
            if (n > 0) FiresCore.Logging.FiresLogger.LogDebug($"[Snow] shelter: {n} loaded zone(s) marked for a recompute (ground snow back with Core)");
        }

        [HarmonyPatch(typeof(Piece), "Awake")]
        private static class Piece_Awake_Shelter
        {
            private static void Postfix(Piece __instance)
            {
                if (!PieceHooksOn || __instance == null) return;
                var nv = __instance.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid()) return;   // placement ghosts have no ZDO
                MarkPiece(__instance.transform.position);
            }
        }

        [HarmonyPatch(typeof(Piece), "OnDestroy")]
        private static class Piece_OnDestroy_Shelter
        {
            private static void Postfix(Piece __instance)
            {
                if (!PieceHooksOn || __instance == null || ZNetScene.instance == null) return;
                MarkPiece(__instance.transform.position);
            }
        }

        // ---- the main-thread job (SnowTicker.Update) -----------------------------------------------------------------------------
        private sealed class Job
        {
            internal Vector2s Zone; internal byte[] Grid = new byte[Cells * Cells]; internal int Block; internal double Ms; internal int Roofed, Closed, Partial, Covers, Logged, Ignored;   // 0.2.1064: + Logged / Ignored
            internal float MinCover = 1f, MaxCover = 0f; internal bool Forced;
        }

        internal static void Tick()
        {
            if (!On) { if (s_grids.Count > 0) ClearAll(); return; }
            if (s_pieceMask == 0) { s_pieceMask = LayerMask.GetMask("piece", "piece_nonsolid"); s_roofMask = LayerMask.GetMask("piece"); }
            int now = Environment.TickCount;
            if (now - s_recheckAt >= 0) { s_recheckAt = now + RecheckMs; Recheck(); }
            if (now - s_proofAt >= 0) { s_proofAt = now + 60000; ProofLine(); }   // 0.2.965
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            while (System.Diagnostics.Stopwatch.GetTimestamp() - t0 < BudgetTicks)
            {
                if (s_job == null && !NextJob(now)) return;
                if (Step(s_job, t0, false)) Finish(s_job);
            }
        }

        private static bool NextJob(int now)
        {
            lock (s_dirty)
            {
                bool found = false; Vector2s zone = default;
                // 0.2.1074 (Fire 10-31, the player's zone filled in last): of the zones past their quiet, the nearest to the player first
                Vector2s me = SnowTicker.PlayerZone(); int bestD = int.MaxValue;
                foreach (var kv in s_dirty)
                    if (now - kv.Value >= QuietMs) { int d = SnowTicker.ZoneDist(kv.Key, me); if (d < bestD) { bestD = d; zone = kv.Key; found = true; } }
                if (!found) return false;
                s_dirty.Remove(zone);
                s_job = new Job { Zone = zone };
                if (s_ebmSwapped.TryGetValue(zone, out int swapped)) { s_ebmSwapped.Remove(zone); FiresCore.Logging.FiresLogger.LogDebug($"[Snow] shelter recompute zone ({zone.x},{zone.y}) after EBM swap ({swapped} piece(s))"); }
            }
            for (int i = 0; i < s_job.Grid.Length; i++) s_job.Grid[i] = 255;
            return true;
        }

        // one 4 m block (16 cells) per call; true when the zone is done
        private static bool Step(Job j, long t0, bool unbudgeted)
        {
            const int blocksPerSide = Cells / 4;
            Vector3 o = ZoneSystem.GetZonePos(j.Zone) - new Vector3(ZoneSystem.c_ZoneSizeHalf, 0f, ZoneSystem.c_ZoneSizeHalf);
            while (j.Block < blocksPerSide * blocksPerSide)
            {
                long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int bx = j.Block % blocksPerSide, bz = j.Block / blocksPerSide;
                j.Block++;
                Vector3 bc = o + new Vector3(bx * 4f + 2f, 0f, bz * 4f + 2f);
                float gy = Ground(bc);
                // cheap reject: no piece in the 4 m column from just under the ground to RoofReach up
                if (Physics.CheckBox(bc + Vector3.up * (gy + RoofReach * 0.5f - 1f), new Vector3(2.5f, RoofReach * 0.5f + 1f, 2.5f), Quaternion.identity, s_pieceMask, QueryTriggerInteraction.Ignore))
                    for (int cz = 0; cz < 4; cz++)
                        for (int cx = 0; cx < 4; cx++)
                        {
                            int x = bx * 4 + cx, z = bz * 4 + cz;
                            Vector3 p = o + new Vector3(x + 0.5f, 0f, z + 0.5f);
                            p.y = Ground(p) + ProbeUp;
                            j.Grid[z * Cells + x] = (byte)Mathf.RoundToInt(CellFactor(j, p) * 255f);
                        }
                j.Ms += (System.Diagnostics.Stopwatch.GetTimestamp() - b0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (!unbudgeted && System.Diagnostics.Stopwatch.GetTimestamp() - t0 >= BudgetTicks) return j.Block >= blocksPerSide * blocksPerSide;
            }
            return true;
        }

        private const int RoofLogPerZone = 6;
        // null = a real roof; else why the hit is not one (0.2.1064)
        private static string RoofWhy(Collider c)
        {
            if (c == null) return "no collider";
            if (!c.enabled || !c.gameObject.activeInHierarchy) return "disabled / inactive collider";
            if (c.GetComponentInParent<WearNTear>() != null) return null;
            if (FiresCore.Compat.EbmStandIns.Supported) return FiresCore.Compat.EbmStandIns.PrefabOf(c) != 0 ? null : "not a building piece (no WearNTear / EBM stand-in)";   // 0.2.1218: EbmApi
            foreach (var mb in c.GetComponentsInParent<MonoBehaviour>(true))
                if (mb != null && mb.GetType().Name == "EbmStandIn") return null;
            return "not a building piece (no WearNTear / EBM stand-in)";
        }

        private static float CellFactor(Job j, Vector3 p)
        {
            if (!Physics.Raycast(p, Vector3.up, out RaycastHit roof, RoofReach, s_roofMask, QueryTriggerInteraction.Ignore)) return 1f;
            // 0.2.1064 (lead / [worldgen], Fire's bare circle at the DN hut: "zone (4,13) 270 cell(s) under a roof"): only a REAL roof counts -
            // an enabled collider on an active object that belongs to a building piece (a WearNTear) or to an EBM stand-in of one (EBM bakes
            // pieces and keeps their colliders on a stand-in, so a batched roof is still a roof). Anything else on the piece layer is ignored.
            string why = RoofWhy(roof.collider);
            if (j.Logged < RoofLogPerZone)
            {
                j.Logged++;
                var b = roof.collider != null ? roof.collider.bounds : default;
                FiresCore.Logging.FiresLogger.LogDebug($"[Snow] shelter roof ray at ({p.x:0.0},{p.z:0.0}) zone ({j.Zone.x},{j.Zone.y}): hit '{(roof.collider != null ? roof.collider.name : "?")}' ({(roof.collider != null ? roof.collider.GetType().Name : "?")}, size {b.size.x:0.0} x {b.size.y:0.0} x {b.size.z:0.0}, {roof.distance:0.0} m up) -> {(why == null ? "a roof" : "IGNORED: " + why)}");
            }
            if (why != null) { j.Ignored++; return 1f; }
            j.Roofed++;
            float f;
            if (s_provider != null)
            {
                try { f = Mathf.Clamp01(s_provider(p)); }
                catch (Exception e) { FiresCore.Logging.FiresLogger.LogWarning($"[Snow] shelter: provider '{s_providerOwner}' threw ({e.Message}); unregistered, the vanilla Cover ramp is used"); s_provider = null; return CellFactor(j, p); }
            }
            else
            {
                Cover.GetCoverForPoint(p, out float cover, out bool underRoof);
                j.Covers++; j.MinCover = Mathf.Min(j.MinCover, cover); j.MaxCover = Mathf.Max(j.MaxCover, cover);
                // 0.2.1072 (Fire 10-31: "snow should not build up under build pieces"): ground with a real piece straight above it gets NO
                // snow - closed rooms, porches, raised floors, platforms alike (the doorway drift ramp is gone); vanilla Cover is still read for
                // the closed / partial counts in the Debug line
                f = 0f;
            }
            if (f <= 0.02f) j.Closed++; else if (f < 0.98f) j.Partial++;
            return f;
        }

        private static void Finish(Job j)
        {
            s_job = null;
            bool any = false;
            for (int i = 0; i < j.Grid.Length; i++) if (j.Grid[i] != 255) { any = true; break; }
            bool changed;
            lock (s_grids)
            {
                s_grids.TryGetValue(j.Zone, out var old);
                changed = old == null ? any : !any || !Same(old, j.Grid);
                if (any) s_grids[j.Zone] = j.Grid; else s_grids.Remove(j.Zone);
            }
            // 0.2.950 (round 45: 2,148 timing lines, almost all quiet 60 s rechecks): the timing line only when the zone changed or a test forced it
            if (changed || j.Forced) FiresCore.Logging.FiresLogger.LogVerbose($"[Snow] shelter timing: zone ({j.Zone.x},{j.Zone.y}) {j.Ms:0.00} ms, {j.Roofed} roofed cell(s), {j.Covers} vanilla Cover call(s){(changed ? "" : ", unchanged")}");
            if (!changed && !j.Forced) return;
            if (changed)
                FiresCore.Logging.FiresLogger.LogDebug($"[Snow] shelter: zone ({j.Zone.x},{j.Zone.y}) {j.Roofed} cell(s) under a roof, {j.Ignored} roof-ray hit(s) ignored (not a piece) ({j.Closed} closed, {j.Partial} partial; vanilla cover {(j.Covers > 0 ? $"{j.MinCover:0.00}-{j.MaxCover:0.00}" : "n/a")}{(s_provider != null ? $", provider '{s_providerOwner}'" : "")}) -> snow re-announced");
            Announce(j.Zone);
        }

        private static bool Same(byte[] a, byte[] b) { for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }

        private static void Announce(Vector2s z)
        {
            if (!SnowField.ZoneHasSnow(ZoneSystem.GetZonePos(z))) return;
            SnowField.RaiseZoneChanged(z);
            if (!SnowFat.OwnsZone(z)) SnowHeightmapInject.Repaint(z);
        }

        // quiet: re-mark sheltered zones within 2 zones of the viewer (doors open / close); lines only when a zone changes
        private static void Recheck()
        {
            var v = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : (GameCamera.instance != null ? GameCamera.instance.transform.position : Vector3.zero);
            var c = ZoneSystem.GetZone(v);
            int now = Environment.TickCount - QuietMs;
            lock (s_grids)
                foreach (var z in s_grids.Keys)
                    if (Mathf.Abs(z.x - c.x) <= 2 && Mathf.Abs(z.y - c.y) <= 2) lock (s_dirty) if (!s_dirty.ContainsKey(z)) s_dirty[z] = now;
            // 0.2.1073 (Fire: "shouldnt it just be rechecked after the ebm swap?"): the 1072 piece-zone poll is gone; EBM stand-in changes
            // mark their zone directly (SnowShelterEbm)
        }

        private static void ClearAll()
        {
            List<Vector2s> zones;
            lock (s_grids) { zones = new List<Vector2s>(s_grids.Keys); s_grids.Clear(); }
            lock (s_dirty) s_dirty.Clear();
            s_job = null;
            foreach (var z in zones) Announce(z);
            FiresCore.Logging.FiresLogger.LogDebug($"[Snow] shelter: [Snow] No snow in closed rooms is off: {zones.Count} sheltered zone(s) back to open snow");
        }

        private static float Ground(Vector3 p) => ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(p) : p.y;

        /// <summary>Test hook: computes a zone now (unbudgeted, main thread) and returns the why-line counts.</summary>
        internal static string ComputeNow(Vector3 p)
        {
            if (s_pieceMask == 0) { s_pieceMask = LayerMask.GetMask("piece", "piece_nonsolid"); s_roofMask = LayerMask.GetMask("piece"); }
            var j = new Job { Zone = ZoneSystem.GetZone(p), Forced = true };
            for (int i = 0; i < j.Grid.Length; i++) j.Grid[i] = 255;
            lock (s_dirty) s_dirty.Remove(j.Zone);
            Step(j, System.Diagnostics.Stopwatch.GetTimestamp(), true);
            string s = $"zone ({j.Zone.x},{j.Zone.y}): {j.Roofed} roofed, {j.Closed} closed, {j.Partial} partial, cover {(j.Covers > 0 ? $"{j.MinCover:0.00}-{j.MaxCover:0.00}" : "n/a")}, {j.Ms:0.0} ms";
            Finish(j);
            return s;
        }
    }
}
