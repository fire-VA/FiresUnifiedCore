using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FiresCore.Movement;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// first_person_test (0.2.968, [lead] 10-28): proof for Core's first person mode (Movement\FirstPerson.cs) at the homestead.
    /// Admin, Test worlds only. Needs "First person mode" on (rounds pass -PluginCfg); it is never switched on here.
    /// Input goes through the player's own path: the test adds to what ZInput returns (scroll wheel, mouse delta, the Attack button),
    /// so vanilla's GameCamera / PlayerController / Humanoid code runs exactly as for a human. The chair is used with Chair.Interact.
    ///  1 zoom      scroll in -> enter (count + head hidden: bone shrunk, head items shadows-only) -> scroll out -> exit ("scrolled out")
    ///  2 shadow    back in, pitched down: the head shadow copy is live and shadows-only; shot
    ///  3 seated    sit on the nearest chair (one is placed 3 m ahead if none within 40 m, removed after), sweep yaw: the clamp fires
    ///              and the look stays within the limit
    ///  4 ghost bow give a bow + arrows, draw in FP: ghost on, the bow's materials are ghosts at the cfg opacity, the shader path is not
    ///              "none (still opaque)"; shot; release: ghost off
    ///  5 crossbow  give a crossbow + bolts, hold attack in FP: no ghost
    /// Shots at the game's resolution with the HUD hidden, after teleport FX settle. Restores: input, HUD, the items given (removed,
    /// leftovers counted), the player's own hand items, the start position, FP state and camera distance. 0 compared is a FAIL.
    /// Lines: BEGIN / "n/N step PASS|FAIL - detail" (+ "shot: path (WxH)") / END PASS|FAIL.
    /// </summary>
    internal static class FirstPersonTest
    {
        private const string Command = "first_person_test";
        private const string Tag = "[FirstPersonTest] ";
        private static bool s_running;

        // ── input added to ZInput's answers while the test runs (zero otherwise) ──────────────────────────────────────────────
        private static float s_wheel; private static Vector2 s_mouse; private static bool s_attack;

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
        private static class ZInput_Wheel_FirstPersonTest { private static void Postfix(ref float __result) { if (s_running) __result += s_wheel; } }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseDelta))]
        private static class ZInput_Mouse_FirstPersonTest { private static void Postfix(ref Vector2 __result) { if (s_running) __result += s_mouse; } }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton))]
        private static class ZInput_Button_FirstPersonTest { private static void Postfix(string name, ref bool __result) { if (s_running && s_attack && name == "Attack") __result = true; } }

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class FirstPersonTest_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[F5 test] [FiresUnifiedCore] first_person_test - first person mode: zoom in/out, head hidden + head shadow, seated yaw limit, ghost bow, crossbow solid (admin, Test worlds only; needs 'First person mode' on)",
                    args =>
                    {
                        string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
                        if (!GroundRingShotsTest.IsTestWorld(world)) { Say(args.Context, $"{Tag}refused, '{world ?? "?"}' is not a Test world"); return; }
                        if (Player.m_localPlayer == null || GameCamera.instance == null) { Say(args.Context, Tag + "no local player / camera"); return; }
                        if (s_running) { Say(args.Context, Tag + "already running"); return; }
                        var host = new GameObject("FiresFirstPersonTest");
                        UnityEngine.Object.DontDestroyOnLoad(host);
                        host.AddComponent<Runner>().StartCoroutine(Run(args.Context, host));
                    }, isCheat: true);
            }
        }

        private sealed class Runner : MonoBehaviour { }

        private static void Say(Terminal ctx, string line) { ctx?.AddString(line); FiresCore.Logging.FiresLogger.LogInfo(line); }

        private static readonly AccessTools.FieldRef<GameCamera, float> Distance = AccessTools.FieldRefAccess<GameCamera, float>("m_distance");
        private static readonly AccessTools.FieldRef<Character, Quaternion> LookYaw = AccessTools.FieldRefAccess<Character, Quaternion>("m_lookYaw");
        private static readonly AccessTools.FieldRef<Player, float> LookPitch = AccessTools.FieldRefAccess<Player, float>("m_lookPitch");
        private static readonly AccessTools.FieldRef<Character, Transform> HeadBone = AccessTools.FieldRefAccess<Character, Transform>("m_head");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> LeftItem = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_leftItemInstance");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> HairItem = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_hairItemInstance");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> HelmetItem = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_helmetItemInstance");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> RightOf = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_rightItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> LeftOf = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_leftItem");

        private const int Planned = 6;   // 0.2.999: + steady_head (runs right after head_shadow, while looking down)
        private static int s_pass, s_fail, s_compared;

        private static void Step(Terminal ctx, int n, string name, bool ok, string detail)
        {
            s_compared++;
            if (ok) s_pass++; else s_fail++;
            Say(ctx, $"{Tag}{n}/{Planned} {name} {(ok ? "PASS" : "FAIL")} - {detail}");
        }

        private static void Fail(Terminal ctx, int n, string name, string why) { s_fail++; Say(ctx, $"{Tag}{n}/{Planned} {name} FAIL - {why} (nothing compared)"); }

        // scroll until first person matches 'want' (or frames run out); vanilla clamps the wheel to +-0.05 a frame
        private static IEnumerator Scroll(bool want, int frames)
        {
            s_wheel = want ? 0.05f : -0.05f;
            for (int i = 0; i < frames && FirstPerson.IsActive != want; i++) yield return null;
            s_wheel = 0f;
            yield return null;
        }

        private static IEnumerator Settle(Player p, float seconds)
        {
            float until = Time.time + 20f;
            while (p != null && p.IsTeleporting() && Time.time < until) yield return null;
            yield return new WaitForSeconds(seconds);
        }

        private static IEnumerator Run(Terminal ctx, GameObject host)
        {
            s_running = true; s_pass = s_fail = s_compared = 0;
            float started = Time.realtimeSinceStartup;
            var shots = new List<string>();
            var p = Player.m_localPlayer; var gc = GameCamera.instance; var inv = p.GetInventory();
            Vector3 startPos = p.transform.position; Quaternion startRot = p.transform.rotation;
            float distWas = Distance(gc); bool fpWas = FirstPerson.IsActive;
            bool hudWas = Hud.instance != null && Hud.instance.m_userHidden;
            ItemDrop.ItemData rightWas = RightOf(p), leftWas = LeftOf(p);
            var given = new List<ItemDrop.ItemData>();
            var ammo = new List<(string name, int before)>();
            GameObject placedChair = null;
            Say(ctx, $"{Tag}BEGIN FiresUnifiedCore {FiresUnifiedCore.PluginVersion} on client: {Planned} steps (2 shots), ~40 s; 'First person mode' = {FirstPerson.Enabled}");
            try
            {
                if (!FirstPerson.Enabled)
                {
                    for (int n = 1; n <= Planned; n++) Fail(ctx, n, "all", "'First person mode' is off (run the round with -PluginCfg 'First person mode' = true)");
                    yield break;
                }
                if (Hud.instance != null) Hud.instance.m_userHidden = true;
                yield return Settle(p, 2f);
                if (FirstPerson.IsActive) yield return Scroll(false, 60);   // start from third person
                Distance(gc) = Mathf.Max(gc.m_minDistance, 1f);
                yield return new WaitForSeconds(0.3f);

                // 1 zoom in / out
                {
                    int e0 = FirstPerson.EnterCount, x0 = FirstPerson.ExitCount;
                    yield return Scroll(true, 120);
                    yield return new WaitForSeconds(0.3f);
                    bool entered = FirstPerson.IsActive && FirstPerson.EnterCount > e0;
                    var head = HeadBone(p); var vis = p.GetComponent<VisEquipment>();
                    float headScale = head != null ? head.lossyScale.x : -1f;
                    int itemsTotal = 0, itemsShadow = 0;
                    foreach (var go in new[] { vis != null ? HairItem(vis) : null, vis != null ? HelmetItem(vis) : null })
                        if (go != null) foreach (var r in go.GetComponentsInChildren<Renderer>()) { itemsTotal++; if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) itemsShadow++; }
                    bool hidden = FirstPerson.HeadHidden && headScale >= 0f && headScale < 0.01f && itemsShadow == itemsTotal;
                    yield return Scroll(false, 60);
                    bool exited = !FirstPerson.IsActive && FirstPerson.ExitCount > x0 && FirstPerson.LastExitWhy == "scrolled out" && !FirstPerson.HeadHidden;
                    Step(ctx, 1, "zoom", entered && hidden && exited,
                        $"scroll in: first person {(entered ? "entered" : "NOT entered")} (enter lines {FirstPerson.EnterCount - e0}); head bone scale {headScale:0.0000}, head items {itemsShadow}/{itemsTotal} shadows-only; scroll out: {(exited ? "exited (scrolled out), head restored" : $"NOT exited cleanly (active {FirstPerson.IsActive}, exit lines {FirstPerson.ExitCount - x0}, why '{FirstPerson.LastExitWhy}', head hidden {FirstPerson.HeadHidden})")}");
                }

                // 2 head shadow
                {
                    yield return Scroll(true, 120);
                    s_mouse = new Vector2(0f, -8f);
                    for (int i = 0; i < 90 && LookPitch(p) < 60f; i++) yield return null;
                    s_mouse = Vector2.zero;
                    yield return new WaitForSeconds(0.5f);
                    var copy = FirstPerson.HeadShadowCopy;
                    var smr = copy != null ? copy.GetComponent<SkinnedMeshRenderer>() : null;
                    bool ok = FirstPerson.IsActive && smr != null && smr.enabled && copy.activeInHierarchy && smr.shadowCastingMode == ShadowCastingMode.ShadowsOnly && LookPitch(p) >= 45f;
                    string shot = null; yield return Capture("firstperson_head_shadow", s => shot = s);
                    if (shot != null) { shots.Add(shot); Say(ctx, $"{Tag}head shadow shot: {shot}"); } else Say(ctx, $"{Tag}head shadow shot: NOT WRITTEN");
                    Step(ctx, 2, "head_shadow", ok && shot != null, $"pitch {LookPitch(p):0} deg down; head shadow copy {(smr == null ? "MISSING" : $"live {smr.enabled && copy.activeInHierarchy}, {smr.shadowCastingMode}")}; shot {(shot != null ? "written" : "MISSING")} (check: the shadow on the ground has a head)");

                    // 6 steady head (0.2.999, Fire 10-29 round R: "first person camera still flickers every frame"): standing, looking down,
                    // the camera's frame-to-frame jump in the player's own frame, 120 frames with the steady-head fix OFF then 120 ON (an
                    // in-memory override; the cfg is never written and the override is cleared after). PASS = OFF jumps (> 5 mm max) and
                    // ON holds (< 1/4 of OFF and < 3 mm): the fix is what removes the flicker
                    {
                        float off = 0f, on = 0f; int offN = 0, onN = 0, offFresh = 0, onFresh = 0;
                        for (int pass = 0; pass < 2; pass++)
                        {
                            FirstPerson.SteadyOverride = pass == 1;
                            for (int i = 0; i < 10; i++) yield return null;   // settle with the new setting
                            Vector3? last = null;
                            for (int i = 0; i < 120; i++)
                            {
                                yield return new WaitForEndOfFrame();
                                if (p == null || GameCamera.instance == null) break;
                                Vector3 cl = p.transform.InverseTransformPoint(GameCamera.instance.transform.position);
                                if (last.HasValue)
                                {
                                    float d = (cl - last.Value).magnitude;
                                    if (pass == 0) { off = Mathf.Max(off, d); offN++; if (FirstPerson.LastSpineFresh) offFresh++; }
                                    else { on = Mathf.Max(on, d); onN++; if (FirstPerson.LastSpineFresh) onFresh++; }
                                }
                                last = cl;
                            }
                        }
                        FirstPerson.SteadyOverride = null;
                        bool steadyOk = offN > 0 && onN > 0 && off > 0.005f && on < off * 0.25f && on < 0.003f;
                        Step(ctx, 6, "steady_head", steadyOk, $"pitch {LookPitch(p):0} deg down, standing; camera jump frame to frame (player frame): fix OFF max {off * 1000f:0.0} mm over {offN} frames ({offFresh} with a new animator pose), fix ON max {on * 1000f:0.0} mm over {onN} frames ({onFresh} new poses); setting back to the cfg ({(FirstPerson.SteadyOn ? "on" : "off")})");
                    }
                    s_mouse = new Vector2(0f, 8f);
                    for (int i = 0; i < 90 && LookPitch(p) > 5f; i++) yield return null;
                    s_mouse = Vector2.zero;
                }

                // 3 seated yaw limit
                {
                    Chair chair = null; float best = 40f;
                    foreach (var c in UnityEngine.Object.FindObjectsByType<Chair>(FindObjectsSortMode.None))
                    {
                        if (c == null || c.m_attachPoint == null || c.IsInUse()) continue;
                        float d = Vector3.Distance(c.transform.position, p.transform.position);
                        if (d < best) { best = d; chair = c; }
                    }
                    if (chair == null)
                    {
                        var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("piece_chair") : null;
                        if (prefab != null)
                        {
                            Vector3 at = p.transform.position + p.transform.forward * 3f;
                            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(at, out float gy)) at.y = gy;
                            placedChair = UnityEngine.Object.Instantiate(prefab, at, Quaternion.LookRotation(-p.transform.forward));
                            yield return new WaitForSeconds(0.5f);
                            chair = placedChair.GetComponentInChildren<Chair>();
                        }
                    }
                    if (chair == null) Fail(ctx, 3, "seated_yaw", "no chair within 40 m and piece_chair could not be placed");
                    else
                    {
                        p.TeleportTo(chair.m_attachPoint.position + chair.m_attachPoint.forward * 0.8f, Quaternion.LookRotation(-chair.m_attachPoint.forward), false);
                        yield return Settle(p, 1.5f);
                        bool sat = chair.Interact(p, false, false);
                        yield return new WaitForSeconds(0.8f);
                        if (!FirstPerson.IsActive) yield return Scroll(true, 120);
                        int c0 = FirstPerson.YawClampCount;
                        float limit = 100f, worst = 0f;
                        s_mouse = new Vector2(12f, 0f);
                        for (int i = 0; i < 120; i++)
                        {
                            yield return null;
                            worst = Mathf.Max(worst, Mathf.Abs(Mathf.DeltaAngle(p.transform.eulerAngles.y, LookYaw(p).eulerAngles.y)));
                        }
                        s_mouse = Vector2.zero;
                        limit = FirstPerson.YawLimit;
                        bool attached = p.IsAttached();
                        bool ok = sat && attached && FirstPerson.YawClampCount > c0 && worst <= limit + 1f;
                        Step(ctx, 3, "seated_yaw", ok, $"{(placedChair != null ? "placed" : "nearest")} chair '{chair.name.Replace("(Clone)", "")}' {best:0.0} m away; sat {sat}, attached {attached}; swept right 120 frames: clamp hits {FirstPerson.YawClampCount - c0}, worst {worst:0} deg off the body (limit {limit:0})");
                        if (p.IsAttached()) p.AttachStop();
                        yield return new WaitForSeconds(0.5f);
                    }
                    p.TeleportTo(startPos, startRot, false);
                    yield return Settle(p, 2f);
                    if (!FirstPerson.IsActive) yield return Scroll(true, 120);
                }

                // 4 ghost bow
                {
                    var bow = Give(inv, given, "Bow"); GiveAmmo(inv, ammo, "ArrowWood", 20);
                    if (bow == null) Fail(ctx, 4, "ghost_bow", "could not give a Bow");
                    else
                    {
                        p.UseItem(inv, bow, false);
                        yield return new WaitForSeconds(1f);
                        int on0 = FirstPerson.GhostOnCount, off0 = FirstPerson.GhostOffCount;
                        s_attack = true;
                        float until = Time.time + 2.5f;
                        while (Time.time < until && p.GetAttackDrawPercentage() < 0.6f) yield return null;
                        yield return new WaitForSeconds(0.2f);
                        var vis = p.GetComponent<VisEquipment>(); var held = vis != null ? LeftItem(vis) : null;
                        int mats = 0, ghosts = 0; float alpha = -1f;
                        if (held != null)
                            foreach (var r in held.GetComponentsInChildren<Renderer>())
                                foreach (var m in r.sharedMaterials)
                                {
                                    if (m == null) continue; mats++;
                                    if (m.name.EndsWith("(fires ghost)")) { ghosts++; if (m.HasProperty("_Color")) alpha = m.GetColor("_Color").a; }
                                }
                        string path = FirstPerson.GhostShaderPath ?? "none recorded";
                        string shot = null; yield return Capture("firstperson_ghost_bow", s => shot = s);
                        if (shot != null) { shots.Add(shot); Say(ctx, $"{Tag}ghost bow shot: {shot}"); } else Say(ctx, $"{Tag}ghost bow shot: NOT WRITTEN");
                        float draw = p.GetAttackDrawPercentage();
                        s_attack = false;
                        yield return new WaitForSeconds(1f);
                        bool opaque = path.StartsWith("none");
                        bool ok = p.GetCurrentWeapon() == bow && FirstPerson.GhostOnCount > on0 && mats > 0 && ghosts == mats && Mathf.Abs(alpha - FirstPerson.GhostAlpha) < 0.02f && !opaque && FirstPerson.GhostOffCount > off0 && shot != null;
                        Step(ctx, 4, "ghost_bow", ok, $"bow equipped {p.GetCurrentWeapon() == bow}, draw {draw:0.00}; ghost on lines {FirstPerson.GhostOnCount - on0}, {ghosts}/{mats} material(s) ghosted at opacity {alpha:0.00} (cfg {FirstPerson.GhostAlpha:0.00}); shader path '{path}'{(opaque ? " = STILL OPAQUE" : "")}; released: ghost off lines {FirstPerson.GhostOffCount - off0}; shot {(shot != null ? "written" : "MISSING")}");
                    }
                }

                // 5 crossbow stays solid
                {
                    var xbow = Give(inv, given, "CrossbowArbalest"); GiveAmmo(inv, ammo, "BoltBone", 20);
                    if (xbow == null) Fail(ctx, 5, "crossbow_solid", "could not give a CrossbowArbalest");
                    else
                    {
                        p.UseItem(inv, xbow, false);
                        yield return new WaitForSeconds(1f);
                        int on0 = FirstPerson.GhostOnCount;
                        s_attack = true; yield return new WaitForSeconds(1.5f); s_attack = false;
                        yield return new WaitForSeconds(0.5f);
                        bool equipped = p.GetCurrentWeapon() == xbow;
                        Step(ctx, 5, "crossbow_solid", equipped && FirstPerson.IsActive && FirstPerson.GhostOnCount == on0,
                            $"crossbow equipped {equipped}, first person {FirstPerson.IsActive}; ghost on lines while aiming {FirstPerson.GhostOnCount - on0} (want 0)");
                    }
                }
            }
            finally
            {
                s_wheel = 0f; s_mouse = Vector2.zero; s_attack = false; FirstPerson.SteadyOverride = null;   // 0.2.999: never leave the test override on
                try
                {
                    foreach (var it in given) { if (p != null && p.IsItemEquiped(it)) p.UnequipItem(it, false); inv?.RemoveItem(it); }
                    foreach (var (name, before) in ammo) { int extra = inv != null ? inv.CountItems(ItemName(name)) - before : 0; if (extra > 0) inv.RemoveItem(ItemName(name), extra); }
                    if (p != null)
                    {
                        if (rightWas != null && inv.ContainsItem(rightWas) && !p.IsItemEquiped(rightWas)) p.EquipItem(rightWas, false);
                        if (leftWas != null && inv.ContainsItem(leftWas) && !p.IsItemEquiped(leftWas)) p.EquipItem(leftWas, false);
                    }
                    if (placedChair != null) { var nv = placedChair.GetComponent<ZNetView>(); if (nv != null && nv.IsValid()) nv.Destroy(); else UnityEngine.Object.Destroy(placedChair); }
                    if (Hud.instance != null) Hud.instance.m_userHidden = hudWas;
                }
                catch (Exception ex) { Say(ctx, $"{Tag}restore error: {ex.Message}"); }
                s_running = false;
            }
            // first person / camera back as the player had it (input path), then the distance
            if (!fpWas && FirstPerson.IsActive) { s_running = true; yield return Scroll(false, 60); s_running = false; }
            if (gc != null) Distance(gc) = distWas;
            if (s_compared == 0) s_fail++;
            Say(ctx, $"{Tag}restored: items given removed ({given.Count} + ammo), hand items '{rightWas?.m_shared.m_name ?? "none"}' / '{leftWas?.m_shared.m_name ?? "none"}', camera {distWas:0.0} m, first person {FirstPerson.IsActive} (was {fpWas}), HUD hidden {hudWas}");
            Say(ctx, $"{Tag}END {(s_fail == 0 ? "PASS" : "FAIL")}: {s_pass} pass, {s_fail} fail, 0 skip, {s_compared} compared, {shots.Count} shot(s) in {Time.realtimeSinceStartup - started:0.0} s");
            UnityEngine.Object.Destroy(host);
        }

        private static string ItemName(string prefab)
        {
            var go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            return go != null ? go.GetComponent<ItemDrop>().m_itemData.m_shared.m_name : prefab;
        }

        private static ItemDrop.ItemData Give(Inventory inv, List<ItemDrop.ItemData> given, string prefab)
        {
            var it = inv?.AddItem(prefab, 1, 1, 0, 0L, "", false);
            if (it != null) given.Add(it);
            return it;
        }

        private static void GiveAmmo(Inventory inv, List<(string, int)> ammo, string prefab, int n)
        {
            if (inv == null) return;
            ammo.Add((prefab, inv.CountItems(ItemName(prefab))));
            inv.AddItem(prefab, n, 1, 0, 0L, "", false);
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
