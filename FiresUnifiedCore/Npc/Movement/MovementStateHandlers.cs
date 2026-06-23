using UnityEngine;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Interface for movement state handlers.
    /// Each state in the state machine has a handler that executes its logic.
    /// </summary>
    public interface IMovementStateHandler
    {
        /// <summary>Called when entering this state.</summary>
        void OnEnter(MovementStateContext ctx);
        
        /// <summary>Called every frame while in this state.</summary>
        void OnUpdate(MovementStateContext ctx);
        
        /// <summary>Called when exiting this state.</summary>
        void OnExit(MovementStateContext ctx);
    }
    
    /// <summary>
    /// Base class for state handlers with common functionality.
    /// </summary>
    public abstract class MovementStateHandlerBase : IMovementStateHandler
    {
        protected CompanionCombatMovement Movement { get; private set; }
        protected Character Character { get; private set; }
        protected Rigidbody Rigidbody { get; private set; }
        
        public static bool VerboseLogging = false;
        
        public void Initialize(CompanionCombatMovement movement, Character character, Rigidbody rigidbody)
        {
            Movement = movement;
            Character = character;
            Rigidbody = rigidbody;
        }
        
        public virtual void OnEnter(MovementStateContext ctx) { }
        public abstract void OnUpdate(MovementStateContext ctx);
        public virtual void OnExit(MovementStateContext ctx) { }
        
        protected void StopMovement()
        {
            if (Character != null && (Rigidbody == null || !Rigidbody.isKinematic))
            {
                Character.SetMoveDir(Vector3.zero);
                Character.SetWalk(false);
                Character.SetRun(false);
            }
        }
        
        protected void ZeroVelocity()
        {
            if (Rigidbody != null && !Rigidbody.isKinematic)
            {
                Rigidbody.linearVelocity = new Vector3(0, Rigidbody.linearVelocity.y, 0);
            }
        }
    }
    
    /// <summary>Handler for Disabled state - minimal processing.</summary>
    public class DisabledStateHandler : MovementStateHandlerBase
    {
        public override void OnUpdate(MovementStateContext ctx)
        {
            // Nothing to do - movement is disabled
        }
    }
    
    /// <summary>Handler for Skipped state - only update grounded.</summary>
    public class SkippedStateHandler : MovementStateHandlerBase
    {
        public override void OnUpdate(MovementStateContext ctx)
        {
            // State controller says skip - just update grounded state
            // This is handled by the main Update() method
        }
    }
    
    /// <summary>Handler for EmoteFrozen state - stop all movement.</summary>
    public class EmoteFrozenStateHandler : MovementStateHandlerBase
    {
        public override void OnEnter(MovementStateContext ctx)
        {
            StopMovement();
        }
        
        public override void OnUpdate(MovementStateContext ctx)
        {
            StopMovement();
        }
    }
    
    /// <summary>Handler for MovementLocked state - keep movement zeroed.</summary>
    public class MovementLockedStateHandler : MovementStateHandlerBase
    {
        public override void OnEnter(MovementStateContext ctx)
        {
            StopMovement();
            ZeroVelocity();
        }
        
        public override void OnUpdate(MovementStateContext ctx)
        {
            if (Character != null && (Rigidbody == null || !Rigidbody.isKinematic))
            {
                Character.SetMoveDir(Vector3.zero);
            }
        }
    }
    
    /// <summary>Handler for CombatCooldown state - idle while cooling down.</summary>
    public class CombatCooldownStateHandler : MovementStateHandlerBase
    {
        public override void OnUpdate(MovementStateContext ctx)
        {
            // Apply idle movement mode, let main class handle the rest
        }
    }
}
