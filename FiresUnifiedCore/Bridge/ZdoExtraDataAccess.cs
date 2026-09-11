using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// The one place the family enumerates ALL typed data on a ZDO.
    ///
    /// Valheim 1.0 deleted <c>ZDOExtraData.GetFloats/GetVec3s/GetQuaternions/GetInts/GetLongs/GetStrings/
    /// GetByteArrays(ZDOID)</c> - the bulk accessors that handed back every key/value pair a ZDO carried.
    /// What survives is per-key lookup (<c>GetFloat(zid, hash, out value)</c>), which is fine when you
    /// already know the key and useless when the whole point is "capture everything this object has"
    /// (blueprint/section save, ZDO inspectors, migration tools).
    ///
    /// The data is still there, in seven private static
    /// <c>Dictionary&lt;ZDOID, BinarySearchDictionary&lt;int, T&gt;&gt;</c> maps. BinarySearchDictionary is
    /// public and implements <c>IEnumerable&lt;KeyValuePair&lt;int, T&gt;&gt;</c>, so once the map itself is
    /// reachable the enumeration is ordinary. Hence: one reflection hop per type, cached, wrapped.
    ///
    /// AccessTools.StaticFieldRefAccess THROWS on every failure mode rather than returning null, and the
    /// maps are readonly and only ever Clear()ed (never reassigned), so caching the resolved reference is
    /// safe and a miss degrades to "no data" instead of an exception storm.
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
