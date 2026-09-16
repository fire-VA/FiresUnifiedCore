using UnityEngine;
using System;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// The single writer of a companion's body rotation, the rotational counterpart of
    /// <see cref="UnifiedMovementAuthority"/>: combat strafing, bow aim, "face the station" and AI look-at used to
    /// write transform.rotation directly and fight each other. One source owns facing at a time on the same
    /// priority ladder, with ties kept by the incumbent, and facing is owned separately from movement so a
    /// standing companion can still face its target. With no owner, vanilla rotation applies. Runs in LateUpdate.
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
