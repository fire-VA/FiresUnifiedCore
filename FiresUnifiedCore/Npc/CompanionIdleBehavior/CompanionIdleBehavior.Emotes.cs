using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc.Animation;
using FiresCore.Npc.Movement;

namespace FiresCore.Npc
{
    // Emote playback and management
    public partial class CompanionIdleBehavior
    {
        #region Emote Data

        // Idle emotes come from the player-rig catalog: one-shots end on their own, loops and holds are ended by
        // StopCurrentEmoteAnimation (emote_stop or the Bool back to false) when their time is up.
        private static List<PlayerAnimation> _quickEmotes;
        private static List<PlayerAnimation> _persistentEmotes;

        private static List<PlayerAnimation> QuickEmotes
        {
            get
            {
                if (_quickEmotes == null) SplitIdleEmotes();
                return _quickEmotes;
            }
        }

        private static List<PlayerAnimation> PersistentEmotes
        {
            get
            {
                if (_persistentEmotes == null) SplitIdleEmotes();
                return _persistentEmotes;
            }
        }

        private static void SplitIdleEmotes()
        {
            _quickEmotes = new List<PlayerAnimation>();
            _persistentEmotes = new List<PlayerAnimation>();
            foreach (var anim in PlayerAnimationCatalog.For(PlayerAnimUse.CompanionIdle))
                (anim.Kind == PlayerAnimKind.OneShot ? _quickEmotes : _persistentEmotes).Add(anim);
        }

        #endregion

        #region Emote Playback

        private void StartIdleEmote()
        {
            // Decide between quick emotes vs persistent emotes
            bool useQuickEmote = Time.time - _lastCombatTime < combatCooldownForIdle * 3f ||
                UnityEngine.Random.value > 0.3f;

            var pool = useQuickEmote ? QuickEmotes : PersistentEmotes;
            if (pool.Count == 0) return;
            var anim = pool[UnityEngine.Random.Range(0, pool.Count)];
            if (!PlayerAnimationCatalog.Has(_animator, anim))
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} model cannot play {anim.Parameter} - skipping emote");
                _nextEmoteTime = Time.time + UnityEngine.Random.Range(idleEmoteMinInterval, idleEmoteMaxInterval);
                return;
            }

            string emote = anim.Parameter;
            bool isPersistent = anim.Kind != PlayerAnimKind.OneShot;
            string emoteType = isPersistent ? "persistent" : "quick";
            float duration;
            if (isPersistent)
            {
                float minDuration = Mathf.Max(persistentEmoteDurationMin, Mathf.Min(anim.Seconds, persistentEmoteDurationMax));
                duration = UnityEngine.Random.Range(minDuration, persistentEmoteDurationMax);
            }
            else
            {
                duration = anim.Seconds > 0f ? anim.Seconds : quickEmoteDuration;
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

            PlayerAnimationCatalog.Play(_zanim, _animator, anim);

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
