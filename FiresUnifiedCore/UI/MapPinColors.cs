using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>0.2.1158 (Fire: "the icon color can live in core", "that way companions and groups can use icon coloring"): the one way to colour a map pin's icon and name. Inert until a mod calls it; the keys live in the mod that uses it.
    /// Written from vanilla Minimap.UpdatePins, which sets each pin's colour on every pin update, so the colour is put back after that. Keeps vanilla's alpha, so a checked or faded pin still fades.</summary>
    public static class MapPinColors
    {
        private static readonly Dictionary<Minimap.PinData, Color> s_colours = new Dictionary<Minimap.PinData, Color>();

        /// <summary>Colour this pin until cleared or the pin is removed.</summary>
        public static void Set(Minimap.PinData pin, Color colour) { if (pin == null) return; s_colours[pin] = colour; Paint(pin, colour); }

        /// <summary>Back to vanilla's colour on the next pin update.</summary>
        public static void Clear(Minimap.PinData pin) { if (pin != null && s_colours.Remove(pin) && Minimap.instance != null) Minimap.instance.m_pinUpdateRequired = true; }

        /// <summary>A stable, readable colour for any key (player id, group name): the same key gives the same colour on every client.</summary>
        public static Color ColorFor(string key)
        {
            int hash = string.IsNullOrEmpty(key) ? 0 : key.GetStableHashCode();
            return Color.HSVToRGB(Mathf.Abs(hash % 360) / 360f, 0.65f, 1f);
        }

        private static void Paint(Minimap.PinData pin, Color c)
        {
            if (pin.m_iconElement != null) { c.a = pin.m_iconElement.color.a; pin.m_iconElement.color = c; }
            var text = pin.m_NamePinData != null ? pin.m_NamePinData.PinNameText : null;
            if (text != null) text.color = c;
        }

        [HarmonyPatch(typeof(Minimap), "UpdatePins")]
        private static class Minimap_UpdatePins_Colours
        {
            private static void Postfix() { if (s_colours.Count == 0) return; foreach (var kv in s_colours) Paint(kv.Key, kv.Value); }
        }

        [HarmonyPatch(typeof(Minimap), nameof(Minimap.RemovePin), new[] { typeof(Minimap.PinData) })]
        private static class Minimap_RemovePin_Colours { private static void Postfix(Minimap.PinData pin) { if (pin != null) s_colours.Remove(pin); } }
    }
}