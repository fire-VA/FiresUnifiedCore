using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Compat
{
    /// <summary>
    /// A vanilla error at quit (Fire: "that's a vanilla error and if we can fix it we should"; [fgn]'s trace, 2026-09-30).
    ///
    /// The game's MagicaCloth 2 is set up once at SubsystemRegistration, and MagicaManager.OnAppQuitting (Application.quitting) sets
    /// its managers to null. EnvMan's GameObject carries a MagicaWindZone, and its Awake/OnEnable call MagicaManager.Wind.AddWind /
    /// SetEnable with no null check (OnDisable/OnDestroy use Wind?.). When a game quits while the main menu scene is still loading
    /// after a logout, the menu's EnvMan wakes after quitting began, and Awake throws "NullReferenceException at
    /// MagicaCloth2.MagicaWindZone.Awake". With the managers gone there is nothing to register, so Awake and OnEnable are skipped then.
    ///
    /// Found by name at load (Core doesn't reference MagicaClothV2.dll); skipped entirely when the type isn't there.
    /// </summary>
    [HarmonyPatch]
    internal static class MagicaWindZoneQuitGuard
    {
        private static Type s_manager;
        private static Func<object> s_wind;
        private static bool s_logged;

        private static bool Prepare()
        {
            Type zone = AccessTools.TypeByName("MagicaCloth2.MagicaWindZone");
            s_manager = AccessTools.TypeByName("MagicaCloth2.MagicaManager");
            if (zone == null || s_manager == null) return false;
            PropertyInfo prop = AccessTools.Property(s_manager, "Wind");
            if (prop != null && prop.GetGetMethod(true)?.IsStatic == true) s_wind = () => prop.GetValue(null);
            else
            {
                FieldInfo field = AccessTools.Field(s_manager, "Wind");
                if (field == null || !field.IsStatic) return false;
                s_wind = () => field.GetValue(null);
            }
            return true;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type zone = AccessTools.TypeByName("MagicaCloth2.MagicaWindZone");
            if (zone == null) yield break;
            foreach (string name in new[] { "Awake", "OnEnable" })
            {
                MethodInfo m = AccessTools.Method(zone, name);
                if (m != null) yield return m;
            }
        }

        // False (skip the original) only when the wind manager is gone: during a quit.
        private static bool Prefix(MethodBase __originalMethod)
        {
            object wind;
            try { wind = s_wind(); }
            catch { return true; }
            if (wind != null) return true;
            if (!s_logged)
            {
                s_logged = true;
                Debug.Log($"[MagicaWindZoneQuitGuard] MagicaWindZone.{__originalMethod?.Name} skipped: MagicaCloth's managers are already disposed (the game is quitting)");
            }
            return false;
        }
    }
}
