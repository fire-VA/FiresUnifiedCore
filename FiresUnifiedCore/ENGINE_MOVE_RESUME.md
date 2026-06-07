# Engine Cluster Block-Move — RESUME (updated 2026-06-06, Option A)

## ✅ PHASE 4 COMPLETE — CORE GREEN (224 → 0). RPGMaker also GREEN.
The whole NPC engine cluster compiles in Core (`FiresCore.Npc.*`, namespace-only, identifiers stay `Companion*`, all functional strings intact). Every cross-assembly seam is bridged.
- **Bridges built in `FiresCore.Bridge`** (providers wired in Phase 5; all null-safe/no-op until then): `NpcModeBridge` (OpenConfigPanel/Interaction/Territory), `NpcInteractionBridge` (HasController/ModelOverridePending/HoverText/StaticInteract), `NpcUiBridge` (ShowInventory/InventoryOpenFor/StatsOpenFor), `CompanionVaultBridge` (Get/Save/Remove/IsVaultAvailable + PlayerAnnouncedToServer + RestoreReadyForPeer gate), `CompanionEventBridge` (recruited/archetype/hybrid), `NpcConfigBridge` (GetFloat/GetBool/IsAdmin), `NpcAssetBridge` (LoadPrefab), `NpcFashionBridge` (ColorToString/ApplyHair/ApplyBeard), `NpcHostBridge` (RegisterServerRpcs/ResetServerRpcs/PushConfigsToClient), `NpcCompanionBridge` (pre-existing).
- **Pulled from Core (FRONTEND — stay RPGMaker; FiresCompanions has own; ⚠ preserve in Phase 6):** `StaticNpcInitializer`, the 4 `Npc/UI/Companion{Roster,Inventory,Stats,Attribute}Screen.cs`, `CompanionInventoryPatches.cs`.
- RPGMaker green: its `CompanionController` `using FiresCore.Npc;` was narrowed to `using NpcSaveState = FiresCore.Npc.NpcSaveState;` (killed an NpcVisEquipment ambiguity now that Core also has NpcVisEquipment).

## ✅ PatchAll DORMANCY — GATED (was the #1 runtime hazard; resolved 2026-06-06)
The cluster's `[HarmonyPatch]` classes (all of `FiresCore.Npc.*`, incl. nested) are now excluded from Core's auto-patch, so they stay compiled-but-DORMANT and don't double-patch the live RPGMaker. Mechanism: `FiresMod` gained `protected virtual IReadOnlyCollection<string> DormantPatchNamespaces => null` (default null ⇒ original `Harmony.PatchAll`, so every OTHER Fires mod is unchanged). When non-empty, FiresMod patches each type via `PatchClassProcessor` (faithful to PatchAll) **except** types whose namespace is/under a dormant entry. `FiresUnifiedCore` overrides it to `["FiresCore.Npc"]`. Core green; this Release build redeployed the gated Core.dll to BlueHills (now safe).
**⚠ Phase-5 hand-off:** when RPGMaker drops its cluster and adopts Core's, **REMOVE the `["FiresCore.Npc"]` override** in `FiresUnifiedCore.cs` so Core's cluster becomes the live engine + its patches activate. (Runtime-verify via a BlueHills load — should load clean now.)

---
## (historical) the move + bucket work that got here

## State (what's DONE)
- 226 cluster files in `FiresUnifiedCore/Npc/`, **namespace-only** rewrite (`VerdantsAscent.Modules.Companions`→`FiresCore.Npc`, literal/case-sensitive). **NO identifier rename** — all functional strings (prefab names `"CompanionNpc"`, `companion_*` ZDO keys, RPC names) are INTACT. (The earlier case-insensitive bulk-rename that mangled ~262 strings was fully reverted + redone clean.)
- `NpcVisEquipment` + `VisEquipmentCompat` moved to Core (engine infra; namespace-only).
- 4 pre-existing Core files preserved: `NpcSaveState`, `NpcPrefabSetup`, `NpcType`, `SavedNpcData`.
- **EquipmentSlot** interop repointed to the cluster's own `CompanionInventory.EquipmentSlot` (severed from RPGMaker UI screen).
- **Territory** redirected to existing `NpcModeBridge.GetTerritoryRadius/Name` (CompanionNpcModule + CompanionSettings).
- Dead `using VerdantsAscent.*` lines stripped from all cluster files.
- **(A) `FiresCore.Bridge.NpcInteractionBridge`** created (probes: `HasInteractionController`, `HasPendingModelOverride`); wired NpcVisEquipment + CompanionPatches probe sites.
- **(B) CompanionNpcModule** interaction region (~187 lines of Marketplace glue) deleted from Core; `OpenNpcConfigPanel`/`HandleNpcInteraction` now delegate to existing `NpcModeBridge.RaiseOpenConfigPanel`/`RaiseInteraction`. The deleted Sync/Handle logic lives in **pristine RPGMaker source** for the Phase-5 provider.
- **(B) StaticNpcInitializer REMOVED from the Core move** — it's Marketplace-only glue (NpcController + SavedNpcManager; invoked ONLY by VAPieceManager; zero Core-code dependents — CompanionAI/CompanionNpcModule refs were comments). **It stays in RPGMaker.** ⚠ Phase 6: when RPGMaker's `Modules/Companions/` source is deleted, PRESERVE `NpcMode/StaticNpcInitializer.cs` (relocate it under a Marketplace folder).

## Remaining for Core green: the Vault bridge (cross-convo — proposed, awaiting Companions confirm)
Only `VaultOfKnowledge` refs remain (~13 data sites + the player-announced patch). Proposed `FiresCore.Bridge.CompanionVaultBridge` (matches the Companions interim name) — see the handoff post for the full shape:
- Data provider: `Func<bool> IsAvailable`, `Func<long,List<CompanionSaveData>> GetCompanions`, `Action<long,CompanionSaveData> SaveCompanion`, `Action<long,string> RemoveCompanion`. (`CompanionSaveData` is now a Core type.)
- Player-announced hook: `event Action<long> PlayerAnnouncedToServer` (replaces CompanionRestoreService's Harmony patch on `VaultOfKnowledge.RPC_AnnouncePlayerInfo`; provider raises it server-side post-announce).

**STATUS 2026-06-06 (confirmed + wired):** `CompanionVaultBridge` built (Companions convo 👍'd the shape as-is). Data-provider sweep DONE + green across 8 files (CompanionPatches, CompanionVault, CompanionSavedDataResolver, CompanionDeathHandler, CompanionRespawnManager, CompanionController, CompanionRosterScreen, PlayerCompanionMigrator) — `Instance==null`→`!IsVaultAvailable()`, `.GetCompanions`→`GetCompanionsFor`, `.SaveCompanion`→`Save`, `.RemoveCompanion`→`Remove`, all `FiresCore.Bridge.CompanionVaultBridge.*`.

`CompanionRestoreService` DONE (option a landed): patches→`CompanionVaultBridge.PlayerAnnouncedToServer` subscription (via ZNet.Start postfix), coroutine host→`FiresCore.FiresUnifiedCore.Instance`, config-sync gate→`CompanionVaultBridge.IsRestoreReadyForPeer`/`ClearRestoreGateFor` (default-ready standalone). NpcInteractionBridge (probes) + CompanionVaultBridge (data+gate+event) both built.

### TRUE SURFACE (cascade-masking unmasked): was ~224 → now 131. Work in buckets.
Every prior build was cascade-masked (a broken `using` suppresses a file's body errors). These are pre-existing deps, not new breakage. **Pristine RPGMaker source = reference for any original-API question.**
⚠ **FILE-LOCK HAZARD:** VS background-build holds project files; PowerShell `Set-Content`/WriteAllText can FAIL silently and **truncate a file to empty** (it wiped CompanionPatches.cs once — recovered by re-deriving from pristine). Use a write-retry loop + verify line-count after bulk writes.
- **Bucket 1 — Mechanical (~92): ✅ DONE.** `FiresLogger`→`using FiresCore.Logging;` (5 files). `UnityEngine.Input.*` fully-qualified (8 files — the `FiresCore.Input` namespace shadows `UnityEngine.Input`; a compilation-unit `using Input=` alias does NOT win over the enclosing-namespace, so fully-qualify instead of alias).
- **Bucket 2 — companion UI screens (71): ✅ DONE.** Pulled the 4 `Npc/UI/Companion{Roster,Inventory,Stats,Attribute}Screen.cs` + `CompanionInventoryPatches.cs` from Core (they're FRONTEND — stay RPGMaker; FiresCompanions has its own). Built `FiresCore.Bridge.NpcUiBridge` (ShowInventory / InventoryOpenFor / StatsOpenFor) + rewired the 3 engine→screen calls (CompanionController.Show, CompanionIdleBehavior ×2 IsOpenFor). ⚠ Phase 6: preserve these 5 frontend files when deleting RPGMaker's Companions source.
- **Bucket 3 — Assorted: clear wins ✅ DONE** (CompendiumDiscoveryTracker→CompanionEventBridge; `VerdantsAscent.UI.NpcVisEquipment`→`NpcVisEquipment`; `Companions.CompanionPatches`→`CompanionPatches`). **REMAINING = 44, the triage tail (each needs a per-type decision — do FRESH, not at depth):**
  - `ConfigManager`(9, mostly CompanionSettings+CompanionNpcModule) — config-value reads. DECISION: Core config bridge (`Func<string,T>` accessors) vs the cluster shouldn't read host config directly. Check what keys (companion AI-while-logged-out, radii, etc.).
  - `FiresRPGmaker`(7) — TWO seams: `FiresRPGmaker.assetBundle` (CompanionPrefabManager 50,246 — companion prefab bundle → asset-load bridge `Func<string,GameObject>` or move bundle ref to Core) + `FiresRPGmaker.Instance.configSync.IsAdmin` (CompanionCommandSystem 354/407/437, CompanionNpcModule 1109/1180 → admin-check bridge `Func<bool> IsAdmin`).
  - `NpcFashion`(6, CompanionRandomLoadout) — Marketplace fashion (NpcFashionComponent / appearance gen). Bridge or relocate the fashion bits.
  - Marketplace interaction UI in **CompanionIdleBehavior** (`TraderUIController`/`QuestInteractionScreenController`/`InfoNpcPlayerPanel`/`NpcMainPanelController`/`Dialogues` ≈8) — SAME pattern as CompanionNpcModule: delegate the static-NPC quest/info/dialogue/trader interaction to existing `NpcModeBridge.RaiseInteraction`, delete the handlers (logic → RPGMaker provider).
  - `CompanionPatches`: `ServerConfigFileWatcher.SendAllConfigsToClient` (L327 config-streaming) + 2× `GetComponent<NpcController>` static-placement routing (L1674/1727). Bridge or relocate (the config-push patch is arguably RPGMaker-server glue → relocate the patch to RPGMaker).
  - `NetworkObjectHelper`(3, AbilityFXManager/TauntEffect) — network spawn helper; Core-move (engine util) or fully-qualify if vanilla.
  - `GUIManager`(3) → `ModUiRegistry`/bridge. `SavedNpcManager`(2) → Marketplace, bridge/relocate. leftover `Companions`(1)/`VerdantsAscent`(1) → mechanical FQ fixes.
  Triage rule unchanged: Core-move (engine infra) / bridge (Marketplace behavior) / relocate-to-RPGMaker (Marketplace-only glue, StaticNpcInitializer precedent).

## Then
- **Phase 5**: RPGMaker side — write the providers (register `NpcModeBridge`/`NpcInteractionBridge`/`CompanionVaultBridge` delegates; the OpenConfigPanel/Interaction provider lifts the deleted SyncTo/Handle* logic from pristine source) + swap the ~10 inbound referencers to `FiresCore.Npc` + drop the 224 cluster `<Compile>` entries. Keep StaticNpcInitializer in RPGMaker.
- **Phase 6**: delete RPGMaker `Modules/Companions/` source (EXCEPT StaticNpcInitializer — relocate).
- **Phase 7**: POST LANDED. Then the separate `Companion*→Npc*` surgical identifier rename (coordinated, identifier-only, NOT strings).

## Build
`& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "<core.csproj>" /p:Configuration=Release /nologo /verbosity:minimal` — count `: error `. NOTE: the C# compiler cascade-MASKS error counts (a file with one unresolved type suppresses its other errors); use Grep for ground-truth surface, not the error count.
