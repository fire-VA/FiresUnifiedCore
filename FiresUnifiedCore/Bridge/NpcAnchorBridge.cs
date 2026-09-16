using System;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// The link between Core's <see cref="FiresCore.Npc.Anchor.NpcAnchorKeeper"/> and the frontend's NPC data. The
    /// frontend registers <see cref="SeedBody"/>, which copies appearance and profiles from the anchor onto a newly
    /// spawned body, and <see cref="CaptureToAnchor"/>, which saves a live body's back to its anchor. Without a provider
    /// every call is a safe no-op.
    /// </summary>
    public static class NpcAnchorBridge
    {
        /// <summary>(anchorZdo, bodyZdo) — write the frontend payload onto the new body.</summary>
        public static Action<ZDO, ZDO> SeedBody;

        /// <summary>(bodyZdo, anchorZdo) — snapshot the live body's frontend payload onto the anchor.</summary>
        public static Action<ZDO, ZDO> CaptureToAnchor;

        public static void InvokeSeedBody(ZDO anchorZdo, ZDO bodyZdo)
        {
            if (anchorZdo == null || bodyZdo == null) return;
            try { SeedBody?.Invoke(anchorZdo, bodyZdo); }
            catch (Exception ex) { Debug.LogWarning($"[FiresCore] NpcAnchorBridge.SeedBody failed: {ex.Message}"); }
        }

        public static void InvokeCaptureToAnchor(ZDO bodyZdo, ZDO anchorZdo)
        {
            if (bodyZdo == null || anchorZdo == null) return;
            try { CaptureToAnchor?.Invoke(bodyZdo, anchorZdo); }
            catch (Exception ex) { Debug.LogWarning($"[FiresCore] NpcAnchorBridge.CaptureToAnchor failed: {ex.Message}"); }
        }
    }
}
