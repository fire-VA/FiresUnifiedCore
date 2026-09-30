using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// When a closed door may be opened, finding one that blocks a route, opening it and closing it behind: one rule for the
    /// companions (CompanionDoorHandler) and the FDT bot (PathWalker's WalkState.Door), per Fire (2026-09-29): "closed doors should
    /// not cut their route; companions know how to open and close doors". A door is passable when it is closed (ZDO state 0), not
    /// locked (or its key is carried) and the wards allow it. Opening is the door's own Interact (what a player's Use does), on the
    /// peer that drives the body. <see cref="CloseBehind"/> closes it again a few seconds after the body is through, unless
    /// someone stands in the doorway.
    /// </summary>
    public static class DoorRule
    {
        public const float InteractDistance = 1.8f, CloseDistance = 2.5f, AutoCloseDelay = 3f, ProximityBlock = 2f;
        // A door counts as "on" a straight route when it stands this close to the line.
        private const float LineSlack = 1.5f;
        private const float CacheLifetime = 5f, PendingTimeout = 60f;

        private static Door[] s_doors;
        private static float s_doorsUntil;

        /// <summary>
        /// How a body uses a door (0.2.209, the lead: one brain, honest play). Null or false: the door's own Interact, what a player's Use
        /// does, with no input or animation. A driver that presses Use for its body (FDT's VerbKit.PlayerUse) sets this and returns
        /// true when it pressed it for that body.
        /// </summary>
        public static System.Func<Humanoid, Door, bool> UseHook;

        private static void Use(Humanoid body, Door door)
        {
            if (UseHook != null)
            {
                try { if (UseHook(body, door)) return; }
                catch (System.Exception e) { Debug.LogWarning($"[DoorRule] use hook failed ({e.GetType().Name}: {e.Message}); the door's own Interact instead"); }
            }
            door.Interact(body, false, false);
        }

        /// <summary>Every loaded door (cached for a few seconds).</summary>
        public static Door[] Doors()
        {
            if (s_doors == null || Time.time >= s_doorsUntil)
            {
                s_doors = Object.FindObjectsByType<Door>(FindObjectsSortMode.None);
                s_doorsUntil = Time.time + CacheLifetime;
            }
            return s_doors;
        }

        /// <summary>Forget the cached doors (after one is placed or destroyed).</summary>
        public static void InvalidateCache()
        {
            s_doors = null;
            s_doorsUntil = 0f;
        }

        /// <summary>A closed door this body may open: not locked (or <paramref name="body"/> carries its key), wards allow.</summary>
        public static bool IsPassable(Door door) => IsPassable(door, null);

        public static bool IsPassable(Door door, Humanoid body)
        {
            if (door == null || !door) return false;
            if (door.m_keyItem != null)
            {
                Inventory inventory = body != null ? body.GetInventory() : null;
                if (inventory == null || !inventory.HaveItem(door.m_keyItem.m_itemData.m_shared.m_name)) return false;
            }
            // Vanilla PrivateArea.CheckAccess dereferences the ward's m_piece and Player.m_localPlayer unguarded, and either can be
            // null (a ward mid-Awake, a player mid-teleport): without a local player, or on a throw, treat it as no access.
            if (door.m_checkGuardStone)
            {
                if (Player.m_localPlayer == null) return false;
                try
                {
                    if (!PrivateArea.CheckAccess(door.transform.position, flash: false)) return false;
                }
                catch
                {
                    return false;
                }
            }
            return IsClosed(door);
        }

        /// <summary>
        /// Seconds after this peer opened or closed a door during which it counts as already toggled. Interact asks the door's owner
        /// by RPC, so the ZDO still says the old state for a round trip; a caller asking every frame would toggle it straight back
        /// ([visual], FDT 1.1.170).
        /// </summary>
        public const float ToggleSettleSeconds = 2f;

        private static readonly Dictionary<Door, float> s_openedAt = new Dictionary<Door, float>();
        private static readonly Dictionary<Door, float> s_closedAt = new Dictionary<Door, float>();

        /// <summary>This peer opened <paramref name="door"/> less than <see cref="ToggleSettleSeconds"/> ago.</summary>
        public static bool JustOpened(Door door) => Recent(s_openedAt, door);

        /// <summary>This peer closed <paramref name="door"/> less than <see cref="ToggleSettleSeconds"/> ago.</summary>
        public static bool JustClosed(Door door) => Recent(s_closedAt, door);

        private static bool Recent(Dictionary<Door, float> table, Door door)
        {
            if (door == null || !table.TryGetValue(door, out float at)) return false;
            if (Time.time - at < ToggleSettleSeconds) return true;
            table.Remove(door);
            return false;
        }

        /// <summary>Why <see cref="IsPassable(Door, Humanoid)"/> refuses, for the logs: "locked (needs X)", "ward", "already open", …</summary>
        public static string WhyNot(Door door, Humanoid body)
        {
            if (door == null || !door) return "gone";
            if (door.m_keyItem != null)
            {
                Inventory inventory = body != null ? body.GetInventory() : null;
                if (inventory == null || !inventory.HaveItem(door.m_keyItem.m_itemData.m_shared.m_name))
                    return $"locked (needs {door.m_keyItem.m_itemData.m_shared.m_name})";
            }
            if (door.m_checkGuardStone)
            {
                if (Player.m_localPlayer == null) return "ward check needs a local player";
                bool allowed;
                try { allowed = PrivateArea.CheckAccess(door.transform.position, flash: false); }
                catch { allowed = false; }
                if (!allowed) return "a ward forbids it";
            }
            if (!IsClosed(door)) return JustOpened(door) ? "opening (just toggled)" : "already open";
            if (body != null && Vector3.Distance(body.transform.position, door.transform.position) > InteractDistance)
                return $"too far ({Vector3.Distance(body.transform.position, door.transform.position):0.0} m)";
            return "no reason found";
        }

        /// <summary>The door's synced state says closed, and this peer didn't just open it (the RPC may not have landed yet).</summary>
        public static bool IsClosed(Door door)
        {
            if (JustOpened(door)) return false;
            if (JustClosed(door)) return true;
            var nview = door != null ? door.GetComponent<ZNetView>() : null;
            ZDO zdo = nview != null ? nview.GetZDO() : null;
            return zdo != null && zdo.GetInt(ZDOVars.s_state) == 0;
        }

        /// <summary>
        /// The passable closed door that blocks the way from <paramref name="from"/> (where a path ends, or the body) toward
        /// <paramref name="to"/>: the nearest one within <paramref name="radius"/> of <paramref name="from"/>, or standing on the
        /// straight line between them. Null when there is none.
        /// </summary>
        public static Door DoorOnPath(Vector3 from, Vector3 to, float radius = 4f, Humanoid body = null)
        {
            Door best = null;
            float bestDistance = float.MaxValue;
            float lineLength = Vector3.Distance(from, to);
            foreach (Door door in Doors())
            {
                if (door == null || !IsPassable(door, body)) continue;
                Vector3 at = door.transform.position;
                float fromDoor = Vector3.Distance(from, at);
                bool near = fromDoor <= radius;
                bool onLine = fromDoor <= lineLength + radius && DistanceToLine(at, from, to) <= LineSlack;
                if (!near && !onLine) continue;
                if (fromDoor < bestDistance)
                {
                    bestDistance = fromDoor;
                    best = door;
                }
            }
            return best;
        }

        /// <summary>
        /// Opens <paramref name="door"/> for <paramref name="body"/> when it is within <see cref="InteractDistance"/> and still
        /// passable (the door's own Interact, what a player's Use does). True when it was opened.
        /// </summary>
        public static bool OpenFor(Humanoid body, Door door)
        {
            if (body == null || !IsPassable(door, body)) return false;
            if (Vector3.Distance(body.transform.position, door.transform.position) > InteractDistance) return false;
            Use(body, door);
            s_closedAt.Remove(door);
            s_openedAt[door] = Time.time;
            return true;
        }

        /// <summary>
        /// Closes <paramref name="door"/> again once <paramref name="body"/> is <see cref="CloseDistance"/> past it, after
        /// <see cref="AutoCloseDelay"/> s, unless someone stands within <see cref="ProximityBlock"/> of it. Runs on this peer.
        /// </summary>
        public static void CloseBehind(Humanoid body, Door door)
        {
            if (body == null || door == null) return;
            Runner.Ensure();
            s_pending.Add(new PendingClose
            {
                Body = body, Door = door, At = door.transform.position, Since = Time.time, Timer = AutoCloseDelay,
                Side = SideOf(door, body.transform.position), Name = Utils.GetPrefabName(door.gameObject),
            });
        }

        // Which side of the doorway a point is on: +1 in front of the door's facing, -1 behind.
        private static int SideOf(Door door, Vector3 p)
        {
            Vector3 forward = door.transform.forward;
            forward.y = 0f;
            Vector3 to = p - door.transform.position;
            to.y = 0f;
            return Vector3.Dot(forward, to) >= 0f ? 1 : -1;
        }

        /// <summary>Through the doorway (the other side from where it was opened) and this far from it (m): close it after <see cref="ThroughCloseDelay"/> s.</summary>
        public const float ThroughDistance = 1.2f, ThroughCloseDelay = 1f;
        /// <summary>A body that came this close (m) to the doorway on its way to the far side went through it.</summary>
        public const float ThroughDoorwayMetres = 1.5f;

        /// <summary>A character (other than <paramref name="except"/>) within <see cref="ProximityBlock"/> of the doorway.</summary>
        public static bool AnyoneNear(Vector3 doorway, GameObject except = null)
        {
            if (Player.m_localPlayer != null && Player.m_localPlayer.gameObject != except
                && Vector3.Distance(Player.m_localPlayer.transform.position, doorway) < ProximityBlock) return true;
            foreach (Collider collider in Physics.OverlapSphere(doorway, ProximityBlock))
            {
                if (collider == null || collider.gameObject == except) continue;
                Character character = collider.GetComponentInParent<Character>();
                if (character != null && character.gameObject != except) return true;
            }
            return false;
        }

        /// <summary>Closes an open door (the door's own Interact), if it is open.</summary>
        public static void Close(Humanoid body, Door door)
        {
            if (door == null || !door || IsClosed(door)) return;
            Use(body, door);
            s_openedAt.Remove(door);
            s_closedAt[door] = Time.time;
        }

        private static float DistanceToLine(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lengthSquared = ab.sqrMagnitude;
            float t = lengthSquared > 0f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / lengthSquared) : 0f;
            return Vector3.Distance(point, a + ab * t);
        }

        private sealed class PendingClose
        {
            public Humanoid Body;
            public Door Door;
            public Vector3 At;
            public float Since;
            public float Timer;
            public int Side;
            public string Name;
            public float Nearest = float.MaxValue;   // the body's closest approach to the doorway since the opening
        }

        private static readonly List<PendingClose> s_pending = new List<PendingClose>();

        // Ticks the pending closes on this peer.
        private sealed class Runner : MonoBehaviour
        {
            private static Runner s_instance;

            internal static void Ensure()
            {
                if (s_instance != null) return;
                var go = new GameObject("FiresDoorRule");
                Object.DontDestroyOnLoad(go);
                s_instance = go.AddComponent<Runner>();
            }

            private void Update()
            {
                for (int i = s_pending.Count - 1; i >= 0; i--)
                {
                    PendingClose p = s_pending[i];
                    string who = p.Body != null ? p.Body.m_name : "?";
                    if (p.Body == null || p.Door == null || !p.Door || Time.time - p.Since > PendingTimeout)
                    {
                        // Evidence either way (0.2.202, R77 doorway: "door left open" with no line saying why).
                        Debug.Log($"[DoorRule] {who}: door: left {p.Name} open ({(p.Body == null ? "the body is gone" : p.Door == null || !p.Door ? "the door unloaded" : $"still near it after {PendingTimeout:0} s")})");
                        s_pending.RemoveAt(i);
                        continue;
                    }
                    Vector3 here = p.Body.transform.position;
                    float distance = Vector3.Distance(here, p.At);
                    p.Nearest = Mathf.Min(p.Nearest, distance);
                    // Through the doorway: the other side and a little clear of it closes it soon, even if the walk ends near the door
                    // (R77 nav_course doorway: the goal sat within CloseDistance, the timer never ran, the door stayed open).
                    bool through = SideOf(p.Door, here) != p.Side && distance >= ThroughDistance;
                    if (!through && distance < CloseDistance) { p.Timer = AutoCloseDelay; continue; }
                    if (through) p.Timer = Mathf.Min(p.Timer, ThroughCloseDelay);
                    p.Timer -= Time.deltaTime;
                    if (p.Timer > 0f) continue;
                    if (AnyoneNear(p.At, p.Body.gameObject))
                    {
                        p.Timer = ThroughCloseDelay;   // wait for the doorway to clear (within the pending timeout)
                        continue;
                    }
                    if (IsClosed(p.Door))
                        Debug.Log($"[DoorRule] {who}: door: {p.Name} already closed");
                    else
                    {
                        Close(p.Body, p.Door);
                        // Which side and which way (0.2.209, R80 doorway: "5.8 m past it" read as a pass when the body had gone round the wall):
                        // through the doorway = on the far side having come within ThroughDoorwayMetres of it.
                        string way = !through ? "on the side it was opened from"
                            : p.Nearest <= ThroughDoorwayMetres ? "on the far side, through the doorway"
                            : $"on the far side but round it, never nearer than {p.Nearest:0.0} m";
                        Debug.Log($"[DoorRule] {who}: door: closed {p.Name} behind ({distance:0.0} m away, {way})");
                    }
                    s_pending.RemoveAt(i);
                }
            }
        }
    }
}
