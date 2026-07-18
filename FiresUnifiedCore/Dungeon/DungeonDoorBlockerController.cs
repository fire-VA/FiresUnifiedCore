using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// Per-ROOM doorway blocker resolver, attached to every dungeon room prefab at ZNetScene registration time so
    /// it rides every spawned clone on the SERVER AND EVERY CLIENT. Rooms are NOT ZNetView network objects (they
    /// are plain GameObject children of the DG), and DungeonGenerator.Generate is server-only, so the open-vs-
    /// connected decision MUST be made locally and deterministically on every peer with NO RPC. RoomConnection
    /// world positions are identical on every peer (same baked prefab, same DG-relative transforms), so the pass
    /// is desync-free.
    ///
    /// Each room bakes ONE solid blocker wall as a CHILD of each RoomConnection (child name = BlockerChildName).
    /// Blockers are baked ENABLED (solid). On every peer this controller, over a short re-check window (neighbour
    /// rooms stream in slightly after this one), OPENS (SetActive(false)) the blocker at any connection that is
    ///   • m_entrance == true (the player entry doorway is never blocked), OR
    ///   • coincident (RoomConnection.TestContact, < 0.1m) with ANOTHER room's connection (a paired passage).
    /// Every remaining blocker stays ENABLED (solid) so the player can't walk through an unused doorway into the
    /// y+5000 void. Idempotent: re-running only ever DISABLES blockers, never re-enables, so a late-streaming
    /// neighbour cleanly opens a doorway that was momentarily solid.
    /// </summary>
    public sealed class DungeonDoorBlockerController : MonoBehaviour
    {
        /// <summary>The baked child name of the solid blocker GameObject under each RoomConnection.</summary>
        public const string BlockerChildName = "DoorBlocker";

        private const float CoincidentSqr = 0.1f * 0.1f; // mirrors RoomConnection.TestContact (< 0.1m)
        private const float ReCheckSeconds = 2.0f;        // window for neighbour rooms to stream in
        private const float ReCheckStep = 0.25f;

        // Process-wide registry of every live room connection on THIS peer, so any room can test pairing against
        // all others without depending on the server-only DungeonGenerator.m_placedRooms. Each peer owns its own.
        private static readonly List<RoomConnection> _allConnections = new List<RoomConnection>();

        private RoomConnection[] _mine;

        private void Awake()
        {
            _mine = GetComponentsInChildren<RoomConnection>(true);
            for (int i = 0; i < _mine.Length; i++)
                if (_mine[i] != null && !_allConnections.Contains(_mine[i]))
                    _allConnections.Add(_mine[i]);
        }

        private void OnEnable()
        {
            StopAllCoroutines();
            StartCoroutine(ResolveLoop());
        }

        private void OnDestroy()
        {
            if (_mine == null) return;
            for (int i = 0; i < _mine.Length; i++)
                _allConnections.Remove(_mine[i]);
        }

        // Re-resolve a few times across the first ~2s: a paired neighbour room may be instantiated a frame or two
        // after this one, so a doorway that looks open on the first pass becomes connected once the neighbour's
        // connection registers. Only ever DISABLES blockers => idempotent + monotonic, no flicker, no re-block.
        private IEnumerator ResolveLoop()
        {
            float t = 0f;
            ResolveOnce();
            while (t < ReCheckSeconds)
            {
                yield return new WaitForSeconds(ReCheckStep);
                t += ReCheckStep;
                ResolveOnce();
            }
        }

        private void ResolveOnce()
        {
            if (_mine == null) return;
            for (int i = 0; i < _mine.Length; i++)
            {
                RoomConnection conn = _mine[i];
                if (conn == null) continue;

                Transform blocker = conn.transform.Find(BlockerChildName);
                if (blocker == null || !blocker.gameObject.activeSelf) continue; // already open / no blocker baked

                if (conn.m_entrance || HasPartner(conn))
                    blocker.gameObject.SetActive(false); // entrance or paired => OPEN
                // else: leave the blocker ENABLED (solid) — unused edge doorway.
            }
        }

        // True if some OTHER room's connection sits within the snap distance of this one (a real passage).
        private static bool HasPartner(RoomConnection conn)
        {
            Vector3 p = conn.transform.position;
            for (int i = 0; i < _allConnections.Count; i++)
            {
                RoomConnection o = _allConnections[i];
                if (o == null || o == conn) continue;
                if ((o.transform.position - p).sqrMagnitude < CoincidentSqr) return true;
            }
            return false;
        }
    }
}
