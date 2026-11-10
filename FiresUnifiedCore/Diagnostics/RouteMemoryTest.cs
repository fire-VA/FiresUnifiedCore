using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using FiresCore.Npc;
using FiresCore.Npc.AI;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// 0.2.919 ([ghost], Fire 10-27 route memory): core_route_memory_test (FDT runner contract, Test / SuperFlat worlds), at the SFSC3
    /// homestead's upstairs chest (293, 54, 935). Route memory is switched on for the test only (RouteMemory.s_testOn), its store cleared.
    /// Steps:
    ///  1 goto 301 952;
    ///  2 setup: two homestead companions + the chest's stand point;
    ///  3 trip 1 (companion A to the chest): arrives and "learned";
    ///  4 trip 2 (A walked back to its start, then to the chest again): "reused", fewer companion FindPath calls, t2 <= 1.1 x t1;
    ///  5 trip 3 (companion B, from A's start): reused (shared);
    ///  6 a wood wall across the route's ground stretch, trip 4: "dropped: stuck", then arrives (re-learned);
    ///  7 the wall removed (WearNTear.Remove): "dropped: build change";
    ///  8 SHOT.
    /// Trips drive the companion's own pathfinding movement (CompanionAI.RequestPathfindingMovement, PlayerCommand authority), the way a
    /// "go there" order does. Cleanup: the wall removed, the flag off, the store cleared, the HUD back.
    /// </summary>
    internal static class RouteMemoryTest
    {
        private const string Command = "core_route_memory_test", Tag = "[RouteMemoryTest] ", Owner = "RouteMemoryTest";
        private const int Steps = 8;
        private const float TripTimeout = 120f;
        private static readonly Vector3 ChestAt = new Vector3(293f, 54f, 935f);
        private static bool s_running;
        private static readonly ManualLogSource s_log = BepInEx.Logging.Logger.CreateLogSource("FiresUnifiedCore");
        private static readonly AccessTools.FieldRef<Hud, bool> s_hudHidden = AccessTools.FieldRefAccess<Hud, bool>("m_userHidden");

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class RouteMemoryTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] core_route_memory_test - companions learn the walk to the homestead's upstairs chest, reuse it (fewer path searches), share it, and drop it when a wall blocks it / is removed (admin, Test / SuperFlat worlds)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null) { Say(args.Context, Tag + "no local player"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        Say(args.Context, $"{Tag}world '{world}' accepted ({kind})");
                        var host = new GameObject("FiresRouteMemoryTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Run(args.Context, host));
                    }, isCheat: true);
            }
        }

        private sealed class Runner : MonoBehaviour { }
        private static void Say(Terminal ctx, string line) { ctx?.AddString(line); s_log.LogInfo(line); }

        private sealed class Capture : ILogListener
        {
            private readonly List<string> m_lines = new List<string>();
            public LogLevel LogLevelFilter => LogLevel.All;
            public void Dispose() { }
            public void LogEvent(object sender, LogEventArgs e)
            {
                string s = e?.Data?.ToString();
                if (s == null || s.StartsWith(Tag) || !s.Contains("[Route]")) return;
                lock (m_lines) m_lines.Add(System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " "));
            }
            internal int Mark { get { lock (m_lines) return m_lines.Count; } }
            internal List<string> Since(int mark, string has) { lock (m_lines) return m_lines.Skip(mark).Where(l => l.Contains(has)).ToList(); }
        }

        private sealed class Trip { public bool Arrived; public float Seconds; public int FindPath; }

        // drive one companion's pathfinding movement to goal until it arrives or times out
        private static IEnumerator Walk(CompanionController c, Vector3 goal, Trip t)
        {
            var ai = c != null ? c.GetCompanionAI() : null;
            float start = Time.time; int fp0 = s_findPath;
            while (ai != null && c != null && Time.time - start < TripTimeout)
            {
                if (ai.RequestPathfindingMovement(goal, false, 1.5f, FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.PlayerCommand, Owner)) { t.Arrived = true; break; }
                yield return null;
            }
            t.Seconds = Time.time - start; t.FindPath = s_findPath - fp0;
            try { c?.GetMovementAuthority()?.ReleaseAuthority(Owner); ai?.StopMoving(); } catch { }
            yield return new WaitForSeconds(1f);
        }

        // companion FindPath calls during the test (RouteMemory's counter resets every 60 s, so the test keeps its own)
        private static int s_findPath;
        [HarmonyPatch(typeof(BaseAI), "FindPath", new[] { typeof(Vector3) })]
        private static class RouteMemoryTest_FindPath
        {
            private static void Prefix(BaseAI __instance) { if (s_running && __instance is CompanionAI) s_findPath++; }
        }

        private static IEnumerator Shot(string name, Action<string> done)
        {
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests", "shots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            yield return new WaitForEndOfFrame();   // the HUD stays as the player has it (Fire 10-14: tests never hide the HUD, shots included)
            ScreenCapture.CaptureScreenshot(path);
            float until = Time.realtimeSinceStartup + 5f; long last = -1;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
                if (!File.Exists(path)) continue;
                long len = new FileInfo(path).Length;
                if (len > 0 && len == last) { done(path + $" ({Screen.width}x{Screen.height})"); yield break; }
                last = len;
            }
            done(File.Exists(path) ? path : null);
        }

        private static IEnumerator Run(Terminal ctx, GameObject host)
        {
            s_running = true;
            var cap = new Capture();
            BepInEx.Logging.Logger.Listeners.Add(cap);
            int pass = 0, fail = 0, skip = 0, step = 0, shots = 0;
            float started = Time.realtimeSinceStartup;
            void Result(string name, bool ok, string detail) { step++; if (ok) pass++; else fail++; Say(ctx, $"{Tag}{step}/{Steps} {name} {(ok ? "PASS" : "FAIL")} - {detail}"); }
            void Skip(string name, string detail) { step++; skip++; Say(ctx, $"{Tag}{step}/{Steps} {name} SKIP - {detail}"); }
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: {Steps} steps, ~{4 * (int)TripTimeout + 120} s");
            GameObject wall = null;
            bool hudWas = Hud.instance != null && s_hudHidden(Hud.instance);
            RouteMemory.s_testOn = true; RouteMemory.ClearForTest();
            // 0.2.937 (Fire 10-27: "our tests are turning off our HUD"; Fire 10-14: tests never hide it): the HUD is never touched

            // 1 goto
            try
            {
                var p = Player.m_localPlayer;
                Vector3 spot = new Vector3(301f, 50f, 952f);
                if (ZoneSystem.instance != null) spot.y = ZoneSystem.instance.GetGroundHeight(spot) + 0.5f;
                p.TeleportTo(spot, p.transform.rotation, true);
            }
            catch (Exception e) { Say(ctx, $"{Tag}goto threw {e.Message}"); }
            float until = Time.realtimeSinceStartup + 40f;
            while (Time.realtimeSinceStartup < until && Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting()) yield return null;
            yield return new WaitForSecondsRealtime(5f);
            var pl = Player.m_localPlayer;
            float off = pl != null ? Utils.DistanceXZ(pl.transform.position, new Vector3(301f, 0f, 952f)) : 999f;
            Result("goto 301 952", off < 15f, $"player {off:0.0} m from the spot");

            // 2 setup: companions + the chest's stand point
            long me = pl != null ? pl.GetPlayerID() : 0;
            var comps = CompanionController.AllCompanions.Where(c => c != null && c.isTamed && c.ownerPlayerId == me && c.GetCompanionAI() != null && Vector3.Distance(c.transform.position, ChestAt) < 80f)
                .OrderBy(c => Vector3.Distance(c.transform.position, ChestAt)).ToList();
            Container chest = null; float cd = 4f;
            foreach (var col in Physics.OverlapSphere(ChestAt, 4f)) { var ct = col != null ? col.GetComponentInParent<Container>() : null; if (ct != null && Vector3.Distance(ct.transform.position, ChestAt) < cd) { cd = Vector3.Distance(ct.transform.position, ChestAt); chest = ct; } }
            CompanionController a = comps.Count > 0 ? comps[comps.Count - 1] : null, b = comps.Count > 1 ? comps[0] : null;   // A: the farthest (the longest walk)
            Vector3 goal = Vector3.zero; bool haveGoal = false;
            if (chest != null && a != null)
                haveGoal = FiresCore.Npc.Core.InteractionPointHelper.TryGetPathReachPoint(chest.gameObject, chest.transform.position, a.transform.position, 1.5f, out goal);
            Vector3 aStart = a != null ? a.transform.position : Vector3.zero;
            bool setup = a != null && b != null && haveGoal;
            Result("setup", setup, $"{comps.Count} companion(s) within 80 m; chest {(chest != null ? $"{Utils.GetPrefabName(chest.gameObject)} at {chest.transform.position:F1}" : "NOT FOUND")}; goal {(haveGoal ? goal.ToString("F1") : "none (no walkable stand point)")}; A {a?.companionName} at {aStart:F1} ({(a != null ? Vector3.Distance(aStart, goal) : 0f):0} m), B {b?.companionName}");

            if (!setup) { for (int i = step; i < Steps; i++) Skip("route", "no setup"); }
            else
            {
                // 3 trip 1: learn
                int m = cap.Mark; var t1 = new Trip();
                yield return Walk(a, goal, t1);
                var learned = cap.Since(m, "learned");
                Result("trip 1 learns", t1.Arrived && learned.Count > 0, $"arrived {t1.Arrived} in {t1.Seconds:0.0} s, companion FindPath {t1.FindPath}; {(learned.Count > 0 ? learned[0] : "no 'learned' line")}; routes {RouteMemory.Count}");

                // 4 trip 2: back to the start, then reuse
                var back = new Trip(); yield return Walk(a, aStart, back);
                m = cap.Mark; var t2 = new Trip();
                yield return Walk(a, goal, t2);
                var reused = cap.Since(m, "reused route");
                bool fewer = t2.FindPath < t1.FindPath, faster = t2.Seconds <= t1.Seconds * 1.1f;
                Result("trip 2 reuses", t2.Arrived && reused.Count > 0 && fewer && faster, $"back {back.Arrived} ({back.Seconds:0} s); arrived {t2.Arrived} in {t2.Seconds:0.0} s (trip 1 {t1.Seconds:0.0} s, <= 1.1x {faster}), FindPath {t2.FindPath} vs {t1.FindPath} (fewer {fewer}); {(reused.Count > 0 ? reused[0] : "no 'reused' line")}");

                // 5 trip 3: B from A's start (shared)
                var bTo = new Trip(); yield return Walk(b, aStart, bTo);
                m = cap.Mark; var t3 = new Trip();
                yield return Walk(b, goal, t3);
                var shared = cap.Since(m, "reused route");
                Result("trip 3 shared", t3.Arrived && shared.Count > 0, $"B to the start {bTo.Arrived}; arrived {t3.Arrived} in {t3.Seconds:0.0} s, FindPath {t3.FindPath}; {(shared.Count > 0 ? shared[0] : "no 'reused' line")}");

                // 6 a wall across the route's ground stretch: stuck drop, then re-learn
                var back2 = new Trip(); yield return Walk(a, aStart, back2);
                Vector3 wallAt = Vector3.zero; Quaternion wallRot = Quaternion.identity; bool placed = false;
                try
                {
                    if (RouteMemory.Find(a.transform, goal, out var route, out var pts, out int join))
                    {
                        // the first corner past the join on level ground (within 0.4 m of the terrain under it), a third of the way along
                        for (int i = Mathf.Max(join + 2, pts.Length / 3); i < pts.Length - 2 && !placed; i++)
                        {
                            float gy = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(pts[i]) : pts[i].y;
                            if (Mathf.Abs(pts[i].y - gy) > 0.4f) continue;
                            Vector3 seg = pts[i + 1] - pts[i - 1]; seg.y = 0f;
                            if (seg.sqrMagnitude < 0.01f) continue;
                            wallAt = new Vector3(pts[i].x, gy, pts[i].z); wallRot = Quaternion.LookRotation(seg.normalized);
                            var prefab = ZNetScene.instance?.GetPrefab("woodwall");
                            if (prefab != null) { wall = UnityEngine.Object.Instantiate(prefab, wallAt, wallRot); placed = wall != null; }
                        }
                    }
                }
                catch (Exception e) { Say(ctx, $"{Tag}wall threw {e.Message}"); }
                yield return new WaitForSeconds(10f);   // build-test rule: the piece settles and stands on the ground before the trip
                bool standing = wall != null && wall.GetComponent<WearNTear>() != null;
                m = cap.Mark; var t4 = new Trip();
                if (placed && standing) yield return Walk(a, goal, t4);
                var stuck = cap.Since(m, "dropped: stuck"); var relearn = cap.Since(m, "learned");
                if (!placed || !standing) Skip("wall: stuck drop + re-learn", $"no level ground corner on the route for a wall (placed {placed}, standing {standing})");
                else Result("wall: stuck drop + re-learn", stuck.Count > 0 && t4.Arrived && relearn.Count > 0, $"wall at {wallAt:F1}; {(stuck.Count > 0 ? stuck[0] : "no 'dropped: stuck' line")}; arrived {t4.Arrived} in {t4.Seconds:0.0} s; {(relearn.Count > 0 ? relearn[0] : "not re-learned")}");

                // 7 the wall removed: build-change drop of the re-learned detour
                m = cap.Mark;
                if (wall != null) { try { wall.GetComponent<WearNTear>()?.Remove(); } catch (Exception e) { Say(ctx, $"{Tag}remove threw {e.Message}"); } }
                yield return new WaitForSeconds(2f);
                var build = cap.Since(m, "dropped: build change");
                if (!placed || !standing) Skip("wall removed: build drop", "no wall");
                else Result("wall removed: build drop", build.Count > 0, build.Count > 0 ? build[0] : $"no 'dropped: build change' line (routes {RouteMemory.Count})");
                wall = null;
            }

            // 8 SHOT
            string got = null; yield return Shot("core_route_memory", x => got = x);
            if (got != null) { shots++; Say(ctx, $"{Tag}SHOT {got}"); }
            Result("shot", got != null, got ?? "no file");

            // cleanup
            try { if (wall != null) wall.GetComponent<WearNTear>()?.Remove(); } catch { }
            RouteMemory.s_testOn = false; RouteMemory.ClearForTest();
            if (Hud.instance != null) s_hudHidden(Hud.instance) = hudWas;
            BepInEx.Logging.Logger.Listeners.Remove(cap);
            Say(ctx, $"{Tag}END {(fail == 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip, {pass + fail} compared, {shots} shot(s) in {Time.realtimeSinceStartup - started:0.0} s");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }
    }
}
