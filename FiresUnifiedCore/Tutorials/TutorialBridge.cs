using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using FiresCore.Input;
using FiresCore.Logging;
using FiresTutorial;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Tutorials
{
    /// <summary>
    /// The one tutorial menu for every Fires mod (the FiresTutorial manager, shared source linked into Core and
    /// into mods that must also run alone, all its types internal). A mod hands Core its chapters as FiresTutorial's JSON plus
    /// callbacks keyed "chapterId/stepId" - only System types cross, so a mod with its own compiled copy (Valcast) calls this by
    /// reflection with no reference to Core: <c>Type.GetType("FiresCore.Tutorials.TutorialBridge, FiresUnifiedCore")</c>.
    /// - Register: the mod's book into Core's manager (6 parameters; the 5-parameter form has no live key text).
    /// - Show: start or resume that mod's book (chapterId null = resume). Unregister, MarkDone (an event-driven step), IsRunning.
    /// Core owns the "tutorial" console command and Skip / Back / Leave keys ([Tutorial] in Core's config) while a bridged book
    /// runs; the mod's own start / stop run through enter("!start") / enter("!stop") (FiresTutorial.TutorialJson). Info: one line
    /// per bridged mod; the manager's own step lines are Debug.
    /// </summary>
    public static class TutorialBridge
    {
        private const string Section = "Tutorial";
        private const string Command = "tutorial";
        private static KeyBinding s_skipKey, s_backKey, s_leaveKey;
        private static readonly Dictionary<string, string> s_bookOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> s_nameOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> s_marked = new HashSet<string>(StringComparer.Ordinal);

        internal static void Bind(ConfigFile config)
        {
            s_skipKey = new KeyBinding(config, Section, "SkipKey", KeyCode.RightArrow, defaultCtrl: true, defaultAlt: true,
                description: "Skip the current step of a tutorial (any Fires mod's, in Core's tutorial menu).");
            s_backKey = new KeyBinding(config, Section, "BackKey", KeyCode.LeftArrow, defaultCtrl: true, defaultAlt: true,
                description: "Go back one tutorial step.");
            s_leaveKey = new KeyBinding(config, Section, "LeaveKey", KeyCode.H, defaultCtrl: true, defaultAlt: true,
                description: "Leave the running tutorial (the mod puts its own state back). Start one with the console command 'tutorial'.");
        }

        /// <summary>True while Core's manager runs any book (a mod's own "open" key should then leave the leaving to Core).</summary>
        public static bool IsRunning => TutorialRunner.IsRunning;

        /// <summary>The bridged book of <paramref name="modGuid"/> into Core's manager; false when the JSON isn't a format this Core reads.</summary>
        public static bool Register(string modGuid, string modName, string chaptersJson, Func<string, bool> stepDone, Action<string> stepEnter, Func<string, string> stepKey)
        {
            if (string.IsNullOrEmpty(modGuid) || string.IsNullOrEmpty(chaptersJson)) return false;
            try
            {
                string guid = modGuid;
                Func<string, bool> done = key => s_marked.Contains(guid + "|" + key) || (stepDone != null && stepDone(key));
                var book = TutorialJson.FromJson(chaptersJson, done, stepEnter, stepKey);
                if (book == null)
                {
                    FiresLogger.LogWarning($"[Tutorial] {modName ?? modGuid}: its chapters are not a tutorial format this Core reads (format {TutorialJson.Format}); not bridged");
                    return false;
                }
                Host(book);
                TutorialRegistry.Register(book);
                s_bookOf[modGuid] = book.Id;
                s_nameOf[modGuid] = string.IsNullOrEmpty(modName) ? modGuid : modName;
                EnsureRunner();
                FiresLogger.LogInfo($"[Tutorial] {s_nameOf[modGuid]} bridged into Core's tutorial menu: '{book.Title}', {book.Chapters.Count} chapter(s), {book.Total} step(s)");
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[Tutorial] {modName ?? modGuid}: bridging its tutorial threw ({ex.GetType().Name}: {ex.Message})");
                return false;
            }
        }

        public static bool Register(string modGuid, string modName, string chaptersJson, Func<string, bool> stepDone, Action<string> stepEnter)
            => Register(modGuid, modName, chaptersJson, stepDone, stepEnter, null);

        /// <summary>Start or resume a bridged mod's book (chapterId null = where the player left off).</summary>
        public static bool Show(string modGuid, string chapterId)
        {
            if (modGuid == null || !s_bookOf.TryGetValue(modGuid, out string bookId)) return false;
            EnsureRunner();
            return TutorialRunner.Begin(bookId, string.IsNullOrEmpty(chapterId) ? null : chapterId);
        }

        public static void Unregister(string modGuid)
        {
            if (modGuid == null || !s_bookOf.TryGetValue(modGuid, out string bookId)) return;
            if (TutorialRunner.RunningBookId == bookId) TutorialRunner.End("its mod unregistered it");
            s_bookOf.Remove(modGuid);
            s_nameOf.Remove(modGuid);
        }

        /// <summary>A step done by an event the mod saw ("chapterId/stepId"); the manager reads it on its next check.</summary>
        public static void MarkDone(string modGuid, string stepKey)
        {
            if (!string.IsNullOrEmpty(modGuid) && !string.IsNullOrEmpty(stepKey)) s_marked.Add(modGuid + "|" + stepKey);
        }

        // Core's half of a bridged book: keys, input gate, controls line, logs.
        private static void Host(TutorialBook book)
        {
            book.SkipPressed = () => s_skipKey != null && s_skipKey.IsPressed();
            book.BackPressed = () => s_backKey != null && s_backKey.IsPressed();
            book.ExitPressed = () => s_leaveKey != null && s_leaveKey.IsPressed();
            book.InputBlocked = Blocked;
            book.ControlsLine = () => $"Skip {s_skipKey}  ·  Back {s_backKey}  ·  Leave {s_leaveKey}";
            book.LogDebug = FiresLogger.LogDebug;
            book.LogInfo = FiresLogger.LogInfo;
        }

        private static bool Blocked()
        {
            try
            {
                return (global::Console.instance != null && global::Console.IsVisible()) || (Chat.instance != null && Chat.instance.HasFocus())
                       || Menu.IsVisible() || TextInput.IsVisible() || InventoryGui.IsVisible() || Minimap.IsOpen();
            }
            catch { return false; }
        }

        private static void EnsureRunner()
        {
            if (TutorialRunner.Instance != null) return;
            var go = new GameObject("FiresTutorialRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TutorialRunner>();
        }

        // ── "tutorial" ──────────────────────────────────────────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class Terminal_InitTerminal_Tutorial
        {
            private static bool s_registered;

            [HarmonyPostfix]
            private static void Postfix()
            {
                if (s_registered) return;
                s_registered = true;
                new Terminal.ConsoleCommand(Command,
                    "[FiresUnifiedCore] The Fires mods' guided tutorials. 'tutorial' lists them; 'tutorial <mod> [chapter]' starts or resumes one; 'tutorial stop' leaves.",
                    args => Run(args.Context, args.Args.Skip(1).ToArray()));
            }
        }

        private static void Run(Terminal console, string[] words)
        {
            if (words.Length == 0)
            {
                if (s_bookOf.Count == 0) { console?.AddString("[Tutorial] no Fires mod has a tutorial here yet."); return; }
                foreach (var pair in s_bookOf)
                {
                    var book = TutorialRegistry.Get(pair.Value);
                    if (book == null) continue;
                    console?.AddString($"[Tutorial] {s_nameOf[pair.Key]}: '{book.Title}' - chapters: {string.Join(", ", book.Chapters.Select(c => c.Id).ToArray())}");
                }
                console?.AddString("[Tutorial] 'tutorial <mod> [chapter]' starts or resumes; 'tutorial stop' leaves.");
                return;
            }
            if (words[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                if (TutorialRunner.IsRunning) TutorialRunner.End("the 'tutorial stop' command");
                return;
            }
            string guid = Find(words[0]);
            if (guid == null) { console?.AddString($"[Tutorial] no tutorial for '{words[0]}' ('tutorial' lists them)."); return; }
            if (!Show(guid, words.Length > 1 ? words[1] : null)) console?.AddString($"[Tutorial] {s_nameOf[guid]}'s tutorial could not start here.");
        }

        // A mod by its GUID, its name, its book id, or the start of its name.
        private static string Find(string word)
        {
            foreach (var pair in s_bookOf)
                if (pair.Key.Equals(word, StringComparison.OrdinalIgnoreCase) || pair.Value.Equals(word, StringComparison.OrdinalIgnoreCase)
                    || s_nameOf[pair.Key].Equals(word, StringComparison.OrdinalIgnoreCase)) return pair.Key;
            foreach (var pair in s_bookOf)
                if (s_nameOf[pair.Key].StartsWith(word, StringComparison.OrdinalIgnoreCase)) return pair.Key;
            return null;
        }
    }
}
