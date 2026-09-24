using UnityEngine;
using FiresCore.Bridge;

namespace FiresCore.Npc.Archetypes.Effects
{
    /// <summary>
    /// Decides, on each viewer's own client, whether a ring is drawn and how strongly. Every client runs the cast's
    /// routed RPC, so the same cast legitimately produces a green ring for an ally and a red one for a hostile player.
    /// A caster id of 0 means no owning player (a wild creature or a monster), and those rings are drawn for everyone -
    /// a telegraph is information every viewer needs.
    ///
    /// Audience, by kind and by phase. "placed" means the cast has committed; "aiming" is the caster's own preview.
    ///
    ///                     caster            ally / party   unrelated      hostile (both PvP on)
    ///   Heal   aiming     yes               yes            no             no
    ///   Heal   placed     yes               yes            no             no
    ///   Buff   aiming     yes               yes            no             no
    ///   Buff   placed     yes               yes            no             no
    ///   Damage aiming     yes, full         no             no             NO
    ///   Damage placed     yes, faint*       no             no             YES
    ///
    ///   * 0.35 opacity, or hidden entirely when ShowOwnDamageRings is off.
    ///   Caster id 0 (monster / wild): every kind, every phase, everyone.
    ///   With no group provider registered an "unrelated" player with PvP off counts as an ally, so they do see
    ///   heal and buff rings. See Docs/PLAN_GroundRings.md.
    ///
    /// A placed damage area is visible to anyone it can hurt so PvP players can avoid it. An aiming preview is not
    /// a telegraph and stays with the caster, so aiming never widens the audience.
    /// </summary>
    public static class GroundRingVisibility
    {
        private const string LogPrefix = "[GroundRings] ";
        private const float FullOpacity = 1f;
        private const float CasterCommittedDamageOpacity = 0.35f;

        private static bool _allySourcesReported;

        public static bool ShouldShow(GroundRingKind kind, long casterPlayerId, bool aiming, out float opacityScale)
        {
            opacityScale = FullOpacity;

            var viewer = Player.m_localPlayer;
            if (viewer == null) return false;

            if (casterPlayerId == 0L) return true;

            long viewerId = viewer.GetPlayerID();
            bool viewerIsCaster = viewerId == casterPlayerId;
            bool allied = viewerIsCaster || AreAllied(viewer, viewerId, casterPlayerId);

            switch (kind)
            {
                case GroundRingKind.Heal:
                case GroundRingKind.Buff:
                    return allied;

                case GroundRingKind.Damage:
                    if (viewerIsCaster)
                    {
                        if (aiming) return true;
                        if (!GroundRingConfig.OwnDamageRingsVisible) return false;
                        opacityScale = CasterCommittedDamageOpacity;
                        return true;
                    }
                    if (aiming) return false;
                    if (allied) return false;
                    return IsHostileTo(viewer, casterPlayerId);
            }

            Debug.LogError($"[GroundRingVisibility] No visibility rule for ring kind {kind}");
            return false;
        }

        /// <summary>
        /// Grouped OR guilded. The two bridges are independent ally sources, so a registered guild provider must not
        /// suppress the group stand-in: only a registered GROUP provider retires it. With no group provider there is
        /// no party concept Core can see, and the agreed stand-in is "everyone with PvP off is on your side".
        /// </summary>
        private static bool AreAllied(Player viewer, long viewerId, long casterPlayerId)
        {
            ReportAllySourcesOnce();

            if (GroupBridge.AreGrouped(viewerId, casterPlayerId)) return true;
            if (GuildBridge.AreGuilded(viewerId, casterPlayerId)) return true;
            if (GroupBridge.HasProvider) return false;

            var caster = FindPlayer(casterPlayerId);
            return caster != null && !viewer.IsPVPEnabled() && !caster.IsPVPEnabled();
        }

        /// <summary>A hostile player is one the cast can actually hurt: both sides PvP on, and not allied.</summary>
        private static bool IsHostileTo(Player viewer, long casterPlayerId)
        {
            if (!viewer.IsPVPEnabled()) return false;
            var caster = FindPlayer(casterPlayerId);
            return caster != null && caster.IsPVPEnabled();
        }

        /// <summary>
        /// One line per session naming which ally sources are live, so a degraded visibility table is readable from a
        /// log instead of being inferred from behaviour.
        /// </summary>
        private static void ReportAllySourcesOnce()
        {
            if (_allySourcesReported) return;
            _allySourcesReported = true;

            bool groups = GroupBridge.HasProvider;
            bool guilds = GuildBridge.HasProvider;

            if (groups)
            {
                LogInfo($"{LogPrefix}ally sources: groups YES, guilds {(guilds ? "YES" : "no")}");
                return;
            }

            LogWarning($"{LogPrefix}ally sources: groups NO (nothing called GroupBridge.Register), " +
                       $"guilds {(guilds ? "YES" : "no")} - party ring visibility falls back to 'PvP off = allied'");
            LogWarning($"{LogPrefix}while that fallback is in use, party members with PvP ON see each other's " +
                       "damage rings and miss each other's heal/buff rings. Register an IGroupProvider to fix");
        }

        private static void LogInfo(string message)
        {
            if (FiresUnifiedCore.Log != null) FiresUnifiedCore.Log.LogInfo(message);
            else Debug.Log(message);
        }

        private static void LogWarning(string message)
        {
            if (FiresUnifiedCore.Log != null) FiresUnifiedCore.Log.LogWarning(message);
            else Debug.LogWarning(message);
        }

        private static Player FindPlayer(long playerId)
        {
            var players = Player.GetAllPlayers();
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player != null && player.GetPlayerID() == playerId) return player;
            }
            return null;
        }
    }
}
