/*
 * yappershq/Kreedz (KZ) — A/D-Only style plugin
 *
 * Standalone ModSharp module registering the A/D-Only (AD) style against Kreedz.Core's IKzStyleRegistry.
 * An input style: while active it zeroes forward-move so the player can only use A/D (strafe), not W/S.
 * Enforced in the ProcessMove hook, gated on the registry's per-player active state.
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

namespace Kreedz.Style.AdOnly;

public sealed unsafe class KreedzStyleAdOnly : IModSharpModule
{
    private const string Id = "ad";

    private readonly ISharedSystem            _shared;
    private readonly IHookManager             _hookManager;
    private readonly ILogger<KreedzStyleAdOnly> _logger;

    private IKzStyleRegistry? _registry;

    public string DisplayName   => "[Kreedz] Style - A/D Only";
    public string DisplayAuthor => "yappershq";

    public KreedzStyleAdOnly(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared      = shared;
        _hookManager = shared.GetHookManager();
        _logger      = shared.GetLoggerFactory().CreateLogger<KreedzStyleAdOnly>();
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
            _logger.LogError("[Kreedz.Style.ADOnly] Kreedz.Core style registry not found — is the core loaded?");
            return;
        }

        _registry.RegisterStyle(Id, "A/D Only", "AD", Empty);
        _logger.LogInformation("[Kreedz.Style.ADOnly] registered.");
    }

    public void Shutdown() => _hookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (client.IsFakeClient || _registry?.HasStyle(client.Slot, Id) != true) return;

        arg.Info->ForwardMove = 0f; // A/D only: block W/S input
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
}
