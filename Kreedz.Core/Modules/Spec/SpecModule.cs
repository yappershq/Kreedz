/*
 * !spec — cs2kz spec service (src/kz/spec). Spectate a player by name: stop any live run, slay the
 * live pawn BEFORE the team transfer (a live pawn moved to spectator leaves a ghost), join spectators,
 * then lock the observer service onto the target in first-person next frame (the observer pawn only
 * exists after the team switch settles). Bare `!spec` just joins spectators (free roam).
 *
 * Not ported: cs2kz's spectator-list HUD line + GetNextSpectator chain (HUD feature, tracked with HUD).
 */

using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal sealed class SpecModule : IModule
{
    private readonly InterfaceBridge     _bridge;
    private readonly ICommandManager     _commandManager;
    private readonly ITimerModule        _timerModule;
    private readonly ILogger<SpecModule> _logger;

    public SpecModule(InterfaceBridge bridge, ICommandManager commandManager, ITimerModule timerModule, ILogger<SpecModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _timerModule    = timerModule;
        _logger         = logger;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("spec", OnCommandSpec);
        _commandManager.AddClientChatCommand("spectate", OnCommandSpec);
        return true;
    }

    private ECommandAction OnCommandSpec(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { IsFakeClient: false } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
            return ECommandAction.Handled;

        Sharp.Shared.GameEntities.IPlayerPawn? target = null;
        if (command.ArgCount >= 1)
        {
            var query = command.GetArg(1);
            target = Resolve(query, slot);
            if (target is null)
            {
                Msg(slot, "Kreedz_Goto_NoMatch", query);
                return ECommandAction.Handled;
            }
        }

        // Spectating ends the run (a paused-timer exemption comes with real pause support).
        _timerModule.StopTimer(slot);

        if (controller.Team != CStrikeTeam.Spectator)
        {
            if (controller.GetPlayerPawn() is { IsValidEntity: true, IsAlive: true } pawn)
                pawn.Slay(); // ghost-pawn guard: never move a live pawn to spectator

            controller.SwitchTeam(CStrikeTeam.Spectator);
        }

        if (target is { } t)
        {
            var targetHandle = t.Handle;
            // The observer pawn materializes after the team switch — attach next frame.
            _bridge.ModSharp.InvokeFrameAction(() => AttachObserver(slot, targetHandle));
        }

        return ECommandAction.Handled;
    }

    private void AttachObserver(PlayerSlot slot, Sharp.Shared.Types.CEntityHandle<Sharp.Shared.GameEntities.IBaseEntity> targetHandle)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller
            || controller.GetObserverPawn() is not { IsValidEntity: true } observerPawn
            || observerPawn.GetObserverService() is not { } obs)
            return;

        if (_bridge.EntityManager.FindEntityByHandle(targetHandle) is not { IsValidEntity: true })
            return; // target vanished between command and frame

        obs.ObserverMode     = ObserverMode.InEye;   // cs2kz OBS_MODE_IN_EYE
        obs.ObserverLastMode = ObserverMode.None;
        obs.ObserverTarget   = targetHandle;
    }

    /// <summary>Exact-then-substring name match (case-insensitive), excluding self (Goto's matcher).</summary>
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
                return pawn;

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
