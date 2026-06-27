using UnityEngine;
using System;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// FACING AUTHORITY — the single writer for a companion's BODY rotation (transform.rotation), the
    /// rotational sibling of <see cref="UnifiedMovementAuthority"/>. It exists because facing is a second,
    /// independent channel: before this, every facing writer (combat strafe-facing, bow aim-lock, work
    /// "face the station", AI "look at enemy") slammed transform.rotation directly and they fought —
    /// the companion would strafe one way while snapping to face another.
    ///
    /// MODEL (mirrors UMA): exactly one source owns facing at a time, chosen by the SAME priority ladder
    /// (UnifiedMovementAuthority.MovementSource). Higher preempts; equal priority is denied to all but the
    /// incumbent (no ping-pong between two Combat-level facers). A writer that doesn't own facing PARKS.
    ///
    /// INDEPENDENT OF MOVEMENT (the carve-out): facing ownership is separate from movement ownership, so a
    /// companion can face an enemy while standing still to attack — it owns FACING without owning MOVEMENT.
    ///
    /// HYBRID WITH VANILLA: when NO source owns facing, this component writes nothing and vanilla Character
    /// rotation (turning toward the move direction) applies as normal. An override only kicks in while a
    /// source holds facing — and releasing hands rotation straight back to vanilla. Applied in LateUpdate
    /// so it has the final say after movement + animation.
    /// </summary>
    public class CompanionFacingAuthority : MonoBehaviour
    {
        /// <summary>Default slerp speed (radians-ish per second factor) for smooth turns.</summary>
        public float TurnSpeed = 12f;

        public UnifiedMovementAuthority.MovementSource CurrentSource { get; private set; }
            = UnifiedMovementAuthority.MovementSource.None;
        public string CurrentOwner { get; private set; } = "";

        /// <summary>Fired when facing changes hands (acquire/release), so parked facers can re-evaluate.</summary>
        public event Action OnFacingChanged;

        private float _duration;
        private float _timeoutTime;

        private bool _hasDir;
        private Vector3 _lookDir;
        private bool _hasTarget;
        private Vector3 _targetPos;
        private bool _snap;

        private Character _character;
        private ZNetView _nview;

        private void Awake()
        {
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
        }

        /// <summary>True if <paramref name="owner"/> currently owns facing — the park query for facers.</summary>
        public bool CanFace(string owner) =>
            CurrentSource != UnifiedMovementAuthority.MovementSource.None && CurrentOwner == owner;

        public bool HasFacing(string owner) => CanFace(owner);

        /// <summary>
        /// Acquire (or extend) facing. Granted only if strictly higher than the current source, or the
        /// same source+owner re-acquiring. Equal priority by a different owner is denied (incumbency).
        /// </summary>
        public bool TryAcquireFacing(UnifiedMovementAuthority.MovementSource source, string owner, float duration = 5f)
        {
            if ((int)source < (int)CurrentSource)
                return false;

            if ((int)source == (int)CurrentSource &&
                CurrentSource != UnifiedMovementAuthority.MovementSource.None &&
                !string.IsNullOrEmpty(CurrentOwner) && owner != CurrentOwner)
                return false;

            if (source == CurrentSource && owner == CurrentOwner)
            {
                if (duration > 0) { _duration = duration; _timeoutTime = Time.time + duration; }
                return true;
            }

            CurrentSource = source;
            CurrentOwner = owner;
            _duration = duration;
            _timeoutTime = duration > 0 ? Time.time + duration : float.MaxValue;
            OnFacingChanged?.Invoke();
            return true;
        }

        /// <summary>Face a world-space direction (horizontal). No-op for a non-owner.</summary>
        public void SetLookDirection(string owner, Vector3 dir, bool instant = false)
        {
            if (CurrentOwner != owner) return;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            _lookDir = dir.normalized;
            _hasDir = true;
            _hasTarget = false;
            _snap = instant;
            if (_duration > 0) _timeoutTime = Time.time + _duration;
        }

        /// <summary>Face a world position (re-evaluated each frame as the companion/target move). No-op for a non-owner.</summary>
        public void SetLookTarget(string owner, Vector3 worldPos, bool instant = false)
        {
            if (CurrentOwner != owner) return;
            _targetPos = worldPos;
            _hasTarget = true;
            _hasDir = false;
            _snap = instant;
            if (_duration > 0) _timeoutTime = Time.time + _duration;
        }

        /// <summary>Release facing if <paramref name="owner"/> holds it; rotation reverts to vanilla.</summary>
        public void ReleaseFacing(string owner)
        {
            if (CurrentOwner != owner && !string.IsNullOrEmpty(CurrentOwner)) return;
            if (CurrentSource == UnifiedMovementAuthority.MovementSource.None) return;
            CurrentSource = UnifiedMovementAuthority.MovementSource.None;
            CurrentOwner = "";
            _hasDir = false;
            _hasTarget = false;
            OnFacingChanged?.Invoke();
        }

        private void Update()
        {
            // Timeout so a forgotten release never locks facing (mirrors UMA's authority timeout).
            if (CurrentSource != UnifiedMovementAuthority.MovementSource.None &&
                _duration > 0 && Time.time >= _timeoutTime)
            {
                ReleaseFacing(CurrentOwner);
            }
        }

        private void LateUpdate()
        {
            if (CurrentSource == UnifiedMovementAuthority.MovementSource.None) return;

            // Only the owning client drives facing — remote clients receive it via ZDO sync (mirrors how
            // movement is owner-driven). Without this, setting transform.rotation here would fight sync.
            if (_nview != null && _nview.IsValid() && !_nview.IsOwner()) return;

            Vector3 dir;
            if (_hasTarget) dir = _targetPos - transform.position;
            else if (_hasDir) dir = _lookDir;
            else return;

            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            dir.Normalize();

            // Drive the vanilla look direction too, so attacks/aim use the same facing we render
            // (otherwise an attack would fire toward a stale m_lookDir while the body faces elsewhere).
            _character?.SetLookDir(dir);

            Quaternion target = Quaternion.LookRotation(dir);
            if (float.IsNaN(target.x) || float.IsNaN(target.y) || float.IsNaN(target.z) || float.IsNaN(target.w))
                return;

            transform.rotation = _snap
                ? target
                : Quaternion.Slerp(transform.rotation, target, Time.deltaTime * TurnSpeed);
        }
    }
}
