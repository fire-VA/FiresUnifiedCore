using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Gives companions the ability to open doors that block their path and
    /// close them again a few seconds after passing through.
    ///
    /// Design overview
    /// ---------------
    /// The handler runs on a simple three-phase state machine:
    ///   Idle          ï¿½ watching for a nearby closed door to interact with.
    ///   Opening       ï¿½ companion has walked close enough and called Door.Interact.
    ///   WaitingToClose ï¿½ companion is past the door; a timer counts down before
    ///                    closing it behind them.
    ///
    /// Triggering
    /// ----------
    /// Every <see cref="ScanInterval"/> seconds the handler checks whether the
    /// companion has been stuck (barely moved) for at least <see cref="StuckThreshold"/>
    /// seconds.  If so it scans for the nearest closed, unlocked door within
    /// <see cref="ScanRadius"/> metres and starts approaching it.
    ///
    /// Closing behind
    /// --------------
    /// Once the companion is more than <see cref="CloseDistance"/> metres from the
    /// door (i.e. clearly past it) a <see cref="AutoCloseDelay"/> second countdown
    /// begins.  When it expires the door is closed provided no player or other
    /// companion is standing in the doorway.
    /// </summary>
    [DisallowMultipleComponent]
    public class CompanionDoorHandler : MonoBehaviour
    {
        // ?? tuneable constants ??????????????????????????????????????????????

        /// <summary>How often (seconds) we poll for stuck-detection and door scanning.</summary>
        private const float ScanInterval = 0.5f;

        /// <summary>How long (seconds) the companion must be nearly stationary before we scan for a blocking door.</summary>
        private const float StuckThreshold = 1.2f;

        /// <summary>Minimum distance moved per <see cref="ScanInterval"/> to be considered "not stuck".</summary>
        private const float StuckMoveDist = 0.25f;

        /// <summary>Radius (metres) in which we search for a closed door when stuck.</summary>
        private const float ScanRadius = 4f;

        /// <summary>Distance (metres) at which the companion tries to interact with the door.</summary>
        private const float InteractDist = 1.8f;

        /// <summary>How far past the door (metres) the companion must be before the close-behind timer starts.</summary>
        private const float CloseDistance = 2.5f;

        /// <summary>Seconds to wait after passing the door before closing it.</summary>
        private const float AutoCloseDelay = 3f;

        /// <summary>Radius around the door used to check for nearby players/companions before closing.</summary>
        private const float ProximityBlock = 2f;

        /// <summary>Cooldown (seconds) applied after completing a door interaction so we don't immediately re-trigger.</summary>
        private const float PostCloseCooldown = 4f;

        // ?? state machine ???????????????????????????????????????????????????

        private enum Phase { Idle, Approaching, WaitingToClose }

        private Phase _phase = Phase.Idle;

        // ?? cached references ????????????????????????????????????????????????

        private CompanionAI _ai;
        private Humanoid _humanoid;
        private ZNetView _nview;

        // ?? door tracking ????????????????????????????????????????????????????

        private Door _targetDoor;
        private Vector3 _doorPos;

        // ?? timers / position snapshots ??????????????????????????????????????

        private float _scanTimer;
        private float _stuckTimer;
        private Vector3 _lastPos;

        private float _closeTimer;     // counts down in WaitingToClose
        private float _cooldownTimer;  // post-close cooldown

        // ?? door cache (shared across all instances) ?????????????????????????

        private static Door[] _doorCache;
        private static float _doorCacheExpiry;
        private const float DoorCacheLifetime = 5f;

        // ?? Unity lifecycle ??????????????????????????????????????????????????

        private void Awake()
        {
            _ai       = GetComponent<CompanionAI>();
            _humanoid = GetComponent<Humanoid>();
            _nview    = GetComponent<ZNetView>();
            _lastPos  = transform.position;
        }

        private void Update()
        {
            // Only run on the authority instance (ZDO owner).
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner())
                return;

            float dt = Time.deltaTime;

            if (_cooldownTimer > 0f)
            {
                _cooldownTimer -= dt;
                return;
            }

            switch (_phase)
            {
                case Phase.Idle:         UpdateIdle(dt);         break;
                case Phase.Approaching:  UpdateApproaching(dt);  break;
                case Phase.WaitingToClose: UpdateWaitingToClose(dt); break;
            }
        }

        // ?? phase updates ?????????????????????????????????????????????????????

        private void UpdateIdle(float dt)
        {
            _scanTimer += dt;
            if (_scanTimer < ScanInterval)
                return;
            _scanTimer = 0f;

            // Measure movement since last check.
            float moved = Vector3.Distance(transform.position, _lastPos);
            _lastPos = transform.position;

            if (moved < StuckMoveDist)
                _stuckTimer += ScanInterval;
            else
                _stuckTimer = 0f;

            if (_stuckTimer < StuckThreshold)
                return;

            // Companion appears stuck ï¿½ look for a door that might be blocking it.
            Door door = FindNearestClosedDoor();
            if (door == null)
                return;

            _targetDoor = door;
            _doorPos    = door.transform.position;
            _phase      = Phase.Approaching;
            _stuckTimer = 0f;
        }

        private void UpdateApproaching(float dt)
        {
            if (!ValidateDoor())
                return;

            float dist = Vector3.Distance(transform.position, _doorPos);

            if (dist > InteractDist)
            {
                // Let the companion's normal pathfinding handle getting close;
                // we just need to make sure the door doesn't stay shut in its face.
                return;
            }

            // Close enough ï¿½ interact (opens the door).
            _targetDoor.Interact(_humanoid, false, false);

            _phase      = Phase.WaitingToClose;
            _closeTimer = AutoCloseDelay;
        }

        private void UpdateWaitingToClose(float dt)
        {
            if (!ValidateDoor())
                return;

            _closeTimer -= dt;

            float distFromDoor = Vector3.Distance(transform.position, _doorPos);

            // Only start counting down once the companion has actually passed the door.
            if (distFromDoor < CloseDistance)
            {
                // Still right at the door ï¿½ keep resetting the timer so we wait
                // until after we're through before closing.
                _closeTimer = AutoCloseDelay;
                return;
            }

            if (_closeTimer > 0f)
                return;

            // Timer elapsed ï¿½ close the door if no one is blocking it.
            if (!IsAnyoneNearDoor())
                TryCloseDoor();

            ResetToIdle(withCooldown: true);
        }

        // ?? helpers ??????????????????????????????????????????????????????????

        private void TryCloseDoor()
        {
            if (_targetDoor == null)
                return;

            var nview = _targetDoor.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null)
                return;

            // Only close if still open (state != 0).
            if (nview.GetZDO().GetInt(ZDOVars.s_state) != 0)
                _targetDoor.Interact(_humanoid, false, false);
        }

        /// <summary>Returns the nearest closed, unlocked, accessible door within <see cref="ScanRadius"/>.</summary>
        private Door FindNearestClosedDoor()
        {
            Door best     = null;
            float bestDist = float.MaxValue;

            foreach (Door door in GetDoorCache())
            {
                if (door == null) continue;
                if (!IsValidClosedDoor(door)) continue;

                float d = Vector3.Distance(transform.position, door.transform.position);
                if (d < ScanRadius && d < bestDist)
                {
                    bestDist = d;
                    best     = door;
                }
            }

            return best;
        }

        private static bool IsValidClosedDoor(Door door)
        {
            if (door == null) return false;
            // Skip locked doors.
            if (door.m_keyItem != null) return false;

            // Guard-stone protected doors: vanilla PrivateArea.CheckAccess
            // â†’ HaveLocalAccess derefs this.m_piece and Player.m_localPlayer
            // with NO null guards. Either can be null in normal play:
            //   * this.m_piece is null on a ward whose Awake hasn't finished
            //     (zone-stream-in race; the PrivateArea registers itself
            //     in m_allAreas during Awake but m_piece is wired later)
            //   * Player.m_localPlayer is null mid-teleport / mid-disconnect
            //     / mid-respawn (Game destroys the player GO and recreates
            //     it across the loading screen)
            //
            // Companions tick Update independently of player state, so
            // they CAN call CheckAccess during either window and trip a
            // raw NRE from vanilla. We pre-gate on the player-ready case
            // and wrap the call to absorb the stale-ward case. On either
            // failure we treat the door as inaccessible (skip it), which
            // is the same behaviour as a real ward block â€” companion just
            // walks past instead of trying to open.
            if (door.m_checkGuardStone)
            {
                if (Player.m_localPlayer == null) return false;
                bool hasAccess;
                try
                {
                    hasAccess = PrivateArea.CheckAccess(door.transform.position, flash: false);
                }
                catch
                {
                    // Stale PrivateArea in vanilla's static m_allAreas list
                    // (m_piece null on a half-initialized or half-destroyed
                    // ward). Conservative: treat as blocked.
                    return false;
                }
                if (!hasAccess) return false;
            }

            var nview = door.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return false;

            // state == 0 means closed.
            return nview.GetZDO().GetInt(ZDOVars.s_state) == 0;
        }

        private bool IsAnyoneNearDoor()
        {
            // Check local player.
            if (Player.m_localPlayer != null &&
                Vector3.Distance(Player.m_localPlayer.transform.position, _doorPos) < ProximityBlock)
                return true;

            // Check other characters (companions, enemies) nearby.
            var hits = Physics.OverlapSphere(_doorPos, ProximityBlock);
            foreach (var col in hits)
            {
                if (col == null || col.gameObject == gameObject) continue;
                if (col.GetComponent<Character>() != null)
                    return true;
            }

            return false;
        }

        private bool ValidateDoor()
        {
            if (_targetDoor != null && _targetDoor)
                return true;

            ResetToIdle();
            return false;
        }

        private void ResetToIdle(bool withCooldown = false)
        {
            _phase      = Phase.Idle;
            _targetDoor = null;
            _stuckTimer = 0f;
            _scanTimer  = 0f;
            _closeTimer = 0f;

            if (withCooldown)
                _cooldownTimer = PostCloseCooldown;
        }

        // ?? door cache ????????????????????????????????????????????????????????

        private static Door[] GetDoorCache()
        {
            if (_doorCache == null || Time.time >= _doorCacheExpiry)
            {
                _doorCache       = Object.FindObjectsByType<Door>(FindObjectsSortMode.None);
                _doorCacheExpiry = Time.time + DoorCacheLifetime;
            }
            return _doorCache;
        }

        /// <summary>
        /// Invalidates the shared door cache. Call this after a door is placed
        /// or destroyed (e.g. from a ZNetScene event) so the next scan picks
        /// up the change.
        /// </summary>
        public static void InvalidateDoorCache()
        {
            _doorCache       = null;
            _doorCacheExpiry = 0f;
        }
    }
}
