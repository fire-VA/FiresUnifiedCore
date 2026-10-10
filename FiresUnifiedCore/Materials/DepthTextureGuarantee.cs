using HarmonyLib;
using UnityEngine;

namespace FiresCore.Materials
{
    /// <summary>
    /// 0.2.314: Custom/FiresInlandWater now takes its shore depth per pixel from _CameraDepthTexture (the tint, opacity,
    /// waterline fade and foam collar). Built-in pipeline cameras only fill that texture when asked; vanilla's own water
    /// asks in practice, but nothing guaranteed it. The game camera asks too, once, when it wakes. Client only (a dedicated
    /// server has no GameCamera).
    /// </summary>
    internal static class DepthTextureGuarantee
    {
        [HarmonyPatch(typeof(GameCamera), "Awake")]
        private static class GameCamera_Awake_DepthTexture
        {
            private static void Postfix(GameCamera __instance)
            {
                try
                {
                    var cam = __instance != null ? __instance.GetComponent<Camera>() : null;
                    if (cam == null) return;
                    bool had = (cam.depthTextureMode & DepthTextureMode.Depth) != 0;
                    cam.depthTextureMode |= DepthTextureMode.Depth;
                    FiresCore.Logging.FiresLogger.LogDebug($"[Water] game camera depth texture {(had ? "already on" : "turned on")} (inland water shore depth)");
                }
                catch (System.Exception ex) { Debug.LogWarning("[Water] depth texture: " + ex.Message); }
            }
        }
    }
}
