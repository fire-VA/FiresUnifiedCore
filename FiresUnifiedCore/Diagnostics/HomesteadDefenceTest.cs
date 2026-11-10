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
    /// 0.2.92x ([ghost], Fire 10-27 homestead defence + rebuild): core_homestead_defence_test (FDT runner contract, Test / SuperFlat worlds).
    /// Defence is on for the test only (HomesteadDefence.s_testOn, never saved to the cfg); the player is in god mode while hostiles are out.
    ///  1 goto 301 952;  2 setup (staying companions, armed, at home; their chores);
    ///  3 a Greydwarf_Elite attacks companion A: the "[Defend] alert" line, >= 2 OTHER companions in combat within 5 s;
    ///  4 it dies within 60 s;  5 "[Defend] resolved", the responders back on a chore within 15 s;
    ///  6 a boar attacks A: exactly 1 responder;
    ///  7 rebuild queue: three woodwalls (ground, 10 s settle) broken by a raider's hits = 3 "queued ... destroyed by monster", one broken by
    ///    fire = "fire", one removed with Remove (the hammer path) = "not queued ... hammer";
    ///  8 after the fight + 30 s quiet: all 4 queued walls rebuilt within 180 s, and the base's Wood dropped by their cost;
    ///  9 SHOT.
    /// Cleanup: spawns killed, test walls removed, the queue cleared, god mode and the flag back.
    /// </summary>
    internal static class HomesteadDefenceTest
    {
        private const string Command = "core_homestead_defence_test", Tag = "[DefenceTest] ";
        private const int Steps = 9;
        private static bool s_running;
        private static readonly ManualLogSource s_log = BepInEx.Logging.Logger.CreateLogSource("FiresUnifiedCore");
        private static readonly AccessTools.FieldRef<Hud, bool> s_hudHidden = AccessTools.FieldRefAccess<Hud, bool>("m_userHidden");

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class HomesteadDefenceTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] core_homestead_defence_test - homestead companions back each other up against a raider (scaled: a boar gets one), resume their chores, and rebuild the walls a raid / fire destroyed from stored materials, never a hammer-removed one (admin, Test / SuperFlat worlds)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!HomeMapTest.IsTestWorld(world, out string kind)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test / SuperFlat world"); return; }
                        if (Player.m_localPlayer == null) { Say(args.Context, Tag + "no local player"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        Say(args.Context, $"{Tag}world '{world}' accepted ({kind})");
                        var host = new GameObject("FiresDefenceTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Run(args.Context, host));
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
                if (s == null || s.StartsWith(Tag) || !(s.Contains("[Defend]") || s.Contains("[Rebuild]"))) return;
                lock (m_lines) m_lines.Add(System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " "));
            }
            internal int Mark { get { lock (m_lines) return m_lines.Count; } }
            internal List<string> Since(int mark, string has) { lock (m_lines) return m_lines.Skip(mark).Where(l => l.Contains(has)).ToList(); }
        }

        private static float Ground(Vector3 p) => ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(p) : p.y;

        private static Character Spawn(string prefab, Vector3 at, Character target)
        {
            var go = ZNetScene.instance?.GetPrefab(prefab);
            if (go == null) return null;
            var inst = UnityEngine.Object.Instantiate(go, at, Quaternion.identity);
            var ch = inst.GetComponent<Character>();
            var mai = inst.GetComponent<MonsterAI>();
            if (mai != null && target != null) { mai.SetTarget(target); mai.SetAlerted(true); }
            return ch;
        }

        private static void Kill(Character ch)
        {
            try { if (ch != null && !ch.IsDead()) { var nv = ch.GetComponent<ZNetView>(); if (nv != null && nv.IsValid()) { nv.ClaimOwnership(); nv.Destroy(); } } } catch { }
        }

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
            var cap = new Capture();
            BepInEx.Logging.Logger.Listeners.Add(cap);
            int pass = 0, fail = 0, skip = 0, step = 0, shots = 0;
            float started = Time.realtimeSinceStartup;
            void Result(string name, bool ok, string detail) { step++; if (ok) pass++; else fail++; Say(ctx, $"{Tag}{step}/{Steps} {name} {(ok ? "PASS" : "FAIL")} - {detail}"); }
            void Skip(string name, string detail) { step++; skip++; Say(ctx, $"{Tag}{step}/{Steps} {name} SKIP - {detail}"); }
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} () on client: {Steps} steps, ~420 s");
            var spawned = new List<Character>(); var walls = new List<GameObject>();
            bool hudWas = Hud.instance != null && s_hudHidden(Hud.instance);
            var pl0 = Player.m_localPlayer; bool godWas = pl0 != null && pl0.InGodMode();
            HomesteadDefence.s_testOn = true; HomesteadDefence.ClearForTest(); RaidRebuild.ClearForTest();
            // 0.2.937 (Fire 10-27: "our tests are turning off our HUD"; Fire 10-14: tests never hide it): the HUD is never touched
            if (pl0 != null) pl0.SetGodMode(true);

            // 1 goto
            try { var p = Player.m_localPlayer; Vector3 spot = new Vector3(301f, 50f, 952f); spot.y = Ground(spot) + 0.5f; p.TeleportTo(spot, p.transform.rotation, true); }
            catch (Exception e) { Say(ctx, $"{Tag}goto threw {e.Message}"); }
            float until = Time.realtimeSinceStartup + 40f;
            while (Time.realtimeSinceStartup < until && Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting()) yield return null;
            yield return new WaitForSecondsRealtime(5f);
            var pl = Player.m_localPlayer;
            float off = pl != null ? Utils.DistanceXZ(pl.transform.position, new Vector3(301f, 0f, 952f)) : 999f;
            Result("goto 301 952", off < 15f, $"player {off:0.0} m from the spot");

            // 2 setup
            long me = pl != null ? pl.GetPlayerID() : 0;
            var comps = CompanionController.AllCompanions.Where(c => c != null && c.isTamed && c.ownerPlayerId == me && !c.ShouldBeFollowing && c.GetCompanionAI() != null
                && pl != null && Vector3.Distance(c.transform.position, pl.transform.position) < 50f).ToList();
            // 0.2.930 (Fire 10-27 "Armed = defends"): an unarmed staying companion is handed a weapon from a base chest into its bag, the way a
            // player would; it equips it itself (up to 20 s); cleanup gives it back to that chest
            bool Armed(CompanionController c) => c.GetComponent<Humanoid>()?.GetCurrentWeapon() is ItemDrop.ItemData w && w.m_shared.m_skillType != Skills.SkillType.Unarmed;
            var handed = new List<(CompanionController c, string prefab, string shared, Container chest)>();
            foreach (var c in comps.Where(x => !Armed(x)).ToList())
            {
                Container from = null; ItemDrop.ItemData weapon = null;
                foreach (var ch in FiresCore.Npc.Core.ContainerRegistry.GetNearby(pl.transform.position, 40f))
                {
                    weapon = ch?.GetInventory()?.GetAllItems().FirstOrDefault(i => i?.m_dropPrefab != null && (i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon || i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon || i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft || i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow));
                    if (weapon != null) { from = ch; break; }
                }
                if (weapon == null) { Say(ctx, $"{Tag}setup: no weapon in a base chest for {c.companionName}"); break; }
                var bag = c.GetInventory()?.GetStorageInventory();
                var res = bag != null ? FiresCore.Npc.Core.InventoryTransferService.PullItem(from, bag, weapon.m_dropPrefab.name, 1) : null;
                if (res != null && res.Success) { c.GetInventory()?.SaveToZDO(); handed.Add((c, weapon.m_dropPrefab.name, weapon.m_shared.m_name, from)); Say(ctx, $"{Tag}setup: handed {c.companionName} a {weapon.m_dropPrefab.name} from the chest at {from.transform.position:F0}"); }
            }
            if (handed.Count > 0) { float t9 = Time.time; while (Time.time - t9 < 20f && handed.Any(h => !Armed(h.c))) yield return new WaitForSeconds(1f); }
            var armed = comps.Where(Armed).ToList();
            CompanionController a = armed.FirstOrDefault();
            string chores = string.Join(", ", comps.Select(c => $"{c.companionName}: {c.GetComponent<CompanionIdleBehavior>()?.ActiveSubBehavior?.BehaviorName ?? "idle"}"));
            bool setup = a != null && armed.Count >= 3;
            Result("setup", setup, $"{comps.Count} staying companion(s) within 50 m, {armed.Count} armed (needs 3; {handed.Count} handed a weapon from storage, {handed.Count(h => Armed(h.c))} equipped it themselves); {chores}");

            if (!setup) { while (step < Steps - 1) Skip("defence", "no setup"); }
            else
            {
                // 3 a brute attacks A
                int m = cap.Mark;
                var aChar = a.GetComponent<Character>();
                var brute = Spawn("Greydwarf_Elite", a.transform.position + a.transform.forward * 4f + Vector3.up, aChar);
                if (brute != null) spawned.Add(brute);
                float t0 = Time.time; int others = 0;
                while (Time.time - t0 < 8f)
                {
                    others = comps.Count(c => c != a && c.GetCompanionAI().IsInCombat);
                    if (others >= 2 && cap.Since(m, "[Defend] alert").Count > 0 && Time.time - t0 >= 1f) break;
                    yield return null;
                }
                var alert = cap.Since(m, "[Defend] alert");
                float respondedIn = Time.time - t0;
                Result("raid: backed up", brute != null && alert.Count > 0 && others >= 2 && respondedIn <= 8f, $"{(brute == null ? "no Greydwarf_Elite prefab; " : "")}{others} other companion(s) in combat after {respondedIn:0.0} s (alert waits for the first hit); {(alert.Count > 0 ? alert[0] : "no alert line")}");

                // 4 it dies
                t0 = Time.time;
                while (Time.time - t0 < 60f && brute != null && !brute.IsDead()) yield return null;
                bool killed = brute == null || brute.IsDead();
                Result("raid: killed", brute != null && killed, $"{(killed ? "dead" : "ALIVE")} after {Time.time - t0:0} s");

                // 5 resolved + chores resume
                t0 = Time.time; List<string> resolved;
                while ((resolved = cap.Since(m, "[Defend] resolved")).Count == 0 && Time.time - t0 < 20f) yield return null;
                float r0 = Time.time; int back = 0;
                while (Time.time - r0 < 15f)
                {
                    back = comps.Count(c => !c.GetCompanionAI().IsInCombat && c.GetComponent<CompanionIdleBehavior>()?.ActiveSubBehavior != null);
                    if (back >= comps.Count - 1) break;
                    yield return null;
                }
                Result("resolved + chores resume", resolved.Count > 0 && back >= Math.Max(1, comps.Count - 1), $"{(resolved.Count > 0 ? resolved[0] : "no resolved line")}; {back}/{comps.Count} on a chore after {Time.time - r0:0} s");

                // 6 a boar: one responder
                m = cap.Mark;
                var boar = Spawn("Boar", a.transform.position + a.transform.right * 3f + Vector3.up, aChar);
                if (boar != null) { spawned.Add(boar); var hit = new HitData { m_point = boar.transform.position }; hit.m_damage.m_blunt = 1f; hit.SetAttacker(aChar); }
                t0 = Time.time;
                while (Time.time - t0 < 15f && cap.Since(m, "[Defend] alert").Count == 0) yield return null;
                var balert = cap.Since(m, "[Defend] alert").FirstOrDefault();
                int responders = 0;
                if (balert != null) { int i = balert.IndexOf("responders: "); int j = balert.IndexOf(" (power", i + 1); if (i >= 0 && j > i) { string list = balert.Substring(i + 12, j - i - 12); responders = list.StartsWith("none") ? 0 : list.Split(new[] { "), " }, StringSplitOptions.None).Length; } }
                if (boar == null) Skip("boar: one responder", "no Boar prefab");
                else Result("boar: one responder", responders == 1, balert ?? "no alert (the boar never attacked)");
                Kill(boar);
                yield return new WaitForSeconds(12f);   // its alert resolves (gone 10 s)

                // 7 rebuild queue: 3 raider-broken walls, 1 fire, 1 hammer-removed
                Vector3 baseAt = pl.transform.position + new Vector3(-12f, 0f, 0f);
                for (int i = 0; i < 5; i++)
                {
                    var wp = ZNetScene.instance?.GetPrefab("woodwall");
                    if (wp == null) break;
                    Vector3 at = baseAt + new Vector3(0f, 0f, i * 2.1f); at.y = Ground(at);
                    walls.Add(UnityEngine.Object.Instantiate(wp, at, Quaternion.identity));
                }
                yield return new WaitForSeconds(10f);   // build-test rule: settle on the ground
                int standing = walls.Count(w => w != null);
                m = cap.Mark;
                var raider = walls.Count == 5 ? Spawn("Greydwarf_Elite", baseAt + new Vector3(-6f, 1f, 0f), null) : null;
                if (raider != null) spawned.Add(raider);
                int wood0 = FiresCore.Npc.Core.ContainerRegistry.GetTotalItemCount(pl.transform.position, "Wood", 60f);
                if (raider != null && standing == 5)
                {
                    for (int i = 0; i < 3; i++) { var hit = new HitData { m_point = walls[i].transform.position }; hit.m_damage.m_blunt = 100000f; hit.SetAttacker(raider); walls[i].GetComponent<WearNTear>()?.Damage(hit); }
                    { var fire = new HitData { m_point = walls[3].transform.position }; fire.m_damage.m_fire = 100000f; walls[3].GetComponent<WearNTear>()?.Damage(fire); }
                    walls[4].GetComponent<WearNTear>()?.Remove();
                }
                yield return new WaitForSeconds(3f);
                var queuedMonster = cap.Since(m, "destroyed by monster"); var queuedFire = cap.Since(m, "destroyed by fire"); var notHammer = cap.Since(m, "removed with the hammer");
                if (raider == null || standing < 5) Skip("rebuild queue", $"setup: walls standing {standing}/5, raider {(raider != null)}");
                else Result("rebuild queue", queuedMonster.Count == 3 && queuedFire.Count == 1 && notHammer.Count == 1 && RaidRebuild.Queued == 4,
                    $"monster {queuedMonster.Count}/3, fire {queuedFire.Count}/1, hammer not queued {notHammer.Count}/1, queue {RaidRebuild.Queued}; weather wear needs real rain (not driven here)");

                // 8 the fight ends (the raider was in the base: companions kill it), then 30 s quiet, then the rebuild
                t0 = Time.time;
                while (Time.time - t0 < 90f && raider != null && !raider.IsDead()) yield return null;
                if (raider != null && !raider.IsDead()) Kill(raider);
                t0 = Time.time;
                while (Time.time - t0 < 180f && cap.Since(m, " rebuilt ").Count < 4) yield return null;
                var rebuilt = cap.Since(m, " rebuilt "); var early = rebuilt.Count > 0 && Time.time - HomesteadDefence.LastResolvedAt < 0f;
                int wood1 = FiresCore.Npc.Core.ContainerRegistry.GetTotalItemCount(pl.transform.position, "Wood", 60f);
                int cost = 0; var pc = ZNetScene.instance?.GetPrefab("woodwall")?.GetComponent<Piece>(); if (pc != null) cost = pc.m_resources.Where(r => r?.m_resItem != null && r.m_resItem.gameObject.name == "Wood").Sum(r => r.m_amount) * 4;
                if (raider == null || standing < 5) Skip("rebuilt from storage", "no queue");
                else Result("rebuilt from storage", rebuilt.Count == 4 && wood0 - wood1 >= cost, $"{rebuilt.Count}/4 rebuilt in {Time.time - t0:0} s; Wood {wood0} -> {wood1} (cost {cost}); {(rebuilt.Count > 0 ? rebuilt[0] : string.Join(" | ", cap.Since(m, "skipped").Take(2)))}");
            }

            // 9 SHOT
            string got = null; yield return Shot("core_homestead_defence", x => got = x);
            if (got != null) { shots++; Say(ctx, $"{Tag}SHOT {got}"); }
            Result("shot", got != null, got ?? "no file");

            // cleanup: spawns, the test walls and their rebuilt copies, the queue, god mode, the flag, the HUD
            foreach (var s in spawned) Kill(s);
            foreach (var w in walls) { try { if (w != null) w.GetComponent<WearNTear>()?.Remove(); } catch { } }
            try
            {
                if (pl != null)
                    foreach (var col in Physics.OverlapSphere(pl.transform.position + new Vector3(-12f, 1f, 4f), 7f))
                    { var w = col != null ? col.GetComponentInParent<WearNTear>() : null; if (w != null && Utils.GetPrefabName(w.gameObject) == "woodwall") w.Remove(); }
            }
            catch { }
            // the weapons handed in setup go back to their chests (unequipped first if the companion equipped it)
            foreach (var h in handed)
            {
                try
                {
                    var hum = h.c != null ? h.c.GetComponent<Humanoid>() : null;
                    var w = hum?.GetCurrentWeapon();
                    if (w != null && w.m_shared.m_name == h.shared) hum.UnequipItem(w, false);
                    var bag = h.c?.GetInventory()?.GetStorageInventory();
                    bool gone = false;
                    if (bag != null && bag.ContainsItemByName(h.shared)) { bag.RemoveItem(h.shared, 1); gone = true; }
                    else if (hum != null && hum.GetInventory().ContainsItemByName(h.shared)) { hum.GetInventory().RemoveItem(h.shared, 1); gone = true; }
                    if (gone) { var pf = ObjectDB.instance?.GetItemPrefab(h.prefab); if (pf != null && h.chest != null) h.chest.GetInventory().AddItem(pf, 1); h.c.GetInventory()?.SaveToZDO(); }
                    Say(ctx, $"{Tag}cleanup: {h.prefab} {(gone ? "returned to its chest" : "NOT found on " + h.c?.companionName)}");
                }
                catch (Exception e) { Say(ctx, $"{Tag}cleanup: returning {h.prefab} threw {e.Message}"); }
            }
            RaidRebuild.ClearForTest(); HomesteadDefence.ClearForTest(); HomesteadDefence.s_testOn = false;
            if (Player.m_localPlayer != null) Player.m_localPlayer.SetGodMode(godWas);
            if (Hud.instance != null) s_hudHidden(Hud.instance) = hudWas;
            BepInEx.Logging.Logger.Listeners.Remove(cap);
            Say(ctx, $"{Tag}END {(fail == 0 ? "PASS" : "FAIL")}: {pass} pass, {fail} fail, {skip} skip, {pass + fail} compared, {shots} shot(s) in {Time.realtimeSinceStartup - started:0.0} s");
            s_running = false;
            UnityEngine.Object.Destroy(host);
        }
    }
}
