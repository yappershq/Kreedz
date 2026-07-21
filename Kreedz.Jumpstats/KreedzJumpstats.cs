/*
 * yappershq/Kreedz (KZ) — Jumpstats plugin (cs2kz src/kz/jumpstats)
 *
 * A standalone ModSharp module (split out of Core, like the mode/style/anticheat plugins). Depends only
 * on ISharedSystem primitives + the public IKzStyleRegistry (to void styled jumps), so a server can
 * install or omit it.
 *
 * Detects takeoffs/landings on the per-tick movement hook, computes jump distance, classifies LongJump
 * vs Bhop, and reports distance + tier (Meh→Wrecker). The full 1:1 stat set (sync/strafes/badAngles/
 * overlap/edge/block, per-mode tier tables, strict validation) needs the native movement AACall telemetry
 * — it layers on once the CKZ movement detours carry physics (they're pass-through today).
 */

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Jumpstats;

public enum JumpType { LongJump, Bhop }

public enum DistanceTier { None, Meh, Impressive, Perfect, Godlike, Ownage, Wrecker }

public sealed class KreedzJumpstats : IModSharpModule
{
    private const float OffsetUnits = 32f;   // KZ block offset added to raw horizontal distance
    private const int   PerfTicks   = 2;     // ground ticks <= this before takeoff -> bhop (perf-ish)
    private const float MinTierDist = 217f;  // below the Meh threshold -> not announced

    private readonly ISharedSystem            _shared;
    private readonly IHookManager             _hookManager;
    private readonly IClientManager           _clientManager;
    private readonly ILogger<KreedzJumpstats> _logger;

    private IKzStyleRegistry? _styles;

    private readonly bool[]     _wasOnGround = new bool[PlayerSlot.MaxPlayerCount];
    private readonly int[]      _groundTicks = new int[PlayerSlot.MaxPlayerCount];
    private readonly bool[]     _tracking    = new bool[PlayerSlot.MaxPlayerCount];
    private readonly Vector[]   _takeoff     = new Vector[PlayerSlot.MaxPlayerCount];
    private readonly JumpType[] _type        = new JumpType[PlayerSlot.MaxPlayerCount];

    public string DisplayName   => "[Kreedz] Jumpstats";
    public string DisplayAuthor => "yappershq";

    public KreedzJumpstats(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared        = shared;
        _hookManager   = shared.GetHookManager();
        _clientManager = shared.GetClientManager();
        _logger        = shared.GetLoggerFactory().CreateLogger<KreedzJumpstats>();
    }

    public bool Init()
    {
        _hookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        return true;
    }

    public void OnAllModulesLoaded()
        => _styles = _shared.GetSharpModuleManager()
                            .GetOptionalSharpModuleInterface<IKzStyleRegistry>(IKzStyleRegistry.Identity)?.Instance;

    public void Shutdown() => _hookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient) return;

        var pawn = arg.Pawn;
        if (!pawn.IsAlive) return;

        var slot     = client.Slot;
        var onGround = pawn.GroundEntityHandle.IsValid();
        var origin   = pawn.GetAbsOrigin();

        if (_wasOnGround[slot] && !onGround)
        {
            // Takeoff.
            _takeoff[slot]  = origin;
            _type[slot]     = _groundTicks[slot] <= PerfTicks ? JumpType.Bhop : JumpType.LongJump;
            _tracking[slot] = pawn.ActualMoveType is MoveType.Walk; // ignore noclip/ladder starts
        }
        else if (!_wasOnGround[slot] && onGround && _tracking[slot])
        {
            // Landing.
            _tracking[slot] = false;
            var dx   = origin.X - _takeoff[slot].X;
            var dy   = origin.Y - _takeoff[slot].Y;
            var dist = MathF.Sqrt(dx * dx + dy * dy) + OffsetUnits;

            if (dist >= MinTierDist && _styles?.HasAnyStyle(slot) != true) // styled runs don't count (1:1)
                Report(slot, _type[slot], dist);
        }

        _groundTicks[slot] = onGround ? _groundTicks[slot] + 1 : 0;
        _wasOnGround[slot] = onGround;
    }

    private void Report(PlayerSlot slot, JumpType type, float distance)
    {
        var tier = Tier(distance);
        if (tier == DistanceTier.None) return;

        var label = type == JumpType.Bhop ? "BH" : "LJ";
        if (_clientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            client.Print(HudPrintChannel.Chat, $"{label}: {distance:0.0}u — {tier}!");
    }

    // VNL/CKZ LongJump tier thresholds (ascending). Full per-mode/per-type tables land with the port.
    private static DistanceTier Tier(float d) => d switch
    {
        >= 284f => DistanceTier.Wrecker,
        >= 280f => DistanceTier.Ownage,
        >= 275f => DistanceTier.Godlike,
        >= 270f => DistanceTier.Perfect,
        >= 265f => DistanceTier.Impressive,
        >= 217f => DistanceTier.Meh,
        _       => DistanceTier.None,
    };
}
