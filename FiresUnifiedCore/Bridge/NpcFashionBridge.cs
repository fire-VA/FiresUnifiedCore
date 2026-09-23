using System;
using System.Collections.Generic;
using FiresCore.Npc;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Seam for applying NPC hair/beard appearance through the owning mod's fashion system (the
    /// NpcFashion module ships in the host mod / FiresCompanions standalone — not in Core). The owner
    /// registers the delegates; while none is present these are no-ops (and ColorToString returns "").
    ///
    /// <para>This is also where a head item hides hair. Vanilla only does that for players
    /// (<see cref="HelmetHairRules"/>), and every hair path — companions, static NPCs, mannequins, the
    /// dressing room — funnels through here, so resolving the rule at the seam covers all of them and
    /// none of them can forget it. The style the caller ASKED for is remembered per NPC so
    /// <see cref="ReapplyForHelmet"/> can put it back when the helmet comes off.</para>
    /// </summary>
    public static class NpcFashionBridge
    {
        public static Func<Color, string> ColorToStringFn;
        public static Action<GameObject, string, string> ApplyHairFn;   // (npc, style, colorStr)
        public static Action<GameObject, string, string> ApplyBeardFn;  // (npc, style, colorStr)

        private struct Chosen
        {
            public string Hair;
            public string Beard;
            public string Color;
        }

        // Keyed by instance id so a destroyed NPC's entry cannot resurrect a body; swept on miss.
        private static readonly Dictionary<int, Chosen> _chosen = new Dictionary<int, Chosen>();

        public static string ColorToString(Color color)
        {
            try { return ColorToStringFn != null ? ColorToStringFn(color) : ""; } catch { return ""; }
        }

        public static void ApplyHair(GameObject npc, string style, string colorStr)
        {
            if (npc == null) return;
            Remember(npc, hair: style, beard: null, colorStr: colorStr, hasHair: true, hasBeard: false);
            try { ApplyHairFn?.Invoke(npc, HelmetHairRules.ResolveHair(HelmetName(npc), style), colorStr); } catch { }
        }

        public static void ApplyBeard(GameObject npc, string style, string colorStr)
        {
            if (npc == null) return;
            Remember(npc, hair: null, beard: style, colorStr: colorStr, hasHair: false, hasBeard: true);
            try { ApplyBeardFn?.Invoke(npc, HelmetHairRules.ResolveBeard(HelmetName(npc), style), colorStr); } catch { }
        }

        /// <summary>
        /// Re-resolves the styles this NPC was last asked to wear against its current head item. Called by
        /// NpcVisEquipment whenever the helmet slot changes, so taking a helmet off brings the hair back and
        /// putting one on hides it — what vanilla does every frame for a player.
        /// </summary>
        public static void ReapplyForHelmet(GameObject npc)
        {
            if (npc == null) return;
            if (!_chosen.TryGetValue(npc.GetInstanceID(), out var chosen)) return;

            string helmet = HelmetName(npc);
            if (chosen.Hair != null)
            {
                try { ApplyHairFn?.Invoke(npc, HelmetHairRules.ResolveHair(helmet, chosen.Hair), chosen.Color); } catch { }
            }
            if (chosen.Beard != null)
            {
                try { ApplyBeardFn?.Invoke(npc, HelmetHairRules.ResolveBeard(helmet, chosen.Beard), chosen.Color); } catch { }
            }
        }

        public static void Forget(GameObject npc)
        {
            if (npc != null) _chosen.Remove(npc.GetInstanceID());
        }

        private static string HelmetName(GameObject npc) =>
            npc.GetComponent<NpcVisEquipment>()?.CurrentHelmetName ?? "";

        private static void Remember(GameObject npc, string hair, string beard, string colorStr, bool hasHair, bool hasBeard)
        {
            int id = npc.GetInstanceID();
            _chosen.TryGetValue(id, out var chosen);
            if (hasHair) chosen.Hair = hair;
            if (hasBeard) chosen.Beard = beard;
            chosen.Color = colorStr;
            _chosen[id] = chosen;
        }
    }
}
