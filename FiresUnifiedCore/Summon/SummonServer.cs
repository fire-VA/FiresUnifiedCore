using System;
using System.Collections.Generic;
using FiresCore.Logging;
using FiresCore.Possession;
using UnityEngine;

namespace FiresCore.Summon
{
    /// <summary>
    /// The server's summon registry: the cap per owner and group (the oldest is despawned first), the timer, and the clean-up
    /// of a disconnected owner's summons (they were saved with the character at logout, or the login re-spawn replaces them).
    /// </summary>
    internal static class SummonServer
    {
        internal const string RegisterRpc = "FiresCore_Summon_Register";
        internal const string DismissRpc = "FiresCore_Summon_Dismiss";
        internal const string DismissAllRpc = "FiresCore_Summon_DismissAll";

        private const int MaxCap = 6;
        private const float OwnerGoneGrace = 15f;

        private sealed class Entry { public long Owner; public string Group; public long Made; public float OwnerMissingSince = -1f; }

        private static readonly Dictionary<ZDOID, Entry> s_summons = new Dictionary<ZDOID, Entry>();

        internal static void RegisterRpcs()
        {
            s_summons.Clear();
            ZRoutedRpc.instance.Register<ZDOID, string, int>(RegisterRpc, RPC_Register);
            ZRoutedRpc.instance.Register<ZDOID>(DismissRpc, RPC_Dismiss);
            ZRoutedRpc.instance.Register<long>(DismissAllRpc, RPC_DismissAll);
        }

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        private static void RPC_Register(long sender, ZDOID id, string group, int cap)
        {
            if (!IsServer) return;
            long owner = PossessionServer.PlayerIdOf(sender, out _);
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || owner == 0L || zdo.GetLong(SummonKeys.Owner, 0L) != owner) return;
            s_summons[id] = new Entry { Owner = owner, Group = group ?? "", Made = zdo.GetLong(SummonKeys.Made, SummonKeys.NowMs) };
            EnforceCap(owner, group ?? "", Mathf.Clamp(cap, 1, MaxCap));
        }

        private static void EnforceCap(long owner, string group, int cap)
        {
            var mine = new List<KeyValuePair<ZDOID, Entry>>();
            foreach (var kv in s_summons) if (kv.Value.Owner == owner && kv.Value.Group == group) mine.Add(kv);
            if (mine.Count <= cap) return;
            mine.Sort((a, b) => a.Value.Made.CompareTo(b.Value.Made));
            for (int i = 0; i < mine.Count - cap; i++) Destroy(mine[i].Key, "over the cap of " + cap);
        }

        private static void RPC_Dismiss(long sender, ZDOID id)
        {
            if (!IsServer) return;
            long owner = PossessionServer.PlayerIdOf(sender, out _);
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (zdo != null && owner != 0L && zdo.GetLong(SummonKeys.Owner, 0L) == owner) Destroy(id, "dismissed");
        }

        /// <summary>Removes every summon this owner has in the world (logout/portal after saving them, death, the login re-spawn).</summary>
        private static void RPC_DismissAll(long sender, long ownerId)
        {
            if (!IsServer) return;
            long owner = PossessionServer.PlayerIdOf(sender, out _);
            if (owner == 0L || owner != ownerId) return;
            var ids = new List<ZDOID>();
            foreach (var kv in s_summons) if (kv.Value.Owner == owner) ids.Add(kv.Key);
            foreach (var id in ids) Destroy(id, "owner's summons cleared");
        }

        internal static void Tick()
        {
            if (!IsServer || s_summons.Count == 0) return;
            long now = SummonKeys.NowMs;
            var drop = new List<ZDOID>();
            var expired = new List<ZDOID>();
            foreach (var kv in s_summons)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(kv.Key);
                if (zdo == null) { drop.Add(kv.Key); continue; }
                long exp = zdo.GetLong(SummonKeys.Expiry, 0L);
                if (exp > 0L && now >= exp) { expired.Add(kv.Key); continue; }
                if (PossessionServer.PlayerConnected(kv.Value.Owner)) kv.Value.OwnerMissingSince = -1f;
                else if (kv.Value.OwnerMissingSince < 0f) kv.Value.OwnerMissingSince = Time.time;
                else if (Time.time - kv.Value.OwnerMissingSince > OwnerGoneGrace) expired.Add(kv.Key);
            }
            foreach (var id in drop) s_summons.Remove(id);
            foreach (var id in expired) Destroy(id, "timer or owner gone");
        }

        private static void Destroy(ZDOID id, string why)
        {
            s_summons.Remove(id);
            ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
            if (zdo == null) return;
            FiresLogger.LogDebug(SummonKeys.Tag + "despawn " + id + " (owner " + zdo.GetLong(SummonKeys.Owner, 0L) + "): " + why);
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(id) : null;
            if (go != null && go.GetComponent<ZNetView>() is ZNetView nv && nv.IsValid())
            {
                if (!nv.IsOwner()) nv.ClaimOwnership();
                ZNetScene.instance.Destroy(go);
                return;
            }
            zdo.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(zdo);
        }
    }
}
