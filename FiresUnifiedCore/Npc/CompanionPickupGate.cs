using System;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Companions obey the
    /// same no-pickup zones as players. The territory owner (RPGmaker) registers <see cref="IsBlocked"/>; every companion pickup asks it
    /// before taking a ground drop (ChestHelper.TakeDropVerified, the one place a companion destroys a drop, plus the auto-pickup scan).
    /// Unset = nothing blocked (Core alone). Add-only API.
    /// </summary>
    public static class CompanionPickupGate
    {
        /// <summary>(drop position, the companion's owner player id or 0) -> true when picking up there is not allowed.</summary>
        public static Func<Vector3, long, bool> IsBlocked;

        private static float s_logAt;
        internal static bool Blocked(Vector3 pos, long owner, string who, string item)
        {
            var f = IsBlocked; if (f == null) return false;
            bool b; try { b = f(pos, owner); } catch (Exception ex) { Debug.LogWarning("[CompanionPickupGate] IsBlocked threw: " + ex.Message); return false; }
            if (b && Time.time >= s_logAt) { s_logAt = Time.time + 5f; FiresCore.Logging.FiresLogger.LogInfo($"[CompanionInventory] {who} pickup refused: {item} lies in a no-pickup territory"); }
            return b;
        }
    }
}