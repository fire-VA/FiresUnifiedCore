## 0.2.1223

**Important when updating**
- Settings that belong to other Fires mods now live in those mods' own config files. Core's config keeps only its own interface, logging and general options; moved settings are tidied out of Core's file once, and the owning mod picks up your old value. No reset needed.
- Far-building sharing between server and players changed: the server and every player must run this version together. Older far-building cache files refill on their own.
- Some optional features now only switch on when the mod that uses them is installed (for example movement extras with Fires Advanced Player Movement, storage with Fires Storage Network).

### New
- Underwater view (depth fog, light ripples on the sea floor, bubbles, murk) is now part of Core's swimming support. It steps aside on its own if an older Fires terrain mod still draws its own underwater view.
- Far view support for other Fires mods: distant trees and buildings can be drawn as light stand-in images out to a long range, lit like the real world. Everything is off by default and is switched on by the mod that uses it.
- Map pin icons and names can be coloured by other mods (for companions and groups).
- Companions: Druid and Necromancer companions get damage roles and gear rules.
- Companions: rolls to dodge (with a short invincible window), side-step enemy swings, and back off to recover when low on stamina instead of running to you.
- Companions stuck in water climb out at the nearest exit, and as a last resort move to dry ground.
- Copper and iron seeds grow the small ore rocks.
- First person bow: your left arm follows when you look down while drawing.
- Mod authors: a hook so climbing mods can pull the player up onto a ledge using Core's vault (needs Fires Advanced Player Movement).

### Changed
- Fires mods now handle their own settings; Core only reads them.
- Snow: deeper winter snow sinks and slows you the same way vanilla deep snow does; footprints and trails dig a soft trench of a steady width instead of a random 0.5 m to 2 m pit, and repeat passes pack the snow down.
- Snow: companions and creatures leave trails too, including on seasonal winter ground, but no longer stamp over your own path while walking next to you.
- Snow: trails fill back in only while it snows, and melt out with the snow in spring; winter cover only starts with the first snowfall, so autumn does not turn straight to mud.
- Snow: when Vedr is installed and runs ground snow, Core's ground snow steps aside so the two never fight.
- Swimming: you can no longer dive down and walk on the sea floor; the camera switches to the underwater view at a more natural head depth.
- Config window (F1): opens even with Shift/Ctrl/Alt held or after closing with a field selected, shows each setting's description when you hover the row, and no longer lets clicks fall through to the menu behind it (no more accidental Quit or Save clicks).
- Window layouts: the NPC book and other windows remember their size, always show their resize grip, and layout files are saved with safe names so they survive copying, zipping and cloud sync.
- Multi-line biome names stay inside the banner.
- Placed NPCs turn their heads to look at you.
- Normal logs are much quieter: a repeating message prints a few times, then only in debug logs. Missing optional mods no longer produce warnings.
- Admin locks: Core no longer decides whether a setting is locked; each mod's own admin check is shown as is, and non-admin players can never write server settings. Commands that check for admin now refuse when unsure instead of allowing.

### Fixed
- Snow no longer pops in late on a freshly loaded area, showing bare mud first.
- Cracks and steps in the ground at zone borders in snow, trails that left holes in the ground mesh, and dark bare lines along zone edges after a season change.
- Cultivated soil no longer shows as raised snow lumps, and fully reset ground returns to normal snow.
- Ground goes back to its normal look after winter.
- Far buildings at the back of a town on a hill were not drawn; they now show, including for areas you are holding but have not loaded.
- Companion summons (such as wolves) no longer show up on your player list or get saved with you, and lost summons are found again.
- Companions finish their workstation visits properly, and the smelter helper fetches the input it is missing instead of looping.
- A crash when another mod (for example Jewelcrafting, with Fires Easy Bake Meshes installed) met Core's prefab warm-up.
- Far forest stand-ins throwing errors every time the forest grew.
- The round minimap frame is back to its normal layout.
- Interface placement checks no longer fail while the loading screen is still up.
- Companions are no longer unloaded by the server's network mod while they should be kept.

### Performance
- Snow: much less work while loading terrain and walking through snow (only the ground that can change is redrawn, one pass instead of two, and work is spread across frames), fixing long stalls when many zones load at once.
- Building pieces: snow checks per piece are worked out once per frame instead of per piece, so towns load faster.
- General speed work that belongs to Fires Fixes, Fires Ghetto Networking or Fires Easy Bake Meshes has moved out of Core into those mods. Install them for those gains; Core keeps working without them.

### Removed
- HD Additions shader compatibility and its config section.
- Core's own copies of speed tweaks now owned by Fires Fixes / Fires Ghetto Networking (cooking slots, station ranges, terrain snapping, server minimap, physics callbacks, distant sectors, moving-object sync).

### Also in this release (0.2.1219 to 0.2.1223)
- The far view (distant buildings) now lives in Fires Easy Bake Meshes. Use Fires Easy Bake Meshes 1.2.210 or newer with this Core.
- Fixed a short freeze every 10 seconds (33 to 90 ms) that got worse the more mods you had, even with no companions. The homestead needs model now only runs when you have a tamed companion and need-based chores are on, and you can switch it off with [Homestead Needs] Needs model.
- Companion and snow code no longer search the whole world for fires, beds, smelters and doors; they keep lists instead. Same results, less work. Setting: [Performance] Scene scans from registries (on).
- Fixed snow staying at full strength after walking through overlapping environment boxes, or after teleporting or flying out of one.
- Fixed ground snow treating a world as full winter for a moment while Vedr was still starting up.

## 0.2.1086
- Settings that belong to other Fires mods now live in those mods' own configs; your saved values are copied over once. (Core)
- Fixed the homestead map. (FiresCompanions)

## 0.2.1085
- No more "Could not find type named ..." warnings in the log when an optional Fires mod (or another mod Core works with) isn't installed: Core now checks for those mods quietly and just skips that feature. (Core)

## 0.2.1084
- Snow now builds up the way the game does it in the Deep North: winter, storm and mountain snow raises the game's own terrain, the build-up is the same everywhere in the world, and zone borders line up with no tears or seams. (snow)
- Walking leaves snow trails in Mountain and seasonal winter snow as well as the Deep North. Trails fill back in over time, and in spring they melt back to grass at the same pace as the ice. (snow)
- Snow fills in tightly around buildings and stays off paved ground. Only a real roof keeps snow out, and town props such as wells, low walls and steps are no longer buried. (snow)
- Snow fills in nearest-first, so the ground around you is ready before distant zones, and gardens you cultivate are no longer reset by snow refills. (snow)
- Deep snow no longer forces the camera into first person. (camera)
- First person: your shoulder and cape no longer block the view during attacks, the camera no longer flickers, and your shield and bow turn mostly see-through while you block or aim. (camera)
- Hotbars and class bars sit at the bottom left above the health HUD by default, the class hotbar is back in its old spot, and the ammo bar no longer jumps to the middle of the screen. (UI)
- The "Unlocked: double jump" and run slide popups show only once per character, not on every login. (movement)
- Companions find you right after a teleport, play the equip animation when they put on rings and necklaces, stop repeating "is staying here" on automatic trips, and no longer swap weapons over and over while idling in town. (companions)
- Frozen inland water shows slush and floating ice chips that drift with the flow. (world)
- Settings that belong to other mods now live in those mods: snow in FiresAdminTerrain and FiresSnowmen, storage in FiresStorageNetwork, swimming and movement in FiresAdvancedPlayerMovement, possession in FiresCharacterCreator, group HUD in FiresRPGmaker, and the terrain height override in FiresAdminTerrain. Your old values carry over. (Core)
- Core no longer unlocks mineral seeds or other pieces unless the mod that uses them is installed. (Core)
- Many performance fixes for snow, terrain, companions and leaderboard saving, plus crash fixes in snow building. (Core)

## 0.2.986
- Resize grips and drag handles on HUD panels only show in the Esc menu, a layout editor, or while you hold Alt+Shift ("Grips only in layout modes"), and a panel's grip sits on its own corner. (UI)
- First person: your arms reach further into view so you see your hands and weapon ("First person arm reach (deg)", "First person upper arm stretch"), your backpack and back items are hidden from your own view, and your weapon and armour no longer flicker in and out. (camera)
- Companions keep fighting when you walk a little way off; they only break off to follow you when you are far away, and never while their foe is attacking your side ("Combat ignores the 12 m moving-owner recall"). (companions)
- Mages hold their staff again instead of fighting with their fists. (companions)
- Dead or stored companions come back with you when you log in again after logging out, quitting or a crash. (companions)
- No more duplicate companions after logging in: a companion already in the world is never spawned twice, and duplicates are merged back into one with all their gear and items ("Restore dedupe"). (companions)
- Companions leave neutral creatures (like Dvergr) alone unless the creature is angry, attacks your side, or you hit it yourself in the last few seconds; a splash hit no longer starts a fight. (companions)
- Companion combat movement is back: companions strafe and dodge in fights again instead of standing and swinging once. (companions)
- Companions left behind by a long teleport are pulled back to you; a respawned companion no longer appears without its gear, and a dead companion keeps its greyed group bar with the respawn countdown. (companions)
- New first-person mode ("First person mode", off by default): scroll all the way in; you see your own body, armour and held items, the camera follows your head, and leaving restores everything. Options for field of view, how far the body pulls back and leans when you look down. (camera)
- Snow: ground snow builds up through the year and in storms, with deeper snow on mountains ("Max depth Mountain" 0.8 m) and winter snow on voxel ground ("Winter snow depth (m)" 0.3); snow stays out of closed rooms ("No snow in closed rooms"); walking leaves trails that no longer flicker, nearby snow no longer pops in, and snow settings are synced from the server. (snow)
- Homestead and Job Board: map work zones and areas, tabs, reports, taming, hunting, mining (prefers ore when a smelter runs low), smelter filling, deposit sorting, replanting, and a defence/raid-rebuild option (off by default). (companions)
- Companion fixes: no more double hits in melee, better pathing near water and thin ice, walking upstairs, passing through each other, and dropping chores while fighting. (companions)
- New minimap clock option "Minimap clock shows": SunTime (default) or GameTime. "Skip Intro Cinematic" is now "Skip new world intro cutscene" (old value kept). (UI)
- Mineral seeds now also come from ore deposits, with the full-rock bonus growing with your Pickaxes skill; see-through inland ice with a smooth freezing edge. (world)
- The shovel only flattens the area around where you click; the terrain height override ("HeightmapOverride" Enabled) is now off by default and a client follows it only when the server says so. Many performance fixes for snow, companions and windows. (Core)

## 0.2.823
- New Homestead Job Board: send companions on farming, hunting, fishing and mining trips, on command or every morning, pick the area on the map, and see the jobs on the map. Trips keep going after you log off, and daily jobs run while you are offline. (FiresCompanions)
- Companion farming plants each crop with the game's own grow space, tends unhealthy plants and gets faster with skill. (FiresCompanions)
- Companions reach chests and stations only without a wall in between, walk on through open doorways, step over door thresholds and no longer stop in doorways. (FiresCompanions)
- Companions animate properly again. (FiresCompanions)
- Mineral seeds: plant ore deposits at your base that regrow. (Core)
- Admins can mark rocks, trees and pickables to respawn after they are destroyed. (Core)
- Snow builds up on the ground through the year and in storms, up to waist deep, when dynamic snow is turned on (off by default). (Core)
- A hopper inside a storage network sorts into the whole network. (FiresStorageNetwork)
- Climbing: hold Jump to stay on the wall and let go to drop; no more vaulting up through ceilings. (Movement)
- Windows remember where you dragged them; Esc closes only the window on top. In the classic (non-unified) UI, panels no longer snap together, a press drags only the panel under the pointer, and the day and time sit under the minimap. (UI)
- A new install starts with a ready-made unified UI layout. (UI)
- Admin checks keep crossplay players' platform ids as they are. (Core)
- Groundwork for curved "ring" worlds: gravity, falling, building and companions can follow the world's own "up" there; normal worlds are unchanged. (Core)
- Remote storage finds your home terminal again after a server restart, and works right next to home. (FiresStorageNetwork)
- Companions catch up to you again when you run or ride far ahead. (FiresCompanions)
- Companion backups are saved again when another player's game is running your companion. (FiresCompanions)
- Items can be taken out of a companion's bag again: the item no longer drops off the cursor, and whoever opens a companion owns it while editing, so nothing is duplicated or lost. (FiresCompanions)
- Companions no longer run back and forth between you and the same enemy: an enemy they left to follow you is ignored for a while, and they only start a new fight close to you, unless it attacks you or them. They also hold still while you look at or open them. (FiresCompanions)
- Companion teleports and abilities right after a long jump are no longer blocked by the network checks. (FiresCompanions)
- Remote storage from far away reads your home chests from the server instead of showing a list that empties. (FiresStorageNetwork)
- A companion's weapon swing trail no longer stops working during slow swings. (FiresCompanions)
- Remote storage now reaches chests that were last opened by a player who has since walked far away. (FiresStorageNetwork)
- Chest writes never exceed the item's normal stack size. (FiresStorageNetwork)
- Chests can be marked with a Locked tag; tags show a display name. (FiresStorageNetwork)
- A HUD panel dragged near the screen edge snaps to it only when you let go, so it can be moved along the edge freely. (UI)
- Climbing locks on only at the top of a ledge; double jump puff scaled down. (Movement)
- Companions recover when stuck while following, step aside from their owner's path, and haul across owners' chests. (FiresCompanions)
## 0.2.735
- Companions fight better: they chain their weapon's combos, swing again as soon as the last swing ends, use kicks and special attacks only when it makes sense, and dodge when an enemy starts a swing.
- Hitting your own companion by accident no longer turns it against you; your party's attacks don't hurt your companions.
- Companions stand up before starting a job instead of chopping or hauling while sitting, and opening one companion's inventory no longer freezes the others in place.
- Companions open doors properly, and loot they pick up no longer flies away.
- Hopper chests: any container can become a hopper (admins: "Make hopper" on the chest panel or the fires_hopper command). A hopper pulls items dropped near it and sorts them into nearby chests by their tags or by what they already hold. Its radius, filter and sort switch sit on the chest panel.
- Double jump now plays a puff and a sound at your feet.
- The Group HUD shows names in full and keeps status icons in their own rows; vanilla HUD pieces start at the game's own spots.
- Fixed config values not reaching players who join a server, and a few crashes when travelling between worlds.
## 0.2.670
- A new install starts with a ready-made HUD and inventory layout, and hidden elements (like a closed chat) no longer get saved in odd spots.
- The group HUD sits centred under the day/time panel; drag it somewhere else and it stays there.
- Panels resize from their own corner (double-click the corner to reset the size), and NPC book and dressing room screens keep one size.
- Fixed the minimap's + and - buttons not responding.
- Companions keep all their gear when they die, come back reliably (even after a restart), Recall works on dedicated servers, and their health bar no longer sticks at half.
- NPCs standing at their post now fight back when attacked.
- Restyled crafting and chest panels, one look for all switches and sliders, and a "Don't show again" button on the keybind conflict popup.
- Mod key combos (like Alt + a key) no longer also trigger the game's own action on that key, and Creatures of Origins races can be used as player, NPC and companion models with hair, beards and helmets fitted to their heads.

## 0.2.616
- Fixed a startup crash where a config file locked by antivirus or a sync tool stopped mods loading.
- Added a Player tab to the F1 hub, with Stats, Appearance, Classes, Leaderboard and Achievements.
- Simpler mod settings: short plain descriptions, with advanced options under Show Advanced.
- HUD panels can be dragged and resized without opening the layout editor.

## 0.2.588
- Crafting from nearby chests now also works at other Fires mods' crafting stations.

## 0.2.586
- Press Esc to drag any HUD element where you want it and resize it from its corner; the Edit layout button opens the full editor.
- HUD elements from other Fires mods can be moved and resized too, and Reset puts everything back.
- One inventory slot look everywhere: a dark square with a gold border.
- Works with Jotunn mods that add their own build categories.
- The Alt+Shift right-click menu is now off by default (turn it on in the settings).
- Smoother combat aiming and climbing, better-fitting monster armour, companion fixes and better performance.
- Fixed the old build menu showing up next to the new one, and the Achievements page emptying.

## 0.2.390
- Minor fixes.

## 0.2.389
- Companion and NPC fixes, including a companion equipment fix.

## 0.2.387
- Settings for other Fires mods only show when those mods are installed.
- One undo and redo (numpad 7 and 9) shared by all Fires mods.
- Added a climbing on/off setting, plus crouching and crawling under low ceilings.
- Panels hide together with the HUD.
- Quieter logs and fixes for companions, camera mods and the settings menu.

## 0.2.277
- Fixed other mods' weapon and item textures being changed.
- Companion fixes.
- Added a Log Level setting.

## 0.2.219
- The Fires config window (F1) covers every installed mod, with search, sliders, colour picker, keybind capture and a list of clashing keys.
- Settings the server locks show greyed out for players.
- Smarter companions: they follow, fight, block, eat, repair and do their jobs properly in Valheim 1.0.
- Joining a server is faster, with no long freeze when using HD textures.
- The server's item vault rolls back with the world after a crash, so items can't be duplicated or lost.
- Heavily built areas load faster, and buildings no longer collapse while the ground is still loading.
- Characters gather snow on their gear in the Deep North and shed it indoors.
- Fixed pink items from Valheim 1.0 mods and custom dungeons losing their rooms on reload.

## 0.2.10
- Fixed Fires mods reloading their settings every couple of seconds while on a server.

## 0.2.8
- Updated for Valheim 1.0.
- Server settings now reach you as you join, and locked settings stay locked for players.
- Admin-only tools work straight away when you join.
- The empty group box under the minimap no longer shows when you have no companions.

## 0.1.36
- First release.
