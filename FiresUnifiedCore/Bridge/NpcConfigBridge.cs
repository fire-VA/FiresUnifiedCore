using System;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Host-services seam for companion-engine config values + the admin check. The host (or the
    /// standalone mod) registers providers that map a key to its BepInEx config entry; while none is
    /// present every getter returns the caller's supplied default and IsAdmin returns false. Keys are
    /// the config-property name without the "config" prefix (e.g. "CompanionIdleWanderRadius").
    /// </summary>
    public static class NpcConfigBridge
    {
        public static Func<string, float, float> GetFloatValue;
        public static Func<string, bool, bool> GetBoolValue;
        public static Func<bool> AdminCheck;

        public static float GetFloat(string key, float fallback)
        {
            try { return GetFloatValue != null ? GetFloatValue(key, fallback) : fallback; } catch { return fallback; }
        }

        public static bool GetBool(string key, bool fallback)
        {
            try { return GetBoolValue != null ? GetBoolValue(key, fallback) : fallback; } catch { return fallback; }
        }

        public static bool IsAdmin()
        {
            try { return AdminCheck != null && AdminCheck(); } catch { return false; }
        }
    }
}
