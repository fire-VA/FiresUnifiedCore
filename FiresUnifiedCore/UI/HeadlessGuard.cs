using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// One check for "this process has no screen" (a dedicated / batchmode / -nographics server). UI hosts
    /// return before creating anything when it is true, so no panel's Update ever runs on a headless dedi (an earlier
    /// dedi crash came from ConfigPanel.Update polling a hotkey there). Mods with UI can call it the same way.
    /// </summary>
    public static class HeadlessGuard
    {
        private static int s_state = -1;

        public static bool IsHeadless
        {
            get
            {
                if (s_state < 0)
                    s_state = Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null ? 1 : 0;
                return s_state == 1;
            }
        }
    }
}