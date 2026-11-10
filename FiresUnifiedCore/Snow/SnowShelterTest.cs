using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.927 ([wishbone], lead 10-27): core_snow_shelter_test, FDT runner contract, Test / SuperFlat worlds (HomeMapTest.IsTestWorld),
    /// admin, needs [Snow] No snow in closed rooms ON. 0.2.930: ground-up, waits 35 s, setup FAIL on any unsupported / collapsed piece. Builds two 4 x 4 m woodwall huts with a wood_floor roof 30 m east of the player
    /// (one closed, one with a doorway: one wall panel left out), computes their zone now (SnowShelter.ComputeNow), then removes them.
    ///  1 hut at x274 z823 (the SFSC3 snow hut): reports closed / open / not loaded (SKIP when not loaded; informational);
    ///  2 closed: the centre factor <= 0.05;
    ///  3 walls: a point 0.6 m inside the middle of each of the closed hut's 4 walls <= 0.05 (no sliver along thin walls);
    ///  4 doorway: 0.6 m inside the opening > 0.05 (snow blows in);
    ///  5 fade: the doorway hut's far corner < its doorway value;
    ///  6 open ground 12 m away = 1;
    ///  7 cleanup: every built piece removed.
    /// Lines: "[CoreSnowShelterTest] BEGIN ...", "n/7 <step> PASS|FAIL|SKIP - detail", "END PASS|FAIL: ...".
    /// </summary>
    internal static class SnowShelterTest
    {
        private const string Command = "core_snow_shelter_test", Tag = "[CoreSnowShelterTest] ";
        private const float SettleSeconds = 35f;
        private static readonly AccessTools.FieldRef<WearNTear, float> s_support = AccessTools.FieldRefAccess<WearNTear, float>("m_support");
        private static readonly System.Reflection.MethodInfo s_minSupport = AccessTools.Method(typeof(WearNTear), "GetMinSupport");

        // null when every built piece still stands with support >= its minimum; else "<piece> support X (min Y)" / "collapsed"
        private static string Unsupported(List<GameObject> built)
        {
            foreach (var go in built)
            {
                if (go == null) return "a piece collapsed (destroyed)";
                var w = go.GetComponent<WearNTear>();
                if (w == null) continue;
                float sup = s_support(w), min = s_minSupport != null ? (float)s_minSupport.Invoke(w, null) : 0f;
                if (sup < min) return $"{Utils.GetPrefabName(go)} at {go.transform.position:F1} support {sup:0.0} (min {min:0.0})";
            }
            return null;
        }
        private static bool s_running;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class SnowShelterTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] core_snow_shelter_test - no snow in a closed room, snow drifting in at a doorway, none along the inside of thin walls (admin, Test / SuperFlat worlds; builds and removes two huts)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!FiresCore.Diagnostics.HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null || ZNetScene.instance == null) { Say(args.Context, Tag + "no local player / scene"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        var host = new GameObject("FiresSnowShelterTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Run(args.Context, kind, host));
                    }, isCheat: true);
            }
        }

        private sealed class Runner : MonoBehaviour { }
        private static void Say(Terminal ctx, string line) { ctx?.AddString(line); FiresCore.Logging.FiresLogger.LogInfo(line); }
        private static float Ground(Vector3 p) => ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(p) : p.y;

        private static GameObject Place(List<GameObject> built, string prefab, Vector3 at, Quaternion rot)
        {
            var go = ZNetScene.instance.GetPrefab(prefab);
            if (go == null) return null;
            var inst = UnityEngine.Object.Instantiate(go, at, rot);
            if (inst != null) built.Add(inst);
            return inst;
        }

        // a 4 x 4 m hut centred on c: 2 woodwall panels (2 m) per side, 4 wood_floor (2 x 2 m) as the roof at 2 m; door = leave out the
        // south-east panel
        private static bool Hut(List<GameObject> built, Vector3 c, bool door)
        {
            Quaternion ns = Quaternion.identity, ew = Quaternion.Euler(0f, 90f, 0f);
            float y = Ground(c);
            for (int s = -1; s <= 1; s += 2)
            {
                if (Place(built, "woodwall", new Vector3(c.x + s, y, c.z + 2f), ns) == null) return false;   // north
                if (!(door && s == 1)) Place(built, "woodwall", new Vector3(c.x + s, y, c.z - 2f), ns);    // south (the doorway)
                Place(built, "woodwall", new Vector3(c.x + 2f, y, c.z + s), ew);                           // east
                Place(built, "woodwall", new Vector3(c.x - 2f, y, c.z + s), ew);                           // west
            }
            for (int a = -1; a <= 1; a += 2)
                for (int b = -1; b <= 1; b += 2)
                    if (Place(built, "wood_floor", new Vector3(c.x + a, y + 2f, c.z + b), ns) == null) return false;
            return true;
        }

        private static IEnumerator Run(Terminal ctx, string kind, GameObject host)
        {
            s_running = true;
            float t0 = Time.realtimeSinceStartup;
            int pass = 0, fail = 0, skip = 0, step = 0;
            void Result(string name, string verdict, string detail) { step++; if (verdict == "PASS") pass++; else if (verdict == "FAIL") fail++; else skip++; Say(ctx, $"{Tag}{step}/7 {name} {verdict} - {detail}"); }
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: 7 steps, ~40 s ({kind})");
            var built = new List<GameObject>();
            if (!SnowShelter.On)
            {
                for (int i = 0; i < 7; i++) Result("setup", "SKIP", "[Snow] No snow in closed rooms is off (turn it on to test)");
            }
            else
            {
                // 1 the SFSC3 snow hut (informational)
                var hut = new Vector3(274f, 0f, 823f);
                Vector3 me = Player.m_localPlayer.transform.position;
                if (Vector3.Distance(new Vector3(me.x, 0f, me.z), hut) > 150f) Result("snow hut x274 z823", "SKIP", $"not loaded ({Vector3.Distance(new Vector3(me.x, 0f, me.z), hut):0} m away)");
                else
                {
                    string why = SnowShelter.ComputeNow(hut);
                    float f = SnowShelter.ShelterAt(hut);
                    Result("snow hut x274 z823", "PASS", $"factor {f:0.00} at its centre = {(f <= 0.05f ? "CLOSED" : f < 0.98f ? "PARTLY OPEN" : "OPEN / no roof")}; {why}");
                }

                Vector3 east = me + new Vector3(30f, 0f, 0f), closed = east + new Vector3(0f, 0f, 6f), open = east + new Vector3(0f, 0f, -6f);
                bool ok = Hut(built, closed, false) && Hut(built, open, true);
                // 0.2.930 (Fire's build-test rule): ground-up, then wait > 30 s (new pieces read MAX support for 30 s), then every piece
                // must still stand with support >= its minimum, else a setup FAIL - only then measure
                if (ok) yield return new WaitForSeconds(SettleSeconds);
                Physics.SyncTransforms();
                string unsupported = ok ? Unsupported(built) : null;
                if (!ok || unsupported != null)
                {
                    Result("closed", "FAIL", ok ? $"REASON setup FAIL: {unsupported}" : "REASON woodwall / wood_floor prefab missing");
                    for (int i = 0; i < 4; i++) Result("skipped", "SKIP", ok ? "setup failed (unsupported piece)" : "no huts");
                }
                else
                {
                    string w1 = SnowShelter.ComputeNow(closed);
                    if (ZoneSystem.GetZone(open) != ZoneSystem.GetZone(closed)) SnowShelter.ComputeNow(open);
                    float fc = SnowShelter.ShelterAt(closed);
                    Result("closed", fc <= 0.05f ? "PASS" : "FAIL", $"{(fc <= 0.05f ? "" : "REASON snow inside a closed room; ")}centre factor {fc:0.00}; {w1}");
                    float worst = 0f; string at = "";
                    foreach (var d in new[] { new Vector3(0f, 0f, 1.4f), new Vector3(0f, 0f, -1.4f), new Vector3(1.4f, 0f, 0f), new Vector3(-1.4f, 0f, 0f) })
                    {
                        float f = SnowShelter.ShelterAt(closed + d);
                        if (f >= worst) { worst = f; at = $"({d.x:0.0},{d.z:0.0})"; }
                    }
                    Result("walls", worst <= 0.05f ? "PASS" : "FAIL", $"{(worst <= 0.05f ? "" : "REASON snow along a wall inside; ")}0.6 m inside each wall: worst factor {worst:0.00} at {at}");
                    float fd = SnowShelter.ShelterAt(open + new Vector3(1f, 0f, -1.4f));
                    Result("doorway", fd > 0.05f ? "PASS" : "FAIL", $"{(fd > 0.05f ? "" : "REASON no drift at the opening; ")}0.6 m inside the doorway factor {fd:0.00}");
                    float ff = SnowShelter.ShelterAt(open + new Vector3(-1.4f, 0f, 1.4f));
                    Result("fade", ff < fd ? "PASS" : "FAIL", $"{(ff < fd ? "" : "REASON no fade from the opening; ")}far corner {ff:0.00} vs doorway {fd:0.00}");
                }
                float fo = SnowShelter.ShelterAt(east + new Vector3(12f, 0f, 0f));
                Result("open ground", fo >= 0.99f ? "PASS" : "FAIL", $"{(fo >= 0.99f ? "" : "REASON open ground masked; ")}factor {fo:0.00} 12 m from the huts");
            }
            int n = built.Count;
            foreach (var go in built) { try { if (go != null) { var w = go.GetComponent<WearNTear>(); if (w != null) w.Remove(); else UnityEngine.Object.Destroy(go); } } catch { } }
            yield return null;
            int left = 0; foreach (var go in built) if (go != null) left++;
            if (SnowShelter.On) Result("cleanup", left == 0 ? "PASS" : "FAIL", $"{n - left}/{n} piece(s) removed");
            int compared = pass + fail;
            Say(ctx, $"{Tag}END {(fail == 0 && compared > 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip, {compared} compared, 0 shot(s) in {Time.realtimeSinceStartup - t0:0.0} s");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }
    }
}
