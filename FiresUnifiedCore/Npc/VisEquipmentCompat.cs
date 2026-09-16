using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace FiresCore.Npc
{
    /// <summary>
    /// Calls VisEquipment's Set*Item slot setters through cached reflection, preferring the int item-hash
    /// overloads Valheim 1.0 uses and falling back to the older string ones, so one DLL runs on either. Strings
    /// become stable hashes for the int form, and an empty name maps to 0 to clear the slot.
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
                var type = typeof(VisEquipment);
                // Prefer the new (int …) overload — that's what the upcoming
                // build exposes, and the patcher does not shim these.
                var method = type.GetMethod(methodName, newSig)
                        ?? type.GetMethod(methodName, oldSig);
                _cache[key] = method;
                return method;
            }
        }

        private static void DispatchSimple(VisEquipment vis, string methodName, string name)
        {
            if (vis == null) return;
            var method = Resolve(methodName,
                newSig: new[] { typeof(int) },
                oldSig: new[] { typeof(string) });
            if (method == null) return;
            object arg = method.GetParameters()[0].ParameterType == typeof(int)
                ? (object)Hash(name)
                : (object)(name ?? "");
            method.Invoke(vis, new[] { arg });
        }

        private static void DispatchWithVariant(VisEquipment vis, string methodName, string name, int variant)
        {
            if (vis == null) return;
            var method = Resolve(methodName,
                newSig: new[] { typeof(int), typeof(int) },
                oldSig: new[] { typeof(string), typeof(int) });
            if (method == null) return;
            object arg0 = method.GetParameters()[0].ParameterType == typeof(int)
                ? (object)Hash(name)
                : (object)(name ?? "");
            method.Invoke(vis, new object[] { arg0, variant });
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
        /// changed string→int across builds) from one VisEquipment to another.
        /// Reads the field reflectively, then invokes the Set*Item overload
        /// whose first parameter matches the field's runtime type. Same-build
        /// guarantees src.field and dst.Set*Item agree, so no conversion is
        /// performed — the value passes through verbatim.
        /// </summary>
        public static void CopyTypeShiftedField(
            VisEquipment src,
            VisEquipment dst,
            string fieldName,
            string setMethodName)
        {
            if (src == null || dst == null) return;
            var field = typeof(VisEquipment).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null) return;
            object value = field.GetValue(src);

            var method = typeof(VisEquipment).GetMethod(setMethodName, new[] { field.FieldType });
            if (method == null) return;
            method.Invoke(dst, new[] { value });
        }

        public static void CopyHairItem(VisEquipment src, VisEquipment dst)
            => CopyTypeShiftedField(src, dst, "m_hairItem", "SetHairItem");

        public static void CopyBeardItem(VisEquipment src, VisEquipment dst)
            => CopyTypeShiftedField(src, dst, "m_beardItem", "SetBeardItem");

        // Dresses mannequin slots without a ZDO. Vanilla's Set*Item methods write through the ZDO and NRE without
        // one, so the string backing fields are set directly and UpdateEquipmentVisuals hashes them itself. A
        // token is either a prefab name, used as is, or an all-digit stable hash, which is resolved back to its
        // prefab name through ObjectDB; an unresolvable hash leaves the slot empty.

        private static readonly Dictionary<string, FieldInfo> _itemFieldCache =
            new Dictionary<string, FieldInfo>();

        private static FieldInfo ItemField(string fieldName)
        {
            lock (_lock)
            {
                if (_itemFieldCache.TryGetValue(fieldName, out var cached)) return cached;
                var field = AccessTools.Field(typeof(VisEquipment), fieldName);
                if (field != null && field.FieldType != typeof(string)) field = null;
                _itemFieldCache[fieldName] = field;
                return field;
            }
        }

        private static bool IsAllDigitHashToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            int start = (token[0] == '-') ? 1 : 0;
            if (start >= token.Length) return false;
            for (int i = start; i < token.Length; i++)
                if (token[i] < '0' || token[i] > '9') return false;
            return true;
        }

        /// <summary>
        /// Resolves a capture token to the prefab NAME the body of
        /// VisEquipment will accept. Prefab-name tokens pass through. All-digit
        /// (hash) tokens are reverse-resolved through ObjectDB; if that fails
        /// the slot resolves to empty. Never throws.
        /// </summary>
        public static string ResolveTokenToName(string token)
        {
            if (string.IsNullOrEmpty(token)) return "";
            if (!IsAllDigitHashToken(token)) return token;

            try
            {
                if (!int.TryParse(token, out int hash)) return "";
                if (hash == 0) return "";
                var odb = ObjectDB.instance;
                if (odb == null) return "";
                var prefab = odb.GetItemPrefab(hash);
                return prefab != null ? prefab.name : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// Writes a slot's STRING backing field directly (ZDO-free). The token
        /// is resolved name-vs-hash first. Returns false only when the field
        /// couldn't be found on this build (degrades to an empty slot).
        /// </summary>
        public static bool SetItemFieldDirect(VisEquipment vis, string fieldName, string token)
        {
            if (vis == null) return false;
            var field = ItemField(fieldName);
            if (field == null) return false;
            try
            {
                field.SetValue(vis, ResolveTokenToName(token) ?? "");
                return true;
            }
            catch { return false; }
        }
    }
}
