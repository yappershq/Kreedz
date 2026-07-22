# CS2 Movement Flow — engine → ModSharp → cs2kz → Kreedz

Ground-truth call graph (current build, Windows addresses from the engine map), annotated with
where **ModSharp** exposes a hook, where **cs2kz** detours, and where **Kreedz** does its work. This is
the reference that settles *which hook point is correct* for each movement modification.

## The spine (one usercmd → the subtick loop → per-subtick move)

```
CCSPlayerController::OnSimulateUserCommands              0x180A098F0
  └─ CBasePlayerController::OnSimulateUserCommands       0x180ACDFA0   rebuild CUserCmd, max 3 queued cmds
       └─ MovementServices vfunc +176 (RunCommand_vt22)  0x180A528A0
            └─ CPlayer_MovementServices::RunCommand      0x180BD26B0   set tick / curtime / frametime
                 └─ ProcessMovement_orchestrator         0x180BD01F0   SetupMoveData / SetupMove
                      └─ ProcessMovement_SubtickLoop     0x180BC3910   ── per-subtick, loops "next subtick" ──┐
                           └─ ProcessMove                0x180A50740   assign maxspeed                        │
                                └─ PlayerMove_dispatch   0x180A4DC20                                          │
                                     └─ (branches below) → FinishMove_PostMove 0x180A4E590 ──────────────────┘
```

**The key structural fact:** the subtick loop (`0x180BC3910`) is the thing that reads the subtick schedule
(`m_arrForceSubtickMoveWhen` + the usercmd's `subtick_moves`) and iterates the sub-steps. It sits **below**
`RunCommand` and **above** `ProcessMove`. `ProcessMove` and everything under it (`PlayerMove_dispatch`,
Walk/Air/Water/Toss, `FinishMove_PostMove`) run **once per subtick, inside the loop**.

## Hook-point mapping

| Engine fn | Addr | ModSharp hook | cs2kz detour | Fires |
|---|---|---|---|---|
| OnSimulateUserCommands | 0x180A098F0 | — | *PhysicsSimulate* (pre/post) | once / tick |
| (base) OnSimulateUserCommands | 0x180ACDFA0 | — | *ProcessUsercmds* | once / tick (≤3 cmds) |
| **RunCommand** | 0x180BD26B0 | **PlayerRunCommand** (pre/post) | — | once / usercmd |
| orchestrator / SetupMove | 0x180BD01F0 | — | *SetupMove* (pre/post) | once / usercmd |
| **SubtickLoop** | 0x180BC3910 | — | — | drives N subticks |
| **ProcessMove** | 0x180A50740 | **PlayerProcessMovePre/Post** | *ProcessMovement* | **once / subtick** |
| (assign maxspeed) | inside 0x180A50740 | PlayerGetMaxSpeed | OnGetMaxSpeed | once / subtick |
| WalkMove | 0x180A5A0C0 | PlayerWalkMove | — | on-ground subtick |

## Where `m_arrForceSubtickMoveWhen` is consumed → why RunCommand pre/post is correct

The forced-subtick array is read by the **SubtickLoop (0x180BC3910)** (set up in the orchestrator's
SetupMove) to decide the sub-step schedule. So the injection has to be written **before the loop runs**:

- **RunCommand pre (0x180BD26B0)** — above the orchestrator + loop → written in time. ✅  (Kreedz uses this.)
- **ProcessMove pre (0x180A50740)** — inside the loop, per-subtick, *after* the loop already scheduled the
  subticks → too late. ❌  (my earlier attempt — wrong, corrected.)

cs2kz injects even higher, at PhysicsSimulate/OnSimulateUserCommands (0x180A098F0). Nothing between there
and RunCommand consumes the array (the vfunc thunk + RunCommand just set tick/curtime/frametime), so the
RunCommand-pre bracket writes the identical value at an equivalent point. The `+0.5` seed goes in RunCommand
**post** (mirrors cs2kz's PhysicsSimulate-post), with a once-per-tick gate because RunCommand can fire up to
3× on a choked tick while cs2kz's PhysicsSimulate fires once.

## Branch map (moveType dispatch, per subtick)

- **Prechecks** (`0x180A4DC20` → CanMove? → CheckParameters `0x180A34E00` → Health≤0/flag0x20 clears wish →
  PlayerMove_PreChecks `0x180A4E830` → Lifestate==2 skips stuck check → `sub_180A35580` stuck →
  CategorizePosition `0x180A32EF0` → timed-movement timer → water/moveType dispatch).
- **moveType 2 WALK** → WalkMove_wrapper `0x180A4E460` → legacy/modern JumpCheck → FL_ONGROUND?
  - grounded → **WalkMove** `0x180A5A0C0`: wishdir → Friction `0x180A3B380` → AccelerateIfAllowed `0x180A2E980`
    → limit maxspeed + base vel → TryPlayerMove `0x180A56F90` → need step? StepMove `0x180A555B0` →
    RemoveBaseVelocity/StayOnGround `0x180A55270`.
  - airborne → **AirMove** `0x180A54430`: air wishdir → Lifestate==2 skips AirAccelerate → AirAccelerate
    `0x180A2F2A0` → AddGravity `0x180A2F050` → FinishGravity `0x180A503E0` → TryPlayerMove →
    RemoveBaseVelocityPostMove `0x180A4FE90`.
- **water level ≥3 & WALK** → WaterMove_wrapper `0x180A4EE10` → WaterMove `0x180A5AFA0` (wish vel, friction,
  buoyancy) → FinishGravity → TryPlayerMove/sweep/step.
- **moveType 4 TOSS** → FullTossMove `0x180A3B6E0` → wishdir/AccelerateIfAllowed → (mt==4? AddGravity) →
  base vel + delta → TossMove_PushMove `0x180A50EA0` trace/sweep → trace<1? ResolveImpact `0x180A4C950` →
  UpdateWaterLevel `0x180A36120`.
- **moveType 7/8 SIMPLE** → SimpleVelocityMove `0x180A3B600` → origin += vel*frametime → (mt9 SPECIAL: can
  switch back to WALK? else UpdateWaterLevel + Jump/Gravity/TryPlayerMove).
- **Life / cannot-move** (CanMove==false) → records dead / cannot_move → CheckParameters → HandleCannotMove
  `0x180A4E220` → FL_ONGROUND? grounded clears input/vel/accel, airborne applies half gravity (StartGravity).
- **Finish/commit** → FinishMove_PostMove `0x180A4E590` → CategorizePosition (ground trace/water/ground ent)
  → CheckFalling (landing speed/damage/sounds) → FinishMove per subtick (SetAbsOrigin/SetAbsVelocity) →
  loop to next subtick.

## Kreedz touchpoints (which hook, doing what)

- **PlayerRunCommand pre** (`Kreedz.Mode.CKZ`): half-tick input quantization (snap subtick `When`→{0,0.5}),
  jump-latch re-arm (`m_bOldJumpPressed`), forced-subtick **−0.5** inject (once-per-tick gate).
- **PlayerRunCommand post** (`Kreedz.Mode.CKZ`): forced-subtick **+0.5** forward-fill.
- **PlayerProcessMovePre** (CKZ): prestrafe turn-rate accum, perf takeoff timing, SlopeFix on landing.
- **PlayerGetMaxSpeed** (CKZ): 250 + prestrafe gain cap (all move types incl. water).
- **PlayerProcessMovePre** (`Kreedz.Style.AutoUnduck`): airborne auto-unduck over standable ground.
- **TimerModule** (ProcessMove pre): pause-freeze velocity-zero; start-gate/zone logic.

Life notes (engine): CanMove==false records dead/cannot_move; Health≤0 clears command movement;
Lifestate==2 skips part of the stuck checks and AirAccelerate.
