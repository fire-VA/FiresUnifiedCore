using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Movement;

namespace FiresCore.Npc
{
    // Emote playback and management
    public partial class CompanionIdleBehavior
    {
        #region Emote Data
        
        // Available emotes
        private static readonly string[] QuickEmotes = new string[]
        {
            "emote_point", "emote_wave", "emote_challenge",
            "emote_cheer", "emote_nonono", "emote_thumbsup", "emote_flex",
            "emote_laugh", "emote_shrug", "emote_blowkiss", "emote_bow",
            "emote_cry", "emote_comehere",
            "emote_roar", "emote_toast", "emote_loveyou"
        };
        
        // Longer quick emotes
        private static readonly string[] LongerQuickEmotes = new string[]
        {
            // Currently empty - headbang and dance moved to persistent
        };
        private const float LONGER_EMOTE_DURATION = 10f;

        // Persistent emotes that hold a pose/animation for extended periods
        // NOTE: emote_relax was removed because it doesn't reset correctly
        private static readonly string[] PersistentEmotes = new string[]
        {
            "emote_sit", "emote_despair", "emote_rest", "emote_vibe",
            "emote_kneel", "emote_headbang", "emote_dance"
        };
        
        /// <summary>
        /// Accurate emote durations based on actual Valheim animation clip lengths.
        /// </summary>
        private static readonly Dictionary<string, float> EmoteDurations = new Dictionary<string, float>
        {
            // Quick one-shot emotes
            { "emote_blowkiss", 2.0f },
            { "emote_bow", 3.5f },
            { "emote_challenge", 3.2f },
            { "emote_cheer", 2.5f },
            { "emote_cower", 2.7f },
            { "emote_cry", 4.0f },
            { "emote_flex", 2.9f },
            { "emote_laugh", 3.1f },
            { "emote_nonono", 2.1f },
            { "emote_point", 2.5f },
            { "emote_roar", 2.2f },
            { "emote_shrug", 2.7f },
            { "emote_thumbsup", 1.3f },
            { "emote_wave", 2.5f },
            { "emote_toast", 2.7f },
            { "emote_loveyou", 2.7f },
            { "emote_comehere", 2.4f },
            
            // Persistent/looping emotes
            { "emote_sit", 10.87f },
            { "emote_despair", 6.7f },
            { "emote_kneel", 2.0f },
            { "emote_headbang", 1.4f },
            { "emote_dance", 5.2f },
            { "emote_rest", 10.0f },
            { "emote_vibe", 5.0f },
        };

        #endregion

        #region Emote Playback

        private void StartIdleEmote()
        {
            // Decide between quick emotes vs persistent emotes
            bool useQuickEmote = Time.time - _lastCombatTime < combatCooldownForIdle * 3f ||
                UnityEngine.Random.value > 0.3f;

            string emote;
            float duration;
            string emoteType;
            bool isPersistent;

            if (useQuickEmote)
            {
                emote = QuickEmotes[UnityEngine.Random.Range(0, QuickEmotes.Length)];
                
                if (EmoteDurations.TryGetValue(emote, out float animDuration))
                {
                    duration = animDuration;
                }
                else
                {
                    duration = quickEmoteDuration;
                }
                
                emoteType = "quick";
                isPersistent = false;
            }
            else
            {
                emote = PersistentEmotes[UnityEngine.Random.Range(0, PersistentEmotes.Length)];
                
                float baseCycleDuration = 10f;
                if (EmoteDurations.TryGetValue(emote, out float cycleDuration))
                {
                    baseCycleDuration = cycleDuration;
                }
                
                float minDuration = Mathf.Max(persistentEmoteDurationMin, baseCycleDuration);
                duration = UnityEngine.Random.Range(minDuration, persistentEmoteDurationMax);
                
                emoteType = "persistent";
                isPersistent = true;
            }
            
            // Try to enter emote state in state controller
            if (_stateController != null)
            {
                if (!_stateController.TryEnterState(CompanionStateController.CompanionState.Emote, duration, "Emote:" + emote))
                {
                    if (VerboseLogging)
                        Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} cannot start emote - state controller rejected");
                    return;
                }
            }

            // For persistent emotes, use SetBool; for quick emotes, use SetTrigger
            if (isPersistent)
            {
                if (_zanim != null)
                    _zanim.SetBool(emote, true);
                else if (_animator != null && HasAnimatorParameter(emote))
                    _animator.SetBool(emote, true);
            }
            else
            {
                if (_zanim != null)
                    _zanim.SetTrigger(emote);
                else if (_animator != null)
                    _animator.SetTrigger(emote);
            }

            _isPlayingEmote = true;
            _currentEmote = emote;
            _emoteEndTime = Time.time + duration;
            _isPersistentEmote = isPersistent;

            _nextEmoteTime = Time.time + UnityEngine.Random.Range(idleEmoteMinInterval, idleEmoteMaxInterval);
            SetIdleState(IdleState.PlayingEmote);

            LockMovementForEmote(duration);

            if (VerboseLogging)
                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} playing {emoteType} emote: {emote}, duration: {duration:F1}s, persistent: {isPersistent}");
        }
        
        /// <summary>
        /// Locks movement for the specified emote duration.
        /// </summary>
        private void LockMovementForEmote(float duration)
        {
            if (_combatMovement != null)
            {
                _combatMovement.LockMovement("IdleEmote", duration + 1f);
            }
            
            if (_rigidbody != null)
            {
                _wasKinematicBeforeEmote = _rigidbody.isKinematic;
                
                if (!_rigidbody.isKinematic)
                {
                    _rigidbody.linearVelocity = Vector3.zero;
                    _rigidbody.angularVelocity = Vector3.zero;
                }
            }
            
            if (_character != null && (_rigidbody == null || !_rigidbody.isKinematic))
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
            
            _hasActiveDestination = false;
            
            if (_companionAI != null)
            {
                _companionAI.ClearIdleDestination();
                _companionAI.ClearCommandDestination();
            }
        }
        
        /// <summary>
        /// Enforces complete freeze during emote playback.
        /// </summary>
        private void EnforceEmoteFreeze()
        {
            // Re-lock movement if somehow unlocked
            if (_combatMovement != null && !_combatMovement.IsMovementLocked)
            {
                float remaining = _emoteEndTime - Time.time;
                if (remaining > 0.1f)
                {
                    _combatMovement.LockMovement("IdleEmote", remaining + 1f);
                }
            }
            
            if (_companionAI != null)
            {
                _companionAI.ClearIdleDestination();
                _companionAI.ClearCommandDestination();
            }
            
            // Skip all movement commands if rigidbody is kinematic
            if (_rigidbody != null && _rigidbody.isKinematic)
            {
                return;
            }
            
            if (_rigidbody != null)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            
            if (_character != null)
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
        }
        
        /// <summary>
        /// Stops all movement and zeros velocity. Used before sitting on chairs.
        /// </summary>
        private void ZeroVelocityAndStopMovement()
        {
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            
            if (_character != null && (_rigidbody == null || !_rigidbody.isKinematic))
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
            
            _hasActiveDestination = false;
        }

        #endregion
    }
}
