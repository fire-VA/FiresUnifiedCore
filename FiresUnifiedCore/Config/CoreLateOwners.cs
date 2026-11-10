using System;
using HarmonyLib;
using FiresCore.Logging;

namespace FiresCore.Config
{
    /// <summary>
    /// 0.2.1069 ([fgn] + the lead's audit 10-31): owner mods hand Core their values (CoreSettings.Provide) in THEIR Awake / Start, which runs
    /// after Core's Awake, so a decision Core made at its own Awake only ever saw Core's fallback. The startup decisions that were made once
    /// now run here, ONCE, at the first FejdStartup.Awake (client) or ZNet.Awake (dedicated server) - every plugin's Awake has run by then:
    ///  - KinematicSync: the ClientSync prefix or the 2 s sweeper ([Performance] Kinematic sync sweep, owner FGN / FiresCompanions);
    ///  - Climbing: the split migration's "climbing was Off -> master off" carry, plus the one-time repair of a cfg migrated from the fallback;
    ///  - Pillion auto-board: its state line.
    /// And a provider that arrives LATER resets the two runtime caches (Headroom camera collision, CoreMovement stand-down).
    /// Debug: "owner value applied late: &lt;section&gt;/&lt;key&gt; = &lt;value&gt; from &lt;mod&gt;".
    /// </summary>
    internal static class CoreLateOwners
    {
        private static bool s_ran, s_hooked;

        internal static void Hook()
        {
            if (s_hooked) return;
            s_hooked = true;
            CoreSettings.Provided += (section, key, owner) =>
            {
                if (section == "Camera" && key == "Camera collision") { FiresCore.Movement.Headroom.ResetCameraGuard(); Say(section, key, owner); }
                else if (section == "Movement" && key == "Stand down for plugins") { FiresCore.Movement.CoreMovement.ResetStandDown(); Say(section, key, owner); }
            };
        }

        private static void Say(string section, string key, string owner)
            => FiresLogger.LogDebug($"owner value applied late: {section}/{key} = {Value(section, key)} from {owner ?? CoreSettings.ProviderOf(section, key) ?? "Core"} (re-resolved)");

        private static string Value(string section, string key)
        {
            try { return Convert.ToString(CoreSettings.Get<object>(section, key, "(Core's own)")); } catch { return "?"; }
        }

        private static void RunOnce(string where)
        {
            if (s_ran) return;
            s_ran = true;
            // 0.2.1217 ([generator]): each bridge on its own line. 0.2.1216 shipped with the FGN line lost to a script slip (a PowerShell
            // '+' concatenation without parentheses passed only its first piece), so FGN's v8 hooks (stats card, far-idle skip) were not wired in 1216.
            try { FiresCore.Compat.EbmHooksBridge.RunOnce(); } catch (Exception e) { FiresLogger.LogWarning("[CoreLateOwners] EBM hooks: " + e.Message); }   // 0.2.1217: before any world bakes
            try { FiresCore.Snow.SnowShelterEbm.BindApi(); } catch (Exception e) { FiresLogger.LogWarning("[CoreLateOwners] EBM stand-in event: " + e.Message); }   // 0.2.1218
            try { FiresCore.Services.DistantRenderer.FlushToFgn(); FiresCore.Services.DistantRenderer.SayIfNoFgn(); } catch (Exception e) { FiresLogger.LogWarning("[CoreLateOwners] distant rules to FGN: " + e.Message); }   // 0.2.1217
            try { FiresCore.Npc.FixesHooksBridge.RunOnce(); } catch (Exception e) { FiresLogger.LogWarning("[CoreLateOwners] FiresFixes hooks: " + e.Message); }   // 0.2.1216
            try { FiresCore.Npc.FgnHooksBridge.RunOnce(); } catch (Exception e) { FiresLogger.LogWarning("[CoreLateOwners] FGN hooks: " + e.Message); }   // 0.2.1215
            try
            {
                FiresCore.Npc.KinematicSync.Decide();
                // 0.2.1217: FGN runs the kinematic refresh from its own key; Core no longer reads it
            }
            catch (Exception e) { FiresLogger.LogWarning("[CoreLateOwners] kinematic sync: " + e.Message); }
            try { FiresCore.Movement.Climbing.LateMigration(); } catch (Exception e) { FiresLogger.LogWarning("[CoreLateOwners] climbing migration: " + e.Message); }
            try
            {
                bool on = CoreSettings.Get("Companions", "Pillion auto-board", true);
                FiresLogger.LogDebug($"owner value applied late: Companions/Pillion auto-board = {on} from {CoreSettings.ProviderOf("Companions", "Pillion auto-board") ?? "Core"}");
            }
            catch { }
            FiresCore.Events.CoreEvents.RaiseAllPluginsLoaded();   // 0.2.1074: every plugin's Awake has run
        }

        [HarmonyPatch(typeof(FejdStartup), "Awake")]
        private static class FejdStartup_Awake_LateOwners
        {
            private static void Postfix() => RunOnce("main menu");
        }

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ZNet_Awake_LateOwners
        {
            private static void Prefix() => RunOnce("world start");
        }
    }
}
