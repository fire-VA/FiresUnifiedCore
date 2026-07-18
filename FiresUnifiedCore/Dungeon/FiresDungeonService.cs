using System;
using System.Collections;
using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// The server-side deferred-coroutine brain for the dungeon engine (lifted from
    /// FiresMausoleum.Structure.MausoleumService's timing half). A DontDestroyOnLoad singleton that exists only on
    /// the server and hosts the engine's regenerate coroutine. The memorial roster / ledger half stays in the
    /// owning mod on a SEPARATE component, so the engine's StopAllCoroutines-free regenerate never disturbs an FM
    /// reconcile coroutine.
    ///
    /// The stale-heal entry point <see cref="RegenerateDungeonSoon"/> waits for ZoneSystem + DungeonDB to be live,
    /// re-checks s_roomData, then dg.Generate(Full) and fires the spec's <see cref="DungeonSpec.OnDungeonGenerated"/>
    /// content hook (e.g. Mausoleum's memorial niche drop).
    /// </summary>
    public sealed class FiresDungeonService : MonoBehaviour
    {
        private static FiresDungeonService _instance;

        /// <summary>Create the server-side singleton. Safe to call repeatedly; no-op off the server.</summary>
        public static void EnsureInstance()
        {
            if (_instance != null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var go = new GameObject("FiresDungeonService");
            _instance = go.AddComponent<FiresDungeonService>();
            DontDestroyOnLoad(go);
            Debug.Log("[FiresDungeon] service started (server).");
        }

        public static FiresDungeonService Instance => _instance;

        /// <summary>
        /// Server-side safety net: regenerate a freshly-loaded dungeon DG that has no saved rooms (a stale dungeon
        /// from an earlier build), then fire the spec's content hook. Deferred a beat so ZoneSystem/DungeonDB are
        /// ready. No-op off the server.
        /// </summary>
        public static void RegenerateDungeonSoon(DungeonGenerator dg, DungeonSpec spec)
        {
            EnsureInstance();
            if (_instance == null || dg == null || spec == null) return;
            _instance.StartCoroutine(_instance.RegenerateRoutine(dg, spec));
        }

        private IEnumerator RegenerateRoutine(DungeonGenerator dg, DungeonSpec spec)
        {
            // ALWAYS defer at least one frame before doing anything. This is scheduled from DungeonGenerator.Awake,
            // and on a FRESH ZoneSystem.SpawnLocation(Full) the vanilla pipeline calls dg.Generate SYNCHRONOUSLY right
            // after Awake returns (same frame). Without this yield the readiness loop below short-circuits (ZoneSystem +
            // DungeonDB are already live), the coroutine runs synchronously inside Awake, and it generates BEFORE the
            // pipeline's own Generate — producing a wasteful double-generation whose first room set the second Generate's
            // Clear() then tears down (orphaning its networked props). Deferring one frame lets the pipeline's Generate
            // save s_roomData first, so the re-check below sees it and skips. A genuinely STALE reload has no such
            // pipeline Generate, so the re-check still finds no rooms and heals it.
            yield return null;

            // wait until the dungeon DB + zone system are live (Generate reads both).
            for (int i = 0; i < 20 && (ZoneSystem.instance == null || DungeonDB.instance == null); i++)
                yield return new WaitForSeconds(0.25f);
            if (dg == null) yield break;

            // re-check: another path may have generated it in the meantime.
            var nv = dg.GetComponent<ZNetView>();
            ZDO zdo = (nv != null && nv.IsValid()) ? nv.GetZDO() : null;
            if (zdo != null && zdo.GetByteArray(ZDOVars.s_roomData, out byte[] data) && data != null && data.Length >= 4)
                yield break;

            bool ok = false;
            try { dg.Generate(ZoneSystem.SpawnMode.Full); ok = true; }
            catch (Exception ex) { Debug.LogError($"{spec.LogTag} stale-dungeon regenerate threw: {ex.Message}"); }
            if (ok) Debug.Log($"{spec.LogTag} regenerated stale dungeon DG at {dg.transform.position}.");

            spec.OnDungeonGenerated?.Invoke();
        }

        /// <summary>
        /// FORCE a resize + regenerate of an ALREADY-generated dungeon: set the DungeonGenerator's m_maxRooms and
        /// re-run Generate(Full) unconditionally (unlike <see cref="RegenerateDungeonSoon"/>, which SKIPS when saved
        /// room data already exists). Used to GROW a dungeon whose room count must scale with demand (e.g. the
        /// mausoleum crypt scaling to the memorial roster). DungeonGenerator.Generate calls Clear() first, so this
        /// tears down and re-places rooms — the CALLER must ensure no player is mid-dungeon. Room placement is
        /// deterministic (position seed), so the existing rooms re-place identically and the extra rooms append.
        /// Fires the spec's <see cref="DungeonSpec.OnDungeonGenerated"/> afterward (which re-runs reconciliation).
        /// Server only.
        /// </summary>
        public static void ResizeRegenerate(DungeonGenerator dg, DungeonSpec spec, int maxRooms)
        {
            EnsureInstance();
            if (_instance == null || dg == null || spec == null || maxRooms <= 0) return;
            _instance.StartCoroutine(_instance.ResizeRegenerateRoutine(dg, spec, maxRooms));
        }

        private IEnumerator ResizeRegenerateRoutine(DungeonGenerator dg, DungeonSpec spec, int maxRooms)
        {
            yield return null;
            for (int i = 0; i < 20 && (ZoneSystem.instance == null || DungeonDB.instance == null); i++)
                yield return new WaitForSeconds(0.25f);
            if (dg == null) yield break;

            dg.m_maxRooms = maxRooms;
            if (dg.m_minRooms > maxRooms) dg.m_minRooms = maxRooms; // vanilla defaults have min > max; keep min <= max

            bool ok = false;
            try { dg.Generate(ZoneSystem.SpawnMode.Full); ok = true; }
            catch (Exception ex) { Debug.LogError($"{spec.LogTag} resize-regenerate threw: {ex.Message}"); }
            if (ok) Debug.Log($"{spec.LogTag} resized to maxRooms={dg.m_maxRooms} and regenerated at {dg.transform.position}.");

            spec.OnDungeonGenerated?.Invoke();
        }

        /// <summary>
        /// Run a deferred action on the server-side coroutine host after <paramref name="delay"/> seconds. A thin
        /// generic helper for owning mods that want to ride the engine's host instead of standing up their own.
        /// </summary>
        public static void RunSoon(Action action, float delay = 0f)
        {
            EnsureInstance();
            if (_instance == null || action == null) return;
            _instance.StartCoroutine(_instance.RunSoonRoutine(action, delay));
        }

        private IEnumerator RunSoonRoutine(Action action, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            try { action(); } catch (Exception ex) { Debug.LogError($"[FiresDungeon] RunSoon threw: {ex.Message}"); }
        }
    }
}
