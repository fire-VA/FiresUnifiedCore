using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Animation
{
    /// <summary>
    /// Centralized controller for companion animations and emotes.
    /// Handles animation state tracking, hard timeouts, and cleanup.
    /// 
    /// This solves the common problem of companions getting stuck in animation states
    /// (dancing forever, sitting indefinitely, etc.) by enforcing hard timeouts on all animations.
    /// 
    /// FEATURES:
    /// - Centralized emote playback with automatic cleanup
    /// - Hard timeout enforcement for ALL animations
    /// - Animation state tracking (is moving, is attacking, etc.)
    /// - Safe animation bool management (tracks what we set, cleans up properly)
    /// - Integration with ZSyncAnimation and Unity Animator
    /// </summary>
    public class CompanionAnimationController : MonoBehaviour
    {
        #region Configuration
        
        [Header("Timeout Settings")]
        [Tooltip("Maximum time any emote can play before forced reset")]
        public float emoteHardTimeout = 60f;
        
        [Tooltip("Maximum time to be in any 'frozen' animation state")]
        public float frozenStateHardTimeout = 120f;
        
        [Tooltip("How often to check for stuck animations")]
        public float stuckCheckInterval = 5f;
        
        [Header("Debug")]
        public bool verboseLogging = false;
        
        #endregion
        
        #region State
        
        // Current emote state
        private bool _isPlayingEmote = false;
        private string _currentEmote = null;
        private float _emoteStartTime = 0f;
        private float _emoteDuration = 0f;
        private bool _isPersistentEmote = false;
        
        // Animation bools we've set (so we can clean them up)
        private HashSet<string> _activeAnimationBools = new HashSet<string>();
        
        // Tracking
        private float _lastStuckCheck = 0f;
        private float _lastAnimationStateChangeTime = 0f;
        
        // Components
        private ZSyncAnimation _zanim;
        private Animator _animator;
        private Character _character;
        private Rigidbody _rigidbody;
        private CompanionController _companion;
        
        // Cached animator parameter names (for efficient checking)
        private HashSet<string> _animatorParameters;

        // Mecanim trigger that the looping-emote state machine watches for
        // to exit; without firing this, clearing the emote bool alone leaves
        // the animator parked in the looping state.
        private static readonly int EmoteStopTriggerHash = Animator.StringToHash("emote_stop");

        #endregion
        
        #region Properties
        
        /// <summary>
        /// Whether an emote is currently playing.
        /// </summary>
        public bool IsPlayingEmote => _isPlayingEmote;
        
        /// <summary>
        /// The name of the currently playing emote (null if none).
        /// </summary>
        public string CurrentEmote => _currentEmote;
        
        /// <summary>
        /// Whether the current emote is a persistent (looping) emote.
        /// </summary>
        public bool IsPersistentEmote => _isPersistentEmote;
        
        /// <summary>
        /// Time remaining on current emote (0 if not playing).
        /// </summary>
        public float EmoteTimeRemaining => _isPlayingEmote 
            ? Mathf.Max(0, _emoteDuration - (Time.time - _emoteStartTime)) 
            : 0f;
        
        /// <summary>
        /// Whether the emote has exceeded its expected duration.
        /// </summary>
        public bool IsEmoteOverdue => _isPlayingEmote && (Time.time - _emoteStartTime) > _emoteDuration;
        
        /// <summary>
        /// Whether the emote has exceeded the hard timeout.
        /// </summary>
        public bool IsEmoteTimedOut => _isPlayingEmote && (Time.time - _emoteStartTime) > emoteHardTimeout;
        
        /// <summary>
        /// Whether the character is currently in locomotion (walking/running).
        /// </summary>
        public bool IsInLocomotion
        {
            get
            {
                if (_character == null) return false;
                Vector3 velocity = _character.GetVelocity();
                return velocity.magnitude > 0.5f;
            }
        }
        
        /// <summary>
        /// Reference to the companion controller.
        /// </summary>
        public CompanionController Companion => _companion;
        
        #endregion
        
        #region Lifecycle
        
        private void Awake()
        {
            _zanim = GetComponent<ZSyncAnimation>();
            _animator = GetComponent<Animator>();
            _character = GetComponent<Character>();
            _rigidbody = GetComponent<Rigidbody>();
            _companion = GetComponent<CompanionController>();
            
            CacheAnimatorParameters();
        }
        
        private void Update()
        {
            // Periodic stuck check
            if (Time.time - _lastStuckCheck >= stuckCheckInterval)
            {
                _lastStuckCheck = Time.time;
                CheckForStuckAnimations();
            }
            
            // Check emote timeout
            if (_isPlayingEmote)
            {
                // Natural end check (for non-persistent emotes)
                if (!_isPersistentEmote && Time.time - _emoteStartTime >= _emoteDuration)
                {
                    EndEmote(false);
                }
                // Hard timeout check
                else if (Time.time - _emoteStartTime >= emoteHardTimeout)
                {
                    if (verboseLogging)
                    {
                        Debug.LogWarning($"[CompanionAnimationController] {_companion?.companionName} emote '{_currentEmote}' hit hard timeout after {emoteHardTimeout}s - forcing end");
                    }
                    ForceEndEmote();
                }
            }
        }
        
        private void OnDisable()
        {
            // Clean up any active animations when disabled
            ClearAllAnimationBools();
            _isPlayingEmote = false;
            _currentEmote = null;
        }
        
        #endregion
        
        #region Emote Playback
        
        /// <summary>
        /// Plays an emote animation.
        /// </summary>
        /// <param name="emoteName">Name of the emote (e.g., "emote_wave", "sit")</param>
        /// <param name="duration">Expected duration in seconds</param>
        /// <param name="isPersistent">True for looping emotes (sit, dance), false for one-shots (wave, point)</param>
        /// <returns>True if emote started successfully</returns>
        public bool PlayEmote(string emoteName, float duration, bool isPersistent = false)
        {
            if (string.IsNullOrEmpty(emoteName)) return false;
            
            // Stop any current emote first
            if (_isPlayingEmote)
            {
                EndEmote(false);
            }
            
            _currentEmote = emoteName;
            _emoteStartTime = Time.time;
            _emoteDuration = duration;
            _isPersistentEmote = isPersistent;
            _isPlayingEmote = true;
            
            // Set the animation
            if (isPersistent)
            {
                SetAnimationBool(emoteName, true);
            }
            else
            {
                // Use trigger for one-shot emotes
                if (_zanim != null)
                {
                    _zanim.SetTrigger(emoteName);
                }
            }
            
            if (verboseLogging)
            {
                Debug.Log($"[CompanionAnimationController] {_companion?.companionName} started emote '{emoteName}' (duration={duration:F1}s, persistent={isPersistent})");
            }
            
            return true;
        }
        
        /// <summary>
        /// Plays a quick one-shot emote (wave, point, etc.).
        /// </summary>
        public bool PlayQuickEmote(string emoteName, float duration = 2f)
        {
            return PlayEmote(emoteName, duration, false);
        }
        
        /// <summary>
        /// Plays a persistent/looping emote (sit, dance, etc.).
        /// </summary>
        public bool PlayPersistentEmote(string emoteName, float expectedDuration)
        {
            return PlayEmote(emoteName, expectedDuration, true);
        }
        
        /// <summary>
        /// Ends the current emote gracefully.
        /// </summary>
        /// <param name="interrupted">True if the emote was interrupted (not natural end)</param>
        public void EndEmote(bool interrupted = false)
        {
            if (!_isPlayingEmote) return;

            string endedEmote = _currentEmote;

            // Always call the vanilla StopEmote first so m_emoteID is cleared
            // and the Mecanim state machine actually exits the looping emote state.
            CallVanillaStopEmote();

            _isPlayingEmote = false;
            _currentEmote = null;
            _isPersistentEmote = false;

            if (verboseLogging)
                Debug.Log($"[CompanionAnimationController] {_companion?.companionName} ended emote '{endedEmote}' (interrupted={interrupted})");
        }

        /// <summary>
        /// Forcefully ends the current emote and resets animation state.
        /// Use this for hard timeouts or emergency resets.
        /// </summary>
        public void ForceEndEmote()
        {
            string emoteToEnd = _currentEmote;

            // Vanilla StopEmote clears m_emoteID and the ZSyncAnimator bool ï¿½
            // without it the Mecanim state machine keeps the looping emote alive
            // regardless of any manual bool-clearing we do below.
            CallVanillaStopEmote();

            // Additional cleanup: zero out every tracked animation bool and
            // push the animator back to its default/idle state.
            ClearAllAnimationBools();

            _isPlayingEmote = false;
            _currentEmote = null;
            _isPersistentEmote = false;

            ResetAnimatorToDefault();

            if (verboseLogging)
                Debug.Log($"[CompanionAnimationController] {_companion?.companionName} FORCE ended emote '{emoteToEnd}'");
        }

        /// <summary>
        /// Invokes vanilla <c>Character.StopEmote()</c> via reflection.
        /// This is the only path that properly clears <c>m_emoteID</c>, which is
        /// what the Mecanim animator reads to keep looping emotes alive.
        /// </summary>
        private void CallVanillaStopEmote()
        {
            if (_character == null) return;

            var method = _character.GetType().GetMethod(
                "StopEmote",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            if (method != null)
            {
                method.Invoke(_character, null);
            }
            else
            {
                // Fallback: replicate what StopEmote does internally.
                if (!string.IsNullOrEmpty(_currentEmote) && _zanim != null)
                    _zanim.SetBool(_currentEmote, false);

                var emoteIdField = typeof(Character).GetField(
                    "m_emoteID",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                emoteIdField?.SetValue(_character, "");
            }

            // Fire the emote_stop trigger so the Mecanim looping-emote state
            // actually exits. Vanilla StopEmote only clears m_emoteID and the
            // emote bool â€” looping-emote transitions in this Animator are
            // gated on the emote_stop trigger, not on the bool going false.
            if (_animator != null)
                _animator.SetTrigger(EmoteStopTriggerHash);
            if (_zanim != null)
                _zanim.SetTrigger("emote_stop");
        }
        
        #endregion
        
        #region Work Animations
        
        /// <summary>
        /// Plays a work/crafting animation.
        /// </summary>
        public void PlayWorkAnimation(bool enable)
        {
            SetAnimationBool("crafting", enable);
            SetAnimationBool("Working", enable);
        }
        
        /// <summary>
        /// Plays an interact animation (one-shot trigger).
        /// </summary>
        public void PlayInteractAnimation()
        {
            if (_zanim != null)
            {
                _zanim.SetTrigger("interact");
            }
        }
        
        #endregion
        
        #region Animation Bool Management
        
        /// <summary>
        /// Sets an animation bool and tracks it for cleanup.
        /// </summary>
        public void SetAnimationBool(string paramName, bool value)
        {
            if (string.IsNullOrEmpty(paramName)) return;
            
            if (value)
            {
                _activeAnimationBools.Add(paramName);
            }
            else
            {
                _activeAnimationBools.Remove(paramName);
            }
            
            // Set on ZSyncAnimation
            if (_zanim != null)
            {
                _zanim.SetBool(paramName, value);
            }
            
            // Set on Animator if parameter exists
            if (_animator != null && HasAnimatorParameter(paramName))
            {
                _animator.SetBool(paramName, value);
            }
        }
        
        /// <summary>
        /// Clears all animation bools we've set.
        /// </summary>
        public void ClearAllAnimationBools()
        {
            foreach (string paramName in _activeAnimationBools)
            {
                if (_zanim != null)
                {
                    _zanim.SetBool(paramName, false);
                }
                if (_animator != null && HasAnimatorParameter(paramName))
                {
                    _animator.SetBool(paramName, false);
                }
            }
            
            _activeAnimationBools.Clear();
            
            // Also clear common emote bools that might not be in our tracking
            string[] commonEmotes = { "sit", "wave", "dance", "cheer", "point", "nonono", "think", 
                                      "comehere", "bow", "cower", "cry", "despair", "flex", 
                                      "headbang", "kneel", "laugh", "roar", "shrug", "blowkiss" };
            
            foreach (string emote in commonEmotes)
            {
                string emoteName = emote.StartsWith("emote_") ? emote : $"emote_{emote}";
                if (_zanim != null) _zanim.SetBool(emoteName, false);
                if (_zanim != null) _zanim.SetBool(emote, false);
            }
        }
        
        #endregion
        
        #region Animation State
        
        /// <summary>
        /// Resets the animator to default state.
        /// </summary>
        public void ResetAnimatorToDefault()
        {
            if (_animator == null) return;
            
            // Try to play the default/idle state
            try
            {
                _animator.Play("Idle", 0, 0f);
            }
            catch
            {
                // Ignore if Idle state doesn't exist
            }
            
            // Reset common parameters
            if (HasAnimatorParameter("forward_speed"))
                _animator.SetFloat("forward_speed", 0f);
            if (HasAnimatorParameter("sideway_speed"))
                _animator.SetFloat("sideway_speed", 0f);
            if (HasAnimatorParameter("turn_speed"))
                _animator.SetFloat("turn_speed", 0f);
        }
        
        /// <summary>
        /// Stops all animations and zeros movement.
        /// </summary>
        public void StopAllAnimations()
        {
            ForceEndEmote();
            PlayWorkAnimation(false);
            
            // Zero velocity (rigidbody only — drift safety). Movement itself is NOT touched here: the
            // animation controller does not own movement. The sole caller (WorkBehaviorBase.Cancel →
            // base.Cancel) releases the behavior's UMA authority immediately after, which stops the body
            // cleanly through the single writer. A raw SetMoveDir here would be a non-owner write.
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                Vector3 vel = _rigidbody.linearVelocity;
                _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
                _rigidbody.angularVelocity = Vector3.zero;
            }
        }
        
        #endregion
        
        #region Stuck Detection
        
        /// <summary>
        /// Checks for and fixes stuck animation states.
        /// </summary>
        private void CheckForStuckAnimations()
        {
            // Check emote timeout
            if (_isPlayingEmote && IsEmoteTimedOut)
            {
                Debug.LogWarning($"[CompanionAnimationController] {_companion?.companionName} stuck in emote '{_currentEmote}' - forcing reset");
                ForceEndEmote();
            }
            
            // Check for lingering animation bools that shouldn't be active
            if (!_isPlayingEmote && _activeAnimationBools.Count > 0)
            {
                if (verboseLogging)
                {
                    Debug.Log($"[CompanionAnimationController] {_companion?.companionName} clearing {_activeAnimationBools.Count} orphaned animation bools");
                }
                ClearAllAnimationBools();
            }
        }
        
        /// <summary>
        /// Checks if the animator has a specific parameter.
        /// </summary>
        public bool HasAnimatorParameter(string paramName)
        {
            if (_animatorParameters == null || _animator == null) return false;
            return _animatorParameters.Contains(paramName);
        }
        
        private void CacheAnimatorParameters()
        {
            _animatorParameters = new HashSet<string>();
            
            if (_animator == null) return;
            
            foreach (var param in _animator.parameters)
            {
                _animatorParameters.Add(param.name);
            }
        }
        
        #endregion
        
        #region Utility
        
        /// <summary>
        /// Gets a debug string with current animation state.
        /// </summary>
        public string GetDebugString()
        {
            if (_isPlayingEmote)
            {
                float timeInEmote = Time.time - _emoteStartTime;
                return $"Emote: {_currentEmote} ({timeInEmote:F1}s/{_emoteDuration:F1}s, persistent={_isPersistentEmote})";
            }
            return "No active emote";
        }
        
        #endregion
    }
}
