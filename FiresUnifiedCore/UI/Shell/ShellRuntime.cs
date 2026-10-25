// Fires UI Shell, Core step 0/1: the one MonoBehaviour of the shell. It exists ONLY while [UI] Fires UI override is ON on a
// client with graphics: FiresShell.Bind/ApplyOverride creates it, and Shutdown() (master OFF) destroys it with the hub, the
// sky camera, the RenderTexture, the canvas and the runtime font assets. With the master OFF it is never created.
using System;
using FiresCore.UI.Shell.Kit;
using UnityEngine;

namespace FiresCore.UI.Shell
{
    internal sealed class ShellRuntime : MonoBehaviour
    {
        internal static ShellRuntime Instance { get; private set; }

        internal GameShellHost Host { get; private set; }
        internal ShellHub Hub { get; private set; }

        private static bool s_commandAdded;

        internal static ShellRuntime Ensure()
        {
            if (Instance != null) return Instance;
            // Gates: master ON, not headless. Callers check too; this is the last line.
            if (HeadlessGuard.IsHeadless || FiresShell.CfgOverride == null || !FiresShell.CfgOverride.Value) return null;

            var go = new GameObject("FiresShellRuntime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Instance = go.AddComponent<ShellRuntime>();
            Instance.Host = new GameShellHost();
            KitText.Host = Instance.Host;
            Instance.Hub = new ShellHub(Instance);
            Instance.Hub.RegisterBuiltIns();
            AddConsoleCommand();
            ShellLog.Debug("runtime created (FiresShellRuntime)");
            return Instance;
        }

        /// <summary>Master OFF: close the hub (OnHide, input released, registry entry removed) and destroy everything.</summary>
        internal static void Shutdown()
        {
            var rt = Instance;
            if (rt == null) return;
            ShellMinimap.Restore();   // the small minimap back to vanilla
            ShellChat.Restore();      // the chat back to vanilla
            try { rt.Hub?.Destroy(); } catch (Exception e) { ShellLog.Warn("hub destroy threw " + e.GetType().Name + ": " + e.Message); }
            rt.Hub = null;
            try { KitText.ClearCache(); } catch { }
            try { KitSprites.ClearCache(); } catch { }
            if (KitText.Host == rt.Host) KitText.Host = null;
            rt.Host?.Reset();
            Instance = null;
            UnityEngine.Object.Destroy(rt.gameObject);
            ShellLog.Debug("runtime destroyed (master OFF)");
        }

        private void Update()
        {
            if (Hub == null) return;
            long t0 = ShellPerfProbe.Now();
            try { Hub.Tick(); }
            catch (Exception e) { ShellLog.Warn("hub tick threw " + e.GetType().Name + ": " + e.Message); }
            ShellPerfProbe.Add(ShellPerfProbe.Slot.HubTick, t0);
        }

        private void LateUpdate()
        {
            long t0 = ShellPerfProbe.Now();
            try { ShellMinimap.Tick(); }   // the small minimap restyle (master ON only, like everything here)
            catch (Exception e) { ShellLog.Warn("minimap tick threw " + e.GetType().Name + ": " + e.Message); }
            ShellPerfProbe.Add(ShellPerfProbe.Slot.MinimapTick, t0);
            t0 = ShellPerfProbe.Now();
            try { ShellChat.Tick(); }      // the chat (ChatOpenChanged, free cursor while typing, rounded skin)
            catch (Exception e) { ShellLog.Warn("chat tick threw " + e.GetType().Name + ": " + e.Message); }
            ShellPerfProbe.Add(ShellPerfProbe.Slot.ChatTick, t0);
            if (Hub == null) return;
            t0 = ShellPerfProbe.Now();
            try { Hub.LateTick(); }
            catch (Exception e) { ShellLog.Warn("hub late tick threw " + e.GetType().Name + ": " + e.Message); }
            ShellPerfProbe.Add(ShellPerfProbe.Slot.HubLateTick, t0);
            try { ShellPerfProbe.Frame(Hub); }   // measurement only, while the hub is open
            catch (Exception e) { ShellLog.Warn("perf probe threw " + e.GetType().Name + ": " + e.Message); }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // "fires_hub": opens the hub while the master is ON (the hotkey may be skipped by the conflict check, and the pause-menu
        // button is not built yet). Added the first time the runtime starts, never while the master is OFF; afterwards it
        // stays in vanilla's command table (it cannot be removed) and only reports that the master is OFF.
        private static void AddConsoleCommand()
        {
            if (s_commandAdded) return;
            s_commandAdded = true;
            try
            {
                new Terminal.ConsoleCommand("fires_hub", "Opens the Fires hub (needs [UI] Fires UI override ON)", (Terminal.ConsoleEvent)(args =>
                {
                    if (!FiresShell.OverrideOn) { args.Context?.AddString("Fires UI override is OFF"); return; }
                    if (FiresShell.IsOpen) FiresShell.Close(); else FiresShell.Open();
                }));
            }
            catch (Exception e) { ShellLog.Warn("fires_hub command not added: " + e.Message); }
            // The layout editor's click-through / visibility proof (run with the HUD layout editor open)
            try
            {
                new Terminal.ConsoleCommand("fires_dock_probe", "HUD layout editor: raycast every name tag + the legend title, and list hotbar visibility (to the log)", (Terminal.ConsoleEvent)(args =>
                {
                    FiresCore.UI.Dock.DockLayoutOverlay.Probe();
                    args.Context?.AddString("dock probe written to the log ([Dock] dock: probe ...)");
                }));
            }
            catch (Exception e) { ShellLog.Warn("fires_dock_probe command not added: " + e.Message); }
        }
    }
}
