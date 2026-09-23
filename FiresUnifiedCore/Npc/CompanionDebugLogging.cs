using System;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// The companion engine keeps its diagnostics behind about eighty `public static bool VerboseLogging` fields, one
    /// per class, and nothing ever assigned them, so every one of those log lines was unreachable. This drives them
    /// all from the two Debug config switches, by reflection so a new class is covered the day it is written.
    /// Job and idle classes answer to the separate jobs switch because they log far more.
    /// </summary>
    internal static class CompanionDebugLogging
    {
        private const string NpcNamespace = "FiresCore.Npc";
        private const string JobNamespace = "FiresCore.Npc.IdleBehaviors";
        private const string InteractionNamespace = "FiresCore.Npc.Interactions";
        private const string VerboseField = "VerboseLogging";
        private const string StateTransitionField = "StateTransitionLogging";

        internal static void Apply(bool companions, bool jobs)
        {
            Type[] types;
            try { types = typeof(CompanionDebugLogging).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionDebugLogging] could not read the assembly's types: {ex.Message}");
                return;
            }

            int set = 0;
            foreach (var type in types)
            {
                if (type?.Namespace == null || !type.Namespace.StartsWith(NpcNamespace, StringComparison.Ordinal)) continue;
                bool value = IsJobClass(type) ? jobs : companions;
                if (SetFlag(type, VerboseField, value)) set++;
                if (SetFlag(type, StateTransitionField, value)) set++;
            }

            Debug.Log($"[CompanionDebugLogging] companion logging {(companions ? "on" : "off")}, job logging {(jobs ? "on" : "off")} ({set} switches).");
        }

        private static bool IsJobClass(Type type) =>
            type.Namespace.StartsWith(JobNamespace, StringComparison.Ordinal)
            || type.Namespace.StartsWith(InteractionNamespace, StringComparison.Ordinal)
            || type == typeof(CompanionIdleBehavior);

        private static bool SetFlag(Type type, string name, bool value)
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (field == null || field.FieldType != typeof(bool) || field.IsInitOnly || field.IsLiteral) return false;
            try
            {
                field.SetValue(null, value);
                return true;
            }
            catch { return false; }
        }
    }
}
