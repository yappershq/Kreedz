/*
 * yappershq/Kreedz (KZ) — Vanilla (VNL) mode plugin
 *
 * A standalone ModSharp module that registers the Vanilla mode against Kreedz.Core's IKzModeRegistry.
 * VNL is faithful stock CS2 movement, so it only ships a convar layer — no custom movement hooks. This
 * mirrors cs2kz shipping modes as separate plugins; drop this DLL next to Kreedz.Core to add VNL.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Mode.Vnl;

public sealed class KreedzModeVnl : IModSharpModule
{
    private readonly ISharedSystem            _shared;
    private readonly ILogger<KreedzModeVnl>   _logger;

    public string DisplayName   => "[Kreedz] Mode - Vanilla";
    public string DisplayAuthor => "yappershq";

    public KreedzModeVnl(ISharedSystem shared,
                         string?        dllPath,
                         string?        sharpPath,
                         Version?       version,
                         IConfiguration? coreConfiguration,
                         bool           hotReload)
    {
        _shared = shared;
        _logger = shared.GetLoggerFactory().CreateLogger<KreedzModeVnl>();
    }

    public bool Init() => true;

    public void OnAllModulesLoaded()
    {
        var registry = _shared.GetSharpModuleManager()
                              .GetOptionalSharpModuleInterface<IKzModeRegistry>(IKzModeRegistry.Identity)?.Instance;

        if (registry is null)
        {
            _logger.LogError("[Kreedz.Mode.VNL] Kreedz.Core mode registry not found — is the core loaded?");
            return;
        }

        registry.RegisterMode("vnl", "Vanilla", "VNL", Convars);
        _logger.LogInformation("[Kreedz.Mode.VNL] registered.");
    }

    public void Shutdown() { }

    // Faithful CS2 stock movement convar defaults.
    private static readonly IReadOnlyDictionary<string, string> Convars = new Dictionary<string, string>
    {
        ["sv_accelerate"]          = "5.5",
        ["sv_airaccelerate"]       = "12",
        ["sv_air_max_wishspeed"]   = "30",
        ["sv_friction"]            = "5.2",
        ["sv_gravity"]             = "800",
        ["sv_jump_impulse"]        = "301.993377",
        ["sv_maxspeed"]            = "320",
        ["sv_autobunnyhopping"]    = "false",
        ["sv_enablebunnyhopping"]  = "false",
        ["sv_staminamax"]          = "80",
        ["sv_staminajumpcost"]     = "0.08",
        ["sv_staminalandcost"]     = "0.05",
        ["sv_staminarecoveryrate"] = "60",
    };
}
