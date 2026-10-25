using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Diagnostic for companions whose melee attacks land no hits. Log Level = Debug: for a companion owned on this peer, one [AttackProbe] line per attack
    /// when it stops - whether StartAttack took it, how many times vanilla's attack trigger (Attack.OnAttackTrigger, which casts the melee
    /// hit) fired, the hits that reached Character.Damage with this companion as attacker, and the animator's culling mode / speed /
    /// enabled plus whether any of its renderers is visible. A refused start is logged too (at most every 2 s per companion).
    /// Answers: trigger missing (animator / visibility - a player who alt-tabs would see it too) or trigger fired and hits dropped.
    /// </summary>
    internal static class CompanionAttackProbe
    {
        private sealed class Track { internal float Start; internal int Triggers, Hits; internal string Target, Hit = ""; }
        private static readonly Dictionary<int, Track> s_open = new Dictionary<int, Track>();
        private static readonly Dictionary<int, float> s_refusedAt = new Dictionary<int, float>();
        private static readonly AccessTools.FieldRef<Attack, Humanoid> s_attackOwner = AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");

        private static bool Enabled => FiresCore.Logging.FiresLogger.DebugEnabled;
        private static bool s_firstRun, s_checked;

        // When the local player spawns, one Info line per hooked method naming EVERY prefix / postfix owner (Harmony.GetPatchInfo), so a
        // round shows whether ours is attached and whose prefix may skip the original. Once per session.
        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class SelfCheckPatch
        {
            [HarmonyPostfix]
            private static void Post()
            {
                if (s_checked) return;
                s_checked = true;
                try
                {
                    Check(AccessTools.Method(typeof(Humanoid), nameof(Humanoid.StartAttack)), "Humanoid.StartAttack");
                    Check(AccessTools.Method(typeof(Attack), nameof(Attack.OnAttackTrigger)), "Attack.OnAttackTrigger");
                    Check(AccessTools.Method(typeof(Attack), nameof(Attack.Stop)), "Attack.Stop");
                    Check(AccessTools.Method(typeof(Character), nameof(Character.Damage)), "Character.Damage");
                }
                catch (System.Exception ex) { FiresCore.Logging.FiresLogger.LogInfo("[AttackProbe] self-check failed: " + ex.Message); }
            }
            private static void Check(System.Reflection.MethodBase m, string label)
            {
                var info = m != null ? Harmony.GetPatchInfo(m) : null;
                string Owners(IEnumerable<Patch> ps) { var l = new List<string>(); if (ps != null) foreach (var p in ps) l.Add(p.owner + "." + p.PatchMethod?.DeclaringType?.Name); return l.Count == 0 ? "none" : string.Join(", ", l); }
                FiresCore.Logging.FiresLogger.LogInfo(m == null ? $"[AttackProbe] self-check: {label} NOT FOUND"
                    : info == null ? $"[AttackProbe] self-check: {label} has NO patches at all"
                    : $"[AttackProbe] self-check: {label} prefixes [{Owners(info.Prefixes)}] postfixes [{Owners(info.Postfixes)}] transpilers [{Owners(info.Transpilers)}]");
            }
        }

        // A companion counts by its controller map, its ZDO (companion_owner set) or its prefab name, and the first
        // time a companion-looking attacker is skipped the reason is logged once
        private static readonly int s_ownerKey = "companion_owner".GetStableHashCode();
        private static readonly HashSet<int> s_skipLogged = new HashSet<int>();
        private static bool OwnedCompanion(Character c)
        {
            if (c == null || c.IsPlayer()) return false;
            var nv = c.GetComponent<ZNetView>();
            var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            bool mapped = CompanionController.TryGet(c, out _);
            bool byZdo = zdo != null && zdo.GetLong(s_ownerKey, 0L) != 0L;
            bool byName = c.gameObject.name.StartsWith("CompanionNpc", StringComparison.Ordinal);
            if (!mapped && !byZdo && !byName) return false;
            bool owner = nv != null && nv.IsValid() && nv.IsOwner();
            if (!owner && s_skipLogged.Add(c.GetInstanceID()))
                FiresCore.Logging.FiresLogger.LogDebug($"[AttackProbe] {c.m_name}: not probed here - its ZDO is owned by peer {(zdo != null ? zdo.GetOwner() : 0L)}, this peer is {ZDOMan.GetSessionID()} (controller {mapped}, companion ZDO {byZdo})");
            return owner;
        }

        private static string Look(Character c)
        {
            var anim = c.GetComponentInChildren<Animator>(true);
            bool visible = false;
            foreach (var r in c.GetComponentsInChildren<Renderer>(false)) if (r.isVisible) { visible = true; break; }
            return anim == null ? $"no animator, renderer visible {visible}"
                : $"animator {anim.cullingMode} speed {anim.speed:0.##} enabled {anim.enabled}, renderer visible {visible}, app focused {Application.isFocused}";
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class StartPatch
        {
            [HarmonyPostfix]
            private static void Post(Humanoid __instance, Character target, bool __result)
            {
                try
                {
                    // Say once that this postfix runs at all, for anyone
                    if (!s_firstRun) { s_firstRun = true; FiresCore.Logging.FiresLogger.LogInfo($"[AttackProbe] self-check: the StartAttack postfix ran (first caller {__instance?.m_name}, result {__result}, companion {CompanionController.TryGet(__instance, out _)}, debug {Enabled})"); }
                    if (!Enabled || !OwnedCompanion(__instance)) return;
                    int id = __instance.GetInstanceID();
                    if (__result)
                    {
                        if (s_open.TryGetValue(id, out var prev))
                            FiresCore.Logging.FiresLogger.LogDebug($"[AttackProbe] {__instance.m_name}: a new attack started while the last one ({Time.time - prev.Start:0.0} s, triggers {prev.Triggers}, hits {prev.Hits}) never stopped");
                        s_open[id] = new Track { Start = Time.time, Target = target != null ? target.m_name : "none" };
                    }
                    else if (!s_refusedAt.TryGetValue(id, out float at) || Time.time - at > 2f)
                    {
                        s_refusedAt[id] = Time.time;
                        FiresCore.Logging.FiresLogger.LogDebug($"[AttackProbe] {__instance.m_name}: StartAttack refused (target {(target != null ? target.m_name : "none")}, InAttack {__instance.InAttack()}); {Look(__instance)}");
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Attack), nameof(Attack.OnAttackTrigger))]
        private static class TriggerPatch
        {
            [HarmonyPrefix]
            private static void Pre(Attack __instance)
            {
                try
                {
                    if (!Enabled) return;
                    var h = s_attackOwner(__instance);
                    if (h != null && s_open.TryGetValue(h.GetInstanceID(), out var t)) t.Triggers++;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class HitPatch
        {
            [HarmonyPrefix]
            private static void Pre(Character __instance, HitData hit)
            {
                try
                {
                    if (!Enabled || hit == null || s_open.Count == 0) return;
                    var attacker = hit.GetAttacker();
                    if (attacker != null && s_open.TryGetValue(attacker.GetInstanceID(), out var t)) { t.Hits++; if (t.Hit.Length < 60) t.Hit += (t.Hit.Length > 0 ? ", " : "") + __instance.m_name; }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Attack), nameof(Attack.Stop))]
        private static class StopPatch
        {
            [HarmonyPostfix]
            private static void Post(Attack __instance)
            {
                try
                {
                    if (!Enabled) return;
                    var h = s_attackOwner(__instance);
                    if (h == null || !s_open.TryGetValue(h.GetInstanceID(), out var t)) return;
                    s_open.Remove(h.GetInstanceID());
                    FiresCore.Logging.FiresLogger.LogDebug($"[AttackProbe] {h.m_name}: attack '{__instance.m_attackAnimation}' on {t.Target} ran {Time.time - t.Start:0.0} s: trigger fired {t.Triggers}x, hits {t.Hits}{(t.Hits > 0 ? " (" + t.Hit + ")" : "")}; {Look(h)}");
                }
                catch { }
            }
        }
    }
}
