using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Companion and NPC effects are only worth creating where a player can see or hear them. Vanilla weapon and
    /// impact sounds fall silent between 20 and 40 m, but some effects reused by abilities carry 120 m to 1 km
    /// (boss roars, thunder), and a companion fighting at a loaded but distant base was heard as if beside you.
    /// </summary>
    public static class NpcFxRange
    {
        public const float AudibleRange = 40f;

        /// <summary>For effects each client spawns itself (routed-RPC broadcasts): is this machine's player close?
        /// A dedicated server has no player and never needs them.</summary>
        public static bool NearLocalPlayer(Vector3 position, float range = AudibleRange)
        {
            var player = Player.m_localPlayer;
            return player != null && (player.transform.position - position).sqrMagnitude <= range * range;
        }

        /// <summary>For networked effects created once and streamed to other clients: is any loaded player close?</summary>
        public static bool NearAnyPlayer(Vector3 position, float range = AudibleRange)
        {
            float rangeSqr = range * range;
            foreach (var player in Player.GetAllPlayers())
                if (player != null && (player.transform.position - position).sqrMagnitude <= rangeSqr)
                    return true;
            return false;
        }
    }
}
