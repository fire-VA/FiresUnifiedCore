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
    /// 0.2.92x ([ghost], Fire 10-27: "with all of their different movements companions should never be getting stuck basically anywhere"):
    /// core_companion_unstuck_test (FDT runner contract, Test / SuperFlat worlds). The pathing moves (dynamic pace + unstuck ladder) are on
    /// for the test only (CompanionAI.s_pathingTestOn, never saved to the cfg). On open ground 25 m east of the SFSC3 homestead, a homestead
    /// companion is put in each trap, built from vanilla pieces on the ground (10 s settle), and ordered to a goal past it:
    ///  1 goto 301 952;  2 setup (companion, open ground);
    ///  3 low wall (stone_wall_1x1 row across the way): out within 15 s, no teleport;
    ///  4 fence (woodwall U, open behind): out within 15 s, no teleport (step back + re-path);
    ///  5 pit (woodwall box all round): out within 15 s, no teleport (vault / climb);
    ///  6 ledge (goal on a 2 m stone_floor_2x2 stack): up within 15 s, no teleport;
    ///  7 pace: a 40 m order trip logs a [Move] change to jog / sprint;
    ///  8 SHOT.
    /// Each case lists the [Unstuck] rungs it used. A prefab that is missing SKIPs its case. Cleanup: pieces removed, the companion back where
    /// it was, the flag off, the HUD back.
    /// </summary>
    internal static class CompanionUnstuckTest
    {
        private const string Command = "core_companion_unstuck_test", Tag = "[UnstuckTest] ", Owner = "UnstuckTest";
        private const int Steps = 8;
        private const float CaseSeconds = 15f;
        private static bool s_running;
        private static readonly ManualLogSource s_log = BepInEx.Logging.Logger.CreateLogSource("FiresUnifiedCore");
        private static readonly AccessTools.FieldRef<Hud, bool> s_hudHidden = AccessTools.FieldRefAccess<Hud, bool>("m_userHidden");

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class CompanionUnstuckTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] core_companion_unstuck_test - a companion behind a low wall, in a fence, in a pit and below a ledge gets out with its own moves (jump, double jump, vault, climb, re-path) within 15 s, and paces walk / jog by the trip (admin, Test / SuperFlat worlds)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null) { Say(args.Context, Tag + "no local player"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        Say(args.Context, $"{Tag}world '{world}' accepted ({kind})");
                        var host = new GameObject("FiresUnstuckTest");
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
                if (s == null || s.StartsWith(Tag) || !(s.Contains("[Unstuck]") || s.Contains("[Move]"))) return;
                lock (m_lines) m_lines.Add(System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " "));
            }
            internal int Mark { get { lock (m_lines) return m_lines.Count; } }
            internal List<string> Since(int mark, string has) { lock (m_lines) return m_lines.Skip(mark).Where(l => l.Contains(has)).ToList(); }
        }

        private static float Ground(Vector3 p) => ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(p) : p.y;

        private static GameObject Place(List<GameObject> built, string prefab, Vector3 at, Quaternion rot)
        {
            var go = ZNetScene.instance?.GetPrefab(prefab);
            if (go == null) return null;
            var inst = UnityEngine.Object.Instantiate(go, at, rot);
            if (inst != null) built.Add(inst);
            return inst;
        }

        private static void Clear(List<GameObject> built)
        {
            foreach (var go in built) { try { if (go != null) { var w = go.GetComponent<WearNTear>(); if (w != null) w.Remove(); else UnityEngine.Object.Destroy(go); } } catch { } }
            built.Clear();
        }

        // order the companion to goal (its own pathfinding movement, PlayerCommand authority) until within reach or the time is up
        private static IEnumerator Order(CompanionController c, Vector3 goal, float seconds, float reach, Action<bool, float> done)
        {
            var ai = c != null ? c.GetCompanionAI() : null;
            float start = Time.time; bool ok = false;
            while (ai != null && c != null && Time.time - start < seconds)
            {
                ai.RequestPathfindingMovement(goal, false, 1.0f, FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.PlayerCommand, Owner);
                if (Utils.DistanceXZ(c.transform.position, goal) <= reach && Mathf.Abs(c.transform.position.y - goal.y) < 0.8f) { ok = true; break; }
                yield return null;
            }
            try { c?.GetMovementAuthority()?.ReleaseAuthority(Owner); ai?.StopMoving(); } catch { }
            done(ok, Time.time - start);
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
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: {Steps} steps, ~240 s");
            var built = new List<GameObject>();
            bool hudWas = Hud.instance != null && s_hudHidden(Hud.instance);
            CompanionAI.s_pathingTestOn = true;
            // 0.2.937 (Fire 10-27: "our tests are turning off our HUD"; Fire 10-14: tests never hide it): the HUD is never touched

            // 1 goto
            try
            {
                var p = Player.m_localPlayer;
                Vector3 spot = new Vector3(301f, 50f, 952f); spot.y = Ground(spot) + 0.5f;
                p.TeleportTo(spot, p.transform.rotation, true);
            }
            catch (Exception e) { Say(ctx, $"{Tag}goto threw {e.Message}"); }
            float until = Time.realtimeSinceStartup + 40f;
            while (Time.realtimeSinceStartup < until && Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting()) yield return null;
            yield return new WaitForSecondsRealtime(5f);
            var pl = Player.m_localPlayer;
            float off = pl != null ? Utils.DistanceXZ(pl.transform.position, new Vector3(301f, 0f, 952f)) : 999f;
            Result("goto 301 952", off < 15f, $"player {off:0.0} m from the spot");

            // 2 setup: the nearest homestead companion; open ground 25 m east
            long me = pl != null ? pl.GetPlayerID() : 0;
            var c = CompanionController.AllCompanions.Where(x => x != null && x.isTamed && x.ownerPlayerId == me && x.GetCompanionAI() != null)
                .OrderBy(x => pl != null ? Vector3.Distance(x.transform.position, pl.transform.position) : 0f).FirstOrDefault();
            Vector3 home = c != null ? c.transform.position : Vector3.zero;
            // 0.2.930 (round #34: the fixed spot 25 m east was Fire's hand-placed ore field): the first clear, level spot on rings 25-60 m out
            // (12 bearings each; nothing solid within 6 m, ground within 0.5 m across 4 m), logged with what blocked the ones before it
            Vector3 area = Vector3.zero; bool open = false; int tried = 0; string firstBlock = null;
            int mask = LayerMask.GetMask("piece", "static_solid", "Default", "Default_small");
            if (pl != null)
                for (float r = 25f; r <= 60f && !open; r += 7f)
                    for (int k = 0; k < 12 && !open; k++)
                    {
                        float a = k * Mathf.PI / 6f;
                        Vector3 p = pl.transform.position + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r; p.y = Ground(p);
                        tried++;
                        var hit = Physics.OverlapSphere(p + Vector3.up * 1.5f, 6f, mask, QueryTriggerInteraction.Ignore).FirstOrDefault(col => col != null && col.GetComponentInParent<Character>() == null);
                        if (hit != null) { if (firstBlock == null) firstBlock = Utils.GetPrefabName(hit.transform.root.gameObject); continue; }
                        float g1 = Ground(p + Vector3.right * 4f), g2 = Ground(p + Vector3.forward * 4f), g3 = Ground(p - Vector3.right * 4f), g4 = Ground(p - Vector3.forward * 4f);
                        if (Mathf.Max(Mathf.Abs(g1 - p.y), Mathf.Abs(g2 - p.y), Mathf.Abs(g3 - p.y), Mathf.Abs(g4 - p.y)) > 0.5f) continue;
                        area = p; open = true;
                    }
            Result("setup", c != null && open, $"companion {c?.companionName ?? "NONE"} at {home:F1}; trap area {(open ? $"{area:F1} (clear and level; {tried} spot(s) tried 25-60 m out)" : $"NONE clear in {tried} spot(s) 25-60 m out (first block: {firstBlock ?? "slope"})")}");

            IEnumerator Case(string name, Func<Vector3> build, Vector3 start, Func<Vector3> goalFn)
            {
                if (c == null || !open) { Skip(name, "no setup"); yield break; }
                Vector3 goal = Vector3.zero; bool ok2 = false; float secs = 0f;
                try { goal = build(); } catch (Exception e) { Say(ctx, $"{Tag}{name} build threw {e.Message}"); }
                if (goal == Vector3.zero || built.Count == 0) { Skip(name, "a prefab is missing"); Clear(built); yield break; }
                yield return new WaitForSeconds(10f);   // build-test rule: pieces settle on the ground first
                int broken = built.Count(g => g == null);
                if (broken > 0) { Result(name, false, $"setup: {broken} piece(s) collapsed / gone before the trip"); Clear(built); yield break; }
                c.TeleportToDestination(start);
                yield return new WaitForSeconds(3f);
                int m = cap.Mark;
                yield return Order(c, goalFn != null ? goalFn() : goal, CaseSeconds, 1.5f, (a, s) => { ok2 = a; secs = s; });
                var rungs = cap.Since(m, "[Unstuck]"); bool teleported = rungs.Any(l => l.Contains("tried teleport"));
                string used = string.Join("; ", rungs.Select(l => l.Substring(Math.Max(0, l.IndexOf("tried"))).Trim()).Take(8));
                Result(name, ok2 && !teleported, $"{(ok2 ? "out" : "NOT out")} in {secs:0.0} s{(teleported ? ", TELEPORTED" : "")}; rungs: {(used.Length > 0 ? used : "none (walked out)")}");
                Clear(built);
                yield return new WaitForSeconds(1f);
            }

            Vector3 east = Vector3.right;
            Quaternion faceEast = Quaternion.LookRotation(east);
            Quaternion faceNorth = Quaternion.LookRotation(Vector3.forward);
            Vector3 G(Vector3 p) { p.y = Ground(p); return p; }

            // 3 low wall: a row of 1 m stone across the way, goal 4 m past it
            yield return Case("low wall", () =>
            {
                for (int i = -3; i <= 3; i++) if (Place(built, "stone_wall_1x1", G(area + new Vector3(0f, 0f, i)), faceEast) == null) return Vector3.zero;
                return G(area + east * 4f);
            }, G(area - east * 2f), null);

            // 4 fence: a woodwall U round the companion, open behind it (west); the goal east past the closed side
            yield return Case("fence", () =>
            {
                if (Place(built, "woodwall", G(area + east * 1.2f), faceEast) == null) return Vector3.zero;
                Place(built, "woodwall", G(area + new Vector3(0f, 0f, 1.2f)), faceNorth);
                Place(built, "woodwall", G(area + new Vector3(0f, 0f, -1.2f)), faceNorth);
                return G(area + east * 6f);
            }, G(area), null);

            // 5 pit: woodwalls on all four sides
            yield return Case("pit", () =>
            {
                if (Place(built, "woodwall", G(area + east * 1.2f), faceEast) == null) return Vector3.zero;
                Place(built, "woodwall", G(area - east * 1.2f), faceEast);
                Place(built, "woodwall", G(area + new Vector3(0f, 0f, 1.2f)), faceNorth);
                Place(built, "woodwall", G(area + new Vector3(0f, 0f, -1.2f)), faceNorth);
                return G(area + east * 6f);
            }, G(area), null);

            // 6 ledge: the goal on top of a 2 m stack of stone_floor_2x2 (1 m each)
            Vector3 ledgeTop = Vector3.zero;
            yield return Case("ledge", () =>
            {
                Vector3 b = G(area + east * 3f);
                if (Place(built, "stone_floor_2x2", b, faceEast) == null) return Vector3.zero;
                Place(built, "stone_floor_2x2", b + Vector3.up * 1f, faceEast);
                ledgeTop = b + Vector3.up * 2f;
                return ledgeTop;
            }, G(area - east * 1f), () => ledgeTop);

            // 7 pace: a 40 m order trip
            if (c == null) Skip("pace", "no companion");
            else
            {
                c.TeleportToDestination(G(area - east * 20f));
                yield return new WaitForSeconds(3f);
                int m = cap.Mark; bool ok3 = false; float secs = 0f;
                yield return Order(c, G(area + east * 20f), 25f, 2f, (a, s) => { ok3 = a; secs = s; });
                var moves = cap.Since(m, "[Move]");
                bool sped = moves.Any(l => l.Contains("->jog") || l.Contains("->sprint"));
                Result("pace", sped, $"arrived {ok3} in {secs:0.0} s; {(moves.Count > 0 ? string.Join("; ", moves.Take(4)) : "no [Move] line")}");
            }

            // 8 SHOT
            string got = null; yield return Shot("core_companion_unstuck", x => got = x);
            if (got != null) { shots++; Say(ctx, $"{Tag}SHOT {got}"); }
            Result("shot", got != null, got ?? "no file");

            // cleanup
            Clear(built);
            try { if (c != null) c.TeleportToDestination(home); } catch { }
            CompanionAI.s_pathingTestOn = false;
            if (Hud.instance != null) s_hudHidden(Hud.instance) = hudWas;
            BepInEx.Logging.Logger.Listeners.Remove(cap);
            Say(ctx, $"{Tag}END {(fail == 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip, {pass + fail} compared, {shots} shot(s) in {Time.realtimeSinceStartup - started:0.0} s");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }
    }
}
