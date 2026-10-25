using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Building
{
    /// <summary>
    /// Vanilla's placement rotation (Player.m_placeRotation, 22.5 degree steps from world north) is a world
    /// heading kept across piece changes, so a freshly picked piece may face back at the player and Infinity Hammer's forward/back/
    /// left/right moves (along the piece's own axes) then feel reversed. With "[Building] New piece faces away from you" on (default
    /// OFF: builders keep vanilla), each time the build tool is taken out and each time a DIFFERENT piece is picked, the rotation is
    /// set to the camera's yaw snapped to 22.5 degrees, so the ghost's forward points away from the player. Rotating with the rotate
    /// keys afterwards is kept while the same piece stays selected. An Infinity Hammer selection of an existing object, location or
    /// room (IH sets the rotation from it) is left alone. Local player only.
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Building)]
    internal static class NewPieceFacesAway
    {
        private const string Tag = "[Building] ";
        private static ConfigEntry<bool> s_enabled;
        private static string s_lastPiece;
        private static readonly AccessTools.FieldRef<Player, int> PlaceRotation = AccessTools.FieldRefAccess<Player, int>("m_placeRotation");
        private static readonly AccessTools.FieldRef<Player, float> PlaceRotationDegrees = AccessTools.FieldRefAccess<Player, float>("m_placeRotationDegrees");

        internal static void Bind(ConfigFile config)
        {
            s_enabled = config.Bind("Building", "New piece faces away from you", false,
                "When the build tool is taken out and whenever a different piece is picked, the piece is turned to face the way the camera "
                + "looks, so forward/back/left/right moves (e.g. Infinity Hammer's arrow keys) go away/toward/left/right. Rotating it yourself "
                + "is kept while you stay on the same piece. Off: vanilla (the last rotation is kept for every piece).");
            FiresCore.Config.CoreFeatures.GateConfig(FiresCore.Config.CoreFeatures.Building, false, s_enabled);   // FAP only
        }

        [HarmonyPatch(typeof(Player), "SetupPlacementGhost")]
        private static class Player_SetupPlacementGhost_FacesAway
        {
            private static void Prefix(Player __instance)
            {
                if (s_enabled == null || !s_enabled.Value || __instance == null || __instance != Player.m_localPlayer) return;
                try
                {
                    Piece piece = __instance.GetSelectedPiece();
                    if (piece == null) return;
                    string key = piece.name;
                    if (key == s_lastPiece) return;   // same piece: the player's own rotation stays
                    s_lastPiece = key;
                    if (InfinityHammerOwnsRotation()) return;
                    if (GameCamera.instance == null) return;
                    float step = PlaceRotationDegrees(__instance);
                    if (step <= 0f) return;
                    int steps = Mathf.RoundToInt(360f / step);
                    int index = Mathf.RoundToInt(GameCamera.instance.transform.eulerAngles.y / step) % steps;
                    if (index < 0) index += steps;
                    PlaceRotation(__instance) = index;
                    FiresCore.Logging.FiresLogger.LogDebug($"{Tag}new piece '{key}' faces away: rotation {index * step:0.#} deg (camera yaw {GameCamera.instance.transform.eulerAngles.y:0})");
                }
                catch (Exception ex) { Debug.LogWarning(Tag + ex.Message); }
            }
        }

        // The build tool put away: the next time it comes out counts as a new pick.
        [HarmonyPatch(typeof(Player), "SetPlaceMode")]
        private static class Player_SetPlaceMode_FacesAway
        {
            private static void Postfix(Player __instance, PieceTable buildPieces)
            {
                if (buildPieces == null && __instance == Player.m_localPlayer) s_lastPiece = null;
            }
        }

        // Infinity Hammer's selection of an existing object / location / room sets the rotation from what was selected; its plain
        // build-menu state is the base BaseSelection type. By reflection; false without Infinity Hammer.
        private static System.Reflection.MethodInfo s_ihGet;
        private static Type s_ihBase;
        private static bool s_ihLooked;

        private static bool InfinityHammerOwnsRotation()
        {
            if (!s_ihLooked)
            {
                s_ihLooked = true;
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("infinity_hammer", out var info) && info?.Instance != null)
                {
                    var asm = info.Instance.GetType().Assembly;
                    s_ihGet = asm.GetType("InfinityHammer.Selection")?.GetMethod("Get", Type.EmptyTypes);
                    s_ihBase = asm.GetType("InfinityHammer.BaseSelection");
                }
            }
            if (s_ihGet == null || s_ihBase == null) return false;
            try
            {
                object sel = s_ihGet.Invoke(null, null);
                return sel != null && sel.GetType() != s_ihBase;
            }
            catch { return false; }
        }
    }
}
