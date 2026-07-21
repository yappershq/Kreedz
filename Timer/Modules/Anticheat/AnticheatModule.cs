/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ anticheat — invalid client-cvar detector (1:1 cs2kz src/kz/anticheat InvalidCvar). Flags illegal
 * client convar values that enable cheating (tampered m_yaw, out-of-range cl_pitchdown/up). Logs and
 * optionally kicks (`kz_ac_autokick`, default off — matching cs2kz). All detection is disabled while
 * `sv_cheats 1` and for fake clients. The full 6-detector suite (nulls/snaptap, bhop, hyperscroll,
 * strafe-hack, subtick) + the ban pipeline read movement telemetry, so they land with the P5 engine.
 */

using System;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Modules;

internal interface IAnticheatModule;

internal sealed class AnticheatModule : IModule, IAnticheatModule
{
    private readonly InterfaceBridge          _bridge;
    private readonly ILogger<AnticheatModule> _logger;
    private readonly IConVar                  _autokick;
    private readonly IConVar?                 _svCheats;

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
        return true;
    }

    public void Shutdown() => _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);

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

        _logger.LogWarning("[KZ.AC] {Name} ({Sid}) invalid cvar: {Violation}", client.Name, client.SteamId, violation);
        if (_autokick.GetBool())
            _bridge.ClientManager.KickClient(client, $"KZ: invalid cvar ({violation})");
        else
            client.Print(HudPrintChannel.Chat, $"[KZ] Invalid client setting flagged: {violation}.");
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
