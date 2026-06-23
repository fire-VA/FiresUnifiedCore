using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Authoritative registry of which companions are currently set to follow,
    /// stored on the OWNER PLAYER'S ZDO (not the companion's ZDO).
    ///
    /// Why on the player's ZDO and not the companion's:
    ///   The companion's ZDO can be unloaded, destroyed, or temporarily
    ///   inaccessible (combat respawn, zone unload, glitch recovery), and any
    ///   transient runtime state can mutate fields on it.  The player's ZDO is
    ///   always loaded whenever the owner is in game and is bundled into the
    ///   character save (.fch), so it survives logout / login intact.
    ///
    /// Mutation rule:
    ///   The set is ONLY modified by explicit owner commands ï¿½
    ///   CommandFollow / CommandStay / ForceDismiss / SetFollowMode / radial-menu
    ///   Follow / Stay / Dismiss.  No AI state, death handler, vault save,
    ///   inventory change, glitch recovery, mid-teleport hook, or any other
    ///   transient path is allowed to call SetFollowing.  Period.
    /// </summary>
    public static class PlayerFollowingRegistry
    {
        // Stored as a comma-separated list of companion IDs on the player's ZDO.
        // We use a string list rather than per-id booleans so the entry survives
        // ZDO key compaction and can be migrated wholesale.
        private const string ZDO_KEY = "va_following_companions";

        /// <summary>
        /// Is this companion (by ID) currently flagged as following the given
        /// player?  Returns false if the player is null, the ID is empty, or
        /// the player's ZDO doesn't list this companion.
        /// </summary>
        public static bool IsFollowing(Player player, string companionId)
        {
            if (player == null || string.IsNullOrEmpty(companionId)) return false;
            var ids = ReadIds(player);
            return ids.Contains(companionId);
        }

        /// <summary>
        /// Sets / clears the following flag for this companion on the given
        /// player's registry.  CALL ONLY FROM EXPLICIT OWNER-COMMAND CODEPATHS
        /// (CommandFollow, CommandStay, ForceDismiss, SetFollowMode, radial menu).
        /// </summary>
        public static void SetFollowing(Player player, string companionId, bool follow)
        {
            if (player == null || string.IsNullOrEmpty(companionId)) return;
            // Refuse to write the player's ZDO during their respawn / loading-screen
            // window. Player-ZDO writes during IsTeleporting=true deadlock the zone
            // stream â€” the canonical mechanism for the respawn freeze. Owner commands
            // shouldn't fire during a loading screen anyway, but gate defensively.
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;

            var nview = player.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            // The local player always owns their own ZDO.  Take ownership defensively.
            if (!nview.IsOwner()) nview.ClaimOwnership();

            var zdo = nview.GetZDO();
            if (zdo == null) return;

            var ids = ReadIds(player);
            bool changed = follow ? ids.Add(companionId) : ids.Remove(companionId);
            if (!changed) return;

            zdo.Set(ZDO_KEY, string.Join(",", ids));
            Debug.Log($"[PlayerFollowingRegistry] {player.GetPlayerName()}: companion '{companionId}' follow={follow} (registry size={ids.Count})");
        }

        /// <summary>
        /// Returns the full set of companion IDs currently flagged as following
        /// the given player.  Used by login / respawn restoration to determine
        /// which companions to bring back into the world in following state.
        /// </summary>
        public static HashSet<string> GetAllFollowing(Player player)
        {
            return ReadIds(player);
        }

        /// <summary>
        /// Imports an existing set of companion IDs into the registry.  Used
        /// once during the legacy-vault migration on first login under the new
        /// system, when the player has saved companions with IsFollowing=true
        /// in their vault but no entries in their ZDO registry yet.  After
        /// migration, the registry is the authoritative source.
        /// </summary>
        public static void ImportLegacyIds(Player player, IEnumerable<string> companionIds)
        {
            if (player == null || companionIds == null) return;
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;

            var nview = player.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            if (!nview.IsOwner()) nview.ClaimOwnership();

            var zdo = nview.GetZDO();
            if (zdo == null) return;

            var ids = ReadIds(player);
            int added = 0;
            foreach (var id in companionIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (ids.Add(id)) added++;
            }
            if (added > 0)
            {
                zdo.Set(ZDO_KEY, string.Join(",", ids));
                Debug.Log($"[PlayerFollowingRegistry] Migrated {added} companion(s) into registry for {player.GetPlayerName()}");
            }
        }

        private static HashSet<string> ReadIds(Player player)
        {
            var set = new HashSet<string>();
            if (player == null) return set;

            var nview = player.GetComponent<ZNetView>();
            var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return set;

            var raw = zdo.GetString(ZDO_KEY, "");
            if (string.IsNullOrEmpty(raw)) return set;

            foreach (var id in raw.Split(','))
            {
                var trimmed = id?.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    set.Add(trimmed);
            }
            return set;
        }
    }
}
