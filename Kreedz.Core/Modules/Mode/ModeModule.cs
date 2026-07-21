/*
 * yappershq/Kreedz (KZ) — CS2KZ port
 *
 * KZ mode framework (1:1 cs2kz src/kz/mode). A "mode" is the base movement ruleset — a set of movement
 * convar values replicated per-player, plus (for CKZ) a custom movement model. This module is the
 * REGISTRY: it owns the per-player current mode, the `!mode` command + per-mode short commands, and
 * applies the registered convars on switch/spawn. It publishes IKzModeRegistry so modes ship as separate
 * plugins (Kreedz.Mode.VNL, Kreedz.Mode.CKZ) that register their convar layer + own their movement hooks,
 * exactly like cs2kz's mode plugins. No mode is hard-coded here.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal interface IModeModule
{
    /// <summary>The player's current mode id (default "vnl").</summary>
    string GetMode(PlayerSlot slot);

    /// <summary>Re-apply the player's current mode convars (base layer; styles stack on top after).</summary>
    void Reapply(PlayerSlot slot);

    /// <summary>The active player's movement-callback impl (CKZ physics), or null (stock movement).</summary>
    IKzMovementMode? GetMovementMode(PlayerSlot slot);
}

internal sealed class ModeModule : IModule, IModeModule, IKzModeRegistry
{
    private const string DefaultMode = "ckz"; // cs2kz server-config.txt: defaultMode "Classic" (CKZ), not Vanilla

    private readonly InterfaceBridge     _bridge;
    private readonly ICommandManager     _commandManager;
    private readonly IPreferencesModule  _prefs;
    private readonly ILogger<ModeModule> _logger;

    private readonly record struct ModeInfo(string Name, string ShortName, IReadOnlyDictionary<string, string> Convars);

    private readonly Dictionary<string, ModeInfo> _modes = new(StringComparer.OrdinalIgnoreCase);
    // A mode's optional native-movement callbacks (CKZ registers physics; VNL registers none). Core's
    // MovementModule installs the detours once and routes each player's callbacks to the impl for their mode.
    private readonly Dictionary<string, IKzMovementMode> _movementModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _current = new string[PlayerSlot.MaxPlayerCount];

    private const string PrefKey = "mode";

    public event Action<PlayerSlot, string>? PlayerModeChanged;

    public ModeModule(InterfaceBridge bridge, ICommandManager commandManager, IPreferencesModule prefs, ILogger<ModeModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _prefs          = prefs;
        _logger         = logger;

        for (var i = 0; i < _current.Length; i++)
            _current[i] = DefaultMode;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("mode", OnCommandMode);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        _prefs.Loaded += OnPreferencesLoaded;
        return true;
    }

    // Publish the registry so external mode plugins can register in their OnAllSharpModulesLoaded.
    public void OnPostInit(ServiceProvider provider)
        => _bridge.SharpModuleManager.RegisterSharpModuleInterface<IKzModeRegistry>(
               _bridge.Entrypoint, IKzModeRegistry.Identity, this);

    public void Shutdown()
    {
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
        _prefs.Loaded -= OnPreferencesLoaded;
    }

    // ── IKzModeRegistry ───────────────────────────────────────────────────────
    public void RegisterMode(string id, string name, string shortName, IReadOnlyDictionary<string, string> convars)
    {
        var isNew = !_modes.ContainsKey(id);
        _modes[id] = new ModeInfo(name, shortName, convars);

        foreach (var cvar in convars.Keys)
            if (_bridge.ConVarManager.FindConVar(cvar) is { } cv)
                cv.Flags |= ConVarFlags.Replicated;

        if (isNew)
            _commandManager.AddClientChatCommand(id, (slot, _) => { SwitchMode(slot, id); return ECommandAction.Handled; });

        _logger.LogInformation("[KZ.Mode] registered mode {Id} ({Short})", id, shortName);
    }

    public string GetPlayerMode(PlayerSlot slot) => _current[slot];

    public void RegisterMovementMode(string id, IKzMovementMode mode)
    {
        _movementModes[id] = mode;
        _logger.LogInformation("[KZ.Mode] registered movement callbacks for mode {Id}", id);
    }

    public IKzMovementMode? GetMovementMode(PlayerSlot slot)
        => _movementModes.TryGetValue(_current[slot], out var m) ? m : null;

    // ── IModeModule (internal) ────────────────────────────────────────────────
    public string GetMode(PlayerSlot slot) => _current[slot];

    public void Reapply(PlayerSlot slot)
    {
        if (_modes.TryGetValue(_current[slot], out var mode)
            && _bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            Apply(client, mode);
    }

    private ECommandAction OnCommandMode(PlayerSlot slot, Sharp.Shared.Types.StringCommand command)
    {
        if (command.ArgCount >= 1) // ArgCount excludes the command itself; GetArg is 1-indexed
        {
            SwitchMode(slot, command.GetArg(1));
            return ECommandAction.Handled;
        }

        var names = _modes.Count == 0 ? "(none installed)" : string.Join(", ", _modes.Values.Select(m => m.ShortName));
        Msg(slot, "Kreedz_Mode_Current", ModeName(_current[slot]), names);
        return ECommandAction.Handled;
    }

    private void SwitchMode(PlayerSlot slot, string id)
    {
        if (!_modes.TryGetValue(id, out var mode))
        {
            Msg(slot, "Kreedz_Mode_Unknown", id);
            return;
        }

        _current[slot] = id;
        _prefs.Set(slot, PrefKey, id);
        PlayerModeChanged?.Invoke(slot, id);

        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
        {
            Apply(client, mode);
            Msg(slot, "Kreedz_Mode_Set", mode.Name);
        }
    }

    private void OnPreferencesLoaded(PlayerSlot slot)
    {
        if (_prefs.Get(slot, PrefKey) is { } id && _modes.ContainsKey(id))
        {
            _current[slot] = id;
            PlayerModeChanged?.Invoke(slot, id);
            Reapply(slot);
        }
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var slot = @params.Client.Slot;
        if (!@params.Client.IsFakeClient && _modes.TryGetValue(_current[slot], out var mode))
            Apply(@params.Client, mode);
    }

    private void Apply(IGameClient client, ModeInfo mode)
    {
        foreach (var (name, value) in mode.Convars)
            _bridge.ConVarManager.FindConVar(name)?.ReplicateToClient(client, value);
    }

    private string ModeName(string id) => _modes.TryGetValue(id, out var m) ? m.Name : id;

    private void Msg(PlayerSlot slot, string key, params object?[] args)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            Loc.Chat(_bridge.LocalizerManager, client, key, args);
    }
}
