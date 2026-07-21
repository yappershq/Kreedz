/*
 * yappershq/Kreedz (KZ) — CS2KZ port
 *
 * KZ style system (1:1 cs2kz src/kz/style). Styles are STACKABLE convar modifiers layered on the base
 * mode (mode → styles, last-wins). This module is the style REGISTRY: it owns the per-player active
 * stack, the style commands, and re-applying mode+styles. It publishes IKzStyleRegistry so styles can
 * ship as separate plugins, and self-registers the built-ins **AutoBhop (ABH)** + **LegacyJump (LGJ)**.
 * Any active style makes a run a separate (styled/unranked) leaderboard category.
 *
 * Commands: !style [name] (toggle), !addstyle, !removestyle, !togglestyle, !clearstyles + per-style short.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal interface IKzStyleModule
{
    /// <summary>True if the player has any style active (→ styled leaderboard category, jumpstats invalid).</summary>
    bool HasAnyStyle(PlayerSlot slot);
}

internal sealed class KzStyleModule : IModule, IKzStyleModule, IKzStyleRegistry
{
    private const string PrefKey = "styles";

    private readonly InterfaceBridge        _bridge;
    private readonly ICommandManager        _commandManager;
    private readonly IModeModule            _modeModule;
    private readonly IPreferencesModule     _prefs;
    private readonly ILogger<KzStyleModule> _logger;

    private readonly record struct StyleInfo(string Name, string ShortName, IReadOnlyDictionary<string, string> Convars);

    private readonly Dictionary<string, StyleInfo> _styles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>[]             _active = new HashSet<string>[PlayerSlot.MaxPlayerCount];

    public KzStyleModule(InterfaceBridge bridge, ICommandManager commandManager, IModeModule modeModule, IPreferencesModule prefs, ILogger<KzStyleModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _modeModule     = modeModule;
        _prefs          = prefs;
        _logger         = logger;

        for (var i = 0; i < _active.Length; i++)
            _active[i] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("style",       (s, c) => { ToggleArg(s, c); return ECommandAction.Handled; });
        _commandManager.AddClientChatCommand("togglestyle", (s, c) => { ToggleArg(s, c); return ECommandAction.Handled; });
        _commandManager.AddClientChatCommand("addstyle",    (s, c) => { SetStyle(s, Arg(c), true);  return ECommandAction.Handled; });
        _commandManager.AddClientChatCommand("removestyle", (s, c) => { SetStyle(s, Arg(c), false); return ECommandAction.Handled; });
        _commandManager.AddClientChatCommand("clearstyles", (s, _) => { ClearStyles(s); return ECommandAction.Handled; });

        // Styles ship as external plugins (Kreedz.Style.*) that register via IKzStyleRegistry — none built in.

        _prefs.Loaded += OnPreferencesLoaded;
        return true;
    }

    public void OnPostInit(ServiceProvider provider)
        => _bridge.SharpModuleManager.RegisterSharpModuleInterface<IKzStyleRegistry>(
               _bridge.Entrypoint, IKzStyleRegistry.Identity, this);

    public void Shutdown() => _prefs.Loaded -= OnPreferencesLoaded;

    // ── IKzStyleRegistry ──────────────────────────────────────────────────────
    public void RegisterStyle(string id, string name, string shortName, IReadOnlyDictionary<string, string> convars)
    {
        var isNew = !_styles.ContainsKey(id);
        _styles[id] = new StyleInfo(name, shortName, convars);

        foreach (var cvar in convars.Keys)
            if (_bridge.ConVarManager.FindConVar(cvar) is { } cv)
                cv.Flags |= ConVarFlags.Replicated;

        if (isNew)
            _commandManager.AddClientChatCommand(id, (slot, _) => { SetStyle(slot, id, !_active[slot].Contains(id)); return ECommandAction.Handled; });
    }

    public bool HasStyle(PlayerSlot slot, string id) => _active[slot].Contains(id);

    public bool HasAnyStyle(PlayerSlot slot) => _active[slot].Count > 0;

    // ──────────────────────────────────────────────────────────────────────────
    private void OnPreferencesLoaded(PlayerSlot slot)
    {
        if (_prefs.Get(slot, PrefKey) is not { Length: > 0 } raw) return;

        _active[slot].Clear();
        foreach (var id in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (_styles.ContainsKey(id))
                _active[slot].Add(id);

        ReapplyAll(slot);
    }

    private void Persist(PlayerSlot slot) => _prefs.Set(slot, PrefKey, string.Join(',', _active[slot]));

    private static string? Arg(StringCommand c) => c.ArgCount >= 1 ? c.GetArg(1) : null;

    private void ToggleArg(PlayerSlot slot, StringCommand command)
    {
        if (Arg(command) is not { } id)
        {
            var active = _active[slot].Count == 0 ? "none" : string.Join(", ", _active[slot]);
            Msg(slot, "Kreedz_Style_List", active, string.Join(", ", _styles.Values.Select(s => s.ShortName)));
            return;
        }

        SetStyle(slot, id, !_active[slot].Contains(id));
    }

    private void SetStyle(PlayerSlot slot, string? id, bool enable)
    {
        if (id is null || !_styles.TryGetValue(id, out var style))
        {
            Msg(slot, "Kreedz_Style_Unknown", id);
            return;
        }

        if (enable) _active[slot].Add(id);
        else        _active[slot].Remove(id);

        Persist(slot);
        ReapplyAll(slot);
        Msg(slot, enable ? "Kreedz_Style_Enabled" : "Kreedz_Style_Disabled", style.Name);
    }

    private void ClearStyles(PlayerSlot slot)
    {
        _active[slot].Clear();
        Persist(slot);
        ReapplyAll(slot);
        Msg(slot, "Kreedz_Style_Cleared");
    }

    /// <summary>Revert to the mode base, then re-apply the active style stack on top (last-wins).</summary>
    private void ReapplyAll(PlayerSlot slot)
    {
        _modeModule.Reapply(slot);

        if (_bridge.ClientManager.GetGameClient(slot) is not { IsFakeClient: false } client) return;
        foreach (var id in _active[slot])
            if (_styles.TryGetValue(id, out var style))
                foreach (var (name, value) in style.Convars)
                    _bridge.ConVarManager.FindConVar(name)?.ReplicateToClient(client, value);
    }

    private void Msg(PlayerSlot slot, string key, params object?[] args)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            Loc.Chat(_bridge.LocalizerManager, client, key, args);
    }
}
