using System;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Read-only probes against the optional Marketplace NPC interaction component (the host mod's
    /// "Press-E" NpcController), which is NOT referenced by Core. The host registers these on init;
    /// while it is absent every probe returns false — a standalone NPC simply has no Marketplace
    /// interaction component. Lets the Core NPC engine ask "is this a host NPC?" / "is a model
    /// override pending?" without a hard reference to the host type.
    /// </summary>
    public static class NpcInteractionBridge
    {
        /// <summary>True when the object carries the host's interaction component (NpcController).</summary>
        public static Func<GameObject, bool> HasController;

        /// <summary>True when the object's interaction component has a model override pending (gates vis-equipment init).</summary>
        public static Func<GameObject, bool> ModelOverridePending;

        public static bool HasInteractionController(GameObject npc)
        {
            try { return HasController != null && HasController(npc); } catch { return false; }
        }

        public static bool HasPendingModelOverride(GameObject npc)
        {
            try { return ModelOverridePending != null && ModelOverridePending(npc); } catch { return false; }
        }

        // ── Static-placement NPC interaction (Marketplace quest/info/dialogue/trader NPCs) ──
        /// <summary>Hover text from the host interaction component for a static-placement NPC; null if none.</summary>
        public static Func<GameObject, string> HoverText;

        /// <summary>Route a Press-E interaction to the host interaction component; null when unhandled / no component.</summary>
        public static Func<GameObject, Humanoid, bool, bool, bool?> StaticInteract;

        public static string GetHoverText(GameObject npc)
        {
            try { return HoverText?.Invoke(npc); } catch { return null; }
        }

        public static bool? InteractStatic(GameObject npc, Humanoid user, bool hold, bool alt)
        {
            try { return StaticInteract?.Invoke(npc, user, hold, alt); } catch { return null; }
        }
    }
}
