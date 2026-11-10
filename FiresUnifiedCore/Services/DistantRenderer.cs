using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Services
{
    // 0.2.1217 ([generator], Fire 11-08: perf only in FGN / EBM / FiresFixes; FGN 1.6.64 FgnApi v9 owns distant rendering):
    // the public API other mods compile against (FiresAdminPrefabs' DistantPieceRenderer) is unchanged, but Core no longer does the
    // work. The rules are kept here (TryGetDistance / IsRegistered / Snapshot answer as before, and Core's FarBuildingDraw reads them),
    // and every change is forwarded to FGN's own FgnApi.RegisterDistantPrefab / UnregisterDistantPrefab / RefreshDistantPrefabs, found
    // by type name (no reference either way; FGN never reads Core). Without FGN 1.6.64+ nothing renders past the active area (one Info
    // line says so). Registrations made before FGN's assembly loads are flushed once from CoreLateOwners.RunOnce (FlushToFgn).
    // The old server scan, client cache, FindSectorObjects / CreateDestroyObjects patches and driver are gone; FGN runs its own.
    public static class DistantRenderer
    {
        private const string FgnApiType = "FiresGhettoNetworkMod.Api.FgnApi";
        private const int NeededVersion = 9;

        private static readonly Dictionary<string, float> DistanceByPrefab = new Dictionary<string, float>(StringComparer.Ordinal);

        private static Action<string, float> s_register;
        private static Action<string> s_unregister;
        private static Action s_refresh;
        private static bool s_bound, s_saidMissing;

        // ─── Status ────────────────────────────────────────────────────
        public static int RegisteredCount => DistanceByPrefab.Count;
        public static bool HasAny => DistanceByPrefab.Count > 0;

        // ─── Public registration API (unchanged shape) ─────────────────
        public static void Register(string prefabName, float maxDistanceMeters)
        {
            if (string.IsNullOrEmpty(prefabName)) return;
            if (maxDistanceMeters <= 0f) { Unregister(prefabName); return; }
            DistanceByPrefab[prefabName] = maxDistanceMeters;
            if (Bind()) Call(() => s_register(prefabName, maxDistanceMeters));
        }

        public static bool Unregister(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            bool removed = DistanceByPrefab.Remove(prefabName);
            if (removed && Bind()) Call(() => s_unregister(prefabName));
            return removed;
        }

        public static bool TryGetDistance(string prefabName, out float maxDistanceMeters)
        {
            if (string.IsNullOrEmpty(prefabName)) { maxDistanceMeters = 0f; return false; }
            return DistanceByPrefab.TryGetValue(prefabName, out maxDistanceMeters);
        }

        public static bool IsRegistered(string prefabName) => !string.IsNullOrEmpty(prefabName) && DistanceByPrefab.ContainsKey(prefabName);

        public static IReadOnlyDictionary<string, float> Snapshot() => DistanceByPrefab;

        // Consumer mods call this after batch-updating registrations: FGN re-marks and re-sends at once.
        public static void Invalidate()
        {
            if (Bind()) Call(() => s_refresh());
        }

        // Kept for callers: FGN runs its own driver, so there is nothing to spawn here.
        public static void EnsureDriverSpawned() { }

        public static void Clear()
        {
            if (Bind()) foreach (var name in DistanceByPrefab.Keys) { var n = name; Call(() => s_unregister(n)); }
            DistanceByPrefab.Clear();
        }

        /// <summary>Once from CoreLateOwners.RunOnce (every plugin's Awake has run): push what was registered before FGN's API could be found.</summary>
        internal static void FlushToFgn()
        {
            if (!Bind()) return;
            foreach (var kv in DistanceByPrefab) { var n = kv.Key; var d = kv.Value; Call(() => s_register(n, d)); }
            if (DistanceByPrefab.Count > 0) Call(() => s_refresh());
            FiresCore.Logging.FiresLogger.LogInfo($"[DistantRenderer] {DistanceByPrefab.Count} distant prefab rule(s) handed to FGN (FgnApi.RegisterDistantPrefab); FGN does the distant rendering");
        }

        // ─── FGN binding (by type name, until found) ───────────────────
        private static bool Bind()
        {
            if (s_bound) return true;
            Type api = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { api = asm.GetType(FgnApiType, false); } catch { continue; }
                if (api != null) break;
            }
            if (api == null) return false;   // FGN not loaded (yet): kept here, flushed by FlushToFgn
            var vf = api.GetField("Version", BindingFlags.Public | BindingFlags.Static);
            int version = vf != null && vf.FieldType == typeof(int) ? (int)vf.GetValue(null) : 0;
            var reg = api.GetMethod("RegisterDistantPrefab", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(float) }, null);
            var unreg = api.GetMethod("UnregisterDistantPrefab", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            var refresh = api.GetMethod("RefreshDistantPrefabs", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (version < NeededVersion || reg == null || unreg == null || refresh == null)
            {
                if (!s_saidMissing) { s_saidMissing = true; FiresCore.Logging.FiresLogger.LogInfo($"[DistantRenderer] FGN API v{version} has no distant-prefab hooks (needs v{NeededVersion}, FGN 1.6.64+): registered prefabs are not drawn past the active area"); }
                return false;
            }
            s_register = (n, d) => { reg.Invoke(null, new object[] { n, d }); };
            s_unregister = n => { unreg.Invoke(null, new object[] { n }); };
            s_refresh = () => { refresh.Invoke(null, null); };
            s_bound = true;
            return true;
        }

        private static void Call(Action a)
        {
            try { a(); }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogWarning("[DistantRenderer] FGN distant-prefab call threw: " + (e.InnerException ?? e).Message); }
        }

        // Lets FgnHooksBridge-style callers know the missing case once at RunOnce.
        internal static void SayIfNoFgn()
        {
            if (s_bound || s_saidMissing || DistanceByPrefab.Count == 0) return;
            if (Bind()) return;
            s_saidMissing = true;
            FiresCore.Logging.FiresLogger.LogInfo($"[DistantRenderer] FGN not installed: {DistanceByPrefab.Count} distant prefab rule(s) kept, but nothing is drawn past the active area (FGN does distant rendering)");
        }
    }
}
