/*
 * !paint — cs2kz paint, ModSharp edition. cs2kz paints persistent decals at bhop spots via the
 * CMsgPlaceDecalEvent net-message hook (color/size/per-viewer filtering) — ModSharp exposes no
 * net-message hook, so the same practical feature (marking your takeoff spots to read your bhop
 * lines) ships as small persistent X-marks from a reused env_beam ring: green = perf takeoff,
 * red = non-perf. Preference-persisted ("paint"). Per-viewer filtering + decal styling are the
 * documented deviation until ModSharp grows the hook.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal sealed class PaintModule : IModule
{
    private const int   MarkCount = 48; // marks per player; 2 beams each
    private const float MarkSize  = 6f;
    private const int   PerfTicks = 2;  // ground ticks <= this at takeoff = perf (jumpstats convention)

    private readonly InterfaceBridge      _bridge;
    private readonly ICommandManager      _commandManager;
    private readonly IPreferencesModule   _prefs;
    private readonly ILogger<PaintModule> _logger;

    private readonly bool[]           _enabled     = new bool[PlayerSlot.MaxPlayerCount];
    private readonly IBaseModelEntity?[][] _marks  = new IBaseModelEntity?[PlayerSlot.MaxPlayerCount][];
    private readonly int[]            _head        = new int[PlayerSlot.MaxPlayerCount];
    private readonly bool[]           _wasGround   = new bool[PlayerSlot.MaxPlayerCount];
    private readonly int[]            _groundTicks = new int[PlayerSlot.MaxPlayerCount];

    public PaintModule(InterfaceBridge bridge, ICommandManager commandManager, IPreferencesModule prefs, ILogger<PaintModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _prefs          = prefs;
        _logger         = logger;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("paint", (slot, _) =>
        {
            _enabled[slot] = !_enabled[slot];
            _prefs.Set(slot, "paint", _enabled[slot] ? "1" : "0");
            if (!_enabled[slot])
                KillMarks(slot);
            if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
                Loc.Chat(_bridge.LocalizerManager, client, _enabled[slot] ? "Kreedz_Opt_On" : "Kreedz_Opt_Off", "paint");
            return ECommandAction.Handled;
        });

        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        _prefs.Loaded += slot => _enabled[slot] = _prefs.Get(slot, "paint") == "1";
        return true;
    }

    public void Shutdown()
        => _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);

    private void OnProcessMovePre(Sharp.Shared.HookParams.IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive)
            return;

        var slot     = client.Slot;
        var onGround = arg.Pawn.GroundEntityHandle.IsValid();

        if (_enabled[slot] && !onGround && _wasGround[slot] && arg.Pawn.ActualMoveType == MoveType.Walk)
            PlaceMark(slot, arg.Pawn.GetAbsOrigin(), perf: _groundTicks[slot] <= PerfTicks);

        _groundTicks[slot] = onGround ? _groundTicks[slot] + 1 : 0;
        _wasGround[slot]   = onGround;
    }

    private void PlaceMark(PlayerSlot slot, Vector origin, bool perf)
    {
        var ring = _marks[slot] ??= new IBaseModelEntity?[MarkCount * 2];
        var kv = new Dictionary<string, KeyValuesVariantValueItem>
        {
            { "rendercolor", perf ? "0 255 0" : "255 40 40" },
            { "BoltWidth", "1" },
        };

        var z = origin.Z + 1f;
        Span<(Vector A, Vector B)> legs =
        [
            (new Vector(origin.X - MarkSize, origin.Y - MarkSize, z), new Vector(origin.X + MarkSize, origin.Y + MarkSize, z)),
            (new Vector(origin.X - MarkSize, origin.Y + MarkSize, z), new Vector(origin.X + MarkSize, origin.Y - MarkSize, z)),
        ];

        foreach (var (a, b) in legs)
        {
            var i = _head[slot];
            _head[slot] = (i + 1) % ring.Length;

            var beam = ring[i];
            if (beam is not { IsValidEntity: true })
            {
                beam = _bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv);
                if (beam is not { IsValidEntity: true })
                    continue;
                ring[i] = beam;
            }
            else
            {
                beam.RenderColor = perf ? new Color32(0, 255, 0, 255) : new Color32(255, 40, 40, 255);
            }

            beam.SetAbsOrigin(a);
            beam.SetNetVar("m_vecEndPos", b);
        }
    }

    private void KillMarks(PlayerSlot slot)
    {
        if (_marks[slot] is not { } ring)
            return;

        foreach (var m in ring)
            if (m is { IsValidEntity: true })
                m.Kill();

        Array.Clear(ring);
        _head[slot] = 0;
    }
}
