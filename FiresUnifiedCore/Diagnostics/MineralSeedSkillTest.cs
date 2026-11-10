using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// 0.2.892 ([wishbone], lead 10-27: "no automated test yet for the mineral-seed skill guarantee"): mineral_seed_skill_test, FDT runner
    /// contract, Test / SuperFlat worlds (HomeMapTest.IsTestWorld), admin. Needs [MineralSeeds] Enabled on the server. Steps:
    ///  1 setup: the player's Pickaxes level saved, a copper deposit (rock4_copper) spawned 8 m ahead;
    ///  2 Pickaxes 0: every area of it broken by the player's own hits; expect "[MineralSeeds] full rock ... (mining 0, ...): guarantee p 0 -> none";
    ///  3 Pickaxes 100: a second deposit 8 m to the side broken the same way; expect "... (mining 100, ...): guarantee p 1 -> drop" AND a
    ///    MineralSeed_ item on the ground within 15 m.
    /// The roll lines are written where the roll runs (the server): on a host they are checked here; on a dedicated server step 2 is a SKIP
    /// (read the line in the dedi log) and step 3 checks the seed on the ground only.
    /// Cleanup (always, also when a step throws): the Pickaxes level put back, every item that dropped within 20 m during the test and any
    /// leftover test rock removed.
    /// Lines: "[MineralSeedSkillTest] BEGIN ...", "n/3 <step> PASS|FAIL|SKIP - detail", "END PASS|FAIL: p pass, f fail, s skip, c compared".
    /// </summary>
    internal static class MineralSeedSkillTest
    {
        private const string Command = "mineral_seed_skill_test", Tag = "[MineralSeedSkillTest] ";
        private const string RockPrefab = "rock4_copper_frac";   // 0.2.893 (round #18 setup FAIL): rock4_copper is the whole Destructible that turns into this MineRock5 when hit
        private const int Steps = 3;
        private static bool s_running, s_cleaned;
        private static float s_oldLevel = -1f;
        private static readonly HashSet<int> s_dropsBefore = new HashSet<int>();
        private static readonly List<GameObject> s_rocks = new List<GameObject>();
        private static Vector3 s_center;
        private static Capture s_cap;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class MineralSeedSkillTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] mineral_seed_skill_test - a full copper rock at Pickaxes 0 gives no guarantee seed, at 100 it gives one (admin, Test / SuperFlat worlds)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null) { Say(args.Context, Tag + "no local player"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        Say(args.Context, $"{Tag}world '{world}' accepted ({kind})");
                        var host = new GameObject("FiresMineralSeedSkillTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Guard(args.Context, host));
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
                if (s.Contains("[MineralSeeds]")) lock (m_lines) m_lines.Add(System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " "));
            }
            internal string Find(Func<string, bool> f) { lock (m_lines) return m_lines.LastOrDefault(f); }
        }

        // ---- the player's Pickaxes level (Skills.GetSkill is private) ----
        private static Skills.Skill PickSkill(Player p)
        {
            var skills = p != null ? p.GetSkills() : null;
            var m = AccessTools.Method(typeof(Skills), "GetSkill");
            return skills != null && m != null ? m.Invoke(skills, new object[] { Skills.SkillType.Pickaxes }) as Skills.Skill : null;
        }
        private static void SetPick(Player p, float level) { var s = PickSkill(p); if (s != null) { s.m_level = level; s.m_accumulator = 0f; } }

        private static GameObject SpawnRock(Vector3 at)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(RockPrefab) : null;
            if (prefab == null) return null;
            if (ZoneSystem.instance != null) at.y = ZoneSystem.instance.GetGroundHeight(at);
            var go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity);
            s_rocks.Add(go);
            return go;
        }

        // Every hit area of a MineRock5 struck by the player, one per frame, with a damage that breaks it in one hit.
        private static IEnumerator BreakAll(GameObject rockGo, Player p)
        {
            var rock = rockGo != null ? rockGo.GetComponentInChildren<MineRock5>() : null;
            if (rock == null) yield break;
            yield return new WaitForSeconds(1f);   // its areas are set up in Awake / Start
            var list = Traverse.Create(rock).Field("m_hitAreas").GetValue() as IList;
            if (list == null) yield break;
            var colliders = new List<Collider>();
            foreach (var a in list) { var c = a != null ? AccessTools.Field(a.GetType(), "m_collider")?.GetValue(a) as Collider : null; if (c != null) colliders.Add(c); }
            foreach (var c in colliders)
            {
                if (rock == null || c == null) break;
                var hit = new HitData { m_point = c.bounds.center, m_hitCollider = c, m_toolTier = 100, m_dir = Vector3.down };
                hit.m_damage.m_pickaxe = 100000f;
                hit.SetAttacker(p);
                rock.Damage(hit);
                yield return null;
            }
        }

        private static IEnumerator WaitFor(Func<bool> f, float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until && !f()) yield return null;
        }

        private static List<ItemDrop> NewDrops(float radius) =>
            UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)
                .Where(d => d != null && !s_dropsBefore.Contains(d.GetInstanceID()) && Vector3.Distance(d.transform.position, s_center) <= radius).ToList();

        private static IEnumerator Guard(Terminal ctx, GameObject host)
        {
            s_cleaned = false;
            float started = Time.realtimeSinceStartup;
            var inner = Run(ctx);
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
                Say(ctx, $"{Tag}END FAIL: the test threw, in {Time.realtimeSinceStartup - started:0.0} s (cleanup ran: Pickaxes level, test drops, test rocks)");
            }
            s_running = false;
            if (host != null) UnityEngine.Object.Destroy(host);
        }

        private static void Cleanup()
        {
            if (s_cleaned) return;
            s_cleaned = true;
            try { if (s_oldLevel >= 0f && Player.m_localPlayer != null) SetPick(Player.m_localPlayer, s_oldLevel); } catch { }
            int removed = 0;
            try
            {
                foreach (var d in NewDrops(20f))
                {
                    var nv = d.GetComponent<ZNetView>();
                    if (nv != null && nv.IsValid()) { if (!nv.IsOwner()) nv.ClaimOwnership(); ZNetScene.instance.Destroy(d.gameObject); } else UnityEngine.Object.Destroy(d.gameObject);
                    removed++;
                }
            }
            catch { }
            try
            {
                foreach (var r in s_rocks)
                {
                    if (r == null) continue;
                    var nv = r.GetComponent<ZNetView>();
                    if (nv != null && nv.IsValid()) { if (!nv.IsOwner()) nv.ClaimOwnership(); ZNetScene.instance.Destroy(r); } else UnityEngine.Object.Destroy(r);
                }
            }
            catch { }
            s_rocks.Clear();
            try { if (s_cap != null) BepInEx.Logging.Logger.Listeners.Remove(s_cap); } catch { }
            FiresCore.Logging.FiresLogger.LogInfo($"{Tag}cleanup: Pickaxes back to {s_oldLevel:0.#}, {removed} test drop(s) removed, test rocks removed");
        }

        private static IEnumerator Run(Terminal ctx)
        {
            s_running = true;
            var cap = new Capture(); s_cap = cap;
            BepInEx.Logging.Logger.Listeners.Add(cap);
            int pass = 0, fail = 0, skip = 0, step = 0, compared = 0;
            float started = Time.realtimeSinceStartup;
            void Result(string name, string verdict, string detail) { step++; if (verdict == "PASS") pass++; else if (verdict == "FAIL") fail++; else skip++; if (verdict != "SKIP") compared++; Say(ctx, $"{Tag}{step}/{Steps} {name} {verdict} - {detail}"); }
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: {Steps} steps, ~60 s");

            var p = Player.m_localPlayer;
            bool host = ZNet.instance != null && ZNet.instance.IsServer();
            s_oldLevel = PickSkill(p)?.m_level ?? -1f;
            s_dropsBefore.Clear();
            foreach (var d in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)) if (d != null) s_dropsBefore.Add(d.GetInstanceID());
            Vector3 fwd = p.transform.forward; fwd.y = 0f; fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward;
            s_center = p.transform.position + fwd * 8f;

            // 1 setup
            if (host && !FiresCore.Loot.MineralSeeds.EnabledForTest)
            {
                Result("setup", "SKIP", "[MineralSeeds] Enabled is off on this server (the test does not change config)");
                Result("pickaxes 0", "SKIP", "MineralSeeds off"); Result("pickaxes 100", "SKIP", "MineralSeeds off");
            }
            else
            {
                var rockA = SpawnRock(s_center);
                bool hasRock = rockA != null && rockA.GetComponentInChildren<MineRock5>() != null;
                string why = s_oldLevel < 0f ? "Pickaxes skill not readable" : rockA == null ? $"prefab {RockPrefab} not found" : !hasRock ? $"{RockPrefab} has no MineRock5" : null;
                bool okSetup = why == null;
                Result("setup", okSetup ? "PASS" : "FAIL", (okSetup ? "" : $"REASON {why}; ") + $"Pickaxes was {s_oldLevel:0.#}; {RockPrefab} at ({s_center.x:0},{s_center.z:0}); {(host ? "host" : "dedicated server (the server replies with its roll line)")}");
                if (okSetup)
                {
                    // 2 Pickaxes 0
                    SetPick(p, 0f);
                    yield return BreakAll(rockA, p);
                    Func<string, bool> f0 = l => l.Contains("full rock") && l.Contains("(mining 0,");
                    yield return WaitFor(() => cap.Find(f0) != null, 15f);
                    string l0 = cap.Find(f0);
                    Result("pickaxes 0", l0 != null && l0.Contains("guarantee p 0 -> none") ? "PASS" : "FAIL", l0 != null ? l0 : $"REASON no \"full rock ... (mining 0\" {(host ? "line" : "(server) reply")} within 15 s");

                    // 3 Pickaxes 100 (a second rock, 8 m to the side: the server dedupes per rock position)
                    SetPick(p, 100f);
                    Vector3 side = Vector3.Cross(Vector3.up, fwd).normalized * 8f;
                    var rockB = SpawnRock(s_center + side);
                    yield return BreakAll(rockB, p);
                    Func<string, bool> f1 = l => l.Contains("full rock") && l.Contains("(mining 100,");
                    yield return WaitFor(() => cap.Find(f1) != null, 15f);
                    yield return WaitFor(() => NewDrops(15f).Any(d => d.name.StartsWith("MineralSeed_")), 10f);
                    var seeds = NewDrops(15f).Where(d => d.name.StartsWith("MineralSeed_")).ToList();
                    string l1 = cap.Find(f1);
                    bool lineOk = l1 != null && l1.Contains("guarantee p 1 -> drop");
                    string r1 = !lineOk ? (l1 == null ? "REASON no \"full rock ... (mining 100\" line / (server) reply within 15 s; " : "REASON the roll did not drop; ") : seeds.Count == 0 ? "REASON no MineralSeed_ on the ground within 15 m; " : "";
                    Result("pickaxes 100", lineOk && seeds.Count > 0 ? "PASS" : "FAIL", $"{r1}{l1 ?? "-"}; {seeds.Count} seed(s) on the ground ({string.Join(", ", seeds.Select(s => s.name.Replace("(Clone)", "")))})");
                }
                else { Result("pickaxes 0", "SKIP", "setup failed"); Result("pickaxes 100", "SKIP", "setup failed"); }
            }

            Cleanup();
            Say(ctx, $"{Tag}END {(fail == 0 && compared > 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip, {compared} compared, 0 shot(s) in {Time.realtimeSinceStartup - started:0.0} s (cleanup: Pickaxes level restored, test drops + rocks removed)");
        }
    }
}
