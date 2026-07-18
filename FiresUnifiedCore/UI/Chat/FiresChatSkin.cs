using UnityEngine;
using FiresCore.UI.Minigames;

namespace FiresCore.UI.FiresChat
{
    /// <summary>
    /// Brown-and-gold chat skin — 9-slice sprites from generic RPG UI kits, embedded in FUC and shared by any mod that
    /// wants to reskin the vanilla chat window into the Fires look. Sprites load lazily from this assembly the first
    /// time a face is requested (client-only; never touched on a headless server).
    /// </summary>
    public static class FiresChatSkin
    {
        private static Sprite _panel, _field, _tab, _handle, _frame;
        private static bool _loaded;

        // Palette — matches the approved render.
        public static readonly Color Gold  = new Color(1f, 0.85f, 0.5f);
        public static readonly Color Cream = new Color(0.92f, 0.87f, 0.77f);
        public static readonly Color Dim   = new Color(0.62f, 0.55f, 0.42f);
        public static readonly Color TabOn = new Color(1f, 0.95f, 0.82f);
        public static readonly Color Ink   = new Color(0.2f, 0.13f, 0.06f);

        /// <summary>Dark maroon-brown panel with an ornate gold frame (chat window background). 512×256.</summary>
        public static Sprite Panel  { get { Ensure(); return _panel; } }
        /// <summary>Dark rounded input-field background. 140×38.</summary>
        public static Sprite Field  { get { Ensure(); return _field; } }
        /// <summary>Dark button with a warm gold rim (channel tabs + minimize). 140×38.</summary>
        public static Sprite Tab    { get { Ensure(); return _tab; } }
        /// <summary>Small dark-metal grip with a gold rivet (scrollbar handle / resize nub). 58×80.</summary>
        public static Sprite Handle { get { Ensure(); return _handle; } }
        /// <summary>Hollow ornate gold frame — overlaid on the active tab. 256×256.</summary>
        public static Sprite Frame  { get { Ensure(); return _frame; } }

        private static void Ensure()
        {
            if (_loaded) return;
            _loaded = true;
            var asm = typeof(FiresChatSkin).Assembly;
            _panel  = FiresMinigameSprites.Load(asm, "FiresChatPanel.png",  new Vector4(30f, 30f, 30f, 30f));
            _field  = FiresMinigameSprites.Load(asm, "FiresChatField.png",  new Vector4(12f, 12f, 12f, 12f));
            _tab    = FiresMinigameSprites.Load(asm, "FiresChatTab.png",    new Vector4(12f, 12f, 12f, 12f));
            _handle = FiresMinigameSprites.Load(asm, "FiresChatHandle.png", new Vector4(10f, 18f, 10f, 18f));
            _frame  = FiresMinigameSprites.Load(asm, "FiresChatFrame.png",  new Vector4(26f, 26f, 26f, 26f));
        }
    }
}
