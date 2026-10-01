using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Keeps a companion's ZDO on its owner's peer while the owner is near. A companion simulates only on its ZDO owner, and that is
    /// where its owner's hits are seen (OwnerAssist); vanilla's ReleaseNearbyZDOS hands a ZDO to another covering peer whenever the
    /// owner's active area briefly misses it (a teleport, a drill hop), and nothing handed it back (R68: "[Drill] took back
    /// FiresBot.fighter (its ZDO was owned by another peer)"). FGN never claims tamed ZDOs for the server, but like vanilla it hands one to
    /// a covering peer when the owner's REPORTED reference position doesn't cover it ([fgn] 10-01). Every <see cref="Interval"/> s on the
    /// owner's own machine: each of its companions within <see cref="Radius"/> m (inside the owner's guaranteed zone coverage) that this
    /// peer does not own is claimed back; never while the owner teleports.
    /// 0.2.265 (HR1: 85 hand-backs between the bot and the observer in 6 min, every chore cancelled at 0 s): the server keeps it there
    /// too (<see cref="ZDOMan_ReleaseZDOS_KeepCompanions"/>), and a cart or ship far from its owner's player no longer drags that player's
    /// reference position away (<see cref="Tracker_FixedUpdate_NearOnly"/>).
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

        // ── 0.2.265: the server keeps a near companion with its owner's peer ([lead]: real MP players hit the same thing) ──────────
        // Right after vanilla's (or FGN's) ownership pass (ZDOMan.ReleaseZDOS, server only, every 2 s; m_releaseZDOTimer is 0 just
        // after it ran): for each player (each peer's character ZDO, and the host's own player), every tamed companion ZDO of theirs
        // within Radius m that another peer (or nobody) owns goes back to that player's peer. One line per companion per minute.

        private static readonly int s_ownerKey = "companion_owner".GetStableHashCode(), s_tamedKey = "companion_tamed".GetStableHashCode(),
            s_nameKey = "companion_name".GetStableHashCode(), s_displayKey = "companion_displayname".GetStableHashCode();
        private static readonly List<ZDO> s_near = new List<ZDO>();
        private static readonly Dictionary<ZDOID, (float at, int quiet)> s_said = new Dictionary<ZDOID, (float, int)>();
        private static bool s_serverFailed;
        private static float s_serverNext;

        [HarmonyPatch(typeof(ZDOMan), "ReleaseZDOS")]
        private static class ZDOMan_ReleaseZDOS_KeepCompanions
        {
            private static void Postfix(ZDOMan __instance)
            {
                // Our own 2 s clock as well: should another mod skip vanilla's pass, its timer stays 0 and this would run every frame.
                if (__instance.m_releaseZDOTimer != 0f || Time.time < s_serverNext || s_serverFailed || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                s_serverNext = Time.time + 1.9f;
                try { KeepOnServer(__instance); }
                catch (Exception e)
                {
                    s_serverFailed = true;
                    Debug.LogWarning($"[CompanionOwnership] server keep stopped ({e.GetType().Name}: {e.Message}); vanilla ownership from now on");
                }
            }
        }

        private static void KeepOnServer(ZDOMan man)
        {
            SimulationDistance sim = ZNet.instance.GetSyncedSimulationDistance();
            var near = new SimulationDistance(sim.NearSimulationDistance, 0, sim.IsClassic);
            Player host = Player.m_localPlayer;
            if (!ZNet.instance.IsDedicated() && host != null && !host.IsDead())
                KeepFor(man, host.GetPlayerID(), host.transform.position, ZDOMan.GetSessionID(), near);
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || peer.m_characterID.IsNone()) continue;
                ZDO body = man.GetZDO(peer.m_characterID);
                if (body == null) continue;
                long playerId = body.GetLong(ZDOVars.s_playerID, 0L);
                if (playerId != 0L) KeepFor(man, playerId, body.GetPosition(), peer.m_uid, near);
            }
        }

        private static void KeepFor(ZDOMan man, long playerId, Vector3 at, long peerUid, SimulationDistance near)
        {
            s_near.Clear();
            man.FindSectorObjects(ZoneSystem.GetZone(at), near, s_near);
            foreach (ZDO zdo in s_near)
            {
                if (zdo == null || zdo.GetOwner() == peerUid) continue;
                if (zdo.GetLong(s_ownerKey, 0L) != playerId || !zdo.GetBool(s_tamedKey)) continue;
                if (Vector3.Distance(at, zdo.GetPosition()) > Radius) continue;
                long would = zdo.GetOwner();
                zdo.SetOwner(peerUid);
                float now = Time.time;
                if (s_said.TryGetValue(zdo.m_uid, out var said) && now - said.at < 60f) { s_said[zdo.m_uid] = (said.at, said.quiet + 1); continue; }
                int quiet = said.quiet;
                s_said[zdo.m_uid] = (now, 0);
                string name = zdo.GetString(s_displayKey, "");
                if (name.Length == 0) name = zdo.GetString(s_nameKey, "a companion");
                Debug.Log($"[CompanionOwnership] kept {name} with its owner's peer {peerUid} ({Vector3.Distance(at, zdo.GetPosition()):0} m from its owner); " +
                          $"the server would have given it to {PeerName(would)}{(quiet > 0 ? $" (and {quiet} more time(s) in the last minute)" : "")}");
            }
        }

        private static string PeerName(long uid)
        {
            if (uid == 0L) return "nobody";
            if (uid == ZDOMan.GetSessionID()) return $"the server ({uid})";
            ZNetPeer peer = ZNet.instance != null ? ZNet.instance.GetPeer(uid) : null;
            return peer != null ? $"{peer.m_playerName} ({uid})" : $"peer {uid}";
        }

        // ── 0.2.265: a far cart or ship no longer moves its owner's reference position ([fgn] 10-01) ────────────────────────────────
        // Vanilla Tracker.FixedUpdate sets ZNet's reference position to its cart / ship every tick on the machine that owns it, after
        // Player.Update set it to the player; the reference position sent to the server every 2 s is then the cart's, wherever it is
        // parked, the server thinks that player covers none of its own surroundings, and hands its companions (and everything else it
        // owns there) to another covering peer. While the local player is alive and the tracker is over FarTracker m from it, the
        // player's own position stays the reference. One line per tracker per minute.

        private const float FarTracker = 32f;
        private static readonly Dictionary<int, float> s_trackerSaid = new Dictionary<int, float>();

        [HarmonyPatch(typeof(Tracker), "FixedUpdate")]
        private static class Tracker_FixedUpdate_NearOnly
        {
            private static bool Prefix(Tracker __instance)
            {
                if (!__instance.m_active) return true;
                Player me = Player.m_localPlayer;
                if (me == null || me.IsDead()) return true;
                float far = Vector3.Distance(me.transform.position, __instance.transform.position);
                if (far <= FarTracker) return true;
                int id = __instance.GetInstanceID();
                if (!s_trackerSaid.TryGetValue(id, out float at) || Time.time - at >= 60f)
                {
                    s_trackerSaid[id] = Time.time;
                    Vector3 p = __instance.transform.position;
                    Debug.Log($"[CompanionOwnership] {Utils.GetPrefabName(__instance.gameObject)} at ({p.x:0}, {p.z:0}) is {far:0} m from {me.GetPlayerName()}: " +
                              "its reference position stays on the player (vanilla would move it to the tracker, and the server would hand this player's surroundings to others)");
                }
                return false;
            }
        }
    }
}
