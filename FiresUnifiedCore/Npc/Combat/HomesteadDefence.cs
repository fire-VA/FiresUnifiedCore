using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// 0.2.92x ([ghost], Fire 10-27: "Companions should also actively defend the homestead, so if one companion gets in a fight with
    /// intruders, the rest of the companions should be able to back them up if needed"; Tools\HOMESTEAD_DEFENCE_PLAN_20261027.md).
    /// Before this a companion only fought what hit it or what it saw itself; nothing shared an alert.
    ///  - ALERT: a homestead companion is hit, a tamed animal of the base is hit, or a base piece is hit by a creature / another player,
    ///    within BaseRadius of the base centre. One alert per attacker; threat = the attacker's max health (level included).
    ///  - RESPONSE (scaled): staying companions within [Homestead Defence] Responder radius (40 m), nearest first, are sent at the attacker
    ///    (CompanionAI.ForceTarget) until their power (max health) reaches 1.5 x the threat; one on a critical chore (FireTending,
    ///    SmelterOperator) stays unless it is needed; civilians (no weapon; Fire 10-27 "Armed = defends", no level rule) go to the shelter (the nearest bed under a roof, else home).
    ///  - RESOLUTION: the attacker dead or gone (beyond BaseRadius + 20 m, 10 s unseen): the alert ends; chores that support it resume
    ///    through the existing ResumeAfterCombat; repairs / rebuilds wait until no alert is left plus 30 s quiet (Fire), and a new alert
    ///    pauses them.
    /// Runs on the process that owns the companions (the server sim usually); companions owned elsewhere are left to their own owner.
    /// Lines: "[Defend] alert at (x, z): <attacker> (threat t) vs <victim>; responders: ...; staying: ...; to shelter: ...",
    ///        "[Defend] resolved at (x, z): <attacker> <killed | gone> after N s; responders: ...".
    /// [Homestead Defence] Back each other up, OFF until core_homestead_defence_test passes (Fire).
    /// </summary>
    internal static class HomesteadDefence
    {
        internal const float BaseRadius = 60f, QuietSeconds = 30f;
        // 1086 ([generator], Fire 11-01: configs live in their own mods): bound by FiresCompanions; Core reads the owner's value (old default without it).
        private static readonly FiresCore.Config.OwnedSetting<bool> s_on = new FiresCore.Config.OwnedSetting<bool>("Homestead Defence", "Back each other up", false);
        private static readonly FiresCore.Config.OwnedSetting<float> s_radius = new FiresCore.Config.OwnedSetting<float>("Homestead Defence", "Responder radius", 40f);
        internal static bool s_testOn;   // core_homestead_defence_test only; never saved to the cfg

        internal static void BindConfig(BepInEx.Configuration.ConfigFile cfg) { }
        internal static bool On => s_testOn || s_on.Value;
        private static float Radius => s_radius.Value;

        internal sealed class Alert
        {
            public Character Attacker;
            public string AttackerName;
            public Vector3 At;
            public float Threat, Raised, LastSeen;
            public readonly List<CompanionController> Responders = new List<CompanionController>();
            public readonly List<CompanionController> Sheltering = new List<CompanionController>();
        }

        private static readonly List<Alert> s_alerts = new List<Alert>();
        private static float s_lastResolved = -999f;
        private static Runner s_runner;
        private static readonly Dictionary<CompanionController, Vector3> s_shelterOf = new Dictionary<CompanionController, Vector3>();

        /// <summary>No alert is active and the last one ended QuietSeconds ago: repairs / rebuilds may run (Fire: "only once all of the
        /// threats have been taken care of").</summary>
        internal static bool QuietForRepairs => s_alerts.Count == 0 && Time.time - s_lastResolved >= QuietSeconds;
        internal static bool AnyAlert => s_alerts.Count > 0;
        internal static int ActiveAlerts => s_alerts.Count;
        internal static IEnumerable<Alert> Alerts => s_alerts;
        internal static float LastResolvedAt => s_lastResolved;

        private static bool InBase(Vector3 at, out Vector3 centre)
        {
            centre = FiresCore.Npc.JobBoard.JobBoard.BaseCenter();
            return centre != Vector3.zero && Vector3.Distance(at, centre) <= BaseRadius;
        }

        private static bool Hostile(Character attacker, out string why)
        {
            why = null;
            if (attacker == null || attacker.IsDead()) return false;
            if (attacker is Player p)
            {
                // PvP raid: another player, not the base owner (the local owner in single player / the base's companions' owner)
                long owner = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
                if (p.GetPlayerID() == owner) return false;
                if (!p.IsPVPEnabled()) return false;
                why = "player " + p.GetPlayerName();
                return true;
            }
            if (attacker.IsTamed()) return false;
            if (attacker.GetComponent<CompanionController>() != null) return false;
            why = attacker.m_name;
            return true;
        }

        /// <summary>Something of the homestead was attacked by <paramref name="attacker"/>.</summary>
        internal static void Raise(Character attacker, string victim, Vector3 at)
        {
            if (!On) return;
            try
            {
                if (!Hostile(attacker, out string who) || !InBase(at, out _)) return;
                var a = s_alerts.FirstOrDefault(x => x.Attacker == attacker);
                if (a != null) { a.LastSeen = Time.time; a.At = attacker.transform.position; return; }
                a = new Alert { Attacker = attacker, AttackerName = Localization.instance != null ? Localization.instance.Localize(attacker.m_name) : attacker.m_name, At = attacker.transform.position, Threat = Mathf.Max(1f, attacker.GetMaxHealth()), Raised = Time.time, LastSeen = Time.time };
                s_alerts.Add(a);
                Respond(a, victim);
                EnsureRunner();
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[Defend] raise failed: " + e.Message); }
        }

        private static float Power(CompanionController c) => c != null && c.GetComponent<Character>() is Character ch ? Mathf.Max(25f, ch.GetMaxHealth()) : 25f;

        private static bool Civilian(CompanionController c, out string why)
        {
            why = null;
            var h = c.GetComponent<Humanoid>();
            if (h != null && h.GetCurrentWeapon() == null) { why = "no weapon"; return true; }
            return false;
        }

        private static bool Critical(CompanionController c, out string what)
        {
            what = null;
            var idle = c.GetComponent<FiresCore.Npc.CompanionIdleBehavior>();
            string n = idle?.ActiveSubBehavior?.BehaviorName;
            if (n == "FireTending" || n == "SmelterOperator") { what = n; return true; }
            return false;
        }

        private static void Respond(Alert a, string victim)
        {
            float need = a.Threat * 1.5f, have = 0f;
            var staying = CompanionController.AllCompanions.Where(c => c != null && c.isTamed && !c.ShouldBeFollowing && c.GetCompanionAI() != null
                    && c.GetComponent<ZNetView>() is ZNetView nv && nv.IsValid() && nv.IsOwner() && !(c.GetComponent<Character>()?.IsDead() ?? true)
                    && Vector3.Distance(c.transform.position, a.At) <= Radius)
                .OrderBy(c => Vector3.Distance(c.transform.position, a.At)).ToList();
            var stay = new List<string>(); var shelter = new List<string>();
            var critical = new List<CompanionController>();
            foreach (var c in staying)
            {
                if (Civilian(c, out string cw)) { a.Sheltering.Add(c); s_shelterOf[c] = ShelterFor(c); shelter.Add($"{c.companionName} ({cw})"); continue; }
                if (have >= need) break;
                if (Critical(c, out string chore)) { critical.Add(c); continue; }
                a.Responders.Add(c); have += Power(c);
            }
            foreach (var c in critical)
            {
                if (have >= need) { stay.Add($"{c.companionName} ({(Critical(c, out string w) ? w : "chore")})"); continue; }
                a.Responders.Add(c); have += Power(c);
            }
            foreach (var c in a.Responders) c.GetCompanionAI().ForceTarget(a.Attacker);
            FiresCore.Logging.FiresLogger.LogInfo($"[Defend] alert at ({a.At.x:0}, {a.At.z:0}): {a.AttackerName} (threat {a.Threat:0}) vs {victim}; responders: {(a.Responders.Count > 0 ? string.Join(", ", a.Responders.Select(c => $"{c.companionName} ({Vector3.Distance(c.transform.position, a.At):0} m)")) : "none in range")} (power {have:0} of {need:0}); staying: {(stay.Count > 0 ? string.Join(", ", stay) : "-")}; to shelter: {(shelter.Count > 0 ? string.Join(", ", shelter) : "-")}");
        }

        private static Vector3 ShelterFor(CompanionController c)
        {
            Vector3 centre = FiresCore.Npc.JobBoard.JobBoard.BaseCenter();
            Bed best = null; float bd = 40f;
            var sw = FiresCore.Npc.Core.SceneScanCensus.Start();   // 0.2.1221 [ghost]: registry, not a scene search
            IList<Bed> all = FiresCore.Npc.Core.SceneScanCensus.UseRegistries ? (IList<Bed>)FiresCore.Npc.JobBoard.HomesteadStations.Live(FiresCore.Npc.JobBoard.HomesteadStations.Beds) : UnityEngine.Object.FindObjectsByType<Bed>(FindObjectsSortMode.None);
            foreach (var b in all)
            {
                if (b == null) continue;
                float d = Vector3.Distance(b.transform.position, centre);
                if (d >= bd) continue;   // distance first: the roof probe is a raycast
                if (!Cover.IsUnderRoof(b.transform.position + Vector3.up * 0.5f)) continue;   // lead 10-27: a shelter is a bed under a roof (inside), never an exposed one
                bd = d; best = b;
            }
            FiresCore.Npc.Core.SceneScanCensus.Note("HomesteadDefence.ShelterFor", sw, all.Count);
            if (best != null) return best.transform.position;
            var idle = c.GetComponent<FiresCore.Npc.CompanionIdleBehavior>();
            return idle != null && idle.HasHomePosition ? idle.HomePosition : centre;
        }

        private static void EnsureRunner()
        {
            if (s_runner != null) return;
            var go = new GameObject("FiresHomesteadDefence");
            UnityEngine.Object.DontDestroyOnLoad(go);
            s_runner = go.AddComponent<Runner>();
        }

        private sealed class Runner : MonoBehaviour
        {
            private float m_tickAt;
            private void Update() { long pf = FiresCore.Diagnostics.CompanionSystemsProfile.Begin(); try { Tick0(); } finally { FiresCore.Diagnostics.CompanionSystemsProfile.End("defence runner", pf); } }
            private void Tick0()
            {
                // civilians walk to the shelter every frame while any alert lasts (an order-level move, like a "go there")
                if (s_alerts.Count > 0)
                    foreach (var kv in s_shelterOf.ToList())
                    {
                        var c = kv.Key; var ai = c != null ? c.GetCompanionAI() : null;
                        if (ai == null) { s_shelterOf.Remove(c); continue; }
                        ai.RequestPathfindingMovement(kv.Value, true, 2f, FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.PlayerCommand, "HomesteadDefence");
                    }
                if (Time.time < m_tickAt) return;
                m_tickAt = Time.time + 0.5f;
                Tick();
            }
        }

        private static void Tick()
        {
            for (int i = s_alerts.Count - 1; i >= 0; i--)
            {
                var a = s_alerts[i];
                bool dead = a.Attacker == null || a.Attacker.IsDead();
                bool here = !dead && InBase(a.Attacker.transform.position, out _) || (!dead && Vector3.Distance(a.Attacker.transform.position, FiresCore.Npc.JobBoard.JobBoard.BaseCenter()) <= BaseRadius + 20f);
                if (!dead && here) { a.LastSeen = Time.time; a.At = a.Attacker.transform.position; continue; }
                if (!dead && Time.time - a.LastSeen < 10f) continue;
                s_alerts.RemoveAt(i);
                s_lastResolved = Time.time;
                FiresCore.Logging.FiresLogger.LogInfo($"[Defend] resolved at ({a.At.x:0}, {a.At.z:0}): {a.AttackerName} {(dead ? "killed" : "gone")} after {Time.time - a.Raised:0} s; responders: {(a.Responders.Count > 0 ? string.Join(", ", a.Responders.Where(c => c != null).Select(c => c.companionName)) : "-")} (their chores resume; repairs / rebuilds after {QuietSeconds:0} s quiet)");
            }
            if (s_alerts.Count == 0 && s_shelterOf.Count > 0)
            {
                foreach (var c in s_shelterOf.Keys) { try { c?.GetMovementAuthority()?.ReleaseAuthority("HomesteadDefence"); } catch { } }
                s_shelterOf.Clear();
            }
        }

        internal static void ClearForTest() { s_alerts.Clear(); s_shelterOf.Clear(); s_lastResolved = -999f; }

        // ---- hooks: a tamed animal of the base hit, a base piece hit (companions: CompanionAI.Targeting's fight-back branch calls Raise)
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class HomesteadDefence_TamedHit
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                if (!On || __instance == null || hit == null || !__instance.IsTamed() || __instance.GetComponent<CompanionController>() != null) return;
                try { Raise(hit.GetAttacker(), "tamed " + __instance.m_name, __instance.transform.position); } catch { }
            }
        }

        [HarmonyPatch(typeof(WearNTear), "RPC_Damage")]
        private static class HomesteadDefence_PieceHit
        {
            private static void Prefix(WearNTear __instance, HitData hit)
            {
                if (!On || __instance == null || hit == null) return;
                try { var att = hit.GetAttacker(); if (att != null) Raise(att, "piece " + Utils.GetPrefabName(__instance.gameObject), __instance.transform.position); } catch { }
            }
        }
    }
}
