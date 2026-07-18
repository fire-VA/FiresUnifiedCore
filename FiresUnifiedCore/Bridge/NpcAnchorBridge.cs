using System;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// The seam between Core's <see cref="FiresCore.Npc.Anchor.NpcAnchorKeeper"/> (anchor lifecycle:
    /// presence check, re-link, respawn timing, spawn) and the frontend's NPC data model (appearance +
    /// module profiles). Mirrors the <see cref="NpcDormancyBridge"/> contract: Core never references a
    /// concrete frontend store; while no provider is registered every call is a null-safe no-op.
    ///
    /// The frontend (FiresRPGmaker / FiresCompanions) registers:
    ///  • <see cref="SeedBody"/> — copy appearance + profile payload from the anchor ZDO onto a freshly
    ///    spawned body's ZDO (called AFTER Core has written identity/mode fields, BEFORE the body's
    ///    initializer coroutine restores from it).
    ///  • <see cref="CaptureToAnchor"/> — snapshot a live body's appearance + profiles onto its anchor
    ///    ZDO (called when the admin book saves config, or before a deliberate body replace).
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
