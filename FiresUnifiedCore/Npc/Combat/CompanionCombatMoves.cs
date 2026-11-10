using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// 0.2.92x ([ghost], Fire 10-27 movement plan - COMBAT set; Tools\COMPANION_MOVEMENT_PLAN_20261027.md): companions in a fight use the
    /// pack's movement / combat moves the way a player would, by circumstance, each with its own cooldown, stamina / eitr from the companion's
    /// own pool, and a why-line:
    ///  - run slide: away from a foe within 4 m at low health (disengage), or toward one 8-16 m off while running (close the distance) - Core;
    ///  - double jump: a foe 1.5 m+ above within 6 m (reposition up) - the RPG class second-jump style when [wishbone]'s RPGClasses API is
    ///    there (Fire: "RPG class second-jump STYLES"), else Core's;
    ///  - air dash: in the air, toward a foe 6 m+ off, or away at low health - VikHavn ([visual]'s VikHavnApi);
    ///  - shield bash (shield, foe within 2.5 m), kick (unarmed, within 2 m), grab / throw (unarmed, within 2 m), taunt (2+ foes within 8 m
    ///    and the companion is the healthiest near) - VikHavn;
    ///  - emergency evasion: attached to companions at AbilityUnlockSystem.EVASION_UNLOCK_LEVEL+ (the component existed but was never added
    ///    to companions); it runs its own low-health rules.
    /// The dodge roll stays where it is (DodgeBehavior). Optional APIs are bound by reflection; a missing one says so once and its moves are
    /// skipped. Owner-only. "[CombatMove] <name>: <move> -> ok | refused: <why>" at Debug.
    /// [Companions] Combat moves, OFF until core_companion_moves_test passes (Fire).
    /// </summary>
    internal static class CompanionCombatMoves
    {
        // 1086 ([generator], Fire 11-01: configs live in their own mods): bound by FiresCompanions; Core reads the owner's value (old default without it).
        private static readonly FiresCore.Config.OwnedSetting<bool> s_on = new FiresCore.Config.OwnedSetting<bool>("Companions", "Combat moves", false);
        internal static bool s_testOn;   // core_companion_moves_test only; never saved to the cfg
        internal static void BindConfig(BepInEx.Configuration.ConfigFile cfg) { }
        internal static bool On => s_testOn || s_on.Value;

        // ---- optional APIs (reflection)
        private static bool s_bound;
        private static Type s_vik, s_rpg;
        private static readonly Dictionary<string, MethodInfo> s_m = new Dictionary<string, MethodInfo>();
        private static void Bind()
        {
            if (s_bound) return;
            s_bound = true;
            s_vik = Type.GetType("VikHavnCombat.VikHavnApi, VikHavnCombatMovesetAdditions");
            s_rpg = Type.GetType("FiresRPGClasses.Combat.ClassMovementApi, FiresRPGClasses") ?? Type.GetType("FiresRPGClasses.Combat.ClassMovement, FiresRPGClasses");
            if (s_vik != null) foreach (var n in new[] { "TryAirDash", "TryKick", "TryShieldBash", "TryGrab", "TryThrow", "TryTaunt", "TryChargedSecondary" }) { var m = s_vik.GetMethod(n, BindingFlags.Public | BindingFlags.Static); if (m != null) s_m["vik." + n] = m; }
            if (s_rpg != null) { var m = s_rpg.GetMethod("TryStyle", BindingFlags.Public | BindingFlags.Static); if (m != null) s_m["rpg.TryStyle"] = m; }
            FiresCore.Logging.FiresLogger.LogDebug($"[CombatMove] APIs: VikHavnApi {(s_vik != null ? string.Join(", ", s_m.Keys.Where(k => k.StartsWith("vik.")).Select(k => k.Substring(4))) : "not found (VikHavn moves skipped)")}; RPG class style {(s_m.ContainsKey("rpg.TryStyle") ? "TryStyle" : "not found (Core double jump)")}");
        }

        // invoke an optional Try*(Character who, <extra>, out string why)
        private static bool Call(string key, Character who, object extra, out string why)
        {
            why = null; Bind();
            if (!s_m.TryGetValue(key, out var m)) { why = "API not installed"; return false; }
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            args[0] = who;
            for (int i = 1; i < ps.Length - 1; i++) args[i] = extra != null && ps[i].ParameterType.IsInstanceOfType(extra) ? extra : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
            try { bool ok = (bool)m.Invoke(null, args); why = args[ps.Length - 1] as string; return ok; }
            catch (Exception e) { why = "threw " + (e.InnerException?.Message ?? e.Message); return false; }
        }

        // ---- the moves (the test calls these directly; the chooser below calls them by circumstance)
        internal static readonly string[] Moves = { "slide", "double jump", "air dash", "shield bash", "kick", "grab", "throw", "taunt", "charged secondary" };

        internal static bool Do(CompanionController c, string move, Character foe, out string why)
        {
            why = null;
            var who = c != null ? c.GetComponent<Character>() : null;
            if (who == null) { why = "no body"; return false; }
            Vector3 to = foe != null ? foe.transform.position - who.transform.position : who.transform.forward;
            bool ok;
            switch (move)
            {
                case "slide": ok = FiresCore.Movement.CoreMovement.TrySlideFor(who, LowHealth(who) && foe != null ? -to : to, out why); break;
                case "double jump":
                    if (s_m.ContainsKey("rpg.TryStyle") || (Bind0() && s_m.ContainsKey("rpg.TryStyle"))) { ok = Call("rpg.TryStyle", who, to, out why); if (ok) { why = "class style" + (why != null ? ": " + why : ""); break; } }
                    ok = FiresCore.Movement.CoreMovement.TryDoubleJumpFor(who, out why); break;
                case "air dash": ok = Call("vik.TryAirDash", who, LowHealth(who) && foe != null ? -to.normalized : to.normalized, out why); break;
                case "shield bash": ok = Call("vik.TryShieldBash", who, null, out why); break;
                case "kick": ok = Call("vik.TryKick", who, foe, out why); break;
                case "grab": ok = Call("vik.TryGrab", who, foe, out why); break;
                case "throw": ok = Call("vik.TryThrow", who, to.normalized, out why); break;
                case "taunt": ok = Call("vik.TryTaunt", who, null, out why); break;
                case "charged secondary": ok = Call("vik.TryChargedSecondary", who, null, out why); break;
                default: why = "unknown move"; return false;
            }
            FiresCore.Logging.FiresLogger.LogDebug($"[CombatMove] {c.companionName}: {move}{(foe != null ? $" vs {foe.m_name} at {Vector3.Distance(who.transform.position, foe.transform.position):0.0} m" : "")} -> {(ok ? "ok" : "refused")}{(why != null ? ": " + why : "")}");
            return ok;
        }
        private static bool Bind0() { Bind(); return true; }

        private static bool LowHealth(Character who) => who.GetHealthPercentage() < 0.35f;

        // ---- the chooser: one component per companion, every 0.5 s in a fight
        internal sealed class Chooser : MonoBehaviour
        {
            private CompanionController m_c; private Character m_body; private ZNetView m_nv;
            private float m_next; private readonly Dictionary<string, float> m_cool = new Dictionary<string, float>();
            private static readonly Dictionary<string, float> Cooldown = new Dictionary<string, float>
            { { "slide", 4f }, { "double jump", 3f }, { "air dash", 6f }, { "shield bash", 3f }, { "kick", 3f }, { "grab", 10f }, { "throw", 2f }, { "taunt", 30f }, { "charged secondary", 8f } };

            private void Awake() { m_c = GetComponent<CompanionController>(); m_body = GetComponent<Character>(); m_nv = GetComponent<ZNetView>(); }

            private bool Ready(string move) => !m_cool.TryGetValue(move, out float t) || Time.time >= t;
            private bool Try(string move, Character foe)
            {
                if (!Ready(move)) return false;
                bool ok = Do(m_c, move, foe, out _);
                m_cool[move] = Time.time + (ok ? Cooldown[move] : 2f);
                return ok;
            }

            private void Update()
            {
                if (!On || m_c == null || m_body == null || Time.time < m_next) return;
                m_next = Time.time + 0.5f;
                long pf = FiresCore.Diagnostics.CompanionSystemsProfile.Begin();
                try { Choose(); } finally { FiresCore.Diagnostics.CompanionSystemsProfile.End("combat-move chooser", pf); }
            }

            private void Choose()
            {
                if (m_nv == null || !m_nv.IsValid() || !m_nv.IsOwner() || m_body.IsDead()) return;
                var ai = m_c.GetCompanionAI();
                var foe = ai != null && ai.IsInCombat ? ai.CombatTarget : null;
                if (foe == null || foe.IsDead()) return;
                Vector3 me = m_body.transform.position, at = foe.transform.position;
                float d = Vector3.Distance(me, at), up = at.y - me.y;
                var h = m_body as Humanoid;
                var right = h != null ? h.GetCurrentWeapon() : null;
                bool unarmed = right == null || right.m_shared.m_skillType == Skills.SkillType.Unarmed;
                bool shield = h != null && h.GetLeftItem() != null && h.GetLeftItem().m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield;
                bool low = LowHealth(m_body);

                if (!m_body.IsOnGround())
                {
                    if ((low && d < 6f) || d > 6f) Try("air dash", foe);
                    else if (up > 1.5f) Try("double jump", foe);
                    return;
                }
                if (low && d < 4f && Try("slide", foe)) return;
                if (up > 1.5f && d < 6f) { m_body.Jump(); m_cool["double jump"] = 0f; return; }   // the second push comes next tick, in the air
                if (shield && d < 2.5f && Try("shield bash", foe)) return;
                if (unarmed && d < 2f && (Try("kick", foe) || Try("grab", foe))) return;
                if (d > 8f && d < 16f && m_body.IsRunning() && Try("slide", foe)) return;
                if (CountFoes(me, 8f) >= 2 && HealthiestNear(m_body) && Try("taunt", null)) return;
            }

            private static int CountFoes(Vector3 at, float r)
            {
                var list = new List<Character>(); Character.GetCharactersInRange(at, r, list);
                return list.Count(ch => ch != null && !ch.IsDead() && !ch.IsPlayer() && !ch.IsTamed() && ch.GetComponent<CompanionController>() == null);
            }
            private static bool HealthiestNear(Character me)
            {
                var list = new List<Character>(); Character.GetCharactersInRange(me.transform.position, 10f, list);
                return list.Where(ch => ch != null && ch.GetComponent<CompanionController>() != null).All(ch => ch.GetMaxHealth() <= me.GetMaxHealth());
            }
        }

        // ---- attach the chooser (and emergency evasion for levelled companions) to owned companions
        private static float s_scanAt;
        private static Runner s_runner;
        private sealed class Runner : MonoBehaviour { private void Update() { if (On && Time.time >= s_scanAt) { s_scanAt = Time.time + 5f; Attach(); } } }

        [HarmonyLib.HarmonyPatch(typeof(ZNet), "Awake")]
        private static class CompanionCombatMoves_Boot
        {
            private static void Postfix()
            {
                if (s_runner != null) return;
                var go = new GameObject("FiresCompanionCombatMoves"); UnityEngine.Object.DontDestroyOnLoad(go); s_runner = go.AddComponent<Runner>();
            }
        }

        internal static void Attach()
        {
            Bind();
            foreach (var c in CompanionController.AllCompanions)
            {
                if (c == null) continue;
                var nv = c.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid() || !nv.IsOwner()) continue;
                if (c.GetComponent<Chooser>() == null) c.gameObject.AddComponent<Chooser>();
                int lvl = c.GetComponent<FiresCore.Npc.CompanionProgression>()?.Level ?? 1;
                if (lvl >= FiresCore.Npc.Archetypes.AbilityUnlockSystem.EVASION_UNLOCK_LEVEL && c.GetComponent<EmergencyEvasion.EmergencyEvasionManager>() == null)
                {
                    c.gameObject.AddComponent<EmergencyEvasion.EmergencyEvasionManager>();
                    FiresCore.Logging.FiresLogger.LogDebug($"[CombatMove] {c.companionName}: emergency evasion attached (level {lvl})");
                }
            }
        }
    }
}
