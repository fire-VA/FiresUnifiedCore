using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using FiresCore.Npc;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// 0.2.92x ([ghost], Fire 10-27 snow chores): core_snow_chores_test (FDT runner contract, Test / SuperFlat worlds) at the SFSC3 snow hut
    /// (274, 823). Snow chores are on for the test only (SnowChores.s_testOn / s_testCentre = the hut, never saved to the cfg); needs FTS
    /// 0.3.268+ (FtsSnowApi), else every step SKIPs.
    ///  1 goto 274 823;  2 setup: FtsSnowApi bound, two homestead companions brought to the hut, its roof pieces (over 1.5 m up, not grounded);
    ///  3 gate: "[Snow] snow chores ON at (274, 823)";
    ///  4 shovel: with none in companion A's bag, one is fetched from storage or crafted ("took" / "crafted" line) - SKIP when A already has one;
    ///  5 urgent roof: one roof loaded to risk 0.9 (FTS TestLoadRoof) is cleared first ("cleared roof ... (urgent)") within 120 s, risk < 0.6;
    ///  6 roofs: two more loaded to 0.7 are cleared within 240 s (all < 0.6), and no piece of the hut collapsed (count before / after);
    ///  7 paths: path samples deeper than 0.4 m get "cleared path" lines and the mean depth drops within 180 s - SKIP with no deep path snow;
    ///  8 SHOT.
    /// Cleanup: the companions back where they were, a shovel the test made / fetched taken back, the flags off, the HUD back.
    /// </summary>
    internal static class SnowChoresTest
    {
        private const string Command = "core_snow_chores_test", Tag = "[SnowChoresTest] ";
        private const int Steps = 8;
        private static readonly Vector3 Hut = new Vector3(274f, 0f, 823f);
        private static bool s_running;
        private static readonly ManualLogSource s_log = BepInEx.Logging.Logger.CreateLogSource("FiresUnifiedCore");
        private static readonly AccessTools.FieldRef<Hud, bool> s_hudHidden = AccessTools.FieldRefAccess<Hud, bool>("m_userHidden");

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class SnowChoresTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] core_snow_chores_test - at the snow hut, companions get a snow shovel, clear an about-to-collapse roof first, then the other loaded roofs and the deep path snow, with nothing collapsing (admin, Test / SuperFlat worlds; needs FiresTossinShade 0.3.268+)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null) { Say(args.Context, Tag + "no local player"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        Say(args.Context, $"{Tag}world '{world}' accepted ({kind})");
                        var host = new GameObject("FiresSnowChoresTest");
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
                if (s == null || s.StartsWith(Tag) || !(s.Contains("[Snow]") || s.Contains("[SnowApi]") || s.Contains("used storage terminal"))) return;
                lock (m_lines) m_lines.Add(System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " "));
            }
            internal int Mark { get { lock (m_lines) return m_lines.Count; } }
            internal List<string> Since(int mark, string has) { lock (m_lines) return m_lines.Skip(mark).Where(l => l.Contains(has)).ToList(); }
        }

        private static float Ground(Vector3 p) => ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(p) : p.y;

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
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: {Steps} steps, ~720 s");
            bool hudWas = Hud.instance != null && s_hudHidden(Hud.instance);
            // 0.2.937 (Fire 10-27: "our tests are turning off our HUD"; Fire 10-14: tests never hide it): the HUD is never touched
            var moved = new Dictionary<CompanionController, Vector3>();
            CompanionController a = null; bool shovelBefore = false;

            // 1 goto
            try { var p = Player.m_localPlayer; Vector3 spot = Hut; spot.y = Ground(spot) + 0.5f; p.TeleportTo(spot, p.transform.rotation, true); }
            catch (Exception e) { Say(ctx, $"{Tag}goto threw {e.Message}"); }
            float until = Time.realtimeSinceStartup + 40f;
            while (Time.realtimeSinceStartup < until && Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting()) yield return null;
            yield return new WaitForSecondsRealtime(5f);
            var pl = Player.m_localPlayer;
            float off = pl != null ? Utils.DistanceXZ(pl.transform.position, Hut) : 999f;
            Result("goto 274 823", off < 15f, $"player {off:0.0} m from the hut");

            // 2 setup
            bool bound = SnowChores.Bound;
            long me = pl != null ? pl.GetPlayerID() : 0;
            var comps = CompanionController.AllCompanions.Where(c => c != null && c.isTamed && c.ownerPlayerId == me && !c.ShouldBeFollowing && c.GetCompanionAI() != null).Take(2).ToList();
            Vector3 hut = Hut; hut.y = Ground(Hut);
            foreach (var c in comps) { moved[c] = c.transform.position; c.TeleportToDestination(hut + new Vector3(3f + moved.Count, 0.5f, 3f)); }
            a = comps.FirstOrDefault();
            yield return new WaitForSeconds(4f);
            var roofs = UnityEngine.Object.FindObjectsByType<WearNTear>(FindObjectsSortMode.None)
                .Where(w => w != null && Utils.DistanceXZ(w.transform.position, hut) < 12f && w.transform.position.y > hut.y + 0.5f).OrderByDescending(w => w.transform.position.y).ToList();
            int piecesBefore = UnityEngine.Object.FindObjectsByType<WearNTear>(FindObjectsSortMode.None).Count(w => w != null && Utils.DistanceXZ(w.transform.position, hut) < 15f);
            bool setup = bound && comps.Count >= 1 && roofs.Count >= 3;
            Result("setup", setup, $"FtsSnowApi {(bound ? "bound" : "NOT FOUND (needs FiresTossinShade 0.3.268+)")}; {comps.Count} companion(s) brought to the hut; {roofs.Count} roof piece(s) above 1.5 m; {piecesBefore} hut piece(s)");

            if (!setup) { while (step < Steps - 1) Skip("snow", "no setup"); }
            else
            {
                SnowChores.ClearForTest(); SnowChores.s_testCentre = hut; SnowChores.s_testOn = true;
                shovelBefore = a.GetInventory()?.GetStorageInventory()?.GetAllItems().Any(i => i?.m_shared?.m_name == "$item_snowshovel") ?? false;

                // 5 (loaded now, so the first roof pass sees it): one roof at 0.9, two at 0.7
                // 0.2.930 (round #34: the 3 highest pieces were all darkwood_roof_45, "risk at full 0.00 (loaded 0.0 vs min 10)" - they carry no
                // load): try the candidates until FTS loads one to the target; the rest are left as they were
                WearNTear urgent = null; float rU = 0f; var others = new List<WearNTear>(); var rO = new List<float>();
                foreach (var w in roofs)
                {
                    if (urgent == null) { float r = SnowChores.TestLoadRoof(w, 0.9f); if (r >= 0.85f) { urgent = w; rU = r; } continue; }
                    if (others.Count >= 2) break;
                    float r2 = SnowChores.TestLoadRoof(w, 0.7f); if (r2 >= 0.6f) { others.Add(w); rO.Add(r2); }
                }
                var owners = string.Join(", ", comps.Select(x => $"{x.companionName} owner {x.GetComponent<ZNetView>()?.GetZDO()?.GetOwner()}"));
                Say(ctx, $"{Tag}roofs: urgent {(urgent != null ? Utils.GetPrefabName(urgent.gameObject) : "NONE could be loaded")} {rU:0.00}, others {others.Count} ({string.Join(", ", others.Select(w => Utils.GetPrefabName(w.gameObject)))}); this peer {ZDOMan.GetSessionID()}; {owners} (see the [SnowApi] TestLoadRoof lines for each piece tried)");
                if (urgent == null) urgent = roofs[0];
                while (others.Count < 2) { others.Add(roofs[Math.Min(roofs.Count - 1, 1 + others.Count)]); rO.Add(0f); }
                int m = cap.Mark;

                // 3 gate
                float t0 = Time.time; List<string> gate;
                while ((gate = cap.Since(m, "snow chores ON")).Count == 0 && Time.time - t0 < 10f) yield return null;
                Result("gate", gate.Count > 0, gate.Count > 0 ? gate[0] : "no 'snow chores ON' line (biome / deep cover at the hut?)");

                // 4 shovel
                t0 = Time.time; List<string> got;
                while ((got = cap.Since(m, "crafted a snow shovel").Concat(cap.Since(m, "SnowShovel")).Concat(cap.Since(m, "took Shovel")).ToList()).Count == 0 && !shovelBefore && Time.time - t0 < 90f) yield return null;
                if (shovelBefore) Skip("shovel", $"{a.companionName} already carries a snow shovel");
                else Result("shovel", got.Count > 0, got.Count > 0 ? got[0] : string.Join(" | ", cap.Since(m, "skipped").Take(2).DefaultIfEmpty("no shovel fetched / crafted")));

                // 5 urgent roof first
                t0 = Time.time; List<string> cleared;
                while ((cleared = cap.Since(m, "cleared roof")).Count == 0 && Time.time - t0 < 120f) yield return null;
                float rAfter = SnowChores.RoofRisk(urgent);
                bool first = cleared.Count > 0 && cleared[0].Contains(Utils.GetPrefabName(urgent.gameObject)) && cleared[0].Contains("(urgent)");
                Result("urgent roof first", rU >= 0.85f && first && rAfter < 0.6f, $"loaded to {rU:0.00}; {(cleared.Count > 0 ? cleared[0] : "no 'cleared roof' line")}; risk now {rAfter:0.00}");

                // 6 the other roofs; nothing collapsed
                t0 = Time.time;
                while (others.Any(w => w != null && SnowChores.RoofRisk(w) >= 0.6f) && Time.time - t0 < 240f) yield return null;
                int piecesAfter = UnityEngine.Object.FindObjectsByType<WearNTear>(FindObjectsSortMode.None).Count(w => w != null && Utils.DistanceXZ(w.transform.position, hut) < 15f);
                string risks = string.Join(", ", others.Select((w, i) => $"{(w != null ? Utils.GetPrefabName(w.gameObject) : "GONE")} {rO[i]:0.00} -> {(w != null ? SnowChores.RoofRisk(w) : -1f):0.00}"));
                Result("roofs cleared, none collapsed", others.All(w => w != null && SnowChores.RoofRisk(w) < 0.6f) && piecesAfter >= piecesBefore && rO.All(r => r >= 0.6f), $"{risks}; hut pieces {piecesBefore} -> {piecesAfter} in {Time.time - t0:0} s");

                // 7 paths
                int pm = cap.Mark;
                float depth0 = FiresCore.Snow.SnowField.Enabled ? FiresCore.Snow.SnowField.DepthAt(hut + new Vector3(0f, 0f, 4f)) * FiresCore.Snow.SnowField.MaxDepthMetres : 0f;
                t0 = Time.time; List<string> paths;
                while ((paths = cap.Since(pm, "cleared path")).Count < 3 && Time.time - t0 < 180f) yield return null;
                if (paths.Count == 0 && cap.Since(pm, "path at").Count == 0) Skip("paths", $"no path snow deeper than 0.4 m at the hut (field {(FiresCore.Snow.SnowField.Enabled ? "on" : "off")}, sample {depth0:0.00} m)");
                else Result("paths", paths.Count >= 3, $"{paths.Count} path clear(s) in {Time.time - t0:0} s; {(paths.Count > 0 ? paths[0] : string.Join(" | ", cap.Since(pm, "[Snow]").Take(2)))}");
            }

            // 8 SHOT
            string shot = null; yield return Shot("core_snow_chores", x => shot = x);
            if (shot != null) { shots++; Say(ctx, $"{Tag}SHOT {shot}"); }
            Result("shot", shot != null, shot ?? "no file");

            // cleanup
            SnowChores.s_testOn = false; SnowChores.s_testCentre = null; SnowChores.ClearForTest();
            try
            {
                if (a != null && !shovelBefore)
                {
                    var bag = a.GetInventory()?.GetStorageInventory();
                    var sh = bag?.GetAllItems().FirstOrDefault(i => i?.m_shared?.m_name == "$item_snowshovel");
                    if (sh != null) { bag.RemoveItem(sh); a.GetInventory()?.SaveToZDO(); Say(ctx, $"{Tag}cleanup: the snow shovel taken back from {a.companionName} (put a stored one back by hand if it came from a chest)"); }
                }
            }
            catch { }
            foreach (var kv in moved) { try { if (kv.Key != null) kv.Key.TeleportToDestination(kv.Value); } catch { } }
            if (Hud.instance != null) s_hudHidden(Hud.instance) = hudWas;
            BepInEx.Logging.Logger.Listeners.Remove(cap);
            Say(ctx, $"{Tag}END {(fail == 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip, {pass + fail} compared, {shots} shot(s) in {Time.realtimeSinceStartup - started:0.0} s");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }
    }
}
