# Kreedz — Honest Port Status vs cs2kz-metamod

A reimplementation of [KZGlobalteam/cs2kz-metamod](https://github.com/KZGlobalteam/cs2kz-metamod) built on a
fork of [Source2Surf/Timer](https://github.com/Source2Surf/Timer). **This file is the source of truth and is
deliberately conservative.** It was rewritten 2026-07-21 after a skeptical, source-verified subsystem audit
(both codebases read line-by-line) that found the previous "✅ / 1:1 / 100% ported" labels were based on code
being *present*, not on it *running* or *matching cs2kz*. Kreedz is a working **movement-feel + DB/ranks
skeleton**; it is **not** a complete cs2kz port. Verdicts below: COMPLETE / PARTIAL / MISSING.

## ⚠️ The one that mattered most — now unblocked (in progress)

Modern `kz_` maps describe zones via entity keyvalues (`timer_trigger_type`), not targetnames. That source
was the #1 blocker. **It's now built + live-verified:** `MapApiSourceModule` detours
`IWorldRendererMgr::CreateWorldInternal` (ported from StripperSharp's native `CEntityKeyValues` read layer),
walks the entity lumps, reads the keyvalues, and feeds the (formerly dead) `MappingApiRegistry` parser.
`ZoneModule` correlates the parsed zones to their spawned `trigger_multiple` by origin and registers real
Start/End/Stage/Checkpoint zones. Verified on kz_pom (1 course, 2 zones) vs de_dust2 (0). **Remaining for full
1:1 zones:** the TriggerFix anti-dodge trace re-detection, the modifier/antibhop/teleport/push/bhop-counter
trigger types, the ZoneSplit type, and multi-course track mapping.

## Verdict matrix

| Subsystem | Verdict | Reality |
|---|---|---|
| Movement (CKZ physics) | 🟡 PARTIAL | Core-owned detours dispatched to a mode via `IKzMovementMode`: AirMove/CategorizePosition/TryPlayerMove + AirAccelerate (AACall telemetry) + a FinishMove-equivalent `PlayerProcessMovePost` dispatch (captures moveDataPost/oldAngles, `OnProcessMovementPost` hook point). Prestrafe turn-rate now cs2kz's wishdir-vs-velocity angle; perf/air-cap real; SlopeFix (OnStartTouchGround) + TryPlayerMove commit-gate implemented. Still open: water-speed cap, backward stuck-trace, and the full CGameMovement surface (SetupMove/FullWalkMove/CheckWater/…) beyond these. VNL registers no callbacks yet (stock). |
| Modes | 🟡 PARTIAL | Registry + both 33-cvar tables faithful. **VNL is NOT stock in cs2kz** (verified kz_mode_vnl.h:69-83 — ~13 overrides: triggerfix, CanTouchTimerZone, OnTeleport, OnStartTouchGround…); our VNL registers none. Mode-switch safety (stop timer/zero vel/invalidate) missing. Per-mode jumpstat tiers not wired. |
| Styles | 🟡 PARTIAL | ABH/LGJ faithful. **AutoUnduck missing** (1 of cs2kz's only 3 real styles). Ice/LowGrav/WSOnly/ADOnly are *invented* (cs2kz never built them). No style-incompatibility check (ADOnly+WSOnly would freeze WASD). No timer-stop on style change. |
| Timer | 🟡 PARTIAL | Source2Surf surf-timer engine + a thin PRO/STANDARD label. Start-gate is `alive && Walk` only — cs2kz's teleport/land/noclip/perf debounce + safeguard-pro enforcement missing. Pause doesn't truly freeze. No global submission state machine. |
| Checkpoints | 🟡 PARTIAL | cp/tp list-cycle + PRO coupling faithful. `undo` is a *different* feature (deletes last cp vs cs2kz reverting the last teleport). Startpos not DB-persisted (cs2kz's is). Trigger-modifier guards missing. |
| Zones / Triggers | 🟡 PARTIAL | Mapping-API zones (Start/End/Stage/Checkpoint) now register via keyvalues + origin correlation, alongside the legacy targetname path. Still missing: cs2kz's TriggerFix anti-dodge trace re-detection, the modifier/antibhop/teleport/push/bhop-counter trigger types, and the Split zone type. |
| Mapping API | 🟡 PARTIAL | Source built + live-verified: `MapApiSourceModule` reads entity keyvalues (native `CEntityKeyValues` via `CreateWorldInternal` detour) → feeds the parser → zones register. Missing: trigger-modifier keyvalues, multi-course track mapping, ZoneSplit. |
| Courses | 🟡 PARTIAL | Multi-course exists as anonymous int tracks (0=main,1-31=bonus), not named `KZCourseDescriptor`. No Mode dimension in the record key, no course-switch-on-start-zone-touch. |
| Global API | 🔴 MISSING | Submit-only shell: `hello` + one-way `NewRecord`, ack-blind (parses only `hello_ack`). No PB/WR/top/map/course queries, no ban sync, no replay transfer, no `filter_id`/course concept. Would likely not validate against a real backend. Needs an issued key regardless. |
| Racing / 1v1 | 🔴 MISSING | 0% built. cs2kz's is a WebSocket cross-server coordinator (gated on their backend). |
| Jumpstats | 🟡 PARTIAL | Distance/strafes/gain/maxspeed/height + DB persistence + jump-type classify (+Jumpbug enum). Per-mode/per-type tier tables (CKZ/VNL) wired. **sync/badAngles/overlap/deadAir/width now bit-exact** via the Core AACall telemetry (`IKzMovementTelemetry` ← AirAccelerate detour capturing velocity pre/post + wishspeed + buttons + subtick duration) run through the cs2kz `Strafe::End` classification, with a per-tick fallback if telemetry is off. External gain/loss now computed from the AACall's cross-tick speed delta (moveDataPost). Still missing: edge/block, gain-efficiency % (needs per-call accel+friction in the AACall), strict validation. |
| Anticheat | 🟡 PARTIAL | All 6 cs2kz detectors wired; strafe_optimizer 1:1; nulls/snaptap cover both LR+FB axes; subtick desubtick check; bhop teleport/noclip guards; autostrafe skips styled players. Enforcement pipeline exists: infraction counter → auto-ban (`kz_ac_autoban`, threshold/duration convars, fixed-window dedupe) via the Ban service. Still thinner than cs2kz's Infraction→Finalize (no replay-evidence attachment; per-detector tuning coarser). |
| Replays | 🟡 PARTIAL | Record + bot-driven playback are real. Frame format is a subset (no duck/jump/subtick/weapon). "Global" storage is a self-hosted blob store, **not interoperable** with cs2kz.org. |
| Options / Preferences | 🟡 PARTIAL | Untyped `Dictionary<string,string>`, only 3 of cs2kz's 65 keys wired (mode/fov/styles). No typed store, no local/global merge, no server-config option layer. Several "ported" utils silently lose state on reconnect because of this. |
| Language / i18n | 🔴 MISSING | 44 keys, `en-US` only, no switching command/pref. cs2kz has 713 phrases × 13 languages. ~6% of phrase volume; the switching mechanism doesn't exist. |
| HUD | 🟡 PARTIAL | One fixed HTML block, no perf-color tint, `mm:ss.mmm` not `h:mm:ss`, no commands, no compact mode, no particle MHUD. |
| Tip | 🟡 PARTIAL | Broadcast cycling works. No configurable interval, no file-scan/shuffle loading, no join side-effects. |
| Ban | 🟡 PARTIAL | Manual `!ban`/`!unban` with ACL works (cs2kz core doesn't even expose that as a command). But no automated AC→ban pipeline; `RemoveBans` hard-deletes vs soft-expire. |
| Goto / Fov / Measure / Pistol / Noclip | 🟡 PARTIAL | Degraded reimplementations: Goto (no timer-block/ladder/collision-reset, wrong arrival angle), Fov (no per-tick reassertion), Measure (measures own position vs cs2kz's aim-trace; 3/4 cmds missing), Pistol (additive-give vs strip-enforce, no team-awareness, no persist), Noclip (no death-auto-disable/safeguard gate — anti-exploit hole, embedded in TimerModule). |
| Quiet/hide · Beam · Paint · Ztopwatch · Spec-by-name · Rank-titles | 🔴 MISSING | Entirely unbuilt — no files, commands, or locale keys. |
| Saveloc/loadloc | ⚪ N/A | cs2kz's own is an empty stub — not a Kreedz gap. |
| Database | 🟡 PARTIAL | SqlSugar dual-backend + LiteDB fallback works (live). No CRC-versioned migration ledger (cs2kz has one). Drops Modes/Styles registry tables. |
| Commands | 🟡 PARTIAL | Dict dispatch with **real ACL** (better than cs2kz's flat scan) but no cooldowns; ~87 vs cs2kz's ~132 names, the shortfall tracking the missing subsystems above. |

## Proposed priority order to reach real 1:1

1. **Mapping API wiring + cs2kz zone/trigger engine** — unblocks real kz_ maps (zones→timer). Needs a live entity-keyvalue source (native `CEntityKeyValues` read on spawn) + porting the 14 trigger types + TriggerFix.
2. **Prestrafe metric fix** (wishdir-vs-velocity) — headline CKZ mechanic, small change.
3. **VNL real movement + both-modes CanTouchTimerZone / triggerfix** — core fidelity.
4. **Timer start-gate / pause / safeguard-pro** — real KZ timer semantics.
5. **Jumpstats per-mode/per-type tier tables + missing stats + Jumpbug**.
6. **AC → ban enforcement pipeline** (Infraction→Finalize).
7. **Global-API full client** (queries + ack handling) — validate against an issued key.
8. **Missing utilities**: quiet/hide, beam, paint, spec-by-name, rank-titles, ztopwatch.
9. **Real i18n** (phrase system + per-player language).
10. **Mode/style safety guards, checkpoint startpos persistence, degraded-util polish, DB migration ledger.**
11. **Racing** (last — gated on KZGlobalteam's coordinator backend).

## What genuinely works right now (live-verified on f1b935e4)

All modules load clean; SQL DB connected; VNL + 6 styles + jumpstats + anticheat + HUD register; CKZ managed
physics (prestrafe/perf/air-cap) + the 3 Core-owned native detours install and dispatch; `!` command layer,
ranks, manual ban, checkpoints, mode/fov/style prefs. Movement *feel* on legacy-named maps works; modern
mapping-API maps do not (see the ⚠️ above).
