/*
 * yappershq/Kreedz (KZ) — HUD plugin (cs2kz src/kz/hud)
 *
 * A standalone ModSharp module (split out of Core). Renders the center-panel run timer + speedometer +
 * keys (W A S D C J) + mode + CP/TP counters per-tick via the `show_survival_respawn_status` center-HTML
 * event with the MS flash-fix. Reads run state through the public IKzRunService + IKzModeRegistry, so it
 * needs no Core-internal services — a server can swap or omit it. PB-delta awaits a cached-PB source.
 */

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;
using Kreedz.Shared.Models.Timer;

namespace Kreedz.Hud;

public sealed class KreedzHud : IModSharpModule
{
    private const float UpdateInterval = 0.10f; // 10 Hz

    private readonly ISharedSystem       _shared;
    private readonly IModSharp           _modSharp;
    private readonly IHookManager        _hookManager;
    private readonly IClientManager      _clientManager;
    private readonly ILogger<KreedzHud>  _logger;

    private readonly IGameEvent _hudEvent;
    private readonly float[]    _nextUpdate = new float[PlayerSlot.MaxPlayerCount];

    private IKzRunService?    _run;
    private IKzModeRegistry?  _modes;

    public string DisplayName   => "[Kreedz] HUD";
    public string DisplayAuthor => "yappershq";

    public KreedzHud(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared        = shared;
        _modSharp      = shared.GetModSharp();
        _hookManager   = shared.GetHookManager();
        _clientManager = shared.GetClientManager();
        _logger        = shared.GetLoggerFactory().CreateLogger<KreedzHud>();

        _hudEvent = shared.GetEventManager().CreateEvent("show_survival_respawn_status", true)
                    ?? throw new NullReferenceException("Failed to create KZ HUD event.");
        _hudEvent.SetInt("duration", 1);
        _hudEvent.SetInt("userid", -1);
    }

    public bool Init()
    {
        _hookManager.PlayerRunCommand.InstallHookPost(OnRunCommandPost);
        _modSharp.InstallGameFrameHook(null, OnGameFramePost);
        return true;
    }

    public void OnAllModulesLoaded()
    {
        var mgr = _shared.GetSharpModuleManager();
        _run   = mgr.GetOptionalSharpModuleInterface<IKzRunService>(IKzRunService.Identity)?.Instance;
        _modes = mgr.GetOptionalSharpModuleInterface<IKzModeRegistry>(IKzModeRegistry.Identity)?.Instance;
    }

    public void Shutdown()
    {
        _hookManager.PlayerRunCommand.RemoveHookPost(OnRunCommandPost);
        _modSharp.RemoveGameFrameHook(null, OnGameFramePost);
        _hudEvent.Dispose();
    }

    // Keep the center HTML panel from being cleared each frame (MS flash-fix).
    private void OnGameFramePost(bool a, bool b, bool c)
    {
        var gr = _modSharp.GetGameRules();
        if (gr.IsWarmupPeriod) return;
        gr.IsGameRestart = gr.RestartRoundTime < _modSharp.GetGlobals().CurTime;
    }

    private void OnRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> ret)
    {
        var client = param.Client;
        if (client.IsFakeClient) return;

        var slot = client.Slot;
        var now  = _modSharp.GetGlobals().CurTime;
        if (now < _nextUpdate[slot]) return;
        _nextUpdate[slot] = now + UpdateInterval;

        if (client.GetPlayerController()?.GetPlayerPawn() is not { IsValidEntity: true, IsAlive: true } pawn) return;

        var speed = (int) MathF.Round(pawn.GetAbsVelocity().Length2D());
        var keys  = Keys(param.KeyButtons);
        var tp    = _run?.GetTeleportCount(slot)   ?? 0;
        var cp    = _run?.GetCheckpointCount(slot) ?? 0;
        var mode  = (_modes?.GetPlayerMode(slot) ?? "vnl").ToUpperInvariant();

        var timeLine = "";
        if (_run?.GetTimerInfo(slot) is { Status: ETimerStatus.Running or ETimerStatus.Paused } info)
        {
            var paused = info.Status == ETimerStatus.Paused ? " <font color='#ffd479'>⏸</font>" : "";
            timeLine = $"<font class='fontSize-l' color='#ffffff'>{FormatTime(info.Time)}</font>{paused}<br>";
        }

        var html = timeLine +
                   $"<font class='fontSize-l' color='#8effc1'>{speed}</font> <font class='fontSize-m' color='#c0cbd8'>u/s</font><br>" +
                   $"<font class='fontSize-m' color='#9fb0c8'>{keys}</font><br>" +
                   $"<font class='fontSize-s' color='#7f8fa6'>{mode} · CP {cp} · TP {tp}</font>";

        _hudEvent.SetString("loc_token", html);
        _hudEvent.FireToClient(client);
    }

    private static string FormatTime(float seconds)
    {
        var totalMs = (int) MathF.Round(seconds * 1000f);
        return $"{totalMs / 60000:00}:{totalMs / 1000 % 60:00}.{totalMs % 1000:000}";
    }

    private static string Keys(UserCommandButtons b)
    {
        char K(UserCommandButtons flag, char c) => b.HasFlag(flag) ? c : '_';
        return $"{K(UserCommandButtons.MoveLeft, 'A')} {K(UserCommandButtons.Forward, 'W')} " +
               $"{K(UserCommandButtons.Back, 'S')} {K(UserCommandButtons.MoveRight, 'D')}  " +
               $"{K(UserCommandButtons.Duck, 'C')} {K(UserCommandButtons.Jump, 'J')}";
    }
}
