/*
 * yappershq/Timer (KZ) — race control (Core-side, event-agnostic).
 *
 * Publishes IKzRaceControl so a 3rd-party gate (e.g. an EventManager adapter) can run a "first to finish the
 * map" race WITHOUT any event knowledge leaking into Core. StartRace arms the live field (reuses the `!r`
 * Restart: stop + change track + teleport to the Start zone); each player's clock auto-starts on the normal
 * start-zone exit. Finishes are surfaced off the existing timer-finish listener, in finish order, while active.
 */

using System;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;
using Kreedz.Shared.Interfaces.Listeners;
using Kreedz.Shared.Models.Timer;

namespace Kreedz.Modules;

internal interface IKzRaceModule;

internal sealed class KzRaceModule : IModule, IKzRaceModule, IKzRaceControl, ITimerModuleListener
{
    private readonly InterfaceBridge _bridge;
    private readonly ITimerModule    _timerModule;

    private bool _active;
    private int  _finishOrder; // increments per finish so the adapter gets 1st/2nd/3rd for free

    public KzRaceModule(InterfaceBridge bridge, ITimerModule timerModule)
    {
        _bridge      = bridge;
        _timerModule = timerModule;
    }

    public bool Init()
    {
        _timerModule.RegisterListener(this);
        return true;
    }

    // Publish the race-control service so an external gate can drive it (mirrors KzTimerModule's IKzRunService).
    public void OnPostInit(ServiceProvider provider)
        => _bridge.SharpModuleManager.RegisterSharpModuleInterface<IKzRaceControl>(
               _bridge.Entrypoint, IKzRaceControl.Identity, this);

    public void Shutdown() => _timerModule.UnregisterListener(this);

    // ── IKzRaceControl ──────────────────────────────────────────────────────────
    public event Action<PlayerSlot, ITimerInfo, int>? PlayerFinished;

    public bool IsRaceActive => _active;

    public void StartRace(int track = 0)
    {
        _active      = true;
        _finishOrder = 0;
        foreach (var client in _bridge.ClientManager.GetGameClients(true))
            if (!client.IsFakeClient)
                _timerModule.Restart(client.Slot, track);
    }

    public bool ArmPlayer(PlayerSlot slot, int track = 0)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { IsFakeClient: false })
            return false;
        _timerModule.Restart(slot, track);
        return true;
    }

    public void StopPlayer(PlayerSlot slot) => _timerModule.StopTimer(slot);

    public void StopRace()
    {
        _active = false;
        foreach (var client in _bridge.ClientManager.GetGameClients(true))
            if (!client.IsFakeClient)
                _timerModule.StopTimer(client.Slot);
    }

    // ── ITimerModuleListener ──────────────────────────────────────────────────────
    void ITimerModuleListener.OnPlayerFinishMap(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        if (!_active || controller.GetGameClient() is not { } client)
            return;

        PlayerFinished?.Invoke(client.Slot, timerInfo, ++_finishOrder);
    }
}
