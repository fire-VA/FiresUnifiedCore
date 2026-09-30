using System.Collections.Generic;
using FiresCore.Npc.Archetypes;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>What <see cref="CombatAdvisor.Advise"/> tells a driver to do this frame.</summary>
    public sealed class CombatOrder
    {
        public Vector3 MoveDir;
        public bool Run;
        public bool Attack;
        public bool Secondary;
        public bool Block;
        public bool Dodge;
        public bool Retreat;
        public Vector3 LookAt;

        /// <summary>
        /// The class-skill bar slot to press this frame, or -1. Core can't see the skill bar: from Core 0.2.158 Advise fills it
        /// from the Ready slots the driver passes in (CompanionBrain.PickClassSkill); until then it stays -1.
        /// </summary>
        public int SkillSlot = -1;

        /// <summary>
        /// Bows / crossbows: keep the attack held (drawing) while true; the release is DrawHold going false after it was true.
        /// <see cref="Attack"/> is the press that starts the draw.
        /// </summary>
        public bool DrawHold;

        /// <summary>What blocks the ranged lane (null when clear or not checked this frame), and whether it is the shooter's side.</summary>
        public string LaneBlocker;
        public bool LaneFriendly;

        /// <summary>Approach, Engage, Draw, LaneBlocked, Hold, Block, Dodge, Retreat or Recover.</summary>
        public string State = "Hold";

        /// <summary>
        /// A timed block (parry, COMBAT_TACTICS §1): the Time.time to RAISE the block, held <see cref="CombatAdvisor.ParryHold"/> s
        /// unless <see cref="Block"/> says hold; -1 = none. Vanilla parries a hit landing within 0.25 s of the raise. Filled from 0.2.199.
        /// </summary>
        public float ParryAt = -1f;

        /// <summary>Sprint along <see cref="MoveDir"/> (a retreat, a run past), stamina permitting.</summary>
        public bool Sprint;

        /// <summary>
        /// Crouch (sneak) while closing on unaware prey (its AI not alerted, no target; Fire, R74: "crouch down to remain hidden so
        /// it can sneak up on the deer"); stand to attack, block, dodge or retreat. Never with <see cref="Sprint"/>.
        /// </summary>
        public bool Sneak;

        /// <summary>A food to eat now (stamina or health low and vanilla lets it eat: <see cref="CombatAdvisor.FoodToEat"/>), or null.</summary>
        public ItemDrop.ItemData Eat;

        /// <summary>The threat level's tactic this frame (<see cref="ThreatLevel.Name"/>): fight, space, pull, chokepoint, run past, retreat.</summary>
        public string Tactic = "fight";
    }

    /// <summary>
    /// The companions' fighting rules for a body something else drives (Fire, 2026-09-29: the FDT autoplay bot defends itself
    /// "with the companion logic"). CompanionAI is a BaseAI and its stamina/attack helpers read companion components, so none of
    /// it runs on a Player; this advisor applies the same rules to any Humanoid and returns orders the caller turns into input.
    /// Targets: whoever is after the body or its party, then anything hostile within <see cref="ClosePressureRange"/>; it never
    /// goes hunting. Stamina: StaminaManager's thresholds (attack from 50 %, recover under 40 % until 85 %, run 50/30). Spacing:
    /// melee at 0.8 x the weapon's reach, bows and crossbows at 12-20 m. Retreat under 25 % health, or out of stamina with a
    /// threat close.
    /// </summary>
    public static class CombatAdvisor
    {
        // The companion defaults (CompanionAI.OwnerDefenseRadius, combatLeashDistance, interceptPriority).
        private const float ClosePressureRange = 8f;
        private const float CombatLeash = 10f;
        private const float InterceptPriority = 3f;
        private const float MinStaminaToAttack = 0.50f;
        private const float RecoveryThreshold = 0.40f;
        private const float ResumeThreshold = 0.85f;
        private const float RunStart = 0.50f;
        private const float RunStop = 0.30f;
        private const float DodgeStamina = 0.35f;
        private const float RetreatHealth = 0.25f;
        private const float MeleeSpacing = 0.8f;
        private const float RangedMin = 12f;
        private const float RangedMax = 20f;
        private const float FacingDot = 0.8f;
        private const float AttackPause = 0.35f;
        // WeaponSwapManager's defaults (swapCooldown, meleePreferenceDistance, rangedPreferenceDistance, swapConfidenceThreshold).
        private const float SwapCooldown = 5f;
        private const float SwapMeleeDistance = 5f;
        private const float SwapRangedDistance = 12f;
        private const float SwapConfidence = 0.7f;
        // Between two skill presses: the bar's own cooldowns gate the rest.
        private const float SkillPause = 0.6f;
        // The ranged lane: checked this often; sidestep this long each way; blocked this long -> melee if carried.
        private const float LaneCheckSeconds = 0.25f, LaneSideSeconds = 2f, LaneMeleeAfter = 4f, LaneMeleeRange = 6f, FullDraw = 0.95f, PreferMeleeSeconds = 10f;
        // Cover: only from a shooter farther than this; when closing on it takes longer than this; searched within this.
        private const float CoverCloseRange = 8f, CoverAfterSeconds = 3f, CoverRange = 8f;
        // Bosses: bow spacing, and how close a melee body must be to spend a dodge roll on a big attack.
        private const float BossRangedMin = 15f, BossRangedMax = 25f, BossRollRange = 8f;

        private sealed class Memory
        {
            // On the melee weapon because the staff ran out of eitr with a foe close (0.2.201); back to the staff at EitrBackShare.
            public bool EitrMelee;
            public bool Recovering;
            public bool Running;
            public float NextAttack;
            public float NextSwap;
            public bool OutOfReach;
            public float NextSkill;
            public float FightStart = Time.time;
            public Character Target;
            public string LastState;
            public string LastSkillWhy;
            public float NextSkillWhy;
            // The ranged lane (LineOfFire): since when it has been blocked (-1 = clear), by what, next check.
            public float FireBlockedSince = -1f;
            public string LastBlocker;
            public float NextLaneCheck;
            public float PreferMeleeUntil;
            // Cover from a shooter (dungeons): the spot, whether we're using it, next re-check, the last logged spot.
            public Vector3 CoverSpot;
            public bool HasCover;
            public float CoverCheckAt;
            public string CoverLogged;
            // Boss fights: the attack being dodged, which side to step, whether this attack's roll is spent.
            public string BossAttack;
            public float BossSide = 1f;
            public bool BossRolled;
            // The threat level (ThreatLevel): the last reading, when its level was chosen, the next re-read.
            public ThreatLevel.Reading Threat;
            public float ThreatSince;
            public float NextThreat;
            // The never-idle rule (§4): the last fill logged.
            public string LastFill;
            public float NextFillLog;
            // Retreat found no way back (a wall within ±90° of away): fighting back from the corner.
            public bool Cornered;
            // This cornering already ended in a run past (counted once); a chokepoint already counted this group fight.
            public bool CornerEscaped;
            public bool ChokeCounted;
            // The group tactic this frame (null = the threat level's name), and the last one logged.
            public string TacticNow;
            // The last "foe behind a wall" line (kind) logged.
            public string NoLosLogged;
            // The next "staff: out of eitr" line.
            public float NextEitrLog;
            // A new target: log the threat verdict at the next read.
            public bool VerdictPending;
            public string LastTactic;
            // The last parry raise ordered (logged once per swing).
            public float LastParryAt = -1f;
            // The weapon picked for a target's resistances, and that target (kept while both hold).
            public string ClassLaneLogged;
            public ItemDrop.ItemData ResistPick;
            public Character ResistTarget;
            // Is the fight going anywhere: since when, the target's health then, our swings since.
            public float ProgressAt;
            public float ProgressHealth;
            public int ProgressSwings;
        }

        private static readonly Dictionary<Humanoid, Memory> s_memory = new Dictionary<Humanoid, Memory>();

        /// <summary>
        /// The target to fight, or null (no threat: hand back). Same rules as a Follow-mode companion's, among what the body
        /// PERCEIVES (sight cone off its look direction, line of sight, hearing, a few seconds of memory; AI.Perception) and can
        /// fight from where it stands (never while swimming; melee not at a target in water or far above/below; AI.CompanionBrain.CanFight).
        /// </summary>
        public static Character PickTarget(Humanoid self, float range)
        {
            if (self == null || self.IsDead()) return null;
            // The companions' own rules (CompanionBrain), with the body as its own owner.
            Character best = null;
            float bestScore = float.MaxValue;
            Vector3 at = self.transform.position;
            float reach = Reach(self);
            bool hasRanged = HasRanged(self);
            AI.Senses senses = AI.Senses.Player;
            senses.ViewRange = Mathf.Min(senses.ViewRange, range);
            Vector3 eye = Eye(self);
            Vector3 look = self.GetLookDir();
            // Already fighting: like an alerted monster, the view cone no longer limits sight (line of sight still does).
            bool alerted = s_memory.ContainsKey(self);
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == null || other == self || other.IsDead() || other.m_aiSkipTarget) continue;
                if (Vector3.Distance(at, other.transform.position) > range) continue;
                if (!ClassTargeting.IsEnemyTarget(self, other)) continue;
                if (!AI.CompanionBrain.IsThreat(self, other, at, self, ClosePressureRange, CombatLeash, false)) continue;
                if (!AI.CompanionBrain.IsFairGame(self, other)) continue;
                if (IsFutile(other)) continue;
                if (!AI.CompanionBrain.CanFight(self, other, reach, hasRanged)) continue;
                if (!AI.Perception.Knows(self, other, eye, look, senses, alerted)) continue;
                // Heard or remembered is not enough to engage: never through a wall (dungeons). A clear line, or a way to walk there.
                // Defending is not hunting ([seasons], R70: the bot chased a fleeing Deer with its Club): a passive animal, or a
                // monster that hasn't noticed anyone, is left alone unless it is after us or our party.
                if (Passive(self, other)) continue;
                if (!Engageable(self, other, eye)) continue;
                float score = AI.CompanionBrain.ScoreTarget(self, other, self, at, reach, InterceptPriority, null, null);
                if (score >= bestScore) continue;
                bestScore = score;
                best = other;
            }
            return best;
        }

        // Engageable = a clear line from the eye (LineOfFire, walls and friends block) or a navmesh path to it; cached per pair
        // for EngageCacheSeconds (the path query costs), a "skip" line once per foe per EngageLogSeconds.
        private const float EngageCacheSeconds = 2f, EngageLogSeconds = 10f;
        private static readonly Dictionary<(Humanoid, Character), (bool ok, float until)> s_engageable = new Dictionary<(Humanoid, Character), (bool, float)>();
        private static readonly Dictionary<(Humanoid, Character), float> s_engageLogged = new Dictionary<(Humanoid, Character), float>();

        private static bool Engageable(Humanoid self, Character other, Vector3 eye)
        {
            var key = (self, other);
            float now = Time.time;
            if (s_engageable.TryGetValue(key, out var cached) && now < cached.until) return cached.ok;
            if (s_engageable.Count > 256) s_engageable.Clear();
            bool ok = LineOfFire.Clear(self, eye, other, out string blocker)
                      || (Pathfinding.instance != null && Pathfinding.instance.HavePath(self.transform.position, other.transform.position, Pathfinding.AgentType.Humanoid));
            s_engageable[key] = (ok, now + EngageCacheSeconds);
            if (!ok && (!s_engageLogged.TryGetValue(key, out float next) || now >= next))
            {
                if (s_engageLogged.Count > 256) s_engageLogged.Clear();
                s_engageLogged[key] = now + EngageLogSeconds;
                Debug.Log($"[CombatAdvisor] {self.m_name}: skip {other.m_name}: no line ({blocker}) and no path");
            }
            return ok;
        }

        // Passive = an AnimalAI body (Deer, Hare …), or a MonsterAI that is not alerted and has no target; unless its target is us or
        // a party member. "skip <foe>: passive, not targeting us" once per foe per EngageLogSeconds.
        private static readonly Dictionary<(Humanoid, Character), float> s_passiveLogged = new Dictionary<(Humanoid, Character), float>();
        // FDT's drill tag (DrillTest: ZDO bool "FiresDrillMonster").
        private static readonly int DrillMonsterKey = "FiresDrillMonster".GetStableHashCode();

        private static bool Passive(Humanoid self, Character other)
        {
            BaseAI ai = other.GetBaseAI();
            if (ai == null) return false;
            // A drill's monster is always fair game: the drill aggroes it on another peer, and that aggro doesn't survive an
            // ownership handover ([visual]).
            ZNetView view = other.m_nview;
            if (view != null && view.IsValid() && view.GetZDO().GetBool(DrillMonsterKey)) return false;
            Character itsTarget = FiresCore.Npc.Combat.ThreatLevel.TargetOf(ai);
            if (itsTarget != null && (itsTarget == self || ClassTargeting.IsPartyMember(self, itsTarget))) return false;
            bool passive = ai is AnimalAI || (ai is MonsterAI monster && !monster.IsAlerted() && itsTarget == null);
            if (!passive) return false;
            var key = (self, other);
            float now = Time.time;
            if (!s_passiveLogged.TryGetValue(key, out float next) || now >= next)
            {
                if (s_passiveLogged.Count > 256) s_passiveLogged.Clear();
                s_passiveLogged[key] = now + EngageLogSeconds;
                Debug.Log($"[CombatAdvisor] {self.m_name}: skip {other.m_name}: passive, not targeting us");
            }
            return true;
        }

        // A foe that shoots: a humanoid holding a bow, crossbow, staff or anything that launches a projectile.
        private static bool ShootsProjectiles(Character foe)
        {
            ItemDrop.ItemData weapon = (foe as Humanoid)?.GetCurrentWeapon();
            if (weapon?.m_shared == null) return false;
            var skill = weapon.m_shared.m_skillType;
            return skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows || skill == Skills.SkillType.ElementalMagic
                   || skill == Skills.SkillType.BloodMagic || weapon.m_shared.m_attack?.m_attackProjectile != null;
        }

        // Cover from a shooter the body can't close on soon: the nearest spot within CoverRange whose line from the foe is broken
        // by a solid. Rings of 12 bearings every 2 m, nearest first; the ground from Surface. False when none.
        // "cover: behind <prefab> from <foe>" once per new spot's blocker; always true (so it can sit in the HasCover expression).
        private static bool LogCover(Humanoid self, Character foe, string behind, Memory memory)
        {
            string key = $"{behind}|{foe.GetZDOID()}";
            if (key != memory.CoverLogged)
            {
                memory.CoverLogged = key;
                Debug.Log($"[CombatAdvisor] {self.m_name}: cover: behind {behind} from {foe.m_name}");
            }
            return true;
        }

        private static bool FindCover(Humanoid self, Character foe, out Vector3 spot, out string behind)
        {
            spot = Vector3.zero;
            behind = null;
            Vector3 at = self.transform.position;
            Vector3 foeEye = foe.GetCenterPoint();
            for (float r = 2f; r <= CoverRange + 0.01f; r += 2f)
            {
                for (int i = 0; i < 12; i++)
                {
                    Vector3 p = at + Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward * r;
                    p.y = FiresCore.World.Surface.HeightOr(p, at.y);
                    Vector3 chest = p + Vector3.up * 1.2f;
                    if (!Physics.Linecast(foeEye, chest, out RaycastHit hit, ArcCheck.Mask, QueryTriggerInteraction.Ignore)) continue;
                    if (hit.distance > Vector3.Distance(foeEye, chest) - 0.4f) continue;   // the spot's own ground, not a wall
                    spot = p;
                    Piece piece = hit.collider.GetComponentInParent<Piece>();
                    behind = Utils.GetPrefabName(piece != null ? piece.gameObject : hit.collider.transform.root.gameObject);
                    return true;
                }
            }
            return false;
        }

        /// <summary>This frame's order against <paramref name="target"/>; call every frame while there is one. <paramref name="healthShare"/>
        /// overrides the body's health share (0..1) for the retreat rule, e.g. a god-mode bot's own tracked health; -1 = the real one.</summary>
        public static CombatOrder Advise(Humanoid self, Character target, float dt, float healthShare = -1f) =>
            Advise(self, target, dt, healthShare, null);

        /// <summary>
        /// <see cref="Advise(Humanoid, Character, float, float)"/> plus the class skills Ready on the body's bar: the order's
        /// <see cref="CombatOrder.SkillSlot"/> is the one to press this frame (<see cref="AI.CompanionBrain.PickClassSkill"/>), or -1.
        /// No skill while swimming, out of reach or holding; at most one press every <see cref="SkillPause"/> s.
        /// </summary>
        public static CombatOrder Advise(Humanoid self, Character target, float dt, float healthShare,
            IReadOnlyList<AI.CompanionBrain.SkillOption> ready)
        {
            CombatOrder order = AdviseMoves(self, target, dt, healthShare);
            if (ready == null || self == null) return order;
            // No memory = AdviseMoves never saw a live target for this body: said, not silent (0.2.215, R86 tank drill: a Ready bar of
            // tank skills every tick and not one "[CombatAdvisor] Human: …skill…" line for the tester).
            if (!s_memory.TryGetValue(self, out Memory memory))
            {
                s_memory[self] = memory = new Memory();
                SkillWhy(self, memory, target == null ? "no target passed" : $"no fight memory yet ({target.m_name})");
                return order;
            }
            if (target == null || target.IsDead()) { SkillWhy(self, memory, target == null ? "no target passed" : "the target is dead"); return order; }
            if (ready.Count == 0) { SkillWhy(self, memory, "ready empty (nothing Ready on the bar)"); return order; }
            if (order.State == "Swim" || order.State == "OutOfReach" || order.State == "Hold")
            {
                SkillWhy(self, memory, order.State);
                return order;
            }
            if (Time.time < memory.NextSkill) return order;

            float distance = target != null ? Vector3.Distance(self.transform.position, target.transform.position) : 0f;
            float fight = Time.time - memory.FightStart;
            order.SkillSlot = AI.CompanionBrain.PickClassSkill(self, target, distance, fight, ready, out string why);
            if (order.SkillSlot >= 0)
            {
                memory.NextSkill = Time.time + SkillPause;
                memory.LastSkillWhy = null;
                Debug.Log($"[CombatAdvisor] {self.m_name}: skill slot {order.SkillSlot} {why} ({order.State}, {distance:0.0} m, fight {fight:0} s)");
            }
            else SkillWhy(self, memory, why, $" ({distance:0.0} m, fight {fight:0} s)");
            return order;
        }

        // Why no class skill was chosen: always on, printed when the reason changes or every SkillWhyRepeat s (R62 class_drill).
        private const float SkillWhyRepeat = 5f;

        private static void SkillWhy(Humanoid self, Memory memory, string why, string context = null)
        {
            if (why == memory.LastSkillWhy && Time.time < memory.NextSkillWhy) return;
            memory.LastSkillWhy = why;
            memory.NextSkillWhy = Time.time + SkillWhyRepeat;
            Debug.Log($"[CombatAdvisor] {self.m_name}: no skill{context}: {why}");
        }

        private static CombatOrder AdviseMoves(Humanoid self, Character target, float dt, float healthShare)
        {
            var order = new CombatOrder();
            if (self == null || target == null || target.IsDead()) return Finish(self, order, "Hold");
            if (!s_memory.TryGetValue(self, out Memory memory)) s_memory[self] = memory = new Memory();
            // A new target is a new fight: the opener / control windows count from here (R62 class_drill: the memory outlived
            // earlier fights, so only Sustained skills were ever tried).
            if (memory.Target != target)
            {
                memory.Target = target;
                memory.FightStart = Time.time;
                memory.ProgressAt = Time.time;
                memory.ProgressHealth = target.GetHealth();
                memory.ProgressSwings = 0;
                // A new fight: read the threat now and log the verdict with its numbers (R76 survival (b): "fight the Greyling at
                // 30 %" and no Core line said why).
                memory.NextThreat = 0f;
                memory.VerdictPending = true;
            }

            // A fight going nowhere (R74: 80+ hits on a wild companion, its hp 344/346 throughout: ~1 per hit after its armour,
            // regenerated): after FutileSeconds of swinging with its health down less than FutileShare, break off and leave it
            // alone for a while. Retreat if it keeps coming; PickTarget skips it meanwhile.
            if (Time.time - memory.ProgressAt >= FutileSeconds)
            {
                float lost = memory.ProgressHealth - target.GetHealth();
                // Only futile when nothing carried does better (ChooseWeapon swaps to that first).
                bool betterCarried = BestAgainst(self, target, self.GetCurrentWeapon(), out string betterWhy) != null;
                if (memory.ProgressSwings >= FutileSwings && lost < target.GetMaxHealth() * FutileShare && betterCarried)
                    Debug.Log($"[CombatAdvisor] {self.m_name}: not futile yet: {betterWhy}");
                else if (memory.ProgressSwings >= FutileSwings && lost < target.GetMaxHealth() * FutileShare)
                {
                    s_futile[target] = Time.time + FutileIgnoreSeconds;
                    Debug.Log($"[CombatAdvisor] {self.m_name}: futile: {target.m_name} hp {target.GetHealth():0}/{target.GetMaxHealth():0} after {FutileSeconds:0} s and {memory.ProgressSwings} swing(s) (down {lost:0}: armour or regen out-pace our hits); breaking off for {FutileIgnoreSeconds:0} s");
                }
                memory.ProgressAt = Time.time;
                memory.ProgressHealth = target.GetHealth();
                memory.ProgressSwings = 0;
            }
            if (IsFutile(target))
            {
                Vector3 off = self.transform.position - target.transform.position;
                off.y = 0f;
                order.Retreat = true;
                order.MoveDir = off.sqrMagnitude > 0.0001f ? off.normalized : -self.transform.forward;
                order.Run = true;
                order.Sprint = true;
                order.Block = TargetSwingingAt(target, self);
                return Finish(self, order, "Futile");
            }

            Vector3 to = target.transform.position - self.transform.position;
            to.y = 0f;
            float distance = to.magnitude;
            Vector3 toward = distance > 0.01f ? to / distance : self.transform.forward;
            order.LookAt = target.GetCenterPoint();
            // Sneak up on unaware prey (Finish stands it up for any attack, block, dodge or retreat).
            order.Sneak = IsUnaware(target);

            float stamina = StaminaShare(self);
            if (memory.Recovering && stamina >= ResumeThreshold) memory.Recovering = false;
            else if (!memory.Recovering && stamina < RecoveryThreshold) memory.Recovering = true;
            if (memory.Running && stamina < RunStop) memory.Running = false;
            else if (!memory.Running && stamina >= RunStart) memory.Running = true;

            float health = healthShare >= 0f ? healthShare : self.GetHealthPercentage();
            ReadThreat(self, memory, health, stamina);

            ItemDrop.ItemData weapon = self.GetCurrentWeapon();
            // Anything whose attack launches a projectile keeps ranged spacing: bows, crossbows AND staffs ([visual], R71 mage
            // class_drill: a StaffFireball got melee spacing and walked into reach).
            bool ranged = IsProjectileWeapon(weapon);
            float reach = weapon != null && weapon.m_shared.m_attack != null ? Mathf.Max(1f, weapon.m_shared.m_attack.m_attackRange) : 2f;

            // No attack works in water: get out, away from the target (R56: the bot swam after targets and landed 0 hits).
            if (self.IsSwimming())
            {
                order.MoveDir = -toward;
                order.Run = memory.Running;
                return Finish(self, order, "Swim");
            }
            // Out of reach from here (melee vs a target in water or far above/below): don't chase it, hand back.
            // Hysteresis: once out of reach, the target must come 1 m nearer in height to count again (R57 flicker).
            float heightLimit = memory.OutOfReach ? AI.CompanionBrain.MeleeHeightReach - 0.5f : AI.CompanionBrain.MeleeHeightReach + 0.5f;
            bool wasOut = memory.OutOfReach;
            memory.OutOfReach = !AI.CompanionBrain.CanFight(self, target, reach, ranged, heightLimit);
            if (memory.OutOfReach)
            {
                // Once per episode (0.2.203, Fire R77: "punching a cliff because the wolf is up top"): why no swing.
                if (!wasOut)
                {
                    float dy = target.transform.position.y - self.transform.position.y;
                    Debug.Log($"[CombatAdvisor] {self.m_name}: no swing: {target.m_name} is {Mathf.Abs(dy):0.0} m {(dy > 0f ? "above" : "below")} " +
                              $"at {distance:0.0} m (melee reaches {heightLimit:0.0} m up/down); repositioning, not swinging");
                }
                return Finish(self, order, "OutOfReach");
            }

            bool threatClose = distance <= reach + 2f;
            bool threatRetreat = memory.Threat.Level == ThreatLevel.Level.Retreat;
            if (health < RetreatHealth || (memory.Recovering && stamina < 0.1f && threatClose) || threatRetreat)
            {
                // Away from all of them (their damage-weighted centre), not just the target (§2 retreat); a wall that way turns it
                // up to 90° either side. No way back at all = cornered: fight back from the corner (below) instead of pressing into
                // the wall with the hits landing unblocked (R73 Crypt4: 4 Skeletons + 2 Ghosts, "not even trying to fight back").
                Vector3 away = memory.Threat.Away.sqrMagnitude > 0.0001f ? memory.Threat.Away : -toward;
                if (FreeWay(self, away, out Vector3 way))
                {
                    memory.Cornered = false;
                    order.Retreat = true;
                    order.MoveDir = way;
                    order.Run = memory.Running;
                    order.Sprint = threatRetreat && memory.Running;
                    order.Block = threatClose && (TargetSwingingAt(target, self) || AnySwingingAt(self));
                    return Finish(self, order, "Retreat");
                }
                if (!memory.Cornered)
                {
                    memory.Cornered = true;
                    memory.CornerEscaped = false;
                    TacticStats.Counters c = TacticStats.For(self);
                    if (c != null) c.Cornered++;
                    Vector3 p = self.transform.position;
                    Debug.Log($"[CombatAdvisor] {self.m_name}: cornered at ({p.x:0}, {p.z:0}): no way back within ±90° with {memory.Threat.OnMe} on us");
                }
                // §3 run past: the widest free gap between them, sprinting, blocking what swings as it goes. No gap: fight back
                // from the corner (below).
                FoesOn(self, 8f, s_foes, out _);
                if (EscapeRules.RunPastGap(self, s_foes, out Vector3 gap, out string gapWhy))
                {
                    TacticStats.Counters c = TacticStats.For(self);
                    if (c != null)
                    {
                        c.RunPast++;
                        if (!memory.CornerEscaped) c.CornerEscapes++;
                    }
                    if (!memory.CornerEscaped)
                        Debug.Log($"[CombatAdvisor] {self.m_name}: run past: {gapWhy}");
                    memory.CornerEscaped = true;
                    memory.TacticNow = "run past";
                    order.Retreat = true;
                    order.MoveDir = gap;
                    order.Run = true;
                    order.Sprint = memory.Running;
                    order.Block = AnySwingingAt(self) && stamina >= FillBlockStamina;
                    return Finish(self, order, "RunPast");
                }
            }
            else memory.Cornered = false;

            // §2/§3 against a group (three or more on us), melee: indoors, hold a narrow spot behind so only one or two reach;
            // outside, back off along the way in until they string out, then fight the one that caught up.
            if (!ranged && memory.Threat.Level == ThreatLevel.Level.Space && memory.Threat.Foes >= GroupFoes)
            {
                if (EscapeRules.Chokepoint(self, out Vector3 choke, out float width))
                {
                    Vector3 toChoke = choke - self.transform.position;
                    toChoke.y = 0f;
                    if (toChoke.magnitude > 1f)
                    {
                        if (!memory.ChokeCounted)
                        {
                            memory.ChokeCounted = true;
                            TacticStats.Counters c = TacticStats.For(self);
                            if (c != null) c.Chokepoints++;
                            Debug.Log($"[CombatAdvisor] {self.m_name}: chokepoint: {memory.Threat.Foes} foes; holding a {width:0.0} m wide spot {toChoke.magnitude:0.0} m back at ({choke.x:0}, {choke.z:0})");
                        }
                        memory.TacticNow = "chokepoint";
                        order.MoveDir = toChoke.normalized;
                        order.Block = AnySwingingAt(self) && stamina >= FillBlockStamina;
                        return Finish(self, order, "Chokepoint");
                    }
                    memory.TacticNow = "chokepoint";
                }
                else
                {
                    FoesOn(self, 15f, s_foes, out float spread);
                    if (spread < PullSpread && EscapeRules.PullPoint(self, memory.Threat.Nearest, out Vector3 pull))
                    {
                        Vector3 toPull = pull - self.transform.position;
                        toPull.y = 0f;
                        if (memory.LastTactic != "pull")
                            Debug.Log($"[CombatAdvisor] {self.m_name}: pull: {s_foes.Count} foes bunched ({spread:0.0} m between the first two); backing off {toPull.magnitude:0} m along the way in");
                        memory.TacticNow = "pull";
                        order.MoveDir = toPull.normalized;
                        order.Block = AnySwingingAt(self) && stamina >= FillBlockStamina;
                        return Finish(self, order, "Pull");
                    }
                }
            }
            else memory.ChokeCounted = false;
            // Pinned: block any swing coming in from any of them, not only the target's (the fill-in covers a frame with no order).
            if (memory.Cornered && !ranged && AnySwingingAt(self) && stamina >= FillBlockStamina)
            {
                order.Block = true;
                return Finish(self, order, "Cornered");
            }

            // A boss winding up a big attack: get out of its way first (BossTactics: sidestep / leave the cone / leave the radius),
            // with a dodge roll for a melee body in the thick of it; one "dodge: <attack> → <response>" line per attack.
            if (target.IsBoss())
            {
                string attack = BossTactics.CurrentAttack(target);
                if (attack == null) memory.BossAttack = null;
                else if (BossTactics.For(attack, out BossTactics.Rule rule))
                {
                    if (attack != memory.BossAttack)
                    {
                        memory.BossAttack = attack;
                        memory.BossSide = Random.value < 0.5f ? 1f : -1f;
                        Debug.Log($"[CombatAdvisor] {self.m_name}: dodge: {attack} → {rule}");
                    }
                    Vector3 escape = BossTactics.Escape(rule, target, self.transform.position, memory.BossSide);
                    if (escape.sqrMagnitude > 0.01f)
                    {
                        order.MoveDir = escape.normalized;
                        order.Run = true;
                        if (!ranged && distance <= BossRollRange && stamina >= DodgeStamina && !memory.BossRolled)
                        {
                            order.Dodge = true;
                            memory.BossRolled = true;
                        }
                        return Finish(self, order, "BossDodge");
                    }
                }
                if (attack == null) memory.BossRolled = false;
            }

            // Cover (dungeons): a melee body under fire from a shooter it can't close on within CoverAfterSeconds steps behind the
            // nearest solid that breaks the shooter's line, and waits there for it to come closer.
            if (!ranged && distance > CoverCloseRange && ShootsProjectiles(target))
            {
                bool exposed = !Physics.Linecast(target.GetCenterPoint(), self.GetCenterPoint(), ArcCheck.Mask, QueryTriggerInteraction.Ignore);
                float closeIn = distance / Mathf.Clamp(self.m_runSpeed, 4f, 10f);
                if (Time.time >= memory.CoverCheckAt)
                {
                    memory.CoverCheckAt = Time.time + 1f;
                    memory.HasCover = exposed && closeIn > CoverAfterSeconds && FindCover(self, target, out memory.CoverSpot, out string behind)
                                      && LogCover(self, target, behind, memory);
                }
                if (memory.HasCover)
                {
                    Vector3 toCover = memory.CoverSpot - self.transform.position;
                    toCover.y = 0f;
                    order.MoveDir = toCover.magnitude > 0.8f ? toCover.normalized : Vector3.zero;
                    order.Run = memory.Running && toCover.magnitude > 2f;
                    return Finish(self, order, "Cover");
                }
            }
            else memory.HasCover = false;

            if (TargetSwingingAt(target, self))
            {
                if (stamina >= DodgeStamina && !ranged && HeavyIncoming(target))
                {
                    order.Dodge = true;
                    order.MoveDir = Vector3.Cross(Vector3.up, toward);
                    return Finish(self, order, "Dodge");
                }
                if (!ranged)
                {
                    // Parry (§1): with a blocker that can parry, the stamina for it and this swing's landing learned, raise the
                    // block just before the hit instead of holding it (a held block never parries). Else hold it as before.
                    if (CanParry(self, stamina) && ParryTiming.Predict(target, out float hitAt, out _, out _)
                        && hitAt > Time.time - ParryHold && hitAt - Time.time < ParryPlanAhead)
                    {
                        order.ParryAt = hitAt - ParryTiming.Lead;
                        order.Block = false;
                        if (distance > reach) order.MoveDir = toward;
                        if (!Mathf.Approximately(memory.LastParryAt, order.ParryAt))
                        {
                            memory.LastParryAt = order.ParryAt;
                            ParryTiming.Planned(self, order.ParryAt);
                        }
                        return Finish(self, order, "Parry");
                    }
                    order.Block = true;
                    // Out of our reach while it swings (a Skeleton hits from ~2.9 m, past a Club): step in under the guard, or
                    // the bot blocks in place forever (R61: 50 s frozen at 2.9 m, Approach <-> Block, 0 hits).
                    if (distance > reach) order.MoveDir = toward;
                    return Finish(self, order, "Block");
                }
            }

            // Staggered (a parry, or a heavy hit): vanilla's damage window. Commit: attack it, no pause, heavy first with the
            // stamina for it, until the stagger ends (§1 "punish the stagger").
            // Facing it first, like every other swing (0.2.203, R77: swings in the wrong direction).
            if (!ranged && target.IsStaggering() && distance <= reach + 0.5f && !self.InAttack() && IsFacing(self, toward))
            {
                order.Attack = true;
                order.Secondary = stamina >= StaggerHeavyStamina && weapon != null && weapon.HaveSecondaryAttack();
                if (distance > reach * MeleeSpacing) order.MoveDir = toward;
                return Finish(self, order, "Punish");
            }

            // Bosses: bows keep 15-25 m (BOSS_DRILL §2); melee closes only between the big attacks (BossDodge above handles those).
            bool boss = target.IsBoss();
            float rangedMin = boss ? BossRangedMin : RangedMin, rangedMax = boss ? BossRangedMax : RangedMax;
            float want = ranged ? Mathf.Clamp(distance, rangedMin, rangedMax) : reach * MeleeSpacing;
            if (ranged && distance < rangedMin) order.MoveDir = -toward;
            else if (distance > (ranged ? rangedMax : reach))
            {
                // A foe behind a wall (R75 Crypt4: "Approach" straight at a Ghost 11.6 m off through a crypt wall): never walk
                // straight at it. A flier that passes through walls is waited out; anything else is reached by the navmesh path
                // if that's short enough, else left alone until it comes into sight.
                if (!HasLineOfSight(self, target))
                {
                    if (target.m_flying)
                    {
                        NoLosLog(self, memory, $"foe behind a wall: {target.m_name} passes through walls; waiting for it to come out ({distance:0.0} m)");
                        return Finish(self, order, "WaitForSight");
                    }
                    AI.PathWalker walker = WalkerFor(self);
                    var walk = walker.Tick(self, self.GetLookDir(), target.transform.position, reach * MeleeSpacing, memory.Running && !order.Sneak,
                        out Vector3 pathMove, out Vector3 pathLook);
                    float pathLength = PathLength(self.transform.position, walker.Corners, target.transform.position);
                    bool reachable = walk == AI.PathWalker.WalkState.Moving || walk == AI.PathWalker.WalkState.Turning || walk == AI.PathWalker.WalkState.Pending
                                     || walk == AI.PathWalker.WalkState.Door || walk == AI.PathWalker.WalkState.Arrived;
                    if (!reachable || pathLength > distance * BehindWallPathFactor + BehindWallPathSlack)
                    {
                        NoLosLog(self, memory, reachable
                            ? $"foe behind a wall: {target.m_name} is {pathLength:0} m away by path ({distance:0.0} m straight); not engaging until it comes into sight"
                            : $"foe behind a wall: no way to {target.m_name} ({walker.State}); not engaging until it comes into sight");
                        return Finish(self, order, "NoSight");
                    }
                    NoLosLog(self, memory, $"foe behind a wall: pathing round to {target.m_name} ({pathLength:0} m by path, {distance:0.0} m straight)");
                    order.MoveDir = pathMove;
                    if (pathLook.sqrMagnitude > 0.0001f) order.LookAt = self.GetCenterPoint() + pathLook * 5f;
                    order.Run = !order.Sneak && memory.Running;
                    return Finish(self, order, "Approach");
                }
                memory.NoLosLogged = null;
                order.MoveDir = toward;
                order.Run = !order.Sneak && memory.Running && distance > want + 4f;
                return Finish(self, order, "Approach");
            }

            // Space (§2): two or more on us: step so the flankers come round to the front, fight the nearer one.
            if (memory.Threat.Level == ThreatLevel.Level.Space && order.MoveDir.sqrMagnitude < 0.0001f
                && memory.Threat.FlankShift.sqrMagnitude > 0.0001f)
                order.MoveDir = memory.Threat.FlankShift;

            // Recovering in melee (R74 Crypt4: "Recover -> Block -> Recover" 0.9-2.2 m from a Skeleton, no swing, 35.8 through the
            // held block, near death): a held block regenerates at 80 % and every blocked hit drains stamina and restarts the
            // regen delay, so it never recovers. Instead: block only a swing coming in, else step back out of reach to refill;
            // with no way back, swing back when a swing is affordable. With a foe on us, resume at ResumeUnderPressure.
            bool pressedClose = !ranged && distance <= reach + 1.5f && memory.Threat.OnMe > 0;
            if (memory.Recovering && pressedClose && StaminaShare(self) >= ResumeUnderPressure) memory.Recovering = false;
            if (memory.Recovering && pressedClose)
            {
                bool incoming = TargetSwingingAt(target, self) || AnySwingingAt(self);
                if (incoming)
                {
                    order.Block = true;
                    return Finish(self, order, "Recover");
                }
                Vector3 back = memory.Threat.Away.sqrMagnitude > 0.0001f ? memory.Threat.Away : -toward;
                if (FreeWay(self, back, out Vector3 way))
                {
                    order.MoveDir = way;
                    if (self is Player eater2) order.Eat = FoodToEat(eater2, out _);
                    return Finish(self, order, "Recover");
                }
                if (IsFacing(self, toward) && Time.time >= memory.NextAttack && !self.InAttack() && CanAfford(self, weapon, false, out _))
                {
                    order.Attack = true;
                    memory.NextAttack = Time.time + AttackPause;
                    return Finish(self, order, "Recover");
                }
            }
            if (memory.Recovering)
            {
                order.Block = !ranged && distance <= reach + 1f;
                // No attack input while the bar refills (R74: the bow drawn at stamina 0); a bow keeps its distance, and a
                // player eats if a food slot is free.
                if (ranged && distance < rangedMin && order.MoveDir.sqrMagnitude < 0.0001f) order.MoveDir = -toward;
                if (self is Player eater) order.Eat = FoodToEat(eater, out _);
                return Finish(self, order, "Recover");
            }

            // A ranged shot only down a clear lane: never through a piece, terrain, a companion or a player (Fire: the bot shot an
            // enemy through its own workbench). Blocked: step sideways to find a lane (switching side every few seconds); after
            // a while ChooseWeapon hands over to melee when there is one.
            bool facing = Vector3.Dot(self.transform.forward, toward) >= FacingDot;
            if (ranged) return Shoot(self, target, weapon, order, memory, toward, facing, stamina);
            if (facing && stamina >= MinStaminaToAttack && Time.time >= memory.NextAttack && !self.InAttack())
            {
                order.Attack = true;
                memory.NextAttack = Time.time + AttackPause;
            }
            return Finish(self, order, "Engage");
        }

        // A bow or crossbow: draw (DrawHold) while the lane is clear, release at a full draw only if it still is; blocked, keep
        // the draw up, step sideways for a lane (the side flips every LaneSideSeconds) and never loose into a friend or a piece.
        // ChooseWeapon hands over to melee after LaneMeleeAfter s blocked or with the target inside LaneMeleeRange.
        private static CombatOrder Shoot(Humanoid self, Character target, ItemDrop.ItemData bow, CombatOrder order, Memory memory,
            Vector3 toward, bool facing, float stamina)
        {
            if (Time.time >= memory.NextLaneCheck || self.GetAttackDrawPercentage() >= FullDraw)
            {
                memory.NextLaneCheck = Time.time + LaneCheckSeconds;
                LaneBlock lane = LineOfFire.Shot(self, bow, target);
                if (!lane.Blocked) memory.FireBlockedSince = -1f;
                else if (memory.FireBlockedSince < 0f) memory.FireBlockedSince = Time.time;
                if (lane.Blocked && lane.Blocker != memory.LastBlocker)
                    Debug.Log($"[CombatAdvisor] {self.m_name}: lane to {target.m_name} blocked by {lane.Blocker}; holding the shot, moving for a lane");
                memory.LastBlocker = lane.Blocked ? lane.Blocker : null;
                order.LaneBlocker = lane.Blocker;
                order.LaneFriendly = lane.Friendly;
            }

            // Only a bow draws; a staff / wand is a single press (with eitr) once the lane is clear.
            bool bowDraw = bow?.m_shared?.m_attack != null && bow.m_shared.m_attack.m_bowDraw;
            bool drawing = bowDraw && self.GetAttackDrawPercentage() > 0f;
            if (memory.FireBlockedSince >= 0f)
            {
                float side = Mathf.FloorToInt((Time.time - memory.FireBlockedSince) / LaneSideSeconds) % 2 == 0 ? 1f : -1f;
                order.MoveDir = Vector3.Cross(Vector3.up, toward) * side;
                // Still blocked after a while (R76: a drake above the mausoleum, "lane blocked by FiresMausoleumPortal" x4, then the
                // fight was dropped): back off at a slant as well, to see over the building instead of sliding along it.
                if (Time.time - memory.FireBlockedSince > LaneBackOffAfter) order.MoveDir = (order.MoveDir - toward * 0.8f).normalized;
                order.DrawHold = drawing;   // keep a drawn arrow up while looking for a lane; never release it into the block
                return Finish(self, order, "LaneBlocked");
            }

            if (drawing)
            {
                float draw = self.GetAttackDrawPercentage();
                order.DrawHold = draw < FullDraw;
                // Out of stamina mid-draw: the draw won't finish; loose what there is rather than hold at zero.
                if (order.DrawHold && self is Player archer && archer.GetStamina() < 1f)
                {
                    order.DrawHold = false;
                    Debug.Log($"[CombatAdvisor] {self.m_name}: out of stamina at {draw * 100f:0}% draw; loosed");
                }
                if (!order.DrawHold)
                {
                    memory.NextAttack = Time.time + AttackPause;
                    Debug.Log($"[CombatAdvisor] {self.m_name}: clear line to {target.m_name}, loosed at {draw * 100f:0}% draw");
                }
                return Finish(self, order, "Draw");
            }
            // A staff out of eitr (Fire, R75 mage drill: point-blank staff swings with no eitr, "sitting there getting hit"): no
            // press; back off out of reach to regenerate, eat eitr food if carried.
            if (!bowDraw && !CanAfford(self, bow, false, out string shortOf) && shortOf != null && shortOf.StartsWith("eitr"))
            {
                Vector3 away = memory.Threat.Away.sqrMagnitude > 0.0001f ? memory.Threat.Away : -toward;
                if (FreeWay(self, away, out Vector3 way)) order.MoveDir = way;
                order.Run = memory.Running;
                string eitrWhy = "not a player";
                ItemDrop.ItemData food = self is Player caster ? EitrFood(caster, out eitrWhy) : null;
                order.Eat = food;
                if (Time.time >= memory.NextEitrLog)
                {
                    memory.NextEitrLog = Time.time + 5f;
                    Debug.Log($"[CombatAdvisor] {self.m_name}: staff: out of {shortOf}; backing off to regen (take: {eitrWhy})");
                }
                return Finish(self, order, "Recover");
            }
            if (facing && stamina >= MinStaminaToAttack && Time.time >= memory.NextAttack && !self.InAttack() && CanAfford(self, bow, false, out _)
                && (!IsEitrWeapon(bow) || StaffShotOk(self, target, bow, order, memory, toward, Vector3.Distance(self.transform.position, target.transform.position))))
            {
                order.DrawHold = bowDraw;
                order.Attack = true;
                if (!bowDraw) memory.NextAttack = Time.time + AttackPause;
            }
            return Finish(self, order, "Engage");
        }

        /// <summary>
        /// Whether <paramref name="self"/> can pay for this attack now (Fire, R74: "trying to spam pull its bow back when it has no
        /// stamina"): stamina for the swing (vanilla's m_attackStamina, less the skill discount taken as none: conservative) plus, for
        /// a bow, a full draw (drain per second × the minimum draw time), and eitr likewise, with a 5 % margin of the bar. Every
        /// driver that presses attack itself (a hunt, a drill) asks this first; Advise's own orders already hold back under 50 %.
        /// </summary>
        public static bool CanAfford(Humanoid self, ItemDrop.ItemData weapon, bool secondary, out string why)
        {
            why = null;
            if (self == null) { why = "no body"; return false; }
            Attack attack = weapon?.m_shared != null ? (secondary ? weapon.m_shared.m_secondaryAttack : weapon.m_shared.m_attack) : null;
            if (attack == null) return true;
            float stamina, max;
            if (self is Player player) { stamina = player.GetStamina(); max = Mathf.Max(1f, player.GetMaxStamina()); }
            else { max = 100f; stamina = StaminaShare(self) * max; }
            float need = attack.m_attackStamina + (attack.m_bowDraw ? attack.m_drawStaminaDrain * Mathf.Max(0.5f, attack.m_drawDurationMin) : 0f) + max * 0.05f;
            if (stamina < need)
            {
                why = $"stamina {stamina:0} < {need:0} for {(attack.m_bowDraw ? "a full draw" : "the swing")}";
                return false;
            }
            float eitrNeed = attack.m_attackEitr + (attack.m_bowDraw ? attack.m_drawEitrDrain * Mathf.Max(0.5f, attack.m_drawDurationMin) : 0f);
            if (eitrNeed > 0f && self is Player p && p.GetEitr() < eitrNeed)
            {
                why = $"eitr {p.GetEitr():0} < {eitrNeed:0}";
                return false;
            }
            return true;
        }

        /// <summary>
        /// The food to eat now, or null: with stamina under <see cref="EatStaminaShare"/> the carried food with the most stamina,
        /// with health under <see cref="EatHealthShare"/> the one with the most health, among what vanilla lets the player eat now
        /// (a free food slot, or one that can be eaten again: Player.CanEat).
        /// </summary>
        public static ItemDrop.ItemData FoodToEat(Player player, out string why)
        {
            why = null;
            if (player == null || player.GetInventory() == null) return null;
            float staminaShare = player.GetStamina() / Mathf.Max(1f, player.GetMaxStamina());
            float healthShare = player.GetHealthPercentage();
            bool forStamina = staminaShare < EatStaminaShare, forHealth = healthShare < EatHealthShare;
            if (!forStamina && !forHealth) return null;
            ItemDrop.ItemData best = null;
            float bestValue = 0f;
            foreach (var item in player.GetInventory().GetAllItems())
            {
                if (item?.m_shared == null || item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) continue;
                float value = forStamina ? item.m_shared.m_foodStamina : item.m_shared.m_food;
                if (value <= bestValue || !player.CanEat(item, false)) continue;
                best = item;
                bestValue = value;
            }
            if (best != null) why = forStamina ? $"stamina {staminaShare * 100f:0} %: {best.m_shared.m_name} (+{bestValue:0} stamina)" : $"health {healthShare * 100f:0} %: {best.m_shared.m_name} (+{bestValue:0} health)";
            else why = "nothing edible carried (or no free food slot)";
            return best;
        }

        public const float EatStaminaShare = 0.2f, EatHealthShare = 0.4f;

        /// <summary>A staff out of eitr hands over to melee with the target this close; the staff comes back at this eitr share.</summary>
        public const float EitrMeleeDistance = 5f, EitrBackShare = 0.5f;

        /// <summary>
        /// What to take for eitr now, or null (0.2.201): first an eitr mead whose effect gives eitr at once (vanilla SE_Stats
        /// m_eitrUpFront: MeadEitrMinor), then one that speeds eitr regen, then the carried food with the most eitr. A mead only when
        /// its effect (and its category: meads share a cooldown) isn't already running, as vanilla's CanConsumeItem, without messages.
        /// </summary>
        public static ItemDrop.ItemData EitrFood(Player player) => EitrFood(player, out _);

        public static ItemDrop.ItemData EitrFood(Player player, out string why)
        {
            why = "nothing with eitr carried";
            if (player?.GetInventory() == null) return null;
            ItemDrop.ItemData upFront = null, regen = null, food = null;
            float bestUpFront = 0f, bestRegen = 1f;
            SEMan seman = player.GetSEMan();
            foreach (var item in player.GetInventory().GetAllItems())
            {
                if (item?.m_shared == null || item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) continue;
                if (item.m_shared.m_consumeStatusEffect is SE_Stats mead && item.m_shared.m_food <= 0f)
                {
                    if (seman != null && (seman.HaveStatusEffect(mead.NameHash())
                        || (!string.IsNullOrEmpty(mead.m_category) && seman.HaveStatusEffectCategory(mead.m_category)))) continue;
                    if (mead.m_eitrUpFront > bestUpFront) { upFront = item; bestUpFront = mead.m_eitrUpFront; }
                    else if (mead.m_eitrRegenMultiplier > bestRegen) { regen = item; bestRegen = mead.m_eitrRegenMultiplier; }
                    continue;
                }
                if (item.m_shared.m_foodEitr <= 0f || (food != null && item.m_shared.m_foodEitr <= food.m_shared.m_foodEitr)) continue;
                if (!player.CanEat(item, false)) continue;
                food = item;
            }
            if (upFront != null) { why = $"{upFront.m_shared.m_name} (+{bestUpFront:0} eitr at once)"; return upFront; }
            if (regen != null) { why = $"{regen.m_shared.m_name} (eitr regen x{bestRegen:0.##})"; return regen; }
            if (food != null) { why = $"{food.m_shared.m_name} (+{food.m_shared.m_foodEitr:0} eitr food)"; return food; }
            return null;
        }

        /// <summary>
        /// How much of <paramref name="weapon"/>'s primary damage gets through <paramref name="target"/>'s own resistances (vanilla's
        /// damage modifiers: immune/ignore 0, very resistant 0.25, resistant 0.5, slightly resistant 0.75, slightly weak 1.25, weak
        /// 1.5, very weak 2). <paramref name="note"/>: the modifiers that mattered, "blunt immune; fire x1.5".
        /// </summary>
        public static float Effectiveness(ItemDrop.ItemData weapon, Character target, out string note)
        {
            note = "";
            if (weapon?.m_shared == null || target == null) return 0f;
            HitData.DamageTypes d = weapon.GetDamage();
            HitData.DamageModifiers mods = target.GetDamageModifiers();
            var parts = new System.Text.StringBuilder();
            float total = 0f;
            total += Through(d.m_blunt, mods.m_blunt, "blunt", parts);
            total += Through(d.m_slash, mods.m_slash, "slash", parts);
            total += Through(d.m_pierce, mods.m_pierce, "pierce", parts);
            total += Through(d.m_fire, mods.m_fire, "fire", parts);
            total += Through(d.m_frost, mods.m_frost, "frost", parts);
            total += Through(d.m_lightning, mods.m_lightning, "lightning", parts);
            total += Through(d.m_poison, mods.m_poison, "poison", parts);
            total += Through(d.m_spirit, mods.m_spirit, "spirit", parts);
            note = parts.ToString();
            return total;
        }

        private static float Through(float damage, HitData.DamageModifier mod, string name, System.Text.StringBuilder parts)
        {
            if (damage <= 0f) return 0f;
            float factor;
            switch (mod)
            {
                case HitData.DamageModifier.Immune:
                case HitData.DamageModifier.Ignore: factor = 0f; break;
                case HitData.DamageModifier.VeryResistant: factor = 0.25f; break;
                case HitData.DamageModifier.Resistant: factor = 0.5f; break;
                case HitData.DamageModifier.SlightlyResistant: factor = 0.75f; break;
                case HitData.DamageModifier.SlightlyWeak: factor = 1.25f; break;
                case HitData.DamageModifier.Weak: factor = 1.5f; break;
                case HitData.DamageModifier.VeryWeak: factor = 2f; break;
                default: factor = 1f; break;
            }
            if (!Mathf.Approximately(factor, 1f))
            {
                if (parts.Length > 0) parts.Append("; ");
                parts.Append(factor == 0f ? $"{name} immune" : $"{name} x{factor:0.##}");
            }
            return damage * factor;
        }

        // A lane still blocked after this long also backs off (s).
        private const float LaneBackOffAfter = 6f;

        // A ranged class bar keeps the bow when the lane is blocked unless a foe is this close (m).
        private const float ClassMeleeForceRange = 2f;

        // Swap for resistances only when the held weapon gets through less than this share of the best carried one.
        private const float ResistSwapShare = 0.5f;

        // R74 Crypt4: a Club did 4 raw blunt to a Ghost (blunt-immune), 15 hits, hp 60/60. The carried weapon that gets the most
        // through the target's resistances (usable: not broken unless in hand; a bow with its ammo), when the held one gets through
        // under ResistSwapShare of it. Null: keep the held weapon.
        private static ItemDrop.ItemData BestAgainst(Humanoid self, Character target, ItemDrop.ItemData held, out string why)
        {
            why = null;
            Inventory inventory = self.GetInventory();
            if (inventory == null || target == null) return null;
            float heldEff = held != null ? Effectiveness(held, target, out _) : 0f;
            // Within the class bar's weapon kind (a Ranger stays on bows, a melee bar off them).
            var classWant = AI.CompanionBrain.ClassWeaponFor(self);
            ItemDrop.ItemData best = null;
            float bestEff = 0f;
            string bestNote = null;
            foreach (var item in inventory.GetAllItems())
            {
                if (item?.m_shared == null || !item.IsWeapon() || !item.HavePrimaryAttack()) continue;
                if (item.m_shared.m_useDurability && item.m_durability <= 0f && !item.m_equipped) continue;
                if (!string.IsNullOrEmpty(item.m_shared.m_ammoType) && !CarriesAmmoFor(self, item)) continue;
                if (AI.CompanionBrain.IsGatheringTool(item) && !item.m_equipped) continue;
                bool itemRanged = IsProjectileWeapon(item);
                if ((classWant == AI.CompanionBrain.ClassWeaponWant.Ranged && !itemRanged) || (classWant == AI.CompanionBrain.ClassWeaponWant.Melee && itemRanged)) continue;
                float eff = Effectiveness(item, target, out string note);
                if (eff > bestEff) { bestEff = eff; best = item; bestNote = note; }
            }
            if (best == null || best == held || heldEff >= bestEff * ResistSwapShare) return null;
            Effectiveness(held, target, out string heldNote);
            why = $"{best.m_shared.m_name} for {target.m_name} ({(string.IsNullOrEmpty(bestNote) ? "full damage" : bestNote)}; " +
                  $"{(held != null ? held.m_shared.m_name : "bare hands")} gets {heldEff:0} of {bestEff:0} through{(string.IsNullOrEmpty(heldNote) ? "" : ": " + heldNote)})";
            return best;
        }

        /// <summary>A weapon whose primary attack launches a projectile: bows, crossbows, staffs, wands.</summary>
        public static bool IsProjectileWeapon(ItemDrop.ItemData weapon)
        {
            if (weapon?.m_shared == null) return false;
            var skill = weapon.m_shared.m_skillType;
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows) return true;
            Attack attack = weapon.m_shared.m_attack;
            return attack != null && (attack.m_attackProjectile != null || attack.m_attackType == Attack.AttackType.Projectile);
        }

        /// <summary>
        /// The weapon to hold against <paramref name="target"/>, or null to keep what is in hand. The companions' rules: melee and
        /// ranged picked as a companion scans its slots (<see cref="AI.CompanionBrain.ChooseWeapons"/>, equipped items first), then
        /// <see cref="AI.CompanionBrain.WeighWeapons"/> when it has both; at most one swap every 5 s, never mid-attack. With nothing
        /// in hand it returns the melee weapon (or the ranged one).
        /// </summary>
        public static ItemDrop.ItemData ChooseWeapon(Humanoid self, Character target)
        {
            if (self == null || self.IsDead() || self.InAttack()) return null;
            Inventory inventory = self.GetInventory();
            if (inventory == null) return null;

            s_ordered.Clear();
            foreach (var item in inventory.GetAllItems()) if (item.m_equipped) s_ordered.Add(item);
            foreach (var item in inventory.GetAllItems()) if (!item.m_equipped) s_ordered.Add(item);
            AI.CompanionBrain.ChooseWeapons(s_ordered, out ItemDrop.ItemData melee, out ItemDrop.ItemData ranged);
            s_ordered.Clear();

            ItemDrop.ItemData held = self.GetCurrentWeapon();

            // The target's resistances first (R74 Crypt4: a Club on a blunt-immune Ghost): the carried weapon that gets the most
            // through, kept while it's in hand against this target (the melee/ranged rules below would hand back the default).
            if (target != null)
            {
                if (!s_memory.TryGetValue(self, out Memory resist)) s_memory[self] = resist = new Memory();
                if (resist.ResistPick != null && resist.ResistPick == held && resist.ResistTarget == target) return null;
                if (Time.time >= resist.NextSwap)
                {
                    ItemDrop.ItemData better = BestAgainst(self, target, held, out string resistWhy);
                    if (better != null)
                    {
                        resist.NextSwap = Time.time + SwapCooldown;
                        resist.ResistPick = better;
                        resist.ResistTarget = target;
                        Debug.Log($"[CombatAdvisor] {self.m_name}: weapon: {resistWhy}");
                        return better;
                    }
                }
            }

            // A staff with a foe close (0.2.203, Fire R77: fireballs at 4.7 m with a Club in the bag; R75: point-blank staff swings with
            // no eitr, "sitting there getting hit"): within EitrMeleeDistance the melee weapon, whatever the eitr, so the eitr stays for
            // shots that hit and for skills. The staff comes back past EitrMeleeDistance + 2 m with eitr over the reserve and a shot.
            if (target != null && melee != null && held != null && held == ranged && IsEitrWeapon(held))
            {
                if (!s_memory.TryGetValue(self, out Memory eitrMemory)) s_memory[self] = eitrMemory = new Memory();
                float gap = Vector3.Distance(self.transform.position, target.transform.position);
                if (Time.time >= eitrMemory.NextSwap && gap <= EitrMeleeDistance)
                {
                    eitrMemory.EitrMelee = true;
                    eitrMemory.NextSwap = Time.time + SwapCooldown;
                    bool afford = CanAfford(self, held, false, out string shortOf);
                    ItemDrop.ItemData food = self is Player drinker ? EitrFood(drinker, out string foodWhy) : null;
                    Debug.Log($"[CombatAdvisor] {self.m_name}: wield {melee.m_shared.m_name} because {target.m_name} is at {gap:0.0} m (staff range starts at {EitrMeleeDistance:0} m)" +
                              $"{(afford ? "" : $" and {held.m_shared.m_name} is out of eitr ({shortOf})")}; staff again past {EitrMeleeDistance + 2f:0} m with eitr over " +
                              $"{StaffReserve * 100f:0}% + a shot (take: {(food != null ? food.m_shared.m_name : "nothing carried")})");
                    return melee;
                }
            }
            if (target != null && held != null && held == melee && ranged != null && s_memory.TryGetValue(self, out Memory backMemory) && backMemory.EitrMelee)
            {
                float eitrNow = AI.CompanionBrain.EitrShare(self);
                float gap = Vector3.Distance(self.transform.position, target.transform.position);
                float shotShare = self is Player p && p.GetMaxEitr() > 0f && ranged.m_shared?.m_attack != null ? ranged.m_shared.m_attack.m_attackEitr / p.GetMaxEitr() : 0f;
                if (gap <= EitrMeleeDistance + 2f || eitrNow < StaffReserve + shotShare || Time.time < backMemory.NextSwap) return null;
                backMemory.EitrMelee = false;
                backMemory.NextSwap = Time.time + SwapCooldown;
                Debug.Log($"[CombatAdvisor] {self.m_name}: wield {ranged.m_shared.m_name} because {target.m_name} is at {gap:0.0} m and eitr is {eitrNow * 100f:0} %");
                return ranged;
            }

            bool holding = held != null && (held == melee || held == ranged);
            if (!holding) return melee ?? ranged;
            if (melee == null || ranged == null || target == null) return null;

            if (!s_memory.TryGetValue(self, out Memory memory)) s_memory[self] = memory = new Memory();
            if (Time.time < memory.NextSwap) return null;

            Vector3 at = self.transform.position;
            float distance = Vector3.Distance(at, target.transform.position);
            bool currentlyRanged = held == ranged;

            // No lane for the bow for a while, or the target close with the lane blocked: melee, and stay on it for a bit
            // (Fire: the bot shot through its own workbench; a blocked archer closes in instead).
            if (!currentlyRanged && Time.time < memory.PreferMeleeUntil && AI.CompanionBrain.ClassWeaponFor(self) != AI.CompanionBrain.ClassWeaponWant.Ranged) return null;

            // Melee can't reach it (far above or below): take the ranged weapon if its ammo is carried. The lane rule still holds
            // a blocked shot.
            // The class bar first (R74 ranger class_drill: these two early branches never asked it, so a Ranger was handed a club
            // at 5.9 m and four ranger skills went untested).
            var classWant = AI.CompanionBrain.ClassWeaponFor(self);
            if (!currentlyRanged && classWant != AI.CompanionBrain.ClassWeaponWant.Melee
                && CarriesAmmoFor(self, ranged) && !AI.CompanionBrain.CanFight(self, target, Reach(self), false))
            {
                memory.NextSwap = Time.time + SwapCooldown;
                float dy = target.transform.position.y - at.y;
                Debug.Log($"[CombatAdvisor] {self.m_name}: melee can't reach {target.m_name} ({Mathf.Abs(dy):0.0} m {(dy < 0f ? "below" : "above")}); wield {ranged.m_shared.m_name} ({distance:0.0} m)");
                return ranged;
            }
            if (currentlyRanged && memory.FireBlockedSince >= 0f && classWant == AI.CompanionBrain.ClassWeaponWant.Ranged
                && distance > ClassMeleeForceRange)
            {
                // A ranged class bar keeps the bow and keeps moving for a lane (Shoot's LaneBlocked sidestep); only a foe on us
                // forces melee (below).
                if (memory.LastBlocker != memory.ClassLaneLogged)
                {
                    memory.ClassLaneLogged = memory.LastBlocker;
                    Debug.Log($"[CombatAdvisor] {self.m_name}: no lane for the bow ({memory.LastBlocker}) but the class bar is ranged: keeping the bow, moving for a lane ({distance:0.0} m)");
                }
                return null;
            }
            if (currentlyRanged && memory.FireBlockedSince >= 0f
                && (Time.time - memory.FireBlockedSince >= LaneMeleeAfter || distance <= LaneMeleeRange))
            {
                if (classWant == AI.CompanionBrain.ClassWeaponWant.Ranged)
                    Debug.Log($"[CombatAdvisor] {self.m_name}: no lane and the foe is on me ({distance:0.0} m): melee despite the ranged class");
                memory.NextSwap = Time.time + SwapCooldown;
                memory.PreferMeleeUntil = Time.time + PreferMeleeSeconds;
                memory.FireBlockedSince = -1f;
                Debug.Log($"[CombatAdvisor] {self.m_name}: no lane for the bow ({memory.LastBlocker}); wield {melee.m_shared.m_name} ({distance:0.0} m)");
                return melee;
            }
            var situation = new AI.CompanionBrain.WeaponSituation
            {
                DistToTarget = distance,
                CurrentlyRanged = currentlyRanged,
                VeryCloseEnemies = AI.CompanionBrain.CountHostiles(self, at, 0f, 4f),
                CloseEnemies = AI.CompanionBrain.CountHostiles(self, at, 0f, SwapMeleeDistance),
                FarEnemies = AI.CompanionBrain.CountHostiles(self, at, SwapRangedDistance, SwapRangedDistance * 2f),
                TargetApproaching = AI.CompanionBrain.IsApproaching(target, at),
                TargetFleeing = AI.CompanionBrain.IsFleeing(target, at),
                HeightDiff = Mathf.Abs(target.transform.position.y - at.y),
                Swimming = self.IsSwimming(),
                HasStamina = true,
                StaminaShare = StaminaShare(self),
                CriticalRecovery = memory.Recovering,
                HasHealth = true,
                HealthShare = self.GetHealthPercentage(),
                CombatEntry = memory.LastState == null,
                MeleePreferenceDistance = SwapMeleeDistance,
                RangedPreferenceDistance = SwapRangedDistance,
                ElevationThreshold = AI.CompanionBrain.MeleeHeightReach,
            };
            var weighed = AI.CompanionBrain.WeighWeapons(situation, out float rangedScore, out float meleeScore);
            // The gate (class bar, too close for the bow) decides without the confidence bar; a weighed pick still needs it.
            bool meleeReaches = AI.CompanionBrain.CanFight(self, target, currentlyRanged ? 2f : Reach(self), false);
            var pick = AI.CompanionBrain.GateWeapons(self, weighed, distance, meleeReaches, out string why);
            bool gated = why != "weighed";
            if (pick == WeaponSwapManager.WeaponRecommendation.Ranged && !CarriesAmmoFor(self, ranged)) return null;
            ItemDrop.ItemData want = null;
            if (pick == WeaponSwapManager.WeaponRecommendation.Ranged && !currentlyRanged && (gated || rangedScore >= SwapConfidence)) want = ranged;
            else if (pick == WeaponSwapManager.WeaponRecommendation.Melee && currentlyRanged && (gated || meleeScore >= SwapConfidence)) want = melee;
            if (want == null) return null;

            memory.NextSwap = Time.time + SwapCooldown;
            Debug.Log($"[CombatAdvisor] {self.m_name}: wield {want.m_shared.m_name}: ranged {rangedScore:0.00} / melee {meleeScore:0.00}, {distance:0.0} m, because {why}");
            return want;
        }


        // ---- The staff policy (0.2.203, Fire R77 mage drill: "the bot absolutely spams fire staff attacks and misses most of them
        // … it shouldnt be wasting eitr like that"): a reserve kept for skills, shots only when a hit is likely, a miss streak closes
        // in, and one tally line per fight. ----

        /// <summary>Eitr share kept for class skills and heals: a staff shot that would take eitr under it is not fired.</summary>
        public const float StaffReserve = 0.3f;
        /// <summary>A target moving across the line of fire this far (m) during the projectile's flight is not shot at (held a while).</summary>
        public const float StaffLeadMetres = 1.5f, StaffHoldSeconds = 2f;
        /// <summary>This many staff shots in a row without a hit: close in before the next.</summary>
        public const int StaffMissStreak = 3;
        private const float StaffCloseInMetres = 8f, StaffFightIdleSeconds = 20f;

        private sealed class StaffTally
        {
            public Character Foe;
            public int Shots, Hits, SinceHit;
            public float EitrStart = -1f, EitrLowest = 2f;
            public bool ReserveBroken;
            public float LastShotAt, HoldSince = -1f, NextHoldLog, CloseInUntil;
        }

        private static readonly Dictionary<Humanoid, StaffTally> s_staff = new Dictionary<Humanoid, StaffTally>();

        private static bool IsEitrWeapon(ItemDrop.ItemData weapon) =>
            weapon?.m_shared?.m_attack != null && weapon.m_shared.m_attack.m_attackEitr > 0f && !weapon.m_shared.m_attack.m_bowDraw;

        private static StaffTally TallyFor(Humanoid self, Character foe)
        {
            if (s_staff.TryGetValue(self, out StaffTally t) && t.Foe == foe && Time.time - t.LastShotAt < StaffFightIdleSeconds) return t;
            if (t != null) ReportStaff(self, t);
            t = new StaffTally { Foe = foe, LastShotAt = Time.time };
            s_staff[self] = t;
            return t;
        }

        // One line per fight: shots, hits, eitr start -> end (lowest), reserve kept.
        private static void ReportStaff(Humanoid self, StaffTally t)
        {
            if (t == null || t.Shots == 0) return;
            float now = AI.CompanionBrain.EitrShare(self);
            Debug.Log($"[CombatAdvisor] {self.m_name}: staff: {t.Shots} shots, {t.Hits} hits vs {(t.Foe != null ? t.Foe.m_name : "?")}, " +
                      $"eitr {t.EitrStart * 100f:0}% -> {now * 100f:0}% (lowest {Mathf.Min(t.EitrLowest, now) * 100f:0}%), " +
                      $"reserve {StaffReserve * 100f:0}% kept {(t.ReserveBroken ? "no" : "yes")}");
        }

        /// <summary>From Core's Character.Damage prefix (the shooter's peer): a hit a staff projectile of a tallied body landed.</summary>
        internal static void NoteStaffHit(Character victim, HitData hit)
        {
            if (hit == null || victim == null) return;
            if (!(hit.GetAttacker() is Humanoid shooter) || !s_staff.TryGetValue(shooter, out StaffTally t)) return;
            if (hit.m_skill != Skills.SkillType.ElementalMagic && hit.m_skill != Skills.SkillType.BloodMagic) return;
            if (Time.time - t.LastShotAt > 4f) return;   // a class skill long after the last shot is not the staff's hit
            t.Hits++;
            t.SinceHit = 0;
        }

        /// <summary>
        /// Whether to fire the staff now; false with <paramref name="order"/> set to hold, close in or recover. Called with the lane
        /// already clear and the weapon affordable.
        /// </summary>
        private static bool StaffShotOk(Humanoid self, Character target, ItemDrop.ItemData staff, CombatOrder order, Memory memory, Vector3 toward, float distance)
        {
            StaffTally t = TallyFor(self, target);
            float share = AI.CompanionBrain.EitrShare(self);
            if (t.EitrStart < 0f) t.EitrStart = share;
            t.EitrLowest = Mathf.Min(t.EitrLowest, share);
            Attack attack = staff.m_shared.m_attack;

            // 1. The reserve: a shot that would take eitr under it waits (regen, mead); the skills keep their share.
            if (self is Player caster && caster.GetMaxEitr() > 0f && caster.GetEitr() - attack.m_attackEitr < StaffReserve * caster.GetMaxEitr())
            {
                Vector3 away = memory.Threat.Away.sqrMagnitude > 0.0001f ? memory.Threat.Away : -toward;
                if (distance < StaffCloseInMetres && FreeWay(self, away, out Vector3 way)) order.MoveDir = way;
                order.Eat = EitrFood(caster, out string take);
                if (Time.time >= memory.NextEitrLog)
                {
                    memory.NextEitrLog = Time.time + 5f;
                    Debug.Log($"[CombatAdvisor] {self.m_name}: staff: holding: eitr {caster.GetEitr():0} - {attack.m_attackEitr:0} would drop under the {StaffReserve * 100f:0}% reserve; " +
                              $"regen (take: {take})");
                }
                return false;
            }

            // 2. A miss streak: close in before the next shot.
            if (t.SinceHit >= StaffMissStreak && Time.time - t.LastShotAt > 1f && distance > StaffCloseInMetres)
            {
                if (t.CloseInUntil <= 0f)
                {
                    t.CloseInUntil = Time.time + 4f;
                    Debug.Log($"[CombatAdvisor] {self.m_name}: staff: {t.SinceHit} misses in a row at {target.m_name}; closing in from {distance:0.0} m to {StaffCloseInMetres:0} m");
                }
                if (Time.time < t.CloseInUntil)
                {
                    order.MoveDir = toward;
                    order.Run = true;
                    return false;
                }
            }
            if (t.CloseInUntil > 0f && (distance <= StaffCloseInMetres || Time.time >= t.CloseInUntil)) { t.CloseInUntil = 0f; t.SinceHit = 0; }

            // 3. Out of the projectile's reach: close in.
            float reach = attack.m_attackRange > 1f ? attack.m_attackRange : MaxShotRange;
            if (distance > reach) { order.MoveDir = toward; return false; }

            // 4. Crossing too fast for the flight time: hold a moment (a shot at where it stands would miss).
            Vector3 v = target.GetVelocity();
            v.y = 0f;
            Vector3 across = v - toward * Vector3.Dot(v, toward);
            float speed = attack.m_projectileVel > 1f ? attack.m_projectileVel : 25f;
            float drift = across.magnitude * distance / speed;
            if (drift > StaffLeadMetres)
            {
                if (t.HoldSince < 0f) t.HoldSince = Time.time;
                if (Time.time - t.HoldSince < StaffHoldSeconds)
                {
                    if (Time.time >= t.NextHoldLog)
                    {
                        t.NextHoldLog = Time.time + 5f;
                        Debug.Log($"[CombatAdvisor] {self.m_name}: staff: holding the shot: {target.m_name} crossing at {across.magnitude:0.0} m/s ({drift:0.0} m off at {distance:0.0} m)");
                    }
                    return false;
                }
            }
            t.HoldSince = -1f;

            t.Shots++;
            t.SinceHit++;
            t.LastShotAt = Time.time;
            if (share < StaffReserve) t.ReserveBroken = true;
            return true;
        }
        /// <summary>Drops the body's remembered pacing (call when handing back to the non-combat driver).</summary>
        public static void Forget(Humanoid self)
        {
            if (self == null) return;
            s_memory.Remove(self);
            // The fight is over: its staff tally line.
            if (s_staff.TryGetValue(self, out StaffTally tally)) ReportStaff(self, tally);
            s_staff.Remove(self);
        }

        private static readonly List<ItemDrop.ItemData> s_ordered = new List<ItemDrop.ItemData>();

        private static Vector3 Eye(Humanoid self) => self.m_eye != null ? self.m_eye.position : self.GetCenterPoint();

        // A ranged weapon in hand, or one carried with its ammo (ChooseWeapon swaps to it), so a target melee can't reach still
        // counts as a fight (R70 read: a Greydwarf 6.4 m below; the Club couldn't, the bow in the bag never came out).
        private static bool HasRanged(Humanoid self)
        {
            ItemDrop.ItemData weapon = self.GetCurrentWeapon();
            if (weapon != null && (AI.CompanionBrain.IsRangedWeapon(weapon) || IsProjectileWeapon(weapon))) return true;
            Inventory inventory = self.GetInventory();
            if (inventory == null) return false;
            AI.CompanionBrain.ChooseWeapons(inventory.GetAllItems(), out _, out ItemDrop.ItemData ranged);
            return CarriesAmmoFor(self, ranged);
        }

        // True for a staff (no ammo), or a bow / crossbow with matching ammo in the bag.
        private static bool CarriesAmmoFor(Humanoid self, ItemDrop.ItemData weapon)
        {
            if (weapon?.m_shared == null) return false;
            string ammo = weapon.m_shared.m_ammoType;
            if (string.IsNullOrEmpty(ammo)) return true;
            Inventory inventory = self.GetInventory();
            if (inventory == null) return false;
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                var type = item.m_shared.m_itemType;
                if ((type == ItemDrop.ItemData.ItemType.Ammo || type == ItemDrop.ItemData.ItemType.AmmoNonEquipable)
                    && item.m_shared.m_ammoType == ammo) return true;
            }
            return false;
        }

        private static CombatOrder Finish(Humanoid self, CombatOrder order, string state)
        {
            order.State = state;
            if (self == null || !s_memory.TryGetValue(self, out Memory memory)) return order;
            if (memory.LastState != state)
            {
                memory.LastState = state;
                Debug.Log($"[CombatAdvisor] {self.m_name}: {state}");
            }
            float stamina = StaminaShare(self);
            // "Hold" returns before the fight's own read: read the threat here, so a foe on us with no target picked still counts.
            if (state == "Hold" && Time.time >= memory.NextThreat) ReadThreat(self, memory, self.GetHealthPercentage(), stamina);
            FillIfIdle(self, order, memory, stamina);
            if (order.Attack) memory.ProgressSwings++;
            // (blocks are counted where vanilla blocks a hit: ParryTiming's BlockAttack postfix)
            if (memory.TacticNow != null) TacticStats.UsedTactic(self, memory.TacticNow);
            order.Tactic = memory.TacticNow ?? ThreatLevel.Name(memory.Threat.Level);
            memory.LastTactic = memory.TacticNow;
            memory.TacticNow = null;
            // Stand up for anything but a quiet approach (a retreat, a block, a swing all stand).
            if (order.Retreat || order.Block || order.Attack || order.Dodge) order.Sneak = false;
            Watched(self, state);
            return order;
        }

        /// <summary>How long a parry's block is held after <see cref="CombatOrder.ParryAt"/> (s).</summary>
        public const float ParryHold = 0.3f;

        // Recovering with a foe on us in melee: resume fighting from this stamina share (not the calm 85 %).
        private const float ResumeUnderPressure = 0.5f;

        private static bool IsFacing(Humanoid self, Vector3 toward) => Vector3.Dot(self.transform.forward, toward) >= FacingDot;

        // A fight is futile when FutileSeconds of FutileSwings+ swings took less than FutileShare of the target's max health.
        private const float FutileSeconds = 15f, FutileShare = 0.03f, FutileIgnoreSeconds = 60f;
        private const int FutileSwings = 6;
        private static readonly Dictionary<Character, float> s_futile = new Dictionary<Character, float>();

        /// <summary>A target this body gave up on as futile (its hits barely scratched it), for the next minute.</summary>
        public static bool IsFutile(Character target)
        {
            if (target == null || !s_futile.TryGetValue(target, out float until)) return false;
            if (Time.time < until) return true;
            s_futile.Remove(target);
            return false;
        }

        // A parry is planned only for a hit due within this many seconds; a heavy attack on a staggered foe needs this stamina.
        private const float ParryPlanAhead = 1.5f, StaggerHeavyStamina = 0.6f, ParryStamina = 0.2f;

        // A blocker that can parry (timedBlockBonus > 1: the shield, or the weapon with none) and the stamina for the block + 10 %.
        private static bool CanParry(Humanoid self, float stamina)
        {
            ItemDrop.ItemData blocker = self.GetCurrentBlocker();
            return blocker?.m_shared != null && blocker.m_shared.m_timedBlockBonus > 1f && stamina >= ParryStamina;
        }

        // A block costs stamina; under this share the filler strafes instead.
        private const float FillBlockStamina = 0.1f;
        private const float FillLogRepeat = 3f;

        private static bool Acting(CombatOrder order) =>
            order.Attack || order.Block || order.Dodge || order.DrawHold || order.SkillSlot >= 0 || order.ParryAt >= 0f
            || order.MoveDir.sqrMagnitude > 0.0001f;

        // §4 never stop defending: a foe in reach on us and this frame's order does nothing -> block, or with no stamina for it,
        // strafe off the nearest foe. Logged as "filled" with the branch that left it empty (R72: "wasn't even trying to defend").
        private static void FillIfIdle(Humanoid self, CombatOrder order, Memory memory, float stamina)
        {
            if (Acting(order) || memory.Threat.OnMe == 0)
            {
                memory.LastFill = null;
                return;
            }
            string fill;
            if (stamina >= FillBlockStamina && !self.IsSwimming())
            {
                order.Block = true;
                fill = "block";
            }
            else
            {
                Character nearest = memory.Threat.Nearest;
                Vector3 gap = nearest != null ? nearest.transform.position - self.transform.position : self.transform.forward;
                gap.y = 0f;
                Vector3 side = Vector3.Cross(Vector3.up, gap.sqrMagnitude > 0.0001f ? gap.normalized : self.transform.forward);
                order.MoveDir = (side - gap.normalized * 0.5f).normalized;
                fill = "strafe";
            }
            string what = $"{order.State} -> {fill}";
            if (memory.LastFill == null)
            {
                TacticStats.Counters c = TacticStats.For(self);
                if (c != null) c.Filled++;
            }
            if (what != memory.LastFill || Time.time >= memory.NextFillLog)
            {
                memory.LastFill = what;
                memory.NextFillLog = Time.time + FillLogRepeat;
                Debug.Log($"[CombatAdvisor] {self.m_name}: filled: {order.State} gave no order with {memory.Threat.OnMe} foe(s) on us -> {fill} (stamina {stamina * 100f:0} %)");
            }
        }

        // The threat level every ThreatInterval s; a change is logged once and counted.
        private const float ThreatInterval = 0.5f;

        private static void ReadThreat(Humanoid self, Memory memory, float healthShare, float stamina)
        {
            if (Time.time < memory.NextThreat) return;
            memory.NextThreat = Time.time + ThreatInterval;
            float max = self.GetMaxHealth();
            ThreatLevel.Level before = memory.Threat.Level;
            bool first = memory.ThreatSince <= 0f;
            memory.Threat = ThreatLevel.Assess(self, healthShare * max, max, stamina, before, memory.ThreatSince);
            if (first || memory.Threat.Level != before)
            {
                memory.ThreatSince = Time.time;
                TacticStats.SawLevel(self, memory.Threat.Level);
                Debug.Log($"[CombatAdvisor] {self.m_name}: threat: {memory.Threat} -> {ThreatLevel.Name(memory.Threat.Level)} ({memory.Threat.Why})");
            }
            if (memory.VerdictPending)
            {
                memory.VerdictPending = false;
                Character foe = memory.Target;
                Debug.Log($"[CombatAdvisor] {self.m_name}: threat verdict vs {(foe != null ? foe.m_name : "?")}: {ThreatLevel.Name(memory.Threat.Level).ToUpperInvariant()} " +
                          $"because {memory.Threat.Why}; incoming {memory.Threat.Dps:0.0}/s after our armour {self.GetBodyArmor():0}, " +
                          $"{(memory.Threat.SecondsToDie >= 999f ? "no" : memory.Threat.SecondsToDie.ToString("0.0") + " s")} to die at hp {memory.Threat.Health:0}/{memory.Threat.MaxHealth:0} " +
                          $"(retreat under {ThreatLevel.RetreatSeconds:0} s, space under {ThreatLevel.SpaceSeconds:0} s or 2+ foes)");
            }
        }

        // ---- The idle watchdog (§4): a body Advise ran for, threatened, with nobody asking for orders ----

        private sealed class Watch
        {
            public float LastAdvise;
            public string LastState;
            public float IdleSince = -1f;
            public bool IdleLogged;
        }

        private static readonly Dictionary<Humanoid, Watch> s_watch = new Dictionary<Humanoid, Watch>();
        private static readonly List<Humanoid> s_watchKeys = new List<Humanoid>();
        private const float IdleAfter = 1f, WatchInterval = 0.5f;

        private static void Watched(Humanoid self, string state)
        {
            if (!s_watch.TryGetValue(self, out Watch w))
            {
                s_watch[self] = w = new Watch();
                WatchRunner.Ensure();
            }
            w.LastAdvise = Time.time;
            w.LastState = state;
        }

        private sealed class WatchRunner : MonoBehaviour
        {
            private static WatchRunner s_instance;
            private float _next;

            internal static void Ensure()
            {
                if (s_instance != null) return;
                var go = new GameObject("FiresCombatWatchdog");
                Object.DontDestroyOnLoad(go);
                s_instance = go.AddComponent<WatchRunner>();
            }

            private void Update()
            {
                if (Time.time < _next) return;
                _next = Time.time + WatchInterval;
                s_watchKeys.Clear();
                s_watchKeys.AddRange(s_watch.Keys);
                foreach (Humanoid body in s_watchKeys)
                {
                    if (body == null || body.IsDead()) { s_watch.Remove(body); continue; }
                    Watch w = s_watch[body];
                    // Orders asked for recently: Advise's own filler answers for it.
                    if (Time.time - w.LastAdvise < IdleAfter) { w.IdleSince = -1f; w.IdleLogged = false; continue; }
                    ThreatLevel.Reading r = ThreatLevel.Assess(body, body.GetHealth(), body.GetMaxHealth(), StaminaShare(body), ThreatLevel.Level.Fight, 0f);
                    if (r.OnMe == 0) { w.IdleSince = -1f; w.IdleLogged = false; continue; }
                    if (w.IdleSince < 0f) w.IdleSince = Time.time;
                    if (w.IdleLogged || Time.time - w.IdleSince < IdleAfter) continue;
                    w.IdleLogged = true;
                    TacticStats.Counters c = TacticStats.For(body);
                    if (c != null) c.Idle++;
                    Debug.Log($"[CombatAdvisor] {body.m_name}: idle while threatened: no orders asked for {Time.time - w.LastAdvise:0.0} s " +
                              $"(the driver stopped asking; last state {w.LastState}); {r.OnMe} foe(s) on us, nearest {(r.Nearest != null ? r.Nearest.m_name : "?")}");
                }
            }
        }

        private static float Reach(Humanoid self)
        {
            ItemDrop.ItemData weapon = self.GetCurrentWeapon();
            return weapon != null && weapon.m_shared.m_attack != null ? Mathf.Max(1f, weapon.m_shared.m_attack.m_attackRange) : 2f;
        }

        // A player's own stamina bar; a companion's tracker; anything else counts as rested.
        private static float StaminaShare(Humanoid self)
        {
            if (self is Player player)
            {
                float max = player.GetMaxStamina();
                return max > 0f ? player.GetStamina() / max : 1f;
            }
            StaminaManager manager = self.GetComponent<StaminaManager>();
            return manager != null ? manager.GetStaminaPercent() : 1f;
        }

        // The target is mid-swing, facing us, and close enough to connect.
        private static bool TargetSwingingAt(Character target, Humanoid self)
        {
            if (!target.InAttack()) return false;
            Vector3 toSelf = self.transform.position - target.transform.position;
            toSelf.y = 0f;
            float distance = toSelf.magnitude;
            // An archer / caster reaches much further than 5 m (R75: a Skeleton archer hit the bot 8 times from 17 m, "not
            // blocking"): its own weapon's range counts for projectiles, so the shield comes up against arrows too.
            float reach = Mathf.Max(5f, RangedReach(target) + 2f);
            if (distance > reach) return false;
            return distance < 0.01f || Vector3.Dot(target.transform.forward, toSelf / distance) >= 0.5f;
        }

        /// <summary>
        /// The threat reading this advisor acts on for <paramref name="body"/> (fight / space / retreat, seconds to die, foes): the
        /// cached one from its last Advise within a second, else a fresh ThreatLevel.Assess. Survival's fight-or-flee reads this
        /// so the two never disagree (R75: survival fled a lone Greyling the advisor would have fought).
        /// </summary>
        public static ThreatLevel.Reading Threat(Humanoid body)
        {
            if (body == null) return default;
            if (s_memory.TryGetValue(body, out Memory memory) && Time.time - memory.NextThreat < 1f && memory.ThreatSince > 0f)
                return memory.Threat;
            return ThreatLevel.Assess(body, body.GetHealth(), body.GetMaxHealth(), StaminaShare(body), ThreatLevel.Level.Fight, 0f);
        }

        /// <summary>No foe shoots from further than this (m): the outer bound for looking at ranged attackers.</summary>
        public const float MaxShotRange = 40f;

        /// <summary>
        /// How far <paramref name="foe"/> shoots (its projectile weapon's attack range), or 0 for a melee foe. A monster's
        /// inventory lives on its owner's peer only, so off the owner its right-hand item is read from the synced ZDO.
        /// </summary>
        public static float RangedReach(Character foe)
        {
            ItemDrop.ItemData weapon = (foe as Humanoid)?.GetCurrentWeapon();
            if ((weapon == null || !IsProjectileWeapon(weapon)) && ObjectDB.instance != null)
            {
                ZDO zdo = foe != null && foe.m_nview != null ? foe.m_nview.GetZDO() : null;
                int hash = zdo != null ? zdo.GetInt(ZDOVars.s_rightItem, 0) : 0;
                GameObject prefab = hash != 0 ? ObjectDB.instance.GetItemPrefab(hash) : null;
                ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop != null) weapon = drop.m_itemData;
            }
            return weapon != null && IsProjectileWeapon(weapon) && weapon.m_shared.m_attack != null
                ? weapon.m_shared.m_attack.m_attackRange : 0f;
        }

        // A foe out of sight is only worth a path this much longer than the straight line (x + m).
        private const float BehindWallPathFactor = 2f, BehindWallPathSlack = 10f;
        private static readonly Dictionary<Humanoid, AI.PathWalker> s_walkers = new Dictionary<Humanoid, AI.PathWalker>();
        private static int s_losMask;
        private static int LosMask => s_losMask != 0 ? s_losMask : (s_losMask = AI.Perception.ObstacleMask | LayerMask.GetMask("terrain"));

        /// <summary>A clear line chest to chest from <paramref name="self"/> to <paramref name="target"/> (solids, pieces, terrain; not characters).</summary>
        public static bool HasLineOfSight(Character self, Character target)
        {
            if (self == null || target == null) return false;
            Vector3 from = self.GetCenterPoint(), to = target.GetCenterPoint();
            if (!Physics.Linecast(from, to, out RaycastHit hit, LosMask, QueryTriggerInteraction.Ignore)) return true;
            // The target's own collider (a big creature) doesn't block the line to itself.
            return hit.collider.GetComponentInParent<Character>() == target;
        }

        private static AI.PathWalker WalkerFor(Humanoid self)
        {
            if (!s_walkers.TryGetValue(self, out AI.PathWalker walker))
            {
                if (s_walkers.Count > 32) s_walkers.Clear();
                s_walkers[self] = walker = new AI.PathWalker();
            }
            return walker;
        }

        private static float PathLength(Vector3 from, IReadOnlyList<Vector3> corners, Vector3 goal)
        {
            if (corners == null || corners.Count == 0) return Vector3.Distance(from, goal);
            float length = 0f;
            Vector3 last = from;
            foreach (Vector3 c in corners) { length += Vector3.Distance(last, c); last = c; }
            return length;
        }

        private static void NoLosLog(Humanoid self, Memory memory, string line)
        {
            // The same kind of line once per target (the distances change every frame).
            string kind = line.Substring(0, Mathf.Min(line.Length, 40));
            if (kind == memory.NoLosLogged) return;
            memory.NoLosLogged = kind;
            Debug.Log($"[CombatAdvisor] {self.m_name}: {line}");
        }

        // Three or more on us is a group; the first two nearer than PullSpread apart are still bunched.
        private const int GroupFoes = 3;
        private const float PullSpread = 3f;
        private static readonly List<Character> s_foes = new List<Character>();

        // The hostiles within radius targeting self (nearest first), and how far apart the first two are (their distances).
        private static void FoesOn(Humanoid self, float radius, List<Character> foes, out float spread)
        {
            foes.Clear();
            spread = float.MaxValue;
            Vector3 at = self.transform.position;
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == null || other == self || other.IsDead()) continue;
                if (Vector3.Distance(at, other.transform.position) > radius) continue;
                BaseAI ai = other.GetBaseAI();
                if (ai == null || !ThreatLevel.IsAfter(other, self, out bool foeOnMe) || !foeOnMe) continue;
                foes.Add(other);
            }
            foes.Sort((a, b) => (a.transform.position - at).sqrMagnitude.CompareTo((b.transform.position - at).sqrMagnitude));
            if (foes.Count >= 2)
                spread = Vector3.Distance(at, foes[1].transform.position) - Vector3.Distance(at, foes[0].transform.position);
        }

        /// <summary>Unaware prey: its AI isn't alerted and has no target (a grazing deer, an idle boar); sneak up on it (a hunt asks this too).</summary>
        public static bool IsUnaware(Character target)
        {
            BaseAI ai = target != null ? target.GetBaseAI() : null;
            return ai != null && !ThreatLevel.IsAlertedSynced(target) && FiresCore.Npc.Combat.ThreatLevel.TargetOf(ai) == null;
        }

        // Any hostile after us mid-swing within reach of us (TargetSwingingAt for each).
        private static bool AnySwingingAt(Humanoid self)
        {
            Vector3 at = self.transform.position;
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == null || other == self || other.IsDead()) continue;
                if (Vector3.Distance(at, other.transform.position) > MaxShotRange) continue;   // TargetSwingingAt judges each foe's own reach (archers: their bow's range)
                BaseAI ai = other.GetBaseAI();
                if (ai == null || !ThreatLevel.IsAfter(other, self, out bool swingOnMe) || !swingOnMe) continue;
                if (TargetSwingingAt(other, self)) return true;
            }
            return false;
        }

        private static int s_wayMask;
        private static int WayMask => s_wayMask != 0 ? s_wayMask : (s_wayMask = AI.Perception.ObstacleMask | LayerMask.GetMask("terrain"));
        private const float WayCheck = 1.5f;

        // A way to back off along away (flat), or turned up to 90° either side: no solid within WayCheck m. False = cornered.
        private static bool FreeWay(Humanoid self, Vector3 away, out Vector3 way)
        {
            way = Vector3.zero;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f) return false;
            away.Normalize();
            Vector3 centre = self.GetCenterPoint();
            float radius = Mathf.Max(0.2f, self.GetRadius() * 0.8f);
            foreach (float turn in s_wayTurns)
            {
                Vector3 dir = Quaternion.Euler(0f, turn, 0f) * away;
                if (Physics.SphereCast(centre, radius, dir, out RaycastHit hit, WayCheck, WayMask, QueryTriggerInteraction.Ignore)
                    && hit.normal.y < 0.5f) continue;
                way = dir;
                return true;
            }
            return false;
        }

        private static readonly float[] s_wayTurns = { 0f, 45f, -45f, 90f, -90f };

        // A creature much stronger than a trash mob swinging at us: worth a dodge over a block.
        private static bool HeavyIncoming(Character target) => target.IsBoss() || target.GetLevel() >= 3 || target.GetMaxHealth() >= 500f;
    }
}
