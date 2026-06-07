using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Services
{
    // Cross-mod "render this prefab at extreme distance" pipeline.
    //
    // Vanilla's distant-object system relies on ZDO.Distant being true at
    // ZDO creation time. Any ZDO with Distant=false stops syncing past
    // the peer's normal active area (~80m on default settings) — clients
    // far away never see those objects render, even if they should.
    //
    // This service lets mods flag specific prefab names as "always distant
    // up to <maxMeters>". A self-spawned driver:
    //   - Server side, every ServerScanIntervalSeconds: walks ZDOMan for
    //     every registered prefab, sets ZDO.Distant=true on each, calls
    //     ForceSendZDO so existing-but-not-yet-distant ZDOs reach far peers.
    //   - Client side, every ClientCacheIntervalSeconds: rebuilds a local
    //     cache of in-range distant ZDOs filtered by the per-prefab maxMeters.
    //   - On ZNetScene.CreateDestroyObjects, the cached client list is
    //     appended to vanilla's distantSectorObjects via the
    //     FindSectorObjects postfix — vanilla then handles creation and
    //     cleanup naturally.
    //
    // Stays no-op until at least one consumer calls Register(name, dist).
    // Driver GameObject is auto-spawned on first registration; never
    // exists if nothing is registered.
    //
    // Mods register via the API; FUC ships the Harmony patches that drive
    // injection. Consumer mods don't have to author any patches themselves.
    public static class DistantRenderer
    {
        private const float ServerScanIntervalSeconds = 10f;
        private const float ClientCacheIntervalSeconds = 2f;
        private const string DriverGameObjectName = "FiresCore_DistantRendererDriver";

        private static readonly Dictionary<string, float> DistanceByPrefab
            = new Dictionary<string, float>(StringComparer.Ordinal);

        private static readonly List<ZDO> ServerScanBuffer = new List<ZDO>();
        private static readonly List<ZDO> ClientScanBuffer = new List<ZDO>();
        private static readonly List<ZDO> CachedDistantZDOs = new List<ZDO>();
        private static readonly HashSet<ZDO> DedupeSet = new HashSet<ZDO>();

        private static bool _injectingForCreateDestroy;
        private static GameObject _driverGameObject;
        // Forces ONE client-cache rebuild ASAP — initial load + after any
        // registration change. An EMPTY cache is a VALID steady state (no distant
        // pieces in range), so it must NOT trigger a rebuild on its own; doing so
        // re-scanned the entire ZDO database every CreateDestroyObjects (a per-frame
        // stall that dominated zone streaming). The 2s timer handles movement.
        private static bool _clientCacheDirty = true;

        // Amortized rebuild state. A full rebuild scans every ZDO once PER prefab,
        // so we spread it across frames (one prefab/frame) into _buildBuffer, then
        // swap its contents into CachedDistantZDOs in a single step on completion —
        // injection only ever sees a COMPLETE set, never a half-built one.
        private static readonly List<ZDO> _buildBuffer = new List<ZDO>();
        private static List<string> _scanPrefabs;
        private static int  _scanPrefabIdx;
        private static bool _scanActive;

        // ─── Status ────────────────────────────────────────────────────

        public static int RegisteredCount => DistanceByPrefab.Count;

        public static bool HasAny => DistanceByPrefab.Count > 0;

        // ─── Public registration API ───────────────────────────────────

        // Add or update a distant-render rule. maxDistanceMeters is the
        // farthest the prefab will render via this pipeline — anything
        // beyond that is filtered out of the client cache and the ZDO
        // will revert to vanilla sync gating (i.e. invisible at extreme
        // range).
        public static void Register(string prefabName, float maxDistanceMeters)
        {
            if (string.IsNullOrEmpty(prefabName)) return;
            if (maxDistanceMeters <= 0f)
            {
                Unregister(prefabName);
                return;
            }

            DistanceByPrefab[prefabName] = maxDistanceMeters;
            _clientCacheDirty = true;
            EnsureDriverSpawned();
        }

        public static bool Unregister(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            bool removed = DistanceByPrefab.Remove(prefabName);
            if (removed) { CachedDistantZDOs.Clear(); _clientCacheDirty = true; }
            return removed;
        }

        public static bool TryGetDistance(string prefabName, out float maxDistanceMeters)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                maxDistanceMeters = 0f;
                return false;
            }
            return DistanceByPrefab.TryGetValue(prefabName, out maxDistanceMeters);
        }

        public static bool IsRegistered(string prefabName)
            => !string.IsNullOrEmpty(prefabName) && DistanceByPrefab.ContainsKey(prefabName);

        public static IReadOnlyDictionary<string, float> Snapshot() => DistanceByPrefab;

        // Consumer mods call this after batch-updating registrations.
        // Drops the client cache (forces rebuild on next CreateDestroy tick)
        // and runs an immediate server scan + ForceSendZDO so existing
        // ZDOs reach far peers without waiting for the periodic timer.
        public static void Invalidate()
        {
            CachedDistantZDOs.Clear();
            _clientCacheDirty = true;
            TryServerMarkAndSendAll();
        }

        // ─── Driver lifecycle ──────────────────────────────────────────

        // Spawns the hidden GameObject that runs the server-side periodic
        // scan. Idempotent. Called automatically on first Register(); can
        // also be called explicitly by a host plugin that wants to spawn
        // the driver early (e.g. before any registrations land).
        public static void EnsureDriverSpawned()
        {
            if (_driverGameObject != null) return;

            _driverGameObject = new GameObject(DriverGameObjectName);
            UnityEngine.Object.DontDestroyOnLoad(_driverGameObject);
            _driverGameObject.hideFlags = HideFlags.HideAndDontSave;
            _driverGameObject.AddComponent<DistantRendererDriver>();
        }

        public static void Clear()
        {
            DistanceByPrefab.Clear();
            CachedDistantZDOs.Clear();
            ClientScanBuffer.Clear();
            ServerScanBuffer.Clear();
            DedupeSet.Clear();
            _injectingForCreateDestroy = false;

            if (_driverGameObject != null)
            {
                UnityEngine.Object.Destroy(_driverGameObject);
                _driverGameObject = null;
            }
        }

        // ─── Driver internals ──────────────────────────────────────────

        private static void TryServerMarkAndSendAll()
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (ZDOMan.instance == null) return;
                ServerMarkAndSendAll();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresCore.DistantRenderer] immediate server scan threw: {ex.Message}");
            }
        }

        private static void ServerMarkAndSendAll()
        {
            if (DistanceByPrefab.Count == 0) return;

            foreach (var kvp in DistanceByPrefab)
            {
                ServerScanBuffer.Clear();
                int idx = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(kvp.Key, ServerScanBuffer, ref idx)) { }

                foreach (var zdo in ServerScanBuffer)
                {
                    if (zdo == null) continue;
                    if (!zdo.Distant) zdo.SetDistant(true);
                    ZDOMan.instance.ForceSendZDO(zdo.m_uid);
                }
            }
            ServerScanBuffer.Clear();
        }

        // ─── Amortized, double-buffered client cache rebuild ──────────────
        // Begin snapshots the registered prefab list and resets the scratch
        // buffer. Step processes ONE prefab's full world scan per call and
        // distance-filters it into _buildBuffer; when the last prefab is done it
        // swaps the scratch contents into the LIVE CachedDistantZDOs atomically.
        // Injection therefore never sees a partially-built set — the hard-won
        // distant-render-without-culling behaviour is unchanged; only the build
        // cost is spread out (no per-frame, and no periodic, stall).

        private static void BeginClientScan()
        {
            _buildBuffer.Clear();
            ClientScanBuffer.Clear();
            _scanPrefabs   = new List<string>(DistanceByPrefab.Keys);  // snapshot — immune to mid-scan (un)register
            _scanPrefabIdx = 0;
            _scanActive    = _scanPrefabs.Count > 0;
            if (!_scanActive) CachedDistantZDOs.Clear();               // nothing registered → publish empty now
        }

        // Returns true once the whole rebuild finished (live cache swapped).
        private static bool StepClientScan()
        {
            if (!_scanActive) return true;
            if (ZDOMan.instance == null || ZNet.instance == null) { _scanActive = false; return true; }

            Vector3 refPos = ZNet.instance.GetReferencePosition();

            string prefab = _scanPrefabs[_scanPrefabIdx];
            float maxDistSqr = DistanceByPrefab.TryGetValue(prefab, out var d) ? d * d : 0f;

            ClientScanBuffer.Clear();
            int idx = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab, ClientScanBuffer, ref idx)) { }

            for (int i = 0; i < ClientScanBuffer.Count; i++)
            {
                var zdo = ClientScanBuffer[i];
                if (zdo == null) continue;
                if ((refPos - zdo.GetPosition()).sqrMagnitude > maxDistSqr) continue;   // DistanceSqr — no sqrt
                _buildBuffer.Add(zdo);
            }
            ClientScanBuffer.Clear();

            _scanPrefabIdx++;
            if (_scanPrefabIdx < _scanPrefabs.Count) return false;     // more prefabs next frame

            // Done — atomically replace the live cache with the freshly-built set.
            CachedDistantZDOs.Clear();
            CachedDistantZDOs.AddRange(_buildBuffer);
            _buildBuffer.Clear();
            _scanActive = false;
            return true;
        }

        // ─── Harmony hooks ─────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateDestroyObjects))]
        private static class CreateDestroyObjectsPatch
        {
            private static float _clientCacheTimer;

            public static void Prefix()
            {
                if (DistanceByPrefab.Count == 0) return;
                if (ZDOMan.instance == null || ZNet.instance == null) return;

                _clientCacheTimer += Time.deltaTime;

                // Kick a rebuild on the interval, or when flagged dirty (initial load
                // / registration change) — NEVER just because the cache is empty
                // (empty is a valid steady state; rebuilding on it scanned the whole
                // ZDO DB every frame). A rebuild already running is left to finish.
                if (!_scanActive && (_clientCacheDirty || _clientCacheTimer >= ClientCacheIntervalSeconds))
                {
                    _clientCacheDirty = false;
                    _clientCacheTimer = 0f;
                    BeginClientScan();
                }

                // Advance the amortized rebuild one prefab/frame; the live cache is
                // swapped only on completion, so injection never sees a partial set.
                if (_scanActive) StepClientScan();

                _injectingForCreateDestroy = true;
            }

            public static Exception Finalizer(Exception __exception) =>
                _injectingForCreateDestroy
                    ? DisarmAndPassThrough(__exception)
                    : __exception;

            private static Exception DisarmAndPassThrough(Exception ex)
            {
                _injectingForCreateDestroy = false;
                return ex;
            }
        }

        // Cross-version target: the upcoming Valheim build retypes the
        // first parameter of FindSectorObjects from Vector2i to Vector2s.
        // Resolve at runtime — prefer the new signature, fall back to the
        // legacy one. typeof(Vector2s) can't appear at compile time on the
        // current live build, so we look it up by name through AppDomain.
        [HarmonyPatch]
        private static class FindSectorObjectsPatch
        {
            private const BindingFlags InstanceMethodFlags =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            private const string FindSectorObjectsMethod = "FindSectorObjects";
            private const string Vector2sTypeName = "Vector2s";

            static MethodBase TargetMethod()
            {
                var zdoMan = typeof(ZDOMan);

                var vector2sType = FindTypeQuietly(Vector2sTypeName);
                if (vector2sType != null)
                {
                    var newSig = new Type[]
                    {
                        vector2sType, typeof(int), typeof(int),
                        typeof(List<ZDO>), typeof(List<ZDO>)
                    };
                    var newMethod = zdoMan.GetMethod(FindSectorObjectsMethod,
                        InstanceMethodFlags, binder: null, types: newSig, modifiers: null);
                    if (newMethod != null) return newMethod;
                }

                var oldSig = new Type[]
                {
                    typeof(Vector2i), typeof(int), typeof(int),
                    typeof(List<ZDO>), typeof(List<ZDO>)
                };
                return zdoMan.GetMethod(FindSectorObjectsMethod,
                    InstanceMethodFlags, binder: null, types: oldSig, modifiers: null);
            }

            // Quiet equivalent of AccessTools.TypeByName — no HarmonyX
            // "could not find type" warning on misses (the WHOLE point of
            // probing both signatures is that one will be missing).
            private static Type FindTypeQuietly(string fullName)
            {
                if (string.IsNullOrEmpty(fullName)) return null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(fullName, throwOnError: false);
                        if (t != null) return t;
                    }
                    catch { /* unloadable assembly — skip */ }
                }
                return null;
            }

            public static void Postfix(List<ZDO> distantSectorObjects)
            {
                if (!_injectingForCreateDestroy) return;
                if (distantSectorObjects == null) return;
                if (CachedDistantZDOs.Count == 0) return;

                var zdoMan = ZDOMan.instance;
                if (zdoMan == null) return;

                DedupeSet.Clear();
                for (int i = 0; i < distantSectorObjects.Count; i++)
                {
                    var z = distantSectorObjects[i];
                    if (z != null) DedupeSet.Add(z);
                }

                for (int i = 0; i < CachedDistantZDOs.Count; i++)
                {
                    var zdo = CachedDistantZDOs[i];
                    if (zdo == null || !zdo.IsValid()) continue;
                    // Hardened lookup: confirm the cached ZDO is STILL the
                    // active entry in ZDOMan. A 2-second cache can drift —
                    // if DestroyZDO replaced the entry between refresh and
                    // here, vanilla's CreateObjects/RemoveObjects NREs deep
                    // inside m_instances iteration. Skip stale entries.
                    if (!ReferenceEquals(zdoMan.GetZDO(zdo.m_uid), zdo)) continue;
                    if (!DedupeSet.Add(zdo)) continue;
                    distantSectorObjects.Add(zdo);
                }
                DedupeSet.Clear();
            }
        }

        // Periodic server scan + first-tick fast path. Attached to the
        // hidden GameObject EnsureDriverSpawned creates.
        private class DistantRendererDriver : MonoBehaviour
        {
            private float _serverScanTimer;
            private bool _initialScanDone;

            private void Update()
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (ZDOMan.instance == null) return;
                if (DistanceByPrefab.Count == 0) return;

                if (!_initialScanDone)
                {
                    _serverScanTimer = 0f;
                    _initialScanDone = true;
                    TryServerMarkAndSendAll();
                    return;
                }

                _serverScanTimer += Time.deltaTime;
                if (_serverScanTimer < ServerScanIntervalSeconds) return;
                _serverScanTimer = 0f;
                TryServerMarkAndSendAll();
            }
        }
    }
}
