/*
 * !rank — cs2kz rank titles, local edition. cs2kz derives titles from GLOBAL points percentiles
 * (KZGlobalteam backend); without a key those don't exist, so the same ladder runs off the LOCAL
 * points system (GetPlayerPointsRank percentile). When the global API lands, the title source swaps —
 * the ladder and display stay.
 */

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal sealed class RankTitleModule : IModule
{
    private readonly InterfaceBridge          _bridge;
    private readonly ICommandManager          _commandManager;
    private readonly IRequestManager          _request;
    private readonly ILogger<RankTitleModule> _logger;

    public RankTitleModule(InterfaceBridge bridge, ICommandManager commandManager, IRequestManager request, ILogger<RankTitleModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _request        = request;
        _logger         = logger;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("rank", (slot, cmd) =>
        {
            if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
                _ = ShowRankAsync(slot, client.SteamId);
            return ECommandAction.Handled;
        });

        return true;
    }

    private async Task ShowRankAsync(PlayerSlot slot, SteamID steamId)
    {
        try
        {
            var (rank, total) = await _request.GetPlayerPointsRank(steamId).ConfigureAwait(false);

            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                if (_bridge.ClientManager.GetGameClient(slot) is not { IsFakeClient: false } client
                    || client.SteamId != steamId)
                    return;

                if (rank <= 0 || total <= 0)
                {
                    Loc.Chat(_bridge.LocalizerManager, client, "Kreedz_Rank_None");
                    return;
                }

                Loc.Chat(_bridge.LocalizerManager, client, "Kreedz_Rank", Title(rank, total), rank, total);
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
}
