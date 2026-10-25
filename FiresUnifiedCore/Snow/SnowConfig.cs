using BepInEx.Configuration;

namespace FiresCore.Snow
{
    /// <summary>[Snow] config, bound lazily from Core's own config file (no edit to Core's Awake). New behaviour ships OFF.</summary>
    internal static class SnowConfig
    {
        private static ConfigEntry<bool> s_on;
        private static bool s_tried;

        internal static bool On
        {
            get
            {
                if (!s_tried) Bind();
                return s_on != null && s_on.Value;
            }
        }

        internal static void Bind()
        {
            if (s_tried) return;
            s_tried = true;
            var cfg = FiresUnifiedCore.Instance?.Config;
            if (cfg == null) { s_tried = false; return; }
            s_on = cfg.Bind("Snow", "Dynamic snow", false,
                "Snow builds up and melts with the seasons (Vedr) and the weather in the Mountains, the Deep North and Winter territories. " +
                "Fires and dug paths clear it; paths fill back in, faster in a blizzard. Every player computes the same snow, so it costs no network traffic.");
        }
    }
}
