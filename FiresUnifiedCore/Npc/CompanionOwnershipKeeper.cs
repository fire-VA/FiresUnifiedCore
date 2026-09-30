using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Keeps a companion's ZDO on its owner's peer while the owner is near. A companion simulates only on its ZDO owner, and that is
    /// where its owner's hits are seen (OwnerAssist); vanilla's ReleaseNearbyZDOS hands a ZDO to another covering peer whenever the
    /// owner's active area briefly misses it (a teleport, a drill hop), and nothing handed it back (R68: "[Drill] took back
    /// FiresBot.fighter (its ZDO was owned by another peer)"). FGN never claims or moves tamed ZDOs ([fgn] 2026-09-29), so there is no
    /// tug-of-war. Every <see cref="Interval"/> s on the owner's own machine: each of its companions within <see cref="Radius"/> m
    /// (inside the owner's guaranteed zone coverage) that this peer does not own is claimed back; never while the owner teleports.
    /// </summary>
    internal static class CompanionOwnershipKeeper
    {
        private const float Interval = 2f, Radius = 64f;
        private static float s_next;

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Player_Update_Keep
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || Time.time < s_next) return;
                s_next = Time.time + Interval;
                try { Keep(__instance); } catch { }
            }
        }

        private static void Keep(Player owner)
        {
            if (owner.IsTeleporting() || owner.IsDead() || CompanionPatches.AreCompanionTeleportsSuppressed()) return;
            long ownerId = owner.GetPlayerID();
            Vector3 at = owner.transform.position;
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null || companion.ownerPlayerId != ownerId) continue;
                ZNetView view = companion.GetComponent<ZNetView>();
                if (view == null || !view.IsValid() || view.IsOwner()) continue;
                if (Vector3.Distance(at, companion.transform.position) > Radius) continue;
                long previous = view.GetZDO().GetOwner();
                view.ClaimOwnership();
                Debug.Log($"[CompanionOwnership] {companion.companionName} reclaimed from peer {previous} "
                          + $"({Vector3.Distance(at, companion.transform.position):0} m from its owner)");
            }
        }
    }
}
