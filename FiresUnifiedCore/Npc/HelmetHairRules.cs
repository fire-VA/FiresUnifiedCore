using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Whether a head item hides an NPC's hair or beard, and which variant it swaps in — vanilla's rule, mirrored
    /// for bodies vanilla refuses to apply it to.
    ///
    /// <para>VisEquipment.UpdateEquipmentVisuals (1.0 :589) runs its hair and beard block inside
    /// <c>if (m_isPlayer)</c>, and every companion and NPC runs with m_isPlayer=false. Vanilla's own helpers
    /// (<c>HelmetHides</c> :946, <c>GetHairItem</c> :603) are private, so this mirrors them instead of calling
    /// them: helmet hash to (hideHair, hideBeard) off the helmet's ItemDrop, then Hidden to nothing, Default to
    /// the chosen style, and anything else to the variant the HAIR item declares for that setting — the settings
    /// list lives on the hair, not the helmet.</para>
    ///
    /// <para>Shared because both hair pipelines need it: companions through CompanionRandomLoadout and static
    /// NPCs through StaticNpcInitializer, both funnelling into NpcFashionBridge. The rule lives here so the two
    /// can never drift; the attach itself stays with NpcFashionManager.</para>
    /// </summary>
    public static class HelmetHairRules
    {
        /// <summary>The style an NPC wearing <paramref name="helmetName"/> should actually show. Empty = bald.</summary>
        public static string ResolveHair(string helmetName, string hairStyle) =>
            Resolve(helmetName, hairStyle, beard: false);

        /// <summary>The beard an NPC wearing <paramref name="helmetName"/> should actually show. Empty = clean-shaven.</summary>
        public static string ResolveBeard(string helmetName, string beardStyle) =>
            Resolve(helmetName, beardStyle, beard: true);

        private static string Resolve(string helmetName, string style, bool beard)
        {
            if (string.IsNullOrEmpty(style)) return style;
            if (!TryGetHideType(helmetName, beard, out var hideType)) return style;

            if (hideType == ItemDrop.ItemData.HelmetHairType.Hidden) return "";
            if (hideType == ItemDrop.ItemData.HelmetHairType.Default) return style;

            // A partial cover (hat / hood / neck / scarf) swaps in a trimmed variant the hair item declares.
            // No declared variant means vanilla shows nothing, so neither do we.
            return VariantFor(style, hideType, beard) ?? "";
        }

        private static bool TryGetHideType(string helmetName, bool beard, out ItemDrop.ItemData.HelmetHairType type)
        {
            type = ItemDrop.ItemData.HelmetHairType.Default;
            if (string.IsNullOrEmpty(helmetName) || ObjectDB.instance == null) return false;

            var prefab = ObjectDB.instance.GetItemPrefab(helmetName);
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null || drop.m_itemData?.m_shared == null) return false;

            type = beard ? drop.m_itemData.m_shared.m_helmetHideBeard : drop.m_itemData.m_shared.m_helmetHideHair;
            return true;
        }

        private static string VariantFor(string style, ItemDrop.ItemData.HelmetHairType type, bool beard)
        {
            var prefab = ObjectDB.instance.GetItemPrefab(style);
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null || drop.m_itemData?.m_shared == null) return null;

            List<ItemDrop.ItemData.HelmetHairSettings> settings = beard
                ? drop.m_itemData.m_shared.m_helmetBeardSettings
                : drop.m_itemData.m_shared.m_helmetHairSettings;
            if (settings == null) return null;

            var match = settings.FirstOrDefault(s => s != null && s.m_setting == type);
            return match?.m_hairPrefab != null ? match.m_hairPrefab.name : null;
        }
    }
}
