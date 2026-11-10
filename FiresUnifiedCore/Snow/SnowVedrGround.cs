using System;
using System.Reflection;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.1197 ([generator], Fire 11-07: "Port Core's ground snow into Vedr"; names agreed with [seasons]): while Vedr's own ground-snow
    /// driver owns the vanilla heightmap snow (<c>Vedr.VedrApi.GroundSnowActive</c>, VedrApi 15), Core's vanilla-ground writers stand down:
    /// SnowBaseMask puts vanilla's G back (the FAT-zone path), and SnowHeightmapInject, SnowMeshRaise, SnowRefill, the trail keep-paint,
    /// the cultivated / clamp rules and SnowGroundAsDeepNorth do nothing. Everything other mods read (SnowField, SnowTrail for FAT
    /// voxel, SnowPathMask, the cover) keeps answering. Soft reflection, the FTS pattern: a missing type or member counts as false, so
    /// without Vedr, or with an older Vedr, Core runs exactly as before. Re-read on SnowTicker's 2 s step (no new poll).
    /// </summary>
    internal static class SnowVedrGround
    {
        private const string VedrApiType = "Vedr.VedrApi, Norger.Vedr";
        private const string FlagName = "GroundSnowActive";

        private static bool s_bound;
        private static Func<bool> s_flag;
        private static bool s_owns;
        private static bool s_said;

        /// <summary>True while Vedr draws the ground snow (Core's vanilla-ground writers stand down).</summary>
        internal static bool Owns => s_owns;

        /// <summary>0.2.1198: goes up at every owner change, so a hand-over's one-time restore is per hand-over.</summary>
        internal static int Epoch { get; private set; }

        private static void Bind()
        {
            s_bound = true;
            try
            {
                var t = Type.GetType(VedrApiType);
                var p = t?.GetProperty(FlagName, BindingFlags.Public | BindingFlags.Static);
                var get = p != null && p.PropertyType == typeof(bool) ? p.GetGetMethod() : null;
                if (get != null) s_flag = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), get);
                FiresLogger.LogDebug(t == null ? "[Snow] ground snow: Vedr not installed, Core runs it"
                    : s_flag == null ? $"[Snow] ground snow: {VedrApiType} has no {FlagName} (older Vedr), Core runs it"
                    : $"[Snow] ground snow: bound {VedrApiType}.{FlagName}");
            }
            catch (Exception e) { s_flag = null; FiresLogger.LogDebug($"[Snow] ground snow: Vedr flag bind failed ({e.Message}), Core runs it"); }
        }

        /// <summary>From SnowTicker's 2 s step. True when the owner changed (the caller re-bakes the loaded chunks once).</summary>
        internal static bool Refresh()
        {
            if (!s_bound) Bind();
            bool now = false;
            if (s_flag != null) { try { now = s_flag(); } catch { now = false; } }
            if (s_owns && FiresLogger.DebugEnabled) { int put = SnowBaseMask.TakeHandedRestored(); if (put > 0) FiresLogger.LogDebug($"[Snow] ground snow: {put} chunk(s) put back to vanilla G once for Vedr (hand-over {Epoch})"); }   // 0.2.1198
            if (now == s_owns && s_said) return false;
            bool changed = now != s_owns;
            s_owns = now; s_said = true;
            if (changed) Epoch++;   // 0.2.1198: each hand-over restores a chunk's mask once (SnowBaseMask)
            if (changed || now)
                FiresLogger.LogInfo(now
                    ? "[Snow] ground snow: Vedr owns it (VedrApi.GroundSnowActive); Core's vanilla-ground writers stand down, every snow query still answers"
                    : "[Snow] ground snow: Core runs it again (VedrApi.GroundSnowActive false)");
            return changed;
        }
    }
}
