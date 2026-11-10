using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using FiresCore.Logging;

namespace FiresCore.Compat
{
    /// <summary>
    /// 0.2.1190 ([generator]; Fire 11-06 "if vikhaven depends on core in any way shape or form we fix it"): VikHavn Combat 1.0.15
    /// knows nothing of Core, and its Ring (VikHavnCombat.Modules.Common.Ring) gives world-up on its own. With VikHavn installed,
    /// Core answers that Ring's InField / Up / Floor from GravityUp and Surface (the ONE ring authority), so on a halo ring lunges,
    /// launches, dashes, grabs and taunt markers keep the local up exactly as before. Same direction as Crawl's SetProne: Core
    /// adapts to VikHavn, VikHavn is never edited for Core. Attached once at the first FejdStartup.Start (every plugin loaded).
    /// Info once: "[VikHavnRing] VikHavn's ring up/floor answered by Core" or the stand-down naming the missing member.
    /// </summary>
    internal static class VikHavnRing
    {
        private const string VikHavnGuid = "VikHavn.CombatMovesetAdditions";
        private const string RingType = "VikHavnCombat.Modules.Common.Ring";
        private static Harmony s_harmony;
        private static bool s_tried;

        internal static void Register(Harmony harmony)
        {
            s_harmony = harmony;
            try
            {
                MethodInfo start = AccessTools.Method(typeof(FejdStartup), "Start");
                if (start != null) harmony.Patch(start, postfix: new HarmonyMethod(typeof(VikHavnRing), nameof(AfterStart)));
            }
            catch (Exception ex) { Debug.LogWarning($"[VikHavnRing] not attached: {ex.Message}"); }
        }

        private static void AfterStart()
        {
            if (s_tried) return;
            s_tried = true;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(VikHavnGuid))
                {
                    FiresLogger.LogDebug("[VikHavnRing] VikHavn not installed: nothing to adapt");
                    return;
                }
                Type ring = OptionalTypes.Find(RingType);
                MethodInfo inField = ring?.GetMethod("InField", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3) }, null);
                MethodInfo up = ring?.GetMethod("Up", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3) }, null);
                MethodInfo floor = ring?.GetMethod("Floor", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3), typeof(Vector3).MakeByRefType() }, null);
                string missing = ring == null ? RingType : inField == null || inField.ReturnType != typeof(bool) ? "Ring.InField(Vector3)"
                    : up == null || up.ReturnType != typeof(Vector3) ? "Ring.Up(Vector3)" : floor == null || floor.ReturnType != typeof(bool) ? "Ring.Floor(Vector3, out Vector3)" : null;
                if (missing != null)
                {
                    Debug.Log($"[VikHavnRing] stand-down: VikHavn's {missing} not found (renamed?); VikHavn keeps world-up on a ring world");
                    return;
                }
                s_harmony.Patch(inField, prefix: new HarmonyMethod(typeof(VikHavnRing), nameof(InFieldPrefix)));
                s_harmony.Patch(up, prefix: new HarmonyMethod(typeof(VikHavnRing), nameof(UpPrefix)));
                s_harmony.Patch(floor, prefix: new HarmonyMethod(typeof(VikHavnRing), nameof(FloorPrefix)));
                Debug.Log("[VikHavnRing] VikHavn's ring up/floor answered by Core (GravityUp / Surface); off a ring world it stays world-up");
            }
            catch (Exception ex) { Debug.LogWarning($"[VikHavnRing] stand-down: {ex.Message}; VikHavn keeps world-up"); }
        }

        // Off the ring GravityUp.Active is one cached bool, so these cost nothing on a normal world.
        private static bool InFieldPrefix(Vector3 pos, ref bool __result)
        {
            try { __result = FiresCore.World.GravityUp.Active && FiresCore.World.GravityUp.InField(pos); }
            catch { __result = false; }
            return false;
        }

        private static bool UpPrefix(Vector3 pos, ref Vector3 __result)
        {
            Vector3 result = Vector3.up;
            try
            {
                if (FiresCore.World.GravityUp.Active && FiresCore.World.GravityUp.InField(pos, out Vector3 up) && up.sqrMagnitude >= 0.0001f)
                    result = up.normalized;
            }
            catch { result = Vector3.up; }
            __result = result;
            return false;
        }

        private static bool FloorPrefix(Vector3 pos, ref Vector3 floor, ref bool __result)
        {
            floor = pos;
            __result = false;
            try
            {
                if (FiresCore.World.GravityUp.Active && FiresCore.World.GravityUp.InField(pos)
                    && FiresCore.World.Surface.Point(pos, out Vector3 point, out _))
                {
                    floor = point;
                    __result = true;
                }
            }
            catch { floor = pos; __result = false; }
            return false;
        }
    }
}
