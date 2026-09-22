# FiresUnifiedCore

**Updated for Valheim 1.0.**

The shared foundation of the Fires mod family. Every Fires mod (FiresAdminPrefabs, FiresAdminTerrain,
FiresEasyBakeMeshes, FiresRPGmaker, FiresCompanions and the rest) runs on it, so install it wherever you install
one of them: on every client and on the dedicated server.

Most of it is plumbing you never see: config sync and admin gating, networking helpers, the companion/NPC
engine, a LiteDB storage vault, and the UI and input framework. A few pieces you use directly.

## What you get

- **F8 config window.** Every Fires mod's settings in one window. Search across all mods, edit live, reset any
  entry to its default, and see changed values tinted gold. Server-synced settings show the server's value, and
  on a locked server only admins can change them. `va_config` opens it from the console.
- **In-game help.** A "? Help" button on the pause menu. Each Fires mod documents its own features there.
- **Context menu.** Hold Alt+Shift and right-click companions, chests and other objects for their actions.
  Rebind the modifiers under `[ContextMenu]`.
- **Group HUD.** Companions and party members listed under the minimap with health, stamina and eitr bars.
- **Environment Box.** A build piece that forces its own weather, skybox and mood inside a volume you size.
- **Live config reloads.** Edit a Fires mod's `.cfg` on disk and the change applies without a restart.

## For server admins

- **Move config files without FTP.** `pushconfigs <pattern>` sends config files from your machine to the
  dedicated server, `pullconfigs <pattern>` fetches the server's copies, and `deleteconfig <pattern> confirm`
  removes them (with a backup). Patterns match an exact file, a folder or a `*` wildcard. `.cfg` files are merged
  entry by entry, so server-only keys survive a push. Admin only, and `.dll`/bundle files can never be sent.
- **Recipe and creature overrides.** Recipe and creature edits made with a Fires editor are stored on the server
  and sent to every player as they join, so everyone crafts and fights with the same numbers.
- **Orphaned objects.** `zdo_scan_orphans` lists saved objects whose prefab no longer exists (for example after
  removing a mod); `zdo_clean_orphans` deletes them. Back up the world first.

## Installation

- Client **and** dedicated server.
- Keep `LiteDB.dll` and `System.Buffers.dll` in the same folder as `FiresUnifiedCore.dll`. A mod manager does
  this for you.
- Update Core together with the Fires mods that depend on it; each one lists the Core version it needs.

## Issues / questions

https://discord.gg/zQRgHbqms4
