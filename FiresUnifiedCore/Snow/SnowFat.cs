using System;
using System.Reflection;

namespace FiresCore.Snow
{
    /// <summary>FAT-owned zone test by reflection (FAT sits above Core): FatApi.IsFatZone(Vector2s) when present, else
    /// VoxelZoneStreamer.IsZoneKnown(Vector2s). Re-asked on every snow step, since a zone can turn voxel after its Heightmap built.</summary>
    internal static class SnowFat
    {
        private static bool s_looked;
        private static Func<Vector2s, bool> s_owns;

        internal static bool OwnsZone(Vector2s zone)
        {
            if (!s_looked) Look();
            if (s_owns == null) return false;
            try { return s_owns(zone); } catch { return false; }
        }

        private static void Look()
        {
            s_looked = true;
            MethodInfo m = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = asm.GetName().Name;
                if (n == null || n.IndexOf("FiresAdminTerrain", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var t in SafeTypes(asm))
                {
                    if (t.Name == "FatApi") m = t.GetMethod("IsFatZone", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector2s) }, null) ?? m;
                    if (m == null && t.Name == "VoxelZoneStreamer") m = t.GetMethod("IsZoneKnown", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector2s) }, null);
                }
            }
            if (m != null) s_owns = (Func<Vector2s, bool>)Delegate.CreateDelegate(typeof(Func<Vector2s, bool>), m);
            FiresCore.Logging.FiresLogger.LogDebug($"[Snow] FAT zone test: {(m != null ? m.DeclaringType?.Name + "." + m.Name : "none (no FAT: every zone is vanilla terrain)")}");
        }

        private static Type[] SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return Array.FindAll(e.Types, t => t != null); } catch { return Type.EmptyTypes; }
        }
    }
}
