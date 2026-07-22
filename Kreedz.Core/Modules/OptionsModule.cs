/*
 * Jumpstats option toggles (cs2kz optionService keys): !jsfailstats (failstat reports on/off, default on)
 * and !jsalways (miss/edge info on every non-block jump, default off). Values persist via the
 * preferences store and are read cross-plugin by Kreedz.Jumpstats through IKzPreferences.
 */

using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal sealed class OptionsModule : IModule
{
    private readonly InterfaceBridge        _bridge;
    private readonly ICommandManager        _commandManager;
    private readonly IPreferencesModule     _prefs;
    private readonly ILogger<OptionsModule> _logger;

    public OptionsModule(InterfaceBridge bridge, ICommandManager commandManager, IPreferencesModule prefs, ILogger<OptionsModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _prefs          = prefs;
        _logger         = logger;
    }

    public bool Init()
    {
        Bind("jsfailstats", "jsFailstats", defaultOn: true);
        Bind("jsalways",    "jsAlways",    defaultOn: false);
        return true;
    }

    // Publish the chat-command surface for satellite plugins (Paint, RankTitles) — Core's command
    // proxy routes to the external CommandCenter or the built-in fallback, so satellites don't care.
    public void OnPostInit(Microsoft.Extensions.DependencyInjection.ServiceProvider provider)
        => _bridge.SharpModuleManager.RegisterSharpModuleInterface<IKzCommands>(
            _bridge.Entrypoint, IKzCommands.Identity, new KzCommandsAdapter(_commandManager));

    private sealed class KzCommandsAdapter(ICommandManager commands) : IKzCommands
    {
        public void AddClientChatCommand(string command, IKzCommands.Handler handler)
            => commands.AddClientChatCommand(command, (slot, cmd) => handler(slot, cmd));
    }

    private void Bind(string command, string key, bool defaultOn)
        => _commandManager.AddClientChatCommand(command, (slot, _) =>
        {
            var on = (_prefs.Get(slot, key) ?? (defaultOn ? "1" : "0")) == "1";
            on = !on;
            _prefs.Set(slot, key, on ? "1" : "0");

            if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
                Loc.Chat(_bridge.LocalizerManager, client, on ? "Kreedz_Opt_On" : "Kreedz_Opt_Off", command);

            return ECommandAction.Handled;
        });
}
