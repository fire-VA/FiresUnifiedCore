using System.Collections.Generic;
using FiresCore.Npc.Combat;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// The companions' decisions, with no body attached (Fire, 2026-09-29: "one brain, two bodies": a companion and the FDT autoplay
    /// bot run the same logic, so refining one refines the other; Tools\COMPANION_BRAIN_PLAN.md). Every input is passed in, so a
    /// CompanionAI (BaseAI body) and a Player driven through inputs ask the same questions and get the same answers. Slice 1: which
    /// hostile threatens the fight, and how much each target is worth.
    /// </summary>
    public static class CompanionBrain
    {
        /// <summary>
        /// Whether <paramref name="target"/> threatens this fight: inside the owner's defense bubble, locked onto
        /// <paramref name="self"/> or the owner, locked onto any player or party member while the fight is within the combat leash
        /// of the owner, or <paramref name="self"/> was hit lately. (CompanionAI's former IsThreatToOwnerOrSelf.) For a player
        /// body, pass the player as its own owner.
        /// </summary>
        public static bool IsThreat(Character self, Character target, Vector3 ownerPos, Character ownerCharacter,
            float defenseRadius, float leashDistance, bool recentlyHit)
        {
            if (target == null) return false;

            float distToOwner = Vector3.Distance(target.transform.position, ownerPos);
            if (distToOwner <= defenseRadius) return true;

            var targetAI = target.GetComponent<BaseAI>();
            if (targetAI != null)
            {
                var aiTarget = FiresCore.Npc.Combat.ThreatLevel.TargetOf(targetAI);
                if (aiTarget != null)
                {
                    if (aiTarget == self) return true;
                    if (ownerCharacter != null && aiTarget == ownerCharacter) return true;
                    // Something after another player, or after a party member, only pulls us in while that fight is near our owner
                    // (Fire 2026-09-28: party members count as well).
                    if (distToOwner <= leashDistance &&
                        (aiTarget.IsPlayer() || Archetypes.ClassTargeting.IsPartyMember(self, aiTarget)))
                        return true;
                }
                // Off the monster's owner its AI target reads null (R76: a drake spitting from 18 m was "no foe after us"): an
                // alerted enemy for which this body is the nearest player counts (ThreatLevel.IsAfter).
                else if (ThreatLevel.IsAfter(target, self)) return true;
            }

            return recentlyHit;
        }

        /// <summary>
        /// How much a target is worth; LOWER is better (CompanionAI's former CalculateTargetScore). Starts at the distance to the
        /// owner, then: the group's assigned target -200; with a ThreatAnalyzer its class (boss -80, elite -50, dangerous -30,
        /// normal -10), after the owner -100 x interceptPriority, after self -50, mid-attack -25, under 25 % health -15; without
        /// one, after the owner -100 x interceptPriority, after self -50, within 10 m of the owner -30; within 2 x attack range
        /// of self -20.
        /// </summary>
        public static float ScoreTarget(Character self, Character target, Character ownerCharacter, Vector3 ownerPos,
            float attackRange, float interceptPriority, ThreatAnalyzer analyzer, Character assignedTarget)
        {
            Vector3 targetPos = target.transform.position;
            float distToMe = Vector3.Distance(self.transform.position, targetPos);
            float distToOwner = Vector3.Distance(ownerPos, targetPos);

            float score = distToOwner;
            if (assignedTarget != null && assignedTarget == target) score -= 200f;

            if (analyzer != null)
            {
                var profile = analyzer.GetThreatProfile(target);
                switch (profile.Classification)
                {
                    case ThreatAnalyzer.EnemyClass.Boss: score -= 80f; break;
                    case ThreatAnalyzer.EnemyClass.Elite: score -= 50f; break;
                    case ThreatAnalyzer.EnemyClass.Dangerous: score -= 30f; break;
                    case ThreatAnalyzer.EnemyClass.Normal: score -= 10f; break;
                }
                if (profile.IsTargetingOwner) score -= 100f * interceptPriority;
                else if (profile.IsTargetingCompanion) score -= 50f;
                if (profile.IsCurrentlyAttacking) score -= 25f;
                if (profile.HealthPercent < 0.25f) score -= 15f;
            }
            else
            {
                if (ownerCharacter != null)
                {
                    var targetAI = target.GetComponent<BaseAI>();
                    if (targetAI != null)
                    {
                        var aiTarget = FiresCore.Npc.Combat.ThreatLevel.TargetOf(targetAI);
                        if (aiTarget == ownerCharacter) score -= 100f * interceptPriority;
                        else if (aiTarget == self) score -= 50f;
                    }
                }
                if (distToOwner < 10f) score -= 30f;
            }

            if (distToMe < attackRange * 2f) score -= 20f;
            return score;
        }

        // ---- Slice 2: what to wield (WeaponSwapManager's rules, moved verbatim) ----

        /// <summary>Bows, crossbows, staves, projectile attacks and anything named staff or wand.</summary>
        public static bool IsRangedWeapon(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;

            // Bows and crossbows are ranged
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                return true;
            if (item.m_shared.m_skillType == Skills.SkillType.Bows ||
                item.m_shared.m_skillType == Skills.SkillType.Crossbows)
                return true;
            if (item.m_shared.m_attack?.m_bowDraw == true ||
                item.m_shared.m_attack?.m_requiresReload == true)
                return true;

            // Staves are ranged (magic weapons with projectiles)
            if (item.m_shared.m_skillType == Skills.SkillType.ElementalMagic ||
                item.m_shared.m_skillType == Skills.SkillType.BloodMagic)
                return true;

            // Only the attack type counts: AtgeirBronze/AtgeirGold keep a leftover m_attackProjectile on a melee swing.
            if (item.m_shared.m_attack?.m_attackType == Attack.AttackType.Projectile)
                return true;

            // Check name for staff indicators
            string itemName = item.m_shared.m_name?.ToLowerInvariant() ?? "";
            if (itemName.Contains("staff") || itemName.Contains("wand"))
                return true;

            return false;
        }

        /// <summary>Pickaxes and chopping axes: for gathering, never picked to fight with (battleaxes are weapons).</summary>
        public static bool IsGatheringTool(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;

            // Check if it's a Tool type item (pickaxes are Tools in Valheim)
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool)
                return true;

            // Check skill type - Pickaxes and WoodCutting are gathering skills
            if (item.m_shared.m_skillType == Skills.SkillType.Pickaxes ||
                item.m_shared.m_skillType == Skills.SkillType.WoodCutting)
                return true;

            string itemName = item.m_shared.m_name?.ToLowerInvariant() ?? "";
            string prefabName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";

            // Pickaxes are always gathering tools
            if (itemName.Contains("pickaxe") || prefabName.Contains("pickaxe"))
                return true;

            // "axe_" prefix or "_axe" without battle/greataxe/dualaxe/jotunbane: a chopping axe
            if ((prefabName.StartsWith("axe") || prefabName.Contains("_axe")) &&
                !prefabName.Contains("battle") && !prefabName.Contains("greataxe") &&
                !prefabName.Contains("dualaxe") && !prefabName.Contains("jotunbane"))
            {
                if (item.m_shared.m_skillType == Skills.SkillType.WoodCutting ||
                    item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool)
                    return true;
            }

            return false;
        }

        /// <summary>An elemental or blood magic staff (or anything whose prefab is named staff).</summary>
        public static bool IsStaff(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;

            var skill = item.m_shared.m_skillType;
            if (skill == Skills.SkillType.ElementalMagic || skill == Skills.SkillType.BloodMagic)
                return true;

            string prefabName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
            return prefabName.Contains("staff");
        }

        /// <summary>
        /// The melee and ranged weapons a body would fight with, from <paramref name="items"/> in the order given (a companion passes
        /// its hands, then its back slots, then storage; a player its equipped items first). Same rule as a companion's weapon scan: the
        /// FIRST combat weapon of each kind, tools skipped, an offensive staff counts as ranged, a support staff never does.
        /// </summary>
        public static void ChooseWeapons(IEnumerable<ItemDrop.ItemData> items, out ItemDrop.ItemData melee, out ItemDrop.ItemData ranged)
        {
            melee = null;
            ranged = null;
            if (items == null) return;
            foreach (var item in items)
            {
                if (!CompanionCombat.IsCombatWeapon(item)) continue;
                if (IsGatheringTool(item)) continue;
                // Broken (durability 0): vanilla won't equip it (Humanoid.EquipItem), so it's no pick unless already in hand, where it
                // still swings (R73 Crypt4: two broken Clubs were "chosen", the equip failed, the bot fought bare-handed as "no weapon").
                if (item.m_shared.m_useDurability && item.m_durability <= 0f && !item.m_equipped) continue;
                if (IsStaff(item))
                {
                    if (!Archetypes.ArchetypeUtils.IsSupportStaff(item) && ranged == null) ranged = item;
                    continue;
                }
                if (IsRangedWeapon(item))
                {
                    if (ranged == null) ranged = item;
                }
                else if (melee == null)
                {
                    melee = item;
                }
            }
        }

        /// <summary>Everything <see cref="WeighWeapons"/> looks at, gathered by the body.</summary>
        public struct WeaponSituation
        {
            public float DistToTarget;
            public bool CurrentlyRanged;
            public int VeryCloseEnemies;   // within 4 m
            public int CloseEnemies;       // within MeleePreferenceDistance
            public int FarEnemies;         // RangedPreferenceDistance .. 2x
            public bool HasThreatProfile;
            public ThreatAnalyzer.EnemyClass TargetClass;
            public float TargetHealth;
            public bool TargetApproaching;
            public bool TargetFleeing;
            public float HeightDiff;
            public bool Unreachable;
            public bool Swimming;
            public bool HasStamina;
            public float StaminaShare;
            public bool CriticalRecovery;
            public bool HasHealth;
            public float HealthShare;
            public bool CombatEntry;       // far, holding melee, not fighting yet
            public float MeleePreferenceDistance;   // companion default 5
            public float RangedPreferenceDistance;  // companion default 12
            public float ElevationThreshold;        // companion default 3
        }

        private const float PointBlankDistance = 3f;
        private const float PreferenceDistanceMargin = 1.5f;
        private const int SurroundedEnemyCount = 3;
        private const float LowTargetHealthFraction = 0.3f;
        private const float LowStaminaFraction = 0.3f;
        private const float LowHealthFraction = 0.3f;
        private const float MinScoreDifferenceToSwap = 0.15f;
        private const float PointBlankMeleeBonus = 0.6f;
        private const float CloseRangeMeleeBonus = 0.4f;
        private const float CloseRangeRangedBonus = 0.1f;
        private const float MediumRangeBlendWeight = 0.3f;
        private const float LongRangeRangedBonus = 0.5f;
        private const float VeryLongRangeRangedBonus = 0.2f;
        private const float CrowdedMeleeBonus = 0.4f;
        private const float GroupedMeleeBonus = 0.25f;
        private const float SpreadOutRangedBonus = 0.3f;
        private const float SurroundedMeleeBonus = 0.3f;
        private const float ToughTargetRangedBonus = 0.3f;
        private const float DangerousApproachRangedBonus = 0.25f;
        private const float TrivialTargetMeleeBonus = 0.2f;
        private const float WeakTargetRangedBonus = 0.2f;
        private const float ElevationRangedBonus = 0.5f;
        private const float ElevationMeleePenalty = 0.3f;
        private const float UnreachableRangedBonus = 0.6f;
        private const float UnreachableMeleePenalty = 0.4f;
        private const float SwimmingRangedBonus = 0.3f;
        private const float FleeingTargetRangedBonus = 0.4f;
        private const float ApproachingFarRangedBonus = 0.3f;
        private const float ApproachingCloseMeleeBonus = 0.2f;
        private const float LowStaminaRangedBonus = 0.2f;
        private const float StaminaRecoveryRangedBonus = 0.5f;
        private const float StaminaRecoveryMeleePenalty = 0.3f;
        private const float LowHealthRangedBonus = 0.3f;
        private const float CombatEntryRangedBonus = 0.3f;

        /// <summary>Added to the weapon in hand so the choice doesn't flip-flop.</summary>
        public const float CurrentWeaponHysteresisBonus = 0.15f;

        /// <summary>
        /// Melee or ranged for this fight (WeaponSwapManager's former EvaluateTacticalSituation): distance first, then crowding, the
        /// target's class, height, reachability, swimming, the target fleeing or closing, the body's stamina and health, a ranged
        /// opening, and a hysteresis bonus for the current weapon. Scores are 0..1; KeepCurrent when they are within 0.15.
        /// </summary>
        public static WeaponSwapManager.WeaponRecommendation WeighWeapons(WeaponSituation s, out float rangedScore, out float meleeScore)
        {
            rangedScore = 0f;
            meleeScore = 0f;
            float d = s.DistToTarget;
            float meleePref = s.MeleePreferenceDistance;
            float rangedPref = s.RangedPreferenceDistance;

            // 1: distance
            if (d <= PointBlankDistance)
            {
                meleeScore += PointBlankMeleeBonus;
            }
            else if (d <= meleePref)
            {
                meleeScore += CloseRangeMeleeBonus;
                rangedScore += CloseRangeRangedBonus;
            }
            else if (d <= rangedPref)
            {
                float rangedBlend = Mathf.InverseLerp(meleePref, rangedPref, d);
                meleeScore += MediumRangeBlendWeight * (1f - rangedBlend);
                rangedScore += MediumRangeBlendWeight * rangedBlend;
            }
            else
            {
                rangedScore += LongRangeRangedBonus;
                if (d > rangedPref * PreferenceDistanceMargin) rangedScore += VeryLongRangeRangedBonus;
            }

            // 2: enemy count and spread
            if (s.VeryCloseEnemies >= 2) meleeScore += CrowdedMeleeBonus;
            else if (s.CloseEnemies >= 2) meleeScore += GroupedMeleeBonus;
            if (s.FarEnemies >= 2 && s.CloseEnemies <= 1) rangedScore += SpreadOutRangedBonus;
            if (s.CloseEnemies >= SurroundedEnemyCount) meleeScore += SurroundedMeleeBonus;

            // 3: the target's class
            if (s.HasThreatProfile)
            {
                if ((s.TargetClass == ThreatAnalyzer.EnemyClass.Boss || s.TargetClass == ThreatAnalyzer.EnemyClass.Elite) && d > meleePref)
                    rangedScore += ToughTargetRangedBonus;
                if (s.TargetClass >= ThreatAnalyzer.EnemyClass.Dangerous && s.TargetApproaching && d > meleePref)
                    rangedScore += DangerousApproachRangedBonus;
                if (s.TargetClass == ThreatAnalyzer.EnemyClass.Trivial && s.CloseEnemies >= 1)
                    meleeScore += TrivialTargetMeleeBonus;
                if (s.TargetHealth < LowTargetHealthFraction && d > meleePref)
                    rangedScore += WeakTargetRangedBonus;
            }

            // 4: terrain and reachability
            if (s.HeightDiff > s.ElevationThreshold)
            {
                rangedScore += ElevationRangedBonus;
                meleeScore -= ElevationMeleePenalty;
            }
            if (s.Unreachable)
            {
                rangedScore += UnreachableRangedBonus;
                meleeScore -= UnreachableMeleePenalty;
            }
            if (s.Swimming && d > PointBlankDistance) rangedScore += SwimmingRangedBonus;

            // 5: the target fleeing or closing
            if (s.TargetFleeing && d > meleePref) rangedScore += FleeingTargetRangedBonus;
            if (s.TargetApproaching)
            {
                if (d > rangedPref) rangedScore += ApproachingFarRangedBonus;
                else if (d <= meleePref * PreferenceDistanceMargin) meleeScore += ApproachingCloseMeleeBonus;
            }

            // 6: the body's stamina and health
            if (s.HasStamina)
            {
                if (s.StaminaShare < LowStaminaFraction) rangedScore += LowStaminaRangedBonus;
                if (s.CriticalRecovery)
                {
                    rangedScore += StaminaRecoveryRangedBonus;
                    meleeScore -= StaminaRecoveryMeleePenalty;
                }
            }
            if (s.HasHealth && s.HealthShare < LowHealthFraction) rangedScore += LowHealthRangedBonus;

            // 7: opening shots
            if (d > rangedPref && !s.CurrentlyRanged && s.CombatEntry) rangedScore += CombatEntryRangedBonus;

            // 8: hysteresis
            if (s.CurrentlyRanged) rangedScore += CurrentWeaponHysteresisBonus;
            else meleeScore += CurrentWeaponHysteresisBonus;

            rangedScore = Mathf.Clamp01(rangedScore);
            meleeScore = Mathf.Clamp01(meleeScore);

            if (Mathf.Abs(rangedScore - meleeScore) < MinScoreDifferenceToSwap) return WeaponSwapManager.WeaponRecommendation.KeepCurrent;
            return rangedScore > meleeScore ? WeaponSwapManager.WeaponRecommendation.Ranged : WeaponSwapManager.WeaponRecommendation.Melee;
        }

        /// <summary>The weapon kind a body's class bar is built on (<see cref="ClassWeapon"/>).</summary>
        public enum ClassWeaponWant { None, Melee, Ranged }

        /// <summary>
        /// Set by a class mod (RPGClasses): which weapon kind drives the body's active class bar, None without a class. Core can't
        /// reference the class mod, so it asks through this. R72: a tank handed a bow at 2.2 m had every class skill refused
        /// (WrongArchetype).
        /// </summary>
        public static System.Func<Humanoid, ClassWeaponWant> ClassWeapon;

        /// <summary>No ranged pick with the target this close while melee can reach it (R72: "wield $item_bow … 2.2 m").</summary>
        public const float RangedMinDistance = 4f;

        /// <summary>The class bar's weapon kind for <paramref name="body"/>; None without a hook, a class, or when the hook throws.</summary>
        public static ClassWeaponWant ClassWeaponFor(Humanoid body)
        {
            var hook = ClassWeapon;
            if (hook == null || body == null) return ClassWeaponWant.None;
            try { return hook(body); }
            catch { return ClassWeaponWant.None; }
        }

        /// <summary>
        /// The final say over a weighed pick (<see cref="WeighWeapons"/>), for the bot and the companions alike:
        /// - melee can't reach the target: the pick stands (ranged is the only way to hit it);
        /// - a class bar built on melee: melee; built on ranged: ranged;
        /// - no class, target inside <see cref="RangedMinDistance"/>: melee.
        /// <paramref name="reason"/> says which rule decided ("weighed" when none did).
        /// </summary>
        public static WeaponSwapManager.WeaponRecommendation GateWeapons(Humanoid body, WeaponSwapManager.WeaponRecommendation pick,
            float distance, bool meleeCanReach, out string reason)
        {
            reason = "weighed";
            if (!meleeCanReach) return pick;
            switch (ClassWeaponFor(body))
            {
                case ClassWeaponWant.Melee:
                    reason = "the class bar is melee";
                    return WeaponSwapManager.WeaponRecommendation.Melee;
                case ClassWeaponWant.Ranged:
                    reason = "the class bar is ranged";
                    return WeaponSwapManager.WeaponRecommendation.Ranged;
            }
            if (distance < RangedMinDistance && pick != WeaponSwapManager.WeaponRecommendation.Melee)
            {
                reason = $"target inside {RangedMinDistance:0} m and melee reaches";
                return WeaponSwapManager.WeaponRecommendation.Melee;
            }
            return pick;
        }

        /// <summary>Living hostiles to <paramref name="self"/> (not players, not tamed) between the two distances of <paramref name="at"/>.</summary>
        public static int CountHostiles(Character self, Vector3 at, float minRange, float maxRange)
        {
            int count = 0;
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == self) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!BaseAI.IsEnemy(self, character)) continue;

                float dist = Vector3.Distance(at, character.transform.position);
                if (dist >= minRange && dist <= maxRange) count++;
            }
            return count;
        }

        /// <summary>The target moving away from <paramref name="from"/> faster than 1 m/s.</summary>
        public static bool IsFleeing(Character target, Vector3 from)
        {
            if (target == null) return false;
            Vector3 velocity = target.GetVelocity();
            if (velocity.magnitude < 1f) return false;
            Vector3 toUs = (from - target.transform.position).normalized;
            return Vector3.Dot(velocity.normalized, toUs) < -0.5f;
        }

        /// <summary>The target moving toward <paramref name="from"/> faster than 0.5 m/s.</summary>
        public static bool IsApproaching(Character target, Vector3 from)
        {
            if (target == null) return false;
            Vector3 velocity = target.GetVelocity();
            if (velocity.magnitude < 0.5f) return false;
            Vector3 toUs = (from - target.transform.position).normalized;
            return Vector3.Dot(velocity.normalized, toUs) > 0.5f;
        }

        // ---- Class skills (the brain picks, the body presses; PickClassSkill arrives in Core 0.2.158) ----

        /// <summary>
        /// One class skill that is Ready on a body's bar, as FiresRPGClasses reports it
        /// (ClassSkillOptions.ReadyOnActiveBar). The brain chooses among them; the driver presses <see cref="Slot"/>.
        /// </summary>
        public struct SkillOption
        {
            /// <summary>The bar slot to press.</summary>
            public int Slot;
            /// <summary>The skill's node id.</summary>
            public string Id;
            /// <summary>Opener, Reactive, Sustained or Situational (AoE damage: Situational with <see cref="Aoe"/>).</summary>
            public Archetypes.SkillDecisionSystem.SkillCategory Category;
            public float MinRange;
            public float MaxRange;
            public bool Heal;
            public bool Buff;
            public bool Aoe;
            public bool Debuff;
        }

        // SkillDecisionSystem's thresholds.
        private const float SkillLowHealth = 0.4f;
        private const float SkillPartyRange = 20f;
        private const float SkillSurroundedRange = 5f;
        private const int SkillSurroundedCount = 3;
        private const int SkillFocusedCount = 2;
        private const float SkillOpenerSeconds = 3f;
        private const float SkillSituationalSeconds = 8f;

        /// <summary>
        /// The class skill to use now, from the Ready options on the body's bar, or -1 (the companions' SkillDecisionSystem
        /// triggers, for any body). In order: hurt (self or a party member within 20 m under 40 %) -> a Reactive heal, else a
        /// Reactive defence; focused (2+ enemies on self) or surrounded (3+ within 5 m) -> an AoE; the first 3 s of a fight -> an
        /// Opener; the first 8 s -> a Situational control/mark; otherwise a Sustained skill. Attacks must have the target inside
        /// [MinRange, MaxRange]; heals and buffs on self don't. Returns the option's Slot.
        /// </summary>
        public static int PickClassSkill(Character caster, Character target, float distance, float fightSeconds, IReadOnlyList<SkillOption> ready) =>
            PickClassSkill(caster, target, distance, fightSeconds, ready, out _);

        /// <summary>
        /// <see cref="PickClassSkill(Character, Character, float, float, IReadOnlyList{SkillOption})"/> plus why: for a pick, the
        /// rule that chose it ("<id> (opener)"); for none, each Ready node's reason ("monk_flurry Sustained: out of range 0-3 m").
        /// After the opener / control windows, a Ready attack or self-buff in range is still used rather than left idle (R62 class_drill:
        /// a kit with no Sustained skill never cast after 8 s).
        /// </summary>
        public static int PickClassSkill(Character caster, Character target, float distance, float fightSeconds, IReadOnlyList<SkillOption> ready,
            out string why)
        {
            why = "ready empty";
            if (caster == null || ready == null || ready.Count == 0) return -1;
            const Archetypes.SkillDecisionSystem.SkillCategory Reactive = Archetypes.SkillDecisionSystem.SkillCategory.Reactive;
            const Archetypes.SkillDecisionSystem.SkillCategory Opener = Archetypes.SkillDecisionSystem.SkillCategory.Opener;
            const Archetypes.SkillDecisionSystem.SkillCategory Situational = Archetypes.SkillDecisionSystem.SkillCategory.Situational;
            const Archetypes.SkillDecisionSystem.SkillCategory Sustained = Archetypes.SkillDecisionSystem.SkillCategory.Sustained;

            bool hurt = caster.GetHealthPercentage() < SkillLowHealth || PartyMemberHurt(caster);
            int slot;
            if (hurt)
            {
                if ((slot = First(caster, ready, o => o.Category == Reactive && o.Heal, target, distance)) >= 0) return Chose(ready, slot, "heal, hurt", out why);
                if ((slot = First(caster, ready, o => o.Category == Reactive, target, distance)) >= 0) return Chose(ready, slot, "defence, hurt", out why);
            }

            // Low eitr (0.2.201): a Ready eitr restore (Eitr Well) answers the eitr bar the way a Reactive heal answers health.
            float eitr = EitrShare(caster);
            if (eitr < SkillLowEitr && (slot = First(caster, ready, IsEitrRestore, target, distance)) >= 0)
                return Chose(ready, slot, $"eitr restore, eitr {eitr * 100f:0} %", out why);

            if (target == null) { why = "no target: " + Reasons(ready, target, distance, hurt); return -1; }
            Vector3 at = caster.transform.position;
            bool pressed = CountHostiles(caster, at, 0f, SkillSurroundedRange) >= SkillSurroundedCount || EnemiesTargeting(caster) >= SkillFocusedCount;
            if (pressed && (slot = First(caster, ready, o => o.Aoe, target, distance)) >= 0) return Chose(ready, slot, "AoE, pressed", out why);
            if (fightSeconds <= SkillOpenerSeconds && (slot = First(caster, ready, o => o.Category == Opener, target, distance)) >= 0)
                return Chose(ready, slot, "opener", out why);
            if (fightSeconds <= SkillSituationalSeconds && (slot = First(caster, ready, o => o.Category == Situational && !o.Aoe, target, distance)) >= 0)
                return Chose(ready, slot, "control", out why);
            if ((slot = First(caster, ready, o => o.Category == Sustained, target, distance)) >= 0) return Chose(ready, slot, "sustained", out why);
            // Past the windows: any Ready attack or self-buff in range beats leaving it on the bar (heals / defences stay for when hurt).
            if ((slot = First(caster, ready, o => o.Category != Reactive && !IsEitrRestore(o) && (!o.Aoe || distance <= o.MaxRange), target, distance)) >= 0)
                return Chose(ready, slot, "ready in range", out why);
            why = Reasons(ready, target, distance, hurt);
            return -1;
        }

        /// <summary>Eitr share under which a Ready eitr restore is cast (the class drill counts a low under 10 % and back at 25 %).</summary>
        public const float SkillLowEitr = 0.25f;

        // A bar skill that restores eitr: its node id names eitr and it acts on the caster (Eitr Well: "mage_eitr_well").
        private static bool IsEitrRestore(SkillOption option) =>
            option.Id != null && option.Id.IndexOf("eitr", System.StringComparison.OrdinalIgnoreCase) >= 0 && (option.Buff || option.Heal);

        /// <summary>The body's eitr share (1 without an eitr bar: companions, or a player with no eitr food).</summary>
        public static float EitrShare(Character body)
        {
            if (!(body is Player player)) return 1f;
            float max = player.GetMaxEitr();
            return max > 0f ? player.GetEitr() / max : 1f;
        }

        private static int Chose(IReadOnlyList<SkillOption> ready, int slot, string rule, out string why)
        {
            string id = "?";
            for (int i = 0; i < ready.Count; i++) if (ready[i].Slot == slot) { id = ready[i].Id; break; }
            why = $"{id} ({rule})";
            return slot;
        }

        // One reason per Ready node, stable while nothing changes (ranges, not the live distance), for the no-skill line.
        private static string Reasons(IReadOnlyList<SkillOption> ready, Character target, float distance, bool hurt)
        {
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < ready.Count; i++)
            {
                SkillOption option = ready[i];
                bool onSelf = option.Heal || option.Buff;
                string reason =
                    IsEitrRestore(option) ? $"eitr restore: kept for eitr under {SkillLowEitr * 100f:0} %"
                    : option.Category == Archetypes.SkillDecisionSystem.SkillCategory.Reactive && !hurt ? "reactive: kept for when hurt (under 40 %)"
                    : !onSelf && target == null ? "no target"
                    : !onSelf && distance < option.MinRange ? $"too close (range {option.MinRange:0.#}-{option.MaxRange:0.#} m)"
                    : !onSelf && distance > option.MaxRange ? $"out of range (0-{option.MaxRange:0.#} m)"
                    : "no rule took it";
                if (text.Length > 0) text.Append("; ");
                text.Append($"{option.Id} {option.Category}{(option.Aoe ? " AoE" : "")}: {reason}");
            }
            return text.ToString();
        }

        private static int First(Character caster, IReadOnlyList<SkillOption> ready, System.Func<SkillOption, bool> want, Character target, float distance)
        {
            for (int i = 0; i < ready.Count; i++)
            {
                SkillOption option = ready[i];
                if (!want(option)) continue;
                bool onSelf = option.Heal || option.Buff;
                if (!onSelf && (target == null || distance < option.MinRange || distance > option.MaxRange)) continue;
                // A damaging skill with a neutral bystander in its area or its lane is skipped (R74: a volley at a Hatchling hit
                // a Dverger-faction wild companion, which then fought the bot for minutes).
                if (!onSelf && caster != null && NeutralInTheWay(caster, target, option.Aoe ? AoeNeutralRadius : LaneNeutralRadius, out string neutral))
                {
                    NeutralSkipLog(caster, option.Id, neutral);
                    continue;
                }
                return option.Slot;
            }
            return -1;
        }

        /// <summary>Round the target an area skill may reach a bystander; beside the lane a volley may.</summary>
        public const float AoeNeutralRadius = 6f, LaneNeutralRadius = 2f;

        /// <summary>
        /// A living character that is neither the caster's party nor an enemy of it (vanilla's IsEnemy: a Dverger passer-by, a
        /// neutral wild companion, another party's companion with PvP off) within <paramref name="radius"/> m of the target, or of
        /// the straight line from the caster to it. Hitting one starts a fight nobody chose.
        /// </summary>
        public static bool NeutralInTheWay(Character caster, Character target, float radius, out string who)
        {
            who = null;
            if (caster == null || target == null) return false;
            Vector3 from = caster.transform.position, to = target.transform.position;
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == null || other == caster || other == target || other.IsDead()) continue;
                Vector3 p = other.transform.position;
                bool near = Vector3.Distance(p, to) <= radius;
                if (!near)
                {
                    Vector3 ab = to - from;
                    float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector3.Dot(p - from, ab) / ab.sqrMagnitude) : 0f;
                    near = Vector3.Distance(p, from + ab * t) <= LaneNeutralRadius;
                }
                if (!near) continue;
                if (Archetypes.ClassTargeting.IsPartyMember(caster, other)) continue;
                if (Archetypes.ClassTargeting.IsEnemyTarget(caster, other)) continue;
                // A player can only be hurt with PvP on (0.2.217, R87 class_drill: "[CompanionBrain] Human: skill ranger_mark: skipped
                // neutral Human in the area" x305 for the bot beside Fire's tester, PvP off, so no attack skill was ever cast).
                if (other is Player bystander && !(bystander.IsPVPEnabled() && (!(caster is Player casterPlayer) || casterPlayer.IsPVPEnabled()))) continue;
                who = other.m_name;
                return true;
            }
            return false;
        }

        private static readonly Dictionary<Character, (string line, float at)> s_neutralLogged = new Dictionary<Character, (string, float)>();

        private static void NeutralSkipLog(Character caster, string skill, string neutral)
        {
            string line = $"skill {skill}: skipped neutral {neutral} in the area";
            if (s_neutralLogged.TryGetValue(caster, out var last) && last.line == line && Time.time - last.at < 10f) return;
            if (s_neutralLogged.Count > 64) s_neutralLogged.Clear();
            s_neutralLogged[caster] = (line, Time.time);
            Debug.Log($"[CompanionBrain] {caster.m_name}: {line}");
        }

        private static bool PartyMemberHurt(Character caster)
        {
            Vector3 at = caster.transform.position;
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == null || other == caster || other.IsDead()) continue;
                if (Vector3.Distance(at, other.transform.position) > SkillPartyRange) continue;
                if (other.GetHealthPercentage() >= SkillLowHealth) continue;
                if (Archetypes.ClassTargeting.IsPartyMember(caster, other)) return true;
            }
            return false;
        }

        private static int EnemiesTargeting(Character self)
        {
            int count = 0;
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == null || other == self || other.IsDead()) continue;
                var ai = other.GetBaseAI();
                if (ai != null && ThreatLevel.IsAfter(other, self, out bool onSelf) && onSelf) count++;
            }
            return count;
        }

        // ---- Slice 2: reach (R56: the bot swam after targets and landed 0 hits) ----

        /// <summary>Height gap past which a melee body can't reach a target (the companion's elevation swap threshold).</summary>
        public const float MeleeHeightReach = 3f;

        /// <summary>
        /// Whether a body can fight <paramref name="target"/> from where it stands: never while swimming (no attack works in water),
        /// and with only melee in hand, not a target in water or more than <see cref="MeleeHeightReach"/> above or below unless it is
        /// already within <paramref name="reach"/> + 1 m. A ranged weapon reaches anything out of the water.
        /// </summary>
        public static bool CanFight(Character self, Character target, float reach, bool ranged) =>
            CanFight(self, target, reach, ranged, MeleeHeightReach);

        /// <summary>
        /// <see cref="CanFight(Character, Character, float, bool)"/> with the height limit given, so a caller can hold a hysteresis
        /// band (R57: a target 3.0 m up flipped Approach / OutOfReach every frame). The height only counts once the body is close
        /// (within reach + <see cref="HeightCheckMargin"/>): from further out a slope is just ground to walk up.
        /// </summary>
        public static bool CanFight(Character self, Character target, float reach, bool ranged, float heightLimit)
        {
            if (self == null || target == null) return false;
            if (self.IsSwimming()) return false;
            if (ranged) return true;
            if (target.IsSwimming()) return false;
            Vector3 gap = target.transform.position - self.transform.position;
            float flat = new Vector2(gap.x, gap.z).magnitude;
            if (flat > reach + HeightCheckMargin) return true;
            return Mathf.Abs(gap.y) <= heightLimit;
        }

        /// <summary>How close (past weapon reach) the melee height limit starts to count.</summary>
        public const float HeightCheckMargin = 2f;

        /// <summary>
        /// Whether <paramref name="target"/> may be picked as a fight at all by a body that isn't a companion's own AI: never a tamed
        /// creature, a companion or a player unless it is coming for <paramref name="self"/> (its AI targets self). R57: with PvP on
        /// the tester's idle companions counted as enemies within 8 m, and the bot hit them 101 times.
        /// </summary>
        public static bool IsFairGame(Character self, Character target)
        {
            if (target == null) return false;
            bool owned = target.IsPlayer() || target.IsTamed() || target.GetComponent<CompanionController>() != null;
            if (!owned) return true;
            var ai = target.GetBaseAI();
            return ai != null && FiresCore.Npc.Combat.ThreatLevel.TargetOf(ai) == self;
        }

        /// <summary>
        /// What to do with the load (<see cref="CarryRules.Plan"/>): store what is above <paramref name="keep"/> in a chest within
        /// reach, drop junk nothing uses, or with no chest while overweight drop the least valuable per weight. The FDT bot passes
        /// its own id and allowDrop; companions carry for the player and don't drop.
        /// </summary>
        public static CarryPlan PlanCarry(Humanoid body, Inventory inventory, float maxWeight, IReadOnlyDictionary<string, int> keep,
            long ownerId, bool allowDrop = true, bool tripAhead = false, float storeAt = -1f, float chestRadius = -1f) =>
            CarryRules.Plan(body, inventory, maxWeight, keep, ownerId, allowDrop, tripAhead, storeAt, chestRadius);

        /// <summary>Whether to pick up an item at all (<see cref="CarryRules.CanTake"/>): fits, or needed (then make room first).</summary>
        public static bool CanTake(Humanoid body, Inventory inventory, ItemDrop.ItemData item, int amount,
            IReadOnlyDictionary<string, int> keep, float maxWeight, out bool makeRoom, out string why) =>
            CarryRules.CanTake(body, inventory, item, amount, keep, maxWeight, out makeRoom, out why);
    }
}
