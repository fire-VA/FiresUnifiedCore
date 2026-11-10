using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Loot
{
    /// <summary>
    /// [CaveOre] regrow (Fire 10-27, ore in caves: "Broken deposits in rich caves come back after several in-game days ... only when no
    /// player is nearby"). FAT places ore in its voxel caves and rolls some 64 m cave zones MINERAL RICH; this rides on Core's one
    /// respawn service (LocationLootRefresh's tracked records): a cave ore deposit in a rich zone that is mined out registers a tracked
    /// record, and the server's ServerTick respawns it after 'Rich cave ore regrow days' with no player within 40 m.
    ///
    /// No compile dependency on FAT: FAT sets <see cref="RichCaveAt"/> at ZNet.Start by reflection (FatCaveOre.TryHookCore). Without FAT
    /// the hook stays null and nothing here runs. The cave ore tag is FAT's ZDO key "fires_cave" = 4 (its CaveContentKind.Ore); it rides
    /// onto the respawned deposit as a carried key, so a regrown deposit regrows again.
    ///
    /// Authority stays on the SERVER: the owner only asks (CaveOreRpc); the server re-checks the prefab is a mine rock, the point is in a
    /// rich cave by its OWN FAT hook, the sender is within reach, and a per-session cap, before it adds the record.
    ///
    /// Security ([wishbone] review 10-27): the request names the deposit's ZDOID, and the server only accepts it if that ZDO still exists
    /// on the server, carries fires_cave == 4, has the sent prefab and sits within MatchRadius of the sent point. The record is built
    /// from the server's ZDO, never the client's values. A ZDO already gone (the destroy synced first) is refused: one missed regrow,
    /// never a dupe. A record already tracked within MatchRadius, or a ZDOID already accepted, is refused (replay guard).
    /// </summary>
    public static class CaveOreRegrow
    {
        /// <summary>Set by FAT: true when a point is in a mineral-rich cave zone, under rock cover. Null = FAT absent (off).</summary>
        public static Func<Vector3, bool> RichCaveAt;

        internal const string Kind = "caveore";
        private const string CaveOreRpc = "FiresCore_CaveOreRegrow";
        private static readonly int CaveKey = "fires_cave".GetStableHashCode();
        private const int OreKind = 4;
        private const float MatchRadius = 1.0f, PlayerRange = 40f, RegisterReach = 256f;
        private const int MaxPerPeer = 200;
        private const string Tag = "[CaveOre] regrow: ";
        // 1086 ([generator], Fire 11-01: configs live in their own mods): FiresAdminTerrain's key, read on use
        private static readonly FiresCore.Config.LiveSetting<float> s_days = new FiresCore.Config.LiveSetting<float>("Loot", "Rich cave ore regrow days", 6f);
        private static readonly Dictionary<long, int> s_sent = new Dictionary<long, int>();
        private static readonly HashSet<ZDOID> s_accepted = new HashSet<ZDOID>();   // the replay guard (per server session)
        // Core's respawn registry (LocationLootRefresh.s_records, private): read only, for the "already tracked here" dedupe.
        private static readonly AccessTools.FieldRef<List<LocationLootRefresh.Record>> s_records =
            AccessTools.StaticFieldRefAccess<List<LocationLootRefresh.Record>>(AccessTools.Field(typeof(LocationLootRefresh), "s_records"));

        internal static float Days => s_days != null ? Mathf.Max(0.1f, s_days.Value) : 6f;

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
        }

        // ---- the trigger: a cave ore deposit removed on its owner ---------------------------------------------------------
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Destroy))]
        private static class ZNetScene_Destroy_CaveOre
        {
            private static void Prefix(GameObject go)
            {
                try
                {
                    if (RichCaveAt == null || go == null) return;
                    var view = go.GetComponent<ZNetView>();
                    if (view == null || !view.IsValid() || !view.IsOwner()) return;
                    ZDO zdo = view.GetZDO();
                    if (zdo.GetInt(CaveKey, 0) != OreKind) return;
                    if (go.GetComponent<MineRock5>() == null && go.GetComponent<MineRock>() == null) return;
                    Vector3 pos = go.transform.position;
                    if (!RichCaveAt(pos)) return;
                    // A PREFIX on ZNetScene.Destroy: this request goes out before the destroy syncs, so the ZDO still exists on the server.
                    var pkg = new ZPackage();
                    pkg.Write(LocationLootRefresh.PrefabName(view));
                    pkg.Write(pos);
                    pkg.Write(go.transform.rotation);
                    pkg.Write(zdo.m_uid);   // add-only: the deposit's ZDOID, after the original fields
                    if (ZNet.instance != null && ZNet.instance.IsServer()) RPC_Regrow(ZDOMan.GetSessionID(), pkg);
                    else ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), CaveOreRpc, pkg);
                }
                catch (Exception e) { Debug.LogWarning(Tag + "destroy: " + e.Message); }
            }
        }

        // ---- the server's checks, then Core's tracked record ----------------------------------------------------------------
        private static void RPC_Regrow(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
            try
            {
                if (pkg.GetPos() != 0) pkg.SetPos(0);
                string sentName = pkg.ReadString();
                Vector3 sentPos = pkg.ReadVector3();
                pkg.ReadQuaternion();   // the client's rotation: not trusted (the ZDO's is used)
                if (pkg.GetPos() >= pkg.Size())
                { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused from {sender}: no ZDOID in the request (an old client)"); return; }
                ZDOID id = pkg.ReadZDOID();

                // The deposit must still exist on the server, be FAT cave ore, be the sent prefab, and be where the client says.
                ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
                if (zdo == null)
                { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused from {sender}: zdo gone {id} (the destroy synced first; no regrow, never a dupe)"); return; }
                if (zdo.GetInt(CaveKey, 0) != OreKind)
                { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused from {sender}: zdo {id} has no cave ore tag"); return; }
                if (zdo.GetPrefab() != sentName.GetStableHashCode())
                { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused from {sender}: prefab mismatch for zdo {id} (sent '{sentName}')"); return; }
                Vector3 pos = zdo.GetPosition();
                Quaternion rot = zdo.GetRotation();
                if ((pos - sentPos).sqrMagnitude > MatchRadius * MatchRadius)
                { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused from {sender}: position mismatch for zdo {id} ({Vector3.Distance(pos, sentPos):0.0} m)"); return; }
                if (s_accepted.Contains(id))
                { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused from {sender}: replay of zdo {id}"); return; }

                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
                if (prefab == null || (prefab.GetComponent<MineRock5>() == null && prefab.GetComponent<MineRock>() == null))
                { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused from {sender}: no prefab / not a mine rock for zdo {id}"); return; }
                string prefabName = prefab.name;
                var records = s_records();
                if (records != null && records.Exists(x => x.Tracked && (x.Pos - pos).sqrMagnitude < MatchRadius * MatchRadius))
                { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused from {sender}: duplicate, a record is already tracked at ({pos.x:0},{pos.z:0})"); return; }
                if (RichCaveAt == null || !RichCaveAt(pos))
                { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused from {sender}: {prefabName} at ({pos.x:0},{pos.z:0}) is not in a mineral-rich cave"); return; }
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                Vector3 from = peer != null ? peer.m_refPos : (sender == ZDOMan.GetSessionID() && Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : pos);
                if (Mathf.Max(Mathf.Abs(from.x - pos.x), Mathf.Abs(from.z - pos.z)) > RegisterReach)
                { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused from {sender}: {prefabName} is {Vector3.Distance(from, pos):0} m from that player"); return; }
                s_sent.TryGetValue(sender, out int n);
                if (n >= MaxPerPeer)
                { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused from {sender}: cap reached ({MaxPerPeer} per session)"); return; }
                s_sent[sender] = n + 1;
                s_accepted.Add(id);
                double now = ZNet.instance.GetTimeSeconds();
                float daySec = EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0 ? EnvMan.instance.m_dayLengthSec : 1800f;
                var r = new LocationLootRefresh.Record
                {
                    Prefab = prefabName, Pos = pos, Rot = rot, Day = LocationLootRefresh.Today, Kind = Kind, Location = "",
                    Tracked = true, DueSeconds = now + Days * daySec, Radius = MatchRadius, Defaults = true, PlayerRange = PlayerRange,
                };
                r.Carry.Add("fires_cave\ti\t" + OreKind);   // the regrown deposit is cave ore again (it regrows again)
                LocationLootRefresh.SendRegister(r);   // on the server: straight into the registry (AddRecord)
                FiresCore.Logging.FiresLogger.LogDebug($"{Tag}{prefabName} at ({pos.x:0},{pos.y:0},{pos.z:0}) mined out in a rich cave: grows back in {Days:0.#} in-game day(s) with no player within {PlayerRange:0} m");
            }
            catch (Exception e) { Debug.LogWarning(Tag + "register: " + e.Message); }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_CaveOreRegrow
        {
            private static void Postfix()
            {
                s_sent.Clear();
                s_accepted.Clear();
                ZRoutedRpc.instance?.Register<ZPackage>(CaveOreRpc, RPC_Regrow);
            }
        }
    }
}
