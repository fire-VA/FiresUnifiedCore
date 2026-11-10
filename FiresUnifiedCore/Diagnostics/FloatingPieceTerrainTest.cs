using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using FiresCore.Terrain;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// core_floating_piece_test (Fire 10-27, a sky build levelled the ground 2000 m below it): FDT runner contract, Test / SuperFlat worlds.
    ///  1 pick a vanilla prefab with a levelling TerrainModifier (ZNetScene, prefers one that is already a Piece);
    ///  2 place it as a piece 100 m above the ground: TerrainGuard skips it, the ground height + paint under it are unchanged (SHOT);
    ///  3 place one ON the ground 20 m away: not skipped (vanilla behaviour kept);
    ///  4 cleanup: both removed, the ground under the floating one still unchanged;
    ///  5-7 the player's case: a rock whose TerrainModifier sits on a CHILD (FAP-placeable) - floating skipped, grounded still applies
    ///      (SKIP, not FAIL, when no such prefab is loaded).
    /// Lines: "[CoreFloatPiece] BEGIN ...", "n/7 &lt;step&gt; PASS|FAIL|SKIP - detail", "[CoreFloatPiece] SHOT &lt;path&gt;", "END PASS|FAIL: ...".
    /// </summary>
    internal static class FloatingPieceTerrainTest
    {
        private const string Command = "core_floating_piece_test", Tag = "[CoreFloatPiece] ";
        private const int Steps = 7;
        private static bool s_running;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class FloatingPieceTerrainTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] core_floating_piece_test - a placed piece floating above the ground never levels / paints the terrain below it (admin, Test / SuperFlat worlds)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null || ZNetScene.instance == null) { Say(args.Context, Tag + "no local player / scene"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        var host = new GameObject("FiresFloatingPieceTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Run(args.Context, host, kind));
                    }, isCheat: true);
            }
        }

        private sealed class Runner : MonoBehaviour { }

        private static void Say(Terminal ctx, string line) { ctx?.AddString(line); FiresCore.Logging.FiresLogger.LogInfo(line); }

        private static bool Sample(Vector3 p, out float h, out Color paint)
        {
            paint = Color.clear;
            if (!Heightmap.GetHeight(p, out h)) return false;
            Heightmap hm = Heightmap.FindHeightmap(p);
            if (hm != null) paint = hm.GetPaintMask(p);
            return true;
        }

        private static float PaintDelta(Color a, Color b) => Mathf.Max(Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g)), Mathf.Max(Mathf.Abs(a.b - b.b), Mathf.Abs(a.a - b.a)));

        private static GameObject Place(GameObject prefab, Vector3 pos)
        {
            var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
            if (go.GetComponent<Piece>() == null) go.AddComponent<Piece>();   // a FAP-exposed prefab is a Piece; a bare one is made one for the test
            return go;
        }

        private static void Remove(GameObject go)
        {
            if (go == null) return;
            if (go.GetComponent<ZNetView>() != null) ZNetScene.instance.Destroy(go); else UnityEngine.Object.Destroy(go);
        }

        private static IEnumerator Capture(string name, Action<string> done)
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

        private static IEnumerator Run(Terminal ctx, GameObject host, string kind)
        {
            s_running = true;
            int pass = 0; string firstFail = null;
            GameObject floating = null, grounded = null;
            void Step(int n, string name, bool ok, string detail)
            {
                if (ok) pass++; else if (firstFail == null) firstFail = name;
                Say(ctx, $"{Tag}{n}/{Steps} {name} {(ok ? "PASS" : "FAIL")} - {detail}");
            }
            Say(ctx, $"{Tag}BEGIN world {kind}");

            // 1 a vanilla prefab whose TerrainModifier levels
            GameObject prefab = ZNetScene.instance.m_prefabs
                .Where(p => p != null && p.GetComponent<ZNetView>() != null && p.GetComponentsInChildren<TerrainModifier>(true).Any(m => m.m_level))
                .OrderBy(p => p.GetComponent<Piece>() != null ? 0 : 1).FirstOrDefault();
            if (prefab == null)
            {
                Step(1, "pick_prefab", false, "no ZNetScene prefab carries a levelling TerrainModifier");
                Say(ctx, $"{Tag}END FAIL: no prefab to test with");
                s_running = false; UnityEngine.Object.Destroy(host); yield break;
            }
            var mod0 = prefab.GetComponentsInChildren<TerrainModifier>(true).First(m => m.m_level);
            Step(1, "pick_prefab", true, $"{prefab.name} (Piece {(prefab.GetComponent<Piece>() != null ? "yes" : "added by the test")}, level r {mod0.m_levelRadius:0.0}, paint {mod0.m_paintCleared}, player mod {mod0.m_playerModifiction})");

            Vector3 me = Player.m_localPlayer.transform.position;
            Vector3 a = new Vector3(me.x + 12f, 0f, me.z + 12f), b = new Vector3(me.x + 32f, 0f, me.z + 12f);
            TerrainGuard.GroundHeight(a, out float groundA); a.y = groundA;
            TerrainGuard.GroundHeight(b, out float groundB); b.y = groundB;
            Sample(a, out float hA0, out Color pA0);
            Sample(b, out float hB0, out Color pB0);

            // 2 floating 100 m up
            bool? skippedA = null; float aboveA = 0f;
            void OnDecided(Component c, bool s, float up) { if (floating != null && c != null && c.transform.IsChildOf(floating.transform)) { skippedA = s; aboveA = up; } }
            TerrainGuard.Decided += OnDecided;
            floating = Place(prefab, a + Vector3.up * 100f);
            yield return new WaitForSeconds(1.5f);
            var modA = floating.GetComponentsInChildren<TerrainModifier>(true).FirstOrDefault(m => m.m_level);
            bool skip = modA != null && TerrainGuard.IsSkipped(modA);
            Sample(a, out float hA1, out Color pA1);
            float dh = Mathf.Abs(hA1 - hA0), dp = PaintDelta(pA0, pA1);
            Step(2, "floating_skipped", skip && dh < 0.05f && dp < 0.02f,
                $"{prefab.name} {(skippedA.HasValue ? aboveA : 0f):0.0} m above ground: skipped {skip}; ground under it {hA0:0.00} -> {hA1:0.00} m (d {dh:0.00}), paint d {dp:0.00}");
            string shot = null; yield return Capture("core_floating_piece", x => shot = x);
            if (shot != null) Say(ctx, $"{Tag}SHOT {shot} ground under the floating {prefab.name}");

            // 3 on the ground
            grounded = Place(prefab, b);
            yield return new WaitForSeconds(1.5f);
            var modB = grounded.GetComponentsInChildren<TerrainModifier>(true).FirstOrDefault(m => m.m_level);
            bool skipB = modB != null && TerrainGuard.IsSkipped(modB);
            Sample(b, out float hB1, out _);
            Step(3, "grounded_applied", modB != null && !skipB, $"{prefab.name} on the ground: skipped {skipB} (vanilla applies it); ground {hB0:0.00} -> {hB1:0.00} m");
            TerrainGuard.Decided -= OnDecided;

            // 4 cleanup
            Remove(floating); Remove(grounded);
            yield return new WaitForSeconds(1.5f);
            Sample(a, out float hA2, out Color pA2);
            Sample(b, out float hB2, out _);
            bool gone = floating == null && grounded == null;
            Step(4, "cleanup", gone && Mathf.Abs(hA2 - hA0) < 0.05f && PaintDelta(pA0, pA2) < 0.02f,
                $"both removed {gone}; floating spot {hA0:0.00} -> {hA2:0.00} m; grounded spot {hB0:0.00} -> {hB2:0.00} m");

            // 5-7 the player's case (round #24): a ROCK whose TerrainModifier sits on a CHILD (FAP rejects only root-level modifiers)
            bool ChildMod(TerrainModifier m, GameObject root) => m.gameObject != root && (m.m_level || m.m_paintCleared);
            GameObject rock = ZNetScene.instance.m_prefabs
                .Where(p => p != null && p.GetComponent<ZNetView>() != null && p.GetComponentsInChildren<TerrainModifier>(true).Any(m => ChildMod(m, p)))
                .OrderBy(p => p.GetComponent<Piece>() != null ? 0 : 1)
                .ThenBy(p => p.name.StartsWith("rock", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
            if (rock == null)
            {
                pass += 3;
                Say(ctx, $"{Tag}5/{Steps} rock_child_pick SKIP - no ZNetScene prefab carries a child-level TerrainModifier (FAP absent?)");
                Say(ctx, $"{Tag}6/{Steps} rock_child_floating SKIP - no prefab");
                Say(ctx, $"{Tag}7/{Steps} rock_child_grounded SKIP - no prefab");
            }
            else
            {
                var rm0 = rock.GetComponentsInChildren<TerrainModifier>(true).First(m => ChildMod(m, rock));
                Step(5, "rock_child_pick", true, $"{rock.name} (Piece {(rock.GetComponent<Piece>() != null ? "yes" : "added by the test")}, modifier on child '{rm0.gameObject.name}', level {rm0.m_level} r {rm0.m_levelRadius:0.0}, paint {rm0.m_paintCleared} r {rm0.m_paintRadius:0.0})");
                Vector3 c = new Vector3(me.x + 12f, 0f, me.z + 44f), d = new Vector3(me.x + 32f, 0f, me.z + 44f);
                TerrainGuard.GroundHeight(c, out float groundC); c.y = groundC;
                TerrainGuard.GroundHeight(d, out float groundD); d.y = groundD;
                Sample(c, out float hC0, out Color pC0);
                Sample(d, out float hD0, out Color pD0);

                GameObject rockUp = Place(rock, c + Vector3.up * 100f);
                yield return new WaitForSeconds(1.5f);
                var rmC = rockUp.GetComponentsInChildren<TerrainModifier>(true).FirstOrDefault(m => ChildMod(m, rockUp));
                bool skipC = rmC != null && TerrainGuard.IsSkipped(rmC);
                Sample(c, out float hC1, out Color pC1);
                float dhC = Mathf.Abs(hC1 - hC0), dpC = PaintDelta(pC0, pC1);
                Step(6, "rock_child_floating", skipC && dhC < 0.05f && dpC < 0.02f,
                    $"{rock.name} 100 m up: skipped {skipC}; ground under it {hC0:0.00} -> {hC1:0.00} m (d {dhC:0.00}), paint d {dpC:0.00}");
                string shot2 = null; yield return Capture("core_floating_rock_child", x => shot2 = x);
                if (shot2 != null) Say(ctx, $"{Tag}SHOT {shot2} ground under the floating {rock.name} (child TerrainModifier)");

                GameObject rockDown = Place(rock, d);
                yield return new WaitForSeconds(1.5f);
                var rmD = rockDown.GetComponentsInChildren<TerrainModifier>(true).FirstOrDefault(m => ChildMod(m, rockDown));
                bool skipD = rmD != null && TerrainGuard.IsSkipped(rmD);
                Sample(d, out float hD1, out Color pD1);
                bool changed = Mathf.Abs(hD1 - hD0) >= 0.05f || PaintDelta(pD0, pD1) >= 0.02f;
                Step(7, "rock_child_grounded", rmD != null && !skipD && changed,
                    $"{rock.name} on the ground: skipped {skipD}; ground {hD0:0.00} -> {hD1:0.00} m, paint d {PaintDelta(pD0, pD1):0.00} (vanilla applies it)");

                Remove(rockUp); Remove(rockDown);
                yield return new WaitForSeconds(1.5f);
            }

            Say(ctx, $"{Tag}END {(firstFail == null ? "PASS" : "FAIL")}: {pass}/{Steps} passed{(firstFail != null ? ", first fail " + firstFail : "")}");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }
    }
}
