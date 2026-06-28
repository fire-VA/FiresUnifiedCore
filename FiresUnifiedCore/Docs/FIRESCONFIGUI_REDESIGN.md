# FiresConfigUI Rebuild — Schema-Driven Global Config Browser

> Design + build plan. Flip the F8 config screen from manual opt-in registration to
> automatic discovery of every Fires mod's BepInEx config, using the
> reflection-over-`ConfigEntry` concept proven in FiresValcast's `SchemaExporter`.

## 1. Diagnosis — why the current F8 screen is empty/broken

The window mechanically works (draws, toggles, edits write back). It is *content-empty* with UX traps. Root causes, ranked:

- **R1 (CRITICAL) — Opt-in registry with no discovery; only Core opts in.** `DrawNav`/`DrawBody` iterate `s_mods`, populated solely by `FiresConfigUI.Register(modName, config)` (`FiresConfigUI.cs:48`). Tree-wide grep finds exactly TWO registrations: Core's self-register (`FiresUnifiedCore.cs:59` → `FiresConfigUI.cs:110`) and FiresTossinShade (`FiresAdminTerrain.cs:166`). FiresCompanions only mentions the type in a comment. So F8 shows ~1 mod; the boot log `(10 entries, 1 mod(s))` is literally Core's own config. The fix data is sitting unused: `Chainloader.PluginInfos` and `FiresMod.Instances` (`FiresMod.cs:24`).
- **R2 (HIGH) — Server-locked synced entries silently revert; `ReadOnly` ignored.** ConfigSync sets `ConfigurationManagerAttributes.ReadOnly` (`ConfigSync.cs:341`) and `resetConfigsFromServer` snaps values back (`ConfigSync.cs:354-386`). FiresConfigUI never reads `isWritableConfig`/`ReadOnly`, renders locked entries as editable, and the slider visibly snaps back — looks "broken."
- **R3 (HIGH) — IMGUI text fields leak keystrokes.** `FiresInputBlockDriver` only engages on focused uGUI TMP/legacy `InputField` (`FiresInputBlock.cs:213-242`). The window's search/text/keybind fields are IMGUI `GUILayout.TextField` (`FiresConfigUI.cs:292,538,561`) and it never calls `FiresInputBlock.Acquire`. Typing in search fires vanilla + other-mod hotkeys.
- **R4 (MEDIUM) — No admin/serverscope gating.** Zero admin check; authority is enforced only by ConfigSync reverting, producing misleading UI.
- **R5 (MEDIUM) — F8 self-suppression.** Toggle reads `CfgHotkey.Value.IsDown()` (`FiresConfigUI.cs:154`); `FiresInputBlockClientGates` patches `KeyboardShortcut.IsDown`→false while the text gate is on. A focused input elsewhere can make F8 not respond. The raw-`Input` fallback only runs when `CfgHotkey` is null, which it never is.
- **R6 (LOW) — Range-less numeric field fails mid-edit.** `DrawNumber` no-range branch only writes on `TryParse` success (`FiresConfigUI.cs:558-567`); `"1."`, `"-"`, comma-decimal locales fail and re-read from `BoxedValue` each frame.

## 2. Concept — Valcast schema-driven model, globalized

FiresValcast proves: **walk the live `ConfigFile` once, reflect over each `ConfigEntryBase`, derive everything generically** — type, label, default, range/options, description — with ZERO per-setting wiring (`SchemaExporter.cs:127-207`). The only hand-curated bit is a section→(panel,label) map.

A single type→control dispatch table (`SchemaExporter.cs:135-175`) drives both the JSON schema (desktop app) and the in-game IMGUI `DrawControlRow` (`CinematicHud.cs:381-398`). Write-back is `ConfigEntry.Value =` / `SetSerializedValue()` (BepInEx auto-persists). Hot-reload is `FileSystemWatcher` + debounced main-thread `Config.Reload()` (`ConfigHotReload.cs:34-75`).

**Map onto a global browser:** keep the reflection core + dispatch table verbatim. Change ONE thing — iterate **every plugin's** `ConfigFile` instead of one `this.Config`. Replace Valcast's private Sections map with **automatic grouping by plugin GUID → raw `Definition.Section`**. Add the one piece Valcast omits: per-entry **server-lock/admin** flags from ConfigSync.

## 3. Discovery — enumerate all mods' configs at runtime

```csharp
foreach (var kvp in BepInEx.Bootstrap.Chainloader.PluginInfos) {
    PluginInfo info = kvp.Value;            // .Metadata.GUID, .Metadata.Name
    BaseUnityPlugin plugin = info.Instance; // may be null if not instantiated
    if (plugin == null) continue;
    ConfigFile cfg = plugin.Config;         // BaseUnityPlugin.Config
    foreach (var e in cfg) {                // IEnumerable<KeyValuePair<ConfigDefinition,ConfigEntryBase>>
        ConfigDefinition def = e.Key;       // .Section, .Key
        ConfigEntryBase entry = e.Value;    // .SettingType, .DefaultValue, .BoxedValue, .Description
    }
}
```

`Chainloader.PluginInfos` is already used in Core (`ClientLogCollector.cs:76`, `UIBuilderAssetCache.cs:1183`). Reflect over the **`ConfigFile`, NOT per-mod ConfigManager fields** — the ConfigFile catches every bound entry regardless of where it was bound.

Per entry, from `entry.Description`: `.Description` → tooltip (first line); `.AcceptableValues` → reflect `AcceptableValueRange<>` (Min/Max) or `AcceptableValueList<>` (`SchemaExporter.cs:180-207`); enums → `Enum.GetNames(entry.SettingType)`; `.Tags` → ConfigSync tag for server-lock.

**Scope: Fires-only by default, all-plugins behind a toggle.** Default source = `FiresMod.Instances` (`FiresMod.cs:24`) — already enumerates every loaded Fires mod, each with `.Config`; fall back to `Chainloader.PluginInfos` GUID-prefix (`com.Fire.*`) for any Fires plugin not deriving from `FiresMod`. A "Show all plugins" toggle switches to full `PluginInfos` (bonus shudnal-style universal browser).

**Borrow vs bespoke:** shudnal's `ConfigurationManager` does this same reflection. We keep our own screen for (a) Fires branding/grouping, (b) in-game IMGUI on our own hotkey coexisting with shudnal's F1, (c) ServerSync-aware locking with a lock badge, (d) Fires input-gate discipline.

## 4. Schema model — per-setting descriptor

```csharp
sealed class CfgDescriptor {
    public string ModGuid, ModName, Section, SectionDisplay, Key, Label;
    public Type   Type;
    public string Description;
    public CtrlKind Kind;            // computed once at discovery
    public bool   HasRange;
    public double Min, Max, Step;
    public string[] Options;         // enum names or AcceptableValueList
    public object Default;
    public object LiveValue => Entry.BoxedValue;  // read fresh each frame
    public bool   IsServerSynced, IsServerLocked, IsAdminOnly, ReadOnlyAttr;
    public ConfigEntryBase Entry;    // live entry for write-back
}
```

`SectionDisplay` strips leading `"NN - "` (`FiresConfigUI.cs:652-680`). `Label` = `Prettify(Key)` (`SchemaExporter.cs:284-296`). `Kind`/`HasRange`/`Options`/`IsServerSynced` computed once at discovery to avoid per-frame reflection; only `BoxedValue` read per frame.

## 5. Rendering — IMGUI per type, nav, search

**Reuse the existing host verbatim** — `FiresConfigUIHost` (`FiresConfigUI.cs:117`), its identity-`GUI.matrix` reset (`266-275`), self-drawn opaque backgrounds (`702-708`), try/finally layout groups. The `DrawEditor` dispatch (`505-545`) already implements most of the table — extend, don't replace.

| CLR type | Condition | Control |
|---|---|---|
| `bool` | — | `GUILayout.Toggle` |
| int/float | `HasRange` | `HorizontalSlider(Min,Max)` snapped to `Step` + numeric field |
| int/float | no range | text field w/ per-field edit buffer (fixes R6) |
| `enum` / `AcceptableValueList<>` | — | dropdown: cycle-button ≤6 options, popup otherwise |
| `Color` | — | 4 RGBA sliders (`570-593`) |
| `KeyboardShortcut`/`KeyCode` | — | click-to-capture (`594-615`) |
| `string` | — | text field |
| else | — | read-only label |

Each row keeps reset-to-default + dim description, PLUS a lock badge when `IsServerLocked`/`ReadOnlyAttr`.

**Left-nav:** two levels (mod → section), **natural-sorted** so `"01 - General"`/`"06 - Auto-Tune"` interleave correctly; strip `NN -` for display. **Search:** extend `Matches` (`460`) across the global list incl. `ModName`. **Scroll:** wrap body in `BeginScrollView` (FGN alone ~50 entries); lazy-render only the selected section.

## 6. Write-back & sync

- **Write-back:** `entry.BoxedValue = newValue` (BepInEx fires `SettingChanged`, persists via `SaveOnConfigSet`). Use `SetSerializedValue`/`GetSerializedValue` for type-agnostic round-trip (Valcast `RemoteControlServer.cs:117-205`).
- **ServerSync detection (duck-typed — critical):** synced iff `Description.Tags` holds a `SyncedConfigEntry`/`OwnConfigEntryBase` tag. Multiple mods carry PRIVATE ConfigSync forks (Valcast `VerdantsAscent`, Discord, VApieces) so you can't test against one type. Detect by runtime type-name `SyncedConfigEntry\`1` or a public `SynchronizedConfig` member. Core-family uses shared `FiresCore.Sync.ConfigSync.configData()` — fast path for those.
- **Lock decision:** `IsServerLocked = synced && ConfigSync.IsLocked && !lockExempt`. When locked: render greyed/disabled + lock badge + tooltip "Server-controlled — admin only"; don't let the user fight `resetConfigsFromServer`. Honor `ReadOnly` as redundant disable. (Fixes R2 + R4.)
- **Hot-reload:** optional per-`.cfg` `FileSystemWatcher` + 0.3s debounce + main-thread `Config.Reload()` (`ConfigHotReload.cs:34-75`). Since UI reads `BoxedValue` fresh, no extra refresh wiring. Cheaper: subscribe each entry's `SettingChanged` to invalidate cached display strings. Don't double-watch Core's already-watched file (`ConfigManager.cs:56-135`).

## 7. Input / UX

- **F8 + shudnal coexistence:** keep `CfgHotkey.IsDown()` + `va_config`, keep the deliberate non-override of shudnal's F1 (`239-242`) and the handoff button.
- **Fix R5:** keep an UNGATED raw `Input.GetKeyDown(ToggleKey)` path active even when `CfgHotkey != null`, or exempt the toggle shortcut from the `KeyboardShortcut.IsDown` gate.
- **Fix R3:** while open AND an IMGUI text field has keyboard focus (`GUIUtility.keyboardControl != 0`), `FiresInputBlock.Acquire(token)` / `Release` on blur/close. Do NOT engage the heavy `InputBlock` (camera/minimap gates) — only cursor-free + text capture.
- **Cursor:** keep inline `Cursor.lockState=None/visible=true` in Update + OnGUI.
- **Standalone-canvas:** N/A by construction — pure IMGUI on a `DontDestroyOnLoad` host, no Canvas/prefab. Keep it that way.

## 8. Migration — keep `Register()` as an optional override

Auto-discovery becomes the default population path. `Register(modName, config)` is retained as enrichment, never required: it can override display name, opt into curated grouping/ordering, and (later) attach custom per-section labels / custom draw widgets. Idempotent by `ConfigFile` reference. If a mod is both auto-discovered and registered, the Register entry wins for display metadata (same ConfigFile → no double-listing). The two existing callers keep working unchanged. Add an optional `RegisterCustomEditor(ConfigDefinition, Action<ConfigEntryBase> draw)` (P6).

## 9. Phased plan (each independently buildable & build-clean)

- **P1 — Discovery core + schema.** New `CfgDiscovery.cs`: enumerate `FiresMod.Instances` + `Chainloader.PluginInfos`, build `CfgDescriptor` list, cache. *Verify:* `va_config_dump` logs `(N mods, M entries)` ≈ 8 mods, not 1.
- **P2 — IMGUI render, read-only.** Two-level nav (mod→section, natural-sorted, prefix-stripped) + scrollable body via existing host/dispatch; display live values, no write. *Verify:* F8 shows every Fires mod; FGN's ~50 entries scroll.
- **P3 — Write-back + persist.** `BoxedValue` writes per control; per-field edit buffer for range-less numerics (R6). *Verify:* edit a float/enum/bool, confirm `.cfg` persist + live effect.
- **P4 — ServerSync / admin locking.** Duck-typed sync detection; grey + lock-badge locked/`ReadOnly`; `IsAdmin` gate. *Verify:* non-admin sees synced entries disabled with badge.
- **P5 — Hot-reload.** Subscribe `SettingChanged`; optional per-`.cfg` watcher (skip Core's). *Verify:* external `.cfg` edit reflects within ~0.3s, no restart.
- **P6 — Register-override layer + polish + input fixes.** Register enrichment + custom-editor hook; `FiresInputBlock.Acquire` on IMGUI focus (R3); ungate toggle key (R5); "Show all plugins" toggle. *Verify:* search no longer fires hotkeys; F8 toggles with a uGUI field focused.

## 10. Risks & open questions

- **No native IMGUI combo.** Enum dropdowns faked: cycle-button ≤6 options (Valcast `Cycle(1)`), scrollable popup otherwise.
- **Per-frame reflection cost.** MUST cache descriptors (P1); never reflect in OnGUI. Re-run discovery on plugin-load / manual refresh, not every frame.
- **Large config sets.** FGN ~50, FiresNPCs ~90. Lazy section rendering so a single layout pass never inflates.
- **KeyboardShortcut/KeyCode across forks.** Duck-type by type-NAME; round-trip writes via `SetSerializedValue` for locale/format safety.
- **Lazy/late-bound entries.** Re-enumerate on world-load + refresh button; always enumerate the live `ConfigFile`, cache only the derived descriptor keyed by `(guid,section,key)`.
- **shudnal CM coexistence.** Separate hotkeys (F8 vs F1), close shudnal on open, handoff button. Both read live `BoxedValue` → converge on next draw.
- **Open Q — scope default:** Fires-only (recommended, branding/clarity) + all-plugins toggle. Confirm exact Fires GUID prefix vs `FiresMod.Instances` GUIDs.
- **Open Q — sync detection:** duck-type the tag (recommended, version-robust) vs reflectively invoke each fork's static `configData`.

## Files this build touches (absolute)

- `FiresUnifiedCore\FiresUnifiedCore\UI\FiresConfigUI.cs` — extend host/dispatch; discovery-fed nav; Register becomes override.
- `FiresUnifiedCore\FiresUnifiedCore\FiresUnifiedCore.cs` — kick discovery at Setup (after configs bound).
- `FiresUnifiedCore\FiresUnifiedCore\Sync\ConfigSync.cs` — `configData()`/`IsLocked`/`IsAdmin`/`lockExempt` gates.
- `FiresUnifiedCore\FiresUnifiedCore\Input\FiresInputBlock.cs` — Acquire on IMGUI text focus; ungate toggle key.
- `FiresUnifiedCore\FiresUnifiedCore\Lifecycle\FiresMod.cs` — `Instances` as the Fires-only discovery source.
- **New:** `FiresUnifiedCore\FiresUnifiedCore\UI\CfgDiscovery.cs` (+ descriptor model). Add every new `.cs` to the old-style csproj `<Compile Include>`.

Reference pattern (read-only): `FiresValcast\FiresValcast\Modules\{SchemaExporter,ConfigHotReload,RemoteControlServer,CinematicHud}.cs`.
