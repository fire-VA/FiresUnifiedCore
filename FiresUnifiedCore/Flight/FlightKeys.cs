using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Logging;
using FiresCore.Possession;
using UnityEngine;

namespace FiresCore.Flight
{
    /// <summary>
    /// [Flight] keys for the shared flight controller (FiresPossession.FlightModel), which mounts (FiresMounts) and
    /// possessed flyers use. Flight presets: "Preset" picks the key map, and
    /// Auto (the default) lets each flyer fly its own natural way (FlightPresets.Resolve). Movement keys are the player's own
    /// vanilla binds (the shared ReadLocal reads "Jump" and "Crouch"; no preset binds LMB / MMB / RMB, the attacks). Yaw, the two abilities and stop
    /// have their own keys here. While a flyer reads the keys, the vanilla buttons on the ability and stop keys (auto-pickup V,
    /// toggle walk C, sit X by default) are held back. Nothing is read while typing in chat, the console or a text field.
    /// </summary>
    public static class FlightKeys
    {
        internal static ConfigEntry<KeyCode> YawLeft;
        internal static ConfigEntry<KeyCode> YawRight;
        internal static ConfigEntry<FiresPossession.FlightPreset> PresetEntry;
        internal static ConfigEntry<KeyCode> Ability1Key;
        internal static ConfigEntry<KeyCode> Ability2Key;
        internal static ConfigEntry<KeyCode> StopKey;

        private static int s_lastReadFrame = -100;
        private static readonly Dictionary<string, string> s_bound = new Dictionary<string, string>(StringComparer.Ordinal);
        private static float s_boundUntil;
        private static readonly HashSet<string> s_heldLogged = new HashSet<string>(StringComparer.Ordinal);

        internal static void Bind(ConfigFile config)
        {
            YawLeft = config.Bind("Flight", "Yaw left key", KeyCode.Q, "While flying: turn left flat, without banking.");
            YawRight = config.Bind("Flight", "Yaw right key", KeyCode.E, "While flying: turn right flat, without banking.");
            PresetEntry = config.Bind("Flight", "Preset", FiresPossession.FlightPreset.Auto,
                "How flying is steered. Auto: each flyer flies its own natural way. Any other value gives every flyer that preset's key map.");
            Ability1Key = config.Bind("Flight", "Ability 1 key", KeyCode.V,
                "While flying: the preset's first ability (boost, dash, flap...). The vanilla action on this key (auto-pickup) is held back while you fly.");
            Ability2Key = config.Bind("Flight", "Ability 2 key", KeyCode.C,
                "While flying: the preset's second ability. The vanilla action on this key (toggle walk) is held back while you fly.");
            StopKey = config.Bind("Flight", "Stop key", KeyCode.X,
                "While flying: stop / brake where the preset has it. The vanilla action on this key (sit) is held back while you fly.");
            // Only Valcast and Mounts fly; without either these keys leave the config.
            FiresCore.Config.OwnerGatedConfig.Gate(FiresCore.Config.OwnerGatedConfig.Owners.FLIGHT, false,
                YawLeft, YawRight, PresetEntry, Ability1Key, Ability2Key, StopKey);
        }

        /// <summary>The configured preset (Auto by default; FlightTuning.ApplyPreset / FlightPresets.Resolve pick each flyer's own).</summary>
        public static FiresPossession.FlightPreset Preset => PresetEntry != null ? PresetEntry.Value : FiresPossession.FlightPreset.Auto;

        /// <summary>True when flight input may be read (not typing, no menu over the game).</summary>
        public static bool InputOpen => PossessionRules.KeysOpen();

        /// <summary>A flyer is reading the keys: ReadLocal was called within the last 2 frames (mounts and possessed flyers call it every frame).</summary>
        public static bool Flying => Time.frameCount - s_lastReadFrame <= 2;

        /// <summary>The local player's flight input through Core's guarded keys, with the configured preset; default (hover) while input is closed.</summary>
        public static FiresPossession.FlightInput ReadLocal() => ReadLocal(null);

        /// <summary>
        /// The local player's flight input for <paramref name="flyer"/>: the configured preset, resolved for this flyer when it is Auto,
        /// with Core's keys, the raw mouse delta and the camera's forward. Default (hover) while input is closed.
        /// </summary>
        public static FiresPossession.FlightInput ReadLocal(Character flyer)
        {
            s_lastReadFrame = Time.frameCount;
            if (!InputOpen) return default(FiresPossession.FlightInput);
            var preset = FiresPossession.FlightPresets.Resolve(Preset, flyer);
            var reads = new FiresPossession.FlightReads
            {
                Button = name => ZInput.GetButton(name),
                YawLeft = () => Held(YawLeft),
                YawRight = () => Held(YawRight),
                Ability1 = () => Held(Ability1Key),
                Ability2 = () => Held(Ability2Key),
                Stop = () => Held(StopKey),
                Mouse = ZInput.GetMouseDelta(),
                Look = GameCamera.instance != null ? GameCamera.instance.transform.forward : (flyer != null ? flyer.transform.forward : Vector3.forward),
            };
            return FiresPossession.FlightInput.ReadLocal(preset, in reads);
        }

        /// <summary>The player's own flight keys in one line, for the help page.</summary>
        public static string KeysLine() =>
            $"Your flight keys: Ability 1 {Name(Ability1Key)}, Ability 2 {Name(Ability2Key)}, Stop {Name(StopKey)}, yaw {Name(YawLeft)} / {Name(YawRight)}. Rebind them in F8 > Fires Core > Flight.";

        private static string Name(ConfigEntry<KeyCode> key) => key == null || key.Value == KeyCode.None ? "(none)" : ZInput.KeyCodeToDisplayName(key.Value);

        private static bool Held(ConfigEntry<KeyCode> key) => key != null && key.Value != KeyCode.None && FiresCore.Input.FiresKeys.GetKey(key.Value);

        /// <summary>
        /// While flying, a vanilla button bound to one of the flight keys (ability 1, ability 2, stop) is held back, so V doesn't also
        /// toggle auto-pickup. It compares ZInput's bound key name with the flight key's display name, so rebinding either side is
        /// followed. Called from Core's ZInput.GetButtonDown/Up gate (FiresInputBlockClientGates).
        /// </summary>
        internal static bool ConsumesVanillaButton(string button)
        {
            if (!Flying || string.IsNullOrEmpty(button) || ZInput.instance == null) return false;
            if (Time.unscaledTime > s_boundUntil) { s_bound.Clear(); s_boundUntil = Time.unscaledTime + 2f; }
            if (!s_bound.TryGetValue(button, out string bound))
            {
                try { bound = ZInput.instance.GetBoundKeyString(button, true) ?? ""; } catch { bound = ""; }
                s_bound[button] = bound;
            }
            if (bound.Length == 0) return false;
            if (!Same(bound, Ability1Key) && !Same(bound, Ability2Key) && !Same(bound, StopKey)) return false;
            if (s_heldLogged.Add(button)) FiresLogger.LogDebug($"[Flight] vanilla '{button}' (bound {bound}) held back while flying");
            return true;
        }

        private static bool Same(string bound, ConfigEntry<KeyCode> key) =>
            key != null && key.Value != KeyCode.None && string.Equals(bound, ZInput.KeyCodeToDisplayName(key.Value), StringComparison.OrdinalIgnoreCase);
    }
}
