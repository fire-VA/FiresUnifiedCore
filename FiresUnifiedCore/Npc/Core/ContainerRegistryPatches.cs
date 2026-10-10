using HarmonyLib;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Harmony patches to automatically track containers as they are created and destroyed.
    /// 
    /// INSPIRED BY: SmartContainers mod's container tracking system.
    /// 
    /// These patches ensure ContainerRegistry always has an up-to-date list of all
    /// containers without needing to scan with Physics.OverlapSphere.
    /// </summary>
    public static class ContainerRegistryPatches
    {
        /// <summary>
        /// Registers containers when they wake up.
        /// </summary>
        [HarmonyPatch(typeof(Container), "Awake")]
        public static class Container_Awake_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Container __instance)
            {
                ContainerRegistry.Register(__instance);
            }
        }
        
        /// <summary>
        /// Unregisters containers when they are destroyed.
        /// </summary>
        [HarmonyPatch(typeof(Container), "OnDestroyed")]
        public static class Container_OnDestroyed_Patch
        {
            [HarmonyPrefix]
            public static void Prefix(Container __instance)
            {
                ContainerRegistry.Unregister(__instance);
            }
        }
        
        /// <summary>
        /// Initialize the registry when the game starts.
        /// </summary>
        [HarmonyPatch(typeof(Game), "Start")]
        public static class Game_Start_Patch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                ContainerRegistry.Initialize();
            }
        }
        
        /// <summary>
        /// Clear the registry when returning to menu.
        /// </summary>
        [HarmonyPatch(typeof(Game), "Logout")]
        public static class Game_Logout_Patch
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                ContainerRegistry.Clear();
            }
        }
        
        /// <summary>
        /// Cleanup invalid containers periodically during zone loading.
        /// </summary>
        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        public static class ZoneSystem_Update_Patch
        {
            private static int _frameCounter = 0;
            
            [HarmonyPostfix]
            public static void Postfix()
            {
                // Only check every 300 frames (~5 seconds at 60fps)
                _frameCounter++;
                if (_frameCounter >= 300)
                {
                    _frameCounter = 0;
                    ContainerRegistry.Cleanup();
                }
            }
        }
    }
}
