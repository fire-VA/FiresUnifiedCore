using System;

namespace FiresCore.Bridge
{
    /// <summary>
    /// General companion-state hooks the Marketplace base can query without a hard reference
    /// to the companion mod. The companion mod assigns these delegates on init; while it is
    /// absent every query returns a safe default. (NPC-framework-specific hooks live in
    /// <see cref="NpcCompanionBridge"/>; UI-screen registration in <see cref="ModUiRegistry"/>.)
    /// </summary>
    public static class CompanionBridge
    {
        /// <summary>
        /// True while companion teleports are suppressed (the zone-stream spawn gate). The
        /// Compendium uses this to avoid recording discoveries during the closed window.
        /// </summary>
        public static Func<bool> TeleportsSuppressed;

        public static bool AreTeleportsSuppressed()
        {
            try { return TeleportsSuppressed?.Invoke() ?? false; } catch { return false; }
        }
    }
}
