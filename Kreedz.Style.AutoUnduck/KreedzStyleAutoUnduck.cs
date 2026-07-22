/*
 * yappershq/Kreedz (KZ) — AutoUnduck style plugin (cs2kz src/kz/style/kz_style_autounduck).
 *
 * Standalone ModSharp module registering the AutoUnduck (AU) style against Kreedz.Core's IKzStyleRegistry.
 * While active: when the player is airborne, ducked, and has clear headroom to stand, it auto-releases the
 * duck (pins m_flLastDuckTime huge so they can't re-duck, and clears the ducking flag). The movement-service
 * fields are reached via the schema net-var API (their typed properties aren't on the packaged interface).
 * Enforced in the ProcessMove hook, gated on the registry's per-player active state.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameObjects;
using Sharp.Shared.Objects;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Style.AutoUnduck;

public sealed unsafe class KreedzStyleAutoUnduck : IModSharpModule
{
    private const string Id = "au";

    private static readonly InteractionLayers WorldMask =
        InteractionLayers.Solid | InteractionLayers.Sky | InteractionLayers.PlayerClip
        | InteractionLayers.WorldGeometry | InteractionLayers.PhysicsProp;

    private readonly ISharedSystem                _shared;
    private readonly IHookManager                 _hookManager;
    private readonly IPhysicsQueryManager         _physics;
    private readonly IConVar?                     _svStandable;
    private readonly ILogger<KreedzStyleAutoUnduck> _logger;

    private IKzStyleRegistry? _registry;
    private readonly bool[] _applying = new bool[PlayerSlot.MaxPlayerCount];

    public string DisplayName   => "[Kreedz] Style - AutoUnduck";
    public string DisplayAuthor => "yappershq";

    public KreedzStyleAutoUnduck(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared      = shared;
        _hookManager = shared.GetHookManager();
        _physics     = shared.GetPhysicsQueryManager();
        _svStandable = shared.GetConVarManager().FindConVar("sv_standable_normal");
        _logger      = shared.GetLoggerFactory().CreateLogger<KreedzStyleAutoUnduck>();
    }

    public bool Init()
    {
        _hookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        return true;
    }

    public void OnAllModulesLoaded()
    {
        _registry = _shared.GetSharpModuleManager()
                          .GetOptionalSharpModuleInterface<IKzStyleRegistry>(IKzStyleRegistry.Identity)?.Instance;

        if (_registry is null)
        {
            _logger.LogError("[Kreedz.Style.AutoUnduck] Kreedz.Core style registry not found — is the core loaded?");
            return;
        }

        _registry.RegisterStyle(Id, "AutoUnduck", "AU", Empty);
        _logger.LogInformation("[Kreedz.Style.AutoUnduck] registered.");
    }

    public void Shutdown() => _hookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);

    // cs2kz KZAutoUnduckStyleService::OnProcessMovement — auto-stand while airborne with headroom.
    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (client.IsFakeClient || _registry?.HasStyle(client.Slot, Id) != true || !arg.Pawn.IsAlive)
            return;

        var slot = client.Slot;
        var ms   = arg.Service;
        var pawn = arg.Pawn;

        var ducked   = ms.GetNetVar<bool>("m_bDucked");
        var onGround = pawn.GroundEntityHandle.IsValid();

        // Only unduck while genuinely airborne + ducked in normal walk movement.
        if (!ducked || pawn.ActualMoveType != MoveType.Walk || onGround)
        {
            if (_applying[slot])
            {
                CancelUnduck(ms);
                _applying[slot] = false;
            }
            return;
        }

        // Already unducking and still holding it — keep pinning.
        if (_applying[slot])
        {
            ApplyUnduck(ms, pawn);
            return;
        }

        var origin = pawn.GetAbsOrigin();
        var hull   = new TraceShapeRay(new TraceShapeHull { Mins = new Vector(-16f, -16f, 0f), Maxs = new Vector(16f, 16f, 54f) });

        // Ground close below (within 9u) — about to land, don't bother unducking (cs2kz).
        var near = _physics.TraceShapeNoPlayers(hull, origin, new Vector(origin.X, origin.Y, origin.Z - 9f),
            WorldMask, CollisionGroupType.Default, TraceQueryFlag.All);
        if (near.DidHit())
            return;

        // Standable ground a touch further down — only auto-unduck over real standable ground.
        var standableZ = _svStandable?.GetFloat() ?? 0.7f;
        var below = _physics.TraceShapeNoPlayers(hull, origin, new Vector(origin.X, origin.Y, origin.Z - 11f),
            WorldMask, CollisionGroupType.Default, TraceQueryFlag.All);
        if (!below.DidHit() || below.PlaneNormal.Z < standableZ)
        {
            CancelUnduck(ms);
            return;
        }

        _applying[slot] = true;
        ApplyUnduck(ms, pawn);
    }

    private static void ApplyUnduck(IMovementService ms, Sharp.Shared.GameEntities.IPlayerPawn pawn)
    {
        ms.SetNetVar("m_flLastDuckTime", 100000f);     // can't re-duck
        pawn.Flags &= ~EntityFlags.Ducking;            // clear ducking each tick (may be in a non-unduckable spot)
    }

    private static void CancelUnduck(IMovementService ms) => ms.SetNetVar("m_flLastDuckTime", 0f);

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
}
