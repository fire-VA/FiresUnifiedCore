using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// The bot's corner rules for the companions (one brain; Fire, R73: "not recognizing collision objects when trying to round
    /// corners"). Companions walk through vanilla BaseAI (FindPath / MoveTo / MoveTowards), so the same two rules go in there:
    /// each NEW path's corners are pushed off solids (<see cref="PathWalker.ClearCorners"/>), and a step straight into a solid face
    /// slides along it (<see cref="PathWalker.Slide"/>). Only CompanionAI bodies; monsters stay vanilla. The steep-ground rule isn't
    /// needed: vanilla never slides a non-player (Character.GetSlideAngle 90°).
    /// </summary>
    internal static class CompanionPathRules
    {
        private const float SlideReach = 0.9f, LogRepeat = 5f;
        private static readonly Dictionary<BaseAI, (string line, float at)> s_logged = new Dictionary<BaseAI, (string, float)>();

        private static void Log(BaseAI ai, string line)
        {
            if (s_logged.TryGetValue(ai, out var last) && last.line == line && Time.time - last.at < LogRepeat) return;
            if (s_logged.Count > 64) s_logged.Clear();
            s_logged[ai] = (line, Time.time);
            Character body = ai.m_character;
            Debug.Log($"[PathWalker] {(body != null ? body.m_name : ai.name)}: {line} (companion)");
        }

        [HarmonyPatch(typeof(BaseAI), "FindPath")]
        private static class BaseAI_FindPath_ClearCorners
        {
            private static void Postfix(BaseAI __instance, bool __result)
            {
                // Only a path asked just now (FindPath hands back the last result for up to 5 s).
                if (!__result || !(__instance is CompanionAI) || __instance.m_lastFindPathTime != Time.time) return;
                Character body = __instance.m_character;
                if (body == null || body.IsFlying()) return;
                string note = PathWalker.ClearCorners(__instance.m_path, body.GetRadius());
                if (note != null) Log(__instance, note);
            }
        }

        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.MoveTowards))]
        private static class BaseAI_MoveTowards_Slide
        {
            private static void Prefix(BaseAI __instance, ref Vector3 dir)
            {
                if (!(__instance is CompanionAI)) return;
                Character body = __instance.m_character;
                if (body == null || body.IsFlying() || body.IsSwimming()) return;
                Vector3 flat = new Vector3(dir.x, 0f, dir.z);
                if (flat.sqrMagnitude < 0.0001f) return;
                flat.Normalize();
                List<Vector3> path = __instance.m_path;
                Vector3? next = path != null && path.Count > 1 ? path[1] : (Vector3?)null;
                if (!PathWalker.Slide(body, flat, SlideReach, next, out Vector3 along, out string what, out string side, out Collider face)) return;
                // A passable door is the door handler's to open (CompanionDoorHandler / DoorRule), not a face to slide along.
                Door door = face != null ? face.GetComponentInParent<Door>() : null;
                if (door != null && DoorRule.IsPassable(door, body as Humanoid)) return;
                dir = along;
                Log(__instance, $"slid along {what} ({side})");
            }
        }
    }
}
