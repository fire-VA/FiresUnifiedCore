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
    /// 0.2.902 ([ghost], lead 10-27 for Fire: "replace all of the infinite health mine rocks ... and retest"): homestead_output_test [minutes]
    /// measures what the homestead companions actually PRODUCE (FDT runner contract, Test / SuperFlat worlds). Around home (60 m):
    ///  1 goto 301 952;
    ///  2 (0.2.903) repair: "fap_fix_resource_health 100" at the homestead, its per-prefab answer in the detail, before the snapshot;
    ///  2 snapshot + SHOT: ore rocks (MineRock / MineRock5) and their intact areas, standing trees, infinite-health resources (should be 0
    ///    after "fap_fix_resource_health"), and ore / bars / wood in the base chests + the companions' bags;
    ///  3 run N minutes (default 10, 1-60) with the companions on their own (the test only watches), counting the per-run lines
    ///    "[Chore] mining run" / "smelting run" / "wood run";
    ///  4 mining: ore gained and ore areas broken (PASS when either rose);
    ///  5 smelting: bars gained (PASS when they rose; the detail quotes the last "smelting run" line, which says why when nothing went in);
    ///  6 wood: trees felled / wood gained (PASS when either rose);
    ///  7 SHOT after.
    /// Lines: "[HomesteadOutputTest] BEGIN ...", "n/7 <step> PASS|FAIL - detail", "SHOT <path>", "END ...".
    /// </summary>
    internal static class HomesteadOutputTest
    {
        private const string Command = "homestead_output_test", Tag = "[HomesteadOutputTest] ";
        private const int Steps = 9;   // 0.2.903: + the repair step; 0.2.932: + mining starts within 2 min
        private const float Radius = 60f;
        private static bool s_running;
        // one line each through a BepInEx source (FiresLogger's Unity Log route wraps long lines; FDT reads BEGIN on one line)
        private static readonly ManualLogSource s_log = BepInEx.Logging.Logger.CreateLogSource("FiresUnifiedCore");
        private static readonly AccessTools.FieldRef<GameCamera, float> s_camDistance = AccessTools.FieldRefAccess<GameCamera, float>("m_distance");   // 0.2.903: pose-locked shots

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class HomesteadOutputTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[minutes] [F5 test] [FiresUnifiedCore] homestead_output_test - what the homestead companions produce in N minutes (default 10): ore mined, ore areas broken, bars smelted, trees felled, with before / after shots (admin, Test / SuperFlat worlds)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null) { Say(args.Context, Tag + "no local player"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        int minutes = 10;
                        if (args.Length > 1 && int.TryParse(args[1], out int m)) minutes = Mathf.Clamp(m, 1, 60);
                        Say(args.Context, $"{Tag}world '{world}' accepted ({kind}), {minutes} min");
                        var host = new GameObject("FiresHomesteadOutputTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Run(args.Context, host, minutes));
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
                if (s == null || s.StartsWith(Tag)) return;
                if (s.Contains("[ResourceHealth]") || s.Contains("[Chore] mining run") || s.Contains("mining: ") || s.Contains("[Chore] smelting run") || s.Contains("[Chore] wood run") || s.Contains("infinite health"))
                    lock (m_lines) m_lines.Add(System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " "));
            }
            internal List<string> All(Func<string, bool> f) { lock (m_lines) return m_lines.Where(f).ToList(); }
        }

        private sealed class Snap
        {
            public int Rocks, AreasIntact, AreasBroken, Trees, Logs, Infinite, Smelters, Kilns, KilnOut; public string InfiniteAt = "", StationInfo = "";   // 0.2.907: + stations, kiln output, where infinite rocks are
            public readonly Dictionary<string, int> Ore = new Dictionary<string, int>(), Bars = new Dictionary<string, int>();
            public int Wood;
            public int OreTotal => Ore.Values.Sum();
            public int BarsTotal => Bars.Values.Sum();
        }

        private static int Stock(Vector3 home, string prefab, List<CompanionController> comps)
        {
            int n = FiresCore.Npc.Core.ContainerRegistry.GetTotalItemCount(home, prefab, Radius);
            foreach (var c in comps)
            {
                var bag = c != null ? c.GetInventory()?.GetStorageInventory() : null;
                if (bag != null) n += bag.GetAllItems().Where(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == prefab).Sum(i => i.m_stack);
            }
            return n;
        }

        private static Snap Take(Vector3 home, List<CompanionController> comps)
        {
            var s = new Snap();
            var seen = new HashSet<GameObject>();
            var barNames = new HashSet<string>(); var oreNames = new HashSet<string>();
            foreach (var col in Physics.OverlapSphere(home, Radius))
            {
                if (col == null) continue;
                var mr5 = col.GetComponentInParent<MineRock5>(); var mr = col.GetComponentInParent<MineRock>(); var tb = col.GetComponentInParent<TreeBase>();
                var sm = col.GetComponentInParent<Smelter>();
                GameObject go = mr5 != null ? mr5.gameObject : mr != null ? mr.gameObject : tb != null ? tb.gameObject : sm != null ? sm.gameObject : null;
                if (go == null || !seen.Add(go)) continue;
                if (sm != null) { if (sm.m_maxFuel > 0 && sm.m_fuelItem != null) foreach (var cv in sm.m_conversion) { if (cv?.m_to != null) barNames.Add(cv.m_to.gameObject.name); if (cv?.m_from != null) oreNames.Add(cv.m_from.gameObject.name); } continue; }   // 0.2.905: fuel-burning smelters only (no kiln coal, no windmill)
                if (FiresCore.World.ResourceHealth.Infinite(go, out string infWhy)) { s.Infinite++; if (s.InfiniteAt.Length < 240) s.InfiniteAt += $"{(s.InfiniteAt.Length > 0 ? ", " : "")}{Utils.GetPrefabName(go)} ({go.transform.position.x:0}, {go.transform.position.z:0}) {infWhy}"; }
                if (tb != null) continue;   // 0.2.923: trees are counted from their instances below (round #33: "trees 0" in range while the wood run broke a Birch)
                s.Rocks++;
                var zdo = go.GetComponent<ZNetView>()?.GetZDO();
                if (mr5 != null && zdo != null)
                {
                    var areas = FiresCore.World.ResourceHealth.Areas(zdo);
                    if (areas.Count == 0) s.AreasIntact += 1; else { s.AreasIntact += areas.Count(a => a > 0f); s.AreasBroken += areas.Count(a => a <= 0f); }
                }
                else s.AreasIntact += 1;
            }
            // 0.2.907 (round 28: "bars 0" with a smelter at (296, 953) - EBM bakes building pieces into stand-ins, so the physics overlap never
            // reached its Smelter): the stations are every loaded Smelter within the radius; fuel-burning ones (ore -> bars) apart from kilns
            var kilnOut = new HashSet<string>();
            foreach (var st in UnityEngine.Object.FindObjectsByType<Smelter>(FindObjectsSortMode.None))
            {
                if (st == null) continue;
                Vector3 d = st.transform.position - home; d.y = 0f;
                if (d.magnitude > Radius) continue;
                bool burns = st.m_maxFuel > 0 && st.m_fuelItem != null;
                if (burns) s.Smelters++; else s.Kilns++;
                if (burns && s.StationInfo.Length < 400)
                {
                    // 0.2.908 (lead): each smelter's support and its ore / fuel path - how many chests within 30 m hold its ore and fuel, the nearest
                    var zdo = st.GetComponent<ZNetView>()?.GetZDO();
                    float support = -1f;   // 0.2.911: vanilla's own WearNTear.GetSupport() (the ZDO key only exists once support changed)
                    try { var wnt = st.GetComponent<WearNTear>(); var gs = wnt != null ? HarmonyLib.AccessTools.Method(typeof(WearNTear), "GetSupport") : null; if (gs != null) support = (float)gs.Invoke(wnt, null); } catch { }
                    string Path(string item) { var cs = FiresCore.Npc.Core.ContainerRegistry.GetAllWithItem(st.transform.position, item, 30f); if (cs.Count == 0) return $"no {item} chest"; var n = cs.OrderBy(c => (c.transform.position - st.transform.position).sqrMagnitude).First().transform.position; return $"{cs.Count} {item} chest(s), nearest ({n.x:0}, {n.y:0}, {n.z:0})"; }
                    string ores = string.Join(" / ", st.m_conversion.Where(cv => cv?.m_from != null).Select(cv => Path(cv.m_from.gameObject.name)).Take(3));
                    s.StationInfo += $"{(s.StationInfo.Length > 0 ? "; " : "")}{Utils.GetPrefabName(st.gameObject)} at ({st.transform.position.x:0}, {st.transform.position.y:0}, {st.transform.position.z:0}): support {(support < 0 ? "?" : support.ToString("0"))}, queued {(zdo != null ? zdo.GetInt(ZDOVars.s_queued, 0) : 0)}/{st.m_maxOre}, fuel {(zdo != null ? zdo.GetFloat(ZDOVars.s_fuel, 0f) : 0f):0.#}/{st.m_maxFuel}; {ores}; {Path(st.m_fuelItem.gameObject.name)}";
                }
                foreach (var cv in st.m_conversion)
                {
                    if (cv?.m_to == null) continue;
                    if (burns) { barNames.Add(cv.m_to.gameObject.name); if (cv.m_from != null) oreNames.Add(cv.m_from.gameObject.name); }
                    else if (cv.m_from != null && !cv.m_from.gameObject.name.Contains("Ore") && !cv.m_from.gameObject.name.Contains("Scrap")) kilnOut.Add(cv.m_to.gameObject.name);
                }
            }
            foreach (string k in kilnOut) s.KilnOut += Stock(home, k, comps);
            if (oreNames.Count == 0) foreach (string o in ResourceDataHelper.SmeltableItems) if (o.EndsWith("Ore") || o.Contains("Scrap")) oreNames.Add(o);   // 0.2.905: ore only (SmeltableItems also lists kiln / windmill inputs: Wood, OatSeeds ...)
            foreach (string ore in oreNames) { int n = Stock(home, ore, comps); if (n > 0) s.Ore[ore] = n; }
            foreach (string bar in barNames) { int n = Stock(home, bar, comps); if (n > 0) s.Bars[bar] = n; }
            // 0.2.923: trees and logs from the loaded instances (EBM stand-ins and LOD hide some from the physics overlap, like the stations)
            foreach (var t in UnityEngine.Object.FindObjectsByType<TreeBase>(FindObjectsSortMode.None)) if (t != null && Vector3.Distance(t.transform.position, home) <= Radius) s.Trees++;
            foreach (var t in UnityEngine.Object.FindObjectsByType<TreeLog>(FindObjectsSortMode.None)) if (t != null && Vector3.Distance(t.transform.position, home) <= Radius) s.Logs++;
            s.Wood = Stock(home, "Wood", comps) + Stock(home, "FineWood", comps) + Stock(home, "RoundLog", comps) + Stock(home, "ElderBark", comps);
            return s;
        }

        private static string Show(Snap s) => $"rocks {s.Rocks} (areas intact {s.AreasIntact}, broken {s.AreasBroken}), trees {s.Trees}, infinite-health {s.Infinite}; ore {s.OreTotal}" +
            (s.Ore.Count > 0 ? $" ({string.Join(", ", s.Ore.Select(kv => $"{kv.Key} {kv.Value}"))})" : "") + $", bars {s.BarsTotal}" +
            (s.Bars.Count > 0 ? $" ({string.Join(", ", s.Bars.Select(kv => $"{kv.Key} {kv.Value}"))})" : "") + $", wood {s.Wood}; stations: {s.Smelters} smelter(s) (ore -> bars), {s.Kilns} kiln(s) (wood -> coal, kiln output in stock {s.KilnOut})" + (s.Infinite > 0 ? $"; infinite at: {s.InfiniteAt}" : "") + (s.StationInfo.Length > 0 ? $"; {s.StationInfo}" : "");

        private static IEnumerator Capture2(string name, Action<string> done)
        {
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests", "shots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            yield return new WaitForEndOfFrame();
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

        private static IEnumerator Run(Terminal ctx, GameObject host, int minutes)
        {
            s_running = true;
            var cap = new Capture();
            BepInEx.Logging.Logger.Listeners.Add(cap);
            int pass = 0, fail = 0, skip = 0, step = 0; var shots = new List<string>();
            float started = Time.realtimeSinceStartup;
            void Result(string name, bool ok, string detail) { step++; if (ok) pass++; else fail++; Say(ctx, $"{Tag}{step}/{Steps} {name} {(ok ? "PASS" : "FAIL")} - {detail}"); }
            void Skip(string name, string detail) { step++; skip++; Say(ctx, $"{Tag}{step}/{Steps} {name} SKIP - {detail}"); }   // 0.2.912
            IEnumerator Shot(string name) { string got = null; yield return Capture2(name, x => got = x); if (got != null) { shots.Add(got); Say(ctx, $"{Tag}SHOT {got}"); } }
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: {Steps} steps, ~{minutes * 60 + 90} s");
            try
            {
                // 1 goto
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
            float off = pl != null ? Vector2.Distance(new Vector2(pl.transform.position.x, pl.transform.position.z), new Vector2(301f, 952f)) : 999f;
            Result("goto 301 952", off < 15f, $"player {off:0.0} m from the spot");

            // 0.2.903 (lead): one camera pose for both rock-field shots - where the goto left the player, looking north and a little down,
            // camera 8 m back; the end shot moves back to it first
            Vector3 posePos = pl != null ? pl.transform.position : Vector3.zero;
            Vector3 poseDir = new Vector3(0f, -0.3f, 1f).normalized;
            IEnumerator Pose()
            {
                var pp = Player.m_localPlayer;
                if (pp == null) yield break;
                if (Vector3.Distance(pp.transform.position, posePos) > 1f)
                {
                    pp.TeleportTo(posePos, Quaternion.LookRotation(new Vector3(poseDir.x, 0f, poseDir.z)), true);
                    float t = Time.realtimeSinceStartup + 40f;
                    while (Time.realtimeSinceStartup < t && pp != null && pp.IsTeleporting()) yield return null;
                    yield return new WaitForSecondsRealtime(3f);   // the teleport FX has to clear first
                }
                pp.transform.rotation = Quaternion.LookRotation(new Vector3(poseDir.x, 0f, poseDir.z));
                pp.SetLookDir(poseDir);
                try { if (GameCamera.instance != null) s_camDistance(GameCamera.instance) = 8f; } catch { }
                yield return new WaitForSecondsRealtime(1.5f);
                Say(ctx, $"{Tag}pose ({pp.transform.position.x:0.0}, {pp.transform.position.y:0.0}, {pp.transform.position.z:0.0}) looking (0, -0.3, 1), camera 8 m");
            }

            // 2 repair (0.2.903, lead / Fire: "replace all of the infinite health mine rocks ... and retest"): the admin repair at the homestead
            // BEFORE the start snapshot, through the console like an admin types it; its server answer (per-prefab counts) is the detail
            string repaired = null;
            try { Console.instance?.TryRunCommand("fap_fix_resource_health 100"); } catch (Exception e) { repaired = "threw " + e.Message; }
            // 0.2.907: every peer answers for the resources it owns - wait for the first, then 4 s more for the rest, and show them all
            float rUntil = Time.realtimeSinceStartup + 20f;
            List<string> answers = null;
            while (repaired == null && Time.realtimeSinceStartup < rUntil)
            {
                answers = cap.All(l => l.Contains("[ResourceHealth] fap_fix_resource_health: ") && !l.Contains("asked the server") && !l.Contains("cleared "));
                if (answers.Count > 0) { yield return new WaitForSecondsRealtime(4f); answers = cap.All(l => l.Contains("[ResourceHealth] fap_fix_resource_health: ") && !l.Contains("asked the server") && !l.Contains("cleared ")); repaired = string.Join(" | ", answers.Select(a => a.Substring(a.IndexOf("fap_fix_resource_health: ", StringComparison.Ordinal) + 25))); }
                else yield return new WaitForSecondsRealtime(0.5f);
            }
            Result("repair fap_fix_resource_health 100", repaired != null && !repaired.Contains("refused") && !repaired.Contains("failed") && !repaired.StartsWith("threw"),
                repaired ?? "no answer from the server in 20 s");
            yield return new WaitForSecondsRealtime(2f);

            long me = pl != null ? pl.GetPlayerID() : 0L;
            var comps = CompanionController.AllCompanions.Where(c => c != null && c.isTamed && c.ownerPlayerId == me).ToList();
            Vector3 home = JobBoard.BaseCenter();
            Snap before = null;
            try { before = Take(home, comps); } catch (Exception e) { Say(ctx, $"{Tag}snapshot threw {e.Message}"); }
            Result("snapshot", before != null && before.Infinite == 0,
                before == null ? "no snapshot" : Show(before) + (before.Infinite > 0 ? $" - {before.Infinite} resource(s) still have infinite health: run fap_fix_resource_health first" : "") + $"; {comps.Count} companion(s)");
            try { Say(ctx, $"{Tag}needs at the start: {HomesteadNeeds.Line(HomesteadNeeds.Build(home))}"); } catch (Exception e) { Say(ctx, $"{Tag}needs threw {e.Message}"); }   // 0.2.909: the homestead needs model
            yield return Pose(); yield return Shot("homestead_output_before");

            // 3 watch
            float end = Time.realtimeSinceStartup + minutes * 60f;
            // 0.2.932 (lead, Fire "the companions still do not mine them"): within the first 2 min a pick must find a mineable deposit
            // ("mineable" > 0 / "mining: picked") and a mining run must start (ResourceGathering on an ore rock)
            float t2 = Time.realtimeSinceStartup + 120f; bool mineStarted = false; string firstMine = null;
            while (Time.realtimeSinceStartup < end)
            {
                yield return new WaitForSecondsRealtime(5f);
                if (!mineStarted && Time.realtimeSinceStartup <= t2)
                {
                    var picked = cap.All(l => l.Contains("mining: picked") || l.Contains("[Chore] mining run"));
                    if (picked.Count > 0) { mineStarted = true; firstMine = picked[0]; }
                }
                if (Time.realtimeSinceStartup > t2 && step == 3) Result("mining starts within 2 min", mineStarted, mineStarted ? firstMine : $"no 'mining: picked' in 2 min; last pick: {cap.All(l => l.Contains("mining: ")).LastOrDefault() ?? "none"}");
            }
            if (step == 3) Result("mining starts within 2 min", mineStarted, mineStarted ? firstMine : "the run was shorter than 2 min");
            var mining = cap.All(l => l.Contains("[Chore] mining run")); var smelting = cap.All(l => l.Contains("[Chore] smelting run")); var wood = cap.All(l => l.Contains("[Chore] wood run"));
            Result($"{minutes} min run", true, $"{mining.Count} mining run(s), {smelting.Count} smelting run(s), {wood.Count} wood run(s); infinite-health skips seen: {cap.All(l => l.Contains("has infinite health (")).Count}");

            Snap after = null;
            try { after = Take(home, comps); } catch (Exception e) { Say(ctx, $"{Tag}snapshot threw {e.Message}"); }
            string Last(List<string> l) => l.Count > 0 ? (l[l.Count - 1].Length > 260 ? l[l.Count - 1].Substring(0, 260) + "..." : l[l.Count - 1]) : "none";
            if (before != null && after != null)
            {
                int ore = after.OreTotal - before.OreTotal, areas = after.AreasBroken - before.AreasBroken + Math.Max(0, before.Rocks - after.Rocks);
                // 0.2.905 (lead, round 27: "Mining PASS - ore +93" with 0 areas broken - the +93 was mostly wood): mining needs ore areas broken
                // AND ore credited by the companions' own "mining run" lines (smelting can lower the stock while mining raises it)
                int mined = 0;
                foreach (var l in mining)
                {
                    var mm = System.Text.RegularExpressions.Regex.Match(l, @"ore \+(\d+)");
                    if (mm.Success) mined += int.Parse(mm.Groups[1].Value);
                }
                // 0.2.912 (lead, round #30: 0 mining runs with 122 CopperOre in the chests): with no bar short (bars + ore on the way >= target)
                // mining SHOULD rest - the step is judged only when the needs model says a bar is short
                var barNeeds = HomesteadNeeds.Build(home).Items.Values.Where(n => n.Kind == "bars").ToList();
                string barsSaid = string.Join(", ", barNeeds.Select(n => $"{n.Item} {n.Stock}{(n.OnTheWay > 0 ? $"+{n.OnTheWay}" : "")}/{n.Target}"));
                // 0.2.932 (Fire "Always mine when free"): no more needs-gated SKIP - free companions mine whatever the stock
                Result("mining", areas > 0 && mined > 0, $"needs: {(barsSaid.Length > 0 ? barsSaid : "no smelter bars tracked")}; ore areas broken {areas} (rocks {before.Rocks} -> {after.Rocks}), ore from mining runs +{mined} ({mining.Count} run(s)), ore stock {(ore >= 0 ? "+" : "")}{ore}; last: {Last(mining)}");
                int bars = after.BarsTotal - before.BarsTotal;
                // 0.2.907 (lead): a kiln run is not smelting - its coal is its own line; no smelter (ore -> bars) in range = said plainly
                int coal = after.KilnOut - before.KilnOut;
                var realRuns = smelting.Where(l => !l.Contains(" at charcoal_kiln")).ToList();
                if (before.Smelters == 0)
                    Result("smelting", false, $"no smelter (ore -> bars) within {Radius:0} m of home - only {before.Kilns} kiln(s); mining -> smelting can't be shown here until one is placed; kiln: coal {(coal >= 0 ? "+" : "")}{coal}, {smelting.Count - realRuns.Count} kiln run(s)");
                else
                    Result("smelting", bars > 0, $"bars {(bars >= 0 ? "+" : "")}{bars} from {before.Smelters} smelter(s), {realRuns.Count} smelter run(s); kiln: coal {(coal >= 0 ? "+" : "")}{coal}, {smelting.Count - realRuns.Count} kiln run(s); last smelter run: {Last(realRuns)}");
                int felled = before.Trees - after.Trees, w = after.Wood - before.Wood;
                int broke = wood.Sum(l => { var mm = System.Text.RegularExpressions.Regex.Match(l, @"broke (\d+)"); return mm.Success ? int.Parse(mm.Groups[1].Value) : 0; });   // 0.2.923: what the wood runs themselves broke
                Result("wood", felled > 0 || w > 0 || broke > 0, $"trees felled {felled} ({before.Trees} -> {after.Trees}), logs {before.Logs} -> {after.Logs}, broken by the wood runs {broke}, wood {(w >= 0 ? "+" : "")}{w}; last: {Last(wood)}");
                Say(ctx, $"{Tag}after: {Show(after)}");
                try { Say(ctx, $"{Tag}needs at the end: {HomesteadNeeds.Line(HomesteadNeeds.Build(home))}"); } catch (Exception e) { Say(ctx, $"{Tag}needs threw {e.Message}"); }   // 0.2.909
            }
            else { Result("mining", false, "no snapshot"); Result("smelting", false, "no snapshot"); Result("wood", false, "no snapshot"); }
            yield return Pose(); yield return Shot("homestead_output_after");
            step++; pass++; Say(ctx, $"{Tag}{step}/{Steps} shots PASS - {shots.Count} shot(s)");

            BepInEx.Logging.Logger.Listeners.Remove(cap);
            Say(ctx, $"{Tag}END {(fail == 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip, {pass + fail} compared, {shots.Count} shot(s) in {Time.realtimeSinceStartup - started:0.0} s");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }
    }
}
