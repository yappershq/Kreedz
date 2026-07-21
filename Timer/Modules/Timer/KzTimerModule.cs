/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ run semantics on top of Timer's run timer (1:1 cs2kz src/kz/timer). A run with **0 teleports is
 * PRO**, **≥1 is STANDARD** — the defining KZ distinction. This module hooks the timer's start/finish
 * events: it resets the teleport counter on start (a fresh Pro attempt) and reports Pro/Standard + the
 * formatted time on finish. Next: record submission (StyleIDFlags + Teleports → Times table, styleless
 * + ban-excluded ranking) and the strict KZ start/end validation gate (via CanStartTimer).
 */

using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models.Timer;

namespace Source2Surf.Timer.Modules;

internal interface IKzTimerModule;

internal sealed class KzTimerModule : IModule, IKzTimerModule, ITimerModuleListener
{
    private readonly InterfaceBridge        _bridge;
    private readonly ITimerModule           _timerModule;
    private readonly ICheckpointModule      _checkpointModule;
    private readonly IKzStyleModule         _styleModule;
    private readonly ILogger<KzTimerModule> _logger;

    public KzTimerModule(InterfaceBridge    bridge,
                         ITimerModule       timerModule,
                         ICheckpointModule  checkpointModule,
                         IKzStyleModule     styleModule,
                         ILogger<KzTimerModule> logger)
    {
        _bridge           = bridge;
        _timerModule      = timerModule;
        _checkpointModule = checkpointModule;
        _styleModule      = styleModule;
        _logger           = logger;
    }

    public bool Init()
    {
        _timerModule.RegisterListener(this);
        return true;
    }

    public void Shutdown() => _timerModule.UnregisterListener(this);

    void ITimerModuleListener.OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        if (controller.GetGameClient() is { } client)
            _checkpointModule.ResetTeleportCount(client.Slot); // fresh run → Pro attempt (0 teleports)
    }

    void ITimerModuleListener.OnPlayerFinishMap(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        if (controller.GetGameClient() is not { } client) return;

        var teleports = _checkpointModule.GetTeleportCount(client.Slot);
        var kind      = teleports == 0 ? "PRO" : "STANDARD";
        var styled    = _styleModule.HasAnyStyle(client.Slot) ? " (styled — unranked)" : "";

        client.Print(HudPrintChannel.Chat, $"Finished — {FormatTime(timerInfo.Time)}  [{kind}]  ({teleports} tp){styled}.");
    }

    private static string FormatTime(float seconds)
    {
        var totalMs = (int) System.MathF.Round(seconds * 1000f);
        var minutes = totalMs / 60000;
        var secs    = totalMs / 1000 % 60;
        var millis  = totalMs % 1000;
        return $"{minutes:00}:{secs:00}.{millis:000}";
    }
}
