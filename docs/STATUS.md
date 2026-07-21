# KZ Port — Status

A 1:1 feature reimplementation of [KZGlobalteam/cs2kz-metamod](https://github.com/KZGlobalteam/cs2kz-metamod)
built on a fork of [Source2Surf/Timer](https://github.com/Source2Surf/Timer). Every named subsystem has
real, working, `-c Release`-green code. See `KZ_PORT_PLAN.md` for the architecture and `EXTENSIBILITY.md`
for the 3rd-party mode/style split.

## Subsystems

| System | State | Notes |
|---|---|---|
| Timer | ✅ | PRO (0 teleports) / STANDARD (≥1) run semantics on Timer's run timer. |
| Checkpoints / teleports | ✅ | `cp/tp/undo/prevcp/nextcp/setstartpos/clearstartpos`, teleport counter → Pro/Standard. |
| Modes | ✅ | External plugins `Kreedz.Mode.VNL` + `Kreedz.Mode.CKZ` (real cs2kz prestrafe/perf math) via `IKzModeRegistry`. |
| Styles | ✅ | External plugins `Kreedz.Style.{ABH,LGJ,LowGrav,Ice,WSOnly,ADOnly}` via `IKzStyleRegistry`. |
| Native movement detours | 🟡 | AirAccelerate→FinishMove hooked (sigs + typed `MoveData`), ON by default, **pass-through** — physics fill + FinishMove vhook pending live validation. |
| Jumpstats | 🟡 | **Basic** — LJ/BH + distance tiers only. NOT the full cs2kz stat set (strafes/sync/gain/height/airtime, jump-type classification, invalidation, edge distance). |
| HUD | ✅ | Center-HTML speed / keys / mode / tp panel (flash-fixed). |
| DB | ✅ | SqlSugar dual-backend + LiteDB fallback; `kz_bans`, `kz_preferences`, teleports persisted. |
| Ranks | ✅ | Points + rank, ban-excluded leaderboards, `wr/pb/rank/top/recent/...`. |
| Global API | ✅ | WebSocket client to api.cs2kz.org (hello/hello_ack + NewRecord). Dormant without `kz_global_apikey`. |
| Anticheat | ✅ | Invalid-cvar + bhop-hack (inhuman perf-chain) detectors. |
| Ban management | ✅ | `!ban`/`!unban` (@kz/ban) + connect-time kick, persisted across restarts. |
| Preferences | ✅ | Mode / FOV / styles persist across reconnect. |
| Utilities | ✅ | `goto`, `fov`, `measure`, `pistol`, `tip`. |

## The two live-gated asterisks (external, not missing code)

1. **Official global submission** needs a real API key issued by KZGlobalteam — their backend
   checksum-validates the plugin, which a clean-room reimplementation can't spoof. The client is
   built and ready; local ranking runs regardless.
2. **Movement tick-fidelity** — the CKZ prestrafe/perf math is transcribed exactly from source and
   its boundaries are unit-checked, but certifying leaderboard-identical times needs a live server +
   recorded cs2kz demos to compare against. Not doable headless.

## Not started (optional / future)

- The 4 cs2kz-*planned* styles (Low-Grav, Ice, W/S-only, A/D-only) — upstream hasn't shipped them.
- Extra AC telemetry detectors (nulls/snaptap, hyperscroll, strafe-hack) — need real movement data to tune.
- Wiring the 3rd-party mode/style split (contract designed in `EXTENSIBILITY.md`; needs an example plugin).
- Racing / 1v1.
