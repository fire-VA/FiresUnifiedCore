// Fires UI kit: the talent node block. All sizes are UI units at 1080, scaled with the canvas like
// every other kit size. One node is a fixed vertical block of height Pitch; rows stack by Pitch and never overlap.
//   PadTop | Ring | RingToPips | Pips (PipSize) | PipsToName | Name (NameH) | Sub (SubH) | PadBottom  = Pitch
// Depends only on UnityEngine. ASCII-only; Core UI/Shell/Kit and ValheimRip1.0/Assets/FiresUIKit/Runtime hold
// byte-identical copies (check with cmp).
namespace FiresCore.UI.Shell.Kit
{
    public static class NodeLayout
    {
        public const float Ring = 52f;        // the node's ring / diamond, on-screen extent
        public const float Halo = Ring + 6f;  // soft disc behind the ring: 3 px past it each side, so the pips (RingToPips 6) sit 3 px clear
        public const float PipSize = 6f;      // rank pip diameter
        public const float PipGap = 5f;       // space between pips
        public const float RingToPips = 6f;
        public const float PipsToName = 6f;
        public const float NameH = 18f;
        public const float SubH = 14f;
        public const float PadTop = 4f;
        public const float PadBottom = 12f;
        public const float Pitch = 118f;      // = PadTop + Ring + RingToPips + PipSize + PipsToName + NameH + SubH + PadBottom
    }
}
