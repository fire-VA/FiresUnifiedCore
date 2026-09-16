using UnityEngine;

namespace FiresCore.Help
{
    // Core's own sections for the shared help panel. Core hosts the panel
    // infrastructure (HelpPanel/HelpRegistry) but the panel itself is created
    // by whichever mod calls HelpPanel.Initialize (FiresRPGmaker today) — this
    // class only registers content, exactly like every other consumer mod.
    internal static class FiresCoreHelpContent
    {
        private const string ModId = "Fires Core";
        private static bool _registered;

        public static void Register()
        {
            if (_registered || Application.isBatchMode) return;
            _registered = true;

            HelpRegistry.RegisterSection(ModId, "", "What Fires Core Provides", 0, BuildOverview);
            HelpRegistry.RegisterSection(ModId, "Interface", "F8 Config Window", 10, BuildConfigWindow);
            HelpRegistry.RegisterSection(ModId, "Interface", "Context Menu & Group HUD", 11, BuildContextAndHud);
            HelpRegistry.RegisterSection(ModId, "Building", "Environment Box", 20, BuildEnvironmentBox);
            HelpRegistry.RegisterSection(ModId, "Server (Admin)", "Config Push / Pull / Delete", 30, BuildConfigCommands, adminOnly: true);
            HelpRegistry.RegisterSection(ModId, "Server (Admin)", "Core Config Reference", 31, BuildConfigReference, adminOnly: true);
            HelpRegistry.RegisterSection(ModId, "Commands (Admin)", "FX & Asset Dump Commands", 40, BuildDumpCommands, adminOnly: true);
        }

        private static void BuildOverview(HelpContentWriter writer)
        {
            writer.Header("What Fires Core Provides");
            writer.Paragraph("FiresUnifiedCore is the shared foundation every Fires mod builds on. Most of it " +
                "is invisible plumbing — networking, storage, the companion engine, admin sync — but a few " +
                "pieces are things you use directly:");
            writer.Bullet("F8 — the Fires config window, covering EVERY Fires mod's settings in one place.");
            writer.Bullet("This help panel — the '? Help' button on the pause (Esc) menu. Every [-] " +
                "subheading in it is collapsible: click the heading to fold or unfold it, and your " +
                "layout is remembered per section for the session.");
            writer.Bullet("Hold Alt+Shift + right-click — the context menu on companions, chests, and more.");
            writer.Bullet("The Group HUD under the minimap — status bars for companions traveling with you.");
            writer.Bullet("The Environment Box build piece — custom weather/environment zones.");
            writer.Divider();
            writer.Paragraph("Each Fires mod documents its own features under its own heading in this panel's " +
                "sidebar. If you're looking for companions, building tools, water, or minigames, jump to " +
                "that mod's group.");
        }

        private static void BuildConfigWindow(HelpContentWriter writer)
        {
            writer.Header("F8 Config Window");
            writer.Paragraph("One window for every Fires mod's settings — no per-mod config editing needed.");
            writer.Divider();

            writer.SubHeader("Basics");
            writer.Bullet("Press F8 to open (rebindable: 00 - Config UI / 00 Hotkey). Esc closes.");
            writer.Bullet("va_config — console fallback that toggles the same window.");
            writer.Bullet("Left side lists every Fires mod; click one to expand its section index.");
            writer.Bullet("Search filters across ALL mods — key, section, and description text.");
            writer.Divider();

            writer.SubHeader("Editing");
            writer.Bullet("Toggles, sliders, dropdowns, color fields, and text boxes edit live.");
            writer.Bullet("Keybind entries: click the key box (or Set), press your keys — chords like " +
                "L + LeftAlt record live in the box — then click it again (or Apply) to commit. " +
                "Esc or X cancels without changing anything; X when idle clears the bind.");
            writer.Bullet("Every row has a Reset button back to its default.");
            writer.Bullet("Changed-from-default values are tinted gold.");
            writer.Divider();

            writer.SubHeader("Notes");
            writer.Bullet("00 - Config UI section styles the window itself (font, size, opacity, accent color).");
            writer.Bullet("05 ShowAllPlugins widens the list to every installed plugin, not just Fires mods.");
            writer.Bullet("Server-synced settings show the server's value; on locked servers only admins can " +
                "change them.");
            writer.Bullet("va_config_dump — log every discovered config entry (for debugging).");
        }

        private static void BuildContextAndHud(HelpContentWriter writer)
        {
            writer.Header("Context Menu & Group HUD");
            writer.SubHeader("Context Menu");
            writer.Paragraph("Hold Alt+Shift and right-click an object to open its context menu. What appears " +
                "depends on the target — companions offer orders ('Go here', 'Sit here', 'Fish here', " +
                "'Move here'), chests offer 'Deposit items' and 'Organize', and other Fires mods add " +
                "their own entries (wayshrine admin options, environment boxes, and more).");
            writer.Bullet("ContextMenu.ModifierKey1 / ModifierKey2 — rebind the hold modifiers (default LeftAlt+LeftShift).");
            writer.Bullet("Set ModifierKey2 to None to require only one modifier.");
            writer.Bullet("Esc closes the menu.");
            writer.Divider();

            writer.SubHeader("Group HUD");
            writer.Paragraph("A panel under the minimap listing everyone traveling with you — companions and " +
                "party members — with name, distance, and health/stamina/eitr bars.");
            writer.Bullet("GroupHud.ShowGroupHud — toggle it");
            writer.Bullet("GroupHud.MaxRowsPerColumn / Columns — layout (rows before scrolling; 1 or 2 columns)");
            writer.Bullet("Drag it to reposition; DefaultPosX/Y set where it starts.");
        }

        private static void BuildEnvironmentBox(HelpContentWriter writer)
        {
            writer.Header("Environment Box");
            writer.Paragraph("A hammer-placeable volume that overrides the environment INSIDE it — weather, " +
                "skybox, biome mood, visibility — like a dungeon interior anywhere in the world. Build a " +
                "cozy interior in a storm biome, or a dark crypt under an open sky.");
            writer.Bullet("Place the Environment Box piece, then press H while looking at it to configure.");
            writer.Bullet("Pick the forced environment, adjust the volume size (Shift+MouseWheel scales it).");
            writer.Bullet("Alt+Shift + right-click the box for its context menu options.");
            writer.Bullet("The override applies to anyone standing inside the box; terrain is untouched.");
        }

        private static void BuildConfigCommands(HelpContentWriter writer)
        {
            writer.AdminHeader("Config Push / Pull / Delete");
            writer.Paragraph("Move ANY mod's config files between your machine and the dedicated server " +
                "without FTP access. Patterns match an exact file, a whole folder, or a * wildcard, with " +
                "Tab autocomplete.");
            writer.AdminDivider();

            writer.SubHeader("Commands");
            writer.Bullet("pushconfigs <pattern> — send local config file(s) UP to the server.");
            writer.Bullet("pullconfigs <pattern> — fetch the SERVER's file(s) down to your machine.");
            writer.Bullet("deleteconfig <pattern> [confirm] — delete on the server; lists matches first, runs " +
                "only with 'confirm'. Deleted files are backed up to config_deleted_backups/<timestamp>/.");
            writer.Bullet("Aliases: pushconfig, pullconfig, deleteconfigs, removeconfig, removeconfigs.");
            writer.AdminDivider();

            writer.SubHeader("Pattern Examples");
            writer.CodeBlock("pushconfigs expand_world_spawns.yaml    exact file\n" +
                "pushconfigs expand_world               whole folder, recursive\n" +
                "pushconfigs expand_world*              wildcard\n" +
                "pullconfigs org.bepinex.plugins.*      any mod's .cfg files");
            writer.AdminDivider();

            writer.SubHeader("Behavior & Safety");
            writer.Bullet("Admin-only on both ends; the host is refused (its files already ARE the server's).");
            writer.Bullet("Push goes ONLY to the server — never echoed to other clients, so no reload cascades.");
            writer.Bullet("Identical files are skipped; .cfg files are MERGED entry-by-entry (server-only keys " +
                "survive), other formats replace whole-file. The safe loop is pull → edit → push.");
            writer.Bullet("Extension allowlist — .dll/.bundle can never be pushed; paths are traversal-checked.");
            writer.Bullet("The owning mod's own file watcher picks up the change and reloads server-side.");
            writer.Paragraph("For FiresRPGmaker quest/dialogue content, prefer its dedicated " +
                "firesnpcs_push_configs — it understands that content's merge semantics.");
        }

        private static void BuildConfigReference(HelpContentWriter writer)
        {
            writer.AdminHeader("Core Config Reference");
            writer.Paragraph("com.Fire.FiresUnifiedCore.cfg — Core's own bindings. [Synced] = server-forced " +
                "when ServerAuthority is on.");
            writer.AdminDivider();

            writer.SubHeader("General / Debug");
            writer.Bullet("General.ServerAuthority — the ConfigSync lock; server values override clients [lock]");
            writer.Bullet("General.VerboseLogging — detailed Core logs");
            writer.Bullet("Debug.CompanionFollowDiagnostics — throttled companion follow telemetry");
            writer.AdminDivider();

            writer.SubHeader("HeightmapOverride [Synced]");
            writer.Bullet("Enabled — lift Valheim's ±8m terrain-edit clamp");
            writer.Bullet("MaxHeight / MinHeight — how far terrain can be raised or dug (up to ±1000)");
            writer.AdminDivider();

            writer.SubHeader("Companions engine [Synced]");
            writer.Bullet("Companions.SmoothSpeedRamp / SpeedRampSeconds — ease companion run speed");
            writer.Bullet("Companions.HuntableCreatures — passive prey companions ignore unless Hunt is on");
            writer.Bullet("Companions.EnforceSingleWriterMovement — advanced movement-authority enforcement");
            writer.AdminDivider();

            writer.SubHeader("BalrondCompat [Synced]");
            writer.Paragraph("Guards for Balrond world-pack content: restrict stray Mistlands mist volumes, " +
                "remove added swamp fog, negate SE_MistSickness, and scale PoisonGeyser density.");
        }

        private static void BuildDumpCommands(HelpContentWriter writer)
        {
            writer.AdminHeader("FX & Asset Dump Commands");
            writer.Paragraph("Discovery commands for finding effect/sound/emote names when authoring content. " +
                "List commands write a file; test commands play the asset at your position.");
            writer.AdminDivider();

            writer.Bullet("listfx / testfx <name> [scale] — visual effects");
            writer.Bullet("listfxcategory <shield|fire|ice|lightning|heal|impact|aura|poison|spirit>");
            writer.Bullet("listsfx / testsfx <name> — sound effects (with audio info)");
            writer.Bullet("listsfxcategory <attack|creature|ambient|ui|building|weapon|footstep|voice>");
            writer.Bullet("listemotes / testemote <name> — player emotes (with durations)");
            writer.Bullet("listanims — every animation clip on the player animator");
            writer.Bullet("listclutter [grass|all] — clutter entries with valid materials");
            writer.Bullet("setgrassmat <clutter_name> — set the grass overlay material");
        }
    }
}
