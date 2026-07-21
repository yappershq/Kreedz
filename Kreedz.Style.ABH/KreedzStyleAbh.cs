/*
 * yappershq/Kreedz (KZ) — Auto Bhop style plugin
 *
 * Standalone ModSharp module registering the Auto Bhop (ABH) style against Kreedz.Core's IKzStyleRegistry.
 * A convar-only style (no custom hooks) — drop this DLL next to Kreedz.Core to offer it via !abh/!style.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Style.Abh;

public sealed class KreedzStyleAbh : IModSharpModule
{
    private readonly ISharedSystem _shared;
    private readonly ILogger<KreedzStyleAbh> _logger;

    public string DisplayName   => "[Kreedz] Style - Auto Bhop";
    public string DisplayAuthor => "yappershq";

    public KreedzStyleAbh(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared = shared;
        _logger = shared.GetLoggerFactory().CreateLogger<KreedzStyleAbh>();
    }

    public bool Init() => true;

    public void OnAllModulesLoaded()
    {
        var registry = _shared.GetSharpModuleManager()
                              .GetOptionalSharpModuleInterface<IKzStyleRegistry>(IKzStyleRegistry.Identity)?.Instance;

        if (registry is null)
        {
            _logger.LogError("[Kreedz.Style.ABH] Kreedz.Core style registry not found — is the core loaded?");
            return;
        }

        registry.RegisterStyle("abh", "Auto Bhop", "ABH", Convars);
        _logger.LogInformation("[Kreedz.Style.ABH] registered.");
    }

    public void Shutdown() { }

    private static readonly IReadOnlyDictionary<string, string> Convars = new Dictionary<string, string>
    {
        ["sv_autobunnyhopping"] = "true",
        ["sv_enablebunnyhopping"] = "true",
    };
}
