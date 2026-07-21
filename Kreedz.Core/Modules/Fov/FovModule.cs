/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ `!fov <value>` — custom field-of-view (1:1 cs2kz src/kz/fov), clamped to [MinFov, MaxFov] and
 * re-applied on spawn (the game resets DesiredFOV each spawn). Persisted per-player via the preference
 * store, so a player's FOV survives a reconnect.
 */

using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal interface IFovModule;

internal sealed class FovModule : IModule, IFovModule
{
    private const uint DefaultFov = 90;
    private const uint MinFov     = 40;
    private const uint MaxFov     = 150;

    private const string PrefKey = "fov";

    private readonly InterfaceBridge    _bridge;
    private readonly ICommandManager    _commandManager;
    private readonly IPreferencesModule _prefs;
    private readonly ILogger<FovModule> _logger;

    private readonly uint[] _fov = new uint[PlayerSlot.MaxPlayerCount];

    public FovModule(InterfaceBridge bridge, ICommandManager commandManager, IPreferencesModule prefs, ILogger<FovModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _prefs          = prefs;
        _logger         = logger;

        for (var i = 0; i < _fov.Length; i++)
            _fov[i] = DefaultFov;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("fov", OnCommandFov);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        _prefs.Loaded += OnPreferencesLoaded;
        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
        _prefs.Loaded -= OnPreferencesLoaded;
    }

    private void OnPreferencesLoaded(PlayerSlot slot)
    {
        if (_prefs.Get(slot, PrefKey) is { } raw && uint.TryParse(raw, out var value))
        {
            _fov[slot] = Math.Clamp(value, MinFov, MaxFov);
            Apply(slot);
        }
    }

    private ECommandAction OnCommandFov(PlayerSlot slot, StringCommand command)
    {
        if (command.ArgCount < 1 || !uint.TryParse(command.GetArg(1), out var requested))
        {
            Msg(slot, "Kreedz_Fov_Usage", MinFov, MaxFov, _fov[slot]);
            return ECommandAction.Handled;
        }

        _fov[slot] = Math.Clamp(requested, MinFov, MaxFov);
        _prefs.Set(slot, PrefKey, _fov[slot].ToString());
        Apply(slot);
        Msg(slot, "Kreedz_Fov_Set", _fov[slot]);
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

    private void Msg(PlayerSlot slot, string key, params object?[] args)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            Loc.Chat(_bridge.LocalizerManager, client, key, args);
    }
}
