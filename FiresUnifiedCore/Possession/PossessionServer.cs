using System;
using System.Collections;
using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Possession
{
    /// <summary>
    /// The server side of a possession grant. A grant sets the creature's "valcast_possessed" key (the possessor's player id,
    /// as FGN expects) and hands its ZDO to the requesting peer in the same step, so no FGN pass sees an unflagged creature
    /// owned by a "wrong" peer. Release, the possessor disconnecting, and the creature going away clear the key here: with
    /// FGN's Server-Side Simulation off nothing else would, and FGN's CreatureOwnership would veto that creature for good.
    /// </summary>
    internal static class PossessionServer
    {
        internal const string RequestRpc = "FiresCore_Possess_Request";
        internal const string ReplyRpc = "FiresCore_Possess_Reply";
        internal const string ReleaseRpc = "FiresCore_Possess_Release";
        internal const string RevokeRpc = "FiresCore_Possess_Revoke";

        // KeySet: this grant wrote the key (it held another value before). A key that already held the player's id was set by
        // another engine copy (Valcast possessing the same creature); Core's release must leave it alone.
        private sealed class Entry { public long PlayerId; public long Peer; public string Tag; public bool KeySet; }

        private static readonly Dictionary<ZDOID, Entry> s_active = new Dictionary<ZDOID, Entry>();
        private static readonly Dictionary<string, PossessValidator> s_validators = new Dictionary<string, PossessValidator>(StringComparer.OrdinalIgnoreCase);

        internal static void RegisterValidator(string tag, PossessValidator v)
        {
            if (string.IsNullOrEmpty(tag) || v == null) return;
            s_validators[tag] = v;
            FiresLogger.LogDebug(PossessionRules.Tag + "validator registered for '" + tag + "'");
        }

        internal static void RegisterRpcs()
        {
            s_active.Clear();
            ZRoutedRpc.instance.Register<ZDOID, string, float>(RequestRpc, RPC_Request);
            ZRoutedRpc.instance.Register<ZDOID>(ReleaseRpc, RPC_Release);
        }

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        private static void RPC_Request(long sender, ZDOID id, string tag, float range)
        {
            if (!IsServer) return;
            string why = Validate(sender, id, tag, range, out long playerId, out ZDO zdo);
            if (why != null)
            {
                FiresLogger.LogDebug(PossessionRules.Tag + "denied " + id + " (" + tag + ") to peer " + sender + ": " + why);
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, ReplyRpc, id, false, why);
                return;
            }
            bool keySet = zdo.GetLong(PossessionRules.PossessedKey, 0L) != playerId;
            zdo.Set(PossessionRules.PossessedKey, playerId);
            zdo.SetOwner(sender);
            s_active[id] = new Entry { PlayerId = playerId, Peer = sender, Tag = tag, KeySet = keySet };
            FiresLogger.LogDebug(PossessionRules.Tag + "granted " + id + " (" + Prefab(zdo) + ", " + tag + ") to player " + playerId + " peer " + sender);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ReplyRpc, id, true, "");
        }

        private static string Validate(long sender, ZDOID id, string tag, float range, out long playerId, out ZDO zdo)
        {
            playerId = PlayerIdOf(sender, out Vector3 peerPos);
            zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
            if (zdo == null) return "the creature is gone";
            if (playerId == 0L) return "unknown player";
            long holder = zdo.GetLong(PossessionRules.PossessedKey, 0L);
            if (holder != 0L && holder != playerId && PlayerConnected(holder)) return "possessed by another player";
            float reach = Mathf.Clamp(range, 2f, 100f) + 8f;
            if (peerPos != Vector3.zero && Vector3.Distance(peerPos, zdo.GetPosition()) > reach) return "too far";

            Character instance = null;
            if (ZNetScene.instance != null)
            {
                GameObject go = ZNetScene.instance.FindInstance(id);
                instance = go != null ? go.GetComponent<Character>() : null;
            }
            if (instance != null && (instance.IsPlayer() || instance.IsDead())) return "not a living creature";

            if (string.Equals(tag, PossessionRules.AdminTag, StringComparison.OrdinalIgnoreCase))
            {
                if (PossessionRules.AdminEnabled == null || !PossessionRules.AdminEnabled.Value) return "admin possession is off on this server";
                if (!IsAdmin(sender)) return "admins only";
                if (instance != null && instance.IsBoss() && !PossessionRules.AllowBosses.Value) return "bosses can't be possessed";
                return null;
            }
            if (instance != null && instance.IsBoss()) return "bosses can't be possessed";
            if (!s_validators.TryGetValue(tag ?? "", out PossessValidator v)) return "no rules registered for '" + tag + "'";
            try { return v(playerId, zdo, instance); }
            catch (Exception e) { return "validator error: " + e.Message; }
        }

        private static void RPC_Release(long sender, ZDOID id)
        {
            if (!IsServer) return;
            if (!s_active.TryGetValue(id, out Entry e) || e.Peer != sender) return;
            Clear(id, e, "released");
        }

        /// <summary>The server ends a possession itself (the creature died on the server, an admin command, ...).</summary>
        internal static void Revoke(ZDOID id, string reason)
        {
            if (!IsServer || !s_active.TryGetValue(id, out Entry e)) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(e.Peer, RevokeRpc, id, reason ?? "revoked");
            Clear(id, e, reason);
        }

        private static void Clear(ZDOID id, Entry e, string why)
        {
            s_active.Remove(id);
            ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
            if (e.KeySet && zdo != null && zdo.GetLong(PossessionRules.PossessedKey, 0L) == e.PlayerId) zdo.Set(PossessionRules.PossessedKey, 0L);
            FiresLogger.LogDebug(PossessionRules.Tag + "cleared " + id + " (player " + e.PlayerId + (e.KeySet ? "" : ", key kept: another mod set it") + "): " + why);
        }

        internal static void OnPeerGone(long peerUid)
        {
            if (!IsServer || s_active.Count == 0) return;
            var gone = new List<ZDOID>();
            foreach (var kv in s_active) if (kv.Value.Peer == peerUid) gone.Add(kv.Key);
            foreach (var id in gone) Clear(id, s_active[id], "the possessor disconnected");
        }

        internal static void Tick()
        {
            if (!IsServer || s_active.Count == 0 || ZDOMan.instance == null) return;
            var gone = new List<ZDOID>();
            foreach (var kv in s_active) if (ZDOMan.instance.GetZDO(kv.Key) == null) gone.Add(kv.Key);
            foreach (var id in gone) { FiresLogger.LogDebug(PossessionRules.Tag + "dropped " + id + ": the creature is gone"); s_active.Remove(id); }
        }

        // ── identity ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The peer's player id (their character ZDO's s_playerID, as FGN maps it) and position.</summary>
        internal static long PlayerIdOf(long peerUid, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (ZNet.instance == null) return 0L;
            if (peerUid == ZDOMan.GetSessionID() && Player.m_localPlayer != null)
            {
                pos = Player.m_localPlayer.transform.position;
                return Player.m_localPlayer.GetPlayerID();
            }
            ZNetPeer peer = ZNet.instance.GetPeer(peerUid);
            if (peer == null) return 0L;
            pos = peer.m_refPos;
            ZDO character = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(peer.m_characterID) : null;
            if (character == null) return 0L;
            pos = character.GetPosition();
            return character.GetLong(ZDOVars.s_playerID, 0L);
        }

        internal static bool PlayerConnected(long playerId)
        {
            if (ZNet.instance == null || playerId == 0L) return false;
            if (Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerID() == playerId) return true;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (character != null && character.GetLong(ZDOVars.s_playerID, 0L) == playerId) return true;
            }
            return false;
        }

        /// <summary>Vanilla's admin list (the listen host is always admin). ServerSync's trust-all fallback is not used here.</summary>
        internal static bool IsAdmin(long peerUid)
        {
            if (ZNet.instance == null) return false;
            if (peerUid == ZDOMan.GetSessionID() && !ZNet.instance.IsDedicated()) return true;
            ZNetPeer peer = ZNet.instance.GetPeer(peerUid);
            if (peer == null || peer.m_socket == null) return false;
            string host = peer.m_socket.GetHostName() ?? "";
            try
            {
                object synced = AccessTools.Field(typeof(ZNet), "m_adminList")?.GetValue(ZNet.instance);
                IList list = synced == null ? null : AccessTools.Method(synced.GetType(), "GetList")?.Invoke(synced, null) as IList;
                if (list == null) return false;
                foreach (object entry in list)
                {
                    string s = entry != null ? entry.ToString().Trim() : "";
                    if (s.Length == 0) continue;
                    if (string.Equals(s, host, StringComparison.OrdinalIgnoreCase)) return true;
                    if (host.EndsWith("_" + s, StringComparison.OrdinalIgnoreCase)) return true;   // "Steam_7656..." vs a bare id
                }
            }
            catch (Exception e) { FiresLogger.LogWarning(PossessionRules.Tag + "admin list read failed: " + e.Message); }
            return false;
        }

        private static string Prefab(ZDO zdo)
        {
            GameObject p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return p != null ? p.name : zdo.GetPrefab().ToString();
        }

        [HarmonyPatch(typeof(ZNet), "Disconnect")]
        private static class ZNet_Disconnect_PossessionClear
        {
            private static void Prefix(ZNetPeer peer)
            {
                try { if (peer != null) OnPeerGone(peer.m_uid); }
                catch (Exception e) { FiresLogger.LogWarning(PossessionRules.Tag + "disconnect clear failed: " + e.Message); }
            }
        }
    }
}
