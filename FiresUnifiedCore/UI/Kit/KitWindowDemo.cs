using FiresCore.Logging;
using FiresCore.UI.Shell.Kit;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.UI.Kit
{
    /// <summary>
    /// fires_kitwindow_demo [report]: (admin or devcommands) opens a KitWindow showing every <see cref="KitUI"/> builder, for
    /// visual checks; "report" logs the open demo window's <see cref="KitWindow.LayoutReport"/>. Touches nothing else.
    /// </summary>
    internal static class KitWindowDemo
    {
        private const string Command = "fires_kitwindow_demo";
        private const string Id = "FiresCore.KitWindowDemo";
        private static KitWindow s_window;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class KitWindowDemo_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[FiresUnifiedCore] fires_kitwindow_demo [report] - (admin or devcommands) opens a window showing every uGUI kit builder; report logs its layout check.",
                    args =>
                    {
                        bool admin = false;
                        try { admin = FiresCore.Sync.AdminSyncing.IsLocalAdmin(); } catch { }
                        if (!admin) { args.Context?.AddString(Command + ": admins only"); return; }   // devcommands no longer counts as admin
                        if (args.Args.Length > 1 && args.Args[1].ToLowerInvariant() == "report")
                        {
                            string line = s_window != null ? s_window.LayoutReport() : "[KitWindow] demo not open";
                            args.Context?.AddString(line);
                            FiresLogger.LogInfo(line);
                            return;
                        }
                        Open();
                        args.Context?.AddString(Command + ": opened (Esc or x closes; '" + Command + " report' logs the layout check)");
                    });
            }
        }

        private static void Open()
        {
            if (s_window == null || s_window.Root == null)
            {
                s_window = KitWindow.Create(Id, "Kit window", "Every builder, for visual checks", new Vector2(420f, 640f), new Vector2(340f, 320f));
                Build(s_window.Body);
                s_window.OnClosed = () => FiresLogger.LogDebug("[KitWindow] demo closed");
            }
            s_window.Show();
            FiresLogger.LogDebug(s_window.LayoutReport());
        }

        private static void Build(RectTransform body)
        {
            var scroll = KitUI.ScrollView(body);

            var controls = KitUI.Section(scroll, "CONTROLS", "Every control builder in a label-left / control-right row.");
            var sw = KitUI.Row(controls, "Pill toggle", "A row label with a tooltip");
            KitUI.PillToggle(sw.Control, true, v => Note("toggle " + v));
            var seg = KitUI.Row(controls, "Segmented");
            KitUI.Segmented(seg.Control, new[] { "All", "Inside", "Tags" }, 0, i => Note("segment " + i));
            var sl = KitUI.Row(controls, "Slider");
            KitUI.Slider(sl.Control, 1f, 8f, 1f, 4f, "m", v => Note("slider " + v));
            int n = 4;
            var st = KitUI.Row(controls, "Stepper");
            KitStepper stepper = null;
            stepper = KitUI.Stepper(st.Control, n + " m", () => stepper.SetValue(--n + " m"), () => stepper.SetValue(++n + " m"));
            var help = KitUI.Row(controls, "Help mark");
            KitUI.HelpMark(help.Control, "The \"?\" shows this on hover.");

            var cells = KitUI.Section(scroll, "CELLS");
            var cellRow = KitUI.Row(cells, "Pouch cells");
            Sprite icon = null;
            try { icon = ObjectDB.instance?.GetItemPrefab("Resin")?.GetComponent<ItemDrop>()?.m_itemData?.GetIcon(); } catch { }
            KitUI.IconCell(cellRow.Control, icon, "37", false);
            KitUI.IconCell(cellRow.Control, null, "", true);

            var chips = KitUI.Section(scroll, "CHIPS", "Chips flow and wrap; x removes one.");
            var flow = KitUI.ChipFlow(chips);
            foreach (var t in new[] { "Ores", "Wood", "Boss drops", "Food" }) { var label = t; RectTransform chip = null; chip = KitUI.Chip(flow, label, false, () => Object.Destroy(chip.gameObject)); }
            KitUI.Chip(flow, "!Copper", true, null);

            var field = KitUI.Section(scroll, "FIELD AND BUTTONS");
            KitUI.TextField(field, "Type and press Enter...", s => Note("submit '" + s + "'"));
            var strip = KitUI.Strip(field);
            KitUI.Button(strip, "Save", true, () => Note("save"));
            KitUI.Button(strip, "Other", false, () => Note("other"));
            KitUI.Button(strip, "Disabled", false, null).SetEnabled(false);
            KitUI.Note(field, "A faint note: 11 px, wraps to the column width when the text runs longer than one line.");
            KitUI.Note(field, "A muted note.", false);
        }

        private static void Note(string what) => FiresLogger.LogDebug("[KitWindow] demo: " + what);
    }
}
