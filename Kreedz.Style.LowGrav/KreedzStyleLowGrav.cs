/*
 * yappershq/Kreedz (KZ) — Low Gravity style plugin
 *
 * Standalone ModSharp module registering the Low Gravity (LG) style against Kreedz.Core's IKzStyleRegistry.
 * A convar-only style (no custom hooks) — drop this DLL next to Kreedz.Core to offer it via !lg/!style.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Style.LowGrav;

public sealed class KreedzStyleLowGrav : IModSharpModule
{
    private readonly ISharedSystem _shared;
    private readonly ILogger<KreedzStyleLowGrav> _logger;

    public string DisplayName   => "[Kreedz] Style - Low Gravity";
    public string DisplayAuthor => "yappershq";

    public KreedzStyleLowGrav(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared = shared;
        _logger = shared.GetLoggerFactory().CreateLogger<KreedzStyleLowGrav>();
    }

    public bool Init() => true;

    public void OnAllModulesLoaded()
    {
        var registry = _shared.GetSharpModuleManager()
                              .GetOptionalSharpModuleInterface<IKzStyleRegistry>(IKzStyleRegistry.Identity)?.Instance;

        if (registry is null)
        {
            _logger.LogError("[Kreedz.Style.LowGrav] Kreedz.Core style registry not found — is the core loaded?");
            return;
        }

        registry.RegisterStyle("lg", "Low Gravity", "LG", Convars);
        _logger.LogInformation("[Kreedz.Style.LowGrav] registered.");
    }

    public void Shutdown() { }

    private static readonly IReadOnlyDictionary<string, string> Convars = new Dictionary<string, string>
    {
        ["sv_gravity"] = "400",
    };
}
