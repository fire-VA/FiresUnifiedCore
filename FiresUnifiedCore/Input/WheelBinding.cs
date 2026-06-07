using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Input
{
    // Per-action mouse-wheel binding. A module declares one WheelBinding
    // per adjustable parameter (e.g. orbit-cam radius on plain wheel,
    // orbit-cam height on Ctrl+wheel, orbit-cam speed on Shift+wheel).
    //
    // Matching is STRICT: a binding with Modifier.Ctrl fires only when
    // Ctrl is held alone; the no-mod variant fires only when no modifiers
    // are held. This lets a single module map wheel-different-things to
    // different mod combos without conflict.
    //
    // GetDelta returns the signed per-frame delta (wheel up = positive,
    // wheel down = negative) scaled by Sensitivity. Zero when no wheel
    // motion this frame or modifier state doesn't match.
    public class WheelBinding
    {
        private const string EnabledEntrySuffix     = "_WheelEnabled";
        private const string ModifierEntrySuffix    = "_WheelMod";
        private const string SensitivityEntrySuffix = "_WheelSensitivity";
        private const string MouseScrollAxisName    = "Mouse ScrollWheel";

        public enum Modifier
        {
            None,
            Ctrl,
            Shift,
            Alt,
        }

        public ConfigEntry<bool>     Enabled     { get; }
        public ConfigEntry<Modifier> Mod         { get; }
        public ConfigEntry<float>    Sensitivity { get; }

        public WheelBinding(
            ConfigFile config,
            string section,
            string name,
            Modifier defaultMod,
            float defaultSensitivity,
            string description = null)
        {
            string desc = string.IsNullOrEmpty(description) ? name : description;
            Enabled     = config.Bind(section, name + EnabledEntrySuffix,     true,                "Enable wheel binding for " + desc + ".");
            Mod         = config.Bind(section, name + ModifierEntrySuffix,    defaultMod,          "Modifier required for " + desc + " wheel action.");
            Sensitivity = config.Bind(section, name + SensitivityEntrySuffix, defaultSensitivity,  "Per-notch delta applied for " + desc + ".");
        }

        public float GetDelta()
        {
            if (!Enabled.Value) return 0f;
            float scroll = UnityEngine.Input.GetAxis(MouseScrollAxisName);
            if (Mathf.Approximately(scroll, 0f)) return 0f;
            if (!ModifierMatches()) return 0f;
            return scroll * Sensitivity.Value;
        }

        private bool ModifierMatches()
        {
            bool ctrl  = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
            bool shift = UnityEngine.Input.GetKey(KeyCode.LeftShift)   || UnityEngine.Input.GetKey(KeyCode.RightShift);
            bool alt   = UnityEngine.Input.GetKey(KeyCode.LeftAlt)     || UnityEngine.Input.GetKey(KeyCode.RightAlt);

            switch (Mod.Value)
            {
                case Modifier.Ctrl:  return ctrl  && !shift && !alt;
                case Modifier.Shift: return shift && !ctrl  && !alt;
                case Modifier.Alt:   return alt   && !ctrl  && !shift;
                case Modifier.None:
                default:             return !ctrl && !shift && !alt;
            }
        }
    }
}
