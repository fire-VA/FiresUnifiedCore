using UnityEngine;

namespace FiresCore.Npc.Formation
{
    /// <summary>
    /// A companion's link to <see cref="GroupFormationManager"/>: registers while enabled, offsets the follow
    /// destination through GetAdjustedFollowTarget, provides idle spread positions through GetIdleSpreadTarget, and
    /// enforces personal space on a throttle in LateUpdate.
    /// </summary>
    public class CompanionFormationController : MonoBehaviour
    {
        #region Settings

        private const float SeparationCheckInterval = 0.2f;
        private const float FormationUpdateInterval = 0.5f;
        private const float CleanupInterval = 5.0f;
        private const float ArrivalThreshold = 0.5f;

        public static bool VerboseLogging = false;

        #endregion

        #region State

        private CompanionController _companion;
        private FormationSlot _cachedSlot;
        private Vector3 _lastSeparationForce;
        private float _lastSeparationTime;
        private float _lastFormationUpdateTime;
        private float _lastCleanupTime;
        private bool _registered;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
        }

        private void OnEnable()
        {
            if (_companion != null && _companion.ownerPlayerId != 0 && _companion.isTamed)
            {
                GroupFormationManager.Instance.Register(_companion);
                _registered = true;
            }
        }

        private void OnDisable()
        {
            if (_registered && _companion != null)
            {
                GroupFormationManager.Instance.Unregister(_companion);
                _registered = false;
            }
        }

        private void OnDestroy()
        {
            if (_registered && _companion != null)
            {
                GroupFormationManager.Instance.Unregister(_companion);
                _registered = false;
            }
        }

        private void LateUpdate()
        {
            if (_companion == null || !_companion.isTamed) return;

            // Lazy registration — companion may become tamed after Awake
            if (!_registered && _companion.ownerPlayerId != 0)
            {
                GroupFormationManager.Instance.Register(_companion);
                _registered = true;
            }

            // Throttled formation update (drives GroupFormationManager for this player's group)
            if (Time.time - _lastFormationUpdateTime >= FormationUpdateInterval)
            {
                _lastFormationUpdateTime = Time.time;
                GroupFormationManager.Instance.UpdateFormations();
            }

            // Throttled personal space check
            if (Time.time - _lastSeparationTime >= SeparationCheckInterval)
            {
                _lastSeparationTime = Time.time;
                _lastSeparationForce = GroupFormationManager.Instance.ComputeSeparation(_companion, transform.position);
            }

            // Periodic cleanup of destroyed companions
            if (Time.time - _lastCleanupTime >= CleanupInterval)
            {
                _lastCleanupTime = Time.time;
                GroupFormationManager.Instance.CleanupDestroyedCompanions();
            }

            // Cache the slot reference for fast reads
            _cachedSlot = GroupFormationManager.Instance.GetSlot(_companion.companionId);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Returns the formation-adjusted follow target position.
        /// When in Following mode, this offsets the raw player position by the formation slot.
        /// When not in formation, returns the raw target unchanged.
        /// </summary>
        /// <param name="rawTarget">The unmodified player/owner position.</param>
        /// <returns>The adjusted target position with formation offset applied.</returns>
        public Vector3 GetAdjustedFollowTarget(Vector3 rawTarget)
        {
            if (_cachedSlot == null || !_cachedSlot.IsValid)
                return rawTarget;

            if (_cachedSlot.Mode == FormationMode.Following)
                return _cachedSlot.WorldPosition;

            return rawTarget;
        }

        /// <summary>
        /// Returns the idle spread target position when the player is stopped.
        /// Returns Vector3.zero if no spread position is assigned (caller should fall back
        /// to default wander behavior).
        /// </summary>
        public Vector3 GetIdleSpreadTarget()
        {
            if (_cachedSlot == null || !_cachedSlot.IsValid)
                return Vector3.zero;

            if (_cachedSlot.Mode == FormationMode.IdleSpread)
                return _cachedSlot.WorldPosition;

            return Vector3.zero;
        }

        /// <summary>
        /// Returns the current personal space separation force.
        /// This is a world-space direction vector indicating which way to push
        /// to maintain personal space from nearby companions.
        /// Returns Vector3.zero if no separation is needed.
        /// </summary>
        public Vector3 GetSeparationForce()
        {
            return _lastSeparationForce;
        }

        /// <summary>
        /// Applies personal space separation to a movement target position.
        /// Blends the separation force with the intended target at the configured blend weight.
        /// </summary>
        /// <param name="intendedTarget">The raw movement target.</param>
        /// <returns>Adjusted target with separation applied.</returns>
        public Vector3 ApplySeparation(Vector3 intendedTarget)
        {
            if (_lastSeparationForce.sqrMagnitude < 0.001f)
                return intendedTarget;

            float blendWeight = GroupFormationManager.Instance.SeparationBlendWeight;
            return Vector3.Lerp(intendedTarget, intendedTarget + _lastSeparationForce, blendWeight);
        }

        /// <summary>
        /// Whether this companion is currently at its assigned formation position.
        /// </summary>
        public bool IsAtFormationPosition
        {
            get
            {
                if (_cachedSlot == null || !_cachedSlot.IsValid) return true;
                if (_cachedSlot.Mode == FormationMode.None) return true;

                float dist = Vector3.Distance(
                    new Vector3(transform.position.x, 0f, transform.position.z),
                    new Vector3(_cachedSlot.WorldPosition.x, 0f, _cachedSlot.WorldPosition.z));
                return dist < ArrivalThreshold;
            }
        }

        /// <summary>
        /// The current formation mode for this companion.
        /// </summary>
        public FormationMode CurrentMode
        {
            get
            {
                if (_cachedSlot == null) return FormationMode.None;
                return _cachedSlot.Mode;
            }
        }

        /// <summary>
        /// Whether formation is active and this companion has a valid slot assignment.
        /// </summary>
        public bool IsInFormation
        {
            get
            {
                return _cachedSlot != null && _cachedSlot.IsValid && _cachedSlot.Mode != FormationMode.None;
            }
        }

        #endregion
    }
}
