using UnityEngine;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Handles smooth state transitions and victory emotes for companions.
    /// Extracted from CompanionCombatMovement to reduce file size.
    /// </summary>
    public class StateTransitionHandler
    {
        private readonly Transform _transform;
        private readonly Character _character;
        private readonly Rigidbody _rigidbody;
        private readonly ZSyncAnimation _zanim;
        private readonly Animator _animator;
        private readonly CompanionController _companion;
        
        // Settings
        public float StateTransitionGracePeriod { get; set; } = 0.5f;
        public float MovementBlendSpeed { get; set; } = 5f;
        public float CombatEndGracePeriod { get; set; } = 3f;
        
        // Transition state
        private bool _isInTransition;
        private float _transitionStartTime;
        private Vector3 _lastMoveDirection;
        
        // Victory emote state
        private bool _hasPlayedVictoryEmote;
        private float _lastEnemyKillTime = -100f;
        private bool _isInCombatCooldown;
        
        // Emote list
        private static readonly string[] VictoryEmotes = {
            "emote_cheer", "emote_flex", "emote_thumbsup", "emote_challenge", "emote_laugh"
        };
        
        public static bool VerboseLogging = false;
        
        // Properties
        public bool IsInTransition => _isInTransition;
        public bool IsInCombatCooldown => _isInCombatCooldown;
        public Vector3 LastMoveDirection => _lastMoveDirection;
        public float TransitionProgress => _isInTransition ? 
            (Time.time - _transitionStartTime) / StateTransitionGracePeriod : 1f;
        
        public StateTransitionHandler(
            Transform transform,
            Character character,
            Rigidbody rigidbody,
            ZSyncAnimation zanim,
            Animator animator,
            CompanionController companion)
        {
            _transform = transform;
            _character = character;
            _rigidbody = rigidbody;
            _zanim = zanim;
            _animator = animator;
            _companion = companion;
        }
        
        /// <summary>Begins a state transition, preserving current movement direction.</summary>
        public void BeginTransition(Vector3 currentMoveDirection)
        {
            _isInTransition = true;
            _transitionStartTime = Time.time;
            _lastMoveDirection = currentMoveDirection;
            
            if (VerboseLogging)
                Debug.Log($"[StateTransitionHandler] {_companion?.companionName} beginning transition");
        }
        
        /// <summary>Ends the current transition.</summary>
        public void EndTransition()
        {
            _isInTransition = false;
            
            if (VerboseLogging)
                Debug.Log($"[StateTransitionHandler] {_companion?.companionName} transition complete");
        }
        
        /// <summary>Checks if the transition grace period has elapsed.</summary>
        public bool ShouldEndTransition()
        {
            return _isInTransition && Time.time > _transitionStartTime + StateTransitionGracePeriod;
        }
        
        /// <summary>Calculates blended movement direction during transition.</summary>
        public Vector3 GetTransitionBlendedDirection(bool toIdle)
        {
            if (!_isInTransition || _lastMoveDirection.sqrMagnitude < 0.1f)
                return Vector3.zero;
            
            float progress = TransitionProgress;
            
            if (toIdle)
                return Vector3.Lerp(_lastMoveDirection, Vector3.zero, progress);
            
            return _lastMoveDirection;
        }
        
        /// <summary>Called when exiting combat to start cooldown.</summary>
        public void OnExitCombat()
        {
            _isInCombatCooldown = true;
            _lastEnemyKillTime = Time.time;
        }
        
        /// <summary>Called when entering combat to reset state.</summary>
        public void OnEnterCombat()
        {
            _isInCombatCooldown = false;
            _hasPlayedVictoryEmote = false;
        }
        
        /// <summary>Checks if combat cooldown has ended and victory emote should play.</summary>
        public bool CheckCombatCooldownEnded()
        {
            if (!_isInCombatCooldown) return false;
            
            if (Time.time > _lastEnemyKillTime + CombatEndGracePeriod)
            {
                _isInCombatCooldown = false;
                return true;
            }
            return false;
        }
        
        /// <summary>Attempts to play a victory emote via coroutine host.</summary>
        public void TryPlayVictoryEmote(MonoBehaviour coroutineHost)
        {
            if (_hasPlayedVictoryEmote) return;
            _hasPlayedVictoryEmote = true;
            
            // 20% chance to skip for variety
            if (Random.value < 0.2f) return;
            
            coroutineHost.StartCoroutine(PlayVictoryEmoteCoroutine());
            
            if (VerboseLogging)
                Debug.Log($"[StateTransitionHandler] {_companion?.companionName} playing victory emote");
        }
        
        private System.Collections.IEnumerator PlayVictoryEmoteCoroutine()
        {
            // Stop movement if not kinematic
            if (_character != null && (_rigidbody == null || !_rigidbody.isKinematic))
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
            
            if (_rigidbody != null && !_rigidbody.isKinematic)
                _rigidbody.linearVelocity = new Vector3(0, _rigidbody.linearVelocity.y, 0);

            // Wait for physics
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();

            // Stop again
            if (_character != null && (_rigidbody == null || !_rigidbody.isKinematic))
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }

            if (_rigidbody != null && !_rigidbody.isKinematic)
                _rigidbody.linearVelocity = new Vector3(0, _rigidbody.linearVelocity.y, 0);
            
            yield return null;
            
            // Play random emote
            string emote = VictoryEmotes[Random.Range(0, VictoryEmotes.Length)];
            
            if (_zanim != null)
            {
                _zanim.SetTrigger(emote);
            }
            else if (_animator != null && HasAnimatorParameter(emote))
            {
                _animator.SetTrigger(emote);
            }
            
            if (VerboseLogging)
                Debug.Log($"[StateTransitionHandler] {_companion?.companionName} celebrates with {emote}!");
        }
        
        private bool HasAnimatorParameter(string paramName)
        {
            if (_animator == null) return false;
            foreach (var param in _animator.parameters)
                if (param.name == paramName) return true;
            return false;
        }
        
        /// <summary>Called when enemy is killed to track time.</summary>
        public void OnEnemyKilled()
        {
            _lastEnemyKillTime = Time.time;
        }
        
        /// <summary>Resets victory emote flag (call when re-entering combat).</summary>
        public void ResetVictoryEmote()
        {
            _hasPlayedVictoryEmote = false;
        }
    }
}
