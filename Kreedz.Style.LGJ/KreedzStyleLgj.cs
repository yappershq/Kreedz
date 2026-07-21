/*
 * yappershq/Kreedz (KZ) — Legacy Jump style plugin
 *
 * Standalone ModSharp module registering the Legacy Jump (LGJ) style against Kreedz.Core's IKzStyleRegistry.
 * A convar-only style (no custom hooks) — drop this DLL next to Kreedz.Core to offer it via !lgj/!style.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Style.Lgj;

public sealed class KreedzStyleLgj : IModSharpModule
{
    private readonly ISharedSystem _shared;
    private readonly ILogger<KreedzStyleLgj> _logger;

    public string DisplayName   => "[Kreedz] Style - Legacy Jump";
    public string DisplayAuthor => "yappershq";

    public KreedzStyleLgj(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared = shared;
        _logger = shared.GetLoggerFactory().CreateLogger<KreedzStyleLgj>();
    }

    public bool Init() => true;

    public void OnAllModulesLoaded()
    {
        var registry = _shared.GetSharpModuleManager()
                              .GetOptionalSharpModuleInterface<IKzStyleRegistry>(IKzStyleRegistry.Identity)?.Instance;

        if (registry is null)
        {
            _logger.LogError("[Kreedz.Style.LGJ] Kreedz.Core style registry not found — is the core loaded?");
            return;
        }

        registry.RegisterStyle("lgj", "Legacy Jump", "LGJ", Convars);
        _logger.LogInformation("[Kreedz.Style.LGJ] registered.");
    }

    public void Shutdown() { }

    private static readonly IReadOnlyDictionary<string, string> Convars = new Dictionary<string, string>
    {
        ["sv_legacy_jump"] = "true",
    };
}
