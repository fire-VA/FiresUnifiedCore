// Dedicated server memory: on a dedicated server
// (Null graphics device) vanilla Minimap.Start still allocates four map-sized textures (fog RG16 16 MB, map RGB24 12 MB, height RHalf
// 8 MB, forest mask 8 MB at the 2048 world size). Nothing on a server ever draws or fills them: vanilla Minimap.Update returns at once
// on a Null graphics device, so the map is never generated, loaded or cached there. Shrinking m_textureSize before Start keeps every
// vanilla path intact (pins, RPCs, the same code), just on 16 px textures. A fix: always on, no key; clients are untouched.
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.World
{
    [HarmonyPatch(typeof(Minimap), "Start")]
    internal static class DediMinimapTextureSkip
    {
        private const int ServerTextureSize = 16;

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Prefix(Minimap __instance)
        {
            if (__instance == null || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null) return;
            int was = __instance.m_textureSize;
            if (was <= ServerTextureSize) return;
            __instance.m_textureSize = ServerTextureSize;
            long saved = (long)was * was * (2 + 3 + 2 + 2) - (long)ServerTextureSize * ServerTextureSize * 9;   // fog RG16 + map RGB24 + height RHalf + mask 4444
            Debug.Log($"[FiresUnifiedCore] [Core] dedicated server: skipped minimap textures (m_textureSize {was} -> {ServerTextureSize} on a Null graphics device, about {saved / (1024 * 1024)} MB not allocated)");
        }
    }
}
