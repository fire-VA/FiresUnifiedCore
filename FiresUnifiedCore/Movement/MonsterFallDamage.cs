using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Movement
{
    /// <summary>
    /// Fall damage for non-player characters, on the players' own terms. Vanilla 1.0.16 hurts only players
    /// (Character.UpdateGroundContact: <c>if (IsPlayer() &amp;&amp; num &gt; 4f)</c>, damage Clamp01((drop - 4) / 16) x 100, then
    /// SEMan.ModifyFallDamage, a HitData of type Fall). With "Monster fall damage" on, every other character takes the same, once
    /// per landing, on its ZDO owner only (vanilla runs UpdateGroundContact on the owner only). Skipped: flyers, fish, swimming /
    /// in liquid, attached (ridden, seated), and any body code moved up more than 3 m in one step (void rescue, goto, teleports):
    /// its fall height starts again from where it was put.
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Movement)]
    public static class MonsterFallDamage
    {
        public enum Mode { Auto, On, Off }

        private const float SafeDrop = 4f, FullDrop = 16f, FullDamage = 100f, TeleportRise = 3f;

        private static ConfigEntry<Mode> s_mode;
        private static int s_resolvedFor = -1;
        private static bool s_enabled;

        private static readonly AccessTools.FieldRef<Character, bool> GroundContact = AccessTools.FieldRefAccess<Character, bool>("m_groundContact");
        private static readonly AccessTools.FieldRef<Character, float> MaxAirAltitude = AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude");
        private static readonly AccessTools.FieldRef<Character, Vector3> LastGroundPoint = AccessTools.FieldRefAccess<Character, Vector3>("m_lastGroundPoint");
        private static readonly AccessTools.FieldRef<Character, Vector3> LastGroundNormal = AccessTools.FieldRefAccess<Character, Vector3>("m_lastGroundNormal");

        private static readonly Dictionary<int, float> s_lastY = new Dictionary<int, float>();
        private static float s_nextPrune, s_nextSkipLog;

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            s_mode = config.Bind("Movement", "Monster fall damage", Mode.Off,
                "SERVER. Non-player characters (monsters, tames, NPCs) take the same fall damage players do: none up to 4 m, rising to 100 at 20 m. "
                + "Auto: off (no feature mod asks for it yet). On / Off: always.");
            configSync?.AddConfigEntry(s_mode);
            FiresCore.Config.CoreFeatures.GateConfig(FiresCore.Config.CoreFeatures.Movement, true, s_mode);   // 0.2.387: FAT only
            s_mode.SettingChanged += (sender, args) => s_resolvedFor = -1;
        }

        public static bool Enabled
        {
            get
            {
                if (s_mode == null) return false;
                if (s_resolvedFor == Time.frameCount || s_resolvedFor == -2) return s_enabled;
                bool was = s_enabled;
                s_enabled = s_mode.Value == Mode.On;   // Auto resolves Off
                if (s_resolvedFor == -1 || was != s_enabled)
                    FiresCore.Logging.FiresLogger.LogDebug($"[FallDamage] monster fall damage {(s_enabled ? "ON" : "OFF")} ({s_mode.Value})");
                s_resolvedFor = -2;
                return s_enabled;
            }
        }

        private static bool Exempt(Character c, out string why)
        {
            why = null;
            if (c.IsPlayer()) why = "player";
            else if (c.m_flying) why = "flyer";
            else if (c.GetComponent<Fish>() != null) why = "fish";
            else if (c.IsSwimming() || c.InLiquid()) why = "in water";
            else if (c.IsAttached()) why = "attached";
            return why != null;
        }

        [HarmonyPatch(typeof(Character), "UpdateGroundContact")]
        private static class Character_UpdateGroundContact_FallDamage
        {
            private static void Prefix(Character __instance, ref float __state)
            {
                __state = 0f;
                if (!Enabled || __instance == null || __instance.IsPlayer()) return;
                try
                {
                    int id = __instance.GetInstanceID();
                    float y = __instance.transform.position.y;
                    if (s_lastY.TryGetValue(id, out float lastY) && y - lastY > TeleportRise)
                    {
                        MaxAirAltitude(__instance) = y;   // moved up by code: the fall starts here
                        if (Time.time >= s_nextSkipLog)
                        {
                            s_nextSkipLog = Time.time + 1f;
                            FiresCore.Logging.FiresLogger.LogDebug($"[FallDamage] {__instance.m_name} moved up {y - lastY:0.0} m in one step: fall height reset");
                        }
                    }
                    s_lastY[id] = y;
                    if (Time.time >= s_nextPrune) { s_nextPrune = Time.time + 60f; if (s_lastY.Count > 4096) s_lastY.Clear(); }

                    if (!GroundContact(__instance)) return;
                    float drop = Mathf.Max(0f, MaxAirAltitude(__instance) - y);
                    if (drop <= SafeDrop || Exempt(__instance, out _)) return;
                    __state = drop;
                }
                catch (Exception ex) { Debug.LogWarning($"[FallDamage] {ex.Message}"); }
            }

            private static void Postfix(Character __instance, float __state)
            {
                if (__state <= SafeDrop || __instance == null || __instance.IsDead()) return;
                try
                {
                    float damage = Mathf.Clamp01((__state - SafeDrop) / FullDrop) * FullDamage;
                    __instance.GetSEMan()?.ModifyFallDamage(damage, ref damage);
                    if (damage <= 0f) return;
                    var hit = new HitData();
                    hit.m_damage.m_damage = damage;
                    hit.m_point = LastGroundPoint(__instance);
                    hit.m_dir = LastGroundNormal(__instance);
                    hit.m_hitType = HitData.HitType.Fall;
                    __instance.Damage(hit);
                    FiresCore.Logging.FiresLogger.LogDebug($"[FallDamage] {__instance.m_name} fell {__state:0.0} m: {damage:0.0} damage");
                }
                catch (Exception ex) { Debug.LogWarning($"[FallDamage] {ex.Message}"); }
            }
        }
    }
}
