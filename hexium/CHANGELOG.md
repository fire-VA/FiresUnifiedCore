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
