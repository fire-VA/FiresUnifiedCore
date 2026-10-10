# FiresUnifiedCore

**Updated for Valheim 1.0.**

The shared foundation of the Fires mod family. Every Fires mod (FiresAdminPrefabs, FiresAdminTerrain,
FiresEasyBakeMeshes, FiresRPGmaker, FiresCompanions and the rest) runs on it, so install it wherever you install
one of them: on every client and on the dedicated server.

Most of it is plumbing you never see: config sync and admin gating, networking helpers, the companion/NPC
engine, a LiteDB storage vault, and the UI and input framework. A few pieces you use directly.

## What you get

- **Config window (F1, or F8 when another configuration manager is installed).** Every installed mod's settings in
  one window. Search across all mods, edit live, reset any entry to its default, and see changed values tinted gold.
  Server-synced settings show the server's value, and on a locked server only admins can change them. It also finds
  keybind conflicts. `va_config` opens it from the console, and Settings has a "Mod Settings" entry.
- **In-game help.** A "? Help" button on the pause menu. Each Fires mod documents its own features there.
- **Context menu.** Hold Alt+Shift and right-click companions, chests and other objects for their actions.
  Rebind the modifiers under `[ContextMenu]`.
- **Group HUD.** Companions and party members listed under the minimap with health, stamina and eitr bars.
- **Environment Box.** A build piece that forces its own weather, skybox and mood inside a volume you size.
- **Live config reloads.** Edit a Fires mod's `.cfg` on disk and the change applies without a restart.
- **The companion brain** (used by FiresCompanions). Companions pick their fights, keep their weapon's range,
  block, dodge, use their class skills, back off when hurt, and help you when you're hit. They walk round
  obstacles and hills, use doors, jump steps, and do chores: gathering, filling chests, repairing and eating.
- **World memory.** Core remembers where resources were found and which enemies were met in each world, under
  `BepInEx/config/FiresCore/WorldMemory`.
- **Quieter logs.** Fires mods write only warnings and errors by default. Set `[Logging] Log Level = Info` in Core's config
  to see what they are doing, or `Verbose` for everything.
- **HD textures.** With HDValheimTextures installed, textures load behind a loading screen instead of freezing the
  menu, at half size by default to save memory. Set `[HD Textures] Size = Full` for full size.

## For server admins

- **Move config files without FTP.** `pushconfigs <pattern>` sends config files from your machine to the
  dedicated server, `pullconfigs <pattern>` fetches the server's copies, and `deleteconfig <pattern> confirm`
  removes them (with a backup). Patterns match an exact file, a folder or a `*` wildcard. `.cfg` files are merged
  entry by entry, so server-only keys survive a push. Admin only, and `.dll`/bundle files can never be sent.
- **Recipe and creature overrides.** Recipe and creature edits made with a Fires editor are stored on the server
  and sent to every player as they join, so everyone crafts and fights with the same numbers.
- **Orphaned objects.** `zdo_scan_orphans` lists saved objects whose prefab no longer exists (for example after
  removing a mod); `zdo_clean_orphans` deletes them. Back up the world first.
- **The item vault rolls back with the world.** After a crash, the vault (bank, mail, marketplace, guilds) goes
  back to the copy taken at the world save the server loaded, so items can't be duplicated or lost. The vault as
  it was is kept beside it. `[Vault] Roll back with the world` turns it off.

## Installation

- Client **and** dedicated server.
- Keep `LiteDB.dll` and `System.Buffers.dll` in the same folder as `FiresUnifiedCore.dll`. A mod manager does
  this for you.
- Update Core together with the Fires mods that depend on it; each one lists the Core version it needs.

## Issues / questions

https://discord.gg/zQRgHbqms4
