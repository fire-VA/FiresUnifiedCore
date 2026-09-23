using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Lifecycle
{
    /// <summary>
    /// Character.UpdateLava asks the world what ground the character is standing on, every fixed step, for every
    /// character this machine owns. The answer costs a ten kilometre raycast, a GetComponent for the heightmap it hit
    /// and a biome lookup - and the check for whether the character is anywhere near the Ashlands comes on the line
    /// AFTER it, so the whole thing is paid everywhere in the world. A client holds ownership of nearly everything
    /// around it, so in a settlement that is most of the characters on screen, fifty times a second.
    ///
    /// The three values it fills in are read in exactly two places: lava, which needs the Ashlands, and the deep snow
    /// that slows you in the Deep North. Inside the Ashlands vanilla runs untouched. Everywhere else the only work its
    /// method does is two timers and this lookup, so that is what is done here instead, and the lookup is repeated only
    /// once the character has actually moved or enough time has passed for the ground under it to be a different answer.
    /// </summary>
    [HarmonyPatch]
    internal static class GroundDataThrottle
    {
        private const string Section = "Performance";
        private const string Key = "Reuse Ground Checks";
        private const string Description =
            "ON by default. Every character this machine controls asks what ground it is standing on fifty times a " +
            "second, which costs a ten kilometre raycast each time, everywhere in the world - the check for whether it " +
            "is somewhere that answer matters happens afterwards. With this on the answer is reused until the character " +
            "has moved far enough or long enough for it to change. The Ashlands are left exactly as the game does them. " +
            "Turn it OFF if deep snow in the Deep North stops slowing characters correctly. This machine only; read live.";

        private const float RefreshSeconds = 0.35f;
        private const float RefreshDistance = 0.75f;
        private const int MaxTracked = 2048;

        private static ConfigEntry<bool> s_enabled;

        private sealed class Ground
        {
            public Vector3 At;
            public float When;
        }

        private static readonly Dictionary<Character, Ground> s_tracked = new Dictionary<Character, Ground>();

        private static readonly AccessTools.FieldRef<Character, float> s_lavaTimer
            = AccessTools.FieldRefAccess<Character, float>("m_lavaTimer");
        private static readonly AccessTools.FieldRef<Character, float> s_aboveOrInLavaTimer
            = AccessTools.FieldRefAccess<Character, float>("m_aboveOrInLavaTimer");
        private static readonly AccessTools.FieldRef<Character, Vector3> s_lastGroundHeight
            = AccessTools.FieldRefAccess<Character, Vector3>("m_lastGroundHeight");
        private static readonly AccessTools.FieldRef<Character, Heightmap.Biome> s_lastBiome
            = AccessTools.FieldRefAccess<Character, Heightmap.Biome>("m_lastBiome");
        private static readonly AccessTools.FieldRef<Character, Heightmap> s_lastHeightmap
            = AccessTools.FieldRefAccess<Character, Heightmap>("m_lastHeightmap");

        internal static long Refreshed;
        internal static long Reused;

        internal static void Initialize(ConfigFile config)
        {
            s_enabled = config.Bind(Section, Key, true, Description);
        }

        [HarmonyPatch(typeof(Character), "UpdateLava")]
        [HarmonyPrefix]
        public static bool UpdateLava_Prefix(Character __instance, float dt)
        {
            try
            {
                if (s_enabled == null || !s_enabled.Value || ZoneSystem.instance == null) return true;

                Vector3 position = __instance.transform.position;
                // Where lava exists, vanilla runs exactly as it always has.
                if (WorldGenerator.IsAshlands(position.x, position.z)) return true;

                s_lavaTimer(__instance) += dt;
                s_aboveOrInLavaTimer(__instance) += dt;

                if (!s_tracked.TryGetValue(__instance, out var ground))
                {
                    if (s_tracked.Count >= MaxTracked) Sweep();
                    ground = new Ground { When = float.NegativeInfinity };
                    s_tracked[__instance] = ground;
                }

                float now = Time.time;
                if (now - ground.When < RefreshSeconds
                    && (position - ground.At).sqrMagnitude < RefreshDistance * RefreshDistance)
                {
                    Reused++;
                    return false;
                }

                ground.At = position;
                ground.When = now;
                Refreshed++;

                s_lastGroundHeight(__instance) = position;
                ZoneSystem.instance.GetGroundData(ref s_lastGroundHeight(__instance), out Vector3 _,
                    out s_lastBiome(__instance), out Heightmap.BiomeArea _, out s_lastHeightmap(__instance));
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void Sweep()
        {
            var gone = new List<Character>();
            foreach (var pair in s_tracked)
                if (pair.Key == null) gone.Add(pair.Key);
            for (int i = 0; i < gone.Count; i++) s_tracked.Remove(gone[i]);
            if (s_tracked.Count >= MaxTracked) s_tracked.Clear();
        }
    }
}
