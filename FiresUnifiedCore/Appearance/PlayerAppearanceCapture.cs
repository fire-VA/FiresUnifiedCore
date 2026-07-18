using System;
using System.Reflection;
using FiresCore.Storage;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Appearance
{
    /// <summary>
    /// Owner-side reader that snapshots the LOCAL player's resolved <c>VisEquipment</c> state into a
    /// <see cref="PlayerAppearance"/>. Only the owning client can cleanly read its own resolved equipment,
    /// so capture is client-only and the dedi guard is automatic: <c>Player.m_localPlayer == null</c> on a
    /// headless server ⇒ <see cref="Capture"/> returns null and nothing is sent.
    ///
    /// Private <c>VisEquipment</c> fields are read through a <see cref="FieldInfo"/> cache resolved once per
    /// AppDomain (the same pattern as <c>NpcVisEquipment.EnsureFieldCachesPopulated</c>), using
    /// <see cref="AccessTools.Field"/> with a soft fallback so a vanilla rename degrades to an empty value
    /// rather than throwing. Item fields are read as <c>object</c>: a string is used verbatim; an int/long
    /// hash is stored as its <c>ToString()</c> token (0 ⇒ empty). Colors are read as Vector3 (vanilla stores
    /// <c>m_skinColor</c>/<c>m_hairColor</c> as Vector3) and emitted as <c>RRGGBBAA</c> via
    /// <see cref="ColorUtility.ToHtmlStringRGBA"/>.
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
            var t = typeof(VisEquipment);

            _fModelIndex = AccessTools.Field(t, "m_modelIndex");
            _fSkinColor = AccessTools.Field(t, "m_skinColor");
            _fHairColor = AccessTools.Field(t, "m_hairColor");
            _fHairItem = AccessTools.Field(t, "m_hairItem");
            _fBeardItem = AccessTools.Field(t, "m_beardItem");
            _fRightItem = AccessTools.Field(t, "m_rightItem");
            _fLeftItem = AccessTools.Field(t, "m_leftItem");
            _fLeftItemVariant = AccessTools.Field(t, "m_leftItemVariant");
            _fChestItem = AccessTools.Field(t, "m_chestItem");
            _fLegItem = AccessTools.Field(t, "m_legItem");
            _fHelmetItem = AccessTools.Field(t, "m_helmetItem");
            _fShoulderItem = AccessTools.Field(t, "m_shoulderItem");
            _fShoulderItemVariant = AccessTools.Field(t, "m_shoulderItemVariant");
            _fUtilityItem = AccessTools.Field(t, "m_utilityItem");
            _fLeftBackItem = AccessTools.Field(t, "m_leftBackItem");
            _fLeftBackItemVariant = AccessTools.Field(t, "m_leftBackItemVariant");
            _fRightBackItem = AccessTools.Field(t, "m_rightBackItem");
            _fTrinketItem = AccessTools.Field(t, "m_trinketItem");

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

        private static int ReadInt(FieldInfo f, VisEquipment vis)
        {
            if (f == null) return 0;
            try
            {
                object v = f.GetValue(vis);
                return v is int i ? i : 0;
            }
            catch { return 0; }
        }

        // Vanilla stores skin/hair color as Vector3 (xyz, implicit alpha 1). On a build where the field is
        // already a Color we read it straight. Either way emit RRGGBBAA via ColorUtility.
        private static string ReadColorRgba(FieldInfo f, VisEquipment vis)
        {
            if (f == null) return "";
            try
            {
                object v = f.GetValue(vis);
                Color c;
                if (v is Vector3 vec) c = new Color(vec.x, vec.y, vec.z, 1f);
                else if (v is Color col) c = col;
                else return "";
                return ColorUtility.ToHtmlStringRGBA(c);
            }
            catch { return ""; }
        }

        // Item fields are type-shifted string↔int across builds. string -> use; int/long hash -> token
        // ("0" hash means empty slot). The dress side re-resolves either form.
        private static string ReadItem(FieldInfo f, VisEquipment vis)
        {
            if (f == null) return "";
            try
            {
                object v = f.GetValue(vis);
                if (v == null) return "";
                if (v is string s) return s ?? "";
                if (v is int i) return i == 0 ? "" : i.ToString();
                if (v is long l) return l == 0L ? "" : l.ToString();
                return "";
            }
            catch { return ""; }
        }
    }
}
