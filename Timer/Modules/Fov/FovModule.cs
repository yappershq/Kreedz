/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ `!fov <value>` — custom field-of-view (1:1 cs2kz src/kz/fov), clamped to [MinFov, MaxFov] and
 * re-applied on spawn (the game resets DesiredFOV each spawn). Persisted per-player in memory for now;
 * moves into the OptionModule preference store when that lands.
 */

using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Modules;

internal interface IFovModule;

internal sealed class FovModule : IModule, IFovModule
{
    private const uint DefaultFov = 90;
    private const uint MinFov     = 40;
    private const uint MaxFov     = 150;

    private readonly InterfaceBridge    _bridge;
    private readonly ICommandManager    _commandManager;
    private readonly ILogger<FovModule> _logger;

    private readonly uint[] _fov = new uint[PlayerSlot.MaxPlayerCount];

    public FovModule(InterfaceBridge bridge, ICommandManager commandManager, ILogger<FovModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _logger         = logger;

        for (var i = 0; i < _fov.Length; i++)
            _fov[i] = DefaultFov;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("fov", OnCommandFov);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        return true;
    }

    public void Shutdown() => _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);

    private ECommandAction OnCommandFov(PlayerSlot slot, StringCommand command)
    {
        if (command.ArgCount < 1 || !uint.TryParse(command.GetArg(1), out var requested))
        {
            Tell(slot, $"Usage: !fov <{MinFov}-{MaxFov}>  (current: {_fov[slot]})");
            return ECommandAction.Handled;
        }

        _fov[slot] = Math.Clamp(requested, MinFov, MaxFov);
        Apply(slot);
        Tell(slot, $"FOV set to {_fov[slot]}.");
        return ECommandAction.Handled;
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        if (!@params.Client.IsFakeClient)
            Apply(@params.Client.Slot);
    }

    private void Apply(PlayerSlot slot)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client
            && client.GetPlayerController() is { IsValidEntity: true } controller)
            controller.DesiredFOV = _fov[slot];
    }

    private void Tell(PlayerSlot slot, string message)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            client.Print(HudPrintChannel.Chat, message);
    }
}
