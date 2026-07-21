/*
 * yappershq/Kreedz (KZ) — CKZ native movement detours
 *
 * The bit-exact path: detours the granular CS2 movement pipeline (AirAccelerate → FinishMove) the same
 * functions cs2kz raw-detours, resolved from sigs in kreedz-ckz.games.jsonc and hooked via ModSharp's
 * IDetourHook. This is what makes CKZ times leaderboard-identical (rampbug/slopefix in TryPlayerMove,
 * exact air-accel curve, ladder physics) rather than the ProcessMove-level approximation.
 *
 * ON by default (`kz_ckz_native_hooks`, default 1 — set 0 if a sig breaks after a CS2 update). Each
 * detour reads/writes native movement through ModSharp's ported `MoveData` struct (CMoveData) via
 * `Unsafe.AsRef` — no hand-ported offsets — so the CKZ physics is filled with typed field access
 * (`md.Velocity`, `md.AbsOrigin`, `md.MaxSpeed`, …).
 *
 * FILLED so far: **CategorizePosition → rampbug fix** (cs2kz OnCategorizePosition), a real physics
 * correction — the raw detour resolves the player via a `{movementService* → slot}` map populated from
 * the managed hook (`IPlayerMovementService.GetAbsPtr()`), tracks `lastValidPlane` best-effort from the
 * per-tick ground trace, and nudges the origin off a rampbug seam via `TraceShapePlayerMovement`. The
 * remaining detours are still pass-through; TryPlayerMove's full collision loop + FinishMove vhook are
 * the next fills. EXPERIMENTAL — this modifies live movement and is best-effort from source; it needs
 * tick-for-tick demo validation on a real server (per prefix's go-ahead to proceed without server tests).
 *
 * Native signatures are cs2kz's movement.h detour typedefs verbatim; x64 uses the platform default
 * calling convention (this in rcx/rdi). FinishMove is a vtable func (offset 38/39) — a virtual hook, TODO.
 */

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Hooks;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Kreedz.Mode.Ckz;

internal sealed unsafe class MovementDetours
{
    private const string Ns = "CCSPlayer_MovementServices::";

    // cs2kz kz_mode_ckz.h — rampbug/slopefix constants.
    private const float RampBugThreshold   = 0.98f;   // RAMP_BUG_THRESHOLD
    private const float RampPierceDistance = 0.0625f; // RAMP_PIERCE_DISTANCE
    private const float NewRampThreshold   = 0.95f;   // NEW_RAMP_THRESHOLD
    private const int   MaxBumps           = 4;       // MAX_BUMPS
    private const float Epsilon            = 0.00001f;
    private const float FltEpsilon         = 1.192092896e-07f;

    /// <summary>The full TryPlayerMove slopefix reimplementation — off by default (`kz_ckz_tpm`). Its
    /// result is applied only when a rampbug is actually detected, so the blast radius is bounded to
    /// rampbug cases; still EXPERIMENTAL until demo-validated.</summary>
    public bool TpmEnabled { get; set; }

    private readonly IHookManager             _hookManager;
    private readonly IPhysicsQueryManager     _physics;
    private readonly IGameData                _gameData;
    private readonly ILogger                  _logger;
    private readonly List<IRuntimeNativeHook> _hooks = [];

    private static nint _tFinishMove;

    // Reverse map {native CCSPlayer_MovementServices* -> slot}, filled from the managed ProcessMove hook
    // (which has both the pawn and the slot) so the raw native detours can resolve the player.
    private const float SpeedNormal = 250.0f; // cs2kz SPEED_NORMAL

    private readonly Dictionary<nint, int> _slotByMs   = new();
    private readonly Vector[]              _lastPlane   = new Vector[PlayerSlot.MaxPlayerCount];
    private readonly float[]               _gain        = new float[PlayerSlot.MaxPlayerCount];

    private static MovementDetours? _self;

    // Trampolines to the originals (static so the [UnmanagedCallersOnly] hooks can reach them).
    private static nint _tProcessMovement, _tAirAccelerate, _tFriction, _tWalkMove, _tAirMove, _tTryPlayerMove,
                        _tCategorizePosition, _tCheckJumpLegacy, _tCheckJumpModern, _tDuck, _tCanUnduck,
                        _tCheckVelocity, _tCheckWater, _tWaterMove, _tLadderMove, _tCheckFalling,
                        _tFullWalkMove, _tMoveInit;

    public MovementDetours(IHookManager hookManager, IPhysicsQueryManager physics, IGameData gameData, ILogger logger)
    {
        _hookManager = hookManager;
        _physics     = physics;
        _gameData    = gameData;
        _logger      = logger;
    }

    /// <summary>Register a player's native movement-service pointer → slot (call each tick from the
    /// managed hook where the pawn+slot are known). Lets the raw native detours resolve the player.</summary>
    public void Map(nint movementServicePtr, int slot)
    {
        if (movementServicePtr != 0)
            _slotByMs[movementServicePtr] = slot;
    }

    /// <summary>Share the player's current prestrafe gain (computed in the managed hook) so the AirMove
    /// detour can restore max speed to 250+gain after the air-move (cs2kz OnAirMovePost).</summary>
    public void SetGain(int slot, float gain) => _gain[slot] = gain;

    public bool Installed { get; private set; }

    public void Install()
    {
        if (Installed) return;

        _tProcessMovement    = Hook("ProcessMovement",       (nint)(delegate* unmanaged<nint, nint, void>)&Hk_ProcessMovement);
        _tAirAccelerate      = Hook("AirAccelerate",         (nint)(delegate* unmanaged<nint, nint, nint, float, float, void>)&Hk_AirAccelerate);
        _tFriction           = Hook("Friction",              (nint)(delegate* unmanaged<nint, nint, void>)&Hk_Friction);
        _tWalkMove           = Hook("WalkMove",              (nint)(delegate* unmanaged<nint, nint, void>)&Hk_WalkMove);
        _tAirMove            = Hook("AirMove",               (nint)(delegate* unmanaged<nint, nint, void>)&Hk_AirMove);
        _tTryPlayerMove      = Hook("TryPlayerMove",         (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&Hk_TryPlayerMove);
        _tCategorizePosition = Hook("CategorizePosition",    (nint)(delegate* unmanaged<nint, nint, byte, void>)&Hk_CategorizePosition);
        _tCheckJumpLegacy    = Hook("CheckJumpButtonLegacy", (nint)(delegate* unmanaged<nint, nint, void>)&Hk_CheckJumpLegacy);
        _tCheckJumpModern    = Hook("CheckJumpButtonModern", (nint)(delegate* unmanaged<nint, nint, void>)&Hk_CheckJumpModern);
        _tDuck               = Hook("Duck",                  (nint)(delegate* unmanaged<nint, nint, void>)&Hk_Duck);
        _tCanUnduck          = Hook("CanUnduck",             (nint)(delegate* unmanaged<nint, nint, byte>)&Hk_CanUnduck);
        _tCheckVelocity      = Hook("CheckVelocity",         (nint)(delegate* unmanaged<nint, nint, nint, void>)&Hk_CheckVelocity);
        _tCheckWater         = Hook("CheckWater",            (nint)(delegate* unmanaged<nint, nint, byte>)&Hk_CheckWater);
        _tWaterMove          = Hook("WaterMove",             (nint)(delegate* unmanaged<nint, nint, void>)&Hk_WaterMove);
        _tLadderMove         = Hook("LadderMove",            (nint)(delegate* unmanaged<nint, nint, byte>)&Hk_LadderMove);
        _tCheckFalling       = Hook("CheckFalling",          (nint)(delegate* unmanaged<nint, nint, void>)&Hk_CheckFalling);
        _tFullWalkMove       = Hook("FullWalkMove",          (nint)(delegate* unmanaged<nint, nint, byte, void>)&Hk_FullWalkMove);
        _tMoveInit           = Hook("MoveInit",              (nint)(delegate* unmanaged<nint, nint, byte>)&Hk_MoveInit);

        InstallFinishMoveVHook();

        _self    = this;
        Installed = true;
        _logger.LogWarning("[CKZ] native movement detours INSTALLED — rampbug fix live (EXPERIMENTAL, validate on a test server).");
    }

    public void Uninstall()
    {
        foreach (var hook in _hooks)
            hook.Uninstall();
        _hooks.Clear();
        Installed = false;
    }

    // FinishMove is a vtable func (index 38 Win / 39 Linux in kreedz-ckz.games VFuncs), not a sig — hook
    // it virtually. cs2kz has no CKZ-specific FinishMove physics, so this is a faithful pass-through that
    // completes the detour surface + gives a landing point for any future post-move fill.
    private void InstallFinishMoveVHook()
    {
        try
        {
            if (!_gameData.GetVFuncIndex("CCSPlayer_MovementServices::FinishMove", out var idx))
            {
                _logger.LogWarning("[CKZ] FinishMove vfunc index not in gamedata — skipping vhook.");
                return;
            }

            var hook = _hookManager.CreateVirtualHook();
            hook.Prepare("server", "CCSPlayer_MovementServices", idx, (nint)(delegate* unmanaged<nint, nint, void>)&Hk_FinishMove);

            if (!hook.Install())
            {
                _logger.LogWarning("[CKZ] failed to install FinishMove vhook.");
                return;
            }

            _tFinishMove = hook.Trampoline;
            _hooks.Add(hook);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[CKZ] FinishMove vhook install threw.");
        }
    }

    [UnmanagedCallersOnly]
    private static void Hk_FinishMove(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tFinishMove)(ms, mv);

    /// <summary>Typed view over the raw CMoveData* — ModSharp's ported struct, platform-correct offsets.</summary>
    private static ref MoveData Move(nint mv) => ref Unsafe.AsRef<MoveData>((void*)mv);

    private nint Hook(string name, nint hookFn)
    {
        var hook = _hookManager.CreateDetourHook();
        hook.Prepare(Ns + name, hookFn);

        if (!hook.Install())
        {
            _logger.LogError("[CKZ] failed to install movement detour {Name} (bad sig for this build?)", name);
            return 0;
        }

        _hooks.Add(hook);
        return hook.Trampoline;
    }

    // ── Detours: verified-signature PASS-THROUGH. Fill CKZ physics per function, then validate live. ──
    // cs2kz reference: KZClassicModeService::On<Fn> in src/kz/mode/kz_mode_ckz.cpp.

    [UnmanagedCallersOnly]
    private static void Hk_ProcessMovement(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tProcessMovement)(ms, mv);

    [UnmanagedCallersOnly] // CKZ: custom air-accel curve (high sv_airaccelerate strafing)
    private static void Hk_AirAccelerate(nint ms, nint mv, nint wishdir, float wishspeed, float accel)
        => ((delegate* unmanaged<nint, nint, nint, float, float, void>)_tAirAccelerate)(ms, mv, wishdir, wishspeed, accel);

    [UnmanagedCallersOnly]
    private static void Hk_Friction(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tFriction)(ms, mv);

    [UnmanagedCallersOnly]
    private static void Hk_WalkMove(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tWalkMove)(ms, mv);

    [UnmanagedCallersOnly] // CKZ OnAirMove/Post: cap air wishspeed at 250 during the air-move, restore 250+gain after
    private static void Hk_AirMove(nint ms, nint mv)
    {
        var slot = _self is not null && _self._slotByMs.TryGetValue(ms, out var s) ? s : -1;

        if (slot >= 0) Move(mv).MaxSpeed = SpeedNormal;                    // OnAirMove
        ((delegate* unmanaged<nint, nint, void>)_tAirMove)(ms, mv);        // engine air-move
        if (slot >= 0) Move(mv).MaxSpeed = SpeedNormal + _self!._gain[slot]; // OnAirMovePost
    }

    [UnmanagedCallersOnly] // CKZ: rampbug/slopefix
    private static void Hk_TryPlayerMove(nint ms, nint mv, nint firstDest, nint firstTrace, nint blocked)
    {
        // Capture pre-move state, run the engine move (default result), then our slopefix — which only
        // overwrites the result when it actually detects+fixes a rampbug (bounded blast radius).
        Vector startOrigin = default, startVel = default;
        var run = _self is { TpmEnabled: true } d && d._slotByMs.ContainsKey(ms);
        if (run) { ref var pm = ref Move(mv); startOrigin = pm.AbsOrigin; startVel = pm.Velocity; }

        ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)_tTryPlayerMove)(ms, mv, firstDest, firstTrace, blocked);

        if (run) _self!.TryPlayerMoveFix(ms, mv, startOrigin, startVel);
    }

    // cs2kz KZClassicModeService::OnTryPlayerMove — the slopefix collision reimplementation. Faithfully
    // transcribed from source (the MAX_BUMPS bump loop + the 3x3x3 offset pierce search that detects and
    // corrects rampbugs). overrideTPM is set only when a rampbug is found; only then do we write the
    // computed origin/velocity back, so normal moves pass through the engine untouched. EXPERIMENTAL.
    private void TryPlayerMoveFix(nint ms, nint mv, Vector start, Vector velocity)
    {
        if (!_slotByMs.TryGetValue(ms, out var slot)) return;
        if (Len(velocity) == 0.0f) return;

        var (mins, maxs) = Hull();
        var plane        = _lastPlane[slot];
        var primal       = velocity;
        var frametime    = FrameTime();

        var timeLeft   = frametime;
        var allFraction = 0f;
        var planes      = new Vector[5];
        var numPlanes   = 0;
        var overrideTPM = false;
        var potentiallyStuck = false;
        (float Frac, Vector Normal, Vector End, bool Solid) pm = default;

        for (var bump = 0; bump < MaxBumps; bump++)
        {
            var end = start + velocity * timeLeft;
            pm = Trace(mins, maxs, start, end);
            if (end == start) continue;
            if (IsValidMovementTrace(pm, mins, maxs) && pm.Frac == 1.0f) break;

            var normalChanged = Dot(pm.Normal, plane) < RampBugThreshold;
            var stuck         = potentiallyStuck && pm.Frac == 0.0f;
            var lastWasWall   = plane.Z < 0.03125f;
            var consider      = (normalChanged && !lastWasWall) || stuck;

            if (Len(plane) > FltEpsilon && consider)
            {
                var offsets = new[] { 0.0f, -1.0f, 1.0f };
                var success = false;
                for (var i = 0; i < 3 && !success; i++)
                for (var j = 0; j < 3 && !success; j++)
                for (var k = 0; k < 3 && !success; k++)
                {
                    Vector offDir;
                    if (i == 0 && j == 0 && k == 0) offDir = plane;
                    else
                    {
                        offDir = new Vector(offsets[i], offsets[j], offsets[k]);
                        if (Dot(plane, offDir) <= 0.0f) continue;
                        var test0 = Trace(mins, maxs, start + offDir * RampPierceDistance, start);
                        if (!IsValidMovementTrace(test0, mins, maxs)) continue;
                    }

                    var good = false; var hitNew = false; var validPlane = false;
                    (float Frac, Vector Normal, Vector End, bool Solid) pierce = default;
                    for (var ratio = 0.25f; ratio <= 1.0f; ratio += 0.25f)
                    {
                        pierce = Trace(mins, maxs, start + offDir * RampPierceDistance * ratio, end + offDir * RampPierceDistance * ratio);
                        if (!IsValidMovementTrace(pierce, mins, maxs)) continue;
                        validPlane = pierce.Frac < 1.0f && pierce.Frac > 0.1f && Dot(pierce.Normal, plane) >= RampBugThreshold;
                        hitNew     = Dot(pm.Normal, pierce.Normal) < NewRampThreshold && Dot(plane, pierce.Normal) > NewRampThreshold;
                        good       = MathF.Abs(pierce.Frac - 1.0f) < FltEpsilon || validPlane;
                        if (good) break;
                    }

                    if (good || hitNew)
                    {
                        var test = Trace(mins, maxs, pierce.End, end);
                        var denom = Len(end - start);
                        var frac  = denom > 0f ? Math.Clamp(Len(pierce.End - start) / denom, 0.0f, 1.0f) : 0f;
                        var normal = Len(pierce.Normal) > 0.0f ? pierce.Normal : test.Normal;
                        pm = (frac, normal, test.End, pm.Solid);
                        _lastPlane[slot] = normal;
                        plane = normal;
                        success = true;
                        overrideTPM = true;
                    }
                }
            }

            if (Len(pm.Normal) > 0.99f) _lastPlane[slot] = pm.Normal;
            potentiallyStuck = pm.Frac == 0.0f;

            if (pm.Frac * Len(velocity) > 0.03125f || pm.Frac > 0.03125f)
            {
                allFraction += pm.Frac;
                start = pm.End;
                numPlanes = 0;
            }

            if (allFraction == 1.0f) break;
            timeLeft -= frametime * pm.Frac;

            if (numPlanes >= 5 || (pm.Normal.Z >= 0.7f && Len2D(velocity) < 1.0f)) { velocity = default; break; }

            planes[numPlanes++] = pm.Normal;

            if (numPlanes == 1) // (cs2kz also checks WALK + no ground entity; approximated as air-clip)
            {
                velocity = ClipVelocity(velocity, planes[0]);
            }
            else
            {
                int i, j;
                for (i = 0; i < numPlanes; i++)
                {
                    velocity = ClipVelocity(velocity, planes[i]);
                    for (j = 0; j < numPlanes; j++)
                        if (j != i && Dot(velocity, planes[j]) < 0) break;
                    if (j == numPlanes) break;
                }

                if (i == numPlanes)
                {
                    if (numPlanes != 2) { velocity = default; break; }
                    var cd = Normalize(Cross(planes[0], planes[1]));
                    velocity = cd * Dot(cd, velocity);
                    if (Dot(velocity, primal) <= 0) { velocity = default; break; }
                }
            }
        }

        // Apply only when a rampbug was detected+fixed — otherwise the engine's result stands.
        if (overrideTPM)
        {
            ref var md = ref Move(mv);
            md.AbsOrigin = pm.End;
            md.Velocity  = velocity;
        }
    }

    // cs2kz ClipVelocity (1:1 with CS2): reflect velocity off a plane with the 0.03125 overbounce.
    private static Vector ClipVelocity(Vector inV, Vector normal)
    {
        var backoff = -(inV.X * normal.X + normal.Z * inV.Z + inV.Y * normal.Y);
        backoff = MathF.Max(backoff, 0.0f) + 0.03125f;
        return normal * backoff + inV;
    }

    // cs2kz IsValidMovementTrace — reject start-in-solid, degenerate/deformed normals, and stuck spots.
    private bool IsValidMovementTrace((float Frac, Vector Normal, Vector End, bool Solid) tr, Vector mins, Vector maxs)
    {
        if (tr.Solid) return false;
        if (tr.Frac < 1.0f && MathF.Abs(tr.Normal.X) < FltEpsilon && MathF.Abs(tr.Normal.Y) < FltEpsilon && MathF.Abs(tr.Normal.Z) < FltEpsilon) return false;
        if (MathF.Abs(tr.Normal.X) > 1.0f || MathF.Abs(tr.Normal.Y) > 1.0f || MathF.Abs(tr.Normal.Z) > 1.0f) return false;

        // Unswept trace at the end point to confirm we're not embedded (cs2kz's stuck check). The extra
        // backward trace cs2kz does is skipped here (it's a secondary check + partly commented out upstream).
        var stuck = Trace(mins, maxs, tr.End, tr.End);
        return !stuck.Solid && stuck.Frac >= 1.0f - FltEpsilon;
    }

    // General hull trace start->end, values only (no ref-struct escape).
    private (float Frac, Vector Normal, Vector End, bool Solid) Trace(Vector mins, Vector maxs, Vector start, Vector end)
    {
        var query = RnQueryShapeAttr.PlayerMovement(InteractionLayers.Solid);
        var t = _physics.TraceShapePlayerMovement(new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }), start, end, in query);
        return (t.Fraction, t.PlaneNormal, t.EndPosition, t.StartInSolid);
    }

    private static float FrameTime() => 1f / 64f; // fallback tick (validated pass reads the engine globals)

    private static float Len(Vector v)   => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    private static float Len2D(Vector v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);
    private static Vector Normalize(Vector v) { var l = Len(v); return l > 0f ? new Vector(v.X / l, v.Y / l, v.Z / l) : default; }
    private static Vector Cross(Vector a, Vector b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    [UnmanagedCallersOnly]
    private static void Hk_CategorizePosition(nint ms, nint mv, byte stayOnGround)
    {
        ((delegate* unmanaged<nint, nint, byte, void>)_tCategorizePosition)(ms, mv, stayOnGround); // engine categorize first
        _self?.RampBugFix(ms, mv, stayOnGround != 0);
    }

    // cs2kz KZClassicModeService::OnCategorizePosition — rampbug fix. When dropping fast onto a plane
    // steeper than the last valid one we stood on, nudge the origin back along that plane so the engine
    // doesn't "rampbug" (lose all speed on a seam). EXPERIMENTAL: lastValidPlane is tracked best-effort
    // from the per-tick ground trace (cs2kz threads it through TryPlayerMove) — needs live validation.
    private void RampBugFix(nint ms, nint mv, bool stayOnGround)
    {
        if (!_slotByMs.TryGetValue(ms, out var slot)) return;

        ref var md = ref Move(mv);
        var (mins, maxs) = Hull();
        var origin = md.AbsOrigin;
        var plane  = _lastPlane[slot];

        // Only fix while dropping (vz < -64) onto a plane steeper than a valid last plane we had.
        if (!stayOnGround && Dot(plane, plane) >= Epsilon * Epsilon && plane.Z <= 0.7f && md.Velocity.Z <= -64.0f)
        {
            var trace = TraceDown(mins, maxs, origin);
            if (trace.Fraction != 1.0f
                && trace.Fraction < 0.95f && trace.PlaneNormal.Z > 0.7f && Dot(plane, trace.PlaneNormal) < RampBugThreshold)
            {
                var nudged = origin + plane * 0.0625f;
                var trace2 = TraceDown(mins, maxs, nudged);
                if (!trace2.StartInSolid && (trace2.Fraction == 1.0f || Dot(plane, trace2.PlaneNormal) >= RampBugThreshold))
                {
                    md.AbsOrigin = nudged;
                    origin       = nudged;
                }
            }
        }

        // Track lastValidPlane: the surface we're currently resting on, if it's genuinely standable.
        var g = TraceDown(mins, maxs, origin);
        if (g.Fraction < 1.0f && g.PlaneNormal.Z > 0.7f)
            _lastPlane[slot] = g.PlaneNormal;
    }

    // Returns plain values (not the GameTrace ref struct) so nothing aliases the local query.
    private (float Fraction, Vector PlaneNormal, bool StartInSolid) TraceDown(Vector mins, Vector maxs, Vector origin)
    {
        var end = origin; end.Z -= 2.0f;
        var query = RnQueryShapeAttr.PlayerMovement(InteractionLayers.Solid);
        var t = _physics.TraceShapePlayerMovement(new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }), origin, end, in query);
        return (t.Fraction, t.PlaneNormal, t.StartInSolid);
    }

    // Standard CS2 standing player hull. Ducking (maxs.z 54) is a refinement for the validated pass.
    private static (Vector Mins, Vector Maxs) Hull()
        => (new Vector(-16f, -16f, 0f), new Vector(16f, 16f, 72f));

    private static float Dot(Vector a, Vector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    [UnmanagedCallersOnly] // CKZ: perf/bhop timing window
    private static void Hk_CheckJumpLegacy(nint jump, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tCheckJumpLegacy)(jump, mv);

    [UnmanagedCallersOnly]
    private static void Hk_CheckJumpModern(nint jump, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tCheckJumpModern)(jump, mv);

    [UnmanagedCallersOnly]
    private static void Hk_Duck(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tDuck)(ms, mv);

    [UnmanagedCallersOnly]
    private static byte Hk_CanUnduck(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, byte>)_tCanUnduck)(ms, mv);

    [UnmanagedCallersOnly]
    private static void Hk_CheckVelocity(nint ms, nint mv, nint desc)
        => ((delegate* unmanaged<nint, nint, nint, void>)_tCheckVelocity)(ms, mv, desc);

    [UnmanagedCallersOnly]
    private static byte Hk_CheckWater(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, byte>)_tCheckWater)(ms, mv);

    [UnmanagedCallersOnly]
    private static void Hk_WaterMove(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tWaterMove)(ms, mv);

    [UnmanagedCallersOnly] // CKZ: ladder physics
    private static byte Hk_LadderMove(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, byte>)_tLadderMove)(ms, mv);

    [UnmanagedCallersOnly]
    private static void Hk_CheckFalling(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tCheckFalling)(ms, mv);

    [UnmanagedCallersOnly]
    private static void Hk_FullWalkMove(nint ms, nint mv, byte waterMoveOnly)
        => ((delegate* unmanaged<nint, nint, byte, void>)_tFullWalkMove)(ms, mv, waterMoveOnly);

    [UnmanagedCallersOnly]
    private static byte Hk_MoveInit(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, byte>)_tMoveInit)(ms, mv);
}
