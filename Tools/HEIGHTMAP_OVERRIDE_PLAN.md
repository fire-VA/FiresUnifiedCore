# Heightmap override — plan and rationale

Code: `FiresUnifiedCore/Terrain/HeightmapOverride*.cs` · Branch: `feat/heightmap-override-fix`

Lifts Valheim's ±8 m limit on raising and lowering terrain to server-configured limits (sliders
±1000 m), the way HeightmapUnlimited does, with no Jotunn dependency. Keeps vanilla limits in voxel
worlds.

---

## What vanilla limits

Each heightmap vertex stores `m_levelDelta`: its offset from the originally generated height.
Vanilla clamps it to ±8 m with float literals in three `TerrainComp` methods:

| Method | Role |
|---|---|
| `LevelTerrain` | levelling tool writes the delta |
| `RaiseTerrain` | raise / dig (hoe, pickaxe) writes the delta |
| `ApplyToHeightmap` | **every peer** applies the stored deltas to render and collide |

`ApplyToHeightmap` is why the limits must match on every peer: a peer with a smaller limit renders
terrain clamped below where others walk on it.

Reference studied: Digitalroot's HeightmapUnlimitedJvL (AGPL-3.0, updated 2026-09-10). Approach
learned from it; no code copied.

---

## What was broken

1. **Never active.** Each transpiler checked `HeightmapOverrideConfig.Enabled` and returned vanilla IL
   when it was null or false. Transpilers run once, in `FiresMod.Awake`, *before* `Setup()` binds the
   config — so `Enabled` was always null and every session ran vanilla, while the config showed
   `Enabled = true`, `MaxHeight = 1000`.
2. **Silent on mismatch.** No count of what was replaced and no error when nothing was, so a Valheim
   update that moved the literals would also have gone unnoticed.
3. **Positional matching.** "The first `8f` in `ApplyToHeightmap` is the lower bound" assumed an
   instruction order instead of reading it.
4. **Labels dropped.** `yield return new CodeInstruction(...)` discarded any branch label on the
   replaced instruction — invalid IL if a literal is ever a jump target. HeightmapUnlimited has the
   same flaw.
5. **Not live.** Even with the order fixed, a transpile-time check could never follow a server-synced
   value that arrives after patching.
6. **No voxel awareness.**

---

## Design

**Limits are read at call time** (`HeightmapOverrideLimits`). The transpilers always replace the
literals with calls; the calls return vanilla ±8 while disabled or suppressed. Fixes 1 and 5 together:
order of initialization stops mattering, and server-synced values apply the moment they arrive. Cost
is two cheap calls per vertex during terrain operations — negligible next to the mesh rebuild those
operations already trigger.

**Structural matching** (`HeightmapOverridePatches.FindBoundTargets`):

| IL | Meaning | Replaced with |
|---|---|---|
| `ldc.r4 -8` | lower bound | `Min()` |
| `ldc.r4 8` then `sub` | `base - 8` lower bound | `MinAbs()` |
| `ldc.r4 8` then `neg` | `-(8)` lower bound | `MinAbs()` (the `neg` stays) |
| `ldc.r4 8` otherwise | upper bound | `Max()` |

Checked against every plausible compile shape with an asymmetric config (+50 / −300) — a lower/upper
mix-up would show immediately. All map correctly; disabled reproduces vanilla exactly.

**Paired clamps or nothing.** A method is patched only if it has ≥1 lower bound and the same number of
upper bounds. An imbalance means an `8f` that isn't a clamp — it is left vanilla rather than guessed
at. Also catches another mod having already replaced the literals.

**In-place retarget.** The matched instruction's opcode and operand are changed, so its labels and
exception blocks survive.

**One patch class per method.** Core's `FiresMod` attaches each type separately so one failure can't
abort the rest; a renamed method now fails alone.

**Fail loud.** `HeightmapOverrideStatus` records what each transpiler found. After config binds:

```
[HeightmapOverride] Patched TerrainComp.LevelTerrain (1 clamp(s)), RaiseTerrain (1 clamp(s)), ApplyToHeightmap (1 clamp(s)).
```

or, per method that was left vanilla or never transpiled, an **error** naming it and the counts found.
On world start and whenever the limits change (including by server sync):

```
[HeightmapOverride] Active — terrain may rise 1000 m and sink 1000 m from its generated height.
[HeightmapOverride] Suppressed by FiresAdminTerrain voxel world — vanilla ±8 m.
[HeightmapOverride] Disabled — vanilla ±8 m.
```

The IL of Valheim 1.0.7 was not inspected directly — no repo carries the game assembly. The first
launch's report is the verification.

---

## Voxel worlds

In `VoxelWrap`, a hoe or pickaxe edit regenerates the heightmap and `VoxelZoneStreamer` rewraps that
zone **and its 8 neighbours** from the heightmap (`Heightmap_Regenerate_RewrapZone`). A ±1000 m edit
would ask the voxel mesher to rebuild across 2 km of vertical range in nine zones. `VoxelOnly` has no
heightmap surface to limit. So voxel worlds keep vanilla limits.

The predicate is the streamer's own gate, `VoxelZoneStreamer.ModeActive`:
voxel `Enabled` **and** `CustomGeneration` **and** `WorldMode != Vanilla`. The override is suppressed
exactly when the streamer would wrap.

**Dependency direction:** FiresAdminTerrain references FiresUnifiedCore, so Core cannot reach into
AdminTerrain. Core exposes `HeightmapOverrideLimits.RegisterSuppressor(owner, predicate)`;
AdminTerrain registers.

### AdminTerrain integration (apply to the current local AdminTerrain)

In `VoxelZoneStreamer`:

```csharp
internal static void RegisterHeightmapSuppressor()
    => FiresCore.Terrain.HeightmapOverrideLimits.RegisterSuppressor("FiresAdminTerrain voxel world", () => ModeActive);
```

Call once from plugin init, after `FiresVoxelConfig.Bind`. `ModeActive` reads live config, so a mode
change takes effect immediately.

⚠️ **Required alongside it:** `FiresVoxelConfig.Enabled`, `CustomGeneration` and `WorldMode` are not
registered with AdminTerrain's ConfigSync. The suppressor must evaluate identically on every peer —
otherwise a client that considers the world voxel renders terrain clamped to ±8 while the server allows
+1000, and players walk on ground they can't see. Sync those three. (Voxel mode itself has the same
latent problem today: a client can voxel-wrap a world the server treats as vanilla.)

---

## Behaviour changes

- **The override now actually runs**, with the existing defaults — `Enabled = true`, ±1000 m. Every
  server on this Core build goes from ±8 to ±1000 on update. Decide the shipped default before release.
- **Lowering a limit live** re-clamps existing terrain on render; the stored deltas are untouched, so
  raising the limit again restores it.
- **Don't run HeightmapUnlimited alongside Core.** Whichever transpiles second finds no literals; Core
  reports it and leaves those methods vanilla.
- **Mixed installs diverge.** A client with Core joining a server without it gets no synced limits.

---

## Verify

1. Build.
2. Launch: exactly one `Patched TerrainComp…` line, three methods, no `[HeightmapOverride]` errors. If
   any method reports counts instead, that is Valheim 1.0.7's IL disagreeing with this plan — the counts
   say how.
3. Vanilla world: raise and dig well past 8 m with the hoe and pickaxe. Re-log — the terrain holds.
4. Set `MaxHeight = 20` in-game: the log reports it live, and raising stops at 20 m.
5. `Enabled = false`: exactly vanilla ±8.
6. Dedicated server + client: set limits on the server only; the client's log shows the server's values
   after joining, and both see the same ground.
7. After the AdminTerrain integration: a `VoxelWrap` world logs `Suppressed by FiresAdminTerrain voxel
   world`, and a vanilla-mode world logs `Active`.
