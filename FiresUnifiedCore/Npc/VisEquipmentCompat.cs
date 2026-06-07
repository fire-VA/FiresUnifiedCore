using System;
using System.Collections.Generic;
using System.Reflection;

namespace FiresCore.Npc
{
    /// <summary>
    /// Backwards-compatible dispatcher for VisEquipment.Set*Item methods.
    ///
    /// In the upcoming Valheim build the visual-equipment slot setters were
    /// retyped from <c>(string name, â€¦)</c> to <c>(int itemHash, â€¦)</c>.
    /// Calling <c>SetHelmetItem(string)</c> directly throws
    /// <c>MissingMethodException</c> on the new build, while calling
    /// <c>SetHelmetItem(int)</c> directly fails to compile against the current
    /// live build.
    ///
    /// Each helper here resolves the real <see cref="MethodInfo"/> on first use
    /// (preferring the int overload, falling back to string) and caches it.
    /// String inputs are converted to <c>GetStableHashCode()</c> when the int
    /// overload is in use; empty/null strings map to 0 to preserve the old
    /// "clear slot" semantics.
    ///
    /// Same DLL ships across both builds.
    /// </summary>
    internal static class VisEquipmentCompat
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, MethodInfo> _cache =
            new Dictionary<string, MethodInfo>();

        private static int Hash(string name) =>
            string.IsNullOrEmpty(name) ? 0 : name.GetStableHashCode();

        private static MethodInfo Resolve(string methodName, Type[] newSig, Type[] oldSig)
        {
            // Cache key disambiguates simple vs with-variant overloads.
            string key = methodName + "|" + newSig.Length;
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached)) return cached;
                var t = typeof(VisEquipment);
                // Prefer the new (int â€¦) overload â€” that's what the upcoming
                // build exposes, and the patcher does not shim these.
                var m = t.GetMethod(methodName, newSig)
                        ?? t.GetMethod(methodName, oldSig);
                _cache[key] = m;
                return m;
            }
        }

        private static void DispatchSimple(VisEquipment vis, string methodName, string name)
        {
            if (vis == null) return;
            var m = Resolve(methodName,
                newSig: new[] { typeof(int) },
                oldSig: new[] { typeof(string) });
            if (m == null) return;
            object arg = m.GetParameters()[0].ParameterType == typeof(int)
                ? (object)Hash(name)
                : (object)(name ?? "");
            m.Invoke(vis, new[] { arg });
        }

        private static void DispatchWithVariant(VisEquipment vis, string methodName, string name, int variant)
        {
            if (vis == null) return;
            var m = Resolve(methodName,
                newSig: new[] { typeof(int), typeof(int) },
                oldSig: new[] { typeof(string), typeof(int) });
            if (m == null) return;
            object arg0 = m.GetParameters()[0].ParameterType == typeof(int)
                ? (object)Hash(name)
                : (object)(name ?? "");
            m.Invoke(vis, new object[] { arg0, variant });
        }

        // Single-arg slot setters
        public static void SetHelmetItem(VisEquipment v, string name)      => DispatchSimple(v, "SetHelmetItem", name);
        public static void SetChestItem(VisEquipment v, string name)       => DispatchSimple(v, "SetChestItem", name);
        public static void SetLegItem(VisEquipment v, string name)         => DispatchSimple(v, "SetLegItem", name);
        public static void SetUtilityItem(VisEquipment v, string name)     => DispatchSimple(v, "SetUtilityItem", name);
        public static void SetRightItem(VisEquipment v, string name)       => DispatchSimple(v, "SetRightItem", name);
        public static void SetRightItemVisual(VisEquipment v, string name) => DispatchSimple(v, "SetRightItemVisual", name);
        public static void SetRightBackItem(VisEquipment v, string name)   => DispatchSimple(v, "SetRightBackItem", name);
        public static void SetBeardItem(VisEquipment v, string name)       => DispatchSimple(v, "SetBeardItem", name);
        public static void SetHairItem(VisEquipment v, string name)        => DispatchSimple(v, "SetHairItem", name);

        // Two-arg slot setters (variant)
        public static void SetShoulderItem(VisEquipment v, string name, int variant) => DispatchWithVariant(v, "SetShoulderItem", name, variant);
        public static void SetLeftItem(VisEquipment v, string name, int variant)     => DispatchWithVariant(v, "SetLeftItem", name, variant);
        public static void SetLeftBackItem(VisEquipment v, string name, int variant) => DispatchWithVariant(v, "SetLeftBackItem", name, variant);

        /// <summary>
        /// Copies a "type-shifted" identifier field (whose backing-field type
        /// changed stringâ†’int across builds) from one VisEquipment to another.
        /// Reads the field reflectively, then invokes the Set*Item overload
        /// whose first parameter matches the field's runtime type. Same-build
        /// guarantees src.field and dst.Set*Item agree, so no conversion is
        /// performed â€” the value passes through verbatim.
        /// </summary>
        public static void CopyTypeShiftedField(
            VisEquipment src,
            VisEquipment dst,
            string fieldName,
            string setMethodName)
        {
            if (src == null || dst == null) return;
            var f = typeof(VisEquipment).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) return;
            object value = f.GetValue(src);

            var m = typeof(VisEquipment).GetMethod(setMethodName, new[] { f.FieldType });
            if (m == null) return;
            m.Invoke(dst, new[] { value });
        }

        public static void CopyHairItem(VisEquipment src, VisEquipment dst)
            => CopyTypeShiftedField(src, dst, "m_hairItem", "SetHairItem");

        public static void CopyBeardItem(VisEquipment src, VisEquipment dst)
            => CopyTypeShiftedField(src, dst, "m_beardItem", "SetBeardItem");
    }
}
