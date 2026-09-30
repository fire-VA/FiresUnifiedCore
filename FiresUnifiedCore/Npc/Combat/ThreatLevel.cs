using System.Collections.Generic;
using FiresCore.Npc.Archetypes;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// How dangerous the fight around a body is, as one number and a tactic (Tools\COMBAT_TACTICS.md §2; Fire, R72 Crypt4: "recognize
    /// the threat level when there's many bad guys bearing down on him and play accordingly"). Every hostile within
    /// <see cref="Radius"/> m that targets the body or its party counts. Its damage per second is its weapon's damage (with the
    /// level bonus) after the body's armour, over its attack interval; the body's seconds to die = health ÷ the sum. The level keys
    /// on that and on how many foes can hit at once. Body-agnostic: the FDT bot (CombatAdvisor) and a companion ask the same thing.
    /// 0.2.198 chooses fight / space / retreat; pull, chokepoint and run past come with the escape rules (0.2.200).
    /// </summary>
    public static class ThreatLevel
    {
        public enum Level { Fight, Space, Pull, Chokepoint, RunPast, Retreat }

        public const float Radius = 15f;
        /// <summary>Seconds to die under which the body breaks off; and between which it keeps the foes on one side.</summary>
        public const float RetreatSeconds = 6f, SpaceSeconds = 20f;
        /// <summary>A foe whose biggest hit (after armour) is at least this share of our health is a one-hit kill: retreat.</summary>
        public const float BurstShare = 0.9f;
        /// <summary>How far a flier counts as able to hit us when its weapon can't be read (a drake's spit).</summary>
        public const float FlyerReach = 22f;
        /// <summary>Stamina share under which two foes in reach mean retreat.</summary>
        public const float RetreatStamina = 0.15f;
        /// <summary>A foe this far off the nearest one's bearing (degrees) is on a flank.</summary>
        public const float FlankAngle = 60f;
        // An easier level only after the harder one has held this long (no flicker at a boundary).
        private const float EaseAfter = 1.5f;
        // Leaving retreat: seconds to die above this and stamina above RetreatExitStamina.
        private const float RetreatExitSeconds = 10f, RetreatExitStamina = 0.3f, MaxRetreatSeconds = 8f;
        // Each foe's swing also takes its animation: added to its weapon's AI attack interval.
        private const float SwingSeconds = 1f;

        // When a body's retreat was last spent (no new retreat for MaxRetreatSeconds after, unless death is close).
        private static readonly Dictionary<Character, float> s_retreatSpent = new Dictionary<Character, float>();

        public struct Reading
        {
            public Level Level;
            /// <summary>Hostiles within <see cref="Radius"/> after the body or its party.</summary>
            public int Foes;
            /// <summary>Of those, within their own reach + 1 m of the body.</summary>
            public int InReach;
            /// <summary>Of those in reach, the ones targeting the body itself (the watchdog's "threatened").</summary>
            public int OnMe;
            public float Dps;
            public float SecondsToDie;
            public float Health, MaxHealth, Stamina;
            /// <summary>The biggest single hit (after armour) a foe in reach can land, and which foe.</summary>
            public float Burst;
            public Character BurstFrom;
            /// <summary>The nearest foe after the body, or null.</summary>
            public Character Nearest;
            /// <summary>A foe that hit the ship the body is aboard (0.2.201), or null; the verdict names it.</summary>
            public Character ShipFoe;
            /// <summary>Flat unit direction away from the foes (their damage-weighted centre), zero with none.</summary>
            public Vector3 Away;
            /// <summary>A flat step that brings flanking foes round to the front (zero when they already are), for "space".</summary>
            public Vector3 FlankShift;
            public string Why;

            public override string ToString() =>
                $"{Name(Level)} ({Foes} foe(s), {InReach} in reach, {(SecondsToDie >= 999f ? "-" : SecondsToDie.ToString("0.0"))} s to die; hp {Health:0}/{MaxHealth:0}, stamina {Stamina * 100f:0} %)";
        }

        /// <summary>
        /// Whether <paramref name="foe"/> is after <paramref name="self"/> (or its party). A monster's AI target lives on its OWNER's
        /// peer only (vanilla MonsterAI.m_targetCreature); off the owner (a dedicated server simulating, another player's client) it
        /// reads null, and R76's drake that killed the bot counted as "0 foe(s) … no foe after us". There: an ALERTED enemy (the
        /// synced alert flag) for which we're the nearest player counts. <paramref name="onMe"/>: after self itself.
        /// </summary>
        public static bool IsAfter(Character foe, Character self, out bool onMe)
        {
            onMe = false;
            if (foe == null || self == null) return false;
            BaseAI ai = foe.GetBaseAI();
            if (ai == null) return false;
            // A foe going for the ship we're aboard is after its whole crew (0.2.201; Fire, R76: a Serpent bit the Karve 4 times
            // and nothing aboard reacted).
            Ship myShip = ShipOf(self);
            if (myShip != null && IsAfterShip(foe, myShip)) return true;
            Character target = TargetOf(ai);
            if (target != null)
            {
                onMe = target == self;
                return onMe || ClassTargeting.IsPartyMember(self, target) || (myShip != null && ShipOf(target) == myShip);
            }
            if (foe.m_nview != null && foe.m_nview.IsOwner()) return false;   // the owner knows: no target is no target
            ZDO zdo = foe.m_nview != null ? foe.m_nview.GetZDO() : null;
            bool alerted = zdo != null ? zdo.GetBool(ZDOVars.s_alert) : ai.IsAlerted();
            if (!alerted || !BaseAI.IsEnemy(foe, self)) return false;
            Vector3 at = foe.transform.position;
            float mine = Vector3.Distance(at, self.transform.position);
            foreach (Player player in Player.GetAllPlayers())
            {
                if (player == null || player == self || ClassTargeting.IsPartyMember(self, player)) continue;
                // Crew on the same ship count as us: the foe nearest a shipmate is after the ship.
                if (myShip != null && ShipOf(player) == myShip) continue;
                if (Vector3.Distance(at, player.transform.position) < mine - 1f) return false;
            }
            onMe = true;
            return true;
        }

        public static bool IsAfter(Character foe, Character self) => IsAfter(foe, self, out _);

        // ---- Who a creature is after, on any peer ----

        // FGN 1.5.12+ (TargetSync): the owner writes its monsters' target (a ZDOID) and its own session id to the ZDO. Readers need no
        // FGN; without it the keys are simply absent.
        private static readonly KeyValuePair<int, int> s_fgnTargetKey = ZDO.GetHashZDOID("fgn_target");
        private static readonly int s_fgnTargetByKey = "fgn_target_by".GetStableHashCode();
        private static readonly HashSet<string> s_targetSourcesLogged = new HashSet<string>();

        /// <summary>
        /// Who <paramref name="ai"/>'s creature is after, on any peer: its owner reads vanilla's target; everyone else reads FGN's
        /// synced target when it was written by the current owner and the creature still has one (vanilla's synced "have target"
        /// flag); else vanilla's (null off the owner, where callers fall back to the synced alert flag, see <see cref="IsAfter"/>).
        /// </summary>
        public static Character TargetOf(BaseAI ai)
        {
            if (ai == null) return null;
            ZNetView view = ai.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || view.IsOwner())
            {
                Character own = ai.GetTargetCreature();
                if (own != null) NoteTargetSource("owner (vanilla target)");
                return own;
            }
            Character synced = SyncedTarget(view.GetZDO(), ai);
            if (synced != null)
            {
                NoteTargetSource("fgn_target (FGN synced target)");
                return synced;
            }
            return ai.GetTargetCreature();
        }

        public static Character TargetOf(Character c) => TargetOf(c != null ? c.GetBaseAI() : null);

        private static Character SyncedTarget(ZDO zdo, BaseAI ai)
        {
            if (zdo == null || ai == null || !ai.HaveTarget()) return null;
            long by = zdo.GetLong(s_fgnTargetByKey, 0L);
            if (by == 0L || by != zdo.GetOwner()) return null;
            ZDOID id = zdo.GetZDOID(s_fgnTargetKey);
            if (id.IsNone() || ZNetScene.instance == null) return null;
            GameObject go = ZNetScene.instance.FindInstance(id);
            return go != null ? go.GetComponent<Character>() : null;
        }

        // Once per source for the session: proof of which path answered (owner, FGN).
        private static void NoteTargetSource(string source)
        {
            if (!s_targetSourcesLogged.Add(source)) return;
            FiresCore.Logging.FiresLogger.LogInfo($"[ThreatLevel] enemy targets: first read via {source}");
        }

        // ---- Ship: foes that hit the ship a body is aboard ----

        /// <summary>A foe that hit a ship counts as after its crew this long after its last hit.</summary>
        public const float ShipHitMemory = 20f;
        /// <summary>How far from the body such a foe still counts (a serpent circles the hull; the hull is 8-20 m long).</summary>
        public const float ShipReach = 30f;

        private static readonly Dictionary<Ship, Dictionary<Character, float>> s_shipHits = new Dictionary<Ship, Dictionary<Character, float>>();

        /// <summary>The ship <paramref name="c"/> is aboard: the one it steers, stands on, or (the local player) is inside.</summary>
        public static Ship ShipOf(Character c)
        {
            if (c == null) return null;
            if (c is Player player)
            {
                Ship steered = player.GetControlledShip();
                if (steered != null) return steered;
                if (player == Player.m_localPlayer)
                {
                    Ship inside = Ship.GetLocalShip();
                    if (inside != null && inside.IsPlayerInBoat(player)) return inside;
                }
            }
            return c.GetStandingOnShip();
        }

        /// <summary><paramref name="foe"/> hit <paramref name="ship"/> within <see cref="ShipHitMemory"/> s (seen on the ship's owner).</summary>
        public static bool IsAfterShip(Character foe, Ship ship)
        {
            if (foe == null || ship == null || !s_shipHits.TryGetValue(ship, out var hits)) return false;
            return hits.TryGetValue(foe, out float at) && Time.time - at < ShipHitMemory && !foe.IsDead();
        }

        /// <summary>A hit arrived on a ship's hull on this peer (the ship's owner, from WearNTear.RPC_Damage).</summary>
        internal static void NoteShipHit(Ship ship, HitData hit)
        {
            Character attacker = hit?.GetAttacker();
            if (ship == null || attacker == null || attacker is Player || attacker.IsTamed()) return;
            if (!s_shipHits.TryGetValue(ship, out var hits))
            {
                if (s_shipHits.Count > 8) s_shipHits.Clear();
                s_shipHits[ship] = hits = new Dictionary<Character, float>();
            }
            // One line per foe per ship until it has left off for ShipHitMemory (evidence for "threat: X after our ship").
            if (!hits.TryGetValue(attacker, out float last) || Time.time - last >= ShipHitMemory)
            {
                var wnt = ship.GetComponent<WearNTear>();
                string hull = wnt != null ? $"{wnt.GetHealthPercentage() * 100f:0} %" : "?";
                FiresCore.Logging.FiresLogger.LogInfo($"[ThreatLevel] {attacker.m_name} hit our ship {Utils.GetPrefabName(ship.gameObject)} " +
                                                      $"for {hit.GetTotalDamage():0} (hull {hull}): a threat to its crew for {ShipHitMemory:0} s");
            }
            hits[attacker] = Time.time;
        }

        [HarmonyPatch(typeof(WearNTear), "RPC_Damage")]
        private static class ShipHitPatch
        {
            private static void Prefix(WearNTear __instance, HitData hit)
            {
                try
                {
                    if (__instance == null || hit == null) return;
                    Ship ship = __instance.GetComponent<Ship>();
                    if (ship != null) NoteShipHit(ship, hit);
                }
                catch { }
            }
        }

        /// <summary>The synced alert flag (valid on every peer; BaseAI.IsAlerted is only kept up to date on the owner).</summary>
        public static bool IsAlertedSynced(Character c)
        {
            BaseAI ai = c != null ? c.GetBaseAI() : null;
            if (ai == null) return false;
            ZDO zdo = c.m_nview != null ? c.m_nview.GetZDO() : null;
            return zdo != null ? zdo.GetBool(ZDOVars.s_alert) : ai.IsAlerted();
        }

        // ---- Burst: the biggest single hit each kind of foe has landed (R76: one 85 frost hit killed the bot at 51 hp while the
        // average said "10.9 s to die") ----

        private static readonly Dictionary<string, float> s_maxHit = new Dictionary<string, float>();

        /// <summary>A hit arrived on this peer (Core's RPC_Damage prefix): remember the biggest raw hit per attacker prefab.</summary>
        internal static void NoteHitTaken(Character victim, HitData hit)
        {
            Character attacker = hit?.GetAttacker();
            if (attacker == null || victim == null) return;
            string key = Utils.GetPrefabName(attacker.gameObject) + ":" + attacker.GetLevel();
            float raw = hit.GetTotalDamage();
            if (!s_maxHit.TryGetValue(key, out float max) || raw > max) s_maxHit[key] = raw;
        }

        /// <summary>The biggest single hit (after the body's armour) this foe can be expected to land: learned, or its weapon's.</summary>
        public static float Burst(Character foe, float armor)
        {
            if (foe == null) return 0f;
            float raw = 0f;
            s_maxHit.TryGetValue(Utils.GetPrefabName(foe.gameObject) + ":" + foe.GetLevel(), out raw);
            ItemDrop.ItemData weapon = (foe as Humanoid)?.GetCurrentWeapon();
            if (weapon?.m_shared != null)
                raw = Mathf.Max(raw, weapon.GetDamage().GetTotalDamage() * (1f + Mathf.Max(0, foe.GetLevel() - 1) * 0.5f));
            return raw > 0f ? HitData.DamageTypes.ApplyArmor(raw, armor) : 0f;
        }

        public static string Name(Level level)
        {
            switch (level)
            {
                case Level.Space: return "space";
                case Level.Pull: return "pull";
                case Level.Chokepoint: return "chokepoint";
                case Level.RunPast: return "run past";
                case Level.Retreat: return "retreat";
                default: return "fight";
            }
        }

        /// <summary>
        /// The threat round <paramref name="self"/> now. <paramref name="health"/> may be a virtual health (god-mode runs);
        /// <paramref name="previous"/> and <paramref name="since"/> (when it was chosen) hold a harder level for a moment.
        /// </summary>
        public static Reading Assess(Humanoid self, float health, float maxHealth, float stamina, Level previous, float since)
        {
            var r = new Reading { Health = health, MaxHealth = maxHealth, Stamina = stamina, SecondsToDie = 999f };
            if (self == null) return r;
            Vector3 at = self.transform.position;
            float armor = self.GetBodyArmor();
            float nearest = float.MaxValue;
            Vector3 weighted = Vector3.zero;
            float weight = 0f;
            Ship myShip = ShipOf(self);
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == null || other == self || other.IsDead()) continue;
                Vector3 gap = other.transform.position - at;
                float distance = gap.magnitude;
                // An archer / caster counts out to its own range (R75: a Skeleton archer at 17 m, "0 hostile"); a flier (a drake
                // spitting frost from above, R76) out to FlyerReach even when its weapon can't be read on this peer; a foe that hit
                // our ship out to ShipReach.
                float shotReach = Mathf.Max(CombatAdvisor.RangedReach(other), other.m_flying ? FlyerReach : 0f);
                bool shipFoe = myShip != null && IsAfterShip(other, myShip);
                if (distance > Radius && (distance > CombatAdvisor.MaxShotRange || distance > shotReach + 2f) && !(shipFoe && distance <= ShipReach)) continue;
                if (!BaseAI.IsEnemy(self, other)) continue;
                if (!IsAfter(other, self, out bool onMe)) continue;

                r.Foes++;
                if (shipFoe && (r.ShipFoe == null || distance < Vector3.Distance(at, r.ShipFoe.transform.position))) r.ShipFoe = other;
                ItemDrop.ItemData weapon = (other as Humanoid)?.GetCurrentWeapon();
                float reach = weapon?.m_shared?.m_attack != null ? Mathf.Max(1f, weapon.m_shared.m_attack.m_attackRange) : 2f;
                reach = Mathf.Max(reach, shotReach);
                float dps = Dps(other, weapon, armor);
                if (distance <= reach + 1f)
                {
                    r.InReach++;
                    if (onMe) r.OnMe++;
                    r.Dps += dps;
                    float burst = Burst(other, armor);
                    if (burst > r.Burst) { r.Burst = burst; r.BurstFrom = other; }
                }
                else if (onMe) r.Dps += dps * 0.5f;   // closing in: half counts
                gap.y = 0f;
                if (distance < nearest) { nearest = distance; r.Nearest = other; }
                weighted += gap * (dps + 0.1f);
                weight += dps + 0.1f;
            }
            if (weight > 0f)
            {
                Vector3 centre = weighted / weight;
                r.Away = centre.sqrMagnitude > 0.0001f ? -centre.normalized : Vector3.zero;
            }
            r.FlankShift = Flank(self, at, r.Nearest);
            if (r.Dps > 0.01f) r.SecondsToDie = health / r.Dps;

            Level want;
            bool oneHitKills = r.Burst >= health * BurstShare;
            if (r.Foes > 0 && (oneHitKills || r.SecondsToDie < RetreatSeconds || (stamina < RetreatStamina && r.InReach >= 2)))
            {
                want = Level.Retreat;
                r.Why = oneHitKills ? $"one hit can kill ({r.BurstFrom?.m_name} {r.Burst:0} after armour vs hp {health:0})"
                    : r.SecondsToDie < RetreatSeconds ? $"{r.SecondsToDie:0.0} s to die" : $"stamina {stamina * 100f:0} % with {r.InReach} in reach";
            }
            else if (r.Foes >= 2 || r.SecondsToDie < SpaceSeconds)
            {
                want = Level.Space;
                r.Why = r.Foes >= 2 ? $"{r.Foes} foes" : $"{r.SecondsToDie:0.0} s to die";
            }
            else
            {
                want = Level.Fight;
                r.Why = r.Foes == 0 ? "no foe after us" : "one foe";
            }
            // Out of retreat only once it's safe again; any easier level only after the harder one held a moment.
            // A retreat that hasn't shaken them off in MaxRetreatSeconds turns to fight again (at "space") unless death is close:
            // running forever with foes on its back lands no hits either.
            bool retreatSpent = previous == Level.Retreat && Time.time - since > MaxRetreatSeconds && r.SecondsToDie >= RetreatSeconds / 2f;
            if (retreatSpent) s_retreatSpent[self] = Time.time;
            bool retreatResting = s_retreatSpent.TryGetValue(self, out float spentAt) && Time.time - spentAt < MaxRetreatSeconds;
            if (want == Level.Retreat && retreatResting && r.SecondsToDie >= RetreatSeconds / 2f)
            {
                want = Level.Space;
                r.Why = $"retreat spent ({MaxRetreatSeconds:0} s without shaking them off), turning to fight";
            }
            else if (previous == Level.Retreat && want != Level.Retreat && r.Foes > 0 && !retreatSpent
                && (r.SecondsToDie < RetreatExitSeconds || stamina < RetreatExitStamina))
            {
                want = Level.Retreat;
                r.Why = "still recovering";
            }
            else if (want < previous && Time.time - since < EaseAfter) want = previous;
            if (r.ShipFoe != null) r.Why += $"; threat: {r.ShipFoe.m_name} after our ship";
            r.Level = want;
            return r;
        }

        // One foe's damage per second on the body: its weapon's damage (level bonus as vanilla's Attack), after armour, per swing.
        private static float Dps(Character foe, ItemDrop.ItemData weapon, float armor)
        {
            if (weapon?.m_shared == null) return 5f;
            float damage = weapon.GetDamage().GetTotalDamage() * (1f + Mathf.Max(0, foe.GetLevel() - 1) * 0.5f);
            if (damage <= 0f) return 0f;
            damage = HitData.DamageTypes.ApplyArmor(damage, armor);
            return damage / (Mathf.Max(0.5f, weapon.m_shared.m_aiAttackInterval) + SwingSeconds);
        }

        // Foes within 6 m more than FlankAngle off the nearest's bearing: step away from them (sideways-back) so all are in front.
        private static Vector3 Flank(Humanoid self, Vector3 at, Character nearest)
        {
            if (nearest == null) return Vector3.zero;
            Vector3 front = nearest.transform.position - at;
            front.y = 0f;
            if (front.sqrMagnitude < 0.0001f) return Vector3.zero;
            front.Normalize();
            Vector3 shift = Vector3.zero;
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == null || other == nearest || other == self || other.IsDead()) continue;
                Vector3 gap = other.transform.position - at;
                gap.y = 0f;
                float distance = gap.magnitude;
                if (distance > 6f || distance < 0.01f || !BaseAI.IsEnemy(self, other)) continue;
                BaseAI ai = other.GetBaseAI();
                if (ai == null || !IsAfter(other, self, out bool flankOnMe) || !flankOnMe) continue;
                if (Vector3.Angle(front, gap) <= FlankAngle) continue;
                shift -= gap / distance;
            }
            if (shift.sqrMagnitude < 0.0001f) return Vector3.zero;
            // Never straight back into the nearest foe's face or through it: keep the part that isn't toward the nearest.
            shift -= front * Mathf.Max(0f, Vector3.Dot(shift, front));
            return shift.sqrMagnitude > 0.0001f ? shift.normalized : Vector3.zero;
        }
    }

    /// <summary>
    /// The combat-tactics counters per body (COMBAT_TACTICS.md §6's combat_group evidence), counted on the peer that drives the
    /// body. Names as the drill parses them: <see cref="Report"/> gives "threat fight,space; idle 0; filled 0; retreats 0; …".
    /// <see cref="Reset"/> at a drill mark / clear; also cleared at Game.Start.
    /// </summary>
    public static class TacticStats
    {
        public sealed class Counters
        {
            public readonly List<string> Threat = new List<string>();
            public readonly List<string> Tactics = new List<string>();
            public int Idle, Filled, Parries, Staggers, StaggerHits, Blocks, Retreats, Chokepoints, RunPast, Cornered, CornerEscapes;
        }

        private static readonly Dictionary<ZDOID, Counters> s_counters = new Dictionary<ZDOID, Counters>();
        private static readonly Counters s_none = new Counters();

        /// <summary>The counters of <paramref name="who"/> on this peer (an empty set when none; don't write to it).</summary>
        public static Counters Of(Character who)
        {
            if (who == null) return s_none;
            return s_counters.TryGetValue(who.GetZDOID(), out Counters c) ? c : s_none;
        }

        /// <summary>"threat fight,space; idle 0; filled 0; parries 0; staggers 0; staggerhits 0; blocks 0; retreats 0; …".</summary>
        public static string Report(Character who)
        {
            Counters c = Of(who);
            string threat = c.Threat.Count > 0 ? string.Join(",", c.Threat) : "none";
            string tactics = c.Tactics.Count > 0 ? string.Join(",", c.Tactics) : "none";
            return $"threat {threat}; tactics {tactics}; idle {c.Idle}; filled {c.Filled}; parries {c.Parries}; staggers {c.Staggers}; staggerhits {c.StaggerHits}; " +
                   $"blocks {c.Blocks}; retreats {c.Retreats}; chokepoints {c.Chokepoints}; runpast {c.RunPast}; cornered {c.Cornered}; cornerescapes {c.CornerEscapes}";
        }

        /// <summary>Forget every body's counters (a drill mark / clear).</summary>
        public static void Reset() => s_counters.Clear();

        /// <summary>Forget one body's counters.</summary>
        public static void Reset(Character who)
        {
            if (who != null) s_counters.Remove(who.GetZDOID());
        }

        internal static Counters For(Character who)
        {
            if (who == null) return null;
            ZDOID id = who.GetZDOID();
            if (id.IsNone()) return null;
            if (!s_counters.TryGetValue(id, out Counters c)) s_counters[id] = c = new Counters();
            return c;
        }

        /// <summary>A group tactic used (run past, pull, chokepoint): the distinct ones go on the report's "tactics" field.</summary>
        internal static void UsedTactic(Character who, string tactic)
        {
            Counters c = For(who);
            if (c == null || string.IsNullOrEmpty(tactic) || c.Tactics.Contains(tactic)) return;
            c.Tactics.Add(tactic);
        }

        internal static void SawLevel(Character who, ThreatLevel.Level level)
        {
            Counters c = For(who);
            if (c == null) return;
            string name = ThreatLevel.Name(level);
            if (!c.Threat.Contains(name)) c.Threat.Add(name);
            if (level == ThreatLevel.Level.Retreat) c.Retreats++;
        }

        [HarmonyPatch(typeof(Game), "Start")]
        private static class Game_Start_Reset
        {
            private static void Postfix() => s_counters.Clear();
        }
    }
}
