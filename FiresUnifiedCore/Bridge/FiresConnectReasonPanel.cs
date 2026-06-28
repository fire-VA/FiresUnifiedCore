using HarmonyLib;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Client-only: when the server pushed a rejection reason via <see cref="FiresConnectReason"/>, replace
    /// the vanilla connection-failed text ("Failed to connect" / "Incompatible version" / "Disconnected")
    /// with that actionable reason. Kept as its own class so it can be skipped on a dedicated server -
    /// patching FejdStartup native-crashes Mono's IL rewriter on a headless build (see FiresUnifiedCore
    /// DedicatedServerSkipPatchTypes).
    /// </summary>
    [HarmonyPatch(typeof(FejdStartup), "ShowConnectError", new[] { typeof(ZNet.ConnectionStatus) })]
    internal static class FiresConnectReasonPanel
    {
        [HarmonyPostfix]
        private static void Postfix(FejdStartup __instance)
        {
            if (__instance == null || __instance.m_connectionFailedError == null) return;
            if (!FiresConnectReason.TryConsumeReason(out var text)) return;

            if (__instance.m_connectionFailedPanel != null)
                __instance.m_connectionFailedPanel.SetActive(true);
            __instance.m_connectionFailedError.text = text;
        }
    }
}
