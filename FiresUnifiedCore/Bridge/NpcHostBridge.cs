using System;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Host server-lifecycle hooks the NPC engine triggers from its peer-connect / logout patches:
    /// (re)registering the host's saved-NPC routed RPCs, and pushing server config files to a joining
    /// peer. These are Marketplace-server concerns; standalone has neither, so each is a no-op until a
    /// host registers it.
    /// </summary>
    public static class NpcHostBridge
    {
        public static Action RegisterServerRpcsFn;
        public static Action ResetServerRpcsFn;
        public static Action<long> PushConfigsToClientFn;

        public static void RegisterServerRpcs()
        {
            try { RegisterServerRpcsFn?.Invoke(); } catch { }
        }

        public static void ResetServerRpcs()
        {
            try { ResetServerRpcsFn?.Invoke(); } catch { }
        }

        public static void PushConfigsToClient(long peerUid)
        {
            try { PushConfigsToClientFn?.Invoke(peerUid); } catch { }
        }
    }
}
