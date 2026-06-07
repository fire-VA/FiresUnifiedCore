using System;
using System.Reflection;
using FiresCore.Logging;

namespace FiresCore.Lifecycle
{
    // Reflective wrapper around ServerSync.AdminSyncing.IsAdmin(long) used by
    // every RPC handler so admin enforcement stays identical across the mod.
    // Falls back to TRUST when ServerSync isn't installed (SP/host-only),
    // and short-circuits sender == 0 (local-server loopback) without
    // touching reflection.
    public static class AdminCheck
    {
        private const string ServerSyncAdminTypeName = "ServerSync.AdminSyncing";
        private const string IsAdminMethodName = "IsAdmin";
        private const long LocalServerLoopbackSender = 0L;
        private const string LogPrefix = "[AdminCheck]";

        private static MethodInfo _isAdminMethod;
        private static bool _resolved;

        public static bool IsAdmin(long sender)
        {
            if (sender == LocalServerLoopbackSender) return true;

            EnsureResolved();
            if (_isAdminMethod == null) return true;

            try
            {
                return (bool)_isAdminMethod.Invoke(null, new object[] { sender });
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} AdminSyncing.IsAdmin threw for sender {sender}: {ex.Message} — defaulting to DENY.");
                return false;
            }
        }

        public static void Reset()
        {
            _resolved = false;
            _isAdminMethod = null;
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                if (TryBindIsAdminMethod()) return;
                FiresLogger.LogInfo($"{LogPrefix} ServerSync not detected — all peers will be treated as admin (SP/host-only mod loadout).");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} Reflection lookup threw: {ex.Message} — defaulting to TRUST.");
            }
        }

        private static bool TryBindIsAdminMethod()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(ServerSyncAdminTypeName);
                if (t == null) continue;

                _isAdminMethod = t.GetMethod(IsAdminMethodName,
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(long) }, null);

                if (_isAdminMethod != null)
                {
                    FiresLogger.LogInfo($"{LogPrefix} ServerSync.AdminSyncing.IsAdmin bound — non-admin RPC handlers will reject.");
                    return true;
                }
            }
            return false;
        }
    }
}
