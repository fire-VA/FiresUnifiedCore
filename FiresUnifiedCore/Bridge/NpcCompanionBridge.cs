using System;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Hook points where the Marketplace NPC framework (NpcController, the NPC interaction
    /// panel, NPC vis-equipment) hands off to the optional companion mod. The companion mod
    /// assigns these delegates on init; while it is absent every delegate is null and the
    /// framework behaves as a plain Marketplace NPC host — no companion sync, no companion
    /// eye glow. Keeps the Marketplace mod free of any hard companion reference; the companion
    /// mod's handler reaches back into the NPC types it needs via its own soft dependency.
    /// </summary>
    public static class NpcCompanionBridge
    {
        /// <summary>Sync the NPC's just-saved profile into a companion module on the same object, if any.</summary>
        public static Action<GameObject> SyncSavedProfile;

        /// <summary>Propagate a display-name change to the companion component (billboard / EnemyHud name).</summary>
        public static Action<GameObject, string> SetDisplayName;

        /// <summary>Companion eye-emission texture for NPC eye rendering; null when no companion mod / texture.</summary>
        public static Func<Texture> EyeEmissionTexture;

        public static void RaiseSyncSavedProfile(GameObject npc)
        {
            try { SyncSavedProfile?.Invoke(npc); } catch { }
        }

        public static void RaiseSetDisplayName(GameObject npc, string displayName)
        {
            try { SetDisplayName?.Invoke(npc, displayName); } catch { }
        }

        public static Texture GetEyeEmissionTexture()
        {
            try { return EyeEmissionTexture?.Invoke(); } catch { return null; }
        }

        /// <summary>The companion module's idle-wander state for this NPC, or null if no companion mod / module manages it.</summary>
        public static Func<GameObject, bool?> IdleWanderState;

        /// <summary>Construct/toggle the companion idle-wander behavior on this NPC (no-op without the companion mod).</summary>
        public static Action<GameObject, bool> ApplyIdleWanderBehavior;

        public static bool? GetIdleWander(GameObject npc)
        {
            try { return IdleWanderState?.Invoke(npc); } catch { return null; }
        }

        public static void RaiseApplyIdleWander(GameObject npc, bool on)
        {
            try { ApplyIdleWanderBehavior?.Invoke(npc, on); } catch { }
        }

        /// <summary>
        /// Overrides who counts as allied for companion damage and targeting. A social mod that owns
        /// both groups and guilds itself (TerresDeViking) assigns this to impose its own answer.
        /// Leave it null and <see cref="AreOwnersAllied"/> answers from the Core social bridges instead.
        /// </summary>
        public static Func<long, long, bool> OwnersAllied;

        /// <summary>
        /// True when two companion owners are on the same side and should be immune to each other's
        /// companion damage. Same owner is always allied. Otherwise a host-assigned
        /// <see cref="OwnersAllied"/> decides, and with none assigned party membership
        /// (<see cref="GroupBridge"/>) or guild membership (<see cref="GuildBridge"/>) does - both
        /// null-safe, so a stack with neither registered falls back to same-owner-only.
        /// </summary>
        public static bool AreOwnersAllied(long ownerA, long ownerB)
        {
            if (ownerA == 0L || ownerB == 0L) return false;
            if (ownerA == ownerB) return true;

            var resolver = OwnersAllied;
            if (resolver != null)
            {
                try { return resolver(ownerA, ownerB); } catch { return false; }
            }

            return GroupBridge.AreGrouped(ownerA, ownerB) || GuildBridge.AreGuilded(ownerA, ownerB);
        }
    }
}
