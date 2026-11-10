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
using FiresCore.Npc.JobBoard;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// 0.2.890 ([ghost], lead 10-27, Fire's work zones + camp replanting): homemap_area_test, FDT runner contract, Test / SuperFlat worlds.
    /// It drives the same entry points the map's mouse handlers call (HomesteadMapCircles.Create = right-drag release, OpenMenu = right-click
    /// on a circle, popup rows clicked as a player would). Steps:
    ///  1 goto 301 952;
    ///  2 circle A (r 15 m) on the nearest tree within 60 m of home: drawn, right-click menu, "Designate as ▸ Logging", "Assign ▸ companion 1":
    ///    a daily "Logging zone #N" board job with that companion (SHOT the circles + menu);
    ///  3 logging run: "[HomeMap] zone run: <c1> works Logging zone", the companion seen INSIDE the circle, the run finished (zone cleared:
    ///    back to the rotation) and the companion back within 30 m of home - within 300 s (SHOT the working companion);
    ///  4 logging replant: 6 tree seeds that grow there are put in the nearest base chest first; PASS on 2+ "[Chore] replant Logging zone ...
    ///    planted ... ok" lines inside the circle (spacing reported);
    ///  5 circle B (r 15 m) on the nearest ore within 60 m of home, "Designate as ▸ Mining", "Assign ▸ companion 2" (2 mineral seeds in its bag):
    ///    the run as in 3 within 300 s;
    ///  6 mining replant: 1+ "[Chore] replant Mining zone ... planted MineralSeed_..." line (SKIP when no mineral seed item exists);
    ///  7 both circles removed via the menu's "Remove circle": circles gone, their board jobs gone (SHOT).
    /// Cleanup: circles + jobs removed (menu path first), the test's seeds taken back, the test's saplings destroyed, map closed.
    /// Lines: "[HomemapAreaTest] BEGIN ...", "n/7 <step> PASS|FAIL|SKIP - detail", "SHOT <path>", "END PASS|FAIL: ...".
    /// </summary>
    internal static class HomemapAreaTest
    {
        private const string Command = "homemap_area_test", Tag = "[HomemapAreaTest] ";
        private const int Steps = 7;
        private const float Radius = 15f, RunWait = 300f;
        private static bool s_running;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class HomemapAreaTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] homemap_area_test - homestead-map work zones (Logging / Mining board jobs, zone runs, camp replanting) (admin, Test / SuperFlat worlds)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null) { Say(args.Context, Tag + "no local player"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        Say(args.Context, $"{Tag}world '{world}' accepted ({kind})");
                        var host = new GameObject("FiresHomemapAreaTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Guard(args.Context, host));
                    }, isCheat: true);
            }
        }

        private sealed class Runner : MonoBehaviour { }

        // 0.2.900 (round #23: FDT's runner read no "~720 s" budget and gave up at 120 s): FiresLogger's Unity Log route wraps long lines
        // ("... () on" / "client: 7 steps, ~720 s"), so the test's own lines go through a BepInEx log source, one line each, as the
        // FTS / FAT tests do
        private static readonly ManualLogSource s_log = BepInEx.Logging.Logger.CreateLogSource("FiresUnifiedCore");
        private static void Say(Terminal ctx, string line) { ctx?.AddString(line); s_log.LogInfo(line); }

        // 0.2.900 (lead): the zone job's own last board line - "not due (now .. last run day ..)", "started on X" or "X can't (why)" -
        // up front in the step detail, so the summary file carries it even when the client log is lost
        private static string BoardWhy(Capture cap, JobBoard.Job j)
        {
            string label = JobBoard.Label(j);
            return Short(cap.Find(l => l.Contains("[JobBoard] " + label) && !l.Contains(": assigned") && !l.Contains("added ")))
                   ?? "no board line for '" + label + "' (never due, never started)";
        }

        private sealed class Capture : ILogListener
        {
            private readonly List<string> m_lines = new List<string>();
            public LogLevel LogLevelFilter => LogLevel.All;
            public void Dispose() { }
            public void LogEvent(object sender, LogEventArgs e)
            {
                string s = e?.Data?.ToString();
                if (s == null || s.StartsWith(Tag)) return;
                if (s.Contains("[HomeMap]") || s.Contains("[Chore]") || s.Contains("[JobBoard]") || s.Contains("ended sub-behavior") || s.Contains("Deposit") || s.Contains("deposit"))
                    lock (m_lines) m_lines.Add(System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " "));   // the logger's wrap indent collapsed
            }
            internal string Find(Func<string, bool> f) { lock (m_lines) return m_lines.LastOrDefault(f); }
            internal List<string> All(Func<string, bool> f) { lock (m_lines) return m_lines.Where(f).ToList(); }
            internal void Clear() { lock (m_lines) m_lines.Clear(); }
        }

        private static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }
        private static string Short(string s) => s == null ? null : (s.Length > 220 ? s.Substring(0, 220) + "..." : s);

        // test state (Cleanup puts it all back, also when a step throws)
        private static Capture s_cap; private static readonly List<HomesteadMapCircles.Circle> s_circles = new List<HomesteadMapCircles.Circle>();
        private static Container s_seedChest; private static string s_treeSeed; private static int s_treeSeedsPut;
        private static CompanionController s_miner; private static string s_mineralSeed; private static int s_mineralPut;
        private static readonly List<string> s_toolsPut = new List<string>();   // lead 10-27: an axe / pickaxe the test put in the chest
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
                Say(ctx, $"{Tag}END FAIL: the test threw, in {Time.realtimeSinceStartup - started:0.0} s (cleanup ran: circles, jobs, seeds, saplings)");
                s_running = false;
                if (host != null) UnityEngine.Object.Destroy(host);
            }
        }

        private static void Cleanup()
        {
            if (s_cleaned) return;
            s_cleaned = true;
            // the test's saplings (from its own replant lines)
            try
            {
                foreach (var l in s_cap != null ? s_cap.All(x => x.Contains("[Chore] replant") && x.Contains(" planted ") && x.Contains(" -> ")) : new List<string>())
                {
                    var m = System.Text.RegularExpressions.Regex.Match(l, @" -> (\S+) at (-?[\d.]+),(-?[\d.]+)");
                    if (!m.Success) continue;
                    var at = new Vector3(float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 0f, float.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
                    foreach (var pl in UnityEngine.Object.FindObjectsByType<Plant>(FindObjectsSortMode.None))
                    {
                        if (pl == null || Utils.GetPrefabName(pl.gameObject) != m.Groups[1].Value || Flat(pl.transform.position, at) > 0.3f) continue;
                        var nv = pl.GetComponent<ZNetView>();
                        if (nv != null && nv.IsValid()) { if (!nv.IsOwner()) nv.ClaimOwnership(); ZNetScene.instance.Destroy(pl.gameObject); }
                    }
                }
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogInfo($"{Tag}cleanup: saplings not removed ({e.Message})"); }
            // circles + their jobs (step 7 removes them via the menu; this is the fallback)
            try { foreach (var c in s_circles) if (HomesteadMapCircles.Circles.Contains(c)) HomesteadMapCircles.Remove(c, "removed by the test cleanup"); } catch { }
            // the test's tools back out: from the chest, else from a companion that took it
            try
            {
                foreach (var tool in s_toolsPut)
                {
                    var pf = ObjectDB.instance?.GetItemPrefab(tool)?.GetComponent<ItemDrop>(); if (pf == null) continue;
                    string sn = pf.m_itemData.m_shared.m_name;
                    var inv = s_seedChest != null ? s_seedChest.GetInventory() : null;
                    if (inv != null && inv.CountItems(sn) > 0) { inv.RemoveItem(sn, 1); continue; }
                    foreach (var comp in CompanionController.AllCompanions)
                    {
                        var bag = comp != null ? comp.GetInventory()?.GetStorageInventory() : null;
                        if (bag != null && bag.CountItems(sn) > 0) { bag.RemoveItem(sn, 1); comp.GetComponent<CompanionInventory>()?.TriggerSaveToZDO(); break; }
                    }
                }
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogInfo($"{Tag}cleanup: tools not taken back ({e.Message})"); }
            // the test's seeds back out (whatever is left of them)
            try
            {
                if (s_seedChest != null && s_treeSeed != null && s_treeSeedsPut > 0)
                {
                    var inv = s_seedChest.GetInventory(); var pf = ObjectDB.instance?.GetItemPrefab(s_treeSeed)?.GetComponent<ItemDrop>();
                    if (inv != null && pf != null) inv.RemoveItem(pf.m_itemData.m_shared.m_name, Mathf.Min(s_treeSeedsPut, inv.CountItems(pf.m_itemData.m_shared.m_name)));
                }
                foreach (var comp in CompanionController.AllCompanions)
                {
                    var bag = comp != null ? comp.GetInventory()?.GetStorageInventory() : null;
                    if (bag == null) continue;
                    foreach (var seed in new[] { s_treeSeed, comp == s_miner ? s_mineralSeed : null })
                    {
                        var pf = seed != null ? ObjectDB.instance?.GetItemPrefab(seed)?.GetComponent<ItemDrop>() : null;
                        if (pf == null) continue;
                        int n = bag.CountItems(pf.m_itemData.m_shared.m_name);
                        if (n > 0) { bag.RemoveItem(pf.m_itemData.m_shared.m_name, n); comp.GetComponent<CompanionInventory>()?.TriggerSaveToZDO(); }
                    }
                }
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogInfo($"{Tag}cleanup: seeds not taken back ({e.Message})"); }
            try { HomesteadMapActions.ClosePopup(true); if (HomesteadMap.TestIsOpen) HomesteadMap.TestClose(); if (Minimap.instance != null) Minimap.instance.SetMapMode(Minimap.MapMode.Small); } catch { }
            try { if (s_cap != null) BepInEx.Logging.Logger.Listeners.Remove(s_cap); } catch { }
        }

        private static Vector2 ScreenPoint(HomesteadMapCircles.Circle c)
        {
            var layer = HomesteadMap.MarkerLayer;
            if (layer == null || !HomesteadMapCircles.ScreenOf(c, out Vector2 gui, out _)) return Vector2.zero;
            return RectTransformUtility.WorldToScreenPoint(null, layer.TransformPoint(gui + layer.rect.min));
        }

        private static IEnumerator Capture2(string name, Action<string> done)
        {
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests", "shots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(path);
            float until = Time.realtimeSinceStartup + 5f;
            long last = -1;
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
            var cap = new Capture(); s_cap = cap;
            s_toolsPut.Clear(); s_circles.Clear(); s_seedChest = null; s_treeSeed = null; s_treeSeedsPut = 0; s_miner = null; s_mineralSeed = null; s_mineralPut = 0;
            BepInEx.Logging.Logger.Listeners.Add(cap);
            int pass = 0, fail = 0, skip = 0, step = 0;
            var shots = new List<string>();
            float started = Time.realtimeSinceStartup;
            void Result(string name, string verdict, string detail) { step++; if (verdict == "PASS") pass++; else if (verdict == "FAIL") fail++; else skip++; Say(ctx, $"{Tag}{step}/{Steps} {name} {verdict} - {detail}"); }
            IEnumerator Shot(string name) { string got = null; yield return Capture2(name, x => got = x); if (got != null) { shots.Add(got); Say(ctx, $"{Tag}SHOT {got}"); } }
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: {Steps} steps, ~720 s");

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
            var c1 = staying.FirstOrDefault(); var c2 = staying.Skip(1).FirstOrDefault() ?? c1; s_miner = c2;

            // tree seeds that grow at the logging spot, into the nearest base chest (4 replant)
            var tree = Physics.OverlapSphere(home, 150f).Select(x => x.GetComponentInParent<TreeBase>()).Where(x => x != null).OrderBy(x => Flat(x.transform.position, home)).FirstOrDefault();   // 0.2.912 (round #30: "Logging zone #1 at (305, 939) on no tree" - the trees near home were felled earlier in the round): 150 m
            Vector3 la = tree != null ? tree.transform.position : home + new Vector3(12f, 0f, 0f);
            s_treeSeed = FarmingDataHelper.GroundSeedItems().Where(s => !s.StartsWith("MineralSeed_"))
                .FirstOrDefault(s => FarmingDataHelper.GroundSaplingsForSeed(s).Any(cs => FarmingDataHelper.CanGrowAt(cs, la)));
            s_seedChest = ChestHelper.FindNearbyChests(home, 30f).OrderBy(x => Flat(x.transform.position, home)).FirstOrDefault();
            if (s_treeSeed != null && s_seedChest != null)
            {
                var pf = ObjectDB.instance.GetItemPrefab(s_treeSeed);
                if (pf != null && s_seedChest.GetInventory().AddItem(pf, 6)) s_treeSeedsPut = 6;
            }
            Say(ctx, $"{Tag}setup: {(s_treeSeedsPut > 0 ? $"6 {s_treeSeed} in the chest at ({s_seedChest.transform.position.x:0}, {s_seedChest.transform.position.z:0})" : $"no tree seeds put (seed {s_treeSeed ?? "none grows here"}, chest {(s_seedChest != null ? "found" : "NONE")})")}");
            // lead 10-27: a zone job is skipped by JobBoard.Missing when the base has no axe / pickaxe - the test brings one (taken back at cleanup)
            foreach (var (kind, tool) in new[] { (JobBoard.Kind.Wood, "AxeStone"), (JobBoard.Kind.MineHome, "PickaxeAntler") })
            {
                var miss = JobBoard.Missing(new JobBoard.Job { Kind = kind });
                if (miss.Count == 0) { Say(ctx, $"{Tag}setup: base already has what {kind} needs"); continue; }
                var tpf = ObjectDB.instance.GetItemPrefab(tool);
                bool put = s_seedChest != null && tpf != null && s_seedChest.GetInventory().AddItem(tpf, 1);
                if (put) s_toolsPut.Add(tool);
                Say(ctx, $"{Tag}setup: {kind} needed {string.Join(", ", miss)} -> {(put ? "put 1 " + tool + " in the chest" : "could NOT put a " + tool + " (no chest)")}");
            }

            // 2 circle A: Logging, companion 1
            JobBoard.Job jobA = null; HomesteadMapCircles.Circle ca = null;
            if (tree == null) { Result("logging zone made", "SKIP", "no tree within 150 m of home to put a logging zone on"); }   // 0.2.912
            else if (c1 == null) Result("logging zone made", "FAIL", "no staying companion of yours at base");
            else
            {
                HomesteadMap.Open(); yield return new WaitForSecondsRealtime(2.5f);
                cap.Clear();
                ca = HomesteadMapCircles.Create(la, Radius); s_circles.Add(ca);
                yield return new WaitForSecondsRealtime(1f);
                HomesteadMapCircles.OpenMenu(ca, ScreenPoint(ca)); yield return new WaitForSecondsRealtime(0.8f);
                bool d1 = HomesteadMapActions.TestClickRow("Designate as"); yield return new WaitForSecondsRealtime(0.5f);
                yield return Shot("homemap_area_menu");
                bool d2 = d1 && HomesteadMapActions.TestClickSubRow("Logging"); yield return new WaitForSecondsRealtime(0.8f);
                HomesteadMapCircles.OpenMenu(ca, ScreenPoint(ca)); yield return new WaitForSecondsRealtime(0.8f);
                bool a1 = HomesteadMapActions.TestClickRow("Assign"); yield return new WaitForSecondsRealtime(0.5f);
                bool a2 = a1 && HomesteadMapActions.TestClickSubRow(c1.GetDisplayName()); yield return new WaitForSecondsRealtime(0.8f);
                jobA = HomesteadMapCircles.JobOf(ca);
                bool ok = jobA != null && jobA.ZoneFocus == "chop" && jobA.Repeat == JobBoard.Repeat.Daily && jobA.Assigned.Contains(c1.companionId);
                Result("logging zone made", ok ? "PASS" : "FAIL", $"circle #{ca.Id} at ({la.x:0}, {la.z:0}) on {(tree != null ? Utils.GetPrefabName(tree.gameObject) : "no tree, 12 m off home")}; menu rows: designate {d1}/{d2}, assign {a1}/{a2}; board job: {(jobA != null ? $"#{jobA.Id} '{JobBoard.Label(jobA)}' {JobBoard.Schedule(jobA)} {JobBoard.AssignedText(jobA)}" : "NONE")}");
                yield return Shot("homemap_area_circles");
            }

            // 3-4 logging run + replant
            if (jobA == null) { string v = tree == null ? "SKIP" : "FAIL"; Result("logging run", v, tree == null ? "no tree to log" : "no logging job"); Result("logging replant", v, tree == null ? "no tree to log" : "no logging job"); }   // 0.2.912
            else
            {
                string[] outA = null;
                yield return ZoneRunInto(cap, c1, ca, home, x => outA = x);
                bool okA = outA[0] != null && outA[1] == "yes" && outA[2] != null && outA[3] == "yes";
                Result("logging run", okA ? "PASS" : "FAIL", $"board: {BoardWhy(cap, jobA)}; works: {Short(outA[0]) ?? "NONE in " + RunWait + " s"}; inside the circle: {outA[1]}; finished: {Short(outA[2]) ?? "NO"}; back home: {outA[3]}; deposit: {Short(outA[4]) ?? "no deposit line"}{(outA[5] != null ? "; " + outA[5] : "")}");
                var planted = cap.All(l => l.Contains("[Chore] replant " + JobBoard.Label(jobA)) && l.Contains(" planted ") && l.EndsWith("ok)"));
                if (s_treeSeedsPut == 0) Result("logging replant", "SKIP", "no tree seed grows at the spot / no chest at base");
                else { int summed = cap.All(l => l.Contains("[Chore] replant " + JobBoard.Label(jobA)) && l.Contains("run summary")).Select(l => System.Text.RegularExpressions.Regex.Match(l, @"(\d+) sapling\(s\) planted")).Where(m => m.Success).Sum(m => int.Parse(m.Groups[1].Value)); Result("logging replant", planted.Count >= 1 || summed >= 2 ? "PASS" : "FAIL", $"{planted.Count} planted ok, {summed} planted by the run summaries (0.2.916: 1+ ok or 2+ planted); {string.Join(" | ", planted.Take(3).Select(Short))}; last: {Short(cap.Find(l => l.Contains("[Chore] replant"))) ?? "no replant line"}"); }
                yield return Shot("homemap_area_logging");
            }

            // 5-6 circle B: Mining, companion 2 with mineral seeds
            var ore = Physics.OverlapSphere(home, 60f).Select(x => (Component)x.GetComponentInParent<MineRock5>() ?? x.GetComponentInParent<MineRock>()).Where(x => x != null)
                .OrderBy(x => Flat(x.transform.position, home)).FirstOrDefault();
            Vector3 ma = ore != null ? ore.transform.position : home + new Vector3(-12f, 0f, 0f);
            s_mineralSeed = FarmingDataHelper.GroundSeedItems().FirstOrDefault(s => s.StartsWith("MineralSeed_Copper")) ?? FarmingDataHelper.GroundSeedItems().FirstOrDefault(s => s.StartsWith("MineralSeed_"));
            if (c2 != null && s_mineralSeed != null)
            {
                var bag = c2.GetInventory()?.GetStorageInventory(); var pf = ObjectDB.instance.GetItemPrefab(s_mineralSeed);
                if (bag != null && pf != null && bag.AddItem(pf, 2)) { s_mineralPut = 2; c2.GetComponent<CompanionInventory>()?.TriggerSaveToZDO(); }
            }
            JobBoard.Job jobB = null; HomesteadMapCircles.Circle cb = null; string assignB = "assign not tried";
            if (c2 == null) { Result("mining run", "FAIL", "no staying companion of yours at base"); Result("mining replant", "FAIL", "no companion"); }
            else
            {
                cap.Clear();
                cb = HomesteadMapCircles.Create(ma, Radius); s_circles.Add(cb);
                yield return new WaitForSecondsRealtime(1f);
                HomesteadMapCircles.OpenMenu(cb, ScreenPoint(cb)); yield return new WaitForSecondsRealtime(0.8f);
                if (HomesteadMapActions.TestClickRow("Designate as")) { yield return new WaitForSecondsRealtime(0.5f); HomesteadMapActions.TestClickSubRow("Mining"); }
                yield return new WaitForSecondsRealtime(0.8f);
                HomesteadMapCircles.OpenMenu(cb, ScreenPoint(cb)); yield return new WaitForSecondsRealtime(0.8f);
                bool aB1 = HomesteadMapActions.TestClickRow("Assign"); bool aB2 = false; if (aB1) { yield return new WaitForSecondsRealtime(0.5f); aB2 = HomesteadMapActions.TestClickSubRow(c2.GetDisplayName()); } assignB = $"assign {c2.GetDisplayName()}: menu {aB1}, row {aB2}";   // 0.2.902: the click result goes in the detail
                yield return new WaitForSecondsRealtime(0.8f);
                jobB = HomesteadMapCircles.JobOf(cb);
                string[] outB = new string[6];
                if (jobB != null) yield return ZoneRunInto(cap, c2, cb, home, x => outB = x);
                bool okB = jobB != null && jobB.ZoneFocus == "mine" && outB[0] != null && outB[1] == "yes" && outB[2] != null && outB[3] == "yes";
                Result("mining run", okB ? "PASS" : "FAIL", $"board: {(jobB != null ? BoardWhy(cap, jobB) : "no job")}; {assignB}; circle #{cb.Id} on {(ore != null ? Utils.GetPrefabName(ore.gameObject) : "no ore, 12 m off home")}; job: {(jobB != null ? $"#{jobB.Id} '{JobBoard.Label(jobB)}' {JobBoard.AssignedText(jobB)}" : "NONE")}; works: {Short(outB[0]) ?? "NONE"}; inside: {outB[1] ?? "no"}; finished: {Short(outB[2]) ?? "NO"}; back home: {outB[3] ?? "no"}; deposit: {Short(outB[4]) ?? "no deposit line"}{(outB[5] != null ? "; " + outB[5] : "")}");
                var mplant = cap.All(l => l.Contains("[Chore] replant Mining zone") && l.Contains(" planted MineralSeed_"));
                if (s_mineralPut == 0) Result("mining replant", "SKIP", "no mineral seed item in this game (MineralSeeds off?)");
                else Result("mining replant", mplant.Count >= 1 ? "PASS" : "FAIL", $"{mplant.Count} planted; {string.Join(" | ", mplant.Take(2).Select(Short))}; last: {Short(cap.Find(l => l.Contains("[Chore] replant"))) ?? "no replant line"}");
            }

            // 7 remove both circles via the menu
            int gone = 0; var jobIds = s_circles.Select(c => c.JobId).Where(id => id != 0).ToList();
            foreach (var c in s_circles.ToList())
            {
                HomesteadMapCircles.OpenMenu(c, ScreenPoint(c)); yield return new WaitForSecondsRealtime(0.8f);
                if (HomesteadMapActions.TestClickRow("Remove circle")) gone++;
                yield return new WaitForSecondsRealtime(0.8f);
            }
            bool circlesGone = s_circles.All(c => !HomesteadMapCircles.Circles.Contains(c));
            bool jobsGone = jobIds.All(id => JobBoard.Jobs.All(j => j.Id != id));
            Result("remove circles via menu", circlesGone && jobsGone && gone == s_circles.Count ? "PASS" : "FAIL", $"{gone}/{s_circles.Count} 'Remove circle' clicked; circles gone: {circlesGone}; board jobs gone: {jobsGone}");
            yield return Shot("homemap_area_removed");

            Cleanup();
            yield return new WaitForSecondsRealtime(1f);
            int compared = pass + fail;
            Say(ctx, $"{Tag}END {(fail == 0 && compared > 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip, {compared} compared, {shots.Count} shot(s) in {Time.realtimeSinceStartup - started:0.0} s (cleanup: circles + jobs, test seeds, test saplings, map closed)");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }

        /// <summary>Waits for one zone run of the companion: [works line, inside yes/no, finished line, back home yes/no, deposit line].</summary>
        private static IEnumerator ZoneRunInto(Capture cap, CompanionController comp, HomesteadMapCircles.Circle c, Vector3 home, Action<string[]> done)
        {
            var r = new string[6]; r[1] = "no"; r[3] = "no";
            string name = comp.GetDisplayName();
            float until = Time.realtimeSinceStartup + RunWait;
            while (Time.realtimeSinceStartup < until)
            {
                if (comp == null) break;
                r[0] = r[0] ?? cap.Find(l => l.Contains("[HomeMap] zone run: " + name + " works"));
                if (r[0] != null && Flat(comp.transform.position, c.Center) <= c.Radius + 2f) r[1] = "yes";
                r[2] = r[2] ?? (r[0] != null ? cap.Find(l => l.Contains("[HomeMap] zone run: " + name + " finished") && !l.Contains("refused")) : null);   // 0.2.912: a refused run is not a finished run
                if (r[2] != null) break;
                yield return new WaitForSecondsRealtime(1f);
            }
            if (r[2] != null)
            {
                float back = Time.realtimeSinceStartup + 60f;
                while (Time.realtimeSinceStartup < back && comp != null && Flat(comp.transform.position, home) > 30f) yield return new WaitForSecondsRealtime(1f);
                r[3] = comp != null && Flat(comp.transform.position, home) <= 30f ? "yes" : "no";
            }
            r[4] = cap.Find(l => (l.Contains("Deposit") || l.Contains("deposit")) && l.Contains(comp != null ? comp.companionName : "?"));
            // lead 10-27: when the run did not finish, name the bail branch - what it is doing now and the last lines that say why
            if (r[2] == null && comp != null)
            {
                var idle = comp.GetComponent<CompanionIdleBehavior>();
                var act = idle != null ? idle.ActiveSubBehavior : null;
                string cn = comp.companionName;
                string now = act != null ? act.BehaviorName + (act.LastRefusal != null ? " (last refusal: " + act.LastRefusal + ")" : "") : "no chore";
                string lastEnd = Short(cap.Find(l => l.Contains("ended sub-behavior") && l.Contains(cn))) ?? "none";
                string lastChore = Short(cap.Find(l => l.Contains("[Chore]") && l.Contains(cn))) ?? "none";
                string lastBoard = Short(cap.Find(l => l.Contains("[JobBoard]") && (l.Contains(cn) || l.Contains("zone #")))) ?? "none";
                r[5] = $"no finish in {RunWait:0} s: now {now}, zone {(idle != null && idle.HasWorkZone ? "still set" : "cleared")}; last end: {lastEnd}; last chore: {lastChore}; last board: {lastBoard}";
            }
            done(r);
        }

    }
}
