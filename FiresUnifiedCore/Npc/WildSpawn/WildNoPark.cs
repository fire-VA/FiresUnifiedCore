using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Npc.WildSpawn
{
    /// <summary>
    /// Keeps wild companions from loitering on doorsteps (R73/R74: the same Dverger-faction wild companion parked at the
    /// mausoleum crypt entrance round after round, blocked the class drill and fought the test bot). A wild companion's home
    /// (its spawn point, or its squad leader's position for a follower) within <see cref="Radius"/> m of a dungeon entrance
    /// (a loaded Teleport) or a configured spot is pushed out along the away direction to that radius. The world keeps its
    /// wild companions; they just don't settle there. SERVER-synced config: [WildSpawn] No-park spots / No-park radius.
    /// </summary>
    internal static class WildNoPark
    {
        private static ConfigEntry<string> s_spots;
        private static ConfigEntry<float> s_radius;
        private static string s_parsedFrom;
        private static readonly List<Vector2> s_spotList = new List<Vector2>();
        private static Teleport[] s_entrances;
        private static float s_entrancesUntil;

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            s_spots = config.Bind("WildSpawn", "No-park spots", "-838,-404; -760,-440; -256,0",
                "SERVER. x,z points (';'-separated) wild companions may not make their home near (test spots, dungeon doors that aren't Teleports). Dungeon entrances (Teleports) always count.");
            s_radius = config.Bind("WildSpawn", "No-park radius", 30f,
                new ConfigDescription("SERVER. How far (m) a wild companion's home is kept from each no-park place.", new AcceptableValueRange<float>(0f, 200f)));
            configSync?.AddConfigEntry(s_spots);
            configSync?.AddConfigEntry(s_radius);
        }

        internal static float Radius => s_radius != null ? s_radius.Value : 30f;

        /// <summary>
        /// <paramref name="home"/> pushed out of every no-park place it is inside; true (with the place and how far) when it moved.
        /// </summary>
        internal static bool Adjust(ref Vector3 home, out string place, out float moved)
        {
            place = null;
            moved = 0f;
            float radius = Radius;
            if (radius <= 0f) return false;
            Vector3 start = home;
            foreach (var spot in Spots())
            {
                if (Push(ref home, new Vector3(spot.x, home.y, spot.y), radius) && place == null)
                    place = $"spot ({spot.x:0}, {spot.y:0})";
            }
            foreach (Teleport entrance in Entrances())
            {
                if (entrance == null) continue;
                if (Push(ref home, entrance.transform.position, radius) && place == null)
                    place = $"{Utils.GetPrefabName(entrance.transform.root.gameObject)} entrance";
            }
            moved = Vector3.Distance(start, home);
            if (moved < 0.01f) return false;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(home, out float height)) home.y = height;
            return true;
        }

        // Out along the flat away direction to the radius, if inside.
        private static bool Push(ref Vector3 home, Vector3 centre, float radius)
        {
            Vector3 away = home - centre;
            away.y = 0f;
            float d = away.magnitude;
            if (d >= radius) return false;
            Vector3 dir = d > 0.01f ? away / d : Vector3.forward;
            home = new Vector3(centre.x, home.y, centre.z) + dir * radius;
            return true;
        }

        private static List<Vector2> Spots()
        {
            string text = s_spots != null ? s_spots.Value : "";
            if (text == s_parsedFrom) return s_spotList;
            s_parsedFrom = text;
            s_spotList.Clear();
            foreach (string part in text.Split(';'))
            {
                string[] xz = part.Split(',');
                if (xz.Length != 2) continue;
                if (float.TryParse(xz[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(xz[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    s_spotList.Add(new Vector2(x, z));
            }
            return s_spotList;
        }

        // Loaded dungeon entrances (every Teleport in the scene), re-scanned every 10 s.
        private static Teleport[] Entrances()
        {
            if (s_entrances == null || Time.time >= s_entrancesUntil)
            {
                s_entrances = Object.FindObjectsByType<Teleport>(FindObjectsSortMode.None);
                s_entrancesUntil = Time.time + 10f;
            }
            return s_entrances;
        }

        /// <summary>Logs a move once per companion per place.</summary>
        internal static void Log(string who, string place, float moved)
        {
            string key = who + "|" + place;
            if (s_logged.Contains(key)) return;
            if (s_logged.Count > 256) s_logged.Clear();
            s_logged.Add(key);
            Debug.Log($"[WildCompanionSquad] {who}: home moved off {place} ({moved:0} m)");
        }

        private static readonly HashSet<string> s_logged = new HashSet<string>();
    }
}
