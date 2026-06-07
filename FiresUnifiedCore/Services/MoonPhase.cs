using UnityEngine;

namespace FiresCore.Services
{
    // Shared lunar-phase math. Pure EnvMan/ZNet — no rendering, no bundle.
    // FiresValheimGalaxies reads this for BOTH its visible 3D moon and the tides
    // that moon drives, so the moon you SEE and the tide cycle stay in lockstep.
    // It lives in FUC (not Galaxies) so any other Fires-* mod can read the same
    // phase — e.g. a future FiresTossinShade moonlight-on-water or aurora-by-phase
    // effect — without taking a dependency on Galaxies' internals.
    //
    // Phase is derived from Valheim's day count + day fraction, so it's
    // deterministic across clients (no sync needed) and auto-compensates for any
    // mod that changes day length. Resets every SynodicDays in-game days.
    public static class MoonPhase
    {
        // Game days per full lunar cycle (new → full → new).
        public static float SynodicDays = 30f;

        // Shift the starting phase (whole/fractional in-game days).
        public static float PhaseOffsetDays = 0f;

        // >= 0 forces a fixed phase (debug / preview commands); < 0 = use the
        // game clock. Mirrors the old FiresMoon.PhaseOverride.
        public static float Override = -1f;

        // 0..1 lunar phase. 0 and 1 = new moon, 0.5 = full moon.
        public static float Compute01()
        {
            if (Override >= 0f) return Mathf.Repeat(Override, 1f);
            try
            {
                if (EnvMan.instance == null) return 0.5f;   // fallback to full
                int day = EnvMan.instance.GetDay(ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0);
                float dayFrac = EnvMan.instance.GetDayFraction();
                float totalDay = day + dayFrac + PhaseOffsetDays;
                return Mathf.Repeat(totalDay / Mathf.Max(0.1f, SynodicDays), 1f);
            }
            catch { return 0.5f; }
        }
    }
}
