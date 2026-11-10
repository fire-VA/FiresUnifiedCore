using System;
using System.Collections;
using System.Collections.Generic;
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
    /// 0.2.92x ([ghost], Fire 10-27 movement plan - COMBAT set): core_companion_moves_test (FDT runner contract, Test / SuperFlat worlds).
    /// Combat moves on for the test only (CompanionCombatMoves.s_testOn, never saved to the cfg); the player in god mode while a hostile is out.
    /// A homestead companion faces a spawned Greydwarf 3 m away on open ground 25 m east of the SFSC3 homestead, and each move is asked once
    /// through CompanionCombatMoves.Do (the same entry the fight chooser uses): its line, PASS when it ran (the move's own why-line shows its cost),
    /// SKIP when its API is not installed, the move is player-only in that API (VikHavn grab / throw / charged secondary in ApiVersion 1), or the companion lacks what it needs (no shield / not unarmed), FAIL when refused otherwise.
    ///  1 goto;  2 setup;  3 slide;  4 double jump (in the air; the RPG class style if there);  5 air dash (in the air);  6 shield bash;
    ///  7 kick;  8 grab;  9 throw;  10 taunt;  11 charged secondary;  12 emergency evasion attached to level 20+ companions;  13 SHOT.
    /// Cleanup: the hostile killed, the companion back, god mode / flag / HUD back.
    /// </summary>
    internal static class CompanionMovesTest
    {
        private const string Command = "core_companion_moves_test", Tag = "[MovesTest] ";
        private const int Steps = 13;
        private static bool s_running;
        private static readonly ManualLogSource s_log = BepInEx.Logging.Logger.CreateLogSource("FiresUnifiedCore");
        private static readonly AccessTools.FieldRef<Hud, bool> s_hudHidden = AccessTools.FieldRefAccess<Hud, bool>("m_userHidden");

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class CompanionMovesTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] core_companion_moves_test - a companion uses each combat move once (slide, double jump / class style, air dash, shield bash, kick, grab, throw, taunt, charged secondary) and levelled companions get emergency evasion (admin, Test / SuperFlat worlds)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null) { Say(args.Context, Tag + "no local player"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        Say(args.Context, $"{Tag}world '{world}' accepted ({kind})");
                        var host = new GameObject("FiresMovesTest");
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
            yield return new WaitForEndOfFrame();   // the HUD stays as the player has it (Fire 10-14: tests never hide the HUD, shots included)
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
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: {Steps} steps, ~120 s");
            bool hudWas = Hud.instance != null && s_hudHidden(Hud.instance);
            var pl0 = Player.m_localPlayer; bool godWas = pl0 != null && pl0.InGodMode();
            // 0.2.937 (Fire 10-27: "our tests are turning off our HUD"; Fire 10-14: tests never hide it): the HUD is never touched
            if (pl0 != null) pl0.SetGodMode(true);
            CompanionCombatMoves.s_testOn = true;
            Character foe = null; CompanionController c = null; Vector3 back = Vector3.zero;

            try { var p = Player.m_localPlayer; Vector3 spot = new Vector3(301f, 50f, 952f); spot.y = Ground(spot) + 0.5f; p.TeleportTo(spot, p.transform.rotation, true); }
            catch (Exception e) { Say(ctx, $"{Tag}goto threw {e.Message}"); }
            float until = Time.realtimeSinceStartup + 40f;
            while (Time.realtimeSinceStartup < until && Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting()) yield return null;
            yield return new WaitForSecondsRealtime(5f);
            var pl = Player.m_localPlayer;
            float off = pl != null ? Utils.DistanceXZ(pl.transform.position, new Vector3(301f, 0f, 952f)) : 999f;
            Result("goto 301 952", off < 15f, $"player {off:0.0} m from the spot");

            long me = pl != null ? pl.GetPlayerID() : 0;
            c = CompanionController.AllCompanions.Where(x => x != null && x.isTamed && x.ownerPlayerId == me && x.GetCompanionAI() != null && x.GetComponent<ZNetView>()?.IsOwner() == true)
                .OrderBy(x => pl != null ? Vector3.Distance(x.transform.position, pl.transform.position) : 0f).FirstOrDefault();
            Vector3 area = pl != null ? pl.transform.position + new Vector3(25f, 0f, 0f) : Vector3.zero; area.y = Ground(area);
            if (c != null) { back = c.transform.position; c.TeleportToDestination(area + Vector3.up * 0.3f); }
            yield return new WaitForSeconds(3f);
            var gp = ZNetScene.instance?.GetPrefab("Greydwarf");
            if (gp != null && c != null) foe = UnityEngine.Object.Instantiate(gp, area + c.transform.forward * 3f + Vector3.up * 0.3f, Quaternion.identity).GetComponent<Character>();
            var body = c != null ? c.GetComponent<Character>() : null;
            Result("setup", c != null && foe != null, $"companion {c?.companionName ?? "NONE (owned by this peer)"}; foe {(foe != null ? "Greydwarf 3 m" : "none")}");

            IEnumerator Move(string move, bool inAir, Func<string> lacks)
            {
                if (c == null || foe == null) { Skip(move, "no setup"); yield break; }
                string l = lacks != null ? lacks() : null;
                if (l != null) { Skip(move, l); yield break; }
                if (inAir) { body.Jump(); yield return new WaitForSeconds(0.3f); }
                bool ok = CompanionCombatMoves.Do(c, move, foe, out string why);
                yield return new WaitForSeconds(1.2f);
                if (!ok && why != null && (why == "API not installed" || why.StartsWith("player only"))) Skip(move, $"not available to companions: {why}");
                else Result(move, ok, $"{(ok ? "ran" : "refused")}{(why != null ? ": " + why : "")}");
                yield return new WaitForSeconds(1.5f);
            }
            var h = body as Humanoid;
            Func<string> needShield = () => h != null && h.GetLeftItem() != null && h.GetLeftItem().m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield ? null : "no shield equipped";
            Func<string> needUnarmed = () => h != null && (h.GetCurrentWeapon() == null || h.GetCurrentWeapon().m_shared.m_skillType == Skills.SkillType.Unarmed) ? null : "not unarmed (has a weapon)";

            yield return Move("slide", false, null);
            yield return Move("double jump", true, null);
            yield return Move("air dash", true, null);
            yield return Move("shield bash", false, needShield);
            yield return Move("kick", false, needUnarmed);
            yield return Move("grab", false, needUnarmed);
            yield return Move("throw", false, needUnarmed);
            yield return Move("taunt", false, null);
            yield return Move("charged secondary", false, null);

            // 12 emergency evasion on levelled companions
            CompanionCombatMoves.Attach();
            var lv = CompanionController.AllCompanions.Where(x => x != null && x.isTamed && x.ownerPlayerId == me && x.GetComponent<ZNetView>()?.IsOwner() == true
                && (x.GetComponent<CompanionProgression>()?.Level ?? 1) >= FiresCore.Npc.Archetypes.AbilityUnlockSystem.EVASION_UNLOCK_LEVEL).ToList();
            if (lv.Count == 0) Skip("emergency evasion attached", $"no companion at level {FiresCore.Npc.Archetypes.AbilityUnlockSystem.EVASION_UNLOCK_LEVEL}+");
            else Result("emergency evasion attached", lv.All(x => x.GetComponent<FiresCore.Npc.Combat.EmergencyEvasion.EmergencyEvasionManager>() != null), $"{lv.Count(x => x.GetComponent<FiresCore.Npc.Combat.EmergencyEvasion.EmergencyEvasionManager>() != null)}/{lv.Count} level {FiresCore.Npc.Archetypes.AbilityUnlockSystem.EVASION_UNLOCK_LEVEL}+ companion(s) have it");

            string got = null; yield return Shot("core_companion_moves", x => got = x);
            if (got != null) { shots++; Say(ctx, $"{Tag}SHOT {got}"); }
            Result("shot", got != null, got ?? "no file");

            try { if (foe != null && !foe.IsDead()) { var nv = foe.GetComponent<ZNetView>(); if (nv != null && nv.IsValid()) { nv.ClaimOwnership(); nv.Destroy(); } } } catch { }
            try { if (c != null) c.TeleportToDestination(back); } catch { }
            CompanionCombatMoves.s_testOn = false;
            if (Player.m_localPlayer != null) Player.m_localPlayer.SetGodMode(godWas);
            if (Hud.instance != null) s_hudHidden(Hud.instance) = hudWas;
            Say(ctx, $"{Tag}END {(fail == 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip, {pass + fail} compared, {shots} shot(s) in {Time.realtimeSinceStartup - started:0.0} s");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }
    }
}
