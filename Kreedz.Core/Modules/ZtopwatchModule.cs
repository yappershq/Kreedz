/*
 * !zt — cs2kz ztopwatch (src/kz/ztopwatch): a personal practice stopwatch between two user-placed
 * box zones. Set each zone from two corner points at your feet, the AABB is drawn with env_beam
 * edges (green start / red end); while the main timer is NOT running, standing in the start zone
 * arms the watch (re-armed every tick inside, so timing effectively begins when you leave) and
 * reaching the end zone prints the elapsed time. startOnJump arms only on takeoff; stopOnLand stops
 * only once grounded. Zone touches honor the mode's CanTouchTimerZone tick gate like real zones.
 *
 * Commands: !zt (toggle) · !zt s1|s2|e1|e2 (corners) · !zt jump · !zt land · !zt reset
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal sealed class ZtopwatchModule : IModule
{
    private sealed class Zone
    {
        public Vector? Point1;
        public Vector? Point2;
        public Vector  Mins;
        public Vector  Maxs;
        public IBaseEntity?[] Edges = new IBaseEntity?[12];

        public bool IsValid => Point1 is not null && Point2 is not null;

        public void Recompute()
        {
            if (Point1 is not { } a || Point2 is not { } b) return;
            Mins = new Vector(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));
            Maxs = new Vector(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z) + 72f);
        }
    }

    private sealed class State
    {
        public readonly Zone Start = new();
        public readonly Zone End   = new();
        public bool  Enabled;
        public bool  StartOnJump;
        public bool  StopOnLand;
        public float StartTime;
        public bool  WasGround;
    }

    private readonly InterfaceBridge         _bridge;
    private readonly ICommandManager         _commandManager;
    private readonly ITimerModule            _timerModule;
    private readonly IModeModule             _modes;
    private readonly ILogger<ZtopwatchModule> _logger;

    private readonly State?[] _states = new State?[PlayerSlot.MaxPlayerCount];

    public ZtopwatchModule(InterfaceBridge bridge, ICommandManager commandManager, ITimerModule timerModule,
        IModeModule modes, ILogger<ZtopwatchModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _timerModule    = timerModule;
        _modes          = modes;
        _logger         = logger;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("zt", OnCommand);
        _commandManager.AddClientChatCommand("ztopwatch", OnCommand);
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        return true;
    }

    public void Shutdown()
        => _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);

    private ECommandAction OnCommand(PlayerSlot slot, StringCommand command)
    {
        var state = _states[slot] ??= new State();
        var arg   = command.ArgCount >= 1 ? command.GetArg(1).ToLowerInvariant() : "";

        switch (arg)
        {
            case "s1": SetPoint(slot, state.Start, first: true);  break;
            case "s2": SetPoint(slot, state.Start, first: false); break;
            case "e1": SetPoint(slot, state.End, first: true);    break;
            case "e2": SetPoint(slot, state.End, first: false);   break;

            case "jump":
                state.StartOnJump = !state.StartOnJump;
                Msg(slot, state.StartOnJump ? "Kreedz_Opt_On" : "Kreedz_Opt_Off", "zt start-on-jump");
                break;

            case "land":
                state.StopOnLand = !state.StopOnLand;
                Msg(slot, state.StopOnLand ? "Kreedz_Opt_On" : "Kreedz_Opt_Off", "zt stop-on-land");
                break;

            case "reset":
                KillEdges(state.Start); KillEdges(state.End);
                _states[slot] = null;
                Msg(slot, "Kreedz_Zt_Reset");
                break;

            default:
                state.Enabled = !state.Enabled;
                if (!state.Enabled) { KillEdges(state.Start); KillEdges(state.End); }
                else { Redraw(state); Msg(slot, "Kreedz_Zt_Usage"); }
                Msg(slot, state.Enabled ? "Kreedz_Opt_On" : "Kreedz_Opt_Off", "ztopwatch");
                break;
        }

        return ECommandAction.Handled;
    }

    private void SetPoint(PlayerSlot slot, Zone zone, bool first)
    {
        if (_bridge.ClientManager.GetGameClient(slot)?.GetPlayerController()?.GetPlayerPawn()
            is not { IsValidEntity: true, IsAlive: true } pawn)
            return;

        if (first) zone.Point1 = pawn.GetAbsOrigin();
        else       zone.Point2 = pawn.GetAbsOrigin();
        zone.Recompute();

        var state = _states[slot]!;
        state.Enabled = true;
        Redraw(state);
        Msg(slot, "Kreedz_Zt_Point", first ? "1" : "2");
    }

    private void OnProcessMovePre(Sharp.Shared.HookParams.IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive)
            return;

        var slot = client.Slot;
        if (_states[slot] is not { Enabled: true } state || !state.Start.IsValid || !state.End.IsValid)
            return;

        var onGround = arg.Pawn.GroundEntityHandle.IsValid();
        var tookOff  = !onGround && state.WasGround;
        state.WasGround = onGround;

        if (_timerModule.GetTimerInfo(slot)?.Status == Kreedz.Shared.Models.Timer.ETimerStatus.Running)
            return;

        if (_modes.GetMovementMode(slot)?.CanTouchTimerZone(slot) == false)
            return;

        var origin = arg.Pawn.GetAbsOrigin();

        // Arm: every tick inside the start zone (or only on takeoff with start-on-jump).
        if ((!state.StartOnJump && InZone(state.Start, origin)) || (state.StartOnJump && tookOff && InZone(state.Start, origin)))
            state.StartTime = _bridge.GlobalVars.CurTime;

        // Stop: inside the end zone (grounded only with stop-on-land).
        if (state.StartTime > 0f && (!state.StopOnLand || onGround) && InZone(state.End, origin))
        {
            var elapsed = _bridge.GlobalVars.CurTime - state.StartTime;
            state.StartTime = 0f;
            Msg(slot, "Kreedz_Zt_Time", Utils.FormatTime(elapsed, true));
        }

        // Map changes kill the beams — lazily redraw when the first edge is gone.
        if (state.Start.IsValid && state.Start.Edges[0] is not { IsValidEntity: true })
            Redraw(state);
    }

    private static bool InZone(Zone zone, Vector origin)
        => zone.IsValid
           && origin.X + 16f >= zone.Mins.X && origin.X - 16f <= zone.Maxs.X
           && origin.Y + 16f >= zone.Mins.Y && origin.Y - 16f <= zone.Maxs.Y
           && origin.Z + 72f >= zone.Mins.Z && origin.Z <= zone.Maxs.Z;

    private void Redraw(State state)
    {
        DrawZone(state.Start, "0 255 0");
        DrawZone(state.End, "255 0 0");
    }

    private void DrawZone(Zone zone, string color)
    {
        KillEdges(zone);
        if (!zone.IsValid)
            return;

        var mn = zone.Mins;
        var mx = zone.Maxs;
        var c = new[]
        {
            new Vector(mn.X, mn.Y, mn.Z), new Vector(mx.X, mn.Y, mn.Z), new Vector(mx.X, mx.Y, mn.Z), new Vector(mn.X, mx.Y, mn.Z),
            new Vector(mn.X, mn.Y, mx.Z), new Vector(mx.X, mn.Y, mx.Z), new Vector(mx.X, mx.Y, mx.Z), new Vector(mn.X, mx.Y, mx.Z),
        };
        int[][] pairs =
        [
            [0, 1], [1, 2], [2, 3], [3, 0], // bottom
            [4, 5], [5, 6], [6, 7], [7, 4], // top
            [0, 4], [1, 5], [2, 6], [3, 7], // verticals
        ];

        var kv = new Dictionary<string, KeyValuesVariantValueItem>
        {
            { "rendercolor", color },
            { "BoltWidth", "2" },
        };

        for (var i = 0; i < 12; i++)
        {
            if (_bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv) is not { IsValidEntity: true } beam)
                continue;

            beam.SetAbsOrigin(c[pairs[i][0]]);
            beam.SetNetVar("m_vecEndPos", c[pairs[i][1]]);
            zone.Edges[i] = beam;
        }
    }

    private static void KillEdges(Zone zone)
    {
        for (var i = 0; i < zone.Edges.Length; i++)
        {
            if (zone.Edges[i] is { IsValidEntity: true } beam)
                beam.Kill();
            zone.Edges[i] = null;
        }
    }

    private void Msg(PlayerSlot slot, string key, params object?[] args)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            Loc.Chat(_bridge.LocalizerManager, client, key, args);
    }
}
