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
            if (Time.time - _lastTargetScanTime < TARGET_SCAN_INTERVAL)
                return;
            _lastTargetScanTime = Time.time;

            // ABSOLUTE-PRIORITY PLAYER COMMAND
            // The owner has explicitly told this companion to do something — Move
            // to a position, Attack a target, etc.  While that command is active
            // the companion's behaviour MUST be exactly that command and nothing
            // else; any "I see something nearby, let me run off and fight it"
            // impulse is the bug the owner explicitly told us to remove.
            //
            // We bail before the threat scan even runs.  UpdateCombatState already
            // bails the same way (CompanionAI.Combat.cs around line 34) so the
            // FSM never enters Combat while a command is up — but without this
            // gate, target detection kept firing every TARGET_SCAN_INTERVAL,
            // re-acquiring a _targetCreature that UpdateCombatState then had to
            // clear again.  Two systems fighting each other to a draw, every
            // frame, is the exact thing the owner just said to stop doing.
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
            bool wasRecentlyDamaged = Time.time - _lastDirectlyDamagedTime < DIRECT_DAMAGE_ALERT_DURATION;
            float effectiveAggroRange = aggroRange;
            
            if (isStaying && !wasRecentlyDamaged)
            {
                effectiveAggroRange = STAY_MODE_AGGRO_RANGE;
                
                if (_targetCreature != null)
                {
                    float distToTarget = Vector3.Distance(transform.position, _targetCreature.transform.position);
                    if (distToTarget > STAY_MODE_AGGRO_RANGE)
                    {
                        if (VerboseLogging)
                            Debug.Log($"[CompanionAI] {m_character?.m_name} is staying - disengaging from distant target {_targetCreature.m_name} ({distToTarget:F1}m)");
                        ClearTarget();
                        SetState(AIState.Idle);
                        return;
                    }
                }
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

                // OWNER-DEFENSE GATE
                // ------------------
                // For aggressive (non-passive) enemies in Follow mode, only
                // pick this character as a target if it is *actually*
                // threatening the owner or this companion. Without this
                // gate, companions in Follow mode would engage every
                // hostile in aggroRange the moment they spotted it — the
                // "chase everything in the forest" behaviour the player
                // reported after the flee-suppression fix.
                //
                // Passives (boar / deer / neck / etc.) already have their
                // own gate inside IsValidTarget that respects the per-
                // companion Hunting toggle. Stay mode uses the much smaller
                // STAY_MODE_AGGRO_RANGE bubble and skips this gate so a
                // staying companion still defends its post.
                // Hostile PvP targets (another player's PvP-enabled companion/player) bypass the
                // owner-defense gate so companions actively engage enemy squads instead of only
                // defending the owner's bubble.
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

        private float CalculateTargetScore(Character target, Character ownerCharacter, Vector3 ownerPos)
        {
            Vector3 targetPos = target.transform.position;
            float distToMe = Vector3.Distance(transform.position, targetPos);
            float distToOwner = Vector3.Distance(ownerPos, targetPos);

            float score = distToOwner;

            // GROUP COMBAT COORDINATION: If the SharedThreatTable has assigned us a specific target,
            // strongly prefer that target to maintain focus fire / coordinated engagement.
            // This does NOT override the existing scoring entirely — it just adds a large bonus
            // so the assigned target is almost always chosen unless something very urgent happens
            // (e.g., an enemy is attacking the player at point blank range).
            if (_companion != null)
            {
                var coordinator = GroupCombatCoordinator.Instance;
                if (coordinator != null)
                {
                    var threatTable = coordinator.GetThreatTable(_companion.ownerPlayerId);
                    if (threatTable != null)
                    {
                        var assignedTarget = threatTable.GetAssignedTarget(_companion.companionId);
                        if (assignedTarget != null && assignedTarget == target)
                        {
                            // This is our assigned target — strong preference
                            score -= 200f;
                        }
                    }
                }
            }

            if (_threatAnalyzer != null)
            {
                var profile = _threatAnalyzer.GetThreatProfile(target);
                
                switch (profile.Classification)
                {
                    case ThreatAnalyzer.EnemyClass.Boss:
                        score -= 80f;
                        break;
                    case ThreatAnalyzer.EnemyClass.Elite:
                        score -= 50f;
                        break;
                    case ThreatAnalyzer.EnemyClass.Dangerous:
                        score -= 30f;
                        break;
                    case ThreatAnalyzer.EnemyClass.Normal:
                        score -= 10f;
                        break;
                }
                
                if (profile.IsTargetingOwner)
                {
                    score -= 100f * interceptPriority;
                }
                else if (profile.IsTargetingCompanion)
                {
                    score -= 50f;
                }
                
                if (profile.IsCurrentlyAttacking)
                {
                    score -= 25f;
                }
                
                if (profile.HealthPercent < 0.25f)
                {
                    score -= 15f;
                }
            }
            else
            {
                if (ownerCharacter != null)
                {
                    var targetAI = target.GetComponent<BaseAI>();
                    if (targetAI != null)
                    {
                        var aiTarget = targetAI.GetTargetCreature();
                        if (aiTarget == ownerCharacter)
                        {
                            score -= 100f * interceptPriority;
                        }
                        else if (aiTarget == m_character)
                        {
                            score -= 50f;
                        }
                    }
                }

                if (distToOwner < 10f)
                {
                    score -= 30f;
                }
            }

            if (distToMe < attackRange * 2f)
            {
                score -= 20f;
            }

            return score;
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

        private bool IsValidTarget(Character target)
        {
            if (target == null || target.IsDead())
                return false;
            if (target == m_character)
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
                    if (Time.time - _lastDirectlyDamagedTime > DIRECT_DAMAGE_ALERT_DURATION)
                        return false;
                    // Even when recently damaged, only retaliate against the actual
                    // creature that hit us — not every passive in the area.
                    // (We use proximity as a cheap proxy here; the precise attacker
                    // is tracked by OnDamaged ? ConsiderTargetSwitch.)
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
                var tp = target as Player;
                if (tp == null || !tp.IsPVPEnabled()) return false;
                targetOwnerId = tp.GetPlayerID();
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
        /// Owner-defense check used by Follow-mode targeting to decide
        /// whether an aggressive enemy is actually a threat we should
        /// engage, vs just a hostile creature that happens to be in our
        /// aggroRange.
        ///
        /// Returns true when AT LEAST ONE of:
        ///   - The target is within <see cref="OWNER_DEFENSE_RADIUS"/> of
        ///     the owner (defending the owner's personal space).
        ///   - The target's BaseAI is actively targeting the owner, any
        ///     player, or this companion (real combat already engaged).
        ///   - This companion was directly damaged in the last
        ///     <see cref="DIRECT_DAMAGE_ALERT_DURATION"/> seconds
        ///     (someone is hitting us — retaliate).
        ///
        /// Otherwise returns false and the caller skips this target.
        ///
        /// Note: explicit owner-issued attack commands go through
        /// <c>ForceTarget</c>, which bypasses the detection scan entirely,
        /// so this gate never blocks a player-directed attack.
        /// </summary>
        private bool IsThreatToOwnerOrSelf(Character target, Vector3 ownerPos, Character ownerCharacter)
        {
            if (target == null) return false;

            // 1. Threat is inside the owner's defense bubble.
            float distToOwner = Vector3.Distance(target.transform.position, ownerPos);
            if (distToOwner <= OWNER_DEFENSE_RADIUS)
                return true;

            // 2. The threat's AI is locked on to the owner / a player /
            //    this companion already.
            var targetAI = target.GetComponent<BaseAI>();
            if (targetAI != null)
            {
                var aiTarget = targetAI.GetTargetCreature();
                if (aiTarget != null)
                {
                    if (aiTarget == m_character) return true;
                    if (ownerCharacter != null && aiTarget == ownerCharacter) return true;
                    // A monster locked onto some OTHER player only pulls us in while the fight is near
                    // OUR owner — unbounded, every skirmish inside aggro range peeled the escort away
                    // ("run off at anything that noticed a player"). Bound it by the combat leash so
                    // companions prioritize fighting with and around their owner.
                    if (aiTarget.IsPlayer() && distToOwner <= combatLeashDistance) return true;
                }
            }

            // 3. We were directly attacked recently — retaliate even if the
            //    attacker has since broken aggro and isn't currently
            //    targeting us. Mirrors the existing retaliation window
            //    that's already used for passive creatures.
            if (Time.time - _lastDirectlyDamagedTime < DIRECT_DAMAGE_ALERT_DURATION)
                return true;

            return false;
        }

        // ?? Ranged LOS support ??????????????????????????????????????????????
        // Targets that the bow/crossbow behavior has tried to engage but gave
        // up on (no LOS even after attempting to reposition) are stored here
        // with an expiry time, so IsValidTarget skips them without paying the
        // cost of a raycast every detection tick.
        private Dictionary<int, float> _losBlacklist;
        private const float LOS_BLACKLIST_DURATION = 8f; // seconds before the same target can be retried

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
            _losBlacklist[target.GetInstanceID()] = Time.time + LOS_BLACKLIST_DURATION;

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

            Vector3 eyePos = transform.position + Vector3.up * 1.5f;
            Vector3 targetPos = target.transform.position + Vector3.up * 1f;
            Vector3 direction = targetPos - eyePos;
            float distance = direction.magnitude;

            // Adjacent — no realistic obstruction possible, skip the raycast.
            if (distance < 2f) return true;

            if (Physics.Raycast(eyePos, direction.normalized, out RaycastHit hit, distance))
            {
                // Triggers (volumes, area-effects, etc.) never block sight.
                if (hit.collider.isTrigger) return true;

                // We hit the target itself or one of its child colliders ? clear.
                var hitChar = hit.collider.GetComponentInParent<Character>();
                if (hitChar == target) return true;

                // We hit something very close to the target (its collider edge,
                // a piece of armor, a mount, etc.) ? still effectively clear.
                if (Vector3.Distance(hit.point, targetPos) < 0.75f) return true;

                // Wall, terrain, or piece in the way.
                return false;
            }

            // Raycast hit nothing within distance — open path.
            return true;
        }

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
        /// Called by <see cref="CompanionController.TeleportToOwner"/> /
        /// <see cref="CompanionController.TeleportToDestination"/> after a long-distance
        /// teleport (portal jump, dungeon entry, owner respawn at a bed, etc).
        ///
        /// Without this, a companion who was mid-combat outside the dungeon will keep
        /// its <c>_targetCreature</c> reference pointing at the now-thousands-of-metres-
        /// away enemy, stay in <see cref="AIState.Combat"/>, and refuse to engage the
        /// new threats around it inside the dungeon — making them functionally useless
        /// in dungeon fights.  We:
        ///   1) drop the stale target reference,
        ///   2) clear alerted state and last-target-position memory,
        ///   3) snap the FSM back to Following (if shouldFollow) or Idle, so the next
        ///      target-acquisition pass scans the new surroundings.
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
            _combatSuppressedUntilTime = Time.time + POST_TELEPORT_COMBAT_SUPPRESS_SECONDS;

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
