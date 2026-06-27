# Companion Movement State-Control Plan (2026-06-27)

Goal (user directive): **eliminate "stop-movement / lock-in-place" hacks as a yield mechanism.**
A per-frame `SetMoveDir(0)` to "hold" the NPC is a second writer fighting the channel — it glitches
against anything that calls movement every frame. Every yield must instead be EITHER:
- **(A) exclusive acquisition** that gates all other callers from even attempting to write, OR
- **(B) clean suspend** of the yielding behavior (its movement loop stops running) + **resume** after.

Standing still must be an **owned state**, never a non-owner spamming zero.

## The contract (one authority, one writer, owned standstill)

- **Sole writer:** `UnifiedMovementAuthority.ApplyMoveDirectionInternal` (UMA.cs:867), driven only by
  `ApplyMovement` from `UMA.Update`, is the ONLY thing that calls `Character.SetMoveDir/SetWalk/SetRun`.
  Everyone else expresses *intent*.
- **Acquire-to-move:** `TryAcquireAuthority(source, owner, dur)` (UMA:297) — granted by priority ladder
  `Forced>PlayerCommand>Animation>Combat>SubBehavior>Following>IdleWander`, with the load-bearing
  equal-priority incumbency guard (UMA:352). Owner then calls `SetMoveDirection(owner,…)`; non-owners
  are rejected (UMA:514).
- **Gating:** before forming any movement a caller asks `CanWrite(owner)`. If no → **park** (run no
  movement code this frame). Denial == do nothing, **never** write a fallback zero.
- **Owned standstill:** "stay put" = the owner either calls `Hold(owner, reason)` (enters
  `MovementState.Stopped`, keeps the slot warm) or simply stops calling `SetMoveDirSafe` (ApplyMovement
  settles to zero through the single writer). Frozen states (emote/chair/UI/root) use `FreezeMovement`.
- **Suspend/resume (generalized park):** an interrupter calls `SuspendBelow(owner, floor)` which saves
  the suspendee's authority **slot**, blocks ≤floor sources, and fires `OnAuthorityChanged` so parked
  loops stop. `Resume(owner)` restores the slot. Nested interrupts compose because the save is a slot,
  not a boolean — the save-stack IS the priority ladder (depth bounded by 7 levels).
- **Structural safety:** once the `Character.SetMoveDir` Harmony prefix (CompanionPatches.cs:2747) is
  hardened to drop any companion write where `IsCurrentlyApplying` is false, a forgotten raw
  `SetMoveDir` in a *future* behavior is a silent no-op, not a fight. Yielding-by-zero becomes
  physically impossible.

## New UMA surface (smallest set)

- `Hold(string owner, string reason)` — owner-only; target dir = zero, enter `Stopped`, keep slot warm.
- `bool CanWrite(string owner)` — `owner==CurrentAuthorityOwner && !frozen-by-other && !suspended-by-other`.
- `SuspendBelow(string owner, MovementSource floor)` / `Resume(string owner)` — save/restore `_suspendedSlot`,
  reuse existing `OnAuthorityChanged` (UMA:88, currently no consumers) + `StopMovementImmediate`-on-handoff.
- `GetCachedAuthority(Character)` on CompanionPatches — mirror of the existing `GetCachedStateController`.

`ShouldBlockExternalMovement` (UMA:137, defaults on) + `IsCurrentlyApplying` (UMA:156, set around
UMA:880/889) already exist — the prefix enforcement is a 3-line add on existing plumbing.

## Standstill mapping rule (Hold vs Freeze)

- **Hold** (owner keeps slot, higher sources can still preempt) → behavior standstills: workstation,
  gather/loot finish, bow aim/draw/shoot/between-shots, halt/hold commands. Combat/command can still interrupt.
- **Freeze** (denies all non-Forced) → true frozen states: emote, chair-sit, UI interaction, root,
  inner-peace. Entered ONCE on state-enter, cleared ONCE on state-exit — never per frame.
- ⚠ Mapping a workstation to Freeze instead of Hold would block combat from ever interrupting the worker.

## Migration plan (ORDERED — freeze-safe; enforce LAST; test gate between every step)

1. **Add UMA primitives + GetCachedAuthority** — purely additive, no caller changes, no behavior change. **LOW.**
2. **Convert one-shot teardown raw writers → ReleaseAuthority/Hold** (12 files: Chest/Fire/Crafting/Loot
   StopBehavior, CompanionInteractionBehavior ×4, CommandMovementHandler.StopCommand, Command Halt/Hold/
   Gather/Loot, CompanionController teleport ×2 = Forced, AnimationController.OnPlayAnimation, Dodge ×2). **LOW.**
3. **Convert per-frame emote/chair/UI holds → frozen-state-once + park** (Chairs ×2, Emotes ×3, idle chair
   ×2, MovementStateHandlers.ExpressionEmote, StateTransitionHandler victory/defeat, Interaction sit/interact).
   One subsystem at a time. **MEDIUM** (risk: state entered but never exited → rely on existing state timeout).
4. **Convert per-frame status-effect/stuck holds** — Rooted/InnerPeace `FreezeMovement(dur)` once + Unfreeze
   on stop; StuckPrevention acquire Forced once, drive/Hold, release. **MEDIUM** (always pass duration so
   freeze auto-expires; stuck must release Forced).
5. **Convert per-frame BowTraining holds → acquire-once + park**; `StopAllMovement` becomes a no-op. **MEDIUM.**
6. **Re-express the two suspend/resume sites on the shared primitive** — IdleBehavior InterruptForCombat/
   ResumeAfterCombat (:648/:673) → SuspendBelow(Combat)/Resume(Combat); StartCommand teardown → acquire
   PlayerCommand + SuspendBelow instead of ForceReset-destroy. Keep the old IsInSubBehavior park in parallel
   until CanWrite-park is verified. **MEDIUM** (suspend with no resume → auto-resume on authority timeout).
7. **Remove the no-authority fallback writers** (SetMoveDirSafe:230-237, Pathfinding:72) → park. **MEDIUM**
   (do this BEFORE enforcement; verify authority is reliably non-null first).
8. **LAST: harden the prefix** — `if (uma!=null && uma.ShouldBlockExternalMovement && !uma.IsCurrentlyApplying)
   return false`. **HIGH** — pre-flight grep `.SetMoveDir(` across the Npc cluster for zero hits outside
   `ApplyMoveDirectionInternal`; keep `DisableExternalBlocking` (UMA:491) as an in-game kill-switch.

Each step has an in-game test gate (see the workflow output / each step's `testGate`). Ship Step 8 only
after a full regression (follow/combat/patrol/all sub-behaviors/commands/sit/emotes/teleport/root/stuck/
bow + combat-interrupt-resume for each) passes with the gate ON.

## Conversion progress (live)

Classification (movement-hack-classification workflow) proved most of the ~50 raw `SetMoveDir` hits are
ALREADY safe (kinematic-guarded during chair/emote, one-shot transitions, enemy/player-targeted roots) or
DEAD CODE (WorkstationInteractionBehavior **V1** is unregistered — V2 replaced it). The genuine fix-list is
~12–15 sites, not 50 — so we do NOT churn out-of-scope code.

DONE (builds clean + deployed, gate still OFF):
- Step 1 foundation: `Hold` / `CanWrite` / `SuspendBelow` / `Resume` + auto-resume-on-release + `GetCachedAuthority`.
- **FleeMovementHandler + CompanionCombatMovement.ExecuteFleeState** — flee NEVER went through UMA (handler
  raw-wrote `SetMoveDir`, coordinator never called `SetMoveDirSafe`). Now: handler computes/returns only,
  coordinator drives via the authority-routed `SetMoveDirSafe` (owner `CompanionCombatMovement`, intent
  `CombatRetreat` → Run). Real bypass fixed.
- **InnerPeaceEffect** (monk meditation) — per-frame `SetMoveDir(0)` → one-time `FreezeMovement(dur)` +
  `UnfreezeMovement` on end; companion-only (non-UMA fallback kept).
- **WorkstationInteractionBehaviorV2.UpdateWorking** — per-frame raw zero-hold → acquire-if-needed + `Hold` + park.
- **DodgeBehavior** (CommitMovement 301 / StopMovement 428 / StopStrafing 563) — drives **both** dodge rolls and
  strafing. It's owned by CompanionCombat, a SEPARATE system from CompanionCombatMovement (which holds Combat
  authority and keeps its 2s lease even while yielding to a dodge), so a same-priority acquire would be denied.
  Now drives through UMA at **Animation priority (80)** under owner `CompanionDodge` (preempts CombatMovement's
  Combat-70 positioning for the committed window; below Command-90/Forced-100 so a command/teleport still wins),
  releasing on stop so CombatMovement resumes. No-UMA kinematic-guarded fallback kept.

REMAINING genuine converts (next batches):
- CompanionCommandSystem 1378/1751 (collect-item per-frame drives), 1411/1788/2080/1121 (stops) — command
  system, owner-string care (a Release from a non-owner no-ops).
- CommandMovementHandler:160 — Release (verify owner contract first).
- Work-behavior `MoveToPosition` fallbacks (ChestDeposit 1128 / Crafting 583 / Loot 596 / FireTending 1020)
  + Pathfinding:72 + CombatMovement.Movement:230 — near-dead `_combatMovement==null` / `authority==null`
  fallbacks; fold into Step 7 (only matter once the gate turns on).
- CompanionAnimationController:472 — UNSURE (Release vs Unfreeze depends on caller context).

EDIT ONLY the FiresUnifiedCore copy — stale copies exist under FiresVAngarde_PRESTRIP_BACKUP and
WORKINGUIREFPREREFACTOR; never touch those.

## Open questions (need a decision before / during)

1. **Facing axis (HOLE 1):** `transform.rotation`/`SetLookDir` is still ungoverned and overlaps many of
   these sites. Recommend **deferring** a parallel "look authority" but flagging every rotation write as we
   convert. (The bow-facing fixes A/B/C already landed.)
2. **Freeze-vs-Hold per-site:** confirm each per-frame site maps to the correct one (see rule above).
3. **Network ownership:** confirm UMA only drives the body on the owning peer so the hardened prefix
   doesn't drop legitimate remote-sync writes (prefix already early-outs on non-companions).
4. **Suspend auto-resume on timeout:** `ReleaseAuthority` (fired on authority timeout, UMA:248) MUST restore
   `_suspendedSlot` or the suspendee is orphaned → frozen. The single most important new freeze-safety.
5. **Legacy MovementPriority sub-system** (CompanionStateController:950-1077) is a THIRD priority ladder
   parallel to UMA's MovementSource. Recommend collapsing onto UMA's PlayerCommand source — but that's a
   larger refactor than this task; **confirm before touching.**
