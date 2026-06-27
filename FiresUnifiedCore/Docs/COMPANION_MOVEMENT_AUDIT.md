# Companion Movement Single-Writer Audit (2026-06-27)

Full read-through of the companion movement/facing flow to enforce the single-writer rule
(exactly ONE component may write movement/facing per frame; two writers = frozen/twitching NPC).

## Architecture (as designed)

`FiresCore.Npc.UnifiedMovementAuthority` (UMA, `Npc/Core/UnifiedMovementAuthority.cs`) is the sole
caller of `Character.SetMoveDir`, from `ApplyMoveDirectionInternal`, once per Update. Every other
system requests movement via `TryAcquireAuthority(source, owner, duration)` then
`SetMoveDirection(owner, dir, walk, run)` — denied if `CurrentAuthorityOwner != owner`.

Priority ladder: `Forced 100 > PlayerCommand 90 > Animation 80 > Combat 70 > SubBehavior 60 >
Following 50 > IdleWander 40 > None 0`. Higher strictly preempts; **equal priority is denied to all
but the incumbent** (incumbency guard, ~line 352) so the two Combat-70 writers (`CompanionAI` and
`CompanionCombatMovement`) can't ping-pong. `FreezeMovement` blocks every source except Forced and
is re-enforced each LateUpdate. `HasAuthority(owner)` answers "do I currently own movement?".

**This movement arbitration is sound — do NOT touch the incumbency guard or FreezeMovement.**

## Two structural holes

1. **Facing is ungoverned.** UMA only arbitrates `SetMoveDir`/walk/run. Nothing arbitrates
   `transform.rotation` or `SetLookDir`. Every facing writer writes directly with zero coordination.
   This is the root of the bow-training "faces owner between shots" bug.
2. **The `SetMoveDir` Harmony prefix (`CompanionPatches.cs:~2747`) does not enforce authority.** It
   only blocks kinematic bodies + hard-frozen state; it never consults `ShouldBlockExternalMovement`/
   `IsCurrentlyApplying`. So single-writer for movement is *cooperative-only*, not enforced — any
   direct `SetMoveDir` lands. (Closing this hole requires routing ALL direct writers through authority
   FIRST, then enabling enforcement — otherwise legit direct writers get blocked → freeze.)

## Conflicts

### FIXED (2026-06-27) — bow-training facing bug, all facing-yield (cannot freeze movement)

- **A — idle body rotation didn't yield to sub-behaviors.** `CompanionIdleBehavior.UpdateSmoothRotation`
  was called outside the `_activeSubBehavior == null` guard that protects head-look. An in-flight
  look-around kept lerping `transform.rotation` while a sub-behavior (bow training) owned facing.
  *Fix:* yield at the top of `UpdateSmoothRotation` — `if (_activeSubBehavior != null) { _isRotating = false; return; }`.
- **B — head-look APPLY didn't yield to sub-behaviors.** `ApplyHeadLookAt` (HeadLook.cs) early-outed on
  chair/emote but not sub-behavior, so the head kept tracking a stale target (the owner) during bow
  training while the body locked to the archery target. *Fix:* `if (IsInSubBehavior) { _headBone.localRotation = _headBaseRotation; return; }`.
- **C — bow facing didn't yield to combat.** `BowTrainingBehavior.UpdateBetweenShots` called
  `FaceTargetAndLock` unconditionally; on the frame combat preempts (before the sub-behavior is
  interrupted) it fought combat facing. *Fix:* gate it behind `!_combatMovement.IsInCombat`.

### DEFERRED — need design + in-game testing (freeze class or subtle facing regressions)

- **#6 (visible-jank) — combat split-brain.** `CompanionAI` and `CompanionCombatMovement` both drive
  combat at priority 70 with different owner strings; the loser's MOVEMENT is denied but its FACING
  (`CompanionCombatMovement.FaceMovementDirection` @ Combat.cs 413/441/841/1131/1138, and
  `CompanionAI.LookAt` @ Combat.cs 615/700/736) still fires → companion strafes one way, faces another.
  *Candidate fix:* gate each facing writer behind `HasAuthority(ownOwner)`. **Risk:** `CompanionAI.LookAt`
  also faces the enemy while attacking STATIONARY, when AI may not hold movement authority — naive
  gating would make the companion attack without facing. Needs a "face target when attacking even if
  not moving" carve-out, or option (a): make `CompanionCombatMovement` the sole combat facing+movement
  owner and have `CompanionAI` defer.
- **#4 (visible-jank) — loot-pickup coroutine bypasses authority.** `CompanionCommandSystem.cs:~1378`
  (and siblings 1751/1411/1788/2080) call `SetMoveDir`+`SetWalk` directly every 0.2s → sliding when
  combat/following also drive movement. *Fix:* route through `TryAcquireAuthority(PlayerCommand)` +
  `SetMoveDirection`, or cancel the coroutine on combat start.
- **#5 (visible-jank) — `ForceStopAllBehaviors` raw stop not atomic.** `CompanionCommandSystem.cs:~1121`
  writes `SetMoveDir(0)` directly, then sets command priority afterward; AI can re-set a nonzero dir on
  the same frame → lurch. *Fix:* `authority.FreezeMovement("command-transition", short)` + set priority
  in the same synchronous block.
- **#8 (minor) — victory emote raw stop.** `StateTransitionHandler.cs:150/165` writes `SetMoveDir(0)`
  twice across FixedUpdate boundaries; Following can re-acquire between them → a step interrupts the
  pose. *Fix:* `FreezeMovement` for the emote duration (or hold Animation-80 authority).
- **#7 (minor) — null-authority fallback double-writes.** `CompanionAI.Pathfinding.cs:71-74` and
  `CompanionCombatMovement.Movement.cs:228-237` both fall back to raw `SetMoveDir` when authority is
  null (early-Update-before-Start window) → both write the same frame, post-teleport settle jank.
  *Fix:* no-op the fallback (log once, wait for authority). **Risk:** if authority never resolves,
  no-op = frozen; only safe once authority resolution is confirmed reliable.

### Verified clean — DO NOT TOUCH

- UMA equal-priority incumbency guard (`UnifiedMovementAuthority.cs:352-365`) — load-bearing; prevents
  the AI↔CombatMovement ping-pong that previously froze loading screens.
- `FreezeMovement`/`EnforceFreeze` + the chair/emote/UI `SetMoveDir(0)` writes — owner-state enforcement,
  correct single-writer cases.
- `UpdateHeadLookAt` target SELECTION (already sub-behavior-gated; only the APPLY half was buggy → B).
- `CompanionAI` follow-speed sync + `MoveToWithAuthority` calls (acquire authority before vanilla
  `MoveTo`, keep vanilla `FindPath` — matches the rule).
- `BowTrainingBehavior.StopAllMovement` (guards on `isKinematic`; the MOVEMENT side of bow training is
  fine — only the FACING side was buggy).

## Durable fix (recommended)

Add a **FacingAuthority** mirroring UMA so `transform.rotation`/`SetLookDir` is arbitrated by the same
priority model. Then the per-site facing gates (#6, and the carve-out for stationary attack-facing)
fall out of one place instead of N scattered guards. Until then, the FIXED set above resolves the
reported bug; the DEFERRED set should be tackled one at a time with in-game verification.
