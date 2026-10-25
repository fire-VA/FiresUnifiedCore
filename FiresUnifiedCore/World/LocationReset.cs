using System;
using System.Collections.Generic;
using FiresCore.Logging;
using FiresCore.Loot;

namespace FiresCore.World
{
    /// <summary>
    /// Hand-placed dungeon rooms reset their loot like real dungeons. Public, add-only;
    /// FDM calls it by reflection. A registered owner (a placed room wrapper) works like a vanilla Location for Core's
    /// [Loot] refresh (LocationLootRefresh): each child is stamped with the owner's id ("fires_reset_owner" = "user:id|kind"), and a
    /// stamped chest, one-shot pickable, mud pile or spawner then counts as location loot of that kind, even though a player placed
    /// it (the creator check and the vanilla-Location check are both skipped for it). Chests refill, pickables re-arm, and what
    /// vanilla destroys comes back at its spot, with the stamp carried onto the respawned object. Spawned creatures are their
    /// spawner's business: a destroyed stamped spawner comes back and spawns again by itself. Children that no longer exist are
    /// skipped. UnregisterResettable switches the owner off; its children stop counting from then on.
    /// </summary>
    public static class LocationReset
    {
        internal const string OwnerKey = "fires_reset_owner";
        private const string OffKey = "fires_reset_off";

        /// <summary>Stamps <paramref name="children"/> as resettable loot of <paramref name="owner"/>. False when Core's [Loot] refresh
        /// is off, or the owner isn't a live ZDO. Call it from the owner's owner; children this client doesn't own are skipped
        /// (counted in the Debug line) - stamp them again from their owner, or place them from the same client.</summary>
        public static bool RegisterResettable(ZDOID owner, IEnumerable<ZDOID> children, string kind = "fdm_room")
        {
            if (!LocationLootRefresh.Enabled || ZDOMan.instance == null || owner.IsNone()) return false;
            ZDO o = ZDOMan.instance.GetZDO(owner);
            if (o == null) return false;
            if (o.IsOwner()) o.Set(OffKey, false);
            string stamp = owner.UserID + ":" + owner.ID + "|" + (string.IsNullOrEmpty(kind) ? "fdm_room" : kind.Replace("|", "/"));
            int set = 0, notOwned = 0, missing = 0;
            if (children != null)
                foreach (ZDOID id in children)
                {
                    ZDO z = id.IsNone() ? null : ZDOMan.instance.GetZDO(id);
                    if (z == null) { missing++; continue; }
                    if (!z.IsOwner()) { notOwned++; continue; }
                    z.Set(OwnerKey, stamp);
                    set++;
                }
            FiresLogger.LogDebug($"[LocationReset] {kind} {owner}: {set} child(ren) stamped, {notOwned} not owned here (skipped), {missing} gone");
            return true;
        }

        /// <summary>Switches <paramref name="owner"/> off: its stamped children stop counting as resettable loot.</summary>
        public static void UnregisterResettable(ZDOID owner)
        {
            if (ZDOMan.instance == null || owner.IsNone()) return;
            ZDO o = ZDOMan.instance.GetZDO(owner);
            if (o != null && o.IsOwner()) o.Set(OffKey, true);
            FiresLogger.LogDebug($"[LocationReset] {owner}: unregistered{(o == null ? " (owner already gone)" : "")}");
        }

        /// <summary>True when <paramref name="zdo"/> is a stamped child of an owner that hasn't been switched off; kind = its stamp's kind.
        /// An owner that isn't loaded here still counts (rooms load with their contents).</summary>
        internal static bool Stamped(ZDO zdo, out string kind)
        {
            kind = null;
            string s = zdo?.GetString(OwnerKey, "");
            if (string.IsNullOrEmpty(s)) return false;
            int bar = s.IndexOf('|');
            string id = bar > 0 ? s.Substring(0, bar) : s;
            kind = bar > 0 ? s.Substring(bar + 1) : "fdm_room";
            int colon = id.IndexOf(':');
            if (colon > 0 && long.TryParse(id.Substring(0, colon), out long user) && uint.TryParse(id.Substring(colon + 1), out uint n))
            {
                ZDO o = ZDOMan.instance?.GetZDO(new ZDOID(user, n));
                if (o != null && o.GetBool(OffKey, false)) return false;
            }
            return true;
        }

        /// <summary>The carried-key entry that puts this child's stamp onto its respawned copy (LocationLootRefresh.Record.Carry).</summary>
        internal static string CarryEntry(ZDO zdo)
        {
            string s = zdo?.GetString(OwnerKey, "");
            return string.IsNullOrEmpty(s) ? null : OwnerKey + "\ts\t" + s;
        }
    }
}
