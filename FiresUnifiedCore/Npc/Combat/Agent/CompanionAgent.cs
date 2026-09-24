using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc.AI;
using FiresCore.Npc.Archetypes;

namespace FiresCore.Npc.Combat.Agent
{
    /// <summary>
    /// ICombatAgent over a companion body. Every movement call routes through CompanionAI so the single-writer
    /// movement authority still decides, and nothing here changes what the companion behaviours already do.
    /// </summary>
    public class CompanionAgent : MonoBehaviour, ICombatAgent
    {
        private const string MovementAuthorityOwner = "CombatAgent";
        private const float AllyScanPadding = 1f;
        private const float FacingHoldSeconds = 0.4f;

        private CompanionController _companion;
        private CompanionAI _ai;
        private CompanionCombat _combat;
        private Humanoid _humanoid;
        private Character _character;
        private ZNetView _nview;
        private Animator _animator;

        private CombatAgentCapability _capabilities;
        private bool _capabilitiesProbed;
        private CompanionStaminaAdapter _stamina;
        private CompanionSkillsAdapter _skills;

        private static readonly List<Character> ScanBuffer = new List<Character>();

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _ai = GetComponent<CompanionAI>();
            _combat = GetComponent<CompanionCombat>();
            _humanoid = GetComponent<Humanoid>();
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
            _animator = GetComponentInChildren<Animator>();

            if (_character == null)
            {
                Debug.LogError($"[CompanionAgent] {gameObject.name} has no Character - a combat agent cannot drive it.");
            }
        }

        public Character Body => _character;
        public Humanoid Humanoid => _humanoid;
        public BaseAI Ai => _ai;
        public ZNetView NView => _nview;
        public Transform Transform => transform;

        public Vector3 Position => transform.position;
        public Vector3 EyePosition => _character != null ? _character.GetEyePoint() : transform.position;
        public float Radius => _character != null ? _character.GetRadius() : 0.5f;

        public Character.Faction Faction => _character != null ? _character.GetFaction() : Character.Faction.Players;
        public string FactionGroup => _character != null ? _character.m_group : string.Empty;

        public bool IsOwner => _nview != null && _nview.IsValid() && _nview.IsOwner();
        public bool IsAlive => _character != null && !_character.IsDead() && (_companion == null || !_companion.isDefeated);
        public float HealthFraction => _character != null ? _character.GetHealthPercentage() : 0f;
        public bool IsStaggering => _character != null && _character.IsStaggering();

        public CombatAgentCapability Capabilities
        {
            get
            {
                if (!_capabilitiesProbed)
                {
                    _capabilities = AnimatorCapabilityProbe.Probe(_character, _animator)
                        | CombatAgentCapability.Cast
                        | CombatAgentCapability.Formation
                        | CombatAgentCapability.Consumables;
                    _capabilitiesProbed = true;
                }
                return _capabilities;
            }
        }

        public bool Can(CombatAgentCapability capability) => (Capabilities & capability) == capability;

        public Character Target => _ai != null ? _ai.GetTargetCreature() : null;

        public bool TrySetTarget(Character target)
        {
            if (_ai == null) return false;
            if (target == null)
            {
                _ai.ClearForceTarget();
                return true;
            }
            _ai.ForceTarget(target);
            return true;
        }

        public bool MoveToward(Vector3 point, bool run, float reachDistance)
        {
            if (_ai == null) return true;
            return _ai.RequestPathfindingMovement(point, run, reachDistance,
                FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.SubBehavior, MovementAuthorityOwner);
        }

        public void LookToward(Vector3 point)
        {
            Vector3 direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) return;
            direction.Normalize();

            var facing = _companion != null ? _companion.GetFacingAuthority() : null;
            if (facing != null)
            {
                if (facing.TryAcquireFacing(FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.SubBehavior,
                        MovementAuthorityOwner, FacingHoldSeconds))
                {
                    facing.SetLookDirection(MovementAuthorityOwner, direction);
                }
                return;
            }
            _ai?.LookTowards(direction);
        }

        public void StopMoving()
        {
            if (_ai == null) return;
            _ai.ReleasePathfindingMovement(MovementAuthorityOwner);
        }

        public bool IsAttacking => _character != null && _character.InAttack();

        public bool TryAttack(Character target)
        {
            if (_humanoid == null || target == null) return false;
            if (_humanoid.GetCurrentWeapon() == null) return false;
            return _humanoid.StartAttack(target, false);
        }

        public bool ForceAttackNow()
        {
            if (_combat == null) return false;
            _combat.ForceAttack();
            return true;
        }

        public ICombatStamina Stamina
        {
            get
            {
                var stats = _companion != null ? _companion.GetStats() : null;
                if (stats == null) return null;
                if (_stamina == null || !_stamina.Wraps(stats)) _stamina = new CompanionStaminaAdapter(stats);
                return _stamina;
            }
        }

        public ICombatSkills Skills
        {
            get
            {
                var skills = _companion != null ? _companion.GetSkills() : null;
                if (skills == null) return null;
                if (_skills == null || !_skills.Wraps(skills)) _skills = new CompanionSkillsAdapter(skills);
                return _skills;
            }
        }

        public bool HasFacingAuthority => _companion != null && _companion.GetFacingAuthority() != null;

        public bool TryFaceThrough(string owner, Vector3 point, CombatFacingPriority priority, float holdSeconds)
        {
            var facing = _companion != null ? _companion.GetFacingAuthority() : null;
            if (facing == null) return false;
            if (!facing.TryAcquireFacing(SourceFor(priority), owner, holdSeconds)) return false;
            facing.SetLookTarget(owner, point);
            return true;
        }

        private static FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource SourceFor(CombatFacingPriority priority)
        {
            return priority == CombatFacingPriority.Animation
                ? FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.Animation
                : FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.SubBehavior;
        }

        public bool HasClearShotTo(Character target) => _ai != null && _ai.HasClearShotTo(target);

        public bool HasClearShotToCurrentTarget() => _ai != null && _ai.HasClearShotToCurrentTarget();

        public void BlacklistTargetForLineOfSight(Character target) => _ai?.BlacklistTargetForLineOfSight(target);

        public bool IsBlocking => _character != null && _character.IsBlocking();

        public bool TryBlock()
        {
            if (!Can(CombatAgentCapability.Block)) return false;
            var blocking = _combat != null ? _combat.GetBlockingBehavior() : null;
            if (blocking == null) return false;
            blocking.StartBlocking();
            return true;
        }

        public void ReleaseBlock()
        {
            var blocking = _combat != null ? _combat.GetBlockingBehavior() : null;
            blocking?.StopBlocking();
        }

        public bool TryDodgeAwayFrom(Character threat)
        {
            if (threat == null) return false;
            if (!Can(CombatAgentCapability.DodgeRoll)) return false;
            var dodge = _combat != null ? _combat.GetDodgeBehavior() : null;
            if (dodge == null || dodge.IsDodging) return false;
            dodge.ExecuteDodge(threat);
            return true;
        }

        public bool TryCast(string statusEffectName, Character target, float duration)
        {
            if (_character == null || string.IsNullOrEmpty(statusEffectName)) return false;
            if (target == null || target == _character)
            {
                return AbilityRPCManager.ApplySelfBuff(_character, statusEffectName, duration);
            }
            return AbilityRPCManager.ApplySingleEffect(_character, target, statusEffectName, duration);
        }

        public int CollectAllies(float range, List<Character> into)
        {
            if (into == null) return 0;
            into.Clear();
            if (_character == null) return 0;

            ScanBuffer.Clear();
            Character.GetCharactersInRange(transform.position, range + AllyScanPadding, ScanBuffer);

            int added = 0;
            for (int index = 0; index < ScanBuffer.Count; index++)
            {
                var candidate = ScanBuffer[index];
                if (candidate == null || candidate == _character || candidate.IsDead()) continue;
                if (IsEnemy(candidate)) continue;
                into.Add(candidate);
                added++;
            }
            ScanBuffer.Clear();
            return added;
        }

        public bool IsEnemy(Character other)
        {
            if (_character == null || other == null) return false;
            return BaseAI.IsEnemy(_character, other);
        }

        public void Stagger(Vector3 forceDirection)
        {
            _character?.Stagger(forceDirection);
        }
    }
}
