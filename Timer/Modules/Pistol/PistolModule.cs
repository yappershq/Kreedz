/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ `!pistol <name>` — force-equip a pistol (cs2kz src/kz/pistol). Stores the preference per-player
 * and re-gives it on spawn (KZ/surf servers strip weapons on spawn). Name→classname with common
 * aliases. Persisted in memory for now (→ OptionModule preference store later).
 */

using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Modules;

internal interface IPistolModule;

internal sealed class PistolModule : IModule, IPistolModule
{
    private static readonly Dictionary<string, string> Pistols = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["usp"] = "weapon_usp_silencer", ["usps"] = "weapon_usp_silencer", ["hkp2000"] = "weapon_hkp2000", ["p2000"] = "weapon_hkp2000",
        ["glock"] = "weapon_glock", ["deagle"] = "weapon_deagle", ["p250"] = "weapon_p250",
        ["fiveseven"] = "weapon_fiveseven", ["57"] = "weapon_fiveseven", ["tec9"] = "weapon_tec9",
        ["cz"] = "weapon_cz75a", ["cz75"] = "weapon_cz75a", ["dualies"] = "weapon_elite", ["elite"] = "weapon_elite",
        ["revolver"] = "weapon_revolver", ["r8"] = "weapon_revolver",
    };

    private readonly InterfaceBridge       _bridge;
    private readonly ICommandManager       _commandManager;
    private readonly ILogger<PistolModule> _logger;

    private readonly string?[] _preferred = new string?[PlayerSlot.MaxPlayerCount];

    public PistolModule(InterfaceBridge bridge, ICommandManager commandManager, ILogger<PistolModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _logger         = logger;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("pistol", OnCommandPistol);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        return true;
    }

    public void Shutdown() => _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);

    private ECommandAction OnCommandPistol(PlayerSlot slot, StringCommand command)
    {
        if (command.ArgCount < 1)
        {
            Tell(slot, $"Usage: !pistol <{string.Join("/", new[] { "usp", "glock", "deagle", "p250", "tec9", "cz", "revolver" })}>");
            return ECommandAction.Handled;
        }

        if (!Pistols.TryGetValue(command.GetArg(1), out var classname))
        {
            Tell(slot, $"Unknown pistol '{command.GetArg(1)}'.");
            return ECommandAction.Handled;
        }

        _preferred[slot] = classname;
        Give(slot, classname);
        Tell(slot, $"Pistol set to {classname["weapon_".Length..]}.");
        return ECommandAction.Handled;
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var slot = @params.Client.Slot;
        if (!@params.Client.IsFakeClient && _preferred[slot] is { } classname)
            // Give after the frame so it survives any spawn-time weapon stripping.
            _bridge.ModSharp.InvokeFrameAction(() => Give(slot, classname));
    }

    private void Give(PlayerSlot slot, string classname)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client
            && client.GetPlayerController() is { IsValidEntity: true } controller
            && controller.GetPlayerPawn() is { IsValidEntity: true, IsAlive: true } pawn)
            pawn.GiveNamedItem(classname);
    }

    private void Tell(PlayerSlot slot, string message)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            client.Print(HudPrintChannel.Chat, message);
    }
}
