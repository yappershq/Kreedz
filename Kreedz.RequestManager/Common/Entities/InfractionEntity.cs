using System;
using Sharp.Shared.Units;
using SqlSugar;

namespace Kreedz.Common.Entities;

/// <summary>
/// KZ anticheat infraction record (cs2kz infractions.cpp). One row per detector flag — the AC plugin
/// writes here so flags persist for later review. Distinct from kz_bans (a ban is an admin/AC action;
/// an infraction is a detection event).
/// </summary>
[SugarTable("kz_infractions")]
[SugarIndex("idx_kz_infractions_steamid", nameof(SteamId), OrderByType.Asc, false)]
internal sealed class InfractionEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 36)]
    public string Id { get; set; } = string.Empty; // UUID

    [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
    public SteamID SteamId { get; set; }

    [SugarColumn(Length = 64)]
    public string Type { get; set; } = string.Empty; // detector name

    [SugarColumn(Length = 255, IsNullable = true)]
    public string? Details { get; set; }

    public DateTime CreatedAt { get; set; }
}
