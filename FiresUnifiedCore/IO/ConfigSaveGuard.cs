using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;

namespace FiresCore.IO
{
    // A player's log had 29 "Sharing violation" IOExceptions from ConfigFile.Save inside Bind, one per mod,
    // Core's own included, before Core's file-watch hub was even installed: something outside the game (an on-access
    // scanner or sync tool) held each cfg for a moment right after it was written. Bind saves after every new key, so the
    // throw killed each mod's Awake halfway - InfinityHammer's took the vanilla console down with it and blocked the menu.
    // Core loads early, so it guards every ConfigFile: a Save that fails with an IOException is retried a few times, and if
    // the file stays locked the values stay in memory (the next Save writes them) and one warning names the file. Nothing
    // runs unless a Save actually throws.
    internal static class ConfigSaveGuard
    {
        private const string LogPrefix = "[ConfigSaveGuard] ";
        private static readonly int[] RetryDelaysMs = { 20, 40, 80, 160 };
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        [ThreadStatic] private static bool s_retrying;
        private static bool s_installed;

        public static void Install(Harmony harmony)
        {
            if (s_installed) return;
            try
            {
                harmony.Patch(AccessTools.Method(typeof(ConfigFile), nameof(ConfigFile.Save)),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(ConfigSaveGuard), nameof(SaveFinalizer))));
                s_installed = true;
            }
            catch (Exception ex)
            {
                FiresUnifiedCore.Log?.LogWarning($"{LogPrefix}not installed: {ex.Message}");
            }
        }

        private static Exception SaveFinalizer(ConfigFile __instance, Exception __exception)
        {
            if (!(__exception is IOException) || s_retrying || __instance == null) return __exception;
            s_retrying = true;
            try
            {
                for (int i = 0; i < RetryDelaysMs.Length; i++)
                {
                    Thread.Sleep(RetryDelaysMs[i]);
                    try
                    {
                        __instance.Save();
                        FiresUnifiedCore.Log?.LogInfo($"{LogPrefix}{Path.GetFileName(__instance.ConfigFilePath)} saved on retry {i + 1} ({__exception.Message})");
                        return null;
                    }
                    catch (IOException) { }
                }
                bool first;
                lock (Warned) first = Warned.Add(__instance.ConfigFilePath ?? "");
                if (first)
                    FiresUnifiedCore.Log?.LogWarning($"{LogPrefix}{__instance.ConfigFilePath} is locked by another program ({__exception.Message}); " +
                                                     "settings kept in memory and written on the next save. Usually antivirus or a sync tool scanning the profile folder.");
                return null;
            }
            finally
            {
                s_retrying = false;
            }
        }
    }
}
