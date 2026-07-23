/*
 * yappershq/Kreedz — Race Event gate (3rd-party module; does NOT touch Kreedz.Core).
 *
 * Registers "KZ Race — First to Finish" with EventManager as an IEventMode and drives Kreedz purely through
 * the published IKzRaceControl. When a streamer activates it: teleport the field to the map start and race —
 * the first player to finish the map wins, announced with a 1st/2nd/3rd podium. Deactivate stops the race.
 * If EventManager isn't installed, or Kreedz.Core isn't published, this stays inert — no hard dependency.
 */

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Units;
using EventManager.Shared;
using Kreedz.Shared.Interfaces;
using Kreedz.Shared.Models.Timer;

namespace Kreedz.Event;

public sealed class KreedzRaceEvent : IModSharpModule, IEventMode
{
    private const string Green   = "";
    private const string White   = "";
    private const string Silver  = "";

    private readonly ISharedSystem            _shared;
    private readonly IClientManager           _clientManager;
    private readonly ILogger<KreedzRaceEvent> _logger;

    private IKzRaceControl? _race;
    private IDisposable?    _registration;
    private bool            _running;

    public string DisplayName   => "[Kreedz] Race Event";
    public string DisplayAuthor => "yappershq";

    public KreedzRaceEvent(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version,
                           IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared        = shared;
        _clientManager = shared.GetClientManager();
        _logger        = shared.GetLoggerFactory().CreateLogger<KreedzRaceEvent>();
    }

    public bool Init() => true;

    public void OnAllModulesLoaded()
    {
        var mgr = _shared.GetSharpModuleManager();

        _race = mgr.GetOptionalSharpModuleInterface<IKzRaceControl>(IKzRaceControl.Identity)?.Instance;
        if (_race is null)
        {
            _logger.LogWarning("[Kreedz.Event] IKzRaceControl not published (Kreedz.Core missing?) — race event unavailable.");
            return;
        }

        var gate = mgr.GetOptionalSharpModuleInterface<IEventManagerShared>(IEventManagerShared.Identity)?.Instance;
        if (gate is null)
        {
            _logger.LogInformation("[Kreedz.Event] EventManager not present — race event stays inert.");
            return;
        }

        _registration = gate.RegisterEvent(this);
        _logger.LogInformation("[Kreedz.Event] registered 'kzrace' with EventManager.");
    }

    public void Shutdown()
    {
        Deactivate();
        _registration?.Dispose();
        _registration = null;
    }

    // ── IEventMode ───────────────────────────────────────────────────────────────
    public string Id => "kzrace";
    string IEventMode.DisplayName => "KZ Race — First to Finish";

    public void Activate()
    {
        if (_race is null)
            return;

        if (!_running)
        {
            _race.PlayerFinished += OnPlayerFinished;
            _running = true;
        }

        _race.StartRace(0);
        Broadcast($" {Green}[KZ Race]{White} First to finish the map wins — go!");
    }

    public void Deactivate()
    {
        if (_race is null || !_running)
            return;

        _race.PlayerFinished -= OnPlayerFinished;
        _race.StopRace();
        _running = false;
    }

    private void OnPlayerFinished(PlayerSlot slot, ITimerInfo info, int order)
    {
        if (_clientManager.GetGameClient(slot) is not { IsFakeClient: false } client)
            return;

        var time = FormatTime(info.Time);
        var text = order switch
        {
            1 => $" {Green}[KZ Race]{White} {Green}{client.Name}{White} wins — {Green}{time}{White}!",
            2 => $" {Green}[KZ Race]{White} {Silver}{client.Name}{White} takes 2nd — {time}",
            3 => $" {Green}[KZ Race]{White} {Silver}{client.Name}{White} takes 3rd — {time}",
            _ => null,
        };

        if (text is not null)
            Broadcast(text);
    }

    private void Broadcast(string text)
    {
        foreach (var client in _clientManager.GetGameClients(true))
            if (!client.IsFakeClient)
                client.Print(HudPrintChannel.Chat, text);
    }

    // h:mm:ss.mmm-ish — matches the HUD's compact race time.
    private static string FormatTime(float seconds)
    {
        var ms   = (int) MathF.Round(seconds * 1000f);
        var mins = ms / 60000;
        return $"{mins}:{ms / 1000 % 60:00}.{ms % 1000:000}";
    }
}
