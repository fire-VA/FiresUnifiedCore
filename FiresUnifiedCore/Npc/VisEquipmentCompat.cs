using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

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

        // ──────────────────────────────────────────────────────────────────
        //  ZDO-free, hash-aware slot dressing for the mannequin preview.
        //
        //  The vanilla Set*Item methods write the desired hash to the ZNetView
        //  ZDO (m_nview.GetZDO()) — they NRE when the VisEquipment has no live
        //  ZDO, which is exactly the case for the static mannequin. So instead
        //  of going through them, we write the STRING backing field
        //  (m_leftItem / m_chestItem / …) directly. With no ZDO present,
        //  VisEquipment.UpdateEquipmentVisuals() reads those string fields and
        //  hashes them itself (GetStableHashCode → ObjectDB.GetItemPrefab(hash)),
        //  so the attach result is identical to the networked path.
        //
        //  Token forms (Phase-1 capture wrote each as a token string):
        //    • prefab NAME  ("ArmorIronChest")  → set the name verbatim.
        //    • literal HASH ("-1234567")        → all-digit (optionally leading
        //      '-') token == the int stable hash. We reverse-resolve it to the
        //      prefab name via ObjectDB so the string field hashes back to the
        //      SAME value. If ObjectDB can't resolve it (not loaded / unknown
        //      hash) the slot is left empty rather than mis-hashing a numeric
        //      string into garbage.
        // ──────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, FieldInfo> _itemFieldCache =
            new Dictionary<string, FieldInfo>();

        private static FieldInfo ItemField(string fieldName)
        {
            lock (_lock)
            {
                if (_itemFieldCache.TryGetValue(fieldName, out var cached)) return cached;
                var f = AccessTools.Field(typeof(VisEquipment), fieldName);
                if (f != null && f.FieldType != typeof(string)) f = null;
                _itemFieldCache[fieldName] = f;
                return f;
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
            var f = ItemField(fieldName);
            if (f == null) return false;
            try
            {
                f.SetValue(vis, ResolveTokenToName(token) ?? "");
                return true;
            }
            catch { return false; }
        }
    }
}
