/*
 * yappershq/Kreedz (KZ) — Core-owned native movement detours
 *
 * cs2kz installs the granular CS2 movement detours ONCE in the core, then calls the active player's
 * KZModeService virtual On* callbacks. This module is that: Core hooks the movement pipeline and routes
 * each player's callbacks to the IKzMovementMode registered for their mode (via IKzModeRegistry). Two mode
 * plugins therefore share one set of trampolines instead of each detouring the same function (which
 * collides). Only the 3 functions with real CKZ physics are hooked — AirMove (250-cap), CategorizePosition
 * (rampbug), TryPlayerMove (slopefix); ProcessMovement/WalkMove come from the framework as managed forwards.
 *
 * The sigs live in their own gamedata file, registered here in a try/catch: a sig that breaks after a CS2
 * update degrades movement to stock, but must never take Core down (unlike kreedz.games, Core's critical
 * all-or-nothing gamedata).
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal sealed unsafe class MovementModule : IModule
{
    private const string Ns = "CCSPlayer_MovementServices::";

    private readonly InterfaceBridge          _bridge;
    private readonly IModeModule              _modes;
    private readonly ILogger<MovementModule>  _logger;
    private readonly List<IRuntimeNativeHook> _hooks = [];

    private static MovementModule? _self;
    private static nint _tAirMove, _tCategorize, _tTryPlayerMove;

    // {native movement-service* -> slot}, filled from PlayerProcessMovePre (which has pawn+slot), so the raw
    // detours can resolve the player; the active movement mode is cached per slot for the same tick.
    private readonly Dictionary<nint, PlayerSlot> _slotByMs   = new();
    private readonly IKzMovementMode?[]           _modeBySlot = new IKzMovementMode?[PlayerSlot.MaxPlayerCount];

    private IConVar? _enabled;

    public MovementModule(InterfaceBridge bridge, IModeModule modes, ILogger<MovementModule> logger)
    {
        _bridge = bridge;
        _modes  = modes;
        _logger = logger;
    }

    public bool Init()
    {
        _enabled = _bridge.ConVarManager.CreateConVar("kz_native_movement", true,
            "Install the Core native movement detours (CKZ rampbug/slopefix/air-cap). Set 0 if a sig breaks after a CS2 update.");

        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);

        if (_enabled?.GetBool() == true)
            TryInstall();

        return true;
    }

    private void TryInstall()
    {
        try
        {
            _bridge.ModSharp.GetGameData().Register("kreedz-movement.games");
            _tAirMove       = Hook("AirMove",            (nint)(delegate* unmanaged<nint, nint, void>)&Hk_AirMove);
            _tCategorize    = Hook("CategorizePosition", (nint)(delegate* unmanaged<nint, nint, byte, void>)&Hk_CategorizePosition);
            _tTryPlayerMove = Hook("TryPlayerMove",      (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&Hk_TryPlayerMove);
            _self = this;
            _logger.LogInformation("[KZ.Movement] native detours installed (AirMove/CategorizePosition/TryPlayerMove).");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[KZ.Movement] native detours unavailable (gamedata/sig failure) — modes run stock movement. Set kz_native_movement 0 to silence.");
        }
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        foreach (var hook in _hooks)
            hook.Uninstall();
        _hooks.Clear();
        _self = null;
    }

    // Map this player's movement-service pointer -> slot and cache their active movement mode for this tick.
    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive)
            return;

        if (arg.Pawn.GetPlayerMovementService() is not { } ms)
            return;

        var slot = client.Slot;
        _slotByMs[ms.GetAbsPtr()] = slot;
        _modeBySlot[slot]         = _modes.GetMovementMode(slot);
    }

    private static (PlayerSlot Slot, IKzMovementMode? Mode) Resolve(nint ms)
        => _self is not null && _self._slotByMs.TryGetValue(ms, out var s) ? (s, _self._modeBySlot[s]) : (default, null);

    [UnmanagedCallersOnly]
    private static void Hk_AirMove(nint ms, nint mv)
    {
        var (slot, mode) = Resolve(ms);
        mode?.OnAirMove(slot, ms, mv);                                // cs2kz OnAirMove
        ((delegate* unmanaged<nint, nint, void>)_tAirMove)(ms, mv);   // engine air-move
        mode?.OnAirMovePost(slot, ms, mv);                           // cs2kz OnAirMovePost
    }

    [UnmanagedCallersOnly]
    private static void Hk_CategorizePosition(nint ms, nint mv, byte stayOnGround)
    {
        // cs2kz OnCategorizePosition runs the rampbug fix FIRST, then calls the engine — so the engine's
        // ground/standable determination sees the corrected origin (fidelity fix vs the old post-order).
        var (slot, mode) = Resolve(ms);
        mode?.OnCategorizePosition(slot, ms, mv, stayOnGround != 0);
        ((delegate* unmanaged<nint, nint, byte, void>)_tCategorize)(ms, mv, stayOnGround);
    }

    [UnmanagedCallersOnly]
    private static void Hk_TryPlayerMove(nint ms, nint mv, nint firstDest, nint firstTrace, nint blocked)
    {
        var (slot, mode) = Resolve(ms);
        mode?.OnTryPlayerMovePre(slot, ms, mv);
        ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)_tTryPlayerMove)(ms, mv, firstDest, firstTrace, blocked);
        mode?.OnTryPlayerMovePost(slot, ms, mv);
    }

    private nint Hook(string name, nint hookFn)
    {
        var hook = _bridge.HookManager.CreateDetourHook();
        hook.Prepare(Ns + name, hookFn);

        if (!hook.Install())
        {
            _logger.LogError("[KZ.Movement] failed to install detour {Name} (bad sig for this build?)", name);
            return 0;
        }

        _hooks.Add(hook);
        return hook.Trampoline;
    }
}
