using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// Resolves doorway blockers for one dungeon room on every peer. Rooms are not network objects and generation
    /// is server-only, so each peer decides locally from identical baked positions, with no RPC. Every room
    /// connection bakes a solid blocker child; over a short re-check window, while neighbouring rooms stream in,
    /// this opens the blocker at the entrance and at any connection touching another room's, leaving unused
    /// doorways solid over the void. It only ever opens blockers, so a late neighbour cleanly opens its passage.
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
            float elapsed = 0f;
            ResolveOnce();
            while (elapsed < ReCheckSeconds)
            {
                yield return new WaitForSeconds(ReCheckStep);
                elapsed += ReCheckStep;
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
            Vector3 connectionPosition = conn.transform.position;
            for (int i = 0; i < _allConnections.Count; i++)
            {
                RoomConnection other = _allConnections[i];
                if (other == null || other == conn) continue;
                if ((other.transform.position - connectionPosition).sqrMagnitude < CoincidentSqr) return true;
            }
            return false;
        }
    }
}
