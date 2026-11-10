using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using FiresCore.Snow;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// core_creature_trail_test (Fire 10-27, creature snow trails on heightmap ground): FDT runner contract, Test / SuperFlat worlds.
    ///  1 config "Creature snow trails" is ON (never forced: off = warn, and the trail steps fail);
    ///  2 a heightmap Deep North spot with deep snow near the player, else at Deep North land found from the generator near (0, 9000) (teleport), not on a FAT zone (SKIP with the reason when there is none);
    ///  3 spawn Deer, Boar, Troll (AI off, no aggro) on parallel lines and walk each 10 m north (Character.SetMoveDir);
    ///  4-6 a trail along each line: the heightmap paint changed at the line's midpoint and its width = the creature's applied width +-0.3 m;
    ///  7 SHOT from above, cleanup.
    /// Lines: "[CoreCreatureTrailTest] BEGIN ...", "n/7 &lt;step&gt; PASS|FAIL|SKIP - detail", "[CoreCreatureTrailTest] SHOT &lt;path&gt;", "END PASS|FAIL".
    /// </summary>
    internal static class CreatureTrailTest
    {
        private const string Command = "core_creature_trail_test", Tag = "[CoreCreatureTrailTest] ";
        private const int Steps = 7;
        private static bool s_running;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class CreatureTrailTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] core_creature_trail_test - deer, boar and troll carve snow trails as wide as they are on heightmap Deep North ground (admin, Test / SuperFlat worlds)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null || ZNetScene.instance == null) { Say(args.Context, Tag + "no local player / scene"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        var host = new GameObject("FiresCreatureTrailTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Run(args.Context, host, kind));
                    }, isCheat: true);
            }
        }

        private sealed class Runner : MonoBehaviour { }

        private static void Say(Terminal ctx, string line) { ctx?.AddString(line); FiresCore.Logging.FiresLogger.LogInfo(line); }

        // The heightmap paint at p (all channels; Deep North trail paint is vanilla's snow-mode cultivation channel).
        private static Color Paint(Vector3 p)
        {
            Heightmap hm = Heightmap.FindHeightmap(p);
            return hm != null ? hm.GetPaintMask(p) : Color.clear;
        }
        private static float Delta(Color a, Color b) => Mathf.Max(Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g)), Mathf.Max(Mathf.Abs(a.b - b.b), Mathf.Abs(a.a - b.a)));

        // Deep snow on heightmap ground at p, the trail's own test minus the character parts.
        private static bool SpotOk(Vector3 p, out string why)
        {
            if (SnowFat.OwnsZone(ZoneSystem.GetZone(p))) { why = "FAT owns the zone"; return false; }
            Heightmap hm = Heightmap.FindHeightmap(p);
            if (hm == null) { why = "no heightmap"; return false; }
            bool dn = WorldGenerator.instance != null && WorldGenerator.instance.GetBiome(p) == Heightmap.Biome.DeepNorth;
            float vanilla = dn ? hm.GetCultivationMask(p) * 2f : 0f;
            float core = SnowField.DeepGroundAt(p) ? SnowField.DepthAt(p) * 2f : 0f;
            why = $"Deep North {dn}, vanilla snow {vanilla:0.00} m, Core deep snow {core:0.00} m";
            return Mathf.Max(vanilla, core) > 0.5f;
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

        private sealed class Row { public string Name; public Character C; public Vector3 Start; public float Width; }

        private static IEnumerator Run(Terminal ctx, GameObject host, string kind)
        {
            s_running = true;
            int pass = 0; string firstFail = null;
            var rows = new List<Row>();
            var me = Player.m_localPlayer;
            Vector3 home = me.transform.position;
            void Step(int n, string name, bool ok, string detail)
            {
                if (ok) pass++; else if (firstFail == null) firstFail = name;
                Say(ctx, $"{Tag}{n}/{Steps} {name} {(ok ? "PASS" : "FAIL")} - {detail}");
            }
            void SkipRest(int from, string why)
            {
                for (int n = from; n <= Steps; n++) { pass++; Say(ctx, $"{Tag}{n}/{Steps} step{n} SKIP - {why}"); }
            }
            Say(ctx, $"{Tag}BEGIN world {kind}");

            // 1 config: never forced
            bool on = CreatureSnowTrail.On;
            if (!on) FiresCore.Logging.FiresLogger.LogWarning($"{Tag}config \"Creature snow trails\" is OFF - set it true in Core's cfg (game closed); the test does not force it");
            Step(1, "config_on", on, on ? "Creature snow trails = true" : "Creature snow trails = false (not forced)");

            // 2 a heightmap deep-snow spot: rings of 8 bearings out to 60 m round `centre`
            Vector3 spot = Vector3.zero; string why = "none checked"; bool found = false;
            void SearchAround(Vector3 centre)
            {
                for (float r = 0f; r <= 60f && !found; r += 6f)
                    for (int b = 0; b < (r == 0f ? 1 : 8) && !found; b++)
                    {
                        float a = b * Mathf.PI / 4f;
                        var p = new Vector3(centre.x + Mathf.Cos(a) * r, 0f, centre.z + Mathf.Sin(a) * r);
                        if (!Heightmap.GetHeight(p, out float h)) continue;
                        p.y = h;
                        if (SpotOk(p, out why)) { spot = p; found = true; }
                    }
            }
            SearchAround(home);
            // Core 0.2.9xx (CoreSnowTest, a vanilla-generated world): nothing near the player, so go to Deep North land.
            // Vanilla 1.0.15 WorldGenerator.IsDeepnorth (WorldGenerator.cs:657-661): |(x, z + 4000)| > 12000 + WorldAngle*100,
            // i.e. z > ~8100-8200 at x = 0, inside the 10 km world. Land isn't guaranteed for every seed, so scan the generator
            // (pure functions, no zones needed) for DeepNorth with ground >= 8 m above the water, nearest to (0, 9000) first.
            if (!found && WorldGenerator.instance != null && ZoneSystem.instance != null)
            {
                float water = ZoneSystem.instance.m_waterLevel;
                Vector3 target = Vector3.zero; bool land = false;
                for (float ring = 0f; ring <= 2000f && !land; ring += 100f)
                    for (int b = 0; b < (ring == 0f ? 1 : 24) && !land; b++)
                    {
                        float a = b * Mathf.PI * 2f / 24f;
                        float x = Mathf.Cos(a) * ring, z = 9000f + Mathf.Sin(a) * ring;
                        if (new Vector2(x, z).magnitude > 9800f) continue;
                        if (WorldGenerator.instance.GetBiome(x, z) != Heightmap.Biome.DeepNorth) continue;
                        float gh = WorldGenerator.instance.GetHeight(x, z);
                        if (gh < water + 8f) continue;
                        target = new Vector3(x, gh, z); land = true;
                    }
                if (land)
                {
                    Say(ctx, $"{Tag}no spot within 60 m of the player; Deep North land at ({target.x:0},{target.z:0}) height {target.y:0.0} (seed '{ZNet.World?.m_seedName}'): teleporting there");
                    me.TeleportTo(target + Vector3.up * 2f, me.transform.rotation, distantTeleport: true);
                    float until = Time.realtimeSinceStartup + 45f;
                    while (Time.realtimeSinceStartup < until && (me.IsTeleporting() || !Heightmap.GetHeight(target, out _))) yield return null;
                    yield return new WaitForSecondsRealtime(3f);   // zones + Core's SnowField settle
                    SearchAround(target);
                }
                else why = $"no Deep North land >= 8 m above the water within 2 km of (0, 9000) for seed '{ZNet.World?.m_seedName}'";
            }
            if (!found)
            {
                Say(ctx, $"{Tag}2/{Steps} heightmap_deep_snow SKIP - no heightmap Deep North spot with deep snow (near the player or at the Deep North probe; last: {why})");
                pass++; SkipRest(3, "no spot");
                Say(ctx, $"{Tag}END {(firstFail == null ? "PASS" : "FAIL")}: {pass}/{Steps} passed{(firstFail != null ? ", first fail " + firstFail : "")}");
                s_running = false; UnityEngine.Object.Destroy(host); yield break;
            }
            Step(2, "heightmap_deep_snow", true, $"spot ({spot.x:0.0},{spot.z:0.0}): {why}");

            // 3 spawn + walk 10 m north
            string[] names = { "Deer", "Boar", "Troll" };
            float[] offs = { -6f, -2f, 5f };   // the troll gets extra room
            for (int i = 0; i < names.Length; i++)
            {
                var prefab = ZNetScene.instance.GetPrefab(names[i]);
                if (prefab == null) continue;
                var s = spot + new Vector3(offs[i], 0f, -5f);
                if (Heightmap.GetHeight(s, out float hs)) s.y = hs + 0.2f;
                var go = UnityEngine.Object.Instantiate(prefab, s, Quaternion.identity);
                foreach (var ai in go.GetComponents<BaseAI>()) ai.enabled = false;   // no aggro, no wander: walked by the test
                var c = go.GetComponent<Character>();
                rows.Add(new Row { Name = names[i], C = c, Start = s, Width = CreatureSnowTrail.Snap(CreatureSnowTrail.AppliedWidth(CreatureSnowTrail.ComputedWidth(go))) });
            }
            yield return new WaitForSeconds(1.5f);
            var before = new Dictionary<Row, Color[]>();
            foreach (var r in rows) if (r.C != null)
            {
                var row = new Color[41];
                Vector3 mid = r.Start + new Vector3(0f, 0f, 5f);
                for (int k = 0; k < 41; k++) row[k] = Paint(mid + new Vector3((k - 20) * 0.1f, 0f, 0f));
                before[r] = row;
            }
            float t0 = Time.time;
            while (Time.time - t0 < 20f)
            {
                bool any = false;
                foreach (var r in rows)
                {
                    if (r.C == null) continue;
                    float done = r.C.transform.position.z - r.Start.z;
                    if (done >= 10f) { r.C.SetMoveDir(Vector3.zero); continue; }
                    any = true;
                    r.C.SetMoveDir(Vector3.forward);
                }
                if (!any) break;
                yield return null;
            }
            foreach (var r in rows) if (r.C != null) r.C.SetMoveDir(Vector3.zero);
            var moved = new System.Text.StringBuilder();
            bool allMoved = rows.Count == names.Length;
            foreach (var r in rows)
            {
                float d = r.C != null ? r.C.transform.position.z - r.Start.z : 0f;
                if (d < 9f) allMoved = false;
                moved.Append($"{r.Name} {d:0.0} m (width {r.Width:0.0}, computed {(r.C != null ? CreatureSnowTrail.ComputedWidth(r.C.gameObject) : 0f):0.00}); ");
            }
            Step(3, "walk_10m", allMoved, moved.ToString());
            yield return new WaitForSeconds(2f);

            // 4-6 a trail across each line's midpoint: the span where the paint changed
            int n = 4;
            foreach (string nm in names)
            {
                var r = rows.Find(x => x.Name == nm);
                if (r == null || !before.ContainsKey(r)) { Step(n++, $"trail_{nm}", false, $"{nm} not spawned"); continue; }
                Vector3 mid = r.Start + new Vector3(0f, 0f, 5f);
                int lo = -1, hi = -1;
                for (int k = 0; k < 41; k++)
                    if (Delta(before[r][k], Paint(mid + new Vector3((k - 20) * 0.1f, 0f, 0f))) > 0.05f) { if (lo < 0) lo = k; hi = k; }
                float w = lo < 0 ? 0f : (hi - lo + 1) * 0.1f;
                bool centre = lo >= 0 && lo <= 20 && hi >= 20;
                Step(n++, $"trail_{nm}", centre && Mathf.Abs(w - r.Width) <= 0.3f,
                    $"{nm}: paint changed across {w:0.0} m at the midpoint (expected {r.Width:0.0} m +-0.3, centre crossed {centre})");
            }

            // 7 SHOT from above + cleanup
            me.transform.position = spot + new Vector3(0f, 14f, -2f);
            me.transform.rotation = Quaternion.LookRotation((spot - me.transform.position).normalized);
            yield return new WaitForSeconds(1f);
            string shot = null; yield return Capture("core_creature_trails", x => shot = x);
            if (shot != null) Say(ctx, $"{Tag}SHOT {shot} deer, boar and troll trails from above");
            foreach (var r in rows) if (r.C != null) { var nv = r.C.GetComponent<ZNetView>(); if (nv != null) ZNetScene.instance.Destroy(r.C.gameObject); else UnityEngine.Object.Destroy(r.C.gameObject); }
            me.transform.position = home;
            yield return new WaitForSeconds(1f);
            Step(7, "shot_cleanup", shot != null, $"shot {(shot != null ? "taken" : "missing")}, {rows.Count} creature(s) removed, player back home");

            Say(ctx, $"{Tag}END {(firstFail == null ? "PASS" : "FAIL")}: {pass}/{Steps} passed{(firstFail != null ? ", first fail " + firstFail : "")}");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }
    }
}
