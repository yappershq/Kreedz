/*
 * yappershq/Kreedz (KZ) — RankTitles plugin (cs2kz rank titles), standalone third-party module.
 *
 * !rank shows the player's title from cs2kz's ladder (Beginner→Legend) over the LOCAL points
 * percentile (IRequestManager.GetPlayerPointsRank). When a KZGlobalteam key/backend lands, the
 * title source swaps to global percentiles — the ladder and display stay.
 *
 * Depends only on ISharedSystem + optional Core interfaces (IKzCommands, IRequestManager) and the
 * LocalizerManager — installable/removable independently of Core (degrades: command unavailable).
 */

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.RankTitles;

public sealed class KreedzRankTitles : IModSharpModule
{
    public string DisplayName   => "[Kreedz] RankTitles";
    public string DisplayAuthor => "yappershq";

    private readonly ISharedSystem             _shared;
    private readonly IModSharp                 _modSharp;
    private readonly IClientManager            _clientManager;
    private readonly ILogger<KreedzRankTitles> _logger;

    private ILocalizerManager? _localizer;
    private IRequestManager?   _request;

    public KreedzRankTitles(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared        = shared;
        _modSharp      = shared.GetModSharp();
        _clientManager = shared.GetClientManager();
        _logger        = shared.GetLoggerFactory().CreateLogger<KreedzRankTitles>();
    }

    public bool Init() => true;

    public void OnAllModulesLoaded()
    {
        var mgr = _shared.GetSharpModuleManager();

        _localizer = mgr.GetOptionalSharpModuleInterface<ILocalizerManager>(ILocalizerManager.Identity)?.Instance;
        _localizer?.LoadLocaleFile("kreedz-ranktitles.json");

        _request = mgr.GetOptionalSharpModuleInterface<IRequestManager>(IRequestManager.Identity)?.Instance;

        var commands = mgr.GetOptionalSharpModuleInterface<IKzCommands>(IKzCommands.Identity)?.Instance;
        if (commands is null || _request is null)
        {
            _logger.LogWarning("[KZ.Rank] Core interfaces missing (commands: {C}, request: {R}) — !rank unavailable",
                commands is not null, _request is not null);
            return;
        }

        commands.AddClientChatCommand("rank", (slot, cmd) =>
        {
            if (_clientManager.GetGameClient(slot) is { IsFakeClient: false } client)
                _ = ShowRankAsync(slot, client.SteamId);
            return ECommandAction.Handled;
        });
    }

    public void Shutdown() { }

    private async Task ShowRankAsync(PlayerSlot slot, SteamID steamId)
    {
        try
        {
            var (rank, total) = await _request!.GetPlayerPointsRank(steamId).ConfigureAwait(false);

            await _modSharp.InvokeFrameActionAsync(() =>
            {
                if (_localizer is not { } lm
                    || _clientManager.GetGameClient(slot) is not { IsFakeClient: false } client
                    || client.SteamId != steamId)
                    return;

                if (rank <= 0 || total <= 0)
                {
                    lm.For(client).Localized("Kreedz_Rank_None").Prefix(null).Transform(ProcessColors).Print(HudPrintChannel.Chat);
                    return;
                }

                lm.For(client).Localized("Kreedz_Rank", Title(rank, total), rank, total)
                  .Prefix(null).Transform(ProcessColors).Print(HudPrintChannel.Chat);
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[KZ.Rank] failed to fetch points rank for {Sid}", steamId);
        }
    }

    // cs2kz's title ladder over the local points percentile (global percentiles once the API lands).
    private static string Title(int rank, int total)
    {
        var pct = (double) rank / total;
        return pct switch
        {
            <= 0.01 => "Legend",
            <= 0.05 => "Master",
            <= 0.10 => "Pro",
            <= 0.20 => "Expert",
            <= 0.35 => "Skilled",
            <= 0.55 => "Regular",
            <= 0.75 => "Casual",
            _       => "Beginner",
        };
    }

    private static string ProcessColors(string s)
        => s.Replace("{green}", "\x04").Replace("{lime}", "\x06").Replace("{gold}", "\x10").Replace("{red}", "\x02").Replace("{default}", "\x01");
}
