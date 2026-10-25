using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    /// <summary>
    /// Core exists so methods are never duplicated across mods (Tools\UI_HANDLING_AUDIT.md): the ONE slot look of the Fires
    /// family. Every inventory slot face (main grid, equipment, pouches, Misc, bags, companion cells, crafting slots) is painted through
    /// <see cref="ApplySlotLook"/>; no mod writes a slot face's sprite, colour or border itself.
    /// The look is the APPROVED pouch-cell look, byte for byte as VAInventory builds it (UnifiedScreen.PouchFace / ApplyLook, values
    /// confirmed): vanilla's element sprite ('item_background', Sliced) coloured with vanilla's grey tint
    /// (the element prefab's Selectable normalColor, grey 0.132 @ alpha 0.502, which over the dark plate renders the approved RGB 34,28,13; recipe from PouchFace) and ONE gold Outline (0.55, 0.42, 0.20, 1) at
    /// (1.5, -1.5). On faces that carry a Selectable the tint is neutralised (white) so the grey is not multiplied twice; the face is
    /// marked KitLeaf so CraftingSkin never repaints it, and Core's Role.Slot outline child is hidden (one border). Add-only.
    /// </summary>
    public static class FiresSkin
    {
        private static readonly Color FallbackFace = new Color(34f / 255f, 28f / 255f, 10f / 255f, 1f);   // measured from TARGET_slot_style.png: near-black cell RGB 34,28,10, opaque

        /// <summary>The approved slot border colour and offset.</summary>
        public static readonly Color SlotBorderColor = new Color(0.55f, 0.42f, 0.20f, 1f);
        public static readonly Vector2 SlotBorderDistance = new Vector2(1.5f, -1.5f);

        /// <summary>Reserved for state looks (selected, disabled...); every state draws the Normal look today. Add-only.</summary>
        public enum SlotState { Normal }

        private static Sprite s_elementSprite;
        private static Image.Type s_elementType = Image.Type.Sliced;
        private static Color? s_face;
        private static bool s_logged;
        private static readonly System.Collections.Generic.HashSet<Image> s_done = new System.Collections.Generic.HashSet<Image>();

        private static void Resolve()
        {
            if (s_elementSprite != null && s_face.HasValue) return;
            var prefab = InventoryGui.instance != null && InventoryGui.instance.m_playerGrid != null ? InventoryGui.instance.m_playerGrid.m_elementPrefab : null;
            if (prefab == null) return;
            var img = prefab.GetComponent<Image>();
            if (img != null) { s_elementSprite = img.sprite; s_elementType = img.type; }
            var sel = prefab.GetComponent<Selectable>();
            s_face = FallbackFace;   // a fixed measured colour (reading the prefab's tint returned white once faces were neutralised: the tan squares)
        }

        /// <summary>Vanilla's inventory element sprite ('item_background'; null before the InventoryGui exists).</summary>
        public static Sprite SlotSprite { get { Resolve(); return s_elementSprite; } }

        /// <summary>The approved slot face colour: the element prefab's Selectable normalColor (grey 0.132 @ 0.502; renders RGB 34,28,13 over the dark plate).</summary>
        public static Color SlotFaceColor { get { Resolve(); return s_face ?? FallbackFace; } }

        /// <summary>Paints <paramref name="face"/> with the approved slot look. Idempotent (writes only what differs); false when the
        /// vanilla sprite isn't available yet (call again later). Only the face: icons, counts and durability bars are untouched.
        /// Pass only faces that should be painted (an element inside a Cell_ that stays transparent is the owner's).</summary>
        /// <summary>The one-argument form other mods find by reflection (GetMethod("ApplySlotLook", new[] { typeof(Image) })).</summary>
        public static bool ApplySlotLook(Image face) => ApplySlotLook(face, SlotState.Normal);

        public static bool ApplySlotLook(Image face, SlotState state)
        {
            if (face == null) return false;
            Resolve();
            if (s_elementSprite == null) return false;
            // InventoryGui.Update cost 95 ms/s because owners re-apply every frame: a face already painted and unchanged returns at
            // once (no GetComponent / Find on the hot path)
            if (s_done.Contains(face) && face.sprite == s_elementSprite && face.color == (s_face ?? FallbackFace)) return true;
            if (face.sprite != s_elementSprite) face.sprite = s_elementSprite;
            if (face.type != s_elementType) face.type = s_elementType;
            var faceColor = s_face ?? FallbackFace;
            if (face.color != faceColor) face.color = faceColor;
            face.canvasRenderer.SetColor(Color.white);
            var sel = face.GetComponent<Selectable>();
            if (sel != null && sel.transition == Selectable.Transition.ColorTint && sel.colors.normalColor != Color.white)
            {
                var cb = sel.colors; cb.normalColor = Color.white; sel.colors = cb;
            }
            var outline = face.GetComponent<Outline>() ?? face.gameObject.AddComponent<Outline>();
            if (outline.effectColor != SlotBorderColor) outline.effectColor = SlotBorderColor;
            if (outline.effectDistance != SlotBorderDistance) outline.effectDistance = SlotBorderDistance;
            if (face.GetComponent<Kit.KitLeaf>() == null) face.gameObject.AddComponent<Kit.KitLeaf>();
            var coreOutline = face.transform.Find("KitSlotOutline");
            if (coreOutline != null && coreOutline.gameObject.activeSelf) coreOutline.gameObject.SetActive(false);
            s_done.Add(face);
            if (s_done.Count > 20000) s_done.RemoveWhere(x => x == null);
            if (!s_logged)
            {
                s_logged = true;
                FiresCore.Logging.FiresLogger.LogDebug($"[FiresSkin] slot look: sprite '{s_elementSprite.name}' {s_elementType}, face RGBA({faceColor.r:0.000}, {faceColor.g:0.000}, {faceColor.b:0.000}, {faceColor.a:0.000}), outline RGBA({SlotBorderColor.r:0.00}, {SlotBorderColor.g:0.00}, {SlotBorderColor.b:0.00}, {SlotBorderColor.a:0.00}) at ({SlotBorderDistance.x}, {SlotBorderDistance.y}), tint neutralised on Selectables, KitLeaf");
            }
            return true;
        }
    }
}
