using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Undo
{
    /// <summary>One undoable edit. Undo and Redo re-apply through the mod's normal edit path (so it persists and syncs); each returns a message or null.</summary>
    public interface IUndoStep
    {
        string Undo();
        string Redo();
        /// <summary>What the step was, for the log and the message ("a V-carve stroke (4 chunks)").</summary>
        string Describe();
    }

    /// <summary>
    /// ONE undo history for every Fires mod (Core 0.2.296, Fire 10-05: "build our undo and redo code into core so that way all of our mods
    /// can use it when jere's mods are not installed"; numpad 7 = undo, numpad 9 = redo, as Infinity Hammer).
    /// - With Server Devcommands installed, every step goes into ITS UndoManager (the history Infinity Hammer and World Edit Commands use),
    ///   so IH's numpad 7 / 9 and the console "undo" / "redo" step through our edits mixed with theirs. It's a soft dependency: SDC calls a
    ///   step's public Undo() / Redo() by reflection, and Core hands it the step through UndoManager's Add the same way; no SDC type is used.
    /// - Without SDC, Core keeps the history itself (per player, bounded).
    /// - Keys: Core reads its own undo / redo keys only when Infinity Hammer isn't installed (IH binds numpad 7 / 9 then, so a press never
    ///   fires twice). Pressed, they step whichever history holds the steps (SDC's or Core's).
    /// Grouping: steps recorded between BeginGroup and EndGroup are one step (undone in reverse order).
    /// Debug lines: "[Undo] + step (…history): …", "[Undo] undo: …", "[Undo] redo: …".
    /// </summary>
    public static class UndoHistory
    {
        public const string SdcGuid = "server_devcommands", InfinityHammerGuid = "infinity_hammer";
        public const int Depth = 50;

        /// <summary>Raised after a step is recorded, undone or redone, or the history cleared.</summary>
        public static event Action Changed;

        private static ConfigEntry<KeyCode> s_undoKey, s_redoKey;
        private static readonly List<IUndoStep> s_steps = new List<IUndoStep>();
        private static int s_index = -1;
        private static bool s_sdcResolved;
        private static MethodInfo s_sdcAdd, s_sdcUndo, s_sdcRedo;
        private static Group s_group;
        private static int s_groupDepth;

        internal static void Bind(ConfigFile config)
        {
            s_undoKey = config.Bind("Undo", "Undo key", KeyCode.Keypad7,
                "Undo the last edit made with a Fires tool (and, with Server Devcommands, any edit in its shared history). Read only when Infinity Hammer isn't installed; it binds numpad 7 to the same undo then.");
            s_redoKey = config.Bind("Undo", "Redo key", KeyCode.Keypad9,
                "Redo the last undone edit. Read only when Infinity Hammer isn't installed; it binds numpad 9 to the same redo then.");
            Runner.Ensure();
        }

        /// <summary>True when Server Devcommands' history holds the steps.</summary>
        public static bool UsesSdc { get { Resolve(); return s_sdcAdd != null; } }

        /// <summary>True when Core reads its own undo / redo keys (Infinity Hammer isn't installed).</summary>
        public static bool OwnKeys => !BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(InfinityHammerGuid);

        /// <summary>Record one step (into the open group, if any).</summary>
        public static void Record(IUndoStep step)
        {
            if (step == null) return;
            if (s_group != null) { s_group.Steps.Add(step); return; }
            Push(step);
        }

        /// <summary>Record one step from two actions.</summary>
        public static void Record(string what, Func<string> undo, Func<string> redo) => Record(new ActionStep(what, undo, redo));

        /// <summary>Open a group: everything recorded until the matching EndGroup becomes one step called <paramref name="what"/>. Nests.</summary>
        public static void BeginGroup(string what)
        {
            if (s_groupDepth++ == 0) s_group = new Group(what);
        }

        public static void EndGroup()
        {
            if (s_groupDepth == 0 || --s_groupDepth > 0) return;
            var group = s_group;
            s_group = null;
            if (group.Steps.Count == 1) Push(group.Steps[0]);
            else if (group.Steps.Count > 1) Push(group);
        }

        /// <summary>Undo the newest step (SDC's history when it holds the steps).</summary>
        public static void Undo()
        {
            Resolve();
            if (s_sdcUndo != null) { CallSdc(s_sdcUndo); Changed?.Invoke(); return; }
            if (s_index < 0) { Show("Nothing to undo."); return; }
            var step = s_steps[s_index--];
            Show(Run(step, false));
            Changed?.Invoke();
        }

        /// <summary>Redo the newest undone step.</summary>
        public static void Redo()
        {
            Resolve();
            if (s_sdcRedo != null) { CallSdc(s_sdcRedo); Changed?.Invoke(); return; }
            if (s_index >= s_steps.Count - 1) { Show("Nothing to redo."); return; }
            var step = s_steps[++s_index];
            Show(Run(step, true));
            Changed?.Invoke();
        }

        /// <summary>Forget Core's own history (a world's history is that world's).</summary>
        public static void Clear()
        {
            s_steps.Clear();
            s_index = -1;
            s_group = null;
            s_groupDepth = 0;
            Changed?.Invoke();
        }

        private static void Push(IUndoStep step)
        {
            Resolve();
            // SDC calls the public Undo()/Redo() of what it holds; a wrapper keeps the log line and the Changed event.
            if (s_sdcAdd != null)
            {
                try
                {
                    s_sdcAdd.Invoke(null, new object[] { new SdcStep(step) });
                    FiresCore.Logging.FiresLogger.LogDebug($"[Undo] + step (Server Devcommands' history): {Describe(step)}");
                    Changed?.Invoke();
                    return;
                }
                catch (Exception ex) { Debug.LogWarning($"[Undo] Server Devcommands' UndoManager refused the step ({ex.InnerException?.Message ?? ex.Message}); Core keeps it"); }
            }
            if (s_index < s_steps.Count - 1) s_steps.RemoveRange(s_index + 1, s_steps.Count - s_index - 1);
            s_steps.Add(step);
            if (s_steps.Count > Depth) s_steps.RemoveAt(0);
            s_index = s_steps.Count - 1;
            FiresCore.Logging.FiresLogger.LogDebug($"[Undo] + step (Core's history): {Describe(step)}");
            Changed?.Invoke();
        }

        internal static string Run(IUndoStep step, bool redo)
        {
            string message;
            try { message = redo ? step.Redo() : step.Undo(); }
            catch (Exception ex)
            {
                message = $"Couldn't {(redo ? "redo" : "undo")} {Describe(step)}: {ex.Message}";
                Debug.LogWarning($"[Undo] {(redo ? "redo" : "undo")} of {Describe(step)} threw: {ex}");
            }
            if (string.IsNullOrEmpty(message)) message = $"{(redo ? "Redid" : "Undid")} {Describe(step)}.";
            FiresCore.Logging.FiresLogger.LogDebug($"[Undo] {(redo ? "redo" : "undo")}: {Describe(step)} - {message}");
            return message;
        }

        private static string Describe(IUndoStep step)
        {
            try { return step.Describe() ?? step.GetType().Name; }
            catch { return step.GetType().Name; }
        }

        private static void CallSdc(MethodInfo method)
        {
            Terminal terminal = (Terminal)Chat.instance ?? global::Console.instance;
            try { method.Invoke(null, new object[] { terminal }); }
            catch (Exception ex) { Debug.LogWarning($"[Undo] Server Devcommands' {method.Name} threw: {ex.InnerException?.Message ?? ex.Message}"); }
        }

        private static void Show(string message)
        {
            if (!string.IsNullOrEmpty(message) && MessageHud.instance != null) MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, message);
        }

        private static void Resolve()
        {
            if (s_sdcResolved) return;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(SdcGuid, out var info)) { s_sdcResolved = true; return; }
            if (info?.Instance == null) return;   // not loaded yet: ask again later
            s_sdcResolved = true;
            var type = info.Instance.GetType().Assembly.GetType("ServerDevcommands.UndoManager");
            if (type == null) return;
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            s_sdcAdd = type.GetMethod("Add", any, null, new[] { typeof(object) }, null);
            s_sdcUndo = type.GetMethod("Undo", any, null, new[] { typeof(Terminal) }, null);
            s_sdcRedo = type.GetMethod("Redo", any, null, new[] { typeof(Terminal) }, null);
            if (s_sdcAdd == null || s_sdcUndo == null || s_sdcRedo == null)
            {
                Debug.LogWarning("[Undo] Server Devcommands is installed but its UndoManager has no Add(object)/Undo/Redo; Core keeps its own history");
                s_sdcAdd = s_sdcUndo = s_sdcRedo = null;
            }
            else FiresCore.Logging.FiresLogger.LogDebug("[Undo] steps go to Server Devcommands' undo history (shared with Infinity Hammer / World Edit Commands)");
        }

        // ── Steps ────────────────────────────────────────────────────────────────────────────────────────────────────────
        private sealed class ActionStep : IUndoStep
        {
            private readonly string _what;
            private readonly Func<string> _undo, _redo;
            public ActionStep(string what, Func<string> undo, Func<string> redo) { _what = what; _undo = undo; _redo = redo; }
            public string Undo() => _undo?.Invoke();
            public string Redo() => _redo?.Invoke();
            public string Describe() => _what;
        }

        private sealed class Group : IUndoStep
        {
            private readonly string _what;
            public readonly List<IUndoStep> Steps = new List<IUndoStep>();
            public Group(string what) { _what = what; }
            public string Undo() { for (int i = Steps.Count - 1; i >= 0; i--) Steps[i].Undo(); return null; }
            public string Redo() { foreach (var step in Steps) step.Redo(); return null; }
            public string Describe() => $"{_what} ({Steps.Count} edits)";
        }

        /// <summary>What SDC's UndoManager holds: it calls these public methods by reflection and shows the returned text.</summary>
        public sealed class SdcStep
        {
            private readonly IUndoStep _step;
            internal SdcStep(IUndoStep step) { _step = step; }
            public string Undo() => Run(_step, false);
            public string Redo() => Run(_step, true);
            public string UndoMessage() => $"Undid {Describe(_step)}.";
            public string RedoMessage() => $"Redid {Describe(_step)}.";
        }

        // ── Keys ─────────────────────────────────────────────────────────────────────────────────────────────────────────
        private sealed class Runner : MonoBehaviour
        {
            private static Runner s_instance;

            internal static void Ensure()
            {
                if (s_instance != null) return;
                var go = new GameObject("FiresUndo");
                DontDestroyOnLoad(go);
                s_instance = go.AddComponent<Runner>();
            }

            private void Update()
            {
                if (Player.m_localPlayer == null || !OwnKeys || s_undoKey == null) return;
                if ((Chat.instance != null && Chat.instance.HasFocus()) || global::Console.IsVisible() || InventoryGui.IsVisible()
                    || Menu.IsVisible() || TextInput.IsVisible()) return;
                if (s_undoKey.Value != KeyCode.None && ZInput.GetKeyDown(s_undoKey.Value)) Undo();
                else if (s_redoKey.Value != KeyCode.None && ZInput.GetKeyDown(s_redoKey.Value)) Redo();
            }
        }
    }
}
