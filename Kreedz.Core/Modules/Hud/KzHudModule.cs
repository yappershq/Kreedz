/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ HUD (1:1 cs2kz src/kz/hud): a center-panel run timer + speedometer + keys (W A S D C J) + mode +
 * teleport counter, rendered per-tick via the `show_survival_respawn_status` center-HTML event with the
 * MS flash-fix (patch gameRules.IsGameRestart each frame so the panel isn't cleared). Replaces the
 * surf HUD in the KZ build. PB-delta needs a per-player cached PB (async DB — folds in with the record
 * cache); spectator/replay HUD is a follow-up. This is the always-on KZ run+movement HUD.
 */

using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Models.Timer;

namespace Kreedz.Modules;

internal sealed class KzHudModule : IModule, IHudModule
{
    private const float UpdateInterval = 0.10f; // 10 Hz

    private readonly InterfaceBridge      _bridge;
    private readonly ICheckpointModule    _checkpoint;
    private readonly IModeModule          _mode;
    private readonly ITimerModule         _timer;
    private readonly ILogger<KzHudModule> _logger;

    private readonly IGameEvent _hudEvent;
    private readonly float[]    _nextUpdate = new float[PlayerSlot.MaxPlayerCount];

    public KzHudModule(InterfaceBridge bridge, ICheckpointModule checkpoint, IModeModule mode, ITimerModule timer, ILogger<KzHudModule> logger)
    {
        _bridge     = bridge;
        _checkpoint = checkpoint;
        _mode       = mode;
        _timer      = timer;
        _logger     = logger;

        _hudEvent = bridge.EventManager.CreateEvent("show_survival_respawn_status", true)
                    ?? throw new NullReferenceException("Failed to create KZ HUD event.");
        _hudEvent.SetInt("duration", 1);
        _hudEvent.SetInt("userid", -1);
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerRunCommand.InstallHookPost(OnRunCommandPost);
        _bridge.ModSharp.InstallGameFrameHook(null, OnGameFramePost);
        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerRunCommand.RemoveHookPost(OnRunCommandPost);
        _bridge.ModSharp.RemoveGameFrameHook(null, OnGameFramePost);
        _hudEvent.Dispose();
    }

    // Keep the center HTML panel from being cleared each frame (MS flash-fix).
    private void OnGameFramePost(bool a, bool b, bool c)
    {
        var gr = _bridge.GameRules;
        if (!gr.IsWarmupPeriod)
            gr.IsGameRestart = gr.RestartRoundTime < _bridge.GlobalVars.CurTime;
    }

    private void OnRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> ret)
    {
        var client = param.Client;
        if (client.IsFakeClient) return;

        var slot = client.Slot;
        var now  = _bridge.GlobalVars.CurTime;
        if (now < _nextUpdate[slot]) return;
        _nextUpdate[slot] = now + UpdateInterval;

        if (client.GetPlayerController()?.GetPlayerPawn() is not { IsValidEntity: true, IsAlive: true } pawn) return;

        var speed = (int) MathF.Round(pawn.GetAbsVelocity().Length2D());
        var keys  = Keys(param.KeyButtons);
        var tp    = _checkpoint.GetTeleportCount(slot);
        var mode  = _mode.GetMode(slot).ToUpperInvariant();

        // Run timer line — only while a run is active (Running/Paused); hidden when stopped.
        var timeLine = "";
        if (_timer.GetTimerInfo(slot) is { Status: ETimerStatus.Running or ETimerStatus.Paused } info)
        {
            var paused = info.Status == ETimerStatus.Paused ? " <font color='#ffd479'>⏸</font>" : "";
            timeLine = $"<font class='fontSize-l' color='#ffffff'>{FormatTime(info.Time)}</font>{paused}<br>";
        }

        var html = timeLine +
                   $"<font class='fontSize-l' color='#8effc1'>{speed}</font> <font class='fontSize-m' color='#c0cbd8'>u/s</font><br>" +
                   $"<font class='fontSize-m' color='#9fb0c8'>{keys}</font><br>" +
                   $"<font class='fontSize-s' color='#7f8fa6'>{mode} · TP {tp}</font>";

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
