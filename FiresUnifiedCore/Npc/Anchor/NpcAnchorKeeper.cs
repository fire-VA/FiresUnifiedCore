using System;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc.Patrol;

namespace FiresCore.Npc.Anchor
{
    /// <summary>
    /// Server-side guardian on the <c>FiresNpcAnchor</c> prefab — vanilla <c>CreatureSpawner</c>
    /// semantics applied to placed NPCs. The anchor is a tiny persistent ZDO holding the NPC's full
    /// identity; this keeper guarantees the body exists whenever the anchor's zone is loaded, and is
    /// the ONLY thing that ever (re)creates a body. Deleting the anchor deletes the NPC forever.
    ///
    /// Duplication-proof presence check, in order:
    ///  1. <c>ZDOConnection(Spawned)</c> → body ZDO alive (pure data lookup — true even while the
    ///     body's zone is unloaded or the body wandered into another sector) → done.
    ///  2. Connection broken (save/load churn, ownership races) → scan body-prefab ZDOs for our
    ///     <see cref="NpcAnchorFields.BodyGuidBackRef"/> and RE-LINK instead of spawning.
    ///  3. Only then treat the body as gone: stamp missing-since once, wait out the respawn delay
    ///     (Static = immediate), then spawn exactly one body — owner-gated, with a spawn cooldown so
    ///     two ticks can never double-spawn.
    ///
    /// Spawn position: Static/Wander → the anchor's exact position (NEVER re-snapped through
    /// <c>ZoneSystem.FindFloor</c>, which is terrain-only and would drag floor-placed NPCs to the
    /// dirt); Patrol → the route checkpoint closest to where the body died (death hook stamps
    /// <see cref="NpcAnchorFields.DeathPos"/>), falling back to the anchor.
    /// </summary>
    public class NpcAnchorKeeper : MonoBehaviour
    {
        private const float TickSeconds = 5f;
        private const float SpawnCooldownSeconds = 10f;

        private ZNetView _nview;
        private float _noSpawnBefore;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            if (_nview == null || _nview.GetZDO() == null) return;   // prefab/ghost instance — never tick
            InvokeRepeating(nameof(Tick), UnityEngine.Random.Range(1f, 1f + TickSeconds), TickSeconds);
        }

        private void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;   // server-authoritative
            if (_nview == null || !_nview.IsValid()) return;
            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            // Anchors are tiny data ZDOs — the server is their single writer.
            if (!_nview.IsOwner()) _nview.ClaimOwnership();

            string guid = zdo.GetString(NpcAnchorFields.Guid, "");
            if (string.IsNullOrEmpty(guid))
            {
                // First server-side sight of a freshly placed anchor: mint its identity.
                guid = System.Guid.NewGuid().ToString("N");
                zdo.Set(NpcAnchorFields.Guid, guid);
            }

            // 1) Linked body still exists? (Verify the guid — never trust a recycled ZDOID.)
            ZDOID linked = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Spawned);
            if (!linked.IsNone())
            {
                var bodyZdo = ZDOMan.instance.GetZDO(linked);
                if (bodyZdo != null && bodyZdo.GetString(NpcAnchorFields.BodyGuidBackRef, "") == guid)
                {
                    if (zdo.GetLong(NpcAnchorFields.MissingSince, 0L) != 0L) zdo.Set(NpcAnchorFields.MissingSince, 0L);
                    // Keep the anchor's payload current with the live body (admin config edits land on
                    // the body ZDO from the client; the server-owned anchor refreshes from it here) —
                    // the ZDO-to-ZDO analogue of the old RefreshPlacementsFromLive.
                    global::FiresCore.Bridge.NpcAnchorBridge.InvokeCaptureToAnchor(bodyZdo, zdo);
                    return;
                }
            }

            // 2) Connection broken — try to RE-LINK before ever considering a spawn.
            string bodyPrefab = zdo.GetString(NpcAnchorFields.BodyPrefab, "StaticNpc");
            ZDO relink = FindBodyByGuid(bodyPrefab, guid);
            if (relink != null)
            {
                zdo.SetConnection(ZDOExtraData.ConnectionType.Spawned, relink.m_uid);
                zdo.Set(NpcAnchorFields.MissingSince, 0L);
                return;
            }

            // 3) Body is genuinely gone. Respawn once the delay has elapsed (Static = immediate).
            long missingSince = zdo.GetLong(NpcAnchorFields.MissingSince, 0L);
            if (missingSince == 0L)
            {
                zdo.Set(NpcAnchorFields.MissingSince, DateTime.UtcNow.Ticks);
                return;   // confirm on a later tick — also spaces delete→(no resurrect) races
            }

            var mode = NpcAnchorFields.GetMode(zdo);
            float delay = mode == NpcAnchorFields.Mode.Static ? 0f : Mathf.Max(0f, zdo.GetFloat(NpcAnchorFields.RespawnDelay, 60f));
            if ((DateTime.UtcNow - new DateTime(missingSince, DateTimeKind.Utc)).TotalSeconds < delay) return;

            if (Time.realtimeSinceStartup < _noSpawnBefore) return;
            _noSpawnBefore = Time.realtimeSinceStartup + SpawnCooldownSeconds;

            SpawnBody(zdo, guid, bodyPrefab, mode);
        }

        /// <summary>
        /// Death-hook entry (server-side): stamps the anchor matching this guid so the keeper respawns
        /// after the configured delay at the right spot — Patrol mode respawns at the route checkpoint
        /// closest to the DEATH position, so the clock and the spot both start here rather than at the
        /// keeper's next discovery tick.
        /// </summary>
        public static void NotifyBodyDeath(string guid, Vector3 deathPos)
        {
            if (string.IsNullOrEmpty(guid) || ZDOMan.instance == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var buf = new List<ZDO>();
            int idx = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(NpcAnchorFields.AnchorPrefabName, buf, ref idx)) { }
            for (int i = 0; i < buf.Count; i++)
            {
                var z = buf[i];
                if (z == null || !z.IsValid()) continue;
                if (z.GetString(NpcAnchorFields.Guid, "") != guid) continue;
                z.Set(NpcAnchorFields.DeathPos, deathPos);
                z.Set(NpcAnchorFields.MissingSince, DateTime.UtcNow.Ticks);
                return;
            }
        }

        /// <summary>Bounded prefab-iterative scan matching our guid back-reference. Only runs when the
        /// connection is broken (rare), and the static-NPC population is small.</summary>
        private static ZDO FindBodyByGuid(string bodyPrefab, string guid)
        {
            if (ZDOMan.instance == null) return null;
            var buf = new List<ZDO>();
            int idx = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(bodyPrefab, buf, ref idx)) { }
            for (int i = 0; i < buf.Count; i++)
            {
                var z = buf[i];
                if (z != null && z.IsValid() && z.GetString(NpcAnchorFields.BodyGuidBackRef, "") == guid)
                    return z;
            }
            return null;
        }

        private void SpawnBody(ZDO anchorZdo, string guid, string bodyPrefab, NpcAnchorFields.Mode mode)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(bodyPrefab) : null;
            if (prefab == null)
            {
                Debug.LogWarning($"[NpcAnchorKeeper] body prefab '{bodyPrefab}' not registered yet — retry next tick");
                return;
            }

            Vector3 anchorPos = transform.position;
            Quaternion rot = anchorZdo.GetQuaternion(NpcAnchorFields.Rotation, transform.rotation);

            // Patrol respawns at the checkpoint closest to the death spot; everything else at the
            // anchor's EXACT position (no terrain re-snap — floor placements stay on the floor).
            Vector3 pos = anchorPos;
            if (mode == NpcAnchorFields.Mode.Patrol)
            {
                Vector3 deathPos = anchorZdo.GetVec3(NpcAnchorFields.DeathPos, anchorPos);
                pos = ClosestRouteCheckpoint(anchorZdo.GetString(NpcAnchorFields.PatrolRoute, ""), deathPos, anchorPos);
            }

            // Movers spawn a hand-width up so the capsule never starts intersecting ground clutter —
            // a stone wedged in the feet at spawn rides along with every step ("pet rock"). They
            // settle the 25cm via gravity once the init freeze releases. Static fixtures stay at the
            // EXACT anchor height (they are permanently frozen; an offset would leave them floating).
            if (mode != NpcAnchorFields.Mode.Static) pos.y += 0.25f;

            var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
            var nview = go.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid())
            {
                Debug.LogError("[NpcAnchorKeeper] spawned body has no valid ZNetView — destroying");
                UnityEngine.Object.Destroy(go);
                return;
            }
            var bodyZdo = nview.GetZDO();

            // Core identity + placement fields (mirrors the proven StaticNpcRespawnManager.Respawn seed
            // order: instantiate → seed ZDO → let the body's initializer coroutine restore from it).
            bodyZdo.Set(NpcAnchorFields.BodyGuidBackRef, guid);
            bodyZdo.Set("npc_initialized", true);
            bodyZdo.Set("npc_static_placement", true);
            bodyZdo.Set("npc_stationed", true);
            bodyZdo.Set("npc_stationed_pos", anchorPos);
            bodyZdo.Set("npc_stationed_rot", rot);
            bodyZdo.Set("npc_placed_anchor", anchorPos);

            // Frontend payload (appearance + module profiles) rides the bridge.
            global::FiresCore.Bridge.NpcAnchorBridge.InvokeSeedBody(anchorZdo, bodyZdo);

            // Re-assert AFTER the seed: if the bridge manifest ever grows the back-ref field, a stale
            // captured value (empty at mint time) would overwrite the stamp above — and a body without
            // its back-ref mints itself a brand-new anchor on next materialization (duplication machine).
            bodyZdo.Set(NpcAnchorFields.BodyGuidBackRef, guid);

            anchorZdo.SetConnection(ZDOExtraData.ConnectionType.Spawned, bodyZdo.m_uid);
            anchorZdo.Set(NpcAnchorFields.MissingSince, 0L);
            anchorZdo.Set(NpcAnchorFields.DeathPos, Vector3.zero);

            Debug.Log($"[NpcAnchorKeeper] spawned body '{bodyPrefab}' for anchor {guid} at {pos} (mode={mode})");
        }

        private static Vector3 ClosestRouteCheckpoint(string routeName, Vector3 nearTo, Vector3 fallback)
        {
            try
            {
                var route = PatrolRouteManager.GetRoute(routeName);
                var points = route != null ? route.Points : null;
                if (points == null || points.Count == 0) return fallback;
                Vector3 best = points[0];
                float bestSqr = (points[0] - nearTo).sqrMagnitude;
                for (int i = 1; i < points.Count; i++)
                {
                    float d = (points[i] - nearTo).sqrMagnitude;
                    if (d < bestSqr) { bestSqr = d; best = points[i]; }
                }
                return best;
            }
            catch { return fallback; }
        }
    }
}
