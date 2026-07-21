/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ run semantics on top of Timer's run timer (1:1 cs2kz src/kz/timer). A run with **0 teleports is
 * PRO**, **≥1 is STANDARD** — the defining KZ distinction. This module hooks the timer's start/finish
 * events: it resets the teleport counter on start (a fresh Pro attempt) and reports Pro/Standard + the
 * formatted time on finish. Next: record submission (StyleIDFlags + Teleports → Times table, styleless
 * + ban-excluded ranking) and the strict KZ start/end validation gate (via CanStartTimer).
 */

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;
using Kreedz.Shared.Interfaces.Listeners;
using Kreedz.Shared.Models.Timer;

namespace Kreedz.Modules;

internal interface IKzTimerModule;

internal sealed class KzTimerModule : IModule, IKzTimerModule, ITimerModuleListener, IKzRunService
{
    private readonly InterfaceBridge        _bridge;
    private readonly ITimerModule           _timerModule;
    private readonly ICheckpointModule      _checkpointModule;
    private readonly IKzStyleModule         _styleModule;
    private readonly ILogger<KzTimerModule> _logger;

    // cs2kz KZTimerService::JustLanded — a run may not start within this window of touching the ground, so
    // you can't perf-land into the start zone and instant-start. Tracked from our own per-tick ground state.
    private const float LandingGrace   = 0.05f; // KZ_TIMER_MIN_GROUND_TIME
    private const float RecentTeleport = 0.05f; // KZ_RECENT_TELEPORT_THRESHOLD
    private readonly bool[]  _wasGround    = new bool[PlayerSlot.MaxPlayerCount];
    private readonly float[] _lastLandTime = new float[PlayerSlot.MaxPlayerCount];

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
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        return true;
    }

    // Track the tick a player last touched ground, so CanStartTimer can enforce cs2kz's JustLanded grace.
    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient)
            return;

        var slot     = client.Slot;
        var onGround = arg.Pawn.GroundEntityHandle.IsValid();

        if (onGround && !_wasGround[slot])
            _lastLandTime[slot] = _bridge.ModSharp.GetGlobals().CurTime;

        _wasGround[slot] = onGround;
    }

    // Publish the public run-state service so external plugins (HUD/Global) can read timer+cp/tp state
    // and subscribe to finishes without Core-internal deps.
    public void OnPostInit(ServiceProvider provider)
        => _bridge.SharpModuleManager.RegisterSharpModuleInterface<IKzRunService>(
               _bridge.Entrypoint, IKzRunService.Identity, this);

    public void Shutdown()
    {
        _timerModule.UnregisterListener(this);
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
    }

    // ── IKzRunService ─────────────────────────────────────────────────────────
    public event Action<PlayerSlot, ITimerInfo, int, bool>? RunFinished;
    ITimerInfo? IKzRunService.GetTimerInfo(PlayerSlot slot)      => _timerModule.GetTimerInfo(slot);
    int IKzRunService.GetTeleportCount(PlayerSlot slot)          => _checkpointModule.GetTeleportCount(slot);
    int IKzRunService.GetCheckpointCount(PlayerSlot slot)        => _checkpointModule.GetCheckpointCount(slot);

    // Strict start-validation gate (cs2kz KZTimerService::TimerStart): alive + Walk (no start while dead or
    // noclipping) + JustLanded (can't perf-land into the start zone and instant-start) + JustTeleported
    // (can't cp/tp into the start and instant-start). Still follow-ups: inPerf, valid-jump (airborne start).
    bool ITimerModuleListener.CanStartTimer(IPlayerController controller, IPlayerPawn pawn)
    {
        if (pawn is not { IsAlive: true, MoveType: MoveType.Walk })
            return false;

        if (controller.GetGameClient() is { } client)
        {
            var now = _bridge.ModSharp.GetGlobals().CurTime;
            if (now - _lastLandTime[client.Slot] < LandingGrace)                              return false; // JustLanded
            if (now - _checkpointModule.GetLastTeleportTime(client.Slot) < RecentTeleport)    return false; // JustTeleported
        }

        return true;
    }

    void ITimerModuleListener.OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        if (controller.GetGameClient() is { } client)
            _checkpointModule.ResetTeleportCount(client.Slot); // fresh run → Pro attempt (0 teleports)
    }

    void ITimerModuleListener.OnPlayerFinishMap(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        if (controller.GetGameClient() is not { } client) return;

        var teleports = _checkpointModule.GetTeleportCount(client.Slot);
        var styled    = _styleModule.HasAnyStyle(client.Slot);
        var kind      = teleports == 0 ? "PRO" : "STANDARD";

        var key = styled ? "Kreedz_Finish_Styled" : "Kreedz_Finish";
        Loc.Chat(_bridge.LocalizerManager, client, key, FormatTime(timerInfo.Time), kind, teleports);

        RunFinished?.Invoke(client.Slot, timerInfo, teleports, styled);
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
