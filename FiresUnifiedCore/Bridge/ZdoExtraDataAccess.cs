using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Enumerates every typed value on a ZDO. Valheim 1.0 removed ZDOExtraData's bulk getters and kept only per-key
    /// lookups, but the data still sits in seven private static maps whose BinarySearchDictionary values are
    /// enumerable, so each map is reached by one cached reflection hop. The maps are never reassigned, so the
    /// cached reference stays valid, and a failed lookup degrades to no data.
    /// </summary>
    public static class ZdoExtraDataAccess
    {
        private static class Maps<T>
        {
            internal static Dictionary<ZDOID, BinarySearchDictionary<int, T>> Value;
            internal static bool Resolved;
        }

        private static bool _warned;

        private static List<KeyValuePair<int, T>> Get<T>(ZDOID zid, string fieldName)
        {
            if (!Maps<T>.Resolved)
            {
                Maps<T>.Resolved = true;
                try
                {
                    Maps<T>.Value = AccessTools.StaticFieldRefAccess<Dictionary<ZDOID, BinarySearchDictionary<int, T>>>(
                        typeof(ZDOExtraData), fieldName);
                }
                catch (Exception ex)
                {
                    Maps<T>.Value = null;
                    if (!_warned)
                    {
                        _warned = true;
                        Debug.LogWarning($"[ZdoExtraDataAccess] ZDOExtraData.{fieldName} did not resolve " +
                                         $"({ex.GetType().Name}: {ex.Message}). Bulk ZDO data capture is " +
                                         $"unavailable this session - a game patch has probably reshaped it again.");
                    }
                }
            }

            var map = Maps<T>.Value;
            BinarySearchDictionary<int, T> entries;
            if (map == null || !map.TryGetValue(zid, out entries) || entries == null || entries.Count == 0)
                return null;

            var result = new List<KeyValuePair<int, T>>(entries.Count);
            foreach (var pair in entries) result.Add(pair);
            return result;
        }

        /// <summary>Every float on this ZDO, or null when it has none / the field is unreachable.</summary>
        public static List<KeyValuePair<int, float>> GetFloats(ZDOID zid) => Get<float>(zid, "s_floats");

        public static List<KeyValuePair<int, Vector3>> GetVec3s(ZDOID zid) => Get<Vector3>(zid, "s_vec3");

        public static List<KeyValuePair<int, Quaternion>> GetQuaternions(ZDOID zid) => Get<Quaternion>(zid, "s_quats");

        public static List<KeyValuePair<int, int>> GetInts(ZDOID zid) => Get<int>(zid, "s_ints");

        public static List<KeyValuePair<int, long>> GetLongs(ZDOID zid) => Get<long>(zid, "s_longs");

        public static List<KeyValuePair<int, string>> GetStrings(ZDOID zid) => Get<string>(zid, "s_strings");

        public static List<KeyValuePair<int, byte[]>> GetByteArrays(ZDOID zid) => Get<byte[]>(zid, "s_byteArrays");
    }
}
