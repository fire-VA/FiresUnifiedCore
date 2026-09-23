using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Companion idle behavior: scan nearby player-placed building pieces for damage,
    /// acquire a hammer (from inventory → nearby chests → craft at workbench), then
    /// walk to each damaged piece and repair it.
    ///
    /// Only activates for companions in Stay mode (HasHomePosition &amp;&amp; !ShouldBeFollowing).
    /// Only repairs pieces placed by a player (Piece.GetCreator() != 0).
    /// Building repair in Valheim is material-free — WearNTear.Repair() resets health to max.
    /// Hammer crafting (3 Wood + 2 Stone) simulates the vanilla recipe.
    /// </summary>
    public class BuildingRepairBehavior : WorkBehaviorBase<BuildingRepairBehavior.RepairPhase>
    {
        public override string BehaviorName => "BuildingRepair";
        public override bool AvailableForIdleRotation => true;
        public override bool SupportsResumption => true;
        public override int InventoryPriority => 5; // Slightly above normal, below deposit (100)

        #region Phase Enum

        public enum RepairPhase
        {
            Scanning,
            FindingHammer,
            MovingToChestForHammer,
            MovingToWorkbench,
            CraftingHammer,
            MovingToPiece,
            Repairing,
            FindingNextPiece,
            Complete
        }

        #endregion

        #region Constants

        private const float ScanRadius         = 40f;
        private const float InteractionRange   = 2.5f;
        private const float RepairThreshold    = 0.95f;  // Repair if health below 95%
        private const float RepairDuration     = 2.0f;   // Animation hold time
        private const float MaxRepairTime     = 180f;
        private const float ScanCacheDuration = 30f;
        // Vanilla Hammer recipe
        private const int   WoodNeeded  = 3;
        private const int   StoneNeeded = 2;
        private const string HammerPrefab = "Hammer";

        #endregion

        #region State

        private List<WearNTear>    _damagedPieces  = new List<WearNTear>();
        private HashSet<WearNTear> _repairedPieces = new HashSet<WearNTear>();
        private WearNTear  _currentPiece;
        private Container  _hammerChest;
        private CraftingStation _workbench;
        private float _repairPhaseStart;
        private int   _totalRepaired;

        private ItemDrop.ItemData _equippedHammer;
        private ItemDrop.ItemData _savedRightHand;  // displaced from right-hand slot when hammer equips
        private bool _swingTriggered;               // fire hammer animation once per repair, not every frame

        // CanStart() cache — Physics.OverlapSphere is expensive, throttle it
        private float _lastCanStartScan  = -999f;
        private bool  _cachedHasDamage   = false;

        // Set by the command system (Shift+MMB on a damaged Piece) to force
        // this companion to repair a specific structure. Bypasses the
        // Stay-mode and proximity-scan gates. Cleared once consumed in Start().
        private GameObject _commandedTarget;

        #endregion

        #region WorkBehaviorBase Implementation

        protected override RepairPhase InitialPhase => RepairPhase.Scanning;

        protected override float GetPhaseTimeout(RepairPhase phase)
        {
            return phase switch
            {
                RepairPhase.Scanning               => 5f,
                RepairPhase.FindingHammer          => 5f,
                RepairPhase.MovingToChestForHammer => 20f,
                RepairPhase.MovingToWorkbench      => 20f,
                RepairPhase.CraftingHammer         => 5f,
                RepairPhase.MovingToPiece          => 20f,
                RepairPhase.Repairing              => 10f,
                RepairPhase.FindingNextPiece       => 5f,
                _                                  => 10f
            };
        }

        protected override string GetPhaseDescription(RepairPhase phase)
        {
            int remaining = _damagedPieces.Count - _repairedPieces.Count;
            return phase switch
            {
                RepairPhase.Scanning               => "Checking for damage",
                RepairPhase.FindingHammer          => "Looking for a hammer",
                RepairPhase.MovingToChestForHammer => "Getting hammer from chest",
                RepairPhase.MovingToWorkbench      => "Going to workbench",
                RepairPhase.CraftingHammer         => "Crafting a hammer",
                RepairPhase.MovingToPiece          => $"Going to repair ({remaining} left)",
                RepairPhase.Repairing              => "Repairing structure",
                RepairPhase.FindingNextPiece       => "Looking for more damage",
                _ => _totalRepaired > 0 ? $"Repaired {_totalRepaired} pieces" : "Repairs complete"
            };
        }

        protected override RepairPhase OnPhaseTimeout(RepairPhase timedOutPhase)
        {
            LogWarning($"Phase {timedOutPhase} timed out");
            return timedOutPhase switch
            {
                RepairPhase.MovingToChestForHammer => RepairPhase.FindingHammer,
                RepairPhase.MovingToWorkbench      => RepairPhase.Complete,
                // Couldn't reach the piece — swing from here and repair anyway.
                // FindingNextPiece would re-queue the same unreachable piece forever.
                RepairPhase.MovingToPiece          => RepairPhase.Repairing,
                RepairPhase.Repairing              => RepairPhase.FindingNextPiece,
                _                                  => RepairPhase.Complete
            };
        }

        protected override void OnPhaseChanged(RepairPhase fromPhase, RepairPhase toPhase)
        {
            if (toPhase == RepairPhase.MovingToPiece || toPhase == RepairPhase.Repairing)
                EquipHammer();

            if (toPhase == RepairPhase.Repairing)
            {
                _repairPhaseStart = Time.time;
                _swingTriggered   = false;  // arm swing for this repair attempt
            }
        }

        #endregion

        #region Lifecycle

        public override void Initialize(CompanionController companion, CompanionIdleBehavior idleBehavior)
        {
            base.Initialize(companion, idleBehavior);
            MaxDuration = MaxRepairTime;
        }

        /// <summary>
        /// Set by the command system when the player Shift+MMBs a damaged
        /// building piece. Forces this companion to repair that piece (and
        /// any others nearby), bypassing the Stay-mode requirement. The
        /// hammer-acquisition check still applies — repair without a hammer
        /// is impossible.
        /// </summary>
        public void SetCommandedTarget(GameObject target)
        {
            _commandedTarget = target;
        }

        public override bool CanStart()
        {
            if (Companion == null) return false;

            // Commanded path: skip toggle + Stay-mode gates. We still require
            // a hammer to be acquirable, since repair without one is impossible.
            if (_commandedTarget != null)
            {
                var commandedWnt = _commandedTarget.GetComponent<WearNTear>()
                                ?? _commandedTarget.GetComponentInParent<WearNTear>();
                if (commandedWnt != null && commandedWnt.GetHealthPercentage() < RepairThreshold
                    && ChestHelper.WardsAllow(commandedWnt.transform.position, Companion))
                {
                    if (!CanAcquireHammer())
                    {
                        LogVerbose("CanStart: FALSE - commanded but cannot acquire hammer");
                        return false;
                    }
                    LogVerbose("CanStart: TRUE - commanded damaged piece");
                    return true;
                }
                // Target was destroyed, fully healed, warded off, or never had a WearNTear; drop it.
                _commandedTarget = null;
            }

            if (!CompanionBehaviorToggles.IsRepairEnabled(Companion)) return false;

            // Only in stay mode
            bool isStaying = IdleBehavior != null && IdleBehavior.HasHomePosition && !Companion.ShouldBeFollowing;
            if (!isStaying) return false;

            // Throttled damage scan
            if (!HasDamagedPiecesNearby())
            {
                LogVerbose("CanStart: FALSE - no damaged pieces nearby");
                return false;
            }

            // Must be able to get a hammer somehow
            if (!CanAcquireHammer())
            {
                LogVerbose("CanStart: FALSE - cannot acquire hammer (none in inventory/chests and no materials to craft)");
                return false;
            }

            LogVerbose("CanStart: TRUE");
            return true;
        }

        public override void Start()
        {
            base.Start();
            _damagedPieces.Clear();
            _repairedPieces.Clear();
            _currentPiece  = null;
            _hammerChest   = null;
            _workbench     = null;
            _equippedHammer = null;
            _savedRightHand = null;
            _swingTriggered = false;
            _totalRepaired = 0;

            // Pre-seed _currentPiece from the commanded target so the Repairing
            // phase finds it immediately. The Scanning phase will still run and
            // pick up any other nearby damage to repair after this one.
            if (_commandedTarget != null)
            {
                var commandedWnt = _commandedTarget.GetComponent<WearNTear>()
                                ?? _commandedTarget.GetComponentInParent<WearNTear>();
                if (commandedWnt != null)
                {
                    _currentPiece = commandedWnt;
                    if (!_damagedPieces.Contains(commandedWnt))
                        _damagedPieces.Add(commandedWnt);
                }
                _commandedTarget = null;
            }

            CompanionChatHelper.QuickMessages.StartingTask(Companion, "repairing buildings");
        }

        protected override bool UpdatePhase(RepairPhase phase)
        {
            CompanionChatHelper.ShowWorkingStatus(Companion, GetStatusDescription());

            return phase switch
            {
                RepairPhase.Scanning               => UpdateScanning(),
                RepairPhase.FindingHammer          => UpdateFindingHammer(),
                RepairPhase.MovingToChestForHammer => UpdateMovingToChestForHammer(),
                RepairPhase.MovingToWorkbench      => UpdateMovingToWorkbench(),
                RepairPhase.CraftingHammer         => UpdateCraftingHammer(),
                RepairPhase.MovingToPiece          => UpdateMovingToPiece(),
                RepairPhase.Repairing              => UpdateRepairing(),
                RepairPhase.FindingNextPiece       => UpdateFindingNextPiece(),
                RepairPhase.Complete               => CompleteAndNotify(),
                _                                  => true
            };
        }

        public override void Cancel()
        {
            // Release any piece we still had reserved
            if (_currentPiece != null)
            {
                InteractableOccupancyManager.Release(_currentPiece.gameObject, Character);
                _currentPiece = null;
            }
            UnequipHammer();
            CompanionChatHelper.ClearWorkingStatus(Companion);
            StopMovement();
            base.Cancel();
        }

        #endregion

        #region Phase Updates

        private bool UpdateScanning()
        {
            _damagedPieces = ScanForDamagedPieces();

            if (_damagedPieces.Count == 0)
            {
                LogVerbose("No damaged pieces found — completing");
                SetPhase(RepairPhase.Complete);
                return false;
            }

            LogVerbose($"Found {_damagedPieces.Count} damaged pieces");

            if (HasHammerInInventory())
            {
                _currentPiece = PickNextPiece();
                if (_currentPiece == null) { SetPhase(RepairPhase.Complete); return false; }
                MoveToPosition(GroundedPosition(_currentPiece.transform.position));
                SetPhase(RepairPhase.MovingToPiece);
            }
            else
            {
                SetPhase(RepairPhase.FindingHammer);
            }

            return false;
        }

        private bool UpdateFindingHammer()
        {
            // Re-check inventory (might have been given one)
            if (HasHammerInInventory())
            {
                _currentPiece = PickNextPiece();
                if (_currentPiece == null) { SetPhase(RepairPhase.Complete); return false; }
                MoveToPosition(GroundedPosition(_currentPiece.transform.position));
                SetPhase(RepairPhase.MovingToPiece);
                return false;
            }

            // Search nearby chests
            Resources.RefreshNearbyChests(true);
            _hammerChest = FindChestWithHammer();
            if (_hammerChest != null)
            {
                Vector3 interactionPoint = InteractionPointHelper.GetContainerInteractionPoint(
                    _hammerChest, Transform.position, InteractionPointHelper.DEFAULT_INTERACTION_DISTANCE);
                MoveToPosition(interactionPoint);
                SetPhase(RepairPhase.MovingToChestForHammer);
                return false;
            }

            // Try to craft one at a workbench
            if (HasCraftingMaterials())
            {
                _workbench = FindNearestWorkbench();
                if (_workbench != null)
                {
                    MoveToPosition(_workbench.transform.position);
                    SetPhase(RepairPhase.MovingToWorkbench);
                    return false;
                }
            }

            LogVerbose("Cannot acquire hammer — completing");
            SetPhase(RepairPhase.Complete);
            return false;
        }

        private bool UpdateMovingToChestForHammer()
        {
            if (_hammerChest == null) { SetPhase(RepairPhase.FindingHammer); return false; }

            if (DistanceTo(_hammerChest.transform.position) < InteractionRange)
            {
                StopMovement();
                FaceTarget(_hammerChest.transform.position);

                if (TryTakeHammerFromChest(_hammerChest))
                {
                    LogVerbose("Picked up hammer from chest");
                    _currentPiece = PickNextPiece();
                    if (_currentPiece == null) { SetPhase(RepairPhase.Complete); return false; }
                    MoveToPosition(_currentPiece.transform.position);
                    SetPhase(RepairPhase.MovingToPiece);
                }
                else
                {
                    // Chest no longer has hammer (taken by someone else)
                    SetPhase(RepairPhase.FindingHammer);
                }
                return false;
            }

            if (ContinueMovement())
            {
                // Arrived via pathfinding
                StopMovement();
                FaceTarget(_hammerChest.transform.position);

                if (TryTakeHammerFromChest(_hammerChest))
                {
                    LogVerbose("Picked up hammer from chest");
                    _currentPiece = PickNextPiece();
                    if (_currentPiece == null) { SetPhase(RepairPhase.Complete); return false; }
                    MoveToPosition(_currentPiece.transform.position);
                    SetPhase(RepairPhase.MovingToPiece);
                }
                else
                {
                    SetPhase(RepairPhase.FindingHammer);
                }
            }

            return false;
        }

        private bool UpdateMovingToWorkbench()
        {
            if (_workbench == null) { SetPhase(RepairPhase.Complete); return false; }

            if (DistanceTo(_workbench.transform.position) < InteractionRange)
            {
                StopMovement();
                FaceTarget(_workbench.transform.position);
                SetPhase(RepairPhase.CraftingHammer);
                return false;
            }

            if (ContinueMovement())
            {
                StopMovement();
                FaceTarget(_workbench.transform.position);
                SetPhase(RepairPhase.CraftingHammer);
            }

            return false;
        }

        private bool UpdateCraftingHammer()
        {
            PlayWorkAnimation(true);

            var storage = GetStorageInventory();
            if (storage == null) { SetPhase(RepairPhase.Complete); return false; }

            var hammerPrefab = ZNetScene.instance?.GetPrefab(HammerPrefab);
            if (hammerPrefab == null || !storage.CanAddItem(hammerPrefab, 1))
            {
                LogWarning("Hammer prefab missing or no inventory space - aborting");
                PlayWorkAnimation(false);
                SetPhase(RepairPhase.Complete);
                return false;
            }

            if (!HasCraftingMaterials() ||
                !TryConsumeItems(storage, "Wood", WoodNeeded) ||
                !TryConsumeItems(storage, "Stone", StoneNeeded))
            {
                LogWarning("Could not consume crafting materials for hammer — aborting");
                PlayWorkAnimation(false);
                SetPhase(RepairPhase.Complete);
                return false;
            }

            // Created from its prefab so m_dropPrefab is set: IsHammerItem matches it and it survives save/load (Inventory.cs:88-96).
            storage.AddItem(hammerPrefab, 1);
            SaveInventory();
            PlayWorkAnimation(false);
            LogVerbose("Crafted a hammer");

            _currentPiece = PickNextPiece();
            if (_currentPiece == null) { SetPhase(RepairPhase.Complete); return false; }
            MoveToPosition(GroundedPosition(_currentPiece.transform.position));
            SetPhase(RepairPhase.MovingToPiece);
            return false;
        }

        private bool UpdateMovingToPiece()
        {
            if (_currentPiece == null || _currentPiece.GetHealthPercentage() >= RepairThreshold)
            {
                // Piece healed itself (e.g., natural regen) or was destroyed/null
                SetPhase(RepairPhase.FindingNextPiece);
                return false;
            }

            if (DistanceXZ(_currentPiece.transform.position) < InteractionRange)
            {
                StopMovement();
                FaceTarget(_currentPiece.transform.position);
                _repairPhaseStart = Time.time;
                SetPhase(RepairPhase.Repairing);
                return false;
            }

            if (ContinueMovement())
            {
                StopMovement();
                FaceTarget(_currentPiece.transform.position);
                _repairPhaseStart = Time.time;
                SetPhase(RepairPhase.Repairing);
            }

            return false;
        }

        private bool UpdateRepairing()
        {
            if (_currentPiece == null)
            {
                SetPhase(RepairPhase.FindingNextPiece);
                return false;
            }

            FaceTarget(_currentPiece.transform.position);

            // Fire the hammer swing animation exactly once when the phase begins
            if (!_swingTriggered)
            {
                _swingTriggered = true;
                PlayHammerSwingAnimation();
            }

            // Hold for swing animation duration before applying repair
            if (Time.time - _repairPhaseStart < RepairDuration)
                return false;

            float hpBefore = _currentPiece.GetHealthPercentage();
            LogVerbose($"Attempting repair on {_currentPiece.name} (hp={hpBefore:P0})");

            // CRITICAL: add before repair attempt — prevents re-queuing the same piece
            // if RepairPiece() returns false (e.g., ZDO ownership race on first try)
            _repairedPieces.Add(_currentPiece);

            if (RepairPiece(_currentPiece))
            {
                _totalRepaired++;
                PlayRepairHitEffect();
                LogVerbose($"Repaired piece ({_totalRepaired} total)");
            }
            else
            {
                LogVerbose($"RepairPiece returned false — skipping (won't retry)");
            }

            SetPhase(RepairPhase.FindingNextPiece);
            return false;
        }

        private bool UpdateFindingNextPiece()
        {
            _currentPiece = PickNextPiece();

            if (_currentPiece == null)
            {
                // Do one more scan in case new damage appeared
                var fresh = ScanForDamagedPieces();
                foreach (var wearNTear in fresh)
                {
                    if (!_repairedPieces.Contains(wearNTear))
                    {
                        _currentPiece = wearNTear;
                        break;
                    }
                }
            }

            if (_currentPiece == null)
            {
                SetPhase(RepairPhase.Complete);
            }
            else
            {
                MoveToPosition(GroundedPosition(_currentPiece.transform.position));
                SetPhase(RepairPhase.MovingToPiece);
            }

            return false;
        }

        private bool CompleteAndNotify()
        {
            // Release any held piece reservation before exiting.
            if (_currentPiece != null)
            {
                InteractableOccupancyManager.Release(_currentPiece.gameObject, Character);
                _currentPiece = null;
            }

            UnequipHammer();
            CompanionChatHelper.ClearWorkingStatus(Companion);

            if (_totalRepaired > 0)
                CompanionChatHelper.NotifyTaskComplete(Companion, $"repaired {_totalRepaired} building piece{(_totalRepaired > 1 ? "s" : "")}");

            // Cache is stale after repairs — reset so next CanStart() re-scans
            _cachedHasDamage  = false;
            _lastCanStartScan = -999f;

            Complete();
            return true;
        }

        #endregion

        #region Scanning

        // XZ-only distance — pieces on walls/upper floors have a Y offset that would
        // make 3D distance always fail, so we only care about horizontal proximity.
        private float DistanceXZ(Vector3 target)
        {
            float dx = Transform.position.x - target.x;
            float dz = Transform.position.z - target.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // Project a world position onto the ground so pathfinding navigates on the
        // terrain surface rather than trying to reach a point on a wall or roof.
        private Vector3 GroundedPosition(Vector3 worldPos)
        {
            if (ZoneSystem.instance != null &&
                ZoneSystem.instance.GetGroundHeight(worldPos, out float groundY))
            {
                return new Vector3(worldPos.x, groundY, worldPos.z);
            }
            return new Vector3(worldPos.x, Transform.position.y, worldPos.z);
        }

        private bool HasDamagedPiecesNearby()
        {
            if (Time.time - _lastCanStartScan < ScanCacheDuration)
                return _cachedHasDamage;

            _lastCanStartScan = Time.time;
            _cachedHasDamage  = QuickDamageScan();
            return _cachedHasDamage;
        }

        private bool QuickDamageScan()
        {
            if (Transform == null) return false;

            var colliders = Physics.OverlapSphere(Transform.position, ScanRadius);
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                var wearNTear = collider.GetComponent<WearNTear>();
                if (wearNTear == null) continue;
                if (!IsRepairCandidate(wearNTear)) continue;
                return true;
            }
            return false;
        }

        private List<WearNTear> ScanForDamagedPieces()
        {
            var result    = new List<WearNTear>();
            var processed = new HashSet<int>();

            if (Transform == null) return result;

            var colliders = Physics.OverlapSphere(Transform.position, ScanRadius);
            foreach (var collider in colliders)
            {
                if (collider == null) continue;

                var wearNTear = collider.GetComponent<WearNTear>() ?? collider.GetComponentInParent<WearNTear>();
                if (wearNTear == null) continue;

                int id = wearNTear.GetInstanceID();
                if (processed.Contains(id)) continue;
                processed.Add(id);

                if (!IsRepairCandidate(wearNTear)) continue;
                result.Add(wearNTear);
            }

            // Most-damaged first
            result.Sort((a, b) => a.GetHealthPercentage().CompareTo(b.GetHealthPercentage()));
            return result;
        }

        private bool IsRepairCandidate(WearNTear wearNTear)
        {
            if (wearNTear == null) return false;

            var nview = wearNTear.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;

            if (wearNTear.GetHealthPercentage() >= RepairThreshold) return false;

            // Only repair player-placed pieces
            var piece = wearNTear.GetComponent<Piece>();
            if (piece == null || piece.GetCreator() == 0L) return false;

            return ChestHelper.WardsAllow(wearNTear.transform.position, Companion);
        }

        private WearNTear PickNextPiece()
        {
            // Release the previously-selected piece's reservation so it can be picked up
            // by another companion if we abandon it.
            if (_currentPiece != null)
            {
                InteractableOccupancyManager.Release(_currentPiece.gameObject, Character);
            }

            foreach (var wearNTear in _damagedPieces)
            {
                if (wearNTear == null) continue;
                if (_repairedPieces.Contains(wearNTear)) continue;
                if (wearNTear.GetHealthPercentage() >= RepairThreshold) continue;

                // EARLY RESERVATION: claim the piece before walking to it so other
                // companions don't pile onto the same wall/roof.
                if (!InteractableOccupancyManager.TryOccupy(wearNTear.gameObject, Character, MaxRepairTime))
                {
                    // Another companion is already on this one — skip it for this session.
                    _repairedPieces.Add(wearNTear);
                    continue;
                }

                return wearNTear;
            }
            return null;
        }

        #endregion

        #region Hammer Acquisition

        private bool CanAcquireHammer()
        {
            if (HasHammerInInventory()) return true;

            Resources.RefreshNearbyChests();
            if (FindChestWithHammer() != null) return true;

            if (HasCraftingMaterials() && FindNearestWorkbench() != null) return true;

            return false;
        }

        private bool HasHammerInInventory()
        {
            var storage = GetStorageInventory();
            if (storage == null) return false;

            foreach (var item in storage.GetAllItems())
            {
                if (IsHammerItem(item)) return true;
            }
            return false;
        }

        private Container FindChestWithHammer()
        {
            foreach (var chest in Resources.NearbyChests)
            {
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;

                foreach (var item in inv.GetAllItems())
                {
                    if (IsHammerItem(item)) return chest;
                }
            }
            return null;
        }

        private bool TryTakeHammerFromChest(Container chest)
        {
            if (chest == null || !ChestHelper.TryClaimForWrite(chest, Companion)) return false;

            var chestInv   = chest.GetInventory();
            var storage    = GetStorageInventory();
            if (chestInv == null || storage == null) return false;

            ItemDrop.ItemData hammer = null;
            foreach (var item in chestInv.GetAllItems())
            {
                if (IsHammerItem(item)) { hammer = item; break; }
            }
            if (hammer == null || ChestHelper.MoveItem(chestInv, storage, hammer, hammer.m_stack) == 0) return false;

            SaveInventory();
            return true;
        }

        private bool HasCraftingMaterials()
        {
            var storage = GetStorageInventory();
            if (storage == null) return false;
            return CountItems(storage, "Wood") >= WoodNeeded &&
                   CountItems(storage, "Stone") >= StoneNeeded;
        }

        private CraftingStation FindNearestWorkbench()
        {
            var stations = SmartStorageOrganizer.FindNearbyStations(Transform.position, ScanRadius);
            foreach (var station in stations.OrderBy(s => s.Distance))
            {
                if (station.Type == SmartStorageOrganizer.StationType.Workbench && station.Object != null)
                    return station.Object.GetComponent<CraftingStation>();
            }
            return null;
        }

        private static bool IsHammerItem(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            string prefab = item.m_dropPrefab?.name ?? "";
            return prefab.Equals(HammerPrefab, System.StringComparison.OrdinalIgnoreCase);
        }

        private void EquipHammer()
        {
            if (Inventory == null || _equippedHammer != null) return;

            var storage = GetStorageInventory();
            if (storage == null) return;

            ItemDrop.ItemData hammer = null;
            foreach (var item in storage.GetAllItems())
            {
                if (IsHammerItem(item)) { hammer = item; break; }
            }
            if (hammer == null) return;

            // Displace whatever is in the right-hand slot so we can restore it later
            _savedRightHand = Inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            if (_savedRightHand != null)
                Inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);

            // Pull hammer out of storage and equip it visually to the right hand
            storage.RemoveItem(hammer);
            Inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, hammer);
            Inventory.RecalculateEquipmentBonusesPublic();
            Inventory.ApplyVisualEquipment();
            Inventory.SaveToZDO();

            _equippedHammer = hammer;
            LogVerbose("Equipped hammer to right hand");
        }

        private void UnequipHammer()
        {
            if (Inventory == null || _equippedHammer == null) return;

            // Return hammer to storage and clear the right-hand slot
            Inventory.UnequipSlotSilent(CompanionInventory.EquipmentSlot.RightHand);
            GetStorageInventory()?.AddItem(_equippedHammer);
            _equippedHammer = null;

            // Restore the weapon that was in the right hand before we took it
            if (_savedRightHand != null)
            {
                Inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, _savedRightHand);
                _savedRightHand = null;
            }

            Inventory.RecalculateEquipmentBonusesPublic();
            Inventory.ApplyVisualEquipment();
            Inventory.SaveToZDO();

            LogVerbose("Unequipped hammer, restored right-hand slot");
        }

        #endregion

        #region Repair

        // The hammer's own attack animation (swing_hammer, upper body), as vanilla Player.Repair plays it (Player.cs:2768).
        private void PlayHammerSwingAnimation()
        {
            string trigger = _equippedHammer?.m_shared.m_attack.m_attackAnimation;
            if (string.IsNullOrEmpty(trigger)) return;
            ZAnim?.SetTrigger(trigger);
            LogVerbose($"Hammer swing: {trigger}");
        }

        private void PlayRepairHitEffect()
        {
            if (_currentPiece == null) return;
            // Spawn the piece's hit particles/sound at the impact point
            Vector3 hitPoint = _currentPiece.transform.position + Vector3.up * 0.5f;
            _currentPiece.m_hitEffect.Create(hitPoint, Quaternion.identity, _currentPiece.transform);
        }

        private bool RepairPiece(WearNTear wearNTear)
        {
            if (wearNTear == null) return false;

            var nview = wearNTear.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            if (!ChestHelper.WardsAllow(wearNTear.transform.position, Companion)) return false;

            // Claim ownership so WearNTear.Repair()'s IsOwner() check passes
            nview.ClaimOwnership();

            // Try vanilla repair first — this is correct and syncs to all clients
            if (wearNTear.Repair()) return true;

            // Fallback: ClaimOwnership is not instant (ZNet ownership propagates
            // over the network), so IsOwner() may still return false in the same
            // frame. Write the health ZDO key directly as a fallback. This is the
            // same write that WearNTear.RPC_Repair() does internally.
            float maxHealth = wearNTear.m_health; // world-level-scaled max, same value RPC_Repair uses
            if (maxHealth <= 0f) return false;

            var zdo = nview.GetZDO();
            if (zdo == null) return false;

            float currentHealth = zdo.GetFloat(ZDOVars.s_health, maxHealth);
            if (currentHealth >= maxHealth) return false; // Already at max

            zdo.Set(ZDOVars.s_health, maxHealth);
            nview.InvokeRPC(ZNetView.Everybody, "RPC_HealthChanged", (object)maxHealth);
            LogVerbose($"RepairPiece: direct ZDO write (health {currentHealth:F0} → {maxHealth:F0})");
            return true;
        }

        #endregion

        #region Material Helpers

        private static int CountItems(Inventory inv, string prefabName)
        {
            int count = 0;
            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_dropPrefab?.name == prefabName)
                    count += item.m_stack;
            }
            return count;
        }

        private static bool TryConsumeItems(Inventory inv, string prefabName, int needed)
        {
            if (CountItems(inv, prefabName) < needed) return false;

            int remaining   = needed;
            var items       = new List<ItemDrop.ItemData>(inv.GetAllItems());
            var toRemove    = new List<ItemDrop.ItemData>();

            foreach (var item in items)
            {
                if (remaining <= 0) break;
                if (item?.m_dropPrefab?.name != prefabName) continue;

                if (item.m_stack <= remaining)
                {
                    remaining -= item.m_stack;
                    toRemove.Add(item);
                }
                else
                {
                    item.m_stack -= remaining;
                    remaining = 0;
                }
            }

            foreach (var item in toRemove)
                inv.RemoveItem(item);

            return true;
        }

        #endregion
    }
}
