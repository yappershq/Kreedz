/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ `!goto <player>` — teleport to another player (1:1 cs2kz src/kz/goto). Name resolution is
 * exact-then-substring, case-insensitive, spectators excluded. Zeroes your velocity on arrival.
 * (cs2kz also blocks this while your own timer is running + pulls spectators to CT — those hook into
 * the timer/spec systems and land as those modules are wired.)
 */

using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal interface IGotoModule;

internal sealed class GotoModule : IModule, IGotoModule
{
    private readonly InterfaceBridge     _bridge;
    private readonly ICommandManager     _commandManager;
    private readonly ILogger<GotoModule> _logger;

    public GotoModule(InterfaceBridge bridge, ICommandManager commandManager, ILogger<GotoModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _logger         = logger;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("goto", OnCommandGoto);
        return true;
    }

    private ECommandAction OnCommandGoto(PlayerSlot slot, StringCommand command)
    {
        if (command.ArgCount < 1)
        {
            Msg(slot, "Kreedz_Goto_Usage");
            return ECommandAction.Handled;
        }

        if (Self(slot) is not { } self)
            return ECommandAction.Handled;

        var query = command.GetArg(1);
        if (Resolve(query, slot) is not { } targetPawn)
        {
            Msg(slot, "Kreedz_Goto_NoMatch", query);
            return ECommandAction.Handled;
        }

        self.Teleport(targetPawn.GetAbsOrigin(), self.GetEyeAngles(), new Vector());
        return ECommandAction.Handled;
    }

    private Sharp.Shared.GameEntities.IPlayerPawn? Self(PlayerSlot slot)
        => _bridge.ClientManager.GetGameClient(slot) is { } client
           && client.GetPlayerController() is { IsValidEntity: true } controller
           && controller.GetPlayerPawn() is { IsValidEntity: true, IsAlive: true } pawn
            ? pawn
            : null;

    /// <summary>Exact-then-substring name match (case-insensitive), excluding self.</summary>
    private Sharp.Shared.GameEntities.IPlayerPawn? Resolve(string query, PlayerSlot selfSlot)
    {
        Sharp.Shared.GameEntities.IPlayerPawn? substringMatch = null;

        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (client.IsFakeClient || client.Slot == selfSlot) continue;
            if (client.GetPlayerController() is not { IsValidEntity: true } controller
                || controller.GetPlayerPawn() is not { IsValidEntity: true, IsAlive: true } pawn)
                continue;

            if (string.Equals(client.Name, query, StringComparison.OrdinalIgnoreCase))
                return pawn; // exact wins immediately

            if (substringMatch is null && client.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                substringMatch = pawn;
        }

        return substringMatch;
    }

    private void Msg(PlayerSlot slot, string key, params object?[] args)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            Loc.Chat(_bridge.LocalizerManager, client, key, args);
    }
}
