using System.Collections;
using System.Collections.Generic;
using FiresCore.Npc.AI;
using FiresCore.Npc.Core;
using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Carries a companion's haul out (Core 0.2.220, Tools\COMPANION_HAUL.md): it asks the companion's
    /// <see cref="HaulJob"/> (FiresCore.Npc.AI.Haul, the same brain the FDT bot bodies run) for the next order and does it the
    /// companion way. Walks go through CompanionAI's pathfinding, swings reuse the gatherers' animation + delayed resource hit,
    /// pickables are picked as ResourceGatheringBehavior picks them, pieces go through ChoreBrain.PlacePiece and crafts through
    /// ResourceDataHelper.CraftTool. Never in the idle rotation: CompanionHaulRunner starts it as a command. It steps aside when a
    /// fight starts (the companion's combat code owns fights) and the runner starts it again once things are calm.
    /// </summary>
    public sealed class HaulBehavior : IdleSubBehavior
    {
        private const float ThinkSeconds = 0.1f;
        private const float SwingSeconds = 1.8f;
        private const float DamageDelay = 0.6f;
        private const float PickupSeconds = 0.25f;
        private const float InteractSeconds = 0.8f;
        private const float MoveReach = 1f;

        private readonly ResourceDataHelper.AttackChain _swingChain = new ResourceDataHelper.AttackChain();
        private readonly Dictionary<GameObject, ResourceDataHelper.ResourceData> _resources = new Dictionary<GameObject, ResourceDataHelper.ResourceData>();

        private Character _character;
        private Humanoid _humanoid;
        private Animator _animator;
        private ZSyncAnimation _zanim;
        private CompanionAutoPickup _autoPickup;

        private float _nextThink, _nextPickup, _busyUntil;
        private TaskOrder _order;

        /// <summary>The companion's haul, set by CompanionHaulRunner before each start.</summary>
        internal HaulJob Job { get; set; }
        /// <summary>The last run ended because a fight started, not because the job ended or someone cancelled it.</summary>
        internal bool SteppedAside { get; private set; }
        /// <summary>The last run was cancelled from outside (a player command, …) while the job was still running.</summary>
        internal bool CancelledFromOutside { get; private set; }

        public override string BehaviorName => "Haul";
        public override bool AvailableForIdleRotation => false;

        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            _character = companion.GetComponent<Character>();
            _humanoid = companion.GetComponent<Humanoid>();
            _animator = companion.GetComponentInChildren<Animator>(true);
            _zanim = companion.GetComponent<ZSyncAnimation>();
            _autoPickup = companion.GetComponent<CompanionAutoPickup>();
            // A haul runs until its quota or its end; the runner stops it, not a timer.
            MaxDuration = 24f * 3600f;
        }

        /// <summary>Set by CompanionHaulRunner just before it starts this; a staying companion's idle rotation may try any behaviour.</summary>
        internal bool StartedByRunner { get; set; }

        public override bool CanStart() => StartedByRunner && Job != null && Job.Active && Companion != null && _humanoid != null;

        public override void Start()
        {
            StartedByRunner = false;
            base.Start();
            SteppedAside = false;
            CancelledFromOutside = false;
            _order = null;
            _nextThink = 0f;
            _busyUntil = 0f;
            _resources.Clear();
        }

        public override void Cancel()
        {
            if (IsActive && Job != null && Job.Active && !SteppedAside) CancelledFromOutside = true;
            base.Cancel();
        }

        public override bool Update()
        {
            if (Job == null || !Job.Active) { Complete(); return true; }
            if (IsFighting())
            {
                SteppedAside = true;
                Complete();
                return true;
            }

            if (Time.time >= _nextPickup)
            {
                _nextPickup = Time.time + PickupSeconds;
                _autoPickup?.ForcePickupCheck();
            }
            if (Time.time < _busyUntil || (_character != null && _character.InAttack())) return false;

            if (_order == null || Time.time >= _nextThink)
            {
                _nextThink = Time.time + ThinkSeconds;
                _order = Job.Next();
            }
            return Carry(_order);
        }

        public override string GetStatusDescription() =>
            Job == null ? "Hauling" : $"Hauling ({Job.Progress.State}, trip {Job.Progress.Trips})";

        private bool IsFighting() =>
            (CompanionAI != null && CompanionAI.IsInCombat) || (Companion != null && Companion.IsInCombat);

        // One order, the companion way. True ends this run (the job is over).
        private bool Carry(TaskOrder order)
        {
            switch (order.Kind)
            {
                case TaskKind.Move:
                    Walk(order.Point, MoveReach);
                    return false;
                case TaskKind.Equip:
                    StopMovement();
                    (Job.Body as CompanionTaskBody)?.Equip(order.Item);
                    _order = null;
                    return false;
                case TaskKind.Swing:
                    StopMovement();
                    Face(order.Point);
                    Swing(order);
                    _order = null;
                    return false;
                case TaskKind.Interact:
                    StopMovement();
                    Face(order.Point);
                    Pick(order.Target);
                    _busyUntil = Time.time + InteractSeconds;
                    _order = null;
                    return false;
                case TaskKind.Build:
                    StopMovement();
                    Face(order.Point);
                    Place(order);
                    _busyUntil = Time.time + InteractSeconds;
                    _order = null;
                    return false;
                case TaskKind.Craft:
                    StopMovement();
                    if (order.Target != null) Face(order.Target.transform.position);
                    Craft(order);
                    _busyUntil = Time.time + InteractSeconds;
                    _order = null;
                    return false;
                default:
                    Complete();
                    return true;
            }
        }

        // TryMoveToPosition's authority handling with a closer reach: loose drops and chests want 1 m, not its 2 m.
        private void Walk(Vector3 point, float reach)
        {
            if (CompanionAI == null) { TryMoveToPosition(point, walk: false); return; }
            var source = IsCommandInitiated ? UnifiedMovementAuthority.MovementSource.PlayerCommand : UnifiedMovementAuthority.MovementSource.SubBehavior;
            if (MovementAuthority != null && !MovementAuthority.HasAuthority(BehaviorName) && !MovementAuthority.TryAcquireAuthority(source, BehaviorName, 5f))
                return;
            CompanionAI.RequestPathfindingMovement(point, false, reachDistance: reach, authoritySource: source, authorityOwner: BehaviorName);
        }

        private void Face(Vector3 point)
        {
            Vector3 dir = point - Transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            var facing = Companion != null ? Companion.GetFacingAuthority() : null;
            if (facing != null)
            {
                if (facing.TryAcquireFacing(UnifiedMovementAuthority.MovementSource.SubBehavior, BehaviorName, 0.4f))
                    facing.SetLookDirection(BehaviorName, dir);
                return;
            }
            Transform.rotation = Quaternion.LookRotation(dir.normalized);
        }

        // The gatherers' swing: the weapon's attack trigger now, the resource hit DamageDelay later.
        private void Swing(TaskOrder order)
        {
            ResourceDataHelper.ResourceData resource = Resource(order.Target);
            ItemDrop.ItemData tool = order.Item ?? (Job.Body as ITaskBodyHands)?.HeldItem;
            if (resource == null || resource.Destructible == null || tool == null) return;
            var targetType = resource.Destructible.GetDestructibleType();
            _swingChain.Swing(_zanim, _animator, tool, _character != null ? _character.GetTimeSinceLastAttack() : 1f, targetType);
            _busyUntil = Time.time + SwingSeconds;
            Companion.StartCoroutine(HitLater(resource, tool));
        }

        private IEnumerator HitLater(ResourceDataHelper.ResourceData resource, ItemDrop.ItemData tool)
        {
            yield return new WaitForSeconds(DamageDelay);
            if (resource.GameObject == null || resource.Destructible == null || Transform == null) yield break;

            Collider hitCollider = null;
            Vector3 hitPoint = resource.InteractionPosition;
            Vector3 origin = Transform.position + Vector3.up;
            Vector3 dir = (resource.InteractionPosition - origin).normalized;
            int mask = LayerMask.GetMask("piece", "Default", "static_solid", "Default_small", "terrain", "piece_nonsolid");
            foreach (RaycastHit hit in Physics.RaycastAll(origin, dir, Vector3.Distance(origin, resource.InteractionPosition) + 1f, mask))
            {
                if (hit.collider == null) continue;
                bool ours = hit.collider.transform == resource.GameObject.transform || hit.collider.transform.IsChildOf(resource.GameObject.transform)
                    || hit.collider.GetComponentInParent<IDestructible>() == resource.Destructible;
                if (!ours) continue;
                hitCollider = hit.collider;
                hitPoint = hit.point;
                break;
            }
            if (hitCollider == null)
                foreach (Collider c in Physics.OverlapSphere(resource.InteractionPosition, 0.5f, mask))
                    if (c != null && (c.transform == resource.GameObject.transform || c.transform.IsChildOf(resource.GameObject.transform)))
                    {
                        hitCollider = c;
                        hitPoint = c.bounds.center;
                        break;
                    }

            Vector3 hitDir = (hitPoint - Transform.position).normalized;
            HitData hitData = ResourceDataHelper.CreateResourceHitData(resource, _character, tool, hitPoint, hitDir, hitCollider);
            resource.Destructible.Damage(hitData);
            Skills.SkillType skill = resource.RequiredTool == ResourceDataHelper.ToolType.Pickaxe ? Skills.SkillType.Pickaxes : Skills.SkillType.WoodCutting;
            Companion?.GetSkills()?.RaiseSkill(skill, 1f);
        }

        private ResourceDataHelper.ResourceData Resource(GameObject target)
        {
            if (target == null) return null;
            if (_resources.TryGetValue(target, out var known) && known.GameObject != null) return known;
            var data = ResourceDataHelper.GetResourceData(target);
            _resources[target] = data;
            return data;
        }

        // ResourceGatheringBehavior's pick: owning the pickable makes Interact's RPC_Pick run here (needs a local player).
        private void Pick(GameObject target)
        {
            if (target == null || _humanoid == null || Player.m_localPlayer == null) return;
            _zanim?.SetTrigger("interact");
            ZNetView view = target.GetComponentInParent<ZNetView>();
            if (view == null || !view.IsValid()) return;
            view.ClaimOwnership();
            Pickable pickable = target.GetComponentInParent<Pickable>();
            if (pickable != null) { if (pickable.CanBePicked()) pickable.Interact(_humanoid, false, false); return; }
            target.GetComponentInParent<PickableItem>()?.Interact(_humanoid, false, false);
        }

        private void Place(TaskOrder order)
        {
            Piece placed = ChoreBrain.PlacePiece(order.PieceName, order.Point, order.Rotation, Job.Body.Inventory, Job.Body.OwnerPlayerId, out string why);
            if (placed == null) Debug.LogWarning($"[Haul] {Companion?.companionName} couldn't place {order.PieceName}: {why}");
            else _zanim?.SetTrigger("interact");
        }

        private void Craft(TaskOrder order)
        {
            CraftingStation station = order.Target != null ? order.Target.GetComponentInParent<CraftingStation>() : null;
            if (order.Recipe == null || !ResourceDataHelper.CraftTool(order.Recipe, station, Job.Body.Inventory))
                Debug.LogWarning($"[Haul] {Companion?.companionName} couldn't craft {(order.Recipe != null && order.Recipe.m_item != null ? order.Recipe.m_item.name : "?")} at {(station != null ? Utils.GetPrefabName(station.gameObject) : "hand")}");
            else _zanim?.SetTrigger("interact");
        }
    }
}
