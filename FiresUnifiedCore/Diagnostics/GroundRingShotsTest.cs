using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FiresCore.Npc.Archetypes.Effects;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// Visual proof for the AoE rings (a solid circle that grows from the centre):
    /// groundring_shots_test. Admin, Test worlds only (the team's IsTestWorld rule, as FMO BossPhaseTest). Draws
    /// one ring of each kind (Damage, Heal, Buff) 7 m in front of the player through GroundRingManager.Show (the same call every
    /// mod uses), then one big Damage ring and an aiming preview. For each it measures the drawn disc's size from its mesh bounds
    /// right after Show, mid-grow and when grown, and takes a native-resolution screenshot at each moment (HUD left as it is).
    /// Lines: BEGIN / "n/N step PASS|FAIL - detail" (+ "shot: path (WxH)") / END PASS|FAIL; 0 compared is a FAIL. Nothing to clean
    /// up: a ring releases itself when its time ends (all are released at the end anyway).
    /// </summary>
    internal static class GroundRingShotsTest
    {
        private const string Command = "groundring_shots_test";
        private const string Tag = "[GroundRingShotsTest] ";
        private static bool s_running;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class GroundRingShotsTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] groundring_shots_test - the solid AoE disc: grows from the centre, per kind, with screenshots (admin, Test worlds only)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!IsTestWorld(world)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test world"); return; }
                        if (Player.m_localPlayer == null) { Say(args.Context, Tag + "no local player"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        var host = new GameObject("FiresGroundRingShotsTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Run(args.Context, host));
                    }, isCheat: true);
            }
        }

        private sealed class Runner : MonoBehaviour { }

        internal static bool IsTestWorld(string world) =>
            !string.IsNullOrEmpty(world) && (world.StartsWith("ATomTest", StringComparison.OrdinalIgnoreCase)
                || world.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0 || world.StartsWith("AB", StringComparison.Ordinal));

        private static void Say(Terminal ctx, string line)
        {
            ctx?.AddString(line);
            FiresCore.Logging.FiresLogger.LogInfo(line);
        }

        private struct Case { public string Name; public GroundRingKind Kind; public float Radius, Duration; public bool Aiming; }

        private static IEnumerator Run(Terminal ctx, GameObject host)
        {
            s_running = true;
            var cases = new List<Case>
            {
                new Case { Name = "damage_r4", Kind = GroundRingKind.Damage, Radius = 4f, Duration = 3f },
                new Case { Name = "heal_r4", Kind = GroundRingKind.Heal, Radius = 4f, Duration = 3f },
                new Case { Name = "buff_r4", Kind = GroundRingKind.Buff, Radius = 4f, Duration = 3f },
                new Case { Name = "damage_r8", Kind = GroundRingKind.Damage, Radius = 8f, Duration = 3f },
                new Case { Name = "aiming_r3", Kind = GroundRingKind.Damage, Radius = 3f, Duration = 3f, Aiming = true },
            };
            int planned = cases.Count, pass = 0, fail = 0, compared = 0;
            var shots = new List<string>();
            float started = Time.realtimeSinceStartup;
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} on client: {planned} steps (3 shots each), ~20 s");
            try
            {
                for (int i = 0; i < cases.Count; i++)
                {
                    var c = cases[i];
                    var player = Player.m_localPlayer;
                    if (player == null) { fail++; Say(ctx, $"{Tag}{i + 1}/{planned} {c.Name} FAIL - no local player"); continue; }
                    Vector3 fwd = GameCamera.instance != null ? GameCamera.instance.transform.forward : player.transform.forward;
                    fwd.y = 0f; fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward;
                    float ahead = Mathf.Max(7f, c.Radius + 2f);
                    Vector3 center = player.transform.position + fwd * ahead;
                    if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(center, out float gy)) center.y = gy;

                    var ring = GroundRingManager.Show(c.Kind, center, c.Radius, c.Duration, 0L, c.Aiming);
                    if (ring == null) { fail++; Say(ctx, $"{Tag}{i + 1}/{planned} {c.Name} FAIL - GroundRingManager.Show returned null (visibility or shader)"); continue; }

                    var moments = new[] { ("start", 0.05f), ("mid", 0.3f), ("full", 1.2f) };
                    var sizes = new List<string>(); var diam = new List<float>();
                    float t0 = Time.time;
                    foreach (var (label, at) in moments)
                    {
                        while (Time.time - t0 < at) yield return null;
                        yield return new WaitForEndOfFrame();
                        float d = DrawnDiameter(ring);
                        diam.Add(d); sizes.Add($"{label} {d:0.0} m");
                        string shot = null;
                        yield return Capture($"groundring_{c.Name}_{label}", p => shot = p);
                        if (shot != null) { shots.Add(shot); Say(ctx, $"{Tag}{c.Name} {label} shot: {shot}"); }
                        else Say(ctx, $"{Tag}{c.Name} {label} shot: NOT WRITTEN");
                    }
                    compared++;
                    float full = c.Radius * 2f;
                    bool fullOk = Mathf.Abs(diam[2] - full) <= full * 0.06f;
                    bool growOk = c.Aiming ? Mathf.Abs(diam[0] - full) <= full * 0.06f : diam[0] < full * 0.5f && diam[1] > diam[0] && diam[1] < diam[2] + 0.01f;
                    bool ok = fullOk && growOk;
                    if (ok) pass++; else fail++;
                    Say(ctx, $"{Tag}{i + 1}/{planned} {c.Name} {(ok ? "PASS" : "FAIL")} - {c.Kind} r={c.Radius:0.#} m {(c.Aiming ? "aiming (full at once)" : "cast (grows)")}: drawn diameter {string.Join(", ", sizes)} (full = {full:0.0} m); {ahead:0.#} m ahead of the player");
                    GroundRingManager.Release(ring);
                    yield return new WaitForSeconds(0.3f);
                }
            }
            finally { s_running = false; }
            if (compared == 0) fail++;
            Say(ctx, $"{Tag}END {(fail == 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, 0 skip, {compared} compared, {shots.Count} shot(s) in {Time.realtimeSinceStartup - started:0.0} s");
            UnityEngine.Object.Destroy(host);
        }

        // the disc's drawn width from its mesh bounds (world space), across x and z
        private static float DrawnDiameter(GroundRing ring)
        {
            var mf = ring != null ? ring.GetComponent<MeshFilter>() : null;
            if (mf == null || mf.sharedMesh == null) return 0f;
            var b = mf.sharedMesh.bounds;
            return Mathf.Max(b.size.x, b.size.z);
        }

        private static IEnumerator Capture(string name, Action<string> done)
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
