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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Hooks;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal sealed unsafe class MovementModule : IModule, IKzMovementTelemetry
{
    private const string Ns = "CCSPlayer_MovementServices::";

    private readonly InterfaceBridge          _bridge;
    private readonly IModeModule              _modes;
    private readonly ILogger<MovementModule>  _logger;
    private readonly List<IRuntimeNativeHook> _hooks = [];

    private static MovementModule? _self;
    private static nint _tAirMove, _tCategorize, _tTryPlayerMove, _tAirAccelerate;

    // {native movement-service* -> slot}, filled from PlayerProcessMovePre (which has pawn+slot), so the raw
    // detours can resolve the player; the active movement mode is cached per slot for the same tick.
    private readonly Dictionary<nint, PlayerSlot> _slotByMs   = new();
    private readonly IKzMovementMode?[]           _modeBySlot = new IKzMovementMode?[PlayerSlot.MaxPlayerCount];

    // Per-tick AACall inputs the AirAccelerate detour can't cheaply read itself. Buttons come from
    // PlayerProcessMovePre (m_nButtons); the last tick's post view-yaw + post speed come from the FinishMove-
    // equivalent PlayerProcessMovePost (cs2kz's moveDataPost/oldAngles), so prevYaw and externalSpeedDiff match.
    private readonly UserCommandButtons[] _buttons          = new UserCommandButtons[PlayerSlot.MaxPlayerCount];
    private readonly float[]              _oldYaw           = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[]              _lastPostVelLen2D = new float[PlayerSlot.MaxPlayerCount];

    /// <summary>cs2kz AACall telemetry — raised once per AirAccelerate call (see <see cref="IKzMovementTelemetry"/>).</summary>
    public event Action<PlayerSlot, AaCall>? AirAccelerate;

    private IConVar? _enabled;

    // CMoveData raw-offset reads the managed MoveData struct doesn't expose. Offsets derived from cs2kz's
    // hardcoded CMoveData layout, anchored to the managed struct's known m_flMaxSpeed = Win 0x120:
    //   m_vecFrameVelocityDelta = the Vector immediately before m_flMaxSpeed → Win 0x114 (shifts -4 on Linux).
    //   m_flSubtickStartFraction/EndFraction sit before the Windows-only 0xe4 pad → same offset both platforms.
    private static readonly nint PlatformOffset = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? -4 : 0;
    private const float EngineFixedTickInterval = 0.015625f; // 1/64 — cs2kz ENGINE_FIXED_TICK_INTERVAL

    private static ref MoveData Move(nint mv)          => ref Unsafe.AsRef<MoveData>((void*)mv);
    private static Vector      FrameVelocityDelta(nint mv) => *(Vector*)(mv + 0x114 + PlatformOffset);
    private static float       SubtickStart(nint mv)   => *(float*)(mv + 0xdc);
    private static float       SubtickEnd(nint mv)      => *(float*)(mv + 0xe0);

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
        _bridge.HookManager.PlayerProcessMovePost.InstallForward(OnProcessMovePost);

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
            _tAirAccelerate = Hook("AirAccelerate",      (nint)(delegate* unmanaged<nint, nint, nint, float, float, void>)&Hk_AirAccelerate);
            _self = this;
            _logger.LogInformation("[KZ.Movement] native detours installed (AirMove/CategorizePosition/TryPlayerMove/AirAccelerate).");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[KZ.Movement] native detours unavailable (gamedata/sig failure) — modes run stock movement. Set kz_native_movement 0 to silence.");
        }
    }

    // Publish the AACall telemetry stream so external plugins (Kreedz.Jumpstats) can subscribe in their
    // OnAllSharpModulesLoaded — same publish-in-PostInit contract as the mode/style/run registries.
    public void OnPostInit(ServiceProvider provider)
        => _bridge.SharpModuleManager.RegisterSharpModuleInterface<IKzMovementTelemetry>(
            _bridge.Entrypoint, IKzMovementTelemetry.Identity, this);

    public void Shutdown()
    {
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        _bridge.HookManager.PlayerProcessMovePost.RemoveForward(OnProcessMovePost);
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

        // Snapshot this tick's held buttons for the AirAccelerate detour (fires mid-ProcessMovement with only
        // ms/mv). prevYaw/externalSpeedDiff come from last tick's post state, captured in OnProcessMovePost.
        _buttons[slot] = arg.Service.KeyButtons;
    }

    // FinishMove-equivalent: cs2kz copies moveDataPost + dispatches OnProcessMovementPost at the end of
    // ProcessMovement. We capture the post view-yaw (oldAngles) and post 2D speed (for the next tick's AACall
    // externalSpeedDiff), then dispatch to the active mode (where VNL's TriggerFix / per-tick trigger work hangs).
    private void OnProcessMovePost(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive)
            return;

        var slot = client.Slot;
        var vel  = arg.Velocity;
        _oldYaw[slot]           = arg.ViewAngles.Y;
        _lastPostVelLen2D[slot] = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y);

        if (arg.Pawn.GetPlayerMovementService() is { } ms)
            _modeBySlot[slot]?.OnProcessMovementPost(slot, ms.GetAbsPtr(), (nint) arg.Info);
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

    // TryPlayerMove entry capture for the triggerfix bump-path replay (cs2kz VNL OnTryPlayerMove):
    // TriggerModifierModule re-predicts the bump path from these exact engine inputs after the tick.
    internal static readonly Vector[] TpmOrigin   = new Vector[PlayerSlot.MaxPlayerCount];
    internal static readonly Vector[] TpmVelocity = new Vector[PlayerSlot.MaxPlayerCount];
    internal static readonly float[]  TpmTime     = new float[PlayerSlot.MaxPlayerCount];
    internal static readonly int[]    TpmTick     = new int[PlayerSlot.MaxPlayerCount];

    [UnmanagedCallersOnly]
    private static void Hk_TryPlayerMove(nint ms, nint mv, nint firstDest, nint firstTrace, nint blocked)
    {
        var (slot, mode) = Resolve(ms);

        if (_self is { } self)
        {
            ref var move = ref Move(mv);
            TpmOrigin[slot]   = move.AbsOrigin;
            TpmVelocity[slot] = move.Velocity;
            TpmTime[slot]     = self._bridge.GlobalVars.FrameTime;
            TpmTick[slot]     = self._bridge.GlobalVars.TickCount;
        }

        mode?.OnTryPlayerMovePre(slot, ms, mv);
        ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)_tTryPlayerMove)(ms, mv, firstDest, firstTrace, blocked);
        mode?.OnTryPlayerMovePost(slot, ms, mv);
    }

    // cs2kz splits AACall capture across pre (velocityPre/buttons) and post (velocityPost/wishdir/duration).
    // We fold both around the single trampoline call: read velocity before, run the engine air-accel, then read
    // velocity + m_vecFrameVelocityDelta after (CS2 air-accel routes most of its impulse through that delta, so
    // velocityPost without it would equal velocityPre and every call would misclassify as badAngles).
    [UnmanagedCallersOnly]
    private static void Hk_AirAccelerate(nint ms, nint mv, nint pWishdir, float wishspeed, float accel)
    {
        var velPre = Move(mv).Velocity;
        ((delegate* unmanaged<nint, nint, nint, float, float, void>)_tAirAccelerate)(ms, mv, pWishdir, wishspeed, accel);

        var self = _self;
        var handler = self?.AirAccelerate;
        if (self is null || handler is null || !self._slotByMs.TryGetValue(ms, out var slot))
            return;

        ref var md      = ref Move(mv);
        var velPost     = md.Velocity + FrameVelocityDelta(mv);
        var duration    = MathF.Max(SubtickEnd(mv) - SubtickStart(mv), 0f) * EngineFixedTickInterval;
        var preLen2D    = MathF.Sqrt(velPre.X * velPre.X + velPre.Y * velPre.Y);
        handler.Invoke(slot, new AaCall(
            Wishdir:      *(Vector*)pWishdir,
            WishSpeed:    wishspeed,
            VelocityPre:  velPre,
            VelocityPost: velPost,
            Buttons:      self._buttons[slot],
            Duration:     duration,
            PrevYaw:      self._oldYaw[slot],
            CurrentYaw:   md.ViewAngles.Y,
            // cs2kz: velocityPre.Length2D() - moveDataPost(last tick).m_vecVelocity.Length2D() — speed injected
            // between ticks by something other than the player (boosters, teleport pushes) → external gain/loss.
            ExternalSpeedDiff: preLen2D - self._lastPostVelLen2D[slot],
            Accel:            accel,          // AirAccelerate's accel param, for jumpstats CalcIdealGain
            MaxSpeed:         md.MaxSpeed));  // CMoveData m_flMaxSpeed (managed accessor)
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
