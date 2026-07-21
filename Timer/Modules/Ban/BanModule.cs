/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ ban management (1:1 cs2kz src/kz/ban write side). The read side already exists — ranking queries
 * exclude players with an unexpired kz_bans row, and the anticheat can write rows. This adds the admin
 * commands to create/remove bans and the connect-time enforcement that kicks a banned player.
 *
 *   !ban   <name|steamid64> <minutes|0=perm> [reason]   (@kz/ban)
 *   !unban <steamid64>                                   (@kz/ban)
 *
 * A raw SteamID64 target lets you ban an offline player; a name matches a connected one (exact then
 * substring). Bans persist via IRequestManager (SQL backend or the LiteDB fallback), so a banned player
 * is kicked on their next connect even across a restart.
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Modules;

internal interface IBanModule;

internal sealed class BanModule : IModule, IBanModule, IClientListener
{
    private const ulong MinSteamId64 = 76561197960265728UL; // individual account base; anything <= is not a real id

    private readonly InterfaceBridge   _bridge;
    private readonly IRequestManager   _request;
    private readonly ICommandManager   _commandManager;
    private readonly ILogger<BanModule> _logger;

    public BanModule(InterfaceBridge bridge, IRequestManager request, ICommandManager commandManager, ILogger<BanModule> logger)
    {
        _bridge         = bridge;
        _request        = request;
        _commandManager = commandManager;
        _logger         = logger;
    }

    public int ListenerVersion  => IClientListener.ApiVersion;
    public int ListenerPriority => 10;

    public bool Init()
    {
        ImmutableArray<string> perm = ["@kz/ban"];
        _commandManager.AddAdminChatCommand("ban", perm, OnCommandBan);
        _commandManager.AddAdminChatCommand("unban", perm, OnCommandUnban);

        _bridge.ClientManager.InstallClientListener(this);
        return true;
    }

    public void Shutdown() => _bridge.ClientManager.RemoveClientListener(this);

    // Kick a banned player the moment they finish connecting.
    public void OnClientPutInServer(IGameClient client)
    {
        if (client.IsFakeClient) return;
        _ = EnforceAsync(client.Slot, client.SteamId);
    }

    private async Task EnforceAsync(PlayerSlot slot, SteamID steamId)
    {
        try
        {
            if (await _request.GetActiveBanAsync(steamId) is not { } ban) return;

            _bridge.ModSharp.InvokeFrameAction(() =>
            {
                // Re-resolve on the game thread — never hold an IGameClient across the async gap.
                if (_bridge.ClientManager.GetGameClient(slot) is { IsValid: true } client && client.SteamId == steamId)
                    _bridge.ClientManager.KickClient(client, KickReason(ban.Reason));
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[KZ.Ban] enforce failed for {Sid}", steamId);
        }
    }

    private ECommandAction OnCommandBan(PlayerSlot slot, StringCommand command)
    {
        if (command.ArgCount < 2)
        {
            Tell(slot, "Usage: !ban <name|steamid64> <minutes|0=perm> [reason]");
            return ECommandAction.Handled;
        }

        if (command.TryGet<int?>(2) is not { } minutes || minutes < 0)
        {
            Tell(slot, "Minutes must be a non-negative integer (0 = permanent).");
            return ECommandAction.Handled;
        }

        if (ResolveTarget(command.GetArg(1), out var onlineSlot) is not { } target)
        {
            Tell(slot, $"No connected player or valid SteamID64 matching '{command.GetArg(1)}'.");
            return ECommandAction.Handled;
        }

        var reason    = BuildReason(command, 3);
        var expiresAt = minutes == 0 ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(minutes);
        _ = BanAsync(slot, target, reason, expiresAt, onlineSlot);
        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandUnban(PlayerSlot slot, StringCommand command)
    {
        if (command.ArgCount < 1 || !ulong.TryParse(command.GetArg(1), out var sid) || sid <= MinSteamId64)
        {
            Tell(slot, "Usage: !unban <steamid64>");
            return ECommandAction.Handled;
        }

        _ = UnbanAsync(slot, new SteamID(sid));
        return ECommandAction.Handled;
    }

    private async Task BanAsync(PlayerSlot adminSlot, SteamID target, string? reason, DateTime expiresAt, PlayerSlot? onlineSlot)
    {
        try
        {
            await _request.AddBanAsync(target, reason, expiresAt);

            _bridge.ModSharp.InvokeFrameAction(() =>
            {
                var when = expiresAt == DateTime.MaxValue ? "permanently" : $"until {expiresAt:u}";
                Tell(adminSlot, $"Banned {target} {when}{(reason is null ? "" : $" — {reason}")}.");

                if (onlineSlot is { } os
                    && _bridge.ClientManager.GetGameClient(os) is { IsValid: true } client
                    && client.SteamId == target)
                    _bridge.ClientManager.KickClient(client, KickReason(reason));
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[KZ.Ban] ban failed for {Target}", target);
            _bridge.ModSharp.InvokeFrameAction(() => Tell(adminSlot, "Ban failed — see server log."));
        }
    }

    private async Task UnbanAsync(PlayerSlot slot, SteamID target)
    {
        try
        {
            var removed = await _request.RemoveBansAsync(target);
            _bridge.ModSharp.InvokeFrameAction(() =>
                Tell(slot, removed > 0 ? $"Removed {removed} ban(s) for {target}." : $"No bans found for {target}."));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[KZ.Ban] unban failed for {Target}", target);
            _bridge.ModSharp.InvokeFrameAction(() => Tell(slot, "Unban failed — see server log."));
        }
    }

    /// <summary>Resolve a target to a SteamID: a raw SteamID64 (offline ban), else a connected player by
    /// exact-then-substring name. Sets <paramref name="onlineSlot"/> when the target is currently on the server.</summary>
    private SteamID? ResolveTarget(string arg, out PlayerSlot? onlineSlot)
    {
        onlineSlot = null;

        if (ulong.TryParse(arg, out var sid) && sid > MinSteamId64)
        {
            var steamId = new SteamID(sid);
            foreach (var client in _bridge.ClientManager.GetGameClients(true))
                if (!client.IsFakeClient && client.SteamId == steamId) { onlineSlot = client.Slot; break; }
            return steamId;
        }

        IGameClient? substring = null;
        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (client.IsFakeClient) continue;
            if (string.Equals(client.Name, arg, StringComparison.OrdinalIgnoreCase)) { onlineSlot = client.Slot; return client.SteamId; }
            substring ??= client.Name.Contains(arg, StringComparison.OrdinalIgnoreCase) ? client : null;
        }

        if (substring is not null) { onlineSlot = substring.Slot; return substring.SteamId; }
        return null;
    }

    private static string? BuildReason(StringCommand command, int from)
    {
        if (command.ArgCount < from) return null;

        var parts = new List<string>(command.ArgCount - from + 1);
        for (var i = from; i <= command.ArgCount; i++)
            parts.Add(command.GetArg(i));

        var reason = string.Join(' ', parts).Trim();
        return reason.Length == 0 ? null : reason;
    }

    private static string KickReason(string? reason) => reason is null ? "KZ: banned" : $"KZ: banned ({reason})";

    private void Tell(PlayerSlot slot, string message)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            client.Print(HudPrintChannel.Chat, message);
    }
}
