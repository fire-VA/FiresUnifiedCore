using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// 0.2.1222 ([ghost], public bug report via Fire 11-08: a one-frame hitch every 10 s that grew with mod count, 33-90 ms, from
    /// HomesteadNeeds.Build's four scene-wide FindObjectsByType calls). The homestead stations the needs model reads (smelters / kilns,
    /// plants, cooking stations, fires), kept as they wake instead of searched for. Vanilla has no OnDestroy on these four, so a destroyed
    /// one is dropped (Unity-null) the next time its list is walked; the walk only touches these four kinds, never the whole scene.
    /// </summary>
    internal static class HomesteadStations
    {
        internal static readonly List<Smelter> Smelters = new List<Smelter>();
        internal static readonly List<Plant> Plants = new List<Plant>();
        internal static readonly List<CookingStation> Cooking = new List<CookingStation>();
        internal static readonly List<Fireplace> Fires = new List<Fireplace>();
        internal static readonly List<Bed> Beds = new List<Bed>();     // 0.2.1221 [ghost]: ChoreBrainBase / HomesteadDefence / SnowChores
        internal static readonly List<Door> Doors = new List<Door>();  // 0.2.1221 [ghost]: SnowChores path samples

        // Awake runs once per instance. 0.2.1220 ([generator]): Near() only drops destroyed entries while the model walks the lists, and the
        // model is idle without a companion, so the lists are purged of destroyed entries whenever they reach a power of two (>= 256):
        // bounded at about twice the live stations, amortised O(1) per Awake.
        internal static void Add<T>(List<T> list, T c) where T : Component
        {
            if (c == null) return;
            int n = list.Count;
            if (n >= 256 && (n & (n - 1)) == 0) list.RemoveAll(x => x == null);
            list.Add(c);
        }

        /// <summary>The live entries within radius (XZ) of home; destroyed entries are removed as they are met.</summary>
        internal static void Near<T>(List<T> list, Vector3 home, float radius, List<T> into) where T : Component
        {
            into.Clear();
            float r2 = radius * radius;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var c = list[i];
                if (c == null) { list[i] = list[list.Count - 1]; list.RemoveAt(list.Count - 1); continue; }
                Vector3 d = c.transform.position - home; d.y = 0f;
                if (d.sqrMagnitude <= r2) into.Add(c);
            }
        }

        /// <summary>0.2.1221: the list with destroyed entries dropped (O(n) over that one kind, never the scene).</summary>
        internal static List<T> Live<T>(List<T> list) where T : Component { list.RemoveAll(x => x == null); return list; }

        internal static void Clear() { Smelters.Clear(); Plants.Clear(); Cooking.Clear(); Fires.Clear(); Beds.Clear(); Doors.Clear(); }

        internal static string Census() => $"registered smelters {Smelters.Count}, plants {Plants.Count}, cooking stations {Cooking.Count}, fires {Fires.Count}, beds {Beds.Count}, doors {Doors.Count}";
    }

    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.NpcBrain)]
    internal static class HomesteadStationsPatches
    {
        [HarmonyPatch(typeof(Smelter), "Awake")] internal static class SmelterAwake { [HarmonyPostfix] static void Postfix(Smelter __instance) => HomesteadStations.Add(HomesteadStations.Smelters, __instance); }
        [HarmonyPatch(typeof(Plant), "Awake")] internal static class PlantAwake { [HarmonyPostfix] static void Postfix(Plant __instance) => HomesteadStations.Add(HomesteadStations.Plants, __instance); }
        [HarmonyPatch(typeof(CookingStation), "Awake")] internal static class CookingAwake { [HarmonyPostfix] static void Postfix(CookingStation __instance) => HomesteadStations.Add(HomesteadStations.Cooking, __instance); }
        [HarmonyPatch(typeof(Bed), "Awake")] internal static class BedAwake { [HarmonyPostfix] static void Postfix(Bed __instance) => HomesteadStations.Add(HomesteadStations.Beds, __instance); }
        [HarmonyPatch(typeof(Door), "Awake")] internal static class DoorAwake { [HarmonyPostfix] static void Postfix(Door __instance) => HomesteadStations.Add(HomesteadStations.Doors, __instance); }
        [HarmonyPatch(typeof(Game), "Logout")] internal static class Logout { [HarmonyPrefix] static void Prefix() => HomesteadStations.Clear(); }
    }

    /// <summary>0.2.1221 [ghost]: fires are read by Snow (SnowFires melt) as well as the companion brain, so their registry is not behind
    /// the NpcBrain feature gate: one list add per Fireplace.Awake.</summary>
    internal static class HomesteadFirePatch
    {
        [HarmonyPatch(typeof(Fireplace), "Awake")] internal static class FireAwake { [HarmonyPostfix] static void Postfix(Fireplace __instance) => HomesteadStations.Add(HomesteadStations.Fires, __instance); }
    }
}
