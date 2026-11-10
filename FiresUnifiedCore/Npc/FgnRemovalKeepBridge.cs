using System;
using HarmonyLib;

namespace FiresCore.Npc
{
    /// <summary>
    /// 0.2.1214 ([generator], Fire 11-08: "fgn better not rely on core for anything"). FGN never reads Core. Instead, Core plugs its
    /// companion keep into FGN's own hook when FGN is installed: FiresGhettoNetworkMod.Api.FgnApi.RegisterServerRemovalKeep(owner,
    /// Func&lt;int&gt; refresh, Func&lt;ZDO,bool&gt; isKept), FgnApi.Version &gt;= 7 (FGN 1.6.62). Found by type name only, so neither mod has a
    /// reference to the other. The callbacks are CompanionKeep.Refresh / IsKept (0.2.1213), built by the same CollectKept the
    /// RemoveObjects prefix uses, so FGN's sliced removal keeps exactly what the prefix keeps. Owner = Core's Harmony id, which tells
    /// FGN this prefix is covered. Registered on every ZNet.Awake (a new world; registering again replaces). Without FGN: nothing happens.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "Awake")]
    internal static class FgnRemovalKeepBridge
    {
        private const string FgnApiType = "FiresGhettoNetworkMod.Api.FgnApi";
        private const int NeededVersion = 7;
        private static bool s_saidMissing, s_saidOld;

        private static readonly Func<int> s_refresh = CompanionKeep.Refresh;
        private static readonly Func<ZDO, bool> s_isKept = CompanionKeep.IsKept;

        [HarmonyPostfix]
        private static void Postfix()
        {
            try { Register(); }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogWarning("[CompanionKeep] could not register with FGN's removal keep: " + e.Message); }
        }

        private static void Register()
        {
            var api = FindType(FgnApiType);
            if (api == null)
            {
                if (!s_saidMissing) { s_saidMissing = true; FiresCore.Logging.FiresLogger.LogDebug("[CompanionKeep] FGN not installed: nothing to register (the RemoveObjects prefix keeps companions as before)"); }
                return;
            }
            var versionField = api.GetField("Version");
            int version = versionField != null ? (int)versionField.GetValue(null) : 0;
            var register = api.GetMethod("RegisterServerRemovalKeep", new[] { typeof(string), typeof(Func<int>), typeof(Func<ZDO, bool>) });
            if (version < NeededVersion || register == null)
            {
                if (!s_saidOld) { s_saidOld = true; FiresCore.Logging.FiresLogger.LogInfo($"[CompanionKeep] FGN's API is v{version} (needs v{NeededVersion}, FGN 1.6.62+): FGN keeps its own fallback to the whole RemoveObjects"); }
                return;
            }
            bool ok = (bool)register.Invoke(null, new object[] { global::FiresCore.FiresUnifiedCore.PluginGUID, s_refresh, s_isKept });
            FiresCore.Logging.FiresLogger.LogInfo(ok
                ? $"[CompanionKeep] registered with FGN's server removal keep (FGN API v{version}): FGN's sliced removal keeps the companion zones' ZDOs"
                : $"[CompanionKeep] FGN refused the removal keep registration (FGN API v{version})");
        }

        // Quiet lookup: Type.GetType / AccessTools.TypeByName would need the assembly name or log a warning on a miss.
        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(fullName, false); } catch { continue; }
                if (t != null) return t;
            }
            return null;
        }
    }
}
