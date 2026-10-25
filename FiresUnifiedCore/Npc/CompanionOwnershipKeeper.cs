using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Keeps a companion's ZDO on its owner's peer while the owner is near. A companion simulates only on its ZDO owner, and that is
    /// where its owner's hits are seen (OwnerAssist); vanilla's ReleaseNearbyZDOS hands a ZDO to another covering peer whenever the
    /// owner's active area briefly misses it (a teleport, a drill hop), and nothing handed it back. FGN never claims tamed ZDOs for the server, but like vanilla it hands one to
    /// a covering peer when the owner's REPORTED reference position doesn't cover it. Every <see cref="Interval"/> s on the
    /// owner's own machine: each of its companions within <see cref="Radius"/> m (inside the owner's guaranteed zone coverage) that this
    /// peer does not own is claimed back; never while the owner teleports.
    /// The server keeps it there
    /// too (<see cref="ZDOMan_ReleaseZDOS_KeepCompanions"/>), and a cart or ship far from its owner's player no longer drags that player's
    /// reference position away (<see cref="Tracker_FixedUpdate_NearOnly"/>).
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
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
                if (CompanionPossession.IsPossessed(view)) continue;   // Its possessor's peer keeps it (FGN 1.5.37 too)
                if (Vector3.Distance(at, companion.transform.position) > Radius) continue;
                long previous = view.GetZDO().GetOwner();
                view.ClaimOwnership();
                Debug.Log($"[CompanionOwnership] {companion.companionName} reclaimed from peer {previous} "
                          + $"({Vector3.Distance(at, companion.transform.position):0} m from its owner)");
            }
        }

        // ── The server keeps a near companion with its owner's peer ──────────
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
            if (ZNet.instance.IsDedicated()) GiveWildToNearestPeer(man, near);
        }

        // FGN ServerOwnership-V3 simulates
        // every Character on the server, wild companions included; a headless server does not
        // animate it, so the client saw it unanimated at the server's position. Runs after vanilla's / FGN's ownership pass: a WILD companion
        // (not tamed) the dedicated server owns, within Radius of a player, goes to the NEAREST player's peer. Tamed ones: KeepFor above.
        private static readonly int s_wildPrefab = "CompanionNpc_Wild".GetStableHashCode(), s_companionPrefab = "CompanionNpc".GetStableHashCode();
        private static readonly List<ZDO> s_wildNear = new List<ZDO>();
        private static void GiveWildToNearestPeer(ZDOMan man, SimulationDistance near)
        {
            long server = ZDOMan.GetSessionID();
            var bodies = new List<(long uid, Vector3 pos)>();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || peer.m_characterID.IsNone()) continue;
                ZDO body = man.GetZDO(peer.m_characterID);
                if (body != null) bodies.Add((peer.m_uid, body.GetPosition()));
            }
            foreach (var (uid, pos) in bodies)
            {
                s_wildNear.Clear();
                man.FindSectorObjects(FiresCore.World.RingZones.ObjectSector(pos), near, s_wildNear);   // halo ring: the ring object-sector id
                foreach (ZDO zdo in s_wildNear)
                {
                    if (zdo == null || zdo.GetOwner() != server) continue;
                    int prefab = zdo.GetPrefab();
                    if ((prefab != s_wildPrefab && prefab != s_companionPrefab) || zdo.GetBool(s_tamedKey)) continue;
                    float mine = Vector3.Distance(pos, zdo.GetPosition());
                    if (mine > Radius) continue;
                    bool nearest = true;
                    foreach (var (other, opos) in bodies) if (other != uid && Vector3.Distance(opos, zdo.GetPosition()) < mine) { nearest = false; break; }
                    if (!nearest) continue;
                    zdo.SetOwner(uid);
                    FiresCore.Logging.FiresLogger.LogDebug($"[CompanionOwnership] wild '{zdo.GetString(s_nameKey, "?")}' handed from the server to the nearest player's peer {uid} ({mine:0} m): the server does not animate it");
                }
            }
        }

        private static void KeepFor(ZDOMan man, long playerId, Vector3 at, long peerUid, SimulationDistance near)
        {
            s_near.Clear();
            man.FindSectorObjects(FiresCore.World.RingZones.ObjectSector(at), near, s_near);   // halo ring: the ring object-sector id
            foreach (ZDO zdo in s_near)
            {
                if (zdo == null || zdo.GetOwner() == peerUid) continue;
                if (CompanionPossession.IsPossessed(zdo)) continue;   // Its possessor's peer keeps it
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

        // ── A far cart or ship no longer moves its owner's reference position ────────────────────────────────
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
