using System;
using System.Collections.Generic;
using System.Reflection;
using FiresCore.Logging;

namespace FiresCore.Compat
{
    /// <summary>
    /// 0.2.1085 ([lead], Fire's minimal profile: HarmonyX "AccessTools.TypeByName: Could not find type named FiresEasyBakeMeshes..." warnings
    /// on client and dedi when EBM is not installed): SILENT lookups of a type / member in an OPTIONAL mod. AccessTools.TypeByName and
    /// AccessTools.Method("Type:Member") log a HarmonyX warning when the mod is absent; these scan the loaded assemblies with
    /// Assembly.GetType(name, false) instead. A hit is cached; a miss is not (the mod may load later), and is said once at Debug.
    /// </summary>
    public static class OptionalTypes
    {
        private static readonly Dictionary<string, Type> s_hits = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly HashSet<string> s_saidMiss = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The type by its full name from any loaded assembly, or null (no warning) when its mod is not installed.</summary>
        public static Type Find(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            lock (s_hits) { if (s_hits.TryGetValue(fullName, out Type had)) return had; }
            Type found = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { found = a.GetType(fullName, false); } catch { found = null; }
                if (found != null) break;
            }
            lock (s_hits)
            {
                if (found != null) s_hits[fullName] = found;
                else if (s_saidMiss.Add(fullName)) FiresLogger.LogDebug($"[OptionalTypes] {fullName} not loaded (its mod is not installed): that integration is skipped");
            }
            return found;
        }

        /// <summary>"Full.Type.Name:Member" like AccessTools.Method, silent when the type is absent; null when either is missing.</summary>
        public static MethodInfo Method(string typeColonMember)
        {
            if (string.IsNullOrEmpty(typeColonMember)) return null;
            int c = typeColonMember.LastIndexOf(':');
            if (c <= 0 || c == typeColonMember.Length - 1) return null;
            Type t = Find(typeColonMember.Substring(0, c));
            return t == null ? null : HarmonyLib.AccessTools.Method(t, typeColonMember.Substring(c + 1));
        }
    }
}
