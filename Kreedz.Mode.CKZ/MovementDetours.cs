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
 * `Unsafe.AsRef` — no hand-ported offsets — so the CKZ physics can be filled in with typed field access
 * (`md.Velocity`, `md.ForwardMove`, `md.MaxSpeed`, …). Every detour currently calls the original after
 * (faithful pass-through); the CKZ physics is filled per function and validated tick-for-tick on a live
 * server — native detours can't be exercised headless (a wrong sig for a given CS2 build crashes on load,
 * which is why the convar exists as a kill switch).
 *
 * Native signatures are cs2kz's movement.h detour typedefs verbatim; x64 uses the platform default
 * calling convention (this in rcx/rdi). FinishMove is a vtable func (offset 38/39) — a virtual hook, TODO.
 */

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;

namespace Kreedz.Mode.Ckz;

internal sealed unsafe class MovementDetours
{
    private const string Ns = "CCSPlayer_MovementServices::";

    private readonly IHookManager             _hookManager;
    private readonly ILogger                  _logger;
    private readonly List<IRuntimeNativeHook> _hooks = [];

    // Trampolines to the originals (static so the [UnmanagedCallersOnly] hooks can reach them).
    private static nint _tProcessMovement, _tAirAccelerate, _tFriction, _tWalkMove, _tAirMove, _tTryPlayerMove,
                        _tCategorizePosition, _tCheckJumpLegacy, _tCheckJumpModern, _tDuck, _tCanUnduck,
                        _tCheckVelocity, _tCheckWater, _tWaterMove, _tLadderMove, _tCheckFalling,
                        _tFullWalkMove, _tMoveInit;

    public MovementDetours(IHookManager hookManager, ILogger logger)
    {
        _hookManager = hookManager;
        _logger      = logger;
    }

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

        // TODO: FinishMove is a vtable func (offset 38/39) — install via CreateVirtualHook, not a sig detour.

        Installed = true;
        _logger.LogWarning("[CKZ] native movement detours INSTALLED (staged pass-through) — validate on a test server.");
    }

    public void Uninstall()
    {
        foreach (var hook in _hooks)
            hook.Uninstall();
        _hooks.Clear();
        Installed = false;
    }

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

    [UnmanagedCallersOnly]
    private static void Hk_AirMove(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tAirMove)(ms, mv);

    [UnmanagedCallersOnly] // CKZ: rampbug/slopefix
    private static void Hk_TryPlayerMove(nint ms, nint mv, nint firstDest, nint firstTrace, nint blocked)
        => ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)_tTryPlayerMove)(ms, mv, firstDest, firstTrace, blocked);

    [UnmanagedCallersOnly]
    private static void Hk_CategorizePosition(nint ms, nint mv, byte stayOnGround)
        => ((delegate* unmanaged<nint, nint, byte, void>)_tCategorizePosition)(ms, mv, stayOnGround);

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
