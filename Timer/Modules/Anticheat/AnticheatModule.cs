/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ anticheat (1:1 cs2kz src/kz/anticheat). Two detectors so far:
 *   1. Invalid client-cvar — illegal client convar values that enable cheating (tampered m_yaw,
 *      out-of-range cl_pitchdown/up), checked on spawn.
 *   2. Bhop-hack — an inhuman chain of consecutive perfect bhops (each jump landing within the perf
 *      window). No human hits 25 perfs in a row; a scripted bhop does it every jump.
 * Both log and optionally kick (`kz_ac_autokick`, default off — matching cs2kz). All detection is
 * disabled while `sv_cheats 1` and for fake clients. The remaining telemetry detectors (nulls/snaptap,
 * hyperscroll, strafe-hack, subtick) + the ban pipeline layer onto the same movement hook.
 */

using System;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Modules;

internal interface IAnticheatModule;

internal sealed class AnticheatModule : IModule, IAnticheatModule
{
    private const int   BhopHackChain = 25;       // cs2kz — perfs in a row that no human can hit
    private const float PerfWindow    = 0.02f;     // cs2kz BH_PERF_WINDOW
    private const float TickTime      = 1f / 64f;

    private readonly InterfaceBridge          _bridge;
    private readonly ILogger<AnticheatModule> _logger;
    private readonly IConVar                  _autokick;
    private readonly IConVar?                 _svCheats;

    private readonly bool[]  _wasGround  = new bool[PlayerSlot.MaxPlayerCount];
    private readonly float[] _groundTime = new float[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _perfChain  = new int[PlayerSlot.MaxPlayerCount];

    public AnticheatModule(InterfaceBridge bridge, ILogger<AnticheatModule> logger)
    {
        _bridge   = bridge;
        _logger   = logger;
        _autokick = bridge.ConVarManager.CreateConVar("kz_ac_autokick", false)!;
        _svCheats = bridge.ConVarManager.FindConVar("sv_cheats");
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
    }

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive) return;
        if (_svCheats?.GetBool() ?? false) return;

        var slot     = client.Slot;
        var onGround = arg.Pawn.GroundEntityHandle.IsValid();

        if (onGround && !_wasGround[slot]) _groundTime[slot] = 0f; // landed
        if (onGround) _groundTime[slot] += TickTime;

        if (!onGround && _wasGround[slot]) // took off
        {
            if (_groundTime[slot] <= PerfWindow)
            {
                if (++_perfChain[slot] >= BhopHackChain)
                {
                    Flag(client, $"bhop-hack ({_perfChain[slot]} perfect bhops in a row)");
                    _perfChain[slot] = 0;
                }
            }
            else
            {
                _perfChain[slot] = 0;
            }
        }

        _wasGround[slot] = onGround;
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var client = @params.Client;
        if (client.IsFakeClient) return;

        // Client cvars replicate shortly after spawn — check next frame.
        _bridge.ModSharp.InvokeFrameAction(() => CheckClient(client));
    }

    private void CheckClient(IGameClient client)
    {
        if (!client.IsValid || (_svCheats?.GetBool() ?? false)) return; // no detection while sv_cheats

        if (FirstViolation(client) is not { } violation) return;

        Flag(client, $"invalid cvar ({violation})");
    }

    private void Flag(IGameClient client, string reason)
    {
        _logger.LogWarning("[KZ.AC] {Name} ({Sid}) flagged: {Reason}", client.Name, client.SteamId, reason);
        if (_autokick.GetBool())
            _bridge.ClientManager.KickClient(client, $"KZ: {reason}");
        else
            client.Print(HudPrintChannel.Chat, $"[KZ] Anticheat flagged: {reason}.");
    }

    private static string? FirstViolation(IGameClient client)
    {
        if (Value(client, "m_yaw")        is { } yaw && Math.Abs(yaw - 0.022) > 0.0005) return "m_yaw";
        if (Value(client, "cl_pitchdown") is { } pd  && pd > 89.0001)                   return "cl_pitchdown";
        if (Value(client, "cl_pitchup")   is { } pu  && pu < -89.0001)                  return "cl_pitchup";
        return null;
    }

    private static double? Value(IGameClient client, string cvar)
        => double.TryParse(client.GetConVarValue(cvar), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
