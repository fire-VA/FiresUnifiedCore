using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using FiresCore.Npc;
using FiresCore.Npc.JobBoard;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// 0.2.879 ([ghost], lead 10-27: "taming + area orders are untested in a round"): homestead_orders_test, FDT runner contract, Test / SuperFlat
    /// worlds (HomeMapTest.IsTestWorld). Steps:
    ///  1 goto 301 952;
    ///  2 a WORK area order (the map's "Work here", server-applied) on the nearest ore deposit within 60 m of home (else 10 m off home), r 15 m:
    ///    "[HomeMap] apply area order" within 10 s, the companion's home moved into the circle, then a "[Chore] pick" of that companion within
    ///    90 s (named in the detail);
    ///  3 a HUNT order (the map's "Hunt here": a Now hunting-trip board job) on the nearest game within 120 m (else home), r 30 m: the board job
    ///    made, then the trip started ("[Trip] <name>: hunt ...") within 90 s;
    ///  4 TAMING: SKIP when no wild tameable is within 60 m of home; else a "taming: picked" / "[Chore] tame" line within 120 s (FAIL names the
    ///    last "taming: skipped" reason).
    /// Cleanup: the work companion is sent back to its old stay point; the test's hunt job is removed from the board.
    /// Lines: "[HomesteadOrdersTest] BEGIN ...", "n/4 <step> PASS|FAIL|SKIP - detail", "END PASS|FAIL: p pass, f fail, s skip, c compared".
    /// </summary>
    internal static class HomesteadOrdersTest
    {
        private const string Command = "homestead_orders_test", Tag = "[HomesteadOrdersTest] ";
        private const int Steps = 4;
        private static bool s_running;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class HomesteadOrdersTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] homestead_orders_test - map area orders (work / hunt) and companion taming at the homestead (admin, Test / SuperFlat worlds)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null) { Say(args.Context, Tag + "no local player"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        Say(args.Context, $"{Tag}world '{world}' accepted ({kind})");
                        var host = new GameObject("FiresHomesteadOrdersTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Guard(args.Context, host));   // 0.2.883: guarded
                    }, isCheat: true);
            }
        }

        private sealed class Runner : MonoBehaviour { }

        private static void Say(Terminal ctx, string line) { ctx?.AddString(line); FiresCore.Logging.FiresLogger.LogInfo(line); }

        private sealed class Capture : ILogListener
        {
            private readonly List<string> m_lines = new List<string>();
            public LogLevel LogLevelFilter => LogLevel.All;
            public void Dispose() { }
            public void LogEvent(object sender, LogEventArgs e)
            {
                string s = e?.Data?.ToString();
                if (s == null || s.StartsWith(Tag)) return;
                if (s.Contains("[HomeMap]") || s.Contains("[Chore]") || s.Contains("[Trip]")) lock (m_lines) m_lines.Add(System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " "));   // 0.2.882: the logger's wrap indent collapsed
            }
            internal string Find(Func<string, bool> f) { lock (m_lines) return m_lines.LastOrDefault(f); }
            internal List<string> All() { lock (m_lines) return new List<string>(m_lines); }
            internal void Clear() { lock (m_lines) m_lines.Clear(); }
        }

        // the highest "progress N%" in the captured "[Chore] tame" lines
        private static float BestProgress(Capture cap)
        {
            float best = 0f;
            foreach (var l in cap.All())
            {
                if (!l.Contains("[Chore] tame ")) continue;
                var m = System.Text.RegularExpressions.Regex.Match(l, @"progress (\d+)%");
                if (m.Success && float.TryParse(m.Groups[1].Value, out float p)) best = Mathf.Max(best, p);
            }
            return best;
        }

        private static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }
        private static string Short(string s) => s == null ? null : (s.Length > 220 ? s.Substring(0, 220) + "..." : s);

        private static IEnumerator WaitFor(Capture cap, Func<string, bool> f, float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until && cap.Find(f) == null) yield return null;
        }

        // 0.2.883 (round #9: the test died silently after the boar was killed - a destroyed-object read threw inside the coroutine, so no END
        // and NO cleanup): the test's state lives in these fields; Guard runs Run step by step and, if a step throws, logs "FAIL - threw ...",
        // runs Cleanup anyway and still writes END. The boar, the food, the hunt / tame jobs and the stay point are always put back.
        private static Capture s_cap; private static CompanionController s_workComp, s_feeder; private static Vector3 s_workOldHome;
        private static JobBoard.Job s_testJob, s_tameJob; private static GameObject s_spawnedBoar; private static string s_foodName, s_foodShared;
        private static bool s_cleaned;

        private static IEnumerator Guard(Terminal ctx, GameObject host)
        {
            s_cleaned = false;
            float started = Time.realtimeSinceStartup;
            var inner = Run(ctx, host);
            string threw = null;
            while (true)
            {
                object cur;
                try { if (!inner.MoveNext()) break; cur = inner.Current; }
                catch (Exception e) { threw = e.GetType().Name + ": " + e.Message; break; }
                yield return cur;
            }
            if (threw != null)
            {
                Say(ctx, $"{Tag}FAIL - the test threw {threw}; cleaning up");
                Cleanup();
                Say(ctx, $"{Tag}END FAIL: the test threw, in {Time.realtimeSinceStartup - started:0.0} s (cleanup ran: stay point, test jobs, test boar + food)");
                s_running = false;
                if (host != null) UnityEngine.Object.Destroy(host);
            }
        }

        private static void Cleanup()
        {
            if (s_cleaned) return;
            s_cleaned = true;
            try
            {
                if (s_workComp != null)
                {
                    var nv = s_workComp.GetComponent<ZNetView>();
                    var t = new HomesteadMapActions.Target { Type = "Companion", Name = s_workComp.GetDisplayName(), Pos = s_workComp.transform.position, Go = s_workComp.gameObject, Id = nv != null && nv.IsValid() ? nv.GetZDO().m_uid : ZDOID.None };
                    HomesteadMapActions.TestSendTo(t, s_workOldHome);
                }
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogInfo($"{Tag}cleanup: stay point not restored ({e.Message})"); }
            try { if (s_testJob != null && JobBoard.Jobs.Contains(s_testJob)) JobBoard.Remove(s_testJob); } catch { }
            try { if (s_tameJob != null && JobBoard.Jobs.Contains(s_tameJob)) JobBoard.Remove(s_tameJob); } catch { }
            try
            {
                if (s_spawnedBoar != null)   // Unity null when it died / was destroyed already
                {
                    var bnv = s_spawnedBoar.GetComponent<ZNetView>();
                    if (bnv != null && bnv.IsValid()) { if (!bnv.IsOwner()) bnv.ClaimOwnership(); ZNetScene.instance.Destroy(s_spawnedBoar); }
                    else UnityEngine.Object.Destroy(s_spawnedBoar);
                }
            }
            catch { }
            try
            {
                if (s_feeder != null && s_foodShared != null)
                {
                    var bag = s_feeder.GetComponent<CompanionInventory>()?.GetStorageInventory();
                    if (bag != null) { int left = Mathf.Min(3, bag.CountItems(s_foodShared)); if (left > 0) { bag.RemoveItem(s_foodShared, left); s_feeder.GetComponent<CompanionInventory>()?.TriggerSaveToZDO(); } }
                }
            }
            catch { }
            try { if (s_cap != null) BepInEx.Logging.Logger.Listeners.Remove(s_cap); } catch { }
        }

        private static IEnumerator Run(Terminal ctx, GameObject host)
        {
            s_running = true;
            var cap = new Capture(); s_cap = cap;
            s_workComp = null; s_workOldHome = Vector3.zero; s_testJob = null; s_spawnedBoar = null; s_feeder = null; s_foodName = null; s_foodShared = null; s_tameJob = null;
            BepInEx.Logging.Logger.Listeners.Add(cap);
            int pass = 0, fail = 0, skip = 0, step = 0;
            float started = Time.realtimeSinceStartup;
            void Result(string name, string verdict, string detail) { step++; if (verdict == "PASS") pass++; else if (verdict == "FAIL") fail++; else skip++; Say(ctx, $"{Tag}{step}/{Steps} {name} {verdict} - {detail}"); }
            // 0.2.884 (round #11: FDT scored it "FAIL - 120 (timed out)" although it ended PASS 4/4): FDT's runner reads "BEGIN <mod> <ver> (<hash>)
            // ...: N steps, ~S s" for its timeout (2 x S); "up to ~10 min" was unread, so it got the 60 s default x 2 = 120 s
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: {Steps} steps, ~420 s");

            // 1 goto
            var p = Player.m_localPlayer;
            Vector3 spot = new Vector3(301f, 50f, 952f);
            if (ZoneSystem.instance != null) spot.y = ZoneSystem.instance.GetGroundHeight(spot) + 0.5f;
            p.TeleportTo(spot, p.transform.rotation, true);
            float until = Time.realtimeSinceStartup + 40f;
            while (Time.realtimeSinceStartup < until && p != null && p.IsTeleporting()) yield return null;
            yield return new WaitForSecondsRealtime(5f);
            p = Player.m_localPlayer;
            float off = p != null ? Flat(p.transform.position, spot) : 999f;
            Result("goto 301 952", off < 15f ? "PASS" : "FAIL", $"player {off:0.0} m from the spot");

            long me = p != null ? p.GetPlayerID() : 0L;
            var staying = CompanionController.AllCompanions.Where(c => c != null && c.isTamed && c.ownerPlayerId == me && !c.ShouldBeFollowing).ToList();
            Vector3 home = JobBoard.BaseCenter();

            // 2 work area order on a deposit
            if (staying.Count == 0) Result("work area order", "FAIL", "no staying companion of yours at base");
            else
            {
                s_workComp = staying[0];
                var idle = s_workComp.GetComponent<CompanionIdleBehavior>();
                s_workOldHome = idle != null && idle.HasHomePosition ? idle.HomePosition : s_workComp.transform.position;
                var deposit = Physics.OverlapSphere(home, 60f).Select(c => (Component)c.GetComponentInParent<MineRock5>() ?? c.GetComponentInParent<MineRock>()).Where(x => x != null)
                    .OrderBy(x => Flat(x.transform.position, home)).FirstOrDefault();
                Vector3 c0 = deposit != null ? deposit.transform.position : home + new Vector3(10f, 0f, 0f);
                cap.Clear();
                HomesteadMapActions.TestAreaOrder("Work here (gather / chop / loot)", c0, 15f, s_workComp);
                yield return WaitFor(cap, l => l.Contains("apply area order"), 10f);
                string applied = cap.Find(l => l.Contains("apply area order"));
                yield return new WaitForSecondsRealtime(1f);
                bool moved = idle != null && idle.HasHomePosition && Flat(idle.HomePosition, c0) < 1.5f;
                string name = s_workComp.companionName;
                yield return WaitFor(cap, l => l.Contains("[Chore] pick " + name + ":"), 90f);
                string pick = cap.Find(l => l.Contains("[Chore] pick " + name + ":"));
                Result("work area order", applied != null && moved ? "PASS" : "FAIL",
                    $"{(deposit != null ? Utils.GetPrefabName(deposit.gameObject) : "no deposit, 10 m off home")} at ({c0.x:0}, {c0.z:0}); apply: {Short(applied) ?? "NONE in 10 s"}; home moved: {(moved ? "yes" : "NO")}; next pick: {Short(pick) ?? "none in 90 s"}");
            }

            // 3 hunt order
            var hunter = staying.Skip(1).FirstOrDefault() ?? staying.FirstOrDefault();
            if (hunter == null) Result("hunt order", "FAIL", "no staying companion of yours at base");
            else
            {
                var game = Character.GetAllCharacters().Where(c => c != null && !c.IsDead() && !c.IsPlayer() && !c.IsTamed() && c.GetComponent<CompanionController>() == null && c.m_faction == Character.Faction.AnimalsVeg && Flat(c.transform.position, home) < 120f)
                    .OrderBy(c => Flat(c.transform.position, home)).FirstOrDefault();
                Vector3 hc = game != null ? game.transform.position : home;
                cap.Clear();
                int before = JobBoard.Jobs.Count;
                HomesteadMapActions.TestAreaOrder("Hunt here", hc, 30f, hunter);
                s_testJob = JobBoard.Jobs.Count > before ? JobBoard.Jobs[JobBoard.Jobs.Count - 1] : null;
                string hname = hunter.companionName;
                yield return WaitFor(cap, l => l.Contains("[Trip] " + hname + ": hunt"), 90f);
                string trip = cap.Find(l => l.Contains("[Trip] " + hname + ": hunt"));
                Result("hunt order", s_testJob != null && trip != null ? "PASS" : "FAIL",
                    $"{(game != null ? Utils.GetPrefabName(game.gameObject) : "no game within 120 m, on home")} at ({hc.x:0}, {hc.z:0}); board job: {(s_testJob != null ? "#" + s_testJob.Id : "NOT MADE")}; trip: {Short(trip) ?? "not started in 90 s"}");
            }

            // 4 taming
            var tameable = Character.GetAllCharacters().FirstOrDefault(c => c != null && !c.IsDead() && !c.IsTamed() && c.GetComponent<Tameable>() != null && Flat(c.transform.position, home) < 60f);
            // 0.2.881 (lead, round #6a: taming SKIPped, no wild tameable near): the test brings its own - a vanilla Boar 15 m from home, and 3 of
            // its own food (MonsterAI.m_consumeItems[0]) in one staying companion's bag; both removed at cleanup
            if (tameable == null && staying.Count > 0 && ZNetScene.instance != null)
            {
                var boarPrefab = ZNetScene.instance.GetPrefab("Boar");
                if (boarPrefab != null)
                {
                    Vector3 bp = home + new Vector3(15f, 0f, 0f);
                    if (ZoneSystem.instance != null) bp.y = ZoneSystem.instance.GetGroundHeight(bp) + 0.3f;
                    s_spawnedBoar = UnityEngine.Object.Instantiate(boarPrefab, bp, Quaternion.identity);
                    tameable = s_spawnedBoar.GetComponent<Character>();
                    var food = s_spawnedBoar.GetComponent<MonsterAI>()?.m_consumeItems?.FirstOrDefault(x => x != null);
                    s_feeder = staying[0];
                    var bag = s_feeder.GetComponent<CompanionInventory>()?.GetStorageInventory();
                    if (food != null && bag != null)
                    {
                        s_foodName = food.gameObject.name;
                        s_foodShared = food.m_itemData.m_shared.m_name;
                        if (bag.AddItem(food.gameObject, 3)) s_feeder.GetComponent<CompanionInventory>()?.TriggerSaveToZDO();
                        else s_foodName = null;
                    }
                    Say(ctx, $"{Tag}taming: spawned a Boar at ({bp.x:0}, {bp.z:0}) for the test{(s_foodName != null ? $", 3 {s_foodName} in {s_feeder.companionName}'s bag" : ", no food added")}");
                }
            }
            if (tameable == null) Result("taming", "SKIP", "no wild tameable within 60 m of home and none could be spawned");
            else
            {
                // 0.2.883 (round #9): the name and distance are read NOW (the animal can die mid-step - the hunters killed the test boar), and
                // taming is started the way a player does it - a "Tame animals" Now board job assigned to the feeder - instead of waiting for
                // the idle picker (FireTending 10 outscored Taming 6 every pick, so it never started)
                string tName = Utils.GetPrefabName(tameable.gameObject);
                float tDist = Flat(tameable.transform.position, home);
                if (s_feeder == null) s_feeder = staying.FirstOrDefault();
                if (s_feeder != null)
                {
                    s_tameJob = JobBoard.Add(JobBoard.Kind.Tame, JobBoard.Repeat.Now);
                    JobBoard.Assign(s_tameJob, s_feeder, false);
                    Say(ctx, $"{Tag}taming: board job #{s_tameJob.Id} 'Tame animals' (Now) assigned to {s_feeder.companionName}");
                }
                cap.Clear();
                yield return WaitFor(cap, l => l.Contains("taming: picked") || l.Contains("[Chore] tame "), 120f);
                string got = cap.Find(l => l.Contains("taming: picked") || l.Contains("[Chore] tame "));
                string why = cap.Find(l => l.Contains("taming: skipped"));
                // 0.2.882 (round #7: picked, then "progress 0%, the animal is gone" - the step ended at the pick and cleanup removed the boar):
                // a pick is not a tame. After the pick: a "fed it" line within 180 s, then up to 120 s more for the logged progress to rise
                // above 0 % (vanilla Tameable counts while it is fed; a full tame can take up to 30 min, so the proof is progress, reported in %)
                string fed = null; float best = 0f; bool tamed = false;
                if (got != null)
                {
                    yield return WaitFor(cap, l => l.Contains("[Chore] tame ") && l.Contains("fed it"), 180f);
                    fed = cap.Find(l => l.Contains("[Chore] tame ") && l.Contains("fed it"));
                    float end = Time.realtimeSinceStartup + (fed != null ? 120f : 0f);
                    while (Time.realtimeSinceStartup < end)
                    {
                        best = Mathf.Max(best, BestProgress(cap));
                        tamed = cap.Find(l => l.Contains("TAMED")) != null;
                        if (tamed || best > 0f) break;
                        yield return new WaitForSecondsRealtime(2f);
                    }
                    best = Mathf.Max(best, BestProgress(cap));
                    tamed = tamed || cap.Find(l => l.Contains("TAMED")) != null;
                }
                bool ok = got != null && fed != null && (best > 0f || tamed);
                Result("taming", ok ? "PASS" : "FAIL", $"{tName} {tDist:0} m from home{(tameable == null ? " (it died during the step)" : "")}; " +
                    (got == null ? "no pick in 120 s; last skip: " + (Short(why) ?? "none")
                     : $"pick: {Short(got)}; fed: {(fed != null ? "yes" : "NO feed line in 180 s")}; progress {best:0}%{(tamed ? ", TAMED" : "")}; last: {Short(cap.Find(l => l.Contains("[Chore] tame "))) ?? "none"}"));
            }

            // cleanup (0.2.883: one method, also run by Guard when a step throws)
            Cleanup();
            yield return new WaitForSecondsRealtime(1f);
            int compared = pass + fail;
            Say(ctx, $"{Tag}END {(fail == 0 && compared > 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip, {compared} compared, 0 shot(s) in {Time.realtimeSinceStartup - started:0.0} s (cleanup: stay point restored, test hunt job removed, test boar + food removed)");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }
    }
}
