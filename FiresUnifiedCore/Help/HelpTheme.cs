using UnityEngine;

namespace FiresCore.Help
{
    // Shared color palette for the help panel and its content. Lifted verbatim from the original
    // VerdantAscentHelpPanel so the look is unchanged.
    internal static class HelpTheme
    {
        public static readonly Color PanelBg = new Color(0.06f, 0.05f, 0.04f, 0.97f);
        public static readonly Color SidebarBg = new Color(0.08f, 0.07f, 0.05f, 0.95f);
        public static readonly Color ContentBg = new Color(0.07f, 0.06f, 0.04f, 0.95f);
        public static readonly Color NavNormal = new Color(0.10f, 0.08f, 0.06f, 0.9f);
        public static readonly Color NavActive = new Color(0.20f, 0.16f, 0.08f, 0.95f);
        public static readonly Color NavHover = new Color(0.16f, 0.12f, 0.07f, 0.9f);
        public static readonly Color TextGold = new Color(1f, 0.85f, 0.5f, 1f);
        public static readonly Color TextLight = new Color(0.85f, 0.75f, 0.55f, 1f);
        public static readonly Color TextMuted = new Color(0.6f, 0.5f, 0.35f, 0.8f);
        public static readonly Color HeaderColor = new Color(0.95f, 0.8f, 0.45f, 1f);
        public static readonly Color CodeBg = new Color(0.06f, 0.06f, 0.08f, 0.9f);
        public static readonly Color DividerColor = new Color(0.4f, 0.3f, 0.15f, 0.4f);
        public static readonly Color AdminBg = new Color(0.12f, 0.06f, 0.06f, 0.6f);
        public static readonly Color AdminHeaderColor = new Color(1f, 0.6f, 0.4f, 1f);
        public static readonly Color AdminDivider = new Color(1f, 0.5f, 0.3f, 0.5f);
        public static readonly Color ScrollbarTrack = new Color(0.06f, 0.06f, 0.08f, 0.6f);
        public static readonly Color ScrollbarHandle = new Color(0.4f, 0.3f, 0.15f, 0.7f);
        public static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.6f);
    }
}
