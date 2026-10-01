using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// A coarse route over the terrain for a walking body (Core 0.2.201, Fire: "pathfinding around terrain features … need work").
    ///
    /// The navmesh lets a humanoid agent up faces far steeper than a player's 38°, and SlopeGuide only sees the ground 2.4 m ahead.
    /// So the bot walked straight at ridges, banks and pockets and got boxed in (Tools\NAV_FAILURE_CATALOGUE.md #1-8, #11, #13-14).
    /// This plans across them: A* over a <see cref="Cell"/> m grid in a box round the start and goal.
    ///
    /// Heights come from the loaded terrain (Heightmap, with digs and raises). Unloaded ground uses the world generator's height,
    /// with an uncertainty cost because the real ground may differ.
    ///
    /// An edge is checked at <see cref="Sample"/> m steps with SlopeGuide's rules, so the plan and the walk agree:
    /// - climbing steeper than the body's limit is impassable;
    /// - going down is walkable unless it drops more than <see cref="SlopeGuide.FallSafe"/> m within one step (a drop-off);
    /// - deep water costs extra (a swim).
    ///
    /// Loaded cells with a rock or a building across them (static_solid / piece at chest height) are blocked.
    /// The grid route is then pulled tight (a waypoint is skipped while the straight line to the next is walkable).
    /// </summary>
    public static class TerrainPlanner
    {
        /// <summary>The coarsest grid (m, long walks); short walks plan finer, down to <see cref="FineCell"/> m (0.2.202: a 4 m U of walls needs it).</summary>
        public const float Cell = 4f, Sample = 2f, FineCell = 1f;
        /// <summary>A plan whose goal cell is refused ends at a cell this near (m) with the goal in sight, the goal one step on.</summary>
        public const float NearGoalMetres = 1.5f;
        /// <summary>The search box: start and goal plus this margin (at least), capped at <see cref="MaxBox"/> m a side.</summary>
        public const float Margin = 40f, MaxBox = 400f;
        /// <summary>Extra cost share for ground not loaded yet (world generator height, no rocks or digs).</summary>
        public const float UnloadedCost = 0.35f;
        /// <summary>Extra cost share for a step through water deeper than a body can wade.</summary>
        public const float SwimCost = 3f, WadeDepth = 1.2f;
        private const float IndoorsHeight = 3000f;
        private const int MaxExpanded = 12000;
        private const float StartFreeRadius = 1.5f;

        private static int s_obstacleMask;
        private static int ObstacleMask => s_indoors ? (s_obstacleMaskIn != 0 ? s_obstacleMaskIn : (s_obstacleMaskIn = LayerMask.GetMask("static_solid", "piece", "Default")))
            : s_obstacleMask != 0 ? s_obstacleMask : (s_obstacleMask = LayerMask.GetMask("static_solid", "piece"));
        // Indoors (a dungeon, far above the world): its floors and walls are on the Default layer (R77 Crypt4: "dirtfloor (Default; DG_ForestCrypt)").
        private static bool s_indoors;
        private static int s_obstacleMaskIn, s_surfaceMaskIn;

        // Search state, reused (one plan at a time, on the main thread).
        private static readonly Dictionary<long, float> s_height = new Dictionary<long, float>();
        private static readonly Dictionary<long, bool> s_loaded = new Dictionary<long, bool>();
        private static readonly Dictionary<long, bool> s_blocked = new Dictionary<long, bool>();
        private static readonly Dictionary<int, float> s_g = new Dictionary<int, float>();
        private static readonly Dictionary<int, int> s_from = new Dictionary<int, int>();
        private static readonly HashSet<int> s_closed = new HashSet<int>();
        private static readonly List<KeyValuePair<float, int>> s_open = new List<KeyValuePair<float, int>>();
        private static readonly List<Vector3> s_raw = new List<Vector3>();

        private static float s_minX, s_minZ;
        private static float s_cell = Cell, s_sample = Sample;
        // The walker's feet level for the surface probe where no heightmap answers (voxel ground).
        private static float s_refY;
        private const float ProbeAbove = 2.2f, ProbeBelow = 80f;
        private static int s_surfaceMask;
        private static int SurfaceMask => s_terrainOnly ? (s_terrainMask != 0 ? s_terrainMask : (s_terrainMask = LayerMask.GetMask("terrain")))
            : s_indoors ? (s_surfaceMaskIn != 0 ? s_surfaceMaskIn : (s_surfaceMaskIn = LayerMask.GetMask("terrain", "static_solid", "piece", "Default")))
            : s_surfaceMask != 0 ? s_surfaceMask : (s_surfaceMask = LayerMask.GetMask("terrain", "static_solid", "piece"));
        // The ridge check reads the ground only (0.2.206, R79 l_corner: its surface ray landed on a 2 m woodwall's top and called it a
        // "49° ridge", which sent a plain corner walk to the planner).
        private static bool s_terrainOnly;
        private static int s_terrainMask;
        private static int s_w, s_h;
        private static float s_limit;
        private static float s_water;

        /// <summary>
        /// Plans a walkable way for <paramref name="body"/> from <paramref name="from"/> to <paramref name="goal"/>. On success
        /// <paramref name="route"/> holds the waypoints (the last one is the goal) and <paramref name="why"/> describes it; on failure
        /// <paramref name="why"/> says why. Indoors too: a dungeon is planned over its floors (0.2.202).
        /// </summary>
        public static bool Plan(Character body, Vector3 from, Vector3 goal, List<Vector3> route, out string why) =>
            Plan(body, from, goal, route, out why, null, 0f, DefaultClearanceWeight);

        /// <summary>
        /// <see cref="Plan(Character, Vector3, Vector3, List{Vector3}, out string)"/> for a retry (0.2.206, Fire R79: "just gonna try the same
        /// shit that failed?"): ground within <paramref name="avoidRadius"/> of each <paramref name="avoid"/> point costs <see cref="AvoidCost"/>
        /// times more (where earlier routes stalled), and nearness to walls weighs <paramref name="clearanceWeight"/>.
        /// </summary>
        public static bool Plan(Character body, Vector3 from, Vector3 goal, List<Vector3> route, out string why,
            IReadOnlyList<Vector3> avoid, float avoidRadius, float clearanceWeight)
        {
            s_avoid = avoid;
            s_avoidRadius = avoidRadius;
            s_clearanceWeight = clearanceWeight > 0f ? clearanceWeight : DefaultClearanceWeight;
            s_start = from;
            s_terrainOnly = false;
            // A goal in a fire's damage goes to its edge (0.2.220, R90: the route's first waypoint was the base campfire).
            Vector3 asked = goal;
            goal = Hazards.SafeGoal(goal, from, out string goalHazard);
            string goalNote = goalHazard != null ? $"goal ({asked.x:0}, {asked.z:0}) moved out of {goalHazard}; " : "";
            route.Clear();
            why = null;
            if (body == null) { why = "no body"; return false; }
            // Indoors too (0.2.202, R77 Crypt4: "no terrain way round … indoors (no terrain)", stuck under the exit ledge): a dungeon is
            // all colliders, and the surface ray, the wall casts and the jumpable-rise rule work on its floors as on the ground.
            s_indoors = from.y > IndoorsHeight || goal.y > IndoorsHeight;
            s_limit = SlopeGuide.Limit(body);
            s_body = body as Humanoid;
            s_refY = from.y;
            if (s_limit >= 89f) { why = "this body walks any slope"; return false; }
            s_water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;

            float span = Mathf.Max(Mathf.Abs(goal.x - from.x), Mathf.Abs(goal.z - from.z));
            float margin = Mathf.Max(Margin, span * 0.5f);
            float minX = Mathf.Min(from.x, goal.x) - margin, maxX = Mathf.Max(from.x, goal.x) + margin;
            float minZ = Mathf.Min(from.z, goal.z) - margin, maxZ = Mathf.Max(from.z, goal.z) + margin;
            if (maxX - minX > MaxBox || maxZ - minZ > MaxBox) { why = $"too far for the terrain planner ({span:0} m; box cap {MaxBox:0} m)"; return false; }
            s_minX = minX;
            s_minZ = minZ;
            // About 100 cells a side: 1 m for a walk within the base (a U of walls, a hut), 4 m for a 300 m hill.
            s_cell = Mathf.Clamp(Mathf.Ceil(Mathf.Max(maxX - minX, maxZ - minZ) / 100f), FineCell, Cell);
            s_sample = Mathf.Min(Sample, s_cell);
            s_w = Mathf.CeilToInt((maxX - minX) / s_cell) + 1;
            s_h = Mathf.CeilToInt((maxZ - minZ) / s_cell) + 1;

            s_height.Clear(); s_loaded.Clear(); s_blocked.Clear(); s_clear.Clear(); s_nodeY.Clear();
            s_g.Clear(); s_from.Clear(); s_closed.Clear(); s_open.Clear();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            s_rejWall = s_rejRise = s_rejDrop = s_rejBlocked = s_rejHazard = 0;
            float closest = float.MaxValue;
            int closestNode = -1;

            int start = NodeAt(from), end = NodeAt(goal);
            s_g[start] = 0f;
            s_nodeY[start] = Surface(from).y;
            Push(Heuristic(start, end), start);
            int expanded = 0;
            bool found = false;
            while (s_open.Count > 0)
            {
                int node = Pop();
                if (!s_closed.Add(node)) continue;
                if (node == end) { found = true; break; }
                float hDist = Heuristic(node, end);
                if (hDist < closest) { closest = hDist; closestNode = node; }
                if (++expanded > MaxExpanded) break;
                int nx = node % s_w, nz = node / s_w;
                float g = s_g[node];
                Vector3 here = NodePos(node);
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int mx = nx + dx, mz = nz + dz;
                    if (mx < 0 || mz < 0 || mx >= s_w || mz >= s_h) continue;
                    int next = mz * s_w + mx;
                    if (s_closed.Contains(next)) continue;
                    // The goal cell is never refused for an obstacle (the goal itself may be a piece or sit by a rock).
                    // Round the start the cell buffer is skipped (0.2.202: wedged in a corner of the mausoleum Facade every neighbour lay within
                    // its 0.8 m and nothing was open); the per-step wall test still refuses a step into the wall itself.
                    // The neighbour's floor is found from THIS cell's floor level (a stair, a ledge, the next storey up), not from
                    // the start's: level by level, the way a body would climb.
                    Vector3 there = Surface(new Vector3(s_minX + mx * s_cell, here.y, s_minZ + mz * s_cell));
                    if (next != end && Flat(there, from) > StartFreeRadius && Blocked(there)) { s_rejBlocked++; continue; }
                    if (next != end && Flat(there, from) > StartFreeRadius && Hazards.Inside(there, out _)) { s_rejHazard++; continue; }
                    float step = EdgeCost(here, there);
                    if (float.IsInfinity(step)) continue;
                    float cand = g + step;
                    if (s_g.TryGetValue(next, out float old) && old <= cand) continue;
                    s_g[next] = cand;
                    s_nodeY[next] = there.y;
                    s_from[next] = node;
                    Push(cand + Heuristic(next, end), next);
                }
            }
            // The goal cell itself refused (a goal against a wall, in a dead end: R85 corridor "nearest reached 1.0 m from the goal"):
            // a cell within NearGoalMetres with the goal in plain sight is the way's end, the goal one step on (0.2.214).
            bool nearEnd = false;
            string nearWhy = null;
            if (!found && closestNode >= 0 && closest <= NearGoalMetres && closestNode != start)
            {
                Vector3 near = NodePos(closestNode);
                // A hit on the goal's own spot (within 0.4 m of it) doesn't count (0.2.218).
                if (!Physics.Linecast(near + Vector3.up * 1f, goal + Vector3.up * 1f, out RaycastHit block, ObstacleMask, QueryTriggerInteraction.Ignore)
                    || Flat(block.point, goal) < 0.4f)
                {
                    end = closestNode;
                    found = true;
                    nearEnd = true;
                }
                // Which cell was nearest and what stood between (R88 corridor, "nearest reached 1.0 m from the goal" with no more said):
                // inside the corridor's cap or outside it.
                else nearWhy = $"; the nearest cell ({near.x:0.0}, {near.z:0.0}) has {block.collider.name} at ({block.point.x:0.0}, {block.point.z:0.0}) between it and the goal";
            }
            if (!found)
            {
                why = expanded > MaxExpanded
                    ? $"no walkable way found within {MaxExpanded} cells ({clock.ElapsedMilliseconds} ms)"
                    : $"no walkable way on the terrain (searched {expanded} cells of {s_cell:0} m; slope limit {s_limit:0}°; {clock.ElapsedMilliseconds} ms; " +
                      $"nearest reached {closest:0.0} m from the goal; refused: {s_rejWall} wall, {s_rejRise} rise over {Jumping.MaxLip:0.0} m, {s_rejDrop} drop-off, {s_rejBlocked} blocked cell, {s_rejHazard} in fire or damage{nearWhy})";
                return false;
            }

            // Back from the goal, then pull tight.
            s_raw.Clear();
            for (int n = end; ; n = s_from[n])
            {
                s_raw.Add(NodePos(n));
                if (n == start || !s_from.ContainsKey(n)) break;
            }
            s_raw.Reverse();
            s_raw[0] = Surface(from);
            if (nearEnd) s_raw.Add(goal);
            else s_raw[s_raw.Count - 1] = goal;
            Vector3 anchor = s_raw[0];
            for (int i = 1; i < s_raw.Count; i++)
            {
                bool last = i == s_raw.Count - 1;
                if (!last && Walkable(anchor, s_raw[i + 1])) continue;
                route.Add(s_raw[i]);
                anchor = s_raw[i];
            }
            float length = 0f;
            Vector3 prev = from;
            foreach (Vector3 p in route) { length += Flat(prev, p); prev = p; }
            int unloaded = 0;
            foreach (Vector3 p in s_raw) if (!IsLoaded(p)) unloaded++;
            // The tightest spot on the route (not the start or the goal, which may sit by a wall on purpose).
            float tightest = WantedClearance;
            Vector3 tightAt = goal;
            for (int i = 1; i < s_raw.Count - 1; i++)
            {
                float c = Clearance(s_raw[i]);
                if (c < tightest) { tightest = c; tightAt = s_raw[i]; }
            }
            // Doors the route goes through (the walker opens each on arrival: its door rule).
            s_doorsCrossed = 0;
            Vector3 legFrom = from;
            foreach (Vector3 p in route)
            {
                if (Crossing(Surface(legFrom), p) == DoorCost) s_doorsCrossed++;
                legFrom = p;
            }
            string clearance = (s_doorsCrossed > 0 ? $"through {s_doorsCrossed} door(s), " : "") +
                               (tightest >= WantedClearance - 0.5f ? $"clearance {tightest:0} m+ (target {WantedClearance:0}, open ground)"
                : $"clearance {tightest:0} m at ({tightAt.x:0}, {tightAt.z:0}) (target {WantedClearance:0}; the tightest point on the best way, wall weight x{s_clearanceWeight:0})");
            why = $"{goalNote}{route.Count} waypoint(s), {length:0} m vs straight {Flat(from, goal):0} m, {clearance} (searched {expanded} cells in {clock.ElapsedMilliseconds} ms; {unloaded} of {s_raw.Count} on unloaded ground)";
            return true;
        }

        /// <summary>
        /// The steepest climb (degrees) along the straight flat line a→b at <see cref="Sample"/> m steps, and where it starts;
        /// 0 when it only goes down or stays level. Used to spot a ridge on a navmesh leg before walking into it.
        /// </summary>
        public static float SteepestClimb(Vector3 a, Vector3 b, out Vector3 at)
        {
            at = a;
            s_refY = a.y;
            s_indoors = a.y > IndoorsHeight;
            // Terrain only, with its own heights (the caches hold surface-ray heights from the last plan).
            s_terrainOnly = true;
            s_height.Clear(); s_loaded.Clear(); s_blocked.Clear(); s_clear.Clear();
            try { return SteepestClimbTerrain(a, b, out at); }
            finally
            {
                s_terrainOnly = false;
                s_height.Clear(); s_loaded.Clear(); s_blocked.Clear(); s_clear.Clear();
            }
        }

        private static float SteepestClimbTerrain(Vector3 a, Vector3 b, out Vector3 at)
        {
            at = a;
            float length = Flat(a, b);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / Sample));
            float worst = 0f;
            Vector3 prev = Surface(a);
            for (int i = 1; i <= steps; i++)
            {
                Vector3 p = Surface(Vector3.Lerp(a, b, i / (float)steps));
                float run = Flat(prev, p);
                if (run > 0.01f)
                {
                    float rise = p.y - prev.y;
                    if (rise > 0f)
                    {
                        float tilt = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;
                        if (tilt > worst) { worst = tilt; at = prev; }
                    }
                }
                prev = p;
            }
            return worst;
        }

        // ---- grid ----

        private static int NodeAt(Vector3 p)
        {
            int x = Mathf.Clamp(Mathf.RoundToInt((p.x - s_minX) / s_cell), 0, s_w - 1);
            int z = Mathf.Clamp(Mathf.RoundToInt((p.z - s_minZ) / s_cell), 0, s_h - 1);
            return z * s_w + x;
        }

        private static Vector3 NodePos(int node)
        {
            float x = s_minX + (node % s_w) * s_cell, z = s_minZ + (node / s_w) * s_cell;
            return new Vector3(x, s_nodeY.TryGetValue(node, out float y) ? y : Height(x, z, s_refY), z);
        }

        // Each reached cell's floor height (found from the cell it was reached from).
        private static readonly Dictionary<int, float> s_nodeY = new Dictionary<int, float>();

        private static float Heuristic(int a, int b)
        {
            int ax = a % s_w, az = a / s_w, bx = b % s_w, bz = b / s_w;
            float dx = Mathf.Abs(ax - bx), dz = Mathf.Abs(az - bz);
            return s_cell * (Mathf.Max(dx, dz) + 0.4142f * Mathf.Min(dx, dz));
        }

        // A binary heap on (f, node).
        private static void Push(float f, int node)
        {
            s_open.Add(new KeyValuePair<float, int>(f, node));
            int i = s_open.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (s_open[parent].Key <= s_open[i].Key) break;
                (s_open[parent], s_open[i]) = (s_open[i], s_open[parent]);
                i = parent;
            }
        }

        private static int Pop()
        {
            int top = s_open[0].Value;
            int last = s_open.Count - 1;
            s_open[0] = s_open[last];
            s_open.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < s_open.Count && s_open[l].Key < s_open[m].Key) m = l;
                if (r < s_open.Count && s_open[r].Key < s_open[m].Key) m = r;
                if (m == i) break;
                (s_open[m], s_open[i]) = (s_open[i], s_open[m]);
                i = m;
            }
            return top;
        }

        // ---- ground ----

        // (x, z) at 0.5 m and the level it was looked up from at 1 m: a stair's two storeys are two keys.
        private static long Key(float x, float z, float y) =>
            ((long)(Mathf.RoundToInt(x * 2f) & 0x1FFFFF) << 42) | ((long)(Mathf.RoundToInt(z * 2f) & 0x1FFFFF) << 21) | (long)(Mathf.RoundToInt(y) & 0x1FFFFF);

        /// <summary>
        /// The floor a body at level <paramref name="nearY"/> would stand on at (x, z): the first surface under a ray from just under head
        /// height above that level (heightmap ground outdoors when the ray misses), else the world generator's height.
        /// </summary>
        private static float Height(float x, float z, float nearY)
        {
            long key = Key(x, z, nearY);
            if (s_height.TryGetValue(key, out float h)) return h;
            // SteepestClimb reads outside a plan too: keep the cache bounded (terrain edits since are picked up on the next fill).
            if (s_height.Count > 60000) { s_height.Clear(); s_loaded.Clear(); s_blocked.Clear(); }
            bool loaded = Heightmap.GetHeight(new Vector3(x, 0f, z), out h);
            float generated = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : 0f;
            // The surface a body would stand on (0.2.202): a ray down onto terrain (voxel chunks included: R77's course is voxel
            // ground, which the heightmap doesn't cover), rock or built pieces (steps, floors), from just under head height above
            // the heightmap ground (or the walker's feet level where there is none). A wall taller than that isn't hit from the
            // inside, so its cell reads as the ground beside it and the wall test blocks it.
            // Indoors from the level walked on (a dungeon's world-generator height is the ground far below it); outdoors from the
            // heightmap ground, or the higher of the level and the generated height on voxel ground.
            // Outdoors never below the level walked on (0.2.211, R82 nav_course on the voxel-levelled pad at y 55: the heightmap under
            // the voxel fill lies lower, the ray started inside the fill and read the heightmap, and the cell test at 1.85 m above that
            // sat in the fill: "39 blocked cell", no way out of the U).
            float probeTop = (s_indoors ? nearY : Mathf.Max(nearY, loaded ? h : generated)) + ProbeAbove;
            if (Physics.Raycast(new Vector3(x, probeTop, z), Vector3.down, out RaycastHit ground, ProbeAbove + ProbeBelow, SurfaceMask, QueryTriggerInteraction.Ignore)
                && !(ground.collider.attachedRigidbody != null && ground.collider.attachedRigidbody.GetComponent<Character>() != null))
            {
                h = ground.point.y;
                loaded = true;
            }
            else if (s_indoors) { h = nearY - ProbeBelow - 10f; loaded = false; }   // no floor within reach: a pit (a drop-off), never walked into
            else if (!loaded) h = generated;
            s_height[key] = h;
            s_loaded[key] = loaded;
            return h;
        }

        private static bool IsLoaded(Vector3 p)
        {
            Height(p.x, p.z, p.y);
            return s_loaded.TryGetValue(Key(p.x, p.z, p.y), out bool loaded) && loaded;
        }

        // The floor under p, found from p's own level (p.y: a cell's floor, or a point between two floors).
        private static Vector3 Surface(Vector3 p) => new Vector3(p.x, Height(p.x, p.z, p.y), p.z);

        // A rock or a building across the cell at chest height (loaded ground only: nothing is known of unloaded ground).
        // ---- Clearance (0.2.202, Fire: "they have a giant field here … why is it trying to use the shortest possible path") ----

        /// <summary>Routes keep this far (m) off walls, rocks and buildings where there is room; closer costs more, down to the cell buffer.</summary>
        public const float WantedClearance = 3f;
        /// <summary>How much nearness to walls weighs by default (0.2.206: 4, was 1.5, which let "clearance 0 m" routes win in open ground).</summary>
        public const float DefaultClearanceWeight = 4f;
        /// <summary>Ground near a point where an earlier route for the same goal stalled costs this many times more.</summary>
        public const float AvoidCost = 15f;
        private static float s_clearanceWeight = DefaultClearanceWeight;
        private static IReadOnlyList<Vector3> s_avoid;
        private static float s_avoidRadius;
        private static Vector3 s_start;

        private static bool NearAvoid(Vector3 p)
        {
            if (s_avoid == null || s_avoidRadius <= 0f || Flat(p, s_start) <= StartFreeRadius) return false;
            foreach (Vector3 a in s_avoid) if (Flat(p, a) <= s_avoidRadius) return true;
            return false;
        }
        private static readonly Dictionary<long, float> s_clear = new Dictionary<long, float>();

        /// <summary>Free room round a loaded point at body height, in whole metres up to <see cref="WantedClearance"/> (unloaded: the full clearance).</summary>
        private static float Clearance(Vector3 p)
        {
            long key = Key(p.x, p.z, p.y);
            if (s_clear.TryGetValue(key, out float c)) return c;
            c = WantedClearance;
            if (IsLoaded(p))
            {
                for (float r = 1f; r <= WantedClearance; r += 1f)
                {
                    if (Physics.CheckCapsule(p + Vector3.up * 0.9f, p + Vector3.up * 1.8f, r, ObstacleMask, QueryTriggerInteraction.Ignore))
                    {
                        c = r - 1f;
                        break;
                    }
                }
            }
            s_clear[key] = c;
            return c;
        }

        // Something built or rocky across the flat line a→b at knee and chest height above the higher end (a riser the body can
        // jump sits below knee height and is handled as a rise, not a wall).
        private static bool WallAcross(Vector3 a, Vector3 b) => float.IsInfinity(Crossing(a, b));

        /// <summary>A door crossed on the way costs this much (m): cheaper than any real detour, dearer than open ground.</summary>
        public const float DoorCost = 3f;
        private static readonly RaycastHit[] s_hits = new RaycastHit[16];
        private static readonly Collider[] s_overlap = new Collider[16];
        private static Humanoid s_body;
        private static int s_doorsCrossed;
        // Why moves were refused in the last plan (a failed plan says which rule stopped it).
        private static int s_rejWall, s_rejRise, s_rejDrop, s_rejBlocked, s_rejHazard;

        // What stands across the flat line a→b at knee and chest height: nothing (0), only a door the body may pass (DoorCost:
        // 0.2.205, R78 doorway: a closed gate read as a wall and the plan went 26 m round it), or a wall (infinity).
        private static float Crossing(Vector3 a, Vector3 b)
        {
            float length = Flat(a, b);
            if (length < 0.01f) return 0f;
            float baseY = Mathf.Max(a.y, b.y);
            bool door = false;
            foreach (float up in WallHeights)
            {
                Vector3 from = new Vector3(a.x, baseY + up, a.z), to = new Vector3(b.x, baseY + up, b.z);
                // Stopping short of both ends (0.2.206): a face just beyond the next cell (the 2 m ledge behind the last riser) is not a wall across this
                // step, and a thinner sphere doesn't reach it from the cell centre.
                int n = length > 0.3f ? Physics.SphereCastNonAlloc(from + (to - from) / length * 0.15f, 0.15f, (to - from) / length, s_hits, length - 0.3f, ObstacleMask, QueryTriggerInteraction.Ignore) : 0;
                for (int i = 0; i < n; i++)
                {
                    if (s_hits[i].collider == null) continue;
                    if (PassableDoor(s_hits[i].collider)) { door = true; continue; }
                    return float.PositiveInfinity;
                }
            }
            return door ? DoorCost : 0f;
        }

        // A collider of a door this body may go through: an open one, or a closed one it can open (not locked, the ward allows).
        private static bool PassableDoor(Collider c)
        {
            Door d = c.GetComponentInParent<Door>();
            if (d == null) return false;
            return !DoorRule.IsClosed(d) || DoorRule.IsPassable(d, s_body);
        }

        private static readonly float[] WallHeights = { 0.6f, 1.4f };

        // The cell test's capsule: from just over a jumpable lip to just under a door lintel (m above the floor), this wide.
        // 0.3 m (was 0.4, 0.2.212): on a 1 m grid a 1.8 m corridor off the grid's alignment had no free cell (R83 corridor: "no walkable way", 7878 cells).
        private const float CellCheckRadius = 0.3f, CellCheckLow = Jumping.MaxLip + 0.05f + CellCheckRadius, CellCheckHigh = 1.85f;

        private static bool Blocked(Vector3 p)
        {
            long key = Key(p.x, p.z, p.y);
            if (s_blocked.TryGetValue(key, out bool blocked)) return blocked;
            blocked = false;
            if (IsLoaded(p))
            {
                // Above jump height only (0.2.205, R78 jump_steps: a capsule from 0.1 m up hit the next riser, so every cell beside a step read
                // as blocked and "no walkable way" came back for a stair of 0.5 / 0.5 / 1.0 m). Anything lower is a rise (the step rule in
                // EdgeCost) or a wall across the step (the knee / chest casts in Crossing).
                int n = Physics.OverlapCapsuleNonAlloc(p + Vector3.up * CellCheckLow, p + Vector3.up * CellCheckHigh, CellCheckRadius, s_overlap, ObstacleMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n && !blocked; i++)
                    if (s_overlap[i] != null && !PassableDoor(s_overlap[i])) blocked = true;   // a doorway cell stays open (0.2.205)
            }
            s_blocked[key] = blocked;
            return blocked;
        }

        /// <summary>
        /// The cost of walking a→b (flat metres, weighted), or infinity when it can't be walked. It is checked at Sample m steps:
        /// a climb past the slope limit is impassable; a drop over FallSafe within one step is a drop-off; steep climbs and swims
        /// cost more; unloaded ground costs UnloadedCost more.
        /// </summary>
        private static float EdgeCost(Vector3 a, Vector3 b)
        {
            float length = Flat(a, b);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / s_sample));
            // A wall across the step itself (0.2.202: a 0.2 m woodwall between two cell centres was invisible to the cell test); a door the
            // body may pass costs DoorCost instead (0.2.205).
            float cost = IsLoaded(a) && IsLoaded(b) ? Crossing(a, b) : 0f;
            if (float.IsInfinity(cost)) { s_rejWall++; return cost; }
            Vector3 prev = a;
            for (int i = 1; i <= steps; i++)
            {
                Vector3 q = Vector3.Lerp(a, b, i / (float)steps);
                Vector3 p = Surface(q);
                // Through a fire's damage never (0.2.220), a smoothed shortcut included; round the start the body may be walking out of one.
                if (Flat(p, s_start) > StartFreeRadius && Hazards.Inside(p, out _)) { s_rejHazard++; return float.PositiveInfinity; }
                float run = Flat(prev, p);
                float rise = p.y - prev.y;
                float step = run;
                if (rise > 0f)
                {
                    float tilt = Mathf.Atan2(rise, Mathf.Max(0.01f, run)) * Mathf.Rad2Deg;
                    // A lip a body can jump is not a slope (SlopeGuide asks Jumping the same way).
                    if (tilt > s_limit && rise > Jumping.MaxLip) { s_rejRise++; return float.PositiveInfinity; }
                    float share = tilt / s_limit;
                    step *= 1f + 2f * share * share;
                }
                else if (-rise > SlopeGuide.FallSafe) { s_rejDrop++; return float.PositiveInfinity; }
                if (s_water - p.y > WadeDepth) step *= 1f + SwimCost;
                if (!IsLoaded(p)) step *= 1f + UnloadedCost;
                // Near a wall, a rock or a building costs more: routes run wide where there's room, tight only where it's the only way.
                else step *= 1f + s_clearanceWeight * (1f - Clearance(p) / WantedClearance);
                if (NearAvoid(p)) step *= 1f + AvoidCost;
                cost += step;
                prev = p;
            }
            return cost;
        }

        // The straight line a→b is walkable: every step passes EdgeCost's rules and no loaded cell on it is blocked.
        private static bool Walkable(Vector3 a, Vector3 b)
        {
            if (float.IsInfinity(EdgeCost(a, b))) return false;
            float length = Flat(a, b);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / s_sample));
            // A shortcut keeps the room its ends have: no cutting back along a wall the grid route kept clear of.
            float ca = Clearance(a), cb = Clearance(b);
            // At least 1 m unless both ends are tighter (0.2.206: a start by a wall let every shortcut hug it).
            float need = Mathf.Max(Mathf.Min(ca, cb), Mathf.Min(1f, Mathf.Max(ca, cb)));
            for (int i = 1; i < steps; i++)
            {
                Vector3 s = Surface(Vector3.Lerp(a, b, i / (float)steps));
                if (Blocked(s) || Clearance(s) < need) return false;
            }
            return true;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x, z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }
    }
}
