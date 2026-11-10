using System;
using System.Reflection;
using FiresCore.Logging;

namespace FiresCore.Npc
{
    /// <summary>
    /// 0.2.1216 ([generator], Fire 11-08: FGN, EBM and FiresFixes rely on no other mod; FiresFixes 0.1.69, FixesApi v1). FiresFixes no
    /// longer looks up Core's CompanionController for its idle / sleeping creature skips. Core plugs into FiresFixes' own hook instead:
    /// FiresFixes.Api.FixesApi.RegisterCreatureSkip(owner, Func&lt;Character,bool&gt; keepFullSpeed), with the same test as FGN's far-idle skip
    /// (a Core companion always keeps full speed). Found by type name only; no reference either way. Run once from CoreLateOwners.RunOnce.
    /// Without FiresFixes, or with FixesApi below v1: one Debug line.
    /// </summary>
    internal static class FixesHooksBridge
    {
        private const string FixesApiType = "FiresFixes.Api.FixesApi";
        private const int NeededVersion = 1;
        private const string Owner = "com.Fire.FiresUnifiedCore";
        private static bool s_ran;

        private static readonly Func<Character, bool> s_keepFullSpeed = c => c != null && c.GetComponent<CompanionController>() != null;

        internal static void RunOnce()
        {
            if (s_ran) return;
            s_ran = true;
            Type api = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { api = asm.GetType(FixesApiType, false); } catch { continue; }
                if (api != null) break;
            }
            if (api == null) { FiresLogger.LogDebug("[FixesHooks] FiresFixes not installed (or without FixesApi): no creature skip to register"); return; }
            var vf = api.GetField("Version", BindingFlags.Public | BindingFlags.Static);
            int version = vf != null && vf.FieldType == typeof(int) ? (int)vf.GetValue(null) : 0;
            var m = api.GetMethod("RegisterCreatureSkip", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Func<Character, bool>) }, null);
            if (version < NeededVersion || m == null) { FiresLogger.LogDebug($"[FixesHooks] FixesApi v{version} has no RegisterCreatureSkip: nothing registered"); return; }
            bool ok;
            try { ok = (bool)m.Invoke(null, new object[] { Owner, s_keepFullSpeed }); }
            catch (Exception e) { FiresLogger.LogWarning("[FixesHooks] RegisterCreatureSkip threw: " + (e.InnerException ?? e).Message); return; }
            FiresLogger.LogInfo($"[FixesHooks] wired into FixesApi v{version}: companions never idle-skipped by FiresFixes {(ok ? "on" : "refused")}");
        }
    }
}
