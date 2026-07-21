using System;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Kreedz.Shared.Interfaces;

/// <summary>
/// One AirAccelerate call's telemetry (cs2kz Jumpstats aaCall) — the native per-call data the full jumpstats
/// stat set (badAngles/sync/gain) is computed from. Captured by Kreedz.Core's movement layer in the
/// AirAccelerate detour (velocity pre/post the engine's air-accel, the wish input, and the held buttons) and
/// published to consumers (Kreedz.Jumpstats) so the stats aren't a per-tick approximation.
/// </summary>
public readonly record struct AaCall(
    Vector             Wishdir,
    float              WishSpeed,
    Vector             VelocityPre,
    Vector             VelocityPost,
    UserCommandButtons Buttons,
    float              Duration,
    float              PrevYaw,
    float              CurrentYaw,
    float              ExternalSpeedDiff);

/// <summary>Published by Kreedz.Core; consumed cross-plugin (e.g. Kreedz.Jumpstats) for per-aaCall stats.</summary>
public interface IKzMovementTelemetry
{
    static readonly string Identity = typeof(IKzMovementTelemetry).FullName!;

    /// <summary>Raised once per AirAccelerate call, after the engine's air-accel, with that call's telemetry.</summary>
    event Action<PlayerSlot, AaCall>? AirAccelerate;
}
