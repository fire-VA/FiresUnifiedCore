using System;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Cross-mod player-progress events, raised CLIENT-side by the mod that owns the mechanic and consumed
    /// by optional presentation mods (e.g. FiresDiscordIntegration's screenshot-and-post pipeline). Same
    /// optional-coupling model as <see cref="DiscordSink"/>: producers raise unconditionally; when nobody
    /// subscribes, nothing happens — so FiresRPGmaker never needs a reference to the Discord mod and the
    /// Discord mod never needs one back.
    /// </summary>
    public static class ProgressEvents
    {
        /// <summary>The local player completed an achievement: (achievementName, description).</summary>
        public static event Action<string, string> AchievementCompleted;

        /// <summary>Producer side (FiresRPGmaker): announce a completed achievement. Subscriber exceptions are swallowed.</summary>
        public static void RaiseAchievementCompleted(string name, string description)
        {
            try { AchievementCompleted?.Invoke(name ?? string.Empty, description ?? string.Empty); }
            catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ProgressEvents] AchievementCompleted subscriber threw: {ex.Message}"); }
        }

        /// <summary>
        /// A mod asks for a captioned screenshot to be captured and posted: (reason). Same optional-coupling
        /// model as <see cref="AchievementCompleted"/> — the producer neither knows nor cares whether a
        /// Discord mod is installed. Raised CLIENT-side; a headless caller has no screen to capture.
        /// </summary>
        public static event Action<string> ScreenshotRequested;

        /// <summary>Producer side (any mod): ask for a captioned screenshot post. Subscriber exceptions are swallowed.</summary>
        public static void RaiseScreenshotRequested(string reason)
        {
            try { ScreenshotRequested?.Invoke(reason ?? string.Empty); }
            catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ProgressEvents] ScreenshotRequested subscriber threw: {ex.Message}"); }
        }
    }
}
