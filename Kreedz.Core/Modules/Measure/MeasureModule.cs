/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ `!measure` — 2-point distance tool (cs2kz src/kz/measure). First call marks point A (your feet),
 * second marks B and reports the horizontal + 3D distance, then resets. Text-only for now; the visual
 * beam line between the points is polish that lands with the beam/particle backbone.
 */

using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal interface IMeasureModule;

internal sealed class MeasureModule : IModule, IMeasureModule
{
    private readonly InterfaceBridge        _bridge;
    private readonly ICommandManager        _commandManager;
    private readonly ILogger<MeasureModule> _logger;

    private readonly Vector?[] _pointA = new Vector?[PlayerSlot.MaxPlayerCount];

    public MeasureModule(InterfaceBridge bridge, ICommandManager commandManager, ILogger<MeasureModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _logger         = logger;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("measure", OnCommandMeasure);
        return true;
    }

    private ECommandAction OnCommandMeasure(PlayerSlot slot, StringCommand command)
    {
        if (Origin(slot) is not { } here)
            return ECommandAction.Handled;

        if (_pointA[slot] is not { } a)
        {
            _pointA[slot] = here;
            Tell(slot, "Measure: point A set. Move and !measure again for point B.");
            return ECommandAction.Handled;
        }

        var dx = here.X - a.X;
        var dy = here.Y - a.Y;
        var dz = here.Z - a.Z;
        var horizontal = MathF.Sqrt(dx * dx + dy * dy);
        var distance   = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        _pointA[slot] = null; // reset for the next measurement
        Tell(slot, $"Measure: horizontal {horizontal:0.0}u, 3D {distance:0.0}u, height {dz:+0.0;-0.0}u.");
        return ECommandAction.Handled;
    }

    private Vector? Origin(PlayerSlot slot)
        => _bridge.ClientManager.GetGameClient(slot) is { } client
           && client.GetPlayerController() is { IsValidEntity: true } controller
           && controller.GetPlayerPawn() is { IsValidEntity: true, IsAlive: true } pawn
            ? pawn.GetAbsOrigin()
            : null;

    private void Tell(PlayerSlot slot, string message)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            client.Print(HudPrintChannel.Chat, message);
    }
}
