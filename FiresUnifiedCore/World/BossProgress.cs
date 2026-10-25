using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// "Has this boss been beaten?" for every mod ([lead] 10-24 global-key audit). FiresRPGmaker's private progression keeps boss keys
    /// (defeated_*) out of ZoneSystem's global list (they become per-player keys), so a plain GetGlobalKey("defeated_bonemass") reads
    /// false on such servers. Has(key) is true when ANY of these hold:
    ///   - the vanilla global key is set;
    ///   - on a client: the local player has it (vanilla unique key, or RPGmaker's PrivateKeyManager.HasKey - its private-or-global view);
    ///   - on the server: any online player holds it in RPGmaker's server store (PrivateKeyManager.GetServerKeysForPlayer).
    /// WHICH ONE: Has(key) for UI / local-client checks; Has(key, playerId) for decisions about one specific player (server logic).
    /// RPGmaker is optional and read by reflection; without it this is exactly the global key (+ the local unique key). Add-only API.
    /// </summary>
    public static class BossProgress
    {
        private static bool s_looked;
        private static MethodInfo s_hasKey, s_serverKeys, s_tierNear;
        private static FieldInfo s_order;

        private static void Look()
        {
            if (s_looked) return;
            s_looked = true;
            var t = AccessTools.TypeByName("FiresRPGmaker.Modules.Progression.PrivateKeyManager");
            if (t == null) return;
            s_hasKey = AccessTools.Method(t, "HasKey", new[] { typeof(string) });
            s_serverKeys = AccessTools.Method(t, "GetServerKeysForPlayer", new[] { typeof(long) });
            s_tierNear = AccessTools.Method(t, "GetHighestBossTierNear", new[] { typeof(Vector3), typeof(float) });
            s_order = AccessTools.Field(t, "BossKeyOrder");
            FiresCore.Logging.FiresLogger.LogDebug($"[BossProgress] RPGmaker private keys: HasKey {(s_hasKey != null ? "found" : "missing")}, server store {(s_serverKeys != null ? "found" : "missing")}");
        }

        /// <summary>True when the key counts as reached (see the class notes). Never throws.</summary>
        public static bool Has(string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
            string k = key.ToLowerInvariant();
            try
            {
                var zs = ZoneSystem.instance;
                if (zs != null && zs.GetGlobalKey(k)) return true;
                Look();
                var p = Player.m_localPlayer;
                if (p != null)
                {
                    if (p.HaveUniqueKey(k)) return true;
                    if (s_hasKey != null && s_hasKey.Invoke(null, new object[] { k }) is bool b && b) return true;
                }
                if (ZNet.instance != null && ZNet.instance.IsServer() && s_serverKeys != null && ZDOMan.instance != null)
                {
                    foreach (var peer in ZNet.instance.GetPeers())
                    {
                        var pz = ZDOMan.instance.GetZDO(peer.m_characterID);
                        long id = pz != null ? pz.GetLong(ZDOVars.s_playerID, 0L) : 0L;
                        if (id == 0L) continue;
                        if (s_serverKeys.Invoke(null, new object[] { id }) is HashSet<string> set && set.Contains(k)) return true;
                    }
                }
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[BossProgress] " + e.Message); }
            return false;
        }

        /// <summary>
        /// True when the global key is set or THAT player holds the key. Use for logic about one specific player (server decisions,
        /// another player's unlocks); use Has(key) for UI / local-client checks. Server: RPGmaker's server store for playerId
        /// (authoritative). Client, local player: same as Has(key). Client, another player: RPGmaker's server-pushed boss tier for
        /// players at that player's spot vs the key's boss order (boss keys only). Never reads client-writable ZDO data. Never throws.
        /// </summary>
        public static bool Has(string key, long playerId)
        {
            if (string.IsNullOrEmpty(key)) return true;
            string k = key.ToLowerInvariant();
            try
            {
                var zs = ZoneSystem.instance;
                if (zs != null && zs.GetGlobalKey(k)) return true;
                Look();
                if (ZNet.instance != null && ZNet.instance.IsServer() && s_serverKeys != null
                    && s_serverKeys.Invoke(null, new object[] { playerId }) is HashSet<string> set && set.Contains(k)) return true;
                var lp = Player.m_localPlayer;
                if (lp != null && lp.GetPlayerID() == playerId)
                    return lp.HaveUniqueKey(k) || (s_hasKey != null && s_hasKey.Invoke(null, new object[] { k }) is bool b && b);
                if (s_tierNear != null && s_order?.GetValue(null) is Dictionary<string, int> order && order.TryGetValue(k, out int need))
                    foreach (var pl in Player.GetAllPlayers())
                        if (pl != null && pl.GetPlayerID() == playerId)
                            return s_tierNear.Invoke(null, new object[] { pl.transform.position, 0.5f }) is int tier && tier >= need;
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[BossProgress] " + e.Message); }
            return false;
        }
    }
}
