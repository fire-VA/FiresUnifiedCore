using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// 0.2.932 ([ghost], Fire 10-27: "companion attacks do damage the second they swing rather than actually using the weapons, actual attack
    /// speed or collision from the weapon trail"): LOG ONLY - which path delivers each companion melee hit and when, so the instant one is
    /// proven before anything is changed. For a companion attacker:
    ///  - Humanoid.StartAttack (postfix, when it started an attack): the swing start time, the weapon, the attack's animation;
    ///  - Attack.OnAttackTrigger (prefix): the hit, with the time since that swing started and WHO called it - vanilla's animation event
    ///    (CharacterAnimEvent) or our CompanionAttackBridge (animator progress / fallback timer) or WeaponBehavior's safety fallback;
    ///  - Character.Damage (prefix) from a companion with no OnAttackTrigger in the last 0.1 s: a direct-damage path (the caller is named).
    /// "[CombatHit] <name>: <path> hit at t=X s after swing start (weapon W, anim A)". 30 lines per session; then counts only, in a
    /// 10-min Debug summary per path.
    /// </summary>
    internal static class CombatHitTrace
    {
        private const int MaxLines = 30;
        private static int s_lines;
        private static readonly Dictionary<Character, (float at, string weapon, string anim)> s_swing = new Dictionary<Character, (float, string, string)>();
        private static readonly Dictionary<Character, float> s_lastTrigger = new Dictionary<Character, float>();
        private static readonly Dictionary<string, int> s_counts = new Dictionary<string, int>();
        private static float s_summaryAt;

        // 0.2.938: the attacks whose hit has fired (any caller), so the bridge / safety fallback never hit a second time, and the swing start
        // of attacks started through Attack.Start directly (WeaponBehavior), which Humanoid.StartAttack never sees
        private static readonly HashSet<Attack> s_triggered = new HashSet<Attack>();
        internal static bool WasTriggered(Attack a) => a != null && s_triggered.Contains(a);
        internal static void NoteSwing(Character who, Attack atk)
        {
            if (!IsCompanion(who) || atk == null) return;
            if (s_triggered.Count > 256) s_triggered.Clear();
            s_triggered.Remove(atk);
            s_swing[who] = (Time.time, atk.m_weapon?.m_shared?.m_name ?? "?", atk.m_attackAnimation);
            if (!s_swingTimes.TryGetValue(who, out var sl)) s_swingTimes[who] = sl = new List<float>();
            sl.Add(Time.time); if (sl.Count > 200) sl.RemoveRange(0, 100);
        }
        internal static bool LastHit(Character who, out float sinceSwing) { sinceSwing = -1f; if (who != null && s_swing.TryGetValue(who, out var s) && s_lastTrigger.TryGetValue(who, out float t) && t >= s.at) { sinceSwing = t - s.at; return true; } return false; }
        internal static int HitsSince(Character who, float since) => who != null && s_hitTimes.TryGetValue(who, out var l) ? l.FindAll(x => x >= since).Count : 0;
        private static readonly Dictionary<Character, List<float>> s_hitTimes = new Dictionary<Character, List<float>>();
        private static readonly Dictionary<Character, List<float>> s_swingTimes = new Dictionary<Character, List<float>>();
        private static readonly Dictionary<Character, List<float>> s_delays = new Dictionary<Character, List<float>>();
        internal static int SwingsSince(Character who, float since) => who != null && s_swingTimes.TryGetValue(who, out var l) ? l.FindAll(x => x >= since).Count : 0;
        internal static List<float> DelaysOf(Character who) => who != null && s_delays.TryGetValue(who, out var l) ? l : new List<float>();

        // 0.2.941 (round #37: "[AnimStuck] Knut: InAttack for 2.1 s ... 'axe_secondary' m_time 4.3 s, done False"): LOG ONLY - every field of the
        // stuck Attack (bools / floats / ints / strings) plus the animator's state, so the reason it never reports done is in the log (8 a session)
        private static int s_dumps;
        internal static void DumpStuck(Character who)
        {
            if (s_dumps >= 8 || !(who is Humanoid h) || !IsCompanion(who)) return;
            var atk = h.m_currentAttack;
            if (atk == null) return;
            s_dumps++;
            try
            {
                var parts = new List<string>();
                foreach (var f in typeof(Attack).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    var t = f.FieldType;
                    if (t != typeof(bool) && t != typeof(float) && t != typeof(int) && t != typeof(string)) continue;
                    object v = f.GetValue(atk);
                    if (v is string sv && sv.Length == 0) continue;
                    parts.Add($"{f.Name}={v}");
                }
                var anim = who.GetComponentInChildren<Animator>();
                string st = "";
                if (anim != null) { var s = anim.GetCurrentAnimatorStateInfo(0); var n = anim.GetNextAnimatorStateInfo(0); st = $"; animator state hash {s.shortNameHash} t {s.normalizedTime:0.00} tag attack {s.IsTag("attack")}, next {n.shortNameHash}, in transition {anim.IsInTransition(0)}, speed {anim.speed:0.00}, enabled {anim.enabled}"; }
                FiresCore.Logging.FiresLogger.LogDebug($"[CombatHit] STUCK attack {s_dumps}/8 {who.m_name}: IsDone {atk.IsDone()}; {string.Join(", ", parts)}{st}");
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[CombatHit] stuck dump: " + e.Message); }
        }

        private static bool IsCompanion(Character c) => c != null && !(c is Player) && c.GetComponent<CompanionController>() != null;

        private static string Caller()
        {
            var st = new StackTrace(2, false);
            for (int i = 0; i < st.FrameCount && i < 12; i++)
            {
                var m = st.GetFrame(i)?.GetMethod();
                if (m == null || m.DeclaringType == null) continue;
                string t = m.DeclaringType.Name;
                if (t.Contains("CombatHitTrace") || t.StartsWith("DMD") || m.Name.StartsWith("DMD") || t.Contains("Harmony") || m.Name.Contains("_Patch")) continue;   // 0.2.938: Harmony's patched copy is a method named DMD<...> on the original type
                return t + "." + m.Name;
            }
            return "?";
        }

        private static void Say(string who, string path, string detail)
        {
            s_counts[path] = (s_counts.TryGetValue(path, out int n) ? n : 0) + 1;
            if (s_lines < MaxLines) { s_lines++; FiresCore.Logging.FiresLogger.LogDebug($"[CombatHit] {who}: {path} {detail}{(s_lines == MaxLines ? " (further hits counted only)" : "")}"); }
            if (Time.time >= s_summaryAt)
            {
                bool first = s_summaryAt == 0f; s_summaryAt = Time.time + 600f;
                if (!first) { FiresCore.Logging.FiresLogger.LogDebug($"[CombatHit] summary 10 min: {string.Join(", ", s_counts)}"); s_counts.Clear(); }
            }
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class CombatHitTrace_StartAttack
        {
            private static void Postfix(Humanoid __instance, bool __result)
            {
                if (!__result || !IsCompanion(__instance)) return;
                try
                {
                    var w = __instance.GetCurrentWeapon();
                    var atk = __instance.m_currentAttack;
                    s_swing[__instance] = (Time.time, w?.m_shared?.m_name ?? "?", atk != null ? atk.m_attackAnimation : "?");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Attack), nameof(Attack.OnAttackTrigger))]
        private static class CombatHitTrace_Trigger
        {
            private static void Prefix(Attack __instance)
            {
                try
                {
                    var who = __instance?.m_character;
                    if (!IsCompanion(who)) return;
                    s_lastTrigger[who] = Time.time;
                    s_triggered.Add(__instance);
                    if (!s_hitTimes.TryGetValue(who, out var hl)) s_hitTimes[who] = hl = new List<float>();
                    hl.Add(Time.time); if (hl.Count > 200) hl.RemoveRange(0, 100);
                    if (s_swing.TryGetValue(who, out var sw0) && Time.time - sw0.at < 3f) { if (!s_delays.TryGetValue(who, out var dl)) s_delays[who] = dl = new List<float>(); dl.Add(Time.time - sw0.at); if (dl.Count > 100) dl.RemoveRange(0, 50); }
                    string caller = Caller();
                    string path = caller.StartsWith("CharacterAnimEvent") ? "vanilla anim event" : caller.Contains("CompanionAttackBridge") ? "bridge (" + caller + ")" : caller.Contains("WeaponBehavior") ? "weapon safety fallback (" + caller + ")" : "other (" + caller + ")";
                    string t = s_swing.TryGetValue(who, out var s) ? $"t={Time.time - s.at:0.00} s after swing start (weapon {s.weapon}, anim {s.anim})" : "(no swing start seen)";
                    Say(who.m_name, path, "hit " + t);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class CombatHitTrace_Damage
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                try
                {
                    var att = hit?.GetAttacker();
                    if (!IsCompanion(att) || hit.m_skill == Skills.SkillType.None) return;
                    CombatLoopStats.NoteLanded(att, __instance);
                    if (s_lastTrigger.TryGetValue(att, out float tt) && Time.time - tt < 0.1f) return;   // the OnAttackTrigger's own hit
                    string t = s_swing.TryGetValue(att, out var s) ? $"t={Time.time - s.at:0.00} s after swing start (weapon {s.weapon}, anim {s.anim})" : "(no swing start)";
                    Say(att.m_name, "DIRECT Damage (" + Caller() + ")", $"on {__instance.m_name} {t}, {hit.GetTotalDamage():0} dmg");
                }
                catch { }
            }
        }
    }
}
