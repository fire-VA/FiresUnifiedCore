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

        // The companions' defaults (the inspector fields' initial values), for PickIdleEmote callers without a companion.
        public const float IdleEmoteMinInterval = 45f, IdleEmoteMaxInterval = 120f;
        private const float DefaultQuickSeconds = 2.5f, DefaultPersistentMin = 8f, DefaultPersistentMax = 25f;

        /// <summary>
        /// An idle emote the way a companion picks one (one brain: FDT's bot waiting for the player uses it too): mostly quick
        /// one-shots (always right after a fight), else a loop / hold for 8-25 s; <paramref name="seconds"/> is how long to
        /// play it. Null when the pool is empty or <paramref name="animator"/> can't play the pick. Play it with
        /// <see cref="PlayerAnimationCatalog.Play"/> and end a loop / hold with <see cref="PlayerAnimationCatalog.Stop"/>;
        /// wait <see cref="IdleEmoteMinInterval"/>-<see cref="IdleEmoteMaxInterval"/> s between emotes.
        /// </summary>
        public static PlayerAnimation PickIdleEmote(Animator animator, bool recentCombat, out float seconds) =>
            PickIdleEmote(animator, recentCombat, DefaultQuickSeconds, DefaultPersistentMin, DefaultPersistentMax, out seconds);

        /// <summary>
        /// A vanilla emote name for Player.StartEmote ("wave", "cheer", "sit" …, the catalog's "emote_" parameter without the
        /// prefix), picked from the companions' idle pool: quick one-shots only with <paramref name="quickOnly"/>, else the same
        /// mix as <see cref="PickIdleEmote(Animator, bool, out float)"/>. With <paramref name="animator"/>, only one it can play.
        /// Null when none fits.
        /// </summary>
        public static string PickIdleEmoteName(bool quickOnly = true, Animator animator = null)
        {
            var pool = new List<PlayerAnimation>();
            foreach (var anim in QuickEmotes) if (anim.IsEmote) pool.Add(anim);
            if (!quickOnly && UnityEngine.Random.value <= 0.3f)
            {
                pool.Clear();
                foreach (var anim in PersistentEmotes) if (anim.IsEmote) pool.Add(anim);
            }
            if (animator != null) pool.RemoveAll(anim => !PlayerAnimationCatalog.Has(animator, anim));
            if (pool.Count == 0) return null;
            return pool[UnityEngine.Random.Range(0, pool.Count)].Parameter.Substring(PlayerAnimationCatalog.EmotePrefix.Length);
        }

        /// <summary>Seconds to the next idle emote, the companions' rule (<see cref="IdleEmoteMinInterval"/>-<see cref="IdleEmoteMaxInterval"/>).</summary>
        public static float NextIdleEmoteDelay() => UnityEngine.Random.Range(IdleEmoteMinInterval, IdleEmoteMaxInterval);

        private static PlayerAnimation PickIdleEmote(Animator animator, bool recentCombat, float quickSeconds, float persistentMin,
            float persistentMax, out float seconds)
        {
            seconds = 0f;
            // Decide between quick emotes vs persistent emotes
            bool useQuickEmote = recentCombat || UnityEngine.Random.value > 0.3f;
            var pool = useQuickEmote ? QuickEmotes : PersistentEmotes;
            if (pool.Count == 0) return null;
            var anim = pool[UnityEngine.Random.Range(0, pool.Count)];
            if (!PlayerAnimationCatalog.Has(animator, anim)) return null;
            if (anim.Kind != PlayerAnimKind.OneShot)
            {
                float minDuration = Mathf.Max(persistentMin, Mathf.Min(anim.Seconds, persistentMax));
                seconds = UnityEngine.Random.Range(minDuration, persistentMax);
            }
            else
            {
                seconds = anim.Seconds > 0f ? anim.Seconds : quickSeconds;
            }
            return anim;
        }

        private void StartIdleEmote()
        {
            bool recentCombat = Time.time - _lastCombatTime < combatCooldownForIdle * 3f;
            var anim = PickIdleEmote(_animator, recentCombat, quickEmoteDuration, persistentEmoteDurationMin, persistentEmoteDurationMax,
                out float duration);
            if (anim == null)
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} no idle emote this model can play - skipping");
                _nextEmoteTime = Time.time + UnityEngine.Random.Range(idleEmoteMinInterval, idleEmoteMaxInterval);
                return;
            }

            string emote = anim.Parameter;
            bool isPersistent = anim.Kind != PlayerAnimKind.OneShot;
            string emoteType = isPersistent ? "persistent" : "quick";

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
