# CS2KZ on Timer — Architecture & 1:1 Feature-Parity Plan

Status: **PLAN (2026-07-21)**. Goal (prefix): fork `Source2Surf/Timer` → build a full **Kreedz (KZ)**
gamemode on it, a **1:1 feature reimplementation of `KZGlobalteam/cs2kz-metamod`**, public under the
`yappershq` org. This doc is the source of truth for the build; it maps every cs2kz feature onto the
Timer base and phases the work.

Base: `yappershq/Timer` (C# ModSharp SurfTimer, .NET 10, AGPL-3.0). Reference: `cs2kz-metamod` (C++
Metamod:Source, ~57k LOC, HEAD `4cd7b19`). Both cloned locally and fully mapped (two deep-read passes).

---

## 1. Strategy: Timer is the chassis; KZ's "soul" is the net-new work

Timer already provides **~60–70% of a KZ timer** as *gamemode-agnostic* infrastructure. We reuse the
spine and build the KZ-specific systems as new `IModule`s bolted onto Timer's existing hook points and
`ListenerHub`. The disciplined rule (house lesson `porting_carries_source_idioms`): do a **rename +
redefine pass up front** so surf idioms (`surf_*` tables, `Jumps/Strafes/Sync` columns, convar-only
"styles", English-only strings) don't leak into the KZ port.

### Reuse ~as-is from Timer
| Timer capability | File(s) | KZ use |
|---|---|---|
| Movement-hook scaffold (`PlayerProcessMovePre/Post`, `PlayerRunCommand`, `PlayerGetMaxSpeed`, `PlayerWalkMove`) | `Timer/Modules/Timer/Movement.cs`, `StyleModule.cs` | the injection seam for KZ's movement model |
| Zones + in-game `!zone` editor + runtime `Script_CreateTrigger` + DB persistence | `Timer/Modules/ZoneModule.cs`, `Zone/Builder.cs`, `Zone/ZoneMatcher.cs` | KZ start/end/split/stage/course zones map 1:1 |
| DB layer — SqlSugar dual-backend (MySQL/Postgres) + LiteDB offline fallback + transparent proxy | `Timer.RequestManager/Storage/*`, `RequestManager.Proxy.cs`, `RequestManager.LiteDB.cs` | reuse wholesale; redefine run columns for KZ |
| Replay record/playback + fake-client ghost bots (movement-only frames, Zstd+MemoryPack, remote blob store) | `ReplayRecorderModule.cs`, `ReplayPlaybackModule.cs`, `IReplayStorage` | KZ replays/ghosts |
| Styles/modes framework (N modes keyed through the whole DB by `Style` int) | `StyleModule.cs`, `StyleSetting.cs` | KZ modes (VNL/CKZ) + styles (ABH/AUD/LGJ) |
| Timer/run-state + `ListenerHub<T>` pub/sub + DI lifecycle (`IManager`/`IModule`, Init→PostInit→Shutdown) | `TimerModule.cs`, `TimerInfo.cs`, `ListenerHub.cs`, `InterfaceBridge.cs`, `ModuleDI.cs` | every KZ subsystem = a new `IModule` |
| Center-HTML HUD (flash-free) + command router | `HudModule.cs`, `Managers/Command/CommandManager.cs` | KZ HUD + commands |
| Score/ranking engine (tier/pot based) + per-map/per-tier config pipeline | `ScoreCalculator`, `MapInfoModule.cs`, `MapConfig.cs` | KZ tier 1–7 scoring |

### Net-new (the KZ "soul") — see phased plan
1. **KZ movement model** (the crux — §4). 2. **cp/tp save-loc practice loop** (Timer has the teleport +
position-save primitives but not the cp/tp/undo/prev-next loop). 3. **Jumpstats** (LJ/BH/MBH/WJ/LAJ/… +
distance tiers + validation). 4. **Global API client** (authenticated ranking — §5). 5. **Localization**
(Timer is hardcoded English; use `ILocalizerManager`). 6. **KZ anticheat** (6 detectors). 7. **KZ trigger
types** (15 kinds beyond Timer's start/end/split).

---

## 2. Two honest caveats to settle with prefix before deep build

**(A) "1:1 with the *official* cs2kz global leaderboard" has an external dependency we don't control.**
cs2kz-metamod ranks on `wss://api.cs2kz.org` and that API **checksum-validates the plugin and each
mode** (per-platform MD5) and requires a **bearer API key** issued by KZGlobalteam; runs also require the
player to have **Steam Prime**. A clean-room C# reimplementation will not pass their official global
validation without KZGlobalteam's cooperation (a new plugin/mode checksum + key). So:
- We can deliver a **1:1 *feature* clone** (all modes/timer/cp-tp/jumpstats/hud/replays/anticheat + **our
  own** global ranking, reusing Timer's `IRequestManager` proxy pattern) — fully playable KZ.
- Joining the **official** cs2kz.org leaderboard is a separate ask to KZGlobalteam. **Recommend:** build
  standalone with our own ranking now, structured so an `HttpGlobalApi` (their WS contract, documented in
  §5) can slot in behind `IRequestManager` later *if* they issue us a key + accept our mode checksums.

**(B) Bit-faithful movement is the hard requirement for KZ times to be comparable.** cs2kz detours **26
native CS2 movement functions** and reimplements CKZ's `TryPlayerMove`/rampbug/prestrafe/perf math
bit-for-bit. Timer rides *stock* CS2 movement and only nudges convars. Matching CKZ times exactly means
porting that movement math (§4) — the single biggest, riskiest chunk. Achievable, but it's where most of
the effort goes and where "1:1" is won or lost.

---

## 3. Project layout (extends the Timer solution)

Rename the solution KZ-ward and add KZ modules alongside the reused ones:

```
Cs2Kz.slnx  (was SurfTimer.slnx)
  Timer/              → Kz/            (main plugin; keep the DI/InterfaceBridge/ListenerHub spine)
    Modules/
      (reused)  ZoneModule, TimerModule, HudModule, ReplayRecorder/Playback, MapInfoModule, ScoreCalculator
      (new)     MovementModule, ModeModule (+ Vnl/Ckz), StyleModule(KZ), CheckpointModule (cp/tp),
                JumpstatsModule, TriggerModule (15 KZ trigger types), AnticheatModule, GlobalModule,
                OptionModule (per-player prefs), MiscModule (restart/end/goto/spec/noclip/…), NicePack
  Kz.Shared/          → contract (IKzService, run/record models, mode/style enums)
  Kz.RequestManager/  → DB (reused; KZ schema) + a future HttpGlobalApi impl behind IRequestManager
  tools/ReplayStorageServer/ → reused
  .assets/locales/    → NEW: kz.<culture>.json (ILocalizerManager) — ported from cs2kz translations/
```

Each KZ subsystem is an `IModule` + (where cross-module) a `ListenerHub` interface — the uniform Timer
pattern. Per-player KZ state = a `KzPlayer`-equivalent holding the service set (mirrors cs2kz's
`KZPlayer` owning ~28 services), keyed by slot/SteamID (never store pawns across ticks).

---

## 4. The movement engine (the crux)

cs2kz: `movement::InitDetours()` detours **26** funcs (`ProcessMovement`, `TryPlayerMove`, `AirAccelerate`,
`Friction`, `Duck`/`CanUnduck`, `LadderMove`, `CheckJumpButton{Legacy,Modern}`, `AirMove`, `WalkMove`,
`CategorizePosition`, `CheckFalling`, `PostThink`, `PhysicsSimulate`, …) via gamedata sigs, each
`On<Hook>()` → original → `On<Hook>Post()`, fanning out **mode → styles (last-wins)**. Two modes:
- **VNL** — faithful CS2 (`TryPlayerMove` 1:1, `sv_bounce` surf reflection, AG2 stuck-fix, 250 clamp,
  full-tick timer zones).
- **CKZ** — the gameplay heart: 33 mode-cvar values (accelerate 6.5, airaccelerate 100, air_max_wishspeed
  30, friction 5.2, jump_impulse 302, stamina 0, legacy_jump true, …) + custom per-tick pipeline
  (velocity-quantization fix, crouch-jump-bind removal, duck-slowdown reduction, view-angle interpolation,
  **prestrafe** [0.02s turn-rate window → speed bonus, PS_SPEED_MAX 26], **bhop/perf** [≤0.02s ground →
  perf speed `min(land/takeoff, (51.5−groundtime*75)*log(v) − norm)`], **custom TryPlayerMove + rampbug
  fix** [0.0625u pierce, 0.98 dot], **SlopeFix**, **subtick half-tick forcing**, half-tick timer zones).

**ModSharp reality:** it exposes coarser forwards (`PlayerProcessMovePre/Post`, `RunCommand`,
`GetMaxSpeed`, `WalkMove`) — not the 26 sub-function detours. Two ways to get bit-faithful CKZ:
- **(a) Native sub-function detours via gamedata sigs** — Timer already resolves natives via gamedata +
  Iced disassembly, so ModSharp *can* install native hooks. Reproduce cs2kz's sig set for the sub-funcs.
  Highest fidelity, but ~26 sigs to maintain across CS2 updates (fragile).
- **(b) Replace stock movement inside `ProcessMovePre`** using the **house CGameMovement port**
  (`reference_cs2_movement_port` → `MovementService`) as the base, then layer CKZ's math (prestrafe/perf/
  rampbug/slopefix) on top. Fewer sigs, one big port to keep accurate.

**Recommendation:** **Phase it.** Phase-A ship **VNL** first (achievable by riding stock movement +
minimal tweaks, like Timer does) to prove the timer/zones/cp-tp/hud/db end-to-end. Phase-B build **CKZ**
via approach (b) (port the house `MovementService`, then port cs2kz's CKZ math onto it), falling back to
targeted native detours (a) only for the sub-functions where (b) can't match. Treat bit-exact parity with
official cs2kz times as a **stretch goal** measured against recorded cs2kz demos.

---

## 5. Data & Global API

**KZ DB schema** (greenfield C# via SqlSugar; keep cs2kz table *shapes* so a future migration/global sync
is clean — CRC32 migration reproduction only needed if we must read existing cs2kz DBs, which we don't):
`Players`(SteamID64 PK, Alias, IP, Preferences JSON, LastPlayed, Created), `Modes`(ID, Name, ShortName),
`Styles`(ID, Name, ShortName), `Maps`(ID, Name, …), `MapCourses`(ID, MapID, Name, StageID),
`Times`(**ID UUIDv7**, SteamID64, MapCourseID, ModeID, **StyleIDFlags bitmask** [0=styleless=ranked],
RunTime, Teleports, Metadata, Created), `Bans`(ID UUID, SteamID64, Reason, ReplayUUID, ExpiresAt, Created).
Ranking rules to match: **styleless-only** (`StyleIDFlags=0`), **Pro (0 tp) vs Standard (≥1 tp)**, exclude
banned (`LEFT JOIN Bans … IS NULL`), cheater = active Bans row (not a Players column). `Jumpstats`/
`StartPosition` tables in cs2kz are dead — we implement real jumpstats persistence if wanted (design choice).

**Global API contract** (documented for the future `HttpGlobalApi` — NOT the official one unless keyed):
persistent WebSocket, `Authorization: Bearer <key>`, envelope `{"id","event","data"}`, binary =
`<json>\n<bytes>`. Handshake `hello`→`hello_ack` (map vpk_checksum + modes/styles checksums + announcements);
steady-state `player-join/leave/prime-confirmed`, `new-record`, `want-*` queries (mode as u8: VNL=1, CKZ=2),
`new-replay` (binary). Record submit `{player_id, filter_id, mode_md5, teleports, time, styles[], metadata}`
→ `NewRecordAck{record_id, pb_data}` (5s timeout → local UUID fallback). Player eligibility = authenticated
+ Prime + not-banned. We can stand up **our own** coordinator implementing this shape, or a simpler REST
ranking behind `IRequestManager` — decide with prefix.

---

## 6. Full 1:1 feature checklist (grouped; → build phase)

**CORE (KZ unplayable without):**
- Movement engine (26-hook fan-out; VNL + CKZ) → **P2/P5**
- Mode (`kz_mode`, per-mode `kz_<short>`; 33 mode-cvars; switch stops timer/zeroes vel/invalidates JS) → **P2/P5**
- Timer (`kz_stop/pause/safeguard/pro/…` + record queries `kz_pb/wr/ctop/…`; strict start/end validation;
  Pro/Standard; compare types SPB/GPB/SR/WR; pause rules; safeguard; split/checkpoint/stage zones) → **P2**
- Checkpoint/cp-tp (`kz_cp/tp/undo/prevcp/nextcp/setstartpos/…`; ground/ladder gate; tpCount→Standard;
  sounds/messages prefs) → **P2**
- HUD (`kz_panel`, `kz_mhud`; keys/cp-tp/timer/speed; perf/jumpbug tint) → **P2**
- Database (schema above; async; UUIDv7 runs) → **P1**
- MappingAPI + Trigger (course descriptors; **15 `KzTriggerType`s**: MODIFIER/ANTI_BHOP/ZONE_*/TELEPORT/
  MULTI|SINGLE|SEQUENTIAL_BHOP/PUSH/RESET_CHECKPOINTS…; ref-counted modifiers; tick-trace touch engine) → **P2/P4**
- Misc glue (`kz_restart/end/lj/hide/playercheck`; colored chat; jointeam gate; time_limit lockstep;
  godmode; turnbind disable) → **P2**

**IMPORTANT:**
- Jumpstats (types LJ/BH/MBH/WJ/LAJ/LAH/JB/Fall; distance tiers Meh→Wrecker per-mode tables; ~20 stats;
  validation; styles force invalid) → **P3**
- Style (ABH/AUD/LGJ; stackable, layered after mode; separate leaderboard category; disables JS/PB-UI/AC) → **P3**
- Global API client (§5) → **P6**
- Recording + Replays (2-min circular buffer; run/jump/manual recorders; `.replay` v5; `kz_replay …`) → **P4** (Timer replay reused)
- Racing (cross-server via coordinator WS; `kz_accept/surrender`) → **P7 (optional)**
- Anticheat (6 detectors: nulls/snaptap, bhop, hyperscroll, invalid-cvar, strafe-hack, subtick; `kz_unban`) → **P6**
- Spec (`kz_spec/specs`; dead/paused-only; @me self-spec; single-slot SavePosition) → **P3**

**NICE:** paint, tip, telemetry (perf stats), quiet (transmit-filter backbone), fov, ztopwatch, pistol,
beam, measure, goto, profile (rank badge/clantag from API), option (pref framework — infra, do early),
language (i18n — do early), noclip. (`saveloc` in cs2kz is an empty stub — skip.) → **P8**

---

## 7. Phased delivery

- **P0 — Rebrand & green build.** Fork built + running as-is; rename SurfTimer→Cs2Kz, `surf_*`→`kz_*`,
  strip surf-only assumptions; solution builds & loads. Stand up `OptionModule` (per-player prefs) +
  `ILocalizerManager` (i18n) early — everything downstream depends on them.
- **P1 — KZ data model.** KZ schema (Modes/Styles/Maps/Courses/Times/Bans), run columns redefined
  (UUIDv7, StyleIDFlags, Teleports, Pro/Standard), ranking queries (styleless, ban-excluded).
- **P2 — Playable KZ (VNL).** Mode framework + VNL mode; KZ timer with strict start/end validation;
  cp/tp save-loc loop; KZ trigger types + mappingapi course parsing; HUD; core commands. *Done: you can
  run a KZ map start→end, set cps/tps, get a Pro/Standard time saved + ranked.*
- **P3 — Jumpstats + styles + spec.** Jump detection/tiers/validation + broadcasts; ABH/AUD/LGJ styles
  (layered after mode); spectator system.
- **P4 — Replays.** Wire Timer's recorder/playback + ghost bots to KZ runs (UUID-linked); `kz_replay`.
- **P5 — CKZ movement.** Port the house `MovementService` + cs2kz CKZ math (prestrafe/perf/rampbug/
  slopefix/subtick); validate times against recorded cs2kz demos. *This is the big one.*
- **P6 — Ranking + anticheat.** Our-own global ranking behind `IRequestManager` (+ document the official
  WS contract for a future keyed integration); the 6 anticheat detectors + ban pipeline.
- **P7 — Racing (optional).** Cross-server races if wanted.
- **P8 — NICE pack + polish.** paint/tip/beam/measure/goto/fov/pistol/profile/telemetry/ztopwatch; README;
  public release on yappershq.

---

## 7b. P1 data-model mapping (Timer entity → KZ schema — the concrete edits)

Timer's entities (`Timer.RequestManager/Common/Entities/`) already model 90% of the KZ shape; P1 is a
targeted redefine, NOT a rewrite. Exact deltas:

| Timer entity | → KZ table | Edits for KZ parity |
|---|---|---|
| `MapEntity` | `Maps` | ~as-is (Id/Name/LastPlayed/Created). |
| `MapTrackEntity` | `MapCourses` | rename track→course; add `StageID`; keep `MapID` FK. Timer's `Track` int already models multiple courses/bonuses. |
| `PlayerEntity` | `Players` | add **`Preferences` TEXT** (KZ per-player options JSON — the OptionModule store); keep SteamID64 PK. |
| `RunEntity` | `Times` | (1) **ID → UUIDv7 string** (Timer uses numeric); (2) add **`Teleports`** (→ Pro=0 / Standard≥1); (3) replace surf `Style` int with **`StyleIDFlags` bitmask** (0 = styleless = ranked); (4) add **`ModeID`** FK; (5) drop surf-only `Jumps/Strafes/Sync` + the 12 velocity floats (→ optional `Metadata` JSON). |
| `RunSegmentEntity` | (per-split times) | keep for split/checkpoint/stage times. |
| `PlayerBestRunEntity` | (materialized PB) | reuse; re-key by `(ModeID, MapCourseID, StyleIDFlags=0, Pro?)`. |
| `ZoneEntity` | (KZ zones) | reuse; extend zone-type enum to KZ trigger types (§6). |
| `ReplayEntity` | (replay URL) | reuse; link by run UUID. |
| — (none) | **`Bans`** (NEW) | UUID Id, SteamID64 FK, Reason, ReplayUUID, ExpiresAt, Created — KZ needs it (anticheat + ranking exclusion `LEFT JOIN Bans … IS NULL`). No Timer equivalent. |
| — (none) | **`Modes`** / **`Styles`** (NEW, optional) | id/name/shortname registries so runs FK a mode/style row (cs2kz shape). Can start as enums + add tables when external modes/styles land. |

`IRequestManager` (`Timer.Shared/Interfaces/IRequestManager.cs`) surface stays the seam — `AddPlayerRecord`/
`GetPlayerRecord`/`GetMapRecords` gain KZ params (mode, teleports, pro/standard); the LiteDB fallback +
SqlSugar proxy get the same edits. Ranking queries add the styleless + ban-exclusion filters. **This is the
first coding step (P1) and is fully decision-independent** — it's the same schema whether ranking is
our-own or official later.

## 8. Open decisions (for prefix)
1. **Official global vs our own ranking?** (caveat §2A) — recommend our-own now, official later if
   KZGlobalteam issues a key + accepts our mode checksums.
2. **Movement fidelity bar** — "feels like KZ" (approach b, faster) vs "bit-exact cs2kz times" (a+b,
   much harder, only meaningful if joining official global)?
3. **Modes/styles as separate loadable plugins (cs2kz style) or in-repo modules?** — recommend in-repo
   modules first (simpler); external-plugin loading later.
4. **DB backend** — MySQL (fleet) reusing Timer's dual-backend, LiteDB offline fallback kept.
5. **Scope of NICE tier for v1** — ship CORE+IMPORTANT first; NICE as fast-follow.
