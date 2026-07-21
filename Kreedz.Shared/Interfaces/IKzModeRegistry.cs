using System;
using System.Collections.Generic;
using Sharp.Shared.Units;

namespace Kreedz.Shared.Interfaces;

/// <summary>
/// The KZ mode registry, published by Kreedz.Core. External mode plugins (e.g. Kreedz.Mode.VNL,
/// Kreedz.Mode.CKZ) consume it in OnAllSharpModulesLoaded to register their mode's convar layer and to
/// gate their own movement hooks on <see cref="GetPlayerMode"/>. The Core owns the per-player current
/// mode, the <c>!mode</c> command, and applying the registered convars on switch/spawn — mirroring how
/// cs2kz ships modes as separate plugins.
/// </summary>
public interface IKzModeRegistry
{
    static readonly string Identity = typeof(IKzModeRegistry).FullName!;

    /// <summary>Register a mode's metadata + convar layer. Idempotent per id (last registration wins).</summary>
    void RegisterMode(string id, string name, string shortName, IReadOnlyDictionary<string, string> convars);

    /// <summary>
    /// Register the mode's native-movement callbacks. Optional — a mode with no custom physics (e.g. VNL)
    /// skips this and gets stock movement. Core installs the movement detours once and routes each player's
    /// callbacks to the impl registered for their active mode id. Idempotent per id.
    /// </summary>
    void RegisterMovementMode(string id, IKzMovementMode mode);

    /// <summary>The active player's movement-callback impl, or null if their mode registered none.</summary>
    IKzMovementMode? GetMovementMode(PlayerSlot slot);

    /// <summary>The player's current mode id (defaults to the configured default, e.g. "vnl").</summary>
    string GetPlayerMode(PlayerSlot slot);

    /// <summary>Raised (slot, newModeId) when a player switches mode — a mode plugin can react to it.</summary>
    event Action<PlayerSlot, string>? PlayerModeChanged;
}
