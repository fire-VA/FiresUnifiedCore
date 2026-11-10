using System;
using System.Reflection;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// Halo ring OBJECT-sector keys (Tools\PLAN_halo_zone_keys_20261024.md, [fgn]'s design change): terrain zones stay vanilla x,z
    /// (ZoneSystem.GetZone / GetZonePos / IsZoneLoaded are NOT patched), but ZDO sectors are ring-keyed on a ring world
    /// (GetSectorIndex patched, SectorToIndex wraps the seam). So Core code that asks ZDOMan.FindSectorObjects / FindObjects for the
    /// objects around a position must pass <see cref="ObjectSector"/>, not ZoneSystem.GetZone, and hand-made sector deltas use
    /// <see cref="Delta"/> / <see cref="Within"/>. FAT's FatApi (VerdantsAscent.FatApi: HaloZonesOn, HaloZoneDelta, HaloZone,
    /// HaloZonePos) is reached by reflection, re-checked at most every 2 s. Without FAT, or with ring keys off, every answer is the plain
    /// vanilla one (GetZone, b - a, the id itself).
    /// </summary>
    public static class RingZones
    {
        private static Func<bool> s_on;
        private static Func<Vector2s, Vector2s, Vector2s> s_delta;
        private static Func<Vector3, Vector2s> s_objectSector;   // FatApi.HaloZone: the ring object-sector id (vanilla GetZone off the ring)
        private static Func<Vector2s, Vector2s> s_zone;   // id -> HaloZone(HaloZonePos(id)): the normalised id

        /// <summary>The object-sector id at <paramref name="pos"/> for ZDOMan's sector queries: the ring key on a ring world, ZoneSystem.GetZone otherwise.</summary>
        public static Vector2s ObjectSector(Vector3 pos)
        {
            if (On && s_objectSector != null)
                try { return s_objectSector(pos); } catch { }
            return ZoneSystem.GetZone(pos);
        }
        private static float s_nextCheck = -1f;
        private static bool s_bound, s_logged;

        /// <summary>Ring zone keys are active in this world.</summary>
        public static bool On
        {
            get
            {
                Bind();
                if (s_on == null) return false;
                try { return s_on(); } catch { return false; }
            }
        }

        /// <summary>b - a with the ring seam wrapped; plain b - a off the ring.</summary>
        public static Vector2s Delta(Vector2s a, Vector2s b)
        {
            if (On && s_delta != null)
                try { return s_delta(a, b); } catch { }
            return new Vector2s(b.x - a.x, b.y - a.y);
        }

        /// <summary>The same zone with its ring coordinate wrapped back into range (an id from a centre +- n loop); the id itself off the ring.</summary>
        public static Vector2s Normalize(Vector2s id)
        {
            if (On && s_zone != null)
                try { return s_zone(id); } catch { }
            return id;
        }

        /// <summary>|dx| &lt;= area and |dy| &lt;= area, seam-wrapped on the ring.</summary>
        public static bool Within(Vector2s a, Vector2s b, int area)
        {
            Vector2s d = Delta(a, b);
            return Math.Abs(d.x) <= area && Math.Abs(d.y) <= area;
        }

        private static void Bind()
        {
            if (s_bound || Time.realtimeSinceStartup < s_nextCheck) return;
            s_nextCheck = Time.realtimeSinceStartup + 2f;
            Type t = Type.GetType("VerdantsAscent.FatApi, FiresAdminTerrain", false);
            if (t == null) return;
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static;
            PropertyInfo on = t.GetProperty("HaloZonesOn", F);
            FieldInfo onField = on == null ? t.GetField("HaloZonesOn", F) : null;
            MethodInfo delta = t.GetMethod("HaloZoneDelta", F, null, new[] { typeof(Vector2s), typeof(Vector2s) }, null);
            MethodInfo zone = t.GetMethod("HaloZone", F, null, new[] { typeof(Vector3) }, null);
            MethodInfo zonePos = t.GetMethod("HaloZonePos", F, null, new[] { typeof(Vector2s) }, null);
            if ((on == null && onField == null) || delta == null || zone == null || zonePos == null) return;   // FAT without ring zone keys: vanilla answers
            // a getter delegate (WearGate asks per piece update; no reflection per call)
            s_on = on != null ? (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), on.GetGetMethod()) : () => (bool)onField.GetValue(null);
            s_delta = (Func<Vector2s, Vector2s, Vector2s>)Delegate.CreateDelegate(typeof(Func<Vector2s, Vector2s, Vector2s>), delta);
            var toZone = (Func<Vector3, Vector2s>)Delegate.CreateDelegate(typeof(Func<Vector3, Vector2s>), zone);
            var toPos = (Func<Vector2s, Vector3>)Delegate.CreateDelegate(typeof(Func<Vector2s, Vector3>), zonePos);
            s_zone = id => toZone(toPos(id));
            s_objectSector = toZone;
            s_bound = true;
            if (!s_logged) { s_logged = true; FiresCore.Logging.FiresLogger.LogDebug("[RingZones] FatApi halo zone keys bound (HaloZonesOn / HaloZoneDelta / HaloZone / HaloZonePos); Core's object-sector queries and deltas use the ring keys"); }
        }
    }
}
