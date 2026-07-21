/*
 * yappershq/Kreedz (KZ) — W/S-Only style plugin
 *
 * Standalone ModSharp module registering the W/S-Only (WS) style against Kreedz.Core's IKzStyleRegistry.
 * An input style: while active it zeroes side-move so the player can only use W/S (forward/back), not A/D
 * strafe. Enforced in the ProcessMove hook, gated on the registry's per-player active state.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Style.WsOnly;

public sealed unsafe class KreedzStyleWsOnly : IModSharpModule
{
    private const string Id = "ws";

    private readonly ISharedSystem            _shared;
    private readonly IHookManager             _hookManager;
    private readonly ILogger<KreedzStyleWsOnly> _logger;

    private IKzStyleRegistry? _registry;

    public string DisplayName   => "[Kreedz] Style - W/S Only";
    public string DisplayAuthor => "yappershq";

    public KreedzStyleWsOnly(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared      = shared;
        _hookManager = shared.GetHookManager();
        _logger      = shared.GetLoggerFactory().CreateLogger<KreedzStyleWsOnly>();
    }

    public bool Init()
    {
        _hookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        return true;
    }

    public void OnAllModulesLoaded()
    {
        _registry = _shared.GetSharpModuleManager()
                          .GetOptionalSharpModuleInterface<IKzStyleRegistry>(IKzStyleRegistry.Identity)?.Instance;

        if (_registry is null)
        {
            _logger.LogError("[Kreedz.Style.WSOnly] Kreedz.Core style registry not found — is the core loaded?");
            return;
        }

        _registry.RegisterStyle(Id, "W/S Only", "WS", Empty);
        _logger.LogInformation("[Kreedz.Style.WSOnly] registered.");
    }

    public void Shutdown() => _hookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (client.IsFakeClient || _registry?.HasStyle(client.Slot, Id) != true) return;

        arg.Info->SideMove = 0f; // W/S only: block A/D strafe input
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
}
