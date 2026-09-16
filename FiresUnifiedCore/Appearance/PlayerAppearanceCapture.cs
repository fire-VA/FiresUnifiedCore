using System;
using System.Reflection;
using FiresCore.Storage;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Appearance
{
    /// <summary>
    /// Snapshots the local player's resolved VisEquipment into a <see cref="PlayerAppearance"/>. Only the owning client
    /// can read its own resolved equipment, so a headless server simply gets null. Private fields are read through a
    /// cached AccessTools lookup that degrades to empty values on a rename; item fields become their string name or hash
    /// token, and skin and hair colors become RRGGBBAA strings.
    /// </summary>
    public static class PlayerAppearanceCapture
    {
        private static FieldInfo _fModelIndex;
        private static FieldInfo _fSkinColor;
        private static FieldInfo _fHairColor;
        private static FieldInfo _fHairItem;
        private static FieldInfo _fBeardItem;
        private static FieldInfo _fRightItem;
        private static FieldInfo _fLeftItem;
        private static FieldInfo _fLeftItemVariant;
        private static FieldInfo _fChestItem;
        private static FieldInfo _fLegItem;
        private static FieldInfo _fHelmetItem;
        private static FieldInfo _fShoulderItem;
        private static FieldInfo _fShoulderItemVariant;
        private static FieldInfo _fUtilityItem;
        private static FieldInfo _fLeftBackItem;
        private static FieldInfo _fLeftBackItemVariant;
        private static FieldInfo _fRightBackItem;
        private static FieldInfo _fTrinketItem;
        private static bool _fieldsCached;

        private static void EnsureFieldsCached()
        {
            if (_fieldsCached) return;
            var type = typeof(VisEquipment);

            _fModelIndex = AccessTools.Field(type, "m_modelIndex");
            _fSkinColor = AccessTools.Field(type, "m_skinColor");
            _fHairColor = AccessTools.Field(type, "m_hairColor");
            _fHairItem = AccessTools.Field(type, "m_hairItem");
            _fBeardItem = AccessTools.Field(type, "m_beardItem");
            _fRightItem = AccessTools.Field(type, "m_rightItem");
            _fLeftItem = AccessTools.Field(type, "m_leftItem");
            _fLeftItemVariant = AccessTools.Field(type, "m_leftItemVariant");
            _fChestItem = AccessTools.Field(type, "m_chestItem");
            _fLegItem = AccessTools.Field(type, "m_legItem");
            _fHelmetItem = AccessTools.Field(type, "m_helmetItem");
            _fShoulderItem = AccessTools.Field(type, "m_shoulderItem");
            _fShoulderItemVariant = AccessTools.Field(type, "m_shoulderItemVariant");
            _fUtilityItem = AccessTools.Field(type, "m_utilityItem");
            _fLeftBackItem = AccessTools.Field(type, "m_leftBackItem");
            _fLeftBackItemVariant = AccessTools.Field(type, "m_leftBackItemVariant");
            _fRightBackItem = AccessTools.Field(type, "m_rightBackItem");
            _fTrinketItem = AccessTools.Field(type, "m_trinketItem");

            _fieldsCached = true;
        }

        /// <summary>
        /// Snapshot the given player's VisEquipment. Returns null on anything but the ready local player —
        /// callers treat null as "nothing to send".
        /// </summary>
        public static PlayerAppearance Capture(Player player)
        {
            if (player == null || player != Player.m_localPlayer || ZNetScene.instance == null) return null;

            var vis = player.GetComponent<VisEquipment>();
            if (vis == null) return null;

            try
            {
                EnsureFieldsCached();

                var rec = new PlayerAppearance
                {
                    Owner = (player.GetPlayerName() ?? string.Empty).Trim(),
                    PlayerName = player.GetPlayerName() ?? string.Empty,
                    UpdatedAtUtcTicks = DateTime.UtcNow.Ticks,

                    ModelIndex = ReadInt(_fModelIndex, vis),
                    SkinColorRgba = ReadColorRgba(_fSkinColor, vis),
                    HairColorRgba = ReadColorRgba(_fHairColor, vis),

                    HairItem = ReadItem(_fHairItem, vis),
                    BeardItem = ReadItem(_fBeardItem, vis),

                    RightItem = ReadItem(_fRightItem, vis),
                    LeftItem = ReadItem(_fLeftItem, vis),
                    LeftItemVariant = ReadInt(_fLeftItemVariant, vis),
                    ChestItem = ReadItem(_fChestItem, vis),
                    LegItem = ReadItem(_fLegItem, vis),
                    HelmetItem = ReadItem(_fHelmetItem, vis),
                    ShoulderItem = ReadItem(_fShoulderItem, vis),
                    ShoulderItemVariant = ReadInt(_fShoulderItemVariant, vis),
                    UtilityItem = ReadItem(_fUtilityItem, vis),
                    LeftBackItem = ReadItem(_fLeftBackItem, vis),
                    LeftBackItemVariant = ReadInt(_fLeftBackItemVariant, vis),
                    RightBackItem = ReadItem(_fRightBackItem, vis),
                    TrinketItem = ReadItem(_fTrinketItem, vis),
                };
                return rec;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayerAppearanceCapture] Capture failed: {ex.Message}");
                return null;
            }
        }

        private static int ReadInt(FieldInfo fieldInfo, VisEquipment vis)
        {
            if (fieldInfo == null) return 0;
            try
            {
                object raw = fieldInfo.GetValue(vis);
                return raw is int i ? i : 0;
            }
            catch { return 0; }
        }

        // Vanilla stores skin/hair color as Vector3 (xyz, implicit alpha 1). On a build where the field is
        // already a Color we read it straight. Either way emit RRGGBBAA via ColorUtility.
        private static string ReadColorRgba(FieldInfo fieldInfo, VisEquipment vis)
        {
            if (fieldInfo == null) return "";
            try
            {
                object raw = fieldInfo.GetValue(vis);
                Color color;
                if (raw is Vector3 vec) color = new Color(vec.x, vec.y, vec.z, 1f);
                else if (raw is Color colorValue) color = colorValue;
                else return "";
                return ColorUtility.ToHtmlStringRGBA(color);
            }
            catch { return ""; }
        }

        // Item fields are type-shifted string↔int across builds. string -> use; int/long hash -> token
        // ("0" hash means empty slot). The dress side re-resolves either form.
        private static string ReadItem(FieldInfo fieldInfo, VisEquipment vis)
        {
            if (fieldInfo == null) return "";
            try
            {
                object raw = fieldInfo.GetValue(vis);
                if (raw == null) return "";
                if (raw is string text) return text ?? "";
                if (raw is int i) return i == 0 ? "" : i.ToString();
                if (raw is long number) return number == 0L ? "" : number.ToString();
                return "";
            }
            catch { return ""; }
        }
    }
}
