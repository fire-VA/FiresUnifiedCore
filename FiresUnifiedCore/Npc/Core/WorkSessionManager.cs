using System;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc.IdleBehaviors;
using FiresCore.Npc.Events;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Tracks a companion's long-running work (smelting, gathering, crafting): progress and statistics, pausing
    /// for combat and resuming afterwards, and a global timeout. Created by BehaviorCoordinator; behaviors call
    /// StartSession, PauseSession, ResumeSession and EndSession.
    /// </summary>
    public class WorkSessionManager
    {
        #region Session Data
        
        /// <summary>
        /// Data about the current work session.
        /// </summary>
        public class SessionData
        {
            public string BehaviorName { get; set; }
            public float StartTime { get; set; }
            public float MaxDuration { get; set; }
            public float TotalPausedTime { get; set; }
            public int ItemsProcessed { get; set; }
            public int ItemsCollected { get; set; }
            public bool WasInterrupted { get; set; }
            public int InterruptCount { get; set; }
            public string LastInterruptReason { get; set; }
            
            // Pause state
            public bool IsPaused { get; set; }
            public float PauseStartTime { get; set; }
            
            /// <summary>
            /// Active time excluding pauses.
            /// </summary>
            public float ActiveTime => IsPaused 
                ? (PauseStartTime - StartTime) - TotalPausedTime
                : (Time.time - StartTime) - TotalPausedTime;
            
            /// <summary>
            /// Time remaining before timeout.
            /// </summary>
            public float TimeRemaining => MaxDuration - ActiveTime;
            
            /// <summary>
            /// Whether the session has timed out.
            /// </summary>
            public bool IsTimedOut => ActiveTime >= MaxDuration;
        }
        
        /// <summary>
        /// Statistics for completed sessions.
        /// </summary>
        public class SessionStatistics
        {
            public int TotalSessions { get; set; }
            public int SuccessfulSessions { get; set; }
            public int FailedSessions { get; set; }
            public int InterruptedSessions { get; set; }
            public float TotalWorkTime { get; set; }
            public int TotalItemsProcessed { get; set; }
            public int TotalItemsCollected { get; set; }
            public Dictionary<string, int> SessionsByBehavior { get; set; } = new Dictionary<string, int>();
        }
        
        #endregion
        
        #region Fields
        
        private readonly BehaviorCoordinator _coordinator;
        private SessionData _currentSession;
        private SessionStatistics _statistics = new SessionStatistics();
        private IdleSubBehavior _currentBehavior;
        
        // Global timeout - no session can run longer than this
        private const float GlobalSessionTimeout = 1800f; // 30 minutes
        
        // Warning threshold - log warning when session runs this long
        private const float SessionWarningThreshold = 600f; // 10 minutes
        
        private bool _warningLogged = false;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// Whether a work session is currently active.
        /// </summary>
        public bool IsInSession => _currentSession != null;
        
        /// <summary>
        /// Whether the current session is paused.
        /// </summary>
        public bool IsPaused => _currentSession?.IsPaused ?? false;
        
        /// <summary>
        /// Time remaining in the current session.
        /// </summary>
        public float SessionTimeRemaining => _currentSession?.TimeRemaining ?? 0f;
        
        /// <summary>
        /// Name of the current activity.
        /// </summary>
        public string CurrentActivity => _currentSession?.BehaviorName ?? "None";
        
        /// <summary>
        /// The current session data (read-only view).
        /// </summary>
        public SessionData CurrentSession => _currentSession;
        
        /// <summary>
        /// Cumulative statistics for all sessions.
        /// </summary>
        public SessionStatistics Statistics => _statistics;
        
        #endregion
        
        #region Events
        
        /// <summary>
        /// Fired when a session starts.
        /// </summary>
        public event Action<SessionData> OnSessionStarted;
        
        /// <summary>
        /// Fired when a session is paused.
        /// </summary>
        public event Action<SessionData, string> OnSessionPaused;
        
        /// <summary>
        /// Fired when a session is resumed.
        /// </summary>
        public event Action<SessionData> OnSessionResumed;
        
        /// <summary>
        /// Fired when a session ends.
        /// Parameters: (session, wasSuccessful, message)
        /// </summary>
        public event Action<SessionData, bool, string> OnSessionEnded;
        
        /// <summary>
        /// Fired when a session times out.
        /// </summary>
        public event Action<SessionData> OnSessionTimeout;
        
        #endregion
        
        #region Constructor
        
        public WorkSessionManager(BehaviorCoordinator coordinator)
        {
            _coordinator = coordinator;
        }
        
        #endregion
        
        #region Session Management
        
        /// <summary>
        /// Starts a new work session.
        /// </summary>
        public void StartSession(IdleSubBehavior behavior, float maxDuration = -1f)
        {
            if (behavior == null) return;
            
            // End any existing session first
            if (_currentSession != null)
            {
                EndSession(false, "New session started");
            }
            
            _currentBehavior = behavior;
            _currentSession = new SessionData
            {
                BehaviorName = behavior.BehaviorName,
                StartTime = Time.time,
                MaxDuration = maxDuration > 0 ? Mathf.Min(maxDuration, GlobalSessionTimeout) : behavior.MaxDuration,
                TotalPausedTime = 0f,
                ItemsProcessed = 0,
                ItemsCollected = 0,
                WasInterrupted = false,
                InterruptCount = 0,
                IsPaused = false
            };
            
            _warningLogged = false;
            
            OnSessionStarted?.Invoke(_currentSession);
            
            if (VerboseLogging)
            {
                Debug.Log($"[WorkSessionManager] Started session: {behavior.BehaviorName}, max duration: {_currentSession.MaxDuration:F0}s");
            }
        }
        
        /// <summary>
        /// Pauses the current session (e.g., for combat).
        /// </summary>
        public void PauseSession(string reason)
        {
            if (_currentSession == null || _currentSession.IsPaused) return;
            
            _currentSession.IsPaused = true;
            _currentSession.PauseStartTime = Time.time;
            _currentSession.WasInterrupted = true;
            _currentSession.InterruptCount++;
            _currentSession.LastInterruptReason = reason;
            
            OnSessionPaused?.Invoke(_currentSession, reason);
            
            if (VerboseLogging)
            {
                Debug.Log($"[WorkSessionManager] Paused session: {_currentSession.BehaviorName} - {reason}");
            }
        }
        
        /// <summary>
        /// Resumes a paused session.
        /// </summary>
        public void ResumeSession()
        {
            if (_currentSession == null || !_currentSession.IsPaused) return;
            
            float pauseDuration = Time.time - _currentSession.PauseStartTime;
            _currentSession.TotalPausedTime += pauseDuration;
            _currentSession.IsPaused = false;
            
            OnSessionResumed?.Invoke(_currentSession);
            
            if (VerboseLogging)
            {
                Debug.Log($"[WorkSessionManager] Resumed session: {_currentSession.BehaviorName} (paused for {pauseDuration:F1}s)");
            }
        }
        
        /// <summary>
        /// Ends the current session.
        /// </summary>
        public void EndSession(bool success, string message = null)
        {
            if (_currentSession == null) return;
            
            // Calculate final stats
            float totalTime = Time.time - _currentSession.StartTime;
            
            // Update statistics
            _statistics.TotalSessions++;
            if (success)
                _statistics.SuccessfulSessions++;
            else
                _statistics.FailedSessions++;
            
            if (_currentSession.WasInterrupted)
                _statistics.InterruptedSessions++;
            
            _statistics.TotalWorkTime += _currentSession.ActiveTime;
            _statistics.TotalItemsProcessed += _currentSession.ItemsProcessed;
            _statistics.TotalItemsCollected += _currentSession.ItemsCollected;
            
            // Track by behavior type
            if (!_statistics.SessionsByBehavior.ContainsKey(_currentSession.BehaviorName))
                _statistics.SessionsByBehavior[_currentSession.BehaviorName] = 0;
            _statistics.SessionsByBehavior[_currentSession.BehaviorName]++;
            
            OnSessionEnded?.Invoke(_currentSession, success, message ?? (success ? "Completed" : "Failed"));
            
            if (VerboseLogging)
            {
                Debug.Log($"[WorkSessionManager] Ended session: {_currentSession.BehaviorName} " +
                    $"(success: {success}, time: {_currentSession.ActiveTime:F1}s, items: {_currentSession.ItemsProcessed})");
            }
            
            _currentSession = null;
            _currentBehavior = null;
        }
        
        #endregion
        
        #region Progress Tracking
        
        /// <summary>
        /// Records that an item was processed (added to station, gathered, etc.)
        /// </summary>
        public void RecordItemProcessed(int count = 1)
        {
            if (_currentSession == null) return;
            _currentSession.ItemsProcessed += count;
        }
        
        /// <summary>
        /// Records that an item was collected (output from station, picked up, etc.)
        /// </summary>
        public void RecordItemCollected(int count = 1)
        {
            if (_currentSession == null) return;
            _currentSession.ItemsCollected += count;
        }
        
        /// <summary>
        /// Extends the session timeout.
        /// </summary>
        public void ExtendSession(float additionalTime)
        {
            if (_currentSession == null) return;
            _currentSession.MaxDuration = Mathf.Min(
                _currentSession.MaxDuration + additionalTime,
                GlobalSessionTimeout
            );
            
            if (VerboseLogging)
            {
                Debug.Log($"[WorkSessionManager] Extended session by {additionalTime:F0}s, new max: {_currentSession.MaxDuration:F0}s");
            }
        }
        
        #endregion
        
        #region Update
        
        /// <summary>
        /// Called each frame by BehaviorCoordinator.
        /// </summary>
        public void Update()
        {
            if (_currentSession == null) return;
            if (_currentSession.IsPaused) return;
            
            // Check for timeout
            if (_currentSession.IsTimedOut)
            {
                Debug.LogWarning($"[WorkSessionManager] Session timed out: {_currentSession.BehaviorName} " +
                    $"after {_currentSession.ActiveTime:F0}s");
                
                OnSessionTimeout?.Invoke(_currentSession);
                
                // Cancel the behavior
                if (_currentBehavior != null && _currentBehavior.IsActive)
                {
                    _currentBehavior.Cancel();
                }
                
                EndSession(false, "Session timed out");
                return;
            }
            
            // Log warning if session is running long
            if (!_warningLogged && _currentSession.ActiveTime > SessionWarningThreshold)
            {
                Debug.LogWarning($"[WorkSessionManager] Session running long: {_currentSession.BehaviorName} " +
                    $"({_currentSession.ActiveTime:F0}s / {_currentSession.MaxDuration:F0}s)");
                _warningLogged = true;
            }
        }
        
        #endregion
        
        #region Debug
        
        /// <summary>
        /// Gets a debug string with current session state.
        /// </summary>
        public string GetDebugString()
        {
            if (_currentSession == null)
            {
                return "[WorkSessionManager] No active session";
            }
            
            return $"[WorkSessionManager] Session: {_currentSession.BehaviorName}\n" +
                $"  Active Time: {_currentSession.ActiveTime:F1}s / {_currentSession.MaxDuration:F0}s\n" +
                $"  Paused: {_currentSession.IsPaused}, Interrupts: {_currentSession.InterruptCount}\n" +
                $"  Items: {_currentSession.ItemsProcessed} processed, {_currentSession.ItemsCollected} collected";
        }
        
        /// <summary>
        /// Gets a debug string with statistics.
        /// </summary>
        public string GetStatisticsString()
        {
            return $"[WorkSessionManager] Statistics:\n" +
                $"  Total Sessions: {_statistics.TotalSessions}\n" +
                $"  Success: {_statistics.SuccessfulSessions}, Failed: {_statistics.FailedSessions}\n" +
                $"  Interrupted: {_statistics.InterruptedSessions}\n" +
                $"  Total Work Time: {_statistics.TotalWorkTime:F0}s\n" +
                $"  Items Processed: {_statistics.TotalItemsProcessed}, Collected: {_statistics.TotalItemsCollected}";
        }
        
        #endregion
    }
}
