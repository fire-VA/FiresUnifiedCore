# FiresUnifiedCore

**Updated for Valheim 1.0.**

The shared foundation of the Fires mod family. Every Fires mod (FiresAdminPrefabs, FiresAdminTerrain,
FiresEasyBakeMeshes, FiresRPGmaker, FiresCompanions and the rest) runs on it, so install it wherever you install
one of them: on every client and on the dedicated server.

Most of it works behind the scenes for the other Fires mods. A few pieces you use directly.

## What's new in 0.2.1223

This is a big update since 0.2.1085. The short version: each Fires mod now looks after its own settings, snow and
swimming feel much better, companions fight smarter, and Core is lighter because work that belongs to other mods has
moved out of it.

### Read this before you update

- **Your settings move, your values come with them.** Settings that belong to another Fires mod now live in that
  mod's own config file. Core's own file keeps only its window, logging and general options. Each moved setting is
  tidied out of Core's file once, and the mod that owns it picks up the value you had. You don't need to reset or
  re-enter anything.
- **Far buildings: everyone updates together.** The way the server and players share distant buildings has changed.
  The server and every player must run Core 0.2.1223. Old far-building cache files refill on their own.
- **The far view now lives in Fires Easy Bake Meshes.** If you want distant buildings drawn, use Fires Easy Bake
  Meshes 1.2.210 or newer with this Core.
- **Some extras only switch on with the mod that uses them.** For example, the movement extras need Fires Advanced
  Player Movement, and the storage features need Fires Storage Network. Without those mods, those parts of Core stay
  off and the game behaves as normal.

### Snow and swimming

- Deep winter snow sinks and slows you the way the game's own deep snow does. Footprints dig a soft trench of a
  steady width instead of a random pit, and walking the same path again packs the snow down.
- Companions and creatures leave trails too, but no longer stamp over your path while walking beside you.
- Trails only fill back in while it is snowing, and melt away with the snow in spring. Winter cover starts with the
  first snowfall, so autumn no longer turns straight to mud.
- Snow no longer pops in late on freshly loaded ground, cracks and steps at zone borders are gone, cultivated soil no
  longer shows as snow lumps, and the ground goes back to normal after winter.
- Snow stays at the right strength after leaving an Environment Box, including by teleport or flying.
- If Vedr is installed and handles ground snow, Core's ground snow steps aside so the two never fight.
- **New underwater view:** depth fog, light ripples on the sea floor, bubbles and murk. You can no longer walk along
  the sea floor, and the camera switches to the underwater view at a more natural depth.

### Companions

- Druid and Necromancer companions get proper roles and gear rules.
- Companions roll to dodge, side-step enemy swings, and back off to catch their breath when low on stamina instead of
  running back to you.
- Companions stuck in water climb out at the nearest exit.
- Summons such as wolves no longer appear on your player list or get saved with you, and lost summons are found again.
- Companions finish their workstation visits, and the smelter helper fetches what is missing instead of looping.

### Smoother play

- Fixed a short freeze every 10 seconds that got worse the more mods you had, even without companions. The homestead
  needs check now only runs when you have a tamed companion and need-based chores are on. You can switch it off with
  `[Homestead Needs] Needs model`.
- Companions and snow no longer search the whole world for fires, beds, smelters and doors; they keep a list instead.
  Same results, less work. Setting: `[Performance] Scene scans from registries` (on).
- Much less work while terrain loads and while you walk through snow, which fixes long stalls when many areas load at
  once. Towns with many building pieces load faster.
- General speed-ups that belong to Fires Fixes, Fires Ghetto Networking or Fires Easy Bake Meshes have moved out of
  Core into those mods. Install them for those gains; Core keeps working without them.

### Interface

- The config window (F1) opens even with Shift, Ctrl or Alt held, shows each setting's description when you hover
  over it, and clicks no longer fall through to the menu behind it (no more accidental Quit or Save).
- Windows such as the NPC book remember their size and always show their resize grip. Layout files are saved with
  safe names so they survive copying, zipping and cloud sync.
- Long biome names stay inside the banner, the round minimap frame is back to normal, and placed NPCs turn their heads
  to look at you.
- Normal logs are much quieter: a repeating message prints a few times, then only in debug logs. Missing optional
  mods no longer cause warnings.

### Also new

- Copper and iron seeds grow the small ore rocks.
- First person bow: your left arm follows when you look down while drawing.
- Other mods can colour map pin icons and names (for companions and groups).

### Fixed

- A crash when another mod (for example Jewelcrafting, with Fires Easy Bake Meshes installed) met Core while it was
  getting items ready at start-up.
- Far buildings at the back of a hillside town were not drawn; they now show.
- Companions are no longer unloaded by the server's network mod while they should be kept.

### Admin locks

- Each mod now decides for itself which of its settings are admin-locked, and the config window shows that as is.
  Non-admin players can never change server settings, and admin commands now refuse when they can't be sure you are
  an admin.

### Removed

- HD Additions shader support and its config section.
- Core's own copies of speed tweaks that now live in Fires Fixes or Fires Ghetto Networking.

## What you get

- **Config window (F1, or F8 when another configuration manager is installed).** Every installed mod's settings in
  one window. Search across all mods, edit live, reset any setting, and see clashing keybinds. Hover a setting to
  read what it does. Server-locked settings show greyed out, and only admins can change them. Also under Settings >
  Mod Settings, or `va_config` in the console.
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
- **The companion brain** (used by FiresCompanions). Companions pick their fights, block, roll and side-step, use
  their class skills, back off when hurt or out of stamina, and help you when you're hit. They find their way round
  obstacles, use doors, climb out of water, and do chores: gathering, filling chests, repairing and eating.
- **Live config reloads.** Edit a Fires mod's `.cfg` file and the change applies without a restart.
- **Quieter logs.** Fires mods write only warnings and errors by default, and repeating messages are cut short. Set
  Log Level to Info in Core's settings to see more.
- **HD textures.** With HDValheimTextures installed, textures load behind a loading screen instead of freezing the
  menu, at half size by default to save memory.

## What Core does for each mod

Much of Core is shared machinery that other Fires mods build on. Each of these parts only switches on when the mod
that uses it is installed, and each mod keeps its own settings in its own config file. Here is what Core does for them:

- **FiresCompanions:** the companion brain. Fighting, dodging, following, opening doors and finding paths, chores
  (gathering, hauling, repairing, eating), picking up loot, and the companions' rows on the Group HUD.
- **Fires Advanced Player Movement:** the climbing, vaulting, ledge pull-ups, crouching and crawling, double jump, run
  slide and swimming that it switches on.
- **FiresStorageNetwork:** the storage network. One view over every chest in your base, safe taking and depositing
  in multiplayer, favourites, crafting from nearby chests, hoppers that pull in dropped items and sort them into your
  chests, and the Storage Terminal window's look.
- **Fires Easy Bake Meshes:** the snow and lighting information it needs to draw distant buildings.
- **FiresRPGmaker:** NPC behaviour (standing posts, patrol routes, fighting back), the territory plate under the
  minimap, and hiding the HUD while its windows are open.
- **FiresRPGClasses:** class skills that companions use, and the class bars' place in the HUD layout.
- **VAInventory:** the look of the inventory, crafting, chest and companion panels, moving and resizing them in the
  layout editor, hover tips, its help pages, and stopping your keys from moving you while you type.
- **FiresAdminTerrain:** its help pages, shared materials and shaders, the ground snow, saved world data, and key
  bindings.
- **FiresMounts:** its help pages, the flying used by flying mounts, shared mount and creature-variant handling,
  and key bindings.
- **FiresDyes:** the look of the Dyer's Bench window, and keeping glowing gear's own glow when you dye it.
- **FiresCharacterCreator:** the shared Fires window it opens in, keeping gear fitted on reshaped bodies, its help page,
  and carrying old settings over to their new names.
- **FiresLoreBooks:** stopping your keys from moving you while you type in a book or journal, and its window look and
  font.
- **FiresMiniGames:** the camera that frames the arcade, TV and air-hockey screens, holding you still while you play,
  its right-click menu and settings panel, the Tinker's Bench glow, and its help page.
- **VABackpacks:** its help pages, the staff animations and secondary casts, the staffs' link to companion classes,
  holding your keys still while you aim, and the ground rings for placing staff spells.
- **FiresTossinShade:** the shared water and season state, shared shaders, setting presets in the config window,
  fitting hair to reshaped heads, and its help pages.
- **FiresAllTheItems:** its window look, item icons, recipe and creature-drop edits that the server keeps and sends to
  everyone, and its help pages.
- **FiresDebugginMenu:** the Environment Box, the Hammer pieces it adds, its right-click menu, and holding your keys still
  in its windows.
- **Every Fires mod:** the config window and server sync, help pages, shared undo and redo, and the HUD layout.

## Core's own features

These work on their own, without another Fires mod:

- The config window, HUD layout editor and in-game help.
- The context menu and the Environment Box.
- Shared undo and redo.

Climbing, crawling, double jump, run slide and the other movement extras are part of Core but only run when Fires
Advanced Player Movement is installed. Crafting from nearby chests, chest tags and hopper chests run with Fires
Storage Network. Without those mods, movement and chests work as in the normal game.

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
- **Admin checks are strict.** Non-admin players can never change server settings, and admin commands refuse when
  they can't confirm you are an admin.

## Install

- Client **and** dedicated server.
- The server and every player must run the same Core version (0.2.1223) for far buildings to be shared correctly.
- Keep `LiteDB.dll` and `System.Buffers.dll` in the same folder as `FiresUnifiedCore.dll`. A mod manager does
  this for you.
- Update Core together with the Fires mods that use it. If you use Fires Easy Bake Meshes, use 1.2.210 or newer.

## Requires

- BepInExPack for Valheim

## Works well with

- **Fires Advanced Player Movement** for climbing, crawling, double jump, run slide and swimming extras.
- **Fires Storage Network** for crafting from chests, chest tags and hoppers.
- **Fires Easy Bake Meshes** (1.2.210 or newer) for the far view of distant buildings.
- **Fires Fixes** and **Fires Ghetto Networking** for the speed-ups that used to be inside Core.

## Issues / questions

https://discord.gg/zQRgHbqms4
