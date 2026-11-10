using System;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Compat
{
    /// <summary>
    /// 0.2.1218 ([generator], Tools\PLAN_CoreToEbmApi_20261108.md section B; EBM 1.2.208): Core reads EBM's stand-ins through EBM's PUBLIC
    /// API instead of its private classes. FiresEasyBakeMeshes.EbmApi.StandInPrefab(Collider) gives the stood-in piece's prefab hash (0 =
    /// not a stand-in), and the event StandInsChanged(Vector2i zone, int left) fires once per touched zone after stand-ins are built or
    /// removed. Found by type name only. With an EBM older than 1.2.208 (no StandInsChanged), Supported is false and the old name-based
    /// hooks (SnowShelterEbm) and checks stay in use.
    /// </summary>
    internal static class EbmStandIns
    {
        private const string EbmApiType = "FiresEasyBakeMeshes.EbmApi";
        private static bool s_looked;
        private static Func<Collider, int> s_prefabOf;
        private static EventInfo s_changed;

        /// <summary>True when EBM's public stand-in API is there (EBM 1.2.208+).</summary>
        internal static bool Supported { get { Look(); return s_prefabOf != null && s_changed != null; } }

        /// <summary>The prefab hash of the piece an EBM stand-in collider stands for, 0 if it is not one (or EBM is older / absent).</summary>
        internal static int PrefabOf(Collider c)
        {
            Look();
            if (s_prefabOf == null || c == null) return 0;
            try { return s_prefabOf(c); } catch { return 0; }
        }

        /// <summary>Subscribe to EbmApi.StandInsChanged; false when EBM has no such event.</summary>
        internal static bool Subscribe(Action<Vector2i, int> onChanged)
        {
            Look();
            if (s_changed == null || onChanged == null) return false;
            try { s_changed.AddEventHandler(null, onChanged); return true; }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[EbmStandIns] subscribe failed: " + e.Message); return false; }
        }

        private static void Look()
        {
            if (s_looked) return;
            Type api = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { api = asm.GetType(EbmApiType, false); } catch { continue; }
                if (api != null) break;
            }
            if (api == null) return;   // not loaded (yet): looked up again on the next call
            s_looked = true;
            var m = api.GetMethod("StandInPrefab", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Collider) }, null);
            if (m != null && m.ReturnType == typeof(int)) s_prefabOf = (Func<Collider, int>)Delegate.CreateDelegate(typeof(Func<Collider, int>), m);
            var ev = api.GetEvent("StandInsChanged", BindingFlags.Public | BindingFlags.Static);
            if (ev != null && ev.EventHandlerType == typeof(Action<Vector2i, int>)) s_changed = ev;
        }
    }
}
