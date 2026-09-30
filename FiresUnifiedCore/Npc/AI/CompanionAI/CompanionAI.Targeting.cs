using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Combat;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Target detection, scoring, and switching logic.
    /// </summary>
    public partial class CompanionAI
    {
        #region Target Detection

        private void UpdateTargetDetection(float dt)
        {
            if (Time.time - _lastTargetScanTime < TargetScanInterval)
                return;
            _lastTargetScanTime = Time.time;

            // A player command means nothing else: skip the threat scan entirely, since re-acquiring a target every scan
            // only for the combat state to clear it again had the two systems fighting every frame.
            if (_stateController != null && _stateController.HasAbsolutePriorityCommand)
            {
                if (_targetCreature != null)
                    ClearTarget();
                return;
            }

            // POST-TELEPORT COMBAT SUPPRESSION
            // After a long-distance teleport (stranded force-pull, dungeon entry,
            // portal jump, owner respawn) we deliberately refuse to acquire any
            // new target for a short window.  Without this gate, a companion
            // who was "wandering too far" and got yanked back will instantly
            // re-acquire the same enemy (or a fresh one) and try to run off
            // again — which is the behaviour the owner explicitly told us to
            // stop.  During the window we also drop any target that snuck back
            // in (defence in depth in case ClearTarget was missed somewhere).
            // After the window expires this branch falls through and normal
            // threat detection resumes immediately.
            if (Time.time < _combatSuppressedUntilTime)
            {
                if (_targetCreature != null)
                    ClearTarget();
                return;
            }

            if (_currentState == AIState.Fleeing)
                return;
            
            if (_combatMovement != null && _combatMovement.HasCommandPriority)
                return;
            
            if (_npcModule != null && _npcModule.IsStationedAsNpc)
            {
                if (!_npcModule.WasDirectlyAttacked)
                {
                    if (_targetCreature != null)
                    {
                        ClearTarget();
                    }
                    return;
                }
            }
            
            bool isStaying = !_shouldFollow && _hasHomePositionSet;
            bool wasRecentlyDamaged = Time.time - _lastDirectlyDamagedTime < DirectDamageAlertDuration;
            float effectiveAggroRange = aggroRange;
            
            if (isStaying && !wasRecentlyDamaged)
            {
                effectiveAggroRange = StayModeAggroRange;
                
                if (_targetCreature != null)
                {
                    float distToTarget = Vector3.Distance(transform.position, _targetCreature.transform.position);
                    if (distToTarget > StayModeAggroRange)
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionAI] {m_character?.m_name} is staying - disengaging from distant target {_targetCreature.m_name} ({distToTarget:F1}m)");
                        ClearTarget();
                        SetState(AIState.Idle);
                        return;
                    }
                }
            }

            // A target that stopped being one (PvP turned off for both owners, it joined the party, it was tamed) is dropped
            // here: only new candidates were validated, and a live current target only lost to a far better score, so after
            // companion_test's pvp step Fire's companions kept chasing the bot's healer through the pve fight (R60).
            if (_targetCreature != null && !_targetCreature.IsDead() && !StillHostile(_targetCreature))
            {
                Debug.Log($"[CompanionAI] {m_character?.m_name} dropped target {_targetCreature.m_name}: no longer hostile (PvP off, party or tame)");
                ClearTarget();
            }

            Character ownerCharacter = null;
            Vector3 ownerPos = transform.position;

            if (_followTarget != null)
            {
                ownerPos = _followTarget.transform.position;
                ownerCharacter = _followTarget.GetComponent<Character>();
            }

            Character bestTarget = null;
            float bestScore = float.MaxValue;

            Character.GetCharactersInRange(transform.position, effectiveAggroRange, _tempCharacterList);

            foreach (var character in _tempCharacterList)
            {
                if (!IsValidTarget(character))
                    continue;

                // In Follow mode an aggressive enemy is only a target when it actually threatens the owner or this
                // companion, or companions chase every hostile in range. Passives have their own hunting gate, Stay
                // mode defends its smaller bubble, and hostile PvP targets are always engaged.
                if (!isStaying && !IsPassiveCreature(character) && !IsHostilePvpTarget(character))
                {
                    if (!IsThreatToOwnerOrSelf(character, ownerPos, ownerCharacter))
                        continue;
                }

                float score = CalculateTargetScore(character, ownerCharacter, ownerPos);
                
                if (score < bestScore)
                {
                    bestScore = score;
                    bestTarget = character;
                }
            }

            _tempCharacterList.Clear();

            // Assist: the foe the owner (a player or the bot) hit in the last AssistSeconds is ours too, unless something is hitting
            // us right now (R67 pve: the bot fought the pack and its companions engaged nothing).
            if (!isStaying && ownerCharacter is Player ownerPlayer && OwnerAssist.Recent(ownerPlayer, AssistSeconds, out Character ownerFoe)
                && ownerFoe != _targetCreature && IsValidTarget(ownerFoe)
                && Vector3.Distance(transform.position, ownerFoe.transform.position) <= effectiveAggroRange * 2f
                && (_targetCreature == null || _targetCreature.IsDead() || !wasRecentlyDamaged))
            {
                if (_assistLogged != ownerFoe)
                {
                    _assistLogged = ownerFoe;
                    Debug.Log($"[CompanionAI] assist: {m_character?.m_name} engages {ownerFoe.m_name} (owner attacked it)");
                }
                SetTarget(ownerFoe);
                return;
            }

            if (bestTarget != null)
            {
                if (_targetCreature == null || _targetCreature.IsDead())
                {
                    SetTarget(bestTarget);
                }
                else if (bestTarget != _targetCreature)
                {
                    ConsiderTargetSwitch(bestTarget, false);
                }
            }
        }

        // The score lives in CompanionBrain (one brain for companions and the FDT bot); this supplies the companion's own inputs,
        // including the group's assigned focus-fire target.
        private float CalculateTargetScore(Character target, Character ownerCharacter, Vector3 ownerPos)
        {
            Character assigned = null;
            if (_companion != null)
            {
                var coordinator = GroupCombatCoordinator.Instance;
                var threatTable = coordinator != null ? coordinator.GetThreatTable(_companion.ownerPlayerId) : null;
                if (threatTable != null) assigned = threatTable.GetAssignedTarget(_companion.companionId);
            }
            return CompanionBrain.ScoreTarget(m_character, target, ownerCharacter, ownerPos, attackRange, interceptPriority,
                _threatAnalyzer, assigned);
        }

        private void ConsiderTargetSwitch(Character newTarget, bool wasDamagedByTarget)
        {
            if (newTarget == _targetCreature)
                return;

            if (!wasDamagedByTarget && Time.time - _lastTargetSwitchTime < targetSwitchCooldown)
                return;

            if (wasDamagedByTarget)
            {
                float distToCurrent = _targetCreature != null ? 
                    Vector3.Distance(transform.position, _targetCreature.transform.position) : float.MaxValue;
                float distToNew = Vector3.Distance(transform.position, newTarget.transform.position);

                if (distToNew < distToCurrent * 0.7f || distToCurrent > attackRange * 3f)
                {
                    SetTarget(newTarget);
                }
                return;
            }

            if (_targetCreature != null && !_targetCreature.IsDead())
            {
                var owner = _companion?.GetOwner();
                Vector3 ownerPos = owner != null ? owner.transform.position : transform.position;
                Character ownerChar = owner?.GetComponent<Character>();

                float currentScore = CalculateTargetScore(_targetCreature, ownerChar, ownerPos);
                float newScore = CalculateTargetScore(newTarget, ownerChar, ownerPos);

                // Require a significant score difference to switch targets.
                // This prevents oscillation between two similarly-scored enemies (e.g., two trolls).
                if (newScore < currentScore - 50f)
                {
                    SetTarget(newTarget);
                }
            }
            else
            {
                SetTarget(newTarget);
            }
        }

        private bool FindNewTarget()
        {
            Character.GetCharactersInRange(transform.position, aggroRange, _tempCharacterList);

            Character bestTarget = null;
            float bestDist = float.MaxValue;

            foreach (var character in _tempCharacterList)
            {
                if (!IsValidTarget(character))
                    continue;
                // Only what it sees or hears (Perception: the same senses the FDT bot uses; Fire 2026-09-29 "a proper vision cone").
                if (!Perceives(character))
                    continue;

                float dist = Vector3.Distance(transform.position, character.transform.position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestTarget = character;
                }
            }

            _tempCharacterList.Clear();

            if (bestTarget != null)
            {
                SetTarget(bestTarget);
                return true;
            }

            return false;
        }

        // Seen in the view cone (any direction while in a fight or hit lately, like an alerted monster) with line of sight, heard, or
        // sensed within the last few seconds.
        private bool Perceives(Character target)
        {
            if (m_character == null) return true;
            bool alerted = IsAlerted() || _targetCreature != null
                           || Time.time - _lastDirectlyDamagedTime < DirectDamageAlertDuration;
            Vector3 eye = m_character.m_eye != null ? m_character.m_eye.position : m_character.GetCenterPoint();
            return Perception.Knows(m_character, target, eye, transform.forward, Senses.Of(this), alerted);
        }

        // Only the relationship is re-checked for a CURRENT target (the hunting, proximity and line-of-sight gates of
        // IsValidTarget are for picking one): a player, tame or companion stays a target while it is a hostile PvP target, or, for
        // a wild companion, a player it is an enemy of. Monsters always stay.
        private bool StillHostile(Character target)
        {
            bool owned = target.IsPlayer() || target.IsTamed() || target.GetComponent<CompanionController>() != null;
            if (!owned) return true;
            if (IsHostilePvpTarget(target)) return true;
            return target.IsPlayer() && _companion != null && !_companion.isTamed && IsEnemy(target);
        }

        private bool IsValidTarget(Character target)
        {
            if (target == null || target.IsDead())
                return false;
            if (target == m_character)
                return false;
            // Vanilla AI never picks these (BaseAI.FindEnemy, BaseAI.cs:1046), e.g. the 1.0 ShadowPerson.
            if (target.m_aiSkipTarget)
                return false;

            // PvP companion battles: a non-allied owner's companion or player — both sides PvP-enabled —
            // is a valid target, bypassing the never-attack-player / never-attack-tamed / same-faction
            // (Dverger) gates below. Opt-in: nothing fires unless both owners enabled PvP.
            if (IsHostilePvpTarget(target))
                return true;

            if (target.IsPlayer())
            {
                // Tamed companions never attack players.
                // Untamed (wild) companions CAN target a player who is their
                // faction enemy — this is what makes them fight back when attacked.
                bool wildEnemy = _companion != null && !_companion.isTamed && IsEnemy(target);
                if (!wildEnemy) return false;
                // Fall through: wild companion vs enemy player is a valid target.
            }
            if (target.IsTamed())
                return false;
            if (!IsEnemy(target))
                return false;

            if (IsPassiveCreature(target))
            {
                // Owner controls whether this companion proactively hunts neutral
                // wildlife (deer / boar / neck / etc.) via the radial "Hunt" toggle.
                // When hunting is OFF (the default) the companion will only engage a
                // passive creature if it was recently *directly* attacked by it —
                // i.e. the wildlife became aggressive and is actively hitting us.
                bool huntingEnabled = CompanionBehaviorToggles.IsHuntingEnabled(_companion);
                if (!huntingEnabled)
                {
                    if (Time.time - _lastDirectlyDamagedTime > DirectDamageAlertDuration)
                        return false;
                    // Even when recently damaged, only retaliate against the actual
                    // creature that hit us — not every passive in the area.
                    // (We use proximity as a cheap proxy here; the precise attacker
                    // is tracked by OnDamaged -> ConsiderTargetSwitch.)
                    float distToHostilePassive = Vector3.Distance(transform.position, target.transform.position);
                    if (distToHostilePassive > 8f)
                        return false;
                }
            }

            // RANGED LINE-OF-SIGHT FILTER (cheap)
            // For ranged-weapon companions we keep a transient blacklist of
            // targets that the bow/crossbow behavior has already determined it
            // can't hit (no LOS, can't reposition).  We do NOT raycast here —
            // doing so per-target per-tick was a measurable perf hit in open
            // areas with many enemies.  Active-target LOS is checked once per
            // engagement decision in the weapon behavior itself.
            if (_combatRef != null && _combatRef.IsRangedWeapon()
                && _losBlacklist != null && _losBlacklist.Count > 0)
            {
                if (_losBlacklist.TryGetValue(target.GetInstanceID(), out float expireTime))
                {
                    if (Time.time < expireTime)
                        return false;
                    // expired — clean up lazily
                    _losBlacklist.Remove(target.GetInstanceID());
                }
            }

            return true;
        }

        /// <summary>
        /// True when <paramref name="target"/> belongs to a DIFFERENT, non-allied owner and BOTH sides
        /// have PvP enabled — a legitimate player-vs-player companion-battle target. This is the only
        /// path by which a tamed companion engages another player's companion (or that player directly),
        /// since they share the Dverger faction and are flagged tamed. Opt-in by design: nothing fires
        /// unless both owners have toggled PvP on, and allied owners (party/guild hook) never qualify.
        /// </summary>
        private bool IsHostilePvpTarget(Character target)
        {
            if (target == null) return false;
            if (_companion == null || !_companion.isTamed || _companion.ownerPlayerId == 0L) return false;

            var myOwner = Player.GetPlayer(_companion.ownerPlayerId);
            if (myOwner == null || !myOwner.IsPVPEnabled()) return false; // our side isn't in PvP → never

            long targetOwnerId;
            if (target.IsPlayer())
            {
                var targetPlayer = target as Player;
                if (targetPlayer == null || !targetPlayer.IsPVPEnabled()) return false;
                targetOwnerId = targetPlayer.GetPlayerID();
            }
            else
            {
                var targetComp = target.GetComponent<CompanionController>();
                if (targetComp == null || !targetComp.isTamed || targetComp.ownerPlayerId == 0L) return false;
                targetOwnerId = targetComp.ownerPlayerId;
                var targetOwner = Player.GetPlayer(targetOwnerId);
                if (targetOwner == null || !targetOwner.IsPVPEnabled()) return false;
            }

            // Same owner or allied side → not a target.
            return !FiresCore.Bridge.NpcCompanionBridge.AreOwnersAllied(_companion.ownerPlayerId, targetOwnerId);
        }

        /// <summary>
        /// The owner-AFK stand-down, only when nothing threatens us. R45: an owner standing still (the bot in its duel, both
        /// owners in the pve step) made every companion pick a target and drop it the same tick ("relaxing - player idle"), so
        /// an owner who stood and fought fought alone. Not while this companion was hit lately, while the target is a PvP
        /// enemy, is after this companion or a party member, or is inside the owner's defense bubble.
        /// </summary>
        public bool ShouldRelaxForIdleOwner(Character target)
        {
            if (!_isOwnerIdle) return false;
            if (Time.time - _lastDirectlyDamagedTime < DirectDamageAlertDuration) return false;

            var owner = _followTarget;
            Character ownerCharacter = owner != null ? owner.GetComponent<Character>() : null;
            Vector3 ownerPos = owner != null ? owner.transform.position : transform.position;
            if (target != null && !target.IsDead())
            {
                if (IsHostilePvpTarget(target)) return false;
                var aiTarget = target.GetBaseAI()?.GetTargetCreature();
                if (aiTarget != null && (aiTarget == m_character || FiresCore.Npc.Archetypes.ClassTargeting.IsPartyMember(m_character, aiTarget)))
                    return false;
                // R46: the engage decision took a monster after any nearby player as a threat and this didn't, so the companion
                // engaged and stood down every tick. A target it would engage is one it keeps.
                if (IsThreatToOwnerOrSelf(target, ownerPos, ownerCharacter)) return false;
            }

            // The lead / Fire: a guard standing still while monsters fight nearby keeps its companions in the fight.
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == null || other == m_character || other.IsDead() || IsPassiveCreature(other)) continue;
                if (!BaseAI.IsEnemy(m_character, other)) continue;
                if (Vector3.Distance(other.transform.position, transform.position) <= OwnerDefenseRadius) return false;
                if (Vector3.Distance(other.transform.position, ownerPos) <= OwnerDefenseRadius) return false;
            }
            return true;
        }

        /// <summary>
        /// In Follow mode, engages a hostile only if it is within <see cref="OwnerDefenseRadius"/> of the owner,
        /// is already targeting the owner, a player or this companion, or this companion was hit within
        /// <see cref="DirectDamageAlertDuration"/>. Explicit attack commands use ForceTarget and bypass this.
        /// </summary>
        // The rule lives in CompanionBrain (one brain for companions and the FDT bot); this supplies the companion's own inputs.
        private bool IsThreatToOwnerOrSelf(Character target, Vector3 ownerPos, Character ownerCharacter)
            => CompanionBrain.IsThreat(m_character, target, ownerPos, ownerCharacter, OwnerDefenseRadius, combatLeashDistance,
                Time.time - _lastDirectlyDamagedTime < DirectDamageAlertDuration);

        // Ranged LOS support
        // Targets that the bow/crossbow behavior has tried to engage but gave
        // up on (no LOS even after attempting to reposition) are stored here
        // with an expiry time, so IsValidTarget skips them without paying the
        // cost of a raycast every detection tick.
        private Dictionary<int, float> _losBlacklist;
        private const float LosBlacklistDuration = 8f; // seconds before the same target can be retried

        /// <summary>
        /// Called by ranged weapon behaviors after they've concluded they cannot
        /// hit a target (no LOS, repositioning failed).  The target is excluded
        /// from this companion's target acquisition for a short window so we
        /// don't immediately reacquire it and lock the AI in an unhittable
        /// engagement loop.
        /// </summary>
        public void BlacklistTargetForLineOfSight(Character target)
        {
            if (target == null) return;
            if (_losBlacklist == null) _losBlacklist = new Dictionary<int, float>();
            _losBlacklist[target.GetInstanceID()] = Time.time + LosBlacklistDuration;

            // If this is our current target, drop it so re-acquisition runs.
            if (_targetCreature == target)
            {
                ClearTarget();
            }
        }

        /// <summary>
        /// Cheap line-of-sight check from the companion's chest to the target's
        /// chest.  Used by ranged behaviors at engagement-decision time, NOT in
        /// the per-target IsValidTarget loop.
        /// </summary>
        public bool HasClearShotTo(Character target)
        {
            if (target == null) return false;
            // Adjacent — no realistic obstruction possible.
            if (Vector3.Distance(transform.position, target.transform.position) < 2f) return true;

            // One brain with the FDT bot (LineOfFire, Fire: "they need to understand line of sight"): a bow / crossbow traces its
            // real arrow; anything else the straight line from the eye. Solids and friendly characters block; enemies don't.
            string blocker;
            bool clear;
            var humanoid = m_character as Humanoid;
            ItemDrop.ItemData weapon = humanoid != null ? humanoid.GetCurrentWeapon() : null;
            var skill = weapon?.m_shared?.m_skillType;
            if (humanoid != null && (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows))
            {
                LaneBlock lane = LineOfFire.Shot(humanoid, weapon, target);
                clear = !lane.Blocked;
                blocker = lane.Blocker;
            }
            else clear = LineOfFire.Clear(m_character, transform.position + Vector3.up * 1.5f, target, out blocker);

            if (!clear && blocker != _laneBlockerLogged)
            {
                _laneBlockerLogged = blocker;
                Debug.Log($"[CompanionAI] {m_character?.m_name}: lane to {target.m_name} blocked by {blocker}; not shooting");
            }
            else if (clear) _laneBlockerLogged = null;
            return clear;
        }

        private string _laneBlockerLogged;
        private Character _assistLogged;
        // How long after the owner hits a foe its companions still join in.
        private const float AssistSeconds = 5f;

        /// <summary>
        /// Public probe used by ranged weapon behaviors to abort an in-progress
        /// shot when the current target ducks behind cover mid-draw.
        /// </summary>
        public bool HasClearShotToCurrentTarget()
        {
            if (_targetCreature == null || _targetCreature.IsDead()) return false;
            return HasClearShotTo(_targetCreature);
        }
        
        private bool IsPassiveCreature(Character target)
        {
            if (target == null) return false;
            
            string creatureName = target.m_name?.ToLower() ?? "";
            string prefabName = "";
            
            var nview = target.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                var zdo = nview.GetZDO();
                if (zdo != null)
                {
                    var prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
                    if (prefab != null)
                    {
                        prefabName = prefab.name.ToLower();
                    }
                }
            }
            
            // Configurable hunt list (FiresCore.Npc.HuntListConfig) — server-synced, admin-editable,
            // defaults to the vanilla prey set (boar/deer/hare/neck/…). Replaces the old hardcoded array.
            if (HuntListConfig.IsHuntable(creatureName, prefabName))
            {
                return true;
            }

            // Vanilla prey-animal faction catches anything tagged AnimalsVeg even
            // when the prefab name doesn't match our keyword list (covers most
            // modded wildlife that uses the standard faction).
            if (target.m_faction == Character.Faction.AnimalsVeg)
            {
                return true;
            }

            // Flee-when-not-alerted monsters (passive until provoked) count as passive prey. m_fleeIfNotAlerted
            // is public on MonsterAI (publicized assembly) — direct read, no per-call reflection in the 0.3s scan.
            var monsterAI = target.GetComponent<MonsterAI>();
            if (monsterAI != null && monsterAI.m_fleeIfNotAlerted)
                return true;

            return false;
        }

        private void SetTarget(Character target)
        {
            _targetCreature = target;
            _lastTargetSwitchTime = Time.time;
            _timeSinceTargetSeen = 0f;
            _timeSinceAttacking = 0f;
            _beenAtLastTargetPos = false;

            if (target != null)
            {
                _lastKnownTargetPos = target.transform.position;
                SetAlerted(true);

                if (VerboseLogging)
                    Debug.Log($"[CompanionAI] Set target: {target.m_name}");
            }
        }

        private void ClearTarget()
        {
            _targetCreature = null;
            _beenAtLastTargetPos = false;
            ClearAlertedState();
        }

        /// <summary>
        /// Public hook for breaking combat externally (e.g. when the owner is too far
        /// away and the companion is being teleported back).
        /// </summary>
        public void ForceClearTarget()
        {
            ClearTarget();
        }

        /// <summary>
        /// Called after a long-distance teleport (portal, dungeon entry, owner respawn). Drops the stale target and
        /// alert memory and returns to Following or Idle, so a companion that was fighting outside a dungeon engages
        /// what is actually around it.
        /// </summary>
        public void OnTeleportedFar()
        {
            ClearTarget();

            // Reset perception breadcrumbs that are tied to the previous environment.
            _beenAtLastTargetPos = false;
            _timeSinceAttacking = 0f;

            // Begin the post-teleport combat suppression window (see field doc on
            // _combatSuppressedUntilTime in CompanionAI.cs).  Three seconds is
            // long enough for the companion to actually walk back to the owner
            // and stop animating "I want to run off and fight that thing", and
            // short enough that real local threats are picked up promptly once
            // it expires.
            _combatSuppressedUntilTime = Time.time + PostTeleportCombatSuppressSeconds;

            // Force the FSM out of any combat-related state.  If we were following,
            // resume following so the AI starts pulling toward the owner immediately;
            // otherwise drop to Idle so wander/idle scans pick up local threats.
            var resetTo = _shouldFollow ? AIState.Following : AIState.Idle;
            if (_currentState == AIState.Combat ||
                _currentState == AIState.Returning ||
                _currentState == AIState.Fleeing)
            {
                SetState(resetTo);
            }
        }

        /// <summary>
        /// True while the post-teleport combat suppression window is active.
        /// Other systems can read this if they need to participate in the
        /// "calm walk back to owner" behaviour (e.g. weapon-swap subsystems
        /// that should not unsheathe a combat weapon during the window).
        /// </summary>
        public bool IsPostTeleportCombatSuppressed => Time.time < _combatSuppressedUntilTime;

        #endregion
    }
}
