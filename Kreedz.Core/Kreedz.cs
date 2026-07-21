/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Kreedz.Managers;
using Kreedz.Managers.Command;
using Kreedz.Managers.Replay;
using Kreedz.Managers.Request;
using Kreedz.Modules;
using Kreedz.Shared.Interfaces;

[assembly: DisableRuntimeMarshalling]

namespace Kreedz;

public class Kreedz : IModSharpModule
{
    private readonly InterfaceBridge         _bridge;
    private readonly ILogger<Kreedz>          _logger;
    private readonly ServiceProvider         _serviceProvider;
    private readonly CancellationTokenSource _token;
    private int                              _shutdownState;

    public Kreedz(ISharedSystem   shared,
                 string?         dllPath,
                 string?         sharpPath,
                 Version?        version,
                 IConfiguration? coreConfiguration,
                 bool            hotReload)
    {
        ArgumentNullException.ThrowIfNull(dllPath);
        ArgumentNullException.ThrowIfNull(sharpPath);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(coreConfiguration);

        var token = new CancellationTokenSource();

        /*var configuration = new ConfigurationBuilder()
                            .AddJsonFile(Path.Combine(dllPath, "appsettings.json"), false, false)
                            .Build();*/

        var bridge = new InterfaceBridge(this,
                                         dllPath,
                                         sharpPath,
                                         version,
                                         shared,
                                         coreConfiguration,
                                         hotReload,
                                         token.Token,
                                         shared.GetModSharp()
                                               .HasCommandLine("-debug"));

        var factory = shared.GetLoggerFactory();
        var logger  = factory.CreateLogger<Kreedz>();

        var gameData = shared.GetModSharp()
                             .GetGameData();

        gameData.Register("kreedz.games");

        /*if (File.Exists(Path.Combine(sharpPath, "gamedata", "test.games.kv")))
        {
            gameData.Register("test.games");
            _testGameData = true;
        }*/

        var services = new ServiceCollection();

        services.AddSingleton(bridge);
        services.AddSingleton(factory);
        services.AddSingleton(shared);
        services.AddSingleton(gameData);
        /*services.AddSingleton<IConfiguration>(configuration);*/
        /*ConfigureDebugServices(services, bridge);*/
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();

        _token  = token;
        _bridge = bridge;
        _logger = logger;
    }

    public string DisplayName   => "Kreedz";
    public string DisplayAuthor => "yappershq";

    public bool Init()
    {
        foreach (var service in _serviceProvider.GetServices<IManager>())
        {
            if (service.Init())
            {
#if DEBUG
                _logger.LogInformation("Init service {service}!",
                                       service.GetType()
                                              .FullName);
#endif
                continue;
            }

            _logger.LogError("Failed to init {service}!",
                             service.GetType()
                                    .FullName);

            return false;
        }

        foreach (var service in _serviceProvider.GetServices<IModule>())
        {
            if (service.Init())
            {
#if DEBUG
                _logger.LogInformation("Init module {service}!",
                                       service.GetType()
                                              .FullName);
#endif
                continue;
            }

            _logger.LogError("Failed to init {service}!",
                             service.GetType()
                                    .FullName);

            return false;
        }

        foreach (var service in _serviceProvider.GetServices<IManager>())
        {
            try
            {
                service.OnPostInit();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "An error occurred while calling PostInit for {type}", service.GetType().FullName);
            }
        }

        foreach (var service in _serviceProvider.GetServices<IModule>())
        {
            try
            {
                service.OnPostInit(_serviceProvider);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "An error occurred while calling PostInit for {type}", service.GetType().FullName);
            }
        }

        return true;
    }

    public void PostInit()
    {
        RefreshRequestManager();
        RefreshCommandManager();
        RefreshReplayProvider();
    }

    public void OnLibraryConnected(string moduleIdentity)
    {
        RefreshRequestManager();
        RefreshReplayProvider();

        if (moduleIdentity.Equals(ICommandManager.Identity, StringComparison.Ordinal))
        {
            RefreshCommandManager();
        }
    }

    public void OnLibraryDisconnect(string moduleIdentity)
    {
        if (moduleIdentity.Equals(IRequestManager.Identity, StringComparison.Ordinal))
        {
            SwitchRequestManagerToLiteDb();
        }
        else if (moduleIdentity.Equals(ICommandManager.Identity, StringComparison.Ordinal))
        {
            SwitchCommandManagerToFallback();
        }
    }

    public void OnAllModulesLoaded()
    {
        RefreshRequestManager();
        RefreshCommandManager();
        RefreshReplayProvider();
        ResolveLocalizer();
    }

    private void ResolveLocalizer()
    {
        var lm = _bridge.SharpModuleManager
                        .GetOptionalSharpModuleInterface<Sharp.Modules.LocalizerManager.Shared.ILocalizerManager>(
                            Sharp.Modules.LocalizerManager.Shared.ILocalizerManager.Identity)?.Instance;

        _bridge.LocalizerManager = lm;

        if (lm is null)
        {
            _logger.LogInformation("[Kreedz] ILocalizerManager not available — user-facing text will be silent.");
            return;
        }

        var localesPath = System.IO.Path.Combine(_bridge.SharpPath, "locales");
        if (!System.IO.Directory.Exists(localesPath)) return;

        foreach (var file in System.IO.Directory.GetFiles(localesPath, "kreedz*.json"))
            lm.LoadLocaleFile(System.IO.Path.GetFileNameWithoutExtension(file));
    }

    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownState, 1) != 0)
        {
            return;
        }

        try
        {
            _serviceProvider.GetRequiredService<IGameData>()
                            .Unregister("kreedz.games");

            foreach (var service in _serviceProvider.GetServices<IManager>())
            {
                try
                {
                    service.Shutdown();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "An error occurred while calling Shutdown for {type}", service.GetType().FullName);
                }
            }

            foreach (var service in _serviceProvider.GetServices<IModule>())
            {
                try
                {
                    service.Shutdown();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "An error occurred while calling Shutdown for {type}", service.GetType().FullName);
                }
            }

            _token.Cancel();

            _logger.LogInformation("Shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error when shutting down");

            // ignored
        }
        finally
        {
            try
            {
                _serviceProvider.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error when disposing ServiceProvider");
            }

            _token.Dispose();
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging();

        services.AddManagerService();
        services.AddModuleService();
    }

    public T GetService<T>()
        => _serviceProvider.GetService<T>() ?? throw new ("Failed to get service");

    private void RefreshRequestManager()
    {
        if (_serviceProvider.GetService<IRequestManager>() is RequestManagerProxy proxy)
        {
            proxy.RefreshManager();

            return;
        }

        _logger.LogWarning("IRequestManager is not RequestManagerProxy, skip refresh.");
    }

    private void SwitchRequestManagerToLiteDb()
    {
        if (_serviceProvider.GetService<IRequestManager>() is RequestManagerProxy proxy)
        {
            proxy.UseFallback();

            return;
        }

        _logger.LogWarning("IRequestManager is not RequestManagerProxy, cannot force LiteDB fallback.");
    }

    private void RefreshCommandManager()
    {
        if (_serviceProvider.GetService<ICommandManager>() is CommandManagerProxy proxy)
        {
            proxy.RefreshManager();

            return;
        }

        _logger.LogWarning("ICommandManager is not CommandManagerProxy, skip refresh.");
    }

    private void SwitchCommandManagerToFallback()
    {
        if (_serviceProvider.GetService<ICommandManager>() is CommandManagerProxy proxy)
        {
            proxy.UseFallback();

            return;
        }

        _logger.LogWarning("ICommandManager is not CommandManagerProxy, cannot force fallback.");
    }

    private void RefreshReplayProvider()
    {
        _serviceProvider.GetService<ReplayProviderProxy>()?.RefreshProvider();
    }
}
