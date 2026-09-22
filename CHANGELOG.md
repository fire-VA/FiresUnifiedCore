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

