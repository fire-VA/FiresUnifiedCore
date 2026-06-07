using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using FiresCore.Config;
using FiresCoreRoot = FiresCore.FiresUnifiedCore;

namespace FiresCore.Sync
{
    // Tracks the server's admin list and pushes per-peer admin status to
    // ConfigSync.lockExempt so clients can locally unlock the config UI
    // without round-tripping through the server. Server side polls the
    // admin list every AdminListPollIntervalSeconds and only broadcasts
    // when the set actually changes.
    public static class AdminSyncing
    {
        private const string AdminStatusRpcSuffix = " AdminStatusSync";
        private const float AdminListPollIntervalSeconds = 30f;
        private const int CompressedPackageThresholdBytes = 10000;
        private const int CompressedPackageMagic = 4;
        private const long ServerPeerLoopbackId = 0L;

        private static bool _isServer;

        // Fired on every client after admin status is received and ConfigSync.lockExempt is set.
        // Consuming mods subscribe to refresh their own config-lock UI; the watcher, RPC channel,
        // and lockExempt push all live here so there is one source of truth across the family.
        public static event Action<bool> AdminStatusChanged;

        private static string AdminStatusRpcName => FiresCoreRoot.PluginName + AdminStatusRpcSuffix;

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class AdminStatusSyncPatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance)
            {
                _isServer = __instance.IsServer();
                if (FiresCoreRoot.Instance == null) return;

                ZRoutedRpc.instance.Register<ZPackage>(AdminStatusRpcName, RPC_AdminStatusSync);

                if (_isServer)
                    __instance.StartCoroutine(WatchAdminListChanges());
            }
        }

        public static bool IsAdmin(long senderId)
        {
            if (!ZNet.instance) return false;
            var peer = ZNet.instance.GetPeer(senderId);
            if (peer == null) return false;
            return GetAdminList().Contains(peer.m_rpc.GetSocket().GetHostName());
        }

        private static IEnumerator WatchAdminListChanges()
        {
            var adminList = GetAdminList();
            var currentAdmins = new HashSet<string>(adminList.GetList());

            while (true)
            {
                yield return new WaitForSeconds(AdminListPollIntervalSeconds);

                var newAdmins = new HashSet<string>(adminList.GetList());
                if (newAdmins.SetEquals(currentAdmins)) continue;

                BroadcastAdminStatusChanges(newAdmins);
                currentAdmins = newAdmins;
            }
        }

        private static void BroadcastAdminStatusChanges(HashSet<string> newAdmins)
        {
            var peers = ZNet.instance.GetPeers();
            var adminPeers = peers
                .Where(p => newAdmins.Contains(p.m_rpc.GetSocket().GetHostName()))
                .ToList();
            var nonAdminPeers = peers.Except(adminPeers).ToList();

            SendAdminStatus(nonAdminPeers, isAdmin: false);
            SendAdminStatus(adminPeers, isAdmin: true);
        }

        private static void SendAdminStatus(List<ZNetPeer> peers, bool isAdmin)
        {
            if (!peers.Any()) return;
            var package = new ZPackage();
            package.Write(isAdmin);
            ZNet.instance.StartCoroutine(SendZPackage(peers, package));
        }

        private static void RPC_AdminStatusSync(long sender, ZPackage package)
        {
            if (_isServer)
            {
                HandleServerSideAdminRequest(sender);
                return;
            }
            HandleClientSideAdminStatusReceived(package);
        }

        private static void HandleServerSideAdminRequest(long sender)
        {
            var peer = ZNet.instance.GetPeer(sender);
            if (peer == null) return;
            if (!GetAdminList().Contains(peer.m_rpc.GetSocket().GetHostName())) return;

            var pkg = new ZPackage();
            pkg.Write(true);
            peer.m_rpc.Invoke(AdminStatusRpcName, pkg);
        }

        private static void HandleClientSideAdminStatusReceived(ZPackage package)
        {
            bool isAdmin = package.ReadBool();
            ConfigSync.lockExempt = isAdmin;

            if (ConfigManager.Instance?.configVerboseLogging?.Value == true)
                Debug.Log($"{FiresCoreRoot.PluginName}: Admin status received: {(isAdmin ? "ADMIN" : "NON-ADMIN")}");

            ConfigManager.Instance?.OnConfigLockChanged();
            AdminStatusChanged?.Invoke(isAdmin);
        }

        private static IEnumerator SendZPackage(List<ZNetPeer> peers, ZPackage package)
        {
            if (!ZNet.instance) yield break;

            byte[] data = package.GetArray();
            if (data.Length > CompressedPackageThresholdBytes)
                package = BuildCompressedPackage(data);

            foreach (var peer in peers.Where(p => p.IsReady()))
            {
                if (_isServer)
                {
                    peer.m_rpc.Invoke(AdminStatusRpcName, package);
                    continue;
                }
                long target = peer.m_server ? ServerPeerLoopbackId : peer.m_uid;
                ZRoutedRpc.instance.InvokeRoutedRPC(target, AdminStatusRpcName, package);
            }
        }

        private static ZPackage BuildCompressedPackage(byte[] data)
        {
            var compressed = new ZPackage();
            compressed.Write(CompressedPackageMagic);

            using var output = new MemoryStream();
            using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal))
                deflate.Write(data, 0, data.Length);

            compressed.Write(output.ToArray());
            return compressed;
        }

        private static SyncedList GetAdminList()
        {
            return (SyncedList)AccessTools.Field(typeof(ZNet), "m_adminList").GetValue(ZNet.instance);
        }
    }
}
