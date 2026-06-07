using System;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Seam for applying NPC hair/beard appearance through the owning mod's fashion system (the
    /// NpcFashion module ships in the host mod / FiresCompanions standalone — not in Core). The owner
    /// registers the delegates; while none is present these are no-ops (and ColorToString returns "").
    /// </summary>
    public static class NpcFashionBridge
    {
        public static Func<Color, string> ColorToStringFn;
        public static Action<GameObject, string, string> ApplyHairFn;   // (npc, style, colorStr)
        public static Action<GameObject, string, string> ApplyBeardFn;  // (npc, style, colorStr)

        public static string ColorToString(Color color)
        {
            try { return ColorToStringFn != null ? ColorToStringFn(color) : ""; } catch { return ""; }
        }

        public static void ApplyHair(GameObject npc, string style, string colorStr)
        {
            try { ApplyHairFn?.Invoke(npc, style, colorStr); } catch { }
        }

        public static void ApplyBeard(GameObject npc, string style, string colorStr)
        {
            try { ApplyBeardFn?.Invoke(npc, style, colorStr); } catch { }
        }
    }
}
