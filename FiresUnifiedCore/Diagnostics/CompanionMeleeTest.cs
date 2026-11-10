using System;
using System.Collections;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using FiresCore.Npc;
using FiresCore.Npc.Combat;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// 0.2.938 ([ghost], Fire 10-27: "companion attacks do damage the second they swing rather than actually using the weapons, actual attack
    /// speed or collision from the weapon trail"): core_companion_melee_test (FDT runner contract, Test / SuperFlat worlds). A homestead
    /// companion with a melee weapon fights a target (a Greydwarf spawned 4 m away whose health is raised so it lasts the 30 s window -
    /// the companion's own state is never changed) while the player is in god mode; CombatHitTrace counts every swing start and every hit.
    ///  1 goto;  2 setup (armed companion, target);
    ///  3 hit timing: every hit lands at least 0.15 s after its swing started (the weapon's hit frame, never at the swing start);
    ///  4 one hit per swing: hits per minute within +-15 % of swings per minute (the weapon's own attack speed sets the swings);
    ///  5 SHOT.
    /// The HUD is never touched (Fire 10-14). Cleanup: the target removed, god mode back.
    /// </summary>
    internal static class CompanionMeleeTest
    {
        private const string Command = "core_companion_melee_test", Tag = "[MeleeTest] ";
        private const int Steps = 5;
        private const float Window = 30f, MinHitDelay = 0.15f;
        private static bool s_running;
        private static readonly ManualLogSource s_log = BepInEx.Logging.Logger.CreateLogSource("FiresUnifiedCore");

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class CompanionMeleeTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] core_companion_melee_test - a companion's melee hits land at the weapon's hit frame (not at the swing start), one hit per swing at the weapon's attack speed (admin, Test / SuperFlat worlds)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null) { Say(args.Context, Tag + "no local player"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        Say(args.Context, $"{Tag}world '{world}' accepted ({kind})");
                        var host = new GameObject("FiresMeleeTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Run(args.Context, host));
                    }, isCheat: true);
            }
        }

        private sealed class Runner : MonoBehaviour { }
        private static void Say(Terminal ctx, string line) { ctx?.AddString(line); s_log.LogInfo(line); }
        private static float Ground(Vector3 p) => ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(p) : p.y;

        private static IEnumerator Shot(string name, Action<string> done)
        {
            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests", "shots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            yield return new WaitForEndOfFrame();   // the HUD stays as the player has it (Fire 10-14)
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

        private static IEnumerator Run(Terminal ctx, GameObject host)
        {
            s_running = true;
            int pass = 0, fail = 0, skip = 0, step = 0, shots = 0;
            float started = Time.realtimeSinceStartup;
            void Result(string name, bool ok, string detail) { step++; if (ok) pass++; else fail++; Say(ctx, $"{Tag}{step}/{Steps} {name} {(ok ? "PASS" : "FAIL")} - {detail}"); }
            void Skip(string name, string detail) { step++; skip++; Say(ctx, $"{Tag}{step}/{Steps} {name} SKIP - {detail}"); }
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: {Steps} steps, ~70 s");
            var pl0 = Player.m_localPlayer; bool godWas = pl0 != null && pl0.InGodMode();
            if (pl0 != null) pl0.SetGodMode(true);
            Character foe = null;

            try { var p = Player.m_localPlayer; Vector3 spot = new Vector3(301f, 50f, 952f); spot.y = Ground(spot) + 0.5f; p.TeleportTo(spot, p.transform.rotation, true); }
            catch (Exception e) { Say(ctx, $"{Tag}goto threw {e.Message}"); }
            float until = Time.realtimeSinceStartup + 40f;
            while (Time.realtimeSinceStartup < until && Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting()) yield return null;
            yield return new WaitForSecondsRealtime(5f);
            var pl = Player.m_localPlayer;
            float off = pl != null ? Utils.DistanceXZ(pl.transform.position, new Vector3(301f, 0f, 952f)) : 999f;
            Result("goto 301 952", off < 15f, $"player {off:0.0} m from the spot");

            long me = pl != null ? pl.GetPlayerID() : 0;
            bool Melee(CompanionController x) { var w = x.GetComponent<Humanoid>()?.GetCurrentWeapon(); return w != null && w.m_shared.m_skillType != Skills.SkillType.Bows && w.m_shared.m_skillType != Skills.SkillType.Unarmed && w.m_shared.m_skillType != Skills.SkillType.Crossbows && w.m_shared.m_skillType != Skills.SkillType.ElementalMagic && w.m_shared.m_skillType != Skills.SkillType.BloodMagic; }
            var c = CompanionController.AllCompanions.Where(x => x != null && x.isTamed && x.ownerPlayerId == me && x.GetCompanionAI() != null && x.GetComponent<ZNetView>()?.IsOwner() == true && Melee(x))
                .OrderBy(x => pl != null ? Vector3.Distance(x.transform.position, pl.transform.position) : 0f).FirstOrDefault();
            var body = c != null ? c.GetComponent<Character>() : null;
            if (c != null)
            {
                var gp = ZNetScene.instance?.GetPrefab("Greydwarf");
                if (gp != null) foe = UnityEngine.Object.Instantiate(gp, c.transform.position + c.transform.forward * 4f + Vector3.up * 0.3f, Quaternion.identity).GetComponent<Character>();
                if (foe != null) { foe.SetMaxHealth(100000f); foe.SetHealth(100000f); c.GetCompanionAI().ForceTarget(foe); }
            }
            string weapon = c != null ? c.GetComponent<Humanoid>()?.GetCurrentWeapon()?.m_shared?.m_name : null;
            Result("setup", c != null && foe != null, $"companion {c?.companionName ?? "NONE with a melee weapon (owned by this peer)"} with {weapon ?? "-"}; target {(foe != null ? "Greydwarf 4 m, health 100000 (lasts the window)" : "none")}");

            if (c == null || foe == null) { Skip("hit timing", "no setup"); Skip("one hit per swing", "no setup"); }
            else
            {
                float t0 = Time.time; int d0 = CombatHitTrace.DelaysOf(body).Count;
                while (Time.time - t0 < Window)
                {
                    if (foe == null || foe.IsDead()) break;
                    if (!c.GetCompanionAI().IsInCombat) c.GetCompanionAI().ForceTarget(foe);
                    yield return null;
                }
                float secs = Time.time - t0;
                var delays = CombatHitTrace.DelaysOf(body).Skip(d0).ToList();
                int swings = CombatHitTrace.SwingsSince(body, t0), hits = CombatHitTrace.HitsSince(body, t0);
                if (delays.Count == 0) Result("hit timing", false, $"no hit in {secs:0} s ({swings} swing start(s)) - see the [CombatHit] lines");
                else Result("hit timing", delays.Min() >= MinHitDelay, $"{delays.Count} hit(s): earliest {delays.Min():0.00} s, mean {delays.Average():0.00} s, latest {delays.Max():0.00} s after the swing start (needs >= {MinHitDelay:0.00} s)");
                float spm = swings * 60f / Mathf.Max(1f, secs), hpm = hits * 60f / Mathf.Max(1f, secs);
                bool match = swings > 0 && Mathf.Abs(hpm - spm) <= 0.15f * spm;
                Result("one hit per swing", match, $"{swings} swing(s) and {hits} hit(s) in {secs:0} s: {spm:0.0} swings/min vs {hpm:0.0} hits/min (within 15 %: {match})");
            }

            string got = null; yield return Shot("core_companion_melee", x => got = x);
            if (got != null) { shots++; Say(ctx, $"{Tag}SHOT {got}"); }
            Result("shot", got != null, got ?? "no file");

            try { if (foe != null) { var nv = foe.GetComponent<ZNetView>(); if (nv != null && nv.IsValid()) { nv.ClaimOwnership(); nv.Destroy(); } } } catch { }
            try { c?.GetCompanionAI()?.ClearForceTarget(); } catch { }
            if (Player.m_localPlayer != null) Player.m_localPlayer.SetGodMode(godWas);
            Say(ctx, $"{Tag}END {(fail == 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip, {pass + fail} compared, {shots} shot(s) in {Time.realtimeSinceStartup - started:0.0} s");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }
    }
}
