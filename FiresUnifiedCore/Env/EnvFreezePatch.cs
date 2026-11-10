using HarmonyLib;

namespace FiresCore.Env
{
    /// <summary>
    /// CoreEnvApi pause = a freeze: vanilla Game.UpdatePause sets Time.timeScale = 1 whenever peers are connected, so while a Studio pause
    /// lock is applied on this machine the postfix holds it at 0. Does nothing unless EnvLockRpc.Frozen; the next UpdatePause after a
    /// clear puts vanilla's own value back. World (ZNet) time keeps counting: "freeze now, true stop later" (Fire 10-29).
    /// </summary>
    [HarmonyPatch(typeof(Game), "UpdatePause")]
    internal static class EnvFreezePatch
    {
        private static void Postfix()
        {
            if (EnvLockRpc.Frozen) UnityEngine.Time.timeScale = 0f;
        }
    }
}
