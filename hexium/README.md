# FiresUnifiedCore

**Updated for Valheim 1.0.**

The shared foundation of the Fires mod family. Every Fires mod (FiresAdminPrefabs, FiresAdminTerrain,
FiresEasyBakeMeshes, FiresRPGmaker, FiresCompanions and the rest) runs on it, so install it wherever you install
one of them: on every client and on the dedicated server.

Most of it works behind the scenes for the other Fires mods. A few pieces you use directly.

## What you get

- **Config window (F1, or F8 when another configuration manager is installed).** Every installed mod's settings in
  one window. Search across all mods, edit live, reset any setting, and see clashing keybinds. Server-locked
  settings show greyed out, and only admins can change them. Also under Settings > Mod Settings, or `va_config` in the console.
- **Move your HUD.** Press Esc and drag any HUD element where you want it, or resize it from its corner
  (double-click the corner to reset its size). The Edit layout button opens the full editor with Save, Cancel and Reset.
  New installs start with a ready-made layout.
- **Player tab in the F1 hub.** Stats, Appearance, Classes, Leaderboard and Achievements in one place.
- **In-game help.** A "? Help" button on the pause menu. Each Fires mod documents its own features there.
- **Group HUD.** Companions and party members listed under the day/time panel with health, stamina and eitr bars.
- **Context menu.** Hold Alt+Shift and right-click companions, chests and other objects for their actions.
  Off by default; turn it on in the settings.
- **Shared undo and redo.** Numpad 7 and 9 undo and redo across all Fires mods.
- **Environment Box.** A build piece that sets its own weather, sky and mood inside an area you size
  (mouse wheel = size, Shift + wheel = height).
- **The companion brain** (used by FiresCompanions). Companions pick their fights, block, dodge, use their class
  skills, back off when hurt and help you when you're hit. They find their way round obstacles, use doors, and do
  chores: gathering, filling chests, repairing and eating.
- **Live config reloads.** Edit a Fires mod's `.cfg` file and the change applies without a restart.
- **Quieter logs.** Fires mods write only warnings and errors by default. Set Log Level to Info in Core's settings to
  see more.
- **HD textures.** With HDValheimTextures installed, textures load behind a loading screen instead of freezing the
  menu, at half size by default to save memory.

## What Core does for each mod

Much of Core is shared machinery that other Fires mods build on. Here is what it does for them:

- **FiresCompanions:** the companion brain. Fighting, following, opening doors and finding paths, chores (gathering,
  hauling, repairing, eating), picking up loot, and the companions' rows on the Group HUD.
- **FiresStorageNetwork:** the storage network. One view over every chest in your base, safe taking and depositing
  in multiplayer, favourites, hoppers that pull in dropped items and sort them into your chests, and the Storage
  Terminal window's look.
- **FiresRPGmaker:** NPC behaviour (standing posts, patrol routes, fighting back), the territory plate under the
  minimap, and hiding the HUD while its windows are open.
- **FiresRPGClasses:** class skills that companions use, and the class bars' place in the HUD layout.
- **VAInventory:** the look of the inventory, crafting, chest and companion panels, moving and resizing them in the
  layout editor, hover tips, its help pages, and stopping your keys from moving you while you type.
- **FiresAdminTerrain:** its help pages, shared materials and shaders, server-synced settings and data, saved world
  data, and key bindings.
- **FiresMounts:** its help pages, the flying used by flying mounts, shared mount and creature-variant handling,
  key bindings, and server-synced settings.
- **FiresDyes:** the look of the Dyer's Bench window, server-synced settings, and keeping glowing gear's own glow when
  you dye it.
- **FiresCharacterCreator:** the shared Fires window it opens in, keeping gear fitted on reshaped bodies, its help page,
  server-synced settings, and carrying old settings over to their new names.
- **FiresLoreBooks:** stopping your keys from moving you while you type in a book or journal, its window look and
  font, and server-synced settings.
- **FiresMiniGames:** the camera that frames the arcade, TV and air-hockey screens, holding you still while you play,
  its right-click menu and settings panel, the Tinker's Bench glow, and its help page.
- **VABackpacks:** its help pages, the staff animations and secondary casts, the staffs' link to companion classes,
  holding your keys still while you aim, and the ground rings for placing staff spells.
- **FiresTossinShade:** the shared water and season state, shared shaders, setting presets in the config window,
  fitting hair to reshaped heads, server sync, and its help pages.
- **FiresAllTheItems:** its window look, item icons, recipe and creature-drop edits that the server keeps and sends to
  everyone, and its help pages.
- **FiresDebugginMenu:** the Environment Box, the Hammer pieces it adds, its right-click menu, and holding your keys still
  in its windows.
- **Every Fires mod:** the config window and server sync, admin-locked settings, help pages, shared undo and redo, and
  the HUD layout.

## Core's own features

These work on their own, without another Fires mod:

- Climbing, vaulting and pulling yourself up ledges.
- Crouching and crawling under low ceilings, plus a closer camera, in caves and dungeons only.
- Double jump and run slide, when unlocked.
- Crafting with materials from nearby chests, and chest tags that say what belongs in a chest.
- Hopper chests.
- The context menu and the Environment Box.

## For server admins

- **Move config files without FTP.** `pushconfigs <pattern>` sends config files from your machine to the
  dedicated server, `pullconfigs <pattern>` fetches the server's copies, and `deleteconfig <pattern> confirm`
  removes them. Settings that only exist on the server are kept when you push. Admin only.
- **Recipe and creature edits.** Edits made with a Fires editor (such as FiresAllTheItems) are stored on the server
  and sent to every player as they join.
- **Leftover objects.** `zdo_scan_orphans` lists saved objects from mods you removed; `zdo_clean_orphans` deletes
  them. Back up the world first.
- **The item vault rolls back with the world.** After a crash, the bank, mail, marketplace and guild storage go back
  to match the world save, so items can't be duplicated or lost.

## Install

- Client **and** dedicated server.
- Keep `LiteDB.dll` and `System.Buffers.dll` in the same folder as `FiresUnifiedCore.dll`. A mod manager does
  this for you.
- Update Core together with the Fires mods that use it.

## Requires

- BepInExPack for Valheim

## Issues / questions

https://discord.gg/zQRgHbqms4
