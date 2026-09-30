using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// A way through a dungeon by its own layout (Core 0.2.207; Fire, R79 Crypt4: "walking into fucking walls trying to get to enemies
    /// because it tries the shortest route rather than the actual open hallways"). The navmesh inside the crypt didn't join its rooms
    /// ("the path ended 21.7 m short"), so the walker jogged straight at a Ghost through the walls.
    ///
    /// Vanilla places a dungeon as rooms (<see cref="Room"/>: a box of m_size round its transform) joined at doorways (a
    /// <see cref="RoomConnection"/> of one room sitting on one of the next: TestContact, 0.1 m). <see cref="Route"/> finds the room
    /// each end is in, runs a shortest search over the doorways, and gives waypoints: before each doorway, the doorway, past it, the goal.
    /// </summary>
    public static class RoomGraph
    {
        /// <summary>Waypoints this far (m) before and past each doorway, so the walker lines up and clears the frame.</summary>
        public const float DoorwayStandoff = 1.5f;
        private const float ContactMetres = 0.3f, RoomSlack = 0.5f, RefreshSeconds = 10f, NearMetres = 250f;

        private sealed class Link
        {
            public Room To;
            public Vector3 Doorway;
        }

        private static Room[] s_rooms = new Room[0];
        private static float s_roomsUntil;
        private static readonly Dictionary<Room, List<Link>> s_links = new Dictionary<Room, List<Link>>();

        /// <summary>The room a point is in (the smallest box holding it), or null.</summary>
        public static Room RoomAt(Vector3 p)
        {
            Refresh(p);
            Room best = null;
            float bestVolume = float.MaxValue;
            foreach (Room r in s_rooms)
            {
                if (r == null || !Inside(r, p)) continue;
                float volume = (float)r.m_size.x * r.m_size.y * r.m_size.z;
                if (volume < bestVolume) { bestVolume = volume; best = r; }
            }
            return best;
        }

        /// <summary>A room's name for the logs ("forestcrypt_Corridor1").</summary>
        public static string Name(Room r) => r != null ? Utils.GetPrefabName(r.gameObject) : "no room";

        /// <summary>
        /// Waypoints from <paramref name="from"/> to <paramref name="to"/> through the rooms' doorways (the goal last), with a
        /// description in <paramref name="why"/>; false when either end isn't in a room, both are in the same room, or no chain of
        /// doorways joins them.
        /// </summary>
        public static bool Route(Vector3 from, Vector3 to, List<Vector3> waypoints, out string why)
        {
            waypoints.Clear();
            Room a = RoomAt(from), b = RoomAt(to);
            if (a == null || b == null) { why = $"{(a == null ? "we are" : "the goal is")} in no room"; return false; }
            if (a == b) { why = $"both in {Name(a)}"; return false; }

            // Dijkstra over rooms, the cost being the walk between doorway points.
            var dist = new Dictionary<Room, float> { [a] = 0f };
            var prev = new Dictionary<Room, KeyValuePair<Room, Vector3>>();
            var at = new Dictionary<Room, Vector3> { [a] = from };
            var open = new List<Room> { a };
            var done = new HashSet<Room>();
            while (open.Count > 0)
            {
                Room r = open[0];
                foreach (Room o in open) if (dist[o] < dist[r]) r = o;
                open.Remove(r);
                if (!done.Add(r)) continue;
                if (r == b) break;
                foreach (Link link in Links(r))
                {
                    if (link.To == null || done.Contains(link.To)) continue;
                    float cost = dist[r] + Vector3.Distance(at[r], link.Doorway);
                    if (dist.TryGetValue(link.To, out float old) && old <= cost) continue;
                    dist[link.To] = cost;
                    at[link.To] = link.Doorway;
                    prev[link.To] = new KeyValuePair<Room, Vector3>(r, link.Doorway);
                    if (!open.Contains(link.To)) open.Add(link.To);
                }
            }
            if (!prev.ContainsKey(b)) { why = $"no chain of doorways from {Name(a)} to {Name(b)}"; return false; }

            // Back from the goal's room: the doorways in order.
            var rooms = new List<Room> { b };
            var doors = new List<Vector3>();
            for (Room r = b; r != a; )
            {
                var step = prev[r];
                doors.Add(step.Value);
                rooms.Add(step.Key);
                r = step.Key;
            }
            rooms.Reverse();
            doors.Reverse();
            for (int i = 0; i < doors.Count; i++)
            {
                Vector3 d = doors[i];
                Vector3 intoFrom = Flat(rooms[i].transform.position - d), intoNext = Flat(rooms[i + 1].transform.position - d);
                waypoints.Add(Snap(d + intoFrom.normalized * DoorwayStandoff));
                waypoints.Add(Snap(d));
                waypoints.Add(Snap(d + intoNext.normalized * DoorwayStandoff));
            }
            waypoints.Add(to);
            var names = new List<string>();
            foreach (Room r in rooms) names.Add(Name(r));
            why = $"via {string.Join(" -> ", names)}: {doors.Count} doorway(s), {dist[b] + Vector3.Distance(doors[doors.Count - 1], to):0} m";
            return true;
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 0.0001f ? v : Vector3.forward; }

        private static Vector3 Snap(Vector3 p)
        {
            if (Pathfinding.instance != null && Pathfinding.instance.FindValidPoint(out Vector3 stand, p, 1.5f, Pathfinding.AgentType.Humanoid))
                return stand;
            return p;
        }

        private static bool Inside(Room r, Vector3 p)
        {
            Vector3 local = Quaternion.Inverse(r.transform.rotation) * (p - r.transform.position);
            return Mathf.Abs(local.x) <= r.m_size.x * 0.5f + RoomSlack && Mathf.Abs(local.z) <= r.m_size.z * 0.5f + RoomSlack
                   && Mathf.Abs(local.y) <= r.m_size.y * 0.5f + 2f;
        }

        // The rooms near p (cached a while; the links are rebuilt with them).
        private static void Refresh(Vector3 p)
        {
            if (Time.time < s_roomsUntil && s_rooms.Length > 0) return;
            s_roomsUntil = Time.time + RefreshSeconds;
            var near = new List<Room>();
            foreach (Room r in Object.FindObjectsByType<Room>(FindObjectsSortMode.None))
                if (r != null && Vector3.Distance(r.transform.position, p) <= NearMetres) near.Add(r);
            s_rooms = near.ToArray();
            s_links.Clear();
        }

        // A room's doorways to the rooms whose connections touch its own.
        private static List<Link> Links(Room r)
        {
            if (s_links.TryGetValue(r, out List<Link> links)) return links;
            links = new List<Link>();
            foreach (RoomConnection c in r.GetConnections())
            {
                if (c == null) continue;
                Vector3 cp = c.transform.position;
                foreach (Room o in s_rooms)
                {
                    if (o == null || o == r) continue;
                    foreach (RoomConnection oc in o.GetConnections())
                    {
                        if (oc == null || Vector3.Distance(cp, oc.transform.position) > ContactMetres) continue;
                        links.Add(new Link { To = o, Doorway = cp });
                        break;
                    }
                }
            }
            s_links[r] = links;
            return links;
        }
    }
}
