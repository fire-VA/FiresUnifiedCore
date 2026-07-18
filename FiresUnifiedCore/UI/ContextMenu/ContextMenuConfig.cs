using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.UI.ContextMenu
{
    using Input = UnityEngine.Input; // FiresCore.Input (a namespace) otherwise shadows UnityEngine.Input here

    /// <summary>
    /// Config for the hold-modifier + right-click context menu (<see cref="FiresContextMenuDriver"/>) - the
    /// menu that offers companion commands (Move here / Attack / Deposit / ...) and patrol-node editing. Lets
    /// the player turn the whole feature off and rebind the modifier keys that arm it. Client-local (a
    /// per-player keybind), so it is NOT server-synced.
    /// </summary>
    public static class ContextMenuConfig
    {
        public static ConfigEntry<bool> Enable;
        public static ConfigEntry<KeyCode> Modifier1;
        public static ConfigEntry<KeyCode> Modifier2;

        public static void Initialize(ConfigFile config)
        {
            if (Enable != null) return; // idempotent

            Enable = config.Bind(
                "ContextMenu", "Enabled", true,
                "Enable the hold-modifier + right-click companion/context menu (companion commands, patrol-node " +
                "editing, etc.). Turn off to disable it entirely.");

            Modifier1 = config.Bind(
                "ContextMenu", "ModifierKey1", KeyCode.LeftAlt,
                "First modifier key to HOLD to arm the context menu, then right-click a target. Left/Right " +
                "variants count the same (LeftAlt == RightAlt). Default LeftAlt.");

            Modifier2 = config.Bind(
                "ContextMenu", "ModifierKey2", KeyCode.LeftShift,
                "Second modifier key to HOLD together with the first. Set to None to require only ModifierKey1. " +
                "Default LeftShift (so the default arm combo is Alt+Shift, then right-click).");
        }

        /// <summary>Cheap hot-path read. True until bound (feature on by default) unless explicitly disabled.</summary>
        public static bool Enabled => Enable == null || Enable.Value;

        /// <summary>True while the configured modifier combo is held (the arm gate for a context-menu right-click).</summary>
        public static bool ModifiersHeld() =>
            Held(Modifier1 != null ? Modifier1.Value : KeyCode.LeftAlt) &&
            Held(Modifier2 != null ? Modifier2.Value : KeyCode.LeftShift);

        private static bool Held(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.None:         return true; // this modifier not required
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:     return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                case KeyCode.LeftShift:
                case KeyCode.RightShift:   return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                case KeyCode.LeftControl:
                case KeyCode.RightControl: return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                default:                   return Input.GetKey(k);
            }
        }
    }
}
