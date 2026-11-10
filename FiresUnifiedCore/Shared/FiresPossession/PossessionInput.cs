using UnityEngine;

namespace FiresPossession
{
    /// <summary>
    /// The keys the engine drives a creature with, through the host's own input seam (Valcast: ValcastKeys, so a test can hold
    /// them; Core: its keys with the chat / console guard). Button names are vanilla's ZInput names (Forward, Backward, Left, Right,
    /// Run, Jump, Crouch, Attack, SecondaryAttack and the Joy* ones); axes are Unity's ("Mouse X", "Mouse ScrollWheel").
    /// </summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    interface IPossessionInput
    {
        bool GetButton(string name);
        float GetAxis(string name);
        bool GetKey(KeyCode key);
        /// <summary>Flight controls' flat turn (the host's Q / E bindings).</summary>
        bool YawLeft();
        bool YawRight();
        /// <summary>False while a menu, the console, chat, a text field or the inventory has the keys: the creature stands still.</summary>
        bool InputOpen();
    }

    /// <summary>The game's own keys (ZInput buttons, Unity axes, Q / E for the flat turn), closed while a menu or chat is up.</summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    sealed class VanillaPossessionInput : IPossessionInput
    {
        public static readonly VanillaPossessionInput Instance = new VanillaPossessionInput();

        public bool GetButton(string name) => name != null && ZInput.GetButton(name);
        public float GetAxis(string name) => name != null ? Input.GetAxis(name) : 0f;
        public bool GetKey(KeyCode key) => Input.GetKey(key);
        public bool YawLeft() => Input.GetKey(KeyCode.Q);
        public bool YawRight() => Input.GetKey(KeyCode.E);

        public bool InputOpen() =>
            !(Menu.IsVisible() || Console.IsVisible() || TextInput.IsVisible() || InventoryGui.IsVisible() ||
              StoreGui.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus()) || Hud.InRadial());
    }
}
