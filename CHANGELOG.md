* v0.2.97 - a creature rule can set health to an exact number
  - a creature rule can now set health to an exact number instead of only multiplying the prefab's own value; the multiplier is still used when no exact number is given, so rules saved before this keep working unchanged
  - the number is the health of an unstarred creature, and stars and world level scale up from it as usual
  - what a star is worth is editable too: health and damage each have a per-star multiplier, set to vanilla's x2 health and x1.5 damage unless you change them
  - preset creatures are left alone by all of it, as they are by every other per-species rule - a preset is exactly what it was set to
* v0.2.80 - the Fires config window replaces ConfigurationManager, config file editor, hidden settings, bigger readable text
  - NOT YET WRITTEN UP: the changes between v0.2.61 and v0.2.79 from the other work streams (performance, banners, companions) still need their entries here; the notes below cover the config window only
  - the Fires config window now covers EVERY installed mod, not just the Fires family, so shudnal's ConfigurationManager is no longer needed beside it; a "Fires only" tick narrows the list back down
  - it opens with F1, or with F8 if another configuration manager is installed and would claim F1; the console command va_config still works
  - Settings in the main menu and in the pause menu both gained a "Mod Settings" entry, so the window is reachable without knowing a hotkey
  - the window reads the same setting names, ordering, descriptions and advanced or hidden marks that mods already ship for ConfigurationManager, so other people's mods look right in it with nothing to change on their side
  - settings the server has locked now show greyed out and refuse edits, instead of looking editable and quietly snapping back to the server's value
  - settings with a list of choices open a proper drop-down instead of cycling one value per click, and a setting that holds several options at once shows one tick box per option
  - settings with a range show a slider and a number box you can type an exact value into
  - colour settings open a picker with red, green, blue and transparency sliders; position and rotation settings get one box per number
  - keybinds: click the value box, press the keys you want including modifiers, then Apply; Esc cancels and X clears the bind
  - the window's hotkey opens it while you are moving - before, holding a movement key stopped it firing
  - a Files tab edits the raw .cfg, .yml and .json files under BepInEx/config from in game, and saving one reloads the mod that owns it
  - double-click a setting's name for a window with its full description, type and default, which is where the long descriptions the list has to cut to one line can be read
  - server admins can hide settings from ordinary players with BepInEx/config/FiresRPGmaker/UI/hidden_settings.yml, one ModGuid=Section=Key per line and * allowed for any part; admins and the host still see everything, and the server sends the list to every player
  - A- and A+ in the title bar scale the window and its text together, and the text is redrawn at the larger size rather than stretched, so it stays sharp; clicking the percentage resets it to 100%
  - the window can pause single player while it is open, if you turn that on
  - the tick boxes for Advanced, Keybinds and All mods replace the buttons that used to report their own state, and Keybinds narrows the whole window to keybind settings across every mod
  - moving and resizing the window no longer loses the drag after a frame
  - setting descriptions no longer run underneath the Reset buttons

* v0.2.60 - safer building wear, faster loading of built-up areas, main-menu tools for mods, lighter file watching, better companions
  - heavily built areas load and unload faster: each building piece no longer copies its whole area's piece list when it appears or goes away (a vanilla cost that grew with the square of the pieces in one area)
  - buildings don't wear or collapse while the ground under them is still loading
  - Fires mods can add right-click options to worlds on the main menu, and show pop-up dialogs with text boxes, checkboxes and dropdowns
  - the right-click menu on the main menu's world list opens at the cursor instead of off screen at resolutions above 1080p
  - every mod's file watchers (config hot reload and the like) now run on Windows file notifications instead of re-scanning their folders every 0.75 seconds; [File Watching] turns this off or sets how long changes are gathered before they reach the mods
  - a file saved again with the same contents no longer counts as a change, so mods that re-save their config after each reload (such as MaxPlayerCount) don't reload over and over
  - Core's exception relay now prints every inner exception (type, message and stack trace) and names the mod behind TypeInitializationException / TargetInvocationException wrappers, so errors such as a failed static initializer show their real cause instead of 'unknown'
  - a second noisy warning filter now reports once, and its counts join the status box
  - status box lines that are too wide now wrap instead of being cut off
  - wild and hammer-placed companions spawn with gear from the real 1.0 item list, matched to where they spawn (Meadows to Deep North) and to their role: guardians with a shield, rangers with a bow or crossbow and matching arrows, mages and clerics with staves, and so on; now and then a lucky one has gear from the next biome up
  - companions no longer carry the rare wooden training weapons
  - companions shooting bows and crossbows fire the arrows or bolts they carry, with their damage (fire arrows burn, frost arrows chill), instead of always wooden arrows
  - giant and dwarf companions' weapons, helmets, hair and beards now grow or shrink with them, instead of staying player-sized
  - one-handed weapons companions carry on their back show up again after a companion's body changes
  - companion shields and parries now actually block damage, so guardians and paladins hold the line
  - the high-level companion passives work: Iron Wall, Unyielding, Rampage, Execute, Death Wish and Chain Casting
  - the archetype and hybrid class you pick in a companion's radial menu now stick
  - companions become their hybrid class as soon as they reach level 25, instead of at their next gear change
  - companion ability effects only play near players, so fights at a distant base are no longer heard in your ear, and they no longer show doubled in multiplayer
  - ability visuals that went missing in 1.0 are back
  - companion group heals no longer heal extra for every player online
  - wild companions' names now match their bodies
  - companion helmets, hoods and hair attach to the skeleton that actually moves
  - followers pulled back to you after you fly or teleport away land on the ground beside you instead of dropping from your height
  - the status box shows how much time AI pathfinding takes
  - following companions now actually arrive at your side after you fly or teleport away, skylands included, instead of staying where they were
  - companions no longer get hair or beard styles meant for other characters, which floated above their heads; ones that had one get a proper style
  - in multiplayer each companion's idle life runs on one machine, so its emotes and chores no longer repeat for every player nearby
  - companions no longer lose or duplicate items when they put things in chests or take them out in multiplayer, leave chests alone while someone has them open, and respect private chests and wards
  - companions organise chests the way players do: ore and coal by the smelter, bars by the forge, wood and stone by the workbench and stonecutter, raw food by the cooking stations, seeds by the fields, clothes and armour in the wardrobe or by the beds, and trophies and valuables in chests of their own
  - companion jobs work in 1.0: smelters, kilns and windmills no longer destroy ore or wood, cooking stations are tended properly, fires get their own fuel, farming knows the 1.0 crops (kale, oats, poteitr), and gathering uses real tools
  - tools, hammers, cultivators and fish that companions make or catch no longer vanish when you log out
  - companions no longer fight with shovels, scythes or tankards, chase shadow creatures they can't hit, or throw frost-orb staves at enemies
  - companions no longer slide across the ground while emoting, and get up properly from thrones, ship seats and divans
  - patrol and quest NPCs walk instead of gliding for other players
  - companions show their work poses at workbenches, forges and cauldrons, jump over obstacles again, and are never unstuck into lava or tar
  - the radial menu's wander switch works
  - with VikHavn Combat Moveset Additions installed, following companions crawl beside you when you go prone, at your crawl speed, and duck lower while sneaking
  - sneaking companions are harder to spot: their stealth now uses their Sneak skill and status effects, and crawling hides them better still
  - companions turn their heads smoothly to look at you instead of whipping them from side to side
  - companions without a weapon really punch and kick now, with the player's own moves
  - with VikHavn Combat Moveset Additions installed, companion kicks launch foes the way yours do, bare-handed companions grab stunned foes, hold them for everyone to hit and then kick them away, and tank companions' taunts are VikHavn taunts (the rune, and the foe stays on them)
  - tank companions roar, flex or call enemies over when they taunt, and every player sees it
  - crawling and sneaking companions follow VikHavn's Sneak Stance settings from the server
  - a command to a companion works even when another player's game was running that companion
  - companions sent fishing find water at the world's real water level
  - woodcutting companions skip trees their axe is too weak to chop
  - companions tending a fire only start a batch they can finish, so food isn't left burning
  - Fires menus and dialogs no longer fill the log with 'The LiberationSans SDF Font Asset was not found' warnings
  - arriving somewhere no longer freezes the game while your companions are found, and big worlds no longer hitch every few seconds from keeping track of companions
  - companions no longer flash invisible or fill the log with "vertex stride" errors when they appear with a female body
  - console summary boxes line up in Windows Terminal, including the ones whose icon it draws narrow
  - quieter logs: pathfinding measurement starts with one line, and old login diagnostics are gone
  - custom dungeons no longer rebuild themselves (with an error and a two-second freeze) when they spawn far from the host
  - a unique dungeon such as the mausoleum crypt now places once on worlds made before it was installed: further out when the middle is taken, never on a player's build or across a generated river, and in a fresh zone of its own on one-zone-at-a-time worlds (not over a converted world's explored land)
  - custom dungeon rooms can carry vanilla props (lit torches) that every player sees, with no extra network objects
  - dungeon environment-box debug lines only print with verbose logging on
  - companions' skills, Strength and Intelligence, class damage bonuses and critical hits now count on every attack, not just some; tamed companions hit harder as they level, the same way their health grows
  - companion armor works like a player's: it counts difficulty, backstabs and staggers the way yours does, upgraded and world-level armor is stronger, and resist meads and gear resistances no longer stack beyond what a player gets
  - heavy armor slows companions and light gear speeds them up, and the Speed attribute now makes them faster
  - companion skill bonuses from armor sets and meads count, and skill-gain bonuses apply
  - companions heal from food at the same rate you do (it was far faster), food wears off gradually like yours, and eitr regeneration uses gear and effects
  - companions drink healing meads for their real effect, and stamina and eitr from meads and effects reach them
  - gear and effects that change attack, block and dodge stamina costs now apply to companions
  - Ashlands companions' fire immunity also stops them catching fire
  - the companion stats page shows the real strength of class abilities
  - companion armour wears out when they are hit, like yours, and they repair it at a workbench; the wear is saved with the companion
  - companions drink any mead that suits the moment: healing when hurt, a resistance against what is hurting them, stamina or eitr when low, and attack meads in a fight
  - companions eat by the same rules you do: they top a food up once it is past halfway, and a new food replaces their emptiest one
  - a fortified companion really gets the armor Fortify promises, so tanks and paladins shrug off hits while it lasts
  - less stutter from physics: the game no longer creates a throwaway object for every collision it reports (about half a million of them in a quarter hour); [Performance] Reuse Collision Callbacks turns it off

* v0.2.35 - one status box for every Fires mod
  - Fires mods share one status box in the log, about once a minute, instead of each printing its own repeating lines; turn it off or change the interval with [General] StatusBanner and StatusBannerSeconds
  - fewer repeated log lines
  - settings with a fixed list of choices, like Fires Tossin Shade's presets, show that list in ConfigurationManager

* v0.2.33 - recipe and creature editing, orphan cleanup, no more pink textures
  - server admins can edit crafting recipes, and creature drops, stars, health and damage, in game with Fires All The Items; the server sends them to every player
  - new admin commands zdo_scan_orphans and zdo_clean_orphans find and remove saved objects left behind by removed mods
  - setting presets and a Simple/Advanced switch in the Fires config window, and locked settings show greyed out
  - big world files a mod needs are sent to each player once instead of every join
  - items and effects from mods built on the 1.0 game files no longer show up pink
  - warnings and errors from Fires mods show in the log again
  - server settings sync properly: empty and enum values, cleared when you leave, and locked settings stay locked
  - companions' dresses and capes work again, and no more error at logout
  - the BepInEx console window is written in the background, so a busy console no longer freezes the game
  - Fires mods that add their own build pieces load faster

 v0.2.10 - no more config reload spam
  - fixed Fires mods reloading their config every couple of seconds while connected to a server
  - other mods can now keep their builds out of Easy Bake Meshes

* v0.2.8 - settings that arrive, and fewer load-time complaints
  - server settings now reach you as you join, instead of only when someone changes one afterwards
  - settings a server locks stay locked for players and stay editable for admins, which was the wrong way round in some cases
  - your admin status is sent as you join, so admin-only settings and tools work straight away
  - the empty group box under the minimap no longer shows up when you have no companions
  - no more load-time errors or warnings from tree logs, terrain compilers and a few other objects while prefabs are warmed up
  - removed four config window settings that did nothing
  - code cleanup

* v0.2.5 - updated for Valheim 1.0

* v0.1.86 - maintenance and fixes

