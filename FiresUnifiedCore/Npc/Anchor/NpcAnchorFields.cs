using UnityEngine;

namespace FiresCore.Npc.Anchor
{
    /// <summary>
    /// ZDO field names + tiny accessors for the NPC anchor system. The ANCHOR is the NPC's durable
    /// in-world record (a tiny invisible persistent-ZDO piece); its keeper guarantees the body exists
    /// whenever the anchor's zone is loaded. Bodies are disposable — deleting the anchor deletes the
    /// NPC forever. Field ownership: everything the KEEPER itself needs lives here (Core); frontend
    /// appearance/profile payloads ride the same anchor ZDO but are read/written via
    /// <see cref="FiresCore.Bridge.NpcAnchorBridge"/> so Core never learns frontend field lists.
    /// </summary>
    public static class NpcAnchorFields
    {
        public const string AnchorPrefabName = "FiresNpcAnchor";

        /// <summary>Stable identity shared by anchor + its current body ("npc_anchor_guid" on the body).
        /// Survives save/load and ZDOID churn — the re-link scan matches on this, never on ZDOIDs.</summary>
        public const string Guid = "fires_anchor_guid";
        /// <summary>Same value written on the BODY so a broken ZDO connection can be re-linked.</summary>
        public const string BodyGuidBackRef = "npc_anchor_guid";

        /// <summary>Body prefab to spawn (default "StaticNpc").</summary>
        public const string BodyPrefab = "fires_anchor_body_prefab";
        /// <summary>Body facing at placement.</summary>
        public const string Rotation = "fires_anchor_rot";
        /// <summary>Wall-clock (DateTime.UtcNow.Ticks) when the keeper first noticed the body missing.
        /// 0 = body present (or never checked). Restart-safe respawn timing.</summary>
        public const string MissingSince = "fires_anchor_missing_since";
        /// <summary>Where the body died last (stamped by the death hook). Used by Patrol mode to pick
        /// the closest route checkpoint; cleared after each respawn.</summary>
        public const string DeathPos = "fires_anchor_death_pos";

        // Mode fields — deliberately the SAME names the body uses so the book's config writes translate 1:1.
        public const string AllowIdleWander = "npc_allow_idle_wander";
        public const string PatrolRoute = "npc_patrol_route";
        public const string RespawnDelay = "npc_respawn_delay";

        public enum Mode { Static, Wander, Patrol }

        public static Mode GetMode(ZDO anchorZdo)
        {
            if (anchorZdo == null) return Mode.Static;
            if (!string.IsNullOrEmpty(anchorZdo.GetString(PatrolRoute, ""))) return Mode.Patrol;
            return anchorZdo.GetBool(AllowIdleWander, false) ? Mode.Wander : Mode.Static;
        }
    }
}
