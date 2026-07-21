/*
 * yappershq/Kreedz (KZ) — Ice style plugin
 *
 * Standalone ModSharp module registering the Ice (ICE) style against Kreedz.Core's IKzStyleRegistry.
 * A convar-only style (no custom hooks) — drop this DLL next to Kreedz.Core to offer it via !ice/!style.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Style.Ice;

public sealed class KreedzStyleIce : IModSharpModule
{
    private readonly ISharedSystem _shared;
    private readonly ILogger<KreedzStyleIce> _logger;

    public string DisplayName   => "[Kreedz] Style - Ice";
    public string DisplayAuthor => "yappershq";

    public KreedzStyleIce(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared = shared;
        _logger = shared.GetLoggerFactory().CreateLogger<KreedzStyleIce>();
    }

    public bool Init() => true;

    public void OnAllModulesLoaded()
    {
        var registry = _shared.GetSharpModuleManager()
                              .GetOptionalSharpModuleInterface<IKzStyleRegistry>(IKzStyleRegistry.Identity)?.Instance;

        if (registry is null)
        {
            _logger.LogError("[Kreedz.Style.Ice] Kreedz.Core style registry not found — is the core loaded?");
            return;
        }

        registry.RegisterStyle("ice", "Ice", "ICE", Convars);
        _logger.LogInformation("[Kreedz.Style.Ice] registered.");
    }

    public void Shutdown() { }

    private static readonly IReadOnlyDictionary<string, string> Convars = new Dictionary<string, string>
    {
        ["sv_friction"] = "1",
    };
}
