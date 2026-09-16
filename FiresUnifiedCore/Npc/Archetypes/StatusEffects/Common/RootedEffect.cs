using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Common
{
    /// <summary>
    /// Rooted effect - completely prevents movement while active.
    /// Used by: Rogue caltrops (on enemies), some boss abilities
    /// 
    /// DESIGN: Character cannot move at all. Can still attack/block if not stunned.
    /// This is applied to ENEMIES, not to companions themselves.
    /// </summary>
    public class RootedEffect : CompanionStatusEffectBase
    {
        /// <summary>
        /// Description shown in tooltip.
        /// </summary>
        public override string Description => "Cannot move or reposition";
        
        public RootedEffect()
        {
            m_name = "Rooted";
            m_tooltip = "Cannot move";
            Duration = 3f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                // Stop all current movement
                var body = m_character.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
                
                m_character.SetMoveDir(Vector3.zero);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[RootedEffect] {m_character.m_name} is now ROOTED for {Duration}s");
                }
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null) return;
            
            // Continuously freeze movement
            var body = m_character.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            
            // Prevent AI-driven movement
            m_character.SetMoveDir(Vector3.zero);
            
            // Also prevent player-controlled movement if applicable
            var player = m_character as Player;
            if (player != null)
            {
                player.SetMoveDir(Vector3.zero);
            }
        }
        
        protected override void OnEffectRemoved()
        {
            // No cleanup needed - velocity resets naturally when effect ends
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[RootedEffect] {m_character.m_name} can move again");
            }
        }
        
        public new RootedEffect Clone()
        {
            var clone = (RootedEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }
}
