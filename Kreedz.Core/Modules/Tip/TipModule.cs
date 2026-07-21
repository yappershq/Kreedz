/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ tips (1:1 cs2kz src/kz/tip): rotating help messages broadcast to everyone every `TipInterval`.
 * `!tips` toggles them per-player (default on). Content is placeholder English → localized string table
 * when i18n lands.
 */

using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal interface ITipModule;

internal sealed class TipModule : IModule, ITipModule
{
    private const double TipIntervalSeconds = 180.0;

    // Localized tip keys (content in kreedz.json) — cycled round-robin.
    private static readonly string[] Tips =
    {
        "Kreedz_Tip_1", "Kreedz_Tip_2", "Kreedz_Tip_3", "Kreedz_Tip_4", "Kreedz_Tip_5",
    };

    private readonly InterfaceBridge    _bridge;
    private readonly ICommandManager    _commandManager;
    private readonly ILogger<TipModule> _logger;
    private readonly bool[]             _enabled = new bool[PlayerSlot.MaxPlayerCount];

    private int  _next;
    private Guid _timer;

    public TipModule(InterfaceBridge bridge, ICommandManager commandManager, ILogger<TipModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _logger         = logger;
        Array.Fill(_enabled, true);
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("tips", (slot, _) =>
        {
            _enabled[slot] = !_enabled[slot];
            if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } c)
                Loc.Chat(_bridge.LocalizerManager, c, _enabled[slot] ? "Kreedz_Tips_On" : "Kreedz_Tips_Off");
            return ECommandAction.Handled;
        });

        _timer = _bridge.ModSharp.PushTimer(BroadcastNextTip, TipIntervalSeconds, GameTimerFlags.Repeatable);
        return true;
    }

    public void Shutdown()
    {
        if (_timer != Guid.Empty) _bridge.ModSharp.StopTimer(_timer);
    }

    private void BroadcastNextTip()
    {
        if (Tips.Length == 0) return;

        var tipKey = Tips[_next];
        _next = (_next + 1) % Tips.Length;

        foreach (var client in _bridge.ClientManager.GetGameClients(inGame: true))
            if (!client.IsFakeClient && _enabled[client.Slot])
                Loc.Chat(_bridge.LocalizerManager, client, tipKey);
    }
}
