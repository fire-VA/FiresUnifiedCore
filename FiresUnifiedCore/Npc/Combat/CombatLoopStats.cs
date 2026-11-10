using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// One Debug line per companion fight, so the test rounds can
    /// put the companion's rhythm next to FiresTestBot's - "[CombatLoop] name: N attacks, max chain M, avg gap X s, dodges D, T s".
    /// A fight ends 5 s after its last swing.
    /// </summary>
    internal static class CombatLoopStats
    {
        private sealed class Fight { public string Name; public int Attacks, MaxChain, Dodges; public float First, Last, GapSum; public int Gaps; }
        private static readonly Dictionary<int, Fight> s_fights = new Dictionary<int, Fight>();
        private const float FightEndsAfter = 5f;
        private const float StandBelow = 0.3f;

        private static Fight For(Character c, bool create)
        {
            if (c == null) return null;
            int id = c.GetInstanceID();
            if (s_fights.TryGetValue(id, out var f)) return f;
            if (!create) return null;
            f = new Fight { Name = c.m_name, First = Time.time, Last = Time.time };
            s_fights[id] = f;
            return f;
        }

        internal static void NoteAttack(Character c, int chainStep)
        {
            var f = For(c, true);
            if (f == null) return;
            var e = Engage(c, "swing with no AI target");
            if (e != null)
            {
                if (e.FirstSwing < 0f) e.FirstSwing = Time.time;
                e.Swings++; e.LastAct = Time.time;
                ReadKit(c, e);
                e.KitSwings[e.Kit] = e.KitSwings.TryGetValue(e.Kit, out int ks) ? ks + 1 : 1;
            }
            if (f.Attacks > 0) { f.GapSum += Time.time - f.Last; f.Gaps++; }
            f.Attacks++;
            if (chainStep > f.MaxChain) f.MaxChain = chainStep;
            f.Last = Time.time;
        }

        internal static void NoteBlock(Character c, float seconds)
        {
            if (c != null && s_eng.TryGetValue(c.GetInstanceID(), out var e)) { e.Blocks++; e.BlockSeconds += seconds; }
        }

        internal static void NoteDodge(Character c)
        {
            var f = For(c, true);
            if (f == null) return;
            f.Dodges++;
            f.Last = Time.time;
        }

        // ---- [CombatFight] ([ghost] 11-02, lead: the combat baseline had no number for time to first hit, damage taken or walk vs run). LOG ONLY, Debug:
        // one line per companion per engagement, foe seen to last action, with why it started and ended so a test phase split in two can be matched up.
        // Time is sampled in Tick (the ZDO owner's UpdateCombat). Running / jogging / walking / standing add up to the fight; recovery is StaminaManager's
        // own no-swing flag and overlaps them. "Landed" = Character.Damage reached a target, so a blocked hit counts; "taken" = m_onDamaged after armour.
        private sealed class FoeStat { public int Landed, HitsTaken; public float Taken; }
        private sealed class Eng
        {
            public string Name, Start, Foe, FoeEnd; public StaminaManager Sm;
            // Approach: first target picked to first in reach or first swing. The companion's OWN path and time, so a foe walking up to it doesn't count as its speed.
            public float ApproachWalkFlag, NextSpeedTerms; public int WalkCallers;
            public bool ApproachOn, ApproachDone; public Vector3 ApproachLast; public float ApproachStartDist, ApproachEndDist, ApproachStart, ApproachEnd, ApproachPath, ApproachTop, ApproachRunFlag;
            public Character Target; public float Seen, FirstSwing = -1f, FirstLanded = -1f, LastAct, LastSample, Taken, Run, Jog, Walk, Stand, Recovery, RunMetres, JogMetres, WalkMetres, TopSpeed;
            public int Swings, Landed, HitsTaken, Blocks; public float BlockSeconds;
            public string Kit; public object KitRight, KitLeft;                                          // what it holds now; "name qN + shield qN"
            public readonly Dictionary<string, int> KitSwings = new Dictionary<string, int>();           // in order: every kit held, swings with each
            public readonly List<string> Kits = new List<string>();
            public readonly List<string> FoeOrder = new List<string>();                                  // every foe: prefab Ln
            public readonly Dictionary<string, FoeStat> FoeStats = new Dictionary<string, FoeStat>();
        }
        private static readonly Dictionary<int, Eng> s_eng = new Dictionary<int, Eng>();
        private static readonly HashSet<int> s_damageHooked = new HashSet<int>();
        private static readonly HashSet<int> s_hitHooked = new HashSet<int>();

        private static Eng Engage(Character c, string why)
        {
            if (c == null) return null;
            int id = c.GetInstanceID();
            if (s_eng.TryGetValue(id, out var e)) return e;
            e = new Eng { Name = c.m_name, Start = why, Seen = Time.time, LastAct = Time.time, LastSample = Time.time, Sm = c.GetComponent<StaminaManager>() };
            s_eng[id] = e;
            ReadKit(c, e);
            return e;
        }

        internal static void NoteTarget(Character c, Character foe)
        {
            if (c == null) return;
            if (foe == null)
            {
                if (s_eng.TryGetValue(c.GetInstanceID(), out var open) && open.Foe != null) { open.FoeEnd = "target cleared"; open.Foe = null; open.LastAct = Time.time; }
                return;
            }
            var e = Engage(c, "target " + foe.m_name);
            StatOf(e, foe);
            e.Foe = foe.m_name; e.Target = foe; e.FoeEnd = null; e.LastAct = Time.time;
            if (!e.ApproachOn && !e.ApproachDone)
            {
                e.ApproachOn = true; e.ApproachStart = Time.time; e.ApproachLast = c.transform.position;
                e.ApproachStartDist = e.ApproachEndDist = Vector3.Distance(c.transform.position, foe.transform.position);
            }
        }

        internal static void NoteLanded(Character attacker, Character victim)
        {
            if (attacker == null || !s_eng.TryGetValue(attacker.GetInstanceID(), out var e)) return;
            if (e.FirstLanded < 0f) e.FirstLanded = Time.time;
            e.Landed++; e.LastAct = Time.time;
            var fs = StatOf(e, victim);
            if (fs != null) fs.Landed++;
        }

        private static FoeStat StatOf(Eng e, Character foe)
        {
            if (foe == null) return null;
            string key = Utils.GetPrefabName(foe.gameObject) + " L" + foe.GetLevel();
            if (e.FoeStats.TryGetValue(key, out var s)) return s;
            s = new FoeStat();
            e.FoeStats[key] = s;
            e.FoeOrder.Add(key);
            return s;
        }

        // Compares references each sample; the names are only built when the held items change.
        private static void ReadKit(Character c, Eng e)
        {
            if (!(c is Humanoid h)) { if (e.Kit == null) { e.Kit = "not humanoid"; e.Kits.Add(e.Kit); } return; }
            var right = h.GetCurrentWeapon();
            var left = h.GetLeftItem();
            if (e.Kit != null && ReferenceEquals(right, e.KitRight) && ReferenceEquals(left, e.KitLeft)) return;
            e.KitRight = right; e.KitLeft = left;
            string kit = ItemName(right);
            if (left != null && !ReferenceEquals(left, right)) kit += " + " + ItemName(left);
            e.Kit = kit;
            if (!e.Kits.Contains(kit)) e.Kits.Add(kit);
        }

        private static string ItemName(ItemDrop.ItemData item) => item == null ? "none" : (item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared?.m_name ?? "?") + " q" + item.m_quality;

        private static void TickEngage(Character c)
        {
            int id = c.GetInstanceID();
            if (s_damageHooked.Add(id)) c.m_onDeath += () => End(c, "companion died");   // vanilla destroys it right after; no later Tick would close the line
            if (s_hitHooked.Add(id))
                c.m_onDamaged += (damage, attacker) =>
                {
                    if (damage > 40f) FiresCore.Logging.FiresLogger.LogDebug($"[CombatFight] {c.m_name}: big hit {damage:0.0} from {(attacker != null ? attacker.m_name : "?")}, health now {c.GetHealth():0}/{c.GetMaxHealth():0}, dead {c.IsDead()}, attacker level {(attacker != null ? attacker.GetLevel() : 0)}, weapon {(attacker is Humanoid h && h.GetCurrentWeapon() != null ? h.GetCurrentWeapon().m_shared.m_name : "?")}");
                    var hit = Engage(c, "took a hit from " + (attacker != null ? attacker.m_name : "?"));
                    if (hit != null)
                    {
                        hit.Taken += damage; hit.HitsTaken++; hit.LastAct = Time.time;
                        var fs = StatOf(hit, attacker);
                        if (fs != null) { fs.Taken += damage; fs.HitsTaken++; }
                    }
                };
            if (!s_eng.TryGetValue(id, out var e)) return;
            ReadKit(c, e);
            float dt = Time.time - e.LastSample;
            e.LastSample = Time.time;
            // [ghost] 11-02: the flags read "running" at about 1.3 m/s in the smoke, so the real speed goes next to each bucket
            float speed = c.GetVelocity().magnitude;
            if (speed > e.TopSpeed) e.TopSpeed = speed;
            if (e.ApproachOn && !e.ApproachDone) TickApproach(c, e, speed, dt);
            if (speed < StandBelow) e.Stand += dt;
            else if (c.IsRunning()) { e.Run += dt; e.RunMetres += speed * dt; }
            else if (c.IsWalking()) { e.Walk += dt; e.WalkMetres += speed * dt; }
            else { e.Jog += dt; e.JogMetres += speed * dt; }
            if (e.Sm != null && e.Sm.IsRecovering()) e.Recovery += dt;
            if (e.Foe != null && (e.Target == null || e.Target.IsDead())) { e.FoeEnd = "foe dead or gone"; e.Foe = null; e.LastAct = Time.time; }
            if (e.Foe == null && Time.time - e.LastAct >= FightEndsAfter) End(c, e.FoeEnd ?? "no target, no action");
        }

        // [ghost] 11-02, LOG ONLY: who turns the walk flag on while a companion is approaching a foe (vanilla UpdateWalking lets m_walk beat m_running).
        private static readonly HarmonyLib.AccessTools.FieldRef<Character, bool> WalkField = HarmonyLib.AccessTools.FieldRefAccess<Character, bool>("m_walk");

        [HarmonyLib.HarmonyPatch(typeof(Character), nameof(Character.SetWalk))]
        private static class WalkWriterTrace
        {
            private static void Prefix(Character __instance, bool walk)
            {
                if (!walk || __instance == null || !s_eng.TryGetValue(__instance.GetInstanceID(), out var e) || !e.ApproachOn || e.ApproachDone || e.WalkCallers >= 3) return;
                e.WalkCallers++;
                var st = new System.Diagnostics.StackTrace(1, false);
                var who = new List<string>();
                for (int i = 0; i < st.FrameCount && who.Count < 4; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    if (m != null && m.DeclaringType != null) who.Add(m.DeclaringType.Name + "." + m.Name);
                }
                FiresCore.Logging.FiresLogger.LogDebug($"[CombatWalk] {e.Name}: SetWalk(true) during approach from {string.Join(" < ", who)}");
            }
        }

        // [ghost] 11-02, LOG ONLY: every term of vanilla UpdateWalking's speed during an approach, at most once a second. A postfix runs before
        // CompanionSpeedRamp's finalizer, so m_runSpeed here is the ramped value this step really used.
        [HarmonyLib.HarmonyPatch(typeof(Character), "UpdateWalking")]
        private static class SpeedTermsTrace
        {
            private static void Postfix(Character __instance)
            {
                if (__instance == null || !s_eng.TryGetValue(__instance.GetInstanceID(), out var e) || !e.ApproachOn || e.ApproachDone || Time.time < e.NextSpeedTerms) return;
                e.NextSpeedTerms = Time.time + 1f;
                float effects = 1f;
                __instance.m_seman.ApplyStatusEffectSpeedMods(ref effects, __instance.m_moveDir);
                Vector3 v = __instance.m_body != null ? __instance.m_body.linearVelocity : Vector3.zero;
                v.y = 0f;
                FiresCore.Logging.FiresLogger.LogDebug($"[CombatWalk] {e.Name}: speed terms: run speed {__instance.m_runSpeed:0.00}, jog {__instance.m_speed:0.00}, walk {__instance.m_walkSpeed:0.00}, run factor {__instance.GetRunSpeedFactor():0.00}, move dir length {__instance.m_moveDir.magnitude:0.00}, attack factor {__instance.GetAttackSpeedFactorMovement():0.00}, in attack {__instance.InAttack()}, effects factor {effects:0.00}, enemy size {Game.m_enemySpeedSize:0.00}, acceleration {__instance.m_acceleration:0.00}, m_run {__instance.m_run}, running {__instance.IsRunning()}, on ground {__instance.IsOnGround()}, can move {__instance.CanMove()}, current vel {__instance.m_currentVel.magnitude:0.00}, body {v.magnitude:0.00}, crouched {(Movement.CompanionStance.TryGet(__instance, out var stance) && stance.IsCrouching)}, owner {(CompanionController.TryGet(__instance, out var cc) && cc.GetOwner() != null ? $"crouched {cc.GetOwner().IsCrouching()}, walking {cc.GetOwner().IsWalking()}, speed {cc.GetOwner().GetVelocity().magnitude:0.00}" : "none")}");
            }
        }

        private static void TickApproach(Character c, Eng e, float speed, float dt)
        {
            Vector3 here = c.transform.position, step = here - e.ApproachLast;
            step.y = 0f;
            e.ApproachLast = here;
            e.ApproachPath += step.magnitude;
            if (speed > e.ApproachTop) e.ApproachTop = speed;
            if (c.IsRunning()) e.ApproachRunFlag += dt;
            if (WalkField(c)) e.ApproachWalkFlag += dt;
            if (e.Target != null) e.ApproachEndDist = Vector3.Distance(here, e.Target.transform.position);
            float reach = (c as Humanoid)?.GetCurrentWeapon()?.m_shared?.m_attack?.m_attackRange ?? 2f;
            if (e.FirstSwing >= 0f || (e.Target != null && e.ApproachEndDist <= reach + e.Target.GetRadius()) || e.Target == null)
            {
                e.ApproachDone = true; e.ApproachEnd = Time.time;
                FiresCore.Logging.FiresLogger.LogDebug($"[CombatWalk] {e.Name}: approach {e.ApproachEnd - e.ApproachStart:0.0} s, m_walk on {e.ApproachWalkFlag:0.0} s, run flag {e.ApproachRunFlag:0.0} s");
            }
        }

        private static string ApproachText(Eng e)
        {
            if (!e.ApproachOn) return "none";
            float t = (e.ApproachDone ? e.ApproachEnd : e.LastSample) - e.ApproachStart;
            return $"{e.ApproachStartDist:0.0} m to {e.ApproachEndDist:0.0} m in {t:0.0} s, own path {e.ApproachPath:0.0} m at {Avg(e.ApproachPath, t):0.0} m/s, top {e.ApproachTop:0.0}, run flag {e.ApproachRunFlag:0.0} s{(e.ApproachDone ? "" : ", never in reach")}";
        }

        private static float Avg(float metres, float seconds) => seconds > 0f ? metres / seconds : 0f;

        private static string KitsText(Eng e)
        {
            var parts = new List<string>();
            foreach (var k in e.Kits) parts.Add($"{k}: {(e.KitSwings.TryGetValue(k, out int n) ? n : 0)} swings");
            return string.Join(", ", parts);
        }

        private static string FoesText(Eng e)
        {
            var parts = new List<string>();
            foreach (var k in e.FoeOrder) { var s = e.FoeStats[k]; parts.Add($"{k}: landed {s.Landed}, took {s.Taken:0.0} in {s.HitsTaken}"); }
            return string.Join(", ", parts);
        }

        internal static void End(Character c, string why)
        {
            if (c == null || !s_eng.TryGetValue(c.GetInstanceID(), out var e)) return;
            s_eng.Remove(c.GetInstanceID());
            string Since(float at) => at < 0f ? "never" : (at - e.Seen).ToString("0.0") + " s";
            FiresCore.Logging.FiresLogger.LogDebug($"[CombatFight] {e.Name}: {e.LastSample - e.Seen:0.0} s (start: {e.Start}; end: {why}); kit [{KitsText(e)}]; foes [{FoesText(e)}]; approach [{ApproachText(e)}]; first swing {Since(e.FirstSwing)}, first landed {Since(e.FirstLanded)}; swings {e.Swings}, landed {e.Landed}; taken {e.Taken:0.0} in {e.HitsTaken} hits; blocking {e.BlockSeconds:0.0} s in {e.Blocks}; running {e.Run:0.0} s at {Avg(e.RunMetres, e.Run):0.0} m/s, jogging {e.Jog:0.0} s at {Avg(e.JogMetres, e.Jog):0.0} m/s, walking {e.Walk:0.0} s at {Avg(e.WalkMetres, e.Walk):0.0} m/s, standing {e.Stand:0.0} s; top speed {e.TopSpeed:0.0} m/s; no-swing recovery {e.Recovery:0.0} s");
        }

        internal static void Tick(Character c)
        {
            TickEngage(c);
            var f = For(c, false);
            if (f == null || Time.time - f.Last < FightEndsAfter) return;
            s_fights.Remove(c.GetInstanceID());
            if (f.Attacks == 0 && f.Dodges == 0) return;
            FiresCore.Logging.FiresLogger.LogDebug($"[CombatLoop] {f.Name}: {f.Attacks} attacks, max chain {f.MaxChain}, avg gap {(f.Gaps > 0 ? f.GapSum / f.Gaps : 0f):0.00} s, dodges {f.Dodges}, {f.Last - f.First:0.0} s");
        }
    }
}