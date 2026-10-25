using UnityEngine;

namespace FiresCore.Input
{
    // Rule: every radius/area tool in every mod uses mouse wheel = radius and Shift+wheel = height (one value).
    // No Ctrl+wheel, no top/bottom pair. This is the one shared reader; mods without a hard Core dependency mirror these bindings.
    // Add-only API.
    public static class RadiusWheel
    {
        private const string MouseScrollAxisName = "Mouse ScrollWheel";
        private const float DeadZone = 0.01f;

        // Signed wheel notch this frame (wheel up = +1, down = -1). Plain wheel → dRadius, Shift+wheel → dHeight.
        // Ctrl or Alt held = nothing (both zero). Returns true when either delta is non-zero.
        public static bool Read(out float dRadius, out float dHeight)
        {
            dRadius = 0f; dHeight = 0f;
            float scroll = UnityEngine.Input.GetAxis(MouseScrollAxisName);
            if (Mathf.Abs(scroll) < DeadZone) return false;
            bool ctrl  = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
            bool alt   = UnityEngine.Input.GetKey(KeyCode.LeftAlt)     || UnityEngine.Input.GetKey(KeyCode.RightAlt);
            if (ctrl || alt) return false;
            bool shift = UnityEngine.Input.GetKey(KeyCode.LeftShift)   || UnityEngine.Input.GetKey(KeyCode.RightShift);
            float notch = scroll > 0f ? 1f : -1f;
            if (shift) dHeight = notch; else dRadius = notch;
            return true;
        }
    }
}
