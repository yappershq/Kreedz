using System;

namespace Kreedz.Shared.Models;

/// <summary>
/// A KZ ban: a player is excluded from ranking (and kicked on connect) while an unexpired ban exists.
/// ORM-free DTO for the request API — <see cref="SteamId"/> is a raw ulong for LiteDB parity with
/// <see cref="RunRecord"/>. The SQL backend maps this to the <c>kz_bans</c> table.
/// </summary>
public sealed class BanRecord
{
    public string   Id        { get; set; } = string.Empty; // UUID
    public ulong    SteamId   { get; set; }
    public string?  Reason    { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
