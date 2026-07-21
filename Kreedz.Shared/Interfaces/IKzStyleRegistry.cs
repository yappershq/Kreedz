using System;
using System.Collections.Generic;
using Sharp.Shared.Units;

namespace Kreedz.Shared.Interfaces;

/// <summary>
/// The KZ style registry, published by Kreedz.Core. External style plugins consume it to register a
/// stackable style's convar layer; the Core owns the per-player active stack, the style commands, and
/// re-applying mode+styles. Styles are optional modifiers — any active style makes a run unranked.
/// </summary>
public interface IKzStyleRegistry
{
    static readonly string Identity = typeof(IKzStyleRegistry).FullName!;

    /// <summary>Register a style's metadata + convar layer. Idempotent per id (last registration wins).</summary>
    void RegisterStyle(string id, string name, string shortName, IReadOnlyDictionary<string, string> convars);

    /// <summary>True if the player has the given style active.</summary>
    bool HasStyle(PlayerSlot slot, string id);

    /// <summary>True if the player has any style active (→ styled/unranked run).</summary>
    bool HasAnyStyle(PlayerSlot slot);
}
