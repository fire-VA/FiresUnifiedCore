using System;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.914 ([wishbone], lead 10-27, Fire "Mountain biomes should get snow build up as well ... a lower Max depth"): core_mountain_snow_test,
    /// FDT runner contract, Test / SuperFlat worlds (HomeMapTest.IsTestWorld), admin. Reads only, changes nothing. Steps:
    ///  1 find Mountain ground: the world generator walked out from the player to 3 km (8 rays, 64 m steps) for a Mountain point;
    ///  2 cap: 121 samples on an 11 x 11 grid (4 m apart) round it - the deepest Mountain-only sample (no Deep North weight) is at or under
    ///    [Snow] Max depth Mountain (m) (+1 cm);
    ///  3 builds up: with snow cover on (Dynamic snow, GlobalCover > 0) the deepest sample is > 0; SKIP when there is no cover now.
    /// Lines: "[CoreMountainSnowTest] BEGIN ...", "n/3 <step> PASS|FAIL|SKIP - detail", "END PASS|FAIL: ...".
    /// </summary>
    internal static class MountainSnowTest
    {
        private const string Command = "core_mountain_snow_test", Tag = "[CoreMountainSnowTest] ";

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class MountainSnowTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] core_mountain_snow_test - Mountain snow builds up to its own cap ([Snow] Max depth Mountain), never beyond (admin, Test / SuperFlat worlds; read-only)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!FiresCore.Diagnostics.HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null || WorldGenerator.instance == null) { Say(args.Context, Tag + "no local player / world generator"); return; }
                        Run(args.Context, kind);
                    }, isCheat: true);
            }
        }

        private static void Say(Terminal ctx, string line) { ctx?.AddString(line); FiresCore.Logging.FiresLogger.LogInfo(line); }

        private static void Run(Terminal ctx, string kind)
        {
            float t0 = Time.realtimeSinceStartup;
            int pass = 0, fail = 0, skip = 0, step = 0;
            void Result(string name, string verdict, string detail) { step++; if (verdict == "PASS") pass++; else if (verdict == "FAIL") fail++; else skip++; Say(ctx, $"{Tag}{step}/3 {name} {verdict} - {detail}"); }
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: 3 steps, ~5 s (full build-up held by SnowField.TestBuildUp) ({kind})");
            try
            {
                float cap = SnowConfig.MountainMaxDepth;
                Vector3 me = Player.m_localPlayer.transform.position;
                // 0.2.934 (lead, round 35: SKIP while FAT found Mountain at (684,525)): the search ran ~800 weight reads in one frame and past
                // the field's 1 ms/frame cell budget every read answered the deferred 0 - it now reads unbudgeted (a bounded one-off), checks
                // the cheap surface biome first, 16 bearings x 32 m, and counts why each Mountain point was rejected
                Vector3 found = Vector3.zero; bool ok = false;
                int seen = 0, lowWeight = 0, dnWeight = 0;
                SnowField.Unbudgeted(() =>
                {
                    for (float d = 0f; d <= 3000f && !ok; d += 32f)
                        for (int a = 0; a < 16 && !ok; a++)
                        {
                            float ang = a * Mathf.PI / 8f;
                            var p = me + new Vector3(Mathf.Cos(ang) * d, 0f, Mathf.Sin(ang) * d);
                            if (WorldGenerator.instance.GetBiome(p.x, p.z) != Heightmap.Biome.Mountain) continue;
                            seen++;
                            if (SnowField.MountainWeight(p) <= 0.99f) { lowWeight++; continue; }
                            if (SnowField.DeepGroundWeight(p) > 0f) { dnWeight++; continue; }
                            found = p; ok = true;
                        }
                    return 0;
                });
                var fat = new Vector3(684f, 0f, 525f);
                Say(ctx, $"{Tag}probe at FAT's Mountain spot: {SnowField.Unbudgeted(() => SnowField.Explain(fat))}");
                Result("find mountain", ok ? "PASS" : "SKIP", ok ? $"Mountain at ({found.x:0},{found.z:0}), {Vector3.Distance(me, found):0} m away ({seen} Mountain point(s) seen, {lowWeight} low weight, {dnWeight} with Deep North weight before it)"
                    : $"REASON no Mountain ground (with no Deep North weight) within 3 km: {seen} Mountain point(s), {lowWeight} with Mountain weight <= 0.99, {dnWeight} with Deep North weight (a snow base / territory over Mountain)");
                if (ok) Say(ctx, $"{Tag}spot {SnowField.Explain(found)}");
                if (!ok) { Result("cap", "SKIP", "no Mountain point"); Result("builds up", "SKIP", "no Mountain point"); }
                else
                {
                    // 0.2.919: full build-up held for the sampling (the TEST hook), so "never beyond the cap" is checked at the deepest case
                    SnowField.TestBuildUp(1f, 30f);
                    Say(ctx, $"{Tag}spot at full build-up {SnowField.Explain(found)}");
                    float deepest = 0f; int n = 0;
                    SnowField.Unbudgeted(() => { for (int x = -5; x <= 5; x++)
                        for (int z = -5; z <= 5; z++)
                        {
                            var p = found + new Vector3(x * 4f, 0f, z * 4f);
                            if (SnowField.DeepGroundWeight(p) > 0f) continue;   // Mountain-only samples
                            deepest = Mathf.Max(deepest, SnowField.DepthAt(p) * SnowField.MaxDepthMetres); n++;
                        } return 0; });
                    Result("cap", deepest <= cap + 0.01f ? "PASS" : "FAIL", $"{(deepest <= cap + 0.01f ? "" : "REASON over the cap; ")}deepest {deepest:0.000} m of {n} Mountain sample(s), cap {cap:0.00} m (cover {SnowField.GlobalCover:0.00}, build-up {SnowField.ViewerBuildUp:0.00})");
                    if (cap <= 0f) Result("builds up", "SKIP", "[Snow] Max depth Mountain (m) is 0 (Mountain buildup off)");
                    else if (!SnowField.Enabled || SnowField.GlobalCover <= 0f) Result("builds up", "SKIP", $"no snow cover now (Dynamic snow {(SnowField.Enabled ? "on" : "off")}, cover {SnowField.GlobalCover:0.00})");
                    else Result("builds up", deepest > 0f ? "PASS" : "FAIL", deepest > 0f ? $"deepest {deepest:0.000} m > 0 with cover {SnowField.GlobalCover:0.00}" : $"REASON no Mountain snow depth with cover {SnowField.GlobalCover:0.00}");
                }
            }
            catch (Exception e) { Say(ctx, $"{Tag}FAIL - the test threw {e.GetType().Name}: {e.Message}"); fail++; }
            finally { try { SnowField.TestBuildUp(0f, 0f); } catch { } }   // 0.2.919: the hold always ends
            int compared = pass + fail;
            Say(ctx, $"{Tag}END {(fail == 0 && compared > 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip, {compared} compared, 0 shot(s) in {Time.realtimeSinceStartup - t0:0.0} s");
        }
    }
}
