using System;
using UnityEngine;

namespace FiresCore.Npc.Events
{
    /// <summary>
    /// Centralized event system for companion behaviors.
    /// Use events for cross-cutting concerns instead of direct method calls.
    /// 
    /// This allows loose coupling between components and makes it easy to add
    /// new functionality that responds to companion actions.
    /// 
    /// USAGE:
    /// - Subscribe: CompanionEvents.OnBehaviorStarted += MyHandler;
    /// - Unsubscribe: CompanionEvents.OnBehaviorStarted -= MyHandler;
    /// - Fire: CompanionEvents.FireBehaviorStarted(companion, behavior);
    /// 
    /// IMPORTANT: Always unsubscribe in OnDestroy to prevent memory leaks!
    /// </summary>
    public static class CompanionEvents
    {
        #region Behavior Lifecycle Events
        
        /// <summary>
        /// Fired when a sub-behavior starts.
        /// Parameters: CompanionController, IdleSubBehavior
        /// </summary>
        public static event Action<CompanionController, IdleBehaviors.IdleSubBehavior> OnBehaviorStarted;
        
        /// <summary>
        /// Fired when a sub-behavior completes (success or failure).
        /// Parameters: CompanionController, IdleSubBehavior, wasSuccessful
        /// </summary>
        public static event Action<CompanionController, IdleBehaviors.IdleSubBehavior, bool> OnBehaviorCompleted;
        
        /// <summary>
        /// Fired when a sub-behavior is cancelled/interrupted.
        /// Parameters: CompanionController, IdleSubBehavior, reason
        /// </summary>
        public static event Action<CompanionController, IdleBehaviors.IdleSubBehavior, string> OnBehaviorCancelled;
        
        public static void FireBehaviorStarted(CompanionController companion, IdleBehaviors.IdleSubBehavior behavior)
        {
            try { OnBehaviorStarted?.Invoke(companion, behavior); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnBehaviorStarted handler: {ex}"); }
        }
        
        public static void FireBehaviorCompleted(CompanionController companion, IdleBehaviors.IdleSubBehavior behavior, bool success)
        {
            try { OnBehaviorCompleted?.Invoke(companion, behavior, success); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnBehaviorCompleted handler: {ex}"); }
        }
        
        public static void FireBehaviorCancelled(CompanionController companion, IdleBehaviors.IdleSubBehavior behavior, string reason)
        {
            try { OnBehaviorCancelled?.Invoke(companion, behavior, reason); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnBehaviorCancelled handler: {ex}"); }
        }
        
        #endregion
        
        #region Combat Events
        
        /// <summary>
        /// Fired when a companion enters combat.
        /// Parameters: CompanionController
        /// </summary>
        public static event Action<CompanionController> OnCombatStarted;
        
        /// <summary>
        /// Fired when a companion exits combat.
        /// Parameters: CompanionController
        /// </summary>
        public static event Action<CompanionController> OnCombatEnded;
        
        /// <summary>
        /// Fired when a companion targets an enemy.
        /// Parameters: CompanionController, targetCreature
        /// </summary>
        public static event Action<CompanionController, Character> OnTargetAcquired;
        
        /// <summary>
        /// Fired when a companion loses their target.
        /// Parameters: CompanionController
        /// </summary>
        public static event Action<CompanionController> OnTargetLost;
        
        public static void FireCombatStarted(CompanionController companion)
        {
            try { OnCombatStarted?.Invoke(companion); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnCombatStarted handler: {ex}"); }
        }
        
        public static void FireCombatEnded(CompanionController companion)
        {
            try { OnCombatEnded?.Invoke(companion); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnCombatEnded handler: {ex}"); }
        }
        
        public static void FireTargetAcquired(CompanionController companion, Character target)
        {
            try { OnTargetAcquired?.Invoke(companion, target); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnTargetAcquired handler: {ex}"); }
        }
        
        public static void FireTargetLost(CompanionController companion)
        {
            try { OnTargetLost?.Invoke(companion); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnTargetLost handler: {ex}"); }
        }
        
        #endregion
        
        #region Animation Events
        
        /// <summary>
        /// Fired when an emote starts.
        /// Parameters: CompanionController, emoteName
        /// </summary>
        public static event Action<CompanionController, string> OnEmoteStarted;
        
        /// <summary>
        /// Fired when an emote ends (naturally or forced).
        /// Parameters: CompanionController, emoteName, wasForced
        /// </summary>
        public static event Action<CompanionController, string, bool> OnEmoteEnded;
        
        public static void FireEmoteStarted(CompanionController companion, string emoteName)
        {
            try { OnEmoteStarted?.Invoke(companion, emoteName); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnEmoteStarted handler: {ex}"); }
        }
        
        public static void FireEmoteEnded(CompanionController companion, string emoteName, bool wasForced)
        {
            try { OnEmoteEnded?.Invoke(companion, emoteName, wasForced); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnEmoteEnded handler: {ex}"); }
        }
        
        #endregion
        
        #region Resource Events
        
        /// <summary>
        /// Fired when a companion gathers resources.
        /// Parameters: CompanionController, itemPrefabName, amount
        /// </summary>
        public static event Action<CompanionController, string, int> OnResourceGathered;
        
        /// <summary>
        /// Fired when a companion deposits items to chests.
        /// Parameters: CompanionController, itemPrefabName, amount
        /// </summary>
        public static event Action<CompanionController, string, int> OnItemDeposited;
        
        /// <summary>
        /// Fired when a companion pulls items from chests.
        /// Parameters: CompanionController, itemPrefabName, amount
        /// </summary>
        public static event Action<CompanionController, string, int> OnItemPulled;
        
        public static void FireResourceGathered(CompanionController companion, string prefabName, int amount)
        {
            try { OnResourceGathered?.Invoke(companion, prefabName, amount); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnResourceGathered handler: {ex}"); }
        }
        
        public static void FireItemDeposited(CompanionController companion, string prefabName, int amount)
        {
            try { OnItemDeposited?.Invoke(companion, prefabName, amount); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnItemDeposited handler: {ex}"); }
        }
        
        public static void FireItemPulled(CompanionController companion, string prefabName, int amount)
        {
            try { OnItemPulled?.Invoke(companion, prefabName, amount); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnItemPulled handler: {ex}"); }
        }
        
        #endregion
        
        #region State Events
        
        /// <summary>
        /// Fired when a companion's primary state changes.
        /// Parameters: CompanionController, oldState, newState
        /// </summary>
        public static event Action<CompanionController, string, string> OnStateChanged;
        
        /// <summary>
        /// Fired when a companion is told to stay.
        /// Parameters: CompanionController, stayPosition
        /// </summary>
        public static event Action<CompanionController, Vector3> OnCompanionStay;
        
        /// <summary>
        /// Fired when a companion is told to follow.
        /// Parameters: CompanionController
        /// </summary>
        public static event Action<CompanionController> OnCompanionFollow;
        
        public static void FireStateChanged(CompanionController companion, string oldState, string newState)
        {
            try { OnStateChanged?.Invoke(companion, oldState, newState); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnStateChanged handler: {ex}"); }
        }
        
        public static void FireCompanionStay(CompanionController companion, Vector3 position)
        {
            try { OnCompanionStay?.Invoke(companion, position); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnCompanionStay handler: {ex}"); }
        }
        
        public static void FireCompanionFollow(CompanionController companion)
        {
            try { OnCompanionFollow?.Invoke(companion); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnCompanionFollow handler: {ex}"); }
        }
        
        #endregion
        
        #region Work Station Events
        
        /// <summary>
        /// Fired when a companion starts operating a work station.
        /// Parameters: CompanionController, stationGameObject, stationType
        /// </summary>
        public static event Action<CompanionController, GameObject, string> OnWorkStationStarted;
        
        /// <summary>
        /// Fired when a companion finishes operating a work station.
        /// Parameters: CompanionController, stationGameObject, itemsAdded, itemsCollected
        /// </summary>
        public static event Action<CompanionController, GameObject, int, int> OnWorkStationCompleted;
        
        public static void FireWorkStationStarted(CompanionController companion, GameObject station, string stationType)
        {
            try { OnWorkStationStarted?.Invoke(companion, station, stationType); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnWorkStationStarted handler: {ex}"); }
        }
        
        public static void FireWorkStationCompleted(CompanionController companion, GameObject station, int itemsAdded, int itemsCollected)
        {
            try { OnWorkStationCompleted?.Invoke(companion, station, itemsAdded, itemsCollected); }
            catch (Exception ex) { Debug.LogError($"[CompanionEvents] Error in OnWorkStationCompleted handler: {ex}"); }
        }
        
        #endregion
        
        #region Utility
        
        /// <summary>
        /// Clears all event subscribers. Use with caution - mainly for testing.
        /// </summary>
        public static void ClearAllSubscribers()
        {
            OnBehaviorStarted = null;
            OnBehaviorCompleted = null;
            OnBehaviorCancelled = null;
            OnCombatStarted = null;
            OnCombatEnded = null;
            OnTargetAcquired = null;
            OnTargetLost = null;
            OnEmoteStarted = null;
            OnEmoteEnded = null;
            OnResourceGathered = null;
            OnItemDeposited = null;
            OnItemPulled = null;
            OnStateChanged = null;
            OnCompanionStay = null;
            OnCompanionFollow = null;
            OnWorkStationStarted = null;
            OnWorkStationCompleted = null;
        }
        
        #endregion
    }
}
