# Heightmap Override Module — Reference

## Purpose
Replaces the external `HeightmapUnlimited` (Jotunn-dependent) mod with a self-contained
module that unlocks the vanilla TerrainComp height clamp of ±8 units.

---

## Original Mod Being Replaced

| Field | Value |
|---|---|
| Mod name | HeightmapUnlimited (Digitalroot remake) |
| Assembly | `HeightmapUnlimited, Version=1.4.1.0` |
| GUID | `Menthus.bepinex.plugins.HeightmapUnlimited` |
| Hard dependency | `com.jotunn.jotunn` ? **reason for removal** |

---

## What the Original Mod Did

Valheim's `TerrainComp` hard-codes `±8f` float literals in three methods to clamp how
far terrain can be raised or lowered relative to its original height:

| Method | Literals patched |
|---|---|
| `LevelTerrain` | `-8f` ? Min, `+8f` ? Max |
| `RaiseTerrain` | `-8f` ? Min, `+8f` ? Max |
| `ApplyToHeightmap` | `-8f` ? Min, first `+8f` ? MinAbs (absolute value of Min), subsequent `+8f` ? Max |

The original used `[HarmonyTranspiler]` patches to replace those IL `Ldc_R4` opcodes
with `Call` opcodes pointing to static helper methods that return configurable values.

---

## Our Implementation

### Config (`HeightmapOverrideConfig`)
- `HeightmapOverride.MaxHeight` — float, default `200f`, range `1–200`  [Server-locked]
- `HeightmapOverride.MinHeight` — float, default `-200f`, range `-200– -1` [Server-locked]
- `HeightmapOverride.Enabled`   — bool, default `true` [Server-locked]

### Helpers
- `HeightmapOverrideConfig.Min()`    ? `MinHeight.Value`
- `HeightmapOverrideConfig.MinAbs()` ? `Math.Abs(MinHeight.Value)`
- `HeightmapOverrideConfig.Max()`    ? `MaxHeight.Value`

### Patches (`HeightmapOverridePatches`)
Three `[HarmonyTranspiler]` patches on `TerrainComp`:
- `LevelTerrain`      — replaces `-8f` / `+8f`
- `RaiseTerrain`      — replaces `-8f` / `+8f`
- `ApplyToHeightmap`  — replaces `-8f` / first `+8f` (MinAbs) / subsequent `+8f` (Max)

Guards: if `Enabled` is false the transpilers pass instructions through unmodified.

---

## Integration Points

| File | Change |
|---|---|
| `ConfigManager.cs` | Declares `configHeightmapEnabled`, `configHeightmapMaxHeight`, `configHeightmapMinHeight`; calls `HeightmapOverrideConfig.Initialize(config)` |
| `Ascend.cs` | Adds config entries to `configSync` via `HeightmapOverrideConfig.BindToSync(configSync)` |

---

## Notes
- No Jotunn dependency — pure BepInEx + HarmonyLib.
- The `ApplyToHeightmap` transpiler must track an index counter to distinguish the
  two `+8f` occurrences (same as the original implementation).
- Compatible with dedicated servers: patches are IL-level and contain no client-only
  assembly references, so they do NOT need to be added to `_clientOnlyPatchTypes`.
