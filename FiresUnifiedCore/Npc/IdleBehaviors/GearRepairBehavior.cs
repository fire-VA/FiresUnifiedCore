using FiresCore.Npc.AI;
using FiresCore.Npc.Core;
using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// 0.2.836 ([ghost], Fire 10-26: "they should repair broken armors if they can"; his log: Wulf's broken ArmorCarapaceChest/Legs
    /// were "refused (broken), reconcile retries" 872 times while he stood idle - no companion chore ever repaired gear).
    /// A staying companion with worn (below ChoreBrain.DefaultRepairThreshold) or broken gear repairs it the way the autoplay bot
    /// does, through Core's one repair path: ChoreBrain.NextRepair on a CompanionTaskBody (the nearest usable station of the
    /// item's repair / crafting station kind and level, vanilla InventoryGui.CanRepair rules), walk there, ChoreBrain.RepairAt,
    /// then NextRepair's Equip order puts the repaired piece back on. A piece no station in range can repair is left to GearUp
    /// (it then counts as an empty slot there and is replaced).
    /// </summary>
    public class GearRepairBehavior : WorkBehaviorBase<GearRepairBehavior.RepairPhase>
    {
        public override string BehaviorName => "GearRepair";
        public override bool AvailableForIdleRotation => false;   // staying (at base) companions only

        public enum RepairPhase { Planning, Walking, Repairing, Complete }

        private const float Radius = 40f, ScanCooldown = 30f, WalkTimeout = 30f, RepairSeconds = 1.5f, MaxSeconds = 120f;
        private CompanionTaskBody _body;
        private TaskOrder _order;
        private float _nextScanAt;
        private string _doing = "checking gear";

        protected override RepairPhase InitialPhase => RepairPhase.Planning;
        protected override float GetPhaseTimeout(RepairPhase p) => p == RepairPhase.Walking ? WalkTimeout : 10f;
        protected override RepairPhase OnPhaseTimeout(RepairPhase t)
        {
            if (t == RepairPhase.Walking && _order?.Target != null) { ChoreBrain.SkipBaseTarget(_order.Target); LogVerbose($"can't reach {Utils.GetPrefabName(_order.Target)}; skipped for now"); }
            return RepairPhase.Complete;
        }
        protected override string GetPhaseDescription(RepairPhase p) => _doing;
        public override string GetStatusDescription() => _doing;

        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior) { base.Initialize(companion, idleBehavior); MaxDuration = MaxSeconds; }

        public override bool CanStart()
        {
            if (Companion == null || Inventory == null || Time.time < _nextScanAt) return false;
            if (!CompanionBehaviorToggles.IsCraftingEnabled(Companion)) return false;   // "crafting station upgrades / repairs"
            bool staying = IdleBehavior != null && IdleBehavior.HasHomePosition && !Companion.ShouldBeFollowing;
            if (!staying) return false;
            _nextScanAt = Time.time + ScanCooldown;
            if (_body == null) _body = new CompanionTaskBody(Companion);
            bool worn = false;
            foreach (var it in _body.Inventory.GetAllItems()) if (ChoreBrain.NeedsRepair(it)) { worn = true; break; }
            if (!worn) return false;
            _order = ChoreBrain.NextRepair(_body, Radius);
            if (_order.Kind == TaskKind.Done || _order.Kind == TaskKind.Failed)
            {
                LogVerbose($"CanStart: FALSE - {_order.Reason}");
                return false;
            }
            return true;
        }

        protected override bool UpdatePhase(RepairPhase phase)
        {
            switch (phase)
            {
                case RepairPhase.Planning:
                    _order = ChoreBrain.NextRepair(_body, Radius);
                    switch (_order.Kind)
                    {
                        case TaskKind.Move:
                            _doing = $"repair: walking to {(_order.Target != null ? Utils.GetPrefabName(_order.Target) : "a station")} with {_order.Item?.m_shared?.m_name}";
                            MoveToPosition(_order.Point);
                            InteractTarget = _order.Target;
                            SetPhase(RepairPhase.Walking);
                            return false;
                        case TaskKind.Repair:
                            _doing = $"repair: {_order.Item?.m_shared?.m_name} at {(_order.Target != null ? Utils.GetPrefabName(_order.Target) : "?")}";
                            if (_order.Target != null) FaceTarget(_order.Target.transform.position);
                            PlayInteractAnimation();
                            SetPhase(RepairPhase.Repairing);
                            return false;
                        case TaskKind.Equip:
                            _doing = $"repair: putting {_order.Item?.m_shared?.m_name} back on";
                            if (_order.Item != null) _body.Equip(_order.Item);
                            return false;   // plan again next frame
                        default:
                            _doing = _order.Kind == TaskKind.Done ? "repair: gear whole" : $"repair: {_order.Reason}";
                            LogVerbose(_doing);
                            SetPhase(RepairPhase.Complete);
                            return false;
                    }
                case RepairPhase.Walking:
                    if (ContinueMovement() || (_order?.Target != null && Vector3.Distance(Transform.position, _order.Target.transform.position) < 2.5f))
                    {
                        StopMovement();
                        SetPhase(RepairPhase.Planning);
                    }
                    return false;
                case RepairPhase.Repairing:
                    if (TimeInCurrentPhase < RepairSeconds) return false;
                    var station = _order?.Target != null ? _order.Target.GetComponent<CraftingStation>() ?? _order.Target.GetComponentInParent<CraftingStation>() : null;
                    if (station == null || _order.Item == null || !ChoreBrain.RepairAt(_body.Character, _order.Item, station))
                        LogVerbose($"repair of {_order?.Item?.m_shared?.m_name} at {(station != null ? station.m_name : "?")} refused");
                    SetPhase(RepairPhase.Planning);
                    return false;
                default:
                    Complete();
                    return true;
            }
        }
    }
}
