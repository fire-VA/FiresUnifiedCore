using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Movement
{
    /// <summary>
    /// Player swimming (Core 0.2.298; Fire 10-05: swimming moves into Core "like crawling and climbing" with a toggle "so people can choose
    /// to use it without galaxies"; "when a cave passes under a water plane (like the ocean) the water shouldnt be registered within the
    /// cave, only cave water WE place should ever register a swim trigger"). Two modes since Fire's refinement (10-05 22:3x): "when we
    /// surface we should remain boyant, but when we dive we should remain neutral ... so that diving is something purposeful".
    /// Setting [Swimming] Swimming = Auto | On | Off, default Auto: on when Fires Valheim Galaxies is installed. An explicit On or Off
    /// always wins; nothing here ever writes the setting.
    /// While on, for the local player:
    /// - SURFACE (entering the water, always): vanilla swimming, floating at the surface. Holding Jump changes nothing there.
    /// - DIVE (Crouch pressed or held while swimming): neutral buoyancy. Crouch held sinks, Jump held rises, at "Vertical swim speed"; with
    ///   neither held the body keeps the depth it is at, nothing pulls it up. Rising back to where vanilla floats a swimmer with Jump held
    ///   returns to SURFACE, which stays after Jump is let go; only Crouch starts a dive again. The game's own swim step does the moving:
    ///   only the depth it aims for changes, for the length of that step. Crouch does not toggle sneaking while swimming.
    /// - CAVES: the sea (vanilla's WaterVolume water) does not register for anyone standing in a cave under it: no swim state, no
    ///   float, no pinning to the roof. Water placed with tools (FiresAdminTerrain's own water) still does. Whether a point is in a
    ///   cave is RoofAbove, a query the terrain mod sets (null = open sky everywhere).
    /// - CAMERA: without Galaxies, the camera may follow the head under the surface while it is under (Galaxies' own camera release,
    ///   which also covers riding ships, asks HeadUnderwater instead).
    /// Debug (never Info): "[Swim] swimming ON|OFF (why)", "[Swim] mode: surface -> dive (Ctrl)", "[Swim] mode: dive -> surface (reached
    /// the top)", "[Swim] dive: hold / rise / sink at y=...", "[Swim] the sea ignored under a cave roof at (...)", "[Swim] open water again".
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Swimming)]
    public static class PlayerSwim
    {
        public enum SwimmingMode { Auto, On, Off }

        public const string GalaxiesGuid = "com.Fire.FiresValheimGalaxies";

        /// <summary>
        /// Set by the terrain mod: true when the point stands in a cave (solid ground over it, below the generated surface) under a
        /// sea whose surface is the given height. Null means no cave information: open sky everywhere.
        /// </summary>
        public static Func<Vector3, float, bool> RoofAbove;

        private const float UnderwaterCameraDistance = -1000f;
        private const float HoldLead = 1f;           // a rise or sink never aims more than this past the body (a roof or the bed stops it)
        private const float ResyncDistance = 3f;     // pushed further than this from the held height (a current, a hit): hold where it is
        private const float SwimGapSeconds = 0.5f;   // no swim step for this long = left the water; the next swim starts at the surface
        private const float TopReached = 0.15f;      // within this of the float line counts as back at the top

        private static ConfigEntry<SwimmingMode> s_mode;
        private static ConfigEntry<float> s_verticalSpeed;
        private static int s_resolvedFor = -1;
        private static bool s_enabled, s_galaxies, s_galaxiesKnown;

        private static bool s_diving;
        private static float s_holdY = float.NaN;
        private static float s_lastSwimTime = -100f;
        private static float s_savedSwimDepth = float.NaN;
        private static int s_lastControl = int.MinValue;   // -1 sink, 0 hold, 1 rise (dive only)
        private static bool s_underRoof;
        private static float? s_cameraMinWater;

        internal static void Bind(ConfigFile config)
        {
            s_mode = config.Bind("Swimming", "Swimming", SwimmingMode.Auto,
                "Diving with neutral buoyancy (Crouch dives and sinks, Jump rises, otherwise keep your depth; back at the top you float as "
                + "usual) and caves below the sea staying dry. Auto: on only when Fires Valheim Galaxies is installed. On / Off: always.");
            s_verticalSpeed = config.Bind("Swimming", "Vertical swim speed", 2.5f,
                new ConfigDescription("Metres per second the player rises or sinks while Jump or Crouch is held in a dive.",
                    new AcceptableValueRange<float>(0.5f, 8f)));
            s_mode.SettingChanged += (sender, args) => s_resolvedFor = -1;
            FiresCore.Config.CoreFeatures.GateConfig(FiresCore.Config.CoreFeatures.Swimming, false, s_mode, s_verticalSpeed);   // 0.2.387: Galaxies only
        }

        /// <summary>True when Core drives player swimming.</summary>
        public static bool Enabled
        {
            get
            {
                SwimmingMode mode = s_mode != null ? s_mode.Value : SwimmingMode.Auto;
                if (s_resolvedFor == (int)mode) return s_enabled;
                s_enabled = mode == SwimmingMode.On || (mode == SwimmingMode.Auto && GalaxiesInstalled);
                s_resolvedFor = (int)mode;
                string why = mode == SwimmingMode.Auto ? $"Auto: Galaxies {(GalaxiesInstalled ? "present" : "not installed")}" : $"{mode} (config)";
                FiresCore.Logging.FiresLogger.LogDebug($"[Swim] swimming {(s_enabled ? "ON" : "OFF")} ({why})");
                return s_enabled;
            }
        }

        /// <summary>The local player is in a dive (neutral buoyancy); false at the surface, out of the water, or with swimming off.</summary>
        public static bool Diving => s_diving && Enabled && Time.time - s_lastSwimTime <= SwimGapSeconds;

        /// <summary>The local player is swimming with the head under the surface (Galaxies' camera release asks this).</summary>
        public static bool HeadUnderwater
        {
            get
            {
                var player = Player.m_localPlayer;
                if (player == null || !player.IsSwimming()) return false;
                return player.GetHeadPoint().y < player.GetLiquidLevel() - 0.1f;
            }
        }

        private static float VerticalSpeed => s_verticalSpeed != null ? s_verticalSpeed.Value : 2.5f;

        // Galaxies installed (read once: the plugin set does not change in a session).
        private static bool GalaxiesInstalled
        {
            get
            {
                if (!s_galaxiesKnown)
                {
                    s_galaxies = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(GalaxiesGuid);
                    s_galaxiesKnown = true;
                }
                return s_galaxies;
            }
        }

        private static void Log(string format, params object[] args)
            => FiresCore.Logging.FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));

        // The local player's swim step. SURFACE leaves vanilla alone; DIVE aims vanilla's depth controller at the held height for this
        // step only.
        private static void BeforeSwimStep(Character character, float dt)
        {
            if (!Enabled || !(character is Player player) || player != Player.m_localPlayer) return;
            float liquid = player.GetLiquidLevel();
            float baseDepth = player.m_swimDepth;
            float top = liquid - baseDepth;                  // where vanilla floats a swimmer: the highest a dive goes
            float y = player.transform.position.y;
            if (Time.time - s_lastSwimTime > SwimGapSeconds)
            {
                s_diving = false;                            // every entry into the water starts at the surface
                s_lastControl = int.MinValue;
                s_holdY = float.NaN;
            }
            s_lastSwimTime = Time.time;

            bool input = player.TakeInput();
            bool jump = input && (ZInput.GetButton("Jump") || ZInput.GetButton("JoyJump"));
            bool crouch = input && (ZInput.GetButton("Crouch") || ZInput.GetButton("JoyCrouch"));

            if (!s_diving)
            {
                if (!crouch) return;                         // SURFACE: vanilla swimming
                s_diving = true;
                s_holdY = Mathf.Min(y, top);
                s_lastControl = int.MinValue;
                Log("[Swim] mode: surface -> dive (Ctrl) at y={0:0.00} (surface {1:0.00}, float line {2:0.00})", y, liquid, top);
            }

            bool rise = jump, sink = !jump && crouch;
            if (float.IsNaN(s_holdY) || Mathf.Abs(s_holdY - y) > ResyncDistance) s_holdY = Mathf.Min(y, top);
            if (rise) s_holdY = Mathf.Min(s_holdY + VerticalSpeed * dt, y + HoldLead);           // a roof stops a rise
            else if (sink) s_holdY = Mathf.Max(s_holdY - VerticalSpeed * dt, y - HoldLead);      // the body sets the pace
            if (player.IsOnGround() && s_holdY < y) s_holdY = y;                                 // the bed stops a sink
            // Never above the float line, for THIS step only (0.2.304; overnight swim_test: a 10 s hold drifted 1.23 m). The
            // line rides the waves; clamping the stored hold to it lowered the hold at every trough and never raised it back.
            float target = Mathf.Min(s_holdY, top);

            if (rise && y >= top - TopReached)
            {
                s_diving = false;                            // back at the top with Jump held: SURFACE, vanilla from this step on
                s_holdY = float.NaN;
                Log("[Swim] mode: dive -> surface (reached the top) at y={0:0.00} (surface {1:0.00}, float line {2:0.00})", y, liquid, top);
                return;
            }

            int control = rise ? 1 : sink ? -1 : 0;
            if (control != s_lastControl)
            {
                s_lastControl = control;
                Log("[Swim] dive: {0} at y={1:0.00}, held {2:0.00} (surface {3:0.00}, float line {4:0.00})",
                    control > 0 ? "rise" : control < 0 ? "sink" : "hold", y, s_holdY, liquid, top);
            }

            s_savedSwimDepth = baseDepth;
            player.m_swimDepth = Mathf.Max(0.05f, liquid - target);
        }

        private static void AfterSwimStep(Character character)
        {
            if (float.IsNaN(s_savedSwimDepth) || !(character is Player player) || player != Player.m_localPlayer) return;
            player.m_swimDepth = s_savedSwimDepth;   // IsSwimming and the rest of the game keep the player's own depth
            s_savedSwimDepth = float.NaN;
        }

        // The sea does not reach into a cave under it; water placed with tools arrives through other components and still counts.
        private static void FilterLiquid(Character character, ref float level, LiquidType type, Component liquidObj)
        {
            if (type != LiquidType.Water || level <= -9999f || !(liquidObj is WaterVolume) || RoofAbove == null || !Enabled) return;
            bool roof;
            try { roof = RoofAbove(character.transform.position, level); }
            catch (Exception ex)
            {
                FiresCore.Logging.FiresLogger.LogDebug($"[Swim] the cave query threw ({ex.GetType().Name}: {ex.Message}); treated as open water");
                roof = false;
            }
            if (character == Player.m_localPlayer && roof != s_underRoof)
            {
                s_underRoof = roof;
                Vector3 p = character.transform.position;
                if (roof) Log("[Swim] the sea ignored under a cave roof at ({0:0.0},{1:0.0},{2:0.0}) (sea surface {3:0.00})", p.x, p.y, p.z, level);
                else FiresCore.Logging.FiresLogger.LogDebug("[Swim] open water again");
            }
            if (roof) level = -10000f;
        }

        // Without Galaxies, the camera may follow the head under the surface (vanilla keeps it above the water).
        private static void CameraBeforeUpdate(GameCamera camera)
        {
            if (camera == null || GalaxiesInstalled) return;
            bool under = Enabled && HeadUnderwater;
            if (under)
            {
                if (!s_cameraMinWater.HasValue) s_cameraMinWater = camera.m_minWaterDistance;
                camera.m_minWaterDistance = UnderwaterCameraDistance;
            }
            else if (s_cameraMinWater.HasValue)
            {
                camera.m_minWaterDistance = s_cameraMinWater.Value;
                s_cameraMinWater = null;
            }
        }

        [HarmonyPatch(typeof(Character), "UpdateSwimming")]
        private static class Character_UpdateSwimming_PlayerSwim
        {
            private static void Prefix(Character __instance, float dt) => BeforeSwimStep(__instance, dt);

            private static void Finalizer(Character __instance) => AfterSwimStep(__instance);
        }

        [HarmonyPatch(typeof(Character), nameof(Character.SetLiquidLevel))]
        private static class Character_SetLiquidLevel_PlayerSwim
        {
            private static void Prefix(Character __instance, ref float level, LiquidType type, Component liquidObj)
                => FilterLiquid(__instance, ref level, type, liquidObj);
        }

        [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
        private static class Player_SetControls_PlayerSwim
        {
            // Crouch is the dive button while swimming: it must not toggle sneaking.
            private static void Prefix(Player __instance, ref bool crouch)
            {
                if (crouch && __instance == Player.m_localPlayer && __instance.IsSwimming() && Enabled) crouch = false;
            }
        }

        [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
        private static class GameCamera_UpdateCamera_PlayerSwim
        {
            private static void Prefix(GameCamera __instance) => CameraBeforeUpdate(__instance);
        }
    }
}
