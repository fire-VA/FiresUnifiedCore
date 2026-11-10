using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using FiresCore.Npc;
using FiresCore.Npc.JobBoard;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// 0.2.875 ([ghost], lead 10-27: "rounds have no human to open the map"): homemap_test - the homestead map / Job Board UI driven unattended,
    /// FDT runner contract. Admin, Test worlds only (GroundRingShotsTest.IsTestWorld). Steps:
    ///  1 goto 301 952 (the homestead test spot), 2 Job Board on Board, 3 on Reports (tab strip + page layout measured), 4 Map from the board
    ///  (homestead map open, board docked), 5 the live map open ~65 s (expects "[LiveMap] levels", "[HomeMap] drew", "[HomeMap] ore scan"),
    ///  6 left-click a companion marker (intent panel), 7 right-click a companion (popup + Assign submenu), 8 right-click a station and a chest
    ///  (popup each), 9 ledger moved + map reopened (restored saved), 10 send-to on a companion (server applies it; its stay point is put back).
    /// Marker clicks go through the EventSystem to the markers' own click handlers (ExecuteEvents), the path a mouse click takes.
    /// Lines: "[HomeMapTest] BEGIN ...", "[HomeMapTest] n/10 <step> PASS|FAIL - detail", "[HomeMapTest] SHOT <path> (WxH)", "[HomeMapTest] END
    /// PASS|FAIL: p pass, f fail, 0 skip, c compared, s shot(s)". Cleanup: map + board + popup closed, the moved companion's stay point restored.
    /// </summary>
    internal static class HomeMapTest
    {
        private const string Command = "homemap_test", Tag = "[HomeMapTest] ";
        private const int Steps = 10;
        private static bool s_running;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class HomeMapTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] homemap_test - the homestead map + Job Board UI unattended, with shots (admin, Test worlds only)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test world (no 'Test' / 'ATomTest' / 'AB' name, not a SuperFlat world)"); return; }
                        Say(args.Context, $"{Tag}world '{world}' accepted ({kind})");
                        if (Player.m_localPlayer == null) { Say(args.Context, Tag + "no local player"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        var host = new GameObject("FiresHomeMapTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Run(args.Context, host));
                    }, isCheat: true);
            }
        }

        private sealed class Runner : MonoBehaviour { }

        // 0.2.878 (round #5: "refused, 'SuperFlatSkyCaves3' is not a Test" - Fire's homestead test world): the team's name rule
        // (GroundRingShotsTest / FDT TestSite.IsTestWorld) OR a SuperFlat world - by name, or FAT's own world profile
        // (VerdantsAscent.Terrain.Voxel.FiresWorldProfile.CurrentIsSuperFlat, by reflection). Only on Test / SuperFlat worlds.
        private static System.Reflection.PropertyInfo s_superFlat; private static bool s_superFlatLooked;
        internal static bool IsTestWorld(string world, out string kind)
        {
            kind = null;
            if (GroundRingShotsTest.IsTestWorld(world)) { kind = "Test-world name"; return true; }
            if (!string.IsNullOrEmpty(world) && world.StartsWith("SuperFlat", StringComparison.OrdinalIgnoreCase)) { kind = "SuperFlat world name"; return true; }
            if (!s_superFlatLooked)
            {
                s_superFlatLooked = true;
                s_superFlat = FiresCore.Compat.OptionalTypes.Find("VerdantsAscent.Terrain.Voxel.FiresWorldProfile")?.GetProperty("CurrentIsSuperFlat", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            }
            try { if (s_superFlat != null && (bool)s_superFlat.GetValue(null)) { kind = "FAT SuperFlat profile"; return true; } } catch { }
            return false;
        }

        private static void Say(Terminal ctx, string line)
        {
            ctx?.AddString(line);
            FiresCore.Logging.FiresLogger.LogInfo(line);
        }

        // the proof lines the steps expect, captured from the log as they are written
        private sealed class Capture : ILogListener
        {
            internal readonly List<string> Lines = new List<string>();
            public LogLevel LogLevelFilter => LogLevel.All;
            public void Dispose() { }
            public void LogEvent(object sender, LogEventArgs e)
            {
                string s = e?.Data?.ToString();
                if (s == null || s.StartsWith(Tag)) return;
                if (s.Contains("[HomeMap]") || s.Contains("[LiveMap]") || s.Contains("[JobBoardUI]")) lock (Lines) Lines.Add(System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " "));   // 0.2.882: FiresLogger wraps long lines with newline + indent; collapsed so "intent panel on" matches
            }
            internal string Find(string part) { lock (Lines) return Lines.LastOrDefault(l => l.Contains(part)); }
            internal void Clear() { lock (Lines) Lines.Clear(); }
        }

        private static IEnumerator Run(Terminal ctx, GameObject host)
        {
            s_running = true;
            var cap = new Capture();
            BepInEx.Logging.Logger.Listeners.Add(cap);
            int pass = 0, fail = 0, compared = 0, step = 0;
            var shots = new List<string>();
            float started = Time.realtimeSinceStartup;
            CompanionController movedComp = null; Vector3 movedHome = Vector3.zero; bool movedHad = false;
            void Result(string name, bool ok, string detail) { step++; compared++; if (ok) pass++; else fail++; Say(ctx, $"{Tag}{step}/{Steps} {name} {(ok ? "PASS" : "FAIL")} - {detail}"); }
            IEnumerator Shot(string name) { string got = null; yield return Capture2(name, p => got = p); if (got != null) { shots.Add(got); Say(ctx, $"{Tag}SHOT {got}"); } }

            // 0.2.884: FDT's runner reads "BEGIN <mod> <ver> (<hash>) ...: N steps, ~S s" for its timeout (2 x S); "~2 min" was unread (60 s default)
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: {Steps} steps, ~150 s");
            var p = Player.m_localPlayer;

            // 1 goto the homestead test spot
            Vector3 spot = new Vector3(301f, 50f, 952f);
            if (ZoneSystem.instance != null) spot.y = ZoneSystem.instance.GetGroundHeight(spot) + 0.5f;
            p.TeleportTo(spot, p.transform.rotation, true);
            float until = Time.realtimeSinceStartup + 40f;
            while (Time.realtimeSinceStartup < until && (p != null && p.IsTeleporting())) yield return null;
            yield return new WaitForSecondsRealtime(4f);
            p = Player.m_localPlayer;
            float off = p != null ? Vector3.Distance(new Vector3(p.transform.position.x, 0f, p.transform.position.z), new Vector3(301f, 0f, 952f)) : 999f;
            Result("goto 301 952", off < 15f, $"player {off:0.0} m from the spot");

            // 2-3 Job Board tabs
            if (!JobBoardWindow.IsShowing) JobBoardWindow.Toggle();
            yield return new WaitForSecondsRealtime(1.5f);
            JobBoardWindow.TestShowTab(0); yield return new WaitForSecondsRealtime(0.8f);
            string lay = JobBoardWindow.TestLayout(out bool okBoard);
            Result("Job Board: Board tab", okBoard, lay);
            yield return Shot("homemap_board");
            yield return Shot("homemap_header");   // 0.2.887: Fire's header proof (the tab row)
            JobBoardWindow.TestShowTab(1); yield return new WaitForSecondsRealtime(1.5f);
            lay = JobBoardWindow.TestLayout(out bool okRep);
            Result("Job Board: Reports tab", okRep, lay);
            yield return Shot("homemap_reports");

            // 0.2.881: every layer shown for the test (the player's saved choices come back at cleanup)
            Say(ctx, $"{Tag}layers: {HomesteadMap.TestShowAllLayers(true)}");

            // 4 Map from the board
            cap.Clear();
            JobBoardWindow.TestShowTab(0);
            HomesteadMap.Open();
            yield return new WaitForSecondsRealtime(2.5f);
            bool open = HomesteadMap.TestIsOpen;
            Result("Map from the board", open && !JobBoardWindow.IsShowing, open ? $"homestead map open, full board {(JobBoardWindow.IsShowing ? "STILL SHOWING" : "hidden (docked)")}; {cap.Find("ledger placed") ?? "no ledger line"}" : (cap.Find("refused") ?? "not open, no refusal line"));
            yield return Shot("homemap_map_open");

            // 5 live map for ~65 s
            yield return new WaitForSecondsRealtime(65f);
            string levels = cap.Find("[LiveMap] levels"), drew = cap.Find("[HomeMap] drew"), ore = cap.Find("ore scan"), cost = cap.Find("[LiveMap] cost");
            Result("live map 65 s", open && levels != null && drew != null && ore != null,
                $"levels: {Short(levels)} | drew: {Short(drew)} | ore: {Short(ore)} | cost: {Short(cost)}");
            yield return Shot("homemap_live");

            // 6 left-click a companion marker
            var comp = HomesteadMap.TestMarker("Companion");
            if (comp != null) { Click(comp, PointerEventData.InputButton.Left); yield return new WaitForSecondsRealtime(1f); }
            Result("left-click companion", comp != null && cap.Find("intent panel on") != null, comp == null ? "no companion marker shown" : Short(cap.Find("intent panel on") ?? cap.Find("click Companion")));
            yield return Shot("homemap_intent");

            // 7 right-click a companion: popup + Assign submenu
            bool sub = false;
            if (comp != null)
            {
                // 0.2.886 (round #12: "click Pickable 'Turnip'" - step 6's marker object, reused a second later, had been re-slotted to a turnip by the
                // 2 s rescan): every click re-fetches its marker in the same frame it clicks it
                comp = HomesteadMap.TestMarker("Companion") ?? comp;
                Click(comp, PointerEventData.InputButton.Right); yield return new WaitForSecondsRealtime(0.8f);
                sub = HomesteadMapActions.TestPopup != null && HomesteadMapActions.TestClickRow("Assign");
                yield return new WaitForSecondsRealtime(0.5f);
                sub = sub && HomesteadMapActions.TestSubmenu != null;
            }
            Result("right-click companion", sub, comp == null ? "no companion marker" : $"popup {(HomesteadMapActions.TestPopup != null ? "open" : "MISSING")}, Assign submenu {(HomesteadMapActions.TestSubmenu != null ? "open" : "MISSING")}; {Short(cap.Find("popup with"))}");
            yield return Shot("homemap_popup_companion");
            HomesteadMapActions.ClosePopup(true);

            // 8 right-click a station and a chest
            var station = HomesteadMap.TestMarker("Workbench") ?? HomesteadMap.TestMarker("Smelter") ?? HomesteadMap.TestMarker("Fire") ?? HomesteadMap.TestMarker("Cooking");
            var chest = HomesteadMap.TestMarker("Chest") ?? HomesteadMap.TestMarker("Terminal");
            bool stOk = false, chOk = false;
            station = HomesteadMap.TestMarker("Workbench") ?? HomesteadMap.TestMarker("Smelter") ?? HomesteadMap.TestMarker("Fire") ?? HomesteadMap.TestMarker("Cooking") ?? station;   // 0.2.886: fresh, same frame
            if (station != null) { Click(station, PointerEventData.InputButton.Right); yield return new WaitForSecondsRealtime(0.8f); stOk = HomesteadMapActions.TestPopup != null; yield return Shot("homemap_popup_station"); HomesteadMapActions.ClosePopup(true); }
            chest = HomesteadMap.TestMarker("Chest") ?? HomesteadMap.TestMarker("Terminal") ?? chest;   // 0.2.886: fresh, same frame
            if (chest != null) { Click(chest, PointerEventData.InputButton.Right); yield return new WaitForSecondsRealtime(0.8f); chOk = HomesteadMapActions.TestPopup != null; yield return Shot("homemap_popup_chest"); HomesteadMapActions.ClosePopup(true); }
            Result("right-click station + chest", stOk && chOk, $"station {(station == null ? "no marker" : stOk ? "popup" : "NO POPUP")}, chest {(chest == null ? "no marker" : chOk ? "popup" : "NO POPUP")}");

            // 9 ledger moved, map reopened: restored saved
            var led = HomesteadMap.TestLedger;
            if (led != null && led.Root != null) led.Root.anchoredPosition += new Vector2(80f, -60f);
            HomesteadMap.TestClose(); yield return new WaitForSecondsRealtime(1f);
            cap.Clear();
            HomesteadMap.Open(); yield return new WaitForSecondsRealtime(2.5f);
            string placed = cap.Find("ledger placed");
            Result("ledger remembered", placed != null && placed.Contains("restored saved"), Short(placed) ?? "no ledger line");
            yield return Shot("homemap_ledger_reopen");

            // 10 send-to (server-applied), then the stay point put back
            var c = CompanionController.AllCompanions.FirstOrDefault(x => x != null && x.isTamed && x.ownerPlayerId == (p != null ? p.GetPlayerID() : 0L) && !x.ShouldBeFollowing);
            bool sent = false;
            if (c != null)
            {
                var idle = c.GetComponent<FiresCore.Npc.CompanionIdleBehavior>();
                movedComp = c; movedHad = idle != null && idle.HasHomePosition; movedHome = movedHad ? idle.HomePosition : c.transform.position;
                var nv = c.GetComponent<ZNetView>();
                var t = new HomesteadMapActions.Target { Type = "Companion", Name = c.GetDisplayName(), Pos = c.transform.position, Go = c.gameObject, Id = nv != null && nv.IsValid() ? nv.GetZDO().m_uid : ZDOID.None };
                cap.Clear();
                HomesteadMapActions.TestSendTo(t, movedHome + new Vector3(6f, 0f, 0f));
                until = Time.realtimeSinceStartup + 10f;
                while (Time.realtimeSinceStartup < until && cap.Find("apply send-to") == null) yield return null;
                sent = cap.Find("apply send-to") != null;
                HomesteadMapActions.TestSendTo(t, movedHome);   // put it back
                yield return new WaitForSecondsRealtime(1f);
            }
            Result("send-to (server-applied)", sent, c == null ? "no staying companion of yours" : Short(cap.Find("apply send-to") ?? cap.Find("refused") ?? "no apply line in 10 s") + "; stay point restored");

            // cleanup
            Say(ctx, $"{Tag}layers: {HomesteadMap.TestShowAllLayers(false)}");
            HomesteadMapActions.ClosePopup(true);
            if (HomesteadMap.TestIsOpen) HomesteadMap.TestClose();
            if (Minimap.instance != null) Minimap.instance.SetMapMode(Minimap.MapMode.Small);
            if (JobBoardWindow.IsShowing) JobBoardWindow.Toggle();
            BepInEx.Logging.Logger.Listeners.Remove(cap);
            Say(ctx, $"{Tag}END {(fail == 0 && compared > 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, 0 skip, {compared} compared, {shots.Count} shot(s) in {Time.realtimeSinceStartup - started:0.0} s");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }

        private static string Short(string s) => s == null ? null : (s.Length > 220 ? s.Substring(0, 220) + "..." : s);

        // a click through the EventSystem onto the marker's own handler, as the mouse would
        private static void Click(GameObject marker, PointerEventData.InputButton button)
        {
            var es = EventSystem.current;
            if (es == null || marker == null) return;
            var rt = (RectTransform)marker.transform;
            var c = new Vector3[4]; rt.GetWorldCorners(c);
            var ped = new PointerEventData(es) { button = button, position = RectTransformUtility.WorldToScreenPoint(null, (c[0] + c[2]) * 0.5f) };
            ExecuteEvents.Execute(marker, ped, ExecuteEvents.pointerClickHandler);
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
    }
}
